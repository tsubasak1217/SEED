// =============================================================================
// SEED アカウント発行窓口 : HTTP API
// =============================================================================
// 契約 docs/seed_accounts.md 3 章のエンドポイントをそのまま実装する。
// ここはルーティングと入出力の変換だけを持ち、判断は store.rs / token.rs に任せる。
//
// 応答の約束:
//   成功  … 200 + JSON
//   失敗  … HTTP ステータス + {"error":"<コード>","message":"<日本語の説明>"}
//
// 情報を漏らさないための決め事:
//   - `/v1/login/challenge` は未登録・失効の名前でも同じ形の応答を返す。
//     「その名前が存在するか」を外から確かめられないようにするため。
//   - `/v1/login/complete` の失敗は理由を区別せず、すべて 401 `login_failed`。
//   - 招待コードの平文・秘密鍵・トークンはログに出さない。
//     ログに出すのは「操作の種類」と「名前」と「リポジトリ ID」まで。
//
// 入力長の上限:
//   本体全体は `MAX_REQUEST_BODY_BYTES`、個々の文字列はそれぞれの
//   モジュールが持つ上限（名前 32 文字、公開鍵 128 文字 …）で二重に絞る。
// =============================================================================

use std::net::SocketAddr;
use std::sync::Arc;

use axum::Json;
use axum::Router;
use axum::extract::ConnectInfo;
use axum::extract::Query;
use axum::extract::State;
use axum::extract::rejection::JsonRejection;
use axum::extract::rejection::QueryRejection;
use axum::http::HeaderMap;
use axum::http::StatusCode;
use axum::http::header;
use axum::response::IntoResponse;
use axum::response::Response;
use axum::routing::get;
use axum::routing::post;
use serde::Deserialize;
use serde::Serialize;
use tracing::info;
use tracing::warn;

// 現在時刻の取得は発行窓口と権限サービスの両方で使うので、
// `auth/mod.rs` に 1 つだけ置いてある。
use super::now_ms;
use super::challenge::CHALLENGE_ID_MAX_CHARS;
use super::challenge::ChallengeIssueError;
use super::challenge::ChallengeTable;
use super::config::SeedAuthConfig;
use super::issuer::IssuerKey;
use super::model::NAME_MAX_CHARS;
use super::model::PROJECT_NAME_MAX_CHARS;
use super::model::Role;
use super::store::AccountStore;
use super::store::INVITE_CODE_MAX_CHARS;
use super::store::StoreError;
use super::token::TokenClaims;
use super::token::TokenError;
use super::token::extract_bearer;
use super::token::issue_token;
use super::token::verify_token;
use super::user_key::PUBLIC_KEY_MAX_CHARS;
use super::user_key::SIGNATURE_MAX_CHARS;
use super::user_key::login_message;
use super::user_key::verify_login_signature;

// -----------------------------------------------------------------------------
// 定数
// -----------------------------------------------------------------------------

/// API のバージョン番号（`/v1/health` が返す `api`）。
/// エディタ側が「この窓口と話せるか」を判断するための値。
const API_VERSION: u32 = 1;

/// 受け付けるリクエスト本体の最大バイト数。
/// 最大でも公開鍵 1 つ分（数百バイト）しか送られてこないので、
/// 16 KiB もあれば十分に余裕がある。
const MAX_REQUEST_BODY_BYTES: usize = 16 * 1024;

/// 招待の有効期間を省略したときの既定値（時間）。契約 3 章の例に合わせる。
const DEFAULT_INVITE_EXPIRES_IN_HOURS: u64 = 72;

/// ルートのパス（打ち間違いを 1 か所に閉じ込める）。
const ROUTE_HEALTH: &str = "/v1/health";
const ROUTE_BOOTSTRAP: &str = "/v1/bootstrap";
const ROUTE_LOGIN_CHALLENGE: &str = "/v1/login/challenge";
const ROUTE_LOGIN_COMPLETE: &str = "/v1/login/complete";
const ROUTE_INVITES: &str = "/v1/invites";
const ROUTE_JOIN: &str = "/v1/join";
const ROUTE_MEMBERS: &str = "/v1/members";
const ROUTE_MEMBERS_REVOKE: &str = "/v1/members/revoke";
const ROUTE_JWKS: &str = "/jwks.json";

