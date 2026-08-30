// ============================================================
//  sprite_bone_ops.rs — スキンスプライトの「ボーン対応表」まわりの操作（Phase A2）
//
//  【このファイルの責務】
//  SkinnedSpriteComponent のボーン（= 2D 子アクター）を、エディタから
//  オーサリングするための操作を集める。単一責任のため、
//  「値 1 個の書き換え」（mesh_path / color / layer）は skinned_sprite_ops.rs、
//  「ボーン構造・対応表」はこのファイル、と分担する。
//
//  提供する操作:
//    1. CPU 側 `.sprite_mesh` キャッシュ（ボーン一覧を引くのに GPU は要らない）
//    2. ボーン対応表 JSON の構築（インスペクタ送信用）
//       - メッシュのボーン名、解決先アクターの相対パス、明示指定かどうか
//       - ドロップ先候補（スプライトルート配下の 2D アクター一覧）
//    3. `bone_overrides` の JSON 一括設定
//    4. メッシュのボーン宣言から 2D 子アクター階層を一括生成
//
//  【なぜ CPU キャッシュを別に持つのか】
//  描画側（renderer::SpriteSkinCache）のメッシュキャッシュは wgpu::Device を
//  要求するため、IPC ハンドラ（デバイスを持たない文脈）からは引けない。
//  ボーン一覧・バインドポーズは純粋な CPU データなので、ここで別途持つ。
// ============================================================

use std::collections::{BTreeMap, HashSet};
use std::sync::Arc;

use crate::engine::components::{
    CanvasTransform, ComponentKind, SkinnedSpriteComponent,
};
use crate::engine::core::app_base::undo::ActorTreeSnapshotCommand;
use crate::engine::core::loader::sprite_mesh::SpriteMesh;
use crate::engine::core::renderer::sprite_skin::resolve_bone;
use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;

use super::{App, SpriteMeshCpuCache, find_actor_by_dfs, find_actor_by_dfs_mut};

/// `.sprite_mesh` をキャッシュ経由で読み込む（キャッシュ操作の唯一の実装）。
///
/// 読み込み失敗は `None` を記録して再試行しない（毎フレーム呼ばれるボーン可視化
/// 経路から使うため、失敗のたびにファイル I/O を繰り返させない）。
pub(crate) fn load_sprite_mesh_cached(
    cache: &SpriteMeshCpuCache,
    path: &str,
) -> Option<Arc<SpriteMesh>> {
    if path.is_empty() {
        return None;
    }
    if let Some(cached) = cache.borrow().get(path) {
        return cached.clone();
    }
    let loaded = crate::engine::asset_fs::read_string(path)
        .ok()
        .and_then(|src| SpriteMesh::from_json(&src).ok())
        .map(Arc::new);
    cache.borrow_mut().insert(path.to_string(), loaded.clone());
    loaded
}

/// ボーン対応表のドロップ候補として送るアクターの最大数。
/// 巨大シーンでインスペクタ送信 JSON が肥大化するのを防ぐ安全弁
/// （超過分は候補一覧に出ないだけで、自動解決・明示パスの動作には影響しない）。
const MAX_BONE_CANDIDATES: usize = 512;

impl App {
    // ── ① CPU メッシュキャッシュ ─────────────────────────────

    /// `.sprite_mesh` を CPU 側キャッシュ経由で取得する。
    ///
    /// 読み込み失敗は `None` を記録して再試行しない（毎フレーム呼ばれる
    /// ボーン表示経路から使うため、失敗の再試行コストを持たせない）。
    /// `&self` で呼べるよう内部可変（RefCell）で持つ。
    pub(crate) fn sprite_mesh_cpu(&self, path: &str) -> Option<Arc<SpriteMesh>> {
        load_sprite_mesh_cached(&self.sprite_mesh_cpu_cache, path)
    }

    /// CPU メッシュキャッシュの共有ハンドルを返す。
    ///
    /// 描画フレーム中は `&mut self` が握られていて `self` を再借用できないため、
    /// 借用が始まる前にこのハンドルだけ取り出してボーン可視化へ渡す。
    pub(crate) fn sprite_mesh_cpu_handle(&self) -> SpriteMeshCpuCache {
        self.sprite_mesh_cpu_cache.clone()
    }

    /// CPU メッシュキャッシュを破棄する（シーン切替・メッシュ差し替え時）。
    pub(crate) fn clear_sprite_mesh_cpu_cache(&self) {
        self.sprite_mesh_cpu_cache.borrow_mut().clear();
    }

