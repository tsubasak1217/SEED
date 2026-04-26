// ============================================================
//  script_component.rs — ScriptComponent / PlaceholderScriptSlot
//
//  ScriptComponent は C# CLR へのブリッジコンポーネント。
//  ライフサイクル呼び出しは ScriptSystem が担う（旧設計では Component 内に記述）。
//
//  PlaceholderScriptSlot はエディタモード（CLR 不使用）のフォールバック。
//  スクリプトのパスだけ保持し、シリアライズ時に復元できる。
// ============================================================

use std::sync::Arc;
use serde::{Deserialize, Serialize};
use crate::engine::ecs::Component;
use crate::engine::core::scripting::ScriptingHost;

// ─── ScriptComponentData ──────────────────────────────────────────────────────

/// シリアライズ用データ。
#[derive(Serialize, Deserialize, Clone)]
pub struct ScriptComponentData {
    pub type_name: String,
}

// ─── ScriptComponent ──────────────────────────────────────────────────────────

/// C# スクリプトを保持するコンポーネント。
///
/// ライフサイクル（begin_frame, update など）の呼び出しは
/// ScriptSystem が World をクエリして行う。
pub struct ScriptComponent {
    pub(crate) host:      Arc<ScriptingHost>,
    pub(crate) handle:    isize,
    pub(crate) type_name: String,
}

impl ScriptComponent {
    /// CLR 上でスクリプトを生成して返す。生成に失敗した場合は None。
    pub fn new(host: Arc<ScriptingHost>, type_name: impl Into<String>) -> Option<Self> {
        let type_name = type_name.into();
        let bytes     = type_name.as_bytes();
        let handle    = unsafe { (host.create_fn)(bytes.as_ptr(), bytes.len() as i32) };
        if handle == 0 { return None; }
        Some(Self { host, handle, type_name })
    }

    pub fn type_name(&self) -> &str { &self.type_name }

    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> ScriptComponentData {
        ScriptComponentData { type_name: self.type_name.clone() }
    }
}

impl Drop for ScriptComponent {
    fn drop(&mut self) {
        unsafe { (self.host.destroy_fn)(self.handle); }
    }
}

impl Component for ScriptComponent {}

// ─── PlaceholderScriptSlot ────────────────────────────────────────────────────

/// CLR 不使用のエディタモードでスクリプトスロットを保持するフォールバック。
/// スクリプトパスのみ保持し、CLR 起動後に ScriptComponent へ変換する。
pub struct PlaceholderScriptSlot {
    pub script_path: String,
}

impl PlaceholderScriptSlot {
    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> ScriptComponentData {
        ScriptComponentData { type_name: self.script_path.clone() }
    }
}

impl Component for PlaceholderScriptSlot {}
