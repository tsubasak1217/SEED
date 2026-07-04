using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion;

/// <summary>
/// インライン補完のゴーストテキスト（未確定の予測）を、指定オフセット位置へ
/// 薄いグレーで描画する背景レンダラ。ドキュメントには挿入せず見た目だけ重ねる。
///
/// v1 は 1 行分の予測をカーソル直後に表示する。Tab で確定するとエディタ側が
/// 実際に文字を挿入し、本レンダラはクリアされる。
/// </summary>
public sealed class GhostTextRenderer : IBackgroundRenderer
{
    /// <summary>ゴーストテキストの色（薄いグレー）。</summary>
    private static readonly Brush GhostBrush = CreateFrozen(Color.FromRgb(0x80, 0x80, 0x80));

    private readonly TextEditor _editor;

    /// <summary>表示中の予測テキスト（null または空なら非表示）。</summary>
    private string? _text;
    /// <summary>予測を表示する文書オフセット（通常はカーソル位置）。</summary>
    private int _offset;

    public GhostTextRenderer(TextEditor editor) => _editor = editor;

    /// <summary>カーソルの後ろに描画されるので、テキストより前面の Caret レイヤーに置く。</summary>
    public KnownLayer Layer => KnownLayer.Caret;

    /// <summary>予測テキストを設定する（呼び出し後に TextView.Redraw が必要）。</summary>
    public void SetText(string text, int offset)
    {
        _text   = text;
        _offset = offset;
    }

    /// <summary>予測表示をクリアする。</summary>
    public void Clear() => _text = null;

    /// <summary>現在予測を表示しているか。</summary>
    public bool HasText => !string.IsNullOrEmpty(_text);

    /// <summary>予測テキストの内容（未表示なら null）。</summary>
    public string? Text => _text;

    /// <summary>予測を表示している文書オフセット。</summary>
    public int Offset => _offset;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (string.IsNullOrEmpty(_text)) return;
        if (_offset < 0 || _offset > _editor.Document.TextLength) return;

        textView.EnsureVisualLines();

        // オフセットの視覚位置（テキスト先頭）を求め、スクロール量を差し引く
        var location = _editor.Document.GetLocation(_offset);
        var vpos     = textView.GetVisualPosition(
            new TextViewPosition(location), VisualYPosition.TextTop) - textView.ScrollOffset;

        // エディタと同じフォントで描画する
        var typeface = new Typeface(_editor.FontFamily, _editor.FontStyle, _editor.FontWeight, _editor.FontStretch);
        var dpi      = VisualTreeHelper.GetDpi(_editor).PixelsPerDip;

        var formatted = new FormattedText(
            _text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            _editor.FontSize,
            GhostBrush,
            dpi);

        drawingContext.DrawText(formatted, vpos);
    }

    private static Brush CreateFrozen(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }
}
