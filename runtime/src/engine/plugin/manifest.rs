// ============================================================
//  plugin/manifest.rs — plugin.json マニフェスト
//
//  各プラグインフォルダに同梱する plugin.json のデシリアライズ型。
//
//  ディレクトリ構造:
//    {assets_root}/../plugins/
//      PhysicsPlugin/
//        PhysicsPlugin.dll
//        plugin.json
// ============================================================

use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

use super::editor_menu::EditorMenuDef;

// ============================================================
//  PluginManifest
// ============================================================

/// plugin.json の内容を表す型。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PluginManifest {
    /// プラグイン識別名（フォルダ名・DLL 名の基準）
    pub name:        String,
    /// バージョン文字列
    pub version:     String,
    /// プラグインの説明文
    #[serde(default)]
    pub description: String,
    /// 作者名
    #[serde(default)]
    pub author:      String,
    /// エントリポイント DLL のファイル名（省略時は "{name}.dll"）
    #[serde(default)]
    pub entry_dll:   String,
    /// エディタのメニューバーへ追加する項目の宣言（省略可）。
    ///
    /// データドリブンにメニューを増やせるようにするための拡張フィールド。
    /// 既存の plugin.json（このキーを持たないもの）は `serde(default)` により
    /// 空配列として読み込まれるため、後方互換が保たれる。
    /// 詳細な書式は `editor_menu.rs` を参照。
    #[serde(default)]
    pub editor_menus: Vec<EditorMenuDef>,
}

impl PluginManifest {
    /// DLL の絶対パスを返す。entry_dll が空の場合は "{name}.dll" を使う。
    pub fn dll_path(&self, plugin_dir: &Path) -> PathBuf {
        let dll_name = if self.entry_dll.is_empty() {
            format!("{}.dll", self.name)
        } else {
            self.entry_dll.clone()
        };
        plugin_dir.join(dll_name)
    }

    /// 指定ディレクトリの plugin.json を読み込む。
    /// 失敗した場合は None を返す。
    pub fn load_from_dir(plugin_dir: &Path) -> Option<Self> {
        let path = plugin_dir.join("plugin.json");
        let text = std::fs::read_to_string(&path).ok()?;
        serde_json::from_str(&text).ok()
    }
}

// ============================================================
//  PluginEntry — project_settings.json 内のプラグイン状態
// ============================================================

/// プロジェクト設定に保存するプラグインのエントリ。
/// 有効/無効の状態とプラグイン名を保持する。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PluginEntry {
    /// プラグイン識別名（PluginManifest.name と一致する）
    pub name:    String,
    /// 有効/無効フラグ（false の場合 DLL はロードされない）
    pub enabled: bool,
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 旧来の plugin.json（editor_menus キーなし）が読め、
    /// editor_menus が空配列になることを確認する（後方互換）。
    #[test]
    fn manifest_without_editor_menus_defaults_to_empty() {
        let json = r#"{
            "name": "SamplePlugin",
            "version": "0.1.0",
            "description": "説明",
            "author": "SEED Engine",
            "entry_dll": "sample_plugin.dll"
        }"#;

        let m: PluginManifest = serde_json::from_str(json).expect("旧形式の plugin.json が読めること");
        assert_eq!(m.name, "SamplePlugin");
        assert!(m.editor_menus.is_empty(), "editor_menus は既定で空配列であること");
    }

    /// 必須キー（name / version）だけの最小 plugin.json も読めることを確認する。
    #[test]
    fn manifest_minimal_json_is_accepted() {
        let json = r#"{ "name": "Tiny", "version": "0.0.1" }"#;
        let m: PluginManifest = serde_json::from_str(json).expect("最小形式が読めること");
        assert!(m.description.is_empty());
        assert!(m.editor_menus.is_empty());
        // entry_dll 省略時は "{name}.dll" が使われる
        assert_eq!(
            m.dll_path(Path::new("plugins/Tiny")).file_name().unwrap(),
            "Tiny.dll"
        );
    }

    /// editor_menus を持つ plugin.json が正しく構造化されることを確認する。
    #[test]
    fn manifest_parses_editor_menus() {
        let json = r#"{
            "name": "GameTools",
            "version": "0.1.0",
            "editor_menus": [
                {
                    "menu": "Game",
                    "items": [
                        { "id": "delete_save", "label": "ユーザーデータ削除", "confirm": "消します？" },
                        { "separator": true },
                        { "id": "noop" }
                    ]
                }
            ]
        }"#;

        let m: PluginManifest = serde_json::from_str(json).expect("editor_menus 付きが読めること");
        assert_eq!(m.editor_menus.len(), 1);

        let menu = &m.editor_menus[0];
        assert!(menu.is_valid());
        assert_eq!(menu.menu, "Game");
        assert_eq!(menu.items.len(), 3);

        // 1件目: 確認ダイアログ付きの実行項目
        assert!(menu.items[0].is_actionable());
        assert_eq!(menu.items[0].display_label(), "ユーザーデータ削除");
        assert_eq!(menu.items[0].confirm, "消します？");

        // 2件目: 区切り線は実行対象にならない
        assert!(!menu.items[1].is_actionable());
        assert!(menu.items[1].separator);

        // 3件目: label 省略時は id がラベルに使われる
        assert!(menu.items[2].is_actionable());
        assert_eq!(menu.items[2].display_label(), "noop");
        assert!(menu.items[2].confirm.is_empty());
    }
}
