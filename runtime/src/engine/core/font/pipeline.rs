// ============================================================
//  font/pipeline.rs — テキスト wgpu パイプライン
//
//  Group 0 : グリフアトラス (R8Unorm の SDF / Rgba8Unorm の MTSDF) + サンプラー
//
//  描画は距離場（旧 Bitmap モードは廃止したので uniform も不要になった）。
//  距離場の種類でフラグメントシェーダーの入口を選ぶ（text.wgsl の fs_sdf / fs_mtsdf。頂点の形は共通）。
//  文字色・縁取り色・縁取り距離はすべて頂点属性で運ぶため、
//  1 バッチの中でテキストごとに違う縁取りを混在させられる。
//
//  アルファブレンディング有効、深度テスト無し（UI オーバーレイ用）。
// ============================================================

// ── 頂点型 ────────────────────────────────────────────────────

/// テキスト頂点。
/// `position` は NDC (-1..1) のスクリーン空間座標。
///
/// `outline_dist` は「SDF のエッジ(0.5) から外側へ何テクスチャ単位ぶん塗るか」。
/// 0 以下 = 縁取りなし。クアッド内では定数なので補間しても値は変わらない。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct TextVertex {
    pub position: [f32; 3],
    pub uv: [f32; 2],
    pub color: [f32; 4],
    pub outline_color: [f32; 4],
    pub outline_dist: f32,
    /// 太さ調整（SDF テクスチャ単位）。正で太く・負で細くなる。
    ///
    /// シェーダーは「エッジ = 0.5 − weight_dist」としてしきい値をずらす。
    /// 0 のとき従来とビット互換（しきい値 0.5 のまま）。
    pub weight_dist: f32,
    /// 追加のスムース幅（SDF テクスチャ単位）。ドロップシャドウのぼかしに使う。
    ///
    /// fwidth 由来のアンチエイリアス幅へ加算する。0 のとき従来とビット互換。
    pub softness: f32,
    /// このグリフの距離場の解像度（em あたりのテクセル数。SDF は 64、MTSDF は 40〜64）。
    ///
    /// シェーダーは「1 テクセルあたりの値の変化 = 0.5 ÷ (0.125 em × これ)」と「画面の上の文字の大きさ = これ ÷ 1 画素のテクセル数」
    /// を求める（2026-10-02。字ごとに解像度が違ってよい）。
    pub field_em: f32,
}

// ── 頂点属性のオフセット（マジックナンバーをここへ集約する）────

/// position (vec3) のバイトオフセット。
const ATTR_OFFSET_POSITION: u64 = 0;
/// uv (vec2) のバイトオフセット。
const ATTR_OFFSET_UV: u64 = 12;
/// color (vec4) のバイトオフセット。
const ATTR_OFFSET_COLOR: u64 = 20;
/// outline_color (vec4) のバイトオフセット。
const ATTR_OFFSET_OUTLINE_COLOR: u64 = 36;
/// outline_dist (f32) のバイトオフセット。
const ATTR_OFFSET_OUTLINE_DIST: u64 = 52;
/// weight_dist (f32) のバイトオフセット。
const ATTR_OFFSET_WEIGHT_DIST: u64 = 56;
/// softness (f32) のバイトオフセット。
const ATTR_OFFSET_SOFTNESS: u64 = 60;
/// field_em (f32) のバイトオフセット。
const ATTR_OFFSET_FIELD_EM: u64 = 64;

// ── TextPipeline ──────────────────────────────────────────────

/// テキスト描画用の wgpu パイプライン一式（レンダーパイプライン・バインドグループレイアウト・サンプラー）。
pub struct TextPipeline {
    pub pipeline: wgpu::RenderPipeline,
    pub atlas_bgl: wgpu::BindGroupLayout,
    pub sampler: wgpu::Sampler,
}

