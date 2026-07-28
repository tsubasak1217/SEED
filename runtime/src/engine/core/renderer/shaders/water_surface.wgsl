// ============================================================
//  water_surface.wgsl — 水面描画パス（Phase W1）
//
//  ## 役割（単一責任）
//  `ResolvedWaterVolume` から作られた水面クアッド（Ocean / Region）を 1 ドローで描き、
//  「シーン深度から求めた水の厚み」による吸収・岸フォーム・フレネル・スクリーンスペース屈折を
//  **このシェーダ内で最終合成**して HDR へ出力する。
//
//  ## 頂点バッファを持たない理由
//  水面は常に軸平行の矩形（Ocean = カメラ追従の巨大クアッド／Region = AABB の上面）なので、
//  頂点データは「中心 + 半径 + 水面 Y」から完全に決まる。よって頂点バッファは一切使わず、
//  `@builtin(vertex_index)`(0..6) でクアッドの角を生成し、`@builtin(instance_index)` で
//  ストレージバッファのパラメータ配列を引く。全水ボリュームが `draw(0..6, 0..N)` の 1 ドローで済む。
//
//  ## 深度の扱い（アタッチメント無し＋手動深度テスト）
//  本パスは **深度アタッチメントを一切持たない**（TOML の `no_depth = true`）。
//  理由: 共有深度テクスチャは他パスで「書き込み可能」としてアタッチされており、
//  同一パス内でアタッチメントとサンプルテクスチャに同時バインドすると
//  エイリアシング（read/write 競合）になる。アタッチメントを外し、DepthOnly ビューを
//  **サンプルテクスチャとして** group1 に受け取って `textureLoad` で読み、
//  「シーン深度 < 水面フラグメントの深度なら遮蔽 → discard」という手動深度テストを行う。
//  水面は元々深度を書かない（`depth_write = false` 相当）ため、挙動は通常の Z テストと等価。
//  同じ 1 回の深度サンプルから **水の厚み** も復元でき、吸収と岸フォームに使い回せる。
//
//  ## 屈折の背景
//  本パス直前に「シーン HDR をそのままコピーした 1 ミップのグラブテクスチャ」を作り、
//  それを `t_scene` として読む（refract_pyramid のようなブラーミップ鎖は水面には過剰なので作らない）。
//  グラブはメインパス・WBOIT 合成の後に取るため、スカイボックスも既存半透明も背景に含まれる。
//
//  ## 波
//  頂点変位は行わず（W1）、フラグメントで **解析的サイン波の微分から法線のみ**を合成する。
//  法線マップ画像などのテクスチャアセットには一切依存しない。
//
//  ## バインディング規約
//    group 0: カメラ（CameraUniform, binding 0）
//    group 1: binding0 = 水パラメータ配列（storage, read）
//             binding1 = シーンカラーのグラブ（屈折の背景）
//             binding2 = そのサンプラー（線形・ClampToEdge）
//             binding3 = シーン深度（DepthOnly ビュー。textureLoad のみ）
// ============================================================

