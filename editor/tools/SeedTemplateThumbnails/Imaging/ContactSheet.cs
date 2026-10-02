// ============================================================
//  ContactSheet.cs — 書き出した見本を名前付きで 1 枚に並べた確認用の画像（--sheet）
//
//  【役割】
//  全件の見本を一目で見比べる（空白・真っ黒・切れていないか、部品だと分かるか）ための画像を作る。
//  見本そのものには使わない（リポジトリにも書かない。書き出し先は利用者が --sheet で決める）。
//  文字を描くために WPF の描画（DrawingVisual・RenderTargetBitmap）を使う。STA のスレッドから呼ぶ。
// ============================================================

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SEEDEditor.Tools.SeedTemplateThumbnails.Imaging;

/// <summary>確認用の一覧の画像。</summary>
public static class ContactSheet
{
    /// <summary>1 行に並べる数。</summary>
    private const int Columns = 6;

    /// <summary>見本の間・外周の余白（画素）。</summary>
    private const double Gap = 12;

    /// <summary>名前の文字の大きさ（画素）。</summary>
    private const double LabelFontSize = 13;

    /// <summary>名前の行の高さ（画素）。</summary>
    private const double LabelHeight = 20;

    /// <summary>名前を行の縦の真ん中へ寄せるときの割る数。</summary>
    private const double Halves = 2;

    /// <summary>画像の解像度（96 DPI = 1 DIP が 1 画素）。</summary>
    private const double Dpi = 96.0;

    /// <summary>文字の 1 DIP あたりの画素数。</summary>
    private const double PixelsPerDip = 1.0;

    /// <summary>台紙の色（見本の暗い背景との境が見える中間の灰色）。</summary>
    private static readonly Color SheetColor = Color.FromRgb(0x5A, 0x5A, 0x60);

    /// <summary>名前の文字の色。</summary>
    private static readonly Color LabelColor = Colors.White;

    /// <summary>名前の書体。</summary>
    private const string LabelFont = "Yu Gothic UI";

    /// <summary>
    /// 見本を並べた画像を書く。
    /// </summary>
    /// <param name="path">書き出し先の PNG。</param>
    /// <param name="items">（名前, 見本の画像のパス）の並び。</param>
    /// <param name="cellSize">見本の一辺（画素。見本の画像の大きさ）。</param>
    public static void Write(string path, IReadOnlyList<(string Label, string ImagePath)> items, int cellSize)
    {
        if (items.Count == 0) return;
        int rows = (items.Count + Columns - 1) / Columns;
        double cellW = cellSize + Gap, cellH = cellSize + LabelHeight + Gap;
        int width = (int)Math.Ceiling(Gap + Columns * cellW);
        int height = (int)Math.Ceiling(Gap + rows * cellH);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(SheetColor), null, new Rect(0, 0, width, height));
            var typeface = new Typeface(LabelFont);
            var labelBrush = new SolidColorBrush(LabelColor);
            for (int i = 0; i < items.Count; i++)
            {
                double x = Gap + (i % Columns) * cellW;
                double y = Gap + (i / Columns) * cellH;
                var image = LoadFrozen(items[i].ImagePath);
                if (image is not null) dc.DrawImage(image, new Rect(x, y, cellSize, cellSize));
                var text = new FormattedText(items[i].Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, LabelFontSize, labelBrush, PixelsPerDip) { MaxTextWidth = cellSize, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(text, new Point(x, y + cellSize + (LabelHeight - text.Height) / Halves));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    /// <summary>画像を読んで凍結する（読めなければ null）。</summary>
    private static BitmapSource? LoadFrozen(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        image.Freeze();
        return image;
    }
}
