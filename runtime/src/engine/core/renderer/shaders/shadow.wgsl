// ============================================================
// shadow.wgsl  —  シャドウマップ サンプリング（group 4 の binding 2〜5）
//
// 方向光 CSM（3 カスケード）＋ スポット（最大 4）の深度シャドウマップを
// 「法線オフセット ＋ カスケード別深度バイアス ＋ 回転ポアソン(Vogel)ディスク PCF」で
// サンプルし、可視率 [0,1] を返す。
//
// このファイルは mesh.toml / skinned_mesh.toml / deferred_lighting.toml などの
// shader_sources に連結され、ライトループ（lighting_eval.wgsl）から呼ばれる。
// **フォワードとデファードで同一の関数**を通るため、片方だけ影が違うことは起きない。
//
// 【バインドグループ】group 4（ライトと同居, mesh / skinned_mesh 共通）
// デバイスの max_bind_groups は 5（group 0〜4）の環境が存在するため、
// group 5 を新設せず既存のライト group 4 へシャドウ資源を統合する。
//   binding 0: array<GpuLight>         （shader_common.wgsl で宣言）
//   binding 1: LightMeta               （shader_common.wgsl で宣言）
//   binding 2: texture_depth_2d_array  方向光 CSM（CSM_CASCADE_COUNT レイヤ）
//   binding 3: texture_depth_2d_array  スポット（MAX_SHADOW_SPOTS レイヤ）
//   binding 4: sampler_comparison      比較サンプラー（LessEqual）
//   binding 5: uniform ShadowMatrices  カスケード/スポットの view-proj と品質パラメータ
// Rust 側 lighting.rs（複合 BindGroup 生成）・shadow.rs（UBO レイアウト）と
// 厳密に一致させること。
//
// 【アクネ（自己遮蔽の縞）対策の設計】
//   シャドウマップのアクネは「受光面のピクセル深度」と「シャドウマップ 1 テクセルに
//   量子化された深度」がズレることで起きる。対策は 3 段構えで、**どれか 1 つでは
//   必ず穴が残る**ため全部入れてある。
//     ① ラスタライザの slope-scaled 深度バイアス（shadow_depth_*.toml）
//        … 深度を書く側で、三角形の傾きに比例した分だけ押し込む。
//     ② 法線オフセット（本ファイル）
//        … サンプル**位置**を幾何法線方向へずらす。ライトに対して斜めの面ほど
//          大きくずらす（sinθ 比例）。深度ではなく位置を動かすので、傾きが
//          きつい面でもピーターパン（影の浮き）になりにくい。
//        ★ ずらす量は「そのカスケードの 1 テクセルが覆うワールド距離」の倍数。
//          カスケード 0 と 2 ではテクセル幅が 1 桁以上違うため、必ず
//          **カスケード単位**で算出する（u_shadow.cascade_texel_world）。
//     ③ カスケード別の定数深度バイアス（u_shadow.cascade_depth_bias）
//        … 法線が光と正対する面（②のオフセットが 0 になる面）の量子化残差を潰す。
//          ワールド距離で与えた値を CPU 側でカスケードの深度レンジ [0,far_z] で
//          割って NDC へ換算済み。旧実装の「NDC 定数 0.0012 固定」は遠カスケードで
//          0.5m 超の押し込みになりピーターパンの原因だったため置き換えた。
//
// 【ジャギー（輪郭の階段）対策の設計】
//   旧実装の PCF 3x3 は「固定格子・9 タップ」で、テクセルが画面上で大きいほど
//   階段の段差がそのまま見える。本実装は
//     - Vogel ディスク（黄金角らせん）で円板上に N タップを均等配置
//     - ピクセルごとに IGN（Interleaved Gradient Noise）で**回転**させる
//   ことで、段差を「向きの揃わない細かなディザ」へ分解する。比較サンプラーが
//   Linear なので 1 タップが 2x2 のバイリニア PCF になり、実効の柔らかさは
//   半径 + 0.5 テクセル程度になる。
// ============================================================

// ─── 定数（Rust: renderer/shadow.rs と一致）─────────────────
/// 方向光カスケード数。
const CSM_CASCADE_COUNT: u32 = 3u;
/// 影付きスポットの最大数（配列レイヤ数）。
const MAX_SHADOW_SPOTS: u32 = 4u;

