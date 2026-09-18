// ============================================================
//  ILockGateNotifier.cs — ゲートの結論を利用者へ見せる境界
//
//  【役割】
//  「止めた」「注意」をどう見せるか（モーダル／トースト／ログ）を
//  バージョン管理の層から切り離す。
//
//  【なぜインターフェースにするのか】
//  モーダルは WPF（<c>MessageBox</c>）で、トーストは MainWindow の機能。
//  どちらもここ（VersionControl 層）から直接触ると、この層が WPF に依存して
//  単体テストへリンクできなくなる。実装は
//  <c>editor/src/VersionControl/Locking/Presentation/LockGateNotifier.cs</c>
//  に置き、アプリ起動時に <see cref="LockGatekeeper.Notifier"/> へ差し込む。
//  差し込まれていなければログだけが残り、動作は止まらない
//  （ヘッドレスや単体テストはこの状態で動く）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

namespace SEEDEditor.VersionControl.Locking;

/// <summary>
/// ゲートの結論を利用者へ見せる窓口。
/// </summary>
public interface ILockGateNotifier
{
    /// <summary>
    /// 操作を止めたことを知らせる（利用者が必ず気づく必要がある）。
    ///
    /// <para>
    /// 保存や送信が黙って行われなかった状態は、利用者から見て
    /// 「保存したつもりのデータが無い」に等しい。必ず目に入る形で出すこと。
    /// </para>
    /// </summary>
    /// <param name="title">見出し。</param>
    /// <param name="message">本文（複数行になり得る）。</param>
    void NotifyBlocked(string title, string message);

    /// <summary>
    /// 注意だけを知らせる（操作は通っている）。
    ///
    /// <para>
    /// こちらは作業を止めないので、モーダルにしないこと
    /// （保存のたびにダイアログが出ると、読まずに閉じる習慣がつく）。
    /// </para>
    /// </summary>
    /// <param name="message">本文（1 行）。</param>
    void NotifyWarning(string message);
}
