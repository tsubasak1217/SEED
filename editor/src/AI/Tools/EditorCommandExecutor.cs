// ============================================================
//  EditorCommandExecutor.cs — AI ツール呼び出しの実行エンジン
//
//  AI から受け取ったツール呼び出し（ToolCall）を解析し、
//  対応するエディタ操作（IPC コマンド送信・ファイル書き出し）を実行する。
//
//  GET_SCENE_INFO のみランタイムからの非同期応答を待機する。
//  その他の変更系コマンドは即時 IPC 送信で完了する。
// ============================================================

using System.IO;
using System.Text.Json;
using SEEDEditor.AI.Models;

namespace SEEDEditor.AI.Tools;

/// <summary>
/// AI ツール呼び出しをエディタ操作に変換して実行するクラス。
/// </summary>
public class EditorCommandExecutor
{
    // ── フィールド ───────────────────────────────────────────
    /// <summary>ランタイムへ IPC コマンドを送信するデリゲート</summary>
    private readonly Action<string>         _sendToRuntime;
    /// <summary>シーン情報の非同期応答を待機するデリゲート</summary>
    private readonly Func<Task<string>>     _getSceneInfoAsync;
    /// <summary>アセットフォルダのルートパス</summary>
    private readonly string                 _assetsPath;
    /// <summary>ツール実行ログ出力デリゲート</summary>
    private readonly Action<string>         _log;

    // ── コンストラクタ ──────────────────────────────────────

    /// <summary>
    /// エディタコマンド実行エンジンを初期化する。
    /// </summary>
    /// <param name="sendToRuntime">IPC コマンド送信デリゲート</param>
    /// <param name="getSceneInfoAsync">シーン情報取得デリゲート（GET_SCENE_INFO → 応答待ち）</param>
    /// <param name="assetsPath">アセットフォルダの絶対パス</param>
    /// <param name="log">ツール実行ログ出力デリゲート</param>
    public EditorCommandExecutor(
        Action<string>      sendToRuntime,
        Func<Task<string>>  getSceneInfoAsync,
        string              assetsPath,
        Action<string>      log)
    {
        _sendToRuntime    = sendToRuntime;
        _getSceneInfoAsync = getSceneInfoAsync;
        _assetsPath       = assetsPath;
        _log              = log;
    }

    // ── 公開メソッド ─────────────────────────────────────────

    /// <summary>
    /// ToolCall を実行し、結果を文字列で返す。
    ///
    /// 返値は AI への tool 結果メッセージとして使用する。
    /// エラーが発生した場合はエラーメッセージを返す（例外は送出しない）。
    /// </summary>
    public async Task<string> ExecuteAsync(ToolCall toolCall)
    {
        _log($"[AI ツール] {toolCall.FunctionName}({toolCall.ArgumentsJson})");

        try
        {
            // 構造変化を伴う操作（add_actor / remove_actor / add_component）は
            // 実行後に自動でシーン情報を取得してレスポンスに付加する。
            // これにより、モデルが get_scene_info を別途呼び出さなくても
            // 最新の DFS ID とスロット情報を把握できる。
            switch (toolCall.FunctionName)
            {
                case "list_asset_files":
                    return ExecuteListAssetFiles(toolCall.ArgumentsJson);

                case "get_scene_info":
                    return await ExecuteGetSceneInfo();

                case "add_actor":
                {
                    var msg   = ExecuteAddActor(toolCall.ArgumentsJson);
                    var scene = await ExecuteGetSceneInfo();
                    return $"{msg}\n[現在のシーン情報]\n{scene}";
                }

                case "remove_actor":
                {
                    var msg   = ExecuteRemoveActor(toolCall.ArgumentsJson);
                    var scene = await ExecuteGetSceneInfo();
                    return $"{msg}\n[現在のシーン情報]\n{scene}";
                }

                case "add_component":
                {
                    var msg   = ExecuteAddComponent(toolCall.ArgumentsJson);
                    var scene = await ExecuteGetSceneInfo();
                    return $"{msg}\n[現在のシーン情報]\n{scene}";
                }

                case "move_actor":
                    return ExecuteMoveActor(toolCall.ArgumentsJson);

                case "set_value":
                    return ExecuteSetValue(toolCall.ArgumentsJson);

                case "write_asset_file":
                    return ExecuteWriteAssetFile(toolCall.ArgumentsJson);

                default:
                    return $"エラー: 未知のツール '{toolCall.FunctionName}'";
            }
        }
        catch (Exception ex)
        {
            var msg = $"エラー: {toolCall.FunctionName} の実行に失敗しました: {ex.Message}";
            _log(msg);
            return msg;
        }
    }

    // ── ツール実装 ────────────────────────────────────────────

    /// <summary>シーン情報を取得する（非同期・IPC 応答待ち）。</summary>
    private async Task<string> ExecuteGetSceneInfo()
    {
        _sendToRuntime("GET_SCENE_INFO");
        var json = await _getSceneInfoAsync();
        _log($"[AI ツール] get_scene_info → {json.Length} 文字");
        return json;
    }

