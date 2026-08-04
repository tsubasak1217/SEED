// pipeline_rs — パイプライン定義
//
// レンダーパイプラインは TOML 設定 + WGSL リフレクションで自動構築。
// コンピュートパイプライン（MeshletCullPipeline, SkinComputePipeline）は手動定義。

use super::pipeline_config::{RenderPipelineBuilder, parse_compare};
use crate::engine::core::loader::model::{CULL_FACE_COUNT, CULL_FACE_VARIANTS};

// ============================================================
//  カリング面バリアント（マテリアル単位のカリング面設定）
// ============================================================

/// カリング面 3 種（Back / Front / None）ぶんのレンダーパイプライン。
/// 添字は `CullFace::index()`（0=Back, 1=Front, 2=None）。
/// 描画側はプリミティブのマテリアルの `CullFace` でこの配列を引く。
pub type CullPipelineSet = [wgpu::RenderPipeline; CULL_FACE_COUNT];

/// 1 つの TOML から cull_mode だけを差し替えて 3 本のパイプラインを生成する。
///
/// TOML を 3 ファイルに増やす代わりに、`RenderPipelineBuilder::with_cull_mode` で
/// cull_mode のみを上書きしてビルドする（設定の重複を作らないため）。
/// TOML 側の `cull_mode` 値は本ヘルパー経由の生成では常に無視される。
///
/// 返り値の `Vec<BindGroupLayout>` は Back バリアントのビルド結果。3 本は同一 WGSL・同一
/// レイアウトから作られるため、この BGL で作った BindGroup は 3 本すべてで使える
/// （wgpu の BGL 等価性に依拠する既存慣例：canvas_id/collider_pick・transparent と同じ）。
fn build_cull_variants<F>(
    device:     &wgpu::Device,
    toml_src:   &str,
    sf:         wgpu::TextureFormat,
    df:         wgpu::TextureFormat,
    cache:      Option<&wgpu::PipelineCache>,
    label_base: &str,
    resolve:    F,
) -> (CullPipelineSet, Vec<wgpu::BindGroupLayout>)
where
    F: Fn(&str) -> &'static str + Copy,
{
    // Back / Front / None の順にビルドし、BGL は最初（Back）のものだけ保持する。
    let mut bgls: Option<Vec<wgpu::BindGroupLayout>> = None;
    let pipelines: CullPipelineSet = std::array::from_fn(|i| {
        let face  = CULL_FACE_VARIANTS[i];
        // ラベルは "mesh_pbr_cull_Back" 等（wgpu の検証エラーでバリアントを特定できるように）。
        let label = format!("{label_base}_cull_{}", face.as_str());
        let (pipeline, built_bgls) = RenderPipelineBuilder::new(device, toml_src, sf, df)
            .with_label(&label)
            .with_cull_mode(face.as_str())
            .with_cache(cache)
            .build(resolve);
        if bgls.is_none() { bgls = Some(built_bgls); }
        pipeline
    });
    // from_fn は 0..CULL_FACE_COUNT を必ず 1 回以上呼ぶため bgls は必ず Some。
    (pipelines, bgls.expect("build_cull_variants: BGL が生成されていない"))
}

/// TOML から polygon_mode=Line・cull_mode=None のワイヤーフレームバリアントを 1 本生成する。
///
/// エディタのシーンビュー「ワイヤーフレーム」モード専用。通常パイプラインと同一 TOML・
/// 同一 group4 レイアウトのため、既存の lights BindGroup をそのまま使える。
/// `PolygonMode::Line` は Features::POLYGON_MODE_LINE（ネイティブ限定）対応 GPU でのみ有効なため、
/// 非対応時は `None` を返し、描画側は塗り（Unlit）へフォールバックする（クラッシュさせない）。
fn build_wireframe_variant(
    device:     &wgpu::Device,
    toml_src:   &str,
    sf:         wgpu::TextureFormat,
    df:         wgpu::TextureFormat,
    cache:      Option<&wgpu::PipelineCache>,
    label:      &str,
) -> Option<wgpu::RenderPipeline> {
    if !super::view_mode::wireframe_supported() {
        return None;
    }
    let (pipeline, _bgls) = RenderPipelineBuilder::new(device, toml_src, sf, df)
        .with_label(label)
        .with_cull_mode("None")
        .with_polygon_mode(wgpu::PolygonMode::Line)
        .with_cache(cache)
        .build(get_shader_source);
    Some(pipeline)
}

// ============================================================
//  シェーダーソースリゾルバ
// ============================================================

pub(crate) fn get_shader_source(name: &str) -> &'static str {
    match name {
        // Clustered Lighting の共有定義（定数・構造体・索引関数。バインディングなし）。
        // light_common.wgsl の group 4 binding 7〜9 がこの構造体を参照するため、
        // TOML の shader_sources では必ず cluster_common.wgsl → shader_common.wgsl →
        // light_common.wgsl の順に置くこと（Phase C1／Phase D3 Phase A）。
        "cluster_common.wgsl"        => include_str!("shaders/cluster_common.wgsl"),
        // PBR ヘルパー関数（distribution_ggx 等。バインディングなし）。shader_common.wgsl と
        // デファードのライティングパスの両方から連結する（Phase D3 Phase A で分離）。
        "pbr_common.wgsl"            => include_str!("shaders/pbr_common.wgsl"),
        "shader_common.wgsl"         => include_str!("shaders/shader_common.wgsl"),
        // ライト（GpuLight/LightMeta）＋クラスタ参照。shader_common.wgsl の直後に連結すること
        // （cluster_common → shader_common → light_common の順。Phase D3 Phase A で分離）。
        "ddgi_common.wgsl"           => include_str!("shaders/ddgi_common.wgsl"),
        "light_common.wgsl"          => include_str!("shaders/light_common.wgsl"),
        "shader_static_vertex.wgsl"  => include_str!("shaders/shader_static_vertex.wgsl"),
        "shader_skinned_vertex.wgsl" => include_str!("shaders/shader_skinned_vertex.wgsl"),
        // PBR シェーディングの 3 段分割（採取 → ライト評価 → エントリ）。
        // 将来の G-Buffer ライティングパスからも lighting_eval.wgsl を再利用するため、
        // マテリアル採取（surface_gather）とライト評価（lighting_eval）を別ファイルにしている。
        "surface.wgsl"               => include_str!("shaders/surface.wgsl"),
        "surface_gather.wgsl"        => include_str!("shaders/surface_gather.wgsl"),
        // シェーディング契約 v1（L3-a）: ShadingSurface / LightSample / 標準ライブラリ /
        // モデル 0（shade_light・shade_model_0）。バインディングを持たない純定義のため、
        // lighting_eval.wgsl を連結する**すべての**パスに必ず一緒に連結すること。
        "shading_contract.wgsl"      => include_str!("shaders/shading_contract.wgsl"),
        // 契約関数 shade_surface の既定実装（アセット未指定時＝モデル 0 固定）。
        // アセット指定時は Rust が生成した同名関数がこれの代わりに連結される。
        "shading_dispatch.wgsl"      => include_str!("shaders/shading_dispatch.wgsl"),
        "lighting_eval.wgsl"         => include_str!("shaders/lighting_eval.wgsl"),
        "shader_fragment.wgsl"       => include_str!("shaders/shader_fragment.wgsl"),
        "shadow.wgsl"                => include_str!("shaders/shadow.wgsl"),
        "rt_shadow_off.wgsl"         => include_str!("shaders/rt_shadow_off.wgsl"),
        "rt_shadow_on.wgsl"          => include_str!("shaders/rt_shadow_on.wgsl"),
        "rt_shadow_tint_avg.wgsl"      => include_str!("shaders/rt_shadow_tint_avg.wgsl"),
        "rt_shadow_tint_bindless.wgsl" => include_str!("shaders/rt_shadow_tint_bindless.wgsl"),
        "unlit.wgsl"                 => include_str!("shaders/unlit.wgsl"),
        "gizmo_line.wgsl"            => include_str!("shaders/gizmo_line.wgsl"),
        "depth_prepass.wgsl"         => include_str!("shaders/depth_prepass.wgsl"),
        "id_pass.wgsl"               => include_str!("shaders/id_pass.wgsl"),
        "outline.wgsl"               => include_str!("shaders/outline.wgsl"),
        "sprite.wgsl"                => include_str!("shaders/sprite.wgsl"),
        "sprite_outline.wgsl"        => include_str!("shaders/sprite_outline.wgsl"),
        "canvas_id.wgsl"             => include_str!("shaders/canvas_id.wgsl"),
        "camera_preview_blit.wgsl"   => include_str!("shaders/camera_preview_blit.wgsl"),
        // トーンマップ演算子（純関数）。カメラプレビューブリットが HDR プレビューを
        // トーンマップするために連結する（Phase R3）。
        "tonemap_ops.wgsl"           => include_str!("shaders/tonemap_ops.wgsl"),
        "bar_fill.wgsl"              => include_str!("shaders/bar_fill.wgsl"),
        "skybox.wgsl"                => include_str!("shaders/skybox.wgsl"),
        // 速度バッファ（モーションベクタ）。velocity_math.wgsl はバインディングを持たない
        // 純関数のみ（草も連結する）、velocity_common.wgsl は group4 の前フレーム行列を
        // 宣言する（G-Buffer のメッシュ／地形パイプラインのみ連結してよい）。
        "velocity_math.wgsl"         => include_str!("shaders/velocity_math.wgsl"),
        "velocity_common.wgsl"       => include_str!("shaders/velocity_common.wgsl"),
        // G-Buffer 専用の頂点シェーダ（前フレーム行列で prev_clip を埋める版）。
        // フォワードと共有の shader_static_vertex / shader_skinned_vertex とは別物。
        "gbuffer_static_vertex.wgsl"  => include_str!("shaders/gbuffer_static_vertex.wgsl"),
        "gbuffer_skinned_vertex.wgsl" => include_str!("shaders/gbuffer_skinned_vertex.wgsl"),
        // 速度バッファのデバッグ可視化（SEED_DEBUG_VELOCITY=1）。
        "velocity_debug.wgsl"        => include_str!("shaders/velocity_debug.wgsl"),
        // G-Buffer 各チャンネルのデバッグ可視化（シーンビュー表示モード「G-Buffer: 〜」）。
        "gbuffer_debug.wgsl"         => include_str!("shaders/gbuffer_debug.wgsl"),
        // G-Buffer 書き込み（Phase D3: Deferred 化 Phase A）。
        "gbuffer_write.wgsl"         => include_str!("shaders/gbuffer_write.wgsl"),
        // 地形レイヤブレンド G-Buffer 書き込み（Terrain T2・terrain_gbuffer.rs が連結する）。
        "terrain_gbuffer_write.wgsl" => include_str!("shaders/terrain_gbuffer_write.wgsl"),
        // プロシージャル草の G-Buffer 書き込み（grass_gbuffer.rs が単体で使う）。
        // 自己完結シェーダのため他ソースと連結しない（CameraUniform を自前宣言している）。
        "grass_gbuffer.wgsl"         => include_str!("shaders/grass_gbuffer.wgsl"),
        // デファードのフルスクリーン・ライティング復元（Phase D3: Deferred 化 Phase A）。
        "deferred_lighting.wgsl"     => include_str!("shaders/deferred_lighting.wgsl"),
        // 反射（SSR / RT）フルスクリーンパス＋合成（Phase D6）。
        // 反射のミス経路が使う「天球テクスチャ直接サンプル」の共有定義
        //（ReflectionSkyUniform / sky_refl_sample。バインディングを持たない純定義）。
        // 不透明反射 D6（reflection_*）と水面反射 W5.2（water_reflection_*）の**両方**が連結し、
        // 利用側の共通ファイルより**前**に置くこと（WGSL は宣言前の識別子を参照できない）。
        "sky_reflection_common.wgsl" => include_str!("shaders/sky_reflection_common.wgsl"),
        "reflection_common.wgsl"     => include_str!("shaders/reflection_common.wgsl"),
        "reflection_ssr.wgsl"        => include_str!("shaders/reflection_ssr.wgsl"),
        "reflection_rt.wgsl"         => include_str!("shaders/reflection_rt.wgsl"),
        "reflection_rt_hit_on.wgsl"  => include_str!("shaders/reflection_rt_hit_on.wgsl"),
        "reflection_rt_hit_off.wgsl" => include_str!("shaders/reflection_rt_hit_off.wgsl"),
        "bindless_common.wgsl"       => include_str!("shaders/bindless_common.wgsl"),
        "reflection_composite.wgsl"  => include_str!("shaders/reflection_composite.wgsl"),
        // AO（SSAO / RT-AO）フルスクリーンパス＋いもす法ブラー（Phase D4）。
        "ao_common.wgsl"             => include_str!("shaders/ao_common.wgsl"),
        "ao_ssao.wgsl"               => include_str!("shaders/ao_ssao.wgsl"),
        "ssgi_common.wgsl"           => include_str!("shaders/ssgi_common.wgsl"),
        "ssgi_gen.wgsl"              => include_str!("shaders/ssgi_gen.wgsl"),
        "ao_rt.wgsl"                 => include_str!("shaders/ao_rt.wgsl"),
        // 水中コースティクス生成パス（Phase W5.3）。連結相手の
        // `water_height_field.wgsl` は water モジュールが正典なので、
        // caustics.rs 側のリゾルバがそちらへ委譲する（二重管理を作らない）。
        "caustics.wgsl"              => include_str!("shaders/caustics.wgsl"),
        // 水面反射パス（Phase W5.2）。RT 変種と SSR 変種で共通部を共有する。
        // こちらも連結相手の `water_height_field.wgsl` は water モジュールが正典で、
        // water_reflection.rs 側のリゾルバがそちらへ委譲する。
        "water_reflection_common.wgsl" => include_str!("shaders/water_reflection_common.wgsl"),
        "water_reflection_rt.wgsl"     => include_str!("shaders/water_reflection_rt.wgsl"),
        "water_reflection_ssr.wgsl"    => include_str!("shaders/water_reflection_ssr.wgsl"),
        // 画面外ヒットのアルベド解決 2 変種（バインドレス可否で連結を差し替える）。
        // on 側は binding_array を宣言するため、非対応 GPU では連結してはならない。
        "water_reflection_hit_on.wgsl"  => include_str!("shaders/water_reflection_hit_on.wgsl"),
        "water_reflection_hit_off.wgsl" => include_str!("shaders/water_reflection_hit_off.wgsl"),
        // RT ソフト影マスク生成（Phase RT-Shadow-Denoise）。
        "shadow_mask.wgsl"           => include_str!("shaders/shadow_mask.wgsl"),
        other => panic!("unknown shader source: {other}"),
    }
}