    // ── ② インスペクタ送信 JSON（ボーン対応表）───────────────

    /// SkinnedSpriteComponent 1 スロットぶんのボーン対応表 JSON 断片を作る。
    ///
    /// 返す文字列は先頭にカンマを含む JSON オブジェクトのフィールド列で、
    /// `send_actor_components` の組み立てへそのまま連結できる:
    ///
    /// ```text
    /// ,"bones":[{"name":..,"path":..,"resolved":0|1,"override":0|1}],
    ///  "bone_candidates":[{"path":..,"dfs":n}],
    ///  "bone_unresolved":n
    /// ```
    ///
    /// - `path`: 実際に変形へ使われるアクターの**スプライトルート基準の相対パス**。
    ///   未解決なら空文字列（`resolved` が 0 になる）。
    /// - `override`: `bone_overrides` の明示エントリで解決したなら 1。
    /// - `bone_candidates`: スプライトルート配下で `CanvasTransform` を持つ
    ///   アクター（= ボーンになれるアクター）の相対パスと DFS ID。
    ///   ヒエラルキーからのドラッグ（DFS ID が来る）を IPC 往復なしで
    ///   相対パスへ変換するために DFS ID を添える。
    ///
    /// メッシュが未設定／読み込み失敗のときは空文字列を返す（表そのものを出さない）。
    pub(super) fn skinned_sprite_bone_json(
        &self,
        comp: &SkinnedSpriteComponent,
        root: &Actor,
        root_dfs_id: u32,
        world: &World,
    ) -> String {
        let Some(mesh) = self.sprite_mesh_cpu(&comp.mesh_path) else {
            return String::new();
        };

        // ボーン行（メッシュの宣言順 = GPU パレットの並び順）
        let mut unresolved = 0usize;
        let mut bones = String::from(r#","bones":["#);
        for (i, bone) in mesh.bones.iter().enumerate() {
            let r = resolve_bone(comp, root, world, &bone.name);
            if r.path.is_none() {
                unresolved += 1;
            }
            if i > 0 {
                bones.push(',');
            }
            let name_json = serde_json::to_string(&bone.name).unwrap_or_default();
            let path_json =
                serde_json::to_string(r.path.as_deref().unwrap_or("")).unwrap_or_default();
            let parent_json = serde_json::to_string(
                bone.parent
                    .and_then(|pi| mesh.bones.get(pi))
                    .map(|p| p.name.as_str())
                    .unwrap_or(""),
            )
            .unwrap_or_default();
            bones.push_str(&format!(
                r#"{{"name":{name_json},"parent":{parent_json},"path":{path_json},"resolved":{},"override":{}}}"#,
                u8::from(r.path.is_some()),
                u8::from(r.is_override),
            ));
        }
        bones.push(']');

        // ドロップ候補（スプライトルート配下の 2D アクター）
        let mut cands: Vec<(String, u32)> = Vec::new();
        collect_bone_candidates(root, world, root_dfs_id, "", &mut cands);
        cands.truncate(MAX_BONE_CANDIDATES);
        let mut cand_json = String::from(r#","bone_candidates":["#);
        for (i, (path, dfs)) in cands.iter().enumerate() {
            if i > 0 {
                cand_json.push(',');
            }
            let p = serde_json::to_string(path).unwrap_or_default();
            cand_json.push_str(&format!(r#"{{"path":{p},"dfs":{dfs}}}"#));
        }
        cand_json.push(']');

        format!(r#"{bones}{cand_json},"bone_unresolved":{unresolved}"#)
    }

    // ── ③ bone_overrides の一括設定 ──────────────────────────

    /// `bone_overrides` を JSON オブジェクト（ボーン名 → 相対パス）で置き換える。
    ///
    /// 値が空文字列のエントリは「自動解決へ戻す」意味なので保存しない
    /// （`SkinnedSpriteComponent::bone_path` が空を未設定として扱うのと同じ規約を、
    /// 保存段階で正規化しておくことで `.scene` にゴミが残らない）。
    ///
    /// JSON が壊れている・対象スロットが違う種別のときは何もしない。
    pub(super) fn handle_set_skinned_sprite_bone_overrides(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        json: &str,
    ) {
        // 先に JSON を検証する（壊れた入力でシーンへ触らない）
        let Some(normalized) = parse_bone_overrides_json(json) else {
            return;
        };

        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::SkinnedSprite)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        let Some(ss) = scene.world.get_mut::<SkinnedSpriteComponent>(entity) else {
            return;
        };
        ss.bone_overrides = normalized;

        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
    }

    // ── ④ ボーンアクターの一括生成 ──────────────────────────

    /// メッシュのボーン宣言から、スプライトルート配下へ同名の 2D 子アクター階層を作る。
    ///
    /// - 階層はメッシュの `parent` 関係をそのまま再現する（ルートボーンは
    ///   スプライトルート直下）。
    /// - 各アクターの `CanvasTransform` にはバインドポーズのローカル TRS を入れる。
    ///   したがって生成直後は**メッシュが完全に無変形で表示される**（＝
    ///   `current_relative × inverse_bind = 単位行列` になる）。これがオーサリングの
    ///   出発点として正しい状態である。
    /// - **既に同名の子アクターがある位置はスキップ**する（作り直しで既存の
    ///   ポーズ・アニメーション参照を壊さない）。
    /// - 操作全体が 1 件の Undo（`ActorTreeSnapshotCommand`）になる。
    ///
    /// 生成した本数を `SPRITE_BONES_CREATED:{n}` でエディタへ返す。
    pub(super) fn handle_create_sprite_bone_actors(&mut self, actor_dfs_id: u32, slot_idx: u32) {
        let wl = self.active_world_line;

        // ── メッシュを引く（対象スロットの種別確認も兼ねる）──
        let mesh_path = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            let Some(actor) = find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c) else {
                return;
            };
            let Some(slot) = actor.slots().get(slot_idx as usize) else {
                return;
            };
            if slot.kind != ComponentKind::SkinnedSprite {
                return;
            }
            let Some(ss) = scene.world.get::<SkinnedSpriteComponent>(slot.entity) else {
                return;
            };
            ss.mesh_path.clone()
        };
        let Some(mesh) = self.sprite_mesh_cpu(&mesh_path) else {
            if let Some(ipc) = &self.ipc {
                ipc.send("LOAD_ERROR:メッシュ（.sprite_mesh）を読み込めないためボーンを生成できません");
            }
            return;
        };

        let before_actors = self.snapshot_actors_for_wl(wl);

        // ── 生成本体（純粋関数へ委譲。テストは create_bone_actors 単体で行う）──
        let created = {
            let Some(scene) = &mut self.scene else { return };
            let mut c = 0u32;
            let Some(root) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c)
            else {
                return;
            };
            // 借用の都合で world を先に取り出せないため、アクターツリーだけ一時的に外へ出す。
            let mut detached = std::mem::take(root.children_mut());
            let n = create_bone_actors(&mut detached, &mut scene.world, &mesh, wl);
            let mut c2 = 0u32;
            if let Some(root2) =
                find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c2)
            {
                *root2.children_mut() = detached;
            }
            n
        };

        if created == 0 {
            // 何も増えていない = 既に全ボーンが揃っている。Undo に空コマンドを積まない。
            if let Some(ipc) = &self.ipc {
                ipc.send("SPRITE_BONES_CREATED:0");
            }
            return;
        }

        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));

        self.send_hierarchy();
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("SPRITE_BONES_CREATED:{created}"));
            ipc.send("SCENE_MODIFIED");
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
    }
}

