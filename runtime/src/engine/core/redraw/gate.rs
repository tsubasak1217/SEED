// ============================================================
//  redraw/gate.rs — 次のフレームを描くかの判定（W2-10a。W2-0 の試作 ui_spike/idle_redraw.rs を本番にしたもの）
//
//  【判定】（フレームの末尾で 1 回。on_frame_end）
//    - 方針が continuous                          → 描く（今までどおり。数えもしない）
//    - このフレームに描く理由がある               → 描く（理由の無いフレームの数を 0 に戻す）
//    - 予定の時刻を過ぎている                     → 描く（次のフレームで時刻の出来事を処理する）
//    - 理由の無いフレームが idle_after_frames 回続いた → 止める（Idle。予定があれば wake_at に入れる）
//  止めている間:
//    - 描く理由が来た（入力・IPC・JNI・スクリプト）→ wake が true を返す（呼び出し側が request_redraw・Poll・dt の切り詰め）
//    - 予定の時刻が来た                           → deadline_due が true（呼び出し側が Timer の理由で wake する）
//    - 予定だけが変わった（Reschedule）           → reschedule で起きる時刻を差し替える（描かない）
//
//  wgpu・winit に触れない純粋な状態機械（時刻は引数で受ける）なので、判定の表を単体テストで確かめられる。
//  App への組み込みは app/redraw_hooks.rs。
// ============================================================

use std::time::{Duration, Instant};

use super::policy::{RenderPolicy, MIN_IDLE_AFTER_FRAMES};
use super::reason::{RedrawReason, RedrawReasons};

/// フレームの末尾に決めた「次のフレーム」の扱い。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NextFrame {
    /// 次のフレームを要求する（ControlFlow は Poll のまま）。
    Continue,
    /// 次のフレームを要求しない。`wake_at` が Some ならその時刻に起きる（ControlFlow::WaitUntil）、
    /// None なら理由が来るまで眠る（ControlFlow::Wait）。
    Idle { wake_at: Option<Instant> },
}

/// 止めた・起きた回数（ログ・計測・試験用）。
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct RedrawStats {
    /// 止めた回数。
    pub idle_entries: u64,
    /// 描く理由で起きた回数（予定の時刻で起きた分を含む）。
    pub wakes: u64,
    /// そのうち予定の時刻で起きた回数。
    pub timer_wakes: u64,
}

/// 起きたときの記録（起きた後の最初のフレームの頭で取り出し、遅れと止めていた時間をログへ出す）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct WakeRecord {
    /// 起こした理由。
    pub reasons: RedrawReasons,
    /// 起こした時刻。
    pub woke_at: Instant,
    /// 止めていた時間（止めた時刻から起こした時刻まで）。
    pub idle_for: Duration,
}

/// 次のフレームを描くかの判定（1 つのウィンドウに 1 つ）。
#[derive(Debug, Clone)]
pub struct RedrawGate {
    /// 理由の無いフレームがこの数だけ続いたら止める（1 以上）。
    idle_after_frames: u32,
    /// 最後に理由があってから描いた、理由の無いフレームの数。
    quiet_frames: u32,
    /// 止めているか（次のフレームを要求していないか）。
    idle: bool,
    /// 止めた時刻（止めていた時間のログ用）。
    idle_since: Option<Instant>,
    /// 止めている間に起きる予定の時刻（WaitUntil）。
    wake_at: Option<Instant>,
    /// 起きたときの記録（起きた後の最初のフレームの頭で 1 度だけ取り出す）。
    last_wake: Option<WakeRecord>,
    /// 数。
    stats: RedrawStats,
}

impl RedrawGate {
    /// 判定を作る（起きている状態から始める）。
    ///
    /// # 引数
    /// * `idle_after_frames` - 理由の無いフレームがこの数だけ続いたら止める（1 未満は 1 にする）
    pub fn new(idle_after_frames: u32) -> Self {
        Self {
            idle_after_frames: idle_after_frames.max(MIN_IDLE_AFTER_FRAMES),
            quiet_frames: 0,
            idle: false,
            idle_since: None,
            wake_at: None,
            last_wake: None,
            stats: RedrawStats::default(),
        }
    }

