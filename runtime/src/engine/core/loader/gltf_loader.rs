use std::path::Path;
use gltf::animation::util::ReadOutputs;
use super::model::*;
use super::LoadError;

// ============================================================
//  エントリポイント
// ============================================================

pub fn load(path: &Path) -> Result<Model, LoadError> {
    let path_str = path.to_str().unwrap_or("");

    // assets:// 仮想パスの場合は PAK から読み込む（GLB 推奨: 自己完結形式）。
    // 通常の絶対パスはファイルシステムから直接読む。
    let (document, buffers, images) = if path_str.starts_with(crate::engine::asset_fs::ASSETS_SCHEME) {
        let bytes = crate::engine::asset_fs::read_bytes(path_str)
            .map_err(|e| LoadError::Io(format!("PAK read failed for {path_str}: {e}")))?;
        gltf::import_slice(&bytes)
            .map_err(|e| LoadError::Parse(e.to_string()))?
    } else {
        gltf::import(path)
            .map_err(|e| LoadError::Parse(e.to_string()))?
    };

    let name = path.file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("unnamed")
        .to_string();

    let textures   = load_textures(&document, &images);
    let materials  = load_materials(&document);
    let meshes     = load_meshes(&document, &buffers);
    let animations = load_animations(&document, &buffers);
    let skins      = load_skins(&document, &buffers);
    let (nodes, root_nodes) = load_nodes(&document);

    Ok(Model { name, nodes, root_nodes, meshes, materials, textures, animations, skins })
}

// ============================================================
//  テクスチャ
// ============================================================

fn load_textures(
    document: &gltf::Document,
    images: &[gltf::image::Data],
) -> Vec<TextureData> {
    // ── 線形テクスチャインデックスの事前収集 ──────────────────────
    // glTF spec:
    //   sRGB テクスチャ（Rgba8UnormSrgb） : ベースカラー・エミッシブ
    //   線形テクスチャ（Rgba8Unorm）      : 法線・メタリックラフネス・オクルージョン
    //
    // GPU は Rgba8UnormSrgb フォーマットのテクスチャをサンプリング時に
    // 自動で sRGB → linear デコードする。線形データ（法線・MR・AO）を
    // Rgba8UnormSrgb として誤ってロードすると、値が圧縮されて正しい
    // PBR 計算ができなくなる（例: roughness 0.5 → 0.214 に化けて
    // スペキュラーが爆発的に明るくなる）。
    //
    // マテリアル定義を先読みし、各テクスチャの実際の用途を判定する。
    let linear_indices: std::collections::HashSet<usize> = document.materials()
        .flat_map(|mat| {
            let pbr = mat.pbr_metallic_roughness();
            [
                mat.normal_texture().map(|t| t.texture().index()),
                pbr.metallic_roughness_texture().map(|t| t.texture().index()),
                mat.occlusion_texture().map(|t| t.texture().index()),
            ]
            .into_iter()
            .flatten()
        })
        .collect();

    document.textures().map(|tex| {
        let img     = &images[tex.source().index()];
        let pixels  = normalize_to_rgba8(&img.pixels, img.format, img.width, img.height);
        let sampler = tex.sampler();

        // 用途に応じてフォーマットを切り替える
        // linear=true  → Rgba8Unorm   （法線・MR・AO：線形データをそのまま使用）
        // linear=false → Rgba8UnormSrgb（ベースカラー・エミッシブ：GPU が sRGB デコード）
        let linear = linear_indices.contains(&tex.index());

        TextureData {
            name:   tex.name().map(String::from),
            source: TextureSource::Embedded {
                width:  img.width,
                height: img.height,
                pixels,
            },
            linear,
            sampler: SamplerData {
                mag_filter: sampler.mag_filter()
                    .map(conv_mag_filter)
                    .unwrap_or(FilterMode::Linear),
                min_filter: sampler.min_filter()
                    .map(conv_min_filter)
                    .unwrap_or(FilterMode::LinearMipmapLinear),
                wrap_u: conv_wrap(sampler.wrap_s()),
                wrap_v: conv_wrap(sampler.wrap_t()),
            },
        }
    }).collect()
}

