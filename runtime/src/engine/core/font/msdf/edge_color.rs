// ============================================================
//  font/msdf/edge_color.rs — 辺の色分け（MSDF のどのチャネルにどの辺を入れるか）
//
//  【なぜ要るか】
//  MSDF は角の両側の辺を **別のチャネル**（赤・緑・青の組み合わせ）へ入れ、描くときに 3 チャネルの中央値を取ることで、
//  1 チャネルの距離場では丸まってしまう角を拡大しても立たせる。色分けの規則は msdfgen と同じ:
//  - 角（向きが `CORNER_ANGLE_THRESHOLD` より大きく変わる所）で色を切り替える。隣り合う色は必ず 2 チャネルを共有しない
//    （シアン・マゼンタ・イエローの 2 チャネルの色を巡回する）。
//  - 角の無い滑らかな輪郭は 1 色。角が 1 つだけの輪郭（「しずく形」）は 3 色に分ける（辺が足りなければ 3 等分する）。
//  - **simple**: 角ごとに色を巡回する（msdfgen の edgeColoringSimple）。
//  - **ink trap**: 角が 4 つ以上の輪郭で、前後より短い区間の始まりの角を「小さな角」（インクトラップの切れ込み）として、
//    その短い区間に前後の色が共有しないチャネルの色を置く（msdfgen の edgeColoringInkTrap。既定）。
//
//  色はビットの組（赤 = 1・緑 = 2・青 = 4）。
// ============================================================

use super::outline::Shape;

/// 辺の色（チャネルのビットの組）。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct EdgeColor(pub u8);

impl EdgeColor {
    pub const BLACK: EdgeColor = EdgeColor(0);
    pub const RED: EdgeColor = EdgeColor(1);
    pub const GREEN: EdgeColor = EdgeColor(2);
    pub const YELLOW: EdgeColor = EdgeColor(3);
    pub const BLUE: EdgeColor = EdgeColor(4);
    pub const MAGENTA: EdgeColor = EdgeColor(5);
    pub const CYAN: EdgeColor = EdgeColor(6);
    pub const WHITE: EdgeColor = EdgeColor(7);

    /// 赤のチャネルを含むか。
    #[inline]
    pub fn has_red(self) -> bool {
        self.0 & Self::RED.0 != 0
    }
    /// 緑のチャネルを含むか。
    #[inline]
    pub fn has_green(self) -> bool {
        self.0 & Self::GREEN.0 != 0
    }
    /// 青のチャネルを含むか。
    #[inline]
    pub fn has_blue(self) -> bool {
        self.0 & Self::BLUE.0 != 0
    }
    /// チャネル番号（0 = 赤・1 = 緑・2 = 青）を含むか。
    #[inline]
    pub fn has_channel(self, channel: usize) -> bool {
        self.0 & (1 << channel) != 0
    }
}

/// 色分けの方式（プロジェクトの設定 `font.msdf_coloring` で選ぶ。既定は ink trap）。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum ColoringStrategy {
    /// 角ごとに色を巡回する（msdfgen の edgeColoringSimple）。
    Simple,
    /// 小さな角（インクトラップ）を見分けて色を置く（msdfgen の edgeColoringInkTrap）。
    #[default]
    InkTrap,
}

impl ColoringStrategy {
    /// 設定の文字列から読む（"simple" / "ink_trap"・"inktrap"。大文字小文字は区別しない。知らなければ None）。
    pub fn parse(s: &str) -> Option<Self> {
        match s.trim().to_ascii_lowercase().as_str() {
            "simple" => Some(Self::Simple),
            "ink_trap" | "inktrap" | "ink-trap" => Some(Self::InkTrap),
            _ => None,
        }
    }

    /// 設定・ログに出す名前。
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Simple => "simple",
            Self::InkTrap => "ink_trap",
        }
    }
}

