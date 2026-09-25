// ============================================================
//  WindowsCommandLine.cs — 引数の並びを Windows のコマンドライン文字列にする（純粋な処理）
//
//  CreateProcessW に直接渡すコマンドラインを作る（DetachedProcess。.NET の ProcessStartInfo.ArgumentList が
//  内部で行っているのと同じ、C ランタイムの argv の分け方に合わせた引用）。
//    - 空白・タブ・改行・" を含まない引数はそのまま
//    - それ以外は " で囲み、中の " は \" に、" の直前の \ と末尾の \ は 2 倍にする
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Collections.Generic;
using System.Text;

namespace SEEDEditor.Android.Processes;

/// <summary>Windows のコマンドライン文字列の組み立て。</summary>
public static class WindowsCommandLine
{
    /// <summary>引用が要る文字（C ランタイムの argv の区切りと引用符）。</summary>
    private static readonly char[] CharsNeedingQuotes = { ' ', '\t', '\n', '\v', '"' };

    /// <summary>引用符。</summary>
    private const char Quote = '"';

    /// <summary>逆スラッシュ（引用符の直前・末尾では 2 倍にする）。</summary>
    private const char Backslash = '\\';

    /// <summary>引数の区切り。</summary>
    private const char ArgumentSeparator = ' ';

    /// <summary>
    /// 実行ファイルと引数からコマンドライン文字列を作る（先頭は実行ファイル＝argv[0]）。
    /// </summary>
    /// <param name="fileName">実行ファイル。</param>
    /// <param name="arguments">引数。</param>
    /// <returns>コマンドライン。</returns>
    public static string Build(string fileName, IEnumerable<string> arguments)
    {
        var builder = new StringBuilder(QuoteArgument(fileName));
        foreach (var argument in arguments)
        {
            builder.Append(ArgumentSeparator).Append(QuoteArgument(argument));
        }
        return builder.ToString();
    }

    /// <summary>
    /// 引数 1 つを、C ランタイムが元の文字列に戻せる形にする。
    /// </summary>
    /// <param name="argument">引数。</param>
    /// <returns>引用した引数（引用が要らなければそのまま）。</returns>
    public static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(CharsNeedingQuotes) < 0) return argument;

        var builder = new StringBuilder();
        builder.Append(Quote);
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == Backslash)
            {
                backslashes++;
                continue;
            }
            if (c == Quote)
            {
                // 引用符の直前の \ は 2 倍にし、引用符そのものを \" にする
                builder.Append(Backslash, backslashes * 2 + 1).Append(Quote);
            }
            else
            {
                builder.Append(Backslash, backslashes).Append(c);
            }
            backslashes = 0;
        }
        // 閉じの引用符の直前の \ も 2 倍にする（\" と読まれないように）
        builder.Append(Backslash, backslashes * 2).Append(Quote);
        return builder.ToString();
    }
}
