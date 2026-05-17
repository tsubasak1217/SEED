// ============================================================
//  canvas_id.wgsl — キャンバスアクター ID パス
//
//  Group 0: CameraUniform（sprite.wgsl と同一レイアウト）
//  Group 1: CanvasIdUniform（モデル行列 + アクター raw ID）
//  Group 2: テクスチャ + サンプラー（スプライトアルファマスク用）
//           スプライトなしのアクターは白 1×1 テクスチャ（alpha=1）で全面選択可能にする
//
//  スプライトパイプラインと同じユニットクワッド頂点を使用する。
//  A チャンネルに bitcast<f32>(actor_raw_id) を書き込む（0 = 背景）。
//  depth_compare = Always のため深度に関わらず常に書き込まれる。
//  DFS 順に描画すると子が親を上書きするため「最前面の子」が選択される。
//  スプライトテクスチャの透明部分（alpha < 0.1）は discard し、
//  空白クリックでのアクター選択を防ぐ。
// ============================================================

// ─── バインドグループ ─────────────────────────────────────────

struct CameraUniform {
    view_proj: mat4x4<f32>,
    view:      mat4x4<f32>,
    position:  vec3<f32>,
    _pad:      f32,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

struct CanvasIdUniform {
    model:    mat4x4<f32>,  // 64 bytes: GPU 列優先モデル行列
    actor_id: u32,          //  4 bytes: raw アクター ID（背景 = 0）
    // vec3<u32> はアライメント 16 になりストラクトが 96 bytes になるため、
    // 個別 u32 × 3 を使って 80 bytes に揃える（Rust 側 CanvasIdUniform と一致）
    _pad0:    u32,
    _pad1:    u32,
    _pad2:    u32,
}
@group(1) @binding(0) var<uniform> u_canvas: CanvasIdUniform;

// スプライトテクスチャ（アルファマスク）
// スプライトなし → 白 1×1 (alpha=1) のフォールバックを使用する
@group(2) @binding(0) var t_sprite: texture_2d<f32>;
@group(2) @binding(1) var s_sprite: sampler;

// ─── 頂点入出力 ───────────────────────────────────────────────

struct VsOut {
    @builtin(position) clip_pos: vec4<f32>,
    /// UV 座標（テクスチャアルファマスク用）
    @location(0) uv: vec2<f32>,
}

// ─── シェーダー ───────────────────────────────────────────────

@vertex
fn vs_main(
    @location(0) position: vec2<f32>,
    @location(1) uv:       vec2<f32>,
) -> VsOut {
    let clip = u_camera.view_proj * u_canvas.model * vec4<f32>(position, 0.0, 1.0);
    return VsOut(clip, uv);
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    // スプライトの透明部分はピッキング対象外にする（空白クリックで選択されない）
    let alpha = textureSample(t_sprite, s_sprite, in.uv).a;
    if alpha < 0.1 { discard; }
    // A チャンネルに actor_id のビットパターンを float として書き込む
    return vec4<f32>(0.0, 0.0, 0.0, bitcast<f32>(u_canvas.actor_id));
}
