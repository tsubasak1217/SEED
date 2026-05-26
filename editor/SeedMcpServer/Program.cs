// ============================================================
//  SeedMcpServer — SEED エディタ MCP (Model Context Protocol) サーバー
//
//  Gemini CLI にシーン操作をネイティブツールとして公開する。
//  stdin/stdout で JSON-RPC 2.0（改行区切り）を話し、
//  ツール呼び出しは SeedAIBridge HTTP API へ転送する。
//
//  公開ツール（2つのみ）:
//    seed_query(type, dir?)         → GET シーン情報またはアセット一覧
//    seed_batch(operations: [...])  → POST 操作を一括実行（変更の唯一の手段）
//
//  【なぜ 2 ツールのみか】
//    Gemini CLI はツール呼び出しのたびに API コールを 1 回消費する。
//    操作を個別に呼ぶと 10～20+ コール/タスクになり RPM 制限に達する。
//    seed_batch で全操作をまとめることで 3～4 コール/タスクに固定できる。
//    「それ以外の変更手段がない」という仕組みで自然に強制される。
// ============================================================

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

// ── 設定 ─────────────────────────────────────────────────────────────────────
const string API_BASE = "http://localhost:7234/seed-ai";
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

// stderr をログ用に使う（stdout は JSON-RPC 専用で汚してはいけない）
Console.Error.WriteLine($"[SeedMcpServer] 起動 — SEED API: {API_BASE}");

