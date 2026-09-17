// =============================================================================
// SEED アカウント発行窓口 : accounts.json の読み書きと参加者の操作
// =============================================================================
// 参加者・権限・招待コードの唯一の置き場。HTTP も JWT も知らない。
// ここに閉じ込めることで、「誰が何をできるか」の規則を 1 ファイルで読める。
//
// 永続化の流儀は `src/lock_store_file.rs` と同じ:
//   インメモリが権威データ → 変更のたびに JSON 全体を tmp → rename で置換 →
//   書き込みに失敗したらメモリ側も巻き戻す。
//   （「利用者には失敗を返したのにサーバ内では成功している」を作らない）
//
// 秘密の扱い:
//   - 招待コードの平文はここに残さない。保存するのは SHA-256 のハッシュだけ。
//   - 平文を返すのは発行時の 1 回だけ（呼び出し側が応答へ載せる）。
//   - ログには絶対に出さない。
// =============================================================================

use std::path::Path;
use std::path::PathBuf;

use parking_lot::Mutex;

use super::atomic_file::read_if_exists;
use super::atomic_file::write_atomic;
use super::crypto::constant_time_eq;
use super::crypto::random_token;
use super::crypto::sha256_base64url;
use super::model::ACCOUNTS_SCHEMA_VERSION;
use super::model::Account;
use super::model::AccountsDocument;
use super::model::Grant;
use super::model::GrantStatus;
use super::model::Invite;
use super::model::NameError;
use super::model::PROJECT_NAME_MAX_CHARS;
use super::model::Role;
use super::model::normalize_name_for_uniqueness;
use super::model::validate_name;
use super::model::validate_repository_id;
use super::user_key::PublicKeyError;
use super::user_key::parse_public_key;

// -----------------------------------------------------------------------------
// 定数
// -----------------------------------------------------------------------------

/// 参加者データを保存するファイル名（`data_dir` 直下）。
pub const ACCOUNTS_FILE_NAME: &str = "accounts.json";

/// 招待コードの乱数バイト数。base64url で 32 文字になる。
/// 総当たりを現実的でなくするのに十分な長さ（192 ビット）。
const INVITE_CODE_BYTES: usize = 24;

/// 招待コードの有効期間として受け付ける最小値（時間）。
const INVITE_MIN_HOURS: u64 = 1;

/// 招待コードの有効期間として受け付ける最大値（時間）。30 日。
/// これ以上長い招待は「実質無期限」になり、失効の意味が薄れる。
const INVITE_MAX_HOURS: u64 = 24 * 30;

/// 受け付ける招待コードの最大文字数（入力長の上限）。
pub const INVITE_CODE_MAX_CHARS: usize = 128;

/// 1 時間あたりのミリ秒数。
const MILLIS_PER_HOUR: u64 = 60 * 60 * 1_000;

// -----------------------------------------------------------------------------
// エラー
// -----------------------------------------------------------------------------

/// 参加者操作の失敗理由。
///
/// HTTP の応答コードとエラーコード文字列は `http.rs` で決める。
/// ここでは「何が起きたか」だけを表す。
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum StoreError {
    /// 名前の規則に反する
    InvalidName(NameError),
    /// 公開鍵の形式が不正
    InvalidPublicKey(PublicKeyError),
    /// リポジトリ ID の形式が不正（32 桁の 16 進数でない）
    InvalidRepositoryId,
    /// プロジェクト名が長すぎる
    InvalidProjectName,
    /// 招待の有効期間が範囲外
    InvalidExpiry,
    /// その名前は既に別の人が使っている
    NameTaken,
    /// そのリポジトリには既にオーナーが居る
    OwnerExists,
    /// 招待コードが不正・期限切れ・使用済み
    InviteInvalid,
    /// 操作する権限が無い（owner でない）
    Forbidden,
    /// 対象の参加者が見つからない
    MemberNotFound,
    /// オーナーの権限は失効できない
    CannotRevokeOwner,
    /// 乱数の生成に失敗した
    RandomFailed,
    /// ファイルへの保存に失敗した
    PersistFailed(String),
}

// -----------------------------------------------------------------------------
// 操作の結果
// -----------------------------------------------------------------------------

/// アカウント登録（bootstrap / join）の結果。
#[derive(Clone, Debug)]
pub struct JoinResult {
    /// 実際に登録された名前（既存アカウントに権限を足した場合は既存の名前）
    pub name: String,
    /// 参加したリポジトリ
    pub repository_id: String,
    /// 表示用のプロジェクト名
    pub project_name: String,
    /// 付与された役割
    pub role: Role,
}

