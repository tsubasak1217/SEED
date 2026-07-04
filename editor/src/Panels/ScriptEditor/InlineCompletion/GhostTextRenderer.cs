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

    /// <summary>2 行目以降の下地を塗る色（エディタ背景が取得できないとき用のフォールバック）。</summary>
    private static readonly Brush FallbackBg = CreateFrozen(Color.FromRgb(0x1E, 0x1E, 0x1E));

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

        var document = _editor.Document;

        // カーソル位置（1 行目の描画開始点）。スクロール量を差し引く。
        var caretLoc = document.GetLocation(_offset);
        var vpos     = textView.GetVisualPosition(
            new TextViewPosition(caretLoc), VisualYPosition.TextTop) - textView.ScrollOffset;

        // 2 行目以降の左端 x（カーソル行の桁 0 の位置）。予測は自身のインデントを含む。
        var lineStart = document.GetLineByOffset(_offset);
        var startLoc  = document.GetLocation(lineStart.Offset);
        double leftX  = textView.GetVisualPosition(
            new TextViewPosition(startLoc), VisualYPosition.TextTop).X - textView.ScrollOffset.X;

        var typeface = new Typeface(_editor.FontFamily, _editor.FontStyle, _editor.FontWeight, _editor.FontStretch);
        var dpi      = VisualTreeHelper.GetDpi(_editor).PixelsPerDip;
        double lineHeight = textView.DefaultLineHeight;

        // 改行で分割し、1 行目はカーソル直後、2 行目以降はその下へ描く。
        var lines = _text.Split('\n');

        // 1 行目（カーソル直後に追記）
        drawingContext.DrawText(MakeText(lines[0], typeface, dpi), vpos);

        // 2 行目以降。背景レンダラは既存テキストを押し下げられないので、
        // 各行の背景をエディタ色で塗って下の内容を隠し「行が空いた」ように見せる。
        var bg = _editor.Background ?? FallbackBg;
        for (int i = 1; i < lines.Length; i++)
        {
            double y = vpos.Y + i * lineHeight;
            drawingContext.DrawRectangle(bg, null,
                new Rect(0, y, Math.Max(0, textView.ActualWidth), lineHeight));
            drawingContext.DrawText(MakeText(lines[i], typeface, dpi), new Point(leftX, y));
        }
    }

    /// <summary>ゴースト色の FormattedText を生成する。</summary>
    private FormattedText MakeText(string text, Typeface typeface, double dpi) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        typeface,
        _editor.FontSize,
        GhostBrush,
        dpi);

    private static Brush CreateFrozen(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }
}
