// ============================================================
//  ChildProcessSpec.cs — 子プロセスの起動内容（実行ファイル・引数・作業フォルダ・環境変数）と結果
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Android.Processes;

/// <summary>子プロセスの出力の種類。</summary>
public enum ChildProcessStream
{
    /// <summary>標準出力。</summary>
    StandardOutput,

    /// <summary>標準エラー。</summary>
    StandardError,
}

/// <summary>子プロセスの起動内容。</summary>
public sealed record ChildProcessSpec
{
    /// <summary>空の環境変数の上書き。</summary>
    private static readonly IReadOnlyDictionary<string, string?> NoEnvironment = new Dictionary<string, string?>();

    /// <summary>実行ファイル（絶対パス、または PATH で見つかる名前）。</summary>
    public required string FileName { get; init; }

    /// <summary>引数（1 要素 1 引数。引用符付けは起動時に行う）。</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>作業フォルダ（null なら呼び出し元と同じ）。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>足す・上書きする環境変数（値が null のものは消す）。</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = NoEnvironment;

    /// <summary>ログに出すコマンドライン（引数に空白があれば二重引用符で囲む。実際の引用符付けとは別）。</summary>
    /// <returns>表示用の文字列。</returns>
    public string Describe() =>
        string.Join(' ', new[] { FileName }.Concat(Arguments).Select(QuoteForDisplay));

    /// <summary>表示用に引数を囲む。</summary>
    /// <param name="argument">引数。</param>
    /// <returns>空白を含めば二重引用符で囲んだもの。</returns>
    private static string QuoteForDisplay(string argument) =>
        argument.Length == 0 || argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;
}

/// <summary>出力を集めた子プロセスの結果。</summary>
/// <param name="ExitCode">終了コード。</param>
/// <param name="StandardOutput">標準出力の行。</param>
/// <param name="StandardError">標準エラーの行。</param>
public sealed record ChildProcessCapture(int ExitCode, IReadOnlyList<string> StandardOutput, IReadOnlyList<string> StandardError)
{
    /// <summary>標準出力を改行でつないだもの。</summary>
    public string StandardOutputText => string.Join('\n', StandardOutput);

    /// <summary>標準出力と標準エラーを改行でつないだもの（エラーの説明用）。</summary>
    public string AllOutputText => string.Join('\n', StandardOutput.Concat(StandardError));
}
