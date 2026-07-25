// ============================================================
//  play_diag.rs — 埋め込みインプレース Play の凍結/黒画面 診断計器（一時）
//
//  【役割】
//  「フレームループがフレーム0以降回らない（恒久凍結）」不具合の**凍結地点・凍結種別**を
//  実機ログ1回で確定させるための常設計器を 1 か所へ集約する。frame_renderer.rs（フレーム
//  内ステージ印・ハートビート）と render.rs（イベントループ側の about_to_wait / WindowEvent）の
//  両方から参照するため、App の submodule として分離した。**原因確定後にこのファイルごと撤去**する。
//
//  【提供する計器】
//   1. フレーム内ステージ印（FrameStage / mark_frame_stage）: どのステージで止まったか。
//   2. フレーム凍結ウォッチドッグ（ensure_frame_watchdog → [PLAY_WD]）: 別スレッドで監視。
//      stage / 経過秒 / about_to_wait tick 増分 / 直近 WindowEvent / IsWindowVisible /
//      window_focused / render_paused を出力する。
//   3. about_to_wait ハートビート（atw_tick）: winit イベントループが回っているかの判定。
//      増分>0 なら「ループは回っているが RedrawRequested が配達されない(B)」、
//      0 なら「イベントループスレッド自体がブロック(A)」を切り分ける。
//   4. WindowEvent トレース（note_window_event → [PLAY_EV]）: ENTER_PLAY から一定時間だけ
//      届いた WindowEvent 種別を列挙（Focused/Occluded 等の到達を見る）。
//   5. Play 黒画面診断のフレームカウンタ/ハートビート状態（PLAY_DIAG_*）。
//
//  すべて原子操作のみで軽量。Play/Edit 問わず常時動作（既定 ON）。
// ============================================================

use std::sync::atomic::{AtomicBool, AtomicIsize, AtomicU32, AtomicU64, AtomicU8, Ordering};
use std::sync::LazyLock;
use std::time::Instant;

// ── Play 黒画面診断（[PLAY_DIAG] / [PLAY_HB]）ゲート・状態 ─────────────────────

/// Play 診断（[PLAY_DIAG]/[PLAY_HB]）を出力するか。
/// 【一時】切り分け中は常時 ON（env はエディタ→子プロセスへ届かない事故があったため）。
/// 原因確定後に env ゲート（SEED_PLAY_DIAG）へ戻す。
pub(super) static PLAY_DIAG_ENABLED: LazyLock<bool> =
    LazyLock::new(|| true || std::env::var_os("SEED_PLAY_DIAG").is_some());
/// PLAY_DIAG を出力する Play フレーム数（先頭からこの回数だけ）。
pub(super) const PLAY_DIAG_FRAMES: u64 = 10;
/// Play（非ポーズ）フレームカウンタ。
pub(super) static PLAY_DIAG_FRAME: AtomicU64 = AtomicU64::new(0);
/// ハートビート直近出力時刻（WD_EPOCH からの経過 ms）。
pub(super) static PLAY_HB_LAST_MS: AtomicU64 = AtomicU64::new(0);
/// ハートビートを強制出力する経過閾値 [ms]。
pub(super) const PLAY_HB_GAP_MS: u64 = 1_000;
/// ハートビートを定期出力するフレーム間隔。
pub(super) const PLAY_HB_EVERY: u64 = 60;

// ── 共通時刻起点 ──────────────────────────────────────────────────────────

/// ウォッチドッグ・ハートビート・イベントトレース共通の時刻起点。
pub(super) static WD_EPOCH: LazyLock<Instant> = LazyLock::new(Instant::now);

// ── フレーム内ステージ印 ───────────────────────────────────────────────────

/// フレーム内の主要ステージ（凍結地点特定用）。u8 表現をウォッチドッグが読む。
#[derive(Clone, Copy)]
#[repr(u8)]
pub(super) enum FrameStage {
    Idle             = 0,
    FrameStart       = 1,
    GamepadDone      = 2,
    TerrainLodDone   = 3,
    IpcDone          = 4,
    PhysicsDone      = 5,
    ScriptPhaseDone  = 6,
    CharCtrlDone     = 7,
    EncodeDone       = 8,
    PresentDone      = 9,
    StartPhysicsDone = 10,
    FrameEnd         = 11,
}

/// 現在通過中のフレームステージ（FrameStage as u8）。
static FRAME_STAGE: AtomicU8 = AtomicU8::new(0);

/// FrameStage の u8 値を人間可読名へ写す。
fn frame_stage_name(v: u8) -> &'static str {
    match v {
        0  => "idle",
        1  => "frame_start",
        2  => "gamepad_done",
        3  => "terrain_lod_done",
        4  => "ipc_done",
        5  => "update_physics_done",
        6  => "script_run_phase_done",
        7  => "sync_char_controllers_done",
        8  => "encode_done",
        9  => "present_done",
        10 => "start_physics_done",
        11 => "frame_end",
        _  => "unknown",
    }
}

