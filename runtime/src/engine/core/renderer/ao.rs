// ============================================================
//  ao.rs — アンビエントオクルージョン（SSAO / RT-AO）パイプライン＋半解像度リソース（Phase D4）
//
//  ## 役割（単一責任）
//  Deferred（G-Buffer）有効時のみ走る独立フルスクリーン AO パスのパイプライン一式
//  （SSAO 常時・RT-AO は対応 GPU のみ）と、AO の数値パラメータ UBO・いもす法ブラー基盤・
//  半解像度リソースを持つ。手本は reflection.rs（build ヘルパで手動パイプライン構築、
//  group0/1 は deferred の camera_bgl/gbuffer_bgl を借用、RT は rt_shadows_supported 時のみ Some）。
//
//  ## パス順（frame_renderer が制御）
//    G-Buffer 書き込み
//      → AO 生成パス（G-Buffer＋深度 → 半解像度 ao_raw へ AO=1..0）
//      → いもす法ブラー（ao_raw → ao_a/ao_b, 結果 ao_b）
//      → デファードライティング（group1 に ao_b をバイリニアで供給し occlusion に乗算）
//    AO は occlusion に乗るためアンビエント/DDGI/疑似バウンスにのみ効き、直接光は暗くしない
//    （lighting_eval.wgsl の shade_light が occlusion を使わないため。deferred_lighting.wgsl 参照）。
//
//  ## 半解像度採用の根拠
//  フル解像度 AO はコスト 4 倍で 35W GPU 予算に見合わない。AO は低周波信号なので、
//  半解像度で計算し Filtering サンプラーでバイリニアアップサンプルすれば実用十分。
//  RT-AO も同理由で半解像度（SSAO と出力先・ブラー・合成を共有するため）。
//
//  ## テクスチャ所有（RtPool を使わない理由）
//  ao_a/ao_b は imos ブラーの storage ping-pong（STORAGE_BINDING）で、RtPool は
//  RENDER_ATTACHMENT|TEXTURE_BINDING 固定のため使えない。よって AoTargets が専有し、
//  半解像度サイズ追従（ensure）も自前で行う（RtPool.ensure が手本）。
//
//  ## フォーマット（Rgba16Float 単一チャンネル運用・重要）
//  ao_raw/ao_a/ao_b/white はすべて AO_FORMAT=Rgba16Float。単一チャンネル R16Float は
//  core WebGPU で「storage 不可（ao_a/ao_b が要求）」かつ「R32Float は filterable 不可
//  （バイリニアアップサンプルが要求）」という二律背反に陥るため使えない。Rgba16Float は
//  storage・filterable・render-attachment のすべてを core 保証で満たす唯一の現実解で、
//  .r のみを AO 値として使う（imos_blur.rs のフォーマット解説と一致）。
// ============================================================

use super::deferred::DeferredLightingPipelines;
use super::imos_blur::{IMOS_BLUR_FORMAT, ImosBlur};
use super::pipeline::get_shader_source;

// ─── 定数 ────────────────────────────────────────────────────

/// AO テクスチャ（raw/a/b/white）の共通フォーマット。imos の storage フォーマットと一致必須。
/// Rgba16Float（storage 可・filterable 可）。.r のみを AO 値として使う（モジュール冒頭参照）。
pub const AO_FORMAT: wgpu::TextureFormat = IMOS_BLUR_FORMAT;

/// AO 計算解像度の分母（フル解像度に対する 1/N）。2＝半解像度（コスト 1/4）。
pub const AO_RESOLUTION_DIVISOR: u32 = 2;

/// いもす法ブラーの反復回数（H→V を 1 反復。多いほど滑らか＝ガウシアン近似が良くなる）。
pub const AO_BLUR_ITERATIONS: u32 = 2;

/// いもす法ブラーのボックス半径（半解像度画素）。AO の粒状ノイズを均す幅。
pub const AO_BLUR_RADIUS: i32 = 2;

/// SSAO のサンプル球半径（世界単位）。AoParams.radius へ書き込む（近傍遮蔽の探索範囲）。
pub const AO_SSAO_WORLD_RADIUS: f32 = 0.5;

/// RT-AO の遮蔽レイ最大距離（世界単位, tmax）。AoParams.radius へ書き込む（近接遮蔽の範囲）。
pub const AO_RTAO_WORLD_RADIUS: f32 = 1.0;

/// AO 強度の既定（PostFxSettings.ao_intensity の初期値。1.0＝定数どおりの効き）。
pub const DEFAULT_AO_INTENSITY: f32 = 1.0;

// ─── AoParams（group2 binding0 uniform, 16B）─────────────────