// ============================================================
//  MeshPipeline — PBR スタティックメッシュ
// ============================================================

/// PBR スタティックメッシュ描画パイプライン一式。
///
/// カリング面（Back/Front/None）3 バリアント＋ワイヤーフレームバリアントと、
/// group0〜4（camera/model/material/gap/lights+shadow）の BindGroupLayout を保持する。
pub struct MeshPipeline {
    /// カリング面 3 種（Back / Front / None）のパイプライン。添字 = `CullFace::index()`。
    pub pipelines:    CullPipelineSet,
    /// ワイヤーフレーム（PolygonMode::Line）バリアント。エディタのシーンビューの
    /// 「ワイヤーフレーム」モード専用。cull_mode=None の単一パイプライン（表裏とも線描画）。
    /// POLYGON_MODE_LINE 非対応 GPU では None（描画側は塗り＝Unlit へフォールバック）。
    /// 通常パイプラインと同一 TOML・同一 group4 レイアウトのため lights BG をそのまま使える。
    pub wireframe:    Option<wgpu::RenderPipeline>,
    pub camera_bgl:   wgpu::BindGroupLayout,
    pub model_bgl:    wgpu::BindGroupLayout,
    pub material_bgl: wgpu::BindGroupLayout,
    /// group 4: ライト＋シャドウ複合（lights storage + メタ + CSM/スポット深度配列 +
    /// 比較サンプラー + シャドウ行列 UBO）。LightBuffer の複合 bind group 生成に使う。
    /// max_bind_groups=5（group 0〜4）のデバイスがあるため group 5 は新設しない（Phase R2）。
    pub lights_bgl:   wgpu::BindGroupLayout,
    /// group 3 用の空 BindGroup。
    ///
    /// mesh パイプラインは fragment が group 4（ライト）を参照する都合で
    /// レイアウトが 5 グループになり、group 3 は「空の gap BGL」になる。
    /// wgpu はレイアウトに存在する全 group 番号への BindGroup 設定を要求する
    /// （空レイアウトでも未設定のまま draw すると検証エラー）ため、
    /// 非スキン描画では必ずこの空 BG を slot 3 へセットする。
    /// 生成は起動時 1 回のみで毎フレーム使い回す。
    pub empty_bg3:    wgpu::BindGroup,
}

impl MeshPipeline {
    fn new(device: &wgpu::Device, sf: wgpu::TextureFormat, df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        // カリング面 3 種のバリアントを 1 つの TOML から生成する（マテリアル単位のカリング面）。
        let (pipelines, bgls) = build_cull_variants(
            device, include_str!("pipelines/mesh.toml"), sf, df, cache, "mesh_pbr", get_shader_source);
        // group 番号順 (0, 1, 2, 3=gap, 4=lights+shadow) にイテレートして取り出す。
        // fragment の group 4 参照によりレイアウトは 5 グループになり、
        // group 3 はスキンなしメッシュでは空の gap BGL になる。
        // ※ シャドウ資源は group 4 の binding 2〜5 に同居（group 5 は新設しない）。
        let mut it = bgls.into_iter();
        let camera_bgl   = it.next().unwrap(); // group 0
        let model_bgl    = it.next().unwrap(); // group 1
        let material_bgl = it.next().unwrap(); // group 2
        let gap_bgl      = it.next().unwrap(); // group 3（空レイアウト）
        let lights_bgl   = it.next().unwrap(); // group 4（ライト＋シャドウ複合）

        // group 3 の空 BindGroup を 1 個だけ生成して保持する（draw 時の必須セット用）。
        let empty_bg3 = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Mesh Empty BG (group 3)"),
            layout:  &gap_bgl,
            entries: &[],
        });

        // ワイヤーフレーム（PolygonMode::Line）バリアントを対応 GPU でのみ生成する。
        // 同一 TOML（mesh.toml）から polygon_mode だけ Line へ差し替え、cull_mode=None で
        // 表裏とも線を描く。非対応 GPU では生成せず、描画側が塗り（Unlit）へフォールバックする。
        let wireframe = build_wireframe_variant(
            device, include_str!("pipelines/mesh.toml"), sf, df, cache, "mesh_pbr_wireframe");

        Self { pipelines, wireframe, camera_bgl, model_bgl, material_bgl, lights_bgl, empty_bg3 }
    }
}

// ============================================================
//  SkinnedMeshPipeline — PBR スキンメッシュ
// ============================================================

/// PBR スキンメッシュ描画パイプライン一式。
///
/// `MeshPipeline` のスキン版。カリング面 3 バリアント＋ワイヤーフレームバリアントに加え、
/// スキン頂点レイアウトと group3=joints の BindGroupLayout を保持する。
pub struct SkinnedMeshPipeline {
    /// カリング面 3 種（Back / Front / None）のパイプライン。添字 = `CullFace::index()`。
    pub pipelines:    CullPipelineSet,
    /// ワイヤーフレーム（PolygonMode::Line）バリアント。MeshPipeline と同じ扱い（cull=None・
    /// 非対応 GPU では None）。スキン頂点レイアウト・group3=joints は通常版と同一。
    pub wireframe:    Option<wgpu::RenderPipeline>,
    pub camera_bgl:   wgpu::BindGroupLayout,
    pub model_bgl:    wgpu::BindGroupLayout,
    pub material_bgl: wgpu::BindGroupLayout,
    pub joint_bgl:    wgpu::BindGroupLayout,
}

impl SkinnedMeshPipeline {
    fn new(device: &wgpu::Device, sf: wgpu::TextureFormat, df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        // カリング面 3 種のバリアントを 1 つの TOML から生成する。
        let (pipelines, bgls) = build_cull_variants(
            device, include_str!("pipelines/skinned_mesh.toml"), sf, df, cache,
            "skinned_mesh_pbr", get_shader_source);
        // group 番号順 (0, 1, 2, 3=joints, 4=lights+shadow) にイテレートして取り出す
        let mut it = bgls.into_iter();
        let camera_bgl   = it.next().unwrap(); // group 0
        let model_bgl    = it.next().unwrap(); // group 1
        let material_bgl = it.next().unwrap(); // group 2
        let joint_bgl    = it.next().unwrap(); // group 3
        let _lights_bgl  = it.next().unwrap(); // group 4（LightBuffer は mesh 側 BGL で生成し共用）

        // ワイヤーフレーム（PolygonMode::Line）バリアント（対応 GPU のみ）。
        let wireframe = build_wireframe_variant(
            device, include_str!("pipelines/skinned_mesh.toml"), sf, df, cache,
            "skinned_mesh_pbr_wireframe");

        Self { pipelines, wireframe, camera_bgl, model_bgl, material_bgl, joint_bgl }
    }
}

// ============================================================
//  RtMeshPipelines — RT 影バリアント（Phase R8, RT 対応 GPU のみ）
// ============================================================

/// mesh / skinned_mesh の RT 影バリアント一式。
///
/// 通常パイプラインとの違いは group 4 に TLAS（binding 6, acceleration_structure）が
/// 加わる点のみ。RT 対応 GPU でのみ生成され、DrawPipelines.rt に Option で保持される。
/// フラグメントは LightMeta.rt_shadows で RT/シャドウマップを実行時分岐するため、
/// このパイプラインは「RT 対応時は常に」使い、設定によるパイプライン差し替えは不要。
pub struct RtMeshPipelines {
    /// スタティックメッシュ RT パイプライン（カリング面 3 種。添字 = `CullFace::index()`）。
    pub mesh:       CullPipelineSet,
    /// スキンメッシュ RT パイプライン（カリング面 3 種。添字 = `CullFace::index()`）。
    pub skinned:    CullPipelineSet,
    /// group 4 レイアウト（ライト＋シャドウ＋TLAS 複合）。RT 用複合 BindGroup の生成に使う。
    pub lights_bgl: wgpu::BindGroupLayout,
    /// mesh RT パイプラインの group 3（空 gap）用 BindGroup（非スキン描画で必須セット）。
    pub empty_bg3:  wgpu::BindGroup,
}

