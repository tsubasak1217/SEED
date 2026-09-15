// =============================================================================
// SEED 用 ファイル永続化ロックストア
// =============================================================================
// 目的:
//   素の loreserver の `[lock_store] mode = "local"` は
//   `lore_server::lock::store::LocalLockStore`（DashMap によるプロセス内メモリ）
//   にマップされるため、サーバを再起動するとロックがすべて消える。
//   SEED は「まずローカルディスクに永続化し、後から S3 / DynamoDB へ
//   設定だけで切り替えたい」ため、JSON ファイルへ書き出す LockStore を
//   自前で用意する。
//
// 設計:
//   - インメモリの権威データは `HashMap<SeedLockKey, LockData>`。
//     読み取り（query / check）はメモリだけで完結し、I/O は発生しない。
//   - 変更が起きたときだけ JSON ファイル全体を書き直す。
//     ロック数は数百〜数千のオーダーを想定しており、
//     全書き換えでも十分に安い（差分書き込みの複雑さを持ち込まない）。
//   - 書き込みは「一時ファイルへ書く → rename で置換」の 2 段階。
//     同一ボリューム上の rename は OS が原子的に扱うため、
//     書き込み中にプロセスが落ちても中途半端な JSON が残らない。
//
// 制限（既知・意図的）:
//   - ファイル I/O は同期 API（std::fs）で行い、async ランタイムを
//     ブロックする。ロック操作は低頻度かつファイルも小さいため許容している。
//     高頻度化したら spawn_blocking か非同期ライターへ移すこと。
//   - 1 プロセスからの単独利用のみを想定（複数 loreserver での共有は不可）。
//     マルチノード構成にするときは S3 / DynamoDB バックエンドへ差し替える。
// =============================================================================

use std::collections::HashMap;
use std::fs;
use std::io::Write;
use std::path::Path;
use std::path::PathBuf;
use std::sync::Arc;

use async_trait::async_trait;
use lore_base::error::InvalidArguments;
use lore_base::error::LockNotFound;
use lore_base::error::LockNotOwned;
use lore_base::error::PluginConfigError;
use lore_base::error::PluginInitError;
use lore_base::types::Hash;
use lore_base::types::LockData;
use lore_base::types::LockResource;
use lore_revision::lock::LockError;
use lore_revision::lock::LockQuery;
use lore_revision::lock::LockStore;
use lore_revision::lore::BranchId;
use lore_revision::lore::RepositoryId;
use lore_revision::util;
use lore_server::plugins::LockStorePluginFactory;
use lore_server::plugins::PluginError;
use lore_server::plugins::PluginRegistry;
use parking_lot::Mutex;
use serde::Deserialize;
use serde::Serialize;
use tracing::info;
use tracing::warn;

// -----------------------------------------------------------------------------
// 定数（マジックナンバー禁止のためすべて名前を付ける）
// -----------------------------------------------------------------------------

/// このプラグインの名前。`[lock_store] mode = "..."` と
/// `[plugins.<name>]` の両方で使われる識別子。
pub const PLUGIN_NAME: &str = "seed_file_lock_store";

/// `[plugins.seed_file_lock_store] path = "..."` を省略したときの既定ファイル名。
/// カレントディレクトリからの相対パスとして解決される。
const DEFAULT_LOCK_FILE_NAME: &str = "seed_locks.json";

/// 原子的置換に使う一時ファイルの拡張子。
/// 本体と同じディレクトリに作るので、rename が必ず同一ボリューム内で完結する。
const TEMP_FILE_SUFFIX: &str = ".tmp";

/// 永続化 JSON のスキーマバージョン。
/// 将来フォーマットを変えたときに、読み込み側で分岐できるようにしておく。
const LOCK_FILE_SCHEMA_VERSION: u32 = 1;

// -----------------------------------------------------------------------------
// 内部データ構造
// -----------------------------------------------------------------------------

