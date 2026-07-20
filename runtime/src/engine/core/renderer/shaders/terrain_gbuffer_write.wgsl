// ============================================================
// terrain_gbuffer_write.wgsl — 地形レイヤブレンド G-Buffer 書き込み（Terrain T2）
//
// ## 役割（単一責任）
// 地形メッシュ専用の G-Buffer ジオメトリパス フラグメントシェーダ。
// 頂点カラーに載って来たスプラット重み（レイヤ 0..3）と group3 のレイヤ定義から、
// triplanar 投影でレイヤをブレンドし、合成済みの base_color / normal / roughness /
// metallic を G-Buffer へ焼く。
//
// ライティング・影・RT 反射は G-Buffer を読むライティングパスがそのまま担当するため、
// 地形専用のライティングコードは一切書かない（既存の deferred 経路に完全に乗る）。
//
// ## 連結（terrain_gbuffer.rs::terrain_gbuffer_shader_sources と一致必須）
//   ["shader_common.wgsl", "shader_static_vertex.wgsl", "terrain_gbuffer_write.wgsl"]
// surface.wgsl / surface_gather.wgsl は連結しない（Surface を経由せず直接 MRT を作るため。
// 法線マップも AO テクスチャも持たない地形では gather_surface の大半が無駄になる）。
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
// ============================================================

// ─── レイヤ数（Rust 側 TERRAIN_LAYER_COUNT と一致必須）────────────────────
// 頂点カラー（vec4）にスプラット重みを載せる設計上の上限。
// 一致は Rust 側 tests.rs::terrain_layer_count_matches_shader が検証する。
const TERRAIN_LAYER_COUNT: u32 = 4u;

/// レイヤ重みの総和がこの値以下なら「重みなし」とみなし、レイヤ 0 を下地に敷く。
/// 補間誤差でゼロベクトルになった頂点が真っ黒に落ちるのを防ぐ。
const TERRAIN_WEIGHT_EPSILON: f32 = 1e-5;

/// triplanar のブレンド重みが完全に 0 ベクトルへ潰れたときの下限。
const TRIPLANAR_MIN_SUM: f32 = 1e-5;

// ─── group(3): 地形レイヤ定義 ─────────────────────────────────────────────
// group0=camera / group1=model / group2=material は shader_common.wgsl の宣言を
// そのまま使う（パイプラインレイアウトも MeshPipeline のものを借りる）。
// group2 のマテリアルテクスチャは地形では参照しないが、レイアウト互換のため残る。

/// レイヤ 1 枚ぶんのパラメータ（Rust TerrainLayerParamsGpu と同一レイアウト）。
struct TerrainLayerParams {
    /// rgb = ベースカラー係数（リニア）, a = ベースカラーテクスチャの有無（0 or 1）
    base_color: vec4<f32>,
    /// r = metallic, g = roughness, b = triplanar UV スケール, a = 予約
    surface: vec4<f32>,
}

/// レイヤ定義一式（Rust TerrainLayerUniformGpu と同一レイアウト）。
struct TerrainLayerUniform {
    layers: array<TerrainLayerParams, 4>,
    /// x = triplanar ブレンドの鋭さ（大きいほど平面の切り替わりが硬い）, yzw = 予約
    params: vec4<f32>,
}

@group(3) @binding(0) var<uniform> u_terrain: TerrainLayerUniform;
@group(3) @binding(1) var s_terrain: sampler;
@group(3) @binding(2) var t_terrain_layer0: texture_2d<f32>;
@group(3) @binding(3) var t_terrain_layer1: texture_2d<f32>;
@group(3) @binding(4) var t_terrain_layer2: texture_2d<f32>;
@group(3) @binding(5) var t_terrain_layer3: texture_2d<f32>;

/// 地形 G-Buffer の MRT 出力（gbuffer_write.wgsl の GBufferOut と同一レイアウト）。
struct TerrainGBufferOut {
    @location(0) albedo_occ: vec4<f32>,
    @location(1) normal:     vec4<f32>,
    @location(2) mr:         vec4<f32>,
    @location(3) emissive:   vec4<f32>,
}

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

