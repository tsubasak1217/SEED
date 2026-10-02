using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ModalHost.Parking.cs — 面を閉じずに、画面のスタックの画面の下へ回す（2026-10-02。lane3。docs/ui_navigation.md §3.7。backlog W3-7 (2)）
//
//  オプションの覆い・プロフィールのポップアップから全画面（設定・編集）へ移るとき、覆いを閉じると戻ったときに開き直す動き・組み立てが見え、
//  開いたまま積むと全画面が覆いの帯の下に隠れる。Park(面の手札, スタック, 全画面の手札) は覆いを開いたまま、全画面の枠より奥・その下の画面より
//  手前へ回す（Flutter で覆いの上に全画面を積んだのと同じ見え方。全画面は覆いの上を右から入り、戻るでは右へ抜ける）:
//    - 底上げ: 面の根の底上げを ParkedPlaneLayers.PlaneBias へ書き換える（元の値は覚えて、戻すときに書き戻す）
//    - 戻る: 回した面は戻るの層（ダイアログ・シート・覆い）で数えず、予測型の戻るの相手にもならない（戻るは画面のスタックへ届き、全画面が下りる）
//    - フォーカス: 面の範囲を「重ねる範囲」から外して後ろへ回す（全画面の入力欄がフォーカスを取れる）
//    - 戻す: 全画面の手札が閉じた（下ろした・置き換えた・根まで戻した）ら自動で戻す（Unpark）。底上げ・重ねる範囲・前へ出すを戻す
//      （全画面が抜け終わった時点で戻すので、見た目は変わらない）。全画面を置き換えて別の全画面の下に残すなら、置き換えた手札で Park し直す
//  面ができる前（作りかけ）に Park したら、面のスクリプトが始まったときに当てる（ApplyPendingPark）。面が閉じたら記録を捨てる。
// ============================================================

public sealed partial class ModalHost
{
    /// <summary>画面の下へ回した面 1 つの記録。</summary>
    private sealed class ParkRecord
    {
        /// <summary>面の手札。</summary>
        public required ModalHandle Handle { get; init; }
        /// <summary>全画面を積んだスタック。</summary>
        public required ScreenStack Stack { get; init; }
        /// <summary>面を下に回した全画面の手札（この画面が閉じたら戻す）。</summary>
        public required ScreenHandle Page { get; set; }
        /// <summary>全画面が閉じたときに戻す（Page.Closed へつないだもの）。</summary>
        public required Action<ScreenHandle> OnPageClosed { get; init; }
        /// <summary>面の根の元の底上げ（当てる前は null）。</summary>
        public int? OriginalBias;
    }

    /// <summary>面の手札 → 画面の下へ回した記録。</summary>
    private readonly Dictionary<ModalHandle, ParkRecord> _parks = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// 面を閉じずに、スタック <paramref name="stack"/> の画面 <paramref name="page"/> の下（その下の画面より手前）へ回す。
    /// 全画面を積んだ直後に呼ぶ（積んだ時点で並びは変わっているので、全画面の段が分かる）。page が閉じたら自動で戻す。
    /// 同じ面を別の画面で Park し直すと、回す先を替える（置き換えた全画面の下に残す）。
    /// </summary>
    /// <param name="handle">面の手札（開いている・作りかけ）。</param>
    /// <param name="stack">全画面を積んだスタック。</param>
    /// <param name="page">全画面の手札（スタックに積まれているもの）。</param>
    /// <returns>回したら true（面が閉じている・画面がスタックに無いなら false）。</returns>
    public bool Park(ModalHandle handle, ScreenStack stack, ScreenHandle page)
    {
        if (handle is null || stack is null || page is null || handle.IsClosed || page.IsClosed) return false;
        if (stack.IndexOf(page) < 0)
        {
            Debug.LogWarning($"{LogPrefix} park: 画面 {page} はスタック {stack.Owner.Name} に積まれていません");
            return false;
        }
        if (_parks.TryGetValue(handle, out var existing))
        {
            // 回す先を替える（前の画面が閉じても戻さない）
            existing.Page.Closed -= existing.OnPageClosed;
            existing.Page = page;
            page.Closed += existing.OnPageClosed;
            if (handle.Plane is { } moved) ApplyPark(existing, moved);
            return true;
        }
        var record = new ParkRecord { Handle = handle, Stack = stack, Page = page, OnPageClosed = _ => Unpark(handle) };
        page.Closed += record.OnPageClosed;
        _parks[handle] = record;
        if (handle.Plane is { } plane) ApplyPark(record, plane);
        Debug.Log($"{LogPrefix} park {handle.Kind} under {page}");
        return true;
    }

