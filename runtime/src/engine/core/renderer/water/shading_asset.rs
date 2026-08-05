// ============================================================
//  water/shading_asset.rs — 水面シェーディングアセット（.wgsl）のロード・検証・
//                           パイプライン生成（Phase W8）
//
//  ## 役割（単一責任）
//  「1 本のユーザー WGSL ファイル（水面シェーディングアセット）」を、水面描画パイプラインへ
//  差し込むまでの一連の変換を担う:
//
//      アセット読み込み → 契約バージョン照合 → `water_shade` の存在検出
//        → `water_shade_entry`（ディスパッチ）の自動生成 → 標準連結への差し込み
//        → naga による事前検証 → wgpu パイプライン生成 → 内容ハッシュでキャッシュ
//
//  GPU の描画呼び出しは持たない（呼び出し側は `water/mod.rs` の `WaterRenderer`）。
//
//  ## L3-a（`renderer/shading_asset.rs`）との関係
//  設計・流儀は L3-a のシェーディングアセットと**意図的に同一**にしてある
//  （契約マーカー・行番号写像・内容ハッシュキャッシュ・mtime ポーリング・
//   失敗記録による再ビルド抑止・LOAD_ERROR への通知）。
//  共通部分（コメント除去・行頭 fn 検出・連結と行会計・naga 検証・マーカー解析）は
//  `renderer::shading_asset` の `pub(crate)` ヘルパを**再利用**しており、
//  同じロジックが 2 箇所に写経される事態は避けてある。
//
//  違うのは 3 点だけである:
//   1. **変種が 1 つしかない**。水面パスは RT / バインドレスの分岐を持たないため、
//      パイプラインは常に 1 本（`Variant` 列挙が要らない）。
//   2. **アセットが実装する関数は `water_shade` の 1 本だけ**。シェーディングモデル ID に
//      よる振り分けは無い（水域ごとにアセットを差せるので ID 分岐が不要）。
//   3. **アセットは水域単位**。カメラ／シーン単位の L3-a と違い、`WaterVolume` の
//      `surface_shader` ごとに別パイプラインを引く（`WaterRenderer` がバケットを分割する）。
//
//  ## 設計上の不変条件
//   1. **アセット未指定の水域は本モジュールを一切通らない**。`resolve` が呼ばれず、
//      `WaterRenderer` は従来どおり組み込みの標準パイプラインで描く。
//   2. **ロードに失敗しても画面は壊れない**。失敗はエラーキューに積むだけで、
//      呼び出し側は標準パイプラインへフォールバックする。失敗した内容は記録され、
//      ファイル内容が変わるまで再試行しない（毎フレームの naga 検証を避ける）。
//   3. **不正な WGSL を `device.create_shader_module` へ渡さない**。生成した連結ソースは
//      必ず naga で parse + validate してからパイプラインを作る（デバイスロスト回避）。
//   4. **アセットが変えられるのは色だけ**。形状・高さ場（`water_height_field.wgsl`）は
//      連結の前段に固定で入り、差し替え対象は `water_shade_dispatch.wgsl` の 1 要素のみ。
//      ID パス・水面反射パス・コースティクスパスは本モジュールを通らないため、
//      ピック形状・反射・水中の光模様は常に標準のままである。
//
//  ## 依存
//   - shaders/water_shading_contract.wgsl : 契約 v1（WaterShadeInput / water_shade_default）
//   - shaders/water_shade_dispatch.wgsl   : 差し替え対象の既定ディスパッチ
//   - pipelines/water_surface.toml        : 連結順・レイアウト・エントリポイントの正典
// ============================================================

use std::cell::RefCell;
use std::collections::HashMap;
use std::sync::Arc;
use std::time::Instant;

use crate::engine::core::renderer::pipeline_config::{PipelineConfig, RenderPipelineBuilder};
use crate::engine::core::renderer::shading_asset::{
    WgslDiagnostic, asset_line_count, concat_sources, content_hash, line_defines_fn,
    map_reported_line, parse_contract_marker, strip_comments, validate_wgsl, MappedLine,
};
use super::shade_params::{WaterShadeParamDecl, parse_params, strip_declarations};

// ============================================================
//  定数（マジックナンバーの一切を名前で持つ）
// ============================================================

/// 水面シェーディング契約のバージョン（Rust 側の写し）。
///
/// `shaders/water_shading_contract.wgsl` の `const WATER_SHADING_CONTRACT_VERSION` と
/// **必ず一致**させる。一致はユニットテスト
/// `rust_and_wgsl_water_contract_versions_match` が固定する。
pub const WATER_SHADING_CONTRACT_VERSION: u32 = 1;

/// アセットが契約バージョンを宣言するための行内マーカー（先頭コメントに書く）。
/// 例: `// @water_shading_contract 1`
///
/// L3-a の `@shading_contract` とは**別のマーカー**である（前者は '@' の次が 's'、
/// こちらは 'w' なので、片方の走査がもう片方に誤ヒットすることはない）。
const WATER_CONTRACT_MARKER: &str = "@water_shading_contract";

/// アセットが実装すべき唯一の関数名。
const WATER_SHADE_FN_NAME: &str = "water_shade";

/// 標準連結リストのうち、アセットで差し替える要素の名前。
/// 正典は `pipelines/water_surface.toml` の `shader_sources`。
const WATER_DISPATCH_SOURCE_NAME: &str = "water_shade_dispatch.wgsl";

/// 生成した `water_shade_entry` に与える擬似シェーダ名（連結リスト上の識別子）。
/// 実ファイルは存在しない。`make_resolver` がこの名前を生成ソースへ解決する。
const GENERATED_SOURCE_NAME: &str = "<generated water_shade_entry>";

/// 生成したパラメータ宣言（`var<private>` 群）に与える擬似シェーダ名（W8.2）。
///
/// **アセット本体より前**に連結する。アセットに書かれた `override` 宣言は
/// 連結前に `strip_declarations` で除去され、代わりにここが生成する
/// `var<private>` が値の置き場になる（WGSL の `override` はスカラーしか許さず、
/// `@color` のような独自属性も通らないため、素のままでは naga を通せない）。
/// `water_shade_entry` の先頭でストレージバッファの値を代入するので、
/// アセット作者は宣言した名前を `water_shade` の中でそのまま値として読める。
const GENERATED_DECLS_SOURCE_NAME: &str = "<generated water shade params>";

/// エディタへ返す診断の `variant` 識別子（ワイヤ契約）。
/// 水面パスは変種が 1 つしかないので固定値 1 個だけ。
const VARIANT_NAME_WATER: &str = "water";
/// 変種の検証以前（契約バージョンの照合）で弾かれたことを表す識別子。
const VARIANT_NAME_CONTRACT: &str = "contract";
/// パラメータ注釈（W8.2）の解析で見つかった問題を表す識別子。
/// **これが返ってもアセット自体は正常にコンパイルされている**（行が無視されるだけ）。
const VARIANT_NAME_PARAM: &str = "param";

/// 契約バージョン診断が指す行番号（1 始まり）。
/// 宣言はアセット先頭コメントに書く規約なので、指摘は常に先頭行へ寄せる。
const CONTRACT_DIAG_LINE: usize = 1;

/// インメモリ検証で使うアセット名（naga のエラー表示にしか現れないダミー）。
const IN_MEMORY_ASSET_NAME: &str = "<editor-buffer>";

/// アセットの mtime をポーリングする最短間隔 [秒]。
/// Edit モードのホットリロード専用。毎フレーム `std::fs::metadata` を叩かないための間引き。
pub const WATER_SHADING_ASSET_POLL_INTERVAL_SECS: f64 = 1.0;

/// パイプライン生成に使うカラーターゲット以外の設定は TOML が正典。
/// パイプラインラベル（デバッグ表示・キャプチャで識別するため）。
const PIPELINE_LABEL: &str = "Water Surface (shading asset)";