// ─── Group 0: カメラ ─────────────────────────────────────────
//
// `shader_common.wgsl` は連結しない（マテリアル group2 等を要求してしまうため）。
// deferred_lighting.wgsl と同じ方針で、Rust 側 `uniforms::CameraUniform`（304 bytes）と
// 1:1 対応する同一レイアウトの構造体をここで自前宣言する。
// フィールドを 1 つでも増減すると全パスのカメラ BindGroup と食い違うため、
// Rust 側 / shader_common.wgsl / deferred_lighting.wgsl と併せて同期すること。
struct CameraUniform {
    view_proj:      mat4x4<f32>,
    view:           mat4x4<f32>,
    position:       vec3<f32>,
    /// ゲーム内累計時間（秒）。波の位相スクロールに使う（Play 非ポーズ時のみ進む）。
    time:           f32,
    /// レンダーターゲット全面の解像度（ピクセル）。グラブテクスチャのアドレッシングに使う。
    resolution:     vec2<f32>,
    _pad2:          vec2<f32>,
    /// 逆 ViewProjection（シーン深度 → ワールド座標の復元用）。
    inv_view_proj:  mat4x4<f32>,
    /// 速度バッファ用の前フレーム ViewProjection（本パスでは未使用。オフセット合わせ）。
    prev_view_proj: mat4x4<f32>,
    /// フレームバッファ基準のビューポート矩形 (x, y, w, h)。
    /// NDC はこの矩形へ写像されるため、深度 → ワールド復元はこれで正規化する
    /// （Play のレターボックス時に RT 全面で正規化すると復元座標が横滑りする）。
    viewport:       vec4<f32>,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// ─── Group 1: 水パラメータ＋背景＋深度 ────────────────────────

/// 水ボリューム 1 個ぶんの GPU パラメータ。
/// Rust 側 `renderer::water::params::WaterParams` と **フィールド順・オフセットを厳密一致**させること
/// （vec4 だけで構成し、std430 のアラインメント問題を構造的に起こさないようにしてある）。
struct WaterParams {
    /// xyz = 水面クアッド中心のワールド座標（y = 水面 Y）／w = 未使用
    center:           vec4<f32>,
    /// x,z = クアッドの片側半径（m）／y,w = 未使用
    half_extent:      vec4<f32>,
    /// rgb = 浅場の色／a = 深色へ収束するまでの水中距離（m）
    shallow_color:    vec4<f32>,
    /// rgb = 深場の色／a = 深場での最大不透明度（0..1）
    deep_color:       vec4<f32>,
    /// rgb = 岸フォームの色／a = フォームが出る水深（m）
    foam_color:       vec4<f32>,
    /// rgb = 簡易反射色／a = フォームの強度（0..1）
    reflection_color: vec4<f32>,
    /// x = 波の振幅／y = 空間周波数（1/m）／z = スクロール速度／w = 屈折 UV の最大歪み（画面比）
    wave:             vec4<f32>,
    /// x = フレネル指数／y = フレネル寄与率／z,w = 未使用
    fresnel:          vec4<f32>,
}

@group(1) @binding(0) var<storage, read> u_water: array<WaterParams>;
@group(1) @binding(1) var t_scene: texture_2d<f32>;
@group(1) @binding(2) var s_scene: sampler;
@group(1) @binding(3) var t_depth: texture_depth_2d;

// ─── 定数（マジックナンバー禁止のため全て命名する）───────────

/// クアッド 1 枚あたりの頂点数（三角形 2 枚 = 6 頂点）。
const WATER_QUAD_VERTEX_COUNT: u32 = 6u;

/// 重ね合わせるサイン波の層数。
const WAVE_LAYER_COUNT: u32 = 4u;

/// 「シーン深度がここ以上なら遠クリップ（＝空／何も無い）」とみなす閾値。
/// 深度は `Clear(1.0)` 起点・比較 LessEqual の通常 Z なので、1.0 近傍が空にあたる。
const WATER_SKY_DEPTH_THRESHOLD: f32 = 0.999999;

/// 空に対する水の厚み（m）。実際には背景が無限遠なので「十分深い」として扱う。
const WATER_SKY_THICKNESS: f32 = 1.0e4;

/// ゼロ除算回避の下限値（吸収距離・フォーム幅など、ユーザ入力が 0 になり得るもの）。
const WATER_EPSILON: f32 = 1.0e-4;

/// 屈折 UV の有効範囲（画面外を舐めないようにクランプする）。
const WATER_UV_MIN: f32 = 0.0;
const WATER_UV_MAX: f32 = 1.0;

/// 波の層ごとの周波数倍率（互いに無理数比に近い値にしてタイル感を消す）。
const WAVE_FREQ_MUL_0: f32 = 1.0;
const WAVE_FREQ_MUL_1: f32 = 2.13;
const WAVE_FREQ_MUL_2: f32 = 3.71;
const WAVE_FREQ_MUL_3: f32 = 5.29;

/// 波の層ごとの振幅倍率（高周波ほど小さく＝1/f 的なスペクトル）。
const WAVE_AMP_MUL_0: f32 = 1.0;
const WAVE_AMP_MUL_1: f32 = 0.5;
const WAVE_AMP_MUL_2: f32 = 0.25;
const WAVE_AMP_MUL_3: f32 = 0.125;

/// 波の層ごとのスクロール速度倍率。
const WAVE_SPEED_MUL_0: f32 = 1.0;
const WAVE_SPEED_MUL_1: f32 = 1.37;
const WAVE_SPEED_MUL_2: f32 = 0.83;
const WAVE_SPEED_MUL_3: f32 = 1.71;

/// 波の層ごとの進行方向（XZ 平面。単位ベクトル）。
const WAVE_DIR_0: vec2<f32> = vec2<f32>( 1.0,  0.0);
const WAVE_DIR_1: vec2<f32> = vec2<f32>( 0.0,  1.0);
const WAVE_DIR_2: vec2<f32> = vec2<f32>( 0.70710678, 0.70710678);
const WAVE_DIR_3: vec2<f32> = vec2<f32>(-0.70710678, 0.70710678);

// ─── ヘルパー ────────────────────────────────────────────────

/// クアッドの角オフセット（-1..1 の XZ）を頂点インデックスから生成する。
/// 0,1,2 / 3,4,5 の 2 三角形。カリングは None なので巻き方向は問わない。
fn water_quad_corner(vi: u32) -> vec2<f32> {
    // 三角形1: 左下 → 右下 → 右上 ／ 三角形2: 左下 → 右上 → 左上
    if (vi == 0u) { return vec2<f32>(-1.0, -1.0); }
    if (vi == 1u) { return vec2<f32>( 1.0, -1.0); }
    if (vi == 2u) { return vec2<f32>( 1.0,  1.0); }
    if (vi == 3u) { return vec2<f32>(-1.0, -1.0); }
    if (vi == 4u) { return vec2<f32>( 1.0,  1.0); }
    return vec2<f32>(-1.0, 1.0);
}

/// 波レイヤ i の進行方向。
fn wave_dir(i: u32) -> vec2<f32> {
    if (i == 0u) { return WAVE_DIR_0; }
    if (i == 1u) { return WAVE_DIR_1; }
    if (i == 2u) { return WAVE_DIR_2; }
    return WAVE_DIR_3;
}

/// 波レイヤ i の周波数倍率。
fn wave_freq_mul(i: u32) -> f32 {
    if (i == 0u) { return WAVE_FREQ_MUL_0; }
    if (i == 1u) { return WAVE_FREQ_MUL_1; }
    if (i == 2u) { return WAVE_FREQ_MUL_2; }
    return WAVE_FREQ_MUL_3;
}

/// 波レイヤ i の振幅倍率。
fn wave_amp_mul(i: u32) -> f32 {
    if (i == 0u) { return WAVE_AMP_MUL_0; }
    if (i == 1u) { return WAVE_AMP_MUL_1; }
    if (i == 2u) { return WAVE_AMP_MUL_2; }
    return WAVE_AMP_MUL_3;
}

/// 波レイヤ i の速度倍率。
fn wave_speed_mul(i: u32) -> f32 {
    if (i == 0u) { return WAVE_SPEED_MUL_0; }
    if (i == 1u) { return WAVE_SPEED_MUL_1; }
    if (i == 2u) { return WAVE_SPEED_MUL_2; }
    return WAVE_SPEED_MUL_3;
}

/// 解析的サイン波の重ね合わせから水面法線（ワールド空間・上向き基準）を求める。
///
/// 高さ場 h(p) = Σ_k A_k * sin(dot(d_k, p) * f_k + t * s_k) に対し、
/// ∂h/∂x, ∂h/∂z を解析微分（cos）で求め、N = normalize(-∂h/∂x, 1, -∂h/∂z) とする。
/// 頂点変位は行わないので、これは「法線だけの波」＝ W1 の意図した表現である。
fn water_wave_normal(p: vec2<f32>, amplitude: f32, scale: f32, speed: f32, t: f32) -> vec3<f32> {
    var grad = vec2<f32>(0.0, 0.0);
    for (var i: u32 = 0u; i < WAVE_LAYER_COUNT; i = i + 1u) {
        let dir   = wave_dir(i);
        let freq  = scale * wave_freq_mul(i);
        let amp   = amplitude * wave_amp_mul(i);
        let phase = dot(dir, p) * freq + t * speed * wave_speed_mul(i);
        // d/dp [ A sin(dot(d,p) f + ...) ] = A f cos(...) * d
        grad = grad + dir * (amp * freq * cos(phase));
    }
    return normalize(vec3<f32>(-grad.x, 1.0, -grad.y));
}

/// フラグメント座標（フレームバッファ画素）＋深度からワールド座標を復元する。
///
/// NDC が写像されるのは **ビューポート矩形だけ**なので、RT 全面ではなく
/// `u_camera.viewport` で正規化する（Play のレターボックス時のズレ防止。
/// deferred_lighting.wgsl と同一の規約）。
fn water_world_from_depth(frag_xy: vec2<f32>, depth: f32) -> vec3<f32> {
    let vp = u_camera.viewport;
    let uv = (frag_xy - vp.xy) / max(vp.zw, vec2<f32>(WATER_EPSILON, WATER_EPSILON));
    // wgpu の NDC は x:[-1,1], y:[-1,1]（上が +1）, z:[0,1]。
    let ndc = vec3<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth);
    let p   = u_camera.inv_view_proj * vec4<f32>(ndc, 1.0);
    return p.xyz / p.w;
}

