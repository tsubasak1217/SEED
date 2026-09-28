using System;

namespace SEED.UI;

// ============================================================
//  ChartViewport.cs — 横の見える範囲（パンとズーム。W2-8。docs/ui_charts.md §5）
//
//  【状態】全体の範囲 Full（データの X の範囲）、倍率 Zoom（1 = 全体が見える。MinZoom〜MaxZoom。Wake or Pay は 1〜6）、
//  見える範囲の左端 Start。見える幅 = Full の幅 ÷ Zoom。見える範囲はいつも Full の中（端で止まる。跳ね返りは無い）。
//  【操作】
//    - パン: 指の横の移動（dp）を値へ直して Start を動かす（右へ引くと前の日が見える）。端で止まる
//    - ズーム: 倍率を変えても、フォーカスの位置（ピンチの中点・ボタンなら真ん中）の下の値は動かさない
//    - ピンチ: 始まったときのフォーカスの値を、今のフォーカスの位置の下へ置く（ズームと 2 本指のパンが同時に効く）
//    - 範囲を見せる（Show）: 指定した範囲がちょうど見える倍率と位置（倍率の上下限と端で収める）
//  エンジンに触れない純粋な計算（editor/tests/UiComponentsTests で検算）。
// ============================================================

/// <summary>全体の範囲が変わったときに、見える範囲をどこへ置くか。</summary>
public enum ViewportAnchor
{
    /// <summary>左端の値を保つ。</summary>
    KeepStart = 0,
    /// <summary>右端（最新）に付ける（データの右端が見えていたら、足された日が見えるように右端に寄せる）。</summary>
    StickToEnd = 1,
    /// <summary>倍率 1（全体）へ戻す。</summary>
    Reset = 2,
}

/// <summary>横の見える範囲（パンとズーム）の計算。</summary>
public sealed class ChartViewport
{
    /// <summary>倍率の下限の既定（全体が見える）。</summary>
    public const double DefaultMinZoom = 1.0;
    /// <summary>倍率の上限の既定（Wake or Pay のペナルティ履歴・起床時間の全期間の 6 倍）。</summary>
    public const double DefaultMaxZoom = 6.0;
    /// <summary>位置・倍率の比べる許容量。</summary>
    private const double Epsilon = 1e-9;
    /// <summary>「全体が見えている」とみなす見える幅の割合の許容量（float の往復で 1.0000001 倍になっても動かせるとしない）。</summary>
    private const double FullViewTolerance = 1e-6;

    /// <summary>全体の範囲。</summary>
    public ChartRange Full { get; private set; } = new(0, 1);
    /// <summary>倍率の下限（1 以上）。</summary>
    public double MinZoom { get; private set; } = DefaultMinZoom;
    /// <summary>倍率の上限。</summary>
    public double MaxZoom { get; private set; } = DefaultMaxZoom;
    /// <summary>倍率（1 = 全体が見える）。</summary>
    public double Zoom { get; private set; } = DefaultMinZoom;
    /// <summary>見える範囲の左端。</summary>
    public double Start { get; private set; }

    /// <summary>見える幅。</summary>
    public double VisibleSpan => Full.SafeSpan / Zoom;

    /// <summary>見える範囲。</summary>
    public ChartRange Visible => new(Start, Start + VisibleSpan);

    /// <summary>横に動かせるか（全体より狭く見ている）。</summary>
    public bool CanPan => VisibleSpan < Full.SafeSpan * (1.0 - FullViewTolerance);

    /// <summary>左端に着いているか。</summary>
    public bool AtStart => Start <= Full.Min + Epsilon * Full.SafeSpan;

    /// <summary>右端に着いているか。</summary>
    public bool AtEnd => Start + VisibleSpan >= Full.Max - Epsilon * Full.SafeSpan;

    /// <summary>倍率の上下限を決める（下限は 1 以上・上限は下限以上。今の倍率も収める）。</summary>
    public void SetZoomLimits(double minZoom, double maxZoom)
    {
        MinZoom = double.IsFinite(minZoom) ? Math.Max(DefaultMinZoom, minZoom) : DefaultMinZoom;
        MaxZoom = double.IsFinite(maxZoom) ? Math.Max(MinZoom, maxZoom) : MinZoom;
        ApplyZoom(Zoom, Start + VisibleSpan * 0.5, 0.5);
    }