/// `report` の予約チャンネル名: このフレームのバケット分割の要約
/// （`WaterRenderer::prepare` が使う。アセットパスをチャンネルにする診断とは別枠）。
pub const REPORT_CHANNEL_GROUPS: &str = "<groups>";

// ============================================================
//  1. アセットソースの解析
// ============================================================

/// 水面シェーディングアセットの WGSL ソースをアセット FS から読み込む。
/// PAK / ファイルシステムの解決は `asset_fs` に委ねる（BOM 除去込み）。
pub fn load_source(path: &str) -> std::io::Result<String> {
    crate::engine::asset_fs::read_string(path)
}

/// アセット本文に `fn water_shade(...)` の**定義**があるかを検出する。
///
/// 厳密な WGSL パースは行わず、コメント除去 →「行頭 `fn` → 空白 → 関数名が完全一致 →
/// 空白* → `(`」という 2 段で判定する（L3-a と同一のヘルパを共有）。
/// `my_water_shade` / `water_shade_default` / 呼び出し側の `water_shade(x)` は
/// いずれも定義として検出されない。
pub fn detect_water_shade(asset_src: &str) -> bool {
    strip_comments(asset_src)
        .lines()
        .any(|line| line_defines_fn(line, WATER_SHADE_FN_NAME))
}

/// アセット先頭コメントの `// @water_shading_contract <N>` 宣言を読む。
///
/// - `Some(n)` : 宣言があり、`n` として解釈できた
/// - `None`    : 宣言が無い（＝ v1 とみなして警告扱いで通す）
pub fn parse_contract_version(asset_src: &str) -> Option<u32> {
    parse_contract_marker(asset_src, WATER_CONTRACT_MARKER)
}

/// アセットパスからパラメータ宣言（W8.2）だけを読み出す（GPU もキャッシュも使わない）。
///
/// インスペクタへ送る `ACTOR_COMPONENTS` の水コンポーネント JSON を組み立てるための入口。
/// **読めないパス・空パスでは空 Vec**（＝パラメータ行を 1 つも出さない）を返す。
/// 描画側の解決とは独立に毎回読み直すが、呼ばれるのはアクタ選択時と
/// アセット保存時だけなので、フレーム毎の I/O にはならない。
pub fn declarations_for_path(asset_path: &str) -> Vec<WaterShadeParamDecl> {
    if asset_path.trim().is_empty() { return Vec::new(); }
    match load_source(asset_path) {
        Ok(src) => parse_params(&src).params,
        // 読めないアセットは「宣言なし」。エラー通知は描画側の解決が行うので、
        // ここで二重に通知はしない。
        Err(_)  => Vec::new(),
    }
}

// ── スクリプト API 用の既定値キャッシュ ─────────────────────────
//
// `GetShaderParamFloat` などは「シーンに保存値が無ければアセットの既定値」を返す契約
// なので、宣言を引く必要がある。素直に毎回ファイルを読むと**毎フレーム I/O** になるため、
// パス → (mtime, 宣言) を憶えておき、mtime が変わったときだけ読み直す。
// 描画側のキャッシュ（`WaterShadingAssetCache`）はデバイスを持つ `DrawContext` の中にあり、
// スクリプトホスト（World しか持たない）からは触れないので、別に小さく持つ。
/// 「まだ一度も読んでいない」ことを表す番兵 mtime（実在し得ない値）。
const SCRIPT_DECL_CACHE_UNREAD: u64 = u64::MAX;

thread_local! {
    /// アセットパス → (最後に読んだ mtime, そのときの宣言)。
    static SCRIPT_DECL_CACHE: RefCell<HashMap<String, (u64, Vec<WaterShadeParamDecl>)>> =
        RefCell::new(HashMap::new());
}

/// アセットが宣言したパラメータの**既定値**を引く（スクリプト API 用）。
///
/// - `None` : アセットが空／読めない／その名前の宣言が無い。
/// - 同じパスを繰り返し引いても、mtime が変わらない限りファイルは読み直さない。
pub fn declaration_default(
    asset_path: &str,
    name:       &str,
) -> Option<[f32; super::shade_params::PARAM_VALUE_COMPONENTS]> {
    if asset_path.trim().is_empty() { return None; }
    let mtime = crate::engine::asset_fs::mtime(asset_path);
    SCRIPT_DECL_CACHE.with(|c| {
        let mut cache = c.borrow_mut();
        // 初回は「あり得ない mtime」で作り、直後の比較で必ず読み込ませる
        //（宣言が 0 個のアセットを毎回読み直さないための番人）。
        let entry = cache.entry(asset_path.to_string())
            .or_insert_with(|| (SCRIPT_DECL_CACHE_UNREAD, Vec::new()));
        // mtime が変わったときだけ読み直す。
        if entry.0 != mtime {
            entry.0 = mtime;
            entry.1 = declarations_for_path(asset_path);
        }
        entry.1.iter().find(|d| d.name == name).map(|d| d.default)
    })
}

/// 宣言リストの同一性を判定するための署名。
///
/// 「アセットを保存したらインスペクタの行を作り直すか」の判定にだけ使う。
/// 名前・型・既定値・ラベルのいずれが変わっても別署名になる。
fn declarations_signature(decls: &[WaterShadeParamDecl]) -> String {
    format!("{decls:?}")
}

// ============================================================
//  2. ディスパッチ生成
// ============================================================

/// パラメータ注釈（W8.2）から `var<private>` 宣言の WGSL を生成する。
///
/// アセット本体の**前**に連結される。宣言が 0 個なら空文字列（連結しても無害）。
///
/// 生成例:
/// ```wgsl
/// var<private> emission_color: vec3<f32>;
/// var<private> crack_speed: f32;
/// ```
pub fn generate_param_decls(decls: &[WaterShadeParamDecl]) -> String {
    if decls.is_empty() { return String::new(); }
    let mut s = String::new();
    s.push_str("// ── 自動生成（water/shading_asset.rs）。アセットの `override` 宣言に対応する\n");
    s.push_str("//    値の置き場です。値は water_shade_entry の先頭で代入されます ──\n");
    for d in decls {
        s.push_str(&format!("var<private> {}: {};\n", d.name, d.kind.wgsl_type()));
    }
    s
}

/// 検出結果から契約関数 `water_shade_entry` の WGSL 実装を生成する。
///
/// - `has_impl = true`  : `water_shade_nan_guard(water_shade(input))`。
///   ユーザーが書いた任意の式が返ってくるので、必ず NaN/Inf/負値のガードを通す。
/// - `has_impl = false` : `water_shade_default(input)`。アセットに実装が無い場合の
///   落とし先で、**既定連結（`water_shade_dispatch.wgsl`）と完全に同値**にする
///   （ガードも掛けない＝アセットを差す前と 1 ビットも変わらない）。
///
/// `decls` が空でなければ、本体の前に「ストレージ → `var<private>` の代入」を並べる。
/// スロット番号は**宣言の出現順**であり、`shade_params::build_block` が同じ順で値を詰める。
pub fn generate_dispatch(has_impl: bool, decls: &[WaterShadeParamDecl]) -> String {
    let mut s = String::new();
    s.push_str("// ── 自動生成（water/shading_asset.rs）。このコードは編集できません ──\n");
    s.push_str("fn water_shade_entry(input: WaterShadeInput) -> vec4<f32> {\n");
    // パラメータの取り込み（宣言順＝スロット順）。
    for (slot, d) in decls.iter().enumerate() {
        s.push_str(&format!(
            "    {} = water_shade_param(input.instance_index, {slot}u).{};\n",
            d.name, d.kind.wgsl_swizzle(),
        ));
    }
    if has_impl {
        // ユーザー実装はガードを通す。
        s.push_str(&format!("    return water_shade_nan_guard({WATER_SHADE_FN_NAME}(input));\n"));
    } else {
        // 実装が無いアセットは標準へ素通し（既定連結と同値・ガード無し）。
        s.push_str("    return water_shade_default(input);\n");
    }
    s.push_str("}\n");
    s
}

