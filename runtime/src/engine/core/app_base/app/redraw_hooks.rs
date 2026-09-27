// ============================================================
//  redraw_hooks.rs — 「描く理由」の判定（engine::core::redraw）を App とイベントループへつなぐ所（W2-10a）
//
//  【受け持ち】（正典は docs/redraw_policy.md）
//    起動       … install_redraw_waker（EventLoopProxy を起こし手として登録。run_with_event_loop から）
//                 apply_redraw_settings（project_settings.json の render_policy・render_idle_frames。app_init.rs から）
//    フレーム   … on_redraw_frame_start（RedrawRequested の頭。止めていたのに OS が描かせたフレームの dt の切り詰め・起きた遅れのログ）
//                 request_next_frame（フレームの末尾の唯一の入口。理由を集めて RedrawGate に決めさせる）
//                 declare_redraw_reason（フレームの中の申告の口。アニメーション・シーンの切り替えなど）
//    起こす     … note_window_event_for_redraw（WindowEvent）・note_device_event_for_redraw（マウスの生の移動）
//                 handle_redraw_wake_signal（user_event。IPC・JNI・他のスレッドのスクリプトが EVENT_LOOP_WAKE で起こした）
//                 pump_redraw_deadline（about_to_wait。WaitUntil の予定の時刻）
//    区切り     … reset_redraw_for_play_session（Play の開始・停止）・on_redraw_foreground（背面から前面へ）
//    検証       … 環境変数 SEED_REDRAW_LOG=1 で、フレームの末尾の理由が変わるたびに [SEED REDRAW] 理由: … を出す（PC）
//
//  【既定を変えない】方針が continuous（既定）なら、フレームの末尾は今までどおり request_redraw するだけ
//  （他のスレッドが積んだ理由を捨てる原子の読み書きが 1 回増えるだけ）。止める判定を使うのは Play（エディタの PAUSE の
//  見た目でないとき）で方針が on_demand のときだけ。Edit は常に毎フレーム描く。
// ============================================================

use std::time::{Duration, Instant};

use winit::event::{DeviceEvent, WindowEvent};
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop};

use crate::engine::core::input::gesture::pointer_clock_now;
use crate::engine::core::redraw::wake::EVENT_LOOP_WAKE;
use crate::engine::core::redraw::{
    parse_redraw_settings, schedule, script_requests, NextFrame, RedrawGate, RedrawReason, RedrawReasons,
    RedrawSettings, RenderPolicy, LOG_PREFIX, RESUME_MAX_DELTA_SECS,
};
use crate::engine::core::renderer::screenshot;

use super::{model_streaming, play_diag, surface_lifecycle, App, RuntimeMode};

/// 秒 → ミリ秒（ログの表示用）。
const MILLIS_PER_SEC: f64 = 1_000.0;

/// 検証用: フレームの末尾に集めた理由が変わるたびにログへ出す環境変数（PC。`SEED_REDRAW_LOG=1`）。
const ENV_REDRAW_LOG: &str = "SEED_REDRAW_LOG";

/// `SEED_REDRAW_LOG` を有効とみなす値。
const ENV_REDRAW_LOG_ENABLED: &str = "1";

/// 理由の変化のログを出すか（プロセスの起動時に 1 度だけ決める）。
static REDRAW_REASON_LOG: std::sync::LazyLock<bool> =
    std::sync::LazyLock::new(|| std::env::var(ENV_REDRAW_LOG).as_deref() == Ok(ENV_REDRAW_LOG_ENABLED));

/// 描画の止め方の App 側の状態（App が 1 つ持つ）。
pub(super) struct RedrawState {
    /// project_settings.json の設定（起動時に 1 回読む）。
    settings: RedrawSettings,
    /// 次のフレームを描くかの判定。
    gate: RedrawGate,
    /// このフレームにイベントループのスレッドで積んだ理由（WindowEvent・フレームの中の申告）。フレームの末尾で取り出す。
    frame_reasons: RedrawReasons,
    /// ControlFlow を Wait / WaitUntil にしてあるか（continuous へ戻したときに Poll へ戻すため）。
    loop_waiting: bool,
    /// 検証用のログ（SEED_REDRAW_LOG=1）で最後に出した理由（変わったときだけ出す）。
    last_logged_reasons: Option<RedrawReasons>,
}

