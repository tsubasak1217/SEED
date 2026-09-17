// =============================================================================
// SEED アカウント発行窓口 : 永続データのモデル
// =============================================================================
// `accounts.json` に書き出す構造体と、そこに出てくる列挙（役割・状態）、
// および「名前の規則」の検証だけを持つファイル。
//
// ここには I/O を一切置かない（読み書きは store.rs、HTTP は http.rs）。
// 「データの形」と「データ単体で決まる妥当性」だけに責任を限定することで、
// 単体テストがファイルシステムもネットワークも要らなくなる。
//
// 設計上の判断（契約 docs/seed_accounts.md からの明確化）:
//   - 失効（status）は **アカウント単位ではなくリポジトリ権限（grant）単位**で持つ。
//     契約の `POST /v1/members/revoke` が `{repository_id, name}` を取り、
//     `GET /v1/members` が `{name, role, status, added_at}` を返す形なので、
//     状態はリポジトリごとに決まるのが自然なため。
//     「アカウント全体が使えない」は「有効な grant が 1 件も無い」で表現する。
//   - 名前の一意判定は **ASCII の大文字小文字を無視**して行う。
//     `alice` と `Alice` が別人として並ぶと、ロック所有者の表示で
//     取り違えが起きる（なりすましの温床になる）ため。表示は入力どおり。
// =============================================================================

use serde::Deserialize;
use serde::Serialize;

// -----------------------------------------------------------------------------
// 定数（マジックナンバー・マジック文字列を作らないため、すべて名前を付ける）
// -----------------------------------------------------------------------------

/// `accounts.json` のスキーマバージョン。
/// 将来フォーマットを変えたときに読み込み側で分岐できるようにしておく。
pub const ACCOUNTS_SCHEMA_VERSION: u32 = 1;

/// 参加者名の最小文字数（Unicode のコードポイント数で数える）。
pub const NAME_MIN_CHARS: usize = 1;

/// 参加者名の最大文字数（同上）。
/// 契約 2 章「1〜32 文字」に対応する。
pub const NAME_MAX_CHARS: usize = 32;

/// 参加者名で ASCII として明示的に許可する記号。
/// 英数字はこれとは別に `is_ascii_alphanumeric()` で許可する。
const NAME_ALLOWED_ASCII_SYMBOLS: [char; 3] = ['_', '-', '.'];

/// 参加者名で許可する非 ASCII 文字の範囲（両端を含むコードポイント）。
///
/// **エディタ側 `editor/src/Accounts/Crypto/AccountNameRule.cs` と同一の表**にすること
/// （契約 `docs/seed_accounts.md` 2 章）。サーバが最終判定者なので、ここを緩めると
/// 全角英数字（`ｔｓｕｂａｓａ`）や半角カナで他人に似た名前を登録でき、ロックの所有者表示で紛れる。
///   - U+3005–U+3007 … 々 〆 〇
///   - U+3041–U+309F … ひらがな
///   - U+30A0–U+30FF … カタカナ（長音符 ー を含む）
///   - U+3400–U+4DBF … CJK 統合漢字拡張 A
///   - U+4E00–U+9FFF … CJK 統合漢字
const NAME_ALLOWED_NON_ASCII_RANGES: [(u32, u32); 5] = [
    (0x3005, 0x3007),
    (0x3041, 0x309F),
    (0x30A0, 0x30FF),
    (0x3400, 0x4DBF),
    (0x4E00, 0x9FFF),
];

/// リポジトリ ID の文字数（Lore の `RepositoryId` は 16 バイト = 32 桁の 16 進数）。
pub const REPOSITORY_ID_HEX_CHARS: usize = 32;

/// プロジェクト名の最大文字数。表示用の文字列なので緩めだが上限は設ける。
pub const PROJECT_NAME_MAX_CHARS: usize = 128;

// -----------------------------------------------------------------------------
// 役割と状態
// -----------------------------------------------------------------------------

/// リポジトリに対する参加者の役割。
///
/// JWT の `resources[].permission` にそのまま小文字で載る。
/// Lore v0.9.0 は「エントリが在るか」でアクセスを判定し、
/// `owner` は他人のロックの強制解放に効く。
#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Role {
    /// プロジェクトの持ち主。招待の発行・参加者の失効・他人のロックの解放ができる。
    Owner,
    /// 一般の参加者。読み書きはできるが、招待の発行と失効はできない。
    Member,
}