impl RtMeshPipelines {
    /// RT 影バリアントを生成する（RT 対応時のみ呼ぶこと）。
    ///
    /// group 4 に acceleration_structure を含む WGSL をリフレクションするため、
    /// EXPERIMENTAL_RAY_QUERY を有効化したデバイスでのみ成功する。
    pub fn new(device: &wgpu::Device, sf: wgpu::TextureFormat, df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        // mesh_rt: group 番号順 (0=camera, 1=model, 2=material, 3=gap, 4=lights+shadow+tlas)。
        // 非 RT 版と同様、カリング面 3 種のバリアントを生成する。
        let (mesh, bgls_m) = build_cull_variants(
            device, include_str!("pipelines/mesh_rt.toml"), sf, df, cache, "mesh_pbr_rt", get_shader_source);
        // group 番号順に所有権で取り出す。group 3 = 空 gap、group 4 = ライト＋シャドウ＋TLAS。
        let mut it_m = bgls_m.into_iter();
        let _camera_bgl   = it_m.next().unwrap(); // group 0
        let _model_bgl    = it_m.next().unwrap(); // group 1
        let _material_bgl = it_m.next().unwrap(); // group 2
        let gap_bgl       = it_m.next().unwrap(); // group 3（空レイアウト）
        let lights_bgl    = it_m.next().unwrap(); // group 4（ライト＋シャドウ＋TLAS）
        let empty_bg3 = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Mesh RT Empty BG (group 3)"),
            layout:  &gap_bgl,
            entries: &[],
        });

        // skinned_mesh_rt: レイアウトは (0,1,2,3=joints,4=lights+shadow+tlas)。パイプラインのみ使う。
        let (skinned, _bgls_s) = build_cull_variants(
            device, include_str!("pipelines/skinned_mesh_rt.toml"), sf, df, cache,
            "skinned_mesh_pbr_rt", get_shader_source);

        Self { mesh, skinned, lights_bgl, empty_bg3 }
    }
}

// ============================================================
//  UnlitPipeline — デバッグ描画（LineList）
// ============================================================

/// デバッグ描画（ライン・ギズモ）用のライトなしパイプライン一式。
///
/// 1px ライン（LineList）、ギズモ用の太線（TriangleList クアッド展開）、選択強調の太線、
/// ソリッドギズモ三角形の 4 パイプラインを保持する。
pub struct UnlitPipeline {
    pub pipeline:           wgpu::RenderPipeline,  // LineList, ColorVertex
    pub gizmo_line_pipeline: wgpu::RenderPipeline, // TriangleList, GizmoVertex (太線, depth=Always)
    /// 選択強調用の太線パイプライン。gizmo_line と同じ GizmoVertex quad 展開シェーダを使うが、
    /// 深度は 1px ライン（unlit）と同じ LessEqual にして遮蔽の見え方を揃える
    /// （ギズモの depth=Always とは異なり、可視物の背後では隠れる）。
    pub thick_line_pipeline: wgpu::RenderPipeline, // TriangleList, GizmoVertex (太線, depth=LessEqual)
    pub gizmo_tri_pipeline:  wgpu::RenderPipeline, // TriangleList, ColorVertex (ソリッド)
    pub camera_bgl:          wgpu::BindGroupLayout,
    pub model_bgl:           wgpu::BindGroupLayout,
}

impl UnlitPipeline {
    fn new(device: &wgpu::Device, sf: wgpu::TextureFormat, df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        let build = |toml: &str| {
            RenderPipelineBuilder::new(device, toml, sf, df).with_cache(cache).build(get_shader_source)
        };

        let (pipeline, bgls) = build(include_str!("pipelines/unlit.toml"));
        // group 番号順 (0, 1) にイテレートして取り出す
        let mut it = bgls.into_iter();
        let camera_bgl = it.next().unwrap(); // group 0
        let model_bgl  = it.next().unwrap(); // group 1

        let (gizmo_line_pipeline, _) = build(include_str!("pipelines/gizmo_line.toml"));
        let (thick_line_pipeline, _) = build(include_str!("pipelines/unlit_thick_line.toml"));
        let (gizmo_tri_pipeline, _)  = build(include_str!("pipelines/gizmo_tri.toml"));

        Self { pipeline, gizmo_line_pipeline, thick_line_pipeline, gizmo_tri_pipeline, camera_bgl, model_bgl }
    }
}

// ============================================================
//  DepthPrepassPipelines — 深度プリパス専用パイプライン
// ============================================================

/// 深度のみを書き込む軽量パイプライン（カラー出力なし）。
///
/// MeshPipeline / SkinnedMeshPipeline と同じ BGL レイアウトを独立して生成するが、
/// 同一レイアウトのため既存 BindGroup とそのまま互換。
pub struct DepthPrepassPipelines {
    pub mesh:    wgpu::RenderPipeline,
    pub skinned: wgpu::RenderPipeline,
}

impl DepthPrepassPipelines {
    pub fn new(device: &wgpu::Device, df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        // color_format = "none" なので surface_format は使用されない
        let sf_unused = wgpu::TextureFormat::Bgra8UnormSrgb;

        let (mesh, _) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/depth_prepass_mesh.toml"), sf_unused, df)
                .with_cache(cache)
                .build(get_shader_source);
        let (skinned, _) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/depth_prepass_skinned.toml"), sf_unused, df)
                .with_cache(cache)
                .build(get_shader_source);

        Self { mesh, skinned }
    }
}

// ============================================================
//  ShadowDepthPipelines — シャドウマップ深度専用パイプライン（Phase R2）
// ============================================================

/// シャドウマップへ深度のみ書き込むパイプライン（カラー出力なし）。
///
/// depth_prepass.wgsl を流用し、深度フォーマットは SHADOW_DEPTH_FORMAT。
/// group 0 = シャドウカメラ（light view-proj）, group 1 = モデル行列。
/// skinned は group 3 = joints、group 2 は空 gap（`empty_bg2` を必ずセットする）。
pub struct ShadowDepthPipelines {
    pub mesh:      wgpu::RenderPipeline,
    pub skinned:   wgpu::RenderPipeline,
    /// skinned パイプラインの group 2（空 gap）用 BindGroup。描画時に必須セット。
    pub empty_bg2: wgpu::BindGroup,
}

impl ShadowDepthPipelines {
    pub fn new(device: &wgpu::Device, shadow_df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        // color_format = "none" なので surface_format は使用されない
        let sf_unused = wgpu::TextureFormat::Bgra8UnormSrgb;

        let (mesh, _) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/shadow_depth_mesh.toml"), sf_unused, shadow_df)
                .with_label("shadow_depth_mesh")
                .with_cache(cache)
                .build(get_shader_source);

        let (skinned, bgls_s) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/shadow_depth_skinned.toml"), sf_unused, shadow_df)
                .with_label("shadow_depth_skinned")
                .with_cache(cache)
                .build(get_shader_source);
        // skinned のレイアウトは (0=camera, 1=instances, 2=gap, 3=joints)。
        // group 2 の空 BindGroup を 1 個だけ生成して描画時に使い回す。
        let gap2_bgl = &bgls_s[2];
        let empty_bg2 = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Shadow Depth Empty BG (group 2)"),
            layout:  gap2_bgl,
            entries: &[],
        });

        Self { mesh, skinned, empty_bg2 }
    }
}

// ============================================================
//  MeshletCullPipeline — GPU メッシュレットカリング（第1弾）
// ============================================================

/// GPU メッシュレットカリング コンピュートパイプライン。
///
/// Group 0 BGL（`shaders/meshlet_cull.wgsl` と一致）:
///   0 = instances   (array<InstanceData>,        Storage RO)
///   1 = meshlets     (array<Meshlet>,             Storage RO)
///   2 = draw_cmds    (array<DrawIndexedIndirect>, Storage RW)
///   3 = draw_count   (atomic<u32>,                Storage RW)
///   4 = params       (CullParams,                 Uniform)
pub struct MeshletCullPipeline {
    pub pipeline: wgpu::ComputePipeline,
    pub bgl:      wgpu::BindGroupLayout,
}

impl MeshletCullPipeline {
    fn new(device: &wgpu::Device, cache: Option<&wgpu::PipelineCache>) -> Self {
        let src    = include_str!("shaders/meshlet_cull.wgsl");
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Meshlet Cull Shader"),
            source: wgpu::ShaderSource::Wgsl(src.into()),
        });

        let make_storage = |binding: u32, read_only: bool| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty:                 wgpu::BufferBindingType::Storage { read_only },
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        };
        let make_uniform = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty:                 wgpu::BufferBindingType::Uniform,
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        };

        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Meshlet Cull BGL"),
            entries: &[
                make_storage(0, true),  // instances
                make_storage(1, true),  // meshlets
                make_storage(2, false), // draw_cmds
                make_storage(3, false), // draw_count (atomic)
                make_uniform(4),        // params
            ],
        });

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Meshlet Cull Pipeline Layout"),
            bind_group_layouts:   &[&bgl],
            push_constant_ranges: &[],
        });
        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label:               Some("Meshlet Cull Compute Pipeline"),
            layout:              Some(&layout),
            module:              &shader,
            entry_point:         Some("cs_main"),
            compilation_options: Default::default(),
            cache,
        });

        Self { pipeline, bgl }
    }
}

// ============================================================
//  SkinComputePipeline — GPU スキニング コンピュートパイプライン
// ============================================================

/// GPU スキニング（頂点ボーン変形）用のコンピュートパイプライン。
///
/// per_frame（joints storage + カウント uniform）・static（頂点属性群の read-only storage）・
/// output（変形後頂点の read-write storage）の 3 つの BindGroupLayout を保持する。
pub struct SkinComputePipeline {
    pub pipeline:      wgpu::ComputePipeline,
    pub per_frame_bgl: wgpu::BindGroupLayout,
    pub static_bgl:    wgpu::BindGroupLayout,
    pub output_bgl:    wgpu::BindGroupLayout,
}

impl SkinComputePipeline {
    fn new(device: &wgpu::Device, cache: Option<&wgpu::PipelineCache>) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Skin Compute Shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("shaders/skin_compute.wgsl").into()),
        });

        let ro_storage = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty:                 wgpu::BufferBindingType::Storage { read_only: true },
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        };
        let rw_storage = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty:                 wgpu::BufferBindingType::Storage { read_only: false },
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        };
        let uniform_entry = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty:                 wgpu::BufferBindingType::Uniform,
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        };

        let per_frame_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Skin PerFrame BGL"),
            entries: &[ro_storage(0), uniform_entry(1)],
        });
        let static_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Skin Static BGL"),
            entries: &[
                ro_storage(0), ro_storage(1), ro_storage(2), ro_storage(3),
                ro_storage(4), ro_storage(5), ro_storage(6), ro_storage(7),
                ro_storage(8), ro_storage(9), ro_storage(10), ro_storage(11),
            ],
        });
        let output_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Skin Output BGL"),
            entries: &[rw_storage(0)],
        });

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Skin Compute Layout"),
            bind_group_layouts:   &[&per_frame_bgl, &static_bgl, &output_bgl],
            push_constant_ranges: &[],
        });
        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label:               Some("Skin Compute Pipeline"),
            layout:              Some(&layout),
            module:              &shader,
            entry_point:         Some("cs_main"),
            compilation_options: Default::default(),
            cache,
        });

        Self { pipeline, per_frame_bgl, static_bgl, output_bgl }
    }
}

// ============================================================
//  SkinDeformPipeline — RT 用「変形後ローカル頂点位置」書き出し compute
// ============================================================

