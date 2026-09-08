// ============================================================
//  SeedMcpServer — SEED エディタ MCP (Model Context Protocol) サーバー
//
//  Claude Code / Gemini CLI にエディタ操作をネイティブツールとして公開する。
//  stdin/stdout で JSON-RPC 2.0（改行区切り）を話し、
//  ツール呼び出しは SeedAIBridge HTTP API へ転送する。
//  **転送先は固定ポートではない**。seed_launch が起動した（または seed_attach で明示的に
//  接続した）インスタンスのポートとトークンへだけ送る。束縛が無い状態では変更系ツールを
//  一切実行しない（詳細は SeedInstance.cs と docs/editor_mcp.md のポストモーテム節）。
//
//  【公開ツール】
//   ■ シーン編集（従来）
//     seed_query(type, dir?)         → GET シーン情報またはアセット一覧
//     seed_batch(operations: [...])  → POST 操作を一括実行（一括変更の手段）
//   ■ ヘッドレス運用（インスタンスの束縛・起動・終了）
//     seed_launch(headless?, scene?, wait_seconds?) → 空きポート＋トークンでエディタを起動し束縛
//     seed_attach(port, token)       → 利用者が明示的に許可したエディタへ接続
//     seed_instance()                → 現在の束縛状態
//     seed_shutdown()                → 束縛中のエディタを正常終了
//   ■ 目視確認・アニメ編集（追加）
//     seed_screenshot(target, method?, path?) → キャプチャを画像として返す（既定は GPU 読み戻し）
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

// SeedAIBridge（エディタ内 HTTP サーバー）のベース URL は固定値ではない。
// 「この MCP サーバーが seed_launch で起動した（あるいは seed_attach で明示的に
//  繋いだ）インスタンス」のポートを SeedInstance が保持しており、そこから解決する。
// かつてここが 7234 固定だったために、利用者のエディタを誤って操作する事故が起きた。

/// <summary>
/// HTTP のタイムアウト（秒）。
/// seed_play("play") はスクリプト再コンパイルを挟むため長くかかりうるので、
/// エディタ側の待ち上限（60 秒）＋余裕を取る。
/// </summary>
const int HTTP_TIMEOUT_SECONDS = 120;

/// <summary>MCP の image コンテンツとして返せる PNG の上限バイト数。超過時はパスのみ返す。</summary>
const int MAX_INLINE_IMAGE_BYTES = 8 * 1024 * 1024;

/// <summary>seed_screenshot の method: ランタイムの GPU 読み戻し（既定）。</summary>
const string SCREENSHOT_METHOD_GPU = "gpu";

/// <summary>seed_screenshot の method: 画面 DC からの BitBlt（従来方式）。</summary>
const string SCREENSHOT_METHOD_SCREEN = "screen";

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SECONDS) };

