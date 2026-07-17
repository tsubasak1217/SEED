// ============================================================
//  ssgi.rs — スクリーンスペース GI（SSGI）パイプライン＋半解像度リソース（Phase SSGI）
//
//  ## 役割（単一責任）
//  Deferred（G-Buffer）有効かつ GI モードが Ssgi のときだけ走る独立フルスクリーン SSGI パスの
//  パイプライン一式（生成＋いもす法カラーブラー）と、半解像度リソース・SsgiParams UBO を持つ。
//  手本は ao.rs（AO のカラー版）と reflection.rs（scene_hdr 入力）。
//
//  ## パス順（frame_renderer が制御。1 フレーム遅延方式）
//    G-Buffer 書き込み → デファードライティング（t_ssgi は前フレームの ssgi_b を読む）
//      → SSGI 生成パス（今フレームの scene_hdr＋G-Buffer → 半解像度 ssgi_raw）
//      → いもす法カラーブラー（ssgi_raw → ssgi_a/ssgi_b、結果 ssgi_b が次フレーム用）
//    evaluate_gi_ambient は同じライティングパス内にあるため今フレームの HDR をすぐ使えない。
//    ゆえに 1 フレーム遅延（標準的妥協）。初回/リサイズの未収束フレームは frame_renderer が
//    GiParams を enabled=false でフラットに倒す（SsgiTargets::ensure の戻り値で検知）。
//
//  ## HDR 読み書き分離（反射と同じ制約）
//  SSGI は半解像度 ssgi_raw（別テクスチャ）へ出力し scene_hdr はサンプル入力として読む。
//  ## フォーマット: SSGI_FORMAT=Rgba16Float（.rgb を使う）。imos_blur は .rgb 集計対応済み。
// ============================================================

use super::deferred::DeferredLightingPipelines;
use super::imos_blur::{ImosBlur, IMOS_BLUR_FORMAT};
use super::pipeline::get_shader_source;

/// SSGI テクスチャ（raw/a/b）の共通フォーマット。いもすの storage フォーマットと一致必須。
pub const SSGI_FORMAT: wgpu::TextureFormat = IMOS_BLUR_FORMAT;
/// SSGI 計算解像度の分母（フル解像度に対する 1/N）。2＝半解像度（コスト 1/4）。
pub const SSGI_RESOLUTION_DIVISOR: u32 = 2;
/// いもす法カラーブラーの反復回数。SSGI はノイズが乗るため AO より 1 多い 3 反復。
pub const SSGI_BLUR_ITERATIONS: u32 = 3;
/// いもす法カラーブラーのボックス半径（半解像度画素）。
pub const SSGI_BLUR_RADIUS: i32 = 2;

/// SSGI パスの数値パラメータ UBO（WGSL ssgi_common.wgsl の SsgiParams と一致・16B）。
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct SsgiParams {
    /// ミス埋め用フラットアンビエント放射照度（リニア RGB＝ambient_color*ambient_intensity）。
    pub ambient: [f32; 3],
    pub _pad0:   f32,
}
impl SsgiParams {
    /// フラットアンビエント色から UBO 値を作る。
    pub fn new(ambient: [f32; 3]) -> Self { Self { ambient, _pad0: 0.0 } }
}

/// SSGI パイプライン一式（生成・いもす法カラーブラー・共有リソース）。起動時 1 回構築・不変。
pub struct SsgiPipelines {
    /// SSGI 生成パイプライン（RAY_QUERY 不要・常に構築）。
    pub gen_pipeline:   wgpu::RenderPipeline,
    /// group2: SsgiParams uniform + scene_hdr tex + sampler のレイアウト。
    pub input_bgl:      wgpu::BindGroupLayout,
    /// SsgiParams UBO（毎フレーム ambient を書き込む）。
    pub params_buffer:  wgpu::Buffer,
    /// いもす法カラーブラー基盤（ssgi_raw → ssgi_a/ssgi_b を均す）。
    pub imos:           ImosBlur,
    /// 半解像度 SSGI をフル解像度へバイリニアアップサンプルする Filtering サンプラー
    /// （deferred の group1 binding9=s_ssgi に渡す。生成パスの scene_hdr 採取にも使う）。
    pub linear_sampler: wgpu::Sampler,
    /// SSGI 非使用時に deferred の t_ssgi スロットを埋めるダミー 1x1（黒。永続）。
    #[allow(dead_code)]
    dummy_tex:          wgpu::Texture,
    /// ダミー 1x1 のビュー（deferred の group1 binding8 へ SSGI 非使用時に渡す）。
    pub dummy_view:     wgpu::TextureView,
}

