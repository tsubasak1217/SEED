// ============================================================
//  ui_shape/shape_hit.rs — 形の当たり判定（W2-4）
//
//  見た目の形（角丸・楕円・弧）の外は押せない（円のボタンの角は当たらない）。ジェスチャー（hit_slop.rs）の
//  最小のヒット領域（48 dp）まで広げるときは、形も同じだけ広げる:
//    - 角丸の矩形 … 大きさを片側 e（縦横それぞれ）広げ、半径に min(ex, ey) を足す（角の丸みを保ったまま外へ）
//    - 楕円       … 広げた矩形に内接する楕円
//    - 弧         … 中心線から (太さの半分 + min(ex, ey)) の帯（角度の範囲はそのまま）
//    - 形なし     … 広げた矩形（従来どおり）
//  広げる量が 0 なら見た目の形そのもの（境界は内側）。点・形は「形の空間」（キャンバスの単位・左上が原点）。
// ============================================================

use super::sdf::{ShapeGeom, ShapeGeomKind};

/// 点が形の中か（境界は内側）【純関数】。
///
/// # 引数
/// * `geom` - 形
/// * `p`    - 形の空間の点（矩形の左上が原点）
pub fn contains(geom: &ShapeGeom, p: [f32; 2]) -> bool {
    geom.distance(p) <= 0.0
}

/// 点が、縦横それぞれ `expand` だけ広げた形の中か【純関数】。
///
/// # 引数
/// * `geom`   - 形
/// * `p`      - 形の空間の点（元の矩形の左上が原点）
/// * `expand` - 片側の広げる量（形の空間。縦横それぞれ。負は 0）
pub fn contains_expanded(geom: &ShapeGeom, p: [f32; 2], expand: [f32; 2]) -> bool {
    let e = [expand[0].max(0.0), expand[1].max(0.0)];
    if e == [0.0, 0.0] {
        return contains(geom, p);
    }
    let grow = e[0].min(e[1]);
    let size = [geom.size[0] + e[0] * 2.0, geom.size[1] + e[1] * 2.0];
    // 広げた矩形の左上が原点になるよう点をずらす
    let q = [p[0] + e[0], p[1] + e[1]];
    let grown = match geom.kind {
        ShapeGeomKind::None => ShapeGeom::none(size),
        ShapeGeomKind::RoundedRect => ShapeGeom::rounded_rect(size, geom.radii.map(|r| r + grow)),
        ShapeGeomKind::Ellipse => ShapeGeom::ellipse(size),
        ShapeGeomKind::Arc => {
            // 弧は中心・中心線の半径を保ったまま帯を太らせる（元の矩形の空間で測る）
            let mut arc = geom.arc;
            arc.half_thickness += grow;
            let wider = ShapeGeom { arc, ..*geom };
            return wider.distance(p) <= 0.0;
        }
    };
    contains(&grown, q)
}

// ============================================================
//  単体テスト（形の当たり判定）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 円のボタンの角は押せない（外接矩形の中でも円の外は外れる）。
    #[test]
    fn circle_corner_is_not_hit() {
        let g = ShapeGeom::ellipse([60.0, 60.0]);
        assert!(contains(&g, [30.0, 30.0]));
        assert!(contains(&g, [30.0, 0.5]), "上の縁の内側");
        assert!(!contains(&g, [4.0, 4.0]), "外接矩形の左上の角");
        assert!(!contains(&g, [58.0, 58.0]), "右下の角");
    }

    /// 最小のヒット領域: 40 の円を 48 まで（片側 4）広げると、広げた円の縁まで当たるが角は外れる。
    #[test]
    fn expanded_circle_keeps_round_hit_area() {
        let g = ShapeGeom::ellipse([40.0, 40.0]);
        let e = [4.0, 4.0];
        assert!(contains_expanded(&g, [20.0, -3.5], e), "上へ 3.5 はみ出した点（広げた円の中）");
        assert!(!contains_expanded(&g, [20.0, -4.5], e), "広げた円の外");
        assert!(!contains_expanded(&g, [-3.0, -3.0], e), "広げた矩形の角は外れる");
        assert!(contains_expanded(&g, [-3.0, 20.0], e), "左へ 3 はみ出した点");
    }

    /// 角丸の矩形の広げ方: 半径が広げた分だけ大きくなり、辺はまっすぐ外へ。
    #[test]
    fn expanded_rounded_rect() {
        let g = ShapeGeom::rounded_rect([100.0, 40.0], [10.0; 4]);
        let e = [2.0, 4.0];
        // 辺の中央は広げた量まで当たる
        assert!(contains_expanded(&g, [50.0, -3.9], e));
        assert!(!contains_expanded(&g, [50.0, -4.1], e));
        assert!(contains_expanded(&g, [-1.9, 20.0], e));
        // 角: 広げた矩形の頂点は外（半径 12 の丸み）
        assert!(!contains_expanded(&g, [-2.0, -4.0], e));
        // 広げる量 0 は元の形
        assert!(!contains_expanded(&g, [0.5, 0.5], [0.0, 0.0]));
        assert!(contains_expanded(&g, [10.0, 10.0], [0.0, 0.0]));
    }

    /// 形なしは矩形（従来どおり）。
    #[test]
    fn none_is_rect() {
        let g = ShapeGeom::none([10.0, 10.0]);
        assert!(contains(&g, [0.0, 0.0]) && contains(&g, [10.0, 10.0]));
        assert!(contains_expanded(&g, [-1.0, -1.0], [1.0, 1.0]));
        assert!(!contains_expanded(&g, [-1.5, 5.0], [1.0, 1.0]));
    }
}
