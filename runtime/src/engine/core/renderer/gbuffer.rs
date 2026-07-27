// ============================================================
//  gbuffer.rs — G-Buffer リソース＋MRT ジオメトリパイプライン（Phase D3 Deferred Phase A）
//
//  ## 役割（単一責任）
//  「不透明ジオメトリを G-Buffer（5 枚の MRT + 深度）へ焼く」パイプライン一式のみを持つ。
//  G-Buffer の確保（RtPool へのテクスチャ確保）とフレームループへの接続は Phase B で行う。
//  本ファイル（Phase A）はパイプライン構築と描画ヘルパーの用意までに留める。
//
//  ## G-Buffer レイアウト（gbuffer_write.wgsl のコメントと同一。正典はシェーダ側）
//
//  | RT | フォーマット   | .r/.g/.b            | .a (.w)                        | 空き |
//  |----|---------------|---------------------|--------------------------------|------|
//  | 0  | Rgba8Unorm    | albedo（リニア）     | occlusion                      | なし |
//  | 1  | Rgba16Float   | world normal N.xyz  | authored 法線フラグ（0/1）      | なし |
//  | 2  | Rgba8Unorm    | metallic / roughness / diffuse_transmission | user_data（0..1） | なし |
//  | 3  | Rgba16Float   | emissive（HDR）      | surface_id（tag 4bit｜モデル 2bit）| 上位ビット |
//  | 4  | Rg16Float     | スクリーンスペース速度（.r/.g のみ。第2層の生成物）    | ―           | なし |
//
//  RT4（速度＝モーションベクタ）は「前フレーム→今フレームのピクセル移動量」を
//  ビューポート正規化 UV で持つ。値の定義は `shaders/velocity_math.wgsl`、
//  前フレーム行列の供給は `shaders/velocity_common.wgsl`（group4）が正典。
//
//  【この表の履歴】かつてここには「RT1.w = 0」「RT2 の b,a は予約」と書かれていたが、
//  実際には RT1.w は authored 法線フラグ（草／地形が 1 を立て、deferred_lighting が
//  幾何法線の代わりに N を使う判定に用いる）、RT2.b は diffuse_transmission として
//  既に使用されていた。シェーダ側（正典）に合わせて修正済み。
//
//  RT2.a / RT3.a の情報系チャンネルのビット規約は `renderer::surface_id`（Rust）と
//  `surface.wgsl` の `pack_surface_id`（WGSL）が対で持つ。RT3.a は 6bit しか使って
//  いないため、half float が無損失で扱える 11bit のうち残り 5bit が将来用に空いている。
//
//  ## パイプラインレイアウトの再利用方針
//  gbuffer_write.wgsl は shader_common.wgsl（group0 カメラ／group1 モデル／group2 マテリアル）
//  ＋ velocity_math/velocity_common.wgsl（group4 前フレーム行列）＋ G-Buffer 専用頂点シェーダ
//  ＋ surface.wgsl ＋ surface_gather.wgsl ＋ 自身を連結し、light_common.wgsl は連結しない
//  （マテリアル採取だけで完結し、ライト情報を必要としないため。**空いた group4 を
//   速度バッファ用の前フレームインスタンス行列に転用している**）。
//  group0〜2（＋skinned は group3=joints）の BindGroupLayout は MeshPipeline /
//  SkinnedMeshPipeline が既に同一シェーダ定義（shader_common.wgsl）から構築済みのものと
//  構造的に同一（同じ binding 番号・同じ型・同じ可視ステージ）になるため、新規に
//  リフレクションし直さず既存 BGL を borrow して使い回す（transparency.rs の WBOIT 手動
//  構築と同じ既存慣例＝wgpu の BindGroupLayout 構造的等価性に依拠する）。
// ============================================================

use crate::engine::core::loader::model::{CullFace, CULL_FACE_VARIANTS};
use super::pipeline::{get_shader_source, CullPipelineSet, MeshPipeline, SkinnedMeshPipeline};
use super::pipeline_config::vertex_buffer_layout;
use super::gpu_resources::{GpuModel, InstancedModelBatch, NUM_LODS};
// 地形レイヤのブレンドスロット数（＝パレット長。Terrain T2b）。
use crate::engine::terrain::layers::TERRAIN_BLEND_SLOTS;

// ============================================================
//  G-Buffer フォーマット定数
// ============================================================