impl SsgiPipelines {
    /// SSGI パイプライン一式を構築する。
    /// - `deferred`: group0/1（camera/gbuffer）の BGL を借用（10 entry gbuffer_bgl。SSGI 生成は
    ///   group1 の 0..5 のみ宣言＝subset 合法）。 - `queue`: ダミー 1x1 の初期化書き込み（黒）。
    pub fn new(
        device:   &wgpu::Device,
        queue:    &wgpu::Queue,
        deferred: &DeferredLightingPipelines,
        cache:    Option<&wgpu::PipelineCache>,
    ) -> Self {
        let vis = wgpu::ShaderStages::FRAGMENT;
        // group2: SsgiParams uniform（0）＋ scene_hdr（1, filterable）＋ sampler（2, filtering）。
        let input_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("SSGI Input BGL (group2)"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0, visibility: vis,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false, min_binding_size: None,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1, visibility: vis,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float { filterable: true },
                        view_dimension: wgpu::TextureViewDimension::D2, multisampled: false,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 2, visibility: vis,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
            ],
        });
        // 生成パイプライン（group0..2, blend なし＝ssgi_raw を上書き）。
        let gen_pipeline = build_ssgi_pipeline(
            device, cache,
            &[&deferred.camera_bgl, &deferred.gbuffer_bgl, &input_bgl],
            &["ssgi_common.wgsl", "ssgi_gen.wgsl"],
            "fs_ssgi", "ssgi_gen",
        );
        let params_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("SSGI Params UBO"),
            size:  std::mem::size_of::<SsgiParams>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let imos = ImosBlur::new(device, cache);
        let linear_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("SSGI Linear Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::FilterMode::Nearest,
            ..Default::default()
        });
        // ダミー 1x1（黒）。SSGI 非使用時の t_ssgi スロットを埋める（GiParams.gi_mode で不参照）。
        let dummy_tex = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("SSGI Dummy 1x1"),
            size: wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
            mip_level_count: 1, sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: SSGI_FORMAT,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        let dummy_px: [u8; 8] = [0u8; 8]; // rgba16f 全 0
        queue.write_texture(
            dummy_tex.as_image_copy(), &dummy_px,
            wgpu::ImageDataLayout { offset: 0, bytes_per_row: Some(8), rows_per_image: Some(1) },
            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
        );
        let dummy_view = dummy_tex.create_view(&wgpu::TextureViewDescriptor::default());
        Self { gen_pipeline, input_bgl, params_buffer, imos, linear_sampler, dummy_tex, dummy_view }
    }

    /// ambient（ミス埋め色）を SsgiParams UBO へ書き込む（毎フレーム SSGI パス直前に呼ぶ）。
    pub fn write_params(&self, queue: &wgpu::Queue, ambient: [f32; 3]) {
        queue.write_buffer(&self.params_buffer, 0, bytemuck::bytes_of(&SsgiParams::new(ambient)));
    }

    /// group2（input）の BindGroup を生成する（params UBO + scene_hdr + sampler）。
    pub fn create_input_bg(&self, device: &wgpu::Device, scene_hdr: &wgpu::TextureView) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("SSGI Input BG"),
            layout: &self.input_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: self.params_buffer.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::TextureView(scene_hdr) },
                wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::Sampler(&self.linear_sampler) },
            ],
        })
    }

    /// ssgi_raw → (ssgi_a/ssgi_b ping-pong) をいもす法カラーブラーする。結果は必ず ssgi_b に残る。
    pub fn blur(&self, device: &wgpu::Device, encoder: &mut wgpu::CommandEncoder, targets: &SsgiTargets) {
        self.imos.record(
            device, encoder,
            targets.raw_view(), targets.a_view(), targets.b_view(),
            targets.width() as i32, targets.height() as i32,
            SSGI_BLUR_RADIUS, SSGI_BLUR_ITERATIONS,
        );
    }
}

