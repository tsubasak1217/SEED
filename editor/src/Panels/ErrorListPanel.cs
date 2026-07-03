using System;
using System.IO;
using System.Linq;
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

    private readonly ScriptEditorPanel _editorPanel;
    private readonly TextBlock _counts;
    private readonly StackPanel _list;

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

        _list = new StackPanel();
        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(scroll);
        Content = root;

        _editorPanel.DiagnosticsChanged += Refresh;
        Refresh();
    }

    /// <summary>診断一覧を再構築する。</summary>
    private void Refresh()
    {
        var diags = _editorPanel.GetDiagnostics();
        int errors   = diags.Count(d => d.IsError);
        int warnings = diags.Count(d => !d.IsError);
        _counts.Text = $"⊘ {errors} エラー    △ {warnings} 警告";

        _list.Children.Clear();
        if (diags.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "エラー・警告はありません",
                Foreground = Dim, FontSize = 11, Margin = new Thickness(8, 8, 8, 8),
            });
            return;
        }
        // エラー→警告、ファイル・行順で並べる
        foreach (var d in diags.OrderByDescending(x => x.IsError).ThenBy(x => x.FilePath).ThenBy(x => x.Line))
            _list.Children.Add(BuildRow(d));
    }

    /// <summary>診断 1 件分の行を生成する。</summary>
    private UIElement BuildRow(ScriptDiagnostic d)
    {
        var row = new Border { Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand };
        var sp  = new StackPanel { Orientation = Orientation.Horizontal };

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
        row.Child = sp;

        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) _editorPanel.GoToDiagnostic(d);
        };
        return row;
    }
}
