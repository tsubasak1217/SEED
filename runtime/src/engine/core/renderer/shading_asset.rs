// ============================================================
//  shading_asset.rs — シェーディングアセット（.wgsl）のロード・検証・パイプライン生成（L3-a 第 2 段）
//
//  ## 役割（単一責任）
//  「1 本のユーザー WGSL ファイル（シェーディングアセット）」を、デファードライティング
//  パイプラインへ差し込むまでの一連の変換を担う:
//
//      アセット読み込み → 契約バージョン照合 → shade_model_N の存在検出
//        → `shade_surface`（ディスパッチ）の自動生成 → 標準連結への差し込み
//        → naga による事前検証 → wgpu パイプライン生成 → 内容ハッシュでキャッシュ
//
//  GPU の描画呼び出しは持たない（呼び出し側は frame_renderer.rs）。
//
//  ## 設計上の不変条件
//   1. **アセット未指定時は本モジュールを一切通らない**。`resolve` が呼ばれず、
//      frame_renderer は従来どおり `DrawContext::pipelines.deferred.*` を使う。
//   2. **ロードに失敗しても画面は壊れない**。失敗はエラーキューに積むだけで、呼び出し側は
//      組み込み標準パイプラインへフォールバックする。失敗した内容は記録され、
//      ファイル内容が変わるまで再試行しない（毎フレームの naga 検証を避ける）。
//   3. **不正な WGSL を `device.create_shader_module` へ渡さない**。生成した連結ソースは
//      必ず naga で parse + validate してからパイプラインを作る（デバイスロスト回避）。
//   4. **モデル 0 の経路は既存と完全に同値**。生成する `shade_surface` の `default:` 腕は
//      `shade_model_0` を素通しで返し、`shading_nan_guard` を掛けない
//      （理由は shading_dispatch.wgsl のコメント参照）。
//
//  ## 依存
//   - shading_contract.wgsl : 契約 v1（型・標準ライブラリ・shade_model_0）
//   - deferred.rs           : パイプライン構成（レイアウト・フォーマット・エントリポイント）の正典
//   - pipelines/deferred_lighting*.toml : rt_off / rt_on 変種の連結順の正典
// ============================================================

use std::cell::RefCell;
use std::collections::HashMap;
use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use std::sync::Arc;
use std::time::Instant;

use super::deferred::{DeferredLightingPipelines, RT_BINDLESS_SHADER_SOURCES};
use super::pipeline::get_shader_source;
use super::pipeline_config::{PipelineConfig, RenderPipelineBuilder};
use super::surface_id::{SHADING_MODEL_DEFAULT_PBR, SHADING_MODEL_MASK};

// ============================================================
//  定数（マジックナンバーの一切を名前で持つ）
// ============================================================

/// シェーディング契約のバージョン（Rust 側の写し）。
///
/// `shaders/shading_contract.wgsl` の `const SHADING_CONTRACT_VERSION` と**必ず一致**させる。
/// 一致はユニットテスト `rust_and_wgsl_contract_versions_match` が固定する。
pub const SHADING_CONTRACT_VERSION: u32 = 1;

/// アセットが契約バージョンを宣言するための行内マーカー（先頭コメントに書く）。
/// 例: `// @shading_contract 1`
const CONTRACT_MARKER: &str = "@shading_contract";

/// アセットが定義できるシェーディングモデル ID の下限。
/// 0 はエンジン標準 PBR（`SHADING_MODEL_DEFAULT_PBR`）で予約されているため 1 から。
const USER_MODEL_ID_MIN: u8 = SHADING_MODEL_DEFAULT_PBR + 1;
/// アセットが定義できるシェーディングモデル ID の上限。
/// G-Buffer に詰められるビット幅（`SHADING_MODEL_MASK`）が決める。
const USER_MODEL_ID_MAX: u8 = SHADING_MODEL_MASK;
/// アセットが定義できるモデルの枠数（= 1..=3 の 3 枠）。
pub const USER_MODEL_SLOTS: usize = (USER_MODEL_ID_MAX - USER_MODEL_ID_MIN + 1) as usize;

/// 生成する `shade_surface` が定義される関数名。標準連結では
/// `shading_dispatch.wgsl` が同名関数を持つため、その要素を差し替える形で連結する。
const DISPATCH_SOURCE_NAME: &str = "shading_dispatch.wgsl";

/// 生成した `shade_surface` に与える擬似シェーダ名（連結リスト上の識別子）。
/// 実ファイルは存在しない。`make_resolver` がこの名前を生成ソースへ解決する。
const GENERATED_SOURCE_NAME: &str = "<generated shade_surface>";

/// アセットの mtime をポーリングする最短間隔 [秒]。
/// Edit モードのホットリロード専用。毎フレーム `std::fs::metadata` を叩かないための間引き。
pub const SHADING_ASSET_POLL_INTERVAL_SECS: f64 = 1.0;

/// キャッシュキーに使うハッシュ方式。
///
/// `std::collections::hash_map::DefaultHasher`（SipHash-1-3）を使う。暗号学的強度は不要で、
/// 求めるのは「同一内容 → 同一キー」「異なる内容 → 実質衝突しない」の 2 点だけである。
/// **内容**のハッシュをキーにするため、別パスに同じ内容を置いた場合はパイプラインを共有できる。
fn content_hash(src: &str) -> u64 {
    let mut h = DefaultHasher::new();
    src.hash(&mut h);
    h.finish()
}

// ============================================================
//  1-1. アセットソースの解析
// ============================================================

/// シェーディングアセットの WGSL ソースをアセット FS から読み込む。
///
/// PAK / ファイルシステムの解決は `asset_fs` に委ねる（BOM 除去込み）。
pub fn load_source(path: &str) -> std::io::Result<String> {
    crate::engine::asset_fs::read_string(path)
}

/// WGSL ソースから行コメント `//` とブロックコメント `/* */` を除去する。
///
/// **改行は必ず保持する**（除去後の行番号が元ソースと 1 対 1 に対応することが、
/// 後段の「行頭 fn」検出と行番号写像の前提になっているため）。
/// 文字列リテラル（WGSL には実質存在しないが将来に備える）は考慮しない単純スキャンだが、
/// 検出側が「行頭の `fn`」を要求するため誤検出には至らない。
fn strip_comments(src: &str) -> String {
    /// スキャナの状態。
    #[derive(Clone, Copy, PartialEq)]
    enum St { Code, Line, Block }

    let mut out = String::with_capacity(src.len());
    let mut st = St::Code;
    let bytes: Vec<char> = src.chars().collect();
    let mut i = 0usize;
    while i < bytes.len() {
        let c = bytes[i];
        let n = bytes.get(i + 1).copied();
        match st {
            St::Code => {
                if c == '/' && n == Some('/') {
                    st = St::Line;
                    i += 2;
                } else if c == '/' && n == Some('*') {
                    st = St::Block;
                    i += 2;
                } else {
                    out.push(c);
                    i += 1;
                }
            }
            St::Line => {
                // 行コメントは改行で終わる。改行そのものはコードとして残す。
                if c == '\n' {
                    out.push('\n');
                    st = St::Code;
                }
                i += 1;
            }
            St::Block => {
                // ブロックコメント内の改行は行番号維持のため残す。
                if c == '*' && n == Some('/') {
                    st = St::Code;
                    i += 2;
                } else {
                    if c == '\n' { out.push('\n'); }
                    i += 1;
                }
            }
        }
    }
    out
}

