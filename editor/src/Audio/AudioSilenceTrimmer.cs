using System;
using System.Collections.Generic;
using System.IO;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SEEDEditor.Audio;

/// <summary>
/// 音声ファイル（WAV / MP3）の先頭・末尾の無音を切り落とすユーティリティ。
///
/// 【処理の流れ】
///  1. 走査パス : ファイル全体を float サンプル（-1.0〜+1.0）としてストリーム読みし、
///                しきい値を初めて／最後に超えたフレーム位置を求める。
///  2. 書き出し : 求めた範囲だけを新しいファイルへ書き出す。
///                 - WAV は元のバイト列をそのまま切り出してコピーする（ビット深度・
///                   サンプルレート・チャンネル数を完全に維持し、再量子化を起こさない）。
///                 - MP3 は Media Foundation で再エンコードする（元ビットレートに近い値を使う）。
///  3. 差し替え : 一時ファイルへ書いてから、上書き時のみ元を .bak へ退避して置き換える。
///
/// UI からは独立しており（WPF 参照なし）、テストハーネスからも直接呼べる。
/// 例外は内部で捕捉し、すべて <see cref="AudioTrimResult"/> として返す。
/// </summary>
public static class AudioSilenceTrimmer
{
    // ── 定数 ──────────────────────────────────────────────────────

    /// <summary>実際に処理できる拡張子（小文字）。</summary>
    private static readonly HashSet<string> ProcessableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3" };

