// =============================================================================
// 権限サービス : 判定そのもの（gRPC から切り離した中核）
// =============================================================================
// Lore から届く問い合わせを「誰が・どのリポジトリを・何をしてよいか」に
// 翻訳して答えるのはこのファイルだけ。gRPC の型や tonic には一切触れない
// （urc_auth.rs / rebac.rs が翻訳を担当する）。
// こう分けてあるので、判定は gRPC サーバを起動せずに単体テストできる。
//
// 【判定の根拠は台帳（accounts.json）】
// 発行済みのトークンに載っている `resources` は**見ない**。
// 見るのは毎回 `AccountStore` の現在の中身。こうすることで、
// **権限の失効がトークンの期限を待たずに即座に効く**
// （トークンの `resources` を信じると、失効しても最大 token_ttl_hours の間
//   アクセスできてしまう）。トークンから使うのは「誰か」（`sub`）だけ。
//
// 【失敗は必ず拒否側へ倒す】
// 判断がつかない場合（トークンが読めない・リソース ID の書式が違う・
// 台帳の保存に失敗した）は、すべて拒否として扱う。
// =============================================================================

use std::sync::Arc;

use crate::auth::http::AuthState;
use crate::auth::model::PROJECT_NAME_MAX_CHARS;
use crate::auth::model::Role;
use crate::auth::store::StoreError;
use crate::auth::token::TokenError;
use crate::auth::token::extract_bearer;
use crate::auth::token::repository_id_from_resource_id;
use crate::auth::token::resource_id_for;
use crate::auth::token::verify_token;

// -----------------------------------------------------------------------------
// 判定の失敗
// -----------------------------------------------------------------------------

/// 権限判定が通らなかった理由。
///
/// gRPC のステータスへの写像は呼び出し側（`urc_auth.rs` / `rebac.rs`）が行う。
/// ここでは「何が起きたか」だけを表す。
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum DecisionError {
    /// `authorization` が無い／署名・発行者・対象・期限のいずれかが不正
    Unauthenticated,
    /// トークンは正しいが、その操作をしてよい権限が台帳に無い
    Denied,
    /// サーバ側の失敗（台帳の保存に失敗したなど）。**利用者には詳細を返さない**
    Internal(String),
}

// -----------------------------------------------------------------------------
// 1 リソース分の判定結果
// -----------------------------------------------------------------------------

/// `CheckUserPermission` に答えるための 1 件分の判定。
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ResourceDecision {
    /// 問い合わせで渡された `resource_id`（そのまま返す）
    pub resource_id: String,
    /// 許可された役割。`None` なら拒否
    pub role: Option<Role>,
}

impl ResourceDecision {
    /// 許可されているか。
    ///
    /// 通常ビルドの呼び出し側（`urc_auth.rs`）は役割そのものが要るので
    /// `role` を直接見ており、この取得子は単体テストからしか呼ばれない。
    /// 「許可されたか」を読みやすく書けるので残す。
    #[allow(dead_code)]
    pub fn is_allowed(&self) -> bool {
        self.role.is_some()
    }
}

// -----------------------------------------------------------------------------
// 判定器
// -----------------------------------------------------------------------------

/// 台帳と設定を見て権限を判定する。
///
/// 発行窓口（HTTP）と同じ `AuthState` を共有する。
/// 同じ `AccountStore` の実体を見るので、招待・失効が即座に判定へ反映される。
pub struct PermissionAuthority {
    /// 設定・台帳・署名鍵をまとめた共有状態（発行窓口と同一の実体）
    state: Arc<AuthState>,
}

impl PermissionAuthority {
    /// 共有状態から判定器を作る。
    pub fn new(state: Arc<AuthState>) -> Self {
        Self { state }
    }

    /// `authorization` メタデータの値から呼び出し元のアカウント名を取り出す。
    ///
    /// 期待する形は `Bearer <この窓口が発行した JWT>`。
    /// Lore 本体は受け取った `authorization` ヘッダをそのまま転送してくる
    /// （`lore-server/src/authnz/common.rs` の `create_request_with_authorization`）。
    ///
    /// # Errors
    /// ヘッダが無い・形式が違う・署名／発行者／対象／期限が不正なら
    /// `Unauthenticated`（理由は区別しない。トークンの内部状態を教えないため）。
    pub fn authenticate(&self, authorization: Option<&str>) -> Result<String, DecisionError> {
        let token = extract_bearer(authorization).map_err(Self::token_error_to_decision)?;

        let claims = verify_token(
            &self.state.issuer_key,
            &self.state.config.issuer,
            &self.state.config.audience,
            token,
        )
        .map_err(Self::token_error_to_decision)?;

        // `sub` はアカウント名（契約 4 章）。トークンの `resources` は使わない。
        Ok(claims.sub)
    }