impl Role {
    /// JWT の `permission` 配列や JSON へ載せるときの文字列表現。
    pub fn as_str(self) -> &'static str {
        match self {
            Role::Owner => "owner",
            Role::Member => "member",
        }
    }
}

/// リポジトリ権限の状態。
#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum GrantStatus {
    /// 有効。トークンの `resources` に載る。
    #[default]
    Active,
    /// オーナーによって失効された。新しいトークンには載らない。
    /// （既に発行済みのトークンは期限まで有効。Lore に失効の仕組みが無いため）
    Revoked,
}

impl GrantStatus {
    /// JSON / API 応答での文字列表現。
    pub fn as_str(self) -> &'static str {
        match self {
            GrantStatus::Active => "active",
            GrantStatus::Revoked => "revoked",
        }
    }
}

// -----------------------------------------------------------------------------
// 永続データ本体
// -----------------------------------------------------------------------------

/// 1 つのリポジトリに対する 1 人の権限。
#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct Grant {
    /// 対象リポジトリの ID（Lore の `.lore/id`。32 桁の 16 進数）
    pub repository_id: String,
    /// 表示用のプロジェクト名（UI に出すだけで、権限判定には使わない）
    pub project_name: String,
    /// 役割
    pub role: Role,
    /// 有効／失効
    #[serde(default)]
    pub status: GrantStatus,
    /// 参加した時刻（UNIX epoch ミリ秒）
    pub added_at: u64,
}

impl Grant {
    /// この権限が現在有効かどうか。
    pub fn is_active(&self) -> bool {
        self.status == GrantStatus::Active
    }
}

/// 1 人の参加者。
///
/// 秘密鍵はサーバに存在しない（利用者の PC から出ない）。
/// サーバが持つのは公開鍵だけ。
#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct Account {
    /// 参加者名。サーバ内で一意・作成後は変更不可。
    /// JWT の `sub` とロック所有者の表示に使う。
    pub name: String,
    /// 公開鍵。ECDSA P-256 の SEC1 非圧縮点（65 バイト）を
    /// base64url（パディングなし）にしたもの。
    pub public_key: String,
    /// 参加しているリポジトリごとの権限
    pub grants: Vec<Grant>,
    /// アカウント作成時刻（UNIX epoch ミリ秒）
    pub created_at: u64,
}

impl Account {
    /// 指定リポジトリの権限を探す（失効済みも含む）。
    pub fn find_grant(&self, repository_id: &str) -> Option<&Grant> {
        self.grants
            .iter()
            .find(|g| g.repository_id == repository_id)
    }

    /// 指定リポジトリの権限を可変で探す（失効済みも含む）。
    pub fn find_grant_mut(&mut self, repository_id: &str) -> Option<&mut Grant> {
        self.grants
            .iter_mut()
            .find(|g| g.repository_id == repository_id)
    }

    /// 有効な権限を 1 件でも持っているか。
    /// 「失効した参加者は新しいトークンを取れない」の判定に使う。
    pub fn has_any_active_grant(&self) -> bool {
        self.grants.iter().any(Grant::is_active)
    }

    /// 指定リポジトリに対して owner として有効か。
    pub fn is_active_owner_of(&self, repository_id: &str) -> bool {
        self.find_grant(repository_id)
            .is_some_and(|g| g.is_active() && g.role == Role::Owner)
    }
}

/// 発行済みの招待コード 1 件。
///
/// **コードの平文は保存しない。** 保存するのはハッシュだけで、
/// 平文は発行時の応答で 1 回だけ返す（契約 3 章）。
#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct Invite {
    /// 招待コードのハッシュ（SHA-256 を base64url にしたもの）
    pub code_hash: String,
    /// この招待で参加できるリポジトリ
    pub repository_id: String,
    /// 表示用のプロジェクト名
    pub project_name: String,
    /// 参加したときに付与される役割
    pub role: Role,
    /// 発行した owner の名前（監査用）
    pub created_by: String,
    /// 発行時刻（UNIX epoch ミリ秒）
    pub created_at: u64,
    /// 有効期限（UNIX epoch ミリ秒）
    pub expires_at: u64,
    /// 使用された時刻。未使用なら `None`。
    /// 一度使ったら二度と使えない（使い捨て）。
    #[serde(default)]
    pub used_at: Option<u64>,
    /// 使用した参加者の名前（監査用）
    #[serde(default)]
    pub used_by: Option<String>,
}

