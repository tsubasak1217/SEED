// outline.wgsl — Back-face inflation outline (depth-correct, orange)
//
// ① スムーズ法線 (@location(8)) でパーツ境界の隙間を軽減
// ② ステンシル Equal(0) でキャラ内側への描画を防止 (pipeline.rs 設定)
// ③ depth_bias + depth_compare: Less で Z ファイティングを防止
//
// Groups:
//   0 = CameraUniform (uniform, VS)
//   1 = u_instances   (storage RO, VS) — compact model matrices for this LOD
//   2 = joint_matrices (storage RO, VS) — skinned variant only

struct CameraUniform {
    view_proj: mat4x4<f32>,
    view:      mat4x4<f32>,
    position:  vec3<f32>,
    _pad:      f32,
}
struct ModelUniform {
    model:         mat4x4<f32>,
    normal_matrix: mat4x4<f32>,
}

@group(0) @binding(0) var<uniform>       u_camera:    CameraUniform;
@group(1) @binding(0) var<storage, read> u_instances: array<ModelUniform>;

const OUTLINE_THICKNESS: f32 = 0.0075; // 画面サイズに対するアウトラインの太さ

struct VsOut { @builtin(position) pos: vec4<f32> }

@vertex
fn vs_mesh(
    @location(0) position:      vec3<f32>,
    @location(8) smooth_normal: vec3<f32>,  // slot 1 のスムーズ法線バッファ (location 8)
    @builtin(instance_index) ii: u32,
) -> VsOut {
    let m    = u_instances[ii];
    let wpos = m.model * vec4<f32>(position, 1.0);
    let wnrm = normalize((m.normal_matrix * vec4<f32>(smooth_normal, 0.0)).xyz);
    var clip = u_camera.view_proj * wpos;
    let cn   = (u_camera.view_proj * vec4<f32>(wnrm, 0.0)).xy;
    let l    = length(cn);
    if l > 0.0001 {
        clip.x += cn.x / l * OUTLINE_THICKNESS * clip.w;
        clip.y += cn.y / l * OUTLINE_THICKNESS * clip.w;
    }
    return VsOut(clip);
}

const MAX_JOINTS: u32 = 128u;
@group(2) @binding(0) var<storage, read> joint_matrices: array<mat4x4<f32>>;

@vertex
fn vs_skinned(
    @location(0) position:      vec3<f32>,
    @location(6) joints:        vec4<u32>,
    @location(7) weights:       vec4<f32>,
    @location(8) smooth_normal: vec3<f32>,  // slot 2 のスムーズ法線バッファ (location 8)
    @builtin(instance_index) ii: u32,
) -> VsOut {
    let joff = ii * MAX_JOINTS;
    let sm   = weights.x * joint_matrices[joff + joints.x]
             + weights.y * joint_matrices[joff + joints.y]
             + weights.z * joint_matrices[joff + joints.z]
             + weights.w * joint_matrices[joff + joints.w];
    let m    = u_instances[ii];
    let wpos = m.model * (sm * vec4<f32>(position, 1.0));
    let wnrm = normalize((m.normal_matrix * (sm * vec4<f32>(smooth_normal, 0.0))).xyz);
    var clip = u_camera.view_proj * wpos;
    let cn   = (u_camera.view_proj * vec4<f32>(wnrm, 0.0)).xy;
    let l    = length(cn);
    if l > 0.0001 {
        clip.x += cn.x / l * OUTLINE_THICKNESS * clip.w;
        clip.y += cn.y / l * OUTLINE_THICKNESS * clip.w;
    }
    return VsOut(clip);
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    return vec4<f32>(1.0, 0.5, 0.0, 1.0); // orange
}

// ── ステンシルマスク用エントリポイント（膨張なし・カラー出力なし）────────────
// 選択インスタンスの前面を通常描画し、可視ピクセルにステンシル=1 を書き込む。
// これにより draw_outline が Equal(0) を使ってキャラ内側への描画を防止できる。
// (draw_model_indirect ではなくこちらで書き込むことで、他のオブジェクトの
//  シルエット外縁でアウトラインが消える問題を回避する。)

@vertex
fn vs_stencil_mesh(
    @location(0) position: vec3<f32>,
    @builtin(instance_index) ii: u32,
) -> VsOut {
    let m = u_instances[ii];
    return VsOut(u_camera.view_proj * m.model * vec4<f32>(position, 1.0));
}

@vertex
fn vs_stencil_skinned(
    @location(0) position: vec3<f32>,
    @location(6) joints:   vec4<u32>,
    @location(7) weights:  vec4<f32>,
    @builtin(instance_index) ii: u32,
) -> VsOut {
    let joff = ii * MAX_JOINTS;
    let sm   = weights.x * joint_matrices[joff + joints.x]
             + weights.y * joint_matrices[joff + joints.y]
             + weights.z * joint_matrices[joff + joints.z]
             + weights.w * joint_matrices[joff + joints.w];
    let m = u_instances[ii];
    return VsOut(u_camera.view_proj * m.model * (sm * vec4<f32>(position, 1.0)));
}