// ── メインループ: 1 行 = 1 JSON-RPC メッセージ ────────────────────────────────
string? line;
while ((line = await Console.In.ReadLineAsync()) != null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    try
    {
        var reply = await ProcessMessageAsync(line, http);
        if (reply is not null)
        {
            Console.WriteLine(reply);
            // stdout はバッファリングされる場合があるため明示的にフラッシュする
            Console.Out.Flush();
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[SeedMcpServer] 処理エラー: {ex.Message}");
    }
}

// ── メッセージ処理 ────────────────────────────────────────────────────────────

/// <summary>1 つの JSON-RPC メッセージを処理し、レスポンス文字列を返す。通知は null。</summary>
static async Task<string?> ProcessMessageAsync(string json, HttpClient http)
{
    using var doc = JsonDocument.Parse(json);
    var root   = doc.RootElement;
    var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";

    // 通知（id フィールドなし）はレスポンス不要
    if (!root.TryGetProperty("id", out var id))
        return null;

    return method switch
    {
        // MCP ハンドシェイク
        "initialize" => Reply(id, new
        {
            protocolVersion = "2024-11-05",
            serverInfo      = new { name = "seed-mcp", version = "1.0.0" },
            capabilities    = new { tools = new { } }
        }),

        // ツール一覧
        "tools/list" => Reply(id, new { tools = BuildToolList() }),

        // ツール実行
        "tools/call" => await HandleToolCallAsync(id, root, http),

        // 死活監視
        "ping" => Reply(id, new { }),

        _ => ReplyError(id, -32601, $"未知のメソッド: {method}")
    };
}

/// <summary>tools/call をツール名でディスパッチする。</summary>
static async Task<string> HandleToolCallAsync(JsonElement id, JsonElement root, HttpClient http)
{
    var paramsEl = root.GetProperty("params");
    var name     = paramsEl.GetProperty("name").GetString() ?? "";
    paramsEl.TryGetProperty("arguments", out var args);

    try
    {
        var result = name switch
        {
            "seed_query" => await ExecQueryAsync(args, http),
            "seed_batch" => await ExecBatchAsync(args, http),
            _            => $"ERROR: 不明なツール '{name}'"
        };

        return Reply(id, new
        {
            content = new[] { new { type = "text", text = result } },
            isError = result.StartsWith("ERROR", StringComparison.Ordinal)
        });
    }
    catch (Exception ex)
    {
        return Reply(id, new
        {
            content = new[] { new { type = "text", text = $"ERROR: {ex.Message}" } },
            isError = true
        });
    }
}

// ── ツール実装 ────────────────────────────────────────────────────────────────

/// <summary>
/// seed_query: シーン情報またはアセット一覧を取得する。
/// 引数 type = "scene" | "assets"、dir は assets 時のサブディレクトリ絞り込み。
/// </summary>
static async Task<string> ExecQueryAsync(JsonElement args, HttpClient http)
{
    var type = args.ValueKind != JsonValueKind.Undefined && args.TryGetProperty("type", out var t)
        ? t.GetString() ?? "scene"
        : "scene";

    if (type == "scene")
    {
        var resp = await http.GetAsync($"{API_BASE}/scene");
        return await resp.Content.ReadAsStringAsync();
    }

    if (type == "assets")
    {
        var dir = args.TryGetProperty("dir", out var d) ? d.GetString() ?? "" : "";
        var url = string.IsNullOrEmpty(dir)
            ? $"{API_BASE}/assets"
            : $"{API_BASE}/assets?dir={Uri.EscapeDataString(dir)}";
        var resp = await http.GetAsync(url);
        return await resp.Content.ReadAsStringAsync();
    }

    return $"ERROR: 不明な type '{type}'（scene / assets のいずれかを指定）";
}

/// <summary>
/// seed_batch: operations 配列の操作を順番に実行し、各操作の成否を返す。
///
/// 【actor_dfs_id の自動補完】
/// add_component / set_value / move_actor で actor_dfs_id が省略されている場合、
/// バッチ内で直前に追加されたアクターの DFS ID を自動的に補完する。
/// DFS ID はバッチ開始前のアクター数 + バッチ内で追加された順番で決まる。
/// これにより、モデルが DFS ID の予測を誤って省略しても操作が成功する。
///
/// 【シーン状態の非返却】
/// 実行後のシーン状態は意図的に返さない。
/// 自動返却するとモデルが「小さいバッチを何度も呼んで中間状態を確認する」
/// 逐次実行パターンを強化してしまう。シーン状態が必要なら seed_query を呼ぶこと。
/// </summary>
static async Task<string> ExecBatchAsync(JsonElement args, HttpClient http)
{
    if (args.ValueKind == JsonValueKind.Undefined
        || !args.TryGetProperty("operations", out var opsEl)
        || opsEl.ValueKind != JsonValueKind.Array)
        return "ERROR: 'operations' 配列が必要です。";

    // バッチ開始前のアクター数を取得する（DFS ID 自動補完の起点）。
    // DFS ID = initialActorCount + (バッチ内で追加済みの add_actor 数 - 1)
    int initialActorCount  = await FetchActorCountAsync(http);
    int actorsAddedInBatch = 0;

    var sb      = new StringBuilder();
    int i       = 0;
    int success = 0;
    int failure = 0;

    foreach (var op in opsEl.EnumerateArray())
    {
        i++;
        var cmdName = op.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";

        // add_component / set_value / move_actor で actor_dfs_id が省略されており、
        // かつバッチ内でアクターが追加済みの場合は、直前の add_actor の DFS ID を補完する。
        string opJson;
        if ((cmdName is "add_component" or "set_value" or "move_actor")
            && !op.TryGetProperty("actor_dfs_id", out _)
            && actorsAddedInBatch > 0)
        {
            int predictedId = initialActorCount + actorsAddedInBatch - 1;
            opJson = InjectActorDfsId(op, predictedId);
            sb.AppendLine($"[Op {i}] [自動補完] actor_dfs_id={predictedId}");
        }
        else
        {
            opJson = op.GetRawText();
        }

        var content = new StringContent(opJson, Encoding.UTF8, "application/json");
        try
        {
            var resp   = await http.PostAsync($"{API_BASE}/cmd", content);
            var result = await resp.Content.ReadAsStringAsync();
            sb.AppendLine($"[Op {i}] {result.TrimEnd()}");
            if (result.TrimStart().StartsWith("ERROR", StringComparison.Ordinal))
                failure++;
            else
            {
                success++;
                // add_actor が成功したらカウンタを更新する
                if (cmdName == "add_actor")
                    actorsAddedInBatch++;
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[Op {i}] ERROR: {ex.Message}");
            failure++;
        }
    }

    // サマリー行（成功/失敗カウントのみ。シーン状態は意図的に含めない）
    sb.AppendLine();
    sb.AppendLine($"=== Batch complete: {success} succeeded, {failure} failed, {i} total ===");

    return sb.Length > 0 ? sb.ToString() : "操作なし";
}

/// <summary>
/// 現在シーンのアクター数を取得する。
/// DFS ID 自動補完の起点として使用する。取得失敗時は 0 を返す。
/// </summary>
static async Task<int> FetchActorCountAsync(HttpClient http)
{
    try
    {
        var resp = await http.GetAsync($"{API_BASE}/scene");
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            return doc.RootElement.GetArrayLength();
    }
    catch { }
    return 0;
}

/// <summary>
/// JsonElement の操作 JSON に actor_dfs_id フィールドを追加して返す。
/// 既存フィールドをすべてコピーし、末尾に actor_dfs_id を追記する。
/// </summary>
static string InjectActorDfsId(JsonElement op, int dfsId)
{
    using var mem    = new MemoryStream();
    using var writer = new Utf8JsonWriter(mem);
    writer.WriteStartObject();
    foreach (var prop in op.EnumerateObject())
        prop.WriteTo(writer);
    writer.WriteNumber("actor_dfs_id", dfsId);
    writer.WriteEndObject();
    writer.Flush();
    return Encoding.UTF8.GetString(mem.ToArray());
}

// ── ツール定義 ────────────────────────────────────────────────────────────────

/// <summary>MCP tools/list レスポンス用のツール定義配列を返す。</summary>
static object[] BuildToolList() => new[] { SeedQueryTool(), SeedBatchTool() };

static object SeedQueryTool() => new
{
    name        = "seed_query",
    description =
        "SEED エディタに現在のシーン情報またはアセットファイル一覧を問い合わせる。" +
        "編集前に必ず呼び出してシーン状態・DFS ID・アセットパスを把握すること。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            type = new
            {
                type        = "string",
                @enum       = new[] { "scene", "assets" },
                description = "scene: アクター/コンポーネント/DFS ID を取得。assets: アセットファイルの絶対パスを取得。"
            },
            dir = new
            {
                type        = "string",
                description = "assets 取得時のサブディレクトリ絞り込み（例: 'models', 'scripts'）。省略時は全ファイル。"
            }
        },
        required = new[] { "type" }
    }
};

