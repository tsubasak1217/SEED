// ============================================================
//  MergeComposer.cs — ブロックごとの選択から結果テキストを組み立てる
//
//  【役割】
//  「このブロックは取り込み元を採る」「こっちは両方」といった選択を受け取り、
//  印を 1 つも含まないテキストを作る。ファイルへ書き戻すのはこの結果。
//
//  【両方採ったときの並び順（取り違えると履歴が読めなくなる）】
//  ★「既に共有されていた側を先」に置く。
//    ・sync（最新を取得）… 取り込み元＝サーバに既にある内容 → 取り込み元 → 現在
//    ・ブランチのマージ  … 現在＝取り込み先のブランチに既にある内容 → 現在 → 取り込み元
//  こうしておくと、後から履歴を見たときに「元々あった並びの後ろへ足された」
//  という形になり、次の差分が最小になる。
//
//  【両方採ったときの組み立て方（2026-09-19 に実測で作り直し）】
//  ★単純な連結ではなく **元（base）を骨格にした union**。
//  実際のシーンでは、末尾へアクターを足し合っただけでも元の節に直前の閉じ行が残る
//  （MergeInsertionMap のコメントに実例）。そこで単純連結すると元の行が 2 回出て
//  JSON が壊れる。元の行を 1 回だけ残し、その同じ位置へ両側の挿入を差し込む。
//  同じ位置に両側の挿入があるときだけ、上の並び順が効く。
//  挿入だけでないブロック（利用者がマージエディタで無理に両方をチェックした場合）は
//  union に落とせないので従来どおり連結する。壊れていれば書き戻す前の検査で止まる。
//
//  【どちらも選ばなかったブロック】
//  「元」（base）の内容を入れる。元が空（両者が同じ場所に足しただけ）なら
//  何も入れない＝どちらの追加も捨てる、が正しい意味になる。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Merge;

/// <summary>
/// 競合ブロック 1 つに対する利用者の選択（不変）。
/// </summary>
/// <param name="TakeIncoming">「取り込み元」側を結果へ入れるか。</param>
/// <param name="TakeCurrent">「現在」側を結果へ入れるか。</param>
public readonly record struct MergeBlockChoice(bool TakeIncoming, bool TakeCurrent)
{
    /// <summary>どちらも採らない（＝元に戻す）選択。</summary>
    public static MergeBlockChoice Neither => new(false, false);

    /// <summary>「取り込み元」だけを採る選択。</summary>
    public static MergeBlockChoice IncomingOnly => new(true, false);

    /// <summary>「現在」だけを採る選択。</summary>
    public static MergeBlockChoice CurrentOnly => new(false, true);

    /// <summary>両方を採る選択。</summary>
    public static MergeBlockChoice Both => new(true, true);

    /// <summary>どちらか一方でも採っているか（未解決ブロックの数え上げに使う）。</summary>
    public bool HasSelection => TakeIncoming || TakeCurrent;
}

/// <summary>
/// 選択から結果テキストを組み立てる。
/// </summary>
public static class MergeComposer
{
    /// <summary>
    /// 結果テキストを組み立てる。
    /// </summary>
    /// <param name="document">解析済みの印つきテキスト。</param>
    /// <param name="choices">
    /// 競合ブロックごとの選択（<see cref="MergeSegment.ConflictIndex"/> で引く）。
    /// 足りない分は「どちらも選ばない」として扱う。
    /// </param>
    /// <param name="origin">
    /// 進行中のマージの出どころ。両方採ったときの並び順がこれで決まる。
    /// </param>
    /// <returns>印を含まない結果テキスト（BOM は含まない）。</returns>
    public static string Compose(
        ConflictMarkerDocument document,
        IReadOnlyList<MergeBlockChoice> choices,
        MergeOrigin origin)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        var lines = new List<MergeLine>();

        foreach (var segment in document.Segments)
        {
            if (segment.Kind == MergeSegmentKind.Common)
            {
                lines.AddRange(segment.CommonLines);
                continue;
            }

            var choice = ChoiceAt(choices, segment.ConflictIndex);
            AppendConflict(lines, segment, choice, origin);
        }

