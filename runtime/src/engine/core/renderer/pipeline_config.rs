// pipeline_config.rs — WGSL リフレクション + レンダーパイプライン自動構築
//
// ## 使い方
// 1. TOML ファイルで設定を記述する（pipelines/*.toml）
// 2. RenderPipelineBuilder::new(device, toml_str, sf, df).build(get_shader_source)
//    を呼ぶと (RenderPipeline, Vec<BindGroupLayout>) が返る
// 3. 返り値の Vec は bind group 番号順（0, 1, 2, ...）で、pop() で末尾から取り出せる

use std::collections::BTreeMap;

// ============================================================
//  PipelineConfig — TOML 設定構造体
// ============================================================

/// パイプライン定義 TOML（pipelines/*.toml）のデシリアライズ先。
///
/// シェーダー連結・頂点/フラグメントエントリ・頂点スロット・トポロジー/カリング/深度・
/// カラーフォーマット/ブレンドなど、`RenderPipelineBuilder::build` がパイプライン生成に
/// 使う設定一式を保持する。
#[derive(serde::Deserialize)]
pub struct PipelineConfig {
    /// 連結するシェーダーファイル名のリスト（順番に join）
    pub shader_sources:  Vec<String>,
    /// 頂点シェーダーエントリポイント名
    pub vertex_entry:    String,
    /// フラグメントシェーダーエントリポイント名（None → fragment: None）
    pub fragment_entry:  Option<String>,
    /// 頂点バッファスロット名のリスト（pipeline_config::vertex_buffer_layout 参照）
    pub vertex_slots:    Vec<String>,
    /// 生成する BindGroupLayout のスロット数。None → 全リフレクション結果から自動決定。
    /// 値より大きい group 番号は無視され、宣言されていない group は空 BGL で埋める。
    pub num_bind_groups: Option<u32>,

    #[serde(default = "default_topology")]
    pub topology:   String,  // "TriangleList" | "LineList" | "TriangleStrip"
    #[serde(default = "default_cull")]
    pub cull_mode:  String,  // "Back" | "Front" | "None"
    #[serde(default = "default_front_face")]
    pub front_face: String,  // "Ccw" | "Cw"

    #[serde(default = "default_true")]
    pub depth_write:   bool,
    #[serde(default = "default_depth_compare")]
    pub depth_compare: String,  // "Less" | "LessEqual" | "Always" | ...

    /// "surface" → surface_format、"R32Uint" → R32Uint、"none" → fragment: None
    #[serde(default = "default_color_format")]
    pub color_format: String,
    /// "Replace" | "AlphaBlending" | "Additive" | "PremultipliedAlpha" | "WboitBgMultiply" | "None"
    #[serde(default = "default_blend")]
    pub blend:        String,
    /// "all" | "none"
    #[serde(default = "default_write_mask")]
    pub write_mask:   String,

    #[serde(default)]
    pub depth_bias_constant: i32,
    /// slope-scaled 深度バイアス（傾き比例分）。シャドウのアクネ防止に使用。
    #[serde(default)]
    pub depth_bias_slope_scale: f32,
    /// 深度バイアスのクランプ上限（0.0 = 無制限）。
    #[serde(default)]
    pub depth_bias_clamp: f32,

    /// true にするとデプスステンシルアタッチメントなしでパイプラインを生成する。
    /// begin_blit_pass など depth 未使用パスで使用。
    #[serde(default)]
    pub no_depth: bool,
}

fn default_topology()      -> String { "TriangleList".into() }
fn default_cull()          -> String { "Back".into() }
fn default_front_face()    -> String { "Ccw".into() }
fn default_true()          -> bool   { true }
fn default_depth_compare() -> String { "Less".into() }
fn default_color_format()  -> String { "surface".into() }
fn default_blend()         -> String { "Replace".into() }
fn default_write_mask()    -> String { "all".into() }

// ============================================================
//  RenderPipelineBuilder
// ============================================================

