// ============================================================
//  ui_shape/nine_slice.rs — 画像の 9 スライスの切り方と写像（W2-4）
//
//  軸（横・縦）ごとに、描く位置の格子 [0, a, b, 大きさ] と UV の格子 [0, ua, ub, 1] を決める
//  （2 軸の積が 3×3 のスライス＝4×4 の頂点の格子）。四隅は大きさを保ち、辺と中央は伸ばすか繰り返す。
//  シェーダー（shaders/sprite_shape.wgsl の `nine_axis`）は 1 枚の四角形のまま、画素ごとにこの写像で UV を求める
//  （頂点を 9 枚に分けないので、角丸・グラデーション・切り抜きと同じ 1 インスタンスで描ける）。
//
//  | 値        | 決め方                                                                                     |
//  |-----------|-------------------------------------------------------------------------------------------|
//  | a, 大きさ−b | 枠の幅（テクスチャの画素）× 倍率。2 辺の和が大きさを超えるなら同じ割合で縮める                  |
//  | ua, ub    | 枠の幅 ÷ テクスチャの幅（2 辺の和がテクスチャを超えるなら同じ割合で縮める）                       |
//  | 回数      | 繰り返し: 中央の帯の長さ ÷ タイルの長さ（テクスチャの中央の長さ × 倍率）を丸めた数（1 以上）。       |
//  |           | 端で切れたタイルを作らない（CSS の border-image-repeat: round）                                |
// ============================================================

/// 0 とみなす長さ（割り算の保護）。
const EPSILON: f32 = 1e-6;
/// 繰り返しの回数の下限。
const MIN_TILES: f32 = 1.0;

/// 1 軸の 9 スライスの切り方。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NineSliceAxis {
    /// 描く位置の格子の内側の 2 本（a = 始まりの枠の終わり、b = 終わりの枠の始まり。形の空間）。
    pub dest: [f32; 2],
    /// UV の格子の内側の 2 本（ua, ub）。
    pub uv: [f32; 2],
    /// 軸の長さ（形の空間）。
    pub size: f32,
    /// 中央の帯の繰り返しの回数（伸ばすときは使わない）。
    pub tiles: f32,
}

/// 1 軸の切り方を決める【純関数】。
///
/// # 引数
/// * `size`     - 描く長さ（形の空間）
/// * `start_px` - 始まりの枠の幅（テクスチャの画素。左か上）
/// * `end_px`   - 終わりの枠の幅（テクスチャの画素。右か下）
/// * `tex_px`   - テクスチャの長さ（画素）
/// * `scale`    - 描く枠の倍率（形の空間 ÷ テクスチャの画素）
pub fn resolve_axis(size: f32, start_px: f32, end_px: f32, tex_px: f32, scale: f32) -> NineSliceAxis {
    let size = size.max(0.0);
    let tex = tex_px.max(EPSILON);
    // テクスチャ側: 2 辺の和がテクスチャを超えるなら同じ割合で縮める
    let (mut s_px, mut e_px) = (start_px.max(0.0), end_px.max(0.0));
    if s_px + e_px > tex {
        let f = tex / (s_px + e_px);
        s_px *= f;
        e_px *= f;
    }
    // 描く側: 枠の幅 × 倍率。2 辺の和が長さを超えるなら同じ割合で縮める
    let scale = scale.max(0.0);
    let (mut ds, mut de) = (s_px * scale, e_px * scale);
    if ds + de > size {
        let f = if ds + de > EPSILON { size / (ds + de) } else { 0.0 };
        ds *= f;
        de *= f;
    }
    // 繰り返しの回数: 中央の帯の長さ ÷ タイル（テクスチャの中央の長さ × 倍率）を丸める
    let center_len = (size - ds - de).max(0.0);
    let tile = (tex - s_px - e_px) * scale;
    let tiles = if tile > EPSILON { (center_len / tile).round().max(MIN_TILES) } else { MIN_TILES };
    NineSliceAxis { dest: [ds, size - de], uv: [s_px / tex, 1.0 - e_px / tex], size, tiles }
}

/// 形の空間の位置 x を UV へ写す【純関数】（シェーダーの `nine_axis` と同じ式）。
///
/// # 引数
/// * `axis`   - 軸の切り方
/// * `x`      - 形の空間の位置
/// * `repeat` - 中央の帯を繰り返すか（false = 伸ばす）
pub fn map_axis(axis: &NineSliceAxis, x: f32, repeat: bool) -> f32 {
    let [a, b] = axis.dest;
    let [ua, ub] = axis.uv;
    if x < a {
        // 始まりの枠: 0..a → 0..ua（大きさを保つ）
        return if a > EPSILON { x / a * ua } else { 0.0 };
    }
    if x > b {
        // 終わりの枠: b..size → ub..1
        let len = axis.size - b;
        return if len > EPSILON { 1.0 - (axis.size - x) / len * (1.0 - ub) } else { 1.0 };
    }
    // 中央の帯: a..b → ua..ub（伸ばす）か、回数ぶん繰り返す
    let t = (x - a) / (b - a).max(EPSILON);
    let f = if repeat { (t * axis.tiles).fract() } else { t };
    ua + f * (ub - ua)
}

