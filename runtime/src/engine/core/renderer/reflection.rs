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
//    SSR: group0=camera / group1=G-Buffer / group2=input(params+hdr+sky) / group3=GI       （4 groups）
//    RT : group0=camera / group1=G-Buffer / group2=input / group3=RTデータ / group4=GI     （5 groups＝上限）
//    composite: group0=反射RT+sampler（1 group）
//  ミス経路の天球サンプル（sky）を **group2 に置いた**のは、group2 が SSR / RT の
//  **両変種で共有される唯一のグループ**であり、かつ `binding_array` を含まないため
//  uniform buffer を素直に置けるからである（水面反射は group3 にバインドレスの
//  テクスチャ配列が同居するので storage 化が必要だった。ここではその制約が無い）。
//  グループ数は 1 つも増えない（上限 5 を維持）。
//  group0/1 は DeferredLightingPipelines の camera_bgl/gbuffer_bgl を借用して等価性を担保する
//  （frame_renderer は camera_buf.bind_group と deferred::create_gbuffer_bind_group をそのまま流用）。
// ============================================================

use super::ddgi::GiResources;
use super::deferred::DeferredLightingPipelines;
use super::pipeline::get_shader_source;
use super::reflection_sky::{ReflectionSkySource, ReflectionSkyUniform};

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
    pub _pad0: f32,
    pub _pad1: f32,
    pub _pad2: f32,
}

impl ReflectionParams {
    /// intensity から UBO 値を作る。
    pub fn new(intensity: f32) -> Self {
        Self {
            intensity,
            _pad0: 0.0,
            _pad1: 0.0,
            _pad2: 0.0,
        }
    }
}

// ─── ReflectionPipelines ─────────────────────────────────────

/// 反射パイプライン一式（SSR 常時・RT は対応 GPU のみ・合成）。
pub struct ReflectionPipelines {
    /// SSR パイプライン（RAY_QUERY 不要・常に構築）。
    pub ssr: wgpu::RenderPipeline,
    /// RT 反射パイプライン（RAY_QUERY 必須・RT 対応 GPU でのみ Some）。
    pub rt: Option<wgpu::RenderPipeline>,
    /// 合成パイプライン（Additive One/One で RT_REFLECTION を scene_hdr へ加算）。
    pub composite: wgpu::RenderPipeline,
    /// group2: 反射パラメータ＋シーン HDR 入力のレイアウト。
    pub input_bgl: wgpu::BindGroupLayout,
    /// GI レイアウト（SSR は group3・RT は group4 で共用）。
    pub gi_bgl: wgpu::BindGroupLayout,
    /// RT データレイアウト（RT の group3。lights/meta/tlas/albedo）。
    pub rt_data_bgl: wgpu::BindGroupLayout,
    /// 合成の group0 レイアウト（反射 RT テクスチャ＋サンプラー）。
    pub composite_bgl: wgpu::BindGroupLayout,
    /// ReflectionParams UBO（毎フレーム intensity を書き込む）。
    pub params_buffer: wgpu::Buffer,
    /// 入力・GI・合成で共用するリニア clamp サンプラー。
    pub sampler: wgpu::Sampler,
    /// ミス経路の天球サンプル用 UBO（64B。毎フレーム `update_sky` で書き換える）。
    sky_buffer: wgpu::Buffer,
    /// 天球サンプラー（**経度方向は Repeat**）。equirectangular の u は 0/1 で巻き付くので、
    /// clamp すると経度 0°（背後）の継ぎ目に縦線が出る。`skybox.rs` / 水面反射と同じ規約。
    sky_sampler: wgpu::Sampler,
    /// スカイボックスが無いフレームに group2 の天球スロットを埋めるダミー 1x1（黒・永続）。
    /// uniform 側の `enabled = 0` によりサンプル自体が起きないが、**バインドは必ず埋める**
    /// 必要がある（レイアウトを常に同一に保ち、パイプラインを 1 本で済ませるため）。
    #[allow(dead_code)]
    sky_dummy_tex: wgpu::Texture,
    /// 上のビュー。
    sky_dummy_view: wgpu::TextureView,
}

