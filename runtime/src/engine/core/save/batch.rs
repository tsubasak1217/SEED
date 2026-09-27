// ============================================================
//  save/batch.rs — SaveData.Batch の状態（入れ子の深さと、待たせた書き出しの要求）
//
//  【役割】
//  スクリプトが複数のキーを 1 まとまりで書き換えている間（`SaveData.Batch(() => { … })` の間）、
//  書き出しの要求（明示の Save・自動保存）をディスクへ書かせずに「要求があった」ことだけを覚え、
//  最も外側の Batch の終わりに 1 回だけ書かせるための状態機械。書き出しそのものは store.rs が行う。
//
//  【なぜストアと同じ Mutex に入れるか】
//  Android の `MainActivity.onDestroy` は UI スレッドから `nativeFlushSaveData`（自動保存）を呼ぶ。
//  スクリプトは android_main のスレッドでキーを順に書き換えるので、Batch の深さがストアとは別の場所に
//  あると「深さを見た直後に書き換えが進む」隙間ができる。SaveStore がこの状態を持ち、ストアの Mutex の中で
//  深さを見て書く／待たせるを決めるので、Batch の途中の半端な組み合わせはディスクに書かれない。
//
//  【割り切り】（docs/scripting_api.md §7.7）
//  - Batch は取り消し（ロールバック）をしない。中で例外が出ても、それまでに書き換えたキーは戻らない。
//  - Batch の途中にプロセスが終わる（onDestroy の書き出しが待たされたまま終了する）と、Batch より前の
//    未書き出しの変更も書かれない（ディスクは前回の書き出しのまま＝半端な状態にはならない）。
// ============================================================

/// Batch の間に来た書き出しの要求の強さ（強いほうへまとめる）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Default)]
pub enum PendingFlush {
    /// 要求は無い。
    #[default]
    None,
    /// 自動保存の要求（未書き出しの変更があるときだけ書く）。
    IfDirty,
    /// 明示の Save の要求（変更が無くても書く）。
    Always,
}

/// Batch を 1 段終えた結果。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BatchEnd {
    /// Batch が開いていなかった（Begin と End の数が合わない）。何もしない。
    NotOpen,
    /// まだ外側の Batch が開いている（書き出しは外側の終わりまで待つ）。
    StillOpen,
    /// 最も外側の Batch が閉じた。待たせていた書き出しの要求を返す（呼び出し元が書く）。
    Closed(PendingFlush),
}

/// Batch の深さと、待たせている書き出しの要求。
#[derive(Debug, Default)]
pub struct BatchState {
    /// 開いている Batch の数（入れ子は数える）。0 なら Batch の外。
    depth: u32,
    /// Batch の間に来た書き出しの要求（最も強いもの）。
    pending: PendingFlush,
}

impl BatchState {
    /// Batch の途中か。
    pub fn is_open(&self) -> bool {
        self.depth > 0
    }

    /// 開いている Batch の数（診断・テスト用）。
    #[allow(dead_code)] // 現状はユニットテストからのみ使う診断アクセサ
    pub fn depth(&self) -> u32 {
        self.depth
    }

    /// Batch を 1 段始める（入れ子は数える。上限に達しても溢れさせない）。
    pub fn begin(&mut self) {
        self.depth = self.depth.saturating_add(1);
    }

    /// Batch の途中に来た書き出しの要求を覚える（強いほうを残す）。
    pub fn defer(&mut self, request: PendingFlush) {
        self.pending = self.pending.max(request);
    }

    /// Batch を 1 段終える。最も外側が閉じたら、待たせていた要求を取り出して返す。
    pub fn end(&mut self) -> BatchEnd {
        if self.depth == 0 {
            return BatchEnd::NotOpen;
        }
        self.depth -= 1;
        if self.depth > 0 {
            return BatchEnd::StillOpen;
        }
        BatchEnd::Closed(std::mem::take(&mut self.pending))
    }
}

// ============================================================
//  ユニットテスト（書き出しを伴う試験は store.rs）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 入れ子は数え、最も外側の終わりで要求を 1 回だけ返す。
    #[test]
    fn nested_batches_release_pending_once_at_outermost_end() {
        let mut state = BatchState::default();
        state.begin();
        state.begin();
        assert_eq!(state.depth(), 2);
        state.defer(PendingFlush::IfDirty);
        assert_eq!(state.end(), BatchEnd::StillOpen);
        assert!(state.is_open());
        assert_eq!(state.end(), BatchEnd::Closed(PendingFlush::IfDirty));
        assert!(!state.is_open());
        // 要求は取り出されて消える（次の Batch へ持ち越さない）
        state.begin();
        assert_eq!(state.end(), BatchEnd::Closed(PendingFlush::None));
    }

    /// 明示の Save の要求は自動保存より強く、後から弱い要求が来ても弱まらない。
    #[test]
    fn stronger_request_wins() {
        let mut state = BatchState::default();
        state.begin();
        state.defer(PendingFlush::Always);
        state.defer(PendingFlush::IfDirty);
        assert_eq!(state.end(), BatchEnd::Closed(PendingFlush::Always));
    }

    /// 開いていない Batch を終えても何も起きない（深さは負にならない）。
    #[test]
    fn end_without_begin_is_ignored() {
        let mut state = BatchState::default();
        assert_eq!(state.end(), BatchEnd::NotOpen);
        assert_eq!(state.depth(), 0);
    }
}
