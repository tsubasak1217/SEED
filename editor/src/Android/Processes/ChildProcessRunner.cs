// ============================================================
//  ChildProcessRunner.cs — 外部プログラム（cargo ndk / gradlew / adb / dotnet）を子プロセスで動かす
//
//  【決まりごと】
//  - 標準出力・標準エラーは 1 行ずつコールバックへ渡す（MixedEncodingLineReader。文字コードの混在に耐える）。
//    コールバックは 2 本の読み取りから並行に呼ばれる（受け手はスレッド安全にすること）。
//  - 標準入力は必ずパイプにする。書くもの（tar のストリーム等）が無ければすぐ閉じる
//    （子が呼び出し元のコンソールから読もうとして止まったり、Ctrl+C を横取りしたりしないように）。
//  - 中断（CancellationToken）では、自分が起動した子プロセスとその子孫だけを終了させる
//    （Process.Kill(entireProcessTree: true)）。終了を待ってから OperationCanceledException を投げる。
//  - 子が終了した後も、孫（Gradle のデーモン等）が出力のパイプを握ったままだと読み取りが終わらない。
//    終了後は一定時間だけ残りの出力を待ち、それを過ぎたらパイプを閉じて打ち切る。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Android.Processes;

/// <summary>子プロセスを起動できなかった（実行ファイルが無い等）。</summary>
public sealed class ChildProcessStartException : Exception
{
    /// <summary>理由を指定して生成する。</summary>
    /// <param name="message">利用者へ見せる説明。</param>
    /// <param name="inner">元の例外。</param>
    public ChildProcessStartException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>子プロセスを起動し、出力を行単位で流す。</summary>
public static class ChildProcessRunner
{
    /// <summary>子の終了後、残りの出力（孫が握ったパイプ）を待つ上限。</summary>
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 子プロセスを動かし、終了コードを返す。
    /// </summary>
    /// <param name="spec">起動内容。</param>
    /// <param name="onLine">出力 1 行ごとに呼ばれる（null なら捨てる。複数のスレッドから呼ばれる）。</param>
    /// <param name="cancellationToken">中断の合図（子プロセスとその子孫を終了させる）。</param>
    /// <param name="standardInputWriter">標準入力へ書く処理（null なら標準入力はすぐ閉じる）。</param>
    /// <returns>終了コード。</returns>
    /// <exception cref="ChildProcessStartException">起動できなかったとき。</exception>
    /// <exception cref="OperationCanceledException">中断されたとき（子は終了済み）。</exception>
    public static async Task<int> RunAsync(
        ChildProcessSpec spec,
        Action<ChildProcessStream, string>? onLine,
        CancellationToken cancellationToken,
        Func<Stream, CancellationToken, Task>? standardInputWriter = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = CreateStartInfo(spec) };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new ChildProcessStartException($"{spec.FileName} を起動できません: {ex.Message}", ex);
        }

        // 中断されたら子プロセスとその子孫を終了させる（自分が起動したものだけ）
        using var registration = cancellationToken.Register(() => TryKillTree(process));

        using var readerCancellation = new CancellationTokenSource();
        var stdout = MixedEncodingLineReader.ReadLinesAsync(
            process.StandardOutput.BaseStream, line => onLine?.Invoke(ChildProcessStream.StandardOutput, line), readerCancellation.Token);
        var stderr = MixedEncodingLineReader.ReadLinesAsync(
            process.StandardError.BaseStream, line => onLine?.Invoke(ChildProcessStream.StandardError, line), readerCancellation.Token);
        var stdin = WriteStandardInputAsync(process, standardInputWriter, cancellationToken);

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        // 終了後の残りの出力を待つ（孫がパイプを握っていたら打ち切る）
        var readers = Task.WhenAll(stdout, stderr);
        if (await Task.WhenAny(readers, Task.Delay(OutputDrainTimeout, CancellationToken.None)).ConfigureAwait(false) != readers)
        {
            readerCancellation.Cancel();
            CloseQuietly(process.StandardOutput.BaseStream);
            CloseQuietly(process.StandardError.BaseStream);
        }
        await IgnoreReaderShutdownAsync(readers).ConfigureAwait(false);
        await IgnoreReaderShutdownAsync(stdin).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    /// <summary>
    /// 子プロセスを動かし、出力を集めて返す（getprop・pm path 等の短い問い合わせ用）。
    /// </summary>
    /// <param name="spec">起動内容。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <param name="standardInputWriter">標準入力へ書く処理（null なら標準入力はすぐ閉じる）。</param>
    /// <returns>終了コードと出力。</returns>
    public static async Task<ChildProcessCapture> CaptureAsync(
        ChildProcessSpec spec,
        CancellationToken cancellationToken,
        Func<Stream, CancellationToken, Task>? standardInputWriter = null)
    {
        var stdout = new List<string>();
        var stderr = new List<string>();
        var gate = new object();
        var exitCode = await RunAsync(spec, (stream, line) =>
        {
            lock (gate)
            {
                (stream == ChildProcessStream.StandardOutput ? stdout : stderr).Add(line);
            }
        }, cancellationToken, standardInputWriter).ConfigureAwait(false);
        lock (gate)
        {
            return new ChildProcessCapture(exitCode, stdout.ToArray(), stderr.ToArray());
        }
    }

