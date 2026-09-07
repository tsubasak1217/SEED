// ============================================================
//  camera_project.rs — カメラのワールド→スクリーン射影（スクリプト API 用）
//
//  SEED.Camera.WorldToScreen / WorldToCanvas の計算本体。
//
//  【なぜ独立モジュールか】
//  host_api.rs は「FFI の受け口」であって座標計算の置き場ではない。射影の中身は
//  行列だけで完結する純粋な計算なので、World も FFI も知らない層として切り出し、
//  ユニットテストで数値検証できるようにしている（単一責任原則）。
//
//  【描画と同じ行列を組む】
//  ビュー・射影・ビューポート矩形は frame_renderer.rs のメインカメラ経路
//  （compute_game_viewport → look_at_lh → perspective_lh / orthographic_lh）と
//  **同一の式**で組む。ここがずれると「画面に見えている位置」と API の返り値が
//  食い違うため、frame_renderer 側を変更したら必ずこちらも合わせること。
// ============================================================

use crate::engine::components::{CameraComponent, CameraProjection, Transform};
use crate::engine::structs::tensor::{Mat4x4, Vector3, Vector4};

// ─── 定数 ───────────────────────────────────────────────────

/// レンダーターゲットサイズが取得できないときのフォールバック幅（px）。
/// frame_renderer.rs の同名フォールバックと同じ値にすること。
pub const FALLBACK_TARGET_WIDTH: f32 = 1280.0;
/// レンダーターゲットサイズが取得できないときのフォールバック高さ（px）。
pub const FALLBACK_TARGET_HEIGHT: f32 = 720.0;

/// 正射投影の縦幅（ortho_height）の下限。0 除算と行列の縮退を防ぐ。
/// frame_renderer.rs の ortho_height.max(0.01) と同じ値。
const ORTHO_HEIGHT_MIN: f32 = 0.01;
/// 正射投影の near の下限（frame_renderer.rs と同じ）。
const ORTHO_NEAR_MIN: f32 = 0.01;
/// 正射投影の far が near から最低限離れているべき距離（frame_renderer.rs と同じ）。
const ORTHO_FAR_MARGIN: f32 = 0.1;

/// ビューポート幅・高さの最小値（px）。0 は以降の除算・射影を壊す。
const VIEWPORT_MIN_SIZE: f32 = 1.0;

/// クリップ空間 w が「カメラ平面上（＝射影が定義できない）」とみなす閾値。
const CLIP_W_EPS: f32 = 1e-6;

// ─── ビューポート矩形 ────────────────────────────────────────

/// ゲーム描画に使われるビューポート矩形（ピクセル・左上原点）。
///
/// LetterBox / PillarBox では帯のぶんだけ原点がずれ、サイズが縮む。
/// compute_game_viewport の戻り値 (vp_x, vp_y, vp_w, vp_h) に対応する。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ViewportRect {
    /// レンダーターゲット左端からの X オフセット（px）
    pub x: f32,
    /// レンダーターゲット上端からの Y オフセット（px）
    pub y: f32,
    /// 幅（px）
    pub w: f32,
    /// 高さ（px）
    pub h: f32,
}

// ─── 射影結果 ───────────────────────────────────────────────

/// ワールド座標をスクリーンへ射影した結果。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ScreenPoint {
    /// レンダーターゲット左上原点のスクリーン座標（px・右が +X / 下が +Y）。
    /// SEED.Input.MousePos と同じ座標系。
    pub screen: [f32; 2],
    /// カメラ前方距離（ビュー空間 Z・ワールド単位）。
    /// **正ならカメラの前方**、負なら背後。0 はカメラ平面上。
    pub depth: f32,
}

// ─── ビューポート計算 ───────────────────────────────────────

/// カメラのスケーリングモードから、ゲームビューポート矩形・射影アスペクト比・
/// 実効 FOV を求める。
///
/// app_base::app::canvas_collect::compute_game_viewport の薄いラッパー。
/// 描画と同じ矩形を使うことを型で明示するためにここへ寄せている。
pub fn game_viewport_of(
    camera:   &CameraComponent,
    target_w: f32,
    target_h: f32,
) -> (ViewportRect, f32, f32) {
    let (vp_x, vp_y, vp_w, vp_h, proj_aspect, fov_y_rad) =
        crate::engine::core::app_base::app::canvas_collect::compute_game_viewport(
            &camera.scaling_mode,
            target_w,
            target_h,
            camera.target_width,
            camera.target_height,
            camera.fov_y_deg,
        );
    // 幅・高さ 0 は以降の除算を壊すので最低 1px を保証する（描画側と同じ扱い）。
    let rect = ViewportRect {
        x: vp_x,
        y: vp_y,
        w: vp_w.max(VIEWPORT_MIN_SIZE),
        h: vp_h.max(VIEWPORT_MIN_SIZE),
    };
    (rect, proj_aspect, fov_y_rad)
}

// ─── 行列生成 ───────────────────────────────────────────────

