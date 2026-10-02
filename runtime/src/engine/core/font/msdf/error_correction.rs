// ============================================================
//  font/msdf/error_correction.rs — MSDF の補間の誤り（偽の縁・欠け）の補正
//
//  【なぜ要るか】
//  MSDF はテクセルの間をチャネルごとに直線で補間してから中央値を取る。隣のテクセルとチャネルの大小が入れ替わる所では、
//  補間した中央値が両端の値の範囲を外れて、本来ない縁（ひげ・点）や欠けが出ることがある。
//  そういうテクセルを見つけて **3 チャネルをその中央値にそろえる**（そのテクセルだけ 1 チャネルの距離場になる）。
//
//  【方式（msdfgen 1.9 以降の MSDFErrorCorrection。モード EDGE_PRIORITY・距離の確認 CHECK_DISTANCE_AT_EDGE と同じ）】
//    1. 守るテクセルの印: 角（隣り合う辺の色が 1 チャネル以下しか共有しない所）を囲む 4 テクセルと、
//       縁をまたぐ隣どうしで縁を作っているチャネル以外が中央値と違うテクセル（protectCorners / protectEdges）。
//    2. 守っていないテクセルで、隣（上下左右と斜め）との補間に誤りが出るものを誤りにする（値だけで判定）。
//    3. 残り全部を守る印にしてから、補間の中央値の符号が反転する点だけを候補にし、形から求めた本当の疑似距離と比べて
//       「そろえたほうが近くなる」ものを誤りにする。
//    4. 誤りのテクセルの 3 チャネルを中央値にそろえる（アルファ = 真の距離はそのまま）。
//  手順 2・3 はテクセルごとに自分の印だけを決める（値は読むだけ）ので、行ごとに並列に判定してから印を付ける。
//  【速さ】補正の前に赤・緑・青を 0..1 に収める（量子化でどうせ収まる＝見た目は変わらない）。遠いテクセルは全チャネルが 0 か 1 の
//  平らな値になり、3x3 の全てが平ら（赤 = 緑 = 青）なテクセルは補間で中央値が崩れようがないので判定を飛ばす。
//  値は正規化した距離（0.5 = 縁、1 テクセルで `FieldScale::value_per_texel` 変わる。0..1 に収める前の値）で扱う。
// ============================================================

use rayon::prelude::*;

use super::distance::{FieldGrid, PointScratch, PreparedShape};
use super::geometry::{median3_f32, solve_quadratic, Vec2};
use super::outline::Shape;
use super::params::{
    FieldScale, ARTIFACT_T_EPSILON, MIN_DEVIATION_RATIO, MIN_IMPROVE_RATIO, PROTECTION_RADIUS_TOLERANCE,
};

/// テクセルの印: 守る（補正しない）。
const PROTECTED: u8 = 1;
/// テクセルの印: 誤り（補正する）。
const ERROR: u8 = 2;
/// 判定の結果の旗: 候補（補間の中央値が範囲を外れた）。
const CLASSIFIER_FLAG_CANDIDATE: u8 = 1;
/// 判定の結果の旗: 誤り（範囲を外れた量が大きい）。
const CLASSIFIER_FLAG_ARTIFACT: u8 = 2;
/// 縁の値。
const EDGE: f32 = 0.5;

/// 1 テクセルの値（正規化した [赤, 緑, 青, アルファ]）。
pub type Texel = [f32; 4];

/// 補正の結果の数（ログ・試験用）。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct CorrectionStats {
    /// 守る印を付けたテクセルの数（手順 1）。
    pub protected: usize,
    /// 誤りとしてそろえたテクセルの数。
    pub corrected: usize,
    /// 形から本当の距離を求めて確かめた回数（手順 3 の候補の数。時間の内訳の目安）。
    pub distance_checks: usize,
}

/// 補正の本体の状態（印の表と格子の大きさ）。
struct Corrector {
    width: usize,
    height: usize,
    stencil: Vec<u8>,
    /// 1 テクセル進むと値がいくつ変わるか（隣のテクセルとの差の基準）。
    value_per_texel: f64,
    /// テクセルの 3x3 の全てが平ら（赤 = 緑 = 青）か（補間の誤りが起こりえない＝判定を飛ばす）。
    flat_neighborhood: Vec<bool>,
}

