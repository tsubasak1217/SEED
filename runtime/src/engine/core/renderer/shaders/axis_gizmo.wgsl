// ============================================================
//  axis_gizmo.wgsl — スクリーン空間 2D ジオメトリシェーダー
//
//  バインドグループなし。NDC 座標を直接渡す。
//  軸ギズモのライン・ドットをアルファブレンドで描画する。
// ============================================================

struct VertIn {
    @location(0) position : vec2<f32>,
    @location(1) color    : vec4<f32>,
}

struct VertOut {
    @builtin(position) clip_pos : vec4<f32>,
    @location(0)       color    : vec4<f32>,
}

@vertex
fn vs_main(in: VertIn) -> VertOut {
    var out : VertOut;
    out.clip_pos = vec4<f32>(in.position, 0.0, 1.0);
    out.color    = in.color;
    return out;
}

@fragment
fn fs_main(in: VertOut) -> @location(0) vec4<f32> {
    if in.color.a < 0.01 { discard; }
    return in.color;
}
