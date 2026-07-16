// ============================================================
// ddgi/grid.rs — DDGI プローブ格子（CPU 側の格子定義とフィット）
//
// ## 役割（単一責任）
// プローブ格子の幾何（次元・原点・間隔）と、
//   - シーン AABB からの自動フィット、
//   - プローブ番号 ⇔ 格子座標 ⇔ ワールド座標の相互変換、
//   - 八面体アトラスのタイル配置（プローブ→アトラスタイル座標）
// を提供する。GPU 側（ddgi_common.wgsl）の同名式と一致させること
// （grid_index_of_coord / probe_world_position / probe_tile などをミラー）。
//
// ## 格子とアトラスの対応（両側で一致させる規約）
//   プローブ番号   idx = px + py*dx + pz*(dx*dy)     （x 最内 → y → z）
//   アトラスタイル  tile = (px + pz*dx, py)           （Z を X 方向へ展開＝classic DDGI）
//   アトラス列数    cols = dx*dz,  行数 rows = dy
// ============================================================

use super::{GI_AABB_MARGIN, GI_IRRADIANCE_TILE, GI_VISIBILITY_TILE};

/// プローブ格子の幾何。毎フレームではなくシーンロード/モデル変化時に再フィットする。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct GiGrid {
    /// 各軸のプローブ数（x, y, z）。既定 16×8×16。
    pub dims: [u32; 3],
    /// 格子原点（プローブ (0,0,0) のワールド座標）。
    pub origin: [f32; 3],
    /// 各軸のプローブ間隔（ワールド単位）。
    pub spacing: [f32; 3],
}

impl GiGrid {
    /// 総プローブ数。
    pub fn probe_count(&self) -> u32 {
        self.dims[0] * self.dims[1] * self.dims[2]
    }

    /// アトラスのタイル列数（= dx*dz）。
    pub fn atlas_cols(&self) -> u32 {
        self.dims[0] * self.dims[2]
    }
    /// アトラスのタイル行数（= dy）。
    pub fn atlas_rows(&self) -> u32 {
        self.dims[1]
    }

    /// 放射輝度アトラスのピクセル寸法（cols*10, rows*10）。
    pub fn irradiance_atlas_size(&self) -> (u32, u32) {
        (self.atlas_cols() * GI_IRRADIANCE_TILE, self.atlas_rows() * GI_IRRADIANCE_TILE)
    }
    /// 可視性アトラスのピクセル寸法（cols*18, rows*18）。
    pub fn visibility_atlas_size(&self) -> (u32, u32) {
        (self.atlas_cols() * GI_VISIBILITY_TILE, self.atlas_rows() * GI_VISIBILITY_TILE)
    }

    /// プローブ番号 → 格子座標 (px, py, pz)。
    pub fn coord_of_index(&self, index: u32) -> [u32; 3] {
        let dx = self.dims[0];
        let dy = self.dims[1];
        let px = index % dx;
        let py = (index / dx) % dy;
        let pz = index / (dx * dy);
        [px, py, pz]
    }

    /// 格子座標 (px, py, pz) → プローブ番号。
    pub fn index_of_coord(&self, c: [u32; 3]) -> u32 {
        c[0] + c[1] * self.dims[0] + c[2] * self.dims[0] * self.dims[1]
    }

    /// 格子座標のプローブのワールド座標。
    pub fn probe_world_position(&self, c: [u32; 3]) -> [f32; 3] {
        [
            self.origin[0] + c[0] as f32 * self.spacing[0],
            self.origin[1] + c[1] as f32 * self.spacing[1],
            self.origin[2] + c[2] as f32 * self.spacing[2],
        ]
    }