/// 補間した中央値が「誤り」かを判定する手（値だけで判定するもの・形の距離で確かめるもの）。
trait ArtifactClassifier {
    /// 補間の位置 xt（at..bt の間）の中央値 xm が、両端の中央値 am・bm から外れているか（旗を返す）。
    fn range_test(&self, at: f64, bt: f64, xt: f64, am: f32, bm: f32, xm: f32) -> u8;
    /// 旗と補間の位置 t の中央値 m から、誤りと決めるか。
    fn evaluate(&mut self, t: f64, m: f32, flags: u8) -> bool;
}

/// 範囲の検査の共通の式（msdfgen の BaseArtifactClassifier::rangeTest）。
///
/// 守るテクセルは「補間の中央値の符号が両端と逆」のときだけ、守らないテクセルは中央値が両端の間に無いときも候補にする。
/// 候補のうち、両端からの距離（span × 比）で届く範囲を外れたものは誤り。
fn range_test_common(span: f64, protected: bool, at: f64, bt: f64, xt: f64, am: f32, bm: f32, xm: f32) -> u8 {
    if (am > EDGE && bm > EDGE && xm <= EDGE) || (am < EDGE && bm < EDGE && xm >= EDGE) || (!protected && median3_f32(am, bm, xm) != xm) {
        let ax_span = ((xt - at) * span) as f32;
        let bx_span = ((bt - xt) * span) as f32;
        if !(xm >= am - ax_span && xm <= am + ax_span && xm >= bm - bx_span && xm <= bm + bx_span) {
            return CLASSIFIER_FLAG_CANDIDATE | CLASSIFIER_FLAG_ARTIFACT;
        }
        return CLASSIFIER_FLAG_CANDIDATE;
    }
    0
}

/// 値だけで判定する手（手順 2）。
struct BaseClassifier {
    span: f64,
    protected: bool,
}

impl ArtifactClassifier for BaseClassifier {
    fn range_test(&self, at: f64, bt: f64, xt: f64, am: f32, bm: f32, xm: f32) -> u8 {
        range_test_common(self.span, self.protected, at, bt, xt, am, bm, xm)
    }
    fn evaluate(&mut self, _t: f64, _m: f32, flags: u8) -> bool {
        flags & CLASSIFIER_FLAG_ARTIFACT != 0
    }
}

/// 形の本当の疑似距離で確かめるための共有の材料（手順 3）。
struct ShapeContext<'a> {
    /// 距離の片側の幅（テクセル。本当の距離を値へ直す）。
    spread_px: f32,
    /// 本当の距離を求めた回数（全スレッドで足す）。
    checks: &'a std::sync::atomic::AtomicUsize,
    values: &'a [Texel],
    width: usize,
    height: usize,
    shape: &'a PreparedShape,
    grid: &'a FieldGrid,
    scratch: PointScratch,
}

/// 形の本当の疑似距離で確かめる手（msdfgen の ShapeDistanceChecker::ArtifactClassifier）。
struct ShapeClassifier<'b, 'a> {
    span: f64,
    protected: bool,
    /// 今のテクセルの格子の座標（中心）と値。
    coord: (f64, f64),
    msd: Texel,
    /// 隣への向き（格子の座標。±1）。
    direction: (f64, f64),
    ctx: &'b mut ShapeContext<'a>,
}

impl ArtifactClassifier for ShapeClassifier<'_, '_> {
    fn range_test(&self, at: f64, bt: f64, xt: f64, am: f32, bm: f32, xm: f32) -> u8 {
        range_test_common(self.span, self.protected, at, bt, xt, am, bm, xm)
    }