/// 招待コードの発行結果。
#[derive(Clone, Debug)]
pub struct CreatedInvite {
    /// 招待コードの平文。**この 1 回しか手に入らない。**
    pub invite_code: String,
    /// 失効時刻（UNIX epoch ミリ秒）
    pub expires_at_ms: u64,
}

/// `GET /v1/members` が返す 1 件分。
#[derive(Clone, Debug)]
pub struct MemberView {
    /// 参加者名
    pub name: String,
    /// 役割
    pub role: Role,
    /// 有効／失効
    pub status: GrantStatus,
    /// 参加した時刻（UNIX epoch ミリ秒）
    pub added_at: u64,
}

// -----------------------------------------------------------------------------
// ストア本体
// -----------------------------------------------------------------------------

/// 参加者データのストア。
///
/// 内部状態は `Mutex<AccountsDocument>` 1 つ。
/// 変更系はすべて `mutate_and_persist()` を通り、
/// 「Mutex を取る → 書き換える → ファイルへ書く → Mutex を放す」を 1 区間で行う。
/// await をまたいで Mutex を保持しないので async 文脈でも安全。
pub struct AccountStore {
    /// 保存先ファイル
    path: PathBuf,
    /// インメモリの権威データ
    document: Mutex<AccountsDocument>,
}

impl AccountStore {
    /// データフォルダからアカウントを読み込む。無ければ空で始める。
    ///
    /// # Errors
    /// ファイルが存在するのに読めない／JSON が壊れている場合。
    /// **壊れていても黙って空から始めない。** 参加者を無言で失うほうが危険。
    pub fn open(data_dir: &Path) -> Result<Self, String> {
        let path = data_dir.join(ACCOUNTS_FILE_NAME);

        let document = match read_if_exists(&path)? {
            None => AccountsDocument::default(),
            Some(raw) if raw.trim().is_empty() => AccountsDocument::default(),
            Some(raw) => {
                let parsed: AccountsDocument = serde_json::from_str(&raw)
                    .map_err(|e| format!("参加者ファイルを解釈できません {path:?}: {e}"))?;
                if parsed.version != ACCOUNTS_SCHEMA_VERSION {
                    return Err(format!(
                        "参加者ファイルのスキーマバージョンが未対応です {path:?}: \
                         file={} expected={ACCOUNTS_SCHEMA_VERSION}",
                        parsed.version
                    ));
                }
                parsed
            }
        };

        Ok(Self {
            path,
            document: Mutex::new(document),
        })
    }

    /// 保存先ファイルのパス。診断用。
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// 現在のアカウント数。診断とテスト用。
    ///
    /// バイナリ crate なので通常ビルドでは呼ばれず dead_code 警告になるが、
    /// テストでは使っているので消さずに許可する。
    #[allow(dead_code)]
    pub fn account_count(&self) -> usize {
        self.document.lock().accounts.len()
    }

    /// 「メモリを書き換える → ファイルへ書く」を 1 つの Mutex 区間で行う。
    /// 保存に失敗したらメモリ側も巻き戻す。
    fn mutate_and_persist<T>(
        &self,
        mutate: impl FnOnce(&mut AccountsDocument) -> Result<T, StoreError>,
    ) -> Result<T, StoreError> {
        let mut document = self.document.lock();
        let backup = document.clone();

        let result = match mutate(&mut document) {
            Ok(value) => value,
            Err(e) => {
                *document = backup;
                return Err(e);
            }
        };

        let encoded = match serde_json::to_string_pretty(&*document) {
            Ok(encoded) => encoded,
            Err(e) => {
                *document = backup;
                return Err(StoreError::PersistFailed(format!(
                    "参加者ファイルの JSON 生成に失敗しました: {e}"
                )));
            }
        };

        if let Err(e) = write_atomic(&self.path, &encoded) {
            *document = backup;
            return Err(StoreError::PersistFailed(e));
        }

        Ok(result)
    }

    // -------------------------------------------------------------------------
    // 読み取り
    // -------------------------------------------------------------------------

    /// 名前でアカウントを引く（大文字小文字を区別しない）。
    /// ログインの署名検証に使う公開鍵を取り出すために使う。
    pub fn find_account(&self, name: &str) -> Option<Account> {
        self.document.lock().find_account(name).cloned()
    }

    /// そのアカウントが指定リポジトリに持つ**有効な**権限の役割を返す。
    ///
    /// 権限サービス（`permission/`）が Lore からの
    /// `CheckUserPermission` に答えるために使う。
    /// **毎回この台帳を見るので、失効はトークンの期限を待たずに即座に効く。**
    /// 名前の照合は他と同じく ASCII の大文字小文字を区別しない。
    pub fn active_role(&self, name: &str, repository_id: &str) -> Option<Role> {
        self.document
            .lock()
            .find_account(name)
            .and_then(|account| account.find_grant(repository_id))
            .filter(|grant| grant.is_active())
            .map(|grant| grant.role)
    }