/// SSGI フルスクリーンパイプラインを 1 本構築する（ao.rs の build_ao_pipeline 相当）。
/// 深度なし・単一カラーターゲット（SSGI_FORMAT）・blend なし。頂点は ssgi_common.wgsl の vs_fullscreen。
fn build_ssgi_pipeline(
    device:         &wgpu::Device,
    cache:          Option<&wgpu::PipelineCache>,
    bgls:           &[&wgpu::BindGroupLayout],
    shader_sources: &[&str],
    fs_entry:       &str,
    label:          &str,
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
            module: &shader, entry_point: Some("vs_fullscreen"),
            buffers: &[], compilation_options: Default::default(),
        },
        fragment: Some(wgpu::FragmentState {
            module: &shader, entry_point: Some(fs_entry),
            targets: &[Some(wgpu::ColorTargetState {
                format: SSGI_FORMAT, blend: None, write_mask: wgpu::ColorWrites::ALL,
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

/// 半解像度 SSGI テクスチャ 3 枚（ssgi_raw / ssgi_a / ssgi_b）。App が保持・フレーム跨ぎ永続。
/// AoTargets と同型だが .rgb（カラー）を扱う。ブラー後の最終 SSGI は必ず ssgi_b に残り永続する
/// （1 フレーム遅延方式）。ensure は半解像度サイズに追従し、再確保（履歴リセット）を戻り値で通知する。
pub struct SsgiTargets {
    raw: Option<(wgpu::Texture, wgpu::TextureView)>,
    a:   Option<(wgpu::Texture, wgpu::TextureView)>,
    b:   Option<(wgpu::Texture, wgpu::TextureView)>,
    hw:  u32,
    hh:  u32,
}
impl Default for SsgiTargets {
    fn default() -> Self { Self::new() }
}
impl SsgiTargets {
    /// 空の（未確保の）ターゲット群を生成する（device 不要・eager 構築可）。
    pub fn new() -> Self { Self { raw: None, a: None, b: None, hw: 0, hh: 0 } }

    /// 半解像度サイズへ追従する。既存が同サイズなら何もしない（AoTargets.ensure の流儀）。
    /// 戻り値: 再確保（初回 or サイズ変更）が起きたら true（＝ssgi_b 履歴リセット＝1 フレーム未収束）。
    pub fn ensure(&mut self, device: &wgpu::Device, hw: u32, hh: u32) -> bool {
        let hw = hw.max(1);
        let hh = hh.max(1);
        if self.raw.is_some() && self.hw == hw && self.hh == hh {
            return false;
        }
        let raw = make_tex(device, "ssgi_raw", hw, hh,
            wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING);
        let storage = wgpu::TextureUsages::STORAGE_BINDING | wgpu::TextureUsages::TEXTURE_BINDING;
        let a = make_tex(device, "ssgi_a", hw, hh, storage);
        let b = make_tex(device, "ssgi_b", hw, hh, storage);
        self.raw = Some(raw); self.a = Some(a); self.b = Some(b);
        self.hw = hw; self.hh = hh;
        true
    }
    /// 確保済み半解像度幅。
    pub fn width(&self) -> u32 { self.hw }
    /// 確保済み半解像度高さ。
    pub fn height(&self) -> u32 { self.hh }
    /// ssgi_raw のビュー（ensure 済みであること）。
    pub fn raw_view(&self) -> &wgpu::TextureView {
        &self.raw.as_ref().expect("SsgiTargets: ensure 未実行（raw）").1
    }
    /// ssgi_a のビュー（ensure 済みであること）。
    pub fn a_view(&self) -> &wgpu::TextureView {
        &self.a.as_ref().expect("SsgiTargets: ensure 未実行（a）").1
    }
    /// ssgi_b のビュー＝ブラー後の最終 SSGI（前フレームの結果を保持）。
    pub fn b_view(&self) -> &wgpu::TextureView {
        &self.b.as_ref().expect("SsgiTargets: ensure 未実行（b）").1
    }
}

/// SSGI テクスチャ 1 枚を確保する（SSGI_FORMAT 固定・指定 usage）。
fn make_tex(device: &wgpu::Device, label: &str, w: u32, h: u32, usage: wgpu::TextureUsages)
    -> (wgpu::Texture, wgpu::TextureView) {
    let tex = device.create_texture(&wgpu::TextureDescriptor {
        label: Some(label),
        size: wgpu::Extent3d { width: w, height: h, depth_or_array_layers: 1 },
        mip_level_count: 1, sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: SSGI_FORMAT, usage, view_formats: &[],
    });
    let view = tex.create_view(&wgpu::TextureViewDescriptor::default());
    (tex, view)
}

// ── WGSL 静的検証（naga parse + validate）＋レイアウト照合 ──
#[cfg(test)]
mod tests {
    use super::*;

    /// SsgiParams は 16 バイト（WGSL ssgi_common.wgsl の SsgiParams と一致）。
    #[test]
    fn ssgi_params_is_16_bytes() {
        assert_eq!(std::mem::size_of::<SsgiParams>(), 16);
        assert_eq!(SSGI_FORMAT, IMOS_BLUR_FORMAT, "SSGI と imos の storage フォーマットは一致必須");
    }

    /// SSGI 連結（ssgi_common + ssgi_gen, RAY_QUERY 不要）を naga で parse + validate する。
    #[test]
    fn ssgi_gen_shader_parses() {
        let common = include_str!("shaders/ssgi_common.wgsl");
        let gen_src    = include_str!("shaders/ssgi_gen.wgsl");
        let src = [common, gen_src].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[ssgi_gen] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(), naga::valid::Capabilities::empty());
        v.validate(&module).unwrap_or_else(|e| panic!("[ssgi_gen] validate 失敗: {e:?}"));
    }

    /// WGSL 側 SsgiParams の naga 計算サイズが Rust と一致すること（回帰ガード）。
    #[test]
    fn wgsl_ssgi_params_size_matches_rust() {
        let src = include_str!("shaders/ssgi_common.wgsl");
        let module = naga::front::wgsl::parse_str(src).expect("ssgi_common.wgsl の parse に失敗");
        let (handle, _) = module.types.iter()
            .find(|(_, t)| t.name.as_deref() == Some("SsgiParams"))
            .expect("WGSL に struct SsgiParams が見つかりません");
        let mut layouter = naga::proc::Layouter::default();
        layouter.update(module.to_ctx()).expect("naga Layouter の計算に失敗");
        assert_eq!(layouter[handle].size as usize, std::mem::size_of::<SsgiParams>(),
            "WGSL の SsgiParams サイズが Rust と一致しません");
    }
}
