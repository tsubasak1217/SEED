using System.Globalization;
using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// プラットフォーム機能の確かめ（実機・デスクトップの模擬で、往復とイベントの経路が通るかを見る。W1-1）。
///
/// <see cref="Ping"/> は往復の時間と、答えたプロセスの pid を返す。<see cref="EmitTestEvent"/> は試験イベント
/// "platform.test_event" を 1 つ流し、それが次のフレーム以降に <see cref="PlatformEvents"/> と <c>SEED.Events</c> へ届く。
/// </summary>
public static class PlatformDiagnostics
{
    /// <summary>基盤そのもののモジュールの名前。</summary>
    private const string ModulePlatform = "platform";

    /// <summary>往復の計測のメソッド。</summary>
    private const string MethodPing = "ping";

    /// <summary>試験イベントのメソッド。</summary>
    private const string MethodEmitTestEvent = "emit_test_event";

    /// <summary>ping の引数・返答の echo の中の合言葉のキー。</summary>
    private const string KeyNonce = "nonce";

    /// <summary>ping の返答: 受け取った JSON。</summary>
    private const string KeyEcho = "echo";

    /// <summary>ping の返答: pid。</summary>
    private const string KeyPid = "pid";

    /// <summary>ping の返答: 起動からの ms。</summary>
    private const string KeyUptimeMs = "uptime_ms";

    /// <summary>ping の返答: 模擬か。</summary>
    private const string KeySimulated = "simulated";

    /// <summary>試験イベントの引数: 文言。</summary>
    private const string KeyMessage = "message";

    /// <summary>合言葉の接頭辞。</summary>
    private const string NoncePrefix = "ping-";

    /// <summary>試験イベントの既定の文言。</summary>
    private const string DefaultTestMessage = "test";

    /// <summary><see cref="PlatformPingResult.Error"/>: 送った合言葉がそのまま返ってこなかった。</summary>
    public const string ErrorEchoMismatch = "echo_mismatch";

    /// <summary>合言葉の通し番号（呼ぶたびに 1 つ進める。スクリプトのスレッドだけが触る）。</summary>
    private static long _nonceCounter;