/// インメモリ Map のキー。
///
/// upstream の参照実装（`lore-server/src/lock/store.rs` の `LockKey`）と
/// 同じ 3 つ組。`RepositoryId` / `BranchId` / `Hash` は lore-base 側で
/// `std::hash::Hash` を手書き実装しているのでキーに使える。
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
struct SeedLockKey {
    /// リポジトリ識別子（= `lore_base::types::Partition`）
    repository: RepositoryId,
    /// ブランチ識別子（= `lore_base::types::Context`）
    branch: BranchId,
    /// ロック対象リソースのハッシュ（ファイルパスのハッシュなど）
    hash: Hash,
}

impl SeedLockKey {
    /// リポジトリ ID と `LockResource` からキーを組み立てる。
    fn new(repository: RepositoryId, resource: &LockResource) -> Self {
        Self {
            repository,
            branch: resource.branch,
            hash: resource.hash,
        }
    }
}

/// JSON へ書き出す 1 レコード。
///
/// `LockResource` / `LockData` は lore-base 側で serde を導出していないため、
/// ここで serde 可能な写像を定義する。フィールド型の
/// `RepositoryId` / `BranchId` / `Hash` は `#[serde(transparent)]` +
/// hex 文字列シリアライズを持つので、そのまま埋め込める
/// （JSON では 32 文字 / 64 文字の 16 進文字列になる）。
#[derive(Clone, Debug, Deserialize, Serialize)]
struct PersistedLock {
    /// リポジトリ識別子
    repository: RepositoryId,
    /// ブランチ識別子
    branch: BranchId,
    /// リソースのハッシュ
    hash: Hash,
    /// 人間可読なリソース説明（通常はファイルパス）
    description: String,
    /// ロック保持者の識別子（通常はユーザーの e-mail）
    owner: String,
    /// ロック取得時刻（UNIX epoch ミリ秒）
    locked_at: u64,
}

/// 永続化ファイル全体の形。
#[derive(Debug, Deserialize, Serialize)]
struct LockFileDocument {
    /// スキーマバージョン（将来のマイグレーション用）
    version: u32,
    /// ロックの一覧
    locks: Vec<PersistedLock>,
}

// -----------------------------------------------------------------------------
// LockStore 実装本体
// -----------------------------------------------------------------------------

/// JSON ファイルへ永続化する `LockStore` 実装。
///
/// 内部状態は `Mutex<HashMap<..>>` 1 つだけ。
/// すべての変更系メソッドは `mutate_and_persist()` を通り、
/// 「Mutex を取る → Map を書き換える → ファイルへ書く → Mutex を放す」を
/// 1 区間で行う（詳細はそのメソッドのコメント）。
/// Mutex を await をまたいで保持しないので、async 文脈でも安全。
pub struct SeedFileLockStore {
    /// 永続化先の JSON ファイルパス
    path: PathBuf,
    /// ロックのインメモリ表現（権威データ）
    locks: Mutex<HashMap<SeedLockKey, LockData>>,
}

impl SeedFileLockStore {
    /// 指定パスからロックを読み込んでストアを作る。
    ///
    /// ファイルが存在しない場合は空のストアとして開始する（初回起動）。
    /// 親ディレクトリが無ければ作成する。
    ///
    /// # Errors
    /// ファイルが存在するのに読めない／JSON として壊れている場合、
    /// および親ディレクトリを作れない場合にエラーを返す。
    /// 「壊れていたら黙って空にする」ことはしない。
    /// ロック情報を無言で失うのは、起動失敗よりずっと危険なため。
    pub fn open(path: impl Into<PathBuf>) -> Result<Self, String> {
        let path = path.into();

        // 親ディレクトリを先に用意する。
        // ここで作っておかないと、初回の保存時に rename が失敗する。
        if let Some(parent) = path.parent()
            && !parent.as_os_str().is_empty()
        {
            fs::create_dir_all(parent)
                .map_err(|e| format!("ロックファイルの親ディレクトリを作成できません {parent:?}: {e}"))?;
        }

        let locks = Self::load_from_disk(&path)?;
        info!(
            path = %path.display(),
            lock_count = locks.len(),
            "SEED ファイルロックストアを初期化しました"
        );

        Ok(Self {
            path,
            locks: Mutex::new(locks),
        })
    }

