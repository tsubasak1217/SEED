// ============================================================
//  gizmo_handler.rs — ギズモのヒットテスト・ドラッグ開始
//
//  actor_virtual_world_pos / selected_actors_centroid /
//  current_gizmo_pos / compute_gizmo_hover / try_gizmo_hit_and_start
// ============================================================

use crate::engine::components::{ModelComponent, Transform as ActorTransform, CanvasTransform, ComponentKind};
use crate::engine::core::app_base::ipc::ToolMode;
use crate::engine::methods::gizmo_interact::{
    GizmoDrag, GizmoPart, screen_to_ray, screen_to_ray_ortho, screen_to_ray_ortho3d,
    hit_test_gizmo, start_drag, hit_test_gizmo_canvas, start_drag_canvas,
    GIZMO_SCREEN_RADIUS_RATIO,
};

use super::{App, RuntimeMode, find_actor_by_dfs, selection_centroid, canvas_anchor_offset_for_dfs,
            find_parent_actor_of_dfs, get_3d_canvas_world_mat};

/// キャンバス座標（ピクセル）→ 3D ワールド座標の変換スケール係数。
const CANVAS_WORLD_SCALE: f32 = 1.0 / 100.0;

impl App {
    /// 全選択アクター（selected_actor_dfs_ids）のワールド位置重心を返す。
    /// 単一選択・マルチ選択共通で使用する。
    /// 2D キャンバスモードでは CanvasTransform.position を使う。
    pub(super) fn selected_actors_centroid(&self) -> Option<[f32; 3]> {
        if self.selected_actor_dfs_ids.is_empty() { return None; }
        let scene = self.scene.as_ref()?;
        let wl = self.active_world_line;
        let is_canvas = self.canvas_world_lines.contains(&wl);
        // ワールドスペースモード判定: エディタでスクリーンスペース OFF ならワールドスペース
        let in_editor = self.mode == RuntimeMode::Edit || self.paused;
        let use_screen_space = self.canvas_screen_space_overlay || !in_editor
            || self.actor_edit_canvas_wls.contains(&wl);
        let mut sum = [0.0f32; 3];
        let mut count = 0usize;
        for &dfs_id in &self.selected_actor_dfs_ids {
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs_id as u32, &mut c) {
                // シーン全体の is_canvas ではなく、アクター個別の種別で分岐する。
                // 3D/2D 混在シーンで 3D アクターを選んでも正しく ActorTransform を参照できる。
                let pos = if actor.is_2d() {
                    // 2D アクター: CanvasTransform から位置を取得し、アンカーオフセットを加算する
                    scene.world.get::<CanvasTransform>(actor.entity)
                        .map(|ct| {
                            let off = canvas_anchor_offset_for_dfs(
                                &scene.actors, &scene.world, wl, dfs_id as u32,
                            );
                            let cx = ct.position[0] + off[0];
                            let cy = ct.position[1] + off[1];

                            // 3D Canvas の子かどうか確認する（親が Actor3D + CanvasComponent）
                            let parent_ctw = {
                                let mut c_p = 0u32;
                                find_parent_actor_of_dfs(&scene.actors, wl, dfs_id as u32, &mut c_p, None)
                                    .and_then(|p| get_3d_canvas_world_mat(p, &scene.world))
                            };
                            if let Some(ctw) = parent_ctw {
                                // 3D Canvas の子: canvas_to_world を適用して正確な 3D 位置を計算する
                                [ctw[0][0]*cx + ctw[0][1]*cy + ctw[0][3],
                                 ctw[1][0]*cx + ctw[1][1]*cy + ctw[1][3],
                                 ctw[2][0]*cx + ctw[2][1]*cy + ctw[2][3]]
                            } else if let Some(ctx2d) = self.actor_2d_layout_ctx(dfs_id as u32) {
                                // シーン SS レイアウト時: 描画と完全に同一の変換チェーンで計算した
                                // ピボット点ワールド座標を使う（自動解像度・ルート恒等化・
                                // ビューポート基準アンカー・auto_scale 対応。Phase B バグ修正）
                                [ctx2d.pivot_world_px[0], ctx2d.pivot_world_px[1], 0.0]
                            } else {
                                // 通常 2D Canvas: ワールドスペースモードでは座標をスケール・Y 反転する
                                let ws     = if !use_screen_space { CANVAS_WORLD_SCALE } else { 1.0 };
                                let y_sign = if !use_screen_space { -1.0f32 } else { 1.0 };
                                [cx * ws, cy * ws * y_sign, 0.0]
                            }
                        })
                } else {
                    // 3D アクター: MC の instance_mats[0] を優先、なければ ActorTransform.position を使う
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

    /// 現在選択中のプライマリアクターが 2D かどうかを返す。
    ///
    /// シーン全体の `canvas_world_lines` ではなくアクター個別の種別で判定するため、
    /// 3D/2D 混在シーンで 3D アクターを選択中でも false を返す。
    pub(super) fn selected_primary_actor_is_2d(&self) -> bool {
        let scene = if let Some(s) = self.scene.as_ref() { s } else { return false };
        let wl = self.active_world_line;
        // actor_virtual_selected_idx が Some のとき、それがプライマリ選択
        if let Some(dfs) = self.actor_virtual_selected_idx {
            let mut c = 0u32;
            return find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c)
                .map(|a| a.is_2d())
                .unwrap_or(false);
        }
        // selected_actor_dfs_ids の先頭をプライマリとする
        if let Some(&dfs_id) = self.selected_actor_dfs_ids.first() {
            let mut c = 0u32;
            return find_actor_by_dfs(&scene.actors, wl, dfs_id as u32, &mut c)
                .map(|a| a.is_2d())
                .unwrap_or(false);
        }
        false
    }

    /// 選択中の 2D アクターが Actor3D + CanvasComponent の直接の子（3D Canvas 子）かを確認し、
    /// そうであれば canvas_to_world のX/Y/Z 軸（単位ベクトル）を返す。
    /// トップレベル 2D アクター・3D アクターの場合は None を返す。
    pub(super) fn selected_canvas_child_axes(&self) -> Option<[[f32; 3]; 3]> {
        let dfs = self.actor_virtual_selected_idx? as u32;
        let scene = self.scene.as_ref()?;
        let wl = self.active_world_line;
        // 2D アクターでなければ対象外
        let mut c = 0u32;
        let actor = find_actor_by_dfs(&scene.actors, wl, dfs, &mut c)?;
        if !actor.is_2d() { return None; }
        // 親が Actor3D + CanvasComponent かどうか確認する
        let mut c2 = 0u32;
        let parent = find_parent_actor_of_dfs(&scene.actors, wl, dfs, &mut c2, None)?;
        let ctw = get_3d_canvas_world_mat(parent, &scene.world)?;
        // canvas_to_world の各列を正規化してキャンバス軸を取得する
        let normalize = |v: [f32; 3]| {
            let l = (v[0]*v[0]+v[1]*v[1]+v[2]*v[2]).sqrt().max(1e-10);
            [v[0]/l, v[1]/l, v[2]/l]
        };
        let ax = normalize([ctw[0][0], ctw[1][0], ctw[2][0]]); // canvas X（列0）
        let ay = normalize([ctw[0][1], ctw[1][1], ctw[2][1]]); // canvas Y（列1）
        let az = normalize([ctw[0][2], ctw[1][2], ctw[2][2]]); // canvas 法線（列2）
        Some([ax, ay, az])
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

    /// Edit ビューモードにより選択アクターのギズモ操作を抑制すべきかを返す。
    ///
    /// - View3D（3D シーンビュー）: SS キャンバスの 2D アクターは非表示のため操作不可。
    ///   ただし 3D ワールドキャンバスの子（selected_canvas_child_axes が Some）は
    ///   3D ビューに表示されるため操作を継続する。
    /// - View2D（2D シーンビュー）: 3D アクターは非表示のため操作不可。
    ///
    /// frame_renderer 側の描画抑制（gizmo_suppressed_by_view）と対になる判定。
    pub(super) fn gizmo_suppressed_by_edit_view(&self) -> bool {
        (self.edit_view_hides_ss_canvas()
            && self.selected_primary_actor_is_2d()
            && self.selected_canvas_child_axes().is_none())
        || (self.edit_view_is_2d() && !self.selected_primary_actor_is_2d())
        // ビューポート所属のルートキャンバスは Transform 恒等固定のため
        // ビューポートタブでの移動・回転・スケール操作を不活性化する（Phase B）
        || (self.edit_view_is_2d() && self.selected_primary_is_viewport_root_canvas())
    }

    /// 選択中プライマリアクターが「ビューポート所属のルートキャンバス」
    /// （トップレベル Actor2D + CanvasComponent）かどうかを返す。
    ///
    /// ルートキャンバスは自動解像度・Transform 恒等固定の対象のため、
    /// ビューポートタブでのギズモ操作を抑制する判定に使用する。
    pub(super) fn selected_primary_is_viewport_root_canvas(&self) -> bool {
        let Some(scene) = self.scene.as_ref() else { return false };
        let wl = self.active_world_line;
        // プライマリ選択の DFS ID（selected_primary_actor_is_2d と同じ優先順位）
        let primary = self.actor_virtual_selected_idx
            .or_else(|| self.selected_actor_dfs_ids.first().copied());
        let Some(dfs) = primary else { return false };
        let mut c = 0u32;
        let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c) else { return false };
        // Actor2D かつ Canvas スロットを持つこと
        if !actor.is_2d() { return false; }
        if !actor.slots().iter().any(|s| s.kind == ComponentKind::Canvas) { return false; }
        // トップレベル（親なし）のみルートキャンバス
        let mut c2 = 0u32;
        find_parent_actor_of_dfs(&scene.actors, wl, dfs as u32, &mut c2, None).is_none()
    }

    /// エディタのデバッグカメラ（3D ビュー）でスクリーン座標をワールドレイに変換する。
    ///
    /// デバッグカメラの投影方式（透視 / 正射トグル）に応じてレイ生成を切り替える。
    /// 正射時に透視用レイを使うとヒット判定がずれるため、必ずこのヘルパーを経由する。
    pub(super) fn editor_3d_ray(&self, cx: f32, cy: f32, vp_w: f32, vp_h: f32) -> ([f32; 3], [f32; 3]) {
        let cam_pos_v = self.camera.position();
        let cam_pos   = [cam_pos_v.x, cam_pos_v.y, cam_pos_v.z];
        let view = self.camera.view_matrix();
        if self.camera.is_ortho() {
            // 正射投影: レイ原点がビュー平面上を移動し、方向はカメラ前方向で一定
            let half_h = self.camera.ortho_half_h.max(0.01);
            let half_w = half_h * (vp_w / vp_h);
            screen_to_ray_ortho3d(cx, cy, vp_w, vp_h, &view.data, cam_pos, half_w, half_h)
        } else {
            // 透視投影: カメラ位置からスクリーン方向へのレイ
            let proj = self.camera.projection_matrix();
            screen_to_ray(cx, cy, vp_w, vp_h, &view.data, &proj.data, cam_pos)
        }
    }

    /// エディタのデバッグカメラ（3D ビュー）でのギズモ半径を返す。
    ///
    /// スクリーン上の見た目の大きさが投影方式・ズームに依存せず一定になるよう、
    /// 透視: 距離 × tan(fov/2)、正射: ortho_half_h を基準に計算する。
    pub(super) fn editor_3d_gizmo_radius(&self, gizmo_pos: [f32; 3]) -> f32 {
        if self.camera.is_ortho() {
            // 正射投影: 可視半高に比例させてズームに追従（見た目の大きさ一定）
            self.camera.ortho_half_h.max(0.01) * GIZMO_SCREEN_RADIUS_RATIO
        } else {
            // 透視投影: カメラ距離と FOV から見た目の大きさが一定になる半径を計算
            let cam_pos = self.camera.position();
            let d = [gizmo_pos[0]-cam_pos.x, gizmo_pos[1]-cam_pos.y, gizmo_pos[2]-cam_pos.z];
            let dist = (d[0]*d[0]+d[1]*d[1]+d[2]*d[2]).sqrt().max(0.01);
            let half_fov = self.camera.base.projection.fov_y_rad * 0.5;
            dist * half_fov.tan() * GIZMO_SCREEN_RADIUS_RATIO
        }
    }

    /// カーソル座標でギズモのヒットテストを行い、当たったパーツを返す。
    /// 2D キャンバスモードでは 2D 有効パーツのみで判定する。
    /// スクリーンスペース: ortho レイ、ワールドスペース: perspective レイ を使用する。
    pub(super) fn compute_gizmo_hover(&self, cx: f32, cy: f32) -> Option<GizmoPart> {
        if self.tool_mode == ToolMode::Select { return None; }
        // 編集時物理タイムラインで過去フレームを表示中はGizmo操作不可
        if self.edit_physics_enabled && !self.edit_physics_at_latest { return None; }
        // Edit ビューモードで非表示のアクターはギズモ操作の対象にしない
        // （frame_renderer の gizmo_suppressed_by_view と対応する判定）
        if self.gizmo_suppressed_by_edit_view() { return None; }
        let gizmo_pos = self.current_gizmo_pos()?;
        let window_size = self.window.as_ref()?.inner_size();
        let vp_w = window_size.width  as f32;
        let vp_h = window_size.height as f32;
        let wl   = self.active_world_line;
        // ワールドスペースモード判定
        let in_editor = self.mode == RuntimeMode::Edit || self.paused;
        let use_screen_space = self.canvas_screen_space_overlay || !in_editor
            || self.actor_edit_canvas_wls.contains(&wl);
        // シーン全体の is_canvas ではなく選択アクター個別の種別で判定する。
        // ワールドスペース表示中（use_screen_space = false）の 2D アクターは
        // パースペクティブカメラで描画されるため 3D ギズモパスを使う。
        let gizmo_actor_is_2d = self.actor_edit_canvas_wls.contains(&wl)
            || (self.selected_primary_actor_is_2d() && use_screen_space);

        let (ray_o, ray_d, radius) = if gizmo_actor_is_2d && use_screen_space {
            // スクリーンスペース: 2D ortho レイ
            let cam_2d = self.canvas_cameras.get(&wl);
            let pan_x  = cam_2d.map(|c| c.pan_x).unwrap_or(0.0);
            let pan_y  = cam_2d.map(|c| c.pan_y).unwrap_or(0.0);
            let half_h = cam_2d.map(|c| c.ortho_half_h).unwrap_or(10.0);
            let half_w = half_h * (vp_w / vp_h);
            let r = half_h * 0.15;
            let (ro, rd) = screen_to_ray_ortho(cx, cy, vp_w, vp_h, pan_x, pan_y, half_w, half_h);
            (ro, rd, r)
        } else {
            // 3D デバッグカメラ（透視 / 正射は editor_3d_ray 内で分岐する）
            let (ro, rd) = self.editor_3d_ray(cx, cy, vp_w, vp_h);
            (ro, rd, self.editor_3d_gizmo_radius(gizmo_pos))
        };

        // 3D Canvas 子アクターの場合はキャンバス軸に沿った oriented ヒットテストを使う
        if let Some([ax, ay, az]) = self.selected_canvas_child_axes() {
            return hit_test_gizmo_canvas(ray_o, ray_d, gizmo_pos, radius, self.tool_mode, ax, ay, az);
        }
        let part = hit_test_gizmo(ray_o, ray_d, gizmo_pos, radius, self.tool_mode)?;
        // 2D キャンバスでは Move/Scale の Z 軸・XZ/YZ 平面ハンドルは無効。
        // Rotate の AxisZ は 2D での回転操作に使うので有効とする。
        if gizmo_actor_is_2d {
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
        // 編集時物理タイムラインで過去フレームを表示中はGizmo操作不可
        if self.edit_physics_enabled && !self.edit_physics_at_latest { return None; }
        // Edit ビューモードで非表示のアクターはギズモ操作の対象にしない
        if self.gizmo_suppressed_by_edit_view() { return None; }
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
        // ワールドスペースモード判定
        let in_editor = self.mode == RuntimeMode::Edit || self.paused;
        let use_screen_space = self.canvas_screen_space_overlay || !in_editor
            || self.actor_edit_canvas_wls.contains(&wl);
        // シーン全体の is_canvas ではなく選択アクター個別の種別で判定する。
        // ワールドスペース表示中（use_screen_space = false）の 2D アクターは
        // パースペクティブカメラで描画されるため 3D ギズモパスを使う。
        let gizmo_actor_is_2d = self.actor_edit_canvas_wls.contains(&wl)
            || (self.selected_primary_actor_is_2d() && use_screen_space);

        let (ray_o, ray_d, radius) = if gizmo_actor_is_2d && use_screen_space {
            // スクリーンスペース: 2D ortho レイ
            let cam_2d = self.canvas_cameras.get(&wl);
            let pan_x  = cam_2d.map(|c| c.pan_x).unwrap_or(0.0);
            let pan_y  = cam_2d.map(|c| c.pan_y).unwrap_or(0.0);
            let half_h = cam_2d.map(|c| c.ortho_half_h).unwrap_or(10.0);
            let half_w = half_h * (vp_w / vp_h);
            let r = half_h * 0.15;
            let (ro, rd) = screen_to_ray_ortho(cx, cy, vp_w, vp_h, pan_x, pan_y, half_w, half_h);
            (ro, rd, r)
        } else {
            // 3D デバッグカメラ（透視 / 正射は editor_3d_ray 内で分岐する）
            let (ro, rd) = self.editor_3d_ray(cx, cy, vp_w, vp_h);
            (ro, rd, self.editor_3d_gizmo_radius(gizmo_pos))
        };

        // 3D Canvas 子アクターの場合はキャンバス軸に沿った oriented ドラッグ開始を使う
        if let Some([ax, ay, az]) = self.selected_canvas_child_axes() {
            let part = hit_test_gizmo_canvas(
                ray_o, ray_d, gizmo_pos, radius, self.tool_mode, ax, ay, az,
            )?;
            return Some(start_drag_canvas(
                part, self.tool_mode, ray_o, ray_d, gizmo_pos, radius, centroid_mat, ax, ay, az,
            ));
        }
        let part = hit_test_gizmo(ray_o, ray_d, gizmo_pos, radius, self.tool_mode)?;
        // 2D キャンバスでは Move/Scale の Z 軸・XZ/YZ 平面ハンドルは無効。
        // Rotate の AxisZ は 2D での回転操作に使うので有効とする。
        if gizmo_actor_is_2d {
            match part {
                GizmoPart::AxisZ if self.tool_mode == ToolMode::Rotate => {}
                GizmoPart::AxisZ | GizmoPart::PlaneXZ | GizmoPart::PlaneYZ => return None,
                _ => {}
            }
        }
        Some(start_drag(part, self.tool_mode, ray_o, ray_d, gizmo_pos, radius, centroid_mat))
    }
}
