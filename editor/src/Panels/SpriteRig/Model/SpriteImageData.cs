using System;

namespace SEEDEditor.Panels.SpriteRig.Model;

/// <summary>
/// スプライトリグ編集の対象画像を、UI 非依存の生ピクセルとして保持する器。
///
/// WPF の <c>BitmapSource</c> を直接持たないのは、自動メッシュ化アルゴリズムを
/// 単体テストから（WPF 抜きで）動かせるようにするため。
/// 実ファイルからの読み込みは <c>SpriteImageLoader</c>（WPF 側）が担当し、
/// 表示用ビットマップは <c>SpriteRigCanvas</c> がこのデータから作り直す。
///
/// ピクセル配列は <b>BGRA・8bit/ch・先乗算なし</b>で、行優先・上から下へ並ぶ
/// （WPF の <c>PixelFormats.Bgra32</c> と同じ並び）。
/// </summary>
public sealed class SpriteImageData
{
    /// <summary>1 ピクセルあたりのバイト数（BGRA）。</summary>
    public const int BytesPerPixel = 4;

    /// <summary>BGRA 並びにおけるアルファ成分のバイトオフセット。</summary>
    public const int AlphaByteOffset = 3;

    /// <summary>アルファ値が取り得る最大値（8bit）。</summary>
    public const int MaxAlpha = 255;

    /// <summary>画像の横幅（ピクセル）。</summary>
    public int Width { get; }

    /// <summary>画像の高さ（ピクセル）。</summary>
    public int Height { get; }

    /// <summary>BGRA ピクセル列（長さ = Width * Height * <see cref="BytesPerPixel"/>）。</summary>
    public byte[] Pixels { get; }

    /// <summary>
    /// 生ピクセルから生成する。
    /// </summary>
    /// <param name="width">横幅（1 以上）。</param>
    /// <param name="height">高さ（1 以上）。</param>
    /// <param name="pixels">BGRA 配列（長さが width*height*4 でなければ例外）。</param>
    public SpriteImageData(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "画像サイズは 1 以上である必要があります");
        if (pixels.Length != width * height * BytesPerPixel)
            throw new ArgumentException("ピクセル配列の長さが width*height*4 と一致しません", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>指定ピクセルのアルファ値（0〜255）を返す。範囲外は 0（透明）。</summary>
    /// <param name="x">X 座標（ピクセル）。</param>
    /// <param name="y">Y 座標（ピクセル）。</param>
    public byte AlphaAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return 0;
        return Pixels[((y * Width) + x) * BytesPerPixel + AlphaByteOffset];
    }

    /// <summary>
    /// アルファ閾値で二値化した「不透明マスク」を作る。
    /// 自動メッシュ化の輪郭抽出はこのマスクだけを見る。
    /// </summary>
    /// <param name="alphaThreshold">この値<b>以上</b>のアルファを不透明とみなす（0〜255）。</param>
    /// <returns>長さ Width*Height の真偽値配列（行優先）。</returns>
    public bool[] BuildSolidMask(int alphaThreshold)
    {
        int threshold = Math.Clamp(alphaThreshold, 0, MaxAlpha);
        var mask = new bool[Width * Height];
        for (int i = 0; i < mask.Length; i++)
        {
            mask[i] = Pixels[i * BytesPerPixel + AlphaByteOffset] >= threshold;
        }
        return mask;
    }

    /// <summary>
    /// 全ピクセル不透明の画像を作る（テスト・フォールバック用）。
    /// </summary>
    /// <param name="width">横幅。</param>
    /// <param name="height">高さ。</param>
    public static SpriteImageData CreateOpaque(int width, int height)
    {
        var pixels = new byte[width * height * BytesPerPixel];
        for (int i = 0; i < width * height; i++)
        {
            pixels[i * BytesPerPixel + 0] = MaxAlpha;
            pixels[i * BytesPerPixel + 1] = MaxAlpha;
            pixels[i * BytesPerPixel + 2] = MaxAlpha;
            pixels[i * BytesPerPixel + AlphaByteOffset] = MaxAlpha;
        }
        return new SpriteImageData(width, height, pixels);
    }
}
