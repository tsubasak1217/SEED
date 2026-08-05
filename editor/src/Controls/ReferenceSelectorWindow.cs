using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SEEDEditor.Controls;

/// <summary>
/// 参照ピッカーの選択ウィンドウ 1 ページ分の内容。
///
/// 1 ページ目は「アクタ内の適合コンポーネント一覧」。
/// 次フェーズの @ref バインディング（コンポーネント選択 → さらに内部の変数選択）では
/// 2 ページ目に「そのコンポーネントの変数一覧」を積む。
/// </summary>
/// <param name="Title">ウィンドウタイトル。</param>
/// <param name="Description">リスト上部の説明文。</param>
/// <param name="Items">選択肢。</param>
internal sealed record ReferenceSelectorPage(string Title, string Description, IReadOnlyList<string> Items);

/// <summary>
/// 参照先を絞り込むための小さな選択ウィンドウ。
///
/// 「1 ページ選んだら次のページを供給する」構造にしてあり、ページを増やすだけで
/// 多段選択（コンポーネント → 変数 …）へ拡張できる。今フェーズでは 1 ページ目
/// （コンポーネント選択）だけを使い、次ページ供給関数は null を渡す。
///
/// 戻り値は「各ページで選んだ項目を順に並べた選択パス」。キャンセル時は null。
/// </summary>
internal sealed class ReferenceSelectorWindow : Window
{
    // ── レイアウト定数（マジックナンバー回避）────────────────────
    private const double WindowWidth       = 320;
    private const double ListMaxHeight     = 180;
    private const double ContentMargin     = 12;
    private const double DescriptionFontSize = 11;

    // ── 配色（エディタ全体のダークテーマに合わせる）──────────────
    private static readonly Brush BrushWindowBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush BrushListBg   = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
    private static readonly Brush BrushText     = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly Brush BrushBorder   = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));

    /// <summary>次ページ供給関数（選択パスを受け取り、続きがなければ null を返す）。</summary>
    private readonly Func<IReadOnlyList<string>, ReferenceSelectorPage?>? _nextPageProvider;

    /// <summary>ここまでに選んだ項目（ページ順）。</summary>
    private readonly List<string> _selectedPath = new();

    /// <summary>戻るボタンで前ページへ戻れるようにするためのページ履歴。</summary>
    private readonly List<ReferenceSelectorPage> _pageHistory = new();

    // ── UI 要素 ────────────────────────────────────────────────
    private readonly TextBlock _description;
    private readonly ListBox   _listBox;
    private readonly Button    _backButton;
    private readonly Button    _okButton;

    /// <summary>確定した選択パス（キャンセル時は null）。</summary>
    private List<string>? _result;

    private ReferenceSelectorWindow(
        Window? owner, ReferenceSelectorPage firstPage,
        Func<IReadOnlyList<string>, ReferenceSelectorPage?>? nextPageProvider)
    {
        _nextPageProvider = nextPageProvider;

        Width                 = WindowWidth;
        SizeToContent         = SizeToContent.Height;
        Owner                 = owner;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        Background            = BrushWindowBg;
        ResizeMode            = ResizeMode.NoResize;

        var root = new StackPanel { Margin = new Thickness(ContentMargin) };

        _description = new TextBlock
        {
            Foreground   = BrushText,
            FontSize     = DescriptionFontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 8),
        };
        root.Children.Add(_description);

        _listBox = new ListBox
        {
            Background  = BrushListBg,
            Foreground  = BrushText,
            BorderBrush = BrushBorder,
            MaxHeight   = ListMaxHeight,
            Margin      = new Thickness(0, 0, 0, 8),
        };
        _listBox.MouseDoubleClick += (_, _) => Advance();
        root.Children.Add(_listBox);

        // 「戻る」はページが 2 枚目以降のときだけ有効（多段選択の拡張用）
        _backButton = new Button
        {
            Content = "← 戻る",
            Padding = new Thickness(10, 4, 10, 4),
            Margin  = new Thickness(0, 0, 6, 0),
        };
        _backButton.Click += (_, _) => GoBack();

        _okButton = new Button
        {
            Content = "選択",
            Padding = new Thickness(12, 4, 12, 4),
        };
        _okButton.Click += (_, _) => Advance();

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(_backButton);
        buttons.Children.Add(_okButton);
        root.Children.Add(buttons);

        Content = root;

        // Enter で確定 / Esc でキャンセル
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Return or Key.Enter) { Advance(); e.Handled = true; }
            else if (e.Key == Key.Escape)         { Close();   e.Handled = true; }
        };

        ShowPage(firstPage);
    }

    /// <summary>
    /// 選択ウィンドウを開き、選択パスを返す（キャンセル時は null）。
    /// </summary>
    /// <param name="owner">親ウィンドウ（中央寄せに使う）。</param>
    /// <param name="firstPage">1 ページ目の内容。</param>
    /// <param name="nextPageProvider">
    /// 次ページ供給関数。選択パスを受け取り、続きのページを返す。
    /// null（既定）または null を返した時点で選択が確定する。
    /// </param>
    public static IReadOnlyList<string>? Show(
        Window? owner, ReferenceSelectorPage firstPage,
        Func<IReadOnlyList<string>, ReferenceSelectorPage?>? nextPageProvider = null)
    {
        var dlg = new ReferenceSelectorWindow(owner, firstPage, nextPageProvider);
        dlg.ShowDialog();
        return dlg._result;
    }

    /// <summary>指定ページを表示する（履歴へ積む）。</summary>
    private void ShowPage(ReferenceSelectorPage page)
    {
        _pageHistory.Add(page);
        Title             = page.Title;
        _description.Text = page.Description;

        _listBox.Items.Clear();
        foreach (var item in page.Items)
            _listBox.Items.Add(new ListBoxItem { Content = item, Foreground = BrushText });
        if (_listBox.Items.Count > 0) _listBox.SelectedIndex = 0;

        // 1 ページ目では戻り先が無いので隠す
        _backButton.Visibility = _pageHistory.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>現在ページの選択を確定し、次ページがあれば進む／無ければウィンドウを閉じる。</summary>
    private void Advance()
    {
        if (_listBox.SelectedItem is not ListBoxItem { Content: string selected }) return;

        _selectedPath.Add(selected);

        var next = _nextPageProvider?.Invoke(_selectedPath);
        if (next is null)
        {
            _result = new List<string>(_selectedPath);
            Close();
            return;
        }
        ShowPage(next);
    }

    /// <summary>1 ページ戻る（多段選択時のみ到達する）。</summary>
    private void GoBack()
    {
        if (_pageHistory.Count <= 1) return;
        _pageHistory.RemoveAt(_pageHistory.Count - 1);          // 現在ページを捨てる
        if (_selectedPath.Count > 0) _selectedPath.RemoveAt(_selectedPath.Count - 1);

        var previous = _pageHistory[^1];
        _pageHistory.RemoveAt(_pageHistory.Count - 1);          // ShowPage が積み直すので一旦外す
        ShowPage(previous);
    }
}
