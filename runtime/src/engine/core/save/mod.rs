// ============================================================
//  save/mod.rs — セーブデータ（永続キー・バリューストア）
//
//  【役割】
//  C# スクリプトから使う `SEED.SaveData` の実体。資金・強化レベル・図鑑・
//  ハイスコア・履歴といった「シーンをまたいで残したいゲーム進行データ」を
//  キー・バリュー形式で保持し、JSON 1 ファイルへ永続化する。
//
//  【構成（単一責任で分割）】
//  - `path`         : 保存先パスの決定だけを担う純関数群（副作用なし・テスト可能）
//  - `value`        : 1 つのキーに入る値の型と読み替え規則
//  - `codec`        : キー・バリューと本文（JSON）の相互変換・壊れた本文の判定
//  - `file_set`     : 本体・一時・1 世代前・壊れた本体のファイル名
//  - `durable_file` : 本体を「消えない・壊れない」順序で置き換える書き出し（sync・rename）
//  - `recovery`     : 読み込みと、本体が無い・壊れているときの 1 世代前への切り替え
//  - `batch`        : SaveData.Batch の深さと、待たせた書き出しの要求
//  - `store`        : 上を束ねるストア本体（いつ書くかを決める）
//  - `mod`          : プロセス全体で 1 つのストアを共有するグローバル層
//
//  【所有権と生存期間】
//  ストアはプロセスグローバル（`Mutex<SaveStore>`）。最初のアクセス時に
//  ファイルから遅延ロードし、以降はメモリ上の値を読み書きする。
//  ディスクへの書き出しは次のタイミングだけ:
//    1. スクリプトが `SaveData.Save()` を呼んだとき（明示保存）
//    2. Play 終了（Edit 復帰）時 / アプリ終了時の自動フラッシュ（保険）
//    3. Android: バックグラウンドへ回るとき（suspended。app/background_lifecycle.rs）と、
//       Activity の破棄でプロセスを終える直前（MainActivity.onDestroy → JNI。保険）の自動フラッシュ。
//       Android はアプリを閉じるとプロセスごと即終了し、2 の「アプリ終了時」が来ないため。
//  ただし `SaveData.Batch` の途中は 1〜3 のどれも書かずに要求を覚え、最も外側の Batch の終わりに
//  1 回だけ書く（batch.rs。Batch の状態はストアと同じ Mutex の中にあるので、UI スレッドからの
//  書き出しも Batch の途中の半端な組み合わせを書かない）。
//
//  【耐久性（W1-S。docs/app_platform_roadmap.md §2.7）】
//  書き出しは「.tmp へ書いて sync → 本体を .bak へ rename → .tmp を本体へ rename → フォルダを sync（Android）」、
//  読み込みは「本体 → .bak → 空」。どこで落ちても本体か 1 世代前のどちらかが読める（durable_file.rs の表）。
//
//  【Play を抜けても揮発させない理由】
//  セーブデータは「ゲームの進行」であって「シーンの編集データ」ではない。
//  Edit へ戻したときに巻き戻すと、エディタで Play を挟むたびに進行が消え、
//  実行ファイル版と挙動が食い違う。よってストアは Play/Edit の切り替えで
//  クリアせず、実ファイルと同期し続ける（消すのは `DeleteAll` だけ）。
// ============================================================

pub mod batch;
pub mod codec;
pub mod durable_file;
pub mod file_set;
pub mod path;
pub mod recovery;
pub mod store;
pub mod value;

use std::path::PathBuf;
use std::sync::{Mutex, OnceLock};

pub use path::resolve_save_path;
pub use recovery::LoadSource;
pub use store::{BatchEndOutcome, FlushOutcome, SaveStore};
pub use value::SaveValue;

/// プロセス全体で共有するセーブストア。
///
/// `OnceLock` で遅延初期化する（初回アクセス時にファイルからロード）。
/// `Mutex` は FFI が別スレッドから呼ばれても壊れないようにするためのもの。
static SAVE_STORE: OnceLock<Mutex<SaveStore>> = OnceLock::new();

/// ストアへの排他参照を取得する（未初期化なら保存先を解決してロードする）。
///
/// ロックが毒された（他スレッドが保持中に panic した）場合は
/// 内側の値をそのまま取り出して続行する。セーブデータの整合性より
/// プロセスを落とさないことを優先する。
fn store() -> std::sync::MutexGuard<'static, SaveStore> {
    let m = SAVE_STORE.get_or_init(|| {
        let path: PathBuf = resolve_save_path();
        // 保存先はモードによって変わるため、初回解決時に必ず 1 行残す。
        // 「セーブが消えた／どこに書かれたか分からない」の切り分けが
        // ログ 1 行で済むようにするための恒久ログ。
        eprintln!("[SEED SAVE] save file: {}", path.display());
        Mutex::new(SaveStore::load(path))
    });
    m.lock().unwrap_or_else(|e| e.into_inner())
}

// ── 読み取り ────────────────────────────────────────────────

/// 整数値を読む。キーが無い / 型が変換不能なら `None`。
pub fn get_int(key: &str) -> Option<i64> {
    store().get_int(key)
}

/// 浮動小数値を読む。キーが無い / 型が変換不能なら `None`。
pub fn get_float(key: &str) -> Option<f32> {
    store().get_float(key)
}

/// 文字列値を読む。キーが無い / 値が文字列でないなら `None`。
pub fn get_string(key: &str) -> Option<String> {
    store().get_string(key)
}

/// キーが存在するか（型は問わない）。
pub fn has(key: &str) -> bool {
    store().has(key)
}

