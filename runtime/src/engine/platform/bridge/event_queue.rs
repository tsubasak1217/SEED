// ============================================================
//  platform/bridge/event_queue.rs — プラットフォームのイベント（JSON 文字列）の待ち行列（上限つき）
//
//  【役割】
//  OS の糊（Android の JNI のスレッド）・デスクトップの模擬・スクリプトへ見せる直前の置き場の 3 か所で使う、
//  「別のスレッドが積む → エンジンのスレッドが取り出す」だけの箱。中身は JSON の文字列（形は wire.rs）。
//    - Android: nativeOnPlatformEvent（Java の SEEDPlatform スレッド）が積み、AndroidPlatformBridge::poll_events が取り出す
//    - 模擬    : DesktopSimBridge が自分の箱に積み、poll_events で取り出す
//    - スクリプト: エンジンがフレームの頭で上の 2 つから移し、C# の PlatformEvents.Poll が 1 件ずつ取り出す
//
//  【上限】
//  誰も取り出さない（スクリプトが無いシーン・Play していない）まま積まれ続けてもメモリが伸びないよう、
//  上限を超えたら**古いものから捨てる**（捨てた数は数えておき、診断に使う）。目覚ましのように取りこぼせない
//  知らせは :seed_platform の記録（EventJournal）が正本で、ここは配達の途中の箱にすぎない（W1-4 で未読の取り直しを足す）。
//
//  【スレッド】
//  Mutex 1 つで守る。持つのは文字列の出し入れの間だけで、ロック中に他のロックを取らない。
//  ロックが毒されていても（他のスレッドが panic）中身は文字列だけで壊れないので、そのまま使い続ける。
// ============================================================

use std::collections::VecDeque;
use std::sync::{Mutex, MutexGuard, PoisonError};

/// 1 つの箱に溜められるイベントの既定の上限（件数）。
///
/// 目覚まし 1 回で出るイベントは数件（鳴動の開始・停止・通知の操作）なので、1 フレームに取り出しきれない量ではない。
/// 上限は「スクリプトが居ないまま長く積まれる」異常の蓋。
pub const DEFAULT_EVENT_QUEUE_CAPACITY: usize = 256;

/// 積んだ結果。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PushOutcome {
    /// そのまま積んだ。
    Accepted,
    /// 上限に達していたので、いちばん古いものを 1 件捨ててから積んだ。
    DroppedOldest,
}

/// 先頭を「呼び出し側の入れ物に収まるときだけ」取り出した結果（C# への受け渡しの 2 段階の約束用）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FrontTake {
    /// 空だった。
    Empty,
    /// 先頭が入れ物より大きかったので取り出していない（値は必要なバイト数）。
    TooLarge(usize),
    /// 取り出した。
    Taken(String),
}

/// 箱の中身（ロックの内側）。
#[derive(Debug)]
struct QueueState {
    /// 積まれた順のイベント（先頭が最も古い）。
    events: VecDeque<String>,
    /// 上限のために捨てた件数（作ってからの累計）。
    dropped: u64,
}

/// プラットフォームのイベントの待ち行列（上限つき・古いものから捨てる）。
#[derive(Debug)]
pub struct PlatformEventQueue {
    /// 中身。
    state: Mutex<QueueState>,
    /// 溜められる件数の上限（1 以上）。
    capacity: usize,
}

/// 上限の下限（0 を渡されても 1 件は溜められるようにする）。
const MIN_CAPACITY: usize = 1;

impl PlatformEventQueue {
    /// 空の箱を作る（`static` にも置けるよう const）。
    ///
    /// # 引数
    /// * `capacity` - 溜められる件数の上限（0 なら 1 として扱う）
    pub const fn new(capacity: usize) -> Self {
        Self {
            state: Mutex::new(QueueState { events: VecDeque::new(), dropped: 0 }),
            capacity: if capacity < MIN_CAPACITY { MIN_CAPACITY } else { capacity },
        }
    }

    /// ロックを取る（毒されていても中身を使う。理由はファイル冒頭）。
    fn lock(&self) -> MutexGuard<'_, QueueState> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// 1 件積む（上限なら古いものを 1 件捨ててから）。
    pub fn push(&self, event: String) -> PushOutcome {
        let mut state = self.lock();
        let outcome = if state.events.len() >= self.capacity {
            state.events.pop_front();
            state.dropped += 1;
            PushOutcome::DroppedOldest
        } else {
            PushOutcome::Accepted
        };
        state.events.push_back(event);
        outcome
    }

    /// 溜まっているものを積まれた順にすべて取り出す。
    pub fn drain(&self) -> Vec<String> {
        self.lock().events.drain(..).collect()
    }

    /// 先頭を、そのバイト数が `capacity_bytes` 以下のときだけ取り出す（1 回のロックの中で判定と取り出しを行う）。
    ///
    /// 覗いてから別に取り出す形にしないのは、その間に他のスレッドが積んで上限で先頭が捨てられると、
    /// 覗いたものと違うものを取り出してしまうため。
    pub fn take_front_if_fits(&self, capacity_bytes: usize) -> FrontTake {
        let mut state = self.lock();
        let Some(front) = state.events.front() else {
            return FrontTake::Empty;
        };
        let needed = front.len();
        if needed > capacity_bytes {
            return FrontTake::TooLarge(needed);
        }
        match state.events.pop_front() {
            Some(event) => FrontTake::Taken(event),
            None => FrontTake::Empty,
        }
    }

