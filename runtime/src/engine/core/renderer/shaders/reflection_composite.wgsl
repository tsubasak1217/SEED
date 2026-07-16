// ============================================================
// reflection_composite.wgsl — 反射 RT をシーン HDR へ加算合成（fragment fs_composite）
//
// 反射専用 RT（RT_REFLECTION）をサンプルして返すだけ。実際の加算はパイプライン側の
// Additive ブレンド（One/One）＋ LoadOp::Load が行う（renderer/reflection.rs 参照）。
//
// 自前完結の理由: reflection_common.wgsl は group0 binding0 に CameraUniform を宣言するため
// composite の group0 binding0（反射テクスチャ）と衝突する。ゆえに reflection_common を連結せず
// 頂点・グループを自前で宣言する（naga テストも単体で行う）。
// ============================================================

@group(0) @binding(0) var t_reflection: texture_2d<f32>;
@group(0) @binding(1) var s_reflection: sampler;

const COMPOSITE_FS_POS: array<vec2<f32>, 3> = array<vec2<f32>, 3>(
    vec2<f32>(-1.0, -1.0),
    vec2<f32>( 3.0, -1.0),
    vec2<f32>(-1.0,  3.0),
);

struct CompositeVsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0)       uv:  vec2<f32>,
}

@vertex
fn vs_composite(@builtin(vertex_index) vi: u32) -> CompositeVsOut {
    var o: CompositeVsOut;
    let p = COMPOSITE_FS_POS[vi];
    o.pos = vec4<f32>(p, 0.0, 1.0);
    o.uv = vec2<f32>(p.x * 0.5 + 0.5, 0.5 - p.y * 0.5);
    return o;
}

@fragment
fn fs_composite(vin: CompositeVsOut) -> @location(0) vec4<f32> {
    return textureSampleLevel(t_reflection, s_reflection, vin.uv, 0.0);
}
