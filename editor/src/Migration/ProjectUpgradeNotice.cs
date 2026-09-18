// ============================================================
//  ProjectUpgradeNotice.cs — プロジェクトを開いたときの「古い形式があります」案内
//
//  【役割】
//  プロジェクトを開いた直後にバックグラウンドで <c>--upgrade-project --dry-run</c> を
//  1 回だけ走らせ、古い形式のファイルがあれば知らせる。
//  **1 バイトも書き込まない**（dry-run なので読むだけ）。
//
//  【なぜ案内するのか】
//  古い形式のまま置いておくと、一括アップグレードのたびに
//  「版の 1 行を足す」差分が出続け、実データと差分が混ざって読めなくなる。
//  ただしアップグレードは**オーナーが 1 回実行して送信する**運用なので、
//  勝手に実行はしない。気づける状態にするところまでが仕事。
//
//  【邪魔をしないこと】
//  ・ランタイム exe が無い（未ビルド）ときは黙って諦める（ログだけ）
//  ・ヘッドレス起動では知らせない（呼び出し側が notify: false を渡す）
//  ・失敗しても例外を外へ出さない（プロジェクトを開く処理を巻き込まない）
//
//  【依存】
//  WPF に依存しない（提示は IMigrationNotifier、ログは Action<string> 経由）。
// ============================================================

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Migration;

/// <summary>
/// プロジェクトを開いたときの下調べと案内（状態を持たない静的クラス）。
/// </summary>
public static class ProjectUpgradeNotice
{
    /// <summary>
    /// 下調べに掛ける待ち時間。
    ///
    /// <para>
    /// 案内のための下調べなので、実行本体（<see cref="ProjectUpgradeRunner.DefaultTimeout"/>）
    /// ほど待つ必要は無い。ここまで掛かるならプロジェクトが大きいということなので、
    /// 黙って諦めてメニューからの実行に任せる。
    /// </para>
    /// </summary>
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 下調べをバックグラウンドで 1 回走らせ、古い形式があれば知らせる。
    ///
    /// <para>
    /// **待たない**。プロジェクトを開く処理を数百ミリ秒でも遅くする理由が無い。
    /// </para>
    /// </summary>
    /// <param name="projectPath">
    /// 対象（<c>.seedproj</c> / プロジェクトフォルダ / assets ルート）。
    /// </param>
    /// <param name="notify">
    /// 利用者へ知らせるか。ヘッドレス起動では false を渡すこと
    /// （AI エージェント運用の画面に通知を出しても誰も読まない）。
    /// </param>
    public static void ScanInBackground(string? projectPath, bool notify)
    {
        // fire-and-forget。例外はすべて内側で握る。
        _ = Task.Run(() => ScanAsync(projectPath, notify));
    }

    /// <summary>下調べの本体（例外を外へ出さない）。</summary>
    /// <param name="projectPath">対象のパス。</param>
    /// <param name="notify">利用者へ知らせるか。</param>
    private static async Task ScanAsync(string? projectPath, bool notify)
    {
        try
        {
            var exePath = AssetMigrationGateway.ResolveRuntimeExePath();
            if (string.IsNullOrWhiteSpace(exePath))
            {
                Skip(MigrationMessages.RUNTIME_EXE_UNRESOLVED);
                return;
            }

            var result = await ProjectUpgradeRunner
                .RunAsync(exePath, projectPath, dryRun: true, CancellationToken.None, ScanTimeout)
                .ConfigureAwait(false);

            if (!result.HasReport)
            {
                // exe が無い・起動できない・期限切れ。案内は諦めてログだけ残す。
                Skip(result.FailureMessage);
                return;
            }

            var summary = result.Report.Summary!;
            AssetMigrationGateway.Log?.Invoke(
                $"{MigrationMessages.LOG_PREFIX} 形式の下調べ: "
                + $"対象 {summary.Total} 件 / 要更新 {summary.Upgraded} 件 / "
                + $"現行版 {summary.UpToDate} 件 / 未来版 {summary.FutureVersion} 件 / "
                + $"読めない {summary.Failed} 件");

            if (summary.Upgraded <= 0) return;

            var message = string.Format(CultureInfo.InvariantCulture,
                MigrationMessages.STARTUP_NOTICE_FORMAT,
                summary.Upgraded, MigrationMessages.UPGRADE_MENU_HEADER);

            AssetMigrationGateway.Log?.Invoke($"{MigrationMessages.LOG_PREFIX} {message}");
            if (notify) AssetMigrationGateway.Notifier?.NotifyInfo(message);
        }
        catch (Exception e)
        {
            // 案内の失敗でエディタの動作を変えない。
            Skip(e.Message);
        }
    }

    /// <summary>案内を省略したことをログへ残す。</summary>
    /// <param name="reason">省略した理由。</param>
    private static void Skip(string reason)
    {
        var flat = (reason ?? string.Empty).Replace("\r\n", " / ").Replace("\n", " / ");
        AssetMigrationGateway.Log?.Invoke(
            $"{MigrationMessages.LOG_PREFIX} "
            + string.Format(CultureInfo.InvariantCulture,
                            MigrationMessages.STARTUP_NOTICE_SKIPPED_FORMAT, flat));
    }
}
