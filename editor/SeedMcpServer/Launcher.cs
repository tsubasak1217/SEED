// ============================================================
//  Launcher.cs — エディタのヘッドレス起動 / 終了（seed_launch / seed_shutdown）
//
//  Blender の --background に相当する運用を可能にする部分。
//  AI が「エディタを自分で起動 → 操作 → 撮影 → 終了」まで完結できるようにする。
//
//  ここが担うのは 2 つだけ:
//    1. SEEDEditor.exe の場所を突き止めて --headless 付きで起動し、
//       「使える状態」になるまで待つ。具体的には HTTP ブリッジ（http://localhost:7234）が
//       応答し、かつランタイムが接続済み（scene 指定時はその読み込みも完了）になるまで。
//       ブリッジは MainWindow 生成直後から応答するので、そこで返すと撮影・再生が失敗する。
//    2. すでに起動しているかの判定（＝二重起動の防止）。
//
//  終了（seed_shutdown）は HTTP ブリッジ経由の cmd "shutdown" で行うため、
//  ここには含めない（Program.cs の PostCmdAsync をそのまま使う）。
// ============================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace SeedMcpServer;

/// <summary>
/// SEED エディタをヘッドレスで起動するためのユーティリティ。
/// </summary>
internal static class Launcher
{
    /// <summary>エディタ実行ファイル名。</summary>
    private const string EDITOR_EXE_NAME = "SEEDEditor.exe";

    /// <summary>
    /// エディタ exe の探索先を上書きする環境変数名。
    /// 開発・計測時に別 OutDir へビルドしたエディタを seed_launch で起動するために使う
    /// （docs/editor_mcp.md「自前ビルドで起動する」を参照）。
    /// </summary>
    private const string EDITOR_EXE_ENV_VAR = "SEED_EDITOR_EXE";

    /// <summary>ヘッドレス起動を指示するコマンドライン引数。</summary>
    private const string ARG_HEADLESS = "--headless";

    /// <summary>起動時に開くシーンを指示するコマンドライン引数。</summary>
    private const string ARG_SCENE = "--scene";

    /// <summary>AI ブリッジの待ち受けポートを指示するコマンドライン引数。</summary>
    private const string ARG_AI_PORT = "--ai-port";

    /// <summary>AI ブリッジのインスタンストークンを指示するコマンドライン引数。</summary>
    private const string ARG_AI_TOKEN = "--ai-token";

    /// <summary>ヘッドレス起動を指示する環境変数（引数の保険）。</summary>
    private const string ENV_HEADLESS = "SEED_HEADLESS";

    /// <summary>AI ブリッジのポートを渡す環境変数（引数の保険）。</summary>
    private const string ENV_AI_PORT = "SEED_AI_PORT";

    /// <summary>AI ブリッジのトークンを渡す環境変数（引数の保険）。</summary>
    private const string ENV_AI_TOKEN = "SEED_AI_TOKEN";

    /// <summary>ヘッドレス用に確保するポート範囲の下限。</summary>
    private const int INSTANCE_PORT_MIN = 7300;

    /// <summary>ヘッドレス用に確保するポート範囲の上限。</summary>
    private const int INSTANCE_PORT_MAX = 7399;

    /// <summary>インスタンストークンのバイト数（16 バイト = 32 桁の 16 進文字列）。</summary>
    private const int TOKEN_BYTES = 16;

    /// <summary>インスタンス識別を問い合わせるエンドポイント名。</summary>
    private const string STATE_ENDPOINT = "/state";

    /// <summary>起動待ちのポーリング間隔（ミリ秒）。</summary>
    private const int READY_POLL_INTERVAL_MS = 500;

    /// <summary>起動待ちの既定タイムアウト（秒）。WPF の初回起動＋ランタイム接続を見込む。</summary>
    public const double DEFAULT_WAIT_SECONDS = 60.0;

    /// <summary>起動待ちタイムアウトの下限（秒）。</summary>
    private const double MIN_WAIT_SECONDS = 1.0;

