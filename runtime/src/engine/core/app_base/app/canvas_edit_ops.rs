// ============================================================
//  canvas_edit_ops.rs — キャンバス編集タブ（シーン内キャンバスの隔離編集）
//
//  インスペクタの「キャンバスを編集」ボタンから開くキャンバス編集タブの
//  ランタイム側処理。ファイルベースのアクター編集タブ（OpenActor）と異なり、
//  シーン世界線（0）のアクターサブツリーを専用世界線へ「移動」して編集し、
//  タブを閉じるときに元の位置へ戻す。ファイル往復がないため
//  アクターの同一性（エンティティ・コンポーネント実データ）が保たれ、
//  編集はそのままシーンへ反映される（SCENE_MODIFIED は各編集操作が送信済み）。
//
//  【3D ワールドキャンバス（Actor3D + CanvasComponent）の扱い】
//  2D 描画・ピッキング・ギズモの既存コードパスはすべて
//  「ルートが Actor2D（CanvasTransform 持ち）」であることを前提とするため、
//  編集セッション中だけルートを一時的に Actor2D 化し、
//  CanvasTransform（デフォルト値 = 原点）を付与する。
//  ルート本来の 3D Transform コンポーネントには触れないため、
//  セッション終了時に Actor3D へ戻すだけで 3D 空間上の配置は完全に復元される。
//
//  handle_edit_canvas_begin / handle_edit_canvas_end
// ============================================================

use crate::engine::components::{CanvasTransform, ComponentKind};
use crate::engine::core::app_base::scene::{DebugCameraData, Scene};
use crate::engine::core::app_base::undo::UndoHistory;
use crate::engine::ecs::Entity;
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::ActorKind;

use super::{App, CanvasEditSession, extract_actor_by_dfs_with_origin, find_actor_by_entity_mut};

/// シーン世界線の番号（0 = 通常シーン）。
const SCENE_WORLD_LINE: u32 = 0;

impl App {
    /// キャンバス編集タブを開始する（IPC: EDIT_CANVAS_BEGIN:{world_line},{actor_dfs_id}）。
    ///
    /// シーン世界線（0）上の `actor_dfs_id` のアクターサブツリーを `world_line` へ移動し、
    /// 2D キャンバス編集世界線として登録してアクティブ化する。
    /// 成功時は `CANVAS_EDIT_WL:{world_line},{root_is_2d:0|1},{actor_name}` →
    /// `ACTOR_EDIT_STARTED` → `CAM_STATE:...` の順にエディタへ送信する。
    pub(super) fn handle_edit_canvas_begin(&mut self, world_line: u32, actor_dfs_id: u32) {
        // シーン世界線そのものへは開始できない・同一世界線の二重開始も不可
        if world_line == SCENE_WORLD_LINE {
            return;
        }
        if self.canvas_edit_sessions.contains_key(&world_line) {
            return;
        }

        // 現在の世界線のカメラを退避する（OpenActor と同じ手順）
        let pos = self.camera.base.transform.position;
        self.saved_cameras.insert(
            self.active_world_line,
            DebugCameraData {
                position: [pos.x, pos.y, pos.z],
                yaw: self.camera.yaw,
                pitch: self.camera.pitch,
                fov_deg: self.camera.base.projection.fov_y_rad.to_degrees(),
                far: self.camera.base.projection.far,
                speed: self.camera.move_speed,
            },
        );

        // シーンからアクターを取り出して専用世界線へ移動する
        let (root_is_2d, actor_name, session) = {
            let Some(scene) = &mut self.scene else { return };

            // 出自（親エンティティ・挿入位置）付きでサブツリーを取り出す
            let Some((mut actor, parent_entity, child_index)) =
                extract_actor_by_dfs_with_origin(&mut scene.actors, SCENE_WORLD_LINE, actor_dfs_id)
            else {
                eprintln!("[EDIT_CANVAS_BEGIN] DFS ID {actor_dfs_id} のアクターが見つかりません");
                return;
            };

            // CanvasComponent を持たないアクターは対象外（エディタ側でガード済みの二重チェック）。
            // 取り出したアクターを元の位置へ戻してから中断する。
            if !actor.has_kind(ComponentKind::Canvas) {
                eprintln!(
                    "[EDIT_CANVAS_BEGIN] DFS ID {actor_dfs_id} は CanvasComponent を持ちません"
                );
                Self::reinsert_scene_actor(scene, actor, parent_entity, child_index);
                return;
            }

            let root_is_2d = actor.is_2d();
            let actor_name = actor.name.clone();

            // サブツリー全体を専用世界線へ移動する（world_line はアクターのタグ）
            actor.set_world_line_recursive(world_line);

            // 3D ワールドキャンバス: 編集セッション中だけ 2D ルートとして振る舞わせる。
            // ルート本来の Transform（3D）はそのまま残し、END 時に元へ戻す。
            if !root_is_2d {
                actor.actor_kind = ActorKind::Actor2D;
                scene.world.insert(actor.entity, CanvasTransform::default());
            }

            scene.actors.push(actor);

            (
                root_is_2d,
                actor_name,
                CanvasEditSession {
                    parent_entity,
                    child_index,
                    was_3d_root: !root_is_2d,
                },
            )
        };

        // 2D キャンバス編集世界線として登録する（Ortho カメラ + 常時スクリーンスペース描画）
        self.canvas_world_lines.insert(world_line);
        self.actor_edit_canvas_wls.insert(world_line);
        self.canvas_edit_sessions.insert(world_line, session);

        // アクティブ世界線を切り替える（SetActiveWorldLine と同じリセット手順）
        self.active_world_line = world_line;
        let cam = self
            .saved_cameras
            .get(&world_line)
            .cloned()
            .unwrap_or_else(DebugCameraData::default);
        self.apply_camera_data(&cam);
        self.selected_instances.clear();
        self.actor_virtual_selected_idx = None;
        self.actor_virtual_selected_slot_idx = 0;
        self.undo_history = UndoHistory::new();
        self.send_selected();
        self.do_send_hierarchy();
        self.send_world_line_info();

        if let Some(ipc) = &self.ipc {
            // タブ生成用の応答（root_is_2d はタブを閉じたとき戻るシーンタブの判定に使う）
            let is_2d_flag = if root_is_2d { 1 } else { 0 };
            ipc.send(&format!(
                "CANVAS_EDIT_WL:{world_line},{is_2d_flag},{actor_name}"
            ));
            // 既存のアクター編集タブと同じ開始通知 → カメラ状態の順に送信する
            ipc.send("ACTOR_EDIT_STARTED");
            let (pos, euler_x, euler_y, euler_z, fov, far, spd) = self.cam_state_tuple();
            ipc.send(&format!(
                "CAM_STATE:{pos},{euler_x},{euler_y},{euler_z},{fov},{far},{spd}"
            ));
        }
    }

