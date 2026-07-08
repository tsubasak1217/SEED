// ============================================================
//  pick_2d.rs — 2D キャンバスアクター CPU ピッキング
//
//  GPU ID パスを使わず、カーソル位置をキャンバス空間（ortho）に変換し
//  各アクターの Sprite / Canvas 矩形（向き付き境界ボックス）との
//  ヒットテストを CPU 上で行う。表示（collect_canvas_rects /
//  collect_sprite_items）と完全に同一の変換チェーン（design_space・
//  自動解像度上書き・eff_viewport・スケールモード）を使うことで、
//  「見た目の矩形」と「クリック判定」を一致させる。
//
//  【選択優先度（重なり時）】
//  1. Sprite（表示物）を Canvas より最優先（canvas は補助）
//  2. 描画ゾーン: 前面（Foreground）を背面（Background）より優先
//  3. 優先度が同じなら子アクタ（深い階層）を優先
//  4. さらに同じなら DFS 順で最前面（後に描画される方）を優先
//  同一地点を連続クリックすると次の優先度の候補へ巡回する。
// ============================================================

use std::collections::HashMap;

use crate::engine::components::{
    CanvasTransform, CanvasComponent, SpriteComponent, ComponentKind,
    CanvasDrawZone, AspectRatioAxis, Transform,
};
use crate::engine::core::app_base::undo::ActorDfsSelectionCommand;
use crate::engine::structs::objects::Actor;
use crate::engine::ecs::{Entity, World};
use crate::engine::methods::gizmo_interact::{mat4x4_mul, screen_to_ray};

use super::App;
use super::canvas_collect::{root_anchor_offset, skip_dfs_subtree};

/// 巡回選択で「同一地点クリック」とみなすスクリーン座標の許容誤差（ピクセル）。
const PICK_CYCLE_TOLERANCE_PX: f32 = 4.0;

/// キャンバス座標（ピクセル）→ 3D ワールド座標の変換スケール（mod.rs の CANVAS_WORLD_SCALE と同値）。
const CANVAS_WORLD_SCALE: f32 = 1.0 / 100.0;

/// ピック候補の種別（優先度: Sprite > Canvas）。
#[derive(Clone, Copy, PartialEq, Eq)]
enum PickKind2d {
    Sprite,
    Canvas,
}

/// クリック点に当たった 2D ピック候補。優先度ソート用のメタ情報を持つ。
struct PickCand2d {
    /// アクター DFS ID（選択に使用）
    dfs:   usize,
    /// Sprite / Canvas 種別（Sprite 最優先）
    kind:  PickKind2d,
    /// 描画ゾーン（前面優先）
    zone:  CanvasDrawZone,
    /// 階層の深さ（大きいほど子＝優先）
    depth: u32,
}

