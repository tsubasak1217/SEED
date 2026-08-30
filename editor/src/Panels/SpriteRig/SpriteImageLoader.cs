using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SEEDEditor.Panels.SpriteRig.Model;

namespace SEEDEditor.Panels.SpriteRig;

/// <summary>
/// 画像ファイルを <see cref="SpriteImageData"/>（UI 非依存の BGRA 配列）へ読み込む。
///
/// メッシュ生成アルゴリズムを WPF から切り離すため、
/// 「WPF のデコーダを使う場所」をこのクラス 1 つに閉じ込めている。
/// 表示用ビットマップも同じピクセル列から作るので、
/// <b>アルファ閾値で見えている形と生成される輪郭が必ず一致する</b>。
/// </summary>
public static class SpriteImageLoader
{
    /// <summary>スプライトリグで開ける画像拡張子（小文字・先頭ドット付き）。</summary>
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".webp",
    };

    /// <summary>ファイルダイアログ用のフィルタ文字列。</summary>
    public const string OpenDialogFilter =
        "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.webp|すべてのファイル|*.*";

    /// <summary>
    /// 拡張子がスプライトリグで扱える画像かどうかを返す。
    /// </summary>
    /// <param name="extension">先頭ドット付きの拡張子（大文字小文字は問わない）。</param>
    public static bool IsSupportedExtension(string? extension)
        => !string.IsNullOrEmpty(extension) && SupportedExtensions.Contains(extension);

    /// <summary>
    /// パスがスプライトリグで扱える画像ファイルかどうかを返す。
    /// </summary>
    /// <param name="path">ファイルパス。</param>
    public static bool IsSupportedImagePath(string? path)
        => !string.IsNullOrEmpty(path) && IsSupportedExtension(Path.GetExtension(path));

    /// <summary>
    /// 画像ファイルを BGRA ピクセル列として読み込む。
    /// </summary>
    /// <param name="path">画像の絶対パス。</param>
    /// <returns>読み込まれたピクセルデータ。</returns>
    /// <exception cref="InvalidDataException">デコードできなかった場合。</exception>
    public static SpriteImageData Load(string path)
    {
        // ファイルロックを残さないよう、いったんメモリへ読み切ってからデコードする
        // （エディタで開いたまま外部ツールで上書き保存できるようにするため）。
        byte[] raw = File.ReadAllBytes(path);
        using var stream = new MemoryStream(raw);

        var decoded = BitmapFrame.Create(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        // 何が来ても BGRA32 に揃える（アルファの取り出し方を 1 通りに固定する）
        var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0.0);
        converted.Freeze();

        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"画像のサイズが不正です: {path}");

        int stride = width * SpriteImageData.BytesPerPixel;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        return new SpriteImageData(width, height, pixels);
    }

    /// <summary>
    /// ピクセルデータから表示用のビットマップを作る（キャンバス描画用）。
    /// </summary>
    /// <param name="image">元のピクセルデータ。</param>
    /// <returns>Freeze 済みの BitmapSource。</returns>
    public static BitmapSource CreateBitmap(SpriteImageData image)
    {
        int stride = image.Width * SpriteImageData.BytesPerPixel;
        var bitmap = BitmapSource.Create(
            image.Width, image.Height,
            DefaultDpi, DefaultDpi,
            PixelFormats.Bgra32, null,
            image.Pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>表示用ビットマップの DPI（画面ピクセルと 1:1 にするため 96 固定）。</summary>
    private const double DefaultDpi = 96.0;
}
