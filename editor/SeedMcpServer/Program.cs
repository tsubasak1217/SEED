// ============================================================
//  SeedMcpServer — SEED エディタ MCP (Model Context Protocol) サーバー
//
//  Claude Code / Gemini CLI にエディタ操作をネイティブツールとして公開する。
//  stdin/stdout で JSON-RPC 2.0（改行区切り）を話し、
//  ツール呼び出しは SeedAIBridge HTTP API（http://localhost:7234/seed-ai/）へ転送する。
//
//  【公開ツール】
//   ■ シーン編集（従来）
//     seed_query(type, dir?)         → GET シーン情報またはアセット一覧
//     seed_batch(operations: [...])  → POST 操作を一括実行（一括変更の手段）
//   ■ 目視確認・アニメ編集（追加）
//     seed_screenshot(target, path?) → 画面キャプチャを画像として返す
//     seed_state()                   → エディタ状態（Edit/Play/Pause・シーン・選択）
//     seed_hierarchy()               → ヒエラルキーツリー
//     seed_select(actor_dfs_id|name) → アクター選択＋コンポーネント情報
//     seed_play(action, wait_seconds?)→ 再生制御
//     seed_anim_preview(...)         → .anim の指定時刻プレビュー
//     seed_anim_preview_stop(...)    → プレビュー解除
//     seed_anim_reload(clip_path)    → .anim キャッシュ破棄（書き換え後に必須）
//     seed_log(lines?)               → エディタログ末尾（ランタイム stderr 込み）
//     seed_save_scene()              → シーン保存（Ctrl+S 相当）
//     seed_send_ipc(command)         → 生 IPC 送信（低レベルの逃げ道）
//
//  【なぜ編集系は seed_batch に集約しているか】
//    エージェントはツール呼び出しごとに API コールを 1 回消費する。
//    編集操作を個別に呼ぶと 10～20+ コール/タスクになり RPM 制限に達する。
//    seed_batch で全操作をまとめることで 3～4 コール/タスクに固定できる。
//    一方、目視確認系（screenshot / state / log）は「1 回呼んで結果を見る」性質のため
//    個別ツールとして公開する方が自然で、まとめる利点がない。
// ============================================================

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

// ── 設定 ─────────────────────────────────────────────────────────────────────

/// <summary>SeedAIBridge（エディタ内 HTTP サーバー）のベース URL。</summary>
const string API_BASE = "http://localhost:7234/seed-ai";

/// <summary>
/// HTTP のタイムアウト（秒）。
/// seed_play("play") はスクリプト再コンパイルを挟むため長くかかりうるので、
/// エディタ側の待ち上限（60 秒）＋余裕を取る。
/// </summary>
const int HTTP_TIMEOUT_SECONDS = 120;

