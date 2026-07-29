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

use std::sync::Arc;

use crate::engine::ecs::{Entity, FnSystem, Phase, Schedule};
use crate::engine::ecs::schedule::all_phases;
use crate::engine::components::ScriptComponent;
use crate::engine::core::scripting::{ScriptingHost, with_world};

/// 全フェーズにスクリプト駆動システムを登録する。
///
/// 各フェーズで World の全 ScriptComponent をクエリし、
/// 対応する C# ライフサイクルメソッドを呼び出す。
///
/// C# のライフサイクル内から transform 等のアクセサが呼ばれると World を可変で
/// 触る（host_api）。そのため、まず呼び出しに必要な値（host/handle/owner）だけを
/// 収集して World の借用を解放し、その後 with_world で World ポインタを公開しながら
/// スクリプトを実行する。これにより「クエリの不変借用」と「アクセサの可変借用」の
/// 競合を避ける。
pub fn register(schedule: &mut Schedule) {
    for phase in all_phases() {
        schedule.add_system(phase, FnSystem::new(system_name(phase), move |world, ctx| {
            // OnStart は「フレーム最初のフェーズ = BeginFrame」でのみ判定する。
            // 有効化されてから最初の BeginFrame の直前に、そのスクリプト自身の
            // OnStart を 1 回だけ呼ぶ（スクリプト単位の直前呼び出し）。
            let is_first_phase = phase == Phase::BeginFrame;

            // 1. 実行に必要な情報を収集する（ここで World の不変借用は終わる）
            // 実効非アクティブ（アクターの active 継承が false またはスロット無効）の
            // スクリプトは呼び出し対象から外す（Scene::sync_script_owners が毎フレーム同期）。
            // needs_start = このフェーズで OnStart を先に呼ぶべきか（＝未 OnStart）。
            let calls: Vec<(Arc<ScriptingHost>, isize, Option<Entity>, Entity, bool)> = world
                .query::<ScriptComponent>()
                .filter(|(_entity, sc)| sc.active)
                .map(|(entity, sc)| (
                    Arc::clone(&sc.host),
                    sc.handle,
                    sc.owner,
                    entity,
                    is_first_phase && !sc.started,
                ))
                .collect();
            if calls.is_empty() { return; }

            // 2. World ポインタを公開しつつ、収集済みハンドルへスクリプトを実行する。
            //    この間 Rust 側は World への参照を保持しないため、アクセサからの
            //    可変アクセスが安全に行える。
            with_world(world, || {
                for (host, handle, owner, _entity, needs_start) in &calls {
                    // OnStart は必ずこのスクリプトの初回ライフサイクル呼び出しより前に走る
                    if *needs_start {
                        ScriptComponent::run_on_start_raw(host, *handle, *owner);
                    }
                    ScriptComponent::run_phase_raw(host, *handle, *owner, phase, ctx);
                }
            });

            // 3. OnStart 済みフラグを立てる（World 借用が戻ってから行う）。
            //    フラグは Drop 時の OnDestroy 発火条件も兼ねる。
            if is_first_phase {
                for (_host, _handle, _owner, entity, needs_start) in &calls {
                    if !*needs_start { continue; }
                    if let Some(sc) = world.get_mut::<ScriptComponent>(*entity) {
                        sc.started = true;
                    }
                }
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
