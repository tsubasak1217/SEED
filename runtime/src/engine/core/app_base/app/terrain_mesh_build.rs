// ============================================================
//  terrain_mesh_build.rs — TerrainMesh → エンジン Model 変換
//
//  【責務】
//    地形ライブラリのエンジン非依存メッシュ（TerrainMesh: 位置＋法線＋インデックス）を、
//    エンジンのレンダラが扱う単一ノード・単一プリミティブの Model へ変換する純粋関数。
//    App / GPU / ECS への依存を持たない（単一責任・テスト容易）。
//
//    頂点は TerrainMesh のチャンクローカル座標（原点＝チャンク最小コーナー）をそのまま
//    使う。接線・UV・頂点カラーは地形では未使用のため既定値で埋める（法線マップ・
//    テクスチャは持たないマテリアルで描画する）。
// ============================================================

use crate::engine::core::loader::model::{
    CullFace, Material, Mesh, Model, ModelNode, Primitive, Vertex,
};
use crate::engine::terrain::marching_cubes::TerrainMesh;

/// 接線の既定値（xyz=+X 軸, w=+1 ハンドネス）。地形は法線マップを持たないためダミー。
const DEFAULT_TANGENT: [f32; 4] = [1.0, 0.0, 0.0, 1.0];
/// UV の既定値（テクスチャ未使用）。
const DEFAULT_UV: [f32; 2] = [0.0, 0.0];
/// 頂点カラーの既定値（白・不透明）。
const DEFAULT_COLOR: [f32; 4] = [1.0, 1.0, 1.0, 1.0];

/// TerrainMesh を単一ノード・単一プリミティブの Model へ変換する。
///
/// - `name`: モデル名（デバッグ表示用。source_path とは別）。
/// - 空メッシュ（三角形なし）でも有効な Model を返す（描画は何も出ないだけ）。
pub fn terrain_mesh_to_model(mesh: &TerrainMesh, name: &str) -> Model {
    // ─── TerrainMesh の位置＋法線を Vertex 配列へ詰め替える ───
    //   positions と normals は同じ長さ（マーチングキューブスが 1:1 で生成）。
    let mut vertices: Vec<Vertex> = Vec::with_capacity(mesh.positions.len());
    for (i, pos) in mesh.positions.iter().enumerate() {
        // 法線は位置と対になっている（境界外でも normals[i] が必ず存在する前提）。
        let normal = mesh.normals.get(i).copied().unwrap_or([0.0, 1.0, 0.0]);
        vertices.push(Vertex {
            position: *pos,
            normal,
            tangent: DEFAULT_TANGENT,
            uv0:     DEFAULT_UV,
            uv1:     DEFAULT_UV,
            color:   DEFAULT_COLOR,
        });
    }

    // ─── 1 プリミティブ（1 マテリアル）を構築する ───
    //   skin_vertices は必ず空（地形はスキニング非対応）。LOD・メッシュレットも未生成。
    let primitive = Primitive {
        vertices,
        skin_vertices:     Vec::new(),
        indices:           mesh.indices.clone(),
        material_index:    Some(0),
        lod_indices:       Vec::new(),
        meshlets:          Vec::new(),
        meshlet_vertices:  Vec::new(),
        meshlet_triangles: Vec::new(),
    };

    let engine_mesh = Mesh {
        name:       name.to_string(),
        primitives: vec![primitive],
    };

    // ─── 単一ノード（恒等ローカル変換・mesh_index=0）を構築する ───
    let node = ModelNode {
        name:         name.to_string(),
        local_matrix: ModelNode::identity_matrix(),
        translation:  [0.0, 0.0, 0.0],
        rotation:     [0.0, 0.0, 0.0, 1.0],
        scale:        [1.0, 1.0, 1.0],
        mesh_index:   Some(0),
        skin_index:   None,
        children:     Vec::new(),
        parent:       None,
    };

    // ─── 地形マテリアル（既定 PBR＋両面描画）───
    //   マーチングキューブスの三角ワインディングは（左手系エンジンの Ccw フロントフェイス
    //   規約と一致しないため）片面カリングだと地表が裏面判定で消える。T1 では両面描画
    //   （cull_face=None / double_sided）にして確実に見えるようにする。ライティングは
    //   面ワインディングではなく頂点法線（密度勾配＝外向き、テスト検証済み）で行われるため
    //   両面でも陰影は正しい。T2 で片面ワインディングへ正す余地あり。
    let material = Material {
        double_sided: true,
        cull_face: CullFace::None,
        ..Material::default()
    };

    // ─── 最小構成の Model（テクスチャ・アニメ・スキンなし・地形マテリアル 1 枚）───
    Model {
        name:       name.to_string(),
        nodes:      vec![node],
        root_nodes: vec![0],
        meshes:     vec![engine_mesh],
        materials:  vec![material],
        textures:   Vec::new(),
        animations: Vec::new(),
        skins:      Vec::new(),
    }
}
