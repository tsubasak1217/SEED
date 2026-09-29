// ============================================================
//  render.rs — ApplicationHandler 実装（resumed / window_event / device_event / user_event）
//
//  winit イベントループへの応答処理の入口。
//  実際のフレームレンダリングは frame_renderer.rs の handle_redraw_requested に委譲する。
//  user_event は他のスレッドがイベントループを起こした知らせ（描く理由。W2-10a の redraw_hooks.rs）。
// ============================================================

use winit::application::ApplicationHandler;
use winit::event::{DeviceEvent, DeviceId, WindowEvent};
use winit::event_loop::ActiveEventLoop;
use winit::window::WindowId;

use super::App;

impl ApplicationHandler for App {
    /// ウィンドウ・レンダラーを初期化し、IPC へ READY を通知する。
    /// 実装本体は app_init.rs の handle_resumed に委譲する。
    ///
    /// 2 回目以降の resumed（Android でバックグラウンドから復帰したとき）は、
    /// 初期化をやり直さず描画サーフェスだけを作り直し（surface_lifecycle.rs）、
    /// 作り直せたらシミュレーションを再開する（background_lifecycle.rs）。
    /// デスクトップでは resumed は起動時の 1 回だけなので、常に初期化経路を通る。
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.renderer.is_some() {
            if self.handle_surface_resumed(event_loop) {
                self.enter_foreground();
                // 描画を止めていた状態（render_policy の on_demand）を捨て、前面へ戻った直後のフレームを描く（W2-10a）
                self.on_redraw_foreground();
            }
            return;
        }
        self.handle_resumed(event_loop);
        // 初期化前に suspended が届いていても、物理スレッドが眠ったままにならないようにする。
        self.ensure_foreground();
    }

    /// 描画サーフェスが使えなくなった（Android でバックグラウンドへ回った）ときに呼ばれる。
    /// 先にセーブ・パイプラインキャッシュを書き出してシミュレーションを止め（background_lifecycle.rs）、
    /// それからサーフェスを破棄してイベントループを待機させる（surface_lifecycle.rs）。
    /// デスクトップでは届かない。
    fn suspended(&mut self, event_loop: &ActiveEventLoop) {
        // アプリが背面へ回った: ジェスチャーの指をすべて取り消す（押下の見た目を戻す。前面へ戻った最初のフレームで配る。W2-2）。
        // タッチの状態・マウスの状態には触れない（フォーカスを失ったときの従来の安全弁のまま）。
        self.input.cancel_gesture_pointers();
        self.enter_background();
        self.handle_suspended(event_loop);
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
        // 音声フォーカス（Android の UI スレッドから JNI で届く）に合わせて音声の出力全体を止める・戻す。
        // フレームではなくイベントループの 1 周ごとに見る: ホームへ戻る途中などは RedrawRequested が止まっても
        // ループは回り続けるので、フレームで見ると手放した音声フォーカスの反映が suspended まで遅れる
        // （エミュレータで約 1 秒、音が鳴り続けた）。変わったときだけ切り替える（audio_output_sync.rs。
        // デスクトップは常に「通常」のままで、原子変数を 2 つ読むだけ）。
        self.sync_audio_output();
        self.pump_ipc_while_frames_stalled(event_loop);
        // 表示由来の再描画が来ない状況（ヘッドレス／撮影待ち）でフレームを強制的に回す。
        self.pump_frame_when_redraw_stalled(event_loop);
        // 撮影が終わっていればエディタへ SCREENSHOT_DONE / SCREENSHOT_ERROR を返す。
        self.poll_screenshot_outcomes();
        // 図鑑サムネイル生成ジョブを 1 段進める（完了時に応答を返し、状態を復帰する）。
        self.poll_thumbnail_job();
        // 描画を止めている間（render_policy の on_demand。W2-10a）、予定の時刻（WaitUntil）が来ていたら再開する。
        // 止めていなければ何もしない（redraw_hooks.rs）。
        self.pump_redraw_deadline(event_loop);
    }

    /// 他のスレッド（IPC の読み取り・Android の JNI・別スレッドのスクリプト）が EventLoopProxy で起こした（W2-10a）。
    ///
    /// 積まれた「描く理由」を読み、描画を止めていれば再開する（予定の変更だけなら起きる時刻を決め直す。redraw_hooks.rs）。
    /// 知らせ自体には中身が無い（理由は engine::core::redraw::wake の原子変数に積まれている）。
    fn user_event(&mut self, event_loop: &ActiveEventLoop, _event: ()) {
        self.handle_redraw_wake_signal(event_loop);
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
        // サーフェス・リサイズ・タッチの診断ログ（Android のみ。デスクトップでは即 return）。
        super::lifecycle_diag::observe_window_event(&event);
        // 描画を止めていたら（render_policy の on_demand。W2-10a）、入力・画面の変化などの WindowEvent で再開する。
        // 止めていなければ理由を積むだけ（redraw_hooks.rs。RedrawRequested は数えない）。
        self.note_window_event_for_redraw(&event, event_loop);

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
                    // 同じ理由で、タッチの離れが届かず指（と指0 由来の左ボタン押下）が
                    // 残り続けないよう、実タッチの指を取り消す（安全弁）。
                    self.input.cancel_touches();
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

            // 複数指のタッチ。プラットフォーム特性に応じて指0 がマウスも駆動する（event_handler.rs）。
            WindowEvent::Touch(touch) => {
                self.on_touch(touch);
            }

            // OS の明暗の設定が変わった（W2-9）: SEED.Platform の模擬が OS の設定を読み直し、変わっていれば
            // platform.ui_mode_changed を積む（SEED.UI のテーマの「端末に従う」。模擬が無ければ何もしない）。
            // 描く理由（Screen）は note_window_event_for_redraw が積む。
            WindowEvent::ThemeChanged(_) => {
                crate::engine::platform::bridge::notify_host_ui_mode_changed();
            }

            // 文字入力（IME。W2-6a）: 今の入力欄の場へ変換中の文字・確定を当てる（text_input_hooks.rs）。
            // winit は IME を許可した窓にだけ Ime を送る（許可は入力欄にフォーカスがある間だけ）。
            WindowEvent::Ime(ime) => {
                self.on_text_input_ime(&ime);
            }

            // ── メインループ ──────────────────────────────────
            WindowEvent::RedrawRequested => {
                // 描画を止めていたのに描くフレーム（OS の再描画の要求）なら起きた扱いにし、起こした直後なら遅れをログへ（W2-10a）。
                self.on_redraw_frame_start(event_loop);
                // 検証用の合成タッチ（debug.seed.touch_test。要求が無ければ何もしない）。
                self.pump_touch_test_sequence();
                self.handle_redraw_requested(event_loop);
            }

            _ => {}
        }
    }

    /// デバイスイベントを処理する（マウス移動 → カメラ入力）。
    fn device_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        _device_id: DeviceId,
        event: DeviceEvent,
    ) {
        // マウスの生の移動でも、描画を止めていれば再開する（カーソルを閉じ込めた視点操作。W2-10a）。
        self.note_device_event_for_redraw(&event, event_loop);
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

