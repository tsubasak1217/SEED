using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// 前面・背面の知らせ（<see cref="App.ResumedEvent"/>「platform.resumed」・<see cref="App.PausedEvent"/>「platform.paused」）の中身
/// （不変値型。2026-10-01）。
///
/// <para>
/// 受け方: <c>this.On(App.ResumedEvent, (string json) =&gt; { if (AppLifecycleEvent.TryParse(json, out var e)) … });</c>
/// （2 つのどちらも同じ <see cref="TryParse"/> で読め、<see cref="Phase"/> で見分ける）。
/// Android は MainActivity の onResume / onPause。<b>プロセスの起動の最初の onResume では届かない</b>ので、resumed は必ず paused の後に来る
/// （「戻った」だけを知らせる。起動時の状態は <c>OnStart</c> で読む）。resumed は、戻ったときの権限の結果・変化
/// （<see cref="PermissionResultEvent"/>・<see cref="PermissionChangedEvent"/>）より後に届く。
/// デスクトップの模擬は窓のフォーカスの出入り（Play の間だけ）と <see cref="PlatformDiagnostics.SimulateLifecycle"/>・IPC の
/// <c>PLATFORM_SIM:lifecycle,…</c>（<see cref="Simulated"/> が true）。
/// </para>
/// </summary>
public readonly struct AppLifecycleEvent
{
    /// <summary>段階（イベントの名前から決まる）。</summary>
    public AppLifecyclePhase Phase { get; }

    /// <summary>何回目の知らせか（前面・背面それぞれ 1 から。Android はプロセスの中、模擬は Play の回の中で数える）。</summary>
    public long Count { get; }

    /// <summary>背面にいた時間（ミリ秒。直前の paused から。resumed だけ。paused は 0）。</summary>
    public long BackgroundMs { get; }

    /// <summary>デスクトップの模擬が作ったか。</summary>
    public bool Simulated { get; }

    /// <summary>値を作る。</summary>
    internal AppLifecycleEvent(AppLifecyclePhase phase, long count, long backgroundMs, bool simulated)
    {
        Phase = phase;
        Count = count;
        BackgroundMs = backgroundMs;
        Simulated = simulated;
    }

    /// <summary>
    /// 前面・背面のイベントの JSON（SEED.Events・PlatformEvents.OnEvent の引数）を読む。
    /// </summary>
    /// <param name="json">イベントの JSON 全体。</param>
    /// <param name="value">読めた値（読めなければ既定値）。</param>
    /// <returns>名前が <see cref="App.ResumedEvent"/> か <see cref="App.PausedEvent"/> で、data がオブジェクトなら true。</returns>
    public static bool TryParse(string json, out AppLifecycleEvent value)
    {
        value = default;
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!AppJson.TryPhaseOfLifecycleEvent(AlarmJson.GetString(root, PlatformJson.KeyName), out AppLifecyclePhase phase)) return false;
            if (!root.TryGetProperty(AlarmJson.KeyData, out JsonElement data) || data.ValueKind != JsonValueKind.Object) return false;
            value = new AppLifecycleEvent(
                phase,
                AlarmJson.GetLong(data, AppJson.KeyLifecycleCount),
                AlarmJson.GetLong(data, AppJson.KeyBackgroundMs),
                AlarmJson.GetBool(data, AlarmJson.KeySimulated));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>ログ向けの 1 行（例 "resumed #2 背面 5321 ms"）。</summary>
    public override string ToString() =>
        (Phase == AppLifecyclePhase.Resumed ? $"resumed #{Count} 背面 {BackgroundMs} ms" : $"paused #{Count}")
        + (Simulated ? " (模擬)" : string.Empty);
}
