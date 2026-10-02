// ============================================================
//  font/msdf/segment.rs — 輪郭の辺（直線・2 次・3 次のベジェ）と、辺への符号つき距離
//
//  【役割】
//  MSDF の 1 本の辺。点・接線・外接矩形・3 等分・反転と、点から辺への **符号つき距離**
//  （msdfgen の EdgeSegment::signedDistance と同じ式）・端点の延長への垂直距離（疑似距離）を持つ。
//
//  【符号の決まり（y 上向きの空間）】
//  点 p から辺 a→b への距離の符号は `cross(p - a, b - a)` の符号（msdfgen と同じ）。y 上向きの空間で
//  **時計回り**の輪郭（TrueType の外側の輪郭の向き）なら内側が正になる。逆向きの書体（CFF の外側が反時計回り）は
//  outline.rs が輪郭をまとめて反転してこの向きにそろえる。
//
//  【退化した辺】
//  2 次の制御点が端点と重なる・3 次の両方の制御点が端点と重なるものは、msdfgen と同じく制御点を
//  端点の間へ置き直す（接線が 0 にならないように）。長さ 0 の辺は outline.rs が捨てる。
// ============================================================

use super::edge_color::EdgeColor;
use super::geometry::{non_zero_sign, solve_cubic, SignedDistance, Vec2};

/// 3 次のベジェの最近点の探索の初期値の数（0, 1/4, …, 1 の 5 点。msdfgen の MSDFGEN_CUBIC_SEARCH_STARTS）。
const CUBIC_SEARCH_STARTS: usize = 4;
/// 3 次のベジェの最近点の探索のニュートン法の反復の数（msdfgen の MSDFGEN_CUBIC_SEARCH_STEPS）。
const CUBIC_SEARCH_STEPS: usize = 4;
/// 3 等分の位置。
const ONE_THIRD: f64 = 1.0 / 3.0;
const TWO_THIRDS: f64 = 2.0 / 3.0;

/// 辺の形（制御点）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum SegmentShape {
    /// 直線 p0 → p1。
    Line([Vec2; 2]),
    /// 2 次のベジェ p0 → (p1) → p2。
    Quad([Vec2; 3]),
    /// 3 次のベジェ p0 → (p1, p2) → p3。
    Cubic([Vec2; 4]),
}

/// 色つきの辺（MSDF のどのチャネルに効くかを `color` が表す）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct EdgeSegment {
    pub shape: SegmentShape,
    pub color: EdgeColor,
}

impl EdgeSegment {
    /// 直線の辺（色は白＝全チャネル。色分けは edge_color.rs が後で決める）。
    pub fn line(p0: Vec2, p1: Vec2) -> Self {
        Self { shape: SegmentShape::Line([p0, p1]), color: EdgeColor::WHITE }
    }

    /// 2 次のベジェの辺。制御点が端点と重なるなら端点の中点へ置き直す（直線と同じ形の 2 次になる）。
    pub fn quad(p0: Vec2, p1: Vec2, p2: Vec2) -> Self {
        let p1 = if p1 == p0 || p1 == p2 { p0.lerp(p2, 0.5) } else { p1 };
        Self { shape: SegmentShape::Quad([p0, p1, p2]), color: EdgeColor::WHITE }
    }

    /// 3 次のベジェの辺。両方の制御点が端点と重なるなら 1/3・2/3 へ置き直す。
    pub fn cubic(p0: Vec2, p1: Vec2, p2: Vec2, p3: Vec2) -> Self {
        let (p1, p2) = if (p1 == p0 || p1 == p3) && (p2 == p0 || p2 == p3) {
            (p0.lerp(p3, ONE_THIRD), p0.lerp(p3, TWO_THIRDS))
        } else {
            (p1, p2)
        };
        Self { shape: SegmentShape::Cubic([p0, p1, p2, p3]), color: EdgeColor::WHITE }
    }