/// RT0: albedo(rgb) + occlusion(a)。8bit で十分な精度（アルベドは知覚的量子化で足りる）。
pub const GBUFFER0_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba8Unorm;
/// RT1: world normal(xyz) + authored 法線フラグ(w)。
/// 法線は符号付き単位ベクトルのため Float フォーマットが必要。
pub const GBUFFER1_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;
/// RT2: metallic(r) + roughness(g) + diffuse_transmission(b) + user_data(a)。
/// いずれも仕様上 [0,1] に収まるため 8bit（1/255 刻み）で十分。
pub const GBUFFER2_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba8Unorm;
/// RT3: emissive(rgb, HDR) + surface_id(a)。
/// emissive は 1.0 を超える値を取り得るため Float フォーマットが必要。
/// .a のパック整数（0..63）は half float の無損失整数域（〜2048）に収まる。
pub const GBUFFER3_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;
/// RT4: スクリーンスペース速度（モーションベクタ）。
/// `.r/.g` = 前フレーム→今フレームの移動量（ビューポート正規化 UV・符号付き）。
///
/// 【なぜ Rg16Float なのか】
/// - 成分は 2 つで足りる（3 つ目・4 つ目に載せる情報が無い）。
/// - 符号付き・1 テクセル未満（〜1e-4）の微小値を扱うため整数系は不可。
///   half float は 6e-8 まで（非正規化数）表現でき、微小移動でも量子化で 0 に潰れない。
/// - **帯域リミットの都合**: WebGPU 仕様の「カラーアタッチメント byte cost」は
///   チャンネル実サイズではなく **フォーマットごとの固定表** で決まり、4 チャンネル形式は
///   8bit でも 16bit でも一律 8 byte として数える（`TextureFormat::target_pixel_byte_cost`）。
///   すなわち速度追加前の G-Buffer 4 枚だけで 8+8+8+8 = **32 byte** = wgpu 既定の
///   `max_color_attachment_bytes_per_sample`（32）にちょうど張り付いていた。
///   Rgba16Float を足せば +8 = 40、Rg16Float なら +4 = 36 で済む。どちらにせよ既定は
///   超えるためデバイス要求側でリミットを引き上げているが（renderer/mod.rs 参照）、
///   将来の追加余地を残すため 2 成分に留めた。
pub const GBUFFER_VELOCITY_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rg16Float;

/// G-Buffer の MRT 1 サンプルあたりの合計バイト数（wgpu リミット検証用）。
///
/// **チャンネル実サイズの合計ではない**（WebGPU 仕様の byte cost 表による値）。
/// 4 チャンネル形式は 8bit/16bit を問わず 8、Rg16Float は 4。
/// 値が実フォーマットとズレていないことは `gbuffer_mrt_fits_within_wgpu_default_limits`
/// テストが `target_pixel_byte_cost()` と突き合わせて固定する。
pub const GBUFFER_BYTES_PER_SAMPLE: u32 = 8 /*RT0*/ + 8 /*RT1*/ + 8 /*RT2*/ + 8 /*RT3*/ + 4 /*RT4*/;

/// G-Buffer の MRT 枚数（カラーアタッチメント数）。wgpu 既定の `max_color_attachments` は 8。
pub const GBUFFER_ATTACHMENT_COUNT: usize = 5;

/// G-Buffer 各 RT の RtPool 登録名（post::rt_pool.rs の命名流儀＝小文字スネークケースに合わせる）。
/// Phase B でフレームループが `RtPool::ensure(device, GBUFFER0_RT_NAME, w, h, GBUFFER0_FORMAT)`
/// のように使う想定（Phase A では未使用）。
pub const GBUFFER0_RT_NAME: &str = "gbuffer0";
pub const GBUFFER1_RT_NAME: &str = "gbuffer1";
pub const GBUFFER2_RT_NAME: &str = "gbuffer2";
pub const GBUFFER3_RT_NAME: &str = "gbuffer3";
/// RT4（速度＝モーションベクタ）の RtPool 登録名。
pub const GBUFFER_VELOCITY_RT_NAME: &str = "gbuffer_velocity";

// ============================================================
//  シェーダソースリゾルバ（本モジュール自己完結）
// ============================================================

