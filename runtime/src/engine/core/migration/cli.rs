// ============================================================
//  cli.rs — マイグレーションのコマンドライン入口（1 本にまとめる）
//
//  【役割】
//  `main` から呼ばれるのはこの 1 本だけにする。サブコマンドが増えるたびに
//  `main.rs` へ分岐を足していくと、「ウィンドウも GPU も作らずに終わる」という
//  この入口の性質（＝エディタ起動中でも安全に走らせられる）が守られているか
//  main を読まないと分からなくなる。
//
//  【提供するサブコマンド】
//  | 引数 | 内容 |
//  |------|------|
//  | `--upgrade-project <パス> [--dry-run]` | プロジェクト配下を一括アップグレードする |
//  | `--migrate-json <kind>` | 標準入力の JSON 1 件を現行版へ変換して標準出力へ返す |
//
//  どちらも該当する引数が無ければ `None` を返し、通常起動へ進む。
// ============================================================

use super::{migrate_json, upgrade};

/// マイグレーション系のサブコマンドが要求されていれば実行し、終了コードを返す。
///
/// 該当する引数が無ければ `None`（通常起動を続ける合図）。
///
/// **この経路はウィンドウも GPU も初期化しない**。描画資源を一切作らないので、
/// エディタが起動中の環境でも安全に走らせられる。
pub fn run_if_requested(args: &[String]) -> Option<i32> {
    // 一括アップグレード（プロジェクト配下のファイルを書き換える）。
    if let Some(code) = upgrade::run_cli_if_requested(args) {
        return Some(code);
    }
    // 標準入出力 1 件の変換（ファイルには一切触らない）。
    if let Some(code) = migrate_json::run_cli_if_requested(args) {
        return Some(code);
    }
    None
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// マイグレーションと無関係な引数では何もしないこと（通常起動を邪魔しない）。
    #[test]
    fn ignores_unrelated_arguments() {
        let args = vec![
            "SEED.exe".to_string(),
            "--mode=play".to_string(),
            "--parent-hwnd=1234".to_string(),
        ];
        assert_eq!(run_if_requested(&args), None);
        // 引数が exe 名だけのとき（ダブルクリック起動）も何もしない
        assert_eq!(run_if_requested(&["SEED.exe".to_string()]), None);
    }
}
