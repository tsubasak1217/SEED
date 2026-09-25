// ============================================================
//  RuntimeIpcCommands.cs — ランタイムとの IPC の命令と応答の文字列（PC と Android で共有。段階D-1）
//
//  書式の正典はランタイムの runtime/src/engine/core/app_base/ipc.rs（read_loop・IpcCommand）と、
//  応答を書く側（app/app_init.rs の READY・app/screenshot_ops.rs の SCREENSHOT_DONE / _ERROR・
//  ipc_transport/tcp.rs の挨拶）。ここの値を変えるときはランタイムも必ず直す（文字列は PC と Android で同じ）。
//
//  使う側:
//    RuntimeManager（PC の Play の一時停止・再開）
//    Android/Ipc（Android の実行の一時停止・再開・切り離し・スクリーンショット。エディタの実行バーと SeedAndroid）
//
//  WPF に依存しない（SeedAndroid・単体テストからリンクされる）。
// ============================================================

namespace SEEDEditor.Ipc;

/// <summary>ランタイムとの IPC の命令と応答の文字列。</summary>
public static class RuntimeIpcCommands
{
    // ── エディタ → ランタイム ─────────────────────────────

    /// <summary>一時停止（ゲームの時間・物理・スクリプトを止める）。</summary>
    public const string Pause = "PAUSE";

    /// <summary>再開。</summary>
    public const string Resume = "RESUME";

    /// <summary>
    /// 意図した切り離し（TCP の通信路。この後に切っても、ランタイムは一時停止を据え置く。黙って切ると再開する）。
    /// SeedAndroid の pause / resume が「つないで 1 命令送って切る」ときに送る（ランタイムの ipc_transport/session_policy.rs）。
    /// </summary>
    public const string Detach = "DETACH";

    /// <summary>
    /// 接続トークンの照合の行の接頭辞（TCP の通信路で、つないだら最初に HELLO:{トークン} を送る。段階D-1。
    /// ランタイムの ipc_transport/auth.rs の HELLO_PREFIX と一致させる。名前付きパイプでは送らない）。
    /// </summary>
    public const string HelloPrefix = "HELLO:";

    /// <summary>スクリーンショットの命令の接頭辞（SCREENSHOT:{対象},{ランタイム側の絶対パス}）。</summary>
    public const string ScreenshotPrefix = "SCREENSHOT:";

    /// <summary>スクリーンショットの対象: ゲームの画面（提示したフレーム）。</summary>
    public const string ScreenshotGameTarget = "game";

    /// <summary>命令の引数の区切り。</summary>
    public const char ArgumentSeparator = ',';

    // ── ランタイム → エディタ ─────────────────────────────

    /// <summary>
    /// 準備ができた（READY:{ウィンドウハンドル}）。TCP の通信路ではつながった直後の挨拶（READY:0）にも使う
    /// （adb forward は端末で誰も待っていなくても PC 側の接続を受け付けるため、この行が届いたら「ランタイムとつながった」）。
    /// </summary>
    public const string ReadyPrefix = "READY:";

    /// <summary>
    /// 接続を断った（IPC_DENIED:{理由}。理由は token＝トークンが違う／hello＝最初の行が HELLO でない・来ない。
    /// ランタイムの ipc_transport/auth.rs の DENIED_PREFIX と一致させる。この行の後に接続は閉じられる）。
    /// </summary>
    public const string DeniedPrefix = "IPC_DENIED:";

    /// <summary>スクリーンショットの成功（SCREENSHOT_DONE:{パス},{幅},{高さ}）。</summary>
    public const string ScreenshotDonePrefix = "SCREENSHOT_DONE:";

    /// <summary>スクリーンショットの失敗（SCREENSHOT_ERROR:{理由}）。</summary>
    public const string ScreenshotErrorPrefix = "SCREENSHOT_ERROR:";

    /// <summary>スクリーンショットの命令を作る。</summary>
    /// <param name="target">対象（<see cref="ScreenshotGameTarget"/> 等）。</param>
    /// <param name="runtimePath">ランタイムが PNG を書く絶対パス（カンマを含めない）。</param>
    /// <returns>1 行の命令。</returns>
    public static string Screenshot(string target, string runtimePath) =>
        $"{ScreenshotPrefix}{target}{ArgumentSeparator}{runtimePath}";

    /// <summary>接続トークンの照合の行を作る（HELLO:{トークン}）。</summary>
    /// <param name="token">起動オプションで渡したトークン。</param>
    /// <returns>1 行。</returns>
    public static string Hello(string token) => HelloPrefix + token;

    /// <summary>接続を断った行か。</summary>
    /// <param name="line">受け取った行。</param>
    /// <returns>IPC_DENIED: で始まれば true。</returns>
    public static bool IsDenied(string line) => line.StartsWith(DeniedPrefix, System.StringComparison.Ordinal);

    /// <summary>準備ができた行（挨拶を含む）か。</summary>
    /// <param name="line">受け取った行。</param>
    /// <returns>READY: で始まれば true。</returns>
    public static bool IsReady(string line) => line.StartsWith(ReadyPrefix, System.StringComparison.Ordinal);

    /// <summary>スクリーンショットの応答（成功・失敗）か。</summary>
    /// <param name="line">受け取った行。</param>
    /// <returns>応答なら true。</returns>
    public static bool IsScreenshotReply(string line) =>
        line.StartsWith(ScreenshotDonePrefix, System.StringComparison.Ordinal)
        || line.StartsWith(ScreenshotErrorPrefix, System.StringComparison.Ordinal);
}
