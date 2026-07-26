// ============================================================
//  gizmo_interact.rs — ギズモのヒットテスト・ドラッグ操作
//
//  【フロー】
//  1. LMB 押下 → screen_to_ray → hit_test_gizmo → start_drag
//  2. CursorMoved → screen_to_ray → update_drag → instance_mats 更新
//  3. LMB 離し → ドラッグ終了
// ============================================================

use crate::engine::core::app_base::ipc::ToolMode;

// ============================================================
//  型定義
// ============================================================

/// ギズモ半径のスクリーン占有率（ビューポート半高に対する比率）。
/// 透視: radius = dist * tan(fov_y/2) * RATIO、
/// 正射: radius = ortho_half_h * RATIO で見た目の大きさが一致する。
pub const GIZMO_SCREEN_RADIUS_RATIO: f32 = 0.233;

/// 編集ギズモを構成するパーツの種別。
///
/// `hit_test_gizmo` 系のヒットテストが返し、`start_drag`/`update_drag` が
/// どの軸・平面・ハンドルを操作中かの識別に使う。
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum GizmoPart {
    AxisX,
    AxisY,
    AxisZ,
    PlaneXY,
    PlaneXZ,
    PlaneYZ,
    /// 原点中心ハンドル（移動: カメラ平面移動 / スケール: 全軸均一スケール）。
    Center,
}

/// ドラッグ開始時に記録する状態。
#[derive(Clone)]
pub struct GizmoDrag {
    pub part: GizmoPart,
    pub tool: ToolMode,
    /// ドラッグ開始時のインスタンス変換行列（row-major）。
    pub start_mat: [[f32; 4]; 4],
    /// ギズモのワールド位置。
    pub gizmo_pos: [f32; 3],
    /// ギズモの半径（スクリーン固定）。
    pub radius: f32,
    /// ドラッグ開始時のワールド空間基準点（軸上 or 平面交点 or 正規化方向）。
    pub ref_point: [f32; 3],
    /// Center ハンドル用: ドラッグ開始時のカメラ平面法線。それ以外は未使用（[0;3]）。
    pub plane_normal: [f32; 3],
    /// キャンバス座標系の X / Y / Z 軸（ワールド空間単位ベクトル）。
    /// None = ワールド軸揃え（通常の 3D / 2D ギズモ）。
    /// Some([ax, ay, az]) = 3D Canvas 子アクター / Local 座標モード用向き付きギズモ。
    pub axes: Option<[[f32; 3]; 3]>,
    /// axes が Some のとき、az 軸（法線）も含む全 3 軸ギズモかどうか。
    /// - true  = Local 座標モードの通常 3D アクター（AxisZ・全平面ハンドル使用可）。
    /// - false = 3D Canvas 子アクター（2 軸限定・Center ハンドルは ax/ay 面内のみスケール）。
    /// axes が None のときは未使用（false のまま）。
    pub full_axes: bool,
}

// ============================================================
//  スクリーン → レイ変換
// ============================================================

/// ビューポートピクセル座標をワールド空間レイに変換する。
///
/// - `view_data` / `proj_data` : row-major 行列（`.data[row][col]`）
/// - 返り値: (ray_origin, ray_direction) ともにワールド空間
pub fn screen_to_ray(
    cx: f32,
    cy: f32,
    vp_w: f32,
    vp_h: f32,
    view_data: &[[f32; 4]; 4],
    proj_data: &[[f32; 4]; 4],
    cam_pos: [f32; 3],
) -> ([f32; 3], [f32; 3]) {
    // ビューポート → NDC（Y 反転）
    let ndc_x = 2.0 * cx / vp_w - 1.0;
    let ndc_y = 1.0 - 2.0 * cy / vp_h;

    // ビュー空間方向（LH 透視: 前方 = +Z）
    // proj[0][0] = f/aspect, proj[1][1] = f  (f = cot(fov_y/2))
    let vd = [ndc_x / proj_data[0][0], ndc_y / proj_data[1][1], 1.0f32];

    // ワールド空間方向 = inv(view) * vd = view^T の 3x3 部分 * vd
    let wd = [
        view_data[0][0] * vd[0] + view_data[1][0] * vd[1] + view_data[2][0] * vd[2],
        view_data[0][1] * vd[0] + view_data[1][1] * vd[1] + view_data[2][1] * vd[2],
        view_data[0][2] * vd[0] + view_data[1][2] * vd[1] + view_data[2][2] * vd[2],
    ];
    let len = len3(wd).max(1e-10);
    (cam_pos, [wd[0] / len, wd[1] / len, wd[2] / len])
}

