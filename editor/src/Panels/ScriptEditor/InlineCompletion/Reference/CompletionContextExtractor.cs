// ============================================================
//  CompletionContextExtractor.cs — 編集中のファイルとカーソルの位置から文脈の語を集める
//
//  【集めるもの】（正規表現 1 回の走査で全部拾う）
//  - using の名前空間の語    : using SEED.Localization; → Localization（種類 Using）
//  - SEED. に続く語          : SEED.UI.ScreenStack → UI・ScreenStack（種類 EngineQualified）
//  - それ以外の識別子        : 型名・メソッド名・変数名・コメントの英単語（種類 File）
//  - カーソルの前後 N 行の語 : 上のどれでも、近くに出たら種類 NearCursor を足す
//  - 書きかけの語            : カーソルの直前の識別子（"Bind|"）。直前が "." なら
//                              その前の識別子（"Bind.|" の Bind）。種類 Typing
//  捨てる語: 1 文字の語・C# のキーワード（ApiIdentifierTokenizer）と設定の ignored_words。
//  文字列・文字のリテラルの中身は数えない（"assets://alarm/prefabs/edit.actor" の edit・actor や
//  L10n のキーのような、API 名でない語が関係の薄い節に点を付けてしまうため）。コメントの中は数える
//  （「// ScreenStack に積む」のように、書こうとしている API の名前が出ることが多いため）。
//
//  【WPF 非依存】editor/tests/InlineCompletionTests がこのフォルダを丸ごとリンクする。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;

/// <summary>
/// 編集中のファイルから <see cref="CompletionContext"/> を作る。
/// </summary>
public static class CompletionContextExtractor
{
    /// <summary>エンジンの名前空間の根（この後に続く語を SEED の API 名として重く数える）。</summary>
    public const string EngineNamespaceRoot = "SEED";

    /// <summary>名前空間・メンバの区切り。</summary>
    private const char MemberSeparator = '.';

    /// <summary>行の区切り。</summary>
    private const char LineBreak = '\n';

    /// <summary>正規表現のグループ名: using 指令の全体。</summary>
    private const string UsingGroup = "using";

    /// <summary>正規表現のグループ名: using 指令の名前空間。</summary>
    private const string NamespaceGroup = "ns";

    /// <summary>正規表現のグループ名: SEED. で始まる修飾の並び。</summary>
    private const string QualifiedGroup = "qualified";

    /// <summary>正規表現のグループ名: 文字列・文字のリテラル（中身は数えずに読み飛ばす）。</summary>
    private const string LiteralGroup = "literal";