/// 1 行が「行頭（空白のみを挟む）に `fn <name>(`」の定義であるかを判定する。
///
/// WGSL の関数定義は必ず行頭から始まる（このプロジェクトのアセット規約でもそう）。
/// 呼び出し側は必ず前置き（`return ` / `= ` など）を伴うため行頭には来ず、
/// この条件だけで「定義」と「呼び出し・識別子の一部」を分離できる。
/// `my_shade_model_1` のような**前置き付き識別子**も、`fn` と空白の後に来る名前が
/// 完全一致であることを要求するため誤検出しない。
fn line_defines_fn(line: &str, fn_name: &str) -> bool {
    let t = line.trim_start();
    // `fn` キーワード＋直後が空白であること。
    let Some(rest) = t.strip_prefix("fn") else { return false };
    if !rest.starts_with(|c: char| c.is_whitespace()) { return false }
    let rest = rest.trim_start();
    // 関数名が完全一致し、その直後は空白か `(` であること（`shade_model_11` を弾く）。
    let Some(after) = rest.strip_prefix(fn_name) else { return false };
    let after = after.trim_start();
    after.starts_with('(')
}

/// アセット本文から `shade_model_1` / `shade_model_2` / `shade_model_3` の**定義の有無**を検出する。
///
/// 返り値はインデックス 0 がモデル ID 1 に対応する `[bool; USER_MODEL_SLOTS]`。
/// 厳密な WGSL パースは行わず、
///   1. コメント（行・ブロック）を除去し
///   2. 「行頭 `fn` → 空白 → `shade_model_N` → 空白* → `(`」に一致する行を探す
/// という 2 段で誤検出を実用上ゼロにしている（正規表現クレートは依存に無いため手書き）。
pub fn detect_shade_models(asset_src: &str) -> [bool; USER_MODEL_SLOTS] {
    let stripped = strip_comments(asset_src);
    let mut found = [false; USER_MODEL_SLOTS];
    for line in stripped.lines() {
        for slot in 0..USER_MODEL_SLOTS {
            let id = USER_MODEL_ID_MIN + slot as u8;
            if line_defines_fn(line, &format!("shade_model_{id}")) {
                found[slot] = true;
            }
        }
    }
    found
}

/// アセット先頭コメントの `// @shading_contract <N>` 宣言を読む。
///
/// - `Some(n)` : 宣言があり、`n` として解釈できた
/// - `None`    : 宣言が無い（＝ v1 とみなして警告扱いで通す）
///
/// 宣言はコメント内に書くため、**コメント除去前**の生ソースを走査する。
pub fn parse_contract_version(asset_src: &str) -> Option<u32> {
    for line in asset_src.lines() {
        let Some(pos) = line.find(CONTRACT_MARKER) else { continue };
        let rest = &line[pos + CONTRACT_MARKER.len()..];
        let num: String = rest.trim_start().chars().take_while(|c| c.is_ascii_digit()).collect();
        if !num.is_empty() {
            return num.parse::<u32>().ok();
        }
    }
    None
}

// ============================================================
//  1-2. ディスパッチ生成
// ============================================================

/// 検出結果から契約関数 `shade_surface` の WGSL 実装を生成する。
///
/// 定義されているモデルの `case` だけを出力し、それ以外はすべて `default:` ＝
/// モデル 0（エンジン標準 PBR）へ落ちる。
///
/// ## ガードの掛け方（設計要件）
/// - `case 1u..3u`（**ユーザー実装**）: `shading_nan_guard` を掛ける。任意の式が書けるため
///   NaN/Inf/負値がブルームやトーンマップを汚染しうる。
/// - `default:`（**モデル 0**）: ガードを掛けない。「ID 0 の経路はアセット導入前と
///   完全に同値」という L3-a の設計要件に疑いを残さないため（shading_dispatch.wgsl 参照）。
pub fn generate_dispatch(found: &[bool; USER_MODEL_SLOTS]) -> String {
    let mut s = String::new();
    s.push_str("// ── 自動生成（shading_asset.rs）。このコードは編集できません ──\n");
    s.push_str("fn shade_surface(sf: ShadingSurface, li: LightSample) -> vec3<f32> {\n");
    s.push_str("    switch sf.shading_model {\n");
    for (slot, defined) in found.iter().enumerate() {
        if !defined { continue }
        let id = USER_MODEL_ID_MIN + slot as u8;
        // ユーザー実装だけ NaN ガードを通す。
        s.push_str(&format!(
            "        case {id}u: {{ return shading_nan_guard(shade_model_{id}(sf, li)); }}\n"
        ));
    }
    // default はモデル 0 へ素通し（ガード無し＝既存経路と同値）。
    s.push_str("        default: { return shade_model_0(sf, li); }\n");
    s.push_str("    }\n");
    s.push_str("}\n");
    s
}

// ============================================================
//  連結（標準リストの shading_dispatch.wgsl をアセット 2 要素で置換する）
// ============================================================

/// デファードライティングの 3 変種。
///
/// `DeferredLightingPipelines` の `pipeline` / `rt` / `rt_bindless` に 1 対 1 対応する。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Variant {
    /// RT 非対応・RT オフ（`pipelines/deferred_lighting.toml`）。
    RtOff,
    /// RT 影あり（`pipelines/deferred_lighting_rt.toml`）。
    RtOn,
    /// RT 影＋バインドレス色付き影（`deferred::RT_BINDLESS_SHADER_SOURCES`）。
    RtBindless,
}

impl Variant {
    /// この変種の**標準**シェーダ連結リストを取得する。
    ///
    /// rt_off / rt_on は TOML の `shader_sources` を、rt_bindless は deferred.rs の
    /// 定数を読む。**配列をここへベタ書きしない**（連結順の二重管理を避ける）。
    fn standard_sources(self) -> Vec<String> {
        match self {
            Variant::RtOff => Self::sources_from_toml(include_str!("pipelines/deferred_lighting.toml")),
            Variant::RtOn  => Self::sources_from_toml(include_str!("pipelines/deferred_lighting_rt.toml")),
            Variant::RtBindless => RT_BINDLESS_SHADER_SOURCES.iter().map(|s| s.to_string()).collect(),
        }
    }

