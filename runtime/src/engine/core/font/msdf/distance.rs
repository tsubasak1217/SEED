// ============================================================
//  font/msdf/distance.rs — テクセルごとの MTSDF の距離（赤・緑・青の疑似距離 + 真の距離）
//
//  【何を求めるか（msdfgen の MultiAndTrueDistanceSelector + OverlappingContourCombiner と同じ式）】
//  - 赤・緑・青: そのチャネルの色を持つ辺のうち最も近い辺への **疑似距離**（最近点が端点の外なら、端点からの延長線への
//    垂直距離。さらに同じ色の辺のつなぎ目の延長〈perpendicular distance〉も見る）。角の両側の辺が別のチャネルにあるので、
//    3 つの中央値の 0 の等値線は角で尖る。
//  - アルファ: 全ての辺への **真の符号つき距離**（小さな文字・縁取り・ぼかしの影に使う）。
//  - 輪郭ごとに距離を求めて、輪郭の向き（外側 +1・穴 -1）と点が内側か外側かで合成する（重なった輪郭の内側の辺を
//    縁とみなさないため。msdfgen の OverlappingContourCombiner）。
//
//  【速さのための打ち切り（msdfgen との違い）】
//  距離はスプレッドより遠いと値が 0 / 1 に張り付くので、テクセルから `cutoff`（スプレッド + 余裕）より遠い辺は見ない
//  （外接矩形で判定）。辺が 1 本も残らない輪郭・チャネルは「遠い（`far`）」とし、符号は走査線で数えた巻き数
//  （点がその輪郭の内側か）から決める。msdfgen の値と違うのは、遠い辺の延長線が近くを通るチャネルの値だけ
//  （どちらでも値は張り付くか、中央値に効かない）。
//
//  【符号の直し】
//  走査線の巻き数（非ゼロ規則）で決めた内外と、中央値の符号が食い違うテクセルは全チャネルの符号を反転する
//  （msdfgen の distanceSignCorrection）。アルファは常に巻き数の内外の符号にする。誤差の補正が比べる基準の
//  1 チャネルの疑似距離（`pseudo_distance_at`）にも同じ内外の符号を当てる（2026-10-03。レビュー #16）。
//
//  【遠い輪郭の入れ子の深さと同点（2026-10-03。レビュー #4）】
//  辺の残らない（遠い）輪郭の距離の大きさは入れ子の深さで決める（深いほど近い）。深さ = 「その輪郭の **全ての辺の中点** を
//  内側に含む、ほかの輪郭の数」（以前は最初の辺の中点 1 つだけで数えたので、小さな輪郭が重なると外枠の深さが 1 増え、
//  外枠と穴が同点になって穴の中の点で外枠を選んでいた）。深さが同じ輪郭どうしは輪郭の番号で決める（番号の大きい輪郭ほど
//  わずかに近い。差は深さ 1 段の半分未満なので深さの順は崩れない）＝ 遠い輪郭の距離に同点が無く、合成の結果が決まる。
// ============================================================

use rayon::prelude::*;

use super::edge_color::EdgeColor;
use super::geometry::{median3, v2, SignedDistance, Vec2};
use super::outline::Shape;
use super::segment::EdgeSegment;

/// 曲線を折れ線へ近似するときの誤差（テクセル。巻き数〈内外〉の判定にだけ使う）。
const FLATTEN_TOLERANCE: f64 = 0.01;
/// 遠い輪郭の距離の大きさを、入れ子の深さ 1 段ごとにこれだけ小さくする（テクセル）。
///
/// 打ち切りで辺の残らない輪郭は本当の距離が分からないので、同じ大きさにすると合成（combine）の「内側の輪郭と穴のどちらが近いか」
/// の比べ方が同点になり、穴の中の点で外側の輪郭を選んで内外を誤る（「図」の囗の中の点が字の中になった。2026-10-02）。
/// 入れ子の内側の輪郭（穴の中の点なら穴）ほど近いとみなす（入れ子なら幾何学的にもそうなる）。
const FAR_DEPTH_STEP: f64 = 0.5;
/// 巻き数の折れ線の索引の帯の高さ（テクセル）。点の y の帯に掛かる折れ線だけを見る（全部を見ると誤差の補正の候補ごとに
/// 数千本を調べて遅かった。2026-10-02 の計測で画数の多い字の補正が 3.4 ms → 帯の索引で短縮）。
const POLY_BAND_HEIGHT: f64 = 1.0;
/// 深さが同じ遠い輪郭の同点を崩す幅の上限（`FAR_DEPTH_STEP` に対する割合）。輪郭 c は c ÷ 輪郭の数 × これ × 深さ 1 段だけ近くする
/// （全輪郭の差の合計が深さ 1 段の半分未満＝深さの順は崩れない）。
const FAR_TIE_FRACTION: f64 = 0.5;
/// 辺の中点（入れ子の深さの判定に使う、輪郭の上の点）。
const EDGE_MIDPOINT: f64 = 0.5;

/// MTSDF の 1 テクセルの距離（テクセルの単位・内側が正）: [赤, 緑, 青, 真の距離]。
pub type MtsdfDistances = [f64; 4];

/// 距離を求める格子（テクセルの中心の位置）。
#[derive(Clone, Copy, Debug)]
pub struct FieldGrid {
    /// 幅（テクセル）。
    pub width: usize,
    /// 高さ（テクセル）。
    pub height: usize,
    /// テクセル (0, 0)（左上）の中心の座標（形の空間・y 上向き）。右のテクセルは x + 1、下の行は y − 1。
    pub origin: Vec2,
}

impl FieldGrid {
    /// テクセル (i, j) の中心の座標。
    #[inline]
    pub fn center(&self, i: usize, j: usize) -> Vec2 {
        v2(self.origin.x + i as f64, self.origin.y - j as f64)
    }

    /// 格子の座標（テクセルの中心が整数 + 0.5。左上が 0）から形の空間の座標へ。
    #[inline]
    pub fn to_shape(&self, gx: f64, gy: f64) -> Vec2 {
        v2(self.origin.x + (gx - 0.5), self.origin.y - (gy - 0.5))
    }
}

/// MTSDF の距離の格子の結果。
pub struct MtsdfField {
    pub width: usize,
    pub height: usize,
    /// 行優先（上の行から）。
    pub texels: Vec<MtsdfDistances>,
    /// テクセルの中心が字の内側か（非ゼロ規則）。
    pub inside: Vec<bool>,
}

// ─── 前処理した形 ──────────────────────────────────────────────

