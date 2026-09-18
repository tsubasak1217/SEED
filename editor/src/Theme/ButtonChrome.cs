// ============================================================
//  ButtonChrome.cs — ボタンの「状態ごとの色」を差し替え可能にする添付プロパティ
//
//  【役割】
//  ホバー・押下・無効・チェック時の色を、ボタン自身のプロパティとして持たせる。
//  これにより、見た目のバリエーション（主操作 / 危険操作 / リンク風 / アイコン専用）を
//  **ControlTemplate を複製せずに** 色の差し替えだけで作れる。
//
//  【なぜ必要か】
//  WPF の ControlTemplate は「ホバー時の色」を外から受け取る仕組みを持たない。
//  素直に書くとバリエーションの数だけテンプレートを丸ごとコピーすることになり、
//  1 か所直し忘れた版が必ず残る（今回の不具合はまさにそれが各所で起きたもの）。
//  添付プロパティにしておけば、テンプレートは 1 つ・Style は色の表だけになる。
//
//  【使い方】
//  SeedButtonStyles.xaml の各 Style が値を入れ、共通テンプレートの Trigger が読む。
//  画面側のコードや XAML から直接これを設定しないこと
//  （ボタンの見た目は Style を指定して決める。docs/editor_ui_style.md）。
// ============================================================

using System.Windows;
using System.Windows.Media;

namespace SEEDEditor.Theme;

/// <summary>
/// ボタンの状態別の色を保持する添付プロパティ群。
/// 値は <see cref="SeedThemeColors"/>（＝<see cref="SeedColorTable"/>）から作る。
/// </summary>
public static class ButtonChrome
{
    /// <summary>色表の値から凍結済みブラシを作る（凍結するとスレッド間で共有でき、描画も速い）。</summary>
    /// <param name="color">元になる色。</param>
    /// <returns>凍結済みのブラシ。</returns>
    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ── 既定値 ───────────────────────────────────────────────
    //  どの Style も値を入れなかった場合は「通常ボタン」の配色になる。

    /// <summary>ホバー背景の既定値。</summary>
    private static readonly SolidColorBrush DefaultHoverBackground = Frozen(SeedThemeColors.ButtonBgHover);

    /// <summary>押下背景の既定値。</summary>
    private static readonly SolidColorBrush DefaultPressedBackground = Frozen(SeedThemeColors.ButtonBgPressed);

    /// <summary>無効背景の既定値。</summary>
    private static readonly SolidColorBrush DefaultDisabledBackground = Frozen(SeedThemeColors.ButtonBgDisabled);

    /// <summary>ホバー文字色の既定値。</summary>
    private static readonly SolidColorBrush DefaultHoverForeground = Frozen(SeedThemeColors.ButtonFgHover);

    /// <summary>押下文字色の既定値。</summary>
    private static readonly SolidColorBrush DefaultPressedForeground = Frozen(SeedThemeColors.ButtonFg);

    /// <summary>無効文字色の既定値。</summary>
    private static readonly SolidColorBrush DefaultDisabledForeground = Frozen(SeedThemeColors.ButtonFgDisabled);

    /// <summary>チェック時背景の既定値。</summary>
    private static readonly SolidColorBrush DefaultCheckedBackground = Frozen(SeedThemeColors.ToggleBgChecked);

    /// <summary>チェック時かつホバーの背景の既定値。</summary>
    private static readonly SolidColorBrush DefaultCheckedHoverBackground = Frozen(SeedThemeColors.ToggleBgCheckedHover);

    /// <summary>チェック時の文字色の既定値。</summary>
    private static readonly SolidColorBrush DefaultCheckedForeground = Frozen(SeedThemeColors.ToggleFgChecked);

    // ── 添付プロパティ ───────────────────────────────────────

