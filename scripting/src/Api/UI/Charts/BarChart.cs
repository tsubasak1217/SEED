using System;
using System.Collections.Generic;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  BarChart.cs — 棒のグラフ（縦・横・積み上げ。W2-8。docs/ui_charts.md §3）
//
//  【見た目】列ごとに 1 本の棒。値が複数なら下から積む（系列の色 color.chart_series_N。Wake or Pay はコインの上にカード）。
//  先（縦は上・横は右）の角だけを丸める（radius.chart_bar）。合計 0 の列も最低の高さ（ratio.chart_empty_bar）の灰色で出す
//  （0 の日も「日」として見える。Flutter 版 penalty_bar_chart.dart と同じ）。選んだ列は全体の高さに薄い強調（color.chart_highlight）。
//  【当たり】列の幅の中なら縦は問わない（空の高さまで当たり。細い棒でも狙わなくてよい）。タップで列を選び BarSelected を出す。
//  【パン・ズーム】列の軸に沿って（縦の棒は横・横の棒は縦）。Wake or Pay のペナルティ履歴は全期間を横スクロールとピンチ 1〜6 倍。
// ============================================================

/// <summary>棒のグラフ。</summary>
public sealed class BarChart : ChartView
{
    /// <summary>1 本の棒の塗り（輪郭の点の区間と色）。</summary>
    private readonly struct Piece
    {
        public readonly int Start, Count;
        public readonly Color Color;
        public Piece(int start, int count, Color color) { Start = start; Count = count; Color = color; }
    }

    /// <summary>値の軸の上の余白（幅に対する割合）。</summary>
    private const double TopHeadroom = 0.05;
    /// <summary>列の間隔が正でないときの間隔（X の単位）。</summary>
    private const double DefaultPitch = 1.0;

    /// <summary>棒の向き（縦・横）。</summary>
    [SerializeField(Label = "向き")]
    public BarOrientation Orientation = BarOrientation.Vertical;
    /// <summary>列の間隔（X の単位。日の番号なら 1 = 1 日）。</summary>
    [SerializeField(Label = "列の間隔")]
    public float SlotWidth = 1f;
    /// <summary>合計 0 の列も最低の高さで出す。</summary>
    [SerializeField(Label = "空の棒")]
    public bool ShowEmptyBars = true;
    /// <summary>選んだ列を薄く強調する。</summary>
    [SerializeField(Label = "選んだ列の強調")]
    public bool HighlightSelection = true;

    /// <summary>列を選んだ（添字。外したときは −1）。</summary>
    public event Action<BarChart, int>? BarSelected;

    /// <summary>吹き出しの文字を独自に作る（null = 「X の書式 合計」、積み上げは「X の書式 a + b」）。</summary>
    public Func<BarDatum, string>? TooltipFormatter { get; set; }

    /// <summary>積み上げの段の色（null = テーマの系列の色）。</summary>
    public IReadOnlyList<Color>? StackColors { get; set; }

    /// <summary>選んでいる列の添字（無ければ −1）。</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>列の数。</summary>
    public int Count => _bars.Count;

    // ── 状態 ──────────────────────────────────────────────────
    private List<BarDatum> _bars = new();
    private double[] _xs = Array.Empty<double>();
    private readonly List<Vector2> _outline = new();
    private readonly List<Vector2> _points = new();
    private readonly List<Piece> _pieces = new();
    private readonly List<(double From, double To)> _stack = new();
    private readonly List<double> _totals = new();
    private float _slotLength;

    /// <summary>列を置き換える（X の昇順に並べ直す）。選んでいた列は同じ X の列があれば保つ。</summary>
    public void SetData(IReadOnlyList<BarDatum> bars)
    {
        double? selectedX = SelectedIndex >= 0 && SelectedIndex < _bars.Count ? _bars[SelectedIndex].X : null;
        _bars = new List<BarDatum>(bars ?? Array.Empty<BarDatum>());
        _bars.Sort((a, b) => a.X.CompareTo(b.X));
        _xs = new double[_bars.Count];
        for (int i = 0; i < _bars.Count; i++) _xs[i] = _bars[i].X;
        SelectedIndex = selectedX is { } x ? Array.BinarySearch(_xs, x) : -1;
        if (SelectedIndex < 0) SelectedIndex = -1;
        MarkDataChanged();
    }

    /// <summary>列の値。</summary>
    public BarDatum Get(int index) => _bars[index];

    /// <summary>列を選ぶ（範囲の外なら外す）。<paramref name="notify"/> なら BarSelected を出す。</summary>
    public void Select(int index, bool notify = false)
    {
        SelectedIndex = index >= 0 && index < _bars.Count ? index : -1;
        if (notify) BarSelected?.Invoke(this, SelectedIndex);
        Invalidate();
    }

