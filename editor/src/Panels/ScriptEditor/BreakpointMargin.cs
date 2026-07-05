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
    /// <summary>ヒット（現在停止行）を示す黄色。実行位置の矢印・リング色。</summary>
    private static readonly Brush HitBrush        = Frozen(Color.FromRgb(0xFF, 0xE0, 0x33));
    /// <summary>ヒット時にブレークポイント丸へ重ねる黄色リング。</summary>
    private static readonly Pen   HitRingPen      = FrozenPen(Color.FromRgb(0xFF, 0xE0, 0x33), 1.6);

    private readonly BreakpointSet _breakpoints;
    private readonly Action        _onChanged;

    /// <summary>ホバー中の行番号（無ければ -1）。</summary>
    private int _hoverLine = -1;
    /// <summary>デバッガが現在停止している行（ヒット表示用。無ければ -1）。</summary>
    private int _hitLine = -1;

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

    private static Pen FrozenPen(Color c, double thickness)
    {
        var p = new Pen(Frozen(c), thickness);
        p.Freeze();
        return p;
    }

    /// <summary>
    /// デバッガが停止している行（ヒット行）を設定する。-1 で解除。
    /// その行のブレークポイントは実行位置（黄色矢印＋リング）付きで描画される。
    /// </summary>
    public void SetHitLine(int line)
    {
        if (_hitLine == line) return;
        _hitLine = line;
        InvalidateVisual();
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
            bool isHit     = lineNumber == _hitLine;
            if (!hasBreak && !isHover && !isHit) continue;

            double top = visualLine.VisualTop - textView.VerticalOffset;
            double cy  = top + visualLine.Height / 2;

            // ブレークポイント丸。ヒット行では黄色リングを重ねて「停止中」を強調する。
            if (hasBreak)
                dc.DrawEllipse(BreakBrush, isHit ? HitRingPen : null, new Point(cx, cy), DotRadius, DotRadius);
            else if (isHover)
                dc.DrawEllipse(HoverBrush, null, new Point(cx, cy), DotRadius, DotRadius);

            // 実行位置（現在停止行）は黄色の右向き矢印を重ねて表示する。
            // ブレークポイント有りなら赤丸＋黄矢印＝「ヒットしたブレークポイント」に見える。
            if (isHit)
                dc.DrawGeometry(HitBrush, null, ArrowGeometry(cx, cy));
        }
    }

    /// <summary>実行位置を示す右向き矢印（三角形）を作る。</summary>
    private static Geometry ArrowGeometry(double cx, double cy)
    {
        // マージン幅（18px）に収まるコンパクトな三角形。中心 (cx,cy) 基準。
        const double half = 4.0;   // 縦の半分
        const double tip  = 5.0;   // 先端の右への張り出し
        const double tail = 3.5;   // 後端の左位置
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(cx - tail, cy - half), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(cx + tip, cy), true, false);
            ctx.LineTo(new Point(cx - tail, cy + half), true, false);
        }
        g.Freeze();
        return g;
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
