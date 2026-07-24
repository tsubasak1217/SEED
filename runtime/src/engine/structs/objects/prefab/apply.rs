// ============================================================
//  apply.rs — プレハブオーバーライドの再適用（シーンロード時）
//
//  【役割】
//  プレハブ本体（.actor）から読み込んだ ActorData に対して、
//  `.scene` に保存されていた差分（PrefabOverrides）をマージする。
//  これにより「プレハブ本体で変わった部分は反映され、ユーザーが変更した部分は
//  シーンの値が勝つ」という Unity 相当の挙動になる。
//
//  【なぜ ActorData の段階でマージするのか】
//  再展開は「プレハブ本体データ → build_actor → ECS」の一方向の流れである。
//  ECS へ載せた後にコンポーネントを差し替えると、種別ごとの構築処理を二重に
//  持つことになり、片方だけ更新し忘れる形の不整合を生む。
//  データの段階でマージすれば構築経路は build_actor 一本のままで済み、
//  GPU/World に依存しないためユニットテストも書ける。
//
//  【行列補正（delta）との順序】
//  オーバーライドに保存されている値は「シーン保存時のワールド空間の値そのもの」。
//  一方プレハブ本体は「ルート位置＝原点」基準なので、再展開時に
//  delta = M_scene_root * M_file_root^-1 をサブツリーへ適用してから
//  オーバーライドをマージする（順序を逆にすると二重変換になる）。
//  → apply_delta_to_subtree() を呼んでから merge_overrides_into() を呼ぶこと。
// ============================================================

use crate::engine::components::{ComponentData, Transform};
use crate::engine::methods::gizmo_interact::mat4x4_mul;
use crate::engine::structs::objects::actor::{ActorData, ComponentSlotData};

use super::overrides::{ComponentKey, NodeStep, PrefabOverrides};

/// 4x4 行列。
type Mat4 = [[f32; 4]; 4];

// ============================================================
//  行列補正（delta）のデータ適用
// ============================================================

/// プレハブ本体データのサブツリー全体へ行列補正 delta を適用する。
///
/// 補正対象は「ワールド空間で保持される値」だけ:
///  - すべてのノードの ModelComponent の instance_mats
///  - **子孫**ノードの Transform
///
/// ルート自身の Transform は補正しない（呼び出し側がシーン保存値で上書きするため）。
pub fn apply_delta_to_subtree(root: &mut ActorData, delta: Mat4) {
    // ルートはコンポーネント（インスタンス行列）のみ補正する
    apply_delta_to_components(&mut root.components, delta);
    for child in root.children.iter_mut() {
        apply_delta_to_descendant(child, delta);
    }
}

/// 子孫ノード 1 つに delta を適用して再帰する。
fn apply_delta_to_descendant(node: &mut ActorData, delta: Mat4) {
    // 子 Transform はワールド空間で保持されるため左から delta を掛ける。
    // Transform を持たないノード（フォルダ・2D）は対象外。
    if let Some(tf) = node.transform.as_mut() {
        *tf = Transform::from_mat4(&mat4x4_mul(delta, tf.to_mat4()));
    }
    apply_delta_to_components(&mut node.components, delta);
    for child in node.children.iter_mut() {
        apply_delta_to_descendant(child, delta);
    }
}

/// スロット配列内の ModelComponent のインスタンス行列へ delta を左乗算する。
fn apply_delta_to_components(components: &mut [ComponentSlotData], delta: Mat4) {
    for slot in components.iter_mut() {
        if let ComponentData::ModelComponent(ref mut mc) = slot.component {
            for m in mc.instances.iter_mut() {
                *m = mat4x4_mul(delta, *m);
            }
        }
    }
}

// ============================================================
//  オーバーライドのマージ
// ============================================================