    /// 問い合わせられたリソースそれぞれについて、許可／拒否を決める。
    ///
    /// 判定は「そのアカウントがそのリポジトリに**有効な**権限を持っているか」だけ。
    /// 役割（owner / member）は許可の可否には関係しない
    /// （Lore 側はロックの強制解放など役割の扱いを JWT の `resources` で行うため、
    ///   ここで役割を絞ると二重に効いてしまう）。
    pub fn decide_resources(&self, caller: &str, resource_ids: &[String]) -> Vec<ResourceDecision> {
        resource_ids
            .iter()
            .map(|resource_id| ResourceDecision {
                resource_id: resource_id.clone(),
                role: repository_id_from_resource_id(resource_id)
                    .and_then(|repository_id| self.state.store.active_role(caller, repository_id)),
            })
            .collect()
    }

    /// そのアカウントが触れるリソースを全部返す（`LookupUserPermissions` 用）。
    ///
    /// 戻り値は `(resource_id, 役割)`。`resource_id` は `urc-<リポジトリID>`。
    pub fn list_resources(&self, caller: &str) -> Vec<(String, Role)> {
        self.state
            .store
            .active_grants(caller)
            .into_iter()
            .map(|(repository_id, role)| (resource_id_for(&repository_id), role))
            .collect()
    }

    /// 新しいリポジトリを作ってよいかを判定し、よければ作成者を owner として登録する。
    ///
    /// # 判定
    /// 1. 設定 `[seed_auth] repository_creators` に名前が載っていること。
    ///    **既定は空 ＝ 認証が有効な間は誰も作れない。**
    /// 2. リソース ID が `urc-<32桁hex>` の形であること。
    /// 3. そのリポジトリに別人の有効な owner が居ないこと。
    ///
    /// 3 が通ったら、作成者をそのリポジトリの owner として台帳へ書く。
    /// ここで書いてしまうので、**ループバックからの `POST /v1/bootstrap` は要らない**。
    ///
    /// # 引数
    /// - `caller`: 呼び出し元のアカウント名（`authenticate` の戻り値）
    /// - `resource_id`: `urc-<リポジトリID>`
    /// - `resource_name`: リポジトリ名。表示用のプロジェクト名として台帳へ残す
    /// - `now_ms`: 現在時刻（UNIX epoch ミリ秒）
    ///
    /// # Errors
    /// 権限が無い／書式が不正なら `Denied`、保存に失敗したら `Internal`。
    pub fn authorize_create(
        &self,
        caller: &str,
        resource_id: &str,
        resource_name: &str,
        now_ms: u64,
    ) -> Result<(), DecisionError> {
        if !self.state.config.may_create_repository(caller) {
            return Err(DecisionError::Denied);
        }

        let Some(repository_id) = repository_id_from_resource_id(resource_id) else {
            return Err(DecisionError::Denied);
        };

        // プロジェクト名は**表示用**なので、長すぎても作成そのものは止めない。
        // 権威ある名前は Lore 側のリポジトリメタデータにあり、こちらは
        // 参加者一覧に出すための控え。長さを理由に「permission denied」を
        // 返すと、利用者は設定を疑って延々と直せない。
        let project_name = Self::truncate_to_chars(resource_name, PROJECT_NAME_MAX_CHARS);

        match self
            .state
            .store
            .register_repository_owner(caller, repository_id, project_name, now_ms)
        {
            Ok(_) => Ok(()),
            // 保存の失敗だけはサーバ側の問題なので区別する
            // （拒否として返すと、利用者は設定を疑って延々と直せない）。
            Err(StoreError::PersistFailed(detail)) => Err(DecisionError::Internal(detail)),
            // それ以外（アカウント不在・別人がオーナー・書式不正）は拒否。
            Err(_) => Err(DecisionError::Denied),
        }
    }

