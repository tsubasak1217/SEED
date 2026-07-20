// ============================================================
//  terrain/mod.rs — 地形ボクセル + マーチングキューブス モジュール
//
//  エンジン非依存の「純粋なデータ + アルゴリズム」層。
//  ECS / GPU への依存は持たず、密度グリッド・メッシュ生成・
//  ブラシ編集・バイナリ永続化のみを提供する。
//  外部から使う主要な型・トレイト・関数を再エクスポートする。
// ============================================================

pub mod settings;
pub mod chunk_coord;
pub mod chunk_data;
pub mod marching_cubes;
pub mod brush;
pub mod tvox;
pub mod heightmap;
pub mod layers;
pub mod paint;
pub mod scatter;

#[cfg(test)]
mod tests;

/// レイヤブレンド（T2）専用のユニットテスト（役割単位でファイル分割）。
#[cfg(test)]
mod tests_layers;

/// 編集ホットパスの CPU 計測（#[ignore] 付き。通常のテスト実行では走らない）。
#[cfg(test)]
mod bench;

pub use settings::{
    TerrainSettings, MAX_CHUNK_CELLS, MAX_GROUND_CHUNKS, MAX_TOTAL_CHUNKS, MAX_VOXEL_SIZE,
    MIN_CHUNK_CELLS, MIN_GROUND_CHUNKS, MIN_VOXEL_SIZE,
};
pub use chunk_coord::ChunkCoord;
pub use chunk_data::TerrainChunkData;
pub use marching_cubes::{
    generate, generate_standalone, interp_vertex_paint, TerrainMesh, TerrainVertexEdge,
};
pub use brush::{apply, chunks_in_brush_aabb, BrushOp, SampleField, SphereBrush};
pub use tvox::{read_chunk, read_header, write_chunk, TvoxError, TvoxHeader, TVOX_MAGIC, TVOX_VERSION};
pub use heightmap::HeightmapField;
pub use layers::{
    blend_rule_and_paint_all, expand_slots, select_top_slots, BlendSlots, DetileMode, LayerRule,
    LayerWeights, TerrainLayer, TerrainLayerSet, TERRAIN_BLEND_SLOTS, TERRAIN_MAX_LAYERS,
};
pub use paint::{apply_paint, PaintField};
// 散布（T3）。tvox の read_chunk / write_chunk と名前が衝突するため、
// 関数は `scatter::` 経由で使う前提とし、ここでは型と定数のみ再エクスポートする。
#[allow(unused_imports)]
pub use scatter::{
    GrassParams, LayerCondition, PropKind, ScatterField, ScatterInstance, ScatterParams,
    ScatterRng, ScatterRule, TerrainProp, TerrainPropSet, TscatterError, TscatterHeader,
    WindParams, GRASS_MAX_SEGMENTS, MAX_SCATTER_GRID_PER_AXIS, MIN_INSTANCE_SPACING_FACTOR,
    TERRAIN_MAX_PROPS, TSCATTER_MAGIC, TSCATTER_VERSION,
};
