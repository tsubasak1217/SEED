// =============================================================================
// SEED アカウント発行窓口 : JWT の組み立てと検証
// =============================================================================
// **トークンの構造を触るのはこのファイルだけ。**
// Lore v0.10 でクレームの構造が変わる見込みがあるため、
// 変更点がここ 1 ファイルに収まるようにしておく（契約 4 章の指示）。
//
// Lore v0.9.0 が要求する形（契約 4 章）:
//   {
//     "sub": "<名前>", "name": "<名前>", "preferred_username": "<名前>",
//     "iss": "<設定の issuer>", "aud": ["<設定の audience>"],
//     "iat": 0, "exp": 0, "env": "seed", "idp": "seed-auth",
//     "resources": [ { "resource_id": "urc-<repository_id>", "permission": ["owner"] } ]
//   }
//
// 入れてはいけないもの:
//   - `is_service_account` … ブランチ保護を素通りしてしまう
//   - `migrate`            … 移行用の特別扱い
//   - `urc-*`（ワイルドカード）… ロックの強制解放に効かない
//
// ヘッダには `kid` が必須（JWKS から鍵を引くため）。
// =============================================================================

use jsonwebtoken::Algorithm;
use jsonwebtoken::Header;
use jsonwebtoken::Validation;
use serde::Deserialize;
use serde::Serialize;

use super::issuer::IssuerKey;
use super::model::Account;
use super::model::Role;
use super::model::validate_repository_id;

// -----------------------------------------------------------------------------
// 定数
// -----------------------------------------------------------------------------

/// `resources[].resource_id` の接頭辞。Lore のリソース識別子の書式。
const RESOURCE_ID_PREFIX: &str = "urc-";

/// クレーム `env` の値。Lore は値そのものを見ないが、フィールドは必須。
const CLAIM_ENV: &str = "seed";

/// クレーム `idp`（identity provider）の値。発行元がこの窓口であることを表す。
const CLAIM_IDP: &str = "seed-auth";

/// 1 秒あたりのミリ秒数。
const MILLIS_PER_SECOND: u64 = 1_000;

/// 1 時間あたりの秒数。
const SECONDS_PER_HOUR: u64 = 3_600;

/// `Authorization` ヘッダの認証方式（後ろに空白 1 つ）。
pub const BEARER_PREFIX: &str = "Bearer ";

/// 受け付けるアクセストークンの最大文字数。
/// 実際のトークンは 600 文字前後。極端に長い入力で CPU を使わせないための上限。
pub const ACCESS_TOKEN_MAX_CHARS: usize = 8_192;

// -----------------------------------------------------------------------------
// クレーム
// -----------------------------------------------------------------------------

/// `resources` 配列の 1 要素。
#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ResourceClaim {
    /// `urc-<repository_id>`
    pub resource_id: String,
    /// 権限の配列。owner なら `["owner"]`、member なら `["member"]`
    pub permission: Vec<String>,
}

/// JWT のペイロード。
///
/// フィールドの並びと名前は Lore v0.9.0 の要求そのもの。
/// 勝手に増やさない・減らさないこと（契約 4 章）。
#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct TokenClaims {
    /// 主体。Lore はこれを user id として使い、ロックの所有者表示になる
    pub sub: String,
    /// 表示名（`sub` と同じ）
    pub name: String,
    /// 別名（`sub` と同じ）
    pub preferred_username: String,
    /// 発行者。設定の `issuer`
    pub iss: String,
    /// 対象。設定の `audience` を 1 要素だけ持つ配列
    pub aud: Vec<String>,
    /// 発行時刻（UNIX epoch 秒）
    pub iat: u64,
    /// 失効時刻（UNIX epoch 秒）
    pub exp: u64,
    /// 環境名
    pub env: String,
    /// identity provider
    pub idp: String,
    /// リポジトリごとの権限
    pub resources: Vec<ResourceClaim>,
}

impl TokenClaims {
    /// 指定リポジトリに対する権限が載っているか（役割は問わない）。
    ///
    /// 現在の HTTP ハンドラはすべて `has_role` で owner を要求するため
    /// 通常ビルドからは呼ばれないが、member 向けの操作を足すときに要る。
    #[allow(dead_code)]
    pub fn has_resource(&self, repository_id: &str) -> bool {
        let wanted = resource_id_for(repository_id);
        self.resources.iter().any(|r| r.resource_id == wanted)
    }

