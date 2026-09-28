namespace SEED.UI;

// ============================================================
//  ChartLook.cs — テーマ → グラフの見た目の値（W2-8。docs/ui_charts.md §7）
//
//  グラフはテーマが替わるたび（UiWidget.ApplyLook）にここで値をまとめて読み、毎フレームの描画はこの値だけを使う
//  （描画のたびにトークンを引かない）。値の意味と既定は ChartTokens・default_theme.json。
// ============================================================

/// <summary>グラフの見た目の値（テーマから読んだもの）。</summary>
public struct ChartLook
{
    /// <summary>系列の色（系列の番号の順）。</summary>
    public Color[] Series;
    /// <summary>格子線・軸・目盛りの文字・基準線・空の棒・選んだ列の強調・吹き出しの面と文字の色。</summary>
    public Color Grid, Axis, Label, Reference, EmptyBar, Highlight, Tooltip, OnTooltip;
    /// <summary>線・点・選んだ点・格子線・軸・基準線の太さ（半径）。</summary>
    public float Line, Dot, DotSelected, DotMinSpacing, GridWidth, AxisWidth, ReferenceWidth;
    /// <summary>軸の文字の列・行・文字の最小の間隔・面との間・面の余白・当たりの横の距離・曲線の刻み・棒の最小の太さ。</summary>
    public float YAxis, XAxis, XLabelSpacing, YLabelSpacing, LabelGap, PlotPad, TouchSlop, SmoothStep, BarMin;
    /// <summary>吹き出しの余白・点との間・角丸、棒の角丸、目盛りの文字・吹き出しの文字の大きさ。</summary>
    public float TooltipPadding, TooltipGap, TooltipRadius, BarRadius, AxisText, TooltipText;
    /// <summary>棒の太さの割合・空の棒の高さの割合・慣性の減速・± の倍率・線の下の塗りの濃さ・慣性の止まる速さ・± の動きの時間。</summary>
    public float BarWidth, EmptyBarRatio, FlingDrag, ZoomStep, AreaOpacity, FlingStop, ZoomMotion;
    /// <summary>縦軸の区間の数の上限。</summary>
    public int YIntervals;

    /// <summary>系列の番号の色（足りなければ最後の色）。</summary>
    public readonly Color SeriesColor(int index)
    {
        if (Series == null || Series.Length == 0) return Color.White;
        return Series[index < 0 ? 0 : (index >= Series.Length ? Series.Length - 1 : index)];
    }

    /// <summary>テーマから読む。</summary>
    public static ChartLook From(UiThemeData theme)
    {
        var series = new Color[ChartTokens.SeriesColors.Length];
        for (int i = 0; i < series.Length; i++) series[i] = theme.Color(ChartTokens.SeriesColors[i]);
        return new ChartLook
        {
            Series = series,
            Grid = theme.Color(ChartTokens.ColorGrid),
            Axis = theme.Color(ChartTokens.ColorAxis),
            Label = theme.Color(ChartTokens.ColorLabel),
            Reference = theme.Color(ChartTokens.ColorReference),
            EmptyBar = theme.Color(ChartTokens.ColorEmptyBar),
            Highlight = theme.Color(ChartTokens.ColorHighlight),
            Tooltip = theme.Color(ChartTokens.ColorTooltip),
            OnTooltip = theme.Color(ChartTokens.ColorOnTooltip),
            Line = theme.Number(ChartTokens.SizeLine),
            Dot = theme.Number(ChartTokens.SizeDot),
            DotSelected = theme.Number(ChartTokens.SizeDotSelected),
            DotMinSpacing = theme.Number(ChartTokens.SizeDotMinSpacing),
            GridWidth = theme.Number(ChartTokens.SizeGrid),
            AxisWidth = theme.Number(ChartTokens.SizeAxis),
            ReferenceWidth = theme.Number(ChartTokens.SizeReference),
            YAxis = theme.Number(ChartTokens.SizeYAxis),
            XAxis = theme.Number(ChartTokens.SizeXAxis),
            XLabelSpacing = theme.Number(ChartTokens.SizeXLabelSpacing),
            YLabelSpacing = theme.Number(ChartTokens.SizeYLabelSpacing),
            LabelGap = theme.Number(ChartTokens.SizeLabelGap),
            PlotPad = theme.Number(ChartTokens.SizePlotPad),
            TouchSlop = theme.Number(ChartTokens.SizeTouchSlop),
            SmoothStep = theme.Number(ChartTokens.SizeSmoothStep),
            BarMin = theme.Number(ChartTokens.SizeBarMin),
            TooltipPadding = theme.Number(ChartTokens.SizeTooltipPadding),
            TooltipGap = theme.Number(ChartTokens.SizeTooltipGap),
            TooltipRadius = theme.Number(ChartTokens.RadiusTooltip),
            BarRadius = theme.Number(ChartTokens.RadiusBar),
            AxisText = theme.Number(ChartTokens.TextAxis),
            TooltipText = theme.Number(ChartTokens.TextTooltip),
            BarWidth = theme.Number(ChartTokens.RatioBarWidth),
            EmptyBarRatio = theme.Number(ChartTokens.RatioEmptyBar),
            FlingDrag = theme.Number(ChartTokens.RatioFlingDrag),
            ZoomStep = theme.Number(ChartTokens.RatioZoomStep),
            AreaOpacity = theme.Number(ChartTokens.OpacityArea),
            FlingStop = theme.Number(ChartTokens.SpeedFlingStop),
            ZoomMotion = theme.Number(ChartTokens.MotionZoom),
            YIntervals = (int)theme.Number(ChartTokens.CountYIntervals),
        };
    }
}