/// 指定のフレームバッファ画素のシーン深度を読む（範囲外はクランプ）。
fn water_load_depth(px: vec2<f32>) -> f32 {
    let dim = vec2<i32>(textureDimensions(t_depth, 0));
    let c   = clamp(vec2<i32>(px), vec2<i32>(0, 0), dim - vec2<i32>(1, 1));
    return textureLoad(t_depth, c, 0);
}

// ─── 頂点シェーダ ────────────────────────────────────────────

/// 頂点シェーダ出力。`idx` はフラグメントでパラメータ配列を引くためのインスタンス番号。
struct WaterVsOut {
    @builtin(position) clip:      vec4<f32>,
    @location(0)       world_pos: vec3<f32>,
    /// インスタンス番号（補間するとフラグメント間で混ざるため flat）
    @location(1) @interpolate(flat) idx: u32,
}

/// 頂点バッファ無しでクアッドを生成する。
/// 中心・半径はインスタンスのパラメータから引く（Ocean はカメラ追従済みの中心が CPU から来る）。
@vertex
fn vs_water(
    @builtin(vertex_index)   vi: u32,
    @builtin(instance_index) ii: u32,
) -> WaterVsOut {
    let p      = u_water[ii];
    let corner = water_quad_corner(vi % WATER_QUAD_VERTEX_COUNT);
    let world  = vec3<f32>(
        p.center.x + corner.x * p.half_extent.x,
        p.center.y,
        p.center.z + corner.y * p.half_extent.z,
    );

    var out: WaterVsOut;
    out.clip      = u_camera.view_proj * vec4<f32>(world, 1.0);
    out.world_pos = world;
    out.idx       = ii;
    return out;
}