impl ReflectionPipelines {
    /// 反射パイプライン一式を構築する。
    ///
    /// - `deferred`   : group0/1（camera/gbuffer）の BGL を借用する（等価性担保）。
    /// - `out_format` : 出力先 HDR フォーマット（scene_hdr / RT_REFLECTION と同一）。
    /// - `queue`      : 天球ダミー 1x1 を黒で初期化するために使う（未初期化メモリを読ませない）。
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        deferred: &DeferredLightingPipelines,
        out_format: wgpu::TextureFormat,
        cache: Option<&wgpu::PipelineCache>,
    ) -> Self {
        let vis = wgpu::ShaderStages::FRAGMENT;
        let uniform = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: vis,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Uniform,
                has_dynamic_offset: false,
                min_binding_size: None,
            },
            count: None,
        };
        let storage_ro = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: vis,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Storage { read_only: true },
                has_dynamic_offset: false,
                min_binding_size: None,
            },
            count: None,
        };
        let tex = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: vis,
            ty: wgpu::BindingType::Texture {
                sample_type: wgpu::TextureSampleType::Float { filterable: true },
                view_dimension: wgpu::TextureViewDimension::D2,
                multisampled: false,
            },
            count: None,
        };
        let samp = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: vis,
            ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
            count: None,
        };

        // group2: input（params uniform + scene_hdr tex + sampler + 天球 uniform/tex/sampler）。
        // 天球 3 本（3..5）は SSR / RT の**両変種が共有**する（ミス経路を揃えるため）。
        // フラグメント段のリソース数（既定上限 16 texture / 16 sampler に対する実測）:
        //   SSR: texture = group1 の 6（G-Buffer 4 + 深度 + AO）+ group2 の 2（hdr + 天球）
        //                + group3 の 2（GI irr/vis）= 10 ／ sampler = 1 + 2 + 1 = 4
        //   RT : 上記に group4 の GI 2 texture・1 sampler が加わり、group3 は
        //        バインドレス時のみ binding_array（別枠 max_binding_array_elements_per_shader_stage）
        //        ＋サンプラー 1 本。単体テクスチャは 10、サンプラーは 5。
        // いずれも上限に対して十分な余裕がある。
        let input_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Reflection Input BGL (group2)"),
            entries: &[uniform(0), tex(1), samp(2), uniform(3), tex(4), samp(5)],
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
        let use_bindless =
            super::rt_shadow::rt_shadows_supported() && super::bindless::bindless_supported();

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
                binding: 2,
                visibility: vis,
                ty: wgpu::BindingType::AccelerationStructure {
                    vertex_return: false,
                },
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
                binding: 7,
                visibility: vis,
                ty: wgpu::BindingType::Texture {
                    sample_type: wgpu::TextureSampleType::Float { filterable: true },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled: false,
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
            device,
            cache,
            &[
                &deferred.camera_bgl,
                &deferred.gbuffer_bgl,
                &input_bgl,
                &gi_bgl,
            ],
            &[
                // 天球サンプルの共有定義は reflection_common より前（宣言前参照はできない）。
                "sky_reflection_common.wgsl",
                "reflection_common.wgsl",
                "ddgi_common.wgsl",
                "reflection_ssr.wgsl",
            ],
            "vs_fullscreen",
            "fs_ssr",
            "reflection_ssr",
            out_format,
            None,
        );

        // RT 反射パイプライン（group0..4, RT 対応 GPU のみ）。
        // ヒットシェーディングはバインドレス可否で連結シェーダを切り替える:
        //   bindless: reflection_rt_hit_on.wgsl（UV 補間＋テクスチャサンプル。binding_array 宣言あり）
        //   従来    : reflection_rt_hit_off.wgsl（平均色。binding_array 宣言なし）
        let rt = if super::rt_shadow::rt_shadows_supported() {
            let mut srcs: Vec<&str> = vec![
                "cluster_common.wgsl",
                // 天球サンプルの共有定義は reflection_common より前（宣言前参照はできない）。
                "sky_reflection_common.wgsl",
                "reflection_common.wgsl",
                "ddgi_common.wgsl",
                "reflection_rt.wgsl",
            ];
            if use_bindless {
                srcs.push("bindless_common.wgsl"); // BindlessInstanceRecord 定義
                srcs.push("reflection_rt_hit_on.wgsl"); // 配列宣言＋UV サンプル
            } else {
                srcs.push("reflection_rt_hit_off.wgsl"); // 平均色フォールバック
            }
            Some(build_reflection_pipeline(
                device,
                cache,
                &[
                    &deferred.camera_bgl,
                    &deferred.gbuffer_bgl,
                    &input_bgl,
                    &rt_data_bgl,
                    &gi_bgl,
                ],
                &srcs,
                "vs_fullscreen",
                "fs_rt",
                "reflection_rt",
                out_format,
                None,
            ))
        } else {
            None
        };

        // 合成パイプライン（Additive One/One で scene_hdr へ加算）。
        let additive = wgpu::BlendState {
            color: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::One,
                dst_factor: wgpu::BlendFactor::One,
                operation: wgpu::BlendOperation::Add,
            },
            alpha: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::One,
                dst_factor: wgpu::BlendFactor::One,
                operation: wgpu::BlendOperation::Add,
            },
        };
        let composite = build_reflection_pipeline(
            device,
            cache,
            &[&composite_bgl],
            &["reflection_composite.wgsl"],
            "vs_composite",
            "fs_composite",
            "reflection_composite",
            out_format,
            Some(additive),
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
            size: std::mem::size_of::<ReflectionParams>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        // ── ミス経路の天球サンプル用リソース ───────────────────────
        // 天球用サンプラー（u=Repeat / v=Clamp）。`skybox.rs` / 水面反射と同じ規約で、
        // 経度の巻き付きを Repeat に、天頂・天底のにじみ防止を Clamp に任せる。
        let sky_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("Reflection Sky Sampler"),
            address_mode_u: wgpu::AddressMode::Repeat,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::FilterMode::Nearest,
            ..Default::default()
        });
        // スカイボックスパラメータ UBO（既定は「無し」＝enabled 0。
        // スカイボックスのあるフレームだけ `update_sky` が上書きする）。
        let sky_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Reflection Sky Params UBO"),
            size: std::mem::size_of::<ReflectionSkyUniform>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        queue.write_buffer(
            &sky_buffer,
            0,
            bytemuck::bytes_of(&ReflectionSkyUniform::disabled()),
        );
        // スカイボックス無しフレーム用のダミー 1x1（黒）。enabled=0 でサンプルされないが、
        // 未初期化メモリを読ませないよう黒で明示的に埋める。
        /// ダミー天球のフォーマット（filterable float であればよい。1 テクセル 4 バイト）。
        const SKY_DUMMY_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba8Unorm;
        /// 上のフォーマットの 1 テクセルのバイト数。
        const SKY_DUMMY_TEXEL_BYTES: u32 = 4;
        let sky_dummy_tex = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Reflection Sky Dummy 1x1"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: SKY_DUMMY_FORMAT,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        queue.write_texture(
            sky_dummy_tex.as_image_copy(),
            &[0u8; SKY_DUMMY_TEXEL_BYTES as usize],
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(SKY_DUMMY_TEXEL_BYTES),
                rows_per_image: Some(1),
            },
            wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
        );
        let sky_dummy_view = sky_dummy_tex.create_view(&wgpu::TextureViewDescriptor::default());

        Self {
            ssr,
            rt,
            composite,
            input_bgl,
            gi_bgl,
            rt_data_bgl,
            composite_bgl,
            params_buffer,
            sampler,
            sky_buffer,
            sky_sampler,
            sky_dummy_tex,
            sky_dummy_view,
        }
    }

    /// intensity を ReflectionParams UBO へ書き込む（毎フレーム反射パス直前に呼ぶ）。
    pub fn write_params(&self, queue: &wgpu::Queue, intensity: f32) {
        queue.write_buffer(
            &self.params_buffer,
            0,
            bytemuck::bytes_of(&ReflectionParams::new(intensity)),
        );
    }

    /// このフレームの代表スカイボックスを GPU へ反映する（`create_input_bg` より前に呼ぶ）。
    ///
    /// `None` を渡すと「スカイボックス無し」（`enabled = 0`）を書き、反射のミス経路は
    /// 従来どおり GI プローブ／アンビエントへ落ちる（＝D6 当初と完全に同じ挙動）。
    /// **パラメータ UBO だけを更新する**（テクスチャの差し替えは `create_input_bg` の引数側）。
    pub fn update_sky(&self, queue: &wgpu::Queue, sky: Option<&ReflectionSkySource<'_>>) {
        let u = match sky {
            Some(s) => s.uniform,
            None => ReflectionSkyUniform::disabled(),
        };
        queue.write_buffer(&self.sky_buffer, 0, bytemuck::bytes_of(&u));
    }

    /// group2（input）の BindGroup を生成する
    /// （params UBO + scene_hdr + sampler + 天球 UBO/テクスチャ/サンプラー）。
    ///
    /// `sky` はこのフレームの代表スカイボックス（`SkyboxSystem::reflection_sky_source`）。
    /// `None` のときは**ダミー 1x1（黒）**を挿す（uniform 側の `enabled = 0` で
    /// サンプル自体が起きないが、バインドは必ず埋める＝レイアウトは常に同一）。
    pub fn create_input_bg(
        &self,
        device: &wgpu::Device,
        scene_hdr: &wgpu::TextureView,
        sky: Option<&ReflectionSkySource<'_>>,
    ) -> wgpu::BindGroup {
        let sky_view = match sky {
            Some(s) => s.view,
            None => &self.sky_dummy_view,
        };
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Reflection Input BG"),
            layout: &self.input_bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: self.params_buffer.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(scene_hdr),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::Sampler(&self.sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: self.sky_buffer.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 4,
                    resource: wgpu::BindingResource::TextureView(sky_view),
                },
                wgpu::BindGroupEntry {
                    binding: 5,
                    resource: wgpu::BindingResource::Sampler(&self.sky_sampler),
                },
            ],
        })
    }

    /// GI の BindGroup を生成する（SSR は group3・RT は group4 で使う）。
    pub fn create_gi_bg(&self, device: &wgpu::Device, gi: &GiResources) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Reflection GI BG"),
            layout: &self.gi_bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: gi.params_buffer().as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(gi.irradiance_view()),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::TextureView(gi.visibility_view()),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: wgpu::BindingResource::Sampler(&self.sampler),
                },
            ],
        })
    }

    /// RT データの BindGroup を生成する（RT の group3。lights/meta/tlas/albedo）。
    pub fn create_rt_data_bg(
        &self,
        device: &wgpu::Device,
        lights: &wgpu::Buffer,
        meta: &wgpu::Buffer,
        tlas: &wgpu::Tlas,
        albedo: &wgpu::Buffer,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Reflection RT Data BG"),
            layout: &self.rt_data_bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: lights.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: meta.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::AccelerationStructure(tlas),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: albedo.as_entire_binding(),
                },
            ],
        })
    }

    /// RT データの拡張 BindGroup（バインドレス B2）を生成する（RT の group3）。
    /// lights/meta/tlas/albedo（0..3）に加え、instance_table(4)・UV(5)・index(6)・
    /// テクスチャ配列(7)・共有サンプラー(8) を同居させる。`rt_data_bgl` が `use_bindless=true` で
    /// 構築されている前提（`ReflectionPipelines::new`）。テクスチャ配列は毎フレーム全スロット
    /// （登録済み＝実 view / 空き＝ダミー白）を並べて構築する（登録変化の追従は再構築で担保）。
    pub fn create_rt_data_bg_bindless(
        &self,
        device: &wgpu::Device,
        lights: &wgpu::Buffer,
        meta: &wgpu::Buffer,
        tlas: &wgpu::Tlas,
        albedo: &wgpu::Buffer,
        bindless: &super::bindless::BindlessResources,
    ) -> wgpu::BindGroup {
        // capacity 個の view 参照列（登録済み or ダミー）。TextureViewArray はこのスライスを要求する。
        let views = bindless.texture_view_list();
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Reflection RT Data BG (bindless)"),
            layout: &self.rt_data_bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: lights.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: meta.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::AccelerationStructure(tlas),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: albedo.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 4,
                    resource: bindless.instance_table_buffer().as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 5,
                    resource: bindless.uv_buffer().as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 6,
                    resource: bindless.index_buffer().as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 7,
                    resource: wgpu::BindingResource::TextureViewArray(&views),
                },
                wgpu::BindGroupEntry {
                    binding: 8,
                    resource: wgpu::BindingResource::Sampler(bindless.shared_sampler()),
                },
            ],
        })
    }

    /// 合成の group0 BindGroup を生成する（反射 RT テクスチャ＋サンプラー）。
    pub fn create_composite_bg(
        &self,
        device: &wgpu::Device,
        refl_view: &wgpu::TextureView,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Reflection Composite BG"),
            layout: &self.composite_bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(refl_view),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::Sampler(&self.sampler),
                },
            ],
        })
    }
}

