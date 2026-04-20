// ============================================================
//  hiz_occlusion.wgsl — インスタンス単位 Hi-Z オクルージョンテスト
//
//  各インスタンスのワールド空間 AABB を View-Projection でスクリーン空間に投影し、
//  対応する Hi-Z ミップレベルをサンプルして可視性フラグを書き込む。
//
//  判定規則（標準深度 0=near, 1=far）:
//    min_ndc_z > hiz_max_depth  →  AABB 全体がオクルーダの背後  → 不可視 (0)
//    それ以外                   →  可視の可能性あり             → 可視   (1)
// ============================================================

struct GpuCullData {
    aabb_min: vec3<f32>,
    _pad0:    f32,
    aabb_max: vec3<f32>,
    _pad1:    f32,
}
struct CameraUniform {
    view_proj: mat4x4<f32>,
    view:      mat4x4<f32>,
    position:  vec3<f32>,
    _pad:      f32,
}
struct ScreenInfo {
    width:      f32,
    height:     f32,
    mip_levels: u32,
    _pad:       u32,
}

@group(0) @binding(0) var<storage, read>  aabbs:      array<GpuCullData>;
@group(0) @binding(1) var<uniform>        u_camera:   CameraUniform;
@group(0) @binding(2) var                 hiz_tex:    texture_2d<f32>;
@group(0) @binding(3) var<storage, read_write> visibility: array<u32>;
@group(0) @binding(4) var<uniform>        u_screen:   ScreenInfo;

@compute @workgroup_size(64)
fn cs_main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let inst_idx = gid.x;
    if inst_idx >= arrayLength(&aabbs) { return; }

    let aabb = aabbs[inst_idx];
    let vp   = u_camera.view_proj;

    var min_xy  = vec2<f32>( 1e9,  1e9);
    var max_xy  = vec2<f32>(-1e9, -1e9);
    var min_z   =  1e9f; // NDC z（最小値 = カメラに最近接の頂点）
    var any_behind = false;

    // 8 頂点を NDC に投影
    for (var bx = 0u; bx < 2u; bx++) {
    for (var by = 0u; by < 2u; by++) {
    for (var bz = 0u; bz < 2u; bz++) {
        let p = vec3<f32>(
            select(aabb.aabb_min.x, aabb.aabb_max.x, bx == 1u),
            select(aabb.aabb_min.y, aabb.aabb_max.y, by == 1u),
            select(aabb.aabb_min.z, aabb.aabb_max.z, bz == 1u),
        );
        let clip = vp * vec4<f32>(p, 1.0);
        if clip.w <= 0.0 {
            // カメラ背後の頂点がある → 保守的に可視
            any_behind = true;
        } else {
            let ndc   = clip.xyz / clip.w;
            min_xy    = min(min_xy, ndc.xy);
            max_xy    = max(max_xy, ndc.xy);
            min_z     = min(min_z, ndc.z);
        }
    }}}

    if any_behind { visibility[inst_idx] = 1u; return; }

    // NDC [-1,1] → スクリーン [0,W] x [0,H]
    // wgpu の NDC: Y 上が正、スクリーン座標: Y 下が正 → Y 反転
    let sw = u_screen.width;
    let sh = u_screen.height;
    let scr_min = vec2<f32>((min_xy.x * 0.5 + 0.5) * sw,
                             (1.0 - (max_xy.y * 0.5 + 0.5)) * sh);
    let scr_max = vec2<f32>((max_xy.x * 0.5 + 0.5) * sw,
                             (1.0 - (min_xy.y * 0.5 + 0.5)) * sh);

    // ビューポート外判定
    if scr_max.x < 0.0 || scr_min.x > sw ||
       scr_max.y < 0.0 || scr_min.y > sh {
        // フラスタム外（GPU 側は保守的に可視とする。CPU フラスタムカリングが主担当）
        visibility[inst_idx] = 1u; return;
    }

    // AABB スクリーン矩形の大きさからミップレベルを選択
    let bbox = max(scr_max - scr_min, vec2<f32>(1.0));
    let max_dim  = max(bbox.x, bbox.y);
    let max_mip  = u_screen.mip_levels - 1u;
    let mip      = u32(clamp(floor(log2(max_dim)), 0.0, f32(max_mip)));

    // 選択したミップの解像度にスクリーン座標をスケール
    let mip_size = vec2<i32>(textureDimensions(hiz_tex, mip));
    let scale    = vec2<f32>(f32(mip_size.x) / sw, f32(mip_size.y) / sh);
    let tc0 = clamp(vec2<i32>(scr_min * scale),       vec2<i32>(0), mip_size - 1);
    let tc1 = clamp(vec2<i32>(scr_max * scale),       vec2<i32>(0), mip_size - 1);

    // Hi-Z サンプル（スクリーン矩形の四隅の最大深度）
    let d00 = textureLoad(hiz_tex, vec2<i32>(tc0.x, tc0.y), i32(mip)).r;
    let d10 = textureLoad(hiz_tex, vec2<i32>(tc1.x, tc0.y), i32(mip)).r;
    let d01 = textureLoad(hiz_tex, vec2<i32>(tc0.x, tc1.y), i32(mip)).r;
    let d11 = textureLoad(hiz_tex, vec2<i32>(tc1.x, tc1.y), i32(mip)).r;
    let hiz_max = max(max(d00, d10), max(d01, d11));

    // AABB 最近接頂点の NDC z がオクルーダの最大深度より奥 → オクルージョン
    if min_z > hiz_max {
        visibility[inst_idx] = 0u;
    } else {
        visibility[inst_idx] = 1u;
    }
}
