// ============================================================
//  editor_preview/ops.rs — プレビューの出し入れ・作り直し・Play 開始で外す（impl App）
//
//  【作る（PREVIEW_PREFAB）】Edit のときだけ
//    1. 親（DFS）を引き、`under` の名前のパスで差し込み先をたどる（無ければどの名前が無いかを返す）
//    2. スクリプトの Instantiate と同じ経路（Scene::load_actor_into）で、枠（あれば）→ 中身の順に組み立てる
//       （入れ子のプレハブの中身は .actor に保存されたまま展開される＝実行時と同じ）
//    3. 種別の確かめ（validate_reparent_kind）: 枠 → 差し込み先、中身 → 枠の body（無ければ差し込み先）
//    4. 読み込んだ木の印を外す（clear_links_recursive）。3D の根はモデルの行列を Transform に合わせる
//    5. 中身を枠の body の末尾へ入れ（body が無ければ枠の直下＝ ScreenStack と同じ）、根に印を付け、
//       根の CanvasLayoutItem へレイヤーの底上げ（印の layer_bias）を付ける（無ければログだけ。editor_preview_bias.rs）
//    6. 差し込み先の末尾へ入れ、Undo を「シーンを変えない操作」として 1 件積み、未保存にしない知らせ付きで
//       ヒエラルキーを送る（SCENE_MODIFIED は送らない）。選択は変えない（DFS のずれは entity から引き直す）
//  【消す（PREVIEW_CLEAR / PREVIEW_CLEAR_ALL）】根を抜いて片付け、同じく Undo を 1 件積む
//  【作り直す（PREVIEW_REFRESH_PATH）】そのプレハブ・枠を使う根（外側だけ）を、同じ材料から組み立て直して
//    同じ位置へ差し替え、入れ子のプレビューも道筋で作り直す。世界線ごとに Undo を 1 件
//  【Play 開始で外す】Undo に積まない（Play 用の空の履歴なので。Play を止めると Play 前の写しから戻る）
//  失敗は `[Preview]` のログと `PREVIEW_ERROR:` で知らせ、木には何も残さない。
// ============================================================

use std::path::Path;
use std::sync::Arc;

use crate::engine::core::app_base::ipc::PreviewPrefabRequest;
use crate::engine::core::app_base::scene::Scene;
use crate::engine::core::app_base::undo::ActorTreeSnapshotCommand;
use crate::engine::core::scripting::ScriptingHost;
use crate::engine::ecs::{Entity, World};
use crate::engine::methods::drawer::DrawContext;
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::{ActorData, EditorPreviewInfo};
use crate::engine::structs::objects::actor::editor_preview_bias::apply_preview_layer_bias;

use super::super::actor_utils::{actor_kind_info, dfs_ids_for_entities};
use super::super::scene_save_ops::normalize_scene_path;
use super::super::script_scene_ops::sync_spawned_model_mats;
use super::super::{
    App, RuntimeMode, despawn_actor_recursive, extract_actor_by_entity, find_actor_by_dfs,
    find_actor_by_entity, find_actor_by_entity_mut, validate_reparent_kind,
};
use super::tree::{
    clear_links_recursive, entity_or_ancestor_matches, find_child_by_names, find_child_by_names_mut,
    find_child_by_path, find_child_by_path_mut, group_roots_by_world_line, locate_in_parent,
    matching_preview_roots, nested_previews, outermost_preview_roots, preview_root_of_dfs, walk_child_path,
    NestedPreview, TreeLocation,
};
use super::undo::EditorPreviewTreeCommand;
use super::wire::{format_added, format_cleared, format_error, format_refreshed, LOG_PREFIX};

/// シーン（ビューポート）の世界線。Play で動くのはこの世界線（アクタ編集タブは 1 以上）。
const SCENE_WORLD_LINE: u32 = 0;

// ── 利用者向けの文言（PREVIEW_ERROR にそのまま出る）─────────────────

