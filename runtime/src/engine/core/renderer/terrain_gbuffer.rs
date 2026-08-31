// ============================================================
//  terrain_gbuffer.rs — 地形レイヤブレンド用 G-Buffer パイプライン（Terrain T2b）
//
//  ## 役割（単一責任）
//  「地形メッシュを、レイヤブレンド（スプラット × triplanar）した結果で G-Buffer へ焼く」
//  ためのパイプラインと、レイヤ定義（layers.json）を GPU リソース化する処理だけを持つ。
//  地形の密度・メッシュ化・ペイントは engine/terrain/ の責務であり、本ファイルは触らない。
//  テクスチャ配列の構築は terrain_layer_textures.rs へ分離してある。
//
//  ## 既存 deferred 経路への乗せ方（設計判断）
//  新しいライティングを一切書かず、**G-Buffer 書き込み段だけ**を差し替える。
//  合成済みの base_color / normal / roughness / metallic を通常の G-Buffer レイアウトへ
//  出すため、ライティング・シャドウ・SSAO・RT 反射・SSGI は既存パスがそのまま効く。
//
//  ## バインドグループ
//    group0 = camera / group1 = model / group2 = material  … MeshPipeline から借りる
//    group3 = 地形レイヤ定義（本ファイルが定義する専用レイアウト）
//      binding 0: uniform  TerrainLayerUniform（全レイヤ定義 + このチャンクのパレット）
//      binding 1: sampler  （Repeat / 線形 / ミップ線形）
//      binding 2: texture_2d_array<f32>  base_color（sRGB）
//      binding 3: texture_2d_array<f32>  normal    （リニア）
//      binding 4: texture_2d_array<f32>  roughness （リニア）
//
//  ## レイヤ番号の運び方 —「チャンク単位パレット」（T2b の中心設計）
//  頂点フォーマット（mesh_vertex・72B）は 22 本のパイプラインが共有しており、
//  1 バイトでも増やすと全面改修になる。そこで頂点カラー RGBA は従来どおり
//  **重みだけ**を運び、「どのレイヤ番号が 4 スロットに載るか」は
//  **チャンクごとの uniform（palette）** で渡す。これで頂点フォーマットを一切変えずに
//  レイヤ総数を TERRAIN_MAX_LAYERS（16）へ拡張できる。
//  パレットは高々数種類なので、`TerrainLayerResources` がパレットをキーに
//  バインドグループをキャッシュする（テクスチャ配列とサンプラは全パレットで共有）。
// ============================================================

use std::collections::HashMap;

use bytemuck::{Pod, Zeroable};
use wgpu::util::DeviceExt;

use crate::engine::core::loader::model::{CullFace, Model, CULL_FACE_VARIANTS};
use crate::engine::terrain::layers::{texture_path, TerrainLayerSet, TERRAIN_BLEND_SLOTS, TERRAIN_MAX_LAYERS};
use crate::engine::terrain::cover::{CoverMaterialSet, TERRAIN_MAX_COVER_MATERIALS};

use super::pipeline::{get_shader_source, CullPipelineSet, MeshPipeline};
use super::terrain_layer_textures::TerrainLayerTextureArrays;

// ─── 調整用定数（マジックナンバー禁止）──────────────────────────────────────

/// triplanar ブレンドの既定の鋭さ。大きいほど 3 平面の切り替わりが硬くなる。
/// 4.0 は地形テクスチャリングで一般的な値（継ぎ目のぼけと引き伸ばしのバランス）。
const DEFAULT_TRIPLANAR_SHARPNESS: f32 = 4.0;

/// レイヤにテクスチャが「有る」ことを表す uniform 値。
const LAYER_HAS_TEXTURE: f32 = 1.0;
/// レイヤにテクスチャが「無い」（単色レイヤ）ことを表す uniform 値。
const LAYER_NO_TEXTURE: f32 = 0.0;

/// 未定義レイヤ（layers.json の定義数に満たないスロット）の既定 roughness。
const UNDEFINED_LAYER_ROUGHNESS: f32 = 1.0;
/// 未定義レイヤの既定 UV スケール（0 だと UV が潰れるため 1 を入れておく）。
const UNDEFINED_LAYER_UV_SCALE: f32 = 1.0;

