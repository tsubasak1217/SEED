using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  DialogActionsLayout.cs — ダイアログのボタンの行: 横に並べるか縦に積むか（2026-10-02。純粋な計算）
//
//  Flutter の AlertDialog のボタンの行（OverflowBar。2026-09-29 に取得した dialog.dart で確かめた）と同じ規則:
//    - ボタンの幅の和 ＋ 間隔 ×（数 − 1）が札の中の幅に入るなら、横に並べて右へ寄せる（alignment: end。間隔はプレハブの Buttons の spacing 8）
//    - 入らなければ縦に積み、右へ寄せる（overflowAlignment: end）。上から左と同じ順（overflowDirection: down = 中立・いいえ・はい）、
//      積んだボタンの間は size.dialog_actions_overflow_gap（Flutter の overflowSpacing の既定 0）
//  1 つのボタンが中の幅より広いときは、その幅を中の幅で切る（文字ははみ出しうる。docs/ui_navigation.md §13）。
//  以前は縦に積まず、長い文字のボタン 3 つが札（312）からはみ出した（Wake or Pay の W3-4 (1)・P2-1 の残り (2)）。
// ============================================================

/// <summary>ボタンの行の並べ方の結果。</summary>
public sealed class DialogActionsArrangement
{
    /// <summary>作る（DialogActionsLayout.Arrange から）。</summary>
    internal DialogActionsArrangement(bool stacked, float rowHeight, float[] widths)
    {
        Stacked = stacked;
        RowHeight = rowHeight;
        Widths = widths;
    }

    /// <summary>縦に積むか（false なら横に並べる）。</summary>
    public bool Stacked { get; }

    /// <summary>ボタンの行の高さ（横なら ボタンの高さ、縦なら 数 × 高さ ＋ 間 ×（数 − 1）。ボタンが無ければ 0）。</summary>
    public float RowHeight { get; }

    /// <summary>ボタンごとの幅（中の幅で切った値。並びは入力と同じ）。</summary>
    public IReadOnlyList<float> Widths { get; }
}

/// <summary>ダイアログのボタンの行の並べ方。</summary>
public static class DialogActionsLayout
{
    /// <summary>
    /// 横に並べるか縦に積むかを決める。
    /// </summary>
    /// <param name="widths">ボタンごとの幅（左から。文字の幅の見積もり ＋ 左右の余白）。</param>
    /// <param name="spacing">横に並べるときの間隔（プレハブの Buttons の CanvasStack.Spacing）。</param>
    /// <param name="innerWidth">札の中の幅（並べる幅）。</param>
    /// <param name="buttonHeight">ボタンの高さ（size.dialog_button_height）。</param>
    /// <param name="overflowGap">縦に積むときの間（size.dialog_actions_overflow_gap）。</param>
    public static DialogActionsArrangement Arrange(IReadOnlyList<float> widths, float spacing, float innerWidth, float buttonHeight, float overflowGap)
    {
        int n = widths.Count;
        float height = NonNegative(buttonHeight);
        if (n == 0) return new DialogActionsArrangement(false, 0f, Array.Empty<float>());
        float inner = NonNegative(innerWidth);
        var clamped = new float[n];
        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            float w = NonNegative(widths[i]);
            // 中の幅より広いボタンは中の幅で切る（中の幅が分からない 0 のときは切らない）
            clamped[i] = inner > 0f ? Math.Min(w, inner) : w;
            sum += NonNegative(widths[i]);
        }
        float total = sum + NonNegative(spacing) * (n - 1);
        // 中の幅が分からない（0）なら横のまま（従来どおり）。入らなければ縦に積む
        bool stacked = inner > 0f && total > inner;
        float row = stacked ? n * height + NonNegative(overflowGap) * (n - 1) : height;
        return new DialogActionsArrangement(stacked, row, clamped);
    }

    /// <summary>負・有限でない値を 0 にする。</summary>
    private static float NonNegative(float value) => float.IsFinite(value) && value > 0f ? value : 0f;
}