/// エラーコード（契約 3 章）。
const ERROR_OWNER_EXISTS: &str = "owner_exists";
const ERROR_NAME_TAKEN: &str = "name_taken";
const ERROR_INVITE_INVALID: &str = "invite_invalid";
const ERROR_LOGIN_FAILED: &str = "login_failed";
const ERROR_UNAUTHORIZED: &str = "unauthorized";
const ERROR_FORBIDDEN: &str = "forbidden";
const ERROR_NOT_FOUND: &str = "not_found";
const ERROR_INVALID_REQUEST: &str = "invalid_request";
const ERROR_LOOPBACK_ONLY: &str = "loopback_only";
const ERROR_TOO_MANY_REQUESTS: &str = "too_many_requests";
const ERROR_INTERNAL: &str = "internal";

/// `/v1/health` が返す状態文字列。
const HEALTH_STATUS_OK: &str = "ok";

/// JSON の Content-Type。
const CONTENT_TYPE_JSON: &str = "application/json";

// -----------------------------------------------------------------------------
// 共有状態
// -----------------------------------------------------------------------------

/// すべてのハンドラが参照する状態。
pub struct AuthState {
    /// 設定
    pub config: SeedAuthConfig,
    /// 参加者データ
    pub store: AccountStore,
    /// 署名鍵
    pub issuer_key: IssuerKey,
    /// 発行中のチャレンジ
    pub challenges: ChallengeTable,
    /// `GET /jwks.json` でそのまま返す JSON（起動時に 1 回だけ作る）
    pub jwks_body: String,
}

/// axum のハンドラへ渡す共有状態。
type SharedState = Arc<AuthState>;

// -----------------------------------------------------------------------------
// エラー応答
// -----------------------------------------------------------------------------

/// API のエラー。HTTP ステータスとエラーコードと日本語の説明を持つ。
///
/// `Debug` は単体テストの `unwrap()` / `assert_eq!` のために導出する。
/// 中身は利用者へ返す文言だけで、秘密は入らない。
#[derive(Debug)]
pub struct ApiError {
    status: StatusCode,
    code: &'static str,
    message: String,
}

impl ApiError {
    /// エラーを組み立てる。
    fn new(status: StatusCode, code: &'static str, message: impl Into<String>) -> Self {
        Self {
            status,
            code,
            message: message.into(),
        }
    }

    /// 入力が不正（400）。
    fn invalid(message: impl Into<String>) -> Self {
        Self::new(StatusCode::BAD_REQUEST, ERROR_INVALID_REQUEST, message)
    }

    /// ログイン失敗（401）。**理由は区別しない。**
    fn login_failed() -> Self {
        Self::new(
            StatusCode::UNAUTHORIZED,
            ERROR_LOGIN_FAILED,
            "ログインできませんでした",
        )
    }

    /// サーバ内部の失敗（500）。詳細はログにだけ残す。
    fn internal(context: &str, detail: String) -> Self {
        warn!(context, error = %detail, "窓口の内部エラー");
        Self::new(
            StatusCode::INTERNAL_SERVER_ERROR,
            ERROR_INTERNAL,
            "サーバ内部でエラーが発生しました",
        )
    }
}

