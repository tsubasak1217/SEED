using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Theme;

namespace SEEDEditor.Panels.ScriptEditor.DiskSync;

/// <summary>
/// タブの上部に出す非モーダルの通知帯。
///
/// ディスク上でファイルが変わった／消えたのに、未保存の編集があるため
/// 勝手に上書きできない、という状況を作業を止めずに伝えるためのもの。
/// ダイアログにすると「マージ直後に何十枚も出る」ため、必ず帯で出す。
///
/// 見た目の色は色表（<see cref="SeedColorTable"/>）から取る。
/// ボタンは共通の暗黙スタイル（Theme/SeedButtonStyles.xaml）に任せ、
/// ここで色を決めない。
/// </summary>
public sealed class ScriptDiskNoticeBar : Border
{
    // ── 文言（1 箇所に集約する）────────────────────────────
    private const string ChangedMessage =
        "このファイルはディスク上で変更されました（未保存の編集があるため上書きしていません）。";
    private const string ChangedReloadLabel = "再読み込み（編集を捨てる）";
    private const string ChangedKeepLabel   = "このまま編集を続ける";
    private const string DeletedMessage =
        "このファイルはディスク上から削除されました。保存すると作り直されます。";

    // ── 寸法（マジックナンバーを置かない）──────────────────
    /// <summary>先頭の警告アイコンの一辺（px）。</summary>
    private const double IconSize = 13.0;
    /// <summary>帯の内側の余白（左, 上, 右, 下）。</summary>
    private static readonly Thickness BarPadding = new(8, 4, 8, 4);
    /// <summary>帯の下線の太さ（px）。上のツールバーと二重線にならないよう下だけ引く。</summary>
    private static readonly Thickness BarBorderThickness = new(0, 0, 0, 1);
    /// <summary>アイコンと本文の間隔。</summary>
    private static readonly Thickness IconMargin = new(0, 0, 6, 0);
    /// <summary>本文とボタンの間隔。</summary>
    private static readonly Thickness ButtonMargin = new(8, 0, 0, 0);
    /// <summary>ボタンの内側余白（帯の高さを抑えるため既定より薄くする）。</summary>
    private static readonly Thickness ButtonPadding = new(8, 1, 8, 1);
    /// <summary>帯の文字サイズ（エディタ本文より一回り小さく）。</summary>
    private const double BarFontSize = 11.0;

    /// <summary>警告アイコン（表示のたびに作り直さず使い回す）。</summary>
    private readonly SEEDEditor.Controls.AppIcon _icon;

    /// <summary>通知の本文。</summary>
    private readonly TextBlock _message;

    /// <summary>右端のボタンを載せる箱（通知の種類ごとに中身を入れ替える）。</summary>
    private readonly StackPanel _actions;

    /// <summary>帯を組み立てる。初期状態は非表示。</summary>
    public ScriptDiskNoticeBar()
    {
        Background      = new SolidColorBrush(SeedThemeColors.NoticeBarBg);
        BorderBrush     = new SolidColorBrush(SeedThemeColors.NoticeBarBorder);
        BorderThickness = BarBorderThickness;
        Padding         = BarPadding;
        Visibility      = Visibility.Collapsed;

        _icon = SEEDEditor.Controls.AppIcon.Create("Icon.Warning", IconSize);
        _icon.SetBrush(new SolidColorBrush(SeedThemeColors.NoticeBarIcon));
        _icon.VerticalAlignment = VerticalAlignment.Center;
        _icon.Margin            = IconMargin;

        _message = new TextBlock
        {
            Foreground        = new SolidColorBrush(SeedThemeColors.DialogText),
            FontSize          = BarFontSize,
            TextWrapping      = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _actions = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ［アイコン］［本文（伸びる）］［ボタン群］
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_icon,    0);
        Grid.SetColumn(_message, 1);
        Grid.SetColumn(_actions, 2);
        grid.Children.Add(_icon);
        grid.Children.Add(_message);
        grid.Children.Add(_actions);
        Child = grid;
    }

    /// <summary>
    /// 「ディスク上で変更された（未保存の編集があるので上書きしていない）」を表示する。
    /// </summary>
    /// <param name="reload">［再読み込み（編集を捨てる）］を押したときの処理。</param>
    /// <param name="keepEditing">［このまま編集を続ける］を押したときの処理。</param>
    public void ShowChangedOnDisk(Action reload, Action keepEditing)
    {
        _message.Text = ChangedMessage;
        _actions.Children.Clear();
        _actions.Children.Add(MakeButton(ChangedReloadLabel, reload));
        _actions.Children.Add(MakeButton(ChangedKeepLabel,   keepEditing));
        Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 「ディスク上から削除された（保存すると作り直される）」を表示する。
    /// 編集内容はそのまま残るため、押すべきボタンは無い（保存するか否かは通常の Ctrl+S に任せる）。
    /// </summary>
    public void ShowDeletedOnDisk()
    {
        _message.Text = DeletedMessage;
        _actions.Children.Clear();
        Visibility = Visibility.Visible;
    }

    /// <summary>帯を畳む（通知の前提が無くなったとき）。</summary>
    public void HideNotice()
    {
        _actions.Children.Clear();
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 帯の中のボタンを作る。
    /// 色・ホバーは共通書式（Theme/SeedButtonStyles.xaml）が決めるので、
    /// ここでは寸法と文字サイズだけを与える。
    /// </summary>
    /// <param name="label">ボタンの文言。</param>
    /// <param name="onClick">押したときの処理。</param>
    /// <returns>組み立てたボタン。</returns>
    private static Button MakeButton(string label, Action onClick)
    {
        var button = new Button
        {
            Content  = label,
            Margin   = ButtonMargin,
            Padding  = ButtonPadding,
            FontSize = BarFontSize,
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
