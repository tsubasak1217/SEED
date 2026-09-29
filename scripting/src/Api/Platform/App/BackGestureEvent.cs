using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// 予測型の戻るの手ぶりのイベント（<see cref="App.BackStartedEvent"/>・<see cref="App.BackProgressedEvent"/>・
/// <see cref="App.BackCancelledEvent"/>・<see cref="App.BackInvokedEvent"/>）の中身（不変値型。W2 の手直し P1-3）。
///
/// <para>
/// 受け方: <c>this.On(App.BackProgressedEvent, (string json) =&gt; { if (BackGestureEvent.TryParse(json, out var e)) … });</c>
/// （4 つのどのイベントも同じ <see cref="TryParse"/> で読め、<see cref="Phase"/> で見分ける）。
/// 届くのはプロジェクト設定 android.predictive_back を true にした APK の Android 13 以上だけ（started・progressed・cancelled は 14 以上）と、
/// デスクトップの模擬の <see cref="PlatformDiagnostics.SimulateBackGesture"/>。
/// 確定（<see cref="BackGesturePhase.Invoked"/>）の後、Android では戻るキーと同じく <c>KeyCode.Escape</c> が届く（知らせとキーは別の道で
/// 届くので同じフレームとは限らない。<see cref="Gesture"/> で同じ手ぶりかを見分ける）。PC の模擬は Escape を注入しない（Esc キーで確定する）。
/// </para>
/// </summary>
public readonly struct BackGestureEvent
{
    /// <summary>段階（イベントの名前から決まる）。</summary>
    public BackGesturePhase Phase { get; }

    /// <summary>手ぶりの通し番号（1 から。同じ手ぶりの started・progressed・cancelled / invoked は同じ番号）。</summary>
    public long Gesture { get; }

    /// <summary>進み具合（0〜1。started・progressed だけ。cancelled・invoked は 0）。</summary>
    public float Progress { get; }

    /// <summary>手ぶりを始めた端（started・progressed だけ。cancelled・invoked は None）。</summary>
    public BackEdge Edge { get; }

    /// <summary>指の x（窓の座標の px。取れないとき〈ボタンの戻る・模擬・cancelled・invoked〉は 0）。</summary>
    public float TouchX { get; }

    /// <summary>指の y（窓の座標の px。取れないときは 0）。</summary>
    public float TouchY { get; }

    /// <summary>デスクトップの模擬が作ったか。</summary>
    public bool Simulated { get; }

    /// <summary>値を作る。</summary>
    internal BackGestureEvent(BackGesturePhase phase, long gesture, float progress, BackEdge edge, float touchX, float touchY, bool simulated)
    {
        Phase = phase;
        Gesture = gesture;
        Progress = progress;
        Edge = edge;
        TouchX = touchX;
        TouchY = touchY;
        Simulated = simulated;
    }

    /// <summary>
    /// 戻るの 4 つのイベントのどれかの JSON（SEED.Events・PlatformEvents.OnEvent の引数）を読む。
    /// </summary>
    /// <param name="json">イベントの JSON 全体。</param>
    /// <param name="value">読めた値（読めなければ既定値）。</param>
    /// <returns>名前が戻るのイベントで、data がオブジェクトなら true。</returns>
    public static bool TryParse(string json, out BackGestureEvent value)
    {
        value = default;
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!BackJson.TryPhaseOfEvent(AlarmJson.GetString(root, PlatformJson.KeyName), out BackGesturePhase phase)) return false;
            if (!root.TryGetProperty(AlarmJson.KeyData, out JsonElement data) || data.ValueKind != JsonValueKind.Object) return false;
            value = new BackGestureEvent(
                phase,
                AlarmJson.GetLong(data, BackJson.KeyGesture),
                BackJson.ClampProgress(SensorJson.GetFloat(data, BackJson.KeyProgress)),
                BackJson.ToEdge(AlarmJson.GetString(data, BackJson.KeyEdge)),
                SensorJson.GetFloat(data, BackJson.KeyTouchX),
                SensorJson.GetFloat(data, BackJson.KeyTouchY),
                AlarmJson.GetBool(data, AlarmJson.KeySimulated));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>ログ向けの 1 行（例 "progressed #3 0.42 left"）。</summary>
    public override string ToString() =>
        $"{BackJson.ToWord(Phase)} #{Gesture} {Progress.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {BackJson.ToWord(Edge)}"
        + (Simulated ? " (模擬)" : string.Empty);
}
