// ============================================================
//  DopeSheetPanel.xaml.cs — ドープシート描画・編集コントロール
//
//  AnimClip の全トラックを行として並べ、各トラックのキーフレームを
//  ◆マーカーで表示する。時間ルーラー・プレイヘッド（再生位置）も
//  同じ Canvas 上に描画する。
//
//  【責務】
//  ・見た目の描画（ルーラー目盛・トラック行の背景・グリッド線・◆・プレイヘッド・矩形選択）
//  ・マウス操作の解釈
//      - ダブルクリック    : キー追加を要求（トラック行 / サマリー行）
//      - クリック          : キー選択（Ctrl=トグル / Shift=追加）
//      - 空白部のドラッグ  : ラバーバンド（矩形選択）
//      - ◆ドラッグ        : 選択キーをまとめてフレーム移動
//      - ルーラードラッグ  : プレイヘッドのスクラブ
//      - ホイールボタン / Alt+左ドラッグ : 水平パン
//      - ホイール          : 横スクロール（Ctrl=ズーム / Shift=縦スクロール）
//    を親パネルへイベントとして通知する
//
//  【時間軸はフレーム基準】
//  ルーラーの目盛・ラベルはクリップの fps（AnimClip.Fps）に基づくフレーム番号で、
//  プレイヘッドもキー移動も必ずフレーム境界へスナップする。
//  内部の保持値は従来どおり秒（.anim / ランタイムの単位）で、
//  フレームとの変換は AnimFrameMath に集約している。
//
//  【スクロール状態は 1 か所】
//  横位置は ScrollViewer.HorizontalOffset ただ 1 つ（ルーラー・◆・プレイヘッド・
//  サマリー行はすべて同じ Canvas に描くため、定義上ずれない）。
//  縦位置は _scrollY（ルーラーを固定したままトラック行だけを送る）。
//  座標計算そのものは AnimDopeSheetLayout（純ロジック）へ委ねる。
//
//  データの保持・IPC 送信・値エディタとの連携は親（AnimationTimelinePanel）が行う。
//  本コントロールは AnimClip を「参照」として持つのみで、モデルの所有権は持たない。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>ドープシート上のドラッグ操作種別。</summary>
internal enum DopeSheetDragMode
{
    None,
    /// <summary>選択キーをまとめてフレーム移動中。</summary>
    Key,
    /// <summary>プレイヘッドをスクラブ中。</summary>
    Playhead,
    /// <summary>ラバーバンド（矩形選択）中。</summary>
    Marquee,
    /// <summary>水平パン中（ホイールボタン / Alt+左ドラッグ）。</summary>
    Pan,
}

/// <summary>
/// アニメーションクリップのキーフレームをグラフィカルに表示・編集するコントロール。
/// </summary>
internal partial class DopeSheetPanel : UserControl
{
    // ── 表示対象データ ──────────────────────────────────────────

    /// <summary>現在表示中のクリップ（未ロード時は null）。所有権は親パネルにある。</summary>
    private AnimClip? _clip;

    /// <summary>水平スケール（1 秒あたりのピクセル数）。</summary>
    private double _pixelsPerSecond = AnimationTimelineConstants.DefaultPixelsPerSecond;

    /// <summary>縦スクロール量（ピクセル）。ルーラーは固定し、行だけを上へずらす。</summary>
    private double _scrollY;

    /// <summary>現在のプレイヘッド位置（秒）。</summary>
    private float _playheadTime;

    /// <summary>選択中のトラック行インデックス（-1 = 選択なし）。トラックリストとの連動用。</summary>
    private int _selectedTrackIndex = -1;

    /// <summary>
    /// 選択中のキー集合。ドープシートが所有し、親パネル（値エディタ・一括操作）からも参照する。
    /// 添字の正規化は AnimKeySelection 側の責務。
    /// </summary>
    private readonly AnimKeySelection _selection = new();

    // ── ドラッグ状態 ────────────────────────────────────────────

    private DopeSheetDragMode _dragMode = DopeSheetDragMode.None;

    /// <summary>◆ドラッグ中、直近に「適用済み」としたカーソル下のフレーム番号。</summary>
    private int _dragLastFrame;

    /// <summary>◆ドラッグで実際に 1 フレーム以上動いたか（Undo を積むかの判定に使う）。</summary>
    private bool _dragMovedAny;

    /// <summary>ラバーバンドの始点（キャンバス座標）。</summary>
    private Point _marqueeOrigin;

