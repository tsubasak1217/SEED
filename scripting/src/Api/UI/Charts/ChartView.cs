using System;
using System.Collections.Generic;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  ChartView.cs — グラフの共通の土台（LineChart・BarChart。W2-8。docs/ui_charts.md）
//
//  【プレハブ】（templates/ui/prefabs/line_chart.actor・bar_chart.actor）
//      Chart（Sprite = グラフ全体の大きさ〈背景。透明でもよい〉・CanvasGesture〈タップ・長押し・横のドラッグ・フリック・ピンチ〉・このスクリプト）
//      ├─ Plot（Sprite〈透明〉= 描く面・CanvasClip）
//      │   ├─ Ink（空のノード。SEED.Draw の座標空間＝面の左上が原点。Plot の切り抜きで切れる）
//      │   └─ Labels（面の中の文字: 基準線の「平均 7:12」）
//      ├─ XLabels（Sprite〈透明〉・CanvasClip。横軸の文字〈パンで動く〉を左右の端で切る）
//      ├─ YLabels（縦軸の文字）
//      ├─ Empty（Text。データが無いときの「まだ記録はありません」）
//      ├─ Tooltip（Sprite〈角丸〉）└─ Text（吹き出し）
//      └─ ZoomIn・ZoomOut（任意。SEED.UI.Button。± で拡大縮小）
//  大きさはグラフのノードの Sprite の幅・高さ（dp のキャンバスでは dp）。子の位置と大きさはこのスクリプトが決める。
//
//  【描き方】線・面・点・棒・格子線は SEED.Draw（イミディエイトモード）で毎フレーム積む（W2-10a の on_demand で止まっている間は
//  フレームが回らないので積まない）。見た目の拡張（W2-8）の「画面の 1 画素のアンチエイリアス」を使う（dp のキャンバスでも縁がにじまない）。
//  データ・見える範囲・大きさ・テーマが変わったフレームだけ、位置の計算（Rebuild）と文字のノードの書き換えを行う。
//  【操作】（W2-2 のジェスチャー）タップ・長押し → 最寄りの点・棒を選んで吹き出し。横のドラッグ → パン、払う → 慣性、
//  2 本指のピンチ → 拡大縮小（1〜MaxZoom 倍）、ZoomIn・ZoomOut のボタン → 真ん中を中心に拡大縮小。端で止まる。
//  パンとズームができない（Interactive = false か倍率 1）ときはドラッグを受けない（縦の一覧・横のページ送りへ指を渡す）。
//
//  派生（LineChart・BarChart）は「データの範囲」「面の範囲」「位置の計算」「描く」「選ぶ」「吹き出しの文字」を決める。
// ============================================================

/// <summary>グラフの共通の土台（LineChart・BarChart）。</summary>
public abstract class ChartView : UiWidget
{
    // ── 子の名前（プレハブ）──────────────────────────────────
    /// <summary>描く面。</summary>
    private const string PlotChild = "Plot";
    /// <summary>SEED.Draw の座標空間。</summary>
    private const string InkChild = "Plot/Ink";
    /// <summary>面の中の文字の親。</summary>
    private const string PlotLabelsChild = "Plot/Labels";
    /// <summary>横軸の文字の親（切り抜き）。</summary>
    private const string XLabelsChild = "XLabels";
    /// <summary>縦軸の文字の親。</summary>
    private const string YLabelsChild = "YLabels";
    /// <summary>空のときの文字。</summary>
    private const string EmptyChild = "Empty";
    /// <summary>吹き出し。</summary>
    private const string TooltipChild = "Tooltip";
    /// <summary>吹き出しの文字。</summary>
    private const string TooltipTextChild = "Tooltip/Text";
    /// <summary>拡大のボタン（任意）。</summary>
    private const string ZoomInChild = "ZoomIn";
    /// <summary>縮小のボタン（任意）。</summary>
    private const string ZoomOutChild = "ZoomOut";

    /// <summary>ログの接頭辞（ギャラリー・検査がログで確かめる）。</summary>
    protected const string LogPrefix = "[UI] chart:";
    /// <summary>文字の揃え（Text.Align）。</summary>
    protected const string AlignLeft = "left", AlignCenter = "center", AlignRight = "right";
    /// <summary>半分。</summary>
    protected const float Half = 0.5f;
    /// <summary>吹き出し・選んだ印を面の図形より手前に出すレイヤーの足し分。</summary>
    private const int OverlayLayerOffset = 1;
    /// <summary>準備（子・文字のノード）を待つフレームの上限（届かなくても描き続けを頼み続けない安全弁）。</summary>
    private const int MaxPendingFrames = 60;
    /// <summary>縦の列の軸（横の棒）の文字の最小の間隔（文字の大きさに対する割合＝1 行ぶん）。</summary>
    private const float CategoryRowEm = 1.6f;
    /// <summary>テーマの ± の倍率が 1 以下（壊れた値）のときの ± の倍率。</summary>
    private const double FallbackZoomStep = 2.0;
    /// <summary>慣性が端で止められたとみなす位置のずれ（見える幅に対する割合）。</summary>
    private const double FlingBlockedTolerance = 1e-9;
    /// <summary>± のボタンの上限・下限の比べる許容量（倍率）。</summary>
    private const double ZoomLimitEpsilon = 1e-6;

