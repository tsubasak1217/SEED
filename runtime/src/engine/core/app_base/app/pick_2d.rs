// ============================================================
//  pick_2d.rs — 2D キャンバスアクター CPU ピッキング
//
//  GPU ID パスを使わず、カーソル位置をキャンバス空間に変換し
//  各アクターの OBB（向き付き境界ボックス）とのヒットテストを
//  CPU 上で行う。Sprite コンポーネントがキャンバスより優先される。
// ============================================================

use crate::engine::components::{
    CanvasTransform, CanvasComponent, SpriteComponent, ComponentKind,
};
// CanvasComponent は子への継承計算で引き続き使用する
use crate::engine::core::app_base::undo::ActorDfsSelectionCommand;
use crate::engine::structs::objects::Actor;
use crate::engine::ecs::World;
use crate::engine::methods::gizmo_interact::mat4x4_mul;

use super::App;

impl App {
    /// 2D キャンバス空間でクリックされたアクターを CPU OBB ヒットテストで選択する。
    ///
    /// `cx`, `cy` はウィンドウスクリーン座標（左上原点、Y-down）。
    /// Sprite コンポーネントが Canvas より優先される。
    /// DFS 順で最後にヒットしたアクター（最前面）が選択される。
    pub(super) fn pick_2d_canvas(&mut self, cx: f32, cy: f32) {
        let wl         = self.active_world_line;
        let win_size   = self.window.as_ref().map(|w| w.inner_size());
        let vp_w       = win_size.map_or(1280.0, |s| s.width  as f32);
        let vp_h       = win_size.map_or(720.0,  |s| s.height as f32);

        // スクリーン座標 → キャンバス空間座標
        // 2D ortho カメラ: pan + NDC * half_size
        let cam_2d  = self.canvas_cameras.get(&wl).cloned().unwrap_or_default();
        let half_h  = cam_2d.ortho_half_h;
        let half_w  = half_h * (vp_w / vp_h);
        let ndx     = 2.0 * cx / vp_w - 1.0;
        let ndy     = 2.0 * cy / vp_h - 1.0; // Y-down
        let canvas_x = cam_2d.pan_x + ndx * half_w;
        let canvas_y = cam_2d.pan_y + ndy * half_h;

        // アクターツリーを DFS ウォークしてヒットを収集する
        let Some(scene) = &self.scene else { return };
        let actors = &scene.actors;
        let world  = &scene.world;

        let mut counter: u32     = 0;
        let mut hit: Option<usize> = None;
        const IDENTITY: [[f32; 4]; 4] = [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        walk_pick_2d(
            actors, world, wl,
            canvas_x, canvas_y, &mut counter,
            IDENTITY, [1.0, 1.0], (false, false), None,
            &mut hit,
        );

        // 選択変更前の状態を保存する（Undo 記録用）
        let before_dfs_ids = self.selected_actor_dfs_ids.clone();
        let before_primary = self.actor_virtual_selected_idx;

        if let Some(dfs_id) = hit {
            // ヒット: アクターを選択する
            if self.drag.ctrl_at_press {
                // Ctrl+クリック: マルチ選択トグル
                if self.selected_actor_dfs_ids.contains(&dfs_id) {
                    self.selected_actor_dfs_ids.retain(|&x| x != dfs_id);
                    if self.actor_virtual_selected_idx == Some(dfs_id) {
                        self.actor_virtual_selected_idx = self.selected_actor_dfs_ids.last().copied();
                    }
                } else {
                    self.selected_actor_dfs_ids.push(dfs_id);
                    self.actor_virtual_selected_idx = Some(dfs_id);
                }
            } else {
                // 通常クリック: 単一選択
                self.actor_virtual_selected_idx = Some(dfs_id);
                self.selected_actor_dfs_ids     = vec![dfs_id];
            }
            self.selected_instances.clear();
            self.send_actor_components(dfs_id as u32, 0);
        } else if !self.drag.ctrl_at_press {
            // 空クリック: 選択解除
            self.actor_virtual_selected_idx = None;
            self.selected_actor_dfs_ids.clear();
            self.selected_instances.clear();
        }

        // アクター DFS 選択の Undo 記録
        let after_dfs_ids = self.selected_actor_dfs_ids.clone();
        let after_primary = self.actor_virtual_selected_idx;
        if before_dfs_ids != after_dfs_ids || before_primary != after_primary {
            self.undo_history.record(Box::new(ActorDfsSelectionCommand {
                before_dfs_ids,
                after_dfs_ids,
                before_primary,
                after_primary,
            }));
        }
        self.send_selected();
    }
}

// ── ヘルパー関数 ────────────────────────────────────────────────

/// キャンバス空間の点 (px, py) が、行列 m で定義された
/// ローカル空間 [0, eff_w] × [0, eff_h] の矩形内にあるか判定する。
///
/// m[0][3], m[1][3] が平行移動成分、2×2 部分が回転×スケールを表す。
/// 逆行列をクラメールの公式で解き、ローカル座標が範囲内か確認する。
fn hit_test_rect_2d(px: f32, py: f32, m: &[[f32; 4]; 4], eff_w: f32, eff_h: f32) -> bool {
    // 2×2 回転スケール行列の行列式
    let det = m[0][0] * m[1][1] - m[0][1] * m[1][0];
    if det.abs() < 1e-9 { return false; } // 面積 0 の退化矩形は無視する
    let dx = px - m[0][3];
    let dy = py - m[1][3];
    // クラメールの公式で逆変換
    let lx = (dx * m[1][1] - dy * m[0][1]) / det;
    let ly = (m[0][0] * dy - m[1][0] * dx) / det;
    lx >= 0.0 && lx <= eff_w && ly >= 0.0 && ly <= eff_h
}

/// アクターツリーを DFS ウォークし、クリック点に当たる最後のアクター DFS ID を返す。
///
/// collect_canvas_rects と同じ DFS カウンタ・累積スケールロジックを使用する。
/// Sprite ヒットを Canvas ヒットより優先する（同一アクター内の判定）。
/// DFS 順で最後にヒットしたアクターが画面最前面として選択される。
#[allow(clippy::too_many_arguments)]
fn walk_pick_2d(
    actors:             &[Actor],
    world:              &World,
    wl:                 u32,
    canvas_x:           f32,
    canvas_y:           f32,
    counter:            &mut u32,
    parent_world_rs:    [[f32; 4]; 4],
    parent_cumul_scale: [f32; 2],
    parent_scale_mode:  (bool, bool),
    parent_canvas_size: Option<[f32; 2]>,
    hit:                &mut Option<usize>,
) {
    let (sm_transform, sm_size) = parent_scale_mode;

    for actor in actors {
        if actor.world_line != wl { continue; }
        let my_dfs = *counter as usize;
        *counter += 1;

        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
        if let Some(ct) = ct_opt {
            // アンカーオフセット（collect_canvas_rects と同じロジック）
            let (anchor_off_x, anchor_off_y) = if parent_canvas_size.is_none() {
                (0.0f32, 0.0f32)
            } else {
                (parent_canvas_size.map_or(0.0, |[pw, _]| pw * ct.anchor[0] * parent_cumul_scale[0]),
                 parent_canvas_size.map_or(0.0, |[_, ph]| ph * ct.anchor[1] * parent_cumul_scale[1]))
            };

            // 有効位置（スケールモードに応じて親累積スケールを適用する）
            let eff_pos = if sm_transform {
                [ct.position[0] * parent_cumul_scale[0] + anchor_off_x,
                 ct.position[1] * parent_cumul_scale[1] + anchor_off_y]
            } else {
                [ct.position[0] + anchor_off_x,
                 ct.position[1] + anchor_off_y]
            };
            let eff_ct = CanvasTransform {
                position: eff_pos,
                rotation: ct.rotation,
                scale:    ct.scale,
                pivot:    ct.pivot,
                anchor:   [0.0, 0.0],
            };

            // キャンバスコンポーネントの有効サイズ（sm_size に応じて累積スケールを適用する）
            let my_canvas_r = actor.slots().iter()
                .filter(|s| s.kind == ComponentKind::Canvas)
                .find_map(|s| world.get::<CanvasComponent>(s.entity));
            let (my_eff_w_r, my_eff_h_r) = my_canvas_r.map(|cc| (
                cc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 },
                cc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 },
            )).unwrap_or((1.0, 1.0));

            // 子への親ワールド RS 行列（自身の累積スケールなし版で渡す）
            let self_world_rs = mat4x4_mul(
                parent_world_rs,
                CanvasTransform { scale: [1.0, 1.0], ..eff_ct.clone() }
                    .to_mat4_sized(my_eff_w_r, my_eff_h_r),
            );

            // SpriteComponent を持つアクターの OBB ヒットテストを行う。
            // テクスチャなし（単色）も含めて全 Sprite を選択対象にする。
            // Canvas のみ（Sprite スロットなし）は選択対象外（空白領域とみなす）。
            let mut sprite_hit = false;
            for slot in actor.slots() {
                if slot.kind == ComponentKind::Sprite {
                    if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                        let eff_w = sc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 };
                        let eff_h = sc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 };
                        let m = mat4x4_mul(parent_world_rs, eff_ct.to_mat4_sized(eff_w, eff_h));
                        if hit_test_rect_2d(canvas_x, canvas_y, &m, eff_w, eff_h) {
                            sprite_hit = true;
                        }
                    }
                }
            }

            if sprite_hit {
                *hit = Some(my_dfs);
            }

            // 子アクターへの累積スケールを計算して再帰する（collect_canvas_rects と同じロジック）
            let child_info = my_canvas_r
                .map(|cc| ([cc.width, cc.height], (cc.scale_transform, cc.scale_size)));
            let child_canvas_size = child_info.map(|(sz, _)| sz);
            let child_scale_mode  = child_info.map(|(_, sm)| sm).unwrap_or((false, false));
            let child_cumul_scale = if child_scale_mode.0 {
                [parent_cumul_scale[0] * ct.scale[0],
                 parent_cumul_scale[1] * ct.scale[1]]
            } else {
                [ct.scale[0], ct.scale[1]]
            };

            walk_pick_2d(
                &actor.children, world, wl,
                canvas_x, canvas_y, counter,
                self_world_rs, child_cumul_scale, child_scale_mode,
                child_canvas_size, hit,
            );
        }
        // CanvasTransform なし: DFS カウンタは進めたが子は skip する（collect_canvas_rects と同様）
    }
}
