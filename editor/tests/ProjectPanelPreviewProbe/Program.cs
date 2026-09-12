using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using SEEDEditor.Assets;
using SEEDEditor.Controls;

namespace ProjectPanelPreviewProbe;

/// <summary>
/// フォントサムネイル生成（<see cref="FontThumbnailRenderer"/>）と
/// 画像寸法取得（<see cref="ImageDimensionsProbe"/>）を、実ファイルへ向けて走らせる検証用コンソール。
///
/// 自動テスト（ProjectPanelLogicTests）は環境に依らない純ロジックだけを見るので、
/// 「実際にそのフォントでグリフが描けたか」「その PNG の寸法が読めたか」はここで確かめる。
///
/// フォントは「描けた／描けなかった」だけでなく、生成画像の不透明ピクセル比率も出す。
/// 真っ白（何も描けていない）サムネイルを成功と誤認しないための検算で、
/// 比率が 0% なら実質失敗とみなせる。
///
/// 入力ファイルは読むだけで、書き換えは一切しない。
/// </summary>
public static class Program
{
    /// <summary>PNG 書き出し先を指定するオプション名。</summary>
    private const string OutOption = "--out";

    /// <summary>不透明とみなすアルファ値の下限（0-255）。</summary>
    private const byte OpaqueAlphaThreshold = 8;

    /// <summary>Pbgra32 の 1 ピクセルあたりバイト数。</summary>
    private const int BytesPerPixel = 4;

    /// <summary>Pbgra32 のピクセル内アルファ成分のオフセット。</summary>
    private const int AlphaByteOffset = 3;

    /// <summary>比率を百分率へ直す係数。</summary>
    private const double PercentScale = 100.0;

