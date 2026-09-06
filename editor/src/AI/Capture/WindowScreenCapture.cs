// ============================================================
//  WindowScreenCapture.cs — 指定ウィンドウの画面キャプチャ（PNG 保存）
//
//  MCP ツール seed_screenshot の実体。指定した HWND が画面上で占める矩形を
//  「画面 DC（GetDC(NULL)）からの BitBlt」で取り込み、PNG として書き出す。
//
//  【なぜ画面 DC からの BitBlt か（PrintWindow / BitBlt(hwnd) ではない理由）】
//   ビューポートに埋め込まれているランタイムウィンドウは wgpu(DX12) の
//   スワップチェーンを直接 Present する GPU ウィンドウであり、
//   ウィンドウ DC への BitBlt / PrintWindow では中身が取得できない（真っ黒になる）。
//   これは FrozenFramePreview.cs のコメントにある既知の制約と同じ理由。
//   一方、画面 DC は DWM が合成した「実際に画面に出ている絵」を保持しているため、
//   GPU ウィンドウの内容もそのまま取得できる。
//
//  【制約】
//   画面に映っているものを撮る方式なので、対象ウィンドウが
//     ・最小化されている / 画面外にある
//     ・他ウィンドウに隠れている
//   場合は正しい絵が撮れない。呼び出し側へ警告文字列で通知する。
// ============================================================

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SEEDEditor.AI.Capture;

/// <summary>ウィンドウキャプチャの結果。</summary>
/// <param name="Ok">キャプチャと PNG 書き出しに成功したか。</param>
/// <param name="Path">書き出した PNG の絶対パス（失敗時は空文字列）。</param>
/// <param name="Width">キャプチャ画像の幅（ピクセル）。</param>
/// <param name="Height">キャプチャ画像の高さ（ピクセル）。</param>
/// <param name="Error">失敗理由（成功時は null）。</param>
/// <param name="Warning">成功したが結果が疑わしい場合の警告（例: 全面が黒）。</param>
public readonly record struct CaptureResult(
    bool    Ok,
    string  Path,
    int     Width,
    int     Height,
    string? Error,
    string? Warning);

/// <summary>
/// 指定ウィンドウの表示内容を画面から取り込んで PNG へ書き出す静的ユーティリティ。
/// GDI ハンドルは必ず finally で解放する（キャプチャは AI から連続で呼ばれうるため）。
/// </summary>
public static class WindowScreenCapture
{
    // ── 定数 ─────────────────────────────────────────────────────
    /// <summary>BitBlt のラスタオペレーション: ソースをそのままコピー。</summary>
    private const int SRCCOPY = 0x00CC0020;

    /// <summary>BitBlt のフラグ: レイヤードウィンドウ（半透明ウィンドウ）も結果へ含める。</summary>
    private const int CAPTUREBLT = 0x40000000;

    /// <summary>DwmGetWindowAttribute の属性 ID: ドロップシャドウを除いた実フレーム矩形。</summary>
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>キャプチャを許可する最大辺長（ピクセル）。異常な矩形でのメモリ爆発を防ぐ。</summary>
    private const int MAX_CAPTURE_EDGE = 16384;

    /// <summary>「全面が黒」の判定に使うサンプリング間隔（ピクセル単位）。</summary>
    private const int BLACK_CHECK_STRIDE_PX = 37;

    /// <summary>「黒」とみなす RGB 各成分の上限値。圧縮ノイズを考慮して 0 ではなく余裕を持たせる。</summary>
    private const byte BLACK_CHECK_THRESHOLD = 8;

    /// <summary>BGRA32 の 1 ピクセルあたりバイト数。</summary>
    private const int BYTES_PER_PIXEL_BGRA32 = 4;

