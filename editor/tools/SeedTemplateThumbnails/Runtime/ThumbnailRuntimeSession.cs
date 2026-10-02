// ============================================================
//  ThumbnailRuntimeSession.cs — 撮影に使うランタイム 1 回分（起動・IPC・シーンの読み込み・撮影・終了）
//
//  【流れ】
//    1. 環境変数（SEED_HEADLESS=1・SEED_SIM_SCALE_FACTOR・SEED_SAVE_DIR）を自分に置いてから、
//       QuietProcess で SEED.exe --mode=play --assets-root=… --scene=<起動用の舞台> --ipc-port=… --ipc-token=… --parent-pid=<自分>
//       を起動する（--parent-pid: このツールが落ちたらランタイムも終わる。runtime の parent_guard）
//    2. TCP（127.0.0.1）へつなぎ、HELLO:<トークン> → READY を待つ（IpcLineChannel。エディタ・SeedAndroid と同じ行の送受信）
//    3. 件ごとに LOAD_SCENE:<シーンの絶対パス> → SCENE_LOADED / LOAD_ERROR、ログの [THUMB] の合図を待ち、
//       SCREENSHOT:game,<画像の絶対パス> → SCREENSHOT_DONE:<パス>,<幅>,<高さ> / SCREENSHOT_ERROR
//    4. STOP（セーブを書き出して終わる）→ 終わらなければ自分が起動したプロセスだけを止める
//  命令の書式の正典はランタイムの runtime/src/engine/core/app_base/ipc.rs。
// ============================================================

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using SEEDEditor.Ipc;

namespace SEEDEditor.Tools.SeedTemplateThumbnails.Runtime;

/// <summary>舞台のスクリプトの合図の待ちの結果の種類。</summary>
public enum StageWaitKind
{
    /// <summary>撮ってよい（ready の合図）。</summary>
    Ready,
    /// <summary>見本の操作ができなかった（failed の合図。データか受け皿の問題なのでやり直しても変わらない）。</summary>
    Failed,
    /// <summary>合図が来ないまま時間切れ（ランタイムのフレームが止まった疑い。起動し直すと直ることがある）。</summary>
    TimedOut,
    /// <summary>ランタイムが終わった・つなぎが切れた。</summary>
    RuntimeEnded,
}

/// <summary>舞台のスクリプトの合図の待ちの結果。</summary>
/// <param name="Kind">結果の種類。</param>
/// <param name="Reason">撮れない理由（Ready なら空）。</param>
public readonly record struct StageWaitResult(StageWaitKind Kind, string Reason)
{
    /// <summary>撮ってよいか。</summary>
    public bool IsReady => Kind == StageWaitKind.Ready;
}

/// <summary>撮影の 1 回分の結果（撮った画像の大きさ）。</summary>
/// <param name="Path">画像の絶対パス。</param>
/// <param name="Width">幅（画素）。</param>
/// <param name="Height">高さ（画素）。</param>
public readonly record struct ScreenshotResult(string Path, int Width, int Height);

/// <summary>撮影に使うランタイム 1 回分。</summary>
public sealed class ThumbnailRuntimeSession : IDisposable
{
    // ── 命令・応答（ランタイムの ipc.rs と同じ文字列。共有のものは RuntimeIpcCommands）──

    private const string LoadScenePrefix = "LOAD_SCENE:";
    private const string SceneLoadedPrefix = "SCENE_LOADED:";
    private const string LoadErrorPrefix = "LOAD_ERROR:";
    private const string StopCommand = "STOP";

    // ── 舞台のスクリプトの合図（templates/ui/scripts/ThumbnailStage.cs と同じ）──

    /// <summary>合図の行の印。</summary>
    public const string SignalPrefix = "[THUMB]";
    /// <summary>撮ってよい合図の語。</summary>
    private const string ReadyWord = "ready";
    /// <summary>諦めた合図の語。</summary>
    private const string FailedWord = "failed";

    // ── 環境変数 ─────────────────────────────────────

    /// <summary>ランタイムにフレームを回し続けさせる（窓が画面の外でも時間が進み、撮れる）。</summary>
    private const string EnvHeadless = "SEED_HEADLESS";
    private const string EnvHeadlessOn = "1";
    /// <summary>表示倍率（1 dp の画素数）の模擬。</summary>
    private const string EnvScaleFactor = "SEED_SIM_SCALE_FACTOR";
    /// <summary>セーブデータの置き場（利用者のセーブに触れない）。</summary>
    private const string EnvSaveDir = "SEED_SAVE_DIR";

