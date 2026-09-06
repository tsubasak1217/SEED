// ============================================================
//  text.wgsl — スクリーン空間テキスト描画シェーダー（SDF 固定）
//
//  Group 0 : グリフアトラス テクスチャ (R8 SDF) + サンプラー
//
//  頂点座標は NDC (-1..1) で渡す（スクリーン空間）。
//  アトラス値は距離場: 0.5 = エッジ、>0.5 = 内側、<0.5 = 外側。
//  fwidth ベースのアンチエイリアスで、拡大してもエッジが階段状にならない。
//
//  縁取り（アウトライン）は「エッジより outline_dist だけ外側」を
//  もう一段の smoothstep で塗り、本体をその上へ source-over 合成して作る。
// ============================================================

@group(0) @binding(0) var atlas      : texture_2d<f32>;
@group(0) @binding(1) var atlas_samp : sampler;

// ── 定数（マジックナンバー禁止）────────────────────────────────

const TEXT_SDF_EDGE: f32 = 0.5;          // SDF のエッジ値
const TEXT_AA_SMOOTH_SCALE: f32 = 0.5;   // fwidth に掛けるアンチエイリアス幅係数（≒1px 遷移）
const TEXT_MIN_AA_WIDTH: f32 = 0.0001;   // 0 除算・階段状エッジ回避の下限
const TEXT_ALPHA_EPSILON: f32 = 0.003;   // これ未満は discard

// ── 頂点入出力 ────────────────────────────────────────────────

struct VertIn {
    @location(0) position      : vec3<f32>,
    @location(1) uv            : vec2<f32>,
    @location(2) color         : vec4<f32>,
    @location(3) outline_color : vec4<f32>,
    @location(4) outline_dist  : f32,
}

struct VertOut {
    @builtin(position) clip_pos      : vec4<f32>,
    @location(0)       uv            : vec2<f32>,
    @location(1)       color         : vec4<f32>,
    @location(2)       outline_color : vec4<f32>,
    // クアッド内で定数なので補間しても値は変わらない（varying で運ぶだけ）。
    @location(3)       outline_dist  : f32,
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
    return out;
}

// ── フラグメントシェーダー ────────────────────────────────────

@fragment
fn fs_main(in: VertOut) -> @location(0) vec4<f32> {
    // 距離場のサンプルと、1 ピクセル相当のアンチエイリアス幅。
    let d = textureSample(atlas, atlas_samp, in.uv).r;
    let w = max(fwidth(d) * TEXT_AA_SMOOTH_SCALE, TEXT_MIN_AA_WIDTH);

    // 本体（エッジより内側）と縁取り（エッジより outline_dist だけ外側まで）。
    let fill_a    = smoothstep(TEXT_SDF_EDGE - w, TEXT_SDF_EDGE + w, d);
    let out_edge  = TEXT_SDF_EDGE - in.outline_dist;
    let outline_a = smoothstep(out_edge - w, out_edge + w, d);

    let a_f = fill_a * in.color.a;
    // outline_dist <= 0 のときはアウトライン無し（同じ式だと黒い縁が出てしまう）
    let a_o = select(0.0, outline_a * in.outline_color.a, in.outline_dist > 0.0);
    let out_a = a_f + a_o * (1.0 - a_f);                 // 本体を縁の上に source-over 合成
    if out_a < TEXT_ALPHA_EPSILON { discard; }
    let rgb = (in.color.rgb * a_f + in.outline_color.rgb * a_o * (1.0 - a_f)) / out_a;
    return vec4<f32>(rgb, out_a);
}
