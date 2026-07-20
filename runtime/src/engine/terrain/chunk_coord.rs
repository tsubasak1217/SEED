// ============================================================
//  terrain/chunk_coord.rs — チャンク座標
//
//  【責務】
//    地形チャンクを一意に識別する整数格子座標を表す。
//    チャンクのワールド空間原点（最小コーナー）計算を提供する。
// ============================================================

use serde::{Deserialize, Serialize};

use super::settings::TerrainSettings;

/// チャンクの整数格子座標。
///
/// ワールドは chunk_extent（既定 16.0 m）刻みのチャンクに分割される。
/// 座標 (x, y, z) のチャンクは、ワールド空間で
/// [x*extent, (x+1)*extent) × ... の範囲を占める。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize, Debug)]
pub struct ChunkCoord {
    pub x: i32,
    pub y: i32,
    pub z: i32,
}

impl ChunkCoord {
    /// チャンク座標を生成する。
    pub fn new(x: i32, y: i32, z: i32) -> Self {
        Self { x, y, z }
    }

    /// チャンクの最小コーナー（原点）をワールド空間メートルで返す。
    ///
    /// = coord * chunk_extent（各軸）。
    /// このチャンク内のローカル座標にこの原点を足すとワールド座標になる。
    pub fn world_origin(&self, settings: &TerrainSettings) -> [f32; 3] {
        // ─── 各軸のチャンク座標にチャンク幅を掛けて最小コーナーを求める ───
        let extent = settings.chunk_extent();
        [
            self.x as f32 * extent,
            self.y as f32 * extent,
            self.z as f32 * extent,
        ]
    }
}