    /// 始点。
    pub fn start(&self) -> Vec2 {
        match &self.shape {
            SegmentShape::Line(p) => p[0],
            SegmentShape::Quad(p) => p[0],
            SegmentShape::Cubic(p) => p[0],
        }
    }

    /// 終点。
    pub fn end(&self) -> Vec2 {
        match &self.shape {
            SegmentShape::Line(p) => p[1],
            SegmentShape::Quad(p) => p[2],
            SegmentShape::Cubic(p) => p[3],
        }
    }

    /// 長さ 0 の辺か（全ての制御点が同じ点）。
    pub fn is_degenerate(&self) -> bool {
        match &self.shape {
            SegmentShape::Line(p) => p[0] == p[1],
            SegmentShape::Quad(p) => p[0] == p[1] && p[1] == p[2],
            SegmentShape::Cubic(p) => p[0] == p[1] && p[1] == p[2] && p[2] == p[3],
        }
    }

    /// パラメータ t（0..1）の点。
    pub fn point(&self, t: f64) -> Vec2 {
        match &self.shape {
            SegmentShape::Line(p) => p[0].lerp(p[1], t),
            SegmentShape::Quad(p) => p[0].lerp(p[1], t).lerp(p[1].lerp(p[2], t), t),
            SegmentShape::Cubic(p) => {
                let p12 = p[1].lerp(p[2], t);
                p[0].lerp(p[1], t).lerp(p12, t).lerp(p12.lerp(p[2].lerp(p[3], t), t), t)
            }
        }
    }

    /// パラメータ t の接線（長さは正規化しない）。0 になる所は端点どうしの向きで代える（msdfgen と同じ）。
    pub fn direction(&self, t: f64) -> Vec2 {
        match &self.shape {
            SegmentShape::Line(p) => p[1] - p[0],
            SegmentShape::Quad(p) => {
                let tangent = (p[1] - p[0]).lerp(p[2] - p[1], t);
                if tangent.is_zero() { p[2] - p[0] } else { tangent }
            }
            SegmentShape::Cubic(p) => {
                let a = p[1] - p[0];
                let b = p[2] - p[1];
                let c = p[3] - p[2];
                let tangent = a.lerp(b, t).lerp(b.lerp(c, t), t);
                if tangent.is_zero() {
                    if t == 0.0 {
                        return p[2] - p[0];
                    }
                    if t == 1.0 {
                        return p[3] - p[1];
                    }
                }
                tangent
            }
        }
    }

    /// 向きを反転した辺（輪郭の向きをそろえるとき）。
    pub fn reversed(&self) -> Self {
        let shape = match self.shape {
            SegmentShape::Line([a, b]) => SegmentShape::Line([b, a]),
            SegmentShape::Quad([a, b, c]) => SegmentShape::Quad([c, b, a]),
            SegmentShape::Cubic([a, b, c, d]) => SegmentShape::Cubic([d, c, b, a]),
        };
        Self { shape, color: self.color }
    }

    /// 辺の外接矩形（曲線は極値まで含めた正確な範囲）を `(min, max)` へ広げる。
    pub fn extend_bounds(&self, min: &mut Vec2, max: &mut Vec2) {
        let mut include = |p: Vec2| {
            min.x = min.x.min(p.x);
            min.y = min.y.min(p.y);
            max.x = max.x.max(p.x);
            max.y = max.y.max(p.y);
        };
        include(self.start());
        include(self.end());
        match &self.shape {
            SegmentShape::Line(_) => {}
            SegmentShape::Quad(p) => {
                // 導関数 = 2((p1-p0)(1-t) + (p2-p1)t) が 0 になる t（軸ごと）
                let bot = (p[1] - p[0]) - (p[2] - p[1]);
                for (num, den) in [((p[1] - p[0]).x, bot.x), ((p[1] - p[0]).y, bot.y)] {
                    if den != 0.0 {
                        let t = num / den;
                        if t > 0.0 && t < 1.0 {
                            include(self.point(t));
                        }
                    }
                }
            }
            SegmentShape::Cubic(p) => {
                // 導関数（2 次式）の根（軸ごと）
                let a0 = p[1] - p[0];
                let a1 = 2.0 * (p[2] - p[1] - a0);
                let a2 = p[3] - 3.0 * p[2] + 3.0 * p[1] - p[0];
                let mut roots = [0.0; 3];
                for (qa, qb, qc) in [(a2.x, a1.x, a0.x), (a2.y, a1.y, a0.y)] {
                    let n = super::geometry::solve_quadratic(&mut roots, qa, qb, qc);
                    for &t in roots.iter().take(n.max(0) as usize) {
                        if t > 0.0 && t < 1.0 {
                            include(self.point(t));
                        }
                    }
                }
            }
        }
    }

