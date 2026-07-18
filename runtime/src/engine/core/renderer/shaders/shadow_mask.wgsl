// ============================================================
// shadow_mask.wgsl — RT ソフト影マスク生成（fragment fs_mask, RAY_QUERY 必須）
//
// ## 役割（単一責任・Phase RT-Shadow-Denoise）
// G-Buffer の法線・深度からワールド位置を復元し、CPU が選定した最大
// RT_SHADOW_MASK_LIGHTS 灯のソフト影ライトについて、既存の rt_shadow_factor
// （rt_shadow_on.wgsl・無改変）を評価して**半解像度の texture_2d_array**（レイヤ=スロット）へ
// 透過率（rgb, 色付き影込み）を書き出す。この生ノイズ入りマスクを後段のいもす法ブラーで
// 各レイヤごとにデノイズし、deferred ライティングがサンプルする。
//
// ## なぜ分離するか（症状の根治）
// ソフト影のディザ状ガサガサは「ピクセルごとの IGN 回転 × 確率的サンプリングによる遮蔽率の
// 量子化ノイズ」。1 ライトを画面全体でまとめて評価し、空間ブラー（いもす法）で均せば、
// フルスクリーンの低周波信号として滑らかな半影になる。半解像度化でレイ本数も従来比 ~1/4。
//
// ## 連結順（RAY_QUERY 必須）
//   [cluster_common, ddgi_common, light_common, rt_shadow_on, shadow_mask]
//   - cluster_common: LIGHT_KIND_* / ClusterCell（light_common が参照）
//   - ddgi_common   : GiParams（light_common binding10 が参照）
//   - light_common  : GpuLight / LightMeta / group4 binding 0/1/7〜13 ＋ light_shadow_geometry
//   - rt_shadow_on  : rt_shadow_factor 本体＋ group4 binding 6（TLAS）/14（平均アルベド）
//   shadow_mask（本ファイル）は group0（カメラ）/1（G-Buffer 0..5）/2（ShadowMaskParams）を宣言する。
//
// ## バインドグループ（max_bind_groups=5 厳守。group3 は空 gap）
//   group0=camera / group1=G-Buffer / group2=ShadowMaskParams / group3=gap / group4=ライト+TLAS
//   group4 は既存の RT 影用複合 BindGroup（lighting.rs create_rt_bind_group）をそのまま借用する
//   （パイプラインレイアウトも RtMeshPipelines.lights_bgl を流用＝レイアウト等価）。
// ============================================================

// ─── Group 0: カメラ（ao_common.wgsl / deferred_lighting.wgsl と同一 224B）───
struct MaskCameraUniform {
    view_proj:      mat4x4<f32>,
    view:           mat4x4<f32>,
    position:       vec3<f32>,
    _pad:           f32,
    resolution:     vec2<f32>,
    _pad2:          vec2<f32>,
    inv_view_proj:  mat4x4<f32>,
}
@group(0) @binding(0) var<uniform> u_mask_camera: MaskCameraUniform;

// ─── Group 1: G-Buffer 入力（deferred.rs の gbuffer_bgl の 0..5 サブセット）───
@group(1) @binding(0) var t_gbuffer0: texture_2d<f32>;   // albedo.rgb + occlusion.a
@group(1) @binding(1) var t_gbuffer1: texture_2d<f32>;   // world normal.xyz
@group(1) @binding(2) var t_gbuffer2: texture_2d<f32>;   // metallic.r + roughness.g
@group(1) @binding(3) var t_gbuffer3: texture_2d<f32>;   // emissive.rgb（未使用・レイアウト一致用）
@group(1) @binding(4) var t_depth:    texture_depth_2d;  // 深度（textureLoad 専用）
@group(1) @binding(5) var s_gbuffer:  sampler;           // 予約（未使用）

// ─── Group 2: ShadowMaskParams（Rust shadow_mask::ShadowMaskParams 32B と同期）───
struct ShadowMaskParams {
    /// スロット 0..3 が評価するライトの u_lights グローバルインデックス。count 未満のみ有効。
    indices: vec4<u32>,
    /// 実際に選定されたソフト影ライト数（0..RT_SHADOW_MASK_LIGHTS）。
    count:   u32,
    _pad0:   u32,
    _pad1:   u32,
    _pad2:   u32,
}
@group(2) @binding(0) var<uniform> u_mask: ShadowMaskParams;