static object SeedBatchTool() => new
{
    name        = "seed_batch",
    description =
        "SEED エディタのシーン操作を一括実行する唯一の手段。" +
        "事前に seed_query でシーン状態とアセットパスを確認し、" +
        "タスクに必要な全操作をこの 1 回の呼び出しにまとめること。" +
        "操作は配列順に逐次実行され、各操作の成否が返される。" +
        "実行後のシーン状態は返されない（必要なら seed_query で明示的に取得）。" +
        "DFS ID は追加順に 0, 1, 2... と割り振られるため事前予測可能。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            operations = new
            {
                type        = "array",
                description = "実行する操作の配列（配列順に実行される）",
                items       = new
                {
                    type                 = "object",
                    required             = new[] { "cmd" },
                    additionalProperties = true,
                    properties           = new
                    {
                        cmd = new
                        {
                            type        = "string",
                            @enum       = new[]
                            {
                                "add_actor", "move_actor", "add_component",
                                "set_value", "remove_actor", "write_asset_file"
                            },
                            description = "コマンド名"
                        },
                        name           = new { type = "string",  description = "add_actor: アクター名" },
                        x              = new { type = "number",  description = "add_actor / move_actor: X 座標" },
                        y              = new { type = "number",  description = "add_actor / move_actor: Y 座標" },
                        z              = new { type = "number",  description = "add_actor / move_actor: Z 座標" },
                        actor_dfs_id   = new { type = "integer", description = "操作対象アクターの DFS ID（seed_query で確認）" },
                        component_type = new { type = "string",  description = "add_component: コンポーネント型名（Model / Camera / Sprite 等）" },
                        slot_idx       = new { type = "integer", description = "set_value: スロットインデックス（0-based、seed_query の slot_idx で確認）" },
                        key            = new { type = "string",  description = "set_value: キー名（model_path / fov / color 等）" },
                        value          = new { type = "string",  description = "set_value: 値（数値も文字列で渡す：\"45.0\"、bool は \"true\"/\"false\"）" },
                        relative_path  = new { type = "string",  description = "write_asset_file: assets/ からの相対パス" },
                        content        = new { type = "string",  description = "write_asset_file: ファイル内容" }
                    }
                }
            }
        },
        required = new[] { "operations" }
    }
};

// ── JSON-RPC 2.0 ヘルパー ─────────────────────────────────────────────────────

/// <summary>
/// 正常レスポンスを JSON 文字列として組み立てる。
/// id フィールドは受信した JSON 値をそのまま埋め込む（数値・文字列・null を保持）。
/// </summary>
static string Reply(JsonElement id, object result)
{
    var opts       = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    var resultJson = JsonSerializer.Serialize(result, opts);
    return $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{resultJson}}}";
}

/// <summary>エラーレスポンスを JSON 文字列として組み立てる。</summary>
static string ReplyError(JsonElement id, int code, string message)
{
    var errorJson = JsonSerializer.Serialize(new { code, message });
    return $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"error\":{errorJson}}}";
}
