// =============================================================================
// 権限サービス : ucs.auth.RebacApi の実装
// =============================================================================
// Lore は `RepositoryCreate` の途中で `CreateResource` を、
// `RepositoryDelete` の途中で `DeleteResource` を、
// **`UrcAuthApi` と同じ auth_url** に対して呼ぶ。
// ここもやることは gRPC と判定器（decision.rs）の翻訳だけ。
//
// 【Lore 側がこの応答をどう読むか（v0.9.0 実測）】
// `lore-server/src/grpc/handlers/repository_create.rs` の
// `repository_create_auth_resource` がコードで場合分けしている。
//   Ok            … そのまま作成を続ける
//   AlreadyExists … 「既にある」とみなして作成を続ける
//   PermissionDenied … 「permission denied」で作成を中止
//   Unauthenticated  … 「reauthenticate」で作成を中止
//   NotFound         … failed_precondition へ変換して中止
//   それ以外          … internal へ変換して中止
// したがって **拒否は `PermissionDenied` で返す**のが素直。
//
// 【作成時に owner を自動登録する】
// `CreateResource` は「リポジトリ ID が初めて確定する瞬間」なので、
// ここで作成者を owner として台帳へ書いてしまう。
// これで、ループバックからの `POST /v1/bootstrap` を踏まなくても
// 2 つめ以降のリポジトリを作れる（判定と登録は `decision.rs` 側）。
// =============================================================================

use std::sync::Arc;

use tonic::Request;
use tonic::Response;
use tonic::Status;
use tracing::info;

use super::decision::PermissionAuthority;
use super::proto::ucs_auth::CreateResourceRequest;
use super::proto::ucs_auth::CreateResourceResponse;
use super::proto::ucs_auth::DeleteResourceRequest;
use super::proto::ucs_auth::DeleteResourceResponse;
use super::proto::ucs_auth::rebac_api_server::RebacApi;
use super::urc_auth::decision_error_to_status;
use crate::auth::now_ms;

/// `authorization` メタデータのキー（`UrcAuthApi` と同じ）。
const METADATA_AUTHORIZATION: &str = "authorization";

/// `ucs.auth.RebacApi` の SEED 実装。
pub struct RebacService {
    /// 判定器（`UrcAuthApi` と共有する）
    authority: Arc<PermissionAuthority>,
}

impl RebacService {
    /// 判定器を受け取ってサービスを作る。
    pub fn new(authority: Arc<PermissionAuthority>) -> Self {
        Self { authority }
    }

    /// 要求の `authorization` メタデータから呼び出し元の名前を取り出す。
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
impl RebacApi for RebacService {
    /// 「この利用者は、この ID のリポジトリを新しく作ってよいか」。
    ///
    /// 許可した場合は、その場で作成者をそのリポジトリの owner として登録する。
    /// 登録に失敗したら `Internal` を返して**作成そのものを止める**
    /// （誰も所有していないリポジトリを作らせないため）。
    async fn create_resource(
        &self,
        request: Request<CreateResourceRequest>,
    ) -> Result<Response<CreateResourceResponse>, Status> {
        let caller = self.caller_of(&request)?;
        let payload = request.into_inner();

        self.authority
            .authorize_create(
                &caller,
                &payload.resource_id,
                &payload.resource_name,
                now_ms(),
            )
            .map_err(decision_error_to_status)?;

        info!(
            user = %caller,
            resource = %payload.resource_id,
            name = %payload.resource_name,
            "権限サービス: 新しいリポジトリの作成を許可し、作成者を owner として登録しました"
        );

        Ok(Response::new(CreateResourceResponse {}))
    }

    /// 「この利用者は、このリポジトリを削除してよいか」。owner だけが通る。
    async fn delete_resource(
        &self,
        request: Request<DeleteResourceRequest>,
    ) -> Result<Response<DeleteResourceResponse>, Status> {
        let caller = self.caller_of(&request)?;
        let payload = request.into_inner();

        self.authority
            .authorize_delete(&caller, &payload.resource_id)
            .map_err(decision_error_to_status)?;

        info!(
            user = %caller,
            resource = %payload.resource_id,
            "権限サービス: リポジトリの削除を許可しました"
        );

        Ok(Response::new(DeleteResourceResponse {}))
    }
}
