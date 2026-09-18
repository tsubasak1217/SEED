// ============================================================
//  ProjectUpgradeRunner.cs — SEED.exe --upgrade-project の呼び出し
//
//  【役割】
//  プロジェクト配下の一括アップグレードを起動し、標準出力（JSON Lines）を
//  <see cref="ProjectUpgradeReport"/> へ写し取る。呼び出し規約の正典は
//  docs/asset_migration.md 6.5 (3)。
//
//  【dry-run を先に出す運用】
//  「まず --dry-run で何件書き換わるかを見せ、承諾を得てから実行する」。
//  この層はどちらも同じ関数で扱い、書き込むかどうかは引数 1 つで決める
//  （経路を 2 本に分けると、片方だけ直す事故が起きる）。
//
//  【ウィンドウも GPU も作らない】
//  ランタイムは main の入口でこの引数を見つけると、描画資源を一切初期化せずに
//  終了する。エディタが起動中でも安全に走らせられる。
//
//  【依存】
//  WPF に依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Migration;

// ── 結果 ─────────────────────────────────────────────────────────

/// <summary>
/// 一括アップグレードの実行結果（不変）。
/// </summary>
/// <param name="Launched">プロセスを起動できたか（false ならレポートは空）。</param>
/// <param name="ExitCode">終了コード（起動できなかったときは -1）。</param>
/// <param name="Report">標準出力を解釈したレポート。</param>
/// <param name="FailureMessage">起動できなかった・期限切れなどの説明（無ければ空）。</param>
public sealed record ProjectUpgradeRunResult(
    bool Launched, int ExitCode, ProjectUpgradeReport Report, string FailureMessage)
{
    /// <summary>レポートを読める状態か（＝画面に結果を並べてよいか）。</summary>
    public bool HasReport => Launched && Report.Summary is not null;
}

// ── 実行 ─────────────────────────────────────────────────────────

/// <summary>
/// ランタイムの一括アップグレードコマンドを呼ぶ（状態を持たない静的クラス）。
/// </summary>
public static class ProjectUpgradeRunner
{
    // ── 呼び出し規約の定数 ───────────────────────────────────

    /// <summary>一括アップグレードを要求するコマンドライン引数。</summary>
    public const string UPGRADE_PROJECT_FLAG = "--upgrade-project";

    /// <summary>書き込みを行わずに結果だけ出すフラグ。</summary>
    public const string DRY_RUN_FLAG = "--dry-run";

    /// <summary>終了コード: 問題なし。</summary>
    public const int EXIT_OK = 0;

    /// <summary>終了コード: 対処が必要な結果（失敗・未来版）があった。</summary>
    public const int EXIT_PROBLEM = 1;

    /// <summary>終了コード: 引数が不正・対象が見つからない。</summary>
    public const int EXIT_BAD_USAGE = 2;

    /// <summary>起動できなかったときに返す終了コード（ランタイムは返さない値）。</summary>
    public const int EXIT_NOT_LAUNCHED = -1;

    /// <summary>
    /// 実行の待ち時間の既定値。
    ///
    /// <para>
    /// 実データ（アセット 70 件規模）では 1 秒で終わるが、プロジェクトが
    /// 大きくなると全ファイルの読み書きぶんだけ延びる。
    /// ここまで待っても終わらないのは異常なので諦めてよい。
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    // ── 公開 API ─────────────────────────────────────────────