    /// ディスク上の JSON を読み込んで Map へ展開する。
    /// ファイルが無いときは空の Map を返す。
    fn load_from_disk(path: &Path) -> Result<HashMap<SeedLockKey, LockData>, String> {
        let raw = match fs::read_to_string(path) {
            Ok(raw) => raw,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                // 初回起動。空のストアから始める。
                return Ok(HashMap::new());
            }
            Err(e) => {
                return Err(format!("ロックファイルを読めません {path:?}: {e}"));
            }
        };

        // 空ファイル（前回の書き込みが極端に早く落ちた等）は空ストア扱いにする。
        if raw.trim().is_empty() {
            warn!(path = %path.display(), "ロックファイルが空でした。ロック 0 件として起動します");
            return Ok(HashMap::new());
        }

        let document: LockFileDocument = serde_json::from_str(&raw)
            .map_err(|e| format!("ロックファイルの JSON を解釈できません {path:?}: {e}"))?;

        if document.version != LOCK_FILE_SCHEMA_VERSION {
            return Err(format!(
                "ロックファイルのスキーマバージョンが未対応です {path:?}: \
                 file={} expected={LOCK_FILE_SCHEMA_VERSION}",
                document.version
            ));
        }

        let mut locks = HashMap::with_capacity(document.locks.len());
        for record in document.locks {
            let resource = LockResource {
                branch: record.branch,
                hash: record.hash,
                description: record.description,
            };
            let key = SeedLockKey {
                repository: record.repository,
                branch: record.branch,
                hash: record.hash,
            };
            let data = LockData {
                resource,
                owner: record.owner,
                locked_at: record.locked_at,
            };
            locks.insert(key, data);
        }

        Ok(locks)
    }

    /// Map のスナップショットを JSON ファイルへ原子的に書き出す。
    ///
    /// 手順:
    ///   1. `<path>.tmp` へ全内容を書く
    ///   2. `sync_all()` で OS のバッファをディスクへ流す
    ///   3. `<path>` へ rename して置換する
    ///
    /// # Errors
    /// 書き込み・rename のいずれかに失敗したらエラーを返す。
    /// 呼び出し側はこれを `LockError::internal` へ変換して、
    /// クライアントにロック操作の失敗として伝える。
    fn persist(&self, snapshot: Vec<PersistedLock>) -> Result<(), String> {
        let document = LockFileDocument {
            version: LOCK_FILE_SCHEMA_VERSION,
            locks: snapshot,
        };
        let encoded = serde_json::to_string_pretty(&document)
            .map_err(|e| format!("ロックファイルの JSON 生成に失敗しました: {e}"))?;

        // 一時ファイル名は「本体パス + .tmp」。同じディレクトリなので
        // rename が同一ボリューム内で完結し、原子的置換が保証される。
        let mut temp_path = self.path.clone().into_os_string();
        temp_path.push(TEMP_FILE_SUFFIX);
        let temp_path = PathBuf::from(temp_path);

        {
            let mut file = fs::File::create(&temp_path)
                .map_err(|e| format!("一時ロックファイルを作成できません {temp_path:?}: {e}"))?;
            file.write_all(encoded.as_bytes())
                .map_err(|e| format!("一時ロックファイルへ書き込めません {temp_path:?}: {e}"))?;
            file.sync_all()
                .map_err(|e| format!("一時ロックファイルを同期できません {temp_path:?}: {e}"))?;
        }

        // Windows の std::fs::rename は「既存ファイルがあっても置換する」
        // MoveFileEx(MOVEFILE_REPLACE_EXISTING) 相当なので、事前削除は不要。
        fs::rename(&temp_path, &self.path).map_err(|e| {
            format!(
                "ロックファイルを置換できません {temp_path:?} -> {:?}: {e}",
                self.path
            )
        })?;

        Ok(())
    }

    /// 「メモリを書き換える → ファイルへ書く」を 1 つの Mutex 区間で行う。
    ///
    /// なぜ Mutex を握ったままファイルへ書くのか:
    ///   Mutex を先に放してから書くと、2 つの操作が並行したときに
    ///   「後から Mutex を取った側が先に書き、古いスナップショットが
    ///   あとから上書きする」順序が起こりうる。メモリ上は正しいのに
    ///   ファイルだけロックが 1 件消えた状態になり、次の再起動で失われる。
    ///   このモジュールの存在理由そのものを壊すので、書き込みまで含めて
    ///   直列化する。ファイルは小さく、ロック操作は低頻度なので
    ///   スループット上の問題にはならない。
    ///   （`persist` は `self.locks` を触らないので再入デッドロックは起きない）
    ///
    /// 永続化に失敗したらメモリ側も変更前へ巻き戻す。
    /// 「クライアントにはロック失敗を返したのにサーバのメモリ上では
    /// ロックされている」という食い違いを残さないため。
    fn mutate_and_persist<T>(
        &self,
        mutate: impl FnOnce(&mut HashMap<SeedLockKey, LockData>) -> Result<T, LockError>,
    ) -> Result<T, LockError> {
        let mut locks = self.locks.lock();

        // 巻き戻し用の控え。ロック件数は高々数千なので複製コストは無視できる。
        let backup = locks.clone();

        let result = match mutate(&mut locks) {
            Ok(value) => value,
            Err(e) => {
                *locks = backup;
                return Err(e);
            }
        };

        let snapshot = Self::snapshot(&locks);
        if let Err(e) = self.persist(snapshot) {
            *locks = backup;
            warn!(error = %e, "ロックの永続化に失敗したためメモリ上の変更を巻き戻しました");
            return Err(LockError::internal("failed to persist locks"));
        }

        Ok(result)
    }

    /// Map から永続化用のスナップショットを作る。
    /// 呼び出し側が Mutex を保持している前提。
    fn snapshot(locks: &HashMap<SeedLockKey, LockData>) -> Vec<PersistedLock> {
        locks
            .iter()
            .map(|(key, data)| PersistedLock {
                repository: key.repository,
                branch: key.branch,
                hash: key.hash,
                description: data.resource.description.clone(),
                owner: data.owner.clone(),
                locked_at: data.locked_at,
            })
            .collect()
    }

    /// 現在保持しているロック数。テストと診断用。
    ///
    /// バイナリ crate なので `pub` でも外部からは呼ばれず、
    /// 通常ビルドでは dead_code 警告になる。テスト側では使っているので
    /// 消さずに許可する（将来 MCP ツール等から状態を覗くときにも使う）。
    #[allow(dead_code)]
    pub fn lock_count(&self) -> usize {
        self.locks.lock().len()
    }
}