// stderr をログ用に使う（stdout は JSON-RPC 専用で汚してはいけない）
Console.Error.WriteLine("[SeedMcpServer] 起動 — 操作対象は seed_launch / seed_attach で束縛します（既定では未束縛）。");

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
        // ── インスタンス束縛のゲート ─────────────────────────────
        // seed_launch / seed_attach でこの MCP サーバーが操作対象を束縛するまで、
        // 変更系ツールは一切実行しない。既定ポートに居る「利用者のエディタ」へ
        // 暗黙に接続してしまう事故（ポストモーテム参照）を構造的に防ぐ。
        var denial = SeedMcpServer.SeedInstance.CheckToolAllowed(name);
        if (denial is not null)
        {
            return Reply(id, new
            {
                content = new[] { new { type = "text", text = denial } },
                isError = true
            });
        }

        // スクリーンショットだけは画像コンテンツを返すため専用経路
        if (name == "seed_screenshot")
            return await HandleScreenshotAsync(id, args, http);

        var result = name switch
        {
            // ヘッドレス運用: エディタの起動・終了・接続
            "seed_launch"            => await HandleLaunchAsync(args),
            "seed_attach"            => await HandleAttachAsync(args),
            "seed_instance"          => SeedMcpServer.SeedInstance.ToJson(),
            "seed_shutdown"          => await HandleShutdownAsync(http, args),

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

            // セーブデータ（SEED.SaveData）の読み書き: 実行中ランタイムのストアを直接触る
            "seed_save_get"          => await PostCmdWithOpAsync(http, "save_data", args, "get"),
            "seed_save_set"          => await PostCmdWithOpAsync(http, "save_data", args, "set"),
            "seed_save_delete"       => await PostCmdWithOpAsync(http, "save_data", args, "delete"),
            "seed_save_flush"        => await PostCmdWithOpAsync(http, "save_data", args, "save"),

            // アクタを名前／パスで引いて DFS ID と構成を返す（観測系）
            "seed_find_actor"        => await PostCmdAsync(http, "find_actor",       args),

            // 高レベル入力ラッパ: キー列の打鍵・クリックを 1 コールで撃つ
            "seed_input"             => await ExecInputAsync(args, http),

            // ゲーム入力の注入: エディタ側が INPUT_* IPC を送り、1 行応答まで待って返す
            "game_input_key"          => await PostCmdAsync(http, "game_input_key",         args),
            "game_input_mouse"        => await PostCmdAsync(http, "game_input_mouse",       args),
            "game_input_sequence"     => await PostCmdAsync(http, "game_input_sequence",    args),
            "game_input_release_all"  => await PostCmdAsync(http, "game_input_release_all", args),

            // プロファイラ一発計測: 応答 JSON を要約表へ整形して返す専用経路
            "seed_profile"           => await HandleProfileAsync(http, args),

            // 図鑑（魚カタログ）画像の一括生成: 変更系なので束縛済みインスタンスが必須
            "seed_generate_fish_thumbnails" => await PostCmdAsync(http, "generate_fish_thumbnails", args),

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
        return await GetWithTokenAsync(http, $"{SeedMcpServer.SeedInstance.ApiBase}/scene");
    }

    if (type == "assets")
    {
        var dir = args.TryGetProperty("dir", out var d) ? d.GetString() ?? "" : "";
        var apiBase = SeedMcpServer.SeedInstance.ApiBase;
        var url = string.IsNullOrEmpty(dir)
            ? $"{apiBase}/assets"
            : $"{apiBase}/assets?dir={Uri.EscapeDataString(dir)}";
        return await GetWithTokenAsync(http, url);
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

        try
        {
            var result = await PostRawAsync(http, opJson);
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
    // method="gpu"（既定）: ランタイムの GPU 読み戻し。ウィンドウが隠れていても撮れる。
    // method="screen"    : 従来の画面 DC BitBlt。エディタ UI 全体（target="editor"）はこちらのみ。
    var method = args.ValueKind == JsonValueKind.Object
              && args.TryGetProperty("method", out var mEl) ? mEl.GetString() : null;
    method ??= SCREENSHOT_METHOD_GPU;

    // target="editor" は GPU 読み戻しでは撮れない（ランタイムの提示画像しか持っていない）ため、
    // 黙って失敗させず画面キャプチャへフォールバックする。
    var target = args.ValueKind == JsonValueKind.Object
              && args.TryGetProperty("target", out var tEl) ? tEl.GetString() : null;
    if (target == "editor") method = SCREENSHOT_METHOD_SCREEN;

    var cmd = method == SCREENSHOT_METHOD_SCREEN ? "screenshot" : "screenshot_gpu";
    var raw = await PostCmdAsync(http, cmd, args);

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
/// seed_launch: SEED エディタを（既定でヘッドレスに）起動し、AI ブリッジが応答するまで待つ。
/// すでに起動していれば何もせず現在の状態を返す。
/// </summary>
static async Task<string> HandleLaunchAsync(JsonElement args)
{
    var headless = true;
    string? scene = null;
    var waitSeconds = SeedMcpServer.Launcher.DEFAULT_WAIT_SECONDS;

    if (args.ValueKind == JsonValueKind.Object)
    {
        if (args.TryGetProperty("headless", out var hEl)
            && (hEl.ValueKind == JsonValueKind.True || hEl.ValueKind == JsonValueKind.False))
            headless = hEl.GetBoolean();

        if (args.TryGetProperty("scene", out var sEl) && sEl.ValueKind == JsonValueKind.String)
            scene = sEl.GetString();

        if (args.TryGetProperty("wait_seconds", out var wEl) && wEl.ValueKind == JsonValueKind.Number)
            waitSeconds = wEl.GetDouble();
    }

    return await SeedMcpServer.Launcher.LaunchAsync(headless, scene, waitSeconds);
}

/// <summary>
/// seed_attach: 利用者が開いているエディタへ、明示的な同意（ポート＋トークン）で接続する。
///
/// トークンはエディタの「編集 → 環境設定」に表示される。利用者がそれを渡したときにだけ
/// 成立するので、AI が勝手に対話エディタへ繋ぐことはできない。
/// </summary>
static async Task<string> HandleAttachAsync(JsonElement args)
{
    if (args.ValueKind != JsonValueKind.Object
        || !args.TryGetProperty("port", out var portEl)
        || portEl.ValueKind != JsonValueKind.Number)
    {
        return "ERROR: port（数値）が必要です。エディタの「編集 → 環境設定」に表示されています。";
    }
    if (!args.TryGetProperty("token", out var tokenEl)
        || tokenEl.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(tokenEl.GetString()))
    {
        return "ERROR: token（文字列）が必要です。エディタの「編集 → 環境設定」に表示されています。";
    }

    return await SeedMcpServer.Launcher.AttachAsync(portEl.GetInt32(), tokenEl.GetString()!);
}

/// <summary>
/// seed_shutdown: 束縛中のインスタンスを終了させ、束縛を解除する。
///
/// エディタ側も「ヘッドレス、または利用者が AI 操作を許可したインスタンス」でなければ
/// shutdown を拒否する。ここで束縛を解除するのは、終了済みの相手へ以後の
/// コマンドを投げ続けないため。
/// </summary>
static async Task<string> HandleShutdownAsync(HttpClient http, JsonElement args)
{
    var result = await PostCmdAsync(http, "shutdown", args);
    if (!IsErrorResult(result)) SeedMcpServer.SeedInstance.Clear();
    return result;
}

/// <summary>
/// 束縛中インスタンスのトークンを載せて GET する。
/// トークンが無い（未束縛での観測系）場合はヘッダーを付けない。
/// </summary>
static async Task<string> GetWithTokenAsync(HttpClient http, string url)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    AttachToken(req);
    var resp = await http.SendAsync(req);
    return await resp.Content.ReadAsStringAsync();
}

/// <summary>束縛中インスタンスのトークンを載せて POST /cmd する（本文はそのまま送る）。</summary>
static async Task<string> PostRawAsync(HttpClient http, string body)
{
    using var req = new HttpRequestMessage(
        HttpMethod.Post, $"{SeedMcpServer.SeedInstance.ApiBase}/cmd")
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
    AttachToken(req);
    var resp = await http.SendAsync(req);
    return await resp.Content.ReadAsStringAsync();
}

/// <summary>束縛中インスタンスのトークンをリクエストヘッダーへ付ける。</summary>
static void AttachToken(HttpRequestMessage req)
{
    var token = SeedMcpServer.SeedInstance.Token;
    if (!string.IsNullOrEmpty(token))
        req.Headers.Add(SeedMcpServer.SeedInstance.TOKEN_HEADER, token);
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
        return await PostRawAsync(http, body);
    }
    catch (Exception ex)
    {
        return $"ERROR: SEED エディタへ接続できません（{SeedMcpServer.SeedInstance.ApiBase}）。"
             + "エディタが起動していません。seed_launch を呼んで起動してください"
             + "（自動起動はしません）。"
             + $"詳細: {ex.Message}";
    }
}