    /// リポジトリを削除してよいかを判定する。
    ///
    /// そのリポジトリの**有効な owner** だけが削除できる。
    ///
    /// 台帳は書き換えない。削除はエディタの操作経路に無く、
    /// 残った権限は「そのリポジトリ ID をもう一度作れる人」を表すだけで
    /// 害が無いため（誤って権限を落とすほうが復旧しにくい）。
    ///
    /// # Errors
    /// owner でなければ `Denied`。
    pub fn authorize_delete(&self, caller: &str, resource_id: &str) -> Result<(), DecisionError> {
        let Some(repository_id) = repository_id_from_resource_id(resource_id) else {
            return Err(DecisionError::Denied);
        };

        match self.state.store.active_role(caller, repository_id) {
            Some(Role::Owner) => Ok(()),
            _ => Err(DecisionError::Denied),
        }
    }

    /// 文字列を先頭から指定の**文字数**（コードポイント数）で切り詰める。
    ///
    /// バイト数ではなく文字数で数えるのは、台帳側の検証（`PROJECT_NAME_MAX_CHARS`）が
    /// 文字数で数えているのと、日本語の途中で切ってバイト列を壊さないため。
    fn truncate_to_chars(text: &str, max_chars: usize) -> &str {
        match text.char_indices().nth(max_chars) {
            Some((byte_index, _)) => &text[..byte_index],
            None => text,
        }
    }

    /// トークンの失敗理由を判定の失敗へ写す。
    ///
    /// **どの失敗も `Unauthenticated` に畳む。**
    /// 「期限切れ」と「署名が違う」を区別して返すと、
    /// 認証できていない相手にトークンの内部状態を教えることになる。
    fn token_error_to_decision(_error: TokenError) -> DecisionError {
        DecisionError::Unauthenticated
    }
}

