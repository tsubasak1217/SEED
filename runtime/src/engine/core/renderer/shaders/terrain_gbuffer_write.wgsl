// ============================================================
// terrain_gbuffer_write.wgsl — 地形レイヤブレンド G-Buffer 書き込み（Terrain T2b）
//
// ## 役割（単一責任）
// 地形メッシュ専用の G-Buffer ジオメトリパス フラグメントシェーダ。
// 頂点カラーに載って来たスプラット重み（4 スロット）と group3 のレイヤ定義から、
// triplanar 投影でレイヤをブレンドし、合成済みの base_color / normal / roughness /
// metallic を G-Buffer へ焼く。
//
// ライティング・影・RT 反射は G-Buffer を読むライティングパスがそのまま担当するため、
// 地形専用のライティングコードは一切書かない（既存の deferred 経路に完全に乗る）。
//
// ## 連結（terrain_gbuffer.rs::terrain_gbuffer_shader_sources と一致必須）
//   ["shader_common.wgsl", "shader_static_vertex.wgsl", "terrain_gbuffer_write.wgsl"]
// surface.wgsl / surface_gather.wgsl は連結しない（Surface を経由せず直接 MRT を作るため。
// 地形はマテリアルテクスチャではなくレイヤ配列テクスチャを読むので gather_surface が使えない）。
//
// ## G-Buffer レイアウト（gbuffer_write.wgsl と同一。正典はそちら）
//   RT0 Rgba8Unorm  : albedo.rgb + occlusion.a
//   RT1 Rgba16Float : world normal.xyz + 0
//   RT2 Rgba8Unorm  : metallic.r + roughness.g + diffuse_transmission.b + 予約 a
//   RT3 Rgba16Float : emissive.rgb(HDR) + 0
//
// ## なぜ triplanar なのか
// マーチングキューブスの地形は UV 展開を持たない（頂点 UV は常に 0）。ワールド座標を
// 3 平面（YZ / XZ / XY）へ投影して法線で重み付けブレンドすることで、UV 無しのまま
// 引き伸ばしのないテクスチャリングが得られる。これが地形テクスチャリングの定番手法。
//
// ## レイヤ番号の運び方（T2b の設計判断）
// 頂点カラー（vec4）は「重み」だけを運び、**どのレイヤ番号がどのスロットに載るか**は
// チャンク単位の uniform（`u_terrain.palette`）で渡す。頂点フォーマット（mesh_vertex・
// 22 パイプライン共有）を一切変えずに、レイヤ総数を 16 へ拡張するための構成である。
//
// ## なぜ全サンプルが textureSampleGrad なのか（設計の要）
//  1. WGSL は暗黙 derivative（textureSample）を **一様制御フロー**でしか許さない。
//     明示 grad なら非一様分岐の内側でも合法にサンプルできるため、
//     「重み 0 のスロットを飛ばす」「寄与が極小の triplanar 面を飛ばす」最適化が可能になる。
//  2. 確率的タイリングはタイルごとに UV を不連続にずらすため、暗黙 derivative では
//     ずらし目に必ずミップ段差＝シームが出る。元 UV の grad を使い回すことでこれを消す。
// world UV の ddx/ddy は分岐へ入る前に一度だけ求め、各サンプル関数へ引数で渡す。
// ============================================================

// ─── レイヤ数（Rust 側と一致必須。tests が文字列一致で検証する）──────────────

/// 1 頂点／1 チャンクが同時にブレンドできるレイヤ数（＝頂点カラー成分数）。
const TERRAIN_BLEND_SLOTS: u32 = 4u;

/// layers.json に定義できるレイヤ数の上限（＝テクスチャ配列のレイヤ枚数）。
const TERRAIN_MAX_LAYERS: u32 = 16u;

// ─── detile モードコード（Rust DetileMode::to_gpu_code と一致必須）────────────