    /// 指定リポジトリに対して、指定の役割を持っているか。
    pub fn has_role(&self, repository_id: &str, role: Role) -> bool {
        let wanted = resource_id_for(repository_id);
        self.resources
            .iter()
            .filter(|r| r.resource_id == wanted)
            .any(|r| r.permission.iter().any(|p| p == role.as_str()))
    }
}

/// リポジトリ ID から Lore のリソース識別子を作る。
///
/// **ワイルドカード（`urc-*`）は絶対に作らない。**
/// Lore v0.9.0 ではワイルドカードがロックの強制解放に効かないため、
/// 「権限があるのに解放できない」という分かりにくい失敗になる。
pub fn resource_id_for(repository_id: &str) -> String {
    format!("{RESOURCE_ID_PREFIX}{repository_id}")
}

/// Lore のリソース識別子からリポジトリ ID を取り出す（`resource_id_for` の逆）。
///
/// Lore 本体が `auth_url` の gRPC へ送ってくる `resource_id` は
/// `urc-<32桁hex>` の形（`lore-server/src/authnz/repository_authorizer.rs` の
/// `format!("urc-{repository_id}")`）。
///
/// 接頭辞が付いていない値、書式が合わない値は `None` を返す。
/// **ワイルドカード（`urc-*`）も `None`** になる
/// （32 桁 16 進の検証を通らないため。権限を広げる抜け道を作らない）。
pub fn repository_id_from_resource_id(resource_id: &str) -> Option<&str> {
    let repository_id = resource_id.strip_prefix(RESOURCE_ID_PREFIX)?;
    if validate_repository_id(repository_id) {
        Some(repository_id)
    } else {
        None
    }
}

// -----------------------------------------------------------------------------
// 発行
// -----------------------------------------------------------------------------

/// 発行したトークンとその失効時刻。
#[derive(Clone, Debug)]
pub struct IssuedToken {
    /// JWT 本体
    pub access_token: String,
    /// 失効時刻（UNIX epoch **ミリ秒**。契約 3 章の `expires_at` はミリ秒）
    pub expires_at_ms: u64,
}

/// アカウントの有効な権限から JWT を組み立てて署名する。
///
/// # 引数
/// - `key`: 窓口の署名鍵
/// - `issuer` / `audience`: 設定値。Lore 側の `[server.auth]` と一致させること
/// - `token_ttl_hours`: 有効期間（時間）
/// - `account`: 対象のアカウント
/// - `now_ms`: 現在時刻（UNIX epoch ミリ秒）
///
/// 失効した権限（`GrantStatus::Revoked`）は `resources` に載せない。
///
/// # Errors
/// 署名に失敗した場合（鍵が壊れているなど）。
pub fn issue_token(
    key: &IssuerKey,
    issuer: &str,
    audience: &str,
    token_ttl_hours: u64,
    account: &Account,
    now_ms: u64,
) -> Result<IssuedToken, String> {
    let issued_at_seconds = now_ms / MILLIS_PER_SECOND;
    let expires_at_seconds = issued_at_seconds + token_ttl_hours * SECONDS_PER_HOUR;

    // 有効な権限だけを resources へ写す。
    let resources = account
        .grants
        .iter()
        .filter(|grant| grant.is_active())
        .map(|grant| ResourceClaim {
            resource_id: resource_id_for(&grant.repository_id),
            permission: vec![grant.role.as_str().to_string()],
        })
        .collect();

    let claims = TokenClaims {
        sub: account.name.clone(),
        name: account.name.clone(),
        preferred_username: account.name.clone(),
        iss: issuer.to_string(),
        aud: vec![audience.to_string()],
        iat: issued_at_seconds,
        exp: expires_at_seconds,
        env: CLAIM_ENV.to_string(),
        idp: CLAIM_IDP.to_string(),
        resources,
    };

    // ヘッダには alg と kid を入れる（kid が無いと JWKS から鍵を引けない）。
    let mut header = Header::new(key.algorithm().to_jsonwebtoken());
    header.kid = Some(key.kid().to_string());

    let access_token = jsonwebtoken::encode(&header, &claims, key.encoding_key())
        .map_err(|e| format!("トークンの署名に失敗しました: {e}"))?;

    Ok(IssuedToken {
        access_token,
        expires_at_ms: expires_at_seconds * MILLIS_PER_SECOND,
    })
}