    /// 溜まっている件数。
    pub fn len(&self) -> usize {
        self.lock().events.len()
    }

    /// 空か。
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    /// 溜まっているものを捨てる（Play の開始・停止で前の回のイベントを持ち越さないため）。
    pub fn clear(&self) {
        self.lock().events.clear();
    }

    /// 上限のために捨てた件数（作ってからの累計。診断用）。
    pub fn dropped_count(&self) -> u64 {
        self.lock().dropped
    }

    /// 溜められる件数の上限。
    pub fn capacity(&self) -> usize {
        self.capacity
    }
}

// ============================================================
//  ユニットテスト（ローカルの箱だけを使い、プロセスで共有する箱には触らない）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 積んだ順に取り出せ、取り出した後は空になる。
    #[test]
    fn drains_in_push_order() {
        let queue = PlatformEventQueue::new(DEFAULT_EVENT_QUEUE_CAPACITY);
        assert!(queue.is_empty());
        assert_eq!(queue.push("a".into()), PushOutcome::Accepted);
        assert_eq!(queue.push("b".into()), PushOutcome::Accepted);
        assert_eq!(queue.len(), 2);
        assert_eq!(queue.drain(), vec!["a".to_string(), "b".to_string()]);
        assert!(queue.is_empty());
        assert!(queue.drain().is_empty());
    }

    /// 上限を超えたら古いものから捨て、捨てた数を数える（新しいものは必ず残る）。
    #[test]
    fn drops_oldest_when_full() {
        const CAPACITY: usize = 3;
        const EXTRA: usize = 2;
        let queue = PlatformEventQueue::new(CAPACITY);
        for index in 0..(CAPACITY + EXTRA) {
            let outcome = queue.push(format!("e{index}"));
            let expected = if index < CAPACITY { PushOutcome::Accepted } else { PushOutcome::DroppedOldest };
            assert_eq!(outcome, expected, "index={index}");
        }
        assert_eq!(queue.dropped_count(), EXTRA as u64);
        assert_eq!(queue.drain(), vec!["e2".to_string(), "e3".to_string(), "e4".to_string()]);
    }

    /// 上限 0 は 1 として扱う（何も溜められない箱にはしない）。
    #[test]
    fn zero_capacity_keeps_one() {
        let queue = PlatformEventQueue::new(0);
        assert_eq!(queue.capacity(), 1);
        queue.push("old".into());
        queue.push("new".into());
        assert_eq!(queue.drain(), vec!["new".to_string()]);
    }

    /// 収まるときだけ先頭を取り出す: 空・大きすぎ（取り出さない）・取り出し の 3 通り。
    #[test]
    fn take_front_if_fits_respects_capacity() {
        let queue = PlatformEventQueue::new(DEFAULT_EVENT_QUEUE_CAPACITY);
        assert_eq!(queue.take_front_if_fits(usize::MAX), FrontTake::Empty);
        queue.push("12345".into());
        queue.push("6".into());
        // 4 バイトの入れ物には 5 バイトの先頭は入らない（取り出さず、必要な大きさを返す）
        assert_eq!(queue.take_front_if_fits(4), FrontTake::TooLarge(5));
        assert_eq!(queue.len(), 2);
        // ちょうどの大きさなら取り出せる
        assert_eq!(queue.take_front_if_fits(5), FrontTake::Taken("12345".into()));
        assert_eq!(queue.take_front_if_fits(5), FrontTake::Taken("6".into()));
        assert_eq!(queue.take_front_if_fits(5), FrontTake::Empty);
    }

    /// 日本語（複数バイトの UTF-8）でもバイト数で判定する。
    #[test]
    fn take_front_measures_utf8_bytes() {
        let queue = PlatformEventQueue::new(DEFAULT_EVENT_QUEUE_CAPACITY);
        queue.push("あ".into()); // 3 バイト
        assert_eq!(queue.take_front_if_fits(2), FrontTake::TooLarge(3));
        assert_eq!(queue.take_front_if_fits(3), FrontTake::Taken("あ".into()));
    }

    /// 別のスレッドから積んだものもすべて取り出せる（件数が合う）。
    #[test]
    fn accepts_pushes_from_other_threads() {
        const THREADS: usize = 4;
        const PER_THREAD: usize = 25;
        let queue = std::sync::Arc::new(PlatformEventQueue::new(THREADS * PER_THREAD));
        let handles: Vec<_> = (0..THREADS)
            .map(|thread| {
                let queue = std::sync::Arc::clone(&queue);
                std::thread::spawn(move || {
                    for index in 0..PER_THREAD {
                        queue.push(format!("t{thread}-{index}"));
                    }
                })
            })
            .collect();
        for handle in handles {
            handle.join().unwrap();
        }
        assert_eq!(queue.drain().len(), THREADS * PER_THREAD);
        assert_eq!(queue.dropped_count(), 0);
    }

    /// clear で中身を捨てても、捨てた数（上限による）は変わらない。
    #[test]
    fn clear_empties_without_counting_drops() {
        let queue = PlatformEventQueue::new(DEFAULT_EVENT_QUEUE_CAPACITY);
        queue.push("x".into());
        queue.clear();
        assert!(queue.is_empty());
        assert_eq!(queue.dropped_count(), 0);
    }
}