    /// <summary>全体の範囲を変える（データが変わった）。見える範囲の置き方は <paramref name="anchor"/>。</summary>
    public void SetFull(ChartRange full, ViewportAnchor anchor = ViewportAnchor.KeepStart)
    {
        bool wasAtEnd = AtEnd;
        double span = VisibleSpan;
        Full = full.IsFinite ? full : new ChartRange(0, 1);
        switch (anchor)
        {
            case ViewportAnchor.Reset:
                Zoom = MinZoom;
                Start = Full.Min;
                break;
            case ViewportAnchor.StickToEnd when wasAtEnd:
                // 見えていた幅を保って右端へ
                Zoom = Math.Clamp(Full.SafeSpan / span, MinZoom, MaxZoom);
                Start = ClampStart(Full.Max - VisibleSpan);
                break;
            default:
                Zoom = Math.Clamp(Full.SafeSpan / span, MinZoom, MaxZoom);
                Start = ClampStart(Start);
                break;
        }
    }

    /// <summary>左端を <paramref name="start"/> へ（端で止める）。動いたら true。</summary>
    public bool PanTo(double start)
    {
        double s = ClampStart(start);
        bool moved = Math.Abs(s - Start) > Epsilon * Full.SafeSpan;
        Start = s;
        return moved;
    }

    /// <summary>値の単位で動かす（正で右＝後の値が見える）。動いたら true。</summary>
    public bool PanBy(double delta) => PanTo(Start + delta);

    /// <summary>
    /// 指の横の移動（描く空間の単位）で動かす。指を右へ動かすと中身も右へ動く（前の値が見える）。動いたら true。
    /// </summary>
    /// <param name="deltaLocal">指の移動（右が正）。</param>
    /// <param name="plotLength">面の幅（描く空間の単位）。</param>
    public bool PanByLocal(float deltaLocal, float plotLength)
    {
        if (!(plotLength > 0)) return false;
        return PanBy(-deltaLocal * VisibleSpan / plotLength);
    }

    /// <summary>描く空間の長さ → 値の長さ（今の倍率で）。</summary>
    public double LocalToValue(float length, float plotLength) => plotLength > 0 ? length * VisibleSpan / plotLength : 0;

    /// <summary>
    /// 倍率を変える。フォーカス（見える範囲の中の割合。0 = 左端・1 = 右端）の下の値は動かさない。変わったら true。
    /// </summary>
    public bool ZoomAround(double zoom, double focalFraction)
    {
        double f = Math.Clamp(focalFraction, 0, 1);
        return ApplyZoom(zoom, Start + VisibleSpan * f, f);
    }

    /// <summary>
    /// 倍率を変え、値 <paramref name="anchorValue"/> を見える範囲の割合 <paramref name="anchorFraction"/> の位置へ置く
    /// （ピンチ: 始まったときのフォーカスの値を今のフォーカスの位置へ）。変わったら true。
    /// </summary>
    public bool ApplyZoom(double zoom, double anchorValue, double anchorFraction)
    {
        double oldZoom = Zoom, oldStart = Start;
        Zoom = double.IsFinite(zoom) ? Math.Clamp(zoom, MinZoom, MaxZoom) : oldZoom;
        Start = ClampStart(anchorValue - VisibleSpan * anchorFraction);
        return Math.Abs(Zoom - oldZoom) > Epsilon || Math.Abs(Start - oldStart) > Epsilon * Full.SafeSpan;
    }

    /// <summary>範囲 <paramref name="range"/> がちょうど見える倍率と位置にする（上下限と端で収める）。</summary>
    public void Show(ChartRange range)
    {
        double zoom = Full.SafeSpan / (range.Span > Epsilon ? range.Span : Full.SafeSpan);
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Start = ClampStart(range.Min + (range.Span - VisibleSpan) * 0.5);
    }

    /// <summary>左端を端の中へ収める。</summary>
    private double ClampStart(double start)
    {
        double max = Full.Max - VisibleSpan;
        if (max <= Full.Min || !double.IsFinite(start)) return Full.Min;
        return Math.Clamp(start, Full.Min, max);
    }
}
