// ============================================================
//  ColorPickerWindow.cs — ダークテーマ HSV カラーピッカーダイアログ
//
//  RGBA 正規化値（0.0〜1.0 リニア）で色を受け取り、編集後の値を返す。
//  内部は sRGB 色空間で表示し、入出力時に linear ↔ sRGB 変換を行う。
//  ・wgpu sRGB サーフェス（linear → sRGB 自動変換）に合わせた補正。
//  ・HSV 色空間での選択（SV 正方形 + H スライダー）、Alpha スライダー、
//    RGBA テキスト入力、Hex 入力（sRGB 値）、現在色/新色プレビュー、
//    スポイト（スクリーンカラーサンプリング）を提供する。
// ============================================================

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace SEEDEditor;

/// <summary>
/// HSV カラーピッカーダイアログ（linear ↔ sRGB 変換付き）。
/// <see cref="ShowDialog(Window, float, float, float, float)"/> はリニア RGBA を受け取り、
/// 結果もリニア RGBA で返す。表示は sRGB 換算値で行うため、ゲーム画面と色が一致する。
/// </summary>
public sealed class ColorPickerWindow : Window
{
    // ── 定数 ─────────────────────────────────────────────────
    /// <summary>SV 正方形の描画サイズ（ピクセル）。</summary>
    private const int SV_SIZE = 200;
    /// <summary>スライダーの高さ（ピクセル）。</summary>
    private const int SLIDER_H = 18;
    /// <summary>カーソル円の半径（ピクセル）。</summary>
    private const double CURSOR_R = 6.0;

    // ── 内部状態（すべて sRGB 空間で保持） ──────────────────────
    /// <summary>色相（0〜360）。</summary>
    private double _hue = 0;
    /// <summary>彩度（0〜1）。</summary>
    private double _sat = 1;
    /// <summary>明度（0〜1）。</summary>
    private double _val = 1;
    /// <summary>不透明度（0〜1）。アルファはガンマ変換しない。</summary>
    private double _alpha = 1;

    private bool _updatingText = false;

    // ── UI 部品 ───────────────────────────────────────────────
    private readonly WriteableBitmap _svBitmap;
    private readonly Image           _svImage;
    private readonly Ellipse         _svCursor;
    private readonly WriteableBitmap _hueBitmap;
    private readonly Image           _hueImage;
    private readonly Rectangle      _hueCursor;
    private readonly WriteableBitmap _alphaBitmap;
    private readonly Image           _alphaImage;
    private readonly Rectangle      _alphaCursor;
    private readonly Border          _previewOld;
    private readonly Border          _previewNew;
    private readonly TextBox         _tbR, _tbG, _tbB, _tbA, _tbHex;

    // ── 結果（リニア値） ──────────────────────────────────────
    /// <summary>OK 時の R 値（0〜1 リニア）。</summary>
    public float ResultR { get; private set; }
    /// <summary>OK 時の G 値（0〜1 リニア）。</summary>
    public float ResultG { get; private set; }
    /// <summary>OK 時の B 値（0〜1 リニア）。</summary>
    public float ResultB { get; private set; }
    /// <summary>OK 時の A 値（0〜1 リニア）。</summary>
    public float ResultA { get; private set; }

