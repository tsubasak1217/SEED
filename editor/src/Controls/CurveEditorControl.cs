// ============================================================
//  CurveEditorControl.cs — xyzw 4チャンネル対応 簡易カーブエディタ
//
//  パーティクルの速度/回転(1ch)・スケール(3ch=xyz)・色(4ch=HSVA) カーブを
//  Canvas 上で編集する UserControl。横軸 t(0..1)・縦軸 v（自動フィット＋余白、
//  最低 0..1）。データモデルは CurveModel.cs（Rust ParamCurve と serde 互換）。
//
//  ── 操作方法 ─────────────────────────────────────────────
//   ・チャンネル切替 : 上部 X/Y/Z/W ボタン。左クリック=アクティブ選択、
//                      右クリック=表示ON/OFF（非表示チャンネルは描画しない）。
//   ・キー移動       : キー点(●)を左ドラッグ。t は隣接キー間にクランプ。
//                      両端キーの t は 0/1 固定（実装判断：端点の並び順崩れを防ぐ）。
//   ・キー追加       : プロット上を左ダブルクリック（アクティブチャンネルへ）。
//   ・キー削除       : キー点を右クリック、または選択して Delete キー。
//                      各チャンネル最低 1 キーは維持する。
//   ・数値編集       : 選択キーの t / v を下部の入力欄で直接編集。
//   ・変更通知       : ドラッグ終了 / 追加 / 削除 / 数値確定で CurveChanged を発火
//                      （ドラッグ中は発火しない＝IPC 毎フレーム送信を避ける）。
//   ・色プレビュー   : HSVA モード時、下部にカーブ評価結果のグラデーションバーを表示。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SEEDEditor.Controls;

/// <summary>
/// パラメータカーブ（1..4ch）の簡易編集 UserControl。
/// 編集確定時に <see cref="CurveChanged"/> を発火する。
/// </summary>
public sealed class CurveEditorControl : UserControl
{
    // ── 見た目・操作の定数（マジックナンバー禁止）────────────────
    private const double PlotHeight      = 130;   // プロット領域の高さ(px)
    private const double PadLeft         = 10;    // プロット左余白
    private const double PadRight        = 10;    // プロット右余白
    private const double PadTop          = 10;    // プロット上余白
    private const double PadBottom       = 10;    // プロット下余白
    private const double KeyRadius       = 4.0;   // キー点の描画半径
    private const double KeySelRadius    = 6.0;   // 選択キーの強調半径
    private const double HitRadius       = 9.0;   // クリック判定半径
    private const double LineThickness   = 1.6;   // 折れ線の太さ
    private const double InactiveOpacity = 0.30;  // 非アクティブチャンネルの不透明度
    private const double GuideOpacity    = 0.35;  // グリッド線の不透明度
    private const double RangeMargin     = 0.10;  // 表示レンジ上下の余白比率
    private const int    GradientStops   = 32;    // グラデーションプレビューのサンプル数
    private const double GradientHeight  = 16;    // グラデーションバーの高さ
    private const int    PlotSamples     = 48;    // 折れ線描画のサンプル数（Smooth/Step を滑らかに表現するため線形補間の直結ではなく Eval サンプリングで描く）

    // チャンネル色（X=赤 / Y=緑 / Z=青 / W=灰）。
    private static readonly Color[] s_channelColors =
    {
        Color.FromRgb(0xE0, 0x55, 0x55), // X 赤
        Color.FromRgb(0x55, 0xC0, 0x55), // Y 緑
        Color.FromRgb(0x55, 0x88, 0xE0), // Z 青
        Color.FromRgb(0xB0, 0xB0, 0xB0), // W 灰
    };
    private static readonly string[] s_channelLabels = { "X", "Y", "Z", "W" };
    // HSVA モード時のチャンネル名（色カーブは HSVA の意味を持つ）。
    private static readonly string[] s_hsvaLabels = { "H", "S", "V", "A" };