impl Default for RedrawState {
    fn default() -> Self {
        let settings = RedrawSettings::default();
        Self {
            gate: RedrawGate::new(settings.idle_after_frames),
            settings,
            frame_reasons: RedrawReasons::EMPTY,
            loop_waiting: false,
            last_logged_reasons: None,
        }
    }
}

/// WindowEvent を描く理由へ分ける（RedrawRequested は理由にしない＝None）。
///
/// 入力（キー・マウス・タッチ・ホイール・IME）は Input、窓の大きさ・倍率・フォーカス・遮蔽・移動・テーマは Screen、
/// それ以外（閉じる・ファイルのドロップなど）は SystemEvent。
fn redraw_reason_of_window_event(event: &WindowEvent) -> Option<RedrawReason> {
    match event {
        WindowEvent::RedrawRequested => None,
        WindowEvent::KeyboardInput { .. }
        | WindowEvent::ModifiersChanged(_)
        | WindowEvent::Ime(_)
        | WindowEvent::CursorMoved { .. }
        | WindowEvent::CursorEntered { .. }
        | WindowEvent::CursorLeft { .. }
        | WindowEvent::MouseWheel { .. }
        | WindowEvent::MouseInput { .. }
        | WindowEvent::Touch(_)
        | WindowEvent::TouchpadPressure { .. }
        | WindowEvent::AxisMotion { .. }
        | WindowEvent::PinchGesture { .. }
        | WindowEvent::PanGesture { .. }
        | WindowEvent::RotationGesture { .. }
        | WindowEvent::DoubleTapGesture { .. } => Some(RedrawReason::Input),
        WindowEvent::Resized(_)
        | WindowEvent::ScaleFactorChanged { .. }
        | WindowEvent::Focused(_)
        | WindowEvent::Occluded(_)
        | WindowEvent::Moved(_)
        | WindowEvent::ThemeChanged(_) => Some(RedrawReason::Screen),
        _ => Some(RedrawReason::SystemEvent),
    }
}

/// ログ用の「次の予定」の表示（無ければ「なし」）。
fn describe_wake_at(wake_at: Option<Instant>, now: Instant) -> String {
    match wake_at {
        Some(at) => format!("{:.1} ms 後", at.saturating_duration_since(now).as_secs_f64() * MILLIS_PER_SEC),
        None => "なし（理由が来るまで眠る）".to_string(),
    }
}

/// 長さをミリ秒で表示する（ログ用）。
fn millis(duration: Duration) -> f64 {
    duration.as_secs_f64() * MILLIS_PER_SEC
}

impl App {
    /// イベントループの EventLoopProxy を起こし手として登録する（run_app の前に 1 回。run_with_event_loop から）。
    ///
    /// 登録の後は、IPC の読み取りのスレッド・JNI の受け口が `redraw::wake::raise` で眠っているイベントループを起こせる
    /// （user_event が届く）。winit 0.30 の EventLoopProxy は Windows・Android とも Send + Sync。
    pub(super) fn install_redraw_waker(event_loop: &EventLoop<()>) {
        let proxy = event_loop.create_proxy();
        let installed = EVENT_LOOP_WAKE.install_waker(Box::new(move || {
            // イベントループが終わっていれば送れない（何もしない）
            let _ = proxy.send_event(());
        }));
        if !installed {
            eprintln!("{LOG_PREFIX}[WARN] 起こし手は既に登録されています（最初の登録のまま使います）");
        }
    }

