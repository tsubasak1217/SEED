use std::path::Path;
use wgpu::util::DeviceExt;
use rayon::prelude::*;
use crate::engine::core::loader::model::{
    Model, ModelNode, Primitive, Vertex, TextureData, TextureSource, SamplerData,
    FilterMode, WrapMode, Material, AlphaMode,
};
use super::uniforms::{CameraUniform, ModelUniform, MaterialUniform, JointUniform, ColorVertex,
                      GpuCullData, FrustumUniform, GizmoVertex};
use super::skin_system::SkinComputeSystem;
use super::pipeline::SkinComputePipeline;

// ============================================================
//  デフォルトテクスチャ（テクスチャなし時のフォールバック）
// ============================================================

/// マテリアルにテクスチャが指定されていない場合のデフォルト値。
pub struct DefaultTextures {
    /// 1×1 白（base_color / occlusion のデフォルト）
    pub white:      GpuTexture,
    /// 1×1 フラット法線 (0.5, 0.5, 1.0) — normal map デフォルト
    pub flat_normal: GpuTexture,
    /// 1×1 黒（emissive のデフォルト）
    pub black:      GpuTexture,
}

impl DefaultTextures {
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue) -> Self {
        let sd = SamplerData::default();
        Self {
            white:      upload_rgba8(device, queue, 1, 1, &[255, 255, 255, 255], false, &sd, Some("Default White")),
            flat_normal: upload_rgba8(device, queue, 1, 1, &[128, 128, 255, 255], false, &sd, Some("Default Normal")),
            black:      upload_rgba8(device, queue, 1, 1, &[0,   0,   0,   255], false, &sd, Some("Default Black")),
        }
    }
}

// ============================================================
//  GpuTexture
// ============================================================

pub struct GpuTexture {
    #[allow(dead_code)]
    pub texture: wgpu::Texture,
    pub view:    wgpu::TextureView,
    pub sampler: wgpu::Sampler,
}

/// RGBA8 ピクセルデータから GPU テクスチャを生成する。
///
/// - `linear`: `true` なら `Rgba8Unorm`（法線・MR・AO）、`false` なら `Rgba8UnormSrgb`（ベースカラー・エミッシブ）
pub fn upload_rgba8(
    device:  &wgpu::Device,
    queue:   &wgpu::Queue,
    width:   u32,
    height:  u32,
    pixels:  &[u8],
    linear:  bool,
    sampler: &SamplerData,
    label:   Option<&str>,
) -> GpuTexture {
    let format = if linear {
        wgpu::TextureFormat::Rgba8Unorm
    } else {
        wgpu::TextureFormat::Rgba8UnormSrgb
    };

    let texture = device.create_texture_with_data(
        queue,
        &wgpu::TextureDescriptor {
            label,
            size: wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        },
        wgpu::util::TextureDataOrder::LayerMajor,
        pixels,
    );

    let view = texture.create_view(&wgpu::TextureViewDescriptor::default());

    let (mag, min, mipmap) = conv_filter(sampler.min_filter, sampler.mag_filter);
    let gpu_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
        label:            None,
        address_mode_u:   conv_wrap(sampler.wrap_u),
        address_mode_v:   conv_wrap(sampler.wrap_v),
        address_mode_w:   wgpu::AddressMode::Repeat,
        mag_filter:       mag,
        min_filter:       min,
        mipmap_filter:    mipmap,
        ..Default::default()
    });

    GpuTexture { texture, view, sampler: gpu_sampler }
}

/// `TextureData` から GPU テクスチャを生成する。
///
/// `TextureSource::FilePath` の場合は `image` クレートでロードする。
pub fn upload_texture_data(
    device:  &wgpu::Device,
    queue:   &wgpu::Queue,
    tex:     &TextureData,
    linear:  bool,
) -> GpuTexture {
    match &tex.source {
        TextureSource::Embedded { width, height, pixels } => {
            upload_rgba8(device, queue, *width, *height, pixels, linear, &tex.sampler,
                         tex.name.as_deref())
        }
        TextureSource::FilePath(path) => {
            load_from_file(device, queue, path, linear, &tex.sampler)
        }
    }
}

fn load_from_file(
    device:  &wgpu::Device,
    queue:   &wgpu::Queue,
    path:    &Path,
    linear:  bool,
    sampler: &SamplerData,
) -> GpuTexture {
    // asset_fs 経由で読む（仮想パス assets:// と PAK に対応）
    let path_str = path.to_str().unwrap_or("");
    let img = crate::engine::asset_fs::read_image(path_str);
    upload_rgba8(device, queue, img.width(), img.height(), &img, linear, sampler,
                 path.to_str())
}

// ─── フォーマット変換ヘルパー ─────────────────────────────────

fn conv_wrap(w: WrapMode) -> wgpu::AddressMode {
    match w {
        WrapMode::Repeat         => wgpu::AddressMode::Repeat,
        WrapMode::MirroredRepeat => wgpu::AddressMode::MirrorRepeat,
        WrapMode::ClampToEdge    => wgpu::AddressMode::ClampToEdge,
    }
}

fn conv_filter(
    min: FilterMode,
    mag: FilterMode,
) -> (wgpu::FilterMode, wgpu::FilterMode, wgpu::FilterMode) {
    let mag_f = match mag {
        FilterMode::Nearest | FilterMode::NearestMipmapNearest | FilterMode::NearestMipmapLinear
            => wgpu::FilterMode::Nearest,
        _   => wgpu::FilterMode::Linear,
    };
    let (min_f, mip_f) = match min {
        FilterMode::Nearest | FilterMode::NearestMipmapNearest
            => (wgpu::FilterMode::Nearest, wgpu::FilterMode::Nearest),
        FilterMode::NearestMipmapLinear
            => (wgpu::FilterMode::Nearest, wgpu::FilterMode::Linear),
        FilterMode::LinearMipmapNearest
            => (wgpu::FilterMode::Linear, wgpu::FilterMode::Nearest),
        _   => (wgpu::FilterMode::Linear, wgpu::FilterMode::Linear),
    };
    (mag_f, min_f, mip_f)
}

