using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Theme;

namespace SEEDEditor.Controls;

/// <summary>
/// ビューポート（シーンパネル）の上に出す「閲覧専用」の帯。
///
/// 端末の一時停止の写しをシーンパネルに出している間（docs/android.md §20.17）、
/// いま見ているのが編集中のシーンではなく端末の写しで、保存・編集できないことを伝える。
/// 帯はビューポート本体（HwndHost）とは別の行に置く（WPF の要素は HwndHost の上に描けないため。Airspace）。
///
/// 見た目の色はスクリプトエディタの通知帯と同じ色表（<see cref="SeedColorTable"/> の NOTICE_BAR_*）から取る
/// （警告だが作業〈閲覧〉は続けられる、という同じ強さ）。文言は判断側（EditorReadOnlyPolicy）が決め、ここは出すだけ。
/// </summary>
public sealed class ReadOnlyViewBanner : Border
{
    // ── 寸法（マジックナンバーを置かない）──────────────────

    /// <summary>先頭のアイコンの一辺（px）。</summary>
    private const double IconSize = 14.0;

    /// <summary>帯の内側の余白（左, 上, 右, 下）。</summary>
    private static readonly Thickness BarPadding = new(10, 4, 10, 4);

    /// <summary>帯の下線の太さ（px）。上のタブバーと二重線にならないよう下だけ引く。</summary>
    private static readonly Thickness BarBorderThickness = new(0, 0, 0, 1);

    /// <summary>アイコンと本文の間隔。</summary>
    private static readonly Thickness IconMargin = new(0, 0, 8, 0);

    /// <summary>帯の文字サイズ（タブバーの文字と同じくらい）。</summary>
    private const double BarFontSize = 12.0;

    /// <summary>先頭のアイコン（閲覧専用＝鍵）。</summary>
    private const string IconKey = "Icon.Lock";

    /// <summary>本文。</summary>
    private readonly TextBlock _message;

    /// <summary>帯を組み立てる。初期状態は非表示。</summary>
    public ReadOnlyViewBanner()
    {
        Background      = new SolidColorBrush(SeedThemeColors.NoticeBarBg);
        BorderBrush     = new SolidColorBrush(SeedThemeColors.NoticeBarBorder);
        BorderThickness = BarBorderThickness;
        Padding         = BarPadding;
        Visibility      = Visibility.Collapsed;

        var icon = AppIcon.Create(IconKey, IconSize);
        icon.SetBrush(new SolidColorBrush(SeedThemeColors.NoticeBarIcon));
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin            = IconMargin;

        _message = new TextBlock
        {
            Foreground        = new SolidColorBrush(SeedThemeColors.DialogText),
            FontSize          = BarFontSize,
            TextWrapping      = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ［アイコン］［本文（伸びる）］
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(_message, 1);
        grid.Children.Add(icon);
        grid.Children.Add(_message);
        Child = grid;
    }

    /// <summary>
    /// 文言を当てる（null・空なら隠す）。
    /// </summary>
    /// <param name="text">帯の文言（EditorReadOnlyPolicy / AndroidViewportPolicy が決めたもの）。</param>
    public void Show(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        _message.Text = text;
        ToolTip = text;
        Visibility = Visibility.Visible;
    }
}
