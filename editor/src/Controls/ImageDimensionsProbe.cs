using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using SEEDEditor.Assets;

namespace SEEDEditor.Controls;

/// <summary>
/// 画像ファイルのピクセル寸法を「ヘッダだけ読んで」取得する軽量プローブ。
///
/// プロジェクトパネルは 1 フォルダに何十枚も画像を並べるため、寸法表示のために
/// 画像本体をデコードしていては開くたびに固まる。そこで
///   - <see cref="BitmapCreateOptions.DelayCreation"/> でピクセルのデコードを遅延させ
///   - <see cref="BitmapCacheOption.None"/> でフレームを丸ごと抱えない
/// 形で <see cref="BitmapDecoder"/> を作り、メタデータ由来の幅・高さだけを読み取る。
///
/// 取得結果は「成功した寸法」も「読めなかったこと（null）」も両方キャッシュする。
/// 読めない形式（.tga / .dds / .exr など WIC コーデックが無いもの）を
/// 一覧の再描画のたびに開き直さないためで、これが体感速度に効く。
/// キーは <see cref="AssetPreviewCacheKey"/>（パス＋更新時刻＋サイズ）なので、
/// 画像を差し替えれば次回は読み直される。
/// </summary>
internal static class ImageDimensionsProbe
{
    /// <summary>キャッシュキー -> 寸法（null = この版のファイルは読めなかった）。</summary>
    private static readonly ConcurrentDictionary<string, ImagePixelSize?> Cache = new();

    /// <summary>
    /// ファイルを開くときの共有指定。
    /// エディタ以外（画像編集ソフトなど）が同じファイルを開いていても失敗しないよう、
    /// 読み書き両方の共有を許す。こちらは読むだけで書き換えない。
    /// </summary>
    private const FileShare ProbeFileShare = FileShare.ReadWrite | FileShare.Delete;

    /// <summary>ストリームの先読みバッファ長（ヘッダしか読まないので小さくてよい）。</summary>
    private const int ProbeBufferSize = 4096;

    /// <summary>
    /// 寸法を非同期に取得する。UI スレッドを止めないためのラッパー。
    /// </summary>
    /// <param name="path">画像ファイルの絶対パス。</param>
    /// <returns>寸法。読めない形式・存在しないファイルなら null。</returns>
    public static Task<ImagePixelSize?> GetAsync(string path)
    {
        // 既に答えを持っているならスレッドを起こさずに即返す
        // （フォルダを行き来したときの再取得を避ける）。
        var key = AssetPreviewCacheKey.BuildFromFile(path);
        if (Cache.TryGetValue(key, out var cached)) return Task.FromResult(cached);

        return Task.Run(() => Get(path));
    }

    /// <summary>
    /// 寸法を同期的に取得する（キャッシュ有り）。
    /// 呼び出し側がすでにバックグラウンドにいる場合や、検証用プローブから使う。
    /// </summary>
    /// <param name="path">画像ファイルの絶対パス。</param>
    /// <returns>寸法。読めない形式・存在しないファイルなら null。</returns>
    public static ImagePixelSize? Get(string path)
    {
        var key = AssetPreviewCacheKey.BuildFromFile(path);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var size = ReadHeader(path);
        Cache[key] = size;
        return size;
    }

    /// <summary>
    /// 実際にヘッダを読んで寸法を取り出す（キャッシュを通さない生の処理）。
    /// </summary>
    /// <param name="path">画像ファイルの絶対パス。</param>
    /// <returns>寸法。読めなければ null。</returns>
    private static ImagePixelSize? ReadHeader(string path)
    {
        // 拡張子の時点で対象外なら開かない（.txt を画像として開こうとしない）。
        if (!AssetPreviewKinds.SupportsPixelSizePath(path)) return null;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, ProbeFileShare, ProbeBufferSize);

            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);

            if (decoder.Frames.Count == 0) return null;

            var frame = decoder.Frames[0];
            var size  = new ImagePixelSize(frame.PixelWidth, frame.PixelHeight);
            return size.IsValid ? size : null;
        }
        catch
        {
            // 対応コーデックが無い / 壊れている / 排他で開けない。
            // いずれも「寸法は出さない」で十分なので握りつぶす（呼び出し側は null で分岐する）。
            return null;
        }
    }
}