/// `PipelineConfig`（TOML）から `wgpu::RenderPipeline` を組み立てるビルダー。
///
/// WGSL リフレクションで BindGroupLayout を自動決定しつつ、depth_write / cull_mode /
/// polygon_mode / stencil をビルダーメソッドで上書きすることで、同一 TOML から
/// 複数のパイプラインバリアント（例: 表裏カリング違い、塗り/ワイヤ違い）を生成できる。
pub struct RenderPipelineBuilder<'d> {
    device:         &'d wgpu::Device,
    cfg:            PipelineConfig,
    surface_format: wgpu::TextureFormat,
    depth_format:   wgpu::TextureFormat,
    stencil:        Option<wgpu::StencilState>,
    /// コンパイル済みパイプラインキャッシュ（Some の場合は再コンパイルをスキップ）。
    cache:          Option<&'d wgpu::PipelineCache>,
    /// パイプラインのデバッグラベル（wgpu の検証エラーメッセージに表示される）。
    /// None の場合は vertex_entry 名（"vs_main" 等）にフォールバックする。
    label:          Option<&'d str>,
    /// depth_write の上書き（Some のとき TOML の depth_write より優先）。
    /// 同一 TOML から depth_write だけ異なる複数バリアントを作る用途（例: スカイボックスの
    /// CameraLocked=false / WorldAnchored=true）。None なら TOML 値を使う。
    depth_write_override: Option<bool>,
    /// cull_mode の上書き（Some のとき TOML の cull_mode より優先）。
    /// マテリアル単位のカリング面（Back / Front / None）を、1 つの TOML から
    /// 3 本のパイプラインバリアントとして生成するために使う（TOML を 3 つに増やさない）。
    /// 値は TOML と同じ文字列表現（"Back" | "Front" | "None"）。
    cull_mode_override: Option<&'d str>,
    /// polygon_mode の上書き（Some のとき既定の Fill より優先）。
    /// 同一 TOML から塗り（Fill）とワイヤ（Line）のバリアントを作るために使う。
    /// Line は Features::POLYGON_MODE_LINE（ネイティブ限定）対応 GPU でのみ有効。
    polygon_mode_override: Option<wgpu::PolygonMode>,
}

impl<'d> RenderPipelineBuilder<'d> {
    /// TOML 文字列から Builder を初期化する。
    pub fn new(
        device:         &'d wgpu::Device,
        toml_src:       &str,
        surface_format: wgpu::TextureFormat,
        depth_format:   wgpu::TextureFormat,
    ) -> Self {
        let cfg: PipelineConfig = toml::from_str(toml_src)
            .expect("invalid pipeline TOML");
        Self { device, cfg, surface_format, depth_format, stencil: None, cache: None, label: None,
               depth_write_override: None, cull_mode_override: None, polygon_mode_override: None }
    }

    /// depth_write を上書きする（TOML の depth_write より優先）。
    /// 同一 TOML から depth_write だけ異なるバリアントを構築する用途に使う。
    pub fn with_depth_write(mut self, enabled: bool) -> Self {
        self.depth_write_override = Some(enabled);
        self
    }

    /// cull_mode を上書きする（TOML の cull_mode より優先）。
    /// `mode` は "Back" | "Front" | "None"（`CullFace::as_str()` が返す表現）。
    /// 同一 TOML からカリング面だけ異なる 3 バリアントを構築する用途に使う。
    pub fn with_cull_mode(mut self, mode: &'d str) -> Self {
        self.cull_mode_override = Some(mode);
        self
    }

    /// polygon_mode を上書きする（既定 Fill より優先）。
    /// 同一 TOML から Fill（塗り）と Line（ワイヤ）のバリアントを構築する用途に使う。
    /// `wgpu::PolygonMode::Line` は Features::POLYGON_MODE_LINE 対応 GPU でのみ使えるため、
    /// 呼び出し側が対応可否（view_mode::wireframe_supported）を確認してから指定すること。
    pub fn with_polygon_mode(mut self, mode: wgpu::PolygonMode) -> Self {
        self.polygon_mode_override = Some(mode);
        self
    }

