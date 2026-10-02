// ============================================================
//  ApiReferenceSectionSplitter.cs — 圧縮後のリファレンスを節と切れ端へ分ける
//
//  【節の境界】
//  見出し # / ## / ###（行頭の # の数 1〜3 と空白）で区切る。#### は節の中に残す
//  （「#### レシピ: …」のような小見出しは親の ### の一部として選ぶ）。
//  コードフェンスの中の # 行（#region など）は見出しにしない。
//  最初の見出しより前の行と、文書の題（#）の節は「§0」（前書き。Unity ではない等の重要注記が入る）。
//
//  【長すぎる節の切り分け】
//  節の単独の本文が max_part_chars を超えるときは、次の「ひと塊（ユニット）」の切れ目で切れ端に分ける。
//    - コードブロック: 空行で区切った段落ごと（それでも長ければ 1 行ごと）
//    - 表: 1 行ごと（表の頭＝見出しの行と区切りの行は最初のデータ行とくっつける）
//    - それ以外の行（#### の小見出し・重要注記）: 1 行ごと
//  ユニットを頭から詰め、次のユニットを足すと上限を超えるところで切る（1 つで上限を超える
//  ユニットはその切れ端に単独で入れる）。コードの途中で切れた切れ端にはフェンスの開き/閉じを、
//  表の途中から始まる切れ端には表の頭を補う（ApiReferencePart の Open / Close）。
//
//  【WPF 非依存】editor/tests/InlineCompletionTests がこのフォルダを丸ごとリンクする。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;

/// <summary>
/// 圧縮後のリファレンスを節（見出し # / ## / ###）と切れ端へ分ける。
/// </summary>
public static class ApiReferenceSectionSplitter
{
    /// <summary>節の境界にする見出しの最も深い段（### まで。#### は節の中に残す）。</summary>
    public const int MaxSectionHeadingLevel = 3;

    /// <summary>文書の題（#）の見出しの深さ。</summary>
    public const int TitleHeadingLevel = 1;

    /// <summary>番号つきの大きな節（##）の見出しの深さ。</summary>
    public const int ChapterHeadingLevel = 2;

    /// <summary>節の名前の頭の記号。</summary>
    public const string SectionMark = "§";

    /// <summary>前書き（見出しの無い先頭・文書の題）の節の名前の番号（"§0"）。</summary>
    public const string PreambleNumber = "0";

    /// <summary>### の節の名前で、親の名前と自分の短い題をつなぐ記号（"§7/Transform"）。</summary>
    public const string LabelSeparator = "/";

    /// <summary>節の名前に使う短い題の最大文字数（ログが長くなりすぎないように）。</summary>
    public const int MaxShortTitleChars = 24;

    /// <summary>短い題を切ったときの印。</summary>
    private const string Ellipsis = "…";

    /// <summary>短い題を切る位置の文字（見出しの補足の括弧の始まり）。</summary>
    private static readonly char[] TitleCutChars = { '（', '(' };

    /// <summary>表の頭の行数（見出しの行と区切りの行）。</summary>
    private const int TableHeadLineCount = 2;

    /// <summary>コードの途中で切れた切れ端の終わりに補う閉じのフェンス。</summary>
    private static readonly string ClosingFence = ApiReferenceCompactor.CodeFence + ApiReferenceCompactor.NewLine;