    /// <summary>ホバー時の背景。</summary>
    public static readonly DependencyProperty HoverBackgroundProperty =
        DependencyProperty.RegisterAttached(
            "HoverBackground", typeof(Brush), typeof(ButtonChrome),
            new FrameworkPropertyMetadata(DefaultHoverBackground));

    /// <summary>押下時の背景。</summary>
    public static readonly DependencyProperty PressedBackgroundProperty =
        DependencyProperty.RegisterAttached(
            "PressedBackground", typeof(Brush), typeof(ButtonChrome),
            new FrameworkPropertyMetadata(DefaultPressedBackground));

    /// <summary>無効時の背景。</summary>
    public static readonly DependencyProperty DisabledBackgroundProperty =
        DependencyProperty.RegisterAttached(
            "DisabledBackground", typeof(Brush), typeof(ButtonChrome),
            new FrameworkPropertyMetadata(DefaultDisabledBackground));

    /// <summary>ホバー時の文字・アイコン色。</summary>
    public static readonly DependencyProperty HoverForegroundProperty =
        DependencyProperty.RegisterAttached(
            "HoverForeground", typeof(Brush), typeof(ButtonChrome),
            new FrameworkPropertyMetadata(DefaultHoverForeground));

    /// <summary>押下時の文字・アイコン色。</summary>
    public static readonly DependencyProperty PressedForegroundProperty =
        DependencyProperty.RegisterAttached(
            "PressedForeground", typeof(Brush), typeof(ButtonChrome),
            new FrameworkPropertyMetadata(DefaultPressedForeground));

    /// <summary>無効時の文字・アイコン色。</summary>
    public static readonly DependencyProperty DisabledForegroundProperty =
        DependencyProperty.RegisterAttached(
            "DisabledForeground", typeof(Brush), typeof(ButtonChrome),
            new FrameworkPropertyMetadata(DefaultDisabledForeground));

    /// <summary>チェック（ON）時の背景。ToggleButton のみ意味を持つ。</summary>
    public static readonly DependencyProperty CheckedBackgroundProperty =
        DependencyProperty.RegisterAttached(
            "CheckedBackground", typeof(Brush), typeof(ButtonChrome),
            new FrameworkPropertyMetadata(DefaultCheckedBackground));

    /// <summary>チェック（ON）かつホバー時の背景。ToggleButton のみ意味を持つ。</summary>
    public static readonly DependencyProperty CheckedHoverBackgroundProperty =
        DependencyProperty.RegisterAttached(
            "CheckedHoverBackground", typeof(Brush), typeof(ButtonChrome),
            new FrameworkPropertyMetadata(DefaultCheckedHoverBackground));

    /// <summary>チェック（ON）時の文字・アイコン色。ToggleButton のみ意味を持つ。</summary>
    public static readonly DependencyProperty CheckedForegroundProperty =
        DependencyProperty.RegisterAttached(
            "CheckedForeground", typeof(Brush), typeof(ButtonChrome),
            new FrameworkPropertyMetadata(DefaultCheckedForeground));

    // ── アクセサ（XAML から見えるようにするために必要）──────

    /// <summary>ホバー背景を取得する。</summary>
    /// <param name="element">対象のボタン。</param>
    public static Brush GetHoverBackground(DependencyObject element)
        => (Brush)element.GetValue(HoverBackgroundProperty);

    /// <summary>ホバー背景を設定する。</summary>
    /// <param name="element">対象のボタン。</param>
    /// <param name="value">設定するブラシ。</param>
    public static void SetHoverBackground(DependencyObject element, Brush value)
        => element.SetValue(HoverBackgroundProperty, value);

    /// <summary>押下背景を取得する。</summary>
    /// <param name="element">対象のボタン。</param>
    public static Brush GetPressedBackground(DependencyObject element)
        => (Brush)element.GetValue(PressedBackgroundProperty);

    /// <summary>押下背景を設定する。</summary>
    /// <param name="element">対象のボタン。</param>
    /// <param name="value">設定するブラシ。</param>
    public static void SetPressedBackground(DependencyObject element, Brush value)
        => element.SetValue(PressedBackgroundProperty, value);