/// `StoreError` を HTTP の応答へ写す。
impl From<StoreError> for ApiError {
    fn from(error: StoreError) -> Self {
        match error {
            StoreError::InvalidName(e) => ApiError::invalid(e.message()),
            StoreError::InvalidPublicKey(e) => ApiError::invalid(e.message()),
            StoreError::InvalidRepositoryId => {
                ApiError::invalid("repository_id は 32 桁の 16 進数である必要があります")
            }
            StoreError::InvalidProjectName => ApiError::invalid(format!(
                "project_name が長すぎます（{PROJECT_NAME_MAX_CHARS} 文字まで）"
            )),
            StoreError::InvalidExpiry => {
                ApiError::invalid("expires_in_hours が範囲外です")
            }
            StoreError::NameTaken => ApiError::new(
                StatusCode::CONFLICT,
                ERROR_NAME_TAKEN,
                "その名前は既に使われています",
            ),
            StoreError::OwnerExists => ApiError::new(
                StatusCode::CONFLICT,
                ERROR_OWNER_EXISTS,
                "このプロジェクトには既にオーナーが居ます",
            ),
            StoreError::InviteInvalid => ApiError::new(
                StatusCode::FORBIDDEN,
                ERROR_INVITE_INVALID,
                "招待コードが正しくないか、期限切れか、既に使われています",
            ),
            StoreError::Forbidden => ApiError::new(
                StatusCode::FORBIDDEN,
                ERROR_FORBIDDEN,
                "この操作はプロジェクトのオーナーだけが行えます",
            ),
            StoreError::MemberNotFound => ApiError::new(
                StatusCode::NOT_FOUND,
                ERROR_NOT_FOUND,
                "その参加者は見つかりません",
            ),
            // ★契約 3 章のとおり **409 `owner_exists`**。
            //   `forbidden` は 403 と対で定義されているコードなので、
            //   409 に載せるとエディタ側が「オーナーだけが行えます」という
            //   別の意味の文言を出してしまう（実サーバとの結合テストで発覚）。
            StoreError::CannotRevokeOwner => ApiError::new(
                StatusCode::CONFLICT,
                ERROR_OWNER_EXISTS,
                "オーナーの権限は失効できません",
            ),
            StoreError::RandomFailed => {
                ApiError::internal("random", "乱数の生成に失敗しました".to_string())
            }
            StoreError::PersistFailed(detail) => ApiError::internal("persist", detail),
        }
    }
}

/// `TokenError` を HTTP の応答へ写す（すべて 401）。
impl From<TokenError> for ApiError {
    fn from(error: TokenError) -> Self {
        ApiError::new(StatusCode::UNAUTHORIZED, ERROR_UNAUTHORIZED, error.message())
    }
}

/// エラー本体の JSON。
#[derive(Serialize)]
struct ErrorBody {
    /// エラーコード（機械可読）
    error: &'static str,
    /// 日本語の説明（人が読む）
    message: String,
}

impl IntoResponse for ApiError {
    fn into_response(self) -> Response {
        (
            self.status,
            Json(ErrorBody {
                error: self.code,
                message: self.message,
            }),
        )
            .into_response()
    }
}

/// `Json<T>` の取り出し失敗を、こちらのエラー形式へ揃える。
///
/// axum の既定の拒否応答はプレーンテキストなので、
/// そのままだとエディタ側が 2 種類の応答を扱う羽目になる。
fn body<T>(payload: Result<Json<T>, JsonRejection>) -> Result<T, ApiError> {
    match payload {
        Ok(Json(value)) => Ok(value),
        Err(rejection) => Err(ApiError::invalid(format!(
            "リクエストの JSON を解釈できません: {}",
            rejection.body_text()
        ))),
    }
}

// -----------------------------------------------------------------------------
// 入出力の型
// -----------------------------------------------------------------------------

/// `GET /v1/health` の応答。
#[derive(Serialize)]
struct HealthResponse {
    status: &'static str,
    issuer: String,
    audience: String,
    api: u32,
}

/// `POST /v1/bootstrap` の入力。
#[derive(Deserialize)]
struct BootstrapRequest {
    name: String,
    public_key: String,
    repository_id: String,
    #[serde(default)]
    project_name: String,
}

/// `POST /v1/bootstrap` の応答。
#[derive(Serialize)]
struct BootstrapResponse {
    name: String,
    role: &'static str,
}

/// `POST /v1/login/challenge` の入力。
#[derive(Deserialize)]
struct ChallengeRequest {
    name: String,
}

/// `POST /v1/login/challenge` の応答。
#[derive(Serialize)]
struct ChallengeResponse {
    challenge_id: String,
    nonce: String,
    expires_in: u64,
}

/// `POST /v1/login/complete` の入力。
#[derive(Deserialize)]
struct LoginRequest {
    challenge_id: String,
    signature: String,
}