    /// TOML から `shader_sources` だけを取り出す。
    fn sources_from_toml(toml_src: &str) -> Vec<String> {
        let cfg: PipelineConfig = toml::from_str(toml_src)
            .expect("deferred_lighting*.toml のパースに失敗（TOML が壊れている）");
        cfg.shader_sources
    }

    /// naga 検証に必要なケイパビリティ。
    /// 正典は deferred.rs の同名テスト群（rt_on=RAY_QUERY / rt_bindless=+非一様インデックス）。
    fn capabilities(self) -> naga::valid::Capabilities {
        match self {
            Variant::RtOff => naga::valid::Capabilities::empty(),
            Variant::RtOn  => naga::valid::Capabilities::RAY_QUERY,
            Variant::RtBindless => naga::valid::Capabilities::RAY_QUERY
                | naga::valid::Capabilities::SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING,
        }
    }
}

/// 標準連結リストの `shading_dispatch.wgsl` を「アセット本体 ＋ 生成ディスパッチ」で置換した
/// シェーダ名リストを返す。
///
/// 置換後のリストは `resolve_with_asset` と組み合わせて使う（名前 → ソースの解決側で
/// アセット本体と生成ディスパッチを返す）。名前は WGSL には現れないので任意でよいが、
/// naga のエラーメッセージ以外の場所で見えることは無い。
fn substitute_dispatch(standard: &[String], asset_name: &str, generated_name: &str) -> Vec<String> {
    let mut out = Vec::with_capacity(standard.len() + 1);
    for name in standard {
        if name == DISPATCH_SOURCE_NAME {
            out.push(asset_name.to_string());
            out.push(generated_name.to_string());
        } else {
            out.push(name.clone());
        }
    }
    out
}

/// 差し替え済みリストからソースを連結し、`(連結ソース, アセット本体の開始行番号)` を返す。
///
/// 開始行番号は **1 始まり**。`RenderPipelineBuilder::build_owned` と同じく `"\n"` で join する
/// ため、パート k の開始行 = 1 + Σ(パート i の改行数 + 1)（i < k）で求まる。
fn concat_sources<F>(names: &[String], asset_name: &str, resolve: F) -> (String, usize)
where
    F: Fn(&str) -> String,
{
    let mut combined = String::new();
    let mut line = 1usize;
    let mut asset_start = 1usize;
    for (i, name) in names.iter().enumerate() {
        if i > 0 { combined.push('\n'); }
        if name == asset_name { asset_start = line; }
        let src = resolve(name);
        // このパートが消費する行数（join の '\n' 1 個ぶんを含む）。
        line += src.matches('\n').count() + 1;
        combined.push_str(&src);
    }
    (combined, asset_start)
}

// ============================================================
//  エラー行番号の写像
// ============================================================

/// naga が報告した連結ソース上の行番号を、アセット内行番号へ写した結果。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum MappedLine {
    /// アセット本体の中（値はアセット先頭を 1 とする行番号）。
    InAsset(usize),
    /// アセット範囲外（エンジン標準ライブラリ側。値は連結ソース上の素の行番号）。
    OutsideAsset(usize),
}

/// 連結ソース上の報告行を、アセット内行番号へ写す。
///
/// - `reported_line`    : naga が返した行番号（1 始まり・連結ソース基準）
/// - `asset_start_line` : 連結ソース中でアセット本体が始まる行番号（1 始まり）
/// - `asset_line_count` : アセット本体の行数
///
/// アセット範囲内なら `reported_line - (asset_start_line - 1)`。範囲外なら
/// 「エンジン標準ライブラリ側」として素の行番号を返す。
pub fn map_reported_line(
    reported_line:    usize,
    asset_start_line: usize,
    asset_line_count: usize,
) -> MappedLine {
    if reported_line >= asset_start_line && reported_line < asset_start_line + asset_line_count {
        MappedLine::InAsset(reported_line - (asset_start_line - 1))
    } else {
        MappedLine::OutsideAsset(reported_line)
    }
}

/// アセットの行数（`concat_sources` の行会計と同じ数え方）。
fn asset_line_count(asset_src: &str) -> usize {
    asset_src.matches('\n').count() + 1
}

/// naga の parse + validate を通す。失敗時は `(メッセージ, 行番号)` を返す。
fn validate_wgsl(src: &str, caps: naga::valid::Capabilities) -> Result<(), (String, Option<usize>)> {
    let module = match naga::front::wgsl::parse_str(src) {
        Ok(m) => m,
        Err(e) => {
            let line = e.location(src).map(|l| l.line_number as usize);
            return Err((e.message().to_string(), line));
        }
    };
    let mut validator = naga::valid::Validator::new(naga::valid::ValidationFlags::all(), caps);
    match validator.validate(&module) {
        Ok(_) => Ok(()),
        Err(e) => {
            let line = e.location(src).map(|l| l.line_number as usize);
            Err((format!("{}", e.as_inner()), line))
        }
    }
}

/// 検証エラーを「`<アセットパス>:<行>: <naga のメッセージ>`」形式の人間可読文字列にする。
fn format_error(
    asset_path:       &str,
    variant:          Variant,
    msg:              &str,
    reported_line:    Option<usize>,
    asset_start_line: usize,
    asset_lines:      usize,
) -> String {
    match reported_line.map(|l| map_reported_line(l, asset_start_line, asset_lines)) {
        Some(MappedLine::InAsset(l))       => format!("{asset_path}:{l}: {msg}"),
        Some(MappedLine::OutsideAsset(l))  =>
            format!("{asset_path}:-: エンジン標準ライブラリ側（連結 {variant:?} の {l} 行目）でエラー: {msg}"),
        None => format!("{asset_path}:-: {msg}"),
    }
}

// ============================================================
//  1-3. パイプライン構築
// ============================================================

