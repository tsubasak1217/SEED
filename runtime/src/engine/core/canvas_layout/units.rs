// ============================================================
//  canvas_layout/units.rs — 端末に依らない単位 dp と、走査が読む画面の情報（W2-1b）
//
//  【dp】1 dp = 表示倍率の画素。表示倍率 = 端末の DPI ÷ `PlatformTraits::reference_dpi`
//  （= winit の scale_factor。Android は densityDpi ÷ 160、Windows は OS の表示スケール ÷ 96 dpi）。
//  ルートキャンバスの単位を dp にすると（CanvasComponent.unit）、ルートの大きさは「画面の画素 ÷ 1 dp の画素数」
//  になり、子の位置・大きさ・余白は縦横同じ倍率（dp_scale）で画素へ換算される（placement.rs の resolve）。
//
//  【画面の情報】`CanvasScreenEnv` は走査全体の入力（CanvasLayoutEnv.screen）で、App がフレームごとに
//  画面の写し（platform/screen の ScreenSnapshot）から作る（app/canvas_screen_env.rs）。
//    - dp_scale  … 1 dp の画素数（Play: 表示倍率。エディタの Edit・設計空間の表示: 1）
//    - safe_area … 安全領域（2D キャンバスのワールド座標＝オーバーレイの座標。None = 縮めない）
//  どちらも既存のシーン（px のルート・安全領域の部品なし）の結果には影響しない。
// ============================================================

use super::safe_area::CanvasRect;

/// dp の換算が使えないとき（DPI が 0・NaN など）の 1 dp の画素数。
pub const FALLBACK_DP_SCALE: f32 = 1.0;

/// 走査全体で変わらない画面の情報（dp の倍率と安全領域）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CanvasScreenEnv {
    /// 1 dp の画素数（0 より大きい有限値）。
    pub dp_scale: f32,
    /// 安全領域（2D キャンバスのワールド座標。画面の中央が原点・Y 下向き）。None = 縮めない。
    pub safe_area: Option<CanvasRect>,
}

impl CanvasScreenEnv {
    /// 画面の情報を使わない文脈（エディタの Edit・設計空間の表示・アクター編集タブ・3D ワールドキャンバス・テスト）。
    pub const NONE: Self = Self { dp_scale: FALLBACK_DP_SCALE, safe_area: None };

    /// 1 dp の画素数（壊れた値は既定へ戻す）。
    pub fn dp_scale(&self) -> f32 {
        sanitize_dp_scale(self.dp_scale)
    }
}

impl Default for CanvasScreenEnv {
    fn default() -> Self {
        Self::NONE
    }
}

/// 1 dp の画素数を 0 より大きい有限値へそろえる【純関数】（それ以外は FALLBACK_DP_SCALE）。
pub fn sanitize_dp_scale(scale: f32) -> f32 {
    if scale.is_finite() && scale > 0.0 { scale } else { FALLBACK_DP_SCALE }
}

/// 端末の DPI と基準 DPI から 1 dp の画素数を求める【純関数】（= 表示倍率。壊れた値は既定）。
///
/// # 引数
/// * `dpi`           - 端末の論理 DPI（`Screen.DPI`。Android は densityDpi、Windows は 96 × 表示スケール）
/// * `reference_dpi` - 表示倍率 1.0 に当たる DPI（`PlatformTraits::reference_dpi`）
pub fn dp_scale_from_dpi(dpi: f32, reference_dpi: f32) -> f32 {
    if !(reference_dpi.is_finite() && reference_dpi > 0.0) {
        return FALLBACK_DP_SCALE;
    }
    sanitize_dp_scale(dpi / reference_dpi)
}

/// dp のルートキャンバスの大きさ（dp）を求める【純関数】: 基準ビューポート（画素）÷ 1 dp の画素数。
pub fn dp_canvas_size(viewport_px: [f32; 2], dp_scale: f32) -> [f32; 2] {
    let scale = sanitize_dp_scale(dp_scale);
    [viewport_px[0] / scale, viewport_px[1] / scale]
}

