// ============================================================
//  ApiReferencePart.cs — リファレンスの節（またはその切れ端）1 つ。選ぶ・数える・並べるの単位
//
//  【節と切れ端】
//  圧縮後のリファレンスを見出し（# / ## / ###）で節に分ける。節が長すぎる
//  （設定 max_part_chars を超える）ときは、コードの段落・表の行の切れ目で「切れ端」に分ける。
//  分けなかった節は切れ端 1 つ（PartCount = 1）。選択・予算の計算はこの単位で行う。
//
//  【単独の本文と、続けて並べたときの本文】
//  切れ端の単独の本文 = 見出し行 ＋ Open ＋ Body ＋ Close。
//    Open  … コードの途中から始まる切れ端のフェンスの開き（"```csharp"）、
//             表の途中から始まる切れ端の表の頭（見出しの行と区切りの行）。
//    Close … コードの途中で終わる切れ端のフェンスの閉じ（"```"）。
//  同じ節の隣り合う切れ端を続けて並べるときは、後ろの見出し・Open と前の Close を省く
//  （＝全部の切れ端を並べると元の節の本文とまったく同じになる）。
//
//  【WPF 非依存】editor/tests/InlineCompletionTests がこのフォルダを丸ごとリンクする。
// ============================================================

using System.Collections.Generic;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;

/// <summary>
/// リファレンスの節（またはその切れ端）1 つ。生成は <see cref="ApiReferenceSectionSplitter"/>。
/// </summary>
public sealed class ApiReferencePart
{
    /// <summary>見出しの無い節（最初の見出しより前の行）の見出しの深さ。</summary>
    public const int NoHeadingLevel = 0;

    /// <summary>全切れ端の通し番号（文書の順。0 から）。</summary>
    public int Index { get; }

    /// <summary>属する節の通し番号（文書の順。0 から）。同じ節の切れ端は同じ値。</summary>
    public int SectionIndex { get; }

    /// <summary>節の中での切れ端の番号（1 から）。</summary>
    public int PartNumber { get; }

    /// <summary>節の切れ端の数（分けなかった節は 1）。</summary>
    public int PartCount { get; }

    /// <summary>見出しの深さ（# = 1・## = 2・### = 3。見出しの無い先頭は 0）。</summary>
    public int HeadingLevel { get; }

    /// <summary>見出し行そのもの（"## 7.18 画面の組み立て（…）"。見出しの無い先頭は空）。</summary>
    public string HeadingLine { get; }

    /// <summary>見出しの文（先頭の # と空白を除いたもの。"7.18 画面の組み立て（…）"）。常に入れる節の照合に使う。</summary>
    public string HeadingText { get; }

    /// <summary>見出しの番号（"7.18"・"1"。番号の無い見出しは null）。</summary>
    public string? Number { get; }

    /// <summary>節の短い名前（ログ用。"§7.18"・"§7/Transform"・"§0"）。</summary>
    public string SectionLabel { get; }

    /// <summary>切れ端の短い名前（分けた節は "§7.18[2/5]"、分けなかった節は <see cref="SectionLabel"/> と同じ）。</summary>
    public string Label { get; }

    /// <summary>単独で並べるときに本文の前へ足す行（コードのフェンスの開き・表の頭。無ければ空）。</summary>
    public string Open { get; }

    /// <summary>元の行（各行は LF で終わる）。</summary>
    public string Body { get; }

    /// <summary>単独で並べるときに本文の後へ足す行（コードのフェンスの閉じ。無ければ空）。</summary>
    public string Close { get; }

    /// <summary>単独で並べたときの本文（見出し行 ＋ Open ＋ Body ＋ Close）。</summary>
    public string Text { get; }

    /// <summary>単独で並べたときの文字数（予算の計算に使う。続けて並べるとこれ以下になる）。</summary>
    public int Length => Text.Length;

    /// <summary>見出しの語（大文字小文字を区別しない集合）。切れ端はすべて節の見出しの語を持つ。</summary>
    public IReadOnlySet<string> HeadingWords { get; }

    /// <summary>本文の語（元の行のコード・表・注記の識別子。大文字小文字を区別しない集合）。</summary>
    public IReadOnlySet<string> BodyWords { get; }

    /// <summary>切れ端を作る（<see cref="ApiReferenceSectionSplitter"/> から）。</summary>
    /// <param name="index">全切れ端の通し番号。</param>
    /// <param name="sectionIndex">節の通し番号。</param>
    /// <param name="partNumber">節の中での番号（1 から）。</param>
    /// <param name="partCount">節の切れ端の数。</param>
    /// <param name="headingLevel">見出しの深さ。</param>
    /// <param name="headingLine">見出し行。</param>
    /// <param name="headingText">見出しの文。</param>
    /// <param name="number">見出しの番号。</param>
    /// <param name="sectionLabel">節の短い名前。</param>
    /// <param name="open">単独のときの前置き。</param>
    /// <param name="body">元の行。</param>
    /// <param name="close">単独のときの後置き。</param>
    /// <param name="headingWords">見出しの語。</param>
    public ApiReferencePart(
        int index, int sectionIndex, int partNumber, int partCount,
        int headingLevel, string headingLine, string headingText, string? number, string sectionLabel,
        string open, string body, string close, IReadOnlySet<string> headingWords)
    {
        Index = index;
        SectionIndex = sectionIndex;
        PartNumber = partNumber;
        PartCount = partCount;
        HeadingLevel = headingLevel;
        HeadingLine = headingLine;
        HeadingText = headingText;
        Number = number;
        SectionLabel = sectionLabel;
        Label = partCount > 1 ? $"{sectionLabel}[{partNumber}/{partCount}]" : sectionLabel;
        Open = open;
        Body = body;
        Close = close;
        Text = HeadingPrefix(headingLine) + open + body + close;
        HeadingWords = headingWords;
        // 語は元の行だけから取る（Open の表の頭やフェンスは前の切れ端の繰り返しなので数えない）
        BodyWords = ApiIdentifierTokenizer.WordsOf(body);
    }

    /// <summary>この切れ端の直後に、同じ節の次の切れ端が続くか（続けて並べるときの判定）。</summary>
    /// <param name="next">後ろに並べる切れ端。</param>
    /// <returns>同じ節の次の番号なら true。</returns>
    public bool IsFollowedBy(ApiReferencePart next) =>
        next.SectionIndex == SectionIndex && next.PartNumber == PartNumber + 1;

    /// <summary>見出し行を本文の頭に置く形にする（見出しの無い先頭は空）。</summary>
    /// <param name="headingLine">見出し行。</param>
    /// <returns>見出し行 ＋ 改行（見出しが無ければ空）。</returns>
    public static string HeadingPrefix(string headingLine) =>
        headingLine.Length == 0 ? string.Empty : headingLine + ApiReferenceCompactor.NewLine;
}