/// <summary>
/// 引数へ <c>op</c> を足してから単発 POST する（seed_save_* → save_data の橋渡し）。
///
/// MCP のツール名を <c>seed_save_get</c> / <c>seed_save_set</c> のように分けておくと
/// AI 側が用途を取り違えにくい一方、エディタ側は 1 コマンド（save_data）で済ませたい。
/// その差を埋めるだけの薄いアダプタ。呼び出し側が <c>op</c> を明示していても上書きする。
/// </summary>
/// <param name="http">HTTP クライアント。</param>
/// <param name="cmd">エディタ側のコマンド名（save_data）。</param>
/// <param name="args">ツール引数。</param>
/// <param name="op">強制する op（get / set / delete / save）。</param>
static async Task<string> PostCmdWithOpAsync(HttpClient http, string cmd, JsonElement args, string op)
{
    using var mem    = new MemoryStream();
    using (var writer = new Utf8JsonWriter(mem))
    {
        writer.WriteStartObject();
        writer.WriteString("op", op);
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in args.EnumerateObject())
            {
                if (prop.NameEquals("op")) continue;   // 呼び出し側の op は無視する
                prop.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
    }
    using var doc = JsonDocument.Parse(mem.ToArray());
    return await PostCmdAsync(http, cmd, doc.RootElement);
}

/// <summary>seed_input: 打鍵 1 回の押しっぱなし時間の既定値（ミリ秒）。</summary>
const int InputDefaultHoldMs = 80;

/// <summary>seed_input: 1 コールで撃てる操作数の上限（暴走したシーケンスで固まらないため）。</summary>
const int InputMaxSteps = 32;

