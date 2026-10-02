using System;

namespace SEED.Binding;

// ============================================================
//  BindingLog.cs — 結び付け（SEED.Binding）の警告・エラーの出し先
//
//  純粋な部分（Model/）はエンジンの API（SEED.Debug）を直接呼ばない（editor/tests/BindingTests がリンクで取り込むため）。
//  エンジンへの出力は部分メソッド WriteToEngine で、実装は実行中だけのファイル（Binding/BindingLog.Engine.cs）にある。
//  実装の無いビルド（テスト）では C# の規則で呼び出しごと消え、標準エラーへ出す。
//  テストは Listener を差して警告を拾う（Listener があればそちらだけへ出す）。
// ============================================================

/// <summary>結び付けの警告・エラーの出し先。</summary>
internal static partial class BindingLog
{
    /// <summary>ログの接頭辞。</summary>
    internal const string Prefix = "[SEED.Binding]";

    /// <summary>
    /// 出力を横取りする受け口（テスト・診断用。null なら既定の出し先〈実行中は SEED.Debug、テストでは標準エラー〉）。
    /// 引数は接頭辞つきの 1 行。
    /// </summary>
    internal static Action<string>? Listener { get; set; }

    /// <summary>警告を出す。</summary>
    /// <param name="message">本文（接頭辞は付けて出す）。</param>
    internal static void Warn(string message) => Write(message, isError: false);

    /// <summary>エラーを出す（購読の処理で起きた例外など）。</summary>
    /// <param name="message">本文（接頭辞は付けて出す）。</param>
    internal static void Error(string message) => Write(message, isError: true);

    /// <summary>出し先を選んで 1 行を出す。</summary>
    /// <param name="message">本文。</param>
    /// <param name="isError">エラーなら true（警告なら false）。</param>
    private static void Write(string message, bool isError)
    {
        string line = $"{Prefix} {message}";

        // ── 横取りがあればそちらだけへ（テストが警告の回数を数える）──
        if (Listener is { } listener)
        {
            listener(line);
            return;
        }

        // ── 実行中はエンジンのログへ（実装の無いビルドでは呼び出しごと消え、delivered は false のまま）──
        bool delivered = false;
        WriteToEngine(line, isError, ref delivered);
        if (!delivered) Console.Error.WriteLine(line);
    }

    /// <summary>
    /// エンジンのログへ出す（実装は Binding/BindingLog.Engine.cs。出せたら <paramref name="delivered"/> を true にする）。
    /// </summary>
    /// <param name="line">接頭辞つきの 1 行。</param>
    /// <param name="isError">エラーなら true。</param>
    /// <param name="delivered">出せたら true にする。</param>
    static partial void WriteToEngine(string line, bool isError, ref bool delivered);
}