/// ログイン応答に含まれる、参加しているプロジェクト 1 件。
#[derive(Serialize)]
struct GrantView {
    repository_id: String,
    project_name: String,
    role: &'static str,
}

/// `POST /v1/login/complete` の応答。
#[derive(Serialize)]
struct LoginResponse {
    access_token: String,
    /// 失効時刻（UNIX epoch ミリ秒）
    expires_at: u64,
    name: String,
    grants: Vec<GrantView>,
}

/// `POST /v1/invites` の入力。
#[derive(Deserialize)]
struct InviteRequest {
    repository_id: String,
    /// 省略時は `member`
    #[serde(default)]
    role: Option<String>,
    /// 省略時は `DEFAULT_INVITE_EXPIRES_IN_HOURS`
    #[serde(default)]
    expires_in_hours: Option<u64>,
}

/// `POST /v1/invites` の応答。
#[derive(Serialize)]
struct InviteResponse {
    /// 招待コードの平文。**この 1 回しか返らない。**
    invite_code: String,
    /// 失効時刻（UNIX epoch ミリ秒）
    expires_at: u64,
}

/// `POST /v1/join` の入力。
#[derive(Deserialize)]
struct JoinRequest {
    invite_code: String,
    name: String,
    public_key: String,
}

/// `POST /v1/join` の応答。
#[derive(Serialize)]
struct JoinResponse {
    name: String,
    repository_id: String,
    project_name: String,
    role: &'static str,
}

/// `GET /v1/members` のクエリ文字列。
#[derive(Deserialize)]
struct MembersQuery {
    repository_id: String,
}

/// 参加者 1 件。
#[derive(Serialize)]
struct MemberItem {
    name: String,
    role: &'static str,
    status: &'static str,
    added_at: u64,
}

/// `GET /v1/members` の応答。
#[derive(Serialize)]
struct MembersResponse {
    members: Vec<MemberItem>,
}

/// `POST /v1/members/revoke` の入力。
#[derive(Deserialize)]
struct RevokeRequest {
    repository_id: String,
    name: String,
}

/// `POST /v1/members/revoke` の応答。
#[derive(Serialize)]
struct RevokeResponse {
    name: String,
    status: &'static str,
}

// -----------------------------------------------------------------------------
// ルータ
// -----------------------------------------------------------------------------

/// 窓口のルータを組み立てる。
pub fn build_router(state: SharedState) -> Router {
    Router::new()
        .route(ROUTE_HEALTH, get(handle_health))
        .route(ROUTE_BOOTSTRAP, post(handle_bootstrap))
        .route(ROUTE_LOGIN_CHALLENGE, post(handle_login_challenge))
        .route(ROUTE_LOGIN_COMPLETE, post(handle_login_complete))
        .route(ROUTE_INVITES, post(handle_create_invite))
        .route(ROUTE_JOIN, post(handle_join))
        .route(ROUTE_MEMBERS, get(handle_list_members))
        .route(ROUTE_MEMBERS_REVOKE, post(handle_revoke_member))
        .route(ROUTE_JWKS, get(handle_jwks))
        // 本体サイズの上限。これを超えると axum が 413 を返す。
        .layer(axum::extract::DefaultBodyLimit::max(MAX_REQUEST_BODY_BYTES))
        .with_state(state)
}

// -----------------------------------------------------------------------------
// ハンドラ
// -----------------------------------------------------------------------------

/// `GET /v1/health` — 窓口が生きているかと、どの issuer/audience で発行するか。
async fn handle_health(State(state): State<SharedState>) -> Json<HealthResponse> {
    Json(HealthResponse {
        status: HEALTH_STATUS_OK,
        issuer: state.config.issuer.clone(),
        audience: state.config.audience.clone(),
        api: API_VERSION,
    })
}

/// `GET /jwks.json` — サーバの公開鍵。
///
/// Lore 本体は同じ内容をファイルから読むが、
/// 「外から見て鍵が合っているか」を確かめられるように HTTP でも出す。
async fn handle_jwks(State(state): State<SharedState>) -> impl IntoResponse {
    (
        [(header::CONTENT_TYPE, CONTENT_TYPE_JSON)],
        state.jwks_body.clone(),
    )
}