/// glTF の様々なピクセル形式を RGBA8 に正規化する。
fn normalize_to_rgba8(
    src: &[u8],
    format: gltf::image::Format,
    _width: u32,
    _height: u32,
) -> Vec<u8> {
    use gltf::image::Format::*;
    match format {
        R8G8B8A8 => src.to_vec(),
        R8G8B8   => src.chunks_exact(3)
            .flat_map(|c| [c[0], c[1], c[2], 255])
            .collect(),
        R8G8     => src.chunks_exact(2)
            .flat_map(|c| [c[0], c[1], 0, 255])
            .collect(),
        R8       => src.iter()
            .flat_map(|&r| [r, r, r, 255])
            .collect(),
        R16G16B16A16 => src.chunks_exact(8)
            .flat_map(|c| {
                let rgba = [
                    u16::from_le_bytes([c[0], c[1]]),
                    u16::from_le_bytes([c[2], c[3]]),
                    u16::from_le_bytes([c[4], c[5]]),
                    u16::from_le_bytes([c[6], c[7]]),
                ];
                rgba.map(|v| (v >> 8) as u8)
            })
            .collect(),
        R16G16B16 => src.chunks_exact(6)
            .flat_map(|c| {
                let r = (u16::from_le_bytes([c[0], c[1]]) >> 8) as u8;
                let g = (u16::from_le_bytes([c[2], c[3]]) >> 8) as u8;
                let b = (u16::from_le_bytes([c[4], c[5]]) >> 8) as u8;
                [r, g, b, 255]
            })
            .collect(),
        // その他の形式はそのまま返す（GPU 側で処理）
        _ => src.to_vec(),
    }
}

fn conv_mag_filter(f: gltf::texture::MagFilter) -> FilterMode {
    match f {
        gltf::texture::MagFilter::Nearest => FilterMode::Nearest,
        gltf::texture::MagFilter::Linear  => FilterMode::Linear,
    }
}

fn conv_min_filter(f: gltf::texture::MinFilter) -> FilterMode {
    use gltf::texture::MinFilter::*;
    match f {
        Nearest              => FilterMode::Nearest,
        Linear               => FilterMode::Linear,
        NearestMipmapNearest => FilterMode::NearestMipmapNearest,
        LinearMipmapNearest  => FilterMode::LinearMipmapNearest,
        NearestMipmapLinear  => FilterMode::NearestMipmapLinear,
        LinearMipmapLinear   => FilterMode::LinearMipmapLinear,
    }
}

fn conv_wrap(w: gltf::texture::WrappingMode) -> WrapMode {
    use gltf::texture::WrappingMode::*;
    match w {
        ClampToEdge    => WrapMode::ClampToEdge,
        MirroredRepeat => WrapMode::MirroredRepeat,
        Repeat         => WrapMode::Repeat,
    }
}

// ============================================================
//  マテリアル
// ============================================================

