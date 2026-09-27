// ============================================================
//  ui_spike/idle_redraw.rs — 「描かなくてよいときは描かない」の判定（W2-0 の試作。X-2・W2-10 の前提）
//
//  【今の描き方】（surface_lifecycle.rs・frame_renderer.rs の末尾）
//  イベントループは ControlFlow::Poll で、毎フレームの末尾に request_redraw して次のフレームを要求し続ける。
//  前面にいる限り、画面が止まっていても target_fps（既定 60）で描き続ける。
//
//  【試作の判定】（このファイル。wgpu・winit に触れない純粋な状態機械なので単体テストできる）
//    - 「描く理由」（入力などの WindowEvent）が来たら数え直す（note_activity）
//    - 理由の無いフレームが idle_after_frames 回続いたら、次のフレームを要求しない（on_frame_end → Idle）
//      呼び出し側は ControlFlow を Wait（入力まで眠る）か WaitUntil（wake_interval ごとに 1 フレーム）にする
//    - 眠っている間に「描く理由」が来たら、呼び出し側は request_redraw して Poll へ戻す（note_activity が true）
//    - WaitUntil の時刻が来たら 1 フレームだけ描き、また眠る（wake_due）
//  「描く理由」を何にするか（アニメーション・スクロールの慣性・スクリプトの要求・プラットフォームのイベント）は
//  W2-10 で決める（docs/app_platform_roadmap.md §3.8）。試作では WindowEvent（入力・大きさ・フォーカス）だけを数える。
// ============================================================

use std::time::{Duration, Instant};

/// フレームの終わりに決めた「次のフレーム」の扱い。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NextFrame {
    /// 従来どおり次のフレームを要求する（ControlFlow は Poll のまま）。
    Continue,
    /// 次のフレームを要求しない。`wake_at` が Some ならその時刻に 1 フレームだけ描くために起きる
    /// （ControlFlow::WaitUntil）。None なら入力が来るまで眠る（ControlFlow::Wait）。
    Idle { wake_at: Option<Instant> },
}

/// 眠りに入った・起きた回数などの数（ログ・計測用）。
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct IdleRedrawStats {
    /// 眠りに入った回数。
    pub idle_entries: u64,
    /// 入力などの「描く理由」で起きた回数。
    pub activity_wakes: u64,
    /// 時刻（wake_interval）で 1 フレームだけ描いた回数。
    pub timer_wakes: u64,
}

/// 「描かなくてよいときは描かない」の判定（1 つのウィンドウに 1 つ）。
#[derive(Debug, Clone)]
pub struct IdleRedrawGate {
    /// 理由の無いフレームがこの数だけ続いたら眠る（None = 無効＝従来どおり毎フレーム描く）。
    idle_after_frames: Option<u32>,
    /// 眠っている間に 1 フレームだけ描く間隔（None = 入力が来るまで起きない）。
    wake_interval: Option<Duration>,
    /// 最後の「描く理由」から描いたフレーム数。
    frames_since_activity: u32,
    /// 眠っているか（次のフレームを要求していないか）。
    idle: bool,
    /// 次に 1 フレームだけ描くために起きる時刻（眠っていて wake_interval があるときだけ Some）。
    wake_at: Option<Instant>,
    /// 入力で起こした時刻（起きた後の最初のフレームの頭で取り出して、起きるまでの遅れを測る）。
    woke_by_activity_at: Option<Instant>,
    /// 数（ログ・計測用）。
    stats: IdleRedrawStats,
}

impl IdleRedrawGate {
    /// 判定を作る。
    ///
    /// # 引数
    /// * `idle_after_frames` - 理由の無いフレームがこの数だけ続いたら眠る（None = 無効）
    /// * `wake_interval`     - 眠っている間に 1 フレームだけ描く間隔（None = 入力まで起きない）
    pub fn new(idle_after_frames: Option<u32>, wake_interval: Option<Duration>) -> Self {
        Self {
            idle_after_frames,
            wake_interval,
            frames_since_activity: 0,
            idle: false,
            wake_at: None,
            woke_by_activity_at: None,
            stats: IdleRedrawStats::default(),
        }
    }

    /// 無効な判定（従来どおり毎フレーム描く）。
    pub fn disabled() -> Self {
        Self::new(None, None)
    }

    /// 判定が有効か（無効なら呼び出し側は従来の経路だけを通る）。
    pub fn is_enabled(&self) -> bool {
        self.idle_after_frames.is_some()
    }

    /// 眠っているか（次のフレームを要求していないか）。
    pub fn is_idle(&self) -> bool {
        self.idle
    }

    /// 数（ログ・計測用）。
    pub fn stats(&self) -> IdleRedrawStats {
        self.stats
    }

    /// 「描く理由」（入力など）があった。
    ///
    /// # 引数
    /// * `now` - 今の時刻（起こしたときだけ控え、最初のフレームまでの遅れの計測に使う）
    ///
    /// # 戻り値
    /// 眠っていたら true（呼び出し側は request_redraw して ControlFlow を Poll へ戻す）。
    /// 起きていれば false（次のフレームは既に要求済み）。
    pub fn note_activity(&mut self, now: Instant) -> bool {
        if !self.is_enabled() {
            return false;
        }
        self.frames_since_activity = 0;
        if !self.idle {
            return false;
        }
        self.idle = false;
        self.wake_at = None;
        self.woke_by_activity_at = Some(now);
        self.stats.activity_wakes += 1;
        true
    }