    /// 辺を 3 等分した 3 本（色は元のまま。輪郭の辺が 1〜2 本しかないときの色分けに使う）。
    pub fn split_in_thirds(&self) -> [EdgeSegment; 3] {
        let color = self.color;
        let with = |shape: SegmentShape| EdgeSegment { shape, color };
        match self.shape {
            SegmentShape::Line([a, b]) => {
                let p1 = self.point(ONE_THIRD);
                let p2 = self.point(TWO_THIRDS);
                [with(SegmentShape::Line([a, p1])), with(SegmentShape::Line([p1, p2])), with(SegmentShape::Line([p2, b]))]
            }
            SegmentShape::Quad(_) | SegmentShape::Cubic(_) => {
                // de Casteljau で 1/3 の所を切り、残り（1/3..1）を真ん中で切る
                let (first, rest) = self.split_at(ONE_THIRD);
                let (second, third) = rest.split_at(0.5);
                [first, second, third]
            }
        }
    }

    /// パラメータ t で 2 本に切る（de Casteljau。色は元のまま）。
    pub fn split_at(&self, t: f64) -> (EdgeSegment, EdgeSegment) {
        let color = self.color;
        let with = |shape: SegmentShape| EdgeSegment { shape, color };
        match self.shape {
            SegmentShape::Line([a, b]) => {
                let m = a.lerp(b, t);
                (with(SegmentShape::Line([a, m])), with(SegmentShape::Line([m, b])))
            }
            SegmentShape::Quad([a, b, c]) => {
                let ab = a.lerp(b, t);
                let bc = b.lerp(c, t);
                let m = ab.lerp(bc, t);
                (with(SegmentShape::Quad([a, ab, m])), with(SegmentShape::Quad([m, bc, c])))
            }
            SegmentShape::Cubic([a, b, c, d]) => {
                let ab = a.lerp(b, t);
                let bc = b.lerp(c, t);
                let cd = c.lerp(d, t);
                let abc = ab.lerp(bc, t);
                let bcd = bc.lerp(cd, t);
                let m = abc.lerp(bcd, t);
                (with(SegmentShape::Cubic([a, ab, abc, m])), with(SegmentShape::Cubic([m, bcd, cd, d])))
            }
        }
    }

    /// 曲線を折れ線へ近似したときの分割数（誤差がおおむね `tolerance` 以下になる数。直線は 1）。
    ///
    /// 2 次・3 次のベジェの弦からのずれは「2 階差分の大きさ ÷ (8 n²)」で抑えられる。
    pub fn flatten_steps(&self, tolerance: f64) -> usize {
        /// 分割数の上限（巨大な曲線で数が暴れないように）。
        const MAX_FLATTEN_STEPS: usize = 64;
        let second_diff = match &self.shape {
            SegmentShape::Line(_) => return 1,
            SegmentShape::Quad(p) => (p[0] - 2.0 * p[1] + p[2]).length(),
            SegmentShape::Cubic(p) => {
                let d1 = (p[0] - 2.0 * p[1] + p[2]).length();
                let d2 = (p[1] - 2.0 * p[2] + p[3]).length();
                // 3 次は 2 階差分（×6 の導関数の上限）を 2 次の式へ合わせて見積もる
                1.5 * d1.max(d2)
            }
        };
        let steps = (second_diff / (8.0 * tolerance)).sqrt().ceil() as usize;
        steps.clamp(1, MAX_FLATTEN_STEPS)
    }

