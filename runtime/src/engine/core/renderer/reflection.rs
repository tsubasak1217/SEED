// ============================================================
//  reflection.rs — 反射（SSR / RT）フルスクリーンパイプライン一式（Phase D6）
//
//  ## 役割（単一責任）
//  Deferred（G-Buffer）有効時のみ走る独立フルスクリーン反射パスのパイプライン・
//  BindGroupLayout・共有リソースを持つ。パス順は frame_renderer が制御する:
//    不透明 Deferred ライティング（scene_hdr 完成・不透明のみ）
//      → 反射パス（G-Buffer＋scene_hdr 入力 → RT_REFLECTION へ反射色）
//      → 合成パス（RT_REFLECTION を scene_hdr へ Additive 加算）
//      → メインフォワード再開（skybox/半透明）…（既存不変）
//
//  ## HDR 読み書き分離
//  反射パスは専用 RT_REFLECTION（Rgba16Float, Clear=0）へ出力し、scene_hdr は
//  サンプル「入力」として読む（描画先が別テクスチャ＝読み書き競合なし＝SSR の
//  自己参照制約を回避）。合成は別パスで Additive(One/One)+LoadOp::Load を使う。
//
//  ## BindGroup グループ割当（max_bind_groups=5 厳守）
//    SSR: group0=camera / group1=G-Buffer / group2=input(params+hdr) / group3=GI        （4 groups）
//    RT : group0=camera / group1=G-Buffer / group2=input / group3=RTデータ / group4=GI  （5 groups＝上限）
//    composite: group0=反射RT+sampler（1 group）
//  group0/1 は DeferredLightingPipelines の camera_bgl/gbuffer_bgl を借用して等価性を担保する
//  （frame_renderer は camera_buf.bind_group と deferred::create_gbuffer_bind_group をそのまま流用）。
// ============================================================

use super::deferred::DeferredLightingPipelines;
use super::ddgi::GiResources;
use super::pipeline::get_shader_source;

// ─── 定数 ────────────────────────────────────────────────────

/// 反射専用 RT の RtPool 登録名（post::rt_pool の小文字スネーク流儀）。
pub const RT_REFLECTION_NAME: &str = "reflection";
/// 反射 RT のフォーマット（scene_hdr と同じ HDR。加算合成で HDR を保つため）。
pub const REFLECTION_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;
/// SSR 前フレーム参照履歴テクスチャのフォーマット。scene_hdr（HDR_FORMAT）と一致必須
/// （copy_texture_to_texture はコピー元／先で同一フォーマットを要求するため）。HDR_FORMAT は
/// Rgba16Float（renderer::HDR_FORMAT）＝フル解像度 1 枚で 1080p 約 16.6MB。
pub const REFLECTION_HISTORY_FORMAT: wgpu::TextureFormat = super::HDR_FORMAT;
/// 反射強度の既定（PostFxSettings.reflection_intensity の初期値）。
pub const DEFAULT_REFLECTION_INTENSITY: f32 = 1.0;

// ─── ReflectionParams（group2 binding0 uniform, 16B）───────────

/// 反射パスの数値パラメータ UBO（WGSL reflection_common.wgsl の ReflectionParams と一致）。
///
/// | offset | field     | size |
/// |--------|-----------|------|
/// |   0    | intensity |   4  |
/// |   4    | _pad0..2  |  12  |
/// 合計 16（uniform 最小アラインメント 16 に一致）。
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct ReflectionParams {
    /// 反射寄与の全体倍率。
    pub intensity: f32,
    pub _pad0:     f32,
    pub _pad1:     f32,
    pub _pad2:     f32,
}

impl ReflectionParams {
    /// intensity から UBO 値を作る。
    pub fn new(intensity: f32) -> Self {
        Self { intensity, _pad0: 0.0, _pad1: 0.0, _pad2: 0.0 }
    }
}

// ─── ReflectionPipelines ─────────────────────────────────────

