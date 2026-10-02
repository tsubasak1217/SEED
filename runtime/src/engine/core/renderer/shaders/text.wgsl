// ============================================================
//  text.wgsl — スクリーン空間テキスト描画シェーダー（距離場: 1 チャネルの SDF / MTSDF）
//
//  Group 0 : グリフアトラス テクスチャ配列（1 層 = 1 ページ。2026-10-03） + サンプラー
//    字のページ（層の番号）は頂点の `page` で運び、補間しない整数（flat）で受けて層を選ぶ（font/atlas.rs。
//    ページごとにバッチを分けない＝どのページの字も 1 回の描画で描ける）。
//    - fs_sdf   … R8 の 1 チャネルの SDF（em 64・spread 8。font/rasterizer.rs。2026-10-01 までの方式。A/B と退避に残す）
//    - fs_mtsdf … RGBA8 の MTSDF（em 40〜64・spread = 0.125 em。font/msdf/。2026-10-02 からの既定）。
//                 赤・緑・青 = 輪郭から作った MSDF（3 つの中央値が形。拡大しても角が立つ）、アルファ = 真の SDF。
//  【字ごとの解像度（頂点の field_em）】距離場の片側の幅はどの字も 0.125 em（`TEXT_FIELD_SPREAD_EM`）。字ごとの解像度
//  （em あたりのテクセル数。SDF は 64、MTSDF は輪郭の長い字ほど大きく 40〜64）を頂点で運び、
//  1 テクセルあたりの値の変化 = 0.5 ÷ (0.125 × field_em)、画面の上の文字の大きさ = field_em ÷ 1 画素のテクセル数 を求める。
//  どちらの入口も同じ頂点の形・同じ塗り方（shade）を使う。距離場の種類は FontConfig（font/field_settings.rs）で決まり、
//  パイプラインを作るときに入口を選ぶ（font/pipeline.rs）。
//
//  頂点座標は NDC (-1..1) で渡す（スクリーン空間）。
//  アトラス値は距離場: 0.5 = エッジ、>0.5 = 内側、<0.5 = 外側。
//  値は「字の縁までの距離」で、1 テクセルあたり 0.5 / spread だけ変わる
//  （SDF は 2026-10-01 に「反対側の画素の中心まで」から改めた。MTSDF は輪郭からの正確な距離）。
//
//  【平滑化の幅（2026-10-01 の直し。docs/ui_components.md §12）】
//  1 画素が何テクセルかを **UV の画面空間の微分** から求め、±0.5 画素（`TEXT_AA_HALF_WIDTH_PX`）で
//  直線に塗る（被覆率 = 画素の中心から縁までの画面の距離 + 0.5 を 0..1 に収めたもの）。
//  以前は距離場の値そのものの微分 fwidth(d) を幅にしていたが、1 画素より細い横画の上では
//  2×2 の画素の組が尾根（線の真ん中）を挟むと fwidth(d) ≒ 0 になって閾値の切り捨てに化け、
//  画素の中心の間に落ちた横画がまるごと消えていた（PC の等倍の「ー」「−」・「ス」→「メ」）。
//  UV はクアッドの中で線形なので、微分が線の形に左右されない。MTSDF でも同じ規則で塗る。
//
//  【MTSDF の中央値と真の SDF の使い分け（docs/ui_components.md §12.9）】
//  - 形（本体の縁）: 画面の 1 画素がアトラスの何テクセルか `t` で切り替える。拡大（t ≤ `TEXT_MTSDF_MEDIAN_FULL_TEXELS_PER_PX`）は
//    中央値だけ（角が立つ）、縮小（t ≥ `TEXT_MTSDF_TRUE_FULL_TEXELS_PER_PX`）は真の SDF だけ、間は直線で混ぜる。
//  - 縁取りの外側の縁・ぼかしの影: 真の SDF（真の距離で外へ広げる＝角は丸い。1 チャネルの SDF と同じ意味）。
//  - 太さ（Weight）: 正は「形 ∪ 真の SDF で太らせた形」、負は「形 ∩ 真の SDF で細らせた形」（太さの意味は真の距離のまま。
//    中央値の疑似距離で太らせると角が尖って伸びるため）。0 なら形そのもの。
//  1 チャネルの SDF では形も真の SDF も同じ値なので、2026-10-01 の塗り方と同じ結果になる。
//
//  【小さな文字の太らせ（`small_text_dilation_px`）】
//  画面の上の文字の大きさ（SEED の文字の大きさの単位 = 書体の ascent − descent）が小さいほど、
//  縁を最大 `TEXT_SMALL_DILATE_MAX_PX` 画素だけ外へ寄せる（しきい値を下げる）。
//  1 画素を切る細い横画が 2 行に割れても薄くなりすぎないようにするため。大きい字（実機の 2.625 倍など）は 0。
//
//  縁取り（アウトライン）は「エッジより outline_dist だけ外側」を
//  もう一段同じ式で塗り、本体をその上へ source-over 合成して作る。
//
//  【太さ（weight_dist）】
//  SDF のしきい値そのものを 0.5 − weight_dist へずらす。正で太く・負で細くなる。
//  縁取りのしきい値も同じだけずれるので、太さを変えても縁取りの太さは保たれる。
//
//  【ぼかし（softness）】
//  平滑化の半分の幅へ加算する追加の幅（SDF の値の単位）。ドロップシャドウ用。
//  ぼかしのある影だけは従来どおり smoothstep の S 字で塗る（直線だと端に折れ目が見える）。
//
//  【CPU の写し】font/text_aa_tests.rs が同じ式を CPU で再現して、細い横画が消えないこと・MTSDF の切り替えを試験する。
//  定数はそのテストがこのファイルから読み、font/sdf.rs・font/msdf/params.rs の定数との一致も確かめる
//  （式を変えるときは両方直すこと）。
// ============================================================

