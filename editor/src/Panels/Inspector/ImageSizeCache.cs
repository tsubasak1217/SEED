using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace SEEDEditor.Panels.Inspector;

/// <summary>
/// 画像ファイルのピクセル寸法（幅・高さ）を、ヘッダだけ読んで取得しキャッシュする。
///
/// <para>
/// インスペクタの「画像比率に設定」はユーザーがメニューを開くたびに寸法を要求する。
/// 毎回フルデコードすると数 MB の PNG で数十 ms 止まるため、
/// <see cref="BitmapDecoder"/> にヘッダだけ読ませ（<see cref="BitmapCreateOptions.DelayCreation"/>
/// ＋ <see cref="BitmapCacheOption.None"/>）、結果をパス単位でキャッシュする。
/// </para>
///
/// <para>
/// キャッシュキーはパスだが、<b>最終更新日時とファイルサイズも一緒に覚える</b>。
/// 画像を描き直して差し替えたのに古い寸法を使い続ける、という取り違えを防ぐため
/// （ペイントツールで上書き保存しながらサイズを合わせる使い方は普通に起きる）。
/// </para>
///
/// <para>
/// WPF のデコーダに依存するため、TGA / WebP のようにコーデックが無い形式では
/// 失敗する。失敗も「失敗した」という結果としてキャッシュし、
/// 開くたびに同じ例外コストを払わないようにする。
/// </para>
/// </summary>
public static class ImageSizeCache
{
    /// <summary>キャッシュ 1 件分（寸法と、それを読んだときのファイルの版）。</summary>
    /// <param name="WriteTimeUtcTicks">読み取り時点の最終更新日時（UTC ticks）。</param>
    /// <param name="Length">読み取り時点のファイルサイズ（バイト）。</param>
    /// <param name="Width">画像の幅（ピクセル）。失敗時は 0。</param>
    /// <param name="Height">画像の高さ（ピクセル）。失敗時は 0。</param>
    /// <param name="Success">寸法を取得できたか。</param>
    private readonly record struct Entry(
        long WriteTimeUtcTicks, long Length, int Width, int Height, bool Success);

    /// <summary>
    /// パス → 寸法のキャッシュ。
    /// UI スレッドからしか触らない想定だが、将来バックグラウンド先読みを足しても
    /// 壊れないよう並行辞書にしておく（Windows のパスは大文字小文字を区別しない）。
    /// </summary>
    private static readonly ConcurrentDictionary<string, Entry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 画像のピクセル寸法を取得する（キャッシュ優先）。
    /// </summary>
    /// <param name="absolutePath">画像ファイルの絶対パス。</param>
    /// <param name="width">画像の幅（ピクセル）。失敗時は 0。</param>
    /// <param name="height">画像の高さ（ピクセル）。失敗時は 0。</param>
    /// <returns>取得できたら true。ファイルが無い・コーデックが無い・壊れている場合は false。</returns>
    public static bool TryGetPixelSize(string? absolutePath, out int width, out int height)
    {
        width  = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(absolutePath)) return false;

        // ファイルの版（更新日時・サイズ）を先に取る。取れない＝存在しないので失敗。
        FileInfo info;
        try
        {
            info = new FileInfo(absolutePath);
            if (!info.Exists) return false;
        }
        catch (Exception ex)
        {
            SEEDEditor.EditorLog.Write($"[ImageSizeCache] ファイル情報の取得に失敗: {absolutePath} ({ex.Message})");
            return false;
        }

        long ticks  = info.LastWriteTimeUtc.Ticks;
        long length = info.Length;

        // 同じ版のキャッシュがあればそれを返す（失敗も含めて再利用する）
        if (Cache.TryGetValue(absolutePath, out var cached) &&
            cached.WriteTimeUtcTicks == ticks && cached.Length == length)
        {
            width  = cached.Width;
            height = cached.Height;
            return cached.Success;
        }

        var entry = ReadPixelSize(absolutePath, ticks, length);
        Cache[absolutePath] = entry;
        width  = entry.Width;
        height = entry.Height;
        return entry.Success;
    }

    /// <summary>キャッシュを空にする（画像形式のコーデックを入れ直した等、取り直したいとき用）。</summary>
    public static void Clear() => Cache.Clear();

    /// <summary>
    /// 画像ヘッダを読んでピクセル寸法を得る（キャッシュ判定を通さない実処理）。
    ///
    /// ファイルを掴んだままにしないよう、自前で開いた <see cref="FileStream"/> を
    /// 寸法読み取り後に必ず閉じる（<see cref="BitmapCacheOption.None"/> は
    /// デコーダにストリームを保持させるため、URI から直接開くとファイルがロックされる）。
    /// </summary>
    private static Entry ReadPixelSize(string absolutePath, long ticks, long length)
    {
        try
        {
            using var stream = new FileStream(
                absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var decoder = BitmapDecoder.Create(
                stream,
                // ヘッダだけ読む（実ピクセルのデコードはしない）＋ カラープロファイル解釈を省く
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);

            if (decoder.Frames.Count == 0)
                return new Entry(ticks, length, 0, 0, false);

            var frame = decoder.Frames[0];
            int w = frame.PixelWidth;
            int h = frame.PixelHeight;
            if (w <= 0 || h <= 0) return new Entry(ticks, length, 0, 0, false);

            return new Entry(ticks, length, w, h, true);
        }
        catch (Exception ex)
        {
            // TGA / WebP などコーデックが無い形式・破損ファイルはここに来る。
            // 失敗もキャッシュして、メニューを開くたびに例外コストを払わないようにする。
            SEEDEditor.EditorLog.Write($"[ImageSizeCache] 画像寸法の取得に失敗: {absolutePath} ({ex.Message})");
            return new Entry(ticks, length, 0, 0, false);
        }
    }
}