/// 3×3 のスライスの頂点の格子（4×4 = 16 頂点）【純関数】。
///
/// 位置は形の空間、UV は 0..1。行優先（上の行の左 → 右、次の行…）。伸ばすときの 9 枚の四角形の頂点と同じ。
///
/// # 戻り値
/// (位置の 16 頂点, UV の 16 頂点)
pub fn grid(x: &NineSliceAxis, y: &NineSliceAxis) -> ([[f32; 2]; 16], [[f32; 2]; 16]) {
    let xs = [0.0, x.dest[0], x.dest[1], x.size];
    let ys = [0.0, y.dest[0], y.dest[1], y.size];
    let us = [0.0, x.uv[0], x.uv[1], 1.0];
    let vs = [0.0, y.uv[0], y.uv[1], 1.0];
    let mut pos = [[0.0f32; 2]; 16];
    let mut uv = [[0.0f32; 2]; 16];
    for row in 0..4 {
        for col in 0..4 {
            pos[row * 4 + col] = [xs[col], ys[row]];
            uv[row * 4 + col] = [us[col], vs[row]];
        }
    }
    (pos, uv)
}

// ============================================================
//  単体テスト（9 スライスの頂点の計算と写像）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 浮動小数の比較の許容量。
    const EPS: f32 = 1e-5;

    fn close(a: f32, b: f32) -> bool {
        (a - b).abs() <= EPS
    }

    /// 64×32 のテクスチャ・枠 左 8・右 16 を 200 の長さへ（倍率 1）: 格子は [0, 8, 184, 200]、UV は [0, 1/8, 3/4, 1]。
    #[test]
    fn grid_vertices() {
        let x = resolve_axis(200.0, 8.0, 16.0, 64.0, 1.0);
        let y = resolve_axis(50.0, 4.0, 4.0, 32.0, 1.0);
        let (pos, uv) = grid(&x, &y);
        assert_eq!(pos[0], [0.0, 0.0]);
        assert_eq!(pos[1], [8.0, 0.0]);
        assert_eq!(pos[2], [184.0, 0.0]);
        assert_eq!(pos[3], [200.0, 0.0]);
        assert_eq!(pos[5], [8.0, 4.0]);
        assert_eq!(pos[15], [200.0, 50.0]);
        assert!(close(uv[1][0], 8.0 / 64.0) && close(uv[2][0], 1.0 - 16.0 / 64.0));
        assert!(close(uv[5][1], 4.0 / 32.0) && close(uv[10][1], 1.0 - 4.0 / 32.0));
        assert_eq!(uv[15], [1.0, 1.0]);
    }

    /// 倍率: 2 倍の解像度の画像は倍率 0.5 で枠が半分の幅に描かれる（UV は変わらない）。
    #[test]
    fn scale_changes_only_dest() {
        let x = resolve_axis(100.0, 20.0, 20.0, 80.0, 0.5);
        assert_eq!(x.dest, [10.0, 90.0]);
        assert!(close(x.uv[0], 0.25) && close(x.uv[1], 0.75));
    }

    /// 枠の和が長さを超えると同じ割合で縮める（角が重ならない）。
    #[test]
    fn borders_shrink_when_too_small() {
        let x = resolve_axis(30.0, 20.0, 40.0, 100.0, 1.0);
        assert!(close(x.dest[0], 10.0) && close(x.dest[1], 10.0), "20:40 を 30 に収める → 10 と 20");
        // テクスチャより大きな枠も同じ割合で縮める
        let t = resolve_axis(100.0, 60.0, 60.0, 100.0, 1.0);
        assert!(close(t.uv[0], 0.5) && close(t.uv[1], 0.5));
    }

    /// 写像: 枠は大きさを保ち、中央は伸ばす。境界で連続。
    #[test]
    fn map_stretch() {
        let x = resolve_axis(200.0, 8.0, 16.0, 64.0, 1.0);
        assert!(close(map_axis(&x, 0.0, false), 0.0));
        assert!(close(map_axis(&x, 4.0, false), 4.0 / 64.0), "左の枠の中は画素のまま");
        assert!(close(map_axis(&x, 8.0, false), 8.0 / 64.0));
        assert!(close(map_axis(&x, 96.0, false), (8.0 / 64.0 + 0.75) / 2.0), "中央の帯の真ん中");
        assert!(close(map_axis(&x, 184.0, false), 0.75));
        assert!(close(map_axis(&x, 192.0, false), 1.0 - 8.0 / 64.0), "右の枠の中は画素のまま");
        assert!(close(map_axis(&x, 200.0, false), 1.0));
    }

    /// 繰り返し: 回数は丸め（端で切れない）、タイルの中の位置で UV が繰り返す。
    #[test]
    fn map_repeat() {
        // テクスチャ中央 40 画素・倍率 1 → タイル 40。中央の帯 176 → 176/40 = 4.4 → 4 回
        let x = resolve_axis(200.0, 8.0, 16.0, 64.0, 1.0);
        assert_eq!(x.tiles, 4.0);
        let tile = (184.0 - 8.0) / 4.0;
        let at = |i: f32, f: f32| map_axis(&x, 8.0 + tile * (i + f), true);
        // どのタイルでも同じ位置は同じ UV
        assert!(close(at(0.0, 0.25), at(2.0, 0.25)));
        assert!(close(at(1.0, 0.5), 8.0 / 64.0 + 0.5 * (0.75 - 8.0 / 64.0)));
        // 帯が短くても 1 回は描く
        let short = resolve_axis(30.0, 8.0, 16.0, 64.0, 1.0);
        assert_eq!(short.tiles, MIN_TILES);
    }
}