        return MergeTextLines.Join(lines, document.NewLine);
    }

    /// <summary>
    /// すべてのブロックを同じ選択にする（ツールバーの「すべて〜」用）。
    /// </summary>
    /// <param name="conflictCount">競合ブロックの数。</param>
    /// <param name="choice">全ブロックへ当てる選択。</param>
    public static IReadOnlyList<MergeBlockChoice> Fill(int conflictCount, MergeBlockChoice choice)
    {
        var choices = new MergeBlockChoice[Math.Max(0, conflictCount)];
        for (var i = 0; i < choices.Length; i++) choices[i] = choice;
        return choices;
    }

    /// <summary>
    /// まだ選ばれていないブロックの数を数える（「残り」の表示に使う）。
    /// </summary>
    /// <param name="choices">ブロックごとの選択。</param>
    public static int CountUnselected(IReadOnlyList<MergeBlockChoice> choices)
    {
        var count = 0;
        foreach (var choice in choices)
        {
            if (!choice.HasSelection) count++;
        }
        return count;
    }

    /// <summary>
    /// 1 つの競合ブロックの中身を結果へ足す。
    /// </summary>
    /// <param name="lines">結果の行（追加先）。</param>
    /// <param name="segment">競合ブロック。</param>
    /// <param name="choice">その利用者の選択。</param>
    /// <param name="origin">両方採ったときの並び順を決める出どころ。</param>
    private static void AppendConflict(
        List<MergeLine> lines, MergeSegment segment,
        MergeBlockChoice choice, MergeOrigin origin)
    {
        // どちらも選ばなかった＝元へ戻す（元が空なら何も入らない）。
        if (!choice.HasSelection)
        {
            lines.AddRange(segment.BaseLines);
            return;
        }

        if (choice.TakeIncoming && !choice.TakeCurrent)
        {
            lines.AddRange(segment.IncomingLines);
            return;
        }

        if (choice.TakeCurrent && !choice.TakeIncoming)
        {
            lines.AddRange(segment.CurrentLines);
            return;
        }

        // ── 両方 ──
        // まず「元を骨格にした union」を試す。これが本命の経路で、
        // 実際のシーンの「2 人がアクターを足した」競合はここを通る。
        if (MergeInsertionMap.TryBuildBoth(segment, out var incoming, out var current))
        {
            AppendUnion(lines, segment.BaseLines, incoming!, current!, origin);
            return;
        }

        // 挿入だけではないブロック（＝「両方を取り込む」は本来できない形）。
        // マージエディタで利用者が無理に両方をチェックしたときだけここへ来る。
        // 元を残しようが無いので連結する。壊れていれば書き戻す前の検査で止まる。
        if (origin == MergeOrigin.BranchMerge)
        {
            lines.AddRange(segment.CurrentLines);
            lines.AddRange(segment.IncomingLines);
        }
        else
        {
            lines.AddRange(segment.IncomingLines);
            lines.AddRange(segment.CurrentLines);
        }
    }

    /// <summary>
    /// 元（base）を骨格に、両側の挿入を元の同じ位置へ差し込む。
    ///
    /// <para>
    /// 元の行は 1 回だけ出る。位置表は「元の i 行目の手前に挿し込まれた行」を
    /// 持っているので、i を 0 から順に「その位置の挿入 → 元の i 行目」と出していき、
    /// 最後に「元の末尾より後ろ」の挿入を出せば union になる。
    /// </para>
    /// </summary>
    /// <param name="lines">結果の行（追加先）。</param>
    /// <param name="baseLines">元の行。</param>
    /// <param name="incoming">「取り込み元」側の挿入位置表。</param>
    /// <param name="current">「現在」側の挿入位置表。</param>
    /// <param name="origin">同じ位置に両側の挿入があるときの並び順を決める出どころ。</param>
    private static void AppendUnion(
        List<MergeLine> lines,
        IReadOnlyList<MergeLine> baseLines,
        MergeInsertionMap incoming,
        MergeInsertionMap current,
        MergeOrigin origin)
    {
        for (var i = 0; i <= baseLines.Count; i++)
        {
            // 既に共有されていた側を先に置く（連結のときと同じ規則）。
            if (origin == MergeOrigin.BranchMerge)
            {
                lines.AddRange(current.Groups[i]);
                lines.AddRange(incoming.Groups[i]);
            }
            else
            {
                lines.AddRange(incoming.Groups[i]);
                lines.AddRange(current.Groups[i]);
            }

            // 最後の位置（元の末尾より後ろ）には対応する元の行が無い。
            if (i < baseLines.Count) lines.Add(baseLines[i]);
        }
    }

    /// <summary>
    /// 指定番号の選択を引く。範囲外は「どちらも選ばない」。
    /// </summary>
    /// <param name="choices">ブロックごとの選択。</param>
    /// <param name="index">競合ブロックの通し番号。</param>
    private static MergeBlockChoice ChoiceAt(IReadOnlyList<MergeBlockChoice> choices, int index)
        => choices is not null && index >= 0 && index < choices.Count
            ? choices[index]
            : MergeBlockChoice.Neither;
}