    // ── 設定（インスペクタ）───────────────────────────────────
    /// <summary>パンとズームを受ける（false = 30 日の島のような固定のグラフ）。</summary>
    [SerializeField(Label = "パンとズーム")]
    public bool Interactive = true;
    /// <summary>倍率の上限（Wake or Pay は 6）。</summary>
    [SerializeField(Label = "最大の倍率")]
    public float MaxZoom = (float)ChartViewport.DefaultMaxZoom;
    /// <summary>タップ・長押しで吹き出しを出す。</summary>
    [SerializeField(Label = "吹き出し")]
    public bool ShowTooltip = true;
    /// <summary>X（横・列）の値の書式。</summary>
    [SerializeField(Label = "X の書式")]
    public ChartValueFormat XFormat = ChartValueFormat.Date;
    /// <summary>Y（値）の書式。</summary>
    [SerializeField(Label = "値の書式")]
    public ChartValueFormat YFormat = ChartValueFormat.Number;
    /// <summary>X の刻みの候補（"1,7,30"。空 = 書式の既定）。</summary>
    [SerializeField(Label = "X の刻みの候補")]
    public string XSteps = "";
    /// <summary>値の刻みの候補（空 = 書式の既定）。</summary>
    [SerializeField(Label = "値の刻みの候補")]
    public string YSteps = "";
    /// <summary>縦軸の文字を出す。</summary>
    [SerializeField(Label = "縦軸の文字")]
    public bool ShowYAxis = true;
    /// <summary>横軸の文字を出す。</summary>
    [SerializeField(Label = "横軸の文字")]
    public bool ShowXAxis = true;
    /// <summary>格子線（値の目盛りの線）を出す。</summary>
    [SerializeField(Label = "格子線")]
    public bool ShowGrid = true;
    /// <summary>データが無いときの文字。</summary>
    [SerializeField(Label = "空のときの文字")]
    public string EmptyText = "まだ記録はありません";
    /// <summary>目盛りの文字のプレハブ。</summary>
    [SerializeField(Label = "文字のプレハブ")]
    public string LabelPrefab = "assets://ui/prefabs/chart_label.actor";

    // ── スクリプトからの設定 ─────────────────────────────────
    /// <summary>X の目盛りの文字を独自に作る（値, 刻み）→ 文字。null = 書式の既定。</summary>
    public Func<double, double, string>? XLabelFormatter { get; set; }
    /// <summary>値の目盛りの文字を独自に作る（値, 刻み）→ 文字。</summary>
    public Func<double, double, string>? YLabelFormatter { get; set; }
    /// <summary>値の範囲の自動の決め方（null = 書式の既定: 時刻は上下 30 分・最小 2 時間・0〜24 時）。</summary>
    public AutoRangeOptions? ValueRangeOptions { get; set; }
    /// <summary>値の範囲を固定する（null = 自動）。</summary>
    public ChartRange? FixedValueRange { get; set; }
    /// <summary>X の全体の範囲を固定する（null = データの最初〜最後。30 日の島は 30 日前〜今日）。</summary>
    public ChartRange? FixedXRange { get; set; }

    /// <summary>見える範囲が変わった（パン・ズーム・データ）。</summary>
    public event Action<ChartView>? ViewChanged;

    /// <summary>見える範囲（パンとズーム）の計算。</summary>
    public ChartViewport Viewport { get; } = new();

    /// <summary>位置の計算をやり直した回数（計測用）。</summary>
    public int RebuildCount { get; private set; }

    /// <summary>最後のフレームに積んだ SEED.Draw の図形の数（計測用）。</summary>
    public int LastDrawCount { get; protected set; }

    /// <summary>最後の位置の計算・描画に掛かった時間（ミリ秒。計測用）。</summary>
    public double LastRebuildMs { get; private set; }
    public double LastPaintMs { get; private set; }

    /// <summary>指でパンしている。</summary>
    public bool IsDragging { get; private set; }

    /// <summary>慣性・± の動きの途中。</summary>
    public bool IsAnimating => _fling.HasValue || _zoomTween.IsRunning;

    // ── 派生に見せる状態 ──────────────────────────────────────
    /// <summary>テーマから読んだ見た目の値。</summary>
    protected ChartLook Look;
    /// <summary>描く面（グラフのノードの単位）。</summary>
    protected ChartRect PlotRect { get; private set; }
    /// <summary>値 ⇔ 描く位置（面の左上が原点）。</summary>
    protected ChartMapping Map { get; private set; }
    /// <summary>横・縦の軸の目盛り（面の横と縦。横の棒では横が値・縦が列）。</summary>
    protected readonly ChartAxis HorizontalAxis = new(), VerticalAxis = new();
    /// <summary>SEED.Draw の座標空間（無ければ描かない）。</summary>
    protected CanvasTransform? Ink { get; private set; }
    /// <summary>面の図形のレイヤー（グラフの Sprite のレイヤー）。</summary>
    protected int BaseLayer { get; private set; }
    /// <summary>吹き出し・選んだ印のレイヤー。</summary>
    protected int OverlayLayer => BaseLayer + OverlayLayerOffset;
    /// <summary>描き方（画面の 1 画素のアンチエイリアス）。</summary>
    protected static DrawStyle Crisp => DrawStyle.Crisp;

