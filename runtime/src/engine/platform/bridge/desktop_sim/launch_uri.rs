// ============================================================
//  platform/bridge/desktop_sim/launch_uri.rs — デスクトップの起動引数のディープリンク（W1-6）
//
//  PC の単体起動（SEED.exe）で --deep-link=<URI> を付けると、模擬の platform.launch_reason が kind = deep_link・uri = <URI> を返す
//  （Android の「VIEW＋data の Intent で起動した」の代わり。runtime/src/main.rs が App を作る前に set_desktop_launch_uri で預ける）。
//  付けなければ従来どおり launcher。プロセスで 1 回だけ預けられる（起動引数なので Play の区切りでは消さない）。
//  規則は Android の LaunchReason と同じ: 空でない・wire::MAX_URL_LENGTH 文字（Unicode の符号位置）以下（中身はアプリが検査する）。
// ============================================================

use std::sync::OnceLock;

use crate::engine::platform::bridge::wire::MAX_URL_LENGTH;

/// 起動引数で預かったディープリンク（無ければ空のまま）。
static PROCESS_LAUNCH_URI: OnceLock<String> = OnceLock::new();

/// 起動引数のディープリンクの規則（空でない・長すぎない）を確かめる。
///
/// # 戻り値
/// 合っていれば Ok(())、合わなければ Err(説明)
pub fn validate_launch_uri(uri: &str) -> Result<(), String> {
    if uri.is_empty() {
        return Err("ディープリンクの URI が空です".to_string());
    }
    let length = uri.chars().count();
    if length > MAX_URL_LENGTH {
        return Err(format!("ディープリンクの URI が長すぎます（{length} 文字 > {MAX_URL_LENGTH}）"));
    }
    Ok(())
}

/// 起動引数のディープリンクを預ける（main.rs から App を作る前に 1 回）。
///
/// # 戻り値
/// 預けたら Ok(())。規則に合わない・既に預けてあれば Err(説明。起動は続ける)
pub fn set_desktop_launch_uri(uri: &str) -> Result<(), String> {
    validate_launch_uri(uri)?;
    PROCESS_LAUNCH_URI.set(uri.to_string()).map_err(|_| "ディープリンクは既に預けてあります".to_string())
}

/// 預かったディープリンク（無ければ None。模擬を作るときに読む）。
pub fn process_launch_uri() -> Option<String> {
    PROCESS_LAUNCH_URI.get().cloned()
}

// ============================================================
//  ユニットテスト（プロセスで共有する置き場には触らず、規則だけを確かめる）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 空・長すぎは誤り。上限ちょうどは通る。
    #[test]
    fn launch_uri_rules() {
        assert!(validate_launch_uri("wakeorpay://alarm/x").is_ok());
        assert!(validate_launch_uri("").is_err());
        let prefix = "wakeorpay://";
        let fill = MAX_URL_LENGTH - prefix.chars().count();
        assert!(validate_launch_uri(&format!("{prefix}{}", "a".repeat(fill))).is_ok());
        assert!(validate_launch_uri(&format!("{prefix}{}", "a".repeat(fill + 1))).is_err());
    }
}
