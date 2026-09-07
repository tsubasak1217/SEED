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

    /// <summary>ヘッドレス起動を指示するコマンドライン引数。</summary>
    private const string ARG_HEADLESS = "--headless";

    /// <summary>起動時に開くシーンを指示するコマンドライン引数。</summary>
    private const string ARG_SCENE = "--scene";

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
    public static async Task<string?> ProbeAsync(string apiBase)
    {
        // 起動待ちループで何度も叩くため、専用の短いタイムアウトのクライアントを使う。
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(PROBE_TIMEOUT_SECONDS) };
        try
        {
            var body    = $"{{\"cmd\":\"{PROBE_COMMAND}\"}}";
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp    = await probe.PostAsync($"{apiBase}/cmd", content);
            return await resp.Content.ReadAsStringAsync();
        }
        catch
        {
            // 接続拒否・タイムアウト = まだ起動していない（正常な待ち状態）。
            return null;
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
    /// エディタを起動し、HTTP ブリッジが応答するまで待つ。
    /// </summary>
    /// <param name="apiBase">ブリッジのベース URL。</param>
    /// <param name="headless">true なら --headless（画面に出さない）で起動する。</param>
    /// <param name="scenePath">起動時に開く .scene の絶対パス。null なら前回シーンを復元する。</param>
    /// <param name="waitSeconds">ブリッジ応答を待つ上限（秒）。</param>
    /// <returns>結果の JSON 文字列（ok / pid / state / already_running など）。</returns>
    public static async Task<string> LaunchAsync(
        string apiBase, bool headless, string? scenePath, double waitSeconds)
    {
        // すでに起動しているなら二重起動しない（ポート 7234 は 1 プロセスしか掴めない）。
        var existing = await ProbeAsync(apiBase);
        if (existing is not null)
        {
            // すでに起動しているなら、ランタイム接続まで進んでいなくてもそのまま返す
            // （別の誰かが起動中のプロセスを勝手に待ち続けても意味がないため）。
            return $"{{\"ok\":true,\"already_running\":true,\"state\":{JsonOrString(existing)}}}";
        }

        var exePath = ResolveEditorExePath();
        if (exePath is null)
        {
            return "ERROR: SEEDEditor.exe が見つかりません。"
                 + "`dotnet build editor/SEEDEditor.csproj` でエディタをビルドしてください。";
        }

        var args = new StringBuilder();
        if (headless) args.Append(ARG_HEADLESS);
        if (!string.IsNullOrWhiteSpace(scenePath))
        {
            if (args.Length > 0) args.Append(' ');
            args.Append(ARG_SCENE).Append(" \"").Append(Path.GetFullPath(scenePath)).Append('"');
        }

        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName         = exePath,
                Arguments        = args.ToString(),
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute  = false,
                CreateNoWindow   = true,
            }) ?? throw new InvalidOperationException("Process.Start が null を返しました。");
        }
        catch (Exception ex)
        {
            return $"ERROR: エディタの起動に失敗しました（{exePath}）: {ex.Message}";
        }

        // ブリッジが応答するまでポーリングする。
        var limit    = Math.Clamp(waitSeconds, MIN_WAIT_SECONDS, MAX_WAIT_SECONDS);
        var deadline = DateTime.UtcNow.AddSeconds(limit);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                return $"ERROR: エディタが起動直後に終了しました（終了コード {process.ExitCode}）。"
                     + "editor/logs/SEEDEditor.log と editor/bin/.../crash.log を確認してください。";
            }

            // ブリッジが応答するだけでは足りない。ランタイム（子プロセス）が接続し、
            // シーンを読み終えて初めて撮影・再生ができる。runtime_connected を待つ。
            var state = await ProbeAsync(apiBase);
            if (state is not null && IsRuntimeConnected(state) && IsSceneReady(state, scenePath))
            {
                return $"{{\"ok\":true,\"already_running\":false,\"pid\":{process.Id},"
                     + $"\"exe\":{JsonString(exePath)},\"headless\":{(headless ? "true" : "false")},"
                     + $"\"state\":{JsonOrString(state)}}}";
            }

            await Task.Delay(READY_POLL_INTERVAL_MS);
        }

        return $"ERROR: 起動から {limit} 秒以内にランタイムが接続しませんでした（{apiBase}）。"
             + $"PID={process.Id} は起動したままです。seed_state / seed_log で状況を確認するか、"
             + "wait_seconds を増やしてください（初回はランタイムの cargo build が走ることがあります）。";
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