/// Play 中にプレビューを出そうとしたとき。
const PLAY_MODE_ADD_REFUSED: &str = "Play 中はプレビューを出せません（Edit で使ってください）";
/// Play 中にプレビューを消そうとしたとき（Play を止めると Play 前の状態へ戻る）。
const PLAY_MODE_CLEAR_REFUSED: &str = "Play 中はプレビューを消せません（Play を止めると Play 前の状態へ戻ります）";
/// シーンが無いとき。
const NO_SCENE: &str = "シーンが読み込まれていません";
/// 描画の準備（DrawContext）が無いとき。
const NO_DRAW_CONTEXT: &str = "描画の準備ができていないためプレビューを組み立てられません";
/// 親（DFS）が見つからないとき（後ろに世界線と DFS を付ける）。
const PARENT_NOT_FOUND: &str = "プレビューの差し込み先の親が見つかりません";
/// `under` の名前のパスがたどれないとき（後ろにどの名前が無いかを付ける）。
const UNDER_NOT_FOUND: &str = "プレビューの差し込み先が見つかりません";
/// プレハブを読めないとき（後ろにパスと理由を付ける）。
const PREFAB_LOAD_FAILED: &str = "プレビューのプレハブを読めませんでした";
/// 根に CanvasLayoutItem が無く、レイヤーの底上げを付けられなかったとき（ログだけ。失敗にはしない）。
const LAYER_BIAS_NOT_APPLIED: &str = "根に CanvasLayoutItem が無いので底上げを付けられません（重なって見えることがあります）";
/// 差し込み先が地形ルートかその部分木の中のとき（Undo・Play の写しが地形を現物のまま残すため。レビュー #7）。
const INSIDE_TERRAIN_REFUSED: &str = "地形の中にはプレビューを出せません（地形のノードの外へ出してください）";
/// 組み立てたプレビューの差し込み先が消えていたとき（通常は起きない）。
const INSERT_TARGET_LOST: &str = "プレビューの差し込み先が見つかりませんでした";
/// 追加したプレビューの位置（DFS）を引けなかったとき（通常は起きない）。
const ADDED_ROOT_LOST: &str = "追加したプレビューの位置を引けませんでした";
/// 作り直す根が木から消えていたとき（通常は起きない）。
const REFRESH_ROOT_LOST: &str = "作り直すプレビューが見つかりませんでした";
/// 作り直しに失敗したとき（古いプレビューのまま残す。後ろに理由を付ける）。
const REFRESH_FAILED: &str = "プレビューを作り直せませんでした（古い表示のまま残します）";

/// プレビューを差し込む先（木にはまだ触らない段階で控える）。
struct InsertTarget {
    /// 差し込み先のアクタ
    entity: Entity,
    /// 差し込み先の種別 (is_2d, has_canvas)。プレビューの根の 2D/3D の確かめに使う
    kind: (bool, bool),
}

/// 選択を entity で控えたもの（プレビューの出し入れで DFS 番号がずれても同じアクタを選び直すため）。
#[derive(Default)]
struct SavedSelection {
    /// 選択中のアクタ（並びは元の選択の順）
    all: Vec<Entity>,
    /// 主の選択（インスペクタに出ている 1 体）
    primary: Option<Entity>,
}

/// 作り直す根 1 つ分の材料（木に触る前に控える）。
struct RefreshPlan {
    /// 根の印（作り直しの材料）
    info: EditorPreviewInfo,
    /// 根の内側の入れ子のプレビュー（DFS 順）
    nested: Vec<NestedPreview>,
    /// 根の世界線
    world_line: u32,
    /// 根の木の中の位置（同じ位置へ差し替える）
    location: TreeLocation,
    /// 根の親の種別（トップレベルなら None）
    parent_kind: Option<(bool, bool)>,
}

impl App {
    // ── 作る ─────────────────────────────────────────────

    /// PREVIEW_PREFAB: 親（DFS）の下の `under` の子へ、プレハブを保存されないプレビューとして作る。
    ///
    /// 成功したら `PREVIEW_ADDED:{world_line},{root_dfs}`、失敗したら `PREVIEW_ERROR:{理由}`（木には何も残さない）。
    pub(crate) fn handle_preview_prefab(&mut self, world_line: u32, parent_dfs: u32, request: PreviewPrefabRequest) {
        match self.try_add_preview(world_line, parent_dfs, request) {
            Ok(root_dfs) => self.send_preview_reply(&format_added(world_line, root_dfs)),
            Err(message) => self.report_preview_error(&message),
        }
    }

