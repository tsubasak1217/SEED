// ============================================================
//  font/msdf/outline.rs — 書体の輪郭（ab_glyph の OutlineCurve の列）を MSDF の形（輪郭 × 辺）へ組み直す
//
//  【入力】ab_glyph の `Font::outline(glyph_id)`: フォントの単位（y 上向き）の曲線の **平らな列**
//  （直線・2 次・3 次）。輪郭の区切りは列に無いので、ここで組み直す。ab_glyph は輪郭の終わり（close）で
//  必ず「最後の点 → 輪郭の最初の点」の直線を足す（TrueType では長さ 0 の直線になる）ので、
//  「終点が輪郭の始点に戻った所」を区切りにする（下の `split_contours`）。
//
//  【出力】テクセルの単位（SEED の文字の大きさ = MTSDF_EM_PX テクセル）・y 上向きの `Shape`。
//  - 長さ 0 の辺は捨てる（接線が決まらない）。
//  - 辺が 1 本だけの輪郭は 3 等分する（色分けで 3 色を置くため。msdfgen の Shape::normalize と同じ）。
//  - 輪郭の向きをそろえる（外側の輪郭が y 上向きで時計回り＝内側の距離が正。逆なら全輪郭を反転する）。
//
//  【重なった輪郭】そのまま残す（距離の合成は distance.rs の「重なりを考える合成」が受け持つ）。
// ============================================================

use ab_glyph::OutlineCurve;

use super::geometry::{v2, Vec2};
use super::segment::EdgeSegment;

/// 1 つの閉じた輪郭（辺の列。終点が次の辺の始点、最後の辺の終点が最初の辺の始点）。
#[derive(Clone, Debug, Default)]
pub struct Contour {
    pub edges: Vec<EdgeSegment>,
}

impl Contour {
    /// 輪郭の向き（msdfgen の Contour::winding）: +1 = y 上向きで時計回り（外側）、-1 = 反時計回り（穴）、0 = 決まらない。
    ///
    /// 台形の公式 `(b.x - a.x)(a.y + b.y)` の和の符号。辺が 1〜2 本のときは曲線の途中の点も使う。
    pub fn winding(&self) -> i32 {
        let total = self.signed_area_sum();
        if total > 0.0 {
            1
        } else if total < 0.0 {
            -1
        } else {
            0
        }
    }

    /// 向きの判定に使う台形の公式の和（正 = 時計回り）。
    fn signed_area_sum(&self) -> f64 {
        let shoelace = |a: Vec2, b: Vec2| (b.x - a.x) * (a.y + b.y);
        match self.edges.len() {
            0 => 0.0,
            1 => {
                let e = &self.edges[0];
                let (a, b, c) = (e.point(0.0), e.point(1.0 / 3.0), e.point(2.0 / 3.0));
                shoelace(a, b) + shoelace(b, c) + shoelace(c, a)
            }
            2 => {
                let (e0, e1) = (&self.edges[0], &self.edges[1]);
                let (a, b, c, d) = (e0.point(0.0), e0.point(0.5), e1.point(0.0), e1.point(0.5));
                shoelace(a, b) + shoelace(b, c) + shoelace(c, d) + shoelace(d, a)
            }
            _ => {
                let mut total = 0.0;
                let mut prev = self.edges[self.edges.len() - 1].start();
                for e in &self.edges {
                    let cur = e.start();
                    total += shoelace(prev, cur);
                    prev = cur;
                }
                total
            }
        }
    }

    /// 輪郭の向きを反転する（辺の順と各辺の向きを逆にする）。
    pub fn reverse(&mut self) {
        self.edges.reverse();
        for e in &mut self.edges {
            *e = e.reversed();
        }
    }
}

/// 字形の全輪郭。
#[derive(Clone, Debug, Default)]
pub struct Shape {
    pub contours: Vec<Contour>,
}