    /// <summary>ラバーバンド開始時点の選択（Ctrl / Shift 併用時に元の選択を残すため）。</summary>
    private List<AnimKeyRef> _marqueeBaseSelection = new();

    /// <summary>ラバーバンドの現在矩形（描画用。ドラッグ中のみ有効）。</summary>
    private Rect _marqueeRect;

    /// <summary>パン中の直前マウス位置（ビューポート座標）。</summary>
    private Point _panLastPoint;

    // ── 公開イベント（親パネルが購読して IPC 送信・モデル更新を行う）────

    /// <summary>トラック行をダブルクリックしてキー追加を要求（trackIndex, time）。</summary>
    public event Action<int, float>? KeyAddRequested;
    /// <summary>サマリー行のダブルクリックでキー追加を要求（time）。全トラックへ一括挿入する。</summary>
    public event Action<float>? SummaryKeyAddRequested;
    /// <summary>
    /// 選択キーの時刻が変わった（◆ドラッグ中に連続発火）。
    /// モデルは本コントロールが既に書き換えているため、親はダーティ化・
    /// ライブプレビュー・値エディタ更新だけを行う。
    /// </summary>
    public event Action? SelectionMoved;
    /// <summary>◆のドラッグ移動が終わった。Undo 履歴を 1 操作＝1 段にするために使う
    /// （SelectionMoved はドラッグ中に連続発火するため、そこで履歴を積むと段数が爆発する）。</summary>
    public event Action? KeyDragEnded;
    /// <summary>右クリックメニューからの削除要求（選択キー全部を削除する）。</summary>
    public event Action? KeyDeleteRequested;
    /// <summary>キー選択が変わった（親は値エディタを更新する）。</summary>
    public event Action? SelectionChanged;
    /// <summary>プレイヘッドのスクラブ中（ドラッグ中に連続発火、秒）。</summary>
    public event Action<float>? PlayheadScrubbed;
    /// <summary>プレイヘッドのスクラブ操作が終了した。</summary>
    public event Action? PlayheadScrubEnded;
    /// <summary>縦スクロール量が変わった（ピクセル）。親がトラックリストの縦位置を追従させる。</summary>
    public event Action<double>? VerticalScrollChanged;

