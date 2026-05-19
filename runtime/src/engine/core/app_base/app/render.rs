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

    /// ウィンドウイベントを処理する（キー入力・マウス・リサイズ・メインループ）。
    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        _id: WindowId,
        event: WindowEvent,
    ) {
        match event {
            WindowEvent::CloseRequested if !self.is_embedded() => {
                if let Some(ipc) = &self.ipc { ipc.send("STOPPED"); }
                event_loop.exit();
            }

            WindowEvent::Resized(size) => {
                self.on_resize(size);
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