/// マスクのレイヤ数（スロット数＝MRT 出力数）。Rust shadow_mask::RT_SHADOW_MASK_LIGHTS と一致。
const SHADOW_MASK_LAYERS: u32 = 4u;
/// 背景深度（DepthStencil の Clear=1.0）。この値以上は「何も描かれていない背景」＝遮蔽なし(1)。
const MASK_BACKGROUND_DEPTH: f32 = 1.0;

// ─── フルスクリーン三角形（UV を varying で出力。ao_common.wgsl と同一）───
struct MaskVsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0)       uv:  vec2<f32>,
}
const MASK_FS_POS: array<vec2<f32>, 3> = array<vec2<f32>, 3>(
    vec2<f32>(-1.0, -1.0),
    vec2<f32>( 3.0, -1.0),
    vec2<f32>(-1.0,  3.0),
);
@vertex
fn vs_fullscreen(@builtin(vertex_index) vi: u32) -> MaskVsOut {
    var out: MaskVsOut;
    let p = MASK_FS_POS[vi];
    out.pos = vec4<f32>(p, 0.0, 1.0);
    // クリップ座標 → UV（uv.x=0 左端 / uv.y=0 上端。ao_world_pos と整合）。
    out.uv = vec2<f32>(p.x * 0.5 + 0.5, (1.0 - p.y) * 0.5);
    return out;
}

// ─── 深度→ワールド復元（ao_common / deferred_lighting.wgsl と同式）───
fn mask_world_pos(uv: vec2<f32>, depth: f32) -> vec3<f32> {
    let ndc  = vec3<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth);
    let clip = u_mask_camera.inv_view_proj * vec4<f32>(ndc, 1.0);
    return clip.xyz / clip.w;
}

// ─── UV → フル解像度 G-Buffer 整数座標（textureDimensions ベース。半解像度非依存）───
fn mask_full_pix(uv: vec2<f32>) -> vec2<i32> {
    let dims = vec2<f32>(textureDimensions(t_gbuffer0));
    let p    = uv * dims;
    let mx   = dims - vec2<f32>(1.0, 1.0);
    return vec2<i32>(clamp(p, vec2<f32>(0.0, 0.0), mx));
}

/// スロット slot（< count 前提）のライトを rt_shadow_factor で評価し、透過率（rgb）を返す。
/// nv には G-Buffer の N（法線マップ後）を代用する（deferred_lighting.wgsl の Nv 代用方針と一致）。
fn mask_eval_slot(slot: u32, world_pos: vec3<f32>, ng: vec3<f32>, nv: vec3<f32>, frag_xy: vec2<f32>) -> vec3<f32> {
    let li    = u_mask.indices[slot];
    let light = u_lights[min(li, arrayLength(&u_lights) - 1u)];
    // L・光源距離・見込み半径（cone_radius）は lighting_eval.wgsl と共通の共有関数で求める
    //（インライン経路とマスク経路で L が食い違わないことを保証する単一実装）。
    let geo = light_shadow_geometry(light, world_pos);
    // deterministic_tint=false: マスクはこの後 separable バイラテラルでデノイズされるため、
    // ソフト影の色付き tint はサンプルごとに評価してよい（均されて色にも半影勾配が乗る＝高品質）。
    return rt_shadow_factor(world_pos, ng, nv, geo.L, geo.dist, geo.cone_radius, frag_xy, false);
}