/// <summary>
/// seed_input: キー列の打鍵とクリックを 1 コールで撃つ高レベルラッパ。
///
/// <para>
/// <c>keys</c>（文字列配列）は「押す → hold_ms 待つ → 離す」を順に実行する。
/// <c>click</c>（{x, y, button?}）はカーソルを移動してから押して離す。
/// どちらも既存の <c>game_input_key</c> / <c>game_input_mouse</c> をそのまま使うので、
/// 安全機構（Play 中のみ・変更系判定）は完全に同じ経路を通る。
/// </para>
/// <para>
/// 時間精度が要る（拍に合わせる等）操作は <c>game_input_sequence</c> を使うこと。
/// こちらは「メニューを 3 つ進めて決定」のような手数の削減が目的。
/// </para>
/// </summary>
/// <param name="args">ツール引数（keys / click / hold_ms）。</param>
/// <param name="http">HTTP クライアント。</param>
static async Task<string> ExecInputAsync(JsonElement args, HttpClient http)
{
    if (args.ValueKind != JsonValueKind.Object)
        return "ERROR: 引数が必要です（keys もしくは click）。";

    var holdMs = args.TryGetProperty("hold_ms", out var h) && h.TryGetInt32(out var hv)
        ? Math.Clamp(hv, 0, 5000)
        : InputDefaultHoldMs;

    var results = new List<string>();

    // ── キー列: 1 つずつ押して離す ──
    if (args.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
    {
        if (keys.GetArrayLength() > InputMaxSteps)
            return $"ERROR: keys が多すぎます（上限 {InputMaxSteps}）。分割して呼んでください。";

        foreach (var k in keys.EnumerateArray())
        {
            var key = k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            if (string.IsNullOrWhiteSpace(key))
                return "ERROR: keys の要素は空でない文字列（InputMap のキー名）で指定してください。";

            results.Add(await PostCmdAsync(http, "game_input_key", KeyArgs(key!, down: true)));
            if (holdMs > 0) await Task.Delay(holdMs);
            results.Add(await PostCmdAsync(http, "game_input_key", KeyArgs(key!, down: false)));
        }
    }

    // ── クリック: 座標へ移動してから押して離す ──
    if (args.TryGetProperty("click", out var click) && click.ValueKind == JsonValueKind.Object)
    {
        if (!click.TryGetProperty("x", out var cx) || !click.TryGetProperty("y", out var cy))
            return "ERROR: click には x と y の両方が必要です。";
        var button = click.TryGetProperty("button", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString()! : "left";

        results.Add(await PostCmdAsync(http, "game_input_mouse",
            ObjArgs($$"""{"x":{{cx.GetRawText()}},"y":{{cy.GetRawText()}}}""")));
        results.Add(await PostCmdAsync(http, "game_input_mouse",
            ObjArgs($$"""{"button":"{{button}}","down":true}""")));
        if (holdMs > 0) await Task.Delay(holdMs);
        results.Add(await PostCmdAsync(http, "game_input_mouse",
            ObjArgs($$"""{"button":"{{button}}","down":false}""")));
    }

    if (results.Count == 0)
        return "ERROR: keys（文字列配列）か click（{x,y}）のどちらかを指定してください。";

    return string.Join(Environment.NewLine, results);
}

/// <summary>seed_input 用: game_input_key の引数オブジェクトを作る。</summary>
/// <param name="key">キー名。</param>
/// <param name="down">押す(true) / 離す(false)。</param>
static JsonElement KeyArgs(string key, bool down)
    => ObjArgs(JsonSerializer.Serialize(new { key, down }));

/// <summary>
/// JSON 文字列を JsonElement へ変換する（seed_input が組み立てた引数を渡すため）。
///
/// JsonDocument は Dispose すると RootElement が無効になるため、
/// ここでは <see cref="JsonElement.Clone"/> して寿命から切り離す。
/// </summary>
/// <param name="json">オブジェクト形式の JSON 文字列。</param>
static JsonElement ObjArgs(string json)
{
    using var doc = JsonDocument.Parse(json);
    return doc.RootElement.Clone();
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
        var json = await GetWithTokenAsync(http, $"{SeedMcpServer.SeedInstance.ApiBase}/scene");
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
    SeedLaunchTool(),
    SeedAttachTool(),
    SeedInstanceTool(),
    SeedShutdownTool(),
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
    SeedProfileTool(),
    SeedGenerateFishThumbnailsTool(),
    SeedSaveGetTool(),
    SeedSaveSetTool(),
    SeedSaveDeleteTool(),
    SeedSaveFlushTool(),
    SeedFindActorTool(),
    SeedInputTool(),
    GameInputKeyTool(),
    GameInputMouseTool(),
    GameInputSequenceTool(),
    GameInputReleaseAllTool(),
};

/// <summary>引数を取らないツールの共通スキーマ。</summary>
static object EmptySchema() => new { type = "object", properties = new { } };

static object SeedLaunchTool() => new
{
    name        = "seed_launch",
    description =
        "SEED エディタを起動する（既定はヘッドレス＝画面に何も出さない）。"
      + "ヘッドレスではウィンドウを画面外へ置いたまま WPF とランタイムを動かすため、"
      + "人が見ていない環境でも seed_play / seed_screenshot(method=\"gpu\") が正しく動く。"
      + "空きポート（7300〜7399）とランダムなトークンを選び、起動したプロセスの pid と"
      + "トークンが一致することを確認してから成功を返す。以後この MCP サーバーは"
      + "**そのインスタンスだけ**を操作する（利用者が開いているエディタには一切触れない）。"
      + "この MCP サーバーがすでにインスタンスを束縛していれば already_running=true を返す。"
      + "他のツールが「seed_launch で起動したインスタンスのみ操作できます」を返したら、まずこれを呼ぶこと。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            headless = new
            {
                type        = "boolean",
                description = "true（既定）で画面に出さずに起動する。false にすると通常のウィンドウで起動する。"
            },
            scene = new
            {
                type        = "string",
                description = "起動時に開く .scene の絶対パス。省略時は前回開いていたシーンを復元する。"
            },
            wait_seconds = new
            {
                type        = "number",
                description = "AI ブリッジが応答するまで待つ上限秒数（既定 60、最大 300）。"
            }
        }
    }
};

/// <summary>
/// seed_attach: 利用者が開いているエディタへ明示的に接続するツール定義。
/// トークンはエディタの環境設定に表示され、利用者が渡したときだけ成立する。
/// </summary>
static object SeedAttachTool() => new
{
    name        = "seed_attach",
    description =
        "利用者がすでに開いている SEED エディタへ接続する（明示的な同意が必要）。"
      + "エディタ側で「編集 → 環境設定 → AI 操作を許可（このインスタンス）」をオンにすると"
      + "ポートとトークンが表示されるので、それを利用者から受け取って渡すこと。"
      + "AI の判断だけで対話中のエディタへ接続することはできない。"
      + "通常のヘッドレス作業では seed_launch を使い、このツールは使わない。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            port  = new { type = "number", description = "エディタの環境設定に表示されているポート番号。" },
            token = new { type = "string", description = "エディタの環境設定に表示されているインスタンストークン。" }
        },
        required = new[] { "port", "token" }
    }
};

