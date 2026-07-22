// ============================================================
//  hiz_copy_depth.wgsl — 深度バッファ(DepthOnly aspect) → Hi-Z mip 0(R32Float)
//  実際の深度フォーマットは Depth24PlusStencil8（DEPTH_FORMAT）。texture_depth_2d は
//  その DepthOnly ビューをサンプルするため、フォーマットに依らずそのまま動く。
// ============================================================

@group(0) @binding(0) var depth_tex: texture_depth_2d;
@group(0) @binding(1) var dst_tex:   texture_storage_2d<r32float, write>;

@compute @workgroup_size(8, 8)
fn cs_main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let dst_size = textureDimensions(dst_tex);
    if gid.x >= dst_size.x || gid.y >= dst_size.y { return; }
    let d = textureLoad(depth_tex, vec2<i32>(gid.xy), 0);
    textureStore(dst_tex, vec2<i32>(gid.xy), vec4<f32>(d, 0.0, 0.0, 1.0));
}
