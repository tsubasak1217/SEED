// ============================================================
//  DopeSheetPanel.xaml.cs — ドープシート描画・編集コントロール
//
//  AnimClip の全トラックを行として並べ、各トラックのキーフレームを
//  ◆マーカーで表示する。時間ルーラー・プレイヘッド（再生位置）も
//  同じ Canvas 上に描画する。
//
//  【責務】
//  ・見た目の描画（ルーラー目盛・トラック行の背景・グリッド線・◆・プレイヘッド）
//  ・マウス操作（ダブルクリックでキー追加、ドラッグでキー移動、右クリックで削除、
//    ルーラードラッグでプレイヘッドのスクラブ）をイベントとして親パネルへ通知する
//
//  【時間軸はフレーム基準】
//  ルーラーの目盛・ラベルはクリップの fps（AnimClip.Fps）に基づくフレーム番号で、
//  プレイヘッドもキー移動も必ずフレーム境界へスナップする。
//  内部の保持値は従来どおり秒（.anim / ランタイムの単位）で、
//  フレームとの変換は AnimFrameMath に集約している。
//
//  データの保持・IPC 送信・値エディタとの連携は親（AnimationTimelinePanel）が行う。
//  本コントロールは AnimClip を「参照」として持つのみで、モデルの所有権は持たない。
// ============================================================

