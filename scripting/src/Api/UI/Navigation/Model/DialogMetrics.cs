using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  DialogMetrics.cs — ダイアログの札の縦の割り付けと、出入りの倍率（W2 の手直し P2-1。純粋な計算）
//
//  【札の高さ】札（Card）は縦の CanvasStack（余白 size.dialog_padding・間隔 0）に 題・本文・（入力欄）・ボタンの行 を並べる
//    （入力欄は W2-6b の DialogOptions.Input のときだけ。題・本文との間は size.dialog_title_gap、ボタンの行との間は size.dialog_actions_gap）。
//    札の高さ = 上の余白 ＋ 題 ＋ 間隔 ＋ 本文 ＋ 間隔 ＋ ボタンの行 ＋ 下の余白（出していない区画とその間隔は数えない）。
//    これを C# で求めて札の CanvasLayoutItem.PreferredSize と背景の Sprite.Size に書く（札は親の CanvasStack が矩形を割り当てるので、
//    CanvasStack の fit_height〈中身に合わせる〉では背景のスプライトが伸びない。canvas_layout/pass.rs の「割り当てがあれば割り当てが勝つ」）。
//  【間隔は区画ごと】Material 3 の間隔は区画で違う（題 → 本文 16・本文 → ボタン 24）ので、CanvasStack の等間隔の spacing は使わず 0 にし、
//    間隔を「上の見える区画の枠の高さ」に足す（区画の枠 = 中身 ＋ 下の間隔。中身の Text は枠の上端に置かれるので、間隔は枠の下の空きになる）。
//    2 つの見える区画の間の間隔は、下の区画の GapAbove（本文の上は size.dialog_title_gap、ボタンの行の上は size.dialog_actions_gap）。
//    本文が無いときの題 → ボタンは size.dialog_actions_gap（24）。Flutter の AlertDialog はこのとき題の下の余白 20 だが、トークンを増やさない
//    ためボタンの行の上の間隔にそろえた（docs/ui_navigation.md §3.2）。
//  【出入りの倍率】札の見た目の倍率（CanvasLayoutItem.VisualScale。矩形の中心の周りに部分木ごと縮む）= 開き具合の倍率 × 予測型の戻るの
//    プレビューの倍率。確定した戻るの後は、プレビューの倍率を保ったまま開き具合の倍率だけが閉じる向きへ進む（縮めた姿勢のまま出る）。
// ============================================================

/// <summary>
/// 札の縦の 1 区画（上から順。題・本文・ボタンの行）。
/// </summary>
/// <param name="Height">中身の高さ（キャンバスの単位。0 以下・有限でない = 出さない）。</param>
/// <param name="GapAbove">上に見える区画があるときの、その区画との間隔（キャンバスの単位。負・有限でないは 0）。</param>
public readonly record struct DialogSection(float Height, float GapAbove)
{
    /// <summary>出す区画か（中身の高さが有限の正）。</summary>
    public bool Shown => float.IsFinite(Height) && Height > 0f;
}

/// <summary>札の縦の割り付けの結果。</summary>
public sealed class DialogCardLayout
{
    /// <summary>作る（DialogMetrics.Arrange から）。</summary>
    internal DialogCardLayout(float cardHeight, float[] slotHeights, float[] gapsBelow)
    {
        CardHeight = cardHeight;
        SlotHeights = slotHeights;
        GapsBelow = gapsBelow;
    }

    /// <summary>札の高さ（上下の余白を含む。キャンバスの単位）。</summary>
    public float CardHeight { get; }

    /// <summary>区画ごとの枠の高さ（CanvasLayoutItem の高さ = 中身 ＋ 下の間隔。出さない区画は 0）。</summary>
    public IReadOnlyList<float> SlotHeights { get; }

    /// <summary>区画ごとの下の間隔（次に見える区画の GapAbove。最後に見える区画・出さない区画は 0）。</summary>
    public IReadOnlyList<float> GapsBelow { get; }
}

/// <summary>ダイアログの札の縦の割り付けと出入りの倍率。</summary>
public static class DialogMetrics
{
    /// <summary>題の区画の番号（<see cref="Sections"/> の並び）。</summary>
    public const int TitleSection = 0;
    /// <summary>本文の区画の番号。</summary>
    public const int MessageSection = 1;
    /// <summary>1 行の入力欄の区画の番号（W2-6b。DialogOptions.Input が無ければ高さ 0 で出さない）。</summary>
    public const int InputSection = 2;
    /// <summary>ボタンの行の区画の番号。</summary>
    public const int ButtonsSection = 3;

