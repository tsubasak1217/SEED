// ============================================================
//  drag_handler.rs — カーソル移動・LMB ドラッグ処理
//
//  【含む処理】
//  - on_cursor_moved:    カーソル移動（矩形選択・ギズモドラッグ適用）
//  - handle_lmb_press:   LMB 押下時: ギズモヒット判定・ドラッグ開始状態収集
//  - handle_lmb_release: LMB 離し時: Undo 記録・ピックスケジュール
// ============================================================

use winit::dpi::PhysicalPosition;

use crate::engine::components::{
    ModelComponent, Transform as ActorTransform, ComponentKind, CanvasTransform,
};
use crate::engine::core::app_base::undo::{
    SelectionCommand, ActorGroupTransformCommand, MultiTransformCommand,
    ActorDfsSelectionCommand, MultiActorDragTransformCommand, CompositeCommand,
    CanvasTransformCommand,
};
use crate::engine::methods::gizmo_interact::{
    screen_to_ray_ortho, update_drag, mat4x4_mul, mat4x4_inv,
};

use super::{
    App, RuntimeMode,
    warp_cursor_to_local,
    find_actor_by_dfs,
    collect_mcs_in_world_line,
    collect_canvas_actors_in_rect,
    collect_transform_only_in_rect,
    collect_child_actor_drag_starts,
    canvas_anchor_offset_for_dfs,
    find_parent_actor_of_dfs,
    get_3d_canvas_world_mat,
    world_to_screen,
    camera_scene_gizmo,
    CANVAS_WORLD_SCALE,
};

impl App {
    // ============================================================
    //  on_cursor_moved
    // ============================================================

    /// カーソル移動処理。
    ///
    /// - RMB 移動量の閾値超え判定（カメラ grab 判定）
    /// - 矩形選択の更新（2D キャンバスまたは 3D）
    /// - ホバーギズモパーツの更新
    /// - ギズモドラッグ中の MC・Transform・Canvas の更新
    pub(super) fn on_cursor_moved(&mut self, position: PhysicalPosition<f64>) {
        let cx = position.x as f32;
        let cy = position.y as f32;
        self.input.process_cursor_moved(cx, cy);
        self.last_cursor_pos = Some((cx, cy));

        // ── モーダルトランスフォーム中 ──────────────────────────────
        // 矩形選択・ホバー判定・通常のギズモドラッグへは一切流さず、
        // モーダル専用の更新だけを行う（排他）。
        if self.modal_transform_active() {
            self.update_modal_transform(cx, cy);
            return;
        }

        // ── Pause モードカメラ回転 ───────────────────────────────────
        // Pause 中は Runtime が WS_CHILD として親プロセス内に埋め込まれており、
        // DeviceEvent::MouseMotion（Raw Input）がフォアグラウンドプロセス（エディタ）に
        // 横取りされてこのウィンドウには届かない。
        // 代替として CursorMoved（WM_MOUSEMOVE）のデルタをカメラ入力へ積算し、
        // カーソルをピボット（ウィンドウ中央）へ毎回ワープすることで無限回転を実現する。
        if self.paused && self.cam_input.rmb {
            if self.pause_cam_warp_pending > 0 {
                // 自己ワープが生成した CursorMoved イベントはスキップする
                self.pause_cam_warp_pending = self.pause_cam_warp_pending.saturating_sub(1);
                return;
            }
            if let Some((pvx, pvy)) = self.pause_cam_pivot {
                let dx = cx - pvx;
                let dy = cy - pvy;
                if dx * dx + dy * dy > 0.5 {
                    // デルタをカメラ入力へ積算する
                    self.cam_input.mouse_dx += dx;
                    self.cam_input.mouse_dy += dy;
                    // カーソルをピボットへ戻し、次の CursorMoved をスキップする
                    warp_cursor_to_local(self.window_hwnd(), pvx as i32, pvy as i32);
                    self.pause_cam_warp_pending = 1;
                    // 短押し判定をキャンセル（コンテキストメニュー抑制）
                    self.rmb_moved = true;
                }
            }
            return;
        }

        // RMB 押下中に移動量が閾値を超えたらカメラ grab とみなす
        if self.cam_input.rmb && !self.rmb_moved {
            if let Some((px, py)) = self.rmb_press_pos {
                let dx = cx - px;
                let dy = cy - py;
                if dx * dx + dy * dy > 25.0 {
                    self.rmb_moved = true;
                }
            }
        }

        // 矩形選択の更新（LMB 押下中かつギズモドラッグなし）。
        // 制御点キューブを掴んだ押下では矩形選択を始めない
        //（点を選んだ直後の微細なカーソル移動でラバーバンドが出るのを防ぐ）。
        if self.drag.lmb_held && self.drag.gizmo_drag.is_none() && !self.drag.control_point_picked {
            if let Some((px, py)) = self.drag.lmb_press_pos {
                let dx = cx - px;
                let dy = cy - py;
                if !self.drag.rect_selecting && dx * dx + dy * dy > 25.0 {
                    self.drag.rect_selecting = true;
                    self.drag.selection_before_rect         = self.selected_instances.clone();
                    self.drag.selection_before_rect_dfs     = self.selected_actor_dfs_ids.clone();
                    self.drag.selection_before_rect_primary = self.actor_virtual_selected_idx;
                }
                if self.drag.rect_selecting {
                    let sx_min = px.min(cx);
                    let sx_max = px.max(cx);
                    let sy_min = py.min(cy);
                    let sy_max = py.max(cy);
                    if let Some(scene) = &self.scene {
                        if let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) {
                            let wl   = self.active_world_line;
                            let vp_w = ws.width as f32;
                            let vp_h = ws.height as f32;
                            let mut rect_dfs: Vec<usize> = Vec::new();

                            // アクター編集タブの純粋 2D シーン（actor_edit_canvas_wls に含まれる）と
                            // Edit の 2D シーンビュー（edit_view_is_2d）は
                            // 2D キャンバス座標系での矩形選択を使用する。
                            // メインシーンの 3D ビューは 3D アクターと Canvas アクターが混在するため
                            // 3D 選択ロジックを使用する（2D シーンビューでは 3D アクターを選択しない）。
                            if self.actor_edit_canvas_wls.contains(&wl) || self.edit_view_is_2d() {
                                // 純粋 2D キャンバス編集タブ: スクリーン矩形をワールド矩形に変換して
                                // CanvasTransform.position が範囲内かで判定する
                                let cam_2d = self.canvas_cameras.get(&wl);
                                let pan_x  = cam_2d.map(|c| c.pan_x).unwrap_or(0.0);
                                let pan_y  = cam_2d.map(|c| c.pan_y).unwrap_or(0.0);
                                let half_h = cam_2d.map(|c| c.ortho_half_h).unwrap_or(10.0);
                                let half_w = half_h * (vp_w / vp_h);
                                // Y-down 規則（bottom=+half_h）でスクリーン→ワールド変換
                                let wx_min = pan_x + (2.0 * sx_min / vp_w - 1.0) * half_w;
                                let wx_max = pan_x + (2.0 * sx_max / vp_w - 1.0) * half_w;
                                let wy_min = pan_y + (2.0 * sy_min / vp_h - 1.0) * half_h;
                                let wy_max = pan_y + (2.0 * sy_max / vp_h - 1.0) * half_h;
                                let mut dfs_counter = 0u32;
                                for actor in scene.actors.iter().filter(|a| a.world_line == wl) {
                                    collect_canvas_actors_in_rect(
                                        actor, &scene.world, &mut dfs_counter,
                                        wx_min, wx_max, wy_min, wy_max, &mut rect_dfs,
                                    );
                                }
                            } else {
                                // 3D: MC の instance_mats をスクリーン投影して判定する
                                let view    = self.camera.view_matrix();
                                let proj    = self.camera.projection_matrix();
                                let all_mcs = collect_mcs_in_world_line(&scene.actors, &scene.world, wl);
                                for (_base, dfs_id, _slot_i, mc) in all_mcs {
                                    let in_rect = mc.instance_mats.iter().any(|m| {
                                        // 描画オフセット適用後（＝見た目）の位置で判定する。
                                        // 判定と見た目がズレると「描かれている場所を囲んでも選べない」
                                        // ことになるため、レンダラと同じ render_matrix を通す。
                                        let m = mc.render_matrix(*m);
                                        let world_pos = [m[0][3], m[1][3], m[2][3]];
                                        world_to_screen(world_pos, &view.data, &proj.data, vp_w, vp_h)
                                            .map(|(sx, sy)| sx >= sx_min && sx <= sx_max && sy >= sy_min && sy <= sy_max)
                                            .unwrap_or(false)
                                    });
                                    if in_rect && !rect_dfs.contains(&(dfs_id as usize)) {
                                        rect_dfs.push(dfs_id as usize);
                                    }
                                }

                                // カメラギズモ: アイコン位置をスクリーン投影して矩形内判定する
                                let cam_gizmo_mats_rect =
                                    camera_scene_gizmo::collect_camera_actor_matrices(
                                        &scene.actors, &scene.world, wl,
                                    );
                                for (dfs_id, icon_mat) in cam_gizmo_mats_rect {
                                    let world_pos = [icon_mat[0][3], icon_mat[1][3], icon_mat[2][3]];
                                    let in_rect = world_to_screen(
                                        world_pos, &view.data, &proj.data, vp_w, vp_h,
                                    )
                                    .map(|(sx, sy)| sx >= sx_min && sx <= sx_max && sy >= sy_min && sy <= sy_max)
                                    .unwrap_or(false);
                                    if in_rect && !rect_dfs.contains(&dfs_id) {
                                        rect_dfs.push(dfs_id);
                                    }
                                }

                                // ModelComponent を持たない 3D アクター（Transform のみ）も選択対象にする
                                // 既選択リスト（rect_dfs）への借用衝突を避けるため一時 Vec に収集して後で結合する
                                let mut tf_only: Vec<usize> = Vec::new();
                                collect_transform_only_in_rect(
                                    &scene.actors, &scene.world, wl,
                                    &view.data, &proj.data, vp_w, vp_h,
                                    sx_min, sx_max, sy_min, sy_max,
                                    &rect_dfs, &mut tf_only,
                                );
                                rect_dfs.extend(tf_only);
                            }

                            self.selected_actor_dfs_ids     = rect_dfs.clone();
                            self.actor_virtual_selected_idx = rect_dfs.last().copied();
                            self.selected_instances.clear();
                        }
                    }
                }
            }
        }