/// 黄金角 [rad]（Vogel ディスクのタップ間の回転角）。
/// π(3−√5) ≒ 2.39996323。この角度で回すとタップが最も均等に散る。
const SHADOW_GOLDEN_ANGLE: f32 = 2.39996323;

/// IGN（Interleaved Gradient Noise）の係数。Jimenez 2014 の値。
/// ピクセル座標から [0,1) の擬似乱数を作る。画面上で高周波かつ規則的に散るため、
/// 回転角に使うとバンディングにならず、かつフレーム間で不動（＝ちらつかない）。
const SHADOW_IGN_SCALE: vec2<f32> = vec2<f32>(0.06711056, 0.00583715);
const SHADOW_IGN_MUL:   f32       = 52.9829189;

/// 2π（IGN の [0,1) を全周の回転角へ写す）。
const SHADOW_TWO_PI: f32 = 6.28318530718;

/// スポットシャドウの定数深度バイアス（NDC 深度 [0,1] 単位）。
/// 方向光（CSM）はカスケードごとにワールド距離から換算した値を UBO で受け取るが、
/// スポットは透視投影で NDC 深度が非線形なため、換算にライトごとの near/far が要る。
/// 主因のテクセル量子化は法線オフセットが吸収するので、ここは Phase R2 からの
/// 実績値を据え置く（大きすぎるとピーターパン、小さすぎるとアクネ）。
const SPOT_SHADOW_CONST_BIAS: f32 = 0.0012;

// ─── ShadowMatrices（uniform, std140）───────────────────────
// Rust: renderer/shadow.rs ShadowMatricesUbo と 1 バイト単位で一致。
//   cascade_vp:          mat4x4 × 3   (192)  カスケードごとの light view-proj
//   spot_vp:             mat4x4 × 4   (256)  スポットごとの light view-proj
//   cascade_splits:      vec4         ( 16)  カスケード遠端のビュー空間距離 (x,y,z)
//   params:              vec4<u32>    ( 16)  x=方向光カスケード数(0=影なし), y=スポット影数
//   cascade_texel_world: vec4         ( 16)  カスケードごとの 1 テクセルのワールド幅 (x,y,z)
//   cascade_depth_bias:  vec4         ( 16)  カスケードごとの定数深度バイアス[NDC] (x,y,z)
//   spot_texel_scale:    vec4         ( 16)  スポットごとの「テクセル幅÷距離」係数 (x,y,z,w)
//   filter_params:       vec4         ( 16)  x=CSM テクセル UV, y=PCF 半径[texel],
//                                            z=法線オフセット[texel], w=スポット テクセル UV
//   filter_params_i:     vec4<u32>    ( 16)  x=PCF タップ数
struct ShadowMatrices {
    cascade_vp:          array<mat4x4<f32>, 3>,
    spot_vp:             array<mat4x4<f32>, 4>,
    cascade_splits:      vec4<f32>,
    params:              vec4<u32>,
    cascade_texel_world: vec4<f32>,
    cascade_depth_bias:  vec4<f32>,
    spot_texel_scale:    vec4<f32>,
    filter_params:       vec4<f32>,
    filter_params_i:     vec4<u32>,
}

@group(4) @binding(2) var t_shadow_dir:  texture_depth_2d_array;
@group(4) @binding(3) var t_shadow_spot: texture_depth_2d_array;
@group(4) @binding(4) var s_shadow_cmp:  sampler_comparison;
@group(4) @binding(5) var<uniform> u_shadow: ShadowMatrices;

// ─── 小物ヘルパー ───────────────────────────────────────────

/// vec4 の成分を実行時添字で読む（WGSL は vec の動的添字を許さないため分岐で代替）。
/// カスケード番号（0..2）やスポットスロット（0..3）で引く用途。
fn shadow_vec4_at(v: vec4<f32>, i: i32) -> f32 {
    if i == 0 { return v.x; }
    if i == 1 { return v.y; }
    if i == 2 { return v.z; }
    return v.w;
}

/// Interleaved Gradient Noise。ピクセル座標 → [0,1) の擬似乱数。
fn shadow_ign(frag_coord: vec2<f32>) -> f32 {
    return fract(SHADOW_IGN_MUL * fract(dot(frag_coord, SHADOW_IGN_SCALE)));
}