/// 角とみなす向きの変化（ラジアン。msdfgen の既定 3.0 = 外積が sin(3.0) ≈ 0.141 を超える ≈ 8 度より大きい折れ）。
pub const CORNER_ANGLE_THRESHOLD: f64 = 3.0;
/// 色の巡回の種（同じ字は毎回同じ色分けになる。msdfgen の既定 0）。
pub const COLORING_SEED: u64 = 0;
/// ink trap の区間の長さの見積もりの刻み（辺 1 本を何個の折れ線で測るか。msdfgen の EDGE_LENGTH_PRECISION）。
const EDGE_LENGTH_PRECISION: usize = 4;
/// しずく形の 3 色の割り振りの係数（msdfgen の symmetricalTrichotomy）。
const TRICHOTOMY_SCALE: f64 = 2.875;
const TRICHOTOMY_OFFSET: f64 = 1.4375;

/// 形の全ての辺へ色を付ける。
pub fn color_edges(shape: &mut Shape, strategy: ColoringStrategy) {
    match strategy {
        ColoringStrategy::Simple => color_edges_simple(shape, CORNER_ANGLE_THRESHOLD, COLORING_SEED),
        ColoringStrategy::InkTrap => color_edges_ink_trap(shape, CORNER_ANGLE_THRESHOLD, COLORING_SEED),
    }
}

/// 2 本の辺のつなぎ目が角か（逆向き〈内積 ≤ 0〉か、外積が閾値を超える）。向きは正規化済みで渡す。
fn is_corner(a_dir: super::geometry::Vec2, b_dir: super::geometry::Vec2, cross_threshold: f64) -> bool {
    a_dir.dot(b_dir) <= 0.0 || a_dir.cross(b_dir).abs() > cross_threshold
}

/// 次の色へ切り替える（msdfgen の switchColor）。`banned` と共有するチャネルが 1 つだけなら、その補色にする。
fn switch_color(color: &mut EdgeColor, seed: &mut u64, banned: EdgeColor) {
    let combined = EdgeColor(color.0 & banned.0);
    if combined == EdgeColor::RED || combined == EdgeColor::GREEN || combined == EdgeColor::BLUE {
        *color = EdgeColor(combined.0 ^ EdgeColor::WHITE.0);
        return;
    }
    if *color == EdgeColor::BLACK || *color == EdgeColor::WHITE {
        const START: [EdgeColor; 3] = [EdgeColor::CYAN, EdgeColor::MAGENTA, EdgeColor::YELLOW];
        *color = START[(*seed % 3) as usize];
        *seed /= 3;
        return;
    }
    let shifted = (color.0 as u32) << (1 + (*seed & 1));
    *color = EdgeColor(((shifted | shifted >> 3) & EdgeColor::WHITE.0 as u32) as u8);
    *seed >>= 1;
}

/// 最初の色（msdfgen の initColor: 種の下位で 3 色から選ぶ）。
fn init_color(seed: &mut u64) -> EdgeColor {
    const COLORS: [EdgeColor; 3] = [EdgeColor::CYAN, EdgeColor::MAGENTA, EdgeColor::YELLOW];
    let color = COLORS[(*seed % 3) as usize];
    *seed /= 3;
    color
}

/// しずく形の辺 `position`（0..n）を 3 色のどれ（-1 / 0 / +1）に割り振るか（左右対称に 3 つへ分ける）。
fn symmetrical_trichotomy(position: usize, n: usize) -> i32 {
    (3.0 + TRICHOTOMY_SCALE * position as f64 / (n as f64 - 1.0) - TRICHOTOMY_OFFSET + 0.5) as i32 - 3
}

/// 輪郭の角の位置（辺の番号。その辺の始点が角）を集める。
fn find_corners(contour: &super::outline::Contour, cross_threshold: f64) -> Vec<usize> {
    let mut corners = Vec::new();
    let mut prev_dir = contour.edges[contour.edges.len() - 1].direction(1.0);
    for (index, edge) in contour.edges.iter().enumerate() {
        if is_corner(prev_dir.normalize(false), edge.direction(0.0).normalize(false), cross_threshold) {
            corners.push(index);
        }
        prev_dir = edge.direction(1.0);
    }
    corners
}

