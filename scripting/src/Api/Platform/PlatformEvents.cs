using System;

namespace SEED.Platform;

/// <summary>
/// プラットフォーム機能のイベントの配り口（W1-1）。
///
/// <para><b>届き方</b><br/>
/// OS 側（Android の :seed_platform・デスクトップの模擬）で起きたことは、エンジンがフレームの頭（スクリプトより前）で取り出し、
/// このクラスがフレームに 1 回（BeginFrame の前）配る。配り先は 2 つ:
///   ① <c>SEED.Events</c>（名前付きイベントバス）へ、イベントの名前（"platform.…"）で string 引数（イベントの JSON 全体）として
///      → スクリプトからは <c>this.On("platform.test_event", (string json) => …)</c> で受ける（寿命に自動追従。おすすめ）
///   ② <see cref="OnEvent"/>（すべてのプラットフォームのイベントを名前と JSON で受ける C# のイベント。ログ・診断向け）
/// </para>
///
/// <para><b>イベントの JSON</b><br/>
/// {"name": "platform.…", "seq": 通し番号, "time_ms": UTC の epoch ミリ秒, "data": {...}}。
/// seq は :seed_platform の記録の番号（接続の知らせなどメインプロセスの中で作ったものは 0、デスクトップの模擬は模擬の中の番号）。
/// </para>
/// </summary>
public static class PlatformEvents
{
    /// <summary>プラットフォームのイベントの名前の接頭辞（SEED.Events でもこの名前で届く）。</summary>
    public const string Prefix = "platform.";

    /// <summary>:seed_platform へつながった（Android。最初の呼び出しが始めた接続が済んだ。以降の呼び出しは同期で通る）。data.connect_ms・data.pid。</summary>
    public const string Connected = "platform.connected";

    /// <summary>:seed_platform へつなげなかった（Android）。data.error に理由。</summary>
    public const string ConnectFailed = "platform.connect_failed";

    /// <summary>:seed_platform のプロセスが居なくなった（Android。次の呼び出しでつなぎ直す）。</summary>
    public const string Disconnected = "platform.disconnected";

    /// <summary>試験イベント（<see cref="PlatformDiagnostics.EmitTestEvent"/>・adb のデバッグの受信機）。data.message・data.pid。</summary>
    public const string TestEvent = "platform.test_event";

    /// <summary>1 フレームに配る最大の件数（残りは次のフレームへ。ハンドラがイベントを呼び続けても止まるように）。</summary>
    private const int MaxEventsPerFrame = 64;

    /// <summary>
    /// すべてのプラットフォームのイベントを受ける（名前, イベントの JSON）。スクリプトの寿命に追従しないので、
    /// 足したら <c>OnDestroy</c> で必ず外すこと（ホットリロードではエンジンが全部外す）。
    /// 名前ごとに受けるなら <c>this.On("platform.…", (string json) => …)</c> のほうが外し忘れが無い。
    /// </summary>
    public static event Action<string, string>? OnEvent;

    /// <summary>
    /// 届いているイベントを配る（エンジンが BeginFrame からフレームに 1 回呼ぶ。スクリプトからは呼ばない）。
    /// </summary>
    internal static void Poll()
    {
        for (int i = 0; i < MaxEventsPerFrame; i++)
        {
            if (!ScriptHost.TryTakePlatformEvent(out string json)) return;
            Dispatch(json);
        }
    }

    /// <summary>
    /// ハンドラを全部外す（ホットリロードで旧アセンブリのデリゲートを掴んだままにしないため。ScriptBridge が呼ぶ）。
    /// </summary>
    internal static void ResetHandlers() => OnEvent = null;

    /// <summary>イベントを 1 件、<see cref="OnEvent"/> と SEED.Events へ配る（ハンドラの例外はログに残して続ける）。</summary>
    private static void Dispatch(string json)
    {
        if (!PlatformJson.TryReadEventName(json, out string name))
        {
            Debug.LogWarning($"[Platform] 名前の読めないイベントを捨てました: {json}");
            return;
        }
        // 名前は必ず platform. で始める（SEED.Events の名前の空間を分ける。基盤側でも確かめているが念のため）
        if (!name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            name = Prefix + name;
        }

        Action<string, string>? handlers = OnEvent;
        if (handlers != null)
        {
            // 1 つのハンドラの例外で残りを止めない
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<string, string>)handler)(name, json);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Platform] {name} の OnEvent ハンドラで例外: {e}");
                }
            }
        }
        // SEED.Events はハンドラの例外を自分で受け止めてログに残す
        Events.Raise(name, json);
    }
}