    // ─── 距離 ───────────────────────────────────────────────

    /// 点 `origin` から辺への符号つき距離と、最近点のパラメータ（範囲外 = 端点の延長側）。
    pub fn signed_distance(&self, origin: Vec2) -> (SignedDistance, f64) {
        match &self.shape {
            SegmentShape::Line(p) => line_signed_distance(p, origin),
            SegmentShape::Quad(p) => self.quad_signed_distance(p, origin),
            SegmentShape::Cubic(p) => self.cubic_signed_distance(p, origin),
        }
    }

    /// 最近点が端点の外（param < 0 / > 1）なら、端点からの延長線への垂直距離のほうが近ければそれに置き換える
    /// （疑似距離。msdfgen の distanceToPerpendicularDistance）。
    pub fn distance_to_perpendicular_distance(&self, distance: &mut SignedDistance, origin: Vec2, param: f64) {
        if param < 0.0 {
            let dir = self.direction(0.0).normalize(false);
            let aq = origin - self.start();
            let ts = aq.dot(dir);
            if ts < 0.0 {
                let perpendicular = aq.cross(dir);
                if perpendicular.abs() <= distance.distance.abs() {
                    distance.distance = perpendicular;
                    distance.dot = 0.0;
                }
            }
        } else if param > 1.0 {
            let dir = self.direction(1.0).normalize(false);
            let bq = origin - self.end();
            let ts = bq.dot(dir);
            if ts > 0.0 {
                let perpendicular = bq.cross(dir);
                if perpendicular.abs() <= distance.distance.abs() {
                    distance.distance = perpendicular;
                    distance.dot = 0.0;
                }
            }
        }
    }

    /// 2 次のベジェへの符号つき距離（最近点の条件は 3 次方程式）。
    fn quad_signed_distance(&self, p: &[Vec2; 3], origin: Vec2) -> (SignedDistance, f64) {
        let qa = p[0] - origin;
        let ab = p[1] - p[0];
        let br = p[2] - p[1] - ab;
        let a = br.dot(br);
        let b = 3.0 * ab.dot(br);
        let c = 2.0 * ab.dot(ab) + qa.dot(br);
        let d = qa.dot(ab);
        let mut t = [0.0; 3];
        let solutions = solve_cubic(&mut t, a, b, c, d);

        // 始点
        let mut ep_dir = self.direction(0.0);
        let mut min_distance = non_zero_sign(ep_dir.cross(qa)) * qa.length();
        let mut param = -qa.dot(ep_dir) / ep_dir.dot(ep_dir);
        // 終点
        {
            ep_dir = self.direction(1.0);
            let distance = (p[2] - origin).length();
            if distance < min_distance.abs() {
                min_distance = non_zero_sign(ep_dir.cross(p[2] - origin)) * distance;
                param = (origin - p[1]).dot(ep_dir) / ep_dir.dot(ep_dir);
            }
        }
        // 途中の極値
        for &ti in t.iter().take(solutions.max(0) as usize) {
            if ti > 0.0 && ti < 1.0 {
                let qe = qa + 2.0 * ti * ab + ti * ti * br;
                let distance = qe.length();
                if distance <= min_distance.abs() {
                    min_distance = non_zero_sign((ab + ti * br).cross(qe)) * distance;
                    param = ti;
                }
            }
        }
        finish_curve_distance(self, min_distance, param, qa, p[2] - origin)
    }