/// 反射パイプライン一式（SSR 常時・RT は対応 GPU のみ・合成）。
pub struct ReflectionPipelines {
    /// SSR パイプライン（RAY_QUERY 不要・常に構築）。
    pub ssr:          wgpu::RenderPipeline,
    /// RT 反射パイプライン（RAY_QUERY 必須・RT 対応 GPU でのみ Some）。
    pub rt:           Option<wgpu::RenderPipeline>,
    /// 合成パイプライン（Additive One/One で RT_REFLECTION を scene_hdr へ加算）。
    pub composite:    wgpu::RenderPipeline,
    /// group2: 反射パラメータ＋シーン HDR 入力のレイアウト。
    pub input_bgl:    wgpu::BindGroupLayout,
    /// GI レイアウト（SSR は group3・RT は group4 で共用）。
    pub gi_bgl:       wgpu::BindGroupLayout,
    /// RT データレイアウト（RT の group3。lights/meta/tlas/albedo）。
    pub rt_data_bgl:  wgpu::BindGroupLayout,
    /// 合成の group0 レイアウト（反射 RT テクスチャ＋サンプラー）。
    pub composite_bgl: wgpu::BindGroupLayout,
    /// ReflectionParams UBO（毎フレーム intensity を書き込む）。
    pub params_buffer: wgpu::Buffer,
    /// 入力・GI・合成で共用するリニア clamp サンプラー。
    pub sampler:      wgpu::Sampler,
}