    /// <summary>音声ファイルとして扱う拡張子。未対応形式もメニュー表示の判定に使う。</summary>
    private static readonly HashSet<string> AudioExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".ogg", ".flac" };

    /// <summary>WAV 拡張子。</summary>
    private const string WavExtension = ".wav";

    /// <summary>別名保存時にファイル名へ付けるサフィックス。</summary>
    private const string TrimmedNameSuffix = "_trim";

    /// <summary>上書き保存時に元ファイルを退避する拡張子。</summary>
    private const string BackupExtension = ".bak";

    /// <summary>書き出し中の一時ファイル名に付けるサフィックス（拡張子の前に入れる）。</summary>
    private const string TempNameSuffix = ".__seedtrim";

    /// <summary>走査時に一度に読むサンプル数（float 単位）。I/O 回数を減らすためのバッファ長。</summary>
    private const int ScanBufferSampleCount = 16384;

    /// <summary>ミリ秒 ⇔ 秒の換算係数。</summary>
    private const double MillisecondsPerSecond = 1000.0;

    /// <summary>Win32 のファイル共有違反エラーコード（ERROR_SHARING_VIOLATION）。</summary>
    private const int ErrorSharingViolation = 32;

    /// <summary>Win32 のロック違反エラーコード（ERROR_LOCK_VIOLATION）。</summary>
    private const int ErrorLockViolation = 33;

    /// <summary>HRESULT から Win32 エラーコード部分を取り出すマスク。</summary>
    private const int Win32ErrorCodeMask = 0xFFFF;

    /// <summary>1 バイトあたりのビット数（ビットレート算出用）。</summary>
    private const int BitsPerByte = 8;

    /// <summary>Media Foundation の初期化を一度だけ行うためのフラグ。</summary>
    private static bool _mediaFoundationStarted;

    /// <summary>_mediaFoundationStarted の排他用ロック。</summary>
    private static readonly object MediaFoundationLock = new();

    // ── 公開判定 API ──────────────────────────────────────────────

    /// <summary>
    /// 指定パスが「無音カットを実行できる」形式かどうかを返す（WAV / MP3）。
    /// </summary>
    /// <param name="path">対象ファイルのパス。</param>
    public static bool IsProcessable(string path)
        => ProcessableExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// 指定パスが音声ファイルかどうかを返す（未対応形式の ogg/flac も true）。
    /// メニュー項目を「表示するが無効化する」判定に使う。
    /// </summary>
    /// <param name="path">対象ファイルのパス。</param>
    public static bool IsAudioFile(string path)
        => AudioExtensions.Contains(Path.GetExtension(path));

    // ── 本体 ──────────────────────────────────────────────────────

    /// <summary>
    /// 音声ファイルの無音をカットして保存する。
    /// </summary>
    /// <param name="sourcePath">対象ファイルの絶対パス。</param>
    /// <param name="options">しきい値・余白・末尾カット・保存方法。</param>
    /// <returns>処理結果（例外は投げず、失敗も結果として返す）。</returns>
    public static AudioTrimResult Trim(string sourcePath, AudioTrimOptions options)
    {
        string extension = Path.GetExtension(sourcePath);

        // 未対応形式は何もせずに理由だけ返す（無理にデコードを試みない）。
        if (!ProcessableExtensions.Contains(extension))
        {
            return new AudioTrimResult
            {
                Status  = AudioTrimStatus.UnsupportedFormat,
                Message = $"{extension} は未対応の形式です（対応: .wav / .mp3）。",
            };
        }

        if (!File.Exists(sourcePath))
        {
            return new AudioTrimResult
            {
                Status  = AudioTrimStatus.Failed,
                Message = "ファイルが見つかりません。",
            };
        }

        try
        {
            // ── 1) 走査パス: 音が鳴っている範囲（フレーム単位）を求める ──
            LoudRange range;
            using (var reader = OpenSampleReader(sourcePath, out var sampleProvider))
            {
                range = ScanLoudRange(sampleProvider, options.ToLinearThreshold());
            }

            if (range.TotalFrames <= 0)
            {
                return new AudioTrimResult
                {
                    Status  = AudioTrimStatus.Failed,
                    Message = "音声データを読み取れませんでした（長さが 0 です）。",
                };
            }

            // 全区間がしきい値以下＝実質無音。切ると何も残らないので中止する。
            if (range.FirstLoudFrame < 0)
            {
                return new AudioTrimResult
                {
                    Status  = AudioTrimStatus.AllSilent,
                    Message = $"しきい値 {options.ThresholdDb:0.#} dB を超える音がありません。"
                            + "ファイルは変更していません（しきい値を下げて再試行してください）。",
                };
            }

            // ── 2) カット範囲を決める ──
            long paddingFrames = MillisecondsToFrames(options.PaddingMs, range.SampleRate);

            // 先頭: 最初に鳴ったフレームから余白分だけ手前へ戻す（0 未満にはしない）。
            long startFrame = Math.Max(0, range.FirstLoudFrame - paddingFrames);

            // 末尾: 末尾カットが有効なときだけ、最後に鳴ったフレーム＋余白で打ち切る。
            long endFrameExclusive = options.TrimTrailing
                ? Math.Min(range.TotalFrames, range.LastLoudFrame + 1 + paddingFrames)
                : range.TotalFrames;

            double removedLeadingMs  = FramesToMilliseconds(startFrame, range.SampleRate);
            double removedTrailingMs = FramesToMilliseconds(range.TotalFrames - endFrameExclusive, range.SampleRate);
            double outputDurationMs  = FramesToMilliseconds(endFrameExclusive - startFrame, range.SampleRate);

            // 切る所が無いなら書き出さない（MP3 の無駄な再エンコードによる劣化を避ける）。
            if (startFrame == 0 && endFrameExclusive == range.TotalFrames)
            {
                return new AudioTrimResult
                {
                    Status           = AudioTrimStatus.NothingToTrim,
                    Message          = "カットできる無音がありませんでした。ファイルは変更していません。",
                    OutputDurationMs = outputDurationMs,
                };
            }

            // ── 3) 一時ファイルへ書き出す ──
            string tempPath = MakeTempPath(sourcePath);
            try
            {
                if (extension.Equals(WavExtension, StringComparison.OrdinalIgnoreCase))
                    WriteTrimmedWav(sourcePath, tempPath, startFrame, endFrameExclusive);
                else
                    WriteTrimmedMp3(sourcePath, tempPath, startFrame, endFrameExclusive, range.Channels);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }

            // ── 4) 所定の場所へ差し替える ──
            var placement = PlaceOutput(sourcePath, tempPath, options.SaveMode);
            if (placement.Error != null)
            {
                TryDelete(tempPath);
                return placement.Error;
            }

            return new AudioTrimResult
            {
                Status            = AudioTrimStatus.Trimmed,
                Message           = "無音をカットしました。",
                RemovedLeadingMs  = removedLeadingMs,
                RemovedTrailingMs = removedTrailingMs,
                OutputDurationMs  = outputDurationMs,
                OutputPath        = placement.OutputPath,
                BackupPath        = placement.BackupPath,
            };
        }
        catch (Exception ex)
        {
            return new AudioTrimResult
            {
                Status  = AudioTrimStatus.Failed,
                Message = $"処理に失敗しました: {ex.Message}",
            };
        }
    }

    // ── 走査 ──────────────────────────────────────────────────────

    /// <summary>
    /// 音が鳴っているフレーム範囲。フレーム＝全チャンネル 1 組（＝時間軸上の 1 点）。
    /// </summary>
    /// <param name="FirstLoudFrame">最初にしきい値を超えたフレーム番号。全区間無音なら -1。</param>
    /// <param name="LastLoudFrame">最後にしきい値を超えたフレーム番号。全区間無音なら -1。</param>
    /// <param name="TotalFrames">総フレーム数。</param>
    /// <param name="SampleRate">サンプルレート（Hz）。</param>
    /// <param name="Channels">チャンネル数。</param>
    private readonly record struct LoudRange(
        long FirstLoudFrame,
        long LastLoudFrame,
        long TotalFrames,
        int  SampleRate,
        int  Channels);

    /// <summary>
    /// float サンプル列を走査し、しきい値を超える最初／最後のフレームと総フレーム数を求める。
    /// 全サンプルをメモリに保持せず逐次読みで判定するため、長尺の BGM でも一定メモリで動く。
    /// </summary>
    /// <param name="provider">走査対象のサンプルプロバイダ。</param>
    /// <param name="linearThreshold">線形しきい値（|sample| がこれを超えたら「音あり」）。</param>
    private static LoudRange ScanLoudRange(ISampleProvider provider, float linearThreshold)
    {
        int channels   = provider.WaveFormat.Channels;
        int sampleRate = provider.WaveFormat.SampleRate;

        // バッファ長はチャンネル数の倍数に丸め、フレーム境界をまたがないようにする。
        int bufferLength = Math.Max(channels, ScanBufferSampleCount / channels * channels);
        var buffer       = new float[bufferLength];

        long readSamples    = 0;   // 読み終えたサンプル総数（インターリーブ）
        long firstLoudFrame = -1;
        long lastLoudFrame  = -1;

        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                if (Math.Abs(buffer[i]) <= linearThreshold) continue;

                long frame = (readSamples + i) / channels;
                if (firstLoudFrame < 0) firstLoudFrame = frame;
                lastLoudFrame = frame;
            }
            readSamples += read;
        }

        return new LoudRange(firstLoudFrame, lastLoudFrame, readSamples / channels, sampleRate, channels);
    }

    /// <summary>
    /// 拡張子に応じたリーダーを開き、float サンプルとして読むためのプロバイダを取り出す。
    /// WAV は AudioFileReader、MP3 は Media Foundation（MediaFoundationReader）でデコードする。
    /// </summary>
    /// <param name="path">対象ファイル。</param>
    /// <param name="sampleProvider">取り出したサンプルプロバイダ。</param>
    /// <returns>呼び出し側が破棄すべきストリーム。</returns>
    private static WaveStream OpenSampleReader(string path, out ISampleProvider sampleProvider)
    {
        if (Path.GetExtension(path).Equals(WavExtension, StringComparison.OrdinalIgnoreCase))
        {
            var wav = new AudioFileReader(path);
            sampleProvider = wav;
            return wav;
        }

        EnsureMediaFoundationStarted();
        var mp3 = new MediaFoundationReader(path);
        sampleProvider = mp3.ToSampleProvider();
        return mp3;
    }

    // ── 書き出し（WAV）──────────────────────────────────────────

    /// <summary>
    /// WAV を指定フレーム範囲で切り出して書き出す。
    /// デコード／再エンコードを挟まず生バイトをコピーするため、
    /// 元のビット深度・サンプルレート・チャンネル数がそのまま保たれる。
    /// </summary>
    /// <param name="sourcePath">入力 WAV。</param>
    /// <param name="destPath">出力先（一時ファイル）。</param>
    /// <param name="startFrame">開始フレーム（含む）。</param>
    /// <param name="endFrameExclusive">終了フレーム（含まない）。</param>
    private static void WriteTrimmedWav(string sourcePath, string destPath, long startFrame, long endFrameExclusive)
    {
        using var reader = new WaveFileReader(sourcePath);

        int  blockAlign = reader.WaveFormat.BlockAlign;
        long startByte  = startFrame * blockAlign;
        long endByte    = Math.Min(reader.Length, endFrameExclusive * blockAlign);

        reader.Position = startByte;

        using var writer = new WaveFileWriter(destPath, reader.WaveFormat);

        // ブロック境界を保ったままコピーする（BlockAlign の倍数で読む）。
        var  buffer    = new byte[Math.Max(blockAlign, ScanBufferSampleCount / blockAlign * blockAlign)];
        long remaining = endByte - startByte;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = reader.Read(buffer, 0, want);
            if (read <= 0) break;
            writer.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    // ── 書き出し（MP3）──────────────────────────────────────────

    /// <summary>
    /// MP3 を指定フレーム範囲で切り出し、Media Foundation の MP3 エンコーダで書き出す。
    /// サンプルレート／チャンネル数は元のまま、ビットレートは元ファイルに近い値を選ぶ。
    /// </summary>
    /// <param name="sourcePath">入力 MP3。</param>
    /// <param name="destPath">出力先（一時ファイル）。</param>
    /// <param name="startFrame">開始フレーム（含む）。</param>
    /// <param name="endFrameExclusive">終了フレーム（含まない）。</param>
    /// <param name="channels">チャンネル数（サンプル数換算に使う）。</param>
    private static void WriteTrimmedMp3(
        string sourcePath, string destPath, long startFrame, long endFrameExclusive, int channels)
    {
        EnsureMediaFoundationStarted();

        int bitrate = DetectMp3Bitrate(sourcePath);

        using var reader = new MediaFoundationReader(sourcePath);

        // 切り出し区間だけを流すプロバイダ。単位はインターリーブ済みサンプル数。
        var offset = new OffsetSampleProvider(reader.ToSampleProvider())
        {
            SkipOverSamples = checked((int)(startFrame * channels)),
            TakeSamples     = checked((int)((endFrameExclusive - startFrame) * channels)),
        };

        // MP3 エンコーダ MFT は 16bit PCM 入力を要求するため、float から変換して渡す。
        var pcm16 = new SampleToWaveProvider16(offset);
        MediaFoundationEncoder.EncodeToMp3(pcm16, destPath, bitrate);
    }

    /// <summary>
    /// MP3 のビットレート（bps）を推定する。
    /// フレームヘッダから読めればその値を、読めなければ既定値を返す。
    /// </summary>
    /// <param name="path">対象 MP3。</param>
    private static int DetectMp3Bitrate(string path)
    {
        try
        {
            using var mp3     = new Mp3FileReader(path);
            int       bitrate = mp3.Mp3WaveFormat.AverageBytesPerSecond * BitsPerByte;
            return bitrate > 0 ? bitrate : AudioTrimOptions.FallbackMp3BitrateBps;
        }
        catch
        {
            return AudioTrimOptions.FallbackMp3BitrateBps;
        }
    }

    /// <summary>
    /// Media Foundation を一度だけ初期化する（MP3 のデコード／エンコードに必要）。
    /// </summary>
    private static void EnsureMediaFoundationStarted()
    {
        lock (MediaFoundationLock)
        {
            if (_mediaFoundationStarted) return;
            MediaFoundationApi.Startup();
            _mediaFoundationStarted = true;
        }
    }

    // ── 出力ファイルの配置 ────────────────────────────────────────

    /// <summary>
    /// 一時ファイルを最終的な場所へ移す処理の結果。
    /// </summary>
    /// <param name="OutputPath">出力された最終パス（失敗時 null）。</param>
    /// <param name="BackupPath">退避した元ファイルのパス（上書き時のみ）。</param>
    /// <param name="Error">失敗した場合の結果オブジェクト（成功時 null）。</param>
    private readonly record struct PlacementResult(string? OutputPath, string? BackupPath, AudioTrimResult? Error);

    /// <summary>
    /// 書き出し済みの一時ファイルを保存方法に従って配置する。
    ///
    /// 上書きの場合は「元 → .bak へ退避」→「一時 → 元の場所へ」の順で行い、
    /// 途中で失敗したら退避を巻き戻して元の状態へ戻す（半端な状態を残さない）。
    /// </summary>
    /// <param name="sourcePath">元ファイルのパス。</param>
    /// <param name="tempPath">書き出し済みの一時ファイル。</param>
    /// <param name="mode">保存方法。</param>
    private static PlacementResult PlaceOutput(string sourcePath, string tempPath, AudioTrimSaveMode mode)
    {
        if (mode == AudioTrimSaveMode.SaveAs)
        {
            string dir       = Path.GetDirectoryName(sourcePath) ?? "";
            string name      = Path.GetFileNameWithoutExtension(sourcePath);
            string extension = Path.GetExtension(sourcePath);
            string destPath  = Path.Combine(dir, name + TrimmedNameSuffix + extension);

            try
            {
                File.Move(tempPath, destPath, overwrite: true);
                return new PlacementResult(destPath, null, null);
            }
            catch (IOException ex)
            {
                return new PlacementResult(null, null, MakeIoError(ex, destPath));
            }
        }

        // ── 上書き ──
        string backupPath    = sourcePath + BackupExtension;
        bool   movedToBackup = false;
        try
        {
            // 既存の .bak は置き換える（File.Move の overwrite が削除も兼ねる）。
            File.Move(sourcePath, backupPath, overwrite: true);
            movedToBackup = true;

            File.Move(tempPath, sourcePath, overwrite: true);
            return new PlacementResult(sourcePath, backupPath, null);
        }
        catch (IOException ex)
        {
            // 退避まで済んでいたら元へ戻す（元ファイルを失わせない）。
            if (movedToBackup)
            {
                try { File.Move(backupPath, sourcePath, overwrite: true); } catch { }
            }
            return new PlacementResult(null, null, MakeIoError(ex, sourcePath));
        }
    }

    /// <summary>
    /// ファイル入出力の失敗を、原因に応じたメッセージ付きの結果へ変換する。
    /// 共有違反（実行中のランタイムがファイルを掴んでいる）は専用の案内を出す。
    /// </summary>
    /// <param name="ex">発生した例外。</param>
    /// <param name="path">対象パス。</param>
    private static AudioTrimResult MakeIoError(IOException ex, string path)
    {
        int win32 = ex.HResult & Win32ErrorCodeMask;
        if (win32 is ErrorSharingViolation or ErrorLockViolation)
        {
            return new AudioTrimResult
            {
                Status  = AudioTrimStatus.FileLocked,
                Message = $"ファイルが他のプロセスに使用中のため置き換えられませんでした。\n{path}\n"
                        + "再生（Play）を停止してから、もう一度お試しください。",
            };
        }

        return new AudioTrimResult
        {
            Status  = AudioTrimStatus.Failed,
            Message = $"ファイルの置き換えに失敗しました: {ex.Message}",
        };
    }

    // ── 小物ヘルパ ────────────────────────────────────────────────

    /// <summary>
    /// 書き出し用の一時ファイルパスを作る。元と同じフォルダ・同じ拡張子にして、
    /// 移動が同一ボリューム内で完結する（＝ほぼアトミックに置き換えられる）ようにする。
    /// </summary>
    /// <param name="sourcePath">元ファイル。</param>
    private static string MakeTempPath(string sourcePath)
    {
        string dir       = Path.GetDirectoryName(sourcePath) ?? "";
        string name      = Path.GetFileNameWithoutExtension(sourcePath);
        string extension = Path.GetExtension(sourcePath);
        return Path.Combine(dir, name + TempNameSuffix + extension);
    }

    /// <summary>ファイルを削除する（失敗しても無視する後始末用）。</summary>
    /// <param name="path">削除対象。</param>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>ミリ秒をフレーム数へ変換する。</summary>
    /// <param name="milliseconds">ミリ秒。</param>
    /// <param name="sampleRate">サンプルレート（Hz）。</param>
    private static long MillisecondsToFrames(double milliseconds, int sampleRate)
        => (long)Math.Round(Math.Max(0.0, milliseconds) * sampleRate / MillisecondsPerSecond);

    /// <summary>フレーム数をミリ秒へ変換する。</summary>
    /// <param name="frames">フレーム数。</param>
    /// <param name="sampleRate">サンプルレート（Hz）。</param>
    private static double FramesToMilliseconds(long frames, int sampleRate)
        => frames * MillisecondsPerSecond / sampleRate;
}