    /// プレビューを作る本体。成功なら根の DFS 番号。
    fn try_add_preview(&mut self, world_line: u32, parent_dfs: u32, request: PreviewPrefabRequest) -> Result<u32, String> {
        // ── 0. Edit のときだけ・シーンと描画の準備があるときだけ ──
        self.ensure_preview_editable(PLAY_MODE_ADD_REFUSED)?;
        if self.draw_ctx.is_none() {
            return Err(NO_DRAW_CONTEXT.to_string());
        }

        // ── 1. 差し込み先を決める（まだ何も作らない）──
        let target = self.resolve_insert_target(world_line, parent_dfs, &request.under)?;
        let info = EditorPreviewInfo {
            prefab: request.prefab,
            frame: request.frame,
            frame_body: request.frame_body,
            layer_bias: request.layer_bias,
        };

        // ── 2〜5. 組み立てる（失敗したら作った分は片付け済み）──
        let root = self.build_preview_tree(&info, world_line, Some(target.kind))?;
        let root_entity = root.entity;

        // ── 6. 木へ入れる（前後の木の写しで挟み、シーンを変えない Undo として 1 件積む）──
        let selection = self.capture_selection();
        let before = self.snapshot_actors_for_wl(world_line);
        if let Err(root) = self.attach_preview_root(target.entity, root) {
            // 直前に見つけた差し込み先が消えていた（通常は起きない）: 作った分を片付けて何もしない
            if let Some(scene) = self.scene.as_mut() {
                despawn_actor_recursive(&root, &mut scene.world);
            }
            return Err(INSERT_TARGET_LOST.to_string());
        }
        let root_dfs = self.dfs_of_entity(world_line, root_entity);
        let after = self.snapshot_actors_for_wl(world_line);
        self.record_preview_tree_change(world_line, before, after);

        // 通知: 未保存にしない知らせ付きのヒエラルキー（SCENE_MODIFIED は送らない）→ 選択を引き直して送る
        // （選択はヒエラルキーの送信の後。エディタが先に行を作るため）
        self.send_hierarchy_quiet();
        self.restore_selection(selection);
        root_dfs.ok_or_else(|| ADDED_ROOT_LOST.to_string())
    }

    /// 親（DFS）と `under` の名前のパスから差し込み先を決める（木には触らない）。
    ///
    /// 差し込み先が地形ルートか、その部分木の中なら断る（レビュー #7）。Undo の写しは地形ルートを
    /// 位置の印だけにして現物を Keep する（actor_ops.rs の snapshot_actors / rebuild_actors_for_wl）ので、
    /// 地形の中のプレビューは Ctrl+Z でも消えずに残って DFS をずらし、前に積んだ Undo が別のアクタへ当たる。
    fn resolve_insert_target(&self, world_line: u32, parent_dfs: u32, under: &str) -> Result<InsertTarget, String> {
        let scene = self.scene.as_ref().ok_or(NO_SCENE)?;
        let mut counter = 0u32;
        let parent = find_actor_by_dfs(&scene.actors, world_line, parent_dfs, &mut counter)
            .ok_or_else(|| format!("{PARENT_NOT_FOUND}（世界線 {world_line}・DFS {parent_dfs}）"))?;
        let target = walk_child_path(parent, under)
            .map_err(|missing| format!("{UNDER_NOT_FOUND}（「{}」の下に「{missing}」がありません）", parent.name))?;
        if entity_or_ancestor_matches(&scene.actors, target.entity, App::is_terrain_root) {
            return Err(format!("{INSIDE_TERRAIN_REFUSED}（差し込み先「{}」）", target.name));
        }
        Ok(InsertTarget { entity: target.entity, kind: actor_kind_info(target) })
    }

    /// 組み立てたプレビューの根を差し込み先の末尾の子として入れる。差し込み先が無ければ根を返す。
    fn attach_preview_root(&mut self, target: Entity, root: Actor) -> Result<(), Actor> {
        let Some(scene) = self.scene.as_mut() else { return Err(root) };
        match find_actor_by_entity_mut(&mut scene.actors, target) {
            Some(parent) => {
                parent.add_child(root);
                Ok(())
            }
            None => Err(root),
        }
    }

