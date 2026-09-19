// ============================================================
//  WaveformThumbnailRenderer.cs — 音声ファイルの波形サムネイルを作る
//
//  【役割】
//  音声を復号して列ごとのピークを求め（重い・UI スレッド外）、
//  それを縦線の集まりとして描いて PNG にする（軽い・UI スレッド）。
//
//  【2 段に分ける理由】
//  復号は数分の BGM で数百 ms〜数秒かかる。UI スレッドでやると一覧が固まる。
//  一方、WPF の描画オブジェクト（DrawingVisual / RenderTargetBitmap）は
//  作ったスレッドに紐づくため、別スレッドで作ったものを UI へ直接は載せられない。
//  そこで「重い復号は Task で、軽い描画は UI スレッドで」と分ける。
//  描いた結果は Freeze() してからタイルへ渡す（以後どのスレッドからでも安全に使える）。
//
//  【形式アイコンを消さない】
//  生成前・生成中・失敗時は、呼び出し側が先に敷いた形式アイコン
//  （FileTypeIcons の音声アイコン）をそのまま残す。ここが null を返したときに
//  呼び出し側が何もしない、という約束でその挙動を守る
//  （.claude/rules/editor-icons.md の「フォールバック（削ってはいけない挙動）」）。
//
//  【キャッシュ】
//  結果は PNG として <プロジェクト>/cache/editor/waveforms/ へ置く
//  （場所と名前の規則は Assets/WaveformThumbnailCacheKey.cs）。
//  2 回目以降はデコードせず PNG を読むだけになる。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SEEDEditor.Assets;
using SEEDEditor.Audio;

namespace SEEDEditor.Controls;

/// <summary>
/// 音声の波形サムネイルを生成する。状態を持たない静的クラス
/// （同時実行数の制限だけはプロセス全体で 1 つ持つ）。
/// </summary>
public static class WaveformThumbnailRenderer
{
    // ── 定数（描画）──────────────────────────────────────────────

    /// <summary>
    /// 波形の縦線 1 本の太さ（DIP）。
    /// 1 列 = 1 本なので、細くしすぎると高 DPI で消え、太くすると隣と重なる。
    /// </summary>
    private const double ColumnStrokeThickness = 1.0;

    /// <summary>中心線の太さ（DIP）。</summary>
    private const double CenterLineThickness = 1.0;

    /// <summary>
    /// 波形が縦方向に使う割合（0〜1）。
    /// 1.0 にすると振り切った波が上下の端で切れて「切れているのか端なのか」が
    /// 分からなくなるため、少し余白を残す。
    /// </summary>
    private const double VerticalFillRatio = 0.86;

    /// <summary>
    /// 帯の高さがこれ未満（DIP）の列は、この値まで太らせる。
    /// 無音に近い区間でも「ここに音の軌跡がある」と分かるようにするため。
    /// </summary>
    private const double MinColumnHeight = 1.0;

    /// <summary>サンプル値の想定範囲（-1.0〜+1.0）の片側。</summary>
    private const float FullScale = 1.0f;

    /// <summary>PNG の解像度（DIP と 1:1 にしてぼけを避ける）。</summary>
    private const double RenderDpi = 96.0;

    // ── 定数（復号）──────────────────────────────────────────────

    /// <summary>
    /// 一度に読むサンプル数（float 単位）。I/O と呼び出し回数を減らすためのバッファ長。
    /// チャンネル数（1/2/4/8）で割り切れる値にして、フレームの端数が出ないようにする。
    /// </summary>
    private const int ReadBufferSampleCount = 16384;

    /// <summary>
    /// 同時に走らせる復号の上限。
    ///
    /// <para>
    /// 音声の復号はディスク読み出しと CPU の両方を使う。フォルダに音声が数十個あると、
    /// 制限しなければ一斉に走ってエディタ全体の操作が重くなる。
    /// 2 本なら、1 本が大きい BGM で詰まっても短い効果音が隣で進める。
    /// </para>
    /// </summary>
    private const int MaxConcurrentDecodes = 2;

    /// <summary>復号の同時実行数を抑える門（プロセス全体で共有）。</summary>
    private static readonly SemaphoreSlim DecodeGate = new(MaxConcurrentDecodes, MaxConcurrentDecodes);

