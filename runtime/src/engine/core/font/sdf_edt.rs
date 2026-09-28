// ============================================================
//  font/sdf_edt.rs — 2 値画像の厳密な二乗ユークリッド距離変換（Felzenszwalb & Huttenlocher）
//
//  【なぜ要るか（2026-09-28。docs/app_platform_roadmap.md §3.9 の「スクロール開始直後の山」）】
//  グリフの SDF（rasterizer.rs の generate_sdf）は、以前は各画素のまわり (2・spread+1)² を総当たりで探していた
//  （em 64・spread 8 の 1 字 ≒ 80×80 画素 × 289 近傍 ≒ 185 万回の比較）。初めて見える文字が出るたびに
//  これが走り、実機（Pixel 6a）の UI のスクロールの最初の十数フレームに 13〜209 ms の山を作っていた
//  （PC の最適化なしのビルドで「描画/UI/テキスト/グリフ焼き」が 1 フレームに最大 58 ms）。
//  ここの変換は画素数に比例（O(幅×高さ)）で、同じ字が数十〜数百倍速く焼ける。
//
//  【結果が総当たりと 1 ビットも違わない理由】
//  総当たりは「反対側（内外が逆）の画素までの二乗距離の最小値」を spread² 以下のものだけ拾い、無ければ spread²+1 にする。
//  spread² 以下の距離の画素は必ず探索窓（±spread の正方形）の中にあるので、総当たりの値は
//  min(厳密な二乗距離, spread²+1) と等しい。この変換は厳密な二乗距離を整数で返すので、呼び出し側で
//  spread²+1 に切り詰めれば同じ整数になり、その後の浮動小数の式（sqrt・正規化・255 倍）も同じになる
//  （rasterizer.rs のテストが実際のグリフで総当たりと全画素一致を確かめる）。
//
//  【アルゴリズム】
//  1 次元の二乗距離変換（放物線の下側包絡線）を列 → 行の順に 2 回かける（separable）。
//  距離は整数（最大でも 幅²+高さ²）。包絡線の交点だけ f64 で比べるが、交点は分母の小さな有理数で、
//  整数の問い合わせ位置をまたぐほどの丸め誤差は出ない（ちょうど整数の交点では両方の放物線が同じ値）。
// ============================================================

/// 「その位置に対象の画素が無い」を表す距離（どの実際の二乗距離よりも大きい）。
pub const EDT_INFINITY: i64 = i64::MAX / 4;

/// 1 次元の二乗距離変換の作業領域（行・列ごとに作り直さないため使い回す）。
struct Edt1dScratch {
    /// 包絡線を作る放物線の頂点の位置。
    vertices: Vec<usize>,
    /// 包絡線で隣り合う放物線の境目（`vertices` より 1 つ多い）。
    bounds: Vec<f64>,
}

impl Edt1dScratch {
    /// 長さ `n` までの 1 次元変換に足りる作業領域を作る。
    fn new(n: usize) -> Self {
        Self { vertices: vec![0; n.max(1)], bounds: vec![0.0; n.max(1) + 1] }
    }
}

/// 1 次元の二乗距離変換: `out[q] = min_p ( (q - p)² + f[p] )`（`f[p] = EDT_INFINITY` の位置は点が無いとして飛ばす）。
///
/// 点が 1 つも無ければ全体が `EDT_INFINITY`。
fn edt_1d(f: &[i64], out: &mut [i64], scratch: &mut Edt1dScratch) {
    let n = f.len();
    let v = &mut scratch.vertices;
    let z = &mut scratch.bounds;
    // ── 下側包絡線を作る（点のある位置だけ）──
    let mut count = 0usize; // 包絡線の放物線の数
    for q in 0..n {
        if f[q] >= EDT_INFINITY {
            continue;
        }
        if count == 0 {
            v[0] = q;
            z[0] = f64::NEG_INFINITY;
            z[1] = f64::INFINITY;
            count = 1;
            continue;
        }
        // 新しい放物線 q と包絡線の最後の放物線の交点。q のほうが低くなる区間が前の区間を覆えば前を捨てる
        let qi = q as i64;
        let fq = f[q] + qi * qi;
        let mut s;
        loop {
            let k = count - 1;
            let vk = v[k] as i64;
            s = (fq - (f[v[k]] + vk * vk)) as f64 / (2 * (qi - vk)) as f64;
            if s <= z[k] {
                // z[0] は -∞ なので count が 1 のときは必ず抜ける（count は 0 にならない）
                count -= 1;
            } else {
                break;
            }
        }
        v[count] = q;
        z[count] = s;
        z[count + 1] = f64::INFINITY;
        count += 1;
    }
    if count == 0 {
        out.iter_mut().for_each(|d| *d = EDT_INFINITY);
        return;
    }
    // ── 各位置で、その位置を受け持つ放物線の値を読む ──
    let mut k = 0usize;
    for (q, d) in out.iter_mut().enumerate() {
        while z[k + 1] < q as f64 {
            k += 1;
        }
        let dq = q as i64 - v[k] as i64;
        *d = dq * dq + f[v[k]];
    }
}

