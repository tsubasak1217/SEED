// ============================================================
//  deferred.rs — フルスクリーン・ライティングパイプライン（Phase D3 Deferred Phase A）
//
//  ## 役割（単一責任）
//  G-Buffer（gbuffer.rs が焼いた 4 枚の MRT + 深度）を読み、evaluate_lighting
//  （lighting_eval.wgsl・既存無改変）を呼ぶフルスクリーン三角形パイプラインを持つ。
//  実際の描画呼び出し（G-Buffer の確保・BindGroup 生成・パス実行）は Phase B で行う。
//  本ファイル（Phase A）はパイプライン構築と BindGroup 生成ヘルパーの用意までに留める。
//
//  ## TOML ビルダーで組める理由
//  gbuffer.rs の MRT 書き込みパイプラインとは異なり、本パスの出力は単一カラーターゲット
//  （HDR シーンへの合成 1 枚）のため、既存の RenderPipelineBuilder（TOML + WGSL リフレクション）
//  にそのまま乗せられる。deferred_lighting.wgsl が宣言する group は 0（カメラ）・1（G-Buffer
//  入力）・4（ライト、light_common.wgsl 由来）のみで group2/3 は未使用だが、
//  RenderPipelineBuilder::build は「最大 group 番号 + 1」まで空 gap BGL で自動的に埋めるため、
//  返り値の Vec<BindGroupLayout> は [group0, group1, group2(gap), group3(gap), group4] の
//  5 要素になる（pipeline_config.rs の `num_groups` 決定ロジック参照）。
// ============================================================

use super::pipeline::get_shader_source;
use super::pipeline_config::RenderPipelineBuilder;

// ============================================================
//  連結リストの単一の真実source（バインドレス変種のみ Rust 側に持つ）
// ============================================================

/// rt_bindless 変種のシェーダ連結順（**この配列が唯一の定義**）。
///
/// rt_off / rt_on 変種は `pipelines/deferred_lighting.toml` /
/// `pipelines/deferred_lighting_rt.toml` の `shader_sources` が正典だが、バインドレス変種は
/// TOML を持たず（group3 の binding_array を WGSL リフレクションで組めないため手動レイアウト）、
/// 連結順を Rust 側に置くしかない。
///
/// シェーディングアセット（shading_asset.rs）は 3 変種すべての連結リストを
/// 「TOML と本定数から機械的に取得し、`shading_dispatch.wgsl` の要素だけを差し替える」形で
/// 導出する。したがって本配列を書き換えればアセット経路にも自動で反映される（二重管理しない）。
pub const RT_BINDLESS_SHADER_SOURCES: &[&str] = &[
    "cluster_common.wgsl", "pbr_common.wgsl", "ddgi_common.wgsl", "light_common.wgsl",
    "shadow.wgsl", "rt_shadow_on.wgsl", "bindless_common.wgsl", "rt_shadow_tint_bindless.wgsl",
    "surface.wgsl", "shading_contract.wgsl", "shading_dispatch.wgsl",
    "lighting_eval.wgsl", "deferred_lighting.wgsl",
];

// ============================================================
//  DeferredLightingPipelines — フルスクリーン・ライティング復元パイプライン一式
// ============================================================

/// G-Buffer からライティングを復元するフルスクリーンパイプライン一式。
///
/// `pipeline` は RT 非対応/オフ用（rt_shadow_off 連結）、`rt` は RT 対応 GPU でのみ
/// 生成される RT 影対応バリアント（rt_shadow_on 連結）。フラグメントは
/// LightMeta.rt_shadows で実行時分岐するため、RT 対応 GPU では常に `rt` を使う
/// （mesh/skinned の RtMeshPipelines と同じ設計方針。pipeline.rs の RtMeshPipelines 参照）。
pub struct DeferredLightingPipelines {
    /// RT 非対応・RT オフ用パイプライン（常に生成される）。
    pub pipeline:    wgpu::RenderPipeline,
    /// RT 影対応バリアント。RT 対応 GPU でのみ Some（rt_shadow::rt_shadows_supported() 参照）。
    pub rt:          Option<wgpu::RenderPipeline>,
    /// RT 影＋バインドレス色付き影バリアント（B3）。RT 対応 かつ バインドレス対応 GPU でのみ Some。
    /// group3 に色付き影のバインドレス資源（instance_table/UV/index/テクスチャ配列/サンプラー）を
    /// 置き、ヒット点テクスチャ実サンプル＋Mask アルファ抜きで影を染める。draw は group3 に
    /// bindless.create_colored_shadow_bind_group で作った BG を bind する（frame_renderer）。
    pub rt_bindless: Option<wgpu::RenderPipeline>,
    /// group3: 色付き影バインドレス資源の BGL（B3, バインドレス対応時のみ Some）。
    /// frame_renderer が per-frame BG 構築に使う。shadow_mask も同一 BGL を借用する。
    pub colored_shadow_bgl: Option<wgpu::BindGroupLayout>,
    /// group0: カメラ（deferred_lighting.wgsl 自前宣言の CameraUniform、shader_common.wgsl
    /// と同一レイアウト）。
    pub camera_bgl:  wgpu::BindGroupLayout,
    /// group1: G-Buffer 入力（テクスチャ 5 枚 + サンプラー 1 個）。
    pub gbuffer_bgl: wgpu::BindGroupLayout,
    /// group2: 未使用（gap）。draw 時に empty_bg2 を必須セットする。
    pub gap_bgl2:    wgpu::BindGroupLayout,
    /// group3: 未使用（gap）。draw 時に empty_bg3 を必須セットする。
    pub gap_bgl3:    wgpu::BindGroupLayout,
    /// group2 の空 BindGroup（起動時 1 回だけ生成し使い回す）。
    pub empty_bg2:   wgpu::BindGroup,
    /// group3 の空 BindGroup（起動時 1 回だけ生成し使い回す）。
    pub empty_bg3:   wgpu::BindGroup,
    /// G-Buffer サンプリング用の non-filtering（point）サンプラー。
    /// G-Buffer は textureLoad で読むため本来サンプラーは不要だが、
    /// deferred_lighting.wgsl の group1 binding5（s_gbuffer）を満たすために保持する
    /// （将来のフィルタ処理拡張に備えた予約バインディング。deferred_lighting.wgsl 参照）。
    pub gbuffer_sampler: wgpu::Sampler,
    /// シャドウマスク非対象時に group1 binding10（t_shadow_mask）を埋めるダミー 1x1×4 配列（白＝遮蔽なし）。
    /// RT 非対応 GPU でも deferred は走るため、常に存在するここに置く（gbuffer_bgl が D2Array を要求する）。
    #[allow(dead_code)]
    mask_dummy_tex:  wgpu::Texture,
    /// ダミーマスクの D2Array ビュー（Phase RT-Shadow-Denoise）。
    pub mask_dummy_view: wgpu::TextureView,
    /// シャドウマスク（半解像度）をフル解像度へバイリニアアップサンプルする Filtering サンプラー。
    /// group1 binding11（s_shadow_mask）に渡す。実マスク・ダミーとも共用する。
    pub mask_sampler: wgpu::Sampler,
}

