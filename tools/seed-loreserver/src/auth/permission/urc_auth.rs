// =============================================================================
// 権限サービス : epic_urc.UrcAuthApi の実装
// =============================================================================
// 役割は **gRPC と判定器（decision.rs）の間の翻訳だけ**。
// 判定そのものはここに書かない（テストしづらくなるため）。
//
// 【Lore 側がこの応答をどう読むか（v0.9.0 実測）】
//   CheckUserPermission:
//     lore-server/src/authnz/repository_authorizer.rs:78-89 が
//     `allowed_resource_permission` の**先頭 1 件**の `resource_id` が
//     問い合わせた値と一致するかだけを見る。一致しなければ内部エラー扱い。
//     エラーコードは PermissionDenied / Unauthenticated を区別して扱い、
//     いずれにせよ最終的に `RepositoryNotFound` へ畳まれて
//     利用者には「Not found」と出る（存在を漏らさないので都合がよい）。
//   LookupUserPermissions:
//     lore-server/src/grpc/handlers/repository_list.rs が
//     `resource_permission[].resource_id` から `urc-` を剥がして
//     32 桁 hex として解釈する。解釈できないものは黙って捨てられる。
//
// 【ログに秘密を出さない】
// トークンそのものは絶対に出さない。出すのは名前とリポジトリ ID と可否だけ。
// =============================================================================

use std::sync::Arc;

use tonic::Request;
use tonic::Response;
use tonic::Status;
use tracing::debug;
use tracing::info;

use super::decision::DecisionError;
use super::decision::PermissionAuthority;
use super::proto::epic_urc::CheckUserPermissionRequest;
use super::proto::epic_urc::CheckUserPermissionResponse;
use super::proto::epic_urc::HealthCheckRequest;
use super::proto::epic_urc::HealthCheckResponse;
use super::proto::epic_urc::LookupUserPermissionsRequest;
use super::proto::epic_urc::LookupUserPermissionsResponse;
use super::proto::epic_urc::ResourcePermission;
use super::proto::epic_urc::urc_auth_api_server::UrcAuthApi;

/// `GET /v1/health` と揃えた死活確認の応答値。
const HEALTH_STATUS_OK: &str = "ok";

/// `authorization` メタデータのキー。
/// Lore は受け取った Authorization ヘッダをこの名前でそのまま転送してくる
/// （`lore-server/src/authnz/common.rs`）。
const METADATA_AUTHORIZATION: &str = "authorization";

/// 認証に失敗したときに返す文言（理由は区別しない）。
const MESSAGE_UNAUTHENTICATED: &str = "Not authenticated";

/// 権限が無いときに返す文言（存在を漏らさない）。
const MESSAGE_DENIED: &str = "Not authorized for resource";

/// 内部エラーのときに返す文言（詳細はサーバのログにだけ残す）。
const MESSAGE_INTERNAL: &str = "Permission service failure";

/// 1 回の `CheckUserPermission` で受け付けるリソース数の上限。
///
/// Lore v0.9.0 が送ってくるのは**常に 1 件**
/// （`repository_authorizer.rs` の `resource_id: vec![resource_id]`）。
/// 上限を置くのは、LAN へ公開したときに巨大な配列で台帳のロックを
/// 長時間握らせられないようにするため。余裕を見て 64 件にしてある。
const MAX_RESOURCE_IDS_PER_REQUEST: usize = 64;

/// リソース数が上限を超えたときに返す文言。
const MESSAGE_TOO_MANY_RESOURCES: &str = "Too many resource ids";

/// `epic_urc.UrcAuthApi` の SEED 実装。
pub struct UrcAuthService {
    /// 判定器（`RebacApi` と共有する）
    authority: Arc<PermissionAuthority>,
}

impl UrcAuthService {
    /// 判定器を受け取ってサービスを作る。
    pub fn new(authority: Arc<PermissionAuthority>) -> Self {
        Self { authority }
    }

    /// 要求の `authorization` メタデータから呼び出し元の名前を取り出す。
    ///
    /// # Errors
    /// トークンが無い／不正なら `Unauthenticated`。
    fn caller_of<T>(&self, request: &Request<T>) -> Result<String, Status> {
        let authorization = request
            .metadata()
            .get(METADATA_AUTHORIZATION)
            .and_then(|value| value.to_str().ok());

        self.authority
            .authenticate(authorization)
            .map_err(decision_error_to_status)
    }
}

