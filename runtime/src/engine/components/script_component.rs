// ============================================================
//  script_component.rs — ScriptComponent / PlaceholderScriptSlot
//
//  ScriptComponent は C# CLR へのブリッジコンポーネント。
//  毎フレームのライフサイクル呼び出しは
//  engine::systems::script_system の ScriptSystem 群が担う。
//
//  PlaceholderScriptSlot はエディタモード（CLR 不使用）のフォールバック。
//  スクリプトのパスとフィールド値だけ保持し、シリアライズ時に復元できる。
// ============================================================

use std::collections::BTreeMap;
use std::sync::Arc;
use serde::{Deserialize, Serialize};
use crate::engine::ecs::Component;
use crate::engine::ecs::schedule::Phase;
use crate::engine::core::clock::FrameContext;
use crate::engine::core::scripting::{ScriptingHost, RawFrameContext};

// ─── ScriptComponentData ──────────────────────────────────────────────────────

/// シリアライズ用データ。
///
/// - type_name : スクリプトの .cs ファイルパス、または C# 型名
/// - fields    : [SerializeField] フィールドの値（フィールド名 → 文字列値）。
///               BTreeMap を使いシリアライズ順を安定させる。
#[derive(Serialize, Deserialize, Clone)]
pub struct ScriptComponentData {
    pub type_name: String,
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub fields: BTreeMap<String, String>,
}

// ─── ScriptComponent ──────────────────────────────────────────────────────────

/// C# スクリプトを保持するコンポーネント。
///
/// ライフサイクル（begin_frame, update など）の呼び出しは
/// ScriptSystem が World をクエリして行う（run_phase 参照）。
pub struct ScriptComponent {
    pub(crate) host:      Arc<ScriptingHost>,
    pub(crate) handle:    isize,
    pub(crate) type_name: String,
    /// [SerializeField] フィールドの現在値（シリアライズ・再生成時の復元用）。
    pub fields: BTreeMap<String, String>,
}

impl ScriptComponent {
    /// CLR 上でスクリプトを生成して返す。生成に失敗した場合は None。
    pub fn new(host: Arc<ScriptingHost>, type_name: impl Into<String>) -> Option<Self> {
        let type_name = type_name.into();
        let bytes     = type_name.as_bytes();
        let handle    = unsafe { (host.create_fn)(bytes.as_ptr(), bytes.len() as i32) };
        if handle == 0 { return None; }
        Some(Self { host, handle, type_name, fields: BTreeMap::new() })
    }

    /// フィールド値付きでスクリプトを生成する（シーンロード・リロード時の復元用）。
    pub fn new_with_fields(
        host:      Arc<ScriptingHost>,
        type_name: impl Into<String>,
        fields:    BTreeMap<String, String>,
    ) -> Option<Self> {
        let mut sc = Self::new(host, type_name)?;
        for (name, value) in &fields {
            sc.apply_field_ffi(name, value);
        }
        sc.fields = fields;
        Some(sc)
    }

    pub fn type_name(&self) -> &str { &self.type_name }

    /// [SerializeField] フィールドの値を設定し、CLR インスタンスにも即時反映する。
    pub fn set_field(&mut self, name: &str, value: &str) {
        self.apply_field_ffi(name, value);
        self.fields.insert(name.to_string(), value.to_string());
    }

    /// CLR インスタンスへフィールド値を FFI 経由で書き込む（内部用）。
    fn apply_field_ffi(&self, name: &str, value: &str) {
        let n = name.as_bytes();
        let v = value.as_bytes();
        unsafe {
            (self.host.set_field_fn)(
                self.handle,
                n.as_ptr(), n.len() as i32,
                v.as_ptr(), v.len() as i32,
            );
        }
    }

    /// 指定フェーズに対応するライフサイクルメソッドを CLR 側で実行する。
    /// ScriptSystem から毎フレーム呼ばれる。
    pub fn run_phase(&self, phase: Phase, ctx: &FrameContext) {
        let raw = RawFrameContext::from(ctx);
        let f = match phase {
            Phase::BeginFrame     => self.host.begin_frame_fn,
            Phase::EarlyUpdate    => self.host.early_update_fn,
            Phase::Update         => self.host.update_fn,
            Phase::ConstantUpdate => self.host.constant_update_fn,
            Phase::LateUpdate     => self.host.late_update_fn,
            Phase::Render         => self.host.render_fn,
            Phase::EndFrame       => self.host.end_frame_fn,
        };
        unsafe { f(self.handle, &raw); }
    }

    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> ScriptComponentData {
        ScriptComponentData {
            type_name: self.type_name.clone(),
            fields:    self.fields.clone(),
        }
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
/// スクリプトパスとフィールド値のみ保持し、CLR 起動後に ScriptComponent へ変換する。
pub struct PlaceholderScriptSlot {
    pub script_path: String,
    /// [SerializeField] フィールドの値（ラウンドトリップ保存用）。
    pub fields: BTreeMap<String, String>,
}

impl PlaceholderScriptSlot {
    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> ScriptComponentData {
        ScriptComponentData {
            type_name: self.script_path.clone(),
            fields:    self.fields.clone(),
        }
    }
}

impl Component for PlaceholderScriptSlot {}
