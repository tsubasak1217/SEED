// ============================================================
//  redraw/script_requests.rs — スクリプトの描画の要求（SEED.Redraw の中身。W2-10a）
//
//  【C# の API と、ここでの意味】（FFI は core/scripting/redraw_bridge.rs。正典は docs/redraw_policy.md §4）
//    Request()            … 次の 1 フレームを描く。どのスレッドから呼んでもよい（止めていれば起こす）
//    RequestAfter(秒)     … その秒数の後に 1 フレームを描く（止めている間は WaitUntil で起きる。時計の表示の毎秒の更新など）
//    KeepAlive(秒)        … その秒数の間は描き続ける（演出・部品のアニメーションの間。延ばすだけで縮めない）
//    SetContinuous(bool)  … true の間は常に描く（ゲームの画面・センサーを読む画面・鳴動の画面）
//    Policy（読み書き）   … 描き方の方針を実行中に上書きする（continuous / on_demand。プロジェクト設定より優先）
//  時間はすべて実時間（Time.Scale の影響を受けない。止めている間もゲームの時間は進まないので、ゲームの時間では測らない）。
//
//  【状態の置き場】プロセスで 1 つ（Mutex）。スクリプトはふつうイベントループのスレッドで動くが、C# の async の続き
//  （通信の完了など）から Request() が呼ばれても起こせるように、スレッドを問わない形にした。
//  App はフレームの末尾で `take_frame_state` を呼び、このフレームの理由と次の予定を取り出す。
//  Play の区切り（開始・停止）で `reset` し、前の回の要求を持ち越さない。
// ============================================================

use std::sync::atomic::{AtomicI32, Ordering};
use std::sync::Mutex;
use std::time::{Duration, Instant};

use super::policy::{RenderPolicy, POLICY_CODE_CONTINUOUS};
use super::reason::{RedrawReason, RedrawReasons};
use super::schedule;
use super::wake;

/// スクリプトの要求の状態（純粋な値。単体テストはこれを直接使う）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ScriptRedrawRequests {
    /// `RequestAfter` の予定のうち、いちばん早いもの。
    request_at: Option<Instant>,
    /// `KeepAlive` の期限（この時刻までは描き続ける）。
    keep_alive_until: Option<Instant>,
    /// `SetContinuous(true)` の間は true。
    continuous: bool,
    /// `Policy` の上書き（None ならプロジェクト設定のまま）。
    policy_override: Option<RenderPolicy>,
}

/// フレームの末尾に取り出す、スクリプトの要求の結果。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ScriptFrameState {
    /// このフレームの理由（KeepAlive の期限の内・常に描く・RequestAfter の時刻が来た）。
    pub reasons: RedrawReasons,
    /// 次の予定（まだ来ていない RequestAfter。KeepAlive は期限まで理由になり続けるので予定にしない）。
    pub next_deadline: Option<Instant>,
}

impl ScriptRedrawRequests {
    /// 空の状態（要求なし・上書きなし）。
    pub const fn new() -> Self {
        Self { request_at: None, keep_alive_until: None, continuous: false, policy_override: None }
    }

    /// `RequestAfter`: `delay` の後に 1 フレームを描く（既にもっと早い予定があればそのまま）。
    pub fn request_after(&mut self, now: Instant, delay: Duration) {
        let at = now + delay;
        self.request_at = Some(self.request_at.map_or(at, |existing| existing.min(at)));
    }

    /// `KeepAlive`: `duration` の間は描き続ける（既にもっと遅い期限があればそのまま＝延ばすだけ）。
    pub fn keep_alive(&mut self, now: Instant, duration: Duration) {
        let until = now + duration;
        self.keep_alive_until = Some(self.keep_alive_until.map_or(until, |existing| existing.max(until)));
    }

    /// `SetContinuous`。
    pub fn set_continuous(&mut self, continuous: bool) {
        self.continuous = continuous;
    }

    /// 常に描く要求の中か。
    pub fn continuous(&self) -> bool {
        self.continuous
    }

    /// `Policy` の上書き（None で上書きを外す）。
    pub fn set_policy_override(&mut self, policy: Option<RenderPolicy>) {
        self.policy_override = policy;
    }

    /// `Policy` の上書き。
    pub fn policy_override(&self) -> Option<RenderPolicy> {
        self.policy_override
    }

