// ============================================================
// gbuffer_write.wgsl  —  G-Buffer 書き込みフラグメント（Phase D3: Deferred 化 Phase A）
//
// ## 役割（単一責任）
// gather_surface（surface_gather.wgsl）が返す Surface を、そのまま G-Buffer の
// 4 枚の MRT（マルチレンダーターゲット）へ焼くだけの薄いフラグメントエントリ。
// ライティングは一切行わない（それは deferred_lighting.wgsl の責務）。
//
// ## G-Buffer レイアウト（確定・帯域概算 24 byte/px + 深度）
//   RT0 Rgba8Unorm  : albedo.rgb（リニア） + occlusion.a
//   RT1 Rgba16Float : world normal N.xyz（法線マップ適用後のシェーディング法線）, .w = 0
//   RT2 Rgba8Unorm  : metallic(.r) + roughness(.g) + 0(.b) + 0(.a)  ※空き 2ch は将来用
//   RT3 Rgba16Float : emissive.rgb（HDR） , .w = 0
//
// 設計判断（コメントとして残す）:
//   - 法線は Rgba16Float に xyz を直接格納する。オクタヘドラル圧縮（8:8 で 1 チャンネルに
//     収める手法）は帯域をさらに縮められるが、精度・実装の単純さを優先して Phase A では
//     見送る（将来の最適化候補）。
//   - emissive はテクスチャ由来で 1.0 を超える HDR 値を取り得るため Rgba16Float 必須。
//   - metallic/roughness は仕様上 [0,1] に収まるため Rgba8Unorm で十分（精度要求が低い）。
//   - 深度は別途 DEPTH_FORMAT（Depth24PlusStencil8）のアタッチメントに書く
//     （本ファイルでは扱わない。パイプライン側の depth_stencil 設定を参照）。
//
// ## 依存
// vs_main（頂点シェーダ）は shader_static_vertex.wgsl / shader_skinned_vertex.wgsl を
// そのまま流用する（本ファイルには含めない）。gather_surface は surface_gather.wgsl。
// light_common.wgsl / cluster_common.wgsl は連結しない（G-Buffer 書き込みはライト情報
// を必要としないため。連結すると使わない group 4 バインディングが要求されてしまう）。
// ============================================================

/// G-Buffer 4 枚ぶんの MRT 出力。@location の並びは上記レイアウト表と一致させること。
struct GBufferOut {
    /// RT0: albedo(rgb) + occlusion(a)
    @location(0) albedo_occ: vec4<f32>,
    /// RT1: world normal(xyz) + 0(w)
    @location(1) normal:     vec4<f32>,
    /// RT2: metallic(r) + roughness(g) + 予約(b,a)
    @location(2) mr:         vec4<f32>,
    /// RT3: emissive(rgb, HDR) + 0(w)
    @location(3) emissive:   vec4<f32>,
}

/// G-Buffer ジオメトリパスのフラグメントエントリ。
///
/// マテリアル採取（gather_surface）の結果を Surface のフィールドそのまま MRT へ焼く。
/// Mask マテリアルのアルファテスト（discard）は gather_surface 内部で発火するため、
/// 本関数では意識する必要がない（呼び出しに到達した時点でテスト済み＝不透明扱い）。
@fragment
fn fs_gbuffer(in: VertexOutput, @builtin(front_facing) front_facing: bool) -> GBufferOut {
    let s = gather_surface(in, front_facing);

    var o: GBufferOut;
    o.albedo_occ = vec4<f32>(s.albedo, s.occlusion);
    o.normal     = vec4<f32>(s.normal, 0.0);
    o.mr         = vec4<f32>(s.metallic, s.roughness, 0.0, 0.0);
    o.emissive   = vec4<f32>(s.emissive, 0.0);
    return o;
}
