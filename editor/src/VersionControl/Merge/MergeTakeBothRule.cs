// ============================================================
//  MergeTakeBothRule.cs — 「両方を取り込む」が成り立つかの判定
//
//  【なぜ判定が要るのか】
//  「両方を取り込む」は、両者が **同じ場所へ別のものを足しただけ** のときにしか
//  意味を持たない（例: シーンの配列末尾に A さんと B さんが 1 体ずつアクタを足した）。
//  同じ箇所を両方が **書き換えた** 場合に両方を並べると、
//  「同じアクタが 2 体いる」「同じキーが 2 回出てくる」壊れたファイルになる。
//  ボタンを押せてしまう前に、押せない理由まで含めて出す。
//
//  【判定そのもの（2026-09-19 に実測で作り直し）】
//  条件は「**どちらの側も元（base）の行を 1 行も消していない**（挿入だけ）」。
//  元の節が空なのは、その特別な場合にすぎない。
//
//  当初は「元の節が空」を条件にしていたが、**実際のシーンではほぼ成立しない**。
//  アクターが複数行あるファイルで 2 人が末尾へアクターを足すと、diff3 は直前の
//  アクターの閉じ行を巻き込むので、元の節に `"components": []` と `}` が残る。
//  利用者の主用途がこの形なので、「元が空」では機能が常に使えないことになる
//  （判定の中身は MergeInsertionMap のコメントに実例つきで書いてある）。
//
//  片側でも元の行が消えている／書き換わっているなら不可。並べると
//  「同じアクタが 2 体いる」「同じキーが 2 回出てくる」壊れたファイルになる。
//
//  【印が無いファイル】
//  バイナリ（.png など）は Lore が印を書けないので中身を見せられない。
//  この場合は理由を変えて返す（利用者がやることが違うため）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

namespace SEEDEditor.VersionControl.Merge;

/// <summary>
/// 「両方を取り込む」ができるかどうかの判定結果（不変）。
/// </summary>
/// <param name="Allowed">できるか。</param>
/// <param name="Reason">できない理由（できるときは空文字）。</param>
public readonly record struct MergeTakeBothVerdict(bool Allowed, string Reason)
{
    /// <summary>できる、を表す結果。</summary>
    public static MergeTakeBothVerdict Ok => new(true, string.Empty);

    /// <summary>できない、を理由つきで作る。</summary>
    /// <param name="reason">できない理由。</param>
    public static MergeTakeBothVerdict No(string reason) => new(false, reason);
}

/// <summary>
/// 「両方を取り込む」の可否判定。
/// </summary>
public static class MergeTakeBothRule
{
    /// <summary>
    /// 解析済みの文書について判定する。
    /// </summary>
    /// <param name="document">解析済みの印つきテキスト。</param>
    public static MergeTakeBothVerdict Evaluate(ConflictMarkerDocument document)
    {
        if (document is null || document.ConflictCount == 0)
            return MergeTakeBothVerdict.No(VersionControlMessages.MERGE_TAKE_BOTH_NO_MARKERS);

        foreach (var conflict in document.Conflicts)
        {
            // 片側でも元の行を消している＝同じ箇所を両方が書き換えた。並べると壊れる。
            if (!IsUnionable(conflict))
                return MergeTakeBothVerdict.No(VersionControlMessages.MERGE_TAKE_BOTH_NOT_ADD_ONLY);
        }

        return MergeTakeBothVerdict.Ok;
    }

    /// <summary>
    /// 競合ブロック 1 つが「両方を並べても壊れない」形か
    /// （＝ 両側とも元に対して挿入しかしていないか）。
    ///
    /// <para>
    /// マージエディタの既定チェック（この形のブロックは最初から両方を採る）と、
    /// 合成で union を使うかどうかの判断にも同じ関数を使う。
    /// **判定と合成が別の条件で動くと、押せたのに壊れた結果が出る**ので必ず共有する。
    /// </para>
    /// </summary>
    /// <param name="segment">競合ブロック。</param>
    public static bool IsUnionable(MergeSegment segment)
        => MergeInsertionMap.TryBuildBoth(segment, out _, out _);

    /// <summary>
    /// 生のテキストについて判定する（印が無い・壊れている場合もここで畳む）。
    /// </summary>
    /// <param name="text">ファイルの中身（読めなかったときは null）。</param>
    public static MergeTakeBothVerdict Evaluate(string? text)
    {
        if (text is null)
            return MergeTakeBothVerdict.No(VersionControlMessages.MERGE_TAKE_BOTH_NO_MARKERS);

        if (!ConflictMarkerDocument.TryParse(text, out var document, out var error))
            return MergeTakeBothVerdict.No(error);

        return Evaluate(document!);
    }
}
