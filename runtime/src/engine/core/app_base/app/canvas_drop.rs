// ============================================================
//  canvas_drop.rs — 2D シーンビューへのアクタードロップ配置支援
//
//  【責務】
//  - ウィンドウピクセル座標 → 2D ortho（キャンバス）空間への変換
//  - ルートスクリーンスペースキャンバスの矩形情報収集とカーソルヒット判定
//  - ヒットしたキャンバスのローカル座標系への逆変換（子アクター配置用）
//  - ドラッグホバー中キャンバスの枠線ハイライト描画
//
//  座標変換チェーンは canvas_collect.rs（collect_canvas_rects）および
//  physics2d_ops.rs（collect_actor2d_contexts）のルートレベル計算と
//  完全に同一のロジックを使用する。
// ============================================================

use crate::engine::components::{CanvasTransform, CanvasComponent, ComponentKind};
use crate::engine::ecs::Entity;

use super::App;
use super::pick_2d::hit_test_rect_2d;

// ─── ドラッグホバーハイライト定数 ────────────────────────────────────────────

/// ドラッグホバー中のキャンバス枠線ハイライト色（通常枠より明るいシアン系）。
pub(super) const DRAG_HOVER_CANVAS_COL: [f32; 4] = [0.35, 1.0, 1.0, 1.0];
/// ハイライト枠の擬似的な「太さ」を表現する同心矩形の本数。
/// GPU ラインは常に 1px のため、外側へオフセットした矩形を重ねて太く見せる。
const DRAG_HOVER_OUTLINE_RINGS: u32 = 3;
/// 同心矩形 1 本あたりの外側オフセット量（ortho 空間ピクセル）。
const DRAG_HOVER_OUTLINE_STEP_PX: f32 = 1.5;

// ─── ルートキャンバス矩形情報 ────────────────────────────────────────────────

/// ルート（トップレベル）スクリーンスペースキャンバス 1 つ分の矩形・座標系情報。
///
/// ドロップ時のヒット判定と、ヒットしたキャンバスのローカル座標系への
/// 逆変換（子アクターの CanvasTransform.position 算出）の両方に使用する。
pub(super) struct RootCanvasInfo {
    /// ルートキャンバスアクターのエンティティ（ハイライト対象の特定・親検索用）
    pub(super) entity:             Entity,
    /// 矩形描画と同じ行優先ワールド行列（eff_ct.to_mat4_sized(eff_w, eff_h)）。
    /// ローカル [0, eff_w]×[0, eff_h] を ortho 空間へマップする。
    pub(super) rect_m:             [[f32; 4]; 4],
    /// キャンバスの有効幅（ビューポート・ルートは自動解像度、それ以外は CanvasComponent.width）
    pub(super) eff_w:              f32,
    /// キャンバスの有効高さ（ビューポート・ルートは自動解像度、それ以外は CanvasComponent.height）
    pub(super) eff_h:              f32,
    /// キャンバスローカル [0,0] の ortho 空間ワールド位置（子の座標系原点）。
    /// collect_actor2d_contexts の child_canvas_origin と同一の計算
    /// （scale=1 の行列で pivot オフセットを逆適用した平行移動成分）。
    pub(super) origin:             [f32; 2],
    /// キャンバスのワールド回転（ラジアン、ルートのため自身の rotation のみ）
    pub(super) world_rot:          f32,
    /// 子が参照する基準キャンバスサイズ（スケール前の [width, height]）
    pub(super) canvas_size:        [f32; 2],
    /// 子への累積スケール（auto_scale_factor 込み）。
    /// collect_actor2d_contexts の child_cumul_scale と同一の計算。
    pub(super) child_cumul_scale:  [f32; 2],
    /// キャンバスの scale_transform フラグ（true なら子 position に累積スケールを乗算）
    pub(super) child_sm_transform: bool,
}

impl RootCanvasInfo {
    /// ortho 空間の点がこのキャンバス矩形内にあるかを判定する。
    pub(super) fn contains(&self, pt: [f32; 2]) -> bool {
        hit_test_rect_2d(pt[0], pt[1], &self.rect_m, self.eff_w, self.eff_h)
    }

