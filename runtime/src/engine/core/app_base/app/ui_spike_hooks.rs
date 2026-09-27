// ============================================================
//  ui_spike_hooks.rs — アプリ基盤 W2-0 のスパイクを App へつなぐ所（既定で無効。docs/app_platform_roadmap.md §3.8）
//
//  【受け持ち】
//  - 描かなくてよいときは描かない（ui_spike の idle= / wake_ms=）:
//      フレームの終わりの「次のフレームの要求」（request_next_frame。frame_renderer.rs の末尾の唯一の入口）、
//      WindowEvent での起こし（note_window_event_for_idle。render.rs の window_event の頭）、
//      WaitUntil の時刻での 1 フレーム（pump_idle_wake。render.rs の about_to_wait）
//  - PC の文字入力（ui_spike の ime）: ウィンドウに IME を許可し（allow_desktop_ime_if_requested）、
//      winit の Ime イベントをログへ出す（log_desktop_ime_event）。Android の IME は runtime/android/native の ui_spike が受け持つ
//  （切り抜きの試作〈clip=〉は W2-1a で本番の CanvasClipComponent に置き換えて外した）
//  どれも指定が無ければ bool を 1 つ見て抜けるだけで、従来の経路（毎フレーム request_redraw・IME は許可しない）を変えない。
//
//  【試作の制限】（W2-10 で決める。§3.8）
//  - 「描く理由」は WindowEvent（入力・大きさ・フォーカス）だけ。アニメーション・スクリプトの要求・
//    プラットフォームのイベント（JNI）・IPC は起こさないので、止まっている間はそれらが次の入力まで遅れる
//  - エディタに埋め込んだ実行（IPC がフレームの中でしか処理されない）と Edit モードでは使わない（Play の単体実行だけ）
// ============================================================

use std::time::{Duration, Instant};

use winit::event::{Ime, WindowEvent};
use winit::event_loop::{ActiveEventLoop, ControlFlow};

use crate::engine::core::ui_spike::idle_redraw::{IdleRedrawGate, NextFrame};
use crate::engine::core::ui_spike::{LOG_PREFIX, UiSpikeConfig};

use super::{App, RuntimeMode, play_diag, surface_lifecycle};

/// 秒 → ミリ秒（ログの表示用）。
const MILLIS_PER_SEC: f64 = 1_000.0;

/// PC の IME の候補窓を出す位置（試作。ウィンドウの左上からの物理 px）。
/// 実際の文字入力欄の位置は W2-6 で入力欄の矩形から決める。
const IME_SPIKE_CURSOR_POS: (i32, i32) = (100, 100);

/// PC の IME の候補窓を避ける矩形の大きさ（試作。物理 px）。
const IME_SPIKE_CURSOR_SIZE: (u32, u32) = (200, 40);

/// 指定から「描かなくてよいときは描かない」の判定を作る（idle= が無ければ無効な判定）。
pub(super) fn idle_gate_from(config: &UiSpikeConfig) -> IdleRedrawGate {
    IdleRedrawGate::new(
        config.idle_after_frames,
        config.idle_wake_ms.map(|ms| Duration::from_millis(u64::from(ms))),
    )
}

impl App {
    /// 「描かなくてよいときは描かない」の判定を使ってよい実行か（Play の単体実行だけ。エディタ埋め込み・Edit では使わない）。
    fn idle_gate_active(&self) -> bool {
        self.ui_spike_idle.is_enabled() && self.mode == RuntimeMode::Play && !self.is_embedded()
    }

    /// フレームの終わりに次のフレームを要求する（frame_renderer.rs の handle_redraw_requested の末尾の唯一の入口）。
    ///
    /// 判定が無効（既定）なら従来どおり毎フレーム request_redraw する。有効なら、入力の無いフレームが続いたら
    /// 要求をやめて ControlFlow を Wait（入力まで眠る）か WaitUntil（wake_ms ごとに 1 フレーム）にする。
    pub(super) fn request_next_frame(&mut self, event_loop: &ActiveEventLoop) {
        if !self.idle_gate_active() {
            if let Some(window) = &self.window {
                window.request_redraw();
            }
            return;
        }
        let was_idle = self.ui_spike_idle.is_idle();
        match self.ui_spike_idle.on_frame_end(Instant::now()) {
            NextFrame::Continue => {
                if let Some(window) = &self.window {
                    window.request_redraw();
                }
            }
            NextFrame::Idle { wake_at } => {
                event_loop.set_control_flow(match wake_at {
                    Some(at) => ControlFlow::WaitUntil(at),
                    None => ControlFlow::Wait,
                });
                // フレームが途絶えても凍結の警告（[PLAY_WD]）を出さない（意図して止めている）
                play_diag::set_intentionally_idle(true);
                if !was_idle {
                    let stats = self.ui_spike_idle.stats();
                    eprintln!(
                        "{LOG_PREFIX} idle: 入力の無いフレームが続いたので描画を止めます（起こす間隔: {}・入った回数 {}・入力で起きた回数 {}・時刻で描いた回数 {}）",
                        wake_at.map_or("入力まで".to_string(), |_| "wake_ms ごと".to_string()),
                        stats.idle_entries,
                        stats.activity_wakes,
                        stats.timer_wakes,
                    );
                }
            }
        }
    }