    private static readonly Brush s_bgBrush     = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22));
    private static readonly Brush s_guideBrush  = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x60));
    private static readonly Brush s_textBrush   = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly Brush s_borderBrush = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x48));

    // ── 編集対象データ・状態 ─────────────────────────────────
    private readonly ParamCurve _curve;      // 編集中カーブ（受信データのクローンを保持）
    private readonly bool _isHsva;           // 色カーブ(HSVA)なら true → グラデーションバー表示
    private int _activeChannel;              // アクティブ（編集対象）チャンネル
    private readonly bool[] _channelVisible;  // 各チャンネルの表示ON/OFF
    private int _selChannel = -1;            // 選択キーのチャンネル
    private int _selKey      = -1;            // 選択キーのインデックス
    private bool _dragging;                  // ドラッグ中フラグ

    // 表示レンジ（自動フィット結果。y 変換に使う）。
    private double _vMin;
    private double _vMax;

    // ── 子要素 ───────────────────────────────────────────────
    private readonly Canvas _canvas;          // 描画＋操作領域
    private readonly Border _gradientBar;     // グラデーションプレビュー（HSVA 時のみ）
    private readonly TextBox _tBox;           // 選択キー t 入力
    private readonly TextBox _vBox;           // 選択キー v 入力
    private readonly TextBlock _selInfo;      // 選択キー情報ラベル
    private readonly StackPanel _channelBar;  // チャンネルトグルボタン列
    private readonly Button[] _interpButtons; // 補間タイプ切替ボタン（Linear/Smooth/Step）

    /// <summary>編集確定（ドラッグ終了/追加/削除/数値確定）時に発火する。</summary>
    public event EventHandler? CurveChanged;

    /// <summary>編集中カーブ（呼び出し側はこれをシリアライズして送信する）。</summary>
    public ParamCurve Curve => _curve;

    /// <summary>
    /// カーブを与えてエディタを構築する。
    /// </summary>
    /// <param name="curve">編集対象カーブ（内部でクローンして保持する）。</param>
    /// <param name="isHsva">色カーブ(HSVA)なら true。グラデーションバーを表示しラベルを HSVA にする。</param>
    public CurveEditorControl(ParamCurve curve, bool isHsva)
    {
        _curve  = curve.Clone();
        _isHsva = isHsva;

        // 最低 1 チャンネルは保証する（空カーブ受信時の保険）。
        if (_curve.Channels.Count == 0) _curve.Channels.Add(CurveChannel.Constant(1f));

        int chCount = _curve.Channels.Count;
        _channelVisible = new bool[chCount];
        for (int i = 0; i < chCount; i++) _channelVisible[i] = true;
        _activeChannel = 0;

        // ルートは縦積み。チャンネルバー → プロット → グラデ → 選択キー編集行。
        var root = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };

        // ── チャンネルトグルバー ─────────────────────────────
        _channelBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 4),
        };
        BuildChannelButtons();
        root.Children.Add(_channelBar);

        // ── プロット Canvas ─────────────────────────────────
        _canvas = new Canvas
        {
            Height     = PlotHeight,
            Background  = s_bgBrush,
            ClipToBounds = true,
            Focusable   = true, // Delete キーを受けるためフォーカス可能にする
        };
        var canvasBorder = new Border
        {
            BorderBrush     = s_borderBrush,
            BorderThickness = new Thickness(1),
            Child           = _canvas,
        };
        // マウス操作ハンドラ。
        _canvas.MouseLeftButtonDown  += OnCanvasLeftDown;
        _canvas.MouseMove            += OnCanvasMouseMove;
        _canvas.MouseLeftButtonUp    += OnCanvasLeftUp;
        _canvas.MouseRightButtonDown += OnCanvasRightDown;
        _canvas.KeyDown              += OnCanvasKeyDown;
        _canvas.SizeChanged          += (_, _) => Redraw();
        root.Children.Add(canvasBorder);

        // ── グラデーションプレビュー（HSVA のみ）──────────────
        _gradientBar = new Border
        {
            Height          = GradientHeight,
            Margin          = new Thickness(0, 3, 0, 0),
            BorderBrush     = s_borderBrush,
            BorderThickness = new Thickness(1),
            Visibility      = _isHsva ? Visibility.Visible : Visibility.Collapsed,
        };
        root.Children.Add(_gradientBar);

        // ── 選択キー編集行（t / v 入力 + 情報）────────────────
        var editRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _selInfo = new TextBlock
        {
            Text = "キー未選択", Foreground = s_textBrush, FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Width = 70,
        };
        editRow.Children.Add(_selInfo);
        editRow.Children.Add(new TextBlock { Text = "t", Foreground = s_textBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
        _tBox = new TextBox { Width = 56, FontSize = 11, IsEnabled = false };
        editRow.Children.Add(_tBox);
        editRow.Children.Add(new TextBlock { Text = "v", Foreground = s_textBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) });
        _vBox = new TextBox { Width = 56, FontSize = 11, IsEnabled = false };
        editRow.Children.Add(_vBox);
        // t/v 入力の確定（Enter / フォーカス喪失）。
        // PreviewKeyDown で Enter/Space を先取りして消費する（親 Expander のヘッダー
        // ToggleButton がキーボードフォーカス経由で Space/Enter に反応して開閉してしまう
        // ことがあるため。バブリングでの誤トグルを断つ実装判断。CommitNumberEdit 自体は
        // KeyDown ハンドラで従来通り呼ぶ）。
        _tBox.PreviewKeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter or Key.Space or Key.Delete) e.Handled = true; };
        _vBox.PreviewKeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter or Key.Space or Key.Delete) e.Handled = true; };
        _tBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitNumberEdit(); e.Handled = true; } };
        _tBox.LostFocus += (_, _) => CommitNumberEdit();
        _vBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitNumberEdit(); e.Handled = true; } };
        _vBox.LostFocus += (_, _) => CommitNumberEdit();
        root.Children.Add(editRow);

        // ── 補間タイプ切替行（選択キーの区間補間: Linear/Smooth/Step）─────
        var interpRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        interpRow.Children.Add(new TextBlock
        {
            Text = "補間", Foreground = s_textBrush, FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Width = 70,
        });
        _interpButtons = new Button[3];
        var interpValues = new[] { CurveInterp.Linear, CurveInterp.Smooth, CurveInterp.Step };
        var interpLabels = new[] { "Linear", "Smooth", "Step" };
        for (int i = 0; i < 3; i++)
        {
            var interp = interpValues[i];
            var btn = new Button
            {
                Content = interpLabels[i], Width = 50, Height = 18, FontSize = 9,
                Margin = new Thickness(0, 0, 2, 0), IsEnabled = false,
            };
            btn.Click += (_, _) => SetSelectedKeyInterp(interp);
            _interpButtons[i] = btn;
            interpRow.Children.Add(btn);
        }
        root.Children.Add(interpRow);

        // 操作ヒント。
        root.Children.Add(new TextBlock
        {
            Text = "ダブルクリック=追加 / 右クリック=削除 / ドラッグ=移動 / 補間=区間の形",
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
            FontSize = 9, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 2, 0, 0),
        });

        Content = root;
        // 初回描画は Loaded 後（ActualWidth 確定後）に行う。
        Loaded += (_, _) => Redraw();
    }

    // ── チャンネルトグルボタン構築 ───────────────────────────
    private void BuildChannelButtons()
    {
        _channelBar.Children.Clear();
        var labels = _isHsva ? s_hsvaLabels : s_channelLabels;
        for (int i = 0; i < _curve.Channels.Count; i++)
        {
            int idx = i;
            var color = s_channelColors[Math.Min(idx, s_channelColors.Length - 1)];
            var btn = new Button
            {
                Content  = idx < labels.Length ? labels[idx] : (idx + 1).ToString(),
                Width    = 26, Height = 20, FontSize = 11, Margin = new Thickness(0, 0, 3, 0),
                Foreground = Brushes.White,
                Tag      = idx,
                ToolTip  = "左クリック=編集対象 / 右クリック=表示切替",
            };
            UpdateChannelButtonStyle(btn, idx, color);
            btn.Click += (_, _) => { _activeChannel = idx; RefreshChannelButtons(); Redraw(); };
            // 右クリックで表示ON/OFF（アクティブは非表示にしない）。
            btn.MouseRightButtonDown += (_, e) =>
            {
                if (idx != _activeChannel) _channelVisible[idx] = !_channelVisible[idx];
                RefreshChannelButtons();
                Redraw();
                e.Handled = true;
            };
            _channelBar.Children.Add(btn);
        }
    }

    // 全チャンネルボタンの見た目を現在状態に合わせて更新する。
    private void RefreshChannelButtons()
    {
        foreach (var child in _channelBar.Children)
        {
            if (child is Button b && b.Tag is int idx)
            {
                var color = s_channelColors[Math.Min(idx, s_channelColors.Length - 1)];
                UpdateChannelButtonStyle(b, idx, color);
            }
        }
    }

    // 1 ボタンの塗り（アクティブ=チャンネル色塗り、非表示=薄暗、通常=枠のみ）。
    private void UpdateChannelButtonStyle(Button btn, int idx, Color color)
    {
        bool active  = idx == _activeChannel;
        bool visible = _channelVisible[idx];
        if (active)
        {
            btn.Background      = new SolidColorBrush(color);
            btn.BorderBrush     = Brushes.White;
            btn.BorderThickness = new Thickness(1.5);
            btn.Opacity         = 1.0;
        }
        else
        {
            btn.Background      = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3A));
            btn.BorderBrush     = new SolidColorBrush(color);
            btn.BorderThickness = new Thickness(1);
            btn.Opacity         = visible ? 1.0 : 0.4;
        }
    }

    // ── 座標変換 ─────────────────────────────────────────────
    private double PlotWidth  => Math.Max(1, _canvas.ActualWidth  - PadLeft - PadRight);
    private double PlotHgt    => Math.Max(1, _canvas.ActualHeight - PadTop  - PadBottom);

    // t(0..1) → 画面 X。
    private double TtoX(double t) => PadLeft + Math.Clamp(t, 0, 1) * PlotWidth;
    // 画面 X → t(0..1)。
    private double XtoT(double x) => Math.Clamp((x - PadLeft) / PlotWidth, 0, 1);
    // v → 画面 Y（上が大きい値）。
    private double VtoY(double v)
    {
        double span = Math.Max(_vMax - _vMin, 1e-6);
        double n = (v - _vMin) / span;
        return PadTop + (1 - n) * PlotHgt;
    }
    // 画面 Y → v。
    private double YtoV(double y)
    {
        double span = Math.Max(_vMax - _vMin, 1e-6);
        double n = 1 - (y - PadTop) / PlotHgt;
        return _vMin + n * span;
    }

    // 表示レンジを全チャンネルのキーから自動フィットする（最低 0..1、上下に余白）。
    private void RecomputeRange()
    {
        double minV = 0, maxV = 1; // 最低 0..1 を保証
        foreach (var ch in _curve.Channels)
            foreach (var k in ch.Keys)
            {
                if (k.V < minV) minV = k.V;
                if (k.V > maxV) maxV = k.V;
            }
        double margin = (maxV - minV) * RangeMargin;
        _vMin = minV - margin;
        _vMax = maxV + margin;
        if (_vMax - _vMin < 1e-3) { _vMin -= 0.5; _vMax += 0.5; } // 潰れ防止
    }

    // ── 描画 ─────────────────────────────────────────────────
    private void Redraw()
    {
        if (_canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0) return;
        RecomputeRange();
        _canvas.Children.Clear();

        DrawGuides();

        // 非アクティブ→アクティブの順に描き、アクティブを最前面にする。
        for (int c = 0; c < _curve.Channels.Count; c++)
            if (c != _activeChannel && _channelVisible[c]) DrawChannel(c, active: false);
        if (_channelVisible.Length > _activeChannel && _channelVisible[_activeChannel])
            DrawChannel(_activeChannel, active: true);

        UpdateGradient();
        UpdateSelectionUi();
    }

    // グリッド線（t=0/0.5/1 の縦線、v=0/1 の横線）と簡易目盛を描く。
    private void DrawGuides()
    {
        double[] tGuides = { 0.0, 0.5, 1.0 };
        foreach (var t in tGuides)
        {
            double x = TtoX(t);
            _canvas.Children.Add(new Line
            {
                X1 = x, Y1 = PadTop, X2 = x, Y2 = PadTop + PlotHgt,
                Stroke = s_guideBrush, StrokeThickness = 1, Opacity = GuideOpacity,
            });
        }
        // v=0 と v=1 の横線（レンジ内にあるときのみ）。
        foreach (var v in new[] { 0.0, 1.0 })
        {
            if (v < _vMin || v > _vMax) continue;
            double y = VtoY(v);
            _canvas.Children.Add(new Line
            {
                X1 = PadLeft, Y1 = y, X2 = PadLeft + PlotWidth, Y2 = y,
                Stroke = s_guideBrush, StrokeThickness = 1, Opacity = GuideOpacity,
            });
            _canvas.Children.Add(MakeLabel(v.ToString("0.#", CultureInfo.InvariantCulture), PadLeft + 1, y - 12));
        }
    }

    // 1 チャンネルの折れ線＋キー点を描く。
    private void DrawChannel(int c, bool active)
    {
        var ch = _curve.Channels[c];
        var color = s_channelColors[Math.Min(c, s_channelColors.Length - 1)];
        var brush = new SolidColorBrush(color);
        double opacity = active ? 1.0 : InactiveOpacity;

        // 折れ線。Eval() を PlotSamples 点でサンプリングして描く（キー間を直結するのではなく
        // 区間ごとの補間方式（Linear/Smooth/Step）を折れ線の形で正しく反映するため）。
        if (ch.Keys.Count >= 2)
        {
            float tMin = ch.Keys[0].T;
            float tMax = ch.Keys[^1].T;
            var poly = new Polyline
            {
                Stroke = brush, StrokeThickness = LineThickness, Opacity = opacity,
            };
            if (tMax > tMin)
            {
                for (int s = 0; s < PlotSamples; s++)
                {
                    float t = tMin + (tMax - tMin) * s / (PlotSamples - 1);
                    poly.Points.Add(new Point(TtoX(t), VtoY(ch.Eval(t))));
                }
            }
            else
            {
                // 全キー同一 t（潰れたレンジ）: そのまま素通りに 2 点で結ぶ。
                foreach (var k in ch.Keys)
                    poly.Points.Add(new Point(TtoX(k.T), VtoY(k.V)));
            }
            _canvas.Children.Add(poly);
        }

        // キー点。
        for (int i = 0; i < ch.Keys.Count; i++)
        {
            var k = ch.Keys[i];
            bool selected = active && c == _selChannel && i == _selKey;
            double r = selected ? KeySelRadius : KeyRadius;
            var dot = new Ellipse
            {
                Width = r * 2, Height = r * 2,
                Fill = selected ? Brushes.White : brush,
                Stroke = selected ? brush : Brushes.Black,
                StrokeThickness = selected ? 2 : 1,
                Opacity = opacity,
            };
            Canvas.SetLeft(dot, TtoX(k.T) - r);
            Canvas.SetTop(dot, VtoY(k.V) - r);
            _canvas.Children.Add(dot);
        }
    }

    private static TextBlock MakeLabel(string text, double x, double y)
    {
        var tb = new TextBlock { Text = text, Foreground = s_textBrush, FontSize = 9 };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        return tb;
    }

    // グラデーションプレビュー（HSVA モード）を更新する。
    // カーブを t=0..1 でサンプルし、HSVA→RGBA へ変換して線形グラデーションを作る。
    private void UpdateGradient()
    {
        if (!_isHsva) return;
        var stops = new GradientStopCollection();
        for (int i = 0; i < GradientStops; i++)
        {
            float t = i / (float)(GradientStops - 1);
            // H,S,V,A の各チャンネルを評価（欠落チャンネルは既定値で補う）。
            float h = ChannelEval(0, t, 0f);
            float s = ChannelEval(1, t, 1f);
            float v = ChannelEval(2, t, 1f);
            float a = ChannelEval(3, t, 1f);
            var (r, g, b) = ColorSpace.HsvToRgb(h, s, v);
            byte br = (byte)(Math.Clamp(r, 0f, 1f) * 255);
            byte bg = (byte)(Math.Clamp(g, 0f, 1f) * 255);
            byte bb = (byte)(Math.Clamp(b, 0f, 1f) * 255);
            byte ba = (byte)(Math.Clamp(a, 0f, 1f) * 255);
            stops.Add(new GradientStop(Color.FromArgb(ba, br, bg, bb), t));
        }
        _gradientBar.Background = new LinearGradientBrush(stops, new Point(0, 0.5), new Point(1, 0.5));
    }

    // 指定チャンネルを t で評価する。チャンネルが存在しなければ fallback を返す。
    private float ChannelEval(int channel, float t, float fallback)
        => channel < _curve.Channels.Count ? _curve.Channels[channel].Eval(t) : fallback;

    // ── 選択キー編集 UI 同期 ─────────────────────────────────
    private void UpdateSelectionUi()
    {
        var k = SelectedKey();
        if (k == null)
        {
            _selInfo.Text = "キー未選択";
            _tBox.IsEnabled = _vBox.IsEnabled = false;
            _tBox.Text = _vBox.Text = "";
            foreach (var b in _interpButtons) { b.IsEnabled = false; b.FontWeight = FontWeights.Normal; }
            return;
        }
        var labels = _isHsva ? s_hsvaLabels : s_channelLabels;
        string chName = _selChannel < labels.Length ? labels[_selChannel] : (_selChannel + 1).ToString();
        _selInfo.Text = $"{chName} #{_selKey}";
        _tBox.IsEnabled = _vBox.IsEnabled = true;
        _tBox.Text = k.T.ToString("0.###", CultureInfo.InvariantCulture);
        _vBox.Text = k.V.ToString("0.###", CultureInfo.InvariantCulture);

        // 補間ボタン: 末尾キーは区間を持たないため無効化する。それ以外は現在値を強調表示する。
        bool isLastKey = _selChannel >= 0 && _selChannel < _curve.Channels.Count &&
                          _selKey == _curve.Channels[_selChannel].Keys.Count - 1;
        var interpValues = new[] { CurveInterp.Linear, CurveInterp.Smooth, CurveInterp.Step };
        for (int i = 0; i < _interpButtons.Length; i++)
        {
            _interpButtons[i].IsEnabled  = !isLastKey;
            _interpButtons[i].FontWeight = k.Interp == interpValues[i] ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    /// <summary>選択中キーの補間タイプ（区間開始キーとしての Interp）を変更し、再描画・通知する。</summary>
    private void SetSelectedKeyInterp(CurveInterp interp)
    {
        var k = SelectedKey();
        if (k == null || k.Interp == interp) return;
        k.Interp = interp;
        UpdateSelectionUi();
        Redraw();
        RaiseChanged();
    }

    private CurveKey? SelectedKey()
    {
        if (_selChannel < 0 || _selChannel >= _curve.Channels.Count) return null;
        var keys = _curve.Channels[_selChannel].Keys;
        if (_selKey < 0 || _selKey >= keys.Count) return null;
        return keys[_selKey];
    }

    // 数値入力欄の確定。t は隣接キー間 / 端点固定でクランプする。
    private void CommitNumberEdit()
    {
        var keys = SelectedKeys();
        if (keys == null) return;
        var k = keys[_selKey];
        bool changed = false;

        if (float.TryParse(_tBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var newT))
        {
            float clamped = ClampKeyT(keys, _selKey, newT);
            if (Math.Abs(clamped - k.T) > float.Epsilon) { k.T = clamped; changed = true; }
        }
        if (float.TryParse(_vBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var newV))
        {
            if (Math.Abs(newV - k.V) > float.Epsilon) { k.V = newV; changed = true; }
        }
        // 表示を正規化値へ戻す。
        UpdateSelectionUi();
        Redraw();
        if (changed) RaiseChanged();
    }

    private List<CurveKey>? SelectedKeys()
    {
        if (_selChannel < 0 || _selChannel >= _curve.Channels.Count) return null;
        var keys = _curve.Channels[_selChannel].Keys;
        if (_selKey < 0 || _selKey >= keys.Count) return null;
        return keys;
    }

    // キー t のクランプ規則:
    //  ・先頭キー → 0 固定、末尾キー → 1 固定（端点の並び順崩れを防ぐ実装判断）。
    //  ・中間キー → 直前キー t と直後キー t の間（微小マージン込み）にクランプ。
    private static float ClampKeyT(List<CurveKey> keys, int index, float t)
    {
        const float eps = 1e-3f;
        int last = keys.Count - 1;
        if (index == 0)    return 0f;
        if (index == last) return 1f;
        float lo = keys[index - 1].T + eps;
        float hi = keys[index + 1].T - eps;
        if (lo > hi) return keys[index].T; // 隙間が無ければ据え置き
        return Math.Clamp(t, lo, hi);
    }

    // ── マウス操作 ───────────────────────────────────────────
    private void OnCanvasLeftDown(object sender, MouseButtonEventArgs e)
    {
        _canvas.Focus();
        var p = e.GetPosition(_canvas);
        // キャンバス上のクリックは常にここで消費する（ヒットなしの単発クリックも含む）。
        // 消費し忘れると未処理のまま親要素（例: このエディタを内包する Expander）まで
        // バブリングし、意図しない副作用を招きうるため一律で止める。
        e.Handled = true;

        // ダブルクリック=アクティブチャンネルへキー追加。
        if (e.ClickCount == 2)
        {
            AddKeyAt(p);
            return;
        }

        // アクティブチャンネルのキーをヒットテストして選択＋ドラッグ開始。
        int hit = HitTestActiveKey(p);
        if (hit >= 0)
        {
            _selChannel = _activeChannel;
            _selKey = hit;
            _dragging = true;
            _canvas.CaptureMouse();
            UpdateSelectionUi();
            Redraw();
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var keys = SelectedKeys();
        if (keys == null) { _dragging = false; return; }
        var p = e.GetPosition(_canvas);
        var k = keys[_selKey];
        // t は隣接/端点クランプ、v は自由（レンジは次回描画で自動拡張）。
        k.T = ClampKeyT(keys, _selKey, (float)XtoT(p.X));
        k.V = (float)YtoV(p.Y);
        UpdateSelectionUi();
        Redraw();
    }

    private void OnCanvasLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _canvas.ReleaseMouseCapture();
        // ドラッグ確定時のみ通知（毎フレーム送信しない契約）。
        RaiseChanged();
    }

    private void OnCanvasRightDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        int hit = HitTestActiveKey(p);
        if (hit >= 0) DeleteKey(_activeChannel, hit);
        e.Handled = true;
    }

    private void OnCanvasKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _selChannel >= 0 && _selKey >= 0)
        {
            DeleteKey(_selChannel, _selKey);
            e.Handled = true;
            return;
        }
        // Space/Enter がキャンバス上で押されても親 ToggleButton（Expander ヘッダー）まで
        // バブリングして開閉を誘発しないよう、キャンバスにフォーカスがある間は消費する。
        if (e.Key is Key.Space or Key.Return or Key.Enter) e.Handled = true;
    }

    // アクティブチャンネルのキーをヒットテスト（最も近い HitRadius 内のキー）。
    private int HitTestActiveKey(Point p)
    {
        if (_activeChannel >= _curve.Channels.Count) return -1;
        var keys = _curve.Channels[_activeChannel].Keys;
        int best = -1;
        double bestD = HitRadius * HitRadius;
        for (int i = 0; i < keys.Count; i++)
        {
            double dx = TtoX(keys[i].T) - p.X;
            double dy = VtoY(keys[i].V) - p.Y;
            double d = dx * dx + dy * dy;
            if (d <= bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // ポイント位置にアクティブチャンネルへキーを追加する（t 昇順を保つ）。
    private void AddKeyAt(Point p)
    {
        if (_activeChannel >= _curve.Channels.Count) return;
        var keys = _curve.Channels[_activeChannel].Keys;
        float t = (float)XtoT(p.X);
        float v = (float)YtoV(p.Y);

        // 挿入位置（t 昇順）。端点と重複する t は微小にずらして重なりを避ける。
        int insert = keys.Count;
        for (int i = 0; i < keys.Count; i++)
            if (t < keys[i].T) { insert = i; break; }

        keys.Insert(insert, new CurveKey(t, v));
        _selChannel = _activeChannel;
        _selKey = insert;
        UpdateSelectionUi();
        Redraw();
        RaiseChanged();
    }

    // キーを削除する（各チャンネル最低 1 キーを維持）。
    private void DeleteKey(int channel, int index)
    {
        if (channel < 0 || channel >= _curve.Channels.Count) return;
        var keys = _curve.Channels[channel].Keys;
        if (keys.Count <= 1) return; // 最低 1 キー維持
        if (index < 0 || index >= keys.Count) return;
        keys.RemoveAt(index);
        _selChannel = -1;
        _selKey = -1;
        UpdateSelectionUi();
        Redraw();
        RaiseChanged();
    }

    private void RaiseChanged() => CurveChanged?.Invoke(this, EventArgs.Empty);

    // ── ミニプレビュー（Inspector 折りたたみヘッダー用）─────────
    /// <summary>
    /// カーブの小さなプレビュー FrameworkElement を作る。
    /// HSVA カーブはグラデーションバー、それ以外は折れ線ミニチャートで表示する。
    /// </summary>
    public static FrameworkElement BuildMiniPreview(ParamCurve curve, bool isHsva, double width, double height)
    {
        if (isHsva)
        {
            // グラデーションバー（HSVA→RGBA）。
            var stops = new GradientStopCollection();
            for (int i = 0; i < GradientStops; i++)
            {
                float t = i / (float)(GradientStops - 1);
                float h = curve.Channels.Count > 0 ? curve.Channels[0].Eval(t) : 0f;
                float s = curve.Channels.Count > 1 ? curve.Channels[1].Eval(t) : 1f;
                float v = curve.Channels.Count > 2 ? curve.Channels[2].Eval(t) : 1f;
                float a = curve.Channels.Count > 3 ? curve.Channels[3].Eval(t) : 1f;
                var (r, g, b) = ColorSpace.HsvToRgb(h, s, v);
                stops.Add(new GradientStop(
                    Color.FromArgb((byte)(Math.Clamp(a, 0f, 1f) * 255),
                                   (byte)(Math.Clamp(r, 0f, 1f) * 255),
                                   (byte)(Math.Clamp(g, 0f, 1f) * 255),
                                   (byte)(Math.Clamp(b, 0f, 1f) * 255)), t));
            }
            return new Border
            {
                Width = width, Height = height,
                BorderBrush = s_borderBrush, BorderThickness = new Thickness(1),
                Background = new LinearGradientBrush(stops, new Point(0, 0.5), new Point(1, 0.5)),
            };
        }

        // 折れ線ミニチャート（全チャンネルを薄く重ねる）。
        var canvas = new Canvas { Width = width, Height = height, Background = s_bgBrush };
        // レンジ（最低 0..1）。
        double vMin = 0, vMax = 1;
        foreach (var ch in curve.Channels)
            foreach (var k in ch.Keys)
            {
                if (k.V < vMin) vMin = k.V;
                if (k.V > vMax) vMax = k.V;
            }
        double span = Math.Max(vMax - vMin, 1e-6);
        for (int c = 0; c < curve.Channels.Count; c++)
        {
            var ch = curve.Channels[c];
            if (ch.Keys.Count < 2) continue;
            var color = s_channelColors[Math.Min(c, s_channelColors.Length - 1)];
            var poly = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 1.2 };
            // Eval() サンプリングで描く（Smooth/Step の形をミニプレビューにも反映するため）。
            float tMin = ch.Keys[0].T;
            float tMax = ch.Keys[^1].T;
            if (tMax > tMin)
            {
                for (int s = 0; s < PlotSamples; s++)
                {
                    float t = tMin + (tMax - tMin) * s / (PlotSamples - 1);
                    double x = t * width;
                    double y = (1 - (ch.Eval(t) - vMin) / span) * height;
                    poly.Points.Add(new Point(x, y));
                }
            }
            else
            {
                foreach (var k in ch.Keys)
                    poly.Points.Add(new Point(Math.Clamp(k.T, 0, 1) * width, (1 - (k.V - vMin) / span) * height));
            }
            canvas.Children.Add(poly);
        }
        return new Border
        {
            Width = width, Height = height,
            BorderBrush = s_borderBrush, BorderThickness = new Thickness(1),
            Child = canvas,
        };
    }
}