impl Invite {
    /// 指定時刻の時点でこの招待が使えるか。
    pub fn is_usable_at(&self, now_ms: u64) -> bool {
        self.used_at.is_none() && now_ms < self.expires_at
    }
}

/// `accounts.json` 全体の形。
#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct AccountsDocument {
    /// スキーマバージョン
    pub version: u32,
    /// 参加者の一覧
    pub accounts: Vec<Account>,
    /// 発行済み招待コードの一覧（使用済み・期限切れも監査のため残す）
    pub invites: Vec<Invite>,
}

impl Default for AccountsDocument {
    fn default() -> Self {
        Self {
            version: ACCOUNTS_SCHEMA_VERSION,
            accounts: Vec::new(),
            invites: Vec::new(),
        }
    }
}

impl AccountsDocument {
    /// 名前でアカウントを引く（大文字小文字を区別しない）。
    ///
    /// 一意判定と同じ比較規則で引くことで、
    /// 「登録はできたのにログインで見つからない」というズレを防ぐ。
    pub fn find_account(&self, name: &str) -> Option<&Account> {
        let key = normalize_name_for_uniqueness(name);
        self.accounts
            .iter()
            .find(|a| normalize_name_for_uniqueness(&a.name) == key)
    }

    /// 名前でアカウントを可変で引く（大文字小文字を区別しない）。
    pub fn find_account_mut(&mut self, name: &str) -> Option<&mut Account> {
        let key = normalize_name_for_uniqueness(name);
        self.accounts
            .iter_mut()
            .find(|a| normalize_name_for_uniqueness(&a.name) == key)
    }

    /// 公開鍵でアカウントを引く。
    /// 「同じ公開鍵の既存アカウントなら権限を足すだけ」（契約 3 章 `/v1/join`）の判定に使う。
    pub fn find_account_by_public_key(&self, public_key: &str) -> Option<&Account> {
        self.accounts.iter().find(|a| a.public_key == public_key)
    }

    /// そのリポジトリに有効な owner が居るか。
    /// `/v1/bootstrap` が「まだオーナーが居ないときだけ成功」を判定するのに使う。
    pub fn has_active_owner(&self, repository_id: &str) -> bool {
        self.accounts
            .iter()
            .any(|a| a.is_active_owner_of(repository_id))
    }
}

// -----------------------------------------------------------------------------
// 名前の規則
// -----------------------------------------------------------------------------

/// 名前の検証に失敗した理由。
/// HTTP 応答のメッセージを組み立てるために、理由ごとに分けて返す。
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum NameError {
    /// 短すぎる（空文字を含む）
    TooShort,
    /// 長すぎる
    TooLong,
    /// 前後に空白がある
    SurroundedByWhitespace,
    /// 使えない文字が含まれている
    ForbiddenCharacter,
}

impl NameError {
    /// 利用者へ返す日本語の説明。
    pub fn message(self) -> &'static str {
        match self {
            NameError::TooShort => "名前が空です",
            NameError::TooLong => "名前が長すぎます（32 文字まで）",
            NameError::SurroundedByWhitespace => "名前の前後に空白は使えません",
            NameError::ForbiddenCharacter => {
                "名前に使えない文字が含まれています（英数字・_ - . と日本語のみ）"
            }
        }
    }
}

