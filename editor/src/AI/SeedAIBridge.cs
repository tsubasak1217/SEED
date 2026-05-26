// ============================================================
//  SeedAIBridge.cs — Gemini CLI から SEED エディタを操作する HTTP ブリッジ
//
//  Gemini CLI（外部エージェント）が PowerShell の Invoke-RestMethod で
//  シーン操作コマンドを呼び出せるよう、ローカル HTTP サーバーを提供する。
//
//  【なぜ HTTP API か】
//   Gemini CLI は JSON 文字列を出力するのではなく、自分自身のシェルツールで
//   コマンドを実行して結果を観察するエージェントとして設計されている。
//   "実行 → 観察 → 再調整" ループが機能することで精度・速度が大幅に向上する。
//
//  【エンドポイント一覧】
//   GET  /seed-ai/scene               → get_scene_info
//   GET  /seed-ai/assets[?dir=subdir] → list_asset_files
//   POST /seed-ai/cmd                 → 任意のコマンドを JSON で実行
//
//  【POST /seed-ai/cmd のリクエスト形式】
//   { "cmd": "コマンド名", ... 引数フィールド ... }
//   例: { "cmd": "add_actor", "name": "Cube", "x": 0, "y": 0, "z": 0 }
//       { "cmd": "move_actor", "actor_dfs_id": 1, "x": 2.0, "y": 0, "z": 0 }
//       { "cmd": "add_component", "actor_dfs_id": 1, "component_type": "Model" }
//       { "cmd": "set_value", "actor_dfs_id": 1, "slot_idx": 0, "key": "model_path", "value": "C:/..." }
//       { "cmd": "remove_actor", "actor_dfs_id": 1 }
//       { "cmd": "write_asset_file", "relative_path": "scripts/Foo.cs", "content": "..." }
// ============================================================

using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SEEDEditor.AI.Models;
using SEEDEditor.AI.Tools;

namespace SEEDEditor.AI;

/// <summary>
/// ローカル HTTP サーバーとして動作し、Gemini CLI などの外部エージェントからの
/// シーン操作コマンドを受け付けて <see cref="EditorCommandExecutor"/> へ委譲する。
/// すべての操作は WPF Dispatcher にマーシャルして UI スレッドで実行する。
/// </summary>
public sealed class SeedAIBridge : IDisposable
{
    // ── 定数 ─────────────────────────────────────────────────────────
    /// <summary>HTTP サーバーがリッスンするポート番号。</summary>
    public const int PORT = 7234;
    private static readonly string PREFIX = $"http://localhost:{PORT}/seed-ai/";

    // ── フィールド ────────────────────────────────────────────────────
    private readonly HttpListener            _listener;
    private readonly EditorCommandExecutor   _executor;
    private readonly CancellationTokenSource _cts = new();

    // ── コンストラクタ ────────────────────────────────────────────────

    /// <summary>ブリッジを初期化する。<see cref="Start"/> を呼ぶまでは待機しない。</summary>
    public SeedAIBridge(EditorCommandExecutor executor)
    {
        _executor = executor;
        _listener = new HttpListener();
        _listener.Prefixes.Add(PREFIX);
    }

    // ── 公開メソッド ──────────────────────────────────────────────────

