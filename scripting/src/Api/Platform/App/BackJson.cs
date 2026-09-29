using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// 予測型の戻る（<see cref="App.SetBackCallbackEnabled"/>・イベント platform.back_*・<see cref="PlatformDiagnostics.SimulateBackGesture"/>）の
/// JSON の名前と変換（内部用。W2 の手直し P1-3）。
///
/// 名前の正典は Rust 側 runtime/src/engine/platform/bridge/wire.rs の <c>wire::app</c>（Java 側は PlatformContract.java の
/// <c>*BACK*</c>・<c>KEY_APP_ON</c>・<c>KEY_APP_ENABLED</c>・<c>METHOD_APP_SET_BACK_CALLBACK</c>）。値を変えるときは 3 か所を必ず揃える。
/// イベントの名前は公開の <see cref="App.BackStartedEvent"/> ほか。
/// </summary>
internal static class BackJson
{
    /// <summary>アプリの戻るのコールバックを出し入れする。引数 { on }。返答 { on, enabled }。</summary>
    internal const string MethodSetBackCallback = "set_back_callback";

    /// <summary>set_back_callback の引数・返答: アプリが戻るを受けるか。</summary>
    internal const string KeyOn = "on";

    /// <summary>set_back_callback の返答: 予測型の戻るが有効か。</summary>
    internal const string KeyEnabled = "enabled";

    /// <summary>模擬だけ: 戻るの手ぶりのイベントを 1 つ積む。引数 { phase, progress, edge }。</summary>
    internal const string MethodSimBackGesture = "sim_back_gesture";

    /// <summary>sim_back_gesture の引数: 段階。</summary>
    internal const string KeyPhase = "phase";

    /// <summary>phase: 始まった。</summary>
    internal const string PhaseStarted = "started";

    /// <summary>phase: 進んだ。</summary>
    internal const string PhaseProgressed = "progressed";

    /// <summary>phase: 取り消された。</summary>
    internal const string PhaseCancelled = "cancelled";

    /// <summary>phase: 確定した。</summary>
    internal const string PhaseInvoked = "invoked";

    /// <summary>イベントの data: 手ぶりの通し番号。</summary>
    internal const string KeyGesture = "gesture";

    /// <summary>イベントの data・sim_back_gesture の引数: 進み具合（0〜1）。</summary>
    internal const string KeyProgress = "progress";

    /// <summary>イベントの data・sim_back_gesture の引数: 手ぶりを始めた端。</summary>
    internal const string KeyEdge = "edge";

    /// <summary>イベントの data: 指の x（窓の座標の px）。</summary>
    internal const string KeyTouchX = "touch_x";

    /// <summary>イベントの data: 指の y（窓の座標の px）。</summary>
    internal const string KeyTouchY = "touch_y";

    /// <summary>edge: 左の端から。</summary>
    internal const string EdgeLeft = "left";

    /// <summary>edge: 右の端から。</summary>
    internal const string EdgeRight = "right";

    /// <summary>edge: 端からの手ぶりでない。</summary>
    internal const string EdgeNone = "none";

    /// <summary>進み具合の下限（Android の BackEvent.getProgress の範囲）。</summary>
    internal const float MinProgress = 0f;

    /// <summary>進み具合の上限（Android の BackEvent.getProgress の範囲）。</summary>
    internal const float MaxProgress = 1f;

    /// <summary>edge の語 → 端（知らない語・無いは None）。</summary>
    internal static BackEdge ToEdge(string? word) => word switch
    {
        EdgeLeft => BackEdge.Left,
        EdgeRight => BackEdge.Right,
        _ => BackEdge.None,
    };

    /// <summary>端 → edge の語。</summary>
    internal static string ToWord(BackEdge edge) => edge switch
    {
        BackEdge.Left => EdgeLeft,
        BackEdge.Right => EdgeRight,
        _ => EdgeNone,
    };

    /// <summary>段階 → phase の語。</summary>
    internal static string ToWord(BackGesturePhase phase) => phase switch
    {
        BackGesturePhase.Started => PhaseStarted,
        BackGesturePhase.Progressed => PhaseProgressed,
        BackGesturePhase.Cancelled => PhaseCancelled,
        _ => PhaseInvoked,
    };

    /// <summary>
    /// イベントの名前 → 段階（戻るの 4 つのイベントでなければ false）。
    /// </summary>
    /// <param name="eventName">イベントの名前（"platform.back_…"）。</param>
    /// <param name="phase">段階。</param>
    /// <returns>戻るのイベントなら true。</returns>
    internal static bool TryPhaseOfEvent(string eventName, out BackGesturePhase phase)
    {
        switch (eventName)
        {
            case App.BackStartedEvent: phase = BackGesturePhase.Started; return true;
            case App.BackProgressedEvent: phase = BackGesturePhase.Progressed; return true;
            case App.BackCancelledEvent: phase = BackGesturePhase.Cancelled; return true;
            case App.BackInvokedEvent: phase = BackGesturePhase.Invoked; return true;
            default: phase = default; return false;
        }
    }

    /// <summary>進み具合を 0〜1 へそろえる（有限でなければ 0）。</summary>
    internal static float ClampProgress(float progress) =>
        float.IsFinite(progress) ? System.Math.Clamp(progress, MinProgress, MaxProgress) : MinProgress;

    /// <summary>
    /// sim_back_gesture の引数（{"phase":…, "progress":…, "edge":…}）を作る。
    /// </summary>
    /// <param name="phase">段階。</param>
    /// <param name="progress">進み具合（0〜1 へそろえる）。</param>
    /// <param name="edge">端。</param>
    /// <returns>JSON の文字列。</returns>
    internal static string SimGestureRequest(BackGesturePhase phase, float progress, BackEdge edge)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(KeyPhase, ToWord(phase));
            // NaN・無限大は Utf8JsonWriter が例外にするので、先にそろえる
            writer.WriteNumber(KeyProgress, ClampProgress(progress));
            writer.WriteString(KeyEdge, ToWord(edge));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
