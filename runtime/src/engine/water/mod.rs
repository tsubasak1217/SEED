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

pub use resolved::{ResolvedWaterVolume, WaterVisualParams};
pub use query::WaterQuery;
pub use collect::collect_water_volumes;
pub use shore::{
    ShoreFieldEntry, ShoreFieldSet, ShoreTerrainBounds,
    SHORE_FIELD_MAX_LAYERS, SHORE_FIELD_RESOLUTION,
};