        // ホバーパーツを更新（ドラッグ中はドラッグパーツを維持）
        self.hovered_gizmo_part = if let Some(drag) = &self.drag.gizmo_drag {
            Some(drag.part)
        } else {
            self.compute_gizmo_hover(cx, cy)
        };

        // ギズモドラッグ中: 新しい変換行列を計算してインスタンスに適用する
        let new_mat_opt = if let Some(drag) = &self.drag.gizmo_drag {
            if let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) {
                let vp_w = ws.width  as f32;
                let vp_h = ws.height as f32;
                let wl_drag = self.active_world_line;
                let in_editor_drag = self.mode == RuntimeMode::Edit || self.paused;
                let use_ss_drag = self.canvas_screen_space_overlay || !in_editor_drag
                    || self.actor_edit_canvas_wls.contains(&wl_drag);
                // 選択アクター個別の is_2d() で判定する。
                // canvas_world_lines.contains(&wl) は「世界線に2Dアクターが存在するか」を表すため、
                // 3Dシーンに2Dアクターと3DアクターMixの場合に3Dアクターが誤って2D扱いになる。
                let selected_actor_is_2d = self.selected_primary_actor_is_2d();
                let (ro, rd) = if selected_actor_is_2d && use_ss_drag {
                    // スクリーンスペース: 2D ortho レイ
                    let cam_2d = self.canvas_cameras.get(&wl_drag);
                    let pan_x  = cam_2d.map(|c| c.pan_x).unwrap_or(0.0);
                    let pan_y  = cam_2d.map(|c| c.pan_y).unwrap_or(0.0);
                    let half_h = cam_2d.map(|c| c.ortho_half_h).unwrap_or(10.0);
                    let half_w = half_h * (vp_w / vp_h);
                    screen_to_ray_ortho(cx, cy, vp_w, vp_h, pan_x, pan_y, half_w, half_h)
                } else {
                    // 3D デバッグカメラ（透視 / 正射は editor_3d_ray 内で分岐する）
                    self.editor_3d_ray(cx, cy, vp_w, vp_h)
                };
                Some(update_drag(drag, ro, rd))
            } else { None }
        } else { None };

        // コントロールポイントのドラッグ中は、書き戻し先が「点 1 個」だけなので
        // アクタ／キャンバス向けの巨大な書き戻しブロックへは一切入らない。
        if self.drag.control_point_drag.is_some() {
            if let (Some(new_mat), Some(start_mat)) =
                (new_mat_opt, self.drag.gizmo_drag.as_ref().map(|d| d.start_mat))
            {
                let delta = mat4x4_mul(new_mat, mat4x4_inv(start_mat));
                self.apply_control_point_drag(delta);
            }
            return;
        }

        if let Some(new_mat) = new_mat_opt {
            self.apply_gizmo_new_mat(new_mat);
        }
    }

    // ============================================================
    //  apply_gizmo_new_mat
    // ============================================================

    /// ギズモの「新しい変換行列」をシーンへ書き戻す。
    ///
    /// `new_mat` はギズモ重心（`drag.start_mat`）に対する変換後の行列で、
    /// 実際に適用されるのは `delta = new_mat * inv(start_mat)` である。
    /// 書き戻し先は
    ///   - プライマリの MC インスタンス行列（ルート／子孫）と ActorTransform
    ///   - 選択スロット以外の MC スロット
    ///   - MC なしアクタの Transform / CanvasTransform
    ///   - 子孫アクタ（Model の有無を問わず）
    ///   - マルチ選択の非プライマリアクタ
    /// の全てで、いずれもドラッグ開始スナップショットに delta を掛けて求める。
    ///
    /// **モーダルトランスフォーム（G/R/S）もこの関数を共用する**。
    /// 単位デルタ（`new_mat == start_mat`）を渡すと、全対象が開始
    /// スナップショットの値へ完全復元されるため、モーダルの取消にも使える。
    pub(super) fn apply_gizmo_new_mat(&mut self, new_mat: [[f32; 4]; 4]) {
        {
            if let Some(drag) = &self.drag.gizmo_drag {
                let delta        = mat4x4_mul(new_mat, mat4x4_inv(drag.start_mat));
                let wl           = self.active_world_line;
                let selected_dfs = self.actor_virtual_selected_idx;

                // 2D ドラッグ書き戻し用: 描画と完全に同一の変換チェーンで計算した
                // レイアウトコンテキスト（親原点・アンカーオフセット・累積スケール）を
                // scene の可変借用前に事前計算する（シーン SS レイアウト時のみ Some）。
                // ルート恒等化・自動解像度・ビューポート基準アンカーを反映した逆変換に使用する。
                let drag_ctx_2d = self.drag.canvas_transform_drag_start.as_ref()
                    .map(|&(dfs, _)| dfs)
                    .and_then(|dfs| self.actor_2d_layout_ctx(dfs));

                if let Some(scene) = &mut self.scene {
                    // 選択スロット entity を取得して MC 行列にデルタを適用する
                    let selected_slot_i = self.actor_virtual_selected_slot_idx;
                    // mc_entity と actor_entity を同時に取得する（ActorTransform 更新のため）
                    let (mc_entity, drag_actor_entity): (
                        Option<crate::engine::ecs::Entity>,
                        Option<crate::engine::ecs::Entity>,
                    ) = if let Some(dfs) = selected_dfs {
                        let mut c = 0u32;
                        if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c) {
                            (actor.mc_entity_at(selected_slot_i), Some(actor.entity))
                        } else { (None, None) }
                    } else { (None, None) };
                    if let Some(mc) = mc_entity.and_then(|e| scene.world.get_mut::<ModelComponent>(e)) {
                        for &(idx, ref start) in &self.drag.drag_root_starts {
                            if let Some(m) = mc.instance_mats.get_mut(idx as usize) {
                                *m = mat4x4_mul(delta, *start);
                            }
                        }
                        for &(idx, ref start) in &self.drag.drag_child_starts {
                            if let Some(m) = mc.instance_mats.get_mut(idx as usize) {
                                *m = mat4x4_mul(delta, *start);
                            }
                        }
                        mc.mark_batch_dirty();
                    }
                    // MC アクターの ActorTransform もドラッグ中に更新する。
                    // frame_renderer がコライダーワイヤーフレーム位置を ActorTransform から
                    // 読み取るため、MC 更新と同期しないとワイヤーフレームが遅れる。
                    // 【注意】new_mat はギズモ重心行列（単位回転）のため、そのまま使うと
                    // 回転が 0 にリセットされる。drag_root_starts の開始行列に delta を
                    // 乗算することで元の回転・スケールを保持した正確な TRS 行列を計算する。
                    if mc_entity.is_some() && !self.drag.drag_root_starts.is_empty() {
                        if let Some(entity) = drag_actor_entity {
                            if let Some(actor_new_mat) = self.drag.drag_root_starts.first()
                                .map(|&(_, ref s)| mat4x4_mul(delta, *s))
                            {
                                if let Some(tf) = scene.world.get_mut::<ActorTransform>(entity) {
                                    *tf = ActorTransform::from_mat4(&actor_new_mat);
                                }
                            }
                        }
                    }
                    // 追加 MC スロット（選択スロット以外）にも同デルタを適用する
                    if let Some(dfs) = selected_dfs {
                        let extra_starts = self.drag.actor_extra_mc_drag_starts.clone();
                        for (slot_i, start_mats) in &extra_starts {
                            let mut c = 0u32;
                            let slot_entity = find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c)
                                .and_then(|a| a.mc_entity_at(*slot_i));
                            if let Some(entity) = slot_entity {
                                if let Some(mc) = scene.world.get_mut::<ModelComponent>(entity) {
                                    for (m, start) in mc.instance_mats.iter_mut().zip(start_mats.iter()) {
                                        *m = mat4x4_mul(delta, *start);
                                    }
                                    mc.mark_batch_dirty();
                                }
                            }
                        }
                    }
                    // MC なし（インスタンス空含む）のアクターは Transform を直接ドラッグする
                    if self.drag.drag_root_starts.is_empty() {
                        // canvas_drag_start が設定されているなら2Dアクター、
                        // actor_transform_drag_start が設定されているなら3Dアクターとして処理する。
                        // canvas_world_lines.contains(&wl) は「世界線に2Dアクターが存在するか」であり、
                        // 3D/2D混在シーンで3DアクターをMixすると誤判定するためここでは使わない。
                        if self.drag.canvas_transform_drag_start.is_some() {
                            // 2D: CanvasTransform の XY 位置・Z 回転・XY スケールを更新する
                            if let Some((drag_dfs, ref start_ct)) = self.drag.canvas_transform_drag_start.clone() {
                                let entity = {
                                    let mut c = 0u32;
                                    find_actor_by_dfs(&scene.actors, wl, drag_dfs, &mut c)
                                        .map(|a| a.entity)
                                };
                                if let Some(entity) = entity {
                                    // new_mat の平行移動成分はアンカーオフセット込みのgizmo位置を基点とするため、
                                    // CanvasTransform.position（アンカーオフセットなし）に戻すためにオフセットを引く。
                                    let anchor_off = canvas_anchor_offset_for_dfs(
                                        &scene.actors, &scene.world, wl, drag_dfs,
                                    );

                                    // 3D Canvas の子かどうか確認する
                                    let parent_ctw = {
                                        let mut c_p = 0u32;
                                        find_parent_actor_of_dfs(&scene.actors, wl, drag_dfs, &mut c_p, None)
                                            .and_then(|p| get_3d_canvas_world_mat(p, &scene.world))
                                    };

                                    if let Some(ct) = scene.world.get_mut::<CanvasTransform>(entity) {
                                        if let Some(ctw) = parent_ctw {
                                            // 3D Canvas の子: canvas_to_world の逆変換でキャンバス座標に戻す
                                            let ctw_inv = mat4x4_inv(ctw);
                                            let wx = new_mat[0][3];
                                            let wy = new_mat[1][3];
                                            let wz = new_mat[2][3];
                                            let cx = ctw_inv[0][0]*wx + ctw_inv[0][1]*wy + ctw_inv[0][2]*wz + ctw_inv[0][3];
                                            let cy = ctw_inv[1][0]*wx + ctw_inv[1][1]*wy + ctw_inv[1][2]*wz + ctw_inv[1][3];
                                            match self.tool_mode {
                                                crate::engine::core::app_base::ipc::ToolMode::Rotate => {
                                                    // 3D Canvas の Y 反転を考慮して回転方向を逆符号にする
                                                    let delta_angle = new_mat[1][0].atan2(new_mat[0][0]).to_degrees();
                                                    ct.rotation = start_ct.rotation - delta_angle;
                                                    ct.scale    = start_ct.scale;
                                                }
                                                crate::engine::core::app_base::ipc::ToolMode::Scale => {
                                                    let sx = (new_mat[0][0]*new_mat[0][0] + new_mat[1][0]*new_mat[1][0]).sqrt();
                                                    let sy = (new_mat[0][1]*new_mat[0][1] + new_mat[1][1]*new_mat[1][1]).sqrt();
                                                    if sx > 0.001 { ct.scale[0] = start_ct.scale[0] * sx; }
                                                    if sy > 0.001 { ct.scale[1] = start_ct.scale[1] * sy; }
                                                    ct.rotation = start_ct.rotation;
                                                }
                                                _ => {
                                                    // Move: canvas_to_world 逆変換で canvas 座標に変換
                                                    ct.position[0] = cx - anchor_off[0];
                                                    ct.position[1] = cy - anchor_off[1];
                                                    ct.rotation = start_ct.rotation;
                                                    ct.scale    = start_ct.scale;
                                                }
                                            }
                                            ct.pivot = start_ct.pivot;
                                        } else {
                                            // 通常 2D Canvas 処理
                                            // SS 表示かどうか（位置の逆変換と回転方向の符号に使用）。
                                            // drag_ctx_2d が Some のとき（シーン SS レイアウト。
                                            // View2D ビューポートタブ含む）は常に SS 扱い。
                                            let in_editor_c = self.mode == RuntimeMode::Edit || self.paused;
                                            let use_ss_c = drag_ctx_2d.is_some()
                                                || self.canvas_screen_space_overlay || !in_editor_c
                                                || self.actor_edit_canvas_wls.contains(&wl);
                                            if let Some(ctx2d) = &drag_ctx_2d {
                                                // シーン SS レイアウト: 描画と同一チェーンの逆変換で
                                                // position を求める（自動解像度・ルート恒等化・
                                                // ビューポート基準アンカー・auto_scale 対応）。
                                                // world → 親キャンバスローカル（親累積回転の逆適用）
                                                let dx = new_mat[0][3] - ctx2d.parent_canvas_origin[0];
                                                let dy = new_mat[1][3] - ctx2d.parent_canvas_origin[1];
                                                let (sin_p, cos_p) = ctx2d.parent_world_rot.sin_cos();
                                                let local_x =  cos_p * dx + sin_p * dy;
                                                let local_y = -sin_p * dx + cos_p * dy;
                                                // アンカーオフセットを除去し、
                                                // sm_transform 時は親累積スケールの逆数を適用する
                                                let px = local_x - ctx2d.anchor_off[0];
                                                let py = local_y - ctx2d.anchor_off[1];
                                                if ctx2d.sm_transform {
                                                    let sx = if ctx2d.cumul_scale[0].abs() > f32::EPSILON { ctx2d.cumul_scale[0] } else { 1.0 };
                                                    let sy = if ctx2d.cumul_scale[1].abs() > f32::EPSILON { ctx2d.cumul_scale[1] } else { 1.0 };
                                                    ct.position[0] = px / sx;
                                                    ct.position[1] = py / sy;
                                                } else {
                                                    ct.position[0] = px;
                                                    ct.position[1] = py;
                                                }
                                            } else {
                                                // 従来経路（ワールドスペース・アクター編集タブ）
                                                // ワールドスペースでは平行移動をキャンバスピクセルに変換し、
                                                // Y 軸を再反転（レンダリング時に反転済みのため元に戻す）
                                                let pos_inv_scale = if use_ss_c { 1.0 } else { 1.0 / CANVAS_WORLD_SCALE };
                                                let y_inv_sign = if use_ss_c { 1.0f32 } else { -1.0 };
                                                ct.position[0] = new_mat[0][3] * pos_inv_scale - anchor_off[0];
                                                ct.position[1] = new_mat[1][3] * pos_inv_scale * y_inv_sign - anchor_off[1];
                                            }
                                            match self.tool_mode {
                                                crate::engine::core::app_base::ipc::ToolMode::Rotate => {
                                                    // new_mat = Rz(delta) * T(pos) なので col0 の XY 角度がデルタ回転。
                                                    // ワールドスペース描画時は Y 軸が反転しているため回転方向を逆符号にする。
                                                    let delta_angle = new_mat[1][0].atan2(new_mat[0][0]).to_degrees();
                                                    let rot_sign = if use_ss_c { 1.0f32 } else { -1.0 };
                                                    ct.rotation = start_ct.rotation + delta_angle * rot_sign;
                                                    ct.scale    = start_ct.scale;
                                                }
                                                crate::engine::core::app_base::ipc::ToolMode::Scale => {
                                                    // new_mat の各列の長さ = centroid 起点のスケール係数
                                                    let sx = (new_mat[0][0]*new_mat[0][0] + new_mat[1][0]*new_mat[1][0]).sqrt();
                                                    let sy = (new_mat[0][1]*new_mat[0][1] + new_mat[1][1]*new_mat[1][1]).sqrt();
                                                    if sx > 0.001 { ct.scale[0] = start_ct.scale[0] * sx; }
                                                    if sy > 0.001 { ct.scale[1] = start_ct.scale[1] * sy; }
                                                    ct.rotation = start_ct.rotation;
                                                }
                                                _ => {
                                                    // Move: 位置のみ変化
                                                    ct.rotation = start_ct.rotation;
                                                    ct.scale    = start_ct.scale;
                                                }
                                            }
                                            // ピボットはドラッグ中変化なし
                                            ct.pivot = start_ct.pivot;
                                        }
                                    }
                                }
                            }
                        } else if let Some((drag_dfs, ref start_tf)) = self.drag.actor_transform_drag_start.clone() {
                            let new_mat_tf = mat4x4_mul(delta, start_tf.to_mat4());
                            let entity = {
                                let mut c = 0u32;
                                find_actor_by_dfs(&scene.actors, wl, drag_dfs, &mut c)
                                    .map(|a| a.entity)
                            };
                            if let Some(entity) = entity {
                                if let Some(tf) = scene.world.get_mut::<ActorTransform>(entity) {
                                    *tf = ActorTransform::from_mat4(&new_mat_tf);
                                }
                            }
                        }
                    }
                    // 子孫アクタにも同デルタを適用する。
                    // MC 行列と Transform はそれぞれ独自の開始行列から再計算するため、
                    // Model を持たない子（カメラ等）でも Transform だけが正しく追従する。
                    {
                        let child_starts = self.drag.actor_child_drag_starts.clone();
                        for cs in &child_starts {
                            let (mc_slot_entity, child_actor_entity) = {
                                let mut c = 0u32;
                                if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, cs.dfs_id, &mut c) {
                                    (actor.mc_entity(), Some(actor.entity))
                                } else { (None, None) }
                            };
                            // MC 先頭インスタンス行列（Model を持つ子のみ）
                            if let (Some(entity), Some(mc_start)) = (mc_slot_entity, cs.mc_start) {
                                if let Some(mc) = scene.world.get_mut::<ModelComponent>(entity) {
                                    if let Some(m) = mc.instance_mats.first_mut() {
                                        *m = mat4x4_mul(delta, mc_start);
                                    }
                                    mc.mark_batch_dirty();
                                }
                            }
                            // 子アクターの ActorTransform も更新する
                            // （コライダーワイヤーフレーム・子カメラのビュー行列が追従する）
                            if let Some(entity) = child_actor_entity {
                                if scene.world.get::<ActorTransform>(entity).is_some() {
                                    let new_tf = ActorTransform::from_mat4(&mat4x4_mul(delta, cs.tf_start));
                                    if let Some(tf) = scene.world.get_mut::<ActorTransform>(entity) {
                                        *tf = new_tf;
                                    }
                                }
                            }
                        }
                    }
                    // マルチ選択: プライマリ以外の全選択アクターにも同デルタを適用する
                    if !self.drag.multi_actor_drag_starts.is_empty() {
                        let multi_starts = self.drag.multi_actor_drag_starts.clone();
                        for (other_dfs, start_mat) in &multi_starts {
                            let mut c = 0u32;
                            if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, *other_dfs, &mut c) {
                                let new_mat_other = mat4x4_mul(delta, *start_mat);
                                let actor_entity = actor.entity;
                                let mc_entity    = actor.mc_entity();
                                // MC の instance_mats を更新する（GPU 描画位置）
                                if let Some(me) = mc_entity {
                                    if let Some(mc) = scene.world.get_mut::<ModelComponent>(me) {
                                        if let Some(m) = mc.instance_mats.first_mut() {
                                            *m = new_mat_other;
                                        }
                                        mc.mark_batch_dirty();
                                    }
                                }
                                // ActorTransform も更新する（Inspector 反映用）
                                if let Some(tf) = scene.world.get_mut::<ActorTransform>(actor_entity) {
                                    *tf = ActorTransform::from_mat4(&new_mat_other);
                                }
                            }
                        }
                    }
                }
            }
            // ドラッグ中のリアルタイム IPC 送信は廃止（ドラッグ終了時に送信）
        }
    }

    // ============================================================
    //  handle_lmb_press
    // ============================================================

    /// LMB 押下時: ギズモヒット判定とドラッグ開始状態の収集。
    ///
    /// `self.last_cursor_pos` がない場合は何もしない。
    pub(super) fn handle_lmb_press(&mut self) {
        if let Some((cx, cy)) = self.last_cursor_pos {
            self.drag.lmb_held      = true;
            self.drag.lmb_press_pos = Some((cx, cy));
            self.drag.ctrl_at_press = self.ctrl_held;

            // 軸ギズモドットのクリック判定（他のすべての処理より優先）
            if let Some(hit) = self.axis_gizmo_hovered {
                self.snap_camera_to_axis(hit);
                return;
            }

            // ギズモヒットを優先。外れた場合は release 時にピックまたは矩形選択。
            if let Some(drag) = self.try_gizmo_hit_and_start(cx, cy) {
                // コントロールポイントを選択中は、ギズモの対象が「点 1 個」なので
                // アクタ側の開始スナップショット（MC インスタンス・子孫・マルチ選択）は
                // 一切集めない。集めてしまうと、点を動かしたつもりでアクタごと動く。
                if self.selected_control_point.is_some() {
                    self.begin_control_point_drag();
                    self.drag.gizmo_drag = Some(drag);
                    return;
                }
                self.collect_transform_drag_starts();
                self.drag.gizmo_drag = Some(drag);
            } else if self.try_pick_control_point(cx, cy) {
                // 制御点キューブを掴んだ。この後の release では通常のオブジェクトピックを
                // 行わない（行うとアクタ選択が更新され、選んだばかりの点が消える）。
                self.drag.control_point_picked = true;
            }
        }
    }

    // ============================================================
    //  collect_transform_drag_starts
    // ============================================================

    /// 選択中アクタ群の「変形開始時スナップショット」を一括収集する。
    ///
    /// 収集先はすべて `self.drag`（DragState）で、内訳は
    ///   - プライマリ MC のルート／子孫インスタンス行列
    ///   - 選択スロット以外の MC スロットの全インスタンス行列
    ///   - 子孫アクタ（Model の有無を問わず全件）
    ///   - MC なしアクタの Transform / CanvasTransform
    ///   - マルチ選択の非プライマリアクタの行列
    /// である。`apply_gizmo_new_mat` はここで集めた開始値にデルタを掛けて
    /// 書き戻すため、**ギズモドラッグ開始とモーダルトランスフォーム開始の
    /// 両方がこの関数を共用する**。
    pub(super) fn collect_transform_drag_starts(&mut self) {
        let wl              = self.active_world_line;
        let selected_dfs    = self.actor_virtual_selected_idx;
        let selected_slot_i = self.actor_virtual_selected_slot_idx;
        self.drag.multi_actor_drag_starts.clear();
        if let Some(scene) = self.scene.as_ref() {
                    // 選択スロット entity を取得する
                    let mc_entity: Option<crate::engine::ecs::Entity> =
                        selected_dfs.and_then(|dfs| {
                            let mut c = 0u32;
                            find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c)
                                .and_then(|a| a.mc_entity_at(selected_slot_i))
                        });
                    if let Some(mc) = mc_entity.and_then(|e| scene.world.get::<ModelComponent>(e)) {
                        // selected_instances が空（矩形選択・マルチ選択後）の場合は
                        // インスタンス 0 をドラッグ対象として扱う
                        let inst_slice: &[u32] = if self.selected_instances.is_empty() {
                            &[0]
                        } else {
                            &self.selected_instances
                        };
                        let roots = mc.filter_selection_roots(inst_slice);
                        self.drag.drag_root_starts = roots.iter()
                            .filter_map(|&i| mc.instance_mats.get(i as usize).map(|&m| (i, m)))
                            .collect();
                        self.drag.drag_child_starts = mc.collect_non_root_descendants(&roots);
                    }
                    // 選択スロット以外の MC スロット開始行列を収集する
                    if let Some(dfs) = selected_dfs {
                        let mut c = 0u32;
                        if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c) {
                            self.drag.actor_extra_mc_drag_starts = actor.slots().iter()
                                .filter(|s| s.kind == ComponentKind::Model)
                                .enumerate()
                                .filter(|&(i, _)| i != selected_slot_i)
                                .filter_map(|(i, s)| {
                                    scene.world.get::<ModelComponent>(s.entity)
                                        .map(|mc| (i, mc.instance_mats.clone()))
                                })
                                .collect();
                        }
                    }
                    // 子孫アクタのドラッグ開始スナップショットを収集する
                    // （Model を持たない子＝カメラ等も含めて全件収集する）
                    if let Some(dfs) = selected_dfs {
                        let mut c = 0u32;
                        if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c) {
                            let mut child_dfs_counter = dfs as u32 + 1;
                            collect_child_actor_drag_starts(
                                actor, &scene.world,
                                &mut child_dfs_counter,
                                &mut self.drag.actor_child_drag_starts,
                            );
                        }
                    }
                    // MC なし（または空）のアクターは Transform を直接動かす
                    if self.drag.drag_root_starts.is_empty() {
                        if let Some(dfs) = selected_dfs {
                            let mut c = 0u32;
                            if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c) {
                                // actor.is_2d() でアクター個別の種別を判定する。
                                // canvas_world_lines.contains(&wl) は「世界線に2Dアクターが存在するか」のため、
                                // 3D/2D混在シーンでは3DアクターがCanvas扱いになって動かせなくなる。
                                // 開始時に両方クリアしてから新しい値をセットする（ステール防止）。
                                self.drag.canvas_transform_drag_start = None;
                                self.drag.actor_transform_drag_start  = None;
                                if actor.is_2d() {
                                    // 2D: CanvasTransform のスナップショットを保持する
                                    let old_ct = scene.world.get::<CanvasTransform>(actor.entity)
                                        .cloned().unwrap_or_default();
                                    self.drag.canvas_transform_drag_start = Some((dfs as u32, old_ct));
                                } else {
                                    let old_tf = scene.world.get::<ActorTransform>(actor.entity)
                                        .cloned().unwrap_or_default();
                                    self.drag.actor_transform_drag_start = Some((dfs as u32, old_tf));
                                }
                            }
                        }
                    }
                    // マルチ選択: プライマリ以外の選択アクターの開始行列を収集する
                    if self.selected_actor_dfs_ids.len() > 1 {
                        for &other_dfs in &self.selected_actor_dfs_ids {
                            if Some(other_dfs) == selected_dfs { continue; }
                            let mut c = 0u32;
                            if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, other_dfs as u32, &mut c) {
                                let start_mat = actor.mc_entity()
                                    .and_then(|e| scene.world.get::<ModelComponent>(e))
                                    .and_then(|mc| mc.instance_mats.first().copied())
                                    .unwrap_or_else(|| {
                                        scene.world.get::<ActorTransform>(actor.entity)
                                            .map(|tf| tf.to_mat4())
                                            .unwrap_or_default()
                                    });
                                self.drag.multi_actor_drag_starts.push((other_dfs as u32, start_mat));
                            }
                        }
                    }
        }
    }

    // ============================================================
    //  handle_lmb_release
    // ============================================================

    /// LMB 離し時: 矩形選択終了・クリックピック・ドラッグ Undo 記録。
    pub(super) fn handle_lmb_release(&mut self) {
        self.drag.lmb_held = false;

        if self.drag.rect_selecting {
            // 矩形選択終了: SelectionCommand と ActorDfsSelectionCommand を記録してエディタへ通知
            let before_inst = std::mem::take(&mut self.drag.selection_before_rect);
            let after_inst  = self.selected_instances.clone();
            if before_inst != after_inst {
                self.undo_history.record(Box::new(SelectionCommand { before: before_inst, after: after_inst }));
            }
            let before_dfs     = std::mem::take(&mut self.drag.selection_before_rect_dfs);
            let before_primary = self.drag.selection_before_rect_primary.take();
            let after_dfs      = self.selected_actor_dfs_ids.clone();
            let after_primary  = self.actor_virtual_selected_idx;
            if before_dfs != after_dfs || before_primary != after_primary {
                self.undo_history.record(Box::new(ActorDfsSelectionCommand {
                    before_dfs_ids: before_dfs,
                    after_dfs_ids:  after_dfs,
                    before_primary,
                    after_primary,
                }));
            }
            self.send_selected();
            // インスペクタへプライマリ選択アクターのコンポーネントをプッシュ
            if let Some(idx) = self.actor_virtual_selected_idx {
                self.send_actor_components(idx as u32, self.actor_virtual_selected_slot_idx);
            }
            self.drag.rect_selecting = false;
        } else if self.drag.gizmo_drag.is_none() && self.drag.lmb_press_pos.is_some()
            && !self.drag.control_point_picked
            && (self.mode == RuntimeMode::Edit || self.paused)
        {
            if let Some((cx, cy)) = self.last_cursor_pos {
                if self.actor_edit_canvas_wls.contains(&self.active_world_line)
                    || self.edit_view_is_2d()
                {
                    // 2D アクター編集/キャンバス編集タブ・2D シーンビュー（ビューポートタブ）:
                    // GPU ID パス不要。CPU 矩形ピック（Sprite/Canvas 対象・優先度巡回）を即時実行する。
                    self.pick_2d_canvas(cx, cy);
                } else {
                    // 3D シーンビュー（ワールドタブ）: GPU ID ピックをスケジュール
                    self.pending_pick = Some((cx as u32, cy as u32));
                }
            }
        }
        self.drag.lmb_press_pos = None;
        // 押下時の「制御点キューブを掴んだ」フラグはここで必ず落とす
        //（次のクリックへ持ち越すと通常のピックが永久に効かなくなる）。
        self.drag.control_point_picked = false;

        // コントロールポイントのドラッグ終了: Undo を 1 件だけ記録して抜ける。
        // 以降のアクタ／キャンバス向け終了処理は、対象が違うので通らせない。
        if self.finish_control_point_drag() {
            self.drag.gizmo_drag = None;
            // 通常経路の末尾と同じくホバーを再評価してから抜ける
            //（掴んだ直後にギズモのハイライトが取り残されないように）。
            self.hovered_gizmo_part = self.last_cursor_pos
                .and_then(|(cx, cy)| self.compute_gizmo_hover(cx, cy));
            return;
        }

        // ドラッグで変化があれば Undo 履歴に一括記録する
        self.finish_gizmo_drag_and_record();
    }

    // ============================================================
    //  finish_gizmo_drag_and_record
    // ============================================================

    /// 進行中のギズモドラッグを終了し、変化があれば Undo 履歴へ
    /// **1 エントリだけ**記録する（複数選択は CompositeCommand で束ねる）。
    ///
    /// **モーダルトランスフォームの確定もこの関数を呼ぶ**ため、
    /// 記録方式（1 操作 = Undo 1 件）はギズモドラッグと完全に一致する。
    pub(super) fn finish_gizmo_drag_and_record(&mut self) {
        let mut primary_recorded = false;
        if self.drag.gizmo_drag.is_some() {
            // CanvasTransform ドラッグ終了処理: 必ず take() してステール状態を防ぐ。
            // canvas_transform_drag_start が take() されないまま残ると、次の 3D アクター
            // ドラッグ時に canvas 用パスが誤って選択されてしまう原因になる。
            if let Some((canvas_drag_dfs, old_ct)) = self.drag.canvas_transform_drag_start.take() {
                let wl = self.active_world_line;
                let new_ct_opt = self.scene.as_ref().and_then(|s| {
                    let mut c = 0u32;
                    find_actor_by_dfs(&s.actors, wl, canvas_drag_dfs, &mut c)
                        .and_then(|a| s.world.get::<crate::engine::components::CanvasTransform>(a.entity).cloned())
                });
                if let Some(new_ct) = new_ct_opt {
                    if old_ct != new_ct {
                        self.undo_history.record(Box::new(CanvasTransformCommand {
                            world_line: wl,
                            dfs_id:     canvas_drag_dfs,
                            old_ct,
                            new_ct,
                        }));
                        primary_recorded = true;
                        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                    }
                }
                self.send_actor_components(canvas_drag_dfs, self.actor_virtual_selected_slot_idx);
            }

            if let Some((dfs_id, old_transform)) = self.drag.actor_transform_drag_start.take() {
                // アクター編集モード: MC なしアクターの Transform ドラッグ終了
                let wl = self.active_world_line;
                let child_drag_starts = std::mem::take(&mut self.drag.actor_child_drag_starts);
                let new_transform_opt = self.scene.as_ref().and_then(|s| {
                    let mut c = 0u32;
                    find_actor_by_dfs(&s.actors, wl, dfs_id, &mut c)
                        .and_then(|a| s.world.get::<ActorTransform>(a.entity).cloned())
                });
                if let Some(new_transform) = new_transform_opt {
                    let delta = mat4x4_mul(new_transform.to_mat4(), mat4x4_inv(old_transform.to_mat4()));
                    let mut child_transforms: Vec<(u32, ActorTransform, ActorTransform, [[f32;4];4], [[f32;4];4])> = Vec::new();
                    for cs in child_drag_starts {
                        if let Some(scene) = &mut self.scene {
                            // MC はスロット専用 entity に格納されるため、アクタの entity ではなく
                            // mc_entity() で解決する（旧実装はアクタ entity を見ていて常に None だった）。
                            let (child_entity, mc_slot_entity) = {
                                let mut c = 0u32;
                                find_actor_by_dfs(&scene.actors, wl, cs.dfs_id, &mut c)
                                    .map(|a| (Some(a.entity), a.mc_entity()))
                                    .unwrap_or((None, None))
                            };
                            if let Some(child_entity) = child_entity {
                                // ドラッグ中に Transform は更新済みなので、開始行列から旧値を復元する
                                let old_child_tf = ActorTransform::from_mat4(&cs.tf_start);
                                let new_child_tf = ActorTransform::from_mat4(&mat4x4_mul(delta, cs.tf_start));
                                if scene.world.get::<ActorTransform>(child_entity).is_some() {
                                    if let Some(tf) = scene.world.get_mut::<ActorTransform>(child_entity) {
                                        *tf = new_child_tf.clone();
                                    }
                                }
                                // MC 行列は MC 開始行列基準で確定させる（Model なしの子は None）
                                let old_mc_mat = cs.mc_start.unwrap_or(super::MAT4_IDENTITY);
                                let new_mc_mat = cs.mc_start
                                    .map(|s| mat4x4_mul(delta, s))
                                    .unwrap_or(super::MAT4_IDENTITY);
                                if let (Some(e), Some(_)) = (mc_slot_entity, cs.mc_start) {
                                    if let Some(mc) = scene.world.get_mut::<ModelComponent>(e) {
                                        if let Some(m) = mc.instance_mats.first_mut() { *m = new_mc_mat; }
                                        mc.mark_batch_dirty();
                                    }
                                }
                                child_transforms.push((cs.dfs_id, old_child_tf, new_child_tf, old_mc_mat, new_mc_mat));
                            }
                        }
                    }
                    if old_transform != new_transform || !child_transforms.is_empty() {
                        self.undo_history.record(Box::new(ActorGroupTransformCommand {
                            wl, dfs_id,
                            old_tf: old_transform,
                            new_tf: new_transform,
                            transforms: vec![],
                            child_transforms,
                            extra_slot_transforms: vec![],
                        }));
                        primary_recorded = true;
                        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                    }
                }
                self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
            } else {
                let mut transforms: Vec<(u32, [[f32;4];4], [[f32;4];4])> = Vec::new();
                let root_starts       = std::mem::take(&mut self.drag.drag_root_starts);
                let child_starts      = std::mem::take(&mut self.drag.drag_child_starts);
                let extra_mc_starts   = std::mem::take(&mut self.drag.actor_extra_mc_drag_starts);
                let wl_end            = self.active_world_line;
                let selected_dfs_end  = self.actor_virtual_selected_idx;
                let selected_slot_i_e = self.actor_virtual_selected_slot_idx;
                let mc_entity: Option<crate::engine::ecs::Entity> = self.scene.as_ref().and_then(|s| {
                    selected_dfs_end.and_then(|dfs| {
                        let mut c = 0u32;
                        find_actor_by_dfs(&s.actors, wl_end, dfs as u32, &mut c)
                            .and_then(|a| a.mc_entity_at(selected_slot_i_e))
                    })
                });
                if let Some(mc) = mc_entity.and_then(|e| self.scene.as_ref()?.world.get::<ModelComponent>(e)) {
                    for (idx, old_mat) in root_starts.into_iter().chain(child_starts) {
                        if let Some(&new_mat) = mc.instance_mats.get(idx as usize) {
                            if new_mat != old_mat {
                                transforms.push((idx, old_mat, new_mat));
                            }
                        }
                    }
                }
                // 追加 MC スロットの old→new 変換を収集する（Undo 用）
                let extra_slot_transforms: Vec<(usize, u32, [[f32;4];4], [[f32;4];4])> =
                    extra_mc_starts.iter().flat_map(|(slot_i, start_mats)| {
                        let cur_mats: Vec<[[f32;4];4]> = selected_dfs_end.and_then(|dfs| {
                            let mut c = 0u32;
                            self.scene.as_ref().and_then(|s| {
                                find_actor_by_dfs(&s.actors, wl_end, dfs as u32, &mut c)
                                    .and_then(|a| a.mc_entity_at(*slot_i))
                                    .and_then(|e| s.world.get::<ModelComponent>(e))
                                    .map(|mc| mc.instance_mats.clone())
                            })
                        }).unwrap_or_default();
                        start_mats.iter().zip(cur_mats.iter()).enumerate()
                            .filter_map(|(i, (old, &new))| {
                                if *old != new { Some((*slot_i, i as u32, *old, new)) } else { None }
                            })
                            .collect::<Vec<_>>()
                    }).collect();
                let wl = self.active_world_line;
                if self.actor_virtual_selected_idx.is_some() {
                    // 仮想ノード選択中: delta を Transform と子アクターに反映して ActorGroupTransformCommand で記録
                    let dfs_id = self.actor_virtual_selected_idx.unwrap() as u32;
                    let child_drag_starts = std::mem::take(&mut self.drag.actor_child_drag_starts);
                    let (old_tf, new_tf, child_transforms) = if let Some(&(_, old_mat, new_mat)) = transforms.first() {
                        let delta = mat4x4_mul(new_mat, mat4x4_inv(old_mat));
                        // ドラッグ中に ActorTransform はリアルタイム更新済みのため、
                        // 「ドラッグ前の AT」は MC の開始行列 (old_mat) から復元する。
                        // 現在の AT から delta を掛けると 2 重に移動してしまうため使用しない。
                        let old_v = ActorTransform::from_mat4(&old_mat);
                        let new_v = ActorTransform::from_mat4(&new_mat);
                        if let Some(scene) = &mut self.scene {
                            let entity = {
                                let mut c = 0u32;
                                find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c)
                                    .map(|a| a.entity)
                            };
                            // AT はドラッグ中に更新済みだが念のため最終値を再セットする
                            if let Some(entity) = entity {
                                if let Some(tf) = scene.world.get_mut::<ActorTransform>(entity) { *tf = new_v.clone(); }
                            }
                        }
                        // 子孫アクターの Transform を確定し Undo 用データを収集する
                        // （Model を持たない子＝カメラ等も Transform だけ確定させる）
                        let mut child_transforms = Vec::new();
                        for cs in child_drag_starts {
                            if let Some(scene) = &mut self.scene {
                                let (child_entity_opt, mc_slot_entity) = {
                                    let mut c = 0u32;
                                    find_actor_by_dfs(&scene.actors, wl, cs.dfs_id, &mut c)
                                        .map(|a| (Some(a.entity), a.mc_entity()))
                                        .unwrap_or((None, None))
                                };
                                if let Some(child_entity) = child_entity_opt {
                                    // 子アクターも Transform がドラッグ中更新済みのため
                                    // tf_start（Transform 由来の開始行列）から old を復元する
                                    let old_child_tf = ActorTransform::from_mat4(&cs.tf_start);
                                    let new_child_tf = ActorTransform::from_mat4(
                                        &mat4x4_mul(delta, cs.tf_start));
                                    if scene.world.get::<ActorTransform>(child_entity).is_some() {
                                        if let Some(tf) = scene.world.get_mut::<ActorTransform>(child_entity) {
                                            *tf = new_child_tf.clone();
                                        }
                                    }
                                    // MC 行列は MC 開始行列基準（Model なしの子は単位行列＝Undo 側で no-op）
                                    let old_mc_mat = cs.mc_start.unwrap_or(super::MAT4_IDENTITY);
                                    let new_mc_mat = match (mc_slot_entity, cs.mc_start) {
                                        (Some(e), Some(_)) => scene.world.get::<ModelComponent>(e)
                                            .and_then(|mc| mc.instance_mats.first().copied())
                                            .unwrap_or(old_mc_mat),
                                        _ => old_mc_mat,
                                    };
                                    child_transforms.push((cs.dfs_id, old_child_tf, new_child_tf, old_mc_mat, new_mc_mat));
                                }
                            }
                        }
                        (old_v, new_v, child_transforms)
                    } else {
                        // インスタンス変化なし: 現在の Transform を取得
                        let tf = self.scene.as_ref().and_then(|s| {
                            let mut c = 0u32;
                            find_actor_by_dfs(&s.actors, wl, dfs_id, &mut c)
                                .and_then(|a| s.world.get::<ActorTransform>(a.entity).cloned())
                        }).unwrap_or_default();
                        (tf.clone(), tf, Vec::new())
                    };
                    self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                    if !transforms.is_empty() || !extra_slot_transforms.is_empty() {
                        self.undo_history.record(Box::new(ActorGroupTransformCommand {
                            wl, dfs_id, old_tf, new_tf, transforms,
                            child_transforms, extra_slot_transforms,
                        }));
                        primary_recorded = true;
                        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                    }
                } else {
                    if !transforms.is_empty() {
                        self.undo_history.record(Box::new(MultiTransformCommand { transforms }));
                        primary_recorded = true;
                        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                    }
                    if self.selected_instances.len() == 1 {
                        self.send_actor_data(self.selected_instances[0]);
                    }
                }
            }
        } else {
            self.drag.actor_transform_drag_start = None;
            self.drag.drag_root_starts.clear();
            self.drag.drag_child_starts.clear();
            self.drag.actor_child_drag_starts.clear();
            self.drag.actor_extra_mc_drag_starts.clear();
        }

        // マルチ選択ドラッグ終了: 非プライマリアクターの Transform を記録する
        if !self.drag.multi_actor_drag_starts.is_empty() {
            let wl = self.active_world_line;
            let mut drag_transforms: Vec<(u32, [[f32; 4]; 4], [[f32; 4]; 4])> = Vec::new();
            if let Some(scene) = self.scene.as_ref() {
                for &(other_dfs, old_mat) in &self.drag.multi_actor_drag_starts {
                    let mut c = 0u32;
                    let new_mat = find_actor_by_dfs(&scene.actors, wl, other_dfs, &mut c)
                        .and_then(|a| a.mc_entity()
                            .and_then(|e| scene.world.get::<ModelComponent>(e))
                            .and_then(|mc| mc.instance_mats.first().copied())
                            .or_else(|| scene.world.get::<ActorTransform>(a.entity)
                                .map(|tf| tf.to_mat4())))
                        .unwrap_or(old_mat);
                    drag_transforms.push((other_dfs, old_mat, new_mat));
                }
            }
            if !drag_transforms.is_empty() {
                let multi_cmd: Box<dyn crate::engine::core::app_base::undo::Command> =
                    Box::new(MultiActorDragTransformCommand { wl, transforms: drag_transforms });
                if primary_recorded {
                    // プライマリのコマンドを取り出して CompositeCommand に統合する
                    if let Some(primary_cmd) = self.undo_history.pop_last() {
                        self.undo_history.record(Box::new(CompositeCommand {
                            commands: vec![primary_cmd, multi_cmd],
                        }));
                    } else {
                        self.undo_history.record(multi_cmd);
                    }
                } else {
                    self.undo_history.record(multi_cmd);
                }
                if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
            }
            self.drag.multi_actor_drag_starts.clear();
        }
        self.drag.gizmo_drag = None;
        // ドラッグ終了後はホバーを再評価する
        self.hovered_gizmo_part = self.last_cursor_pos
            .and_then(|(cx, cy)| self.compute_gizmo_hover(cx, cy));
    }
}