    /// <summary>X の値の列を選ぶ（無ければ外す）。</summary>
    public void SelectX(double x, bool notify = false) => Select(ChartHit.NearestX(_xs, x, SlotWidth * Half), notify);

    // ── 土台が決めること ──────────────────────────────────────

    /// <inheritdoc />
    protected override bool HasData => _bars.Count > 0;

    /// <inheritdoc />
    protected override bool CategoryIsVertical => Orientation == BarOrientation.Horizontal;

    /// <summary>列の間隔（正でなければ 1）。</summary>
    private double Pitch => SlotWidth > 0 ? SlotWidth : DefaultPitch;

    /// <inheritdoc />
    protected override ChartRange DataXRange()
        => _bars.Count == 0 ? new ChartRange(0, 1) : new ChartRange(_xs[0] - Pitch * Half, _xs[^1] + Pitch * Half);

    /// <inheritdoc />
    protected override double XOrigin() => _bars.Count > 0 ? _xs[0] : 0;

    /// <inheritdoc />
    protected override ChartRange ValueRange()
    {
        if (FixedValueRange is { } fixedRange) return fixedRange;
        _totals.Clear();
        foreach (var b in _bars) _totals.Add(b.Total);
        // 0 から。上に少しの余白（いちばん高い棒の先の角丸が面の上の縁に貼り付かない）を足してから切りの良い刻みへ丸める
        var options = ValueRangeOptions ?? new AutoRangeOptions
        {
            IncludeZero = true, PaddingFraction = TopHeadroom, ClampMin = double.NaN, ClampMax = double.NaN, Empty = new ChartRange(0, 1),
        };
        var range = ChartAutoRange.FromValues(_totals, options);
        // 値の軸は 0 から切りの良い刻みへ（目盛りが上の端に乗る）
        return ChartTicks.NiceBounds(range, ChartTicks.NiceStep(range.Span, Math.Max(1, Look.YIntervals)));
    }

    /// <inheritdoc />
    protected override void RebuildGeometry(in ChartMapping map)
    {
        _points.Clear();
        _pieces.Clear();
        if (_bars.Count == 0) return;
        bool horizontal = CategoryIsVertical;
        // 列の範囲（見える範囲 ± 1 列）
        var visible = horizontal ? map.Y : map.X;
        int first = Math.Max(0, LowerBound(visible.Min - Pitch) );
        int last = Math.Min(_bars.Count - 1, LowerBound(visible.Max + Pitch));
        // 列の長さ（描く空間）と棒の太さ
        float categoryLength = horizontal ? map.Height : map.Width;
        _slotLength = (float)(categoryLength * Pitch / (horizontal ? map.Y.SafeSpan : map.X.SafeSpan));
        float thickness = BarGeometry.Thickness(_slotLength, Look.BarWidth, Look.BarMin);
        var valueRange = horizontal ? map.X : map.Y;
        double floor = BarGeometry.FloorValue(valueRange, Look.EmptyBarRatio);
        for (int i = first; i <= last; i++)
        {
            var bar = _bars[i];
            float center = horizontal ? map.YToLocal(bar.X) : map.XToLocal(bar.X);
            float c0 = center - thickness * Half, c1 = center + thickness * Half;
            double total = BarGeometry.Stack(bar.Values, _stack);
            if (total <= 0)
            {
                if (ShowEmptyBars) AddPiece(map, horizontal, c0, c1, 0, floor, Look.EmptyBar, roundEnd: true);
                continue;
            }
            // 上の段（値が 0 でない最後の段）だけ先を丸める
            int topSegment = -1;
            for (int k = 0; k < _stack.Count; k++) if (_stack[k].To > _stack[k].From) topSegment = k;
            for (int k = 0; k < _stack.Count; k++)
            {
                var (from, to) = _stack[k];
                if (!(to > from)) continue;
                AddPiece(map, horizontal, c0, c1, from, to, StackColor(k), roundEnd: k == topSegment);
            }
        }
    }

    /// <summary>1 段の輪郭を積む（値の軸の from〜to、列の軸の c0〜c1）。</summary>
    private void AddPiece(in ChartMapping map, bool horizontal, float c0, float c1, double from, double to, Color color, bool roundEnd)
    {
        float radius = roundEnd ? Look.BarRadius : 0f;
        if (horizontal)
        {
            float x0 = map.XToLocal(from), x1 = map.XToLocal(to);
            BarGeometry.RoundedEndOutline(x0, c0, x1, c1, radius, BarEnd.Right, _outline);
        }
        else
        {
            float y0 = map.YToLocal(to), y1 = map.YToLocal(from);
            BarGeometry.RoundedEndOutline(c0, y0, c1, y1, radius, BarEnd.Top, _outline);
        }
        _pieces.Add(new Piece(_points.Count, _outline.Count, color));
        _points.AddRange(_outline);
    }

