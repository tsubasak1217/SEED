// ============================================================
//  shading_validate_ops.rs — シェーディングアセット WGSL のインメモリ検証（IPC 応答）
//
//  【含む処理】
//  - handle_validate_wgsl: エディタから届いた未保存の WGSL ソースを検証し、
//                          診断配列を `WGSL_DIAG:` レスポンスで返す
//
//  【責務の切り分け】
//  検証そのもの（契約照合・ディスパッチ生成・naga 検証・行番号の写像）は
//  renderer/shading_asset.rs が持つ。ここは「IPC の受け口として呼び、
//  ワイヤ形式へ整形して返す」だけを担う。
// ============================================================

use crate::engine::core::renderer::shading_asset::validate_asset_source;

use super::App;

/// `WGSL_DIAG:` レスポンスのプレフィクス（エディタとのワイヤ契約）。
const WGSL_DIAG_PREFIX: &str = "WGSL_DIAG:";

/// 診断のシリアライズに失敗した場合に返す配列。
///
/// `Vec<WgslDiagnostic>` の JSON 化は現実には失敗しないが、失敗時に無応答にすると
/// エディタ側のリクエストが永久に待ちになる。空配列（＝エラー無し）を返して必ず応答する。
const EMPTY_DIAG_ARRAY: &str = "[]";

impl App {
    /// エディタから送られた WGSL ソースをファイル保存なしで検証し、診断を返す。
    ///
    /// - `request_id` : エディタ側がリクエストと応答を対応付けるための識別子。そのまま返す。
    /// - `source`     : シェーディングアセットの WGSL ソース全文（未保存バッファ）。
    ///
    /// 応答は `WGSL_DIAG:{request_id},{json_array}` の 1 行。診断は 0 件または 1 件で、
    /// 最初に失敗した変種のエラーのみが入る（`validate_asset_source` の契約）。
    /// GPU デバイスを使わないため、Play 中・非 Play を問わずいつでも処理できる。
    pub(super) fn handle_validate_wgsl(&mut self, request_id: u64, source: &str) {
        let Some(ipc) = &self.ipc else { return };
        let diagnostics = validate_asset_source(source);
        let json = serde_json::to_string(&diagnostics)
            .unwrap_or_else(|_| EMPTY_DIAG_ARRAY.to_string());
        ipc.send(&format!("{WGSL_DIAG_PREFIX}{request_id},{json}"));
    }
}
