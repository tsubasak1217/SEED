// ============================================================
//  ConflictMarkerDocument.cs — 印つきのテキストを区画の列へ切り分ける
//
//  【Lore が書く印の形（実機で確認済み）】
//      <<<<<<< ours          … ここから「現在」側
//      ...
//      ||||||| original      … ここから「元」（省略されることがある）
//      ...
//      =======               … ここから「取り込み元」側
//      ...
//      >>>>>>> theirs        … ブロックの終わり
//  ラベル（ours / original / theirs）は **判定に使わない**。
//  ラベルは Lore の版や経路で変わり得るので、先頭の記号 7 文字だけで見る。
//
//  【壊れた入力は黙って直さない】
//  閉じていない・入れ子になっている印は、こちらで辻褄を合わせると
//  「利用者が意図していない中身」を書き戻すことになる。必ず例外で止める。
//
//  【BOM と改行】
//  BOM の有無と各行の改行はそのまま持つ。触っていない行を
//  バイト単位で戻せないと、解決した瞬間に全行が「変更」になる。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.VersionControl.Merge;

/// <summary>
/// 印つきテキストを解析した結果（不変）。
/// </summary>
public sealed class ConflictMarkerDocument
{
    // ── 印の綴り（マジック文字列の一元化）──────────────────────

    /// <summary>印の記号の繰り返し回数（diff3 形式は必ず 7 文字）。</summary>
    public const int MARKER_LENGTH = 7;

    /// <summary>「現在」側の始まりを表す記号。</summary>
    public const char MARKER_CURRENT = '<';

    /// <summary>「元」の始まりを表す記号。</summary>
    public const char MARKER_BASE = '|';

    /// <summary>「取り込み元」側の始まりを表す記号。</summary>
    public const char MARKER_SEPARATOR = '=';

    /// <summary>ブロックの終わりを表す記号。</summary>
    public const char MARKER_INCOMING_END = '>';

    /// <summary>印の記号すべて（残存検査で回す）。</summary>
    public static readonly IReadOnlyList<char> ALL_MARKERS =
        new[] { MARKER_CURRENT, MARKER_BASE, MARKER_SEPARATOR, MARKER_INCOMING_END };

    // ── 解析結果 ────────────────────────────────────────────

    /// <summary>ファイルを順に切り分けた区画。</summary>
    public IReadOnlyList<MergeSegment> Segments { get; }

    /// <summary>競合ブロックだけを取り出したもの（通し番号の順）。</summary>
    public IReadOnlyList<MergeSegment> Conflicts { get; }

    /// <summary>元のテキストに UTF-8 BOM が付いていたか。</summary>
    public bool HasUtf8Bom { get; }

    /// <summary>新しく足す行に使う改行（ファイル内で最も多い改行）。</summary>
    public string NewLine { get; }

    /// <summary>競合ブロックの数。</summary>
    public int ConflictCount => Conflicts.Count;

    /// <summary>全項目を指定して生成する（生成は <see cref="Parse"/> だけが行う）。</summary>
    /// <param name="segments">区画の列。</param>
    /// <param name="conflicts">競合ブロックだけを取り出したもの。</param>
    /// <param name="hasUtf8Bom">BOM が付いていたか。</param>
    /// <param name="newLine">新しく足す行に使う改行。</param>
    private ConflictMarkerDocument(
        IReadOnlyList<MergeSegment> segments,
        IReadOnlyList<MergeSegment> conflicts,
        bool hasUtf8Bom,
        string newLine)
    {
        Segments   = segments;
        Conflicts  = conflicts;
        HasUtf8Bom = hasUtf8Bom;
        NewLine    = newLine;
    }

    // ── 判定 ────────────────────────────────────────────────

    /// <summary>
    /// 指定の行が、指定の記号の印かどうか。
    ///
    /// <para>
    /// 記号が 7 文字続き、その後が「行末」か「空白」のときだけ印とみなす。
    /// これを緩めると、区切り線（Markdown の <c>=====</c> など）まで印に見えてしまう。
    /// </para>
    /// </summary>
    /// <param name="lineText">行の中身（改行を含まない）。</param>
    /// <param name="marker">印の記号。</param>
    public static bool IsMarkerLine(string lineText, char marker)
    {
        if (lineText.Length < MARKER_LENGTH) return false;

        for (var i = 0; i < MARKER_LENGTH; i++)
        {
            if (lineText[i] != marker) return false;
        }

        return lineText.Length == MARKER_LENGTH || lineText[MARKER_LENGTH] == ' ';
    }

    /// <summary>この行がいずれかの印か。</summary>
    /// <param name="lineText">行の中身。</param>
    public static bool IsAnyMarkerLine(string lineText)
    {
        foreach (var marker in ALL_MARKERS)
        {
            if (IsMarkerLine(lineText, marker)) return true;
        }
        return false;
    }

