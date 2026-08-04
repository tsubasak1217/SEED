// ============================================================
//  water_reflection_hit_on.wgsl — 水面反射ヒットのアルベド解決（バインドレス・実テクスチャ）
//
//  ## 役割（単一責任）
//  反射レイが**画面外／遮蔽裏**に当たったときのベースカラーを 1 つ返す。
//  本ファイルはヒット三角形の UV を復元して**ベースカラーテクスチャを実サンプル**し、
//  `base_color_factor` を掛けて返す（＝水面に映る画面外の物体が本来の模様・色で映る）。
//
//  ## なぜ必要か（W5.2 の仕様限界の解消）
//  従来は平均アルベド（`wr_albedo`）のベタ塗りだった。テクスチャの平均色は多くのモデルで
//  灰色に寄るため、**画面外にあるカラフルなモデルが一様な灰色の塊として映る**
//  （スキンメッシュ・非スキンを問わず発生する。原因はスキン側の紐付け漏れではない）。
//  不透明 RT 反射は D6 で既にこの経路を持っており（`reflection_rt_hit_on.wgsl`）、
//  本ファイルはその方式を水面反射へそのまま持ち込んだものである。
//
//  ## 連結順（RT-bindless）
//    [water_height_field, cluster_common, ddgi_common, water_reflection_common,
//     water_reflection_rt, bindless_common, （本ファイル）]
//    - `bindless_common.wgsl` が `BindlessInstanceRecord` と `BINDLESS_FLAG_*` を宣言。
//    - `wr_albedo`（group4 binding7）と `WATER_REFL_MISSING_ALBEDO` は
//      `water_reflection_rt.wgsl` が宣言済み（フォールバックに使う）。
//
//  ## group3 の追加バインディング（`water_reflection.rs` の BindGroup と一致）
//    binding 6  = instance_table（`BindlessInstanceRecord` 配列。custom_data で引く）
//    binding 7  = UV メガバッファ（vec2<f32> 配列。頂点番号で引く）
//    binding 8  = index メガバッファ（u32 配列。三角形の頂点番号）
//    binding 9  = テクスチャ配列（`binding_array<texture_2d<f32>>`。albedo_tex_index で引く）
//    binding 10 = 共有サンプラー（リニア・リピート）
//
//  【なぜ group3 なのか】group4 は GI パラメータ（uniform）を持つため
//  binding_array を同居させられない（wgpu: "Bind groups may not contain both a
//  binding array and a uniform buffer"）。group3 は W5.2 当時 `wr_sky` が uniform
//  だったが、本フェーズで storage へ変えて uniform ゼロにしてある。
//  group1/group2 は水面パスと共有する高さ場モジュールが宣言しており、番号を動かすと
//  水面パス側まで巻き込むため触らない（＝グループ総数 5 のまま拡張できる唯一の場所）。
// ============================================================

@group(3) @binding(6)  var<storage, read> wrbl_records: array<BindlessInstanceRecord>;
@group(3) @binding(7)  var<storage, read> wrbl_uv:      array<vec2<f32>>;
@group(3) @binding(8)  var<storage, read> wrbl_index:   array<u32>;
@group(3) @binding(9)  var wrbl_tex: binding_array<texture_2d<f32>>;
@group(3) @binding(10) var wrbl_smp: sampler;

/// ヒット先のベースカラー アルベドを返す。
///   ai         : instance_custom_data（TLAS インスタンス番号＝レコード index）
///   prim_index : ヒット三角形のプリミティブ番号（BLAS 内。index_offset + prim_index*3 が先頭）
///   bary       : 重心座標（vec2）。頂点 0/1/2 の重みは (1-x-y, x, y)。
///
/// テクスチャが引けないインスタンス（UV/index 未登録・テクスチャ無し・範囲外）は
/// 従来どおり平均アルベドへ縮退する＝**この変種でも見た目が退化することはない**。
fn water_refl_hit_albedo(ai: u32, prim_index: u32, bary: vec2<f32>) -> vec3<f32> {
    // フォールバック（平均色）。従来のベタ塗りとまったく同じ値。
    var fallback = WATER_REFL_MISSING_ALBEDO;
    if ai < arrayLength(&wr_albedo) {
        fallback = wr_albedo[ai].rgb;
    }
    if ai >= arrayLength(&wrbl_records) {
        return fallback;
    }
    let rec = wrbl_records[ai];
    // バインドレス対象外（UV/index/テクスチャが引けない）なら平均色へ縮退。
    if (rec.flags & BINDLESS_FLAG_ELIGIBLE) == 0u {
        return fallback;
    }

    // 三角形の 3 頂点番号（index メガバッファから）。
    let tri = rec.index_offset + prim_index * 3u;
    let i0 = wrbl_index[tri + 0u];
    let i1 = wrbl_index[tri + 1u];
    let i2 = wrbl_index[tri + 2u];

    // 各頂点の UV（UV メガバッファから。頂点番号 + プリミティブの uv_offset）。
    let uv0 = wrbl_uv[rec.uv_offset + i0];
    let uv1 = wrbl_uv[rec.uv_offset + i1];
    let uv2 = wrbl_uv[rec.uv_offset + i2];

    // 重心補間で UV を復元する（w0=1-x-y, w1=x, w2=y）。
    let w = vec3<f32>(1.0 - bary.x - bary.y, bary.x, bary.y);
    let uv = uv0 * w.x + uv1 * w.y + uv2 * w.z;

    // ベースカラーテクスチャをミップ 0 でサンプルし、base_color_factor を掛ける。
    // 添字（albedo_tex_index）はヒットごとに非一様（NON_UNIFORM_INDEXING が必須）。
    let tex = textureSampleLevel(wrbl_tex[rec.albedo_tex_index], wrbl_smp, uv, 0.0);
    return tex.rgb * rec.base_color_factor.rgb;
}
