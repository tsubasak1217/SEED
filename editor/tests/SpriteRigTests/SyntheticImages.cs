using System;
using SEEDEditor.Panels.SpriteRig.Model;

namespace SpriteRigTests;

/// <summary>
/// テスト用の合成画像（透過円・ドーナツ・2 つの島など）を作る。
///
/// 実ファイルを読まずにアルファ形状を作れるようにして、
/// 「どの形を入力したか」がテストコード上で自明になるようにしている。
/// </summary>
public static class SyntheticImages
{
    /// <summary>不透明ピクセルの色（BGR。アルファは形状で決まる）。</summary>
    private const byte OpaqueChannel = 200;

    /// <summary>
    /// 中央に不透明な円を描いた画像を作る（円の外は完全透明）。
    /// </summary>
    /// <param name="size">画像の一辺（正方形）。</param>
    /// <param name="radius">円の半径（ピクセル）。</param>
    public static SpriteImageData Circle(int size, double radius)
    {
        double center = size * 0.5;
        return FromPredicate(size, size, (x, y) =>
            Distance(x, y, center, center) <= radius);
    }

    /// <summary>
    /// 中央にドーナツ（穴あき円）を描いた画像を作る。
    /// </summary>
    /// <param name="size">画像の一辺（正方形）。</param>
    /// <param name="outerRadius">外側の半径。</param>
    /// <param name="innerRadius">穴の半径。</param>
    public static SpriteImageData Donut(int size, double outerRadius, double innerRadius)
    {
        double center = size * 0.5;
        return FromPredicate(size, size, (x, y) =>
        {
            double d = Distance(x, y, center, center);
            return d <= outerRadius && d > innerRadius;
        });
    }

    /// <summary>
    /// 左右に離れた 2 つの円（＝別々の島）を描いた画像を作る。
    /// </summary>
    /// <param name="width">画像の横幅。</param>
    /// <param name="height">画像の高さ。</param>
    /// <param name="radius">各円の半径。</param>
    public static SpriteImageData TwoIslands(int width, int height, double radius)
    {
        double cy = height * 0.5;
        double leftX = width * 0.25;
        double rightX = width * 0.75;
        return FromPredicate(width, height, (x, y) =>
            Distance(x, y, leftX, cy) <= radius || Distance(x, y, rightX, cy) <= radius);
    }

    /// <summary>全画素が完全に透明な画像を作る。</summary>
    /// <param name="width">画像の横幅。</param>
    /// <param name="height">画像の高さ。</param>
    public static SpriteImageData FullyTransparent(int width, int height)
        => FromPredicate(width, height, (_, _) => false);

    /// <summary>
    /// 「不透明かどうか」を返す述語からアルファ画像を組み立てる。
    /// </summary>
    /// <param name="width">画像の横幅。</param>
    /// <param name="height">画像の高さ。</param>
    /// <param name="isOpaque">ピクセル中心座標を受け取り、不透明なら true を返す述語。</param>
    private static SpriteImageData FromPredicate(int width, int height, Func<double, double, bool> isOpaque)
    {
        var pixels = new byte[width * height * SpriteImageData.BytesPerPixel];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // ピクセルの中心で判定する（境界のギザギザが左右対称になる）
                bool opaque = isOpaque(x + 0.5, y + 0.5);
                int offset = ((y * width) + x) * SpriteImageData.BytesPerPixel;
                pixels[offset + 0] = OpaqueChannel;
                pixels[offset + 1] = OpaqueChannel;
                pixels[offset + 2] = OpaqueChannel;
                pixels[offset + SpriteImageData.AlphaByteOffset] =
                    opaque ? (byte)SpriteImageData.MaxAlpha : (byte)0;
            }
        }
        return new SpriteImageData(width, height, pixels);
    }

    /// <summary>2 点間の距離。</summary>
    private static double Distance(double x0, double y0, double x1, double y1)
        => Math.Sqrt((x0 - x1) * (x0 - x1) + (y0 - y1) * (y0 - y1));
}
