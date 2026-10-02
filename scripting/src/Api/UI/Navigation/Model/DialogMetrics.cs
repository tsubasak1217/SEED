using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  DialogMetrics.cs — ダイアログの札の縦の割り付けと、出入りの倍率（W2 の手直し P2-1。2026-10-02 に区画と高さの上限を足した。純粋な計算）
//
//  【札の高さ】札（Card）は縦の CanvasStack（余白 size.dialog_padding・間隔 0）に区画を上から並べる:
//      題 → 進捗（スピナーと本文。2026-10-02）→ 本文 → 選択肢の一覧（2026-10-02）→ 1 行の入力欄（W2-6b）→ ボタンの行
//    札の高さ = 上の余白 ＋ 見える区画の枠の和 ＋ 下の余白（出していない区画とその間隔は数えない）。これを C# で求めて札の
//    CanvasLayoutItem.PreferredSize と背景の Sprite.Size に書く（札は親の CanvasStack が矩形を割り当てるので、CanvasStack の
//    fit_height〈中身に合わせる〉では背景のスプライトが伸びない。canvas_layout/pass.rs の「割り当てがあれば割り当てが勝つ」）。
//  【間隔は区画ごと】Material 3 の間隔は区画で違う（題 → 本文 16・本文 → ボタン 24・題 → 選択肢の一覧 12）ので、CanvasStack の
//    等間隔の spacing は使わず 0 にし、間隔を「上の見える区画の枠の高さ」に足す（区画の枠 = 中身 ＋ 下の間隔。中身は枠の上端に置かれる）。
//    2 つの見える区画の間の間隔は、上の区画の GapBelow（あれば。選択肢の一覧は size.dialog_items_inset）か、下の区画の GapAbove
//    （本文・進捗・入力欄の上は size.dialog_title_gap、ボタンの行の上は size.dialog_actions_gap、選択肢の一覧の上は size.dialog_items_inset）。
//    本文が無いときの題 → ボタンは size.dialog_actions_gap（24。Flutter の AlertDialog は題の下の余白 20。docs/ui_navigation.md §3.2）。
//  【札の端の余白】上の余白は最初に見える区画の EdgeInset（あれば）、下の余白は最後に見える区画の EdgeInset（あれば）、無ければ
//    size.dialog_padding。選択肢の一覧が札の上端・下端に来るときは size.dialog_items_inset（Flutter の SimpleDialog の contentPadding）。
//  【高さの上限（2026-10-02）】札が「画面の高さ − 上下の安全領域 − size.dialog_margin × 2」を超えるときは、縮められる区画（選択肢の一覧、
//    次に本文）の窓を縮めて（下限は 1 行）その区画をスクロールにする（Fit）。Material 3 の長い本文のダイアログ・Flutter の SimpleDialog の
//    SingleChildScrollView と同じ考え。題・入力欄・ボタンは縮めない。
//  【出入りの倍率】札の見た目の倍率（CanvasLayoutItem.VisualScale。矩形の中心の周りに部分木ごと縮む）= 開き具合の倍率 × 予測型の戻るの
//    プレビューの倍率。確定した戻るの後は、プレビューの倍率を保ったまま開き具合の倍率だけが閉じる向きへ進む（縮めた姿勢のまま出る）。
// ============================================================

/// <summary>
/// 札の縦の 1 区画（上から順。題・進捗・本文・選択肢の一覧・入力欄・ボタンの行）。
/// </summary>
/// <param name="Height">中身の高さ（キャンバスの単位。0 以下・有限でない = 出さない）。</param>
/// <param name="GapAbove">上に見える区画があるときの、その区画との間隔（キャンバスの単位。負・有限でないは 0）。</param>
/// <param name="GapBelow">下に見える区画があるときの間隔の上書き（null = 下の区画の GapAbove。2026-10-02）。</param>
/// <param name="EdgeInset">この区画が札の上端・下端に来るときの札の余白（null = 札の余白。2026-10-02）。</param>
public readonly record struct DialogSection(float Height, float GapAbove, float? GapBelow = null, float? EdgeInset = null)
{
    /// <summary>出す区画か（中身の高さが有限の正）。</summary>
    public bool Shown => float.IsFinite(Height) && Height > 0f;
}