/// 距離の計算のために前処理した 1 本の辺。
struct PreparedEdge {
    seg: EdgeSegment,
    /// この辺を含む輪郭の番号。
    contour: usize,
    /// 外接矩形。
    bb_min: Vec2,
    bb_max: Vec2,
    /// 始点・終点。
    a: Vec2,
    b: Vec2,
    /// 始点・終点の接線（正規化）。
    a_dir: Vec2,
    b_dir: Vec2,
    /// 始点の角の二等分の向き（前の辺の終わりの接線 + この辺の始まりの接線。正規化）。
    a_bisector: Vec2,
    /// 終点の角の二等分の向き（この辺の終わりの接線 + 次の辺の始まりの接線。正規化）。
    b_bisector: Vec2,
}

/// 前処理した輪郭（辺の範囲と向き）。
struct PreparedContour {
    first: usize,
    len: usize,
    /// 輪郭の向き（+1 = 外側・-1 = 穴・0 = 決まらない）。
    orientation: i32,
    /// 辺が残らないときの距離の大きさ（テクセル。入れ子が深いほど小さい。`FAR_DEPTH_STEP`）。
    far: f64,
}

/// 巻き数を数えるための折れ線の 1 本（輪郭の番号つき）。
#[derive(Clone, Copy)]
struct PolySegment {
    a: Vec2,
    b: Vec2,
    contour: usize,
}

/// 距離の計算のために前処理した形。
pub struct PreparedShape {
    edges: Vec<PreparedEdge>,
    contours: Vec<PreparedContour>,
    polylines: Vec<PolySegment>,
    /// 折れ線の y の帯ごとの索引（帯 k = y が [poly_y0 + k, poly_y0 + k + 1) に掛かる折れ線の番号）。
    poly_bands: Vec<Vec<u32>>,
    /// 帯 0 の下端の y。
    poly_y0: f64,
    /// これより遠い辺は見ない（テクセル）。
    cutoff: f64,
    /// 辺が残らない輪郭・チャネルの距離の大きさ（テクセル。スプレッドより大きい）。
    far: f64,
}

/// 1 チャネルの距離の選び手（msdfgen の PerpendicularDistanceSelectorBase）。
#[derive(Clone, Copy)]
struct ChannelSelector {
    min_true: SignedDistance,
    near_edge: Option<usize>,
    near_param: f64,
    min_negative_perpendicular: f64,
    min_positive_perpendicular: f64,
}

impl ChannelSelector {
    /// 何も見ていない状態（msdfgen と同じく距離は -∞ 扱い）。
    const EMPTY: ChannelSelector = ChannelSelector {
        min_true: SignedDistance::INFINITE,
        near_edge: None,
        near_param: 0.0,
        min_negative_perpendicular: -f64::MAX,
        min_positive_perpendicular: f64::MAX,
    };

    /// 「遠い」状態（距離 = sign × far。辺の残らない輪郭・チャネル）。
    fn far(sign: f64, far: f64) -> ChannelSelector {
        ChannelSelector {
            min_true: SignedDistance::new(sign * far, 0.0),
            near_edge: None,
            near_param: 0.0,
            min_negative_perpendicular: -far,
            min_positive_perpendicular: far,
        }
    }

    /// 辺への真の距離を足す（近ければ最近の辺として覚える）。
    #[inline]
    fn add_true(&mut self, edge: usize, distance: SignedDistance, param: f64) {
        if distance.closer_than(&self.min_true) {
            self.min_true = distance;
            self.near_edge = Some(edge);
            self.near_param = param;
        }
    }

    /// 端点の延長への垂直距離を足す（符号ごとに最小を覚える）。
    #[inline]
    fn add_perpendicular(&mut self, distance: f64) {
        if distance <= 0.0 && distance > self.min_negative_perpendicular {
            self.min_negative_perpendicular = distance;
        }
        if distance >= 0.0 && distance < self.min_positive_perpendicular {
            self.min_positive_perpendicular = distance;
        }
    }

    /// 別の選び手の結果を合わせる。
    #[inline]
    fn merge(&mut self, other: &ChannelSelector) {
        if other.min_true.closer_than(&self.min_true) {
            self.min_true = other.min_true;
            self.near_edge = other.near_edge;
            self.near_param = other.near_param;
        }
        if other.min_negative_perpendicular > self.min_negative_perpendicular {
            self.min_negative_perpendicular = other.min_negative_perpendicular;
        }
        if other.min_positive_perpendicular < self.min_positive_perpendicular {
            self.min_positive_perpendicular = other.min_positive_perpendicular;
        }
    }

    /// このチャネルの疑似距離（msdfgen の computeDistance）。
    fn compute(&self, p: Vec2, edges: &[PreparedEdge]) -> f64 {
        let mut min_distance =
            if self.min_true.distance < 0.0 { self.min_negative_perpendicular } else { self.min_positive_perpendicular };
        if let Some(e) = self.near_edge {
            let mut distance = self.min_true;
            edges[e].seg.distance_to_perpendicular_distance(&mut distance, p, self.near_param);
            if distance.distance.abs() < min_distance.abs() {
                min_distance = distance.distance;
            }
        }
        min_distance
    }
}

/// 1 つの輪郭（または合成したもの）の 3 チャネル + 真の距離の選び手。
#[derive(Clone, Copy)]
struct MultiSelector {
    channels: [ChannelSelector; 3],
}

impl MultiSelector {
    const EMPTY: MultiSelector = MultiSelector { channels: [ChannelSelector::EMPTY; 3] };

    /// 合わせる（チャネルごと）。
    fn merge(&mut self, other: &MultiSelector) {
        for (a, b) in self.channels.iter_mut().zip(other.channels.iter()) {
            a.merge(b);
        }
    }

    /// 真の距離（3 チャネルの最近の辺の真の距離のうち最も近いもの）。
    fn true_distance(&self) -> f64 {
        let mut best = self.channels[0].min_true;
        for c in &self.channels[1..] {
            if c.min_true.closer_than(&best) {
                best = c.min_true;
            }
        }
        best.distance
    }

    /// [赤, 緑, 青, 真の距離]。
    fn distances(&self, p: Vec2, edges: &[PreparedEdge]) -> MtsdfDistances {
        [
            self.channels[0].compute(p, edges),
            self.channels[1].compute(p, edges),
            self.channels[2].compute(p, edges),
            self.true_distance(),
        ]
    }
}

/// 距離の中央値（合成で輪郭の距離を比べるときの代表値。msdfgen の resolveDistance）。
#[inline]
fn resolve(d: &MtsdfDistances) -> f64 {
    median3(d[0], d[1], d[2])
}

