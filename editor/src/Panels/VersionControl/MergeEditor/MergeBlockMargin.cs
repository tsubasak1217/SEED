// ============================================================
//  MergeBlockMargin.cs — 競合ブロックの先頭行に置く「この側を採用」チェック
//
//  【役割】
//  上段 2 面それぞれの左端に付く、クリックできる余白。
//  競合ブロックの先頭行にだけチェックボックスを描き、押すと採用/不採用が切り替わる。
//
//  【なぜ WPF の CheckBox を並べないのか】
//  テキストは仮想化されてスクロールで作り直される。行に紐づく CheckBox を
//  本物のコントロールとして重ねると、スクロールのたびに配置し直す仕組みが要り、
//  ずれると「隣のブロックのチェックを操作した」という最悪の事故になる。
//  スクリプトエディタの BreakpointMargin と同じく、
//  **描画とヒット判定を行番号で行う** 方が確実で軽い。
//
//  【色】
//  枠とチェックは Theme/SeedColorTable の定数から取る（ここで色を決めない）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using SEEDEditor.Theme;
using SEEDEditor.VersionControl.Merge;

namespace SEEDEditor.Panels.VersionControl.MergeEditor;

/// <summary>
/// 競合ブロックごとの「この側を採用」チェックを描くクリック可能な余白。
/// </summary>
public sealed class MergeBlockMargin : AbstractMargin
{
    /// <summary>余白の幅 [px]。</summary>
    private const double MARGIN_WIDTH_PX = 20;

    /// <summary>チェックボックスの一辺 [px]。</summary>
    private const double BOX_SIZE_PX = 12;

    /// <summary>チェックボックスの枠の太さ [px]。</summary>
    private const double BOX_BORDER_THICKNESS_PX = 1.0;

    /// <summary>チェック（レ点）の線の太さ [px]。</summary>
    private const double CHECK_THICKNESS_PX = 1.8;

    /// <summary>チェックの左端が箱に対して占める割合。</summary>
    private const double CHECK_LEFT_RATIO = 0.22;

    /// <summary>チェックの折れ点が箱に対して占める割合（横）。</summary>
    private const double CHECK_MIDDLE_RATIO = 0.44;

    /// <summary>チェックの右端が箱に対して占める割合。</summary>
    private const double CHECK_RIGHT_RATIO = 0.80;

    /// <summary>余白の背景（エディタ本文と同じ暗さで、線だけを見せる）。</summary>
    private static readonly Brush BackgroundBrush = Frozen(SeedThemeColors.FieldBg);

    /// <summary>チェックボックスの枠。</summary>
    private static readonly Pen BoxPen =
        FrozenPen(SeedThemeColors.MergeBlockBorder, BOX_BORDER_THICKNESS_PX);

    /// <summary>チェック（レ点）。</summary>
    private static readonly Pen CheckPen =
        FrozenPen(SeedThemeColors.MergeBlockBorder, CHECK_THICKNESS_PX);

    /// <summary>チェックが入っているときの箱の塗り。</summary>
    private static readonly Brush CheckedBoxBrush = Frozen(SeedThemeColors.MergeBlockBg);

    /// <summary>競合ブロックが占める表示行の範囲。</summary>
    private IReadOnlyList<MergeBlockRange> _blocks = Array.Empty<MergeBlockRange>();

    /// <summary>そのブロックが採用されているかを尋ねる。</summary>
    private readonly Func<int, bool> _isSelected;

    /// <summary>そのブロックの採用を切り替える。</summary>
    private readonly Action<int> _toggle;

    /// <summary>
    /// 余白を作る。
    /// </summary>
    /// <param name="isSelected">ブロックの通し番号を受け取り、採用されているかを返す。</param>
    /// <param name="toggle">ブロックの通し番号を受け取り、採用を切り替える。</param>
    /// <param name="toolTip">余白全体のツールチップ（何を選ぶ場所なのかの説明）。</param>
    public MergeBlockMargin(Func<int, bool> isSelected, Action<int> toggle, string toolTip)
    {
        _isSelected = isSelected ?? throw new ArgumentNullException(nameof(isSelected));
        _toggle     = toggle     ?? throw new ArgumentNullException(nameof(toggle));
        Cursor      = Cursors.Hand;
        ToolTip     = toolTip;
    }

    /// <summary>描く対象のブロックを差し替える。</summary>
    /// <param name="blocks">競合ブロックが占める表示行の範囲。</param>
    public void SetBlocks(IReadOnlyList<MergeBlockRange> blocks)
    {
        _blocks = blocks ?? Array.Empty<MergeBlockRange>();
        InvalidateVisual();
    }

