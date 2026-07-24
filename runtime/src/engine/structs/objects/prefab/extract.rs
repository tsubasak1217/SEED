// ============================================================
//  extract.rs — プレハブオーバーライドの差分抽出（シーン保存時）
//
//  【役割】
//  「現在のシーン上のプレハブインスタンス（ActorData）」と
//  「プレハブ本体ファイル（.actor の ActorData）」を突き合わせ、
//  シーン側で加えられた変更を PrefabOverrides として抽出する。
//
//  【なぜ保存時に比較するのか】
//  編集操作のたびにフラグを立てる方式は、IPC 編集経路のどれか 1 本に処理を
//  通し忘れるだけで差分が失われる（このプロジェクトで実証済みの失敗パターン）。
//  保存時にプレハブ本体と丸ごと比較すれば、編集経路が何本あっても漏れが出ない。
//
//  【比較の単位】
//  - コンポーネント: スロットデータ（名前・本体・enabled）を JSON 値として等値比較
//  - 子アクタ:       名前ベースの対応付けを行い、対応が無い子を「追加」とみなす
//  子アクタの Transform 変更・名前変更・削除は段階 1 の対象外（mod.rs 参照）。
//
//  【行列補正（delta）の考慮】
//  再展開時、プレハブ本体は「ルート位置＝原点」基準で構築され、シーン側ルート
//  Transform との差分 delta = M_scene * M_file^-1 がサブツリー全体
//  （ModelComponent の instance_mats・子 Transform）へ適用される（prefab_ops.rs）。
//  そのためシーン上の instance_mats は「プレハブ本体の値 × delta」になっており、
//  素朴に比較すると **移動しただけのインスタンスで ModelComponent が常に差分扱い**に
//  なってしまう（＝プレハブ本体のモデル変更が伝播しなくなる）。
//  これを避けるため、比較の際はプレハブ本体側へ同じ delta を適用した
//  「再展開後に期待される値」を作ってから突き合わせる。
// ============================================================

use std::collections::HashMap;

use crate::engine::components::{ComponentData, Transform};
use crate::engine::methods::gizmo_interact::{mat4x4_inv, mat4x4_mul};
use crate::engine::structs::objects::actor::{ActorData, ActorKind, ComponentSlotData};

use super::overrides::{
    ChildOverride, ComponentKey, ComponentOverride, NodeStep, PrefabOverrides,
    SINGULAR_SCALE_EPS,
};

/// 4x4 行列（列優先／行優先の解釈はエンジン共通の gizmo_interact に準じる）。
type Mat4 = [[f32; 4]; 4];

// ============================================================
//  公開 API
// ============================================================

/// 再展開時にサブツリーへ適用される行列補正 delta を求める。
///
/// `prefab_ops::reinstantiate_single` の補正条件を **そのまま再現**したもの。
/// 補正が行われない場合（2D／Transform 無し／同値／プレハブ側スケールが特異）は None。
///
/// # 引数
/// - `scene_root`:  シーン上のプレハブインスタンスルートのデータ
/// - `prefab_root`: プレハブ本体ファイルのルートデータ
pub fn compute_reinstantiate_delta(scene_root: &ActorData, prefab_root: &ActorData) -> Option<Mat4> {
    compute_delta_for_root(
        scene_root.transform.as_ref(),
        scene_root.actor_kind == ActorKind::Actor2D,
        prefab_root,
    )
}

/// 行列補正 delta の計算本体（再展開側からも同じ判定で呼べる形）。
///
/// # 引数
/// - `keep_tf`: シーン側で維持されるルート Transform（無ければ補正なし）
/// - `is_2d`:   インスタンスが 2D アクターか（2D は補正対象外）
/// - `prefab_root`: プレハブ本体ファイルのルートデータ
pub fn compute_delta_for_root(
    keep_tf:     Option<&Transform>,
    is_2d:       bool,
    prefab_root: &ActorData,
) -> Option<Mat4> {
    // 2D アクター（CanvasTransform 階層）は毎フレーム再計算されるため補正されない。
    if is_2d { return None; }

    // シーン側ルート Transform（保持される値）。持たない場合は補正なし。
    let keep = keep_tf?;
    // プレハブ本体側ルート Transform（build_actor は未指定時 default を挿入する）。
    let file_tf = prefab_root.transform.clone().unwrap_or_default();

    // 同値なら delta は単位行列。誤差蓄積を避けるため適用しない（再展開側と同じ判定）。
    if *keep == file_tf { return None; }
    // プレハブ側スケールが 0 なら逆行列が特異になるため補正しない（再展開側と同じガード）。
    if file_tf.scale.iter().any(|&s| s.abs() < SINGULAR_SCALE_EPS) { return None; }

    Some(mat4x4_mul(keep.to_mat4(), mat4x4_inv(file_tf.to_mat4())))
}

