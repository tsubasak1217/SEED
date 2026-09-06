using System;

namespace SEEDEditor.Audio;

/// <summary>
/// 無音カット結果の保存方法。
/// </summary>
public enum AudioTrimSaveMode
{
    /// <summary>元ファイルを上書きする（元は &lt;file&gt;.bak へ退避する）。</summary>
    Overwrite,

    /// <summary>元ファイルは残し、&lt;name&gt;_trim.&lt;ext&gt; として別名保存する。</summary>
    SaveAs,
}

/// <summary>
/// 音声ファイルの無音カット処理（<see cref="AudioSilenceTrimmer"/>）に渡すパラメータ一式。
///
/// UI（ダイアログ）と処理本体を疎結合にするための入力専用データ構造。
/// 既定値はここに一箇所だけ定義し、UI 側はこの既定値を初期表示に使う
/// （マジックナンバーを UI とロジックの両方に持たないため）。
/// </summary>
public sealed class AudioTrimOptions
{
    /// <summary>しきい値の既定値（dBFS）。これを超える最初のサンプルまでを無音とみなす。</summary>
    public const double DefaultThresholdDb = -45.0;

    /// <summary>余白の既定値（ミリ秒）。カット位置を音の手前へ少し戻す量。</summary>
    public const double DefaultPaddingMs = 5.0;

    /// <summary>末尾カットの既定値（既定では末尾は触らない）。</summary>
    public const bool DefaultTrimTrailing = false;

    /// <summary>保存方法の既定値。</summary>
    public const AudioTrimSaveMode DefaultSaveMode = AudioTrimSaveMode.Overwrite;

    /// <summary>MP3 出力時、元ファイルのビットレートが判別できなかった場合に使う既定ビットレート（bps）。</summary>
    public const int FallbackMp3BitrateBps = 192_000;

    /// <summary>無音判定のしきい値（dBFS、負の値）。振幅がこれを超えた地点を「音の始まり」とする。</summary>
    public double ThresholdDb { get; set; } = DefaultThresholdDb;

    /// <summary>カット位置に残す余白（ミリ秒）。先頭側は音の手前へ、末尾側は音の後ろへこの分だけ残す。</summary>
    public double PaddingMs { get; set; } = DefaultPaddingMs;

    /// <summary>true なら末尾の無音も同じしきい値でカットする。</summary>
    public bool TrimTrailing { get; set; } = DefaultTrimTrailing;

    /// <summary>保存方法（上書き／別名保存）。</summary>
    public AudioTrimSaveMode SaveMode { get; set; } = DefaultSaveMode;

    /// <summary>
    /// しきい値（dB）を線形振幅（0.0〜1.0）へ変換する。
    /// float サンプルは -1.0〜+1.0 に正規化されているため、比較はこの線形値で行う。
    /// </summary>
    /// <returns>|sample| と比較するための線形しきい値。</returns>
    public float ToLinearThreshold() => (float)Math.Pow(10.0, ThresholdDb / 20.0);
}