/// 現在のフレームステージを記録する（原子ストアのみ・実質ゼロコスト）。
#[inline]
pub(super) fn mark_frame_stage(s: FrameStage) {
    FRAME_STAGE.store(s as u8, Ordering::Relaxed);
}

// ── ループ生存信号（フレーム開始ごとに更新）───────────────────────────────

/// 「ループが生きている」信号: 最後にフレーム処理へ入った時刻（WD_EPOCH からの経過 ms）。
static LAST_FRAME_DONE_MS: AtomicU64 = AtomicU64::new(0);

/// フレーム処理へ入ったことを記録する（handle_redraw_requested 先頭で毎回呼ぶ）。
/// これが止まると＝次フレームが来ない＝凍結、とウォッチドッグが判定する。
#[inline]
pub(super) fn note_frame_alive() {
    LAST_FRAME_DONE_MS.store(WD_EPOCH.elapsed().as_millis() as u64, Ordering::Relaxed);
}

// ── (A)/(B) 切り分け用の状態 ───────────────────────────────────────────────

/// about_to_wait 呼び出し回数（winit イベントループが回っているかの指標）。
static ATW_TICKS: AtomicU64 = AtomicU64::new(0);
/// 直近に処理した WindowEvent 種別（EvKind as u8）。
static LAST_WINDOW_EVENT: AtomicU8 = AtomicU8::new(0);
/// 直近フレームの window_focused / render_paused / HWND（ウォッチドッグが読む）。
static WINDOW_FOCUSED: AtomicBool = AtomicBool::new(false);
static RENDER_PAUSED:  AtomicBool = AtomicBool::new(false);
static DIAG_HWND:      AtomicIsize = AtomicIsize::new(0);

/// winit の WindowEvent 種別（[PLAY_EV]/[PLAY_WD] 表示用）。
#[derive(Clone, Copy)]
#[repr(u8)]
pub(super) enum EvKind {
    Other          = 0,
    RedrawRequested = 1,
    Focused        = 2,
    Occluded       = 3,
    Resized        = 4,
    CursorMoved    = 5,
    MouseInput     = 6,
    MouseWheel     = 7,
    KeyboardInput  = 8,
    CloseRequested = 9,
    CursorEntered  = 10,
    CursorLeft     = 11,
}

/// EvKind の u8 値を人間可読名へ写す。
fn ev_kind_name(v: u8) -> &'static str {
    match v {
        0  => "(none)",
        1  => "RedrawRequested",
        2  => "Focused",
        3  => "Occluded",
        4  => "Resized",
        5  => "CursorMoved",
        6  => "MouseInput",
        7  => "MouseWheel",
        8  => "KeyboardInput",
        9  => "CloseRequested",
        10 => "CursorEntered",
        11 => "CursorLeft",
        _  => "unknown",
    }
}

/// about_to_wait を通過したことを記録する（イベントループ生存カウンタ）。
#[inline]
pub(super) fn atw_tick() {
    ATW_TICKS.fetch_add(1, Ordering::Relaxed);
}

/// 直近フレームの状態フラグを公開する（handle_redraw_requested から毎フレーム）。
#[inline]
pub(super) fn publish_frame_flags(focused: bool, render_paused: bool, hwnd: isize) {
    WINDOW_FOCUSED.store(focused, Ordering::Relaxed);
    RENDER_PAUSED.store(render_paused, Ordering::Relaxed);
    DIAG_HWND.store(hwnd, Ordering::Relaxed);
}

// ── WindowEvent トレース（[PLAY_EV]）────────────────────────────────────────

/// トレース有効窓の起点（WD_EPOCH からの経過 ms）。0 = 無効。
static PLAY_EV_START_MS: AtomicU64 = AtomicU64::new(0);
/// トレース済みイベント件数。
static PLAY_EV_COUNT: AtomicU32 = AtomicU32::new(0);
/// トレースを続ける時間 [ms]（ENTER_PLAY から）。
const PLAY_EV_WINDOW_MS: u64 = 10_000;
/// トレースする最大イベント件数。
const PLAY_EV_MAX: u32 = 100;

/// WindowEvent トレースを開始する（ENTER_PLAY 時に呼ぶ）。
pub(super) fn begin_event_trace() {
    PLAY_EV_COUNT.store(0, Ordering::Relaxed);
    PLAY_EV_START_MS.store(WD_EPOCH.elapsed().as_millis() as u64, Ordering::Relaxed);
    eprintln!("[PLAY_EV] --- trace begin (ENTER_PLAY, {PLAY_EV_WINDOW_MS}ms / max {PLAY_EV_MAX}件) ---");
}