/// スキンメッシュの変形後ローカル頂点位置を storage へ書き出す compute パイプライン。
///
/// 【役割】`SkinComputePipeline` はジョイント行列しか出力せず、頂点変形は頂点シェーダが
/// 描画時に行う。レイトレーシングの BLAS は実体としての頂点バッファを要求するため、
/// このパイプラインが VS と同じ式で変形後位置（ローカル空間）を書き出す。
/// 出力バッファはそのまま BLAS の頂点入力になる（`rt_skin_blas.rs`）。
///
/// Group 0（`entry_bgl`）— エントリ（プリミティブ×インスタンス）ごとに 1 個作る:
///   0 = params        (SkinDeformParams, uniform)
///   1 = src_vertices  (array<u32>,       storage read)   … `Vertex` 生バイト列
///   2 = src_skin      (array<u32>,       storage read)   … `SkinVertex` 生バイト列
///   3 = out_positions (array<vec4<f32>>, storage rw)     … 変形後ローカル位置
///
/// Group 1（`joint_bgl`）— LOD ごとに 1 個作る:
///   0 = joint_matrices (array<mat4x4<f32>>, storage read) … `sk_jmats_lodN`
///
/// 【なぜ joint_bgl を新設するのか】頂点シェーダ用の既存 joint BGL は
/// `visibility = VERTEX` で作られており、そのまま compute パイプラインへ渡すと
/// 「compute 段から見えない binding」でパイプライン生成が失敗する。既存 BGL の
/// visibility を書き換えると描画側へ副作用が出るため、compute 専用を別に持つ
/// （バッファ実体は同一の `sk_jmats_lodN` を共有するのでメモリ増加はゼロ）。
///
/// `MeshletCullPipeline` / `SkinComputePipeline` と同じ手動 BGL 構築の流儀。
pub struct SkinDeformPipeline {
    pub pipeline:  wgpu::ComputePipeline,
    /// group 0 レイアウト（エントリごとの BindGroup 生成に使う）。
    pub entry_bgl: wgpu::BindGroupLayout,
    /// group 1 レイアウト（LOD ごとのジョイント行列 BindGroup 生成に使う。COMPUTE 可視）。
    pub joint_bgl: wgpu::BindGroupLayout,
}

impl SkinDeformPipeline {
    /// パイプラインを生成する。`DrawPipelines` の構築時に 1 回だけ呼ばれる。
    /// （実 GPU テストからも直接生成できるよう pub）
    pub fn new(device: &wgpu::Device, cache: Option<&wgpu::PipelineCache>) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Skin Deform Shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("shaders/skin_deform.wgsl").into()),
        });

        // ── BGL エントリのヘルパー（SkinComputePipeline と同じ流儀）──────
        let ro_storage = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty:                 wgpu::BufferBindingType::Storage { read_only: true },
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        };
        let rw_storage = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty:                 wgpu::BufferBindingType::Storage { read_only: false },
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        };
        let uniform_entry = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty:                 wgpu::BufferBindingType::Uniform,
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        };

        let entry_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Skin Deform Entry BGL"),
            entries: &[uniform_entry(0), ro_storage(1), ro_storage(2), rw_storage(3)],
        });
        let joint_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Skin Deform Joint BGL (compute visible)"),
            entries: &[ro_storage(0)],
        });

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Skin Deform Layout"),
            bind_group_layouts:   &[&entry_bgl, &joint_bgl],
            push_constant_ranges: &[],
        });
        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label:               Some("Skin Deform Pipeline"),
            layout:              Some(&layout),
            module:              &shader,
            entry_point:         Some("cs_main"),
            compilation_options: Default::default(),
            cache,
        });

        Self { pipeline, entry_bgl, joint_bgl }
    }
}

// ============================================================
//  IdPassPipeline — Actor ID 書き込みパス
// ============================================================

/// Actor ID（ピッキング用オフスクリーンバッファ）書き込みパイプライン一式。
///
/// スタティック／スキンメッシュそれぞれのカリング面 3 バリアントと、
/// group0〜4（camera/model/id_data/joint/id_base）の BindGroupLayout を保持する。
pub struct IdPassPipeline {
    /// スタティックメッシュ用 ID 書き込みパイプライン（カリング面 3 種。添字 = `CullFace::index()`）。
    ///
    /// メインパス（`MeshPipeline::pipelines`）と同様にマテリアル単位のカリング面へ追従させる。
    /// これをしないと両面マテリアル（glTF double_sided → CullFace::None）を裏面から
    /// クリックしたとき、ID パスだけが背面カリングで裏面を描かず ID バッファが空になり、
    /// ピッキングで選択できなくなる（メインパスは両面を描くため表示はされる）。
    pub mesh_pipelines:    CullPipelineSet,
    /// スキンメッシュ用 ID 書き込みパイプライン（カリング面 3 種。添字 = `CullFace::index()`）。
    pub skinned_pipelines: CullPipelineSet,
    pub camera_bgl:       wgpu::BindGroupLayout,
    pub model_bgl:        wgpu::BindGroupLayout,
    pub id_data_bgl:      wgpu::BindGroupLayout,
    pub joint_bgl:        wgpu::BindGroupLayout,
    pub id_base_bgl:      wgpu::BindGroupLayout,
}

impl IdPassPipeline {
    fn new(device: &wgpu::Device, _sf: wgpu::TextureFormat, df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        // ID パスはオフスクリーン Rgba32Float テクスチャへ描画するため、
        // サーフェスフォーマット (_sf) ではなく固定フォーマットを使う。
        // メインパス／G-Buffer と同じ流儀で、1 つの TOML から cull_mode だけを差し替えた
        // Back / Front / None の 3 バリアントを生成する（TOML 側の cull_mode 値は無視される）。
        let build = |toml: &str, label_base: &str| {
            build_cull_variants(
                device,
                toml,
                wgpu::TextureFormat::Rgba32Float,
                df,
                cache,
                label_base,
                get_shader_source,
            )
        };

        // mesh: num_bind_groups=5 → bgls[0..4] = [camera, model, id_data, joint, id_base]
        // build_cull_variants の返す BGL は Back バリアントのもの（3 本は同一レイアウト）。
        let (mesh_pipelines, mut bgls_m) = build(include_str!("pipelines/id_pass_mesh.toml"), "id_pass_mesh");
        let id_base_bgl  = bgls_m.pop().unwrap(); // group 4
        let _joint_bgl_m = bgls_m.pop().unwrap(); // group 3 (mesh では未使用だが layout は存在)
        let id_data_bgl  = bgls_m.pop().unwrap(); // group 2
        let model_bgl    = bgls_m.pop().unwrap(); // group 1
        let camera_bgl   = bgls_m.pop().unwrap(); // group 0

        // skinned: num_bind_groups=5 → 同様。group 3 の joint_bgl だけ取り出す
        let (skinned_pipelines, mut bgls_s) = build(include_str!("pipelines/id_pass_skinned.toml"), "id_pass_skinned");
        let _id_base_bgl_s = bgls_s.pop().unwrap(); // group 4 (mesh 側と同レイアウト)
        let joint_bgl      = bgls_s.pop().unwrap(); // group 3
        let _ = bgls_s;

        Self { mesh_pipelines, skinned_pipelines, camera_bgl, model_bgl, id_data_bgl, joint_bgl, id_base_bgl }
    }
}

// ID パスのカリング面バリアントがメインパスと同一の `CullPipelineSet`
// （= 添字 0..CULL_FACE_COUNT の 3 本）であることをコンパイル時に固定する回帰ガード。
// 誰かが ID パスを単一 `RenderPipeline` へ戻すと（＝両面ピッキング不具合の再発）
// この型注釈が型検査に失敗してビルドが通らなくなる。実行時コストはゼロ。
const _: fn() = || {
    fn _assert_id_pass_matches_main_pass_cull_variants(m: &MeshPipeline, id: &IdPassPipeline) {
        let _: &CullPipelineSet = &m.pipelines;
        let _: &CullPipelineSet = &id.mesh_pipelines;
        let _: &CullPipelineSet = &id.skinned_pipelines;
    }
};

// ============================================================
//  OutlinePipeline — バックフェース膨張法アウトライン
// ============================================================

/// バックフェース膨張法による選択アウトライン描画パイプライン一式。
///
/// メッシュ／スキンメッシュそれぞれに、ステンシル読み取り専用版（既に書かれたステンシル=1
/// の内側を避けて描く）とステンシル書き込み版（選択インスタンス前面に 1 を書く）の 2 本を持つ。
pub struct OutlinePipeline {
    pub mesh_pipeline:            wgpu::RenderPipeline,
    pub skinned_pipeline:         wgpu::RenderPipeline,
    pub mesh_stencil_pipeline:    wgpu::RenderPipeline,
    pub skinned_stencil_pipeline: wgpu::RenderPipeline,
}

impl OutlinePipeline {
    fn new(device: &wgpu::Device, sf: wgpu::TextureFormat, df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        // stencil Equal(0): アウトライン内側への描画を抑制
        let stencil_read_only = wgpu::StencilState {
            front: wgpu::StencilFaceState {
                compare:       wgpu::CompareFunction::Equal,
                fail_op:       wgpu::StencilOperation::Keep,
                depth_fail_op: wgpu::StencilOperation::Keep,
                pass_op:       wgpu::StencilOperation::Keep,
            },
            back: wgpu::StencilFaceState {
                compare:       wgpu::CompareFunction::Equal,
                fail_op:       wgpu::StencilOperation::Keep,
                depth_fail_op: wgpu::StencilOperation::Keep,
                pass_op:       wgpu::StencilOperation::Keep,
            },
            read_mask:  0xFF,
            write_mask: 0x00,
        };

        // stencil Replace: 選択インスタンス前面にステンシル=1 を書き込む
        let stencil_write = wgpu::StencilState {
            front: wgpu::StencilFaceState {
                compare:       wgpu::CompareFunction::Always,
                fail_op:       wgpu::StencilOperation::Keep,
                depth_fail_op: wgpu::StencilOperation::Keep,
                pass_op:       wgpu::StencilOperation::Replace,
            },
            back: wgpu::StencilFaceState {
                compare:       wgpu::CompareFunction::Always,
                fail_op:       wgpu::StencilOperation::Keep,
                depth_fail_op: wgpu::StencilOperation::Keep,
                pass_op:       wgpu::StencilOperation::Replace,
            },
            read_mask:  0xFF,
            write_mask: 0xFF,
        };

        let build = |toml: &str, stencil: wgpu::StencilState| {
            RenderPipelineBuilder::new(device, toml, sf, df)
                .with_stencil(stencil)
                .with_cache(cache)
                .build(get_shader_source)
                .0
        };

        let mesh_pipeline = build(
            include_str!("pipelines/outline_mesh.toml"),
            stencil_read_only.clone(),
        );
        let skinned_pipeline = build(
            include_str!("pipelines/outline_skinned.toml"),
            stencil_read_only,
        );
        let mesh_stencil_pipeline = build(
            include_str!("pipelines/outline_stencil_mesh.toml"),
            stencil_write.clone(),
        );
        let skinned_stencil_pipeline = build(
            include_str!("pipelines/outline_stencil_skinned.toml"),
            stencil_write,
        );

        Self { mesh_pipeline, skinned_pipeline, mesh_stencil_pipeline, skinned_stencil_pipeline }
    }
}

// ============================================================
//  SpritePipeline — 2D スプライトテクスチャ描画
// ============================================================

/// ワールド空間テクスチャクワッドパイプライン。
///
/// Group 0: CameraUniform（mesh パイプラインと同一レイアウト → camera_buf.bind_group と互換）
/// Group 1: テクスチャ + サンプラー
///
/// Phase R6 でモデル行列・カラーは per-instance 頂点属性へ移行したため、
/// 旧 Group 1（SpriteUniform）は撤去済み。テクスチャは Group 1 に繰り上がった。
pub struct SpritePipeline {
    pub pipeline:           wgpu::RenderPipeline,
    /// Group 1: テクスチャ＋サンプラー バインドグループレイアウト
    pub tex_bgl:            wgpu::BindGroupLayout,
    /// リニアフィルタリングサンプラー（テクスチャ BG 構築に使用）
    pub sampler:            wgpu::Sampler,
    /// テクスチャ未設定時のフォールバック（白 1×1）
    pub white_fallback_bg:  wgpu::BindGroup,
    /// ユニットクワッド頂点バッファ ([0,1]×[0,1], 2 三角形 = 6 頂点)
    pub unit_quad_vbuf:     wgpu::Buffer,
}