    /// フレームの末尾に、このフレームの理由と次の予定を取り出す。
    ///
    /// 時刻の来た `RequestAfter` は理由（ScriptRequest）にして消し、期限の過ぎた `KeepAlive` は消す。
    ///
    /// # 引数
    /// * `now` - フレームの末尾の時刻
    pub fn take_frame_state(&mut self, now: Instant) -> ScriptFrameState {
        let mut state = ScriptFrameState::default();
        if let Some(at) = self.request_at {
            if at <= now {
                state.reasons.insert(RedrawReason::ScriptRequest);
                self.request_at = None;
            } else {
                state.next_deadline = Some(at);
            }
        }
        match self.keep_alive_until {
            Some(until) if now < until => state.reasons.insert(RedrawReason::ScriptKeepAlive),
            Some(_) => self.keep_alive_until = None,
            None => {}
        }
        state.reasons.insert_if(self.continuous, RedrawReason::ScriptContinuous);
        state
    }

    /// 眠っている間に予定が変わったときの、次の予定（取り出さずに見るだけ）。
    ///
    /// KeepAlive の期限の内・常に描くなら「今」（すぐ起きて描く）。そうでなければ RequestAfter の予定。
    pub fn peek_next_deadline(&self, now: Instant) -> Option<Instant> {
        let keeps_drawing = self.continuous || self.keep_alive_until.is_some_and(|until| now < until);
        if keeps_drawing {
            return Some(now);
        }
        self.request_at
    }
}

/// プロセスで 1 つのスクリプトの要求。
static SCRIPT_REQUESTS: Mutex<ScriptRedrawRequests> = Mutex::new(ScriptRedrawRequests::new());

/// プロジェクト設定の方針（FFI の番号。App が起動時に `publish_configured_policy` で写す。既定は continuous）。
///
/// スクリプトの `SEED.Redraw.Policy` の読み取り（上書きが無いとき）が App を参照できないため、ここへ写す
/// （frame_pacing の publish_configured_target_fps と同じ形）。
static CONFIGURED_POLICY: AtomicI32 = AtomicI32::new(POLICY_CODE_CONTINUOUS);

/// 状態を触る（毒されていても中身を使う。値は小さな平の構造体なので途中の状態は壊れない）。
fn with_requests<R>(f: impl FnOnce(&mut ScriptRedrawRequests) -> R) -> R {
    let mut guard = SCRIPT_REQUESTS.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    f(&mut guard)
}

/// `SEED.Redraw.Request()`: 次の 1 フレームを描く（止めていれば起こす）。
pub fn request() {
    wake::raise(RedrawReason::ScriptRequest);
}

/// `SEED.Redraw.RequestAfter(秒)`: その秒数の後に 1 フレームを描く。
///
/// # 引数
/// * `secs` - 秒（負・NaN は 0 ＝ 次のフレーム。上限は schedule::MAX_SCHEDULE_AHEAD_SECS）
pub fn request_after(secs: f64) {
    let delay = schedule::clamped_delay(secs);
    with_requests(|requests| requests.request_after(Instant::now(), delay));
    // 止めている間（他のスレッドからの呼び出し）なら、起きる時刻を決め直させる（描かない）
    wake::raise(RedrawReason::Reschedule);
}

/// `SEED.Redraw.KeepAlive(秒)`: その秒数の間は描き続ける。
///
/// # 引数
/// * `secs` - 秒（負・NaN は 0 ＝ 何もしない。上限は schedule::MAX_SCHEDULE_AHEAD_SECS）
pub fn keep_alive(secs: f64) {
    let duration = schedule::clamped_delay(secs);
    if duration.is_zero() {
        return;
    }
    with_requests(|requests| requests.keep_alive(Instant::now(), duration));
    wake::raise(RedrawReason::Reschedule);
}

/// `SEED.Redraw.SetContinuous(bool)`。
pub fn set_continuous(continuous: bool) {
    with_requests(|requests| requests.set_continuous(continuous));
    wake::raise(RedrawReason::Reschedule);
}

/// `SEED.Redraw.IsContinuous`。
pub fn continuous() -> bool {
    with_requests(|requests| requests.continuous())
}

/// `SEED.Redraw.Policy` の設定（None で上書きを外してプロジェクト設定へ戻す）。
pub fn set_policy_override(policy: Option<RenderPolicy>) {
    with_requests(|requests| requests.set_policy_override(policy));
    wake::raise(RedrawReason::Reschedule);
}

/// `SEED.Redraw.Policy` の上書き（無ければ None）。
pub fn policy_override() -> Option<RenderPolicy> {
    with_requests(|requests| requests.policy_override())
}

/// プロジェクト設定の方針を写す（App が起動時に project_settings.json を読んだ直後に 1 回）。
pub fn publish_configured_policy(policy: RenderPolicy) {
    CONFIGURED_POLICY.store(policy.to_code(), Ordering::Relaxed);
}