    fn evaluate(&mut self, t: f64, _m: f32, flags: u8) -> bool {
        if flags & CLASSIFIER_FLAG_CANDIDATE == 0 {
            return false;
        }
        // 値だけで誤りと分かったものは距離を求めずに誤り
        if flags & CLASSIFIER_FLAG_ARTIFACT != 0 {
            return true;
        }
        let tv = (t * self.direction.0, t * self.direction.1);
        // 今の補間の色（候補の位置）と、今のテクセルをそろえたときの補間の色
        let pos = (self.coord.0 + tv.0, self.coord.1 + tv.1);
        let old = bilinear(self.ctx.values, self.ctx.width, self.ctx.height, pos);
        let a_weight = ((1.0 - tv.0.abs()) * (1.0 - tv.1.abs())) as f32;
        let a_psd = median3_f32(self.msd[0], self.msd[1], self.msd[2]);
        let new = [
            old[0] + a_weight * (a_psd - self.msd[0]),
            old[1] + a_weight * (a_psd - self.msd[1]),
            old[2] + a_weight * (a_psd - self.msd[2]),
        ];
        let old_psd = median3_f32(old[0], old[1], old[2]);
        let new_psd = median3_f32(new[0], new[1], new[2]);
        // 本当の値（形から求めた 1 チャネルの疑似距離を正規化）
        let p: Vec2 = self.ctx.grid.to_shape(pos.0, pos.1);
        self.ctx.checks.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        // 値は 0..1 に収めてあるので、本当の値も同じく収めて比べる
        let ref_psd = distance_to_value(self.ctx.shape.pseudo_distance_at(p, &mut self.ctx.scratch), self.ctx.spread_px).clamp(0.0, 1.0);
        MIN_IMPROVE_RATIO as f32 * (new_psd - ref_psd).abs() < (old_psd - ref_psd).abs()
    }
}

/// 判定の手の種類（手順 2 = 値だけ、手順 3 = 形の距離で確かめる）。
enum Mode<'a> {
    Base,
    Shape(ShapeContext<'a>),
}

/// 距離（テクセル）→ 正規化した値（0.5 = 縁。0..1 に収める前）。`spread_px` は距離の片側の幅（テクセル）。
#[inline]
pub fn distance_to_value(d: f64, spread_px: f32) -> f32 {
    (0.5 + d / (2.0 * f64::from(spread_px))) as f32
}

/// 格子の座標（テクセルの中心が i + 0.5）でバイリニアに読む（端は端のテクセルを繰り返す）。3 チャネルだけ返す。
fn bilinear(values: &[Texel], width: usize, height: usize, pos: (f64, f64)) -> [f32; 3] {
    let px = pos.0 - 0.5;
    let py = pos.1 - 0.5;
    let l = px.floor();
    let b = py.floor();
    let lr = (px - l) as f32;
    let bt = (py - b) as f32;
    let clamp_x = |v: f64| v.clamp(0.0, (width - 1) as f64) as usize;
    let clamp_y = |v: f64| v.clamp(0.0, (height - 1) as f64) as usize;
    let (l0, r0) = (clamp_x(l), clamp_x(l + 1.0));
    let (b0, t0) = (clamp_y(b), clamp_y(b + 1.0));
    let at = |x: usize, y: usize| values[y * width + x];
    let mut out = [0.0f32; 3];
    for (i, o) in out.iter_mut().enumerate() {
        let top = at(l0, b0)[i] + (at(r0, b0)[i] - at(l0, b0)[i]) * lr;
        let bottom = at(l0, t0)[i] + (at(r0, t0)[i] - at(l0, t0)[i]) * lr;
        *o = top + (bottom - top) * bt;
    }
    out
}

/// 2 テクセルの間の補間の中央値。
#[inline]
fn interpolated_median(a: &Texel, b: &Texel, t: f64) -> f32 {
    let t = t as f32;
    median3_f32(a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t)
}

/// 斜めの 4 テクセルの双線形補間を対角線に沿って読んだ中央値（a + t(l + t q)）。
#[inline]
fn interpolated_median_diag(a: &Texel, l: &[f32; 3], q: &[f32; 3], t: f64) -> f32 {
    let t = t as f32;
    median3_f32(a[0] + t * (l[0] + t * q[0]), a[1] + t * (l[1] + t * q[1]), a[2] + t * (l[2] + t * q[2]))
}

/// 2 チャネルが等しくなる位置（中央値が極端になる所）で補間の誤りが出るか（msdfgen の hasLinearArtifactInner）。
fn has_linear_artifact_inner(c: &mut dyn ArtifactClassifier, am: f32, bm: f32, a: &Texel, b: &Texel, da: f32, db: f32) -> bool {
    let t = f64::from(da) / f64::from(da - db);
    if t > ARTIFACT_T_EPSILON && t < 1.0 - ARTIFACT_T_EPSILON {
        let xm = interpolated_median(a, b, t);
        let flags = c.range_test(0.0, 1.0, t, am, bm, xm);
        return c.evaluate(t, xm, flags);
    }
    false
}