/// ロードのときにどこから読んだか（`SaveData.RecoveredFrom`。未ロードならここでロードする）。
pub fn recovered_from() -> LoadSource {
    store().load_source()
}

// ── 書き込み ────────────────────────────────────────────────

/// 整数値を書く（既存の値は型ごと上書きする）。
pub fn set_int(key: &str, value: i64) {
    store().set(key, SaveValue::Int(value));
}

/// 浮動小数値を書く（既存の値は型ごと上書きする）。
pub fn set_float(key: &str, value: f32) {
    store().set(key, SaveValue::Float(value as f64));
}

/// 文字列値を書く（既存の値は型ごと上書きする）。
pub fn set_string(key: &str, value: &str) {
    store().set(key, SaveValue::Str(value.to_string()));
}

/// キーを 1 つ削除する。削除した=true / 元から無かった=false。
pub fn delete_key(key: &str) -> bool {
    store().delete_key(key)
}

/// 全キーを削除する（ファイルはフラッシュするまで残る）。
pub fn delete_all() {
    store().delete_all();
}

// ── 永続化 ──────────────────────────────────────────────────

/// メモリ上の内容をディスクへ書き出す。成功=true。
///
/// 変更が無い（dirty でない）場合も要求どおり書き出す
/// （スクリプトが明示的に `Save()` を呼んだ意図を尊重する）。
/// `SaveData.Batch` の途中なら書かずに要求を覚えて true を返す（最も外側の Batch の終わりに書き、
/// その成否は Batch の戻り値になる）。
pub fn save() -> bool {
    match store().request_save() {
        Ok(_) => true,
        Err(e) => {
            eprintln!("[SEED SAVE] flush failed: {e}");
            false
        }
    }
}

/// 自動保存（`flush_if_dirty`）の結果。呼び出し元がログに残すために返す。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AutoFlushOutcome {
    /// ストアが 1 度も使われていない（セーブを使わないプロジェクト。ファイルも作らない）。
    NotLoaded,
    /// 未書き出しの変更が無かった（書く必要が無い）。
    Clean,
    /// 未書き出しの変更をディスクへ書き出した。
    Written,
    /// `SaveData.Batch` の途中なので書かなかった（最も外側の Batch の終わりに書く）。
    Deferred,
    /// 書き出しに失敗した（理由は標準エラーへ出してある）。
    Failed,
}

impl AutoFlushOutcome {
    /// ログ用の短い説明。
    pub fn describe(self) -> &'static str {
        match self {
            AutoFlushOutcome::NotLoaded => "セーブ未使用（書き出すものなし）",
            AutoFlushOutcome::Clean => "未書き出しの変更なし",
            AutoFlushOutcome::Written => "未書き出しの変更を書き出しました",
            AutoFlushOutcome::Deferred => {
                "SaveData.Batch の途中なので書き出しを Batch の終わりまで待たせました（半端な状態は書きません）"
            }
            AutoFlushOutcome::Failed => "書き出しに失敗しました",
        }
    }
}

/// 変更がある場合のみディスクへ書き出す（自動保存用）。
///
/// Play 終了時・アプリ終了時・バックグラウンドへ回るとき（Android の suspended と、
/// MainActivity.onDestroy からの JNI 呼び出し）に呼ぶ。ストアが未初期化（1 度もアクセス
/// されていない）なら何もしない — セーブを使わないプロジェクトで空ファイルを作らないため。
///
/// ストアは Mutex で守られているので、どのスレッドから呼んでもよい（onDestroy は UI スレッド）。
/// `SaveData.Batch` の途中なら書かない（`Deferred`）。
///
/// # 戻り値
/// 何をしたか（呼び出し元がログに残す。無視してもよい）。
pub fn flush_if_dirty() -> AutoFlushOutcome {
    let Some(m) = SAVE_STORE.get() else { return AutoFlushOutcome::NotLoaded };
    let mut s = m.lock().unwrap_or_else(|e| e.into_inner());
    match s.request_auto_flush() {
        Ok(FlushOutcome::Written) => AutoFlushOutcome::Written,
        Ok(FlushOutcome::Clean) => AutoFlushOutcome::Clean,
        Ok(FlushOutcome::Deferred) => AutoFlushOutcome::Deferred,
        Err(e) => {
            eprintln!("[SEED SAVE] auto flush failed: {e}");
            AutoFlushOutcome::Failed
        }
    }
}

// ── Batch（複数キーの更新を 1 まとまりにする）─────────────────────

/// `SaveData.Batch` を 1 段始める（入れ子は数える）。
pub fn begin_batch() {
    store().begin_batch();
}

/// `SaveData.Batch` を 1 段終える。最も外側が閉じ、途中に書き出しの要求があれば 1 回だけ書く。
///
/// # 戻り値
/// 書き出しが要らなかった・成功した=true。書き出しに失敗した・Batch が開いていなかった=false。
pub fn end_batch() -> bool {
    match store().end_batch() {
        Ok(BatchEndOutcome::NotOpen) => {
            eprintln!("[SEED SAVE] 警告: 開いていない SaveData.Batch を終えようとしました（Begin と End の数が合いません）");
            false
        }
        Ok(BatchEndOutcome::StillOpen | BatchEndOutcome::ClosedWithoutWrite | BatchEndOutcome::ClosedAndWritten) => true,
        Err(e) => {
            eprintln!("[SEED SAVE] flush failed (SaveData.Batch の終わり): {e}");
            false
        }
    }
}