/// 距離場の種類ごとのフラグメントシェーダーの入口（text.wgsl）。
fn fragment_entry(kind: super::glyph_field::DistanceFieldKind) -> &'static str {
    match kind {
        super::glyph_field::DistanceFieldKind::Sdf => "fs_sdf",
        super::glyph_field::DistanceFieldKind::Mtsdf => "fs_mtsdf",
    }
}

impl TextPipeline {
    pub fn new(
        device: &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format: wgpu::TextureFormat,
        kind: super::glyph_field::DistanceFieldKind,
    ) -> Self {
        // ── シェーダー ────────────────────────────────────────
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("Text Shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../renderer/shaders/text.wgsl").into()),
        });

        // ── BGL: Group 0 — Atlas テクスチャ + サンプラー ───────
        let atlas_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Text Atlas BGL"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float { filterable: true },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled: false,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
            ],
        });

        // ── パイプラインレイアウト ─────────────────────────────
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("Text Pipeline Layout"),
            bind_group_layouts: &[&atlas_bgl],
            push_constant_ranges: &[],
        });

        // ── 頂点バッファレイアウト ─────────────────────────────
        let stride = std::mem::size_of::<TextVertex>() as u64;
        let vertex_layout = wgpu::VertexBufferLayout {
            array_stride: stride,
            step_mode: wgpu::VertexStepMode::Vertex,
            attributes: &[
                // location 0: position (vec3)
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x3,
                    offset: ATTR_OFFSET_POSITION,
                    shader_location: 0,
                },
                // location 1: uv (vec2)
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x2,
                    offset: ATTR_OFFSET_UV,
                    shader_location: 1,
                },
                // location 2: color (vec4)
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x4,
                    offset: ATTR_OFFSET_COLOR,
                    shader_location: 2,
                },
                // location 3: outline_color (vec4)
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x4,
                    offset: ATTR_OFFSET_OUTLINE_COLOR,
                    shader_location: 3,
                },
                // location 4: outline_dist (f32)
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32,
                    offset: ATTR_OFFSET_OUTLINE_DIST,
                    shader_location: 4,
                },
                // location 5: weight_dist (f32) — 太さ調整
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32,
                    offset: ATTR_OFFSET_WEIGHT_DIST,
                    shader_location: 5,
                },
                // location 6: softness (f32) — 影のぼかし幅
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32,
                    offset: ATTR_OFFSET_SOFTNESS,
                    shader_location: 6,
                },
                // location 7: field_em (f32) — このグリフの距離場の解像度（em あたりのテクセル数）
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32,
                    offset: ATTR_OFFSET_FIELD_EM,
                    shader_location: 7,
                },
            ],
        };

        // ── レンダーパイプライン ───────────────────────────────
        // 共有のパイプラインキャッシュ（Renderer が作ったもの。未作成・非対応なら None＝従来どおりキャッシュ無し）。
        let shared_cache = crate::engine::core::renderer::pipeline_cache::shared::shared();
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("Text Render Pipeline"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                buffers: &[vertex_layout],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some(fragment_entry(kind)),
                targets: &[Some(wgpu::ColorTargetState {
                    format: surface_format,
                    blend: Some(wgpu::BlendState::ALPHA_BLENDING),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology: wgpu::PrimitiveTopology::TriangleList,
                strip_index_format: None,
                front_face: wgpu::FrontFace::Ccw,
                cull_mode: None, // 両面描画（文字は裏返ることがある）
                ..Default::default()
            },
            // 深度書き込み・テストなし。メインパスのアタッチメント構成に合わせるためフォーマットのみ指定。
            depth_stencil: Some(wgpu::DepthStencilState {
                format: depth_format,
                depth_write_enabled: false,
                depth_compare: wgpu::CompareFunction::Always,
                stencil: wgpu::StencilState::default(),
                bias: wgpu::DepthBiasState::default(),
            }),
            multisample: wgpu::MultisampleState::default(),
            multiview: None,
            cache: shared_cache.as_ref(),
        });

        // ── サンプラー（バイリニアフィルタリング）─────────────
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("Text Atlas Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        Self {
            pipeline,
            atlas_bgl,
            sampler,
        }
    }
}

