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
//
//  【スプラット（レイヤ重み）の表現決定 — T2】
//    密度と同じ 33³ グリッド上に「手ペイントされたレイヤ重み」を持つ。
//    保持するのは *ペイント分だけ* であり、斜度／高度ルールによる自動下地は
//    保存しない（メッシュ生成時に法線・高度から毎回計算する）。
//    理由: ルールはデータ（layers.json）で後から差し替えられるべきで、
//    グリッドへ焼き込むと差し替えが効かなくなるため。
//
//    レイヤ番号を持つ理由（T2b）: レイヤ定義は TERRAIN_MAX_LAYERS 層まで増えるが、
//    1 サンプルが同時に持てるのは TERRAIN_BLEND_SLOTS 層ぶんの重みだけ。よって
//    「どのレイヤに塗ったか」をスロットごとのレイヤ番号として併せて保持する。
//
//    メモリ試算（1 チャンク = 33³ = 35937 サンプル）:
//      density      f32 × 1                  = 143.7 KB
//      paint_index  u8  × 4（レイヤ番号）    = 143.7 KB
//      paint_weight u8  × 4（スロット重み）  = 143.7 KB
//      paint_amount u8  × 1（ペイント量）    =  35.9 KB
//      ────────────────────────────────────────────
//      合計                                   = 467.0 KB/チャンク
//    既定の地面 4×4×3 = 48 チャンクで約 22.4 MB。u8 量子化を採用したのは、
//    f32 のままだと paint だけで 575 KB/チャンクとなり density の 4 倍に
//    膨らむため（重みは 0..1 の被覆率であり 8bit で視覚的に十分）。
//    レイヤ番号も u8 で足りる（TERRAIN_MAX_LAYERS = 16 << 255）。
// ============================================================

use super::chunk_coord::ChunkCoord;
use super::layers::{dequantize_weight, quantize_weight, BlendSlots, TERRAIN_BLEND_SLOTS};
use super::settings::TerrainSettings;

/// 1 チャンク分の密度サンプル＋スプラット（手ペイント重み）を保持する構造体。
///
/// 密度の規約は settings.rs を参照（density < iso ⇒ solid、> iso ⇒ air）。
/// スプラットの規約は layers.rs を参照（重みは総和 1 に正規化・u8 量子化）。
#[derive(Clone, Debug)]
pub struct TerrainChunkData {
    /// 1 軸あたりのサンプル数（= chunk_cells + 1）。インデックス計算に使う。
    samples: usize,
    /// 密度サンプル配列（row-major, 長さ = samples³）。
    density: Vec<f32>,
    /// 各スロットが指すレイヤ番号（row-major, 長さ = samples³）。
    /// paint_amount が 0 のサンプルでは内容は無意味（未使用）。
    paint_index: Vec<[u8; TERRAIN_BLEND_SLOTS]>,
    /// 各スロットの手ペイント重み（row-major, 長さ = samples³）。u8 量子化。
    /// paint_index と添字が対応する。
    paint_weight: Vec<[u8; TERRAIN_BLEND_SLOTS]>,
    /// 各サンプルのペイント量（0 = 未ペイント＝ルール任せ、255 = 完全に手描き優先）。
    paint_amount: Vec<u8>,
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
            // スプラットは「未ペイント」で初期化する（＝全面がルール自動生成に従う）。
            paint_index: vec![[0u8; TERRAIN_BLEND_SLOTS]; total],
            paint_weight: vec![[0u8; TERRAIN_BLEND_SLOTS]; total],
            paint_amount: vec![0u8; total],
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

    // ─── スプラット（手ペイントレイヤ重み）のアクセサ ──────────────────────