    /// <summary>
    /// 画面の下へ回した面を元へ戻す（底上げ・重ねる範囲・前へ出す。全画面が閉じたときに自動で呼ばれる）。
    /// </summary>
    /// <param name="handle">面の手札。</param>
    /// <returns>戻したら true（回していなければ false）。</returns>
    public bool Unpark(ModalHandle handle)
    {
        if (handle is null || !_parks.Remove(handle, out var record)) return false;
        record.Page.Closed -= record.OnPageClosed;
        if (handle.Plane is { Phase: not ModalPhase.Closed } plane) RestorePark(record, plane);
        Debug.Log($"{LogPrefix} unpark {handle.Kind}");
        return true;
    }

    /// <summary>面を画面の下へ回しているか。</summary>
    /// <param name="handle">面の手札。</param>
    public bool IsParked(ModalHandle handle) => handle is not null && _parks.ContainsKey(handle);

    /// <summary>面のスクリプトが始まった: 開く前に頼まれた「画面の下へ回す」を当てる（ModalPlane.OnWidgetStart から）。</summary>
    internal void ApplyPendingPark(ModalPlane plane)
    {
        if (plane.Handle is { } handle && _parks.TryGetValue(handle, out var record)) ApplyPark(record, plane);
    }

    /// <summary>面を画面の下へ回す（底上げを書き換え、重ねる範囲から外して後ろへ回す）。画面がもうスタックに無ければ戻す。</summary>
    private void ApplyPark(ParkRecord record, ModalPlane plane)
    {
        int index = record.Stack.IndexOf(record.Page);
        if (index < 0)
        {
            Unpark(record.Handle);
            return;
        }
        int step = record.Stack.EffectiveLayerStep;
        int pageFrame = ParkedPlaneLayers.PageFrameBias(NavNode.EffectiveBias(record.Stack.ScreensNode), index, step);
        int bias = ParkedPlaneLayers.PlaneBias(pageFrame, step, NavNode.AncestorsBias(plane.Owner));
        record.OriginalBias ??= NavNode.OwnBias(plane.Owner);
        NavNode.SetBias(plane.Owner, bias);
        plane.IsParked = true;
        // フォーカス: 画面の範囲と同じ扱いにして後ろへ（全画面が落ち着くとその範囲が前に出る）
        UiFocus.SetOverlay(plane.Scope, false);
        UiFocus.SendToBack(plane.Scope);
        Redraw.Request();
    }

    /// <summary>面を元へ戻す（底上げを書き戻し、重ねる範囲へ戻して前へ出す）。</summary>
    private static void RestorePark(ParkRecord record, ModalPlane plane)
    {
        if (record.OriginalBias is { } original) NavNode.SetBias(plane.Owner, original);
        plane.IsParked = false;
        UiFocus.SetOverlay(plane.Scope, true);
        UiFocus.BringToFront(plane.Scope);
        Redraw.Request();
    }

    /// <summary>面が閉じた・消えた: 画面の下へ回した記録を捨てる（戻すものは無い）。</summary>
    private void ForgetPark(ModalPlane plane)
    {
        if (plane.Handle is { } handle) ForgetParkOf(handle);
        plane.IsParked = false;
    }

    /// <summary>手札の画面の下へ回した記録を捨てる（全画面の知らせのつなぎも外す）。</summary>
    private void ForgetParkOf(ModalHandle handle)
    {
        if (!_parks.Remove(handle, out var record)) return;
        record.Page.Closed -= record.OnPageClosed;
    }

    /// <summary>部品が消えるとき: すべての記録を捨てる（全画面の知らせのつなぎも外す）。</summary>
    private void ForgetAllParks()
    {
        foreach (var record in _parks.Values) record.Page.Closed -= record.OnPageClosed;
        _parks.Clear();
    }
}
