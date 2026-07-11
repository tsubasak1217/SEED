// ============================================================
//  sprite_outline.wgsl — 選択スプライトのスクリーンスペース均一アウトライン
//                        （Phase R6 インスタンシング）
//
//  3D モデルアウトライン (outline.wgsl) と同じ OUTLINE_THICKNESS を使用し、
//  クリップ空間でコーナーを外側に押し出すことで、どこから見ても一定幅を実現する。
//
//  Group 0: CameraUniform（sprite.wgsl と同一レイアウト）
//
//  モデル行列・カラーは per-instance の頂点属性（sprite.wgsl と同一レイアウト）で供給する。
//  テクスチャ不要（単色塗りつぶし）のためバインドグループは Group 0（Camera）のみ。
// ============================================================

struct CameraUniform {
    view_proj:  mat4x4<f32>,
    view:       mat4x4<f32>,
    position:   vec3<f32>,
    _pad:       f32,
    resolution: vec2<f32>,
    _pad2:      vec2<f32>,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// 3D モデルアウトライン (outline.wgsl) の 1.5 倍の太さ
const OUTLINE_THICKNESS: f32 = 0.0075 * 1.5;

struct VsOut {
    @builtin(position) clip_pos: vec4<f32>,
    @location(0)       color:    vec4<f32>,
}

@vertex
fn vs_main(
    // per-vertex（ユニットクワッド）
    @location(0) position: vec2<f32>,
    @location(1) uv:       vec2<f32>,
    // per-instance（model 行列の 4 列 + カラー, 列優先）
    @location(2) m0:    vec4<f32>,
    @location(3) m1:    vec4<f32>,
    @location(4) m2:    vec4<f32>,
    @location(5) m3:    vec4<f32>,
    @location(6) color: vec4<f32>,
) -> VsOut {
    let model = mat4x4<f32>(m0, m1, m2, m3);
    let mvp   = u_camera.view_proj * model;

    // 各コーナーをクリップ空間に変換する
    var clip = mvp * vec4<f32>(position, 0.0, 1.0);

    // スプライト中心（ローカル (0.5, 0.5)）のクリップ座標
    let clip_center = mvp * vec4<f32>(0.5, 0.5, 0.0, 1.0);

    // NDC 空間でのコーナー→中心方向を計算する。
    // パースペクティブ除算後に比較するため、傾斜スプライトでも均一幅になる。
    let ndc_corner = clip.xy / clip.w;
    let ndc_center = clip_center.xy / clip_center.w;
    let ndc_dir    = ndc_corner - ndc_center;
    let l          = length(ndc_dir);

    // クリップ空間で OUTLINE_THICKNESS × clip.w だけ外側に押し出す。
    // → NDC 移動量 = OUTLINE_THICKNESS（画面サイズ比で一定幅）
    if l > 0.001 {
        clip.x += ndc_dir.x / l * OUTLINE_THICKNESS * clip.w;
        clip.y += ndc_dir.y / l * OUTLINE_THICKNESS * clip.w;
    }

    return VsOut(clip, color);
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    return in.color;
}
