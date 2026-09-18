// ============================================================
//  IMigrationNotifier.cs — マイグレーションの結論を利用者へ見せる境界
//
//  【役割】
//  「開けませんでした」「古い形式が n 件あります」をどう見せるか
//  （モーダル／トースト／ログ）を、読み込み経路から切り離す。
//
//  【なぜインターフェースにするのか】
//  モーダルは WPF（EditorDialogs）、トーストは MainWindow の機能であり、
//  どちらもここから直接触ると ProjectSettingsData のような
//  「WPF 非依存でテストにリンクされているファイル」が WPF を引き込んでしまう。
//  実装は Presentation/MigrationNotifier.cs に置き、起動時に
//  <see cref="AssetMigrationGateway.Notifier"/> へ差し込む。
//  差し込まれていなければログだけが残り、動作は止まらない
//  （ヘッドレス・単体テストはこの状態で動く）。
//
//  【依存】
//  WPF に依存しない（単体テストへそのままリンクできる）。
// ============================================================

namespace SEEDEditor.Migration;

/// <summary>
/// マイグレーションの結論を利用者へ見せる窓口。
/// </summary>
public interface IMigrationNotifier
{
    /// <summary>
    /// ファイルを開けなかったことを知らせる（利用者が必ず気づく必要がある）。
    ///
    /// <para>
    /// 「開いたつもりなのに中身が既定値だった」という状態は、
    /// そのまま保存すると利用者のデータを消す。必ず目に入る形で出すこと。
    /// </para>
    /// </summary>
    /// <param name="title">見出し。</param>
    /// <param name="message">本文（複数行になり得る）。</param>
    void NotifyBlocked(string title, string message);

    /// <summary>
    /// 作業を止めない案内を知らせる（古い形式が残っている等）。
    ///
    /// <para>
    /// モーダルにしないこと（プロジェクトを開くたびにダイアログが出ると、
    /// 読まずに閉じる習慣がつく）。
    /// </para>
    /// </summary>
    /// <param name="message">本文（1 行）。</param>
    void NotifyInfo(string message);
}