    /// ステンシルステートを上書き設定する（アウトライン等の特殊用途）。
    pub fn with_stencil(mut self, s: wgpu::StencilState) -> Self {
        self.stencil = Some(s);
        self
    }

    /// デバッグラベルを設定する（検証エラー時にどのパイプラインか特定しやすくする）。
    pub fn with_label(mut self, label: &'d str) -> Self {
        self.label = Some(label);
        self
    }

    /// パイプラインキャッシュを設定する。
    /// キャッシュが有効な場合、2 回目以降の起動でシェーダーコンパイルをスキップできる。
    /// パイプラインキャッシュを設定する。None の場合はキャッシュなし（非対応 GPU 向け）。
    pub fn with_cache(mut self, cache: Option<&'d wgpu::PipelineCache>) -> Self {
        self.cache = cache;
        self
    }

    /// パイプラインを構築して返す。
    ///
    /// `resolve` は shader_sources 中のファイル名を `&'static str` ソースに変換する関数。
    /// 返り値の `Vec<BindGroupLayout>` は group 番号順（0, 1, 2, ...）。
    /// pop() で末尾（最大グループ）から順に取り出せる。
    pub fn build<F>(self, resolve: F) -> (wgpu::RenderPipeline, Vec<wgpu::BindGroupLayout>)
    where
        F: Fn(&str) -> &'static str,
    {
        let Self { device, cfg, surface_format, depth_format, stencil, cache, label,
                   depth_write_override, cull_mode_override, polygon_mode_override } = self;
        // ラベル未指定時は vertex_entry 名を使う（従来の label: None から改善）。
        let pipeline_label = label.unwrap_or(cfg.vertex_entry.as_str());

        // ── 1. シェーダーソースを連結 ──────────────────────────
        let combined: String = cfg.shader_sources.iter()
            .map(|name| resolve(name))
            .collect::<Vec<_>>()
            .join("\n");

        // ── 2. WGSL リフレクション → BGL マップ ─────────────────
        // （シェーダモジュール作成より先に行う。グループ数がデバイスリミットを
        //   超えている場合、wgpu は create_shader_module 時点でパニックするため、
        //   その前に原因が分かるメッセージで検証する）
        let mut reflected = reflect_bgls(device, &combined);

        let num_groups = cfg.num_bind_groups.unwrap_or_else(|| {
            reflected.keys().max().copied().map_or(0, |m| m + 1)
        });

        // ── 3. デバイスリミット検証（再発防止）──────────────────
        // WGSL に @group(N) を追加すると本ビルダーが自動でグループ数を増やすが、
        // デバイスの max_bind_groups（Vulkan の maxBoundDescriptorSets 依存。
        // 5 = group 0〜4 の環境が実在する）を超えるとシェーダモジュール作成で
        // パニックしエディタが起動不能になる。超過時は新グループを足すのではなく
        // 既存グループへ binding を追加して同居させること（例: シャドウは group 4 の
        // binding 2〜5 にライトと同居）。
        let max_groups = device.limits().max_bind_groups;
        assert!(
            num_groups <= max_groups,
            "pipeline '{pipeline_label}' は {num_groups} 個の bind group を要求するが、\
             デバイスの max_bind_groups は {max_groups}。WGSL の @group 番号を \
             {max_groups} 未満に収めること（既存グループへの binding 追加で同居させる）"
        );

        // ── シェーダーモジュール ────────────────────────────────
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some(cfg.vertex_entry.as_str()),
            source: wgpu::ShaderSource::Wgsl(combined.clone().into()),
        });

        // ── 4. 空 BGL で gap を埋めた Vec<BGL> を構築 ─────────
        let all_bgls: Vec<wgpu::BindGroupLayout> = (0..num_groups)
            .map(|g| {
                reflected.remove(&g).unwrap_or_else(|| {
                    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                        label:   Some("Gap BGL"),
                        entries: &[],
                    })
                })
            })
            .collect();

        // ── 5. パイプラインレイアウト ──────────────────────────
        let bgl_refs: Vec<&wgpu::BindGroupLayout> = all_bgls.iter().collect();
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some(cfg.vertex_entry.as_str()),
            bind_group_layouts:   &bgl_refs,
            push_constant_ranges: &[],
        });

        // ── 6. 頂点バッファレイアウト ──────────────────────────
        let vertex_buffers: Vec<wgpu::VertexBufferLayout<'static>> = cfg.vertex_slots.iter()
            .map(|name| vertex_buffer_layout(name))
            .collect();

        // ── 7. カラーターゲット ────────────────────────────────
        let color_target: Option<wgpu::ColorTargetState> = if cfg.color_format == "none" {
            None
        } else {
            let format = match cfg.color_format.as_str() {
                "R32Uint"     => wgpu::TextureFormat::R32Uint,
                "Rgba32Float" => wgpu::TextureFormat::Rgba32Float,
                _             => surface_format,
            };
            let blend = match cfg.blend.as_str() {
                "AlphaBlending" => Some(wgpu::BlendState::ALPHA_BLENDING),
                // 加算合成（src*1 + dst*1）。ブルームのアップサンプル／合成パスで、
                // 既存の RT 内容へ寄与を積み上げる（LoadOp::Load と併用する）ために使う。
                "Additive"      => Some(wgpu::BlendState {
                    color: wgpu::BlendComponent {
                        src_factor: wgpu::BlendFactor::One,
                        dst_factor: wgpu::BlendFactor::One,
                        operation:  wgpu::BlendOperation::Add,
                    },
                    alpha: wgpu::BlendComponent {
                        src_factor: wgpu::BlendFactor::One,
                        dst_factor: wgpu::BlendFactor::One,
                        operation:  wgpu::BlendOperation::Add,
                    },
                }),
                // Premultiplied over（src*1 + dst*(1-srcA)）。フラグメントが premultiplied 色
                // （色に被覆を折り込んだ値）を出力する前提の「over」合成。straight AlphaBlending と
                // 数学的に等価だが、屈折フラグメント（背景を自前合成し alpha=1 を出す）が dst を
                // 隠せるのはこのモード。RT-Translucency の距離ソート半透明で使う。
                "PremultipliedAlpha" => Some(wgpu::BlendState::PREMULTIPLIED_ALPHA_BLENDING),
                // 色付き透過率 WBOIT の背景濾過パス。final.rgb = dst.rgb * src.rgb（＝scene * Π T_frag）。
                // color: src*Zero + dst*Src = dst*src.rgb（per-channel の乗算）。
                // alpha: src*Zero + dst*One = dst.a（HDR のアルファを保持し、自色加算パスと非破壊に連鎖する）。
                "WboitBgMultiply" => Some(wgpu::BlendState {
                    color: wgpu::BlendComponent {
                        src_factor: wgpu::BlendFactor::Zero,
                        dst_factor: wgpu::BlendFactor::Src,
                        operation:  wgpu::BlendOperation::Add,
                    },
                    alpha: wgpu::BlendComponent {
                        src_factor: wgpu::BlendFactor::Zero,
                        dst_factor: wgpu::BlendFactor::One,
                        operation:  wgpu::BlendOperation::Add,
                    },
                }),
                "None"          => None,
                _               => Some(wgpu::BlendState::REPLACE),
            };
            let write_mask = if cfg.write_mask == "none" {
                wgpu::ColorWrites::empty()
            } else {
                wgpu::ColorWrites::ALL
            };
            Some(wgpu::ColorTargetState { format, blend, write_mask })
        };

        // ── 8. フラグメントステート ────────────────────────────
        let color_targets = color_target.as_ref().map(|ct| vec![Some(ct.clone())]);
        let fragment: Option<wgpu::FragmentState<'_>> =
            match (&cfg.fragment_entry, &color_targets) {
                (Some(entry), Some(targets)) => Some(wgpu::FragmentState {
                    module:              &shader,
                    entry_point:         Some(entry.as_str()),
                    targets:             targets.as_slice(),
                    compilation_options: Default::default(),
                }),
                _ => None,
            };

        // ── 9. 深度ステンシルステート ──────────────────────────
        // no_depth = true の場合はデプスアタッチメントなし（blit パス等で使用）。
        let depth_stencil = if cfg.no_depth {
            None
        } else {
            let depth_compare = parse_compare(&cfg.depth_compare);
            Some(wgpu::DepthStencilState {
                format:              depth_format,
                // 上書き指定があればそれを優先（同一 TOML からの depth_write バリアント生成用）。
                depth_write_enabled: depth_write_override.unwrap_or(cfg.depth_write),
                depth_compare,
                stencil: stencil.unwrap_or_default(),
                bias: wgpu::DepthBiasState {
                    constant:    cfg.depth_bias_constant,
                    slope_scale: cfg.depth_bias_slope_scale,
                    clamp:       cfg.depth_bias_clamp,
                },
            })
        };

        // ── 10. プリミティブステート ───────────────────────────
        let topology = match cfg.topology.as_str() {
            "LineList"      => wgpu::PrimitiveTopology::LineList,
            "TriangleStrip" => wgpu::PrimitiveTopology::TriangleStrip,
            _               => wgpu::PrimitiveTopology::TriangleList,
        };
        // 上書き指定があればそれを優先（同一 TOML からのカリング面バリアント生成用）。
        let cull_mode = match cull_mode_override.unwrap_or(cfg.cull_mode.as_str()) {
            "None"  => None,
            "Front" => Some(wgpu::Face::Front),
            _       => Some(wgpu::Face::Back),
        };
        let front_face = if cfg.front_face == "Cw" { wgpu::FrontFace::Cw } else { wgpu::FrontFace::Ccw };

        // ── 11. パイプライン生成 ───────────────────────────────
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some(pipeline_label),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module:              &shader,
                entry_point:         Some(cfg.vertex_entry.as_str()),
                buffers:             &vertex_buffers,
                compilation_options: Default::default(),
            },
            fragment,
            primitive: wgpu::PrimitiveState {
                topology,
                front_face,
                cull_mode,
                // 上書き指定があればそれを優先（同一 TOML からの Fill/Line バリアント生成用）。
                polygon_mode: polygon_mode_override.unwrap_or(wgpu::PolygonMode::Fill),
                ..Default::default()
            },
            depth_stencil,
            multisample: wgpu::MultisampleState::default(),
            multiview:   None,
            cache,
        });

        (pipeline, all_bgls)
    }
}