/// Vogel ディスク（黄金角らせん）の i 番目のタップ位置（単位円板内）。
/// `phi` はピクセルごとの回転角。`count` はタップ総数。
fn shadow_vogel_tap(i: u32, count: u32, phi: f32) -> vec2<f32> {
    // 半径は sqrt((i+0.5)/n)。面積で均等になる（中心に偏らない）。
    let r     = sqrt((f32(i) + 0.5) / f32(max(count, 1u)));
    let theta = f32(i) * SHADOW_GOLDEN_ANGLE + phi;
    return vec2<f32>(r * cos(theta), r * sin(theta));
}

/// 受光点を幾何法線方向へずらす量（ワールド距離）を求める。
///
/// `texel_world` … そのシャドウマップ 1 テクセルが覆うワールド距離
/// `n_dot_l`     … 幾何法線と光方向の内積（クランプ済み）
///
/// ずらす量 = テクセル幅 × 設定倍率 × sinθ。
/// sinθ = sqrt(1 − (N·L)²) は「面が光に対してどれだけ寝ているか」で、
/// 正対（N·L=1）なら 0、かすめる（N·L=0）なら 1。依頼の「(1 − N·L) 相当」と
/// 同じ振る舞い（両端が 0 と 1）で、こちらは深度勾配 tanθ の分子そのものなので
/// 中間角での効きが素直に合う。
fn shadow_normal_offset_amount(texel_world: f32, n_dot_l: f32) -> f32 {
    let sin_theta = sqrt(clamp(1.0 - n_dot_l * n_dot_l, 0.0, 1.0));
    return texel_world * u_shadow.filter_params.z * sin_theta;
}

// ─── PCF（回転 Vogel ディスク）──────────────────────────────

/// 深度配列テクスチャを回転 Vogel ディスクでサンプルし可視率 [0,1] を返す。
/// 1 = 全照射, 0 = 全遮蔽。方向光 CSM とスポットで**同じ実装**を共有する。
///
/// - `radius_uv`  … フィルタ半径（UV 単位 = テクセル UV 幅 × 半径[テクセル]）
/// - `phi`        … ピクセルごとの回転角
/// - `taps`       … タップ数（1..=16 を想定。Rust 側でクランプ済み）
fn pcf_disk(
    tex:       texture_depth_2d_array,
    smp:       sampler_comparison,
    uv:        vec2<f32>,
    layer:     i32,
    ref_depth: f32,
    radius_uv: f32,
    phi:       f32,
    taps:      u32,
) -> f32 {
    // 半径 0（＝フィルタ無効）のときは中心 1 タップだけ。
    // 比較サンプラーが Linear なので、この 1 タップでも 2x2 バイリニア PCF になる。
    if radius_uv <= 0.0 || taps <= 1u {
        return textureSampleCompare(tex, smp, uv, layer, ref_depth);
    }
    var sum = 0.0;
    for (var i: u32 = 0u; i < taps; i = i + 1u) {
        let off = shadow_vogel_tap(i, taps, phi) * radius_uv;
        // 比較サンプラー(LessEqual): ref_depth <= 格納深度 → 1.0（手前=照射）
        sum = sum + textureSampleCompare(tex, smp, uv + off, layer, ref_depth);
    }
    return sum / f32(taps);
}

// ─── 方向光 CSM ─────────────────────────────────────────────

