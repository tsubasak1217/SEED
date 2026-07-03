using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// エディタ右側に表示する概観ルーラー（ミニスクロールバー）。
///
/// ドキュメント全体を縦方向に圧縮し、一致箇所（選択単語・検索ヒット）を
/// 色付きマークで表示する。マークをクリックするとその行へスクロールする。
/// VS の「スクロールバー上のマーク」に相当する。
/// </summary>
public sealed class OverviewRuler : FrameworkElement
{
    /// <summary>1 件のマーク（行番号 + 色）。</summary>
    public readonly record struct Mark(int Line, Color Color);

    private readonly TextEditor _editor;
    private List<Mark> _marks = new();

    public OverviewRuler(TextEditor editor)
    {
        _editor = editor;
        Width   = 14;
        Cursor  = Cursors.Hand;
        Background = null; // 描画は OnRender で行う
        MouseLeftButtonDown += OnClick;
    }

    // 背景色（描画用）。FrameworkElement は Background を持たないため自前で保持。
    public Brush? Background { get; set; }

    /// <summary>マーク一覧を差し替えて再描画する。</summary>
    public void SetMarks(IEnumerable<Mark> marks)
    {
        _marks = new List<Mark>(marks);
        InvalidateVisual();
    }

    public void Clear()
    {
        _marks.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // 背景
        var bg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));

        int lineCount = _editor.Document?.LineCount ?? 0;
        if (lineCount <= 0 || ActualHeight <= 0) return;

        double h = ActualHeight;
        foreach (var mark in _marks)
        {
            double y = (mark.Line - 0.5) / lineCount * h;
            var brush = new SolidColorBrush(mark.Color);
            // 幅いっぱいに 2px 帯で描く（視認性優先）
            dc.DrawRectangle(brush, null, new Rect(2, Math.Max(0, y - 1), ActualWidth - 4, 2.5));
        }
    }

    /// <summary>クリック位置に対応する行へエディタをスクロールする。</summary>
    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        int lineCount = _editor.Document?.LineCount ?? 0;
        if (lineCount <= 0 || ActualHeight <= 0) return;

        double ratio = e.GetPosition(this).Y / ActualHeight;
        int line = Math.Clamp((int)Math.Round(ratio * lineCount) + 1, 1, lineCount);
        _editor.ScrollToLine(line);
        var offset = _editor.Document.GetLineByNumber(line).Offset;
        _editor.CaretOffset = offset;
        _editor.Focus();
    }
}
