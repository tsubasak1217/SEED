// ============================================================
//  editor_preview/wire.rs — エディタのプレビューが外へ出す文字列（応答の書式・インスペクタの断片・ログの接頭辞）
//
//  【応答】（命令ごとに 1 通。エディタは PREVIEW_ERROR を表示し、ほかは結果として使う）
//    PREVIEW_ADDED:{world_line},{root_dfs}   … 作った（エディタはその行を見せる）
//    PREVIEW_CLEARED:{count}                 … 消した数（0 でも返す）
//    PREVIEW_REFRESHED:{count},{path}        … 作り直した根の数（0 でも返す。path は受け取ったまま）
//    PREVIEW_ERROR:{message}                 … 失敗・拒否（改行は空白へ置き換える＝1 行に収める）
//  【インスペクタ】ACTOR_COMPONENTS の "editor_preview" の値（inspector_preview_json）
//    null / {"root_dfs":N,"is_root":bool,"prefab":"..","frame":".."|null}
// ============================================================

use super::tree::PreviewRootRef;

/// ログの接頭辞（標準エラー。エディタの Output で探しやすくする）。
pub(super) const LOG_PREFIX: &str = "[Preview]";

/// プレビューを作った応答の頭（`PREVIEW_ADDED:{world_line},{root_dfs}`）。
const PREVIEW_ADDED_PREFIX: &str = "PREVIEW_ADDED:";

/// プレビューを消した応答の頭（`PREVIEW_CLEARED:{count}`）。
const PREVIEW_CLEARED_PREFIX: &str = "PREVIEW_CLEARED:";

/// プレビューを作り直した応答の頭（`PREVIEW_REFRESHED:{count},{path}`）。
const PREVIEW_REFRESHED_PREFIX: &str = "PREVIEW_REFRESHED:";

/// 失敗・拒否の応答の頭（`PREVIEW_ERROR:{message}`）。
const PREVIEW_ERROR_PREFIX: &str = "PREVIEW_ERROR:";

/// 応答の 1 行を壊す文字（IPC は 1 行 = 1 通なので、文言の中の改行は空白へ置き換える）。
const LINE_BREAKS: [char; 2] = ['\r', '\n'];

/// 改行の代わりに入れる文字。
const LINE_BREAK_REPLACEMENT: &str = " ";

/// インスペクタの "editor_preview" がプレビューの外のときの値。
const INSPECTOR_NOT_IN_PREVIEW: &str = "null";

/// `PREVIEW_ADDED:{world_line},{root_dfs}` を作る。
pub(super) fn format_added(world_line: u32, root_dfs: u32) -> String {
    format!("{PREVIEW_ADDED_PREFIX}{world_line},{root_dfs}")
}

/// `PREVIEW_CLEARED:{count}` を作る（0 でも返す）。
pub(super) fn format_cleared(count: usize) -> String {
    format!("{PREVIEW_CLEARED_PREFIX}{count}")
}

/// `PREVIEW_REFRESHED:{count},{path}` を作る（0 でも返す。path は受け取ったまま）。
pub(super) fn format_refreshed(count: usize, path: &str) -> String {
    format!("{PREVIEW_REFRESHED_PREFIX}{count},{path}")
}

/// `PREVIEW_ERROR:{message}` を作る（文言の改行は空白へ置き換えて 1 行に収める）。
///
/// 写しの閲覧専用の拒否（snapshot_view_ops.rs）もこの書式で返すので crate 内へ公開する。
pub(crate) fn format_error(message: &str) -> String {
    format!("{PREVIEW_ERROR_PREFIX}{}", message.replace(LINE_BREAKS, LINE_BREAK_REPLACEMENT))
}

/// ACTOR_COMPONENTS の "editor_preview" の値 1 つ分（欄の並びを固定するための直列化用の形）。
#[derive(serde::Serialize)]
struct InspectorPreview<'a> {
    /// そのアクタを含むいちばん近いプレビューの根の DFS 番号
    root_dfs: u32,
    /// 問い合わせたアクタ自身が根か
    is_root: bool,
    /// 中身のプレハブ
    prefab: &'a str,
    /// 枠のプレハブ（無ければ null）
    frame: Option<&'a str>,
}

