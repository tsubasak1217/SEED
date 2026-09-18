// ============================================================
//  MergeInsertionMap.cs — 片側が「元へ何を、どこへ挿し込んだか」
//
//  【なぜ必要なのか（実機で分かったこと）】
//  当初は「元（base）の節が空 ＝ 両者が足しただけ」と判定していたが、
//  **実際のシーンではほぼ成立しない**。アクターが複数行あるファイルで
//  2 人が配列末尾へアクターを足すと、diff3 は直前のアクターの閉じ行を巻き込むため、
//  元の節に `"components": []` と `}` が残る:
//
//      <<<<<<< ours
//            "components": []        ← 元にもある
//          },                        ← ours が挿し込んだ
//          { …AddedByMe… }
//      ||||||| original
//            "components": []
//          }                         ← 元にもある
//      =======
//            "components": []
//          },
//          { …AddedByOwner… }
//      >>>>>>> theirs
//
//  元は残っているが、**どちらの側も元の行を 1 行も消していない**（挿入だけ）。
//  これが利用者の主用途（2 人がアクターを追加）の実際の形なので、
//  判定を「元が空か」ではなく「**挿入だけか**」に変えた。
//  元が空なのは、その特別な場合（挿入位置が 1 か所しか無い）にすぎない。
//
//  【何を持つのか】
//  元の各行の **手前** に何行挿し込まれたか、を位置ごとに分けて持つ。
//  位置は 0 〜 元の行数（最後の位置は「元の末尾より後ろ」＝ 末尾への追加）。
//  両側のこの表があれば、元を骨格にして両方の挿入を差し込む合成（union）ができる。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.VersionControl.Merge;

/// <summary>
/// 片側が元（base）に対して行った挿入を、挿入位置ごとにまとめたもの（不変）。
/// </summary>
public sealed class MergeInsertionMap
{
    /// <summary>
    /// 挿入位置ごとの行。
    /// <c>Groups[i]</c> は「元の i 行目の **手前** に挿し込まれた行」で、
    /// 要素数は必ず 元の行数 + 1（最後は元の末尾より後ろ＝末尾への追加）。
    /// </summary>
    public IReadOnlyList<IReadOnlyList<MergeLine>> Groups { get; }

    /// <summary>挿入された行の総数（1 行も無ければ 0）。</summary>
    public int InsertedLineCount { get; }

    /// <summary>全項目を指定して生成する（生成は <see cref="TryBuild"/> だけが行う）。</summary>
    /// <param name="groups">挿入位置ごとの行。</param>
    /// <param name="insertedLineCount">挿入された行の総数。</param>
    private MergeInsertionMap(
        IReadOnlyList<IReadOnlyList<MergeLine>> groups, int insertedLineCount)
    {
        Groups            = groups;
        InsertedLineCount = insertedLineCount;
    }

    /// <summary>
    /// 片側が元に対して **挿入だけ** を行っているかを調べ、そうならその位置表を作る。
    /// </summary>
    /// <param name="baseLines">元（base）の行。空でもよい。</param>
    /// <param name="sideLines">片側（現在 または 取り込み元）の行。</param>
    /// <param name="map">位置表（挿入だけでなければ null）。</param>
    /// <returns>挿入だけだったか。1 行でも消えていれば偽。</returns>
    public static bool TryBuild(
        IReadOnlyList<MergeLine> baseLines,
        IReadOnlyList<MergeLine> sideLines,
        out MergeInsertionMap? map)
    {
        map = null;

        var groups = new List<MergeLine>[baseLines.Count + 1];
        for (var i = 0; i < groups.Length; i++) groups[i] = new List<MergeLine>();

        var diff = MergeLineDiff.Diff(
            MergeTextLines.ToTexts(baseLines), MergeTextLines.ToTexts(sideLines));

        var baseIndex = 0;
        var sideIndex = 0;
        var inserted  = 0;

        foreach (var line in diff)
        {
            switch (line.Kind)
            {
                case MergeDiffKind.Removed:
                    // 元の行が消えている＝挿入だけではない（並べると内容が二重になる）。
                    return false;

                case MergeDiffKind.Added:
                    // 差分は側の行を順にたどるので、位置から実物の行（改行つき）を引ける。
                    // 数が合わないのは差分側の異常。黙って崩さず「挿入だけではない」へ倒す。
                    if (sideIndex >= sideLines.Count) return false;
                    groups[baseIndex].Add(sideLines[sideIndex]);
                    sideIndex++;
                    inserted++;
                    break;

                case MergeDiffKind.Common:
                    if (baseIndex >= baseLines.Count || sideIndex >= sideLines.Count) return false;
                    baseIndex++;
                    sideIndex++;
                    break;
            }
        }

        map = new MergeInsertionMap(groups, inserted);
        return true;
    }

    /// <summary>
    /// 競合ブロックの両側について位置表を作る（両方とも挿入だけのときだけ成功）。
    /// </summary>
    /// <param name="segment">競合ブロック。</param>
    /// <param name="incoming">「取り込み元」側の位置表（失敗時は null）。</param>
    /// <param name="current">「現在」側の位置表（失敗時は null）。</param>
    /// <returns>両側とも挿入だけだったか。</returns>
    public static bool TryBuildBoth(
        MergeSegment segment,
        out MergeInsertionMap? incoming,
        out MergeInsertionMap? current)
    {
        incoming = null;
        current  = null;

        if (segment is null || segment.Kind != MergeSegmentKind.Conflict) return false;

        return TryBuild(segment.BaseLines, segment.IncomingLines, out incoming)
               && TryBuild(segment.BaseLines, segment.CurrentLines, out current);
    }
}