/// WindowEvent を 1 件記録する（種別のみ。値は出さない）。
/// 直近種別を LAST_WINDOW_EVENT へ保存し、トレース窓内なら [PLAY_EV] を出す。
pub(super) fn note_window_event(k: EvKind) {
    let code = k as u8;
    LAST_WINDOW_EVENT.store(code, Ordering::Relaxed);

    let start = PLAY_EV_START_MS.load(Ordering::Relaxed);
    if start == 0 { return; }
    let now = WD_EPOCH.elapsed().as_millis() as u64;
    if now.saturating_sub(start) > PLAY_EV_WINDOW_MS {
        // 窓終了: 以後は種別記録のみ（トレース停止）。
        PLAY_EV_START_MS.store(0, Ordering::Relaxed);
        return;
    }
    let n = PLAY_EV_COUNT.fetch_add(1, Ordering::Relaxed);
    if n >= PLAY_EV_MAX {
        PLAY_EV_START_MS.store(0, Ordering::Relaxed);
        return;
    }
    eprintln!("[PLAY_EV] {}", ev_kind_name(code));
}

// ── フレーム凍結ウォッチドッグ（[PLAY_WD]）─────────────────────────────────

/// フレーム凍結とみなす無更新閾値 [ms]。
const WD_STUCK_THRESHOLD_MS: u64 = 2_000;
/// 凍結中に [PLAY_WD] を再出力する間隔 [ms]。
const WD_WARN_INTERVAL_MS: u64 = 5_000;
/// 監視スレッドのポーリング周期 [ms]。
const WD_POLL_INTERVAL_MS: u64 = 500;
/// 監視スレッドを 1 度だけ spawn するためのフラグ。
static WD_SPAWNED: AtomicBool = AtomicBool::new(false);

/// HWND が可視かどうか（Windows のみ実 API・他は常に true 相当）。
#[cfg(target_os = "windows")]
fn is_window_visible(hwnd: isize) -> bool {
    if hwnd == 0 { return false; }
    use windows_sys::Win32::UI::WindowsAndMessaging::IsWindowVisible;
    unsafe { IsWindowVisible(hwnd as _) != 0 }
}
#[cfg(not(target_os = "windows"))]
fn is_window_visible(_hwnd: isize) -> bool { true }

/// フレーム凍結監視スレッドを 1 度だけ起動する（軽量・常時 ON）。
/// フレーム処理への進入が WD_STUCK_THRESHOLD_MS を超えて途絶えたら、最後に通過した
/// ステージ・about_to_wait tick 増分・直近 WindowEvent・ウィンドウ可視/フォーカス/描画停止
/// を付けて [PLAY_WD] を WD_WARN_INTERVAL_MS 間隔で出力する。
pub(super) fn ensure_frame_watchdog() {
    if WD_SPAWNED.swap(true, Ordering::Relaxed) {
        return;
    }
    // 起点を確定し、初回進入時刻を「今」に置く（フレーム0末での凍結も検出できるよう 0 にしない）。
    LAST_FRAME_DONE_MS.store(WD_EPOCH.elapsed().as_millis() as u64, Ordering::Relaxed);
    std::thread::Builder::new()
        .name("seed-frame-watchdog".into())
        .spawn(|| {
            let mut last_warn_ms: u64 = 0;
            let mut last_atw:     u64 = 0;
            loop {
                std::thread::sleep(std::time::Duration::from_millis(WD_POLL_INTERVAL_MS));
                let now  = WD_EPOCH.elapsed().as_millis() as u64;
                let done = LAST_FRAME_DONE_MS.load(Ordering::Relaxed);
                let gap  = now.saturating_sub(done);
                if gap >= WD_STUCK_THRESHOLD_MS
                    && now.saturating_sub(last_warn_ms) >= WD_WARN_INTERVAL_MS
                {
                    let stage   = frame_stage_name(FRAME_STAGE.load(Ordering::Relaxed));
                    let atw     = ATW_TICKS.load(Ordering::Relaxed);
                    let atw_inc = atw.saturating_sub(last_atw);
                    last_atw    = atw;
                    let last_ev = ev_kind_name(LAST_WINDOW_EVENT.load(Ordering::Relaxed));
                    let hwnd    = DIAG_HWND.load(Ordering::Relaxed);
                    let visible = is_window_visible(hwnd);
                    let focused = WINDOW_FOCUSED.load(Ordering::Relaxed);
                    let rpaused = RENDER_PAUSED.load(Ordering::Relaxed);
                    // atw_inc>0 → イベントループは回っている(B: RedrawRequested 不配達)。
                    // atw_inc==0 → イベントループスレッド自体がブロック(A)。
                    eprintln!(
                        "[PLAY_WD] stuck at stage={stage} elapsed={:.1}s atw_ticks={atw_inc} \
                         last_event={last_ev} visible={visible} focused={focused} render_paused={rpaused} \
                         hwnd=0x{:X}  => {}",
                        gap as f64 / 1000.0, hwnd,
                        if atw_inc > 0 {
                            "(B)イベントループは回っている・RedrawRequested が配達されない"
                        } else {
                            "(A)イベントループスレッド自体がブロック"
                        },
                    );
                    last_warn_ms = now;
                }
            }
        })
        .ok();
}
