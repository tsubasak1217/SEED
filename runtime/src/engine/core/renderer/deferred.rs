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
}

impl DeferredLightingPipelines {
    /// フルスクリーン・ライティングパイプラインを構築する。
    ///
    /// - `out_format` : 出力先（シーン HDR, Rgba16Float）。
    /// - `df`         : 深度フォーマット。no_depth=true のため実際には未使用だが、
    ///                  RenderPipelineBuilder::new のシグネチャを満たすためのダミー値。
    pub fn new(
        device:     &wgpu::Device,
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
        let rt = if super::rt_shadow::rt_shadows_supported() {
            let (rt_pipeline, _bgls_rt) = RenderPipelineBuilder::new(
                device, include_str!("pipelines/deferred_lighting_rt.toml"), out_format, df,
            )
            .with_label("deferred_lighting_rt")
            .with_cache(cache)
            .build(get_shader_source);
            Some(rt_pipeline)
        } else {
            None
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

        Self { pipeline, rt, camera_bgl, gbuffer_bgl, gap_bgl2, gap_bgl3, empty_bg2, empty_bg3, gbuffer_sampler }
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
        let light_ev = include_str!("shaders/lighting_eval.wgsl");
        let deferred = include_str!("shaders/deferred_lighting.wgsl");

        let src = [cluster, pbr_c, ddgi_c, light_c, shadow, rt_off, surf, light_ev, deferred].join("\n");
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
        let surf     = include_str!("shaders/surface.wgsl");
        let light_ev = include_str!("shaders/lighting_eval.wgsl");
        let deferred = include_str!("shaders/deferred_lighting.wgsl");

        let src = [cluster, pbr_c, ddgi_c, light_c, shadow, rt_on, surf, light_ev, deferred].join("\n");
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
}