/// <summary>seed_instance: 現在の束縛状態（どのインスタンスを操作しているか）を返す。</summary>
static object SeedInstanceTool() => new
{
    name        = "seed_instance",
    description =
        "この MCP サーバーが現在操作対象として束縛しているエディタインスタンスを返す"
      + "（bound / port / pid / headless / attached）。"
      + "bound=false のときは変更系ツールがすべて拒否される。",
    inputSchema = EmptySchema()
};

static object SeedShutdownTool() => new
{
    name        = "seed_shutdown",
    description =
        "束縛中の SEED エディタを正常終了させる（ランタイム子プロセスも停止する）。"
      + "seed_launch で起動したセッションの後始末に使う。応答を返してから終了するため、"
      + "呼び出しは成功で返り、その直後にプロセスが消える。"
      + "利用者が開いている対話エディタは既定で終了できない（エディタ側が拒否する）。",
    inputSchema = EmptySchema()
};

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
                                "select_actor", "play_control", "save_scene", "send_ipc",
                                // ゲーム入力の注入（Play 中のみ有効）
                                "game_input_key", "game_input_mouse",
                                "game_input_sequence", "game_input_release_all",
                                // セーブデータ（進行状態を作ってから Play する用）
                                "save_data",
                                // アクタ検索（名前 → DFS ID。後続操作の宛先を得る）
                                "find_actor"
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
                        command        = new { type = "string",  description = "send_ipc: 生 IPC 文字列" },
                        op             = new { type = "string",  description = "save_data: get / set / delete / save" },
                        type           = new { type = "string",  description = "save_data(set): int / float / string（省略時は value から推論）" },
                        flush          = new { type = "boolean", description = "save_data(set): true なら書き込み後にディスクへ書き出す" },
                        components     = new { type = "boolean", description = "find_actor: コンポーネント一覧も返すか（既定 true）" }
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
        "描画結果を PNG でキャプチャし、画像として返す（同時にファイルへも保存する）。"
      + "target=\"viewport\": シーンビュー、\"game\": Play 中のゲーム画面、\"editor\": エディタウィンドウ全体。"
      + "method=\"gpu\"（既定）はランタイムの GPU から直接読み戻すため、"
      + "ウィンドウが隠れていても・最小化でも・ヘッドレス起動でも正しく撮れる（target は viewport/game のみ）。"
      + "method=\"screen\" は画面に映っているものを撮る従来方式で、target=\"editor\" のときはこちらが自動的に使われる"
      + "（この方式はウィンドウが最小化・他ウィンドウで隠れていると正しく撮れない）。"
      + "max_width / scale を指定すると縮小した PNG を返す（保存されるファイルも縮小版になる）。"
      + "レイアウト確認だけなら max_width=800 程度にするとコンテキスト消費を大きく減らせる。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            target = new
            {
                type        = "string",
                @enum       = new[] { "viewport", "game", "editor" },
                description = "撮影対象。省略時は viewport（method=gpu では viewport と game は同じ絵）。"
            },
            method = new
            {
                type        = "string",
                @enum       = new[] { "gpu", "screen" },
                description = "撮影方式。省略時は gpu（隠れていても撮れる）。target=\"editor\" では自動的に screen。"
            },
            path = new
            {
                type        = "string",
                description = "出力 PNG の絶対パス。省略時は OS のテンポラリ配下へ自動命名で保存する。"
            },
            max_width = new
            {
                type        = "integer",
                description = "縮小後の最大幅（px）。これより広い画像は縦横比を保って縮小される。省略時は縮小しない。"
            },
            scale = new
            {
                type        = "number",
                description = "縮小率（0〜1）。max_width と併用した場合は「より小さくなるほう」が採用される。"
            },
            keep_full = new
            {
                type        = "boolean",
                description = "true なら縮小前のフル解像度 PNG も \"<名前>.full.png\" として保存する（返す画像は縮小版のまま）。"
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
    description =
        "現在のシーンを保存する（Ctrl+S 相当）。Edit 状態でのみ実行でき、保存完了通知まで待つ。"
      + "ヘッドレスインスタンスでは confirm:true が必須（利用者が見ていない場所で "
      + ".scene を書き換えないための安全弁）。"
      + "他のエディタが同じシーンを開いている（.lock がある）場合は拒否される。"
      + "ランタイムが実際に読み込んでいるシーンと保存先が違う場合も拒否される。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            confirm = new
            {
                type        = "boolean",
                description = "ヘッドレスで保存する場合は true を明示する。省略すると拒否される。"
            }
        }
    }
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

static object SeedGenerateFishThumbnailsTool() => new
{
    name        = "seed_generate_fish_thumbnails",
    description =
        "魚図鑑（ずかん）用の画像を一括生成する。"
      + "assets/mainGame/actors/Fish/Lv<N>/*.actor の全 prefab を 1 匹ずつランタイムに"
      + "オフスクリーン描画させ、横向き（side）の透過 PNG を"
      + "assets/mainGame/textures/zukan/Lv<N>/<名前>.png へ書き出す。"
      + "全部を描き終えたあと、スクリプトから参照する静的データ表"
      + "assets/mainGame/scripts/FishCatalog.cs を prefab の内容から丸ごと再生成する。"
      + "現在開いているシーンは変更しない（描画はオフスクリーンで行う）。"
      + "魚の追加・リネーム・見た目の変更をしたら実行すること。"
      + "1 匹ずつ逐次で往復するため、匹数に比例して時間がかかる。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            size = new { type = "integer", description = "生成するサムネイルの一辺ピクセル数。省略時 512。" }
        }
    }
};