// ============================================================
//  WGSL リフレクション
// ============================================================

/// WGSL ソースを naga で解析し、各 bind group のレイアウトを返す。
///
/// バッファ類は `VERTEX_FRAGMENT` 可視、テクスチャ・サンプラーは `FRAGMENT` 可視とする。
fn reflect_bgls(
    device: &wgpu::Device,
    src:    &str,
) -> BTreeMap<u32, wgpu::BindGroupLayout> {
    let module = naga::front::wgsl::parse_str(src)
        .unwrap_or_else(|e| panic!("WGSL parse failed: {e:?}"));

    let mut groups: BTreeMap<u32, Vec<wgpu::BindGroupLayoutEntry>> = BTreeMap::new();

    for (_, var) in module.global_variables.iter() {
        let Some(rb) = &var.binding else { continue };

        let ty = &module.types[var.ty];

        let Some((wgpu_ty, visibility)) = to_binding_type(&ty.inner, var.space)
            else { continue };

        groups.entry(rb.group).or_default().push(wgpu::BindGroupLayoutEntry {
            binding:    rb.binding,
            visibility,
            ty:         wgpu_ty,
            count:      None,
        });
    }

    // 各グループ内のエントリを binding 番号でソート
    for entries in groups.values_mut() {
        entries.sort_by_key(|e| e.binding);
    }

    groups.into_iter().map(|(group, entries)| {
        let label = format!("Auto BGL group={group}");
        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some(&label),
            entries: &entries,
        });
        (group, bgl)
    }).collect()
}

