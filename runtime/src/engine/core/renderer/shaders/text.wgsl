// ============================================================
//  text.wgsl — スクリーン空間テキスト描画シェーダー（SDF 固定）
//
//  Group 0 : グリフアトラス テクスチャ (R8 SDF) + サンプラー
//
//  頂点座標は NDC (-1..1) で渡す（スクリーン空間）。
//  アトラス値は距離場: 0.5 = エッジ、>0.5 = 内側、<0.5 = 外側。
//  値は「字の縁（2 値の境目）までの距離」で、1 テクセルあたり 0.5 / spread だけ変わる
//  （焼く側は font/rasterizer.rs の generate_sdf。2026-10-01 に「反対側の画素の中心まで」から改めた）。
//
//  【平滑化の幅（2026-10-01 の直し。docs/ui_components.md §12）】
//  1 画素が何テクセルかを **UV の画面空間の微分** から求め、±0.5 画素（`TEXT_AA_HALF_WIDTH_PX`）で
//  直線に塗る（被覆率 = 画素の中心から縁までの画面の距離 + 0.5 を 0..1 に収めたもの）。
//  以前は距離場の値そのものの微分 fwidth(d) を幅にしていたが、1 画素より細い横画の上では
//  2×2 の画素の組が尾根（線の真ん中）を挟むと fwidth(d) ≒ 0 になって閾値の切り捨てに化け、
//  画素の中心の間に落ちた横画がまるごと消えていた（PC の等倍の「ー」「−」・「ス」→「メ」）。
//  UV はクアッドの中で線形なので、微分が線の形に左右されない。
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
//  【CPU の写し】font/text_aa_tests.rs が同じ式を CPU で再現して、細い横画が消えないことを試験する。
//  定数はそのテストがこのファイルから読み、font/sdf.rs の定数との一致も確かめる（式を変えるときは両方直すこと）。
// ============================================================

@group(0) @binding(0) var atlas      : texture_2d<f32>;
@group(0) @binding(1) var atlas_samp : sampler;

// ── 定数（マジックナンバー禁止）────────────────────────────────

const TEXT_SDF_EDGE: f32 = 0.5;              // SDF のエッジ値
const TEXT_SDF_EM_PX: f32 = 64.0;            // SDF を焼いた文字の大きさ（テクセル。= font/sdf.rs の SDF_EM_PX）
const TEXT_SDF_SPREAD_TEXELS: f32 = 8.0;     // SDF の片側の幅（テクセル。= font/sdf.rs の SDF_SPREAD_PX）
// 1 テクセル進むと SDF の値がいくつ変わるか（エッジ 0.5 から spread テクセルで 0 / 1 に届く）
const TEXT_SDF_VALUE_PER_TEXEL: f32 = TEXT_SDF_EDGE / TEXT_SDF_SPREAD_TEXELS;
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
    return out;
}

// ── 塗り方の部品 ──────────────────────────────────────────────

/// 画面の 1 画素がアトラスの何テクセルか（UV の x・y の微分の二乗平均。分岐の前に呼ぶこと）。
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

// ── フラグメントシェーダー ────────────────────────────────────

@fragment
fn fs_main(in: VertOut) -> @location(0) vec4<f32> {
    // 距離場のサンプルと、画面の 1 画素ぶんの SDF の値の変化（UV の微分から。線の形に左右されない）。
    let d = textureSample(atlas, atlas_samp, in.uv).r;
    let texels = max(texels_per_pixel(in.uv), TEXT_MIN_TEXELS_PER_PX);
    let value_per_px = texels * TEXT_SDF_VALUE_PER_TEXEL;
    // 平滑化の半分の幅。softness（影のぼかし）は幅への加算として効かせる。
    let w = max(value_per_px * TEXT_AA_HALF_WIDTH_PX, TEXT_MIN_AA_WIDTH) + in.softness;

    // 太さ調整: しきい値を weight_dist だけ外側（小さい値）へずらす。
    // 正 = 塗る範囲が外へ広がる = 太い。weight_dist = 0 で従来どおり 0.5。
    // 小さな文字はさらに画面の画素で決めた量だけ外へ寄せる（画面の上の文字の大きさ = SDF の em ÷ 1 画素のテクセル数）。
    let dilation = small_text_dilation_px(TEXT_SDF_EM_PX / texels) * value_per_px;
    let edge = TEXT_SDF_EDGE - in.weight_dist - dilation;

    // 本体（エッジより内側）と縁取り（エッジより outline_dist だけ外側まで）。
    let fill_a    = edge_coverage(d, edge, w, in.softness);
    let out_edge  = edge - in.outline_dist;
    let outline_a = edge_coverage(d, out_edge, w, in.softness);

    let a_f = fill_a * in.color.a;
    // outline_dist <= 0 のときはアウトライン無し（同じ式だと黒い縁が出てしまう）
    let a_o = select(0.0, outline_a * in.outline_color.a, in.outline_dist > 0.0);
    let out_a = a_f + a_o * (1.0 - a_f);                 // 本体を縁の上に source-over 合成
    if out_a < TEXT_ALPHA_EPSILON { discard; }
    let rgb = (in.color.rgb * a_f + in.outline_color.rgb * a_o * (1.0 - a_f)) / out_a;
    return vec4<f32>(rgb, out_a);
}
