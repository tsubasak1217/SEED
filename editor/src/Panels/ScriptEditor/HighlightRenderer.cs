using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// 指定オフセット範囲の背景をハイライトする描画レイヤー。
///
/// - 選択単語と一致する箇所のハイライト（薄い塗り + 枠）
/// - 検索一致部分のハイライト（オレンジ）
/// の 2 用途で、エディタごとに 2 インスタンス登録して使う。
/// </summary>
public sealed class HighlightRenderer : IBackgroundRenderer
{
    /// <summary>ハイライト対象範囲（開始オフセット, 長さ）。</summary>
    public List<(int Start, int Length)> Segments { get; } = new();

    private readonly Brush _fill;
    private readonly Pen?  _border;

    public HighlightRenderer(Color fill, byte fillAlpha, Color? border = null)
    {
        _fill = new SolidColorBrush(Color.FromArgb(fillAlpha, fill.R, fill.G, fill.B));
        _fill.Freeze();
        if (border is { } b)
        {
            _border = new Pen(new SolidColorBrush(b), 1.0);
            _border.Freeze();
        }
    }

    /// <summary>ハイライト範囲を差し替える。呼び出し後 TextView.Redraw() が必要。</summary>
    public void SetSegments(IEnumerable<(int Start, int Length)> segments)
    {
        Segments.Clear();
        Segments.AddRange(segments);
    }

    public void Clear() => Segments.Clear();

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Segments.Count == 0 || !textView.VisualLinesValid) return;

        int docLength = textView.Document?.TextLength ?? 0;
        var builder = new BackgroundGeometryBuilder { CornerRadius = 1.5, AlignToWholePixels = true };

        foreach (var (start, length) in Segments)
        {
            if (length <= 0) continue;
            int s = Math.Max(0, start);
            int e = Math.Min(docLength, start + length);
            if (e <= s) continue;
            builder.AddSegment(textView, new ICSharpCode.AvalonEdit.Document.TextSegment
            {
                StartOffset = s,
                EndOffset   = e,
            });
        }

        var geometry = builder.CreateGeometry();
        if (geometry is not null)
            drawingContext.DrawGeometry(_fill, _border, geometry);
    }
}