    /// <summary>HTTP サーバーをバックグラウンドで起動する。</summary>
    public void Start()
    {
        try
        {
            _listener.Start();
            _ = ListenLoopAsync(_cts.Token);
            EditorLog.Write($"[SeedAIBridge] 起動: {PREFIX}");
        }
        catch (Exception ex)
        {
            EditorLog.Write($"[SeedAIBridge] 起動失敗 (ポート {PORT} が使用中の可能性): {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop();  } catch { }
        try { _listener.Close(); } catch { }
        _cts.Dispose();
    }

    // ── 受信ループ ────────────────────────────────────────────────────

    /// <summary>リクエストを受け付け続けるループ。バックグラウンドで動作する。</summary>
    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                // サーバー停止・キャンセル時はループを終了する
                break;
            }
            // リクエストは並行処理するが、_executor へのアクセスは
            // HandleRequestAsync 内で WPF Dispatcher にマーシャルして直列化する
            _ = HandleRequestAsync(ctx, ct);
        }
    }

    // ── リクエスト処理 ────────────────────────────────────────────────

    /// <summary>1 リクエストを処理して応答を返す。</summary>
    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        string result;
        try
        {
            result = await DispatchAsync(ctx, ct);
        }
        catch (Exception ex)
        {
            result = $"ERROR: {ex.Message}";
            EditorLog.Write($"[SeedAIBridge] リクエスト処理エラー: {ex.Message}");
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(result);
            ctx.Response.ContentType     = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            // ローカル専用 API だが CORS ヘッダーを付けておく（curl 等からのアクセスに対応）
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
        }
        catch { /* クライアントが切断済みの場合は無視 */ }
    }

    /// <summary>URL パスに基づいてコマンドをディスパッチする。</summary>
    private async Task<string> DispatchAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var path   = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
        var method = ctx.Request.HttpMethod.ToUpperInvariant();

        // すべてのエディタ操作を WPF UI スレッドで実行する。
        // EditorCommandExecutor 内の IPC 送信・TCS 操作が UI スレッド前提のため。
        return await Application.Current.Dispatcher.InvokeAsync<Task<string>>(async () =>
        {
            return path switch
            {
                // シーン情報取得
                "/seed-ai/scene" when method == "GET"
                    => await _executor.ExecuteAsync(new ToolCall
                       { FunctionName = "get_scene_info", ArgumentsJson = "{}" }),

                // アセット一覧（?dir=subdir で絞り込み可能）
                "/seed-ai/assets" when method == "GET"
                    => await _executor.ExecuteAsync(new ToolCall
                       {
                           FunctionName  = "list_asset_files",
                           ArgumentsJson = BuildAssetsArgs(ctx.Request.QueryString),
                       }),

                // 汎用コマンド実行（POST /seed-ai/cmd）
                "/seed-ai/cmd" when method == "POST"
                    => await ExecuteCmdAsync(ctx),

                _ => $"ERROR: 不明なエンドポイント '{path}' [{method}]\n" +
                     $"利用可能: GET /seed-ai/scene, GET /seed-ai/assets, POST /seed-ai/cmd",
            };
        }).Task.Unwrap();
    }

    /// <summary>POST /seed-ai/cmd の JSON ボディを解析してコマンドを実行する。</summary>
    private async Task<string> ExecuteCmdAsync(HttpListenerContext ctx)
    {
        // リクエストボディを読み取る
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8, leaveOpen: true))
            body = await reader.ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(body))
            return "ERROR: リクエストボディが空です。{ \"cmd\": \"コマンド名\", ...引数... } の形式で送ってください。";

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // "cmd" フィールドをコマンド名として取り出す
        if (!root.TryGetProperty("cmd", out var cmdEl) || string.IsNullOrWhiteSpace(cmdEl.GetString()))
            return "ERROR: 'cmd' フィールドが必要です。例: { \"cmd\": \"add_actor\", \"name\": \"Cube\", \"x\": 0, \"y\": 0, \"z\": 0 }";

        var cmdName = cmdEl.GetString()!;

        // "cmd" を除いた残りのプロパティを引数 JSON として再構築する
        using var mem    = new MemoryStream();
        using var writer = new Utf8JsonWriter(mem);
        writer.WriteStartObject();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == "cmd") continue;
            prop.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
        var argsJson = Encoding.UTF8.GetString(mem.ToArray());

        // MCP バッチモード: 自動シーン取得をスキップして IPC 往復を削減する。
        // seed_batch 内の各操作は中間結果が Gemini に届かないため
        // add_actor 後のシーン情報取得は不要。
        return await _executor.ExecuteAsync(new ToolCall
        {
            FunctionName  = cmdName,
            ArgumentsJson = argsJson,
        }, includeSceneInfo: false);
    }

    /// <summary>クエリパラメータからアセット一覧用の引数 JSON を生成する。</summary>
    private static string BuildAssetsArgs(NameValueCollection qs)
    {
        var dir = qs["dir"] ?? "";
        return string.IsNullOrEmpty(dir) ? "{}" : $"{{\"subdirectory\":\"{dir}\"}}";
    }
}