// ─── マスク生成フラグメント（4 レイヤを MRT で同時出力。rgb=透過率, a=半解像度深度）───
// location 0..3: 各ソフト影ライトの出力（rgba16float, レイヤ=スロット）。
//   .rgb = 透過率（色付き影込み）／.a = 半解像度ビュー空間深度（バイラテラルブラーの深度ガイド）。
//
// 【なぜ .a に同梱するか（別 R32Float アタッチメント不可）】
//   4×Rgba16Float=32B に R32Float の 4B を足すと 36B となり、WebGPU 既定の
//   max_color_attachment_bytes_per_sample=32 を超過して create_render_pipeline がパニックする。
//   マスクの .a は未使用だったため、ここへビュー空間深度を載せてバイト予算内（32B）に収める。
//
// 【f16 精度の妥当性】.a は f16（仮数 ~11bit ＝相対誤差 ~0.05%）へ量子化されるが、深度類似度の
//   許容幅は相対 5%（BILATERAL_DEPTH_TOL_FRAC）なので、数十〜数百 m レンジでも
//   f16 誤差（~0.05%）<< 許容幅（5%）で重み計算に影響しない。全レイヤ同じ深度でよい
//   （深度は画素固有でライト非依存）。※ deferred のアップサンプルはフル解像度深度から同一写像で
//   復元するため .a は参照しない（.a はブラーの深度ガイド専用）。
struct MaskOut {
    @location(0) l0: vec4<f32>,
    @location(1) l1: vec4<f32>,
    @location(2) l2: vec4<f32>,
    @location(3) l3: vec4<f32>,
}

@fragment
fn fs_mask(in: MaskVsOut) -> MaskOut {
    var out: MaskOut;

    let uv  = in.uv;
    let pix = mask_full_pix(uv);
    let depth = textureLoad(t_depth, pix, 0);

    // 幾何法線 Ng は深度復元ワールド座標の画面微分から作る（deferred_lighting.wgsl と同式）。
    // dpdx/dpdy は一様制御フローで呼ぶ必要があるため、背景の早期 return より**前**に計算する。
    let world_pos = mask_world_pos(uv, depth);
    // 半解像度ビュー空間深度（バイラテラルブラーの深度ガイド）。全レイヤの .a へ同梱する。
    // deferred の mask_half_texel_view_z と同式（view * world_pos の z）にし、生成時と
    // ブラー時・アップサンプル時の深度基準を一致させる。背景も含め全テクセルで書き込む。
    let view_z = (u_mask_camera.view * vec4<f32>(world_pos, 1.0)).z;
    // 既定は全レイヤ rgb=1（遮蔽なし）＋ a=view_z（深度ガイド）。count 未満のスロット・背景はこのまま。
    out.l0 = vec4<f32>(1.0, 1.0, 1.0, view_z);
    out.l1 = vec4<f32>(1.0, 1.0, 1.0, view_z);
    out.l2 = vec4<f32>(1.0, 1.0, 1.0, view_z);
    out.l3 = vec4<f32>(1.0, 1.0, 1.0, view_z);

    let N         = normalize(textureLoad(t_gbuffer1, pix, 0).xyz);
    let ng_raw    = cross(dpdx(world_pos), dpdy(world_pos));
    let ng_len    = length(ng_raw);
    var Ng: vec3<f32>;
    if ng_len < 1e-8 {
        Ng = N;
    } else {
        Ng = ng_raw / ng_len;
    }
    if dot(Ng, N) < 0.0 {
        Ng = -Ng;
    }

    // 背景（深度クリア値以上）＝遮蔽物なし。全レイヤ rgb=1（既定値）＋ a=view_z で返す。
    if depth >= MASK_BACKGROUND_DEPTH {
        return out;
    }

    // IGN 種は半解像度フラグ座標（マスクはこの後デノイズするためノイズは均される）。
    // 各スロットは .rgb=透過率, .a=view_z（深度ガイド）で出力する。
    let frag_xy = in.pos.xy;
    let cnt     = min(u_mask.count, SHADOW_MASK_LAYERS);
    if cnt > 0u { out.l0 = vec4<f32>(mask_eval_slot(0u, world_pos, Ng, N, frag_xy), view_z); }
    if cnt > 1u { out.l1 = vec4<f32>(mask_eval_slot(1u, world_pos, Ng, N, frag_xy), view_z); }
    if cnt > 2u { out.l2 = vec4<f32>(mask_eval_slot(2u, world_pos, Ng, N, frag_xy), view_z); }
    if cnt > 3u { out.l3 = vec4<f32>(mask_eval_slot(3u, world_pos, Ng, N, frag_xy), view_z); }
    return out;
}