/// 1 点の計算の作業領域（点ごとに確保し直さないため使い回す）。
pub struct PointScratch {
    /// 輪郭ごとの選び手。
    selectors: Vec<MultiSelector>,
    /// 輪郭ごとの距離（合成で何度も使うので 1 回だけ求めて置く）。
    per_contour: Vec<MtsdfDistances>,
    /// 輪郭ごとの巻き数（任意の点の計算用）。
    windings: Vec<i32>,
    /// 全ての辺の番号（任意の点の計算で候補にする）。
    all_edges: Vec<u32>,
}

impl PreparedShape {
    /// 形を前処理する。`cutoff` より遠い辺は見ない・辺の残らないものは `far` の距離にする（どちらもテクセル）。
    pub fn new(shape: &Shape, cutoff: f64, far: f64) -> Self {
        let mut edges = Vec::with_capacity(shape.edge_count());
        let mut contours = Vec::with_capacity(shape.contours.len());
        let mut polylines = Vec::new();
        for (ci, contour) in shape.contours.iter().enumerate() {
            let first = edges.len();
            let n = contour.edges.len();
            for k in 0..n {
                let seg = contour.edges[k];
                let prev = &contour.edges[(k + n - 1) % n];
                let next = &contour.edges[(k + 1) % n];
                let mut bb_min = v2(f64::MAX, f64::MAX);
                let mut bb_max = v2(-f64::MAX, -f64::MAX);
                seg.extend_bounds(&mut bb_min, &mut bb_max);
                let a_dir = seg.direction(0.0).normalize(true);
                let b_dir = seg.direction(1.0).normalize(true);
                let prev_dir = prev.direction(1.0).normalize(true);
                let next_dir = next.direction(0.0).normalize(true);
                edges.push(PreparedEdge {
                    seg,
                    contour: ci,
                    bb_min,
                    bb_max,
                    a: seg.start(),
                    b: seg.end(),
                    a_dir,
                    b_dir,
                    a_bisector: (prev_dir + a_dir).normalize(true),
                    b_bisector: (b_dir + next_dir).normalize(true),
                });
                // 巻き数用の折れ線
                let steps = seg.flatten_steps(FLATTEN_TOLERANCE);
                let mut prev_pt = seg.start();
                for s in 1..=steps {
                    let pt = if s == steps { seg.end() } else { seg.point(s as f64 / steps as f64) };
                    polylines.push(PolySegment { a: prev_pt, b: pt, contour: ci });
                    prev_pt = pt;
                }
            }
            contours.push(PreparedContour { first, len: n, orientation: contour.winding(), far });
        }
        let (poly_bands, poly_y0) = band_index(&polylines);
        let mut prepared = Self { edges, contours, polylines, poly_bands, poly_y0, cutoff, far };
        prepared.assign_far_by_depth(shape);
        prepared
    }

    /// 高さ y の帯に掛かる折れ線の番号（範囲の外なら空）。
    #[inline]
    fn band_at(&self, y: f64) -> &[u32] {
        let k = ((y - self.poly_y0) / POLY_BAND_HEIGHT).floor();
        if k < 0.0 || k as usize >= self.poly_bands.len() {
            return &[];
        }
        &self.poly_bands[k as usize]
    }

    /// 輪郭ごとの「遠い」距離の大きさを入れ子の深さで決める（冒頭の【遠い輪郭の入れ子の深さと同点】）。
    ///
    /// 深さ = その輪郭の全ての辺の中点を内側に含む、ほかの輪郭の数（`nesting_depths`）。深さが同じ輪郭は番号の大きいほうを
    /// わずかに近くして同点を無くす（距離 → 輪郭の番号の順に決まる）。
    fn assign_far_by_depth(&mut self, shape: &Shape) {
        let n = self.contours.len();
        let depths = self.nesting_depths(shape);
        // 同点を崩す 1 輪郭ぶんの幅（全輪郭で合わせても深さ 1 段の半分未満）
        let tie_step = FAR_DEPTH_STEP * FAR_TIE_FRACTION / n.max(1) as f64;
        for (c, depth) in depths.into_iter().enumerate() {
            // 打ち切りより遠いまま（値が必ず張り付く）にする
            let by_depth = (self.far - depth as f64 * FAR_DEPTH_STEP).max(self.cutoff + FAR_DEPTH_STEP);
            self.contours[c].far = by_depth - c as f64 * tie_step;
        }
    }

    /// 輪郭ごとの入れ子の深さ（その輪郭の **全ての** 辺の中点を内側〈巻き数が 0 でない〉に含む、ほかの輪郭の数）。
    ///
    /// 1 点だけで数えると、その点にたまたま重なった小さな輪郭まで「外側の親」に数えてしまう（レビュー #4 の O・Q）。
    /// 入れ子の親は輪郭を丸ごと含むので、全ての辺の中点で確かめれば重なっただけの輪郭は数えない。
    fn nesting_depths(&self, shape: &Shape) -> Vec<usize> {
        let n = self.contours.len();
        let mut windings = vec![0i32; n];
        shape
            .contours
            .iter()
            .enumerate()
            .map(|(c, contour)| {
                // contains[d] = これまでの全ての中点が輪郭 d の内側だったか
                let mut contains = vec![true; n];
                contains[c] = false;
                for edge in &contour.edges {
                    self.windings_at(edge.point(EDGE_MIDPOINT), &mut windings);
                    for (d, inside) in contains.iter_mut().enumerate() {
                        *inside &= windings[d] != 0;
                    }
                }
                contains.iter().filter(|&&inside| inside).count()
            })
            .collect()
    }

    /// 1 点の作業領域を作る。
    pub fn scratch(&self) -> PointScratch {
        PointScratch {
            selectors: vec![MultiSelector::EMPTY; self.contours.len()],
            per_contour: vec![[0.0; 4]; self.contours.len()],
            windings: vec![0; self.contours.len()],
            all_edges: (0..self.edges.len() as u32).collect(),
        }
    }

    /// 点 p の輪郭ごとの巻き数（p から左〈-x〉へ伸ばした半直線と折れ線の交差の向きの和）を `out` へ入れる。
    ///
    /// 上向きに横切る線を +1 にする（y 上向きで時計回りの外側の輪郭の内側が +1・穴の内側が -1）。
    pub fn windings_at(&self, p: Vec2, out: &mut [i32]) {
        out.iter_mut().for_each(|w| *w = 0);
        for &si in self.band_at(p.y) {
            let s = &self.polylines[si as usize];
            if let Some((x, dir)) = crossing(s, p.y) {
                if x < p.x {
                    out[s.contour] += dir;
                }
            }
        }
    }