/// <summary>MCP の image コンテンツとして返せる PNG の上限バイト数。超過時はパスのみ返す。</summary>
const int MAX_INLINE_IMAGE_BYTES = 8 * 1024 * 1024;

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SECONDS) };

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
            serverInfo      = new { name = "seed-mcp", version = "1.1.0" },
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
        // スクリーンショットだけは画像コンテンツを返すため専用経路
        if (name == "seed_screenshot")
            return await HandleScreenshotAsync(id, args, http);

        var result = name switch
        {
            "seed_query"             => await ExecQueryAsync(args, http),
            "seed_batch"             => await ExecBatchAsync(args, http),

            // 目視確認・アニメ編集系: いずれも /seed-ai/cmd への単発 POST
            "seed_state"             => await PostCmdAsync(http, "get_editor_state",  args),
            "seed_hierarchy"         => await PostCmdAsync(http, "get_hierarchy",     args),
            "seed_select"            => await PostCmdAsync(http, "select_actor",      args),
            "seed_play"              => await PostCmdAsync(http, "play_control",      args),
            "seed_anim_preview"      => await PostCmdAsync(http, "anim_preview",      args),
            "seed_anim_preview_stop" => await PostCmdAsync(http, "anim_preview_stop", args),
            "seed_anim_reload"       => await PostCmdAsync(http, "anim_reload",       args),
            "seed_log"               => await PostCmdAsync(http, "get_log",           args),
            "seed_save_scene"        => await PostCmdAsync(http, "save_scene",        args),
            "seed_send_ipc"          => await PostCmdAsync(http, "send_ipc",          args),

            _ => $"ERROR: 不明なツール '{name}'"
        };

        return Reply(id, new
        {
            content = new[] { new { type = "text", text = result } },
            isError = IsErrorResult(result)
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
            if (IsErrorResult(result))
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
/// seed_screenshot: エディタにキャプチャを依頼し、PNG を MCP の image コンテンツとして返す。
/// 併せてファイルパスもテキストで返すので、後続の編集で参照できる。
/// </summary>
static async Task<string> HandleScreenshotAsync(JsonElement id, JsonElement args, HttpClient http)
{
    var raw = await PostCmdAsync(http, "screenshot", args);

    // エディタ側は {"ok":true,"path":...} 形式の JSON を返す。
    // 到達できない・失敗した場合はテキストのみ返してエラーとする。
    string? path = null;
    try
    {
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("ok", out var okEl)
            && okEl.ValueKind == JsonValueKind.True
            && doc.RootElement.TryGetProperty("path", out var pathEl))
            path = pathEl.GetString();
    }
    catch { /* JSON でない = エラーメッセージ。下の分岐でテキスト返却する */ }

    if (path is null || !File.Exists(path))
    {
        return Reply(id, new
        {
            content = new[] { new { type = "text", text = raw } },
            isError = true
        });
    }

    var bytes = await File.ReadAllBytesAsync(path);
    if (bytes.Length > MAX_INLINE_IMAGE_BYTES)
    {
        // 巨大画像をそのまま埋め込むとコンテキストを食い潰すため、パスのみ返す
        return Reply(id, new
        {
            content = new[]
            {
                new { type = "text", text = $"{raw}\n（画像が {bytes.Length} バイトと大きいため埋め込みませんでした。上記 path を直接読んでください）" }
            },
            isError = false
        });
    }

    // image と text を両方返す: 画像は目視確認用、テキストはパス・サイズ・警告の確認用
    var imageContent = new
    {
        type     = "image",
        data     = Convert.ToBase64String(bytes),
        mimeType = "image/png",
    };
    var textContent = new { type = "text", text = raw };

    return Reply(id, new
    {
        content = new object[] { imageContent, textContent },
        isError = false
    });
}

/// <summary>
/// エディタの POST /seed-ai/cmd を 1 回叩く。
/// MCP ツールの引数オブジェクトへ "cmd" フィールドを足したものをそのまま本文にする。
/// </summary>
static async Task<string> PostCmdAsync(HttpClient http, string cmd, JsonElement args)
{
    var body = BuildCmdBody(cmd, args);
    try
    {
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp    = await http.PostAsync($"{API_BASE}/cmd", content);
        return await resp.Content.ReadAsStringAsync();
    }
    catch (Exception ex)
    {
        return $"ERROR: SEED エディタへ接続できません（{API_BASE}）。"
             + $"エディタが起動しているか確認してください。詳細: {ex.Message}";
    }
}

/// <summary>
/// { "cmd": "...", ...引数... } 形式のリクエスト本文を組み立てる。
/// 引数が未指定（ValueKind = Undefined / Null）でも cmd だけの本文を返す。
/// </summary>
static string BuildCmdBody(string cmd, JsonElement args)
{
    using var mem    = new MemoryStream();
    using var writer = new Utf8JsonWriter(mem);
    writer.WriteStartObject();
    writer.WriteString("cmd", cmd);
    if (args.ValueKind == JsonValueKind.Object)
    {
        foreach (var prop in args.EnumerateObject())
        {
            // 呼び出し側が誤って cmd を渡してきても上書きさせない
            if (prop.NameEquals("cmd")) continue;
            prop.WriteTo(writer);
        }
    }
    writer.WriteEndObject();
    writer.Flush();
    return Encoding.UTF8.GetString(mem.ToArray());
}

/// <summary>
/// エディタからの応答がエラーかどうかを判定する。
/// 追加コマンドは {"ok":false,...} の JSON、既存コマンドとネットワーク失敗は
/// "ERROR"/"エラー" で始まるテキストを返すため、両方を見る。
/// </summary>
static bool IsErrorResult(string result)
{
    var trimmed = result.TrimStart();
    if (trimmed.StartsWith("ERROR", StringComparison.Ordinal)) return true;
    if (trimmed.StartsWith("エラー", StringComparison.Ordinal)) return true;

    if (trimmed.StartsWith("{", StringComparison.Ordinal))
    {
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty("ok", out var ok))
                return ok.ValueKind == JsonValueKind.False;
        }
        catch { /* JSON として読めないならエラー扱いしない */ }
    }
    return false;
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
static object[] BuildToolList() => new[]
{
    SeedQueryTool(),
    SeedBatchTool(),
    SeedStateTool(),
    SeedHierarchyTool(),
    SeedSelectTool(),
    SeedScreenshotTool(),
    SeedPlayTool(),
    SeedAnimPreviewTool(),
    SeedAnimPreviewStopTool(),
    SeedAnimReloadTool(),
    SeedLogTool(),
    SeedSaveSceneTool(),
    SeedSendIpcTool(),
};