    // ── 内部の状態 ────────────────────────────────────────────
    private bool _dataDirty = true, _viewDirty = true, _layoutDirty = true, _lookDirty = true;
    private bool _firstData = true;
    private float _width = -1f, _height = -1f;
    private GameObject _plotNode, _xLabelsNode, _yLabelsNode, _plotLabelsNode, _emptyNode, _tooltipNode;
    private ChartLabelPool? _xLabels, _yLabels, _plotLabels;
    private CanvasGesture? _gesture;
    private Button? _zoomIn, _zoomOut;
    private int _registryVersion = -1;
    private int _pendingFrames;
    // パン・ズームの動き
    private ChartFling? _fling;
    private float _flingTime;
    private UiTween _zoomTween;
    /// <summary>± の動きの行き先の倍率（動きの終わりにちょうどこの値へ置く。float の往復で 1.0000001 倍にしない）。</summary>
    private double _zoomTarget = ChartViewport.DefaultMinZoom;
    private double _zoomAnchorValue, _zoomAnchorFraction;
    private double _pinchZoom0, _pinchAnchor;
    private float _dragLast;
    // 書いた値の覚え（同じなら FFI で書かない）
    private bool? _lastDragFlag, _lastPinchFlag;
    private GestureDragAxis? _lastDragAxis;
    private string _lastEmpty = "\0";
    private Vector2 _lastTooltipPos = new(float.NaN, float.NaN);
    private string _lastTooltipText = "\0";
    /// <summary>吹き出しを見せているか（null = まだ書いていない。プレハブの初めの値に依らず最初に必ず書く）。</summary>
    private bool? _tooltipShown;
    private readonly List<double> _labelScratch = new();

    // ── 派生が決めること ──────────────────────────────────────
    /// <summary>データがあるか（無ければ空の文字だけを出す）。</summary>
    protected abstract bool HasData { get; }
    /// <summary>列（X）の軸が縦か（横の棒）。</summary>
    protected virtual bool CategoryIsVertical => false;
    /// <summary>X の全体の範囲（データの最初〜最後。棒は列の幅の半分ずつ外）。</summary>
    protected abstract ChartRange DataXRange();
    /// <summary>X の目盛りの起点（日付の軸はデータの最初の日）。</summary>
    protected abstract double XOrigin();
    /// <summary>値の全体の範囲（自動・固定。見える範囲が動いても変えない＝パンで縦がぶれない）。</summary>
    protected abstract ChartRange ValueRange();
    /// <summary>位置の計算（見える範囲・大きさ・データ・テーマが変わったときだけ）。</summary>
    protected abstract void RebuildGeometry(in ChartMapping map);
    /// <summary>データを描く（毎フレーム。格子線の後・選んだ印の前）。</summary>
    protected abstract void PaintData(CanvasTransform ink, in ChartMapping map);
    /// <summary>選んだ印を描く（毎フレーム）。</summary>
    protected virtual void PaintSelection(CanvasTransform ink, in ChartMapping map) { }
    /// <summary>面の中の文字（基準線など）を置く（位置の計算のとき）。</summary>
    protected virtual void PlacePlotLabels(ChartLabelPool pool, in ChartMapping map) { }
    /// <summary>面の中の位置（面の左上が原点）で選ぶ。変わったら true。</summary>
    protected abstract bool SelectAt(Vector2 plotLocal);
    /// <summary>選びを外す。変わったら true。</summary>
    protected abstract bool ClearSelectionCore();
    /// <summary>吹き出しを出す点（面の左上が原点）と文字。出さないなら false。</summary>
    protected abstract bool TooltipAnchor(out Vector2 plotLocal, out string text);

    // ── 公開の操作 ────────────────────────────────────────────

    /// <summary>データが変わったことを知らせる（派生の SetData から）。次のフレームで作り直す。</summary>
    protected void MarkDataChanged()
    {
        _dataDirty = true;
        Redraw.Request();
    }

    /// <summary>見た目の設定（書式・刻みの候補・範囲の決め方など）を変えたら呼ぶ。</summary>
    public void MarkDirty()
    {
        _dataDirty = true;
        _lookDirty = true;
        Redraw.Request();
    }

    /// <summary>選びを外す（吹き出しを隠す）。</summary>
    public void ClearSelection()
    {
        if (ClearSelectionCore()) Invalidate();
    }

    /// <summary>拡大する（± のボタンと同じ。真ん中を中心に ratio.chart_zoom_step 倍）。</summary>
    public void ZoomIn(bool animate = true) => ZoomBy(ZoomStepFactor(), animate);

    /// <summary>縮小する。</summary>
    public void ZoomOut(bool animate = true) => ZoomBy(1.0 / ZoomStepFactor(), animate);

    /// <summary>± 1 回の倍率（テーマの ratio.chart_zoom_step。1 以下なら既定の 2）。</summary>
    private double ZoomStepFactor() => Look.ZoomStep > 1f ? Look.ZoomStep : FallbackZoomStep;