    /// プレビューの木を 1 つ組み立てる（まだ木に入れない。手順 2〜5）。
    ///
    /// 失敗したら作った分を片付けて理由を返す（World に何も残さない）。
    ///
    /// # 引数
    /// * `info`        - 組み立ての材料（中身・枠・枠の body）。根の印としてそのまま付ける
    /// * `world_line`  - 組み立てるアクタの世界線
    /// * `target_kind` - 差し込み先の種別 (is_2d, has_canvas)。トップレベルへ戻すときは None
    fn build_preview_tree(
        &mut self,
        info: &EditorPreviewInfo,
        world_line: u32,
        target_kind: Option<(bool, bool)>,
    ) -> Result<Actor, String> {
        let host = self.scripting_host.clone();
        let ctx = self.draw_ctx.as_ref().ok_or(NO_DRAW_CONTEXT)?;
        let scene = self.scene.as_mut().ok_or(NO_SCENE)?;
        assemble_preview_tree(info, ctx, &mut scene.world, host.as_ref(), world_line, target_kind)
    }

    // ── 消す ─────────────────────────────────────────────

    /// PREVIEW_CLEAR: `dfs` を含むプレビュー（根でも中のノードでもよい）を 1 つ消す。
    ///
    /// 応答は `PREVIEW_CLEARED:{count}`（含むプレビューが無ければ 0 で、木も履歴も触らない）。
    pub(crate) fn handle_preview_clear(&mut self, world_line: u32, dfs: u32) {
        if let Err(message) = self.ensure_preview_editable(PLAY_MODE_CLEAR_REFUSED) {
            self.report_preview_error(&message);
            return;
        }
        let root = self
            .scene
            .as_ref()
            .and_then(|scene| preview_root_of_dfs(&scene.actors, world_line, dfs));
        let removed = match root {
            Some(root) => self.remove_preview_roots_with_undo(world_line, &[root.entity]),
            None => 0,
        };
        self.send_preview_reply(&format_cleared(removed));
    }

    /// PREVIEW_CLEAR_ALL: その世界線のプレビュー（外側の根）を全部、1 回の Undo で消す。
    ///
    /// 応答は `PREVIEW_CLEARED:{count}`（無ければ 0 で、木も履歴も触らない）。
    pub(crate) fn handle_preview_clear_all(&mut self, world_line: u32) {
        if let Err(message) = self.ensure_preview_editable(PLAY_MODE_CLEAR_REFUSED) {
            self.report_preview_error(&message);
            return;
        }
        let roots = self
            .scene
            .as_ref()
            .map(|scene| outermost_preview_roots(&scene.actors, world_line))
            .unwrap_or_default();
        let removed = if roots.is_empty() { 0 } else { self.remove_preview_roots_with_undo(world_line, &roots) };
        self.send_preview_reply(&format_cleared(removed));
    }

    /// プレビューの根を抜いて片付け、シーンを変えない Undo を 1 件積んで知らせる（消した数を返す）。
    fn remove_preview_roots_with_undo(&mut self, world_line: u32, roots: &[Entity]) -> usize {
        let selection = self.capture_selection();
        let before = self.snapshot_actors_for_wl(world_line);
        let removed = self.detach_and_despawn(roots);
        if removed == 0 {
            return 0;
        }
        let after = self.snapshot_actors_for_wl(world_line);
        self.record_preview_tree_change(world_line, before, after);
        self.send_hierarchy_quiet();
        // 消えたものは選択から外れる（何も残らなければ SELECTED:-1。send_selected の規則）
        self.restore_selection(selection);
        removed
    }

    /// 根（エンティティ）を木から抜いて、部分木ごと World から片付ける（抜けた数を返す）。
    fn detach_and_despawn(&mut self, roots: &[Entity]) -> usize {
        let Some(scene) = self.scene.as_mut() else { return 0 };
        let mut removed = 0;
        for &entity in roots {
            if let Some(actor) = extract_actor_by_entity(&mut scene.actors, entity) {
                despawn_actor_recursive(&actor, &mut scene.world);
                removed += 1;
            }
        }
        removed
    }

    // ── 作り直す ─────────────────────────────────────────

