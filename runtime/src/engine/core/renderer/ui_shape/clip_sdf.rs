// ============================================================
//  ui_shape/clip_sdf.rs — 角丸・楕円の切り抜きの形と写像（W2-4）
//
//  CanvasClipComponent の形（角丸の矩形・楕円）を、切り抜きの領域（4 隅）と一緒に運ぶ値（`UiClipShape`）と、
//  子の画素を SDF で切るためにシェーダーへ渡す値（`ClipSdf`）。
//
//  【どこを SDF で切るか】（W2-0 の決定。docs/app_platform_roadmap.md §3.8.4）
//  ノードの切り抜きの鎖（内側 → 外側）のうち、**いちばん内側の形のある領域 1 つだけ**を SDF で切る。
//  その領域の外接矩形と、他の祖先（形があっても）は従来どおり矩形の scissor で切る。
//
//  【写像】領域の 4 隅（(0,0)・(1,0)・(0,1)・(1,1) の順）と領域のローカルの大きさ（キャンバスの単位）から、
//  「点 → 領域のローカル座標（左上が原点・キャンバスの単位）」のアフィン写像を作る。描画はワールド座標の 4 隅
//  （renderer/ui_clip.rs の UiClipRegion）、当たり判定はキャンバス空間の 4 隅（canvas_layout/clip.rs）から同じ関数で作る。
//  回転・拡大した領域も正しく切れる（逆行列を CPU で解く）。
// ============================================================

use super::sdf::{clamp_radii, ShapeGeom};

/// 0 とみなす行列式・長さ。
const EPSILON: f32 = 1e-9;

/// 切り抜きの形の種類（描画・当たり判定の側）。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum ClipGeomKind {
    /// 形なし（矩形の scissor だけ。既定）。
    #[default]
    None,
    /// 角丸の矩形。
    RoundedRect,
    /// 楕円。
    Ellipse,
}

/// 切り抜きの領域の形（領域の 4 隅と一緒に運ぶ）。
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct UiClipShape {
    /// 種類。
    pub kind: ClipGeomKind,
    /// 領域のローカルの大きさ（キャンバスの単位。4 隅の (1,0)・(0,1) がこの大きさに当たる）。
    pub local_size: [f32; 2],
    /// 四隅の半径（左上・右上・右下・左下。キャンバスの単位。縮め済み）。
    pub radii: [f32; 4],
}

impl UiClipShape {
    /// 形なし（矩形の scissor だけ）。
    pub const NONE: UiClipShape = UiClipShape { kind: ClipGeomKind::None, local_size: [0.0; 2], radii: [0.0; 4] };

    /// 角丸の矩形（半径は縮める。すべて 0 なら形なし）。
    pub fn rounded_rect(local_size: [f32; 2], radii: [f32; 4]) -> Self {
        let radii = clamp_radii(local_size, radii);
        if radii.iter().all(|r| *r <= 0.0) {
            return Self::NONE;
        }
        Self { kind: ClipGeomKind::RoundedRect, local_size, radii }
    }

    /// 楕円。
    pub fn ellipse(local_size: [f32; 2]) -> Self {
        Self { kind: ClipGeomKind::Ellipse, local_size, radii: [0.0; 4] }
    }

    /// 形があるか（SDF で切るか）。
    pub fn has_shape(&self) -> bool {
        self.kind != ClipGeomKind::None
    }

    /// 領域のローカル座標での形（SDF の計算に使う）。
    pub fn geom(&self) -> ShapeGeom {
        match self.kind {
            ClipGeomKind::None => ShapeGeom::none(self.local_size),
            ClipGeomKind::RoundedRect => ShapeGeom::rounded_rect(self.local_size, self.radii),
            ClipGeomKind::Ellipse => ShapeGeom::ellipse(self.local_size),
        }
    }
}

/// 点 → 領域のローカル座標のアフィン写像（lx = row0 · (x, y, 1)、ly = row1 · (x, y, 1)）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ClipAffine {
    /// ローカルの x を作る行（a, b, c）。
    pub row0: [f32; 3],
    /// ローカルの y を作る行（d, e, f）。
    pub row1: [f32; 3],
}

impl ClipAffine {
    /// 領域の 4 隅とローカルの大きさから写像を作る【純関数】（退化していれば None）。
    ///
    /// # 引数
    /// * `corners`    - 4 隅（(0,0)・(1,0)・(0,1)・(1,1) の順。xy だけ使う）
    /// * `local_size` - 領域のローカルの大きさ
    pub fn from_corners(corners: &[[f32; 3]; 4], local_size: [f32; 2]) -> Option<Self> {
        let [w, h] = local_size;
        if w.abs() <= EPSILON || h.abs() <= EPSILON {
            return None;
        }
        let o = [corners[0][0], corners[0][1]];
        // ローカルの 1 単位あたりの x 軸・y 軸（点の空間）
        let ex = [(corners[1][0] - o[0]) / w, (corners[1][1] - o[1]) / w];
        let ey = [(corners[2][0] - o[0]) / h, (corners[2][1] - o[1]) / h];
        let det = ex[0] * ey[1] - ey[0] * ex[1];
        if det.abs() <= EPSILON {
            return None;
        }
        // [ex ey]⁻¹ = 1/det · [[ey.y, −ey.x], [−ex.y, ex.x]]。平行移動は −M⁻¹ o
        let inv = [[ey[1] / det, -ey[0] / det], [-ex[1] / det, ex[0] / det]];
        Some(Self {
            row0: [inv[0][0], inv[0][1], -(inv[0][0] * o[0] + inv[0][1] * o[1])],
            row1: [inv[1][0], inv[1][1], -(inv[1][0] * o[0] + inv[1][1] * o[1])],
        })
    }

