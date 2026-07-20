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

#[cfg(test)]
mod tests;

/// レイヤブレンド（T2）専用のユニットテスト（役割単位でファイル分割）。
#[cfg(test)]
mod tests_layers;

pub use settings::TerrainSettings;
pub use chunk_coord::ChunkCoord;
pub use chunk_data::TerrainChunkData;
pub use marching_cubes::{generate, generate_standalone, TerrainMesh};
pub use brush::{apply, chunks_in_brush_aabb, BrushOp, SampleField, SphereBrush};
pub use tvox::{read_chunk, write_chunk, TvoxError, TVOX_MAGIC, TVOX_VERSION};
pub use heightmap::HeightmapField;
pub use layers::{
    blend_rule_and_paint, LayerRule, LayerWeights, TerrainLayer, TerrainLayerSet,
    TERRAIN_LAYER_COUNT,
};
pub use paint::{apply_paint, PaintField};
