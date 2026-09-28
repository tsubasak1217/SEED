using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ChartTokens.cs — グラフ（W2-8）が読むテーマのトークンの名前の一覧
//
//  UiTokens（W2-4・W2-5）・NavTokens（W2-7）と同じ考え方: 値は Theme/default_theme.json（SEEDScripting に埋め込み）にあり、
//  グラフはここの名前だけを使う（線の太さ・点の大きさ・色・目盛りの文字の大きさ・吹き出し・慣性の減速を直接書かない）。
//  足すときは default_theme.json・docs/ui_charts.md §7 の表にも足すこと。
// ============================================================

/// <summary>グラフ（W2-8）のテーマのトークンの名前。</summary>
public static class ChartTokens
{
    // ── 色 ──────────────────────────────────────────────────
    /// <summary>系列 1 の色（折れ線・棒の 1 段目。既定は主の色）。</summary>
    public const string ColorSeries1 = "color.chart_series_1";
    /// <summary>系列 2 の色（積み上げの 2 段目。Wake or Pay のカードの赤）。</summary>
    public const string ColorSeries2 = "color.chart_series_2";
    /// <summary>系列 3 の色。</summary>
    public const string ColorSeries3 = "color.chart_series_3";
    /// <summary>系列 4 の色。</summary>
    public const string ColorSeries4 = "color.chart_series_4";
    /// <summary>格子線（横の目盛りの線）。</summary>
    public const string ColorGrid = "color.chart_grid";
    /// <summary>軸の線（下の基準線）。</summary>
    public const string ColorAxis = "color.chart_axis";
    /// <summary>目盛りの文字。</summary>
    public const string ColorLabel = "color.chart_label";
    /// <summary>基準線（平均の横線）とその文字。</summary>
    public const string ColorReference = "color.chart_reference";
    /// <summary>合計 0 の棒（最低の高さの灰色）。</summary>
    public const string ColorEmptyBar = "color.chart_empty_bar";
    /// <summary>選んだ列の背景（薄い強調）。</summary>
    public const string ColorHighlight = "color.chart_highlight";
    /// <summary>吹き出しの面（W2-7 のトーストと同じ inverse surface）。</summary>
    public const string ColorTooltip = "color.inverse_surface";
    /// <summary>吹き出しの文字。</summary>
    public const string ColorOnTooltip = "color.on_inverse_surface";

    // ── 大きさ（キャンバスの単位＝dp）──────────────────────────
    /// <summary>折れ線の太さ（Flutter 版の barWidth 2）。</summary>
    public const string SizeLine = "size.chart_line";
    /// <summary>点の印の半径（Flutter 版の 2.5）。</summary>
    public const string SizeDot = "size.chart_dot";
    /// <summary>選んだ点の印の半径。</summary>
    public const string SizeDotSelected = "size.chart_dot_selected";
    /// <summary>点の印を打つ最小の間隔（点が詰まる縮めた全期間では打たない）。</summary>
    public const string SizeDotMinSpacing = "size.chart_dot_min_spacing";
    /// <summary>格子線の太さ。</summary>
    public const string SizeGrid = "size.chart_grid";
    /// <summary>軸の線の太さ。</summary>
    public const string SizeAxis = "size.chart_axis";
    /// <summary>基準線の太さ（Flutter 版の平均の横線 1.5）。</summary>
    public const string SizeReference = "size.chart_reference";
    /// <summary>左の縦軸の文字の列の幅（Flutter 版の reservedSize 46）。</summary>
    public const string SizeYAxis = "size.chart_y_axis";
    /// <summary>下の横軸の文字の行の高さ（Flutter 版の reservedSize 26）。</summary>
    public const string SizeXAxis = "size.chart_x_axis";
    /// <summary>横軸の文字の最小の間隔（これより詰まる刻みは選ばない）。</summary>
    public const string SizeXLabelSpacing = "size.chart_x_label_spacing";
    /// <summary>縦軸の文字の最小の間隔。</summary>
    public const string SizeYLabelSpacing = "size.chart_y_label_spacing";
    /// <summary>目盛りの文字と面の間。</summary>
    public const string SizeLabelGap = "size.chart_label_gap";
    /// <summary>面の上と右の余白。</summary>
    public const string SizePlotPad = "size.chart_plot_pad";
    /// <summary>吹き出しの点を選ぶ横の距離（48 dp の半分）。</summary>
    public const string SizeTouchSlop = "size.chart_touch_slop";
    /// <summary>滑らかな曲線を分ける刻み（横の間隔）。</summary>
    public const string SizeSmoothStep = "size.chart_smooth_step";
    /// <summary>棒の最小の太さ（Flutter 版の 2）。</summary>
    public const string SizeBarMin = "size.chart_bar_min";
    /// <summary>吹き出しの内側の余白。</summary>
    public const string SizeTooltipPadding = "size.chart_tooltip_padding";
    /// <summary>吹き出しと点の間。</summary>
    public const string SizeTooltipGap = "size.chart_tooltip_gap";

