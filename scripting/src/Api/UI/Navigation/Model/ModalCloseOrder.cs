using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ModalCloseOrder.cs — 開いている面を「全部閉じる」順と結果（ModalHost.CloseAll。2026-10-02。lane3。純粋な計算。
//                       docs/ui_navigation.md §3.1。backlog W3-2 (1)）
//
//  閉じる順は戻るの段（BackOrder）と同じく上の層から: ダイアログ → シート → 覆い。同じ種類の中は後から開いた面（手前）から。
//  戻るを 1 回ずつ押して閉じていったのと同じ順になる（Wake or Pay は鳴動画面を出す前に戻るを上限つきで繰り返して代えていた）。
//  結果: ダイアログは DialogResult.Dismissed（DialogHandle.Dismiss と同じ）、シート・覆い（ポップアップを含む）は null
//  （幕のタップ・戻るで閉じたときと同じ）。閉じない設定（DismissOnScrimTap・CancelableByBack = false）の面も閉じる。
// ============================================================

/// <summary>開いている面を全部閉じる順と結果。</summary>
public static class ModalCloseOrder
{
    /// <summary>閉じる種類の順（上の層から。戻るの段の Dialog 200 → Sheet 300 → Overlay 400 と同じ）。</summary>
    public static IReadOnlyList<ModalKind> KindsTopFirst { get; } = new[] { ModalKind.Dialog, ModalKind.Sheet, ModalKind.Overlay };

    /// <summary>
    /// 閉じる順に並べる（種類は上の層から、同じ種類の中は新しい順）。
    /// </summary>
    /// <typeparam name="T">面（部品・番号など）。</typeparam>
    /// <param name="openedOldestFirst">種類 → 開いた順（古い順）の面。</param>
    /// <param name="only">この種類だけ（null = 全部の種類）。</param>
    /// <returns>閉じる順の面。</returns>
    public static List<T> Sequence<T>(Func<ModalKind, IReadOnlyList<T>> openedOldestFirst, ModalKind? only = null)
    {
        var order = new List<T>();
        foreach (var kind in KindsTopFirst)
        {
            if (only is { } k && k != kind) continue;
            var opened = openedOldestFirst(kind);
            for (int i = opened.Count - 1; i >= 0; i--) order.Add(opened[i]);
        }
        return order;
    }

    /// <summary>全部閉じるで閉じた面の結果（ダイアログは Dismissed・シートと覆いは null）。</summary>
    /// <param name="kind">面の種類。</param>
    /// <returns>手札へ渡す結果。</returns>
    public static object? ResultFor(ModalKind kind) => kind == ModalKind.Dialog ? DialogResult.Dismissed : null;
}