    /// <summary>無効背景を取得する。</summary>
    /// <param name="element">対象のボタン。</param>
    public static Brush GetDisabledBackground(DependencyObject element)
        => (Brush)element.GetValue(DisabledBackgroundProperty);

    /// <summary>無効背景を設定する。</summary>
    /// <param name="element">対象のボタン。</param>
    /// <param name="value">設定するブラシ。</param>
    public static void SetDisabledBackground(DependencyObject element, Brush value)
        => element.SetValue(DisabledBackgroundProperty, value);

    /// <summary>ホバー文字色を取得する。</summary>
    /// <param name="element">対象のボタン。</param>
    public static Brush GetHoverForeground(DependencyObject element)
        => (Brush)element.GetValue(HoverForegroundProperty);

    /// <summary>ホバー文字色を設定する。</summary>
    /// <param name="element">対象のボタン。</param>
    /// <param name="value">設定するブラシ。</param>
    public static void SetHoverForeground(DependencyObject element, Brush value)
        => element.SetValue(HoverForegroundProperty, value);

    /// <summary>押下文字色を取得する。</summary>
    /// <param name="element">対象のボタン。</param>
    public static Brush GetPressedForeground(DependencyObject element)
        => (Brush)element.GetValue(PressedForegroundProperty);

    /// <summary>押下文字色を設定する。</summary>
    /// <param name="element">対象のボタン。</param>
    /// <param name="value">設定するブラシ。</param>
    public static void SetPressedForeground(DependencyObject element, Brush value)
        => element.SetValue(PressedForegroundProperty, value);

    /// <summary>無効文字色を取得する。</summary>
    /// <param name="element">対象のボタン。</param>
    public static Brush GetDisabledForeground(DependencyObject element)
        => (Brush)element.GetValue(DisabledForegroundProperty);

    /// <summary>無効文字色を設定する。</summary>
    /// <param name="element">対象のボタン。</param>
    /// <param name="value">設定するブラシ。</param>
    public static void SetDisabledForeground(DependencyObject element, Brush value)
        => element.SetValue(DisabledForegroundProperty, value);

    /// <summary>チェック時背景を取得する。</summary>
    /// <param name="element">対象のボタン。</param>
    public static Brush GetCheckedBackground(DependencyObject element)
        => (Brush)element.GetValue(CheckedBackgroundProperty);

    /// <summary>チェック時背景を設定する。</summary>
    /// <param name="element">対象のボタン。</param>
    /// <param name="value">設定するブラシ。</param>
    public static void SetCheckedBackground(DependencyObject element, Brush value)
        => element.SetValue(CheckedBackgroundProperty, value);

    /// <summary>チェック時ホバー背景を取得する。</summary>
    /// <param name="element">対象のボタン。</param>
    public static Brush GetCheckedHoverBackground(DependencyObject element)
        => (Brush)element.GetValue(CheckedHoverBackgroundProperty);

    /// <summary>チェック時ホバー背景を設定する。</summary>
    /// <param name="element">対象のボタン。</param>
    /// <param name="value">設定するブラシ。</param>
    public static void SetCheckedHoverBackground(DependencyObject element, Brush value)
        => element.SetValue(CheckedHoverBackgroundProperty, value);

    /// <summary>チェック時文字色を取得する。</summary>
    /// <param name="element">対象のボタン。</param>
    public static Brush GetCheckedForeground(DependencyObject element)
        => (Brush)element.GetValue(CheckedForegroundProperty);

    /// <summary>チェック時文字色を設定する。</summary>
    /// <param name="element">対象のボタン。</param>
    /// <param name="value">設定するブラシ。</param>
    public static void SetCheckedForeground(DependencyObject element, Brush value)
        => element.SetValue(CheckedForegroundProperty, value);
}