impl ReflectionPipelines {
    /// 反射パイプライン一式を構築する。
    ///
    /// - `deferred`   : group0/1（camera/gbuffer）の BGL を借用する（等価性担保）。
    /// - `out_format` : 出力先 HDR フォーマット（scene_hdr / RT_REFLECTION と同一）。
    pub fn new(
        device:     &wgpu::Device,
        deferred:   &DeferredLightingPipelines,
        out_format: wgpu::TextureFormat,
        cache:      Option<&wgpu::PipelineCache>,
    ) -> Self {
        let vis = wgpu::ShaderStages::FRAGMENT;
        let uniform = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding, visibility: vis,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Uniform,
                has_dynamic_offset: false, min_binding_size: None,
            },
            count: None,
        };
        let storage_ro = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding, visibility: vis,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Storage { read_only: true },
                has_dynamic_offset: false, min_binding_size: None,
            },
            count: None,
        };
        let tex = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding, visibility: vis,
            ty: wgpu::BindingType::Texture {
                sample_type: wgpu::TextureSampleType::Float { filterable: true },
                view_dimension: wgpu::TextureViewDimension::D2, multisampled: false,
            },
            count: None,
        };
        let samp = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding, visibility: vis,
            ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
            count: None,
        };

        // group2: input（params uniform + scene_hdr tex + sampler）。
        let input_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Reflection Input BGL (group2)"),
            entries: &[uniform(0), tex(1), samp(2)],
        });
        // GI（GiParams uniform + irr tex + vis tex + sampler）。SSR group3 / RT group4 共用。
        let gi_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Reflection GI BGL"),
            entries: &[uniform(0), tex(1), tex(2), samp(3)],
        });
        // RT データ（lights storage + meta uniform + tlas + albedo storage）。RT group3。
        let rt_data_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Reflection RT Data BGL (group3)"),
            entries: &[
                storage_ro(0),
                uniform(1),
                wgpu::BindGroupLayoutEntry {
                    binding: 2, visibility: vis,
                    ty: wgpu::BindingType::AccelerationStructure { vertex_return: false },
                    count: None,
                },
                storage_ro(3),
            ],
        });
        // composite の group0（反射 RT テクスチャ＋サンプラー）。
        let composite_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Reflection Composite BGL (group0)"),
            entries: &[tex(0), samp(1)],
        });

        // SSR パイプライン（group0..3, blend なし＝反射 RT を上書き）。
        let ssr = build_reflection_pipeline(
            device, cache,
            &[&deferred.camera_bgl, &deferred.gbuffer_bgl, &input_bgl, &gi_bgl],
            &["reflection_common.wgsl", "ddgi_common.wgsl", "reflection_ssr.wgsl"],
            "vs_fullscreen", "fs_ssr", "reflection_ssr", out_format, None,
        );

        // RT 反射パイプライン（group0..4, RT 対応 GPU のみ）。
        let rt = if super::rt_shadow::rt_shadows_supported() {
            Some(build_reflection_pipeline(
                device, cache,
                &[&deferred.camera_bgl, &deferred.gbuffer_bgl, &input_bgl, &rt_data_bgl, &gi_bgl],
                &["cluster_common.wgsl", "reflection_common.wgsl", "ddgi_common.wgsl", "reflection_rt.wgsl"],
                "vs_fullscreen", "fs_rt", "reflection_rt", out_format, None,
            ))
        } else {
            None
        };

        // 合成パイプライン（Additive One/One で scene_hdr へ加算）。
        let additive = wgpu::BlendState {
            color: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::One, dst_factor: wgpu::BlendFactor::One,
                operation: wgpu::BlendOperation::Add,
            },
            alpha: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::One, dst_factor: wgpu::BlendFactor::One,
                operation: wgpu::BlendOperation::Add,
            },
        };
        let composite = build_reflection_pipeline(
            device, cache, &[&composite_bgl],
            &["reflection_composite.wgsl"],
            "vs_composite", "fs_composite", "reflection_composite", out_format, Some(additive),
        );

        // 反射 RT・GI・合成で共用するリニア clamp サンプラー。
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("Reflection Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        // ReflectionParams UBO（初期値は既定強度で write_params にて毎フレーム更新）。
        let params_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Reflection Params UBO"),
            size:  std::mem::size_of::<ReflectionParams>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        Self { ssr, rt, composite, input_bgl, gi_bgl, rt_data_bgl, composite_bgl, params_buffer, sampler }
    }

    /// intensity を ReflectionParams UBO へ書き込む（毎フレーム反射パス直前に呼ぶ）。
    pub fn write_params(&self, queue: &wgpu::Queue, intensity: f32) {
        queue.write_buffer(&self.params_buffer, 0, bytemuck::bytes_of(&ReflectionParams::new(intensity)));
    }

    /// group2（input）の BindGroup を生成する（params UBO + scene_hdr + sampler）。
    pub fn create_input_bg(&self, device: &wgpu::Device, scene_hdr: &wgpu::TextureView) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Reflection Input BG"),
            layout: &self.input_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: self.params_buffer.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::TextureView(scene_hdr) },
                wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::Sampler(&self.sampler) },
            ],
        })
    }

    /// GI の BindGroup を生成する（SSR は group3・RT は group4 で使う）。
    pub fn create_gi_bg(&self, device: &wgpu::Device, gi: &GiResources) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Reflection GI BG"),
            layout: &self.gi_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: gi.params_buffer().as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::TextureView(gi.irradiance_view()) },
                wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::TextureView(gi.visibility_view()) },
                wgpu::BindGroupEntry { binding: 3, resource: wgpu::BindingResource::Sampler(&self.sampler) },
            ],
        })
    }

    /// RT データの BindGroup を生成する（RT の group3。lights/meta/tlas/albedo）。
    pub fn create_rt_data_bg(
        &self, device: &wgpu::Device,
        lights: &wgpu::Buffer, meta: &wgpu::Buffer,
        tlas: &wgpu::Tlas, albedo: &wgpu::Buffer,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Reflection RT Data BG"),
            layout: &self.rt_data_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: lights.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: meta.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::AccelerationStructure(tlas) },
                wgpu::BindGroupEntry { binding: 3, resource: albedo.as_entire_binding() },
            ],
        })
    }

    /// 合成の group0 BindGroup を生成する（反射 RT テクスチャ＋サンプラー）。
    pub fn create_composite_bg(&self, device: &wgpu::Device, refl_view: &wgpu::TextureView) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Reflection Composite BG"),
            layout: &self.composite_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(refl_view) },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::Sampler(&self.sampler) },
            ],
        })
    }
}