// =============================================================================
// 単体テスト
// =============================================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::auth::challenge::ChallengeTable;
    use crate::auth::config::SeedAuthConfig;
    use crate::auth::issuer::IssuerKey;
    use crate::auth::store::AccountStore;
    use crate::auth::token::issue_token;

    /// テスト用の設定値。
    const TEST_ISSUER: &str = "seed-auth";
    const TEST_AUDIENCE: &str = "seed-lore";
    const TEST_TTL_HOURS: u64 = 8;

    /// テストで使うリポジトリ ID（32 桁の 16 進小文字）。
    const REPOSITORY_A: &str = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const REPOSITORY_B: &str = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    /// テストで使うアカウント名。
    const OWNER: &str = "alice";
    const MEMBER: &str = "bob";
    const OUTSIDER: &str = "mallory";

    /// テストで使うプロジェクト名。
    const PROJECT: &str = "SeedPermission";

    /// .NET の `ECDsa` が出す形（SEC1 非圧縮点の base64url）を模した公開鍵。
    /// ここでは署名検証を通さないので、形式だけ正しければよい。
    fn public_key_for(seed: u8) -> String {
        let mut bytes = [0_u8; 65];
        bytes[0] = 0x04;
        for (index, byte) in bytes.iter_mut().enumerate().skip(1) {
            *byte = seed.wrapping_add(index as u8);
        }
        crate::auth::crypto::base64url_encode(&bytes)
    }

    /// テスト用の共有状態を組み立てる。
    ///
    /// 戻り値の `TempDir` は保持し続けること（落とすとフォルダごと消える）。
    fn new_state(repository_creators: Vec<String>) -> (Arc<AuthState>, tempfile::TempDir) {
        let dir = tempfile::tempdir().unwrap();

        let config = SeedAuthConfig {
            host: "127.0.0.1".to_string(),
            port: 41350,
            permission_port: 41352,
            repository_creators,
            data_dir: dir.path().to_path_buf(),
            issuer: TEST_ISSUER.to_string(),
            audience: TEST_AUDIENCE.to_string(),
            token_ttl_hours: TEST_TTL_HOURS,
            // 判定には関係しない（起動時の助言だけに使う値）。
            configured_auth_url: None,
        };

        let issuer_key = IssuerKey::load_or_create(dir.path(), 1).unwrap();
        let jwks_body = issuer_key.jwks_json().unwrap();
        let store = AccountStore::open(dir.path()).unwrap();

        let state = Arc::new(AuthState {
            config,
            store,
            issuer_key,
            challenges: ChallengeTable::new(),
            jwks_body,
        });
        (state, dir)
    }

    /// 現在時刻（UNIX epoch ミリ秒）。
    /// トークンの期限検証は OS の時計を見るので、実時刻で組み立てる。
    fn real_now_ms() -> u64 {
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_millis() as u64
    }

    /// そのアカウントの現在の権限でトークンを発行し、`Bearer ...` の形で返す。
    fn bearer_for(state: &AuthState, name: &str) -> String {
        let account = state.store.find_account(name).expect("アカウントが無い");
        let issued = issue_token(
            &state.issuer_key,
            &state.config.issuer,
            &state.config.audience,
            state.config.token_ttl_hours,
            &account,
            real_now_ms(),
        )
        .unwrap();
        format!("Bearer {}", issued.access_token)
    }

    /// owner 1 人 + member 1 人が居る台帳を作る。
    fn state_with_members(
        repository_creators: Vec<String>,
    ) -> (Arc<AuthState>, tempfile::TempDir) {
        let (state, dir) = new_state(repository_creators);
        state
            .store
            .bootstrap_owner(OWNER, &public_key_for(1), REPOSITORY_A, PROJECT, 1)
            .unwrap();
        // member は招待経由でしか入れないので、bootstrap した別リポジトリ経由ではなく
        // 招待を作って参加させる（実際の経路と同じにする）。
        let invite = state
            .store
            .create_invite(OWNER, REPOSITORY_A, Role::Member, 1, 1)
            .unwrap();
        state
            .store
            .join_with_invite(&invite.invite_code, MEMBER, &public_key_for(2), 1)
            .unwrap();
        (state, dir)
    }

    // -------------------------------------------------------------------------
    // 認証
    // -------------------------------------------------------------------------

    /// 発行したトークンから名前が取れること。
    #[test]
    fn authenticate_returns_the_account_name() {
        let (state, _dir) = state_with_members(vec![]);
        let bearer = bearer_for(&state, OWNER);
        let authority = PermissionAuthority::new(state);

        assert_eq!(authority.authenticate(Some(&bearer)).unwrap(), OWNER);
    }

    /// ヘッダが無い・方式が違う・中身が壊れている場合は、
    /// すべて同じ `Unauthenticated` になること（理由を漏らさない）。
    #[test]
    fn authenticate_rejects_anything_that_is_not_a_valid_bearer() {
        let (state, _dir) = state_with_members(vec![]);
        let bearer = bearer_for(&state, OWNER);
        let raw = bearer.trim_start_matches("Bearer ").to_string();
        let authority = PermissionAuthority::new(state);

        assert_eq!(
            authority.authenticate(None).unwrap_err(),
            DecisionError::Unauthenticated
        );
        // 方式名が無い（生の JWT だけ）
        assert_eq!(
            authority.authenticate(Some(&raw)).unwrap_err(),
            DecisionError::Unauthenticated
        );
        // 署名が壊れている
        assert_eq!(
            authority
                .authenticate(Some(&format!("Bearer {raw}x")))
                .unwrap_err(),
            DecisionError::Unauthenticated
        );
        // 方式名の大文字小文字違い（契約 3 章: `Bearer ` は区別する）
        assert_eq!(
            authority
                .authenticate(Some(&format!("bearer {raw}")))
                .unwrap_err(),
            DecisionError::Unauthenticated
        );
    }

    // -------------------------------------------------------------------------
    // リソースの判定
    // -------------------------------------------------------------------------

    /// 参加しているリポジトリは許可され、参加していないリポジトリは拒否されること。
    #[test]
    fn resources_are_allowed_only_for_members() {
        let (state, _dir) = state_with_members(vec![]);
        let authority = PermissionAuthority::new(state);

        let requested = vec![resource_id_for(REPOSITORY_A), resource_id_for(REPOSITORY_B)];

        let owner = authority.decide_resources(OWNER, &requested);
        assert_eq!(owner[0].role, Some(Role::Owner), "owner は A を触れる");
        assert_eq!(owner[1].role, None, "参加していない B は触れない");

        let member = authority.decide_resources(MEMBER, &requested);
        assert_eq!(member[0].role, Some(Role::Member), "member も A を触れる");
        assert_eq!(member[1].role, None);

        let outsider = authority.decide_resources(OUTSIDER, &requested);
        assert!(!outsider[0].is_allowed(), "台帳に居ない人は何も触れない");
        assert!(!outsider[1].is_allowed());
    }

    /// **失効が即座に効くこと。**
    /// 発行済みトークンが期限内でも、台帳を見るので次の問い合わせから拒否になる。
    #[test]
    fn revocation_takes_effect_immediately() {
        let (state, _dir) = state_with_members(vec![]);
        let requested = vec![resource_id_for(REPOSITORY_A)];

        // 失効前に発行したトークンを保持しておく（期限はまだ十分先）。
        let bearer = bearer_for(&state, MEMBER);

        state.store.revoke_member(OWNER, REPOSITORY_A, MEMBER).unwrap();

        let authority = PermissionAuthority::new(state);
        // トークンそのものはまだ有効（署名も期限も通る）。
        let caller = authority.authenticate(Some(&bearer)).unwrap();
        assert_eq!(caller, MEMBER);
        // それでも台帳を見るので拒否される。
        assert!(!authority.decide_resources(&caller, &requested)[0].is_allowed());
    }

    /// リソース ID の書式が違うものは、すべて拒否になること。
    /// **ワイルドカード（`urc-*`）で権限を広げられないこと**を含む。
    #[test]
    fn malformed_resource_ids_are_denied() {
        let (state, _dir) = state_with_members(vec![]);
        let authority = PermissionAuthority::new(state);

        let requested = vec![
            REPOSITORY_A.to_string(),            // 接頭辞なし
            "urc-*".to_string(),                 // ワイルドカード
            "urc-".to_string(),                  // 空
            format!("urc-{}", &REPOSITORY_A[..8]), // 短い
            format!("URC-{REPOSITORY_A}"),       // 接頭辞の大文字
            format!("urc-{}", REPOSITORY_A.to_uppercase()), // 16 進大文字
        ];

        for decision in authority.decide_resources(OWNER, &requested) {
            assert!(
                !decision.is_allowed(),
                "拒否されるべき resource_id が通った: {}",
                decision.resource_id
            );
        }
    }

    /// 触れるリソースの一覧に、有効な権限だけが出ること。
    #[test]
    fn list_resources_returns_only_active_grants() {
        let (state, _dir) = state_with_members(vec![]);

        let before = PermissionAuthority::new(Arc::clone(&state)).list_resources(MEMBER);
        assert_eq!(before.len(), 1);
        assert_eq!(before[0].0, resource_id_for(REPOSITORY_A));
        assert_eq!(before[0].1, Role::Member);

        state.store.revoke_member(OWNER, REPOSITORY_A, MEMBER).unwrap();

        let after = PermissionAuthority::new(state).list_resources(MEMBER);
        assert!(after.is_empty(), "失効した権限は一覧に出ない");
    }

    // -------------------------------------------------------------------------
    // 新しいリポジトリの作成
    // -------------------------------------------------------------------------

    /// 既定（`repository_creators` が空）では誰も作れないこと。
    #[test]
    fn nobody_can_create_a_repository_by_default() {
        let (state, _dir) = state_with_members(vec![]);
        let authority = PermissionAuthority::new(state);

        assert_eq!(
            authority
                .authorize_create(OWNER, &resource_id_for(REPOSITORY_B), PROJECT, 2)
                .unwrap_err(),
            DecisionError::Denied
        );
    }

    /// 一覧に載っている人は作れて、そのリポジトリの owner になること。
    #[test]
    fn listed_creator_becomes_the_owner() {
        let (state, _dir) = state_with_members(vec![OWNER.to_string()]);
        let authority = PermissionAuthority::new(Arc::clone(&state));

        authority
            .authorize_create(OWNER, &resource_id_for(REPOSITORY_B), PROJECT, 2)
            .unwrap();

        assert_eq!(
            state.store.active_role(OWNER, REPOSITORY_B),
            Some(Role::Owner),
            "作成者が owner として台帳に載る"
        );
        // 作った直後から、そのリポジトリを引けるようになる（clone / push の前提）。
        assert!(authority.decide_resources(OWNER, &[resource_id_for(REPOSITORY_B)])[0].is_allowed());
    }

    /// 一覧に載っていない人は作れないこと（台帳にも何も書かれないこと）。
    #[test]
    fn unlisted_creator_is_denied_and_writes_nothing() {
        let (state, _dir) = state_with_members(vec![OWNER.to_string()]);
        let authority = PermissionAuthority::new(Arc::clone(&state));

        assert_eq!(
            authority
                .authorize_create(MEMBER, &resource_id_for(REPOSITORY_B), PROJECT, 2)
                .unwrap_err(),
            DecisionError::Denied
        );
        assert_eq!(state.store.active_role(MEMBER, REPOSITORY_B), None);
    }

    /// 台帳に居ない名前は、一覧に載っていても作れないこと。
    /// （トークンを持てない名前なので実際には起きないが、順序の取り違えを防ぐ）
    #[test]
    fn unknown_account_cannot_create_even_if_listed() {
        let (state, _dir) = state_with_members(vec![OUTSIDER.to_string()]);
        let authority = PermissionAuthority::new(Arc::clone(&state));

        assert_eq!(
            authority
                .authorize_create(OUTSIDER, &resource_id_for(REPOSITORY_B), PROJECT, 2)
                .unwrap_err(),
            DecisionError::Denied
        );
    }

    /// 別人が既に owner のリポジトリ ID は奪えないこと。
    #[test]
    fn cannot_take_over_a_repository_owned_by_someone_else() {
        let (state, _dir) = state_with_members(vec![MEMBER.to_string()]);
        let authority = PermissionAuthority::new(Arc::clone(&state));

        // A は alice が owner。bob は作成を許可されているが奪えない。
        assert_eq!(
            authority
                .authorize_create(MEMBER, &resource_id_for(REPOSITORY_A), PROJECT, 2)
                .unwrap_err(),
            DecisionError::Denied
        );
        assert_eq!(
            state.store.active_role(OWNER, REPOSITORY_A),
            Some(Role::Owner),
            "元の owner が残っている"
        );
    }

    /// **長すぎるリポジトリ名でも作成そのものは止めないこと。**
    /// プロジェクト名は表示用の控えなので、長さを理由に
    /// 「permission denied」を返すと原因が分からなくなる。
    #[test]
    fn a_very_long_repository_name_does_not_block_creation() {
        let (state, _dir) = state_with_members(vec![OWNER.to_string()]);
        let authority = PermissionAuthority::new(Arc::clone(&state));

        // 日本語（1 文字 3 バイト）で上限を大きく超える名前にする。
        // バイト境界で切ると壊れるので、文字数で切れていることも確かめる。
        let long_name = "あ".repeat(PROJECT_NAME_MAX_CHARS * 2);

        authority
            .authorize_create(OWNER, &resource_id_for(REPOSITORY_B), &long_name, 2)
            .unwrap();

        assert_eq!(
            state.store.active_role(OWNER, REPOSITORY_B),
            Some(Role::Owner)
        );
    }

    /// 文字数での切り詰めが、日本語のバイト列を壊さないこと。
    #[test]
    fn truncation_counts_characters_not_bytes() {
        assert_eq!(PermissionAuthority::truncate_to_chars("あいうえお", 3), "あいう");
        assert_eq!(PermissionAuthority::truncate_to_chars("abc", 10), "abc");
        assert_eq!(PermissionAuthority::truncate_to_chars("", 3), "");
        assert_eq!(PermissionAuthority::truncate_to_chars("あいう", 0), "");
    }

    /// 同じ作成者が同じ ID をもう一度作るのは通ること
    /// （Lore 側は作成をやり直すことがあるので、ここで落とすと復旧できなくなる）。
    #[test]
    fn creating_the_same_repository_twice_is_allowed() {
        let (state, _dir) = state_with_members(vec![OWNER.to_string()]);
        let authority = PermissionAuthority::new(state);

        let resource = resource_id_for(REPOSITORY_B);
        authority.authorize_create(OWNER, &resource, PROJECT, 2).unwrap();
        authority.authorize_create(OWNER, &resource, PROJECT, 3).unwrap();
    }

    // -------------------------------------------------------------------------
    // 削除
    // -------------------------------------------------------------------------

    /// 削除は owner だけが通ること。
    #[test]
    fn only_the_owner_may_delete() {
        let (state, _dir) = state_with_members(vec![]);
        let authority = PermissionAuthority::new(state);

        let resource = resource_id_for(REPOSITORY_A);
        assert!(authority.authorize_delete(OWNER, &resource).is_ok());
        assert_eq!(
            authority.authorize_delete(MEMBER, &resource).unwrap_err(),
            DecisionError::Denied
        );
        assert_eq!(
            authority.authorize_delete(OUTSIDER, &resource).unwrap_err(),
            DecisionError::Denied
        );
        // 参加していないリポジトリも当然だめ。
        assert_eq!(
            authority
                .authorize_delete(OWNER, &resource_id_for(REPOSITORY_B))
                .unwrap_err(),
            DecisionError::Denied
        );
    }
}
