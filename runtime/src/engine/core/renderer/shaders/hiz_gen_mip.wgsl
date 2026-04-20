// ============================================================
//  hiz_gen_mip.wgsl — Hi-Z ピラミッド mip[n-1] → mip[n]
//
//  2×2 ブロックの MAX 深度を書き込む（保守的オクルージョン）。
//  標準深度規約（0=near, 1=far）で MAX を保持することで
//  「この領域のオクルーダは最大でもこの深度」を表現する。
// ============================================================

@group(0) @binding(0) var src_tex: texture_2d<f32>;
@group(0) @binding(1) var dst_tex: texture_storage_2d<r32float, write>;

@compute @workgroup_size(8, 8)
fn cs_main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let dst_size = vec2<i32>(textureDimensions(dst_tex));
    if i32(gid.x) >= dst_size.x || i32(gid.y) >= dst_size.y { return; }

    let src_size = vec2<i32>(textureDimensions(src_tex, 0));
    let base     = vec2<i32>(gid.xy) * 2;

    let c00 = clamp(base + vec2<i32>(0, 0), vec2<i32>(0), src_size - 1);
    let c10 = clamp(base + vec2<i32>(1, 0), vec2<i32>(0), src_size - 1);
    let c01 = clamp(base + vec2<i32>(0, 1), vec2<i32>(0), src_size - 1);
    let c11 = clamp(base + vec2<i32>(1, 1), vec2<i32>(0), src_size - 1);

    let d = max(
        max(textureLoad(src_tex, c00, 0).r, textureLoad(src_tex, c10, 0).r),
        max(textureLoad(src_tex, c01, 0).r, textureLoad(src_tex, c11, 0).r),
    );
    textureStore(dst_tex, vec2<i32>(gid.xy), vec4<f32>(d, 0.0, 0.0, 1.0));
}
