// ============================================================
//  launch_kind.rs — 起動形態（配布パッケージ実行かどうか）の判定
//
//  【役割】
//  「今の起動が配布物（パッケージ版）としての起動か、エディタ／開発ビルドからの起動か」を
//  main() の最初期＝アセット層（`asset_fs`）の初期化より前に決める。
//
//  【なぜ asset_fs::is_packaged() を使えないか】
//  `asset_fs` の初期化は `App::handle_resumed`（ウィンドウ生成時）まで走らない。
//  一方、起動ログのリダイレクトはそれより遥かに手前（main の 1 行目）で必要になる。
//  そのため「exe の隣に assets.pak があるか」という **同じ規則** をここで独立に評価する。
//  規則は `app_init.rs::init_asset_fs` と一致させること（片方だけ変えると挙動がずれる）。
// ============================================================

use std::path::Path;

// ── 起動引数の接頭辞（エディタが渡すもの）───────────────────────────
/// アセットルートの明示指定。エディタ起動（編集／埋め込み Play）でのみ渡される。
const ASSETS_ROOT_ARG_PREFIX: &str = "--assets-root=";
/// IPC 名前付きパイプ名。エディタと接続する起動でのみ渡される。
const PIPE_ARG_PREFIX: &str = "--pipe=";

/// 配布パッケージに同梱されるアセットアーカイブのファイル名（exe の隣に置かれる）。
pub const PAK_FILE_NAME: &str = "assets.pak";

/// 起動形態。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LaunchKind {
    /// 配布パッケージとしての単体起動（エディタ引数が無く、exe の隣に `assets.pak` がある）。
    /// この場合だけログファイルへのリダイレクトと panic ダイアログを有効にする。
    Packaged,
    /// エディタからの起動、または開発ビルドの単体起動（`cargo run` など）。
    /// 標準エラーはエディタの Output パネル／コンソールへ流すのが正しいので、何も変えない。
    Editor,
}

/// 起動形態を決める【純関数】。
///
/// # 引数
/// * `args` — `std::env::args()` 相当（先頭は実行ファイルパス。判定では読み飛ばす）
/// * `pak_exists` — exe の隣に `assets.pak` が存在するか（呼び出し側がファイル系で確かめた結果）
///
/// # 判定
/// エディタ引数（`--assets-root=` / `--pipe=`）が 1 つでもあれば `Editor`。
/// そうでなく `assets.pak` があれば `Packaged`、無ければ `Editor`（開発時の `cargo run`）。
pub fn decide_launch_kind(args: &[String], pak_exists: bool) -> LaunchKind {
    // args[0] は実行ファイル自身のパス。パスに `--pipe=` などが含まれる可能性を排除するため
    // 判定対象から外す（実害はまず無いが、判定条件を引数だけに限定しておく）。
    let has_editor_arg = args.iter().skip(1).any(|a| {
        a.starts_with(ASSETS_ROOT_ARG_PREFIX) || a.starts_with(PIPE_ARG_PREFIX)
    });

    if has_editor_arg {
        return LaunchKind::Editor;
    }
    if pak_exists {
        LaunchKind::Packaged
    } else {
        LaunchKind::Editor
    }
}

/// 実環境（プロセス引数と実行ファイルの隣）を見て起動形態を決める。
///
/// `exe_dir` は実行ファイルのあるフォルダ。取得できなかった場合は `None` を渡すと
/// 「`assets.pak` は無い」＝ `Editor` 扱いになる（安全側＝何も変更しない側へ倒す）。
pub fn detect_launch_kind(args: &[String], exe_dir: Option<&Path>) -> LaunchKind {
    let pak_exists = exe_dir
        .map(|dir| dir.join(PAK_FILE_NAME).is_file())
        .unwrap_or(false);
    decide_launch_kind(args, pak_exists)
}

// ============================================================
//  単体テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用に `Vec<String>` の引数列を作る（先頭に exe パスを自動で置く）。
    fn args(rest: &[&str]) -> Vec<String> {
        let mut v = vec![r"C:\game\SEED.exe".to_string()];
        v.extend(rest.iter().map(|s| s.to_string()));
        v
    }

    #[test]
    fn packaged_when_no_editor_args_and_pak_present() {
        assert_eq!(decide_launch_kind(&args(&[]), true), LaunchKind::Packaged);
    }

    #[test]
    fn editor_when_pak_missing() {
        // 開発ビルドの `cargo run`（PAK も無くエディタ引数も無い）
        assert_eq!(decide_launch_kind(&args(&[]), false), LaunchKind::Editor);
    }

    #[test]
    fn editor_when_assets_root_given_even_with_pak() {
        // エディタからの起動は PAK があっても必ず Editor（Output パネルへ流し続ける）
        assert_eq!(
            decide_launch_kind(&args(&[r"--assets-root=C:\SEED\runtime\assets"]), true),
            LaunchKind::Editor
        );
    }

    #[test]
    fn editor_when_pipe_given_even_with_pak() {
        assert_eq!(
            decide_launch_kind(&args(&["--pipe=seed_ipc_1234"]), true),
            LaunchKind::Editor
        );
    }

    #[test]
    fn other_args_do_not_disable_packaged_mode() {
        // Play 用の引数だけならパッケージ判定は維持される
        assert_eq!(
            decide_launch_kind(&args(&["--mode=play", "--play-collider-draw=1"]), true),
            LaunchKind::Packaged
        );
    }

    #[test]
    fn exe_path_containing_arg_like_text_is_ignored() {
        // args[0]（exe パス）に紛らわしい文字列が入っていても判定に影響しない
        let v = vec![r"C:\--pipe=weird\SEED.exe".to_string()];
        assert_eq!(decide_launch_kind(&v, true), LaunchKind::Packaged);
    }
}