// ─── フラグメントシェーダ ────────────────────────────────────

/// 水面 1 フラグメントを合成する。処理順は
/// 波法線 → 手動深度テスト → 厚み復元 → 吸収 → 屈折 → 岸フォーム → フレネル → 合成。
@fragment
fn fs_water(in: WaterVsOut) -> @location(0) vec4<f32> {
    let p = u_water[in.idx];

    // ① 波法線（ワールド空間、上向き基準）
    let n = water_wave_normal(
        in.world_pos.xz,
        p.wave.x,           // amplitude
        p.wave.y,           // scale
        p.wave.z,           // speed
        u_camera.time,
    );

    // ② 手動深度テスト（深度アタッチメントを持たないため自前で行う）
    //    シーン深度が水面フラグメントより手前なら、水は不透明物に隠れている。
    let scene_depth = water_load_depth(in.clip.xy);
    if (scene_depth < in.clip.z) {
        discard;
    }

    // ③ 水の厚み（水面点 → 水底/背景点までの距離）
    var thickness: f32;
    if (scene_depth >= WATER_SKY_DEPTH_THRESHOLD) {
        // 背景が空（無限遠）＝ 水底が無い。最大厚みとして扱い、深場の色へ収束させる。
        thickness = WATER_SKY_THICKNESS;
    } else {
        let bottom = water_world_from_depth(in.clip.xy, scene_depth);
        thickness  = distance(bottom, in.world_pos);
    }

    // ④ 吸収（Beer-Lambert 近似）: 厚いほど deep_color へ寄る。
    let absorb_dist = max(p.shallow_color.a, WATER_EPSILON);
    let absorb      = exp(-thickness / absorb_dist);
    let tint        = mix(p.deep_color.rgb, p.shallow_color.rgb, absorb);

    // ⑤ 屈折（スクリーンスペース）: 波法線の XZ で背景 UV をずらしてグラブを読む。
    let base_uv = in.clip.xy / max(u_camera.resolution, vec2<f32>(WATER_EPSILON, WATER_EPSILON));
    let warp_uv = clamp(
        base_uv + n.xz * p.wave.w,
        vec2<f32>(WATER_UV_MIN, WATER_UV_MIN),
        vec2<f32>(WATER_UV_MAX, WATER_UV_MAX),
    );
    // 歪んだ先のシーン深度が水面より **手前** なら、そこは水より前にある物体（岸・手前のオブジェクト）で、
    // その色を水中の背景として拾うと岸際で背景が滲む典型バグになる。その場合は歪み無し UV へ戻す。
    let warp_depth = water_load_depth(warp_uv * u_camera.resolution);
    var refract_uv = warp_uv;
    if (warp_depth < in.clip.z) {
        refract_uv = base_uv;
    }
    let background = textureSampleLevel(t_scene, s_scene, refract_uv, 0.0).rgb;

    // ⑥ 最終合成: 深いほど背景を隠す（surface_opacity が最大被覆率）。
    let opacity = clamp(p.deep_color.a, 0.0, 1.0) * (1.0 - absorb);
    var color   = mix(background, tint, opacity);

    // ⑦ 岸フォーム: 水深が foam_width 未満の帯に滑らかに乗せる。
    let foam_width = max(p.foam_color.a, WATER_EPSILON);
    let foam       = (1.0 - smoothstep(0.0, foam_width, thickness)) * p.reflection_color.a;
    color          = color + p.foam_color.rgb * foam;

    // ⑧ フレネル: 浅い角度ほど反射色へ寄せる。
    let view_dir = normalize(u_camera.position - in.world_pos);
    let fresnel  = clamp(
        p.fresnel.y * pow(1.0 - clamp(dot(n, view_dir), 0.0, 1.0), max(p.fresnel.x, WATER_EPSILON)),
        0.0, 1.0,
    );
    color = mix(color, p.reflection_color.rgb, fresnel);

    // 背景を自前で合成済みなので、そのまま不透明（alpha=1）で書き出す（ブレンドは Replace）。
    return vec4<f32>(color, 1.0);
}