/// <summary>引数を取らないツールの共通スキーマ。</summary>
static object EmptySchema() => new { type = "object", properties = new { } };

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
        "SEED エディタのシーン編集を一括実行する。" +
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
                                "set_value", "remove_actor", "write_asset_file",
                                // 目視確認ループで一括実行したくなる変更系も許可する
                                "anim_reload", "anim_preview", "anim_preview_stop",
                                "select_actor", "play_control", "save_scene", "send_ipc"
                            },
                            description = "コマンド名"
                        },
                        name           = new { type = "string",  description = "add_actor: アクター名 / アニメ系: 対象アクター名" },
                        x              = new { type = "number",  description = "add_actor / move_actor: X 座標" },
                        y              = new { type = "number",  description = "add_actor / move_actor: Y 座標" },
                        z              = new { type = "number",  description = "add_actor / move_actor: Z 座標" },
                        actor_dfs_id   = new { type = "integer", description = "操作対象アクターの DFS ID（seed_query で確認）" },
                        component_type = new { type = "string",  description = "add_component: コンポーネント型名（Model / Camera / Sprite 等）" },
                        slot_idx       = new { type = "integer", description = "set_value: スロットインデックス（0-based、seed_query の slot_idx で確認）" },
                        key            = new { type = "string",  description = "set_value: キー名（model_path / fov / color 等）" },
                        value          = new { type = "string",  description = "set_value: 値（数値も文字列で渡す：\"45.0\"、bool は \"true\"/\"false\"）" },
                        relative_path  = new { type = "string",  description = "write_asset_file: assets/ からの相対パス" },
                        content        = new { type = "string",  description = "write_asset_file: ファイル内容" },
                        clip_path      = new { type = "string",  description = "anim_preview / anim_reload: .anim のパス（絶対 or seed://）" },
                        time           = new { type = "number",  description = "anim_preview: プレビュー時刻（秒）" },
                        action         = new { type = "string",  description = "play_control: play / pause / resume / stop" },
                        command        = new { type = "string",  description = "send_ipc: 生 IPC 文字列" }
                    }
                }
            }
        },
        required = new[] { "operations" }
    }
};

static object SeedStateTool() => new
{
    name        = "seed_state",
    description =
        "エディタの状態スナップショットを返す（Edit/Play/Pause、現在のシーンパス、選択中アクターの DFS ID、"
      + "ランタイム接続状態、アクター数、アセットパス）。安価なので状況が不明なときは最初にこれを呼ぶ。",
    inputSchema = EmptySchema()
};

static object SeedHierarchyTool() => new
{
    name        = "seed_hierarchy",
    description =
        "現在のヒエラルキーツリーを JSON で返す（id = DFS ID、name、parent、is_2d、is_vp、active、is_folder、is_prefab）。"
      + "seed_query(type=\"scene\") より軽量で、名前から DFS ID を引くのに使う。",
    inputSchema = EmptySchema()
};

static object SeedSelectTool() => new
{
    name        = "seed_select",
    description =
        "アクターを選択し（ヒエラルキーをクリックしたのと同じ経路）、そのコンポーネント情報 JSON を返す。"
      + "actor_dfs_id か name のどちらかを指定する。エディタのインスペクタ表示も追従する。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            actor_dfs_id = new { type = "integer", description = "選択するアクターの DFS ID" },
            name         = new { type = "string",  description = "アクター名（DFS ID が不明なとき）" }
        }
    }
};

