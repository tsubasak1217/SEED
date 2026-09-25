// ============================================================
// terrain_gbuffer_write.wgsl — 地形レイヤブレンド G-Buffer 書き込み（Terrain T2b）
//
// ## 役割（単一責任）
// 地形メッシュ専用の G-Buffer ジオメトリパスのフラグメントエントリだけを持つ。
// レイヤブレンドそのもの（スプラット重み × triplanar × detile・法線シャープネス・
// 地表カバー・踏み固め）は共有モジュール terrain_layer_blend.wgsl の
// `terrain_blend_surface` が作り、本ファイルはその結果を G-Buffer の MRT へ焼くだけである。
// 同じ関数を前方描画（terrain_forward.wgsl）も呼ぶので、デファードと前方描画で
// 地表の見た目（色・法線・粗さ）が食い違わない。
//
// ライティング・影・RT 反射は G-Buffer を読むライティングパスがそのまま担当するため、
// 地形専用のライティングコードは一切書かない（既存の deferred 経路に完全に乗る）。
//
// ## 連結（terrain_gbuffer.rs::terrain_gbuffer_shader_sources と一致必須）
//   ["shader_common.wgsl", "velocity_math.wgsl", "velocity_common.wgsl",
//    "gbuffer_static_vertex.wgsl", "terrain_layer_blend.wgsl", "terrain_gbuffer_write.wgsl"]
// surface.wgsl / surface_gather.wgsl は連結しない（Surface を経由せず直接 MRT を作るため。
// 地形はマテリアルテクスチャではなくレイヤ配列テクスチャを読むので gather_surface が使えない）。
//
// ## G-Buffer レイアウト（gbuffer_write.wgsl と同一。正典はそちら）
//   RT0 Rgba8Unorm  : albedo.rgb + occlusion.a
//   RT1 Rgba16Float : world normal.xyz + authored 法線フラグ.w
//   RT2 Rgba8Unorm  : metallic.r + roughness.g + diffuse_transmission.b + 予約 a
//   RT3 Rgba16Float : emissive.rgb(HDR) + surface_id.a
//   RT4 Rg16Float   : スクリーンスペース速度
// ============================================================

/// 地形 G-Buffer の MRT 出力（gbuffer_write.wgsl の GBufferOut と同一レイアウト）。
struct TerrainGBufferOut {
    @location(0) albedo_occ: vec4<f32>,
    @location(1) normal:     vec4<f32>,
    @location(2) mr:         vec4<f32>,
    @location(3) emissive:   vec4<f32>,
    /// RT4: スクリーンスペース速度（前フレーム→今フレームの UV 移動量）。
    /// 地形は静的なので実質「カメラ由来の速度」だけが乗る。頂点シェーダは
    /// gbuffer_static_vertex.wgsl を共有しており、地形チャンクのインスタンス行列は
    /// 前フレームと同一なので `prev_model == model` に自動的に縮退する。
    ///
    /// 【LOD チャンク差し替え時の挙動】地形 LOD の入れ替えはメッシュ（頂点／インデックス）の
    /// 差し替えであってインスタンス行列は変わらない。しかも本方式は「**今フレームの**頂点を
    /// 前フレームのカメラで再投影する」ため、差し替えフレームでも値はカメラ由来の速度に
    /// なるだけで爆発しない（前フレームの頂点位置を参照していないことが効いている）。
    @location(4) velocity:   vec2<f32>,
}

// ============================================================
//  フラグメント本体
// ============================================================

/// 地形レイヤをブレンドして G-Buffer へ焼く。
@fragment
fn fs_terrain_gbuffer(
    in: VertexOutput,
    @builtin(front_facing) front_facing: bool,
) -> TerrainGBufferOut {
    // ── レイヤブレンド（共有モジュール。画面微分を使うので必ず先頭＝一様制御フローで呼ぶ）──
    let b = terrain_blend_surface(in, front_facing);

    // ── G-Buffer へ書き込む ──
    //   occlusion は轍のキャビティのみ（地形は AO テクスチャを持たない。SSAO は後段のパスが乗せる）。
    //   emissive / diffuse_transmission は地形では常に 0。
    var o: TerrainGBufferOut;
    o.albedo_occ = vec4<f32>(b.albedo, b.occlusion);
    // RT1.w=1: authored 法線フラグ（地形の信頼できる法線を geo_gate に使わせる）。
    o.normal     = vec4<f32>(b.normal, TERRAIN_NORMAL_AUTHORED_FLAG);
    // .a = user_data（汎用ユーザーデータ）。地形はレイヤ定義側に相当する概念を持たないため 0。
    o.mr         = vec4<f32>(b.metallic, b.roughness, 0.0, 0.0);
    // .a = surface_id（セマンティックタグ | シェーディングモデル ID）。
    // 地形はアクタではなくタグを持たず、シェーディングも DefaultPBR なのでパック値 0 が正しい
    // （gbuffer_write.wgsl の pack_surface_id(RENDER_TAG_NONE, SHADING_MODEL_DEFAULT_PBR) と同値）。
    o.emissive   = vec4<f32>(0.0, 0.0, 0.0, 0.0);
    // スクリーンスペース速度（velocity_common.wgsl の定義に従う）。
    o.velocity   = compute_velocity_uv(in.curr_clip, in.prev_clip);
    return o;
}