impl DeferredLightingPipelines {
    /// フルスクリーン・ライティングパイプラインを構築する。
    ///
    /// - `out_format` : 出力先（シーン HDR, Rgba16Float）。
    /// - `df`         : 深度フォーマット。no_depth=true のため実際には未使用だが、
    ///                  RenderPipelineBuilder::new のシグネチャを満たすためのダミー値。
    pub fn new(
        device:     &wgpu::Device,
        queue:      &wgpu::Queue,
        out_format: wgpu::TextureFormat,
        df:         wgpu::TextureFormat,
        cache:      Option<&wgpu::PipelineCache>,
    ) -> Self {
        // ── RT オフ版（常に構築） ───────────────────────────────
        let (pipeline, bgls) = RenderPipelineBuilder::new(
            device, include_str!("pipelines/deferred_lighting.toml"), out_format, df,
        )
        .with_label("deferred_lighting")
        .with_cache(cache)
        .build(get_shader_source);

        // group 番号順 (0=camera, 1=gbuffer, 2=gap, 3=gap, 4=lights) にイテレートして取り出す。
        // group4（lights_bgl）は本構造体では保持しない（draw 時は既存 LightBuffer の
        // BindGroup をそのまま使う＝レイアウト等価性に依拠。mesh 系と同じ既存慣例）。
        let mut it = bgls.into_iter();
        let camera_bgl  = it.next().unwrap(); // group 0
        let gbuffer_bgl = it.next().unwrap(); // group 1
        let gap_bgl2    = it.next().unwrap(); // group 2（空レイアウト）
        let gap_bgl3    = it.next().unwrap(); // group 3（空レイアウト）
        let _lights_bgl = it.next().unwrap(); // group 4（既存 LightBuffer BG を使うため破棄）

        let empty_bg2 = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Deferred Lighting Empty BG (group 2)"),
            layout:  &gap_bgl2,
            entries: &[],
        });
        let empty_bg3 = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Deferred Lighting Empty BG (group 3)"),
            layout:  &gap_bgl3,
            entries: &[],
        });

        // ── RT オン版（RT 対応 GPU でのみ構築） ─────────────────
        // acceleration_structure を含むシェーダは非対応デバイスではモジュール作成に
        // 失敗し得るため、対応判定を通った場合だけビルドする（RtMeshPipelines と同じ方針）。
        // rt 変種の group4（ライト＋シャドウ＋TLAS binding6/albedo binding14）BGL を捕まえておく。
        // バインドレス変種の手動レイアウト構築（group4）に流用する（両者は同一 group4 レイアウト）。
        let mut rt_group4_bgl: Option<wgpu::BindGroupLayout> = None;
        let rt = if super::rt_shadow::rt_shadows_supported() {
            let (rt_pipeline, bgls_rt) = RenderPipelineBuilder::new(
                device, include_str!("pipelines/deferred_lighting_rt.toml"), out_format, df,
            )
            .with_label("deferred_lighting_rt")
            .with_cache(cache)
            .build(get_shader_source);
            // group 順 [0 camera,1 gbuffer,2 gap,3 gap,4 lights+TLAS]。group4 を控える。
            rt_group4_bgl = bgls_rt.into_iter().nth(4);
            Some(rt_pipeline)
        } else {
            None
        };

        // ── RT 影＋バインドレス色付き影バリアント（B3, RT 対応 かつ バインドレス対応 GPU のみ）──
        // group3 に色付き影のバインドレス資源（uniform を含まない＝binding_array と両立）を置く。
        // group4 は rt 変種と同一（rt_group4_bgl）。手動レイアウト（reflection.rs / shadow_mask.rs と同流儀）。
        let use_bindless = super::rt_shadow::rt_shadows_supported() && super::bindless::bindless_supported();
        let (rt_bindless, colored_shadow_bgl) = if use_bindless {
            let g4 = rt_group4_bgl.as_ref()
                .expect("RT 対応時は rt_group4_bgl が必ず確定している");
            let cs_bgl = super::bindless::colored_shadow_bgl(device, super::bindless::bindless_capacity());
            // 連結順は RT_BINDLESS_SHADER_SOURCES が唯一の定義。
            // （テスト deferred_lighting_shaders_parse_and_validate_rt_bindless は同じ順序を
            //   include_str! で独立に書き下ろしており、順序がずれれば naga 検証で落ちる番人になる）。
            let combined: String = RT_BINDLESS_SHADER_SOURCES
                .iter().map(|n| get_shader_source(n)).collect::<Vec<_>>().join("\n");
            let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
                label:  Some("deferred_lighting_rt_bindless"),
                source: wgpu::ShaderSource::Wgsl(combined.into()),
            });
            let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("deferred_lighting_rt_bindless"),
                bind_group_layouts: &[&camera_bgl, &gbuffer_bgl, &gap_bgl2, &cs_bgl, g4],
                push_constant_ranges: &[],
            });
            let pipe = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label:  Some("deferred_lighting_rt_bindless"),
                layout: Some(&layout),
                vertex: wgpu::VertexState {
                    module: &shader, entry_point: Some("vs_fullscreen"),
                    buffers: &[], compilation_options: Default::default(),
                },
                fragment: Some(wgpu::FragmentState {
                    module: &shader, entry_point: Some("fs_deferred"),
                    targets: &[Some(wgpu::ColorTargetState {
                        format: out_format, blend: None, write_mask: wgpu::ColorWrites::ALL,
                    })],
                    compilation_options: Default::default(),
                }),
                primitive:     wgpu::PrimitiveState::default(),
                depth_stencil: None,
                multisample:   wgpu::MultisampleState::default(),
                multiview:     None,
                cache,
            });
            (Some(pipe), Some(cs_bgl))
        } else {
            (None, None)
        };

        // G-Buffer は textureLoad で読むため厳密には不要だが、group1 binding5 の
        // サンプラーバインディングを満たすために non-filtering（point）で用意する。
        let gbuffer_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("Deferred GBuffer Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Nearest,
            min_filter:     wgpu::FilterMode::Nearest,
            mipmap_filter:  wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        // シャドウマスク用ダミー 1x1×4 配列（白＝遮蔽なし）＋ Filtering サンプラー（Phase RT-Shadow-Denoise）。
        // マスク非対象フレーム／RT 非対応 GPU の group1 binding10/11 を埋める（常在させる）。
        let mask_dummy_tex = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Deferred Shadow Mask Dummy 1x1x4"),
            size: wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: super::shadow_mask::RT_SHADOW_MASK_LIGHTS },
            mip_level_count: 1, sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: super::shadow_mask::SHADOW_MASK_FORMAT,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        // 全レイヤの 1 texel を白（R=G=B=A=1.0）で埋める。Rgba16Float の 1.0 は f16 0x3C00（LE: 00 3C）。
        // 1 texel=8 バイト、rows_per_image=1 で 1 レイヤ=1 行。RT_SHADOW_MASK_LIGHTS レイヤぶんを一括書き込み。
        let white_texel: [u8; 8] = [0x00, 0x3C, 0x00, 0x3C, 0x00, 0x3C, 0x00, 0x3C];
        let mut mask_white = Vec::with_capacity(8 * super::shadow_mask::RT_SHADOW_MASK_LIGHTS as usize);
        for _ in 0..super::shadow_mask::RT_SHADOW_MASK_LIGHTS { mask_white.extend_from_slice(&white_texel); }
        queue.write_texture(
            mask_dummy_tex.as_image_copy(),
            &mask_white,
            wgpu::ImageDataLayout { offset: 0, bytes_per_row: Some(8), rows_per_image: Some(1) },
            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: super::shadow_mask::RT_SHADOW_MASK_LIGHTS },
        );
        let mask_dummy_view = mask_dummy_tex.create_view(&wgpu::TextureViewDescriptor {
            label: Some("Deferred Shadow Mask Dummy View"),
            dimension: Some(wgpu::TextureViewDimension::D2Array),
            ..Default::default()
        });
        let mask_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("Deferred Shadow Mask Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        Self { pipeline, rt, rt_bindless, colored_shadow_bgl, camera_bgl, gbuffer_bgl, gap_bgl2, gap_bgl3,
               empty_bg2, empty_bg3, gbuffer_sampler,
               mask_dummy_tex, mask_dummy_view, mask_sampler }
    }
}

