// ============================================================
//  input_inject_ops.rs — 入力注入 IPC のアプリ側ハンドラ
//
//  エディタ（MCP 経由の AI）から届いた `INPUT_*` コマンドを、
//  ランタイムの `Input` へ橋渡しし、IPC で応答を返す。
//
//  【責務】
//  - 受理可否の判定（Play 中か）
//  - `Input` への適用と応答（INPUT_OK / INPUT_ERROR:{reason}）
//  - 毎フレームのシーケンス進行と完走通知（INPUT_SEQUENCE_DONE）
//
//  注入状態そのものは `Input`（engine::core::input::inject）が持つ。
//  App 側に状態フィールドを増やしていないのは、
//  「入力の状態は Input が一元管理する」という既存の責務分担を守るため。
// ============================================================

use crate::engine::core::app_base::app::RuntimeMode;
use crate::engine::core::input::inject::{
    InjectCommand, INJECT_ERROR_NOT_PLAYING, INJECT_ERROR_SEQUENCE_BUSY,
    INJECT_REPLY_ERROR_PREFIX, INJECT_REPLY_OK, INJECT_REPLY_SEQUENCE_DONE,
};

use super::App;

impl App {
    // ============================================================
    //  IPC ハンドラ
    // ============================================================

    /// `INPUT_*` コマンド 1 件を処理し、必ず 1 つの応答を返す。
    ///
    /// 応答は `INPUT_OK` か `INPUT_ERROR:{reason}` のいずれか。
    /// シーケンスの完走通知（`INPUT_SEQUENCE_DONE`）だけは非同期で
    /// `tick_input_injection` から送られる。
    pub(super) fn handle_input_inject(&mut self, cmd: InjectCommand) {
        // ── Play 中でなければ一律で拒否する ────────────────────────────
        //   Edit 中に注入すると、エディタ操作（ギズモ・カメラ）と競合して
        //   誰が入力しているのか分からない状態になるため。
        if self.mode != RuntimeMode::Play {
            self.reply_input_error(INJECT_ERROR_NOT_PLAYING);
            return;
        }

        match cmd {
            // 解釈できなかったコマンド。理由をそのまま返す（黙って捨てない）。
            InjectCommand::Invalid(reason) => self.reply_input_error(&reason),

            InjectCommand::Action(action) => {
                self.input.apply_injected_action(action);
                self.reply_input_ok();
            }

            InjectCommand::ReleaseAll => {
                self.input.release_injected_input();
                self.reply_input_ok();
            }

            InjectCommand::Sequence(player) => {
                if self.input.start_injected_sequence(player) {
                    self.reply_input_ok();
                } else {
                    // 多重再生は受け付けない。完走通知（INPUT_SEQUENCE_DONE）を
                    // 待ってから次を送る運用にすることで、どのシーケンスが
                    // 走っているのかが常に一意に決まる。
                    self.reply_input_error(INJECT_ERROR_SEQUENCE_BUSY);
                }
            }
        }
    }

    // ============================================================
    //  毎フレームの進行
    // ============================================================

    /// 毎フレーム 1 回、IPC 消化の直後に呼ぶ。
    ///
    /// - 再生中シーケンスを**実時間**で進める（Play 一時停止中は進めない）。
    /// - Play から抜けた瞬間、注入中の押下をすべて解放する（安全弁）。
    /// - シーケンスが完走したら `INPUT_SEQUENCE_DONE` を送る。
    pub(super) fn tick_input_injection(&mut self) {
        // Play から抜けた瞬間だけ全解放し、一時停止では時計を止めるだけにする。
        // 一時停止中に時計を進めると、再開した瞬間に溜まったイベントが
        // 1 フレームへ雪崩れ込み、意図した操作にならない。
        let playing = self.mode == RuntimeMode::Play;
        let outcome = self.input.tick_injection(playing, self.paused);

        if outcome.sequence_finished {
            if let Some(ipc) = &self.ipc {
                ipc.send(INJECT_REPLY_SEQUENCE_DONE);
            }
        }
    }

    // ============================================================
    //  応答ヘルパー
    // ============================================================

    /// `INPUT_OK` を返す。
    fn reply_input_ok(&self) {
        if let Some(ipc) = &self.ipc {
            ipc.send(INJECT_REPLY_OK);
        }
    }

    /// `INPUT_ERROR:{reason}` を返す。
    ///
    /// reason はパース層で 1 行・長さ上限まで整形済み（`sanitize_reason`）。
    fn reply_input_error(&self, reason: &str) {
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("{INJECT_REPLY_ERROR_PREFIX}{reason}"));
        }
    }
}