// ============================================================
//  GpuMaterial
// ============================================================

pub struct GpuMaterial {
    pub bind_group:     wgpu::BindGroup,
    #[allow(dead_code)]
    uniform_buffer:     wgpu::Buffer,
    // テクスチャのライフタイムを保持する
    #[allow(dead_code)]
    textures:           Vec<GpuTexture>,
}

impl GpuMaterial {
    pub fn upload(
        device:   &wgpu::Device,
        queue:    &wgpu::Queue,
        mat:      &Material,
        all_textures: &[TextureData],   // モデル全体のテクスチャスロット
        gpu_textures: &[GpuTexture],    // 上記に対応する GPU テクスチャ
        bgl:      &wgpu::BindGroupLayout,
        defaults: &DefaultTextures,
    ) -> Self {
        // ── ユニフォームバッファ ─────────────────────────────
        let alpha_cutoff = match mat.alpha_mode {
            AlphaMode::Mask   => mat.alpha_cutoff,
            _                 => 0.0,
        };

        let uniform = MaterialUniform {
            base_color_factor:  mat.base_color_factor,
            metallic_factor:    mat.metallic_factor,
            roughness_factor:   mat.roughness_factor,
            alpha_cutoff,
            has_base_color_tex: mat.base_color_texture.is_some() as u32,
            emissive_factor:    mat.emissive_factor,
            has_normal_tex:     mat.normal_texture.is_some() as u32,
            has_mr_tex:         mat.metallic_roughness_texture.is_some() as u32,
            has_occlusion_tex:  mat.occlusion_texture.is_some() as u32,
            has_emissive_tex:   mat.emissive_texture.is_some() as u32,
            _pad:               0,
        };

        let uniform_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Material Uniform"),
            contents: bytemuck::bytes_of(&uniform),
            usage:    wgpu::BufferUsages::UNIFORM,
        });

        // ── テクスチャ参照のヘルパー ─────────────────────────
        let tex_view = |idx: usize| &gpu_textures[idx].view;
        let tex_sampler = |idx: usize| &gpu_textures[idx].sampler;

        let base_color_idx   = mat.base_color_texture.as_ref().map(|t| t.texture_index);
        let normal_idx       = mat.normal_texture.as_ref().map(|t| t.texture_index);
        let mr_idx           = mat.metallic_roughness_texture.as_ref().map(|t| t.texture_index);
        let occlusion_idx    = mat.occlusion_texture.as_ref().map(|t| t.texture_index);
        let emissive_idx     = mat.emissive_texture.as_ref().map(|t| t.texture_index);

        let bc_view    = base_color_idx.map(tex_view).unwrap_or(&defaults.white.view);
        let bc_sampler = base_color_idx.map(tex_sampler).unwrap_or(&defaults.white.sampler);
        let nm_view    = normal_idx.map(tex_view).unwrap_or(&defaults.flat_normal.view);
        let nm_sampler = normal_idx.map(tex_sampler).unwrap_or(&defaults.flat_normal.sampler);
        let mr_view    = mr_idx.map(tex_view).unwrap_or(&defaults.white.view);
        let mr_sampler = mr_idx.map(tex_sampler).unwrap_or(&defaults.white.sampler);
        let ao_view    = occlusion_idx.map(tex_view).unwrap_or(&defaults.white.view);
        let ao_sampler = occlusion_idx.map(tex_sampler).unwrap_or(&defaults.white.sampler);
        let em_view    = emissive_idx.map(tex_view).unwrap_or(&defaults.black.view);
        let em_sampler = emissive_idx.map(tex_sampler).unwrap_or(&defaults.black.sampler);

        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:  Some("Material BG"),
            layout: bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0,  resource: uniform_buffer.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1,  resource: wgpu::BindingResource::TextureView(bc_view) },
                wgpu::BindGroupEntry { binding: 2,  resource: wgpu::BindingResource::Sampler(bc_sampler) },
                wgpu::BindGroupEntry { binding: 3,  resource: wgpu::BindingResource::TextureView(nm_view) },
                wgpu::BindGroupEntry { binding: 4,  resource: wgpu::BindingResource::Sampler(nm_sampler) },
                wgpu::BindGroupEntry { binding: 5,  resource: wgpu::BindingResource::TextureView(mr_view) },
                wgpu::BindGroupEntry { binding: 6,  resource: wgpu::BindingResource::Sampler(mr_sampler) },
                wgpu::BindGroupEntry { binding: 7,  resource: wgpu::BindingResource::TextureView(ao_view) },
                wgpu::BindGroupEntry { binding: 8,  resource: wgpu::BindingResource::Sampler(ao_sampler) },
                wgpu::BindGroupEntry { binding: 9,  resource: wgpu::BindingResource::TextureView(em_view) },
                wgpu::BindGroupEntry { binding: 10, resource: wgpu::BindingResource::Sampler(em_sampler) },
            ],
        });

        Self { bind_group, uniform_buffer, textures: Vec::new() }
    }
}

// ============================================================
//  GpuPrimitive / GpuMesh / GpuModel
// ============================================================

