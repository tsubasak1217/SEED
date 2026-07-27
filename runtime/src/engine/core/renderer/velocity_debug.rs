// ============================================================
//  velocity_debug.rs — 速度バッファ（モーションベクタ）のデバッグ可視化パイプライン
//
//  ## 役割（単一責任）
//  「速度 RT（G-Buffer の RT4）を疑似カラーで HDR シーンへ上書きする」フルスクリーン
//  パイプラインと、その入力 BindGroup 生成だけを持つ。
//  実行するかどうかの判断（環境変数ゲート）とパスの記録は frame_renderer の責務。
//
//  ## 有効化
//  環境変数 `SEED_DEBUG_VELOCITY=1` を設定して起動する。未設定時はパイプライン自体を
//  構築せず（`Option::None`）、GPU 資源も描画コストもゼロになる。
//
//  ## 見方
//  疑似カラーの規約は `shaders/velocity_debug.wgsl` の冒頭コメントが正典
//  （灰色＝速度 0 / 赤・シアン＝水平 / 緑・マゼンタ＝垂直 / 青＝飽和）。
// ============================================================

use super::pipeline::get_shader_source;

/// 速度可視化を有効にする環境変数名。
pub const VELOCITY_DEBUG_ENV: &str = "SEED_DEBUG_VELOCITY";

/// `SEED_DEBUG_VELOCITY` が有効か（プロセス起動時に一度だけ評価する）。
///
/// 判定規約は本リポジトリの他のデバッグフラグ（`SEED_PERF_LOG` 等）と同じく
/// 「値が "0" でも "" でもなければ有効」。
pub static VELOCITY_DEBUG_ENABLED: std::sync::LazyLock<bool> = std::sync::LazyLock::new(|| {
    std::env::var(VELOCITY_DEBUG_ENV)
        .map(|v| !v.is_empty() && v != "0")
        .unwrap_or(false)
});

/// 速度可視化のフルスクリーンパイプライン一式。
pub struct VelocityDebugPipeline {
    /// フルスクリーン三角形 → 疑似カラー出力のパイプライン。
    pub pipeline: wgpu::RenderPipeline,
    /// group0: 速度テクスチャ 1 枚だけのレイアウト（サンプラー無し＝textureLoad 直読み）。
    pub bgl:      wgpu::BindGroupLayout,
}

impl VelocityDebugPipeline {
    /// 可視化パイプラインを構築する。
    ///
    /// - `out_format`: 出力先（シーン HDR, `Rgba16Float`）。
    ///   深度は持たない（`depth_stencil: None`）。ライティング結果の上へ無条件に
    ///   全画素を上書きするデバッグ表示なので、深度テストは不要かつ有害。
    pub fn new(
        device:     &wgpu::Device,
        out_format: wgpu::TextureFormat,
        cache:      Option<&wgpu::PipelineCache>,
    ) -> Self {
        const LABEL: &str = "velocity_debug";

        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some(LABEL),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding:    0,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Texture {
                    // 速度 RT は Rg16Float（フィルタ可能）だが textureLoad しか使わない。
                    sample_type:    wgpu::TextureSampleType::Float { filterable: true },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled:   false,
                },
                count: None,
            }],
        });

        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some(LABEL),
            source: wgpu::ShaderSource::Wgsl(get_shader_source("velocity_debug.wgsl").into()),
        });

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some(LABEL),
            bind_group_layouts:   &[&bgl],
            push_constant_ranges: &[],
        });

        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some(LABEL),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module:              &shader,
                entry_point:         Some("vs_fullscreen"),
                buffers:             &[],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module:      &shader,
                entry_point: Some("fs_velocity_debug"),
                targets:     &[Some(wgpu::ColorTargetState {
                    format:     out_format,
                    blend:      None,
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive:     wgpu::PrimitiveState::default(),
            depth_stencil: None,
            multisample:   wgpu::MultisampleState::default(),
            multiview:     None,
            cache,
        });

        Self { pipeline, bgl }
    }

    /// 速度テクスチャビューから group0 の BindGroup を生成する。
    /// 速度 RT はウィンドウリサイズで作り直されるため、毎フレーム呼んでよい。
    pub fn create_bind_group(
        &self,
        device:        &wgpu::Device,
        velocity_view: &wgpu::TextureView,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Velocity Debug BG"),
            layout:  &self.bgl,
            entries: &[wgpu::BindGroupEntry {
                binding:  0,
                resource: wgpu::BindingResource::TextureView(velocity_view),
            }],
        })
    }
}

// ============================================================
//  WGSL 静的検証（naga parse + validate）
// ============================================================
#[cfg(test)]
mod tests {
    /// 可視化 WGSL（単体でモジュールになる）を naga で parse + validate する。
    #[test]
    fn velocity_debug_shader_parses_and_validates() {
        let src = include_str!("shaders/velocity_debug.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("[velocity_debug] WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("[velocity_debug] WGSL validate 失敗: {e:?}"));
    }

    /// リゾルバ登録漏れの検出（pipeline.rs::get_shader_source）。
    #[test]
    fn velocity_debug_shader_is_registered_in_resolver() {
        assert_eq!(
            super::get_shader_source("velocity_debug.wgsl"),
            include_str!("shaders/velocity_debug.wgsl"),
            "pipeline.rs::get_shader_source に velocity_debug.wgsl が登録されていない"
        );
    }
}