/// 既定パレット（レイヤ 0..3 をスロット 0..3 へ素通し）。
///
/// T2 までの 4 層 layers.json はこのパレットで従来どおり描ける（後方互換の要）。
/// パレット未登録のチャンクを描くときのフォールバックでもある。
pub const IDENTITY_PALETTE: [u32; TERRAIN_BLEND_SLOTS] = [0, 1, 2, 3];

/// group3（地形レイヤ）のバインディング番号。WGSL 側の @binding と一致必須。
const BINDING_UNIFORM:          u32 = 0;
const BINDING_SAMPLER:          u32 = 1;
const BINDING_TEXTURE_BASE:     u32 = 2;
const BINDING_TEXTURE_NORMAL:   u32 = 3;
const BINDING_TEXTURE_ROUGH:    u32 = 4;

// ============================================================
//  GPU uniform レイアウト（WGSL TerrainLayerUniform と一致必須）
// ============================================================

/// レイヤ 1 枚ぶんの GPU パラメータ。
///
/// WGSL の `TerrainLayerParams` と同一レイアウト（vec4 × 3 = 48 バイト）。
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
struct TerrainLayerParamsGpu {
    /// rgb = ベースカラー係数（リニア）, a = ベースカラーテクスチャの有無（0 or 1）
    base_color: [f32; 4],
    /// x=metallic, y=roughness, z=triplanar UV スケール, w=detile モードコード
    surface: [f32; 4],
    /// x=法線テクスチャの有無, y=ラフネステクスチャの有無, z=detile 強度, w=予約
    extra: [f32; 4],
}

/// レイヤ定義一式の GPU uniform。
///
/// WGSL の `TerrainLayerUniform` と同一レイアウト。
/// `array<TerrainLayerParams, 16>` の要素ストライドは 48 バイト（vec4 アライン 16 の倍数）
/// なので、Rust 側の `[TerrainLayerParamsGpu; 16]` とバイト単位で一致する。
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
struct TerrainLayerUniformGpu {
    /// レイヤ定義本体（最大 TERRAIN_MAX_LAYERS 層）。全パレットで内容は同じ。
    layers: [TerrainLayerParamsGpu; TERRAIN_MAX_LAYERS],
    /// カバー素材表（I3.1）。全チャンク・全パレットで内容は同じ。
    ///
    /// 1 素材 = vec4（rgb = アルベド（リニア）, a = 粗さ）。**変位は載せない**
    /// （変位は CPU が頂点位置へ焼き込むので、シェーダは知る必要が無い）。
    cover: [[f32; 4]; TERRAIN_MAX_COVER_MATERIALS],
    /// このチャンクが使うレイヤ番号 4 つ（頂点カラー成分 → レイヤ番号の対応表）。
    palette: [u32; TERRAIN_BLEND_SLOTS],
    /// x = triplanar ブレンドの鋭さ, y = 有効レイヤ数, z = カバー素材数, w = 予約
    params: [f32; 4],
}

/// カバー素材定義（CPU 側）から uniform のカバー配列部分を組み立てる。
///
/// 定義数が `TERRAIN_MAX_COVER_MATERIALS` に満たない場合、余りは
/// 「黒・粗さ 1」で埋める（未定義値を残さない）。量 0 のテクセルでは
/// そもそも参照されないため、この埋め値が絵に出ることは無い。
fn build_cover_params(set: &CoverMaterialSet) -> [[f32; 4]; TERRAIN_MAX_COVER_MATERIALS] {
    let mut out = [[0.0, 0.0, 0.0, 1.0]; TERRAIN_MAX_COVER_MATERIALS];
    for (i, m) in set.materials.iter().take(TERRAIN_MAX_COVER_MATERIALS).enumerate() {
        out[i] = [m.albedo[0], m.albedo[1], m.albedo[2], m.roughness];
    }
    out
}