/// AO パスの数値パラメータ UBO（WGSL ao_common.wgsl の AoParams と一致・16B）。
///
/// | offset | field     | size |
/// |--------|-----------|------|
/// |   0    | intensity |   4  |
/// |   4    | radius    |   4  |
/// |   8    | _pad0..1  |   8  |
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct AoParams {
    /// AO 寄与の全体倍率（ao_intensity ノブ）。
    pub intensity: f32,
    /// AO の世界単位半径（SSAO=サンプル球半径 / RT=レイ tmax）。方式ごとの定数を書く。
    pub radius: f32,
    pub _pad0: f32,
    pub _pad1: f32,
}

impl AoParams {
    /// intensity/radius から UBO 値を作る。
    pub fn new(intensity: f32, radius: f32) -> Self {
        Self {
            intensity,
            radius,
            _pad0: 0.0,
            _pad1: 0.0,
        }
    }
}

// ─── AoPipelines（静的リソース: パイプライン・BGL・UBO・ブラー・サンプラー・白）───

/// AO パイプライン一式（SSAO 常時・RT-AO は対応 GPU のみ・いもすブラー・共有リソース）。
///
/// 静的（起動時 1 回構築・不変）なので DrawPipelines が保持する。半解像度テクスチャは
/// サイズ追従が要るため AoTargets（App 側）が別に持つ。
pub struct AoPipelines {
    /// SSAO パイプライン（RAY_QUERY 不要・常に構築）。
    pub ssao: wgpu::RenderPipeline,
    /// RT-AO パイプライン（RAY_QUERY 必須・RT 対応 GPU でのみ Some）。
    pub rt: Option<wgpu::RenderPipeline>,
    /// group2: AoParams のレイアウト。
    pub params_bgl: wgpu::BindGroupLayout,
    /// group3: RT-AO の TLAS レイアウト（RT 用）。
    pub rt_bgl: wgpu::BindGroupLayout,
    /// AoParams UBO（毎フレーム intensity/radius を書き込む）。
    pub params_buffer: wgpu::Buffer,
    /// いもす法ブラー基盤（ao_raw → ao_a/ao_b を均す）。
    pub imos: ImosBlur,
    /// 半解像度 AO をフル解像度へバイリニアアップサンプルするための Filtering サンプラー。
    pub linear_sampler: wgpu::Sampler,
    /// AO=Off 時・AO 生成パスの t_ao スロット用の白 1x1（R=1＝AO 無効果）。永続。
    #[allow(dead_code)]
    white_tex: wgpu::Texture,
    /// 白 1x1 のビュー（deferred / reflection の group1 binding6 へ Off 時に渡す）。
    pub white_view: wgpu::TextureView,
}