    /// <summary>
    /// 文脈の語を 1 回の走査で拾う正規表現。左の選択肢ほど優先する。
    ///   ① using 指令（行頭。static・別名つきも可）の名前空間
    ///   ② SEED. で始まる修飾の並び（直前が英数字・_ でないこと）
    ///   ③ 文字列・文字のリテラル（逐語的文字列 @"…"・普通の文字列 "…"（1 行・エスケープ可）・文字 'x'）
    ///   ④ それ以外の識別子
    /// </summary>
    private static readonly Regex ContextPattern = new(
        "(?<" + UsingGroup + ">^[ \\t]*using[ \\t]+(?:static[ \\t]+)?" +
            "(?:" + ApiIdentifierTokenizer.IdentifierPatternText + "[ \\t]*=[ \\t]*)?" +
            "(?<" + NamespaceGroup + ">" + ApiIdentifierTokenizer.IdentifierPatternText +
                "(?:\\." + ApiIdentifierTokenizer.IdentifierPatternText + ")*)[ \\t]*;)" +
        "|(?<" + QualifiedGroup + ">(?<![A-Za-z0-9_])" + EngineNamespaceRoot +
            "(?:\\." + ApiIdentifierTokenizer.IdentifierPatternText + ")+)" +
        "|(?<" + LiteralGroup + ">@\"(?:[^\"]|\"\")*\"|\"(?:[^\"\\\\\\n]|\\\\.)*\"|'(?:[^'\\\\\\n]|\\\\.)')" +
        "|" + ApiIdentifierTokenizer.IdentifierPatternText,
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// ファイル全文とカーソルの位置から文脈の語を集める。
    /// </summary>
    /// <param name="text">編集中のファイルの全文。</param>
    /// <param name="caretOffset">カーソルの位置（文字の添字。範囲外は丸める）。</param>
    /// <param name="settings">選び方の設定（近くの行数・捨てる語・重み）。</param>
    /// <returns>文脈。</returns>
    public static CompletionContext Extract(string text, int caretOffset, ApiReferenceSettings settings)
    {
        text ??= string.Empty;
        int caret = Math.Clamp(caretOffset, 0, text.Length);

        // ① カーソルの前後 N 行の範囲（文字の添字の半開区間）
        int windowStart = LineStartAbove(text, caret, settings.CursorWindowLines);
        int windowEnd = LineEndBelow(text, caret, settings.CursorWindowLines);

        // ② 全文を 1 回だけ走査して語と出方を集める
        var kinds = new Dictionary<string, ContextWordKinds>(ApiIdentifierTokenizer.WordComparer);
        foreach (Match match in ContextPattern.Matches(text))
        {
            var near = match.Index >= windowStart && match.Index < windowEnd
                ? ContextWordKinds.NearCursor
                : ContextWordKinds.None;

            if (match.Groups[LiteralGroup].Success)
            {
                // 文字列・文字のリテラルの中身は API 名ではないので数えない
                continue;
            }
            if (match.Groups[UsingGroup].Success)
            {
                // using SEED.Localization; → SEED（捨てる語）・Localization
                foreach (var segment in match.Groups[NamespaceGroup].Value.Split(MemberSeparator))
                    Note(kinds, segment, ContextWordKinds.File | ContextWordKinds.Using | near, settings);
            }
            else if (match.Groups[QualifiedGroup].Success)
            {
                // SEED.UI.ScreenStack → UI・ScreenStack（先頭の SEED は飛ばす）
                var segments = match.Value.Split(MemberSeparator);
                for (int i = 1; i < segments.Length; i++)
                    Note(kinds, segments[i], ContextWordKinds.File | ContextWordKinds.EngineQualified | near, settings);
            }
            else
            {
                Note(kinds, match.Value, ContextWordKinds.File | near, settings);
            }
        }

        // ③ 書きかけの語（全文の走査でも拾っているので、出方 Typing を足す）
        string? typing = FindTypingWord(text, caret);
        if (typing is not null && Accepts(typing, settings))
            Note(kinds, typing, ContextWordKinds.File | ContextWordKinds.NearCursor | ContextWordKinds.Typing, settings);
        else
            typing = null;

        return new CompletionContext(kinds, settings.Weights, typing);
    }

    /// <summary>
    /// カーソルの直前で書きかけの語を返す。直前が識別子ならそれ（"Bind|" → Bind）、
    /// 直前が "." ならその前の識別子（"Bind.|" → Bind）。無ければ null。
    /// </summary>
    /// <param name="text">全文。</param>
    /// <param name="caret">カーソルの位置。</param>
    /// <returns>書きかけの語。</returns>
    public static string? FindTypingWord(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var word = IdentifierEndingAt(text, caret);
        if (word.Length == 0 && caret > 0 && text[caret - 1] == MemberSeparator)
            word = IdentifierEndingAt(text, caret - 1);
        return word.Length == 0 ? null : word;
    }

    /// <summary>end の直前で終わる識別子（無ければ空）。数字で始まる並びは数字を飛ばす。</summary>
    private static string IdentifierEndingAt(string text, int end)
    {
        int start = end;
        while (start > 0 && ApiIdentifierTokenizer.IsIdentifierPart(text[start - 1])) start--;
        while (start < end && !ApiIdentifierTokenizer.IsIdentifierStart(text[start])) start++;
        return text[start..end];
    }

    /// <summary>語を数えるか（短すぎず・キーワードでなく・捨てる語でない）。</summary>
    private static bool Accepts(string word, ApiReferenceSettings settings) =>
        ApiIdentifierTokenizer.IsCandidate(word) && !settings.IgnoredWords.Contains(word);

    /// <summary>語の出方を重ねて記録する（捨てる語は記録しない）。</summary>
    private static void Note(Dictionary<string, ContextWordKinds> kinds, string word, ContextWordKinds kind, ApiReferenceSettings settings)
    {
        if (!Accepts(word, settings)) return;
        kinds[word] = kinds.TryGetValue(word, out var already) ? already | kind : kind;
    }

    /// <summary>カーソルの行から linesUp 行上の行の頭（文字の添字）。</summary>
    private static int LineStartAbove(string text, int caret, int linesUp)
    {
        // カーソルの行の頭
        int start = caret > 0 ? text.LastIndexOf(LineBreak, caret - 1) + 1 : 0;
        for (int i = 0; i < linesUp && start > 0; i++)
        {
            // start - 1 は前の行の終わりの改行。その前の改行の次が前の行の頭
            start = start >= 2 ? text.LastIndexOf(LineBreak, start - 2) + 1 : 0;
        }
        return start;
    }

    /// <summary>カーソルの行から linesDown 行下の行の終わり（改行の次。文字の添字）。</summary>
    private static int LineEndBelow(string text, int caret, int linesDown)
    {
        int end = text.IndexOf(LineBreak, caret);
        for (int i = 0; i < linesDown && end >= 0; i++) end = text.IndexOf(LineBreak, end + 1);
        return end < 0 ? text.Length : end + 1;
    }
}