/// 3D 正射投影デバッグカメラのスクリーン座標をワールド空間レイに変換する。
///
/// 透視と異なりレイ原点がスクリーン位置ごとにビュー平面上を移動し、
/// レイ方向はカメラ前方向で一定になる。
/// - `view_data`: row-major ビュー行列（行 0/1/2 = 右/上/前 のカメラ基底）
/// - `half_w` / `half_h`: 正射投影の半幅・半高（ワールド単位）
pub fn screen_to_ray_ortho3d(
    cx: f32,
    cy: f32,
    vp_w: f32,
    vp_h: f32,
    view_data: &[[f32; 4]; 4],
    cam_pos: [f32; 3],
    half_w: f32,
    half_h: f32,
) -> ([f32; 3], [f32; 3]) {
    // ビューポート → NDC（Y 反転、Y-up 規則）
    let ndc_x = 2.0 * cx / vp_w - 1.0;
    let ndc_y = 1.0 - 2.0 * cy / vp_h;
    // ビュー行列の行ベクトル = カメラ基底（右・上・前）のワールド表現
    let right = [view_data[0][0], view_data[0][1], view_data[0][2]];
    let up = [view_data[1][0], view_data[1][1], view_data[1][2]];
    let fwd = [view_data[2][0], view_data[2][1], view_data[2][2]];
    // レイ原点 = カメラ位置 + ビュー平面上のオフセット
    let ox = ndc_x * half_w;
    let oy = ndc_y * half_h;
    let origin = [
        cam_pos[0] + right[0] * ox + up[0] * oy,
        cam_pos[1] + right[1] * ox + up[1] * oy,
        cam_pos[2] + right[2] * ox + up[2] * oy,
    ];
    let len = len3(fwd).max(1e-10);
    (origin, [fwd[0] / len, fwd[1] / len, fwd[2] / len])
}

/// 2D 正射影カメラのスクリーン座標をワールド空間レイに変換する。
/// レイ方向は常に [0, 0, 1]（カメラは Z=-100 から XY 平面を見下ろす）。
pub fn screen_to_ray_ortho(
    cx: f32,
    cy: f32,
    vp_w: f32,
    vp_h: f32,
    pan_x: f32,
    pan_y: f32,
    half_w: f32,
    half_h: f32,
) -> ([f32; 3], [f32; 3]) {
    // Y-down 規則でスクリーン→ワールド XY を計算する
    let ndc_x = 2.0 * cx / vp_w - 1.0;
    let ndc_y = 2.0 * cy / vp_h - 1.0;
    let world_x = pan_x + ndc_x * half_w;
    let world_y = pan_y + ndc_y * half_h;
    ([world_x, world_y, -100.0], [0.0, 0.0, 1.0])
}

// ============================================================
//  ヒットテスト
// ============================================================

/// ギズモ各パーツに対してレイテストを行い、最も近いパーツを返す。
pub fn hit_test_gizmo(
    ray_o: [f32; 3],
    ray_d: [f32; 3],
    gizmo_pos: [f32; 3],
    radius: f32,
    tool: ToolMode,
) -> Option<GizmoPart> {
    let mut best: Option<(GizmoPart, f32)> = None;
    let mut try_update = |part, dist: f32| {
        if best.map_or(true, |(_, d)| dist < d) {
            best = Some((part, dist));
        }
    };

    let axis_thresh = radius * 0.18; // 軸シャフトのヒット幅
    let ring_bw = radius * 0.18; // リングの帯幅

    match tool {
        ToolMode::Move | ToolMode::Scale => {
            // 中心ハンドル（最優先）
            let center_dist = ray_to_point_dist(ray_o, ray_d, gizmo_pos);
            if center_dist < radius * 0.12 {
                try_update(GizmoPart::Center, -2.0);
            }

            // 軸シャフト
            for (part, axis) in [
                (GizmoPart::AxisX, [1.0f32, 0.0, 0.0]),
                (GizmoPart::AxisY, [0.0, 1.0, 0.0]),
                (GizmoPart::AxisZ, [0.0, 0.0, 1.0]),
            ] {
                let d = ray_to_segment_dist(ray_o, ray_d, gizmo_pos, axis, radius);
                if d < axis_thresh {
                    try_update(part, d);
                }
            }

            // 平面ハンドル（軸より優先するため距離 0 扱いにシフト）
            let o = radius * 0.5;
            let s = radius * 0.075;
            for (part, center, normal) in [
                (
                    GizmoPart::PlaneXY,
                    [gizmo_pos[0] + o, gizmo_pos[1] + o, gizmo_pos[2]],
                    [0.0f32, 0.0, 1.0],
                ),
                (
                    GizmoPart::PlaneXZ,
                    [gizmo_pos[0] + o, gizmo_pos[1], gizmo_pos[2] + o],
                    [0.0, 1.0, 0.0],
                ),
                (
                    GizmoPart::PlaneYZ,
                    [gizmo_pos[0], gizmo_pos[1] + o, gizmo_pos[2] + o],
                    [1.0, 0.0, 0.0],
                ),
            ] {
                if let Some(t) = ray_plane_t(ray_o, ray_d, center, normal) {
                    if t > 0.0 {
                        let hit = ray_at(ray_o, ray_d, t);
                        let diff = sub3(hit, center);
                        // 平面法線方向以外の2成分が half_size 以内なら hit
                        let in_x = normal[0].abs() < 0.5 || diff[0].abs() <= s;
                        let in_y = normal[1].abs() < 0.5 || diff[1].abs() <= s;
                        let in_z = normal[2].abs() < 0.5 || diff[2].abs() <= s;
                        let in_rect = in_x
                            && in_y
                            && in_z
                            && diff[0].abs() <= s + 1e-4
                            && diff[1].abs() <= s + 1e-4
                            && diff[2].abs() <= s + 1e-4;
                        if in_rect {
                            try_update(part, -1.0); // 平面を軸より優先
                        }
                    }
                }
            }
        }
        ToolMode::Rotate => {
            for (part, normal) in [
                (GizmoPart::AxisX, [1.0f32, 0.0, 0.0]),
                (GizmoPart::AxisY, [0.0, 1.0, 0.0]),
                (GizmoPart::AxisZ, [0.0, 0.0, 1.0]),
            ] {
                if let Some(t) = ray_plane_t(ray_o, ray_d, gizmo_pos, normal) {
                    if t > 0.0 {
                        let hit = ray_at(ray_o, ray_d, t);
                        let dist = len3(sub3(hit, gizmo_pos));
                        let ring_dist = (dist - radius).abs();
                        if ring_dist < ring_bw {
                            try_update(part, ring_dist);
                        }
                    }
                }
            }
        }
        _ => {}
    }

    best.map(|(p, _)| p)
}

