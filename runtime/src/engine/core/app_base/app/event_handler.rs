// ============================================================
//  event_handler.rs — ウィンドウイベントハンドラ（基本入力）
//
//  【含む処理】
//  - on_resize:         ウィンドウリサイズ処理
//  - on_keyboard_input: キーボード入力処理（Ctrl+Z/Y Undo/Redo）
//  - on_mouse_button:   マウスボタン処理（LMB / RMB）
//  - on_mouse_wheel:    マウスホイール処理
//
//  カーソル移動・ドラッグ処理は drag_handler.rs に移動した。
// ============================================================

use winit::dpi::PhysicalSize;
use winit::event::{ElementState, KeyEvent, MouseButton, MouseScrollDelta};
use winit::keyboard::{KeyCode, PhysicalKey};

use crate::engine::methods::drawer::IdBuffer;

use super::{
    App, RuntimeMode,
    camera_grab_start, camera_grab_end,
    mmb_grab_start, mmb_grab_end,
    release_window_clamp, warp_cursor_to_local,
};

impl App {
    // ============================================================
    //  on_resize
    // ============================================================

    /// ウィンドウリサイズ時の処理。
    ///
    /// 埋め込みモードでは Vulkan の currentExtent が親コンテナの可視領域サイズに固定される。
    /// SetWindowLong(WS_CHILD) が生成する中間的な WM_SIZE を使うと
    /// depth と color attachment の不一致が起こるため、親がいる場合は GetClientRect(parent) を優先する。
    pub(super) fn on_resize(&mut self, size: PhysicalSize<u32>) {
        let effective_size = self.get_parent_client_size().unwrap_or(size);
        if let Some(r) = &mut self.renderer { r.resize(effective_size); }
        self.camera.set_aspect_ratio(effective_size.width, effective_size.height);
        if effective_size.width > 0 && effective_size.height > 0 {
            if let Some(dc) = &self.draw_ctx {
                self.id_buffer = Some(IdBuffer::new(&dc.device, effective_size.width, effective_size.height));
            }
        }
    }

    // ============================================================
    //  on_keyboard_input
    // ============================================================

    /// キーボード入力処理。
    ///
    /// Ctrl+Z で Undo、Ctrl+Y で Redo を実行する。
    /// Ctrl キー押下状態を `self.ctrl_held` に記録する。
    pub(super) fn on_keyboard_input(&mut self, event: KeyEvent) {
        let pressed = event.state == ElementState::Pressed;
        if let PhysicalKey::Code(key) = event.physical_key {
            self.input.process_key(key, pressed);

            match key {
                KeyCode::ControlLeft | KeyCode::ControlRight => {
                    self.ctrl_held = pressed;
                }
                KeyCode::Escape if pressed => {
                    // Esc: コントロールポイントの選択を解除する
                    //（アクタの選択は解除しない。点だけを手放して、
                    //  移動ギズモを通常どおりアクタ Transform へ戻す）。
                    self.clear_control_point_selection();
                }
                KeyCode::KeyZ if pressed && self.ctrl_held => {
                    // Ctrl+Z: Undo 実行
                    let result = if let Some(scene) = &mut self.scene {
                        self.undo_history.undo(scene)
                    } else { None };
                    if let Some((structural, sel)) = result {
                        if let Some(ids) = sel {
                            self.selected_instances = ids;
                            self.send_selected();
                        }
                        if structural {
                            self.send_hierarchy();
                        }
                    }
                }
                KeyCode::KeyY if pressed && self.ctrl_held => {
                    // Ctrl+Y: Redo 実行
                    let result = if let Some(scene) = &mut self.scene {
                        self.undo_history.redo(scene)
                    } else { None };
                    if let Some((structural, sel)) = result {
                        if let Some(ids) = sel {
                            self.selected_instances = ids;
                            self.send_selected();
                        }
                        if structural {
                            self.send_hierarchy();
                        }
                    }
                }
                _ => {}
            }
        }
    }

    // ============================================================
    //  on_mouse_button
    // ============================================================