    /// <summary>起動待ちタイムアウトの上限（秒）。</summary>
    private const double MAX_WAIT_SECONDS = 300.0;

    /// <summary>疎通確認に使うコマンド名（POST /seed-ai/cmd）。</summary>
    private const string PROBE_COMMAND = "get_editor_state";

    /// <summary>疎通確認 1 回あたりのタイムアウト（秒）。起動直後は接続拒否が普通なので短くてよい。</summary>
    private const int PROBE_TIMEOUT_SECONDS = 3;

    /// <summary>
    /// エディタ状態 JSON の「ランタイム接続済み」フラグ名。
    /// HTTP ブリッジは MainWindow の生成直後から応答するが、その時点ではまだ
    /// ランタイム子プロセスが起動していない（state=Idle/Building）。
    /// この段階で撮影や再生を要求しても失敗するため、接続完了まで待つ判定に使う。
    /// </summary>
    private const string STATE_RUNTIME_CONNECTED = "runtime_connected";

    /// <summary>
    /// エディタ状態 JSON の「現在開いているシーン」フィールド名。
    /// <c>--scene</c> 指定時は、ランタイム接続だけでなくシーン読み込みの完了まで待つのに使う
    /// （読み込み前に撮ると空のシーンが写るため）。
    /// </summary>
    private const string STATE_SCENE_PATH = "scene_path";

