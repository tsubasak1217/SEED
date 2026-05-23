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