static object SeedProfileTool() => new
{
    name        = "seed_profile",
    description =
        "ランタイムの CPU プロファイラを seconds 秒ぶん計測し、セクション別の時間表と"
      + "「統合バッチ更新ゲート」の判定理由集計を返す。プロファイラパネルを開く必要はない"
      + "（計測中だけ自動で有効化される）。Play 中でも Edit 中でも計測できる。"
      + "戻り値は上位 top 件の要約表（テキスト）＋ 完全な JSON。"
      + "性能改修の「計測 → 修正 → 再計測」ループの入口として使う。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            seconds = new { type = "number", description = "計測する実時間（秒）。既定 3、範囲 0.2〜30。" },
            top     = new { type = "number", description = "要約表に出すスコープ行数（既定 40）。" }
        }
    }
};


// ── セーブデータ（SEED.SaveData）とアクタ検索 ────────────────────────────────
//  検証したい進行状態を素早く作り、名前しか知らないアクタの DFS ID を引くための道具。
//  いずれもエディタ側の save_data / find_actor コマンドへ橋渡しする。

static object SeedSaveGetTool() => new
{
    name        = "seed_save_get",
    description =
        "実行中ランタイムのセーブデータ（SEED.SaveData）から 1 件読む。"
      + "保存先はランタイムが決めるため（SEED_SAVE_DIR があればそれ）、"
      + "呼び出し側がパスを推測する必要はない。"
      + "戻り値は {ok, result:{op,key,found,type,value}}。",
    inputSchema = new
    {
        type       = "object",
        properties = new { key = new { type = "string", description = "セーブキー" } },
        required   = new[] { "key" }
    }
};

static object SeedSaveSetTool() => new
{
    name        = "seed_save_set",
    description =
        "実行中ランタイムのセーブデータへ 1 件書く（検証したい進行状態を作る用）。"
      + "type を省略すると value の JSON 型から推論する（整数 → int / 実数 → float / 文字列 → string）。"
      + "flush:true でディスクにも書き出す（省略時はメモリ上のみ。Play 終了時に自動保存される）。"
      + "ファイルを手で書く方式と違い、Play 中でもランタイム側の値が正しく更新される。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            key   = new { type = "string",  description = "セーブキー" },
            value = new { description = "書き込む値（数値または文字列）" },
            type  = new { type = "string",  description = "int / float / string（省略時は value から推論）" },
            flush = new { type = "boolean", description = "true なら書き込み後にディスクへ書き出す" }
        },
        required = new[] { "key", "value" }
    }
};

static object SeedSaveDeleteTool() => new
{
    name        = "seed_save_delete",
    description = "実行中ランタイムのセーブデータからキーを 1 件削除する。",
    inputSchema = new
    {
        type       = "object",
        properties = new { key = new { type = "string", description = "セーブキー" } },
        required   = new[] { "key" }
    }
};

static object SeedSaveFlushTool() => new
{
    name        = "seed_save_flush",
    description = "セーブデータをディスクへ書き出す（seed_save_set の flush:true と同じ処理を単体で行う）。",
    inputSchema = new { type = "object", properties = new { } }
};

static object SeedFindActorTool() => new
{
    name        = "seed_find_actor",
    description =
        "アクタを名前またはパスで探し、DFS ID と構成（コンポーネント一覧）を返す。"
      + "seed_select / seed_batch の宛先はすべて DFS ID なので、"
      + "「名前しか知らない」状態から 1 コールで橋渡しできる。"
      + "name は素の名前（ヒエラルキー DFS 順で最初の一致）か "
      + "\"Root/Child/Grand\" 形式の絶対パス（2D フォルダは透過）。"
      + "選択状態は変えないので、利用者が開いているエディタでも安全に呼べる。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            name       = new { type = "string",  description = "アクタ名、または \"Root/Child\" 形式のパス" },
            components = new { type = "boolean", description = "コンポーネント一覧も返すか（既定 true）" }
        },
        required = new[] { "name" }
    }
};

static object SeedInputTool() => new
{
    name        = "seed_input",
    description =
        "キー列の打鍵とクリックを 1 コールで撃つ高レベルラッパ（Play 中のみ）。"
      + "keys は「押す → hold_ms 待つ → 離す」を順に実行する。"
      + "click は {x,y(,button)} でカーソルを移動してから押して離す。"
      + "中身は game_input_key / game_input_mouse そのものなので安全機構は同じ。"
      + "拍に合わせるなど時間精度が要る操作は game_input_sequence を使うこと。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            keys    = new
            {
                type        = "array",
                items       = new { type = "string" },
                description = "順に打鍵するキー名の配列（InputMap と同じ表記）"
            },
            click   = new
            {
                type        = "object",
                description = "クリックする位置 {x, y, button?}（button は left / right / middle）"
            },
            hold_ms = new { type = "integer", description = "押してから離すまでの時間（ミリ秒。既定 80、上限 5000）" }
        }
    }
};