    public DopeSheetPanel()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
    }

    // ── 公開 API ────────────────────────────────────────────────

    /// <summary>選択中のキー集合（親パネルの一括操作・値エディタが参照する）。</summary>
    public AnimKeySelection Selection => _selection;

    /// <summary>表示するクリップを設定する（null で空表示）。</summary>
    public void SetClip(AnimClip? clip)
    {
        _clip = clip;
        _selection.Clear();
        _scrollY = 0;
        Redraw();
        SelectionChanged?.Invoke();
    }

    /// <summary>クリップ内容が変わった（キー追加・削除・トラック追加など）ことを通知し再描画する。</summary>
    public void NotifyClipChanged()
    {
        if (_clip is not null) _selection.Normalize(_clip.Tracks);
        Redraw();
    }

    /// <summary>プレイヘッド位置を設定する（秒）。</summary>
    public void SetPlayheadTime(float time)
    {
        _playheadTime = time;
        Redraw();
    }

    /// <summary>トラックリスト側の選択と同期する（行のハイライト表示用）。</summary>
    public void SetSelectedTrackIndex(int trackIndex)
    {
        _selectedTrackIndex = trackIndex;
        Redraw();
    }

    /// <summary>水平ズーム（1 秒あたりピクセル数）を変更する。</summary>
    public void SetPixelsPerSecond(double pps)
    {
        _pixelsPerSecond = AnimTimelineZoom.ClampPixelsPerSecond(pps);
        Redraw();
    }

    /// <summary>
    /// クリップ全体が画面幅に収まるようズームし、先頭へスクロールする（「F」キー）。
    /// </summary>
    public void FrameAll()
    {
        if (_clip is null) return;
        SetPixelsPerSecond(AnimTimelineZoom.FitPixelsPerSecond(Scroller.ActualWidth, _clip.Duration));
        Scroller.UpdateLayout();
        Scroller.ScrollToHorizontalOffset(0);
    }

    /// <summary>現在のプレイヘッド位置をフレーム番号で返す。</summary>
    public int PlayheadFrame => AnimFrameMath.TimeToFrame(_playheadTime, Fps);

    /// <summary>選択が外部（親パネルのショートカット等）で変わったときに再描画と通知を行う。</summary>
    public void NotifySelectionChangedExternally()
    {
        if (_clip is not null) _selection.Normalize(_clip.Tracks);
        Redraw();
        SelectionChanged?.Invoke();
    }

    // ── 座標変換 ────────────────────────────────────────────────

    /// <summary>現在のズーム・縦スクロールに対応するレイアウト（純ロジック）。</summary>
    private AnimDopeSheetLayout Layout => AnimDopeSheetLayout.Create(_pixelsPerSecond, _scrollY);

    /// <summary>編集中クリップのフレームレート（クリップ未ロード時は既定 fps）。</summary>
    private float Fps => AnimFrameMath.NormalizeFps(_clip?.Fps ?? AnimFrameMath.DefaultFps);

    /// <summary>時刻をフレーム境界へ丸め、[0, duration] にクランプする。</summary>
    private float ClampAndSnapTime(float time)
        => AnimFrameMath.ClampAndSnapTime(time, Fps, _clip?.Duration ?? 0f);

    // ── 描画 ────────────────────────────────────────────────────

    /// <summary>Canvas 全体を再構築する。トラック数・キー数の変化のたびに全体を引き直すシンプルな実装。</summary>
    private void Redraw()
    {
        DrawCanvas.Children.Clear();
        if (_clip is null) return;

        var layout   = Layout;
        var duration = Math.Max(_clip.Duration, AnimationTimelineConstants.MinDuration);
        var contentW = Math.Max(layout.TimeToX(duration) + AnimationTimelineConstants.FitContentMarginPx, ActualWidth);
        // 縦は ScrollViewer を使わないため、キャンバス高さは常にビューポート高さ。
        // 行がはみ出す場合は _scrollY で送る（ClampScrollY が範囲を保証する）。
        var viewH    = Math.Max(ActualHeight, layout.RulerHeight);
        DrawCanvas.Width  = contentW;
        DrawCanvas.Height = viewH;

        DrawTrackRowBackgrounds(layout, contentW);
        DrawSummaryRowBackground(layout, contentW);
        DrawGrid(layout, contentW, viewH, duration);
        DrawKeys(layout);
        DrawSummaryKeys(layout);
        DrawRuler(layout, contentW, duration);   // ルーラーは行より後（＝上）に描いて最上段へ固定する
        DrawPlayhead(layout, viewH);
        DrawMarquee();
    }

    /// <summary>トラック行の背景（偶数/奇数で色分け・選択行はハイライト）を描画する。</summary>
    private void DrawTrackRowBackgrounds(AnimDopeSheetLayout layout, double contentW)
    {
        if (_clip is null) return;
        for (int i = 0; i < _clip.Tracks.Count; i++)
        {
            var bg = i == _selectedTrackIndex
                ? AnimationTimelineConstants.TrackRowSelectedBackground
                : (i % 2 == 0 ? AnimationTimelineConstants.TrackRowEvenBackground : AnimationTimelineConstants.TrackRowOddBackground);

            var rect = new Rectangle
            {
                Width  = contentW,
                Height = layout.RowHeight,
                Fill   = new SolidColorBrush(bg),
            };
            Canvas.SetLeft(rect, 0);
            Canvas.SetTop(rect, layout.TrackRowTop(i));
            DrawCanvas.Children.Add(rect);
        }
    }

    /// <summary>主目盛のフレーム位置に縦グリッド線を描画する。</summary>
    private void DrawGrid(AnimDopeSheetLayout layout, double contentW, double contentH, float duration)
    {
        var step  = PickRulerStepFrames();
        var last  = AnimFrameMath.LastFrame(Fps, duration);
        var brush = new SolidColorBrush(AnimationTimelineConstants.GridLineColor);
        for (int f = 0; f <= last; f += step)
        {
            var x = layout.TimeToX(AnimFrameMath.FrameToTime(f, Fps));
            var line = new Line
            {
                X1 = x, X2 = x,
                Y1 = layout.RulerHeight, Y2 = contentH,
                Stroke = brush, StrokeThickness = 1,
            };
            DrawCanvas.Children.Add(line);
        }
    }

    /// <summary>1 フレームぶんの横幅（ピクセル）。</summary>
    private double PixelsPerFrame => Layout.PixelsPerFrame(Fps);

    /// <summary>
    /// 現在のズームで読みやすい主目盛のフレーム刻みを候補から選ぶ。
    /// 「1 刻みの幅が MinRulerTickSpacingPx 以上になる最小の候補」を採用する。
    /// これによりフレーム番号はどのズームでも必ずフレーム格子の上に乗る。
    /// </summary>
    private int PickRulerStepFrames()
    {
        var ppf = PixelsPerFrame;
        foreach (var step in AnimationTimelineConstants.RulerTickStepsFrames)
        {
            if (step * ppf >= AnimationTimelineConstants.MinRulerTickSpacingPx)
                return step;
        }
        return AnimationTimelineConstants.RulerTickStepsFrames[AnimationTimelineConstants.RulerTickStepsFrames.Length - 1];
    }

    /// <summary>上部の時間ルーラー（背景・目盛・フレーム番号ラベル）を描画する。</summary>
    private void DrawRuler(AnimDopeSheetLayout layout, double contentW, float duration)
    {
        var rulerBg = new Rectangle
        {
            Width  = contentW,
            Height = layout.RulerHeight,
            Fill   = new SolidColorBrush(AnimationTimelineConstants.RulerBackground),
        };
        Canvas.SetLeft(rulerBg, 0);
        Canvas.SetTop(rulerBg, 0);
        DrawCanvas.Children.Add(rulerBg);

        var fps       = Fps;
        var last      = AnimFrameMath.LastFrame(fps, duration);
        var step      = PickRulerStepFrames();
        var tickBrush = new SolidColorBrush(AnimationTimelineConstants.RulerTickColor);

        // 副目盛（1 フレームごと）。密になりすぎるズームでは省略する。
        if (PixelsPerFrame >= AnimationTimelineConstants.MinFrameTickSpacingPx)
        {
            var minorBrush = new SolidColorBrush(AnimationTimelineConstants.RulerMinorTickColor);
            for (int f = 0; f <= last; f++)
            {
                if (f % step == 0) continue;   // 主目盛の位置は下のループで描く
                var mx = layout.TimeToX(AnimFrameMath.FrameToTime(f, fps));
                DrawCanvas.Children.Add(new Line
                {
                    X1 = mx, X2 = mx,
                    Y1 = layout.RulerHeight - AnimationTimelineConstants.RulerMinorTickLength,
                    Y2 = layout.RulerHeight,
                    Stroke = minorBrush, StrokeThickness = 1,
                });
            }
        }

        // 主目盛 + フレーム番号ラベル
        for (int f = 0; f <= last; f += step)
        {
            var x = layout.TimeToX(AnimFrameMath.FrameToTime(f, fps));
            var tick = new Line
            {
                X1 = x, X2 = x,
                Y1 = layout.RulerHeight - AnimationTimelineConstants.RulerMajorTickLength,
                Y2 = layout.RulerHeight,
                Stroke = tickBrush, StrokeThickness = 1,
            };
            DrawCanvas.Children.Add(tick);

            var label = new TextBlock
            {
                // フレーム番号で表示する（秒は値エディタ側に併記される）
                Text       = f.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Foreground = new SolidColorBrush(AnimationTimelineConstants.RulerTextColor),
                FontSize   = 9,
            };
            Canvas.SetLeft(label, x + 2);
            Canvas.SetTop(label, 2);
            DrawCanvas.Children.Add(label);
        }
    }

    /// <summary>全トラックのキーフレーム◆を描画する。</summary>
    private void DrawKeys(AnimDopeSheetLayout layout)
    {
        if (_clip is null) return;
        for (int ti = 0; ti < _clip.Tracks.Count; ti++)
        {
            var track = _clip.Tracks[ti];
            var rowCenterY = layout.TrackRowCenterY(ti);
            // ルーラーの下に隠れる行・画面外の行は描かない（縦スクロール時の無駄描画を避ける）
            if (rowCenterY < layout.RulerHeight || rowCenterY > ActualHeight) continue;

            for (int ki = 0; ki < track.Keys.Count; ki++)
            {
                var key = track.Keys[ki];
                var isSelected = _selection.Contains(ti, ki);
                // 選択中トラックのキーは色を変え、どのトラックを編集中か一目で分かるようにする
                var inSelectedTrack = ti == _selectedTrackIndex;
                DrawDiamond(layout.TimeToX(key.Time), rowCenterY, isSelected, inSelectedTrack);
            }
        }
    }

    /// <summary>
    /// ◆形状そのものを作る共通ヘルパー（通常キー・サマリーキーの両方から使う）。
    /// 塗り色はそれぞれの呼び出し側に任せる。
    /// </summary>
    private static Polygon CreateDiamondShape(double cx, double cy, Brush fill)
    {
        var r = AnimationTimelineConstants.KeyDiamondRadius;
        return new Polygon
        {
            Points = new PointCollection
            {
                new Point(cx,     cy - r),
                new Point(cx + r, cy),
                new Point(cx,     cy + r),
                new Point(cx - r, cy),
            },
            Fill            = fill,
            Stroke          = new SolidColorBrush(AnimationTimelineConstants.KeyDiamondBorder),
            StrokeThickness = 1,
        };
    }

    /// <summary>1 個のキーフレーム◆マーカーを描画する。</summary>
    private void DrawDiamond(double cx, double cy, bool isSelected, bool inSelectedTrack)
    {
        var fill = new SolidColorBrush(isSelected
            ? AnimationTimelineConstants.KeyDiamondSelectedFill
            : inSelectedTrack
                ? AnimationTimelineConstants.KeyDiamondTrackHighlightFill
                : AnimationTimelineConstants.KeyDiamondFill);
        DrawCanvas.Children.Add(CreateDiamondShape(cx, cy, fill));
    }

    /// <summary>サマリー行（先頭固定・常にトラック行の上）の背景を描画する。</summary>
    private void DrawSummaryRowBackground(AnimDopeSheetLayout layout, double contentW)
    {
        var rect = new Rectangle
        {
            Width  = contentW,
            Height = layout.RowHeight,
            Fill   = new SolidColorBrush(AnimationTimelineConstants.SummaryRowBackground),
        };
        Canvas.SetLeft(rect, 0);
        Canvas.SetTop(rect, layout.SummaryRowTop);
        DrawCanvas.Children.Add(rect);
    }

    /// <summary>
    /// サマリー行の◆（いずれかのトラックにキーがあるフレーム）を描画する。
    /// 集計そのものは AnimKeyEditor.SummaryFrames（純ロジック、単体テスト済み）に委ねる。
    /// そのフレームのキーが全て選択されているときだけ選択色にする（選択状態を別に持たない）。
    /// </summary>
    private void DrawSummaryKeys(AnimDopeSheetLayout layout)
    {
        if (_clip is null) return;
        var rowCenterY = layout.SummaryRowCenterY;
        if (rowCenterY < layout.RulerHeight || rowCenterY > ActualHeight) return;

        foreach (var time in AnimKeyEditor.SummaryFrames(_clip.Tracks))
        {
            var fill = new SolidColorBrush(IsFrameFullySelected(time)
                ? AnimationTimelineConstants.KeyDiamondSelectedFill
                : AnimationTimelineConstants.KeyDiamondFill);
            DrawCanvas.Children.Add(CreateDiamondShape(layout.TimeToX(time), rowCenterY, fill));
        }
    }

    /// <summary>指定フレームのキーが（1 個以上あり）すべて選択されているか。サマリー◆の表示に使う。</summary>
    private bool IsFrameFullySelected(float time)
    {
        if (_clip is null) return false;
        var found = false;
        for (int ti = 0; ti < _clip.Tracks.Count; ti++)
        {
            var ki = AnimKeyEditor.FindKeyAtFrame(_clip.Tracks[ti], time, Fps);
            if (ki < 0) continue;
            if (!_selection.Contains(ti, ki)) return false;
            found = true;
        }
        return found;
    }

    /// <summary>プレイヘッド（縦線 + 上部ハンドル）を描画する。</summary>
    private void DrawPlayhead(AnimDopeSheetLayout layout, double contentH)
    {
        var x = layout.TimeToX(_playheadTime);
        var brush = new SolidColorBrush(AnimationTimelineConstants.PlayheadColor);

        var line = new Line
        {
            X1 = x, X2 = x,
            Y1 = 0, Y2 = contentH,
            Stroke          = brush,
            StrokeThickness = AnimationTimelineConstants.PlayheadLineThickness,
        };
        DrawCanvas.Children.Add(line);

        var s = AnimationTimelineConstants.PlayheadHandleSize;
        var handle = new Polygon
        {
            Points = new PointCollection
            {
                new Point(x - s / 2, 0),
                new Point(x + s / 2, 0),
                new Point(x,         s),
            },
            Fill = brush,
        };
        DrawCanvas.Children.Add(handle);
    }

    /// <summary>ラバーバンド（矩形選択）の枠を描画する。ドラッグ中のみ。</summary>
    private void DrawMarquee()
    {
        if (_dragMode != DopeSheetDragMode.Marquee || _marqueeRect.Width <= 0 || _marqueeRect.Height <= 0) return;

        var rect = new Rectangle
        {
            Width           = _marqueeRect.Width,
            Height          = _marqueeRect.Height,
            Fill            = new SolidColorBrush(AnimationTimelineConstants.MarqueeFillColor),
            Stroke          = new SolidColorBrush(AnimationTimelineConstants.MarqueeBorderColor),
            StrokeThickness = AnimationTimelineConstants.MarqueeBorderThickness,
        };
        Canvas.SetLeft(rect, _marqueeRect.X);
        Canvas.SetTop(rect, _marqueeRect.Y);
        DrawCanvas.Children.Add(rect);
    }

    // ── ヒットテスト ────────────────────────────────────────────

    /// <summary>座標からキー◆を特定する（見つからなければ null）。</summary>
    private AnimKeyRef? HitTestKey(Point p)
        => _clip is null
            ? null
            : Layout.HitTestKey(_clip.Tracks, p.X, p.Y, AnimationTimelineConstants.KeyDiamondHitRadius);

    /// <summary>座標からサマリー行の◆を特定する（見つからなければ null）。ヒットしたフレームの時刻を返す。</summary>
    private float? HitTestSummaryKey(Point p)
    {
        if (_clip is null || !Layout.IsInSummaryRow(p.Y)) return null;
        foreach (var time in AnimKeyEditor.SummaryFrames(_clip.Tracks))
        {
            if (Math.Abs(p.X - Layout.TimeToX(time)) <= AnimationTimelineConstants.KeyDiamondHitRadius) return time;
        }
        return null;
    }

    // ── マウス操作 ──────────────────────────────────────────────

    /// <summary>
    /// マウスボタン押下。左＝選択・ドラッグ・矩形選択・スクラブ、
    /// 中（ホイールボタン）と Alt+左＝水平パン。
    /// </summary>
    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_clip is null) return;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        // ── パン（ホイールボタンドラッグ / Alt+左ドラッグ）──
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && alt))
        {
            _dragMode     = DopeSheetDragMode.Pan;
            _panLastPoint = e.GetPosition(Scroller);
            DrawCanvas.CaptureMouse();
            Cursor = Cursors.ScrollWE;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;

        var p     = e.GetPosition(DrawCanvas);
        var ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift)   != 0;

        // ── ルーラー領域 → プレイヘッドのスクラブ ──
        if (Layout.IsInRuler(p.Y))
        {
            _dragMode = DopeSheetDragMode.Playhead;
            DrawCanvas.CaptureMouse();
            ScrubTo(p.X);
            e.Handled = true;
            return;
        }

        // ── サマリー行の◆ → そのフレームのキーを全トラックぶん選択 ──
        if (Layout.IsInSummaryRow(p.Y))
        {
            if (HitTestSummaryKey(p) is { } summaryTime)
            {
                if (ctrl || shift) SelectFrameAdditive(summaryTime);
                else               _selection.SelectFrame(_clip.Tracks, summaryTime, Fps);

                BeginKeyDrag(p);
                SelectionChanged?.Invoke();
                e.Handled = true;
                return;
            }

            // 空白部のダブルクリック → 全トラックへキー追加を要求する
            if (e.ClickCount == 2)
            {
                SummaryKeyAddRequested?.Invoke(ClampAndSnapTime(Layout.XToTime(p.X)));
                e.Handled = true;
                return;
            }
        }

        // ── 通常キーの◆ ──
        if (HitTestKey(p) is { } hit)
        {
            if (ctrl)
            {
                _selection.Toggle(hit.TrackIndex, hit.KeyIndex);
            }
            else if (shift)
            {
                _selection.Add(hit.TrackIndex, hit.KeyIndex);
            }
            else if (!_selection.Contains(hit.TrackIndex, hit.KeyIndex))
            {
                // 既に選択済みの◆を掴んだ場合は選択を保つ（複数選択のドラッグ移動を成立させるため）
                _selection.SelectSingle(hit.TrackIndex, hit.KeyIndex);
            }

            if (_selection.Contains(hit.TrackIndex, hit.KeyIndex)) BeginKeyDrag(p);
            SelectionChanged?.Invoke();
            Redraw();
            e.Handled = true;
            return;
        }

        // ── 空白部のダブルクリック → その時刻・トラックへキー追加を要求する ──
        if (e.ClickCount == 2)
        {
            var trackIndex = Layout.HitTestTrackRow(p.Y, _clip.Tracks.Count);
            if (trackIndex >= 0)
            {
                KeyAddRequested?.Invoke(trackIndex, ClampAndSnapTime(Layout.XToTime(p.X)));
                e.Handled = true;
                return;
            }
        }

        // ── 空白部のドラッグ開始 → ラバーバンド（矩形選択）──
        _dragMode             = DopeSheetDragMode.Marquee;
        _marqueeOrigin        = p;
        _marqueeRect          = new Rect(p, p);
        _marqueeBaseSelection = ctrl || shift ? _selection.Ordered() : new List<AnimKeyRef>();
        if (!ctrl && !shift) _selection.Clear();
        DrawCanvas.CaptureMouse();
        SelectionChanged?.Invoke();
        Redraw();
    }

    /// <summary>指定フレームのキーを既存選択へ追加する（Ctrl / Shift + サマリー◆クリック）。</summary>
    private void SelectFrameAdditive(float time)
    {
        if (_clip is null) return;
        for (int ti = 0; ti < _clip.Tracks.Count; ti++)
        {
            var ki = AnimKeyEditor.FindKeyAtFrame(_clip.Tracks[ti], time, Fps);
            if (ki >= 0) _selection.Add(ti, ki);
        }
    }

    /// <summary>選択キーのドラッグ移動を開始する。</summary>
    private void BeginKeyDrag(Point p)
    {
        _dragMode      = DopeSheetDragMode.Key;
        _dragLastFrame = AnimFrameMath.TimeToFrame(Layout.XToTime(p.X), Fps);
        _dragMovedAny  = false;
        DrawCanvas.CaptureMouse();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == DopeSheetDragMode.None || _clip is null) return;

        switch (_dragMode)
        {
            case DopeSheetDragMode.Playhead:
                ScrubTo(e.GetPosition(DrawCanvas).X);
                break;

            case DopeSheetDragMode.Key:
            {
                // カーソル下のフレームと「直近に適用したフレーム」の差ぶんだけ選択全体を動かす。
                // 端でクランプされたときは適用できた量だけ基準を進めるので、
                // 戻すときに取りこぼしが出ない。
                var frame = AnimFrameMath.TimeToFrame(Layout.XToTime(e.GetPosition(DrawCanvas).X), Fps);
                var moved = AnimKeyEditor.MoveSelectedKeys(
                    _clip.Tracks, _selection, frame - _dragLastFrame, Fps, _clip.Duration);
                if (moved == 0) break;

                _dragLastFrame += moved;
                _dragMovedAny   = true;
                SelectionMoved?.Invoke();
                Redraw();
                break;
            }

            case DopeSheetDragMode.Marquee:
            {
                var p = e.GetPosition(DrawCanvas);
                // わずかな手ぶれで選択が入れ替わらないよう、しきい値未満の移動は無視する
                if (Math.Abs(p.X - _marqueeOrigin.X) < AnimationTimelineConstants.MarqueeStartThresholdPx &&
                    Math.Abs(p.Y - _marqueeOrigin.Y) < AnimationTimelineConstants.MarqueeStartThresholdPx)
                    break;

                _marqueeRect = new Rect(_marqueeOrigin, p);

                _selection.Clear();
                _selection.AddRange(_marqueeBaseSelection);
                _selection.AddRange(Layout.KeysInRect(_clip.Tracks, _marqueeOrigin.X, _marqueeOrigin.Y, p.X, p.Y));
                SelectionChanged?.Invoke();
                Redraw();
                break;
            }

            case DopeSheetDragMode.Pan:
            {
                var p  = e.GetPosition(Scroller);
                var dx = p.X - _panLastPoint.X;
                _panLastPoint = p;
                Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - dx);
                break;
            }
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        switch (_dragMode)
        {
            case DopeSheetDragMode.Playhead:
                PlayheadScrubEnded?.Invoke();
                break;

            case DopeSheetDragMode.Key:
                // 実際に動いたときだけ Undo を 1 段積む（クリックしただけで履歴を汚さない）
                if (_dragMovedAny) KeyDragEnded?.Invoke();
                break;

            case DopeSheetDragMode.Marquee:
                _marqueeRect = Rect.Empty;
                break;

            case DopeSheetDragMode.Pan:
                Cursor = Cursors.Arrow;
                break;
        }

        if (_dragMode != DopeSheetDragMode.None)
        {
            _dragMode = DopeSheetDragMode.None;
            DrawCanvas.ReleaseMouseCapture();
            Redraw();
        }
    }

    /// <summary>右クリック: ◆上なら削除メニューを出す（選択キー全部が対象）。</summary>
    private void OnCanvasMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_clip is null) return;
        var p = e.GetPosition(DrawCanvas);

        // サマリー行の◆ → そのフレームのキーを選択してから削除メニュー
        if (Layout.IsInSummaryRow(p.Y))
        {
            if (HitTestSummaryKey(p) is not { } summaryTime) return;
            _selection.SelectFrame(_clip.Tracks, summaryTime, Fps);
        }
        else
        {
            if (HitTestKey(p) is not { } hit) return;
            // 選択外の◆を右クリックしたらそれ 1 個を選び直す（見えている対象と操作対象を一致させる）
            if (!_selection.Contains(hit.TrackIndex, hit.KeyIndex))
                _selection.SelectSingle(hit.TrackIndex, hit.KeyIndex);
        }

        SelectionChanged?.Invoke();
        Redraw();

        var menu = new ContextMenu { Background = new SolidColorBrush(AnimationTimelineConstants.ToolbarBackground) };
        var header = _selection.IsMultiple
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            AnimationTimelineConstants.DeleteSelectedKeysMenuFormat, _selection.Count)
            : AnimationTimelineConstants.DeleteKeyMenuHeader;
        var deleteItem = new MenuItem { Header = header, Foreground = new SolidColorBrush(AnimationTimelineConstants.TextColor) };
        deleteItem.Click += (_, _) => KeyDeleteRequested?.Invoke();
        menu.Items.Add(deleteItem);
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ── ホイール操作（ズーム / 横スクロール / 縦スクロール）────────

    /// <summary>
    /// ホイール。Ctrl でカーソル位置を軸にした時間軸ズーム、
    /// Shift でトラックの縦スクロール、無修飾で横スクロール。
    /// いずれもナビゲーションであり、クリップは一切変更しない（Undo も積まない）。
    /// </summary>
    private void OnScrollerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_clip is null) return;
        var notches = e.Delta / AnimationTimelineConstants.WheelDeltaPerNotch;
        e.Handled = true;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ZoomAtCursor(e.GetPosition(Scroller).X, (int)Math.Round(notches));
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            ScrollVerticalBy(-notches * AnimationTimelineConstants.WheelVerticalScrollRows * Layout.RowHeight);
            return;
        }

        Scroller.ScrollToHorizontalOffset(
            Scroller.HorizontalOffset - notches * AnimationTimelineConstants.WheelScrollStepPx);
    }

    /// <summary>カーソル下の時刻を固定したままズームする（計算は AnimTimelineZoom）。</summary>
    private void ZoomAtCursor(double cursorX, int notches)
    {
        if (notches == 0) return;
        var oldPps = _pixelsPerSecond;
        var newPps = AnimTimelineZoom.ZoomedPixelsPerSecond(oldPps, notches);
        if (Math.Abs(newPps - oldPps) < double.Epsilon) return;   // 端でクランプ済み

        var newScrollX = AnimTimelineZoom.ScrollXForZoomAtCursor(oldPps, newPps, Scroller.HorizontalOffset, cursorX);
        SetPixelsPerSecond(newPps);
        Scroller.UpdateLayout();     // 新しいコンテンツ幅を確定させてからスクロールする
        Scroller.ScrollToHorizontalOffset(newScrollX);
    }

    /// <summary>トラック行を縦にスクロールする（ルーラーは動かさない）。</summary>
    private void ScrollVerticalBy(double deltaPx)
    {
        if (_clip is null) return;
        var contentH = Layout.ContentHeight(_clip.Tracks.Count);
        var newY     = AnimTimelineZoom.ClampScrollY(_scrollY + deltaPx, contentH, ActualHeight);
        if (Math.Abs(newY - _scrollY) < double.Epsilon) return;

        _scrollY = newY;
        Redraw();
        VerticalScrollChanged?.Invoke(_scrollY);
    }

    /// <summary>
    /// プレイヘッドを x 座標に応じた時刻へ移動し、スクラブイベントを発火する。
    /// 位置は必ずフレーム境界へスナップする（キーと同じ格子に乗せるため）。
    /// </summary>
    private void ScrubTo(double x)
    {
        var time = ClampAndSnapTime(Layout.XToTime(x));
        _playheadTime = time;
        Redraw();
        PlayheadScrubbed?.Invoke(time);
    }
}