    /// 輪郭 c に辺が残らないときの距離の符号（点が輪郭の内側なら輪郭の向き、外側ならその逆）。
    #[inline]
    fn far_sign(&self, c: usize, winding: i32) -> f64 {
        let orientation = if self.contours[c].orientation >= 0 { 1.0 } else { -1.0 };
        if winding != 0 { orientation } else { -orientation }
    }

    /// 点 p の MTSDF の距離（`candidates` は見る辺の番号。`windings` は輪郭ごとの巻き数）。
    ///
    /// `force_white` が真なら全ての辺を全チャネルに入れる（1 チャネルの疑似距離 = 誤差の補正の基準）。
    fn evaluate(&self, p: Vec2, candidates: &[u32], windings: &[i32], force_white: bool, scratch: &mut PointScratch) -> MtsdfDistances {
        let n_contours = self.contours.len();
        scratch.selectors.iter_mut().for_each(|s| *s = MultiSelector::EMPTY);
        for &ei in candidates {
            let ei = ei as usize;
            let e = &self.edges[ei];
            // 打ち切り: 外接矩形が cutoff より遠い辺は見ない
            if p.x < e.bb_min.x - self.cutoff || p.x > e.bb_max.x + self.cutoff || p.y < e.bb_min.y - self.cutoff || p.y > e.bb_max.y + self.cutoff {
                continue;
            }
            let c = e.contour;
            let color = if force_white { EdgeColor::WHITE } else { e.seg.color };
            let sel = &mut scratch.selectors[c];
            let (distance, param) = e.seg.signed_distance(p);
            for ch in 0..3 {
                if color.has_channel(ch) {
                    sel.channels[ch].add_true(ei, distance, param);
                }
            }
            // 端点の外側の延長（同じ色の辺のつなぎ目での疑似距離の切れ目を埋める。msdfgen の addEdge）
            let ap = p - e.a;
            let bp = p - e.b;
            let add = ap.dot(e.a_bisector);
            let bdd = -bp.dot(e.b_bisector);
            if add > 0.0 {
                let mut pd = distance.distance;
                if perpendicular_distance(&mut pd, ap, -e.a_dir) {
                    pd = -pd;
                    for ch in 0..3 {
                        if color.has_channel(ch) {
                            sel.channels[ch].add_perpendicular(pd);
                        }
                    }
                }
            }
            if bdd > 0.0 {
                let mut pd = distance.distance;
                if perpendicular_distance(&mut pd, bp, e.b_dir) {
                    for ch in 0..3 {
                        if color.has_channel(ch) {
                            sel.channels[ch].add_perpendicular(pd);
                        }
                    }
                }
            }
        }
        // 辺の残らない輪郭・チャネルは「遠い」（符号は巻き数から）
        for c in 0..n_contours {
            let sign = self.far_sign(c, windings[c]);
            let far = self.contours[c].far;
            let sel = &mut scratch.selectors[c];
            for ch in sel.channels.iter_mut() {
                if ch.near_edge.is_none() {
                    *ch = ChannelSelector::far(sign, far);
                }
            }
        }
        self.combine(p, scratch)
    }

    /// 輪郭ごとの距離を合成する（msdfgen の OverlappingContourCombiner::distance）。
    fn combine(&self, p: Vec2, scratch: &mut PointScratch) -> MtsdfDistances {
        let edges = &self.edges;
        let n = self.contours.len();
        let mut shape_sel = MultiSelector::EMPTY;
        let mut inner_sel = MultiSelector::EMPTY;
        let mut outer_sel = MultiSelector::EMPTY;
        // 輪郭ごとの距離（下で何度も使うので先に求める）
        for c in 0..n {
            scratch.per_contour[c] = scratch.selectors[c].distances(p, edges);
        }
        let per_contour = &scratch.per_contour;
        for c in 0..n {
            let d = resolve(&per_contour[c]);
            shape_sel.merge(&scratch.selectors[c]);
            if self.contours[c].orientation > 0 && d >= 0.0 {
                inner_sel.merge(&scratch.selectors[c]);
            }
            if self.contours[c].orientation < 0 && d <= 0.0 {
                outer_sel.merge(&scratch.selectors[c]);
            }
        }
        let shape_distance = shape_sel.distances(p, edges);
        let inner_distance = inner_sel.distances(p, edges);
        let outer_distance = outer_sel.distances(p, edges);
        let inner_scalar = resolve(&inner_distance);
        let outer_scalar = resolve(&outer_distance);

        let mut distance: MtsdfDistances;
        let winding: i32;
        if inner_scalar >= 0.0 && inner_scalar.abs() <= outer_scalar.abs() {
            distance = inner_distance;
            winding = 1;
            for c in 0..n {
                if self.contours[c].orientation > 0 {
                    let cd = per_contour[c];
                    if resolve(&cd).abs() < outer_scalar.abs() && resolve(&cd) > resolve(&distance) {
                        distance = cd;
                    }
                }
            }
        } else if outer_scalar <= 0.0 && outer_scalar.abs() < inner_scalar.abs() {
            distance = outer_distance;
            winding = -1;
            for c in 0..n {
                if self.contours[c].orientation < 0 {
                    let cd = per_contour[c];
                    if resolve(&cd).abs() < inner_scalar.abs() && resolve(&cd) < resolve(&distance) {
                        distance = cd;
                    }
                }
            }
        } else {
            return shape_distance;
        }
        for c in 0..n {
            if self.contours[c].orientation != winding {
                let cd = per_contour[c];
                if resolve(&cd) * resolve(&distance) >= 0.0 && resolve(&cd).abs() < resolve(&distance).abs() {
                    distance = cd;
                }
            }
        }
        if resolve(&distance) == resolve(&shape_distance) {
            distance = shape_distance;
        }
        distance
    }

    /// 任意の点の MTSDF の距離（全ての辺を候補にする。誤差の補正・試験用）。
    pub fn mtsdf_at(&self, p: Vec2, scratch: &mut PointScratch) -> MtsdfDistances {
        let mut windings = std::mem::take(&mut scratch.windings);
        let all = std::mem::take(&mut scratch.all_edges);
        self.windings_at(p, &mut windings);
        let d = self.evaluate(p, &all, &windings, false, scratch);
        scratch.windings = windings;
        scratch.all_edges = all;
        d
    }