/// 1 レイヤぶんの triplanar サンプル（3 平面をブレンド重みで合成）。
///
/// テクスチャをパラメータで受けるのは、WGSL がテクスチャバインディングの
/// 動的インデックスを許さないため（レイヤごとに明示的に呼び分ける）。
fn triplanar_sample(
    tex: texture_2d<f32>,
    world_pos: vec3<f32>,
    bw: vec3<f32>,
    uv_scale: f32,
) -> vec3<f32> {
    let sx = textureSample(tex, s_terrain, world_pos.zy * uv_scale).rgb;
    let sy = textureSample(tex, s_terrain, world_pos.xz * uv_scale).rgb;
    let sz = textureSample(tex, s_terrain, world_pos.xy * uv_scale).rgb;
    return sx * bw.x + sy * bw.y + sz * bw.z;
}

/// 地形レイヤをブレンドして G-Buffer へ焼く。
@fragment
fn fs_terrain_gbuffer(
    in: VertexOutput,
    @builtin(front_facing) front_facing: bool,
) -> TerrainGBufferOut {
    // ── 法線（地形は法線マップを持たない＝密度勾配由来の頂点法線が唯一の真実）──
    //   両面描画（cull_face=None）なので裏面フラグメントでは法線を反転して
    //   「見えている側の法線」に揃える（surface_gather.wgsl と同じ規約）。
    let facing_sign = select(-1.0, 1.0, front_facing);
    let N = normalize(in.world_normal) * facing_sign;

    // ── スプラット重み（頂点カラー RGBA = レイヤ 0..3）を正規化する ──
    //   ラスタライザの重心補間で総和が 1 から僅かにずれるため必ず正規化し直す。
    var w = max(in.color, vec4<f32>(0.0));
    let w_sum = w.x + w.y + w.z + w.w;
    if w_sum < TERRAIN_WEIGHT_EPSILON {
        // 縮退：レイヤ 0 を下地として敷く（黒落ち防止）。
        w = vec4<f32>(1.0, 0.0, 0.0, 0.0);
    } else {
        w = w / w_sum;
    }

    // ── triplanar のブレンド重み（3 平面）──
    //   textureSample は一様制御フローで呼ぶ必要があるため、重みが 0 のレイヤも
    //   分岐せずにサンプルする（4 層 × 3 平面 = 最大 12 タップ）。
    let bw = triplanar_blend_weights(N, u_terrain.params.x);

    // ── 各レイヤの実効ベースカラー（テクスチャ有無で分岐せず lerp で合成）──
    //   base_color.a が 1 のときだけテクスチャ色を乗算する（0 なら単色レイヤ）。
    let l0 = u_terrain.layers[0];
    let l1 = u_terrain.layers[1];
    let l2 = u_terrain.layers[2];
    let l3 = u_terrain.layers[3];

    let t0 = triplanar_sample(t_terrain_layer0, in.world_pos, bw, l0.surface.b);
    let t1 = triplanar_sample(t_terrain_layer1, in.world_pos, bw, l1.surface.b);
    let t2 = triplanar_sample(t_terrain_layer2, in.world_pos, bw, l2.surface.b);
    let t3 = triplanar_sample(t_terrain_layer3, in.world_pos, bw, l3.surface.b);

    let c0 = l0.base_color.rgb * mix(vec3<f32>(1.0), t0, l0.base_color.a);
    let c1 = l1.base_color.rgb * mix(vec3<f32>(1.0), t1, l1.base_color.a);
    let c2 = l2.base_color.rgb * mix(vec3<f32>(1.0), t2, l2.base_color.a);
    let c3 = l3.base_color.rgb * mix(vec3<f32>(1.0), t3, l3.base_color.a);

    // ── スプラット重みでレイヤを線形合成する ──
    let albedo = c0 * w.x + c1 * w.y + c2 * w.z + c3 * w.w;
    let metallic = l0.surface.r * w.x + l1.surface.r * w.y
                 + l2.surface.r * w.z + l3.surface.r * w.w;
    let roughness = clamp(
        l0.surface.g * w.x + l1.surface.g * w.y + l2.surface.g * w.z + l3.surface.g * w.w,
        0.04, 1.0,
    );

    // ── G-Buffer へ書き込む ──
    //   occlusion は 1（地形は AO テクスチャを持たない。SSAO は後段のパスが乗せる）。
    //   emissive / diffuse_transmission は地形では常に 0。
    var o: TerrainGBufferOut;
    o.albedo_occ = vec4<f32>(albedo, 1.0);
    o.normal     = vec4<f32>(N, 0.0);
    o.mr         = vec4<f32>(metallic, roughness, 0.0, 0.0);
    o.emissive   = vec4<f32>(0.0, 0.0, 0.0, 0.0);
    return o;
}
