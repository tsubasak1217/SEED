using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SEEDEditor.Panels;

/// <summary>
/// スクリプトエディタで開いているファイルを縦積みで一覧表示する「タブ」パネル。
///
/// - 各行はファイル名 + 未保存マーク(●) + 閉じる(✕)
/// - 行クリックでそのファイルをアクティブ化
/// - アクティブ行はハイライト表示
/// エラー数などのバッジは表示しない（ファイル名のみ）。
/// </summary>
public sealed class OpenDocumentsPanel : UserControl
{
    private static readonly SolidColorBrush Bg       = new(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly SolidColorBrush Text     = new(Color.FromRgb(0xDC, 0xDC, 0xDC));
    private static readonly SolidColorBrush Dim      = new(Color.FromRgb(0x99, 0x99, 0x99));
    private static readonly SolidColorBrush Active   = new(Color.FromRgb(0x09, 0x3A, 0x5E));
    private static readonly SolidColorBrush Accent   = new(Color.FromRgb(0x55, 0xAA, 0xFF));

    private readonly ScriptEditorPanel _editorPanel;
    private readonly StackPanel _list;

    public OpenDocumentsPanel(ScriptEditorPanel editorPanel)
    {
        _editorPanel = editorPanel;
        Background    = Bg;

        _list = new StackPanel();
        Content = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        // エディタ側でドキュメント構成が変わったら一覧を再構築する
        _editorPanel.DocumentsChanged += Refresh;
        Refresh();
    }

    /// <summary>開いているドキュメント一覧を再構築する。</summary>
    private void Refresh()
    {
        _list.Children.Clear();
        var docs = _editorPanel.GetOpenDocuments();
        if (docs.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "開いているファイルはありません",
                Foreground = Dim, FontSize = 11, Margin = new Thickness(8, 8, 8, 8),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var d in docs)
            _list.Children.Add(BuildRow(d));
    }

    /// <summary>1 ファイル分の行を生成する。</summary>
    private UIElement BuildRow(OpenDocInfo doc)
    {
        var row = new Border
        {
            Background = doc.IsActive ? Active : Brushes.Transparent,
            Padding    = new Thickness(8, 4, 6, 4),
            Cursor     = Cursors.Hand,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ファイル名（+ 未保存なら ● を前置）
        var nameStack = new StackPanel { Orientation = Orientation.Horizontal };
        if (doc.IsDirty)
            nameStack.Children.Add(new TextBlock
            {
                Text = "●", Foreground = Accent, FontSize = 9,
                Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        nameStack.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(doc.FilePath),
            Foreground = Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = doc.FilePath,
        });
        Grid.SetColumn(nameStack, 0);
        grid.Children.Add(nameStack);

        // 閉じるボタン
        var close = new TextBlock
        {
            Text = "✕", Foreground = Dim, FontSize = 10, Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 0, 2, 0),
        };
        close.MouseEnter += (_, _) => close.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66));
        close.MouseLeave += (_, _) => close.Foreground = Dim;
        close.MouseLeftButtonDown += (_, e) => { _editorPanel.CloseFile(doc.FilePath); e.Handled = true; };
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);

        row.Child = grid;

        // 行クリックでアクティブ化、中クリックで閉じる
        row.MouseLeftButtonDown += (_, _) => _editorPanel.ActivateFile(doc.FilePath);
        row.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) { _editorPanel.CloseFile(doc.FilePath); e.Handled = true; }
        };
        if (!doc.IsActive)
        {
            row.MouseEnter += (_, _) => { if (row.Background == Brushes.Transparent) row.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)); };
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        }
        return row;
    }
}
