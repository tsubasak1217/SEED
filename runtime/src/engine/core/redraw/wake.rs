// ============================================================
//  redraw/wake.rs — 他のスレッドから「描く理由」を積み、眠っているイベントループを起こす口（W2-10a）
//
//  【なぜ要るか】
//  on_demand で描画を止めている間、イベントループは ControlFlow::Wait（予定があれば WaitUntil）で眠る。
//  入力の WindowEvent は winit が自分で起こすが、IPC の読み取りのスレッド・Android の JNI（プラットフォームのイベント・
//  文字入力・音声フォーカス・画面の変化）は winit の外から届くので、誰かが起こさないと次の入力まで気付かない
//  （W2-0 の試作では IPC の命令が最大 1 秒遅れた。roadmap §3.8.3 の D-1）。
//
//  【仕組み】
//    raise(理由)   … 理由のビットを積む（AtomicU32 の fetch_or）。イベントループが眠っていれば、登録された起こし手
//                    （App が EventLoopProxy::send_event を包んで登録する）を 1 回だけ呼ぶ（送った印で重複を抑える）
//    enter_idle()  … イベントループが眠る直前に呼ぶ。「眠る」の印を立ててから積まれた理由を取り出す
//    leave_idle()  … 起きたときに呼ぶ（印を下ろす）
//  眠る側は「印を立てる → 理由を読む」、積む側は「理由を積む → 印を読む」の順（どちらも SeqCst）なので、
//  どちらかが必ず相手の書き込みを見る（眠る側が理由に気付いて眠らない、か、積む側が印に気付いて起こす）。取りこぼさない。
//
//  【Android の起こし方】winit 0.30 の EventLoopProxy::send_event は、Android では android-activity の
//  AndroidAppWaker（ALooper_wake）でルーパーを起こし、user_event を配る（W2-0 の I-10）。Windows は PostMessage。
// ============================================================

use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::OnceLock;

use super::reason::{RedrawReason, RedrawReasons};

/// イベントループを起こす手（EventLoopProxy::send_event を包んだもの）。
pub type EventLoopWaker = Box<dyn Fn() + Send + Sync>;

/// 他のスレッドから描く理由を積み、眠っているイベントループを起こす口。
///
/// プロセスで 1 つ（`EVENT_LOOP_WAKE`）を使う。単体テストはそれぞれ自分の値を作る。
pub struct WakeSignal {
    /// 積まれた理由（まだ誰も取り出していないもの）。
    pending: AtomicU32,
    /// イベントループが眠っている（眠ろうとしている）か。
    loop_idle: AtomicBool,
    /// 起こしの知らせを送ってから、まだ受け取られていないか（知らせを 1 通に抑える）。
    wake_posted: AtomicBool,
    /// 起こし手（最初に登録したものだけが有効）。
    waker: OnceLock<EventLoopWaker>,
    /// 起こしの知らせを送った回数（ログ・計測・試験）。
    posted: AtomicU64,
}

impl WakeSignal {
    /// 空の口を作る（起こし手なし・眠っていない）。
    pub const fn new() -> Self {
        Self {
            pending: AtomicU32::new(0),
            loop_idle: AtomicBool::new(false),
            wake_posted: AtomicBool::new(false),
            waker: OnceLock::new(),
            posted: AtomicU64::new(0),
        }
    }

    /// 起こし手を登録する（App がイベントループを作った直後に 1 回）。
    ///
    /// # 戻り値
    /// 登録できたら true。既に登録があれば false（最初の登録のまま）。
    pub fn install_waker(&self, waker: EventLoopWaker) -> bool {
        self.waker.set(waker).is_ok()
    }

    /// 起こし手が登録済みか（無ければ眠っている間に他のスレッドから起こせない＝on_demand を使わない）。
    pub fn has_waker(&self) -> bool {
        self.waker.get().is_some()
    }

    /// 理由を 1 つ積む（どのスレッドから呼んでもよい）。
    pub fn raise(&self, reason: RedrawReason) {
        self.raise_all(reason.into());
    }

