// ============================================================
//  save/store.rs — セーブデータのキー・バリューストア本体
//
//  【役割】
//  キー（文字列）→ 値（整数 / 浮動小数 / 文字列）のマップをメモリ上に保持し、
//  「いつディスクへ書くか」（明示保存・自動保存・Batch の待たせ）を決める。
//  値の型（value.rs）・本文の変換（codec.rs）・書き出しの順序（durable_file.rs）・
//  読み込みと復旧（recovery.rs）・Batch の状態（batch.rs）はそれぞれの層に任せ、ここは束ねるだけ。
//
//  【書き出しの要求】
//  - `request_save`       … 明示の Save（変更が無くても書く）
//  - `request_auto_flush` … 自動保存（Play 終了・アプリ終了・Android の背面・onDestroy。変更があるときだけ）
//  どちらも Batch の途中なら書かずに要求を覚え、最も外側の `end_batch` で 1 回だけ書く（batch.rs）。
//
//  【復旧したとき】
//  1 世代前から読んだ・読めずに空で始めたときは未書き出し（dirty）として始める。次の書き出しで
//  本体（save.json）を作り直し、以後の起動で毎回「復旧した」と出続けないようにするため。
// ============================================================

use std::collections::BTreeMap;
use std::io;
use std::path::{Path, PathBuf};

use super::batch::{BatchEnd, BatchState, PendingFlush};
use super::codec;
use super::durable_file::{self, PrimaryHandling};
use super::file_set::SaveFileSet;
use super::recovery::{self, LoadSource};
use super::value::SaveValue;

/// ログの行頭（セーブの他のログと同じ印）。
const LOG_TAG: &str = "[SEED SAVE]";

/// 書き出しの要求（明示保存・自動保存）に対して何をしたか。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FlushOutcome {
    /// ディスクへ書いた。
    Written,
    /// 書く必要が無かった（自動保存で、未書き出しの変更が無い）。
    Clean,
    /// Batch の途中なので書かずに要求を覚えた（最も外側の Batch の終わりに書く）。
    Deferred,
}

/// Batch を 1 段終えた結果。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BatchEndOutcome {
    /// Batch が開いていなかった（Begin と End の数が合わない）。何もしていない。
    NotOpen,
    /// まだ外側の Batch が開いている（書き出しは外側の終わりで）。
    StillOpen,
    /// 最も外側の Batch が閉じた。待たせた要求が無かった（または変更が無かった）ので書いていない。
    ClosedWithoutWrite,
    /// 最も外側の Batch が閉じ、待たせていた書き出しを 1 回行った。
    ClosedAndWritten,
}

/// セーブデータのキー・バリューストア。
///
/// キーは `BTreeMap` で保持する（書き出しが常に同じ順序になり、
/// セーブファイルの差分が読める＝手動デバッグしやすいため）。
#[derive(Debug)]
pub struct SaveStore {
    /// 本体（save.json）と仲間のファイル（.tmp・.bak・.corrupt-*）のパス。
    files: SaveFileSet,
    /// キー → 値。
    values: BTreeMap<String, SaveValue>,
    /// 最後の書き出し以降に変更があったか。
    dirty: bool,
    /// 本体の場所に、退避できなかった壊れた本体が残っているか（.bak へ回してはいけない）。
    primary_untrusted: bool,
    /// ロードのときにどこから読んだか（`SaveData.RecoveredFrom`）。
    load_source: LoadSource,
    /// Batch の深さと、待たせている書き出しの要求。
    batch: BatchState,
}

impl SaveStore {
    /// 空のストアを作る（ファイルは読まない。テスト用）。
    #[cfg(test)]
    pub fn new_empty(path: PathBuf) -> Self {
        Self {
            files: SaveFileSet::new(path),
            values: BTreeMap::new(),
            dirty: false,
            primary_untrusted: false,
            load_source: LoadSource::Fresh,
            batch: BatchState::default(),
        }
    }

