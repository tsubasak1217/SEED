using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using SEEDEditor.Assets;
using SEEDEditor.Audio;
using SEEDEditor.Controls;
using SEEDEditor.Theme;

namespace AudioTilePreviewProbe;

/// <summary>
/// 音声タイル（波形サムネイル＋試聴ボタン）を実際に組み立てて PNG へ書き出す検証用コンソール。
///
/// <para>
/// 自動テスト（ProjectPanelLogicTests）はピーク計算と状態遷移という
/// 「数で確かめられること」だけを見る。ここで確かめるのはその先で、
///   ・実際に音声を復号して波形が描けるか（無音・正弦波・減衰・ステレオ）
///   ・波形がタイルの地色から読み取れるか
///   ・試聴ボタンのアイコン（Icon.Play / Icon.Stop）が解決できているか
///   ・共通ボタン書式が当たっているか（自前の色を使っていないか）
/// という「見ないと分からないこと」。
/// </para>
///
/// <para>
/// 音は鳴らさない（出力デバイスには触れない）。入力の音声も自分で合成するので、
/// 実アセットやプロジェクトには一切触れない。
/// </para>
/// </summary>
public static class Program
{
    // ── タイルの寸法（ProjectPanel の私有定数の写し）──────────────
    //
    //  ProjectPanel の定数は private なので参照できない。ここは「見た目を確かめる
    //  ための模型」であって本物のタイルではない、という前提で同じ値を置く。
    //  片方だけ変えても壊れはしないが、絵が実物とずれるので気づいたら揃えること。

    /// <summary>タイル 1 枚の幅（px）。</summary>
    private const double TileWidth = 116;

    /// <summary>タイル 1 枚の高さ（px）。</summary>
    private const double TileHeight = 116 + 16;

    /// <summary>サムネイル表示時の一辺（px）。</summary>
    private const double ThumbnailDisplaySize = 90;

    /// <summary>サムネイルの上マージン（px）。</summary>
    private const double ThumbnailTopMargin = 3;

    /// <summary>サムネイルの角丸半径（px）。</summary>
    private const double ThumbnailCornerRadius = 3;

    /// <summary>ファイル名の文字サイズ（px）。</summary>
    private const double TileNameFontSize = 11;

    /// <summary>タイルの角丸半径（px）。</summary>
    private const double TileCornerRadius = 4;

    /// <summary>
    /// 試聴ボタンのアイコン一辺（px）。ProjectPanel.AudioPreview.cs の AudioPreviewIconSize と同じ値にすること
    /// （このプローブはタイルの構造を写して描いているだけで、パネルの実コードは通らない）。
    /// </summary>
    private const double PreviewIconSize = 14;

    /// <summary>試聴ボタンとファイル名の間隔（px）。ProjectPanel 側の AudioPreviewButtonGap と同じ値。</summary>
    private const double PreviewButtonGap = 3;

    /// <summary>ファイル名の折り返し幅（px。ボタンぶん狭めた後の値）。</summary>
    private const double NameMaxWidth = 106 - 21;

    // ── 合成する音声の仕様 ────────────────────────────────────────

    /// <summary>合成する音声のサンプルレート（Hz）。</summary>
    private const int SampleRate = 44100;

    /// <summary>合成する音声の長さ（秒）。</summary>
    private const double DurationSeconds = 1.5;

    /// <summary>正弦波の周波数（Hz）。</summary>
    private const double SineFrequency = 440.0;

    /// <summary>減衰音の時定数（秒）。小さいほど早く消える。</summary>
    private const double DecayTimeConstant = 0.18;

    /// <summary>合成時の最大振幅（0〜1）。</summary>
    private const float PeakAmplitude = 0.85f;

    /// <summary>ステレオ右チャンネルの振幅比（左右差が波形に出ないことの確認用）。</summary>
    private const float RightChannelRatio = 0.25f;

    // ── 出力 ──────────────────────────────────────────────────────

    /// <summary>出力先を指定するオプション名。</summary>
    private const string OutOption = "--out";

    /// <summary>出力先を指定しなかったときの既定（実行ファイルの隣）。</summary>
    private const string DefaultOutDir = "audio_tile_preview";

    /// <summary>PNG の解像度（DIP と 1:1 にしてぼけを避ける）。</summary>
    private const double RenderDpi = 96.0;

    /// <summary>
    /// エントリポイント。音声を合成し、タイルを組み立てて PNG を書き出す。
    /// </summary>
    /// <param name="args">コマンドライン引数（--out &lt;出力先&gt;）。</param>
    /// <returns>1 枚でも書き出せたら 0。</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        var outDir = ResolveOutDir(args);
        Directory.CreateDirectory(outDir);

        // App を生成するとアイコン辞書（Icons.xaml）と共通ボタン書式が
        // Application.Current.Resources に載る。Run() は呼ばない（画面は出ない）。
        var app = new SEEDEditor.App();
        app.InitializeComponent();