    /// 指定サンプルの手ペイントスロット（レイヤ番号＋重み）を読む。
    ///
    /// 重みは u8 から復元した生値であり、再正規化はしない
    /// （未ペイントのサンプルを「全 0」のまま返すため。縮退補正を掛けると
    ///  未ペイントが「レイヤ 0 を 100% 塗った」と区別できなくなる）。
    #[inline]
    pub fn paint_slots(&self, ix: usize, iy: usize, iz: usize) -> BlendSlots {
        let i = self.index(ix, iy, iz);
        let qi = self.paint_index[i];
        let qw = self.paint_weight[i];
        let mut slots = BlendSlots { index: [0; TERRAIN_BLEND_SLOTS], weight: [0.0; TERRAIN_BLEND_SLOTS] };
        for k in 0..TERRAIN_BLEND_SLOTS {
            slots.index[k] = qi[k] as u32;
            slots.weight[k] = dequantize_weight(qw[k]);
        }
        slots
    }

    /// 指定サンプルの手ペイントスロットを書き込む（重みは 0..=1 前提・u8 へ量子化）。
    ///
    /// レイヤ番号は u8 へ切り詰められる（TERRAIN_MAX_LAYERS <= 255 なので実害なし）。
    #[inline]
    pub fn set_paint_slots(&mut self, ix: usize, iy: usize, iz: usize, slots: &BlendSlots) {
        let i = self.index(ix, iy, iz);
        for k in 0..TERRAIN_BLEND_SLOTS {
            self.paint_index[i][k] = slots.index[k].min(u8::MAX as u32) as u8;
            self.paint_weight[i][k] = quantize_weight(slots.weight[k]);
        }
    }

    /// 指定サンプルのペイント量（0=未ペイント〜1=完全に手描き優先）を読む。
    #[inline]
    pub fn paint_amount(&self, ix: usize, iy: usize, iz: usize) -> f32 {
        dequantize_weight(self.paint_amount[self.index(ix, iy, iz)])
    }

    /// 指定サンプルのペイント量を書き込む（0..=1 前提・u8 へ量子化される）。
    #[inline]
    pub fn set_paint_amount(&mut self, ix: usize, iy: usize, iz: usize, a: f32) {
        let i = self.index(ix, iy, iz);
        self.paint_amount[i] = quantize_weight(a);
    }

    /// ペイントのレイヤ番号配列全体への読み取り参照（永続化・undo スナップショットで使用）。
    pub fn raw_paint_index(&self) -> &[[u8; TERRAIN_BLEND_SLOTS]] {
        &self.paint_index
    }

    /// ペイント重み配列全体への読み取り参照（永続化・undo スナップショットで使用）。
    pub fn raw_paint_weight(&self) -> &[[u8; TERRAIN_BLEND_SLOTS]] {
        &self.paint_weight
    }

    /// ペイント量配列全体への読み取り参照（永続化・undo スナップショットで使用）。
    pub fn raw_paint_amount(&self) -> &[u8] {
        &self.paint_amount
    }

    /// ペイントのレイヤ番号配列全体を書き換える（undo/redo 復元・.tvox 読込で使用）。
    pub fn set_raw_paint_index(&mut self, p: Vec<[u8; TERRAIN_BLEND_SLOTS]>) {
        debug_assert_eq!(
            p.len(), self.paint_index.len(),
            "set_raw_paint_index length mismatch: {} != {}", p.len(), self.paint_index.len()
        );
        self.paint_index = p;
    }

    /// ペイント重み配列全体を書き換える（undo/redo 復元・.tvox 読込で使用）。
    pub fn set_raw_paint_weight(&mut self, p: Vec<[u8; TERRAIN_BLEND_SLOTS]>) {
        debug_assert_eq!(
            p.len(), self.paint_weight.len(),
            "set_raw_paint_weight length mismatch: {} != {}", p.len(), self.paint_weight.len()
        );
        self.paint_weight = p;
    }

    /// ペイント量配列全体を書き換える（undo/redo 復元・.tvox 読込で使用）。
    pub fn set_raw_paint_amount(&mut self, a: Vec<u8>) {
        debug_assert_eq!(
            a.len(), self.paint_amount.len(),
            "set_raw_paint_amount length mismatch: {} != {}", a.len(), self.paint_amount.len()
        );
        self.paint_amount = a;
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
