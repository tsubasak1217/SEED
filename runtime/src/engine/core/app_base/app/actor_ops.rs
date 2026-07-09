// ============================================================
//  actor_ops.rs — アクターの追加・削除・整理操作
//
//  handle_add_actor / handle_add_actor_2d / handle_drop_actor /
//  handle_remove_actor / handle_reparent_actor / handle_rename_actor /
//  apply_delete /
//  snapshot_actors_for_wl / rebuild_actors_for_wl
// ============================================================

use crate::engine::components::{ModelComponent, Transform as ActorTransform, ComponentKind};
use crate::engine::core::app_base::scene::{Scene, build_actor};
use crate::engine::core::app_base::undo::ActorTreeSnapshotCommand;
use crate::engine::components::{CanvasTransform, CanvasComponent};
use crate::engine::ecs::Entity;
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::ActorData;

use super::{
    App,
    find_actor_by_dfs, find_actor_by_dfs_mut,
    actor_subtree_size,
    extract_actor_by_dfs,
    despawn_actor_recursive,
    remove_actor_by_dfs,
    collect_entities_for_wl,
};

impl App {
    /// 3D Actor（ActorTransform を持つ）をシーンに追加する。
    ///
    /// `world_line` で対象の世界線を指定し、`parent_dfs_id` が Some の場合はその
    /// アクターの子として追加する。
    /// `spawn_pos` が Some の場合はその位置に配置する（コンテキストメニュー経由など）。
    /// None の場合はデフォルト Transform（原点）で配置する。操作は Undo/Redo の対象。
    pub(super) fn handle_add_actor(
        &mut self,
        world_line:    u32,
        parent_dfs_id: Option<u32>,
        spawn_pos:     Option<[f32; 3]>,
    ) {
        if self.scene.is_none() { return; }

        let before_actors = self.snapshot_actors_for_wl(world_line);

        let Some(scene) = &mut self.scene else { return };

        // World にエンティティを生成して Transform を挿入する。
        // Actor::with_name() が使う Entity::default() は index=0 で全アクターが衝突するため
        // 必ず world.spawn() で一意な entity を取得する。
        let entity = scene.world.spawn();
        let tf = if let Some(pos) = spawn_pos {
            ActorTransform { position: pos, rotation: [0.0, 0.0, 0.0], scale: [1.0, 1.0, 1.0] }
        } else {
            ActorTransform::default()
        };
        scene.world.insert(entity, tf);

        let new_actor = {
            let mut a = Actor::new(entity, "Actor");
            a.world_line = world_line;
            a
        };

        if let Some(pid) = parent_dfs_id {
            let mut c = 0u32;
            if let Some(parent) = find_actor_by_dfs_mut(&mut scene.actors, world_line, pid, &mut c) {
                parent.add_child(new_actor);
            }
        } else {
            scene.actors.push(new_actor);
        }

        let after_actors = self.snapshot_actors_for_wl(world_line);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line,
            before_actors,
            after_actors,
        }));

        self.send_hierarchy();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 2D Actor（CanvasTransform を持つ）をシーンに追加する。
    ///
    /// handle_add_actor の 2D 版。World に CanvasTransform を挿入し、
    /// Actor::new_2d でアクターを生成する。
    pub(super) fn handle_add_actor_2d(&mut self, world_line: u32, parent_dfs_id: Option<u32>) {
        if self.scene.is_none() { return; }

        let before_actors = self.snapshot_actors_for_wl(world_line);

        let Some(scene) = &mut self.scene else { return };

        // 2D Actor 専用エンティティを spawn して CanvasTransform を挿入する
        let entity = scene.world.spawn();
        scene.world.insert(entity, CanvasTransform::default());

        let new_actor = {
            let mut a = Actor::new_2d(entity, "Actor");
            a.world_line = world_line;
            a
        };

        if let Some(pid) = parent_dfs_id {
            let mut c = 0u32;
            if let Some(parent) = find_actor_by_dfs_mut(&mut scene.actors, world_line, pid, &mut c) {
                parent.add_child(new_actor);
            }
        } else {
            scene.actors.push(new_actor);
        }

        let after_actors = self.snapshot_actors_for_wl(world_line);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line,
            before_actors,
            after_actors,
        }));

        self.send_hierarchy();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 指定アクターの子として 3D アクターを追加する。
    ///
    /// world_line は親アクターから自動取得する（Inspector の右クリックメニュー等から使用）。
    pub(super) fn handle_add_actor_child(&mut self, parent_dfs_id: u32) {
        let wl = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, self.active_world_line, parent_dfs_id, &mut c)
                .map(|a| a.world_line)
                .unwrap_or(self.active_world_line)
        };

        // ── 種別不整合ガード（3D アクターを 2D アクターの子にすることを禁止） ──
        // 指定された親アクターが 2D（Actor2D）の場合、3D アクターの子追加を拒否する。
        // 2D アクターの子追加は handle_add_actor_2d_child 側で行われるため対象外。
        {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            let parent_is_2d = find_actor_by_dfs(&scene.actors, self.active_world_line, parent_dfs_id, &mut c)
                .map(|a| a.is_2d())
                .unwrap_or(false);
            if parent_is_2d {
                if let Some(ipc) = &self.ipc {
                    ipc.send("LOAD_ERROR:3Dアクターは2Dアクターの子にできません");
                }
                return;
            }
        }

        self.handle_add_actor(wl, Some(parent_dfs_id), None);
    }

    /// 指定アクターの子として 2D アクターを追加する。
    ///
    /// world_line は親アクターから自動取得する（Inspector の右クリックメニュー等から使用）。
    pub(super) fn handle_add_actor_2d_child(&mut self, parent_dfs_id: u32) {
        let wl = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, self.active_world_line, parent_dfs_id, &mut c)
                .map(|a| a.world_line)
                .unwrap_or(self.active_world_line)
        };
        self.handle_add_actor_2d(wl, Some(parent_dfs_id));
    }

    /// .actor ファイルをシーンにドロップ配置する（3D ビュー用）。
    ///
    /// 3D アクターの場合は `spawn_pos` にトランスフォームを設定して配置する。
    /// 2D アクター（Actor2D）の場合はドロップ位置を無視し、アクターファイルの
    /// CanvasTransform（アンカー・ピボット・position）をそのまま使用して配置する。
    /// ※ 2D シーンビュー（Edit View2D）での .actor2d ドロップは、ドロップ位置と
    ///   キャンバスヒット判定を使う handle_drop_actor_2d 側で処理される。
    /// いずれも world_line=0 のシーンに追加し、配置操作は Undo/Redo の対象。
    pub(super) fn handle_drop_actor(&mut self, path: &str, spawn_pos: [f32; 3]) {
        if self.draw_ctx.is_none() || self.scene.is_none() { return; }

        // Undo のために配置前の world_line=0 アクターをスナップショットする
        let before_actors = self.snapshot_actors_for_wl(0);

        let host = self.scripting_host.clone();

        // load_actor_into は draw_ctx と scene.world を同時に参照するため
        // ブロックスコープで借用ライフタイムを制限する
        let load_result = {
            let ctx   = self.draw_ctx.as_ref().unwrap();
            let scene = self.scene.as_mut().unwrap();
            Scene::load_actor_into(
                std::path::Path::new(path),
                ctx,
                &mut scene.world,
                host.as_ref(),
                0,    // world_line = 通常シーン
                None, // ルートエンティティは新規 spawn
            )
        };

        match load_result {
            Ok(actor) => {
                if actor.is_2d() {
                    // ── 2D キャンバスアクター ─────────────────────────────────────
                    // ドロップ位置は無視する。
                    // アクターファイルに保存された CanvasTransform（position/anchor/pivot/scale）を
                    // そのまま使用して配置場所を決定する。
                    // world_line=0 を 2D キャンバスモードとして登録する。
                    self.canvas_world_lines.insert(0);
                    let scene = self.scene.as_mut().unwrap();
                    scene.actors.push(actor);
                } else {
                    // ── 3D アクター ──────────────────────────────────────────────
                    // ドロップ位置に ActorTransform を設定し、instance_mats を同期する
                    let scene = self.scene.as_mut().unwrap();
                    let tf = ActorTransform {
                        position: spawn_pos,
                        rotation: [0.0, 0.0, 0.0],
                        scale:    [1.0, 1.0, 1.0],
                    };
                    let spawn_mat = tf.to_mat4();
                    scene.world.insert(actor.entity, tf);
                    // GPU 描画に使われる instance_mats も spawn 位置行列で更新する
                    // （ActorTransform だけでは描画には反映されないため）
                    for slot in actor.slots() {
                        if slot.kind == ComponentKind::Model {
                            if let Some(mc) = scene.world.get_mut::<ModelComponent>(slot.entity) {
                                for m in mc.instance_mats.iter_mut() {
                                    *m = spawn_mat;
                                }
                                mc.mark_batch_dirty();
                            }
                        }
                    }
                    scene.actors.push(actor);
                }

                // 配置後スナップショットを取得し、Undo 履歴に記録する
                let after_actors = self.snapshot_actors_for_wl(0);
                self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
                    world_line: 0,
                    before_actors,
                    after_actors,
                }));

                self.send_hierarchy();
                if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
            }
            Err(e) => {
                eprintln!("[Drop] load_actor_into ERR: {e}");
                if let Some(ipc) = &self.ipc {
                    ipc.send(&format!("LOAD_ERROR:{e}"));
                }
            }
        }
    }

    /// .actor2d ファイルを 2D シーンビュー（Edit View2D）へドロップ配置する。
    ///
    /// `screen_x`, `screen_y` はビューポートローカルの物理ピクセル座標
    /// （エディタの DROP_ACTOR IPC が送信する値）。2D ortho カメラの
    /// パン・ズームを反映してキャンバス（ortho）空間座標へ変換し、
    /// ドロップ位置にアクターを配置する。
    ///
    /// # 配置ルール（docs/editor_2d3d_tabs.md Phase 3）
    /// - ルートに CanvasComponent がある → シーンルートとして配置し、
    ///   ドロップ位置を root CanvasTransform.position に設定する
    /// - ルートに CanvasComponent がない → カーソルがヒットしている
    ///   ルートキャンバスの子として挿入し、そのキャンバスのローカル座標系で
    ///   position を設定する。ヒットなしなら先頭のルートキャンバス、
    ///   キャンバスが 1 つもなければデフォルトキャンバスを新規作成する
    ///
    /// 配置操作は ActorTreeSnapshotCommand で Undo/Redo の対象。
    pub(super) fn handle_drop_actor_2d(&mut self, path: &str, screen_x: u32, screen_y: u32) {
        if self.draw_ctx.is_none() || self.scene.is_none() { return; }

        // Undo のために配置前の world_line=0 アクターをスナップショットする
        let before_actors = self.snapshot_actors_for_wl(0);

        let host = self.scripting_host.clone();

        // load_actor_into は draw_ctx と scene.world を同時に参照するため
        // ブロックスコープで借用ライフタイムを制限する
        let load_result = {
            let ctx   = self.draw_ctx.as_ref().unwrap();
            let scene = self.scene.as_mut().unwrap();
            Scene::load_actor_into(
                std::path::Path::new(path),
                ctx,
                &mut scene.world,
                host.as_ref(),
                0,    // world_line = 通常シーン
                None, // ルートエンティティは新規 spawn
            )
        };

        match load_result {
            Ok(actor) => {
                // ドロップカーソル位置を ortho（キャンバス）空間へ変換する
                let drop_pt = self.window_to_canvas_2d(screen_x as f32, screen_y as f32);

                if !actor.is_2d() {
                    // 想定外の 3D アクター（拡張子判定と中身の不一致など）:
                    // ファイル保存値のままルート配置するフォールバック
                    let scene = self.scene.as_mut().unwrap();
                    scene.actors.push(actor);
                } else if actor.has_kind(ComponentKind::Canvas) {
                    // ── ルートに Canvas あり: シーンルートとして配置 ─────────────
                    // ドロップ位置（ortho 空間）から root position を逆算する。
                    // ルートレベルの順変換は
                    //   world = position + anchor_off（ビューポート中央基準）
                    // のため position = drop_pt - anchor_off。
                    // 注: CanvasViewportRef::Camera を持つファイルはウィンドウサイズで
                    //     近似する（配置後にレイアウトが自動追従するため実害なし）。
                    self.canvas_world_lines.insert(0);
                    let win_size = self.window.as_ref().map(|w| w.inner_size());
                    let vw = win_size.map_or(1280.0, |s| s.width  as f32);
                    let vh = win_size.map_or(720.0,  |s| s.height as f32);
                    let scene = self.scene.as_mut().unwrap();
                    if let Some(ct) = scene.world.get_mut::<CanvasTransform>(actor.entity) {
                        let anchor_off = [vw * ct.anchor[0] - vw / 2.0, vh * ct.anchor[1] - vh / 2.0];
                        ct.position = [drop_pt[0] - anchor_off[0], drop_pt[1] - anchor_off[1]];
                    }
                    scene.actors.push(actor);
                } else {
                    // ── ルートに Canvas なし: 既存ルートキャンバスの子として挿入 ──
                    self.canvas_world_lines.insert(0);

                    // ヒット判定（手前 = 後に描画されるキャンバスを優先）。
                    // ヒットなしなら先頭のルートキャンバスへフォールバックする。
                    let infos = self.collect_root_canvas_infos();
                    let target_entity: Option<Entity> = infos.iter().rev()
                        .find(|i| i.contains(drop_pt))
                        .map(|i| i.entity)
                        .or_else(|| infos.first().map(|i| i.entity));

                    // ルートキャンバスが 1 つもなければデフォルトキャンバスを新規作成する
                    let target_entity = target_entity
                        .unwrap_or_else(|| self.spawn_default_root_canvas());

                    // 新規作成分も含めて矩形情報を再収集し、ローカル座標を計算する
                    let infos = self.collect_root_canvas_infos();
                    if let Some(info) = infos.iter().find(|i| i.entity == target_entity) {
                        let scene = self.scene.as_mut().unwrap();
                        // 子アクター自身の anchor と scale_transform を考慮してローカル position を逆算する
                        // （スケールモードは各ノードの CanvasTransform が保持するため子の値を使う）
                        let (child_anchor, child_sm_transform) = scene.world.get::<CanvasTransform>(actor.entity)
                            .map(|ct| (ct.anchor, ct.scale_transform))
                            .unwrap_or(([0.0, 0.0], true));
                        let local_pos = info.world_to_child_local(drop_pt, child_anchor, child_sm_transform);
                        if let Some(ct) = scene.world.get_mut::<CanvasTransform>(actor.entity) {
                            ct.position = local_pos;
                        }
                        // ヒットしたキャンバス（トップレベル）の子として挿入する
                        if let Some(parent) = scene.actors.iter_mut()
                            .find(|a| a.world_line == 0 && a.entity == target_entity)
                        {
                            parent.add_child(actor);
                        } else {
                            // 親が見つからない場合はルートへフォールバック
                            scene.actors.push(actor);
                        }
                    } else {
                        // 矩形情報が取れない場合はルートへフォールバック
                        let scene = self.scene.as_mut().unwrap();
                        scene.actors.push(actor);
                    }
                }

                // 配置後スナップショットを取得し、Undo 履歴に記録する
                let after_actors = self.snapshot_actors_for_wl(0);
                self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
                    world_line: 0,
                    before_actors,
                    after_actors,
                }));

                self.send_hierarchy();
                if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
            }
            Err(e) => {
                eprintln!("[Drop2D] load_actor_into ERR: {e}");
                if let Some(ipc) = &self.ipc {
                    ipc.send(&format!("LOAD_ERROR:{e}"));
                }
            }
        }

        // ドロップ確定後はホバーハイライトを解除する
        self.drag_hover_canvas_entity = None;
    }

    /// デフォルト設定のルートスクリーンスペースキャンバスアクターを新規作成する。
    ///
    /// Canvas なしの 2D アクターをドロップした際に受け皿となるキャンバスが
    /// シーンに 1 つもない場合に呼ばれる（canvas_component_ops.rs の
    /// add_canvas_then_child_with_component と同じ既定値: 1920×1080・auto_scale）。
    /// 戻り値は作成したキャンバスアクターのエンティティ。
    fn spawn_default_root_canvas(&mut self) -> Entity {
        let scene = self.scene.as_mut().unwrap();

        // アクター本体（CanvasTransform）と Canvas スロットをそれぞれ spawn する
        let actor_entity = scene.world.spawn();
        scene.world.insert(actor_entity, CanvasTransform::default());
        let slot_entity = scene.world.spawn();
        scene.world.insert(slot_entity, CanvasComponent::default());

        let mut canvas_actor = Actor::new_2d(actor_entity, "Canvas");
        canvas_actor.world_line = 0;
        canvas_actor.add_slot_typed::<CanvasComponent>(
            "Canvas".to_string(), ComponentKind::Canvas, slot_entity,
        );
        scene.actors.push(canvas_actor);
        actor_entity
    }

    /// アクターを削除する（DFS id で特定）。
    pub(super) fn handle_remove_actor(&mut self, dfs_id: u32) {
        let Some(_scene) = &self.scene else { return };
        let wl = self.active_world_line;

        let before_actors = self.snapshot_actors_for_wl(wl);

        {
            let scene = self.scene.as_mut().unwrap();
            let mut c = 0u32;
            remove_actor_by_dfs(&mut scene.actors, wl, dfs_id, &mut c);
        }

        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));

        self.selected_instances.clear();
        self.actor_virtual_selected_idx = None;
        self.actor_virtual_selected_slot_idx = 0;
        if let Some(ipc) = &self.ipc { ipc.send("SELECTED:-1"); }
        // 2D アクターが全削除された場合に canvas_world_lines を更新する
        self.update_canvas_wl_state_for(wl);
        self.send_hierarchy();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// アクター編集モードでアクターツリーのペアレント関係を変更する。
    ///
    /// child_dfs / new_parent_dfs / anchor_sibling_dfs は変更前のアクターツリー上の DFS id。
    /// 実際にアクターを取り出して新しい親の下へ移動するため、ドラッグ追跡が正しく機能するようになる。
    ///
    /// anchor_sibling_dfs が Some の場合、そのアクターを基準に place_before に応じて
    /// 直前／直後へ挿入する（ヒエラルキーパネルでのドラッグ&ドロップ並べ替え用）。
    /// None または見つからない場合は従来どおり新しい親の末尾へ追加する。
    pub(super) fn handle_reparent_actor(
        &mut self,
        child_dfs: u32,
        new_parent_dfs: Option<u32>,
        anchor_sibling_dfs: Option<u32>,
        place_before: bool,
    ) {
        let wl = self.active_world_line;
        let Some(_scene) = &self.scene else { return };

        // ── 種別不整合ガード（3D アクターを 2D アクターの子にすることを禁止） ──
        // 「新しい親が 2D アクターかつ子が 3D アクター」の組み合わせだけを弾く。
        // 逆方向（3D 親に 2D 子）や同種同士（2D-2D / 3D-3D）は許可のまま。
        // ツリーから抽出する前に判定し、違反時は一切ツリーへ触れずに早期 return する。
        {
            let scene = self.scene.as_ref().unwrap();
            let mut c = 0u32;
            let child_is_2d = find_actor_by_dfs(&scene.actors, wl, child_dfs, &mut c)
                .map(|a| a.is_2d())
                .unwrap_or(false);
            let new_parent_is_2d = new_parent_dfs.map(|pid| {
                let mut c2 = 0u32;
                find_actor_by_dfs(&scene.actors, wl, pid, &mut c2)
                    .map(|a| a.is_2d())
                    .unwrap_or(false)
            }).unwrap_or(false);
            if new_parent_is_2d && !child_is_2d {
                if let Some(ipc) = &self.ipc {
                    ipc.send("LOAD_ERROR:3Dアクターは2Dアクターの子にできません");
                }
                return;
            }
        }

        let before_actors = self.snapshot_actors_for_wl(wl);

        {
            let scene = self.scene.as_mut().unwrap();

            // アンカー兄弟の Entity を取り出し前に控えておく。
            // Entity は世代付きで安定なため、child 取り出しに伴う DFS id ズレの影響を受けずに
            // 挿入位置を特定できる（DFS id ベースの補正よりも堅牢）。
            let anchor_entity = anchor_sibling_dfs.and_then(|adfs| {
                let mut c = 0u32;
                find_actor_by_dfs(&scene.actors, wl, adfs, &mut c).map(|a| a.entity)
            });

            // child のサブツリーサイズを先に算出する（取り出し後の DFS 補正に使う）
            let child_subtree_size = {
                let mut c = 0u32;
                find_actor_by_dfs(&scene.actors, wl, child_dfs, &mut c)
                    .map(|a| actor_subtree_size(a))
                    .unwrap_or(0)
            };
            if child_subtree_size == 0 { return; }

            // child をツリーから取り出す
            let mut extracted: Option<Actor> = None;
            let mut c = 0u32;
            extract_actor_by_dfs(&mut scene.actors, wl, child_dfs, &mut c, &mut extracted);
            let Some(mut child_actor) = extracted else { return };
            child_actor.set_world_line_recursive(wl);

            // child が new_parent より前（DFS 順）にある場合、取り出し後に new_parent の
            // DFS id が child_subtree_size 分ずれるため補正する
            let adjusted_parent_dfs = new_parent_dfs.map(|pid| {
                if child_dfs < pid { pid - child_subtree_size } else { pid }
            });

            // 挿入先の兄弟リストへ、アンカー（entity 一致）を基準に挿入する。
            // アンカーが指定されていない・見つからない場合は末尾へ追加する（従来動作）。
            let insert_with_anchor = |siblings: &mut Vec<Actor>, actor: Actor| {
                if let Some(ae) = anchor_entity {
                    if let Some(pos) = siblings.iter().position(|a| a.entity == ae) {
                        siblings.insert(if place_before { pos } else { pos + 1 }, actor);
                        return;
                    }
                }
                siblings.push(actor);
            };

            // 新しい親へ挿入する（None の場合はルートへ追加）
            if let Some(pid) = adjusted_parent_dfs {
                let mut c2 = 0u32;
                if let Some(parent) = find_actor_by_dfs_mut(&mut scene.actors, wl, pid, &mut c2) {
                    insert_with_anchor(parent.children_mut(), child_actor);
                } else {
                    // 親が見つからない場合はルートへフォールバック
                    scene.actors.push(child_actor);
                }
            } else {
                insert_with_anchor(&mut scene.actors, child_actor);
            }
        }

        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));

        self.send_hierarchy();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// アクター名を変更する。
    pub(super) fn handle_rename_actor(&mut self, dfs_id: u32, name: &str) {
        let Some(scene) = &mut self.scene else { return };
        let wl = self.active_world_line;
        let mut c = 0u32;
        if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, dfs_id, &mut c) {
            actor.name = name.to_string();
        }
        self.send_hierarchy();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 選択インスタンス／グループを削除し Undo 履歴に記録する。
    ///
    /// - `recursive = true`  → 子孫ごと削除（アクターツリーでは常に子孫ごと削除）
    /// - `recursive = false` → 指定ノードのみ削除（子孫は root へ移動）※現在は recursive と同一動作
    pub(super) fn apply_delete(&mut self, base_ids: &[u32], _recursive: bool) {
        let wl = self.active_world_line;
        if self.scene.is_none() { return; }

        let before_actors = self.snapshot_actors_for_wl(wl);

        // DFS id を降順にソートし、後ろから削除することで前方のアクターの id ズレを防ぐ
        let mut sorted_desc: Vec<u32> = base_ids.to_vec();
        sorted_desc.sort_unstable_by(|a, b| b.cmp(a));

        {
            let scene = self.scene.as_mut().unwrap();
            for &dfs_id in &sorted_desc {
                // アクターをツリーから取り出し、エンティティを World から despawn する
                let mut extracted: Option<Actor> = None;
                let mut c = 0u32;
                extract_actor_by_dfs(&mut scene.actors, wl, dfs_id, &mut c, &mut extracted);
                if let Some(actor) = extracted {
                    despawn_actor_recursive(&actor, &mut scene.world);
                }
            }
        }

        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));

        // 選択をクリアしてエディタへ通知
        self.selected_instances.clear();
        self.actor_virtual_selected_idx = None;
        self.actor_virtual_selected_slot_idx = 0;
        if let Some(ipc) = &self.ipc { ipc.send("SELECTED:-1"); }
        // 2D アクターが全削除された場合に canvas_world_lines を更新する
        self.update_canvas_wl_state_for(wl);
        self.send_hierarchy();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 指定世界線のアクターツリー全体をデータとしてスナップショットする。
    pub(super) fn snapshot_actors_for_wl(&self, wl: u32) -> Vec<ActorData> {
        self.scene.as_ref().map(|s| {
            s.actors.iter()
                .filter(|a| a.world_line == wl)
                .map(|a| a.to_data(&s.world))
                .collect()
        }).unwrap_or_default()
    }

    /// 指定世界線のアクターを data から再構築する（Undo/Redo 用）。
    pub(super) fn rebuild_actors_for_wl(&mut self, wl: u32, actors_data: Vec<ActorData>) {
        let host = self.scripting_host.clone();
        if self.draw_ctx.is_none() { return; }

        // scene を一時的に取り出して draw_ctx との同時借用問題を回避
        let mut scene = self.scene.take().unwrap_or_else(|| Scene::new("main"));

        // 既存の wl アクターエンティティを despawn して削除
        let old_entities: Vec<_> = collect_entities_for_wl(&scene.actors, wl);
        for e in old_entities { scene.world.despawn(e); }
        scene.actors.retain(|a| a.world_line != wl);

        // 新アクターを構築
        let ctx = self.draw_ctx.as_ref().unwrap();
        for data in actors_data {
            match build_actor(data, ctx, &mut scene.world, host.as_ref(), None) {
                Ok(mut a) => { a.set_world_line_recursive(wl); scene.actors.push(a); }
                Err(e) => eprintln!("[SEED] rebuild_actors_for_wl error: {e}"),
            }
        }

        self.scene = Some(scene);
        self.selected_instances.clear();
        self.actor_virtual_selected_idx = None;
        self.actor_virtual_selected_slot_idx = 0;

        // Undo/Redo 後も canvas_world_lines を正しく同期する
        self.update_canvas_wl_state_for(wl);
    }

    /// 指定 world_line に 2D アクターが残っているかを確認し、canvas_world_lines を更新する。
    /// アクター削除・Undo/Redo 後に呼び出すことで選択モードの不整合を防ぐ。
    ///
    /// # 判定ルール
    /// トップレベルアクター（scene.actors に直接登録）が is_2d() のみを対象とする。
    /// Actor3D の子として存在する Actor2D（3D Canvas の配下スプライト等）はここで
    /// canvas_world_lines に含めない。含めると canvas ID picking が 3D シーンで誤動作する。
    pub(super) fn update_canvas_wl_state_for(&mut self, wl: u32) {
        if let Some(scene) = &self.scene {
            let has = scene.actors.iter()
                .filter(|a| a.world_line == wl)
                .any(|a| a.is_2d());
            if has {
                self.canvas_world_lines.insert(wl);
            } else {
                self.canvas_world_lines.remove(&wl);
            }
        }
    }

    /// 選択中アクターをファイルへ書き出す（アクタファイル化）。
    ///
    /// - ルートのトランスフォームのみ 0 にリセット（子は相対位置を維持）
    /// - 子アクターも含め再帰的にシリアライズする
    /// - `path` はエディタの SaveFileDialog が返した絶対ファイルパス
    /// - 成功時: `EXPORT_ACTOR_OK:{saved_path}` を IPC で返す
    /// - 失敗時: `EXPORT_ACTOR_ERR:{reason}` を IPC で返す
    pub(super) fn handle_export_actor(&self, dfs_id: u32, path: &str) {
        let result: Result<String, String> = (|| {
            let scene = self.scene.as_ref().ok_or("シーンが読み込まれていません")?;
            let wl = self.active_world_line;

            // DFS ID でアクターを検索する
            let mut c = 0u32;
            let actor = find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c)
                .ok_or_else(|| format!("DFS ID {} のアクターが見つかりません", dfs_id))?;

            // World を参照してシリアライズ（子も再帰的に含まれる）
            let mut data = actor.to_data(&scene.world);

            // ルートの Transform を 0 にリセット（配置時の起点をオリジンに統一）
            if let Some(ref mut tf) = data.transform {
                tf.position = [0.0, 0.0, 0.0];
                tf.rotation = [0.0, 0.0, 0.0];
                tf.scale    = [1.0, 1.0, 1.0];
            }
            // 2D アクター: CanvasTransform の position のみリセットする。
            // ドロップ配置時にドロップ位置から position を設定し直すため 0 化が必要。
            // rotation / scale / pivot / anchor は見た目の再現に必要なため維持する
            // （docs/editor_2d3d_tabs.md Phase 3 の保存規則）。
            if let Some(ref mut ct) = data.canvas_transform {
                ct.position = [0.0, 0.0];
            }

            // 保存先ディレクトリが存在しない場合は作成する
            let save_path = std::path::Path::new(path);
            if let Some(parent) = save_path.parent() {
                std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
            }

            // pretty-print JSON で書き出す
            let json = serde_json::to_string_pretty(&data).map_err(|e| e.to_string())?;
            std::fs::write(save_path, json).map_err(|e| e.to_string())?;

            Ok(save_path.to_string_lossy().to_string())
        })();

        if let Some(ipc) = &self.ipc {
            match result {
                Ok(path) => ipc.send(&format!("EXPORT_ACTOR_OK:{path}")),
                Err(err) => ipc.send(&format!("EXPORT_ACTOR_ERR:{err}")),
            }
        }
    }
}
