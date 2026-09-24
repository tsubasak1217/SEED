// ============================================================
//  platform/screen/snapshot.rs — スクリプトへ見せる画面情報（フレームごとの写し）
//
//  【役割】
//  スクリプトの `SEED.Screen`（Width / Height / SafeArea / Orientation / DPI）が返す値を、
//  フレームごとに 1 回だけ作る【純関数】。入力は App が集めた「描画ターゲットの寸法・描画面の実寸・
//  レターボックスの有無・OS の報告・表示倍率」で、ここはファイルも OS も触らない（単体テスト対象）。
//
//  【座標系】
//  すべて `Input.MousePos` と同じ「描画ターゲットの左上原点・Y 下向き・ピクセル」。
//  OS の安全領域は描画面（ウィンドウ＝Android では画面全体）の物理ピクセルで届くので、
//    - ウィンドウに合わせて描く（既定）… 描画ターゲット＝描画面なのでそのまま
//    - 内部解像度固定（レターボックス）… 入力と同じ写像（renderer/letterbox.rs の window_to_internal）で
//                                         内部解像度の座標へ移し、描画ターゲットの外（黒帯）は切り落とす
//  とする。どちらの場合も安全領域は描画ターゲットの矩形 [0, Width] x [0, Height] の内側に収まる。
//
//  【1 フレーム内で値が変わらないこと】
//  OS の報告は別スレッドからいつでも届くが、スクリプトが読むのはここで作った写しだけで、
//  写しはフレームの決まった位置（スクリプトのフェーズより前）で 1 回だけ差し替える
//  （core/scripting/screen_bridge.rs・app/screen_publish.rs）。
// ============================================================

use crate::engine::core::renderer::letterbox;

use super::orientation::{orientation_from_aspect, orientation_from_rotation, ScreenOrientation};
use super::report::{EdgeInsets, ScreenReport};

/// 描画ターゲット座標系の矩形（左上原点・Y 下向き・ピクセル）。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ScreenRect {
    /// 左上の X。
    pub x: f32,
    /// 左上の Y。
    pub y: f32,
    /// 幅（0 以上）。
    pub width: f32,
    /// 高さ（0 以上）。
    pub height: f32,
}

impl ScreenRect {
    /// 原点から指定の大きさまでの矩形（全画面）。
    pub const fn full(width: f32, height: f32) -> Self {
        Self { x: 0.0, y: 0.0, width, height }
    }
}

/// スクリプトへ見せる画面情報 1 フレーム分。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ScreenSnapshot {
    /// 描画ターゲットの幅（ピクセル。`Input.MousePos` と同じ単位）。
    pub width: f32,
    /// 描画ターゲットの高さ（ピクセル）。
    pub height: f32,
    /// 安全領域（描画ターゲット座標。安全領域が無い環境では全画面）。
    pub safe_area: ScreenRect,
    /// 画面の向き。
    pub orientation: ScreenOrientation,
    /// OS が報告する論理 DPI（表示倍率 × 基準 DPI）。
    pub dpi: f32,
}

/// フレームの画面情報を作るための入力（App が集める）。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SnapshotInputs {
    /// 描画ターゲットの寸法（ピクセル。`App::render_target_size_px` と同じ値）。
    pub target_size: [f32; 2],
    /// 描画面（ウィンドウのクライアント領域・Android のサーフェス）の実寸（物理ピクセル）。
    /// ウィンドウがまだ無いなど分からなければ None。
    pub window_size: Option<(u32, u32)>,
    /// 内部解像度固定（レターボックス表示）が効いているときの内部解像度。効いていなければ None。
    pub letterbox_internal: Option<(u32, u32)>,
    /// この描画面の大きさについての OS の報告（`report::select_for_frame`）。無ければ None。
    pub report: Option<ScreenReport>,
    /// OS の表示倍率（winit の `Window::scale_factor`）。分からなければ None。
    pub scale_factor: Option<f64>,
    /// 表示倍率 1.0 に当たる DPI（`PlatformTraits::reference_dpi`）。
    pub reference_dpi: f32,
}

impl ScreenSnapshot {
    /// 報告もウィンドウも無いときの値（最初の公開より前にスクリプトが読んだ場合など）。
    ///
    /// 全画面が安全領域・向きは縦横比・DPI は基準値。
    pub fn fallback(target_size: [f32; 2], reference_dpi: f32) -> Self {
        let [width, height] = target_size;
        Self {
            width,
            height,
            safe_area: ScreenRect::full(width, height),
            orientation: orientation_from_aspect(to_pixels(width), to_pixels(height)),
            dpi: reference_dpi,
        }
    }