impl SpritePipeline {
    fn new(
        device: &wgpu::Device,
        queue:  &wgpu::Queue,
        sf:     wgpu::TextureFormat,
        df:     wgpu::TextureFormat,
        cache:  Option<&wgpu::PipelineCache>,
    ) -> Self {
        let (pipeline, bgls) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/sprite.toml"), sf, df)
                .with_cache(cache)
                .build(get_shader_source);
        // group 番号順 (0, 1) にイテレートして取り出す
        let mut it = bgls.into_iter();
        let _camera_bgl_compat = it.next(); // group 0: camera_buf.bind_group と互換のため不使用
        let tex_bgl            = it.next().unwrap(); // group 1: テクスチャ＋サンプラー

        // リニアフィルタリングサンプラー
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("Sprite Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Linear,
            min_filter:     wgpu::FilterMode::Linear,
            mipmap_filter:  wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        // 白 1×1 フォールバックテクスチャ（テクスチャ未設定時に使用）
        let white_tex = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("Sprite White Fallback Tex"),
            size:            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          wgpu::TextureFormat::Rgba8UnormSrgb,
            usage:           wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats:    &[],
        });
        queue.write_texture(
            white_tex.as_image_copy(),
            &[255u8, 255, 255, 255],
            wgpu::ImageDataLayout {
                offset:         0,
                bytes_per_row:  Some(4),
                rows_per_image: Some(1),
            },
            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
        );
        let white_view = white_tex.create_view(&Default::default());
        let white_fallback_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Sprite White Fallback BG"),
            layout:  &tex_bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding:  0,
                    resource: wgpu::BindingResource::TextureView(&white_view),
                },
                wgpu::BindGroupEntry {
                    binding:  1,
                    resource: wgpu::BindingResource::Sampler(&sampler),
                },
            ],
        });

        // ユニットクワッド: [0,1]×[0,1], 2 三角形（6頂点）
        // レイアウト: position(vec2) + uv(vec2) = 16 bytes / 頂点
        let quad_verts: &[f32] = &[
            0.0, 0.0,  0.0, 0.0,   // tri1 v0: 左上
            1.0, 0.0,  1.0, 0.0,   // tri1 v1: 右上
            1.0, 1.0,  1.0, 1.0,   // tri1 v2: 右下
            0.0, 0.0,  0.0, 0.0,   // tri2 v0: 左上
            1.0, 1.0,  1.0, 1.0,   // tri2 v1: 右下
            0.0, 1.0,  0.0, 1.0,   // tri2 v2: 左下
        ];
        use wgpu::util::DeviceExt;
        let unit_quad_vbuf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Sprite Unit Quad VBuf"),
            contents: bytemuck::cast_slice(quad_verts),
            usage:    wgpu::BufferUsages::VERTEX,
        });

        Self { pipeline, tex_bgl, sampler, white_fallback_bg, unit_quad_vbuf }
    }
}

// ============================================================
//  CanvasIdPipeline — 2D キャンバスアクター ID 書き込みパス
// ============================================================

/// キャンバスアクター ID パスで GPU に送るユニフォーム（80 bytes）。
///
/// model は GPU 列優先（WGSL mat4x4<f32> レイアウト）で格納する。
/// actor_id は「bitcast<f32>(u32)」として A チャンネルに書き込まれる。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct CanvasIdUniform {
    /// GPU 列優先モデル行列（64 bytes）
    pub model:    [[f32; 4]; 4],
    /// raw アクター ID（0 = 背景、1 以上 = アクター）
    pub actor_id: u32,
    /// 16 バイトアライメント用パディング（12 bytes）
    pub _pad:     [u32; 3],
}

/// キャンバスアクター ID パスパイプライン。
///
/// スプライトと同じユニットクワッド頂点レイアウトを使用し、
/// Rgba32Float テクスチャの A チャンネルにアクター ID を書き込む。
/// スプライトテクスチャのアルファが 0.1 未満のピクセルは discard するため、
/// 透明領域クリックではアクターが選択されない。
pub struct CanvasIdPipeline {
    pub pipeline:         wgpu::RenderPipeline,
    /// コライダーピック面用の深度対応バリアント（collider_pick.toml）。
    /// シェーダー・バインドグループレイアウトは `pipeline` と同一で、
    /// depth_compare = LessEqual / depth_write = true のみ異なる。
    /// メインパスのシーン深度に対してテストしつつ自身も深度を書くため、
    /// コライダー面同士・可視物との重なりが「カメラに近い方優先」で解決される。
    pub pick_depth_pipeline: wgpu::RenderPipeline,
    /// Group 1 BGL: CanvasIdUniform（モデル行列 + アクター ID）
    pub canvas_id_bgl:    wgpu::BindGroupLayout,
    /// Group 2 BGL: テクスチャ + サンプラー（アルファマスク用）
    pub tex_bgl:          wgpu::BindGroupLayout,
    /// スプライトなし時のフォールバック（白 1×1, alpha=1）
    pub white_view:       wgpu::TextureView,
    /// リニアフィルタリングサンプラー
    pub sampler:          wgpu::Sampler,
}

impl CanvasIdPipeline {
    fn new(device: &wgpu::Device, queue: &wgpu::Queue, df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        // color_format = "Rgba32Float" のため surface_format は使用しない
        let sf_unused = wgpu::TextureFormat::Bgra8UnormSrgb;
        let (pipeline, bgls) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/canvas_id.toml"), sf_unused, df)
                .with_cache(cache)
                .build(get_shader_source);
        // group 番号順 (0, 1, 2) にイテレートして取り出す
        let mut it = bgls.into_iter();
        let _camera_bgl_compat = it.next(); // group 0: camera_buf.bind_group と互換のため不使用
        let canvas_id_bgl      = it.next().unwrap(); // group 1
        let tex_bgl            = it.next().unwrap(); // group 2

        // コライダーピック面用の深度対応バリアント。
        // 同一シェーダー（canvas_id.wgsl）から生成されるため BGL は上記と等価であり、
        // canvas_id 側の BGL で作った BindGroup をそのまま使用できる
        // （group 0 の camera_buf 互換と同じく wgpu の BGL 等価性に依拠する既存慣例）。
        let (pick_depth_pipeline, _bgls_depth) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/collider_pick.toml"), sf_unused, df)
                .with_cache(cache)
                .build(get_shader_source);

        // 白 1×1 テクスチャ（alpha=1）を作成してフォールバックビューとする
        let white_tex = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("CanvasId White Fallback Tex"),
            size:            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          wgpu::TextureFormat::Rgba8UnormSrgb,
            usage:           wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats:    &[],
        });
        queue.write_texture(
            white_tex.as_image_copy(),
            &[255u8, 255, 255, 255],
            wgpu::ImageDataLayout { offset: 0, bytes_per_row: Some(4), rows_per_image: Some(1) },
            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
        );
        let white_view = white_tex.create_view(&Default::default());

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:      Some("CanvasId Sampler"),
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            ..Default::default()
        });

        Self { pipeline, pick_depth_pipeline, canvas_id_bgl, tex_bgl, white_view, sampler }
    }
}

// ============================================================
//  DrawPipelines — 全パイプラインをまとめた型
// ============================================================

// ============================================================
//  CameraPreviewBlitPipeline — カメラプレビューブリットパイプライン
// ============================================================

/// オフスクリーンのカメラプレビューテクスチャをスクリーン矩形に貼り付けるパイプライン。
///
/// Group 0: BlitRect ユニフォーム (NDC 矩形座標)
/// Group 1: プレビューテクスチャ + サンプラー
pub struct CameraPreviewBlitPipeline {
    /// ブリット描画パイプライン（頂点バッファなし、6 頂点の三角形 2 枚）
    pub pipeline: wgpu::RenderPipeline,
    /// Group 0 レイアウト（BlitRect ユニフォームバッファ）
    pub rect_bgl: wgpu::BindGroupLayout,
    /// Group 1 レイアウト（テクスチャ + サンプラー）
    pub tex_bgl:  wgpu::BindGroupLayout,
}

impl CameraPreviewBlitPipeline {
    fn new(
        device: &wgpu::Device,
        sf:     wgpu::TextureFormat,
        df:     wgpu::TextureFormat,
        cache:  Option<&wgpu::PipelineCache>,
    ) -> Self {
        let (pipeline, bgls) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/camera_preview_blit.toml"), sf, df)
                .with_cache(cache)
                .build(get_shader_source);
        // group 番号順 (0, 1) にイテレートして取り出す
        let mut it = bgls.into_iter();
        let rect_bgl = it.next().unwrap(); // group 0
        let tex_bgl  = it.next().unwrap(); // group 1
        Self { pipeline, rect_bgl, tex_bgl }
    }
}

// ============================================================
//  BarFillPipeline — 帯塗りつぶしパイプライン
// ============================================================

/// 帯塗りつぶし用 GPU ユニフォーム（32 bytes）。
///
/// NDC 座標系 (X: -1=左/+1=右、Y: -1=下/+1=上) で矩形を指定する。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct BarFillUniform {
    /// 塗りカラー（RGBA リニア）
    pub color: [f32; 4],
    /// NDC 左端
    pub x0:    f32,
    /// NDC 下端
    pub y0:    f32,
    /// NDC 右端
    pub x1:    f32,
    /// NDC 上端
    pub y1:    f32,
}

/// LetterBox / PillarBox の帯エリアを単色で塗りつぶすパイプライン。
///
/// 頂点バッファなし（@builtin(vertex_index) で 6 頂点を生成）。
/// depth_write = false / depth_compare = Always のため深度に干渉しない。
pub struct BarFillPipeline {
    /// 帯描画パイプライン
    pub pipeline:     wgpu::RenderPipeline,
    /// Group 0 レイアウト（BarFillUniform バッファ）
    pub uniform_bgl:  wgpu::BindGroupLayout,
}

impl BarFillPipeline {
    fn new(
        device: &wgpu::Device,
        sf:     wgpu::TextureFormat,
        df:     wgpu::TextureFormat,
        cache:  Option<&wgpu::PipelineCache>,
    ) -> Self {
        let (pipeline, bgls) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/bar_fill.toml"), sf, df)
                .with_cache(cache)
                .build(get_shader_source);
        let mut it = bgls.into_iter();
        let uniform_bgl = it.next().unwrap(); // group 0
        Self { pipeline, uniform_bgl }
    }

    /// 指定した NDC 矩形を単色で塗りつぶす。
    ///
    /// - `ndc_x0/y0`: 矩形の左端・下端（NDC）
    /// - `ndc_x1/y1`: 矩形の右端・上端（NDC）
    pub fn draw(
        &self,
        pass:    &mut wgpu::RenderPass<'_>,
        device:  &wgpu::Device,
        color:   [f32; 4],
        ndc_x0:  f32,
        ndc_y0:  f32,
        ndc_x1:  f32,
        ndc_y1:  f32,
    ) {
        use wgpu::util::DeviceExt;
        let uniform = BarFillUniform { color, x0: ndc_x0, y0: ndc_y0, x1: ndc_x1, y1: ndc_y1 };
        // フレームごとに小さなユニフォームバッファを生成して即時描画する
        let buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("BarFill Uniform Buf"),
            contents: bytemuck::bytes_of(&uniform),
            usage:    wgpu::BufferUsages::UNIFORM,
        });
        let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("BarFill BG"),
            layout:  &self.uniform_bgl,
            entries: &[wgpu::BindGroupEntry {
                binding:  0,
                resource: buf.as_entire_binding(),
            }],
        });
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, &bg, &[]);
        // 6 頂点（2 三角形）を 1 インスタンス描画
        pass.draw(0..6, 0..1);
    }
}