    /// project_settings.json の描き方の設定を読む（handle_resumed で 1 回。target_fps の後）。
    pub(super) fn apply_redraw_settings(&mut self, settings_json: &str) {
        let parsed = parse_redraw_settings(settings_json);
        for warning in &parsed.warnings {
            eprintln!("{LOG_PREFIX}[WARN] {warning}");
        }
        self.redraw.settings = parsed.settings;
        self.redraw.gate = RedrawGate::new(parsed.settings.idle_after_frames);
        // スクリプトの SEED.Redraw.Policy の読み取り（上書きが無いとき）がこの値を返せるよう写す
        script_requests::publish_configured_policy(parsed.settings.policy);
        // 「fps が 0 になった」「電池」の相談で最初に見る値なので起動ログへ残す
        eprintln!(
            "[SEED INIT] render_policy={} render_idle_frames={}",
            parsed.settings.policy.as_str(),
            parsed.settings.idle_after_frames
        );
    }

    /// 今の方針（スクリプトの `SEED.Redraw.Policy` の上書き → プロジェクト設定の順）。
    fn effective_render_policy(&self) -> RenderPolicy {
        script_requests::policy_override().unwrap_or(self.redraw.settings.policy)
    }

    /// 止める判定を使ってよい実行か。
    ///
    /// Play（エディタの PAUSE でデバッグカメラの見た目にしていないとき）で、起こし手が登録済みのときだけ。
    /// Edit・エディタの PAUSE（ギズモ・カメラ操作の見た目）は常に毎フレーム描く。
    fn redraw_gate_active(&self) -> bool {
        self.mode == RuntimeMode::Play && !self.paused && EVENT_LOOP_WAKE.has_waker()
    }

    /// 描画を止めているか（ヘッドレスのフレームの駆動などが見る）。
    pub(super) fn redraw_is_idle(&self) -> bool {
        self.redraw_gate_active() && self.redraw.gate.is_idle()
    }

    /// フレームの中で描く理由を申告する（アニメーションの再生中・シーンの切り替えなど。フレームの末尾の判定が読む）。
    pub(super) fn declare_redraw_reason(&mut self, reason: RedrawReason) {
        self.redraw.frame_reasons.insert(reason);
    }

    /// RedrawRequested の頭で呼ぶ。止めていたのに描くフレーム（OS の再描画の要求・ヘッドレスの駆動）は起きた扱いにし、
    /// 起きた後の最初のフレームなら起こしてからの遅れをログへ出す。
    pub(super) fn on_redraw_frame_start(&mut self, event_loop: &ActiveEventLoop) {
        if !self.redraw_gate_active() {
            return;
        }
        let now = Instant::now();
        if self.redraw.gate.is_idle() && self.resume_redraw(event_loop, RedrawReason::Screen.into(), now) {
            // 自分で要求していないフレーム（OS の再描画の要求など）: 止めていた時間を dt に入れないよう、起きた扱いにした
            eprintln!("{LOG_PREFIX} 描画を再開します（理由: {}・要求していない再描画）", RedrawReason::Screen.name());
        }
        if let Some(record) = self.redraw.gate.take_wake_record() {
            eprintln!(
                "{LOG_PREFIX} 起こしてから最初のフレームの頭まで {:.2} ms（理由: {}・止めていた時間 {:.1} ms）",
                millis(now.saturating_duration_since(record.woke_at)),
                record.reasons,
                millis(record.idle_for),
            );
        }
    }