/// シェーディングアセットで差し替えたデファードライティングパイプライン一式。
///
/// `DeferredLightingPipelines` の 3 バリアントに 1 対 1 対応する差し替え版。
/// 設定（レイアウト・ターゲットフォーマット・エントリポイント）は deferred.rs の
/// `DeferredLightingPipelines::new` と**同一**にすること（片方だけ変えると
/// フォールバック時に描画結果が食い違う）。
///
/// `rt` / `rt_bindless` は組み込み側と同じく「対応 GPU でのみ Some」。加えて
/// **その変種の naga 検証に失敗した場合も None** になる（呼び出し側は組み込みへ落とす）。
pub struct ShadingAssetPipelines {
    /// RT 非対応・RT オフ用（常に構築される。ここが失敗したらアセット全体が失敗）。
    pub pipeline:    wgpu::RenderPipeline,
    /// RT 影対応バリアント。
    pub rt:          Option<wgpu::RenderPipeline>,
    /// RT 影＋バインドレス色付き影バリアント。
    pub rt_bindless: Option<wgpu::RenderPipeline>,
    /// このアセットが実装しているシェーディングモデル（インデックス 0 = モデル ID 1）。
    /// 診断ログ（`[SHADING] loaded ... models=`）で「どのモデル ID が有効になったか」を
    /// 一目で分かるようにするために保持する。描画判定には使わない。
    pub models:      [bool; USER_MODEL_SLOTS],
}

/// 1 変種ぶんの連結ソースを組み立てて naga 検証する。
///
/// 返り値は `(連結ソース, シェーダ名リスト, アセット開始行)`。
fn prepare_variant(
    variant:    Variant,
    asset_name: &str,
    asset_src:  &str,
    generated:  &str,
) -> Result<(String, Vec<String>, usize), (String, Option<usize>, usize)> {
    let standard = variant.standard_sources();
    // 差し替えの前提（標準リストに既定ディスパッチが含まれること）を明示的に固定する。
    // ここが崩れると「アセットが連結されないのにエラーも出ない」＝ユーザーからは
    // 「設定したのに何も変わらない」という最も分かりにくい壊れ方をする。
    // 標準リストは TOML / deferred.rs から取るため将来の編集で外れうるので番人を置く。
    assert!(
        standard.iter().any(|n| n == DISPATCH_SOURCE_NAME),
        "シェーディングアセットの連結リストに {DISPATCH_SOURCE_NAME} が含まれていない\
         （バリアント {variant:?} の標準連結順を確認すること）"
    );
    let names   = substitute_dispatch(&standard, asset_name, GENERATED_SOURCE_NAME);
    let resolve = make_resolver(asset_name, asset_src, generated);
    let (combined, asset_start) = concat_sources(&names, asset_name, &resolve);
    match validate_wgsl(&combined, variant.capabilities()) {
        Ok(())            => Ok((combined, names, asset_start)),
        Err((msg, line))  => Err((msg, line, asset_start)),
    }
}

impl ShadingAssetPipelines {
    /// アセットソースから 3 変種のパイプラインを構築する。
    ///
    /// - `deferred`    : 組み込み側。バインドレス変種のレイアウト（camera/gbuffer/gap2/
    ///                   colored_shadow BGL）を**借用**する。
    /// - `out_format`  : 出力先（シーン HDR）。`DrawContext` が保持する scene_format。
    /// - `depth_format`: no_depth=true のため実際には未使用（ビルダーのシグネチャ充足）。
    ///
    /// 失敗（rt_off 変種の検証失敗・パイプライン構築不能）は `Err(メッセージ)`。
    /// rt / rt_bindless 変種の失敗は `Err` にせず、当該フィールドを None にしてメッセージを
    /// `warnings` へ積む（呼び出し側がそこだけ組み込みへフォールバックできる）。
    fn build(
        device:       &wgpu::Device,
        deferred:     &DeferredLightingPipelines,
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
            Some(v) if v != SHADING_CONTRACT_VERSION => {
                return Err(format!(
                    "{asset_path}:-: 契約バージョン不一致（アセット宣言 {v} / エンジン \
                     {SHADING_CONTRACT_VERSION}）。`// @shading_contract {SHADING_CONTRACT_VERSION}` \
                     に更新し、契約の変更点に追随してください"
                ));
            }
            Some(_) => {}
            None => {
                // 宣言が無い場合は v1 とみなして通す（警告扱い）。
                warnings.push(format!(
                    "{asset_path}:-: `// @shading_contract {SHADING_CONTRACT_VERSION}` の宣言が \
                     ありません（v{SHADING_CONTRACT_VERSION} とみなして読み込みました）"
                ));
            }
        }

        // ── モデル検出とディスパッチ生成 ──────────────────────
        let found = detect_shade_models(asset_src);
        if !found.iter().any(|f| *f) {
            warnings.push(format!(
                "{asset_path}:-: shade_model_{USER_MODEL_ID_MIN}..{USER_MODEL_ID_MAX} の定義が \
                 1 つも見つかりません（全マテリアルがエンジン標準 PBR で描画されます）"
            ));
        }
        let generated = generate_dispatch(&found);

        // ── rt_off 変種（必須） ───────────────────────────────
        let (_src_off, names_off, _start_off) =
            prepare_variant(Variant::RtOff, asset_name, asset_src, &generated)
                .map_err(|(msg, line, start)| {
                    format_error(asset_path, Variant::RtOff, &msg, line, start, lines)
                })?;
        let resolve_off = make_resolver(asset_name, asset_src, &generated);
        let (pipeline, _bgls) = RenderPipelineBuilder::new(
            device, include_str!("pipelines/deferred_lighting.toml"), out_format, depth_format,
        )
        .with_label("deferred_lighting_shading_asset")
        // 差し替え済みリストを渡すため、TOML の shader_sources を上書きする。
        .with_shader_sources(names_off)
        .build_owned(resolve_off);

        // ── rt_on 変種（RT 対応 GPU のみ） ────────────────────
        let mut rt_group4_bgl: Option<wgpu::BindGroupLayout> = None;
        let rt = if super::rt_shadow::rt_shadows_supported() {
            match prepare_variant(Variant::RtOn, asset_name, asset_src, &generated) {
                Ok((_src, names, _start)) => {
                    let resolve = make_resolver(asset_name, asset_src, &generated);
                    let (pipe, bgls) = RenderPipelineBuilder::new(
                        device, include_str!("pipelines/deferred_lighting_rt.toml"),
                        out_format, depth_format,
                    )
                    .with_label("deferred_lighting_rt_shading_asset")
                    .with_shader_sources(names)
                    .build_owned(resolve);
                    // group 順 [0 camera,1 gbuffer,2 gap,3 gap,4 lights+TLAS]。group4 を控える。
                    rt_group4_bgl = bgls.into_iter().nth(4);
                    Some(pipe)
                }
                Err((msg, line, start)) => {
                    warnings.push(format_error(asset_path, Variant::RtOn, &msg, line, start, lines));
                    None
                }
            }
        } else {
            None
        };