    /// そのアカウントが持つ**有効な**権限を全部返す（リポジトリ ID と役割の組）。
    ///
    /// 権限サービスが `LookupUserPermissions`（`repository list`）に答えるために使う。
    pub fn active_grants(&self, name: &str) -> Vec<(String, Role)> {
        self.document
            .lock()
            .find_account(name)
            .map(|account| {
                account
                    .grants
                    .iter()
                    .filter(|grant| grant.is_active())
                    .map(|grant| (grant.repository_id.clone(), grant.role))
                    .collect()
            })
            .unwrap_or_default()
    }

    /// 新しいリポジトリの作成者を、そのリポジトリの owner として台帳へ登録する。
    ///
    /// Lore は `RepositoryCreate` の途中で
    /// `ucs.auth.RebacApi/CreateResource` を呼ぶ。そこで**リポジトリ ID が
    /// 初めて確定する**ので、この瞬間に owner を記録してしまえば
    /// ループバックからの `POST /v1/bootstrap` が要らなくなる。
    ///
    /// 前提と判定:
    ///   - アカウントは既に存在していること（トークンを持っている＝ログイン済み）。
    ///     居なければ `MemberNotFound`。
    ///   - そのリポジトリに**別人の**有効な owner が既に居れば `OwnerExists`。
    ///     （同じ ID を後から奪いに来る経路を塞ぐ。作成者自身なら作り直しなので通す。）
    ///
    /// 成功したら、そのアカウントの当該リポジトリの権限を owner／有効にする。
    ///
    /// # Errors
    /// 入力の形式不正、アカウント不在、別人がオーナー、保存の失敗。
    pub fn register_repository_owner(
        &self,
        name: &str,
        repository_id: &str,
        project_name: &str,
        now_ms: u64,
    ) -> Result<String, StoreError> {
        validate_name(name).map_err(StoreError::InvalidName)?;
        if !validate_repository_id(repository_id) {
            return Err(StoreError::InvalidRepositoryId);
        }
        if project_name.chars().count() > PROJECT_NAME_MAX_CHARS {
            return Err(StoreError::InvalidProjectName);
        }

        self.mutate_and_persist(|document| {
            // 作成者のアカウントを先に確定させる（表示名は台帳のものを使う）。
            let display_name = document
                .find_account(name)
                .map(|account| account.name.clone())
                .ok_or(StoreError::MemberNotFound)?;

            // 別人が既に owner なら拒否する。
            let taken_by_other = document.accounts.iter().any(|account| {
                account.is_active_owner_of(repository_id)
                    && normalize_name_for_uniqueness(&account.name)
                        != normalize_name_for_uniqueness(&display_name)
            });
            if taken_by_other {
                return Err(StoreError::OwnerExists);
            }

            let account = document
                .find_account_mut(&display_name)
                .expect("直前に見つけたアカウントが消えている");

            match account.find_grant_mut(repository_id) {
                Some(grant) => {
                    grant.role = Role::Owner;
                    grant.status = GrantStatus::Active;
                    grant.project_name = project_name.to_string();
                }
                None => account.grants.push(Grant {
                    repository_id: repository_id.to_string(),
                    project_name: project_name.to_string(),
                    role: Role::Owner,
                    status: GrantStatus::Active,
                    added_at: now_ms,
                }),
            }

            Ok(display_name)
        })
    }

    /// 指定リポジトリの参加者一覧を返す（失効済みも含む）。
    ///
    /// # Errors
    /// 呼び出し元が owner でない場合（`Forbidden`）。
    pub fn list_members(
        &self,
        caller_name: &str,
        repository_id: &str,
    ) -> Result<Vec<MemberView>, StoreError> {
        if !validate_repository_id(repository_id) {
            return Err(StoreError::InvalidRepositoryId);
        }

        let document = self.document.lock();
        Self::require_owner(&document, caller_name, repository_id)?;

        let mut members: Vec<MemberView> = document
            .accounts
            .iter()
            .filter_map(|account| {
                account.find_grant(repository_id).map(|grant| MemberView {
                    name: account.name.clone(),
                    role: grant.role,
                    status: grant.status,
                    added_at: grant.added_at,
                })
            })
            .collect();

        // 参加順に並べる（UI で毎回順序が変わらないように）
        members.sort_by_key(|m| (m.added_at, m.name.clone()));
        Ok(members)
    }

    /// 呼び出し元がそのリポジトリの有効な owner であることを確かめる。
    fn require_owner(
        document: &AccountsDocument,
        caller_name: &str,
        repository_id: &str,
    ) -> Result<(), StoreError> {
        let is_owner = document
            .find_account(caller_name)
            .is_some_and(|account| account.is_active_owner_of(repository_id));
        if is_owner {
            Ok(())
        } else {
            Err(StoreError::Forbidden)
        }
    }

