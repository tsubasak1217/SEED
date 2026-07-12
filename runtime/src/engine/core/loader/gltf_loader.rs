use std::path::Path;
use gltf::animation::util::ReadOutputs;
use super::model::*;
use super::LoadError;

// ============================================================
//  エントリポイント
// ============================================================

pub fn load(path: &Path) -> Result<Model, LoadError> {
    let path_str = path.to_str().unwrap_or("");

    // 【画像の遅延デコード】
    // `gltf::import` は全画像を一括で RGBA 展開するため、Sponza 級のモデルでは
    // 数 GB のピークメモリが発生する（エディタ同居環境ではスワップの原因になる）。
    // ここでは document とジオメトリバッファのみをロードし、画像は
    // 「エンコード済みバイト（GLB 埋め込み/data URI）」または「外部ファイルパス」の
    // まま保持する。実デコードは asset_cache::process_model_textures が
    // 1 枚ずつストリーミング（デコード → ミップ → BC 圧縮 → 即 drop）で行う。
    //
    // assets:// 仮想パスの場合は PAK から読み込む（GLB 推奨: 自己完結形式）。
    // 通常の絶対パスはファイルシステムから直接読む。
    let (document, buffers, base_dir, virtual_base) =
        if path_str.starts_with(crate::engine::asset_fs::ASSETS_SCHEME) {
            let bytes = crate::engine::asset_fs::read_bytes(path_str)
                .map_err(|e| LoadError::Io(format!("PAK read failed for {path_str}: {e}")))?;
            let g = gltf::Gltf::from_slice(&bytes)
                .map_err(|e| LoadError::Parse(e.to_string()))?;
            let buffers = gltf::import_buffers(&g.document, None, g.blob)
                .map_err(|e| LoadError::Parse(e.to_string()))?;
            // 仮想ベースディレクトリ: "assets://dir/file.gltf" → "assets://dir"
            let vbase = path_str.rsplit_once('/').map(|(b, _)| b.to_string());
            (g.document, buffers, None, vbase)
        } else {
            let g = gltf::Gltf::open(path)
                .map_err(|e| LoadError::Parse(e.to_string()))?;
            let base = path.parent().map(|p| p.to_path_buf());
            let buffers = gltf::import_buffers(&g.document, base.as_deref(), g.blob)
                .map_err(|e| LoadError::Parse(e.to_string()))?;
            (g.document, buffers, base, None)
        };

    let name = path.file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("unnamed")
        .to_string();

    let textures   = load_textures(&document, &buffers, base_dir.as_deref(), virtual_base.as_deref());
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
    document:     &gltf::Document,
    buffers:      &[gltf::buffer::Data],
    base_dir:     Option<&Path>,
    virtual_base: Option<&str>,
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
        // 画像はデコードせず、供給元（バッファビュー/外部ファイル/data URI）を
        // そのまま TextureSource に写し取る（遅延デコード）。
        let source  = image_texture_source(&tex.source().source(), buffers, base_dir, virtual_base);
        let sampler = tex.sampler();

        // 用途に応じてフォーマットを切り替える
        // linear=true  → Rgba8Unorm   （法線・MR・AO：線形データをそのまま使用）
        // linear=false → Rgba8UnormSrgb（ベースカラー・エミッシブ：GPU が sRGB デコード）
        let linear = linear_indices.contains(&tex.index());

        TextureData {
            name:   tex.name().map(String::from),
            source,
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

/// glTF 画像の供給元を（デコードせずに）`TextureSource` へ変換する。
///
/// - バッファビュー（GLB 埋め込み等）→ エンコード済みバイトをコピー（PNG/JPG のまま。小さい）
/// - data URI → base64/パーセントデコードしてエンコード済みバイトに
/// - 外部 URI → ファイルパスとして保持（実読み込みはテクスチャ処理時）
///
/// RGBA 展開はここでは一切行わないため、パース段階のピークメモリは
/// 「圧縮画像バイトの合計」（RGBA 展開の 1/10 前後）に抑えられる。
fn image_texture_source(
    source:       &gltf::image::Source<'_>,
    buffers:      &[gltf::buffer::Data],
    base_dir:     Option<&Path>,
    virtual_base: Option<&str>,
) -> TextureSource {
    match source {
        // GLB 埋め込み: バッファビューの範囲をコピー（エンコード済みのまま）
        gltf::image::Source::View { view, .. } => {
            let start = view.offset();
            let end   = start + view.length();
            let bytes = buffers
                .get(view.buffer().index())
                .and_then(|b| b.get(start..end))
                .map(|s| s.to_vec())
                .unwrap_or_default();
            TextureSource::EncodedBytes { bytes }
        }
        gltf::image::Source::Uri { uri, .. } => {
            // data URI（インライン画像）
            if let Some(bytes) = decode_data_uri(uri) {
                return TextureSource::EncodedBytes { bytes };
            }
            // 外部ファイル: パーセントエンコーディング（%20 等）を解除して解決
            let decoded = urlencoding::decode(uri)
                .map(|c| c.into_owned())
                .unwrap_or_else(|_| uri.to_string());
            if let Some(base) = base_dir {
                TextureSource::FilePath(base.join(&decoded))
            } else if let Some(vbase) = virtual_base {
                // PAK モード: 仮想パス "assets://dir/tex.png" として保持
                //（asset_fs::read_bytes が PAK/FS の双方を解決する）
                TextureSource::FilePath(std::path::PathBuf::from(format!("{vbase}/{decoded}")))
            } else {
                TextureSource::FilePath(std::path::PathBuf::from(decoded))
            }
        }
    }
}

/// data URI（`data:<mime>[;base64],<payload>`）をバイト列にデコードする。
/// data URI でない・デコード失敗の場合は None。
fn decode_data_uri(uri: &str) -> Option<Vec<u8>> {
    let rest = uri.strip_prefix("data:")?;
    let (head, payload) = rest.split_once(',')?;
    if head.ends_with(";base64") {
        use base64::Engine as _;
        base64::engine::general_purpose::STANDARD.decode(payload).ok()
    } else {
        // 非 base64 data URI（パーセントエンコードされた生バイト）
        Some(urlencoding::decode_binary(payload.as_bytes()).into_owned())
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
            // glTF の double_sided をカリング面へマップする（true → 両面描画＝カリング無し）。
            // これで Sponza のカーテン等、片面しか描かれず裏から見ると消えていたマテリアルが
            // 正しく両面描画される。
            cull_face:        crate::engine::core::loader::model::cull_face_from_double_sided(mat.double_sided()),
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

/// 法線コーン軸に掛ける符号補正係数（-1 = 反転）。
///
/// 【なぜ反転が必要か】
/// meshopt の `compute_meshlet_bounds` は、三角形の **代数的外積**
/// `cross(b - a, c - a)`（右手系規約）を面法線としてコーン軸を求める。
///
/// 一方、本エンジンは左手座標系（`Mat4x4::look_at_lh` / `perspective_lh`）で、
/// ラスタライザは `front_face = Ccw` / `cull_mode = Back`（`pipelines/mesh.toml`）。
/// glTF / OBJ ローダは右手系データを左手系へ変換する際、
/// **位置・法線の Z 成分だけを反転し、インデックスの巻き順は据え置く**
/// （スクリーン空間の CCW は `perspective_lh` により保存されるため巻き順反転は不要）。
///
/// Z 反転 `M = diag(1, 1, -1)` は行列式が -1 の鏡映変換であり、外積は
/// `cross(M·u, M·v) = det(M) · M·cross(u, v) = -M·cross(u, v)`
/// と符号が反転する。つまり **変換後の頂点データにおける代数的外積は、
/// 変換後の（正しい外向き）頂点法線と必ず逆向き**になる。
/// 法線自体は M で変換されるだけなので正しい向きを保つ（シェーディングは正常）が、
/// 外積由来のコーン軸だけが裏返る、というズレが生じる。
///
/// この状態で `meshlet_cull.wgsl` の背面コーン棄却
/// （`dot(center_w - camera_pos, axis_w) >= cutoff·dist + radius_w + margin`）を走らせると、
/// 「カメラの方を向いている（＝見えている）メッシュレット」が背面と誤判定されて棄却される。
/// 距離が遠いほど `radius/dist` 項が小さく成立しやすいため、
/// 「遠景がまだらに消え、近づくと直る」という症状になる。
///
/// これは座標変換規約に由来する **常に成立する** 関係なので、条件判定ではなく
/// 無条件反転で補正する（回帰テスト `meshlet_cone_axis_agrees_with_authored_normals` /
/// `loader_rh_to_lh_flip_inverts_algebraic_face_normal` で担保）。
const MESHLET_CONE_AXIS_SIGN: f32 = -1.0;

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

        // 【コーン軸の符号補正】
        // meshopt のコーン軸は代数的外積（右手系規約）由来のため、
        // RH→LH 変換（Z 反転＝鏡映）済みの頂点データでは実際の外向き法線と逆を向く。
        // エンジンの前面規約（左手系 + FrontFace::Ccw + Back カリング）に合わせて反転する。
        // 詳細は MESHLET_CONE_AXIS_SIGN のコメントを参照。
        let cone_axis = [
            bounds.cone_axis[0] * MESHLET_CONE_AXIS_SIGN,
            bounds.cone_axis[1] * MESHLET_CONE_AXIS_SIGN,
            bounds.cone_axis[2] * MESHLET_CONE_AXIS_SIGN,
        ];

        descs.push(MeshletDesc {
            vertex_offset:   raw.vertex_offset,
            triangle_offset: raw.triangle_offset,
            vertex_count:    raw.vertex_count,
            triangle_count:  raw.triangle_count,
            center:      bounds.center,
            radius:      bounds.radius,
            cone_axis,
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

    // --------------------------------------------------------
    //  コーン軸の符号（GPU メッシュレットカリングの誤棄却対策）
    // --------------------------------------------------------

    /// テスト用に「glTF 規約（右手系・表面 CCW・法線は外向き）」の UV 球を組み、
    /// 最小構成の .gltf（バッファは data URI 埋め込み）としてテンポラリに書き出す。
    ///
    /// リポジトリ内の実モデル（A.gltf = 40 三角形・1 メッシュレット、BrainStem = スキン）は
    /// メッシュレットが 1 個以下 or 対象外でコーンカリングの検証に使えないため、
    /// 検証用ジオメトリをここで生成する。**実際のローダ経路（`super::load`）を通す**ので、
    /// RH→LH 変換を含めた本番と同じ処理が検証対象になる。
    ///
    /// 戻り値: 書き出した .gltf のパス。
    fn write_test_sphere_gltf(stacks: usize, sectors: usize) -> std::path::PathBuf {
        use base64::Engine as _;

        // ── 頂点（glTF 空間 = 右手系）─────────────────────────
        // φ: +Y から下向き（0..π）、θ: XZ 平面の方位角（0..2π）
        // p = (sinφcosθ, cosφ, sinφsinθ)、外向き法線 = p（単位球なので位置と一致）
        let mut pos: Vec<[f32; 3]> = Vec::new();
        for i in 0..=stacks {
            let phi = std::f32::consts::PI * (i as f32 / stacks as f32);
            for j in 0..=sectors {
                let th = std::f32::consts::TAU * (j as f32 / sectors as f32);
                pos.push([phi.sin() * th.cos(), phi.cos(), phi.sin() * th.sin()]);
            }
        }
        let nrm = pos.clone(); // 単位球: 外向き法線 = 位置

        // ── インデックス（右手系で外側から見て CCW）───────────
        // 球面の接ベクトルを u=∂p/∂φ, v=∂p/∂θ とすると cross(v, u) = +sinφ·p（＝外向き）。
        // よって a=(i,j) → b=(i,j+1)（+θ 方向）→ c=(i+1,j)（+φ 方向）の順で
        // cross(b-a, c-a) が外向き法線と同符号になり、glTF の表面巻き順（CCW）を満たす。
        let vid = |i: usize, j: usize| (i * (sectors + 1) + j) as u32;
        let mut idx: Vec<u32> = Vec::new();
        for i in 0..stacks {
            for j in 0..sectors {
                idx.extend_from_slice(&[vid(i, j),     vid(i, j + 1), vid(i + 1, j)]);
                idx.extend_from_slice(&[vid(i, j + 1), vid(i + 1, j + 1), vid(i + 1, j)]);
            }
        }

        // ── バイナリバッファ（POSITION | NORMAL | INDICES）────
        let mut buf: Vec<u8> = Vec::new();
        for p in &pos { for c in p { buf.extend_from_slice(&c.to_le_bytes()); } }
        let n_off = buf.len();
        for p in &nrm { for c in p { buf.extend_from_slice(&c.to_le_bytes()); } }
        let i_off = buf.len();
        for v in &idx { buf.extend_from_slice(&v.to_le_bytes()); }

        let pos_len = n_off;
        let nrm_len = i_off - n_off;
        let idx_len = buf.len() - i_off;
        let b64 = base64::engine::general_purpose::STANDARD.encode(&buf);

        // glTF 定数: 5126 = FLOAT, 5125 = UNSIGNED_INT
        const COMPONENT_TYPE_FLOAT: u32 = 5126;
        const COMPONENT_TYPE_U32:   u32 = 5125;

        let json = format!(r#"{{
  "asset": {{ "version": "2.0" }},
  "scene": 0,
  "scenes": [ {{ "nodes": [0] }} ],
  "nodes": [ {{ "mesh": 0 }} ],
  "meshes": [ {{ "primitives": [ {{ "attributes": {{ "POSITION": 0, "NORMAL": 1 }}, "indices": 2 }} ] }} ],
  "accessors": [
    {{ "bufferView": 0, "componentType": {ct_f}, "count": {nv}, "type": "VEC3", "min": [-1.0,-1.0,-1.0], "max": [1.0,1.0,1.0] }},
    {{ "bufferView": 1, "componentType": {ct_f}, "count": {nv}, "type": "VEC3" }},
    {{ "bufferView": 2, "componentType": {ct_u}, "count": {ni}, "type": "SCALAR" }}
  ],
  "bufferViews": [
    {{ "buffer": 0, "byteOffset": 0,        "byteLength": {pl} }},
    {{ "buffer": 0, "byteOffset": {no},     "byteLength": {nl} }},
    {{ "buffer": 0, "byteOffset": {io},     "byteLength": {il} }}
  ],
  "buffers": [ {{ "byteLength": {tl}, "uri": "data:application/octet-stream;base64,{b64}" }} ]
}}"#,
            ct_f = COMPONENT_TYPE_FLOAT, ct_u = COMPONENT_TYPE_U32,
            nv = pos.len(), ni = idx.len(),
            pl = pos_len, no = n_off, nl = nrm_len, io = i_off, il = idx_len,
            tl = buf.len(), b64 = b64,
        );

        // 複数テストが並行実行されるためファイル名は分割数で一意化する。
        let path = std::env::temp_dir()
            .join(format!("seed_meshlet_cone_test_sphere_{stacks}x{sectors}.gltf"));
        std::fs::write(&path, json).expect("テスト用 .gltf の書き出しに成功すること");
        path
    }

    /// メッシュレットのコーン軸が「オーサリング済み頂点法線（＝実際に見える面の外向き法線）」
    /// と同符号であることを保証する回帰テスト。
    ///
    /// 背景（このテストが守る不変条件）:
    /// 本エンジンは左手座標系のため、ローダは glTF（右手系）の位置・法線の Z を反転する。
    /// これは行列式が負の鏡映変換なので、変換後データの代数的外積 cross(b-a, c-a) は
    /// 変換後の頂点法線と **逆向き** になる（巻き順は反転していないため）。
    /// meshopt の `compute_meshlet_bounds` はこの代数的外積からコーン軸を作るので、
    /// 補正しないとコーン軸が外向き法線の逆を向き、GPU カリングが
    /// **表を向いたメッシュレットを背面と誤判定して棄却**する（＝見えているのに消える）。
    /// → `build_meshlets_for_primitive` の符号補正が効いていることをここで担保する。
    #[test]
    fn meshlet_cone_axis_agrees_with_authored_normals() {
        // 40×40 の UV 球（3200 三角形）→ 複数メッシュレットに分割され、
        // 各メッシュレットの法線が十分揃うので有効な（cutoff < 1）コーンが生成される。
        const STACKS:  usize = 40;
        const SECTORS: usize = 40;
        let path  = write_test_sphere_gltf(STACKS, SECTORS);
        let model = super::load(&path).expect("テスト用 .gltf のロードに成功すること");

        let mut total     = 0usize; // 判定対象メッシュレット数
        let mut positive  = 0usize; // dot(cone_axis, 平均法線) > 0 の個数
        let mut valid_cone= 0usize; // cone_cutoff < 1（＝実際にコーン棄却が発火しうる）個数
        let mut dot_sum   = 0.0f64;
        let mut dot_min   = f32::INFINITY;
        let mut dot_max   = f32::NEG_INFINITY;

        for mesh in &model.meshes {
            for prim in &mesh.primitives {
                for d in &prim.meshlets {
                    // メッシュレット構成頂点の authored 法線を平均して代表法線 B を作る。
                    let mut acc = [0.0f32; 3];
                    for lv in 0..d.vertex_count as usize {
                        let orig = prim.meshlet_vertices[d.vertex_offset as usize + lv] as usize;
                        let nv   = prim.vertices[orig].normal;
                        acc[0] += nv[0]; acc[1] += nv[1]; acc[2] += nv[2];
                    }
                    let len = (acc[0] * acc[0] + acc[1] * acc[1] + acc[2] * acc[2]).sqrt();
                    if len < 1e-3 { continue; } // 法線が打ち消し合う場合は判定不能なので除外

                    let b = [acc[0] / len, acc[1] / len, acc[2] / len];
                    let a = d.cone_axis;
                    // 軸が退化（0 ベクトル＝コーン無効）なら判定対象外。
                    if a[0] == 0.0 && a[1] == 0.0 && a[2] == 0.0 { continue; }

                    let dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
                    total   += 1;
                    dot_sum += dot as f64;
                    if dot > 0.0 { positive += 1; }
                    if d.cone_cutoff < 1.0 { valid_cone += 1; }
                    dot_min = dot_min.min(dot);
                    dot_max = dot_max.max(dot);
                }
            }
        }

        assert!(total > 0, "有効なコーン軸を持つメッシュレットが 1 つ以上あること");
        let mean      = dot_sum / total as f64;
        let pos_ratio = positive as f64 / total as f64;
        eprintln!(
            "[cone_axis stats] sphere{STACKS}x{SECTORS} meshlets={total} valid_cone={valid_cone} \
             mean_dot={mean:.4} positive={positive} ({:.1}%) min={dot_min:.4} max={dot_max:.4}",
            pos_ratio * 100.0
        );

        // 符号補正が効いていれば全メッシュレットで内積が正になる（球なので明確に正）。
        assert_eq!(positive, total, "全メッシュレットでコーン軸が頂点法線と同符号であること");
        assert!(mean > 0.5, "平均内積が明確に正であること: mean_dot={mean}");
        assert!(dot_min > 0.0, "最小内積も正であること: min={dot_min}");
    }

    /// ローダの RH→LH 変換により、**変換後データの代数的外積は頂点法線と逆向きになる**
    /// ことを直接確認するテスト（上のコーン軸補正が必要な理由そのものを固定する）。
    ///
    /// 位置・法線の Z のみを反転する変換は行列式 -1 の鏡映であり、
    /// 巻き順（インデックス順）は据え置かれる。鏡映 M に対し
    /// `cross(Mu, Mv) = det(M) · M·cross(u, v) = -M·cross(u, v)` が成り立つため、
    /// 「変換後の外積」は「変換後の法線」の逆符号になる。
    #[test]
    fn loader_rh_to_lh_flip_inverts_algebraic_face_normal() {
        let path  = write_test_sphere_gltf(8, 8);
        let model = super::load(&path).expect("テスト用 .gltf のロードに成功すること");
        let prim  = &model.meshes[0].primitives[0];

        let mut checked = 0usize;
        for tri in prim.indices.chunks_exact(3) {
            let (a, b, c) = (
                prim.vertices[tri[0] as usize].position,
                prim.vertices[tri[1] as usize].position,
                prim.vertices[tri[2] as usize].position,
            );
            let e1 = [b[0] - a[0], b[1] - a[1], b[2] - a[2]];
            let e2 = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
            // 代数的外積（右手系規約。meshopt が面法線として使うもの）。
            let cr = [
                e1[1] * e2[2] - e1[2] * e2[1],
                e1[2] * e2[0] - e1[0] * e2[2],
                e1[0] * e2[1] - e1[1] * e2[0],
            ];
            let cl = (cr[0] * cr[0] + cr[1] * cr[1] + cr[2] * cr[2]).sqrt();
            if cl < 1e-6 { continue; } // 極付近の縮退三角形はスキップ

            // 頂点法線（変換後 = エンジン空間での正しい外向き法線）。
            let n = prim.vertices[tri[0] as usize].normal;
            let dot = (cr[0] * n[0] + cr[1] * n[1] + cr[2] * n[2]) / cl;
            assert!(dot < 0.0, "変換後の代数的外積は頂点法線と逆向きになる: dot={dot}");
            checked += 1;
        }
        assert!(checked > 0, "縮退でない三角形が存在すること");
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



// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::decode_data_uri;

    /// data URI のデコード: base64 / パーセントエンコード / 非 data URI。
    #[test]
    fn data_uri_decode_variants() {
        // base64 形式（"ABC" の base64 は "QUJD"）
        assert_eq!(
            decode_data_uri("data:image/png;base64,QUJD"),
            Some(b"ABC".to_vec()),
        );
        // 非 base64（パーセントエンコードされた生データ）
        assert_eq!(
            decode_data_uri("data:text/plain,A%20B"),
            Some(b"A B".to_vec()),
        );
        // data URI でない通常の URI は None（外部ファイル扱い）
        assert_eq!(decode_data_uri("textures/wall.png"), None);
    }
}