/// <summary>区画の中身の高さ（2026-10-02。0 = 出さない）。</summary>
/// <param name="Title">題。</param>
/// <param name="Progress">進捗（スピナーと本文の行）。</param>
/// <param name="Message">本文。</param>
/// <param name="Items">選択肢の一覧（行の数 × 行の高さ）。</param>
/// <param name="Input">1 行の入力欄。</param>
/// <param name="Buttons">ボタンの行（縦に積むときは積んだ高さ）。</param>
public readonly record struct DialogContentHeights(float Title, float Progress, float Message, float Items, float Input, float Buttons);

/// <summary>区画の間隔（2026-10-02）。</summary>
/// <param name="TitleGap">題 → 本文・進捗・入力欄の間隔（size.dialog_title_gap）。</param>
/// <param name="ActionsGap">ボタンの行の上の間隔（size.dialog_actions_gap）。</param>
/// <param name="ItemsInset">選択肢の一覧の上下の空き（size.dialog_items_inset）。</param>
public readonly record struct DialogSpacing(float TitleGap, float ActionsGap, float ItemsInset);

/// <summary>縮めてよい区画（高さの上限を超えたとき。2026-10-02）。</summary>
/// <param name="Index">区画の番号（DialogMetrics.*Section）。</param>
/// <param name="MinHeight">縮める下限（キャンバスの単位。1 行の高さなど）。</param>
public readonly record struct DialogShrink(int Index, float MinHeight);

/// <summary>札の縦の割り付けの結果。</summary>
public sealed class DialogCardLayout
{
    /// <summary>作る（DialogMetrics.Arrange から）。</summary>
    internal DialogCardLayout(float cardHeight, float[] slotHeights, float[] gapsBelow, float paddingTop, float paddingBottom)
    {
        CardHeight = cardHeight;
        SlotHeights = slotHeights;
        GapsBelow = gapsBelow;
        PaddingTop = paddingTop;
        PaddingBottom = paddingBottom;
    }

    /// <summary>札の高さ（上下の余白を含む。キャンバスの単位）。</summary>
    public float CardHeight { get; }

    /// <summary>区画ごとの枠の高さ（CanvasLayoutItem の高さ = 中身 ＋ 下の間隔。出さない区画は 0）。</summary>
    public IReadOnlyList<float> SlotHeights { get; }

    /// <summary>区画ごとの下の間隔（次に見える区画との間。最後に見える区画・出さない区画は 0）。</summary>
    public IReadOnlyList<float> GapsBelow { get; }

    /// <summary>札の上の余白（最初に見える区画の EdgeInset か札の余白。2026-10-02）。</summary>
    public float PaddingTop { get; }

    /// <summary>札の下の余白（最後に見える区画の EdgeInset か札の余白。2026-10-02）。</summary>
    public float PaddingBottom { get; }
}

/// <summary>高さの上限に合わせた区画（2026-10-02）。</summary>
public sealed class DialogFitResult
{
    /// <summary>作る（DialogMetrics.Fit から）。</summary>
    internal DialogFitResult(DialogSection[] sections, bool[] scrolls)
    {
        Sections = sections;
        Scrolls = scrolls;
    }

    /// <summary>縮めた後の区画（縮めていない区画は元のまま）。</summary>
    public IReadOnlyList<DialogSection> Sections { get; }

    /// <summary>区画ごとに、縮めたか（縮めた区画は窓が中身より低い＝スクロールする）。</summary>
    public IReadOnlyList<bool> Scrolls { get; }
}

/// <summary>ダイアログの札の縦の割り付けと出入りの倍率。</summary>
public static class DialogMetrics
{
    /// <summary>題の区画の番号（<see cref="Sections(DialogContentHeights, DialogSpacing)"/> の並び）。</summary>
    public const int TitleSection = 0;
    /// <summary>進捗（スピナーと本文の行）の区画の番号（2026-10-02。DialogOptions.Progress のときだけ）。</summary>
    public const int ProgressSection = 1;
    /// <summary>本文の区画の番号。</summary>
    public const int MessageSection = 2;
    /// <summary>選択肢の一覧の区画の番号（2026-10-02。DialogOptions.Items があるときだけ）。</summary>
    public const int ItemsSection = 3;
    /// <summary>1 行の入力欄の区画の番号（W2-6b。DialogOptions.Input が無ければ高さ 0 で出さない）。</summary>
    public const int InputSection = 4;
    /// <summary>ボタンの行の区画の番号。</summary>
    public const int ButtonsSection = 5;
    /// <summary>区画の数。</summary>
    public const int SectionCount = 6;