    /// 点をローカル座標へ写す。
    pub fn apply(&self, p: [f32; 2]) -> [f32; 2] {
        [
            self.row0[0] * p[0] + self.row0[1] * p[1] + self.row0[2],
            self.row1[0] * p[0] + self.row1[1] * p[1] + self.row1[2],
        ]
    }
}

/// シェーダーで子の画素を切る値（いちばん内側の形のある領域）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ClipSdf {
    /// 描画のワールド座標 → 領域のローカル座標。
    pub affine: ClipAffine,
    /// 領域の形。
    pub shape: UiClipShape,
}

impl ClipSdf {
    /// 領域の 4 隅と形から作る【純関数】（形が無い・退化していれば None）。
    pub fn new(corners: &[[f32; 3]; 4], shape: &UiClipShape) -> Option<Self> {
        if !shape.has_shape() {
            return None;
        }
        ClipAffine::from_corners(corners, shape.local_size).map(|affine| Self { affine, shape: *shape })
    }

    /// 点（領域の 4 隅と同じ空間）が形の内側か（境界は内側）。
    pub fn contains(&self, p: [f32; 2]) -> bool {
        self.shape.geom().distance(self.affine.apply(p)) <= 0.0
    }
}

/// 切り抜きの鎖から、いちばん内側の形のある領域を探す【純関数】。
///
/// # 引数
/// * `len`      - 領域の表の長さ（鎖の長さの上限）
/// * `start`    - 調べ始める領域の番号（ノードの切り抜きの番号）
/// * `lookup`   - 番号 → (4 隅, 形, 親の番号)
///
/// # 戻り値
/// 見つかった領域の SDF（無ければ None）。
pub fn innermost_clip_sdf<'a>(
    len: usize,
    start: Option<u16>,
    lookup: impl Fn(u16) -> Option<(&'a [[f32; 3]; 4], &'a UiClipShape, Option<u16>)>,
) -> Option<ClipSdf> {
    let mut current = start;
    // 親は必ず子より先に積まれる（番号が小さい）ので、鎖は表の長さ以内で終わる
    for _ in 0..len {
        let id = current?;
        let (corners, shape, parent) = lookup(id)?;
        if shape.has_shape() {
            return ClipSdf::new(corners, shape);
        }
        current = parent;
    }
    None
}

// ============================================================
//  単体テスト（写像と形の内外）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 浮動小数の比較の許容量。
    const EPS: f32 = 1e-4;

    /// 左上 (x, y)・大きさ (w, h) の軸に沿った 4 隅。
    fn rect(x: f32, y: f32, w: f32, h: f32) -> [[f32; 3]; 4] {
        [[x, y, 0.0], [x + w, y, 0.0], [x, y + h, 0.0], [x + w, y + h, 0.0]]
    }

    /// 軸に沿った領域: 左上が (0,0)、右下が大きさ。画面の画素 2 倍（4 隅 200×100・ローカル 100×50）も戻せる。
    #[test]
    fn affine_maps_corners_to_local() {
        let a = ClipAffine::from_corners(&rect(10.0, 20.0, 200.0, 100.0), [100.0, 50.0]).unwrap();
        let p = a.apply([10.0, 20.0]);
        assert!(p[0].abs() < EPS && p[1].abs() < EPS);
        let q = a.apply([210.0, 120.0]);
        assert!((q[0] - 100.0).abs() < EPS && (q[1] - 50.0).abs() < EPS);
    }

    /// 回転した領域（90 度）も正しく戻せる。
    #[test]
    fn affine_handles_rotation() {
        // ローカルの x 軸が画面の +y、y 軸が画面の −x（原点 (100, 0)）
        let corners = [[100.0, 0.0, 0.0], [100.0, 40.0, 0.0], [80.0, 0.0, 0.0], [80.0, 40.0, 0.0]];
        let a = ClipAffine::from_corners(&corners, [40.0, 20.0]).unwrap();
        let p = a.apply([90.0, 30.0]);
        assert!((p[0] - 30.0).abs() < EPS && (p[1] - 10.0).abs() < EPS);
        // 退化した領域は None
        assert!(ClipAffine::from_corners(&rect(0.0, 0.0, 0.0, 10.0), [0.0, 10.0]).is_none());
    }

    /// 円の切り抜き: 外接矩形の角は外、中心は内。
    #[test]
    fn circle_clip_rejects_corners() {
        let sdf = ClipSdf::new(&rect(0.0, 0.0, 100.0, 100.0), &UiClipShape::ellipse([100.0, 100.0])).unwrap();
        assert!(sdf.contains([50.0, 50.0]));
        assert!(!sdf.contains([3.0, 3.0]), "外接矩形の角は切られる");
        assert!(sdf.contains([50.0, 1.0]), "上の縁の近くは内");
    }

    /// 角丸 0 は形なし（scissor だけ）。いちばん内側の形のある領域を探す。
    #[test]
    fn innermost_shaped_region() {
        assert!(!UiClipShape::rounded_rect([10.0, 10.0], [0.0; 4]).has_shape());
        let outer = (rect(0.0, 0.0, 100.0, 100.0), UiClipShape::ellipse([100.0, 100.0]), None);
        let middle = (rect(10.0, 10.0, 50.0, 50.0), UiClipShape::NONE, Some(0u16));
        let table = [outer, middle];
        let found = innermost_clip_sdf(table.len(), Some(1), |id| {
            table.get(id as usize).map(|(c, s, p)| (c, s, *p))
        })
        .unwrap();
        assert_eq!(found.shape.kind, ClipGeomKind::Ellipse, "形の無い内側を飛ばして外側の楕円");
        assert!(innermost_clip_sdf(table.len(), None, |_| None).is_none());
    }
}