    /// 入力からこのフレームの画面情報を作る【純関数】。
    pub fn compute(inputs: &SnapshotInputs) -> Self {
        let [width, height] = inputs.target_size;

        // 安全領域: 報告と描画面の実寸がそろっているときだけ OS の値を使い、それ以外は全画面。
        let safe_area = match (inputs.report, inputs.window_size) {
            (Some(report), Some(window)) => safe_area_in_target(
                &report.insets,
                window,
                inputs.target_size,
                inputs.letterbox_internal,
            ),
            _ => ScreenRect::full(width, height),
        };

        // 向き: 報告があれば回転と表示の自然な向きから、無ければ描画面の縦横比から。
        // 安全領域と同じ報告から決める（1 フレームの中で安全領域と向きが別々の時点の値にならないように）。
        let orientation = match (inputs.report, inputs.window_size) {
            (Some(report), _) => orientation_from_rotation(
                report.rotation_quarter_turns,
                report.natural_width,
                report.natural_height,
            ),
            (None, Some((window_w, window_h))) => orientation_from_aspect(window_w, window_h),
            (None, None) => orientation_from_aspect(to_pixels(width), to_pixels(height)),
        };

        Self {
            width,
            height,
            safe_area,
            orientation,
            dpi: dpi_from_scale_factor(inputs.scale_factor, inputs.reference_dpi),
        }
    }
}

/// 表示倍率から論理 DPI を求める【純関数】。倍率が分からない・不正（0 以下・NaN）なら基準 DPI。
pub fn dpi_from_scale_factor(scale_factor: Option<f64>, reference_dpi: f32) -> f32 {
    match scale_factor {
        Some(scale) if scale.is_finite() && scale > 0.0 => (scale * f64::from(reference_dpi)) as f32,
        _ => reference_dpi,
    }
}

/// 描画面の各辺からの距離（物理ピクセル）を、描画ターゲット座標系の安全領域の矩形へ写す【純関数】。
///
/// # 引数
/// * `insets` - 安全領域の、描画面の各辺からの距離
/// * `window` - 描画面の実寸（物理ピクセル）
/// * `target` - 描画ターゲットの寸法（結果はこの矩形の内側へ切り詰める）
/// * `letterbox_internal` - 内部解像度固定のときの内部解像度（Some なら入力と同じレターボックス写像を通す）
///
/// 左右（上下）の距離の和が描画面より大きい壊れた値でも、幅・高さは負にならない（0 に潰れる）。
pub fn safe_area_in_target(
    insets: &EdgeInsets,
    window: (u32, u32),
    target: [f32; 2],
    letterbox_internal: Option<(u32, u32)>,
) -> ScreenRect {
    let (window_w, window_h) = window;
    // 描画面（ウィンドウ座標）での安全領域の左上・右下。右下が左上より手前に来ないようにそろえる。
    let left = insets.left.min(window_w);
    let top = insets.top.min(window_h);
    let right = window_w.saturating_sub(insets.right).max(left);
    let bottom = window_h.saturating_sub(insets.bottom).max(top);
    let window_min = [left as f32, top as f32];
    let window_max = [right as f32, bottom as f32];

    // 描画ターゲット座標へ写す（レターボックスが無ければ描画ターゲット＝描画面なので恒等）。
    let (target_min, target_max) = match letterbox_internal {
        Some(internal) => (
            letterbox::window_to_internal(window_min, window, internal).0,
            letterbox::window_to_internal(window_max, window, internal).0,
        ),
        None => (window_min, window_max),
    };

    // 黒帯の上にかかった分は描画ターゲットの外なので切り落とす
    // （寸法が壊れていても clamp の上下限が逆転して panic しないよう、先に 0 以上の有限値へそろえる）。
    let target_w = finite_non_negative(target[0]);
    let target_h = finite_non_negative(target[1]);
    let x0 = target_min[0].clamp(0.0, target_w);
    let y0 = target_min[1].clamp(0.0, target_h);
    let x1 = target_max[0].clamp(x0, target_w.max(x0));
    let y1 = target_max[1].clamp(y0, target_h.max(y0));
    ScreenRect { x: x0, y: y0, width: x1 - x0, height: y1 - y0 }
}