    /// <summary>
    /// 子プロセスを動かし、標準出力をバイト列のまま集めて返す（adb exec-out でファイルを取り出す等。段階D-1）。
    /// 標準エラーは行で集める。中断・孫がパイプを握ったときの扱いは <see cref="RunAsync"/> と同じ。
    /// </summary>
    /// <param name="spec">起動内容。</param>
    /// <param name="cancellationToken">中断の合図（子プロセスとその子孫を終了させる）。</param>
    /// <returns>終了コードと出力。</returns>
    /// <exception cref="ChildProcessStartException">起動できなかったとき。</exception>
    /// <exception cref="OperationCanceledException">中断されたとき（子は終了済み）。</exception>
    public static async Task<ChildProcessBytesCapture> CaptureBytesAsync(ChildProcessSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = CreateStartInfo(spec) };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new ChildProcessStartException($"{spec.FileName} を起動できません: {ex.Message}", ex);
        }

        // 中断されたら子プロセスとその子孫を終了させる（自分が起動したものだけ）
        using var registration = cancellationToken.Register(() => TryKillTree(process));

        using var readerCancellation = new CancellationTokenSource();
        using var output = new MemoryStream();
        var errors = new List<string>();
        var errorGate = new object();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(output, readerCancellation.Token);
        var stderr = MixedEncodingLineReader.ReadLinesAsync(
            process.StandardError.BaseStream, line => { lock (errorGate) errors.Add(line); }, readerCancellation.Token);
        var stdin = WriteStandardInputAsync(process, null, cancellationToken);

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        // 終了後の残りの出力を待つ（孫がパイプを握っていたら打ち切る）
        var readers = Task.WhenAll(stdout, stderr);
        if (await Task.WhenAny(readers, Task.Delay(OutputDrainTimeout, CancellationToken.None)).ConfigureAwait(false) != readers)
        {
            readerCancellation.Cancel();
            CloseQuietly(process.StandardOutput.BaseStream);
            CloseQuietly(process.StandardError.BaseStream);
        }
        await IgnoreReaderShutdownAsync(readers).ConfigureAwait(false);
        await IgnoreReaderShutdownAsync(stdin).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        lock (errorGate)
        {
            return new ChildProcessBytesCapture(process.ExitCode, output.ToArray(), errors.ToArray());
        }
    }

    /// <summary>起動設定を作る（窓を出さない・入出力はすべてパイプ）。</summary>
    /// <param name="spec">起動内容。</param>
    /// <returns>起動設定。</returns>
    private static ProcessStartInfo CreateStartInfo(ChildProcessSpec spec)
    {
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = spec.WorkingDirectory ?? string.Empty,
        };
        foreach (var argument in spec.Arguments) startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in spec.Environment)
        {
            if (value is null) startInfo.Environment.Remove(name);
            else startInfo.Environment[name] = value;
        }
        return startInfo;
    }

    /// <summary>標準入力へ書き、閉じる（書くものが無ければすぐ閉じる）。</summary>
    /// <param name="process">子プロセス。</param>
    /// <param name="writer">書く処理。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    private static async Task WriteStandardInputAsync(
        Process process, Func<Stream, CancellationToken, Task>? writer, CancellationToken cancellationToken)
    {
        var stream = process.StandardInput.BaseStream;
        try
        {
            if (writer is not null) await writer(stream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // 子が先に終わってパイプが切れていても、閉じる失敗は無視する
            CloseQuietly(stream);
        }
    }

    /// <summary>
    /// 読み取り・書き込みのタスクを待つ。パイプを閉じて打ち切った・子が先に終わったことによる例外は無視する
    /// （結果は終了コードで判断する）。
    /// </summary>
    /// <param name="task">待つタスク。</param>
    private static async Task IgnoreReaderShutdownAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // パイプを閉じた・中断した結果。ここでは何もしない
        }
    }

    /// <summary>子プロセスとその子孫を終了させる（既に終わっていれば何もしない）。</summary>
    /// <param name="process">子プロセス。</param>
    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // 終了と行き違った等。待ちの側が終了を確かめる
        }
    }

    /// <summary>ストリームを閉じる（失敗は無視）。</summary>
    /// <param name="stream">閉じるストリーム。</param>
    private static void CloseQuietly(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 既に閉じている・パイプが切れている
        }
    }
}
