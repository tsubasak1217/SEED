using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// スクリプトエディタ用の検索・置換バー。
///
/// Ctrl+F で検索モード、Ctrl+H で置換モードを表示する。
/// - Enter / 次へ: 次の一致へ移動（末尾で先頭に折り返す）
/// - 大文字小文字の区別トグル
/// - 置換 / すべて置換
/// 対象エディタは SetTarget() で切り替える（タブ切り替え時に更新）。
/// </summary>
public sealed class FindReplaceBar : Border
{
    private static readonly SolidColorBrush Bg     = new(Color.FromRgb(0x25, 0x25, 0x26));
    private static readonly SolidColorBrush FieldBg= new(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly SolidColorBrush Text   = new(Color.FromRgb(0xDC, 0xDC, 0xDC));
    private static readonly SolidColorBrush Border2= new(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly SolidColorBrush Dim    = new(Color.FromRgb(0x99, 0x99, 0x99));

    private readonly TextBox   _findBox;
    private readonly TextBox   _replaceBox;
    private readonly CheckBox  _caseBox;
    private readonly TextBlock _statusText;
    private readonly StackPanel _replaceRow;

    private TextEditor? _editor;

    /// <summary>検索語・大文字小文字設定・表示状態が変わったときに発火する（ハイライト更新用）。</summary>
    public event Action? SearchChanged;

    /// <summary>現在の検索語。</summary>
    public string SearchTerm => _findBox.Text;

    /// <summary>大文字小文字を区別するか。</summary>
    public bool MatchCase => _caseBox.IsChecked == true;

    /// <summary>検索バーが表示され、かつ検索語が入力されているか。</summary>
    public bool IsSearching => Visibility == Visibility.Visible && !string.IsNullOrEmpty(_findBox.Text);

    public FindReplaceBar()
    {
        Background      = Bg;
        BorderBrush     = Border2;
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding         = new Thickness(6, 4, 6, 4);
        Visibility      = Visibility.Collapsed;

        _findBox    = MakeTextBox();
        _replaceBox = MakeTextBox();
        _statusText = new TextBlock { Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _caseBox    = new CheckBox { Content = "Aa", Foreground = Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), ToolTip = "大文字小文字を区別" };

        // 検索行
        var findRow = new StackPanel { Orientation = Orientation.Horizontal };
        findRow.Children.Add(new TextBlock { Text = "検索", Foreground = Dim, FontSize = 11, Width = 40, VerticalAlignment = VerticalAlignment.Center });
        findRow.Children.Add(_findBox);
        findRow.Children.Add(MakeButton("<", "前へ (Shift+Enter)", () => FindNext(back: true)));
        findRow.Children.Add(MakeButton(">", "次へ (Enter)",       () => FindNext(back: false)));
        findRow.Children.Add(_caseBox);
        findRow.Children.Add(MakeButton("✕", "閉じる (Esc)", Hide));
        findRow.Children.Add(_statusText);

        // 置換行（Ctrl+H のときのみ表示）
        _replaceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _replaceRow.Children.Add(new TextBlock { Text = "置換", Foreground = Dim, FontSize = 11, Width = 40, VerticalAlignment = VerticalAlignment.Center });
        _replaceRow.Children.Add(_replaceBox);
        _replaceRow.Children.Add(MakeButton("置換", "現在の一致を置換", ReplaceCurrent));
        _replaceRow.Children.Add(MakeButton("すべて", "すべて置換", ReplaceAll));

        var col = new StackPanel();
        col.Children.Add(findRow);
        col.Children.Add(_replaceRow);
        Child = col;

        _findBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)      { FindNext(back: (Keyboard.Modifiers & ModifierKeys.Shift) != 0); e.Handled = true; }
            else if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        };
        _replaceBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        };