/// プレハブ本体データ `root` へ差分 `ov` をマージする。
///
/// # 適用順序
/// 1. コンポーネントの上書き
/// 2. コンポーネントの追加
/// 3. 子アクタの追加
/// 子を先に挿入すると children のインデックスがずれ、コンポーネント側の
/// ノードパス解決が狂うため、必ずコンポーネントを先に処理する。
///
/// # 失敗時の扱い
/// 個々の差分の適用先が見つからない（プレハブ本体の構造が変わった等）場合は、
/// 警告ログを出してその差分だけ諦め、残りの差分は適用する（部分復元 > 全滅）。
pub fn merge_overrides_into(root: &mut ActorData, ov: &PrefabOverrides) {
    if ov.is_empty() { return; }

    // ── 1. 値の上書き ────────────────────────────────────────
    for o in &ov.modified_components {
        let Some(node) = resolve_node_mut(root, &o.path) else {
            eprintln!("[Prefab] オーバーライド適用先ノードが見つかりません（上書きをスキップ）: {}", path_text(&o.path));
            continue;
        };
        put_slot(node, &o.key, o.slot.clone());
    }

    // ── 2. 追加されたコンポーネント ───────────────────────────
    for o in &ov.added_components {
        let Some(node) = resolve_node_mut(root, &o.path) else {
            eprintln!("[Prefab] オーバーライド適用先ノードが見つかりません（追加をスキップ）: {}", path_text(&o.path));
            continue;
        };
        // プレハブ本体側にも同じキーのスロットが後から追加された場合は
        // 二重追加を避けて上書きする（put_slot が同一処理で両方を扱う）。
        put_slot(node, &o.key, o.slot.clone());
    }

    // ── 3. 追加された子アクタ ────────────────────────────────
    // 挿入位置が小さいものから順に処理する（後続の挿入位置が意図通りになるため）。
    let mut added: Vec<_> = ov.added_children.iter().collect();
    added.sort_by_key(|c| c.index);
    for c in added {
        let Some(parent) = resolve_node_mut(root, &c.parent_path) else {
            eprintln!("[Prefab] 追加子アクタの親ノードが見つかりません（スキップ）: {}", path_text(&c.parent_path));
            continue;
        };
        let at = (c.index as usize).min(parent.children.len());
        parent.children.insert(at, c.actor.clone());
    }
}

/// キーに一致するスロットがあれば置換し、無ければ末尾へ追加する。
fn put_slot(node: &mut ActorData, key: &ComponentKey, slot: ComponentSlotData) {
    match find_slot_index(&node.components, key) {
        Some(idx) => node.components[idx] = slot,
        None      => node.components.push(slot),
    }
}

// ============================================================
//  パス／キーの解決
// ============================================================

/// ノードパスをたどって対象ノードへの可変参照を得る。
/// パスが空ならインスタンスルート自身。1 段でも解決できなければ None。
fn resolve_node_mut<'a>(node: &'a mut ActorData, path: &[NodeStep]) -> Option<&'a mut ActorData> {
    match path.split_first() {
        None => Some(node),
        Some((step, rest)) => {
            let idx = resolve_step(&node.children, step)?;
            resolve_node_mut(&mut node.children[idx], rest)
        }
    }
}

/// パス 1 段分を子配列の中で解決する。
///
/// プレハブ本体の子順序が変わっても追従できるよう、
/// 「保存時のインデックス位置に同名の子がいればそれ」→「無ければ名前で検索」
/// の順に解決する。
fn resolve_step(children: &[ActorData], step: &NodeStep) -> Option<usize> {
    let i = step.index as usize;
    if let Some(c) = children.get(i) {
        if c.name == step.name { return Some(i); }
    }
    children.iter().position(|c| c.name == step.name)
}

/// コンポーネントキーに一致するスロットの位置を探す。
fn find_slot_index(components: &[ComponentSlotData], key: &ComponentKey) -> Option<usize> {
    // 同じ (型タグ, スロット名) の中での出現順を数えながら探す
    let mut ordinal = 0u32;
    for (i, s) in components.iter().enumerate() {
        if s.component.type_tag() == key.type_tag && s.name == key.name {
            if ordinal == key.ordinal { return Some(i); }
            ordinal += 1;
        }
    }
    None
}

/// ノードパスをログ用の文字列へ整形する（例: `Arm/Hand`）。空パスは `<root>`。
fn path_text(path: &[NodeStep]) -> String {
    if path.is_empty() { return "<root>".to_string(); }
    path.iter().map(|s| s.name.as_str()).collect::<Vec<_>>().join("/")
}