fn to_binding_type(
    ty:    &naga::TypeInner,
    space: naga::AddressSpace,
) -> Option<(wgpu::BindingType, wgpu::ShaderStages)> {
    use naga::{ImageClass, ScalarKind, StorageAccess};

    match ty {
        // テクスチャ
        naga::TypeInner::Image { class, dim, arrayed } => {
            let view_dim = naga_dim_to_wgpu(*dim, *arrayed);
            let binding_ty = match class {
                ImageClass::Sampled { kind, .. } => {
                    let st = match kind {
                        ScalarKind::Float => wgpu::TextureSampleType::Float { filterable: true },
                        ScalarKind::Sint  => wgpu::TextureSampleType::Sint,
                        ScalarKind::Uint  => wgpu::TextureSampleType::Uint,
                        _                 => wgpu::TextureSampleType::Float { filterable: true },
                    };
                    wgpu::BindingType::Texture {
                        sample_type:  st,
                        view_dimension: view_dim,
                        multisampled: false,
                    }
                },
                ImageClass::Depth { .. } => {
                    wgpu::BindingType::Texture {
                        sample_type:    wgpu::TextureSampleType::Depth,
                        view_dimension: view_dim,
                        multisampled:   false,
                    }
                },
                ImageClass::Storage { format, access } => {
                    let acc = match (
                        access.contains(StorageAccess::LOAD),
                        access.contains(StorageAccess::STORE),
                    ) {
                        (true, true)  => wgpu::StorageTextureAccess::ReadWrite,
                        (_, true)     => wgpu::StorageTextureAccess::WriteOnly,
                        _             => wgpu::StorageTextureAccess::ReadOnly,
                    };
                    wgpu::BindingType::StorageTexture {
                        access:         acc,
                        format:         naga_storage_fmt(*format),
                        view_dimension: view_dim,
                    }
                },
            };
            Some((binding_ty, wgpu::ShaderStages::FRAGMENT))
        },
        // 加速構造（インラインレイトレ影 Phase R8, group 4 binding 6）
        // WGSL の `var accel: acceleration_structure;` を反映する。これを扱わないと
        // リフレクションが binding を握り潰し、パイプラインレイアウトと BindGroup が
        // 食い違って検証エラーになる。フラグメントのライトループから rayQuery で参照する。
        naga::TypeInner::AccelerationStructure { .. } => {
            Some((
                wgpu::BindingType::AccelerationStructure { vertex_return: false },
                wgpu::ShaderStages::FRAGMENT,
            ))
        },
        // サンプラー
        naga::TypeInner::Sampler { comparison } => {
            let st = if *comparison {
                wgpu::SamplerBindingType::Comparison
            } else {
                wgpu::SamplerBindingType::Filtering
            };
            Some((wgpu::BindingType::Sampler(st), wgpu::ShaderStages::FRAGMENT))
        },
        // バッファ（Uniform / Storage）
        _ => {
            let bt = match space {
                naga::AddressSpace::Uniform => wgpu::BindingType::Buffer {
                    ty:                 wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size:   None,
                },
                naga::AddressSpace::Storage { access } => wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Storage {
                        read_only: !access.contains(naga::StorageAccess::STORE),
                    },
                    has_dynamic_offset: false,
                    min_binding_size:   None,
                },
                _ => return None,
            };
            Some((bt, wgpu::ShaderStages::VERTEX_FRAGMENT))
        },
    }
}

