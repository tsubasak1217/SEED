using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ModalHost.CloseAll.cs — 開いている面を全部閉じる（2026-10-02。lane3。docs/ui_navigation.md §3.1。backlog W3-2 (1)）
//
//  鳴動画面のように「何が開いていても、その上に全画面を出す」ときに使う（Wake or Pay は戻るを上限つきで繰り返して代えていた）。
//  - 順: ダイアログ → シート → 覆い（ポップアップを含む）、同じ種類の中は後から開いた面から（ModalCloseOrder。戻るを 1 回ずつ押したのと同じ）
//  - 結果: 手札の型で決める（2026-10-03。2 回目のレビュー #23。ModalCloseOrder.ResultForHandle）: ShowDialog の手札（DialogHandle）は
//    DialogResult.Dismissed、それ以外（シート・覆い・ポップアップ〈帯が Dialog でも〉・ShowPlane の自前の面〈種類が Dialog でも〉）は null。
//    閉じない設定の面（DismissOnScrimTap・CancelableByBack = false・進捗の札）も閉じる。画面の下へ回した面（Park）も閉じる
//  - 動き: animate = true なら各面の出る動きのあとに手札が閉じる（閉じる動きの途中の面はそのまま）。
//    false なら出る動きを見せずにこの呼び出しの中で閉じ、手札の Closed・Completed もこの中で、閉じる順に届く
//    （閉じる動きの途中の面もすぐ閉じ終える）
//  - 作りかけ（面のスクリプトがまだ動いていない）の面は、見せずに取りやめて手札をこの呼び出しの中で閉じる（動きの有無によらない）。
//    取りやめる面は帳面（ModalOpeningBook）から先に全部外すので、手札の知らせの中で CloseAll を呼び直しても同じ面を二度扱わず、
//    作りかけの数も二重に減らない（レビュー #31）
//  - 知らせの中で開いた面: この呼び出しが閉じる面は呼んだ時点で決まる。手札の Closed・Completed の中で新しく開いた面は閉じずに残る
//    （閉じた後に開き直す使い方のため。それも閉じるなら、知らせの中で CloseAll を呼び直す）
// ============================================================

public sealed partial class ModalHost
{
    /// <summary>
    /// 外から閉じた・開くのを取りやめた面の手札へ渡す結果（手札が DialogHandle なら Dismissed、それ以外は null。
    /// 2026-10-03。2 回目のレビュー #23。全部閉じる・作りかけの取りやめ・受け取りの上限・ModalHost の破棄・作れなかった面で共通）。
    /// </summary>
    /// <param name="handle">閉じる面の手札。</param>
    /// <returns>結果。</returns>
    private static object? CancelResult(ModalHandle handle) => ModalCloseOrder.ResultForHandle(handle is DialogHandle);

    /// <summary>
    /// 開いている覆い・シート・ダイアログ・ポップアップを全部閉じる（閉じる順と結果はファイルの頭の説明）。
    /// </summary>
    /// <param name="animate">出る動きを見せるか（false = すぐ閉じ、手札もこの中で閉じる）。</param>
    /// <returns>この呼び出しで閉じ始めた・閉じた面の数（作りかけの取りやめを含む。もう閉じる動きの途中だった面は animate = true なら数えない）。</returns>
    public int CloseAll(bool animate = true) => CloseAllOf(null, animate);

    /// <summary>種類の面を全部閉じる（<see cref="CloseAll(bool)"/> の種類を絞った版）。</summary>
    /// <param name="kind">面の種類。</param>
    /// <param name="animate">出る動きを見せるか。</param>
    /// <returns>この呼び出しで閉じ始めた・閉じた面の数。</returns>
    public int CloseAll(ModalKind kind, bool animate = true) => CloseAllOf(kind, animate);

    /// <summary>全部閉じるの本体（種類を絞るなら only）。</summary>
    private int CloseAllOf(ModalKind? only, bool animate)
    {
        // 作りかけ: 見せずに取りやめて手札をすぐ閉じる（ダイアログ → シート → 覆いの順）
        int closed = CancelPending(only);
        // 開いている面: 閉じる順に並べてから閉じる（閉じると並びから外れるので、先に順を決める）
        var order = ModalCloseOrder.Sequence<ModalPlane>(kind => _planes[kind], only);
        foreach (var plane in order)
        {
            if (plane.Phase == ModalPhase.Closed) continue;
            // 閉じる動きの途中の面: 動きありならそのまま（数えない）、動きなしなら今すぐ閉じ終える
            if (plane.Phase == ModalPhase.Exiting && animate) continue;
            // 外から閉じる入口を通す（ダイアログは手札の Close と同じくボタンと同じ決め方で片付ける。2026-10-03）。
            // 結果は面の種類（帯）ではなく手札の型で決める（帯が Dialog のポップアップに箱入りの DialogResult を入れない。レビュー #23）
            plane.CloseFromOutside(plane.Handle is { } handle ? CancelResult(handle) : null, animate);
            closed++;
        }
        if (closed > 0) Debug.Log($"{LogPrefix} close all {(only?.ToString() ?? "all")} count={closed}{(animate ? string.Empty : " (no motion)")}");
        Redraw.Request();
        return closed;
    }

    /// <summary>
    /// 作りかけの面（作ったが面のスクリプトがまだ受け取っていない）を取りやめる: 根を消し、手札を全部閉じるの結果で閉じる（閉じる順）。
    /// 面のスクリプトが後で始まっても黙って消える（TakeCancelled）。帳面から先に全部外してから手札を閉じるので、手札の知らせの中の
    /// 呼び直しは取りやめ済みの面を扱わない（レビュー #31。以前は同じ面の作りかけの数を二度減らし、新しい作りかけの分まで減らした）。
    /// </summary>
    /// <returns>取りやめた数。</returns>
    private int CancelPending(ModalKind? only)
    {
        var cancelled = _openings.CancelAll(only);
        foreach (var opening in cancelled)
        {
            var claimed = opening.Claim;
            claimed.Root.Destroy();
            ForgetParkOf(claimed.Handle);
            claimed.Handle.Complete(CancelResult(claimed.Handle));
        }
        return cancelled.Count;
    }
}
