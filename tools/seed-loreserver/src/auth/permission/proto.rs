// =============================================================================
// 権限サービス : 生成された gRPC スタブの取り込み
// =============================================================================
// `build.rs` が `proto/*.proto` から生成したサーバ側スタブを、
// パッケージ名に対応した Rust モジュールとして取り込むだけのファイル。
//
// 生成物は `OUT_DIR` にあり、リポジトリへはコミットしない
// （proto を直せば必ず作り直されるようにするため）。
//
// **ここには実装を書かないこと。** サービスの中身は
//   urc_auth.rs … epic_urc.UrcAuthApi
//   rebac.rs    … ucs.auth.RebacApi
// にある。
// =============================================================================

/// `package epic_urc;`（`proto/urc_auth_api.proto`）から生成された型。
///
/// 中には `urc_auth_api_server::{UrcAuthApi, UrcAuthApiServer}` と
/// 各メッセージ型が入る。
#[rustfmt::skip]
#[allow(clippy::doc_markdown, clippy::derive_partial_eq_without_eq)]
pub mod epic_urc {
    include!(concat!(env!("OUT_DIR"), "/epic_urc.rs"));
}

/// `package ucs.auth;`（`proto/rebac_api.proto`）から生成された型。
///
/// 中には `rebac_api_server::{RebacApi, RebacApiServer}` と
/// 各メッセージ型が入る。
#[rustfmt::skip]
#[allow(clippy::doc_markdown, clippy::derive_partial_eq_without_eq)]
pub mod ucs_auth {
    include!(concat!(env!("OUT_DIR"), "/ucs.auth.rs"));
}
