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

use crate::engine::core::app_base::ipc::ToolMode;
use crate::engine::methods::drawer::IdBuffer;

use super::{
    App, RuntimeMode, camera_grab_end, camera_grab_start, mmb_grab_end, mmb_grab_start,
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
        if let Some(r) = &mut self.renderer {
            r.resize(effective_size);
        }
        self.camera
            .set_aspect_ratio(effective_size.width, effective_size.height);
        if effective_size.width > 0 && effective_size.height > 0 {
            if let Some(dc) = &self.draw_ctx {
                self.id_buffer = Some(IdBuffer::new(
                    &dc.device,
                    effective_size.width,
                    effective_size.height,
                ));
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

            // 修飾キーの押下状態は、モーダルへ渡す前に必ず更新する
            // （モーダル中は他のキーを飲み込むため、ここで漏らすと Shift が効かない）。
            match key {
                KeyCode::ControlLeft | KeyCode::ControlRight => self.ctrl_held = pressed,
                KeyCode::ShiftLeft | KeyCode::ShiftRight => self.shift_held = pressed,
                _ => {}
            }

            // ── ロジック配置モード ──────────────────────────────────
            // Esc で取消。それ以外のキーはモード中すべて飲み込む
            //（Ctrl+Z 等が中途半端な状態で走らないようにする。モーダルと同じ方針）。
            if self.placement_mode_active() {
                if pressed && key == KeyCode::Escape {
                    self.cancel_placement();
                }
                return;
            }

            // ── モーダルトランスフォーム（Blender 風 G/R/S）─────────────
            // 開始キーと、モーダル中の全キーをここで消費する。
            // Ctrl 併用時は既存ショートカット（Ctrl+Z/Y 等）を優先するため通さない。
            if pressed && !self.ctrl_held && self.handle_modal_transform_key(key) {
                return;
            }
            // モーダル中はキーリリースも含めて以降の処理を行わない
            if self.modal_transform_active() {
                return;
            }

            // ── ツール切り替えホットキー ────────────────────────────────
            // Q=選択 / W=移動 / E=回転 / T=拡縮。
            // 拡縮が R ではなく T なのは、R をモーダル回転に明け渡したため。
            if pressed && !self.ctrl_held {
                if let Some(tool) = match key {
                    KeyCode::KeyQ => Some(ToolMode::Select),
                    KeyCode::KeyW => Some(ToolMode::Move),
                    KeyCode::KeyE => Some(ToolMode::Rotate),
                    KeyCode::KeyT => Some(ToolMode::Scale),
                    _ => None,
                } {
                    if self.mode == RuntimeMode::Edit || self.paused {
                        self.set_tool_mode_from_hotkey(tool);
                        return;
                    }
                }
            }

            match key {
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
                    } else {
                        None
                    };
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
                    } else {
                        None
                    };
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
    //  set_tool_mode_from_hotkey
    // ============================================================

    /// ホットキー（Q/W/E/T）でツールモードを切り替え、エディタへ通知する。
    ///
    /// ランタイム側だけで切り替えるとツールバーのラジオボタンがずれるため、
    /// `TOOL_MODE:<名前>` を送ってエディタの表示を同期させる。
    ///
    /// 【無視する条件】
    /// - RMB でのカメラ操作中（Q/W/E は上下・前後移動キーを兼ねるため）
    /// - モーダルトランスフォーム進行中（モーダルはツール切替と排他）
    pub(super) fn set_tool_mode_from_hotkey(&mut self, tool: ToolMode) {
        if self.cam_input.rmb || self.modal_transform_active() {
            return;
        }
        if !(self.mode == RuntimeMode::Edit || self.paused) {
            return;
        }
        if self.tool_mode == tool {
            return;
        }
        self.tool_mode = tool;
        let name = match tool {
            ToolMode::Select => "SELECT",
            ToolMode::Move => "MOVE",
            ToolMode::Rotate => "ROTATE",
            ToolMode::Scale => "SCALE",
        };
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("TOOL_MODE:{name}"));
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

        // ── ロジック配置モード中の排他処理 ──────────────────────────
        // 左クリックで確定 / 右クリックで取消。それ以外のボタンは無視する。
        //
        // **右クリックをカメラ grab より優先する**のがここの要点。
        // 取消は「置くのをやめる」という最も頻度の高い操作なので、
        // カメラ回転（RMB ドラッグ）と衝突する場合は取消を採る。
        // モード中に視点を変えたいときは、いったん取り消してから動かす。
        //
        // 左ボタンは**押下と解放の両方**を見る。円形パターンは
        // 「押下で中心固定 → ドラッグで半径調整 → 解放で確定」に使うため、
        // 押した時点では確定しない（押してすぐ離せばクリック扱いで従来どおり）。
        if self.placement_mode_active() {
            match (button, pressed) {
                (MouseButton::Left,  true)  => self.on_placement_left_press(),
                (MouseButton::Left,  false) => self.on_placement_left_release(),
                (MouseButton::Right, true)  => self.cancel_placement(),
                _ => {}
            }
            return;
        }

        // ── モーダルトランスフォーム中の排他処理 ────────────────────
        // 左クリックで確定 / 右クリックで取消。それ以外のボタンは無視する。
        // 通常の選択クリック・ギズモドラッグ・カメラ grab へは一切流さない。
        if self.modal_transform_active() {
            if pressed {
                match button {
                    MouseButton::Left => self.confirm_modal_transform(),
                    MouseButton::Right => self.cancel_modal_transform(),
                    _ => {}
                }
            }
            return;
        }

        if button == MouseButton::Left {
            if pressed && (self.mode == RuntimeMode::Edit || self.paused) && !self.cam_input.rmb {
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
                        self.rmb_moved = false;
                        // MMB 押し込み中はカーソル管理を MMB に任せる（ClipCursor の二重適用を避ける）
                        if !self.cam_input.mmb {
                            self.cam_grab_screen_pos = camera_grab_start(self.window_hwnd());
                            window.set_cursor_visible(false);
                        }
                        // Pause モード: DeviceEvent::MouseMotion は WS_CHILD に届かないため
                        // CursorMoved ベースのカメラ回転を使う。
                        // ウィンドウ中央をピボットとしてカーソルをワープして固定する。
                        if self.paused {
                            if let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) {
                                let pvx = ws.width as f32 / 2.0;
                                let pvy = ws.height as f32 / 2.0;
                                self.pause_cam_pivot = Some((pvx, pvy));
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
                                self.context_menu_screen_pos =
                                    self.last_cursor_pos.map(|(x, y)| (x as u32, y as u32));
                            }
                        } else {
                            // RMB_DOWN が処理されていない（コンテキストメニューのポップアップが
                            // WM_RBUTTONDOWN を横取りした等）。ClipCursor のみ解除する。
                            release_window_clamp();
                        }
                        self.rmb_press_pos = None;
                        self.rmb_moved = false;
                        // Pause モードカメラ回転用ピボットをリセット
                        self.pause_cam_pivot = None;
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
        // モーダル中はカメラ操作を無効化する（ズームでピボット投影がずれるのを防ぐ）。
        // ロジック配置モードも同様に止める（モード中は視点を固定する方針）。
        if self.modal_transform_active() || self.placement_mode_active() {
            return;
        }
        let lines = match delta {
            MouseScrollDelta::LineDelta(_, y) => y,
            MouseScrollDelta::PixelDelta(p) => p.y as f32 / 20.0,
        };
        self.cam_input.scroll += lines;
    }
}
