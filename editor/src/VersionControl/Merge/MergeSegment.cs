// ============================================================
//  MergeSegment.cs — 競合ファイルを切り分けた 1 区画
//
//  【役割】
//  印つきのファイルは「どちらも同じ共通部分」と「食い違っている競合ブロック」の
//  繰り返しでできている。その 1 区画をこの型で表す。
//
//  【語彙（向きの定義。取り違えると利用者の変更が消える）】
//  ・Current（現在）   … 印の <<<<<<< 側。sync では自分の作業コピー、
//                        ブランチのマージでは取り込み先＝現在のブランチ。
//  ・Base（元）        … 印の ||||||| 節。分岐する前の共通の祖先。
//                        節が無い（＝両者が同じ場所に追加した）ことも普通にある。
//  ・Incoming（取り込み元）… 印の >>>>>>> 側。sync ではリモート、
//                        ブランチのマージでは取り込み元のブランチ。
//  sync でもブランチのマージでもこの並びは同じ（実機で確認済み）。
//  Lore CLI の resolve mine / theirs の向きとは別物なので混ぜないこと。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.VersionControl.Merge;

/// <summary>
/// 区画の種類。
/// </summary>
public enum MergeSegmentKind
{
    /// <summary>印の外側。どちらの側でも同じ内容で、そのまま結果へ入る。</summary>
    Common,

    /// <summary>印で囲まれた競合ブロック。利用者がどちらを採るか選ぶ。</summary>
    Conflict,
}

/// <summary>
/// 競合ファイルを切り分けた 1 区画（不変）。
/// </summary>
public sealed class MergeSegment
{
    /// <summary>区画の種類。</summary>
    public MergeSegmentKind Kind { get; }

    /// <summary>
    /// 競合ブロックの通し番号（0 始まり）。共通部分は -1。
    /// 利用者の選択はこの番号で引く。
    /// </summary>
    public int ConflictIndex { get; }

    /// <summary>共通部分の行（競合ブロックでは空）。</summary>
    public IReadOnlyList<MergeLine> CommonLines { get; }

    /// <summary>「現在」側の行（印の <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> 節）。</summary>
    public IReadOnlyList<MergeLine> CurrentLines { get; }

    /// <summary>「元」の行（印の <c>|||||||</c> 節。節が無ければ空）。</summary>
    public IReadOnlyList<MergeLine> BaseLines { get; }

    /// <summary>「取り込み元」側の行（印の <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> 節）。</summary>
    public IReadOnlyList<MergeLine> IncomingLines { get; }

    /// <summary>
    /// 元（base）の節が空のブロックか。
    ///
    /// <para>
    /// ★これは「両方を取り込めるか」の判定 **ではない**。
    /// 実際のシーンでは、末尾へアクターを足し合っただけでも直前の閉じ行が
    /// 元の節に残るため、元が空になることはむしろ少ない（`MergeInsertionMap` 参照）。
    /// 可否の判定は必ず <c>MergeTakeBothRule.IsUnionable</c> を使うこと。
    /// </para>
    /// </summary>
    public bool HasEmptyBase => Kind == MergeSegmentKind.Conflict && BaseLines.Count == 0;

    /// <summary>共通部分の区画を作る。</summary>
    /// <param name="lines">そのまま結果へ入る行。</param>
    public static MergeSegment Common(IReadOnlyList<MergeLine> lines)
        => new(MergeSegmentKind.Common, -1, lines,
               Array.Empty<MergeLine>(), Array.Empty<MergeLine>(), Array.Empty<MergeLine>());

    /// <summary>競合ブロックの区画を作る。</summary>
    /// <param name="conflictIndex">競合ブロックの通し番号（0 始まり）。</param>
    /// <param name="current">「現在」側の行。</param>
    /// <param name="baseLines">「元」の行（無ければ空）。</param>
    /// <param name="incoming">「取り込み元」側の行。</param>
    public static MergeSegment Conflict(
        int conflictIndex,
        IReadOnlyList<MergeLine> current,
        IReadOnlyList<MergeLine> baseLines,
        IReadOnlyList<MergeLine> incoming)
        => new(MergeSegmentKind.Conflict, conflictIndex,
               Array.Empty<MergeLine>(), current, baseLines, incoming);

    /// <summary>全項目を指定して生成する（生成は上の 2 つの工場だけが行う）。</summary>
    /// <param name="kind">区画の種類。</param>
    /// <param name="conflictIndex">競合ブロックの通し番号。</param>
    /// <param name="commonLines">共通部分の行。</param>
    /// <param name="currentLines">「現在」側の行。</param>
    /// <param name="baseLines">「元」の行。</param>
    /// <param name="incomingLines">「取り込み元」側の行。</param>
    private MergeSegment(
        MergeSegmentKind kind,
        int conflictIndex,
        IReadOnlyList<MergeLine> commonLines,
        IReadOnlyList<MergeLine> currentLines,
        IReadOnlyList<MergeLine> baseLines,
        IReadOnlyList<MergeLine> incomingLines)
    {
        Kind          = kind;
        ConflictIndex = conflictIndex;
        CommonLines   = commonLines;
        CurrentLines  = currentLines;
        BaseLines     = baseLines;
        IncomingLines = incomingLines;
    }

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString()
        => Kind == MergeSegmentKind.Common
            ? $"共通({CommonLines.Count} 行)"
            : $"競合#{ConflictIndex}(現在 {CurrentLines.Count} / 元 {BaseLines.Count} / 取り込み元 {IncomingLines.Count} 行)";
}
