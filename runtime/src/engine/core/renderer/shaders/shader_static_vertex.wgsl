// ============================================================
// shader_static_vertex.wgsl  —  スタティックメッシュ頂点シェーダ
// ============================================================

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) normal:   vec3<f32>,
    @location(2) tangent:  vec4<f32>,
    @location(3) uv0:      vec2<f32>,
    @location(4) uv1:      vec2<f32>,
    @location(5) color:    vec4<f32>,
}

@vertex
fn vs_main(v: VertexInput, @builtin(instance_index) inst_idx: u32) -> VertexOutput {
    // inst_idx は DrawIndexedIndirect.first_instance 経由でコンピュートシェーダが
    // 書き込んだ実インスタンス番号（u_instances の直接インデックス）。
    let u_model    = u_instances[inst_idx];
    let world_pos4 = u_model.model * vec4<f32>(v.position, 1.0);
    let nm         = u_model.normal_matrix;
    let wn         = normalize((nm * vec4<f32>(v.normal,      0.0)).xyz);
    let wt         = normalize((nm * vec4<f32>(v.tangent.xyz, 0.0)).xyz);
    let wbt        = normalize(cross(wn, wt) * v.tangent.w);

    var out: VertexOutput;
    let clip         = u_camera.view_proj * world_pos4;
    out.clip_pos     = clip;
    // 速度用クリップ座標: フォワード経路は速度 MRT を持たないため prev = curr（速度 0）を入れる。
    // 実際の前フレーム再投影は G-Buffer 専用の gbuffer_static_vertex.wgsl が行う。
    out.curr_clip    = clip;
    out.prev_clip    = clip;
    out.world_pos    = world_pos4.xyz;
    out.world_normal = wn;
    out.world_tan    = wt;
    out.world_bitan  = wbt;
    out.uv0          = v.uv0;
    out.uv1          = v.uv1;
    out.color        = v.color;
    // 法線シャープネス（地形のみ意味を持つ。通常メッシュでは必ず 0 に落ちる）。
    out.sharpness    = decode_vertex_sharpness(v.tangent.w);
    // アクタ単位のセマンティックタグ（インスタンス拡張スロット＝normal_matrix の 4 列目）。
    // 行列演算には一切影響しない領域から読み出し、flat 補間でフラグメントへ運ぶ。
    out.render_tag   = instance_render_tag(u_model);
    return out;
}