pub struct GpuPrimitive {
    pub vertex_buffer:        wgpu::Buffer,
    pub skin_vertex_buffer:   Option<wgpu::Buffer>,
    /// アウトライン描画用スムーズ法線（位置が同じ頂点の法線を平均化したもの）
    pub smooth_normal_buffer: wgpu::Buffer,
    /// LOD0 インデックスバッファ（フル解像度）
    pub index_buffer:         wgpu::Buffer,
    pub index_count:          u32,
    /// LOD1, LOD2, LOD3 インデックスバッファ（lod_index_buffers[0] = LOD1）
    pub lod_index_buffers:    Vec<wgpu::Buffer>,
    pub lod_index_counts:     Vec<u32>,
    pub material_index:       Option<usize>,
}

impl GpuPrimitive {
    /// `lod` 番号に対応するインデックスバッファとインデックス数を返す。
    /// 対応する LOD データが存在しない場合は利用可能な最高 LOD（最も簡略化済み）を使用。
    pub fn get_lod_index_buffer(&self, lod: usize) -> (&wgpu::Buffer, u32) {
        if lod == 0 || self.lod_index_buffers.is_empty() {
            return (&self.index_buffer, self.index_count);
        }
        let idx = (lod - 1).min(self.lod_index_buffers.len() - 1);
        (&self.lod_index_buffers[idx], self.lod_index_counts[idx])
    }

    fn upload(device: &wgpu::Device, prim: &Primitive) -> Self {
        use crate::engine::core::loader::model::{Vertex, SkinVertex};

        let vertex_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Vertex Buffer"),
            contents: bytemuck::cast_slice::<Vertex, u8>(&prim.vertices),
            usage:    wgpu::BufferUsages::VERTEX,
        });

        let smooth_normals = compute_smooth_normals(&prim.vertices, &prim.indices);
        let smooth_normal_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Smooth Normal Buffer"),
            contents: bytemuck::cast_slice::<[f32; 3], u8>(&smooth_normals),
            usage:    wgpu::BufferUsages::VERTEX,
        });

        let skin_vertex_buffer = if prim.skin_vertices.is_empty() {
            None
        } else {
            Some(device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label:    Some("Skin Vertex Buffer"),
                contents: bytemuck::cast_slice::<SkinVertex, u8>(&prim.skin_vertices),
                usage:    wgpu::BufferUsages::VERTEX,
            }))
        };

        let index_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Index Buffer"),
            contents: bytemuck::cast_slice(&prim.indices),
            usage:    wgpu::BufferUsages::INDEX,
        });

        // LOD インデックスバッファをアップロード
        let lod_index_buffers: Vec<wgpu::Buffer> = prim.lod_indices.iter()
            .map(|lod_idx| device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label:    Some("LOD Index Buffer"),
                contents: bytemuck::cast_slice(lod_idx),
                usage:    wgpu::BufferUsages::INDEX,
            }))
            .collect();
        let lod_index_counts: Vec<u32> = prim.lod_indices.iter()
            .map(|lod_idx| lod_idx.len() as u32)
            .collect();

        Self {
            vertex_buffer,
            skin_vertex_buffer,
            smooth_normal_buffer,
            index_buffer,
            index_count:    prim.indices.len() as u32,
            lod_index_buffers,
            lod_index_counts,
            material_index: prim.material_index,
        }
    }
}

/// 隣接トライアングルの面法線を加重平均してスムーズ法線を計算する。
///
/// ハードエッジ部分（頂点が複製されている）では複製ごとに独立した結果になるが、
/// それでも同一プリミティブ内の連続面の法線は滑らかになりアウトラインの破綻を軽減する。
fn compute_smooth_normals(
    vertices: &[crate::engine::core::loader::model::Vertex],
    indices:  &[u32],
) -> Vec<[f32; 3]> {
    let n = vertices.len();
    let mut accum = vec![[0.0f32; 3]; n];

    for tri in indices.chunks(3) {
        if tri.len() < 3 { continue; }
        let (i0, i1, i2) = (tri[0] as usize, tri[1] as usize, tri[2] as usize);
        if i0 >= n || i1 >= n || i2 >= n { continue; }

        let p0 = vertices[i0].position;
        let p1 = vertices[i1].position;
        let p2 = vertices[i2].position;

        let e1 = [p1[0]-p0[0], p1[1]-p0[1], p1[2]-p0[2]];
        let e2 = [p2[0]-p0[0], p2[1]-p0[1], p2[2]-p0[2]];
        // 面積比例の面法線（正規化しないことで大きい面を重視）
        // エンジンは左手座標系（Z 反転済みの頂点座標）を使用するため、
        // 標準クロス積 e1×e2 は LH でのアウトライン向き（外向き）と逆になる。
        // → e2×e1 = -(e1×e2) を使って正しい外向き法線を得る。
        let fn_ = [
            e2[1]*e1[2] - e2[2]*e1[1],
            e2[2]*e1[0] - e2[0]*e1[2],
            e2[0]*e1[1] - e2[1]*e1[0],
        ];

        for &i in &[i0, i1, i2] {
            accum[i][0] += fn_[0];
            accum[i][1] += fn_[1];
            accum[i][2] += fn_[2];
        }
    }

    accum.into_iter().enumerate().map(|(i, mut s)| {
        let len = (s[0]*s[0] + s[1]*s[1] + s[2]*s[2]).sqrt();
        if len > 1e-6 {
            s[0] /= len; s[1] /= len; s[2] /= len;
            s
        } else {
            // インデックス未使用の孤立頂点: 元の法線をフォールバックとして使用
            vertices[i].normal
        }
    }).collect()
}

pub struct GpuMesh {
    pub primitives: Vec<GpuPrimitive>,
}

