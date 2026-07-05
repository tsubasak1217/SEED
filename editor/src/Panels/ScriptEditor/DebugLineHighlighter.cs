using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// デバッガが現在停止している行を、背景を赤っぽく塗って強調する背景レンダラー。
///
/// AvalonEdit の <see cref="IBackgroundRenderer"/> として TextView に登録し、
/// <see cref="Line"/>（1 始まり、-1 で無効）を設定して <see cref="ICSharpCode.AvalonEdit.Rendering.TextView.InvalidateLayer"/>
/// を呼ぶと、その行に半透明の赤背景＋左端の赤バーを描く。
/// 選択レイヤーに描くのでテキスト自体は上に重なり読みやすさを保つ。
/// </summary>
public sealed class DebugLineHighlighter : IBackgroundRenderer
{
    /// <summary>行全体を塗る半透明の赤。</summary>
    private static readonly Brush FillBrush = Frozen(Color.FromArgb(0x40, 0xE5, 0x14, 0x00));
    /// <summary>左端の実行位置バー（不透明の赤）。</summary>
    private static readonly Brush BarBrush  = Frozen(Color.FromRgb(0xE5, 0x14, 0x00));
    /// <summary>左端バーの幅（px）。</summary>
    private const double BarWidth = 3.0;

    /// <summary>現在停止している行番号（1 始まり）。-1 で非表示。</summary>
    public int Line { get; set; } = -1;

    /// <summary>背景（テキストの下）に描く。</summary>
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Line < 1 || !textView.VisualLinesValid) return;

        foreach (var v in textView.VisualLines)
        {
            // 折り返しを考慮し、この論理行を含むビジュアル行を対象にする。
            if (v.FirstDocumentLine.LineNumber > Line || Line > v.LastDocumentLine.LineNumber)
                continue;

            double top    = v.VisualTop - textView.VerticalOffset;
            double height = v.Height;
            double width  = textView.ActualWidth;

            drawingContext.DrawRectangle(FillBrush, null, new Rect(0, top, width, height));
            drawingContext.DrawRectangle(BarBrush,  null, new Rect(0, top, BarWidth, height));
            break;
        }
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
