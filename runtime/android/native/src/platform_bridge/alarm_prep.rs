// ============================================================
//  platform_bridge/alarm_prep.rs — alarm.schedule を Java へ送る前の前処理（音源の書き出し。W1-3）
//
//  【なぜここか】目覚ましの鳴動（W1-4）は Java だけの :seed_platform で動き、エンジンの pak・APK の中のアセットを読めない。
//  そこでスクリプトが "assets://…" の音を渡したら、メインプロセス（このエンジン）が予約を送る前にアセットのバイト列を
//  :seed_platform が読める実ファイル（端末保護ストレージの files/seed_platform/sounds/<内容のハッシュ>.<拡張子>）へ書き、
//  予約の sound_asset を sound_path（絶対パス）に置き換える。中身の処理はエンジンの
//  engine::platform::bridge::alarm::sound_export（単体テスト付き）で、ここは「書き出し先を 1 回だけ教わる」と「ログ」だけ。
//
//  【書き出し先】:seed_platform の platform.paths（Java の CorePlatformModule）が返す sounds_dir を、最初に要るとき 1 回だけ
//  取り、プロセスの間ずっと持つ（アプリの置き場は変わらない）。つないでいる途中（connecting）なら予約も送らずに
//  その返答を返す（予約も同じく connecting で失敗する。スクリプトは platform.connected の後に呼び直す＝音源も書き出される）。
//  それ以外の理由で取れなければ（古い APK で paths が無い等）既定の音で予約する（鳴らないより既定の音で鳴るほうがよい）。
// ============================================================

use std::path::PathBuf;
use std::sync::OnceLock;

use seed_engine::engine::asset_fs;
use seed_engine::engine::platform::bridge::alarm::sound_export::{self, PathsReply};
use seed_engine::engine::platform::bridge::{wire, LOG_PREFIX};

use super::java_bridge;
use crate::logcat;

/// 音源の書き出し先（:seed_platform の platform.paths の sounds_dir。最初に取れたときに入れる）。
static SOUNDS_DIR: OnceLock<PathBuf> = OnceLock::new();

/// 前処理の結果。
pub enum Prepared {
    /// この JSON を Java へ送る。
    Send(String),
    /// 送らずにこの返答をそのまま返す（書き出し先を聞く呼び出しが connecting だった）。
    Reply(String),
}

/// alarm.schedule の JSON を、Java へ送る形にする（音源を書き出して sound_path に入れる）。
///
/// # 引数
/// * `json` - スクリプトからの予約の JSON
pub fn prepare_schedule(json: &str) -> Prepared {
    let sounds_dir = if sound_export::requires_export(json) {
        match sounds_dir() {
            Ok(dir) => Some(dir),
            Err(Some(connecting_reply)) => return Prepared::Reply(connecting_reply),
            Err(None) => None,
        }
    } else {
        None
    };
    let prepared = sound_export::prepare_schedule_json(json, sounds_dir.as_deref(), asset_fs::read_bytes);
    if let Some(warning) = &prepared.warning {
        logcat::warn(&format!("{LOG_PREFIX} {warning}"));
    } else if sounds_dir.is_some() {
        logcat::info(&format!("{LOG_PREFIX} 目覚ましの音源を書き出しました: {}", prepared.sound_path));
    }
    Prepared::Send(prepared.json)
}

/// 書き出し先（初めてなら :seed_platform の platform.paths で聞く）。
///
/// # 戻り値
/// Ok(書き出し先)。つないでいる途中なら Err(Some(その返答))、ほかの理由で取れなければ Err(None)（警告を出した）
fn sounds_dir() -> Result<PathBuf, Option<String>> {
    if let Some(dir) = SOUNDS_DIR.get() {
        return Ok(dir.clone());
    }
    let reply = match java_bridge::invoke(wire::MODULE_PLATFORM, wire::METHOD_PATHS, "{}") {
        Ok(reply) => reply,
        Err(reason) => {
            logcat::warn(&format!("{LOG_PREFIX} 目覚ましの音源の書き出し先を聞けませんでした（既定の音にします）: {reason}"));
            return Err(None);
        }
    };
    match sound_export::parse_paths_reply(&reply) {
        PathsReply::SoundsDir(dir) => {
            logcat::info(&format!("{LOG_PREFIX} 目覚ましの音源の書き出し先: {}", dir.display()));
            Ok(SOUNDS_DIR.get_or_init(|| dir).clone())
        }
        PathsReply::Connecting => Err(Some(reply)),
        PathsReply::Failed(reason) => {
            logcat::warn(&format!("{LOG_PREFIX} 目覚ましの音源の書き出し先が分かりません（既定の音にします）: {reason}"));
            Err(None)
        }
    }
}