/// モデル全体の GPU リソース。
pub struct GpuModel {
    pub meshes:           Vec<GpuMesh>,
    pub materials:        Vec<GpuMaterial>,
    /// マテリアル未指定プリミティブ用のデフォルトマテリアル bind group
    pub default_material: GpuMaterial,
    /// スキンなしノード用の単位行列ジョイント bind group
    pub identity_joints_bg: wgpu::BindGroup,
    // テクスチャ所有権
    #[allow(dead_code)]
    textures: Vec<GpuTexture>,
}

impl GpuModel {
    pub fn upload(
        device:       &wgpu::Device,
        queue:        &wgpu::Queue,
        model:        &Model,
        material_bgl: &wgpu::BindGroupLayout,
        joint_bgl:    &wgpu::BindGroupLayout,
        defaults:     &DefaultTextures,
    ) -> Self {
        // ── テクスチャ ─────────────────────────────────────────
        let gpu_textures: Vec<GpuTexture> = model.textures.iter().enumerate().map(|(i, td)| {
            // TextureData.linear フラグで sRGB / 線形を切り替える。
            // false → Rgba8UnormSrgb（ベースカラー・エミッシブ）
            // true  → Rgba8Unorm   （法線・MR・AO など線形データ）
            upload_texture_data(device, queue, td, td.linear)
        }).collect();

        // ── マテリアル ─────────────────────────────────────────
        let materials: Vec<GpuMaterial> = model.materials.iter().map(|mat| {
            GpuMaterial::upload(device, queue, mat, &model.textures, &gpu_textures,
                                material_bgl, defaults)
        }).collect();

        // デフォルトマテリアル（白・完全不透明）
        let default_mat_data = Material::default();
        let default_material = GpuMaterial::upload(
            device, queue, &default_mat_data, &[], &[], material_bgl, defaults);

        // ── メッシュ ───────────────────────────────────────────
        let meshes: Vec<GpuMesh> = model.meshes.iter().map(|mesh| {
            GpuMesh {
                primitives: mesh.primitives.iter()
                    .map(|p| GpuPrimitive::upload(device, p))
                    .collect(),
            }
        }).collect();

        // ── 単位行列ジョイント bind group ─────────────────────
        let identity_joints = JointUniform::identity();
        let joint_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Identity Joints"),
            contents: bytemuck::bytes_of(&identity_joints),
            usage:    wgpu::BufferUsages::STORAGE,
        });
        let identity_joints_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:  Some("Identity Joints BG"),
            layout: joint_bgl,
            entries: &[wgpu::BindGroupEntry {
                binding:  0,
                resource: joint_buf.as_entire_binding(),
            }],
        });

        Self { meshes, materials, default_material, identity_joints_bg, textures: gpu_textures }
    }
}

// ============================================================
//  視錐台カリング
// ============================================================

/// ビュープロジェクション行列（行優先）から視錐台の 6 平面を抽出する。
///
/// 平面方程式: `dot(plane.xyz, P) + plane.w ≥ 0` が「内側」。
/// DX / wgpu の深度レンジ [0, 1] に対応する Near/Far 平面を使用。
pub fn extract_frustum_planes(vp: &[[f32; 4]; 4]) -> [[f32; 4]; 6] {
    // vp[row][col] 行優先: clip = vp * world_pos
    [
        // Left:   vp[3] + vp[0]
        add_rows(vp[3], vp[0]),
        // Right:  vp[3] - vp[0]
        sub_rows(vp[3], vp[0]),
        // Bottom: vp[3] + vp[1]
        add_rows(vp[3], vp[1]),
        // Top:    vp[3] - vp[1]
        sub_rows(vp[3], vp[1]),
        // Near:   vp[2]         （depth [0,1]）
        vp[2],
        // Far:    vp[3] - vp[2]
        sub_rows(vp[3], vp[2]),
    ]
}

#[inline]
fn add_rows(a: [f32; 4], b: [f32; 4]) -> [f32; 4] {
    [a[0]+b[0], a[1]+b[1], a[2]+b[2], a[3]+b[3]]
}
#[inline]
fn sub_rows(a: [f32; 4], b: [f32; 4]) -> [f32; 4] {
    [a[0]-b[0], a[1]-b[1], a[2]-b[2], a[3]-b[3]]
}

/// AABB が視錐台と交差するか判定する。
///
/// 6 平面すべてに対して「正頂点」が内側であれば可視と判断する。
/// 保守的な判定（偽陽性あり）だが高速。
#[inline]
pub fn test_aabb_frustum(planes: &[[f32; 4]; 6], min: [f32; 3], max: [f32; 3]) -> bool {
    for p in planes {
        // 平面法線に最も揃った頂点（正頂点）を選ぶ
        let px = if p[0] >= 0.0 { max[0] } else { min[0] };
        let py = if p[1] >= 0.0 { max[1] } else { min[1] };
        let pz = if p[2] >= 0.0 { max[2] } else { min[2] };
        if p[0]*px + p[1]*py + p[2]*pz + p[3] < 0.0 {
            return false;   // 完全に外側
        }
    }
    true
}

/// モデルローカル空間の AABB をルート変換行列でワールド空間に変換する。
///
/// 8 頂点すべてを変換し、新しい AABB を求める（精密かつシンプル）。
#[inline]
fn transform_aabb(
    local_min: [f32; 3],
    local_max: [f32; 3],
    mat:       &[[f32; 4]; 4],
) -> ([f32; 3], [f32; 3]) {
    let mut wmin = [f32::MAX; 3];
    let mut wmax = [f32::MIN; 3];
    for &bx in &[local_min[0], local_max[0]] {
        for &by in &[local_min[1], local_max[1]] {
            for &bz in &[local_min[2], local_max[2]] {
                for i in 0..3 {
                    let v = mat[i][0]*bx + mat[i][1]*by + mat[i][2]*bz + mat[i][3];
                    wmin[i] = wmin[i].min(v);
                    wmax[i] = wmax[i].max(v);
                }
            }
        }
    }
    (wmin, wmax)
}