// ============================================================
//  SpriteOutlinePipeline — 選択スプライトのスクリーンスペース均一アウトライン
// ============================================================

/// スプライト選択時のオレンジアウトラインパイプライン。
///
/// sprite_outline.wgsl によりクリップ空間でコーナーを押し出し、
/// 3D モデルアウトラインと同一の OUTLINE_THICKNESS を実現する。
/// テクスチャ不要のためバインドグループは Group 0（Camera）+ Group 1（SpriteUniform）のみ。
pub struct SpriteOutlinePipeline {
    pub pipeline: wgpu::RenderPipeline,
}

impl SpriteOutlinePipeline {
    fn new(
        device: &wgpu::Device,
        sf:     wgpu::TextureFormat,
        df:     wgpu::TextureFormat,
        cache:  Option<&wgpu::PipelineCache>,
    ) -> Self {
        let (pipeline, _bgls) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/sprite_outline.toml"), sf, df)
                .with_cache(cache)
                .build(get_shader_source);
        Self { pipeline }
    }
}

// ============================================================
//  ParticleComputePipeline — GPU パーティクル シミュレーション compute
// ============================================================

/// パーティクルシミュレーションのコンピュートパイプライン（particle_sim.wgsl）。
///
/// Group 0 BGL:
///   0 = particles (array<Particle>,  storage read_write)
///   1 = params    (EmitterParams,    uniform)
///   2 = lut       (array<vec4<f32>>, storage read)  … カーブ LUT（連結）
/// MeshletCullPipeline / SkinComputePipeline と同じ手動 BGL 構築の流儀。
pub struct ParticleComputePipeline {
    pub pipeline: wgpu::ComputePipeline,
    /// group 0 レイアウト（エミッタごとの compute BindGroup 生成に使う）。
    pub bgl:      wgpu::BindGroupLayout,
}

impl ParticleComputePipeline {
    fn new(device: &wgpu::Device, cache: Option<&wgpu::PipelineCache>) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Particle Sim Shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("shaders/particle_sim.wgsl").into()),
        });

        // binding 0: particles（storage read_write, COMPUTE）
        // binding 1: params  （uniform,             COMPUTE）
        // binding 2: lut     （storage read,        COMPUTE）… カーブ LUT
        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Particle Compute BGL"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding:    0,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Buffer {
                        ty:                 wgpu::BufferBindingType::Storage { read_only: false },
                        has_dynamic_offset: false,
                        min_binding_size:   None,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding:    1,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Buffer {
                        ty:                 wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size:   None,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding:    2,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Buffer {
                        ty:                 wgpu::BufferBindingType::Storage { read_only: true },
                        has_dynamic_offset: false,
                        min_binding_size:   None,
                    },
                    count: None,
                },
            ],
        });

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Particle Compute Layout"),
            bind_group_layouts:   &[&bgl],
            push_constant_ranges: &[],
        });
        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label:               Some("Particle Compute Pipeline"),
            layout:              Some(&layout),
            module:              &shader,
            entry_point:         Some("cs_main"),
            compilation_options: Default::default(),
            cache,
        });

        Self { pipeline, bgl }
    }
}

// ============================================================
//  ClusterBuildPipeline — Clustered Lighting のクラスタ構築 compute（Phase C1）
// ============================================================

/// クラスタ（3D フロクセル）ごとの影響ライトリストを構築する compute パイプライン。
///
/// シェーダ: cluster_common.wgsl（共有定義）＋ cluster_build.wgsl（バインディング＋エントリ）。
/// shader_common.wgsl / light_common.wgsl は連結しない（group 2/4 のバインディングが混入し、
/// 手動で組む group 0 のレイアウトと食い違うため。GpuLight は cluster_build 側でミラーする）。
///
/// Group 0 BGL（cluster_build.wgsl の宣言と一致させること）:
///   0 = lights  (array<GpuLight>,   storage read)   … LightBuffer と同一バッファを共有
///   1 = params  (ClusterParams,     uniform)
///   2 = grid    (array<ClusterCell>,storage rw)
///   3 = indices (array<u32>,        storage rw)
///   4 = cursor  (atomic<u32>,       storage rw)
/// MeshletCullPipeline / ParticleComputePipeline と同じ手動 BGL 構築の流儀。
pub struct ClusterBuildPipeline {
    pub pipeline: wgpu::ComputePipeline,
    /// group 0 レイアウト（ClusterResources::attach_lights が BindGroup 生成に使う）。
    pub bgl:      wgpu::BindGroupLayout,
}

impl ClusterBuildPipeline {
    fn new(device: &wgpu::Device, cache: Option<&wgpu::PipelineCache>) -> Self {
        // 共有定義 → 実装の順で連結する（ClusterParams / ClusterCell を先に宣言する）。
        let src = format!(
            "{}\n{}",
            include_str!("shaders/cluster_common.wgsl"),
            include_str!("shaders/cluster_build.wgsl"),
        );
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Cluster Build Shader"),
            source: wgpu::ShaderSource::Wgsl(src.into()),
        });

        // storage/uniform のヘルパー（同じ記述の繰り返しを避ける）。
        let buf = |binding: u32, storage: bool, read_only: bool| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty: if storage {
                    wgpu::BufferBindingType::Storage { read_only }
                } else {
                    wgpu::BufferBindingType::Uniform
                },
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        };
        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Cluster Build BGL"),
            entries: &[
                buf(0, true,  true),   // lights  (storage read)
                buf(1, false, false),  // params  (uniform)
                buf(2, true,  false),  // grid    (storage rw)
                buf(3, true,  false),  // indices (storage rw)
                buf(4, true,  false),  // cursor  (storage rw / atomic)
            ],
        });

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Cluster Build Layout"),
            bind_group_layouts:   &[&bgl],
            push_constant_ranges: &[],
        });
        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label:               Some("Cluster Build Pipeline"),
            layout:              Some(&layout),
            module:              &shader,
            entry_point:         Some("cs_main"),
            compilation_options: Default::default(),
            cache,
        });

        Self { pipeline, bgl }
    }
}
// ============================================================
//  GiUpdatePipeline - DDGI probe-update compute (Phase RT-GI)
// ============================================================
//
// cluster_common + ddgi_common + ddgi_probe_update concatenation (rayQuery compute).
// Built only on RT-capable GPUs (EXPERIMENTAL_RAY_QUERY); DrawPipelines holds it as
// Option. The manual group-0 BGL MUST match GiResources::attach (resources.rs).
pub struct GiUpdatePipeline {
    pub pipeline: wgpu::ComputePipeline,
    pub bgl:      wgpu::BindGroupLayout,
}

impl GiUpdatePipeline {
    fn new(device: &wgpu::Device, cache: Option<&wgpu::PipelineCache>) -> Self {
        let src = format!(
            "{}
{}
{}",
            include_str!("shaders/cluster_common.wgsl"),
            include_str!("shaders/ddgi_common.wgsl"),
            include_str!("shaders/ddgi_probe_update.wgsl"),
        );
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("GI Probe Update Shader"),
            source: wgpu::ShaderSource::Wgsl(src.into()),
        });

        let vis = wgpu::ShaderStages::COMPUTE;
        let buf = |binding: u32, storage: bool, read_only: bool| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: vis,
            ty: wgpu::BindingType::Buffer {
                ty: if storage {
                    wgpu::BufferBindingType::Storage { read_only }
                } else {
                    wgpu::BufferBindingType::Uniform
                },
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        };
        let sampled_tex = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: vis,
            ty: wgpu::BindingType::Texture {
                sample_type:    wgpu::TextureSampleType::Float { filterable: true },
                view_dimension: wgpu::TextureViewDimension::D2,
                multisampled:   false,
            },
            count: None,
        };
        let storage_tex = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: vis,
            ty: wgpu::BindingType::StorageTexture {
                access:         wgpu::StorageTextureAccess::WriteOnly,
                format:         super::ddgi::GI_ATLAS_FORMAT,
                view_dimension: wgpu::TextureViewDimension::D2,
            },
            count: None,
        };

        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("GI Update BGL"),
            entries: &[
                buf(0, false, false),  // GiParams (uniform)
                buf(1, true,  true),   // lights (storage read)
                buf(2, false, false),  // LightMeta (uniform)
                wgpu::BindGroupLayoutEntry {
                    binding: 3,
                    visibility: vis,
                    ty: wgpu::BindingType::AccelerationStructure { vertex_return: false },
                    count: None,
                },
                buf(4, true, true),    // avg albedo (storage read)
                sampled_tex(5),        // hist irradiance
                sampled_tex(6),        // hist visibility
                wgpu::BindGroupLayoutEntry {
                    binding: 7,
                    visibility: vis,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
                storage_tex(8),        // out irradiance
                storage_tex(9),        // out visibility
            ],
        });

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("GI Update Layout"),
            bind_group_layouts:   &[&bgl],
            push_constant_ranges: &[],
        });
        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label:               Some("GI Update Pipeline"),
            layout:              Some(&layout),
            module:              &shader,
            entry_point:         Some("cs_main"),
            compilation_options: Default::default(),
            cache,
        });
        Self { pipeline, bgl }
    }
}

// ============================================================
//  ParticlePipelines — GPU パーティクル描画（形状メッシュ×インスタンス）
// ============================================================

/// パーティクル描画パイプライン一式（Additive / Alpha）と共有リソース。
///
/// group 0 = camera（既存 CameraBuffer.bind_group を流用。camera_bgl を共有）
/// group 1 = particles（storage read）+ params（uniform）+ lut（storage read）
/// group 2 = texture + sampler（未指定時は既定白 1x1＋プロシージャル円）
///
/// 深度: テスト ON（LessEqual）・書込 OFF（透明物と同じく既存深度で遮蔽のみ判定）。
/// 頂点バッファは形状メッシュ（particle_shapes.rs、位置 vec3 のみ）を 1 本バインドし、
/// draw_indexed のインスタンスで粒子を引く。フラグメントは premultiplied alpha を出力する。
/// パーティクルの合成モード数（None/Normal/Add/Sub/Mul/Screen。ParticleBlend::to_code と一致）。
pub const PARTICLE_BLEND_COUNT: usize = 6;

/// GPU パーティクル描画パイプライン一式（形状メッシュ×インスタンス）。
///
/// ブレンドモード（None/Normal/Add/Sub/Mul/Screen）× 形状（mesh/Pixel）ぶんの
/// パイプラインと、group1（particles/params/lut）・group2（texture）の BindGroupLayout、
/// テクスチャ未指定エミッタ用の既定白 BindGroup を保持する。
pub struct ParticlePipelines {
    /// メッシュ形状（TriangleList）描画パイプライン。索引 = ParticleBlend::to_code()。
    pub mesh:             [wgpu::RenderPipeline; PARTICLE_BLEND_COUNT],
    /// Pixel（PointList・1 頂点/インスタンス）描画パイプライン。索引 = ParticleBlend::to_code()。
    pub pixel:            [wgpu::RenderPipeline; PARTICLE_BLEND_COUNT],
    /// group 1 レイアウト（particles ro + params uniform）。エミッタごとの draw BG 生成に使う。
    pub particle_bgl:     wgpu::BindGroupLayout,
    /// group 2 レイアウト（texture_2d_array + sampler）。
    pub tex_bgl:          wgpu::BindGroupLayout,
    /// リニアサンプラー（テクスチャ BG・既定白 BG 共通）。
    pub sampler:          wgpu::Sampler,
    /// テクスチャ未指定エミッタ用の既定白 1x1×1 レイヤ配列 BindGroup。
    pub default_white_bg: wgpu::BindGroup,
}