    /// マウスボタン入力処理（LMB / RMB）。
    ///
    /// - LMB 押下: Edit/Pause モードでギズモドラッグ開始を試みる
    /// - LMB 離し: ドラッグ Undo 記録またはピックをスケジュール
    /// - RMB 押下: カメラ grab 開始（Edit/Pause モードのみ）
    /// - RMB 離し: カメラ grab 解除、短押しでコンテキストメニュー通知
    pub(super) fn on_mouse_button(&mut self, button: MouseButton, state: ElementState) {
        let pressed = state == ElementState::Pressed;
        self.input.process_mouse_button(button, pressed);

        if button == MouseButton::Left {
            if pressed
                && (self.mode == RuntimeMode::Edit || self.paused)
                && !self.cam_input.rmb
            {
                self.handle_lmb_press();
            }
            if !pressed {
                self.handle_lmb_release();
            }
        }

        if button == MouseButton::Middle {
            self.cam_input.mmb = pressed;
            if self.mode == RuntimeMode::Edit || self.paused {
                if pressed {
                    // RMB が先に押されていない場合のみ MMB がカーソルを管理する
                    // （RMB 中に MMB を追加した場合はカーソルは RMB 管理のまま）
                    if !self.cam_input.rmb {
                        self.mmb_grab_screen_pos = mmb_grab_start(self.window_hwnd());
                        if let Some(window) = &self.window {
                            window.set_cursor_visible(false);
                        }
                    }
                } else {
                    // MMB 離し: カーソルを起点に戻して表示する
                    if let Some((x, y)) = self.mmb_grab_screen_pos.take() {
                        mmb_grab_end(x, y);
                        // RMB がまだ押されていなければカーソルを再表示する
                        if !self.cam_input.rmb {
                            if let Some(window) = &self.window {
                                window.set_cursor_visible(true);
                            }
                        }
                    }
                }
            }
        }

        if button == MouseButton::Right {
            self.cam_input.rmb = pressed;
            // カメラ grab は Edit / Pause モードのみ。
            // Play モードでは editor 側が ClipCursor を管理するため
            // ここで ClipCursor(null) を呼ばないようにする。
            if self.mode == RuntimeMode::Edit || self.paused {
                if let Some(window) = &self.window {
                    if pressed {
                        self.rmb_press_pos = self.last_cursor_pos;
                        self.rmb_moved     = false;
                        // MMB 押し込み中はカーソル管理を MMB に任せる（ClipCursor の二重適用を避ける）
                        if !self.cam_input.mmb {
                            self.cam_grab_screen_pos =
                                camera_grab_start(self.window_hwnd());
                            window.set_cursor_visible(false);
                        }
                        // Pause モード: DeviceEvent::MouseMotion は WS_CHILD に届かないため
                        // CursorMoved ベースのカメラ回転を使う。
                        // ウィンドウ中央をピボットとしてカーソルをワープして固定する。
                        if self.paused {
                            if let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) {
                                let pvx = ws.width  as f32 / 2.0;
                                let pvy = ws.height as f32 / 2.0;
                                self.pause_cam_pivot        = Some((pvx, pvy));
                                self.pause_cam_warp_pending = 1;
                                warp_cursor_to_local(self.window_hwnd(), pvx as i32, pvy as i32);
                            }
                        }
                    } else {
                        // MMB がまだ押されている場合はカーソル管理を MMB に任せる
                        if !self.cam_input.mmb {
                            window.set_cursor_visible(true);
                        }
                        if let Some((x, y)) = self.cam_grab_screen_pos.take() {
                            camera_grab_end(x, y);
                            // 短押し（カーソル移動なし）→ コンテキストメニュー
                            if !self.rmb_moved {
                                if let Some(ipc) = &self.ipc {
                                    ipc.send("CONTEXT_MENU");
                                }
                                // アクタ追加時のスポーン位置計算用に座標を保存する
                                self.context_menu_screen_pos = self.last_cursor_pos
                                    .map(|(x, y)| (x as u32, y as u32));
                            }
                        } else {
                            // RMB_DOWN が処理されていない（コンテキストメニューのポップアップが
                            // WM_RBUTTONDOWN を横取りした等）。ClipCursor のみ解除する。
                            release_window_clamp();
                        }
                        self.rmb_press_pos          = None;
                        self.rmb_moved              = false;
                        // Pause モードカメラ回転用ピボットをリセット
                        self.pause_cam_pivot        = None;
                        self.pause_cam_warp_pending = 0;
                    }
                }
            }
        }
    }


    // ============================================================
    //  on_mouse_wheel
    // ============================================================

    /// マウスホイール処理（スクロール量をカメラ入力に積算する）。
    pub(super) fn on_mouse_wheel(&mut self, delta: MouseScrollDelta) {
        self.input.process_scroll(&delta);
        let lines = match delta {
            MouseScrollDelta::LineDelta(_, y) => y,
            MouseScrollDelta::PixelDelta(p)   => p.y as f32 / 20.0,
        };
        self.cam_input.scroll += lines;
    }
}