    // ── Win32 P/Invoke ───────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int  ReleaseDC(nint hWnd, nint hDC);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hWnd, ref POINT pt);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint hObj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint hObj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(
        nint hdcDest, int xDest, int yDest, int w, int h,
        nint hdcSrc,  int xSrc,  int ySrc,  int rop);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hWnd, int attr, out RECT value, int size);

    // ── 公開メソッド ─────────────────────────────────────────────

    /// <summary>
    /// 指定ウィンドウを画面からキャプチャして PNG を書き出す。
    /// </summary>
    /// <param name="hwnd">キャプチャ対象のウィンドウハンドル。</param>
    /// <param name="clientAreaOnly">
    /// true: クライアント領域のみ（枠・タイトルバーを除く。ビューポート用）。
    /// false: ウィンドウ全体（DWM の実フレーム矩形。エディタウィンドウ用）。
    /// </param>
    /// <param name="outputPath">書き出し先の PNG 絶対パス。親ディレクトリは自動生成する。</param>
    public static CaptureResult Capture(nint hwnd, bool clientAreaOnly, string outputPath)
    {
        // ── 対象ウィンドウの妥当性検査 ──────────────────────────
        if (hwnd == nint.Zero || !IsWindow(hwnd))
            return Fail("キャプチャ対象のウィンドウハンドルが無効です（ランタイム未起動の可能性）。");
        if (IsIconic(hwnd))
            return Fail("対象ウィンドウが最小化されています。画面に表示してから再実行してください。");
        if (!IsWindowVisible(hwnd))
            return Fail("対象ウィンドウが非表示です。画面に表示してから再実行してください。");

        // ── キャプチャ矩形（スクリーン座標・物理ピクセル）を求める ──
        if (!TryGetScreenRect(hwnd, clientAreaOnly, out var rect))
            return Fail("対象ウィンドウの矩形取得に失敗しました。");

        int width  = rect.Right  - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            return Fail($"対象ウィンドウのサイズが不正です（{width}x{height}）。");
        if (width > MAX_CAPTURE_EDGE || height > MAX_CAPTURE_EDGE)
            return Fail($"対象ウィンドウが大きすぎます（{width}x{height}）。");

        // ── 画面 DC → メモリ DC へ BitBlt ─────────────────────────
        nint screenDc = nint.Zero, memDc = nint.Zero, bitmap = nint.Zero, oldBitmap = nint.Zero;
        try
        {
            screenDc = GetDC(nint.Zero);
            if (screenDc == nint.Zero) return Fail("画面デバイスコンテキストの取得に失敗しました。");

            memDc = CreateCompatibleDC(screenDc);
            if (memDc == nint.Zero) return Fail("メモリデバイスコンテキストの生成に失敗しました。");

            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == nint.Zero) return Fail("ビットマップの生成に失敗しました。");

            oldBitmap = SelectObject(memDc, bitmap);

            if (!BitBlt(memDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, SRCCOPY | CAPTUREBLT))
                return Fail("画面の転送（BitBlt）に失敗しました。");

            // ── HBITMAP → WPF BitmapSource → PNG ─────────────────
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, nint.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            var warning = IsMostlyBlack(source)
                ? "画像がほぼ真っ黒です。対象ウィンドウが他のウィンドウに隠れているか、"
                  + "描画前の可能性があります（本方式は画面に映っている内容を撮ります）。"
                : null;

            WritePng(source, outputPath);
            return new CaptureResult(true, outputPath, width, height, null, warning);
        }
        catch (Exception ex)
        {
            return Fail($"キャプチャ中に例外が発生しました: {ex.Message}");
        }
        finally
        {
            // GDI ハンドルは確保と逆順で確実に解放する（リークするとエディタごと不安定になる）
            if (memDc != nint.Zero && oldBitmap != nint.Zero) SelectObject(memDc, oldBitmap);
            if (bitmap    != nint.Zero) DeleteObject(bitmap);
            if (memDc     != nint.Zero) DeleteDC(memDc);
            if (screenDc  != nint.Zero) ReleaseDC(nint.Zero, screenDc);
        }
    }

    // ── 内部ヘルパー ─────────────────────────────────────────────

    /// <summary>失敗結果を組み立てる（呼び出し箇所を短く保つためのヘルパー）。</summary>
    private static CaptureResult Fail(string error) =>
        new(false, "", 0, 0, error, null);

    /// <summary>
    /// キャプチャ対象のスクリーン座標矩形を求める。
    /// クライアント領域指定時は GetClientRect + ClientToScreen、
    /// ウィンドウ全体指定時は DWM の実フレーム矩形（取得失敗時は GetWindowRect）を使う。
    /// </summary>
    private static bool TryGetScreenRect(nint hwnd, bool clientAreaOnly, out RECT rect)
    {
        if (clientAreaOnly)
        {
            if (!GetClientRect(hwnd, out var client)) { rect = default; return false; }
            var origin = new POINT { X = client.Left, Y = client.Top };
            if (!ClientToScreen(hwnd, ref origin))    { rect = default; return false; }
            rect = new RECT
            {
                Left   = origin.X,
                Top    = origin.Y,
                Right  = origin.X + (client.Right  - client.Left),
                Bottom = origin.Y + (client.Bottom - client.Top),
            };
            return true;
        }

        // ウィンドウ全体: GetWindowRect は Win10 以降だと不可視のドロップシャドウ分だけ
        // 大きい矩形を返すため、DWM の実フレーム矩形を優先する。
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                out rect, Marshal.SizeOf<RECT>()) == 0)
            return true;

        return GetWindowRect(hwnd, out rect);
    }

    /// <summary>
    /// 画像がほぼ真っ黒かどうかを判定する。
    /// 全画素を見ると無駄なので一定間隔でサンプリングし、1 つでも明るい画素があれば false。
    /// </summary>
    private static bool IsMostlyBlack(BitmapSource source)
    {
        try
        {
            // 画素フォーマットを BGRA32 に揃えてからバイト列を取り出す
            var bgra   = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0.0);
            int stride = bgra.PixelWidth * BYTES_PER_PIXEL_BGRA32;
            var pixels = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(pixels, stride, 0);

            int step = BLACK_CHECK_STRIDE_PX * BYTES_PER_PIXEL_BGRA32;
            for (int i = 0; i + 2 < pixels.Length; i += step)
            {
                if (pixels[i]     > BLACK_CHECK_THRESHOLD  // B
                 || pixels[i + 1] > BLACK_CHECK_THRESHOLD  // G
                 || pixels[i + 2] > BLACK_CHECK_THRESHOLD) // R
                    return false;
            }
            return true;
        }
        catch
        {
            // 判定できない場合は警告を出さない側（黒ではない）に倒す
            return false;
        }
    }

    /// <summary>BitmapSource を PNG としてファイルへ書き出す。親ディレクトリは自動生成する。</summary>
    private static void WritePng(BitmapSource source, string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(fs);
    }
}
