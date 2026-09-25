// ============================================================
// terrain_forward.wgsl — 地形の前方描画（G-Buffer を使わないフレーム用。段階D の不具合修正）
//
// ## 役割（単一責任）
// 地形メッシュを**前方描画のメインパスで**描くためのフラグメントエントリだけを持つ。
// レイヤブレンドは共有モジュール terrain_layer_blend.wgsl の `terrain_blend_surface`
// （G-Buffer 版と同じ関数）で作り、その結果から Surface を組んで、フォワードの不透明メッシュと
// 同じ `evaluate_lighting`（アンビエント／GI・クラスタのライト・影・PBR）へ渡す。
//
// ## なぜ要るのか（直した不具合）
// 描画品質プリセット `mobile`（Android の既定）は `deferred=false`（前方描画）である。
// 以前は前方描画の地形を汎用メッシュのシェーダ（shader_fragment.wgsl）で描いており、
// 地形の頂点カラー（＝レイヤの**重み**。R=スロット0・G=スロット1…）がそのまま色として
// 乗算されて、島が赤・緑のベタ塗りに見えていた（デファードは地形専用の G-Buffer 書き込みで
// レイヤをブレンドしていたので正常だった）。
//
// ## デファードとの対応（同じ見た目にするための約束）
//   - 面の情報（アルベド・法線・メタリック・ラフネス・轍のキャビティ）… 同じ関数で作る。
//   - 幾何法線（geo_gate）… デファードは地形の RT1.w=1（authored）により深度復元の Ng ではなく
//     G-Buffer の法線 N を使う。ここでも Ng・Nv に合成済みの法線をそのまま入れて同じ扱いにする。
//   - occlusion … デファードは「キャビティ × SSAO」。前方描画には SSAO が無い（mobile は ao=off）。
//   - SSGI・RT ソフト影マスク・水中コースティクス … デファード専用。`var s: Surface;` のゼロ初期化で
//     screen_gi.a=0（フラットへ）・shadow_mask_valid=0（インライン影へ）になり、コースティクスは
//     乗算項なので中立値を明示する（surface_gather.wgsl と同じ規約）。
//
// ## 連結（terrain_forward.rs::terrain_forward_shader_sources と一致必須）
//   フォワードの不透明メッシュ（pipelines/mesh.toml）の連結から surface_gather.wgsl /
//   shader_fragment.wgsl を外し、terrain_layer_blend.wgsl と本ファイルを足したもの。
//   RT 影の変種は rt_shadow_off.wgsl を rt_shadow_on.wgsl + rt_shadow_tint_avg.wgsl に替える（mesh_rt.toml と同じ）。
//
// ## バインドグループ（パイプラインレイアウトは terrain_forward.rs が既存の BGL から組む）
//   group0 = camera / group1 = model / group2 = material … MeshPipeline と同じ
//   group3 = 地形レイヤ定義（G-Buffer 版と同じ TerrainLayerResources のバインドグループをそのまま使う）
//   group4 = ライト＋シャドウ（＋RT 変種は TLAS）… MeshPipeline / RtMeshPipelines と同じ
// ============================================================

/// 前方描画で地形を描く。出力はリニア HDR（トーンマップは後段の post_tonemap.wgsl）。
@fragment
fn fs_terrain_forward(
    in: VertexOutput,
    @builtin(front_facing) front_facing: bool,
) -> @location(0) vec4<f32> {
    // ── レイヤブレンド（画面微分を使うので必ず先頭＝一様制御フローで呼ぶ）──
    let b = terrain_blend_surface(in, front_facing);

    // ── Surface を組む（デファードのライティングパスが地形の G-Buffer から復元する値と同じ意味）──
    var s: Surface;
    // 水中の直達光変調はデファード専用。透過率（rgb）は乗算項なので中立値（透過率 1・集光 0）を必ず入れる。
    s.caustics              = vec4<f32>(1.0, 1.0, 1.0, 0.0);
    // 影の屈折オフセットもデファード専用。加算項なので中立値は 0（ゼロ初期化と同じだが意図を残す）。
    s.shadow_refract_offset = vec2<f32>(0.0, 0.0);
    s.world_pos     = in.world_pos;
    s.normal        = b.normal;
    // 地形は authored 法線（デファードの RT1.w=1）: 幾何法線・補間法線にも合成済みの法線を使う。
    s.geo_normal    = b.normal;
    s.vertex_normal = b.normal;
    s.albedo        = b.albedo;
    // 不透明（地形は常に不透明で描く）。
    s.alpha         = 1.0;
    s.metallic      = b.metallic;
    s.roughness     = b.roughness;
    // 地形は逆光透け・自己発光を持たない（G-Buffer 版も 0 を焼いている）。
    s.diffuse_transmission = 0.0;
    s.emissive      = vec3<f32>(0.0, 0.0, 0.0);
    s.occlusion     = b.occlusion;
    // フラグメント座標（RT ソフト影のノイズの種。clip_pos はフラグメント段ではピクセル座標）。
    s.frag_coord    = in.clip_pos.xy;
    // 情報系: 地形はタグ無し・既定の PBR（G-Buffer 版の surface_id パック値 0 と同じ）。
    s.render_tag    = 0u;
    s.shading_model = SHADING_MODEL_DEFAULT_PBR;
    s.user_data     = 0.0;

    return vec4<f32>(evaluate_lighting(s), s.alpha);
}
