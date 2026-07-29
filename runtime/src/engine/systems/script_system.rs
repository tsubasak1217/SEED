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

/// 1 スクリプト分の呼び出し情報（World の借用を解放した後に使う退避データ）。
///
/// クエリ結果をタプルで持ち回ると各要素の意味が読み取れなくなるため、
/// 名前付きフィールドの構造体にしている。
struct ScriptCall {
    /// CLR ホスト（ライフサイクル関数ポインタ表）
    host:   Arc<ScriptingHost>,
    /// CLR 側スクリプトインスタンスの GCHandle
    handle: isize,
    /// スクリプトが乗るアクターのエンティティ（gameObject / transform の束縛用）
    owner:  Option<Entity>,
    /// ScriptComponent 自体が格納されているスロットエンティティ（フラグ更新用）
    entity: Entity,
    /// このフェーズで OnStart を先に呼ぶべきか（＝まだ OnStart 未実行）
    needs_start: bool,
    /// このフェーズで [SerializeField] 参照フィールドの解決を先に発行すべきか
    needs_resolve: bool,
}

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
            // needs_resolve = 参照フィールドの解決を先に発行すべきか（＝refs_dirty）。
            let calls: Vec<ScriptCall> = world
                .query::<ScriptComponent>()
                .filter(|(_entity, sc)| sc.active)
                .map(|(entity, sc)| ScriptCall {
                    host:          Arc::clone(&sc.host),
                    handle:        sc.handle,
                    owner:         sc.owner,
                    entity,
                    needs_start:   is_first_phase && !sc.started,
                    needs_resolve: is_first_phase && sc.refs_dirty,
                })
                .collect();
            if calls.is_empty() { return; }

            // 2. World ポインタを公開しつつ、収集済みハンドルへスクリプトを実行する。
            //    この間 Rust 側は World への参照を保持しないため、アクセサからの
            //    可変アクセスが安全に行える。
            with_world(world, || {
                for c in &calls {
                    // [SerializeField] の参照フィールドは World / Actor ツリーが
                    // 公開されているこの区間でしか解決できない。OnStart より前に
                    // 発行することで、ユーザーコードからは常に解決済みに見える。
                    if c.needs_resolve {
                        ScriptComponent::resolve_references_raw(&c.host, c.handle);
                    }
                    // OnStart は必ずこのスクリプトの初回ライフサイクル呼び出しより前に走る
                    if c.needs_start {
                        ScriptComponent::run_on_start_raw(&c.host, c.handle, c.owner);
                    }
                    ScriptComponent::run_phase_raw(&c.host, c.handle, c.owner, phase, ctx);
                }
            });

            // 3. OnStart 済み / 参照解決済みフラグを立てる（World 借用が戻ってから行う）。
            //    started は Drop 時の OnDestroy 発火条件も兼ねる。
            if is_first_phase {
                for c in &calls {
                    if !c.needs_start && !c.needs_resolve { continue; }
                    if let Some(sc) = world.get_mut::<ScriptComponent>(c.entity) {
                        if c.needs_start   { sc.started    = true; }
                        if c.needs_resolve { sc.refs_dirty = false; }
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
