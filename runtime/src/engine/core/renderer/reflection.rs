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
        // バインドレス（B2）: RT 影対応 かつ バインドレス対応の GPU でのみ、RT 反射のヒット
        // シェーディングをテクスチャサンプルに差し替える。片方でも欠ければ従来の平均色経路。
        // 非対応 GPU で binding_array を宣言すると BGL 生成が失敗するため、**BGL/パイプライン
        // レベルで分岐**する（シェーダ内分岐では不可）。
        let use_bindless = super::rt_shadow::rt_shadows_supported() && super::bindless::bindless_supported();

        // RT データ（group3）: lights storage + meta storage + tlas + albedo storage。
        // meta は **storage** で持つ（WebGPU 制約: binding_array と uniform buffer は同一 bind group
        // に同居不可。バインドレス時 group3 は binding_array を含むため meta を uniform にできない。
        // レイアウト 32B は uniform と一致＝値は不変。非バインドレス時も同レイアウトに揃える）。
        // バインドレス時は instance_table(4) + UV(5) + index(6) + テクスチャ配列(7) + サンプラー(8)
        // を同居させる（reflection_rt は専用パイプラインなので group 配置の自由度が高い）。
        // storage 本数: 非バインドレス 3（lights/meta/albedo）/ バインドレス 6（+instance_table/UV/index）。
        // いずれもフラグメント段の上限 12 以内。group3 に uniform は無い（binding_array と両立）。
        let mut rt_entries = vec![
            storage_ro(0),
            storage_ro(1),
            wgpu::BindGroupLayoutEntry {
                binding: 2, visibility: vis,
                ty: wgpu::BindingType::AccelerationStructure { vertex_return: false },
                count: None,
            },
            storage_ro(3),
        ];
        if use_bindless {
            // テクスチャ配列容量（アダプタ上限でクランプ済みの確定値）。
            let cap = super::bindless::bindless_capacity().max(1);
            rt_entries.push(storage_ro(4)); // instance_table（BindlessInstanceRecord 配列）
            rt_entries.push(storage_ro(5)); // UV メガバッファ（vec2<f32> 配列）
            rt_entries.push(storage_ro(6)); // index メガバッファ（u32 配列）
            rt_entries.push(wgpu::BindGroupLayoutEntry {
                binding: 7, visibility: vis,
                ty: wgpu::BindingType::Texture {
                    sample_type:    wgpu::TextureSampleType::Float { filterable: true },
                    view_dimension: wgpu::TextureViewDimension::D2, multisampled: false,
                },
                // binding_array（count=cap）。max_binding_array_elements_per_shader_stage=cap に一致。
                count: Some(std::num::NonZeroU32::new(cap).unwrap()),
            });
            rt_entries.push(samp(8)); // 共有サンプラー（リニア・リピート）
        }
        let rt_data_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Reflection RT Data BGL (group3)"),
            entries: &rt_entries,
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
        // ヒットシェーディングはバインドレス可否で連結シェーダを切り替える:
        //   bindless: reflection_rt_hit_on.wgsl（UV 補間＋テクスチャサンプル。binding_array 宣言あり）
        //   従来    : reflection_rt_hit_off.wgsl（平均色。binding_array 宣言なし）
        let rt = if super::rt_shadow::rt_shadows_supported() {
            let mut srcs: Vec<&str> = vec![
                "cluster_common.wgsl", "reflection_common.wgsl", "ddgi_common.wgsl", "reflection_rt.wgsl",
            ];
            if use_bindless {
                srcs.push("bindless_common.wgsl");         // BindlessInstanceRecord 定義
                srcs.push("reflection_rt_hit_on.wgsl");    // 配列宣言＋UV サンプル
            } else {
                srcs.push("reflection_rt_hit_off.wgsl");   // 平均色フォールバック
            }
            Some(build_reflection_pipeline(
                device, cache,
                &[&deferred.camera_bgl, &deferred.gbuffer_bgl, &input_bgl, &rt_data_bgl, &gi_bgl],
                &srcs, "vs_fullscreen", "fs_rt", "reflection_rt", out_format, None,
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

    /// RT データの拡張 BindGroup（バインドレス B2）を生成する（RT の group3）。
    /// lights/meta/tlas/albedo（0..3）に加え、instance_table(4)・UV(5)・index(6)・
    /// テクスチャ配列(7)・共有サンプラー(8) を同居させる。`rt_data_bgl` が `use_bindless=true` で
    /// 構築されている前提（`ReflectionPipelines::new`）。テクスチャ配列は毎フレーム全スロット
    /// （登録済み＝実 view / 空き＝ダミー白）を並べて構築する（登録変化の追従は再構築で担保）。
    pub fn create_rt_data_bg_bindless(
        &self, device: &wgpu::Device,
        lights: &wgpu::Buffer, meta: &wgpu::Buffer,
        tlas: &wgpu::Tlas, albedo: &wgpu::Buffer,
        bindless: &super::bindless::BindlessResources,
    ) -> wgpu::BindGroup {
        // capacity 個の view 参照列（登録済み or ダミー）。TextureViewArray はこのスライスを要求する。
        let views = bindless.texture_view_list();
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Reflection RT Data BG (bindless)"),
            layout: &self.rt_data_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: lights.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: meta.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::AccelerationStructure(tlas) },
                wgpu::BindGroupEntry { binding: 3, resource: albedo.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 4, resource: bindless.instance_table_buffer().as_entire_binding() },
                wgpu::BindGroupEntry { binding: 5, resource: bindless.uv_buffer().as_entire_binding() },
                wgpu::BindGroupEntry { binding: 6, resource: bindless.index_buffer().as_entire_binding() },
                wgpu::BindGroupEntry { binding: 7, resource: wgpu::BindingResource::TextureViewArray(&views) },
                wgpu::BindGroupEntry { binding: 8, resource: wgpu::BindingResource::Sampler(bindless.shared_sampler()) },
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

// ============================================================
//  WGSL 静的検証（naga parse + validate）＋レイアウト照合
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

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

    /// RT 反射連結（従来・平均色フォールバック, RAY_QUERY 必須）を naga で parse + validate する。
    /// reflection_rt.wgsl は `rt_hit_base_color` を連結先（hit_off）に委ねるため、単体では
    /// 未定義関数で落ちる。よってヒットファイルを必ず連結して検証する。
    #[test]
    fn reflection_rt_off_shader_parses() {
        let cluster = include_str!("shaders/cluster_common.wgsl");
        let refl_c  = include_str!("shaders/reflection_common.wgsl");
        let ddgi_c  = include_str!("shaders/ddgi_common.wgsl");
        let rt      = include_str!("shaders/reflection_rt.wgsl");
        let hit_off = include_str!("shaders/reflection_rt_hit_off.wgsl");
        let src = [cluster, refl_c, ddgi_c, rt, hit_off].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[reflection_rt off] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(), naga::valid::Capabilities::RAY_QUERY);
        v.validate(&module).unwrap_or_else(|e| panic!("[reflection_rt off] validate 失敗: {e:?}"));
    }

    /// RT 反射連結（バインドレス・本物のテクスチャ色）を naga で parse + validate する。
    /// binding_array（テクスチャ配列）が RAY_QUERY と共存して validate を通ること（B2 の要）。
    /// 非一様インデックスのため SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING を要求する。
    #[test]
    fn reflection_rt_on_bindless_shader_parses() {
        let cluster  = include_str!("shaders/cluster_common.wgsl");
        let refl_c   = include_str!("shaders/reflection_common.wgsl");
        let ddgi_c   = include_str!("shaders/ddgi_common.wgsl");
        let rt       = include_str!("shaders/reflection_rt.wgsl");
        let bindless = include_str!("shaders/bindless_common.wgsl");
        let hit_on   = include_str!("shaders/reflection_rt_hit_on.wgsl");
        let src = [cluster, refl_c, ddgi_c, rt, bindless, hit_on].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[reflection_rt on] WGSL parse 失敗: {e:?}"));
        let caps = naga::valid::Capabilities::RAY_QUERY
            | naga::valid::Capabilities::SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING;
        let mut v = naga::valid::Validator::new(naga::valid::ValidationFlags::all(), caps);
        v.validate(&module).unwrap_or_else(|e| panic!("[reflection_rt on] validate 失敗: {e:?}"));
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
        let hit_off = include_str!("shaders/reflection_rt_hit_off.wgsl");
        let src = [cluster, refl_c, ddgi_c, rt, hit_off].join("\n");
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