/// 反射フルスクリーンパイプラインを 1 本構築する（手動レイアウト。gbuffer.rs の
/// build_gbuffer_pipeline と同じ手法）。深度なし・単一カラーターゲット。
#[allow(clippy::too_many_arguments)]
fn build_reflection_pipeline(
    device:         &wgpu::Device,
    cache:          Option<&wgpu::PipelineCache>,
    bgls:           &[&wgpu::BindGroupLayout],
    shader_sources: &[&str],
    vs_entry:       &str,
    fs_entry:       &str,
    label:          &str,
    out_format:     wgpu::TextureFormat,
    blend:          Option<wgpu::BlendState>,
) -> wgpu::RenderPipeline {
    let combined: String = shader_sources.iter()
        .map(|n| get_shader_source(n))
        .collect::<Vec<_>>()
        .join("\n");
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label:  Some(label),
        source: wgpu::ShaderSource::Wgsl(combined.into()),
    });
    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label:                Some(label),
        bind_group_layouts:   bgls,
        push_constant_ranges: &[],
    });
    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label:  Some(label),
        layout: Some(&layout),
        vertex: wgpu::VertexState {
            module:              &shader,
            entry_point:         Some(vs_entry),
            buffers:             &[],
            compilation_options: Default::default(),
        },
        fragment: Some(wgpu::FragmentState {
            module:              &shader,
            entry_point:         Some(fs_entry),
            targets:             &[Some(wgpu::ColorTargetState {
                format:     out_format,
                blend,
                write_mask: wgpu::ColorWrites::ALL,
            })],
            compilation_options: Default::default(),
        }),
        primitive:     wgpu::PrimitiveState::default(),
        depth_stencil: None,
        multisample:   wgpu::MultisampleState::default(),
        multiview:     None,
        cache,
    })
}

// ─── ReflectionHistory（SSR 前フレーム参照履歴）─────────────────

/// SSR のヒット色サンプル元となる「前フレームの完成 HDR」を保持するフル解像度テクスチャ 1 枚。
///
/// ## なぜ必要か（1 フレーム遅延方式・SSGI と同じ実績ある流儀）
/// 反射パスは不透明ライティング直後（半透明・スカイボックス・パーティクル描画より前）の
/// scene_hdr を読むため、そのままでは後から描かれる半透明（ガラス等）が反射に映らない。
/// そこで **毎フレーム末尾（半透明等の描画後・ポスト処理前）の完成 HDR を本テクスチャへコピー**
/// しておき、次フレームの SSR がこれをサンプルする。これにより半透明も反射へ含められる。
///
/// ## 既知トレードオフ（コメント＋ロードマップに明記）
/// - 動くものは反射内で 1 フレーム遅れる（幾何は今フレーム深度・色は前フレーム）。
/// - 反射の中に反射（フィードバック）が生じるが、フレネル×粗さフェードで自然減衰し実用上問題にならない。
///
/// ## 履歴の有効性（`history_readable`）
/// 初回フレーム・リサイズ・無効→有効の直後は前フレームのコピーが無い（または解像度不一致）ため、
/// その 1 フレームは従来の「不透明のみ scene_hdr」へフォールバックする（SSGI の ssgi_readable と同流儀）。
pub struct ReflectionHistory {
    /// テクスチャ本体＋ビュー（未確保なら None）。
    tex: Option<(wgpu::Texture, wgpu::TextureView)>,
    /// 確保済み幅。
    w:   u32,
    /// 確保済み高さ。
    h:   u32,
}

impl Default for ReflectionHistory {
    fn default() -> Self { Self::new() }
}

