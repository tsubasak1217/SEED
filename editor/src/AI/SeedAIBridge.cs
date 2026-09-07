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
//   GET  /seed-ai/state               → インスタンス識別情報（pid / port / token / headless）
//   POST /seed-ai/cmd                 → 任意のコマンドを JSON で実行
//
//  【インスタンス束縛（重要）】
//   かつてポートは 7234 固定で、誰が繋いできたかも見ていなかった。そのため
//   MCP が起動したつもりのヘッドレスエディタ宛のコマンドが、同じポートを先に
//   掴んでいた「利用者が手で開いているエディタ」に届き、別シーンの内容で
//   .scene を上書きしたうえでそのエディタを終了させる事故が起きた。
//   現在は:
//     ・起動時に --ai-port / --ai-token を受け取ったら、そのポートを必ず掴む。
//       掴めなければフォールバックせずプロセスを異常終了させる。
//     ・トークンが設定されていれば X-Seed-Token ヘッダーの一致を要求する。
//     ・許可判定は AiOperationPolicy に集約する（既定は読み取り専用）。
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
    /// <summary>
    /// HTTP サーバーの既定ポート番号。
    /// <c>--ai-port</c> が指定されていない（利用者が手で起動した）ときだけ使う。
    /// </summary>
    public const int PORT = AiOperationPolicy.DEFAULT_PORT;

    /// <summary>
    /// 指定ポートを掴めずにエディタを終了させるときの終了コード。
    /// MCP の seed_launch はこの値でポート衝突を判別できる。
    /// </summary>
    public const int EXIT_CODE_BIND_FAILED = 78;

    /// <summary>トークン不一致で拒否するときの HTTP ステータスコード。</summary>
    private const int HTTP_STATUS_FORBIDDEN = 403;

    // ── フィールド ────────────────────────────────────────────────────
    private readonly HttpListener            _listener;
    private readonly EditorCommandExecutor   _executor;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>このブリッジが待ち受けるポート。</summary>
    private readonly int                     _port;

    /// <summary>待ち受け URL の接頭辞（ログ表示にも使う）。</summary>
    private readonly string                  _prefix;

    // ── コンストラクタ ────────────────────────────────────────────────

    /// <summary>ブリッジを初期化する。<see cref="Start"/> を呼ぶまでは待機しない。</summary>
    public SeedAIBridge(EditorCommandExecutor executor)
    {
        _executor = executor;
        _port     = AiOperationPolicy.Port;
        _prefix   = $"http://localhost:{_port}/seed-ai/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_prefix);
    }

    // ── 公開メソッド ──────────────────────────────────────────────────

    /// <summary>
    /// HTTP サーバーをバックグラウンドで起動する。
    ///
    /// <para>
    /// ポートが <c>--ai-port</c> で明示指定されている場合、掴めなかったら
    /// **プロセスごと異常終了する**。別インスタンスへ黙って乗り移らせないため、
    /// フォールバックは一切しない（これが事故の根本原因だった）。
    /// 指定が無い（利用者が手で起動した）場合は従来どおりログだけ残して起動を続ける。
    /// </para>
    /// </summary>
    public void Start()
    {
        try
        {
            _listener.Start();
            _ = ListenLoopAsync(_cts.Token);
            EditorLog.Write($"[SeedAIBridge] 起動: {_prefix} "
                          + $"(pid={Environment.ProcessId}, headless={AiOperationPolicy.IsHeadless}, "
                          + $"token={(string.IsNullOrEmpty(AiOperationPolicy.Token) ? "なし" : "あり")}, "
                          + $"AI操作={(AiOperationPolicy.MutationsEnabled ? "許可" : "読み取り専用")})");
        }
        catch (Exception ex)
        {
            if (AiOperationPolicy.HasExplicitPort)
            {
                EditorLog.Write(
                    $"[SeedAIBridge] 致命的: 指定ポート {_port} を確保できませんでした: {ex.Message}"
                  + " / 別インスタンスへ接続してしまう事故を防ぐため、フォールバックせず終了します。");
                Environment.Exit(EXIT_CODE_BIND_FAILED);
                return;
            }
            EditorLog.Write($"[SeedAIBridge] 起動失敗 (ポート {_port} が使用中の可能性): {ex.Message}");
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

        // ── インスタンス束縛の検証 ────────────────────────────────
        // トークンが設定されているインスタンス（MCP が起動したもの）は、
        // 一致するトークンを持つリクエスト以外を一切受け付けない。
        // ここを通す前に UI スレッドへ入らないのは、拒否のコストを最小にするため。
        if (!AiOperationPolicy.IsTokenValid(ctx.Request.Headers[AiOperationPolicy.TOKEN_HEADER]))
        {
            ctx.Response.StatusCode = HTTP_STATUS_FORBIDDEN;
            EditorLog.Write($"[SeedAIBridge] トークン不一致のリクエストを拒否しました: {method} {path}");
            return $"ERROR: {AiOperationPolicy.DENY_BAD_TOKEN}";
        }

        // ── インスタンス識別（GET /seed-ai/state）────────────────
        // seed_launch が「起動したのは本当にこのプロセスか」を確かめるための応答。
        // UI スレッドを介さずに答えられるので、起動待ちの間も確実に返る。
        if (path == "/seed-ai/state" && method == "GET")
            return BuildInstanceStateJson();

        // すべてのエディタ操作を WPF UI スレッドで実行する。
        // EditorCommandExecutor 内の IPC 送信・TCS 操作が UI スレッド前提のため。
        return await Application.Current.Dispatcher.InvokeAsync<Task<string>>(async () =>
        {
            return path switch
            {
                // シーン情報取得
                "/seed-ai/scene" when method == "GET"
                    => await _executor.ExecuteAsync(new ToolCall
                       { FunctionName = "get_scene_info", ArgumentsJson = "{}" },
                       origin: AiCommandOrigin.Remote),

                // アセット一覧（?dir=subdir で絞り込み可能）
                "/seed-ai/assets" when method == "GET"
                    => await _executor.ExecuteAsync(new ToolCall
                       {
                           FunctionName  = "list_asset_files",
                           ArgumentsJson = BuildAssetsArgs(ctx.Request.QueryString),
                       },
                       origin: AiCommandOrigin.Remote),

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
        // 外部プロセスからの操作なので Remote 発信として許可判定を通す
        //（既定は読み取り専用。変更系は AiOperationPolicy が拒否する）。
        return await _executor.ExecuteAsync(new ToolCall
        {
            FunctionName  = cmdName,
            ArgumentsJson = argsJson,
        }, includeSceneInfo: false, origin: AiCommandOrigin.Remote);
    }

    /// <summary>
    /// インスタンス識別情報（GET /seed-ai/state）の JSON を組み立てる。
    ///
    /// MCP サーバーはこの pid と token が「自分が起動したプロセスのもの」と
    /// 一致することを確かめてから、以後のコマンドを送る。ここが一致しない限り
    /// MCP は変更系ツールを一切実行しない。
    /// </summary>
    private string BuildInstanceStateJson()
    {
        var payload = new
        {
            ok                = true,
            pid               = Environment.ProcessId,
            port              = _port,
            token             = AiOperationPolicy.Token,
            headless          = AiOperationPolicy.IsHeadless,
            mutations_enabled = AiOperationPolicy.MutationsEnabled,
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>クエリパラメータからアセット一覧用の引数 JSON を生成する。</summary>
    private static string BuildAssetsArgs(NameValueCollection qs)
    {
        var dir = qs["dir"] ?? "";
        return string.IsNullOrEmpty(dir) ? "{}" : $"{{\"subdirectory\":\"{dir}\"}}";
    }
}
