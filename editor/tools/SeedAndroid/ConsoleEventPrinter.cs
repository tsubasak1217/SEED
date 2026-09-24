// ============================================================
//  ConsoleEventPrinter.cs — 中核のイベント（AndroidPipelineEvent）をコンソールへ書く
//
//  従来の build_and_run.ps1 と同じ見た目（工程の見出しは水色、説明は字下げ、警告は黄、エラーは赤）。
//  子プロセスの出力を読むスレッドからも届くので、書き込みはロックで 1 行ずつにする（色の切り替えと混ざらないように）。
// ============================================================

using System;
using SEEDEditor.Android.Pipeline;

namespace SEEDEditor.Tools.SeedAndroid;

/// <summary>イベントをコンソールへ書く。</summary>
public sealed class ConsoleEventPrinter : IProgress<AndroidPipelineEvent>
{
    /// <summary>説明・子プロセスの出力の字下げ。</summary>
    private const string Indent = "      ";

    /// <summary>書き込みの排他。</summary>
    private readonly object _gate = new();

    /// <inheritdoc />
    public void Report(AndroidPipelineEvent value)
    {
        lock (_gate)
        {
            switch (value)
            {
                case AndroidPhaseStarted started:
                    Write(ConsoleColor.Cyan, $"{Label(started.Phase, started.Index, started.Count)} {started.Title} — {started.Reason}");
                    break;
                case AndroidPhaseFinished finished:
                    WriteFinished(finished);
                    break;
                case AndroidLogLine line:
                    WriteLine(line);
                    break;
                case AndroidPipelineError error:
                    Write(ConsoleColor.Red, $"エラー（{error.Kind}）: {error.Message}", toError: true);
                    break;
                case AndroidProgressChanged:
                    // 進み具合は工程の見出しで分かるのでコンソールには出さない（エディタの進捗バー用）
                    break;
            }
        }
    }

    /// <summary>工程の終わりを書く。</summary>
    private static void WriteFinished(AndroidPhaseFinished finished)
    {
        var seconds = $"{finished.Elapsed.TotalSeconds:F1} 秒";
        switch (finished.Outcome)
        {
            case AndroidPhaseOutcome.Skipped:
                Write(ConsoleColor.DarkGray, $"{Label(finished.Phase, finished.Index, finished.Count)} {finished.Title} — 飛ばしました: {finished.Summary}");
                break;
            case AndroidPhaseOutcome.Succeeded:
                Write(ConsoleColor.Green, $"{Indent}完了: {finished.Summary}（{seconds}）");
                break;
            case AndroidPhaseOutcome.Canceled:
                Write(ConsoleColor.Yellow, $"{Indent}中断しました（{seconds}）");
                break;
            default:
                Write(ConsoleColor.Red, $"{Indent}失敗: {finished.Summary}（{seconds}）", toError: true);
                break;
        }
    }

    /// <summary>1 行のログを書く。</summary>
    private static void WriteLine(AndroidLogLine line)
    {
        switch (line.Level)
        {
            case AndroidLogLevel.Warning:
                Write(ConsoleColor.Yellow, $"{Indent}警告: {line.Text}");
                break;
            case AndroidLogLevel.Error:
                Write(ConsoleColor.Red, $"{Indent}{line.Text}", toError: true);
                break;
            case AndroidLogLevel.Logcat:
                Console.Out.WriteLine(line.Text);
                break;
            default:
                Console.Out.WriteLine(Indent + line.Text);
                break;
        }
    }

    /// <summary>工程の番号の見出し（準備は [準備]）。</summary>
    private static string Label(AndroidPipelinePhase phase, int index, int count) =>
        phase == AndroidPipelinePhase.Prepare ? "[準備]" : $"[{index}/{count}]";

    /// <summary>色を付けて 1 行書く（出力がリダイレクトされていれば色は付けない）。</summary>
    private static void Write(ConsoleColor color, string text, bool toError = false)
    {
        var writer = toError ? Console.Error : Console.Out;
        var redirected = toError ? Console.IsErrorRedirected : Console.IsOutputRedirected;
        if (redirected)
        {
            writer.WriteLine(text);
            return;
        }
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        writer.WriteLine(text);
        Console.ForegroundColor = previous;
    }
}