/// 単純タイリング（従来動作）。
const DETILE_MODE_NONE: u32 = 0u;
/// 六角格子の確率的タイリング（Heitz-Neyret 風）。
const DETILE_MODE_STOCHASTIC: u32 = 1u;
/// 大スケールノイズ変調＋マルチスケール重ね（安価）。
const DETILE_MODE_MACRO: u32 = 2u;

// ─── 数値の閾値・調整定数（マジックナンバー禁止）───────────────────────────

/// レイヤ重みの総和がこの値以下なら「重みなし」とみなし、スロット 0 を下地に敷く。
const TERRAIN_WEIGHT_EPSILON: f32 = 1e-5;

/// スロット重みがこの値以下なら、そのスロットのサンプルを丸ごと省略する。
/// SampleGrad を使っているので非一様分岐でも合法。最悪タップ数
/// （4 スロット × 3 面 × 3 タップ × 3 マップ = 108）を実用域まで落とすための最適化。
const TERRAIN_SLOT_WEIGHT_EPSILON: f32 = 1.0e-3;

/// triplanar の面重みがこの値以下なら、その面のサンプルを省略する（同上の理由）。
const TRIPLANAR_PLANE_EPSILON: f32 = 1.0e-3;

/// triplanar のブレンド重みが完全に 0 ベクトルへ潰れたときの下限。
const TRIPLANAR_MIN_SUM: f32 = 1e-5;

/// 確率的タイリングのブレンド鋭さ。重心座標を pow で鋭くしてから再正規化することで、
/// 3 タップが均等に混ざる領域（＝ゴースト／ぼやけ）を狭める。
const DETILE_BLEND_SHARPNESS: f32 = 7.0;

/// 確率的タイリングの六角格子スケール（2√3）。Heitz-Neyret の TriangleGrid と同値。
const HEX_GRID_SCALE: f32 = 3.4641016;
/// 正方格子 → 斜交格子への変換係数（-1/√3）。
const HEX_SKEW_XY: f32 = -0.57735027;
/// 正方格子 → 斜交格子への変換係数（2/√3）。
const HEX_SKEW_YY: f32 = 1.15470054;

/// ハッシュ乱数の sin スケール（GPU ハッシュの定番値）。
const HASH_SIN_SCALE: f32 = 43758.5453;
/// ハッシュ乱数の内積係数その 1。
const HASH_DOT_A: vec2<f32> = vec2<f32>(127.1, 311.7);
/// ハッシュ乱数の内積係数その 2。
const HASH_DOT_B: vec2<f32> = vec2<f32>(269.5, 183.3);

/// macro モードの第 2 スケール比。元スケールに対して十分素な比にして周期の重なりを避ける。
const MACRO_DETAIL_RATIO: f32 = 0.13;
/// macro モードの明度変調ノイズのスケール（ワールド UV に対する周期の逆数）。
const MACRO_NOISE_SCALE: f32 = 0.017;
/// macro モードの明度変調の振れ幅（±この値）。
const MACRO_MODULATION_RANGE: f32 = 0.45;
/// macro モードで元スケールと第 2 スケールを混ぜる比率（0.5 = 等分）。
const MACRO_DETAIL_MIX: f32 = 0.5;
/// 第 2 スケールを乗算したときの明度落ちを補正するゲイン（平均 0.5 のテクスチャ想定）。
const MACRO_DETAIL_GAIN: f32 = 2.0;
/// value ノイズ（0..1）を -1..1 の変調量へ写すための係数とバイアス。
const MACRO_NOISE_SCALE_TO_SIGNED: f32 = 2.0;
const MACRO_NOISE_BIAS_TO_SIGNED:  f32 = 1.0;

/// roughness の下限（完全鏡面によるスペキュラ発散を防ぐ）。
const TERRAIN_ROUGHNESS_MIN: f32 = 0.04;

/// 法線マップのタンジェント空間復元係数（0..1 → -1..1）。
const NORMAL_MAP_SCALE: f32 = 2.0;
const NORMAL_MAP_BIAS:  f32 = 1.0;