        // ── rt_bindless 変種（RT かつバインドレス対応 GPU のみ） ──
        // レイアウトは組み込み側の BGL を借用する（deferred.rs の rt_bindless 構築と同じ流儀。
        // group4 は rt 変種のリフレクション結果、group3 は組み込みの colored_shadow_bgl）。
        let use_bindless = rt.is_some()
            && super::bindless::bindless_supported()
            && deferred.colored_shadow_bgl.is_some();
        let rt_bindless = if use_bindless {
            let g4     = rt_group4_bgl.as_ref().expect("rt 変種構築時に group4 BGL は必ず確定する");
            let cs_bgl = deferred.colored_shadow_bgl.as_ref().expect("use_bindless の条件で Some");
            match prepare_variant(Variant::RtBindless, asset_name, asset_src, &generated) {
                Ok((combined, _names, _start)) => Some(build_bindless_pipeline(
                    device, deferred, g4, cs_bgl, &combined, out_format,
                )),
                Err((msg, line, start)) => {
                    warnings.push(format_error(asset_path, Variant::RtBindless, &msg, line, start, lines));
                    None
                }
            }
        } else {
            None
        };

        Ok(Self { pipeline, rt, rt_bindless, models: found })
    }
}

/// 検出済みモデル配列を診断ログ用のラベルにする（例: `1,3` / `なし`）。
pub fn models_label(models: &[bool; USER_MODEL_SLOTS]) -> String {
    let ids: Vec<String> = models.iter().enumerate()
        .filter(|(_, defined)| **defined)
        .map(|(slot, _)| (USER_MODEL_ID_MIN + slot as u8).to_string())
        .collect();
    if ids.is_empty() { "なし".to_string() } else { ids.join(",") }
}

/// シェーダ名 → ソースの解決関数を作る（アセット本体・生成ディスパッチ・組み込みの 3 分岐）。
fn make_resolver<'a>(
    asset_name: &'a str,
    asset_src:  &'a str,
    generated:  &'a str,
) -> impl Fn(&str) -> String + 'a {
    move |n: &str| {
        if n == asset_name                    { asset_src.to_string() }
        else if n == GENERATED_SOURCE_NAME    { generated.to_string() }
        else                                  { get_shader_source(n).to_string() }
    }
}

/// バインドレス変種のパイプラインを手動レイアウトで構築する。
///
/// 設定は deferred.rs の `rt_bindless` 構築と**完全に同一**（レイアウト・ターゲット・
/// エントリポイント・プリミティブ/深度/マルチサンプル）。BGL は組み込み側から借用する。
fn build_bindless_pipeline(
    device:     &wgpu::Device,
    deferred:   &DeferredLightingPipelines,
    g4:         &wgpu::BindGroupLayout,
    cs_bgl:     &wgpu::BindGroupLayout,
    combined:   &str,
    out_format: wgpu::TextureFormat,
) -> wgpu::RenderPipeline {
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label:  Some("deferred_lighting_rt_bindless_shading_asset"),
        source: wgpu::ShaderSource::Wgsl(combined.to_string().into()),
    });
    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("deferred_lighting_rt_bindless_shading_asset"),
        bind_group_layouts: &[
            &deferred.camera_bgl, &deferred.gbuffer_bgl, &deferred.gap_bgl2, cs_bgl, g4,
        ],
        push_constant_ranges: &[],
    });
    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label:  Some("deferred_lighting_rt_bindless_shading_asset"),
        layout: Some(&layout),
        vertex: wgpu::VertexState {
            module: &shader, entry_point: Some("vs_fullscreen"),
            buffers: &[], compilation_options: Default::default(),
        },
        fragment: Some(wgpu::FragmentState {
            module: &shader, entry_point: Some("fs_deferred"),
            targets: &[Some(wgpu::ColorTargetState {
                format: out_format, blend: None, write_mask: wgpu::ColorWrites::ALL,
            })],
            compilation_options: Default::default(),
        }),
        primitive:     wgpu::PrimitiveState::default(),
        depth_stencil: None,
        multisample:   wgpu::MultisampleState::default(),
        multiview:     None,
        cache:         None,
    })
}

// ============================================================
//  キャッシュ（内容ハッシュ → パイプライン）
// ============================================================

/// アセットパスごとの追跡状態（ホットリロードの間引きと再読込判定に使う）。
struct PathState {
    /// 最後に mtime を確認した時刻（`SHADING_ASSET_POLL_INTERVAL_SECS` の間引き用）。
    last_poll: Instant,
    /// 最後に確認した mtime（秒。`asset_fs::mtime` の値）。
    mtime:     u64,
    /// 最後に読み込んだ内容のハッシュ。`None` = 読み込み自体に失敗した。
    hash:      Option<u64>,
}

/// シェーディングアセットのビルド結果キャッシュ。
///
/// `DrawContext` は `&self` 共有で使われるため、フレーム内から更新できるよう
/// 内部可変（`RefCell`）で持つ（先例: `SpritePostfxCache`）。
///
/// ## キー設計
/// - `built` のキーは**アセット内容のハッシュ**。同一内容なら別パスでもパイプラインを共有する。
///   値が `Err(メッセージ)` のエントリは「この内容ではビルドに失敗した」ことの記録で、
///   **内容が変わるまで再ビルドしない**（毎フレームの naga 検証を避ける）。
///   失敗メッセージを保持しておくのは、いったん直してからまた同じ壊し方に戻したときにも
///   同じエラーをエディタへ再通知できるようにするため。
/// - `paths` はパス → (最終ポーリング時刻, mtime, 内容ハッシュ)。mtime 不変なら
///   ファイル読み込み自体をスキップする。
pub struct ShadingAssetCache {
    /// 内容ハッシュ → ビルド結果（`Err` = 失敗済み・そのときのメッセージ）。
    built:      RefCell<HashMap<u64, Result<Arc<ShadingAssetPipelines>, String>>>,
    /// アセットパス → 追跡状態。
    paths:      RefCell<HashMap<String, PathState>>,
    /// App が毎フレーム drain して IPC（`LOAD_ERROR:`）へ流すエラー／警告キュー。
    errors:     RefCell<Vec<String>>,
    /// 直近に積んだメッセージ（同じものを連続で積まないための番人）。
    last_error: RefCell<Option<String>>,
    /// 直近に出力した `[SHADING]` 診断行（チャンネル名 → 前回の行）。
    ///
    /// `resolve` も描画側の採用ログも毎フレーム呼ばれるため、同一内容の行を出し続けない
    /// よう「前回と同じ行なら出力しない」で間引く。チャンネルを分けるのは、
    /// 「ロード結果（load）」と「実際に採用したバリアント（apply）」が交互に上書きし合って
    /// 毎フレーム出力になるのを防ぐため。
    last_report: RefCell<HashMap<&'static str, String>>,
}

impl Default for ShadingAssetCache {
    fn default() -> Self { Self::new() }
}