impl AoPipelines {
    /// AO パイプライン一式を構築する。
    ///
    /// - `deferred` : group0/1（camera/gbuffer）の BGL を借用する（8 entry gbuffer_bgl。AO シェーダは
    ///                group1 の 0..5 のみ宣言するため subset として合法）。
    /// - `queue`    : 白 1x1 の初期化書き込みに使う（Rgba16Float, R=1）。
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        deferred: &DeferredLightingPipelines,
        cache: Option<&wgpu::PipelineCache>,
    ) -> Self {
        // group2: AoParams uniform（binding0）。
        let params_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("AO Params BGL (group2)"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size: None,
                },
                count: None,
            }],
        });
        // group3: RT-AO の TLAS（acceleration_structure, binding0）。
        let rt_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("AO RT BGL (group3)"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::AccelerationStructure {
                    vertex_return: false,
                },
                count: None,
            }],
        });

        // SSAO パイプライン（group0..2, blend なし＝ao_raw を上書き）。
        let ssao = build_ao_pipeline(
            device,
            cache,
            &[&deferred.camera_bgl, &deferred.gbuffer_bgl, &params_bgl],
            &["ao_common.wgsl", "ao_ssao.wgsl"],
            "fs_ssao",
            "ao_ssao",
        );
        // RT-AO パイプライン（group0..3, RT 対応 GPU のみ）。
        let rt = if super::rt_shadow::rt_shadows_supported() {
            Some(build_ao_pipeline(
                device,
                cache,
                &[
                    &deferred.camera_bgl,
                    &deferred.gbuffer_bgl,
                    &params_bgl,
                    &rt_bgl,
                ],
                &["ao_common.wgsl", "ao_rt.wgsl"],
                "fs_rt",
                "ao_rt",
            ))
        } else {
            None
        };

        // AoParams UBO（毎フレーム write_params で更新）。
        let params_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("AO Params UBO"),
            size: std::mem::size_of::<AoParams>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let imos = ImosBlur::new(device, cache);

        // 半解像度 → フル解像度のバイリニアアップサンプル用 Filtering サンプラー。
        let linear_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("AO Linear Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        // 白 1x1（R=1）。AO=Off 時と AO 生成パスの t_ao スロットを埋める。
        // Rgba16Float = 4×f16。R=1.0 の f16 は 0x3C00（LE: 00 3C）。A も 1.0 にする。
        let white_tex = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("AO White 1x1"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: AO_FORMAT,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        // R=1, G=0, B=0, A=1 の f16 バイト列（8 バイト）。
        let white_px: [u8; 8] = [0x00, 0x3C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3C];
        queue.write_texture(
            white_tex.as_image_copy(),
            &white_px,
            wgpu::ImageDataLayout {
                offset: 0,
                bytes_per_row: Some(8),
                rows_per_image: Some(1),
            },
            wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
        );
        let white_view = white_tex.create_view(&wgpu::TextureViewDescriptor::default());

        Self {
            ssao,
            rt,
            params_bgl,
            rt_bgl,
            params_buffer,
            imos,
            linear_sampler,
            white_tex,
            white_view,
        }
    }

    /// intensity/radius を AoParams UBO へ書き込む（毎フレーム AO パス直前に呼ぶ）。
    pub fn write_params(&self, queue: &wgpu::Queue, intensity: f32, radius: f32) {
        queue.write_buffer(
            &self.params_buffer,
            0,
            bytemuck::bytes_of(&AoParams::new(intensity, radius)),
        );
    }

    /// group2（AoParams）の BindGroup を生成する。
    pub fn create_params_bg(&self, device: &wgpu::Device) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("AO Params BG"),
            layout: &self.params_bgl,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: self.params_buffer.as_entire_binding(),
            }],
        })
    }

    /// group3（RT-AO の TLAS）の BindGroup を生成する（RT-AO 時のみ）。
    pub fn create_rt_bg(&self, device: &wgpu::Device, tlas: &wgpu::Tlas) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("AO RT BG"),
            layout: &self.rt_bgl,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: wgpu::BindingResource::AccelerationStructure(tlas),
            }],
        })
    }

    /// ao_raw → (ao_a/ao_b ping-pong) をいもす法でブラーする。結果は必ず ao_b に残る。
    pub fn blur(
        &self,
        device: &wgpu::Device,
        encoder: &mut wgpu::CommandEncoder,
        targets: &AoTargets,
    ) {
        self.imos.record(
            device,
            encoder,
            targets.raw_view(),
            targets.a_view(),
            targets.b_view(),
            targets.width() as i32,
            targets.height() as i32,
            AO_BLUR_RADIUS,
            AO_BLUR_ITERATIONS,
        );
    }
}