fn naga_dim_to_wgpu(dim: naga::ImageDimension, arrayed: bool) -> wgpu::TextureViewDimension {
    match (dim, arrayed) {
        (naga::ImageDimension::D1,   _    ) => wgpu::TextureViewDimension::D1,
        (naga::ImageDimension::D2,   false) => wgpu::TextureViewDimension::D2,
        (naga::ImageDimension::D2,   true ) => wgpu::TextureViewDimension::D2Array,
        (naga::ImageDimension::D3,   _    ) => wgpu::TextureViewDimension::D3,
        (naga::ImageDimension::Cube, false) => wgpu::TextureViewDimension::Cube,
        (naga::ImageDimension::Cube, true ) => wgpu::TextureViewDimension::CubeArray,
    }
}

fn naga_storage_fmt(fmt: naga::StorageFormat) -> wgpu::TextureFormat {
    match fmt {
        naga::StorageFormat::R32Float     => wgpu::TextureFormat::R32Float,
        naga::StorageFormat::Rgba8Unorm   => wgpu::TextureFormat::Rgba8Unorm,
        naga::StorageFormat::Rgba16Float  => wgpu::TextureFormat::Rgba16Float,
        naga::StorageFormat::Rgba32Float  => wgpu::TextureFormat::Rgba32Float,
        naga::StorageFormat::R32Uint      => wgpu::TextureFormat::R32Uint,
        naga::StorageFormat::R32Sint      => wgpu::TextureFormat::R32Sint,
        other => panic!("unsupported storage format: {other:?}"),
    }
}

