// ============================================================
//  canvas_layout/safe_area.rs — 安全領域の部品の縮め方（W2-1b。純関数）
//
//  CanvasSafeAreaComponent を持つノードの「子が並ぶ箱」（ノードのローカル座標の [0, 箱の大きさ]）を、
//  安全領域（2D キャンバスのワールド座標）の内側へ縮める。辺ごとに適用の有無を選べる。
//    1. 安全領域の 4 隅をノードのローカル座標へ戻す（ワールド行列の逆。行列は回転と平行移動だけなので転置で戻る）
//    2. その外接矩形と箱の交差を、適用する辺についてだけ取る（適用しない辺は箱の辺のまま）
//    3. 縮めた矩形（ローカル座標の左上と大きさ）を返す。走査（pass.rs）がこれで箱の原点と大きさを置き換える
//  回転したノード（祖先を含む）では外接矩形になる（正確には切れない。切り抜きと同じ扱い）。
//  安全領域と箱が重ならないときは大きさ 0 の矩形になる（負の大きさにはしない）。
// ============================================================

/// 軸に沿った矩形（左上 min・右下 max。Y 下向き）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CanvasRect {
    /// 左上の角。
    pub min: [f32; 2],
    /// 右下の角。
    pub max: [f32; 2],
}

impl CanvasRect {
    /// 原点と大きさから作る。
    pub fn from_origin_size(origin: [f32; 2], size: [f32; 2]) -> Self {
        Self { min: origin, max: [origin[0] + size[0], origin[1] + size[1]] }
    }

    /// 大きさ（負にはしない）。
    pub fn size(&self) -> [f32; 2] {
        [(self.max[0] - self.min[0]).max(0.0), (self.max[1] - self.min[1]).max(0.0)]
    }
}

/// 安全領域の辺の並び（左・上・右・下）の添字。
const EDGE_LEFT: usize = 0;
/// 上の辺の添字。
const EDGE_TOP: usize = 1;
/// 右の辺の添字。
const EDGE_RIGHT: usize = 2;
/// 下の辺の添字。
const EDGE_BOTTOM: usize = 3;

/// ワールド座標の矩形を、ノードのワールド行列（回転と平行移動だけの行優先の行列）の逆でローカル座標へ戻し、
/// 外接矩形を返す【純関数】。
///
/// # 引数
/// * `world_rs` - ノードのワールド行列（ローカル → ワールド。スケールを含まない）
/// * `rect`     - ワールド座標の矩形
pub fn world_rect_to_local(world_rs: &[[f32; 4]; 4], rect: CanvasRect) -> CanvasRect {
    let corners = [
        [rect.min[0], rect.min[1]],
        [rect.max[0], rect.min[1]],
        [rect.min[0], rect.max[1]],
        [rect.max[0], rect.max[1]],
    ];
    let mut out = CanvasRect { min: [f32::INFINITY; 2], max: [f32::NEG_INFINITY; 2] };
    for [x, y] in corners {
        // ローカル = R^T × (ワールド − 平行移動)（R は直交行列なので逆は転置）
        let dx = x - world_rs[0][3];
        let dy = y - world_rs[1][3];
        let lx = world_rs[0][0] * dx + world_rs[1][0] * dy;
        let ly = world_rs[0][1] * dx + world_rs[1][1] * dy;
        out.min = [out.min[0].min(lx), out.min[1].min(ly)];
        out.max = [out.max[0].max(lx), out.max[1].max(ly)];
    }
    out
}