    /// <summary>倍率を <paramref name="factor"/> 倍にする（真ん中を中心。上下限で止まる）。</summary>
    public void ZoomBy(double factor, bool animate = true)
    {
        if (!(factor > 0) || !double.IsFinite(factor)) return;
        StopMotion();
        double target = Math.Clamp(Viewport.Zoom * factor, Viewport.MinZoom, Viewport.MaxZoom);
        _zoomAnchorFraction = Half;
        _zoomAnchorValue = Viewport.Start + Viewport.VisibleSpan * Half;
        _zoomTarget = target;
        if (animate && Look.ZoomMotion > 0f)
        {
            _zoomTween = UiTween.At((float)Math.Log(Viewport.Zoom));
            _zoomTween.Retarget((float)Math.Log(target), Look.ZoomMotion);
        }
        else
        {
            Viewport.ApplyZoom(target, _zoomAnchorValue, _zoomAnchorFraction);
        }
        Invalidate();
    }

    /// <summary>X の範囲をちょうど見せる（倍率の上下限で収める）。</summary>
    public void ShowRange(ChartRange range)
    {
        StopMotion();
        EnsureViewport();
        Viewport.Show(range);
        Invalidate();
    }

    /// <summary>右端（最新）を見せる（倍率はそのまま）。</summary>
    public void ScrollToEnd()
    {
        StopMotion();
        EnsureViewport();
        // 全体の右端（PanTo が端へ収める）
        Viewport.PanTo(Viewport.Full.Max);
        Invalidate();
    }

    // ── 部品の土台 ────────────────────────────────────────────