/// 参加者名が規則を満たすか検証する。
///
/// 許可する文字:
///   - ASCII の英数字
///   - ASCII 記号のうち `_` `-` `.`
///   - 非 ASCII は `NAME_ALLOWED_NON_ASCII_RANGES` の範囲だけ
///     （ひらがな・カタカナ・漢字・々〆〇。**全角英数字と半角カナは不可**）
///
/// 空白・制御文字・絵文字・その他の記号はすべて拒否する。
/// 名前はロックの所有者表示に使われるため、見た目が紛れるものを入れない。
pub fn validate_name(name: &str) -> Result<(), NameError> {
    // 前後の空白は「うっかりコピペ」で入りやすいので、専用の理由で弾く。
    // （下の文字種チェックでも弾かれるが、原因が分かるメッセージを返したい）
    if name != name.trim() {
        return Err(NameError::SurroundedByWhitespace);
    }

    let char_count = name.chars().count();
    if char_count < NAME_MIN_CHARS {
        return Err(NameError::TooShort);
    }
    if char_count > NAME_MAX_CHARS {
        return Err(NameError::TooLong);
    }

    for c in name.chars() {
        let allowed = if c.is_ascii() {
            c.is_ascii_alphanumeric() || NAME_ALLOWED_ASCII_SYMBOLS.contains(&c)
        } else {
            // 非 ASCII は明示した範囲だけ通す（エディタと同じ表）。
            // Unicode の「英数字」で判定すると全角英数字や他言語の文字まで通り、
            // 見た目が紛れる名前を作れてしまうため、範囲で絞る。
            let code = c as u32;
            NAME_ALLOWED_NON_ASCII_RANGES
                .iter()
                .any(|(begin, end)| (*begin..=*end).contains(&code))
        };
        if !allowed {
            return Err(NameError::ForbiddenCharacter);
        }
    }

    Ok(())
}

/// 一意判定に使う正規化した名前。
///
/// ASCII の大文字小文字だけを畳む。`to_lowercase()` を使うと
/// トルコ語の I など言語依存の変換が入りうるので、ASCII に限定する。
pub fn normalize_name_for_uniqueness(name: &str) -> String {
    name.chars()
        .map(|c| if c.is_ascii() { c.to_ascii_lowercase() } else { c })
        .collect()
}

/// リポジトリ ID が Lore の形式（32 桁の 16 進数）かを検証する。
///
/// ここで形を固定しておかないと、`resources[].resource_id` に
/// 変な文字列が混ざってトークンの意味が壊れる。
pub fn validate_repository_id(repository_id: &str) -> bool {
    repository_id.len() == REPOSITORY_ID_HEX_CHARS
        && repository_id.chars().all(|c| c.is_ascii_hexdigit())
}

