use std::path::Path;
use super::model::*;
use super::LoadError;

pub fn load(path: &Path) -> Result<Model, LoadError> {
    let (obj_models, obj_materials_result) = tobj::load_obj(
        path,
        &tobj::LoadOptions {
            triangulate:  true,
            single_index: true,
            ..Default::default()
        },
    ).map_err(|e| LoadError::Parse(e.to_string()))?;

    // マテリアルが読めなくてもモデルは返す
    let obj_materials = obj_materials_result.unwrap_or_default();

    let name = path.file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("unnamed")
        .to_string();

    // ── テクスチャ ─────────────────────────────────────────────
    // OBJ は外部ファイル参照のみ。モデルファイルと同じディレクトリを基準とする。
    // (tex_name, linear) のタプルで用途を保持する:
    //   diffuse  → linear: false（sRGB カラーテクスチャ）
    //   normal   → linear: true （線形データテクスチャ）
    //   specular → linear: true （線形データテクスチャ）
    let base_dir = path.parent().unwrap_or(Path::new("."));
    let textures: Vec<TextureData> = obj_materials.iter()
        .flat_map(|mat| {
            [
                (mat.diffuse_texture.as_deref(),  false),   // sRGB
                (mat.normal_texture.as_deref(),   true),    // linear
                (mat.specular_texture.as_deref(), true),    // linear
            ]
        })
        .filter(|(name, _)| name.map_or(false, |s| !s.is_empty()))
        .filter_map(|(name, linear)| Some((name?, linear)))
        .map(|(tex_name, linear)| TextureData {
            name:    Some(tex_name.to_string()),
            source:  TextureSource::FilePath(base_dir.join(tex_name)),
            sampler: SamplerData::default(),
            linear,
        })
        .collect();

    // ── マテリアル（Phong → PBR 近似マッピング） ─────────────
    // OBJ は Phong モデル。diffuse → base_color、specular は roughness に近似。
    let materials: Vec<Material> = if obj_materials.is_empty() {
        vec![Material::default()]
    } else {
        obj_materials.iter().map(|mat| {
            // Kd（拡散反射色）→ PBR ベースカラー。alpha は後で dissolve から設定する。
            let base_color_factor = [
                mat.diffuse.map_or(1.0, |d| d[0]),
                mat.diffuse.map_or(1.0, |d| d[1]),
                mat.diffuse.map_or(1.0, |d| d[2]),
                1.0,  // alpha は後の dissolve 計算で上書きする
            ];
            // specular の輝度 → rough ness の逆数として近似
            let spec_lum = mat.specular.map_or(0.0, |s| {
                (s[0] * 0.2126 + s[1] * 0.7152 + s[2] * 0.0722).min(1.0)
            });
            let roughness_factor = 1.0 - spec_lum;

            // テクスチャインデックスの逆引き（textures vec の何番目か）
            let find_tex = |name: Option<&str>| -> Option<TextureInfo> {
                let n = name?;
                if n.is_empty() { return None; }
                let idx = textures.iter().position(|t| {
                    t.name.as_deref() == Some(n)
                })?;
                Some(TextureInfo { texture_index: idx, tex_coord_set: 0 })
            };

            // OBJ の dissolve: 1.0 = 完全不透明、0.0 = 完全透明
            // alpha = dissolve をそのまま使用する（1 - dissolve は Tr 値と混同するバグ）
            let alpha    = mat.dissolve.unwrap_or(1.0);
            let alpha_mode = if alpha < 1.0 {
                AlphaMode::Blend
            } else {
                AlphaMode::Opaque
            };

            Material {
                name:                       mat.name.clone(),
                base_color_factor:          [
                    base_color_factor[0],
                    base_color_factor[1],
                    base_color_factor[2],
                    alpha,
                ],
                base_color_texture:         find_tex(mat.diffuse_texture.as_deref()),
                metallic_factor:            0.0, // OBJ は非 PBR なので金属度 0 でフォールバック
                roughness_factor,
                metallic_roughness_texture: None,
                normal_texture: find_tex(mat.normal_texture.as_deref()).map(|ti| {
                    NormalTextureInfo {
                        texture_index: ti.texture_index,
                        tex_coord_set: 0,
                        scale: 1.0,
                    }
                }),
                occlusion_texture:  None,
                // OBJ の Ka（アンビエント係数）は PBR エミッシブとは別物。
                // Ka を emissive にマッピングすると Ka=1.0 の白モデルが全面発光してしまう。
                // OBJ/MTL に Ke（エミッシブ）拡張がある場合のみ使用する（tobj 未対応のため 0 固定）。
                emissive_factor:    [0.0; 3],
                emissive_texture:   None,
                alpha_mode,
                alpha_cutoff:       0.5,
                // OBJ/MTL には屈折率（IOR）拡張が無いため既定 1.0（屈折なし）。
                ior:                1.0,
                // OBJ/MTL には透過率（transmission）拡張が無いため既定 0.0（透過なし＝従来動作）。
                transmission:       0.0,
                // OBJ/MTL には拡散透過拡張が無いため既定 0.0（拡散透過なし＝従来動作）。
                diffuse_transmission: 0.0,
                // OBJ/MTL に MR テクスチャは無いためトグルは常に false（従来動作＝乗算）。
                mr_tex_ignore:      false,
                // 頂点カラー無視トグル。OBJ ロード時は常に false（従来どおり頂点カラーを乗算）。
                ignore_vertex_color: false,
                // 情報系（OBJ/MTL は対応する記述を持たないため既定値）。
                user_data:           0.0,
                shading_model:       crate::engine::core::renderer::surface_id::SHADING_MODEL_DEFAULT_PBR,
                // OBJ/MTL には両面フラグが無いため常に背面カリング（従来挙動）。
                double_sided:       false,
                cull_face:          crate::engine::core::loader::model::CullFace::Back,
                // 平均アルベド（Phase RT-GI）は既定（白）。ロード後 compute_material_avg_albedo が焼き直す。
                avg_albedo:         [1.0, 1.0, 1.0, 1.0],
                // テクスチャ平均（factor 抜き）も既定（白）。同じく compute_material_avg_albedo が焼き直す。
                base_color_tex_avg: [1.0, 1.0, 1.0],
                // 地形レイヤブレンド（Terrain T2）。glTF/OBJ 由来のマテリアルは常に false
                // （true を立てるのは地形メッシュを組む terrain_mesh_build.rs だけ）。
                terrain_layers: false,
                // 地形パレットは地形以外では未使用。恒等パレットで埋めておく。
                terrain_palette: Material::default().terrain_palette,
            }
        }).collect()
    };

    // ── メッシュ（tobj::Model 1 つ = 1 Primitive）────────────
    let mut meshes: Vec<Mesh>     = Vec::new();
    let mut nodes:  Vec<ModelNode> = Vec::new();
    let mut root_nodes: Vec<usize> = Vec::new();

    for (node_idx, obj_model) in obj_models.iter().enumerate() {
        let mesh_idx = meshes.len();
        let prim     = build_primitive(&obj_model.mesh);

        let mat_idx  = if obj_materials.is_empty() {
            None
        } else {
            obj_model.mesh.material_id
        };

        meshes.push(Mesh {
            name: obj_model.name.clone(),
            primitives: vec![Primitive {
                material_index: mat_idx,
                ..prim
            }],
        });

        nodes.push(ModelNode {
            name:         obj_model.name.clone(),
            local_matrix: ModelNode::identity_matrix(),
            translation:  [0.0, 0.0, 0.0],
            rotation:     [0.0, 0.0, 0.0, 1.0],
            scale:        [1.0, 1.0, 1.0],
            mesh_index:   Some(mesh_idx),
            skin_index:   None,
            children:     Vec::new(),
            parent:       None,
        });
        root_nodes.push(node_idx);
    }

    Ok(Model {
        name,
        nodes,
        root_nodes,
        meshes,
        materials,
        textures,
        animations: Vec::new(),
        skins:      Vec::new(),
    })
}

