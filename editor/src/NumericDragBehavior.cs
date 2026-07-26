// ============================================================
//  NumericDragBehavior.cs — ImGui 風ドラッグ数値入力
//
//  TextBox に Enabled="True" をアタッチすると、
//  フィールド上でマウスを左右にドラッグして値を増減できる。
//  クリックのみの場合は通常のテキスト編集モードに入る。
//
//  プロパティ:
//    Sensitivity (double, default=0.1) … 1px あたりの変化量（刻み幅）
//    IsInteger   (bool,   default=false) … true なら最終値を四捨五入して整数化
//
//  コードからのアタッチ（推奨）:
//    NumericDragBehavior.Attach(tb);                             // float, 刻み 0.1
//    NumericDragBehavior.Attach(tb, sensitivity:1.0, isInteger:true); // int, 刻み 1
//
//  ドラッグ中の動作:
//    - マウスカーソルを非表示にする
//    - 画面端に達したら反対端にループして無限スクロールを実現
//    - ドラッグ終了時にカーソルを押下位置へ戻す
//
//  感度修飾:
//    Shift を押しながら … 10 倍速
//    Ctrl  を押しながら … 0.1 倍速（精密入力）
// ============================================================

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SEEDEditor;

/// <summary>
/// ImGui 風の横ドラッグ数値変更動作を TextBox に付与する添付ビヘイビア。
/// </summary>
public static class NumericDragBehavior
{
    // ── Win32 API ────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Win32Point pt);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>Win32 GetCursorPos/SetCursorPos とやり取りするための物理スクリーン座標構造体。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point { public int X, Y; }

    private const int SM_XVIRTUALSCREEN  = 76;
    private const int SM_CXVIRTUALSCREEN = 78;

    // ── 添付プロパティ: Enabled ─────────────────────────────────

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(NumericDragBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) =>
        (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) =>
        obj.SetValue(EnabledProperty, value);

    // ── 添付プロパティ: Sensitivity ─────────────────────────────
    // 1 物理ピクセルあたりの値変化量（刻み幅）。
    // 例: 0.1 → 10px で 1.0 変化、1.0 → 1px で 1 変化

    public static readonly DependencyProperty SensitivityProperty =
        DependencyProperty.RegisterAttached(
            "Sensitivity",
            typeof(double),
            typeof(NumericDragBehavior),
            new PropertyMetadata(0.1));

    public static double GetSensitivity(DependencyObject obj) =>
        (double)obj.GetValue(SensitivityProperty);
    public static void SetSensitivity(DependencyObject obj, double value) =>
        obj.SetValue(SensitivityProperty, value);

    // ── 添付プロパティ: IsInteger ───────────────────────────────
    // true のとき最終値を切り捨て整数として返す。

    public static readonly DependencyProperty IsIntegerProperty =
        DependencyProperty.RegisterAttached(
            "IsInteger",
            typeof(bool),
            typeof(NumericDragBehavior),
            new PropertyMetadata(false));

    public static bool GetIsInteger(DependencyObject obj) =>
        (bool)obj.GetValue(IsIntegerProperty);
    public static void SetIsInteger(DependencyObject obj, bool value) =>
        obj.SetValue(IsIntegerProperty, value);

    // ── 添付プロパティ: Min / Max ───────────────────────────────
    // ドラッグ中の値を [Min, Max] にクランプする。
    // デフォルトは制限なし（NegativeInfinity / PositiveInfinity）。

    public static readonly DependencyProperty MinProperty =
        DependencyProperty.RegisterAttached(
            "Min",
            typeof(double),
            typeof(NumericDragBehavior),
            new PropertyMetadata(double.NegativeInfinity));

    public static double GetMin(DependencyObject obj) =>
        (double)obj.GetValue(MinProperty);
    public static void SetMin(DependencyObject obj, double value) =>
        obj.SetValue(MinProperty, value);

    public static readonly DependencyProperty MaxProperty =
        DependencyProperty.RegisterAttached(
            "Max",
            typeof(double),
            typeof(NumericDragBehavior),
            new PropertyMetadata(double.PositiveInfinity));

    public static double GetMax(DependencyObject obj) =>
        (double)obj.GetValue(MaxProperty);
    public static void SetMax(DependencyObject obj, double value) =>
        obj.SetValue(MaxProperty, value);

