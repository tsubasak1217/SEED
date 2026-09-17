// =============================================================================
// SEED アカウント発行窓口
// =============================================================================
// Hub（スタート画面）で作った SEED アカウントを、このプロジェクトの
// 参加者として登録・認証し、Lore が受け取れる短寿命の JWT を発行する
// 小さな HTTP サーバ。取り決めの正典は docs/seed_accounts.md。
//
// ファイルの分担:
//   config.rs      … `[seed_auth]` の読み込み（Lore と同じ規則で同じファイルを読む）
//   model.rs       … accounts.json のデータ構造と名前の規則
//   store.rs       … accounts.json の読み書きと参加者の操作（招待・参加・失効）
//   issuer.rs      … サーバの署名鍵の生成・保存と jwks.json の書き出し
//   token.rs       … JWT の組み立てと検証（**トークンの構造を触るのはここだけ**）
//   user_key.rs    … 利用者の公開鍵と署名（ECDSA P-256 / P1363）の検証
//   challenge.rs   … ログインチャレンジ（1 回限り・60 秒・件数上限）
//   crypto.rs      … base64url・乱数・SHA-256
//   atomic_file.rs … tmp → rename の原子的置換
//   http.rs        … HTTP API（ルーティングと入出力の変換だけ）
//
// **起動の順番が重要。**
//   `server_main()` は同期関数で、内部で tokio ランタイムを作り、
//   呼び出したスレッドをブロックする。したがって
//     1. このモジュールの `start()` を呼ぶ（別スレッド＋別ランタイムで窓口が動き出す）
//     2. そのあとで `server_main()` を呼ぶ
//   の順にする。逆にすると窓口は永久に起動しない。
//   また、非同期タスクの内側から `server_main()` を呼ぶと panic する。
//
//   さらに、`jwks.json` は **`server_main()` より前に**書き終えておく必要がある。
//   Lore 本体は起動時に JWKS を読み、読めないと起動そのものが失敗する
//   （lore-server/src/server.rs の `fetch_new_keys(None).await?`）。
//
// ログについて:
//   tracing の購読は `server_main()` の中で初期化されるため、
//   それより前に出す起動時のメッセージは `eprintln!` を使う
//   （tracing で出しても捨てられて、設定ミスの原因が分からなくなる）。
//   起動後のリクエストログは tracing を使う。
// =============================================================================

mod atomic_file;
mod challenge;
mod config;
mod crypto;
mod http;
mod issuer;
mod model;
mod store;
mod token;
mod user_key;

use std::net::TcpListener;
use std::sync::Arc;

use challenge::ChallengeTable;
use config::SeedAuthConfig;
use http::AuthState;
use issuer::IssuerKey;
use store::AccountStore;

/// 起動時メッセージの接頭辞（設定ミスを見つけやすくするため統一する）。
const LOG_PREFIX: &str = "[seed_auth]";

/// 窓口を起動する（`[seed_auth] enabled = true` のときだけ）。
///
/// **`server_main()` を呼ぶ前に、同じスレッドから 1 回だけ呼ぶこと。**
///
/// 戻り値:
///   - `Ok(false)` … 設定が無い／`enabled = false` なので何も起動していない
///   - `Ok(true)`  … 窓口が別スレッドで動き出した
///   - `Err(_)`    … 設定・鍵・データファイル・ポートのいずれかに問題がある。
///                   この場合は**サーバ全体の起動を止める**こと。
///                   窓口が動かないまま Lore だけ認証有効で起動すると、
///                   誰もトークンを取れず、全員が締め出される。
pub fn start() -> Result<bool, String> {
    let args: Vec<String> = std::env::args().collect();

    let Some(config) = config::load(&args)? else {
        return Ok(false);
    };

    // --- データフォルダと鍵 ------------------------------------------------
    std::fs::create_dir_all(&config.data_dir).map_err(|e| {
        format!(
            "{LOG_PREFIX} データフォルダを作成できません {:?}: {e}",
            config.data_dir
        )
    })?;

    let now_ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0);

    let issuer_key = IssuerKey::load_or_create(&config.data_dir, now_ms)?;
    // JWKS は Lore 本体が起動時に読むので、ここで必ず書き終えておく。
    let jwks_path = issuer_key.write_jwks(&config.data_dir)?;
    let jwks_body = issuer_key.jwks_json()?;

    let store = AccountStore::open(&config.data_dir)?;

    // --- 待ち受け ----------------------------------------------------------
    // bind だけは同期でここで行う。別スレッドの中で bind すると、
    // ポートが塞がっていても main 側はエラーに気づけない。
    let address = config.listen_address();
    let listener = TcpListener::bind(&address)
        .map_err(|e| format!("{LOG_PREFIX} {address} を待ち受けられません: {e}"))?;
    listener
        .set_nonblocking(true)
        .map_err(|e| format!("{LOG_PREFIX} ソケットを非ブロッキングにできません: {e}"))?;

    eprintln!(
        "{LOG_PREFIX} 発行窓口を起動します: http://{address} \
         (issuer={}, audience={}, token_ttl_hours={})",
        config.issuer, config.audience, config.token_ttl_hours
    );
    eprintln!("{LOG_PREFIX}   データフォルダ: {:?}", config.data_dir);
    eprintln!("{LOG_PREFIX}   参加者ファイル: {:?}", store.path());
    eprintln!("{LOG_PREFIX}   JWKS: {jwks_path:?} (alg={}, kid={})",
        issuer_key.algorithm().as_str(),
        issuer_key.kid()
    );
    eprintln!(
        "{LOG_PREFIX}   Lore 側は [server.auth] jwt_issuer / jwt_audience と \
         [server.auth.jwk] endpoint = \"file:///{}\" を設定してください",
        jwks_path.display().to_string().replace('\\', "/")
    );

    let state = Arc::new(AuthState {
        config: config.clone(),
        store,
        issuer_key,
        challenges: ChallengeTable::new(),
        jwks_body,
    });

    spawn_server_thread(listener, state, &config)?;
    Ok(true)
}

/// 窓口の HTTP サーバを、専用スレッド＋専用 tokio ランタイムで走らせる。
///
/// ランタイムを分ける理由:
///   `server_main()` は自前でランタイムを作って呼び出しスレッドを占有する。
///   同じランタイムに相乗りする手段が無いので、こちらも独立させる。
///   窓口の負荷は「数人が数時間に 1 回ログインする」程度なので、
///   現在のスレッドだけで回す最小構成（`new_current_thread`）で足りる。
fn spawn_server_thread(
    listener: TcpListener,
    state: Arc<AuthState>,
    config: &SeedAuthConfig,
) -> Result<(), String> {
    let address = config.listen_address();

    std::thread::Builder::new()
        .name("seed-auth-http".to_string())
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

                let router = http::build_router(state);
                // ConnectInfo を使うため（bootstrap のループバック判定）、
                // 接続元アドレスを渡す make_service を使う。
                let service =
                    router.into_make_service_with_connect_info::<std::net::SocketAddr>();

                if let Err(e) = axum::serve(listener, service).await {
                    eprintln!("{LOG_PREFIX} 発行窓口が停止しました: {e}");
                }
            });
        })
        .map_err(|e| format!("{LOG_PREFIX} {address} の待ち受けスレッドを作成できません: {e}"))?;

    Ok(())
}