// ============================================================
//  ドラッグ開始
// ============================================================

/// ヒットしたパーツに対応するドラッグ状態を初期化する。
///
/// Rotate モードの軸パーツでは、ref_point にリング平面との交点から
/// ギズモ中心への**正規化方向ベクトル**を格納する（絶対座標ではない）。
/// Move / Scale の軸パーツでは従来通り軸上の最近接点（絶対座標）を格納する。
pub fn start_drag(
    part: GizmoPart,
    tool: ToolMode,
    ray_o: [f32; 3],
    ray_d: [f32; 3],
    gizmo_pos: [f32; 3],
    radius: f32,
    start_mat: [[f32; 4]; 4],
) -> GizmoDrag {
    let (ref_point, plane_normal) = if tool == ToolMode::Rotate {
        // リング平面との交点を求め、中心からの正規化方向を保存する。
        // t < 0（平面がカメラ後方）も許容する。
        let normal = match part {
            GizmoPart::AxisX => [1.0f32, 0.0, 0.0],
            GizmoPart::AxisY => [0.0, 1.0, 0.0],
            GizmoPart::AxisZ => [0.0, 0.0, 1.0],
            _ => [0.0, 0.0, 1.0],
        };
        let dir = if let Some(t) = ray_plane_t(ray_o, ray_d, gizmo_pos, normal) {
            let hit = ray_at(ray_o, ray_d, t);
            let d = sub3(hit, gizmo_pos);
            if len3(d) > 1e-6 {
                normalize3(d)
            } else {
                perp_to(normal)
            }
        } else {
            perp_to(normal)
        };
        (dir, [0.0f32; 3])
    } else if part == GizmoPart::Center {
        // カメラ正面平面（ドラッグ中は法線を固定）
        let cam_dir = normalize3(sub3(gizmo_pos, ray_o));
        let hit = ray_plane_t(ray_o, ray_d, gizmo_pos, cam_dir)
            .map(|t| ray_at(ray_o, ray_d, t))
            .unwrap_or(gizmo_pos);
        (hit, cam_dir)
    } else {
        let rp = match part {
            GizmoPart::AxisX => closest_on_axis(gizmo_pos, [1.0, 0.0, 0.0], ray_o, ray_d),
            GizmoPart::AxisY => closest_on_axis(gizmo_pos, [0.0, 1.0, 0.0], ray_o, ray_d),
            GizmoPart::AxisZ => closest_on_axis(gizmo_pos, [0.0, 0.0, 1.0], ray_o, ray_d),
            GizmoPart::PlaneXY => {
                ray_plane_hit(ray_o, ray_d, gizmo_pos, [0.0, 0.0, 1.0]).unwrap_or(gizmo_pos)
            }
            GizmoPart::PlaneXZ => {
                ray_plane_hit(ray_o, ray_d, gizmo_pos, [0.0, 1.0, 0.0]).unwrap_or(gizmo_pos)
            }
            GizmoPart::PlaneYZ => {
                ray_plane_hit(ray_o, ray_d, gizmo_pos, [1.0, 0.0, 0.0]).unwrap_or(gizmo_pos)
            }
            GizmoPart::Center => gizmo_pos,
        };
        (rp, [0.0f32; 3])
    };
    GizmoDrag {
        part,
        tool,
        start_mat,
        gizmo_pos,
        radius,
        ref_point,
        plane_normal,
        axes: None,
        full_axes: false,
    }
}

// ============================================================
//  ドラッグ更新 → 新しい変換行列を返す
// ============================================================