    /// フレームの末尾に次のフレームを要求する（frame_renderer.rs の handle_redraw_requested の末尾の唯一の入口）。
    ///
    /// continuous（既定）なら今までどおり毎フレーム request_redraw する。on_demand なら理由を集めて判定し、
    /// 理由の無いフレームが続いたら要求をやめて ControlFlow を Wait（予定があれば WaitUntil）にする。
    pub(super) fn request_next_frame(&mut self, event_loop: &ActiveEventLoop) {
        // Edit・エディタの PAUSE は方針を読むまでもなく毎フレーム描く（方針の読み取りは Mutex を 1 回取る）
        if !self.redraw_gate_active() {
            self.request_next_frame_continuous(event_loop);
            return;
        }
        let policy = self.effective_render_policy();
        if policy == RenderPolicy::Continuous {
            self.request_next_frame_continuous(event_loop);
            return;
        }
        let now = Instant::now();
        let (reasons, next_deadline) = self.collect_redraw_frame(now);
        self.log_redraw_reasons_if_changed(reasons, next_deadline, now);
        let was_idle = self.redraw.gate.is_idle();
        match self.redraw.gate.on_frame_end(policy, reasons, next_deadline, now) {
            NextFrame::Continue => self.request_redraw_now(),
            NextFrame::Idle { wake_at } => self.enter_redraw_idle(event_loop, wake_at, now, was_idle),
        }
    }

    /// 毎フレーム描く（continuous・Edit・判定を使わない実行）。止めていた状態が残っていれば捨てて Poll へ戻す。
    fn request_next_frame_continuous(&mut self, event_loop: &ActiveEventLoop) {
        // 他のスレッドが積んだ理由は使わない（溜めない）。原子の読み書き 1 回だけ
        let _ = EVENT_LOOP_WAKE.take_pending();
        self.redraw.frame_reasons = RedrawReasons::EMPTY;
        if self.redraw.gate.is_idle() || self.redraw.loop_waiting {
            // on_demand で止めた後に continuous へ戻した・Edit へ戻った
            self.redraw.gate.force_awake();
            EVENT_LOOP_WAKE.leave_idle();
            self.redraw.loop_waiting = false;
            event_loop.set_control_flow(surface_lifecycle::ACTIVE_CONTROL_FLOW);
            play_diag::set_intentionally_idle(false);
        }
        self.request_redraw_now();
    }

    /// 次のフレームを要求する（ウィンドウがあれば）。
    fn request_redraw_now(&self) {
        if let Some(window) = &self.window {
            window.request_redraw();
        }
    }

    /// このフレームの描く理由と、次の予定の時刻を集める（on_demand のフレームの末尾だけ）。
    fn collect_redraw_frame(&mut self, now: Instant) -> (RedrawReasons, Option<Instant>) {
        let mut reasons = std::mem::take(&mut self.redraw.frame_reasons);
        // 他のスレッド（IPC・JNI）とフレームの中のスクリプト（Request）が積んだ理由
        reasons = reasons.union(EVENT_LOOP_WAKE.take_pending().frame_reasons());
        // 押している間・注入の再生中・ジェスチャーの進行中は描き続ける
        reasons.insert_if(self.input.is_any_input_held(), RedrawReason::InputHeld);
        reasons.insert_if(self.input.is_injected_sequence_playing(), RedrawReason::InjectedInput);
        let gesture = self.gestures.activity();
        reasons.insert_if(gesture.is_active(), RedrawReason::Gesture);
        // スクリプトの要求（KeepAlive・常に描く・RequestAfter の時刻）
        let script = script_requests::take_frame_state(now);
        reasons = reasons.union(script.reasons);
        // 動いているもの・撮影など
        reasons.insert_if(self.redraw_motion_active(), RedrawReason::Motion);
        reasons.insert_if(self.redraw_capture_active(), RedrawReason::Capture);
        let next_deadline = schedule::earliest([
            script.next_deadline,
            gesture.next_deadline.map(|at| schedule::instant_from_clock_secs(now, pointer_clock_now(), at)),
            self.platform_timed_deadline(now),
        ]);
        (reasons, next_deadline)
    }

    /// 検証用（SEED_REDRAW_LOG=1）: フレームの末尾に集めた理由が前のフレームと変わったらログへ出す。
    fn log_redraw_reasons_if_changed(&mut self, reasons: RedrawReasons, next_deadline: Option<Instant>, now: Instant) {
        if !*REDRAW_REASON_LOG || self.redraw.last_logged_reasons == Some(reasons) {
            return;
        }
        self.redraw.last_logged_reasons = Some(reasons);
        // 「押している」が理由のときは内訳も出す（何を押している扱いかを調べるため）
        let held = if reasons.contains(RedrawReason::InputHeld) {
            format!("・押している: {}", self.input.held_input_summary())
        } else {
            String::new()
        };
        eprintln!("{LOG_PREFIX} 理由: {reasons}（次の予定: {}{held}）", describe_wake_at(next_deadline, now));
    }