// -----------------------------------------------------------------------------
// 検証
// -----------------------------------------------------------------------------

/// Bearer トークンの検証に失敗した理由。
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TokenError {
    /// `Authorization` ヘッダが無い、または `Bearer ` で始まっていない
    Missing,
    /// 長さが上限を超えている
    TooLong,
    /// 署名・発行者・対象・期限のいずれかが不正
    Invalid,
}

impl TokenError {
    /// 利用者へ返す日本語の説明。
    ///
    /// 「期限切れ」と「署名が違う」を区別して返さない。
    /// トークンの内部状態を攻撃者に教えないため。
    pub fn message(self) -> &'static str {
        match self {
            TokenError::Missing => "Authorization ヘッダ（Bearer トークン）が必要です",
            TokenError::TooLong => "アクセストークンが長すぎます",
            TokenError::Invalid => "アクセストークンが無効か期限切れです",
        }
    }
}

/// `Authorization: Bearer <token>` の値からトークン本体を取り出す。
pub fn extract_bearer(authorization_header: Option<&str>) -> Result<&str, TokenError> {
    let raw = authorization_header.ok_or(TokenError::Missing)?;
    let token = raw.strip_prefix(BEARER_PREFIX).ok_or(TokenError::Missing)?;
    if token.len() > ACCESS_TOKEN_MAX_CHARS {
        return Err(TokenError::TooLong);
    }
    Ok(token)
}

/// 窓口が発行した JWT を検証してクレームを返す。
///
/// 署名・`iss`・`aud`・`exp` をすべて検証する。
/// 時刻は jsonwebtoken が OS の時計を見る（引数で渡せないため、
/// 「期限切れが弾かれる」テストは過去の `exp` を持つトークンを作って確認する）。
pub fn verify_token(
    key: &IssuerKey,
    issuer: &str,
    audience: &str,
    token: &str,
) -> Result<TokenClaims, TokenError> {
    if token.len() > ACCESS_TOKEN_MAX_CHARS {
        return Err(TokenError::TooLong);
    }

    let algorithm: Algorithm = key.algorithm().to_jsonwebtoken();
    let mut validation = Validation::new(algorithm);
    validation.set_issuer(&[issuer]);
    validation.set_audience(&[audience]);
    // exp は既定で検証されるが、意図を残すため明示する。
    validation.validate_exp = true;

    jsonwebtoken::decode::<TokenClaims>(token, key.decoding_key(), &validation)
        .map(|data| data.claims)
        .map_err(|_| TokenError::Invalid)
}

