// ============================================================
// gbuffer_write.wgsl  —  G-Buffer 書き込みフラグメント（Phase D3: Deferred 化 Phase A）
//
// ## 役割（単一責任）
// gather_surface（surface_gather.wgsl）が返す Surface を、そのまま G-Buffer の
// 4 枚の MRT（マルチレンダーターゲット）へ焼くだけの薄いフラグメントエントリ。
// ライティングは一切行わない（それは deferred_lighting.wgsl の責務）。
//
// ## G-Buffer レイアウト（確定・帯域概算 28 byte/px 実転送 + 深度。**このファイルが正典**）
//   RT0 Rgba8Unorm  : albedo.rgb（リニア） + occlusion(.a)                       ← 空き無し
//   RT1 Rgba16Float : world normal N.xyz（法線マップ適用後） + authored 法線フラグ(.w)
//   RT2 Rgba8Unorm  : metallic(.r) + roughness(.g) + diffuse_transmission(.b) + user_data(.a)
//   RT3 Rgba16Float : emissive.rgb（HDR） + surface_id(.w)                        ← tag|shading model
//   RT4 Rg16Float   : スクリーンスペース速度（.rg = 前フレーム→今フレームの UV 移動量）
//
// ## RT4（速度 = モーションベクタ）— 第2層の生成物
//   TAA / モーションブラー / L3（合成アセット）の入力素材。**本フェーズでは消費者はいない**
//   （正しく生成され、bind 可能であることまでがスコープ）。
//   ・値の定義・符号・クランプの正典は velocity_common.wgsl（compute_velocity_uv）。
//   ・Rg16Float（実転送 4 byte/px）を選んだ理由: 必要な成分は 2 つだけであり、
//     WebGPU の byte cost 表（4 チャンネル形式は一律 8・Rg16Float は 4）でも最小になるため。
//   ・**リミットの注意**: byte cost 表では速度追加前の 4 枚だけで 8+8+8+8 = 32 と
//     wgpu 既定の `max_color_attachment_bytes_per_sample`（32）にちょうど張り付いている。
//     速度を足すと 36 になるため、renderer/mod.rs がデバイス生成時にこの上限を
//     アダプタ実値（DX12/Vulkan では 8×16 = 128）へ引き上げている。
//   ・アタッチメント枚数は 4→5 で、既定リミット `max_color_attachments = 8` の内側。
//
// ## RT1.w（authored 法線フラグ）
//   0 = 通常メッシュ（ライティングパスは深度復元の幾何法線 Ng を使う）
//   1 = 草／地形など「法線が信頼できる」サーフェス（深度不連続で暴れる Ng を使わず N を使う）
//   deferred_lighting.wgsl の GBUFFER_NORMAL_AUTHORED_THRESHOLD が判定する。**予約ではない**。
//
// ## RT2.a（user_data）／RT3.w（surface_id）— 情報系チャンネル
//   RT2.a = マテリアルの汎用ユーザーデータ（0..1。濡れ・ダメージ等。8bit＝1/255 刻み）。
//   RT3.w = セマンティックタグ(4bit) | シェーディングモデル ID(2bit) を整数として詰めた値。
//           half float は整数 2048 まで無損失なので 0..63 は誤差ゼロで往復する。
//           規約は surface.wgsl の pack_surface_id / Rust の renderer::surface_id。
//   いずれも既定値 0（＝タグ無し・DefaultPBR・ユーザーデータ 0）であり、値を設定していない
//   マテリアル／アクタや、これらを書かないパス（クリア値 0）と完全に整合する。
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

/// G-Buffer 5 枚ぶんの MRT 出力。@location の並びは上記レイアウト表と一致させること。
struct GBufferOut {
    /// RT0: albedo(rgb) + occlusion(a)
    @location(0) albedo_occ: vec4<f32>,
    /// RT1: world normal(xyz) + authored 法線フラグ(w)
    @location(1) normal:     vec4<f32>,
    /// RT2: metallic(r) + roughness(g) + diffuse_transmission(b) + user_data(a)
    @location(2) mr:         vec4<f32>,
    /// RT3: emissive(rgb, HDR) + surface_id(w = tag|shading model のパック値)
    @location(3) emissive:   vec4<f32>,
    /// RT4: スクリーンスペース速度（前フレーム→今フレームの UV 移動量）。
    /// 定義の正典は velocity_common.wgsl（`compute_velocity_uv`）。
    @location(4) velocity:   vec2<f32>,
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
    // .b に拡散透過（葉・布・紙の逆光透け）を焼く。deferred_lighting が g2.b から復元する。
    // .a はマテリアルの汎用ユーザーデータ（0..1）。8bit 量子化で 1/255 刻みになる。
    o.mr         = vec4<f32>(s.metallic, s.roughness, s.diffuse_transmission, s.user_data);
    // .w にセマンティックタグ＋シェーディングモデル ID のパック値を焼く（無損失）。
    o.emissive   = vec4<f32>(s.emissive, pack_surface_id(s.render_tag, s.shading_model));
    // スクリーンスペース速度。頂点段で作った 2 本のクリップ座標をここで透視除算して差を取る
    // （除算を頂点段で行うと補間が遠近的に歪むため、必ずフラグメント段で行う）。
    o.velocity   = compute_velocity_uv(in.curr_clip, in.prev_clip);
    return o;
}
