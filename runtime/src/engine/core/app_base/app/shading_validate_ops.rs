// ============================================================
//  shading_validate_ops.rs — シェーディングアセット WGSL のインメモリ検証（IPC 応答）
//
//  【含む処理】
//  - handle_validate_wgsl: エディタから届いた未保存の WGSL ソースを検証し、
//                          診断配列を `WGSL_DIAG:` レスポンスで返す
//
//  【責務の切り分け】
//  検証そのもの（契約照合・ディスパッチ生成・naga 検証・行番号の写像）は
//  renderer/shading_asset.rs（L3-a）と renderer/water/shading_asset.rs（Phase W8）が持つ。
//  ここは「IPC の受け口として呼び、**どちらの契約で検証するかを選び**、
//  ワイヤ形式へ整形して返す」だけを担う。
//
//  【契約の選び方（ワイヤを増やさない理由）】
//  エディタは「今開いている .wgsl がどちらの契約のものか」を知らない（拡張子は同じ）。
//  そこで**ソース自身の宣言**で判別する:
//    ・`// @water_shading_contract N` があれば水面シェーディング契約（Phase W8）
//    ・無ければ L3-a のシェーディング契約（従来どおり）
//  アセットが契約バージョンを先頭コメントで宣言するのは両契約に共通の規約なので、
//  IPC コマンドに種別パラメータを足すより宣言 1 行を正典にする方が破綻しにくい
//  （ファイルを移動・改名しても、内容が正しければ常に正しい契約で検証される）。
// ============================================================

use crate::engine::core::renderer::shading_asset::validate_asset_source;
use crate::engine::core::renderer::water::shading_asset as water_shading_asset;

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
    /// 最初に失敗した変種のエラーのみが入る（各 `validate_asset_source` の契約）。
    /// GPU デバイスを使わないため、Play 中・非 Play を問わずいつでも処理できる。
    pub(super) fn handle_validate_wgsl(&mut self, request_id: u64, source: &str) {
        let Some(ipc) = &self.ipc else { return };
        // 水面シェーディング契約の宣言があるかで検証コンテキストを切り替える。
        let diagnostics = if water_shading_asset::parse_contract_version(source).is_some() {
            water_shading_asset::validate_asset_source(source)
        } else {
            validate_asset_source(source)
        };
        let json = serde_json::to_string(&diagnostics)
            .unwrap_or_else(|_| EMPTY_DIAG_ARRAY.to_string());
        ipc.send(&format!("{WGSL_DIAG_PREFIX}{request_id},{json}"));
    }
}