    // -------------------------------------------------------------------------
    // 登録
    // -------------------------------------------------------------------------

    /// 最初のオーナーを登録する（`POST /v1/bootstrap`）。
    ///
    /// そのリポジトリに有効な owner がまだ居ないときだけ成功する。
    pub fn bootstrap_owner(
        &self,
        name: &str,
        public_key: &str,
        repository_id: &str,
        project_name: &str,
        now_ms: u64,
    ) -> Result<JoinResult, StoreError> {
        validate_inputs(name, public_key, repository_id, project_name)?;

        self.mutate_and_persist(|document| {
            if document.has_active_owner(repository_id) {
                return Err(StoreError::OwnerExists);
            }
            upsert_account_with_grant(
                document,
                name,
                public_key,
                repository_id,
                project_name,
                Role::Owner,
                now_ms,
            )
        })
    }

    /// 招待コードでプロジェクトへ参加する（`POST /v1/join`）。
    ///
    /// コードは使い捨て。成功した時点で使用済みになる。
    pub fn join_with_invite(
        &self,
        invite_code: &str,
        name: &str,
        public_key: &str,
        now_ms: u64,
    ) -> Result<JoinResult, StoreError> {
        if invite_code.len() > INVITE_CODE_MAX_CHARS {
            return Err(StoreError::InviteInvalid);
        }
        // 名前と鍵の形式は、招待の正否より先に見る。
        // （形式エラーで招待コードを 1 つ無駄にさせないため）
        if let Err(e) = validate_name(name) {
            return Err(StoreError::InvalidName(e));
        }
        let public_key_valid = parse_public_key(public_key);
        if let Err(e) = public_key_valid {
            return Err(StoreError::InvalidPublicKey(e));
        }

        let code_hash = sha256_base64url(invite_code);

        self.mutate_and_persist(|document| {
            // 使える招待を探す。ハッシュ同士の比較は定数時間で行う。
            let index = document
                .invites
                .iter()
                .position(|invite| {
                    constant_time_eq(&invite.code_hash, &code_hash)
                        && invite.is_usable_at(now_ms)
                })
                .ok_or(StoreError::InviteInvalid)?;

            let (repository_id, project_name, role) = {
                let invite = &document.invites[index];
                (
                    invite.repository_id.clone(),
                    invite.project_name.clone(),
                    invite.role,
                )
            };

            let result = upsert_account_with_grant(
                document,
                name,
                public_key,
                &repository_id,
                &project_name,
                role,
                now_ms,
            )?;

            // 成功したときだけ使用済みにする
            // （名前の衝突で失敗した招待は、名前を変えてもう一度使えるべき）
            let invite = &mut document.invites[index];
            invite.used_at = Some(now_ms);
            invite.used_by = Some(result.name.clone());

            Ok(result)
        })
    }

    // -------------------------------------------------------------------------
    // 招待
    // -------------------------------------------------------------------------

    /// 招待コードを発行する（`POST /v1/invites`）。owner だけが呼べる。
    ///
    /// 返した平文のコードはサーバに残らない（保存するのはハッシュだけ）。
    pub fn create_invite(
        &self,
        caller_name: &str,
        repository_id: &str,
        role: Role,
        expires_in_hours: u64,
        now_ms: u64,
    ) -> Result<CreatedInvite, StoreError> {
        if !validate_repository_id(repository_id) {
            return Err(StoreError::InvalidRepositoryId);
        }
        if !(INVITE_MIN_HOURS..=INVITE_MAX_HOURS).contains(&expires_in_hours) {
            return Err(StoreError::InvalidExpiry);
        }

        let invite_code =
            random_token(INVITE_CODE_BYTES).map_err(|_| StoreError::RandomFailed)?;
        let code_hash = sha256_base64url(&invite_code);
        let expires_at_ms = now_ms + expires_in_hours * MILLIS_PER_HOUR;

        self.mutate_and_persist(|document| {
            Self::require_owner(document, caller_name, repository_id)?;

            // プロジェクト名は owner の権限に載っているものを引き継ぐ
            // （招待を受ける側に表示名を教えるため）。
            let project_name = document
                .find_account(caller_name)
                .and_then(|account| {
                    account
                        .find_grant(repository_id)
                        .map(|grant| grant.project_name.clone())
                })
                .unwrap_or_default();

            document.invites.push(Invite {
                code_hash: code_hash.clone(),
                repository_id: repository_id.to_string(),
                project_name,
                role,
                created_by: caller_name.to_string(),
                created_at: now_ms,
                expires_at: expires_at_ms,
                used_at: None,
                used_by: None,
            });

            Ok(())
        })?;

        Ok(CreatedInvite {
            invite_code,
            expires_at_ms,
        })
    }

