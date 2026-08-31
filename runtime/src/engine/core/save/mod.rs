// ============================================================
//  save/mod.rs — セーブデータ（永続キー・バリューストア）
//
//  【役割】
//  C# スクリプトから使う `SEED.SaveData` の実体。資金・強化レベル・図鑑・
//  ハイスコアといった「シーンをまたいで残したいゲーム進行データ」を
//  キー・バリュー形式で保持し、JSON 1 ファイルへ永続化する。
//
//  【構成（単一責任で分割）】
//  - `path`  : 保存先パスの決定だけを担う純関数群（副作用なし・テスト可能）
//  - `store` : キー・バリューの保持と JSON との相互変換
//  - `mod`   : プロセス全体で 1 つのストアを共有するグローバル層
//
//  【所有権と生存期間】
//  ストアはプロセスグローバル（`Mutex<SaveStore>`）。最初のアクセス時に
//  ファイルから遅延ロードし、以降はメモリ上の値を読み書きする。
//  ディスクへの書き出しは次のタイミングだけ:
//    1. スクリプトが `SaveData.Save()` を呼んだとき（明示保存）
//    2. Play 終了（Edit 復帰）時 / アプリ終了時の自動フラッシュ（保険）
//
//  【Play を抜けても揮発させない理由】
//  セーブデータは「ゲームの進行」であって「シーンの編集データ」ではない。
//  Edit へ戻したときに巻き戻すと、エディタで Play を挟むたびに進行が消え、
//  実行ファイル版と挙動が食い違う。よってストアは Play/Edit の切り替えで
//  クリアせず、実ファイルと同期し続ける（消すのは `DeleteAll` だけ）。
// ============================================================

pub mod path;
pub mod store;

use std::path::PathBuf;
use std::sync::{Mutex, OnceLock};

pub use path::resolve_save_path;
pub use store::{SaveStore, SaveValue};

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
        Mutex::new(SaveStore::load_or_empty(path))
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
pub fn save() -> bool {
    match store().flush() {
        Ok(()) => true,
        Err(e) => {
            eprintln!("[SEED SAVE] flush failed: {e}");
            false
        }
    }
}

/// 変更がある場合のみディスクへ書き出す（自動保存用）。
///
/// Play 終了時・アプリ終了時に呼ぶ。ストアが未初期化（1 度もアクセス
/// されていない）なら何もしない — セーブを使わないプロジェクトで
/// 空ファイルを作らないため。
pub fn flush_if_dirty() {
    let Some(m) = SAVE_STORE.get() else { return };
    let mut s = m.lock().unwrap_or_else(|e| e.into_inner());
    if s.is_dirty() {
        if let Err(e) = s.flush() {
            eprintln!("[SEED SAVE] auto flush failed: {e}");
        }
    }
}