@group(0) @binding(0) var atlas      : texture_2d_array<f32>;
@group(0) @binding(1) var atlas_samp : sampler;

// ── 定数（マジックナンバー禁止）────────────────────────────────

const TEXT_SDF_EDGE: f32 = 0.5;              // 距離場のエッジ値（SDF・MTSDF 共通）
// 距離場の片側の幅（em 単位。= font/sdf.rs の SDF_SPREAD_EM・font/msdf/params.rs の MTSDF_SPREAD_EM）。
// エッジ 0.5 からこの距離で 0 / 1 に届く。1 テクセルあたりの値の変化 = TEXT_SDF_EDGE ÷ (これ × 頂点の field_em)
const TEXT_FIELD_SPREAD_EM: f32 = 0.125;
// MTSDF の形: 1 画素がこのテクセル数以下なら中央値だけ、TRUE_FULL 以上なら真の SDF だけ、間は直線で混ぜる
// （参照のラスタとの誤差の計測で、中央値は 1 画素 2.4 テクセル〈em 40 の 17 px〉より拡大で良く、3.3 テクセル〈12 px〉では真の SDF が少し良い。§12.9）
const TEXT_MTSDF_MEDIAN_FULL_TEXELS_PER_PX: f32 = 2.0;
const TEXT_MTSDF_TRUE_FULL_TEXELS_PER_PX: f32 = 3.5;
const TEXT_AA_HALF_WIDTH_PX: f32 = 0.5;      // 平滑化の半分の幅（画面の画素。縁の前後 ±0.5 画素 = 1 画素の傾き）
const TEXT_COVERAGE_AT_EDGE: f32 = 0.5;      // 縁の真上の画素の被覆率（直線の傾きの真ん中）
const TEXT_RMS_HALF: f32 = 0.5;              // x・y の微分の二乗平均の 1/2（sprite_shape.wgsl の pixel_width と同じ式）
const TEXT_MIN_AA_WIDTH: f32 = 0.0001;       // 0 除算の回避の下限（SDF の値の単位）
const TEXT_MIN_TEXELS_PER_PX: f32 = 0.000001; // 退化したクアッド（微分 0）での 0 除算の回避
const TEXT_ALPHA_EPSILON: f32 = 0.003;       // これ未満は discard
// 小さな文字の太らせ: 画面の上の文字の大きさがこれ以下なら最大、これ以上なら 0、間は直線でつなぐ
const TEXT_SMALL_DILATE_MAX_PX: f32 = 0.1;   // 縁を外へ寄せる最大の量（画面の画素）
const TEXT_SMALL_DILATE_FULL_SIZE_PX: f32 = 16.0; // 最大の量で太らせる文字の大きさ（画面の画素）
const TEXT_SMALL_DILATE_NONE_SIZE_PX: f32 = 24.0; // これより大きい字は太らせない（画面の画素）

// ── 頂点入出力 ────────────────────────────────────────────────

struct VertIn {
    @location(0) position      : vec3<f32>,
    @location(1) uv            : vec2<f32>,
    @location(2) color         : vec4<f32>,
    @location(3) outline_color : vec4<f32>,
    @location(4) outline_dist  : f32,
    @location(5) weight_dist   : f32,
    @location(6) softness      : f32,
    @location(7) field_em      : f32,
    @location(8) page          : u32,
}