#[async_trait]
impl LockStore for SeedFileLockStore {
    /// 要求されたリソースすべてにロックを取る。
    /// 1 つでも他人に取られていたら、全体を失敗させる（all-or-nothing）。
    ///
    /// upstream の `LocalLockStore` は失敗時に部分ロックを
    /// `unlock_resources` で巻き戻しているが、本実装は
    /// 「Mutex 内で全件を検査 → 全件通ったときだけ挿入」とすることで
    /// そもそも部分適用が起きない形にしている。
    async fn lock_resources(
        &self,
        owner_id: &str,
        repository: RepositoryId,
        resources: &[LockResource],
    ) -> Result<Vec<LockData>, LockError> {
        let timestamp = util::time::timestamp();

        // `mutate_and_persist` は同期クロージャなので await をまたがない。
        self.mutate_and_persist(|locks| {
            // --- 第 1 段階: 競合検査 ---------------------------------------
            // 他人が持っているロックが 1 件でもあれば、この時点で失敗させる。
            // 自分が既に持っているものは「再取得」として通す（冪等）。
            for resource in resources {
                let key = SeedLockKey::new(repository, resource);
                if let Some(existing) = locks.get(&key)
                    && existing.owner != owner_id
                {
                    return Err(LockError::internal("resource already locked"));
                }
            }

            // --- 第 2 段階: 反映 -------------------------------------------
            let mut acquired = Vec::with_capacity(resources.len());
            for resource in resources {
                let key = SeedLockKey::new(repository, resource);
                let data = LockData {
                    resource: resource.clone(),
                    owner: owner_id.to_string(),
                    locked_at: timestamp,
                };
                // 既存が自分のものなら locked_at を更新せず維持する
                // （ロック取得時刻は最初に取った時点を意味するため）。
                let stored = locks.entry(key).or_insert(data);
                acquired.push(stored.clone());
            }

            Ok(acquired)
        })
    }