    /// 動いているもの（エンジンが自分で分かるもの）があるか。
    ///
    /// アニメーション（Animator の再生中）はフレームの中で `declare_redraw_reason(Motion)` するのでここに無い。
    /// シェーダーの時間で動くもの（水面・草の風・L3 の time）は申告しない（使う画面は SEED.Redraw.SetContinuous(true)）。
    fn redraw_motion_active(&self) -> bool {
        self.particle_system.is_animating()
            || model_streaming::pending_count() > 0
            || self.play_physics_bodies_moving()
            // スクロールの慣性・跳ね返り・ScrollTo・スクリプトの位置の要求の処理待ち（W2-3）
            || self.scroll_motion_active()
    }

    /// 撮影・サムネイルの生成・プロファイラの一発計測が進行中か（フレームが要る）。
    fn redraw_capture_active(&self) -> bool {
        screenshot::has_pending_request()
            || self.thumbnail_job.is_some()
            || self.thumbnail_session.is_some()
            || crate::engine::core::profiling::dump_active()
    }

    /// SEED.Platform の時刻で起こる出来事（デスクトップの模擬の目覚まし・鳴動の安全弁）の予定を Instant へ直す。
    fn platform_timed_deadline(&self, now: Instant) -> Option<Instant> {
        let delay_ms = crate::engine::platform::bridge::next_timed_event_delay_ms()?;
        Some(schedule::instant_after_millis(now, delay_ms))
    }

    /// 眠っている間の予定の決め直し（Reschedule）で使う、次の予定の時刻（取り出さずに見るだけ）。
    fn peek_redraw_deadline(&self, now: Instant) -> Option<Instant> {
        let gesture = self.gestures.activity();
        schedule::earliest([
            script_requests::peek_next_deadline(now),
            gesture.next_deadline.map(|at| schedule::instant_from_clock_secs(now, pointer_clock_now(), at)),
            self.platform_timed_deadline(now),
        ])
    }

    /// 描画を止める（次のフレームを要求せず、ControlFlow を Wait / WaitUntil にする）。
    ///
    /// 眠る直前に他のスレッドが理由を積んでいたら（EVENT_LOOP_WAKE.enter_idle が返す）、眠らずに描く。
    fn enter_redraw_idle(&mut self, event_loop: &ActiveEventLoop, wake_at: Option<Instant>, now: Instant, was_idle: bool) {
        let late = EVENT_LOOP_WAKE.enter_idle();
        if late.draws_frame() {
            self.resume_redraw(event_loop, late, now);
            return;
        }
        // 他のスレッドのスクリプトが予定を変えていたら決め直す（描かない）
        let wake_at = if late.contains(RedrawReason::Reschedule) {
            schedule::earliest([wake_at, self.peek_redraw_deadline(now)])
        } else {
            wake_at
        };
        let _ = self.redraw.gate.reschedule(wake_at);
        self.set_idle_control_flow(event_loop, wake_at);
        // フレームが途絶えても凍結の警告（[PLAY_WD]）を出さない（意図して止めている）
        play_diag::set_intentionally_idle(true);
        if !was_idle {
            let stats = self.redraw.gate.stats();
            eprintln!(
                "{LOG_PREFIX} 描画を止めます（理由の無いフレームが {} 回。次の予定: {}・止めた回数 {}・起きた回数 {}（予定の時刻で {}））",
                self.redraw.gate.idle_after_frames(),
                describe_wake_at(wake_at, now),
                stats.idle_entries,
                stats.wakes,
                stats.timer_wakes,
            );
        }
    }