// =============================================================================
// 単体テスト
// =============================================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 名前の規則: 通るべきものが通ること。
    #[test]
    fn valid_names_are_accepted() {
        for name in [
            "tsubasa",
            "Tsubasa_1217",
            "a",
            "user-name.v2",
            "つばさ",
            "翼",
            "カタカナー",
            "々〆〇",
            "つばさ-01",
            &"a".repeat(NAME_MAX_CHARS),
        ] {
            assert!(validate_name(name).is_ok(), "拒否された: {name:?}");
        }
    }

    /// 名前の規則: 落とすべきものが落ちること。
    #[test]
    fn invalid_names_are_rejected() {
        assert_eq!(validate_name(""), Err(NameError::TooShort));
        assert_eq!(
            validate_name(&"a".repeat(NAME_MAX_CHARS + 1)),
            Err(NameError::TooLong)
        );
        assert_eq!(
            validate_name(" tsubasa"),
            Err(NameError::SurroundedByWhitespace)
        );
        assert_eq!(
            validate_name("tsubasa "),
            Err(NameError::SurroundedByWhitespace)
        );
        // 内部の空白・記号・制御文字・絵文字はすべて文字種で落ちる
        // 全角英数字・半角カナ・他言語の文字は「見た目が紛れる」ので通さない（エディタと同じ規則）
        for name in [
            "tsu basa", "tsubasa@example.com", "a\tb", "a\nb", "🙂", "a/b", "a:b",
            "ＡＢＣ123", "ｔｓｕｂａｓａ", "ｶﾀｶﾅ", "한국어", "émile",
        ] {
            assert_eq!(
                validate_name(name),
                Err(NameError::ForbiddenCharacter),
                "通ってしまった: {name:?}"
            );
        }
    }

    /// 一意判定は ASCII の大文字小文字を畳むこと。日本語は畳まないこと。
    #[test]
    fn uniqueness_folds_ascii_case_only() {
        assert_eq!(
            normalize_name_for_uniqueness("Alice"),
            normalize_name_for_uniqueness("alice")
        );
        assert_ne!(
            normalize_name_for_uniqueness("つばさ"),
            normalize_name_for_uniqueness("ツバサ")
        );
    }

    /// リポジトリ ID の形式検証。
    #[test]
    fn repository_id_format_is_checked() {
        assert!(validate_repository_id(&"a".repeat(REPOSITORY_ID_HEX_CHARS)));
        assert!(validate_repository_id("0123456789ABCDEF0123456789abcdef"));
        assert!(!validate_repository_id(""));
        assert!(!validate_repository_id(&"a".repeat(REPOSITORY_ID_HEX_CHARS - 1)));
        assert!(!validate_repository_id(&"a".repeat(REPOSITORY_ID_HEX_CHARS + 1)));
        // 16 進数でない文字が混ざっている
        assert!(!validate_repository_id(&format!(
            "{}g",
            "a".repeat(REPOSITORY_ID_HEX_CHARS - 1)
        )));
    }

    /// 権限の探索と有効判定。
    #[test]
    fn grant_lookup_and_active_checks() {
        let repo = "a".repeat(REPOSITORY_ID_HEX_CHARS);
        let other = "b".repeat(REPOSITORY_ID_HEX_CHARS);
        let account = Account {
            name: "owner1".to_string(),
            public_key: "pk".to_string(),
            created_at: 1,
            grants: vec![
                Grant {
                    repository_id: repo.clone(),
                    project_name: "Proj".to_string(),
                    role: Role::Owner,
                    status: GrantStatus::Active,
                    added_at: 1,
                },
                Grant {
                    repository_id: other.clone(),
                    project_name: "Other".to_string(),
                    role: Role::Member,
                    status: GrantStatus::Revoked,
                    added_at: 1,
                },
            ],
        };

        assert!(account.is_active_owner_of(&repo));
        assert!(!account.is_active_owner_of(&other));
        assert!(account.has_any_active_grant());
        assert!(account.find_grant(&other).is_some());
        assert!(!account.find_grant(&other).unwrap().is_active());
    }

    /// 全権限が失効したアカウントは「有効な権限なし」になること。
    #[test]
    fn fully_revoked_account_has_no_active_grant() {
        let account = Account {
            name: "gone".to_string(),
            public_key: "pk".to_string(),
            created_at: 1,
            grants: vec![Grant {
                repository_id: "c".repeat(REPOSITORY_ID_HEX_CHARS),
                project_name: "Proj".to_string(),
                role: Role::Member,
                status: GrantStatus::Revoked,
                added_at: 1,
            }],
        };
        assert!(!account.has_any_active_grant());
    }

    /// アカウントの検索が大文字小文字を無視すること。
    #[test]
    fn account_lookup_is_case_insensitive() {
        let mut doc = AccountsDocument::default();
        doc.accounts.push(Account {
            name: "Tsubasa".to_string(),
            public_key: "pk".to_string(),
            grants: Vec::new(),
            created_at: 1,
        });
        assert!(doc.find_account("tsubasa").is_some());
        assert!(doc.find_account("TSUBASA").is_some());
        assert!(doc.find_account("tsubasa2").is_none());
    }

    /// 招待コードの使用可否判定（期限・使い捨て）。
    #[test]
    fn invite_usability() {
        let base = Invite {
            code_hash: "h".to_string(),
            repository_id: "d".repeat(REPOSITORY_ID_HEX_CHARS),
            project_name: "Proj".to_string(),
            role: Role::Member,
            created_by: "owner1".to_string(),
            created_at: 1_000,
            expires_at: 2_000,
            used_at: None,
            used_by: None,
        };

        assert!(base.is_usable_at(1_500), "期限内なら使える");
        assert!(!base.is_usable_at(2_000), "期限ちょうどは使えない");
        assert!(!base.is_usable_at(2_500), "期限切れは使えない");

        let mut used = base.clone();
        used.used_at = Some(1_200);
        used.used_by = Some("member1".to_string());
        assert!(!used.is_usable_at(1_500), "使用済みは使えない");
    }

    /// 役割・状態の文字列表現が契約どおりであること
    /// （JWT の permission と API 応答に直接出るので固定する）。
    #[test]
    fn role_and_status_strings_are_stable() {
        assert_eq!(Role::Owner.as_str(), "owner");
        assert_eq!(Role::Member.as_str(), "member");
        assert_eq!(GrantStatus::Active.as_str(), "active");
        assert_eq!(GrantStatus::Revoked.as_str(), "revoked");

        // serde 経由でも同じ表現になること
        assert_eq!(serde_json::to_string(&Role::Owner).unwrap(), "\"owner\"");
        assert_eq!(
            serde_json::to_string(&GrantStatus::Revoked).unwrap(),
            "\"revoked\""
        );
    }
}