/// 上下左右の隣 b との補間に誤りが出るか（縁から遠いほうのテクセルだけが報告する）。
fn has_linear_artifact(c: &mut dyn ArtifactClassifier, am: f32, a: &Texel, b: &Texel) -> bool {
    let bm = median3_f32(b[0], b[1], b[2]);
    (am - EDGE).abs() >= (bm - EDGE).abs()
        && (has_linear_artifact_inner(c, am, bm, a, b, a[1] - a[0], b[1] - b[0])
            || has_linear_artifact_inner(c, am, bm, a, b, a[2] - a[1], b[2] - b[1])
            || has_linear_artifact_inner(c, am, bm, a, b, a[0] - a[2], b[0] - b[2]))
}

/// 斜めの補間で 2 チャネルが等しくなる位置の誤り（msdfgen の hasDiagonalArtifactInner）。
#[allow(clippy::too_many_arguments)]
fn has_diagonal_artifact_inner(
    c: &mut dyn ArtifactClassifier,
    am: f32,
    dm: f32,
    a: &Texel,
    l: &[f32; 3],
    q: &[f32; 3],
    da: f32,
    dbc: f32,
    dd: f32,
    t_ex0: f64,
    t_ex1: f64,
) -> bool {
    let mut t = [0.0f64; 3];
    let solutions = solve_quadratic(&mut t, f64::from(dd - dbc + da), f64::from(dbc - da - da), f64::from(da));
    for &ti in t.iter().take(solutions.max(0) as usize) {
        // t = 0 / 1 は 2 チャネルが等しいテクセルそのもの（よくある特異点）なので見ない
        if ti > ARTIFACT_T_EPSILON && ti < 1.0 - ARTIFACT_T_EPSILON {
            let xm = interpolated_median_diag(a, l, q, ti);
            let mut flags = c.range_test(0.0, 1.0, ti, am, dm, xm);
            // 各チャネルの極値の位置の中央値とも比べる
            for t_ex in [t_ex0, t_ex1] {
                if t_ex > 0.0 && t_ex < 1.0 {
                    let mut t_end = [0.0, 1.0];
                    let mut em = [am, dm];
                    let k = usize::from(t_ex > ti);
                    t_end[k] = t_ex;
                    em[k] = interpolated_median_diag(a, l, q, t_ex);
                    flags |= c.range_test(t_end[0], t_end[1], ti, em[0], em[1], xm);
                }
            }
            if c.evaluate(ti, xm, flags) {
                return true;
            }
        }
    }
    false
}

/// 斜めの隣 d（b・c は間の 2 テクセル）との補間に誤りが出るか（msdfgen の hasDiagonalArtifact）。
fn has_diagonal_artifact(cl: &mut dyn ArtifactClassifier, am: f32, a: &Texel, b: &Texel, c: &Texel, d: &Texel) -> bool {
    let dm = median3_f32(d[0], d[1], d[2]);
    if (am - EDGE).abs() < (dm - EDGE).abs() {
        return false;
    }
    let abc = [a[0] - b[0] - c[0], a[1] - b[1] - c[1], a[2] - b[2] - c[2]];
    // 双線形補間の 1 次の項と 2 次の項
    let l = [-a[0] - abc[0], -a[1] - abc[1], -a[2] - abc[2]];
    let q = [d[0] + abc[0], d[1] + abc[1], d[2] + abc[2]];
    // 各チャネルの極値の位置（導関数 2 q t + l = 0）
    let t_ex = [
        -0.5 * f64::from(l[0]) / f64::from(q[0]),
        -0.5 * f64::from(l[1]) / f64::from(q[1]),
        -0.5 * f64::from(l[2]) / f64::from(q[2]),
    ];
    has_diagonal_artifact_inner(cl, am, dm, a, &l, &q, a[1] - a[0], b[1] - b[0] + c[1] - c[0], d[1] - d[0], t_ex[0], t_ex[1])
        || has_diagonal_artifact_inner(cl, am, dm, a, &l, &q, a[2] - a[1], b[2] - b[1] + c[2] - c[1], d[2] - d[1], t_ex[1], t_ex[2])
        || has_diagonal_artifact_inner(cl, am, dm, a, &l, &q, a[0] - a[2], b[0] - b[2] + c[0] - c[2], d[0] - d[2], t_ex[2], t_ex[0])
}

