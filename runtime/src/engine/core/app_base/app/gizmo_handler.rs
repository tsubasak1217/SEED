// ============================================================
//  gizmo_handler.rs — ギズモのヒットテスト・ドラッグ開始
//
//  actor_virtual_world_pos / selected_actors_centroid /
//  current_gizmo_pos / compute_gizmo_hover / try_gizmo_hit_and_start
// ============================================================

use crate::engine::components::{ModelComponent, Transform as ActorTransform, CanvasTransform};
use crate::engine::core::app_base::ipc::ToolMode;
use crate::engine::methods::gizmo_interact::{
    GizmoDrag, GizmoPart, screen_to_ray, screen_to_ray_ortho, hit_test_gizmo, start_drag,
};

use super::{App, find_actor_by_dfs, selection_centroid};

impl App {
    /// 全選択アクター（selected_actor_dfs_ids）のワールド位置重心を返す。
    /// 単一選択・マルチ選択共通で使用する。
    /// 2D キャンバスモードでは CanvasTransform.position を使う。
    pub(super) fn selected_actors_centroid(&self) -> Option<[f32; 3]> {
        if self.selected_actor_dfs_ids.is_empty() { return None; }
        let scene = self.scene.as_ref()?;
        let wl = self.active_world_line;
        let is_canvas = self.canvas_world_lines.contains(&wl);
        let mut sum = [0.0f32; 3];
        let mut count = 0usize;
        for &dfs_id in &self.selected_actor_dfs_ids {
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs_id as u32, &mut c) {
                let pos = if is_canvas {
                    // 2D: CanvasTransform から位置を取得する
                    scene.world.get::<CanvasTransform>(actor.entity)
                        .map(|ct| [ct.position[0], ct.position[1], 0.0])
                } else {
                    // 3D: MC の instance_mats[0] を優先、なければ ActorTransform.position を使う
                    actor.mc_entity()
                        .and_then(|e| scene.world.get::<ModelComponent>(e))
                        .and_then(|mc| mc.instance_mats.first())
                        .map(|m| [m[0][3], m[1][3], m[2][3]])
                        .or_else(|| scene.world.get::<ActorTransform>(actor.entity).map(|tf| tf.position))
                };
                if let Some(p) = pos {
                    sum[0] += p[0]; sum[1] += p[1]; sum[2] += p[2];
                    count += 1;
                }
            }
        }
        if count > 0 { Some([sum[0] / count as f32, sum[1] / count as f32, sum[2] / count as f32]) }
        else { None }
    }

    /// 選択中アクター/インスタンスのギズモ中心位置を返す共通ヘルパー。
    /// マルチ選択時は全選択アクターの重心を返す。
    pub(super) fn current_gizmo_pos(&self) -> Option<[f32; 3]> {
        // マルチ選択: 全選択アクターの重心
        if !self.selected_actor_dfs_ids.is_empty() {
            return self.selected_actors_centroid();
        }
        // 単一選択: selected_instances 重心、なければ ActorTransform 位置
        let scene = self.scene.as_ref()?;
        if let Some(dfs) = self.actor_virtual_selected_idx {
            let mut c = 0u32;
            let mc_slot_entity = find_actor_by_dfs(
                &scene.actors, self.active_world_line, dfs as u32, &mut c
            ).and_then(|a| a.mc_entity_at(self.actor_virtual_selected_slot_idx));
            let mc_pos = mc_slot_entity.and_then(|e| scene.world.get::<ModelComponent>(e))
                .and_then(|mc| {
                    if self.selected_instances.is_empty() {
                        mc.instance_mats.first().map(|m| [m[0][3], m[1][3], m[2][3]])
                    } else {
                        selection_centroid(&self.selected_instances, &mc.instance_mats)
                    }
                });
            mc_pos.or_else(|| self.actor_virtual_world_pos())
        } else {
            self.actor_virtual_world_pos()
        }
    }

    /// カーソル座標でギズモのヒットテストを行い、当たったパーツを返す。
    /// 2D キャンバスモードでは ortho レイと 2D 有効パーツのみで判定する。
    pub(super) fn compute_gizmo_hover(&self, cx: f32, cy: f32) -> Option<GizmoPart> {
        if self.tool_mode == ToolMode::Select { return None; }
        let gizmo_pos = self.current_gizmo_pos()?;
        let window_size = self.window.as_ref()?.inner_size();
        let vp_w = window_size.width  as f32;
        let vp_h = window_size.height as f32;
        let wl   = self.active_world_line;
        let is_canvas = self.canvas_world_lines.contains(&wl);

        let (ray_o, ray_d, radius) = if is_canvas {
            let cam_2d = self.canvas_cameras.get(&wl);
            let pan_x  = cam_2d.map(|c| c.pan_x).unwrap_or(0.0);
            let pan_y  = cam_2d.map(|c| c.pan_y).unwrap_or(0.0);
            let half_h = cam_2d.map(|c| c.ortho_half_h).unwrap_or(10.0);
            let half_w = half_h * (vp_w / vp_h);
            let r = half_h * 0.15;
            let (ro, rd) = screen_to_ray_ortho(cx, cy, vp_w, vp_h, pan_x, pan_y, half_w, half_h);
            (ro, rd, r)
        } else {
            let cam_pos_v = self.camera.position();
            let cam_pos   = [cam_pos_v.x, cam_pos_v.y, cam_pos_v.z];
            let d    = [gizmo_pos[0]-cam_pos[0], gizmo_pos[1]-cam_pos[1], gizmo_pos[2]-cam_pos[2]];
            let dist = (d[0]*d[0]+d[1]*d[1]+d[2]*d[2]).sqrt().max(0.01);
            let half_fov = self.camera.base.projection.fov_y_rad * 0.5;
            let r = dist * half_fov.tan() * 0.233;
            let view = self.camera.view_matrix();
            let proj = self.camera.projection_matrix();
            let (ro, rd) = screen_to_ray(cx, cy, vp_w, vp_h, &view.data, &proj.data, cam_pos);
            (ro, rd, r)
        };

        let part = hit_test_gizmo(ray_o, ray_d, gizmo_pos, radius, self.tool_mode)?;
        // 2D では Move/Scale の Z 軸・XZ/YZ 平面ハンドルは無効。
        // Rotate の AxisZ は 2D での回転操作に使うので有効とする。
        if is_canvas {
            match part {
                GizmoPart::AxisZ if self.tool_mode == ToolMode::Rotate => Some(part),
                GizmoPart::AxisZ | GizmoPart::PlaneXZ | GizmoPart::PlaneYZ => None,
                _ => Some(part),
            }
        } else {
            Some(part)
        }
    }

    /// カーソル座標でギズモのヒットテストを行い、当たった場合は GizmoDrag を返す。
    ///
    /// start_mat には重心を平行移動成分とする単位行列を使う
    /// （回転・スケールは各インスタンスが保持）。
    pub(super) fn try_gizmo_hit_and_start(&self, cx: f32, cy: f32) -> Option<GizmoDrag> {
        if self.tool_mode == ToolMode::Select { return None; }
        let gizmo_pos = self.current_gizmo_pos()?;

        let centroid_mat = [
            [1.0, 0.0, 0.0, gizmo_pos[0]],
            [0.0, 1.0, 0.0, gizmo_pos[1]],
            [0.0, 0.0, 1.0, gizmo_pos[2]],
            [0.0, 0.0, 0.0, 1.0f32],
        ];

        let window_size = self.window.as_ref()?.inner_size();
        let vp_w = window_size.width  as f32;
        let vp_h = window_size.height as f32;
        let wl   = self.active_world_line;
        let is_canvas = self.canvas_world_lines.contains(&wl);

        let (ray_o, ray_d, radius) = if is_canvas {
            let cam_2d = self.canvas_cameras.get(&wl);
            let pan_x  = cam_2d.map(|c| c.pan_x).unwrap_or(0.0);
            let pan_y  = cam_2d.map(|c| c.pan_y).unwrap_or(0.0);
            let half_h = cam_2d.map(|c| c.ortho_half_h).unwrap_or(10.0);
            let half_w = half_h * (vp_w / vp_h);
            let r = half_h * 0.15;
            let (ro, rd) = screen_to_ray_ortho(cx, cy, vp_w, vp_h, pan_x, pan_y, half_w, half_h);
            (ro, rd, r)
        } else {
            let cam_pos_v = self.camera.position();
            let cam_pos   = [cam_pos_v.x, cam_pos_v.y, cam_pos_v.z];
            let d    = [gizmo_pos[0]-cam_pos[0], gizmo_pos[1]-cam_pos[1], gizmo_pos[2]-cam_pos[2]];
            let dist = (d[0]*d[0]+d[1]*d[1]+d[2]*d[2]).sqrt().max(0.01);
            let half_fov = self.camera.base.projection.fov_y_rad * 0.5;
            let r = dist * half_fov.tan() * 0.233;
            let view = self.camera.view_matrix();
            let proj = self.camera.projection_matrix();
            let (ro, rd) = screen_to_ray(cx, cy, vp_w, vp_h, &view.data, &proj.data, cam_pos);
            (ro, rd, r)
        };

        let part = hit_test_gizmo(ray_o, ray_d, gizmo_pos, radius, self.tool_mode)?;
        // 2D では Move/Scale の Z 軸・XZ/YZ 平面ハンドルは無効。
        // Rotate の AxisZ は 2D での回転操作に使うので有効とする。
        if is_canvas {
            match part {
                GizmoPart::AxisZ if self.tool_mode == ToolMode::Rotate => {}
                GizmoPart::AxisZ | GizmoPart::PlaneXZ | GizmoPart::PlaneYZ => return None,
                _ => {}
            }
        }
        Some(start_drag(part, self.tool_mode, ray_o, ray_d, gizmo_pos, radius, centroid_mat))
    }
}