impl ReflectionHistory {
    /// 空の（未確保の）履歴を生成する（device 不要・eager 構築可）。
    pub fn new() -> Self { Self { tex: None, w: 0, h: 0 } }

    /// フル解像度サイズへ追従する。既存が同サイズなら何もしない（SsgiTargets::ensure の流儀）。
    ///
    /// 戻り値: 再確保（初回 or サイズ変更）が起きたら true（＝前フレーム履歴が失われ 1 フレーム未収束）。
    /// usage は COPY_DST（scene_hdr からのコピー先）＋ TEXTURE_BINDING（SSR でのサンプル）。
    /// RENDER_ATTACHMENT は不要（描画先にはしない）。
    pub fn ensure(&mut self, device: &wgpu::Device, w: u32, h: u32) -> bool {
        let w = w.max(1);
        let h = h.max(1);
        if self.tex.is_some() && self.w == w && self.h == h {
            return false;
        }
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Reflection History (SSR prev-frame HDR)"),
            size:  wgpu::Extent3d { width: w, height: h, depth_or_array_layers: 1 },
            mip_level_count: 1, sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format:    REFLECTION_HISTORY_FORMAT,
            usage:     wgpu::TextureUsages::COPY_DST | wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        self.tex = Some((texture, view));
        self.w = w; self.h = h;
        true
    }

    /// 履歴ビュー（SSR の group2 binding1 に渡す。ensure 済みであること）。
    pub fn view(&self) -> &wgpu::TextureView {
        &self.tex.as_ref().expect("ReflectionHistory: ensure 未実行").1
    }

    /// 履歴テクスチャ本体（scene_hdr → 本テクスチャの copy_texture_to_texture コピー先。ensure 済みであること）。
    pub fn texture(&self) -> &wgpu::Texture {
        &self.tex.as_ref().expect("ReflectionHistory: ensure 未実行").0
    }
}

/// このフレームで SSR が履歴（前フレーム完成 HDR）を「読める」かを判定する純関数。
///
/// SSGI の `ssgi_readable = ssgi_active && ssgi_warmed && !ssgi_reallocated` と同一の三条件:
/// - `active`      : 今フレーム反射パスを走らせる（reflection_effective != Off）。
/// - `warmed`      : 前フレームも反射 active でフレーム末に履歴コピーを済ませた。
/// - `reallocated` : 今フレーム履歴を再確保した（初回 or リサイズ）＝前フレーム内容が失われた。
///
/// 全て満たすときのみ true。false のときは従来の「不透明のみ scene_hdr」へフォールバックする。
pub fn history_readable(active: bool, warmed: bool, reallocated: bool) -> bool {
    active && warmed && !reallocated
}