fn load_materials(document: &gltf::Document) -> Vec<Material> {
    document.materials().map(|mat| {
        let pbr  = mat.pbr_metallic_roughness();

        let base_color_factor = pbr.base_color_factor();
        let base_color_texture = pbr.base_color_texture().map(|t| TextureInfo {
            texture_index: t.texture().index(),
            tex_coord_set: t.tex_coord(),
        });
        let metallic_roughness_texture = pbr.metallic_roughness_texture().map(|t| TextureInfo {
            texture_index: t.texture().index(),
            tex_coord_set: t.tex_coord(),
        });
        let normal_texture = mat.normal_texture().map(|t| NormalTextureInfo {
            texture_index: t.texture().index(),
            tex_coord_set: t.tex_coord(),
            scale:         t.scale(),
        });
        let occlusion_texture = mat.occlusion_texture().map(|t| OcclusionTextureInfo {
            texture_index: t.texture().index(),
            tex_coord_set: t.tex_coord(),
            strength:      t.strength(),
        });
        let emissive_texture = mat.emissive_texture().map(|t| TextureInfo {
            texture_index: t.texture().index(),
            tex_coord_set: t.tex_coord(),
        });
        let alpha_mode = match mat.alpha_mode() {
            gltf::material::AlphaMode::Opaque => AlphaMode::Opaque,
            gltf::material::AlphaMode::Mask   => AlphaMode::Mask,
            gltf::material::AlphaMode::Blend  => AlphaMode::Blend,
        };

        Material {
            name: mat.name().unwrap_or("").to_string(),
            base_color_factor,
            base_color_texture,
            metallic_factor:             pbr.metallic_factor(),
            roughness_factor:            pbr.roughness_factor(),
            metallic_roughness_texture,
            normal_texture,
            occlusion_texture,
            emissive_factor:  mat.emissive_factor(),
            emissive_texture,
            alpha_mode,
            alpha_cutoff: mat.alpha_cutoff().unwrap_or(0.5),
            double_sided:     mat.double_sided(),
        }
    }).collect()
}

// ============================================================
//  座標系変換ユーティリティ
// ============================================================