/// G-Buffer 書き込みパイプライン用の連結ソースを返す。
///
/// `vertex_source` は "gbuffer_static_vertex.wgsl" または "gbuffer_skinned_vertex.wgsl"
/// （フォワードと共有の shader_static_vertex.wgsl ではない。速度バッファのために
///  group4 の前フレーム行列を読む専用版を使う）。
/// light_common.wgsl / cluster_common.wgsl は連結しない（マテリアル採取のみでライト情報が
/// 不要なため。空いた group4 を速度用の前フレーム行列に転用している）。
pub const GBUFFER_SHADER_SOURCE_COUNT: usize = 7;
fn gbuffer_shader_sources(vertex_source: &'static str)
    -> [&'static str; GBUFFER_SHADER_SOURCE_COUNT]
{
    [
        "shader_common.wgsl",
        // 速度の純関数（バインディングなし）と group4 の前フレーム行列。
        // 頂点シェーダより前に置く（vs_main が u_prev_instances を参照するため）。
        "velocity_math.wgsl",
        "velocity_common.wgsl",
        vertex_source,
        "surface.wgsl",
        "surface_gather.wgsl",
        "gbuffer_write.wgsl",
    ]
}

// ============================================================
//  MRT カラーターゲット
// ============================================================

/// G-Buffer 5 枚ぶんのカラーターゲット定義を返す。
/// 全 RT ともブレンドなし（Replace 相当）・全チャンネル書き込み
/// （不透明ジオメトリを上書きするだけで合成は行わないため）。
///
/// 速度 RT（RT4）も同じ規約でよい: 不透明ジオメトリは深度テストで最前面 1 枚だけが
/// 残るため、そのフラグメントの速度がそのまま最終値になる（合成する意味が無い）。
fn gbuffer_color_targets() -> [Option<wgpu::ColorTargetState>; GBUFFER_ATTACHMENT_COUNT] {
    let target = |format: wgpu::TextureFormat| wgpu::ColorTargetState {
        format,
        blend:      None,
        write_mask: wgpu::ColorWrites::ALL,
    };
    [
        Some(target(GBUFFER0_FORMAT)),
        Some(target(GBUFFER1_FORMAT)),
        Some(target(GBUFFER2_FORMAT)),
        Some(target(GBUFFER3_FORMAT)),
        Some(target(GBUFFER_VELOCITY_FORMAT)),
    ]
}

// ============================================================
//  group3 ギャップ / group4 前フレーム行列のレイアウト
// ============================================================

/// 速度用「前フレームのインスタンス行列」バインドグループレイアウト（group 4, binding 0）。
///
/// `velocity_common.wgsl` の `u_prev_instances`（`array<PrevModelUniform>`）に対応する。
/// 読み取り専用ストレージバッファ 1 本だけの最小レイアウトで、頂点ステージからのみ見える。
///
/// 【group4 を選んだ理由】G-Buffer 書き込みパスはライトを一切使わないため group4 が
/// 丸ごと空いている。ここへ置けば mesh / skinned / terrain の 3 バリアントすべてで
/// **同じ group 番号**にできる（group3 は skinned=joints・terrain=layers で埋まっており
/// 統一できない）。`max_bind_groups = 5` の内側にも収まる。
pub fn create_prev_instances_bgl(device: &wgpu::Device) -> wgpu::BindGroupLayout {
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("gbuffer_prev_instances_bgl"),
        entries: &[wgpu::BindGroupLayoutEntry {
            binding:    0,
            visibility: wgpu::ShaderStages::VERTEX,
            ty: wgpu::BindingType::Buffer {
                ty:                 wgpu::BufferBindingType::Storage { read_only: true },
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        }],
    })
}

/// 空のバインドグループレイアウト（非スキン・非地形の G-Buffer パイプラインの group3 埋め）。
///
/// 【なぜ必要か】パイプラインレイアウトの bind group 配列は 0 から連番で並べる必要がある。
/// 速度用の前フレーム行列を group4 に置いた結果、group3 を使わない
/// スタティックメッシュ用パイプラインでも「group3 に何か」を置かなければ group4 へ届かない。
/// 中身ゼロのレイアウト＋中身ゼロのバインドグループで穴を埋める
/// （deferred.rs の `gap_bgl2` / `empty_bg2` と同じ既存の流儀）。
fn create_empty_bgl(device: &wgpu::Device) -> wgpu::BindGroupLayout {
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label:   Some("gbuffer_gap_bgl3"),
        entries: &[],
    })
}

// ============================================================
//  GBufferPipelines — G-Buffer 書き込みパイプライン一式
// ============================================================

/// スタティック／スキンメッシュを G-Buffer へ焼くパイプライン一式。
///
/// 各々カリング面 3 種（Back / Front / None）のバリアントを持つ（添字 = `CullFace::index()`）。
/// マテリアル単位のカリング面設定に対応するため、MeshPipeline 等と同じ流儀で
/// 1 つの連結ソースから cull_mode だけを差し替えて 3 本ビルドする。
pub struct GBufferPipelines {
    /// スタティックメッシュ用（レイアウト: group0=camera, 1=model, 2=material）。
    pub mesh:    CullPipelineSet,
    /// スキンメッシュ用（レイアウト: group0=camera, 1=model, 2=material, 3=joints）。
    pub skinned: CullPipelineSet,
    /// 地形レイヤブレンド用（レイアウト: group0=camera, 1=model, 2=material, 3=terrain layers）。
    /// Terrain T2。`Material::terrain_layers` が true のプリミティブだけがここへ流れる。
    pub terrain: super::terrain_gbuffer::TerrainGBufferPipelines,
    /// プロシージャル草用（レイアウト: group0=camera, 1=草インスタンス）。
    /// 頂点バッファを持たず、頂点シェーダが `vertex_index` から葉を生成する。
    /// 散布データの供給は engine/terrain/scatter/ の責務（本パイプラインは描くだけ）。
    pub grass: super::grass_gbuffer::GrassGBufferPipeline,

