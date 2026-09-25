// ============================================================
//  IAndroidIpcLink.cs — 端末のアプリとつながった IPC の通信路（エディタの実行バーが使う窓口。段階D-1）
//
//  本番は AndroidIpcSession（adb forward ＋ TCP）。エディタの実行の段取り（AndroidRun/AndroidRunController）は
//  この窓口越しに一時停止・再開を送り、切断を見張る。単体テストは偽物に差し替えて、端末なしで状態の動きを確かめる。
//
//  WPF に依存しない（SeedAndroid・単体テストからリンクされる）。
// ============================================================

using System.Threading.Tasks;

namespace SEEDEditor.Android.Ipc;

/// <summary>端末のアプリとつながった IPC の通信路。</summary>
public interface IAndroidIpcLink
{
    /// <summary>
    /// 1 行の命令を送る（RuntimeIpcCommands.Pause 等）。
    /// </summary>
    /// <param name="command">命令。</param>
    /// <returns>送れたら true（切れていれば false）。</returns>
    bool Send(string command);

    /// <summary>通信路が切れると完了する（アプリが終わった・adb が切れた・閉じた）。</summary>
    Task Closed { get; }

    /// <summary>
    /// 閉じて adb forward を外す（2 回目以降は何もしない）。
    /// </summary>
    /// <param name="detach">
    /// true なら先に DETACH（意図した切り離し）を送り、端末のアプリは一時停止を据え置く。
    /// false なら黙って閉じ、端末のアプリは一時停止中なら再開する。
    /// </param>
    /// <returns>完了。</returns>
    Task CloseAsync(bool detach);
}