    /// ortho 空間ワールド座標を、このキャンバスの子ローカル座標
    /// （CanvasTransform.position に設定すべき値）へ逆変換する。
    ///
    /// `child_anchor` は配置する子アクターの anchor（親キャンバスサイズ基準 [0,1]）。
    /// collect_actor2d_contexts の順変換
    ///   eff_pos_local = position (* cumul_scale) + anchor_off
    ///   world = origin + R(rot) * eff_pos_local
    /// の逆を解く。
    pub(super) fn world_to_child_local(&self, world_pt: [f32; 2], child_anchor: [f32; 2]) -> [f32; 2] {
        // ワールド → キャンバスローカル（回転の逆適用）
        let dx = world_pt[0] - self.origin[0];
        let dy = world_pt[1] - self.origin[1];
        let (sin_r, cos_r) = self.world_rot.sin_cos();
        let eff_pos_local = [
            cos_r * dx + sin_r * dy,
            -sin_r * dx + cos_r * dy,
        ];

        // 子のアンカーオフセット（親キャンバスサイズ × anchor × 累積スケール）
        let anchor_off = [
            self.canvas_size[0] * child_anchor[0] * self.child_cumul_scale[0],
            self.canvas_size[1] * child_anchor[1] * self.child_cumul_scale[1],
        ];

        // scale_transform の逆スケール（0 除算はスケール 1 として扱う）
        let px = eff_pos_local[0] - anchor_off[0];
        let py = eff_pos_local[1] - anchor_off[1];
        if self.child_sm_transform {
            let sx = if self.child_cumul_scale[0].abs() > f32::EPSILON { self.child_cumul_scale[0] } else { 1.0 };
            let sy = if self.child_cumul_scale[1].abs() > f32::EPSILON { self.child_cumul_scale[1] } else { 1.0 };
            [px / sx, py / sy]
        } else {
            [px, py]
        }
    }
}

impl App {
    /// ウィンドウピクセル座標（左上原点・Y-down）を 2D ortho（キャンバス）空間へ変換する。
    ///
    /// pick_2d_canvas と同一のマッピング:
    ///   canvas = pan + NDC × half_size（2D ortho カメラのパン・ズームを反映）。
    pub(super) fn window_to_canvas_2d(&self, cx: f32, cy: f32) -> [f32; 2] {
        let win_size = self.window.as_ref().map(|w| w.inner_size());
        let vp_w     = win_size.map_or(1280.0, |s| s.width  as f32);
        let vp_h     = win_size.map_or(720.0,  |s| s.height as f32);

        let cam_2d = self.canvas_cameras.get(&self.active_world_line).cloned().unwrap_or_default();
        let half_h = cam_2d.ortho_half_h;
        let half_w = half_h * (vp_w / vp_h);
        let ndx    = 2.0 * cx / vp_w - 1.0;
        let ndy    = 2.0 * cy / vp_h - 1.0; // Y-down
        [cam_2d.pan_x + ndx * half_w, cam_2d.pan_y + ndy * half_h]
    }

    /// シーン世界線（world_line=0）のルートスクリーンスペースキャンバス矩形情報を
    /// scene.actors の並び順（描画順 = 手前が後）で収集する。
    ///
    /// トップレベルの Actor2D + CanvasComponent のみが対象
    /// （Actor3D 配下のワールドキャンバスは含まない）。
    pub(super) fn collect_root_canvas_infos(&self) -> Vec<RootCanvasInfo> {
        const SCENE_WL: u32 = 0;
        let mut result = Vec::new();
        let Some(scene) = &self.scene else { return result };

        // ビューポートサイズ（WYSIWYG レイアウトの基準）
        let win_size = self.window.as_ref().map(|w| w.inner_size());
        let vp_w     = win_size.map_or(1280.0, |s| s.width  as f32);
        let vp_h     = win_size.map_or(720.0,  |s| s.height as f32);

        // ビューポート上書き + ルート自動解像度マップ（描画と同一条件・共通ヘルパー。
        // View2D では設計空間表示 = キャンバス中心が ortho 原点になる）
        let (vp_overrides, auto_sizes) = self.build_ss_layout_maps(
            &scene.actors, &scene.world, SCENE_WL, vp_w, vp_h, None,
        );

        for actor in &scene.actors {
            if actor.world_line != SCENE_WL { continue; }
            if !actor.is_2d() { continue; }

            // CanvasTransform + CanvasComponent の両方を持つルートのみ対象
            let Some(ct) = scene.world.get::<CanvasTransform>(actor.entity) else { continue };
            let Some(cc) = actor.slots().iter()
                .filter(|s| s.kind == ComponentKind::Canvas)
                .find_map(|s| scene.world.get::<CanvasComponent>(s.entity)) else { continue };

            // 自動解像度上書き（Some のとき Transform は恒等として扱う）
            let root_auto = auto_sizes.get(&actor.entity).copied();
            // ルートキャンバスの実効 CanvasTransform（自動解像度対象は恒等固定）
            let ct = if root_auto.is_some() { CanvasTransform::default() } else { ct.clone() };

            // 実効ビューポート（Camera 参照キャンバスはオーバーライドを優先）
            let [evw, evh] = vp_overrides.get(&actor.entity).copied().unwrap_or([vp_w, vp_h]);

            // ルートレベルのアンカーオフセット（collect_canvas_rects と同一の共通ヘルパー）。
            // design_space=true（ビューポート編集）ではキャンバス左上をワールド原点に一致させる。
            let anchor_off = super::canvas_collect::root_anchor_offset(
                ct.anchor, evw, evh, self.edit_view_is_2d(),
            );

            // ルートは sm_transform=false・親累積スケール=1 のため position をそのまま使用する
            let eff_ct = CanvasTransform {
                position: [ct.position[0] + anchor_off[0], ct.position[1] + anchor_off[1]],
                rotation: ct.rotation,
                scale:    ct.scale,
                pivot:    ct.pivot,
                anchor:   [0.0, 0.0],
            };

            // ルートの有効サイズ = 自動解像度（対象外は CanvasComponent の保存サイズ）
            let [eff_w, eff_h] = root_auto.unwrap_or([cc.width, cc.height]);

            // 矩形ヒット判定用行列（描画される矩形と同じ、ct.scale 込み）
            let rect_m = eff_ct.to_mat4_sized(eff_w, eff_h);

            // 子座標系原点用行列（scale=1、collect_canvas_rects の self_world_rs と同一）
            let origin_m = CanvasTransform { scale: [1.0, 1.0], ..eff_ct.clone() }
                .to_mat4_sized(eff_w, eff_h);

            // auto_scale_factor: ルートキャンバスかつ auto_scale=true のとき
            // ビューポートサイズ / 基準キャンバスサイズ
            let auto_scale_factor = if cc.auto_scale {
                [evw / eff_w.max(f32::EPSILON), evh / eff_h.max(f32::EPSILON)]
            } else {
                [1.0f32, 1.0]
            };

            // 子への累積スケール（ルートのため親累積 = 1 で scale_transform の分岐は結果同一）
            let child_cumul_scale = [
                ct.scale[0] * auto_scale_factor[0],
                ct.scale[1] * auto_scale_factor[1],
            ];

            result.push(RootCanvasInfo {
                entity:             actor.entity,
                rect_m,
                eff_w,
                eff_h,
                origin:             [origin_m[0][3], origin_m[1][3]],
                world_rot:          ct.rotation.to_radians(),
                // 子のアンカー基準サイズにも自動解像度上書きを反映する
                canvas_size:        root_auto.unwrap_or([cc.width, cc.height]),
                child_cumul_scale,
                child_sm_transform: cc.scale_transform,
            });
        }
        result
    }

