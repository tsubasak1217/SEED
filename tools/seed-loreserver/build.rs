// =============================================================================
// build.rs : 権限サービスの gRPC サーバ側スタブを生成する
// =============================================================================
// Lore v0.9.0 のサーバは `[environment.endpoint] auth_url` へ
//   epic_urc.UrcAuthApi/CheckUserPermission        （リポジトリを引くとき）
//   epic_urc.UrcAuthApi/LookupUserPermissions      （リポジトリ一覧）
//   ucs.auth.RebacApi/CreateResource / DeleteResource（リポジトリ作成・削除）
// を問い合わせる。SEED はこれらに**応答する側**を自前で持つ必要があるが、
// upstream の `lore-proto` が生成しているのは**クライアント側スタブだけ**
// （`lore-proto/src/grpc/epic_urc.rs` と `ucs.auth.rs` に `*_server` が無い）。
// そこで proto/ に置いたワイヤ契約から、サーバ側スタブをここで生成する。
//
// 【protoc について】
// システムに protoc を入れさせないため、`protoc-bin-vendored` が同梱する
// 実行ファイルを使う。PATH の protoc には依存しない
// （利用者の PC の状態でビルドの成否が変わらないようにするため）。
//
// 【出力先】
// 既定の OUT_DIR。生成物は `src/auth/permission/proto.rs` から
// `include!` して取り込む（リポジトリへは生成物をコミットしない）。
// =============================================================================

use std::io::Result;

/// proto ファイルを置いているフォルダ（このファイルからの相対）。
const PROTO_DIR: &str = "proto";

/// 生成対象の proto ファイル。
const PROTO_FILES: [&str; 2] = [
    "proto/urc_auth_api.proto",
    "proto/rebac_api.proto",
];

/// protoc へ渡す引数。
///
/// `optional`（proto3 optional）を使っているため、古い protoc では
/// この明示が要る。新しい protoc では単に無視される。
const PROTOC_ARG_PROTO3_OPTIONAL: &str = "--experimental_allow_proto3_optional";

fn main() -> Result<()> {
    // proto を編集したときだけ再生成する。
    for file in PROTO_FILES {
        println!("cargo:rerun-if-changed={file}");
    }
    println!("cargo:rerun-if-changed=build.rs");

    // 同梱の protoc を使う。見つからなければビルドを失敗させる
    // （黙って古い生成物やシステムの protoc へ落ちると、
    //   ワイヤ契約の食い違いに気づけないため）。
    let protoc = protoc_bin_vendored::protoc_bin_path().map_err(|e| {
        std::io::Error::other(format!(
            "同梱の protoc を取り出せません（protoc-bin-vendored）: {e}"
        ))
    })?;
    // SAFETY: build.rs は単一スレッドで走るため、環境変数の設定は安全。
    unsafe {
        std::env::set_var("PROTOC", &protoc);
    }

    // サーバ側スタブだけを作る。クライアント側は lore-proto が持っているので要らない。
    tonic_prost_build::configure()
        .build_client(false)
        .build_server(true)
        .protoc_arg(PROTOC_ARG_PROTO3_OPTIONAL)
        .compile_protos(&PROTO_FILES, &[PROTO_DIR])?;

    Ok(())
}
