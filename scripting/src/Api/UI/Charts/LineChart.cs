using System;
using System.Collections.Generic;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  LineChart.cs — 折れ線のグラフ（W2-8。docs/ui_charts.md §3）
//
//  【見た目】系列ごとに線（太さ size.chart_line・色 color.chart_series_N）・点の印（半径 size.chart_dot。点が詰まるほど縮めたときは
//  打たない）・線の下の塗り（任意。線の色 × opacity.chart_area から下へ透明へのグラデーション）・滑らかな曲線（任意。単調な 3 次補間で
//  点と点の間で行き過ぎない）・基準線（任意。平均の横線と「平均 7:12」の文字）。欠けた値は点を打たずに前後をつなぐ（GapMode.Connect）。
//  【Wake or Pay】起床時間の遷移（30 日の島は Interactive = false・FixedXRange = 30 日）と全期間（パンとピンチ 1〜6 倍）。
//  値は 0 時からの分（YFormat = TimeOfDay。縦軸は H:mm、上下 30 分の余白・最小 2 時間の幅・目盛りは 15/30/60/120/180/360 分から）。
//  X は日の番号（XFormat = Date。横軸は M/d を間引く）。吹き出しの文字は TooltipFormatter で M/d HH:mm などにできる。
// ============================================================

/// <summary>折れ線のグラフ。</summary>
public sealed class LineChart : ChartView
{
    /// <summary>滑らかな曲線の 1 区間の分け方の上限（点が離れていても 16 本の線分まで）。</summary>
    private const int MaxSmoothSubdivisions = 16;
    /// <summary>基準線の文字の左の余白（面の左端から）。</summary>
    private const float ReferenceLabelInset = 4f;
    /// <summary>文字の行の高さ（文字の大きさに対する割合）。</summary>
    private const float LineHeightEm = 1.4f;
    /// <summary>透明（線の下の塗りの下端）。</summary>
    private const float Transparent = 0f;
    /// <summary>線・面を描くのに要る点の数（2 点で 1 本の線分）。</summary>
    private const int MinStrokePoints = 2;
    /// <summary>X の間隔が分からないとき（点が 1 つ以下）の間隔。</summary>
    private const double DefaultPitch = 1.0;

    /// <summary>滑らかな曲線で結ぶ（単調な 3 次補間）。</summary>
    [SerializeField(Label = "滑らかな曲線")]
    public bool Smooth;
    /// <summary>線の下を塗る（上から下へ透明へのグラデーション）。</summary>
    [SerializeField(Label = "線の下の塗り")]
    public bool FillArea;
    /// <summary>点の印を打つ（点が詰まる倍率では打たない）。</summary>
    [SerializeField(Label = "点の印")]
    public bool ShowDots = true;
    /// <summary>欠けた値の扱い（つなぐ・切る）。</summary>
    [SerializeField(Label = "欠けた値")]
    public GapMode Gaps = GapMode.Connect;

    /// <summary>点を選んだ（系列, 点の添字）。選びを外したときは (−1, −1)。</summary>
    public event Action<LineChart, int, int>? PointSelected;

    /// <summary>吹き出しの文字を独自に作る（null = 「X の書式 値の書式」。例 "9/28 7:05"）。</summary>
    public Func<ChartPoint, string>? TooltipFormatter { get; set; }

    /// <summary>選んでいる系列と点の添字（無ければ (−1, −1)）。</summary>
    public (int Series, int Index) Selected => (_selSeries, _selIndex);

    /// <summary>系列の数。</summary>
    public int SeriesCount => _series.Count;

    // ── 状態 ──────────────────────────────────────────────────
    /// <summary>系列 1 本（点は X の昇順の写し）と、その描く点。</summary>
    private sealed class SeriesData
    {
        public List<ChartPoint> Points = new();
        public readonly List<Vector2> Raw = new();
        public readonly List<int> RawRuns = new();
        public readonly List<int> RawSource = new();
        public readonly List<Vector2> Curve = new();
        public readonly List<int> CurveRuns = new();
        public bool Dots;
        public float Top;
        /// <summary>隣り合う点の X の間隔の中央値（値のある点。点の印を打つかを倍率だけで決める＝パンで点が出たり消えたりしない）。</summary>
        public double Pitch = DefaultPitch;
    }

    private readonly List<SeriesData> _series = new();
    private double? _reference;
    private string _referenceLabel = "";
    private int _selSeries = -1, _selIndex = -1;
    private readonly List<double> _values = new();

