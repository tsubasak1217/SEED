// ============================================================
//  water_reflection_hit_off.wgsl — 水面反射ヒットのアルベド解決（従来・平均色）
//
//  ## 役割（単一責任）
//  反射レイが**画面外／遮蔽裏**に当たったときのベースカラーを 1 つ返す。
//  本ファイルは「プリミティブ平均アルベド storage（`wr_albedo`）を
//  instance_custom_data で引くベタ塗り」＝バインドレス非対応 GPU 向けの縮退経路である。
//
//  ## なぜ変種として切り出すのか
//  バインドレス版（`water_reflection_hit_on.wgsl`）は `binding_array<texture_2d<f32>>` を
//  宣言する。非対応 GPU では **その宣言があるだけで BGL 生成が落ちる**ため、
//  シェーダ内分岐では回避できず、連結するファイルごと差し替える必要がある。
//  不透明 RT 反射の `reflection_rt_hit_off.wgsl` / `_on.wgsl` とまったく同じ流儀である。
//
//  ## 連結順（RT-off）
//    [water_height_field, cluster_common, ddgi_common, water_reflection_common,
//     water_reflection_rt, （本ファイル）]
//  `wr_albedo`（group4 binding7）と `WATER_REFL_MISSING_ALBEDO` は
//  `water_reflection_rt.wgsl` が宣言済み。
// ============================================================

/// ヒット先のベースカラー（プリミティブ平均アルベド）。
/// `prim_index` / `bary` は使わない（バインドレス版と**同一シグネチャ**にするために受けるだけ）。
fn water_refl_hit_albedo(ai: u32, prim_index: u32, bary: vec2<f32>) -> vec3<f32> {
    if ai < arrayLength(&wr_albedo) {
        return wr_albedo[ai].rgb;
    }
    return WATER_REFL_MISSING_ALBEDO;
}
