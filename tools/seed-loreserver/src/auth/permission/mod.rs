// =============================================================================
// SEED 権限サービス（Lore の `auth_url` が指す先）
// =============================================================================
// 【何のためにあるか】
// Lore v0.9.0 のサーバは `[environment.endpoint] auth_url` を設定すると、
// リポジトリを引く／作る／削除する操作の可否を **その URL の gRPC** へ
// 問い合わせる。一方クライアントは、`auth_url` が空だと QUIC の
// ストレージセッションにトークンを載せない（= pull が通らない）。
// そのため「auth_url を書くと clone と作成が落ち、書かないと pull が落ちる」
// という二律背反があった（docs/seed_accounts.md の同名の節）。
//
// ここはその問い合わせを**受ける側**を SEED 自身の中に立てるモジュール。
// `auth_url` をここへ向ければ、1 つの設定のままクローン・送信・取得・
// ロック・新しいリポジトリの作成がすべて通る。
//
// 【Lore が呼ぶ RPC（v0.9.0 のソースで確認）】
//   epic_urc.UrcAuthApi/CheckUserPermission
//     … lore-server/src/authnz/repository_authorizer.rs の AuthClientAuthorizer。
//        RepositoryGet / RepositoryQuery / RepositoryMetadataGet / MetadataSet が使う。
//        引数は resource_id = ["urc-<32桁hexのリポジトリID>"]、target_user = なし。
//        `authorization` メタデータに、受け取った Authorization ヘッダがそのまま載る。
//   epic_urc.UrcAuthApi/LookupUserPermissions
//     … lore-server/src/grpc/handlers/repository_list.rs。`repository list` が使う。
//        引数は resource_filter = "urc"。
//   ucs.auth.RebacApi/CreateResource / DeleteResource
//     … lore-server/src/grpc/{handlers,repository/v1}/repository_create.rs と
//        repository_delete.rs。**接続先は同じ auth_url。**
//
// 【スキームと TLS】
// `lore-server/src/authnz/auth.rs` と `rebac.rs` は
// `if auth_url.starts_with("https://")` のときだけ TLS を設定する。
// つまり **`http://127.0.0.1:41352` がそのまま使える**（平文 h2c）。
// OS の証明書ストアへ何かを登録する必要は無い。
//
// 【クライアントはここへ接続しない】
// `auth_url` はサーバの environment 応答に載ってクライアントへも届くが、
// エディタと CLI はトークンを供給しているので交換を短絡し、この URL へは
// 接続しに行かない（`lore-transport/src/auth/exchange.rs` の `exchange()` 冒頭で
// access token があれば即 return、`lore-credential/src/token_store.rs:606` で
// identity token があれば即 return）。したがって `127.0.0.1` を書いても
// 他の PC の参加者が困ることは無い。
//
// 【ファイルの分担】
//   proto.rs     … build.rs が生成したスタブの取り込み
//   decision.rs  … 判定そのもの（gRPC に依存しない。単体テストはここ）
//   urc_auth.rs  … epic_urc.UrcAuthApi の実装（翻訳だけ）
//   rebac.rs     … ucs.auth.RebacApi の実装（翻訳だけ）
//   mod.rs       … 起動（別スレッド＋別ランタイム＋別ポート）
//
// 【起動の流儀は発行窓口（`auth/http.rs`）と同じ】
// `server_main()` は同期関数で呼び出しスレッドを占有するため、
// **その前に**別スレッド＋別ランタイムで立ち上げておく。
// 待ち受けの bind だけは呼び出しスレッドで行い、ポートが塞がっていれば
// サーバ全体の起動を止める（権限サービスが居ないまま Lore を
// auth_url 付きで起動すると、全員が「Not found」で締め出される）。
// =============================================================================

mod decision;
mod proto;
mod rebac;
mod urc_auth;

use std::net::TcpListener;
use std::sync::Arc;

use tonic::transport::Server;

use super::http::AuthState;
use decision::PermissionAuthority;
use proto::epic_urc::urc_auth_api_server::UrcAuthApiServer;
use proto::ucs_auth::rebac_api_server::RebacApiServer;

/// 起動時メッセージの接頭辞（発行窓口と見分けられるようにする）。
const LOG_PREFIX: &str = "[seed_auth/permission]";