    /// ortho 空間の点にヒットしているルートキャンバスのエンティティを返す。
    ///
    /// 複数ヒット時は scene.actors の並びで最後（最前面描画）のキャンバスを優先する。
    pub(super) fn hit_root_canvas_at(&self, pt: [f32; 2]) -> Option<Entity> {
        self.collect_root_canvas_infos()
            .iter()
            .rev() // 後に描画されるもの（手前）を優先
            .find(|info| info.contains(pt))
            .map(|info| info.entity)
    }

    /// ドラッグホバー中のルートキャンバス枠線を「明るく・太く」表示するための
    /// ハイライト線分リスト（始点・終点・色）を計算して返す。
    ///
    /// 通常の枠線（collect_canvas_rects）の上から、外側へオフセットした
    /// 同心矩形を DRAG_HOVER_OUTLINE_RINGS 本重ねることで太さを表現する。
    /// 座標系は 2D シーンビューの ortho 空間（canvas_scale=1, y_sign=1）前提。
    /// レンダーパス構築中は self の可変借用が続くため、
    /// frame_renderer 側で可変借用前に事前計算してから LineBatch へ流し込む。
    pub(super) fn collect_drag_hover_highlight_lines(&self) -> Vec<([f32; 3], [f32; 3], [f32; 4])> {
        let mut lines = Vec::new();
        let Some(hover_entity) = self.drag_hover_canvas_entity else { return lines };
        let infos = self.collect_root_canvas_infos();
        let Some(info) = infos.iter().find(|i| i.entity == hover_entity) else { return lines };

        // 矩形の 4 隅を ortho 空間へ変換する（collect_canvas_rects の tp と同一）
        let m = &info.rect_m;
        let tp = |lx: f32, ly: f32| -> [f32; 2] {
            [m[0][0] * lx + m[0][1] * ly + m[0][3],
             m[1][0] * lx + m[1][1] * ly + m[1][3]]
        };
        let corners = [
            tp(0.0,        0.0),
            tp(info.eff_w, 0.0),
            tp(info.eff_w, info.eff_h),
            tp(0.0,        info.eff_h),
        ];

        // 矩形中心（コーナー平均）から外側方向へ各リングをオフセットする
        let center = [
            (corners[0][0] + corners[1][0] + corners[2][0] + corners[3][0]) / 4.0,
            (corners[0][1] + corners[1][1] + corners[2][1] + corners[3][1]) / 4.0,
        ];

        for ring in 0..DRAG_HOVER_OUTLINE_RINGS {
            let offset = ring as f32 * DRAG_HOVER_OUTLINE_STEP_PX;
            // 各コーナーを中心から外側方向へ offset だけ移動した矩形を生成する
            let ring_corners: Vec<[f32; 3]> = corners.iter().map(|c| {
                let dx  = c[0] - center[0];
                let dy  = c[1] - center[1];
                let len = (dx * dx + dy * dy).sqrt().max(f32::EPSILON);
                [c[0] + dx / len * offset, c[1] + dy / len * offset, 0.0]
            }).collect();
            for i in 0..4 {
                lines.push((ring_corners[i], ring_corners[(i + 1) % 4], DRAG_HOVER_CANVAS_COL));
            }
        }
        lines
    }
}
