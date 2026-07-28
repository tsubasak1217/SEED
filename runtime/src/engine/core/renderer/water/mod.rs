// ============================================================
//  renderer/water/mod.rs — 水面描画パス（Phase W1）
//
//  ## 役割（単一責任）
//  エンジン層が解決した `ResolvedWaterVolume` の配列を受け取り、
//  「1 ドローで全水面クアッドを描く」ためのリソース（ストレージバッファ・
//  屈折背景グラブ・BindGroup・パイプライン）を管理して描画する。
//
//  ## メッシュを持たない
//  水面は常に軸平行の矩形なので、頂点バッファは一切持たない。
//  `draw(0..6, 0..N)` の 1 ドローで N 個の水ボリュームを描き、
//  頂点位置は `vertex_index`、パラメータは `instance_index` からシェーダが引く。
//
//  ## 深度
//  本パスは深度アタッチメントを持たず（TOML `no_depth = true`）、
//  共有深度の DepthOnly ビューを **サンプルテクスチャとして** group1 に受け取る。
//  遮蔽判定（手動深度テスト）と水の厚み復元をシェーダ内で行う。
//  詳細は `shaders/water_surface.wgsl` の冒頭コメントを参照。
//
//  ## 屈折の背景（自前グラブ）
//  `RefractPyramid` はブラーミップ鎖まで作るため水面には過剰。
//  ここでは「シーン HDR をフル解像度 1 ミップへコピーするだけ」の専用テクスチャを持つ。
//  **水ボリュームが 1 つも無いフレームではテクスチャ確保もコピーも行わない**（コスト 0）。
// ============================================================

pub mod params;

pub use params::{WaterParams, WATER_MAX_VOLUMES};

use crate::engine::water::ResolvedWaterVolume;
use super::{DEPTH_FORMAT, pipeline_config::RenderPipelineBuilder};

/// 水面描画に必要な GPU リソース一式と描画手続き。
pub struct WaterRenderer {
    /// 水面パイプライン（頂点バッファ無し・深度アタッチメント無し）。
    pipeline: wgpu::RenderPipeline,
    /// group1（パラメータ配列＋背景＋サンプラー＋深度）の BindGroupLayout。
    params_bgl: wgpu::BindGroupLayout,
    /// 屈折背景サンプラー（線形・ClampToEdge）。
    sampler: wgpu::Sampler,
    /// 屈折背景グラブのフォーマット（シーン HDR と一致必須。copy_texture_to_texture の要件）。
    hdr_format: wgpu::TextureFormat,

    /// 水パラメータのストレージバッファ（必要に応じて容量を拡張する）。
    params_buf: Option<wgpu::Buffer>,
    /// `params_buf` の容量（要素数）。
    params_capacity: usize,

    /// 屈折背景グラブ（シーン HDR のフル解像度 1 ミップコピー）。
    grab_tex: Option<wgpu::Texture>,
    grab_view: Option<wgpu::TextureView>,
    /// グラブの現在サイズ（サーフェスサイズ追従）。
    grab_width: u32,
    grab_height: u32,

    /// このフレームの group1 BindGroup（深度ビューがフレーム依存のため毎フレーム作り直す）。
    frame_bind_group: Option<wgpu::BindGroup>,
    /// このフレームで描くインスタンス数（= 水ボリューム数、上限クランプ後）。
    instance_count: u32,
    /// 上限超過の警告を出したか（毎フレームのログ氾濫を防ぐため 1 回だけ出す）。
    warned_overflow: bool,
}