    /// group4: 速度用「前フレームのインスタンス行列」の BindGroupLayout。
    ///
    /// `InstancedModelBatch::new` がこのレイアウトで per-LOD / per-node の
    /// バインドグループを作る（`methods/drawer` の `create_instanced_batch` 経由）。
    pub prev_instances_bgl: wgpu::BindGroupLayout,
    /// group3: スタティックメッシュ用の空レイアウト（group4 へ届かせるためのギャップ）。
    pub gap_bgl3: wgpu::BindGroupLayout,
    /// group3 の空バインドグループ（起動時 1 回だけ生成し使い回す）。
    pub empty_bg3: wgpu::BindGroup,
}

impl GBufferPipelines {
    /// `mesh_pipeline` / `skinned_pipeline` から group0〜2（＋joints）の BGL を借りて
    /// パイプラインレイアウトを組む（新規リフレクションはしない。モジュール冒頭のコメント参照）。
    pub fn new(
        device:            &wgpu::Device,
        mesh_pipeline:     &MeshPipeline,
        skinned_pipeline:  &SkinnedMeshPipeline,
        df:                wgpu::TextureFormat,
        cache:             Option<&wgpu::PipelineCache>,
    ) -> Self {
        // 速度バッファ用の group3 ギャップ／group4 前フレーム行列レイアウト。
        let gap_bgl3           = create_empty_bgl(device);
        let prev_instances_bgl = create_prev_instances_bgl(device);
        let empty_bg3 = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("GBuffer Empty BG (group 3)"),
            layout:  &gap_bgl3,
            entries: &[],
        });

        // group 番号は 0 から連番で並べる（gap を含む）。group4 = 前フレーム行列（速度用）。
        let static_bgls: Vec<&wgpu::BindGroupLayout> = vec![
            &mesh_pipeline.camera_bgl,
            &mesh_pipeline.model_bgl,
            &mesh_pipeline.material_bgl,
            &gap_bgl3,
            &prev_instances_bgl,
        ];
        let skinned_bgls: Vec<&wgpu::BindGroupLayout> = vec![
            &skinned_pipeline.camera_bgl,
            &skinned_pipeline.model_bgl,
            &skinned_pipeline.material_bgl,
            &skinned_pipeline.joint_bgl,
            &prev_instances_bgl,
        ];

        let mesh: CullPipelineSet = std::array::from_fn(|i| {
            build_gbuffer_pipeline(
                device, df, cache, &static_bgls,
                &gbuffer_shader_sources("gbuffer_static_vertex.wgsl"),
                &["mesh_vertex"],
                "gbuffer_mesh",
                CULL_FACE_VARIANTS[i],
            )
        });
        let skinned: CullPipelineSet = std::array::from_fn(|i| {
            build_gbuffer_pipeline(
                device, df, cache, &skinned_bgls,
                &gbuffer_shader_sources("gbuffer_skinned_vertex.wgsl"),
                &["mesh_vertex", "skin_vertex"],
                "gbuffer_skinned",
                CULL_FACE_VARIANTS[i],
            )
        });

        // ── 地形レイヤブレンド用（group3 にレイヤ定義を差す）──
        //   MRT カラーターゲットは通常の G-Buffer と完全に同一（同じ 4 枚へ焼く）。
        let terrain = super::terrain_gbuffer::TerrainGBufferPipelines::new(
            device, mesh_pipeline, df, cache, &gbuffer_color_targets(),
            // group4 = 速度用の前フレーム行列（地形も同じレイアウトを共有する）。
            &prev_instances_bgl,
        );

        // ── プロシージャル草（group1 に草インスタンスを差す）──
        //   MRT カラーターゲットは通常の G-Buffer と完全に同一（同じ 4 枚へ焼く）。
        let grass = super::grass_gbuffer::GrassGBufferPipeline::new(
            device, mesh_pipeline, df, cache, &gbuffer_color_targets(),
        );

        Self { mesh, skinned, terrain, grass, prev_instances_bgl, gap_bgl3, empty_bg3 }
    }
}

