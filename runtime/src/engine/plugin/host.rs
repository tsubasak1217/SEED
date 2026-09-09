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

/// セーブファイルの書き出しに失敗したときにプラグインへ返す理由文字列。
/// 削除・書き換えのどちらの経路でも同じ文言を使う（メッセージの一元管理）。
const SAVE_FLUSH_FAILED_MESSAGE: &str = "セーブファイルの書き出しに失敗しました";

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
            Err(SAVE_FLUSH_FAILED_MESSAGE.to_string())
        }
    }

    /// セーブデータの整数キーを 1 つ書き換えて即座にディスクへ書き出す。
    ///
    /// 【他のキーが消えない理由】
    /// `save::set_int` はグローバルストアへの書き込みであり、ストアは初回アクセス時に
    /// 既存の save.json を読み込んでから使われる（`save::store()` の遅延ロード）。
    /// そのため図鑑・所持金など既存のキーは読み込まれたまま残り、
    /// 続く `save::save()` で「既存 ＋ 今回の 1 キー」がまとめて書き戻される。
    ///
    /// 【ファイルが無い場合】
    /// 空のストアとして始まり、`SaveStore::flush` が親ディレクトリごと新規作成する。
    ///
    /// 【メモリ上のストアも更新する理由】
    /// `delete_save_data` と同じ理屈で、ファイルだけ書き換えてもプロセス内の
    /// ストアが古いままだと Play 停止時の自動フラッシュで書き戻されてしまう。
    /// `save::set_int` はメモリ上のストアを更新するため、この巻き戻りは起きない。
    fn set_save_int(&mut self, key: &str, value: i64) -> Result<(), String> {
        // 1. メモリ上のストアへ書き込む（既存キーは保持されたまま dirty になる）
        save::set_int(key, value);

        // 2. 変更を即座に書き出す（明示保存。エディタ側へ結果を返すため同期的に行う）
        if save::save() {
            Ok(())
        } else {
            Err(SAVE_FLUSH_FAILED_MESSAGE.to_string())
        }
    }
}

// ============================================================
//  ユニットテスト
// ============================================================

/// `set_save_int` がゲーム側の読み出しと同じ形式で save.json を書けることの検証。
///
/// 【なぜ 1 テストにまとめてあるか】
/// セーブストアは `OnceLock` によるプロセスグローバルであり、初回アクセス時に
/// 保存先が確定して以降は変えられない。テストを分けると実行順によって保存先の
/// 設定が間に合わず不安定になるため、「保存先の設定 → 既存キーの用意 →
/// 書き換え → 検証」を 1 本のシナリオとして通す。
#[cfg(test)]
mod tests {
    use super::*;
    use seed_plugin_api::PluginHost as _;

    /// テスト用のセーブ先ディレクトリ名（`SEED_SAVE_DIR` に渡す）。
    const TEST_SAVE_DIR_NAME: &str = "seed_plugin_host_save_test";

    /// チュートリアル完了フラグのキー（ゲーム側 `GameProgressKeys.TutorialDone` と同じ）。
    const KEY_TUTORIAL_DONE: &str = "tutorial_done";

    /// 真偽値 true の整数表現（C# の `SetBool` は `SetInt(key, 1)`）。
    const VALUE_TRUE: i64 = 1;

    /// 「他のキーが消えないこと」を確かめるために先に入れておく図鑑相当のキー。
    const KEY_OTHER: &str = "fish_record_kani";

    /// 上記キーへ入れておく値（1 以外なら何でもよいので固定値を置く）。
    const VALUE_OTHER: i64 = 42;

    #[test]
    fn set_save_int_writes_flag_and_keeps_other_keys() {
        // ── 1. 保存先をテスト専用の一時ディレクトリへ向ける ──────────
        // ストアの初回アクセスより前に設定する必要がある。
        let dir = std::env::temp_dir().join(TEST_SAVE_DIR_NAME);
        let _ = std::fs::remove_dir_all(&dir);
        // SAFETY: テストはシングルスレッドで完結し、他スレッドは環境変数を読まない。
        unsafe {
            std::env::set_var(save::path::SAVE_DIR_ENV, &dir);
        }

        // ── 2. 既存のセーブデータ（図鑑相当）を用意する ──────────────
        save::set_int(KEY_OTHER, VALUE_OTHER);
        assert!(save::save(), "前提となる初期セーブの書き出しに失敗した");

        // ── 3. ホスト API でチュートリアル完了フラグだけを書き換える ──
        let mut host = RuntimePluginHost::new();
        host.set_save_int(KEY_TUTORIAL_DONE, VALUE_TRUE)
            .expect("set_save_int が失敗した");

        // ── 4. メモリ上のストアが期待どおりか ────────────────────────
        assert_eq!(save::get_int(KEY_TUTORIAL_DONE), Some(VALUE_TRUE));
        assert_eq!(
            save::get_int(KEY_OTHER),
            Some(VALUE_OTHER),
            "既存キーが失われている"
        );

        // ── 5. ディスク上の JSON がゲーム側の読み出し形式か ──────────
        // C# の GetBool は「0 以外を true」と見るので、整数 1 で保存されていること。
        let path = save::resolve_save_path();
        let text = std::fs::read_to_string(&path)
            .unwrap_or_else(|e| panic!("save.json を読めない: {} ({e})", path.display()));
        let json: serde_json::Value =
            serde_json::from_str(&text).expect("save.json が JSON として壊れている");
        assert_eq!(json[KEY_TUTORIAL_DONE], serde_json::json!(VALUE_TRUE));
        assert_eq!(json[KEY_OTHER], serde_json::json!(VALUE_OTHER));

        // ── 6. 後片付け ──────────────────────────────────────────────
        let _ = std::fs::remove_dir_all(&dir);
    }
}