impl WaterRenderer {
    /// パイプラインを構築する（テクスチャ・バッファは `prepare` で遅延確保）。
    ///
    /// `hdr_format` はシーン HDR のフォーマット（`HDR_FORMAT`）。
    /// カラーターゲットと屈折背景グラブの両方に使う。
    pub fn new(
        device:     &wgpu::Device,
        hdr_format: wgpu::TextureFormat,
        cache:      Option<&wgpu::PipelineCache>,
    ) -> Self {
        // 自己完結のシェーダリゾルバ（連結は water_surface.wgsl 1 本のみ。
        // shader_common.wgsl は連結しない＝マテリアル group を要求しないため）。
        let (pipeline, mut bgls) = RenderPipelineBuilder::new(
            device,
            include_str!("../pipelines/water_surface.toml"),
            hdr_format,
            DEPTH_FORMAT,
        )
        .with_label("Water Surface")
        .with_cache(cache)
        .build(|name: &str| -> &'static str {
            match name {
                "water_surface.wgsl" => include_str!("../shaders/water_surface.wgsl"),
                other => panic!("water: unknown shader source: {other}"),
            }
        });
        // bgls は group 番号順（0 = カメラ, 1 = 水リソース）。group1 だけを保持する。
        assert!(bgls.len() >= 2, "water_surface.wgsl は group0(カメラ)/group1(水リソース) を宣言すること");
        let params_bgl = bgls.remove(1);

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("Water Scene Grab Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Linear,
            min_filter:     wgpu::FilterMode::Linear,
            mipmap_filter:  wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        Self {
            pipeline,
            params_bgl,
            sampler,
            hdr_format,
            params_buf:       None,
            params_capacity:  0,
            grab_tex:         None,
            grab_view:        None,
            grab_width:       0,
            grab_height:      0,
            frame_bind_group: None,
            instance_count:   0,
            warned_overflow:  false,
        }
    }

    /// このフレームの水面描画を準備する。
    ///
    /// 戻り値 `false` は「描くものが無い」の意味で、呼び出し側は
    /// グラブコピーも水面パスもスキップすること（リソース確保も行われない＝コスト 0）。
    ///
    /// `depth_view` は共有深度の **DepthOnly ビュー**（`RenderFrame::depth_only_view_r`）。
    /// 本パスは深度アタッチメントを持たず、これをサンプルして手動深度テストを行うため、
    /// BindGroup 生成に必要（フレーム依存なので毎フレーム作り直す）。
    #[allow(clippy::too_many_arguments)]
    pub fn prepare(
        &mut self,
        device:     &wgpu::Device,
        queue:      &wgpu::Queue,
        volumes:    &[ResolvedWaterVolume],
        camera_pos: [f32; 3],
        width:      u32,
        height:     u32,
        depth_view: &wgpu::TextureView,
    ) -> bool {
        self.instance_count   = 0;
        self.frame_bind_group = None;
        if volumes.is_empty() {
            return false;
        }

        // 上限超過は切り捨て（警告は 1 回だけ）。
        let count = volumes.len().min(WATER_MAX_VOLUMES);
        if volumes.len() > WATER_MAX_VOLUMES && !self.warned_overflow {
            self.warned_overflow = true;
            eprintln!(
                "[water] 水ボリュームが上限 {} 個を超えたため {} 個を切り捨てます",
                WATER_MAX_VOLUMES,
                volumes.len() - WATER_MAX_VOLUMES,
            );
        }

        // ── パラメータ配列を作ってアップロード ──
        let gpu: Vec<WaterParams> = volumes[..count]
            .iter()
            .map(|v| WaterParams::from_resolved(v, camera_pos))
            .collect();

        if self.params_buf.is_none() || self.params_capacity < count {
            let capacity = count.max(1);
            self.params_buf = Some(device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("Water Params Storage"),
                size:  (capacity * std::mem::size_of::<WaterParams>()) as u64,
                usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            }));
            self.params_capacity = capacity;
        }
        let params_buf = self.params_buf.as_ref().expect("water: params buffer 未確保");
        queue.write_buffer(params_buf, 0, bytemuck::cast_slice(&gpu));

        // ── 屈折背景グラブをサーフェスサイズへ追従確保 ──
        let w = width.max(1);
        let h = height.max(1);
        if self.grab_tex.is_none() || self.grab_width != w || self.grab_height != h {
            let tex = device.create_texture(&wgpu::TextureDescriptor {
                label:           Some("Water Scene Grab"),
                size:            wgpu::Extent3d { width: w, height: h, depth_or_array_layers: 1 },
                mip_level_count: 1,
                sample_count:    1,
                dimension:       wgpu::TextureDimension::D2,
                format:          self.hdr_format,
                // サンプル（屈折の背景）＋ シーン HDR からのコピー先。
                usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
                view_formats:    &[],
            });
            self.grab_view   = Some(tex.create_view(&wgpu::TextureViewDescriptor::default()));
            self.grab_tex    = Some(tex);
            self.grab_width  = w;
            self.grab_height = h;
        }
        let grab_view = self.grab_view.as_ref().expect("water: grab view 未確保");

        // ── group1 BindGroup（深度ビューがフレーム依存のため毎フレーム生成）──
        self.frame_bind_group = Some(device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Water Resources BG"),
            layout:  &self.params_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: params_buf.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::TextureView(grab_view) },
                wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::Sampler(&self.sampler) },
                wgpu::BindGroupEntry { binding: 3, resource: wgpu::BindingResource::TextureView(depth_view) },
            ],
        }));
        self.instance_count = count as u32;
        true
    }

    /// シーン HDR を屈折背景グラブへコピーする（水面パスの **直前・レンダーパス外**で呼ぶ）。
    ///
    /// メインパス・WBOIT 合成の後に呼ぶことで、スカイボックスも既存半透明も
    /// 屈折の背景に含まれる。`prepare` が `true` を返したフレームでのみ呼ぶこと。
    pub fn record_grab(&self, encoder: &mut wgpu::CommandEncoder, scene_hdr_tex: &wgpu::Texture) {
        let Some(tex) = self.grab_tex.as_ref() else { return; };
        encoder.copy_texture_to_texture(
            wgpu::ImageCopyTexture {
                texture:   scene_hdr_tex,
                mip_level: 0,
                origin:    wgpu::Origin3d::ZERO,
                aspect:    wgpu::TextureAspect::All,
            },
            wgpu::ImageCopyTexture {
                texture:   tex,
                mip_level: 0,
                origin:    wgpu::Origin3d::ZERO,
                aspect:    wgpu::TextureAspect::All,
            },
            wgpu::Extent3d {
                width:                self.grab_width,
                height:               self.grab_height,
                depth_or_array_layers: 1,
            },
        );
    }

    /// 水面パス内で全水ボリュームを 1 ドローで描く。
    /// `prepare` が `true` を返したフレームでのみ呼ぶこと。
    pub fn draw<'p>(&'p self, pass: &mut wgpu::RenderPass<'p>, camera_bg: &'p wgpu::BindGroup) {
        let Some(bg) = self.frame_bind_group.as_ref() else { return; };
        if self.instance_count == 0 {
            return;
        }
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, camera_bg, &[]);
        pass.set_bind_group(1, bg, &[]);
        // 頂点バッファ無し: 6 頂点 × N インスタンス（= 水ボリューム数）。
        pass.draw(0..6, 0..self.instance_count);
    }
}