/// 反射フルスクリーンパイプラインを 1 本構築する（手動レイアウト。gbuffer.rs の
/// build_gbuffer_pipeline と同じ手法）。深度なし・単一カラーターゲット。
#[allow(clippy::too_many_arguments)]
fn build_reflection_pipeline(
    device: &wgpu::Device,
    cache: Option<&wgpu::PipelineCache>,
    bgls: &[&wgpu::BindGroupLayout],
    shader_sources: &[&str],
    vs_entry: &str,
    fs_entry: &str,
    label: &str,
    out_format: wgpu::TextureFormat,
    blend: Option<wgpu::BlendState>,
) -> wgpu::RenderPipeline {
    let combined: String = shader_sources
        .iter()
        .map(|n| get_shader_source(n))
        .collect::<Vec<_>>()
        .join("\n");
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some(label),
        source: wgpu::ShaderSource::Wgsl(combined.into()),
    });
    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some(label),
        bind_group_layouts: bgls,
        push_constant_ranges: &[],
    });
    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some(label),
        layout: Some(&layout),
        vertex: wgpu::VertexState {
            module: &shader,
            entry_point: Some(vs_entry),
            buffers: &[],
            compilation_options: Default::default(),
        },
        fragment: Some(wgpu::FragmentState {
            module: &shader,
            entry_point: Some(fs_entry),
            targets: &[Some(wgpu::ColorTargetState {
                format: out_format,
                blend,
                write_mask: wgpu::ColorWrites::ALL,
            })],
            compilation_options: Default::default(),
        }),
        primitive: wgpu::PrimitiveState::default(),
        depth_stencil: None,
        multisample: wgpu::MultisampleState::default(),
        multiview: None,
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
        let sky_c = include_str!("shaders/sky_reflection_common.wgsl");
        let refl_c = include_str!("shaders/reflection_common.wgsl");
        let ddgi_c = include_str!("shaders/ddgi_common.wgsl");
        let ssr = include_str!("shaders/reflection_ssr.wgsl");
        let src = [sky_c, refl_c, ddgi_c, ssr].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[reflection_ssr] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        v.validate(&module)
            .unwrap_or_else(|e| panic!("[reflection_ssr] validate 失敗: {e:?}"));
    }

    /// RT 反射連結（従来・平均色フォールバック, RAY_QUERY 必須）を naga で parse + validate する。
    /// reflection_rt.wgsl は `rt_hit_base_color` を連結先（hit_off）に委ねるため、単体では
    /// 未定義関数で落ちる。よってヒットファイルを必ず連結して検証する。
    #[test]
    fn reflection_rt_off_shader_parses() {
        let cluster = include_str!("shaders/cluster_common.wgsl");
        let sky_c = include_str!("shaders/sky_reflection_common.wgsl");
        let refl_c = include_str!("shaders/reflection_common.wgsl");
        let ddgi_c = include_str!("shaders/ddgi_common.wgsl");
        let rt = include_str!("shaders/reflection_rt.wgsl");
        let hit_off = include_str!("shaders/reflection_rt_hit_off.wgsl");
        let src = [cluster, sky_c, refl_c, ddgi_c, rt, hit_off].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[reflection_rt off] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::RAY_QUERY,
        );
        v.validate(&module)
            .unwrap_or_else(|e| panic!("[reflection_rt off] validate 失敗: {e:?}"));
    }

    /// RT 反射連結（バインドレス・本物のテクスチャ色）を naga で parse + validate する。
    /// binding_array（テクスチャ配列）が RAY_QUERY と共存して validate を通ること（B2 の要）。
    /// 非一様インデックスのため SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING を要求する。
    #[test]
    fn reflection_rt_on_bindless_shader_parses() {
        let cluster = include_str!("shaders/cluster_common.wgsl");
        let sky_c = include_str!("shaders/sky_reflection_common.wgsl");
        let refl_c = include_str!("shaders/reflection_common.wgsl");
        let ddgi_c = include_str!("shaders/ddgi_common.wgsl");
        let rt = include_str!("shaders/reflection_rt.wgsl");
        let bindless = include_str!("shaders/bindless_common.wgsl");
        let hit_on = include_str!("shaders/reflection_rt_hit_on.wgsl");
        let src = [cluster, sky_c, refl_c, ddgi_c, rt, bindless, hit_on].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[reflection_rt on] WGSL parse 失敗: {e:?}"));
        let caps = naga::valid::Capabilities::RAY_QUERY
            | naga::valid::Capabilities::SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING;
        let mut v = naga::valid::Validator::new(naga::valid::ValidationFlags::all(), caps);
        v.validate(&module)
            .unwrap_or_else(|e| panic!("[reflection_rt on] validate 失敗: {e:?}"));
    }

    /// 合成連結（自前完結）を naga で parse + validate する。
    #[test]
    fn reflection_composite_shader_parses() {
        let comp = include_str!("shaders/reflection_composite.wgsl");
        let module = naga::front::wgsl::parse_str(comp)
            .unwrap_or_else(|e| panic!("[reflection_composite] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        v.validate(&module)
            .unwrap_or_else(|e| panic!("[reflection_composite] validate 失敗: {e:?}"));
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
        let sky_c = include_str!("shaders/sky_reflection_common.wgsl");
        let refl_c = include_str!("shaders/reflection_common.wgsl");
        let ddgi_c = include_str!("shaders/ddgi_common.wgsl");
        let rt = include_str!("shaders/reflection_rt.wgsl");
        let hit_off = include_str!("shaders/reflection_rt_hit_off.wgsl");
        let src = [cluster, sky_c, refl_c, ddgi_c, rt, hit_off].join("\n");
        let module = naga::front::wgsl::parse_str(&src).expect("RT 反射連結の parse に失敗");
        let mut layouter = naga::proc::Layouter::default();
        layouter
            .update(module.to_ctx())
            .expect("naga Layouter 計算に失敗");
        let size_of = |name: &str| -> usize {
            let (h, _) = module
                .types
                .iter()
                .find(|(_, t)| t.name.as_deref() == Some(name))
                .unwrap_or_else(|| panic!("WGSL に struct {name} が見つかりません"));
            layouter[h].size as usize
        };
        assert_eq!(
            size_of("GpuLightR"),
            112,
            "GpuLightR は 112B（lighting.rs GpuLight と一致）"
        );
        assert_eq!(
            size_of("LightMetaR"),
            32,
            "LightMetaR は 32B（lighting.rs LightMeta と一致）"
        );
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
            let line = src
                .lines()
                .map(str::trim)
                .find(|l| l.starts_with(&format!("const {name}")))
                .unwrap_or_else(|| {
                    panic!("reflection_common.wgsl に const {name} が見つかりません")
                });
            // `const NAME: f32 = 0.30; // ...` → `=` の後ろから `;` までを数値化。
            let rhs = line.split('=').nth(1).unwrap();
            let num: String = rhs
                .trim()
                .chars()
                .take_while(|c| c.is_ascii_digit() || *c == '.' || *c == '-')
                .collect();
            num.parse::<f32>()
                .unwrap_or_else(|_| panic!("const {name} を f32 として解釈できません: {num:?}"))
        };
        let start = parse_f32("REFLECTION_ROUGHNESS_FADE_START");
        let end = parse_f32("REFLECTION_ROUGHNESS_FADE_END");

        // WGSL smoothstep(e0,e1,x) と同一（clamp 後に 3t^2-2t^3）。weight = 1 - smoothstep。
        let smoothstep = |e0: f32, e1: f32, x: f32| -> f32 {
            let t = ((x - e0) / (e1 - e0)).clamp(0.0, 1.0);
            t * t * (3.0 - 2.0 * t)
        };
        let weight = |r: f32| -> f32 { 1.0 - smoothstep(start, end, r) };

        // 定数の健全性: START < END <= 1.0。END>1.0 だと roughness=1 でも weight>0 となり漏れる。
        assert!(
            start < end,
            "FADE_START({start}) < FADE_END({end}) であること"
        );
        assert!(
            end <= 1.0,
            "FADE_END({end}) <= 1.0（roughness=1 で必ず 0 にするため）"
        );

        // 端点性質: roughness=1 で厳密に 0（＝反射寄与 0）。FADE_END でも 0。FADE_START 以下で全反射(1)。
        assert_eq!(
            weight(1.0),
            0.0,
            "roughness=1 で反射フェード weight は厳密に 0 であること"
        );
        assert_eq!(
            weight(end),
            0.0,
            "roughness=FADE_END で weight は 0 であること"
        );
        assert_eq!(
            weight(start),
            1.0,
            "roughness<=FADE_START で weight は 1（全反射）であること"
        );
    }

    /// RT 反射のハイブリッド（画面内ヒット採用）の深度一致許容 HIT_DEPTH_TOLERANCE の健全性。
    /// 相対許容（ヒット深度に対する割合）であり、0 だと浮動小数の丸めで一致がほぼ成立せず常に
    /// 解析近似へフォールバックする（機能無効化）。逆に大きすぎると手前で遮蔽された別の面まで
    /// 「一致」と誤判定して誤った scene_hdr 色を反射に採用する。よって (0, 0.5) の範囲に収める
    /// （shadow_mask / joint bilateral と同じ相対 5% 前後の流儀）。
    #[test]
    fn rt_reflection_hit_depth_tolerance_is_valid() {
        let src = include_str!("shaders/reflection_rt.wgsl");
        // `const HIT_DEPTH_TOLERANCE: f32 = 0.05;` の右辺から数値部分を抽出する。
        let line = src
            .lines()
            .map(str::trim)
            .find(|l| l.starts_with("const HIT_DEPTH_TOLERANCE"))
            .expect("reflection_rt.wgsl に const HIT_DEPTH_TOLERANCE が見つかりません");
        let rhs = line
            .split('=')
            .nth(1)
            .expect("HIT_DEPTH_TOLERANCE の右辺がありません");
        let num: String = rhs
            .trim()
            .chars()
            .take_while(|c| c.is_ascii_digit() || *c == '.')
            .collect();
        let t: f32 = num
            .parse()
            .unwrap_or_else(|_| panic!("HIT_DEPTH_TOLERANCE を f32 として解釈できません: {num:?}"));
        assert!(
            t > 0.0,
            "HIT_DEPTH_TOLERANCE({t}) は 0 より大（0 だと一致成立せず機能無効化）"
        );
        assert!(
            t < 0.5,
            "HIT_DEPTH_TOLERANCE({t}) は 0.5 未満（遮蔽面の誤一致を防ぐ許容幅）"
        );
    }

    /// ミス経路（レイが空へ抜けたとき）が **GI より先に天球テクスチャを直接サンプル**すること。
    ///
    /// これが無いと、反射方向の空が画面外にあるケース（見下ろし・壁際・磨かれた床など）で
    /// 反射が GI プローブの平坦色（GI 無効なら黒）になり、**同じシーンの水面反射
    /// （W5.2 は同じ経路を持つ）と見た目が食い違う**。本タスクの回帰ガードである。
    /// SSR / RT の**両変種**を見る（片方だけ空が映らない事故の防止）。
    #[test]
    fn reflection_miss_path_samples_the_skybox_before_gi() {
        // SSR: ssr_fallback の中で「天球 → GI」の順であること。
        let ssr = include_str!("shaders/reflection_ssr.wgsl").replace("\r\n", "\n");
        let ssr_fb = ssr
            .split("fn ssr_fallback(")
            .nth(1)
            .expect("reflection_ssr.wgsl にミス経路 ssr_fallback が無い");
        let i_sky = ssr_fb
            .find("reflection_sky_miss(")
            .expect("SSR のミス経路に天球サンプルが無い（画面外の空が映らない）");
        let i_gi = ssr_fb
            .find("ssr_gi.enabled")
            .expect("SSR のミス経路に GI フォールバックが無い");
        assert!(
            i_sky < i_gi,
            "SSR のミス経路が「天球 → GI」の順でない（GI の平坦色が空を上書きする）"
        );

        // RT: rt_refl_fallback の中で「天球 → GI」の順であること。
        let rt = include_str!("shaders/reflection_rt.wgsl").replace("\r\n", "\n");
        let rt_fb = rt
            .split("fn rt_refl_fallback(")
            .nth(1)
            .expect("reflection_rt.wgsl にミス経路 rt_refl_fallback が無い");
        let i_sky_rt = rt_fb
            .find("reflection_sky_miss(")
            .expect("RT のミス経路に天球サンプルが無い（画面外の空が映らない）");
        let i_gi_rt = rt_fb
            .find("rt_gi.enabled")
            .expect("RT のミス経路に GI フォールバックが無い");
        assert!(
            i_sky_rt < i_gi_rt,
            "RT のミス経路が「天球 → GI」の順でない（GI の平坦色が空を上書きする）"
        );

        // 天球サンプルの実体は **水面反射と共有**の関数であること。
        // 自前実装に戻ると UV 変換・実効色がズレて水面と不透明面で違う空の色になる。
        let common = include_str!("shaders/reflection_common.wgsl").replace("\r\n", "\n");
        assert!(
            common.contains("sky_refl_sample(u_refl_sky, t_refl_sky, s_refl_sky, dir)"),
            "天球サンプルが共有関数 sky_refl_sample を経由していない（水面反射と式が分岐する）"
        );
        // group2（SSR / RT の両変種が共有する唯一のグループ）へ 3 本を置く契約。
        // group2 に binding_array は無いので uniform で置ける（水面側は storage）。
        for decl in [
            "@group(2) @binding(3) var<uniform> u_refl_sky: ReflectionSkyUniform;",
            "@group(2) @binding(4) var          t_refl_sky: texture_2d<f32>;",
            "@group(2) @binding(5) var          s_refl_sky: sampler;",
        ] {
            assert!(
                common.contains(decl),
                "reflection_common.wgsl に天球バインドの宣言が無い/変わっている: {decl}"
            );
        }
    }

    /// **GPU 実機での反射パイプライン生成と group2 BindGroup 生成**が通ること
    /// （`--ignored` で実行する検証用。水面反射の同種テストと同じ流儀）。
    ///
    /// naga の静的検証は WGSL の妥当性しか見ない。実機でしか出ない失敗要因は 2 つ:
    ///   ① **group2 に足した天球 3 本（uniform / texture / sampler）の種別・binding 番号**が
    ///      Rust の `entries` と WGSL の宣言で食い違うと、ここで初めて落ちる。
    ///   ② **`max_bind_groups`**（RT 変種は group0〜4 の 5 個＝上限ちょうどを使う）。
    /// そのため `Limits::default()`（max_bind_groups=4）ではなく 5 を要求する。
    ///
    /// 実行: `cargo test reflection::tests::reflection_pipelines_build_on_gpu -- --ignored --nocapture`
    #[test]
    #[ignore = "実 GPU が必要。--ignored で実行する"]
    fn reflection_pipelines_build_on_gpu() {
        // RT／バインドレスのグローバルフラグを書き換えるので、他の GPU テストと直列化する
        //（並列に走ると相手の「元へ戻す」で設定を潰され、偽陽性で落ちる）。
        let _guard = super::super::GPU_TEST_LOCK
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor::default());
        let Ok(adapter) =
            pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions::default()))
        else {
            eprintln!("[reflection] GPU アダプタが見つからないため検証をスキップ");
            return;
        };
        // アダプタが RAY_QUERY に対応していれば RT 変種も検証対象に含める。
        let rt_feats = wgpu::Features::EXPERIMENTAL_RAY_QUERY
            | wgpu::Features::EXPERIMENTAL_RAY_TRACING_ACCELERATION_STRUCTURE;
        let has_rt = adapter.features().contains(rt_feats);
        // バインドレスも本体と同じ条件で判定する（RT の group3 が binding_array を含む変種）。
        let bindless_feats = wgpu::Features::TEXTURE_BINDING_ARRAY
            | wgpu::Features::SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING;
        let adapter_limits = adapter.limits();
        let array_cap = adapter_limits.max_binding_array_elements_per_shader_stage;
        let has_bindless = has_rt && adapter.features().contains(bindless_feats) && array_cap > 0;
        let bindless_cap = array_cap
            .min(adapter_limits.max_sampled_textures_per_shader_stage)
            .min(super::super::BINDLESS_MAX_TEXTURES)
            .max(1);
        let default_limits = wgpu::Limits::default();
        let (device, queue) = pollster::block_on(adapter.request_device(&wgpu::DeviceDescriptor {
            required_features: if has_rt {
                rt_feats
            } else {
                wgpu::Features::empty()
            } | if has_bindless {
                bindless_feats
            } else {
                wgpu::Features::empty()
            },
            required_limits: wgpu::Limits {
                // 本体（renderer/mod.rs）と同じ要求。RT 変種は group0〜4 を使う。
                max_bind_groups: 5,
                max_sampled_textures_per_shader_stage: if has_bindless {
                    bindless_cap.max(default_limits.max_sampled_textures_per_shader_stage)
                } else {
                    default_limits.max_sampled_textures_per_shader_stage
                },
                max_binding_array_elements_per_shader_stage: if has_bindless {
                    bindless_cap
                } else {
                    default_limits.max_binding_array_elements_per_shader_stage
                },
                ..wgpu::Limits::default()
            },
            ..Default::default()
        }))
        .expect("デバイス生成に失敗");

        // RT／バインドレス対応フラグは他テストと共有のグローバルなので、必ず元へ戻す。
        let saved_rt = super::super::rt_shadow::rt_shadows_supported();
        let saved_bl = super::super::bindless::bindless_supported();
        let saved_bl_cap = super::super::bindless::bindless_capacity();
        super::super::rt_shadow::set_rt_shadows_supported(has_rt);
        super::super::bindless::set_bindless_supported(has_bindless);
        super::super::bindless::set_bindless_capacity(if has_bindless { bindless_cap } else { 0 });

        // group0/1 の BGL 提供元。反射は deferred の camera_bgl / gbuffer_bgl を借りる。
        let deferred = DeferredLightingPipelines::new(
            &device,
            &queue,
            REFLECTION_FORMAT,
            super::super::DEPTH_FORMAT,
            None,
        );
        // 生成そのものが検証（失敗すれば wgpu がパニック／エラーを出す）。
        let pipelines =
            ReflectionPipelines::new(&device, &queue, &deferred, REFLECTION_FORMAT, None);
        assert_eq!(
            pipelines.rt.is_some(),
            has_rt,
            "RT 対応アダプタなら RT 変種が、非対応なら SSR 変種だけが構築されること"
        );

        // ── group2（params + scene_hdr + sampler + 天球 3 本）の BindGroup 生成まで検証する ──
        // 天球バインドは本タスクで後から足した分なので、ここが通ることが
        // 「binding 3..5 の種別（uniform / texture / sampler）が Rust と WGSL で一致している」
        // ことの実機側の証拠になる。
        let hdr = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("reflection test scene hdr"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: REFLECTION_FORMAT,
            usage: wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        });
        let hdr_view = hdr.create_view(&wgpu::TextureViewDescriptor::default());
        // スカイボックス無し（ダミー 1x1 が挿さる）と有り（HDR を天球に見立てる）の両方。
        let sky = ReflectionSkySource {
            view: &hdr_view,
            uniform: ReflectionSkyUniform::disabled(),
        };
        let _bg_none = pipelines.create_input_bg(&device, &hdr_view, None);
        let _bg_sky = pipelines.create_input_bg(&device, &hdr_view, Some(&sky));
        pipelines.update_sky(&queue, Some(&sky));
        pipelines.update_sky(&queue, None);

        // グローバルを元へ戻す（他テストへの副作用を残さない）。
        super::super::rt_shadow::set_rt_shadows_supported(saved_rt);
        super::super::bindless::set_bindless_supported(saved_bl);
        super::super::bindless::set_bindless_capacity(saved_bl_cap);
    }
}