/// モデルの全頂点から AABB（ローカル空間）を計算する。
fn compute_model_aabb(model: &Model) -> ([f32; 3], [f32; 3]) {
    let mut min = [f32::MAX; 3];
    let mut max = [f32::MIN; 3];
    let mut found = false;
    for mesh in &model.meshes {
        for prim in &mesh.primitives {
            for v in &prim.vertices {
                found = true;
                for i in 0..3 {
                    min[i] = min[i].min(v.position[i]);
                    max[i] = max[i].max(v.position[i]);
                }
            }
        }
    }
    if found { (min, max) } else { ([-1.0; 3], [1.0; 3]) }
}

// ============================================================
//  InstancedModelBatch — CPU フラスタムカリング + LOD + Indirect Draw
// ============================================================

/// LOD レベル数（0 = フル解像度、1〜3 = 簡略化済み）。
pub const NUM_LODS: usize = 4;

/// LOD 切り替え距離の二乗値。`dist_sq < LOD_DIST_SQ[i]` なら LOD i を使用。
/// [LOD0→LOD1, LOD1→LOD2, LOD2→LOD3] の境界距離の二乗。
const LOD_DIST_SQ: [f32; 3] = [
    10.0 * 10.0,   // 10 ユニット以内: LOD0（フル）
    30.0 * 30.0,   // 30 ユニット以内: LOD1（50%）
    60.0 * 60.0,   // 60 ユニット以内: LOD2（25%）
                   // 60 ユニット以遠: LOD3（10%）
];

/// レンダーパスで `draw_indexed` を呼ぶ際に必要な
/// (ノード, プリミティブ) ペア情報。
///
/// `node_prim_list` は `(is_skinned, material_idx)` で昇順ソート済み。
/// これによりパイプライン・マテリアル切り替え回数を最小化できる。
pub struct NodePrimDraw {
    /// `lod_node_data[lod][node_idx]` の bind group を参照するためのインデックス。
    pub node_idx:     usize,
    /// `gpu_model.meshes[mesh_idx]` の参照に使用。
    pub mesh_idx:     usize,
    /// `gpu_mesh.primitives[prim_idx]` の参照に使用。
    pub prim_idx:     usize,
    /// スキンメッシュパイプラインを使うかどうか（ソートキー①）。
    pub is_skinned:   bool,
    /// マテリアルインデックス（ソートキー②）。`None` は デフォルトマテリアル。
    pub material_idx: Option<usize>,
}

/// N インスタンス分のモデル変換・CPU フラスタムカリング・距離 LOD。
///
/// ## フレームごとの流れ
/// 1. dirty 時のみ: rayon でワールド行列と AABB を全インスタンス分計算して CPU キャッシュに保存
/// 2. 毎フレーム: 視錐台テスト → 可視インスタンスをカメラ距離で LOD バケットに振り分け →
///    各 LOD のコンパクト行列をノードバッファへアップロードし `lod_visible_counts` を更新。
/// 3. render pass: LOD ごとに `draw_indexed(0..lod_index_count, 0, 0..lod_visible_count)` で描画
///
/// ## Dirty Flag
/// インスタンス変換が変化した場合のみワールド行列・AABB を再計算する。
/// 視錐台カリング・LOD 選択は毎フレーム実行する（カメラが動くため）。
pub struct InstancedModelBatch {
    /// lod_node_data[lod][node_idx] = (storage_buffer, bind_group)。
    /// メッシュを持たないノードは None。
    pub lod_node_data:      Vec<Vec<Option<(wgpu::Buffer, wgpu::BindGroup)>>>,
    pub num_instances:      u32,
    /// 各 LOD の直前フレーム可視インスタンス数（draw_indexed の instance_count に使用）
    pub lod_visible_counts: [u32; NUM_LODS],

    // ── 事前計算データ ───────────────────────────────────────
    mesh_node_indices: Vec<usize>,
    node_pos_map:      Vec<Option<usize>>,

    // ── CPU キャッシュ（dirty 時のみ再計算）─────────────────
    /// ワールド行列キャッシュ: flat[inst_idx * n_mesh_nodes + pos]
    world_mats_cache: Vec<ModelUniform>,
    /// ワールド空間 AABB キャッシュ: [inst_idx]
    world_aabbs:      Vec<GpuCullData>,
    /// メッシュノード数（キャッシュアクセスに使用）
    n_mesh_nodes:     usize,

    /// レンダーパスで参照する (node, prim) フラットリスト
    pub node_prim_list: Vec<NodePrimDraw>,
    /// node_prim_list の長さ
    pub n_prims:        u32,

    // ── ローカル AABB ────────────────────────────────────────
    model_aabb_min: [f32; 3],
    model_aabb_max: [f32; 3],

    // ── GPU スキニング ───────────────────────────────────────
    /// スキンとアニメーションを持つ場合のみ Some
    pub skin: Option<SkinComputeSystem>,

    // ── ID パス用コンパクトインスタンス ID バッファ ───────────
    /// lod_id_buffers[lod]: そのフレームの可視インスタンス元インデックスの配列（u32）
    pub lod_id_buffers: Vec<wgpu::Buffer>,
    /// lod_id_bgs[lod]: id_pass パイプラインの group 2 用 BindGroup
    pub lod_id_bgs:     Vec<wgpu::BindGroup>,

