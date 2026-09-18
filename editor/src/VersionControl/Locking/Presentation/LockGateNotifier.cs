// ============================================================
//  LockGateNotifier.cs — ゲートの結論を画面へ出す実装（WPF 依存）
//
//  【役割】
//  <see cref="ILockGateNotifier"/> の唯一の実装。
//  ・止めた   → モーダル（必ず気づかせる）
//  ・注意     → トースト（作業を止めない）
//  どちらもログには必ず残す（ログの出力は <see cref="LockGatekeeper.Log"/> 側で行う）。
//
//  【★このファイルだけは WPF に依存する】
//  VersionControl 層は原則 WPF に依存しない（単体テストへリンクできるように）。
//  ここは「画面へ出す」ことが仕事なので例外。テストプロジェクトへは
//  **リンクしないこと**（リンクすると WPF 参照を引き込んでしまう）。
//
//  【ヘッドレスで止まらないこと】
//  モーダルは必ず <see cref="SEEDEditor.Headless.EditorDialogs"/> 経由で出す。
//  ヘッドレス起動では表示されずログへ流れ、UI スレッドが待ち続けない。
//  トーストも差し込まれていなければ何もしない。
// ============================================================

using System;
using System.Windows;

namespace SEEDEditor.VersionControl.Locking.Presentation;

/// <summary>
/// ゲートの結論を画面へ出す実装。
/// </summary>
public sealed class LockGateNotifier : ILockGateNotifier
{
    /// <summary>トーストの出力先（MainWindow が差し込む。無ければ出さない）。</summary>
    private readonly Action<string>? _toast;

    /// <summary>
    /// トーストの出力先を指定して生成する。
    /// </summary>
    /// <param name="toast">
    /// トーストを出す関数（`MainWindow.ShowToast`）。
    /// null なら注意はログだけになる（ヘッドレス起動を想定）。
    /// </param>
    public LockGateNotifier(Action<string>? toast)
    {
        _toast = toast;
    }

    /// <summary>
    /// 止めたことをモーダルで知らせる。
    /// </summary>
    /// <param name="title">見出し。</param>
    /// <param name="message">本文。</param>
    public void NotifyBlocked(string title, string message)
    {
        // 保存・送信が行われなかったという事実は見逃されてはいけないので警告アイコンにする。
        Invoke(() => SEEDEditor.Headless.EditorDialogs.Show(
            message, title, MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    /// <summary>
    /// 注意をトーストで知らせる（作業は通っている）。
    /// </summary>
    /// <param name="message">本文。</param>
    public void NotifyWarning(string message)
    {
        if (_toast is null) return;

        // トーストは 1 行で読ませるものなので、改行は区切り文字へ潰す。
        var flat = message.Replace("\r\n", " / ").Replace("\n", " / ");
        Invoke(() => _toast(flat));
    }

    /// <summary>
    /// UI スレッドで実行する。
    ///
    /// <para>
    /// 通常の呼び出し（保存・送信）はすでに UI スレッドなので、その場で実行される。
    /// 将来ワーカースレッドから提示されても落ちないよう、念のため移してある
    /// （<c>MessageBox</c> もトーストも UI スレッド専用）。
    /// </para>
    /// </summary>
    /// <param name="action">実行する処理。</param>
    private static void Invoke(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        // アプリが無い（単体テスト）／すでに UI スレッド → そのまま実行する。
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }
}