/// 1 チャネルが 0.5 を横切る位置で、そのチャネルが中央値なら縁がある（msdfgen の edgeBetweenTexelsChannel）。
fn edge_between_texels_channel(a: &Texel, b: &Texel, channel: usize) -> bool {
    let t = (f64::from(a[channel]) - 0.5) / (f64::from(a[channel]) - f64::from(b[channel]));
    if t > 0.0 && t < 1.0 {
        let tf = t as f32;
        let c = [a[0] + (b[0] - a[0]) * tf, a[1] + (b[1] - a[1]) * tf, a[2] + (b[2] - a[2]) * tf];
        return median3_f32(c[0], c[1], c[2]) == c[channel];
    }
    false
}

/// 2 テクセルの間で縁を作っているチャネルのビットの組。
fn edge_between_texels(a: &Texel, b: &Texel) -> u8 {
    (0..3).filter(|&ch| edge_between_texels_channel(a, b, ch)).fold(0u8, |m, ch| m | (1 << ch))
}

/// 縁を作るチャネルのうち中央値と違うものがあれば守る（msdfgen の protectExtremeChannels）。
fn protect_extreme_channels(stencil: &mut u8, msd: &Texel, m: f32, mask: u8) {
    if (mask & 1 != 0 && msd[0] != m) || (mask & 2 != 0 && msd[1] != m) || (mask & 4 != 0 && msd[2] != m) {
        *stencil |= PROTECTED;
    }
}

impl Corrector {
    fn new(width: usize, height: usize, value_per_texel: f64) -> Self {
        Self { width, height, stencil: vec![0; width * height], value_per_texel, flat_neighborhood: vec![false; width * height] }
    }

    /// 3x3 の全てのテクセルが平ら（赤 = 緑 = 青）かを求める（範囲の外は数えない）。
    ///
    /// 2 テクセルとも平らなら、どの 2 チャネルも差が 0 のまま補間されるので、2 チャネルが入れ替わる点が無い（has_linear_artifact・
    /// has_diagonal_artifact は必ず偽）。3x3 の全てが平らなら 8 近傍のどの組でも誤りにならない。
    fn compute_flat_neighborhood(&mut self, values: &[Texel]) {
        let (w, h) = (self.width, self.height);
        let flat: Vec<bool> = values.iter().map(|v| v[0] == v[1] && v[1] == v[2]).collect();
        for y in 0..h {
            for x in 0..w {
                let mut all = true;
                for ny in y.saturating_sub(1)..=(y + 1).min(h - 1) {
                    for nx in x.saturating_sub(1)..=(x + 1).min(w - 1) {
                        all &= flat[ny * w + nx];
                    }
                }
                self.flat_neighborhood[y * w + x] = all;
            }
        }
    }

    /// 角（隣り合う辺の色が 1 チャネル以下しか共有しない所）を囲む 4 テクセルを守る。
    fn protect_corners(&mut self, shape: &Shape, grid: &FieldGrid) {
        for contour in &shape.contours {
            if contour.edges.is_empty() {
                continue;
            }
            let mut prev = contour.edges[contour.edges.len() - 1];
            for edge in &contour.edges {
                let common = prev.color.0 & edge.color.0;
                // 共有するチャネルが 1 つ以下 → 色が変わる → 角
                if common & common.wrapping_sub(1) == 0 {
                    let p = edge.start();
                    // 形の座標 → 格子の座標（テクセルの中心が i + 0.5）
                    let gx = p.x - grid.origin.x + 0.5;
                    let gy = grid.origin.y - p.y + 0.5;
                    let l = (gx - 0.5).floor() as i64;
                    let t = (gy - 0.5).floor() as i64;
                    for (x, y) in [(l, t), (l + 1, t), (l, t + 1), (l + 1, t + 1)] {
                        if x >= 0 && y >= 0 && (x as usize) < self.width && (y as usize) < self.height {
                            self.stencil[y as usize * self.width + x as usize] |= PROTECTED;
                        }
                    }
                }
                prev = *edge;
            }
        }
    }