    // ── アウトライン / ピック用 CPU コンパクトインスタンスリスト ──
    /// lod_compact_insts[lod]: compact_idx → original_inst_idx マッピング（CPU 側）
    pub lod_compact_insts: Vec<Vec<usize>>,

    // ── Dirty Flag ───────────────────────────────────────────
    dirty: bool,

    /// インスタンスごとの安定アニメーション位相シード（InstanceMeta::anim_seed と同期）
    pub anim_seeds: Vec<u32>,
}

impl InstancedModelBatch {
    pub fn new(
        device:        &wgpu::Device,
        model:         &Model,
        model_bgl:     &wgpu::BindGroupLayout,
        skin_pipeline: &SkinComputePipeline,
        joint_bgl:     &wgpu::BindGroupLayout,
        id_data_bgl:   &wgpu::BindGroupLayout,
        num_instances: u32,
    ) -> Self {
        let n = num_instances.max(1) as usize;

        // ── メッシュノードの順序テーブル ────────────────────────
        let mesh_node_indices: Vec<usize> = model.nodes.iter().enumerate()
            .filter_map(|(i, nd)| nd.mesh_index.map(|_| i))
            .collect();
        let mut node_pos_map = vec![None::<usize>; model.nodes.len()];
        for (pos, &node_idx) in mesh_node_indices.iter().enumerate() {
            node_pos_map[node_idx] = Some(pos);
        }

        // ── (node, prim) フラットリスト ──────────────────────────
        let mut node_prim_list = Vec::new();
        let mut prim_slot = 0u32;

        for &node_idx in &mesh_node_indices {
            let mesh_idx = model.nodes[node_idx].mesh_index.unwrap();
            let mesh     = &model.meshes[mesh_idx];
            for (prim_idx, prim) in mesh.primitives.iter().enumerate() {
                node_prim_list.push(NodePrimDraw {
                    node_idx,
                    mesh_idx,
                    prim_idx,
                    is_skinned:   prim.is_skinned(),
                    material_idx: prim.material_index,
                });
                prim_slot += 1;
            }
        }
        let n_prims = prim_slot;

        // ── ソート: (is_skinned, material_idx) 昇順 ────────────
        node_prim_list.sort_by_key(|d| {
            (d.is_skinned as u8, d.material_idx.unwrap_or(usize::MAX))
        });

        // ── LOD ごとのノードインスタンスバッファ + bind group ────
        let stride = std::mem::size_of::<ModelUniform>() as u64;
        let inst_buf_size = (stride * n as u64).max(16);

        let lod_node_data: Vec<Vec<Option<(wgpu::Buffer, wgpu::BindGroup)>>> = (0..NUM_LODS)
            .map(|lod| {
                model.nodes.iter().enumerate().map(|(i, node)| {
                    if node.mesh_index.is_none() { return None; }
                    let buf = device.create_buffer(&wgpu::BufferDescriptor {
                        label:              Some(&format!("Node[{}] LOD[{}] Instance Buffer", i, lod)),
                        size:               inst_buf_size,
                        usage:              wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
                        mapped_at_creation: false,
                    });
                    let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                        label:   None,
                        layout:  model_bgl,
                        entries: &[wgpu::BindGroupEntry {
                            binding:  0,
                            resource: buf.as_entire_binding(),
                        }],
                    });
                    Some((buf, bg))
                }).collect()
            })
            .collect();

        // ── モデル AABB（ローカル空間）──────────────────────────
        let (model_aabb_min, model_aabb_max) = compute_model_aabb(model);

        // ── GPU スキニングシステム ────────────────────────────
        let skin = SkinComputeSystem::new(device, model, num_instances, skin_pipeline, joint_bgl);

        // ── ID パス用インスタンス ID バッファ（per-LOD）────────
        let id_buf_size = (4 * n as u64).max(16);
        let id_data: Vec<(wgpu::Buffer, wgpu::BindGroup)> = (0..NUM_LODS)
            .map(|lod| {
                let buf = device.create_buffer(&wgpu::BufferDescriptor {
                    label:              Some(&format!("Instance ID Buffer LOD[{}]", lod)),
                    size:               id_buf_size,
                    usage:              wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
                    mapped_at_creation: false,
                });
                let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                    label:  None,
                    layout: id_data_bgl,
                    entries: &[wgpu::BindGroupEntry {
                        binding:  0,
                        resource: buf.as_entire_binding(),
                    }],
                });
                (buf, bg)
            })
            .collect();
        let (lod_id_buffers, lod_id_bgs): (Vec<_>, Vec<_>) = id_data.into_iter().unzip();

        let n_mesh_nodes = mesh_node_indices.len();
        Self {
            lod_node_data,
            num_instances,
            lod_visible_counts: [0; NUM_LODS],
            mesh_node_indices,
            node_pos_map,
            world_mats_cache: Vec::new(),
            world_aabbs:      Vec::new(),
            n_mesh_nodes,
            node_prim_list,
            n_prims,
            model_aabb_min,
            model_aabb_max,
            skin,
            lod_id_buffers,
            lod_id_bgs,
            lod_compact_insts: vec![Vec::new(); NUM_LODS],
            dirty: true,
            anim_seeds: Vec::new(),
        }
    }

    /// 変換が変化したことを通知する。
    /// 次の `update()` でワールド行列・AABB が再計算される。
    pub fn mark_dirty(&mut self) { self.dirty = true; }

    /// InstanceMeta::anim_seed の配列を同期する。
    /// インスタンスの追加・削除・Undo/Redo 後に呼び出す。
    pub fn set_anim_seeds(&mut self, seeds: &[u32]) {
        self.anim_seeds.clear();
        self.anim_seeds.extend_from_slice(seeds);
    }

    /// 毎フレーム呼び出す更新関数。
    ///
    /// 1. `dirty` な場合のみ: rayon でワールド行列と AABB を全インスタンス分計算して
    ///    CPU キャッシュ（`world_mats_cache`・`world_aabbs`）に保存。
    /// 2. 毎フレーム: 視錐台テスト → カメラ距離で LOD バケットに振り分け →
    ///    各 LOD の可視行列をノードバッファへアップロードし `lod_visible_counts` を更新。
    pub fn update(
        &mut self,
        queue:           &wgpu::Queue,
        model:           &Model,
        root_transforms: &[[[f32; 4]; 4]],
        frustum_planes:  &[[f32; 4]; 6],
        camera_pos:      [f32; 3],
        anim_time:       f32,
    ) {
        let n_instances  = root_transforms.len();
        let n_mesh_nodes = self.n_mesh_nodes;
        if n_mesh_nodes == 0 { return; }
        if n_instances == 0 {
            self.dirty = false;
            self.world_mats_cache.clear();
            self.world_aabbs.clear();
            for lod in 0..NUM_LODS {
                self.lod_visible_counts[lod] = 0;
                self.lod_compact_insts[lod].clear();
            }
            return;
        }

        // ── ① ワールド行列と AABB をキャッシュ（dirty 時のみ）──────
        if self.dirty {
            self.dirty = false;

            let mut flat = vec![ModelUniform::identity(); n_instances * n_mesh_nodes];
            let node_pos_map = &self.node_pos_map;
            let root_nodes   = &model.root_nodes;

            flat.par_chunks_mut(n_mesh_nodes)
                .zip(root_transforms.par_iter())
                .for_each(|(chunk, root_t)| {
                    for &root_node in root_nodes {
                        fill_chunk(model, root_node, root_t, node_pos_map, chunk);
                    }
                });
            self.world_mats_cache = flat;

            let aabb_min = self.model_aabb_min;
            let aabb_max = self.model_aabb_max;
            self.world_aabbs = root_transforms
                .par_iter()
                .map(|mat| {
                    let (wmin, wmax) = transform_aabb(aabb_min, aabb_max, mat);
                    GpuCullData { aabb_min: wmin, _pad0: 0.0, aabb_max: wmax, _pad1: 0.0 }
                })
                .collect();
        }

        // ── ② 視錐台カリング + 距離 LOD → per-LOD 可視インスタンスリスト ─
        let mut lod_visible_insts: Vec<Vec<usize>> = vec![Vec::new(); NUM_LODS];
        let mut compact: Vec<Vec<Vec<ModelUniform>>> = (0..NUM_LODS)
            .map(|_| (0..n_mesh_nodes).map(|_| Vec::with_capacity(n_instances / NUM_LODS + 1)).collect())
            .collect();

        for (inst_idx, aabb) in self.world_aabbs.iter().enumerate() {
            if !test_aabb_frustum(frustum_planes, aabb.aabb_min, aabb.aabb_max) { continue; }

            let cx = (aabb.aabb_min[0] + aabb.aabb_max[0]) * 0.5;
            let cy = (aabb.aabb_min[1] + aabb.aabb_max[1]) * 0.5;
            let cz = (aabb.aabb_min[2] + aabb.aabb_max[2]) * 0.5;
            let dx = cx - camera_pos[0];
            let dy = cy - camera_pos[1];
            let dz = cz - camera_pos[2];
            let dist_sq = dx*dx + dy*dy + dz*dz;

            let lod = if dist_sq < LOD_DIST_SQ[0] { 0 }
                      else if dist_sq < LOD_DIST_SQ[1] { 1 }
                      else if dist_sq < LOD_DIST_SQ[2] { 2 }
                      else { 3 };

            lod_visible_insts[lod].push(inst_idx);
            for pos in 0..n_mesh_nodes {
                compact[lod][pos].push(self.world_mats_cache[inst_idx * n_mesh_nodes + pos]);
            }
        }

        // ── ③ 各 LOD: モデル行列・インスタンス ID をアップロード ──────
        // CPU 側コンパクトリストを保存（アウトライン描画で compact_idx を逆引きするため）
        for lod in 0..NUM_LODS {
            self.lod_compact_insts[lod] = std::mem::take(&mut lod_visible_insts[lod]);
        }

        for lod in 0..NUM_LODS {
            let visible = self.lod_compact_insts[lod].len() as u32;
            self.lod_visible_counts[lod] = visible;

            if visible > 0 {
                for (pos, &node_idx) in self.mesh_node_indices.iter().enumerate() {
                    if let Some((buf, _)) = &self.lod_node_data[lod][node_idx] {
                        queue.write_buffer(buf, 0, bytemuck::cast_slice(&compact[lod][pos]));
                    }
                }

                // ID パス用: 元インスタンスインデックスをアップロード
                let ids: Vec<u32> = self.lod_compact_insts[lod].iter().map(|&i| i as u32).collect();
                queue.write_buffer(&self.lod_id_buffers[lod], 0, bytemuck::cast_slice(&ids));
            }

            // スキンシステムへの anim_times 転送（GPU スキニング計算の入力）
            if let Some(skin) = &self.skin {
                skin.upload_lod_times(queue, lod, &self.lod_compact_insts[lod], &self.anim_seeds, anim_time);
            }
        }
    }

    /// 指定インスタンスが現在フレームで可視な場合、その LOD と compact index を返す。
    /// カリングで非表示の場合は None。
    pub fn find_compact_index(&self, inst_idx: u32) -> Option<(usize, u32)> {
        for lod in 0..NUM_LODS {
            if let Some(pos) = self.lod_compact_insts[lod].iter().position(|&i| i == inst_idx as usize) {
                return Some((lod, pos as u32));
            }
        }
        None
    }

    /// GPU スキニング コンピュートシェーダを全 LOD 分ディスパッチする。
    /// レンダーパスより前にコマンドエンコーダに積む必要がある。
    pub fn dispatch_skin(
        &self,
        encoder:  &mut wgpu::CommandEncoder,
        pipeline: &SkinComputePipeline,
    ) {
        if let Some(skin) = &self.skin {
            for lod in 0..NUM_LODS {
                skin.dispatch_lod(encoder, pipeline, lod, self.lod_visible_counts[lod]);
            }
        }
    }

    /// LOD の頂点シェーダ用ジョイント BG（group 3）を返す。
    /// スキンなしの場合は None。
    pub fn joint_vs_bg(&self, lod: usize) -> Option<&wgpu::BindGroup> {
        self.skin.as_ref().map(|s| &s.lod_joint_vs_bgs[lod])
    }
}

