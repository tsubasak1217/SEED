// ============================================================
//  game_tools/src/lib.rs — ゲーム運用ツールプラグイン
//
//  【役割】
//  ECS コンポーネントを提供しない「ツール専用」プラグイン。
//  plugin.json の `editor_menus` でエディタのメニューバーへ項目を宣言し、
//  クリック時に `on_editor_action` が呼ばれる。
//
//  【追加できるメニューの増やし方（データドリブン）】
//  1. plugin.json の editor_menus に項目（id / label / confirm）を足す
//  2. このファイルの ACTION_* 定数と on_editor_action の分岐を 1 つ足す
//  エディタ側 UI のコードは触らなくてよい。
//
//  【アクション一覧】
//  - `delete_save` … セーブデータ（ユーザーデータ）を全削除して即保存する
// ============================================================

use seed_plugin_api::{Plugin, PluginFieldDef, PluginHost};

// ============================================================
//  定数（マジックナンバー / マジックストリングの排除）
// ============================================================

/// プラグイン識別名。plugin.json の `name` と必ず一致させること。
const PLUGIN_NAME: &str = "GameTools";

/// プラグインのバージョン。plugin.json の `version` と揃える。
const PLUGIN_VERSION: &str = "0.1.0";

/// エディタのプラグイン一覧に出る説明文。
const PLUGIN_DESCRIPTION: &str =
    "ゲーム運用ツール。エディタの「Game」メニューからユーザーデータを削除できます。";

/// アクション ID: セーブデータ削除。plugin.json の `items[].id` と一致させること。
const ACTION_DELETE_SAVE: &str = "delete_save";

/// セーブデータ削除の成功ログ文言。
const LOG_DELETE_SAVE_OK: &str = "ユーザーデータ（セーブデータ）を削除しました。";

// ============================================================
//  GameToolsPlugin
// ============================================================

/// ゲーム運用ツールのプラグイン本体。
///
/// 状態を持たない（各アクションはホスト API を呼ぶだけ）ため、
/// フィールドなしのユニット構造体で足りる。
struct GameToolsPlugin;

impl Plugin for GameToolsPlugin {
    /// プラグイン識別名（plugin.json の name と一致）。
    fn name(&self) -> &str {
        PLUGIN_NAME
    }

    /// バージョン文字列。
    fn version(&self) -> &str {
        PLUGIN_VERSION
    }

    /// エディタ表示用の説明文。
    fn description(&self) -> &str {
        PLUGIN_DESCRIPTION
    }

    /// インスペクタへ公開するフィールドはない（ツール専用プラグインのため）。
    ///
    /// 空を返すと ECS コンポーネントとして貼り付けても意味がないが、
    /// メニュー機能はコンポーネントの有無と無関係に動作する。
    fn field_defs(&self) -> Vec<PluginFieldDef> {
        Vec::new()
    }

    /// エディタメニューの項目が実行されたときのディスパッチ。
    ///
    /// - `id`:   plugin.json の `editor_menus[].items[].id`
    /// - `host`: エンジン機能の呼び出し窓口
    ///
    /// 未知の id は `Err` を返し、エディタ側にエラーとして表示させる。
    fn on_editor_action(&mut self, id: &str, host: &mut dyn PluginHost) -> Result<(), String> {
        match id {
            // ── ユーザーデータ削除 ────────────────────────────────
            // ホスト側でメモリ上のストアを空にしたうえで即保存するため、
            // Play 停止時の自動保存でデータが復活することはない。
            ACTION_DELETE_SAVE => {
                host.delete_save_data()?;
                host.log(LOG_DELETE_SAVE_OK);
                Ok(())
            }
            // ── 未知のアクション ─────────────────────────────────
            // plugin.json にだけ項目を足して実装を忘れた場合にここへ来る。
            other => Err(format!(
                "{PLUGIN_NAME}: 未実装のアクションです: {other}"
            )),
        }
    }
}

// ============================================================
//  DLL エントリポイント
// ============================================================

/// ランタイムが `libloading` で呼び出すプラグイン生成関数。
///
/// # Safety
/// 返り値は `Box<Box<dyn Plugin>>` の生ポインタ。
/// ランタイム（`PluginRegistry::load_dll`）が `Box::from_raw` で回収して所有する。
/// エンジンと同一の Rust ツールチェーンでビルドすること（ABI 互換性のため）。
#[unsafe(no_mangle)]
pub extern "C" fn seed_create_plugin() -> *mut std::ffi::c_void {
    let plugin: Box<dyn Plugin> = Box::new(GameToolsPlugin);
    Box::into_raw(Box::new(plugin)) as *mut std::ffi::c_void
}
