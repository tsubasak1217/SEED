// ============================================================
//  EditorCommandExecutor.SaveData.cs — セーブデータ操作とアクタ検索
//
//  外部エージェント（MCP）が「検証したい状態」を素早く作るためのコマンド群。
//  EditorCommandExecutor の partial 実装。
//
//  【対応コマンド】
//    save_data   : 実行中ランタイムのセーブデータ（SEED.SaveData）を読み書きする。
//                  IPC `SAVE_DATA:{json}` → `SAVE_DATA_OK:{json}` / `SAVE_DATA_ERROR:{msg}`。
//                  ランタイムのストアを直接触るので、保存先（SEED_SAVE_DIR の有無・
//                  実行モード）を呼び出し側が推測せずに済み、Play 中の上書き事故も起きない。
//    find_actor  : 名前またはパスからアクタの DFS ID と構成（コンポーネント一覧）を引く。
//                  seed_select / seed_batch の宛先はすべて DFS ID なので、
//                  「名前しか知らない」状態から 1 コールで橋渡しできる。
//
//  【安全性】
//   save_data は変更系（AiOperationPolicy の読み取り専用インスタンスでは拒否）。
//   find_actor は観測のみなので読み取り専用でも許可する。
// ============================================================

using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace SEEDEditor.AI.Tools;