// ============================================================
//  3. 連結（標準リストの water_shade_dispatch.wgsl をアセット 2 要素で置換する）
// ============================================================

/// 水面パスの**標準**シェーダ連結リストを TOML から取得する。
///
/// **配列をここへベタ書きしない**（連結順の二重管理を避ける）。
fn standard_sources() -> Vec<String> {
    let cfg: PipelineConfig = toml::from_str(WATER_SURFACE_TOML)
        .expect("water_surface.toml のパースに失敗（TOML が壊れている）");
    cfg.shader_sources
}

/// 水面パスのパイプライン設定（連結順・レイアウト・エントリポイントの正典）。
const WATER_SURFACE_TOML: &str = include_str!("../pipelines/water_surface.toml");

/// 標準連結リストの `water_shade_dispatch.wgsl` を
/// 「生成パラメータ宣言 ＋ アセット本体 ＋ 生成ディスパッチ」で置換した名前リストを返す。
///
/// パラメータ宣言がアセット本体より**前**に来るのが要点である
/// （アセットは宣言された名前を参照するため、順序が逆だと未定義識別子になる）。
fn substitute_dispatch(
    standard:      &[String],
    asset_name:    &str,
    decls_name:    &str,
    generated_name: &str,
) -> Vec<String> {
    let mut out = Vec::with_capacity(standard.len() + 2);
    for name in standard {
        if name == WATER_DISPATCH_SOURCE_NAME {
            out.push(decls_name.to_string());
            out.push(asset_name.to_string());
            out.push(generated_name.to_string());
        } else {
            out.push(name.clone());
        }
    }
    out
}

/// シェーダ名 → ソースの解決関数を作る
/// （アセット本体・生成パラメータ宣言・生成ディスパッチ・組み込みの 4 分岐）。
fn make_resolver<'a>(
    asset_name: &'a str,
    asset_src:  &'a str,
    decls:      &'a str,
    generated:  &'a str,
) -> impl Fn(&str) -> String + 'a {
    move |n: &str| {
        if n == asset_name                       { asset_src.to_string() }
        else if n == GENERATED_DECLS_SOURCE_NAME { decls.to_string() }
        else if n == GENERATED_SOURCE_NAME       { generated.to_string() }
        else                                     { super::resolve_water_shader(n).to_string() }
    }
}

/// 連結ソースを組み立てて naga 検証する。
///
/// 返り値は `(連結ソース, シェーダ名リスト, アセット開始行)`。
/// 失敗時は `(メッセージ, 報告行, アセット開始行)`。
fn prepare_variant(
    asset_name: &str,
    asset_src:  &str,
    decls:      &str,
    generated:  &str,
) -> Result<(String, Vec<String>, usize), (String, Option<usize>, usize)> {
    let standard = standard_sources();
    // 差し替えの前提（標準リストに既定ディスパッチが含まれること）を明示的に固定する。
    // ここが崩れると「アセットが連結されないのにエラーも出ない」＝ユーザーからは
    // 「設定したのに何も変わらない」という最も分かりにくい壊れ方をする。
    assert!(
        standard.iter().any(|n| n == WATER_DISPATCH_SOURCE_NAME),
        "水面シェーディングアセットの連結リストに {WATER_DISPATCH_SOURCE_NAME} が含まれていない\
         （water_surface.toml の shader_sources を確認すること）"
    );
    let names   = substitute_dispatch(
        &standard, asset_name, GENERATED_DECLS_SOURCE_NAME, GENERATED_SOURCE_NAME);
    let resolve = make_resolver(asset_name, asset_src, decls, generated);
    let (combined, asset_start) = concat_sources(&names, asset_name, &resolve);
    // 水面パスは RT / バインドレスを使わないため追加ケイパビリティは不要。
    match validate_wgsl(&combined, naga::valid::Capabilities::empty()) {
        Ok(())           => Ok((combined, names, asset_start)),
        Err((msg, line)) => Err((msg, line, asset_start)),
    }
}

// ============================================================
//  4. 診断メッセージ（build / インメモリ検証で共有）
// ============================================================

/// 契約バージョン不一致のメッセージ本体（パス接頭辞を含まない）。
fn contract_mismatch_message(declared: u32) -> String {
    format!(
        "水面シェーディング契約のバージョン不一致（アセット宣言 {declared} / エンジン \
         {WATER_SHADING_CONTRACT_VERSION}）。`// {WATER_CONTRACT_MARKER} \
         {WATER_SHADING_CONTRACT_VERSION}` に更新し、契約の変更点に追随してください"
    )
}

/// 契約バージョン宣言が無い場合のメッセージ本体（パス接頭辞を含まない）。
fn contract_missing_message() -> String {
    format!(
        "`// {WATER_CONTRACT_MARKER} {WATER_SHADING_CONTRACT_VERSION}` の宣言が \
         ありません（v{WATER_SHADING_CONTRACT_VERSION} とみなして読み込みました）"
    )
}

/// `water_shade` の定義が無い場合のメッセージ本体。
fn missing_impl_message() -> String {
    format!(
        "`fn {WATER_SHADE_FN_NAME}(input: WaterShadeInput) -> vec4<f32>` の定義が \
         見つかりません（この水域はエンジン標準の見た目で描画されます）"
    )
}

/// 検証エラーを「`<アセットパス>:<行>: <naga のメッセージ>`」形式の人間可読文字列にする。
fn format_error(
    asset_path:       &str,
    msg:              &str,
    reported_line:    Option<usize>,
    asset_start_line: usize,
    asset_lines:      usize,
) -> String {
    match reported_line.map(|l| map_reported_line(l, asset_start_line, asset_lines)) {
        Some(MappedLine::InAsset(l))      => format!("{asset_path}:{l}: {msg}"),
        Some(MappedLine::OutsideAsset(l)) =>
            format!("{asset_path}:-: エンジン標準ライブラリ側（連結の {l} 行目）でエラー: {msg}"),
        None => format!("{asset_path}:-: {msg}"),
    }
}