    // ── 色（テーマ表から引く。ここで色を決めない）──────────────────

    /// <summary>波形の帯の色。</summary>
    private static readonly Brush ColumnBrush = MakeFrozenBrush(Theme.SeedColorTable.WAVEFORM_FG);

    /// <summary>中心線の色。</summary>
    private static readonly Brush CenterLineBrush = MakeFrozenBrush(Theme.SeedColorTable.WAVEFORM_CENTER_LINE);

    /// <summary>波形の帯を描くペン。</summary>
    private static readonly Pen ColumnPen = MakeFrozenPen(ColumnBrush, ColumnStrokeThickness);

    /// <summary>中心線を描くペン。</summary>
    private static readonly Pen CenterLinePen = MakeFrozenPen(CenterLineBrush, CenterLineThickness);

    // ── 復号（UI スレッド外で呼ぶこと）────────────────────────────

    /// <summary>
    /// 音声を復号して列ごとのピークを求める。**重いので UI スレッドから呼ばないこと。**
    /// </summary>
    /// <param name="audioPath">音声ファイルの絶対パス。</param>
    /// <param name="columnCount">列数（= サムネイルの横ピクセル数）。</param>
    /// <param name="cancellation">
    /// 打ち切り。フォルダを移動したなど、結果がもう要らなくなったときに立てる。
    /// </param>
    /// <returns>
    /// 列ごとのピーク。開けない・読めない・打ち切られたときは <c>null</c>
    /// （呼び出し側は形式アイコンのままにする）。
    /// </returns>
    public static async Task<WaveformColumn[]?> DecodeColumnsAsync(
        string audioPath, int columnCount, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || columnCount < 1) return null;

        try
        {
            await DecodeGate.WaitAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            return await Task.Run(() => DecodeColumns(audioPath, columnCount, cancellation),
                                  cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            // 壊れたファイル・権限なし・復号器が無いなど。形式アイコンのままにする。
            return null;
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    /// <summary>
    /// 復号の本体（同期）。読みながら列へ畳み込むので、全サンプルをメモリに載せない。
    /// </summary>
    /// <param name="audioPath">音声ファイルの絶対パス。</param>
    /// <param name="columnCount">列数。</param>
    /// <param name="cancellation">打ち切り。</param>
    /// <returns>列ごとのピーク。打ち切られたときは null。</returns>
    private static WaveformColumn[]? DecodeColumns(
        string audioPath, int columnCount, CancellationToken cancellation)
    {
        using var reader = AudioDecoding.OpenReader(audioPath, out var sampleProvider);

        int channels = Math.Max(1, sampleProvider.WaveFormat?.Channels ?? 1);
        var accumulator = new WaveformPeakAccumulator(
            columnCount, AudioDecoding.EstimateFrameCount(reader));

        var buffer = new float[ReadBufferSampleCount];
        int read;
        while ((read = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (cancellation.IsCancellationRequested) return null;
            accumulator.AddInterleaved(buffer, read, channels);
        }

        return accumulator.Build();
    }

    // ── 描画（UI スレッドで呼ぶこと）──────────────────────────────

    /// <summary>
    /// 列ごとのピークを画像へ描く。**WPF のオブジェクトを作るので UI スレッドで呼ぶこと。**
    /// 返す画像は <c>Freeze()</c> 済みで、以後どのスレッドからでも使える。
    /// </summary>
    /// <param name="columns">列ごとのピーク（<paramref name="widthPx"/> 件を想定）。</param>
    /// <param name="widthPx">出力の横ピクセル数。</param>
    /// <param name="heightPx">出力の縦ピクセル数。</param>
    /// <returns>描いた画像。描けないときは <c>null</c>。</returns>
    public static BitmapSource? RenderColumns(
        IReadOnlyList<WaveformColumn>? columns, int widthPx, int heightPx)
    {
        if (columns is null || columns.Count == 0) return null;
        if (widthPx < 1 || heightPx < 1) return null;

        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // 背景は塗らない。タイルの地色（選択・ホバーで変わる）を透かすため。
                double centerY   = heightPx / 2.0;
                double halfRange = centerY * VerticalFillRatio;

                // 中心線（振幅 0 の高さ）。無音の区間でも軌跡が見えるように引く。
                dc.DrawLine(CenterLinePen, new Point(0, centerY), new Point(widthPx, centerY));

                // 列を 1 本ずつ縦線で描く。列数と横ピクセル数が食い違っても
                // 幅いっぱいに収まるよう、比率で x を求める。
                for (int i = 0; i < columns.Count; i++)
                {
                    double x = (columns.Count == 1)
                        ? widthPx / 2.0
                        : (i + 0.5) * widthPx / columns.Count;

                    var column = columns[i];
                    double top    = centerY - Clamp01Signed(column.Max) * halfRange;
                    double bottom = centerY - Clamp01Signed(column.Min) * halfRange;

                    // 帯が潰れる列（1 サンプルしか入らない短い音・無音）でも
                    // 線が消えないよう、最低限の高さを確保する。
                    if (bottom - top < MinColumnHeight)
                    {
                        double mid = (top + bottom) / 2.0;
                        top    = mid - MinColumnHeight / 2.0;
                        bottom = mid + MinColumnHeight / 2.0;
                    }

                    dc.DrawLine(ColumnPen, new Point(x, top), new Point(x, bottom));
                }
            }

            var bitmap = new RenderTargetBitmap(
                widthPx, heightPx, RenderDpi, RenderDpi, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            // 描画に失敗しても形式アイコンのままにする。
            return null;
        }
    }

    // ── キャッシュ（PNG）──────────────────────────────────────────

    /// <summary>
    /// キャッシュ PNG を読む。無い・壊れているときは <c>null</c>。
    /// </summary>
    /// <param name="cachePath">キャッシュ PNG の絶対パス（null 可）。</param>
    /// <returns>読み込んだ画像（Freeze 済み）。読めなければ null。</returns>
    public static BitmapSource? TryLoadCachedPng(string? cachePath)
    {
        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath)) return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            // ファイルを掴みっぱなしにしない（掴んだままだと後で消せない・上書きできない）。
            bitmap.CacheOption  = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource    = new Uri(cachePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            // 書きかけ・壊れた PNG。次回作り直される。
            return null;
        }
    }