/// `POST /v1/bootstrap` — 最初のオーナーを登録する。**ループバック接続のみ。**
///
/// ループバックに限る理由: 招待も認証も無しにオーナーになれるため、
/// LAN の他の PC から叩けてはいけない。
/// オーナーの PC で動いているエディタからだけ呼べればよい。
async fn handle_bootstrap(
    State(state): State<SharedState>,
    ConnectInfo(peer): ConnectInfo<SocketAddr>,
    payload: Result<Json<BootstrapRequest>, JsonRejection>,
) -> Result<Json<BootstrapResponse>, ApiError> {
    if !is_loopback_peer(&peer) {
        warn!(peer = %peer, "ループバック以外からの bootstrap を拒否しました");
        return Err(ApiError::new(
            StatusCode::FORBIDDEN,
            ERROR_LOOPBACK_ONLY,
            "bootstrap はサーバと同じ PC からのみ実行できます",
        ));
    }

    let request = body(payload)?;
    check_length("name", &request.name, NAME_MAX_CHARS)?;
    check_length("public_key", &request.public_key, PUBLIC_KEY_MAX_CHARS)?;
    check_length("project_name", &request.project_name, PROJECT_NAME_MAX_CHARS)?;

    let result = state.store.bootstrap_owner(
        &request.name,
        &request.public_key,
        &request.repository_id,
        &request.project_name,
        now_ms(),
    )?;

    info!(
        name = %result.name,
        repository_id = %result.repository_id,
        "オーナーを登録しました"
    );

    Ok(Json(BootstrapResponse {
        name: result.name,
        role: Role::Owner.as_str(),
    }))
}

/// `POST /v1/login/challenge` — チャレンジを発行する。
///
/// **未登録・失効の名前でも同じ形の応答を返す。**
/// ここで存在を確かめてしまうと、名前の総当たりで参加者一覧を作られる。
async fn handle_login_challenge(
    State(state): State<SharedState>,
    payload: Result<Json<ChallengeRequest>, JsonRejection>,
) -> Result<Json<ChallengeResponse>, ApiError> {
    let request = body(payload)?;
    // 長さだけは見る（形式エラーであって、存在の有無ではない）
    check_length("name", &request.name, NAME_MAX_CHARS)?;

    let issued = state
        .challenges
        .issue(&request.name, now_ms())
        .map_err(|e| match e {
            ChallengeIssueError::TooManyPending => ApiError::new(
                StatusCode::TOO_MANY_REQUESTS,
                ERROR_TOO_MANY_REQUESTS,
                "ログイン要求が多すぎます。しばらく待ってからやり直してください",
            ),
            ChallengeIssueError::RandomFailed => {
                ApiError::internal("challenge", "乱数の生成に失敗しました".to_string())
            }
        })?;

    Ok(Json(ChallengeResponse {
        challenge_id: issued.challenge_id,
        nonce: issued.nonce,
        expires_in: issued.expires_in_seconds,
    }))
}