pub fn update_drag(drag: &GizmoDrag, ray_o: [f32; 3], ray_d: [f32; 3]) -> [[f32; 4]; 4] {
    let mut mat = drag.start_mat;
    let gp = drag.gizmo_pos;

    match drag.tool {
        ToolMode::Move => {
            let delta = if let Some([ax, ay, az]) = drag.axes {
                // 向き付きギズモ（3D Canvas 子アクター / Local 座標モード）: ax/ay/az 軸に沿った移動。
                // 3D Canvas 子は hit_test_gizmo_canvas が AxisZ・XZ/YZ 平面を除外するため
                // 実際には出現しないが、Local モードの通常アクターは全軸・全平面を使うため
                // ワールド軸版と対称に全パーツを扱う。
                match drag.part {
                    GizmoPart::AxisX => {
                        let curr = closest_on_axis(gp, ax, ray_o, ray_d);
                        let proj = dot3(sub3(curr, drag.ref_point), ax);
                        scale3(ax, proj)
                    }
                    GizmoPart::AxisY => {
                        let curr = closest_on_axis(gp, ay, ray_o, ray_d);
                        let proj = dot3(sub3(curr, drag.ref_point), ay);
                        scale3(ay, proj)
                    }
                    GizmoPart::AxisZ => {
                        let curr = closest_on_axis(gp, az, ray_o, ray_d);
                        let proj = dot3(sub3(curr, drag.ref_point), az);
                        scale3(az, proj)
                    }
                    GizmoPart::PlaneXY => {
                        // canvas/ローカル XY 平面（法線 = az）上での移動
                        if let Some(p) = ray_plane_hit(ray_o, ray_d, gp, az) {
                            sub3(p, drag.ref_point)
                        } else {
                            [0.0; 3]
                        }
                    }
                    GizmoPart::PlaneXZ => {
                        // ローカル XZ 平面（法線 = ay）上での移動
                        if let Some(p) = ray_plane_hit(ray_o, ray_d, gp, ay) {
                            sub3(p, drag.ref_point)
                        } else {
                            [0.0; 3]
                        }
                    }
                    GizmoPart::PlaneYZ => {
                        // ローカル YZ 平面（法線 = ax）上での移動
                        if let Some(p) = ray_plane_hit(ray_o, ray_d, gp, ax) {
                            sub3(p, drag.ref_point)
                        } else {
                            [0.0; 3]
                        }
                    }
                    GizmoPart::Center => {
                        if let Some(t) = ray_plane_t(ray_o, ray_d, gp, drag.plane_normal) {
                            let p = ray_at(ray_o, ray_d, t);
                            sub3(p, drag.ref_point)
                        } else {
                            [0.0; 3]
                        }
                    }
                }
            } else {
                // 通常ワールド軸
                match drag.part {
                    GizmoPart::AxisX => {
                        let curr = closest_on_axis(gp, [1.0, 0.0, 0.0], ray_o, ray_d);
                        [curr[0] - drag.ref_point[0], 0.0, 0.0]
                    }
                    GizmoPart::AxisY => {
                        let curr = closest_on_axis(gp, [0.0, 1.0, 0.0], ray_o, ray_d);
                        [0.0, curr[1] - drag.ref_point[1], 0.0]
                    }
                    GizmoPart::AxisZ => {
                        let curr = closest_on_axis(gp, [0.0, 0.0, 1.0], ray_o, ray_d);
                        [0.0, 0.0, curr[2] - drag.ref_point[2]]
                    }
                    GizmoPart::PlaneXY => {
                        if let Some(p) = ray_plane_hit(ray_o, ray_d, gp, [0.0, 0.0, 1.0]) {
                            [p[0] - drag.ref_point[0], p[1] - drag.ref_point[1], 0.0]
                        } else {
                            [0.0; 3]
                        }
                    }
                    GizmoPart::PlaneXZ => {
                        if let Some(p) = ray_plane_hit(ray_o, ray_d, gp, [0.0, 1.0, 0.0]) {
                            [p[0] - drag.ref_point[0], 0.0, p[2] - drag.ref_point[2]]
                        } else {
                            [0.0; 3]
                        }
                    }
                    GizmoPart::PlaneYZ => {
                        if let Some(p) = ray_plane_hit(ray_o, ray_d, gp, [1.0, 0.0, 0.0]) {
                            [0.0, p[1] - drag.ref_point[1], p[2] - drag.ref_point[2]]
                        } else {
                            [0.0; 3]
                        }
                    }
                    GizmoPart::Center => {
                        if let Some(t) = ray_plane_t(ray_o, ray_d, gp, drag.plane_normal) {
                            let p = ray_at(ray_o, ray_d, t);
                            sub3(p, drag.ref_point)
                        } else {
                            [0.0; 3]
                        }
                    }
                }
            };
            mat[0][3] = drag.start_mat[0][3] + delta[0];
            mat[1][3] = drag.start_mat[1][3] + delta[1];
            mat[2][3] = drag.start_mat[2][3] + delta[2];
        }

        ToolMode::Scale => {
            let apply_col = |mat: &mut [[f32; 4]; 4], col: usize, factor: f32| {
                for r in 0..3 {
                    mat[r][col] = drag.start_mat[r][col] * factor;
                }
            };
            if let Some([ax, ay, az]) = drag.axes {
                // 向き付きギズモ（3D Canvas 子アクター / Local 座標モード）: ax/ay/az は
                // ワールド軸と一致しない任意方向を向きうるため、apply_col（列インデックスへの
                // 直接上書き）は使えない。外積による正規直交基底スケール
                //   S = fx*(ax⊗ax) + fy*(ay⊗ay) + fz*(az⊗az)
                // を線形 3x3 部分へ直接構築する（スケールしない軸の係数は 1.0）。
                let apply_oriented = |mat: &mut [[f32; 4]; 4], fx: f32, fy: f32, fz: f32| {
                    for r in 0..3 {
                        for c in 0..3 {
                            mat[r][c] =
                                fx * ax[r] * ax[c] + fy * ay[r] * ay[c] + fz * az[r] * az[c];
                        }
                    }
                };
                match drag.part {
                    GizmoPart::AxisX => {
                        let curr = closest_on_axis(gp, ax, ray_o, ray_d);
                        let f = scale_factor_axis(drag.ref_point, curr, ax, gp);
                        apply_oriented(&mut mat, f, 1.0, 1.0);
                    }
                    GizmoPart::AxisY => {
                        let curr = closest_on_axis(gp, ay, ray_o, ray_d);
                        let f = scale_factor_axis(drag.ref_point, curr, ay, gp);
                        apply_oriented(&mut mat, 1.0, f, 1.0);
                    }
                    GizmoPart::AxisZ => {
                        // full_axes（Local モード）のみ到達する。3D Canvas 子は
                        // hit_test_gizmo_canvas が AxisZ を除外するためここには来ない。
                        let curr = closest_on_axis(gp, az, ray_o, ray_d);
                        let f = scale_factor_axis(drag.ref_point, curr, az, gp);
                        apply_oriented(&mut mat, 1.0, 1.0, f);
                    }
                    GizmoPart::PlaneXY => {
                        if let Some(p) = ray_plane_hit(ray_o, ray_d, gp, az) {
                            let f = plane_scale_factor(drag.ref_point, p, gp);
                            apply_oriented(&mut mat, f, f, 1.0);
                        }
                    }
                    GizmoPart::PlaneXZ => {
                        // full_axes（Local モード）のみ到達する
                        if let Some(p) = ray_plane_hit(ray_o, ray_d, gp, ay) {
                            let f = plane_scale_factor(drag.ref_point, p, gp);
                            apply_oriented(&mut mat, f, 1.0, f);
                        }
                    }
                    GizmoPart::PlaneYZ => {
                        // full_axes（Local モード）のみ到達する
                        if let Some(p) = ray_plane_hit(ray_o, ray_d, gp, ax) {
                            let f = plane_scale_factor(drag.ref_point, p, gp);
                            apply_oriented(&mut mat, 1.0, f, f);
                        }
                    }
                    GizmoPart::Center => {
                        if let Some(t) = ray_plane_t(ray_o, ray_d, gp, drag.plane_normal) {
                            let p = ray_at(ray_o, ray_d, t);
                            let d_curr = len3(sub3(p, gp));
                            let d_ref = len3(sub3(drag.ref_point, gp));
                            let f = (1.0 + (d_curr - d_ref) / drag.radius).max(0.05);
                            // 3D Canvas 子（full_axes=false）は従来どおり ax/ay 面内のみ
                            // 均等スケールし、法線（az）方向は変更しない。
                            // Local モード（full_axes=true）は ax/ay/az 全軸を均等スケールする。
                            let fz = if drag.full_axes { f } else { 1.0 };
                            apply_oriented(&mut mat, f, f, fz);
                        }
                    }
                }
            } else {
                match drag.part {
                    GizmoPart::AxisX => {
                        let curr = closest_on_axis(gp, [1.0, 0.0, 0.0], ray_o, ray_d);
                        let f = scale_factor_axis(drag.ref_point, curr, [1.0, 0.0, 0.0], gp);
                        apply_col(&mut mat, 0, f);
                    }
                    GizmoPart::AxisY => {
                        let curr = closest_on_axis(gp, [0.0, 1.0, 0.0], ray_o, ray_d);
                        let f = scale_factor_axis(drag.ref_point, curr, [0.0, 1.0, 0.0], gp);
                        apply_col(&mut mat, 1, f);
                    }
                    GizmoPart::AxisZ => {
                        let curr = closest_on_axis(gp, [0.0, 0.0, 1.0], ray_o, ray_d);
                        let f = scale_factor_axis(drag.ref_point, curr, [0.0, 0.0, 1.0], gp);
                        apply_col(&mut mat, 2, f);
                    }
                    GizmoPart::PlaneXY => {
                        if let Some(p) = ray_plane_hit(ray_o, ray_d, gp, [0.0, 0.0, 1.0]) {
                            let f = plane_scale_factor(drag.ref_point, p, gp);
                            apply_col(&mut mat, 0, f);
                            apply_col(&mut mat, 1, f);
                        }
                    }
                    GizmoPart::PlaneXZ => {
                        if let Some(p) = ray_plane_hit(ray_o, ray_d, gp, [0.0, 1.0, 0.0]) {
                            let f = plane_scale_factor(drag.ref_point, p, gp);
                            apply_col(&mut mat, 0, f);
                            apply_col(&mut mat, 2, f);
                        }
                    }
                    GizmoPart::PlaneYZ => {
                        if let Some(p) = ray_plane_hit(ray_o, ray_d, gp, [1.0, 0.0, 0.0]) {
                            let f = plane_scale_factor(drag.ref_point, p, gp);
                            apply_col(&mut mat, 1, f);
                            apply_col(&mut mat, 2, f);
                        }
                    }
                    GizmoPart::Center => {
                        if let Some(t) = ray_plane_t(ray_o, ray_d, gp, drag.plane_normal) {
                            let p = ray_at(ray_o, ray_d, t);
                            let d_curr = len3(sub3(p, gp));
                            let d_ref = len3(sub3(drag.ref_point, gp));
                            let f = (1.0 + (d_curr - d_ref) / drag.radius).max(0.05);
                            apply_col(&mut mat, 0, f);
                            apply_col(&mut mat, 1, f);
                            apply_col(&mut mat, 2, f);
                        }
                    }
                }
            }
        }

        ToolMode::Rotate => {
            // axes が Some の場合: 3D Canvas 子（full_axes=false）は AxisZ = canvas 法線
            // 周りの回転のみ許容する（hit_test_gizmo_canvas が他パーツを除外済み）。
            // Local 座標モード（full_axes=true）は ax/ay/az 全軸周りの回転を許容する。
            let axis = if let Some([ax, ay, az]) = drag.axes {
                match drag.part {
                    GizmoPart::AxisX if drag.full_axes => ax,
                    GizmoPart::AxisY if drag.full_axes => ay,
                    GizmoPart::AxisZ => az,
                    _ => return mat,
                }
            } else {
                match drag.part {
                    GizmoPart::AxisX => [1.0f32, 0.0, 0.0],
                    GizmoPart::AxisY => [0.0, 1.0, 0.0],
                    GizmoPart::AxisZ => [0.0, 0.0, 1.0],
                    _ => return mat,
                }
            };
            // t < 0（平面がカメラ後方）も許容して交点を取得する。
            if let Some(t) = ray_plane_t(ray_o, ray_d, gp, axis) {
                let curr_hit = ray_at(ray_o, ray_d, t);
                let v1 = normalize3(sub3(curr_hit, gp));
                // ref_point は start_drag で正規化済みの方向ベクトル。
                let v0 = drag.ref_point;
                if len3(v1) > 0.5 {
                    // atan2 で全周 360° の符号付き角度を得る。
                    // sin は cross(v0,v1) の軸方向成分（右手系符号）。
                    let cos_t = dot3(v0, v1).clamp(-1.0, 1.0);
                    let sin_t = dot3(cross3(v0, v1), axis);
                    let theta = sin_t.atan2(cos_t);
                    let rot = rot_matrix(axis, theta);
                    // ワールド空間回転: rot * start_mat（平行移動は維持）
                    mat = mat4x4_mul(rot, drag.start_mat);
                    mat[0][3] = drag.start_mat[0][3];
                    mat[1][3] = drag.start_mat[1][3];
                    mat[2][3] = drag.start_mat[2][3];
                }
            }
        }

        _ => {}
    }

    mat
}

