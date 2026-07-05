using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// 行番号の左に置くブレークポイント用ガター（余白）。
///
/// - ブレークポイントのある行に赤丸を描く
/// - クリックでその行のブレークポイントをトグルする
/// - マウスホバー中の行には半透明の赤丸を出し、クリック可能なことを示す
///
/// 位置管理は <see cref="BreakpointSet"/>（TextAnchor 追従）に委譲する。
/// トグル時に <see cref="_onChanged"/> を呼び、呼び出し側が永続化する。
/// </summary>
public sealed class BreakpointMargin : AbstractMargin
{
    /// <summary>ガター幅（px）。</summary>
    private const double MarginWidth = 18;
    /// <summary>ブレークポイント丸の半径（px）。</summary>
    private const double DotRadius = 5;

    private static readonly Brush BackgroundBrush = Frozen(Color.FromRgb(0x25, 0x25, 0x26));
    private static readonly Brush BreakBrush      = Frozen(Color.FromRgb(0xE5, 0x14, 0x00));
    private static readonly Brush HoverBrush      = Frozen(Color.FromArgb(0x66, 0xE5, 0x14, 0x00));

    private readonly BreakpointSet _breakpoints;
    private readonly Action        _onChanged;

    /// <summary>ホバー中の行番号（無ければ -1）。</summary>
    private int _hoverLine = -1;

    public BreakpointMargin(BreakpointSet breakpoints, Action onChanged)
    {
        _breakpoints = breakpoints;
        _onChanged   = onChanged;
        Cursor       = Cursors.Hand;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ── レイアウト ───────────────────────────────────────────
    protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

    // ── TextView 連携（再描画のフック）────────────────────────
    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged   -= OnRedrawRequested;
            oldTextView.ScrollOffsetChanged  -= OnRedrawRequested;
        }
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged  += OnRedrawRequested;
            newTextView.ScrollOffsetChanged += OnRedrawRequested;
        }
        InvalidateVisual();
    }

    private void OnRedrawRequested(object? sender, EventArgs e) => InvalidateVisual();

    // ── 描画 ────────────────────────────────────────────────
    protected override void OnRender(DrawingContext dc)
    {
        var textView = TextView;
        var size = RenderSize;

        // 背景（クリック判定を全面で有効にするためにも塗る）
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, size.Width, size.Height));

        if (textView is null || !textView.VisualLinesValid) return;

        double cx = size.Width / 2;
        foreach (var visualLine in textView.VisualLines)
        {
            int lineNumber = visualLine.FirstDocumentLine.LineNumber;
            bool hasBreak  = _breakpoints.Contains(lineNumber);
            bool isHover   = lineNumber == _hoverLine;
            if (!hasBreak && !isHover) continue;

            double top = visualLine.VisualTop - textView.VerticalOffset;
            double cy  = top + visualLine.Height / 2;
            dc.DrawEllipse(hasBreak ? BreakBrush : HoverBrush, null, new Point(cx, cy), DotRadius, DotRadius);
        }
    }

    // ── 入力 ────────────────────────────────────────────────
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Left) return;

        int line = LineAt(e.GetPosition(this).Y);
        if (line < 0) return;

        _breakpoints.Toggle(line);
        _onChanged();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int line = LineAt(e.GetPosition(this).Y);
        if (line != _hoverLine)
        {
            _hoverLine = line;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverLine != -1)
        {
            _hoverLine = -1;
            InvalidateVisual();
        }
    }

    /// <summary>ガター上の Y 座標（このマージン基準）に対応するドキュメント行番号。無ければ -1。</summary>
    private int LineAt(double marginY)
    {
        var textView = TextView;
        if (textView is null || !textView.VisualLinesValid) return -1;

        // マージンと TextView は同じ縦レイアウトに並ぶため、スクロール量を足して
        // ビジュアル座標に変換し、その位置を含むビジュアル行を探す。
        double visualY = marginY + textView.VerticalOffset;
        foreach (var visualLine in textView.VisualLines)
        {
            if (visualY >= visualLine.VisualTop && visualY < visualLine.VisualTop + visualLine.Height)
                return visualLine.FirstDocumentLine.LineNumber;
        }
        return -1;
    }
}
