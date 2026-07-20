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