    /// <summary>積み上げの段の色。</summary>
    private Color StackColor(int k)
        => StackColors is { Count: > 0 } colors ? colors[Math.Min(k, colors.Count - 1)] : Look.SeriesColor(k);

    /// <summary>X が <paramref name="x"/> 以上の最初の列の添字。</summary>
    private int LowerBound(double x)
    {
        int lo = 0, hi = _xs.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_xs[mid] < x) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <inheritdoc />
    protected override void PaintData(CanvasTransform ink, in ChartMapping map)
    {
        // 選んだ列の強調（棒の後ろ・列の幅・面の全体の高さ）
        if (HighlightSelection && SelectedIndex >= 0 && SelectedIndex < _bars.Count)
        {
            bool horizontal = CategoryIsVertical;
            float center = horizontal ? map.YToLocal(_xs[SelectedIndex]) : map.XToLocal(_xs[SelectedIndex]);
            float half = _slotLength * Half;
            Span<Vector2> rect = stackalloc Vector2[4];
            if (horizontal)
            {
                rect[0] = new Vector2(0f, center - half); rect[1] = new Vector2(map.Width, center - half);
                rect[2] = new Vector2(map.Width, center + half); rect[3] = new Vector2(0f, center + half);
            }
            else
            {
                rect[0] = new Vector2(center - half, 0f); rect[1] = new Vector2(center + half, 0f);
                rect[2] = new Vector2(center + half, map.Height); rect[3] = new Vector2(center - half, map.Height);
            }
            Draw.Polygon(rect, Look.Highlight, Crisp, BaseLayer, ink);
            LastDrawCount++;
        }
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_points);
        foreach (var piece in _pieces)
        {
            Draw.Polygon(span.Slice(piece.Start, piece.Count), piece.Color, Crisp, BaseLayer, ink);
            LastDrawCount++;
        }
    }

    /// <inheritdoc />
    protected override bool SelectAt(Vector2 plotLocal)
    {
        // 列の軸の値（縦の棒は横・横の棒は縦）。縦は問わない（空の高さまで当たり）
        double x = CategoryIsVertical ? Map.LocalToY(plotLocal.y) : Map.LocalToX(plotLocal.x);
        int index = ChartHit.NearestX(_xs, x, Pitch * Half);
        // 列の外（棒の無い所）を押しても選びは変えない（Flutter 版と同じ: 列を押したときだけ動く）
        if (index < 0 || index == SelectedIndex) return false;
        SelectedIndex = index;
        Debug.Log($"{LogPrefix} bar select index={index} x={_xs[index]:0.###} total={_bars[index].Total:0.###} text=\"{TooltipText(_bars[index])}\"");
        BarSelected?.Invoke(this, index);
        return true;
    }

    /// <inheritdoc />
    protected override bool ClearSelectionCore()
    {
        if (SelectedIndex < 0) return false;
        SelectedIndex = -1;
        return true;
    }

    /// <inheritdoc />
    protected override bool TooltipAnchor(out Vector2 plotLocal, out string text)
    {
        plotLocal = default;
        text = "";
        if (SelectedIndex < 0 || SelectedIndex >= _bars.Count) return false;
        var bar = _bars[SelectedIndex];
        double top = Math.Max(bar.Total, ShowEmptyBars ? BarGeometry.FloorValue(CategoryIsVertical ? Map.X : Map.Y, Look.EmptyBarRatio) : 0);
        plotLocal = CategoryIsVertical
            ? new Vector2(Map.XToLocal(top), Map.YToLocal(bar.X))
            : new Vector2(Map.XToLocal(bar.X), Map.YToLocal(top));
        text = TooltipText(bar);
        return true;
    }

    /// <summary>吹き出しの文字（独自の作り方か「X の書式 合計」・積み上げは「X の書式 a + b」）。</summary>
    private string TooltipText(BarDatum bar)
    {
        if (TooltipFormatter != null) return TooltipFormatter(bar);
        var valueAxis = CategoryIsVertical ? HorizontalAxis : VerticalAxis;
        string x = ChartFormat.Format(XFormat, bar.X, 1);
        if (bar.Values.Count <= 1) return $"{x} {ChartFormat.Format(YFormat, bar.Total, valueAxis.Step)}";
        var parts = new string[bar.Values.Count];
        for (int k = 0; k < parts.Length; k++) parts[k] = ChartFormat.Format(YFormat, BarGeometry.Positive(bar.Values[k]), valueAxis.Step);
        return $"{x} {string.Join(" + ", parts)}";
    }
}
