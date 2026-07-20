// ============================================================
//  terrain/settings.rs — 地形ボクセルの調整用定数（データドリブン）
//
//  【責務】
//    ボクセル地形システムのすべての調整可能な定数を一箇所に集約する。
//    エンジン全体でマジックナンバーを禁止しているため、値はすべて
//    この構造体（またはファイル内の名前付き const）に定義する。
//
//  【密度の規約（重要）】
//    density <  iso_level ⇒ SOLID（内部・地面の中）
//    density >  iso_level ⇒ AIR  （外部・空中）
//    density == iso_level ⇒ 表面（マーチングキューブスの等値面）
//
//    平坦な初期地面では density(p) = p.y と定義する。
//    → y=0 平面が表面、y<0 が地中（solid）、y>0 が空中（air）となる。
// ============================================================

use serde::{Deserialize, Serialize};

// ─── デフォルト値（名前付き const・マジックナンバー禁止） ────────────────────

/// 1 ボクセルセル辺の既定サイズ（メートル）
const DEFAULT_VOXEL_SIZE: f32 = 0.5;
/// チャンク 1 軸あたりの既定セル数
const DEFAULT_CHUNK_CELLS: u32 = 32;
/// マーチングキューブスの既定等値面しきい値
const DEFAULT_ISO_LEVEL: f32 = 0.0;
/// 初期地面の X 軸方向チャンク数
const DEFAULT_GROUND_CHUNKS_X: u32 = 4;
/// 初期地面の Z 軸方向チャンク数
const DEFAULT_GROUND_CHUNKS_Z: u32 = 4;
/// 縦方向チャンク範囲の下限（掘り下げ用の余白）
const DEFAULT_GROUND_CHUNK_Y_MIN: i32 = -1;
/// 縦方向チャンク範囲の上限（盛り上げ用の余白）
const DEFAULT_GROUND_CHUNK_Y_MAX: i32 = 1;

// ─── 設定可能範囲（エディタからの入力を丸める上下限） ───────────────────────
//
//  【なぜ上限が要るか】
//    chunk_cells はサンプル数が (cells+1)³ で効くため、メモリが 3 乗で増える。
//    cells=64 で 65³ = 274,625 サンプル ⇒ 1 チャンクあたり
//      density f32×1 + paint_index u8×4 + paint_weight u8×4 + paint_amount u8×1
//      = 274,625 × 13 バイト ≒ 3.6 MB。
//    cells=128 では 129³ ≒ 2.15M サンプル ⇒ 約 28 MB/チャンク となり、
//    数十チャンクで GB 級に達して実用にならない。よって 64 を上限とする。

/// チャンク 1 軸あたりのセル数の下限（これ未満だとチャンクが粗すぎて編集できない）。
pub const MIN_CHUNK_CELLS: u32 = 4;
/// チャンク 1 軸あたりのセル数の上限（メモリ 3 乗増加の抑制。上のコメント参照）。
pub const MAX_CHUNK_CELLS: u32 = 64;
/// 初期地面の 1 軸あたりチャンク数の下限。
pub const MIN_GROUND_CHUNKS: u32 = 1;
/// 初期地面の 1 軸あたりチャンク数の上限。
pub const MAX_GROUND_CHUNKS: u32 = 32;
/// ボクセル 1 辺サイズの下限（メートル）。
pub const MIN_VOXEL_SIZE: f32 = 0.05;
/// ボクセル 1 辺サイズの上限（メートル）。
pub const MAX_VOXEL_SIZE: f32 = 8.0;
/// 地形が同時に保持できるチャンク総数の上限（チャンク追加の暴走を止める安全弁）。
pub const MAX_TOTAL_CHUNKS: usize = 4096;

// ─── serde default 用の関数 ──────────────────────────────────────────────────

fn default_voxel_size() -> f32 { DEFAULT_VOXEL_SIZE }
fn default_chunk_cells() -> u32 { DEFAULT_CHUNK_CELLS }
fn default_iso_level() -> f32 { DEFAULT_ISO_LEVEL }
fn default_ground_chunks_x() -> u32 { DEFAULT_GROUND_CHUNKS_X }
fn default_ground_chunks_z() -> u32 { DEFAULT_GROUND_CHUNKS_Z }
fn default_ground_chunk_y_min() -> i32 { DEFAULT_GROUND_CHUNK_Y_MIN }
fn default_ground_chunk_y_max() -> i32 { DEFAULT_GROUND_CHUNK_Y_MAX }

/// 密度クランプの既定値。
/// 1 チャンク分の広がり（voxel_size * chunk_cells = 0.5 * 32 = 16.0 m）を上限とする。
/// ブラシ編集で密度が発散して勾配（法線）が壊れるのを防ぐ。
fn default_density_clamp() -> f32 {
    DEFAULT_VOXEL_SIZE * DEFAULT_CHUNK_CELLS as f32
}