/// `tobj::Mesh` → `Primitive`（スキニングなし）
fn build_primitive(mesh: &tobj::Mesh) -> Primitive {
    let n = mesh.positions.len() / 3;

    let vertices: Vec<Vertex> = (0..n).map(|i| {
        let p = |off: usize| mesh.positions.get(i * 3 + off).copied().unwrap_or(0.0);
        let nm = |off: usize| mesh.normals.get(i * 3 + off).copied().unwrap_or(0.0);
        let uv = |off: usize| mesh.texcoords.get(i * 2 + off).copied().unwrap_or(0.0);

        // RH→LH: 位置・法線の Z 成分を反転してエンジン左手座標系に変換する。
        // perspective_lh を使う LH では Z 反転後もスクリーン空間 CCW が保存されるため
        // 巻き順スワップは不要。
        Vertex {
            position: [p(0), p(1), -p(2)],
            normal:   {
                let nx = nm(0); let ny = nm(1); let nz = nm(2);
                let len = (nx*nx + ny*ny + nz*nz).sqrt();
                // Z 反転してエンジン LH 系に合わせる
                if len > 1e-6 { [nx/len, ny/len, -nz/len] } else { [0.0, 1.0, 0.0] }
            },
            // tobj は tangent 非対応。デフォルト接線 +X。
            // Z 反転で座標系の手性が変わるため w（ビタンジェント符号）を反転する。
            tangent: [1.0, 0.0, 0.0, -1.0],
            uv0:     [uv(0), 1.0 - uv(1)],  // OBJ は V 軸が反転
            uv1:     [0.0; 2],
            color:   [1.0; 4],
        }
    }).collect();

    // LOD0 メッシュレット分割（GPU カリング第1弾）。OBJ は常に非スキン。
    // gltf 同様に所要時間を計測して初回ロード内訳へ出力する。
    let t_ml = std::time::Instant::now();
    let (meshlets, meshlet_vertices, meshlet_triangles) =
        super::gltf_loader::build_meshlets_for_primitive(&mesh.indices, &vertices, false);
    super::gen_timing::add_meshlet(t_ml.elapsed());

    Primitive {
        vertices,
        skin_vertices: Vec::new(),
        indices: mesh.indices.clone(),
        material_index: None,  // 呼び出し元で上書きする
        lod_indices:    Vec::new(),
        meshlets,
        meshlet_vertices,
        meshlet_triangles,
    }
}