    /// <summary>見出しの番号（"7.18 …" の 7.18・"1. …" の 1）。</summary>
    private static readonly Regex NumberPattern =
        new(@"^(\d+(?:\.\d+)*)\.?(?=\s|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 圧縮後のテキストを節と切れ端へ分ける。
    /// </summary>
    /// <param name="compactText"><see cref="ApiReferenceCompactor.Compact"/> の結果。</param>
    /// <param name="maxPartChars">切れ端の単独の本文の上限（これを超える節を分ける）。</param>
    /// <returns>文書の順に並んだ切れ端。</returns>
    public static IReadOnlyList<ApiReferencePart> Split(string compactText, int maxPartChars)
    {
        var parts = new List<ApiReferencePart>();
        if (string.IsNullOrEmpty(compactText)) return parts;

        // ① 見出しで節に分ける
        var sections = ParseSections(compactText);

        // ② 節ごとに名前を付け、長すぎる節は切れ端に分ける
        string parentLabel = SectionMark + PreambleNumber;
        for (int sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
        {
            var section = sections[sectionIndex];
            string headingText = section.HeadingLine.TrimStart(ApiReferenceCompactor.HeadingMark).Trim();
            string? number = ParseNumber(headingText);
            string label = MakeLabel(section.Level, headingText, number, parentLabel);
            if (section.Level <= ChapterHeadingLevel) parentLabel = label;

            var headingWords = ApiIdentifierTokenizer.WordsOf(section.HeadingLine);
            var groups = Pack(section, maxPartChars);
            for (int k = 0; k < groups.Count; k++)
            {
                var (open, body, close) = groups[k];
                parts.Add(new ApiReferencePart(
                    index: parts.Count, sectionIndex: sectionIndex,
                    partNumber: k + 1, partCount: groups.Count,
                    headingLevel: section.Level, headingLine: section.HeadingLine,
                    headingText: headingText, number: number, sectionLabel: label,
                    open: open, body: body, close: close, headingWords: headingWords));
            }
        }
        return parts;
    }

    // ── 節に分ける ───────────────────────────────────────────

    /// <summary>見出しで区切った節 1 つ（見出し行と本文の行）。</summary>
    /// <param name="Level">見出しの深さ（見出しの無い先頭は 0）。</param>
    /// <param name="HeadingLine">見出し行（無ければ空）。</param>
    /// <param name="Lines">本文の行（改行を含まない）。</param>
    private sealed record RawSection(int Level, string HeadingLine, List<string> Lines);

    /// <summary>圧縮後のテキストを見出し（# / ## / ###）で節に分ける。中身の無い先頭は捨てる。</summary>
    /// <param name="compactText">圧縮後のテキスト。</param>
    /// <returns>文書の順の節。</returns>
    private static List<RawSection> ParseSections(string compactText)
    {
        var sections = new List<RawSection>();
        var current = new RawSection(ApiReferencePart.NoHeadingLevel, string.Empty, new List<string>());
        bool inCode = false;

        var lines = compactText.Split(ApiReferenceCompactor.NewLine);
        // 末尾の改行の後の空の要素は行ではない
        int lineCount = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        for (int i = 0; i < lineCount; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // フェンスの中の # 行（#region・#if など）は見出しにしない
            if (trimmed.StartsWith(ApiReferenceCompactor.CodeFence, StringComparison.Ordinal)) inCode = !inCode;
            else if (!inCode && TryGetHeadingLevel(trimmed, out int level) && level <= MaxSectionHeadingLevel)
            {
                AddIfNotEmpty(sections, current);
                current = new RawSection(level, line, new List<string>());
                continue;
            }
            current.Lines.Add(line);
        }
        AddIfNotEmpty(sections, current);
        return sections;
    }

    /// <summary>見出しも本文も無い節（文書の頭に何も無いとき）は足さない。</summary>
    private static void AddIfNotEmpty(List<RawSection> sections, RawSection section)
    {
        if (section.HeadingLine.Length > 0 || section.Lines.Count > 0) sections.Add(section);
    }

    /// <summary>
    /// 行が見出し（# が 1 つ以上続き、その後が空白か行末）なら深さを返す。
    /// </summary>
    /// <param name="trimmed">先頭の空白を除いた行。</param>
    /// <param name="level"># の数。</param>
    /// <returns>見出しなら true。</returns>
    public static bool TryGetHeadingLevel(string trimmed, out int level)
    {
        level = 0;
        while (level < trimmed.Length && trimmed[level] == ApiReferenceCompactor.HeadingMark) level++;
        if (level == 0) return false;
        return level == trimmed.Length || char.IsWhiteSpace(trimmed[level]);
    }

    // ── 名前 ─────────────────────────────────────────────────

    /// <summary>見出しの文から番号を取り出す（"7.18 画面…" → "7.18"・"1. スクリプト…" → "1"）。</summary>
    /// <param name="headingText">見出しの文。</param>
    /// <returns>番号（無ければ null）。</returns>
    public static string? ParseNumber(string headingText)
    {
        var match = NumberPattern.Match(headingText);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// 節の短い名前を作る。前書き・題は "§0"、## は "§番号"（番号が無ければ "§短い題"）、
    /// ### は "親の名前/短い題"（"§7/Transform"）。
    /// </summary>
    private static string MakeLabel(int level, string headingText, string? number, string parentLabel)
    {
        if (level <= TitleHeadingLevel) return SectionMark + PreambleNumber;
        if (level == ChapterHeadingLevel) return SectionMark + (number ?? ShortTitle(headingText, number));
        return parentLabel + LabelSeparator + ShortTitle(headingText, number);
    }

    /// <summary>
    /// 見出しの文から短い題を作る（番号と補足の括弧を落とし、長ければ切る）。
    /// </summary>
    /// <param name="headingText">見出しの文。</param>
    /// <param name="number">見出しの番号（落とす）。</param>
    /// <returns>短い題。</returns>
    public static string ShortTitle(string headingText, string? number)
    {
        var title = headingText;
        if (number is not null)
            title = title[number.Length..].TrimStart('.').Trim();
        int cut = title.IndexOfAny(TitleCutChars);
        if (cut > 0) title = title[..cut].Trim();
        if (title.Length == 0) title = headingText.Trim();
        return title.Length > MaxShortTitleChars ? title[..MaxShortTitleChars] + Ellipsis : title;
    }

    // ── 切れ端に分ける ───────────────────────────────────────

    /// <summary>ユニットの種類。</summary>
    private enum UnitKind
    {
        /// <summary>コードブロックの段落（または 1 行）。</summary>
        Code,
        /// <summary>表の行（最初のユニットは表の頭つき）。</summary>
        Table,
        /// <summary>それ以外の 1 行（#### の小見出し・重要注記）。</summary>
        Plain,
    }

    /// <summary>
    /// 切れ端を詰めるときの分けられない塊。
    /// </summary>
    /// <param name="Kind">種類。</param>
    /// <param name="Text">元の行（各行は LF で終わる）。</param>
    /// <param name="IsBlockStart">コード・表の最初のユニットか（前置きが要らない）。</param>
    /// <param name="IsBlockEnd">コードの最後のユニットか（閉じのフェンスを含む＝補わない）。</param>
    /// <param name="BlockOpen">ブロックの途中から始まるときの前置き（フェンスの開き・表の頭）。</param>
    private sealed record Unit(UnitKind Kind, string Text, bool IsBlockStart, bool IsBlockEnd, string BlockOpen);

    /// <summary>
    /// 節の本文を切れ端（前置き・本文・後置き）の並びにする。上限内の節は 1 つのまま返す。
    /// </summary>
    /// <param name="section">節。</param>
    /// <param name="maxPartChars">切れ端の単独の本文の上限。</param>
    /// <returns>切れ端ごとの（Open, Body, Close）。</returns>
    private static List<(string Open, string Body, string Close)> Pack(RawSection section, int maxPartChars)
    {
        string headingPrefix = ApiReferencePart.HeadingPrefix(section.HeadingLine);
        string wholeBody = JoinLines(section.Lines, 0, section.Lines.Count - 1);
        var groups = new List<(string Open, string Body, string Close)>();

        // 上限内なら分けない（元の本文のまま）
        if (headingPrefix.Length + wholeBody.Length <= maxPartChars)
        {
            groups.Add((string.Empty, wholeBody, string.Empty));
            return groups;
        }

        // 見出しの分を除いた残りが 1 ユニットの目安（これを超える段落は 1 行ずつに割る）
        int unitBudget = Math.Max(1, maxPartChars - headingPrefix.Length);
        var units = BuildUnits(section.Lines, unitBudget);

        // ユニットを頭から詰める。次を足すと上限を超えるなら、そこで切れ端を閉じる
        var current = new List<Unit>();
        int currentBodyLength = 0;
        foreach (var unit in units)
        {
            if (current.Count > 0)
            {
                int candidate = headingPrefix.Length + OpenOf(current[0]).Length
                              + currentBodyLength + unit.Text.Length + CloseOf(unit).Length;
                if (candidate > maxPartChars)
                {
                    groups.Add(Finish(current));
                    current.Clear();
                    currentBodyLength = 0;
                }
            }
            current.Add(unit);
            currentBodyLength += unit.Text.Length;
        }
        if (current.Count > 0) groups.Add(Finish(current));
        return groups;
    }

    /// <summary>詰め終えたユニットの並びを（前置き・本文・後置き）にする。</summary>
    private static (string Open, string Body, string Close) Finish(List<Unit> units)
    {
        var body = new StringBuilder();
        foreach (var unit in units) body.Append(unit.Text);
        return (OpenOf(units[0]), body.ToString(), CloseOf(units[^1]));
    }

    /// <summary>このユニットから切れ端が始まるときの前置き（ブロックの途中からならフェンスの開き・表の頭）。</summary>
    private static string OpenOf(Unit first) => first.IsBlockStart ? string.Empty : first.BlockOpen;

    /// <summary>このユニットで切れ端が終わるときの後置き（コードの途中で終わるなら閉じのフェンス）。</summary>
    private static string CloseOf(Unit last) =>
        last.Kind == UnitKind.Code && !last.IsBlockEnd ? ClosingFence : string.Empty;

    /// <summary>
    /// 節の本文の行をユニットの並びにする（コードブロック・表・それ以外の行を見分ける）。
    /// </summary>
    /// <param name="lines">本文の行。</param>
    /// <param name="unitBudget">1 ユニットの目安の上限（超えるコードの段落は 1 行ずつに割る）。</param>
    /// <returns>文書の順のユニット。</returns>
    private static List<Unit> BuildUnits(List<string> lines, int unitBudget)
    {
        var units = new List<Unit>();
        int i = 0;
        while (i < lines.Count)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(ApiReferenceCompactor.CodeFence, StringComparison.Ordinal))
            {
                // コードブロック: 閉じのフェンスまで（閉じが無ければ節の終わりまで）
                int end = i + 1;
                while (end < lines.Count
                       && !lines[end].TrimStart().StartsWith(ApiReferenceCompactor.CodeFence, StringComparison.Ordinal))
                {
                    end++;
                }
                int last = Math.Min(end, lines.Count - 1);
                AddCodeUnits(units, lines, i, last, unitBudget);
                i = last + 1;
            }
            else if (trimmed.Length > 0 && trimmed[0] == ApiReferenceCompactor.TableMark)
            {
                // 表: | で始まる行が続くところまで
                int last = i;
                while (last + 1 < lines.Count && IsTableLine(lines[last + 1])) last++;
                AddTableUnits(units, lines, i, last);
                i = last + 1;
            }
            else
            {
                // それ以外（#### の小見出し・重要注記）は 1 行ずつ
                units.Add(new Unit(UnitKind.Plain, JoinLines(lines, i, i), IsBlockStart: true, IsBlockEnd: true, BlockOpen: string.Empty));
                i++;
            }
        }
        return units;
    }

    /// <summary>表の行か（先頭の空白を除いて | で始まる）。</summary>
    private static bool IsTableLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 0 && trimmed[0] == ApiReferenceCompactor.TableMark;
    }