    /// 理由をまとめて積む。イベントループが眠っていれば起こす（知らせは受け取られるまで 1 通だけ）。
    pub fn raise_all(&self, reasons: RedrawReasons) {
        if reasons.is_empty() {
            return;
        }
        self.pending.fetch_or(reasons.bits(), Ordering::SeqCst);
        if !self.loop_idle.load(Ordering::SeqCst) {
            // 起きている: フレームの末尾の判定が積んだ理由を読む
            return;
        }
        if self.wake_posted.swap(true, Ordering::SeqCst) {
            // 知らせは送ってあり、まだ受け取られていない（重ねて送らない）
            return;
        }
        match self.waker.get() {
            Some(waker) => {
                waker();
                self.posted.fetch_add(1, Ordering::Relaxed);
            }
            // 起こし手が無い（イベントループの外・試験）: 送った印を戻す
            None => self.wake_posted.store(false, Ordering::SeqCst),
        }
    }

    /// 積まれた理由を取り出す（イベントループのスレッドから）。
    pub fn take_pending(&self) -> RedrawReasons {
        RedrawReasons::from_bits(self.pending.swap(0, Ordering::SeqCst))
    }

    /// イベントループが眠る直前に呼ぶ。「眠る」の印を立ててから、積まれていた理由を取り出す。
    ///
    /// # 戻り値
    /// その間に積まれていた理由。描く理由を含んでいれば、呼び出し側は眠らずに `leave_idle` して描く。
    pub fn enter_idle(&self) -> RedrawReasons {
        self.loop_idle.store(true, Ordering::SeqCst);
        self.take_pending()
    }

    /// イベントループが起きた（眠りをやめた）ときに呼ぶ。印を下ろし、送った知らせの印も下ろす。
    pub fn leave_idle(&self) {
        self.loop_idle.store(false, Ordering::SeqCst);
        self.wake_posted.store(false, Ordering::SeqCst);
    }

    /// 起こしの知らせ（user event）を受け取った。眠ったままなら、次の raise でまた知らせを送れるようにする。
    pub fn acknowledge_wake(&self) {
        self.wake_posted.store(false, Ordering::SeqCst);
    }

    /// 眠っている（眠ろうとしている）か。
    pub fn is_loop_idle(&self) -> bool {
        self.loop_idle.load(Ordering::SeqCst)
    }

    /// 起こしの知らせを送った回数。
    pub fn posted_count(&self) -> u64 {
        self.posted.load(Ordering::Relaxed)
    }
}

impl Default for WakeSignal {
    fn default() -> Self {
        Self::new()
    }
}

/// プロセスで 1 つの起こしの口（App がイベントループの EventLoopProxy を登録する）。
pub static EVENT_LOOP_WAKE: WakeSignal = WakeSignal::new();