    /// 任意の点の 1 チャネルの疑似距離（全ての辺を 1 色とみなす。誤差の補正で「本来の値」に使う）。
    ///
    /// 符号は `generate` のテクセルと同じく走査線の内外（非ゼロ規則）に直す（2026-10-03。レビュー #16）。
    /// 直さないと、合成の符号と内外が食い違う所（自己交差・向きの誤った輪郭）で基準が 0.5 を挟んで反転し、
    /// 補正が細い隙間を平らにしたり偽の縁を残したりする。
    pub fn pseudo_distance_at(&self, p: Vec2, scratch: &mut PointScratch) -> f64 {
        let mut windings = std::mem::take(&mut scratch.windings);
        let all = std::mem::take(&mut scratch.all_edges);
        self.windings_at(p, &mut windings);
        let inside = windings.iter().sum::<i32>() != 0;
        let d = self.evaluate(p, &all, &windings, true, scratch);
        scratch.windings = windings;
        scratch.all_edges = all;
        corrected_pseudo_distance(d[0], inside)
    }

    /// 点が字の内側か（全輪郭の巻き数の和が 0 でない＝非ゼロ規則）。
    pub fn is_inside(&self, p: Vec2, scratch: &mut PointScratch) -> bool {
        let mut windings = std::mem::take(&mut scratch.windings);
        self.windings_at(p, &mut windings);
        let inside = windings.iter().sum::<i32>() != 0;
        scratch.windings = windings;
        inside
    }

    /// 格子の全テクセルの MTSDF の距離を求め、符号を直す（行ごとに並列）。
    pub fn generate(&self, grid: &FieldGrid) -> MtsdfField {
        let width = grid.width;
        let height = grid.height;
        let mut texels = vec![[0.0f64; 4]; width * height];
        let mut inside = vec![false; width * height];
        let n_contours = self.contours.len();
        texels
            .par_chunks_mut(width.max(1))
            .zip(inside.par_chunks_mut(width.max(1)))
            .enumerate()
            .for_each_init(
                || (self.scratch(), Vec::<u32>::new(), Vec::<(f64, i32, usize)>::new(), vec![0i32; n_contours]),
                |(scratch, candidates, crossings, windings), (j, (row, row_inside))| {
                    let y = grid.center(0, j).y;
                    // この行で cutoff 以内に来うる辺（縦の範囲だけで絞る）
                    candidates.clear();
                    for (ei, e) in self.edges.iter().enumerate() {
                        if y >= e.bb_min.y - self.cutoff && y <= e.bb_max.y + self.cutoff {
                            candidates.push(ei as u32);
                        }
                    }
                    // この行の走査線の交差（x の昇順）
                    crossings.clear();
                    for &si in self.band_at(y) {
                        let s = &self.polylines[si as usize];
                        if let Some((x, dir)) = crossing(s, y) {
                            crossings.push((x, dir, s.contour));
                        }
                    }
                    crossings.sort_by(|a, b| a.0.partial_cmp(&b.0).unwrap_or(std::cmp::Ordering::Equal));
                    windings.iter_mut().for_each(|w| *w = 0);
                    let mut next_crossing = 0usize;
                    for i in 0..width {
                        let p = grid.center(i, j);
                        // p より左の交差を巻き数へ足す
                        while next_crossing < crossings.len() && crossings[next_crossing].0 < p.x {
                            let (_, dir, c) = crossings[next_crossing];
                            windings[c] += dir;
                            next_crossing += 1;
                        }
                        let fill = windings.iter().sum::<i32>() != 0;
                        let mut d = self.evaluate(p, candidates, windings, false, scratch);
                        correct_sign(&mut d, fill);
                        row[i] = d;
                        row_inside[i] = fill;
                    }
                },
            );
        MtsdfField { width, height, texels, inside }
    }
}

/// 折れ線を y の帯（高さ `POLY_BAND_HEIGHT`）へ振り分けた索引と、帯 0 の下端の y。
///
/// 1 本の折れ線は y の範囲が掛かる全ての帯に入る（どの帯で問い合わせても、その高さを横切る折れ線は漏れない）。
fn band_index(polylines: &[PolySegment]) -> (Vec<Vec<u32>>, f64) {
    if polylines.is_empty() {
        return (Vec::new(), 0.0);
    }
    let y0 = polylines.iter().map(|s| s.a.y.min(s.b.y)).fold(f64::MAX, f64::min).floor();
    let y1 = polylines.iter().map(|s| s.a.y.max(s.b.y)).fold(-f64::MAX, f64::max);
    let count = ((y1 - y0) / POLY_BAND_HEIGHT).floor() as usize + 1;
    let mut bands = vec![Vec::new(); count];
    for (i, s) in polylines.iter().enumerate() {
        let k0 = ((s.a.y.min(s.b.y) - y0) / POLY_BAND_HEIGHT).floor() as usize;
        let k1 = (((s.a.y.max(s.b.y) - y0) / POLY_BAND_HEIGHT).floor() as usize).min(count - 1);
        for band in &mut bands[k0..=k1] {
            band.push(i as u32);
        }
    }
    (bands, y0)
}

/// 端点の延長線への垂直距離（msdfgen の getPerpendicularDistance）: 点が端点の外側（`edge_dir` の向き）にあり、
/// 延長線への距離が今の距離より近ければ置き換えて真を返す。
#[inline]
fn perpendicular_distance(distance: &mut f64, ep: Vec2, edge_dir: Vec2) -> bool {
    let ts = ep.dot(edge_dir);
    if ts > 0.0 {
        let perpendicular = ep.cross(edge_dir);
        if perpendicular.abs() < distance.abs() {
            *distance = perpendicular;
            return true;
        }
    }
    false
}

/// 折れ線の 1 本と水平線 y の交差（x と向き）。端の扱いは半開区間 [下, 上) で 2 重に数えない。
#[inline]
fn crossing(s: &PolySegment, y: f64) -> Option<(f64, i32)> {
    let (a, b) = (s.a, s.b);
    if (a.y <= y && y < b.y) || (b.y <= y && y < a.y) {
        let t = (y - a.y) / (b.y - a.y);
        let x = a.x + (b.x - a.x) * t;
        let dir = if b.y > a.y { 1 } else { -1 };
        Some((x, dir))
    } else {
        None
    }
}

/// 1 チャネルの疑似距離の符号を内外にそろえる（`correct_sign` の 1 チャネル版。0 はそのまま）。
#[inline]
fn corrected_pseudo_distance(d: f64, inside: bool) -> f64 {
    if d != 0.0 && (d > 0.0) != inside {
        -d
    } else {
        d
    }
}

