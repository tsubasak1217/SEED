// ============================================================
//  water/mod.rs — 水システムのエンジン層
//
//  水の「形状定義（WaterVolumeComponent）」と、それを使う側
//  （描画・遊泳・浮力・水中ポストエフェクト）の間に置く中間層。
//
//  【設計方針】
//  シーン上のアクタ＋WaterVolumeComponent を、まず一度だけ
//  ワールド空間の中間表現 `ResolvedWaterVolume` へ解決する（collect）。
//  描画も問い合わせ（query）もこの単一の中間表現だけを見るため、
//  「描画都合の実装をゲームロジックが参照してしまう」依存が生じない。
//
//    Actor + WaterVolumeComponent
//              │  collect::collect_water_volumes
//              ▼
//      [ResolvedWaterVolume]  ←─ 唯一の中間表現
//         │            │
//         │            └─ WaterQuery（遊泳・浮力・水中判定の正式 API）
//         └─ レンダラ（別タスク）
// ============================================================

// W1 時点では本モジュールの消費側（レンダラ／遊泳・浮力・水中ポスト）が未実装のため、
// 公開 API がまだどこからも呼ばれず dead_code 警告になる。API は確定済みで
// 後続フェーズ（W2 以降）がそのまま使うため、モジュール単位で警告を抑止する。
#![allow(dead_code)]

pub mod resolved;
pub mod query;
pub mod collect;
pub mod shore;
pub mod spline;
// 水位グラフ（Phase W2.5）: 数値計算（level_graph）と ECS 結線（level_sim）を分ける。
pub mod level_graph;
pub mod level_sim;

pub use resolved::{ResolvedWaterVolume, WaterVisualParams};
pub use spline::{
    RiverNode, RiverPath, RiverSample,
    RIVER_MAX_CONTROL_POINTS, RIVER_MAX_SEGMENTS, RIVER_MIN_CONTROL_POINTS,
    RIVER_SAMPLE_STEP_M, RIVER_SEGMENT_LENGTH_MIN, RIVER_WIDTH_MIN,
};
pub use query::WaterQuery;
pub use collect::collect_water_volumes;
pub use shore::{
    ShoreFieldEntry, ShoreFieldSet, ShoreTerrainBounds,
    SHORE_FIELD_MAX_LAYERS, SHORE_FIELD_RESOLUTION,
};
// 水位グラフ（Phase W2.5）。計算は level_graph、シーンとの結線は level_sim。
// 外部（app/interaction）が使うのは結線側の 2 型のみ。level_graph の計算 API は
// level_sim だけが呼ぶ内部詳細なので、モジュール外へは再エクスポートしない。
pub use level_sim::{WaterFlowEvent, WaterLevelSim};