    /// ロックを検索する。
    /// upstream の `LocalLockStore` が対応している 6 種類の
    /// `LockQuery` に加え、`Owner` 単独クエリにも対応する
    /// （メモリ上の全走査なのでコストは変わらない）。
    async fn query_locks(&self, query: LockQuery) -> Result<Vec<LockData>, LockError> {
        let locks = self.locks.lock();
        let mut found = Vec::new();

        match query {
            LockQuery::Repository(repository) => {
                for (key, data) in locks.iter() {
                    if key.repository == repository {
                        found.push(data.clone());
                    }
                }
            }
            LockQuery::RepositoryBranch(repository, branch) => {
                for (key, data) in locks.iter() {
                    if key.repository == repository && key.branch == branch {
                        found.push(data.clone());
                    }
                }
            }
            LockQuery::RepositoryBranchDescription(repository, branch, description) => {
                for (key, data) in locks.iter() {
                    if key.repository == repository
                        && key.branch == branch
                        && data.resource.description == description
                    {
                        found.push(data.clone());
                    }
                }
            }
            LockQuery::Owner(owner) => {
                for data in locks.values() {
                    if data.owner == owner {
                        found.push(data.clone());
                    }
                }
            }
            LockQuery::OwnerRepository(owner, repository) => {
                for (key, data) in locks.iter() {
                    if key.repository == repository && data.owner == owner {
                        found.push(data.clone());
                    }
                }
            }
            LockQuery::OwnerRepositoryBranch(owner, repository, branch) => {
                for (key, data) in locks.iter() {
                    if key.repository == repository && key.branch == branch && data.owner == owner {
                        found.push(data.clone());
                    }
                }
            }
            LockQuery::HashRepositoryBranch(hash, repository, branch) => {
                let key = SeedLockKey {
                    repository,
                    branch,
                    hash,
                };
                if let Some(data) = locks.get(&key) {
                    found.push(data.clone());
                }
            }
            // Hash 単独 / HashRepository は「どのブランチか」が決まらず
            // 全走査でも意味が曖昧になるため、upstream 参照実装と同じく非対応。
            LockQuery::Hash(_) | LockQuery::HashRepository(_, _) => {
                return Err(InvalidArguments {
                    reason: "unsupported lock query".into(),
                }
                .into());
            }
        }

        Ok(found)
    }

    /// 指定リソース群のうち、実際にロックされているものを返す。
    /// ロックされていないリソースは結果に含まれない（エラーにはしない）。
    async fn check_locks_status(
        &self,
        repository: RepositoryId,
        resources: &[LockResource],
    ) -> Result<Vec<LockData>, LockError> {
        let locks = self.locks.lock();
        let mut locked = Vec::new();

        for resource in resources {
            let key = SeedLockKey::new(repository, resource);
            if let Some(data) = locks.get(&key) {
                locked.push(data.clone());
            }
        }

        Ok(locked)
    }

    /// ロックを解放する。
    /// 1 つでも解放できないものがあれば全体を失敗させる（all-or-nothing）。
    ///
    /// `validate_user = true` のときは所有者一致を要求する。
    /// 管理者による強制解除では `false` が渡ってくる。
    async fn unlock_resources(
        &self,
        owner_id: &str,
        validate_user: bool,
        repository: RepositoryId,
        resources: &[LockResource],
    ) -> Result<Vec<LockResource>, LockError> {
        self.mutate_and_persist(|locks| {
            // --- 第 1 段階: 検査 -------------------------------------------
            // 途中で失敗して「一部だけ解放された」状態を作らないよう、
            // 先に全件チェックしてから削除する。
            for resource in resources {
                let key = SeedLockKey::new(repository, resource);
                match locks.get(&key) {
                    None => return Err(LockNotFound.into()),
                    Some(existing) => {
                        if validate_user && existing.owner != owner_id {
                            return Err(LockNotOwned.into());
                        }
                    }
                }
            }

            // --- 第 2 段階: 削除 -------------------------------------------
            for resource in resources {
                let key = SeedLockKey::new(repository, resource);
                locks.remove(&key);
            }

            Ok(resources.to_vec())
        })
    }
}

