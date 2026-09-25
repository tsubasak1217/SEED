// ============================================================
//  quality/scale.rs — 描画スケール（ゲーム画面だけを縮小して描く）の寸法計算
//
//  【描画スケールとは】
//  3D のゲーム画面（HDR・G-Buffer・深度・後処理）を「論理サイズ × スケール」の解像度で描き、
//  トーンマップのところで論理サイズの LDR へ拡大する。UI（キャンバスのオーバーレイ）はその後で
//  論理サイズのまま重ね、最後に画面へ出す。論理サイズは従来の描画解像度（ウィンドウ実寸、
//  内部解像度固定ならその内部解像度）そのもので、スクリプトの Screen.Width・Input.MousePosition・
//  タッチ・安全領域の座標系は論理サイズのまま（スケールで変わらない）。
//
//  【写像の約束】
//  - 等倍（スケール 1.0）のときは描画解像度＝論理サイズで、比率は (1.0, 1.0) ちょうど。
//    どの写像も入力をそのまま返す（掛け算も割り算もしない）ので、デスクトップの従来の描画と
//    ビット単位で同じになる。
//  - 縮小のときは軸ごとに四捨五入した整数の解像度にする（1 未満にはしない）。縦横比は
//    四捨五入のぶん（1 px 未満）だけ論理サイズとずれることがあるが、拡大は全面へ引き伸ばすので
//    黒帯は出ない（ずれは 1 px 未満の伸縮になる）。
//
//  すべて純関数（GPU・ウィンドウに触らない）。単体テストは本ファイル末尾。
// ============================================================

use super::knobs::{MAX_RENDER_SCALE, MIN_RENDER_SCALE};

/// 描画解像度の 1 辺の最小ピクセル数（0 はテクスチャを作れない）。
const MIN_RENDER_EXTENT_PX: u32 = 1;

/// 描画スケールを有効な値へそろえる【純関数】。
///
/// 非有限（NaN・無限大）は等倍、範囲外は MIN_RENDER_SCALE〜MAX_RENDER_SCALE へ収める。
pub fn sanitize_render_scale(scale: f32) -> f32 {
    if scale.is_finite() {
        scale.clamp(MIN_RENDER_SCALE, MAX_RENDER_SCALE)
    } else {
        MAX_RENDER_SCALE
    }
}

/// 描画スケールが等倍か（等倍なら縮小の経路へ入らない）。
pub fn is_native_scale(scale: f32) -> bool {
    sanitize_render_scale(scale) >= MAX_RENDER_SCALE
}

/// 論理サイズに描画スケールを掛けた描画解像度を返す【純関数】。
///
/// 等倍なら論理サイズをそのまま返す。縮小なら軸ごとに四捨五入し、1 px 未満にはしない。
pub fn scaled_render_size(logical: (u32, u32), scale: f32) -> (u32, u32) {
    if is_native_scale(scale) {
        return logical;
    }
    let scale = sanitize_render_scale(scale);
    let axis = |extent: u32| -> u32 {
        let scaled = (extent as f32 * scale).round() as u32;
        scaled.max(MIN_RENDER_EXTENT_PX).min(extent.max(MIN_RENDER_EXTENT_PX))
    };
    (axis(logical.0), axis(logical.1))
}

/// 論理サイズ → 描画解像度の軸ごとの比率（等倍なら (1.0, 1.0) ちょうど）【純関数】。
pub fn render_ratio(logical: (u32, u32), render: (u32, u32)) -> (f32, f32) {
    if logical == render {
        return (1.0, 1.0);
    }
    let ratio = |r: u32, l: u32| r as f32 / l.max(MIN_RENDER_EXTENT_PX) as f32;
    (ratio(render.0, logical.0), ratio(render.1, logical.1))
}