        // 音声は一時フォルダへ合成する。実アセットには触れない。
        var workDir = Path.Combine(Path.GetTempPath(),
                                   "seed_audio_tile_probe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        int written = 0;
        try
        {
            foreach (var (name, make) in Scenarios())
            {
                var wavPath = Path.Combine(workDir, name + ".wav");
                try
                {
                    make(wavPath);
                    if (RenderTile(wavPath, outDir, name)) written++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [FAIL] {name}: {ex.Message}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* 一時物の掃除失敗は無視 */ }
        }

        Console.WriteLine();
        Console.WriteLine($"{written} 件の PNG を {outDir} に書き出しました。");
        return written > 0 ? 0 : 1;
    }

    // ── 検証する音の種類 ──────────────────────────────────────────

    /// <summary>
    /// 書き出す絵の一覧（名前 → その WAV を作る関数）。
    /// </summary>
    /// <returns>シナリオの列。</returns>
    private static IEnumerable<(string Name, Action<string> Make)> Scenarios()
    {
        // 無音: 中心線だけが見えること（真っ白でも真っ黒でもない）
        yield return ("01_silence", path => WriteWav(path, channels: 1,
            sample: (t, _) => 0f));

        // 正弦波: 帯が上下いっぱいに開くこと
        yield return ("02_sine", path => WriteWav(path, channels: 1,
            sample: (t, _) => (float)(Math.Sin(2 * Math.PI * SineFrequency * t) * PeakAmplitude)));

        // 減衰音（効果音らしい形）: 左が太く右へ細くなること
        yield return ("03_decay", path => WriteWav(path, channels: 1,
            sample: (t, _) => (float)(Math.Sin(2 * Math.PI * SineFrequency * t)
                                    * Math.Exp(-t / DecayTimeConstant) * PeakAmplitude)));

        // 前半だけ鳴る: 後半が中心線だけになること（無音区間が読み取れるか）
        yield return ("04_half_silent", path => WriteWav(path, channels: 1,
            sample: (t, _) => t < DurationSeconds / 2
                ? (float)(Math.Sin(2 * Math.PI * SineFrequency * t) * PeakAmplitude)
                : 0f));

        // ステレオ（左右で振幅が違う）: 混ぜて 1 本になること
        yield return ("05_stereo", path => WriteWav(path, channels: 2,
            sample: (t, ch) => (float)(Math.Sin(2 * Math.PI * SineFrequency * t) * PeakAmplitude
                                     * (ch == 0 ? 1.0 : RightChannelRatio))));
    }

    // ── 音声の合成 ────────────────────────────────────────────────

    /// <summary>
    /// 指定の式で WAV を書き出す（32bit float・<see cref="SampleRate"/> Hz）。
    /// </summary>
    /// <param name="path">出力先。</param>
    /// <param name="channels">チャンネル数。</param>
    /// <param name="sample">時刻（秒）とチャンネル番号からサンプル値を返す式。</param>
    private static void WriteWav(string path, int channels, Func<double, int, float> sample)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, channels);
        using var writer = new WaveFileWriter(path, format);

