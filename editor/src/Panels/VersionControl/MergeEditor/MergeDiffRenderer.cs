// ============================================================
//  MergeDiffRenderer.cs — 上段 2 面の背景（追加 / 削除 / 詰め物 / ブロックの帯）
//
//  【役割】
//  AvalonEdit の背景レイヤーに、表示行の種類ごとの色を敷く。
//  スクリプトエディタの HighlightRenderer と同じ仕組み（IBackgroundRenderer）で、
//  こちらは「オフセット範囲」ではなく **行番号** で塗る点だけが違う。
//
//  【なぜ行番号で塗るのか】
//  上段 2 面は「1 表示行 = 1 ドキュメント行」で組み立てている
//  （MergeAlignedView.ToDisplayText）。行番号で引ければ、
//  折り返しや装飾で文字オフセットがずれても塗る場所を間違えない。
//
//  【色】
//  すべて Theme/SeedColorTable の定数。ここで色を決めない
//  （コントラスト検査の対象から外れるため。docs/editor_ui_style.md）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using SEEDEditor.Theme;
using SEEDEditor.VersionControl.Merge;

namespace SEEDEditor.Panels.VersionControl.MergeEditor;

/// <summary>
/// 表示行の種類に応じて背景を塗る描画レイヤー。
/// </summary>
public sealed class MergeDiffRenderer : IBackgroundRenderer
{
    /// <summary>競合ブロックの枠線の太さ [px]。</summary>
    private const double BLOCK_BORDER_THICKNESS_PX = 1.0;

    /// <summary>競合ブロックの左端に引く縦帯の幅 [px]。</summary>
    private const double BLOCK_BAR_WIDTH_PX = 3.0;

    /// <summary>詰め物の斜線の間隔 [px]。</summary>
    private const double PADDING_HATCH_SPACING_PX = 7.0;

    /// <summary>詰め物の斜線の太さ [px]。</summary>
    private const double PADDING_HATCH_THICKNESS_PX = 1.0;

    /// <summary>競合ブロック全体にかける薄い帯。</summary>
    private static readonly Brush BlockBrush = Frozen(SeedThemeColors.MergeBlockBg);

    /// <summary>その側が足した行の背景。</summary>
    private static readonly Brush AddedBrush = Frozen(SeedThemeColors.MergeAddedBg);

    /// <summary>その側で消えた行の背景。</summary>
    private static readonly Brush RemovedBrush = Frozen(SeedThemeColors.MergeRemovedBg);

    /// <summary>競合ブロックの枠線。</summary>
    private static readonly Pen BlockBorderPen =
        FrozenPen(SeedThemeColors.MergeBlockBorder, BLOCK_BORDER_THICKNESS_PX);

    /// <summary>競合ブロックの左端の縦帯。</summary>
    private static readonly Brush BlockBarBrush = Frozen(SeedThemeColors.MergeBlockBorder);

    /// <summary>詰め物の斜線。</summary>
    private static readonly Pen PaddingPen =
        FrozenPen(SeedThemeColors.MergePaddingStroke, PADDING_HATCH_THICKNESS_PX);

    /// <summary>塗る対象の表示行（1 表示行 = 1 ドキュメント行）。</summary>
    private IReadOnlyList<MergeDisplayRow> _rows = Array.Empty<MergeDisplayRow>();

    /// <summary>背景レイヤーへ描く（選択色より下）。</summary>
    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>塗る対象の行を差し替える。呼び出し後に再描画が要る。</summary>
    /// <param name="rows">表示行。</param>
    public void SetRows(IReadOnlyList<MergeDisplayRow> rows)
        => _rows = rows ?? Array.Empty<MergeDisplayRow>();

    /// <summary>行の種類に応じて背景を描く。</summary>
    /// <param name="textView">描画先のテキストビュー。</param>
    /// <param name="drawingContext">描画コンテキスト。</param>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_rows.Count == 0 || !textView.VisualLinesValid) return;

        var width = textView.ActualWidth;

        foreach (var visualLine in textView.VisualLines)
        {
            var rowIndex = visualLine.FirstDocumentLine.LineNumber - 1;
            if (rowIndex < 0 || rowIndex >= _rows.Count) continue;

            var row    = _rows[rowIndex];
            var top    = visualLine.VisualTop - textView.VerticalOffset;
            var bounds = new Rect(0, top, width, visualLine.Height);

            // 1) 競合ブロック全体の薄い帯（どこからどこまでが 1 ブロックかを見せる）。
            if (row.ConflictIndex >= 0)
            {
                drawingContext.DrawRectangle(BlockBrush, null, bounds);
                drawingContext.DrawRectangle(
                    BlockBarBrush, null,
                    new Rect(0, top, BLOCK_BAR_WIDTH_PX, visualLine.Height));
            }

            // 2) 行そのものの色。
            switch (row.Kind)
            {
                case MergeRowKind.Added:
                    drawingContext.DrawRectangle(AddedBrush, null, bounds);
                    break;

                case MergeRowKind.Removed:
                    drawingContext.DrawRectangle(RemovedBrush, null, bounds);
                    break;

                case MergeRowKind.Padding:
                    DrawHatch(drawingContext, bounds);
                    break;
            }

            // 3) ブロックの上下の境目に線を引く（帯だけだと隣接ブロックが繋がって見える）。
            DrawBlockEdges(drawingContext, rowIndex, row, top, visualLine.Height, width);
        }
    }

    /// <summary>
    /// 競合ブロックの先頭行の上・末尾行の下に線を引く。
    /// </summary>
    /// <param name="drawingContext">描画コンテキスト。</param>
    /// <param name="rowIndex">いま描いている表示行の位置。</param>
    /// <param name="row">いま描いている表示行。</param>
    /// <param name="top">その行の上端 Y。</param>
    /// <param name="height">その行の高さ。</param>
    /// <param name="width">描画幅。</param>
    private void DrawBlockEdges(
        DrawingContext drawingContext, int rowIndex, MergeDisplayRow row,
        double top, double height, double width)
    {
        if (row.ConflictIndex < 0) return;

        var isFirst = rowIndex == 0 || _rows[rowIndex - 1].ConflictIndex != row.ConflictIndex;
        var isLast  = rowIndex == _rows.Count - 1
                      || _rows[rowIndex + 1].ConflictIndex != row.ConflictIndex;

        if (isFirst)
        {
            drawingContext.DrawLine(BlockBorderPen, new Point(0, top), new Point(width, top));
        }
        if (isLast)
        {
            var bottom = top + height;
            drawingContext.DrawLine(
                BlockBorderPen, new Point(0, bottom), new Point(width, bottom));
        }
    }

    /// <summary>
    /// 詰め物の行を斜線で塗る（背景を暗くするだけでは空行と区別が付かないため）。
    /// </summary>
    /// <param name="drawingContext">描画コンテキスト。</param>
    /// <param name="bounds">塗る範囲。</param>
    private static void DrawHatch(DrawingContext drawingContext, Rect bounds)
    {
        // 斜線がはみ出さないように、この行の範囲へ切り取ってから引く。
        drawingContext.PushClip(new RectangleGeometry(bounds));
        try
        {
            for (var x = bounds.Left - bounds.Height;
                 x < bounds.Right;
                 x += PADDING_HATCH_SPACING_PX)
            {
                drawingContext.DrawLine(
                    PaddingPen,
                    new Point(x, bounds.Bottom),
                    new Point(x + bounds.Height, bounds.Top));
            }
        }
        finally
        {
            drawingContext.Pop();
        }
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