// ============================================================
//  WGSL 静的検証（naga parse + validate）＋レイアウト照合
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 履歴有効判定 `history_readable` の真理値（初回/リサイズ/無効化の扱いを固定）。
    ///
    /// SSGI の ssgi_readable と同じ三条件（active && warmed && !reallocated）を検証する。
    /// 実機の「床の反射にガラスが映る」経路はこの判定が true になって初めて成立するため回帰ガードとして重要。
    #[test]
    fn history_readable_truth_table() {
        // 収束済みの通常フレームのみ true。
        assert!(history_readable(true, true, false), "active＋warmed＋未再確保 → 読める");

        // 反射 Off（非 active）は常に false（履歴を参照しない）。
        assert!(!history_readable(false, true, false), "非 active は読めない");
        assert!(!history_readable(false, false, false), "非 active は読めない（warmed 無関係）");

        // 前フレームで履歴コピー未実施（初回・無効→有効の直後）は false。
        assert!(!history_readable(true, false, false), "warmed でない（初回/有効化直後）は読めない");

        // 今フレーム再確保（初回・リサイズ）は前フレーム内容が失われるため false。
        assert!(!history_readable(true, true, true), "再確保（リサイズ）フレームは読めない");
        assert!(!history_readable(true, false, true), "warmed でない かつ 再確保 は読めない");
    }

    /// 履歴フォーマットは scene_hdr（HDR_FORMAT）と一致すること。
    /// copy_texture_to_texture はコピー元／先で同一フォーマットを要求するため不一致だとパニックする。
    #[test]
    fn history_format_matches_scene_hdr() {
        assert_eq!(REFLECTION_HISTORY_FORMAT, super::super::HDR_FORMAT,
            "履歴フォーマットは scene_hdr（HDR_FORMAT）と一致必須（コピー要件）");
    }


    /// SSR 連結（RAY_QUERY 不要）を naga で parse + validate する。
    #[test]
    fn reflection_ssr_shader_parses() {
        let refl_c = include_str!("shaders/reflection_common.wgsl");
        let ddgi_c = include_str!("shaders/ddgi_common.wgsl");
        let ssr    = include_str!("shaders/reflection_ssr.wgsl");
        let src = [refl_c, ddgi_c, ssr].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[reflection_ssr] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(), naga::valid::Capabilities::empty());
        v.validate(&module).unwrap_or_else(|e| panic!("[reflection_ssr] validate 失敗: {e:?}"));
    }

    /// RT 反射連結（RAY_QUERY 必須）を naga で parse + validate する。
    #[test]
    fn reflection_rt_shader_parses() {
        let cluster = include_str!("shaders/cluster_common.wgsl");
        let refl_c  = include_str!("shaders/reflection_common.wgsl");
        let ddgi_c  = include_str!("shaders/ddgi_common.wgsl");
        let rt      = include_str!("shaders/reflection_rt.wgsl");
        let src = [cluster, refl_c, ddgi_c, rt].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[reflection_rt] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(), naga::valid::Capabilities::RAY_QUERY);
        v.validate(&module).unwrap_or_else(|e| panic!("[reflection_rt] validate 失敗: {e:?}"));
    }

    /// 合成連結（自前完結）を naga で parse + validate する。
    #[test]
    fn reflection_composite_shader_parses() {
        let comp = include_str!("shaders/reflection_composite.wgsl");
        let module = naga::front::wgsl::parse_str(comp)
            .unwrap_or_else(|e| panic!("[reflection_composite] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(), naga::valid::Capabilities::empty());
        v.validate(&module).unwrap_or_else(|e| panic!("[reflection_composite] validate 失敗: {e:?}"));
    }

    /// ReflectionParams は 16 バイト（WGSL reflection_common.wgsl の ReflectionParams と一致）。
    #[test]
    fn reflection_params_is_16_bytes() {
        assert_eq!(std::mem::size_of::<ReflectionParams>(), 16);
    }

    /// RT 反射の GpuLightR / LightMetaR ミラーが 112B / 32B であること（naga 計算）。
    /// lighting.rs の GpuLight/LightMeta と同一ストライドでなければ storage/uniform 読みが化ける。
    #[test]
    fn reflection_rt_mirror_layouts_match() {
        let cluster = include_str!("shaders/cluster_common.wgsl");
        let refl_c  = include_str!("shaders/reflection_common.wgsl");
        let ddgi_c  = include_str!("shaders/ddgi_common.wgsl");
        let rt      = include_str!("shaders/reflection_rt.wgsl");
        let src = [cluster, refl_c, ddgi_c, rt].join("\n");
        let module = naga::front::wgsl::parse_str(&src).expect("RT 反射連結の parse に失敗");
        let mut layouter = naga::proc::Layouter::default();
        layouter.update(module.to_ctx()).expect("naga Layouter 計算に失敗");
        let size_of = |name: &str| -> usize {
            let (h, _) = module.types.iter()
                .find(|(_, t)| t.name.as_deref() == Some(name))
                .unwrap_or_else(|| panic!("WGSL に struct {name} が見つかりません"));
            layouter[h].size as usize
        };
        assert_eq!(size_of("GpuLightR"), 112, "GpuLightR は 112B（lighting.rs GpuLight と一致）");
        assert_eq!(size_of("LightMetaR"), 32, "LightMetaR は 32B（lighting.rs LightMeta と一致）");
    }

    /// 粗さフェードが roughness=1（＝完全に粗い面）で反射寄与を**厳密に 0** にすることを保証する。
    ///
    /// バグ「roughness=1 なのに鏡面反射が出る」の回帰ガード。反射シェーダ（SSR/RT）は
    /// `reflection_smoothness_weight(r) = 1 - smoothstep(FADE_START, FADE_END, r)` を計算し、
    /// `smooth_w <= 0.0` で早期 return する（reflection_ssr.wgsl / reflection_rt.wgsl）。ゆえに
    /// weight が roughness>=FADE_END で 0 になる（＝FADE_END<=1.0）ことが「roughness=1 で反射 0」の
    /// 数学的根拠である。本テストは WGSL からフェード定数を抽出し、その端点性質を Rust で再現検証する。
    ///
    /// なお実機で「roughness を上げても反射が残る」場合、原因はこのフェードではなく
    /// **実効 roughness = roughness_factor × MRテクスチャ.g（surface_gather.wgsl）が FADE_END 未満に
    /// 留まっている**ケースである（factor は乗算係数のため、MR テクスチャ持ちの面はスライダ最大でも
    /// 実効 roughness を FADE_END 以上へ持ち上げられない）。反射パスのチャンネル対応・バインドは正当。
    #[test]
    fn roughness_one_gives_zero_reflection_weight() {
        // reflection_common.wgsl から `const NAME: f32 = <値>;` を抽出する小さなパーサ。
        let src = include_str!("shaders/reflection_common.wgsl");
        let parse_f32 = |name: &str| -> f32 {
            let line = src.lines().map(str::trim)
                .find(|l| l.starts_with(&format!("const {name}")))
                .unwrap_or_else(|| panic!("reflection_common.wgsl に const {name} が見つかりません"));
            // `const NAME: f32 = 0.30; // ...` → `=` の後ろから `;` までを数値化。
            let rhs = line.split('=').nth(1).unwrap();
            let num: String = rhs.trim().chars()
                .take_while(|c| c.is_ascii_digit() || *c == '.' || *c == '-')
                .collect();
            num.parse::<f32>().unwrap_or_else(|_| panic!("const {name} を f32 として解釈できません: {num:?}"))
        };
        let start = parse_f32("REFLECTION_ROUGHNESS_FADE_START");
        let end   = parse_f32("REFLECTION_ROUGHNESS_FADE_END");

        // WGSL smoothstep(e0,e1,x) と同一（clamp 後に 3t^2-2t^3）。weight = 1 - smoothstep。
        let smoothstep = |e0: f32, e1: f32, x: f32| -> f32 {
            let t = ((x - e0) / (e1 - e0)).clamp(0.0, 1.0);
            t * t * (3.0 - 2.0 * t)
        };
        let weight = |r: f32| -> f32 { 1.0 - smoothstep(start, end, r) };

        // 定数の健全性: START < END <= 1.0。END>1.0 だと roughness=1 でも weight>0 となり漏れる。
        assert!(start < end, "FADE_START({start}) < FADE_END({end}) であること");
        assert!(end <= 1.0, "FADE_END({end}) <= 1.0（roughness=1 で必ず 0 にするため）");

        // 端点性質: roughness=1 で厳密に 0（＝反射寄与 0）。FADE_END でも 0。FADE_START 以下で全反射(1)。
        assert_eq!(weight(1.0), 0.0, "roughness=1 で反射フェード weight は厳密に 0 であること");
        assert_eq!(weight(end), 0.0, "roughness=FADE_END で weight は 0 であること");
        assert_eq!(weight(start), 1.0, "roughness<=FADE_START で weight は 1（全反射）であること");
    }
}