impl Shape {
    /// ab_glyph の輪郭（フォントの単位）を `scale` 倍したテクセルの単位の形にする（y 上向きのまま）。
    ///
    /// 組み直し → 長さ 0 の辺を捨てる → 辺 1 本の輪郭を 3 等分 → 向きをそろえる、までを行う。
    pub fn from_outline_curves(curves: &[OutlineCurve], scale: f64) -> Shape {
        let to = |p: ab_glyph::Point| v2(f64::from(p.x) * scale, f64::from(p.y) * scale);
        let mut shape = Shape::default();
        for contour_curves in split_contours(curves) {
            let mut contour = Contour::default();
            for curve in contour_curves {
                let edge = match *curve {
                    OutlineCurve::Line(a, b) => EdgeSegment::line(to(a), to(b)),
                    OutlineCurve::Quad(a, b, c) => EdgeSegment::quad(to(a), to(b), to(c)),
                    OutlineCurve::Cubic(a, b, c, d) => EdgeSegment::cubic(to(a), to(b), to(c), to(d)),
                };
                if !edge.is_degenerate() {
                    contour.edges.push(edge);
                }
            }
            if !contour.edges.is_empty() {
                shape.contours.push(contour);
            }
        }
        shape.normalize();
        shape.orient();
        shape
    }

    /// 辺が 1 本だけの輪郭を 3 等分する（色分けで角の両側に別の色を置けるように）。
    pub fn normalize(&mut self) {
        for contour in &mut self.contours {
            if contour.edges.len() == 1 {
                contour.edges = contour.edges[0].split_in_thirds().to_vec();
            }
        }
    }

    /// 全輪郭の向きをそろえる: 向きの和が負（外側が反時計回りの書体）なら全輪郭を反転する。
    ///
    /// 外側の輪郭は穴より面積が大きいので、和の符号は外側の輪郭の向きで決まる。
    pub fn orient(&mut self) {
        let total: f64 = self.contours.iter().map(|c| c.signed_area_sum()).sum();
        if total < 0.0 {
            for contour in &mut self.contours {
                contour.reverse();
            }
        }
    }

    /// 辺の総数。
    pub fn edge_count(&self) -> usize {
        self.contours.iter().map(|c| c.edges.len()).sum()
    }

    /// 形の外接矩形（曲線の極値まで含む）。辺が無ければ None。
    pub fn bounds(&self) -> Option<(Vec2, Vec2)> {
        let mut min = v2(f64::MAX, f64::MAX);
        let mut max = v2(-f64::MAX, -f64::MAX);
        let mut any = false;
        for e in self.contours.iter().flat_map(|c| c.edges.iter()) {
            e.extend_bounds(&mut min, &mut max);
            any = true;
        }
        any.then_some((min, max))
    }
}