    /// シーンの静的メッシュ AABB からプローブ格子を自動フィットする。
    ///
    /// AABB を GI_AABB_MARGIN ぶん各辺へ広げ（隅のプローブが壁に張り付かないように）、
    /// 広げた範囲の両端にプローブ (0..dims-1) が来るよう間隔を決める。
    /// 空/退化した AABB（size≈0）でも間隔が 0 にならないようフォールバックする。
    ///
    /// - `aabb_min` / `aabb_max`: シーン静的メッシュのワールド AABB。
    /// - `dims`: 各軸プローブ数（各成分 >= 1）。
    pub fn fit_from_aabb(aabb_min: [f32; 3], aabb_max: [f32; 3], dims: [u32; 3]) -> Self {
        let mut origin = [0.0f32; 3];
        let mut spacing = [1.0f32; 3];
        let clamped_dims = [dims[0].max(1), dims[1].max(1), dims[2].max(1)];
        for a in 0..3 {
            let lo = aabb_min[a];
            let hi = aabb_max[a].max(aabb_min[a]);
            let size = hi - lo;
            // 各辺へ margin*size/2 ずつ広げる（総拡張率 = 1 + margin）。
            let ext = 0.5 * GI_AABB_MARGIN * size;
            let emin = lo - ext;
            let emax = hi + ext;
            let esize = emax - emin;
            let n = clamped_dims[a];
            if n > 1 {
                // 退化（esize≈0）時は間隔 1.0 にフォールバックし、原点を中心に置く。
                if esize > 1e-4 {
                    spacing[a] = esize / (n - 1) as f32;
                    origin[a] = emin;
                } else {
                    spacing[a] = 1.0;
                    let center = 0.5 * (emin + emax);
                    origin[a] = center - 0.5 * (n - 1) as f32;
                }
            } else {
                // 1 枚だけの軸は中央に 1 プローブを置く。
                spacing[a] = esize.max(1.0);
                origin[a] = 0.5 * (emin + emax);
            }
        }
        Self { dims: clamped_dims, origin, spacing }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn default_grid() -> GiGrid {
        GiGrid::fit_from_aabb([-10.0, 0.0, -5.0], [10.0, 8.0, 5.0], [16, 8, 16])
    }

    /// プローブ番号 ⇔ 格子座標の往復が全プローブで一致すること。
    #[test]
    fn index_coord_roundtrip() {
        let g = default_grid();
        for idx in 0..g.probe_count() {
            let c = g.coord_of_index(idx);
            assert!(c[0] < g.dims[0] && c[1] < g.dims[1] && c[2] < g.dims[2], "座標が範囲外: {c:?}");
            assert_eq!(g.index_of_coord(c), idx, "index→coord→index が不一致: idx={idx} c={c:?}");
        }
    }

    /// 隅のプローブがフィット範囲の両端（margin 拡張後）に一致すること。
    #[test]
    fn corner_probes_span_expanded_aabb() {
        let g = default_grid();
        let p0 = g.probe_world_position([0, 0, 0]);
        // 原点は拡張後 min（= min - 5%/2*size）に一致。
        // size_x=20 → ext=0.5*0.05*20=0.5 → emin_x=-10.5
        assert!((p0[0] - (-10.5)).abs() < 1e-3, "隅(0,0,0).x が拡張後 min と不一致: {}", p0[0]);
        let plast = g.probe_world_position([g.dims[0] - 1, g.dims[1] - 1, g.dims[2] - 1]);
        assert!((plast[0] - 10.5).abs() < 1e-3, "反対隅.x が拡張後 max と不一致: {}", plast[0]);
    }

    /// アトラス寸法がタイル配置と整合すること。
    #[test]
    fn atlas_sizes_match_layout() {
        let g = default_grid();
        assert_eq!(g.atlas_cols(), 16 * 16);
        assert_eq!(g.atlas_rows(), 8);
        assert_eq!(g.irradiance_atlas_size(), (256 * 10, 8 * 10));
        assert_eq!(g.visibility_atlas_size(), (256 * 18, 8 * 18));
    }

    /// 退化 AABB（size 0）でも間隔が 0 にならないこと（0 除算・NaN の防止）。
    #[test]
    fn degenerate_aabb_has_nonzero_spacing() {
        let g = GiGrid::fit_from_aabb([1.0, 1.0, 1.0], [1.0, 1.0, 1.0], [4, 4, 4]);
        for a in 0..3 {
            assert!(g.spacing[a].is_finite() && g.spacing[a] > 0.0, "spacing[{a}]={} が不正", g.spacing[a]);
        }
    }
}