/// レイヤ定義（CPU 側）から uniform のレイヤ配列部分を組み立てる。
///
/// 定義数が TERRAIN_MAX_LAYERS に満たない場合、余りの層は
/// 「重み 0 でしか参照されない黒・テクスチャ無し」で埋める（未定義値を残さない）。
fn build_layer_params(set: &TerrainLayerSet) -> [TerrainLayerParamsGpu; TERRAIN_MAX_LAYERS] {
    let mut layers = [TerrainLayerParamsGpu {
        base_color: [0.0, 0.0, 0.0, LAYER_NO_TEXTURE],
        surface:    [0.0, UNDEFINED_LAYER_ROUGHNESS, UNDEFINED_LAYER_UV_SCALE, 0.0],
        extra:      [LAYER_NO_TEXTURE, LAYER_NO_TEXTURE, 0.0, 0.0],
    }; TERRAIN_MAX_LAYERS];

    for (i, l) in set.layers.iter().take(TERRAIN_MAX_LAYERS).enumerate() {
        // テクスチャの有無フラグ。シェーダはこれで「単色レイヤ」へ分岐する
        // （＝テクスチャ未指定でも T2 までと同じ絵になる＝後方互換）。
        // 空文字列は「未設定」と同義。texture_path を通すことで、
        // テクスチャ配列側（collect_layer_images）と判定が必ず一致する。
        let has_base   = has_texture_flag(texture_path(&l.base_color_texture).is_some());
        let has_normal = has_texture_flag(texture_path(&l.normal_texture).is_some());
        let has_rough  = has_texture_flag(texture_path(&l.roughness_texture).is_some());

        layers[i] = TerrainLayerParamsGpu {
            base_color: [l.base_color[0], l.base_color[1], l.base_color[2], has_base],
            surface: [
                l.metallic,
                l.roughness,
                l.uv_scale,
                // detile モードは u32 コードだが uniform は f32 配列なので数値として運ぶ。
                // シェーダ側で u32() へ戻す。0/1/2 の小さい整数なので f32 で厳密に表せる。
                l.detile.to_gpu_code() as f32,
            ],
            extra: [has_normal, has_rough, l.detile_strength, 0.0],
        };
    }
    layers
}

/// テクスチャ有無の bool を uniform 値（0.0 / 1.0）へ変換する。
#[inline]
fn has_texture_flag(present: bool) -> f32 {
    if present { LAYER_HAS_TEXTURE } else { LAYER_NO_TEXTURE }
}

// ============================================================
//  TerrainLayerResources — group3 のリソース一式（パレット別 BG キャッシュ付き）
// ============================================================

/// 地形レイヤの GPU リソース一式。
///
/// テクスチャ配列・サンプラ・レイヤパラメータは全チャンクで共有し、
/// **パレット（レイヤ番号 4 つ）だけ**がチャンクごとに違う。パレットは
/// uniform に載るので、パレットごとに小さな uniform バッファ（800B 前後）と
/// バインドグループを 1 組ずつ作ってキャッシュする。
///
/// 描画中（`&wgpu::RenderPass` が生きている間）は `&self` でしか触れないため、
/// 必要なパレットは**描画前に** `ensure_palette` / `ensure_palettes_from_model`
/// で登録しておくこと。未登録パレットは `bind_group` が既定パレットへフォールバックする
/// （＝真っ黒にならず、レイヤ割り当てだけがずれる安全側の縮退）。
pub struct TerrainLayerResources {
    /// レイヤパラメータ（全パレット共通の部分）。
    layer_params: [TerrainLayerParamsGpu; TERRAIN_MAX_LAYERS],
    /// 有効レイヤ数（uniform の params.y に載る）。
    active_layer_count: u32,
    /// カバー素材表（全パレット共通。I3.1）。
    cover_params: [[f32; 4]; TERRAIN_MAX_COVER_MATERIALS],
    /// 有効カバー素材数（uniform の params.z に載る）。
    active_cover_count: u32,
    /// 2D 配列テクスチャ 3 本（base_color / normal / roughness）。
    textures: TerrainLayerTextureArrays,
    /// 全レイヤ共通のサンプラ。
    sampler: wgpu::Sampler,
    /// パレット → バインドグループのキャッシュ。
    bind_groups: HashMap<[u32; TERRAIN_BLEND_SLOTS], wgpu::BindGroup>,
}

