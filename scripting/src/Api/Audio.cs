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
    private const int CmdSetBgmSpeed = 4;
    private const int CmdPauseBgm = 5;
    private const int CmdResumeBgm = 6;
    private const int CmdPlaySeDict = 7;
    private const int CmdPlayBgmDict = 8;

    /// <summary>
    /// PlayDict 系の volume 引数が「未指定（＝辞書の既定音量を使う）」を意味する値。
    /// 負の音量は物理的に意味がないので、追加の引数なしで「省略」を表現できる
    /// （Rust 側 host_api.rs の AUDIO_DICT_VOLUME_UNSPECIFIED と対）。
    /// </summary>
    private const float VolumeUnspecified = -1f;

    /// <summary>PlayBgmDict の loop 引数の既定値（BGM はループが既定）。</summary>
    private const bool DefaultBgmLoop = true;

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

    // ── 音声辞書のキーで鳴らす（PlayDict 系）────────────────────
    //
    // パス指定の Play / PlayBgm とは **別名** にしてある。
    // 同じ名前のオーバーロードにすると「パスのつもりでキーを渡した／その逆」を
    // コンパイラが見抜けず、無音という分かりにくい形で失敗するため。

    /// <summary>
    /// 音声辞書のキー（<c>グループ名/用途名</c>）で効果音を再生する（多重再生可）。
    ///
    /// キーはシーン内の全 AudioDictionary を横断して引く（完全一致・先勝ち）。
    /// 解決できないキーはランタイムが警告を出し、何も鳴らさない。
    /// </summary>
    /// <param name="key">辞書のキー（例 <c>"Player/attack"</c>）</param>
    /// <param name="volume">音量（1.0 = 等倍）。負の値なら辞書の既定音量を使う</param>
    public static void PlayDict(string key, float volume = VolumeUnspecified)
        => ScriptHost.AudioCommand(CmdPlaySeDict, key, volume, 0);

    /// <summary>
    /// 音声辞書のキーで BGM を再生する（既存 BGM は停止して置き換え）。
    /// </summary>
    /// <param name="key">辞書のキー（例 <c>"Bgm/stage1"</c>）</param>
    /// <param name="volume">音量（1.0 = 等倍）。負の値なら辞書の既定音量を使う</param>
    /// <param name="loop">ループ再生するか（既定 true）</param>
    public static void PlayBgmDict(string key, float volume = VolumeUnspecified, bool loop = DefaultBgmLoop)
        => ScriptHost.AudioCommand(CmdPlayBgmDict, key, volume, loop ? 1 : 0);

    /// <summary>
    /// 音声辞書のキーで BGM を再生する（音量は辞書の既定値）。
    /// <c>PlayBgmDict("Bgm/jingle", false)</c> のように書けるようにするための短縮形。
    /// </summary>
    /// <param name="key">辞書のキー（例 <c>"Bgm/jingle"</c>）</param>
    /// <param name="loop">ループ再生するか</param>
    public static void PlayBgmDict(string key, bool loop)
        => ScriptHost.AudioCommand(CmdPlayBgmDict, key, VolumeUnspecified, loop ? 1 : 0);

    /// <summary>BGM を停止する。</summary>
    public static void StopBgm()
        => ScriptHost.AudioCommand(CmdStopBgm, "", 0f, 0);

    /// <summary>
    /// BGM を一時停止する（<b>再生位置を保持したまま</b>止める）。
    ///
    /// <see cref="StopBgm"/> と違い、<see cref="ResumeBgm"/> で<b>止めた位置から</b>続けられる。
    /// ゲーム時間を止めている間だけ BGM も凍結し、再開時に拍の位相をそのまま繋ぎたい
    /// （リズムゲームのループなど）場面で使う。
    ///
    /// BGM が鳴っていないとき・既に一時停止しているときは何も起きない（多重呼び出し安全）。
    /// </summary>
    public static void PauseBgm()
        => ScriptHost.AudioCommand(CmdPauseBgm, "", 0f, 0);

    /// <summary>
    /// <see cref="PauseBgm"/> で止めた BGM を、止めた位置から再開する。
    /// BGM が鳴っていないとき・再生中のときは何も起きない（多重呼び出し安全）。
    /// </summary>
    public static void ResumeBgm()
        => ScriptHost.AudioCommand(CmdResumeBgm, "", 0f, 0);

    /// <summary>再生中の BGM の音量を変更する（1.0 = 等倍）。</summary>
    public static void SetBgmVolume(float volume)
        => ScriptHost.AudioCommand(CmdSetBgmVolume, "", volume, 0);

    /// <summary>
    /// BGM の再生速度を変更する（1.0 = 等倍）。
    ///
    /// 早送り／スロー再生なので<b>速度に比例してピッチも変わる</b>
    /// （テンポだけを変える機能ではない）。指定値は 0.25〜4.0 にクランプされる。
    ///
    /// 速度は BGM を差し替えても保持される（<see cref="PlayBgm"/> の前に指定しても後に
    /// 指定しても同じ結果になる）。等倍へ戻したいときは明示的に 1.0 を渡すこと。
    /// </summary>
    /// <param name="speed">再生速度（1.0 = 等倍）</param>
    public static void SetBgmSpeed(float speed)
        => ScriptHost.AudioCommand(CmdSetBgmSpeed, "", speed, 0);
}