/// 論理ピクセルの矩形 (x, y, 幅, 高さ) を描画解像度のピクセルへ写す【純関数】。
///
/// ゲームのビューポート（カメラのスケーリングモードが決める矩形）を、縮小して描く 3D の
/// レンダーパスの `set_viewport` へ渡すために使う。比率が (1.0, 1.0) なら入力をそのまま返す。
pub fn logical_rect_to_render(rect: (f32, f32, f32, f32), ratio: (f32, f32)) -> (f32, f32, f32, f32) {
    if ratio == (1.0, 1.0) {
        return rect;
    }
    let (x, y, w, h) = rect;
    (x * ratio.0, y * ratio.1, w * ratio.0, h * ratio.1)
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 等倍は論理サイズそのもの・比率 (1,1)・矩形もそのまま（デスクトップの従来の描画と同じ）。
    #[test]
    fn native_scale_is_identity() {
        for logical in [(1920, 1080), (1080, 2400), (1, 1), (7, 3)] {
            assert_eq!(scaled_render_size(logical, 1.0), logical);
            assert_eq!(render_ratio(logical, logical), (1.0, 1.0));
        }
        let rect = (10.25, 20.5, 300.75, 400.125);
        assert_eq!(logical_rect_to_render(rect, (1.0, 1.0)), rect);
        // 範囲外・非有限は等倍へ。
        assert_eq!(scaled_render_size((1920, 1080), 1.5), (1920, 1080));
        assert_eq!(scaled_render_size((1920, 1080), f32::NAN), (1920, 1080));
        assert_eq!(scaled_render_size((1920, 1080), f32::INFINITY), (1920, 1080));
        assert!(is_native_scale(1.0) && is_native_scale(2.0) && !is_native_scale(0.75));
    }

    /// 縮小: Pixel 6a の 1080x2400（縦）・2400x1080（横）で 0.5 / 0.75。縦横比は保たれる。
    #[test]
    fn scaled_sizes_for_phone_screen() {
        assert_eq!(scaled_render_size((1080, 2400), 0.5), (540, 1200));
        assert_eq!(scaled_render_size((2400, 1080), 0.5), (1200, 540));
        assert_eq!(scaled_render_size((1080, 2400), 0.75), (810, 1800));
        // 下限より小さいスケールは下限（0.5）へ。
        assert_eq!(scaled_render_size((1080, 2400), 0.1), (540, 1200));
        // 1 px 未満にはならない。
        assert_eq!(scaled_render_size((1, 1), 0.5), (1, 1));
    }

    /// 四捨五入で縦横比が 1 px 未満ずれる解像度でも、比率は軸ごとに求めて矩形の写像が全面に一致する。
    #[test]
    fn ratio_maps_full_rect_to_full_target() {
        let logical = (1366, 768);
        let render = scaled_render_size(logical, 0.6);
        assert_eq!(render, (820, 461));
        let ratio = render_ratio(logical, render);
        let (x, y, w, h) = logical_rect_to_render((0.0, 0.0, 1366.0, 768.0), ratio);
        assert!((x, y) == (0.0, 0.0));
        assert!((w - render.0 as f32).abs() < 1e-3, "w={w}");
        assert!((h - render.1 as f32).abs() < 1e-3, "h={h}");
    }

    /// 座標の一致: 論理座標（UI・タッチ・スクリプトの座標）の点と、それを描画解像度へ写した点は、
    /// それぞれのターゲットの NDC で同じ位置になる（＝3D を縮小して描いても、画面上の位置は UI・入力と一致する）。
    /// 縦横で四捨五入の違う解像度（1366x768 × 0.6）と端末の画面（1080x2400 × 0.5 / 0.75）で確かめる。
    #[test]
    fn logical_and_render_points_share_ndc() {
        const NDC_EPS: f32 = 1e-5;
        let ndc = |p: f32, extent: u32| p / extent as f32 * 2.0 - 1.0;
        for (logical, scale) in [((1366, 768), 0.6), ((1080, 2400), 0.5), ((2400, 1080), 0.75), ((1920, 1080), 1.0)] {
            let render = scaled_render_size(logical, scale);
            let ratio = render_ratio(logical, render);
            for (x, y) in [(0.0, 0.0), (240.0, 280.0), (1079.0, 767.0), (333.3, 511.7)] {
                let (rx, ry, _, _) = logical_rect_to_render((x, y, 0.0, 0.0), ratio);
                assert!((ndc(x, logical.0) - ndc(rx, render.0)).abs() < NDC_EPS, "x {logical:?} {scale} {x}");
                assert!((ndc(y, logical.1) - ndc(ry, render.1)).abs() < NDC_EPS, "y {logical:?} {scale} {y}");
            }
        }
    }

    /// レターボックス付きのゲームビューポート（論理 1080x2400 に 16:9 の帯付き領域）を 0.5 で写す。
    #[test]
    fn letterboxed_viewport_is_scaled_per_axis() {
        let logical = (1080, 2400);
        let render = scaled_render_size(logical, 0.5);
        let ratio = render_ratio(logical, render);
        let viewport = (0.0, 896.25, 1080.0, 607.5);
        let (x, y, w, h) = logical_rect_to_render(viewport, ratio);
        assert_eq!((x, w), (0.0, 540.0));
        assert!((y - 448.125).abs() < 1e-3 && (h - 303.75).abs() < 1e-3, "y={y} h={h}");
    }
}