/// プレハブインスタンスの差分（オーバーライド）を抽出する。
///
/// # 引数
/// - `scene_root`:  現在のシーン上のインスタンス（ActorData 化済み）
/// - `prefab_root`: プレハブ本体ファイルの内容
///
/// # 戻り値
/// 抽出された差分。差分が無ければ空の `PrefabOverrides`。
///
/// # 対象外
/// ルートの name / active / Transform / prefab_source は再展開時に維持されるため
/// 比較しない。子アクタの Transform 変更・名前変更・削除も段階 1 では扱わない。
pub fn extract_prefab_overrides(scene_root: &ActorData, prefab_root: &ActorData) -> PrefabOverrides {
    let delta = compute_reinstantiate_delta(scene_root, prefab_root);
    let mut out = PrefabOverrides::default();
    collect_node_diff(scene_root, prefab_root, &[], delta, &mut out);
    out
}

/// アクタデータのツリー群を走査し、プレハブインスタンスの `prefab_overrides` を
/// 最新の差分で更新する（シーン保存の直前に呼ぶ）。
///
/// # 走査ルール
/// `prefab_ops::reinstantiate_prefabs_in_actors` と同じ探索規則に揃える:
///  - `prefab_source` を持つノードは差分を抽出し、**その子へは再帰しない**
///    （ネストプレハブは 1 段のみという再展開側の制約に合わせるため）
///  - それ以外のノードは子へ再帰する（通常アクタ配下のネストインスタンスに対応）
///
/// # 参照先ファイルが読めない場合
/// 再展開もスキップされる（＝シーン保存値がそのまま生き続ける）ため、
/// 既存の `prefab_overrides` を **消さずに維持**する。
pub fn refresh_prefab_overrides(actors: &mut [ActorData]) {
    // 同じ `.actor` を参照するインスタンスが複数あってもファイル読み込みは 1 回で済ませる。
    // 値が None のエントリは「読めなかった」ことの記憶（再試行しない）。
    let mut cache: HashMap<String, Option<ActorData>> = HashMap::new();
    for a in actors.iter_mut() {
        refresh_node(a, &mut cache);
    }
}

/// `refresh_prefab_overrides` の再帰本体（プレハブ本体データのキャッシュ付き）。
fn refresh_node(data: &mut ActorData, cache: &mut HashMap<String, Option<ActorData>>) {
    if let Some(src) = data.prefab_source.clone() {
        let prefab_root = cache.entry(src.clone())
            .or_insert_with(|| load_prefab_root(&src))
            .clone();
        match prefab_root {
            Some(root) => {
                data.prefab_overrides = extract_prefab_overrides(data, &root);
            }
            None => {
                // 参照先が読めない: 再展開されないので既存の差分をそのまま維持する。
                eprintln!("[Prefab] オーバーライド抽出をスキップ（参照先を読めません）: {src}");
            }
        }
        // プレハブノードの子へは再帰しない（再展開側と同じ 1 段のみの規則）。
        return;
    }
    for child in data.children.iter_mut() {
        refresh_node(child, cache);
    }
}

/// プレハブ本体ファイル（`assets://` 仮想パスまたは絶対パス）を ActorData として読む。
/// 読めない／パースできない場合は None。
fn load_prefab_root(src: &str) -> Option<ActorData> {
    let raw  = crate::engine::asset_fs::read_string(src).ok()?;
    let json = raw.strip_prefix('\u{FEFF}').unwrap_or(&raw);
    serde_json::from_str::<ActorData>(json).ok()
}

// ============================================================
//  内部実装
// ============================================================

