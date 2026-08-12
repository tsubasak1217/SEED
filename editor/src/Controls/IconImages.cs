using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace SEEDEditor.Controls;

/// <summary>
/// Icons.xaml の Geometry を <see cref="ImageSource"/> へ変換して配る変換キャッシュ。
///
/// WPF には Foreground を継承できる <see cref="AppIcon"/> を置けず ImageSource を
/// 直接要求する API がある（AvalonDock の <c>LayoutContent.IconSource</c>、
/// 既存の <see cref="System.Windows.Controls.Image"/> の Source など）。
/// そういう箇所ではここで作った <see cref="DrawingImage"/> を使う。
///
/// 色はここでも各所にハードコードせず、Icons.xaml の
/// <see cref="AppIcon.DefaultBrushKey"/> ブラシ 1 箇所だけを参照する。
/// ImageSource は Foreground を継承できないため、色を変えたい場合は
/// <see cref="Get(string, Brush)"/> でブラシを明示する。
///
/// 生成した DrawingImage は Freeze 済みでキーごとにキャッシュするため、
/// 同じアイコンを何百個並べても実体は 1 個で済む
/// （プロジェクトパネルのファイルタイル等で効く）。
/// </summary>
internal static class IconImages
{
    /// <summary>Icons.xaml の全 Geometry が前提とする viewBox の一辺（MDI は 24x24 固定）。</summary>
    private const double IconViewBoxSize = 24.0;

    /// <summary>ブラシを明示しなかった場合の最終フォールバック色。</summary>
    private static readonly Brush HardFallbackBrush = Brushes.White;

    /// <summary>キー "{iconKey}|{ブラシ文字列}" -> 生成済み ImageSource。</summary>
    private static readonly Dictionary<string, ImageSource?> Cache = new();

    /// <summary>
    /// 既定色（Icons.xaml の Icon.DefaultBrush）でアイコンの ImageSource を得る。
    /// </summary>
    /// <param name="iconKey">Icons.xaml のリソースキー（例 "Icon.Panel.Project"）。</param>
    /// <returns>未登録キーなら null。呼び出し側は「アイコンなし」として扱えばよい。</returns>
    public static ImageSource? Get(string iconKey) => Get(iconKey, DefaultBrush());

    /// <summary>
    /// 塗り色を明示してアイコンの ImageSource を得る。
    /// </summary>
    /// <param name="iconKey">Icons.xaml のリソースキー。</param>
    /// <param name="brush">塗りブラシ。Freeze できるものを渡すこと。</param>
    /// <returns>未登録キーなら null。</returns>
    public static ImageSource? Get(string iconKey, Brush brush)
    {
        var cacheKey = iconKey + "|" + brush;
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

        var image = Build(iconKey, brush);
        Cache[cacheKey] = image;
        return image;
    }

    /// <summary>Icons.xaml の既定アイコン色ブラシを引く（無ければ白）。</summary>
    private static Brush DefaultBrush()
        => Application.Current?.TryFindResource(AppIcon.DefaultBrushKey) as Brush
           ?? HardFallbackBrush;

    /// <summary>
    /// Geometry から 24x24 の描画範囲を持つ DrawingImage を組み立てる。
    ///
    /// 透明な 24x24 の矩形を最背面に敷いているのが要点。これが無いと
    /// DrawingImage のサイズが「実際に塗られた範囲」になり、余白の量が
    /// アイコンごとに違うぶん Stretch=Uniform での見かけの大きさがバラつく。
    /// </summary>
    private static ImageSource? Build(string iconKey, Brush brush)
    {
        if (Application.Current?.TryFindResource(iconKey) is not Geometry geometry) return null;

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            Brushes.Transparent, null,
            new RectangleGeometry(new Rect(0, 0, IconViewBoxSize, IconViewBoxSize))));
        group.Children.Add(new GeometryDrawing(brush, null, geometry));

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