// ============================================================
//  自由関数ヘルパー
// ============================================================

/// ボーン対応表 JSON（`{"ボーン名": "相対パス", ...}`）を検証して正規化する。
///
/// 値が空文字列のエントリは「自動解決へ戻す」意味なので**落とす**
/// （`SkinnedSpriteComponent::bone_path` が空を未設定として扱うのと同じ規約を
/// 保存段階で適用し、`.scene` にゴミを残さない）。
///
/// JSON が壊れている・オブジェクトでない場合は `None`（＝ 呼び出し側は何もしない）。
fn parse_bone_overrides_json(json: &str) -> Option<BTreeMap<String, String>> {
    let map: BTreeMap<String, String> = serde_json::from_str(json).ok()?;
    Some(
        map.into_iter()
            .filter(|(k, v)| !k.is_empty() && !v.is_empty())
            .collect(),
    )
}

/// メッシュのボーン宣言から 2D 子アクター階層を `children` 直下へ構築する。
///
/// `children` は「スプライトルートの子リスト」。ルートボーンはここへ直接足され、
/// 子ボーンは親ボーンのアクターの子として足される。
///
/// - **既に同名のアクターがある位置はスキップ**する（作り直しで既存のポーズや
///   アニメーション参照を壊さないため）。スキップしたボーンも「その位置に居る」
///   ものとして扱い、その子ボーンは既存アクターの下へ足される。
/// - 生成した `CanvasTransform` はバインドポーズのローカル TRS そのもの。
///   したがって生成直後は `current_relative × inverse_bind = 単位行列` となり、
///   メッシュは無変形（= 素材のとおり）で表示される。
///
/// 戻り値: 実際に新規生成したアクター数。
fn create_bone_actors(
    children: &mut Vec<Actor>,
    world: &mut World,
    mesh: &SpriteMesh,
    world_line: u32,
) -> usize {
    let mut created = 0usize;
    // ボーン添字 → そのボーンのアクターへの「children からの相対パス」
    let mut path_of: Vec<Option<String>> = vec![None; mesh.bones.len()];

    for bi in topological_bone_order(mesh) {
        let bone = &mesh.bones[bi];
        // 親のパス（ルートボーンは空 = children 直下）
        let parent_path = match bone.parent {
            Some(pi) => match &path_of[pi] {
                Some(p) => p.clone(),
                // 親を置けなかった（＝ 名前衝突以外の異常）なら子も置き場所が無い
                None => continue,
            },
            None => String::new(),
        };

        // 親ノードの子リストを引く（空パスなら children 自身）
        let Some(siblings) = descend_children_mut(children, &parent_path) else {
            continue;
        };
        if !siblings.iter().any(|c| c.name == bone.name) {
            // バインドポーズのローカル TRS を CanvasTransform へ写す。
            // `.sprite_mesh` のボーン宣言とキャンバス座標系は同一空間
            //（+X 右 / +Y 下・ピクセル）なので無変換でよい。
            let entity = world.spawn();
            world.insert(
                entity,
                CanvasTransform {
                    position: bone.bind_position,
                    rotation: bone.bind_rotation,
                    scale: bone.bind_scale,
                    ..CanvasTransform::default()
                },
            );
            let mut a = Actor::new_2d(entity, &bone.name);
            a.world_line = world_line;
            siblings.push(a);
            created += 1;
        }

        path_of[bi] = Some(if parent_path.is_empty() {
            bone.name.clone()
        } else {
            format!("{parent_path}/{}", bone.name)
        });
    }
    created
}