    /// 3 次のベジェへの符号つき距離（初期値 5 点からのニュートン法）。
    fn cubic_signed_distance(&self, p: &[Vec2; 4], origin: Vec2) -> (SignedDistance, f64) {
        let qa = p[0] - origin;
        let ab = p[1] - p[0];
        let br = p[2] - p[1] - ab;
        let as_ = (p[3] - p[2]) - (p[2] - p[1]) - br;

        // 始点
        let mut ep_dir = self.direction(0.0);
        let mut min_distance = non_zero_sign(ep_dir.cross(qa)) * qa.length();
        let mut param = -qa.dot(ep_dir) / ep_dir.dot(ep_dir);
        // 終点
        {
            ep_dir = self.direction(1.0);
            let distance = (p[3] - origin).length();
            if distance < min_distance.abs() {
                min_distance = non_zero_sign(ep_dir.cross(p[3] - origin)) * distance;
                param = (ep_dir - (p[3] - origin)).dot(ep_dir) / ep_dir.dot(ep_dir);
            }
        }
        // 途中: いくつかの初期値からニュートン法で最近点を探す
        for i in 0..=CUBIC_SEARCH_STARTS {
            let mut t = i as f64 / CUBIC_SEARCH_STARTS as f64;
            let mut qe = qa + 3.0 * t * ab + 3.0 * t * t * br + t * t * t * as_;
            for _ in 0..CUBIC_SEARCH_STEPS {
                let d1 = 3.0 * ab + 6.0 * t * br + 3.0 * t * t * as_;
                let d2 = 6.0 * br + 6.0 * t * as_;
                t -= qe.dot(d1) / (d1.dot(d1) + qe.dot(d2));
                if t <= 0.0 || t >= 1.0 {
                    break;
                }
                qe = qa + 3.0 * t * ab + 3.0 * t * t * br + t * t * t * as_;
                let distance = qe.length();
                if distance < min_distance.abs() {
                    min_distance = non_zero_sign(d1.cross(qe)) * distance;
                    param = t;
                }
            }
        }
        finish_curve_distance(self, min_distance, param, qa, p[3] - origin)
    }
}

/// 直線への符号つき距離（msdfgen の LinearSegment::signedDistance）。
fn line_signed_distance(p: &[Vec2; 2], origin: Vec2) -> (SignedDistance, f64) {
    let aq = origin - p[0];
    let ab = p[1] - p[0];
    let param = aq.dot(ab) / ab.dot(ab);
    let eq = (if param > 0.5 { p[1] } else { p[0] }) - origin;
    let endpoint_distance = eq.length();
    if param > 0.0 && param < 1.0 {
        // 辺の途中が最近点: 法線方向の距離（符号は cross(aq, ab) / |ab|）
        let ortho_distance = aq.cross(ab) / ab.length();
        if ortho_distance.abs() < endpoint_distance {
            return (SignedDistance::new(ortho_distance, 0.0), param);
        }
    }
    let dot = ab.normalize(false).dot(eq.normalize(false)).abs();
    (SignedDistance::new(non_zero_sign(aq.cross(ab)) * endpoint_distance, dot), param)
}