/// AO フルスクリーンパイプラインを 1 本構築する（reflection.rs の build_reflection_pipeline 相当）。
/// 深度なし・単一カラーターゲット（AO_FORMAT）・blend なし。頂点は ao_common.wgsl の vs_fullscreen。
fn build_ao_pipeline(
    device: &wgpu::Device,
    cache: Option<&wgpu::PipelineCache>,
    bgls: &[&wgpu::BindGroupLayout],
    shader_sources: &[&str],
    fs_entry: &str,
    label: &str,
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
            entry_point: Some("vs_fullscreen"),
            buffers: &[],
            compilation_options: Default::default(),
        },
        fragment: Some(wgpu::FragmentState {
            module: &shader,
            entry_point: Some(fs_entry),
            targets: &[Some(wgpu::ColorTargetState {
                format: AO_FORMAT,
                blend: None,
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

// ─── AoTargets（動的リソース: 半解像度 ao_raw / ao_a / ao_b。App が保持）───

/// 半解像度 AO テクスチャ 3 枚（ao_raw=生成出力 / ao_a,ao_b=いもすブラー ping-pong）。
///
/// STORAGE_BINDING を要するため RtPool には載せられず、本構造体が専有する。
/// 毎フレーム ensure で半解像度サイズに追従する（RtPool.ensure が手本）。
/// ブラー後の最終 AO は必ず ao_b に残る（imos_blur.rs / AoPipelines::blur の不変条件）。
pub struct AoTargets {
    /// AO 生成パスの出力（RENDER_ATTACHMENT|TEXTURE_BINDING）。
    raw: Option<(wgpu::Texture, wgpu::TextureView)>,
    /// いもすブラー ping-pong その 1（STORAGE_BINDING|TEXTURE_BINDING）。
    a: Option<(wgpu::Texture, wgpu::TextureView)>,
    /// いもすブラー ping-pong その 2（同上）。最終 AO はここに残る。
    b: Option<(wgpu::Texture, wgpu::TextureView)>,
    /// 確保済み半解像度サイズ（不一致で再確保）。
    hw: u32,
    hh: u32,
}

impl Default for AoTargets {
    fn default() -> Self {
        Self::new()
    }
}

impl AoTargets {
    /// 空の（未確保の）ターゲット群を生成する（device 不要・eager 構築可）。
    pub fn new() -> Self {
        Self {
            raw: None,
            a: None,
            b: None,
            hw: 0,
            hh: 0,
        }
    }

    /// 半解像度サイズへ追従する。既存が同サイズなら何もしない（RtPool.ensure の流儀）。
    /// `hw`/`hh` は半解像度（呼び出し側で (surf/AO_RESOLUTION_DIVISOR).max(1) 済み）。
    pub fn ensure(&mut self, device: &wgpu::Device, hw: u32, hh: u32) {
        let hw = hw.max(1);
        let hh = hh.max(1);
        if self.raw.is_some() && self.hw == hw && self.hh == hh {
            return;
        }
        // ao_raw: 生成パスのレンダー出力＋ブラー入力。
        let raw = make_tex(
            device,
            "ao_raw",
            hw,
            hh,
            wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
        );
        // ao_a / ao_b: いもすブラーの storage ping-pong（write）＋次段/サンプル入力。
        let storage = wgpu::TextureUsages::STORAGE_BINDING | wgpu::TextureUsages::TEXTURE_BINDING;
        let a = make_tex(device, "ao_a", hw, hh, storage);
        let b = make_tex(device, "ao_b", hw, hh, storage);
        self.raw = Some(raw);
        self.a = Some(a);
        self.b = Some(b);
        self.hw = hw;
        self.hh = hh;
    }

    /// 確保済み半解像度幅。
    pub fn width(&self) -> u32 {
        self.hw
    }
    /// 確保済み半解像度高さ。
    pub fn height(&self) -> u32 {
        self.hh
    }

    /// ao_raw のビュー（ensure 済みであること）。
    pub fn raw_view(&self) -> &wgpu::TextureView {
        &self
            .raw
            .as_ref()
            .expect("AoTargets: ensure 未実行（raw）")
            .1
    }
    /// ao_a のビュー（ensure 済みであること）。
    pub fn a_view(&self) -> &wgpu::TextureView {
        &self.a.as_ref().expect("AoTargets: ensure 未実行（a）").1
    }
    /// ao_b のビュー＝ブラー後の最終 AO（ensure 済みであること）。
    pub fn b_view(&self) -> &wgpu::TextureView {
        &self.b.as_ref().expect("AoTargets: ensure 未実行（b）").1
    }
}

/// AO テクスチャ 1 枚を確保する（AO_FORMAT 固定・指定 usage）。
fn make_tex(
    device: &wgpu::Device,
    label: &str,
    w: u32,
    h: u32,
    usage: wgpu::TextureUsages,
) -> (wgpu::Texture, wgpu::TextureView) {
    let tex = device.create_texture(&wgpu::TextureDescriptor {
        label: Some(label),
        size: wgpu::Extent3d {
            width: w,
            height: h,
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: AO_FORMAT,
        usage,
        view_formats: &[],
    });
    let view = tex.create_view(&wgpu::TextureViewDescriptor::default());
    (tex, view)
}

// ============================================================
//  WGSL 静的検証（naga parse + validate）＋レイアウト照合
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// AoParams は 16 バイト（WGSL ao_common.wgsl の AoParams と一致）。
    #[test]
    fn ao_params_is_16_bytes() {
        assert_eq!(std::mem::size_of::<AoParams>(), 16);
    }

    /// SSAO 連結（ao_common + ao_ssao, RAY_QUERY 不要）を naga で parse + validate する。
    #[test]
    fn ao_ssao_shader_parses() {
        let common = include_str!("shaders/ao_common.wgsl");
        let ssao = include_str!("shaders/ao_ssao.wgsl");
        let src = [common, ssao].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[ao_ssao] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        v.validate(&module)
            .unwrap_or_else(|e| panic!("[ao_ssao] validate 失敗: {e:?}"));
    }

    /// RT-AO 連結（ao_common + ao_rt, RAY_QUERY 必須）を naga で parse + validate する。
    #[test]
    fn ao_rt_shader_parses() {
        let common = include_str!("shaders/ao_common.wgsl");
        let rt = include_str!("shaders/ao_rt.wgsl");
        let src = [common, rt].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[ao_rt] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::RAY_QUERY,
        );
        v.validate(&module)
            .unwrap_or_else(|e| panic!("[ao_rt] validate 失敗: {e:?}"));
    }

    /// AoParams のワールド半径定数が方式ごとに正の有限値であること（マジックナンバー回帰防止）。
    #[test]
    fn ao_world_radii_are_positive() {
        assert!(AO_SSAO_WORLD_RADIUS > 0.0 && AO_SSAO_WORLD_RADIUS.is_finite());
        assert!(AO_RTAO_WORLD_RADIUS > 0.0 && AO_RTAO_WORLD_RADIUS.is_finite());
        assert_eq!(
            AO_FORMAT, IMOS_BLUR_FORMAT,
            "AO と imos の storage フォーマットは一致必須"
        );
    }
}
