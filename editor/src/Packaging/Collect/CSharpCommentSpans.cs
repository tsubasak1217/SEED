// ============================================================
//  CSharpCommentSpans.cs — C# のソースのコメントの範囲（// ・ /// ・ /* */）を求める（純粋な処理）
//
//  【何に使うか】
//  パッケージの収録の「参照先が見つからないパス」の警告から、説明のコメントの中に例として書いたパス
//  （"assets://common/data/xxx.json" など。Wake or Pay で 2 件の誤検出。docs/backlog.md W3-2 (7)）を除くため。
//  AssetReferenceScanner が「コメントの中にだけある参照」に印を付け、AssetCollector が警告から外す。
//  収録（実在するファイルを入れること）には使わない（コメントに頼って入っていたものを落とさない）。
//
//  【字句の扱い】
//  コメントの始まりの "//"・"/*" は、文字列・文字のリテラルの中では数えない（"assets://..." 自体が "//" を含むので、
//  文字列を飛ばさないと文字列の中の参照がすべてコメント扱いになる）。飛ばすリテラル:
//    - 普通の文字列 "..."（\ のエスケープ。行末まで閉じなければその行で閉じたとみなす＝書きかけのソースでも先へ進む）
//    - 逐語的文字列 @"..."（"" がエスケープ。行をまたげる）
//    - 補間文字列 $"..."・$@"..."・@$"..."（{ } の穴の中は式として読み、入れ子の文字列・コメントも扱う。{{ }} はエスケープ）
//    - 生の文字列 """..."""・$"""..."""（開きと同じ数以上の引用符の並びで閉じる。補間の穴の中は読まない＝簡略化）
//    - 文字のリテラル '...'（\ のエスケープ。行末で打ち切る）
//  完全な C# の字句解析ではない（プリプロセッサの #if で外れた部分もコードとして読む・#region の文の引用符は行末で打ち切る）。
//  外れたときの害は「その参照の欠落の警告が出る・出ない」だけで、収録には影響しない。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.Packaging.Collect;

/// <summary>C# のソースのコメントの範囲を求める。状態を持たない純粋な処理。</summary>
public static class CSharpCommentSpans
{
    /// <summary>コメントの範囲（文字の位置。Start 以上 End 未満）。</summary>
    /// <param name="Start">コメントの始まり（"//" か "/*" の最初の文字）。</param>
    /// <param name="End">コメントの終わりの次（行のコメントは改行の位置、ブロックのコメントは "*/" の次）。</param>
    public readonly record struct Span(int Start, int End);

    /// <summary>生の文字列の開きとみなす引用符の数の下限（C# 11 の raw string literal）。</summary>
    private const int RawStringMinQuotes = 3;

    /// <summary>行のコメントの始まり。</summary>
    private const string LineCommentStart = "//";

    /// <summary>ブロックのコメントの始まり。</summary>
    private const string BlockCommentStart = "/*";

    /// <summary>ブロックのコメントの終わり。</summary>
    private const string BlockCommentEnd = "*/";

    // ============================================================
    //  公開 API
    // ============================================================

    /// <summary>
    /// ソースのコメントの範囲を、出てきた順（昇順・重ならない）で返す。
    /// </summary>
    /// <param name="text">C# のソース。</param>
    /// <returns>コメントの範囲の一覧。</returns>
    public static IReadOnlyList<Span> Find(string text)
    {
        var spans = new List<Span>();
        int i = 0;
        ScanCode(text, ref i, spans, insideInterpolation: false);
        return spans;
    }

    /// <summary>
    /// 位置がどれかのコメントの中か（<see cref="Find"/> の結果を二分探索する）。
    /// </summary>
    /// <param name="spans"><see cref="Find"/> の結果（昇順・重ならない）。</param>
    /// <param name="position">調べる文字の位置。</param>
    /// <returns>コメントの中なら true。</returns>
    public static bool Contains(IReadOnlyList<Span> spans, int position)
    {
        // 始まりが position 以下の最後の範囲を探し、その終わりより前かを見る
        int lo = 0, hi = spans.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (spans[mid].Start <= position) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found >= 0 && position < spans[found].End;
    }

    // ============================================================
    //  コード
    // ============================================================