    /// 止めている間の ControlFlow（予定があれば WaitUntil、無ければ Wait）。
    fn set_idle_control_flow(&mut self, event_loop: &ActiveEventLoop, wake_at: Option<Instant>) {
        event_loop.set_control_flow(match wake_at {
            Some(at) => ControlFlow::WaitUntil(at),
            None => ControlFlow::Wait,
        });
        self.redraw.loop_waiting = true;
    }

    /// 止めていた描画を再開する（理由が来た）。
    ///
    /// # 戻り値
    /// 止めていて再開したら true。起きていれば false（理由はフレームの末尾の判定が読む）。
    fn resume_redraw(&mut self, event_loop: &ActiveEventLoop, reasons: RedrawReasons, now: Instant) -> bool {
        if !self.redraw.gate.wake(reasons, now) {
            return false;
        }
        EVENT_LOOP_WAKE.leave_idle();
        play_diag::set_intentionally_idle(false);
        if self.surface_missing() {
            // 背面（Android の suspended）: 描けないので Wait のまま。前面へ戻る resumed が描画を再開する
            return true;
        }
        // 止めていた時間をゲームの時間へ入れない（起きた最初のフレームの dt を切り詰める）
        self.clock.limit_next_delta(RESUME_MAX_DELTA_SECS);
        event_loop.set_control_flow(surface_lifecycle::ACTIVE_CONTROL_FLOW);
        self.redraw.loop_waiting = false;
        self.request_redraw_now();
        true
    }

    /// WindowEvent が来た（render.rs の window_event の頭。RedrawRequested は数えない）。止めていたら再開する。
    pub(super) fn note_window_event_for_redraw(&mut self, event: &WindowEvent, event_loop: &ActiveEventLoop) {
        let Some(reason) = redraw_reason_of_window_event(event) else {
            return;
        };
        self.note_loop_thread_reason(reason, event_loop);
    }

    /// マウスの生の移動（DeviceEvent::MouseMotion。カーソルを閉じ込めているときの視点操作）が来た。止めていたら再開する。
    ///
    /// winit の既定では DeviceEvent はウィンドウにフォーカスがあるときだけ届く。
    pub(super) fn note_device_event_for_redraw(&mut self, event: &DeviceEvent, event_loop: &ActiveEventLoop) {
        if matches!(event, DeviceEvent::MouseMotion { .. }) {
            self.note_loop_thread_reason(RedrawReason::Input, event_loop);
        }
    }

    /// イベントループのスレッドで理由が来た（フレームの末尾へ積み、止めていたら再開する）。
    fn note_loop_thread_reason(&mut self, reason: RedrawReason, event_loop: &ActiveEventLoop) {
        self.redraw.frame_reasons.insert(reason);
        if self.redraw_gate_active() && self.redraw.gate.is_idle() {
            let now = Instant::now();
            if self.resume_redraw(event_loop, reason.into(), now) {
                eprintln!("{LOG_PREFIX} 描画を再開します（理由: {}）", reason.name());
            }
        }
    }

    /// 起こしの知らせ（user_event）を受け取った: 他のスレッドが積んだ理由を読み、止めていれば再開する。
    ///
    /// 理由が「予定の変更」（Reschedule）だけなら描かずに起きる時刻を決め直す。
    pub(super) fn handle_redraw_wake_signal(&mut self, event_loop: &ActiveEventLoop) {
        EVENT_LOOP_WAKE.acknowledge_wake();
        let reasons = EVENT_LOOP_WAKE.take_pending();
        if reasons.is_empty() {
            return;
        }
        if !self.redraw_gate_active() || !self.redraw.gate.is_idle() {
            // 起きている: フレームの末尾の判定へ回す
            self.redraw.frame_reasons = self.redraw.frame_reasons.union(reasons.frame_reasons());
            return;
        }
        let now = Instant::now();
        if reasons.draws_frame() {
            if self.resume_redraw(event_loop, reasons, now) {
                eprintln!("{LOG_PREFIX} 描画を再開します（理由: {}）", reasons.frame_reasons());
            }
            return;
        }
        // 他のスレッドのスクリプトが方針を continuous へ戻した: 毎フレーム描く状態へ戻す
        if self.effective_render_policy() == RenderPolicy::Continuous {
            if self.resume_redraw(event_loop, RedrawReason::ScriptRequest.into(), now) {
                eprintln!("{LOG_PREFIX} 描画を再開します（理由: 方針が continuous へ戻った）");
            }
            return;
        }
        // 予定の変更だけ: 描かずに起きる時刻を決め直す
        let wake_at = schedule::earliest([self.redraw.gate.wake_at(), self.peek_redraw_deadline(now)]);
        if let Some(wake_at) = self.redraw.gate.reschedule(wake_at) {
            self.set_idle_control_flow(event_loop, wake_at);
        }
    }