/// 権限サービスを起動する。
///
/// **`server_main()` を呼ぶ前に、同じスレッドから 1 回だけ呼ぶこと。**
///
/// # 引数
/// - `state`: 発行窓口と共有する状態（設定・台帳・署名鍵）。
///   同じ `AccountStore` の実体を見るので、招待と失効が即座に判定へ効く。
///
/// # Errors
/// 待ち受けアドレスを掴めない、またはスレッドを作れない場合。
/// この場合は**サーバ全体の起動を止めること**。
pub fn start(state: Arc<AuthState>) -> Result<(), String> {
    let address = state.config.permission_listen_address();

    // bind だけは同期でここで行う。別スレッドの中で bind すると、
    // ポートが塞がっていても呼び出し側はエラーに気づけない。
    let listener = TcpListener::bind(&address)
        .map_err(|e| format!("{LOG_PREFIX} {address} を待ち受けられません: {e}"))?;
    listener
        .set_nonblocking(true)
        .map_err(|e| format!("{LOG_PREFIX} ソケットを非ブロッキングにできません: {e}"))?;

    eprintln!("{LOG_PREFIX} 権限サービスを起動します: http://{address}");
    eprintln!(
        "{LOG_PREFIX}   Lore 側は [environment.endpoint] auth_url = \"http://{address}\" を設定してください"
    );
    // 設定ミスの筆頭（auth_url が別の場所を指している）を起動時に知らせる。
    // 止めはしない（auth_url を読むのは Lore 本体であって、こちらではないため）。
    if let Some(advice) = state.config.auth_url_advice() {
        eprintln!("{LOG_PREFIX}   ★確認してください: {advice}");
    }
    if state.config.repository_creators.is_empty() {
        eprintln!(
            "{LOG_PREFIX}   repository_creators が空です。\
             認証が有効な間は誰も新しいリポジトリを作れません"
        );
    } else {
        eprintln!(
            "{LOG_PREFIX}   新しいリポジトリを作れる人: {}",
            state.config.repository_creators.join(", ")
        );
    }

    spawn_server_thread(listener, state, &address)
}

/// 権限サービスの gRPC サーバを、専用スレッド＋専用ランタイムで走らせる。
///
/// ランタイムを分ける理由は発行窓口と同じ。
/// `server_main()` は自前でランタイムを作って呼び出しスレッドを占有するので、
/// 相乗りする手段が無い。
///
/// 負荷は「リポジトリを引くたびに 1 回」程度なので、
/// 現在のスレッドだけで回す最小構成（`new_current_thread`）で足りる。
fn spawn_server_thread(
    listener: TcpListener,
    state: Arc<AuthState>,
    address: &str,
) -> Result<(), String> {
    let address_for_error = address.to_string();

    std::thread::Builder::new()
        .name("seed-auth-permission".to_string())
        .spawn(move || {
            let runtime = match tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
            {
                Ok(runtime) => runtime,
                Err(e) => {
                    eprintln!("{LOG_PREFIX} tokio ランタイムを作成できません: {e}");
                    return;
                }
            };

            runtime.block_on(async move {
                let listener = match tokio::net::TcpListener::from_std(listener) {
                    Ok(listener) => listener,
                    Err(e) => {
                        eprintln!("{LOG_PREFIX} ソケットを tokio へ渡せません: {e}");
                        return;
                    }
                };

                // 判定器は 2 つのサービスで共有する（台帳の実体を 1 つに保つ）。
                let authority = Arc::new(PermissionAuthority::new(state));

                let result = Server::builder()
                    .add_service(UrcAuthApiServer::new(urc_auth::UrcAuthService::new(
                        Arc::clone(&authority),
                    )))
                    .add_service(RebacApiServer::new(rebac::RebacService::new(authority)))
                    .serve_with_incoming(tokio_stream::wrappers::TcpListenerStream::new(listener))
                    .await;

                if let Err(e) = result {
                    eprintln!("{LOG_PREFIX} 権限サービスが停止しました: {e}");
                }
            });
        })
        .map_err(|e| {
            format!("{LOG_PREFIX} {address_for_error} の待ち受けスレッドを作成できません: {e}")
        })?;

    Ok(())
}