// ============================================================
//  テスト（WGSL 静的検証）
// ============================================================
#[cfg(test)]
mod tests {
    /// 水面シェーダを naga で parse + validate する（GPU デバイス不要）。
    /// TOML の shader_sources は water_surface.wgsl 単体なので、連結順の考慮は不要。
    #[test]
    fn water_surface_shader_parses_and_validates() {
        let src = include_str!("../shaders/water_surface.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("water_surface.wgsl WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("water_surface.wgsl WGSL validate 失敗: {e:?}"));
    }

    /// TOML のエントリポイント名がシェーダの実装と一致すること
    /// （不一致はパイプライン生成時のランタイム失敗になるため、静的に照合しておく）。
    #[test]
    fn water_surface_toml_entries_match_shader() {
        let toml_src = include_str!("../pipelines/water_surface.toml");
        let wgsl_src = include_str!("../shaders/water_surface.wgsl");
        assert!(toml_src.contains("vertex_entry    = \"vs_water\""));
        assert!(toml_src.contains("fragment_entry  = \"fs_water\""));
        assert!(wgsl_src.contains("fn vs_water("));
        assert!(wgsl_src.contains("fn fs_water("));
        // 深度アタッチメントを持たない前提（手動深度テスト）を TOML 側でも保証する。
        assert!(toml_src.contains("no_depth        = true"));
    }
}