/// カメラの Transform（ワールド）からビュー行列を組む。
///
/// SEED の Transform はワールド空間保持なので、親子合成は不要
/// （transform_sync.rs が代入時に子孫へ伝播済み）。
pub fn view_matrix_of(camera_tf: &Transform) -> Mat4x4<f32> {
    let [px, py, pz] = camera_tf.position;
    let [fx, fy, fz] = camera_tf.forward();
    let [ux, uy, uz] = camera_tf.up();
    let pos    = Vector3::new(px, py, pz);
    let target = pos + Vector3::new(fx, fy, fz);
    let up_vec = Vector3::new(ux, uy, uz);
    Mat4x4::look_at_lh(pos, target, up_vec)
}

/// カメラの投影方式に応じた射影行列を組む（frame_renderer.rs と同一式）。
pub fn projection_matrix_of(
    camera:      &CameraComponent,
    proj_aspect: f32,
    fov_y_rad:   f32,
) -> Mat4x4<f32> {
    match camera.projection {
        CameraProjection::Perspective => {
            Mat4x4::perspective_lh(fov_y_rad, proj_aspect, camera.near, camera.far)
        }
        CameraProjection::Orthographic => {
            let half_h = camera.ortho_height.max(ORTHO_HEIGHT_MIN) * 0.5;
            let half_w = half_h * proj_aspect;
            Mat4x4::orthographic_lh(
                -half_w, half_w, -half_h, half_h,
                camera.near.max(ORTHO_NEAR_MIN),
                camera.far.max(camera.near + ORTHO_FAR_MARGIN),
            )
        }
    }
}

// ─── 射影本体（純関数）─────────────────────────────────────

/// ビュー行列・射影行列・ビューポート矩形からワールド座標をスクリーンへ射影する。
///
/// # 戻り値
/// - Some(ScreenPoint): 射影できた（カメラ背後の点も含む。判定は depth の符号で行う）。
/// - None: クリップ w がほぼ 0 で射影が定義できない（点がカメラ平面上にある）。
///
/// # カメラ背後の扱い
/// 透視投影ではクリップ w = ビュー空間 Z なので、背後の点は w < 0 となり
/// NDC が符号反転して「画面内にあるように見える」座標を返してしまう。
/// そのため depth（ビュー空間 Z）を必ず併せて返し、利用側が
/// depth > 0 で前方判定できるようにしている。
pub fn project_world_point(
    view:  &Mat4x4<f32>,
    proj:  &Mat4x4<f32>,
    vp:    ViewportRect,
    world: [f32; 3],
) -> Option<ScreenPoint> {
    // ① ワールド → ビュー空間（Z がカメラ前方距離になる）
    let world_v = Vector4::new(world[0], world[1], world[2], 1.0);
    let view_v  = *view * world_v;
    let depth   = view_v.z;

    // ② ビュー → クリップ空間
    let clip = *proj * view_v;
    if clip.w.abs() <= CLIP_W_EPS {
        return None; // カメラ平面上：射影が定義できない
    }

    // ③ クリップ → NDC（x,y は [-1, 1]、Y は上が +1）
    let ndc_x = clip.x / clip.w;
    let ndc_y = clip.y / clip.w;

    // ④ NDC → ビューポート内ピクセル（左上原点・Y 下向き）
    //    ビューポート矩形のオフセットを足すことで、LetterBox / PillarBox の
    //    帯を考慮した「実際に描かれている位置」になる。
    let screen_x = vp.x + (ndc_x * 0.5 + 0.5) * vp.w;
    let screen_y = vp.y + (0.5 - ndc_y * 0.5) * vp.h;

    Some(ScreenPoint { screen: [screen_x, screen_y], depth })
}

/// スクリーン座標（左上原点）をキャンバス座標（レンダーターゲット中央原点・
/// Y 下向き・1 単位 = 1px）へ変換する。
///
/// SEED.Input.MousePositionCanvas および CanvasTransform.Position と同じ座標系。
/// 変換基準はビューポート矩形ではなく**レンダーターゲット全体**である
/// （スクリーンスペースキャンバスはウィンドウ全体に張られるため。
///  physics2d_ops::collect_2d_screen_positions の +win/2 と対称）。
pub fn screen_to_canvas(screen: [f32; 2], target_w: f32, target_h: f32) -> [f32; 2] {
    [screen[0] - target_w * 0.5, screen[1] - target_h * 0.5]
}