// -----------------------------------------------------------------------------
// プラグインファクトリ
// -----------------------------------------------------------------------------

/// `[plugins.seed_file_lock_store]` セクションの設定。
#[derive(Debug, Deserialize)]
struct SeedFileLockStoreConfig {
    /// ロックを保存する JSON ファイルのパス。
    /// 省略時は `DEFAULT_LOCK_FILE_NAME`（カレント相対）。
    #[serde(default = "default_lock_file_path")]
    path: String,
}

/// `path` 未指定時の既定値。
fn default_lock_file_path() -> String {
    DEFAULT_LOCK_FILE_NAME.to_string()
}

/// `SeedFileLockStore` を生成するプラグインファクトリ。
///
/// `lore_server::plugins::PluginRegistry` に登録すると、
/// 設定 `[lock_store] mode = "seed_file_lock_store"` で選べるようになる。
pub struct SeedFileLockStorePluginFactory;

impl SeedFileLockStorePluginFactory {
    /// TOML 設定を構造体へ落とす共通処理。
    /// `validate_config` と `create` の両方から使う。
    fn parse_config(config: &toml::Value) -> Result<SeedFileLockStoreConfig, PluginError> {
        let parsed: SeedFileLockStoreConfig = config.clone().try_into().map_err(|e| {
            PluginError::from(PluginConfigError {
                plugin_name: PLUGIN_NAME.to_string(),
                message: format!("設定を解釈できません: {e}"),
            })
        })?;
        Ok(parsed)
    }
}

impl LockStorePluginFactory for SeedFileLockStorePluginFactory {
    /// 設定だけを検証する（ファイルは開かない）。
    fn validate_config(&self, config: &toml::Value) -> Result<(), PluginError> {
        Self::parse_config(config)?;
        Ok(())
    }

    /// 設定からロックストア本体を作る。
    /// この時点で既存のロックファイルを読み込むので、
    /// 壊れたファイルはサーバ起動失敗として表面化する。
    fn create(&self, config: &toml::Value) -> Result<Arc<dyn LockStore>, PluginError> {
        let parsed = Self::parse_config(config)?;
        let store = SeedFileLockStore::open(&parsed.path).map_err(|e| {
            PluginError::from(PluginInitError {
                plugin_name: PLUGIN_NAME.to_string(),
                message: e,
            })
        })?;
        Ok(Arc::new(store))
    }

    fn name(&self) -> &'static str {
        PLUGIN_NAME
    }
}

/// このモジュールが提供するプラグインをレジストリへ登録する。
///
/// upstream の `lore-server/src/plugins/<name>.rs` が持つ `register()` と
/// 同じ役割だが、こちらは自前バイナリの `main()` から明示的に呼ぶ。
pub fn register(registry: &mut PluginRegistry) {
    registry.register_lock_store_plugin(Box::new(SeedFileLockStorePluginFactory));
}