    /// PREVIEW_REFRESH_PATH: そのプレハブ・枠（絶対パス or assets://）から作ったプレビューを作り直す。
    ///
    /// 比較は保存の突き合わせと同じ正規化（仮想パスの解決・区切り・大小文字）。外側の根だけを作り直し、
    /// 入れ子のプレビューは作り直した根の中へ道筋で作り直す。世界線ごとに Undo を 1 件積む。
    /// 組み立てに失敗した根は古いまま残し `PREVIEW_ERROR` も送る。
    /// 応答は `PREVIEW_REFRESHED:{作り直した根の数},{path}`（Edit 以外は何もしないで 0）。
    pub(crate) fn handle_preview_refresh_path(&mut self, path: &str) {
        let refreshed = if self.mode == RuntimeMode::Edit && self.draw_ctx.is_some() {
            self.refresh_previews_using(path)
        } else {
            0
        };
        self.send_preview_reply(&format_refreshed(refreshed, path));
    }

    /// `path` を中身か枠に使うプレビューを作り直す本体（作り直した根の数を返す）。
    fn refresh_previews_using(&mut self, path: &str) -> usize {
        let wanted = normalize_scene_path(path);
        let roots = match self.scene.as_ref() {
            Some(scene) => matching_preview_roots(&scene.actors, |info| preview_uses_path(info, &wanted)),
            None => return 0,
        };
        if roots.is_empty() {
            return 0;
        }

        // 表示中の世界線を作り直すなら、選択を entity で控える（作り直した中のアクタは選択から外れる）
        let active = self.active_world_line;
        let selection = roots.iter().any(|&(wl, _)| wl == active).then(|| self.capture_selection());

        let mut refreshed = 0usize;
        let mut active_changed = false;
        for (wl, entities) in group_roots_by_world_line(roots) {
            let before = self.snapshot_actors_for_wl(wl);
            let mut changed = 0usize;
            for entity in entities {
                match self.rebuild_preview_root(entity) {
                    Ok(()) => changed += 1,
                    // 組み立てに失敗した根は古いまま（木は変えていない）
                    Err(message) => self.report_preview_error(&message),
                }
            }
            if changed > 0 {
                let after = self.snapshot_actors_for_wl(wl);
                self.record_preview_tree_change(wl, before, after);
                refreshed += changed;
                active_changed |= wl == active;
            }
        }

        if active_changed {
            self.send_hierarchy_quiet();
            if let Some(selection) = selection {
                self.restore_selection(selection);
            }
        }
        refreshed
    }

    /// 根 1 つを同じ材料から組み立て直し、同じ位置へ差し替える（入れ子のプレビューも作り直す）。
    ///
    /// 組み立てに成功したときだけ木を変える（失敗したら古い根のまま理由を返す）。
    fn rebuild_preview_root(&mut self, entity: Entity) -> Result<(), String> {
        // ── 1. 古い根の材料を控える（印・入れ子・位置・親の種別）──
        let plan = self.plan_preview_refresh(entity)?;

        // ── 2. 同じ材料から新しい根を組み立てる ──
        let mut new_root = self
            .build_preview_tree(&plan.info, plan.world_line, plan.parent_kind)
            .map_err(|e| format!("{REFRESH_FAILED}: {e}"))?;

        // ── 3. 入れ子のプレビューを、新しい根の中の同じ道筋の親の末尾へ DFS 順に作り直す ──
        self.rebuild_nested_previews(&mut new_root, &plan.nested, plan.world_line);

        // ── 4. 古い根があった親の同じ位置へ差し替え、古い根を片付ける ──
        self.swap_preview_root(plan.location, entity, new_root)
    }

    /// 作り直す根の材料を木から控える（木には触らない）。
    fn plan_preview_refresh(&self, entity: Entity) -> Result<RefreshPlan, String> {
        let scene = self.scene.as_ref().ok_or(NO_SCENE)?;
        let old = find_actor_by_entity(&scene.actors, entity).ok_or(REFRESH_ROOT_LOST)?;
        let info = old.editor_preview.clone().ok_or(REFRESH_ROOT_LOST)?;
        let location = locate_in_parent(&scene.actors, entity).ok_or(REFRESH_ROOT_LOST)?;
        let parent_kind = location
            .parent
            .and_then(|parent| find_actor_by_entity(&scene.actors, parent))
            .map(actor_kind_info);
        Ok(RefreshPlan {
            info,
            nested: nested_previews(old),
            world_line: old.world_line,
            location,
            parent_kind,
        })
    }