// ============================================================
//  数学ユーティリティ
// ============================================================

fn dot3(a: [f32; 3], b: [f32; 3]) -> f32 {
    a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
}
fn sub3(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}
fn len3(v: [f32; 3]) -> f32 {
    (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt()
}
fn normalize3(v: [f32; 3]) -> [f32; 3] {
    let l = len3(v).max(1e-10);
    [v[0] / l, v[1] / l, v[2] / l]
}
fn cross3(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]
}
fn ray_at(o: [f32; 3], d: [f32; 3], t: f32) -> [f32; 3] {
    [o[0] + d[0] * t, o[1] + d[1] * t, o[2] + d[2] * t]
}

/// レイと点の最短距離（点がレイ後方の場合は始点からの距離）。
fn ray_to_point_dist(ray_o: [f32; 3], ray_d: [f32; 3], p: [f32; 3]) -> f32 {
    let t = dot3(sub3(p, ray_o), ray_d).max(0.0);
    len3(sub3(ray_at(ray_o, ray_d, t), p))
}

/// 法線に垂直な任意の単位ベクトルを返す（回転ドラッグ開始時のフォールバック）。
fn perp_to(n: [f32; 3]) -> [f32; 3] {
    let up = if n[0].abs() < 0.9 {
        [1.0_f32, 0.0, 0.0]
    } else {
        [0.0_f32, 1.0, 0.0]
    };
    let c = cross3(n, up);
    let l = len3(c).max(1e-10);
    [c[0] / l, c[1] / l, c[2] / l]
}

