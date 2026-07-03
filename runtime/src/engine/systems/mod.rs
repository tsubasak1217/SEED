// ============================================================
//  systems/mod.rs — エンジン組み込み ECS システム群
//
//  Schedule に登録して毎フレーム実行されるシステムをここに集約する。
//  ゲームロジックはコンポーネントにではなくシステムに置く（ECS 理念）。
// ============================================================

pub mod script_system;

use crate::engine::ecs::Schedule;

/// エンジン標準のシステム一式を Schedule に登録する。
/// Scene 生成時に呼ばれる。
pub fn register_default_systems(schedule: &mut Schedule) {
    script_system::register(schedule);
}