    /// 止めている間に予定の時刻が来ていたら再開する（render.rs の about_to_wait で毎周）。
    pub(super) fn pump_redraw_deadline(&mut self, event_loop: &ActiveEventLoop) {
        if !self.redraw_gate_active() {
            return;
        }
        let now = Instant::now();
        if self.redraw.gate.deadline_due(now) && self.resume_redraw(event_loop, RedrawReason::Timer.into(), now) {
            eprintln!("{LOG_PREFIX} 描画を再開します（理由: {}・予定の時刻）", RedrawReason::Timer.name());
        }
    }

    /// Play の区切り（開始・停止）: スクリプトの要求を捨て、止めていれば起きている扱いにする
    /// （ControlFlow はフレームの末尾の continuous の経路か、次の起こしで Poll へ戻る）。
    pub(super) fn reset_redraw_for_play_session(&mut self) {
        script_requests::reset();
        self.redraw.gate.force_awake();
        self.redraw.frame_reasons.insert(RedrawReason::Lifecycle);
        EVENT_LOOP_WAKE.leave_idle();
        play_diag::set_intentionally_idle(false);
        self.request_redraw_now();
    }

    /// 背面から前面へ戻った（2 回目以降の resumed でサーフェスを作り直せたとき）: 起きている扱いにする。
    /// ControlFlow と request_redraw は surface_lifecycle.rs の handle_surface_resumed が済ませている。
    pub(super) fn on_redraw_foreground(&mut self) {
        self.redraw.gate.force_awake();
        self.redraw.frame_reasons.insert(RedrawReason::Lifecycle);
        self.redraw.loop_waiting = false;
        EVENT_LOOP_WAKE.leave_idle();
        play_diag::set_intentionally_idle(false);
    }
}

// ============================================================
//  テスト（WindowEvent の分け方）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// RedrawRequested は理由にしない。大きさ・フォーカスは Screen、閉じるは SystemEvent。
    #[test]
    fn window_events_map_to_reasons() {
        assert_eq!(redraw_reason_of_window_event(&WindowEvent::RedrawRequested), None);
        assert_eq!(
            redraw_reason_of_window_event(&WindowEvent::Resized(winit::dpi::PhysicalSize::new(1, 1))),
            Some(RedrawReason::Screen)
        );
        assert_eq!(redraw_reason_of_window_event(&WindowEvent::Focused(true)), Some(RedrawReason::Screen));
        assert_eq!(redraw_reason_of_window_event(&WindowEvent::Occluded(false)), Some(RedrawReason::Screen));
        assert_eq!(redraw_reason_of_window_event(&WindowEvent::CloseRequested), Some(RedrawReason::SystemEvent));
        assert_eq!(
            redraw_reason_of_window_event(&WindowEvent::Ime(winit::event::Ime::Enabled)),
            Some(RedrawReason::Input)
        );
    }

    /// 予定の表示（ログ）。
    #[test]
    fn wake_at_is_described() {
        let now = Instant::now();
        assert!(describe_wake_at(None, now).starts_with("なし"));
        assert_eq!(describe_wake_at(Some(now + Duration::from_millis(250)), now), "250.0 ms 後");
    }
}