/// `POST /v1/login/complete` — 署名を検証してトークンを発行する。
///
/// 失敗の理由は一切区別しない（すべて 401 `login_failed`）。
/// 「名前が無い」「鍵が違う」「失効している」を区別すると、
/// それ自体が参加者名簿を調べる手段になる。
async fn handle_login_complete(
    State(state): State<SharedState>,
    payload: Result<Json<LoginRequest>, JsonRejection>,
) -> Result<Json<LoginResponse>, ApiError> {
    let request = body(payload)?;
    check_length("challenge_id", &request.challenge_id, CHALLENGE_ID_MAX_CHARS)?;
    check_length("signature", &request.signature, SIGNATURE_MAX_CHARS)?;

    let now = now_ms();

    // チャレンジは成否にかかわらずここで使い切る（1 回限り）
    let consumed = state
        .challenges
        .consume(&request.challenge_id, now)
        .ok_or_else(ApiError::login_failed)?;

    // 名前 → アカウント。無ければ失敗（理由は返さない）
    let account = state
        .store
        .find_account(&consumed.name)
        .ok_or_else(ApiError::login_failed)?;

    // 署名の検証
    let message = login_message(&request.challenge_id, &consumed.nonce);
    if !verify_login_signature(&account.public_key, &request.signature, &message) {
        return Err(ApiError::login_failed());
    }

    // 失効した参加者は新しいトークンを取れない
    if !account.has_any_active_grant() {
        return Err(ApiError::login_failed());
    }

    let issued = issue_token(
        &state.issuer_key,
        &state.config.issuer,
        &state.config.audience,
        state.config.token_ttl_hours,
        &account,
        now,
    )
    .map_err(|e| ApiError::internal("issue_token", e))?;

    let grants = account
        .grants
        .iter()
        .filter(|grant| grant.is_active())
        .map(|grant| GrantView {
            repository_id: grant.repository_id.clone(),
            project_name: grant.project_name.clone(),
            role: grant.role.as_str(),
        })
        .collect();

    info!(name = %account.name, "トークンを発行しました");

    Ok(Json(LoginResponse {
        access_token: issued.access_token,
        expires_at: issued.expires_at_ms,
        name: account.name,
        grants,
    }))
}

/// `POST /v1/invites` — 招待コードを発行する（owner だけ）。
async fn handle_create_invite(
    State(state): State<SharedState>,
    headers: HeaderMap,
    payload: Result<Json<InviteRequest>, JsonRejection>,
) -> Result<Json<InviteResponse>, ApiError> {
    let request = body(payload)?;
    let claims = authenticate(&state, &headers)?;

    // トークン側にも owner のエントリがあることを確かめる（契約 3 章）。
    // 実際の可否は store 側の require_owner が決める（失効が即座に効く）。
    if !claims.has_role(&request.repository_id, Role::Owner) {
        return Err(StoreError::Forbidden.into());
    }

    let role = parse_role(request.role.as_deref())?;
    let expires_in_hours = request
        .expires_in_hours
        .unwrap_or(DEFAULT_INVITE_EXPIRES_IN_HOURS);

    let created = state.store.create_invite(
        &claims.sub,
        &request.repository_id,
        role,
        expires_in_hours,
        now_ms(),
    )?;

    // **招待コードの平文はログに出さない。**
    info!(
        owner = %claims.sub,
        repository_id = %request.repository_id,
        role = role.as_str(),
        "招待コードを発行しました"
    );

    Ok(Json(InviteResponse {
        invite_code: created.invite_code,
        expires_at: created.expires_at_ms,
    }))
}

/// `POST /v1/join` — 招待コードでプロジェクトへ参加する。
///
/// 認証は不要（招待コードそのものが資格）。
async fn handle_join(
    State(state): State<SharedState>,
    payload: Result<Json<JoinRequest>, JsonRejection>,
) -> Result<Json<JoinResponse>, ApiError> {
    let request = body(payload)?;
    check_length("invite_code", &request.invite_code, INVITE_CODE_MAX_CHARS)?;
    check_length("name", &request.name, NAME_MAX_CHARS)?;
    check_length("public_key", &request.public_key, PUBLIC_KEY_MAX_CHARS)?;

    let result = state.store.join_with_invite(
        &request.invite_code,
        &request.name,
        &request.public_key,
        now_ms(),
    )?;

    info!(
        name = %result.name,
        repository_id = %result.repository_id,
        role = result.role.as_str(),
        "招待コードでの参加を受け付けました"
    );

    Ok(Json(JoinResponse {
        name: result.name,
        repository_id: result.repository_id,
        project_name: result.project_name,
        role: result.role.as_str(),
    }))
}

/// `GET /v1/members?repository_id=…` — 参加者の一覧（owner だけ）。
async fn handle_list_members(
    State(state): State<SharedState>,
    headers: HeaderMap,
    query: Result<Query<MembersQuery>, QueryRejection>,
) -> Result<Json<MembersResponse>, ApiError> {
    // クエリ文字列の不備も、本体の JSON と同じ形のエラーで返す。
    let Query(query) =
        query.map_err(|_| ApiError::invalid("repository_id を指定してください"))?;
    let claims = authenticate(&state, &headers)?;

    if !claims.has_role(&query.repository_id, Role::Owner) {
        return Err(StoreError::Forbidden.into());
    }

    let members = state.store.list_members(&claims.sub, &query.repository_id)?;

    Ok(Json(MembersResponse {
        members: members
            .into_iter()
            .map(|member| MemberItem {
                name: member.name,
                role: member.role.as_str(),
                status: member.status.as_str(),
                added_at: member.added_at,
            })
            .collect(),
    }))
}