// ─── group(3): 地形レイヤ定義 ─────────────────────────────────────────────
// group0=camera / group1=model / group2=material は shader_common.wgsl の宣言を
// そのまま使う（パイプラインレイアウトも MeshPipeline のものを借りる）。
// group2 のマテリアルテクスチャは地形では参照しないが、レイアウト互換のため残る。
//
// 【なぜ binding_array（bindless）ではなく texture_2d_array なのか】
// group3 には uniform（レイヤパラメータ）が同居する必要があるが、wgpu では
// binding_array と uniform を同一バインドグループへ置けない（実機で BGL 構築時に
// パニックする既往あり）。group0〜3 はすべて埋まっており uniform を逃がす先が無い。
// texture_2d_array なら uniform と同居でき、機能フラグ（TEXTURE_BINDING_ARRAY）も
// 不要で全 GPU で動く。代償は「全レイヤが同一解像度・同一フォーマット」だが、
// CPU 側で共通解像度へリサイズして束ねることで吸収している。

/// レイヤ 1 枚ぶんのパラメータ（Rust TerrainLayerParamsGpu と同一レイアウト）。
struct TerrainLayerParams {
    /// rgb = ベースカラー係数（リニア）, a = ベースカラーテクスチャの有無（0 or 1）
    base_color: vec4<f32>,
    /// x=metallic, y=roughness, z=triplanar UV スケール, w=detile モードコード
    surface: vec4<f32>,
    /// x=法線テクスチャの有無, y=ラフネステクスチャの有無, z=detile 強度, w=予約
    extra: vec4<f32>,
}

/// レイヤ定義一式（Rust TerrainLayerUniformGpu と同一レイアウト）。
struct TerrainLayerUniform {
    /// レイヤ定義本体（最大 TERRAIN_MAX_LAYERS 層）。
    layers: array<TerrainLayerParams, TERRAIN_MAX_LAYERS>,
    /// このチャンクが使うレイヤ番号 4 つ（頂点カラー成分 → レイヤ番号の対応表）。
    palette: vec4<u32>,
    /// x = triplanar ブレンドの鋭さ, y = 有効レイヤ数, zw = 予約
    params: vec4<f32>,
}

@group(3) @binding(0) var<uniform> u_terrain: TerrainLayerUniform;
@group(3) @binding(1) var s_terrain: sampler;
/// ベースカラー配列（sRGB。サンプル時にリニアへ展開される）。
@group(3) @binding(2) var t_terrain_base: texture_2d_array<f32>;
/// 法線マップ配列（リニア。タンジェント空間 XYZ を 0..1 で格納）。
@group(3) @binding(3) var t_terrain_normal: texture_2d_array<f32>;
/// ラフネス配列（リニア。R チャンネルのみ使用）。
@group(3) @binding(4) var t_terrain_rough: texture_2d_array<f32>;

/// 地形 G-Buffer の MRT 出力（gbuffer_write.wgsl の GBufferOut と同一レイアウト）。
struct TerrainGBufferOut {
    @location(0) albedo_occ: vec4<f32>,
    @location(1) normal:     vec4<f32>,
    @location(2) mr:         vec4<f32>,
    @location(3) emissive:   vec4<f32>,
}

// ============================================================
//  ハッシュ / ノイズ
// ============================================================

/// 2D → 2D のハッシュ乱数（0..1）。確率的タイリングのセルオフセット生成に使う。
fn terrain_hash2(p: vec2<f32>) -> vec2<f32> {
    let q = vec2<f32>(dot(p, HASH_DOT_A), dot(p, HASH_DOT_B));
    return fract(sin(q) * HASH_SIN_SCALE);
}

/// 2D → 1D のハッシュ乱数（0..1）。
fn terrain_hash1(p: vec2<f32>) -> f32 {
    return fract(sin(dot(p, HASH_DOT_A)) * HASH_SIN_SCALE);
}

