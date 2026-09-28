// ============================================================
//  primitive2d/offscreen_cull.rs — 画面の外にある SEED.Draw の図形を積まない判定（純関数）
//
//  【なぜ要るか（2026-09-28。docs/app_platform_roadmap.md §3.9 の原因 2）】
//  Primitive2dRenderer::push は図形の全頂点を CPU で NDC へ射影してから積む。縦に長いページ（グラフの見本は 1200 dp、
//  Pixel 6a の画面は 914 dp）では、スクロールで画面の外にある図形の頂点も毎フレーム射影して GPU へ送り、ラスタライザが捨てていた。
//
//  【見た目が変わらない理由】
//  図形の頂点はすべて外接矩形（描画空間）の中にある。2D キャンバスの行列とカメラはアフィン（w が一定）なので、外接矩形の 4 隅を
//  射影した NDC の外接矩形は全頂点の射影を含む（w > 0 の射影変換でも凸な矩形の像は 4 隅の凸包に収まる）。その NDC の外接矩形が
//  画面（NDC の [-1, 1]²）の外なら、どの三角形も画面の画素を 1 つも覆わない（ラスタライザがビューポートの外を捨てる）。
//  丸めの差（頂点ごとの射影と 4 隅の射影の誤差は 1e-6 程度）に対しては `NDC_CULL_MARGIN` の余白を取る。
//  切り抜き（scissor）の矩形では判定しない（scissor を張らないパスもあるので、画面の外だけを捨てる）。
//  3D ワールドキャンバスの図形（深度テストあり）は判定しない（呼び出し側）。
// ============================================================

use super::pass::project;
use super::tessellate::Mesh2d;

/// 画面の外と判定するときの NDC の余白（画面の 1 画素よりずっと小さく、射影の丸めの差よりずっと大きい値）。
const NDC_CULL_MARGIN: f32 = 1.0e-3;

/// NDC の画面の端（[-1, 1]）。
const NDC_EDGE: f32 = 1.0;

/// メッシュの頂点の外接矩形（描画空間の [min_x, min_y, max_x, max_y]。頂点が無ければ None）。
pub fn mesh_bounds(mesh: &Mesh2d) -> Option<[f32; 4]> {
    let first = mesh.verts.first()?;
    let mut b = [first.pos[0], first.pos[1], first.pos[0], first.pos[1]];
    for v in &mesh.verts {
        b[0] = b[0].min(v.pos[0]);
        b[1] = b[1].min(v.pos[1]);
        b[2] = b[2].max(v.pos[0]);
        b[3] = b[3].max(v.pos[1]);
    }
    Some(b)
}

/// 外接矩形を射影した NDC の範囲が画面（NDC の [-1, 1]²）の外か。
///
/// 4 隅のどれかが射影できない（カメラの背後）なら判定しない（false ＝ 従来どおり頂点ごとに射影する）。
pub fn bounds_outside_viewport(bounds: [f32; 4], model: &[[f32; 4]; 4], view_proj: &[[f32; 4]; 4]) -> bool {
    let corners = [
        [bounds[0], bounds[1]],
        [bounds[2], bounds[1]],
        [bounds[2], bounds[3]],
        [bounds[0], bounds[3]],
    ];
    let mut ndc_min = [f32::INFINITY; 2];
    let mut ndc_max = [f32::NEG_INFINITY; 2];
    for c in corners {
        let Some(p) = project(c[0], c[1], model, view_proj) else {
            return false;
        };
        for axis in 0..2 {
            ndc_min[axis] = ndc_min[axis].min(p[axis]);
            ndc_max[axis] = ndc_max[axis].max(p[axis]);
        }
    }
    let limit = NDC_EDGE + NDC_CULL_MARGIN;
    (0..2).any(|axis| ndc_max[axis] < -limit || ndc_min[axis] > limit)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::renderer::primitive2d::tessellate::Vert2d;

    const IDENTITY: [[f32; 4]; 4] = [
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ];

    /// 列優先の平行移動の行列（model[3] が平行移動）。
    fn translate(x: f32, y: f32) -> [[f32; 4]; 4] {
        let mut m = IDENTITY;
        m[3][0] = x;
        m[3][1] = y;
        m
    }

    fn mesh(points: &[[f32; 2]]) -> Mesh2d {
        Mesh2d { verts: points.iter().map(|&pos| Vert2d { pos, alpha: 1.0 }).collect(), idx: Vec::new() }
    }

    /// 外接矩形は全頂点の最小・最大。頂点が無ければ None。
    #[test]
    fn bounds_cover_all_vertices() {
        assert_eq!(mesh_bounds(&mesh(&[[0.5, -0.2], [-0.3, 0.4], [0.1, 0.9]])), Some([-0.3, -0.2, 0.5, 0.9]));
        assert_eq!(mesh_bounds(&mesh(&[])), None);
    }

    /// 画面の中・端をまたぐ・余白の内側は捨てない。完全に外（上下左右）は捨てる。
    #[test]
    fn only_fully_outside_bounds_are_culled() {
        let b = [-0.1, -0.1, 0.1, 0.1];
        assert!(!bounds_outside_viewport(b, &IDENTITY, &IDENTITY), "画面の中");
        assert!(!bounds_outside_viewport(b, &translate(1.05, 0.0), &IDENTITY), "右の端をまたぐ");
        assert!(bounds_outside_viewport(b, &translate(1.2, 0.0), &IDENTITY), "右の外");
        assert!(bounds_outside_viewport(b, &translate(-1.2, 0.0), &IDENTITY), "左の外");
        assert!(bounds_outside_viewport(b, &translate(0.0, 1.2), &IDENTITY), "上の外");
        assert!(bounds_outside_viewport(b, &translate(0.0, -1.2), &IDENTITY), "下の外");
        // 端にちょうど接する（丸めで 1 画素に掛かりうる）ものは余白の内側なので捨てない
        assert!(!bounds_outside_viewport(b, &translate(1.1, 0.0), &IDENTITY), "端に接する");
    }

    /// カメラの背後（w <= 0）の隅があれば判定しない。
    #[test]
    fn behind_camera_is_not_culled() {
        let mut vp = IDENTITY;
        vp[3][3] = -1.0;
        assert!(!bounds_outside_viewport([5.0, 5.0, 6.0, 6.0], &IDENTITY, &vp));
    }
}