    // -------------------------------------------------------------------------
    // 失効
    // -------------------------------------------------------------------------

    /// 参加者の権限を失効させる（`POST /v1/members/revoke`）。owner だけが呼べる。
    ///
    /// **owner ロールの権限は失効できない**（自分自身を含む）。
    /// オーナーが居なくなったリポジトリは誰も管理できなくなるため。
    pub fn revoke_member(
        &self,
        caller_name: &str,
        repository_id: &str,
        target_name: &str,
    ) -> Result<String, StoreError> {
        if !validate_repository_id(repository_id) {
            return Err(StoreError::InvalidRepositoryId);
        }

        self.mutate_and_persist(|document| {
            Self::require_owner(document, caller_name, repository_id)?;

            let account = document
                .find_account_mut(target_name)
                .ok_or(StoreError::MemberNotFound)?;
            let display_name = account.name.clone();
            let grant = account
                .find_grant_mut(repository_id)
                .ok_or(StoreError::MemberNotFound)?;

            if grant.role == Role::Owner {
                return Err(StoreError::CannotRevokeOwner);
            }

            grant.status = GrantStatus::Revoked;
            Ok(display_name)
        })
    }
}

// -----------------------------------------------------------------------------
// 共通の内部処理
// -----------------------------------------------------------------------------

/// 登録系の入力をまとめて検証する。
fn validate_inputs(
    name: &str,
    public_key: &str,
    repository_id: &str,
    project_name: &str,
) -> Result<(), StoreError> {
    validate_name(name).map_err(StoreError::InvalidName)?;
    parse_public_key(public_key).map_err(StoreError::InvalidPublicKey)?;
    if !validate_repository_id(repository_id) {
        return Err(StoreError::InvalidRepositoryId);
    }
    if project_name.chars().count() > PROJECT_NAME_MAX_CHARS {
        return Err(StoreError::InvalidProjectName);
    }
    Ok(())
}

/// アカウントを作る（または既存のものを使う）うえで、指定リポジトリの権限を付ける。
///
/// 判定の順序:
///   1. 同じ公開鍵のアカウントが在れば、**それが本人**。名前は既存のものを使う
///      （名前は作成後に変更できないので、要求された名前では上書きしない）。
///   2. 無ければ、要求された名前が空いているか見る。取られていれば `NameTaken`。
///   3. 空いていれば新規作成。
///
/// 既に同じリポジトリの権限を持っていた場合は、役割と状態を更新する
/// （失効された人がもう一度招待されたら復活できる）。
fn upsert_account_with_grant(
    document: &mut AccountsDocument,
    name: &str,
    public_key: &str,
    repository_id: &str,
    project_name: &str,
    role: Role,
    now_ms: u64,
) -> Result<JoinResult, StoreError> {
    // --- 1. 同じ公開鍵の既存アカウント ---------------------------------
    let existing_name = document
        .find_account_by_public_key(public_key)
        .map(|account| account.name.clone());

    let account_name = match existing_name {
        Some(existing) => existing,
        None => {
            // --- 2. 名前の衝突を見る ---------------------------------
            if document.find_account(name).is_some() {
                return Err(StoreError::NameTaken);
            }
            // --- 3. 新規作成 -----------------------------------------
            document.accounts.push(Account {
                name: name.to_string(),
                public_key: public_key.to_string(),
                grants: Vec::new(),
                created_at: now_ms,
            });
            name.to_string()
        }
    };

    let account = document
        .find_account_mut(&account_name)
        .expect("直前に作成／取得したアカウントが見つからない");

    match account.find_grant_mut(repository_id) {
        Some(grant) => {
            // 既に権限がある: 役割と状態を更新する（失効からの復活を含む）
            grant.role = role;
            grant.status = GrantStatus::Active;
            grant.project_name = project_name.to_string();
        }
        None => account.grants.push(Grant {
            repository_id: repository_id.to_string(),
            project_name: project_name.to_string(),
            role,
            status: GrantStatus::Active,
            added_at: now_ms,
        }),
    }

    Ok(JoinResult {
        name: account_name,
        repository_id: repository_id.to_string(),
        project_name: project_name.to_string(),
        role,
    })
}

