// ============================================================
//  terrain_mesh_build.rs — TerrainMesh → エンジン Model 変換
//
//  【責務】
//    地形ライブラリのエンジン非依存メッシュ（TerrainMesh: 位置＋法線＋スプラット＋インデックス）を、
//    エンジンのレンダラが扱う単一ノード・単一プリミティブの Model へ変換する純粋関数。
//    App / GPU / ECS への依存を持たない（単一責任・テスト容易）。
//
//    頂点は TerrainMesh のチャンクローカル座標（原点＝チャンク最小コーナー）をそのまま
//    使う。接線・UV は地形では未使用のため既定値で埋める（テクスチャ座標は
//    シェーダ側の triplanar がワールド座標から生成するため頂点 UV は不要）。
//
//  【レイヤ重み（スプラット）の運び方 — T2 の設計判断】
//    レイヤ重み 4 成分は **頂点カラー（Vertex.color の RGBA）** に載せて GPU へ渡す。
//    専用の頂点属性スロットを増やす案もあったが、Vertex/mesh_vertex レイアウトは
//    エンジン内の全パイプライン（forward / shadow / depth / id / outline / RT）が
//    共有しており、1 バイトでも増やすと全パイプラインへ波及する。頂点カラーは
//    地形メッシュでは未使用（常に白だった）ため、これを転用するのが最小の差分で
//    済み、かつ既存の頂点アップロード経路をそのまま使える。
//    → 1 頂点が同時にブレンドできるレイヤ数はここで 4（TERRAIN_BLEND_SLOTS）に固定される。
//
//  【チャンク単位パレット — T2b の設計判断】
//    レイヤ定義は TERRAIN_MAX_LAYERS 層まで増やせるが、頂点カラーは 4 成分しかない。
//    そこで「このチャンクが使う 4 レイヤ番号」＝パレットをチャンクごとに 1 つ決め、
//    頂点カラーはそのパレット内の重みだけを運ぶ。パレットは uniform で GPU へ渡す
//    （このファイルはパレットを戻り値として返すだけで、結線は呼び出し側の責務）。
//
//    パレットは 2 パスで決める:
//      パス1: 全頂点の密重みベクトル（len = layers.len()）を求め、チャンク合計を累積
//      パス2: 合計の上位 4 層をパレットとし、各頂点の重みをその 4 成分へ射影・正規化
//
//    【設計上の限界】
//      1 チャンク（既定 16m 角）内で同時に使えるレイヤは 4 種まで。パレットは
//      チャンク全体の重み合計で決まるため、チャンク内で局所的にしか出ない
//      5 番目の層は落ちる（その部分は残り 4 層へ再正規化されて描かれる）。
//      これを避けたい場合はチャンクを小さくするか、レイヤ設計を見直すこと。
// ============================================================

use crate::engine::core::loader::model::{
    CullFace, Material, Mesh, Model, ModelNode, Primitive, Vertex,
};
use crate::engine::terrain::layers::{
    blend_rule_and_paint_all, select_top_slots, TerrainLayerSet, TERRAIN_BLEND_SLOTS,
};
use crate::engine::terrain::marching_cubes::TerrainMesh;

/// 接線の既定値（xyz=+X 軸, w=+1 ハンドネス）。地形は法線マップを持たないためダミー。
const DEFAULT_TANGENT: [f32; 4] = [1.0, 0.0, 0.0, 1.0];
/// UV の既定値（頂点 UV 未使用。シェーダ側 triplanar がワールド座標から UV を作る）。
const DEFAULT_UV: [f32; 2] = [0.0, 0.0];