/// ACTOR_COMPONENTS の "editor_preview" の値を作る（プレビューの外なら `null`）。
///
/// 文字列は serde_json で直列化する（パスの '\' や '"' を安全にエスケープするため）。
pub(crate) fn inspector_preview_json(root: Option<&PreviewRootRef>) -> String {
    let Some(root) = root else {
        return INSPECTOR_NOT_IN_PREVIEW.to_string();
    };
    let value = InspectorPreview {
        root_dfs: root.dfs,
        is_root: root.is_self,
        prefab: &root.info.prefab,
        frame: root.info.frame.as_deref(),
    };
    serde_json::to_string(&value).unwrap_or_else(|_| INSPECTOR_NOT_IN_PREVIEW.to_string())
}

// ============================================================
//  テスト — 応答の書式とインスペクタの断片
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::ecs::Entity;
    use crate::engine::structs::objects::actor::EditorPreviewInfo;

    /// 応答の書式（0 件でも返す・path は受け取ったまま・エラーの改行は空白へ）。
    #[test]
    fn replies_follow_the_wire_format() {
        assert_eq!(format_added(0, 12), "PREVIEW_ADDED:0,12");
        assert_eq!(format_cleared(0), "PREVIEW_CLEARED:0", "0 件でも返す");
        assert_eq!(format_cleared(3), "PREVIEW_CLEARED:3");
        assert_eq!(
            format_refreshed(2, "assets://ui/screens/Home,1.actor"),
            "PREVIEW_REFRESHED:2,assets://ui/screens/Home,1.actor",
            "path は受け取ったまま（カンマも含む）"
        );
        assert_eq!(format_refreshed(0, "C:\\p\\a.actor"), "PREVIEW_REFRESHED:0,C:\\p\\a.actor");
        assert_eq!(
            format_error("読めません:\r\n  a.actor\nb"),
            "PREVIEW_ERROR:読めません:    a.actor b",
            "改行は空白へ置き換えて 1 行に収める"
        );
    }

    /// インスペクタの "editor_preview": 外なら null、中なら根の DFS・自分が根か・中身と枠（無ければ null）。
    /// パスの '\' と '"' は JSON として安全にエスケープされること。
    #[test]
    fn inspector_json_is_null_outside_and_object_inside() {
        assert_eq!(inspector_preview_json(None), "null");

        let root = PreviewRootRef {
            dfs: 7,
            entity: Entity::default(),
            is_self: false,
            info: EditorPreviewInfo {
                prefab: "C:\\ui\\\"Home\".actor".to_string(),
                frame: None,
                frame_body: String::new(),
                layer_bias: 0,
            },
        };
        let json = inspector_preview_json(Some(&root));
        let value: serde_json::Value = serde_json::from_str(&json).expect("JSON として読めること");
        assert_eq!(value["root_dfs"], 7);
        assert_eq!(value["is_root"], false);
        assert_eq!(value["prefab"], "C:\\ui\\\"Home\".actor", "エスケープして往復すること");
        assert!(value["frame"].is_null(), "枠なしは null: {json}");
        assert!(json.starts_with("{\"root_dfs\":7,\"is_root\":false,"), "欄の並びは固定: {json}");

        let framed = PreviewRootRef {
            is_self: true,
            info: EditorPreviewInfo {
                prefab: "assets://ui/screens/Home.actor".to_string(),
                frame: Some("assets://ui/prefabs/screen_frame.actor".to_string()),
                frame_body: "Body".to_string(),
                layer_bias: 0,
            },
            ..root
        };
        let value: serde_json::Value =
            serde_json::from_str(&inspector_preview_json(Some(&framed))).expect("JSON として読めること");
        assert_eq!(value["is_root"], true);
        assert_eq!(value["frame"], "assets://ui/prefabs/screen_frame.actor");
    }
}
