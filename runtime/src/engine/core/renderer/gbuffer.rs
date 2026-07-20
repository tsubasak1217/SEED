// ============================================================
//  gbuffer.rs — G-Buffer リソース＋MRT ジオメトリパイプライン（Phase D3 Deferred Phase A）
//
//  ## 役割（単一責任）
//  「不透明ジオメトリを G-Buffer（4 枚の MRT + 深度）へ焼く」パイプライン一式のみを持つ。
//  G-Buffer の確保（RtPool へのテクスチャ確保）とフレームループへの接続は Phase B で行う。
//  本ファイル（Phase A）はパイプライン構築と描画ヘルパーの用意までに留める。
//
//  ## G-Buffer レイアウト（gbuffer_write.wgsl のコメントと同一。正典はシェーダ側）
//    RT0 Rgba8Unorm  : albedo.rgb + occlusion.a
//    RT1 Rgba16Float : world normal.xyz + 0
//    RT2 Rgba8Unorm  : metallic.r + roughness.g + 予約(b,a)
//    RT3 Rgba16Float : emissive.rgb(HDR) + 0
//
//  ## パイプラインレイアウトの再利用方針
//  gbuffer_write.wgsl は shader_common.wgsl（group0 カメラ／group1 モデル／group2 マテリアル）
//  ＋ surface.wgsl ＋ surface_gather.wgsl ＋ 自身のみを連結し、light_common.wgsl（group4）は
//  連結しない（マテリアル採取だけで完結し、ライト情報を必要としないため）。
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

// ============================================================
//  G-Buffer フォーマット定数
// ============================================================

/// RT0: albedo(rgb) + occlusion(a)。8bit で十分な精度（アルベドは知覚的量子化で足りる）。
pub const GBUFFER0_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba8Unorm;
/// RT1: world normal(xyz)。法線は符号付き単位ベクトルのため Float フォーマットが必要。
pub const GBUFFER1_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;
/// RT2: metallic(r) + roughness(g) + 予約(b, a)。仕様上 [0,1] に収まるため 8bit で十分。
pub const GBUFFER2_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba8Unorm;
/// RT3: emissive(rgb, HDR)。1.0 を超える値を取り得るため Float フォーマットが必要。
pub const GBUFFER3_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;

/// G-Buffer 各 RT の RtPool 登録名（post::rt_pool.rs の命名流儀＝小文字スネークケースに合わせる）。
/// Phase B でフレームループが `RtPool::ensure(device, GBUFFER0_RT_NAME, w, h, GBUFFER0_FORMAT)`
/// のように使う想定（Phase A では未使用）。
pub const GBUFFER0_RT_NAME: &str = "gbuffer0";
pub const GBUFFER1_RT_NAME: &str = "gbuffer1";
pub const GBUFFER2_RT_NAME: &str = "gbuffer2";
pub const GBUFFER3_RT_NAME: &str = "gbuffer3";

// ============================================================
//  シェーダソースリゾルバ（本モジュール自己完結）
// ============================================================

/// G-Buffer 書き込みパイプライン用の連結ソースを返す。
///
/// `vertex_source` は "shader_static_vertex.wgsl" または "shader_skinned_vertex.wgsl"。
/// light_common.wgsl / cluster_common.wgsl は連結しない（マテリアル採取のみでライト情報が
/// 不要なため。連結すると使わない group4 バインディングがリフレクションに載ってしまう）。
fn gbuffer_shader_sources(vertex_source: &'static str) -> [&'static str; 5] {
    [
        "shader_common.wgsl",
        vertex_source,
        "surface.wgsl",
        "surface_gather.wgsl",
        "gbuffer_write.wgsl",
    ]
}

// ============================================================
//  MRT カラーターゲット
// ============================================================

