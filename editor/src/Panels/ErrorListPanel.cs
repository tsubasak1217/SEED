using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SEEDEditor.Panels;

/// <summary>
/// スクリプトのエラー・警告を一覧表示する独立パネル（VS の「エラー一覧」相当）。
///
/// - ヘッダーにエラー数・警告数を表示
/// - 各行はアイコン + コード + メッセージ + ファイル:行
/// - 行ダブルクリックで該当箇所へジャンプ
/// - Shift/Ctrl クリックで複数選択、Ctrl+C で選択行を一括コピー
/// スクリプトエディタの DiagnosticsChanged イベントで自動更新される。
/// </summary>
public sealed class ErrorListPanel : UserControl
{
    private static readonly SolidColorBrush Bg     = new(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly SolidColorBrush HeaderBg = new(Color.FromRgb(0x2D, 0x2D, 0x2E));
    private static readonly SolidColorBrush Text   = new(Color.FromRgb(0xDC, 0xDC, 0xDC));
    private static readonly SolidColorBrush Dim    = new(Color.FromRgb(0x99, 0x99, 0x99));
    private static readonly SolidColorBrush ErrorC = new(Color.FromRgb(0xF4, 0x47, 0x47));
    private static readonly SolidColorBrush WarnC  = new(Color.FromRgb(0xD7, 0xBA, 0x36));
    private static readonly SolidColorBrush SelBg  = new(Color.FromRgb(0x09, 0x3A, 0x5E));

    private readonly ScriptEditorPanel _editorPanel;
    private readonly TextBlock _counts;
    private readonly ListBox _list;

    public ErrorListPanel(ScriptEditorPanel editorPanel)
    {
        _editorPanel = editorPanel;
        Background    = Bg;

        _counts = new TextBlock { Foreground = Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var header = new Border
        {
            Background = HeaderBg,
            Padding    = new Thickness(8, 4, 8, 4),
            Child      = _counts,
        };

        // ListBox にすることで Shift/Ctrl 複数選択と Ctrl+C コピーを実現する
        _list = new ListBox
        {
            Background      = Bg,
            BorderThickness = new Thickness(0),
            Foreground      = Text,
            SelectionMode   = SelectionMode.Extended,
            ItemContainerStyle = BuildItemStyle(),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        _list.KeyDown += OnListKeyDown;
        _list.MouseDoubleClick += OnListDoubleClick;

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_list);
        Content = root;

        _editorPanel.DiagnosticsChanged += Refresh;
        Refresh();
    }

    /// <summary>ListBoxItem のスタイル（余白除去・選択色をダークテーマに合わせる）を生成する。</summary>
    private static Style BuildItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

        // 選択時の背景色をダークブルーに上書きする
        var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, SelBg));
        selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        style.Triggers.Add(selectedTrigger);
        return style;
    }

    /// <summary>診断一覧を再構築する。</summary>
    private void Refresh()
    {
        var diags = _editorPanel.GetDiagnostics();
        int errors   = diags.Count(d => d.IsError);
        int warnings = diags.Count(d => !d.IsError);
        _counts.Text = $"⊘ {errors} エラー    △ {warnings} 警告";

        _list.Items.Clear();
        if (diags.Count == 0)
        {
            _list.Items.Add(new ListBoxItem
            {
                Content    = new TextBlock { Text = "エラー・警告はありません", Foreground = Dim, FontSize = 11 },
                Padding    = new Thickness(8, 8, 8, 8),
                IsHitTestVisible = false,
            });
            return;
        }
        // エラー→警告、ファイル・行順で並べる
        foreach (var d in diags.OrderByDescending(x => x.IsError).ThenBy(x => x.FilePath).ThenBy(x => x.Line))
            _list.Items.Add(BuildRow(d));
    }

    /// <summary>診断 1 件分の行（ListBoxItem）を生成する。Tag に診断本体を保持する。</summary>
    private ListBoxItem BuildRow(ScriptDiagnostic d)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };

        sp.Children.Add(new TextBlock
        {
            Text = d.IsError ? "⊘" : "△",
            Foreground = d.IsError ? ErrorC : WarnC,
            Width = 18, VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = d.Id, Foreground = Dim, Width = 64, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = d.Message, Foreground = Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 640,
        });
        sp.Children.Add(new TextBlock
        {
            Text = $"  {Path.GetFileName(d.FilePath)}:{d.Line}",
            Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
        });

        return new ListBoxItem
        {
            Content = sp,
            Padding = new Thickness(8, 2, 8, 2),
            Tag     = d,
        };
    }

    /// <summary>ダブルクリックで選択中の診断へジャンプする。</summary>
    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_list.SelectedItem is ListBoxItem { Tag: ScriptDiagnostic d })
            _editorPanel.GoToDiagnostic(d);
    }

    /// <summary>Ctrl+C で選択中の診断をまとめてクリップボードへコピーする。</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        var sb = new StringBuilder();
        foreach (var item in _list.SelectedItems.OfType<ListBoxItem>())
            if (item.Tag is ScriptDiagnostic d)
                sb.AppendLine(FormatDiagnostic(d));

        if (sb.Length > 0)
        {
            Clipboard.SetText(sb.ToString().TrimEnd());
            e.Handled = true;
        }
    }

    /// <summary>診断 1 件を VS 互換のテキスト形式へ整形する。</summary>
    private static string FormatDiagnostic(ScriptDiagnostic d) =>
        $"{Path.GetFileName(d.FilePath)}({d.Line},{d.Column}): " +
        $"{(d.IsError ? "error" : "warning")} {d.Id}: {d.Message}";
}