// ── ゲーム入力の注入（game_input_*）────────────────────────────────────────────
//  ランタイムの Input へ直接注入するツール群。スクリプトの SEED.Input.* /
//  InputMap のアクションがそのまま反応する。Play 中のみ有効。
//  IPC とその応答の仕様は docs/editor_mcp.md 9 章が正典。

static object GameInputKeyTool() => new
{
    name        = "game_input_key",
    description =
        "ゲームへキー入力を注入する（Play 中のみ）。down:true で押し、false で離す。"
      + "押しっぱなしは離すまで（または game_input_release_all / Play 停止まで）保持される。"
      + "キー名は InputMap と同じ表記（W / Space / Enter / LeftShift / Alpha0 / UpArrow / F1 …）。"
      + "OS のカーソルやフォーカスには影響しない。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            key  = new { type = "string",  description = "キー名（InputMap と同じ表記）" },
            down = new { type = "boolean", description = "true = 押す / false = 離す" }
        },
        required = new[] { "key", "down" }
    }
};

static object GameInputMouseTool() => new
{
    name        = "game_input_mouse",
    description =
        "ゲームへマウス操作を 1 件注入する（Play 中のみ）。"
      + "button(+down) = ボタン押下/解放、dx,dy = 相対移動（実入力へ加算）、"
      + "x,y = 絶対座標（ゲームビューポート左上原点 px。注入中は実カーソルより優先）、"
      + "scroll = ホイール（ライン数）。"
      + "1 回の呼び出しで指定できるのは 1 種類だけ。複数を組み合わせたい場合や"
      + "「左から右へ振る」ような連続移動は game_input_sequence を使う。",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            button = new
            {
                type        = "string",
                @enum       = new[] { "left", "right", "middle" },
                description = "押下/解放するボタン。指定時は down も必須。"
            },
            down   = new { type = "boolean", description = "button 指定時: true = 押す / false = 離す" },
            dx     = new { type = "number",  description = "相対移動量 X（px）" },
            dy     = new { type = "number",  description = "相対移動量 Y（px）" },
            x      = new { type = "number",  description = "絶対座標 X（px、y と併用）" },
            y      = new { type = "number",  description = "絶対座標 Y（px、x と併用）" },
            scroll = new { type = "number",  description = "ホイール量（ライン数。負値で下方向）" }
        }
    }
};

static object GameInputSequenceTool() => new
{
    name        = "game_input_sequence",
    description =
        "時間軸付きの入力イベント列をまとめて再生する（Play 中のみ）。"
      + "各要素は {\"t\":秒, ...} で、操作は 1 要素につき 1 個だけ書く"
      + "（key+down / mouse_button+down / mouse_move:[dx,dy] / mouse_pos:[x,y] / scroll）。"
      + "t は再生開始からの実時間で、省略時は 0（即時）。同じ t の要素は書いた順に同一フレームで発火する。"
      + "既定（wait:true）では全イベントを撃ち終える（INPUT_SEQUENCE_DONE）まで待って返るので、"
      + "直後に seed_screenshot を撮れば操作後の画面が得られる。"
      + "例: [{\"t\":0,\"key\":\"W\",\"down\":true},{\"t\":1.0,\"key\":\"W\",\"down\":false}]",
    inputSchema = new
    {
        type       = "object",
        properties = new
        {
            events = new
            {
                type        = "array",
                description = "イベントの配列（docs/editor_mcp.md 9.3 節の書式）",
                items       = new { type = "object", additionalProperties = true }
            },
            wait = new
            {
                type        = "boolean",
                description = "true（既定）= 再生完了まで待つ / false = 受理された時点で返る"
            }
        },
        required = new[] { "events" }
    }
};

static object GameInputReleaseAllTool() => new
{
    name        = "game_input_release_all",
    description =
        "注入中のキー・マウスボタンの押下をすべて解放し、絶対座標の注入も解除する（安全弁）。"
      + "解放されたキーはそのフレームの GetKeyUp として観測されるので、"
      + "スクリプトの状態機械が押しっぱなしのまま取り残されない。"
      + "一連の操作を終えたら Play を止める前にこれを呼ぶこと。",
    inputSchema = EmptySchema()
};

// ── seed_profile: 応答 JSON の要約整形 ────────────────────────────────────────

/// <summary>要約表に出すスコープ行数の既定値。</summary>
const int PROFILE_DEFAULT_TOP = 40;

/// <summary>要約表に出すスコープ行数の上限（応答が読めない長さになるのを防ぐ）。</summary>
const int PROFILE_MAX_TOP = 200;

/// <summary>統合バッチ統計で「更新に落ちたバッチ」を並べる最大件数。</summary>
const int PROFILE_MAX_BATCH_ROWS = 30;

/// <summary>スコープ階層の区切り（フラット化したパス表記に使う）。</summary>
const string PROFILE_PATH_SEPARATOR = " > ";