    /// <summary>
    /// コードブロック（first 行目の開きのフェンスから last 行目まで）をユニットにする。
    /// 空行で区切った段落を 1 ユニットにし、目安を超える段落は 1 行ずつに割る
    /// （開きのフェンスは次の行と、閉じのフェンスは前の行とくっつける）。
    /// </summary>
    private static void AddCodeUnits(List<Unit> units, List<string> lines, int first, int last, int unitBudget)
    {
        string blockOpen = lines[first] + ApiReferenceCompactor.NewLine;
        int paragraphStart = first;
        for (int k = first; k <= last; k++)
        {
            // ブロックの中の空行（フェンス行以外）で段落を閉じる。最後の行でも閉じる
            bool blankInside = k > first && k < last && lines[k].Trim().Length == 0;
            if (!blankInside && k != last) continue;

            AddCodeParagraph(units, lines, paragraphStart, k, first, last, blockOpen, unitBudget);
            paragraphStart = k + 1;
        }
    }

    /// <summary>コードの段落 1 つ（start〜end 行目）をユニットにする（目安を超えたら 1 行ずつ）。</summary>
    private static void AddCodeParagraph(
        List<Unit> units, List<string> lines, int start, int end,
        int blockFirst, int blockLast, string blockOpen, int unitBudget)
    {
        string paragraph = JoinLines(lines, start, end);
        if (paragraph.Length <= unitBudget)
        {
            units.Add(new Unit(UnitKind.Code, paragraph, start == blockFirst, end == blockLast, blockOpen));
            return;
        }

        // 長すぎる段落は 1 行ずつ。開きのフェンスは次の行と、閉じのフェンスは前の行とくっつける
        int lineStart = start;
        for (int k = start; k <= end; k++)
        {
            bool glueToNext = k == blockFirst && k < end;              // 開きのフェンスの行
            bool nextIsClosingFence = k + 1 == blockLast && k + 1 <= end
                                      && lines[blockLast].TrimStart().StartsWith(ApiReferenceCompactor.CodeFence, StringComparison.Ordinal);
            if (glueToNext || nextIsClosingFence) continue;

            units.Add(new Unit(UnitKind.Code, JoinLines(lines, lineStart, k),
                               lineStart == blockFirst, k == blockLast, blockOpen));
            lineStart = k + 1;
        }
    }

