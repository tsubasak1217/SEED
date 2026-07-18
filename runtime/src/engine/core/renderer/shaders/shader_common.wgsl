// ============================================================
// shader_common.wgsl  —  PBR シェーダ共通定義
// ============================================================

// ─── Group 0: カメラ ──────────────────────────────────────────

struct CameraUniform {
    view_proj:      mat4x4<f32>,
    view:           mat4x4<f32>,
    position:       vec3<f32>,
    _pad:           f32,
    // 以下、Rust 側 uniforms::CameraUniform と完全一致させるための末尾フィールド。
    // mesh/skinned のフラグメントは resolution/inv_view_proj を参照しないため未使用のまま
    // でよいが、宣言を欠くと（G-Buffer デファードパスが独自に持つ同名構造体との）
    // オフセット規約がズレて見えるため、常に Rust と 1:1 で保つ（実害はないが保守事故防止）。
    resolution:     vec2<f32>,
    _pad2:          vec2<f32>,
    // 逆 ViewProjection 行列（Phase D3: G-Buffer デファード化の準備で追加）。
    // 本ファイルを連結する mesh/skinned 系フラグメントは使わない
    // （ライティングパスは deferred_lighting.wgsl が shader_common を連結せず
    //   自前の CameraUniform を宣言して使う。ここでは Rust 構造体との
    //   バイトレイアウト一致だけを保つために宣言している）。
    inv_view_proj:  mat4x4<f32>,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// ─── Group 1: モデル変換（インスタンス配列）─────────────────
//
// storage buffer に全インスタンス分の行列を格納し、
// 頂点シェーダが @builtin(instance_index) でインデックスする。
// インスタンス数 1 の通常描画でも同じ構造を使う。

struct ModelUniform {
    model:         mat4x4<f32>,
    normal_matrix: mat4x4<f32>,
}
/// ノードごとのワールド行列配列（全インスタンス分）。
/// @builtin(instance_index) は DrawIndexedIndirect.first_instance を通じて
/// GPU カリングが書き込んだ実インスタンス番号を直接受け取る。
@group(1) @binding(0) var<storage, read> u_instances: array<ModelUniform>;

// ─── Group 2: マテリアル ──────────────────────────────────────

struct MaterialUniform {
    base_color_factor:  vec4<f32>,
    metallic_factor:    f32,
    roughness_factor:   f32,
    alpha_cutoff:       f32,
    has_base_color_tex: u32,
    emissive_factor:    vec3<f32>,
    has_normal_tex:     u32,
    has_mr_tex:         u32,
    has_occlusion_tex:  u32,
    has_emissive_tex:   u32,
    // 屈折率（IOR, Phase RT-Translucency）。旧 _pad（offset 60）を転用。1.0=屈折なし。
    ior:                f32,
    // 透過率（transmission, 0..1。ガラス表現）。offset 64。0.0=従来動作（後方互換）。
    transmission:       f32,
    // MR テクスチャ無視トグル（旧 _pad0, offset 68 を転用）。0=無視しない（従来の乗算）、
    // 1=無視（metallic/roughness factor をそのまま実効値にする）。MR 採取分岐で参照。
    mr_tex_ignore:      u32,
    // 拡散透過（diffuse_transmission, 0..1。葉・布・紙の逆光透け）。旧 _pad1（offset 72）を転用。
    // 0.0=従来動作。gather_surface が Surface.diffuse_transmission へ渡し lighting_eval の逆光項が使う。
    diffuse_transmission: f32,
    // std140 で構造体サイズを 16 の倍数（80）へ揃えるパディング（未使用）。Rust uniforms::MaterialUniform と 1:1。
    _pad2:              f32,
}
@group(2) @binding(0)  var<uniform> u_material:          MaterialUniform;
@group(2) @binding(1)  var          t_base_color:         texture_2d<f32>;
@group(2) @binding(2)  var          s_base_color:         sampler;
@group(2) @binding(3)  var          t_normal:             texture_2d<f32>;
@group(2) @binding(4)  var          s_normal:             sampler;
@group(2) @binding(5)  var          t_metallic_roughness: texture_2d<f32>;
@group(2) @binding(6)  var          s_metallic_roughness: sampler;
@group(2) @binding(7)  var          t_occlusion:          texture_2d<f32>;
@group(2) @binding(8)  var          s_occlusion:          sampler;
@group(2) @binding(9)  var          t_emissive:           texture_2d<f32>;
@group(2) @binding(10) var          s_emissive:           sampler;

// ─── Group 4: ライト／クラスタ参照は light_common.wgsl へ移設 ─────────
//
// GpuLight / LightMeta 構造体、group 4 binding 0/1（ライト）、
// binding 7〜9（クラスタ参照）は light_common.wgsl（本ファイルより後方に連結）へ
// 移設した（Phase D3: G-Buffer デファード化 Phase A）。
//
// 理由: デファードのライティングパス（deferred_lighting.wgsl）は group 4 のライト／
// クラスタ参照だけを必要とし、本ファイルが持つ group 2（マテリアル 11 binding）は
// 不要である。両方を同じファイルに同居させると、ライティングパスが本ファイルを
// 連結した時点で使わない group 2 のバインディングまでリフレクションに載ってしまう
// （pipeline_config::reflect_bgls は module.global_variables を無条件に走査するため）。
// そのため mesh / skinned / transparent 系の TOML は本ファイルの直後に
// light_common.wgsl を連結すること（cluster_common → shader_common → light_common の順）。


// ─── 頂点シェーダ出力 / フラグメントシェーダ入力 ─────────────

struct VertexOutput {
    @builtin(position) clip_pos:     vec4<f32>,
    @location(0)       world_pos:    vec3<f32>,
    @location(1)       world_normal: vec3<f32>,
    @location(2)       world_tan:    vec3<f32>,
    @location(3)       world_bitan:  vec3<f32>,
    @location(4)       uv0:          vec2<f32>,
    @location(5)       uv1:          vec2<f32>,
    @location(6)       color:        vec4<f32>,
}

// ─── PBR ヘルパー関数は pbr_common.wgsl へ移設 ───────────────
//
// distribution_ggx / geometry_smith / fresnel_schlick / PI は pbr_common.wgsl
// （バインディングを持たない共有定義）へ移設した（Phase D3: G-Buffer デファード化 Phase A）。
// 理由は光源系構造体の分離（本ファイル冒頭の Group 4 のコメント参照）と同じ:
// デファードのライティングパスが shader_common.wgsl を連結せずに evaluate_lighting を
// 呼べるようにするため。本ファイルを連結する全 TOML は pbr_common.wgsl も連結すること
// （例: ["cluster_common.wgsl", "pbr_common.wgsl", "shader_common.wgsl", "light_common.wgsl", ...]）。