impl ShadingAssetCache {
    /// 空のキャッシュを生成する。
    pub fn new() -> Self {
        Self {
            built:      RefCell::new(HashMap::new()),
            paths:      RefCell::new(HashMap::new()),
            errors:      RefCell::new(Vec::new()),
            last_error:  RefCell::new(None),
            last_report: RefCell::new(HashMap::new()),
        }
    }

    /// `[SHADING]` 診断行を出力する（前回と同一内容なら出力しない）。
    ///
    /// 呼び出しは `resolve` の各 return 直前に集約してあり、
    /// 「アセットが読めたか」「どのモデル ID が有効か」「どのバリアントが作れたか」を
    /// 1 行で表す。エラー通知（`LOAD_ERROR:` へ流す `errors` キュー）とは独立で、
    /// **成功時も必ず出る**のがこの行の役割（＝無反応の切り分けに使える）。
    pub fn report(&self, channel: &'static str, line: String) {
        if self.last_report.borrow().get(&channel) == Some(&line) { return }
        eprintln!("{line}");
        self.last_report.borrow_mut().insert(channel, line);
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

    /// 全キャッシュを破棄する（アセットの差し替え・シーン切替時の保険）。
    #[allow(dead_code)]
    pub fn clear(&self) {
        self.built.borrow_mut().clear();
        self.paths.borrow_mut().clear();
    }
}

// ============================================================
//  解決（描画直前に呼ばれる入口）
// ============================================================

/// シェーディングアセットのパスからパイプライン一式を解決する。
///
/// 描画直前に毎フレーム呼ばれる想定。実際の I/O・ビルドは以下の条件でのみ走る:
///   - 初回（そのパスを見たことがない）
///   - `allow_hot_reload` かつ前回ポーリングから `SHADING_ASSET_POLL_INTERVAL_SECS` 経過し、
///     かつ mtime が変化している
///
/// 返り値 `None` は「アセットを使えない」の意味で、呼び出し側は組み込み標準
/// （`DrawContext::pipelines.deferred.*`）へフォールバックすること。
///
/// - `allow_hot_reload` : Edit モードのみ true。Play 中は開始時点のパイプラインを使い続ける。
pub fn resolve(
    device:           &wgpu::Device,
    deferred:         &DeferredLightingPipelines,
    cache:            &ShadingAssetCache,
    asset_path:       &str,
    out_format:       wgpu::TextureFormat,
    depth_format:     wgpu::TextureFormat,
    allow_hot_reload: bool,
) -> Option<Arc<ShadingAssetPipelines>> {
    let result = resolve_inner(
        device, deferred, cache, asset_path, out_format, depth_format, allow_hot_reload,
    );
    // ── 診断ログ（常時 ON・変化時のみ）───────────────────────
    // 「アセットを設定したのに見た目が変わらない」を切り分けるための 1 行。
    // resolve は毎フレーム呼ばれるので、ここに行が出ていれば少なくとも
    // 「シーン/カメラのアセット指定は描画側へ届いている」ことが確定する。
    let line = match &result {
        Some(p) => format!(
            "[SHADING] loaded path={asset_path} models={} variants=rt_off{}{}",
            models_label(&p.models),
            if p.rt.is_some()          { "+rt" }          else { "" },
            if p.rt_bindless.is_some() { "+rt_bindless" } else { "" },
        ),
        None => format!(
            "[SHADING] failed path={asset_path} \
             （組み込み標準シェーディングへフォールバック。詳細は [SEED shading_asset] 行）"
        ),
    };
    cache.report(REPORT_CHANNEL_LOAD, line);
    result
}

/// `[SHADING]` 診断行のチャンネル: アセットのロード結果。
pub const REPORT_CHANNEL_LOAD:  &str = "load";
/// `[SHADING]` 診断行のチャンネル: 実際に採用したパイプライン（描画側から報告）。
pub const REPORT_CHANNEL_APPLY: &str = "apply";

/// `resolve` の本体（診断ログを挟まない素の解決処理）。
fn resolve_inner(
    device:           &wgpu::Device,
    deferred:         &DeferredLightingPipelines,
    cache:            &ShadingAssetCache,
    asset_path:       &str,
    out_format:       wgpu::TextureFormat,
    depth_format:     wgpu::TextureFormat,
    allow_hot_reload: bool,
) -> Option<Arc<ShadingAssetPipelines>> {
    let now = Instant::now();

    // ── 1. 既知パスなら、読み込みが必要かを判定する ────────────
    let known: Option<(bool, u64, Option<u64>)> = cache.paths.borrow().get(asset_path)
        .map(|st| {
            // ポーリング間隔を過ぎたか。
            let due = now.duration_since(st.last_poll).as_secs_f64() >= SHADING_ASSET_POLL_INTERVAL_SECS;
            (due, st.mtime, st.hash)
        });

    let needs_read = match known {
        // 初回は必ず読む。
        None => true,
        Some((due, prev_mtime, _)) => {
            // Play 中（allow_hot_reload=false）は一切読み直さない。
            if !allow_hot_reload {
                false
            } else if !due {
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
            cache.push_message(format!("{asset_path}:-: シェーディングアセットを読めません: {e}"));
            // 読み込み失敗も記録して、mtime が変わるまで再試行しない。
            cache.paths.borrow_mut().insert(
                asset_path.to_string(),
                PathState { last_poll: now, mtime, hash: None },
            );
            return None;
        }
    };
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
    let result = ShadingAssetPipelines::build(
        device, deferred, asset_path, &src, out_format, depth_format, &mut warnings,
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
    // エントリが増える。1 エントリはレンダーパイプライン最大 3 本を抱えるため、
    // 放置すると編集を重ねるほど GPU メモリが単調増加する（数十回の保存で効いてくる）。
    // どのパスからも参照されなくなったハッシュはもう二度と引かれないので、ここで捨てる。
    // 残るのは「現在解決済みのアセットパスの数」ぶんだけで、上限が構造的に決まる。
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

    /// テスト用のサンプル「トゥーン」シェーディングアセット。
    ///
    /// 3 階調のセルシェーディング＋リムライトを `shade_model_1` として実装する。
    /// docs のサンプルコードとしてもそのまま使える実用的な内容にしてある。
    const TOON_ASSET: &str = r#"// @shading_contract 1
// ============================================================
// toon.wgsl — 3 階調セルシェーディング + リムライト（シェーディングモデル 1）
//
// マテリアルの「シェーディングモデル」を 1 に設定したアクタだけがこの実装で描かれる。
// 未設定（0）のアクタはエンジン標準 PBR のまま。
// ============================================================

/// 拡散光の階調数（3 階調＝影・中間・ハイライト）。
const TOON_DIFFUSE_STEPS: f32 = 3.0;
/// 最も暗い階調でも完全な黒にしないための下限（環境光的な底上げ）。
const TOON_SHADOW_FLOOR: f32 = 0.15;
/// スペキュラを「乗るか乗らないか」の 2 値にするしきい値。
const TOON_SPECULAR_THRESHOLD: f32 = 0.6;
/// 2 値スペキュラの強さ。
const TOON_SPECULAR_STRENGTH: f32 = 0.7;
/// スペキュラの鋭さ（大きいほど小さく硬いハイライト）。
const TOON_SPECULAR_POWER: f32 = 48.0;
/// リムライトの立ち上がり（大きいほど輪郭が細くなる）。
const TOON_RIM_POWER: f32 = 3.0;
/// リムライトの強さ。
const TOON_RIM_STRENGTH: f32 = 0.35;

/// シェーディングモデル 1: トゥーン。
fn shade_model_1(sf: ShadingSurface, li: LightSample) -> vec3<f32> {
    let N = sf.normal;
    let V = sf.view_dir;
    let L = li.direction;

    // ── 拡散: N·L を階調化する ─────────────────────────────
    let ndl  = shading_saturate(dot(N, L));
    // floor(x * steps) / steps は「段の下端」を返すため、段の中心へ寄せて明るさの目減りを防ぐ。
    let band = shading_posterize(ndl, TOON_DIFFUSE_STEPS) + 0.5 / TOON_DIFFUSE_STEPS;
    let diffuse_term = max(shading_saturate(band), TOON_SHADOW_FLOOR);

    // ── スペキュラ: Blinn-Phong を 2 値化する ───────────────
    let H    = normalize(V + L);
    let ndh  = shading_saturate(dot(N, H));
    let spec = pow(ndh, TOON_SPECULAR_POWER);
    let spec_term = select(0.0, TOON_SPECULAR_STRENGTH, spec > TOON_SPECULAR_THRESHOLD);

    // ── リムライト: 視線に対して立っている面ほど明るく ──────
    // 影の付いていない側に出ると不自然なので、ライト方向の寄与で抑える。
    let rim  = pow(1.0 - shading_saturate(dot(N, V)), TOON_RIM_POWER);
    let rim_term = rim * TOON_RIM_STRENGTH * ndl;

    // li.color は減衰・スポット円錐・影まで織り込み済み（再計算してはならない）。
    return (sf.base_color * diffuse_term + vec3<f32>(spec_term) + vec3<f32>(rim_term)) * li.color;
}
"#;

    // ── 1. 契約バージョンの一致 ────────────────────────────

    /// Rust 側の `SHADING_CONTRACT_VERSION` と shading_contract.wgsl の
    /// `const SHADING_CONTRACT_VERSION` が一致すること。
    ///
    /// 食い違うとローダの照合（アセットの `// @shading_contract N`）が意味を失う。
    #[test]
    fn rust_and_wgsl_contract_versions_match() {
        let src = include_str!("shaders/shading_contract.wgsl");
        let line = src.lines().map(str::trim)
            .find(|l| l.starts_with("const SHADING_CONTRACT_VERSION"))
            .expect("shading_contract.wgsl に const SHADING_CONTRACT_VERSION が見つかりません");
        let rhs = line.split('=').nth(1).expect("= の右辺がありません");
        let num: String = rhs.trim().chars().take_while(|c| c.is_ascii_digit()).collect();
        let wgsl_version: u32 = num.parse().expect("u32 として解釈できません");
        assert_eq!(wgsl_version, SHADING_CONTRACT_VERSION,
            "Rust と WGSL の契約バージョンが食い違っている");
    }

    /// アセット定義可能な ID の範囲が surface_id のビット規約から導かれていること。
    #[test]
    fn user_model_id_range_is_derived_from_surface_id() {
        assert_eq!(USER_MODEL_ID_MIN, 1);
        assert_eq!(USER_MODEL_ID_MAX, 3);
        assert_eq!(USER_MODEL_SLOTS, 3);
    }

    // ── 2. 関数存在検出 ────────────────────────────────────

    /// 3 モデルすべてを定義したアセットを正しく検出する。
    #[test]
    fn detects_all_three_models() {
        let src = "\
fn shade_model_1(sf: ShadingSurface, li: LightSample) -> vec3<f32> { return vec3<f32>(0.0); }
fn shade_model_2 (sf: ShadingSurface, li: LightSample) -> vec3<f32> { return vec3<f32>(0.0); }
    fn shade_model_3(sf: ShadingSurface, li: LightSample) -> vec3<f32> { return vec3<f32>(0.0); }
";
        assert_eq!(detect_shade_models(src), [true, true, true]);
    }

    /// 1 個だけ定義したアセットは、その 1 個だけ検出する。
    #[test]
    fn detects_only_defined_model() {
        assert_eq!(detect_shade_models(TOON_ASSET), [true, false, false]);
    }

    /// コメントアウトされた定義は検出しない（行コメント・ブロックコメントの両方）。
    #[test]
    fn commented_out_definitions_are_not_detected() {
        let src = "\
// fn shade_model_1(sf: ShadingSurface, li: LightSample) -> vec3<f32> { return vec3<f32>(0.0); }
/*
fn shade_model_2(sf: ShadingSurface, li: LightSample) -> vec3<f32> { return vec3<f32>(0.0); }
*/
fn shade_model_3(sf: ShadingSurface, li: LightSample) -> vec3<f32> { return vec3<f32>(0.0); }
";
        assert_eq!(detect_shade_models(src), [false, false, true]);
    }

    /// 識別子の一部・呼び出し・文字列は誤検出しない。
    #[test]
    fn partial_identifiers_are_not_detected() {
        let src = "\
fn my_shade_model_1(sf: ShadingSurface, li: LightSample) -> vec3<f32> { return vec3<f32>(0.0); }
fn helper(sf: ShadingSurface, li: LightSample) -> vec3<f32> { return shade_model_2(sf, li); }
fn shade_model_31(sf: ShadingSurface, li: LightSample) -> vec3<f32> { return vec3<f32>(0.0); }
";
        assert_eq!(detect_shade_models(src), [false, false, false]);
    }

    /// コメント除去で改行が保持されること（行番号写像の前提）。
    #[test]
    fn comment_stripping_preserves_line_count() {
        let src = "a\n// comment\n/* multi\nline\ncomment */\nb\n";
        let stripped = strip_comments(src);
        assert_eq!(stripped.lines().count(), src.lines().count());
    }

    // ── 3. 生成ディスパッチ ────────────────────────────────

    /// 検出されたモデルの case だけが出ること。
    #[test]
    fn dispatch_emits_only_detected_cases() {
        let d = generate_dispatch(&[true, false, true]);
        assert!(d.contains("case 1u:"), "モデル 1 の case が必要");
        assert!(!d.contains("case 2u:"), "未定義のモデル 2 の case は出さない");
        assert!(d.contains("case 3u:"), "モデル 3 の case が必要");
        assert!(d.contains("fn shade_surface(sf: ShadingSurface, li: LightSample) -> vec3<f32>"));
    }

    /// `default:` は常にモデル 0 で、`shading_nan_guard` を掛けないこと（設計要件）。
    /// 逆にユーザー実装の case には必ずガードが掛かること。
    #[test]
    fn dispatch_default_is_model_zero_without_guard() {
        for found in [[false; 3], [true, true, true], [false, true, false]] {
            let d = generate_dispatch(&found);
            let default_line = d.lines().find(|l| l.trim_start().starts_with("default:"))
                .expect("default 腕が必要");
            assert!(default_line.contains("shade_model_0(sf, li)"),
                "default はモデル 0 へ落ちること: {default_line}");
            assert!(!default_line.contains("shading_nan_guard"),
                "default にガードを掛けてはならない（既存経路との同値性）: {default_line}");
            for (slot, defined) in found.iter().enumerate() {
                if !defined { continue }
                let id = USER_MODEL_ID_MIN + slot as u8;
                let case_line = d.lines().find(|l| l.trim_start().starts_with(&format!("case {id}u:")))
                    .expect("定義済みモデルの case が必要");
                assert!(case_line.contains("shading_nan_guard"),
                    "ユーザー実装にはガードが要る: {case_line}");
            }
        }
    }

    // ── 4. 行番号写像 ──────────────────────────────────────

    /// プレフィクス N 行のとき、報告行 N+3 がアセット 3 行目に写ること。
    #[test]
    fn reported_line_maps_into_asset() {
        const PREFIX_LINES: usize = 120;
        let asset_start = PREFIX_LINES + 1;   // アセット本体はプレフィクスの次の行から
        let asset_lines = 40;
        assert_eq!(
            map_reported_line(PREFIX_LINES + 3, asset_start, asset_lines),
            MappedLine::InAsset(3),
        );
        // アセット先頭行・末尾行の境界。
        assert_eq!(map_reported_line(asset_start, asset_start, asset_lines), MappedLine::InAsset(1));
        assert_eq!(
            map_reported_line(asset_start + asset_lines - 1, asset_start, asset_lines),
            MappedLine::InAsset(asset_lines),
        );
    }

    /// プレフィクス内の行・アセット末尾より後の行はアセット範囲外と判定されること。
    #[test]
    fn reported_line_outside_asset_is_flagged() {
        const PREFIX_LINES: usize = 120;
        let asset_start = PREFIX_LINES + 1;
        let asset_lines = 40;
        assert_eq!(map_reported_line(5, asset_start, asset_lines), MappedLine::OutsideAsset(5));
        assert_eq!(
            map_reported_line(PREFIX_LINES, asset_start, asset_lines),
            MappedLine::OutsideAsset(PREFIX_LINES),
        );
        let after = asset_start + asset_lines;
        assert_eq!(map_reported_line(after, asset_start, asset_lines), MappedLine::OutsideAsset(after));
    }

    /// `concat_sources` が返すアセット開始行が、実際の連結ソース上の行と一致すること
    /// （行番号写像の正しさは、この開始行が正しいことに依存する）。
    #[test]
    fn concat_reports_correct_asset_start_line() {
        let names: Vec<String> = ["a", "asset", "b"].iter().map(|s| s.to_string()).collect();
        let resolve = |n: &str| -> String {
            match n {
                "a"     => "1\n2\n3".to_string(),          // 3 行
                "asset" => "A1\nA2".to_string(),           // 2 行
                _       => "z".to_string(),
            }
        };
        let (combined, start) = concat_sources(&names, "asset", resolve);
        assert_eq!(start, 4, "アセットは 4 行目から始まる");
        let line4 = combined.lines().nth(start - 1).unwrap();
        assert_eq!(line4, "A1");
    }

    // ── 5. naga 検証（サンプルのトゥーンアセット） ─────────

    /// トゥーンアセット＋生成ディスパッチを差し込んだ連結が、3 変種すべてで
    /// naga の parse + validate を通ること。
    ///
    /// アセットを `runtime/assets/` に置かず**テストコード内の文字列リテラル**で持つのは、
    /// アセットディレクトリを読み取り専用に保つため（このリテラルは docs のサンプルにもなる）。
    #[test]
    fn toon_asset_passes_naga_validation_for_all_variants() {
        let found = detect_shade_models(TOON_ASSET);
        assert_eq!(found, [true, false, false]);
        let generated = generate_dispatch(&found);
        for variant in [Variant::RtOff, Variant::RtOn, Variant::RtBindless] {
            match prepare_variant(variant, "toon.wgsl", TOON_ASSET, &generated) {
                Ok((_src, _names, start)) => {
                    assert!(start > 1, "アセットは標準ライブラリの後に来る（{variant:?}）");
                }
                Err((msg, line, start)) => panic!(
                    "[{variant:?}] トゥーンアセットの検証に失敗: {}",
                    format_error("toon.wgsl", variant, &msg, line, start,
                                 asset_line_count(TOON_ASSET)),
                ),
            }
        }
    }

    /// 契約バージョンの宣言を読めること／無い場合は None であること。
    #[test]
    fn contract_version_declaration_is_parsed() {
        assert_eq!(parse_contract_version(TOON_ASSET), Some(1));
        assert_eq!(parse_contract_version("fn shade_model_1() {}"), None);
        assert_eq!(parse_contract_version("// @shading_contract 42\n"), Some(42));
    }

    /// 標準連結リストの差し替えが「shading_dispatch.wgsl の位置」で行われること。
    #[test]
    fn dispatch_element_is_substituted_in_place() {
        for variant in [Variant::RtOff, Variant::RtOn, Variant::RtBindless] {
            let std_list = variant.standard_sources();
            let idx = std_list.iter().position(|n| n == DISPATCH_SOURCE_NAME)
                .unwrap_or_else(|| panic!("{variant:?} の標準リストに {DISPATCH_SOURCE_NAME} が無い"));
            let subst = substitute_dispatch(&std_list, "asset.wgsl", "<gen>");
            assert_eq!(subst.len(), std_list.len() + 1);
            assert_eq!(subst[idx], "asset.wgsl");
            assert_eq!(subst[idx + 1], "<gen>");
            assert!(!subst.iter().any(|n| n == DISPATCH_SOURCE_NAME),
                "既定ディスパッチは連結から外れること（shade_surface の二重定義になる）");
        }
    }
}