// ============================================================
//  頂点バッファレイアウトプリセット
// ============================================================

/// スロット名から頂点バッファレイアウトを返す。
///
/// 定義済みスロット名:
/// - `"mesh_vertex"`   — Vertex 構造体 (72 bytes, location 0-5)
/// - `"skin_vertex"`   — SkinVertex (24 bytes, location 6-7)
/// - `"smooth_normal"` — smooth normal (12 bytes, location 8)
/// - `"color_vertex"`  — ColorVertex (28 bytes, location 0-1)
pub fn vertex_buffer_layout(name: &str) -> wgpu::VertexBufferLayout<'static> {
    use wgpu::{VertexAttribute as VA, VertexFormat as VF, VertexStepMode, VertexBufferLayout};

    static MESH_ATTRS: &[VA] = &[
        VA { format: VF::Float32x3, offset: 0,  shader_location: 0 },
        VA { format: VF::Float32x3, offset: 12, shader_location: 1 },
        VA { format: VF::Float32x4, offset: 24, shader_location: 2 },
        VA { format: VF::Float32x2, offset: 40, shader_location: 3 },
        VA { format: VF::Float32x2, offset: 48, shader_location: 4 },
        VA { format: VF::Float32x4, offset: 56, shader_location: 5 },
    ];
    static SKIN_ATTRS: &[VA] = &[
        VA { format: VF::Uint16x4,  offset: 0, shader_location: 6 },
        VA { format: VF::Float32x4, offset: 8, shader_location: 7 },
    ];
    static SMOOTH_NORMAL_ATTRS: &[VA] = &[
        VA { format: VF::Float32x3, offset: 0, shader_location: 8 },
    ];
    static COLOR_ATTRS: &[VA] = &[
        VA { format: VF::Float32x3, offset: 0,  shader_location: 0 },
        VA { format: VF::Float32x4, offset: 12, shader_location: 1 },
    ];
    // 位置のみ頂点（12 bytes, location 0 = vec3）。スカイボックスの内向き球メッシュ等、
    // 方向ベースでサンプルするジオメトリに使う（法線・UV は頂点に持たずシェーダで算出）。
    static POS3_ATTRS: &[VA] = &[
        VA { format: VF::Float32x3, offset: 0, shader_location: 0 },
    ];
    // GizmoVertex: pos_a(0-11), t(12-15), pos_b(16-27), side(28-31), color(32-47)
    static GIZMO_ATTRS: &[VA] = &[
        VA { format: VF::Float32x3, offset: 0,  shader_location: 0 },
        VA { format: VF::Float32,   offset: 12, shader_location: 1 },
        VA { format: VF::Float32x3, offset: 16, shader_location: 2 },
        VA { format: VF::Float32,   offset: 28, shader_location: 3 },
        VA { format: VF::Float32x4, offset: 32, shader_location: 4 },
    ];
    // SpriteVertex: position(0-7, vec2) + uv(8-15, vec2) = 16 bytes
    static SPRITE_ATTRS: &[VA] = &[
        VA { format: VF::Float32x2, offset: 0, shader_location: 0 },
        VA { format: VF::Float32x2, offset: 8, shader_location: 1 },
    ];
    // SpriteInstance（Phase R6 汎用バッチング）: per-instance データ 80 bytes。
    //   model 行列（列優先 mat4x4 = 4 × vec4, location 2〜5, offset 0/16/32/48）
    //   + color（vec4, location 6, offset 64）。
    // step_mode = Instance のため 6 頂点のユニットクワッドを共有しつつ、
    // インスタンスごとに行列・カラーを切り替える。頂点属性 location 0/1 と衝突しないよう
    // location 2 以降に割り当てる（sprite.wgsl / sprite_outline.wgsl の @location と一致必須）。
    static SPRITE_INSTANCE_ATTRS: &[VA] = &[
        VA { format: VF::Float32x4, offset: 0,  shader_location: 2 },  // model col0
        VA { format: VF::Float32x4, offset: 16, shader_location: 3 },  // model col1
        VA { format: VF::Float32x4, offset: 32, shader_location: 4 },  // model col2
        VA { format: VF::Float32x4, offset: 48, shader_location: 5 },  // model col3
        VA { format: VF::Float32x4, offset: 64, shader_location: 6 },  // color
    ];

    match name {
        "mesh_vertex"   => VertexBufferLayout { array_stride: 72, step_mode: VertexStepMode::Vertex, attributes: MESH_ATTRS },
        "skin_vertex"   => VertexBufferLayout { array_stride: 24, step_mode: VertexStepMode::Vertex, attributes: SKIN_ATTRS },
        "smooth_normal" => VertexBufferLayout { array_stride: 12, step_mode: VertexStepMode::Vertex, attributes: SMOOTH_NORMAL_ATTRS },
        "color_vertex"  => VertexBufferLayout { array_stride: 28, step_mode: VertexStepMode::Vertex, attributes: COLOR_ATTRS },
        "pos3"          => VertexBufferLayout { array_stride: 12, step_mode: VertexStepMode::Vertex, attributes: POS3_ATTRS },
        "gizmo_vertex"  => VertexBufferLayout { array_stride: 48, step_mode: VertexStepMode::Vertex, attributes: GIZMO_ATTRS },
        "sprite_vertex" => VertexBufferLayout { array_stride: 16, step_mode: VertexStepMode::Vertex, attributes: SPRITE_ATTRS },
        // 80 bytes / インスタンス・step_mode=Instance（Phase R6）
        "sprite_instance" => VertexBufferLayout { array_stride: 80, step_mode: VertexStepMode::Instance, attributes: SPRITE_INSTANCE_ATTRS },
        other => panic!("unknown vertex slot preset: {other}"),
    }
}

// ============================================================
//  ヘルパー
// ============================================================

pub fn parse_compare(s: &str) -> wgpu::CompareFunction {
    match s {
        "Never"        => wgpu::CompareFunction::Never,
        "Less"         => wgpu::CompareFunction::Less,
        "Equal"        => wgpu::CompareFunction::Equal,
        "LessEqual"    => wgpu::CompareFunction::LessEqual,
        "Greater"      => wgpu::CompareFunction::Greater,
        "NotEqual"     => wgpu::CompareFunction::NotEqual,
        "GreaterEqual" => wgpu::CompareFunction::GreaterEqual,
        "Always"       => wgpu::CompareFunction::Always,
        other          => panic!("unknown compare function: {other}"),
    }
}