// =============================================================================
// 単体テスト
// =============================================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の `LockResource` を作る。
    /// `Hash` / `BranchId` は 16 進文字列から serde 経由で作る
    /// （lore-base は生バイトからのコンストラクタを公開していないため）。
    fn make_resource(hash_hex: &str, description: &str) -> LockResource {
        LockResource {
            branch: serde_json::from_str::<BranchId>(&format!("\"{}\"", "1".repeat(32))).unwrap(),
            hash: serde_json::from_str::<Hash>(&format!("\"{hash_hex}\"")).unwrap(),
            description: description.to_string(),
        }
    }

    /// テスト用のリポジトリ ID。
    fn test_repository() -> RepositoryId {
        serde_json::from_str::<RepositoryId>(&format!("\"{}\"", "a".repeat(32))).unwrap()
    }

    /// 64 文字の 16 進ハッシュ文字列を作る。
    fn hash_hex(seed: char) -> String {
        std::iter::repeat_n(seed, 64).collect()
    }

    /// ロックを取ったあと、別インスタンスで開き直しても残っていること。
    /// これがこのモジュールの存在理由そのもの（再起動でロックが消えない）。
    #[tokio::test]
    async fn locks_persist_and_reload() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("locks.json");
        let repository = test_repository();
        let resource = make_resource(&hash_hex('b'), "assets/mainGame/MainGame.scene");

        // --- 1 回目のプロセス相当 ---
        {
            let store = SeedFileLockStore::open(&path).unwrap();
            let acquired = store
                .lock_resources("alice@example.com", repository, &[resource.clone()])
                .await
                .unwrap();
            assert_eq!(acquired.len(), 1);
            assert_eq!(acquired[0].owner, "alice@example.com");
        }

        // ファイルが実際にできていること
        assert!(path.exists(), "ロックファイルが作られていない");

        // --- 2 回目のプロセス相当（サーバ再起動） ---
        let reopened = SeedFileLockStore::open(&path).unwrap();
        assert_eq!(reopened.lock_count(), 1);

        let found = reopened
            .query_locks(LockQuery::Repository(repository))
            .await
            .unwrap();
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].owner, "alice@example.com");
        assert_eq!(
            found[0].resource.description,
            "assets/mainGame/MainGame.scene"
        );
    }

    /// 他人がロック中のリソースは取得できず、かつ
    /// 同じ要求に含まれた他のリソースも巻き込んでロックされないこと
    /// （all-or-nothing。部分適用が残ると後始末が地獄になる）。
    #[tokio::test]
    async fn cannot_steal_others_lock_and_no_partial_apply() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("locks.json");
        let repository = test_repository();
        let taken = make_resource(&hash_hex('c'), "taken.txt");
        let free = make_resource(&hash_hex('d'), "free.txt");

        let store = SeedFileLockStore::open(&path).unwrap();
        store
            .lock_resources("alice@example.com", repository, &[taken.clone()])
            .await
            .unwrap();

        // bob が「取られている 1 件 + 空いている 1 件」をまとめて要求する
        let result = store
            .lock_resources(
                "bob@example.com",
                repository,
                &[free.clone(), taken.clone()],
            )
            .await;
        assert!(result.is_err(), "他人のロックを奪えてしまっている");

        // free.txt は bob のものになっていないこと
        let all = store
            .query_locks(LockQuery::Repository(repository))
            .await
            .unwrap();
        assert_eq!(all.len(), 1, "部分適用が起きている: {all:?}");
        assert_eq!(all[0].owner, "alice@example.com");
    }

    /// 自分自身のロックの取り直しは成功し、重複登録されないこと。
    #[tokio::test]
    async fn reacquiring_own_lock_is_idempotent() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("locks.json");
        let repository = test_repository();
        let resource = make_resource(&hash_hex('e'), "same.txt");

        let store = SeedFileLockStore::open(&path).unwrap();
        store
            .lock_resources("alice@example.com", repository, &[resource.clone()])
            .await
            .unwrap();
        store
            .lock_resources("alice@example.com", repository, &[resource.clone()])
            .await
            .unwrap();

        assert_eq!(store.lock_count(), 1);
    }

    /// 解放すると永続化ファイルからも消えること。
    /// 所有者が違う場合は解放できないこと。
    #[tokio::test]
    async fn unlock_validates_owner_and_persists() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("locks.json");
        let repository = test_repository();
        let resource = make_resource(&hash_hex('f'), "unlock.txt");

        let store = SeedFileLockStore::open(&path).unwrap();
        store
            .lock_resources("alice@example.com", repository, &[resource.clone()])
            .await
            .unwrap();

        // 別人は解放できない
        let denied = store
            .unlock_resources("bob@example.com", true, repository, &[resource.clone()])
            .await;
        assert!(denied.is_err(), "他人のロックを解放できてしまっている");
        assert_eq!(store.lock_count(), 1);

        // 本人は解放できる
        store
            .unlock_resources("alice@example.com", true, repository, &[resource.clone()])
            .await
            .unwrap();
        assert_eq!(store.lock_count(), 0);

        // 再読み込みしても 0 件
        let reopened = SeedFileLockStore::open(&path).unwrap();
        assert_eq!(reopened.lock_count(), 0);
    }

    /// 存在しないロックの解放は `LockNotFound` になること。
    #[tokio::test]
    async fn unlocking_missing_lock_fails() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("locks.json");
        let repository = test_repository();
        let resource = make_resource(&hash_hex('0'), "missing.txt");

        let store = SeedFileLockStore::open(&path).unwrap();
        let result = store
            .unlock_resources("alice@example.com", true, repository, &[resource])
            .await;
        assert!(result.is_err());
    }

    /// `check_locks_status` はロック済みのものだけを返すこと。
    #[tokio::test]
    async fn check_status_returns_only_locked() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("locks.json");
        let repository = test_repository();
        let locked = make_resource(&hash_hex('1'), "locked.txt");
        let unlocked = make_resource(&hash_hex('2'), "unlocked.txt");

        let store = SeedFileLockStore::open(&path).unwrap();
        store
            .lock_resources("alice@example.com", repository, &[locked.clone()])
            .await
            .unwrap();

        let status = store
            .check_locks_status(repository, &[locked.clone(), unlocked.clone()])
            .await
            .unwrap();
        assert_eq!(status.len(), 1);
        assert_eq!(status[0].resource.description, "locked.txt");
    }

    /// 壊れた JSON は黙って無視せず、エラーとして表面化すること。
    /// （ロックを無言で失うほうが危険なので、起動を止める方を選ぶ）
    #[test]
    fn broken_lock_file_is_an_error() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("broken.json");
        fs::write(&path, "{ this is not json").unwrap();

        let result = SeedFileLockStore::open(&path);
        assert!(result.is_err(), "壊れたファイルが素通りしている");
    }

    /// 親ディレクトリが存在しなくても、open 時に作られること。
    #[tokio::test]
    async fn parent_directory_is_created() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("nested").join("deeper").join("locks.json");
        let repository = test_repository();
        let resource = make_resource(&hash_hex('3'), "nested.txt");

        let store = SeedFileLockStore::open(&path).unwrap();
        store
            .lock_resources("alice@example.com", repository, &[resource])
            .await
            .unwrap();
        assert!(path.exists());
    }

    /// 永続化に失敗したとき、メモリ上の変更が巻き戻ること。
    ///
    /// 一時ファイル `<path>.tmp` と同名のディレクトリを先に作っておくと
    /// `File::create` が必ず失敗するので、書き込み失敗を再現できる。
    /// ここで巻き戻さないと「クライアントには失敗を返したのに
    /// サーバ内ではロック済み」という誰も解除できない状態が残る。
    #[tokio::test]
    async fn persist_failure_rolls_back_memory() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("locks.json");
        let repository = test_repository();
        let resource = make_resource(&hash_hex('4'), "rollback.txt");

        let store = SeedFileLockStore::open(&path).unwrap();

        // 一時ファイルの置き場をディレクトリで塞ぐ
        let mut blocked = path.clone().into_os_string();
        blocked.push(TEMP_FILE_SUFFIX);
        fs::create_dir(PathBuf::from(blocked)).unwrap();

        let result = store
            .lock_resources("alice@example.com", repository, &[resource])
            .await;
        assert!(result.is_err(), "書き込みが失敗したのに成功扱いになっている");
        assert_eq!(
            store.lock_count(),
            0,
            "永続化に失敗したのにメモリ上へロックが残っている"
        );
    }

    /// プラグインファクトリが設定を読めること。
    #[test]
    fn plugin_config_parses() {
        let config: toml::Value = toml::from_str(r#"path = "C:/tmp/seed_locks.json""#).unwrap();
        let factory = SeedFileLockStorePluginFactory;
        assert!(factory.validate_config(&config).is_ok());
        assert_eq!(factory.name(), PLUGIN_NAME);

        // path 省略時も既定値で通ること
        let empty: toml::Value = toml::Value::Table(toml::map::Map::new());
        assert!(factory.validate_config(&empty).is_ok());
    }
}