public partial class EditorCommandExecutor
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>セーブデータ操作の応答待ちタイムアウト（ミリ秒）。IPC 受信フレームで即答される。</summary>
    private const int SaveDataTimeoutMs = 5_000;

    /// <summary>アクタ構成（ACTOR_COMPONENTS）の応答待ちタイムアウト（ミリ秒）。</summary>
    private const int FindActorTimeoutMs = 5_000;

    /// <summary>save_data: 操作種別（get / set / delete / save）。</summary>
    private const string SaveDataOpArg = "op";

    /// <summary>save_data: 対象キー。</summary>
    private const string SaveDataKeyArg = "key";

    /// <summary>save_data: 書き込む値。</summary>
    private const string SaveDataValueArg = "value";

    /// <summary>save_data: 値の型（int / float / string。省略時は value から推論）。</summary>
    private const string SaveDataTypeArg = "type";

    /// <summary>save_data: 書き込み後にディスクへ書き出すか。</summary>
    private const string SaveDataFlushArg = "flush";

    /// <summary>find_actor: 探すアクタ名またはパス。</summary>
    private const string FindActorNameArg = "name";

    /// <summary>find_actor: コンポーネント一覧も取るか（既定 true）。</summary>
    private const string FindActorComponentsArg = "components";

    // ── ディスパッチ ─────────────────────────────────────────────

    /// <summary>
    /// セーブデータ・アクタ検索系コマンドを実行する。
    /// 扱わないコマンド名の場合は null を返し、呼び出し元が他のグループへ委ねる。
    /// </summary>
    /// <param name="command">コマンド名。</param>
    /// <param name="args">ツール引数。</param>
    private Task<string>? ExecuteSaveDataTool(string command, JsonElement args)
        => command switch
        {
            "save_data"  => ExecuteSaveDataAsync(args),
            "find_actor" => ExecuteFindActorAsync(args),
            _            => null,
        };

    // ── save_data ────────────────────────────────────────────────

    /// <summary>
    /// セーブデータを読み書きする【AI からセーブ状態を作る唯一の入口】。
    ///
    /// 引数はランタイム側の要求 JSON をほぼそのまま組み立てる:
    ///   op="get"    + key
    ///   op="set"    + key + value（+ type）
    ///   op="delete" + key
    ///   op="save"
    /// set のとき <c>flush:true</c> を付けると、書き込み後に続けて op="save" を送る
    /// （Play を挟まずに次回起動へ残したい場合に使う）。
    /// </summary>
    private async Task<string> ExecuteSaveDataAsync(JsonElement args)
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        var op = GetString(args, SaveDataOpArg);
        if (string.IsNullOrWhiteSpace(op))
            return Error("'op' が必要です（get / set / delete / save）。");

        // 要求 JSON を組み立てる（ランタイム側 save_data_ops.rs が正典）
        string request;
        switch (op)
        {
            case "save":
                request = BuildSaveDataRequest(op, key: null, args);
                break;

            case "get":
            case "delete":
            case "set":
            {
                var key = GetString(args, SaveDataKeyArg);
                if (string.IsNullOrWhiteSpace(key))
                    return Error($"'key' が必要です（op=\"{op}\"）。");
                if (op == "set" && !HasProperty(args, SaveDataValueArg))
                    return Error("'value' が必要です（op=\"set\"）。");
                request = BuildSaveDataRequest(op, key, args);
                break;
            }

            default:
                return Error($"不明な op '{op}'（get / set / delete / save）。");
        }

        var reply = await host.SendSaveDataAsync(request, SaveDataTimeoutMs);
        _log($"[AI ツール] SAVE_DATA:{request} → {reply ?? "(タイムアウト)"}");
        var formatted = FormatSaveDataReply(request, reply);

        // set のあとに flush:true が指定されていれば、続けてディスクへ書き出す
        if (op == "set" && GetBoolOrNull(args, SaveDataFlushArg) == true && !IsErrorJson(formatted))
        {
            var flushRequest = BuildSaveDataRequest("save", key: null, args);
            var flushReply   = await host.SendSaveDataAsync(flushRequest, SaveDataTimeoutMs);
            _log($"[AI ツール] SAVE_DATA:{flushRequest} → {flushReply ?? "(タイムアウト)"}");

            var flushFormatted = FormatSaveDataReply(flushRequest, flushReply);
            // 書き込み結果を捨てないよう、set の結果に flush の成否を添えて返す
            if (IsErrorJson(flushFormatted)) return flushFormatted;
            return formatted[..^1] + ",\"flushed\":true}";
        }

        return formatted;
    }

    /// <summary>
    /// ランタイムへ送る 1 行 JSON を組み立てる。
    /// value / type は指定があるときだけ載せる（省略時はランタイム側が推論する）。
    /// </summary>
    /// <param name="op">操作種別。</param>
    /// <param name="key">対象キー（op="save" では null）。</param>
    /// <param name="args">ツール引数。</param>
    private static string BuildSaveDataRequest(string op, string? key, JsonElement args)
    {
        using var stream = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString(SaveDataOpArg, op);
            if (key is not null) w.WriteString(SaveDataKeyArg, key);

            if (GetString(args, SaveDataTypeArg) is { Length: > 0 } type)
                w.WriteString(SaveDataTypeArg, type);

            if (args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty(SaveDataValueArg, out var value)
                && value.ValueKind != JsonValueKind.Undefined)
            {
                w.WritePropertyName(SaveDataValueArg);
                value.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>ランタイム応答を機械可読な JSON へ整形する。</summary>
    /// <param name="request">送信した要求 JSON（失敗時の手掛かりとして返す）。</param>
    /// <param name="reply">ランタイム応答行（タイムアウト時は null）。</param>
    private static string FormatSaveDataReply(string request, string? reply)
    {
        if (reply is null)
            return Json(new
            {
                ok      = false,
                request,
                error   = "ランタイムから応答が返りませんでした"
                        + "（未接続、または描画ループが止まっている可能性）。",
            });

        if (reply.StartsWith(Runtime.RuntimeManager.SAVE_DATA_ERROR_PREFIX, StringComparison.Ordinal))
            return Json(new
            {
                ok      = false,
                request,
                error   = reply[Runtime.RuntimeManager.SAVE_DATA_ERROR_PREFIX.Length..],
            });

        var body = reply.StartsWith(Runtime.RuntimeManager.SAVE_DATA_OK_PREFIX, StringComparison.Ordinal)
            ? reply[Runtime.RuntimeManager.SAVE_DATA_OK_PREFIX.Length..]
            : reply;

        // 結果 JSON はそのまま埋め込みたいので、生 JSON を組み立てる
        return $"{{\"ok\":true,\"result\":{body}}}";
    }

    /// <summary>整形済み応答がエラー扱いか（flush の追撃を止めるための判定）。</summary>
    /// <param name="json">FormatSaveDataReply の戻り値。</param>
    private static bool IsErrorJson(string json)
        => json.StartsWith("{\"ok\":false", StringComparison.Ordinal);

    // ── find_actor ───────────────────────────────────────────────

    /// <summary>
    /// 名前またはパスからアクタを探し、DFS ID と構成を返す
    /// 【名前しか知らない AI が DFS ID を得る唯一の入口】。
    ///
    /// 名前の解決規則は Hierarchy のノードモデル
    /// （<c>HierarchyPanel.ActorDfsIdByPath</c>）に一元化してある。
    /// </summary>
    private async Task<string> ExecuteFindActorAsync(JsonElement args)
    {
        var name = GetString(args, FindActorNameArg);
        if (string.IsNullOrWhiteSpace(name))
            return Error("'name' が必要です（アクタ名、または \"Root/Child\" 形式のパス）。");

        var resolver = Panels.ActorRefJump.ActorDfsIdByPath;
        if (resolver is null)
            return Error("Hierarchy パネルへ接続されていません（エディタ初期化前の可能性）。");

        var dfsId = resolver(name);
        if (dfsId is null)
            return Json(new { ok = false, name, found = false, error = $"アクタ '{name}' が見つかりません。" });

        // components:false なら DFS ID だけ返す（軽量な問い合わせ）
        if (GetBoolOrNull(args, FindActorComponentsArg) == false)
            return Json(new { ok = true, name, found = true, dfs_id = dfsId.Value });

        var host = Host;
        if (host is null)
            return Json(new { ok = true, name, found = true, dfs_id = dfsId.Value });

        var json = await host.GetActorComponentsAsync(dfsId.Value, FindActorTimeoutMs);
        if (string.IsNullOrEmpty(json))
            return Json(new
            {
                ok         = true,
                name,
                found      = true,
                dfs_id     = dfsId.Value,
                components = (string?)null,
                warning    = "構成（ACTOR_COMPONENTS）の取得がタイムアウトしました。",
            });

        // ACTOR_COMPONENTS の JSON をそのまま埋め込む（正典はランタイム側の書式）
        return $"{{\"ok\":true,\"name\":{JsonSerializer.Serialize(name)},"
             + $"\"found\":true,\"dfs_id\":{dfsId.Value},\"components\":{json}}}";
    }
}