    /// ファイルからロードする（本体 → 1 世代前 → 空の順。recovery.rs）。どの場合も失敗させない。
    ///
    /// 壊れたファイルでゲームが起動しなくなるほうが害が大きいので、読めなければ 1 世代前か空で始める。
    /// 壊れた本体は上書きせずに `save.json.corrupt-<時刻>` として残る。
    pub fn load(path: PathBuf) -> Self {
        let files = SaveFileSet::new(path);
        let outcome = recovery::load(&files);
        Self {
            files,
            values: outcome.values,
            // 復旧したときは次の書き出しで本体を作り直す（ファイル先頭のコメント）
            dirty: outcome.source.is_recovery(),
            primary_untrusted: outcome.primary_untrusted,
            load_source: outcome.source,
            batch: BatchState::default(),
        }
    }

    /// 保存先（本体）のパス。
    pub fn path(&self) -> &Path {
        self.files.primary()
    }

    /// ロードのときにどこから読んだか。
    pub fn load_source(&self) -> LoadSource {
        self.load_source
    }

    /// 最後の書き出し以降に変更があったか。
    pub fn is_dirty(&self) -> bool {
        self.dirty
    }

    /// 保持しているキー数（テスト・診断用）。
    #[allow(dead_code)] // 現状はユニットテストからのみ使う診断アクセサ
    pub fn len(&self) -> usize {
        self.values.len()
    }