        // 検索語・大文字小文字が変わるたびにハイライト更新を通知する
        _findBox.TextChanged += (_, _) => SearchChanged?.Invoke();
        _caseBox.Checked     += (_, _) => SearchChanged?.Invoke();
        _caseBox.Unchecked   += (_, _) => SearchChanged?.Invoke();
    }

    /// <summary>操作対象のエディタを設定する（タブ切り替え時に呼ぶ）。</summary>
    public void SetTarget(TextEditor? editor) => _editor = editor;

    /// <summary>検索モードで表示する。選択テキストがあれば検索欄に入れる。</summary>
    public void ShowFind()
    {
        _replaceRow.Visibility = Visibility.Collapsed;
        Show();
    }

    /// <summary>置換モードで表示する。</summary>
    public void ShowReplace()
    {
        _replaceRow.Visibility = Visibility.Visible;
        Show();
    }

    private void Show()
    {
        if (_editor is not null && _editor.SelectionLength > 0 && !_editor.SelectedText.Contains('\n'))
            _findBox.Text = _editor.SelectedText;
        Visibility = Visibility.Visible;
        _findBox.Focus();
        _findBox.SelectAll();
        _statusText.Text = "";
        SearchChanged?.Invoke();
    }

    /// <summary>バーを閉じてフォーカスをエディタへ戻す。</summary>
    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        _editor?.Focus();
        SearchChanged?.Invoke();
    }

    // ── 検索・置換ロジック ────────────────────────────────────

    private StringComparison Comparison =>
        _caseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private void FindNext(bool back)
    {
        if (_editor is null || string.IsNullOrEmpty(_findBox.Text)) return;
        var text    = _editor.Text;
        var keyword  = _findBox.Text;

        int found;
        if (back)
        {
            int start = _editor.SelectionStart - 1;
            if (start < 0) start = text.Length - 1;
            found = start >= 0 ? text.LastIndexOf(keyword, Math.Min(start, text.Length - 1), Comparison) : -1;
            if (found < 0) found = text.LastIndexOf(keyword, Comparison); // 先頭へ折り返し
        }
        else
        {
            int start = _editor.SelectionStart + Math.Max(_editor.SelectionLength, 1);
            if (start > text.Length) start = 0;
            found = text.IndexOf(keyword, Math.Min(start, text.Length), Comparison);
            if (found < 0) found = text.IndexOf(keyword, 0, Comparison); // 末尾から折り返し
        }

        if (found < 0)
        {
            _statusText.Text = "見つかりません";
            return;
        }
        _statusText.Text = "";
        _editor.Select(found, keyword.Length);
        _editor.ScrollToLine(_editor.Document.GetLineByOffset(found).LineNumber);
    }

    private void ReplaceCurrent()
    {
        if (_editor is null || string.IsNullOrEmpty(_findBox.Text)) return;
        // 現在の選択が検索語と一致していれば置換し、次を検索する
        if (_editor.SelectionLength > 0 &&
            string.Equals(_editor.SelectedText, _findBox.Text, Comparison))
        {
            _editor.SelectedText = _replaceBox.Text;
        }
        FindNext(back: false);
    }

    private void ReplaceAll()
    {
        if (_editor is null || string.IsNullOrEmpty(_findBox.Text)) return;
        var text    = _editor.Text;
        var keyword  = _findBox.Text;
        int count = 0;
        int idx = 0;
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            int next = text.IndexOf(keyword, idx, Comparison);
            if (next < 0) { sb.Append(text, idx, text.Length - idx); break; }
            sb.Append(text, idx, next - idx);
            sb.Append(_replaceBox.Text);
            idx = next + keyword.Length;
            count++;
        }
        if (count > 0) _editor.Document.Text = sb.ToString();
        _statusText.Text = $"{count} 件置換しました";
    }

    // ── ウィジェット生成 ──────────────────────────────────────

    private static TextBox MakeTextBox() => new()
    {
        Width           = 200,
        Background      = FieldBg,
        Foreground      = Text,
        CaretBrush      = Text,
        BorderBrush     = Border2,
        BorderThickness = new Thickness(1),
        FontSize        = 12,
        Padding         = new Thickness(3, 1, 3, 1),
        Margin          = new Thickness(2, 0, 2, 0),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static Button MakeButton(string content, string tooltip, Action onClick)
    {
        var btn = new Button
        {
            Content    = content,
            ToolTip    = tooltip,
            Foreground = Text,
            Background = FieldBg,
            BorderBrush= Border2,
            MinWidth   = 28,
            Margin     = new Thickness(2, 0, 0, 0),
            Padding    = new Thickness(4, 1, 4, 1),
            FontSize   = 12,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