/// 描く理由を積む（IPC の読み取り・JNI の受け口など、どのスレッドからでも呼べる入口）。
///
/// イベントループが眠っていれば起こす。起きていれば、次のフレームの末尾の判定がこの理由を読む。
pub fn raise(reason: RedrawReason) {
    EVENT_LOOP_WAKE.raise(reason);
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;

    /// 呼ばれた回数を数える起こし手をつけた口を作る。
    fn signal_with_counter() -> (Arc<WakeSignal>, Arc<AtomicU64>) {
        let signal = Arc::new(WakeSignal::new());
        let count = Arc::new(AtomicU64::new(0));
        let counter = Arc::clone(&count);
        assert!(signal.install_waker(Box::new(move || {
            counter.fetch_add(1, Ordering::SeqCst);
        })));
        (signal, count)
    }

    /// 起きている間は積むだけで起こさない（フレームの末尾が読む）。
    #[test]
    fn awake_loop_only_accumulates() {
        let (signal, count) = signal_with_counter();
        signal.raise(RedrawReason::Ipc);
        signal.raise(RedrawReason::PlatformEvent);
        assert_eq!(count.load(Ordering::SeqCst), 0);
        let taken = signal.take_pending();
        assert!(taken.contains(RedrawReason::Ipc) && taken.contains(RedrawReason::PlatformEvent));
        assert!(signal.take_pending().is_empty(), "取り出したら空");
    }

    /// 眠っている間は起こす。知らせは受け取られるまで 1 通だけ。
    #[test]
    fn idle_loop_is_woken_once_until_acknowledged() {
        let (signal, count) = signal_with_counter();
        assert!(signal.enter_idle().is_empty());
        signal.raise(RedrawReason::Ipc);
        signal.raise(RedrawReason::Ipc);
        signal.raise(RedrawReason::TextInput);
        assert_eq!(count.load(Ordering::SeqCst), 1, "重ねて送らない");
        assert_eq!(signal.posted_count(), 1);
        // 受け取ったが眠ったまま（予定の決め直しだけ）→ 次の raise でまた送る
        signal.acknowledge_wake();
        let _ = signal.take_pending();
        signal.raise(RedrawReason::Reschedule);
        assert_eq!(count.load(Ordering::SeqCst), 2);
        // 起きたら送らない
        signal.leave_idle();
        signal.raise(RedrawReason::Ipc);
        assert_eq!(count.load(Ordering::SeqCst), 2);
    }

    /// 眠る直前に積まれていた理由は enter_idle が返す（眠らずに描ける）。
    #[test]
    fn reasons_raised_before_sleeping_are_returned() {
        let (signal, count) = signal_with_counter();
        signal.raise(RedrawReason::PlatformEvent);
        let late = signal.enter_idle();
        assert!(late.contains(RedrawReason::PlatformEvent));
        assert_eq!(count.load(Ordering::SeqCst), 0, "眠る前なので起こしは要らない");
    }

    /// 起こし手が無ければ送らず、送った印も残さない。
    #[test]
    fn no_waker_means_no_post() {
        let signal = WakeSignal::new();
        assert!(!signal.has_waker());
        let _ = signal.enter_idle();
        signal.raise(RedrawReason::Ipc);
        assert_eq!(signal.posted_count(), 0);
        // 後から登録すれば次の raise で送れる（印が残っていない）
        let count = Arc::new(AtomicU64::new(0));
        let counter = Arc::clone(&count);
        assert!(signal.install_waker(Box::new(move || {
            counter.fetch_add(1, Ordering::SeqCst);
        })));
        assert!(!signal.install_waker(Box::new(|| {})), "2 回目の登録は無視");
        signal.raise(RedrawReason::Ipc);
        assert_eq!(count.load(Ordering::SeqCst), 1);
    }

    /// 他のスレッドが積み続ける間に眠る・起きるを繰り返しても、積まれた理由を取りこぼさない
    /// （眠る側が enter_idle で気付く、か、起こし手が呼ばれる、のどちらかが必ず起きる）。
    #[test]
    fn concurrent_raises_are_never_lost() {
        /// 積む側のスレッドの数。
        const PRODUCERS: usize = 4;
        /// 1 本のスレッドが積む回数。
        const RAISES_PER_PRODUCER: usize = 2_000;
        let (signal, count) = signal_with_counter();
        let producers: Vec<_> = (0..PRODUCERS)
            .map(|_| {
                let signal = Arc::clone(&signal);
                std::thread::spawn(move || {
                    for _ in 0..RAISES_PER_PRODUCER {
                        signal.raise(RedrawReason::Ipc);
                        std::thread::yield_now();
                    }
                })
            })
            .collect();
        /// 眠ってから起こされるまで待つ上限（積む側が動いている限り、これを超えたら起こしを取りこぼした）。
        const WAKE_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(2);
        // イベントループの真似: 眠る → 起こされたか enter_idle で気付いたら起きる、を繰り返す
        let mut noticed = 0usize;
        let mut woken = 0usize;
        let mut lost = 0usize;
        let mut last_posts = 0u64;
        while producers.iter().any(|p| !p.is_finished()) {
            let late = signal.enter_idle();
            if !late.is_empty() {
                noticed += 1;
                signal.leave_idle();
                continue;
            }
            // 眠った: 起こし手が呼ばれるまで待つ（本物のルーパーの待ちの代わり）
            let wait_deadline = std::time::Instant::now() + WAKE_TIMEOUT;
            loop {
                let posts = count.load(Ordering::SeqCst);
                if posts > last_posts {
                    last_posts = posts;
                    woken += 1;
                    break;
                }
                if producers.iter().all(|p| p.is_finished()) {
                    break;
                }
                if std::time::Instant::now() > wait_deadline {
                    // 積む側が動いているのに起こされない＝取りこぼし
                    lost += 1;
                    break;
                }
                std::thread::yield_now();
            }
            signal.acknowledge_wake();
            let _ = signal.take_pending();
            signal.leave_idle();
        }
        for producer in producers {
            producer.join().unwrap();
        }
        assert_eq!(lost, 0, "起こしを取りこぼした（気付いた {noticed} 回・起こされた {woken} 回）");
        assert!(noticed + woken > 0, "一度も気付かなかった");
    }
}