    /// <summary>
    /// 往復を 1 回計測する（合言葉を送り、echo で同じものが返るかも確かめる）。
    ///
    /// Android の最初の 1 回は :seed_platform の起動を描画のスレッドで待たないため失敗し、
    /// <see cref="PlatformPingResult.Error"/> == <see cref="Platform.ErrorConnecting"/> になる
    /// （"platform.connected" のイベントの後に呼び直す）。
    /// </summary>
    /// <returns>結果（失敗も <see cref="PlatformPingResult.Ok"/> == false の値で返る。例外は投げない）。</returns>
    public static PlatformPingResult Ping()
    {
        _nonceCounter++;
        string nonce = NoncePrefix + _nonceCounter.ToString(CultureInfo.InvariantCulture);
        string request = PlatformJson.StringObject((KeyNonce, nonce));

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool ok = Platform.TryInvoke(ModulePlatform, MethodPing, request, out string reply);
        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!ok)
        {
            return new PlatformPingResult(false, elapsedMs, 0, 0, false, Platform.LastError);
        }
        return ReadPingReply(reply, nonce, elapsedMs);
    }

    /// <summary>
    /// 試験イベント "platform.test_event" を 1 つ流す（data.message に文言が入る）。
    /// イベントはすぐには届かず、次のフレーム以降に <see cref="PlatformEvents"/> から配られる。
    /// </summary>
    /// <param name="message">イベントに入れる文言。</param>
    /// <returns>受け付けられたら true（失敗なら <see cref="Platform.LastError"/>）。</returns>
    public static bool EmitTestEvent(string message = DefaultTestMessage)
    {
        string request = PlatformJson.StringObject((KeyMessage, message ?? DefaultTestMessage));
        return Platform.TryInvoke(ModulePlatform, MethodEmitTestEvent, request, out _);
    }

    /// <summary>
    /// デスクトップの模擬だけ: 端末の明暗（<see cref="App.UiMode"/>）を差し替え、変わったら <see cref="App.UiModeChangedEvent"/> を流す（W2-9）。
    /// OS の設定を変えずに、端末の明暗に従うテーマ（SEED.UI の UiBrightnessMode.System）の切り替えを PC で試すため。
    /// Android の実機では unknown_method（false）。
    /// </summary>
    /// <param name="mode">差し替える明暗（null で差し替えをやめ OS の設定へ戻す）。</param>
    /// <returns>受け付けたら true（失敗なら <see cref="Platform.LastError"/>）。</returns>
    public static bool SimulateUiMode(SystemUiMode? mode)
    {
        string night = mode is { } m ? AppJson.ToNight(m) : AppJson.NightSystem;
        return Platform.TryInvoke(AppJson.Module, AppJson.MethodSimSetUiMode, PlatformJson.StringObject((AppJson.KeyNight, night)), out _);
    }

    /// <summary>
    /// デスクトップの模擬だけ: 予測型の戻るの手ぶりのイベント（<see cref="App.BackStartedEvent"/> ほか）を 1 つ流す（W2 の手直し P1-3）。
    /// 端末が無くても SEED.UI の予測型の戻るのプレビュー（縮む見た目）を PC で試すため。手ぶりの番号は Android と同じ決まり
    /// （Started で 1 増え、Started の無い Invoked も 1 増える）。<see cref="BackGesturePhase.Invoked"/> は確定の知らせだけで、
    /// Escape は注入しない（PC の確定は Esc キー）。イベントは次のフレーム以降に届く。Android の実機では unknown_method（false）。
    /// </summary>
    /// <param name="phase">段階。</param>
    /// <param name="progress">進み具合（0〜1。範囲外はそろえる。Started・Progressed だけが使う）。</param>
    /// <param name="edge">手ぶりを始めた端（Started・Progressed だけが使う）。</param>
    /// <returns>受け付けたら true（失敗なら <see cref="Platform.LastError"/>）。</returns>
    public static bool SimulateBackGesture(BackGesturePhase phase, float progress = 0f, BackEdge edge = BackEdge.Left) =>
        Platform.TryInvoke(AppJson.Module, BackJson.MethodSimBackGesture, BackJson.SimGestureRequest(phase, progress, edge), out _);

    /// <summary>ping の返答を読み、合言葉の一致を確かめて結果にする。</summary>
    private static PlatformPingResult ReadPingReply(string reply, string nonce, double elapsedMs)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(reply);
            JsonElement root = document.RootElement;
            bool echoMatches = root.TryGetProperty(KeyEcho, out JsonElement echo)
                && echo.ValueKind == JsonValueKind.Object
                && echo.TryGetProperty(KeyNonce, out JsonElement echoedNonce)
                && echoedNonce.ValueKind == JsonValueKind.String
                && echoedNonce.GetString() == nonce;
            // TryGetInt32 / TryGetInt64 は数値でない要素に例外を投げるので、先に種類を確かめる
            int pid = root.TryGetProperty(KeyPid, out JsonElement pidElement)
                && pidElement.ValueKind == JsonValueKind.Number
                && pidElement.TryGetInt32(out int pidValue) ? pidValue : 0;
            long uptime = root.TryGetProperty(KeyUptimeMs, out JsonElement uptimeElement)
                && uptimeElement.ValueKind == JsonValueKind.Number
                && uptimeElement.TryGetInt64(out long uptimeValue) ? uptimeValue : 0;
            bool simulated = root.TryGetProperty(KeySimulated, out JsonElement simulatedElement) && simulatedElement.ValueKind == JsonValueKind.True;
            return echoMatches
                ? new PlatformPingResult(true, elapsedMs, pid, uptime, simulated, string.Empty)
                : new PlatformPingResult(false, elapsedMs, pid, uptime, simulated, ErrorEchoMismatch);
        }
        catch (JsonException)
        {
            return new PlatformPingResult(false, elapsedMs, 0, 0, false, Platform.ErrorInvalidReply);
        }
    }
}