    /// <summary>札の左右の余白の数（左と右）。</summary>
    private const float HorizontalPaddingCount = 2f;
    /// <summary>札の上下の端の余白の数（上と下。画面の端との間の上限の計算）。</summary>
    private const float VerticalMarginCount = 2f;
    /// <summary>倍率なし（開いた・プレビューなし）。</summary>
    private const float NoScale = 1f;
    /// <summary>いちばん上の区画（題）の上の間隔（上に区画が無いので使われない。札の上は余白だけ）。</summary>
    private const float NoGapAbove = 0f;

    /// <summary>
    /// ダイアログの区画（題・本文・1 行の入力欄・ボタンの行。W2 の手直し P2-1・W2-6b の形。進捗と選択肢の一覧は出さない）。
    /// </summary>
    /// <param name="titleHeight">題の枠の高さ（題が空なら 0）。</param>
    /// <param name="messageHeight">本文の枠の高さ（本文が空なら 0）。</param>
    /// <param name="buttonsHeight">ボタンの行の高さ。</param>
    /// <param name="titleGap">題 → 本文の間隔（size.dialog_title_gap）。題・本文 → 入力欄の間隔にも使う。</param>
    /// <param name="actionsGap">本文（本文が無ければ題・入力欄があれば入力欄）→ ボタンの行の間隔（size.dialog_actions_gap）。</param>
    /// <param name="inputHeight">1 行の入力欄の高さ（W2-6b。入力欄が無ければ 0＝出さない）。</param>
    public static IReadOnlyList<DialogSection> Sections(float titleHeight, float messageHeight, float buttonsHeight, float titleGap, float actionsGap,
        float inputHeight = 0f)
        => Sections(new DialogContentHeights(titleHeight, 0f, messageHeight, 0f, inputHeight, buttonsHeight),
            new DialogSpacing(titleGap, actionsGap, 0f));

    /// <summary>
    /// ダイアログの 6 つの区画（題・進捗・本文・選択肢の一覧・1 行の入力欄・ボタンの行の順。2026-10-02）。高さ 0 の区画は出さない。
    /// </summary>
    /// <param name="heights">区画の中身の高さ。</param>
    /// <param name="spacing">区画の間隔。</param>
    public static IReadOnlyList<DialogSection> Sections(DialogContentHeights heights, DialogSpacing spacing)
    {
        float inset = NonNegative(spacing.ItemsInset);
        var sections = new DialogSection[SectionCount];
        sections[TitleSection] = new DialogSection(heights.Title, NoGapAbove);
        sections[ProgressSection] = new DialogSection(heights.Progress, spacing.TitleGap);
        sections[MessageSection] = new DialogSection(heights.Message, spacing.TitleGap);
        // 選択肢の一覧: 上下の空き（上の区画との間・下の区画との間・札の端の余白）はどれも size.dialog_items_inset
        sections[ItemsSection] = new DialogSection(heights.Items, inset, inset, inset);
        sections[InputSection] = new DialogSection(heights.Input, spacing.TitleGap);
        sections[ButtonsSection] = new DialogSection(heights.Buttons, spacing.ActionsGap);
        return sections;
    }

