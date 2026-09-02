// ============================================================
//  render.rs — ApplicationHandler 実装（resumed / window_event / device_event）
//
//  winit イベントループへの応答処理の入口。
//  実際のフレームレンダリングは frame_renderer.rs の handle_redraw_requested に委譲する。
// ============================================================

use winit::application::ApplicationHandler;
use winit::event::{DeviceEvent, DeviceId, WindowEvent};
use winit::event_loop::ActiveEventLoop;
use winit::window::WindowId;

use super::App;

impl ApplicationHandler for App {
    /// ウィンドウ・レンダラーを初期化し、IPC へ READY を通知する。
    /// 実装本体は app_init.rs の handle_resumed に委譲する。
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        self.handle_resumed(event_loop);
    }

    /// イベントループが次のイベント待ちへ入る直前に呼ばれる。
    /// 【一時・診断】ここでカウンタを進めることで「イベントループスレッドが回っているか」を
    /// ウォッチドッグ（別スレッド）が判定できる。凍結時に atw_ticks が増えていれば
    /// 「ループは回っているが RedrawRequested が配達されない(B)」、増分ゼロなら
    /// 「イベントループスレッド自体がブロック(A)」を切り分ける。
    ///
    /// 【恒久機能】ここからフレーム途絶中の IPC ポンプも行う。埋め込みウィンドウが
    /// 他タブの裏に隠れると WM_PAINT（RedrawRequested）が配送されずフレームループが
    /// 止まるが、IPC 処理はフレーム内でしか走らないため VALIDATE_WGSL 等が滞留して
    /// エディタ側がタイムアウトする。描画が止まっている間だけ IPC を処理する。
    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        super::play_diag::atw_tick();
        self.pump_ipc_while_frames_stalled(event_loop);
    }

    /// ウィンドウイベントを処理する（キー入力・マウス・リサイズ・メインループ）。
    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        _id: WindowId,
        event: WindowEvent,
    ) {
        // 【一時・診断】届いた WindowEvent 種別を記録（ENTER_PLAY から一定時間 [PLAY_EV] へ出力）。
        // 「RedrawRequested が配達されなくなる直前に何が届いていたか」（Focused/Occluded 等）を見る。
        super::play_diag::note_window_event(classify_window_event(&event));

        match event {
            WindowEvent::CloseRequested if !self.is_embedded() => {
                // 未保存のセーブデータを書き出してから終了する（明示 Save() の取りこぼし対策）。
                crate::engine::core::save::flush_if_dirty();
                if let Some(ipc) = &self.ipc { ipc.send("STOPPED"); }
                event_loop.exit();
            }

            WindowEvent::Resized(size) => {
                self.on_resize(size);
            }

            // フォーカス状態を記録する。フォーカスが無い間はフレームレートを抑えて
            // 遮蔽時の present 即時リターンによる暴走ループ（毎秒数千フレーム）を防ぐ。
            WindowEvent::Focused(focused) => {
                self.window_focused = focused;
                // フォーカスを失うとボタンの解放イベントが届かなくなることがあり、
                // カメラ操作中だとカーソルが非表示のまま取り残される。
                // 押下状態を落として閉じ込め・非表示を必ず解除する。
                if !focused {
                    self.force_restore_camera_cursor();
                }
            }

            WindowEvent::KeyboardInput { event, .. } => {
                self.on_keyboard_input(event);
            }

            WindowEvent::MouseInput { button, state, .. } => {
                self.on_mouse_button(button, state);
            }

            WindowEvent::CursorMoved { position, .. } => {
                self.on_cursor_moved(position);
            }

            WindowEvent::MouseWheel { delta, .. } => {
                self.on_mouse_wheel(delta);
            }

            // ── メインループ ──────────────────────────────────
            WindowEvent::RedrawRequested => {
                self.handle_redraw_requested(event_loop);
            }

            _ => {}
        }
    }

    /// デバイスイベントを処理する（マウス移動 → カメラ入力）。
    fn device_event(
        &mut self,
        _event_loop: &ActiveEventLoop,
        _device_id: DeviceId,
        event: DeviceEvent,
    ) {
        if let DeviceEvent::MouseMotion { delta: (dx, dy) } = event {
            self.input.process_mouse_motion(dx, dy);
            self.cam_input.mouse_dx += dx as f32;
            self.cam_input.mouse_dy += dy as f32;
        }
    }
}

/// 【一時・診断】WindowEvent を診断用の EvKind へ分類する（種別のみ・値は見ない）。
fn classify_window_event(event: &WindowEvent) -> super::play_diag::EvKind {
    use super::play_diag::EvKind;
    match event {
        WindowEvent::RedrawRequested   => EvKind::RedrawRequested,
        WindowEvent::Focused(_)        => EvKind::Focused,
        WindowEvent::Occluded(_)       => EvKind::Occluded,
        WindowEvent::Resized(_)        => EvKind::Resized,
        WindowEvent::CursorMoved { .. } => EvKind::CursorMoved,
        WindowEvent::CursorEntered { .. } => EvKind::CursorEntered,
        WindowEvent::CursorLeft { .. } => EvKind::CursorLeft,
        WindowEvent::MouseInput { .. } => EvKind::MouseInput,
        WindowEvent::MouseWheel { .. } => EvKind::MouseWheel,
        WindowEvent::KeyboardInput { .. } => EvKind::KeyboardInput,
        WindowEvent::CloseRequested    => EvKind::CloseRequested,
        _                              => EvKind::Other,
    }
}