/// G-Buffer 書き込みパイプラインを 1 本構築する（手動 MRT。transparency.rs の
/// build_wboit_pipeline と同じ手法。TOML ビルダーは単一カラーターゲットにしか
/// 対応しないため MRT はここで直接組む）。
#[allow(clippy::too_many_arguments)]
fn build_gbuffer_pipeline(
    device:         &wgpu::Device,
    df:             wgpu::TextureFormat,
    cache:          Option<&wgpu::PipelineCache>,
    bgls:           &[&wgpu::BindGroupLayout],
    shader_sources: &[&str],
    vertex_slots:   &[&str],
    label_base:     &str,
    cull_face:      CullFace,
) -> wgpu::RenderPipeline {
    let label = format!("{label_base}_cull_{}", cull_face.as_str());
    let label = label.as_str();

    // ── シェーダモジュール（ソース連結）─────────────────────────
    let combined: String = shader_sources.iter()
        .map(|n| get_shader_source(n))
        .collect::<Vec<_>>()
        .join("\n");
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label:  Some(label),
        source: wgpu::ShaderSource::Wgsl(combined.into()),
    });

    // ── パイプラインレイアウト（BGL を再利用）───────────────────
    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label:                Some(label),
        bind_group_layouts:   bgls,
        push_constant_ranges: &[],
    });

    // ── 頂点バッファレイアウト ──────────────────────────────────
    let vbuffers: Vec<wgpu::VertexBufferLayout<'static>> =
        vertex_slots.iter().map(|n| vertex_buffer_layout(n)).collect();

    // ── 4 枚の MRT カラーターゲット ─────────────────────────────
    let targets = gbuffer_color_targets();

    // ── 深度: フォワードの mesh.toml と同一（Less・書き込みあり）───
    // 後続の透明／ギズモパスがこの深度を Load して整合させるため、フォワードと
    // 同じ規約（depth_write=true, compare=Less）にする。
    let depth_stencil = wgpu::DepthStencilState {
        format:              df,
        depth_write_enabled: true,
        depth_compare:       wgpu::CompareFunction::Less,
        stencil:             wgpu::StencilState::default(),
        bias:                wgpu::DepthBiasState::default(),
    };

    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label:  Some(label),
        layout: Some(&layout),
        vertex: wgpu::VertexState {
            module:              &shader,
            entry_point:         Some("vs_main"),
            buffers:             &vbuffers,
            compilation_options: Default::default(),
        },
        fragment: Some(wgpu::FragmentState {
            module:              &shader,
            entry_point:         Some("fs_gbuffer"),
            targets:             &targets,
            compilation_options: Default::default(),
        }),
        primitive: wgpu::PrimitiveState {
            topology:   wgpu::PrimitiveTopology::TriangleList,
            front_face: wgpu::FrontFace::Ccw,
            cull_mode:  match cull_face {
                CullFace::Back  => Some(wgpu::Face::Back),
                CullFace::Front => Some(wgpu::Face::Front),
                CullFace::None  => None,
            },
            ..Default::default()
        },
        depth_stencil: Some(depth_stencil),
        multisample:   wgpu::MultisampleState::default(),
        multiview:     None,
        cache,
    })
}

// ============================================================
//  draw_gbuffer_indirect — G-Buffer ジオメトリパス描画（Phase B で使用予定）
// ============================================================

