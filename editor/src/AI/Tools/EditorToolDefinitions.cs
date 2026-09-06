// ============================================================
//  EditorToolDefinitions.cs — AI に公開するエディタ操作ツールの定義
//
//  AI が呼び出せるツールの一覧を返す。
//  各ツールは JSON Schema 形式のパラメータ定義を持つ。
//
//  対応ツール（シーン編集）:
//    - get_scene_info    : シーン全体の情報取得
//    - list_asset_files  : アセットフォルダのファイル一覧取得
//    - add_actor         : アクター追加
//    - remove_actor      : アクター削除
//    - move_actor        : アクター移動
//    - add_component     : コンポーネント追加
//    - set_value         : コンポーネントフィールド値変更
//    - write_asset_file  : アセットファイル書き出し
//
//  対応ツール（目視確認・再生制御・アニメ。実装は EditorCommandExecutor.Visual.cs）:
//    - screenshot        : 画面キャプチャ（PNG）
//    - select_actor      : アクター選択＋コンポーネント情報取得
//    - get_hierarchy     : ヒエラルキーツリー取得
//    - play_control      : play / pause / resume / stop
//    - send_ipc          : 生 IPC 送信（低レベル）
//    - anim_preview      : .anim の指定時刻プレビュー
//    - anim_preview_stop : プレビュー解除
//    - anim_reload       : .anim キャッシュ破棄
//    - get_log           : エディタログ末尾（ランタイム stderr 込み）
//    - save_scene        : シーン保存
//    - get_editor_state  : エディタ状態スナップショット
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

        // ── スクリーンショット（視覚確認）───────────────────────────────────
        new ToolDefinition
        {
            Name        = "screenshot",
            Description =
                "Capture what is currently on screen as a PNG and return its path. " +
                "target: 'viewport' (scene view), 'game' (Play view), 'editor' (whole editor window). " +
                "Use this to VERIFY a change visually instead of guessing. " +
                "The editor window must be visible and not covered by other windows.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "target": { "type": "string", "enum": ["viewport", "game", "editor"], "description": "撮影対象。省略時は viewport。" },
                "path":   { "type": "string", "description": "出力 PNG の絶対パス。省略時は OS のテンポラリ配下へ自動命名で保存する。" }
              }
            }
            """,
        },

        // ── アクター選択 ───────────────────────────────────────────────────
        new ToolDefinition
        {
            Name        = "select_actor",
            Description =
                "Select an actor (same path as clicking it in the Hierarchy) and return its ACTOR_COMPONENTS JSON. " +
                "Specify actor_dfs_id or name.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "actor_dfs_id": { "type": "integer", "description": "選択するアクターの DFS ID" },
                "name":         { "type": "string",  "description": "アクター名（DFS ID が不明なとき。ヒエラルキーから解決する）" }
              }
            }
            """,
        },

        // ── ヒエラルキー取得 ───────────────────────────────────────────────
        new ToolDefinition
        {
            Name        = "get_hierarchy",
            Description =
                "Return the current hierarchy tree as JSON (id = DFS id, name, parent, is_2d, is_vp, active, is_folder, is_prefab). " +
                "Lighter than get_scene_info; use it to look up DFS IDs by name.",
            ParametersJson = """{"type":"object","properties":{}}""",
        },

        // ── 再生制御 ───────────────────────────────────────────────────────
        new ToolDefinition
        {
            Name        = "play_control",
            Description =
                "Drive the editor play bar. action: 'play' (Edit only), 'pause' (Play only), 'resume' (Pause only), 'stop' (Play/Pause only). " +
                "wait_seconds waits after the transition so a following screenshot sees the game running.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "action":       { "type": "string", "enum": ["play", "pause", "resume", "stop"], "description": "実行する再生操作" },
                "wait_seconds": { "type": "number", "description": "遷移後に待つ秒数（0〜20）。ゲームを少し進めてから撮りたいときに使う。" }
              },
              "required": ["action"]
            }
            """,
        },

        // ── 生 IPC 送信（低レベル）─────────────────────────────────────────
        new ToolDefinition
        {
            Name        = "send_ipc",
            Description =
                "LOW-LEVEL escape hatch: send a raw IPC command string to the runtime. " +
                "No reply is awaited and no validation is done. Prefer the dedicated tools; " +
                "use this only for commands that have no tool yet.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "command": { "type": "string", "description": "ランタイムへ送る IPC 文字列（例: 'ANIM_RELOAD:seed://animations/foo.anim'）" }
              },
              "required": ["command"]
            }
            """,
        },

        // ── アニメーションプレビュー ───────────────────────────────────────
        new ToolDefinition
        {
            Name        = "anim_preview",
            Description =
                "Apply an .anim clip at a given time to an actor in Edit mode (same as scrubbing the animation timeline). " +
                "Follow with screenshot to see the pose. Call anim_preview_stop to restore the original values.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "actor_dfs_id": { "type": "integer", "description": "対象アクターの DFS ID" },
                "name":         { "type": "string",  "description": "対象アクター名（DFS ID の代わり）" },
                "clip_path":    { "type": "string",  "description": ".anim の絶対パスまたは seed:// 仮想パス" },
                "time":         { "type": "number",  "description": "プレビューする時刻（秒）" }
              },
              "required": ["clip_path", "time"]
            }
            """,
        },

        new ToolDefinition
        {
            Name        = "anim_preview_stop",
            Description = "Stop the animation preview for the actor and restore the values it had before the preview started.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "actor_dfs_id": { "type": "integer", "description": "対象アクターの DFS ID" },
                "name":         { "type": "string",  "description": "対象アクター名（DFS ID の代わり）" }
              }
            }
            """,
        },

        new ToolDefinition
        {
            Name        = "anim_reload",
            Description =
                "Drop the runtime's cached copy of an .anim clip so the next anim_preview re-reads it from disk. " +
                "MANDATORY after rewriting a .anim with write_asset_file.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "clip_path": { "type": "string", "description": ".anim の絶対パスまたは seed:// 仮想パス" }
              },
              "required": ["clip_path"]
            }
            """,
        },

        // ── ログ取得 ───────────────────────────────────────────────────────
        new ToolDefinition
        {
            Name        = "get_log",
            Description =
                "Return the last N lines of editor/logs/SEEDEditor.log. Runtime stderr is included with a [STDERR] prefix, " +
                "so LOAD_ERROR and script exceptions show up here. Use it when something did not appear as expected.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "lines": { "type": "integer", "description": "取得する末尾行数（1〜5000、省略時 200）" }
              }
            }
            """,
        },

        // ── シーン保存 ─────────────────────────────────────────────────────
        new ToolDefinition
        {
            Name        = "save_scene",
            Description = "Save the current scene (equivalent to Ctrl+S). Edit mode only. Waits for the runtime's save-completed reply.",
            ParametersJson = """{"type":"object","properties":{}}""",
        },

        // ── エディタ状態 ───────────────────────────────────────────────────
        new ToolDefinition
        {
            Name        = "get_editor_state",
            Description =
                "Return an editor state snapshot: Edit/Play/Pause state, current scene path, selected actor DFS id, " +
                "runtime connection, actor count and assets path. Cheap; call it first when unsure of the current context.",
            ParametersJson = """{"type":"object","properties":{}}""",
        },
    };
}