/// 水面シェーディングアセットのソースを、**ファイル保存も GPU デバイスも使わずに**検証する。
///
/// エディタの未保存バッファをそのまま受け取り、`WaterShadingAssetPipelines::build` と同じ
/// 前処理（契約照合 → 実装検出 → ディスパッチ生成 → 連結 → naga 検証）を通して診断を返す。
///
/// 返り値は **0 件または 1 件**。成功時は空 Vec。
pub fn validate_asset_source(asset_src: &str) -> Vec<WgslDiagnostic> {
    // ── 1. 契約バージョンの照合 ───────────────────────────
    match parse_contract_version(asset_src) {
        Some(v) if v != WATER_SHADING_CONTRACT_VERSION => {
            return vec![WgslDiagnostic {
                message: contract_mismatch_message(v),
                line:    Some(CONTRACT_DIAG_LINE),
                variant: VARIANT_NAME_CONTRACT.to_string(),
            }];
        }
        Some(_) => {}
        None => {
            return vec![WgslDiagnostic {
                message: contract_missing_message(),
                line:    Some(CONTRACT_DIAG_LINE),
                variant: VARIANT_NAME_CONTRACT.to_string(),
            }];
        }
    }

    // ── 2. 実装検出・パラメータ宣言の解析・生成 ───────────
    //    宣言（`override` ＋ 属性）は naga が解釈できないので、連結へ渡す前に
    //    **行数を保ったまま**除去する（行番号写像がそのまま使える）。
    let has_impl  = detect_water_shade(asset_src);
    let params    = parse_params(asset_src);
    let body      = strip_declarations(asset_src);
    let decls     = generate_param_decls(&params.params);
    let generated = generate_dispatch(has_impl, &params.params);
    let lines     = asset_line_count(&body);

    // ── 3. 連結の検証 ─────────────────────────────────────
    if let Err((msg, line, start)) =
        prepare_variant(IN_MEMORY_ASSET_NAME, &body, &decls, &generated)
    {
        let mapped = line.map(|l| map_reported_line(l, start, lines));
        let (line, message) = match mapped {
            Some(MappedLine::InAsset(l)) => (Some(l), msg),
            Some(MappedLine::OutsideAsset(l)) => (
                None,
                format!("アセット外（エンジン側連結コード）で検出されました（連結の {l} 行目）: {msg}"),
            ),
            None => (None, msg),
        };
        return vec![WgslDiagnostic { message, line, variant: VARIANT_NAME_WATER.to_string() }];
    }

    // ── 4. 実装が無いのは「エラー」ではないが、無反応の切り分けのため警告を返す ──
    if !has_impl {
        return vec![WgslDiagnostic {
            message: missing_impl_message(),
            line:    Some(CONTRACT_DIAG_LINE),
            variant: VARIANT_NAME_WATER.to_string(),
        }];
    }

    // ── 5. パラメータ宣言（W8.2）の問題を診断として返す ─────
    //    連結は通る（＝壊れた宣言行は除去されている）ので、
    //    ここまで来て初めて報告できる。行番号はアセット内のものをそのまま使う。
    params.warnings.iter()
        .map(|w| WgslDiagnostic {
            message: format!("パラメータ宣言: {}", w.message),
            line:    Some(w.line),
            variant: VARIANT_NAME_PARAM.to_string(),
        })
        .collect()
}

// ============================================================
//  5. パイプライン構築
// ============================================================

/// 水面シェーディングアセットで差し替えた水面描画パイプライン。
///
/// 設定（レイアウト・ターゲットフォーマット・エントリポイント）は
/// `WaterRenderer::new` の標準パイプラインと**同一**である
/// （どちらも `pipelines/water_surface.toml` から作るので、構造的に食い違わない）。
///
/// BindGroup は `WaterRenderer` が標準パイプラインのリフレクション結果から作ったものを
/// そのまま使う。wgpu は**同一エントリ構成の BindGroupLayout を内部で共有する**ため、
/// 別々に作った 2 本のパイプラインでもレイアウト互換になる（L3-a と同じ前提）。
pub struct WaterShadingAssetPipelines {
    /// 水面描画パイプライン（アセット差し替え版）。
    pub pipeline: wgpu::RenderPipeline,
    /// アセットが `water_shade` を実装しているか（診断ログ用。描画判定には使わない）。
    pub has_impl: bool,
    /// アセットが宣言したパラメータ（W8.2）。**出現順＝GPU のスロット順**。
    ///
    /// このパイプラインで描く水域のパラメータブロックは、必ずこの順で詰める
    /// （`WaterRenderer::prepare` が `shade_params::build_block` で行う）。
    pub params: Vec<WaterShadeParamDecl>,
}

impl WaterShadingAssetPipelines {
    /// アセットソースからパイプラインを構築する。
    ///
    /// - `out_format`   : 出力先（シーン HDR）。`WaterRenderer::new` の `hdr_format` と同値。
    /// - `depth_format` : `no_depth = true` のため実際には未使用（ビルダーのシグネチャ充足）。
    ///
    /// 失敗（検証失敗）は `Err(メッセージ)`。実装が無い・契約宣言が無いといった
    /// 「動くが意図と違うかもしれない」ケースは `warnings` へ積んで成功扱いにする。
    fn build(
        device:       &wgpu::Device,
        asset_path:   &str,
        asset_src:    &str,
        out_format:   wgpu::TextureFormat,
        depth_format: wgpu::TextureFormat,
        warnings:     &mut Vec<String>,
    ) -> Result<Self, String> {
        // アセット名は naga のエラー表示にしか現れないので、アセットのパスをそのまま使う。
        let asset_name = asset_path;
        let lines      = asset_line_count(asset_src);

        // ── 契約バージョンの照合 ──────────────────────────────
        match parse_contract_version(asset_src) {
            Some(v) if v != WATER_SHADING_CONTRACT_VERSION => {
                return Err(format!("{asset_path}:-: {}", contract_mismatch_message(v)));
            }
            Some(_) => {}
            None => {
                // 宣言が無い場合は v1 とみなして通す（警告扱い）。
                warnings.push(format!("{asset_path}:-: {}", contract_missing_message()));
            }
        }

        // ── 実装検出とディスパッチ生成 ────────────────────────
        let has_impl = detect_water_shade(asset_src);
        if !has_impl {
            warnings.push(format!("{asset_path}:-: {}", missing_impl_message()));
        }

        // ── パラメータ宣言（W8.2）の解析と生成 ────────────────
        //    壊れた宣言行は除去されるだけで描画は成立する。警告として通知する。
        let params = parse_params(asset_src);
        for w in &params.warnings {
            warnings.push(format!("{asset_path}:{}: パラメータ宣言: {}", w.line, w.message));
        }
        // 宣言ブロックを除去した本体（naga・wgpu へ渡すのは必ずこちら）。
        let body      = strip_declarations(asset_src);
        let decls     = generate_param_decls(&params.params);
        let generated = generate_dispatch(has_impl, &params.params);

        // ── 連結の検証（不正な WGSL をデバイスへ渡さない）──────
        let (_src, names, _start) = prepare_variant(asset_name, &body, &decls, &generated)
            .map_err(|(msg, line, start)| format_error(asset_path, &msg, line, start, lines))?;

        // ── パイプライン生成 ──────────────────────────────────
        let resolve = make_resolver(asset_name, &body, &decls, &generated);
        let (pipeline, _bgls) =
            RenderPipelineBuilder::new(device, WATER_SURFACE_TOML, out_format, depth_format)
                .with_label(PIPELINE_LABEL)
                // 差し替え済みリストを渡すため、TOML の shader_sources を上書きする。
                .with_shader_sources(names)
                .build_owned(resolve);

        Ok(Self { pipeline, has_impl, params: params.params })
    }
}

// ============================================================
//  6. キャッシュ（内容ハッシュ → パイプライン）
// ============================================================

/// アセットパスごとの追跡状態（ホットリロードの間引きと再読込判定に使う）。
struct PathState {
    /// 最後に mtime を確認した時刻（ポーリング間引き用）。
    last_poll: Instant,
    /// 最後に確認した mtime（秒。`asset_fs::mtime` の値）。
    mtime:     u64,
    /// 最後に読み込んだ内容のハッシュ。`None` = 読み込み自体に失敗した。
    hash:      Option<u64>,
}

