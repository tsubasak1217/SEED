// ============================================================
//  background_gate.rs — アプリがバックグラウンドにいるかの共有状態と、その間の待機
//
//  【役割】
//  Android はアプリがバックグラウンドへ回ると描画サーフェスを破棄する（winit の suspended）。
//  メインのイベントループはそこで眠る（ControlFlow::Wait）が、物理スレッド（3D / 2D）などの
//  自前のスレッドは知らされないまま 60Hz で回り続け、端末の CPU と電池を使い続けていた。
//  ここに「今バックグラウンドか」を 1 か所だけ持ち、スレッド側はループの先頭で
//  `wait_while_background` を呼んで、バックグラウンドの間は条件変数で眠る。
//
//  【誰が切り替えるか】
//  App の suspended / 2 回目以降の resumed（app/background_lifecycle.rs）だけ。
//  デスクトップ（Windows）には suspended が届かないので、常に前面のまま（振る舞いは不変）。
//
//  【待ち方】
//  条件変数で眠り、前面へ戻った瞬間に起きる（再開の遅れが無い）。ただしスレッドへのコマンド
//  （停止・同期の問い合わせ）はバックグラウンド中も受け付ける必要があるため、上限時間
//  （呼び出し側が渡す）ごとに起きて呼び出し元のループへ戻す。前面の間は Atomic の読み取り 1 回だけ。
// ============================================================

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Condvar, Mutex};
use std::time::Duration;

/// バックグラウンド状態と、その変化を待つための条件変数の組。
///
/// プロセスで 1 つ（`GATE`）を使う。単体テストのために型として切り出してある。
pub struct BackgroundGate {
    /// バックグラウンドか（前面の間の速い判定用。本体は `state`）。
    in_background: AtomicBool,
    /// 条件変数と組になる本体（true＝バックグラウンド）。
    state: Mutex<bool>,
    /// 前面・背面が切り替わったことを待ち手へ知らせる。
    changed: Condvar,
}

impl BackgroundGate {
    /// 前面（バックグラウンドでない）状態で作る。
    pub const fn new() -> Self {
        Self {
            in_background: AtomicBool::new(false),
            state: Mutex::new(false),
            changed: Condvar::new(),
        }
    }

    /// 前面・背面を切り替え、待っているスレッドを起こす。
    ///
    /// # 引数
    /// * `background` - true＝バックグラウンドへ回った（suspended）/ false＝前面へ戻った（resumed）
    pub fn set_background(&self, background: bool) {
        // 毒されたロック（他スレッドが保持中に panic）でも bool の書き換えは安全なので続ける。
        let mut state = self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        *state = background;
        self.in_background.store(background, Ordering::Release);
        self.changed.notify_all();
    }

    /// 今バックグラウンドか。
    pub fn is_background(&self) -> bool {
        self.in_background.load(Ordering::Acquire)
    }

    /// バックグラウンドの間だけ眠る。
    ///
    /// # 引数
    /// * `max_wait` - 1 回に眠る上限（前面へ戻らなくても、この時間で呼び出し元へ戻る。
    ///                呼び出し元がコマンドを処理できるようにするため）
    ///
    /// # 戻り値
    /// 呼んだ時点でバックグラウンドだった（＝眠った）なら true。前面なら待たずに false。
    /// true を受け取った呼び出し元は、その周回の処理（物理ステップ等）を飛ばしてループの先頭へ戻る。
    pub fn wait_while_background(&self, max_wait: Duration) -> bool {
        if !self.is_background() {
            return false;
        }
        let state = self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        // 前面へ戻る（*background == false）か、上限時間が経つまで眠る。見かけ上の目覚め
        // （spurious wakeup）は wait_timeout_while が条件を見直して眠り直す。
        let _ = self
            .changed
            .wait_timeout_while(state, max_wait, |background| *background)
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        true
    }
}

impl Default for BackgroundGate {
    fn default() -> Self {
        Self::new()
    }
}

/// プロセス全体のバックグラウンド状態。
static GATE: BackgroundGate = BackgroundGate::new();

/// バックグラウンドへ回った（Android の suspended。App だけが呼ぶ）。
pub fn enter_background() {
    GATE.set_background(true);
}

/// 前面へ戻った（Android の 2 回目以降の resumed。App だけが呼ぶ）。
pub fn enter_foreground() {
    GATE.set_background(false);
}

/// 今バックグラウンドか。
pub fn is_background() -> bool {
    GATE.is_background()
}

/// バックグラウンドの間だけ眠る（戻り値・引数は `BackgroundGate::wait_while_background`）。
pub fn wait_while_background(max_wait: Duration) -> bool {
    GATE.wait_while_background(max_wait)
}

// ============================================================
//  ユニットテスト（プロセス共有の GATE は触らず、ローカルの BackgroundGate で確かめる。
//  並行に走る物理スレッドのテストを止めないため）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;
    use std::time::Instant;

    /// 眠る上限（前面へ戻らないときに呼び出し元へ戻るまで）。テストを遅くしない短さにする。
    const SHORT_WAIT: Duration = Duration::from_millis(30);

    /// 起こされるのを待つ上限（これより十分早く起きれば「前面へ戻った瞬間に起きた」とみなす）。
    const LONG_WAIT: Duration = Duration::from_secs(10);

    /// 前面の間は待たずに false（毎ループの判定が速いこと）。
    #[test]
    fn foreground_does_not_wait() {
        let gate = BackgroundGate::new();
        let started = Instant::now();
        assert!(!gate.wait_while_background(LONG_WAIT));
        assert!(started.elapsed() < LONG_WAIT / 2, "前面なのに待った");
    }

    /// バックグラウンドの間は上限時間まで眠って true（その周回のステップを飛ばす合図）。
    #[test]
    fn background_waits_up_to_max_wait() {
        let gate = BackgroundGate::new();
        gate.set_background(true);
        assert!(gate.is_background());
        let started = Instant::now();
        assert!(gate.wait_while_background(SHORT_WAIT));
        assert!(started.elapsed() >= SHORT_WAIT, "上限時間より早く戻った（前面へ戻っていないのに）");
    }

    /// 前面へ戻った瞬間に待っているスレッドが起きる（上限時間を待たない）。
    #[test]
    fn returning_to_foreground_wakes_waiter_immediately() {
        let gate = Arc::new(BackgroundGate::new());
        gate.set_background(true);

        let waiter = {
            let gate = Arc::clone(&gate);
            std::thread::spawn(move || {
                let started = Instant::now();
                let waited = gate.wait_while_background(LONG_WAIT);
                (waited, started.elapsed())
            })
        };
        // 待ち手が眠りに入るまで少し置いてから前面へ戻す（先に戻しても false で即戻るだけで結果は同じ）。
        std::thread::sleep(SHORT_WAIT);
        gate.set_background(false);

        let (waited, elapsed) = waiter.join().expect("待ち手のスレッドが panic した");
        assert!(elapsed < LONG_WAIT / 2, "前面へ戻したのに起きなかった: {elapsed:?}");
        // 眠りに入る前に前面へ戻っていれば false（待たずに戻る）。どちらでも上限は待たない。
        let _ = waited;
        assert!(!gate.is_background());
    }
}
