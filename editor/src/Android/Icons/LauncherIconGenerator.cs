// ============================================================
//  LauncherIconGenerator.cs — 元の画像と背景色から、ランチャーのアイコンの生成物（res/ の下のファイル）を作る（純粋な処理。段階D）
//
//  【作り方】（大きさとファイル名の決まりは LauncherIconSpec）
//    従来型   … 背景色で塗った 48dp の正方形に、元の画像を縦横比を保って全体に収めて重ねる
//    前景     … 透明な 108dp の正方形の中央の安全域（66dp）に、元の画像を縦横比を保って収める
//               （アダプティブアイコンは端末ごとに円・角丸などの形に切り抜かれるので、安全域の外は欠け得る）
//    背景色   … @color/ic_launcher_background（アダプティブアイコンの背景の層）
//  元の画像は縮小（面積平均）で各密度の大きさにする。元の画像が小さければ拡大（双線形）になり、ぼやける
//  （拡大が要るときは警告を返す。512 画素以上の正方形を勧める）。
//
//  ファイルの書き出しはしない（LauncherIconStager が中身の違うものだけを書く）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SEEDEditor.Android.Icons;

/// <summary>生成したファイルの一覧と、気を付けること。</summary>
/// <param name="Files">res からの相対パス（/ 区切り）→ 中身。</param>
/// <param name="Warnings">警告（元の画像が小さい等）。</param>
public sealed record LauncherIconOutput(IReadOnlyDictionary<string, byte[]> Files, IReadOnlyList<string> Warnings);

/// <summary>ランチャーのアイコンの生成物を作る。</summary>
public static class LauncherIconGenerator
{
    /// <summary>元の画像として勧める最小の辺（最も大きい前景 = xxxhdpi の 108dp は 432 画素。それ以上なら拡大しない）。</summary>
    public const int RecommendedSourceSize = 512;

    /// <summary>
    /// 生成物を作る。
    /// </summary>
    /// <param name="source">元の画像。</param>
    /// <param name="background">背景色。</param>
    /// <returns>ファイルの一覧と警告。</returns>
    public static LauncherIconOutput Generate(RgbaImage source, RgbaColor background)
    {
        var files = new SortedDictionary<string, byte[]>(System.StringComparer.Ordinal);
        foreach (var density in LauncherIconSpec.Densities)
        {
            var folder = LauncherIconSpec.MipmapFolder(density);
            files[$"{folder}/{LauncherIconSpec.LegacyIconFileName}"] =
                PngEncoder.Encode(Compose(source, density.Pixels(LauncherIconSpec.LegacyIconDp), density.Pixels(LauncherIconSpec.LegacyIconDp), background));
            files[$"{folder}/{LauncherIconSpec.ForegroundFileName}"] =
                PngEncoder.Encode(Compose(source, density.Pixels(LauncherIconSpec.AdaptiveLayerDp), density.Pixels(LauncherIconSpec.AdaptiveSafeZoneDp), RgbaColor.Transparent));
        }
        files[LauncherIconSpec.AdaptiveIconRelativePath] = Encoding.UTF8.GetBytes(LauncherIconSpec.AdaptiveIconXml);
        files[LauncherIconSpec.BackgroundColorRelativePath] = Encoding.UTF8.GetBytes(
            string.Format(CultureInfo.InvariantCulture, LauncherIconSpec.BackgroundColorXmlFormat, background.ToAndroidHex()));

        var warnings = new List<string>();
        var largestContent = LauncherIconSpec.Densities.Max(d => d.Pixels(LauncherIconSpec.LegacyIconDp));
        var largestForeground = LauncherIconSpec.Densities.Max(d => d.Pixels(LauncherIconSpec.AdaptiveSafeZoneDp));
        var needed = System.Math.Max(largestContent, largestForeground);
        if (System.Math.Min(source.Width, source.Height) < needed)
        {
            warnings.Add($"アイコンの元の画像（{source.Width}x{source.Height}）が小さいため、大きい密度では拡大してぼやけます" +
                         $"（{RecommendedSourceSize}x{RecommendedSourceSize} 以上の正方形の PNG を勧めます）。");
        }
        if (source.Width != source.Height)
        {
            warnings.Add($"アイコンの元の画像が正方形ではありません（{source.Width}x{source.Height}）。縦横比を保って中央に収めます（余りは背景色・透明）。");
        }
        return new LauncherIconOutput(files, warnings);
    }

    /// <summary>
    /// canvasSize の正方形（背景色で塗る）の中央に、元の画像を contentSize の正方形へ縦横比を保って収めて重ねる。
    /// </summary>
    /// <param name="source">元の画像。</param>
    /// <param name="canvasSize">出力の一辺（画素）。</param>
    /// <param name="contentSize">元の画像を収める正方形の一辺（画素）。</param>
    /// <param name="background">背景色（透明なら透明のまま）。</param>
    /// <returns>画像。</returns>
    public static RgbaImage Compose(RgbaImage source, int canvasSize, int contentSize, RgbaColor background)
    {
        var canvas = RgbaImage.Filled(canvasSize, canvasSize, background);
        var (width, height) = ImageResampler.FitInside(source.Width, source.Height, contentSize);
        var resized = ImageResampler.Resize(source, width, height);
        canvas.DrawOver(resized, (canvasSize - width) / 2, (canvasSize - height) / 2);
        return canvas;
    }
}