/// インスタンス 1 件分のシーングラフをトラバースし、
/// メッシュノードのワールド行列をチャンクの対応列に書き込む。
fn fill_chunk(
    model:        &Model,
    node_idx:     usize,
    parent_mat:   &[[f32; 4]; 4],
    node_pos_map: &[Option<usize>],
    chunk:        &mut [ModelUniform],
) {
    let node  = &model.nodes[node_idx];
    let world = mat4_mul(parent_mat, &node.local_matrix);

    if node.mesh_index.is_some() {
        if let Some(pos) = node_pos_map[node_idx] {
            let world_t = transpose4x4(&world);
            chunk[pos]  = ModelUniform::from_matrix(world_t);
        }
    }

    for &child in &node.children {
        fill_chunk(model, child, &world, node_pos_map, chunk);
    }
}

/// 4×4 行列の転置（行優先 → 列優先変換に使用）
fn transpose4x4(m: &[[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let mut out = [[0.0f32; 4]; 4];
    for i in 0..4 {
        for j in 0..4 {
            out[i][j] = m[j][i];
        }
    }
    out
}

/// 行優先 4×4 行列乗算
pub fn mat4_mul(a: &[[f32; 4]; 4], b: &[[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let mut out = [[0.0f32; 4]; 4];
    for i in 0..4 {
        for j in 0..4 {
            for k in 0..4 {
                out[i][j] += a[i][k] * b[k][j];
            }
        }
    }
    out
}

// ============================================================
//  CameraBuffer
// ============================================================

/// カメラユニフォームバッファ + bind group。
pub struct CameraBuffer {
    pub buffer:     wgpu::Buffer,
    pub bind_group: wgpu::BindGroup,
}

impl CameraBuffer {
    pub fn new(device: &wgpu::Device, camera_bgl: &wgpu::BindGroupLayout) -> Self {
        let buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label:              Some("Camera Uniform Buffer"),
            size:               std::mem::size_of::<CameraUniform>() as u64,
            usage:              wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:  Some("Camera BG"),
            layout: camera_bgl,
            entries: &[wgpu::BindGroupEntry {
                binding:  0,
                resource: buffer.as_entire_binding(),
            }],
        });
        Self { buffer, bind_group }
    }

    /// カメラパラメータを GPU にアップロードする。
    pub fn update(&self, queue: &wgpu::Queue, uniform: &CameraUniform) {
        queue.write_buffer(&self.buffer, 0, bytemuck::bytes_of(uniform));
    }
}

// ============================================================
//  GpuLineBatch — ラインバッチの GPU バッファ
// ============================================================

/// `LineBatch::build()` で生成した描画可能なライン頂点バッファ。
pub struct GpuLineBatch {
    pub vertex_buffer: wgpu::Buffer,
    pub vertex_count:  u32,
}

impl GpuLineBatch {
    pub fn new(device: &wgpu::Device, vertices: &[ColorVertex]) -> Self {
        let vertex_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Line Batch Vertex Buffer"),
            contents: bytemuck::cast_slice(vertices),
            usage:    wgpu::BufferUsages::VERTEX,
        });
        Self { vertex_buffer, vertex_count: vertices.len() as u32 }
    }
}