    // ── 角丸・文字 ──────────────────────────────────────────
    /// <summary>棒の先の角丸。</summary>
    public const string RadiusBar = "radius.chart_bar";
    /// <summary>吹き出しの角丸。</summary>
    public const string RadiusTooltip = "radius.chart_tooltip";
    /// <summary>目盛りの文字の大きさ（Flutter 版の bodySmall 11〜12）。</summary>
    public const string TextAxis = "text.chart_axis";
    /// <summary>吹き出しの文字の大きさ（Flutter 版の 12）。</summary>
    public const string TextTooltip = "text.chart_tooltip";

    // ── 割合・濃さ・動き ─────────────────────────────────────
    /// <summary>棒の太さ（列の幅に対する割合。Flutter 版の 0.7）。</summary>
    public const string RatioBarWidth = "ratio.chart_bar_width";
    /// <summary>合計 0 の棒の高さ（値の軸の幅に対する割合。Flutter 版の 0.015）。</summary>
    public const string RatioEmptyBar = "ratio.chart_empty_bar";
    /// <summary>払った後の慣性の減速（1 秒で速度が何倍になるか。Flutter の BouncingScrollSimulation と同じ 0.135）。</summary>
    public const string RatioFlingDrag = "ratio.chart_fling_drag";
    /// <summary>± のボタン 1 回の倍率。</summary>
    public const string RatioZoomStep = "ratio.chart_zoom_step";
    /// <summary>線の下の塗りの上端の濃さ（線の色に掛ける）。</summary>
    public const string OpacityArea = "opacity.chart_area";
    /// <summary>払った後の慣性が止まる速さ（dp/秒）。</summary>
    public const string SpeedFlingStop = "speed.chart_fling_stop";
    /// <summary>± のボタンの拡大縮小の動きの時間（秒）。</summary>
    public const string MotionZoom = "motion.chart_zoom";
    /// <summary>縦軸の区間の数の上限（Flutter 版の「4 区間以下」）。</summary>
    public const string CountYIntervals = "count.chart_y_intervals";

    /// <summary>系列の色のトークン（系列の番号の順。足りなければ最後の色をくり返す）。</summary>
    public static readonly string[] SeriesColors = { ColorSeries1, ColorSeries2, ColorSeries3, ColorSeries4 };

    /// <summary>グラフが読むすべてのトークン（既定のテーマが揃っているかをテストで確かめる）。</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        ColorSeries1, ColorSeries2, ColorSeries3, ColorSeries4, ColorGrid, ColorAxis, ColorLabel, ColorReference,
        ColorEmptyBar, ColorHighlight, ColorTooltip, ColorOnTooltip,
        SizeLine, SizeDot, SizeDotSelected, SizeDotMinSpacing, SizeGrid, SizeAxis, SizeReference, SizeYAxis, SizeXAxis,
        SizeXLabelSpacing, SizeYLabelSpacing, SizeLabelGap, SizePlotPad, SizeTouchSlop, SizeSmoothStep, SizeBarMin,
        SizeTooltipPadding, SizeTooltipGap,
        RadiusBar, RadiusTooltip, TextAxis, TextTooltip,
        RatioBarWidth, RatioEmptyBar, RatioFlingDrag, RatioZoomStep, OpacityArea, SpeedFlingStop, MotionZoom, CountYIntervals,
    };

    /// <summary>系列の番号の色のトークン（負は 0、足りなければ最後）。</summary>
    public static string SeriesColor(int index)
        => SeriesColors[index < 0 ? 0 : (index >= SeriesColors.Length ? SeriesColors.Length - 1 : index)];
}