impl TerrainLayerResources {
    /// レイヤ定義から GPU リソースを構築する（テクスチャの読み込みを伴う）。
    ///
    /// 既定パレット（IDENTITY_PALETTE）のバインドグループは必ず先に作る
    /// （フォールバック先が常に存在することを型ではなく構築順で保証する）。
    pub fn new(
        device: &wgpu::Device,
        queue:  &wgpu::Queue,
        layout: &wgpu::BindGroupLayout,
        set:    &TerrainLayerSet,
        cover:  &CoverMaterialSet,
    ) -> Self {
        let mut me = Self {
            layer_params:       build_layer_params(set),
            active_layer_count: set.layers.len().min(TERRAIN_MAX_LAYERS) as u32,
            cover_params:       build_cover_params(cover),
            active_cover_count: cover.len().min(TERRAIN_MAX_COVER_MATERIALS) as u32,
            textures:           TerrainLayerTextureArrays::new(device, queue, set),
            sampler:            create_layer_sampler(device),
            bind_groups:        HashMap::new(),
        };
        me.ensure_palette(device, layout, IDENTITY_PALETTE);
        me
    }

    /// 指定パレットのバインドグループを（無ければ）作る。冪等。
    pub fn ensure_palette(
        &mut self,
        device:  &wgpu::Device,
        layout:  &wgpu::BindGroupLayout,
        palette: [u32; TERRAIN_BLEND_SLOTS],
    ) {
        if self.bind_groups.contains_key(&palette) {
            return;
        }
        // ─── パレットぶんの uniform バッファ（レイヤ配列は同一内容の複製）───
        //   1 パレット 800B 程度なので、パレット種類数（高々数十）ぶん持っても問題ない。
        let uniform = TerrainLayerUniformGpu {
            layers:  self.layer_params,
            cover:   self.cover_params,
            palette: clamp_palette(palette, self.active_layer_count),
            params:  [
                DEFAULT_TRIPLANAR_SHARPNESS,
                self.active_layer_count as f32,
                self.active_cover_count as f32,
                0.0,
            ],
        };
        let ubuf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("terrain_layer_uniform"),
            contents: bytemuck::bytes_of(&uniform),
            usage:    wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });

        let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("terrain_layer_bg"),
            layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding:  BINDING_UNIFORM,
                    resource: ubuf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding:  BINDING_SAMPLER,
                    resource: wgpu::BindingResource::Sampler(&self.sampler),
                },
                wgpu::BindGroupEntry {
                    binding:  BINDING_TEXTURE_BASE,
                    resource: wgpu::BindingResource::TextureView(&self.textures.base_color),
                },
                wgpu::BindGroupEntry {
                    binding:  BINDING_TEXTURE_NORMAL,
                    resource: wgpu::BindingResource::TextureView(&self.textures.normal),
                },
                wgpu::BindGroupEntry {
                    binding:  BINDING_TEXTURE_ROUGH,
                    resource: wgpu::BindingResource::TextureView(&self.textures.roughness),
                },
            ],
        });
        self.bind_groups.insert(palette, bg);
    }

    /// Model のマテリアルを走査し、地形マテリアルのパレットをまとめて登録する。
    ///
    /// 地形チャンクを GPU へアップロードした直後に呼ぶことを想定（描画前に登録が済む）。
    pub fn ensure_palettes_from_model(
        &mut self,
        device: &wgpu::Device,
        layout: &wgpu::BindGroupLayout,
        model:  &Model,
    ) {
        for mat in &model.materials {
            if mat.terrain_layers {
                self.ensure_palette(device, layout, mat.terrain_palette);
            }
        }
    }

    /// パレットに対応するバインドグループを返す。
    ///
    /// 未登録パレットは既定パレットへフォールバックする（描画をパニックさせない）。
    /// 既定パレットは `new` で必ず作られているため `expect` は到達しない。
    pub fn bind_group(&self, palette: [u32; TERRAIN_BLEND_SLOTS]) -> &wgpu::BindGroup {
        self.bind_groups.get(&palette).unwrap_or_else(|| {
            self.bind_groups
                .get(&IDENTITY_PALETTE)
                .expect("terrain: 既定パレットの bind group は new() で必ず作られる")
        })
    }
}