/// 曲線の距離の仕上げ: 最近点が端点なら、接線と「点 → 端点」（`qa` = 始点 − 点・`end_minus_origin` = 終点 − 点）の
/// 角の余弦の絶対値を副の値にする。
fn finish_curve_distance(seg: &EdgeSegment, min_distance: f64, param: f64, qa: Vec2, end_minus_origin: Vec2) -> (SignedDistance, f64) {
    if (0.0..=1.0).contains(&param) {
        return (SignedDistance::new(min_distance, 0.0), param);
    }
    let dot = if param < 0.5 {
        seg.direction(0.0).normalize(false).dot(qa.normalize(false)).abs()
    } else {
        seg.direction(1.0).normalize(false).dot(end_minus_origin.normalize(false)).abs()
    };
    (SignedDistance::new(min_distance, dot), param)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::font::msdf::geometry::v2;

    /// 直線: 途中が最近点なら法線方向の距離、符号は進行方向の右（y 上向きの時計回りの内側）が正。
    #[test]
    fn line_distance_and_sign() {
        // 左から右へ進む水平線（y 上向き）。右側 = 下（y が小さい側）が正
        let seg = EdgeSegment::line(v2(0.0, 0.0), v2(10.0, 0.0));
        let (below, t) = seg.signed_distance(v2(5.0, -2.0));
        assert!((below.distance - 2.0).abs() < 1e-12 && (t - 0.5).abs() < 1e-12, "{below:?}");
        let (above, _) = seg.signed_distance(v2(5.0, 3.0));
        assert!((above.distance + 3.0).abs() < 1e-12, "{above:?}");
        // 端点の外: 端点までの距離、param は範囲外
        let (beyond, t) = seg.signed_distance(v2(13.0, 4.0));
        assert!((beyond.distance.abs() - 5.0).abs() < 1e-12 && t > 1.0, "{beyond:?} {t}");
    }

    /// 2 次のベジェ: 総当たりで細かく刻んだ最近点の距離と一致する（符号も直線の決まりと同じ）。
    #[test]
    fn quad_distance_matches_dense_sampling() {
        let seg = EdgeSegment::quad(v2(0.0, 0.0), v2(5.0, 8.0), v2(10.0, 0.0));
        for &q in &[v2(5.0, 1.0), v2(5.0, 6.0), v2(-3.0, 2.0), v2(12.0, -1.0), v2(2.0, 4.5)] {
            let (d, _) = seg.signed_distance(q);
            let brute = (0..=20000).map(|i| (seg.point(i as f64 / 20000.0) - q).length()).fold(f64::MAX, f64::min);
            assert!((d.distance.abs() - brute).abs() < 1e-4, "{q:?}: {} vs {brute}", d.distance);
        }
    }

    /// 3 次のベジェ: 総当たりの最近点の距離と一致する。
    #[test]
    fn cubic_distance_matches_dense_sampling() {
        let seg = EdgeSegment::cubic(v2(0.0, 0.0), v2(2.0, 9.0), v2(8.0, -5.0), v2(10.0, 3.0));
        for &q in &[v2(5.0, 1.0), v2(1.0, 6.0), v2(9.0, -2.0), v2(-2.0, -1.0), v2(11.0, 6.0)] {
            let (d, _) = seg.signed_distance(q);
            let brute = (0..=20000).map(|i| (seg.point(i as f64 / 20000.0) - q).length()).fold(f64::MAX, f64::min);
            assert!((d.distance.abs() - brute).abs() < 1e-3, "{q:?}: {} vs {brute}", d.distance);
        }
    }

    /// 3 等分・切断は形を変えない（つなぎ目が一致し、元の曲線の点の上にある）。
    #[test]
    fn split_keeps_the_curve() {
        let seg = EdgeSegment::cubic(v2(0.0, 0.0), v2(2.0, 9.0), v2(8.0, -5.0), v2(10.0, 3.0));
        let parts = seg.split_in_thirds();
        assert_eq!(parts[0].start(), seg.start());
        assert_eq!(parts[2].end(), seg.end());
        assert!((parts[0].end() - seg.point(1.0 / 3.0)).length() < 1e-12);
        assert!((parts[1].end() - seg.point(2.0 / 3.0)).length() < 1e-9);
        assert!((parts[1].point(0.5) - seg.point(0.5)).length() < 1e-9);
    }

    /// 外接矩形は曲線の極値まで含む（制御点の矩形より狭く、曲線の点は全て中）。
    #[test]
    fn bounds_cover_the_curve_exactly() {
        let seg = EdgeSegment::quad(v2(0.0, 0.0), v2(5.0, 8.0), v2(10.0, 0.0));
        let mut min = v2(f64::MAX, f64::MAX);
        let mut max = v2(-f64::MAX, -f64::MAX);
        seg.extend_bounds(&mut min, &mut max);
        assert!((max.y - 4.0).abs() < 1e-12, "2 次の頂点の高さは制御点の半分: {max:?}");
        for i in 0..=100 {
            let p = seg.point(i as f64 / 100.0);
            assert!(p.x >= min.x - 1e-12 && p.x <= max.x + 1e-12 && p.y >= min.y - 1e-12 && p.y <= max.y + 1e-12);
        }
    }
}
