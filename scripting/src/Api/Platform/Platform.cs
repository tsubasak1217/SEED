namespace SEED.Platform;

/// <summary>
/// アプリのプラットフォーム機能（目覚まし・通知・権限など OS の機能。W1）の入口（W1-1 の骨組み）。
///
/// <para><b>仕組み</b><br/>
/// スクリプト → エンジン（Rust）→ Android では JNI → Java の SeedPlatform → 別プロセス :seed_platform、
/// デスクトップではエンジンの中の模擬（DesktopSimBridge）へ届く。どちらも同じ形の JSON で答える
/// （docs/android.md §25・docs/app_platform_roadmap.md §2.2）。
/// 型付きの API はこの上へ作る（W1-3 で <see cref="Alarms"/>、W1-4a で <see cref="App"/>・<see cref="Window"/>、W1-5 で
/// <see cref="Notifications"/>・<see cref="Permissions"/>）。W1-1 のスクリプト向けは
/// <see cref="IsSupported"/>・<see cref="IsSimulated"/>・<see cref="LastError"/>・<see cref="PlatformDiagnostics"/>・
/// <see cref="PlatformEvents"/> だけで、生の命令（TryInvoke）は公開しない。
/// </para>
///
/// <para><b>最初の呼び出し（Android）</b><br/>
/// :seed_platform のプロセスは最初の呼び出しまで起動しない。起動には約 120 ms かかるので、描画のスレッドでは待たず、
/// 最初の呼び出しは失敗（<see cref="LastError"/> == <see cref="ErrorConnecting"/>）して背面で接続が始まる。
/// つながると <see cref="PlatformEvents.Connected"/>（"platform.connected"）のイベントが届くので、そこで呼び直す。
/// </para>
///
/// 名前空間とクラス名が同じなので、<c>using SEED.Platform;</c> して <c>Platform.IsSupported</c> と書くのがおすすめ
/// （修飾するなら <c>SEED.Platform.Platform.IsSupported</c>）。
/// </summary>
public static class Platform
{
    /// <summary><see cref="LastError"/>: :seed_platform へつないでいる途中（Android の最初の呼び出し）。platform.connected を待って呼び直す。</summary>
    public const string ErrorConnecting = "connecting";

    /// <summary><see cref="LastError"/>: 基盤が無い（Android で Java 側の登録が無い・ホスト API 未登録）。</summary>
    public const string ErrorUnavailable = "platform_unavailable";

    /// <summary><see cref="LastError"/>: 返答が約束の JSON でなかった（版の食い違いなど）。</summary>
    public const string ErrorInvalidReply = "invalid_reply";

    /// <summary><see cref="LastError"/>: 失敗の返答に理由が無かった。</summary>
    public const string ErrorUnknown = "unknown_error";

    /// <summary>
    /// プラットフォーム機能が使えるか（Android で Java 側の準備が済んでいる、またはデスクトップの模擬）。
    /// IPC も JNI も通らないので毎フレーム読んでよい。
    /// </summary>
    public static bool IsSupported => ScriptHost.PlatformStatus() != ScriptHost.PlatformStatusUnavailable;

    /// <summary>デスクトップの模擬で動いているか（OS の機能は使わず、エンジンの中で同じ形の返答を作る）。</summary>
    public static bool IsSimulated => ScriptHost.PlatformStatus() == ScriptHost.PlatformStatusSimulated;

    /// <summary>
    /// 直前の呼び出し（<see cref="PlatformDiagnostics"/> や W1-3 以降の API）の失敗の理由（成功なら空文字）。
    /// 主な値: <see cref="ErrorConnecting"/> / <see cref="ErrorUnavailable"/> / "unknown_method" / "invalid_json" /
    /// "provider_unavailable" / "internal_error"。
    /// </summary>
    public static string LastError { get; internal set; } = string.Empty;

    /// <summary>
    /// 命令を 1 つ同期で送り、返答の JSON を受け取る（内部用。型付きの API が包む）。
    /// </summary>
    /// <param name="module">モジュールの名前（小文字英数字と _。例 "platform"）。</param>
    /// <param name="method">メソッドの名前（例 "ping"）。</param>
    /// <param name="json">引数の JSON（空なら {}）。</param>
    /// <param name="reply">返答の JSON（失敗でも受け取れていれば入る）。</param>
    /// <returns>返答の ok が true なら true。false なら <see cref="LastError"/> に理由が入る。</returns>
    internal static bool TryInvoke(string module, string method, string json, out string reply)
    {
        if (!ScriptHost.PlatformInvoke(module, method, json, out reply))
        {
            LastError = ErrorUnavailable;
            return false;
        }
        if (!PlatformJson.TryReadReply(reply, out bool ok, out string error))
        {
            LastError = ErrorInvalidReply;
            return false;
        }
        LastError = ok ? string.Empty : (error.Length > 0 ? error : ErrorUnknown);
        return ok;
    }
}