    /// キーが 1 つも無いか。
    #[allow(dead_code)] // 現状はユニットテストからのみ使う診断アクセサ
    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }

    // ── 読み取り ─────────────────────────────────────────────

    /// キーが存在するか（型は問わない）。
    pub fn has(&self, key: &str) -> bool {
        self.values.contains_key(key)
    }

    /// 整数として読む。
    pub fn get_int(&self, key: &str) -> Option<i64> {
        self.values.get(key).and_then(SaveValue::as_int)
    }

    /// 実数として読む。
    pub fn get_float(&self, key: &str) -> Option<f32> {
        self.values.get(key).and_then(SaveValue::as_float)
    }

    /// 文字列として読む。
    pub fn get_string(&self, key: &str) -> Option<String> {
        self.values
            .get(key)
            .and_then(SaveValue::as_str)
            .map(str::to_string)
    }

    // ── 書き込み ─────────────────────────────────────────────

    /// 値を書く（同じキーの既存値は型ごと置き換える）。
    ///
    /// 空キーは受け付けない（JSON のキーとしては合法だが、
    /// スクリプト側の変数未初期化バグを黙って通す入り口になるため）。
    pub fn set(&mut self, key: &str, value: SaveValue) {
        if key.is_empty() {
            return;
        }
        // 同値の再代入では dirty を立てない（毎フレーム Set する UI 由来の
        // 無駄なフラッシュを避ける）。
        if self.values.get(key) == Some(&value) {
            return;
        }
        self.values.insert(key.to_string(), value);
        self.dirty = true;
    }

    /// キーを削除する。削除した=true / 元から無かった=false。
    pub fn delete_key(&mut self, key: &str) -> bool {
        let removed = self.values.remove(key).is_some();
        if removed {
            self.dirty = true;
        }
        removed
    }

    /// 全キーを削除する。
    pub fn delete_all(&mut self) {
        if !self.values.is_empty() {
            self.values.clear();
            self.dirty = true;
        }
    }

    // ── 書き出しの要求 ───────────────────────────────────────

    /// 明示の Save。変更が無くても書く（スクリプトが明示的に `Save()` を呼んだ意図を尊重する）。
    /// Batch の途中なら書かずに要求を覚え、最も外側の Batch の終わりに書く。
    pub fn request_save(&mut self) -> io::Result<FlushOutcome> {
        if self.batch.is_open() {
            self.batch.defer(PendingFlush::Always);
            return Ok(FlushOutcome::Deferred);
        }
        self.write_now()?;
        Ok(FlushOutcome::Written)
    }

    /// 自動保存。未書き出しの変更があるときだけ書く。Batch の途中なら書かずに要求を覚える。
    pub fn request_auto_flush(&mut self) -> io::Result<FlushOutcome> {
        if !self.dirty {
            return Ok(FlushOutcome::Clean);
        }
        if self.batch.is_open() {
            self.batch.defer(PendingFlush::IfDirty);
            return Ok(FlushOutcome::Deferred);
        }
        self.write_now()?;
        Ok(FlushOutcome::Written)
    }

    // ── Batch ────────────────────────────────────────────────

    /// Batch を 1 段始める（入れ子は数える）。
    pub fn begin_batch(&mut self) {
        self.batch.begin();
    }

    /// Batch を 1 段終える。最も外側が閉じ、途中に書き出しの要求があったら 1 回だけ書く。
    ///
    /// # 戻り値
    /// 何をしたか。書き出しに失敗したら `Err`（変更は未書き出しのまま残り、次の書き出しで再び試す）。
    pub fn end_batch(&mut self) -> io::Result<BatchEndOutcome> {
        let pending = match self.batch.end() {
            BatchEnd::NotOpen => return Ok(BatchEndOutcome::NotOpen),
            BatchEnd::StillOpen => return Ok(BatchEndOutcome::StillOpen),
            BatchEnd::Closed(pending) => pending,
        };
        let should_write = match pending {
            PendingFlush::None => false,
            PendingFlush::IfDirty => self.dirty,
            PendingFlush::Always => true,
        };
        if !should_write {
            return Ok(BatchEndOutcome::ClosedWithoutWrite);
        }
        self.write_now()?;
        Ok(BatchEndOutcome::ClosedAndWritten)
    }

    // ── 永続化 ───────────────────────────────────────────────

    /// 今の内容をすぐにディスクへ書く（Batch を見ない。呼び出し元が Batch の外であることを確かめる）。
    ///
    /// 本文を組み立て（codec.rs）、本体を置き換える（durable_file.rs の順序）。
    fn write_now(&mut self) -> io::Result<()> {
        let text = codec::encode(&self.values)?;
        let handling = self.primary_handling();
        let report = durable_file::replace_primary(&self.files, text.as_bytes(), handling)?;
        for warning in &report.warnings {
            eprintln!("{LOG_TAG} 警告: {warning}");
        }
        self.dirty = false;
        // 本体は自分で書いた正しい世代になった（次からは .bak へ回してよい）
        self.primary_untrusted = false;
        Ok(())
    }

    /// 今の本体をどう扱って置き換えるかを決める。
    ///
    /// ロードのときに退避できなかった壊れた本体が残っていれば、もう一度退避を試す。
    /// それでも退避できなければ .bak へ回さずに置き換える（壊れた本体で正しい 1 世代前を消さないため）。
    fn primary_handling(&mut self) -> PrimaryHandling {
        if !self.primary_untrusted {
            return PrimaryHandling::RotateToBackup;
        }
        match recovery::quarantine_primary(&self.files) {
            Ok(kept) => {
                eprintln!("{LOG_TAG} 警告: 壊れた本体を {} へ退避しました", kept.display());
                self.primary_untrusted = false;
                PrimaryHandling::RotateToBackup
            }
            Err(e) if e.kind() == io::ErrorKind::NotFound => {
                // もう本体が無い（誰かが消した）。回すものも無い。
                self.primary_untrusted = false;
                PrimaryHandling::RotateToBackup
            }
            Err(e) => {
                eprintln!("{LOG_TAG} 警告: 壊れた本体をまだ退避できません。1 世代前を残したまま本体を置き換えます: {e}");
                PrimaryHandling::ReplaceInPlace
            }
        }
    }
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    /// テスト用の空ストア（パスは実在しなくてよい）。
    fn empty() -> SaveStore {
        SaveStore::new_empty(PathBuf::from("__test__/save.json"))
    }

    /// テストごとの一時フォルダ（終わったら消す。panic しても Drop で消える）。
    struct TestDir(PathBuf);

    impl TestDir {
        /// 他のテスト・他のプロセスと重ならない名前で作る。
        fn new(tag: &str) -> Self {
            let nanos = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0);
            let dir = std::env::temp_dir().join(format!("seed_save_store_{tag}_{}_{nanos}", std::process::id()));
            let _ = fs::remove_dir_all(&dir);
            fs::create_dir_all(&dir).expect("一時フォルダを作れない");
            Self(dir)
        }

        /// このフォルダの save.json のパス。
        fn primary(&self) -> PathBuf {
            self.0.join("save.json")
        }

        /// このフォルダのファイル一式。
        fn files(&self) -> SaveFileSet {
            SaveFileSet::new(self.primary())
        }
    }

    impl Drop for TestDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    /// ファイルを本文として読む（テストの検証用。読めなければ panic）。
    fn read_values(path: &Path) -> BTreeMap<String, SaveValue> {
        let text = fs::read_to_string(path).unwrap_or_else(|e| panic!("{} を読めない: {e}", path.display()));
        codec::decode(&text).unwrap_or_else(|e| panic!("{} が壊れている: {e}", path.display())).values
    }

    /// 1 キーだけのセーブを書いたストアを作る（世代を作る下ごしらえ）。
    fn store_with_generation(dir: &TestDir, generation: i64) -> SaveStore {
        let mut store = SaveStore::load(dir.primary());
        store.set("generation", SaveValue::Int(generation));
        store.request_save().expect("書き出しに失敗した");
        store
    }

    // ── キー・バリューの基本 ──────────────────────────────────

    /// 未設定キーの読み取りはすべて None（＝ C# 側が既定値を返す）。
    #[test]
    fn missing_key_returns_none() {
        let s = empty();
        assert_eq!(s.get_int("money"), None);
        assert_eq!(s.get_float("money"), None);
        assert_eq!(s.get_string("money"), None);
        assert!(!s.has("money"));
    }

    /// 各型の書き込み → 読み取り往復。
    #[test]
    fn set_get_roundtrip_each_type() {
        let mut s = empty();
        s.set("money", SaveValue::Int(1200));
        s.set("best", SaveValue::Float(41.5));
        s.set("name", SaveValue::Str("kani".into()));

        assert_eq!(s.get_int("money"), Some(1200));
        assert_eq!(s.get_float("best"), Some(41.5));
        assert_eq!(s.get_string("name"), Some("kani".to_string()));
        assert!(s.has("money") && s.has("best") && s.has("name"));
    }

    /// 整数 ⇄ 実数は相互変換する（実数→整数は切り捨て）。
    #[test]
    fn numeric_types_convert_both_ways() {
        let mut s = empty();
        s.set("a", SaveValue::Float(41.9));
        s.set("b", SaveValue::Int(7));
        assert_eq!(s.get_int("a"), Some(41)); // 切り捨て
        assert_eq!(s.get_float("b"), Some(7.0));
        // 負の実数も 0 方向へ切り捨てる
        s.set("c", SaveValue::Float(-2.7));
        assert_eq!(s.get_int("c"), Some(-2));
    }

    /// 文字列と数値は相互変換しない（既定値へ落ちる）。
    #[test]
    fn string_and_number_do_not_convert() {
        let mut s = empty();
        s.set("name", SaveValue::Str("123".into()));
        assert_eq!(s.get_int("name"), None);
        assert_eq!(s.get_float("name"), None);

        s.set("money", SaveValue::Int(100));
        assert_eq!(s.get_string("money"), None);
    }

    /// 同じキーへ別の型を書くと型ごと置き換わる。
    #[test]
    fn set_overwrites_type() {
        let mut s = empty();
        s.set("v", SaveValue::Int(1));
        s.set("v", SaveValue::Str("one".into()));
        assert_eq!(s.get_string("v"), Some("one".to_string()));
        assert_eq!(s.get_int("v"), None);
    }

    /// 空キーは無視する。
    #[test]
    fn empty_key_is_rejected() {
        let mut s = empty();
        s.set("", SaveValue::Int(1));
        assert!(s.is_empty());
        assert!(!s.is_dirty());
    }

    /// 削除の戻り値と dirty フラグ。
    #[test]
    fn delete_key_and_delete_all() {
        let mut s = empty();
        s.set("a", SaveValue::Int(1));
        s.set("b", SaveValue::Int(2));
        assert!(s.delete_key("a"));
        assert!(!s.delete_key("a")); // 2 回目は false
        assert_eq!(s.len(), 1);
        s.delete_all();
        assert!(s.is_empty());
    }

    /// 同値の再代入では dirty を立てない（無駄なフラッシュ防止）。
    #[test]
    fn setting_same_value_does_not_dirty() {
        let mut s = empty();
        s.set("a", SaveValue::Int(1));
        assert!(s.is_dirty());
        // dirty を落とした状態から同値を書いても立たない
        let mut s2 = SaveStore::new_empty(PathBuf::from("x/save.json"));
        s2.values.insert("a".into(), SaveValue::Int(1));
        s2.set("a", SaveValue::Int(1));
        assert!(!s2.is_dirty());
    }

    /// 存在しないファイルのロードは空ストア（エラーにしない・復旧ではない）。
    #[test]
    fn load_missing_file_is_empty() {
        let dir = TestDir::new("missing");
        let s = SaveStore::load(dir.primary());
        assert!(s.is_empty());
        assert!(!s.is_dirty());
        assert_eq!(s.load_source(), LoadSource::Fresh);
        assert_eq!(s.load_source().recovery_code(), recovery::RECOVERY_CODE_NONE);
    }

    // ── 壊れ方ごとの試験（W1-S。docs/app_platform_roadmap.md §2.7）──────────

    /// (a) 通常の書き出し → 読み戻し。フォルダが無くても作られ、一時ファイルは残らない。
    #[test]
    fn a_flush_then_load_roundtrip() {
        let dir = TestDir::new("a_roundtrip");
        let path = dir.0.join("nested").join("save.json");

        let mut s = SaveStore::load(path.clone());
        s.set("money", SaveValue::Int(999));
        s.set("name", SaveValue::Str("angler".into()));
        assert_eq!(s.request_save().unwrap(), FlushOutcome::Written);
        assert!(!s.is_dirty(), "書き出し後は dirty が下りる");
        assert!(path.exists(), "親ディレクトリごと作られる");
        assert!(!SaveFileSet::new(path.clone()).temp().exists(), "一時ファイルが残った");

        let loaded = SaveStore::load(path.clone());
        assert_eq!(loaded.get_int("money"), Some(999));
        assert_eq!(loaded.get_string("name"), Some("angler".to_string()));
        assert!(!loaded.is_dirty(), "ロード直後は dirty ではない");
        assert_eq!(loaded.load_source(), LoadSource::Primary);
        assert_eq!(loaded.load_source().recovery_code(), recovery::RECOVERY_CODE_NONE);

        // 自動保存は変更が無ければ書かない
        let mut again = SaveStore::load(path.clone());
        assert_eq!(again.request_auto_flush().unwrap(), FlushOutcome::Clean);
    }

    /// (b) 書き出しの途中で落ちた跡（半端な .tmp）が残っていても、前の save.json が読める。
    /// 完全な .tmp（sync の後・rename の前に落ちた跡）も読まない（書き出しが済んでいない世代）。
    #[test]
    fn b_leftover_temp_does_not_hide_previous_save() {
        let dir = TestDir::new("b_temp");
        store_with_generation(&dir, 1);
        let files = dir.files();

        // 半端な一時ファイル（閉じ括弧の前で切れた）
        fs::write(files.temp(), "{\n  \"generation\": 2").unwrap();
        let loaded = SaveStore::load(dir.primary());
        assert_eq!(loaded.get_int("generation"), Some(1));
        assert_eq!(loaded.load_source(), LoadSource::Primary);
        assert!(!loaded.is_dirty());

        // 完全な一時ファイルでも本体を優先する
        fs::write(files.temp(), "{\n  \"generation\": 3\n}").unwrap();
        let mut loaded = SaveStore::load(dir.primary());
        assert_eq!(loaded.get_int("generation"), Some(1));

        // 次の書き出しは残った一時ファイルを作り直して成功し、一時ファイルは残らない
        loaded.set("generation", SaveValue::Int(4));
        loaded.request_save().unwrap();
        assert!(!files.temp().exists());
        assert_eq!(read_values(files.primary()).get("generation"), Some(&SaveValue::Int(4)));
        assert_eq!(read_values(files.backup()).get("generation"), Some(&SaveValue::Int(1)));
    }

    /// (c) save.json が無く .bak がある（本体を .bak へ回した直後に落ちた跡）→ .bak から読み、Backup と報告する。
    /// 次の自動保存で本体を作り直し、.bak は残る。
    #[test]
    fn c_missing_primary_recovers_from_backup() {
        let dir = TestDir::new("c_backup");
        let files = dir.files();
        fs::write(files.backup(), "{\n  \"money\": 500\n}").unwrap();
        // 完全な一時ファイル（新しい世代）も残っている＝書き出しの 2 と 3 の間で落ちた形
        fs::write(files.temp(), "{\n  \"money\": 700\n}").unwrap();

        let mut s = SaveStore::load(dir.primary());
        assert_eq!(s.get_int("money"), Some(500));
        assert_eq!(s.load_source(), LoadSource::Backup);
        assert_eq!(s.load_source().recovery_code(), recovery::RECOVERY_CODE_BACKUP);
        assert!(s.is_dirty(), "復旧したら次の書き出しで本体を作り直す");

        assert_eq!(s.request_auto_flush().unwrap(), FlushOutcome::Written);
        assert_eq!(read_values(files.primary()).get("money"), Some(&SaveValue::Int(500)));
        assert_eq!(read_values(files.backup()).get("money"), Some(&SaveValue::Int(500)), ".bak が消えた");

        // 作り直した後の起動は普段どおり
        assert_eq!(SaveStore::load(dir.primary()).load_source(), LoadSource::Primary);
    }

    /// (d) save.json が壊れている（途中で切れた JSON）→ .bak から読み、壊れた本体は .corrupt-* へ退避される。
    /// 次の書き出しで .bak が壊れた本体に置き換わらない。
    #[test]
    fn d_truncated_primary_falls_back_to_backup_and_is_quarantined() {
        let dir = TestDir::new("d_corrupt");
        let files = dir.files();
        let truncated = "{\n  \"money\": 12";
        fs::write(files.primary(), truncated).unwrap();
        fs::write(files.backup(), "{\n  \"money\": 300\n}").unwrap();

        let mut s = SaveStore::load(dir.primary());
        assert_eq!(s.get_int("money"), Some(300));
        assert_eq!(s.load_source(), LoadSource::Backup);
        assert!(!files.primary().exists(), "壊れた本体が本体の場所に残った");
        let corrupt = files.existing_corrupt_files();
        assert_eq!(corrupt.len(), 1, "退避ファイルは 1 つ: {corrupt:?}");
        assert_eq!(fs::read_to_string(&corrupt[0]).unwrap(), truncated, "壊れた中身がそのまま残る");

        s.set("money", SaveValue::Int(310));
        s.request_save().unwrap();
        assert_eq!(read_values(files.primary()).get("money"), Some(&SaveValue::Int(310)));
        assert_eq!(read_values(files.backup()).get("money"), Some(&SaveValue::Int(300)), ".bak が壊れた本体で上書きされた");
    }

    /// (d') 退避ファイルは 1 つだけ残す（古いものは消え、新しいものが残る）。
    #[test]
    fn d_only_one_corrupt_file_is_kept() {
        let dir = TestDir::new("d_one_corrupt");
        let files = dir.files();
        let old = files.corrupt_path("20000101-000000");
        fs::write(&old, "old garbage").unwrap();
        fs::write(files.primary(), "new garbage").unwrap();

        let s = SaveStore::load(dir.primary());
        assert_eq!(s.load_source(), LoadSource::Unrecoverable);
        let corrupt = files.existing_corrupt_files();
        assert_eq!(corrupt.len(), 1, "{corrupt:?}");
        assert_ne!(corrupt[0], old, "古い退避ファイルが残った");
        assert_eq!(fs::read_to_string(&corrupt[0]).unwrap(), "new garbage");
    }

    /// (d'') 本体が壊れていて .bak も無い → 空で始め、Lost（番号 2）と報告する。次の書き出しで本体ができる。
    #[test]
    fn d_corrupt_primary_without_backup_is_unrecoverable() {
        let dir = TestDir::new("d_lost");
        let files = dir.files();
        fs::write(files.primary(), "").unwrap(); // 0 バイト（電源断で中身が届かなかった形）

        let mut s = SaveStore::load(dir.primary());
        assert!(s.is_empty());
        assert_eq!(s.load_source(), LoadSource::Unrecoverable);
        assert_eq!(s.load_source().recovery_code(), recovery::RECOVERY_CODE_LOST);
        assert_eq!(files.existing_corrupt_files().len(), 1);

        assert_eq!(s.request_auto_flush().unwrap(), FlushOutcome::Written);
        assert!(read_values(files.primary()).is_empty());
        assert_eq!(SaveStore::load(dir.primary()).load_source(), LoadSource::Primary);
    }

    /// (e) 書き出すたびに、直前の本体が .bak（1 世代前）になる。
    #[test]
    fn e_each_flush_rotates_previous_generation_into_backup() {
        let dir = TestDir::new("e_rotate");
        let files = dir.files();
        let mut s = store_with_generation(&dir, 1);
        assert!(!files.backup().exists(), "初回は 1 世代前が無い");

        s.set("generation", SaveValue::Int(2));
        s.request_save().unwrap();
        assert_eq!(read_values(files.primary()).get("generation"), Some(&SaveValue::Int(2)));
        assert_eq!(read_values(files.backup()).get("generation"), Some(&SaveValue::Int(1)));

        s.set("generation", SaveValue::Int(3));
        s.request_save().unwrap();
        assert_eq!(read_values(files.primary()).get("generation"), Some(&SaveValue::Int(3)));
        assert_eq!(read_values(files.backup()).get("generation"), Some(&SaveValue::Int(2)));
    }

    /// (f) Batch の途中の書き出しの要求（自動保存・明示の Save）は書かれず、最も外側の Batch の終わりに 1 回だけ書かれる。
    #[test]
    fn f_batch_defers_flush_until_outermost_end() {
        let dir = TestDir::new("f_batch");
        let files = dir.files();
        let mut s = store_with_generation(&dir, 1);

        s.begin_batch();
        s.set("money", SaveValue::Int(100));
        // UI スレッドの onDestroy に当たる自動保存: 書かれない
        assert_eq!(s.request_auto_flush().unwrap(), FlushOutcome::Deferred);
        assert_eq!(read_values(files.primary()).get("money"), None, "Batch の途中で書かれた");

        s.begin_batch(); // 入れ子
        s.set("history", SaveValue::Str("[\"paid 100\"]".into()));
        assert_eq!(s.request_save().unwrap(), FlushOutcome::Deferred);
        assert_eq!(s.end_batch().unwrap(), BatchEndOutcome::StillOpen);
        assert_eq!(read_values(files.primary()).get("money"), None, "内側の Batch の終わりで書かれた");

        assert_eq!(s.end_batch().unwrap(), BatchEndOutcome::ClosedAndWritten);
        let primary = read_values(files.primary());
        assert_eq!(primary.get("money"), Some(&SaveValue::Int(100)));
        assert_eq!(primary.get("history"), Some(&SaveValue::Str("[\"paid 100\"]".into())));
        // 1 回だけ書いた証拠: .bak が Batch より前の世代（2 回書いていれば .bak も新しい内容になる）
        let backup = read_values(files.backup());
        assert_eq!(backup.get("generation"), Some(&SaveValue::Int(1)));
        assert_eq!(backup.get("money"), None);
        assert!(!s.is_dirty());
    }

    /// (f') Batch の間に書き出しの要求が無ければ、終わりでも書かない（変更は未書き出しのまま残る）。
    #[test]
    fn f_batch_without_request_does_not_write() {
        let dir = TestDir::new("f_no_request");
        let files = dir.files();
        let mut s = store_with_generation(&dir, 1);

        s.begin_batch();
        s.set("generation", SaveValue::Int(2));
        assert_eq!(s.end_batch().unwrap(), BatchEndOutcome::ClosedWithoutWrite);
        assert_eq!(read_values(files.primary()).get("generation"), Some(&SaveValue::Int(1)));
        assert!(s.is_dirty(), "変更は次の書き出しで書かれる");
        // Batch の外に出たので自動保存は書く
        assert_eq!(s.request_auto_flush().unwrap(), FlushOutcome::Written);
        assert_eq!(read_values(files.primary()).get("generation"), Some(&SaveValue::Int(2)));
    }

    /// (f'') 開いていない Batch を終えても何も書かない（Begin と End の数の食い違い）。
    #[test]
    fn f_end_without_begin_is_ignored() {
        let dir = TestDir::new("f_unbalanced");
        let mut s = SaveStore::load(dir.primary());
        s.set("money", SaveValue::Int(1));
        assert_eq!(s.end_batch().unwrap(), BatchEndOutcome::NotOpen);
        assert!(!dir.primary().exists());
        assert!(s.is_dirty());
    }

    /// 後方互換: W1-S より前の形式の save.json（1 階層の JSON）をそのまま読み、書き戻しても同じ形になる。
    #[test]
    fn previous_format_save_is_read_and_rewritten_in_same_format() {
        let dir = TestDir::new("compat");
        // 旧実装（to_string_pretty・キーは辞書順）が書いた形そのもの
        let old = "{\n  \"best_size_bass\": 41.5,\n  \"money\": 1200,\n  \"player_name\": \"kani\",\n  \"rod_level\": 3\n}";
        fs::write(dir.primary(), old).unwrap();

        let mut s = SaveStore::load(dir.primary());
        assert_eq!(s.load_source(), LoadSource::Primary);
        assert_eq!(s.get_int("money"), Some(1200));
        assert_eq!(s.get_int("rod_level"), Some(3));
        assert_eq!(s.get_float("best_size_bass"), Some(41.5));
        assert_eq!(s.get_string("player_name"), Some("kani".to_string()));

        s.request_save().unwrap();
        assert_eq!(fs::read_to_string(dir.primary()).unwrap(), old, "書き戻した本文の形が変わった");
        assert_eq!(fs::read_to_string(dir.files().backup()).unwrap(), old);
    }

    /// 大きな文字列（2 MB の 1 文書）も往復する。
    #[test]
    fn large_string_roundtrips_through_file() {
        const LARGE_LEN: usize = 2 * 1024 * 1024;
        let dir = TestDir::new("large");
        let large: String = "記録".repeat(LARGE_LEN / "記録".len());
        let mut s = SaveStore::load(dir.primary());
        s.set("doc", SaveValue::Str(large.clone()));
        s.request_save().unwrap();
        assert_eq!(SaveStore::load(dir.primary()).get_string("doc"), Some(large));
    }
}