    /// <summary>系列 0 を置き換える（点は X の昇順に並べ直す）。</summary>
    public void SetSeries(IReadOnlyList<ChartPoint> points) => SetSeries(0, points);

    /// <summary>系列 <paramref name="index"/> を置き換える（足りなければ足す。点は X の昇順に並べ直す）。</summary>
    public void SetSeries(int index, IReadOnlyList<ChartPoint> points)
    {
        if (index < 0) return;
        while (_series.Count <= index) _series.Add(new SeriesData());
        var copy = new List<ChartPoint>(points ?? Array.Empty<ChartPoint>());
        copy.Sort((a, b) => a.X.CompareTo(b.X));
        _series[index].Points = copy;
        _series[index].Pitch = MedianPitch(copy);
        if (_selSeries == index && _selIndex >= copy.Count) ClearSelectionCore();
        MarkDataChanged();
    }

    /// <summary>系列をすべて消す。</summary>
    public void ClearSeries()
    {
        _series.Clear();
        ClearSelectionCore();
        MarkDataChanged();
    }

    /// <summary>系列の点（X の昇順）。</summary>
    public IReadOnlyList<ChartPoint> GetSeries(int index) => index >= 0 && index < _series.Count ? _series[index].Points : Array.Empty<ChartPoint>();

    /// <summary>基準線（平均の横線など）を置く（値の範囲にも含める）。</summary>
    public void SetReferenceLine(double value, string label)
    {
        _reference = double.IsFinite(value) ? value : null;
        _referenceLabel = label ?? "";
        MarkDataChanged();
    }

    /// <summary>基準線を消す。</summary>
    public void ClearReferenceLine()
    {
        _reference = null;
        _referenceLabel = "";
        MarkDataChanged();
    }

    /// <summary>点を選ぶ（吹き出しを出す。範囲の外なら外す）。<paramref name="notify"/> なら PointSelected を出す。</summary>
    public void Select(int series, int index, bool notify = false)
    {
        bool valid = series >= 0 && series < _series.Count && index >= 0 && index < _series[series].Points.Count
            && _series[series].Points[index].HasValue;
        (_selSeries, _selIndex) = valid ? (series, index) : (-1, -1);
        if (notify) PointSelected?.Invoke(this, _selSeries, _selIndex);
        Invalidate();
    }

    // ── 土台が決めること ──────────────────────────────────────

    /// <inheritdoc />
    protected override bool HasData
    {
        get
        {
            foreach (var s in _series)
                foreach (var p in s.Points)
                    if (p.HasValue) return true;
            return false;
        }
    }

