// ============================================================
//  ScreenshotDownscaler.cs — スクリーンショット PNG の縮小後処理
//
//  MCP ツール seed_screenshot の max_width / scale オプションの実体。
//
//  【なぜ「撮ってから縮小する」後処理なのか】
//   撮影経路は 2 つある（画面 DC BitBlt = WindowScreenCapture、
//   ランタイムの GPU 読み戻し = IPC SCREENSHOT）。どちらも最終成果物は
//   「フル解像度の PNG ファイル 1 枚」なので、縮小をその後段の共通処理として
//   切り出せば、撮影そのものの経路は一切変更せずに両方式へ同じ挙動を与えられる。
//   （撮影側に縮小を埋め込むと GPU 側＝ Rust ランタイムまで巻き込むことになる）
//
//  【縮小の品質】
//   DrawingVisual へ BitmapScalingMode.HighQuality（Fant フィルタ）で描き直す。
//   単純な TransformedBitmap（既定 Fant）でもほぼ同等だが、
//   明示的に指定しておくほうが将来の既定変更に影響されない。
//
//  【アルファ・DPI】
//   PNG は Bgra32 のまま保持し、出力の DPI は 96 に固定する
//   （RenderTargetBitmap の DPI を変えると画素数と論理サイズがずれるため）。
// ============================================================

using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SEEDEditor.AI.Capture;

/// <summary>スクリーンショット縮小の結果。</summary>
/// <param name="Scaled">実際に縮小したか（縮小不要・失敗時は false）。</param>
/// <param name="Path">エージェントへ返すべき PNG のパス（縮小版、または元のまま）。</param>
/// <param name="Width">Path が指す画像の幅。</param>
/// <param name="Height">Path が指す画像の高さ。</param>
/// <param name="FullPath">keep_full 指定でフル解像度版を残した場合のパス（それ以外は null）。</param>
/// <param name="FullWidth">縮小前の幅。</param>
/// <param name="FullHeight">縮小前の高さ。</param>
/// <param name="Warning">縮小を試みたが失敗した場合の理由（成功・不要時は null）。</param>
public readonly record struct DownscaleResult(
    bool    Scaled,
    string  Path,
    int     Width,
    int     Height,
    string? FullPath,
    int     FullWidth,
    int     FullHeight,
    string? Warning);

/// <summary>
/// 撮影済み PNG を要求サイズまで縮小する静的ユーティリティ。
/// WPF のイメージング API を使うため UI スレッド（STA）から呼ぶこと。
/// </summary>
public static class ScreenshotDownscaler
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>keep_full=true のときフル解像度版に付ける拡張子（"foo.png" → "foo.full.png"）。</summary>
    private const string FullSuffix = ".full.png";

    /// <summary>出力 PNG の DPI（画素数と論理サイズを一致させるため 96 固定）。</summary>
    private const double OutputDpi = 96.0;

    /// <summary>縮小後に許す最小の辺の長さ（px）。0 幅の画像を作らないためのクランプ。</summary>
    private const int MinSide = 1;

    /// <summary>scale 引数の下限（これ未満は指定なしとみなす）。</summary>
    private const double MinScale = 0.0;

    /// <summary>scale 引数の上限（1 を超える拡大は行わない）。</summary>
    private const double MaxScale = 1.0;

    // ── API ──────────────────────────────────────────────────────

    /// <summary>
    /// path の PNG を maxWidth / scale に従って縮小する。
    ///
    /// 縮小が不要（どちらも未指定、または元画像が既に十分小さい）なら何もせず、
    /// Scaled=false と元の寸法をそのまま返す。
    /// keepFull=true のときはフル解像度版を "&lt;名前&gt;.full.png" として隣に残し、
    /// path 自体は縮小版で上書きする（エージェントが受け取る画像＝縮小版に統一するため）。
    /// </summary>
    /// <param name="path">撮影済み PNG の絶対パス（縮小版もここへ書き戻す）。</param>
    /// <param name="maxWidth">縮小後の最大幅（px）。null なら幅の制限なし。</param>
    /// <param name="scale">縮小率 0..1。null なら比率指定なし。</param>
    /// <param name="keepFull">フル解像度版を別名で残すか。</param>
    public static DownscaleResult Apply(string path, int? maxWidth, double? scale, bool keepFull)
    {
        // 元画像を読む。ここで失敗しても撮影自体は成功しているので、
        // 例外にせず「縮小しなかった」として扱う。
        int srcW, srcH;
        BitmapSource src;
        try
        {
            src  = LoadDetached(path);
            srcW = src.PixelWidth;
            srcH = src.PixelHeight;
        }
        catch (Exception ex)
        {
            return new DownscaleResult(false, path, 0, 0, null, 0, 0,
                $"縮小のための読み込みに失敗しました: {ex.Message}");
        }

        var targetW = ComputeTargetWidth(srcW, maxWidth, scale);
        if (targetW is null || targetW.Value >= srcW)
            return new DownscaleResult(false, path, srcW, srcH, null, srcW, srcH, null);

        // 縦横比を保ったまま高さを決める（最低 1 px）。
        var dstW = Math.Max(MinSide, targetW.Value);
        var dstH = Math.Max(MinSide, (int)Math.Round(srcH * (double)dstW / srcW));

        try
        {
            var scaled = Resample(src, dstW, dstH);

            // keep_full のときだけフル解像度版を退避する（既存があれば上書き）。
            string? fullPath = null;
            if (keepFull)
            {
                fullPath = System.IO.Path.ChangeExtension(path, null) + FullSuffix;
                File.Copy(path, fullPath, overwrite: true);
            }

            WritePng(scaled, path);
            return new DownscaleResult(true, path, dstW, dstH, fullPath, srcW, srcH, null);
        }
        catch (Exception ex)
        {
            // 縮小に失敗してもフル解像度の PNG は残っている。
            return new DownscaleResult(false, path, srcW, srcH, null, srcW, srcH,
                $"縮小に失敗しました（フル解像度のまま返します）: {ex.Message}");
        }
    }

    // ── 内部処理 ─────────────────────────────────────────────────

    /// <summary>
    /// max_width と scale から縮小後の目標幅を決める。
    /// 両方指定された場合は「より小さくなるほう」を採用する（どちらも上限として働く）。
    /// </summary>
    /// <returns>目標幅。縮小指定がなければ null。</returns>
    private static int? ComputeTargetWidth(int srcWidth, int? maxWidth, double? scale)
    {
        int? byMax = maxWidth is > 0 ? maxWidth : null;

        int? byScale = null;
        if (scale is not null && scale.Value > MinScale && scale.Value < MaxScale)
            byScale = (int)Math.Round(srcWidth * scale.Value);

        if (byMax is null)   return byScale;
        if (byScale is null) return byMax;
        return Math.Min(byMax.Value, byScale.Value);
    }

    /// <summary>
    /// PNG をファイルハンドルを掴んだままにせず読み込む（同じパスへ書き戻すため）。
    /// </summary>
    private static BitmapSource LoadDetached(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption   = BitmapCacheOption.OnLoad;    // 読み終えたらファイルを離す
        bmp.CreateOptions = BitmapCreateOptions.None;
        bmp.UriSource     = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>高品質フィルタで指定画素数へ描き直す。</summary>
    private static BitmapSource Resample(BitmapSource src, int width, int height)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
            dc.DrawImage(src, new Rect(0, 0, width, height));

        var rtb = new RenderTargetBitmap(width, height, OutputDpi, OutputDpi, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>BitmapSource を PNG として書き出す。</summary>
    private static void WritePng(BitmapSource bmp, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(fs);
    }
}