    /// <summary>
    /// コードを読み進め、コメントの範囲を積む。補間の穴の中なら、対になる '}' の次で戻る。
    /// </summary>
    /// <param name="text">ソース。</param>
    /// <param name="i">読む位置（読み終えた位置を返す）。</param>
    /// <param name="spans">コメントの範囲の積み先。</param>
    /// <param name="insideInterpolation">補間の穴の中か（'}' で戻る）。</param>
    private static void ScanCode(string text, ref int i, List<Span> spans, bool insideInterpolation)
    {
        int braceDepth = 0;
        while (i < text.Length)
        {
            char c = text[i];

            // ── コメント ──
            if (StartsWithAt(text, i, LineCommentStart))
            {
                int start = i;
                i = LineEnd(text, i);
                spans.Add(new Span(start, i));
                continue;
            }
            if (StartsWithAt(text, i, BlockCommentStart))
            {
                int start = i;
                int close = text.IndexOf(BlockCommentEnd, i + BlockCommentStart.Length, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + BlockCommentEnd.Length;   // 閉じないブロックは最後まで
                spans.Add(new Span(start, i));
                continue;
            }

            // ── リテラル（中の // ・ /* を数えない）──
            if (c == '\'')
            {
                i = SkipCharLiteral(text, i);
                continue;
            }
            if (IsStringStart(text, i))
            {
                i = SkipString(text, i, spans);
                continue;
            }

            // ── 補間の穴の終わり（入れ子の { } は数える）──
            if (insideInterpolation)
            {
                if (c == '{') braceDepth++;
                else if (c == '}')
                {
                    if (braceDepth == 0) { i++; return; }
                    braceDepth--;
                }
            }
            i++;
        }
    }

    // ============================================================
    //  リテラル
    // ============================================================

    /// <summary>
    /// 位置が文字列の始まりか（'"'、または '$' の並びと '@' 1 つの後に '"'）。
    /// '@' だけの後に '"' が無いもの（@class のような逐語的な識別子）は文字列ではない。
    /// </summary>
    private static bool IsStringStart(string text, int i)
    {
        if (text[i] == '"') return true;
        if (text[i] != '$' && text[i] != '@') return false;
        int ats = 0;
        int j = i;
        while (j < text.Length && (text[j] == '$' || text[j] == '@'))
        {
            if (text[j] == '@') ats++;
            j++;
        }
        return ats <= 1 && j < text.Length && text[j] == '"';
    }

    /// <summary>
    /// 文字列のリテラルを読み飛ばす（頭の '$'・'@' から）。補間の穴の中のコメントは積む。
    /// </summary>
    /// <returns>文字列の次の位置。</returns>
    private static int SkipString(string text, int i, List<Span> spans)
    {
        // ── 頭（$ の数・@ の有無）──
        int dollars = 0;
        bool verbatim = false;
        while (text[i] == '$' || text[i] == '@')
        {
            if (text[i] == '$') dollars++;
            else verbatim = true;
            i++;
        }

        // ── 生の文字列（"""…"""。@ は付かない）──
        int quotes = CountRun(text, i, '"');
        if (!verbatim && quotes >= RawStringMinQuotes) return SkipRawString(text, i + quotes, quotes);

        // ── 普通・逐語的・補間の文字列 ──
        bool interpolated = dollars > 0;
        i++;   // 開きの '"'
        while (i < text.Length)
        {
            char c = text[i];
            if (verbatim)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }   // "" はエスケープ
                    return i + 1;
                }
            }
            else
            {
                if (c == '\\') { i += 2; continue; }        // \" などのエスケープ
                if (c == '"') return i + 1;
                if (c == '\n') return i;                    // 行末まで閉じない（書きかけ）。その行で閉じたとみなす
            }

            if (interpolated && c == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{') { i += 2; continue; }   // {{ はエスケープ
                i++;
                ScanCode(text, ref i, spans, insideInterpolation: true);           // 穴の中は式（対になる } の次で戻る）
                continue;
            }
            if (interpolated && c == '}' && i + 1 < text.Length && text[i + 1] == '}') { i += 2; continue; }   // }} はエスケープ
            i++;
        }
        return text.Length;
    }

    /// <summary>
    /// 生の文字列の中身を読み飛ばす（開きと同じ数以上の引用符の並びで閉じる）。
    /// </summary>
    /// <param name="text">ソース。</param>
    /// <param name="contentStart">中身の始まり（開きの引用符の次）。</param>
    /// <param name="quotes">開きの引用符の数。</param>
    /// <returns>閉じの引用符の次の位置（閉じなければ最後）。</returns>
    private static int SkipRawString(string text, int contentStart, int quotes)
    {
        int search = contentStart;
        while (true)
        {
            int q = text.IndexOf('"', search);
            if (q < 0) return text.Length;
            int run = CountRun(text, q, '"');
            if (run >= quotes) return q + run;
            search = q + run;
        }
    }

    /// <summary>
    /// 文字のリテラル（'x'・'\''・'A'）を読み飛ばす。行末で打ち切る（#region の文の ' など、文字のリテラルでない ' に備える）。
    /// </summary>
    /// <returns>閉じの ' の次の位置。</returns>
    private static int SkipCharLiteral(string text, int i)
    {
        int j = i + 1;
        while (j < text.Length && text[j] != '\'' && text[j] != '\n')
        {
            if (text[j] == '\\') j++;   // \' などのエスケープ
            j++;
        }
        return j < text.Length && text[j] == '\'' ? j + 1 : Math.Min(j, text.Length);
    }

    // ============================================================
    //  小道具
    // ============================================================

    /// <summary>位置から文字列が始まるか（序数比較）。</summary>
    private static bool StartsWithAt(string text, int i, string value) =>
        string.CompareOrdinal(text, i, value, 0, value.Length) == 0;

    /// <summary>位置から同じ文字が何個続くか。</summary>
    private static int CountRun(string text, int i, char c)
    {
        int n = 0;
        while (i + n < text.Length && text[i + n] == c) n++;
        return n;
    }

    /// <summary>行の終わり（改行の位置。無ければ最後）。</summary>
    private static int LineEnd(string text, int i)
    {
        int newline = text.IndexOf('\n', i);
        return newline < 0 ? text.Length : newline;
    }
}
