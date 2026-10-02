namespace SEED.Binding;

// ============================================================
//  BindingLimits.cs — 結び付け（SEED.Binding）の上限の定数
//
//  観測値（Observable・Computed・Deferred）と一覧（ObservableList）は、購読の中で値を変える「再入」を
//  今の知らせを配り終えてから次の周として配る（深い呼び出しの入れ子にしない）。その周の数の上限をここで決める。
//  正典は docs/ui_binding.md §5「フレームと再入の規則」。
// ============================================================

/// <summary>結び付けの上限の定数（観測値・一覧の再入の深さ）。</summary>
public static class BindingLimits
{
    /// <summary>
    /// 購読の中で値を変えたとき（再入）に、もう 1 周知らせ直す段の上限（1 = 1 段だけ許す）。
    /// 最初の知らせを 1 周目として、その中での変更は配り終えた後に 2 周目として知らせる。
    /// 2 周目の中でさらに変えると、値は入るが知らせず警告する（A の購読が B を変え、B の購読が A を変える…の無限の往復の保険。
    /// SEED.Events の「同名イベントの再入は深さ上限で打ち切る」と同じ考え方）。
    /// </summary>
    public const int MaxReentrantDepth = 1;

    /// <summary>知らせている途中でないことを表す周の番号。</summary>
    internal const int IdleRound = 0;

    /// <summary>最初の知らせの周の番号。</summary>
    internal const int FirstRound = 1;

    /// <summary>知らせてよい最後の周の番号（最初の周 ＋ 再入の段）。</summary>
    internal const int LastRound = FirstRound + MaxReentrantDepth;
}
