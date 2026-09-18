// ============================================================
//  MigrationNotifier.cs — マイグレーションの結論を画面へ出す実装（WPF 依存）
//
//  【役割】
//  <see cref="IMigrationNotifier"/> の唯一の実装。
//  ・開けなかった → モーダル（必ず気づかせる。開いたと思われたまま保存されると壊れる）
//  ・案内         → トースト（作業を止めない）
//
//  【★このファイルだけは WPF に依存する】
//  Migration 層は原則 WPF に依存しない（ProjectSettingsData などが単体テストへ
//  リンクされているため）。ここは「画面へ出す」ことが仕事なので例外。
//  テストプロジェクトへは **リンクしないこと**。
//
//  【ヘッドレスで止まらないこと】
//  モーダルは必ず <see cref="SEEDEditor.Headless.EditorDialogs"/> 経由で出す。
//  ヘッドレス起動では表示されずログへ流れ、UI スレッドが待ち続けない。
//  トーストも差し込まれていなければ何もしない。
// ============================================================

using System;
using System.Windows;

namespace SEEDEditor.Migration.Presentation;

/// <summary>
/// マイグレーションの結論を画面へ出す実装。
/// </summary>
public sealed class MigrationNotifier : IMigrationNotifier
{
    /// <summary>トーストの出力先（MainWindow が差し込む。無ければ出さない）。</summary>
    private readonly Action<string>? _toast;

    /// <summary>
    /// トーストの出力先を指定して生成する。
    /// </summary>
    /// <param name="toast">
    /// トーストを出す関数（<c>MainWindow.ShowToast</c>）。
    /// null なら案内はログだけになる（ヘッドレス起動を想定）。
    /// </param>
    public MigrationNotifier(Action<string>? toast)
    {
        _toast = toast;
    }

    /// <summary>
    /// 開けなかったことをモーダルで知らせる。
    /// </summary>
    /// <param name="title">見出し。</param>
    /// <param name="message">本文。</param>
    public void NotifyBlocked(string title, string message)
    {
        // 「開いたつもりで中身が空」という状態は見逃されてはいけないので警告アイコンにする。
        Invoke(() => SEEDEditor.Headless.EditorDialogs.Show(
            message, title, MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    /// <summary>
    /// 案内をトーストで知らせる（作業は止まっていない）。
    /// </summary>
    /// <param name="message">本文。</param>
    public void NotifyInfo(string message)
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
    /// 読み込みは UI スレッドから呼ばれることが多いが、プロジェクトを開いた直後の
    /// 下調べはワーカースレッドから来る。どちらでも落ちないよう必ず移す
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