/// レイとセグメント間の最短距離。
fn ray_to_segment_dist(
    ray_o: [f32; 3],
    ray_d: [f32; 3],
    seg_o: [f32; 3],
    seg_dir: [f32; 3],
    seg_len: f32,
) -> f32 {
    let w = sub3(ray_o, seg_o);
    let b = dot3(ray_d, seg_dir);
    let e = dot3(seg_dir, w);
    let d = dot3(ray_d, w);
    let denom = 1.0 - b * b;
    let (t_ray, t_seg) = if denom.abs() > 1e-8 {
        (
            (b * e - d) / denom,
            ((e - b * d) / denom).clamp(0.0, seg_len),
        )
    } else {
        (0.0f32, 0.0f32)
    };
    let pr = ray_at(ray_o, ray_d, t_ray);
    let ps = [
        seg_o[0] + seg_dir[0] * t_seg,
        seg_o[1] + seg_dir[1] * t_seg,
        seg_o[2] + seg_dir[2] * t_seg,
    ];
    len3(sub3(pr, ps))
}

/// 無限軸上でレイに最近接するワールド座標。
fn closest_on_axis(
    axis_o: [f32; 3],
    axis_d: [f32; 3],
    ray_o: [f32; 3],
    ray_d: [f32; 3],
) -> [f32; 3] {
    let w = sub3(ray_o, axis_o);
    let b = dot3(ray_d, axis_d);
    let e = dot3(axis_d, w);
    let d = dot3(ray_d, w);
    let denom = 1.0 - b * b;
    let t = if denom.abs() > 1e-8 {
        (e - b * d) / denom
    } else {
        0.0
    };
    [
        axis_o[0] + axis_d[0] * t,
        axis_o[1] + axis_d[1] * t,
        axis_o[2] + axis_d[2] * t,
    ]
}

/// レイ→平面の交差パラメータ t（負なら背面）。
fn ray_plane_t(
    ray_o: [f32; 3],
    ray_d: [f32; 3],
    plane_o: [f32; 3],
    normal: [f32; 3],
) -> Option<f32> {
    let denom = dot3(ray_d, normal);
    if denom.abs() < 1e-8 {
        return None;
    }
    Some(dot3(sub3(plane_o, ray_o), normal) / denom)
}