    // ── 時間 ───────────────────────────────────────

    /// <summary>起動してから IPC につながるまでの上限（GPU の初期化・シェーダーの準備）。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(180);
    /// <summary>つなぎ直しの間隔。</summary>
    private static readonly TimeSpan ConnectRetryInterval = TimeSpan.FromMilliseconds(500);
    /// <summary>1 回のつなぎの上限。</summary>
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(2);
    /// <summary>挨拶（HELLO → READY）の上限。</summary>
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(30);
    /// <summary>シーンの読み込みの上限（モデルの読み込みを含む）。</summary>
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(120);
    /// <summary>スクリーンショットの上限。</summary>
    private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(30);
    /// <summary>STOP の後に終わるのを待つ上限。</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    /// <summary>応答を待つ間にランタイムが生きているかを見直す間隔。</summary>
    private static readonly TimeSpan ReplyPollInterval = TimeSpan.FromMilliseconds(250);
    /// <summary>空いているポートを探す数（最初のポートから）。</summary>
    private const int PortSearchCount = 10;
    /// <summary>トークンのバイト数（16 進で 2 倍の文字数になる）。</summary>
    private const int TokenBytes = 16;

    /// <summary>起動したプロセス。</summary>
    private readonly QuietProcess _process;

    /// <summary>TCP のつなぎ。</summary>
    private readonly TcpClient _client;

    /// <summary>行の送受信。</summary>
    private readonly IpcLineChannel _channel;

    /// <summary>受け取った行（受信のスレッドから積まれる）。</summary>
    private readonly BlockingCollection<string> _incoming = new();

    /// <summary>ランタイムのログ。</summary>
    public RuntimeLog Log { get; }

    /// <summary>使ったポート。</summary>
    public int Port { get; }

    /// <summary>窓が出たときに前面を奪ったか（null = 分からない）。</summary>
    public bool? StoleForeground => _process.StoleForeground;

    private ThumbnailRuntimeSession(QuietProcess process, TcpClient client, IpcLineChannel channel, RuntimeLog log, int port)
    {
        _process = process;
        _client = client;
        _channel = channel;
        Log = log;
        Port = port;
    }

    // ============================================================
    //  起動
    // ============================================================

