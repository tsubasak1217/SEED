// ============================================================
//  physics/background_pause.rs — アプリがバックグラウンドの間、物理スレッドを止める（3D / 2D 共通）
//
//  【役割】
//  物理スレッド（thread.rs / thread2d.rs）はループの先頭（コマンドを捌いた直後）で
//  `BackgroundPause::sleep_if_background` を呼ぶ。アプリがバックグラウンド（Android の suspended〜resumed。
//  core::background_gate）の間は物理ステップを進めず、条件変数で眠る。前面へ戻った瞬間に起きて再開する。
//  止まった・再開したは 1 回ずつログに残す（端末で「背面で本当に止まっているか」を確かめるため）。
//
//  【Pause（タイムライン停止・スクリプトのポーズ）との違い】
//  Pause/Resume はゲーム側の状態でスレッドごとのコマンドとして届く。こちらはアプリの前面・背面で、
//  両者は独立している（背面から戻っても、ゲームがポーズ中ならポーズのまま）。
//
//  【デスクトップ】suspended が届かないので常に前面。毎ループ Atomic の読み取り 1 回だけで素通りする。
// ============================================================

use std::time::{Duration, Instant};

use crate::engine::core::background_gate;

/// バックグラウンド中に 1 回眠る上限（前面へ戻らなくてもこの間隔で起きてコマンドを捌く）。
///
/// バックグラウンド中でもスレッドの停止（Drop の Stop）や同期の問い合わせには応答する必要がある。
/// 前面へ戻ったときは条件変数ですぐ起きるので、この値は再開の遅れには効かない
/// （効くのはバックグラウンド中のコマンド応答の遅れの上限と、1 秒あたりの起床回数＝約 4 回）。
const BACKGROUND_COMMAND_POLL: Duration = Duration::from_millis(250);

/// ログの行頭（logcat で grep する印）。
const LOG_TAG: &str = "[SEED PHYSICS]";

/// 物理スレッド 1 本ぶんの「背面で止まっているか」の状態（ログを切り替わりの 1 回だけにするため）。
pub(super) struct BackgroundPause {
    /// ログに出すスレッドの種類（"3D" / "2D"）。
    label: &'static str,
    /// 背面で止まり始めた時刻（前面で動いている間は None）。
    paused_since: Option<Instant>,
}

impl BackgroundPause {
    /// 前面で動いている状態で作る。
    ///
    /// # 引数
    /// * `label` - ログに出すスレッドの種類（"3D" / "2D"）
    pub(super) fn new(label: &'static str) -> Self {
        Self { label, paused_since: None }
    }

    /// アプリがバックグラウンドの間だけ眠る。
    ///
    /// # 戻り値
    /// 眠った（＝バックグラウンドだった）なら true。呼び出し元はその周回の物理ステップを飛ばし、
    /// 次ステップ時刻を今へ合わせ直してからループの先頭（コマンド処理）へ戻る
    /// （背面にいた時間ぶんを取り戻そうとステップを連発しないため）。前面なら待たずに false。
    pub(super) fn sleep_if_background(&mut self) -> bool {
        if !background_gate::is_background() {
            if let Some(since) = self.paused_since.take() {
                eprintln!(
                    "{LOG_TAG} {} 物理スレッド: 前面へ戻ったのでステップを再開します（止めていた時間 {:.1} 秒）",
                    self.label,
                    since.elapsed().as_secs_f64()
                );
            }
            return false;
        }
        if self.paused_since.is_none() {
            self.paused_since = Some(Instant::now());
            eprintln!(
                "{LOG_TAG} {} 物理スレッド: アプリがバックグラウンドのためステップを止めて眠ります",
                self.label
            );
        }
        background_gate::wait_while_background(BACKGROUND_COMMAND_POLL);
        true
    }
}