    /// <summary>
    /// テキストに競合ブロックの始まり（<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>）があるか。
    /// マージエディタを開けるか・「両方を取り込む」ができるかの入口の判定に使う。
    /// </summary>
    /// <param name="text">調べるテキスト（null 可）。</param>
    public static bool HasConflictMarkers(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var line in MergeTextLines.Split(StripBom(text, out _)))
        {
            if (IsMarkerLine(line.Text, MARKER_CURRENT)) return true;
        }
        return false;
    }

    // ── 解析 ────────────────────────────────────────────────

    /// <summary>
    /// 印つきテキストを区画の列へ切り分ける。
    /// </summary>
    /// <param name="text">印つきのテキスト（BOM が付いていてもよい）。</param>
    /// <exception cref="MergeParseException">印が閉じていない・入れ子などで解析できないとき。</exception>
    public static ConflictMarkerDocument Parse(string text)
    {
        var body  = StripBom(text ?? string.Empty, out var hasBom);
        var lines = MergeTextLines.Split(body);

        var segments  = new List<MergeSegment>();
        var conflicts = new List<MergeSegment>();
        var common    = new List<MergeLine>();

        var index = 0;
        while (index < lines.Count)
        {
            var lineText = lines[index].Text;

            // 競合ブロックの外に、始まり以外の印が転がっていたら壊れている。
            if (IsMarkerLine(lineText, MARKER_BASE)
                || IsMarkerLine(lineText, MARKER_SEPARATOR)
                || IsMarkerLine(lineText, MARKER_INCOMING_END))
            {
                throw new MergeParseException(MergeParseException.OrphanMarker(index + 1, lineText));
            }

            if (!IsMarkerLine(lineText, MARKER_CURRENT))
            {
                common.Add(lines[index]);
                index++;
                continue;
            }

            // ここから競合ブロック。溜めていた共通部分を先に確定させる。
            FlushCommon(segments, common);

            var block = ParseConflictBlock(lines, ref index, conflicts.Count);
            segments.Add(block);
            conflicts.Add(block);
        }

        FlushCommon(segments, common);

        return new ConflictMarkerDocument(
            segments, conflicts, hasBom, MergeTextLines.DominantNewLine(lines));
    }

    /// <summary>
    /// 解析を試み、壊れていたら理由を返す（例外を投げたくない呼び出し側向け）。
    /// </summary>
    /// <param name="text">印つきのテキスト。</param>
    /// <param name="document">解析結果（失敗時は null）。</param>
    /// <param name="error">失敗の理由（成功時は空文字）。</param>
    /// <returns>解析できたか。</returns>
    public static bool TryParse(
        string? text, out ConflictMarkerDocument? document, out string error)
    {
        try
        {
            document = Parse(text ?? string.Empty);
            error    = string.Empty;
            return true;
        }
        catch (MergeParseException ex)
        {
            document = null;
            error    = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 1 つの競合ブロックを読み取る。
    /// </summary>
    /// <param name="lines">ファイル全体の行。</param>
    /// <param name="index">
    /// 始まりの印を指している位置。読み終えると終わりの印の次を指す。
    /// </param>
    /// <param name="conflictIndex">この競合ブロックの通し番号。</param>
    private static MergeSegment ParseConflictBlock(
        IReadOnlyList<MergeLine> lines, ref int index, int conflictIndex)
    {
        var startLineNumber = index + 1;
        index++;    // 始まりの印を読み飛ばす

        // ── 「現在」側 ──
        var current = new List<MergeLine>();
        while (index < lines.Count
               && !IsMarkerLine(lines[index].Text, MARKER_BASE)
               && !IsMarkerLine(lines[index].Text, MARKER_SEPARATOR))
        {
            RejectNestedMarker(lines[index].Text, index + 1, startLineNumber);
            current.Add(lines[index]);
            index++;
        }
        if (index >= lines.Count)
            throw new MergeParseException(MergeParseException.Unterminated(startLineNumber));

        // ── 「元」（省略されることがある）──
        var baseLines = new List<MergeLine>();
        if (IsMarkerLine(lines[index].Text, MARKER_BASE))
        {
            index++;    // 「元」の印を読み飛ばす
            while (index < lines.Count && !IsMarkerLine(lines[index].Text, MARKER_SEPARATOR))
            {
                RejectNestedMarker(lines[index].Text, index + 1, startLineNumber);
                baseLines.Add(lines[index]);
                index++;
            }
            if (index >= lines.Count)
                throw new MergeParseException(MergeParseException.Unterminated(startLineNumber));
        }

        index++;    // 区切りの印（=======）を読み飛ばす

        // ── 「取り込み元」側 ──
        var incoming = new List<MergeLine>();
        while (index < lines.Count && !IsMarkerLine(lines[index].Text, MARKER_INCOMING_END))
        {
            RejectNestedMarker(lines[index].Text, index + 1, startLineNumber);
            incoming.Add(lines[index]);
            index++;
        }
        if (index >= lines.Count)
            throw new MergeParseException(MergeParseException.Unterminated(startLineNumber));

        index++;    // 終わりの印を読み飛ばす

        return MergeSegment.Conflict(conflictIndex, current, baseLines, incoming);
    }

    /// <summary>
    /// 競合ブロックの中に別の印が現れたら止める（入れ子は解釈しない）。
    /// </summary>
    /// <param name="lineText">行の中身。</param>
    /// <param name="lineNumber">その行の番号（1 始まり）。</param>
    /// <param name="blockStartLine">ブロックが始まった行番号。</param>
    private static void RejectNestedMarker(string lineText, int lineNumber, int blockStartLine)
    {
        if (IsAnyMarkerLine(lineText))
        {
            throw new MergeParseException(
                MergeParseException.NestedMarker(lineNumber, blockStartLine, lineText));
        }
    }

    /// <summary>溜めていた共通部分を区画として確定させる。</summary>
    /// <param name="segments">区画の列（追加先）。</param>
    /// <param name="common">溜めていた共通部分の行（呼び出し後は空になる）。</param>
    private static void FlushCommon(List<MergeSegment> segments, List<MergeLine> common)
    {
        if (common.Count == 0) return;

        segments.Add(MergeSegment.Common(common.ToArray()));
        common.Clear();
    }

    /// <summary>先頭の BOM 文字を取り除く。</summary>
    /// <param name="text">元のテキスト。</param>
    /// <param name="hasBom">BOM が付いていたか。</param>
    private static string StripBom(string text, out bool hasBom)
    {
        hasBom = text.Length > 0 && text[0] == MergeTextLines.BOM_CHAR;
        return hasBom ? text[1..] : text;
    }
}
