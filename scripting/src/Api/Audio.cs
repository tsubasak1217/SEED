namespace SEED;

/// <summary>
/// BGM / 効果音の再生 API。ファイルは assets:// 仮想パスで指定する
/// （対応形式: wav / ogg / mp3 / flac）。
///
/// 実際の再生はフレーム末尾に行われる（遅延は数 ms 以内で知覚できない）。
/// オーディオデバイスが無い環境では全操作が無音で無視される。
/// </summary>
public static class Audio
{
    // ── コマンド種別（Rust 側 host_api.rs の AUDIO_CMD_* と一致させる）──
    private const int CmdPlaySe = 0;
    private const int CmdPlayBgm = 1;
    private const int CmdStopBgm = 2;
    private const int CmdSetBgmVolume = 3;

    /// <summary>
    /// 効果音を再生する（多重再生可）。
    /// </summary>
    /// <param name="path">音声ファイルの assets:// 仮想パス</param>
    /// <param name="volume">音量（1.0 = 等倍）</param>
    public static void Play(string path, float volume = 1f)
        => ScriptHost.AudioCommand(CmdPlaySe, path, volume, 0);

    /// <summary>
    /// BGM を再生する。既に BGM が再生中の場合は停止して置き換える。
    /// </summary>
    /// <param name="path">音声ファイルの assets:// 仮想パス</param>
    /// <param name="volume">音量（1.0 = 等倍）</param>
    /// <param name="loop">ループ再生するか（既定 true）</param>
    public static void PlayBgm(string path, float volume = 1f, bool loop = true)
        => ScriptHost.AudioCommand(CmdPlayBgm, path, volume, loop ? 1 : 0);

    /// <summary>BGM を停止する。</summary>
    public static void StopBgm()
        => ScriptHost.AudioCommand(CmdStopBgm, "", 0f, 0);

    /// <summary>再生中の BGM の音量を変更する（1.0 = 等倍）。</summary>
    public static void SetBgmVolume(float volume)
        => ScriptHost.AudioCommand(CmdSetBgmVolume, "", volume, 0);
}
