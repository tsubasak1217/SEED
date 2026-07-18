// ============================================================
// reflection_rt_hit_on.wgsl — RT 反射ヒットのアルベド解決（バインドレス・本物のテクスチャ色）
//
// バインドレス対応 GPU（TEXTURE_BINDING_ARRAY + 非一様インデックス）で reflection_rt.wgsl に連結する。
// ヒットした三角形の UV をベースカラーテクスチャからサンプルし、base_color_factor を掛けて返す。
//
// 連結順（RT-on）:
//   [cluster_common, reflection_common, ddgi_common, reflection_rt, bindless_common, （本ファイル）]
//   - bindless_common.wgsl が struct BindlessInstanceRecord と BINDLESS_FLAG_ELIGIBLE 等を宣言。
//   - rt_albedo は reflection_rt.wgsl（group3 binding3）で宣言済み（フォールバックに使う）。
//
// group3 の追加バインディング（reflection.rs の拡張 rt_data_bgl と一致）:
//   binding 4 = instance_table（BindlessInstanceRecord 配列。custom_data で引く）
//   binding 5 = UV メガバッファ（vec2<f32> 配列。頂点番号で引く）
//   binding 6 = index メガバッファ（u32 配列。三角形の頂点番号）
//   binding 7 = テクスチャ配列（binding_array<texture_2d<f32>>。albedo_tex_index で引く）
//   binding 8 = 共有サンプラー（リニア・リピート）
// ============================================================

@group(3) @binding(4) var<storage, read> bl_records: array<BindlessInstanceRecord>;
@group(3) @binding(5) var<storage, read> bl_uv:      array<vec2<f32>>;
@group(3) @binding(6) var<storage, read> bl_index:   array<u32>;
@group(3) @binding(7) var bl_tex: binding_array<texture_2d<f32>>;
@group(3) @binding(8) var bl_smp: sampler;

// ヒット先のベースカラー アルベドを返す。
//   ai         : instance_custom_data（TLAS インスタンス番号＝レコード index）
//   prim_index : ヒット三角形のプリミティブ番号（BLAS 内。index_offset + prim_index*3 が先頭）
//   bary       : 重心座標（vec2）。頂点 0/1/2 の重みは (1-x-y, x, y)。
fn rt_hit_base_color(ai: u32, prim_index: u32, bary: vec2<f32>) -> vec3<f32> {
    // フォールバック（平均色）。flags 対象外・tex 0・範囲外はこれを返す（従来のベタ塗りと同一）。
    var fallback = vec3<f32>(0.5, 0.5, 0.5);
    if ai < arrayLength(&rt_albedo) {
        fallback = rt_albedo[ai].rgb;
    }
    if ai >= arrayLength(&bl_records) {
        return fallback;
    }
    let rec = bl_records[ai];
    // バインドレス対象外（UV/index/テクスチャが引けない）なら平均色へ縮退。
    if (rec.flags & BINDLESS_FLAG_ELIGIBLE) == 0u {
        return fallback;
    }

    // 三角形の 3 頂点番号（index メガバッファから）。
    let tri = rec.index_offset + prim_index * 3u;
    let i0 = bl_index[tri + 0u];
    let i1 = bl_index[tri + 1u];
    let i2 = bl_index[tri + 2u];

    // 各頂点の UV（UV メガバッファから。頂点番号 + プリミティブの uv_offset）。
    let uv0 = bl_uv[rec.uv_offset + i0];
    let uv1 = bl_uv[rec.uv_offset + i1];
    let uv2 = bl_uv[rec.uv_offset + i2];

    // 重心補間で UV を復元する（w0=1-x-y, w1=x, w2=y）。
    let w = vec3<f32>(1.0 - bary.x - bary.y, bary.x, bary.y);
    let uv = uv0 * w.x + uv1 * w.y + uv2 * w.z;

    // ベースカラーテクスチャをミップ 0 でサンプルし、base_color_factor を掛ける。
    // 添字（albedo_tex_index）はヒットごとに非一様（NON_UNIFORM_INDEXING が必須）。
    let tex = textureSampleLevel(bl_tex[rec.albedo_tex_index], bl_smp, uv, 0.0);
    return tex.rgb * rec.base_color_factor.rgb;
}
