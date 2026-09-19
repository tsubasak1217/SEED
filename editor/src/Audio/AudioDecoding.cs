// ============================================================
//  AudioDecoding.cs — 音声ファイルを開く手順を 1 か所へ集約
//
//  【役割】
//  「この拡張子ならどの復号器を使うか」という判断を、音声を読むすべての機能で共有する。
//  今のところ試聴（NAudioPreviewPlayer）と波形サムネイル（WaveformThumbnailRenderer）が使う。
//
//  【なぜ共有するのか】
//  復号器の選び方は環境依存の知識（どの形式を NAudio が直接読めて、
//  どこから Windows の復号器に頼るのか）の塊で、間違えると「鳴るのに波形が出ない」
//  のような噛み合わない状態になる。1 か所で決めれば必ず揃う。
//
//  【無音カット（AudioSilenceTrimmer）と統一しない理由】
//  あちらは「拡張子が .wav なら WaveFileReader で生バイトを扱う」という、
//  再エンコードを避けるための別要件で開いている（読むだけではなく書き出しもする）。
//  目的が違うものを同じ関数にまとめると、片方の都合が他方を壊す。
// ============================================================

using NAudio.Wave;

namespace SEEDEditor.Audio;

/// <summary>
/// 音声ファイルを float サンプルとして読むための復号器を用意する。状態を持たない静的クラス。
/// </summary>
public static class AudioDecoding
{
    /// <summary>
    /// ファイルを開いて復号器とサンプル供給を用意する。
    ///
    /// <list type="number">
    ///   <item><description>
    ///     <c>AudioFileReader</c> … WAV / MP3 / AIFF を NAudio 自身が読む。軽く、失敗が少ない。
    ///   </description></item>
    ///   <item><description>
    ///     <c>MediaFoundationReader</c> … 1 で開けなかった形式を Windows の復号器へ回す
    ///     （FLAC は Windows 10 以降が読む）。使う前に Media Foundation の初期化が要る。
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// どちらでも開けなければ例外を投げる。呼び出し側が捕まえて
    /// 「試聴できない」「波形を描けない」として扱うこと（例外で落とさない）。
    /// </para>
    /// </summary>
    /// <param name="path">音声ファイルの絶対パス。</param>
    /// <param name="sampleProvider">float サンプルの供給（チャンネルはインターリーブ）。</param>
    /// <returns>寿命を管理するための復号器本体（呼び出し側が Dispose する）。</returns>
    public static WaveStream OpenReader(string path, out ISampleProvider sampleProvider)
    {
        try
        {
            var reader = new AudioFileReader(path);
            sampleProvider = reader;
            return reader;
        }
        catch
        {
            MediaFoundationRuntime.EnsureStarted();
            var reader = new MediaFoundationReader(path);
            sampleProvider = reader.ToSampleProvider();
            return reader;
        }
    }

    /// <summary>
    /// 復号器が申告する総フレーム数（チャンネルを混ぜた後の長さ）を求める。
    /// </summary>
    /// <remarks>
    /// 形式によっては推定値で、実際の再生長とずれることがある（可変ビットレートの MP3 など）。
    /// 波形の列割りの初期値に使うだけなので、ずれても
    /// <c>WaveformPeakAccumulator</c> が自動で吸収する。
    /// </remarks>
    /// <param name="reader">開いた復号器。</param>
    /// <returns>総フレーム数。求められないときは 0（＝不明）。</returns>
    public static long EstimateFrameCount(WaveStream? reader)
    {
        if (reader is null) return 0;
        try
        {
            int blockAlign = reader.WaveFormat?.BlockAlign ?? 0;
            if (blockAlign <= 0) return 0;
            return reader.Length / blockAlign;
        }
        catch
        {
            // 長さを持たないストリームもある。不明として扱う。
            return 0;
        }
    }
}