    /// <summary>
    /// 表（first〜last 行目）をユニットにする。表の頭（見出しの行と区切りの行）は最初のデータ行と
    /// くっつけ、残りは 1 行ずつ。途中から始まる切れ端には表の頭を補う。
    /// </summary>
    private static void AddTableUnits(List<Unit> units, List<string> lines, int first, int last)
    {
        int headLast = Math.Min(first + TableHeadLineCount - 1, last);
        string head = JoinLines(lines, first, headLast);
        // 頭の後にデータ行があれば最初の 1 行をくっつける（頭だけが切れ端の終わりに残らないように）
        int firstUnitLast = headLast < last ? headLast + 1 : headLast;
        units.Add(new Unit(UnitKind.Table, JoinLines(lines, first, firstUnitLast), IsBlockStart: true, IsBlockEnd: true, BlockOpen: head));
        for (int k = firstUnitLast + 1; k <= last; k++)
            units.Add(new Unit(UnitKind.Table, JoinLines(lines, k, k), IsBlockStart: false, IsBlockEnd: true, BlockOpen: head));
    }

    /// <summary>start〜end 行目を改行つきでつなぐ（end &lt; start なら空）。</summary>
    private static string JoinLines(List<string> lines, int start, int end)
    {
        if (end < start) return string.Empty;
        var sb = new StringBuilder();
        for (int k = start; k <= end; k++) sb.Append(lines[k]).Append(ApiReferenceCompactor.NewLine);
        return sb.ToString();
    }
}
