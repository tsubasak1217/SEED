// ============================================================
//  TemplateActorThumbnails.cs — テンプレートアクタのサムネイル画像の読み込み（WPF）
//
//  【役割】
//  カタログが指すサムネイル（templates/<フォルダ>/thumbnails/<名前>.png。推奨 192×192）を
//  一覧の枠の大きさに合わせて縮めて読み、凍結した ImageSource にして返す。
//  画像が無い・読めないときは null を返し、窓は灰色の板に頭文字を置く（枠だけは必ず出す）。
//
//  【キャッシュ】
//  窓を開き直すたびに読み直さないよう、パスと更新時刻で覚えておく。
//  画像を差し替えた（更新時刻が変わった）ときは読み直すので、エディタを再起動しなくても反映される。
//
//  【ファイルを掴まない】
//  FileStream から読んで OnLoad で中身を写し取り、すぐ閉じる。
//  窓を開いたまま画像を差し替えられるようにするため。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// サムネイル画像の読み込みとキャッシュ。UI スレッドから呼ぶ。
/// </summary>
public static class TemplateActorThumbnails
{
    /// <summary>
    /// 読み込むときに縮める幅（px）。一覧の枠（TemplateActorPickerWindow.ThumbnailDisplaySize）の
    /// 2 倍にして、表示の拡大率 200% までにじまないようにする。
    /// </summary>
    public const int DecodePixelWidth = 128;

    /// <summary>読み込み済みの画像（キー = 絶対パス。大文字小文字は区別しない）。</summary>
    private static readonly Dictionary<string, (DateTime Stamp, ImageSource? Image)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// サムネイル画像を読む。
    /// </summary>
    /// <param name="absolutePath">画像の絶対パス。</param>
    /// <returns>凍結した画像。無い・読めないなら null。</returns>
    public static ImageSource? Load(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath)) return null;

        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(absolutePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        if (Cache.TryGetValue(absolutePath, out var hit) && hit.Stamp == stamp) return hit.Image;

        var image = Decode(absolutePath);
        Cache[absolutePath] = (stamp, image);
        return image;
    }

    /// <summary>画像ファイルを縮めて読み、凍結する（読めなければ null）。</summary>
    private static ImageSource? Decode(string absolutePath)
    {
        try
        {
            using var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption      = BitmapCacheOption.OnLoad;   // ここで中身を写し取り、ファイルを掴まない
            bitmap.DecodePixelWidth = DecodePixelWidth;
            bitmap.StreamSource     = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            // 壊れた画像・対応していない形式は「画像なし」と同じ扱い（頭文字の板を出す）
            return null;
        }
    }
}