/// 不透明ジオメトリを G-Buffer へ描画する。
///
/// model_drawer.rs の draw_model_indirect を土台にした簡略版:
///   - group4（ライト）を一切 set_bind_group しない（G-Buffer 書き込みはライト情報不要）。
///   - RT 影バリアント（rt_pipes）は存在しない（G-Buffer 自体はシャドウマップ／RT を問わない
///     ジオメトリ情報のみを焼くため、ライティングパス側で分岐する）。
///   - ワイヤーフレーム表示モードは対象外（デファードの G-Buffer は Lit 専用パスであり、
///     ワイヤーフレーム表示時はフォワード経由の従来パイプラインを使う想定）。
///   - 半透明（Blend）プリミティブはスキップする（半透明は Forward パスが担当）。
///
/// Phase A の時点ではフレームループから呼ばれない（未接続・Phase B で接続予定）。
/// `#[allow(dead_code)]` はその間のみ暫定的に付与する。
#[allow(dead_code)]
pub fn draw_gbuffer_indirect<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    gpu_model:   &'pass GpuModel,
    batch:       &'pass InstancedModelBatch,
    camera_bg:   &'pass wgpu::BindGroup,
    pipelines:   &'pass GBufferPipelines,
    // GPU メッシュレットカリング（第1弾）を LOD0 で使うか。model_drawer.rs の
    // draw_model_indirect と同じ条件（LOD0・非スキンのみ対象、他は自動フォールバック）。
    meshlet_cull: bool,
    // 地形レイヤの GPU リソース（group3・Terrain T2b）。
    // `None`（レイヤ未初期化＝地形が無いシーン）のときは地形パイプラインへ切り替えず、
    // 通常のマテリアル経路で描く（group3 未バインドでの描画パニックを避ける）。
    //
    // T2b からはチャンクごとに「レイヤ番号パレット」が違うため、単一のバインドグループ
    // ではなく `TerrainLayerResources`（パレット → BG のキャッシュ）を受け取り、
    // プリミティブのマテリアルが持つパレットで引き当てる。
    terrain_layers: Option<&'pass super::terrain_gbuffer::TerrainLayerResources>,
) {
    if batch.n_prims == 0 { return; }

    for lod in 0..NUM_LODS {
        let visible = batch.lod_visible_counts[lod];
        if visible == 0 { continue; }

        let mut cur_skinned: Option<bool>                   = None;
        let mut cur_cull:    Option<CullFace>               = None;
        let mut cur_mat_ptr: Option<*const wgpu::BindGroup> = None;
        // 直前のドローが地形パイプラインだったか（パイプライン切り替え判定に使う）。
        let mut cur_terrain: Option<bool>                   = None;
        // 直前に group3 へバインドした地形パレット（Terrain T2b）。
        // パレットが変わると group3 のバインドグループも変わるため再バインドが要る。
        let mut cur_palette: Option<[u32; TERRAIN_BLEND_SLOTS]> = None;
        // 直前に group4 へバインドした前フレーム行列バインドグループ（速度用）。
        // ノードごとに別バッファなので model bind group（group1）と同じ頻度で切り替わる。
        let mut cur_prev_ptr: Option<*const wgpu::BindGroup> = None;

        let joint_bg = batch.joint_vs_bg(lod);

        for (draw_idx, draw) in batch.node_prim_list.iter().enumerate() {
            let Some((_, model_bg)) = batch.lod_node_data[lod][draw.node_idx].as_ref()
                else { continue };

            // 半透明は Forward パスの担当（transparency.rs）。
            if gpu_model.primitive_alpha_mode(draw.material_idx)
                == crate::engine::core::loader::model::AlphaMode::Blend
            {
                continue;
            }

            let gpu_mesh = &gpu_model.meshes[draw.mesh_idx];
            let prim     = &gpu_mesh.primitives[draw.prim_idx];

            let mat_bg: &wgpu::BindGroup = draw.material_idx
                .and_then(|mi| gpu_model.materials.get(mi))
                .map(|m| &m.bind_group)
                .unwrap_or(&gpu_model.default_material.bind_group);

            let cull = gpu_model.primitive_cull_face(draw.material_idx);

            // ── 地形レイヤブレンド対象か（Terrain T2）────────────────
            //   レイヤ定義 BG が用意できているときだけ地形パイプラインへ振り分ける。
            //   スキンメッシュは地形になり得ないため、地形判定はスキン判定より優先する。
            let is_terrain = terrain_layers.is_some()
                && !draw.is_skinned
                && gpu_model.primitive_terrain_layers(draw.material_idx);

            // ── このプリミティブの地形パレット（非地形なら None）────────
            let palette = if is_terrain {
                Some(gpu_model.primitive_terrain_palette(draw.material_idx))
            } else {
                None
            };

            // ── パイプライン切り替え ──────────────────────────────
            //   パレットが変わっただけのときもパイプラインは同じだが、group3 の
            //   バインドグループが変わるため、ここでまとめて再バインドする。
            if cur_skinned != Some(draw.is_skinned)
                || cur_cull != Some(cull)
                || cur_terrain != Some(is_terrain)
                || (is_terrain && cur_palette != palette)
            {
                cur_palette = palette;
                if is_terrain {
                    render_pass.set_pipeline(&pipelines.terrain.pipes[cull.index()]);
                    // group3 = 地形レイヤ定義（uniform + パレット + 配列テクスチャ 3 本）。
                    // is_terrain が true になるのは terrain_layers が Some のときだけ。
                    if let (Some(res), Some(p)) = (terrain_layers, palette) {
                        render_pass.set_bind_group(3, res.bind_group(p), &[]);
                    }
                } else if draw.is_skinned {
                    render_pass.set_pipeline(&pipelines.skinned[cull.index()]);
                    if let Some(jbg) = joint_bg {
                        render_pass.set_bind_group(3, jbg, &[]);
                    } else {
                        render_pass.set_bind_group(3, &gpu_model.identity_joints_bg, &[]);
                    }
                } else {
                    render_pass.set_pipeline(&pipelines.mesh[cull.index()]);
                    // group3 は非スキン G-Buffer レイアウトでは空（ギャップ）。
                    // group4（速度用の前フレーム行列）へ届かせるために必ずセットする。
                    render_pass.set_bind_group(3, &pipelines.empty_bg3, &[]);
                }
                render_pass.set_bind_group(0, camera_bg, &[]);
                cur_skinned = Some(draw.is_skinned);
                cur_cull    = Some(cull);
                cur_terrain = Some(is_terrain);
                cur_mat_ptr = None;
                // パイプラインを切り替えたら group4（前フレーム行列）の再バインドが要る。
                cur_prev_ptr = None;
            }

            // ── マテリアル bind group（group 2）────────────────────
            let mat_ptr = mat_bg as *const _;
            if cur_mat_ptr != Some(mat_ptr) {
                render_pass.set_bind_group(2, mat_bg, &[]);
                cur_mat_ptr = Some(mat_ptr);
            }

            // ── モデル行列 bind group（group 1）───────────────────
            render_pass.set_bind_group(1, model_bg, &[]);

            // ── 前フレームのモデル行列 bind group（group 4・速度バッファ用）──────
            //   同じ (lod, node) の現行インスタンスバッファと **同順・同数** で
            //   `InstancedModelBatch::update` が詰めたバッファ。
            //   バッチが速度対応前に生成された等でスロットが無い場合は
            //   フォールバック（恒等行列 1 件のダミー）を bind する
            //   ＝速度は「カメラ由来ぶんのみ」に縮退し、未バインドでのパニックを防ぐ。
            let prev_bg = batch.prev_instances_bg(lod, draw.node_idx)
                .unwrap_or(&batch.identity_prev_bg);
            let prev_ptr = prev_bg as *const _;
            if cur_prev_ptr != Some(prev_ptr) {
                render_pass.set_bind_group(4, prev_bg, &[]);
                cur_prev_ptr = Some(prev_ptr);
            }

            // ── 頂点バッファ ───────────────────────────────────────
            render_pass.set_vertex_buffer(0, prim.vertex_buffer.slice(..));
            if draw.is_skinned {
                render_pass.set_vertex_buffer(1, prim.skin_vertex_buffer.as_ref().unwrap().slice(..));
            }

            // ── メッシュレット間接描画（LOD0 のみ、非スキンのみ）────────
            if meshlet_cull && lod == 0 && !draw.is_skinned {
                if let (Some(mi_buf), Some((cmd_buf, count_buf, capacity))) =
                    (prim.meshlet_index_buffer.as_ref(), batch.meshlet_draw(draw_idx))
                {
                    render_pass.set_index_buffer(mi_buf.slice(..), wgpu::IndexFormat::Uint32);
                    render_pass.multi_draw_indexed_indirect_count(
                        cmd_buf, 0, count_buf, 0, capacity,
                    );
                    continue;
                }
            }

            // ── 従来経路（CPU カリング済み draw_indexed）──────────────
            let (idx_buf, idx_count) = prim.get_lod_index_buffer(lod);
            render_pass.set_index_buffer(idx_buf.slice(..), wgpu::IndexFormat::Uint32);
            render_pass.draw_indexed(0..idx_count, 0, 0..visible);
        }
    }
}