    /// <inheritdoc />
    protected override ChartRange DataXRange()
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var s in _series)
        {
            if (s.Points.Count == 0) continue;
            min = Math.Min(min, s.Points[0].X);
            max = Math.Max(max, s.Points[^1].X);
        }
        if (min > max) return new ChartRange(0, 1);
        // 1 点だけなら左右に半分ずつ（Flutter 版の singleDay と同じ）
        return max - min > 0 ? new ChartRange(min, max) : new ChartRange(min - Half, max + Half);
    }

    /// <inheritdoc />
    protected override double XOrigin()
    {
        if (FixedXRange is { } fixedRange) return Math.Ceiling(fixedRange.Min);
        double min = double.PositiveInfinity;
        foreach (var s in _series)
            if (s.Points.Count > 0) min = Math.Min(min, s.Points[0].X);
        return double.IsFinite(min) ? min : 0;
    }

    /// <summary>書式の既定の値の範囲の決め方（時刻は Wake or Pay の起床時間と同じ）。</summary>
    public static AutoRangeOptions DefaultRangeOptions(ChartValueFormat format)
    {
        const double timePadding = 30, timeMinSpan = 120, timeEmptyMin = 240, timeEmptyMax = 720, numberPaddingFraction = 0.05;
        return format switch
        {
            ChartValueFormat.TimeOfDay => new AutoRangeOptions
            {
                Padding = timePadding, MinSpan = timeMinSpan, ClampMin = 0, ClampMax = ChartFormat.MinutesPerDay,
                Empty = new ChartRange(timeEmptyMin, timeEmptyMax),
            },
            ChartValueFormat.Number => new AutoRangeOptions
            {
                PaddingFraction = numberPaddingFraction, ClampMin = double.NaN, ClampMax = double.NaN, Empty = new ChartRange(0, 1),
            },
            _ => AutoRangeOptions.Plain,
        };
    }

    /// <inheritdoc />
    protected override ChartRange ValueRange()
    {
        if (FixedValueRange is { } fixedRange) return fixedRange;
        _values.Clear();
        foreach (var s in _series)
            foreach (var p in s.Points)
                if (p.HasValue) _values.Add(p.Y!.Value);
        if (_reference is { } r) _values.Add(r);
        var range = ChartAutoRange.FromValues(_values, ValueRangeOptions ?? DefaultRangeOptions(YFormat));
        // 数の軸は切りの良い刻みへ丸める（目盛りが端に乗る）
        if (YFormat == ChartValueFormat.Number)
            range = ChartTicks.NiceBounds(range, ChartTicks.NiceStep(range.Span, Math.Max(1, Look.YIntervals)));
        return range;
    }

    /// <inheritdoc />
    protected override void RebuildGeometry(in ChartMapping map)
    {
        foreach (var s in _series)
        {
            LinePath.Build(s.Points, map, Gaps, s.Raw, s.RawRuns, s.RawSource);
            s.Curve.Clear();
            s.CurveRuns.Clear();
            s.Top = map.Height;
            if (Smooth)
            {
                for (int r = 0; r < s.RawRuns.Count; r++)
                {
                    s.CurveRuns.Add(s.Curve.Count);
                    var run = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(s.Raw)
                        .Slice(s.RawRuns[r], LinePath.RunLength(s.RawRuns, s.Raw.Count, r));
                    MonotoneCubic.Sample(run, Look.SmoothStep, MaxSmoothSubdivisions, s.Curve);
                }
            }
            foreach (var p in (Smooth ? s.Curve : s.Raw)) s.Top = MathF.Min(s.Top, p.y);
            // 点の印は、点の間隔（X の中央値 × 今の倍率の 1 単位の長さ）が最小の間隔以上のときだけ
            // （全期間を縮めると点が詰まって線が見えない。欠けた日の数に依らず倍率だけで決まる）
            int n = s.Raw.Count;
            s.Dots = ShowDots && n > 0 && (n == 1 || s.Pitch * map.XUnitLength >= Look.DotMinSpacing);
        }
    }

    /// <inheritdoc />
    protected override void PaintData(CanvasTransform ink, in ChartMapping map)
    {
        // 基準線（平均の横線）は線の下
        if (_reference is { } r)
        {
            float y = map.YToLocal(r);
            DrawLine(ink, new Vector2(0f, y), new Vector2(map.Width, y), Look.Reference, Look.ReferenceWidth);
        }
        for (int i = 0; i < _series.Count; i++)
        {
            var s = _series[i];
            var color = Look.SeriesColor(i);
            var pts = Smooth ? s.Curve : s.Raw;
            var runs = Smooth ? s.CurveRuns : s.RawRuns;
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pts);
            for (int k = 0; k < runs.Count; k++)
            {
                var run = span.Slice(runs[k], LinePath.RunLength(runs, pts.Count, k));
                PaintRun(ink, run, color, map.Height, s.Top);
            }
            if (s.Dots)
            {
                foreach (var p in s.Raw)
                {
                    Draw.Circle(p, Look.Dot, color, Crisp, BaseLayer, ink);
                    LastDrawCount++;
                }
            }
        }
    }

    /// <summary>つながった区間 1 つを描く（線の下の塗り → 線。1024 点ごとの塊に分ける）。</summary>
    private void PaintRun(CanvasTransform ink, ReadOnlySpan<Vector2> run, Color color, float baseline, float top)
    {
        int chunks = LinePath.ChunkCount(run.Length, Draw.MaxPointsPerPrimitive);
        if (FillArea && run.Length >= MinStrokePoints)
        {
            var areaTop = color.WithAlpha(color.a * Look.AreaOpacity);
            var style = Crisp.WithLinearGradient(new Vector2(0f, top), new Vector2(0f, baseline), color.WithAlpha(Transparent));
            for (int c = 0; c < chunks; c++)
            {
                var (start, length) = LinePath.Chunk(run.Length, Draw.MaxPointsPerPrimitive, c);
                Draw.Area(run.Slice(start, length), baseline, areaTop, style, BaseLayer, ink);
                LastDrawCount++;
            }
        }
        if (run.Length >= MinStrokePoints)
        {
            for (int c = 0; c < chunks; c++)
            {
                var (start, length) = LinePath.Chunk(run.Length, Draw.MaxPointsPerPrimitive, c);
                Draw.Polyline(run.Slice(start, length), false, color, Crisp, Look.Line, BaseLayer, ink);
                LastDrawCount++;
            }
        }
    }

    /// <inheritdoc />
    protected override void PaintSelection(CanvasTransform ink, in ChartMapping map)
    {
        if (!TryGetSelected(out var p)) return;
        var at = map.ToLocal(p.X, p.Y!.Value);
        if (at.x < -Half || at.x > map.Width + Half) return;
        // 縦の案内線と大きな点の印（吹き出しの根元）
        DrawLine(ink, new Vector2(at.x, 0f), new Vector2(at.x, map.Height), Look.Reference, Look.GridWidth);
        Draw.Circle(at, Look.DotSelected, Look.SeriesColor(_selSeries), Crisp, BaseLayer, ink);
        LastDrawCount++;
    }

    /// <inheritdoc />
    protected override void PlacePlotLabels(ChartLabelPool pool, in ChartMapping map)
    {
        if (_reference is not { } r || _referenceLabel.Length == 0) return;
        float size = Look.AxisText;
        float h = size * LineHeightEm;
        float y = map.YToLocal(r);
        // 線の上（入らなければ線の下）に左揃え
        float top = y - h >= 0f ? y - h : y;
        pool.Place(_referenceLabel, new Vector2(ReferenceLabelInset, top), new Vector2(map.Width, h), AlignLeft, Look.Reference, size, BaseLayer);
    }

    /// <inheritdoc />
    protected override bool SelectAt(Vector2 plotLocal)
    {
        int bestSeries = -1, bestIndex = -1;
        float bestDx = float.PositiveInfinity, bestDy = float.PositiveInfinity;
        for (int i = 0; i < _series.Count; i++)
        {
            var s = _series[i];
            int k = ChartHit.Nearest(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(s.Raw), plotLocal, Look.TouchSlop);
            if (k < 0) continue;
            float dx = MathF.Abs(s.Raw[k].x - plotLocal.x), dy = MathF.Abs(s.Raw[k].y - plotLocal.y);
            if (dx < bestDx || (dx == bestDx && dy < bestDy))
            {
                (bestSeries, bestIndex, bestDx, bestDy) = (i, s.RawSource[k], dx, dy);
            }
        }
        if (bestSeries == _selSeries && bestIndex == _selIndex) return false;
        (_selSeries, _selIndex) = (bestSeries, bestIndex);
        if (TryGetSelected(out var p)) Debug.Log($"{LogPrefix} line select series={_selSeries} index={_selIndex} x={p.X:0.###} y={p.Y:0.###} text=\"{TooltipText(p)}\"");
        else Debug.Log($"{LogPrefix} line select none");
        PointSelected?.Invoke(this, _selSeries, _selIndex);
        return true;
    }

    /// <inheritdoc />
    protected override bool ClearSelectionCore()
    {
        if (_selSeries < 0) return false;
        (_selSeries, _selIndex) = (-1, -1);
        return true;
    }

    /// <inheritdoc />
    protected override bool TooltipAnchor(out Vector2 plotLocal, out string text)
    {
        plotLocal = default;
        text = "";
        if (!TryGetSelected(out var p)) return false;
        plotLocal = Map.ToLocal(p.X, p.Y!.Value);
        text = TooltipText(p);
        return true;
    }

    /// <summary>値のある点の隣どうしの X の間隔の中央値（点が 1 つ以下なら 1）。</summary>
    private static double MedianPitch(List<ChartPoint> points)
    {
        var gaps = new List<double>();
        double prev = double.NaN;
        foreach (var p in points)
        {
            if (!p.HasValue) continue;
            if (!double.IsNaN(prev) && p.X > prev) gaps.Add(p.X - prev);
            prev = p.X;
        }
        if (gaps.Count == 0) return DefaultPitch;
        gaps.Sort();
        // 中央値（偶数個なら上の真ん中）
        return gaps[gaps.Count >> 1];
    }

    /// <summary>選んでいる点（値のあるもの）。</summary>
    private bool TryGetSelected(out ChartPoint p)
    {
        p = default;
        if (_selSeries < 0 || _selSeries >= _series.Count) return false;
        var pts = _series[_selSeries].Points;
        if (_selIndex < 0 || _selIndex >= pts.Count || !pts[_selIndex].HasValue) return false;
        p = pts[_selIndex];
        return true;
    }

    /// <summary>吹き出しの文字（独自の作り方か「X の書式 値の書式」）。</summary>
    private string TooltipText(ChartPoint p)
        => TooltipFormatter?.Invoke(p)
           ?? $"{ChartFormat.Format(XFormat, p.X, 1)} {ChartFormat.Format(YFormat, p.Y ?? 0, VerticalAxis.Step)}";
}
