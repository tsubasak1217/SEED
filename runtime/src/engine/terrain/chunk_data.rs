// ============================================================
//  terrain/chunk_data.rs — チャンクの密度グリッド
//
//  【責務】
//    1 チャンク分の密度サンプル配列を保持する。
//    サンプルの読み書きと格子初期化のみを担当し、
//    メッシュ化やブラシ編集ロジックは持たない（単一責任）。
//
//  【表現の決定】
//    density: Vec<f32>（長さ = samples_per_axis³ = 33³ = 35937）。
//    row-major で index = x + y*S + z*S*S（S = samples_per_axis）。
//    f32 を選んだ理由は、編集時の精度と勾配（法線）品質のため。
//    → i8 量子化はメモリ最適化(T2)であり、ここでは採用しない。
//      f32 では 35937 * 4 = 約 143 KB/チャンク。
// ============================================================

use super::chunk_coord::ChunkCoord;
use super::settings::TerrainSettings;

/// 1 チャンク分の密度サンプルを保持する構造体。
///
/// 密度の規約は settings.rs を参照（density < iso ⇒ solid、> iso ⇒ air）。
#[derive(Clone, Debug)]
pub struct TerrainChunkData {
    /// 1 軸あたりのサンプル数（= chunk_cells + 1）。インデックス計算に使う。
    samples: usize,
    /// 密度サンプル配列（row-major, 長さ = samples³）。
    density: Vec<f32>,
}

impl TerrainChunkData {
    /// すべてのサンプルを `value` で埋めたチャンクを生成する。
    pub fn new_filled(settings: &TerrainSettings, value: f32) -> Self {
        // ─── 1 軸のサンプル数から総サンプル数を求めて確保する ───
        let samples = settings.samples_per_axis();
        let total = samples * samples * samples;
        Self {
            samples,
            density: vec![value; total],
        }
    }

    /// 1 軸あたりのサンプル数を返す。
    pub fn samples_per_axis(&self) -> usize {
        self.samples
    }

    /// (ix, iy, iz) から配列インデックスを計算する（row-major）。
    #[inline]
    fn index(&self, ix: usize, iy: usize, iz: usize) -> usize {
        // ─── x + y*S + z*S*S ───
        ix + iy * self.samples + iz * self.samples * self.samples
    }

    /// 指定サンプルの密度を読む。範囲外は debug ビルドで assert する。
    #[inline]
    pub fn sample(&self, ix: usize, iy: usize, iz: usize) -> f32 {
        debug_assert!(
            ix < self.samples && iy < self.samples && iz < self.samples,
            "sample index out of bounds: ({ix},{iy},{iz}) >= {}",
            self.samples
        );
        self.density[self.index(ix, iy, iz)]
    }

    /// 指定サンプルへ密度を書き込む。範囲外は debug ビルドで assert する。
    #[inline]
    pub fn set_sample(&mut self, ix: usize, iy: usize, iz: usize, v: f32) {
        debug_assert!(
            ix < self.samples && iy < self.samples && iz < self.samples,
            "set_sample index out of bounds: ({ix},{iy},{iz}) >= {}",
            self.samples
        );
        let i = self.index(ix, iy, iz);
        self.density[i] = v;
    }

    /// 密度配列全体への読み取り参照（永続化などで使用）。
    pub fn raw_density(&self) -> &[f32] {
        &self.density
    }

    /// 密度配列全体を書き換える（undo/redo でのスナップショット復元に使用）。
    ///
    /// 長さが一致しない場合は debug ビルドで assert する（raw_density() と対で
    /// 使うことを前提にしており、通常は長さが変わることはない）。
    pub fn set_raw_density(&mut self, d: Vec<f32>) {
        debug_assert_eq!(
            d.len(), self.density.len(),
            "set_raw_density length mismatch: {} != {}", d.len(), self.density.len()
        );
        self.density = d;
    }

    /// 密度関数 `density_fn(world_x, world_y, world_z) -> density` を全サンプルへ適用して
    /// チャンクを初期化する（ハイトマップ読込など、地面以外の初期地形を敷くための汎用版）。
    ///
    /// from_ground_plane（density = world_y 固定）の一般化版。任意の SDF/高さ場から
    /// チャンクを組み立てられる。
    pub fn from_fn<F: Fn(f32, f32, f32) -> f32>(
        settings: &TerrainSettings,
        coord: ChunkCoord,
        density_fn: F,
    ) -> Self {
        let mut data = Self::new_filled(settings, 0.0);
        let origin = coord.world_origin(settings);
        let voxel = settings.voxel_size;
        let s = data.samples;

        for iz in 0..s {
            let world_z = origin[2] + iz as f32 * voxel;
            for iy in 0..s {
                let world_y = origin[1] + iy as f32 * voxel;
                for ix in 0..s {
                    let world_x = origin[0] + ix as f32 * voxel;
                    data.set_sample(ix, iy, iz, density_fn(world_x, world_y, world_z));
                }
            }
        }
        data
    }

    /// 平坦な地面（y=0 平面が表面）としてチャンクを初期化する。
    ///
    /// 各サンプルのワールド座標を求め、density = ワールド Y 座標 とする。
    /// → y<0 が solid（地中）、y>0 が air（空中）、y=0 が表面となる。
    pub fn from_ground_plane(settings: &TerrainSettings, coord: ChunkCoord) -> Self {
        // ─── まず 0 埋めで確保する ───
        let mut data = Self::new_filled(settings, 0.0);
        let origin = coord.world_origin(settings);
        let voxel = settings.voxel_size;
        let s = data.samples;

        // ─── 各サンプルのワールド Y を密度として書き込む ───
        for iz in 0..s {
            for iy in 0..s {
                // ワールド Y = チャンク原点Y + サンプルインデックス * voxel_size
                let world_y = origin[1] + iy as f32 * voxel;
                for ix in 0..s {
                    data.set_sample(ix, iy, iz, world_y);
                }
            }
        }
        data
    }
}