    /// 入力で起こしてから、このフレームの頭までの遅れ（起こした後の最初のフレームで 1 度だけ Some）。
    ///
    /// # 引数
    /// * `now` - フレームの頭の時刻
    pub fn take_wake_latency(&mut self, now: Instant) -> Option<Duration> {
        self.woke_by_activity_at.take().map(|at| now.saturating_duration_since(at))
    }

    /// フレームの終わりに、次のフレームを要求するかを決める。
    ///
    /// # 引数
    /// * `now` - 今の時刻（起きる時刻の計算に使う）
    pub fn on_frame_end(&mut self, now: Instant) -> NextFrame {
        let Some(limit) = self.idle_after_frames else {
            return NextFrame::Continue;
        };
        self.frames_since_activity = self.frames_since_activity.saturating_add(1);
        if self.frames_since_activity < limit {
            return NextFrame::Continue;
        }
        if !self.idle {
            self.idle = true;
            self.stats.idle_entries += 1;
        }
        self.wake_at = self.wake_interval.map(|interval| now + interval);
        NextFrame::Idle { wake_at: self.wake_at }
    }

    /// 眠っている間に 1 フレームだけ描く時刻が来たか（イベントループの 1 周ごとに呼ぶ）。
    ///
    /// # 戻り値
    /// 来ていれば true（呼び出し側は request_redraw する。そのフレームの終わりに on_frame_end がまた眠らせる）。
    /// 同じ時刻で 2 度 true にならないよう、true を返したら起きる時刻を消す。
    pub fn wake_due(&mut self, now: Instant) -> bool {
        if !self.idle {
            return false;
        }
        match self.wake_at {
            Some(at) if now >= at => {
                self.wake_at = None;
                self.stats.timer_wakes += 1;
                true
            }
            _ => false,
        }
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 無効な判定は常に Continue で、理由を数えても何もしない（従来どおり）。
    #[test]
    fn disabled_gate_always_continues() {
        let mut gate = IdleRedrawGate::disabled();
        let now = Instant::now();
        for _ in 0..1000 {
            assert_eq!(gate.on_frame_end(now), NextFrame::Continue);
        }
        assert!(!gate.note_activity(now));
        assert!(!gate.wake_due(now));
        assert!(!gate.is_idle());
        assert_eq!(gate.take_wake_latency(now), None);
    }

    /// N フレーム目の終わりで眠り、wake_interval が無ければ Wait（wake_at なし）。
    #[test]
    fn sleeps_after_n_quiet_frames() {
        let mut gate = IdleRedrawGate::new(Some(3), None);
        let now = Instant::now();
        assert_eq!(gate.on_frame_end(now), NextFrame::Continue);
        assert_eq!(gate.on_frame_end(now), NextFrame::Continue);
        assert_eq!(gate.on_frame_end(now), NextFrame::Idle { wake_at: None });
        assert!(gate.is_idle());
        assert_eq!(gate.stats().idle_entries, 1);
    }

    /// 眠っている間の理由で起き（true）、数え直す。起きている間の理由は false。
    #[test]
    fn activity_wakes_and_resets_count() {
        let mut gate = IdleRedrawGate::new(Some(2), None);
        let now = Instant::now();
        assert!(!gate.note_activity(now), "起きている間は要求済み");
        assert_eq!(gate.take_wake_latency(now), None, "起こしていなければ遅れは無い");
        gate.on_frame_end(now);
        assert_eq!(gate.on_frame_end(now), NextFrame::Idle { wake_at: None });
        assert!(gate.note_activity(now), "眠っていたら起こす");
        assert!(!gate.is_idle());
        // 起こしてから最初のフレームの頭までの遅れは 1 度だけ取り出せる
        let later = now + Duration::from_millis(3);
        assert_eq!(gate.take_wake_latency(later), Some(Duration::from_millis(3)));
        assert_eq!(gate.take_wake_latency(later), None);
        // 数え直したので、また 2 フレーム描いてから眠る
        assert_eq!(gate.on_frame_end(now), NextFrame::Continue);
        assert_eq!(gate.on_frame_end(now), NextFrame::Idle { wake_at: None });
        assert_eq!(gate.stats().activity_wakes, 1);
        assert_eq!(gate.stats().idle_entries, 2);
    }

    /// wake_interval があれば、その時刻に 1 度だけ起き、次のフレームの終わりでまた眠る。
    #[test]
    fn timer_wake_draws_one_frame_then_sleeps() {
        let interval = Duration::from_millis(1000);
        let mut gate = IdleRedrawGate::new(Some(1), Some(interval));
        let t0 = Instant::now();
        assert_eq!(gate.on_frame_end(t0), NextFrame::Idle { wake_at: Some(t0 + interval) });
        assert!(!gate.wake_due(t0), "まだ早い");
        assert!(gate.wake_due(t0 + interval), "時刻が来た");
        assert!(!gate.wake_due(t0 + interval), "同じ時刻で 2 度起きない");
        // 起きて描いたフレームの終わり: 理由は無いのでまた眠る（次の時刻を予約）
        let t1 = t0 + interval;
        assert_eq!(gate.on_frame_end(t1), NextFrame::Idle { wake_at: Some(t1 + interval) });
        assert_eq!(gate.stats().timer_wakes, 1);
        assert_eq!(gate.stats().idle_entries, 1, "眠ったままなので入った回数は増えない");
    }
}
