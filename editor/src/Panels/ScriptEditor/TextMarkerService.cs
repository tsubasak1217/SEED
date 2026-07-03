using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// AvalonEdit のテキスト上に波線（squiggle）や下線マーカーを描画するサービス。
///
/// エラー・警告を該当範囲に波線表示するために使う。AvalonEdit 公式サンプルの
/// TextMarkerService を本プロジェクト用に簡略化・整理したもの。
///
/// TextView の BackgroundRenderer（波線描画）と LineTransformer（未使用）の
/// 両方に登録して使用する。マーカーは TextDocument の変更に追従して自動更新される。
/// </summary>
public sealed class TextMarkerService : IBackgroundRenderer
{
    private readonly TextDocument _document;
    private readonly List<TextMarker> _markers = new();

    public TextMarkerService(TextDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    // ── マーカー管理 ──────────────────────────────────────────

    /// <summary>指定オフセット範囲にマーカーを追加する。</summary>
    public TextMarker Create(int startOffset, int length, Color color, string? tooltip)
    {
        int textLength = _document.TextLength;
        // ドキュメント範囲内にクランプ（コンパイルとテキスト編集のタイミング差で範囲外になり得る）
        if (startOffset < 0) startOffset = 0;
        if (startOffset > textLength) startOffset = textLength;
        if (length < 1) length = 1;
        if (startOffset + length > textLength) length = Math.Max(1, textLength - startOffset);

        var marker = new TextMarker(startOffset, length)
        {
            MarkerColor = color,
            ToolTip     = tooltip,
        };
        _markers.Add(marker);
        return marker;
    }

    /// <summary>全マーカーを削除する。</summary>
    public void RemoveAll()
    {
        _markers.Clear();
    }

    /// <summary>指定オフセットに重なるマーカーを列挙する（ツールチップ表示用）。</summary>
    public IEnumerable<TextMarker> GetMarkersAtOffset(int offset)
        => _markers.Where(m => m.StartOffset <= offset && offset <= m.EndOffset);

    // ── IBackgroundRenderer ───────────────────────────────────

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_markers.Count == 0 || !textView.VisualLinesValid) return;

        var visualLines = textView.VisualLines;
        if (visualLines.Count == 0) return;

        int viewStart = visualLines.First().FirstDocumentLine.Offset;
        int viewEnd   = visualLines.Last().LastDocumentLine.EndOffset;

        foreach (var marker in _markers)
        {
            if (marker.EndOffset < viewStart || marker.StartOffset > viewEnd) continue;

            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, marker))
            {
                DrawSquiggle(drawingContext, rect, marker.MarkerColor);
            }
        }
    }

    /// <summary>矩形の下辺に沿って波線を描く。</summary>
    private static void DrawSquiggle(DrawingContext ctx, Rect rect, Color color)
    {
        const double step = 4.0; // 波の 1 周期あたりの水平ピクセル
        double y = rect.BottomLeft.Y - 1;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(rect.Left, y), false, false);
            bool up = false;
            for (double x = rect.Left; x < rect.Right; x += step)
            {
                double nextX = Math.Min(x + step, rect.Right);
                double peakY = up ? y - 2 : y;
                g.LineTo(new Point(nextX, peakY), true, false);
                up = !up;
            }
        }
        geometry.Freeze();

        var pen = new Pen(new SolidColorBrush(color), 1.0);
        pen.Freeze();
        ctx.DrawGeometry(null, pen, geometry);
    }
}

/// <summary>
/// テキストマーカー 1 件（波線範囲 + 色 + ツールチップ）。
/// TextSegment を継承しているため TextDocument の編集に追従してオフセットが更新される。
/// </summary>
public sealed class TextMarker : TextSegment
{
    public TextMarker(int startOffset, int length)
    {
        StartOffset = startOffset;
        Length      = length;
    }

    /// <summary>波線の色（エラー=赤、警告=黄など）。</summary>
    public Color MarkerColor { get; init; }

    /// <summary>ホバー時に表示する診断メッセージ。</summary>
    public string? ToolTip { get; init; }
}
