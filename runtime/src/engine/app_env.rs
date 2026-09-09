// ============================================================
//  app_env.rs — 実行環境フラグ（スクリプト API `SEED.Application` の判定源）
//
//  【役割】
//  「今このプロセスがどういう立場で動いているか」をプロセス全体で 1 箇所に保持する。
//  スクリプト側（C# の SEED.Application）は、ここの値を FFI 経由で読み、
//  デバッグ表示・デバッグコマンドといった開発用機能を配布版で無効化する判断に使う。
//
//  【保持する情報】
//  ・エディタからの Play 実行かどうか（EDITOR_PLAY）
//
//  なお「パッケージ実行（assets.pak を読んで動いている）」かどうかは
//  `asset_fs::is_packaged()` が既に判定源として存在するため、ここでは重複して持たない。
//
//  【初期化】
//  `App::new` で起動引数（LaunchArgs）と IPC 接続結果が確定した直後に
//  `init(...)` を一度だけ呼ぶ。以降は不変（実行中に変化しない）。
// ============================================================

use std::sync::OnceLock;

// ============================================================
//  グローバル状態
// ============================================================

/// エディタからの Play 実行かどうか。
///
/// `OnceLock` なので 2 回目以降の `init` は無視される（最初の 1 回だけが効く）。
/// 未初期化のまま参照された場合は `is_editor_play()` が false を返す。
static EDITOR_PLAY: OnceLock<bool> = OnceLock::new();

// ============================================================
//  判定ロジック（純関数）
// ============================================================

/// 「エディタからの Play 実行」かを、起動条件から決定する純関数。
///
/// `OnceLock` を触らないので単体テストで自由に検証できる（`init` はプロセス共有で
/// 1 度しか効かず、テストからは繰り返し検証できないためロジックだけを切り出している）。
///
/// 【判定根拠（実際の起動経路）】
/// - エディタの Play 起動: `editor/src/Runtime/RuntimeManager.cs:1415` が
///   `--mode=play --pipe=<name> …` を渡す → mode = Play かつ IPC 接続あり。
/// - エディタの Edit 起動（ビューポート埋め込み）: 同 `:1414` が
///   `--mode=edit --pipe=<name> --parent-hwnd=<hwnd> …` を渡す → mode = Edit。
/// - 配布された実行ファイルの単体起動: 引数なし →
///   `main.rs::parse_args` の既定で mode = Play、`--pipe=` が無いので IPC 接続なし。
///
/// つまり「mode が Play」かつ「IPC 接続がある」の同時成立は、
/// エディタから Play したときにだけ起こる。
///
/// 【エディタの Edit モード（埋め込みビュー）の扱い】
/// Edit モードでは false を返す。`IsEditorPlay` は名前どおり
/// 「エディタから Play したゲーム実行中か」を表すフラグであり、
/// Edit モードはゲームロジック（スクリプト）が回っていない編集中の状態なので、
/// 「Play 中だけ出したいデバッグ表示」の条件に Edit モードを混ぜると意味が壊れる。
/// なお Edit モードでも `is_packaged()` は false なので `IsDebugAllowed` は true になる。
///
/// - `mode_is_play`: 起動モードが `RuntimeMode::Play` か
/// - `has_ipc`: エディタとの IPC（名前付きパイプ）接続が確立しているか
pub fn decide_editor_play(mode_is_play: bool, has_ipc: bool) -> bool {
    mode_is_play && has_ipc
}

// ============================================================
//  初期化・参照
// ============================================================

/// 実行環境フラグを初期化する。アプリ起動時に一度だけ呼ぶこと。
///
/// - `is_editor_play`: `decide_editor_play` の結果を渡す
pub fn init(is_editor_play: bool) {
    // 2 回目以降は Err になるが、最初の値を保つのが正しいので無視する。
    let _ = EDITOR_PLAY.set(is_editor_play);
}

/// エディタからの Play 実行かを返す。未初期化なら false。
///
/// 未初期化時に false を返すのは、この値が「開発中だけ true になる」性質のもので、
/// 不明なら安全側（＝開発用機能を有効にしない側）へ倒すのが妥当なため。
pub fn is_editor_play() -> bool {
    *EDITOR_PLAY.get().unwrap_or(&false)
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 判定ロジック（純関数）が、実際の 4 通りの起動経路を正しく分類することを確認する。
    #[test]
    fn decide_editor_play_covers_launch_paths() {
        // エディタからの Play（--mode=play --pipe=…）
        assert!(decide_editor_play(true, true));
        // 配布 exe の単体起動（引数なし → mode=Play・IPC なし）
        assert!(!decide_editor_play(true, false));
        // エディタの Edit モード（--mode=edit --pipe=… → mode=Edit・IPC あり）
        assert!(!decide_editor_play(false, true));
        // どちらでもない（理論上の組み合わせ）
        assert!(!decide_editor_play(false, false));
    }

    /// `init` は最初の 1 回だけ効き、未初期化時は false を返すことを確認する。
    ///
    /// `EDITOR_PLAY` はプロセス共有の `OnceLock` なので、テストは
    /// 「init 前は false」→「init(true) 後は true」→「init(false) しても true のまま」
    /// を 1 つのテスト関数内で順に検証する（別テストへ分けると実行順に依存してしまう）。
    #[test]
    fn init_is_applied_only_once() {
        // 未初期化なら false
        assert!(!is_editor_play());

        // 最初の init が効く
        init(true);
        assert!(is_editor_play());

        // 2 回目以降は無視される
        init(false);
        assert!(is_editor_play());
    }
}