    /// <summary>札の上下の余白の数（上と下）。</summary>
    private const float VerticalPaddingCount = 2f;
    /// <summary>札の左右の余白の数（左と右）。</summary>
    private const float HorizontalPaddingCount = 2f;
    /// <summary>倍率なし（開いた・プレビューなし）。</summary>
    private const float NoScale = 1f;
    /// <summary>いちばん上の区画（題）の上の間隔（上に区画が無いので使われない。札の上は余白だけ）。</summary>
    private const float NoGapAbove = 0f;

    /// <summary>
    /// ダイアログの 4 つの区画（題・本文・1 行の入力欄〈W2-6b〉・ボタンの行の順）。高さ 0 の区画は出さない。
    /// </summary>
    /// <param name="titleHeight">題の枠の高さ（題が空なら 0）。</param>
    /// <param name="messageHeight">本文の枠の高さ（本文が空なら 0）。</param>
    /// <param name="buttonsHeight">ボタンの行の高さ。</param>
    /// <param name="titleGap">題 → 本文の間隔（size.dialog_title_gap）。題・本文 → 入力欄の間隔にも使う。</param>
    /// <param name="actionsGap">本文（本文が無ければ題・入力欄があれば入力欄）→ ボタンの行の間隔（size.dialog_actions_gap）。</param>
    /// <param name="inputHeight">1 行の入力欄の高さ（W2-6b。入力欄が無ければ 0＝出さない）。</param>
    public static IReadOnlyList<DialogSection> Sections(float titleHeight, float messageHeight, float buttonsHeight, float titleGap, float actionsGap,
        float inputHeight = 0f)
        => new[]
        {
            new DialogSection(titleHeight, NoGapAbove),
            new DialogSection(messageHeight, titleGap),
            new DialogSection(inputHeight, titleGap),
            new DialogSection(buttonsHeight, actionsGap),
        };

    /// <summary>
    /// 区画を上から並べる: 見える区画の間に下の区画の GapAbove を入れ、その間隔を上の区画の枠に足す。札の高さ = 余白 × 2 ＋ 枠の和。
    /// </summary>
    /// <param name="padding">札の内側の余白（上下とも。負・有限でないは 0）。</param>
    /// <param name="sections">区画（上から順）。</param>
    public static DialogCardLayout Arrange(float padding, IReadOnlyList<DialogSection> sections)
    {
        int n = sections.Count;
        var slots = new float[n];
        var gaps = new float[n];
        int previous = -1;
        for (int i = 0; i < n; i++)
        {
            var section = sections[i];
            if (!section.Shown) continue;
            // 上に見える区画があれば、その区画の下の間隔 = この区画の上の間隔
            if (previous >= 0)
            {
                float gap = NonNegative(section.GapAbove);
                gaps[previous] = gap;
                slots[previous] += gap;
            }
            slots[i] = section.Height;
            previous = i;
        }
        float sum = 0f;
        foreach (float slot in slots) sum += slot;
        return new DialogCardLayout(VerticalPaddingCount * NonNegative(padding) + sum, slots, gaps);
    }

    /// <summary>札の中の幅（札の幅 − 左右の余白。0 未満にしない）。</summary>
    /// <param name="cardWidth">札の幅（size.dialog_width）。</param>
    /// <param name="padding">内側の余白（size.dialog_padding）。</param>
    public static float InnerWidth(float cardWidth, float padding)
        => Math.Max(0f, NonNegative(cardWidth) - HorizontalPaddingCount * NonNegative(padding));

    /// <summary>
    /// 開き具合 → 札の倍率（出るときの最初の大きさ ratio.dialog_scale_from から 1 へ）。
    /// </summary>
    /// <param name="progress">開き具合（曲線を通した 0〜1。0 = 閉じた・1 = 開いた。範囲の外は収める。有限でなければ開いたとみなす）。</param>
    /// <param name="scaleFrom">閉じたときの倍率（有限でなければ 1 = 縮めない）。</param>
    public static float OpenScale(float progress, float scaleFrom)
    {
        float from = float.IsFinite(scaleFrom) ? scaleFrom : NoScale;
        float p = float.IsFinite(progress) ? Math.Clamp(progress, 0f, 1f) : 1f;
        return from + (NoScale - from) * p;
    }

    /// <summary>
    /// 札に書く見た目の倍率 = 開き具合の倍率 × 予測型の戻るのプレビューの倍率（有限でない方は 1 とみなす）。
    /// </summary>
    /// <param name="openScale">開き具合の倍率（<see cref="OpenScale"/>）。</param>
    /// <param name="previewScale">プレビューの倍率（BackPreviewPose.Scale。プレビューなしは 1）。</param>
    public static float CardScale(float openScale, float previewScale)
        => (float.IsFinite(openScale) ? openScale : NoScale) * (float.IsFinite(previewScale) ? previewScale : NoScale);

    /// <summary>負・有限でない値を 0 にする（壊れたトークンで札の大きさを壊さない）。</summary>
    private static float NonNegative(float value) => float.IsFinite(value) && value > 0f ? value : 0f;
}