/// 水面シェーディングアセットのビルド結果キャッシュ。
///
/// `DrawContext` は `&self` 共有で使われるため、フレーム内から更新できるよう
/// 内部可変（`RefCell`）で持つ（L3-a の `ShadingAssetCache` と同一の流儀）。
///
/// ## キー設計
/// - `built` のキーは**アセット内容のハッシュ**。同一内容なら別パスでもパイプラインを共有する
///   （マグマ池を 10 個置いても実際のパイプラインは 1 本）。
///   値が `Err(メッセージ)` のエントリは「この内容ではビルドに失敗した」ことの記録で、
///   **内容が変わるまで再ビルドしない**（毎フレームの naga 検証を避ける）。
/// - `paths` はパス → (最終ポーリング時刻, mtime, 内容ハッシュ)。mtime 不変なら
///   ファイル読み込み自体をスキップする。
pub struct WaterShadingAssetCache {
    /// 内容ハッシュ → ビルド結果（`Err` = 失敗済み・そのときのメッセージ）。
    built:       RefCell<HashMap<u64, Result<Arc<WaterShadingAssetPipelines>, String>>>,
    /// アセットパス → 追跡状態。
    paths:       RefCell<HashMap<String, PathState>>,
    /// App が毎フレーム drain して IPC（`LOAD_ERROR:`）へ流すエラー／警告キュー。
    errors:      RefCell<Vec<String>>,
    /// 直近に積んだメッセージ（同じものを連続で積まないための番人）。
    last_error:  RefCell<Option<String>>,
    /// 直近に出力した `[WATER SHADING]` 診断行（アセットパス → 前回の行）。
    /// 毎フレーム呼ばれるので「前回と同じ行なら出力しない」で間引く。
    last_report: RefCell<HashMap<String, String>>,
    /// アセットパス → 前回のパラメータ宣言署名（W8.2。ホットリロード連動用）。
    decl_sigs:   RefCell<HashMap<String, String>>,
    /// ホットリロードで**宣言が変わった**アセットパスのキュー（W8.2）。
    ///
    /// App がフレーム末に drain し、選択中アクタの `ACTOR_COMPONENTS` を再送して
    /// インスペクタの行を作り直させる（値の行が増減・改名しても UI が追随する）。
    decls_changed: RefCell<Vec<String>>,
}

impl Default for WaterShadingAssetCache {
    fn default() -> Self { Self::new() }
}

impl WaterShadingAssetCache {
    /// 空のキャッシュを生成する。
    pub fn new() -> Self {
        Self {
            built:       RefCell::new(HashMap::new()),
            paths:       RefCell::new(HashMap::new()),
            errors:      RefCell::new(Vec::new()),
            last_error:  RefCell::new(None),
            last_report: RefCell::new(HashMap::new()),
            decl_sigs:     RefCell::new(HashMap::new()),
            decls_changed: RefCell::new(Vec::new()),
        }
    }

    /// 宣言が変わったアセットパスを取り出して空にする（W8.2）。
    /// 呼び出し側（App）は、選択中アクタがそのアセットを使っていれば
    /// `ACTOR_COMPONENTS` を再送する。
    pub fn take_decls_changed(&self) -> Vec<String> {
        std::mem::take(&mut *self.decls_changed.borrow_mut())
    }

    /// 読み込んだソースの宣言署名を記録し、**前回から変わっていれば**通知キューへ積む。
    ///
    /// 初回（署名の記録が無い）は積まない。初回のインスペクタ表示は
    /// アクタ選択時に `declarations_for_path` が直接ファイルを読んで作るため、
    /// 既に正しい行が出ているからである。
    fn note_declarations(&self, asset_path: &str, asset_src: &str) {
        let sig = declarations_signature(&parse_params(asset_src).params);
        let prev = self.decl_sigs.borrow_mut().insert(asset_path.to_string(), sig.clone());
        if let Some(prev) = prev {
            if prev != sig {
                self.decls_changed.borrow_mut().push(asset_path.to_string());
            }
        }
    }

    /// `[WATER SHADING]` 診断行を出力する（前回と同一内容なら出力しない）。
    ///
    /// エラー通知（`errors` キュー）とは独立で、**成功時も必ず出る**のがこの行の役割
    /// （＝「アセットを指定したのに見た目が変わらない」の切り分けに使える）。
    pub fn report(&self, channel: &str, line: String) {
        if self.last_report.borrow().get(channel) == Some(&line) { return }
        eprintln!("{line}");
        self.last_report.borrow_mut().insert(channel.to_string(), line);
    }

    /// エラー／警告を 1 件積む（直前と同一内容なら積まない）。
    fn push_message(&self, msg: String) {
        if self.last_error.borrow().as_deref() == Some(msg.as_str()) { return }
        *self.last_error.borrow_mut() = Some(msg.clone());
        self.errors.borrow_mut().push(msg);
    }

    /// 溜まったエラー／警告を取り出して空にする（App が IPC へ流す）。
    pub fn drain_errors(&self) -> Vec<String> {
        std::mem::take(&mut *self.errors.borrow_mut())
    }

    /// 全キャッシュを破棄する（シーン切替時の保険）。
    #[allow(dead_code)]
    pub fn clear(&self) {
        self.built.borrow_mut().clear();
        self.paths.borrow_mut().clear();
    }
}

// ============================================================
//  7. 解決（描画準備から呼ばれる入口）
// ============================================================

/// 水面シェーディングアセットのパスからパイプラインを解決する。
///
/// `WaterRenderer::prepare` から毎フレーム呼ばれる想定。実際の I/O・ビルドは以下の条件でのみ走る:
///   - 初回（そのパスを見たことがない）
///   - `allow_hot_reload` かつ前回ポーリングから
///     `WATER_SHADING_ASSET_POLL_INTERVAL_SECS` 経過し、かつ mtime が変化している
///
/// 返り値 `None` は「アセットを使えない」の意味で、呼び出し側は組み込み標準パイプラインへ
/// フォールバックすること。
///
/// - `allow_hot_reload` : Edit モードは常に true。Play 中はエディタ設定
///   `play_shader_hot_reload`（既定 ON）に従う（L3-a と同一の設定を共有する）。
pub fn resolve(
    device:           &wgpu::Device,
    cache:            &WaterShadingAssetCache,
    asset_path:       &str,
    out_format:       wgpu::TextureFormat,
    depth_format:     wgpu::TextureFormat,
    allow_hot_reload: bool,
) -> Option<Arc<WaterShadingAssetPipelines>> {
    let result = resolve_inner(device, cache, asset_path, out_format, depth_format, allow_hot_reload);
    // ── 診断ログ（常時 ON・変化時のみ）───────────────────────
    let line = match &result {
        Some(p) => format!(
            "[WATER SHADING] loaded path={asset_path} water_shade={}",
            if p.has_impl { "あり" } else { "なし（標準へ素通し）" },
        ),
        None => format!(
            "[WATER SHADING] failed path={asset_path} \
             （組み込み標準の水へフォールバック。詳細は [SEED water_shading_asset] 行）"
        ),
    };
    cache.report(asset_path, line);
    result
}