    /// キャンバス編集タブを終了する（IPC: EDIT_CANVAS_END:{world_line}）。
    ///
    /// 専用世界線のアクターサブツリーをシーン世界線（0）の元の位置へ戻し、
    /// 世界線の登録・カメラ状態を破棄する。編集中の世界線を閉じた場合は
    /// シーン世界線へ切り替えて `ACTOR_EDIT_ENDED` を送信する
    /// （エディタが別タブへ移動済みの場合は世界線切替を行わない）。
    pub(super) fn handle_edit_canvas_end(&mut self, world_line: u32) {
        let Some(session) = self.canvas_edit_sessions.remove(&world_line) else {
            return;
        };

        // アクターをシーン世界線へ戻す
        if let Some(scene) = &mut self.scene {
            // BEGIN で push したトップレベルアクター（この世界線に 1 体のみ）を取り出す
            if let Some(idx) = scene.actors.iter().position(|a| a.world_line == world_line) {
                let mut actor = scene.actors.remove(idx);
                actor.set_world_line_recursive(SCENE_WORLD_LINE);

                // 3D ワールドキャンバス: 一時的な 2D 化を解除して Actor3D へ戻す
                if session.was_3d_root {
                    actor.actor_kind = ActorKind::Actor3D;
                    scene.world.remove::<CanvasTransform>(actor.entity);
                }

                Self::reinsert_scene_actor(
                    scene,
                    actor,
                    session.parent_entity,
                    session.child_index,
                );
            }
        }

        // 世界線の登録・カメラ状態を破棄する
        self.canvas_world_lines.remove(&world_line);
        self.actor_edit_canvas_wls.remove(&world_line);
        self.saved_cameras.remove(&world_line);
        self.canvas_cameras.remove(&world_line);

        if self.active_world_line == world_line {
            // 編集中の世界線を閉じた場合: シーン世界線へ戻す（SetActiveWorldLine(0) と同じ手順）
            self.active_world_line = SCENE_WORLD_LINE;
            if let Some(cam) = self.saved_cameras.get(&SCENE_WORLD_LINE).cloned() {
                self.apply_camera_data(&cam);
            }
            self.selected_instances.clear();
            self.actor_virtual_selected_idx = None;
            self.actor_virtual_selected_slot_idx = 0;
            self.undo_history = UndoHistory::new();
            self.send_selected();
            self.do_send_hierarchy();
            self.send_world_line_info();
            if let Some(ipc) = &self.ipc {
                ipc.send("ACTOR_EDIT_ENDED");
                let (pos, euler_x, euler_y, euler_z, fov, far, spd) = self.cam_state_tuple();
                ipc.send(&format!(
                    "CAM_STATE:{pos},{euler_x},{euler_y},{euler_z},{fov},{far},{spd}"
                ));
            }
        } else if self.active_world_line == SCENE_WORLD_LINE {
            // シーン表示中に非アクティブなタブを閉じた場合: アクターが戻ったので
            // ヒエラルキーだけ更新する（世界線切替・カメラ操作は不要）
            self.do_send_hierarchy();
        }
    }

    /// キャンバス編集を終えたアクターをシーンツリーの元の位置へ戻す。
    ///
    /// - 親エンティティが Some: 親アクターの children の記録位置へ挿入する。
    ///   親がセッション中に削除されていた場合はシーン直下の末尾へフォールバックする。
    /// - 親エンティティが None: シーン直下（scene.actors）の記録位置へ挿入する。
    /// 記録位置が現在の要素数を超える場合は末尾へクランプする。
    fn reinsert_scene_actor(
        scene: &mut Scene,
        actor: Actor,
        parent_entity: Option<Entity>,
        child_index: usize,
    ) {
        match parent_entity {
            Some(pe) => {
                if let Some(parent) = find_actor_by_entity_mut(&mut scene.actors, pe) {
                    let idx = child_index.min(parent.children_mut().len());
                    parent.children_mut().insert(idx, actor);
                } else {
                    // 親がセッション中に削除された場合のフォールバック
                    scene.actors.push(actor);
                }
            }
            None => {
                let idx = child_index.min(scene.actors.len());
                scene.actors.insert(idx, actor);
            }
        }
    }
}
