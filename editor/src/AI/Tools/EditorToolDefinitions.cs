// ============================================================
//  EditorToolDefinitions.cs — AI に公開するエディタ操作ツールの定義
//
//  AI が呼び出せるツールの一覧を返す。
//  各ツールは JSON Schema 形式のパラメータ定義を持つ。
//
//  対応ツール:
//    - get_scene_info    : シーン全体の情報取得
//    - list_asset_files  : アセットフォルダのファイル一覧取得
//    - add_actor         : アクター追加
//    - remove_actor      : アクター削除
//    - move_actor        : アクター移動
//    - add_component     : コンポーネント追加
//    - set_value         : コンポーネントフィールド値変更
//    - write_asset_file  : アセットファイル書き出し
// ============================================================

using SEEDEditor.AI.Models;

namespace SEEDEditor.AI.Tools;

/// <summary>
/// AI アシスタントに公開するエディタ操作ツールの定義を提供する静的クラス。
/// </summary>
public static class EditorToolDefinitions
{
    /// <summary>
    /// 全ツール定義の一覧を返す。
    /// AI プロバイダーへ渡す tools パラメータとして使用する。
    /// </summary>
    public static List<ToolDefinition> All() => new()
    {
        // ── アセットファイル一覧取得 ───────────────────────────────────────
        new ToolDefinition
        {
            Name        = "list_asset_files",
            Description =
                "List files in the project assets folder (absolute paths). " +
                "ALWAYS call this before setting model_path, texture_path, asset_path, or any file path field. " +
                "Use subdirectory to filter: 'models' for .glb files, 'textures' for images, 'scripts' for .cs files. " +
                "Returns one absolute file path per line.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "subdirectory": {
                  "type": "string",
                  "description": "検索するサブディレクトリ（例: 'models', 'textures', 'scripts'）。省略時はアセットフォルダ直下を一覧表示。"
                }
              }
            }
            """,
        },

        // ── シーン情報取得 ─────────────────────────────────────────────────
        new ToolDefinition
        {
            Name        = "get_scene_info",            Description =
                "Return the full scene as JSON: actor list with DFS IDs, transforms, and component slot details.\n" +
                "MANDATORY: Call this tool (1) before any edit, and (2) immediately after add_actor / remove_actor / add_component.\n" +
                "DFS IDs are re-indexed after structural changes — always re-fetch to get current IDs and slot indices.",
            ParametersJson = """{"type":"object","properties":{}}""",
        },

        // ── アクター追加 ───────────────────────────────────────────────────
        new ToolDefinition
        {
            Name        = "add_actor",
            Description = "Add a new actor to the scene root with an optional position. After calling this, you MUST call get_scene_info to obtain the new actor's DFS ID before using it.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string",  "description": "アクターの名前" },
                "x":    { "type": "number",  "description": "X 座標（省略時は 0）" },
                "y":    { "type": "number",  "description": "Y 座標（省略時は 0）" },
                "z":    { "type": "number",  "description": "Z 座標（省略時は 0）" }
              },
              "required": ["name"]
            }
            """,
        },

        // ── アクター削除 ───────────────────────────────────────────────────
        new ToolDefinition
        {
            Name        = "remove_actor",
            Description = "Remove the actor with the given DFS ID from the scene. All child actors are also removed. After calling this, you MUST call get_scene_info because all DFS IDs are re-assigned.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "actor_dfs_id": { "type": "integer", "description": "削除するアクターの DFS ID（get_scene_info で確認できる）" }
              },
              "required": ["actor_dfs_id"]
            }
            """,
        },

        // ── アクター移動 ───────────────────────────────────────────────────
        new ToolDefinition
        {
            Name        = "move_actor",
            Description = "Move the actor with the given DFS ID to world-space coordinates. This does NOT change DFS IDs.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "actor_dfs_id": { "type": "integer", "description": "移動するアクターの DFS ID" },
                "x": { "type": "number", "description": "新しい X 座標" },
                "y": { "type": "number", "description": "新しい Y 座標" },
                "z": { "type": "number", "description": "新しい Z 座標" }
              },
              "required": ["actor_dfs_id", "x", "y", "z"]
            }
            """,
        },

        // ── コンポーネント追加 ─────────────────────────────────────────────
        new ToolDefinition
        {
            Name        = "add_component",
            Description =
                "Add a component to the actor. component_type: \"Model\"/\"Camera\"/\"Sprite\"/\"Canvas\"/\"InputMap\"/\"Script\"/\"Collider\"/\"Rigidbody\"/\"Plugin:{Name}\". " +
                "After calling this, call get_scene_info to get the new slot_idx.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "actor_dfs_id":   { "type": "integer", "description": "コンポーネントを追加するアクターの DFS ID" },
                "component_type": { "type": "string",  "description": "追加するコンポーネントの種別（例: 'Model', 'Camera', 'Plugin:SamplePlugin'）" }
              },
              "required": ["actor_dfs_id", "component_type"]
            }
            """,
        },

        // ── フィールド値変更 ───────────────────────────────────────────────
        new ToolDefinition
        {
            Name        = "set_value",
            Description =
                "Set a component field. Get slot_idx from get_scene_info first. All values are strings. " +
                "Keys: Model→model_path; Camera→fov/near/far/is_main/clear_color(\"r,g,b,a\"); " +
                "Sprite→texture_path/color(\"r,g,b,a\")/width/height; Canvas→width/height/auto_scale; " +
                "InputMap→asset_path; Plugin→plugin-defined key. " +
                "Collider and Rigidbody: NOT supported, tell user to use inspector.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "actor_dfs_id": { "type": "integer", "description": "対象アクターの DFS ID" },
                "slot_idx":     { "type": "integer", "description": "コンポーネントスロットのインデックス（0 始まり、get_scene_info で確認可能）" },
                "key":          { "type": "string",  "description": "変更するフィールドのキー名" },
                "value":        { "type": "string",  "description": "設定する値（文字列表現。数値や色も文字列として渡す）" }
              },
              "required": ["actor_dfs_id", "slot_idx", "key", "value"]
            }
            """,
        },

        // ── アセットファイル書き出し ────────────────────────────────────────
        new ToolDefinition
        {
            Name        = "write_asset_file",
            Description = "Write a file to the project assets folder. Use this to generate script files (.cs), data files, or config files. Path is relative to the assets folder (e.g. \"scripts/player.cs\"). The file is written immediately and can be referenced by ScriptComponent or other components.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "relative_path": { "type": "string", "description": "アセットフォルダからの相対パス（例: 'scripts/player.rs'）" },
                "content":       { "type": "string", "description": "書き出すファイルの内容" }
              },
              "required": ["relative_path", "content"]
            }
            """,
        },
    };
}