/// <summary>
/// profile コマンドを実行し、応答 JSON を「要約表 ＋ 完全 JSON」のテキストへ整形する。
/// 整形に失敗した場合は元の JSON をそのまま返す（情報を失わない）。
/// </summary>
static async Task<string> HandleProfileAsync(HttpClient http, JsonElement args)
{
    var raw = await PostCmdAsync(http, "profile", args);
    if (IsErrorResult(raw)) return raw;

    var top = PROFILE_DEFAULT_TOP;
    if (args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty("top", out var topEl)
        && topEl.ValueKind == JsonValueKind.Number
        && topEl.TryGetInt32(out var requestedTop))
    {
        top = Math.Clamp(requestedTop, 1, PROFILE_MAX_TOP);
    }

    try
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("dump", out var dump)) return raw;

        var sb = new StringBuilder();
        AppendProfileScopeTable(sb, dump, top);
        AppendProfileMergeTable(sb, dump);
        sb.Append("\n── 完全な JSON ──\n").Append(raw);
        return sb.ToString();
    }
    catch (Exception)
    {
        // 整形できない形（スキーマ変更など）でも生 JSON は必ず返す。
        return raw;
    }
}

/// <summary>スコープツリーをフラット化し、平均時間の降順で表にする。</summary>
static void AppendProfileScopeTable(StringBuilder sb, JsonElement dump, int top)
{
    if (!dump.TryGetProperty("profile", out var profile)) return;

    double GetNum(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;

    sb.Append("── フレーム ──\n");
    sb.Append($"frames={GetNum(profile, "frames"):0} window={GetNum(profile, "window_ms"):0} ms ")
      .Append($"fps={GetNum(profile, "fps"):0.0} ")
      .Append($"frame_avg={GetNum(profile, "frame_avg_ms"):0.00} ms ")
      .Append($"frame_max={GetNum(profile, "frame_max_ms"):0.00} ms\n\n");

    // (パス, avg_ms, max_ms, self_ms, share, calls) をフラットに集める。
    var rows = new List<(string Path, double Avg, double Max, double Self, double Share, double Calls)>();
    void Walk(JsonElement node, string prefix)
    {
        var name = node.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
        var path = prefix.Length == 0 ? name : prefix + PROFILE_PATH_SEPARATOR + name;
        rows.Add((path, GetNum(node, "avg_ms"), GetNum(node, "max_ms"),
                  GetNum(node, "self_ms"), GetNum(node, "share"), GetNum(node, "calls")));
        if (node.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
            foreach (var kid in kids.EnumerateArray()) Walk(kid, path);
    }
    if (profile.TryGetProperty("root", out var rootNode)) Walk(rootNode, "");

    rows.Sort((a, b) => b.Avg.CompareTo(a.Avg));
    sb.Append("── スコープ（avg_ms 降順・上位 ").Append(top).Append(" 件）──\n");
    sb.Append("  avg_ms   max_ms  self_ms   share  calls/f  scope\n");
    foreach (var r in rows.Take(top))
    {
        sb.Append($"{r.Avg,8:0.000} {r.Max,8:0.000} {r.Self,8:0.000} {r.Share,6:0.0}% {r.Calls,8:0.0}  {r.Path}\n");
    }
    sb.Append('\n');
}

/// <summary>統合バッチ更新ゲートの判定理由集計を表にする。</summary>
static void AppendProfileMergeTable(StringBuilder sb, JsonElement dump)
{
    if (!dump.TryGetProperty("merge", out var merge) || merge.ValueKind != JsonValueKind.Object) return;

    double GetNum(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;

    sb.Append("── 統合バッチ更新ゲート ──\n");
    sb.Append($"frames={GetNum(merge, "frames"):0} batches={GetNum(merge, "batches_total"):0} ")
      .Append($"updates/frame={GetNum(merge, "updates_per_frame"):0.00}\n");

    if (merge.TryGetProperty("reason_totals", out var reasons) && reasons.ValueKind == JsonValueKind.Object)
    {
        sb.Append("理由別合計: ");
        foreach (var r in reasons.EnumerateObject())
            sb.Append(r.Name).Append('=').Append(r.Value.ToString()).Append("  ");
        sb.Append('\n');
    }

    if (merge.TryGetProperty("batches", out var batches) && batches.ValueKind == JsonValueKind.Array)
    {
        sb.Append("\n updated skipped  insts  upd/f  reasons  key\n");
        var shown = 0;
        foreach (var b in batches.EnumerateArray())
        {
            if (GetNum(b, "updated") <= 0) break;   // 更新回数の降順なので 0 が出たら以降は全部 0
            if (shown++ >= PROFILE_MAX_BATCH_ROWS) break;
            var reasonText = "";
            if (b.TryGetProperty("reasons", out var rs) && rs.ValueKind == JsonValueKind.Object)
                reasonText = string.Join(",", rs.EnumerateObject().Select(x => $"{x.Name}:{x.Value}"));
            var key = b.TryGetProperty("key", out var k) ? (k.GetString() ?? "") : "";
            sb.Append($"{GetNum(b, "updated"),8:0} {GetNum(b, "skipped"),7:0} {GetNum(b, "instances"),6:0} ")
              .Append($"{GetNum(b, "updates_per_frame"),6:0.00}  {reasonText}  {key}\n");
        }
    }
    sb.Append('\n');
}

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