    /// <summary>
    /// エントリポイント。引数のファイル／フォルダを走査してプレビュー生成を試す。
    /// </summary>
    /// <returns>1 件でもプレビューを生成できたら 0、そうでなければ 1。</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        var targets = new List<string>();
        string? outDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == OutOption && i + 1 < args.Length) outDir = args[++i];
            else                                            targets.Add(args[i]);
        }

        if (targets.Count == 0)
        {
            Console.WriteLine("使い方: ProjectPanelPreviewProbe <ファイルまたはフォルダ> [...] [" + OutOption + " <出力先>]");
            return 1;
        }

        if (outDir != null) Directory.CreateDirectory(outDir);

        // パネルが実際に通る経路（ワーカースレッドでのヘッダ読み）を先に 1 件だけ検算する。
        // キャッシュに載る前でないとワーカースレッドを通らないので、同期の一覧より前に行う。
        VerifyAsyncPath(targets);

        int fontOk = 0, fontNg = 0, imageOk = 0, imageNg = 0;

        foreach (var file in EnumerateFiles(targets))
        {
            switch (AssetPreviewKinds.OfPath(file))
            {
                case AssetPreviewKind.Font:
                    if (ProbeFont(file, outDir)) fontOk++; else fontNg++;
                    break;
                case AssetPreviewKind.Image:
                    if (ProbeImage(file)) imageOk++; else imageNg++;
                    break;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"フォント: 成功 {fontOk} / 失敗 {fontNg}");
        Console.WriteLine($"画像寸法: 成功 {imageOk} / 失敗 {imageNg}");
        return fontOk + imageOk > 0 ? 0 : 1;
    }

    /// <summary>
    /// 最初に見つかった画像 1 枚を <see cref="ImageDimensionsProbe.GetAsync"/> で取得し、
    /// 「ワーカースレッドでヘッダを読む」経路が動くことを確かめる。
    ///
    /// プロジェクトパネルは UI を止めないためにこの非同期経路を使う。
    /// WPF のデコーダはスレッド親和性を持つ型を含むため、同期経路が通っても
    /// ワーカースレッドで例外になる可能性があり、そこを実際に走らせて潰しておく。
    /// </summary>
    /// <param name="targets">走査対象（ファイルまたはフォルダ）。</param>
    private static void VerifyAsyncPath(IEnumerable<string> targets)
    {
        foreach (var file in EnumerateFiles(targets))
        {
            if (AssetPreviewKinds.OfPath(file) != AssetPreviewKind.Image) continue;

            try
            {
                var size = ImageDimensionsProbe.GetAsync(file).GetAwaiter().GetResult();
                Console.WriteLine($"[ASYNC] ワーカースレッド経路: "
                                + $"{(size is { } s ? s.ToTooltipText() : "取得不可")} : {file}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ASYNC] 例外: {ex.GetType().Name} {ex.Message} : {file}");
            }
            return;
        }
    }

    /// <summary>
    /// 指定されたファイル／フォルダを展開して、対象ファイルを列挙する。
    /// </summary>
    /// <param name="targets">ファイルまたはフォルダのパス。</param>
    private static IEnumerable<string> EnumerateFiles(IEnumerable<string> targets)
    {
        foreach (var target in targets)
        {
            if (File.Exists(target))
            {
                yield return target;
            }
            else if (Directory.Exists(target))
            {
                IEnumerable<string> found;
                try   { found = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var file in found) yield return file;
            }
            else
            {
                Console.WriteLine($"[skip ] 見つかりません: {target}");
            }
        }
    }

    // ── フォント ──────────────────────────────────────────────────

    /// <summary>
    /// フォント 1 個ぶんのサムネイル生成を試し、結果を 1 行で報告する。
    /// </summary>
    /// <param name="path">フォントファイルのパス。</param>
    /// <param name="outDir">PNG 書き出し先（null なら書き出さない）。</param>
    /// <returns>サムネイルを生成できたら true。</returns>
    private static bool ProbeFont(string path, string? outDir)
    {
        var bitmap = FontThumbnailRenderer.Render(path);
        if (bitmap == null)
        {
            Console.WriteLine($"[FONT ] 生成不可      : {path}");
            return false;
        }

        double opaqueRatio = MeasureOpaqueRatio(bitmap);
        Console.WriteLine($"[FONT ] {bitmap.PixelWidth}x{bitmap.PixelHeight} "
                        + $"不透明 {opaqueRatio * PercentScale:0.0}% : {path}");

        if (outDir != null) SavePng(bitmap, outDir, path);
        return true;
    }

    /// <summary>
    /// 生成画像のうち「何かが描かれている」ピクセルの比率を測る。
    /// </summary>
    /// <param name="bitmap">Pbgra32 のサムネイル。</param>
    /// <returns>0.0〜1.0 の比率。測れなければ 0。</returns>
    private static double MeasureOpaqueRatio(BitmapSource bitmap)
    {
        try
        {
            int stride = bitmap.PixelWidth * BytesPerPixel;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);

            int opaque = 0;
            for (int i = AlphaByteOffset; i < pixels.Length; i += BytesPerPixel)
                if (pixels[i] >= OpaqueAlphaThreshold) opaque++;

            int total = bitmap.PixelWidth * bitmap.PixelHeight;
            return total > 0 ? (double)opaque / total : 0.0;
        }
        catch
        {
            return 0.0;
        }
    }

    /// <summary>
    /// 生成したサムネイルを PNG として書き出す（目視確認用）。
    /// </summary>
    /// <param name="bitmap">書き出すサムネイル。</param>
    /// <param name="outDir">出力フォルダ。</param>
    /// <param name="sourcePath">元のフォントファイルのパス（出力名に使う）。</param>
    private static void SavePng(BitmapSource bitmap, string outDir, string sourcePath)
    {
        try
        {
            var name    = Path.GetFileNameWithoutExtension(sourcePath) + ".png";
            var outPath = Path.Combine(outDir, MakeSafeFileName(name));

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(outPath);
            encoder.Save(stream);

            Console.WriteLine($"         -> {outPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"         -> 書き出し失敗: {ex.Message}");
        }
    }

    /// <summary>ファイル名に使えない文字を '_' へ置き換える。</summary>
    /// <param name="name">元のファイル名。</param>
    private static string MakeSafeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name;
    }

    // ── 画像 ──────────────────────────────────────────────────────

    /// <summary>
    /// 画像 1 枚ぶんの寸法取得を試し、結果を 1 行で報告する。
    /// </summary>
    /// <param name="path">画像ファイルのパス。</param>
    /// <returns>寸法を取得できたら true。</returns>
    private static bool ProbeImage(string path)
    {
        var size = ImageDimensionsProbe.Get(path);
        if (size is not { } pixels)
        {
            Console.WriteLine($"[IMAGE] 寸法取得不可   : {path}");
            return false;
        }

        Console.WriteLine($"[IMAGE] {pixels.ToTooltipText(),-16}: {path}");
        return true;
    }
}