// ============================================================
//  WGSL 静的検証（naga parse + validate）
// ============================================================
#[cfg(test)]
mod tests {
    /// G-Buffer 書き込み連結 WGSL（static / skinned）を naga で parse + validate する。
    /// 連結順は gbuffer_shader_sources() と一致させること。
    #[test]
    fn gbuffer_shaders_parse_and_validate() {
        let common   = include_str!("shaders/shader_common.wgsl");
        let vmath    = include_str!("shaders/velocity_math.wgsl");
        let vcommon  = include_str!("shaders/velocity_common.wgsl");
        let static_v = include_str!("shaders/gbuffer_static_vertex.wgsl");
        let skin_v   = include_str!("shaders/gbuffer_skinned_vertex.wgsl");
        let surf     = include_str!("shaders/surface.wgsl");
        let gather   = include_str!("shaders/surface_gather.wgsl");
        let gwrite   = include_str!("shaders/gbuffer_write.wgsl");

        let variants: [(&str, Vec<&str>); 2] = [
            ("gbuffer_mesh",    vec![common, vmath, vcommon, static_v, surf, gather, gwrite]),
            ("gbuffer_skinned", vec![common, vmath, vcommon, skin_v,   surf, gather, gwrite]),
        ];

        for (name, parts) in variants {
            let src = parts.join("\n");
            let module = naga::front::wgsl::parse_str(&src)
                .unwrap_or_else(|e| panic!("[{name}] WGSL parse 失敗: {e:?}"));
            let mut validator = naga::valid::Validator::new(
                naga::valid::ValidationFlags::all(),
                naga::valid::Capabilities::empty(),
            );
            validator
                .validate(&module)
                .unwrap_or_else(|e| panic!("[{name}] WGSL validate 失敗: {e:?}"));
        }
    }