/// 描画ターゲットの画素の矩形（左上原点・Y 下向き。`Screen.SafeArea` と同じ座標）を、
/// 2D キャンバスのワールド座標（オーバーレイの座標: 描画ターゲットの中央が原点・Y 下向き）へ写す【純関数】。
///
/// Play・スクリーンスペースの合成では、ルートキャンバスの原点（anchor 0 の左上）は `-ビューポート / 2` にある
/// （anchor.rs の root_anchor_offset）。オーバーレイのカメラは描画ターゲット全体を中央原点で写すので、
/// 画素 (x, y) はワールド (x - W/2, y - H/2) になる。
///
/// # 引数
/// * `rect_px`     - 矩形（x, y, 幅, 高さ。画素）
/// * `target_size` - 描画ターゲットの大きさ（画素。レイアウトの基準ビューポートと同じ値）
pub fn target_rect_to_canvas_world(rect_px: [f32; 4], target_size: [f32; 2]) -> CanvasRect {
    let [x, y, w, h] = rect_px;
    let half = [target_size[0] * 0.5, target_size[1] * 0.5];
    CanvasRect { min: [x - half[0], y - half[1]], max: [x + w - half[0], y + h - half[1]] }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// Android の基準 DPI（mdpi）。
    const ANDROID_REFERENCE_DPI: f32 = 160.0;
    /// Windows の基準 DPI（表示スケール 100%）。
    const DESKTOP_REFERENCE_DPI: f32 = 96.0;

    /// Pixel 6a（420 dpi）は 1 dp = 2.625 px、Windows の 150% は 1.5 px、100% は 1 px。
    #[test]
    fn dp_scale_matches_platform_definitions() {
        assert_eq!(dp_scale_from_dpi(420.0, ANDROID_REFERENCE_DPI), 2.625);
        assert_eq!(dp_scale_from_dpi(144.0, DESKTOP_REFERENCE_DPI), 1.5);
        assert_eq!(dp_scale_from_dpi(96.0, DESKTOP_REFERENCE_DPI), 1.0);
    }

    /// 壊れた DPI・基準は 1 dp = 1 px へ戻す（0 除算・NaN を走査へ持ち込まない）。
    #[test]
    fn broken_values_fall_back() {
        assert_eq!(dp_scale_from_dpi(0.0, ANDROID_REFERENCE_DPI), FALLBACK_DP_SCALE);
        assert_eq!(dp_scale_from_dpi(f32::NAN, ANDROID_REFERENCE_DPI), FALLBACK_DP_SCALE);
        assert_eq!(dp_scale_from_dpi(420.0, 0.0), FALLBACK_DP_SCALE);
        assert_eq!(CanvasScreenEnv { dp_scale: -2.0, safe_area: None }.dp_scale(), FALLBACK_DP_SCALE);
    }

    /// dp のルートの大きさ: Pixel 6a の縦画面 1080×2400 px は 411.43×914.29 dp。
    #[test]
    fn dp_canvas_size_divides_viewport() {
        let [w, h] = dp_canvas_size([1080.0, 2400.0], 2.625);
        assert!((w - 411.428_57).abs() < 1e-3 && (h - 914.285_7).abs() < 1e-3, "{w} {h}");
        assert_eq!(dp_canvas_size([540.0, 1200.0], 1.0), [540.0, 1200.0]);
    }

    /// 画素の矩形 → オーバーレイの座標（中央原点）。
    #[test]
    fn target_rect_maps_to_centered_canvas_world() {
        let r = target_rect_to_canvas_world([0.0, 136.0, 1080.0, 2201.0], [1080.0, 2400.0]);
        assert_eq!(r.min, [-540.0, -1064.0]);
        assert_eq!(r.max, [540.0, 1137.0]);
    }
}