    /// <summary>
    /// ランタイムを起動してつなぐ。
    /// </summary>
    /// <param name="exe">SEED.exe。</param>
    /// <param name="workingDirectory">ランタイムの作業フォルダ（../scripting の DLL を読む起点）。</param>
    /// <param name="assetsRoot">一時のプロジェクトのアセットルート。</param>
    /// <param name="bootScene">起動用の舞台（assets:// の仮想パス）。</param>
    /// <param name="firstPort">最初に試すポート。</param>
    /// <param name="workRoot">作業フォルダ（ログ・セーブの置き場）。</param>
    /// <param name="logFileName">ランタイムのログのファイル名（作業フォルダの下。起動ごとに分ける）。</param>
    /// <param name="renderScale">1 dp の画素数（表示倍率の模擬）。</param>
    /// <param name="log">出来事を伝える先。</param>
    /// <returns>つないだセッション。</returns>
    /// <exception cref="InvalidOperationException">起動・接続・挨拶に失敗した（理由つき）。</exception>
    public static ThumbnailRuntimeSession Start(
        string exe, string workingDirectory, string assetsRoot, string bootScene, int firstPort,
        string workRoot, string logFileName, double renderScale, Action<string> log)
    {
        int port = FindFreePort(firstPort)
                   ?? throw new InvalidOperationException($"ポート {firstPort}〜{firstPort + PortSearchCount - 1} がすべて使われています");
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes));
        string saveDir = Path.Combine(workRoot, "save");
        Directory.CreateDirectory(saveDir);
        string logPath = Path.Combine(workRoot, logFileName);

        // 子は自分の環境変数を引き継ぐ（このツールのプロセスだけの設定。利用者の環境は変えない）
        Environment.SetEnvironmentVariable(EnvHeadless, EnvHeadlessOn);
        Environment.SetEnvironmentVariable(EnvScaleFactor, renderScale.ToString("R", CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(EnvSaveDir, saveDir);

        var args = new List<string>
        {
            "--mode=play",
            "--assets-root=" + assetsRoot,
            "--scene=" + bootScene,
            "--ipc-port=" + port.ToString(CultureInfo.InvariantCulture),
            "--ipc-token=" + token,
            "--parent-pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
        };
        log($"[thumbnails] ランタイムを起動します: {exe}（作業フォルダ {workingDirectory}・ポート {port}）");
        var process = QuietProcess.Start(exe, args, workingDirectory, logPath, log);
        var runtimeLog = new RuntimeLog(logPath);

        TcpClient? client = null;
        IpcLineChannel? channel = null;
        try
        {
            client = Connect(port, process);
            channel = new IpcLineChannel(client.GetStream(), "SeedTemplateThumbnails");
            var session = new ThumbnailRuntimeSession(process, client, channel, runtimeLog, port);
            channel.MessageReceived += line => session._incoming.Add(line);
            channel.Start();
            channel.Send(RuntimeIpcCommands.Hello(token));
            var reply = session.WaitForReply(l => RuntimeIpcCommands.IsReady(l) || RuntimeIpcCommands.IsDenied(l), HelloTimeout);
            if (reply is null || RuntimeIpcCommands.IsDenied(reply))
                throw new InvalidOperationException($"IPC の挨拶に失敗しました（{reply ?? "応答なし"}）");
            return session;
        }
        catch
        {
            // つなぎを閉じ、自分が起動したプロセスだけを止める
            channel?.Dispose();
            client?.Dispose();
            KillIfRunning(process);
            process.Dispose();
            throw;
        }
    }

    /// <summary>最初のポートから順に、いま待ち受けに使われていないポートを探す。</summary>
    private static int? FindFreePort(int first)
    {
        var used = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        for (int p = first; p < first + PortSearchCount; p++)
            if (!used.Contains(p)) return p;
        return null;
    }

    /// <summary>ランタイムが待ち受けを始めるまでつなぎ直す（プロセスが終わったら諦める）。</summary>
    private static TcpClient Connect(int port, QuietProcess process)
    {
        var deadline = DateTime.UtcNow + ConnectTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited())
                throw new InvalidOperationException("ランタイムが IPC の待ち受けを始める前に終わりました（作業フォルダのランタイムのログを見てください）");
            var client = new TcpClient();
            try
            {
                if (client.ConnectAsync(IPAddress.Loopback, port).Wait(ConnectAttemptTimeout) && client.Connected) return client;
            }
            catch (AggregateException) { }
            catch (SocketException) { }
            client.Dispose();
            Thread.Sleep(ConnectRetryInterval);
        }
        throw new InvalidOperationException($"ランタイムの IPC（127.0.0.1:{port}）につながりませんでした");
    }

    // ============================================================
    //  命令
    // ============================================================

    /// <summary>ランタイムが生きているか。</summary>
    public bool IsAlive => !_process.HasExited() && !_channel.IsClosed;

    /// <summary>
    /// シーンを読み込む。
    /// </summary>
    /// <param name="scenePath">シーンの絶対パス。</param>
    /// <returns>読めたら null、読めなければ理由。</returns>
    public string? LoadScene(string scenePath)
    {
        DrainReplies();
        _channel.Send(LoadScenePrefix + scenePath);
        var reply = WaitForReply(l => l.StartsWith(SceneLoadedPrefix, StringComparison.Ordinal)
                                      || l.StartsWith(LoadErrorPrefix, StringComparison.Ordinal), LoadTimeout);
        if (reply is null) return "シーンの読み込みの応答がありません";
        return reply.StartsWith(LoadErrorPrefix, StringComparison.Ordinal) ? reply[LoadErrorPrefix.Length..] : null;
    }

    /// <summary>
    /// 舞台のスクリプトの合図（[THUMB] ready / failed &lt;札&gt;）を待つ。
    /// </summary>
    /// <param name="ticket">待つ札。</param>
    /// <param name="timeout">待つ上限。</param>
    /// <param name="seen">待つ間に読んだログの行の積み先。</param>
    /// <returns>待ちの結果（撮ってよいか・だめならその種類と理由）。</returns>
    public StageWaitResult WaitForStageReady(string ticket, TimeSpan timeout, List<string> seen)
    {
        string ready = $"{SignalPrefix} {ReadyWord} {ticket}";
        string failed = $"{SignalPrefix} {FailedWord} {ticket}";
        var line = Log.WaitFor(l => EndsWithSignal(l, ready) || l.Contains(failed, StringComparison.Ordinal),
            timeout, seen, () => IsAlive);
        if (line is null)
        {
            return IsAlive
                ? new StageWaitResult(StageWaitKind.TimedOut, "舞台のスクリプトの合図がありません（時間切れ）")
                : new StageWaitResult(StageWaitKind.RuntimeEnded, "ランタイムが終わりました");
        }
        if (EndsWithSignal(line, ready)) return new StageWaitResult(StageWaitKind.Ready, "");
        int at = line.IndexOf(failed, StringComparison.Ordinal);
        return new StageWaitResult(StageWaitKind.Failed, "見本の操作ができません: " + line[(at + failed.Length)..].Trim());
    }

    /// <summary>合図の行か（行の終わりが合図。"t01" と "t010" を取り違えない）。</summary>
    private static bool EndsWithSignal(string line, string signal) => line.EndsWith(signal, StringComparison.Ordinal);

    /// <summary>
    /// ゲームの画面を撮る（GPU から読み戻した PNG）。
    /// </summary>
    /// <param name="path">書き出す画像の絶対パス。</param>
    /// <param name="error">撮れなかった理由。</param>
    /// <returns>撮れたら結果、撮れなければ null。</returns>
    public ScreenshotResult? Screenshot(string path, out string error)
    {
        DrainReplies();
        _channel.Send(RuntimeIpcCommands.Screenshot(RuntimeIpcCommands.ScreenshotGameTarget, path));
        var reply = WaitForReply(RuntimeIpcCommands.IsScreenshotReply, ScreenshotTimeout);
        if (reply is null)
        {
            error = "スクリーンショットの応答がありません";
            return null;
        }
        if (reply.StartsWith(RuntimeIpcCommands.ScreenshotErrorPrefix, StringComparison.Ordinal))
        {
            error = reply[RuntimeIpcCommands.ScreenshotErrorPrefix.Length..];
            return null;
        }
        // SCREENSHOT_DONE:<パス>,<幅>,<高さ>（パスはカンマを含みうるので後ろから 2 つを切る）
        var body = reply[RuntimeIpcCommands.ScreenshotDonePrefix.Length..];
        int last = body.LastIndexOf(RuntimeIpcCommands.ArgumentSeparator);
        int prev = last > 0 ? body.LastIndexOf(RuntimeIpcCommands.ArgumentSeparator, last - 1) : -1;
        if (prev < 0
            || !int.TryParse(body[(prev + 1)..last], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            || !int.TryParse(body[(last + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
        {
            error = "スクリーンショットの応答が読めません: " + reply;
            return null;
        }
        error = "";
        return new ScreenshotResult(body[..prev], w, h);
    }

    // ============================================================
    //  応答の待ち
    // ============================================================

    /// <summary>
    /// 条件に合う応答が来るまで待つ（合わない行は捨てる）。ランタイムが終わった・つなぎが切れたら、時間切れを待たずに諦める。
    /// </summary>
    private string? WaitForReply(Func<string, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            // 少しずつ待ち、合間にランタイムが生きているかを見る（終わったプロセスの応答を上限まで待たない）
            var slice = left < ReplyPollInterval ? left : ReplyPollInterval;
            if (_incoming.TryTake(out var line, slice))
            {
                if (match(line)) return line;
                continue;
            }
            if (!IsAlive) return null;
        }
    }

    /// <summary>前の命令の残りの応答を捨てる。</summary>
    private void DrainReplies()
    {
        while (_incoming.TryTake(out _)) { }
    }

    // ============================================================
    //  終了
    // ============================================================

    /// <summary>
    /// STOP を送って終わるのを待つ。終わらなければ自分が起動したプロセスだけを止める。
    /// </summary>
    /// <returns>STOP で終わったら true。</returns>
    public bool Stop()
    {
        _channel.TrySend(StopCommand);
        bool exited = _process.Process.WaitForExit((int)StopTimeout.TotalMilliseconds);
        if (!exited) KillIfRunning(_process);
        return exited;
    }

    /// <summary>自分が起動したプロセスがまだ動いていれば止める。</summary>
    private static void KillIfRunning(QuietProcess process)
    {
        if (process.HasExited()) return;
        try { process.Process.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>つなぎを閉じる（プロセスが残っていれば止める）。</summary>
    public void Dispose()
    {
        _channel.Dispose();
        _client.Dispose();
        KillIfRunning(_process);
        _process.Dispose();
        _incoming.Dispose();
    }
}
