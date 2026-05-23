// ============================================================
//  plugin/mod.rs — プラグインシステムの基盤型
//
//  【設計】
//  Plugin トレイト・PluginFieldKind・PluginFieldDef は `seed-plugin-api` クレートで
//  定義し、このモジュールで re-export する。
//
//  runtime と各プラグイン DLL が同じ `seed-plugin-api` クレートに依存することで
//  vtable レイアウトの互換性を保証する（同一ツールチェーン前提）。
//
//  DLL が export する関数:
//    #[no_mangle]
//    pub extern "C" fn seed_create_plugin() -> *mut std::ffi::c_void
// ============================================================

pub mod registry;
pub mod manifest;

// ── seed-plugin-api の型を engine 内から使いやすいよう re-export ──

/// プラグインのフィールド型定義（Float/Int/String/Bool/Color/FilePath/Enum）。
pub use seed_plugin_api::PluginFieldKind;

/// プラグインがエディタに公開するフィールド定義。
pub use seed_plugin_api::PluginFieldDef;

/// エンジンプラグインのインターフェーストレイト。
pub use seed_plugin_api::Plugin;

/// プラグイン DLL の生成関数型エイリアス。
pub use seed_plugin_api::PluginCreateFn;

/// プラグイン DLL のエントリ関数名（null 終端バイト列）。
pub use seed_plugin_api::PLUGIN_ENTRY_FN;