    // ── 添付プロパティ: OnDrag ──────────────────────────────────
    // ドラッグで値が変化するたびに呼ばれるコールバック。
    // LostFocusEvent を発火しないため WPF のマウスキャプチャを壊さない。

    public static readonly DependencyProperty OnDragProperty =
        DependencyProperty.RegisterAttached(
            "OnDrag",
            typeof(Action),
            typeof(NumericDragBehavior),
            new PropertyMetadata(null));

    public static Action? GetOnDrag(DependencyObject obj) =>
        (Action?)obj.GetValue(OnDragProperty);
    public static void SetOnDrag(DependencyObject obj, Action? value) =>
        obj.SetValue(OnDragProperty, value);

    // ── 便利メソッド ─────────────────────────────────────────────

    /// <summary>
    /// ドラッグ入力を有効化してプロパティを一括設定する。
    /// onDrag を渡すとドラッグ中にリアルタイムコミットが呼ばれる。
    /// 既に有効化済みの場合もプロパティのみ更新して二重登録を防ぐ。
    /// </summary>
    public static void Attach(
        TextBox tb,
        double  sensitivity = 0.1,
        bool    isInteger   = false,
        Action? onDrag      = null,
        double  min         = double.NegativeInfinity,
        double  max         = double.PositiveInfinity)
    {
        SetSensitivity(tb, sensitivity);
        SetIsInteger(tb, isInteger);
        if (onDrag != null) SetOnDrag(tb, onDrag);
        if (!double.IsNegativeInfinity(min)) SetMin(tb, min);
        if (!double.IsPositiveInfinity(max)) SetMax(tb, max);
        // 既に true の場合は PropertyChanged コールバックが呼ばれないため二重登録されない
        SetEnabled(tb, true);
    }

    // ── 定数 ────────────────────────────────────────────────────

    /// <summary>ドラッグ確定の最小移動量（物理ピクセル）。</summary>
    private const double DragThreshold = 4.0;

    // ── 内部ドラッグ状態 ─────────────────────────────────────────

    private static TextBox?   _target;
    private static Win32Point _startPhysicalPos;
    private static Win32Point _prevPhysicalPos;
    private static double     _startValue;
    private static double     _totalDx;
    private static bool       _didDrag;

    // ── 有効化 / 無効化 ─────────────────────────────────────────

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;