struct VertOut {
    @builtin(position) clip_pos      : vec4<f32>,
    @location(0)       uv            : vec2<f32>,
    @location(1)       color         : vec4<f32>,
    @location(2)       outline_color : vec4<f32>,
    // クアッド内で定数なので補間しても値は変わらない（varying で運ぶだけ）。
    @location(3)       outline_dist  : f32,
    @location(4)       weight_dist   : f32,
    @location(5)       softness      : f32,
    // このグリフの距離場の解像度（em あたりのテクセル数）。クアッド内で定数
    @location(6)       field_em      : f32,
    // このグリフのアトラスのページ（テクスチャ配列の層）。整数なので補間しない
    @location(7) @interpolate(flat) page : u32,
}

// ── 頂点シェーダー ────────────────────────────────────────────

@vertex
fn vs_main(in: VertIn) -> VertOut {
    var out : VertOut;
    // 入力は NDC 座標（スクリーン空間）そのまま clip space へ
    out.clip_pos      = vec4<f32>(in.position.xy, 0.0, 1.0);
    out.uv            = in.uv;
    out.color         = in.color;
    out.outline_color = in.outline_color;
    out.outline_dist  = in.outline_dist;
    out.weight_dist   = in.weight_dist;
    out.softness      = in.softness;
    out.field_em      = in.field_em;
    out.page          = in.page;
    return out;
}

// ── 塗り方の部品 ──────────────────────────────────────────────

/// 画面の 1 画素がアトラスの何テクセルか（UV の x・y の微分の二乗平均。分岐の前に呼ぶこと）。
/// どのページも同じ大きさなので、テクスチャ配列の 1 層の大きさで換算する。
fn texels_per_pixel(uv: vec2<f32>) -> f32 {
    let tex_size = vec2<f32>(textureDimensions(atlas));
    let dx = dpdx(uv) * tex_size;
    let dy = dpdy(uv) * tex_size;
    return sqrt((dot(dx, dx) + dot(dy, dy)) * TEXT_RMS_HALF);
}

/// 小さな文字で縁を外へ寄せる量（画面の画素）。`size_px` は画面の上の文字の大きさ。
fn small_text_dilation_px(size_px: f32) -> f32 {
    let t = clamp(
        (TEXT_SMALL_DILATE_NONE_SIZE_PX - size_px) / (TEXT_SMALL_DILATE_NONE_SIZE_PX - TEXT_SMALL_DILATE_FULL_SIZE_PX),
        0.0,
        1.0,
    );
    return TEXT_SMALL_DILATE_MAX_PX * t;
}

/// 縁 `edge` の被覆率。`half_w` は平滑化の半分の幅（SDF の値の単位。ぼかし込み）。
///
/// ぼかし無し: 縁の前後 ±half_w の直線（画素の中心から縁までの距離 + 0.5 を 0..1 に収めた値）。
/// ぼかし有り（影）: 従来どおりの smoothstep。
fn edge_coverage(d: f32, edge: f32, half_w: f32, softness: f32) -> f32 {
    // 傾きの全体の幅 = 半分の幅の 2 倍。縁の真上で TEXT_COVERAGE_AT_EDGE、前後 half_w で 0 と 1
    let linear_a = clamp((d - edge) / (2.0 * half_w) + TEXT_COVERAGE_AT_EDGE, 0.0, 1.0);
    let smooth_a = smoothstep(edge - half_w, edge + half_w, d);
    return select(linear_a, smooth_a, softness > 0.0);
}

/// 3 つの値の中央値（MSDF の形の距離）。
fn median3(a: f32, b: f32, c: f32) -> f32 {
    return max(min(a, b), min(max(a, b), c));
}

/// MTSDF の形に真の SDF をどれだけ混ぜるか（0 = 中央値だけ・1 = 真の SDF だけ）。`texels` は 1 画素のテクセル数。
fn mtsdf_true_weight(texels: f32) -> f32 {
    return clamp(
        (texels - TEXT_MTSDF_MEDIAN_FULL_TEXELS_PER_PX) / (TEXT_MTSDF_TRUE_FULL_TEXELS_PER_PX - TEXT_MTSDF_MEDIAN_FULL_TEXELS_PER_PX),
        0.0,
        1.0,
    );
}

