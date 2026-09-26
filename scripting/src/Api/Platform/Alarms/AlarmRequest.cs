namespace SEED.Platform;

/// <summary>
/// 目覚ましの予約 1 件（<see cref="Alarms.Schedule"/> に渡す。W1-3）。
///
/// <para>
/// 時刻はすべて UTC の epoch ミリ秒（壁時計の計算・タイムゾーンはアプリがする。例
/// <c>DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds()</c>）。同じ <see cref="Id"/> の予約は置き換わる。
/// 鳴らし方の欄（音源・音量・安全弁・通知の文言）は W1-3 では控えに持つだけで、鳴動（音・通知）は W1-4 で使われる。
/// </para>
/// <para>
/// 受け付ける値（Android の :seed_platform とデスクトップの模擬で同じ規則。範囲外は直してから控えに入る）:
/// Id は 1〜128 文字、TriggerAtUtcMs は正の数（過去の時刻はすぐ鳴る）、ForceVolume は負なら「触らない」・1 を超えたら 1、
/// FadeInSeconds は負なら 0、MaxRingMinutes は 1 未満なら 1、Title・Body は 4096 文字まで、PayloadJson は 16384 文字まで。
/// </para>
/// </summary>
public sealed class AlarmRequest
{
    /// <summary><see cref="ForceVolume"/> の「音量に触らない」の値。</summary>
    public const float VolumeUnchanged = -1f;

    /// <summary><see cref="FadeInSeconds"/> の既定値（秒）。</summary>
    public const float DefaultFadeInSeconds = 5f;

    /// <summary><see cref="MaxRingMinutes"/> の既定値（分）。</summary>
    public const int DefaultMaxRingMinutes = 60;

    /// <summary>予約の ID（アプリが決める。同じ ID は置き換え）。1〜128 文字。</summary>
    public string Id = string.Empty;

    /// <summary>鳴らす時刻（UTC の epoch ミリ秒）。</summary>
    public long TriggerAtUtcMs;

    /// <summary>
    /// 音源（"assets://…" か端末のファイルの絶対パス。空なら既定の音）。Android では "assets://…" の音をエンジンが
    /// :seed_platform の読める場所へ書き出してから予約する（読めなければ既定の音）。
    /// </summary>
    public string SoundAsset = string.Empty;

    /// <summary>バイブするか。</summary>
    public bool Vibrate = true;

    /// <summary>鳴っている間のアラームの音量（0..1。負なら触らない）。止めたら元へ戻す（W1-4）。</summary>
    public float ForceVolume = VolumeUnchanged;

    /// <summary>true なら、鳴動中に利用者が音量を下げても <see cref="ForceVolume"/> へ戻す（W1-4）。</summary>
    public bool KeepVolume;

    /// <summary>音量の漸増の秒（0 なら最初から最大。W1-4）。</summary>
    public float FadeInSeconds = DefaultFadeInSeconds;

    /// <summary>安全弁。これを過ぎたら自動で止める（分。W1-4）。</summary>
    public int MaxRingMinutes = DefaultMaxRingMinutes;

    /// <summary>鳴動中の通知・フルスクリーン通知の題（W1-4）。</summary>
    public string Title = string.Empty;

    /// <summary>同・本文（W1-4）。</summary>
    public string Body = string.Empty;

    /// <summary>イベント（<see cref="AlarmFiredEvent"/> 等）と起動理由にそのまま返す任意の JSON（アプリのアラーム ID 等）。</summary>
    public string PayloadJson = string.Empty;
}