/// ParticleBlend::to_code() の各コードに対応する wgpu ブレンドステートを返す。
///
/// フラグメントは premultiplied alpha（rgb*a, a）を出力する前提。
/// 0=None(不透明) / 1=Normal(over) / 2=Add / 3=Sub(reverse-subtract) /
/// 4=Mul(Dst×Src) / 5=Screen(One + OneMinusSrcColor)。
fn particle_blend_state(code: usize) -> wgpu::BlendState {
    use wgpu::{BlendComponent, BlendFactor, BlendOperation, BlendState};
    // 同一係数を color/alpha 両方へ使うブレンドを作るヘルパー。
    let both = |src: BlendFactor, dst: BlendFactor, op: BlendOperation| BlendState {
        color: BlendComponent { src_factor: src, dst_factor: dst, operation: op },
        alpha: BlendComponent { src_factor: src, dst_factor: dst, operation: op },
    };
    match code {
        // None: 不透明（置換 src=One / dst=Zero）。
        0 => both(BlendFactor::One, BlendFactor::Zero, BlendOperation::Add),
        // Normal: premultiplied over（src=One / dst=OneMinusSrcAlpha）。
        1 => both(BlendFactor::One, BlendFactor::OneMinusSrcAlpha, BlendOperation::Add),
        // Add: 加算（src=One / dst=One）。
        2 => both(BlendFactor::One, BlendFactor::One, BlendOperation::Add),
        // Sub: 減算（reverse-subtract = dst - src。src=One / dst=One）。
        3 => both(BlendFactor::One, BlendFactor::One, BlendOperation::ReverseSubtract),
        // Mul: 乗算（Dst×Src。src_factor=Dst / dst_factor=Zero）。
        4 => both(BlendFactor::Dst, BlendFactor::Zero, BlendOperation::Add),
        // Screen: One + OneMinusSrcColor（src=One / dst=OneMinusSrc）。
        _ => both(BlendFactor::One, BlendFactor::OneMinusSrc, BlendOperation::Add),
    }
}

impl ParticlePipelines {
    /// - `sf`         : シーン HDR フォーマット（HDR_FORMAT）。
    /// - `df`         : 深度フォーマット（DEPTH_FORMAT, Depth24PlusStencil8）。
    /// - `camera_bgl` : 既存メッシュの group 0 camera BGL（共有カメラ BG を流用するため同一 BGL を使う）。
    fn new(
        device:     &wgpu::Device,
        queue:      &wgpu::Queue,
        sf:         wgpu::TextureFormat,
        df:         wgpu::TextureFormat,
        camera_bgl: &wgpu::BindGroupLayout,
        cache:      Option<&wgpu::PipelineCache>,
    ) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Particle Draw Shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("shaders/particle_draw.wgsl").into()),
        });

        // group 1: particles（storage read, VERTEX）+ params（uniform, VERTEX|FRAGMENT）
        //          + lut（storage read, VERTEX。色／スケールカーブは頂点で評価する）。
        let particle_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Particle Draw BGL (group1)"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding:    0,
                    visibility: wgpu::ShaderStages::VERTEX,
                    ty: wgpu::BindingType::Buffer {
                        ty:                 wgpu::BufferBindingType::Storage { read_only: true },
                        has_dynamic_offset: false,
                        min_binding_size:   None,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding:    1,
                    visibility: wgpu::ShaderStages::VERTEX_FRAGMENT,
                    ty: wgpu::BindingType::Buffer {
                        ty:                 wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size:   None,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding:    2,
                    visibility: wgpu::ShaderStages::VERTEX,
                    ty: wgpu::BindingType::Buffer {
                        ty:                 wgpu::BufferBindingType::Storage { read_only: true },
                        has_dynamic_offset: false,
                        min_binding_size:   None,
                    },
                    count: None,
                },
            ],
        });

        // group 2: texture_2d_array + sampler（FRAGMENT）。粒子ごとにレイヤを選ぶ。
        let tex_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Particle Texture BGL (group2)"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding:    0,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type:    wgpu::TextureSampleType::Float { filterable: true },
                        view_dimension: wgpu::TextureViewDimension::D2Array,
                        multisampled:   false,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding:    1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty:         wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count:      None,
                },
            ],
        });

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Particle Draw Layout"),
            bind_group_layouts:   &[camera_bgl, &particle_bgl, &tex_bgl],
            push_constant_ranges: &[],
        });

        // 深度: LessEqual・書込 OFF（既存の不透明深度で遮蔽のみ。透明物と同じ方針）。
        let depth_stencil = wgpu::DepthStencilState {
            format:              df,
            depth_write_enabled: false,
            depth_compare:       parse_compare("LessEqual"),
            stencil:             wgpu::StencilState::default(),
            bias:                wgpu::DepthBiasState::default(),
        };

        // 形状メッシュの頂点バッファレイアウト（位置 vec3 のみ・particle_shapes::ShapeVertex）。
        // Pixel（PointList）は頂点バッファ不要（instance_index のみ）。
        let mesh_buffers = [super::particle_shapes::ShapeVertex::LAYOUT];

        // ブレンド × トポロジ（mesh=TriangleList / pixel=PointList）で 1 本ずつ構築する。
        // カリングなし（Plane の両面表示のため）。
        let build = |code: usize, is_pixel: bool| -> wgpu::RenderPipeline {
            let (entry, buffers, topology): (&str, &[wgpu::VertexBufferLayout], wgpu::PrimitiveTopology) =
                if is_pixel {
                    ("vs_pixel", &[], wgpu::PrimitiveTopology::PointList)
                } else {
                    ("vs_main", &mesh_buffers, wgpu::PrimitiveTopology::TriangleList)
                };
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label:  Some(if is_pixel { "particle_pixel" } else { "particle_mesh" }),
                layout: Some(&layout),
                vertex: wgpu::VertexState {
                    module:              &shader,
                    entry_point:         Some(entry),
                    buffers,
                    compilation_options: Default::default(),
                },
                fragment: Some(wgpu::FragmentState {
                    module:              &shader,
                    entry_point:         Some("fs_main"),
                    targets:             &[Some(wgpu::ColorTargetState {
                        format:     sf,
                        blend:      Some(particle_blend_state(code)),
                        write_mask: wgpu::ColorWrites::ALL,
                    })],
                    compilation_options: Default::default(),
                }),
                primitive: wgpu::PrimitiveState {
                    topology,
                    front_face: wgpu::FrontFace::Ccw,
                    cull_mode:  None,
                    ..Default::default()
                },
                depth_stencil: Some(depth_stencil.clone()),
                multisample:   wgpu::MultisampleState::default(),
                multiview:     None,
                cache,
            })
        };

        // ブレンド 6 種 × トポロジ 2 種を索引 = ParticleBlend::to_code() で構築する。
        let mesh:  [wgpu::RenderPipeline; PARTICLE_BLEND_COUNT] = std::array::from_fn(|i| build(i, false));
        let pixel: [wgpu::RenderPipeline; PARTICLE_BLEND_COUNT] = std::array::from_fn(|i| build(i, true));

        // リニアサンプラー（テクスチャ・既定白 共通）。
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("Particle Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Linear,
            min_filter:     wgpu::FilterMode::Linear,
            mipmap_filter:  wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        // 既定白 1x1 テクスチャ（テクスチャ未指定エミッタ用。フラグメントの use_texture=0 と併用）。
        let white_tex = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("Particle White 1x1"),
            size:            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          wgpu::TextureFormat::Rgba8UnormSrgb,
            usage:           wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats:    &[],
        });
        queue.write_texture(
            white_tex.as_image_copy(),
            &[255u8, 255, 255, 255],
            wgpu::ImageDataLayout { offset: 0, bytes_per_row: Some(4), rows_per_image: Some(1) },
            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
        );
        // texture_2d_array レイアウトに合わせ、1 レイヤの配列ビューとして作る。
        let white_view = white_tex.create_view(&wgpu::TextureViewDescriptor {
            label:            Some("Particle White Array View"),
            dimension:        Some(wgpu::TextureViewDimension::D2Array),
            array_layer_count: Some(1),
            ..Default::default()
        });
        let default_white_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Particle White BG"),
            layout:  &tex_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(&white_view) },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::Sampler(&sampler) },
            ],
        });

        Self { mesh, pixel, particle_bgl, tex_bgl, sampler, default_white_bg }
    }
}

/// Renderer が保持する全描画パイプラインを 1 か所に束ねた集約構造体。
///
/// メッシュ／スキンメッシュ／RT 影／深度プリパス／シャドウ／ID パス／アウトライン／
/// スプライト／パーティクル／スカイボックス／G-Buffer／デファード／反射／AO／SSGI／
/// シャドウマスク等、起動時に一度だけ構築される全パイプラインを保持する。
pub struct DrawPipelines {
    pub mesh:                 MeshPipeline,
    pub skinned_mesh:         SkinnedMeshPipeline,
    /// RT 影バリアント（Phase R8）。RT 対応 GPU でのみ Some。
    /// 非対応時は None で、mesh/skinned_mesh の従来パイプラインのみを使う。
    pub rt:                   Option<RtMeshPipelines>,
    pub unlit_line:           UnlitPipeline,
    /// GPU メッシュレットカリング compute パイプライン（第1弾）。
    /// オブジェクト単位の視錐台カリング（旧 CullPipeline）は撤去済み。可視範囲の間引きはこれのみが担う。
    pub meshlet_cull:         MeshletCullPipeline,
    pub skin_compute:         SkinComputePipeline,
    /// RT スキン BLAS 用「変形後ローカル頂点位置」書き出し compute（Phase RT-Skin）。
    /// RT 非対応 GPU でも構築コストは軽微（compute 1 本）なので常に持つ。
    pub skin_deform:          SkinDeformPipeline,
    pub depth_prepass:        DepthPrepassPipelines,
    pub shadow_depth:         ShadowDepthPipelines,
    pub id_pass:              IdPassPipeline,
    pub outline:              OutlinePipeline,
    pub sprite:               SpritePipeline,
    pub sprite_outline:       SpriteOutlinePipeline,
    pub canvas_id:            CanvasIdPipeline,
    pub camera_preview_blit:  CameraPreviewBlitPipeline,
    pub bar_fill:             BarFillPipeline,
    /// 透明描画パイプライン一式（距離ソート / WBOIT, Phase R5）。
    pub transparent:          super::transparency::TransparentPipelines,
    /// GPU パーティクル シミュレーション compute パイプライン（Phase RP）。
    pub particle_compute:     ParticleComputePipeline,
    /// Clustered Lighting のクラスタ構築 compute パイプライン（Phase C1）。
    pub cluster_build:        ClusterBuildPipeline,
    /// DDGI プローブ更新 compute パイプライン（Phase RT-GI, RT 対応 GPU のみ Some）。
    pub gi_update:            Option<GiUpdatePipeline>,
    /// GPU パーティクル描画パイプライン一式（Additive / Alpha, Phase RP）。
    pub particles:            ParticlePipelines,
    /// スカイボックス（天球）描画パイプライン一式（Phase R9）。
    pub skybox:               super::skybox::SkyboxPipelines,
    /// G-Buffer 書き込みパイプライン一式（Phase D3: Deferred 化 Phase A）。
    /// Phase A 時点ではフレームループへ未接続（Phase B で接続予定）。
    pub gbuffer:              super::gbuffer::GBufferPipelines,
    /// デファードのフルスクリーン・ライティング復元パイプライン一式（Phase D3 Phase A）。
    /// Phase A 時点ではフレームループへ未接続（Phase B で接続予定）。
    pub deferred:             super::deferred::DeferredLightingPipelines,
    /// 速度バッファ（モーションベクタ）のデバッグ可視化パイプライン。
    /// 環境変数 `SEED_DEBUG_VELOCITY=1` のときだけ Some（既定は None＝GPU 資源も描画も 0 コスト）。
    pub velocity_debug:       Option<super::velocity_debug::VelocityDebugPipeline>,
    /// G-Buffer 各チャンネルのデバッグ可視化パイプライン（シーンビュー表示モード「G-Buffer: 〜」）。
    /// 環境変数ゲートは持たず常時構築する（エディタのコンボから任意に選べる正規機能のため）。
    /// パイプライン 1 本＋UBO 1 個だけで、モードは uniform の enum 値で切り替える。
    pub gbuffer_debug:        super::gbuffer_debug::GBufferDebugPipeline,
    /// 反射（SSR / RT）フルスクリーンパス＋合成パイプライン一式（Phase D6）。
    /// Deferred 有効時のみ frame_renderer が使う（G-Buffer＋scene_hdr 入力→RT_REFLECTION→加算合成）。
    pub reflection:           super::reflection::ReflectionPipelines,
    /// AO（SSAO / RT-AO）フルスクリーンパス＋いもす法ブラー一式（Phase D4）。
    /// Deferred 有効かつ AO モードが Off でないときのみ frame_renderer が使う
    /// （G-Buffer＋深度 → 半解像度 ao_raw → ブラー ao_b → deferred の occlusion 乗算）。
    pub ao:                   super::ao::AoPipelines,
    /// SSGI（スクリーンスペース GI, Phase SSGI）。deferred の camera_bgl/gbuffer_bgl を借りて構築。
    /// 生成パス＋いもす法カラーブラー＋半解像度リソースの一式（frame_renderer が 1F 遅延で駆動）。
    pub ssgi:                 super::ssgi::SsgiPipelines,
    /// RT ソフト影マスク生成＋いもす法デノイズ（Phase RT-Shadow-Denoise）。RT 対応 GPU でのみ Some
    /// （group4 に TLAS を含む rt.lights_bgl を借用するため）。deferred 有効かつ RT 影オンかつソフト影
    /// ライトがあるときだけ frame_renderer が使う。
    pub shadow_mask:          Option<super::shadow_mask::ShadowMaskPipelines>,
    /// 水中コースティクス生成パス（Phase W5.3）。deferred 有効かつ水域があるフレームだけ
    /// frame_renderer が使う（G-Buffer 深度 → 水中判定 → 集光係数 → deferred の Surface.caustics）。
    /// 常時構築するのは、機能 OFF 時に deferred の group1 binding12 を埋めるダミー 1x1 を
    /// ここが持っているため（パイプライン自体の構築コストは起動時 1 回きり）。
    pub caustics:             super::caustics::CausticsPipelines,
    /// 水面反射パス（Phase W5.2）。deferred 有効・反射モードが Off でない・水域があるフレームだけ
    /// frame_renderer が使う（水面の格子を再ラスタライズして反射色を専用 RT へ焼き、
    /// 水面パスがそれを 1 枚 textureLoad してフレネル合成する）。
    /// 常時構築するのは、機能 OFF のフレームに水面パスの反射スロットを埋めるダミー 1x1 を
    /// ここが持っているため（RT 変種は RAY_QUERY 対応 GPU でのみ内部で構築される）。
    pub water_reflection:     super::water_reflection::WaterReflectionPipelines,
}