/// 曲線の平らな列を輪郭ごとに分ける。
///
/// 区切りの規則: 曲線の終点が輪郭の始点に戻り、かつ（次の曲線が無い・次の曲線の始点が今の終点と違う・
/// 今の曲線が長さ 0 の直線＝ab_glyph が close で足した閉じの線）なら、そこで輪郭を閉じる。
/// 途中で始点を通り過ぎる輪郭（次の曲線が続く）は閉じない。前の曲線の終点と次の曲線の始点が違えば、そこでも区切る。
fn split_contours(curves: &[OutlineCurve]) -> Vec<&[OutlineCurve]> {
    let start_of = |c: &OutlineCurve| match *c {
        OutlineCurve::Line(a, _) | OutlineCurve::Quad(a, _, _) | OutlineCurve::Cubic(a, _, _, _) => a,
    };
    let end_of = |c: &OutlineCurve| match *c {
        OutlineCurve::Line(_, b) | OutlineCurve::Quad(_, _, b) | OutlineCurve::Cubic(_, _, _, b) => b,
    };
    let is_zero_line = |c: &OutlineCurve| matches!(*c, OutlineCurve::Line(a, b) if a == b);

    let mut out = Vec::new();
    let mut begin = 0usize;
    for i in 0..curves.len() {
        let curve = &curves[i];
        let contour_start = start_of(&curves[begin]);
        let end = end_of(curve);
        let next = curves.get(i + 1);
        let closes = end == contour_start && (next.is_none_or(|n| start_of(n) != end) || is_zero_line(curve));
        let breaks = next.is_some_and(|n| start_of(n) != end);
        if closes || breaks || next.is_none() {
            out.push(&curves[begin..=i]);
            begin = i + 1;
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use ab_glyph::{point, Font, FontArc};

    fn builtin() -> FontArc {
        FontArc::try_from_slice(crate::engine::core::font::DEFAULT_FONT_BYTES).expect("組み込みフォント")
    }

    /// 2 つの四角（ab_glyph と同じく閉じの長さ 0 の直線つき）は 2 つの輪郭になり、長さ 0 の辺は捨てられる。
    #[test]
    fn splits_contours_at_closing_lines() {
        let sq = |x: f32| {
            vec![
                OutlineCurve::Line(point(x, 0.0), point(x, 1.0)),
                OutlineCurve::Line(point(x, 1.0), point(x + 1.0, 1.0)),
                OutlineCurve::Line(point(x + 1.0, 1.0), point(x + 1.0, 0.0)),
                OutlineCurve::Line(point(x + 1.0, 0.0), point(x, 0.0)),
                OutlineCurve::Line(point(x, 0.0), point(x, 0.0)),
            ]
        };
        let mut curves = sq(0.0);
        curves.extend(sq(3.0));
        let shape = Shape::from_outline_curves(&curves, 1.0);
        assert_eq!(shape.contours.len(), 2);
        assert!(shape.contours.iter().all(|c| c.edges.len() == 4), "長さ 0 の閉じの線は捨てる");
        // y 上向きで時計回り（左辺を上へ → 上辺を右へ …）は外側（+1）のまま
        assert!(shape.contours.iter().all(|c| c.winding() == 1));
    }

    /// 反時計回りの外側（CFF の向き）は反転して時計回りにそろえる。
    #[test]
    fn orients_counter_clockwise_outer_contours() {
        let curves = vec![
            OutlineCurve::Line(point(0.0, 0.0), point(1.0, 0.0)),
            OutlineCurve::Line(point(1.0, 0.0), point(1.0, 1.0)),
            OutlineCurve::Line(point(1.0, 1.0), point(0.0, 1.0)),
            OutlineCurve::Line(point(0.0, 1.0), point(0.0, 0.0)),
        ];
        let shape = Shape::from_outline_curves(&curves, 1.0);
        assert_eq!(shape.contours.len(), 1);
        assert_eq!(shape.contours[0].winding(), 1, "外側は時計回りにそろう");
    }

    /// 実際の字: 「A」は外側 1・穴 1、「口」は外側と穴、どの輪郭も閉じている（終点 = 次の始点）。
    #[test]
    fn real_glyphs_form_closed_contours() {
        let font = builtin();
        for (ch, contours) in [('A', 2usize), ('口', 2), ('O', 2), ('i', 2), ('鬱', 5)] {
            let outline = font.outline(font.glyph_id(ch)).expect("輪郭がある");
            let shape = Shape::from_outline_curves(&outline.curves, 1.0);
            assert_eq!(shape.contours.len(), contours, "'{ch}' の輪郭の数");
            for c in &shape.contours {
                for k in 0..c.edges.len() {
                    let next = &c.edges[(k + 1) % c.edges.len()];
                    assert_eq!(c.edges[k].end(), next.start(), "'{ch}' の輪郭が閉じていない");
                }
            }
            // 外側の輪郭（+1）の数 ≥ 1
            assert!(shape.contours.iter().any(|c| c.winding() == 1), "'{ch}' に外側の輪郭がある");
        }
    }
}