/// パレットのレイヤ番号を有効範囲へ丸める（不正データでの配列外参照を防ぐ）。
///
/// 丸め先は `TERRAIN_MAX_LAYERS` ではなく **実際に定義されたレイヤ数** であることが重要。
/// 配列テクスチャは定義レイヤ数ぶんしか確保していない（VRAM 節約のため）ので、
/// 上限を定数側に取ると存在しない配列レイヤを `textureSampleGrad` してしまう。
/// 例: 2 層構成に恒等パレット [0,1,2,3] が来ると 2,3 が範囲外になる。
fn clamp_palette(
    p:           [u32; TERRAIN_BLEND_SLOTS],
    layer_count: u32,
) -> [u32; TERRAIN_BLEND_SLOTS] {
    // 0 層はあり得ない（空 layers.json は既定セットへフォールバックする）が、
    // アンダーフローを避けるため 1 を下限に取る。
    let max = layer_count.clamp(1, TERRAIN_MAX_LAYERS as u32) - 1;
    std::array::from_fn(|i| p[i].min(max))
}

/// 全レイヤ共通のサンプラを作る。
///
/// ワールド座標由来 UV をタイリングするため Repeat。ミップ連鎖は CPU 生成済みなので
/// mipmap_filter は Linear（trilinear）。異方性フィルタは `textureSampleGrad` と
/// 併用しても効くが、レイヤ数 × タップ数が多いので既定（1＝無効）に留める。
fn create_layer_sampler(device: &wgpu::Device) -> wgpu::Sampler {
    device.create_sampler(&wgpu::SamplerDescriptor {
        label:          Some("terrain_layer_sampler"),
        address_mode_u: wgpu::AddressMode::Repeat,
        address_mode_v: wgpu::AddressMode::Repeat,
        address_mode_w: wgpu::AddressMode::Repeat,
        mag_filter:     wgpu::FilterMode::Linear,
        min_filter:     wgpu::FilterMode::Linear,
        mipmap_filter:  wgpu::FilterMode::Linear,
        ..Default::default()
    })
}

// ============================================================
//  TerrainGBufferPipelines — 地形 G-Buffer パイプライン一式
// ============================================================

/// 地形メッシュを G-Buffer へ焼くパイプライン一式（カリング面 3 種）。
///
/// `layer_bgl` は group3（レイヤ定義）のレイアウト。App 側がこのレイアウトから
/// `TerrainLayerResources` を作り、描画時に渡す（レイヤ定義はアセット読み込みを伴い、
/// パイプライン構築時点ではまだ手元に無いため分離している）。
pub struct TerrainGBufferPipelines {
    /// カリング面 3 種のパイプライン（添字 = `CullFace::index()`）。
    pub pipes: CullPipelineSet,
    /// group3（地形レイヤ定義）のバインドグループレイアウト。
    pub layer_bgl: wgpu::BindGroupLayout,
}

/// 地形 G-Buffer 書き込み用の連結ソースを返す。
///
/// surface.wgsl / surface_gather.wgsl は連結しない（Surface を経由せず直接 MRT を作る）。
/// naga 検証テストはこの並びと一致させること。
pub fn terrain_gbuffer_shader_sources() -> [&'static str; 5] {
    [
        "shader_common.wgsl",
        // 速度バッファ（モーションベクタ）: 純関数と group4 の前フレーム行列。
        // 頂点シェーダより前に置く（vs_main が u_prev_instances を参照するため）。
        "velocity_math.wgsl",
        "velocity_common.wgsl",
        // G-Buffer 専用の頂点シェーダ（フォワードと共有の shader_static_vertex ではない）。
        "gbuffer_static_vertex.wgsl",
        "terrain_gbuffer_write.wgsl",
    ]
}