    /// 縁をまたぐ隣どうし（上下左右・斜め）で、縁を作るチャネル以外が中央値と違うテクセルを守る。
    fn protect_edges(&mut self, values: &[Texel]) {
        let (w, h) = (self.width, self.height);
        let unit = self.value_per_texel;
        let straight = (PROTECTION_RADIUS_TOLERANCE * unit) as f32;
        let diagonal = (PROTECTION_RADIUS_TOLERANCE * unit * std::f64::consts::SQRT_2) as f32;
        let visit = |stencil: &mut [u8], i: usize, j: usize, radius: f32| {
            let (a, b) = (&values[i], &values[j]);
            let am = median3_f32(a[0], a[1], a[2]);
            let bm = median3_f32(b[0], b[1], b[2]);
            if (am - EDGE).abs() + (bm - EDGE).abs() < radius {
                let mask = edge_between_texels(a, b);
                protect_extreme_channels(&mut stencil[i], a, am, mask);
                protect_extreme_channels(&mut stencil[j], b, bm, mask);
            }
        };
        for y in 0..h {
            for x in 0..w {
                let i = y * w + x;
                if x + 1 < w {
                    visit(&mut self.stencil, i, i + 1, straight);
                }
                if y + 1 < h {
                    visit(&mut self.stencil, i, i + w, straight);
                }
                if x + 1 < w && y + 1 < h {
                    visit(&mut self.stencil, i, i + w + 1, diagonal);
                    visit(&mut self.stencil, i + 1, i + w, diagonal);
                }
            }
        }
    }

    /// 全てのテクセルを守る印にする。
    fn protect_all(&mut self) {
        self.stencil.iter_mut().for_each(|s| *s |= PROTECTED);
    }

    /// 1 テクセルを 8 近傍と比べ、補間の誤りがあるか（判定の手は `mode` で選ぶ。印は付けない＝並列に呼べる）。
    fn texel_has_error(&self, values: &[Texel], x: usize, y: usize, mode: &mut Mode) -> bool {
        let (w, h) = (self.width, self.height);
        let i = y * w + x;
        // 3x3 が全て平らなら補間の誤りは起こりえない
        if self.flat_neighborhood[i] {
            return false;
        }
        let c = values[i];
        let cm = median3_f32(c[0], c[1], c[2]);
        let protected = self.stencil[i] & PROTECTED != 0;
        let unit = self.value_per_texel;
        let h_span = MIN_DEVIATION_RATIO * unit;
        let d_span = MIN_DEVIATION_RATIO * unit * std::f64::consts::SQRT_2;
        let at = |xx: usize, yy: usize| &values[yy * w + xx];
        let coord = (x as f64 + 0.5, y as f64 + 0.5);
        // 隣 1 つぶんの判定（上下左右は直線の補間、斜めは双線形の補間を対角線に沿って見る）
        let check = |dx: i64, dy: i64, mode: &mut Mode| -> bool {
            let (nx, ny) = (x as i64 + dx, y as i64 + dy);
            if nx < 0 || ny < 0 || nx as usize >= w || ny as usize >= h {
                return false;
            }
            let (nx, ny) = (nx as usize, ny as usize);
            let diagonal = dx != 0 && dy != 0;
            let span = if diagonal { d_span } else { h_span };
            let direction = (dx as f64, dy as f64);
            let run = |cl: &mut dyn ArtifactClassifier| {
                if diagonal {
                    has_diagonal_artifact(cl, cm, &c, at(nx, y), at(x, ny), at(nx, ny))
                } else {
                    has_linear_artifact(cl, cm, &c, at(nx, ny))
                }
            };
            match mode {
                Mode::Base => run(&mut BaseClassifier { span, protected }),
                Mode::Shape(ctx) => run(&mut ShapeClassifier { span, protected, coord, msd: c, direction, ctx }),
            }
        };
        const NEIGHBORS: [(i64, i64); 8] = [(-1, 0), (0, -1), (1, 0), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1)];
        NEIGHBORS.iter().any(|&(dx, dy)| check(dx, dy, mode))
    }

    /// 全てのテクセルを行ごとに並列に判定し、誤りに印を付ける（すでに誤りの印があるものは飛ばす）。
    /// `make_mode` はスレッドごとの判定の手（作業領域を持つ）を作る。
    fn mark_errors<'a>(&mut self, values: &'a [Texel], make_mode: impl Fn() -> Mode<'a> + Sync + Send) {
        let w = self.width;
        let flags: Vec<bool> = {
            let this = &*self;
            (0..self.height)
                .into_par_iter()
                .map_init(&make_mode, |mode, y| {
                    (0..w)
                        .map(|x| this.stencil[y * w + x] & ERROR == 0 && this.texel_has_error(values, x, y, mode))
                        .collect::<Vec<bool>>()
                })
                .flatten()
                .collect()
        };
        for (s, e) in self.stencil.iter_mut().zip(flags) {
            if e {
                *s |= ERROR;
            }
        }
    }

    /// 誤りの印のテクセルの 3 チャネルを中央値にそろえる。返り値はそろえた数。
    fn apply(&self, values: &mut [Texel]) -> usize {
        let mut count = 0;
        for (v, s) in values.iter_mut().zip(self.stencil.iter()) {
            if s & ERROR != 0 {
                let m = median3_f32(v[0], v[1], v[2]);
                v[0] = m;
                v[1] = m;
                v[2] = m;
                count += 1;
            }
        }
        count
    }
}

