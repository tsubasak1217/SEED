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
    App, RuntimeMode, camera_grab_end, camera_grab_start,
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
                    // 最初に押されたカメラ操作ボタンだけが ClipCursor を張る。
                    // 既に張ってあるなら（RMB が先・または連打）二重適用しない。
                    self.begin_camera_grab();
                } else if !self.cam_input.rmb {
                    // 中も右も離れた＝カメラ操作の終了。閉じ込め解除と座標復元を行う。
                    self.end_camera_grab();
                }
            }
        }

        if button == MouseButton::Right {
            self.cam_input.rmb = pressed;
            // カメラ grab は Edit / Pause モードのみ。
            // Play モードでは editor 側が ClipCursor を管理するため
            // ここで ClipCursor(null) を呼ばないようにする。
            if self.mode == RuntimeMode::Edit || self.paused {
                // ウィンドウが生きているときだけ grab を触る
                // （カーソルの表示/非表示は末尾の sync_camera_cursor_visibility が一括で行う）。
                if self.window.is_some() {
                    if pressed {
                        self.rmb_press_pos = self.last_cursor_pos;
                        self.rmb_moved = false;
                        // 最初に押されたカメラ操作ボタンだけが ClipCursor を張る
                        // （MMB が先に押されていれば既に張られている＝二重適用しない）。
                        self.begin_camera_grab();
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
                        // MMB がまだ押されている間はカメラ操作が継続しているので、
                        // 閉じ込めも座標復元もしない（全ボタンを離した時点で一括して行う）。
                        let all_released = !self.cam_input.mmb;
                        // コンテキストメニューは「右ボタン単独の短押し」だけに出す。
                        // 中ボタン併用（オービット）からの右解放でメニューが出ると
                        // 視点操作の途中で操作が奪われるため、単独時に限定する。
                        let short_click = all_released && !self.rmb_moved;
                        if all_released {
                            self.end_camera_grab();
                        }
                        if short_click {
                            if let Some(ipc) = &self.ipc {
                                ipc.send("CONTEXT_MENU");
                            }
                            // アクタ追加時のスポーン位置計算用に座標を保存する
                            self.context_menu_screen_pos =
                                self.last_cursor_pos.map(|(x, y)| (x as u32, y as u32));
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

        // ── オービット（中＋右の同時押し）の状態機械へ流す ─────────────
        //
        // 上の 2 ブロックで `cam_input.mmb` / `cam_input.rmb` を更新し終えた**後**に呼ぶ。
        // 同時押しの成立判定はこの 2 つの現在値だけで決まるので、
        // 押下順（中→右 / 右→中）に関わらず同じ 1 か所で扱える。
        // 配置モード・モーダル中はこの行まで到達しない（上で return 済み）。
        if matches!(button, MouseButton::Middle | MouseButton::Right) {
            self.update_orbit_on_button(button, pressed);
        }

        // カーソル可視状態の調停（唯一の適用点）。
        //
        // 上のブロックで `cam_input.mmb` / `cam_input.rmb` を更新し終えた**後**に、
        // ボタンの現在状態だけから可視状態を導いて 1 回だけ反映する。
        // 「どのボタンが隠す担当か」を持たないので、押下順・解放順のどの組合せでも
        // 全ボタンを離せば必ず表示に戻る（中＋右のカーソル消失バグの根治）。
        self.sync_camera_cursor_visibility();
    }

    // ============================================================
    //  カメラ操作のカーソル管理（中／右で共有する単一の入口・出口）
    // ============================================================

    /// カメラ操作の開始。まだ確保していなければカーソルをウィンドウ内へ閉じ込め、
    /// 開始時のスクリーン座標を覚える。すでに確保済みなら何もしない（冪等）。
    ///
    /// 中・右のどちらから始まっても同じこの関数を通るため、ClipCursor の
    /// 二重適用も、担当ボタンの取り違えも起こらない。
    pub(super) fn begin_camera_grab(&mut self) {
        if self.cam_grab_screen_pos.is_some() {
            return;
        }
        self.cam_grab_screen_pos = camera_grab_start(self.window_hwnd());
    }

    /// カメラ操作の終了。閉じ込めを解除し、開始時のスクリーン座標へカーソルを戻す。
    ///
    /// **中・右の両方が離れたときにだけ**呼ぶこと。開始座標を持っていない場合
    /// （押下イベントを他ウィンドウに横取りされた等）でも、閉じ込めだけは必ず解除する。
    pub(super) fn end_camera_grab(&mut self) {
        match self.cam_grab_screen_pos.take() {
            Some((x, y)) => camera_grab_end(x, y),
            // 開始座標が無い＝復元先が不明。ClipCursor だけは確実に解除する。
            None => release_window_clamp(),
        }
    }

    /// 現在のボタン状態からカーソルの可視状態を決め、変化したときだけ OS へ反映する。
    ///
    /// Edit / Pause 以外（Play）ではカーソル管理をエディタ側に委ねるため
    /// `enabled=false` となり、隠したままモードが変わっても表示へ収束する。
    pub(super) fn sync_camera_cursor_visibility(&mut self) {
        let enabled = self.mode == RuntimeMode::Edit || self.paused;
        let (mmb, rmb) = (self.cam_input.mmb, self.cam_input.rmb);
        if let Some(visible) = self.camera_cursor.reconcile(enabled, mmb, rmb) {
            if let Some(window) = &self.window {
                window.set_cursor_visible(visible);
            }
        }
    }

    /// フォーカス喪失時などにカーソルを強制的に表示へ戻す。
    ///
    /// ウィンドウがフォーカスを失うとボタンの解放イベントが届かないことがあり、
    /// その場合 `sync_camera_cursor_visibility` の入力（押下状態）が更新されない。
    /// 「隠れたまま操作不能」を避ける最後の砦として、押下状態を無視して復帰させる。
    pub(super) fn force_restore_camera_cursor(&mut self) {
        if let Some(visible) = self.camera_cursor.force_show() {
            if let Some(window) = &self.window {
                window.set_cursor_visible(visible);
            }
        }
        // 押下状態も落として、次の押下から正しくやり直せるようにする。
        self.cam_input.mmb = false;
        self.cam_input.rmb = false;
        self.end_camera_grab();
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