impl TerrainGBufferPipelines {
    /// 地形 G-Buffer パイプラインを構築する。
    ///
    /// group0〜2 は `mesh_pipeline` の BGL を借りる（gbuffer.rs と同じ方針）。
    /// group3 だけを本ファイルで新規に定義し、group4（速度用の前フレーム行列）は
    /// `GBufferPipelines` から借りる（mesh / skinned / terrain で同一レイアウトを共有）。
    pub fn new(
        device:        &wgpu::Device,
        mesh_pipeline: &MeshPipeline,
        df:            wgpu::TextureFormat,
        cache:         Option<&wgpu::PipelineCache>,
        color_targets: &[Option<wgpu::ColorTargetState>],
        prev_bgl:      &wgpu::BindGroupLayout,
    ) -> Self {
        let layer_bgl = create_layer_bind_group_layout(device);

        let bgls: Vec<&wgpu::BindGroupLayout> = vec![
            &mesh_pipeline.camera_bgl,
            &mesh_pipeline.model_bgl,
            &mesh_pipeline.material_bgl,
            &layer_bgl,
            prev_bgl,
        ];

        let pipes: CullPipelineSet = std::array::from_fn(|i| {
            build_terrain_gbuffer_pipeline(
                device, df, cache, &bgls, color_targets, CULL_FACE_VARIANTS[i],
            )
        });

        Self { pipes, layer_bgl }
    }
}

/// group3（地形レイヤ定義）のバインドグループレイアウトを作る。
///
/// 内訳: uniform 1 本 + サンプラ 1 本 + 2D 配列テクスチャ 3 本。
/// bindless（binding_array）ではなく 2D 配列テクスチャを使う理由は
/// terrain_layer_textures.rs の冒頭コメントを参照（uniform との同居制約）。
fn create_layer_bind_group_layout(device: &wgpu::Device) -> wgpu::BindGroupLayout {
    // ─── 配列テクスチャ 3 本ぶんの共通エントリ定義 ───
    let array_texture = |binding: u32| wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Texture {
            sample_type:    wgpu::TextureSampleType::Float { filterable: true },
            view_dimension: wgpu::TextureViewDimension::D2Array,
            multisampled:   false,
        },
        count: None,
    };

    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("terrain_layer_bgl"),
        entries: &[
            // uniform（レイヤパラメータ + パレット）
            wgpu::BindGroupLayoutEntry {
                binding:    BINDING_UNIFORM,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Buffer {
                    ty:                 wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size:   None,
                },
                count: None,
            },
            // サンプラ（全レイヤ共通・リピート＋トライリニア）
            wgpu::BindGroupLayoutEntry {
                binding:    BINDING_SAMPLER,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty:         wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                count:      None,
            },
            array_texture(BINDING_TEXTURE_BASE),
            array_texture(BINDING_TEXTURE_NORMAL),
            array_texture(BINDING_TEXTURE_ROUGH),
        ],
    })
}

/// 地形 G-Buffer パイプラインを 1 本構築する（手動 MRT。gbuffer.rs と同じ手法）。
fn build_terrain_gbuffer_pipeline(
    device:        &wgpu::Device,
    df:            wgpu::TextureFormat,
    cache:         Option<&wgpu::PipelineCache>,
    bgls:          &[&wgpu::BindGroupLayout],
    color_targets: &[Option<wgpu::ColorTargetState>],
    cull_face:     CullFace,
) -> wgpu::RenderPipeline {
    let label = format!("terrain_gbuffer_cull_{}", cull_face.as_str());
    let label = label.as_str();

    // ── シェーダモジュール（ソース連結）──
    let combined: String = terrain_gbuffer_shader_sources()
        .iter()
        .map(|n| get_shader_source(n))
        .collect::<Vec<_>>()
        .join("\n");
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label:  Some(label),
        source: wgpu::ShaderSource::Wgsl(combined.into()),
    });

    // ── パイプラインレイアウト（group0〜2 は借用・group3 は自前）──
    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label:                Some(label),
        bind_group_layouts:   bgls,
        push_constant_ranges: &[],
    });

    // ── 頂点バッファレイアウト（通常の mesh_vertex。頂点カラーにスプラット重みが載る）──
    let vbuffers = [super::pipeline_config::vertex_buffer_layout("mesh_vertex")];

    // ── 深度: 通常の G-Buffer パスと同一（Less・書き込みあり）──
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
            entry_point:         Some("fs_terrain_gbuffer"),
            targets:             color_targets,
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
//  確率的タイリングの CPU 参照実装（WGSL と同一式・テスト用）
// ============================================================