        int frames = (int)(SampleRate * DurationSeconds);
        var buffer = new float[channels];
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / SampleRate;
            for (int c = 0; c < channels; c++) buffer[c] = sample(t, c);
            writer.WriteSamples(buffer, 0, channels);
        }
    }

    // ── タイルの組み立てと書き出し ────────────────────────────────

    /// <summary>
    /// 1 つの音声について、波形サムネイルと試聴ボタンを載せたタイルを描いて PNG にする。
    ///
    /// <para>
    /// 再生中（停止アイコン）と停止中（再生アイコン）の両方を横に並べて出す。
    /// ボタンの状態でアイコンが入れ替わることを 1 枚で確かめられるようにするため。
    /// </para>
    /// </summary>
    /// <param name="wavPath">合成済みの音声。</param>
    /// <param name="outDir">出力フォルダ。</param>
    /// <param name="name">出力ファイル名（拡張子なし）。</param>
    /// <returns>書き出せたら true。</returns>
    private static bool RenderTile(string wavPath, string outDir, string name)
    {
        int width  = WaveformThumbnailCacheKey.DefaultWidthPx;
        int height = WaveformThumbnailCacheKey.DefaultHeightPx;

        // 実際のパネルと同じ経路で復号する（ここが落ちるなら本番も落ちる）。
        var columns = WaveformThumbnailRenderer
            .DecodeColumnsAsync(wavPath, width).GetAwaiter().GetResult();
        if (columns is null)
        {
            Console.WriteLine($"  [FAIL] {name}: 復号できませんでした");
            return false;
        }

        var waveform = WaveformThumbnailRenderer.RenderColumns(columns, width, height);
        if (waveform is null)
        {
            Console.WriteLine($"  [FAIL] {name}: 波形を描けませんでした");
            return false;
        }

        // 停止中・再生中の 2 枚を横に並べる。
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(BuildTile(wavPath, waveform, playing: false));
        row.Children.Add(BuildTile(wavPath, waveform, playing: true));

        // パネルの地色を敷く（波形と地色のコントラストを目で見るため）。
        var host = new Border
        {
            Background = MakeBrush(SeedColorTable.SURFACE_WINDOW),
            Padding    = new Thickness(8),
            Child      = row,
        };

        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(host.DesiredSize.Width),
            (int)Math.Ceiling(host.DesiredSize.Height),
            RenderDpi, RenderDpi, PixelFormats.Pbgra32);
        bitmap.Render(host);

        var outPath = Path.Combine(outDir, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(outPath)) encoder.Save(stream);

        // 波形が「本当に描かれたか」を数でも裏取りする（真っ黒な絵を成功と誤認しないため）。
        bool silent = WaveformPeaks.IsSilent(columns, SilenceThreshold);
        Console.WriteLine($"  {name}: 列 {columns.Length} / 無音判定 {(silent ? "はい" : "いいえ")} -> {outPath}");
        return true;
    }

    /// <summary>無音とみなす振れ幅の上限（表示用の裏取りに使う）。</summary>
    private const float SilenceThreshold = 1e-4f;

    /// <summary>
    /// タイル 1 枚（波形サムネイル＋ファイル名＋試聴ボタン）を組み立てる。
    /// 構造は ProjectPanel の WrapTile に合わせてある。
    /// </summary>
    /// <param name="audioPath">音声ファイルのパス（名前とツールチップに使う）。</param>
    /// <param name="waveform">描き終えた波形。</param>
    /// <param name="playing">再生中の見た目にするなら true。</param>
    /// <returns>タイルの外枠。</returns>
    private static Border BuildTile(string audioPath, BitmapSource waveform, bool playing)
    {
        var image = new Image
        {
            Source              = waveform,
            Width               = ThumbnailDisplaySize,
            Height              = ThumbnailDisplaySize,
            Stretch             = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, ThumbnailTopMargin, 0, 0),
            Clip                = new RectangleGeometry(
                new Rect(0, 0, ThumbnailDisplaySize, ThumbnailDisplaySize),
                ThumbnailCornerRadius, ThumbnailCornerRadius),
        };

        var nameBlock = new TextBlock
        {
            Text                = Path.GetFileName(audioPath),
            Foreground          = MakeBrush(SeedColorTable.BUTTON_FG),
            FontSize            = TileNameFontSize,
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping        = TextWrapping.Wrap,
            MaxWidth            = NameMaxWidth,
        };

        var icon = AppIcon.Create(playing ? "Icon.Stop" : "Icon.Play", PreviewIconSize);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment   = VerticalAlignment.Center;

        var button = new Button
        {
            Content   = icon,
            MinWidth  = SeedButtonMetrics.IconHitAreaSize(PreviewIconSize),
            MinHeight = SeedButtonMetrics.IconHitAreaSize(PreviewIconSize),
            Padding   = new Thickness(0),
            // ボタンは名前の左。間隔は名前との間（右側）に取る。
            Margin    = new Thickness(0, 0, PreviewButtonGap, 0),
            VerticalAlignment          = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment   = VerticalAlignment.Center,
            ToolTip   = playing ? "試聴を止める" : "試聴する",
        };
        SeedButtonStyle.Apply(button, SeedButtonStyle.ICON);

        var nameRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        nameRow.Children.Add(button);
        nameRow.Children.Add(nameBlock);

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(image);
        stack.Children.Add(nameRow);

        return new Border
        {
            Width        = TileWidth,
            Height       = TileHeight,
            Margin       = new Thickness(3),
            // 停止中は通常のタイル（透明）、再生中は選択されている想定で薄く白を重ねる。
            Background   = playing
                ? MakeBrush(SeedColorTable.ICON_OVERLAY_PRESSED)
                : Brushes.Transparent,
            CornerRadius = new CornerRadius(TileCornerRadius),
            Child        = stack,
        };
    }

    // ── 小物 ──────────────────────────────────────────────────────

    /// <summary>16 進の色文字列からブラシを作る。</summary>
    /// <param name="hex">"#RRGGBB" または "#AARRGGBB"。</param>
    private static Brush MakeBrush(string hex)
        => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    /// <summary>出力先フォルダを決める（--out 指定が無ければ実行ファイルの隣）。</summary>
    /// <param name="args">コマンドライン引数。</param>
    private static string ResolveOutDir(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == OutOption) return args[i + 1];
        }
        return Path.Combine(AppContext.BaseDirectory, DefaultOutDir);
    }
}