/// `POST /v1/members/revoke` — 参加者を失効させる（owner だけ）。
async fn handle_revoke_member(
    State(state): State<SharedState>,
    headers: HeaderMap,
    payload: Result<Json<RevokeRequest>, JsonRejection>,
) -> Result<Json<RevokeResponse>, ApiError> {
    let request = body(payload)?;
    let claims = authenticate(&state, &headers)?;
    check_length("name", &request.name, NAME_MAX_CHARS)?;

    if !claims.has_role(&request.repository_id, Role::Owner) {
        return Err(StoreError::Forbidden.into());
    }

    let name = state
        .store
        .revoke_member(&claims.sub, &request.repository_id, &request.name)?;

    info!(
        owner = %claims.sub,
        target = %name,
        repository_id = %request.repository_id,
        "参加者を失効させました"
    );

    Ok(Json(RevokeResponse {
        name,
        status: super::model::GrantStatus::Revoked.as_str(),
    }))
}

// -----------------------------------------------------------------------------
// 補助
// -----------------------------------------------------------------------------

/// `Authorization: Bearer <token>` を検証してクレームを返す。
fn authenticate(state: &AuthState, headers: &HeaderMap) -> Result<TokenClaims, ApiError> {
    let raw = headers
        .get(header::AUTHORIZATION)
        .and_then(|value| value.to_str().ok());
    let token = extract_bearer(raw)?;
    let claims = verify_token(
        &state.issuer_key,
        &state.config.issuer,
        &state.config.audience,
        token,
    )?;
    Ok(claims)
}

/// 接続元が同じ PC（ループバック）かどうか。
///
/// `host = "::"` で待ち受けると、IPv4 の接続が
/// IPv4 射影アドレス（`::ffff:127.0.0.1`）として見える。
/// `Ipv6Addr::is_loopback()` は `::1` にしか true を返さないので、
/// 射影を解いてから判定しないと、同じ PC からの bootstrap まで拒否してしまう。
fn is_loopback_peer(peer: &SocketAddr) -> bool {
    match peer.ip() {
        std::net::IpAddr::V4(v4) => v4.is_loopback(),
        std::net::IpAddr::V6(v6) => {
            v6.is_loopback() || v6.to_ipv4_mapped().is_some_and(|v4| v4.is_loopback())
        }
    }
}

/// 文字列の長さ（文字数）が上限以内かを確かめる。
///
/// 本体サイズの上限とは別に、フィールドごとにも絞る。
/// 「16 KiB の名前」が store まで届かないようにするため。
fn check_length(field: &str, value: &str, max_chars: usize) -> Result<(), ApiError> {
    if value.chars().count() > max_chars {
        return Err(ApiError::invalid(format!(
            "{field} が長すぎます（{max_chars} 文字まで）"
        )));
    }
    Ok(())
}

/// 役割の文字列を列挙へ変換する。省略時は `member`。
fn parse_role(role: Option<&str>) -> Result<Role, ApiError> {
    match role {
        None => Ok(Role::Member),
        Some(text) if text == Role::Member.as_str() => Ok(Role::Member),
        Some(text) if text == Role::Owner.as_str() => Ok(Role::Owner),
        Some(other) => Err(ApiError::invalid(format!(
            "role には {} か {} を指定してください: {other}",
            Role::Owner.as_str(),
            Role::Member.as_str()
        ))),
    }
}


