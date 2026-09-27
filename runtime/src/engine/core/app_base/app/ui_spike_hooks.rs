// ============================================================
//  ui_spike_hooks.rs — アプリ基盤 W2-0 のスパイクを App へつなぐ所（既定で無効。docs/app_platform_roadmap.md §3.8）
//
//  【受け持ち】
//  - PC の文字入力（ui_spike の ime）: ウィンドウに IME を許可し（allow_desktop_ime_if_requested）、
//      winit の Ime イベントをログへ出す（log_desktop_ime_event）。Android の IME は runtime/android/native の ui_spike が受け持つ
//  指定が無ければ bool を 1 つ見て抜けるだけで、従来の経路（IME は許可しない）を変えない。
//  （切り抜きの試作〈clip=〉は W2-1a で本番の CanvasClipComponent に、描かないときの試作〈idle=・wake_ms=〉は
//   W2-10a で本番の render_policy〈engine::core::redraw・app/redraw_hooks.rs〉に置き換えて外した）
// ============================================================

use winit::event::Ime;

use crate::engine::core::ui_spike::LOG_PREFIX;

use super::App;

/// PC の IME の候補窓を出す位置（試作。ウィンドウの左上からの物理 px）。
/// 実際の文字入力欄の位置は W2-6 で入力欄の矩形から決める。
const IME_SPIKE_CURSOR_POS: (i32, i32) = (100, 100);

/// PC の IME の候補窓を避ける矩形の大きさ（試作。物理 px）。
const IME_SPIKE_CURSOR_SIZE: (u32, u32) = (200, 40);

impl App {
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