    /// <summary>
    /// 画像をキャッシュ PNG として保存する。失敗しても表示は続けられるので例外にしない。
    /// </summary>
    /// <param name="bitmap">保存する画像。</param>
    /// <param name="cachePath">保存先の絶対パス（null なら何もしない）。</param>
    /// <returns>保存できたか。</returns>
    public static bool TrySavePng(BitmapSource? bitmap, string? cachePath)
    {
        if (bitmap is null || string.IsNullOrWhiteSpace(cachePath)) return false;

        try
        {
            var dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            // 書きかけのファイルを他所から読まれないよう、一時名で書き切ってから置換する
            // （Assets/SafeFileWriter.cs と同じ考え方。あちらは世代バックアップを作るので
            //   キャッシュには使わない）。
            var temp = cachePath + TempSuffix;
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
            }
            File.Move(temp, cachePath, overwrite: true);
            return true;
        }
        catch
        {
            // 書き込み権限が無い・ディスクが一杯など。表示は続く（次回また作る）。
            return false;
        }
    }

    /// <summary>キャッシュ書き込み中の一時ファイルに付ける拡張子。</summary>
    private const string TempSuffix = ".tmp";

    // ── 内部 ─────────────────────────────────────────────────────

    /// <summary>サンプル値を -1.0〜+1.0 に収める（振り切った音で枠外へはみ出さないように）。</summary>
    /// <param name="value">サンプル値。</param>
    private static double Clamp01Signed(float value)
        => Math.Clamp(value, -FullScale, FullScale);

    /// <summary>16 進の色文字列から凍結済みブラシを作る。</summary>
    /// <param name="hex">"#RRGGBB" 形式の色。</param>
    private static Brush MakeFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>凍結済みのペンを作る。</summary>
    /// <param name="brush">線の色。</param>
    /// <param name="thickness">線の太さ。</param>
    private static Pen MakeFrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