    /// G-Buffer フォーマット定数と MRT 出力ロケーション数（5）の整合を示す簡易テスト。
    /// gbuffer_write.wgsl の GBufferOut は @location(0..4) の 5 出力を持つ
    /// （本テストはその前提＝定数側が 5 枚ぶん揃っていることだけを固定する）。
    #[test]
    fn gbuffer_format_constants_cover_five_render_targets() {
        use super::*;
        assert_eq!(gbuffer_color_targets().len(), GBUFFER_ATTACHMENT_COUNT);
        assert_eq!(GBUFFER_ATTACHMENT_COUNT, 5,
                   "G-Buffer の MRT 出力数は 5 枚（gbuffer_write.wgsl の GBufferOut 参照）");
        // 速度は Rg16Float（2 成分・半精度）。velocity_common.wgsl の出力型 vec2<f32> と対応。
        assert_eq!(GBUFFER_VELOCITY_FORMAT, wgpu::TextureFormat::Rg16Float);
    }

    /// **カラーアタッチメントのリミット計算**を固定する回帰ガード。
    ///
    /// 【この回帰テストが守るもの】
    /// WebGPU の `max_color_attachment_bytes_per_sample` はチャンネル実サイズではなく
    /// **フォーマットごとの固定 byte cost 表**で数える（4 チャンネル形式は 8bit でも
    /// 16bit でも一律 8）。この事実を見落とすと「Rg16Float はたった 4 byte だから
    /// 既定リミット 32 の内側」と誤解するが、実際は速度追加前で既に 32 ちょうどであり、
    /// 1 枚足した時点で `create_render_pipeline` が検証エラーで落ちる。
    /// そのため renderer/mod.rs はアダプタ実値でリミットを引き上げている。
    ///
    /// ここでは (1) 定数が実フォーマットの byte cost 合計と一致すること、
    /// (2) 既定リミットを超えている＝mod.rs の引き上げが必須であること、
    /// (3) 枚数は既定リミット内であること、を固定する。
    #[test]
    fn gbuffer_mrt_byte_cost_matches_formats_and_requires_raised_limit() {
        use super::{GBUFFER_ATTACHMENT_COUNT, GBUFFER_BYTES_PER_SAMPLE};
        let limits = wgpu::Limits::default();

        // (3) 枚数は既定（8）の内側。
        assert!(
            GBUFFER_ATTACHMENT_COUNT as u32 <= limits.max_color_attachments,
            "MRT 枚数 {GBUFFER_ATTACHMENT_COUNT} が max_color_attachments {} を超過",
            limits.max_color_attachments,
        );

        // (1) 実フォーマットから算出した合計と定数が一致すること。
        //     wgpu の validate_color_attachment_bytes_per_sample と同じ手順
        //     （各フォーマットのアラインメントへ切り上げてから byte cost を加算）で数える。
        let mut sum: u32 = 0;
        for f in [
            super::GBUFFER0_FORMAT, super::GBUFFER1_FORMAT, super::GBUFFER2_FORMAT,
            super::GBUFFER3_FORMAT, super::GBUFFER_VELOCITY_FORMAT,
        ] {
            let cost  = f.target_pixel_byte_cost().expect("G-Buffer の全 RT はカラーターゲット可能");
            let align = f.target_component_alignment().expect("同上");
            sum = sum.next_multiple_of(align) + cost;
        }
        assert_eq!(sum, GBUFFER_BYTES_PER_SAMPLE,
                   "GBUFFER_BYTES_PER_SAMPLE がフォーマット実体と不一致");

        // (2) 既定リミットは超える ⇒ renderer/mod.rs の引き上げが必須である。
        //     ここが「超えない」に変わったら、mod.rs の引き上げコメントも見直すこと。
        assert!(
            GBUFFER_BYTES_PER_SAMPLE > limits.max_color_attachment_bytes_per_sample,
            "既定リミット内に収まっている（mod.rs の引き上げ理由コメントを更新すること）",
        );
    }
}
