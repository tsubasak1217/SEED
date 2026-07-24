// ============================================================
//  components/plugin_component.rs — プラグインコンポーネント
//
//  【設計】
//  プラグインがアクターに付与するコンポーネント。
//  フィールド値は HashMap<String, String> でキー→値（文字列）として保持し、
//  型変換はプラグイン側・エディタ側で行う。
//
//  シリアライズはそのまま JSON に保存される。
//  PluginRegistry を参照してフィールド定義（PluginFieldDef）を取得し
//  エディタへ送信する。
// ============================================================

use crate::engine::ecs::storage::Component;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

// ============================================================
//  PluginComponent
// ============================================================

/// プラグインコンポーネントの実体（ECS World に格納される）。
///
/// plugin_name で PluginRegistry から対応プラグインを引き、
/// フィールド定義を取得してエディタのインスペクタに表示する。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PluginComponent {
    /// プラグイン識別名（PluginRegistry のキーに使う）
    pub plugin_name: String,
    /// フィールドの現在値（key → 文字列値）
    /// 値が未設定のフィールドはプラグインの default_value を使う
    pub fields: HashMap<String, String>,
}

impl PluginComponent {
    /// 指定プラグイン名で空のコンポーネントを生成する。
    pub fn new(plugin_name: impl Into<String>) -> Self {
        Self {
            plugin_name: plugin_name.into(),
            fields: HashMap::new(),
        }
    }

    /// フィールド値を取得する。未設定の場合は提供されたデフォルト値を返す。
    pub fn get_field<'a>(&'a self, key: &str, default: &'a str) -> &'a str {
        self.fields.get(key).map(|s| s.as_str()).unwrap_or(default)
    }

    /// フィールド値を設定する。
    pub fn set_field(&mut self, key: impl Into<String>, value: impl Into<String>) {
        self.fields.insert(key.into(), value.into());
    }
}

// ============================================================
//  PluginComponentData — シリアライズ用エイリアス
// ============================================================

/// ECS コンポーネントマーカーを実装する。
impl Component for PluginComponent {}

/// シリアライズ用データ（PluginComponent と同一構造）。
/// ComponentData::PluginComponent で使用する。
pub type PluginComponentData = PluginComponent;