/// 寸法（ピクセル・f32）を縦横比の判定用の整数へ（負・NaN は 0）。
fn to_pixels(value: f32) -> u32 {
    finite_non_negative(value) as u32
}

/// 寸法を 0 以上の有限値へそろえる（負・NaN・無限大は 0）。
fn finite_non_negative(value: f32) -> f32 {
    if value.is_finite() && value > 0.0 { value } else { 0.0 }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 浮動小数の比較の許容誤差（ピクセル）。
    const EPS: f32 = 1e-3;

    /// Android の基準 DPI（表示倍率 1.0 = mdpi = 160）。
    const ANDROID_REFERENCE_DPI: f32 = 160.0;

    /// Pixel 6 の表示の自然な向きの大きさ（縦持ち）。
    const NATURAL: (u32, u32) = (1080, 2400);

    /// Pixel 6 相当の縦画面（1080x2400）・上に穴・下にジェスチャーバー。
    fn portrait_report() -> ScreenReport {
        ScreenReport {
            frame_width: 1080,
            frame_height: 2400,
            insets: EdgeInsets { left: 0, top: 136, right: 0, bottom: 63 },
            rotation_quarter_turns: 0,
            natural_width: NATURAL.0,
            natural_height: NATURAL.1,
        }
    }

    fn assert_rect(got: ScreenRect, want: (f32, f32, f32, f32)) {
        let ok = (got.x - want.0).abs() <= EPS
            && (got.y - want.1).abs() <= EPS
            && (got.width - want.2).abs() <= EPS
            && (got.height - want.3).abs() <= EPS;
        assert!(ok, "rect mismatch: got={got:?} want={want:?}");
    }

    fn inputs_for(report: Option<ScreenReport>, window: (u32, u32)) -> SnapshotInputs {
        SnapshotInputs {
            target_size: [window.0 as f32, window.1 as f32],
            window_size: Some(window),
            letterbox_internal: None,
            report,
            scale_factor: Some(2.625),
            reference_dpi: ANDROID_REFERENCE_DPI,
        }
    }

    /// ウィンドウに合わせて描くとき、安全領域は描画面の値そのまま（穴の辺とジェスチャーバーの辺だけ内側に寄る）。
    #[test]
    fn window_mode_uses_insets_directly() {
        let snap = ScreenSnapshot::compute(&inputs_for(Some(portrait_report()), (1080, 2400)));
        assert_eq!((snap.width, snap.height), (1080.0, 2400.0));
        assert_rect(snap.safe_area, (0.0, 136.0, 1080.0, 2400.0 - 136.0 - 63.0));
        assert_eq!(snap.orientation, ScreenOrientation::Portrait);
        // 表示倍率 2.625 × 160 = 420（Pixel 6 の densityDpi）
        assert!((snap.dpi - 420.0).abs() <= EPS, "dpi={}", snap.dpi);
    }

    /// 横向き（穴が左・ジェスチャーバーが下）。
    #[test]
    fn landscape_report_moves_cutout_to_the_left_edge() {
        let report = ScreenReport {
            frame_width: 2400,
            frame_height: 1080,
            insets: EdgeInsets { left: 136, top: 0, right: 0, bottom: 63 },
            rotation_quarter_turns: 1,
            natural_width: NATURAL.0,
            natural_height: NATURAL.1,
        };
        let snap = ScreenSnapshot::compute(&inputs_for(Some(report), (2400, 1080)));
        assert_rect(snap.safe_area, (136.0, 0.0, 2400.0 - 136.0, 1080.0 - 63.0));
        assert_eq!(snap.orientation, ScreenOrientation::LandscapeLeft);
    }

    /// 逆さの縦（回転 2）: 穴とジェスチャーバーは下端に重なり、向きは PortraitUpsideDown
    /// （エミュレータ Pixel 6 の実測値: 下端 128）。
    #[test]
    fn upside_down_report_moves_cutout_to_the_bottom() {
        let report = ScreenReport {
            insets: EdgeInsets { left: 0, top: 0, right: 0, bottom: 128 },
            rotation_quarter_turns: 2,
            ..portrait_report()
        };
        let snap = ScreenSnapshot::compute(&inputs_for(Some(report), (1080, 2400)));
        assert_rect(snap.safe_area, (0.0, 0.0, 1080.0, 2272.0));
        assert_eq!(snap.orientation, ScreenOrientation::PortraitUpsideDown);
    }

    /// 報告が無い（デスクトップ・Android で最初の報告前）なら全画面・向きは縦横比から。
    #[test]
    fn no_report_means_full_screen_and_aspect_orientation() {
        let snap = ScreenSnapshot::compute(&inputs_for(None, (540, 960)));
        assert_rect(snap.safe_area, (0.0, 0.0, 540.0, 960.0));
        assert_eq!(snap.orientation, ScreenOrientation::Portrait);

        let wide = ScreenSnapshot::compute(&inputs_for(None, (1920, 1080)));
        assert_eq!(wide.orientation, ScreenOrientation::LandscapeLeft);
    }

    /// 内部解像度固定（縦の画面 1080x2400 に横長 1920x1080 をレターボックス表示）。
    /// 上下に大きな黒帯ができ、穴（上 136）とジェスチャーバー（下 63）は黒帯の中に収まるので、
    /// 安全領域は描画ターゲット全体（内部解像度 1920x1080）になる。
    #[test]
    fn letterbox_bars_absorb_insets() {
        let inputs = SnapshotInputs {
            target_size: [1920.0, 1080.0],
            letterbox_internal: Some((1920, 1080)),
            ..inputs_for(Some(portrait_report()), (1080, 2400))
        };
        let snap = ScreenSnapshot::compute(&inputs);
        assert_eq!((snap.width, snap.height), (1920.0, 1080.0));
        assert_rect(snap.safe_area, (0.0, 0.0, 1920.0, 1080.0));
        // 向きは描画面（端末）の向き。内部解像度が横長でも Portrait のまま。
        assert_eq!(snap.orientation, ScreenOrientation::Portrait);
    }

    /// 内部解像度固定で、穴が映像部分にかかる場合は内部解像度の座標へ縮めて写す。
    /// 横画面 2400x1080 に 1200x540（ちょうど 1/2）を表示 → 帯なし・拡大率 2。左の穴 136px は内部 68px。
    #[test]
    fn letterbox_scales_insets_into_internal_resolution() {
        let report = ScreenReport {
            frame_width: 2400,
            frame_height: 1080,
            insets: EdgeInsets { left: 136, top: 0, right: 0, bottom: 64 },
            rotation_quarter_turns: 1,
            natural_width: NATURAL.0,
            natural_height: NATURAL.1,
        };
        let inputs = SnapshotInputs {
            target_size: [1200.0, 540.0],
            letterbox_internal: Some((1200, 540)),
            ..inputs_for(Some(report), (2400, 1080))
        };
        let snap = ScreenSnapshot::compute(&inputs);
        assert_rect(snap.safe_area, (68.0, 0.0, 1200.0 - 68.0, 540.0 - 32.0));
    }

    /// 壊れた距離（左右の和が描画面を超える）でも幅・高さは負にならない。
    #[test]
    fn oversized_insets_collapse_to_zero_size() {
        let insets = EdgeInsets { left: 700, top: 0, right: 700, bottom: 3000 };
        let rect = safe_area_in_target(&insets, (1080, 2400), [1080.0, 2400.0], None);
        assert!(rect.width >= 0.0 && rect.height >= 0.0, "{rect:?}");
        assert!(rect.x <= 1080.0 && rect.y <= 2400.0, "{rect:?}");
        assert_eq!(rect.width, 0.0);
        assert_eq!(rect.height, 0.0);
    }

    /// 表示倍率が分からない・不正なら基準 DPI。デスクトップ（基準 96）の 150% 表示は 144。
    #[test]
    fn dpi_falls_back_to_reference() {
        assert_eq!(dpi_from_scale_factor(None, 96.0), 96.0);
        assert_eq!(dpi_from_scale_factor(Some(0.0), 96.0), 96.0);
        assert_eq!(dpi_from_scale_factor(Some(f64::NAN), 96.0), 96.0);
        assert!((dpi_from_scale_factor(Some(1.5), 96.0) - 144.0).abs() <= EPS);
    }

    /// 最初の公開前の値: 全画面・縦横比の向き・基準 DPI。
    #[test]
    fn fallback_is_full_screen() {
        let snap = ScreenSnapshot::fallback([1280.0, 720.0], 96.0);
        assert_rect(snap.safe_area, (0.0, 0.0, 1280.0, 720.0));
        assert_eq!(snap.orientation, ScreenOrientation::LandscapeLeft);
        assert_eq!(snap.dpi, 96.0);
    }
}