    /// <summary>
    /// 一括アップグレードを実行する。
    /// </summary>
    /// <param name="exePath">ランタイム exe の絶対パス。</param>
    /// <param name="projectPath">
    /// プロジェクトのパス（<c>.seedproj</c> / プロジェクトフォルダ / assets ルートのいずれか）。
    /// </param>
    /// <param name="dryRun">true なら 1 バイトも書き込まず、結果だけを出す。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <param name="timeout">待ち時間（null なら <see cref="DefaultTimeout"/>）。</param>
    /// <returns>実行結果。</returns>
    public static async Task<ProjectUpgradeRunResult> RunAsync(
        string? exePath,
        string? projectPath,
        bool dryRun,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        // ── 前提の確認 ──
        if (string.IsNullOrWhiteSpace(exePath))
            return NotLaunched(MigrationMessages.RUNTIME_EXE_UNRESOLVED);
        if (!File.Exists(exePath))
        {
            return NotLaunched(string.Format(CultureInfo.InvariantCulture,
                MigrationMessages.RUNTIME_EXE_MISSING_FORMAT, exePath));
        }
        if (string.IsNullOrWhiteSpace(projectPath))
            return NotLaunched(MigrationMessages.UPGRADE_NO_PROJECT);

        var limit = timeout ?? DefaultTimeout;
        var start = BuildStartInfo(exePath, projectPath, dryRun);

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return NotLaunched(string.Format(CultureInfo.InvariantCulture,
                    MigrationMessages.MIGRATE_LAUNCH_FAILED_FORMAT, "プロセスを起動できませんでした"));
            }

            // 標準入力は使わないが、リダイレクトしている以上は閉じておく
            // （閉じないと、万一ランタイムが読もうとしたときに止まる）。
            try { process.StandardInput.Close(); } catch (IOException) { /* 既に閉じている */ }

            // 出力の読み取りを先に始めてから終了を待つ（バッファ満杯の行き詰まりを避ける）。
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limited.CancelAfter(limit);

            try
            {
                await process.WaitForExitAsync(limited.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);
                var reason = cancellationToken.IsCancellationRequested
                    ? "中断されました"
                    : string.Format(CultureInfo.InvariantCulture,
                        MigrationMessages.MIGRATE_TIMEOUT_FORMAT,
                        limit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture));
                return NotLaunched(reason);
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            var report = ProjectUpgradeReport.Parse(SplitLines(stdout));

            // 標準エラーへ何か出ていたら失敗の説明として拾う
            // （ランタイムは通常ここへ何も書かないが、パニックはここへ出る）。
            var failure = report.Summary is null ? stderr.Trim() : string.Empty;

            return new ProjectUpgradeRunResult(true, process.ExitCode, report, failure);
        }
        catch (Exception e)
        {
            return NotLaunched(string.Format(CultureInfo.InvariantCulture,
                MigrationMessages.MIGRATE_LAUNCH_FAILED_FORMAT, e.Message));
        }
    }

    // ── 内部 ─────────────────────────────────────────────────

    /// <summary>起動情報を組み立てる（3 本のストリームをリダイレクトし、窓を出さない）。</summary>
    /// <param name="exePath">ランタイム exe の絶対パス。</param>
    /// <param name="projectPath">プロジェクトのパス。</param>
    /// <param name="dryRun">書き込みを行わないか。</param>
    private static ProcessStartInfo BuildStartInfo(string exePath, string projectPath, bool dryRun)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var info = new ProcessStartInfo
        {
            FileName               = exePath,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardInputEncoding  = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding  = utf8,
            WorkingDirectory       = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? string.Empty,
        };
        info.ArgumentList.Add(UPGRADE_PROJECT_FLAG);
        info.ArgumentList.Add(projectPath);
        if (dryRun) info.ArgumentList.Add(DRY_RUN_FLAG);
        return info;
    }

    /// <summary>標準出力を行へ分ける（CRLF / LF の両方を扱う）。</summary>
    /// <param name="text">標準出力の全文。</param>
    private static IEnumerable<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (var line in text.Split('\n')) yield return line.TrimEnd('\r');
    }

    /// <summary>刺さったプロセスを落とす（失敗しても無視する）。</summary>
    /// <param name="process">対象のプロセス。</param>
    private static void KillQuietly(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception) { /* 既に終わっている・権限が無い */ }
    }

    /// <summary>起動できなかった結果を作る。</summary>
    /// <param name="message">説明。</param>
    private static ProjectUpgradeRunResult NotLaunched(string message)
        => new(false, EXIT_NOT_LAUNCHED, new ProjectUpgradeReport(), message);
}
