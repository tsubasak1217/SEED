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

    /// <summary>タブ行に添えるアイコン（鍵・未保存・閉じる）の一辺サイズ（px）。</summary>
    private const double TabIconSize = 11.0;

    /// <summary>
    /// タブ名の前へ置く小さなアイコン（読み取り専用の鍵・未保存マーク）を作る。
    /// </summary>
    /// <param name="iconKey">Icons.xaml のリソースキー。</param>
    /// <param name="brush">アイコンの塗り色。</param>
    private static SEEDEditor.Controls.AppIcon MakeTabIcon(string iconKey, Brush brush)
    {
        var icon = SEEDEditor.Controls.AppIcon.Create(iconKey, TabIconSize);
        icon.SetBrush(brush);
        icon.Margin            = new Thickness(0, 0, 4, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        return icon;
    }

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

        // ファイル名（+ 未保存なら未保存マーク / 読み取り専用なら鍵アイコンを前置）
        var nameStack = new StackPanel { Orientation = Orientation.Horizontal };
        if (doc.IsReadOnly)
            nameStack.Children.Add(MakeTabIcon(
                "Icon.Lock", new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x2E))));
        if (doc.IsDirty)
            nameStack.Children.Add(MakeTabIcon("Icon.Dirty", Accent));
        nameStack.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(doc.FilePath),
            Foreground = Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = doc.IsReadOnly ? $"{doc.FilePath}（読み取り専用・エンジン API）" : doc.FilePath,
        });
        Grid.SetColumn(nameStack, 0);
        grid.Children.Add(nameStack);

        // 閉じるボタン
        var close = SEEDEditor.Controls.AppIcon.Create("Icon.Close", TabIconSize);
        close.SetBrush(Dim);
        close.Cursor            = Cursors.Hand;
        close.VerticalAlignment = VerticalAlignment.Center;
        close.Margin            = new Thickness(4, 0, 2, 0);
        close.MouseEnter += (_, _) => close.SetBrush(new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)));
        close.MouseLeave += (_, _) => close.SetBrush(Dim);
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