/// 角が 1 つだけの輪郭（しずく形）を 3 色にする。辺が 3 本未満なら 3 等分してから塗る（msdfgen と同じ手順）。
fn color_teardrop(contour: &mut super::outline::Contour, corner: usize, color: &mut EdgeColor, seed: &mut u64) {
    let mut colors = [EdgeColor::BLACK; 3];
    switch_color(color, seed, EdgeColor::BLACK);
    colors[0] = *color;
    colors[1] = EdgeColor::WHITE;
    switch_color(color, seed, EdgeColor::BLACK);
    colors[2] = *color;
    let m = contour.edges.len();
    if m >= 3 {
        for i in 0..m {
            let slot = (1 + symmetrical_trichotomy(i, m)) as usize;
            contour.edges[(corner + i) % m].color = colors[slot];
        }
    } else {
        // 辺が 1〜2 本: 各辺を 3 等分して 3 色を置く（角の位置から始まるよう並べる）
        let mut parts: Vec<super::segment::EdgeSegment> = Vec::with_capacity(6);
        let first = contour.edges[0].split_in_thirds();
        if m >= 2 {
            let second = contour.edges[1].split_in_thirds();
            // corner が 0 なら [辺0 の 3 つ, 辺1 の 3 つ]、1 なら [辺1 の 3 つ, 辺0 の 3 つ]（msdfgen の parts の並べ方）
            let ordered: [&[super::segment::EdgeSegment; 3]; 2] = if corner == 0 { [&first, &second] } else { [&second, &first] };
            parts.extend_from_slice(ordered[0]);
            parts.extend_from_slice(ordered[1]);
            parts[0].color = colors[0];
            parts[1].color = colors[0];
            parts[2].color = colors[1];
            parts[3].color = colors[1];
            parts[4].color = colors[2];
            parts[5].color = colors[2];
        } else {
            parts.extend_from_slice(&first);
            parts[0].color = colors[0];
            parts[1].color = colors[1];
            parts[2].color = colors[2];
        }
        contour.edges = parts;
    }
}

/// msdfgen の edgeColoringSimple。
pub fn color_edges_simple(shape: &mut Shape, angle_threshold: f64, seed: u64) {
    let cross_threshold = angle_threshold.sin();
    let mut seed = seed;
    let mut color = init_color(&mut seed);
    for contour in &mut shape.contours {
        if contour.edges.is_empty() {
            continue;
        }
        let corners = find_corners(contour, cross_threshold);
        if corners.is_empty() {
            // 滑らかな輪郭: 1 色
            switch_color(&mut color, &mut seed, EdgeColor::BLACK);
            for edge in &mut contour.edges {
                edge.color = color;
            }
        } else if corners.len() == 1 {
            color_teardrop(contour, corners[0], &mut color, &mut seed);
        } else {
            // 角が複数: 角ごとに色を巡回する（最後の区間は最初の色と共有しない色）
            let corner_count = corners.len();
            let mut spline = 0usize;
            let start = corners[0];
            let m = contour.edges.len();
            switch_color(&mut color, &mut seed, EdgeColor::BLACK);
            let initial_color = color;
            for i in 0..m {
                let index = (start + i) % m;
                if spline + 1 < corner_count && corners[spline + 1] == index {
                    spline += 1;
                    let banned = if spline == corner_count - 1 { initial_color } else { EdgeColor::BLACK };
                    switch_color(&mut color, &mut seed, banned);
                }
                contour.edges[index].color = color;
            }
        }
    }
}