    /// 控えた入れ子のプレビューを、新しい根の中で名前の道筋でたどった親の末尾へ DFS 順に作り直す。
    ///
    /// 道筋がたどれない・組み立てられない入れ子は捨てる（ログで知らせる）。
    fn rebuild_nested_previews(&mut self, new_root: &mut Actor, nested: &[NestedPreview], world_line: u32) {
        for preview in nested {
            let parent_path = preview.parent_path.join("/");
            let Some(parent_kind) = find_child_by_names(new_root, &preview.parent_path).map(actor_kind_info) else {
                eprintln!(
                    "{LOG_PREFIX} 入れ子のプレビュー（{}）の親「{parent_path}」が作り直した中に見つからないため捨てました",
                    preview.info.prefab
                );
                continue;
            };
            let built = match self.build_preview_tree(&preview.info, world_line, Some(parent_kind)) {
                Ok(built) => built,
                Err(e) => {
                    eprintln!("{LOG_PREFIX} 入れ子のプレビュー（{}）を作り直せないため捨てました: {e}", preview.info.prefab);
                    continue;
                }
            };
            match find_child_by_names_mut(new_root, &preview.parent_path) {
                Some(parent) => parent.add_child(built),
                // 直前にたどれた道筋なので起きない（保険: 作った分を片付ける）
                None => {
                    if let Some(scene) = self.scene.as_mut() {
                        despawn_actor_recursive(&built, &mut scene.world);
                    }
                }
            }
        }
    }

    /// 古い根があった位置へ新しい根を差し替え、古い根を部分木ごと片付ける。
    ///
    /// 位置に古い根が無かったら（通常は起きない）、新しい根を片付けて理由を返す（木は変えない）。
    fn swap_preview_root(&mut self, location: TreeLocation, old_entity: Entity, new_root: Actor) -> Result<(), String> {
        let Some(scene) = self.scene.as_mut() else { return Err(NO_SCENE.to_string()) };
        let siblings = match location.parent {
            Some(parent) => find_actor_by_entity_mut(&mut scene.actors, parent).map(|actor| actor.children_mut()),
            None => Some(&mut scene.actors),
        };
        match siblings {
            Some(siblings) if siblings.get(location.index).is_some_and(|actor| actor.entity == old_entity) => {
                let old = std::mem::replace(&mut siblings[location.index], new_root);
                despawn_actor_recursive(&old, &mut scene.world);
                Ok(())
            }
            _ => {
                despawn_actor_recursive(&new_root, &mut scene.world);
                Err(format!("{REFRESH_FAILED}: {REFRESH_ROOT_LOST}"))
            }
        }
    }

    // ── Play 開始で外す ───────────────────────────────────

    /// ENTER_PLAY: シーン（世界線 0）のプレビューを外す（play_mode_ops.rs が Play 開始の写しを取った直後に呼ぶ）。
    ///
    /// **Undo には積まない**: 呼ぶ時点の履歴は Play 用の空の履歴で、Play を止めると Play 前の写し
    /// （プレビュー入り）から木が戻り、Play 前の履歴の DFS とも合う。
    /// 外したら知らせ（未保存にしない印付きのヒエラルキー）を送る。
    pub(crate) fn remove_previews_for_play(&mut self) {
        let roots = match self.scene.as_ref() {
            Some(scene) => outermost_preview_roots(&scene.actors, SCENE_WORLD_LINE),
            None => return,
        };
        if roots.is_empty() {
            return;
        }
        let removed = self.detach_and_despawn(&roots);
        if removed > 0 {
            eprintln!("{LOG_PREFIX} Play の開始でプレビュー {removed} 個を外しました（Play を止めると戻ります）");
            self.send_hierarchy_quiet();
        }
    }

    // ── 共通 ─────────────────────────────────────────────

    /// Edit のときだけ・シーンがあるときだけ通す（Edit 以外は `not_edit_message` を返す）。
    fn ensure_preview_editable(&self, not_edit_message: &str) -> Result<(), String> {
        if self.mode != RuntimeMode::Edit {
            return Err(not_edit_message.to_string());
        }
        if self.scene.is_none() {
            return Err(NO_SCENE.to_string());
        }
        Ok(())
    }