// ============================================================
//  create_gbuffer_bind_group — G-Buffer 入力 BindGroup 生成（Phase B で使用予定）
// ============================================================

/// G-Buffer 4 枚 + 深度テクスチャから group1 の BindGroup を生成する。
///
/// Phase B でフレームループが「G-Buffer 確保 → 本関数で BindGroup 生成 → ライティングパス
/// 実行」の順に呼ぶ想定（G-Buffer はウィンドウリサイズ時に再生成されるため、本関数も
/// 毎フレーム・毎リサイズで呼び直すことになる。post::rt_pool.rs の RtPool 経由の
/// テクスチャビューをそのまま渡す想定）。Phase D4 で AO 入力（binding6/7）を追加した。
pub fn create_gbuffer_bind_group(
    device:      &wgpu::Device,
    gbuffer_bgl: &wgpu::BindGroupLayout,
    g0_view:     &wgpu::TextureView,
    g1_view:     &wgpu::TextureView,
    g2_view:     &wgpu::TextureView,
    g3_view:     &wgpu::TextureView,
    depth_view:  &wgpu::TextureView,
    sampler:     &wgpu::Sampler,
    // ── AO 入力（Phase D4: SSAO / RT-AO）───────────────────────────
    // binding 6=AO テクスチャ（半解像度 AO の .r。AO=Off 時は白 1x1）、
    // binding 7=AO サンプラー（Filtering=linear。半解像度→フル解像度のバイリニア用）。
    // deferred_lighting.wgsl が group1 binding6/7 を宣言するため gbuffer_bgl は 8 entry。
    ao_view:     &wgpu::TextureView,
    ao_sampler:  &wgpu::Sampler,
    // ── SSGI 入力（Phase SSGI: スクリーンスペース GI, 1 フレーム遅延）───────────────
    // binding 8=SSGI テクスチャ（半解像度 .rgb の間接放射照度。SSGI 非使用時はダミー 1x1）、
    // binding 9=SSGI サンプラー（Filtering=linear。半解像度→フル解像度のバイリニア用）。
    // deferred_lighting.wgsl が group1 binding8/9 を宣言するため gbuffer_bgl は 10 entry。
    // AO 生成・反射パスも本関数を使うが、それらの WGSL は group1 の 0..5 のみ宣言する（subset は
    // 合法）ため、それらの呼び出しではダミー SSGI テクスチャ／サンプラーを渡してよい。
    ssgi_view:    &wgpu::TextureView,
    ssgi_sampler: &wgpu::Sampler,
    // ── シャドウマスク入力（Phase RT-Shadow-Denoise）: deferred_lighting.wgsl の group1 binding10/11 ──
    // binding 10 = 半解像度 4 レイヤの texture_2d_array（.rgb にデノイズ済み遮蔽率）、非対象時はダミー白。
    // binding 11 = Filtering サンプラー（半解像度→フル解像度のバイリニア）。AO/SSGI/反射パスは group1 を
    // 0..5 のみ宣言する（subset 合法）ため、それらの呼び出しでは deferred.mask_dummy_view/mask_sampler を渡す。
    mask_view:    &wgpu::TextureView,
    mask_sampler: &wgpu::Sampler,
    // ── 水中コースティクス入力（Phase W5.3）: deferred_lighting.wgsl の group1 binding12 ──
    // binding 12 = フル解像度 1ch（R16Float）の集光係数。**サンプラーは無い**（textureLoad で 1:1）。
    // 機能 OFF のフレーム（水域なし・deferred 以外・G-Buffer デバッグ中）は
    // CausticsPipelines::dummy_view（1x1 黒）を渡す。範囲外 textureLoad は WGSL 仕様で
    // ゼロを返すため、全画面が caustics=0＝増幅なしになる。
    // AO/シャドウマスク/SSGI/反射パスは group1 を 0..5 のみ宣言する（subset 合法）ため、
    // それらの呼び出しでも同じダミーを渡してよい。
    caustics_view: &wgpu::TextureView,
    // ── 影の屈折オフセット入力（Phase W5.3）: group1 binding13 ────────────────────
    // binding 13 = フル解像度 2ch（Rg16Float）の影サンプル位置オフセット（ワールド XZ・m）。
    // **サンプラーは無い**（textureLoad で 1:1）。機能 OFF のフレームは
    // CausticsPipelines::dummy_offset_view（1x1 ゼロ）を渡す（0＝ずらさない＝従来と同一）。
    //
    // **RT ソフト影マスク生成パス（shadow_mask.wgsl）はこの binding を実際に読む**
    // （レイ原点をずらせるのは生成側だけのため）。AO / SSGI / 反射パスは group1 の
    // 0..5 のみ宣言する（subset 合法）ので、それらの呼び出しではダミーを渡してよい。
    caustics_offset_view: &wgpu::TextureView,
) -> wgpu::BindGroup {
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label:  Some("Deferred GBuffer BG (group 1)"),
        layout: gbuffer_bgl,
        entries: &[
            wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(g0_view) },
            wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::TextureView(g1_view) },
            wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::TextureView(g2_view) },
            wgpu::BindGroupEntry { binding: 3, resource: wgpu::BindingResource::TextureView(g3_view) },
            wgpu::BindGroupEntry { binding: 4, resource: wgpu::BindingResource::TextureView(depth_view) },
            wgpu::BindGroupEntry { binding: 5, resource: wgpu::BindingResource::Sampler(sampler) },
            wgpu::BindGroupEntry { binding: 6, resource: wgpu::BindingResource::TextureView(ao_view) },
            wgpu::BindGroupEntry { binding: 7, resource: wgpu::BindingResource::Sampler(ao_sampler) },
            wgpu::BindGroupEntry { binding: 8, resource: wgpu::BindingResource::TextureView(ssgi_view) },
            wgpu::BindGroupEntry { binding: 9, resource: wgpu::BindingResource::Sampler(ssgi_sampler) },
            wgpu::BindGroupEntry { binding: 10, resource: wgpu::BindingResource::TextureView(mask_view) },
            wgpu::BindGroupEntry { binding: 11, resource: wgpu::BindingResource::Sampler(mask_sampler) },
            wgpu::BindGroupEntry { binding: 12, resource: wgpu::BindingResource::TextureView(caustics_view) },
            wgpu::BindGroupEntry { binding: 13, resource: wgpu::BindingResource::TextureView(caustics_offset_view) },
        ],
    })
}