/// glTF 右手座標系 → エンジン左手座標系 の行列変換。
///
/// Z 軸反転: M_lh = C * M_rh * C  (C = diag(1, 1, -1, 1))
/// 具体的には (row==2) XOR (col==2) の要素を符号反転する。
fn rh_to_lh_mat4(m: [[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let mut out = m;
    for j in 0..4usize {
        if j != 2 { out[2][j] = -m[2][j]; }   // 行 2 を反転（[2][2] 除く）
    }
    for i in 0..4usize {
        if i != 2 { out[i][2] = -m[i][2]; }   // 列 2 を反転（[2][2] 除く）
    }
    out
}

/// クォータニオン（xyzw）を RH→LH 変換する。
///
/// Z 軸反転では X・Y 軸まわりの回転方向が反転するため、
/// qx と qy を符号反転する: q_lh = [-qx, -qy, qz, qw]
#[inline]
fn rh_to_lh_quat(q: [f32; 4]) -> [f32; 4] {
    [-q[0], -q[1], q[2], q[3]]
}

// ============================================================
//  メッシュ
// ============================================================

fn load_meshes(
    document: &gltf::Document,
    buffers: &[gltf::buffer::Data],
) -> Vec<Mesh> {
    document.meshes().map(|mesh| {
        let primitives = mesh.primitives().map(|prim| {
            load_primitive(&prim, buffers)
        }).collect();

        Mesh {
            name: mesh.name().unwrap_or("").to_string(),
            primitives,
        }
    }).collect()
}

fn load_primitive(
    prim: &gltf::Primitive<'_>,
    buffers: &[gltf::buffer::Data],
) -> Primitive {
    let reader = prim.reader(|b| Some(&*buffers[b.index()]));

    // ── 位置（必須）────────────────────────────────────────
    let positions: Vec<[f32; 3]> = reader
        .read_positions()
        .map(|it| it.collect())
        .unwrap_or_default();
    let n = positions.len();

    // ── その他の属性（なければデフォルト）──────────────────
    let normals: Vec<[f32; 3]> = reader
        .read_normals()
        .map(|it| it.collect())
        .unwrap_or_else(|| vec![[0.0, 1.0, 0.0]; n]);

    let tangents: Vec<[f32; 4]> = reader
        .read_tangents()
        .map(|it| it.collect())
        .unwrap_or_else(|| vec![[1.0, 0.0, 0.0, 1.0]; n]);

    let uvs0: Vec<[f32; 2]> = reader
        .read_tex_coords(0)
        .map(|v| v.into_f32().collect())
        .unwrap_or_else(|| vec![[0.0; 2]; n]);

    let uvs1: Vec<[f32; 2]> = reader
        .read_tex_coords(1)
        .map(|v| v.into_f32().collect())
        .unwrap_or_else(|| vec![[0.0; 2]; n]);

    let colors: Vec<[f32; 4]> = reader
        .read_colors(0)
        .map(|v| v.into_rgba_f32().collect())
        .unwrap_or_else(|| vec![[1.0; 4]; n]);

    // ── インデックス（なければ順番に生成）──────────────────
    // Z 反転後も perspective_lh によってスクリーン空間の巻き順（CCW）は保存されるため
    // 巻き順の反転は不要。変換前後で左手系 GPU が同じ front-face 判定を行う。
    let indices: Vec<u32> = reader
        .read_indices()
        .map(|v| v.into_u32().collect())
        .unwrap_or_else(|| (0..n as u32).collect());

    // ── 頂点構築 ───────────────────────────────────────────
    // RH→LH: 位置・法線・接線の Z 成分を反転し、左手座標系に変換する。
    // 接線の w（ビタンジェント符号）も反転（座標系の手性が変わるため）。
    let vertices: Vec<Vertex> = (0..n).map(|i| Vertex {
        position: [positions[i][0],  positions[i][1],  -positions[i][2]],
        normal:   [normals[i][0],    normals[i][1],    -normals[i][2]],
        tangent:  [tangents[i][0],   tangents[i][1],   -tangents[i][2], -tangents[i][3]],
        uv0:      uvs0[i],
        uv1:      uvs1[i],
        color:    colors[i],
    }).collect();

    // ── スキニング ─────────────────────────────────────────
    let joints: Vec<[u16; 4]> = reader
        .read_joints(0)
        .map(|v| v.into_u16().collect())
        .unwrap_or_default();

    let weights: Vec<[f32; 4]> = reader
        .read_weights(0)
        .map(|v| v.into_f32().collect())
        .unwrap_or_default();

    let skin_vertices: Vec<SkinVertex> = if joints.len() == n {
        (0..n).map(|i| SkinVertex {
            joints:  joints[i],
            weights: weights.get(i).copied().unwrap_or([1.0, 0.0, 0.0, 0.0]),
        }).collect()
    } else {
        Vec::new()
    };

    // LOD 生成・メッシュレット分割は初回生成コストの主要因候補のため個別に計測する
    // （[SEED cache] 初回ロード行へ内訳出力）。
    let t_lod = std::time::Instant::now();
    let lod_indices = generate_lod_indices(&indices, &vertices);
    super::gen_timing::add_lod(t_lod.elapsed());

    // LOD0 メッシュレット分割（GPU カリング第1弾）。スキンメッシュは対象外。
    let t_ml = std::time::Instant::now();
    let (meshlets, meshlet_vertices, meshlet_triangles) =
        build_meshlets_for_primitive(&indices, &vertices, !skin_vertices.is_empty());
    super::gen_timing::add_meshlet(t_ml.elapsed());
    Primitive {
        vertices,
        skin_vertices,
        indices,
        material_index: prim.material().index(),
        lod_indices,
        meshlets,
        meshlet_vertices,
        meshlet_triangles,
    }
}

// ============================================================
//  メッシュレット分割（GPU カリング第1弾, LOD0 のみ）
// ============================================================

/// 1 メッシュレットの最大頂点数（meshopt 制約: <= 64）。
const MESHLET_MAX_VERTS: usize = 64;
/// 1 メッシュレットの最大三角形数（meshopt 制約: <= 126 かつ 4 の倍数）。
const MESHLET_MAX_TRIS: usize = 124;
/// コーン生成の重み（0=クラスタサイズ優先 / 1=コーンカリング効率優先。中庸の 0.5）。
const MESHLET_CONE_WEIGHT: f32 = 0.5;

/// LOD0 のインデックス/頂点から meshopt でメッシュレットを分割し、
/// 各メッシュレットの境界球・法線コーンを計算して記述子を返す。
///
/// 戻り値: `(記述子, meshlet_vertices, meshlet_triangles)`。
/// スキンメッシュ（`skinned=true`）や分割不能・失敗時は全て空を返す
/// （→ 従来の LOD0 描画経路が使われる）。
pub(super) fn build_meshlets_for_primitive(
    indices:  &[u32],
    vertices: &[Vertex],
    skinned:  bool,
) -> (Vec<MeshletDesc>, Vec<u32>, Vec<u8>) {
    use meshopt::VertexDataAdapter;

    let empty = (Vec::new(), Vec::new(), Vec::new());

    // スキンメッシュはロード時の静的境界が毎フレームの変形で無効になるため対象外。
    if skinned { return empty; }
    // 三角形が無い / 不正なインデックス数なら分割しない。
    if indices.len() < 3 || indices.len() % 3 != 0 { return empty; }

    // position は Vertex の先頭フィールド（offset 0, ストライド = size_of::<Vertex>()）。
    let adapter = match VertexDataAdapter::new(
        bytemuck::cast_slice::<Vertex, u8>(vertices),
        std::mem::size_of::<Vertex>(),
        0,
    ) {
        Ok(a) => a,
        Err(_) => return empty,
    };

    // meshopt でメッシュレット分割。
    let ms = meshopt::build_meshlets(
        indices,
        &adapter,
        MESHLET_MAX_VERTS,
        MESHLET_MAX_TRIS,
        MESHLET_CONE_WEIGHT,
    );
    if ms.meshlets.is_empty() { return empty; }

    // 各メッシュレットの境界球・法線コーンを計算して記述子を組む。
    let mut descs = Vec::with_capacity(ms.len());
    for i in 0..ms.len() {
        let raw    = &ms.meshlets[i]; // ffi: vertex_offset / triangle_offset / vertex_count / triangle_count
        let bounds = meshopt::compute_meshlet_bounds(ms.get(i), &adapter);
        descs.push(MeshletDesc {
            vertex_offset:   raw.vertex_offset,
            triangle_offset: raw.triangle_offset,
            vertex_count:    raw.vertex_count,
            triangle_count:  raw.triangle_count,
            center:      bounds.center,
            radius:      bounds.radius,
            cone_axis:   bounds.cone_axis,
            cone_cutoff: bounds.cone_cutoff,
        });
    }

    // build_meshlets の vertices/triangles は最悪ケース長（メッシュレット数×上限）で
    // 確保されるため、実使用範囲まで切り詰めてキャッシュサイズを抑える。
    // オフセットは切り詰め後も不変（先頭からの絶対位置のため）。
    let used_v = descs.iter().map(|d| (d.vertex_offset + d.vertex_count) as usize).max().unwrap_or(0);
    let used_t = descs.iter().map(|d| (d.triangle_offset + d.triangle_count * 3) as usize).max().unwrap_or(0);
    let mut mv = ms.vertices;
    let mut mt = ms.triangles;
    mv.truncate(used_v);
    mt.truncate(used_t);
    // truncate は容量を解放しないため、最悪ケース確保分（メッシュレット数×上限）の
    // 余剰メモリを明示的に返す（多数プリミティブのモデルで保持メモリが膨らむのを防ぐ）。
    mv.shrink_to_fit();
    mt.shrink_to_fit();

    (descs, mv, mt)
}

// ============================================================
//  LOD インデックス生成
// ============================================================

/// meshopt を使ってロード時に LOD インデックスバッファを生成する。
///
/// 戻り値: `[LOD1_indices, LOD2_indices, LOD3_indices]`（簡略化できなかった段階で打ち切り）。
/// 各 LOD の目標三角形数は元の 50 % / 25 % / 10 %。
fn generate_lod_indices(indices: &[u32], vertices: &[super::model::Vertex]) -> Vec<Vec<u32>> {
    use meshopt::VertexDataAdapter;
    use super::model::Vertex;

    // 三角形が 4 枚未満なら LOD 生成不要
    if indices.len() < 12 { return vec![]; }

    let adapter = match VertexDataAdapter::new(
        bytemuck::cast_slice::<Vertex, u8>(vertices),
        std::mem::size_of::<Vertex>(),
        0,  // position は Vertex の先頭フィールド (offset 0)
    ) {
        Ok(a) => a,
        Err(_) => return vec![],
    };

    let base_count = indices.len();
    let target_error = 1e-2_f32;
    // LOD1: 50%, LOD2: 25%, LOD3: 10%
    let ratios: &[f32] = &[0.5, 0.25, 0.1];

    let mut result = Vec::new();
    for &ratio in ratios {
        // 3 の倍数に切り捨て（三角形単位）、最低 1 三角形
        let target_count = ((base_count as f32 * ratio) as usize / 3 * 3).max(3);
        let simplified = meshopt::simplify(
            indices,
            &adapter,
            target_count,
            target_error,
            meshopt::SimplifyOptions::None,
            None,
        );
        if simplified.is_empty() || simplified.len() >= base_count {
            break;  // これ以上簡略化できなければ打ち切り
        }
        result.push(simplified);
    }
    result
}

// ============================================================
//  ノード（シーングラフ）
// ============================================================

fn load_nodes(document: &gltf::Document) -> (Vec<ModelNode>, Vec<usize>) {
    let mut nodes: Vec<ModelNode> = document.nodes().map(|node| {
        // glTF は列優先行列 → 行優先に転置、さらに RH→LH 変換（Z 軸反転）
        let local_matrix = rh_to_lh_mat4(transpose_mat4(node.transform().matrix()));

        // TRS を取得（アニメーション補間用）、RH→LH 変換を適用
        let (translation, rotation, scale) = match node.transform() {
            gltf::scene::Transform::Decomposed { translation, rotation, scale } => {
                // 平行移動 Z を反転、クォータニオン XY を反転
                let t_lh = [translation[0], translation[1], -translation[2]];
                let r_lh = rh_to_lh_quat(rotation);
                (t_lh, r_lh, scale)
            }
            gltf::scene::Transform::Matrix { .. } => {
                // matrix ノードは通常アニメーションされないため恒等値を使用
                ([0.0_f32, 0.0, 0.0], [0.0_f32, 0.0, 0.0, 1.0], [1.0_f32, 1.0, 1.0])
            }
        };

        ModelNode {
            name:         node.name().unwrap_or("").to_string(),
            local_matrix,
            translation,
            rotation,
            scale,
            mesh_index:   node.mesh().map(|m| m.index()),
            skin_index:   node.skin().map(|s| s.index()),
            children:     node.children().map(|c| c.index()).collect(),
            parent:       None, // 後で補完
        }
    }).collect();

    // 親インデックスを補完
    let children_map: Vec<Vec<usize>> = nodes.iter()
        .map(|n| n.children.clone())
        .collect();
    for (parent_idx, children) in children_map.iter().enumerate() {
        for &child_idx in children {
            if let Some(n) = nodes.get_mut(child_idx) {
                n.parent = Some(parent_idx);
            }
        }
    }

    // ルートノード（親なし）を収集
    let root_nodes: Vec<usize> = nodes.iter()
        .enumerate()
        .filter_map(|(i, n)| if n.parent.is_none() { Some(i) } else { None })
        .collect();

    (nodes, root_nodes)
}

/// 列優先 4x4 行列（glTF）→ 行優先 4x4 行列（本エンジン）
fn transpose_mat4(m: [[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let mut out = [[0.0f32; 4]; 4];
    for r in 0..4 {
        for c in 0..4 {
            out[r][c] = m[c][r];
        }
    }
    out
}

// ============================================================
//  アニメーション
// ============================================================

fn load_animations(
    document: &gltf::Document,
    buffers: &[gltf::buffer::Data],
) -> Vec<Animation> {
    document.animations().map(|anim| {
        let mut duration = 0.0_f32;

        let channels = anim.channels().filter_map(|ch| {
            let target = ch.target();
            let sampler = ch.sampler();

            // utils feature では Channel に reader() が生えている
            let reader = ch.reader(|b: gltf::Buffer<'_>| Some(&*buffers[b.index()]));

            let timestamps: Vec<f32> = reader
                .read_inputs()
                .map(|it| it.collect::<Vec<f32>>())
                .unwrap_or_default();

            if let Some(&last) = timestamps.last() {
                duration = duration.max(last);
            }

            let interpolation = match sampler.interpolation() {
                gltf::animation::Interpolation::Linear      => Interpolation::Linear,
                gltf::animation::Interpolation::Step        => Interpolation::Step,
                gltf::animation::Interpolation::CubicSpline => Interpolation::CubicSpline,
            };

            // RH→LH 変換: 平行移動は Z 反転、回転はクォータニオン XY 反転
            let outputs = match reader.read_outputs()? {
                ReadOutputs::Translations(it) =>
                    AnimationOutputs::Translations(
                        it.map(|t| [t[0], t[1], -t[2]]).collect()
                    ),
                ReadOutputs::Rotations(rot) =>
                    AnimationOutputs::Rotations(
                        rot.into_f32().map(rh_to_lh_quat).collect()
                    ),
                ReadOutputs::Scales(it) =>
                    AnimationOutputs::Scales(it.collect()),
                ReadOutputs::MorphTargetWeights(w) =>
                    AnimationOutputs::MorphWeights(w.into_f32().collect()),
            };

            Some(AnimationChannel {
                target_node_index: target.node().index(),
                sampler: AnimationSampler { interpolation, timestamps, outputs },
            })
        }).collect();

        Animation {
            name: anim.name().unwrap_or("").to_string(),
            duration,
            channels,
        }
    }).collect()
}

// ============================================================
//  スキン
// ============================================================

fn load_skins(
    document: &gltf::Document,
    buffers: &[gltf::buffer::Data],
) -> Vec<Skin> {
    document.skins().map(|skin| {
        let joints: Vec<gltf::Node<'_>> = skin.joints().collect();
        let reader = skin.reader(|b| Some(&*buffers[b.index()]));

        // インバースバインド行列（列優先 → 行優先に転置、さらに RH→LH 変換）
        let ibms: Vec<[[f32; 4]; 4]> = reader
            .read_inverse_bind_matrices()
            .map(|it| it.map(|m| rh_to_lh_mat4(transpose_mat4(m))).collect())
            .unwrap_or_else(|| vec![ModelNode::identity_matrix(); joints.len()]);

        // スキンのルートジョイント（skin.skeleton() で取得できる場合）
        let root_node_index = skin.skeleton().map(|n| n.index());

        let skin_joints: Vec<SkinJoint> = joints.iter().enumerate().map(|(i, node)| {
            SkinJoint {
                node_index:           node.index(),
                name:                 node.name().unwrap_or("").to_string(),
                inverse_bind_matrix:  ibms[i],
            }
        }).collect();

        // root_node_index → joints 配列内でのインデックスに変換
        let root_joint = root_node_index.and_then(|rni| {
            skin_joints.iter().position(|j| j.node_index == rni)
        });

        Skin {
            name: skin.name().unwrap_or("").to_string(),
            joints: skin_joints,
            root_joint,
        }
    }).collect()
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod meshlet_tests {
    use super::*;

    /// 平面グリッド（N×N クアッド）を生成してメッシュレット分割の健全性を検証する。
    /// - 全三角形が過不足なくいずれかのメッシュレットに割り当てられる
    /// - 各メッシュレットのオフセット/カウントが連結配列の範囲内
    /// - 三角形が参照するローカル頂点番号が vertex_count 未満
    /// - 境界球中心・半径が有限で、全構成頂点を（マージン内で）内包する
    /// - 法線コーン軸が概ね単位ベクトル（全法線 +Y なので軸も +Y 付近）
    #[test]
    fn meshlets_cover_all_triangles_and_bounds_are_sane() {
        // 32×32 頂点のグリッド（= 31×31×2 三角形 ≒ 1922 枚）。1 メッシュレット 124 枚上限を
        // 超えるため複数メッシュレットに分割される。
        const N: usize = 32;
        let mut vertices = Vec::new();
        for z in 0..N {
            for x in 0..N {
                vertices.push(Vertex {
                    position: [x as f32, 0.0, z as f32],
                    normal:   [0.0, 1.0, 0.0],
                    ..Default::default()
                });
            }
        }
        let mut indices: Vec<u32> = Vec::new();
        for z in 0..N - 1 {
            for x in 0..N - 1 {
                let i = (z * N + x) as u32;
                let r = i + N as u32;
                indices.extend_from_slice(&[i, r, i + 1, i + 1, r, r + 1]);
            }
        }
        let tri_count = indices.len() / 3;

        let (descs, mv, mt) = build_meshlets_for_primitive(&indices, &vertices, false);
        assert!(!descs.is_empty(), "メッシュレットが生成されること");
        assert!(descs.len() >= 2, "上限超えで複数メッシュレットに分割されること");

        // 三角形総数がメッシュレット三角形数の合計と一致する。
        let sum_tris: u32 = descs.iter().map(|d| d.triangle_count).sum();
        assert_eq!(sum_tris as usize, tri_count, "全三角形が割り当てられる");

        for d in &descs {
            assert!(d.vertex_count as usize <= MESHLET_MAX_VERTS);
            assert!(d.triangle_count as usize <= MESHLET_MAX_TRIS);
            // オフセット + カウントが連結配列の範囲内。
            assert!((d.vertex_offset + d.vertex_count) as usize <= mv.len());
            assert!((d.triangle_offset + d.triangle_count * 3) as usize <= mt.len());

            // 三角形コーナーの参照するローカル頂点番号は vertex_count 未満。
            for c in 0..(d.triangle_count * 3) as usize {
                let local = mt[d.triangle_offset as usize + c] as u32;
                assert!(local < d.vertex_count, "ローカル頂点番号が範囲内");
            }

            // 境界: 有限・正の半径。
            assert!(d.radius.is_finite() && d.radius >= 0.0);
            assert!(d.center.iter().all(|v| v.is_finite()));

            // 境界球が全構成頂点を（数値マージン込みで）内包する。
            for lv in 0..d.vertex_count as usize {
                let orig = mv[d.vertex_offset as usize + lv] as usize;
                let p = vertices[orig].position;
                let dx = p[0] - d.center[0];
                let dy = p[1] - d.center[1];
                let dz = p[2] - d.center[2];
                let dist = (dx * dx + dy * dy + dz * dz).sqrt();
                assert!(dist <= d.radius + 1e-2, "頂点が境界球内: dist={dist} r={}", d.radius);
            }

            // 法線コーン軸は概ね単位ベクトル。
            let al = (d.cone_axis[0].powi(2) + d.cone_axis[1].powi(2) + d.cone_axis[2].powi(2)).sqrt();
            assert!((al - 1.0).abs() < 1e-2 || al == 0.0, "コーン軸が単位ベクトル: len={al}");
        }
    }

    /// スキンメッシュ・三角形なしは空を返す（従来経路へフォールバック）。
    #[test]
    fn skinned_and_degenerate_yield_no_meshlets() {
        let verts = vec![Vertex::default(); 3];
        let idx = vec![0u32, 1, 2];
        assert!(build_meshlets_for_primitive(&idx, &verts, true).0.is_empty(), "スキンは空");
        assert!(build_meshlets_for_primitive(&[], &verts, false).0.is_empty(), "三角形なしは空");
    }
}


