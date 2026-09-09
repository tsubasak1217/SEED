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
//  PluginHost — プラグインからエンジン機能を呼ぶためのホスト API
// ============================================================

/// プラグイン DLL からエンジン（ランタイム）側の機能を呼び出すための窓口。
///
/// 【なぜトレイトで渡すのか】
/// プラグイン DLL はエンジン本体のクレートに依存できない（依存の肥大化と
/// ABI の結合を避けるため、依存先は `seed-plugin-api` のみ）。
/// そのため「エンジンにやってほしいこと」はこのトレイト越しに間接呼び出しする。
/// 実装はランタイム側に置き、`&mut dyn PluginHost` として貸し出す。
///
/// 【ライフタイム】
/// ホスト参照はコールバック（`Plugin::on_editor_action` など）の呼び出し中だけ
/// 有効。プラグイン側で保持してはならない。
///
/// 【拡張時の注意】
/// トレイトにメソッドを追加するときは必ず既定実装を与えるか、
/// runtime と全プラグインを同時に再ビルドすること（vtable レイアウトが変わるため）。
pub trait PluginHost {
    /// ランタイムのログへ 1 行出力する（エディタの Output パネルに流れる）。
    fn log(&mut self, message: &str);

    /// セーブデータ（`SEED.SaveData` の永続ストア）を全削除して即座に保存する。
    ///
    /// メモリ上のストアも空にするため、Play 停止時の自動保存で復活しない。
    /// 成功なら `Ok(())`、書き出しに失敗した場合は理由を `Err` で返す。
    fn delete_save_data(&mut self) -> Result<(), String>;

    /// セーブデータ（`SEED.SaveData` の永続ストア）の **整数キーを 1 つ書き換えて**
    /// 即座に保存する。他のキー（図鑑・所持金など）はそのまま残る。
    ///
    /// 【なぜ整数だけなのか】
    /// C# 側の `SEED.SaveData.SetBool(key, value)` は
    /// `SetInt(key, value ? 1 : 0)` の別名であり、真偽値も整数として保存される
    /// （`scripting/src/Api/SaveData.cs`）。したがって整数の書き込みが 1 本あれば
    /// 「進行フラグを立てる」「所持金を書き換える」の両方をゲーム側の読み出しと
    /// 完全に同じ形式で行える。
    ///
    /// 【ファイルが無いとき】
    /// ストアは空の状態から始まり、保存時に親ディレクトリごと新規作成される。
    ///
    /// # 引数
    /// - `key`:   セーブキー（ゲーム側の定数と完全に一致させること）
    /// - `value`: 書き込む整数値（真偽値なら true=1 / false=0）
    ///
    /// # 戻り値
    /// 書き出しに成功したら `Ok(())`、失敗したら理由を `Err`。
    fn set_save_int(&mut self, key: &str, value: i64) -> Result<(), String>;
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

    /// エディタのメニュー項目（plugin.json の `editor_menus`）が実行されたときに呼ばれる。
    ///
    /// - `id`:   実行された項目の `items[].id`
    /// - `host`: エンジン機能を呼ぶためのホスト API（呼び出し中のみ有効）
    ///
    /// 戻り値はエディタへそのまま返り、`Ok` なら成功トースト、
    /// `Err(reason)` なら理由付きのエラー表示になる。
    /// 既定実装は「未知のアクション」としてエラーを返す
    /// （メニューを宣言したのに実装を忘れた場合に気付けるようにするため）。
    fn on_editor_action(&mut self, id: &str, _host: &mut dyn PluginHost) -> Result<(), String> {
        Err(format!("{UNKNOWN_ACTION_MESSAGE}: {id}"))
    }
}

/// `on_editor_action` の既定実装が返すエラー文言の先頭部分。
/// エディタ側のログと突き合わせられるよう定数化しておく。
pub const UNKNOWN_ACTION_MESSAGE: &str = "unknown action";

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