// ============================================================
//  GpuGizmoBatch — ギズモ描画用 GPU バッファ
// ============================================================

/// `GizmoBatch::build()` で生成した描画可能なギズモバッファ。
///
/// - `line_buffer`: 太線クワッド（GizmoVertex 形式）
/// - `tri_buffer` : ソリッド三角形（ColorVertex 形式）
pub struct GpuGizmoBatch {
    pub line_buffer: Option<wgpu::Buffer>,
    pub line_count:  u32,
    pub tri_buffer:  Option<wgpu::Buffer>,
    pub tri_count:   u32,
}

impl GpuGizmoBatch {
    pub fn new(device: &wgpu::Device, lines: &[GizmoVertex], tris: &[ColorVertex]) -> Self {
        let line_buffer = if lines.is_empty() { None } else {
            Some(device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label:    Some("Gizmo Line Buffer"),
                contents: bytemuck::cast_slice(lines),
                usage:    wgpu::BufferUsages::VERTEX,
            }))
        };
        let tri_buffer = if tris.is_empty() { None } else {
            Some(device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label:    Some("Gizmo Tri Buffer"),
                contents: bytemuck::cast_slice(tris),
                usage:    wgpu::BufferUsages::VERTEX,
            }))
        };
        Self {
            line_buffer,
            line_count: lines.len() as u32,
            tri_buffer,
            tri_count:  tris.len() as u32,
        }
    }
}
