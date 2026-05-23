// ============================================================
//  seed-plugin-api/src/lib.rs — プラグイン API 公開インターフェース
//
//  【設計】
//  このクレートは runtime と各プラグイン DLL の両方が依存することで
//  Plugin トレイトの vtable レイアウト互換性を保証する。
//
//  プラグイン DLL 作成者はこのクレートだけに依存すればよく、
//  エンジン本体（SEED ランタイム）の全依存関係を引き込まずに済む。
//
//  # プラグイン DLL の最低限の実装
//
//  ```rust
//  use seed_plugin_api::{Plugin, PluginFieldDef, PluginFieldKind};
//
//  struct MyPlugin;
//
//  impl Plugin for MyPlugin {
//      fn name(&self)        -> &str { "MyPlugin" }
//      fn version(&self)     -> &str { "0.1.0" }
//      fn description(&self) -> &str { "My plugin description." }
//      fn field_defs(&self)  -> Vec<PluginFieldDef> { vec![] }
//  }
//
//  #[no_mangle]
//  pub extern "C" fn seed_create_plugin() -> *mut std::ffi::c_void {
//      let plugin: Box<dyn Plugin> = Box::new(MyPlugin);
//      Box::into_raw(Box::new(plugin)) as *mut std::ffi::c_void
//  }
//  ```
// ============================================================

use serde::{Deserialize, Serialize};

// ============================================================
//  PluginFieldKind — インスペクタフィールドの型定義
// ============================================================

/// プラグインがエディタインスペクタに公開するフィールドの型。
///
/// serde の tag/content 形式で JSON にシリアライズされる:
/// `{"type": "Float", "params": {"min": 0.0, "max": 1.0, "step": 0.01}}`
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "type", content = "params")]
pub enum PluginFieldKind {
    /// 浮動小数点数スライダー
    Float { min: f32, max: f32, step: f32 },
    /// 整数スライダー
    Int { min: i32, max: i32 },
    /// テキスト入力（最大文字数制限あり）
    String { max_len: usize },
    /// チェックボックス（true/false）
    Bool,
    /// RGBA カラーピッカー（各成分 0.0〜1.0, "r,g,b,a" 形式で保存）
    Color,
    /// ファイルパス入力（filter は ".png;.jpg" 形式のダイアログフィルタ）
    FilePath { filter: std::string::String },
    /// ドロップダウン選択（options のインデックスを文字列で保存）
    Enum { options: Vec<std::string::String> },
}

// ============================================================
//  PluginFieldDef — フィールド定義（プラグインが宣言する）
// ============================================================

/// プラグインがエディタに公開するフィールドの定義。
/// `Plugin::field_defs()` で返す。
///
/// # JSON シリアライズ例
/// ```json
/// {
///   "key": "speed",
///   "label": "移動速度",
///   "kind": {"type": "Float", "params": {"min": 0.0, "max": 100.0, "step": 0.1}},
///   "default_value": "1.0",
///   "tooltip": "アクターの移動速度"
/// }
/// ```
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PluginFieldDef {
    /// フィールドの識別キー（シリアライズ・IPC で使用）
    pub key:           std::string::String,
    /// エディタ表示ラベル
    pub label:         std::string::String,
    /// フィールドの型と制約
    pub kind:          PluginFieldKind,
    /// デフォルト値（文字列表現）
    pub default_value: std::string::String,
    /// ツールチップ説明文（空文字列可）
    #[serde(default, skip_serializing_if = "std::string::String::is_empty")]
    pub tooltip:       std::string::String,
}

// ============================================================
//  Plugin トレイト
// ============================================================

/// エンジンプラグインのインターフェース。
///
/// `Send + Sync` を要求するため、実装構造体も同様にスレッドセーフである必要がある。
///
/// # DLL エクスポート
///
/// プラグイン DLL は `seed_create_plugin()` を `no_mangle` + `extern "C"` で
/// エクスポートする必要がある。
/// エンジンと DLL は同一の Rust ツールチェーンでコンパイルすること（ABI 互換性のため）。
pub trait Plugin: Send + Sync {
    /// プラグイン識別名（plugin.json の name フィールドと一致させること）
    fn name(&self) -> &str;

    /// バージョン文字列（semver 推奨）
    fn version(&self) -> &str;

    /// プラグインの説明（エディタ UI に表示される）
    fn description(&self) -> &str;

    /// エディタインスペクタに表示するフィールド定義の一覧を返す。
    ///
    /// フィールドの現在値は `PluginComponent.fields` に保存される。
    /// 変更時は `on_field_changed()` が呼ばれる。
    fn field_defs(&self) -> Vec<PluginFieldDef>;

    /// フィールド値が変更されたときに呼ばれるコールバック。
    ///
    /// - `key`:       変更されたフィールドのキー
    /// - `old_value`: 変更前の値（文字列表現）
    /// - `new_value`: 変更後の値（文字列表現）
    ///
    /// オーバーライドしてバリデーション・副作用処理を行える。
    /// デフォルト実装は何もしない。
    fn on_field_changed(&self, _key: &str, _old_value: &str, _new_value: &str) {}
}

// ============================================================
//  DLL エクスポート関数の型エイリアスと定数
// ============================================================

/// プラグイン DLL が export する生成関数の型。
///
/// DLL 側の実装例:
/// ```rust,ignore
/// #[unsafe(no_mangle)]
/// pub extern "C" fn seed_create_plugin() -> *mut std::ffi::c_void {
///     let plugin: Box<dyn seed_plugin_api::Plugin> = Box::new(MyPlugin);
///     Box::into_raw(Box::new(plugin)) as *mut std::ffi::c_void
/// }
/// ```
pub type PluginCreateFn = unsafe extern "C" fn() -> *mut std::ffi::c_void;

/// プラグイン DLL が export する関数名（null 終端バイト列）。
pub const PLUGIN_ENTRY_FN: &[u8] = b"seed_create_plugin\0";
