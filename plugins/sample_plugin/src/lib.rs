// ============================================================
//  sample_plugin/src/lib.rs — サンプルプラグイン実装
//
//  【目的】
//  プラグインシステムの動作確認用。
//  Float / Bool / String / Color / Enum の全フィールド種別をひと通り備え、
//  インスペクタ UI が正しく生成されることをテストする。
//
//  【DLL ビルド】
//  cargo build -p sample_plugin
//  → target/debug/sample_plugin.dll が生成される。
//
//  【配置】
//  runtime/plugins/SamplePlugin/
//      sample_plugin.dll
//      plugin.json
// ============================================================

use seed_plugin_api::{Plugin, PluginFieldDef, PluginFieldKind};

// ============================================================
//  SamplePlugin — プラグイン実装
// ============================================================

/// サンプルプラグイン。
/// 各フィールド種別の動作確認用として全種類のフィールドを公開する。
pub struct SamplePlugin;

impl Plugin for SamplePlugin {
    /// プラグイン識別名（plugin.json の name と一致させること）。
    fn name(&self) -> &str {
        "SamplePlugin"
    }

    /// バージョン文字列。
    fn version(&self) -> &str {
        "0.1.0"
    }

    /// エディタ UI に表示される説明文。
    fn description(&self) -> &str {
        "プラグインシステムのテスト用サンプルプラグイン。"
    }

    /// エディタインスペクタに表示するフィールド定義の一覧を返す。
    fn field_defs(&self) -> Vec<PluginFieldDef> {
        vec![
            // ── Float フィールド ──────────────────────────────────
            PluginFieldDef {
                key: "speed".to_string(),
                label: "移動速度".to_string(),
                kind: PluginFieldKind::Float {
                    min: 0.0,
                    max: 50.0,
                    step: 0.5,
                },
                default_value: "5.0".to_string(),
                tooltip: "アクターの移動速度（m/s）".to_string(),
            },
            // ── Int フィールド ────────────────────────────────────
            PluginFieldDef {
                key: "max_health".to_string(),
                label: "最大 HP".to_string(),
                kind: PluginFieldKind::Int { min: 1, max: 9999 },
                default_value: "100".to_string(),
                tooltip: "アクターの最大体力値".to_string(),
            },
            // ── Bool フィールド ───────────────────────────────────
            PluginFieldDef {
                key: "enable_gravity".to_string(),
                label: "重力を有効化".to_string(),
                kind: PluginFieldKind::Bool,
                default_value: "true".to_string(),
                tooltip: "チェックで重力の影響を受ける".to_string(),
            },
            // ── String フィールド ─────────────────────────────────
            PluginFieldDef {
                key: "tag".to_string(),
                label: "タグ".to_string(),
                kind: PluginFieldKind::String { max_len: 64 },
                default_value: "Player".to_string(),
                tooltip: "衝突判定などに使うタグ名".to_string(),
            },
            // ── Color フィールド ──────────────────────────────────
            PluginFieldDef {
                key: "tint_color".to_string(),
                label: "色合い".to_string(),
                kind: PluginFieldKind::Color,
                default_value: "1.0,1.0,1.0,1.0".to_string(),
                tooltip: "アクターのティントカラー（R,G,B,A: 0.0〜1.0）".to_string(),
            },
            // ── Enum フィールド ───────────────────────────────────
            PluginFieldDef {
                key: "faction".to_string(),
                label: "陣営".to_string(),
                kind: PluginFieldKind::Enum {
                    options: vec![
                        "プレイヤー".to_string(),
                        "味方".to_string(),
                        "敵".to_string(),
                        "中立".to_string(),
                    ],
                },
                default_value: "0".to_string(),
                tooltip: "このアクターの所属陣営".to_string(),
            },
        ]
    }

    /// フィールド値が変更されたときに呼ばれるコールバック。
    /// ここでバリデーションや副作用処理を行える。
    fn on_field_changed(&self, key: &str, old_value: &str, new_value: &str) {
        eprintln!("[SamplePlugin] フィールド変更: key={key}, {old_value} → {new_value}");
    }
}

// ============================================================
//  DLL エントリポイント
// ============================================================

/// エンジンが DLL ロード時に呼び出すエントリポイント。
///
/// `Box<Box<dyn Plugin>>` を raw pointer として返す。
/// エンジン側が `Box::from_raw` で回収して `Box<dyn Plugin>` を取り出す。
///
/// # Safety
/// - 同一の Rust ツールチェーンでコンパイルされていること。
/// - エンジン側の `seed_plugin_api` と同じバージョンを使用していること。
#[unsafe(no_mangle)]
pub extern "C" fn seed_create_plugin() -> *mut std::ffi::c_void {
    let plugin: Box<dyn Plugin> = Box::new(SamplePlugin);
    Box::into_raw(Box::new(plugin)) as *mut std::ffi::c_void
}