/// 六角（三角）格子の 3 セルとその重心座標（WGSL `terrain_hex_cells` と同一式）。
///
/// Heitz & Neyret (2018) の TriangleGrid。重心座標は常に非負で総和 1 になる
/// ——これが確率的タイリングのブレンドが色を保存するための必要条件であり、
/// テストで数値的に検証する。
#[cfg_attr(not(test), allow(dead_code))]
pub fn hex_triangle_grid(uv_in: [f32; 2]) -> ([f32; 3], [[f32; 2]; 3]) {
    // WGSL 側 const と一致必須。
    const HEX_GRID_SCALE: f32 = 3.4641016;
    const HEX_SKEW_XY: f32 = -0.57735027;
    const HEX_SKEW_YY: f32 = 1.15470054;

    let uv = [uv_in[0] * HEX_GRID_SCALE, uv_in[1] * HEX_GRID_SCALE];
    let skewed = [uv[0] + HEX_SKEW_XY * uv[1], HEX_SKEW_YY * uv[1]];
    let base = [skewed[0].floor(), skewed[1].floor()];
    let f = [skewed[0] - base[0], skewed[1] - base[1]];
    let third = 1.0 - f[0] - f[1];

    if third > 0.0 {
        (
            [third, f[1], f[0]],
            [base, [base[0], base[1] + 1.0], [base[0] + 1.0, base[1]]],
        )
    } else {
        (
            [-third, 1.0 - f[1], 1.0 - f[0]],
            [
                [base[0] + 1.0, base[1] + 1.0],
                [base[0] + 1.0, base[1]],
                [base[0], base[1] + 1.0],
            ],
        )
    }
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::terrain::layers::DetileMode;

    /// WGSL ソース（連結前の地形フラグメント）。
    fn shader_src() -> &'static str {
        include_str!("shaders/terrain_gbuffer_write.wgsl")
    }

    /// 地形 G-Buffer 連結 WGSL を naga で parse + validate する。
    /// 連結順は terrain_gbuffer_shader_sources() と一致させること。
    #[test]
    fn terrain_gbuffer_shader_parses_and_validates() {
        let common   = include_str!("shaders/shader_common.wgsl");
        let vmath    = include_str!("shaders/velocity_math.wgsl");
        let vcommon  = include_str!("shaders/velocity_common.wgsl");
        let static_v = include_str!("shaders/gbuffer_static_vertex.wgsl");
        let terrain  = shader_src();

        let src = [common, vmath, vcommon, static_v, terrain].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[terrain_gbuffer] WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("[terrain_gbuffer] WGSL validate 失敗: {e:?}"));
    }

    /// WGSL のレイヤ数定数が Rust 側と一致することを検証する。
    /// 不一致だと頂点カラー成分数・パレット長・テクスチャ配列枚数がズレて描画が壊れる。
    #[test]
    fn terrain_layer_count_matches_shader() {
        let src = shader_src();
        for expected in [
            format!("const TERRAIN_BLEND_SLOTS: u32 = {TERRAIN_BLEND_SLOTS}u;"),
            format!("const TERRAIN_MAX_LAYERS: u32 = {TERRAIN_MAX_LAYERS}u;"),
            format!(
                "const TERRAIN_MAX_COVER_MATERIALS: u32 = {TERRAIN_MAX_COVER_MATERIALS}u;"
            ),
        ] {
            assert!(
                src.contains(&expected),
                "terrain_gbuffer_write.wgsl に `{expected}` が無い（Rust 側定数と不一致）"
            );
        }
    }

    /// detile モードコードが Rust ↔ WGSL で一致することを検証する。
    #[test]
    fn detile_mode_codes_match_shader() {
        let src = shader_src();
        for (mode, name) in [
            (DetileMode::None,       "DETILE_MODE_NONE"),
            (DetileMode::Stochastic, "DETILE_MODE_STOCHASTIC"),
            (DetileMode::Macro,      "DETILE_MODE_MACRO"),
        ] {
            let expected = format!("const {name}: u32 = {}u;", mode.to_gpu_code());
            assert!(
                src.contains(&expected),
                "terrain_gbuffer_write.wgsl に `{expected}` が無い（DetileMode と不一致）"
            );
        }
    }

    /// uniform のバイトサイズ・アラインが WGSL 側レイアウトと一致することを検証する。
    #[test]
    fn terrain_layer_uniform_size_matches_wgsl_layout() {
        // TerrainLayerParams = vec4 × 3 = 48 バイト。
        const PARAMS_BYTES: usize = 48;
        assert_eq!(std::mem::size_of::<TerrainLayerParamsGpu>(), PARAMS_BYTES);

        // uniform = 48 × 16 層 + cover(vec4 × 16 = 256) + palette(vec4<u32>=16) + params(vec4=16)。
        const EXPECTED_BYTES: usize =
            PARAMS_BYTES * TERRAIN_MAX_LAYERS + 16 * TERRAIN_MAX_COVER_MATERIALS + 16 + 16;
        assert_eq!(std::mem::size_of::<TerrainLayerUniformGpu>(), EXPECTED_BYTES);

        // uniform バッファは 16 バイト境界（WGSL uniform address space の要求）。
        assert_eq!(EXPECTED_BYTES % 16, 0);
        assert_eq!(std::mem::align_of::<TerrainLayerUniformGpu>() % 4, 0);
    }

    /// 確率的タイリングの重心座標が「非負・総和 1」であることを多数の UV で検証する。
    ///
    /// これが崩れると 3 タップのブレンドがエネルギー保存せず、明度が波打つ。
    #[test]
    fn hex_triangle_grid_weights_are_valid_barycentrics() {
        // 決定的な擬似乱数（テストを再現可能にするため乱数クレートを使わない）。
        let mut state: u32 = 0x1234_5678;
        let mut next = || {
            state = state.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
            (state >> 8) as f32 / (1u32 << 24) as f32
        };

        const SAMPLES: usize = 20_000;
        const UV_RANGE: f32 = 64.0;
        const SUM_TOLERANCE: f32 = 1.0e-4;

        for _ in 0..SAMPLES {
            let uv = [
                (next() - 0.5) * UV_RANGE,
                (next() - 0.5) * UV_RANGE,
            ];
            let (w, cells) = hex_triangle_grid(uv);
            let sum = w[0] + w[1] + w[2];
            assert!(
                (sum - 1.0).abs() < SUM_TOLERANCE,
                "重心座標の総和が 1 でない: uv={uv:?} w={w:?} sum={sum}"
            );
            for v in w {
                assert!(v >= 0.0, "重心座標が負: uv={uv:?} w={w:?}");
            }
            // セル ID は整数格子でなければならない（ハッシュの入力が安定するため）。
            for c in cells {
                assert_eq!(c[0], c[0].floor(), "セル ID が整数でない: {c:?}");
                assert_eq!(c[1], c[1].floor(), "セル ID が整数でない: {c:?}");
            }
        }
    }

    /// パレットのクランプが範囲外レイヤ番号を潰すこと。
    #[test]
    fn palette_is_clamped_into_range() {
        // 16 層フル定義なら 15 が上限。
        let clamped = clamp_palette([0, 15, 16, 9999], TERRAIN_MAX_LAYERS as u32);
        assert_eq!(clamped, [0, 15, 15, 15]);
        // 既定パレットは 4 層以上あればクランプしても不変（＝T2 の 4 層互換）。
        assert_eq!(
            clamp_palette(IDENTITY_PALETTE, TERRAIN_BLEND_SLOTS as u32),
            IDENTITY_PALETTE
        );
        // レイヤ数が少ない構成では、配列テクスチャの実層数までしか使ってはならない。
        // 2 層構成に恒等パレットが来ても範囲外（2,3）を指さないこと。
        assert_eq!(clamp_palette(IDENTITY_PALETTE, 2), [0, 1, 1, 1]);
        // 1 層構成では全スロットがレイヤ 0 に潰れる。
        assert_eq!(clamp_palette(IDENTITY_PALETTE, 1), [0, 0, 0, 0]);
        // 0 層（あり得ないが防御）でもアンダーフローせずレイヤ 0 に潰れる。
        assert_eq!(clamp_palette(IDENTITY_PALETTE, 0), [0, 0, 0, 0]);
    }
}