        if ((bool)e.NewValue)
        {
            tb.PreviewMouseDown += OnPreviewMouseDown;
            tb.PreviewMouseMove += OnPreviewMouseMove;
            tb.PreviewMouseUp   += OnPreviewMouseUp;
            tb.LostMouseCapture += OnLostMouseCapture;
            tb.LostFocus        += OnLostFocus;
            tb.Cursor            = Cursors.SizeWE;
        }
        else
        {
            tb.PreviewMouseDown -= OnPreviewMouseDown;
            tb.PreviewMouseMove -= OnPreviewMouseMove;
            tb.PreviewMouseUp   -= OnPreviewMouseUp;
            tb.LostMouseCapture -= OnLostMouseCapture;
            tb.LostFocus        -= OnLostFocus;
            tb.ClearValue(Control.CursorProperty);
        }
    }

    // ── マウスイベントハンドラ ───────────────────────────────────

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (sender is not TextBox tb) return;
        if (tb.IsFocused) return;

        var ic = System.Globalization.CultureInfo.InvariantCulture;
        double.TryParse(tb.Text, System.Globalization.NumberStyles.Float, ic, out double val);

        GetCursorPos(out var pt);
        _target           = tb;
        _startPhysicalPos = pt;
        _prevPhysicalPos  = pt;
        _startValue       = val;
        _totalDx          = 0;
        _didDrag          = false;

        tb.CaptureMouse();
        e.Handled = true;
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_target == null || !_target.IsMouseCaptured) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        GetCursorPos(out var cur);
        var dx = cur.X - _prevPhysicalPos.X;

        if (!_didDrag)
        {
            _totalDx += dx;
            _prevPhysicalPos = cur;
            if (Math.Abs(_totalDx) < DragThreshold) return;

            _didDrag = true;
            Mouse.OverrideCursor = Cursors.None;
        }
        else
        {
            _totalDx        += dx;
            _prevPhysicalPos = cur;
        }

        // 画面端で X 座標をループ（水平方向のみ）
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        const int Margin = 3;
        if (cur.X <= vx + Margin)
        {
            int newX = vx + vw - Margin - 1;
            SetCursorPos(newX, cur.Y);
            _prevPhysicalPos = new Win32Point { X = newX, Y = cur.Y };
        }
        else if (cur.X >= vx + vw - Margin - 1)
        {
            int newX = vx + Margin + 1;
            SetCursorPos(newX, cur.Y);
            _prevPhysicalPos = new Win32Point { X = newX, Y = cur.Y };
        }

        // 感度（刻み）と修飾キーを適用して値を計算する
        double step  = GetSensitivity(_target);
        double mod   = 1.0;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) mod *= 10.0;
        if (Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl))  mod *= 0.1;

        double rawVal  = _startValue + _totalDx * step * mod;
        // Min/Max クランプを適用する
        double minVal  = GetMin(_target);
        double maxVal  = GetMax(_target);
        rawVal = Math.Clamp(rawVal, minVal, maxVal);
        bool   isInt   = GetIsInteger(_target);
        var    ic      = System.Globalization.CultureInfo.InvariantCulture;
        _target.Text   = FormatValue(rawVal, step, isInt, ic);

        // ドラッグ中のリアルタイムコミット（WPFのキャプチャに影響しない直接コールバック）
        GetOnDrag(_target)?.Invoke();

        // NOTE: ドラッグ中は LostFocus を発火しない。
        //       発火すると InspectorPanel の Commit ハンドラがフォーカス移動や
        //       UI 再構築を起こし、マウスキャプチャが解放されてドラッグが中断される。
        //       コミットはドラッグ終了時（OnPreviewMouseUp）に一括で行う。

        e.Handled = true;
    }

    private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // ドラッグ中だった場合は返り値に関わらず必ずカーソルをリセットする
        bool wasDragging = _didDrag;
        if (wasDragging) Mouse.OverrideCursor = null;
        _didDrag = false;

        if (_target == null) return;

        var tb  = _target;
        _target = null;
        tb.ReleaseMouseCapture();

        if (wasDragging)
        {
            SetCursorPos(_startPhysicalPos.X, _startPhysicalPos.Y);
            // ドラッグ完了時に LostFocus を一度だけ発火してコミットを起動する。
            // この時点ではキャプチャ解放済み・_didDrag=false なので
            // OnLostMouseCapture が連鎖してもドラッグ状態を破壊しない。
            tb.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, tb));
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else
        {
            tb.Cursor = Cursors.IBeam;
            tb.Focus();
            tb.SelectAll();
        }
    }

    /// <summary>
    /// マウスキャプチャが強制解放されたとき（ウィンドウフォーカス喪失等）に
    /// カーソルを確実にリセットする。
    /// </summary>
    private static void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_didDrag) return;
        Mouse.OverrideCursor = null;
        _didDrag = false;
        _target  = null;
    }

    private static void OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && GetEnabled(tb))
            tb.Cursor = Cursors.SizeWE;
    }

    // ── 値フォーマット ───────────────────────────────────────────

    /// <summary>
    /// 値を表示用にフォーマットする。
    /// - isInteger = true  : Math.Round で整数化して表示
    /// - isInteger = false : step から小数桁数を自動決定して表示
    /// </summary>
    private static string FormatValue(
        double val, double step, bool isInteger,
        System.Globalization.CultureInfo ic)
    {
        // 仕様: int は小数切り捨て（Math.Floor = 負方向への切り捨て）
        if (isInteger)
            return ((long)Math.Floor(val)).ToString(ic);

        // step から表示桁数を決定する（例: step=0.1 → 1桁、step=0.01 → 2桁）
        int decimals = DecimalPlacesFromStep(step);
        return val.ToString("F" + decimals, ic);
    }

    /// <summary>刻み幅から適切な小数桁数を返す。</summary>
    private static int DecimalPlacesFromStep(double step)
    {
        if (step >= 1.0)   return 0;
        if (step >= 0.1)   return 1;
        if (step >= 0.01)  return 2;
        if (step >= 0.001) return 3;
        return 4;
    }
}
