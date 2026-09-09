// ============================================================
//  plugin/host.rs — PluginHost のランタイム側実装
//
//  【役割】
//  プラグイン DLL は `seed-plugin-api` にしか依存できないため、
//  エンジン機能（セーブデータ操作・ログ）はトレイト越しに間接呼び出しする。
//  そのトレイト `PluginHost` の実体をここに置く。
//
//  【単一責任】
//  この型は「プラグインからの要求をエンジンの既存 API へ橋渡しする」ことだけを行い、
//  自前の状態やビジネスロジックは持たない。
// ============================================================

use seed_plugin_api::PluginHost;

use crate::engine::core::save;

/// ログ出力の接頭辞。エディタの Output パネルで発生源を判別できるようにする。
const PLUGIN_LOG_PREFIX: &str = "[Plugin]";

// ============================================================
//  RuntimePluginHost
// ============================================================

/// ランタイムが `Plugin::on_editor_action` へ貸し出すホスト実装。
///
/// 状態を持たない（エンジンのグローバル API を叩くだけ）ため、
/// 呼び出しごとに使い捨てで生成してよい。
pub struct RuntimePluginHost;

impl RuntimePluginHost {
    /// ホストを生成する。
    pub fn new() -> Self {
        Self
    }
}

impl Default for RuntimePluginHost {
    fn default() -> Self {
        Self::new()
    }
}

impl PluginHost for RuntimePluginHost {
    /// プラグインからのログを標準エラーへ流す（エディタが拾って Output に出す）。
    fn log(&mut self, message: &str) {
        eprintln!("{PLUGIN_LOG_PREFIX} {message}");
    }

    /// セーブデータを全削除して即座にディスクへ書き出す。
    ///
    /// 【メモリ上のストアも空にする理由】
    /// ファイルだけ消しても、プロセス内のストアには値が残っている。
    /// その状態で Play を停止すると自動フラッシュ（`flush_if_dirty`）が走り、
    /// 消したはずのデータがそのまま書き戻ってしまう。
    /// `delete_all()` はメモリ上のストアを空にするため、この復活が起きない。
    fn delete_save_data(&mut self) -> Result<(), String> {
        // 1. メモリ上のキーをすべて削除する（この時点で dirty になる）
        save::delete_all();

        // 2. 空になった状態を即座に書き出す（明示保存。dirty でなくても必ず書く）
        if save::save() {
            Ok(())
        } else {
            Err("セーブファイルの書き出しに失敗しました".to_string())
        }
    }
}