impl DrawPipelines {
    pub fn new(
        device:         &wgpu::Device,
        queue:          &wgpu::Queue,
        scene_format:   wgpu::TextureFormat,
        surface_format: wgpu::TextureFormat,
        depth_format:   wgpu::TextureFormat,
        cache:          Option<&wgpu::PipelineCache>,
    ) -> Self {
        // scene_format: シーン描画先の HDR オフスクリーンフォーマット（Rgba16Float, Phase R3）。
        //   3D メッシュ・スプライト・ギズモ・アウトライン・帯塗り等、メインパス／キャンバス
        //   オーバーレイへ描くパイプラインはすべてこのフォーマットを使う。
        // surface_format: スワップチェーンフォーマット。トーンマップ後に直接描くパス
        //   （カメラプレビューブリット）のみが使う。
        // ※ ID パス（Rgba32Float）・深度/シャドウパス（カラーなし）はパイプライン内部で
        //   固定フォーマットを使うため sf の値に依存しない。
        let sf = scene_format;
        let df = depth_format;
        // cache を全パイプラインに渡す。
        // 初回は通常コンパイル（15 秒程度）してキャッシュに記録し、
        // 2 回目以降はキャッシュから復元するためコンパイルをスキップする。
        let mesh                = MeshPipeline::new(device, sf, df, cache);
        let skinned_mesh        = SkinnedMeshPipeline::new(device, sf, df, cache);
        // RT 影バリアントは RT 対応 GPU でのみ生成する（非対応時は None＝従来経路のみ）。
        // acceleration_structure を含むシェーダは非対応デバイスではモジュール作成に失敗し得るため、
        // 対応判定を通った場合だけビルドする。これにより非対応 GPU は完全に無変更で動作する。
        let rt = if super::rt_shadow::rt_shadows_supported() {
            Some(RtMeshPipelines::new(device, sf, df, cache))
        } else {
            None
        };
        let unlit_line          = UnlitPipeline::new(device, sf, df, cache);
        let meshlet_cull        = MeshletCullPipeline::new(device, cache);
        let skin_compute        = SkinComputePipeline::new(device, cache);
        // RT スキン BLAS 用の変形 compute（Phase RT-Skin）。
        let skin_deform         = SkinDeformPipeline::new(device, cache);
        let depth_prepass       = DepthPrepassPipelines::new(device, df, cache);
        let shadow_depth        = ShadowDepthPipelines::new(device, super::shadow::SHADOW_DEPTH_FORMAT, cache);
        let id_pass             = IdPassPipeline::new(device, sf, df, cache);
        let outline             = OutlinePipeline::new(device, sf, df, cache);
        let sprite              = SpritePipeline::new(device, queue, sf, df, cache);
        let sprite_outline      = SpriteOutlinePipeline::new(device, sf, df, cache);
        let canvas_id           = CanvasIdPipeline::new(device, queue, df, cache);
        // カメラプレビューブリットはトーンマップ後にスワップチェーンへ直接描くため surface_format。
        let camera_preview_blit = CameraPreviewBlitPipeline::new(device, surface_format, df, cache);
        let bar_fill            = BarFillPipeline::new(device, sf, df, cache);
        // 透明描画パイプライン（Phase R5）。シーン HDR（sf）へ描くため sf/df を渡す。
        let transparent         = super::transparency::TransparentPipelines::new(device, sf, df, cache);
        // GPU パーティクル（Phase RP）。描画は group0 に mesh の camera BGL を流用する
        // （共有カメラ BindGroup をそのまま使うため同一 BGL オブジェクトを渡す）。
        let particle_compute    = ParticleComputePipeline::new(device, cache);
        let particles           = ParticlePipelines::new(device, queue, sf, df, &mesh.camera_bgl, cache);
        // スカイボックス（Phase R9）。シーン HDR（sf）へ描くため sf/df を渡す。
        let skybox              = super::skybox::SkyboxPipelines::new(device, sf, df, cache);
        // Clustered Lighting のクラスタ構築 compute（Phase C1）。
        let cluster_build       = ClusterBuildPipeline::new(device, cache);
        // DDGI プローブ更新 compute（Phase RT-GI）。RT 対応 GPU でのみ構築する。
        let gi_update           = if super::rt_shadow::rt_shadows_supported() {
            Some(GiUpdatePipeline::new(device, cache))
        } else {
            None
        };
        // G-Buffer 書き込み（Phase D3 Phase A）。mesh/skinned_mesh の BGL を借りて構築するため
        // それらの構築後に呼ぶ（モジュール冒頭コメントの BGL 再利用方針を参照）。
        let gbuffer              = super::gbuffer::GBufferPipelines::new(device, &mesh, &skinned_mesh, df, cache);
        // デファードのフルスクリーン・ライティング復元（Phase D3 Phase A）。
        // sf（シーン HDR）へ出力する（PostPipeline 等と同じ HDR オフスクリーン規約）。
        let deferred              = super::deferred::DeferredLightingPipelines::new(device, queue, sf, df, cache);
        // 速度バッファのデバッグ可視化。環境変数が立っているときだけ構築する
        // （デバッグ専用の追加パスなので、既定では 1 バイトの GPU 資源も確保しない）。
        let velocity_debug        = if *super::velocity_debug::VELOCITY_DEBUG_ENABLED {
            eprintln!(
                "[SEED renderer] {}=1: 速度バッファ（モーションベクタ）の可視化を有効化しました                  （灰色=速度0 / 赤・シアン=水平 / 緑・マゼンタ=垂直 / 青=飽和）",
                super::velocity_debug::VELOCITY_DEBUG_ENV,
            );
            Some(super::velocity_debug::VelocityDebugPipeline::new(device, sf, cache))
        } else {
            None
        };
        // G-Buffer デバッグ可視化。出力先は sf（シーン HDR）。BGL は自前で持つため
        // 他パイプラインへの依存は無い（構築順は任意）。
        let gbuffer_debug         = super::gbuffer_debug::GBufferDebugPipeline::new(device, sf, cache);
        // 反射（Phase D6）。deferred の camera_bgl/gbuffer_bgl を借りて構築するため deferred の後に呼ぶ。
        // 出力先は sf（scene_hdr / RT_REFLECTION と同じ HDR）。
        let reflection            = super::reflection::ReflectionPipelines::new(device, queue, &deferred, sf, cache);
        // AO（Phase D4）。deferred の camera_bgl/gbuffer_bgl を借りて構築するため deferred の後に呼ぶ。
        // 白 1x1 の初期化に queue を使う（AO=Off 時の t_ao スロット用）。
        let ao                    = super::ao::AoPipelines::new(device, queue, &deferred, cache);
        // SSGI（Phase SSGI）。deferred の camera_bgl/gbuffer_bgl を借りるため deferred の後に呼ぶ。
        // ダミー 1x1 の初期化に queue を使う（SSGI 非使用時の t_ssgi スロット用）。
        let ssgi                  = super::ssgi::SsgiPipelines::new(device, queue, &deferred, cache);
        // 水中コースティクス（Phase W5.3）。水面パスと同じ高さ場モジュールを連結する独立パス。
        let caustics              = super::caustics::CausticsPipelines::new(device, queue, cache);
        // 水面反射（Phase W5.2）。水面パスと同じ高さ場・同じ格子で反射色を焼く独立パス。
        // RT 変種は RAY_QUERY 対応 GPU のみ内部で構築され、非対応なら SSR 変種だけになる。
        // ダミー 1x1（黒＝反射寄与ゼロ）の初期化に queue を使う。
        let water_reflection      = super::water_reflection::WaterReflectionPipelines::new(device, queue, cache);
        // RT ソフト影マスク（Phase RT-Shadow-Denoise）。RT 対応 GPU（rt=Some）でのみ構築する
        // （group4 に TLAS を含む rt.lights_bgl を借用するため）。deferred の後に呼ぶ。
        let shadow_mask           = rt.as_ref().map(|r| {
            super::shadow_mask::ShadowMaskPipelines::new(device, &deferred, &r.lights_bgl, cache)
        });
        Self { mesh, skinned_mesh, rt, unlit_line, meshlet_cull, skin_compute, skin_deform, depth_prepass, shadow_depth, id_pass, outline, sprite, sprite_outline, canvas_id, camera_preview_blit, bar_fill, transparent, particle_compute, particles, skybox, cluster_build, gi_update, gbuffer, deferred, velocity_debug, gbuffer_debug, reflection, ao, ssgi, shadow_mask, caustics, water_reflection }
    }
}
