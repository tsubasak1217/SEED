// ============================================================
//  script_system.rs — C# スクリプト駆動システム
//
//  World 内の全 ScriptComponent に対して、フェーズごとの
//  ライフサイクルメソッド（BeginFrame → … → EndFrame）を
//  CLR 側で実行する。
//
//  実行タイミングは frame_renderer のゲームロジックブロック
//  （Play モード・非ポーズ時のみ）で Scene::run_phase 経由。
// ============================================================

use crate::engine::ecs::{FnSystem, Phase, Schedule};
use crate::engine::ecs::schedule::all_phases;
use crate::engine::components::ScriptComponent;

/// 全フェーズにスクリプト駆動システムを登録する。
///
/// 各フェーズで World の全 ScriptComponent をクエリし、
/// 対応する C# ライフサイクルメソッドを呼び出す。
pub fn register(schedule: &mut Schedule) {
    for phase in all_phases() {
        schedule.add_system(phase, FnSystem::new(system_name(phase), move |world, ctx| {
            // ScriptComponent はスロット専用 entity に格納されている。
            // run_phase は &self で呼べるため不変クエリで十分。
            for (_entity, sc) in world.query::<ScriptComponent>() {
                sc.run_phase(phase, ctx);
            }
        }));
    }
}

/// フェーズごとのシステム名（デバッグ・プロファイル表示用）。
fn system_name(phase: Phase) -> &'static str {
    match phase {
        Phase::BeginFrame     => "script_begin_frame",
        Phase::EarlyUpdate    => "script_early_update",
        Phase::Update         => "script_update",
        Phase::ConstantUpdate => "script_constant_update",
        Phase::LateUpdate     => "script_late_update",
        Phase::Render         => "script_render",
        Phase::EndFrame       => "script_end_frame",
    }
}