/// TerrainMesh を単一ノード・単一プリミティブの Model へ変換する。
///
/// - `name`:         モデル名（デバッグ表示用。source_path とは別）。
/// - `world_origin`: このチャンクのワールド原点（メートル）。頂点ローカル座標へ足して
///                   ワールド Y を求め、高度ルールの評価に使う。
/// - `layers`:       レイヤ定義一式（斜度／高度ルールの供給元）。
///
/// 各頂点のレイヤ重みは
///   `blend_rule_and_paint_all(layers.rule_weights_all(n.y, world_y), mesh.paint[i], mesh.paint_amount[i])`
/// で決まる（＝ルール自動下地と手ペイントの共存。layers.rs の解説を参照）。
///
/// 戻り値の 2 つ目は **このチャンクのレイヤパレット**（頂点カラー RGBA の各成分が
/// どのレイヤ番号を指すか）。GPU へは呼び出し側が uniform として渡す。
///
/// 空メッシュ（三角形なし）でも有効な Model を返す（描画は何も出ないだけ）。
pub fn terrain_mesh_to_model(
    mesh: &TerrainMesh,
    name: &str,
    world_origin: [f32; 3],
    layers: &TerrainLayerSet,
) -> (Model, [u32; TERRAIN_BLEND_SLOTS]) {
    // レイヤ定義数（密重みベクトルの次元）。0 層はあり得ないが防御的に 1 を下限とする。
    let layer_count = layers.layers.len().max(1);
    let vertex_count = mesh.positions.len();

    // ─── パス 1: 全頂点の密重みベクトルを求め、チャンク合計を累積する ───
    //   密重みは flat な Vec<f32>（頂点 i の重み = dense[i*layer_count .. +layer_count]）
    //   として持つ。Vec<Vec<f32>> より確保回数が少なく、走査も連続で速い。
    let mut dense = vec![0.0f32; vertex_count * layer_count];
    let mut chunk_total = vec![0.0f32; layer_count];
    for (i, pos) in mesh.positions.iter().enumerate() {
        // 法線は位置と対になっている（境界外でも normals[i] が必ず存在する前提）。
        let normal = mesh.normals.get(i).copied().unwrap_or([0.0, 1.0, 0.0]);

        // ── ルールによる自動下地（斜度＝法線 Y／高度＝ワールド Y）──
        let world_y = world_origin[1] + pos[1];
        let rule_w = layers.rule_weights_all(normal[1], world_y);

        // ── 手ペイント分と合成（ペイント量 0 の頂点は完全にルール任せ）──
        let paint_slots = mesh.paint.get(i).copied().unwrap_or_default();
        let paint_amount = mesh.paint_amount.get(i).copied().unwrap_or(0.0);
        let w = blend_rule_and_paint_all(&rule_w, &paint_slots, paint_amount);

        // rule_weights_all は layers.len() 長を返すが、layer_count は下限 1 なので
        // 短い場合に備えて明示的に切り詰めながら書き込む。
        let base = i * layer_count;
        for (k, &v) in w.iter().take(layer_count).enumerate() {
            dense[base + k] = v;
            chunk_total[k] += v;
        }
    }

    // ─── パレット決定: チャンク全体の重み合計で上位 4 層を選ぶ ───
    //   ここで落ちた層はこのチャンクでは一切描かれない（上記「設計上の限界」）。
    let palette_slots = select_top_slots(&chunk_total);
    let palette = palette_slots.index;

    // ─── パス 2: 各頂点の密重みをパレットの 4 成分へ射影し、正規化して頂点カラーへ ───
    let mut vertices: Vec<Vertex> = Vec::with_capacity(vertex_count);
    for (i, pos) in mesh.positions.iter().enumerate() {
        let normal = mesh.normals.get(i).copied().unwrap_or([0.0, 1.0, 0.0]);
        let base = i * layer_count;

        // パレットが指すレイヤの重みだけを抜き出す。
        let mut w = [0.0f32; TERRAIN_BLEND_SLOTS];
        let mut sum = 0.0f32;
        for slot in 0..TERRAIN_BLEND_SLOTS {
            let layer = palette[slot] as usize;
            // パレットの層がレイヤ定義の範囲外（定義が減った等）なら 0 のまま。
            if layer < layer_count {
                w[slot] = dense[base + layer];
                sum += w[slot];
            }
        }
        // パレット内で総和 1 に再正規化する（落とした層のぶんが配分される）。
        // 総和 0（この頂点ではパレット外の層しか立っていない）ならスロット 0 へ寄せる
        // ＝ layers.rs の縮退規約と同じ（黒落ち防止）。
        if sum > 0.0 {
            let inv = 1.0 / sum;
            for v in w.iter_mut() {
                *v *= inv;
            }
        } else {
            w[0] = 1.0;
        }

        vertices.push(Vertex {
            position: *pos,
            normal,
            tangent: DEFAULT_TANGENT,
            uv0:     DEFAULT_UV,
            uv1:     DEFAULT_UV,
            // 頂点カラー = パレット内スロット重み（RGBA = palette[0..3] の各層）。
            color:   [w[0], w[1], w[2], w[3]],
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

    // ─── 地形マテリアル（レイヤブレンド＋両面描画）───
    //   マーチングキューブスの三角ワインディングは（左手系エンジンの Ccw フロントフェイス
    //   規約と一致しないため）片面カリングだと地表が裏面判定で消える。両面描画
    //   （cull_face=None / double_sided）にして確実に見えるようにする。ライティングは
    //   面ワインディングではなく頂点法線（密度勾配＝外向き、テスト検証済み）で行われるため
    //   両面でも陰影は正しい。
    //
    //   terrain_layers=true が G-Buffer ジオメトリパスでの地形専用パイプライン選択の
    //   唯一のスイッチ（gbuffer.rs::draw_gbuffer_indirect を参照）。
    //   フォワード経路（deferred 無効時）へ落ちた場合は頂点カラー＝レイヤ重みが
    //   そのまま base_color へ乗算されるため、レイヤ色にはならないが黒落ちもしない。
    let material = Material {
        double_sided: true,
        cull_face: CullFace::None,
        terrain_layers: true,
        ..Material::default()
    };

    // ─── 最小構成の Model（テクスチャ・アニメ・スキンなし・地形マテリアル 1 枚）───
    let model = Model {
        name:       name.to_string(),
        nodes:      vec![node],
        root_nodes: vec![0],
        meshes:     vec![engine_mesh],
        materials:  vec![material],
        textures:   Vec::new(),
        animations: Vec::new(),
        skins:      Vec::new(),
    };

    (model, palette)
}