    /// 止めるまでのフレームの数。
    pub fn idle_after_frames(&self) -> u32 {
        self.idle_after_frames
    }

    /// 止めているか。
    pub fn is_idle(&self) -> bool {
        self.idle
    }

    /// 止めている間に起きる予定の時刻。
    pub fn wake_at(&self) -> Option<Instant> {
        self.wake_at
    }

    /// 数。
    pub fn stats(&self) -> RedrawStats {
        self.stats
    }

    /// フレームの末尾に、次のフレームを要求するかを決める。
    ///
    /// # 引数
    /// * `policy`        - 今の方針（スクリプトの上書きを当てた後）
    /// * `reasons`       - このフレームに集めた理由（`Reschedule` は数えない）
    /// * `next_deadline` - 次の予定の時刻（ジェスチャー・模擬の目覚まし・スクリプトの RequestAfter のいちばん早いもの）
    /// * `now`           - フレームの末尾の時刻
    pub fn on_frame_end(
        &mut self,
        policy: RenderPolicy,
        reasons: RedrawReasons,
        next_deadline: Option<Instant>,
        now: Instant,
    ) -> NextFrame {
        if policy == RenderPolicy::Continuous {
            // 今までどおり毎フレーム描く（途中で continuous へ戻したときは起きている状態へ）
            self.mark_awake();
            return NextFrame::Continue;
        }
        let deadline_passed = next_deadline.is_some_and(|at| at <= now);
        if reasons.draws_frame() || deadline_passed {
            self.quiet_frames = 0;
            self.mark_awake();
            return NextFrame::Continue;
        }
        self.quiet_frames = self.quiet_frames.saturating_add(1);
        if self.quiet_frames < self.idle_after_frames {
            return NextFrame::Continue;
        }
        if !self.idle {
            self.idle = true;
            self.idle_since = Some(now);
            self.stats.idle_entries += 1;
        }
        self.wake_at = next_deadline;
        NextFrame::Idle { wake_at: next_deadline }
    }

    /// 描く理由が来た（入力・IPC・JNI・スクリプト・予定の時刻）。
    ///
    /// # 引数
    /// * `reasons` - 来た理由（描く理由を含まなければ何もしない）
    /// * `now`     - 今の時刻
    ///
    /// # 戻り値
    /// 止めていたら true（呼び出し側は request_redraw・ControlFlow を Poll・起きた最初のフレームの dt の切り詰め）。
    /// 起きていれば false（次のフレームは要求済み。理由はフレームの末尾の判定が読む）。
    pub fn wake(&mut self, reasons: RedrawReasons, now: Instant) -> bool {
        if !self.idle || !reasons.draws_frame() {
            return false;
        }
        let idle_for = self.idle_since.map_or(Duration::ZERO, |since| now.saturating_duration_since(since));
        self.last_wake = Some(WakeRecord { reasons: reasons.frame_reasons(), woke_at: now, idle_for });
        self.stats.wakes += 1;
        if reasons.contains(RedrawReason::Timer) {
            self.stats.timer_wakes += 1;
        }
        self.quiet_frames = 0;
        self.mark_awake();
        true
    }

    /// 止めている間に予定だけが変わった（描かない）。新しい起きる時刻を覚える。
    ///
    /// # 戻り値
    /// 止めていれば新しい起きる時刻（呼び出し側は ControlFlow を WaitUntil / Wait に差し替える）。起きていれば None。
    pub fn reschedule(&mut self, next_deadline: Option<Instant>) -> Option<Option<Instant>> {
        if !self.idle {
            return None;
        }
        self.wake_at = next_deadline;
        Some(next_deadline)
    }

    /// 止めている間に、予定の時刻が来たか（イベントループの 1 周ごとに見る）。
    pub fn deadline_due(&self, now: Instant) -> bool {
        self.idle && self.wake_at.is_some_and(|at| at <= now)
    }

    /// 起きた後の最初のフレームの頭で、起きたときの記録を 1 度だけ取り出す。
    pub fn take_wake_record(&mut self) -> Option<WakeRecord> {
        self.last_wake.take()
    }

    /// 止めていた状態を捨てて起きている扱いにする（前面へ戻った・Play の区切り・方針を continuous へ戻した）。
    pub fn force_awake(&mut self) {
        self.quiet_frames = 0;
        self.mark_awake();
    }