/// `resolve` の本体（診断ログを挟まない素の解決処理）。
fn resolve_inner(
    device:           &wgpu::Device,
    cache:            &WaterShadingAssetCache,
    asset_path:       &str,
    out_format:       wgpu::TextureFormat,
    depth_format:     wgpu::TextureFormat,
    allow_hot_reload: bool,
) -> Option<Arc<WaterShadingAssetPipelines>> {
    let now = Instant::now();

    // ── 1. 既知パスなら、読み込みが必要かを判定する ────────────
    let known: Option<(bool, u64, Option<u64>)> = cache.paths.borrow().get(asset_path)
        .map(|st| {
            let due = now.duration_since(st.last_poll).as_secs_f64()
                >= WATER_SHADING_ASSET_POLL_INTERVAL_SECS;
            (due, st.mtime, st.hash)
        });

    let needs_read = match known {
        // 初回は必ず読む。
        None => true,
        Some((due, prev_mtime, _)) => {
            if !allow_hot_reload || !due {
                // ホットリロード禁止（Play 中で設定 OFF）／ポーリング間隔内なら読み直さない。
                false
            } else {
                // ポーリング時刻を更新したうえで mtime の変化を見る。
                let m = crate::engine::asset_fs::mtime(asset_path);
                if let Some(st) = cache.paths.borrow_mut().get_mut(asset_path) {
                    st.last_poll = now;
                }
                m != prev_mtime
            }
        }
    };

    if !needs_read {
        // キャッシュ済みの内容ハッシュからビルド結果を引く（失敗記録なら黙って None）。
        let hash = known.and_then(|(_, _, h)| h)?;
        return cache.built.borrow().get(&hash).and_then(|r| r.as_ref().ok()).cloned();
    }

    // ── 2. 読み込み ───────────────────────────────────────────
    let mtime = crate::engine::asset_fs::mtime(asset_path);
    let src = match load_source(asset_path) {
        Ok(s) => s,
        Err(e) => {
            cache.push_message(
                format!("{asset_path}:-: 水面シェーディングアセットを読めません: {e}"));
            // 読み込み失敗も記録して、mtime が変わるまで再試行しない。
            cache.paths.borrow_mut().insert(
                asset_path.to_string(),
                PathState { last_poll: now, mtime, hash: None },
            );
            return None;
        }
    };
    // 宣言（W8.2）が前回から変わっていれば、インスペクタの行を作り直させる。
    cache.note_declarations(asset_path, &src);
    let hash = content_hash(&src);
    cache.paths.borrow_mut().insert(
        asset_path.to_string(),
        PathState { last_poll: now, mtime, hash: Some(hash) },
    );

    // ── 3. 同一内容が既にビルド済み（成功・失敗とも）ならそれを使う ──
    // ここに来るのは「実際にファイルを読み直した」直後だけなので、失敗記録に当たった場合は
    // エラーを**もう一度**通知してよい（毎フレームの再通知にはならない）。
    let cached = cache.built.borrow().get(&hash).cloned();
    if let Some(entry) = cached {
        return match entry {
            Ok(p)    => Some(p),
            Err(msg) => { cache.push_message(msg); None }
        };
    }

    // ── 4. ビルド ─────────────────────────────────────────────
    let mut warnings = Vec::new();
    let result = WaterShadingAssetPipelines::build(
        device, asset_path, &src, out_format, depth_format, &mut warnings,
    );
    for w in warnings { cache.push_message(w); }

    let entry = match result {
        Ok(p) => Ok(Arc::new(p)),
        Err(msg) => {
            cache.push_message(msg.clone());
            Err(msg)
        }
    };
    cache.built.borrow_mut().insert(hash, entry.clone());

    // ── 5. 到達不能になったビルド結果を破棄する（ホットリロードの蓄積対策）──
    // キーが「内容ハッシュ」なので、Edit 中にアセットを保存するたびに新しいハッシュの
    // エントリが増える。どのパスからも参照されなくなったハッシュはもう二度と引かれないので、
    // ここで捨てる（残るのは現在解決済みのアセットパスの数ぶんだけ）。
    {
        let live: std::collections::HashSet<u64> =
            cache.paths.borrow().values().filter_map(|st| st.hash).collect();
        cache.built.borrow_mut().retain(|h, _| live.contains(h));
    }

    entry.ok()
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の最小アセット（docs のスニペットと同内容）。
    /// 標準の水を作ってから緑へ寄せるだけ＝「標準の部分流用」の最小形。
    const TINT_ASSET: &str = r#"// @water_shading_contract 1
/// 標準の水を作ってから、深いところだけ緑へ寄せる。
const TINT_COLOR: vec3<f32> = vec3<f32>(0.2, 0.9, 0.35);

fn water_shade(input: WaterShadeInput) -> vec4<f32> {
    let base  = water_shade_default(input);
    let depth = water_shade_saturate(input.thickness / max(input.absorption_distance, 0.01));
    return vec4<f32>(mix(base.rgb, base.rgb * TINT_COLOR, depth), base.a);
}
"#;

    // ── 1. 契約バージョンの一致 ────────────────────────────

    /// Rust 側の `WATER_SHADING_CONTRACT_VERSION` と water_shading_contract.wgsl の
    /// `const WATER_SHADING_CONTRACT_VERSION` が一致すること。
    #[test]
    fn rust_and_wgsl_water_contract_versions_match() {
        let src = include_str!("../shaders/water_shading_contract.wgsl");
        let line = src.lines().map(str::trim)
            .find(|l| l.starts_with("const WATER_SHADING_CONTRACT_VERSION"))
            .expect("water_shading_contract.wgsl に const WATER_SHADING_CONTRACT_VERSION が無い");
        let rhs = line.split('=').nth(1).expect("= の右辺がありません");
        let num: String = rhs.trim().chars().take_while(|c| c.is_ascii_digit()).collect();
        let wgsl_version: u32 = num.parse().expect("u32 として解釈できません");
        assert_eq!(wgsl_version, WATER_SHADING_CONTRACT_VERSION,
            "Rust と WGSL の水面シェーディング契約バージョンが食い違っている");
    }

    /// 契約マーカーが L3-a のマーカーと衝突しないこと
    /// （`@water_shading_contract` の中に `@shading_contract` が現れない）。
    #[test]
    fn water_marker_does_not_collide_with_shading_marker() {
        assert!(!WATER_CONTRACT_MARKER.contains("@shading_contract"),
            "水の契約マーカーが L3-a のマーカーを部分文字列に含むと相互誤検出する");
        assert_eq!(parse_contract_version("// @water_shading_contract 1\n"), Some(1));
        assert_eq!(
            crate::engine::core::renderer::shading_asset::parse_contract_version(
                "// @water_shading_contract 1\n"),
            None,
            "L3-a の走査が水の宣言を拾ってはならない",
        );
    }

    // ── 2. 実装の検出 ──────────────────────────────────────

    /// `water_shade` の定義を検出すること。
    #[test]
    fn detects_water_shade_definition() {
        assert!(detect_water_shade(TINT_ASSET));
        assert!(detect_water_shade(
            "  fn water_shade (input: WaterShadeInput) -> vec4<f32> { return vec4<f32>(0.0); }"));
    }

    /// コメントアウト・部分一致・呼び出しは検出しないこと。
    #[test]
    fn does_not_detect_non_definitions() {
        assert!(!detect_water_shade(
            "// fn water_shade(input: WaterShadeInput) -> vec4<f32> { return vec4<f32>(0.0); }"));
        assert!(!detect_water_shade(
            "/*\nfn water_shade(input: WaterShadeInput) -> vec4<f32> { }\n*/"));
        assert!(!detect_water_shade(
            "fn my_water_shade(input: WaterShadeInput) -> vec4<f32> { return vec4<f32>(0.0); }"));
        assert!(!detect_water_shade(
            "fn f(i: WaterShadeInput) -> vec4<f32> { return water_shade(i); }"));
        // 契約側の標準実装名を「ユーザー実装」と誤認しないこと（連結時に必ず存在する名前）。
        assert!(!detect_water_shade(
            "fn water_shade_default2(i: WaterShadeInput) -> vec4<f32> { return vec4<f32>(0.0); }"));
    }

    // ── 3. 生成ディスパッチ ────────────────────────────────

    /// 実装ありならガード付きでユーザー実装を呼ぶこと。
    #[test]
    fn dispatch_calls_user_impl_with_guard() {
        let d = generate_dispatch(true, &[]);
        assert!(d.contains("return water_shade_nan_guard(water_shade(input));"), "{d}");
        assert!(!d.contains("water_shade_default"), "{d}");
    }

    /// 実装なしなら標準へ素通し（ガードも掛けない＝既定連結と同値）。
    #[test]
    fn dispatch_falls_back_to_default_without_guard() {
        let d = generate_dispatch(false, &[]);
        assert!(d.contains("return water_shade_default(input);"), "{d}");
        assert!(!d.contains("water_shade_nan_guard"),
            "既定経路にガードを掛けてはならない（アセット導入前との同値性）: {d}");
    }

    /// 既定ディスパッチの差し替えが「water_shade_dispatch.wgsl の位置」で行われること。
    #[test]
    fn dispatch_element_is_substituted_in_place() {
        let std_list = standard_sources();
        let idx = std_list.iter().position(|n| n == WATER_DISPATCH_SOURCE_NAME)
            .expect("標準リストに water_shade_dispatch.wgsl が無い");
        let subst = substitute_dispatch(&std_list, "asset.wgsl", "<decls>", "<gen>");
        assert_eq!(subst.len(), std_list.len() + 2);
        assert_eq!(subst[idx], "<decls>", "パラメータ宣言はアセット本体より前に来ること");
        assert_eq!(subst[idx + 1], "asset.wgsl");
        assert_eq!(subst[idx + 2], "<gen>");
        assert!(!subst.iter().any(|n| n == WATER_DISPATCH_SOURCE_NAME),
            "既定ディスパッチは連結から外れること（water_shade_entry の二重定義になる）");
    }

    // ── 4. naga 検証 ───────────────────────────────────────

    /// サンプルアセット＋生成ディスパッチを差し込んだ連結が naga を通ること。
    #[test]
    fn tint_asset_passes_naga_validation() {
        let generated = generate_dispatch(detect_water_shade(TINT_ASSET), &[]);
        match prepare_variant("tint.wgsl", TINT_ASSET, "", &generated) {
            Ok((_src, _names, start)) => {
                assert!(start > 1, "アセットは共有モジュール・契約の後に来る");
            }
            Err((msg, line, start)) => panic!(
                "サンプルアセットの検証に失敗: {}",
                format_error("tint.wgsl", &msg, line, start, asset_line_count(TINT_ASSET)),
            ),
        }
    }

    /// リポジトリ同梱のサンプルアセット 2 点が naga 検証を通ること。
    ///
    /// アセットは `runtime/assets/shaders/` の実ファイルで、エディタから
    /// そのまま差せる状態のものである。ここが落ちる＝ユーザーが差した瞬間に
    /// LOAD_ERROR が出る、という意味なので必ず固定しておく。
    #[test]
    fn bundled_sample_assets_pass_naga_validation() {
        const SAMPLES: [(&str, &str); 2] = [
            ("magma.wgsl",  include_str!("../../../../../assets/shaders/magma.wgsl")),
            ("poison.wgsl", include_str!("../../../../../assets/shaders/poison.wgsl")),
        ];
        for (name, src) in SAMPLES {
            assert_eq!(parse_contract_version(src), Some(WATER_SHADING_CONTRACT_VERSION),
                "{name}: 契約バージョン宣言が無い／版が違う");
            assert!(detect_water_shade(src), "{name}: water_shade の定義が無い");
            let params    = parse_params(src);
            let body      = strip_declarations(src);
            let decls     = generate_param_decls(&params.params);
            let generated = generate_dispatch(true, &params.params);
            if let Err((msg, line, start)) = prepare_variant(name, &body, &decls, &generated) {
                panic!("{name} の検証に失敗: {}",
                    format_error(name, &msg, line, start, asset_line_count(&body)));
            }
            // インメモリ検証（エディタが叩く経路）でも診断ゼロであること。
            assert!(validate_asset_source(src).is_empty(),
                "{name}: エディタ検証で診断が出た: {:?}", validate_asset_source(src));
        }
    }

    /// 同梱サンプルがパラメータ注釈を宣言していること（W8.2 のショーケース）。
    ///
    /// ここが空になる＝「アセットを差してもインスペクタにパラメータ行が出ない」なので、
    /// 機能の入口が見えなくなる退行として検出する。
    #[test]
    fn bundled_samples_declare_inspector_params() {
        const SAMPLES: [(&str, &str); 2] = [
            ("magma.wgsl",  include_str!("../../../../../assets/shaders/magma.wgsl")),
            ("poison.wgsl", include_str!("../../../../../assets/shaders/poison.wgsl")),
        ];
        for (name, src) in SAMPLES {
            let set = parse_params(src);
            assert!(set.warnings.is_empty(), "{name}: 注釈の警告が出ている: {:?}", set.warnings);
            assert!(set.params.len() >= 3,
                "{name}: パラメータ宣言が 3 個未満（{} 個）", set.params.len());
            // 宣言した名前がアセット本体で実際に参照されていること
            //（宣言だけして使っていないと、インスペクタで動かしても何も起きない）。
            for p in &set.params {
                let uses = src.matches(p.name.as_str()).count();
                assert!(uses >= 2,
                    "{name}: パラメータ `{}` が宣言のみで使われていない", p.name);
            }
        }
    }

    // ── 5. インメモリ検証（エディタの未保存バッファ） ───────

    /// 正常なアセットは診断ゼロで通ること。
    #[test]
    fn validate_accepts_valid_asset() {
        let diags = validate_asset_source(TINT_ASSET);
        assert!(diags.is_empty(), "正常なアセットで診断が出た: {diags:?}");
    }

    /// 構文エラーはアセット内の**その行**を指す診断 1 件になること。
    #[test]
    fn validate_reports_asset_line_for_syntax_error() {
        // 1 行目に契約宣言、4 行目に構文エラー（`retur` は未知の識別子）。
        let broken = "// @water_shading_contract 1\n\
                      fn water_shade(input: WaterShadeInput) -> vec4<f32> {\n\
                      \x20   let c = water_shade_default(input);\n\
                      \x20   retur c;\n\
                      }\n";
        let diags = validate_asset_source(broken);
        assert_eq!(diags.len(), 1, "診断は 1 件だけ返る: {diags:?}");
        assert_eq!(diags[0].line, Some(4), "アセット内 4 行目を指すこと: {:?}", diags[0]);
        assert_eq!(diags[0].variant, VARIANT_NAME_WATER);
    }

    /// 契約バージョン宣言が無いアセットは variant="contract" の診断 1 件で終わること。
    #[test]
    fn validate_reports_missing_contract() {
        let no_contract =
            "fn water_shade(input: WaterShadeInput) -> vec4<f32> { return vec4<f32>(0.0); }\n";
        let diags = validate_asset_source(no_contract);
        assert_eq!(diags.len(), 1);
        assert_eq!(diags[0].variant, VARIANT_NAME_CONTRACT);
        assert_eq!(diags[0].line, Some(CONTRACT_DIAG_LINE));
        assert!(diags[0].message.contains(WATER_CONTRACT_MARKER),
            "宣言の書き方を示すこと: {}", diags[0].message);
    }

    /// 契約バージョンが食い違うアセットも variant="contract" で弾かれること。
    #[test]
    fn validate_reports_contract_version_mismatch() {
        let future = format!(
            "// {WATER_CONTRACT_MARKER} {}\nfn water_shade(i: WaterShadeInput) -> vec4<f32> \
             {{ return vec4<f32>(0.0); }}\n",
            WATER_SHADING_CONTRACT_VERSION + 1);
        let diags = validate_asset_source(&future);
        assert_eq!(diags.len(), 1);
        assert_eq!(diags[0].variant, VARIANT_NAME_CONTRACT);
        assert!(diags[0].message.contains("不一致"), "{}", diags[0].message);
    }

    // ── 6. パラメータ注釈（Phase W8.2）───────────────────────

    /// パラメータ宣言つきのテストアセット。宣言した名前を `water_shade` から直接読む
    /// （＝この機能の中心的な使い勝手）。
    const PARAM_ASSET: &str = r#"// @water_shading_contract 1
@color
override emission_color: vec3<f32> = vec3(1.0, 0.4, 0.1);  // 発光色
@range(0.0, 10.0) @reset
override crack_speed: f32 = 1.5;                           // 亀裂の流速
override glow_boost: f32 = 2.0;                            // 発光の強さ

fn water_shade(input: WaterShadeInput) -> vec4<f32> {
    let n = water_shade_fbm(input.world_pos.xz - input.flow * input.time * crack_speed);
    return vec4<f32>(emission_color * n * glow_boost, 1.0);
}
"#;

    /// Rust の上限本数と WGSL の配列長・定数が一致すること。
    ///
    /// ここが食い違うと「宣言 8 個目だけが読めない」という、
    /// 画面を見ても原因の分からない壊れ方をする。
    #[test]
    fn rust_and_wgsl_param_slot_counts_match() {
        use super::super::shade_params::WATER_SHADE_PARAM_MAX;
        let src = include_str!("../shaders/water_shade_params.wgsl");
        // ① 定数 WATER_SHADE_PARAM_SLOTS
        let line = src.lines().map(str::trim)
            .find(|l| l.starts_with("const WATER_SHADE_PARAM_SLOTS"))
            .expect("water_shade_params.wgsl に const WATER_SHADE_PARAM_SLOTS が無い");
        let rhs = line.split('=').nth(1).expect("= の右辺がありません");
        let num: String = rhs.trim().chars().take_while(|c| c.is_ascii_digit()).collect();
        assert_eq!(num.parse::<usize>().expect("数値として読めません"), WATER_SHADE_PARAM_MAX,
            "WGSL の WATER_SHADE_PARAM_SLOTS と Rust の WATER_SHADE_PARAM_MAX が食い違っている");
        // ② 構造体の配列長
        assert!(src.contains(&format!("array<vec4<f32>, {WATER_SHADE_PARAM_MAX}>")),
            "WaterShadeParamBlock の配列長が {WATER_SHADE_PARAM_MAX} でない");
    }

    /// **標準連結（アセット未指定）にもパラメータ用の binding が宣言されていること**。
    ///
    /// BindGroupLayout はシェーダ宣言のリフレクション（`pipeline_config::reflect_bgls` が
    /// `global_variables` を走査する）で作られ、`WaterRenderer` は標準パイプライン由来の
    /// レイアウトへ binding 7 のエントリを挿す。標準側の宣言が落ちると
    /// **BindGroup の生成時に「レイアウトに無い binding」で落ちる**ため、
    /// 「使っていなくても宣言は残る」ことをここで固定する。
    #[test]
    fn standard_concat_declares_param_storage_binding() {
        let names = standard_sources();
        // アセット名に該当しないダミーを渡す（標準連結そのものを組み立てる）。
        let (src, _start) = concat_sources(&names, "<none>", |n: &str| {
            super::super::resolve_water_shader(n).to_string()
        });
        let module = naga::front::wgsl::parse_str(&src)
            .expect("標準連結が naga で解析できること");
        let found = module.global_variables.iter().any(|(_, v)| {
            v.binding.as_ref().map(|b| (b.group, b.binding)) == Some((1, 7))
        });
        assert!(found,
            "標準連結に @group(1) @binding(7)（パラメータ storage）の宣言が無い。\
             アセット側パイプラインとレイアウトが食い違って BindGroup 生成が落ちる");
    }

    /// 宣言から `var<private>` が型どおりに生成されること。
    #[test]
    fn generates_private_vars_for_declarations() {
        let params = parse_params(PARAM_ASSET);
        let decls  = generate_param_decls(&params.params);
        assert!(decls.contains("var<private> emission_color: vec3<f32>;"), "{decls}");
        assert!(decls.contains("var<private> crack_speed: f32;"), "{decls}");
        assert!(decls.contains("var<private> glow_boost: f32;"), "{decls}");
        // 宣言が無ければ 1 文字も出さない（連結しても無害）。
        assert!(generate_param_decls(&[]).is_empty());
    }

    /// ディスパッチが宣言順のスロットから値を取り込むこと（Color は xyz / スカラーは x）。
    #[test]
    fn dispatch_loads_params_in_declaration_order() {
        let params = parse_params(PARAM_ASSET);
        let d = generate_dispatch(true, &params.params);
        assert!(d.contains("emission_color = water_shade_param(input.instance_index, 0u).xyz;"), "{d}");
        assert!(d.contains("crack_speed = water_shade_param(input.instance_index, 1u).x;"), "{d}");
        assert!(d.contains("glow_boost = water_shade_param(input.instance_index, 2u).x;"), "{d}");
        // 取り込みはユーザー実装の呼び出しより前であること（順序が逆だと値が届かない）。
        let load_at = d.find("emission_color = ").expect("取り込みが無い");
        let call_at = d.find("water_shade_nan_guard").expect("呼び出しが無い");
        assert!(load_at < call_at, "パラメータの取り込みは呼び出しより前であること: {d}");
    }

    /// 宣言した名前をアセット内でそのまま参照する連結が naga 検証を通ること
    /// （この機能の中核。ここが落ちるとアセットが一切コンパイルできない）。
    #[test]
    fn param_asset_passes_naga_validation() {
        let params    = parse_params(PARAM_ASSET);
        let body      = strip_declarations(PARAM_ASSET);
        let decls     = generate_param_decls(&params.params);
        let generated = generate_dispatch(true, &params.params);
        if let Err((msg, line, start)) = prepare_variant("param.wgsl", &body, &decls, &generated) {
            panic!("パラメータ宣言つきアセットの検証に失敗: {}",
                format_error("param.wgsl", &msg, line, start, asset_line_count(&body)));
        }
        // エディタ検証（未保存バッファ経路）でも診断ゼロであること。
        assert!(validate_asset_source(PARAM_ASSET).is_empty(),
            "診断が出た: {:?}", validate_asset_source(PARAM_ASSET));
    }

    /// **宣言を除去せずに連結すると naga が落ちる**こと（＝前処理が必須である根拠）。
    ///
    /// これが通ってしまうなら前処理は要らない、という逆向きの確認でもある。
    #[test]
    fn raw_override_block_would_fail_naga() {
        let params    = parse_params(PARAM_ASSET);
        let decls     = generate_param_decls(&params.params);
        let generated = generate_dispatch(true, &params.params);
        assert!(prepare_variant("param.wgsl", PARAM_ASSET, &decls, &generated).is_err(),
            "override 宣言つきの生ソースが naga を通ってしまった（前処理の前提が崩れている）");
    }

    /// 宣言の**後ろ**にある構文エラーが、除去後もアセットの実際の行を指すこと
    /// （行数を保つ除去にしてある理由。ここがずれると誤った行に赤線が出る）。
    #[test]
    fn line_numbers_survive_declaration_stripping() {
        let broken = "// @water_shading_contract 1\n\
                      @color\n\
                      override tint: vec3<f32> = vec3(1.0, 0.0, 0.0);\n\
                      fn water_shade(input: WaterShadeInput) -> vec4<f32> {\n\
                      \x20   retur vec4<f32>(tint, 1.0);\n\
                      }\n";
        let diags = validate_asset_source(broken);
        assert_eq!(diags.len(), 1, "{diags:?}");
        assert_eq!(diags[0].line, Some(5), "宣言を除いた後もアセット 5 行目を指すこと: {:?}", diags[0]);
    }

    /// 壊れた宣言行は「診断 1 件（variant=param）＋ アセット自体は正常」になること。
    #[test]
    fn broken_declaration_reports_param_diagnostic() {
        let src = "// @water_shading_contract 1\n\
                   override weird: i32 = 1;\n\
                   fn water_shade(i: WaterShadeInput) -> vec4<f32> { return vec4<f32>(0.0); }\n";
        let diags = validate_asset_source(src);
        assert_eq!(diags.len(), 1, "{diags:?}");
        assert_eq!(diags[0].variant, VARIANT_NAME_PARAM);
        assert_eq!(diags[0].line, Some(2), "宣言のある行を指すこと: {:?}", diags[0]);
    }

    /// `water_shade` を定義していないアセットは「警告 1 件」（＝エラーではない）。
    #[test]
    fn validate_warns_when_impl_missing() {
        let empty = "// @water_shading_contract 1\nconst UNUSED: f32 = 1.0;\n";
        let diags = validate_asset_source(empty);
        assert_eq!(diags.len(), 1);
        assert_eq!(diags[0].variant, VARIANT_NAME_WATER);
        assert!(diags[0].message.contains(WATER_SHADE_FN_NAME), "{}", diags[0].message);
    }
}