static object SeedScreenshotTool() => new
{
    name        = "seed_screenshot",
    description =
        "現在画面に出ている内容を PNG でキャプチャし、画像として返す（同時にファイルへも保存する）。"
      + "target=\"viewport\": シーンビュー、\"game\": Play 中のゲーム画面、\"editor\": エディタウィンドウ全体。"
      + "変更の結果を推測せず目視確認するために使う。"
      + "【制約】画面に映っているものを撮る方式のため、エディタウィンドウが最小化・他ウィンドウで隠れていると正しく撮れない。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            target = new
            {
                type        = "string",
                @enum       = new[] { "viewport", "game", "editor" },
                description = "撮影対象。省略時は viewport。"
            },
            path = new
            {
                type        = "string",
                description = "出力 PNG の絶対パス。省略時は OS のテンポラリ配下へ自動命名で保存する。"
            }
        }
    }
};

static object SeedPlayTool() => new
{
    name        = "seed_play",
    description =
        "エディタのプレイバーを操作する。action=\"play\"（Edit 中のみ）/ \"pause\"（Play 中のみ）/ "
      + "\"resume\"（Pause 中のみ）/ \"stop\"（Play・Pause 中のみ）。"
      + "wait_seconds を指定すると遷移後にその秒数だけ待ってから返るので、"
      + "直後の seed_screenshot でゲームが進んだ状態を撮れる。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            action = new
            {
                type        = "string",
                @enum       = new[] { "play", "pause", "resume", "stop" },
                description = "実行する再生操作"
            },
            wait_seconds = new
            {
                type        = "number",
                description = "遷移後に待つ秒数（0〜20）。ゲームを少し進めてから撮りたいときに使う。"
            }
        },
        required = new[] { "action" }
    }
};

static object SeedAnimPreviewTool() => new
{
    name        = "seed_anim_preview",
    description =
        "Edit モードで .anim クリップの指定時刻を対象アクターへ適用する（アニメーションタイムラインの"
      + "スクラブと同じ）。適用後に seed_screenshot でポーズを確認する。"
      + ".anim を書き換えた直後は先に seed_anim_reload を呼ぶこと。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            actor_dfs_id = new { type = "integer", description = "対象アクターの DFS ID" },
            name         = new { type = "string",  description = "対象アクター名（DFS ID の代わり）" },
            clip_path    = new { type = "string",  description = ".anim の絶対パスまたは seed:// 仮想パス" },
            time         = new { type = "number",  description = "プレビューする時刻（秒）" }
        },
        required = new[] { "clip_path", "time" }
    }
};

static object SeedAnimPreviewStopTool() => new
{
    name        = "seed_anim_preview_stop",
    description = "アニメーションプレビューを終了し、プレビュー前の値へ復元する。プレビュー後は必ず呼ぶこと。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            actor_dfs_id = new { type = "integer", description = "対象アクターの DFS ID" },
            name         = new { type = "string",  description = "対象アクター名（DFS ID の代わり）" }
        }
    }
};

static object SeedAnimReloadTool() => new
{
    name        = "seed_anim_reload",
    description =
        "ランタイムが持つ .anim のロード済みキャッシュを破棄し、次の seed_anim_preview でディスクから読み直させる。"
      + "seed_batch の write_asset_file で .anim を書き換えた後は必須。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            clip_path = new { type = "string", description = ".anim の絶対パスまたは seed:// 仮想パス" }
        },
        required = new[] { "clip_path" }
    }
};

static object SeedLogTool() => new
{
    name        = "seed_log",
    description =
        "editor/logs/SEEDEditor.log の末尾 N 行を返す。ランタイムの stderr も \"[STDERR] \" 付きで"
      + "同じファイルへ入るため、LOAD_ERROR やスクリプト例外もここで確認できる。"
      + "期待した見た目にならなかったときに呼ぶ。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            lines = new { type = "integer", description = "取得する末尾行数（1〜5000、省略時 200）" }
        }
    }
};

static object SeedSaveSceneTool() => new
{
    name        = "seed_save_scene",
    description = "現在のシーンを保存する（Ctrl+S 相当）。Edit 状態でのみ実行でき、保存完了通知まで待つ。",
    inputSchema = EmptySchema()
};

static object SeedSendIpcTool() => new
{
    name        = "seed_send_ipc",
    description =
        "【低レベル】ランタイムへ生の IPC 文字列を送る逃げ道。応答は待たず、検証もしない。"
      + "専用ツールがある操作はそちらを使い、これはツール化されていないコマンドにだけ使うこと。"
      + "実行結果は seed_log / seed_screenshot で別途確認する。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            command = new { type = "string", description = "ランタイムへ送る IPC 文字列（例: \"ANIM_RELOAD:seed://animations/foo.anim\"）" }
        },
        required = new[] { "command" }
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