    /// 起きている状態の印（止めた時刻と予定を捨てる）。
    fn mark_awake(&mut self) {
        self.idle = false;
        self.idle_since = None;
        self.wake_at = None;
    }
}

// ============================================================
//  テスト（判定の表）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 止めるまでのフレームの数（試験用）。
    const N: u32 = 3;

    fn ms(n: u64) -> Duration {
        Duration::from_millis(n)
    }

    /// continuous は理由が無くても常に描き、止めない（既定のゲームの動き）。
    #[test]
    fn continuous_always_draws() {
        let mut gate = RedrawGate::new(N);
        let now = Instant::now();
        for _ in 0..1_000 {
            assert_eq!(gate.on_frame_end(RenderPolicy::Continuous, RedrawReasons::EMPTY, None, now), NextFrame::Continue);
        }
        assert!(!gate.is_idle());
        assert_eq!(gate.stats(), RedrawStats::default());
        assert!(!gate.wake(RedrawReason::Input.into(), now), "起きているので起こさない");
    }

    /// on_demand: 理由の無いフレームが N 回続いたら止める（N-1 回目までは描く）。
    #[test]
    fn on_demand_stops_after_n_quiet_frames() {
        let mut gate = RedrawGate::new(N);
        let now = Instant::now();
        for _ in 0..N - 1 {
            assert_eq!(gate.on_frame_end(RenderPolicy::OnDemand, RedrawReasons::EMPTY, None, now), NextFrame::Continue);
        }
        assert_eq!(
            gate.on_frame_end(RenderPolicy::OnDemand, RedrawReasons::EMPTY, None, now),
            NextFrame::Idle { wake_at: None }
        );
        assert!(gate.is_idle());
        assert_eq!(gate.stats().idle_entries, 1);
    }

    /// 理由ごとに描く・描かない（Reschedule だけは描く理由に数えない）。
    #[test]
    fn each_reason_keeps_drawing_except_reschedule() {
        let now = Instant::now();
        for reason in crate::engine::core::redraw::reason::ALL_REDRAW_REASONS {
            let mut gate = RedrawGate::new(1);
            let decision = gate.on_frame_end(RenderPolicy::OnDemand, reason.into(), None, now);
            if reason == RedrawReason::Reschedule {
                assert_eq!(decision, NextFrame::Idle { wake_at: None }, "{reason:?} は描かない");
            } else {
                assert_eq!(decision, NextFrame::Continue, "{reason:?} は描く");
            }
        }
    }

    /// 理由のあるフレームで数え直す（止めるのは最後の理由から N フレーム後）。
    #[test]
    fn reason_resets_the_quiet_count() {
        let mut gate = RedrawGate::new(N);
        let now = Instant::now();
        gate.on_frame_end(RenderPolicy::OnDemand, RedrawReasons::EMPTY, None, now);
        gate.on_frame_end(RenderPolicy::OnDemand, RedrawReasons::EMPTY, None, now);
        gate.on_frame_end(RenderPolicy::OnDemand, RedrawReason::Motion.into(), None, now);
        for _ in 0..N - 1 {
            assert_eq!(gate.on_frame_end(RenderPolicy::OnDemand, RedrawReasons::EMPTY, None, now), NextFrame::Continue);
        }
        assert!(matches!(gate.on_frame_end(RenderPolicy::OnDemand, RedrawReasons::EMPTY, None, now), NextFrame::Idle { .. }));
    }

    /// 予定があれば WaitUntil（止めた後の起きる時刻）、過ぎた予定は描く。
    #[test]
    fn deadline_becomes_wait_until_and_past_deadline_draws() {
        let t0 = Instant::now();
        let mut gate = RedrawGate::new(1);
        let at = t0 + ms(250);
        assert_eq!(gate.on_frame_end(RenderPolicy::OnDemand, RedrawReasons::EMPTY, Some(at), t0), NextFrame::Idle { wake_at: Some(at) });
        assert_eq!(gate.wake_at(), Some(at));
        assert!(!gate.deadline_due(t0 + ms(249)));
        assert!(gate.deadline_due(at));
        // 時刻で起こす（呼び出し側は Timer の理由で wake する）
        assert!(gate.wake(RedrawReason::Timer.into(), at));
        assert_eq!(gate.stats().timer_wakes, 1);
        // 過ぎた予定を持ったままのフレームの末尾は描く
        let mut gate = RedrawGate::new(1);
        assert_eq!(gate.on_frame_end(RenderPolicy::OnDemand, RedrawReasons::EMPTY, Some(t0), t0 + ms(1)), NextFrame::Continue);
    }

    /// 止めている間の理由で起き、止めていた時間と理由を 1 度だけ取り出せる。起きた後はまた N フレームで止まる。
    #[test]
    fn wake_records_reason_and_idle_time() {
        let t0 = Instant::now();
        let mut gate = RedrawGate::new(2);
        gate.on_frame_end(RenderPolicy::OnDemand, RedrawReasons::EMPTY, None, t0);
        assert!(matches!(gate.on_frame_end(RenderPolicy::OnDemand, RedrawReasons::EMPTY, None, t0), NextFrame::Idle { .. }));
        assert!(!gate.wake(RedrawReason::Reschedule.into(), t0 + ms(5)), "Reschedule では起きない");
        assert!(gate.is_idle());
        assert!(gate.wake(RedrawReasons::only(RedrawReason::Ipc).union(RedrawReason::Reschedule.into()), t0 + ms(40)));
        let record = gate.take_wake_record().expect("記録がある");
        assert_eq!(record.reasons, RedrawReasons::only(RedrawReason::Ipc), "Reschedule は記録しない");
        assert_eq!(record.idle_for, ms(40));
        assert_eq!(gate.take_wake_record(), None, "1 度だけ");
        assert!(!gate.wake(RedrawReason::Ipc.into(), t0 + ms(41)), "起きているので false");
        assert_eq!(gate.on_frame_end(RenderPolicy::OnDemand, RedrawReasons::EMPTY, None, t0), NextFrame::Continue);
        assert!(matches!(gate.on_frame_end(RenderPolicy::OnDemand, RedrawReasons::EMPTY, None, t0), NextFrame::Idle { .. }));
        assert_eq!(gate.stats().wakes, 1);
        assert_eq!(gate.stats().idle_entries, 2);
    }

    /// 止めている間の予定の差し替え（描かずに起きる時刻だけ変える）。起きていれば何もしない。
    #[test]
    fn reschedule_only_changes_wake_time_while_idle() {
        let t0 = Instant::now();
        let mut gate = RedrawGate::new(1);
        assert_eq!(gate.reschedule(Some(t0)), None, "起きている間は何もしない");
        gate.on_frame_end(RenderPolicy::OnDemand, RedrawReasons::EMPTY, None, t0);
        assert_eq!(gate.reschedule(Some(t0 + ms(100))), Some(Some(t0 + ms(100))));
        assert_eq!(gate.wake_at(), Some(t0 + ms(100)));
        assert!(gate.is_idle(), "予定の差し替えでは起きない");
        assert_eq!(gate.reschedule(None), Some(None));
        assert!(!gate.deadline_due(t0 + ms(1_000)), "予定が無ければ時刻では起きない");
    }

    /// 止めた後に continuous へ戻したら、次のフレームの末尾で起きている状態へ戻る。
    #[test]
    fn switching_back_to_continuous_resets() {
        let now = Instant::now();
        let mut gate = RedrawGate::new(1);
        gate.on_frame_end(RenderPolicy::OnDemand, RedrawReasons::EMPTY, None, now);
        assert!(gate.is_idle());
        assert_eq!(gate.on_frame_end(RenderPolicy::Continuous, RedrawReasons::EMPTY, None, now), NextFrame::Continue);
        assert!(!gate.is_idle());
        gate.on_frame_end(RenderPolicy::OnDemand, RedrawReasons::EMPTY, None, now);
        gate.force_awake();
        assert!(!gate.is_idle());
        assert_eq!(gate.wake_at(), None);
    }

    /// 止めるまでのフレームの数は 1 未満にならない。
    #[test]
    fn idle_after_frames_has_a_floor() {
        assert_eq!(RedrawGate::new(0).idle_after_frames(), MIN_IDLE_AFTER_FRAMES);
    }
}
