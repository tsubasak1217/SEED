using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  SuppliedContentRules.cs — 渡された中身（Push(GameObject)・Replace(GameObject)）の受け付けと手放し方の決め方
//                            （2026-10-03。lane3。2 回目のレビュー #21・#22。純粋な計算。docs/ui_navigation.md §2.8）
//
//  ScreenStack.Content.cs が、エンジンから読んだ様子（その中身を持つ段・中身が枠の下にあるか・元の親がまだあるか）を渡して決める。
//  【受け付け（CheckSupplied）】同じ中身をほかの段が持っていたら断る（二度押しの Push で 2 段目へ移ると、1 段目は空の枠になり、
//  既定の Destroy では 2 段目を下ろすと中身も消えた。レビュー #22）。例外は、持っている段がもう外れていて（下ろした・置き換えた）
//  外れる処理で中身を元の親へ戻す（ReturnToParent）ときだけ: 出入りは順に動くので、外れる処理（Finish）で戻してから新しい段が
//  枠へ移す（下ろす動きの途中にもう一度積む形）。外れた段が Destroy なら、外れる処理で中身が消えるので断る。
//  【手放し方（ReleaseAction）】段が外れるとき（OnScreenExit の後）:
//    - 中身がもうこの枠の下に無い（アプリが自分で付け替えた）→ 触らない（戻さない・消さない）
//    - Destroy → 枠と一緒に消える
//    - ReturnToParent → 元の親（枠へ移したフレームに読んだ親。レビュー #21）へ戻す。元の親が無い（シーンの根にあった）・消えたなら、
//      シーンの根へ移して隠す（見えたまま根に出さない。消さない）
// ============================================================

/// <summary>渡された中身をすでに持っている段 1 つ（重ねて積めるかの判定に使う）。</summary>
/// <param name="Removed">その段がスタックから外れたか（下ろした・置き換えた。中身は外れる処理で離れる）。</param>
/// <param name="Release">その段の、外れたときの中身の扱い。</param>
public readonly record struct SuppliedHolder(bool Removed, ScreenContentRelease Release);

/// <summary>渡された中身を積めるか。</summary>
public enum SuppliedAvailability
{
    /// <summary>積める（どの段も持っていない・持っている段は外れていて、外れる処理で元の親へ戻す）。</summary>
    Free = 0,
    /// <summary>ほかの段で使用中（スタックに残っている段が持っている。二度押しの Push など）。</summary>
    InUse = 1,
    /// <summary>外れた段が持っていて、外れる処理で枠と一緒に消える（Destroy）。積んでも空の枠になる。</summary>
    Doomed = 2,
}

/// <summary>段が外れるときに、渡された中身にすること。</summary>
public enum SuppliedReleaseAction
{
    /// <summary>枠と一緒に消す（Destroy。何もしなければ枠を消すときに一緒に消える）。</summary>
    DestroyWithFrame = 0,
    /// <summary>押下を取り消して元の親へ戻す（ReturnToParent）。</summary>
    ReturnToParent = 1,
    /// <summary>元の親が無い・消えた: 押下を取り消し、隠してからシーンの根へ移す（消さない。見えたまま根に出さない）。</summary>
    ReturnToRootHidden = 2,
    /// <summary>中身がもうこの枠の下に無い（アプリが付け替えた）: 触らない。</summary>
    LeaveInPlace = 3,
}

/// <summary>渡された中身の受け付けと手放し方の決め方。</summary>
public static class SuppliedContentRules
{
    /// <summary>
    /// 渡された中身を、いまそれを持っている段（<paramref name="holders"/>）があっても積めるか。
    /// スタックに残っている段が持っていれば <see cref="SuppliedAvailability.InUse"/>、外れた段が Destroy で持っていれば
    /// <see cref="SuppliedAvailability.Doomed"/>、どれでもなければ <see cref="SuppliedAvailability.Free"/>。
    /// </summary>
    /// <param name="holders">その中身を持っている段（まだ枠へ移していない段も含む）。</param>
    /// <returns>積めるか。</returns>
    public static SuppliedAvailability CheckSupplied(IEnumerable<SuppliedHolder> holders)
    {
        var availability = SuppliedAvailability.Free;
        foreach (var holder in holders)
        {
            // スタックに残っている段が持っている: その段の枠から中身を奪うことになるので断る（使用中がいちばん強い）
            if (!holder.Removed) return SuppliedAvailability.InUse;
            // 外れた段が Destroy で持っている: 外れる処理（新しい段が枠へ移すより先）で中身が消える
            if (holder.Release == ScreenContentRelease.Destroy) availability = SuppliedAvailability.Doomed;
        }
        return availability;
    }

    /// <summary>
    /// 段が外れるときに、渡された中身にすることを決める。
    /// </summary>
    /// <param name="release">その段の、外れたときの中身の扱い。</param>
    /// <param name="underFrame">中身がまだその段の枠の下にあるか。</param>
    /// <param name="parentAlive">元の親（枠へ移したフレームに読んだ親）が今もあるか（無かった・消えたなら false）。</param>
    /// <returns>すること。</returns>
    public static SuppliedReleaseAction ReleaseAction(ScreenContentRelease release, bool underFrame, bool parentAlive)
    {
        // もう枠の下に無い（アプリが自分で付け替えた）: 引き戻さない・枠を消しても巻き込まれないので消さない
        if (!underFrame) return SuppliedReleaseAction.LeaveInPlace;
        if (release == ScreenContentRelease.Destroy) return SuppliedReleaseAction.DestroyWithFrame;
        // 戻す先が無い（シーンの根にあった・親が消えた）: 根へ移すが隠す（以前は根の直下に見えたまま出た。レビュー #21）
        return parentAlive ? SuppliedReleaseAction.ReturnToParent : SuppliedReleaseAction.ReturnToRootHidden;
    }
}