/// 符号を直す: 中央値の符号が内外と食い違えば全チャネルを反転し、真の距離は内外の符号にそろえる。
#[inline]
fn correct_sign(d: &mut MtsdfDistances, inside: bool) {
    let m = median3(d[0], d[1], d[2]);
    if m != 0.0 && (m > 0.0) != inside {
        d[0] = -d[0];
        d[1] = -d[1];
        d[2] = -d[2];
    }
    let a = d[3].abs();
    d[3] = if inside { a } else { -a };
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::font::msdf::edge_color::{color_edges, ColoringStrategy};
    use crate::engine::core::font::msdf::outline::Contour;

    /// 時計回り（y 上向き）の正方形 [0, s]²。
    fn square(s: f64) -> Shape {
        Shape {
            contours: vec![Contour {
                edges: vec![
                    EdgeSegment::line(v2(0.0, 0.0), v2(0.0, s)),
                    EdgeSegment::line(v2(0.0, s), v2(s, s)),
                    EdgeSegment::line(v2(s, s), v2(s, 0.0)),
                    EdgeSegment::line(v2(s, 0.0), v2(0.0, 0.0)),
                ],
            }],
        }
    }

    /// 正方形からの真の符号つき距離（解析解）。
    fn square_true_distance(s: f64, p: Vec2) -> f64 {
        let dx = (p.x - s * 0.5).abs() - s * 0.5;
        let dy = (p.y - s * 0.5).abs() - s * 0.5;
        let outside = v2(dx.max(0.0), dy.max(0.0)).length();
        let inside = dx.max(dy).min(0.0);
        -(outside + inside)
    }

    /// 【解析解との比較】正方形の真の距離（アルファ）は全てのテクセルで解析解と一致し、中央値は内側で真の距離と同じ・
    /// 角の外側では辺の延長への距離（疑似距離。角が尖る）になる。
    #[test]
    fn square_field_matches_analytic_distance() {
        const S: f64 = 10.0;
        const PAD: usize = 4;
        let mut shape = square(S);
        color_edges(&mut shape, ColoringStrategy::Simple);
        let prepared = PreparedShape::new(&shape, 100.0, 1000.0);
        let n = S as usize + 2 * PAD;
        let grid = FieldGrid { width: n, height: n, origin: v2(-(PAD as f64) + 0.5, S + PAD as f64 - 0.5) };
        let field = prepared.generate(&grid);
        for j in 0..n {
            for i in 0..n {
                let p = grid.center(i, j);
                let d = field.texels[j * n + i];
                let expect = square_true_distance(S, p);
                assert!((d[3] - expect).abs() < 1e-9, "真の距離 {p:?}: {} vs {expect}", d[3]);
                let m = median3(d[0], d[1], d[2]);
                // 角の外側の領域（両方の軸で外）以外では中央値 = 真の距離
                let corner_region = (p.x < 0.0 || p.x > S) && (p.y < 0.0 || p.y > S);
                if !corner_region {
                    assert!((m - expect).abs() < 1e-9, "中央値 {p:?}: {m} vs {expect}");
                } else {
                    // 角の外: 中央値は 2 本の延長線への距離の大きいほう（チェビシェフ距離。尖った角の等値線）
                    let cheb = -((p.x - S * 0.5).abs() - S * 0.5).max((p.y - S * 0.5).abs() - S * 0.5);
                    assert!((m - cheb).abs() < 1e-9, "角の外の中央値 {p:?}: {m} vs {cheb}");
                }
                assert_eq!(field.inside[j * n + i], expect > 0.0, "内外 {p:?}");
            }
        }
    }

    /// 円（2 本の 3 次の近似）の真の距離は半径との差にほぼ一致する（3 次のベジェの円の近似の誤差まで）。
    #[test]
    fn circle_true_distance_matches_radius() {
        const R: f64 = 8.0;
        const K: f64 = 0.552_284_749_8; // 4 本の 3 次で円を近似する制御点の係数
        let c = v2(0.0, 0.0);
        let pts = [v2(-R, 0.0), v2(0.0, R), v2(R, 0.0), v2(0.0, -R)];
        let mut edges = Vec::new();
        for k in 0..4 {
            let a = pts[k];
            let b = pts[(k + 1) % 4];
            let ta = v2(-(a.y - c.y), a.x - c.x) * (-K); // 時計回りの接線
            let tb = v2(-(b.y - c.y), b.x - c.x) * (-K);
            edges.push(EdgeSegment::cubic(a, a + ta, b - tb, b));
        }
        let mut shape = Shape { contours: vec![Contour { edges }] };
        shape.orient();
        color_edges(&mut shape, ColoringStrategy::InkTrap);
        let prepared = PreparedShape::new(&shape, 100.0, 1000.0);
        let mut scratch = prepared.scratch();
        for &(x, y) in &[(0.0, 0.0), (3.0, 1.0), (7.5, 0.0), (9.0, 2.0), (-12.0, 0.5), (0.0, -5.0)] {
            let p = v2(x, y);
            let d = prepared.mtsdf_at(p, &mut scratch);
            let expect = R - p.length();
            assert!((d[3] - expect).abs() < 0.03, "円の真の距離 {p:?}: {} vs {expect}", d[3]);
            let m = median3(d[0], d[1], d[2]);
            assert!((m - expect).abs() < 0.03, "滑らかな輪郭の中央値 {p:?}: {m} vs {expect}");
        }
    }

    /// 重なった 2 つの四角（どちらも外側）: 重なりの中の内側の辺の上で距離が 0 に落ちない
    /// （msdfgen と同じく「点を含む輪郭のうち、その輪郭の縁から最も深い距離」になる。和集合の縁までの真の距離ではない）。
    #[test]
    fn overlapping_contours_hide_inner_edges() {
        // [0,10]x[0,10] と [6,16]x[0,10] が x=6..10 で重なる
        let sq = |x0: f64| Contour {
            edges: vec![
                EdgeSegment::line(v2(x0, 0.0), v2(x0, 10.0)),
                EdgeSegment::line(v2(x0, 10.0), v2(x0 + 10.0, 10.0)),
                EdgeSegment::line(v2(x0 + 10.0, 10.0), v2(x0 + 10.0, 0.0)),
                EdgeSegment::line(v2(x0 + 10.0, 0.0), v2(x0, 0.0)),
            ],
        };
        let mut shape = Shape { contours: vec![sq(0.0), sq(6.0)] };
        color_edges(&mut shape, ColoringStrategy::Simple);
        let prepared = PreparedShape::new(&shape, 100.0, 1000.0);
        let mut scratch = prepared.scratch();
        // 重なりの中: x = 6（右の四角の左の縁の上）・x = 10（左の四角の右の縁の上）は、もう一方の四角の中で 4 の深さ、
        // 真ん中 x = 8 は両方の縁から 2（0 に落ちる継ぎ目は出ない）
        for &(x, expect) in &[(6.0, 4.0), (8.0, 2.0), (10.0, 4.0)] {
            let d = prepared.mtsdf_at(v2(x, 5.0), &mut scratch);
            assert!((d[3] - expect).abs() < 1e-9, "x={x}: 真の距離 {} （期待 {expect}）", d[3]);
            assert!((median3(d[0], d[1], d[2]) - expect).abs() < 1e-9, "x={x}: 中央値");
        }
        // 外側の本当の縁の近くは正しい距離
        let d = prepared.mtsdf_at(v2(1.0, 5.0), &mut scratch);
        assert!((d[3] - 1.0).abs() < 1e-9, "左の縁の近く {}", d[3]);
    }

    /// 【「図」の不具合の再発防止】大きな枠（外側の輪郭 + 穴）の中に小さな四角（外側の輪郭）があり、枠の辺はどちらも打ち切りより遠い点:
    /// 遠い輪郭どうしが同点にならず、近い小さな四角への距離（外側・負）が選ばれる。
    #[test]
    fn far_nested_contours_do_not_hide_near_contour() {
        let rect = rect_contour;
        // 枠: 外 [0,60]²・穴 [4,56]²（反時計回り）・中の四角 [28,32]×[28,32]
        let mut shape = Shape { contours: vec![rect(0.0, 0.0, 60.0, 60.0, true), rect(4.0, 4.0, 56.0, 56.0, false), rect(28.0, 28.0, 32.0, 32.0, true)] };
        color_edges(&mut shape, ColoringStrategy::InkTrap);
        let cut = PreparedShape::new(&shape, 6.5, 13.0);
        let full = PreparedShape::new(&shape, 1000.0, 10000.0);
        let mut s1 = cut.scratch();
        let mut s2 = full.scratch();
        // 中の四角の左 2 テクセル（穴の中＝字の外）: 枠の辺は 24 テクセル以上遠い
        let p = v2(26.0, 30.0);
        let a = cut.mtsdf_at(p, &mut s1);
        let b = full.mtsdf_at(p, &mut s2);
        assert!((b[3] + 2.0).abs() < 1e-9, "打ち切らない真の距離 {}", b[3]);
        assert!((a[3] - b[3]).abs() < 1e-9, "打ち切っても近い輪郭の距離 {}（{}）", a[3], b[3]);
        assert!((median3(a[0], a[1], a[2]) - median3(b[0], b[1], b[2])).abs() < 1e-9);
    }

    /// 時計回り（y 上向き）の長方形の輪郭（`clockwise` が偽なら反時計回り＝穴）。
    fn rect_contour(x0: f64, y0: f64, x1: f64, y1: f64, clockwise: bool) -> Contour {
        let mut c = Contour {
            edges: vec![
                EdgeSegment::line(v2(x0, y0), v2(x0, y1)),
                EdgeSegment::line(v2(x0, y1), v2(x1, y1)),
                EdgeSegment::line(v2(x1, y1), v2(x1, y0)),
                EdgeSegment::line(v2(x1, y0), v2(x0, y0)),
            ],
        };
        if !clockwise {
            c.reverse();
        }
        c
    }

    /// 【レビュー #4】外枠 O[0,60]²・穴 H[4,56]²・中の四角 I[28,32]²・O の最初の辺の中点 (0,30) に重なる小さな輪郭 Q[-2,2]×[28,32]。
    /// 以前は Q が (0,30) を含むので O の深さが 1 になり、穴 H と同点 → 点 (26,30) で O が選ばれて I の左の傾き（-2）が消えていた。
    /// 深さを全ての辺の中点で数えると O は 0・H は 1・I は 2 で、打ち切っても打ち切らない値（-2）と同じになる。
    #[test]
    fn overlapping_small_contour_does_not_tie_outer_and_hole() {
        let mut shape = Shape {
            contours: vec![
                rect_contour(0.0, 0.0, 60.0, 60.0, true),
                rect_contour(4.0, 4.0, 56.0, 56.0, false),
                rect_contour(28.0, 28.0, 32.0, 32.0, true),
                rect_contour(-2.0, 28.0, 2.0, 32.0, true),
            ],
        };
        color_edges(&mut shape, ColoringStrategy::InkTrap);
        let cut = PreparedShape::new(&shape, 6.5, 13.0);
        assert_eq!(cut.nesting_depths(&shape), vec![0, 1, 2, 0], "O・H・I・Q の深さ");
        // 遠い距離はどの 2 つも同点にならない（深さ → 輪郭の番号の順）
        for a in 0..4 {
            for b in (a + 1)..4 {
                assert!(cut.contours[a].far != cut.contours[b].far, "輪郭 {a} と {b} の遠い距離が同点");
            }
        }
        assert!(cut.contours[0].far > cut.contours[1].far && cut.contours[1].far > cut.contours[2].far, "深いほど近い");
        let full = PreparedShape::new(&shape, 1000.0, 10000.0);
        let (mut s1, mut s2) = (cut.scratch(), full.scratch());
        for p in [v2(26.0, 30.0), v2(27.0, 30.0), v2(30.0, 26.5), v2(33.5, 30.0)] {
            let a = cut.mtsdf_at(p, &mut s1);
            let b = full.mtsdf_at(p, &mut s2);
            assert!((a[3] - b[3]).abs() < 1e-9, "{p:?}: 打ち切った真の距離 {} と打ち切らない {}", a[3], b[3]);
            assert!((median3(a[0], a[1], a[2]) - median3(b[0], b[1], b[2])).abs() < 1e-9, "{p:?}: 中央値");
        }
        let d = cut.mtsdf_at(v2(26.0, 30.0), &mut s1);
        assert!((d[3] + 2.0).abs() < 1e-9, "I の左 2 テクセル（穴の中＝字の外）: {}", d[3]);
        // 格子の全体を焼いても、I のまわり（I から 3 テクセル以内）は打ち切らない値と同じ
        let grid = FieldGrid { width: 70, height: 70, origin: v2(-4.5, 64.5) };
        let (fa, fb) = (cut.generate(&grid), full.generate(&grid));
        for j in 0..grid.height {
            for i in 0..grid.width {
                let p = grid.center(i, j);
                let near_i = p.x > 25.0 && p.x < 35.0 && p.y > 25.0 && p.y < 35.0;
                if near_i {
                    let k = j * grid.width + i;
                    assert!((fa.texels[k][3] - fb.texels[k][3]).abs() < 1e-9, "{p:?}: {} vs {}", fa.texels[k][3], fb.texels[k][3]);
                }
            }
        }
    }

    /// 深さが同じ輪郭どうし（離れた 2 つの外側の四角）は番号で決まる（番号の大きいほうがわずかに近い）。差は深さ 1 段の半分未満。
    #[test]
    fn equal_depth_far_contours_are_ordered_by_index() {
        let mut shape = Shape { contours: vec![rect_contour(0.0, 0.0, 10.0, 10.0, true), rect_contour(40.0, 0.0, 50.0, 10.0, true)] };
        color_edges(&mut shape, ColoringStrategy::Simple);
        let prepared = PreparedShape::new(&shape, 6.5, 13.0);
        assert_eq!(prepared.nesting_depths(&shape), vec![0, 0]);
        let (f0, f1) = (prepared.contours[0].far, prepared.contours[1].far);
        assert!(f0 > f1, "番号の大きい輪郭ほど近い: {f0} {f1}");
        assert!(f0 - f1 < FAR_DEPTH_STEP * 0.5, "同点を崩す差は深さ 1 段の半分未満");
        assert!(f1 > 6.5, "打ち切りより遠いまま（値が張り付く）");
    }

    /// 【レビュー #16】自己交差した輪郭（向きの逆な小さな輪がある 8 の字）: 小さな輪の中は非ゼロ規則で内側だが、
    /// 合成の疑似距離は外側（負）になる。誤差の補正の基準（`pseudo_distance_at`）は走査線の内外の符号にそろう。
    #[test]
    fn self_intersecting_contour_reference_distance_follows_fill() {
        // (0,0) → (0,20) → (30,0) → (30,10) → (0,0): 2 本の斜めの辺が (20, 20/3) で交わる。
        // 左の大きな三角は時計回り（面積 200）、右の小さな三角は反時計回り（面積 50）＝輪郭の向きは外側
        let mut shape = Shape {
            contours: vec![Contour {
                edges: vec![
                    EdgeSegment::line(v2(0.0, 0.0), v2(0.0, 20.0)),
                    EdgeSegment::line(v2(0.0, 20.0), v2(30.0, 0.0)),
                    EdgeSegment::line(v2(30.0, 0.0), v2(30.0, 10.0)),
                    EdgeSegment::line(v2(30.0, 10.0), v2(0.0, 0.0)),
                ],
            }],
        };
        color_edges(&mut shape, ColoringStrategy::InkTrap);
        let prepared = PreparedShape::new(&shape, 100.0, 1000.0);
        let mut scratch = prepared.scratch();
        // 小さな輪の中（右の縦の辺から 2）・大きな輪の中・外
        for (p, inside) in [(v2(28.0, 5.0), true), (v2(3.0, 8.0), true), (v2(15.0, 15.0), false), (v2(33.0, 5.0), false)] {
            assert_eq!(prepared.is_inside(p, &mut scratch), inside, "{p:?} の内外");
            let d = prepared.pseudo_distance_at(p, &mut scratch);
            assert_eq!(d > 0.0, inside, "{p:?}: 基準の疑似距離 {d} の符号が内外と食い違う");
        }
        // 小さな輪の中の値の大きさは、いちばん近い辺（右の縦の辺）までの距離
        let d = prepared.pseudo_distance_at(v2(28.0, 5.0), &mut scratch);
        assert!((d - 2.0).abs() < 1e-9, "小さな輪の中の基準の距離 {d}");
        // 焼いた距離場も同じ: 縁から 1 テクセルより深いテクセルは、中央値の符号が内外と一致する
        let grid = FieldGrid { width: 40, height: 30, origin: v2(-4.5, 24.5) };
        let field = prepared.generate(&grid);
        for k in 0..grid.width * grid.height {
            let d = field.texels[k];
            if d[3].abs() > 1.0 {
                assert_eq!(median3(d[0], d[1], d[2]) > 0.0, field.inside[k], "テクセル {k} の中央値の符号");
            }
        }
    }

    /// 帯の索引で数えた巻き数は、全ての折れ線を見た巻き数と同じ（実際の字のいろいろな点で）。
    #[test]
    fn banded_windings_match_full_scan() {
        use ab_glyph::{Font, FontArc};
        let font = FontArc::try_from_slice(crate::engine::core::font::DEFAULT_FONT_BYTES).unwrap();
        for ch in ['図', '鬱', 'A', 'g', '0'] {
            let outline = font.outline(font.glyph_id(ch)).unwrap();
            let shape = Shape::from_outline_curves(&outline.curves, 0.04);
            let prepared = PreparedShape::new(&shape, 6.5, 13.0);
            let n = shape.contours.len();
            let (mut banded, mut full) = (vec![0; n], vec![0; n]);
            let (min, max) = shape.bounds().unwrap();
            for j in 0..60 {
                for i in 0..60 {
                    let p = v2(min.x - 2.0 + (max.x - min.x + 4.0) * (i as f64 + 0.37) / 60.0, min.y - 2.0 + (max.y - min.y + 4.0) * (j as f64 + 0.61) / 60.0);
                    prepared.windings_at(p, &mut banded);
                    full.iter_mut().for_each(|w| *w = 0);
                    for s in &prepared.polylines {
                        if let Some((x, dir)) = crossing(s, p.y) {
                            if x < p.x {
                                full[s.contour] += dir;
                            }
                        }
                    }
                    assert_eq!(banded, full, "'{ch}' {p:?}");
                }
            }
        }
    }

    /// 打ち切り: cutoff より遠いテクセルは「遠い」値（符号は内外どおり）になり、近いテクセルは打ち切らない値と同じ。
    #[test]
    fn cutoff_keeps_near_values_and_sign() {
        const S: f64 = 20.0;
        let mut shape = square(S);
        color_edges(&mut shape, ColoringStrategy::Simple);
        let full = PreparedShape::new(&shape, 1000.0, 10000.0);
        let cut = PreparedShape::new(&shape, 3.0, 50.0);
        let n = 28usize;
        let grid = FieldGrid { width: n, height: n, origin: v2(-3.5, S + 3.5) };
        let a = full.generate(&grid);
        let b = cut.generate(&grid);
        for k in 0..n * n {
            let near = a.texels[k][3].abs() <= 3.0;
            if near {
                assert!((a.texels[k][3] - b.texels[k][3]).abs() < 1e-9, "近いテクセルの真の距離が違う");
                assert!((median3(a.texels[k][0], a.texels[k][1], a.texels[k][2]) - median3(b.texels[k][0], b.texels[k][1], b.texels[k][2])).abs() < 1e-9);
            } else {
                assert_eq!(b.texels[k][3] > 0.0, a.texels[k][3] > 0.0, "遠いテクセルの符号");
                assert!(b.texels[k][3].abs() >= 3.0, "遠いテクセルは cutoff 以上");
            }
        }
    }
}