/// 辺の長さの見積もり（折れ線 `EDGE_LENGTH_PRECISION` 本の長さの和）。
fn estimate_edge_length(edge: &super::segment::EdgeSegment) -> f64 {
    let mut len = 0.0;
    let mut prev = edge.point(0.0);
    for i in 1..=EDGE_LENGTH_PRECISION {
        let cur = edge.point(i as f64 / EDGE_LENGTH_PRECISION as f64);
        len += (cur - prev).length();
        prev = cur;
    }
    len
}

/// ink trap の色分けの角（辺の番号・その角までの区間の長さ・小さな角か・角から始まる区間の色）。
#[derive(Clone, Copy)]
struct InkTrapCorner {
    index: usize,
    prev_edge_length_estimate: f64,
    minor: bool,
    color: EdgeColor,
}

/// msdfgen の edgeColoringInkTrap。
pub fn color_edges_ink_trap(shape: &mut Shape, angle_threshold: f64, seed: u64) {
    let cross_threshold = angle_threshold.sin();
    let mut seed = seed;
    let mut color = init_color(&mut seed);
    for contour in &mut shape.contours {
        if contour.edges.is_empty() {
            continue;
        }
        // 角と、角までの区間の長さを集める
        let mut corners: Vec<InkTrapCorner> = Vec::new();
        let mut spline_length = 0.0;
        {
            let mut prev_dir = contour.edges[contour.edges.len() - 1].direction(1.0);
            for (index, edge) in contour.edges.iter().enumerate() {
                if is_corner(prev_dir.normalize(false), edge.direction(0.0).normalize(false), cross_threshold) {
                    corners.push(InkTrapCorner { index, prev_edge_length_estimate: spline_length, minor: false, color: EdgeColor::BLACK });
                    spline_length = 0.0;
                }
                spline_length += estimate_edge_length(edge);
                prev_dir = edge.direction(1.0);
            }
        }
        if corners.is_empty() {
            switch_color(&mut color, &mut seed, EdgeColor::BLACK);
            for edge in &mut contour.edges {
                edge.color = color;
            }
        } else if corners.len() == 1 {
            let corner = corners[0].index;
            color_teardrop(contour, corner, &mut color, &mut seed);
        } else {
            let corner_count = corners.len();
            let mut major_corner_count = corner_count;
            if corner_count > 3 {
                // 最後の角から最初の角までの区間（輪郭を一周した残り）を最初の角へ足す
                corners[0].prev_edge_length_estimate += spline_length;
                for i in 0..corner_count {
                    let a = corners[i].prev_edge_length_estimate;
                    let b = corners[(i + 1) % corner_count].prev_edge_length_estimate;
                    let c = corners[(i + 2) % corner_count].prev_edge_length_estimate;
                    // 角 i から角 i+1 までの区間が前後より短い → 角 i は小さな角
                    if a > b && b < c {
                        corners[i].minor = true;
                        major_corner_count -= 1;
                    }
                }
            }
            // 大きな角へ色を巡回して置く（最後の大きな角は最初の色と共有しない）
            let mut initial_color = EdgeColor::BLACK;
            for corner in corners.iter_mut() {
                if !corner.minor {
                    major_corner_count -= 1;
                    let banned = if major_corner_count == 0 { initial_color } else { EdgeColor::BLACK };
                    switch_color(&mut color, &mut seed, banned);
                    corner.color = color;
                    if initial_color == EdgeColor::BLACK {
                        initial_color = color;
                    }
                }
            }
            // 小さな角: 前の大きな角の色と次の角の色が共有しないチャネルの色
            for i in 0..corner_count {
                if corners[i].minor {
                    let next_color = corners[(i + 1) % corner_count].color;
                    corners[i].color = EdgeColor((color.0 & next_color.0) ^ EdgeColor::WHITE.0);
                } else {
                    color = corners[i].color;
                }
            }
            // 角から角までの区間を、その始まりの角の色で塗る
            let mut spline = 0usize;
            let start = corners[0].index;
            color = corners[0].color;
            let m = contour.edges.len();
            for i in 0..m {
                let index = (start + i) % m;
                if spline + 1 < corner_count && corners[spline + 1].index == index {
                    spline += 1;
                    color = corners[spline].color;
                }
                contour.edges[index].color = color;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::font::msdf::geometry::v2;
    use crate::engine::core::font::msdf::outline::Contour;
    use crate::engine::core::font::msdf::segment::EdgeSegment;

    /// 四角（4 つの角）: 隣り合う辺の色は必ず違い、共有するチャネルは 1 つ以下（角の両側が別のチャネルに入る）。
    #[test]
    fn square_corners_get_distinct_colors() {
        for strategy in [ColoringStrategy::Simple, ColoringStrategy::InkTrap] {
            let mut shape = Shape {
                contours: vec![Contour {
                    edges: vec![
                        EdgeSegment::line(v2(0.0, 0.0), v2(0.0, 10.0)),
                        EdgeSegment::line(v2(0.0, 10.0), v2(10.0, 10.0)),
                        EdgeSegment::line(v2(10.0, 10.0), v2(10.0, 0.0)),
                        EdgeSegment::line(v2(10.0, 0.0), v2(0.0, 0.0)),
                    ],
                }],
            };
            color_edges(&mut shape, strategy);
            let e = &shape.contours[0].edges;
            for k in 0..4 {
                let (a, b) = (e[k].color, e[(k + 1) % 4].color);
                assert_ne!(a, b, "{strategy:?}: 辺 {k} と次の辺が同じ色");
                assert!((a.0 & b.0).count_ones() <= 1, "{strategy:?}: 角の両側が 2 チャネル以上を共有");
                assert_eq!(a.0.count_ones(), 2, "{strategy:?}: 角のある輪郭の辺は 2 チャネルの色");
            }
        }
    }

    /// 円（角なし）は 1 色。しずく形（角 1 つ・辺 1 本）は 3 等分して 3 色（真ん中は白）。
    #[test]
    fn smooth_and_teardrop_contours() {
        // 2 本の半円（滑らかにつながる）
        let mut smooth = Shape {
            contours: vec![Contour {
                edges: vec![
                    EdgeSegment::cubic(v2(0.0, 0.0), v2(0.0, 5.5), v2(10.0, 5.5), v2(10.0, 0.0)),
                    EdgeSegment::cubic(v2(10.0, 0.0), v2(10.0, -5.5), v2(0.0, -5.5), v2(0.0, 0.0)),
                ],
            }],
        };
        color_edges(&mut smooth, ColoringStrategy::InkTrap);
        let c = &smooth.contours[0].edges;
        assert_eq!(c[0].color, c[1].color, "滑らかな輪郭は 1 色");
        assert_eq!(c[0].color.0.count_ones(), 2);

        // しずく形: 始点で尖る 1 本の 3 次（outline.rs の normalize と同じく 3 等分済みの 3 本で渡す）
        let drop = EdgeSegment::cubic(v2(0.0, 0.0), v2(10.0, 10.0), v2(10.0, -10.0), v2(0.0, 0.0));
        let mut teardrop = Shape { contours: vec![Contour { edges: drop.split_in_thirds().to_vec() }] };
        color_edges(&mut teardrop, ColoringStrategy::Simple);
        let t = &teardrop.contours[0].edges;
        assert_eq!(t.len(), 3);
        assert_eq!(t[1].color, EdgeColor::WHITE, "しずく形の真ん中は白");
        assert_ne!(t[0].color, t[2].color, "尖りの両側は別の色");
    }

    /// 設定の文字列の読み取り。
    #[test]
    fn parses_strategy_names() {
        assert_eq!(ColoringStrategy::parse("Simple"), Some(ColoringStrategy::Simple));
        assert_eq!(ColoringStrategy::parse(" ink_trap "), Some(ColoringStrategy::InkTrap));
        assert_eq!(ColoringStrategy::parse("inktrap"), Some(ColoringStrategy::InkTrap));
        assert_eq!(ColoringStrategy::parse("distance"), None);
    }
}