/// レイ→平面の交差点。
fn ray_plane_hit(
    ray_o: [f32; 3],
    ray_d: [f32; 3],
    plane_o: [f32; 3],
    normal: [f32; 3],
) -> Option<[f32; 3]> {
    let t = ray_plane_t(ray_o, ray_d, plane_o, normal)?;
    if t < 0.0 {
        return None;
    }
    Some(ray_at(ray_o, ray_d, t))
}

/// 軸方向のスケール係数（符号あり、最小値 0.05 でクランプ）。
fn scale_factor_axis(ref_pt: [f32; 3], curr_pt: [f32; 3], axis: [f32; 3], origin: [f32; 3]) -> f32 {
    let d_ref = dot3(sub3(ref_pt, origin), axis);
    let d_curr = dot3(sub3(curr_pt, origin), axis);
    if d_ref.abs() < 1e-4 {
        return 1.0;
    }
    (d_curr / d_ref).max(0.05)
}

/// 平面内の均一スケール係数（中心からの距離比）。
fn plane_scale_factor(ref_pt: [f32; 3], curr_pt: [f32; 3], origin: [f32; 3]) -> f32 {
    let d_ref = len3(sub3(ref_pt, origin));
    let d_curr = len3(sub3(curr_pt, origin));
    if d_ref < 1e-4 {
        return 1.0;
    }
    (d_curr / d_ref).max(0.05)
}