/// カメラコンポーネント・その Transform・レンダーターゲットサイズから
/// ワールド座標を射影する（WorldToScreen / WorldToCanvas の共通経路）。
pub fn world_to_screen(
    camera:    &CameraComponent,
    camera_tf: &Transform,
    target_w:  f32,
    target_h:  f32,
    world:     [f32; 3],
) -> Option<ScreenPoint> {
    let (vp, proj_aspect, fov_y_rad) = game_viewport_of(camera, target_w, target_h);
    let view = view_matrix_of(camera_tf);
    let proj = projection_matrix_of(camera, proj_aspect, fov_y_rad);
    project_world_point(&view, &proj, vp, world)
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用ビューポート（1280x720 全面）。
    const VP: ViewportRect = ViewportRect { x: 0.0, y: 0.0, w: 1280.0, h: 720.0 };

    /// 原点を +Z 方向から見る標準的なカメラのビュー行列。
    fn test_view() -> Mat4x4<f32> {
        Mat4x4::look_at_lh(
            Vector3::new(0.0, 0.0, -10.0), // カメラは -Z 側
            Vector3::new(0.0, 0.0, 0.0),   // 原点を見る
            Vector3::new(0.0, 1.0, 0.0),
        )
    }

    /// 60 度・16:9 の透視射影行列。
    fn test_proj() -> Mat4x4<f32> {
        Mat4x4::perspective_lh(60.0_f32.to_radians(), 1280.0 / 720.0, 0.1, 1000.0)
    }

    /// 浮動小数の近似比較。
    fn approx(a: f32, b: f32, eps: f32) -> bool { (a - b).abs() <= eps }

    /// カメラが真っ直ぐ見ている点は画面中央に来る。
    #[test]
    fn center_point_maps_to_screen_center() {
        let p = project_world_point(&test_view(), &test_proj(), VP, [0.0, 0.0, 0.0]).unwrap();
        assert!(approx(p.screen[0], 640.0, 1e-3), "x={}", p.screen[0]);
        assert!(approx(p.screen[1], 360.0, 1e-3), "y={}", p.screen[1]);
        // カメラは z=-10、点は z=0 なので前方 10 単位
        assert!(approx(p.depth, 10.0, 1e-3), "depth={}", p.depth);
    }

    /// ワールドで上（+Y）にある点は、スクリーンでは中央より上（Y が小さい）へ行く。
    /// スクリーン座標が Y 下向きであることの検証。
    #[test]
    fn upward_point_maps_above_center() {
        let p = project_world_point(&test_view(), &test_proj(), VP, [0.0, 1.0, 0.0]).unwrap();
        assert!(approx(p.screen[0], 640.0, 1e-3));
        assert!(p.screen[1] < 360.0, "y={} should be above center", p.screen[1]);
    }

    /// ワールドで右（+X）にある点は、スクリーンでも中央より右へ行く。
    #[test]
    fn rightward_point_maps_right_of_center() {
        let p = project_world_point(&test_view(), &test_proj(), VP, [1.0, 0.0, 0.0]).unwrap();
        assert!(p.screen[0] > 640.0, "x={} should be right of center", p.screen[0]);
        assert!(approx(p.screen[1], 360.0, 1e-3));
    }

    /// カメラ背後の点は depth が負になる。
    /// NDC 上は画面内に見える座標を返しうるので、前方判定は必ず depth で行う。
    #[test]
    fn behind_camera_point_has_negative_depth() {
        // カメラは z=-10 で +Z を向いている。z=-20 はカメラの後ろ。
        let p = project_world_point(&test_view(), &test_proj(), VP, [0.0, 0.0, -20.0]).unwrap();
        assert!(p.depth < 0.0, "depth={} should be negative", p.depth);
    }

    /// ビューポートにオフセット（LetterBox の帯）があると、中央点も帯のぶんずれる。
    #[test]
    fn letterbox_offset_shifts_screen_position() {
        // 上下に 60px ずつ帯が入り、描画領域は y=60..660 の 600px
        let vp = ViewportRect { x: 0.0, y: 60.0, w: 1280.0, h: 600.0 };
        let p = project_world_point(&test_view(), &test_proj(), vp, [0.0, 0.0, 0.0]).unwrap();
        assert!(approx(p.screen[0], 640.0, 1e-3));
        assert!(approx(p.screen[1], 60.0 + 300.0, 1e-3), "y={}", p.screen[1]);
    }

    /// スクリーン座標 → キャンバス座標（中央原点）の対応。
    #[test]
    fn screen_to_canvas_uses_target_center_as_origin() {
        assert_eq!(screen_to_canvas([640.0, 360.0], 1280.0, 720.0), [0.0, 0.0]);
        assert_eq!(screen_to_canvas([0.0, 0.0], 1280.0, 720.0), [-640.0, -360.0]);
        assert_eq!(screen_to_canvas([1280.0, 720.0], 1280.0, 720.0), [640.0, 360.0]);
    }

    /// 正射投影でも中央点は画面中央に来る。
    #[test]
    fn orthographic_center_point_maps_to_screen_center() {
        let proj = Mat4x4::orthographic_lh(-8.0, 8.0, -4.5, 4.5, 0.1, 1000.0);
        let p = project_world_point(&test_view(), &proj, VP, [0.0, 0.0, 0.0]).unwrap();
        assert!(approx(p.screen[0], 640.0, 1e-3));
        assert!(approx(p.screen[1], 360.0, 1e-3));
        assert!(approx(p.depth, 10.0, 1e-3));
    }
}