    /// <summary>幅は固定（高さはテキストビューに従う）。</summary>
    /// <param name="availableSize">親が与えた大きさ。</param>
    protected override Size MeasureOverride(Size availableSize)
        => new(MARGIN_WIDTH_PX, 0);

    /// <summary>テキストビューの差し替えに合わせて再描画のフックを張り替える。</summary>
    /// <param name="oldTextView">前のテキストビュー。</param>
    /// <param name="newTextView">新しいテキストビュー。</param>
    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged  -= OnRedrawRequested;
            oldTextView.ScrollOffsetChanged -= OnRedrawRequested;
        }

        base.OnTextViewChanged(oldTextView, newTextView);

        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged  += OnRedrawRequested;
            newTextView.ScrollOffsetChanged += OnRedrawRequested;
        }

        InvalidateVisual();
    }

    /// <summary>スクロール・行の作り直しで描き直す。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnRedrawRequested(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>ブロックの先頭行にチェックボックスを描く。</summary>
    /// <param name="drawingContext">描画コンテキスト。</param>
    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = RenderSize;

        // クリック判定を余白全面で効かせるため、背景も塗る。
        drawingContext.DrawRectangle(
            BackgroundBrush, null, new Rect(0, 0, size.Width, size.Height));

        var textView = TextView;
        if (textView is null || !textView.VisualLinesValid || _blocks.Count == 0) return;

        foreach (var visualLine in textView.VisualLines)
        {
            var rowIndex = visualLine.FirstDocumentLine.LineNumber - 1;
            var block    = BlockStartingAt(rowIndex);
            if (block is null) continue;

            var top      = visualLine.VisualTop - textView.VerticalOffset;
            var centerY  = top + visualLine.Height / 2;
            var box      = new Rect(
                (size.Width - BOX_SIZE_PX) / 2, centerY - BOX_SIZE_PX / 2,
                BOX_SIZE_PX, BOX_SIZE_PX);

            var selected = _isSelected(block.Value.ConflictIndex);
            drawingContext.DrawRectangle(selected ? CheckedBoxBrush : null, BoxPen, box);
            if (selected) DrawCheck(drawingContext, box);
        }
    }

    /// <summary>チェック（レ点）を描く。</summary>
    /// <param name="drawingContext">描画コンテキスト。</param>
    /// <param name="box">チェックボックスの範囲。</param>
    private static void DrawCheck(DrawingContext drawingContext, Rect box)
    {
        var left   = new Point(box.Left + box.Width * CHECK_LEFT_RATIO,   box.Top + box.Height * 0.52);
        var middle = new Point(box.Left + box.Width * CHECK_MIDDLE_RATIO, box.Top + box.Height * 0.74);
        var right  = new Point(box.Left + box.Width * CHECK_RIGHT_RATIO,  box.Top + box.Height * 0.28);

        drawingContext.DrawLine(CheckPen, left, middle);
        drawingContext.DrawLine(CheckPen, middle, right);
    }

    /// <summary>クリックされた位置のブロックを切り替える。</summary>
    /// <param name="e">マウスイベント。</param>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Left) return;

        var rowIndex = RowAt(e.GetPosition(this).Y);
        var block    = BlockStartingAt(rowIndex);
        if (block is null) return;

        _toggle(block.Value.ConflictIndex);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>その表示行から始まるブロックを返す（無ければ null）。</summary>
    /// <param name="rowIndex">表示行の位置（0 始まり）。</param>
    private MergeBlockRange? BlockStartingAt(int rowIndex)
    {
        if (rowIndex < 0) return null;

        foreach (var block in _blocks)
        {
            if (block.FirstRow == rowIndex) return block;
        }
        return null;
    }

    /// <summary>余白上の Y 座標に対応する表示行の位置（無ければ -1）。</summary>
    /// <param name="marginY">この余白の中での Y 座標。</param>
    private int RowAt(double marginY)
    {
        var textView = TextView;
        if (textView is null || !textView.VisualLinesValid) return -1;

        // 余白とテキストビューは同じ縦レイアウトに並ぶので、スクロール量を足す。
        var visualY = marginY + textView.VerticalOffset;
        foreach (var visualLine in textView.VisualLines)
        {
            if (visualY >= visualLine.VisualTop
                && visualY < visualLine.VisualTop + visualLine.Height)
            {
                return visualLine.FirstDocumentLine.LineNumber - 1;
            }
        }
        return -1;
    }

    /// <summary>凍結済みの塗りブラシを作る。</summary>
    /// <param name="color">色。</param>
    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>凍結済みのペンを作る。</summary>
    /// <param name="color">色。</param>
    /// <param name="thickness">太さ [px]。</param>
    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
