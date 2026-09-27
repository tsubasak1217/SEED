// ============================================================
//  physics/result_backlog.rs — 物理の結果の待ち行列に上限を設ける（W2-10a）
//
//  【なぜ要るか】
//  物理のスレッド（3D・2D）は 1 ステップごと（60 Hz）に結果をメインのスレッドへ送り、メインはフレームの頭で
//  recv_latest（最新の 1 件だけを使い、古いものは捨てる）で取り出す。フレームが回らない間（render_policy の on_demand で
//  描画を止めている間。W2-10a）は誰も取り出さないので、上限が無いと待ち行列が 1 時間で 21 万件（3D と 2D で倍）と
//  際限なく伸び、起きた最初のフレームの recv_latest がそれを全部たどる。
//
//  【やり方】送る側で「上限に届いていたら古いものから捨ててから送る」。受け手は最新の 1 件しか使わないので、
//  捨てても受け手が見る結果は変わらない。毎フレーム取り出す通常の実行（continuous）では待ち行列は 1〜2 件で、上限に届かない。
//  物理のスレッドを止めるか（背面の background_pause と同じ扱いにするか）は W2-10 で決める（今は進め続ける）。
// ============================================================

use crossbeam_channel::{Receiver, Sender};

/// 待ち行列に残す結果の上限（60 Hz で 2 秒ぶん）。フレームが 2 秒以上取り出さないときだけ効く。
pub const MAX_PENDING_RESULTS: usize = 120;

/// ログの印（物理のスレッドのログ）。
const LOG_PREFIX: &str = "[SEED PHYSICS]";

/// 上限つきで結果を送る口（物理のスレッドが持つ）。
pub struct ResultBacklog<T> {
    /// 結果の送り口。
    tx: Sender<T>,
    /// 同じ待ち行列の受け口の写し（上限に届いたとき古いものを捨てるためだけに使う）。
    trim: Receiver<T>,
    /// 待ち行列に残す上限（1 以上）。
    max_pending: usize,
    /// ログに出す名前（"3D" / "2D"）。
    label: &'static str,
    /// 捨てた数（試験・ログ用）。
    dropped: u64,
    /// 捨て始めたことをログへ出したか（1 度だけ出す）。
    logged: bool,
}

impl<T> ResultBacklog<T> {
    /// 口を作る。
    ///
    /// # 引数
    /// * `tx`          - 結果の送り口
    /// * `trim`        - 同じ待ち行列の受け口の写し（`Receiver::clone`）
    /// * `max_pending` - 待ち行列に残す上限（0 は 1 とみなす）
    /// * `label`       - ログに出す名前（"3D" / "2D"）
    pub fn new(tx: Sender<T>, trim: Receiver<T>, max_pending: usize, label: &'static str) -> Self {
        Self { tx, trim, max_pending: max_pending.max(1), label, dropped: 0, logged: false }
    }

    /// 結果を送る。待ち行列が上限に届いていれば、古いものから捨ててから送る。
    ///
    /// # 戻り値
    /// 送れたら true。受け手が居なければ false（物理のスレッドは従来どおり結果を捨てて続ける）。
    pub fn send(&mut self, result: T) -> bool {
        while self.trim.len() >= self.max_pending {
            match self.trim.try_recv() {
                Ok(_) => self.dropped += 1,
                // 受け手が同時に取り出して空になった
                Err(_) => break,
            }
        }
        if self.dropped > 0 && !self.logged {
            // 描画を止めている間などフレームが取り出さないときだけ起きる（通常の実行では出ない）
            eprintln!(
                "{LOG_PREFIX} {}: フレームが結果を取り出さないので、古い結果から捨て始めました（待ち行列の上限 {} 件・捨てた数 {}）",
                self.label,
                self.max_pending,
                self.dropped(),
            );
            self.logged = true;
        }
        self.tx.send(result).is_ok()
    }

    /// 上限に届いて捨てた数。
    pub fn dropped(&self) -> u64 {
        self.dropped
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crossbeam_channel::unbounded;

    /// ログの名前（試験用）。
    const LABEL: &str = "test";

    /// 取り出されない間は上限で止まり、受け手が見る最新の 1 件は変わらない。
    #[test]
    fn backlog_is_capped_and_latest_survives() {
        /// 上限（試験用）。
        const CAP: usize = 3;
        /// 送る数。
        const SENDS: u32 = 10;
        let (tx, rx) = unbounded::<u32>();
        let mut backlog = ResultBacklog::new(tx, rx.clone(), CAP, LABEL);
        for i in 1..=SENDS {
            assert!(backlog.send(i));
            assert!(rx.len() <= CAP, "上限を超えた: {}", rx.len());
        }
        assert_eq!(backlog.dropped(), u64::from(SENDS) - CAP as u64);
        // recv_latest と同じ取り出し方で、最後に送ったものが見える
        let latest = std::iter::from_fn(|| rx.try_recv().ok()).last();
        assert_eq!(latest, Some(SENDS));
    }

    /// 毎回取り出している間は捨てない（通常の実行の振る舞いは変わらない）。
    #[test]
    fn draining_receiver_never_drops() {
        let (tx, rx) = unbounded::<u32>();
        let mut backlog = ResultBacklog::new(tx, rx.clone(), MAX_PENDING_RESULTS, LABEL);
        for i in 0..1_000 {
            backlog.send(i);
            assert_eq!(rx.try_recv().ok(), Some(i));
        }
        assert_eq!(backlog.dropped(), 0);
    }

    /// 上限 0 は 1 とみなす（待ち行列は最新の 1 件だけ）。
    #[test]
    fn zero_cap_keeps_only_latest() {
        let (tx, rx) = unbounded::<u32>();
        let mut backlog = ResultBacklog::new(tx, rx.clone(), 0, LABEL);
        assert!(backlog.send(1));
        assert!(backlog.send(2));
        assert_eq!(rx.len(), 1, "上限 0 は 1");
        assert_eq!(rx.try_recv().ok(), Some(2));
    }
}