    /// 木の前後の写しを「シーンを変えない操作」の Undo として 1 件積み、キャンバスの世界線の状態を合わせる。
    fn record_preview_tree_change(&mut self, world_line: u32, before: Vec<ActorData>, after: Vec<ActorData>) {
        self.undo_history.record(Box::new(EditorPreviewTreeCommand {
            inner: ActorTreeSnapshotCommand { world_line, before_actors: before, after_actors: after },
        }));
        self.update_canvas_wl_state_for(world_line);
    }

    /// 世界線 `world_line` の木でのエンティティの DFS 番号。
    fn dfs_of_entity(&self, world_line: u32, entity: Entity) -> Option<u32> {
        let scene = self.scene.as_ref()?;
        dfs_ids_for_entities(&scene.actors, world_line, &[entity]).first().copied().flatten()
    }

    /// いまのアクタの選択（表示中の世界線の DFS）を entity で控える。
    fn capture_selection(&self) -> SavedSelection {
        let Some(scene) = self.scene.as_ref() else { return SavedSelection::default() };
        let wl = self.active_world_line;
        let entity_of = |dfs: usize| -> Option<Entity> {
            let mut counter = 0u32;
            u32::try_from(dfs)
                .ok()
                .and_then(|dfs| find_actor_by_dfs(&scene.actors, wl, dfs, &mut counter))
                .map(|actor| actor.entity)
        };
        SavedSelection {
            all: self.selected_actor_dfs_ids.iter().filter_map(|&dfs| entity_of(dfs)).collect(),
            primary: self.actor_virtual_selected_idx.and_then(entity_of),
        }
    }

    /// 控えた選択を、いまの木の DFS 番号へ引き直して戻し、エディタへ送る。
    ///
    /// 消えたアクタは選択から外す。主の選択が消えていれば残った選択の最後を主にする
    /// （何も残らなければ未選択＝ SELECTED:-1。send_selected の規則）。主が残っていればインスペクタも送り直す。
    fn restore_selection(&mut self, saved: SavedSelection) {
        let Some(scene) = self.scene.as_ref() else { return };
        let wl = self.active_world_line;
        let to_dfs = |entities: &[Entity]| -> Vec<usize> {
            dfs_ids_for_entities(&scene.actors, wl, entities)
                .into_iter()
                .flatten()
                .map(|dfs| dfs as usize)
                .collect()
        };
        let all = to_dfs(&saved.all);
        let primary = saved
            .primary
            .and_then(|entity| to_dfs(&[entity]).first().copied())
            .or_else(|| all.last().copied());

        // 書き換えて送る（SELECTED と主の ACTOR_COMPONENTS。Undo/Redo の引き直しと共用。undo_selection.rs）
        self.apply_actor_selection(all, primary);
    }

    /// 応答をエディタへ送る（IPC が無ければ何もしない）。
    fn send_preview_reply(&self, reply: &str) {
        if let Some(ipc) = &self.ipc {
            ipc.send(reply);
        }
    }

    /// 失敗・拒否をログとエディタ（`PREVIEW_ERROR:`）へ知らせる。
    fn report_preview_error(&self, message: &str) {
        eprintln!("{LOG_PREFIX} {message}");
        self.send_preview_reply(&format_error(message));
    }
}

// ============================================================
//  組み立て（App を借りずに World と描画の準備だけで行う部分）
// ============================================================

