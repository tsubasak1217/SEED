using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Theme;

namespace SEEDEditor.Panels.ScriptEditor.DiskSync;

/// <summary>
/// ディスクから消えたファイルのタブで、エディタ領域の代わりに出す表示。
///
/// バージョン管理でブランチを切り替えると、開いていたスクリプトがそのまま
/// 消えることがある。古い中身を編集できる状態で残しておくと、
/// 「保存したつもりが別ブランチのファイルを復活させていた」事故になるため、
/// 編集させずに「無い」と言い切る。
///
/// ファイルが戻ってきたら、パネル側がこの表示を外してエディタへ戻す。
/// </summary>
public sealed class ScriptMissingFileView : Border
{
    // ── 文言 ────────────────────────────────────────────────
    private const string Title = "ファイルが見つかりません";
    private const string Hint  =
        "ブランチの切り替えやマージで、このファイルがディスク上から無くなりました。"
      + "ファイルが戻れば自動で元の表示に戻ります。";

    // ── 寸法 ────────────────────────────────────────────────
    /// <summary>見出しに添える警告アイコンの一辺（px）。</summary>
    private const double IconSize = 20.0;
    /// <summary>見出しの文字サイズ。</summary>
    private const double TitleFontSize = 14.0;
    /// <summary>パス・補足の文字サイズ。</summary>
    private const double BodyFontSize = 11.0;
    /// <summary>中身の最大幅（長いパスで画面いっぱいに伸びないように）。</summary>
    private const double ContentMaxWidth = 560.0;
    /// <summary>見出し行とパスの間隔。</summary>
    private static readonly Thickness PathMargin = new(0, 10, 0, 0);
    /// <summary>パスと補足の間隔。</summary>
    private static readonly Thickness HintMargin = new(0, 8, 0, 0);
    /// <summary>アイコンと見出し文字の間隔。</summary>
    private static readonly Thickness IconMargin = new(0, 0, 8, 0);

    /// <summary>
    /// 指定パスの「見つかりません」表示を作る。
    /// </summary>
    /// <param name="filePath">見つからないファイルの絶対パス。</param>
    public ScriptMissingFileView(string filePath)
    {
        // エディタ本体と同じ下地にして、タブを切り替えたときのちらつきを避ける
        Background = new SolidColorBrush(SeedThemeColors.WindowSurface);

        var icon = SEEDEditor.Controls.AppIcon.Create("Icon.Warning", IconSize);
        icon.SetBrush(new SolidColorBrush(SeedThemeColors.NoticeBarIcon));
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin            = IconMargin;

        // ［アイコン］［ファイルが見つかりません］
        var heading = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        heading.Children.Add(icon);
        heading.Children.Add(new TextBlock
        {
            Text              = Title,
            Foreground        = new SolidColorBrush(SeedThemeColors.DialogText),
            FontSize          = TitleFontSize,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var body = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            MaxWidth            = ContentMaxWidth,
        };
        body.Children.Add(heading);
        // どのファイルが無いのかを省略せずに出す（フォルダごと消えている場合の切り分け用）
        body.Children.Add(new TextBlock
        {
            Text                = filePath,
            Foreground          = new SolidColorBrush(SeedThemeColors.DialogText),
            FontSize            = BodyFontSize,
            FontFamily          = new FontFamily("Cascadia Mono, Consolas"),
            TextWrapping        = TextWrapping.Wrap,
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = PathMargin,
            ToolTip             = filePath,
        });
        body.Children.Add(new TextBlock
        {
            Text                = Hint,
            Foreground          = new SolidColorBrush(SeedThemeColors.DialogDimText),
            FontSize            = BodyFontSize,
            TextWrapping        = TextWrapping.Wrap,
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = HintMargin,
        });

        Child = body;
        // 読み上げ・自動テストからも「どのファイルが無いか」が分かるようにしておく
        ToolTip = $"{Title}: {Path.GetFileName(filePath)}";
    }
}
