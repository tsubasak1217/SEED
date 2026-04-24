// ============================================================
//  icon_overlay.wgsl — テクスチャ付きクワッドシェーダー
//
//  NDC 座標を直接渡す。バインドグループ 0 にテクスチャ + サンプラー。
//  アルファブレンドで UI オーバーレイとして描画する。
// ============================================================

struct VertIn {
    @location(0) position : vec2<f32>,
    @location(1) uv       : vec2<f32>,
}

struct VertOut {
    @builtin(position) clip_pos : vec4<f32>,
    @location(0)       uv       : vec2<f32>,
}

@group(0) @binding(0) var t_icon : texture_2d<f32>;
@group(0) @binding(1) var s_icon : sampler;

@vertex
fn vs_main(in: VertIn) -> VertOut {
    var out : VertOut;
    out.clip_pos = vec4<f32>(in.position, 0.0, 1.0);
    out.uv       = in.uv;
    return out;
}

@fragment
fn fs_main(in: VertOut) -> @location(0) vec4<f32> {
    let color = textureSample(t_icon, s_icon, in.uv);
    if color.a < 0.01 { discard; }
    return color;
}