/// 2 値画像の各画素から「`is_target` が true の画素」までの二乗ユークリッド距離（整数）を返す。
///
/// - `width` × `height` の行優先。対象の画素自身は 0。
/// - 対象の画素が 1 つも無ければ全画素 `EDT_INFINITY`。
pub fn squared_distance_to_targets(width: usize, height: usize, is_target: impl Fn(usize) -> bool) -> Vec<i64> {
    let len = width * height;
    let mut dist = vec![EDT_INFINITY; len];
    if len == 0 {
        return dist;
    }
    let mut scratch = Edt1dScratch::new(width.max(height));
    // ── 列ごと（縦）: 同じ列の対象までの縦の二乗距離 ──
    let mut column_in = vec![EDT_INFINITY; height];
    let mut column_out = vec![0i64; height];
    for x in 0..width {
        for y in 0..height {
            column_in[y] = if is_target(y * width + x) { 0 } else { EDT_INFINITY };
        }
        edt_1d(&column_in, &mut column_out, &mut scratch);
        for y in 0..height {
            dist[y * width + x] = column_out[y];
        }
    }
    // ── 行ごと（横）: 縦の距離を重みにして横の二乗距離を足した最小 ──
    let mut row_out = vec![0i64; width];
    for y in 0..height {
        let row = &mut dist[y * width..(y + 1) * width];
        edt_1d(row, &mut row_out, &mut scratch);
        row.copy_from_slice(&row_out);
    }
    dist
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 総当たりの二乗距離（検算用）。
    fn brute(width: usize, height: usize, targets: &[bool]) -> Vec<i64> {
        let mut out = vec![EDT_INFINITY; width * height];
        for y in 0..height {
            for x in 0..width {
                for ty in 0..height {
                    for tx in 0..width {
                        if targets[ty * width + tx] {
                            let d = ((x as i64 - tx as i64).pow(2) + (y as i64 - ty as i64).pow(2)) as i64;
                            out[y * width + x] = out[y * width + x].min(d);
                        }
                    }
                }
            }
        }
        out
    }

    /// 再現できる疑似乱数（線形合同法。テストの入力を固定する）。
    struct Lcg(u64);
    impl Lcg {
        fn next(&mut self) -> u64 {
            self.0 = self.0.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
            self.0 >> 33
        }
    }

    /// いろいろな大きさ・密度の乱数の画像で総当たりと全画素一致する（1×N・N×1・対象なし・全部対象を含む）。
    #[test]
    fn matches_brute_force_on_random_images() {
        let mut rng = Lcg(20260928);
        for &(w, h) in &[(1, 1), (1, 7), (9, 1), (5, 5), (13, 8), (17, 23), (31, 29)] {
            for density in [0u64, 1, 5, 20, 50, 100] {
                let targets: Vec<bool> = (0..w * h).map(|_| rng.next() % 100 < density).collect();
                let got = squared_distance_to_targets(w, h, |i| targets[i]);
                assert_eq!(got, brute(w, h, &targets), "{w}x{h} 密度 {density}%");
            }
        }
    }

    /// 対象が 1 つも無ければ全画素が無限大（呼び出し側は spread²+1 へ切り詰める）。
    #[test]
    fn no_targets_is_infinity_everywhere() {
        let got = squared_distance_to_targets(4, 3, |_| false);
        assert!(got.iter().all(|&d| d == EDT_INFINITY));
        assert!(squared_distance_to_targets(0, 0, |_| true).is_empty());
    }

    /// 1 点だけなら各画素の値はその点までの二乗距離そのもの。
    #[test]
    fn single_target_gives_exact_squared_distance() {
        let (w, h) = (7usize, 5usize);
        let got = squared_distance_to_targets(w, h, |i| i == 2 * w + 3);
        for y in 0..h {
            for x in 0..w {
                let expect = (x as i64 - 3).pow(2) + (y as i64 - 2).pow(2);
                assert_eq!(got[y * w + x], expect, "({x},{y})");
            }
        }
    }
}