/// プレビューの木を 1 つ組み立てる（手順 2〜5。失敗したら作った分を片付けて理由を返す）。
fn assemble_preview_tree(
    info: &EditorPreviewInfo,
    ctx: &DrawContext,
    world: &mut World,
    host: Option<&Arc<ScriptingHost>>,
    world_line: u32,
    target_kind: Option<(bool, bool)>,
) -> Result<Actor, String> {
    // ── 2. 組み立て（スクリプトの Instantiate と同じ経路。枠 → 中身の順）──
    let frame = match info.frame.as_deref() {
        Some(path) => Some(load_preview_part(path, ctx, world, host, world_line)?),
        None => None,
    };
    let content = match load_preview_part(&info.prefab, ctx, world, host, world_line) {
        Ok(content) => content,
        Err(e) => {
            if let Some(frame) = &frame {
                despawn_actor_recursive(frame, world);
            }
            return Err(e);
        }
    };

    // ── 3. 種別の確かめ（だめなら作った分を片付ける）──
    if let Err(e) = check_preview_kinds(frame.as_ref(), &content, info, target_kind) {
        if let Some(frame) = &frame {
            despawn_actor_recursive(frame, world);
        }
        despawn_actor_recursive(&content, world);
        return Err(e);
    }

    // ── 4. 読み込んだ木の印を外す・3D の根のモデルの行列を根の Transform に合わせる ──
    let mut content = content;
    prepare_loaded_part(world, &mut content);
    let mut root = match frame {
        Some(mut frame) => {
            prepare_loaded_part(world, &mut frame);
            // ── 5. 中身を枠の body の末尾へ入れる（根 = 枠）──
            put_into_frame_body(&mut frame, content, &info.frame_body);
            frame
        }
        None => content,
    };
    root.editor_preview = Some(info.clone());
    // 根へレイヤーの底上げを付ける（実行時の ScreenStack・ModalHost の前後に近づける）。
    // 根に CanvasLayoutItem が無ければ付けられないが、表示はできるので失敗にはしない（知らせるだけ）
    if !apply_preview_layer_bias(world, &root) {
        eprintln!("{LOG_PREFIX} {LAYER_BIAS_NOT_APPLIED}（{}・底上げ {}）", info.prefab, info.layer_bias);
    }
    Ok(root)
}

/// プレハブ 1 つを World へ組み立てる（スクリプトの Instantiate と同じ Scene::load_actor_into。世界線は子孫まで設定される）。
fn load_preview_part(
    path: &str,
    ctx: &DrawContext,
    world: &mut World,
    host: Option<&Arc<ScriptingHost>>,
    world_line: u32,
) -> Result<Actor, String> {
    Scene::load_actor_into(Path::new(path), ctx, world, host, world_line, None)
        .map_err(|e| format!("{PREFAB_LOAD_FAILED}: {path}（{e}）"))
}

/// 2D/3D の種別を確かめる: 枠 → 差し込み先、中身 → 枠の body（枠が無ければ差し込み先）。
fn check_preview_kinds(
    frame: Option<&Actor>,
    content: &Actor,
    info: &EditorPreviewInfo,
    target_kind: Option<(bool, bool)>,
) -> Result<(), String> {
    let content_parent_kind = match frame {
        Some(frame) => {
            let frame_path = info.frame.as_deref().unwrap_or_default();
            validate_reparent_kind(frame.is_2d(), target_kind).map_err(|m| format!("{m}（{frame_path}）"))?;
            // body が見つからなければ枠そのもの（put_into_frame_body と同じ規則）
            let body = find_child_by_path(frame, &info.frame_body).unwrap_or(frame);
            Some(actor_kind_info(body))
        }
        None => target_kind,
    };
    validate_reparent_kind(content.is_2d(), content_parent_kind).map_err(|m| format!("{m}（{}）", info.prefab))
}

/// 読み込んだプレハブの木を整える（印を外す・3D の根はモデルの行列を根の Transform に合わせる）。
fn prepare_loaded_part(world: &mut World, part: &mut Actor) {
    clear_links_recursive(part);
    if !part.is_2d() {
        sync_spawned_model_mats(world, part);
    }
}

/// 中身を枠の body（`frame_body` の名前のパス）の末尾へ入れる。
///
/// body が空・見つからなければ枠の直下（ScreenStack の `body.IsValid ? body : frame` と同じ）。
/// 見つからないときはログで知らせる。
fn put_into_frame_body(frame: &mut Actor, content: Actor, frame_body: &str) {
    if find_child_by_path(frame, frame_body).is_none() {
        eprintln!(
            "{LOG_PREFIX} 枠「{}」の中に「{frame_body}」が見つからないため、枠の直下へ入れます",
            frame.name
        );
    }
    match find_child_by_path_mut(frame, frame_body) {
        Some(body) => body.add_child(content),
        None => frame.add_child(content),
    }
}

/// プレビューの印がそのパス（正規化済み）を中身か枠に使っているか。
fn preview_uses_path(info: &EditorPreviewInfo, wanted: &str) -> bool {
    normalize_scene_path(&info.prefab) == wanted
        || info.frame.as_deref().is_some_and(|frame| normalize_scene_path(frame) == wanted)
}