/// 地形ボクセルシステムの調整用設定。
///
/// | フィールド             | 既定値 | 意味                                                      |
/// |------------------------|--------|-----------------------------------------------------------|
/// | `voxel_size`           | 0.5    | 1 セル辺のメートル数                                       |
/// | `chunk_cells`          | 32     | チャンク 1 軸のセル数（サンプル数 = cells + 1 = 33）       |
/// | `iso_level`            | 0.0    | 等値面しきい値（この値で表面が生成される）                |
/// | `density_clamp`        | 16.0   | ブラシ編集後の密度を [-clamp, +clamp] に制限              |
/// | `ground_chunks_x`      | 4      | 初期地面の X 方向チャンク数                                |
/// | `ground_chunks_z`      | 4      | 初期地面の Z 方向チャンク数                                |
/// | `ground_chunk_y_min`   | -1     | 縦方向チャンクの下限（掘削余白）                          |
/// | `ground_chunk_y_max`   | 1      | 縦方向チャンクの上限（盛土余白）                          |
#[derive(Clone, Serialize, Deserialize, Debug)]
pub struct TerrainSettings {
    /// 1 ボクセルセル辺のサイズ（メートル）
    #[serde(default = "default_voxel_size")]
    pub voxel_size: f32,
    /// チャンク 1 軸あたりのセル数（サンプル数 = chunk_cells + 1）
    #[serde(default = "default_chunk_cells")]
    pub chunk_cells: u32,
    /// マーチングキューブスの等値面しきい値
    #[serde(default = "default_iso_level")]
    pub iso_level: f32,
    /// ブラシ編集後の密度クランプ範囲 [-density_clamp, +density_clamp]
    #[serde(default = "default_density_clamp")]
    pub density_clamp: f32,
    /// 初期地面の X 方向チャンク数
    #[serde(default = "default_ground_chunks_x")]
    pub ground_chunks_x: u32,
    /// 初期地面の Z 方向チャンク数
    #[serde(default = "default_ground_chunks_z")]
    pub ground_chunks_z: u32,
    /// 縦方向チャンクの下限インデックス
    #[serde(default = "default_ground_chunk_y_min")]
    pub ground_chunk_y_min: i32,
    /// 縦方向チャンクの上限インデックス
    #[serde(default = "default_ground_chunk_y_max")]
    pub ground_chunk_y_max: i32,
}

impl TerrainSettings {
    /// チャンク 1 軸あたりのサンプル数を返す（= chunk_cells + 1 = 33）。
    ///
    /// セル数より 1 多いのは、隣り合うセルが端のサンプルを共有し、
    /// さらに +x/+y/+z 境界の 33 枚目のサンプル面を自チャンク内に含めるため。
    pub fn samples_per_axis(&self) -> usize {
        self.chunk_cells as usize + 1
    }

    /// チャンク 1 軸あたりのワールド空間での広がり（メートル）を返す。
    ///
    /// = voxel_size * chunk_cells（= 0.5 * 32 = 16.0 m）
    pub fn chunk_extent(&self) -> f32 {
        self.voxel_size * self.chunk_cells as f32
    }

    /// チャンク構成（初期地面の X/Z チャンク数・チャンク分割数・ボクセルサイズ）を
    /// 上下限へ丸めた上で適用する。
    ///
    /// エディタ（`TERRAIN_INIT` / `TERRAIN_HEIGHTMAP` の引数）から受けた値をそのまま
    /// 信用せず、この 1 箇所で必ず検証・クランプしてから設定へ反映する
    /// （不正値が入ると密度グリッドの確保サイズが破綻するため）。
    ///
    /// `density_clamp` は「1 チャンク分の広がり」を意味する派生値なので、
    /// voxel_size / chunk_cells の変更に合わせてここで再計算する。これを怠ると
    /// 旧構成の 16.0 が残り、大きいチャンクではブラシ編集で密度が頭打ちになる。
    ///
    /// `iso_level` と縦方向チャンク範囲（ground_chunk_y_min/max）は変更しない。
    pub fn apply_chunk_config(
        &mut self,
        chunks_x: u32,
        chunks_z: u32,
        chunk_cells: u32,
        voxel_size: f32,
    ) {
        self.ground_chunks_x = chunks_x.clamp(MIN_GROUND_CHUNKS, MAX_GROUND_CHUNKS);
        self.ground_chunks_z = chunks_z.clamp(MIN_GROUND_CHUNKS, MAX_GROUND_CHUNKS);
        self.chunk_cells = chunk_cells.clamp(MIN_CHUNK_CELLS, MAX_CHUNK_CELLS);
        // NaN は clamp がパニックするため、先に有限性を検査して既定値へ落とす。
        let vs = if voxel_size.is_finite() { voxel_size } else { DEFAULT_VOXEL_SIZE };
        self.voxel_size = vs.clamp(MIN_VOXEL_SIZE, MAX_VOXEL_SIZE);
        // 派生値（1 チャンク分の広がり）を作り直す。
        self.density_clamp = self.chunk_extent();
    }
}

/// 既定の地形設定。
impl Default for TerrainSettings {
    fn default() -> Self {
        Self {
            voxel_size: DEFAULT_VOXEL_SIZE,
            chunk_cells: DEFAULT_CHUNK_CELLS,
            iso_level: DEFAULT_ISO_LEVEL,
            density_clamp: default_density_clamp(),
            ground_chunks_x: DEFAULT_GROUND_CHUNKS_X,
            ground_chunks_z: DEFAULT_GROUND_CHUNKS_Z,
            ground_chunk_y_min: DEFAULT_GROUND_CHUNK_Y_MIN,
            ground_chunk_y_max: DEFAULT_GROUND_CHUNK_Y_MAX,
        }
    }
}