using System;
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
    Key,
    Playhead,
    /// <summary>サマリー行（「全チャンネル」）の◆をドラッグ中。</summary>
    SummaryKey,
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

    /// <summary>現在のプレイヘッド位置（秒）。</summary>
    private float _playheadTime;

    /// <summary>選択中のトラック行インデックス（-1 = 選択なし）。トラックリストとの連動用。</summary>
    private int _selectedTrackIndex = -1;

    /// <summary>選択中のキー（トラックリストとの連動・値エディタ表示用）。</summary>
    private int _selectedTrackForKey = -1;
    private int _selectedKeyIndex    = -1;

    /// <summary>
    /// サマリー行（「全チャンネル」）で選択中のフレーム時刻（秒）。未選択は null。
    /// 通常キーの選択（_selectedTrackForKey/_selectedKeyIndex）とは排他。
    /// 「どの (トラック,キー) か」ではなく「どのフレームか」で持つ理由は、
    /// サマリー行の◆がトラックをまたいだ集計（AnimKeyEditor.SummaryFrames）の
    /// 派生表示であり、トラック側の増減で添字が意味を失うため。
    /// </summary>
    private float? _selectedSummaryTime;

    // ── ドラッグ状態 ────────────────────────────────────────────

    private DopeSheetDragMode _dragMode = DopeSheetDragMode.None;
    private int    _dragTrackIndex = -1;
    private int    _dragKeyIndex   = -1;
    /// <summary>サマリー◆ドラッグ中の「元の時刻」。移動のたびに現在位置へ更新する。</summary>
    private float  _dragSummaryTime;

    // ── 公開イベント（親パネルが購読して IPC 送信・モデル更新を行う）────

    /// <summary>トラック行をダブルクリックしてキー追加を要求（trackIndex, time）。</summary>
    public event Action<int, float>? KeyAddRequested;
    /// <summary>◆ドラッグでキーの時刻が変わった（trackIndex, keyIndex, newTime）。ドラッグ中に連続発火。</summary>
    public event Action<int, int, float>? KeyMoved;
    /// <summary>◆のドラッグ移動が終わった。Undo 履歴を 1 操作＝1 段にするために使う
    /// （KeyMoved はドラッグ中に連続発火するため、そこで履歴を積むと段数が爆発する）。</summary>
    public event Action? KeyDragEnded;
    /// <summary>◆右クリックメニューからの削除要求（trackIndex, keyIndex）。</summary>
    public event Action<int, int>? KeyDeleteRequested;
    /// <summary>◆選択が変わった（trackIndex, keyIndex）。(-1,-1) は選択解除。</summary>
    public event Action<int, int>? KeySelectionChanged;
    /// <summary>プレイヘッドのスクラブ中（ドラッグ中に連続発火、秒）。</summary>
    public event Action<float>? PlayheadScrubbed;
    /// <summary>プレイヘッドのスクラブ操作が終了した。</summary>
    public event Action? PlayheadScrubEnded;

    // ── サマリー行（「全チャンネル」）のイベント ──────────────────
    // 通常キーの (trackIndex, keyIndex) では「どのフレームか」を表せないため、
    // サマリー行専用に時刻ベースのイベントを別途用意する。

    /// <summary>サマリー行のダブルクリックでキー追加を要求（time）。全トラックへ一括挿入する。</summary>
    public event Action<float>? SummaryKeyAddRequested;
    /// <summary>サマリー◆ドラッグで時刻が変わった（oldTime, newTime）。ドラッグ中に連続発火。</summary>
    public event Action<float, float>? SummaryKeyMoved;
    /// <summary>サマリー◆右クリックメニューからの削除要求（time）。</summary>
    public event Action<float>? SummaryKeyDeleteRequested;
    /// <summary>サマリー◆選択が変わった（null = 選択解除）。</summary>
    public event Action<float?>? SummaryKeySelectionChanged;

    public DopeSheetPanel()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
    }

    // ── 公開 API ────────────────────────────────────────────────

    /// <summary>表示するクリップを設定する（null で空表示）。</summary>
    public void SetClip(AnimClip? clip)
    {
        _clip = clip;
        _selectedTrackForKey = -1;
        _selectedKeyIndex    = -1;
        _selectedSummaryTime = null;
        Redraw();
    }

    /// <summary>クリップ内容が変わった（キー追加・削除・トラック追加など）ことを通知し再描画する。</summary>
    public void NotifyClipChanged() => Redraw();

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

    /// <summary>現在選択中のキー（トラックインデックス, キーインデックス）。未選択は (-1,-1)。</summary>
    public (int trackIndex, int keyIndex) SelectedKey => (_selectedTrackForKey, _selectedKeyIndex);

    /// <summary>サマリー行で現在選択中のフレーム時刻（秒）。未選択は null。</summary>
    public float? SelectedSummaryTime => _selectedSummaryTime;

    /// <summary>水平ズーム（1 秒あたりピクセル数）を変更する。</summary>
    public void SetPixelsPerSecond(double pps)
    {
        _pixelsPerSecond = Math.Clamp(pps, AnimationTimelineConstants.MinPixelsPerSecond, AnimationTimelineConstants.MaxPixelsPerSecond);
        Redraw();
    }

    // ── 座標変換 ────────────────────────────────────────────────

    private double TimeToX(float time) => time * _pixelsPerSecond;
    private float XToTime(double x) => (float)(x / _pixelsPerSecond);

    /// <summary>
    /// サマリー行（「全チャンネル」）の上端 Y 座標。ルーラー直下・常に 1 行ぶんを占有する。
    /// 実トラック行はこの下（<see cref="TrackRowTop"/>）に積む。
    /// </summary>
    private double SummaryRowTop => AnimationTimelineConstants.RulerHeight;

    private double TrackRowTop(int trackIndex) =>
        AnimationTimelineConstants.RulerHeight + AnimationTimelineConstants.TrackRowHeight
        + trackIndex * AnimationTimelineConstants.TrackRowHeight;

    /// <summary>編集中クリップのフレームレート（クリップ未ロード時は既定 fps）。</summary>
    private float Fps => AnimFrameMath.NormalizeFps(_clip?.Fps ?? AnimFrameMath.DefaultFps);

    /// <summary>時刻をフレーム境界へ丸め、[0, duration] にクランプする。</summary>
    private float ClampAndSnapTime(float time)
        => AnimFrameMath.ClampAndSnapTime(time, Fps, _clip?.Duration ?? 0f);

    /// <summary>現在のプレイヘッド位置をフレーム番号で返す。</summary>
    public int PlayheadFrame => AnimFrameMath.TimeToFrame(_playheadTime, Fps);

    // ── 描画 ────────────────────────────────────────────────────

    /// <summary>Canvas 全体を再構築する。トラック数・キー数の変化のたびに全体を引き直すシンプルな実装。</summary>
    private void Redraw()
    {
        DrawCanvas.Children.Clear();
        if (_clip is null) return;

        var duration   = Math.Max(_clip.Duration, AnimationTimelineConstants.MinDuration);
        var contentW   = Math.Max(TimeToX(duration) + 40, ActualWidth);
        // +1 行ぶんは常時先頭のサマリー行（「全チャンネル」）。
        var contentH   = AnimationTimelineConstants.RulerHeight
                        + (1 + _clip.Tracks.Count) * AnimationTimelineConstants.TrackRowHeight;
        DrawCanvas.Width  = contentW;
        DrawCanvas.Height = Math.Max(contentH, ActualHeight);

        DrawTrackRowBackgrounds(contentW);
        DrawSummaryRowBackground(contentW);
        DrawGrid(contentW, contentH, duration);
        DrawRuler(contentW, duration);
        DrawKeys();
        DrawSummaryKeys();
        DrawPlayhead(contentH);
    }

    /// <summary>トラック行の背景（偶数/奇数で色分け・選択行はハイライト）を描画する。</summary>
    private void DrawTrackRowBackgrounds(double contentW)
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
                Height = AnimationTimelineConstants.TrackRowHeight,
                Fill   = new SolidColorBrush(bg),
            };
            Canvas.SetLeft(rect, 0);
            Canvas.SetTop(rect, TrackRowTop(i));
            DrawCanvas.Children.Add(rect);
        }
    }

    /// <summary>主目盛のフレーム位置に縦グリッド線を描画する。</summary>
    private void DrawGrid(double contentW, double contentH, float duration)
    {
        var step  = PickRulerStepFrames();
        var last  = AnimFrameMath.LastFrame(Fps, duration);
        var brush = new SolidColorBrush(AnimationTimelineConstants.GridLineColor);
        for (int f = 0; f <= last; f += step)
        {
            var x = TimeToX(AnimFrameMath.FrameToTime(f, Fps));
            var line = new Line
            {
                X1 = x, X2 = x,
                Y1 = AnimationTimelineConstants.RulerHeight, Y2 = contentH,
                Stroke = brush, StrokeThickness = 1,
            };
            DrawCanvas.Children.Add(line);
        }
    }

    /// <summary>1 フレームぶんの横幅（ピクセル）。</summary>
    private double PixelsPerFrame => _pixelsPerSecond / Fps;

    /// <summary>
    /// 現在のズームで読みやすい主目盛のフレーム刻みを候補から選ぶ。
    /// 「1 刻みの幅が MinRulerTickSpacingPx 以上になる最小の候補」を採用する。
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

    /// <summary>上部の時間ルーラー（背景・目盛・秒数ラベル）を描画する。</summary>
    private void DrawRuler(double contentW, float duration)
    {
        var rulerBg = new Rectangle
        {
            Width  = contentW,
            Height = AnimationTimelineConstants.RulerHeight,
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
                var mx = TimeToX(AnimFrameMath.FrameToTime(f, fps));
                DrawCanvas.Children.Add(new Line
                {
                    X1 = mx, X2 = mx,
                    Y1 = AnimationTimelineConstants.RulerHeight - AnimationTimelineConstants.RulerMinorTickLength,
                    Y2 = AnimationTimelineConstants.RulerHeight,
                    Stroke = minorBrush, StrokeThickness = 1,
                });
            }
        }

        // 主目盛 + フレーム番号ラベル
        for (int f = 0; f <= last; f += step)
        {
            var x = TimeToX(AnimFrameMath.FrameToTime(f, fps));
            var tick = new Line
            {
                X1 = x, X2 = x,
                Y1 = AnimationTimelineConstants.RulerHeight - AnimationTimelineConstants.RulerMajorTickLength,
                Y2 = AnimationTimelineConstants.RulerHeight,
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
    private void DrawKeys()
    {
        if (_clip is null) return;
        for (int ti = 0; ti < _clip.Tracks.Count; ti++)
        {
            var track = _clip.Tracks[ti];
            var rowCenterY = TrackRowTop(ti) + AnimationTimelineConstants.TrackRowHeight / 2.0;
            for (int ki = 0; ki < track.Keys.Count; ki++)
            {
                var key = track.Keys[ki];
                var isSelected = ti == _selectedTrackForKey && ki == _selectedKeyIndex;
                // 選択中トラックのキーは色を変え、どのトラックを編集中か一目で分かるようにする
                var inSelectedTrack = ti == _selectedTrackIndex;
                DrawDiamond(TimeToX(key.Time), rowCenterY, isSelected, inSelectedTrack, ti, ki);
            }
        }
    }

    /// <summary>
    /// ◆形状そのものを作る共通ヘルパー（通常キー・サマリーキーの両方から使う）。
    /// 塗り色・Tag の付け方はそれぞれの呼び出し側に任せる。
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

    /// <summary>1 個のキーフレーム◆マーカーを描画する。Tag に (trackIndex, keyIndex) を仕込みヒットテストに使う。</summary>
    private void DrawDiamond(double cx, double cy, bool isSelected, bool inSelectedTrack, int trackIndex, int keyIndex)
    {
        var fill = new SolidColorBrush(isSelected
            ? AnimationTimelineConstants.KeyDiamondSelectedFill
            : inSelectedTrack
                ? AnimationTimelineConstants.KeyDiamondTrackHighlightFill
                : AnimationTimelineConstants.KeyDiamondFill);
        var poly = CreateDiamondShape(cx, cy, fill);
        poly.Tag = (trackIndex, keyIndex);
        DrawCanvas.Children.Add(poly);
    }

    /// <summary>サマリー行（先頭固定・常にトラック行の上）の背景とラベルを描画する。</summary>
    private void DrawSummaryRowBackground(double contentW)
    {
        var rect = new Rectangle
        {
            Width  = contentW,
            Height = AnimationTimelineConstants.TrackRowHeight,
            Fill   = new SolidColorBrush(AnimationTimelineConstants.SummaryRowBackground),
        };
        Canvas.SetLeft(rect, 0);
        Canvas.SetTop(rect, SummaryRowTop);
        DrawCanvas.Children.Add(rect);
    }

    /// <summary>
    /// サマリー行の◆（いずれかのトラックにキーがあるフレーム）を描画する。
    /// 集計そのものは AnimKeyEditor.SummaryFrames（純ロジック、単体テスト済み）に委ねる。
    /// </summary>
    private void DrawSummaryKeys()
    {
        if (_clip is null) return;
        var rowCenterY = SummaryRowTop + AnimationTimelineConstants.TrackRowHeight / 2.0;
        foreach (var time in AnimKeyEditor.SummaryFrames(_clip.Tracks))
        {
            var isSelected = _selectedSummaryTime is { } sel && AnimFrameMath.SameFrame(sel, time, Fps);
            var fill = new SolidColorBrush(isSelected
                ? AnimationTimelineConstants.KeyDiamondSelectedFill
                : AnimationTimelineConstants.KeyDiamondFill);
            DrawCanvas.Children.Add(CreateDiamondShape(TimeToX(time), rowCenterY, fill));
        }
    }

    /// <summary>プレイヘッド（縦線 + 上部ハンドル）を描画する。</summary>
    private void DrawPlayhead(double contentH)
    {
        var x = TimeToX(_playheadTime);
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

    // ── ヒットテスト ────────────────────────────────────────────

    /// <summary>座標からキー◆を特定する（見つからなければ null）。</summary>
    private (int trackIndex, int keyIndex)? HitTestKey(Point p)
    {
        if (_clip is null) return null;
        for (int ti = 0; ti < _clip.Tracks.Count; ti++)
        {
            var rowCenterY = TrackRowTop(ti) + AnimationTimelineConstants.TrackRowHeight / 2.0;
            if (Math.Abs(p.Y - rowCenterY) > AnimationTimelineConstants.KeyDiamondHitRadius) continue;

            var track = _clip.Tracks[ti];
            for (int ki = 0; ki < track.Keys.Count; ki++)
            {
                var x = TimeToX(track.Keys[ki].Time);
                if (Math.Abs(p.X - x) <= AnimationTimelineConstants.KeyDiamondHitRadius)
                    return (ti, ki);
            }
        }
        return null;
    }

    /// <summary>座標が属するトラック行インデックス（ルーラー・サマリー行は -1）。</summary>
    private int HitTestTrackRow(Point p)
    {
        var tracksTop = AnimationTimelineConstants.RulerHeight + AnimationTimelineConstants.TrackRowHeight;
        if (_clip is null || p.Y < tracksTop) return -1;
        var idx = (int)((p.Y - tracksTop) / AnimationTimelineConstants.TrackRowHeight);
        return idx >= 0 && idx < _clip.Tracks.Count ? idx : -1;
    }

    /// <summary>座標がサマリー行（ルーラー直下 1 行）の帯の中にあるか。</summary>
    private bool IsInSummaryRow(Point p) =>
        p.Y >= SummaryRowTop && p.Y < SummaryRowTop + AnimationTimelineConstants.TrackRowHeight;

    /// <summary>座標からサマリー行の◆を特定する（見つからなければ null）。ヒットしたフレームの時刻を返す。</summary>
    private float? HitTestSummaryKey(Point p)
    {
        if (_clip is null || !IsInSummaryRow(p)) return null;
        foreach (var time in AnimKeyEditor.SummaryFrames(_clip.Tracks))
        {
            var x = TimeToX(time);
            if (Math.Abs(p.X - x) <= AnimationTimelineConstants.KeyDiamondHitRadius) return time;
        }
        return null;
    }

    // ── マウス操作 ──────────────────────────────────────────────

    private void OnCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(DrawCanvas);

        // ルーラー領域のクリック/ドラッグ開始 → プレイヘッドのスクラブ
        if (p.Y < AnimationTimelineConstants.RulerHeight)
        {
            _dragMode = DopeSheetDragMode.Playhead;
            DrawCanvas.CaptureMouse();
            ScrubTo(p.X);
            e.Handled = true;
            return;
        }

        // サマリー行（「全チャンネル」）のクリック/ドラッグ開始
        if (IsInSummaryRow(p))
        {
            var summaryHit = HitTestSummaryKey(p);
            if (summaryHit is { } st)
            {
                SelectSummaryKey(st);
                _dragMode        = DopeSheetDragMode.SummaryKey;
                _dragSummaryTime = st;
                DrawCanvas.CaptureMouse();
                Redraw();
                e.Handled = true;
                return;
            }

            // 空白部のダブルクリック → 全トラックへキー追加を要求する
            if (e.ClickCount == 2)
            {
                var time = ClampAndSnapTime(XToTime(p.X));
                SummaryKeyAddRequested?.Invoke(time);
                e.Handled = true;
                return;
            }

            // 空白部の単クリックは選択解除
            SelectSummaryKey(null);
            Redraw();
            return;
        }

        var hit = HitTestKey(p);
        if (hit is { } h)
        {
            // ダブルクリックは無視（キー追加はダブルクリック時に別処理する既存キー上では発火しない）
            SelectSummaryKey(null);
            _selectedTrackForKey = h.trackIndex;
            _selectedKeyIndex    = h.keyIndex;
            KeySelectionChanged?.Invoke(h.trackIndex, h.keyIndex);

            _dragMode       = DopeSheetDragMode.Key;
            _dragTrackIndex = h.trackIndex;
            _dragKeyIndex   = h.keyIndex;
            DrawCanvas.CaptureMouse();
            Redraw();
            e.Handled = true;
            return;
        }

        // 空白部のダブルクリック → その時刻・トラックへキー追加を要求する
        if (e.ClickCount == 2)
        {
            var trackIndex = HitTestTrackRow(p);
            if (trackIndex >= 0)
            {
                var time = ClampAndSnapTime(XToTime(p.X));
                KeyAddRequested?.Invoke(trackIndex, time);
                e.Handled = true;
                return;
            }
        }

        // 空白部の単クリックは選択解除
        _selectedTrackForKey = -1;
        _selectedKeyIndex    = -1;
        KeySelectionChanged?.Invoke(-1, -1);
        SelectSummaryKey(null);
        Redraw();
    }

    /// <summary>
    /// サマリー行の選択状態を設定し、通常キーの選択を解除する（両者は排他）。
    /// 選択解除（null）でも SummaryKeySelectionChanged を発火する。
    /// </summary>
    private void SelectSummaryKey(float? time)
    {
        _selectedSummaryTime = time;
        if (time is not null)
        {
            _selectedTrackForKey = -1;
            _selectedKeyIndex    = -1;
        }
        SummaryKeySelectionChanged?.Invoke(time);
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == DopeSheetDragMode.None) return;
        var p = e.GetPosition(DrawCanvas);

        switch (_dragMode)
        {
            case DopeSheetDragMode.Playhead:
                ScrubTo(p.X);
                break;

            case DopeSheetDragMode.Key when _clip is not null:
                var newTime = ClampAndSnapTime(XToTime(p.X));
                var track   = _clip.Tracks[_dragTrackIndex];
                track.Keys[_dragKeyIndex].Time = newTime;
                KeyMoved?.Invoke(_dragTrackIndex, _dragKeyIndex, newTime);
                Redraw();
                break;

            case DopeSheetDragMode.SummaryKey when _clip is not null:
                var newSummaryTime = ClampAndSnapTime(XToTime(p.X));
                AnimKeyEditor.MoveKeysAtFrame(_clip.Tracks, _dragSummaryTime, newSummaryTime, Fps);
                SummaryKeyMoved?.Invoke(_dragSummaryTime, newSummaryTime);
                _dragSummaryTime      = newSummaryTime;
                _selectedSummaryTime  = newSummaryTime;
                Redraw();
                break;
        }
    }

    private void OnCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode == DopeSheetDragMode.Playhead)
            PlayheadScrubEnded?.Invoke();
        else if (_dragMode is DopeSheetDragMode.Key or DopeSheetDragMode.SummaryKey)
            KeyDragEnded?.Invoke();   // Undo 履歴に 1 段積む処理は通常キー・サマリーキーで共通

        _dragMode = DopeSheetDragMode.None;
        DrawCanvas.ReleaseMouseCapture();
    }

    private void OnCanvasMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(DrawCanvas);

        if (IsInSummaryRow(p))
        {
            var summaryHit = HitTestSummaryKey(p);
            if (summaryHit is not { } st) return;

            SelectSummaryKey(st);
            Redraw();

            var summaryMenu = new ContextMenu { Background = new SolidColorBrush(AnimationTimelineConstants.ToolbarBackground) };
            var summaryDeleteItem = new MenuItem { Header = "キーを削除（全トラック）", Foreground = new SolidColorBrush(AnimationTimelineConstants.TextColor) };
            summaryDeleteItem.Click += (_, _) => SummaryKeyDeleteRequested?.Invoke(st);
            summaryMenu.Items.Add(summaryDeleteItem);
            summaryMenu.IsOpen = true;
            e.Handled = true;
            return;
        }

        var hit = HitTestKey(p);
        if (hit is not { } h) return;

        _selectedTrackForKey = h.trackIndex;
        _selectedKeyIndex    = h.keyIndex;
        KeySelectionChanged?.Invoke(h.trackIndex, h.keyIndex);
        Redraw();

        var menu = new ContextMenu { Background = new SolidColorBrush(AnimationTimelineConstants.ToolbarBackground) };
        var deleteItem = new MenuItem { Header = "キーを削除", Foreground = new SolidColorBrush(AnimationTimelineConstants.TextColor) };
        deleteItem.Click += (_, _) => KeyDeleteRequested?.Invoke(h.trackIndex, h.keyIndex);
        menu.Items.Add(deleteItem);
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// プレイヘッドを x 座標に応じた時刻へ移動し、スクラブイベントを発火する。
    /// 位置は必ずフレーム境界へスナップする（キーと同じ格子に乗せるため）。
    /// </summary>
    private void ScrubTo(double x)
    {
        var time = ClampAndSnapTime(XToTime(x));
        _playheadTime = time;
        Redraw();
        PlayheadScrubbed?.Invoke(time);
    }
}
