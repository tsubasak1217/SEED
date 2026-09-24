// ============================================================
//  AndroidLogcatSession.cs — logcat を「止められるまで / 決めた秒数だけ」流し、必要ならファイルへ保存する
//
//  止める合図（CancellationToken）と秒数の時間切れは、どちらも正常な終わりとして扱う（行数を返す）。
//  保存は UTF-8（BOM なし）。adb の出力は UTF-8 のまま読むので、エンジンの日本語ログが化けない
//  （従来の build_and_run.ps1 -LogFile はコンソールのコードページで読んで化けていた。docs/backlog.md）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Android.Adb;

/// <summary>logcat を流す 1 回ぶん。</summary>
public static class AndroidLogcatSession
{
    /// <summary>
    /// logcat を流す。
    /// </summary>
    /// <param name="adb">adb。</param>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="since">この時刻以降（端末の時刻。AdbClient.GetLogcatSinceAsync）。</param>
    /// <param name="filters">タグの絞り込み。</param>
    /// <param name="duration">流す長さ（null なら止められるまで）。</param>
    /// <param name="logFile">保存先（null なら保存しない）。</param>
    /// <param name="onLine">1 行ごとに呼ばれる（adb の出力を読むスレッドから）。</param>
    /// <param name="cancellationToken">止める合図。</param>
    /// <returns>流した行数と、adb が自分で終わったときの終了コード（止めた・時間切れなら null）。</returns>
    public static async Task<(int Lines, int? ExitCode)> RunAsync(
        AdbClient adb,
        string serial,
        string since,
        IReadOnlyList<string> filters,
        TimeSpan? duration,
        string? logFile,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        using var timeout = duration is { } length ? new CancellationTokenSource(length) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        StreamWriter? writer = null;
        if (!string.IsNullOrWhiteSpace(logFile))
        {
            var fullPath = Path.GetFullPath(logFile);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            writer = new StreamWriter(fullPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }

        var lines = 0;
        var gate = new object();
        try
        {
            var exitCode = await adb.StreamLogcatAsync(serial, since, filters, line =>
            {
                lock (gate)
                {
                    writer?.WriteLine(line);
                    lines++;
                }
                onLine(line);
            }, linked.Token).ConfigureAwait(false);
            return (lines, exitCode);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // 止めた・時間切れ（どちらも正常な終わり）
            return (lines, null);
        }
        finally
        {
            lock (gate)
            {
                // 以後に届いた行（読み取りの打ち切りと行き違ったもの）は保存しない
                writer?.Dispose();
                writer = null;
            }
        }
    }
}