impl App {
    /// 2D キャンバス空間でクリックされたアクターを CPU 矩形ヒットテストで選択する。
    ///
    /// `cx`, `cy` はウィンドウスクリーン座標（左上原点、Y-down）。
    /// Sprite が Canvas より優先され、重なり時は描画ゾーン→子優先の順で並ぶ。
    /// 同一地点を連続クリックすると次の候補へ巡回選択する。
    pub(super) fn pick_2d_canvas(&mut self, cx: f32, cy: f32) {
        let wl       = self.active_world_line;
        let win_size = self.window.as_ref().map(|w| w.inner_size());
        let vp_w     = win_size.map_or(1280.0, |s| s.width  as f32);
        let vp_h     = win_size.map_or(720.0,  |s| s.height as f32);

        // スクリーン座標 → キャンバス空間（ortho）座標
        // 2D ortho カメラ: pan + NDC * half_size
        let cam_2d   = self.canvas_cameras.get(&wl).cloned().unwrap_or_default();
        let half_h   = cam_2d.ortho_half_h;
        let half_w   = half_h * (vp_w / vp_h);
        let ndx      = 2.0 * cx / vp_w - 1.0;
        let ndy      = 2.0 * cy / vp_h - 1.0; // Y-down
        let canvas_x = cam_2d.pan_x + ndx * half_w;
        let canvas_y = cam_2d.pan_y + ndy * half_h;

        // ── 表示と同一条件のレイアウトマップ・フラグを構築する ────────────────
        // アクター編集/キャンバス編集タブ（actor_edit_canvas_wls）:
        //   ビューポート基準なし（viewport_size=None）・自動解像度なし・design_space=false。
        // シーンのビューポートタブ（2D シーンビュー）:
        //   ビューポート基準あり・自動解像度あり・design_space=edit_view_is_2d()。
        let is_actor_edit = self.actor_edit_canvas_wls.contains(&wl);
        let design_space  = self.edit_view_is_2d();

        // 候補を収集する（scene を読み取り専用で借用して完結させる）
        let mut cands: Vec<PickCand2d> = Vec::new();
        if let Some(scene) = self.scene.as_ref() {
            // アクター編集/キャンバス編集タブはビューポート基準・自動解像度なし。
            // 2D シーンビューは表示と同一のレイアウトマップを構築する。
            let (viewport_size, overrides, root_auto) = if is_actor_edit {
                (None, HashMap::new(), HashMap::new())
            } else {
                let (ov, ra) = self.build_ss_layout_maps(
                    &scene.actors, &scene.world, wl, vp_w, vp_h, None,
                );
                (Some([vp_w, vp_h]), ov, ra)
            };

            const IDENTITY: [[f32; 4]; 4] = [
                [1.0, 0.0, 0.0, 0.0],
                [0.0, 1.0, 0.0, 0.0],
                [0.0, 0.0, 1.0, 0.0],
                [0.0, 0.0, 0.0, 1.0],
            ];
            let mut counter: u32 = 0;
            walk_pick_candidates_2d(
                &scene.actors, &scene.world, wl,
                canvas_x, canvas_y, &mut counter,
                IDENTITY, [1.0, 1.0], (false, false, false, true), None,
                0, CanvasDrawZone::Foreground,
                viewport_size, &overrides, &root_auto, design_space,
                &mut cands,
            );
        }

        // ── 優先度順にソートする（index 0 = 既定で選択される最優先候補）────────
        //   1. Sprite < Canvas         （Sprite 最優先）
        //   2. Foreground < Background  （前面優先）
        //   3. depth 降順              （子＝深い階層を優先）
        //   4. dfs 降順                （DFS 後方＝最前面を優先）
        cands.sort_by(|a, b| {
            let ka = (kind_rank(a.kind), zone_rank(a.zone));
            let kb = (kind_rank(b.kind), zone_rank(b.zone));
            ka.cmp(&kb)
                .then(b.depth.cmp(&a.depth))
                .then(b.dfs.cmp(&a.dfs))
        });
        let sorted_dfs: Vec<usize> = cands.iter().map(|c| c.dfs).collect();

        // ── 巡回選択: 同一地点＆同一候補列の連続クリックで次候補へ回す ──────────
        let chosen: Option<usize> = if sorted_dfs.is_empty() {
            self.pick_2d_cycle = None;
            None
        } else {
            let cycle_index = match &self.pick_2d_cycle {
                Some(([px, py], prev_list, prev_idx))
                    if (px - cx).abs() <= PICK_CYCLE_TOLERANCE_PX
                        && (py - cy).abs() <= PICK_CYCLE_TOLERANCE_PX
                        && *prev_list == sorted_dfs =>
                {
                    (prev_idx + 1) % sorted_dfs.len()
                }
                _ => 0,
            };
            self.pick_2d_cycle = Some(([cx, cy], sorted_dfs.clone(), cycle_index));
            Some(sorted_dfs[cycle_index])
        };

        // ── 選択状態を更新する（Undo 記録付き）───────────────────────────────
        let before_dfs_ids = self.selected_actor_dfs_ids.clone();
        let before_primary = self.actor_virtual_selected_idx;

        if let Some(dfs_id) = chosen {
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

// ── 優先度ランク（小さいほど優先）─────────────────────────────────────────────

/// 種別ランク: Sprite（表示物）を最優先、Canvas は補助。
fn kind_rank(k: PickKind2d) -> u8 {
    match k {
        PickKind2d::Sprite => 0,
        PickKind2d::Canvas => 1,
    }
}

/// 描画ゾーンランク: 前面（Foreground）を背面（Background）より優先。
fn zone_rank(z: CanvasDrawZone) -> u8 {
    match z {
        CanvasDrawZone::Foreground => 0,
        CanvasDrawZone::Background => 1,
    }
}

// ── ヒットテスト ─────────────────────────────────────────────────────────────

/// キャンバス空間の点 (px, py) が、行列 m で定義された
/// ローカル空間 [0, eff_w] × [0, eff_h] の矩形内にあるか判定する。
///
/// m[0][3], m[1][3] が平行移動成分、2×2 部分が回転×スケールを表す。
/// 逆行列をクラメールの公式で解き、ローカル座標が範囲内か確認する。
/// canvas_drop.rs のドロップ先キャンバスヒット判定でも共用するため pub(super)。
pub(super) fn hit_test_rect_2d(px: f32, py: f32, m: &[[f32; 4]; 4], eff_w: f32, eff_h: f32) -> bool {
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

// ── 候補収集ウォーク（collect_canvas_rects と同一の変換チェーン）──────────────

/// アクターツリーを DFS ウォークし、クリック点に当たる Sprite/Canvas 候補を集める。
///
/// collect_canvas_rects / collect_sprite_items と完全に同じ変換チェーン
/// （design_space・自動解像度上書き・eff_viewport・スケールモード）を使うことで、
/// 描画される矩形とヒット判定を一致させる。SS ピック専用のため canvas_scale=1・
/// y_sign=1（ortho 空間）で計算する。
#[allow(clippy::too_many_arguments)]
fn walk_pick_candidates_2d(
    actors:             &[Actor],
    world:              &World,
    wl:                 u32,
    canvas_x:           f32,
    canvas_y:           f32,
    counter:            &mut u32,
    parent_world_rs:    [[f32; 4]; 4],
    parent_cumul_scale: [f32; 2],
    parent_scale_mode:  (bool, bool, bool, bool),
    parent_canvas_size: Option<[f32; 2]>,
    depth:              u32,
    parent_zone:        CanvasDrawZone,
    viewport_size:      Option<[f32; 2]>,
    overrides:          &HashMap<Entity, [f32; 2]>,
    root_auto_sizes:    &HashMap<Entity, [f32; 2]>,
    design_space:       bool,
    out:                &mut Vec<PickCand2d>,
) {
    let (sm_transform, sm_size, keep_aspect, is_width_axis) = parent_scale_mode;

    for actor in actors {
        if actor.world_line != wl { continue; }
        let my_dfs = *counter as usize;
        *counter += 1;

        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
        let Some(ct) = ct_opt else {
            // CanvasTransform なし（Actor3D 等）: ヒット対象外だが、DFS 番号は
            // find_actor_by_dfs と同じ規則（子孫も含めて全カウント）で消費する。
            // ここで子孫の番号を消費しないと以降の DFS ID がズレて、
            // ビューポートタブのクリックでワールドの別アクターが選択されてしまう。
            skip_dfs_subtree(&actor.children, counter);
            continue;
        };

        // ビューポート・ルートキャンバス: 自動解像度上書き + Transform 恒等化
        let root_auto = if parent_canvas_size.is_none() {
            root_auto_sizes.get(&actor.entity).copied()
        } else {
            None
        };
        let ct = if root_auto.is_some() { CanvasTransform::default() } else { ct };

        // アンカーオフセット（collect_canvas_rects と同一。Camera 参照を優先）
        let eff_viewport = if parent_canvas_size.is_none() {
            overrides.get(&actor.entity).copied().or(viewport_size)
        } else {
            viewport_size
        };
        let (anchor_off_x, anchor_off_y) = if parent_canvas_size.is_none() {
            if let Some([vw, vh]) = eff_viewport {
                let [ox, oy] = root_anchor_offset(ct.anchor, vw, vh, design_space);
                (ox, oy)
            } else {
                (0.0, 0.0)
            }
        } else {
            (parent_canvas_size.map_or(0.0, |[pw, _]| pw * ct.anchor[0] * parent_cumul_scale[0]),
             parent_canvas_size.map_or(0.0, |[_, ph]| ph * ct.anchor[1] * parent_cumul_scale[1]))
        };

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

        let my_canvas = actor.slots().iter()
            .filter(|s| s.kind == ComponentKind::Canvas)
            .find_map(|s| world.get::<CanvasComponent>(s.entity));

        // 描画ゾーン（ルートは自身、子は親から継承）
        let my_zone = if parent_canvas_size.is_none() {
            my_canvas.map(|cc| cc.draw_zone).unwrap_or(parent_zone)
        } else {
            parent_zone
        };

        // スケールモードに応じた有効サイズ係数（アスペクト比維持を考慮）
        let size_sc_x = if sm_size { if keep_aspect && !is_width_axis { parent_cumul_scale[1] } else { parent_cumul_scale[0] } } else { 1.0 };
        let size_sc_y = if sm_size { if keep_aspect && is_width_axis  { parent_cumul_scale[0] } else { parent_cumul_scale[1] } } else { 1.0 };
        let (my_eff_w, my_eff_h) = my_canvas.map(|cc| {
            let [bw, bh] = root_auto.unwrap_or([cc.width, cc.height]);
            (bw * size_sc_x, bh * size_sc_y)
        }).unwrap_or((1.0, 1.0));

        let self_world_rs = mat4x4_mul(
            parent_world_rs,
            CanvasTransform { scale: [1.0, 1.0], ..eff_ct.clone() }
                .to_mat4_sized(my_eff_w, my_eff_h),
        );

        // ── Sprite ヒット（最優先候補）────────────────────────────────────────
        for slot in actor.slots() {
            if slot.kind == ComponentKind::Sprite {
                if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                    let eff_w = sc.width  * size_sc_x;
                    let eff_h = sc.height * size_sc_y;
                    let m = mat4x4_mul(parent_world_rs, eff_ct.to_mat4_sized(eff_w, eff_h));
                    if hit_test_rect_2d(canvas_x, canvas_y, &m, eff_w, eff_h) {
                        out.push(PickCand2d { dfs: my_dfs, kind: PickKind2d::Sprite, zone: my_zone, depth });
                    }
                }
            }
        }

        // ── Canvas 矩形ヒット（補助候補）──────────────────────────────────────
        if let Some(_cc) = my_canvas {
            let m = mat4x4_mul(parent_world_rs, eff_ct.to_mat4_sized(my_eff_w, my_eff_h));
            if hit_test_rect_2d(canvas_x, canvas_y, &m, my_eff_w, my_eff_h) {
                out.push(PickCand2d { dfs: my_dfs, kind: PickKind2d::Canvas, zone: my_zone, depth });
            }
        }

        // ── 子への継承情報を計算して再帰する（collect_canvas_rects と同一）─────
        let child_info = my_canvas.map(|cc| (
            root_auto.unwrap_or([cc.width, cc.height]),
            (cc.scale_transform, cc.scale_size,
             cc.keep_aspect_ratio, matches!(cc.aspect_ratio_axis, AspectRatioAxis::Width)),
            cc.auto_scale,
        ));
        let child_canvas_size = child_info.map(|(sz, _, _)| sz);
        let child_scale_mode  = child_info.map(|(_, sm, _)| sm)
            .unwrap_or((false, false, false, true));
        let auto_scale_factor = if parent_canvas_size.is_none() {
            if let (Some([vw, vh]), Some((_, _, true))) = (eff_viewport, child_info) {
                [vw / my_eff_w.max(f32::EPSILON), vh / my_eff_h.max(f32::EPSILON)]
            } else {
                [1.0f32, 1.0]
            }
        } else {
            [1.0f32, 1.0]
        };
        let child_cumul_scale = if child_scale_mode.0 {
            [parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
             parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1]]
        } else {
            [ct.scale[0] * auto_scale_factor[0],
             ct.scale[1] * auto_scale_factor[1]]
        };

        walk_pick_candidates_2d(
            &actor.children, world, wl,
            canvas_x, canvas_y, counter,
            self_world_rs, child_cumul_scale, child_scale_mode,
            child_canvas_size, depth + 1, my_zone,
            viewport_size, overrides, root_auto_sizes, design_space,
            out,
        );
    }
}

// ── 3D ワールドキャンバスのレイピック（world タブ用フォールバック）──────────────

impl App {
    /// world（3D）タブで、カメラレイと 3D ワールドキャンバス面の交差により
    /// キャンバスアクターの DFS ID を返す（GPU ID ピックが背景ヒットした際のフォールバック）。
    ///
    /// トップレベル Actor3D + CanvasComponent のキャンバス面（[0,w]×[0,h]）を対象にし、
    /// レイ最近点のキャンバスを返す。重なり時の巡回は 2D 専用のためここでは行わない。
    pub(super) fn pick_3d_world_canvas(&self, cx: f32, cy: f32) -> Option<usize> {
        let scene = self.scene.as_ref()?;
        let win   = self.window.as_ref().map(|w| w.inner_size());
        let vp_w  = win.map_or(1280.0, |s| s.width  as f32);
        let vp_h  = win.map_or(720.0,  |s| s.height as f32);
        let cam_v = self.camera.position();
        let cam   = [cam_v.x, cam_v.y, cam_v.z];
        let view  = self.camera.view_matrix();
        let proj  = self.camera.projection_matrix();
        let (r0, rd) = screen_to_ray(cx, cy, vp_w, vp_h, &view.data, &proj.data, cam);

        let mut counter = 0u32;
        let mut best: Option<(f32, usize)> = None; // (レイパラメータ t, DFS ID)
        walk_3d_canvas_pick(
            &scene.actors, &scene.world, self.active_world_line, &mut counter, r0, rd, &mut best,
        );
        best.map(|(_, dfs)| dfs)
    }
}

/// 3D ワールドキャンバス（Actor3D + CanvasComponent）の面とレイの交差を DFS 走査で調べ、
/// レイ最近点のキャンバス DFS ID を `best` に記録する。
fn walk_3d_canvas_pick(
    actors:  &[Actor],
    world:   &World,
    wl:      u32,
    counter: &mut u32,
    r0:      [f32; 3],
    rd:      [f32; 3],
    best:    &mut Option<(f32, usize)>,
) {
    for actor in actors {
        if actor.world_line != wl { continue; }
        let my_dfs = *counter as usize;
        *counter += 1;

        // Actor3D + CanvasComponent の面をテストする（描画と同一の canvas_to_world）
        if !actor.is_2d() {
            let canvas_slot = actor.slots().iter().find(|s| s.kind == ComponentKind::Canvas);
            if let (Some(cs), Some(tf)) = (canvas_slot, world.get::<Transform>(actor.entity)) {
                if let Some(cc) = world.get::<CanvasComponent>(cs.entity) {
                    let cws = CANVAS_WORLD_SCALE;
                    let (px, py) = (cc.pivot[0], cc.pivot[1]);
                    let ctw = mat4x4_mul(tf.to_mat4(), [
                        [ cws,  0.0, 0.0, -px * cc.width  * cws],
                        [ 0.0, -cws, 0.0,  py * cc.height * cws],
                        [ 0.0,  0.0, 1.0,  0.0                 ],
                        [ 0.0,  0.0, 0.0,  1.0                 ],
                    ]);
                    if let Some(t) = ray_hit_canvas_quad(r0, rd, &ctw, cc.width, cc.height) {
                        if best.map_or(true, |(bt, _)| t < bt) {
                            *best = Some((t, my_dfs));
                        }
                    }
                }
            }
        }

        walk_3d_canvas_pick(&actor.children, world, wl, counter, r0, rd, best);
    }
}

/// レイ (r0, rd) と、行列 `ctw`（canvas_to_world）が定義するローカル矩形
/// [0,w]×[0,h] 平面との交差レイパラメータ t を返す（ヒットなしは None）。
fn ray_hit_canvas_quad(r0: [f32; 3], rd: [f32; 3], ctw: &[[f32; 4]; 4], w: f32, h: f32) -> Option<f32> {
    let o  = [ctw[0][3], ctw[1][3], ctw[2][3]];         // 面の原点（ローカル [0,0]）
    let xw = [ctw[0][0], ctw[1][0], ctw[2][0]];         // ローカル +x（1px）→ ワールドベクトル
    let yw = [ctw[0][1], ctw[1][1], ctw[2][1]];         // ローカル +y（1px）→ ワールドベクトル
    let n  = cross(xw, yw);                             // 面の法線
    let denom = dot(rd, n);
    if denom.abs() < 1e-9 { return None; }              // レイが面と平行
    let t = dot(sub(o, r0), n) / denom;
    if t <= 0.0 { return None; }                        // 面がカメラ後方
    let p = [r0[0] + rd[0] * t, r0[1] + rd[1] * t, r0[2] + rd[2] * t];
    let l = sub(p, o);
    // xw ⊥ yw のためローカル px 座標は各軸への射影で求まる
    let lx = dot(l, xw) / dot(xw, xw).max(1e-12);
    let ly = dot(l, yw) / dot(yw, yw).max(1e-12);
    if lx >= 0.0 && lx <= w && ly >= 0.0 && ly <= h { Some(t) } else { None }
}

#[inline]
fn dot(a: [f32; 3], b: [f32; 3]) -> f32 { a[0]*b[0] + a[1]*b[1] + a[2]*b[2] }
#[inline]
fn sub(a: [f32; 3], b: [f32; 3]) -> [f32; 3] { [a[0]-b[0], a[1]-b[1], a[2]-b[2]] }
#[inline]
fn cross(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[1]*b[2] - a[2]*b[1], a[2]*b[0] - a[0]*b[2], a[0]*b[1] - a[1]*b[0]]
}
