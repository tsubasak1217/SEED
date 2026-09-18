// ============================================================
//  MergeAlignedView.cs — 上段 2 面を「行を揃えて」並べるための表示モデル
//
//  【なぜ揃える必要があるのか】
//  取り込み元と現在を左右に並べても、競合ブロックの行数は普通は違う。
//  素直に並べると 1 ブロック目の途中から左右がずれ、以降ずっと
//  「違う場所どうし」を見比べることになる。そこで **ブロック単位で行数を揃え**、
//  短い側に詰め物の行を入れる。こうすると同じ Y にある行は必ず同じブロックに属する
//  ＝ 2 面の縦スクロールを単純に同期させるだけで意味が通る。
//
//  【行の中身は差分の結果】
//  各側は「元（base）との差分」で色が決まる（共通 / 追加 / 削除）。
//  削除された行は、その側の表示に赤で **差し込んで** 見せる
//  （消えたことは、消えた行が見えないと分からないため）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.VersionControl.Merge;

/// <summary>
/// 上段に並べる 1 行の種類。
/// </summary>
public enum MergeRowKind
{
    /// <summary>印の外側、または元と同じ行（色を付けない）。</summary>
    Common,

    /// <summary>その側が足した行（緑）。</summary>
    Added,

    /// <summary>元にあって、その側では消えた行（赤）。</summary>
    Removed,

    /// <summary>反対側にしか行が無い場所の詰め物（斜線）。</summary>
    Padding,
}

/// <summary>
/// 上段に並べる 1 行（不変）。
/// </summary>
/// <param name="Kind">行の種類。</param>
/// <param name="Text">表示する中身（詰め物は空文字）。</param>
/// <param name="ConflictIndex">属する競合ブロックの通し番号。共通部分は -1。</param>
public readonly record struct MergeDisplayRow(MergeRowKind Kind, string Text, int ConflictIndex);

/// <summary>
/// 競合ブロックが表示上どこからどこまでを占めるか（0 始まり・両端を含む）。
/// </summary>
/// <param name="ConflictIndex">競合ブロックの通し番号。</param>
/// <param name="FirstRow">先頭の表示行。</param>
/// <param name="LastRow">末尾の表示行。</param>
public readonly record struct MergeBlockRange(int ConflictIndex, int FirstRow, int LastRow)
{
    /// <summary>占める表示行数。</summary>
    public int RowCount => LastRow - FirstRow + 1;
}

/// <summary>
/// 上段 2 面ぶんの表示モデル（不変）。左右の行数は必ず等しい。
/// </summary>
public sealed class MergeAlignedView
{
    /// <summary>「取り込み元」面の行。</summary>
    public IReadOnlyList<MergeDisplayRow> IncomingRows { get; }

    /// <summary>「現在」面の行。</summary>
    public IReadOnlyList<MergeDisplayRow> CurrentRows { get; }

    /// <summary>競合ブロックが占める表示行の範囲（通し番号の順）。</summary>
    public IReadOnlyList<MergeBlockRange> Blocks { get; }

    /// <summary>表示行数（左右で同じ）。</summary>
    public int RowCount => IncomingRows.Count;

    /// <summary>全項目を指定して生成する（生成は <see cref="Build"/> だけが行う）。</summary>
    /// <param name="incomingRows">「取り込み元」面の行。</param>
    /// <param name="currentRows">「現在」面の行。</param>
    /// <param name="blocks">競合ブロックの表示範囲。</param>
    private MergeAlignedView(
        IReadOnlyList<MergeDisplayRow> incomingRows,
        IReadOnlyList<MergeDisplayRow> currentRows,
        IReadOnlyList<MergeBlockRange> blocks)
    {
        IncomingRows = incomingRows;
        CurrentRows  = currentRows;
        Blocks       = blocks;
    }

    /// <summary>
    /// 解析済みの文書から、行を揃えた表示モデルを作る。
    /// </summary>
    /// <param name="document">解析済みの印つきテキスト。</param>
    public static MergeAlignedView Build(ConflictMarkerDocument document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        var incoming = new List<MergeDisplayRow>();
        var current  = new List<MergeDisplayRow>();
        var blocks   = new List<MergeBlockRange>();

        foreach (var segment in document.Segments)
        {
            if (segment.Kind == MergeSegmentKind.Common)
            {
                // 共通部分は同じ行を両側へ入れる（ここでずらすと以降が全部ずれる）。
                foreach (var line in segment.CommonLines)
                {
                    var row = new MergeDisplayRow(MergeRowKind.Common, line.Text, -1);
                    incoming.Add(row);
                    current.Add(row);
                }
                continue;
            }

            var firstRow = incoming.Count;

            var baseTexts     = MergeTextLines.ToTexts(segment.BaseLines);
            var incomingRows  = ToRows(
                MergeLineDiff.Diff(baseTexts, MergeTextLines.ToTexts(segment.IncomingLines)),
                segment.ConflictIndex);
            var currentRows   = ToRows(
                MergeLineDiff.Diff(baseTexts, MergeTextLines.ToTexts(segment.CurrentLines)),
                segment.ConflictIndex);

            incoming.AddRange(incomingRows);
            current.AddRange(currentRows);

            // 短い側へ詰め物を入れて、ブロックの行数を揃える。
            Pad(incoming, currentRows.Count - incomingRows.Count, segment.ConflictIndex);
            Pad(current,  incomingRows.Count - currentRows.Count, segment.ConflictIndex);

            // 両側とも 0 行（元も両側も空）のブロックでも、チェックを置く場所が要る。
            if (incoming.Count == firstRow)
            {
                Pad(incoming, 1, segment.ConflictIndex);
                Pad(current,  1, segment.ConflictIndex);
            }

            blocks.Add(new MergeBlockRange(segment.ConflictIndex, firstRow, incoming.Count - 1));
        }

        return new MergeAlignedView(incoming, current, blocks);
    }

    /// <summary>
    /// 表示行を、エディタへ流し込む 1 つのテキストへ組み立てる。
    /// 表示専用なので改行は LF に揃える（この文字列はファイルへ書かない）。
    /// </summary>
    /// <param name="rows">表示行。</param>
    public static string ToDisplayText(IReadOnlyList<MergeDisplayRow> rows)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0) builder.Append(MergeTextLines.LF);
            builder.Append(rows[i].Text);
        }
        return builder.ToString();
    }

    /// <summary>差分の行を表示行へ写す。</summary>
    /// <param name="diff">差分の行。</param>
    /// <param name="conflictIndex">属する競合ブロックの通し番号。</param>
    private static List<MergeDisplayRow> ToRows(
        IReadOnlyList<MergeDiffLine> diff, int conflictIndex)
    {
        var rows = new List<MergeDisplayRow>(diff.Count);
        foreach (var line in diff)
        {
            var kind = line.Kind switch
            {
                MergeDiffKind.Added   => MergeRowKind.Added,
                MergeDiffKind.Removed => MergeRowKind.Removed,
                _                     => MergeRowKind.Common,
            };
            rows.Add(new MergeDisplayRow(kind, line.Text, conflictIndex));
        }
        return rows;
    }

    /// <summary>詰め物の行を足す（0 以下なら何もしない）。</summary>
    /// <param name="rows">足す先。</param>
    /// <param name="count">足す行数。</param>
    /// <param name="conflictIndex">属する競合ブロックの通し番号。</param>
    private static void Pad(List<MergeDisplayRow> rows, int count, int conflictIndex)
    {
        for (var i = 0; i < count; i++)
        {
            rows.Add(new MergeDisplayRow(MergeRowKind.Padding, string.Empty, conflictIndex));
        }
    }
}
