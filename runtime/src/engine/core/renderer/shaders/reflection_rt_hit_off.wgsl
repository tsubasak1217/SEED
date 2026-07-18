// ============================================================
// reflection_rt_hit_off.wgsl — RT 反射ヒットのアルベド解決（従来・平均色フォールバック）
//
// バインドレス非対応 GPU（TEXTURE_BINDING_ARRAY 等が無い）で reflection_rt.wgsl に連結する。
// binding_array を一切宣言しないため、非対応 GPU でも BGL 生成が通る（＝従来経路と完全に同一）。
//
// ヒット先の色は「プリミティブ平均アルベド storage（rt_albedo, group3 binding3）」を
// instance_custom_data で引くベタ塗り近似。prim_index / bary は使わない（引数だけ受ける）。
//
// 連結順（RT-off）: [cluster_common, reflection_common, ddgi_common, reflection_rt, （本ファイル）]
// rt_albedo は reflection_rt.wgsl（group3 binding3）で宣言済み。
// ============================================================

// ヒット先のベースカラー アルベドを返す（平均色）。
fn rt_hit_base_color(ai: u32, prim_index: u32, bary: vec2<f32>) -> vec3<f32> {
    if ai < arrayLength(&rt_albedo) {
        return rt_albedo[ai].rgb;
    }
    return vec3<f32>(0.5, 0.5, 0.5);
}