    /// WindowEvent（入力・大きさ・フォーカス）が来た。止めていたら描画を再開する。
    ///
    /// render.rs の window_event の頭で、どのイベントでも呼ぶ（RedrawRequested は数えない）。
    pub(super) fn note_window_event_for_idle(&mut self, event: &WindowEvent, event_loop: &ActiveEventLoop) {
        if !self.idle_gate_active() || matches!(event, WindowEvent::RedrawRequested) {
            return;
        }
        if self.ui_spike_idle.note_activity(Instant::now()) {
            event_loop.set_control_flow(surface_lifecycle::ACTIVE_CONTROL_FLOW);
            play_diag::set_intentionally_idle(false);
            if let Some(window) = &self.window {
                window.request_redraw();
            }
            eprintln!("{LOG_PREFIX} idle: 入力で描画を再開します（{}）", window_event_name(event));
        }
    }

    /// 入力で起こした後の最初のフレームの頭で、起こしてからの遅れをログへ出す（render.rs の RedrawRequested の頭で呼ぶ）。
    pub(super) fn log_idle_wake_latency(&mut self) {
        if !self.idle_gate_active() {
            return;
        }
        if let Some(latency) = self.ui_spike_idle.take_wake_latency(Instant::now()) {
            eprintln!(
                "{LOG_PREFIX} idle: 入力から最初のフレームの頭まで {:.2} ms",
                latency.as_secs_f64() * MILLIS_PER_SEC
            );
        }
    }

    /// WaitUntil の時刻が来ていたら 1 フレームだけ描く（render.rs の about_to_wait で毎周呼ぶ）。
    pub(super) fn pump_idle_wake(&mut self, event_loop: &ActiveEventLoop) {
        if !self.idle_gate_active() {
            return;
        }
        if self.ui_spike_idle.wake_due(Instant::now()) {
            event_loop.set_control_flow(surface_lifecycle::ACTIVE_CONTROL_FLOW);
            if let Some(window) = &self.window {
                window.request_redraw();
            }
        }
    }

    /// PC: ウィンドウに IME を許可する（ui_spike の ime のときだけ。ウィンドウを作った直後に 1 回呼ぶ）。
    ///
    /// winit 0.30 の Windows はウィンドウを作るときに IME を切り離し（ImmAssociateContextEx(IACE_CHILDREN)）、
    /// set_ime_allowed(true) で既定の IME の文脈へ戻す。Android では set_ime_allowed(true) がソフトキーボードを
    /// 出してしまう（show_soft_input）ので呼ばない（Android は runtime/android/native の ui_spike が受け持つ）。
    pub(super) fn allow_desktop_ime_if_requested(&self) {
        if cfg!(target_os = "android") || !crate::engine::core::ui_spike::config().ime {
            return;
        }
        let Some(window) = &self.window else { return };
        window.set_ime_allowed(true);
        let (x, y) = IME_SPIKE_CURSOR_POS;
        let (width, height) = IME_SPIKE_CURSOR_SIZE;
        window.set_ime_cursor_area(
            winit::dpi::PhysicalPosition::new(x, y),
            winit::dpi::PhysicalSize::new(width, height),
        );
        eprintln!(
            "{LOG_PREFIX} ime: ウィンドウに IME を許可しました（候補窓は ({x}, {y}) の {width}×{height} の矩形を避けて出る）"
        );
    }

    /// PC: winit の Ime イベントをログへ出す（ui_spike の ime のときだけ。render.rs の window_event から呼ぶ）。
    pub(super) fn log_desktop_ime_event(&self, ime: &Ime) {
        if !crate::engine::core::ui_spike::config().ime {
            return;
        }
        match ime {
            Ime::Enabled => eprintln!("{LOG_PREFIX} ime: Enabled"),
            Ime::Preedit(text, cursor) => eprintln!("{LOG_PREFIX} ime: Preedit {text:?} カーソル（バイト位置）={cursor:?}"),
            Ime::Commit(text) => eprintln!("{LOG_PREFIX} ime: Commit {text:?}"),
            Ime::Disabled => eprintln!("{LOG_PREFIX} ime: Disabled"),
        }
    }
}

/// ログ用の WindowEvent の短い名前（値は出さない）。
fn window_event_name(event: &WindowEvent) -> &'static str {
    match event {
        WindowEvent::Touch(_) => "Touch",
        WindowEvent::KeyboardInput { .. } => "KeyboardInput",
        WindowEvent::MouseInput { .. } => "MouseInput",
        WindowEvent::CursorMoved { .. } => "CursorMoved",
        WindowEvent::MouseWheel { .. } => "MouseWheel",
        WindowEvent::Resized(_) => "Resized",
        WindowEvent::Focused(_) => "Focused",
        WindowEvent::Ime(_) => "Ime",
        _ => "その他",
    }
}