/// G-Buffer 4 枚ぶんのカラーターゲット定義を返す。
/// 全 RT ともブレンドなし（Replace 相当）・全チャンネル書き込み
/// （不透明ジオメトリを上書きするだけで合成は行わないため）。
fn gbuffer_color_targets() -> [Option<wgpu::ColorTargetState>; 4] {
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
    ]
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
        let static_bgls: Vec<&wgpu::BindGroupLayout> = vec![
            &mesh_pipeline.camera_bgl,
            &mesh_pipeline.model_bgl,
            &mesh_pipeline.material_bgl,
        ];
        let skinned_bgls: Vec<&wgpu::BindGroupLayout> = vec![
            &skinned_pipeline.camera_bgl,
            &skinned_pipeline.model_bgl,
            &skinned_pipeline.material_bgl,
            &skinned_pipeline.joint_bgl,
        ];

        let mesh: CullPipelineSet = std::array::from_fn(|i| {
            build_gbuffer_pipeline(
                device, df, cache, &static_bgls,
                &gbuffer_shader_sources("shader_static_vertex.wgsl"),
                &["mesh_vertex"],
                "gbuffer_mesh",
                CULL_FACE_VARIANTS[i],
            )
        });
        let skinned: CullPipelineSet = std::array::from_fn(|i| {
            build_gbuffer_pipeline(
                device, df, cache, &skinned_bgls,
                &gbuffer_shader_sources("shader_skinned_vertex.wgsl"),
                &["mesh_vertex", "skin_vertex"],
                "gbuffer_skinned",
                CULL_FACE_VARIANTS[i],
            )
        });

        // ── 地形レイヤブレンド用（group3 にレイヤ定義を差す）──
        //   MRT カラーターゲットは通常の G-Buffer と完全に同一（同じ 4 枚へ焼く）。
        let terrain = super::terrain_gbuffer::TerrainGBufferPipelines::new(
            device, mesh_pipeline, df, cache, &gbuffer_color_targets(),
        );

        Self { mesh, skinned, terrain }
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
    // 地形レイヤ定義のバインドグループ（group3・Terrain T2）。
    // `None`（レイヤ未初期化＝地形が無いシーン）のときは地形パイプラインへ切り替えず、
    // 通常のマテリアル経路で描く（group3 未バインドでの描画パニックを避ける）。
    terrain_layer_bg: Option<&'pass wgpu::BindGroup>,
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
            let is_terrain = terrain_layer_bg.is_some()
                && !draw.is_skinned
                && gpu_model.primitive_terrain_layers(draw.material_idx);

            // ── パイプライン切り替え ──────────────────────────────
            if cur_skinned != Some(draw.is_skinned)
                || cur_cull != Some(cull)
                || cur_terrain != Some(is_terrain)
            {
                if is_terrain {
                    render_pass.set_pipeline(&pipelines.terrain.pipes[cull.index()]);
                    // group3 = 地形レイヤ定義（uniform + レイヤテクスチャ 4 枚）。
                    // is_terrain が true になるのは terrain_layer_bg が Some のときだけ。
                    if let Some(tbg) = terrain_layer_bg {
                        render_pass.set_bind_group(3, tbg, &[]);
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
                    // group3 は非スキンレイアウトに存在しない（gbuffer 静的レイアウトは
                    // group0〜2 の 3 グループのみ）ため set_bind_group(3, ...) は不要。
                }
                render_pass.set_bind_group(0, camera_bg, &[]);
                cur_skinned = Some(draw.is_skinned);
                cur_cull    = Some(cull);
                cur_terrain = Some(is_terrain);
                cur_mat_ptr = None;
            }

            // ── マテリアル bind group（group 2）────────────────────
            let mat_ptr = mat_bg as *const _;
            if cur_mat_ptr != Some(mat_ptr) {
                render_pass.set_bind_group(2, mat_bg, &[]);
                cur_mat_ptr = Some(mat_ptr);
            }

            // ── モデル行列 bind group（group 1）───────────────────
            render_pass.set_bind_group(1, model_bg, &[]);

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
        let static_v = include_str!("shaders/shader_static_vertex.wgsl");
        let skin_v   = include_str!("shaders/shader_skinned_vertex.wgsl");
        let surf     = include_str!("shaders/surface.wgsl");
        let gather   = include_str!("shaders/surface_gather.wgsl");
        let gwrite   = include_str!("shaders/gbuffer_write.wgsl");

        let variants: [(&str, Vec<&str>); 2] = [
            ("gbuffer_mesh",    vec![common, static_v, surf, gather, gwrite]),
            ("gbuffer_skinned", vec![common, skin_v,   surf, gather, gwrite]),
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

    /// G-Buffer フォーマット定数と MRT 出力ロケーション数（4）の整合を示す簡易テスト。
    /// gbuffer_write.wgsl の GBufferOut は @location(0..3) の 4 出力を持つ
    /// （本テストはその前提＝定数側が 4 枚ぶん揃っていることだけを固定する）。
    #[test]
    fn gbuffer_format_constants_cover_four_render_targets() {
        use super::{GBUFFER0_FORMAT, GBUFFER1_FORMAT, GBUFFER2_FORMAT, GBUFFER3_FORMAT};
        let formats = [GBUFFER0_FORMAT, GBUFFER1_FORMAT, GBUFFER2_FORMAT, GBUFFER3_FORMAT];
        assert_eq!(formats.len(), 4, "G-Buffer の MRT 出力数は 4 枚で確定（gbuffer_write.wgsl 参照）");
    }
}