/// `/` 区切りの相対パスで子リストを辿り、その位置の**子リスト**を返す。
/// 空パスは `children` 自身。
fn descend_children_mut<'a>(
    children: &'a mut Vec<Actor>,
    path: &str,
) -> Option<&'a mut Vec<Actor>> {
    let mut cur = children;
    for seg in path.split('/') {
        if seg.is_empty() {
            continue;
        }
        let node = cur.iter_mut().find(|c| c.name == seg)?;
        cur = node.children_mut();
    }
    Some(cur)
}

/// ボーンを「親が必ず先」になる順序へ並べ替える。
///
/// `.sprite_mesh` の検証は循環を禁止しているだけで、宣言順が親優先である
/// 保証はない。生成時に親のパスが必要なので、ここで安全な順序を作る。
fn topological_bone_order(mesh: &SpriteMesh) -> Vec<usize> {
    let mut out = Vec::with_capacity(mesh.bones.len());
    let mut done: HashSet<usize> = HashSet::new();
    // 循環は読み込み時に排除済みなので、単純な「解決できるものから積む」で必ず全件入る
    while out.len() < mesh.bones.len() {
        let before = out.len();
        for (i, b) in mesh.bones.iter().enumerate() {
            if done.contains(&i) {
                continue;
            }
            let parent_ready = match b.parent {
                Some(p) => done.contains(&p),
                None => true,
            };
            if parent_ready {
                out.push(i);
                done.insert(i);
            }
        }
        // 進捗なし（理論上ここへは来ない）＝ 壊れたデータ。無限ループを避けて打ち切る。
        if out.len() == before {
            break;
        }
    }
    out
}

/// `/` 区切りの相対パスで子アクターを辿る（可変参照）。空パスは自分自身。
fn descend_by_path_mut<'a>(root: &'a mut Actor, path: &str) -> Option<&'a mut Actor> {
    let mut cur = root;
    for seg in path.split('/') {
        if seg.is_empty() {
            continue;
        }
        cur = cur.children_mut().iter_mut().find(|c| c.name == seg)?;
    }
    Some(cur)
}