/// MSDF の補間の誤りを補正する（手順は冒頭）。`values` は行優先・上の行から（格子 `grid` と同じ並び）。
pub fn correct_errors(values: &mut [Texel], grid: &FieldGrid, shape: &Shape, prepared: &PreparedShape, scale: &FieldScale) -> CorrectionStats {
    let (w, h) = (grid.width, grid.height);
    if w == 0 || h == 0 {
        return CorrectionStats::default();
    }
    // 赤・緑・青を 0..1 に収める（量子化と同じ。遠いテクセルが平らになり判定を飛ばせる）
    for v in values.iter_mut() {
        for c in v.iter_mut().take(3) {
            *c = c.clamp(0.0, 1.0);
        }
    }
    let mut corrector = Corrector::new(w, h, f64::from(scale.value_per_texel()));
    corrector.compute_flat_neighborhood(values);
    // 1. 守るテクセル
    corrector.protect_corners(shape, grid);
    corrector.protect_edges(values);
    let protected = corrector.stencil.iter().filter(|s| **s & PROTECTED != 0).count();
    // 2. 値だけで判定
    corrector.mark_errors(values, || Mode::Base);
    // 3. 残りを守る印にして、形の距離で確かめる（誤りの印が付いたものは飛ばす）
    corrector.protect_all();
    let checks = std::sync::atomic::AtomicUsize::new(0);
    {
        let snapshot: &[Texel] = values;
        let spread_px = scale.spread_px;
        let checks = &checks;
        corrector.mark_errors(snapshot, || {
            Mode::Shape(ShapeContext { spread_px, checks, values: snapshot, width: w, height: h, shape: prepared, grid, scratch: prepared.scratch() })
        });
    }
    // 4. そろえる
    let corrected = corrector.apply(values);
    CorrectionStats { protected, corrected, distance_checks: checks.load(std::sync::atomic::Ordering::Relaxed) }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 補間の中央値が両端の範囲を大きく外れる組（チャネルの大小の入れ替わり）は、守っていなければ値だけで誤りになり、
    /// 守るテクセルでは符号の反転が無いので誤りにしない。
    #[test]
    fn base_classifier_flags_clashing_neighbors() {
        let span = MIN_DEVIATION_RATIO * f64::from(FieldScale::BASE.value_per_texel());
        // 赤と青が入れ替わる組: 両端の中央値は 0.9 だが、真ん中（t = 0.5）で [0.55, 0.9, 0.55] → 中央値 0.55 に落ち込む
        let a: Texel = [0.9, 0.9, 0.2, 0.9];
        let b: Texel = [0.2, 0.9, 0.9, 0.9];
        let am = median3_f32(a[0], a[1], a[2]);
        assert!(has_linear_artifact(&mut BaseClassifier { span, protected: false }, am, &a, &b), "守らないテクセルは誤り");
        assert!(!has_linear_artifact(&mut BaseClassifier { span, protected: true }, am, &a, &b), "守るテクセルは符号が反転しない限り誤りにしない");
        // チャネルがそろった組は誤りでない
        let flat: Texel = [0.7, 0.7, 0.7, 0.7];
        let flat2: Texel = [0.6, 0.6, 0.6, 0.6];
        assert!(!has_linear_artifact(&mut BaseClassifier { span, protected: false }, 0.7, &flat, &flat2));
    }

    /// 距離 → 値の変換（0 で 0.5、片側の幅で 1.0 / 0.0）。
    #[test]
    fn distance_value_mapping() {
        let spread = FieldScale::BASE.spread_px;
        assert!((distance_to_value(0.0, spread) - 0.5).abs() < 1e-7);
        assert!((distance_to_value(f64::from(spread), spread) - 1.0).abs() < 1e-6);
        assert!((distance_to_value(-f64::from(spread), spread) - 0.0).abs() < 1e-6);
    }
}