    /// <inheritdoc />
    protected override void OnWidgetStart()
    {
        FindNodes();
        Viewport.SetZoomLimits(ChartViewport.DefaultMinZoom, MaxZoom);
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        Look = ChartLook.From(Theme);
        _lookDirty = true;
        // 空の文字・吹き出しは「前に書いた値と同じなら書かない」ので、テーマが替わったら書き直させる（色・文字の大きさ）
        _lastEmpty = "\0";
        _lastTooltipText = "\0";
    }

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt)
    {
        if (_registryVersion != UiRegistry.Version) BindZoomButtons();
        ReadSize();
        StepMotion(dt);
        bool labelsPending = (_xLabels?.Pending ?? false) || (_yLabels?.Pending ?? false) || (_plotLabels?.Pending ?? false);
        if (_dataDirty || _viewDirty || _layoutDirty || _lookDirty || labelsPending)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Rebuild();
            LastRebuildMs = sw.Elapsed.TotalMilliseconds;
            // 文字のノードを作っている途中なら、できるまで描き直しを頼む（上限つき）
            if (labelsPending && _pendingFrames++ < MaxPendingFrames) Redraw.Request();
            else if (!labelsPending) _pendingFrames = 0;
        }
        var paint = System.Diagnostics.Stopwatch.StartNew();
        Paint();
        LastPaintMs = paint.Elapsed.TotalMilliseconds;
    }

    // ── ジェスチャー（W2-2。docs/input_gestures.md）──────────────────

    /// <inheritdoc />
    public override void OnGestureTap(GestureEvent e) => SelectAtLocal(e.LocalPosition);

    /// <inheritdoc />
    public override void OnGestureLongPress(GestureEvent e) => SelectAtLocal(e.LocalPosition);

    /// <inheritdoc />
    public override void OnGestureDragStart(GestureEvent e)
    {
        if (!CanPanNow()) return;
        StopMotion();
        IsDragging = true;
        _dragLast = Along(e.LocalPosition);
        // slop（8 dp）の分も動かす（指の下の値が指に付いてくる）
        PanAlong(Along(e.DeltaDp));
    }

    /// <inheritdoc />
    public override void OnGestureDragUpdate(GestureEvent e)
    {
        if (!IsDragging) return;
        float now = Along(e.LocalPosition);
        PanAlong(now - _dragLast);
        _dragLast = now;
    }

    /// <inheritdoc />
    public override void OnGestureDragEnd(GestureEvent e) => IsDragging = false;

    /// <inheritdoc />
    public override void OnGestureFling(GestureEvent e)
    {
        if (!CanPanNow()) return;
        float length = CategoryLength();
        if (!(length > 0)) return;
        // 指の速度（dp/秒）→ 値の速度（指を右へ払うと前の値が見える＝左端が減る）
        double velocity = -Along(e.VelocityDp) * Viewport.VisibleSpan / length;
        double stop = Look.FlingStop * Viewport.VisibleSpan / length;
        _fling = new ChartFling(Viewport.Start, velocity, Look.FlingDrag, stop);
        _flingTime = 0f;
        Redraw.Request();
    }

    /// <inheritdoc />
    public override void OnGesturePinchStart(GestureEvent e)
    {
        if (!Interactive) return;
        StopMotion();
        IsDragging = false;
        _pinchZoom0 = Viewport.Zoom;
        _pinchAnchor = ValueAtAlong(Along(e.LocalPosition) - AlongPlotOrigin());
        Debug.Log($"{LogPrefix} pinch start zoom={Viewport.Zoom:0.###} anchor={_pinchAnchor:0.###}");
    }

    /// <inheritdoc />
    public override void OnGesturePinchUpdate(GestureEvent e)
    {
        if (!Interactive) return;
        float length = CategoryLength();
        if (!(length > 0)) return;
        double fraction = (Along(e.LocalPosition) - AlongPlotOrigin()) / length;
        if (Viewport.ApplyZoom(_pinchZoom0 * e.Scale, _pinchAnchor, fraction)) Invalidate();
    }

    /// <inheritdoc />
    public override void OnGesturePinchEnd(GestureEvent e)
    {
        if (Interactive) Debug.Log($"{LogPrefix} pinch end zoom={Viewport.Zoom:0.###}");
    }

    // ── 内部 ──────────────────────────────────────────────────

    /// <summary>作り直しと描き直しを頼む（見える範囲・選びが変わった）。</summary>
    protected void Invalidate()
    {
        _viewDirty = true;
        Redraw.Request();
    }

    /// <summary>列の軸の成分（横のグラフは x・横の棒は y）。</summary>
    private float Along(Vector2 v) => CategoryIsVertical ? v.y : v.x;

    /// <summary>面の左上の列の軸の成分（グラフのノードの単位）。</summary>
    private float AlongPlotOrigin() => CategoryIsVertical ? PlotRect.Y : PlotRect.X;

    /// <summary>面の列の軸の長さ。</summary>
    private float CategoryLength() => CategoryIsVertical ? PlotRect.Height : PlotRect.Width;

    /// <summary>面の中の列の軸の位置 → X の値。</summary>
    private double ValueAtAlong(float alongInPlot)
    {
        float length = CategoryLength();
        return Viewport.Start + (length > 0 ? alongInPlot / length : 0) * Viewport.VisibleSpan;
    }

    /// <summary>今パンできるか（受ける設定で、全体より狭く見ている）。</summary>
    private bool CanPanNow() => Interactive && Viewport.CanPan;

    /// <summary>指の移動（列の軸・グラフのノードの単位）でパンする。</summary>
    private void PanAlong(float delta)
    {
        // 横の棒は上から下へ列が並ぶので、指を下へ動かすと前の列（上）が見える＝同じ向き
        if (Viewport.PanByLocal(delta, CategoryLength())) Invalidate();
    }

    /// <summary>慣性と ± の動きを止める。</summary>
    private void StopMotion()
    {
        _fling = null;
        _zoomTween.Jump(_zoomTween.Value);
    }

    /// <summary>慣性・± の動きを 1 フレーム進める。</summary>
    private void StepMotion(float dt)
    {
        if (_fling is { } fling)
        {
            _flingTime += dt;
            double target = fling.PositionAt(_flingTime);
            Viewport.PanTo(target);
            // 端で止まった（目標まで動けない）・止まる時刻を過ぎた → 終わり
            bool blocked = Math.Abs(Viewport.Start - target) > Viewport.VisibleSpan * FlingBlockedTolerance;
            if (blocked || fling.IsDone(_flingTime)) _fling = null;
            Invalidate();
        }
        if (_zoomTween.IsRunning)
        {
            _zoomTween.Advance(dt);
            // 途中は対数で補間した倍率（見た目の速さが倍率に依らない）、終わりはちょうど行き先の倍率
            double zoom = _zoomTween.IsRunning ? Math.Exp(_zoomTween.Value) : _zoomTarget;
            Viewport.ApplyZoom(zoom, _zoomAnchorValue, _zoomAnchorFraction);
            Invalidate();
        }
    }

    /// <summary>グラフのノード（グラフの単位）の位置で選ぶ（タップ・長押し）。</summary>
    private void SelectAtLocal(Vector2 local)
    {
        if (!ShowTooltip || !HasData) return;
        var inPlot = new Vector2(local.x - PlotRect.X, local.y - PlotRect.Y);
        bool changed = SelectAt(inPlot);
        if (changed) Invalidate();
    }

    /// <summary>子のノードを引く（プレハブの名前）。</summary>
    private void FindNodes()
    {
        _plotNode = gameObject.FindChild(PlotChild);
        _xLabelsNode = gameObject.FindChild(XLabelsChild);
        _yLabelsNode = gameObject.FindChild(YLabelsChild);
        _plotLabelsNode = gameObject.FindChild(PlotLabelsChild);
        _emptyNode = gameObject.FindChild(EmptyChild);
        _tooltipNode = gameObject.FindChild(TooltipChild);
        Ink = gameObject.FindChild(InkChild).GetComponent<CanvasTransform>();
        _gesture = gameObject.GetComponent<CanvasGesture>();
        if (_xLabelsNode.IsValid) _xLabels = new ChartLabelPool(_xLabelsNode, LabelPrefab);
        if (_yLabelsNode.IsValid) _yLabels = new ChartLabelPool(_yLabelsNode, LabelPrefab);
        if (_plotLabelsNode.IsValid) _plotLabels = new ChartLabelPool(_plotLabelsNode, LabelPrefab);
        if (Ink is null) Debug.LogWarning($"{LogPrefix} {gameObject.Name}: 子の {InkChild} が無いので描けません");
    }

    /// <summary>± のボタンを引いてつなぐ（登録簿が変わったときだけ）。</summary>
    private void BindZoomButtons()
    {
        _registryVersion = UiRegistry.Version;
        var zoomIn = Of<Button>(gameObject.FindChild(ZoomInChild));
        var zoomOut = Of<Button>(gameObject.FindChild(ZoomOutChild));
        if (!ReferenceEquals(zoomIn, _zoomIn))
        {
            if (_zoomIn != null) _zoomIn.Clicked -= OnZoomInClicked;
            _zoomIn = zoomIn;
            if (_zoomIn != null) _zoomIn.Clicked += OnZoomInClicked;
        }
        if (!ReferenceEquals(zoomOut, _zoomOut))
        {
            if (_zoomOut != null) _zoomOut.Clicked -= OnZoomOutClicked;
            _zoomOut = zoomOut;
            if (_zoomOut != null) _zoomOut.Clicked += OnZoomOutClicked;
        }
        UpdateZoomButtons();
    }

    /// <summary>拡大のボタン。</summary>
    private void OnZoomInClicked(Button _)
    {
        ZoomIn();
        Debug.Log($"{LogPrefix} zoom in → {Viewport.Zoom:0.###}（動きの行き先 {_zoomTarget:0.###}）");
    }

    /// <summary>縮小のボタン。</summary>
    private void OnZoomOutClicked(Button _)
    {
        ZoomOut();
        Debug.Log($"{LogPrefix} zoom out → {Viewport.Zoom:0.###}（動きの行き先 {_zoomTarget:0.###}）");
    }

    /// <summary>± のボタンの押せる・押せない（上限・下限・パンとズームを受けない）。</summary>
    private void UpdateZoomButtons()
    {
        double target = _zoomTween.IsRunning ? _zoomTarget : Viewport.Zoom;
        _zoomIn?.SetInteractable(Interactive && target < Viewport.MaxZoom - ZoomLimitEpsilon);
        _zoomOut?.SetInteractable(Interactive && target > Viewport.MinZoom + ZoomLimitEpsilon);
    }

    /// <summary>グラフの大きさ（ノードの Sprite）を読む。変わったら割り付けし直す。</summary>
    private void ReadSize()
    {
        var sprite = SpriteOf();
        if (sprite is null) return;
        float w = sprite.Value.Width, h = sprite.Value.Height;
        int layer = sprite.Value.Layer;
        if (w != _width || h != _height || layer != BaseLayer)
        {
            _width = w;
            _height = h;
            BaseLayer = layer;
            _layoutDirty = true;
        }
    }

    /// <summary>全体の範囲を今のデータで決め直す（はじめは倍率 1、以後は右端に付く）。</summary>
    private void EnsureViewport()
    {
        if (!_dataDirty) return;
        var full = FixedXRange ?? DataXRange();
        Viewport.SetZoomLimits(ChartViewport.DefaultMinZoom, MaxZoom);
        Viewport.SetFull(full, _firstData ? ViewportAnchor.Reset : ViewportAnchor.StickToEnd);
        if (HasData) _firstData = false;
    }

    /// <summary>位置の計算と文字の置き直し（変わったフレームだけ）。</summary>
    private void Rebuild()
    {
        RebuildCount++;
        EnsureViewport();
        bool look = _lookDirty;
        // 枠の割り付け
        var spec = new ChartFrameSpec
        {
            YAxisWidth = ShowYAxis ? Look.YAxis : Look.PlotPad,
            XAxisHeight = ShowXAxis ? Look.XAxis : Look.PlotPad,
            PadTop = Look.PlotPad,
            PadRight = Look.PlotPad,
        };
        PlotRect = ChartLayout.Plot(Math.Max(0f, _width), Math.Max(0f, _height), spec);
        if (_layoutDirty || look) PlaceFrameNodes();
        // 面の範囲（横の棒は横が値・縦が列〈上から下〉）
        var visible = Viewport.Visible;
        var values = ValueRange();
        var hFormat = CategoryIsVertical ? YFormat : XFormat;
        var vFormat = CategoryIsVertical ? XFormat : YFormat;
        HorizontalAxis.Format = hFormat;
        VerticalAxis.Format = vFormat;
        HorizontalAxis.Steps = ChartAxis.ParseSteps(CategoryIsVertical ? YSteps : XSteps);
        VerticalAxis.Steps = ChartAxis.ParseSteps(CategoryIsVertical ? XSteps : YSteps);
        HorizontalAxis.Formatter = CategoryIsVertical ? YLabelFormatter : XLabelFormatter;
        VerticalAxis.Formatter = CategoryIsVertical ? XLabelFormatter : YLabelFormatter;
        HorizontalAxis.Origin = CategoryIsVertical ? 0 : XOrigin();
        VerticalAxis.Origin = CategoryIsVertical ? XOrigin() : 0;
        int hIntervals = ChartTicks.IntervalsForLength(PlotRect.Width, Look.XLabelSpacing);
        // 縦の値の軸は区間の数の上限（count.chart_y_intervals）と文字の間隔、縦の列の軸（横の棒）は 1 行ぶんの間隔まで詰める
        int vIntervals = CategoryIsVertical
            ? ChartTicks.IntervalsForLength(PlotRect.Height, Look.AxisText * CategoryRowEm)
            : Math.Max(1, Math.Min(Look.YIntervals > 0 ? Look.YIntervals : int.MaxValue,
                ChartTicks.IntervalsForLength(PlotRect.Height, Look.YLabelSpacing)));
        var hRange = CategoryIsVertical ? values : visible;
        var vRange = CategoryIsVertical ? visible : values;
        HorizontalAxis.Compute(hRange, hIntervals);
        VerticalAxis.Compute(vRange, vIntervals);
        Map = new ChartMapping(hRange, vRange, PlotRect.Width, PlotRect.Height, invertY: CategoryIsVertical);
        // 位置の計算（派生）
        RebuildGeometry(Map);
        // 文字
        PlaceAxisLabels();
        PlaceEmpty();
        UpdateGestureFlags();
        UpdateZoomButtons();
        _dataDirty = _viewDirty = _layoutDirty = _lookDirty = false;
        ViewChanged?.Invoke(this);
    }

    /// <summary>子のノード（面・文字の親・吹き出し）の位置と大きさを決める。</summary>
    private void PlaceFrameNodes()
    {
        var plot = PlotRect;
        SetRect(_plotNode, new Vector2(plot.X, plot.Y), new Vector2(plot.Width, plot.Height));
        // 横軸の文字は面の下の行。左右は枠いっぱい（端の文字が半分に切れないよう、左の縦軸の列の下の空きまで使う）
        SetRect(_xLabelsNode, new Vector2(0f, plot.Bottom), new Vector2(Math.Max(0f, _width), Math.Max(0f, _height - plot.Bottom)));
        if (_yLabelsNode.IsValid && _yLabelsNode.GetComponent<CanvasTransform>() is { } yct) yct.Position = Vector2.Zero;
        if (Ink is { } ink) ink.Position = Vector2.Zero;
    }

    /// <summary>ノードの左上と大きさ（Sprite の大きさ）を置く。</summary>
    private static void SetRect(GameObject node, Vector2 position, Vector2 size)
    {
        if (!node.IsValid) return;
        if (node.GetComponent<CanvasTransform>() is { } ct) ct.Position = position;
        if (node.GetComponent<Sprite>() is { } sp) sp.Size = size;
    }

    /// <summary>横軸・縦軸・面の中の文字を置き直す。</summary>
    private void PlaceAxisLabels()
    {
        bool show = HasData;
        float fontSize = Look.AxisText;
        float rowHeight = Math.Max(fontSize, Look.XAxis - Look.LabelGap);
        // 縦軸（面の左の列。右揃え）
        if (_yLabels != null)
        {
            _yLabels.Begin();
            if (show && ShowYAxis)
            {
                float boxW = Math.Max(0f, PlotRect.X - Look.LabelGap);
                foreach (var v in VerticalAxis.Values)
                {
                    float y = PlotRect.Y + Map.YToLocal(v);
                    if (y < PlotRect.Y - Half || y > PlotRect.Bottom + Half) continue;
                    _yLabels.Place(VerticalAxis.Label(v), new Vector2(0f, y - fontSize), new Vector2(boxW, fontSize * 2f),
                        AlignRight, Look.Label, fontSize, BaseLayer);
                }
            }
            _yLabels.End();
        }
        // 横軸（面の下の行。真ん中揃え。パンで動き、枠の左右の端で切れる）
        if (_xLabels != null)
        {
            _xLabels.Begin();
            if (show && ShowXAxis)
            {
                float boxW = Look.XLabelSpacing;
                foreach (var v in HorizontalAxis.Values)
                {
                    float x = PlotRect.X + Map.XToLocal(v);
                    if (x < PlotRect.X - Half || x > PlotRect.Right + Half) continue;
                    _xLabels.Place(HorizontalAxis.Label(v), new Vector2(x - boxW * Half, Look.LabelGap * Half),
                        new Vector2(boxW, rowHeight), AlignCenter, Look.Label, fontSize, BaseLayer);
                }
            }
            _xLabels.End();
        }
        // 面の中の文字（派生: 基準線の「平均 7:12」）
        if (_plotLabels != null)
        {
            _plotLabels.Begin();
            if (show) PlacePlotLabels(_plotLabels, Map);
            _plotLabels.End();
        }
    }

    /// <summary>空のときの文字（データが無いときだけ、面の真ん中）。</summary>
    private void PlaceEmpty()
    {
        if (!_emptyNode.IsValid) return;
        string text = HasData ? "" : EmptyText;
        if (text == _lastEmpty) return;
        _lastEmpty = text;
        _emptyNode.Visible = text.Length > 0;
        if (_emptyNode.GetComponent<Text>() is { } t)
        {
            t.Content = text;
            t.BoxWidth = PlotRect.Width;
            t.BoxHeight = PlotRect.Height;
            t.Align = AlignCenter;
            t.VerticalAlign = "middle";
            t.Color = Look.Label;
            t.FontSize = Theme.Number(UiTokens.TextBody);
        }
        if (_emptyNode.GetComponent<CanvasTransform>() is { } ct) ct.Position = new Vector2(PlotRect.X, PlotRect.Y);
    }

    /// <summary>ジェスチャーの旗（パンできないときはドラッグを受けない＝親のスクロールへ渡す）。</summary>
    private void UpdateGestureFlags()
    {
        if (_gesture is not { } g) return;
        bool drag = CanPanNow();
        bool pinch = Interactive && HasData;
        var axis = CategoryIsVertical ? GestureDragAxis.Vertical : GestureDragAxis.Horizontal;
        if (_lastDragFlag != drag) { g.Drag = drag; g.Fling = drag; _lastDragFlag = drag; }
        if (_lastPinchFlag != pinch) { g.Pinch = pinch; _lastPinchFlag = pinch; }
        if (_lastDragAxis != axis) { g.DragAxis = axis; _lastDragAxis = axis; }
    }

    /// <summary>毎フレーム: 格子線・データ・選んだ印を積み、吹き出しを置く。</summary>
    private void Paint()
    {
        LastDrawCount = 0;
        if (Ink is not { } ink || PlotRect.Width <= 0f || PlotRect.Height <= 0f)
        {
            PlaceTooltip();
            return;
        }
        if (HasData)
        {
            PaintGrid(ink);
            PaintData(ink, Map);
            PaintSelection(ink, Map);
        }
        PlaceTooltip();
    }

    /// <summary>格子線（値の目盛り）と軸の線（値の 0 側の端）。</summary>
    private void PaintGrid(CanvasTransform ink)
    {
        float w = PlotRect.Width, h = PlotRect.Height;
        if (ShowGrid)
        {
            if (!CategoryIsVertical)
            {
                foreach (var v in VerticalAxis.Values)
                {
                    float y = Map.YToLocal(v);
                    DrawLine(ink, new Vector2(0f, y), new Vector2(w, y), Look.Grid, Look.GridWidth);
                }
            }
            else
            {
                foreach (var v in HorizontalAxis.Values)
                {
                    float x = Map.XToLocal(v);
                    DrawLine(ink, new Vector2(x, 0f), new Vector2(x, h), Look.Grid, Look.GridWidth);
                }
            }
        }
        // 軸の線: 縦のグラフは下の縁、横の棒は左の縁（線の太さの半分だけ内側＝切り抜きで細らない）
        float inset = Look.AxisWidth * Half;
        if (!CategoryIsVertical) DrawLine(ink, new Vector2(0f, h - inset), new Vector2(w, h - inset), Look.Axis, Look.AxisWidth);
        else DrawLine(ink, new Vector2(inset, 0f), new Vector2(inset, h), Look.Axis, Look.AxisWidth);
    }

    /// <summary>直線を 1 本積む（数える）。</summary>
    protected void DrawLine(CanvasTransform ink, Vector2 a, Vector2 b, Color color, float thickness, int layerOffset = 0)
    {
        Draw.Line(a, b, color, Crisp, thickness, BaseLayer + layerOffset, ink);
        LastDrawCount++;
    }

    /// <summary>吹き出しを置く（選んだ点・棒があり、面の中に見えているときだけ）。</summary>
    private void PlaceTooltip()
    {
        if (!_tooltipNode.IsValid) return;
        Vector2 anchor = default;
        string text = "";
        // 選んだ点が見える範囲の外（パンで外へ出た）なら隠す
        bool show = ShowTooltip && HasData && Ink is not null && TooltipAnchor(out anchor, out text)
            && anchor.x >= -Half && anchor.x <= PlotRect.Width + Half && anchor.y >= -Half && anchor.y <= PlotRect.Height + Half;
        if (!show)
        {
            if (_tooltipShown != false) { _tooltipNode.Visible = false; _tooltipShown = false; }
            return;
        }
        float fontSize = Look.TooltipText;
        float pad = Look.TooltipPadding;
        var size = new Vector2(
            DialogLayout.EstimateWidth(text, fontSize) + pad * 2f,
            DialogLayout.EstimateHeight(text, fontSize, float.MaxValue) + pad * 2f);
        var local = new Vector2(PlotRect.X + anchor.x, PlotRect.Y + anchor.y);
        var pos = ChartLayout.TooltipPosition(local, size, new ChartRect(0f, 0f, _width, _height), Look.TooltipGap);
        if (_tooltipShown != true) { _tooltipNode.Visible = true; _tooltipShown = true; }
        if (pos != _lastTooltipPos || text != _lastTooltipText)
        {
            _lastTooltipPos = pos;
            _lastTooltipText = text;
            if (_tooltipNode.GetComponent<CanvasTransform>() is { } ct) ct.Position = pos;
            if (_tooltipNode.GetComponent<Sprite>() is { } sp)
            {
                sp.Size = size;
                sp.Color = Look.Tooltip;
                sp.CornerRadius = Look.TooltipRadius;
                sp.Layer = OverlayLayer;
            }
            if (gameObject.FindChild(TooltipTextChild).GetComponent<Text>() is { } t)
            {
                t.Content = text;
                t.BoxWidth = size.x;
                t.BoxHeight = size.y;
                t.Align = AlignCenter;
                t.VerticalAlign = "middle";
                t.Color = Look.OnTooltip;
                t.FontSize = fontSize;
                t.Layer = OverlayLayer;
            }
        }
    }
}