/// フラグメントのビュー空間深度からカスケードを選び、方向光の可視率を返す。
/// - `world_pos`:   フラグメントのワールド座標
/// - `view_z`:      ビュー空間深度（正、= (u_camera.view * world).z）
/// - `geo_normal`:  幾何法線（法線マップ前のフラットな面法線）。法線オフセット用。
/// - `light_dir`:   フラグメント → 光源の方向（正規化済み）
/// - `frag_coord`:  ピクセル座標（PCF の回転角用）
fn sample_shadow_dir(
    world_pos:  vec3<f32>,
    view_z:     f32,
    geo_normal: vec3<f32>,
    light_dir:  vec3<f32>,
    frag_coord: vec2<f32>,
) -> f32 {
    // 方向光影が無効（params.x == 0）なら常に照射。
    if u_shadow.params.x == 0u { return 1.0; }

    // カスケード選択（ビュー空間距離の分割境界で判定）。
    // vec4 は動的添字不可のため成分比較で分岐する。
    var cascade: i32 = 2;
    if view_z < u_shadow.cascade_splits.x {
        cascade = 0;
    } else if view_z < u_shadow.cascade_splits.y {
        cascade = 1;
    } else {
        cascade = 2;
    }

    // ── 法線オフセット（カスケードのテクセル幅基準）───────────
    // カスケードを決めてからでないとテクセル幅が分からないので、この順序は動かさない。
    let texel_world = shadow_vec4_at(u_shadow.cascade_texel_world, cascade);
    let n_dot_l     = clamp(dot(geo_normal, light_dir), 0.0, 1.0);
    let sample_pos  = world_pos + geo_normal * shadow_normal_offset_amount(texel_world, n_dot_l);

    // ライト空間クリップ座標 → NDC。
    let clip = u_shadow.cascade_vp[cascade] * vec4<f32>(sample_pos, 1.0);
    if clip.w <= 0.0 { return 1.0; }
    let ndc = clip.xyz / clip.w;

    // NDC(x,y) [-1,1] → UV [0,1]（Y は反転）。
    let uv = vec2<f32>(ndc.x * 0.5 + 0.5, ndc.y * -0.5 + 0.5);
    // マップ外・深度 [0,1] 外は影なし扱い（境界破綻を防ぐ）。
    // 影の最大距離（shadow.distance）より遠いフラグメントもここで 1.0 になる。
    if uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || ndc.z > 1.0 || ndc.z < 0.0 {
        return 1.0;
    }

    // カスケード別の定数深度バイアス（CPU で NDC へ換算済み）。
    let ref_depth = ndc.z - shadow_vec4_at(u_shadow.cascade_depth_bias, cascade);
    let radius_uv = u_shadow.filter_params.x * u_shadow.filter_params.y;
    let phi       = shadow_ign(frag_coord) * SHADOW_TWO_PI;
    return pcf_disk(
        t_shadow_dir, s_shadow_cmp, uv, cascade, ref_depth,
        radius_uv, phi, u_shadow.filter_params_i.x,
    );
}

// ─── スポット ───────────────────────────────────────────────

/// スポット光の可視率を返す。`slot` は 0..MAX_SHADOW_SPOTS-1 の配列レイヤ。
/// - `light_dist`: フラグメント → 光源の距離。透視シャドウマップのテクセル幅は
///   距離に比例するため、法線オフセット量の算出に使う。
///   （厳密には円錐軸方向の距離が正しいが、外側コーン内では両者の差は
///     1/cos(半画角) 倍までで、オフセットの見積もりには十分な精度。）
fn sample_shadow_spot(
    world_pos:  vec3<f32>,
    slot:       i32,
    geo_normal: vec3<f32>,
    light_dir:  vec3<f32>,
    light_dist: f32,
    frag_coord: vec2<f32>,
) -> f32 {
    // ── 法線オフセット（距離に比例するテクセル幅基準）─────────
    let texel_world = shadow_vec4_at(u_shadow.spot_texel_scale, slot) * light_dist;
    let n_dot_l     = clamp(dot(geo_normal, light_dir), 0.0, 1.0);
    let sample_pos  = world_pos + geo_normal * shadow_normal_offset_amount(texel_world, n_dot_l);

    let clip = u_shadow.spot_vp[slot] * vec4<f32>(sample_pos, 1.0);
    if clip.w <= 0.0 { return 1.0; }
    let ndc = clip.xyz / clip.w;

    let uv = vec2<f32>(ndc.x * 0.5 + 0.5, ndc.y * -0.5 + 0.5);
    if uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || ndc.z > 1.0 || ndc.z < 0.0 {
        return 1.0;
    }

    // スポットは**従来どおり NDC 固定の定数バイアス**を使う。
    // 透視投影の NDC 深度は非線形（Δndc = Δworld × f·n/((f−n)·z²)）で、
    // ワールド距離バイアスを正しく NDC へ換算するにはスポットごとの near/far が要る。
    // シャドウマップ側の主因である「テクセル量子化」は上の法線オフセットが
    // カバーするため、ここは実績のある固定値を据え置く（挙動の後退を作らない）。
    let ref_depth  = ndc.z - SPOT_SHADOW_CONST_BIAS;
    let radius_uv  = u_shadow.filter_params.w * u_shadow.filter_params.y;
    let phi        = shadow_ign(frag_coord) * SHADOW_TWO_PI;
    return pcf_disk(
        t_shadow_spot, s_shadow_cmp, uv, slot, ref_depth,
        radius_uv, phi, u_shadow.filter_params_i.x,
    );
}