// =============================================================================
// 単体テスト
// =============================================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// テスト内の基準時刻。
    const T0: u64 = 1_800_000_000_000;
    /// テスト用リポジトリ ID（32 桁の 16 進数）。
    const REPO_A: &str = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const REPO_B: &str = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    /// テスト用プロジェクト名。
    const PROJECT: &str = "WarashibeFishing";
    /// 招待の既定有効期間（時間）。
    const INVITE_HOURS: u64 = 72;

    /// 形式の正しいテスト用公開鍵を作る（実際に検証はしないので中身は任意）。
    fn fake_public_key(seed: u8) -> String {
        let mut bytes = [seed; super::super::user_key::UNCOMPRESSED_POINT_LEN];
        bytes[0] = 0x04;
        super::super::crypto::base64url_encode(&bytes)
    }

    /// 一時ディレクトリ上に空のストアを作る。
    fn new_store() -> (tempfile::TempDir, AccountStore) {
        let dir = tempfile::tempdir().unwrap();
        let store = AccountStore::open(dir.path()).unwrap();
        (dir, store)
    }

    /// owner を 1 人登録済みのストアを作る。
    fn store_with_owner() -> (tempfile::TempDir, AccountStore) {
        let (dir, store) = new_store();
        store
            .bootstrap_owner("tsubasa", &fake_public_key(1), REPO_A, PROJECT, T0)
            .unwrap();
        (dir, store)
    }

    /// bootstrap でオーナーが 1 人だけ作られること。
    #[test]
    fn bootstrap_creates_the_first_owner() {
        let (_dir, store) = new_store();

        let result = store
            .bootstrap_owner("tsubasa", &fake_public_key(1), REPO_A, PROJECT, T0)
            .unwrap();
        assert_eq!(result.name, "tsubasa");
        assert_eq!(result.role, Role::Owner);
        assert_eq!(store.account_count(), 1);

        // 2 人目のオーナーは作れない
        assert_eq!(
            store
                .bootstrap_owner("other", &fake_public_key(2), REPO_A, PROJECT, T0)
                .unwrap_err(),
            StoreError::OwnerExists
        );
    }

    /// 別リポジトリなら bootstrap できること。
    #[test]
    fn bootstrap_is_per_repository() {
        let (_dir, store) = store_with_owner();
        assert!(
            store
                .bootstrap_owner("tsubasa", &fake_public_key(1), REPO_B, "Another", T0)
                .is_ok()
        );
        // 同じ公開鍵なのでアカウントは増えず、権限だけ増える
        assert_eq!(store.account_count(), 1);
        let account = store.find_account("tsubasa").unwrap();
        assert_eq!(account.grants.len(), 2);
    }

    /// 名前・公開鍵・リポジトリ ID の形式が検証されること。
    #[test]
    fn bootstrap_validates_inputs() {
        let (_dir, store) = new_store();

        assert!(matches!(
            store
                .bootstrap_owner("", &fake_public_key(1), REPO_A, PROJECT, T0)
                .unwrap_err(),
            StoreError::InvalidName(_)
        ));
        assert!(matches!(
            store
                .bootstrap_owner("tsubasa", "not-a-key", REPO_A, PROJECT, T0)
                .unwrap_err(),
            StoreError::InvalidPublicKey(_)
        ));
        assert_eq!(
            store
                .bootstrap_owner("tsubasa", &fake_public_key(1), "short", PROJECT, T0)
                .unwrap_err(),
            StoreError::InvalidRepositoryId
        );
        assert_eq!(
            store
                .bootstrap_owner(
                    "tsubasa",
                    &fake_public_key(1),
                    REPO_A,
                    &"x".repeat(PROJECT_NAME_MAX_CHARS + 1),
                    T0
                )
                .unwrap_err(),
            StoreError::InvalidProjectName
        );
    }

    /// owner だけが招待を発行できること。
    #[test]
    fn only_owner_can_create_invites() {
        let (_dir, store) = store_with_owner();

        // owner は発行できる
        let invite = store
            .create_invite("tsubasa", REPO_A, Role::Member, INVITE_HOURS, T0)
            .unwrap();
        assert!(!invite.invite_code.is_empty());
        assert_eq!(invite.expires_at_ms, T0 + INVITE_HOURS * MILLIS_PER_HOUR);

        // その招待で member を 1 人入れる
        store
            .join_with_invite(&invite.invite_code, "member1", &fake_public_key(2), T0)
            .unwrap();

        // member は発行できない
        assert_eq!(
            store
                .create_invite("member1", REPO_A, Role::Member, INVITE_HOURS, T0)
                .unwrap_err(),
            StoreError::Forbidden
        );
        // 存在しない人も発行できない
        assert_eq!(
            store
                .create_invite("nobody", REPO_A, Role::Member, INVITE_HOURS, T0)
                .unwrap_err(),
            StoreError::Forbidden
        );
    }

    /// 招待コードの平文が保存されていないこと（ハッシュだけであること）。
    #[test]
    fn invite_code_plaintext_is_not_stored() {
        let (dir, store) = store_with_owner();
        let invite = store
            .create_invite("tsubasa", REPO_A, Role::Member, INVITE_HOURS, T0)
            .unwrap();

        let saved = std::fs::read_to_string(dir.path().join(ACCOUNTS_FILE_NAME)).unwrap();
        assert!(
            !saved.contains(&invite.invite_code),
            "招待コードの平文がファイルに残っている"
        );
        assert!(saved.contains(&sha256_base64url(&invite.invite_code)));
    }

    /// 招待は 1 回だけ使えること。
    #[test]
    fn invite_can_be_used_only_once() {
        let (_dir, store) = store_with_owner();
        let invite = store
            .create_invite("tsubasa", REPO_A, Role::Member, INVITE_HOURS, T0)
            .unwrap();

        store
            .join_with_invite(&invite.invite_code, "member1", &fake_public_key(2), T0)
            .unwrap();

        assert_eq!(
            store
                .join_with_invite(&invite.invite_code, "member2", &fake_public_key(3), T0)
                .unwrap_err(),
            StoreError::InviteInvalid
        );
    }

    /// 期限切れの招待は使えないこと。
    #[test]
    fn expired_invite_is_rejected() {
        let (_dir, store) = store_with_owner();
        let invite = store
            .create_invite("tsubasa", REPO_A, Role::Member, INVITE_HOURS, T0)
            .unwrap();

        let after_expiry = T0 + INVITE_HOURS * MILLIS_PER_HOUR;
        assert_eq!(
            store
                .join_with_invite(&invite.invite_code, "member1", &fake_public_key(2), after_expiry)
                .unwrap_err(),
            StoreError::InviteInvalid
        );
        // 期限の 1 ミリ秒前なら通る
        assert!(
            store
                .join_with_invite(
                    &invite.invite_code,
                    "member1",
                    &fake_public_key(2),
                    after_expiry - 1
                )
                .is_ok()
        );
    }

    /// 存在しない招待コードは弾かれること。
    #[test]
    fn unknown_invite_code_is_rejected() {
        let (_dir, store) = store_with_owner();
        assert_eq!(
            store
                .join_with_invite("completely-made-up", "member1", &fake_public_key(2), T0)
                .unwrap_err(),
            StoreError::InviteInvalid
        );
    }

    /// 招待の有効期間が範囲外なら弾かれること。
    #[test]
    fn invite_expiry_range_is_checked() {
        let (_dir, store) = store_with_owner();
        assert_eq!(
            store
                .create_invite("tsubasa", REPO_A, Role::Member, 0, T0)
                .unwrap_err(),
            StoreError::InvalidExpiry
        );
        assert_eq!(
            store
                .create_invite("tsubasa", REPO_A, Role::Member, INVITE_MAX_HOURS + 1, T0)
                .unwrap_err(),
            StoreError::InvalidExpiry
        );
    }

    /// 名前の衝突は 409 相当になること。衝突しても招待は消費されないこと。
    #[test]
    fn name_collision_is_reported_and_invite_survives() {
        let (_dir, store) = store_with_owner();
        let invite = store
            .create_invite("tsubasa", REPO_A, Role::Member, INVITE_HOURS, T0)
            .unwrap();

        // owner と同じ名前（大文字小文字違い）で入ろうとする
        assert_eq!(
            store
                .join_with_invite(&invite.invite_code, "TSUBASA", &fake_public_key(9), T0)
                .unwrap_err(),
            StoreError::NameTaken
        );

        // 招待はまだ使える
        assert!(
            store
                .join_with_invite(&invite.invite_code, "member1", &fake_public_key(9), T0)
                .is_ok()
        );
    }

    /// 同じ公開鍵の既存アカウントには、権限を足すだけであること（契約 3 章）。
    #[test]
    fn same_public_key_only_adds_a_grant() {
        let (_dir, store) = new_store();
        store
            .bootstrap_owner("tsubasa", &fake_public_key(1), REPO_A, PROJECT, T0)
            .unwrap();
        store
            .bootstrap_owner("ignored-name", &fake_public_key(1), REPO_B, "Other", T0)
            .unwrap();

        assert_eq!(store.account_count(), 1, "アカウントが増えている");
        let account = store.find_account("tsubasa").unwrap();
        assert_eq!(account.name, "tsubasa", "名前が上書きされている");
        assert_eq!(account.grants.len(), 2);
    }

    /// 失効した人は `has_any_active_grant()` が false になり、再招待で復活できること。
    #[test]
    fn revoke_then_reinvite() {
        let (_dir, store) = store_with_owner();
        let invite = store
            .create_invite("tsubasa", REPO_A, Role::Member, INVITE_HOURS, T0)
            .unwrap();
        store
            .join_with_invite(&invite.invite_code, "member1", &fake_public_key(2), T0)
            .unwrap();

        store.revoke_member("tsubasa", REPO_A, "member1").unwrap();
        let account = store.find_account("member1").unwrap();
        assert!(!account.has_any_active_grant(), "失効が反映されていない");

        // もう一度招待すれば復活する
        let invite2 = store
            .create_invite("tsubasa", REPO_A, Role::Member, INVITE_HOURS, T0)
            .unwrap();
        store
            .join_with_invite(&invite2.invite_code, "member1", &fake_public_key(2), T0)
            .unwrap();
        assert!(store.find_account("member1").unwrap().has_any_active_grant());
    }

    /// owner 以外は失効させられないこと。owner 自身は失効できないこと。
    #[test]
    fn revoke_permissions_are_enforced() {
        let (_dir, store) = store_with_owner();
        let invite = store
            .create_invite("tsubasa", REPO_A, Role::Member, INVITE_HOURS, T0)
            .unwrap();
        store
            .join_with_invite(&invite.invite_code, "member1", &fake_public_key(2), T0)
            .unwrap();

        // member は失効させられない
        assert_eq!(
            store.revoke_member("member1", REPO_A, "member1").unwrap_err(),
            StoreError::Forbidden
        );
        // owner は失効できない
        assert_eq!(
            store.revoke_member("tsubasa", REPO_A, "tsubasa").unwrap_err(),
            StoreError::CannotRevokeOwner
        );
        // 居ない人は失効できない
        assert_eq!(
            store.revoke_member("tsubasa", REPO_A, "nobody").unwrap_err(),
            StoreError::MemberNotFound
        );
    }

    /// 参加者一覧は owner だけが取れ、失効済みも含めて返ること。
    #[test]
    fn member_list_is_owner_only_and_includes_revoked() {
        let (_dir, store) = store_with_owner();
        let invite = store
            .create_invite("tsubasa", REPO_A, Role::Member, INVITE_HOURS, T0)
            .unwrap();
        store
            .join_with_invite(&invite.invite_code, "member1", &fake_public_key(2), T0 + 1)
            .unwrap();
        store.revoke_member("tsubasa", REPO_A, "member1").unwrap();

        let members = store.list_members("tsubasa", REPO_A).unwrap();
        assert_eq!(members.len(), 2);
        assert_eq!(members[0].name, "tsubasa");
        assert_eq!(members[0].status, GrantStatus::Active);
        assert_eq!(members[1].name, "member1");
        assert_eq!(members[1].status, GrantStatus::Revoked);

        assert_eq!(
            store.list_members("member1", REPO_A).unwrap_err(),
            StoreError::Forbidden
        );
    }

    /// accounts.json の往復（保存 → 読み直しで同じ状態になる）。
    #[test]
    fn accounts_file_round_trips() {
        let dir = tempfile::tempdir().unwrap();
        let invite_code;
        {
            let store = AccountStore::open(dir.path()).unwrap();
            store
                .bootstrap_owner("tsubasa", &fake_public_key(1), REPO_A, PROJECT, T0)
                .unwrap();
            let invite = store
                .create_invite("tsubasa", REPO_A, Role::Member, INVITE_HOURS, T0)
                .unwrap();
            invite_code = invite.invite_code.clone();
            store
                .join_with_invite(&invite_code, "member1", &fake_public_key(2), T0)
                .unwrap();
            store.revoke_member("tsubasa", REPO_A, "member1").unwrap();
        }

        // 別インスタンスで開き直す（サーバ再起動に相当）
        let reopened = AccountStore::open(dir.path()).unwrap();
        assert_eq!(reopened.account_count(), 2);

        let owner = reopened.find_account("tsubasa").unwrap();
        assert!(owner.is_active_owner_of(REPO_A));
        assert_eq!(owner.public_key, fake_public_key(1));

        let member = reopened.find_account("member1").unwrap();
        assert!(!member.has_any_active_grant());

        // 使用済みの招待も引き継がれ、再利用できないこと
        assert_eq!(
            reopened
                .join_with_invite(&invite_code, "member2", &fake_public_key(3), T0)
                .unwrap_err(),
            StoreError::InviteInvalid
        );
    }

    /// 壊れたファイルは黙って空にせずエラーにすること。
    #[test]
    fn broken_accounts_file_is_an_error() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(dir.path().join(ACCOUNTS_FILE_NAME), "{ broken").unwrap();
        assert!(AccountStore::open(dir.path()).is_err());
    }

    /// 空ファイルは「まだ誰も居ない」として扱うこと。
    #[test]
    fn empty_accounts_file_starts_fresh() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(dir.path().join(ACCOUNTS_FILE_NAME), "").unwrap();
        let store = AccountStore::open(dir.path()).unwrap();
        assert_eq!(store.account_count(), 0);
    }
}