// ============================================================
//  WGSL 静的検証（naga parse + validate）
// ============================================================
#[cfg(test)]
mod tests {
    /// デファードのライティング復元連結 WGSL（rt_off / rt_on）を naga で parse + validate する。
    /// 連結順は pipelines/deferred_lighting*.toml と一致させること。
    #[test]
    fn deferred_lighting_shaders_parse_and_validate_rt_off() {
        let cluster  = include_str!("shaders/cluster_common.wgsl");
        let pbr_c    = include_str!("shaders/pbr_common.wgsl");
        let light_c  = include_str!("shaders/light_common.wgsl");
        let ddgi_c   = include_str!("shaders/ddgi_common.wgsl");
        let shadow   = include_str!("shaders/shadow.wgsl");
        let rt_off   = include_str!("shaders/rt_shadow_off.wgsl");
        let surf     = include_str!("shaders/surface.wgsl");
        // シェーディング契約 v1（型・標準ライブラリ）＋ 既定ディスパッチ（shade_surface）。
        let sc       = include_str!("shaders/shading_contract.wgsl");
        let sd       = include_str!("shaders/shading_dispatch.wgsl");
        let light_ev = include_str!("shaders/lighting_eval.wgsl");
        let deferred = include_str!("shaders/deferred_lighting.wgsl");

        let src = [cluster, pbr_c, ddgi_c, light_c, shadow, rt_off, surf, sc, sd, light_ev, deferred].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[deferred_lighting rt_off] WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("[deferred_lighting rt_off] WGSL validate 失敗: {e:?}"));
    }

    /// RT 影対応バリアント（rt_shadow_on 連結）。acceleration_structure / rayQuery を
    /// 使うため RAY_QUERY ケイパビリティを有効にして検証する（rt_shadow.rs のテストに倣う）。
    #[test]
    fn deferred_lighting_shaders_parse_and_validate_rt_on() {
        let cluster  = include_str!("shaders/cluster_common.wgsl");
        let pbr_c    = include_str!("shaders/pbr_common.wgsl");
        let light_c  = include_str!("shaders/light_common.wgsl");
        let ddgi_c   = include_str!("shaders/ddgi_common.wgsl");
        let shadow   = include_str!("shaders/shadow.wgsl");
        let rt_on    = include_str!("shaders/rt_shadow_on.wgsl");
        let tint_avg = include_str!("shaders/rt_shadow_tint_avg.wgsl");
        let surf     = include_str!("shaders/surface.wgsl");
        // シェーディング契約 v1（型・標準ライブラリ）＋ 既定ディスパッチ（shade_surface）。
        let sc       = include_str!("shaders/shading_contract.wgsl");
        let sd       = include_str!("shaders/shading_dispatch.wgsl");
        let light_ev = include_str!("shaders/lighting_eval.wgsl");
        let deferred = include_str!("shaders/deferred_lighting.wgsl");

        let src = [cluster, pbr_c, ddgi_c, light_c, shadow, rt_on, tint_avg, surf, sc, sd, light_ev, deferred].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[deferred_lighting rt_on] WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            // 加速構造/レイクエリを使う RT バリアントの検証に RAY_QUERY が必須。
            naga::valid::Capabilities::RAY_QUERY,
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("[deferred_lighting rt_on] WGSL validate 失敗: {e:?}"));
    }

    /// バインドレス色付き影バリアント（B3）。rt_shadow_tint_bindless.wgsl を連結し、group3 の
    /// binding_array（テクスチャ配列）＋ヒット点テクスチャサンプル＋Mask アルファ抜きを含む。
    /// deferred_lighting_rt のバインドレス影パイプライン（deferred.rs で手動構築）と同一連結順。
    /// 連結順は deferred.rs の rt_bindless 構築と一致させること。
    #[test]
    fn deferred_lighting_shaders_parse_and_validate_rt_bindless() {
        let cluster   = include_str!("shaders/cluster_common.wgsl");
        let pbr_c     = include_str!("shaders/pbr_common.wgsl");
        let light_c   = include_str!("shaders/light_common.wgsl");
        let ddgi_c    = include_str!("shaders/ddgi_common.wgsl");
        let shadow    = include_str!("shaders/shadow.wgsl");
        let rt_on     = include_str!("shaders/rt_shadow_on.wgsl");
        let bindless  = include_str!("shaders/bindless_common.wgsl");
        let tint_bl   = include_str!("shaders/rt_shadow_tint_bindless.wgsl");
        let surf      = include_str!("shaders/surface.wgsl");
        // シェーディング契約 v1（型・標準ライブラリ）＋ 既定ディスパッチ（shade_surface）。
        let sc        = include_str!("shaders/shading_contract.wgsl");
        let sd        = include_str!("shaders/shading_dispatch.wgsl");
        let light_ev  = include_str!("shaders/lighting_eval.wgsl");
        let deferred  = include_str!("shaders/deferred_lighting.wgsl");

        let src = [cluster, pbr_c, ddgi_c, light_c, shadow, rt_on, bindless, tint_bl, surf, sc, sd, light_ev, deferred].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[deferred_lighting rt_bindless] WGSL parse 失敗: {e:?}"));
        // binding_array の非一様インデックス＋RAY_QUERY を要求（reflection_rt on と同じ）。
        let caps = naga::valid::Capabilities::RAY_QUERY
            | naga::valid::Capabilities::SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING;
        let mut validator = naga::valid::Validator::new(naga::valid::ValidationFlags::all(), caps);
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("[deferred_lighting rt_bindless] WGSL validate 失敗: {e:?}"));
    }

    /// 水中コースティクス（Phase W5.3）の**消費経路**が生きていること。
    ///
    /// この配線はどこか 1 箇所が落ちても「コースティクスが出ない」だけでコンパイルは通る
    /// （水域を置かないと目視でも気づけない）ため、3 点を文字列で押さえる:
    ///   ① deferred が group1 binding12 を宣言している（Rust 側の entries と対）
    ///   ② Surface に受け皿がある（フォワードはゼロ初期化＝無効、が成立する前提）
    ///   ③ ライト評価が平行光の直達だけを増幅している（影の中で光らない根拠）
    #[test]
    fn caustics_consumption_path_is_wired() {
        let deferred = include_str!("shaders/deferred_lighting.wgsl");
        assert!(deferred.contains("@group(1) @binding(12) var t_caustics: texture_2d<f32>;"),
            "deferred_lighting.wgsl の t_caustics（group1 binding12）宣言が消えている");
        // サンプラーは足さない設計（フル解像度 1:1 の textureLoad）。
        assert!(deferred.contains("let caustics_raw = textureLoad(t_caustics, pix, 0);"),
            "deferred が t_caustics を読んでいない");
        // 透過率は乗算項: 全成分 0（クリア値・ダミー・範囲外）を中立値へ写す守りが必須。
        assert!(deferred.contains("vec4<f32>(1.0, 1.0, 1.0, 0.0)"),
            "非水中ピクセルの中立値 (1,1,1,0) への写しが消えている（消えると画面が黒くなる）");

        let surface = include_str!("shaders/surface.wgsl");
        assert!(surface.contains("caustics: vec4<f32>,"),
            "surface.wgsl の Surface に caustics フィールドが無い");

        let eval = include_str!("shaders/lighting_eval.wgsl");
        assert!(eval.contains("if light.kind == LIGHT_KIND_DIRECTIONAL"),
            "平行光限定のコースティクス増幅が消えている");
        assert!(eval.contains("radiance = radiance * s.caustics.rgb * (1.0 + s.caustics.a);"),
            "変調式が変わっている（影適用後の radiance へ 透過率×(1+集光) を掛けるのが要件）");
        // フォワード採取点の中立値設定（乗算項なのでゼロ初期化では直達光が消える）。
        let gather = include_str!("shaders/surface_gather.wgsl");
        assert!(gather.contains("s.caustics      = vec4<f32>(1.0, 1.0, 1.0, 0.0);"),
            "surface_gather の caustics 中立値設定が消えている（フォワードの直達光が黒くなる）");
        // 逆光透け用の radiance_direct には掛けないこと（影を無視する項に集光を足さない）。
        assert!(!eval.contains("radiance_direct = radiance_direct * (1.0 + s.caustics)"),
            "radiance_direct にコースティクスを掛けてはいけない");
    }

    /// 水中に落ちる影の**屈折ゆらぎ**（Phase W5.3）の配線が全経路で生きていること。
    ///
    /// コースティクス本体と違い、この機能は「影が揺らがない」だけで絵が壊れないため、
    /// 配線が 1 本落ちても目視・コンパイルの両方で気づけない。影の 3 経路それぞれの
    /// 適用点と、「非水中／機能 OFF では厳密に従来と同一」を担保する中立値を文字列で固定する。
    #[test]
    fn shadow_refraction_path_is_wired() {
        // ① 生成側: コースティクスパスが 2 枚目の MRT（location1）へオフセットを書く。
        let caustics = include_str!("shaders/caustics.wgsl");
        assert!(caustics.contains("@location(1) shadow_offset: vec2<f32>,"),
            "caustics.wgsl の 2 枚目の MRT（影の屈折オフセット）宣言が消えている");
        assert!(caustics.contains("(sxz - world_pos.xz) * p.caustics.w"),
            "オフセット式が変わっている（屈折点 sxz と水底点の差 × 誇張倍率 caustics.w が要件）");

        // ② 消費側 A: deferred が binding13 を読んで Surface へ渡す。
        let deferred = include_str!("shaders/deferred_lighting.wgsl");
        assert!(deferred.contains("@group(1) @binding(13) var t_caustics_offset: texture_2d<f32>;"),
            "deferred_lighting.wgsl の t_caustics_offset（group1 binding13）宣言が消えている");
        assert!(deferred.contains("s.shadow_refract_offset = textureLoad(t_caustics_offset, pix, 0).xy;"),
            "deferred が影の屈折オフセットを Surface へ渡していない");
        let surface = include_str!("shaders/surface.wgsl");
        assert!(surface.contains("shadow_refract_offset: vec2<f32>,"),
            "surface.wgsl の Surface に shadow_refract_offset フィールドが無い");

        // ③ 消費側 B: ライト評価が**平行光の影だけ**をずらす（シャドウマップ／インライン RT）。
        let eval = include_str!("shaders/lighting_eval.wgsl");
        // 改行コード（CRLF/LF）に依存しないよう、1 行ずつ独立に押さえる。
        assert!(eval.contains("var shadow_pos = s.world_pos;"),
            "影サンプル位置の変数（既定＝素のワールド座標）が消えている");
        assert!(eval.contains("+ vec3<f32>(s.shadow_refract_offset.x, 0.0, s.shadow_refract_offset.y);"),
            "平行光限定の影サンプル位置オフセットが消えている");
        assert!(eval.contains("sample_shadow_dir(shadow_pos, view_z)"),
            "シャドウマップ経路（既定の影方式）がオフセット位置を使っていない");
        assert!(eval.contains("shadow_pos, Ng, Nv, L, light_dist, cone_radius, s.frag_coord, true,"),
            "インライン RT 影経路がオフセット位置をレイ原点にしていない");
        // カスケード選択は素の view_z のまま（ずらすとカスケード境界で影の解像度が明滅する）。
        assert!(eval.contains("let view_z = (u_camera.view * vec4<f32>(s.world_pos, 1.0)).z;"),
            "カスケード選択の view_z がオフセット由来に変わっている");

        // ④ 消費側 C: RT ソフト影マスクは**生成パス側**でレイ原点をずらす（消費側では不可能）。
        let mask = include_str!("shaders/shadow_mask.wgsl");
        assert!(mask.contains("@group(1) @binding(13) var t_caustics_offset: texture_2d<f32>;"),
            "shadow_mask.wgsl が影の屈折オフセットを宣言していない");
        // フル解像度オフセットを G-Buffer/深度と同一の写像（mask_full_pix の pix）で読むこと。
        assert!(mask.contains("let refract_offset = textureLoad(t_caustics_offset, pix, 0).xy;"),
            "マスク生成が半解像度と食い違う座標でオフセットを読んでいる疑い");
        assert!(mask.contains("ray_origin = world_pos + vec3<f32>(refract_offset.x, 0.0, refract_offset.y);"),
            "マスク生成のレイ原点がオフセットされていない");

        // ⑤ 中立値: **加算項なので 0 が中立**。フォワード採取点も明示的に 0 を書く。
        let gather = include_str!("shaders/surface_gather.wgsl");
        assert!(gather.contains("s.shadow_refract_offset = vec2<f32>(0.0, 0.0);"),
            "surface_gather の影オフセット中立値（0）設定が消えている");
    }

    /// RT ソフト影マスクの「深度考慮アップサンプル（joint bilateral）」の深度重みの境界性質を検証する。
    ///
    /// deferred_lighting.wgsl は半解像度マスクをフル解像度へ拡大する際、周囲 4 テクセルを
    /// バイリニア重み × 深度類似度重み `exp(-|d_half - d_full| / tol)` で正規化平均する
    /// （tol = FRAC × max(|d_full|, MIN)）。深度が大きく食い違うテクセルの重みが確実に
    /// フォールバック閾値 UPSAMPLE_MIN_WEIGHT を下回ること（＝別深度面を混ぜず、フチのにじみが
    /// 消えること）を、WGSL から抽出した実定数で数値検証する回帰ガード。
    #[test]
    fn shadow_mask_upsample_depth_weight_boundaries() {
        let src = include_str!("shaders/deferred_lighting.wgsl");
        // `const NAME: f32 = <値>;` を抽出（reflection.rs のフェード定数テストと同じ流儀）。
        let parse_f32 = |name: &str| -> f32 {
            let line = src.lines().map(str::trim)
                .find(|l| l.starts_with(&format!("const {name}")))
                .unwrap_or_else(|| panic!("deferred_lighting.wgsl に const {name} が見つかりません"));
            let rhs = line.split('=').nth(1).unwrap();
            let num: String = rhs.trim().chars()
                .take_while(|c| c.is_ascii_digit() || *c == '.' || *c == '-' || *c == 'e' || *c == 'E')
                .collect();
            num.parse::<f32>().unwrap_or_else(|_| panic!("const {name} を f32 解釈できません: {num:?}"))
        };
        let frac       = parse_f32("SHADOW_MASK_DEPTH_TOLERANCE_FRAC");
        let min_tol     = parse_f32("SHADOW_MASK_DEPTH_TOLERANCE_MIN");
        let min_weight = parse_f32("SHADOW_MASK_UPSAMPLE_MIN_WEIGHT");

        // 定数の健全性。
        assert!(frac > 0.0 && frac < 1.0, "TOLERANCE_FRAC({frac}) は (0,1)");
        assert!(min_tol > 0.0, "TOLERANCE_MIN({min_tol}) > 0（極近距離の 0 割れ防止）");
        assert!(min_weight > 0.0 && min_weight < 0.01, "UPSAMPLE_MIN_WEIGHT({min_weight}) は小さい正値");

        // WGSL と同一の深度重み。tol は基準深度に対する相対許容幅。
        let depth_weight = |d_full: f32, d_half: f32| -> f32 {
            let tol = frac * f32::max(d_full.abs(), min_tol);
            (-(d_half - d_full).abs() / tol).exp()
        };

        // 同一深度 → 重み 1（バイリニアに一致＝平坦面で従来挙動を保つ）。
        assert_eq!(depth_weight(10.0, 10.0), 1.0, "深度一致で重みは 1");
        // 1×tol の差 → exp(-1)≈0.368（近傍は緩やかに減衰）。
        let one_tol = 10.0 + frac * 10.0;
        assert!((depth_weight(10.0, one_tol) - (-1.0f32).exp()).abs() < 1e-5, "diff=tol で exp(-1)");
        // 大きな深度不連続（カーテンと床のフチ相当, 10×tol）→ 単独テクセルでも重みが
        // フォールバック閾値を下回る＝別深度面は必ず棄却される（にじみ根絶の要）。
        let far = 10.0 + 10.0 * (frac * 10.0);
        assert!(depth_weight(10.0, far) < min_weight,
            "深度不連続テクセルの重み({}) < UPSAMPLE_MIN_WEIGHT({min_weight})", depth_weight(10.0, far));
    }

    // ========================================================
    //  シェーディング契約 v1（L3-a）の回帰ガード
    // ========================================================

    /// シェーディング契約のバージョンが 1 であること。
    ///
    /// アセット側は先頭コメントに `// @shading_contract 1` を書く規約であり、この数値と
    /// WGSL の `SHADING_CONTRACT_VERSION` が食い違うとローダの照合が意味を失う。
    /// WGSL ソースから定数行を直接パースして固定する（上の
    /// shadow_mask_upsample_depth_weight_boundaries と同じ流儀）。
    #[test]
    fn shading_contract_version_is_one() {
        let src = include_str!("shaders/shading_contract.wgsl");
        let line = src.lines().map(str::trim)
            .find(|l| l.starts_with("const SHADING_CONTRACT_VERSION"))
            .expect("shading_contract.wgsl に const SHADING_CONTRACT_VERSION が見つかりません");
        let rhs = line.split('=').nth(1).expect("= の右辺がありません");
        let num: String = rhs.trim().chars().take_while(|c| c.is_ascii_digit()).collect();
        let version: u32 = num.parse()
            .unwrap_or_else(|_| panic!("SHADING_CONTRACT_VERSION を u32 解釈できません: {num:?}"));
        assert_eq!(version, 1, "契約バージョンは 1（上げるときはアセット側の @shading_contract も同時に更新）");
        // 契約の宣言規約そのものもコメントとして残っていること（規約が消えると照合が形骸化する）。
        assert!(src.contains("@shading_contract 1"),
            "shading_contract.wgsl にアセット側の宣言規約（// @shading_contract 1）の説明が必要");
    }

    /// `shade_light` の定義が **shading_contract.wgsl にちょうど 1 つだけ**存在し、
    /// 移設元の lighting_eval.wgsl には**残っていない**こと。
    ///
    /// 両方に定義が残ると連結時に「関数の二重定義」で全パイプラインが落ちる。naga 検証テスト
    /// でも落ちるが、失敗メッセージが分かりにくいため、原因を名指しする番人としてここに置く。
    #[test]
    fn shade_light_is_defined_exactly_once_in_the_contract() {
        let contract = include_str!("shaders/shading_contract.wgsl");
        let eval     = include_str!("shaders/lighting_eval.wgsl");
        // 定義の目印は行頭の `fn shade_light(`（呼び出しは必ず前置きがあるので行頭には来ない）。
        let count_defs = |src: &str| src.lines().filter(|l| l.starts_with("fn shade_light(")).count();
        assert_eq!(count_defs(contract), 1, "shade_light の定義は shading_contract.wgsl に 1 つだけ");
        assert_eq!(count_defs(eval), 0, "shade_light の定義が lighting_eval.wgsl に残っている（二重定義になる）");
        // 既定ディスパッチ（shade_surface）も 1 本だけ。アセット生成版と排他であることの土台。
        let dispatch = include_str!("shaders/shading_dispatch.wgsl");
        assert_eq!(
            dispatch.lines().filter(|l| l.starts_with("fn shade_surface(")).count(), 1,
            "shade_surface の定義は shading_dispatch.wgsl に 1 つだけ",
        );
    }

    /// シェーディングモデル ID の値域（`SHADING_MODEL_MASK` 由来）を固定する。
    ///
    /// ID は G-Buffer RT3.a に SHADING_MODEL_BITS ビットで詰められるため 0..=3 の 4 値しかない。
    /// 0 はエンジン標準 PBR で予約済み（アセットでは上書き不可）なので、**アセットが定義できる
    /// モデル ID は 1..=3 の 3 枠**である。この前提でアセットのローダ・switch 生成を書くため、
    /// ビット幅を広げたときに必ずここが落ちるようにしておく。
    #[test]
    fn user_definable_shading_model_ids_are_one_to_three() {
        use crate::engine::core::renderer::surface_id::{
            SHADING_MODEL_DEFAULT_PBR, SHADING_MODEL_MASK,
        };
        assert_eq!(SHADING_MODEL_DEFAULT_PBR, 0, "モデル 0 はエンジン標準 PBR で予約");
        assert_eq!(SHADING_MODEL_MASK, 3, "モデル ID は 2bit＝0..3");
        // アセットが名乗れる ID の集合（0 を除いた残り）。
        let user_ids: Vec<u8> = (0..=SHADING_MODEL_MASK).filter(|id| *id != SHADING_MODEL_DEFAULT_PBR).collect();
        assert_eq!(user_ids, vec![1, 2, 3], "アセット定義可能なモデル ID は 1..3 の 3 枠");
    }
}