// =============================================================================
// 単体テスト
// =============================================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::auth::model::Grant;
    use crate::auth::model::GrantStatus;

    /// テスト用の設定値。
    const TEST_ISSUER: &str = "seed-auth";
    const TEST_AUDIENCE: &str = "seed-lore";
    const TEST_TTL_HOURS: u64 = 8;
    const REPOSITORY_A: &str = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const REPOSITORY_B: &str = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    /// OS の現在時刻（UNIX epoch ミリ秒）。
    /// jsonwebtoken の期限検証は OS の時計を見るので、
    /// 期限まわりのテストは実時刻を基準に組み立てる。
    fn real_now_ms() -> u64 {
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_millis() as u64
    }

    /// owner 1 件 + 失効した member 1 件を持つアカウントを作る。
    fn test_account() -> Account {
        Account {
            name: "tsubasa".to_string(),
            public_key: "dummy".to_string(),
            created_at: 1,
            grants: vec![
                Grant {
                    repository_id: REPOSITORY_A.to_string(),
                    project_name: "WarashibeFishing".to_string(),
                    role: Role::Owner,
                    status: GrantStatus::Active,
                    added_at: 1,
                },
                Grant {
                    repository_id: REPOSITORY_B.to_string(),
                    project_name: "Revoked".to_string(),
                    role: Role::Member,
                    status: GrantStatus::Revoked,
                    added_at: 1,
                },
            ],
        }
    }

    /// テスト用の署名鍵（一時ディレクトリに作る）。
    fn test_key(dir: &tempfile::TempDir) -> IssuerKey {
        IssuerKey::load_or_create(dir.path(), real_now_ms()).unwrap()
    }

    /// 契約 4 章のクレームがすべて入り、余計なものが入らないこと。
    #[test]
    fn claims_match_the_contract() {
        let dir = tempfile::tempdir().unwrap();
        let key = test_key(&dir);
        let now = real_now_ms();

        let issued = issue_token(
            &key,
            TEST_ISSUER,
            TEST_AUDIENCE,
            TEST_TTL_HOURS,
            &test_account(),
            now,
        )
        .unwrap();

        // 署名を検証せずにペイロードだけを取り出して、生の JSON を確認する
        let payload_b64 = issued.access_token.split('.').nth(1).unwrap();
        let payload = super::super::crypto::base64url_decode(payload_b64).unwrap();
        let payload: serde_json::Value = serde_json::from_slice(&payload).unwrap();

        assert_eq!(payload["sub"], "tsubasa");
        assert_eq!(payload["name"], "tsubasa");
        assert_eq!(payload["preferred_username"], "tsubasa");
        assert_eq!(payload["iss"], TEST_ISSUER);
        assert_eq!(payload["aud"], serde_json::json!([TEST_AUDIENCE]));
        assert_eq!(payload["env"], CLAIM_ENV);
        assert_eq!(payload["idp"], CLAIM_IDP);
        assert!(payload["iat"].is_u64());
        assert!(payload["exp"].is_u64());

        // 失効した権限は載らない。owner のエントリだけが残る。
        assert_eq!(payload["resources"], serde_json::json!([{
            "resource_id": format!("{RESOURCE_ID_PREFIX}{REPOSITORY_A}"),
            "permission": ["owner"],
        }]));

        // 入れてはいけないクレームが無いこと
        assert!(payload.get("is_service_account").is_none());
        assert!(payload.get("migrate").is_none());

        // ワイルドカードを作っていないこと
        assert!(!issued.access_token.contains("urc-*"));
    }

    /// ヘッダに `kid` と正しい `alg` が入ること。
    #[test]
    fn header_contains_kid_and_alg() {
        let dir = tempfile::tempdir().unwrap();
        let key = test_key(&dir);

        let issued = issue_token(
            &key,
            TEST_ISSUER,
            TEST_AUDIENCE,
            TEST_TTL_HOURS,
            &test_account(),
            real_now_ms(),
        )
        .unwrap();

        let header_b64 = issued.access_token.split('.').next().unwrap();
        let header = super::super::crypto::base64url_decode(header_b64).unwrap();
        let header: serde_json::Value = serde_json::from_slice(&header).unwrap();

        assert_eq!(header["kid"].as_str().unwrap(), key.kid());
        assert_eq!(
            header["alg"].as_str().unwrap(),
            key.algorithm().as_str(),
            "JWKS の alg と JWT ヘッダの alg が食い違うと鍵を引けない"
        );
    }

    /// 発行したトークンを自分で検証できること。
    #[test]
    fn issued_token_verifies() {
        let dir = tempfile::tempdir().unwrap();
        let key = test_key(&dir);

        let issued = issue_token(
            &key,
            TEST_ISSUER,
            TEST_AUDIENCE,
            TEST_TTL_HOURS,
            &test_account(),
            real_now_ms(),
        )
        .unwrap();

        let claims = verify_token(&key, TEST_ISSUER, TEST_AUDIENCE, &issued.access_token).unwrap();
        assert_eq!(claims.sub, "tsubasa");
        assert!(claims.has_role(REPOSITORY_A, Role::Owner));
        assert!(!claims.has_role(REPOSITORY_A, Role::Member));
        assert!(!claims.has_resource(REPOSITORY_B), "失効した権限が載っている");
    }

    /// 期限切れのトークンは弾かれること。
    #[test]
    fn expired_token_is_rejected() {
        let dir = tempfile::tempdir().unwrap();
        let key = test_key(&dir);

        // TTL の 2 倍だけ過去に発行したことにすると、確実に期限切れになる
        let long_ago =
            real_now_ms() - TEST_TTL_HOURS * 2 * SECONDS_PER_HOUR * MILLIS_PER_SECOND;
        let issued = issue_token(
            &key,
            TEST_ISSUER,
            TEST_AUDIENCE,
            TEST_TTL_HOURS,
            &test_account(),
            long_ago,
        )
        .unwrap();

        assert_eq!(
            verify_token(&key, TEST_ISSUER, TEST_AUDIENCE, &issued.access_token).unwrap_err(),
            TokenError::Invalid
        );
    }

    /// issuer / audience が違うトークンは弾かれること。
    #[test]
    fn wrong_issuer_or_audience_is_rejected() {
        let dir = tempfile::tempdir().unwrap();
        let key = test_key(&dir);

        let issued = issue_token(
            &key,
            TEST_ISSUER,
            TEST_AUDIENCE,
            TEST_TTL_HOURS,
            &test_account(),
            real_now_ms(),
        )
        .unwrap();

        assert!(verify_token(&key, "other-issuer", TEST_AUDIENCE, &issued.access_token).is_err());
        assert!(verify_token(&key, TEST_ISSUER, "other-audience", &issued.access_token).is_err());
    }

    /// 別の鍵で署名されたトークンは弾かれること。
    #[test]
    fn token_signed_by_another_key_is_rejected() {
        let dir_a = tempfile::tempdir().unwrap();
        let dir_b = tempfile::tempdir().unwrap();
        let key_a = test_key(&dir_a);
        let key_b = test_key(&dir_b);

        let issued = issue_token(
            &key_a,
            TEST_ISSUER,
            TEST_AUDIENCE,
            TEST_TTL_HOURS,
            &test_account(),
            real_now_ms(),
        )
        .unwrap();

        assert!(verify_token(&key_b, TEST_ISSUER, TEST_AUDIENCE, &issued.access_token).is_err());
    }

    /// 壊れた文字列でも panic しないこと。
    #[test]
    fn malformed_tokens_do_not_panic() {
        let dir = tempfile::tempdir().unwrap();
        let key = test_key(&dir);

        for bad in ["", "a.b.c", "not-a-token", "....."] {
            assert!(verify_token(&key, TEST_ISSUER, TEST_AUDIENCE, bad).is_err());
        }

        let too_long = "a".repeat(ACCESS_TOKEN_MAX_CHARS + 1);
        assert_eq!(
            verify_token(&key, TEST_ISSUER, TEST_AUDIENCE, &too_long).unwrap_err(),
            TokenError::TooLong
        );
    }

    /// Authorization ヘッダの取り出し。
    #[test]
    fn bearer_extraction() {
        assert_eq!(extract_bearer(Some("Bearer abc")).unwrap(), "abc");
        assert_eq!(extract_bearer(None).unwrap_err(), TokenError::Missing);
        assert_eq!(extract_bearer(Some("abc")).unwrap_err(), TokenError::Missing);
        // 方式名は大文字小文字を区別する（HTTP の仕様上は区別しないが、
        // エディタ側の実装を 1 つに固定したいので厳しくしておく）
        assert_eq!(
            extract_bearer(Some("bearer abc")).unwrap_err(),
            TokenError::Missing
        );

        let long = format!("{BEARER_PREFIX}{}", "a".repeat(ACCESS_TOKEN_MAX_CHARS + 1));
        assert_eq!(extract_bearer(Some(&long)).unwrap_err(), TokenError::TooLong);
    }

    /// `expires_at_ms` がミリ秒で返ること（契約 3 章）。
    #[test]
    fn expires_at_is_in_milliseconds() {
        let dir = tempfile::tempdir().unwrap();
        let key = test_key(&dir);
        let now = real_now_ms();

        let issued = issue_token(
            &key,
            TEST_ISSUER,
            TEST_AUDIENCE,
            TEST_TTL_HOURS,
            &test_account(),
            now,
        )
        .unwrap();

        let expected = (now / MILLIS_PER_SECOND + TEST_TTL_HOURS * SECONDS_PER_HOUR)
            * MILLIS_PER_SECOND;
        assert_eq!(issued.expires_at_ms, expected);
    }

    /// リソース識別子の組み立てが契約どおりであること。
    #[test]
    fn resource_id_format_is_fixed() {
        assert_eq!(resource_id_for(REPOSITORY_A), format!("urc-{REPOSITORY_A}"));
    }
}
