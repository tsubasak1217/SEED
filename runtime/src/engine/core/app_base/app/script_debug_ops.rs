// ============================================================
//  script_debug_ops.rs — デバッグコマンド IPC（SCRIPT_DEBUG）のアプリ側ハンドラ
//
//  エディタ（MCP 経由の AI）から届いた `SCRIPT_DEBUG:{name},{arg}` を
//  スクリプト側の待ち行列へ積み、IPC で応答を返す。
//
//  【責務】
//  - 受理可否の判定（Play 中か）
//  - 待ち行列（scripting::debug_command）への投入と応答
//
//  「コマンドが何をするか」は一切知らない。名前と引数を運ぶだけで、
//  意味づけは C# 側（SEED.Debug.OnCommand に登録されたハンドラ）が持つ。
//  これは入力注入（input_inject_ops）と同じ責務分担で、
//  ランタイムにゲーム固有の知識を持ち込まないための線引き。
// ============================================================

use crate::engine::core::app_base::app::RuntimeMode;
use crate::engine::core::scripting::debug_command;

use super::App;

/// 受理したときの応答。
pub const SCRIPT_DEBUG_REPLY_OK: &str = "SCRIPT_DEBUG_OK";

/// 拒否したときの応答の接頭辞（この後ろに理由が付く）。
pub const SCRIPT_DEBUG_REPLY_ERROR_PREFIX: &str = "SCRIPT_DEBUG_ERROR:";

/// 拒否理由: Play 中でない（Edit 中はスクリプトが走っていないため受け付けない）。
pub const SCRIPT_DEBUG_ERROR_NOT_PLAYING: &str = "not_playing";

impl App {
    /// `SCRIPT_DEBUG:{name},{arg}` を 1 件処理し、必ず 1 つの応答を返す。
    ///
    /// Play 中でなければ積まずに拒否する（Edit 中に積むと、次に Play したときに
    /// 意図しないコマンドが突然走るため）。
    pub(super) fn handle_script_debug(&mut self, name: String, arg: String) {
        if self.mode != RuntimeMode::Play {
            self.reply_script_debug_error(SCRIPT_DEBUG_ERROR_NOT_PLAYING);
            return;
        }

        debug_command::push(name, arg);
        self.reply_script_debug_ok();
    }

    /// 溜まっているデバッグコマンドを捨てる（Play の開始／停止で持ち越さないため）。
    pub(super) fn clear_script_debug_commands(&mut self) {
        debug_command::clear();
    }

    /// `SCRIPT_DEBUG_OK` を返す。
    fn reply_script_debug_ok(&self) {
        if let Some(ipc) = &self.ipc {
            ipc.send(SCRIPT_DEBUG_REPLY_OK);
        }
    }

    /// `SCRIPT_DEBUG_ERROR:{reason}` を返す。
    fn reply_script_debug_error(&self, reason: &str) {
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("{SCRIPT_DEBUG_REPLY_ERROR_PREFIX}{reason}"));
        }
    }
}