// =============================================================================
// 単体テスト
// =============================================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// ループバック判定。IPv4 射影アドレスも同じ PC として扱うこと。
    #[test]
    fn loopback_detection_covers_ipv4_mapped() {
        let addr = |s: &str| s.parse::<SocketAddr>().unwrap();

        assert!(is_loopback_peer(&addr("127.0.0.1:1234")));
        assert!(is_loopback_peer(&addr("127.0.0.53:1234")));
        assert!(is_loopback_peer(&addr("[::1]:1234")));
        // host = "::" で待ち受けたときに IPv4 の接続がこう見える
        assert!(is_loopback_peer(&addr("[::ffff:127.0.0.1]:1234")));

        // 外部からの接続は拒否
        assert!(!is_loopback_peer(&addr("192.168.1.10:1234")));
        assert!(!is_loopback_peer(&addr("[::ffff:192.168.1.10]:1234")));
        assert!(!is_loopback_peer(&addr("[2001:db8::1]:1234")));
    }

    /// 長さの検証が文字数（バイト数ではない）で行われること。
    #[test]
    fn length_check_counts_characters() {
        // 日本語 3 文字はバイト数だと 9 だが、文字数は 3
        assert!(check_length("name", "つばさ", 3).is_ok());
        assert!(check_length("name", "つばさだ", 3).is_err());
        assert!(check_length("name", "", 0).is_ok());
    }

    /// 役割の文字列変換。
    #[test]
    fn role_parsing() {
        assert_eq!(parse_role(None).unwrap(), Role::Member);
        assert_eq!(parse_role(Some("member")).unwrap(), Role::Member);
        assert_eq!(parse_role(Some("owner")).unwrap(), Role::Owner);
        assert!(parse_role(Some("admin")).is_err());
        assert!(parse_role(Some("")).is_err());
        // 大文字は受け付けない（表記を 1 つに固定する）
        assert!(parse_role(Some("Owner")).is_err());
    }

    /// `StoreError` から HTTP ステータスへの写像が契約どおりであること。
    #[test]
    fn store_errors_map_to_contract_status_codes() {
        let cases: Vec<(StoreError, StatusCode, &str)> = vec![
            (
                StoreError::OwnerExists,
                StatusCode::CONFLICT,
                ERROR_OWNER_EXISTS,
            ),
            (StoreError::NameTaken, StatusCode::CONFLICT, ERROR_NAME_TAKEN),
            (
                StoreError::InviteInvalid,
                StatusCode::FORBIDDEN,
                ERROR_INVITE_INVALID,
            ),
            (
                StoreError::Forbidden,
                StatusCode::FORBIDDEN,
                ERROR_FORBIDDEN,
            ),
            (
                StoreError::MemberNotFound,
                StatusCode::NOT_FOUND,
                ERROR_NOT_FOUND,
            ),
            // owner の権限を失効させようとしたとき。
            // 契約 3 章では bootstrap の「既にオーナーが居る」と同じ
            // 409 `owner_exists` で返し、呼び出した操作で言い分ける。
            (
                StoreError::CannotRevokeOwner,
                StatusCode::CONFLICT,
                ERROR_OWNER_EXISTS,
            ),
            (
                StoreError::InvalidRepositoryId,
                StatusCode::BAD_REQUEST,
                ERROR_INVALID_REQUEST,
            ),
        ];

        for (error, status, code) in cases {
            let api: ApiError = error.into();
            assert_eq!(api.status, status);
            assert_eq!(api.code, code);
            assert!(!api.message.is_empty(), "説明が空");
        }
    }

    /// ログイン失敗はすべて同じ応答になること（理由を漏らさない）。
    #[test]
    fn login_failures_are_indistinguishable() {
        let a = ApiError::login_failed();
        let b = ApiError::login_failed();
        assert_eq!(a.status, StatusCode::UNAUTHORIZED);
        assert_eq!(a.code, ERROR_LOGIN_FAILED);
        assert_eq!(a.message, b.message);
    }

    /// トークンのエラーはすべて 401 になること。
    #[test]
    fn token_errors_map_to_unauthorized() {
        for error in [TokenError::Missing, TokenError::TooLong, TokenError::Invalid] {
            let api: ApiError = error.into();
            assert_eq!(api.status, StatusCode::UNAUTHORIZED);
            assert_eq!(api.code, ERROR_UNAUTHORIZED);
        }
    }
}