/// 1 ノード分の差分を集め、対応の取れた子へ再帰する。
///
/// # 引数
/// - `path`: インスタンスルートからこのノードまでの相対パス（ルートは空）
/// - `delta`: 再展開時にサブツリーへ適用される行列補正（無ければ None）
fn collect_node_diff(
    scene:  &ActorData,
    prefab: &ActorData,
    path:   &[NodeStep],
    delta:  Option<Mat4>,
    out:    &mut PrefabOverrides,
) {
    // ── コンポーネントの差分 ──────────────────────────────────
    // プレハブ側のキー → スロット参照の索引を作る（同一キーは出現順で区別される）。
    let prefab_slots: HashMap<ComponentKey, &ComponentSlotData> =
        keyed_slots(&prefab.components).into_iter().collect();

    for (key, scene_slot) in keyed_slots(&scene.components) {
        match prefab_slots.get(&key) {
            // プレハブにも存在する: 値が違えば「上書き」として記録する
            Some(prefab_slot) => {
                // 再展開後に期待される値（プレハブ本体 ＋ 行列補正）を作って比較する
                let expected = expected_after_reinstantiate(prefab_slot, delta);
                if !slots_equal(scene_slot, &expected) {
                    out.modified_components.push(ComponentOverride {
                        path: path.to_vec(),
                        key,
                        slot: scene_slot.clone(),
                    });
                }
            }
            // プレハブに存在しない: 「追加」として記録する
            None => {
                out.added_components.push(ComponentOverride {
                    path: path.to_vec(),
                    key,
                    slot: scene_slot.clone(),
                });
            }
        }
    }

    // ── 子アクタの差分 ────────────────────────────────────────
    // プレハブ側の子を「名前 → 出現順のインデックス列」で索引する。
    let mut prefab_child_slots: HashMap<&str, Vec<usize>> = HashMap::new();
    for (i, c) in prefab.children.iter().enumerate() {
        prefab_child_slots.entry(c.name.as_str()).or_default().push(i);
    }
    // 名前ごとに「次に何番目の同名子と対応付けるか」のカーソル。
    let mut cursor: HashMap<&str, usize> = HashMap::new();

    for (scene_idx, scene_child) in scene.children.iter().enumerate() {
        let name = scene_child.name.as_str();
        let nth  = cursor.entry(name).or_insert(0);
        let matched = prefab_child_slots.get(name).and_then(|v| v.get(*nth).copied());

        match matched {
            // 対応するプレハブ子がある: 同じ名前の次の子へカーソルを進めて再帰する
            Some(prefab_idx) => {
                *nth += 1;
                let mut child_path = path.to_vec();
                child_path.push(NodeStep { index: prefab_idx as u32, name: name.to_string() });
                collect_node_diff(scene_child, &prefab.children[prefab_idx], &child_path, delta, out);
            }
            // 対応が無い: シーン側で追加された子としてサブツリー丸ごと記録する
            None => {
                out.added_children.push(ChildOverride {
                    parent_path: path.to_vec(),
                    index:       scene_idx as u32,
                    actor:       scene_child.clone(),
                });
            }
        }
    }
}

/// スロット配列に `ComponentKey`（型タグ・スロット名・同キー内の出現順）を割り当てる。
fn keyed_slots(slots: &[ComponentSlotData]) -> Vec<(ComponentKey, &ComponentSlotData)> {
    // (型タグ, スロット名) ごとの出現回数カウンタ
    let mut counter: HashMap<(&str, &str), u32> = HashMap::new();
    slots.iter().map(|s| {
        let tag = s.component.type_tag();
        let n   = counter.entry((tag, s.name.as_str())).or_insert(0);
        let key = ComponentKey::new(tag, s.name.clone(), *n);
        *n += 1;
        (key, s)
    }).collect()
}

/// プレハブ本体のスロットに対し「再展開後に期待される値」を作る。
///
/// ModelComponent の instance_mats は再展開時に delta が左乗算されるため、
/// 比較の前に同じ変換を適用しておく（そうしないと移動しただけのインスタンスが
/// 常に差分扱いになり、プレハブ本体のモデル変更が伝播しなくなる）。
/// それ以外のコンポーネントは変換の対象外なのでそのまま返す。
fn expected_after_reinstantiate(prefab_slot: &ComponentSlotData, delta: Option<Mat4>) -> ComponentSlotData {
    let Some(d) = delta else { return prefab_slot.clone(); };
    let mut expected = prefab_slot.clone();
    if let ComponentData::ModelComponent(ref mut mc) = expected.component {
        for m in mc.instances.iter_mut() {
            *m = mat4x4_mul(d, *m);
        }
    }
    expected
}

/// スロット 2 つが等価か（JSON 表現での等値比較）。
///
/// コンポーネントデータ型は PartialEq を実装していないものが多く、
/// 保存フォーマットそのもので比べるのが「保存して壊れないか」という目的に一致する。
/// シリアライズに失敗した場合は「差分あり」として安全側に倒す。
fn slots_equal(a: &ComponentSlotData, b: &ComponentSlotData) -> bool {
    match (serde_json::to_value(a), serde_json::to_value(b)) {
        (Ok(va), Ok(vb)) => va == vb,
        _ => false,
    }
}