    /// <summary>
    /// 区画を上から並べる: 見える区画の間に間隔（上の区画の GapBelow か下の区画の GapAbove）を入れ、その間隔を上の区画の枠に足す。
    /// 札の高さ = 上の余白 ＋ 枠の和 ＋ 下の余白（端の区画の EdgeInset があればそれ、無ければ <paramref name="padding"/>）。
    /// </summary>
    /// <param name="padding">札の内側の余白（上下とも。負・有限でないは 0）。</param>
    /// <param name="sections">区画（上から順）。</param>
    public static DialogCardLayout Arrange(float padding, IReadOnlyList<DialogSection> sections)
    {
        int n = sections.Count;
        var slots = new float[n];
        var gaps = new float[n];
        int previous = -1, first = -1;
        for (int i = 0; i < n; i++)
        {
            var section = sections[i];
            if (!section.Shown) continue;
            if (first < 0) first = i;
            // 上に見える区画があれば、その区画の下の間隔 = 上の区画の GapBelow か、この区画の上の間隔
            if (previous >= 0)
            {
                float gap = NonNegative(sections[previous].GapBelow ?? section.GapAbove);
                gaps[previous] = gap;
                slots[previous] += gap;
            }
            slots[i] = section.Height;
            previous = i;
        }
        float sum = 0f;
        foreach (float slot in slots) sum += slot;
        float pad = NonNegative(padding);
        float top = first >= 0 ? NonNegative(sections[first].EdgeInset ?? pad) : pad;
        float bottom = previous >= 0 ? NonNegative(sections[previous].EdgeInset ?? pad) : pad;
        return new DialogCardLayout(top + sum + bottom, slots, gaps, top, bottom);
    }

    /// <summary>
    /// 札の高さが上限を超えるなら、縮めてよい区画を順に縮める（下限まで）。縮めた区画はスクロールにする（2026-10-02）。
    /// 上限が有限の正でなければ縮めない。縮めきっても超える（題・ボタンだけで画面より高い）ときは超えたまま。
    /// </summary>
    /// <param name="padding">札の内側の余白（<see cref="Arrange"/> と同じ）。</param>
    /// <param name="sections">区画（上から順）。</param>
    /// <param name="maxCardHeight">札の高さの上限（<see cref="MaxCardHeight"/>）。</param>
    /// <param name="shrinkOrder">縮めてよい区画（縮める順。選択肢の一覧 → 本文）。</param>
    public static DialogFitResult Fit(float padding, IReadOnlyList<DialogSection> sections, float maxCardHeight, IReadOnlyList<DialogShrink> shrinkOrder)
    {
        var fitted = new DialogSection[sections.Count];
        for (int i = 0; i < fitted.Length; i++) fitted[i] = sections[i];
        var scrolls = new bool[sections.Count];
        if (!(float.IsFinite(maxCardHeight) && maxCardHeight > 0f)) return new DialogFitResult(fitted, scrolls);
        float overflow = Arrange(padding, fitted).CardHeight - maxCardHeight;
        foreach (var shrink in shrinkOrder)
        {
            if (!(overflow > 0f)) break;
            if (shrink.Index < 0 || shrink.Index >= fitted.Length || !fitted[shrink.Index].Shown) continue;
            var section = fitted[shrink.Index];
            float reducible = Math.Max(0f, section.Height - NonNegative(shrink.MinHeight));
            float take = Math.Min(reducible, overflow);
            if (!(take > 0f)) continue;
            fitted[shrink.Index] = section with { Height = section.Height - take };
            scrolls[shrink.Index] = true;
            overflow -= take;
        }
        return new DialogFitResult(fitted, scrolls);
    }

    /// <summary>
    /// 札の高さの上限 = 覆う領域の高さ − 上下の安全領域 − 端との間 × 2（0 未満にしない。領域の高さが有限の正でなければ上限なし＝無限大）。
    /// </summary>
    /// <param name="areaHeight">ダイアログが覆う領域の高さ（ダイアログの帯のレイアウトの高さ）。</param>
    /// <param name="insetTop">上の安全領域（キャンバスの単位）。</param>
    /// <param name="insetBottom">下の安全領域。</param>
    /// <param name="margin">札と端の最小の間（size.dialog_margin）。</param>
    public static float MaxCardHeight(float areaHeight, float insetTop, float insetBottom, float margin)
    {
        if (!(float.IsFinite(areaHeight) && areaHeight > 0f)) return float.PositiveInfinity;
        return Math.Max(0f, areaHeight - NonNegative(insetTop) - NonNegative(insetBottom) - VerticalMarginCount * NonNegative(margin));
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