// ============================================================
//  ユニットテスト（GPU 不要）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 頂点ストライドと各属性オフセットが構造体レイアウトと一致すること。
    ///
    /// フィールドを増減するとオフセット定数と実体がずれ、
    /// 「色だけおかしい」「文字が消える」といった原因追跡の難しい不具合になる。
    #[test]
    fn vertex_layout_offsets_match_struct() {
        // position(12) + uv(8) + color(16) + outline_color(16)
        //   + outline_dist(4) + weight_dist(4) + softness(4) + field_em(4) = 68
        assert_eq!(std::mem::size_of::<TextVertex>(), 68);
        assert_eq!(ATTR_OFFSET_POSITION, 0);
        assert_eq!(ATTR_OFFSET_UV, 12);
        assert_eq!(ATTR_OFFSET_COLOR, 20);
        assert_eq!(ATTR_OFFSET_OUTLINE_COLOR, 36);
        assert_eq!(ATTR_OFFSET_OUTLINE_DIST, 52);
        assert_eq!(ATTR_OFFSET_WEIGHT_DIST, 56);
        assert_eq!(ATTR_OFFSET_SOFTNESS, 60);
        assert_eq!(ATTR_OFFSET_FIELD_EM, 64);
    }

    /// シェーダーが naga で parse + validate できること。
    ///
    /// WGSL は**実行時**にコンパイルされるので、書き間違いはビルドでは捕まらない。
    /// ここで検証しておかないと「テキストを出した瞬間に落ちる」まで気付けない。
    /// 距離場の 2 種類の入口（fs_sdf / fs_mtsdf）が両方あることも確かめる。
    #[test]
    fn text_shader_parses_and_validates() {
        let src = include_str!("../renderer/shaders/text.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("[text] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        v.validate(&module)
            .unwrap_or_else(|e| panic!("[text] validate 失敗: {e:?}"));
        for kind in [super::super::glyph_field::DistanceFieldKind::Sdf, super::super::glyph_field::DistanceFieldKind::Mtsdf] {
            let entry = fragment_entry(kind);
            assert!(
                module.entry_points.iter().any(|e| e.name == entry && e.stage == naga::ShaderStage::Fragment),
                "text.wgsl に入口 {entry} が無い"
            );
        }
    }

    /// Android の描画の裏側（Vulkan の SPIR-V・GLES の GLSL ES 3.00）へ naga で訳せること（両方の入口）。
    #[test]
    fn text_shader_translates_for_android_backends() {
        let src = include_str!("../renderer/shaders/text.wgsl");
        let module = naga::front::wgsl::parse_str(src).expect("parse");
        let info = naga::valid::Validator::new(naga::valid::ValidationFlags::all(), naga::valid::Capabilities::empty())
            .validate(&module)
            .expect("validate");
        naga::back::spv::write_vec(&module, &info, &naga::back::spv::Options::default(), None).expect("SPIR-V へ訳せる");
        for entry in ["vs_main", "fs_sdf", "fs_mtsdf"] {
            let stage = if entry == "vs_main" { naga::ShaderStage::Vertex } else { naga::ShaderStage::Fragment };
            let options = naga::back::glsl::Options {
                version: naga::back::glsl::Version::Embedded { version: 300, is_webgl: false },
                ..Default::default()
            };
            let pipeline_options = naga::back::glsl::PipelineOptions { shader_stage: stage, entry_point: entry.to_string(), multiview: None };
            let mut out = String::new();
            let mut writer = naga::back::glsl::Writer::new(
                &mut out,
                &module,
                &info,
                &options,
                &pipeline_options,
                naga::proc::BoundsCheckPolicies::default(),
            )
            .unwrap_or_else(|e| panic!("GLSL ES の準備（{entry}）: {e:?}"));
            writer.write().unwrap_or_else(|e| panic!("GLSL ES へ訳す（{entry}）: {e:?}"));
            assert!(!out.is_empty());
        }
    }
}