/// axis 周りの回転行列（row-major）。
fn rot_matrix(axis: [f32; 3], angle: f32) -> [[f32; 4]; 4] {
    let (s, c) = angle.sin_cos();
    let t = 1.0 - c;
    let [x, y, z] = axis;
    [
        [t * x * x + c, t * x * y - s * z, t * x * z + s * y, 0.0],
        [t * x * y + s * z, t * y * y + c, t * y * z - s * x, 0.0],
        [t * x * z - s * y, t * y * z + s * x, t * z * z + c, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]
}

/// アフィン変換行列（下行 = [0,0,0,1]）の逆行列。
pub fn mat4x4_inv(m: [[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let r = [
        [m[0][0], m[0][1], m[0][2]],
        [m[1][0], m[1][1], m[1][2]],
        [m[2][0], m[2][1], m[2][2]],
    ];
    let det = r[0][0] * (r[1][1] * r[2][2] - r[1][2] * r[2][1])
        - r[0][1] * (r[1][0] * r[2][2] - r[1][2] * r[2][0])
        + r[0][2] * (r[1][0] * r[2][1] - r[1][1] * r[2][0]);
    let inv = if det.abs() < 1e-8 { 1.0 } else { 1.0 / det };
    let ri = [
        [
            (r[1][1] * r[2][2] - r[1][2] * r[2][1]) * inv,
            (r[0][2] * r[2][1] - r[0][1] * r[2][2]) * inv,
            (r[0][1] * r[1][2] - r[0][2] * r[1][1]) * inv,
        ],
        [
            (r[1][2] * r[2][0] - r[1][0] * r[2][2]) * inv,
            (r[0][0] * r[2][2] - r[0][2] * r[2][0]) * inv,
            (r[0][2] * r[1][0] - r[0][0] * r[1][2]) * inv,
        ],
        [
            (r[1][0] * r[2][1] - r[1][1] * r[2][0]) * inv,
            (r[0][1] * r[2][0] - r[0][0] * r[2][1]) * inv,
            (r[0][0] * r[1][1] - r[0][1] * r[1][0]) * inv,
        ],
    ];
    let t = [m[0][3], m[1][3], m[2][3]];
    let tx = -(ri[0][0] * t[0] + ri[0][1] * t[1] + ri[0][2] * t[2]);
    let ty = -(ri[1][0] * t[0] + ri[1][1] * t[1] + ri[1][2] * t[2]);
    let tz = -(ri[2][0] * t[0] + ri[2][1] * t[1] + ri[2][2] * t[2]);
    [
        [ri[0][0], ri[0][1], ri[0][2], tx],
        [ri[1][0], ri[1][1], ri[1][2], ty],
        [ri[2][0], ri[2][1], ri[2][2], tz],
        [0.0, 0.0, 0.0, 1.0],
    ]
}

/// row-major 4x4 行列の積 a * b。
pub fn mat4x4_mul(a: [[f32; 4]; 4], b: [[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let mut r = [[0.0f32; 4]; 4];
    for i in 0..4 {
        for j in 0..4 {
            for k in 0..4 {
                r[i][j] += a[i][k] * b[k][j];
            }
        }
    }
    r
}

fn add3(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
}
fn scale3(v: [f32; 3], s: f32) -> [f32; 3] {
    [v[0] * s, v[1] * s, v[2] * s]
}

// ============================================================
//  キャンバス座標系向け oriented ギズモ関数
// ============================================================

/// 3D Canvas 子アクター向けのヒットテスト。
///
/// レイをキャンバスローカル空間（ax/ay/az = X/Y/Z 軸）に変換してから
/// 標準 hit_test_gizmo を実行し、キャンバスで有効なパーツのみを返す。
///   Move/Scale: AxisX, AxisY, PlaneXY（canvas 平面移動）, Center
///   Rotate    : AxisZ（canvas 法線周り）のみ
pub fn hit_test_gizmo_canvas(
    ray_o: [f32; 3],
    ray_d: [f32; 3],
    gizmo_pos: [f32; 3],
    radius: f32,
    tool: ToolMode,
    ax: [f32; 3],
    ay: [f32; 3],
    az: [f32; 3],
) -> Option<GizmoPart> {
    // レイをキャンバスローカル空間に変換する（gizmo_pos が原点）
    let d = sub3(ray_o, gizmo_pos);
    let local_o = [dot3(d, ax), dot3(d, ay), dot3(d, az)];
    let local_d = [dot3(ray_d, ax), dot3(ray_d, ay), dot3(ray_d, az)];
    let part = hit_test_gizmo(local_o, local_d, [0.0, 0.0, 0.0], radius, tool)?;
    // キャンバス子アクター向けの有効パーツフィルタ
    let valid = match tool {
        ToolMode::Move | ToolMode::Scale => {
            matches!(
                part,
                GizmoPart::AxisX | GizmoPart::AxisY | GizmoPart::PlaneXY | GizmoPart::Center
            )
        }
        ToolMode::Rotate => matches!(part, GizmoPart::AxisZ),
        _ => false,
    };
    if valid { Some(part) } else { None }
}

/// 向き付きギズモ（オブジェクトのローカル回転軸）向けの汎用ヒットテスト。
///
/// `hit_test_gizmo_canvas` と異なり、2D キャンバス用のパーツ制限（AxisZ・XZ/YZ 平面の除外）
/// を行わない。gizmo_space = Local の通常 3D アクター向けに、全軸・全平面ハンドルを
/// そのままローカル回転軸 [ax, ay, az] に沿ってヒットテストする。
pub fn hit_test_gizmo_oriented(
    ray_o: [f32; 3],
    ray_d: [f32; 3],
    gizmo_pos: [f32; 3],
    radius: f32,
    tool: ToolMode,
    ax: [f32; 3],
    ay: [f32; 3],
    az: [f32; 3],
) -> Option<GizmoPart> {
    // レイをローカル回転軸空間に変換する（gizmo_pos が原点）
    let d = sub3(ray_o, gizmo_pos);
    let local_o = [dot3(d, ax), dot3(d, ay), dot3(d, az)];
    let local_d = [dot3(ray_d, ax), dot3(ray_d, ay), dot3(ray_d, az)];
    hit_test_gizmo(local_o, local_d, [0.0, 0.0, 0.0], radius, tool)
}

/// 向き付きギズモ（3D Canvas 子アクター / Local 座標モード）向けのドラッグ開始状態を生成する。
///
/// レイを ax/ay/az ローカル空間に変換してから start_drag を呼び出し、
/// ref_point と plane_normal をワールド空間に戻して GizmoDrag に格納する。
/// `axes = Some([ax, ay, az])` が設定されるため update_drag が向き付き軸を使用する。
/// パーツの有効/無効フィルタは行わないため、3D Canvas 子（2D 平面限定）・
/// Local 通常アクター（全軸）の両方から共通で使用できる。
///
/// `full_axes`: true = Local 座標モードの全 3 軸ギズモ（Center ハンドルは ax/ay/az 均等スケール）、
/// false = 3D Canvas 子アクターの 2 軸限定ギズモ（Center ハンドルは ax/ay 面内のみスケール、
/// 従来どおりの挙動を維持）。
pub fn start_drag_oriented(
    part: GizmoPart,
    tool: ToolMode,
    ray_o: [f32; 3],
    ray_d: [f32; 3],
    gizmo_pos: [f32; 3],
    radius: f32,
    start_mat: [[f32; 4]; 4],
    ax: [f32; 3],
    ay: [f32; 3],
    az: [f32; 3],
    full_axes: bool,
) -> GizmoDrag {
    // レイをキャンバスローカル空間に変換（gizmo_pos = 原点）
    let d = sub3(ray_o, gizmo_pos);
    let local_o = [dot3(d, ax), dot3(d, ay), dot3(d, az)];
    let local_d = [dot3(ray_d, ax), dot3(ray_d, ay), dot3(ray_d, az)];
    let local_drag = start_drag(
        part,
        tool,
        local_o,
        local_d,
        [0.0, 0.0, 0.0],
        radius,
        start_mat,
    );
    // ref_point をキャンバスローカル→ワールド空間に変換する。
    // 【重要】Rotate の ref_point は start_drag 内で正規化済みの「方向ベクトル」であり
    // 絶対座標ではない（update_drag の Rotate 分岐が dot3/cross3 で角度計算にそのまま使う）。
    // Move/Scale/Center の ref_point は局所原点からのオフセット（絶対点）のため
    // gizmo_pos を加算して世界座標へ戻す必要があるが、Rotate は方向のみを基底変換すればよく
    // gizmo_pos を加算すると大きさ・原点がずれて角度計算が破綻する。
    let rp = local_drag.ref_point;
    let rp_world_lin = add3(
        add3(scale3(ax, rp[0]), scale3(ay, rp[1])),
        scale3(az, rp[2]),
    );
    let ref_w = if tool == ToolMode::Rotate {
        rp_world_lin
    } else {
        add3(rp_world_lin, gizmo_pos)
    };
    // plane_normal も変換（Center ハンドル用）
    let pn = local_drag.plane_normal;
    let pn_w = if pn[0] != 0.0 || pn[1] != 0.0 || pn[2] != 0.0 {
        add3(
            add3(scale3(ax, pn[0]), scale3(ay, pn[1])),
            scale3(az, pn[2]),
        )
    } else {
        [0.0; 3]
    };
    GizmoDrag {
        ref_point: ref_w,
        plane_normal: pn_w,
        gizmo_pos,
        axes: Some([ax, ay, az]),
        full_axes,
        ..local_drag
    }
}