    /// <summary>アクターを追加する。</summary>
    private string ExecuteAddActor(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "Actor" : "Actor";
        var x    = root.TryGetProperty("x",    out var xe) ? xe.GetSingle() : 0f;
        var y    = root.TryGetProperty("y",    out var ye) ? ye.GetSingle() : 0f;
        var z    = root.TryGetProperty("z",    out var ze) ? ze.GetSingle() : 0f;

        _sendToRuntime($"AI_ADD_ACTOR:{name},{x},{y},{z}");
        return $"アクター '{name}' を追加しました。新しく追加されたアクターの DFS ID を特定するために、次に必ず 'get_scene_info' を呼び出してください。";
    }

    /// <summary>アクターを削除する。</summary>
    private string ExecuteRemoveActor(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var id = doc.RootElement.GetProperty("actor_dfs_id").GetInt32();

        _sendToRuntime($"AI_REMOVE_ACTOR:{id}");
        return $"DFS ID={id} のアクターを削除しました。シーン構造が変わったため、次に必ず 'get_scene_info' を呼び出してください。";
    }

    /// <summary>アクターを移動する。</summary>
    private string ExecuteMoveActor(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        var id = root.GetProperty("actor_dfs_id").GetInt32();
        var x  = root.GetProperty("x").GetSingle();
        var y  = root.GetProperty("y").GetSingle();
        var z  = root.GetProperty("z").GetSingle();

        _sendToRuntime($"AI_MOVE_ACTOR:{id},{x},{y},{z}");
        return $"DFS ID={id} のアクターを移動しました。";
    }

    /// <summary>コンポーネントを追加する。</summary>
    private string ExecuteAddComponent(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        var actorId   = root.GetProperty("actor_dfs_id").GetInt32();
        var compType  = root.GetProperty("component_type").GetString() ?? "";

        // params_json は現在は空文字列を渡す（将来の拡張用）
        _sendToRuntime($"AI_ADD_COMPONENT:{actorId},{compType},{{}}");
        return $"DFS ID={actorId} のアクターに '{compType}' コンポーネントを追加しました。追加されたコンポーネントのスロットインデックスを確認するために、次に 'get_scene_info' を呼び出すことを推奨します。";
    }

    /// <summary>コンポーネントフィールドの値を変更する。</summary>
    private string ExecuteSetValue(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        var actorId  = root.GetProperty("actor_dfs_id").GetInt32();
        var slotIdx  = root.GetProperty("slot_idx").GetInt32();
        var key      = root.GetProperty("key").GetString()   ?? "";
        var value    = root.GetProperty("value").GetString() ?? "";

        _sendToRuntime($"AI_SET_VALUE:{actorId},{slotIdx},{key},{value}");
        return $"DFS ID={actorId} スロット={slotIdx} の '{key}' を '{value}' に設定しました。";
    }

    /// <summary>アセットファイルを書き出す。</summary>
    private string ExecuteWriteAssetFile(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        var relPath = root.GetProperty("relative_path").GetString() ?? "";
        var content = root.GetProperty("content").GetString()       ?? "";

        // パスインジェクション対策: アセットフォルダ外への書き出しを防ぐ
        var fullPath    = Path.GetFullPath(Path.Combine(_assetsPath, relPath));
        var assetsRoot  = Path.GetFullPath(_assetsPath);
        if (!fullPath.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
            return $"エラー: アセットフォルダ外への書き出しは禁止されています: {relPath}";

        // ディレクトリが存在しない場合は作成する
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(fullPath, content);
        _log($"[AI ツール] write_asset_file → {fullPath}");
        return $"'{relPath}' に {content.Length} 文字を書き出しました。";
    }

    /// <summary>
    /// アセットフォルダ内のファイル一覧を返す。
    /// AI がモデルパスや画像パスを設定する前に実際に存在するファイルを把握するために使用する。
    /// パスインジェクション対策として、アセットフォルダ外へのアクセスは禁止する。
    /// </summary>
    private string ExecuteListAssetFiles(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        // サブディレクトリが指定されていれば検索対象を絞り込む
        var subDir = root.TryGetProperty("subdirectory", out var sd)
            ? (sd.GetString() ?? "").Trim('/', '\\')
            : "";

        // 検索ディレクトリの解決
        var searchDir  = string.IsNullOrEmpty(subDir)
            ? Path.GetFullPath(_assetsPath)
            : Path.GetFullPath(Path.Combine(_assetsPath, subDir));
        var assetsRoot = Path.GetFullPath(_assetsPath);

        // パスインジェクション対策: アセットフォルダ外への参照を禁止する
        if (!searchDir.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
            return $"エラー: アセットフォルダ外へのアクセスは禁止されています: {subDir}";

        if (!Directory.Exists(searchDir))
            return $"ディレクトリが見つかりません: {(string.IsNullOrEmpty(subDir) ? "(assets root)" : subDir)}";

        // 再帰的にファイルを列挙して絶対パスの一覧を返す
        var files = Directory
            .EnumerateFiles(searchDir, "*", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
            return $"ファイルが見つかりません: {(string.IsNullOrEmpty(subDir) ? "(assets root)" : subDir)}";

        _log($"[AI ツール] list_asset_files → {files.Count} 件");
        return string.Join("\n", files);
    }
}