/// スプライトルート配下で `CanvasTransform` を持つ全アクターを
/// 「相対パス + DFS ID」として集める（ボーン対応表のドロップ候補）。
///
/// DFS ID は `find_actor_by_dfs` と同じ規則（子孫を world_line 無関係に全カウント）で
/// 数えるため、ヒエラルキーがドラッグに載せる DFS ID とそのまま突き合わせられる。
fn collect_bone_candidates(
    node: &Actor,
    world: &World,
    node_dfs: u32,
    prefix: &str,
    out: &mut Vec<(String, u32)>,
) {
    let mut child_dfs = node_dfs + 1;
    for child in node.children() {
        let path = if prefix.is_empty() {
            child.name.clone()
        } else {
            format!("{prefix}/{}", child.name)
        };
        if world.get::<CanvasTransform>(child.entity).is_some() {
            out.push((path.clone(), child_dfs));
            collect_bone_candidates(child, world, child_dfs, &path, out);
        }
        // 子孫ぶんの DFS 番号を消費して次の兄弟へ進む
        child_dfs += 1 + subtree_len(child);
    }
}

/// アクターの子孫数（自分は含まない）。DFS 番号の送り幅計算に使う。
fn subtree_len(actor: &Actor) -> u32 {
    actor
        .children()
        .iter()
        .map(|c| 1 + subtree_len(c))
        .sum::<u32>()
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::loader::sprite_mesh::SpriteMesh;

    /// 2 ボーンの帯メッシュ（root → elbow）。
    const TWO_BONE_ARM: &str = include_str!("../../../../../tests/fixtures/two_bone_arm.sprite_mesh");

    /// 親が後ろに宣言されていても、親が必ず先に来る順序が得られる。
    #[test]
    fn topological_order_puts_parent_first() {
        let mesh = SpriteMesh::from_json(TWO_BONE_ARM).expect("読み込み成功");
        let order = topological_bone_order(&mesh);
        assert_eq!(order.len(), mesh.bones.len());
        let mut seen = HashSet::new();
        for &i in &order {
            if let Some(p) = mesh.bones[i].parent {
                assert!(seen.contains(&p), "親より先に子が来ている");
            }
            seen.insert(i);
        }
    }

    /// 相対パス降下: 空パスは自分自身、多段パスは子を辿る。
    #[test]
    fn descend_by_path_walks_children() {
        use crate::engine::ecs::World;
        let mut world = World::new();
        let re = world.spawn();
        let ce = world.spawn();
        let ge = world.spawn();
        let mut root = Actor::new_2d(re, "root");
        let mut child = Actor::new_2d(ce, "arm");
        child.add_child(Actor::new_2d(ge, "hand"));
        root.add_child(child);

        assert_eq!(descend_by_path_mut(&mut root, "").map(|a| a.name.clone()),
                   Some("root".to_string()));
        assert_eq!(descend_by_path_mut(&mut root, "arm/hand").map(|a| a.name.clone()),
                   Some("hand".to_string()));
        assert!(descend_by_path_mut(&mut root, "arm/none").is_none());
    }

    // ── ボーンアクター生成 ──────────────────────────────────

    /// ボーン生成: メッシュの親子関係どおりの階層になり、
    /// 各アクターの CanvasTransform にバインドポーズの TRS が入る。
    #[test]
    fn create_bone_actors_builds_hierarchy_with_bind_pose() {
        let mesh = SpriteMesh::from_json(TWO_BONE_ARM).expect("読み込み成功");
        let mut world = World::new();
        let mut children: Vec<Actor> = Vec::new();

        let created = create_bone_actors(&mut children, &mut world, &mesh, 0);
        assert_eq!(created, mesh.bones.len(), "全ボーンぶん生成される");

        // ルートボーンは children 直下に 1 体だけ
        let roots: Vec<&Actor> = children.iter().collect();
        assert_eq!(roots.len(), 1, "ルートボーンは 1 本（two_bone_arm）");
        let root_bone = roots[0];

        // 各ボーンについて、階層位置と CanvasTransform を検証する
        for (bi, bone) in mesh.bones.iter().enumerate() {
            let node = match bone.parent {
                None => {
                    assert_eq!(root_bone.name, bone.name);
                    root_bone
                }
                Some(pi) => {
                    let parent_name = &mesh.bones[pi].name;
                    // 親アクターの子として存在すること
                    let parent = find_by_name(&children, parent_name).expect("親アクターがある");
                    find_by_name(parent.children(), &bone.name).expect("子アクターがある")
                }
            };
            let ct = world
                .get::<CanvasTransform>(node.entity)
                .unwrap_or_else(|| panic!("ボーン {} に CanvasTransform がある", bone.name));
            assert_eq!(ct.position, bone.bind_position, "bone[{bi}] の位置");
            assert_eq!(ct.rotation, bone.bind_rotation, "bone[{bi}] の回転");
            assert_eq!(ct.scale, bone.bind_scale, "bone[{bi}] のスケール");
            assert!(node.is_2d(), "ボーンアクターは 2D");
        }
    }

    /// バインドポーズで生成した直後は、全ボーン行列が単位行列になる
    /// （= メッシュが無変形で表示される）。オーサリングの出発点として正しい状態。
    #[test]
    fn generated_bones_yield_identity_matrices() {
        use crate::engine::core::renderer::sprite_skin::build_bone_matrices;

        let mesh = SpriteMesh::from_json(TWO_BONE_ARM).expect("読み込み成功");
        let mut world = World::new();
        let root_entity = world.spawn();
        world.insert(root_entity, CanvasTransform::default());
        let mut root = Actor::new_2d(root_entity, "sprite");

        let mut children: Vec<Actor> = Vec::new();
        create_bone_actors(&mut children, &mut world, &mesh, 0);
        *root.children_mut() = children;

        let comp = SkinnedSpriteComponent::default();
        let (mats, unresolved) = build_bone_matrices(&mesh, &comp, &root, &world);
        assert!(unresolved.is_empty(), "全ボーンが自動解決される: {unresolved:?}");
        for (bi, m) in mats.iter().enumerate() {
            for r in 0..2 {
                for c in 0..4 {
                    let expect = if r == c { 1.0 } else { 0.0 };
                    assert!(
                        (m[r][c] - expect).abs() < 1.0e-4,
                        "bone[{bi}] の行列が単位行列でない: {m:?}"
                    );
                }
            }
        }
    }

    /// 既に同名アクターがある位置は作り直さない（既存のポーズを壊さない）。
    #[test]
    fn create_bone_actors_skips_existing_names() {
        let mesh = SpriteMesh::from_json(TWO_BONE_ARM).expect("読み込み成功");
        let mut world = World::new();
        let mut children: Vec<Actor> = Vec::new();

        let first = create_bone_actors(&mut children, &mut world, &mesh, 0);
        assert!(first > 0);
        let second = create_bone_actors(&mut children, &mut world, &mesh, 0);
        assert_eq!(second, 0, "2 回目は 1 体も増えない");
        assert_eq!(children.len(), 1, "ルート直下が増殖しない");
    }

    // ── bone_overrides JSON ─────────────────────────────────

    /// bone_overrides JSON: 往復して同じ対応表が得られる。空値は落ちる。
    #[test]
    fn bone_overrides_json_round_trip() {
        let mut src: BTreeMap<String, String> = BTreeMap::new();
        src.insert("root".into(), "rig/root".into());
        src.insert("elbow".into(), "rig/root/elbow".into());
        let json = serde_json::to_string(&src).expect("シリアライズ成功");

        let parsed = parse_bone_overrides_json(&json).expect("パース成功");
        assert_eq!(parsed, src);

        // 空文字列の値は「自動解決へ戻す」なので保存しない
        let with_empty = r#"{"root":"rig/root","hand":""}"#;
        let parsed2 = parse_bone_overrides_json(with_empty).expect("パース成功");
        assert_eq!(parsed2.len(), 1);
        assert_eq!(parsed2.get("root").map(String::as_str), Some("rig/root"));
        assert!(!parsed2.contains_key("hand"));

        // 壊れた JSON・オブジェクト以外は None（＝ シーンに触らない）
        assert!(parse_bone_overrides_json("{").is_none());
        assert!(parse_bone_overrides_json("[1,2]").is_none());
    }

    /// 名前で子アクターを探すテスト用ヘルパー。
    fn find_by_name<'a>(list: &'a [Actor], name: &str) -> Option<&'a Actor> {
        for a in list {
            if a.name == name {
                return Some(a);
            }
            if let Some(f) = find_by_name(a.children(), name) {
                return Some(f);
            }
        }
        None
    }
}