/// プロジェクト設定の方針（写したもの）。
pub fn configured_policy() -> RenderPolicy {
    RenderPolicy::from_code(CONFIGURED_POLICY.load(Ordering::Relaxed)).unwrap_or_default()
}

/// 今の方針（スクリプトの上書き → プロジェクト設定の順。`SEED.Redraw.Policy` の読み取り・App の判定）。
pub fn effective_policy() -> RenderPolicy {
    policy_override().unwrap_or_else(configured_policy)
}

/// フレームの末尾に、このフレームの理由と次の予定を取り出す（App から）。
pub fn take_frame_state(now: Instant) -> ScriptFrameState {
    with_requests(|requests| requests.take_frame_state(now))
}

/// 眠っている間の予定の決め直し（App から。取り出さずに見るだけ）。
pub fn peek_next_deadline(now: Instant) -> Option<Instant> {
    with_requests(|requests| requests.peek_next_deadline(now))
}

/// Play の区切り（開始・停止）で要求をすべて捨てる（前の回の要求・方針の上書きを持ち越さない）。
pub fn reset() {
    with_requests(|requests| *requests = ScriptRedrawRequests::new());
}

// ============================================================
//  テスト（プロセスで 1 つの状態は触らず、純粋な値で確かめる）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 何も要求しなければ理由も予定も無い。
    #[test]
    fn empty_requests_have_no_reasons() {
        let mut requests = ScriptRedrawRequests::new();
        let state = requests.take_frame_state(Instant::now());
        assert!(state.reasons.is_empty());
        assert_eq!(state.next_deadline, None);
        assert_eq!(requests.policy_override(), None);
    }

    /// RequestAfter: 時刻の前は予定、時刻が来たら 1 回だけ理由になって消える。早い予定が勝つ。
    #[test]
    fn request_after_becomes_deadline_then_reason_once() {
        let t0 = Instant::now();
        let mut requests = ScriptRedrawRequests::new();
        requests.request_after(t0, Duration::from_millis(500));
        requests.request_after(t0, Duration::from_millis(900));
        let before = requests.take_frame_state(t0 + Duration::from_millis(100));
        assert!(before.reasons.is_empty());
        assert_eq!(before.next_deadline, Some(t0 + Duration::from_millis(500)), "早い予定が勝つ");
        requests.request_after(t0, Duration::from_millis(200));
        assert_eq!(requests.peek_next_deadline(t0), Some(t0 + Duration::from_millis(200)));
        let due = requests.take_frame_state(t0 + Duration::from_millis(200));
        assert_eq!(due.reasons, RedrawReasons::only(RedrawReason::ScriptRequest));
        assert_eq!(due.next_deadline, None);
        let after = requests.take_frame_state(t0 + Duration::from_millis(300));
        assert!(after.reasons.is_empty(), "1 回だけ");
    }

    /// KeepAlive: 期限まで理由、過ぎたら消える。延ばすだけで縮めない。
    #[test]
    fn keep_alive_extends_only() {
        let t0 = Instant::now();
        let mut requests = ScriptRedrawRequests::new();
        requests.keep_alive(t0, Duration::from_secs(2));
        requests.keep_alive(t0, Duration::from_secs(1));
        assert_eq!(
            requests.take_frame_state(t0 + Duration::from_millis(1500)).reasons,
            RedrawReasons::only(RedrawReason::ScriptKeepAlive),
            "短い KeepAlive で縮まない"
        );
        assert_eq!(requests.peek_next_deadline(t0 + Duration::from_millis(1500)), Some(t0 + Duration::from_millis(1500)));
        assert!(requests.take_frame_state(t0 + Duration::from_secs(2)).reasons.is_empty(), "期限ちょうどで終わる");
        assert_eq!(requests.peek_next_deadline(t0 + Duration::from_secs(3)), None);
    }

    /// SetContinuous の間は毎フレーム理由、外せば消える。方針の上書きは値を持つだけ。
    #[test]
    fn continuous_and_policy_override() {
        let now = Instant::now();
        let mut requests = ScriptRedrawRequests::new();
        requests.set_continuous(true);
        for _ in 0..3 {
            assert!(requests.take_frame_state(now).reasons.contains(RedrawReason::ScriptContinuous));
        }
        assert_eq!(requests.peek_next_deadline(now), Some(now), "常に描くならすぐ起きる");
        requests.set_continuous(false);
        assert!(requests.take_frame_state(now).reasons.is_empty());
        requests.set_policy_override(Some(RenderPolicy::OnDemand));
        assert_eq!(requests.policy_override(), Some(RenderPolicy::OnDemand));
        requests.set_policy_override(None);
        assert_eq!(requests.policy_override(), None);
    }
}