/// 本体・縁取りを塗った色（事前に乗算しない RGBA。alpha が小さければ呼び出し側が discard する）。
///
/// - `d_shape` : 本体の縁の形の距離（SDF はその値・MTSDF は中央値と真の SDF を混ぜた値）
/// - `d_true`  : 真の SDF（縁取りの外側・ぼかしの影・太さに使う。SDF は d_shape と同じ値）
/// - `texels`  : 画面の 1 画素がアトラスの何テクセルか
/// 1 テクセルの値の変化と焼いた文字の大きさは、頂点の field_em（このグリフの em あたりのテクセル数）から求める。
fn shade(d_shape: f32, d_true: f32, texels: f32, in: VertOut) -> vec4<f32> {
    // 1 テクセル進むと値がいくつ変わるか（片側の幅 = 0.125 em = 0.125 × field_em テクセル）。
    let value_per_texel = TEXT_SDF_EDGE / (TEXT_FIELD_SPREAD_EM * in.field_em);
    let em_px = in.field_em;
    // 画面の 1 画素ぶんの値の変化（UV の微分から。線の形に左右されない）。
    let value_per_px = texels * value_per_texel;
    // 平滑化の半分の幅。softness（影のぼかし）は幅への加算として効かせる。
    let w = max(value_per_px * TEXT_AA_HALF_WIDTH_PX, TEXT_MIN_AA_WIDTH) + in.softness;

    // 小さな文字は画面の画素で決めた量だけ縁を外へ寄せる（画面の上の文字の大きさ = 焼いた em ÷ 1 画素のテクセル数）。
    let dilation = small_text_dilation_px(em_px / texels) * value_per_px;
    let base_edge = TEXT_SDF_EDGE - dilation;
    // 太さ調整: しきい値を weight_dist だけ外側（小さい値）へずらす。正 = 太い。0 で従来どおり。
    let edge = base_edge - in.weight_dist;

    // 本体: 形の縁（太さ 0）と、真の SDF で太らせた・細らせた縁を合わせる（冒頭の【使い分け】）。
    let shape_a = edge_coverage(d_shape, base_edge, w, in.softness);
    let true_a = edge_coverage(d_true, edge, w, in.softness);
    let weighted_a = select(min(shape_a, true_a), max(shape_a, true_a), in.weight_dist > 0.0);
    let body_a = select(weighted_a, shape_a, in.weight_dist == 0.0);
    // ぼかしの影は真の SDF だけで塗る（丸く広がる）。
    let fill_a = select(body_a, true_a, in.softness > 0.0);
    // 縁取り（エッジより outline_dist だけ外側まで。真の SDF で外へ広げる）。
    let outline_a = edge_coverage(d_true, edge - in.outline_dist, w, in.softness);

    let a_f = fill_a * in.color.a;
    // outline_dist <= 0 のときはアウトライン無し（同じ式だと黒い縁が出てしまう）
    let a_o = select(0.0, outline_a * in.outline_color.a, in.outline_dist > 0.0);
    let out_a = a_f + a_o * (1.0 - a_f);                 // 本体を縁の上に source-over 合成
    let rgb = (in.color.rgb * a_f + in.outline_color.rgb * a_o * (1.0 - a_f)) / max(out_a, TEXT_ALPHA_EPSILON);
    return vec4<f32>(rgb, out_a);
}

// ── フラグメントシェーダー ────────────────────────────────────

/// 1 チャネルの SDF（R8）。
@fragment
fn fs_sdf(in: VertOut) -> @location(0) vec4<f32> {
    let d = textureSample(atlas, atlas_samp, in.uv, in.page).r;
    let texels = max(texels_per_pixel(in.uv), TEXT_MIN_TEXELS_PER_PX);
    let color = shade(d, d, texels, in);
    if color.a < TEXT_ALPHA_EPSILON { discard; }
    return color;
}

/// MTSDF（RGBA8。赤・緑・青 = MSDF、アルファ = 真の SDF）。
@fragment
fn fs_mtsdf(in: VertOut) -> @location(0) vec4<f32> {
    let s = textureSample(atlas, atlas_samp, in.uv, in.page);
    let texels = max(texels_per_pixel(in.uv), TEXT_MIN_TEXELS_PER_PX);
    // 形の距離: 拡大では中央値（角が立つ）、縮小では真の SDF（小さな文字の滑らかさ）。
    let d_shape = mix(median3(s.r, s.g, s.b), s.a, mtsdf_true_weight(texels));
    let color = shade(d_shape, s.a, texels, in);
    if color.a < TEXT_ALPHA_EPSILON { discard; }
    return color;
}