    // ── コンストラクタ（リニア値を受け取る） ─────────────────────
    /// <param name="r">入力 R（リニア 0〜1）</param>
    /// <param name="g">入力 G（リニア 0〜1）</param>
    /// <param name="b">入力 B（リニア 0〜1）</param>
    /// <param name="a">入力 A（リニア 0〜1、ガンマ変換なし）</param>
    private ColorPickerWindow(float r, float g, float b, float a)
    {
        Title           = "カラーピッカー";
        ResizeMode      = ResizeMode.NoResize;
        SizeToContent   = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background      = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28));

        // ── リニア入力を sRGB 空間に変換して内部状態に設定 ─────────
        // wgpu は sRGB サーフェスを使用するため、リニア値 → sRGB で表示すると
        // ゲーム上での表示色と一致する。
        float sR = LinearToSrgb(r), sG = LinearToSrgb(g), sB = LinearToSrgb(b);
        RgbToHsv(sR, sG, sB, out _hue, out _sat, out _val);
        _alpha = Math.Clamp(a, 0, 1);

        // ── SV 正方形 ─────────────────────────────────────────
        _svBitmap = new WriteableBitmap(SV_SIZE, SV_SIZE, 96, 96, PixelFormats.Bgra32, null);
        _svImage  = new Image { Width = SV_SIZE, Height = SV_SIZE, Source = _svBitmap, Stretch = Stretch.None };
        _svCursor = new Ellipse
        {
            Width = CURSOR_R * 2, Height = CURSOR_R * 2,
            Stroke = Brushes.White, StrokeThickness = 2,
            Fill = Brushes.Transparent,
        };

        _svImage.MouseDown += SvMouseDown;
        _svImage.MouseMove += SvMouseMove;
        _svImage.MouseUp   += (_, _) => _svImage.ReleaseMouseCapture();

        var svCanvas = new Canvas { Width = SV_SIZE, Height = SV_SIZE };
        svCanvas.Children.Add(_svImage);
        svCanvas.Children.Add(_svCursor);

        // ── 色相スライダー ────────────────────────────────────
        _hueBitmap = new WriteableBitmap(SV_SIZE, SLIDER_H, 96, 96, PixelFormats.Bgra32, null);
        _hueImage  = new Image { Width = SV_SIZE, Height = SLIDER_H, Source = _hueBitmap, Stretch = Stretch.None };
        _hueCursor = new Rectangle
        {
            Width = 4, Height = SLIDER_H,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
            StrokeThickness = 1,
        };

        _hueImage.MouseDown += HueMouseDown;
        _hueImage.MouseMove += HueMouseMove;
        _hueImage.MouseUp   += (_, _) => _hueImage.ReleaseMouseCapture();

        var hueCanvas = new Canvas { Width = SV_SIZE, Height = SLIDER_H };
        hueCanvas.Children.Add(_hueImage);
        hueCanvas.Children.Add(_hueCursor);

        // ── Alpha スライダー ──────────────────────────────────
        _alphaBitmap = new WriteableBitmap(SV_SIZE, SLIDER_H, 96, 96, PixelFormats.Bgra32, null);
        _alphaImage  = new Image { Width = SV_SIZE, Height = SLIDER_H, Source = _alphaBitmap, Stretch = Stretch.None };
        _alphaCursor = new Rectangle
        {
            Width = 4, Height = SLIDER_H,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
            StrokeThickness = 1,
        };

        _alphaImage.MouseDown += AlphaMouseDown;
        _alphaImage.MouseMove += AlphaMouseMove;
        _alphaImage.MouseUp   += (_, _) => _alphaImage.ReleaseMouseCapture();

        var alphaCanvas = new Canvas { Width = SV_SIZE, Height = SLIDER_H };
        alphaCanvas.Children.Add(_alphaImage);
        alphaCanvas.Children.Add(_alphaCursor);

        // ── プレビュー ────────────────────────────────────────
        // 「現在色」は入力リニア値をそのまま ScRgb で表示する（WPF が gamma 補正するため
        // GPU の sRGB 出力と同じ見た目になる）。
        _previewOld = MakePreviewBorder(Color.FromScRgb((float)_alpha, r, g, b));
        _previewNew = MakePreviewBorder(Colors.White);

        var previewRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 4, 0, 0),
        };
        var oldLabel = MakeLabel("現在");
        var newLabel = MakeLabel("新規");
        previewRow.Children.Add(new StackPanel { Children = { oldLabel, _previewOld } });
        previewRow.Children.Add(new StackPanel { Children = { newLabel, _previewNew } });

        // ── テキスト入力 ──────────────────────────────────────
        _tbR   = MakeNumTextBox();
        _tbG   = MakeNumTextBox();
        _tbB   = MakeNumTextBox();
        _tbA   = MakeNumTextBox();
        _tbHex = MakeHexTextBox();

        foreach (var tb in new[] { _tbR, _tbG, _tbB, _tbA })
        {
            tb.LostFocus += (_, _) => ApplyRgbaText();
            tb.KeyDown   += (_, e) => { if (e.Key == Key.Return) ApplyRgbaText(); };
        }
        _tbHex.LostFocus += (_, _) => ApplyHexText();
        _tbHex.KeyDown   += (_, e) => { if (e.Key == Key.Return) ApplyHexText(); };

        var textGrid = BuildTextGrid();

        // ── ボタン行（スポイト / OK / Cancel） ───────────────────
        var btnEye = MakeButton("💉 スポイト", "#4A5A4A");
        var btnOk  = MakeButton("OK",          "#61AFEF");
        var btnCc  = MakeButton("キャンセル",   "#3A3A3A");

        btnEye.Click += (_, _) => StartEyedropper();
        btnOk.Click  += (_, _) =>
        {
            // sRGB 選択値をリニアに変換して結果として返す
            var (nr, ng, nb) = HsvToRgb(_hue, _sat, _val);
            ResultR = SrgbToLinear((float)nr);
            ResultG = SrgbToLinear((float)ng);
            ResultB = SrgbToLinear((float)nb);
            ResultA = (float)_alpha; // alpha はガンマ変換なし
            DialogResult = true;
            Close();
        };
        btnCc.Click += (_, _) => { DialogResult = false; Close(); };

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 8, 0, 0),
        };
        btnRow.Children.Add(btnEye);
        btnRow.Children.Add(btnCc);
        btnRow.Children.Add(btnOk);

        // ── レイアウト組み立て ────────────────────────────────
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(svCanvas);
        sp.Children.Add(new Border { Height = 4 });
        sp.Children.Add(MakeLabel("色相"));
        sp.Children.Add(hueCanvas);
        sp.Children.Add(new Border { Height = 4 });
        sp.Children.Add(MakeLabel("不透明度"));
        sp.Children.Add(alphaCanvas);
        sp.Children.Add(previewRow);
        sp.Children.Add(new Border { Height = 4 });
        sp.Children.Add(textGrid);
        sp.Children.Add(btnRow);

        Content = sp;

        DrawSvBitmap();
        DrawHueBitmap();
        DrawAlphaBitmap();
        UpdateCursors();
        RefreshTextFields();
    }

    // ── 静的エントリポイント ──────────────────────────────────

    /// <summary>
    /// カラーピッカーを開いて結果を返す。入出力はリニア RGBA（0〜1）。
    /// キャンセル時は null。
    /// </summary>
    public static (float r, float g, float b, float a)? ShowDialog(
        Window owner, float r, float g, float b, float a)
    {
        var dlg = new ColorPickerWindow(r, g, b, a) { Owner = owner };
        if (dlg.ShowDialog() == true)
            return (dlg.ResultR, dlg.ResultG, dlg.ResultB, dlg.ResultA);
        return null;
    }

    /// <summary>
    /// UI 色（sRGB）向けのカラーピッカーを開く。ゲーム描画色と異なり
    /// リニア変換を伴わないため、エディタの配色設定など「見たままの色」を
    /// 扱う用途に使う。入力・戻り値ともに sRGB の <see cref="Color"/>（不透明）。
    /// キャンセル時は null。
    /// </summary>
    /// <remarks>
    /// 内部ピッカーはリニア RGBA で動作するため、入力 sRGB → リニアへ変換して渡し、
    /// 結果のリニア → sRGB へ戻すことで、表示・往復ともに sRGB 値を保つ。
    /// </remarks>
    public static Color? ShowDialogSrgb(Window owner, Color srgb)
    {
        // sRGB(0..1) → リニアへ変換してピッカーへ渡す
        float lr = SrgbToLinear(srgb.R / 255f);
        float lg = SrgbToLinear(srgb.G / 255f);
        float lb = SrgbToLinear(srgb.B / 255f);

        var result = ShowDialog(owner, lr, lg, lb, 1f);
        if (result is not { } res) return null;

        // 結果のリニア → sRGB へ戻して 8bit 色にする
        byte r = LinearToByte(res.r);
        byte g = LinearToByte(res.g);
        byte b = LinearToByte(res.b);
        return Color.FromRgb(r, g, b);
    }

    /// <summary>リニア値（0〜1）を sRGB の 8bit 成分へ変換する。</summary>
    private static byte LinearToByte(float linear)
    {
        int v = (int)Math.Round(LinearToSrgb(linear) * 255f);
        return (byte)Math.Clamp(v, 0, 255);
    }

    // ── スポイト ──────────────────────────────────────────────

    /// <summary>
    /// スポイトモードを開始する。半透明のフルスクリーンオーバーレイを表示し、
    /// クリックした位置のスクリーンカラー（sRGB）を取得してピッカーに反映する。
    /// </summary>
    private void StartEyedropper()
    {
        var overlay = new EyedropperOverlay();
        // ピッカーウィンドウを一時的に非アクティブ化して視認性を確保する
        overlay.Owner = this;
        if (overlay.ShowDialog() == true && overlay.Result is { } result)
        {
            // スクリーンから取得した sRGB 値をそのまま HSV に変換する
            RgbToHsv(result.r, result.g, result.b, out _hue, out _sat, out _val);
            DrawSvBitmap();
            DrawAlphaBitmap();
            UpdateCursors();
            RefreshTextFields();
        }
        Activate();
    }

    // ── スクリーンカラーサンプリング（GDI32） ─────────────────────

    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr hdc, int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    /// <summary>物理ピクセル座標のスクリーンカラーを sRGB（0〜1）で返す。</summary>
    private static (float r, float g, float b) SampleScreenPixel(int physX, int physY)
    {
        var hdc   = GetDC(IntPtr.Zero);
        uint col  = GetPixel(hdc, physX, physY);
        ReleaseDC(IntPtr.Zero, hdc);
        // COLORREF: 0x00BBGGRR
        byte rb = (byte)(col & 0xFF);
        byte gb = (byte)((col >> 8) & 0xFF);
        byte bb = (byte)((col >> 16) & 0xFF);
        return (rb / 255f, gb / 255f, bb / 255f);
    }

    // ── フルスクリーンスポイトオーバーレイ ──────────────────────

    /// <summary>
    /// スポイト操作用の半透明フルスクリーンウィンドウ。
    /// マウス追従カラープレビュー付き。クリックで色を確定、Esc でキャンセル。
    /// </summary>
    private sealed class EyedropperOverlay : Window
    {
        /// <summary>確定した sRGB カラー。キャンセル時は null。</summary>
        public (float r, float g, float b)? Result { get; private set; }

        // カーソル近くに表示するプレビュー部品
        private readonly Border    _colorBlock;
        private readonly TextBlock _hexLabel;
        private readonly Canvas    _canvas;

        public EyedropperOverlay()
        {
            WindowStyle           = WindowStyle.None;
            AllowsTransparency    = true;
            // alpha=1 にすることでマウスイベントをキャプチャしつつほぼ透明にする
            Background            = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            Topmost               = true;
            WindowState           = WindowState.Maximized;
            Cursor                = Cursors.Cross;
            ShowInTaskbar         = false;

            // ── カーソル追従プレビュー ──
            _colorBlock = new Border
            {
                Width           = 36,
                Height          = 36,
                BorderBrush     = Brushes.White,
                BorderThickness = new Thickness(2),
            };
            _hexLabel = new TextBlock
            {
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                FontSize   = 10,
                Padding    = new Thickness(3, 1, 3, 1),
            };
            // プレビュー全体を小パネルに格納
            var preview = new StackPanel { Orientation = Orientation.Vertical };
            preview.Children.Add(_colorBlock);
            preview.Children.Add(_hexLabel);

            _canvas = new Canvas();
            _canvas.Children.Add(preview);
            Content = _canvas;

            MouseMove           += OnMouseMove;
            MouseLeftButtonDown += OnMouseDown;
            KeyDown             += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); } };
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var pos  = e.GetPosition(this);
            var (px, py) = ToPhysPixels(pos);
            var (r, g, b) = SampleScreenPixel(px, py);
            _colorBlock.Background = new SolidColorBrush(
                Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255)));
            _hexLabel.Text = $"#{(byte)(r*255):X2}{(byte)(g*255):X2}{(byte)(b*255):X2}";
            // プレビューを右下にオフセット（カーソルと重ならないよう）
            var preview = (FrameworkElement)_canvas.Children[0];
            Canvas.SetLeft(preview, Math.Min(pos.X + 14, ActualWidth  - preview.ActualWidth  - 4));
            Canvas.SetTop( preview, Math.Min(pos.Y + 14, ActualHeight - preview.ActualHeight - 4));
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(this);
            var (px, py) = ToPhysPixels(pos);
            Result       = SampleScreenPixel(px, py);
            DialogResult = true;
            Close();
        }

        /// <summary>WPF 論理ピクセル → 物理ピクセル変換（DPI 対応）。</summary>
        private (int x, int y) ToPhysPixels(Point logical)
        {
            var src = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
            double sx = src?.M11 ?? 1.0;
            double sy = src?.M22 ?? 1.0;
            return ((int)(logical.X * sx), (int)(logical.Y * sy));
        }
    }

    // ── ビットマップ描画 ──────────────────────────────────────

    /// <summary>SV 正方形を現在の色相で再描画する（sRGB 値で描画）。</summary>
    private void DrawSvBitmap()
    {
        int w = SV_SIZE, h = SV_SIZE;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            double v = 1.0 - (double)y / (h - 1);
            for (int x = 0; x < w; x++)
            {
                double s = (double)x / (w - 1);
                var (r, g, b) = HsvToRgb(_hue, s, v);
                int idx = (y * w + x) * 4;
                pixels[idx + 0] = (byte)(b * 255); // BGRA
                pixels[idx + 1] = (byte)(g * 255);
                pixels[idx + 2] = (byte)(r * 255);
                pixels[idx + 3] = 255;
            }
        }
        _svBitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
    }

    /// <summary>色相スライダーを描画する（左=0° 右=360°）。</summary>
    private void DrawHueBitmap()
    {
        int w = SV_SIZE, h = SLIDER_H;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double hue = (double)x / (w - 1) * 360.0;
                var (r, g, b) = HsvToRgb(hue, 1.0, 1.0);
                int idx = (y * w + x) * 4;
                pixels[idx + 0] = (byte)(b * 255);
                pixels[idx + 1] = (byte)(g * 255);
                pixels[idx + 2] = (byte)(r * 255);
                pixels[idx + 3] = 255;
            }
        }
        _hueBitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
    }

    /// <summary>Alpha スライダーを描画する（左=透明 右=不透明）。現在色で着色。</summary>
    private void DrawAlphaBitmap()
    {
        int w = SV_SIZE, h = SLIDER_H;
        var pixels = new byte[w * h * 4];
        var (cr, cg, cb) = HsvToRgb(_hue, _sat, _val);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double t = (double)x / (w - 1);
                bool even = ((x / 6) + (y / 6)) % 2 == 0;
                double bg = even ? 0.6 : 0.4;
                double r = cr * t + bg * (1 - t);
                double g = cg * t + bg * (1 - t);
                double b = cb * t + bg * (1 - t);
                int idx = (y * w + x) * 4;
                pixels[idx + 0] = (byte)(b * 255);
                pixels[idx + 1] = (byte)(g * 255);
                pixels[idx + 2] = (byte)(r * 255);
                pixels[idx + 3] = 255;
            }
        }
        _alphaBitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
    }

    // ── カーソル・プレビュー更新 ──────────────────────────────

    /// <summary>SV / 色相 / Alpha カーソルと新色プレビューを更新する。</summary>
    private void UpdateCursors()
    {
        double svX = _sat * (SV_SIZE - 1);
        double svY = (1.0 - _val) * (SV_SIZE - 1);
        Canvas.SetLeft(_svCursor, svX - CURSOR_R);
        Canvas.SetTop(_svCursor,  svY - CURSOR_R);

        double hx = _hue / 360.0 * (SV_SIZE - 1) - 2;
        Canvas.SetLeft(_hueCursor, Math.Clamp(hx, 0, SV_SIZE - 4));
        Canvas.SetTop(_hueCursor, 0);

        double ax = _alpha * (SV_SIZE - 1) - 2;
        Canvas.SetLeft(_alphaCursor, Math.Clamp(ax, 0, SV_SIZE - 4));
        Canvas.SetTop(_alphaCursor, 0);

        // 新色プレビュー（sRGB 表示）
        var (r, g, b) = HsvToRgb(_hue, _sat, _val);
        _previewNew.Background = new SolidColorBrush(
            Color.FromArgb((byte)(_alpha * 255), (byte)(r * 255), (byte)(g * 255), (byte)(b * 255)));
    }

    /// <summary>テキストフィールドを現在の sRGB 値で更新する。</summary>
    private void RefreshTextFields()
    {
        _updatingText = true;
        var (r, g, b) = HsvToRgb(_hue, _sat, _val);
        _tbR.Text = r.ToString("F3", CultureInfo.InvariantCulture);
        _tbG.Text = g.ToString("F3", CultureInfo.InvariantCulture);
        _tbB.Text = b.ToString("F3", CultureInfo.InvariantCulture);
        _tbA.Text = _alpha.ToString("F3", CultureInfo.InvariantCulture);
        // Hex は sRGB 0〜255 の 16 進表記
        int hr = (int)(r * 255); int hg = (int)(g * 255);
        int hb = (int)(b * 255); int ha = (int)(_alpha * 255);
        _tbHex.Text = $"{hr:X2}{hg:X2}{hb:X2}{ha:X2}";
        _updatingText = false;
    }

    // ── マウスイベント ────────────────────────────────────────

    private void SvMouseDown(object sender, MouseButtonEventArgs e)
    {
        _svImage.CaptureMouse();
        ApplySvPos(e.GetPosition(_svImage));
    }

    private void SvMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            ApplySvPos(e.GetPosition(_svImage));
    }

    private void ApplySvPos(Point p)
    {
        _sat = Math.Clamp(p.X / (SV_SIZE - 1), 0, 1);
        _val = Math.Clamp(1.0 - p.Y / (SV_SIZE - 1), 0, 1);
        DrawAlphaBitmap();
        UpdateCursors();
        RefreshTextFields();
    }

    private void HueMouseDown(object sender, MouseButtonEventArgs e)
    {
        _hueImage.CaptureMouse();
        ApplyHuePos(e.GetPosition(_hueImage));
    }

    private void HueMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            ApplyHuePos(e.GetPosition(_hueImage));
    }

    private void ApplyHuePos(Point p)
    {
        _hue = Math.Clamp(p.X / (SV_SIZE - 1) * 360.0, 0, 360);
        DrawSvBitmap();
        DrawAlphaBitmap();
        UpdateCursors();
        RefreshTextFields();
    }

    private void AlphaMouseDown(object sender, MouseButtonEventArgs e)
    {
        _alphaImage.CaptureMouse();
        ApplyAlphaPos(e.GetPosition(_alphaImage));
    }

    private void AlphaMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            ApplyAlphaPos(e.GetPosition(_alphaImage));
    }

    private void ApplyAlphaPos(Point p)
    {
        _alpha = Math.Clamp(p.X / (SV_SIZE - 1), 0, 1);
        UpdateCursors();
        RefreshTextFields();
    }

    // ── テキスト適用 ──────────────────────────────────────────

    /// <summary>R/G/B/A テキストボックスの sRGB 値を内部状態に反映する。</summary>
    private void ApplyRgbaText()
    {
        if (_updatingText) return;
        if (!TryPF(_tbR, out float r) || !TryPF(_tbG, out float g) ||
            !TryPF(_tbB, out float b) || !TryPF(_tbA, out float a)) return;
        r = Math.Clamp(r, 0, 1); g = Math.Clamp(g, 0, 1);
        b = Math.Clamp(b, 0, 1); a = Math.Clamp(a, 0, 1);
        RgbToHsv(r, g, b, out _hue, out _sat, out _val);
        _alpha = a;
        DrawSvBitmap();
        DrawAlphaBitmap();
        UpdateCursors();
        RefreshTextFields();
    }

    /// <summary>Hex テキストボックスの sRGB 値を内部状態に反映する。RRGGBBAA 形式。</summary>
    private void ApplyHexText()
    {
        if (_updatingText) return;
        string hex = _tbHex.Text.Trim().TrimStart('#');
        if (hex.Length == 6)  hex += "FF";
        if (hex.Length != 8)  return;
        if (!TryParseHexByte(hex, 0, out byte hr) || !TryParseHexByte(hex, 2, out byte hg) ||
            !TryParseHexByte(hex, 4, out byte hb) || !TryParseHexByte(hex, 6, out byte ha)) return;
        RgbToHsv(hr / 255f, hg / 255f, hb / 255f, out _hue, out _sat, out _val);
        _alpha = ha / 255.0;
        DrawSvBitmap();
        DrawAlphaBitmap();
        UpdateCursors();
        RefreshTextFields();
    }

    private static bool TryParseHexByte(string s, int start, out byte val)
        => byte.TryParse(s.Substring(start, 2), NumberStyles.HexNumber, null, out val);

    // ── linear ↔ sRGB 変換 ───────────────────────────────────

    /// <summary>
    /// リニア値 → sRGB 変換（IEC 61966-2-1 準拠）。
    /// wgpu sRGB サーフェスに書き込んだリニア値をピッカーで正確に表示するために使用する。
    /// </summary>
    private static float LinearToSrgb(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        return c <= 0.0031308f
            ? c * 12.92f
            : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
    }

    /// <summary>
    /// sRGB 値 → リニア変換（IEC 61966-2-1 準拠）。
    /// ピッカーで選択した sRGB 値を wgpu シェーダー用リニア値に変換するために使用する。
    /// </summary>
    private static float SrgbToLinear(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        return c <= 0.04045f
            ? c / 12.92f
            : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    // ── HSV ↔ RGB 変換（sRGB 空間） ──────────────────────────

    /// <summary>HSV → RGB 変換（H: 0〜360, S/V: 0〜1 → R/G/B: 0〜1 sRGB）。</summary>
    private static (double r, double g, double b) HsvToRgb(double h, double s, double v)
    {
        if (s == 0) return (v, v, v);
        double hh = (h % 360) / 60.0;
        int    i  = (int)hh;
        double f  = hh - i;
        double p  = v * (1 - s);
        double q  = v * (1 - s * f);
        double t  = v * (1 - s * (1 - f));
        return i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }

    /// <summary>RGB → HSV 変換（R/G/B: 0〜1 sRGB → H: 0〜360, S/V: 0〜1）。</summary>
    private static void RgbToHsv(float r, float g, float b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        v = max;
        s = max == 0 ? 0 : delta / max;
        if (delta == 0) { h = 0; return; }
        if (max == r)      h = 60 * (((g - b) / delta) % 6);
        else if (max == g) h = 60 * ((b - r) / delta + 2);
        else               h = 60 * ((r - g) / delta + 4);
        if (h < 0) h += 360;
    }

    // ── UI ヘルパー ───────────────────────────────────────────

    private static TextBlock MakeLabel(string text)
        => new TextBlock
        {
            Text       = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize   = 10,
            Margin     = new Thickness(0, 2, 0, 1),
        };

    private static Border MakePreviewBorder(Color color)
        => new Border
        {
            Width             = 90,
            Height            = 24,
            Margin            = new Thickness(2, 0, 2, 0),
            BorderBrush       = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness   = new Thickness(1),
            Background        = new SolidColorBrush(color),
        };

    private static TextBox MakeNumTextBox()
        => new TextBox
        {
            Width           = 60,
            Height          = 22,
            Margin          = new Thickness(2),
            Background      = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground      = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            CaretBrush      = Brushes.White,
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            FontSize        = 11,
            Padding         = new Thickness(3, 1, 3, 1),
        };

    private static TextBox MakeHexTextBox()
        => new TextBox
        {
            Width           = 90,
            Height          = 22,
            Margin          = new Thickness(2),
            Background      = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground      = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            CaretBrush      = Brushes.White,
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            FontSize        = 11,
            Padding         = new Thickness(3, 1, 3, 1),
            MaxLength       = 8,
        };

    private static Button MakeButton(string text, string hexColor)
    {
        var col = (Color)ColorConverter.ConvertFromString(hexColor);
        return new Button
        {
            Content         = text,
            Margin          = new Thickness(4, 0, 0, 0),
            Padding         = new Thickness(12, 4, 12, 4),
            Background      = new SolidColorBrush(col),
            Foreground      = Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize        = 12,
        };
    }

    /// <summary>RGBA + Hex テキスト入力グリッドを生成する。</summary>
    private Grid BuildTextGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        void AddRow(int row, string la, TextBox tba, string lb, TextBox tbb)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            var lblA = new TextBlock { Text = la, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            var lblB = new TextBlock { Text = lb, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lblA, row); Grid.SetColumn(lblA, 0); g.Children.Add(lblA);
            Grid.SetRow(tba,  row); Grid.SetColumn(tba,  1); g.Children.Add(tba);
            Grid.SetRow(lblB, row); Grid.SetColumn(lblB, 2); g.Children.Add(lblB);
            Grid.SetRow(tbb,  row); Grid.SetColumn(tbb,  3); g.Children.Add(tbb);
        }

        AddRow(0, "R", _tbR, "G", _tbG);
        AddRow(1, "B", _tbB, "A", _tbA);

        // Hex 行（全幅）
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        var hexLabel = new TextBlock { Text = "Hex", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(hexLabel, 2); Grid.SetColumn(hexLabel, 0); g.Children.Add(hexLabel);
        Grid.SetRow(_tbHex,   2); Grid.SetColumn(_tbHex,   1);
        Grid.SetColumnSpan(_tbHex, 3);
        _tbHex.Width = double.NaN;
        g.Children.Add(_tbHex);

        return g;
    }

    private static bool TryPF(TextBox tb, out float val)
        => float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
}