/// 双一次補間の value ノイズ（0..1）。macro モードの大スケール明度変調に使う。
fn terrain_value_noise(p: vec2<f32>) -> f32 {
    let i = floor(p);
    let f = fract(p);
    // smoothstep で格子境界の折れを消す。
    let u = f * f * (3.0 - 2.0 * f);
    let a = terrain_hash1(i);
    let b = terrain_hash1(i + vec2<f32>(1.0, 0.0));
    let c = terrain_hash1(i + vec2<f32>(0.0, 1.0));
    let d = terrain_hash1(i + vec2<f32>(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

// ============================================================
//  六角格子（Heitz-Neyret 風 確率的タイリング）
// ============================================================

/// 六角格子の 3 頂点セルとその重心座標。
struct HexCells {
    /// 重心座標（w.x + w.y + w.z == 1、すべて非負）。
    w:  vec3<f32>,
    /// セル 1 の格子 ID。
    v1: vec2<f32>,
    /// セル 2 の格子 ID。
    v2: vec2<f32>,
    /// セル 3 の格子 ID。
    v3: vec2<f32>,
}

/// UV を六角（三角）格子へ写し、最近傍 3 セルとその重心座標を返す。
///
/// Heitz & Neyret "High-Performance By-Example Noise using a Histogram-Preserving
/// Blending Operator" (2018) の TriangleGrid をそのまま WGSL へ移植したもの。
/// Rust 側 `hex_triangle_grid`（terrain_gbuffer.rs のテスト用参照実装）と同一式であること。
fn terrain_hex_cells(uv_in: vec2<f32>) -> HexCells {
    let uv = uv_in * HEX_GRID_SCALE;
    // 正方格子 → 斜交（三角）格子。
    let skewed = vec2<f32>(uv.x + HEX_SKEW_XY * uv.y, HEX_SKEW_YY * uv.y);
    let base_id = floor(skewed);
    let f = fract(skewed);
    let third = 1.0 - f.x - f.y;

    var out: HexCells;
    if third > 0.0 {
        // 下側の三角形。
        out.w  = vec3<f32>(third, f.y, f.x);
        out.v1 = base_id;
        out.v2 = base_id + vec2<f32>(0.0, 1.0);
        out.v3 = base_id + vec2<f32>(1.0, 0.0);
    } else {
        // 上側の三角形（重心座標は反転して非負にする）。
        out.w  = vec3<f32>(-third, 1.0 - f.y, 1.0 - f.x);
        out.v1 = base_id + vec2<f32>(1.0, 1.0);
        out.v2 = base_id + vec2<f32>(1.0, 0.0);
        out.v3 = base_id + vec2<f32>(0.0, 1.0);
    }
    return out;
}

/// 重心座標を鋭くしてから再正規化する（3 タップが均等に混ざるぼやけ領域を狭める）。
fn terrain_sharpen_weights(w: vec3<f32>) -> vec3<f32> {
    let s = pow(max(w, vec3<f32>(0.0)), vec3<f32>(DETILE_BLEND_SHARPNESS));
    let sum = s.x + s.y + s.z;
    if sum < TRIPLANAR_MIN_SUM {
        return vec3<f32>(1.0, 0.0, 0.0);
    }
    return s / sum;
}

// ============================================================
//  detile 付き 2D 配列サンプル（すべて textureSampleGrad）
// ============================================================

/// UV とその画面微分（明示 grad）をまとめたもの。分岐前に一度だけ作って持ち回る。
struct TerrainUv {
    uv:  vec2<f32>,
    ddx: vec2<f32>,
    ddy: vec2<f32>,
}

/// ベースカラー配列を detile モードに従ってサンプルする。
///
/// 【限界の明示】stochastic モードは Heitz-Neyret の**ヒストグラム保存（inverse CDF）を
/// 実装していない**。3 タップを線形ブレンドしているだけなので、重なり領域では
/// コントラスト（分散）が理論値より低下し、わずかに眠い絵になる。
/// ヒストグラム保存には前処理でガウス化した T(input) テクスチャと逆変換 LUT が要り、
/// アセットパイプラインの追加が必要なため T2b の範囲外とした。
fn terrain_sample_base(layer: u32, t: TerrainUv, mode: u32, strength: f32) -> vec3<f32> {
    let plain = textureSampleGrad(t_terrain_base, s_terrain, t.uv, layer, t.ddx, t.ddy).rgb;
    if mode == DETILE_MODE_NONE {
        return plain;
    }
    if mode == DETILE_MODE_STOCHASTIC {
        let cells = terrain_hex_cells(t.uv);
        let bw = terrain_sharpen_weights(cells.w);
        // セルごとにハッシュでオフセットした UV を、**元 UV の grad** でサンプルする。
        // 暗黙 derivative だとオフセットの不連続がミップ段差＝シームとして出る。
        let s1 = textureSampleGrad(t_terrain_base, s_terrain, t.uv + terrain_hash2(cells.v1), layer, t.ddx, t.ddy).rgb;
        let s2 = textureSampleGrad(t_terrain_base, s_terrain, t.uv + terrain_hash2(cells.v2), layer, t.ddx, t.ddy).rgb;
        let s3 = textureSampleGrad(t_terrain_base, s_terrain, t.uv + terrain_hash2(cells.v3), layer, t.ddx, t.ddy).rgb;
        let blended = s1 * bw.x + s2 * bw.y + s3 * bw.z;
        return mix(plain, blended, clamp(strength, 0.0, 1.0));
    }
    // DETILE_MODE_MACRO: 第 2 スケールを重ね、大スケール value ノイズで明度を変調する。
    let detail = textureSampleGrad(
        t_terrain_base, s_terrain,
        t.uv * MACRO_DETAIL_RATIO, layer,
        t.ddx * MACRO_DETAIL_RATIO, t.ddy * MACRO_DETAIL_RATIO,
    ).rgb;
    let multiscale = mix(plain, plain * detail * MACRO_DETAIL_GAIN, MACRO_DETAIL_MIX);
    let n = terrain_value_noise(t.uv * MACRO_NOISE_SCALE);
    let signed_n = n * MACRO_NOISE_SCALE_TO_SIGNED - MACRO_NOISE_BIAS_TO_SIGNED;
    let modulated = multiscale * (1.0 + signed_n * MACRO_MODULATION_RANGE);
    return mix(plain, modulated, clamp(strength, 0.0, 1.0));
}

/// 法線マップ配列をサンプルしてタンジェント空間法線（-1..1）を返す。
/// detile は base_color ほど目立たないため、stochastic のみ 3 タップ、macro は素通し。
fn terrain_sample_normal(layer: u32, t: TerrainUv, mode: u32, strength: f32) -> vec3<f32> {
    let plain = textureSampleGrad(t_terrain_normal, s_terrain, t.uv, layer, t.ddx, t.ddy).xyz;
    var enc = plain;
    if mode == DETILE_MODE_STOCHASTIC {
        let cells = terrain_hex_cells(t.uv);
        let bw = terrain_sharpen_weights(cells.w);
        let s1 = textureSampleGrad(t_terrain_normal, s_terrain, t.uv + terrain_hash2(cells.v1), layer, t.ddx, t.ddy).xyz;
        let s2 = textureSampleGrad(t_terrain_normal, s_terrain, t.uv + terrain_hash2(cells.v2), layer, t.ddx, t.ddy).xyz;
        let s3 = textureSampleGrad(t_terrain_normal, s_terrain, t.uv + terrain_hash2(cells.v3), layer, t.ddx, t.ddy).xyz;
        enc = mix(plain, s1 * bw.x + s2 * bw.y + s3 * bw.z, clamp(strength, 0.0, 1.0));
    }
    return enc * NORMAL_MAP_SCALE - NORMAL_MAP_BIAS;
}

/// ラフネス配列（R チャンネル）をサンプルする。
fn terrain_sample_roughness(layer: u32, t: TerrainUv) -> f32 {
    return textureSampleGrad(t_terrain_rough, s_terrain, t.uv, layer, t.ddx, t.ddy).r;
}

// ============================================================
//  triplanar
// ============================================================

/// triplanar ブレンド重みを法線から求める（総和 1 に正規化済み）。
///
/// `sharpness` が大きいほど支配的な軸へ寄る（＝平面の継ぎ目が硬くなる）。
fn triplanar_blend_weights(n: vec3<f32>, sharpness: f32) -> vec3<f32> {
    var w = pow(abs(n), vec3<f32>(sharpness));
    let sum = w.x + w.y + w.z;
    // 縮退（法線が 0 ベクトル）時は XZ 平面（真上投影）へフォールバックする。
    if sum < TRIPLANAR_MIN_SUM {
        return vec3<f32>(0.0, 1.0, 0.0);
    }
    return w / sum;
}

/// 3 平面ぶんの UV と明示 grad をまとめて作る。
///
/// world_pos の画面微分は**必ず一様制御フローで**（＝フラグメント関数の先頭で）
/// 求めた値を渡すこと。ここでは掛け算しかしない。
struct TriplanarUv {
    /// YZ 平面（法線 X 軸）の UV。
    x: TerrainUv,
    /// XZ 平面（法線 Y 軸）の UV。
    y: TerrainUv,
    /// XY 平面（法線 Z 軸）の UV。
    z: TerrainUv,
}

/// ワールド座標と UV スケールから 3 平面の UV／grad を組み立てる。
fn make_triplanar_uv(p: vec3<f32>, dpdx_p: vec3<f32>, dpdy_p: vec3<f32>, s: f32) -> TriplanarUv {
    var o: TriplanarUv;
    o.x = TerrainUv(p.zy * s, dpdx_p.zy * s, dpdy_p.zy * s);
    o.y = TerrainUv(p.xz * s, dpdx_p.xz * s, dpdy_p.xz * s);
    o.z = TerrainUv(p.xy * s, dpdx_p.xy * s, dpdy_p.xy * s);
    return o;
}

/// 1 レイヤぶんの triplanar ベースカラー。寄与が極小の面は丸ごと省略する
/// （SampleGrad なので非一様分岐でも合法。最悪タップ数を実用域へ落とすための最適化）。
fn triplanar_base_color(layer: u32, uv: TriplanarUv, bw: vec3<f32>, mode: u32, strength: f32) -> vec3<f32> {
    var acc = vec3<f32>(0.0);
    if bw.x > TRIPLANAR_PLANE_EPSILON {
        acc += terrain_sample_base(layer, uv.x, mode, strength) * bw.x;
    }
    if bw.y > TRIPLANAR_PLANE_EPSILON {
        acc += terrain_sample_base(layer, uv.y, mode, strength) * bw.y;
    }
    if bw.z > TRIPLANAR_PLANE_EPSILON {
        acc += terrain_sample_base(layer, uv.z, mode, strength) * bw.z;
    }
    return acc;
}

/// 1 レイヤぶんの triplanar ラフネス。
fn triplanar_roughness(layer: u32, uv: TriplanarUv, bw: vec3<f32>) -> f32 {
    var acc = 0.0;
    if bw.x > TRIPLANAR_PLANE_EPSILON { acc += terrain_sample_roughness(layer, uv.x) * bw.x; }
    if bw.y > TRIPLANAR_PLANE_EPSILON { acc += terrain_sample_roughness(layer, uv.y) * bw.y; }
    if bw.z > TRIPLANAR_PLANE_EPSILON { acc += terrain_sample_roughness(layer, uv.z) * bw.z; }
    return acc;
}

/// 1 レイヤぶんの triplanar 法線をワールド空間へ持っていく。
///
/// 手法: **whiteout ブレンド**（Ben Golus "Normal Mapping for a Triplanar Shader"）。
/// 接空間（タンジェント／バイタンジェント）を厳密に構築せず、各投影面の
/// タンジェント法線 XY を頂点法線の対応成分へ加算し、Z に頂点法線の符号を掛けて
/// 面ごとの向きを揃えてから、triplanar 重みで合成する近似である。
/// 地形は UV 展開を持たず正しい接空間が存在しないため、この近似が事実上の標準解。
fn triplanar_normal(
    layer: u32, uv: TriplanarUv, bw: vec3<f32>, n: vec3<f32>, mode: u32, strength: f32,
) -> vec3<f32> {
    var acc = vec3<f32>(0.0);
    if bw.x > TRIPLANAR_PLANE_EPSILON {
        let t = terrain_sample_normal(layer, uv.x, mode, strength);
        // uv.x は p.zy なので、タンジェント XY はワールド ZY に対応する。
        let w = vec3<f32>(abs(t.z) * n.x, t.y + n.y, t.x + n.z);
        acc += w * bw.x;
    }
    if bw.y > TRIPLANAR_PLANE_EPSILON {
        let t = terrain_sample_normal(layer, uv.y, mode, strength);
        // uv.y は p.xz なので、タンジェント XY はワールド XZ に対応する。
        let w = vec3<f32>(t.x + n.x, abs(t.z) * n.y, t.y + n.z);
        acc += w * bw.y;
    }
    if bw.z > TRIPLANAR_PLANE_EPSILON {
        let t = terrain_sample_normal(layer, uv.z, mode, strength);
        // uv.z は p.xy なので、タンジェント XY はワールド XY に対応する。
        let w = vec3<f32>(t.x + n.x, t.y + n.y, abs(t.z) * n.z);
        acc += w * bw.z;
    }
    let len = length(acc);
    if len < TRIPLANAR_MIN_SUM {
        return n;
    }
    return acc / len;
}

// ============================================================
//  フラグメント本体
// ============================================================

/// 地形レイヤをブレンドして G-Buffer へ焼く。
@fragment
fn fs_terrain_gbuffer(
    in: VertexOutput,
    @builtin(front_facing) front_facing: bool,
) -> TerrainGBufferOut {
    // ── ジオメトリ法線 ──
    //   両面描画（cull_face=None）なので裏面フラグメントでは法線を反転して
    //   「見えている側の法線」に揃える（surface_gather.wgsl と同じ規約）。
    let facing_sign = select(-1.0, 1.0, front_facing);
    let geo_n = normalize(in.world_normal) * facing_sign;

    // ── ワールド座標の画面微分（必ず一様制御フローで取る）──
    //   以降のサンプルはすべて textureSampleGrad なので、暗黙 derivative は使わない。
    //   ここで一度だけ取った値を UV スケール倍して各サンプルへ配る。
    let dpdx_p = dpdx(in.world_pos);
    let dpdy_p = dpdy(in.world_pos);

    // ── スプラット重み（頂点カラー RGBA = スロット 0..3）を正規化する ──
    //   ラスタライザの重心補間で総和が 1 から僅かにずれるため必ず正規化し直す。
    var wv = max(in.color, vec4<f32>(0.0));
    let w_sum = wv.x + wv.y + wv.z + wv.w;
    if w_sum < TERRAIN_WEIGHT_EPSILON {
        // 縮退：スロット 0 を下地として敷く（黒落ち防止）。
        wv = vec4<f32>(1.0, 0.0, 0.0, 0.0);
    } else {
        wv = wv / w_sum;
    }

    // ── 動的添字で回すため配列へ落とす（vec の動的添字は var 経由が必要）──
    var slot_w: array<f32, TERRAIN_BLEND_SLOTS>;
    slot_w[0] = wv.x;
    slot_w[1] = wv.y;
    slot_w[2] = wv.z;
    slot_w[3] = wv.w;
    var palette: array<u32, TERRAIN_BLEND_SLOTS>;
    palette[0] = u_terrain.palette.x;
    palette[1] = u_terrain.palette.y;
    palette[2] = u_terrain.palette.z;
    palette[3] = u_terrain.palette.w;

    // ── triplanar のブレンド重み（3 平面）──
    let bw = triplanar_blend_weights(geo_n, u_terrain.params.x);

    // ── スロットごとにレイヤを合成する ──
    var albedo    = vec3<f32>(0.0);
    var normal    = vec3<f32>(0.0);
    var metallic  = 0.0;
    var roughness = 0.0;
    var used_w    = 0.0;

    for (var i: u32 = 0u; i < TERRAIN_BLEND_SLOTS; i = i + 1u) {
        let sw = slot_w[i];
        // 重み 0 のスロットは丸ごと省略する（SampleGrad なので非一様分岐でも合法）。
        if sw <= TERRAIN_SLOT_WEIGHT_EPSILON {
            continue;
        }
        // パレット外のレイヤ番号は 0 番へ丸める（不正データでの範囲外読みを防ぐ）。
        let li = min(palette[i], TERRAIN_MAX_LAYERS - 1u);
        let L = u_terrain.layers[li];

        let mode     = u32(L.surface.w);
        let strength = L.extra.z;
        let uv       = make_triplanar_uv(in.world_pos, dpdx_p, dpdy_p, L.surface.z);

        // ベースカラー: テクスチャが無いレイヤは単色（T2 までの動作を維持）。
        var c = L.base_color.rgb;
        if L.base_color.a > 0.5 {
            c = c * triplanar_base_color(li, uv, bw, mode, strength);
        }
        albedo += c * sw;

        // 法線: テクスチャが無いレイヤはジオメトリ法線をそのまま使う。
        var n = geo_n;
        if L.extra.x > 0.5 {
            n = triplanar_normal(li, uv, bw, geo_n, mode, strength);
        }
        normal += n * sw;

        // ラフネス: テクスチャがあれば R チャンネルを係数へ乗算する。
        var r = L.surface.y;
        if L.extra.y > 0.5 {
            r = r * triplanar_roughness(li, uv, bw);
        }
        roughness += r * sw;
        metallic  += L.surface.x * sw;
        used_w    += sw;
    }

    // ── 省略したスロットぶんの重みを補正する（総和が 1 未満になるため）──
    if used_w > TERRAIN_WEIGHT_EPSILON {
        let inv = 1.0 / used_w;
        albedo    = albedo * inv;
        metallic  = metallic * inv;
        roughness = roughness * inv;
        normal    = normal * inv;
    } else {
        // 全スロットが閾値以下（理論上起きないが数値保険）。
        albedo    = u_terrain.layers[0].base_color.rgb;
        metallic  = u_terrain.layers[0].surface.x;
        roughness = u_terrain.layers[0].surface.y;
        normal    = geo_n;
    }

    let n_len = length(normal);
    let out_n = select(geo_n, normal / n_len, n_len > TRIPLANAR_MIN_SUM);

    // ── G-Buffer へ書き込む ──
    //   occlusion は 1（地形は AO テクスチャを持たない。SSAO は後段のパスが乗せる）。
    //   emissive / diffuse_transmission は地形では常に 0。
    var o: TerrainGBufferOut;
    o.albedo_occ = vec4<f32>(albedo, 1.0);
    o.normal     = vec4<f32>(out_n, 0.0);
    o.mr         = vec4<f32>(metallic, clamp(roughness, TERRAIN_ROUGHNESS_MIN, 1.0), 0.0, 0.0);
    o.emissive   = vec4<f32>(0.0, 0.0, 0.0, 0.0);
    return o;
}
