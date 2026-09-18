// ============================================================
//  MergeLineDiff.cs — 「元」と片側との行単位の差分
//
//  【役割】
//  競合ブロックの中で「その側が元から何を足したか・何を消したか」を色で見せるため、
//  元（base）と片側（現在 / 取り込み元）の行を突き合わせる。
//
//  【なぜ LCS なのか】
//  行単位で十分（単語単位は docs/backlog.md 送り）であり、
//  最長共通部分列は「共通・追加・削除」の 3 色へそのまま落ちる。
//  Myers のような凝った実装を足す理由が無い。
//
//  【大きなブロックへの退避】
//  LCS は O(n×m) の表を作る。競合ブロックが巨大（機械生成の .scene 全体が
//  1 ブロックになる等）だと、表だけで数百 MB になり得る。
//  上限（<see cref="MAX_TABLE_CELLS"/>）を超えたら差分を諦め、
//  「元は全部消えて、その側は全部足された」として色を付ける。
//  ★正しさは落ちない（合成は差分を使わない。差分は色を決めるだけ）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.VersionControl.Merge;

/// <summary>
/// 差分の 1 行の種類。
/// </summary>
public enum MergeDiffKind
{
    /// <summary>元にも側にもある行（色を付けない）。</summary>
    Common,

    /// <summary>その側が足した行（緑）。</summary>
    Added,

    /// <summary>元にあって、その側では消えた行（赤）。</summary>
    Removed,
}

/// <summary>
/// 差分の 1 行（不変）。
/// </summary>
/// <param name="Kind">行の種類。</param>
/// <param name="Text">行の中身（改行は含まない）。</param>
public readonly record struct MergeDiffLine(MergeDiffKind Kind, string Text);

/// <summary>
/// 行単位の差分（最長共通部分列）。
/// </summary>
public static class MergeLineDiff
{
    /// <summary>
    /// LCS の表に許すマス数の上限。
    /// これを超えるブロックは差分を取らず「全行を変更扱い」へ退避する。
    /// （1,000,000 マス ≒ int の表で 4 MB。1000 行 × 1000 行に相当する。）
    /// </summary>
    public const int MAX_TABLE_CELLS = 1_000_000;

    /// <summary>
    /// 元と片側の行を突き合わせ、「共通 / 追加 / 削除」の列を作る。
    /// </summary>
    /// <param name="baseTexts">「元」の行（節が無ければ空）。</param>
    /// <param name="sideTexts">片側（現在 または 取り込み元）の行。</param>
    /// <returns>表示順に並んだ差分の行。</returns>
    public static IReadOnlyList<MergeDiffLine> Diff(
        IReadOnlyList<string> baseTexts, IReadOnlyList<string> sideTexts)
    {
        // 片方が空なら突き合わせるまでもない（よくある「追加だけ」の形）。
        if (baseTexts.Count == 0) return AllOf(sideTexts, MergeDiffKind.Added);
        if (sideTexts.Count == 0) return AllOf(baseTexts, MergeDiffKind.Removed);

        // 表が大きすぎるときは差分を諦めて全行を変更扱いにする。
        if ((long)baseTexts.Count * sideTexts.Count > MAX_TABLE_CELLS)
        {
            var fallback = new List<MergeDiffLine>(baseTexts.Count + sideTexts.Count);
            fallback.AddRange(AllOf(baseTexts, MergeDiffKind.Removed));
            fallback.AddRange(AllOf(sideTexts, MergeDiffKind.Added));
            return fallback;
        }

        var table = BuildLcsTable(baseTexts, sideTexts);
        return Walk(baseTexts, sideTexts, table);
    }

    /// <summary>
    /// 最長共通部分列の長さの表を作る。
    /// <c>table[i, j]</c> は「元の i 行目以降」と「側の j 行目以降」の共通部分列の長さ。
    /// </summary>
    /// <param name="baseTexts">「元」の行。</param>
    /// <param name="sideTexts">片側の行。</param>
    private static int[,] BuildLcsTable(
        IReadOnlyList<string> baseTexts, IReadOnlyList<string> sideTexts)
    {
        var table = new int[baseTexts.Count + 1, sideTexts.Count + 1];

        for (var i = baseTexts.Count - 1; i >= 0; i--)
        {
            for (var j = sideTexts.Count - 1; j >= 0; j--)
            {
                table[i, j] = string.Equals(baseTexts[i], sideTexts[j], StringComparison.Ordinal)
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        return table;
    }

    /// <summary>
    /// 表をたどって差分の行を並べる。
    /// 同じ長さになる分岐では「削除を先、追加を後」に固定する
    /// （毎回同じ並びにならないと、チェックの付け外しで表示が踊る）。
    /// </summary>
    /// <param name="baseTexts">「元」の行。</param>
    /// <param name="sideTexts">片側の行。</param>
    /// <param name="table">LCS の表。</param>
    private static IReadOnlyList<MergeDiffLine> Walk(
        IReadOnlyList<string> baseTexts, IReadOnlyList<string> sideTexts, int[,] table)
    {
        var result = new List<MergeDiffLine>(baseTexts.Count + sideTexts.Count);

        int i = 0, j = 0;
        while (i < baseTexts.Count && j < sideTexts.Count)
        {
            if (string.Equals(baseTexts[i], sideTexts[j], StringComparison.Ordinal))
            {
                result.Add(new MergeDiffLine(MergeDiffKind.Common, baseTexts[i]));
                i++;
                j++;
            }
            else if (table[i + 1, j] >= table[i, j + 1])
            {
                result.Add(new MergeDiffLine(MergeDiffKind.Removed, baseTexts[i]));
                i++;
            }
            else
            {
                result.Add(new MergeDiffLine(MergeDiffKind.Added, sideTexts[j]));
                j++;
            }
        }

        while (i < baseTexts.Count) result.Add(new MergeDiffLine(MergeDiffKind.Removed, baseTexts[i++]));
        while (j < sideTexts.Count) result.Add(new MergeDiffLine(MergeDiffKind.Added,   sideTexts[j++]));

        return result;
    }

    /// <summary>全行を同じ種類として並べる。</summary>
    /// <param name="texts">行の中身。</param>
    /// <param name="kind">付ける種類。</param>
    private static IReadOnlyList<MergeDiffLine> AllOf(
        IReadOnlyList<string> texts, MergeDiffKind kind)
    {
        var result = new MergeDiffLine[texts.Count];
        for (var i = 0; i < texts.Count; i++) result[i] = new MergeDiffLine(kind, texts[i]);
        return result;
    }
}