#[tonic::async_trait]
impl UrcAuthApi for UrcAuthService {
    /// 死活確認。認証は要らない（存在の確認だけで、何も漏れない）。
    async fn health_check(
        &self,
        _request: Request<HealthCheckRequest>,
    ) -> Result<Response<HealthCheckResponse>, Status> {
        Ok(Response::new(HealthCheckResponse {
            status: HEALTH_STATUS_OK.to_string(),
        }))
    }

    /// 「この利用者はこのリソース（リポジトリ）を触ってよいか」。
    ///
    /// 1 件も許可できなかった場合は **`PermissionDenied` を返す**。
    /// 空の応答を返しても Lore 側は内部エラーとして扱うが、
    /// 拒否だと分かる形にしておくほうがサーバのログを追いやすい。
    async fn check_user_permission(
        &self,
        request: Request<CheckUserPermissionRequest>,
    ) -> Result<Response<CheckUserPermissionResponse>, Status> {
        let caller = self.caller_of(&request)?;
        let requested = request.into_inner().resource_id;

        if requested.len() > MAX_RESOURCE_IDS_PER_REQUEST {
            return Err(Status::invalid_argument(MESSAGE_TOO_MANY_RESOURCES));
        }

        let decisions = self.authority.decide_resources(&caller, &requested);

        let mut allowed: Vec<ResourcePermission> = Vec::new();
        let mut denied: Vec<ResourcePermission> = Vec::new();
        for decision in decisions {
            match decision.role {
                Some(role) => allowed.push(ResourcePermission {
                    resource_id: decision.resource_id,
                    permission: vec![role.as_str().to_string()],
                }),
                None => denied.push(ResourcePermission {
                    resource_id: decision.resource_id,
                    permission: Vec::new(),
                }),
            }
        }

        if allowed.is_empty() {
            // 拒否の理由（誰が・何を）はサーバのログにだけ残す。
            debug!(
                user = %caller,
                resources = ?denied.iter().map(|r| r.resource_id.as_str()).collect::<Vec<_>>(),
                "権限サービス: 参加していないリポジトリへの要求を拒否しました"
            );
            return Err(Status::permission_denied(MESSAGE_DENIED));
        }

        Ok(Response::new(CheckUserPermissionResponse {
            allowed_resource_permission: allowed,
            denied_resource_permission: denied,
        }))
    }

    /// 「この利用者が触れるリソースを全部挙げよ」（`repository list`）。
    ///
    /// ページングは使わない（参加できるプロジェクト数は多くても数十で、
    /// 分割する意味が無い）。`next_page_token` は常に未設定にする。
    async fn lookup_user_permissions(
        &self,
        request: Request<LookupUserPermissionsRequest>,
    ) -> Result<Response<LookupUserPermissionsResponse>, Status> {
        let caller = self.caller_of(&request)?;

        let resource_permission = self
            .authority
            .list_resources(&caller)
            .into_iter()
            .map(|(resource_id, role)| ResourcePermission {
                resource_id,
                permission: vec![role.as_str().to_string()],
            })
            .collect::<Vec<_>>();

        info!(
            user = %caller,
            count = resource_permission.len(),
            "権限サービス: 触れるリポジトリの一覧を返しました"
        );

        Ok(Response::new(LookupUserPermissionsResponse {
            resource_permission,
            next_page_token: None,
        }))
    }
}

/// 判定の失敗を gRPC のステータスへ写す。
///
/// **`RebacApi` 側と同じ写し方をすること**（片方だけ変えると、
/// 同じ拒否理由が操作によって別の見え方になる）。
pub fn decision_error_to_status(error: DecisionError) -> Status {
    match error {
        DecisionError::Unauthenticated => Status::unauthenticated(MESSAGE_UNAUTHENTICATED),
        DecisionError::Denied => Status::permission_denied(MESSAGE_DENIED),
        DecisionError::Internal(detail) => {
            // 詳細はログにだけ残す（利用者へ返すと内部構造が漏れる）。
            tracing::warn!(detail = %detail, "権限サービス: 内部エラー");
            Status::internal(MESSAGE_INTERNAL)
        }
    }
}