/// 箱（ローカル座標の [0, box_size]）を、安全領域（ローカル座標）の内側へ辺ごとに縮める【純関数】。
///
/// # 引数
/// * `box_size`   - 箱の大きさ（ローカルの画素）
/// * `safe_local` - 安全領域（ノードのローカル座標）
/// * `edges`      - 適用する辺（左・上・右・下）
///
/// # 戻り値
/// 縮めた矩形（ローカル座標）。適用しない辺は箱の辺のまま。重ならなければ大きさ 0。
pub fn inset_box(box_size: [f32; 2], safe_local: CanvasRect, edges: [bool; 4]) -> CanvasRect {
    let left = if edges[EDGE_LEFT] { safe_local.min[0].max(0.0) } else { 0.0 };
    let top = if edges[EDGE_TOP] { safe_local.min[1].max(0.0) } else { 0.0 };
    let right = if edges[EDGE_RIGHT] { safe_local.max[0].min(box_size[0]) } else { box_size[0] };
    let bottom = if edges[EDGE_BOTTOM] { safe_local.max[1].min(box_size[1]) } else { box_size[1] };
    // 重ならない（安全領域が箱の外）ときは、左上を箱の中へ収めて大きさ 0 にする
    let left = left.min(box_size[0].max(0.0));
    let top = top.min(box_size[1].max(0.0));
    CanvasRect { min: [left, top], max: [right.max(left), bottom.max(top)] }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::CanvasTransform;

    /// 縦画面（1080×2400）の箱を、上 136・下 63 の安全領域へ縮める（Pixel 6 の縦持ち）。
    #[test]
    fn portrait_insets_shrink_top_and_bottom() {
        let safe = CanvasRect { min: [0.0, 136.0], max: [1080.0, 2337.0] };
        let r = inset_box([1080.0, 2400.0], safe, [true; 4]);
        assert_eq!(r, CanvasRect { min: [0.0, 136.0], max: [1080.0, 2337.0] });
        assert_eq!(r.size(), [1080.0, 2201.0]);
    }

    /// 横画面に回すと切り欠きは左へ移る（辺ごとの適用・下を外すと下は箱のまま）。
    #[test]
    fn landscape_and_per_edge_flags() {
        let safe = CanvasRect { min: [136.0, 0.0], max: [2337.0, 1017.0] };
        let r = inset_box([2400.0, 1080.0], safe, [true, true, true, false]);
        assert_eq!(r, CanvasRect { min: [136.0, 0.0], max: [2337.0, 1080.0] });
        let none = inset_box([2400.0, 1080.0], safe, [false; 4]);
        assert_eq!(none, CanvasRect { min: [0.0, 0.0], max: [2400.0, 1080.0] });
    }

    /// 安全領域が箱の外なら大きさ 0（負の大きさにしない）。
    #[test]
    fn disjoint_safe_area_collapses_to_zero() {
        let safe = CanvasRect { min: [500.0, 500.0], max: [600.0, 600.0] };
        let r = inset_box([100.0, 100.0], safe, [true; 4]);
        assert_eq!(r.size(), [0.0, 0.0]);
        assert!(r.min[0] <= 100.0 && r.min[1] <= 100.0);
    }

    /// ワールド → ローカル: 平行移動だけの行列は引き算、90 度の回転は軸が入れ替わる。
    #[test]
    fn world_rect_to_local_inverts_translation_and_rotation() {
        let m = CanvasTransform { position: [-540.0, -1200.0], ..CanvasTransform::default() }.to_mat4_sized(1.0, 1.0);
        let r = world_rect_to_local(&m, CanvasRect { min: [-540.0, -1064.0], max: [540.0, 1137.0] });
        assert_eq!(r, CanvasRect { min: [0.0, 136.0], max: [1080.0, 2337.0] });
        let rot = CanvasTransform { rotation: 90.0, ..CanvasTransform::default() }.to_mat4_sized(1.0, 1.0);
        let r = world_rect_to_local(&rot, CanvasRect { min: [0.0, 0.0], max: [10.0, 20.0] });
        assert!((r.min[0] - 0.0).abs() < 1e-4 && (r.max[0] - 20.0).abs() < 1e-4, "{r:?}");
        assert!((r.min[1] + 10.0).abs() < 1e-4 && (r.max[1] - 0.0).abs() < 1e-4, "{r:?}");
    }
}