    /// <summary>
    /// HTTP ブリッジがすでに応答するか（＝エディタが起動済みか）を調べる。
    /// </summary>
    /// <param name="apiBase">ブリッジのベース URL。</param>
    /// <returns>応答本文。到達できなければ null。</returns>
    public static async Task<string?> ProbeAsync(string apiBase, string? token = null)
    {
        // 起動待ちループで何度も叩くため、専用の短いタイムアウトのクライアントを使う。
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(PROBE_TIMEOUT_SECONDS) };
        try
        {
            var body    = $"{{\"cmd\":\"{PROBE_COMMAND}\"}}";
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/cmd")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(token)) req.Headers.Add(SeedInstance.TOKEN_HEADER, token);
            var resp = await probe.SendAsync(req);
            return await resp.Content.ReadAsStringAsync();
        }
        catch
        {
            // 接続拒否・タイムアウト = まだ起動していない（正常な待ち状態）。
            return null;
        }
    }

    /// <summary>
    /// <c>GET /seed-ai/state</c> でインスタンス識別情報（pid / port / token）を取得する。
    /// 到達できなければ null。
    /// </summary>
    public static async Task<string?> FetchInstanceStateAsync(string apiBase, string? token)
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(PROBE_TIMEOUT_SECONDS) };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}{STATE_ENDPOINT}");
            if (!string.IsNullOrEmpty(token)) req.Headers.Add(SeedInstance.TOKEN_HEADER, token);
            var resp = await probe.SendAsync(req);
            return await resp.Content.ReadAsStringAsync();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// ヘッドレスインスタンス用の空きポートを 1 つ選ぶ。
    ///
    /// 既定ポート 7234 は「利用者が手で起動したエディタ」の指定席なので絶対に使わない。
    /// 専用レンジ（<see cref="INSTANCE_PORT_MIN"/>〜<see cref="INSTANCE_PORT_MAX"/>）を
    /// 順に調べ、TCP で bind できたものを返す。
    /// </summary>
    /// <returns>空いているポート番号。全滅した場合は null。</returns>
    public static int? PickFreePort()
    {
        for (int port = INSTANCE_PORT_MIN; port <= INSTANCE_PORT_MAX; port++)
        {
            if (IsPortFree(port)) return port;
        }
        return null;
    }

    /// <summary>指定ポートが今この瞬間 bind 可能かを調べる。</summary>
    public static bool IsPortFree(int port)
    {
        System.Net.Sockets.TcpListener? listener = null;
        try
        {
            listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { listener?.Stop(); } catch { }
        }
    }

    /// <summary>ランダムなインスタンストークン（16 進小文字）を生成する。</summary>
    public static string GenerateToken()
        => Convert.ToHexString(
               System.Security.Cryptography.RandomNumberGenerator.GetBytes(TOKEN_BYTES))
           .ToLowerInvariant();

    /// <summary>
    /// <c>GET /state</c> の応答が「自分が起動したプロセス」のものかを検証する。
    /// pid とトークンの両方が一致して初めて true。
    /// </summary>
    public static bool IsExpectedInstance(string? stateJson, int expectedPid, string expectedToken)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(stateJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("pid", out var pidEl)
                || pidEl.ValueKind != System.Text.Json.JsonValueKind.Number
                || pidEl.GetInt32() != expectedPid)
            {
                return false;
            }
            if (!root.TryGetProperty("token", out var tokenEl)
                || tokenEl.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return false;
            }
            return string.Equals(tokenEl.GetString(), expectedToken, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// SEEDEditor.exe の絶対パスを解決する。
    ///
    /// <para>探索順（この MCP サーバー exe の位置を起点にする）:</para>
    /// <list type="number">
    ///   <item>同じディレクトリ … エディタビルド時にコピーされた配置</item>
    ///   <item>bin/&lt;Configuration&gt;/net9.0-windows … リポジトリ内の開発ビルド配置
    ///         （MCP は editor/SeedMcpServer/bin/&lt;Cfg&gt;/net9.0 に居るので 4 階層上が editor/）</item>
    /// </list>
    /// </summary>
    /// <returns>見つかった絶対パス。見つからなければ null。</returns>
    public static string? ResolveEditorExePath()
    {
        // 0) 環境変数による明示指定を最優先する（実在するファイルのときだけ採用）。
        //    利用者のエディタが起動していると通常の出力先はロックされているため、
        //    別の OutDir へビルドしたエディタを起動したいときに使う。
        var overridePath = Environment.GetEnvironmentVariable(EDITOR_EXE_ENV_VAR);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return Path.GetFullPath(overridePath);

        var baseDir = AppContext.BaseDirectory;

        // 1) エディタと同じフォルダへコピーされている配置
        var sameDir = Path.Combine(baseDir, EDITOR_EXE_NAME);
        if (File.Exists(sameDir)) return Path.GetFullPath(sameDir);

        // 2) 開発時配置: editor/SeedMcpServer/bin/<Cfg>/net9.0 → editor/
        //    Configuration 名は自分のパスから引き継ぐ（Debug で動いていれば Debug を見る）。
        var editorRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var configName = Path.GetFileName(Path.GetFullPath(Path.Combine(baseDir, "..")));
        foreach (var cfg in new[] { configName, "Debug", "Release" })
        {
            if (string.IsNullOrEmpty(cfg)) continue;
            var candidate = Path.Combine(editorRoot, "bin", cfg, "net9.0-windows", EDITOR_EXE_NAME);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return null;
    }

    /// <summary>
    /// エディタを起動し、**自分が起動したそのプロセス**が応答するまで待つ。
    ///
    /// <para>
    /// 従来は「ポート 7234 に誰か居れば起動済み」と判断していたため、利用者の
    /// エディタを自分の操作対象と誤認する事故が起きた。現在は:
    ///   1. 専用レンジから空きポートを選び、ランダムなトークンを生成する
    ///   2. <c>--ai-port</c> / <c>--ai-token</c> を付けてエディタを起動する
    ///      （エディタ側はそのポートを掴めなければ即終了する）
    ///   3. 起動したプロセスの生死を見ながら <c>GET /state</c> を叩き、
    ///      pid とトークンが一致して初めて「起動成功」とする
    /// </para>
    /// </summary>
    /// <param name="headless">true なら --headless（画面に出さない）で起動する。</param>
    /// <param name="scenePath">起動時に開く .scene の絶対パス。null なら前回シーンを復元する。</param>
    /// <param name="waitSeconds">応答を待つ上限（秒）。</param>
    /// <returns>結果の JSON 文字列（ok / pid / port / state など）。</returns>
    public static async Task<string> LaunchAsync(
        bool headless, string? scenePath, double waitSeconds)
    {
        // すでにこの MCP サーバーがインスタンスを束縛しているなら、二重起動しない。
        // 「ポートに誰か居るか」ではなく「自分が起動した相手が生きているか」で判断する。
        if (SeedInstance.IsBound)
        {
            var alive = await FetchInstanceStateAsync(SeedInstance.ApiBase, SeedInstance.Token);
            if (alive is not null)
                return $"{{\"ok\":true,\"already_running\":true,\"instance\":{SeedInstance.ToJson()},"
                     + $"\"state\":{JsonOrString(alive)}}}";
            // 死んでいたら束縛を捨てて起動し直す。
            SeedInstance.Clear();
        }

        var exePath = ResolveEditorExePath();
        if (exePath is null)
        {
            return "ERROR: SEEDEditor.exe が見つかりません。"
                 + "`dotnet build editor/SEEDEditor.csproj` でエディタをビルドしてください。";
        }

        var port = PickFreePort();
        if (port is null)
        {
            return $"ERROR: 空きポートがありません（{INSTANCE_PORT_MIN}〜{INSTANCE_PORT_MAX} をすべて確認）。"
                 + "起動しっぱなしのヘッドレスエディタが残っていないか確認してください。";
        }
        var token = GenerateToken();

        var args = new StringBuilder();
        if (headless) args.Append(ARG_HEADLESS);
        if (!string.IsNullOrWhiteSpace(scenePath))
        {
            if (args.Length > 0) args.Append(' ');
            args.Append(ARG_SCENE).Append(" \"").Append(Path.GetFullPath(scenePath)).Append('"');
        }
        // ポートとトークンは引数と環境変数の両方で渡す（どちらか一方でも届けば成立する）。
        args.Append(' ').Append(ARG_AI_PORT).Append(' ').Append(port.Value);
        args.Append(' ').Append(ARG_AI_TOKEN).Append(' ').Append(token);

        Process process;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName         = exePath,
                Arguments        = args.ToString(),
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute  = false,
                CreateNoWindow   = true,
            };
            if (headless) psi.Environment[ENV_HEADLESS] = "1";
            psi.Environment[ENV_AI_PORT]  = port.Value.ToString();
            psi.Environment[ENV_AI_TOKEN] = token;

            process = Process.Start(psi)
                   ?? throw new InvalidOperationException("Process.Start が null を返しました。");
        }
        catch (Exception ex)
        {
            return $"ERROR: エディタの起動に失敗しました（{exePath}）: {ex.Message}";
        }

        var apiBase  = SeedInstance.BaseUrlFor(port.Value);
        var limit    = Math.Clamp(waitSeconds, MIN_WAIT_SECONDS, MAX_WAIT_SECONDS);
        var deadline = DateTime.UtcNow.AddSeconds(limit);
        bool identified = false;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                return $"ERROR: エディタが起動直後に終了しました（終了コード {process.ExitCode}）。"
                     + $"ポート {port.Value} を確保できなかった可能性があります。"
                     + "editor/logs/SEEDEditor.log と editor/bin/.../crash.log を確認してください。";
            }

            // ── 1. 「自分が起動したプロセス」であることを pid + トークンで確認する ──
            if (!identified)
            {
                var stateJson = await FetchInstanceStateAsync(apiBase, token);
                if (IsExpectedInstance(stateJson, process.Id, token))
                {
                    identified = true;
                }
                else
                {
                    await Task.Delay(READY_POLL_INTERVAL_MS);
                    continue;
                }
            }

            // ── 2. ランタイム接続（scene 指定時はその読み込み）まで待つ ──
            // ブリッジが応答するだけでは足りない。ランタイム（子プロセス）が接続し、
            // シーンを読み終えて初めて撮影・再生ができる。
            var editorState = await ProbeAsync(apiBase, token);
            if (editorState is not null
                && IsRuntimeConnected(editorState)
                && IsSceneReady(editorState, scenePath))
            {
                SeedInstance.Bind(port.Value, token, process.Id, headless, attached: false);
                return $"{{\"ok\":true,\"already_running\":false,\"pid\":{process.Id},"
                     + $"\"port\":{port.Value},"
                     + $"\"exe\":{JsonString(exePath)},\"headless\":{(headless ? "true" : "false")},"
                     + $"\"state\":{JsonOrString(editorState)}}}";
            }

            await Task.Delay(READY_POLL_INTERVAL_MS);
        }

        // タイムアウト。起動したプロセスは掃除する（放置すると次回の空きポートを食い潰す）。
        var stage = identified ? "ランタイムが接続しませんでした" : "AI ブリッジが応答しませんでした";
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        return $"ERROR: 起動から {limit} 秒以内に{stage}（ポート {port.Value}, PID {process.Id}）。"
             + "起動したプロセスは終了させました。seed_log で状況を確認するか、"
             + "wait_seconds を増やしてください（初回はランタイムの cargo build が走ることがあります）。";
    }

    /// <summary>
    /// 利用者が開いているエディタへ、明示的な同意のもとで接続する（seed_attach）。
    ///
    /// <para>
    /// 接続先の <c>GET /state</c> が指定トークンで応答し、そのトークンが一致した
    /// ときだけ束縛する。トークンはエディタの「環境設定」に表示されるので、
    /// 利用者が意図して MCP へ渡したときにしか成立しない。
    /// </para>
    /// </summary>
    public static async Task<string> AttachAsync(int port, string token)
    {
        var apiBase   = SeedInstance.BaseUrlFor(port);
        var stateJson = await FetchInstanceStateAsync(apiBase, token);
        if (stateJson is null)
            return $"ERROR: ポート {port} のエディタへ接続できませんでした（起動していない可能性）。";

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(stateJson);
            var root = doc.RootElement;
            var actualToken = root.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
            if (!string.Equals(actualToken, token, StringComparison.Ordinal))
                return "ERROR: トークンが一致しません。エディタの「環境設定」に表示されている値を渡してください。";

            var pid      = root.TryGetProperty("pid", out var p) ? p.GetInt32() : 0;
            var headless = root.TryGetProperty("headless", out var h)
                        && h.ValueKind == System.Text.Json.JsonValueKind.True;

            SeedInstance.Bind(port, token, pid, headless, attached: true);
            return $"{{\"ok\":true,\"instance\":{SeedInstance.ToJson()},\"state\":{stateJson}}}";
        }
        catch (Exception ex)
        {
            return $"ERROR: /state の応答を解釈できませんでした: {ex.Message}";
        }
    }

    /// <summary>
    /// エディタ状態 JSON を見て、ランタイムが接続済みかどうかを判定する。
    /// JSON として読めない・フラグが無い場合は「まだ」とみなす（＝待ち続ける）。
    /// </summary>
    private static bool IsRuntimeConnected(string stateJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(stateJson);
            return doc.RootElement.TryGetProperty(STATE_RUNTIME_CONNECTED, out var el)
                && el.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// <c>--scene</c> 指定時に、そのシーンの読み込みが終わっているかを判定する。
    /// シーン未指定なら常に true（待つ理由がない）。
    /// </summary>
    private static bool IsSceneReady(string stateJson, string? requestedScene)
    {
        if (string.IsNullOrWhiteSpace(requestedScene)) return true;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(stateJson);
            if (!doc.RootElement.TryGetProperty(STATE_SCENE_PATH, out var el)) return false;
            var current = el.GetString();
            if (string.IsNullOrEmpty(current)) return false;
            // 区切り文字の違い（/ と \）を吸収して比較する。
            return string.Equals(
                Path.GetFullPath(current),
                Path.GetFullPath(requestedScene),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 応答本文が JSON ならそのまま、そうでなければ JSON 文字列として埋め込めるよう整形する。
    /// </summary>
    private static string JsonOrString(string body)
    {
        var trimmed = body.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[') ? body : JsonString(body);
    }

    /// <summary>任意の文字列を JSON 文字列リテラルへ変換する。</summary>
    private static string JsonString(string value)
        => System.Text.Json.JsonSerializer.Serialize(value);
}
