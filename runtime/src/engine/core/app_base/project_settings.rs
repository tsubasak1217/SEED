// ============================================================
//  project_settings.rs — `project_settings.json` を読む唯一の入口
//
//  【なぜ 1 本にまとめるのか】
//  以前は `app/app_init.rs` の 4 か所（解像度・プラグイン・シーンレジストリ・
//  グラフィックス設定）と `core/loader/async_loader.rs` の 1 か所が、
//  それぞれ `asset_fs::read_string` ＋ `serde_json::from_str::<Value>` で
//  同じファイルを直に読んでいた。版のマイグレーションのように
//  「読み込みの入口で必ず通す処理」を足すと、5 か所へ書き写すことになり必ず漏れる。
//  読み込みをここへ集約し、各呼び出し側は `load_text` / `load_value` を呼ぶだけにする。
//
//  【この層の責務】
//  - `asset_fs` 経由で読む（仮想パス・PAK・実フォルダのどれでも同じ経路）
//  - マイグレーション（版の判定・変換・未来版の拒否）を通す
//  - 読めない／壊れている場合は**既定へ落とすための空の結果**を返す
//    （プロジェクト設定が無いのは正常な運用。ここで起動を止めない）
//
//  【この層がやらないこと】
//  個々のキーの解釈。解像度・プラグイン・シーン一覧・グラフィックス設定の読み方は
//  それぞれの呼び出し側が持つ（1 ファイルに全部の意味を書かないため）。
// ============================================================

use serde_json::Value;

use crate::engine::asset_fs;
use crate::engine::core::migration::{self, FormatKind};

// ── 定数 ─────────────────────────────────────────────────────────

/// プロジェクト設定ファイルのアセット仮想パス。
pub const ASSET_PATH: &str = "assets://project_settings.json";

/// 読み込みに失敗したときに返すテキスト。
///
/// 空文字列は各 `parse_*` が「JSON として読めない → 既定値」へ落ちる合図であり、
/// 機構の導入前（`read_string` の失敗時に `unwrap_or_default()`）と同じ挙動になる。
const EMPTY_TEXT: &str = "";

// ── 公開 API ─────────────────────────────────────────────────────

/// 現行版へ変換済みのプロジェクト設定を JSON テキストとして返す。
///
/// テキストを受け取る解釈関数（`parse_window_size` / `parse_game_name` /
/// `parse_streaming_config` など）のための入口。
/// ファイルが無い・壊れている・未来版のときは**空文字列**を返す
/// （呼び出し側はそのまま既定値へ落ちる）。
///
/// 返るのは変換後の値を直列化したテキストなので、元ファイルの整形・欄の並びは保たれない。
/// キーを引いて読む用途しか無いため問題にならない。
pub fn load_text() -> String {
    match load_migrated() {
        Some(v) => serde_json::to_string(&v).unwrap_or_else(|_| EMPTY_TEXT.to_string()),
        None => EMPTY_TEXT.to_string(),
    }
}

/// 現行版へ変換済みのプロジェクト設定を `Value` として返す。
///
/// キーを直接引く呼び出し側（プラグイン一覧・シーンレジストリ・グラフィックス設定）用。
/// ファイルが無い・壊れている・未来版のときは**空のオブジェクト**を返すので、
/// 呼び出し側は `v["key"]` をそのまま書ける（`Value::Null` が返り既定へ落ちる）。
pub fn load_value() -> Value {
    load_migrated().unwrap_or_else(|| Value::Object(serde_json::Map::new()))
}

// ── 内部 ─────────────────────────────────────────────────────────

/// ファイルを読み、現行版へ変換した `Value` を返す。読めなければ `None`。
///
/// 失敗の種類でログの出し方を変える:
/// - ファイルが無い   … 正常な運用（未設定のプロジェクト）なので黙って `None`
/// - 版・解析の失敗   … 設定が丸ごと既定へ落ちる重い結果なので 1 行警告する
fn load_migrated() -> Option<Value> {
    let text = asset_fs::read_string(ASSET_PATH).ok()?;
    match migration::load_json::<Value>(FormatKind::ProjectSettings, &text) {
        Ok(v) => Some(v),
        Err(e) => {
            eprintln!("[SEED settings] {ASSET_PATH} を読めません（既定値で続行）: {e}");
            None
        }
    }
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// アセット層が未初期化（テスト環境）でも落ちず、既定へ落ちる結果を返すこと。
    ///
    /// 実ファイルを読む経路は結合テストの領分なので、ここでは
    /// 「読めないときに呼び出し側が期待する形（空文字列・空オブジェクト）を返す」
    /// ことだけを固定する。
    #[test]
    fn falls_back_to_empty_results_when_unreadable() {
        // asset_fs が未初期化でも panic しない（読めなければ None 経由で既定へ）。
        let text = load_text();
        let value = load_value();
        // 読めた場合も読めなかった場合も、呼び出し側が扱える形であること。
        assert!(text.is_empty() || serde_json::from_str::<Value>(&text).is_ok());
        assert!(value.is_object(), "Value は常にオブジェクトであること: {value}");
    }
}
