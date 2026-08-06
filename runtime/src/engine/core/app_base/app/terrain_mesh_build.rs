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

use crate::engine::core::app_base::app::terrain_layer_albedo::chunk_avg_albedo;
use crate::engine::core::loader::model::{
    CullFace, Material, Mesh, Model, ModelNode, Primitive, Vertex,
};
use crate::engine::terrain::layers::{
    BlendSlots, TERRAIN_BLEND_SLOTS, TERRAIN_MAX_LAYERS, TerrainLayerSet, blend_rule_and_paint_all,
    blend_rule_and_paint_all_into, select_top_slots,
};
use crate::engine::terrain::marching_cubes::TerrainMesh;
use crate::engine::terrain::cover::{CoverField, CoverMaterialSet};

/// 接線の既定値（xyz=+X 軸, w=+1 ハンドネス）。地形は法線マップを持たないためダミー。
const DEFAULT_TANGENT: [f32; 4] = [1.0, 0.0, 0.0, 1.0];
/// 法線が欠けている頂点の代替値（真上向き）。positions と normals の長さは常に
/// 一致する前提だが、崩れても黒落ち・NaN を出さないための防御値。
const DEFAULT_NORMAL: [f32; 3] = [0.0, 1.0, 0.0];
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
    // ─── 頂点カラー（レイヤ重み）とチャンクパレットを求める ───
    //   計算そのものは compute_layer_colors に集約してある（差分更新と共有するため）。
    let (colors, palette) = compute_layer_colors(
        &mesh.positions,
        &mesh.normals,
        &mesh.paint,
        &mesh.paint_amount,
        world_origin,
        layers,
    );

    // ─── 位置・法線・色を束ねて Vertex 列を組み立てる ───
    let mut vertices: Vec<Vertex> = Vec::with_capacity(mesh.positions.len());
    for (i, pos) in mesh.positions.iter().enumerate() {
        let normal = mesh.normals.get(i).copied().unwrap_or(DEFAULT_NORMAL);
        vertices.push(Vertex {
            position: *pos,
            normal,
            tangent: DEFAULT_TANGENT,
            uv0: DEFAULT_UV,
            uv1: DEFAULT_UV,
            // 頂点カラー = パレット内スロット重み（RGBA = palette[0..3] の各層）。
            color: colors[i],
        });
    }

    // チャンクの実効平均アルベド（RT 反射／水面反射／DDGI／色付き影のヒット縮退色）。
    // 頂点カラー（スロット重み）とレイヤ実効色から求める。詳細は terrain_layer_albedo.rs。
    let avg_albedo = chunk_avg_albedo(&colors, palette, layers);

    build_terrain_model(vertices, mesh.indices.clone(), name, palette, avg_albedo)
}

/// 頂点ごとのレイヤ重み（＝頂点カラー RGBA）とチャンクパレットを計算する。
///
/// `terrain_mesh_to_model`（フル生成）と、ペイント時の頂点カラー差分更新の
/// **両方がこの 1 関数を使う**ため、差分更新の結果はフル再生成と定義上必ず一致する。
///
/// - `positions` / `normals`: 頂点の位置（チャンクローカル）と法線。同じ長さ・同じ順序。
/// - `paint` / `paint_amount`: 手ペイントのスロット重みとペイント量（TerrainMesh 由来）。
/// - `world_origin`: チャンクのワールド原点（高度ルールの評価に使う）。
/// - `layers`: レイヤ定義一式（斜度／高度ルールの供給元）。
///
/// 戻り値は (頂点カラー列, チャンクパレット)。頂点カラーは positions と同じ長さ。
pub fn compute_layer_colors(
    positions: &[[f32; 3]],
    normals: &[[f32; 3]],
    paint: &[BlendSlots],
    paint_amount: &[f32],
    world_origin: [f32; 3],
    layers: &TerrainLayerSet,
) -> (Vec<[f32; 4]>, [u32; TERRAIN_BLEND_SLOTS]) {
    // レイヤ定義数（密重みベクトルの次元）。0 層はあり得ないが防御的に 1 を下限とする。
    let layer_count = layers.layers.len().max(1);
    let vertex_count = positions.len();

    // ─── 頂点ループ用スクラッチバッファ（1 頂点ごとのヒープ確保を無くすため）───
    //   レイヤ重みの密ベクトルは最大でも TERRAIN_MAX_LAYERS 次元なので、
    //   スタック上の固定長配列で足りる。ここを Vec 確保にすると 64³ チャンクで
    //   8 万回以上の malloc/free が走り、頂点カラー計算がホットパス化する。
    let mut rule_buf = [0.0f32; TERRAIN_MAX_LAYERS];
    let mut blend_buf = [0.0f32; TERRAIN_MAX_LAYERS];

    // レイヤ定義数（＝密重みベクトルの実際の次元。layer_count と違い下限 1 を課さない）。
    let rule_len = layers.layers.len();
    // スクラッチ経路が使えるか。レイヤ定義は from_json_str が TERRAIN_MAX_LAYERS へ
    // 切り詰めるため通常は必ず true だが、TerrainLayerSet を直接組み立てれば
    // 上限超えもあり得る。その場合は従来の Vec 経路へ落とし、挙動を一切変えない。
    let use_scratch = rule_len <= TERRAIN_MAX_LAYERS;

    // ─── パス 1: 全頂点の密重みベクトルを求め、チャンク合計を累積する ───
    //   密重みは flat な Vec<f32>（頂点 i の重み = dense[i*layer_count .. +layer_count]）
    //   として持つ。Vec<Vec<f32>> より確保回数が少なく、走査も連続で速い。
    let mut dense = vec![0.0f32; vertex_count * layer_count];
    let mut chunk_total = vec![0.0f32; layer_count];
    for (i, pos) in positions.iter().enumerate() {
        // 法線は位置と対になっている（境界外でも normals[i] が必ず存在する前提）。
        let normal = normals.get(i).copied().unwrap_or(DEFAULT_NORMAL);

        // ── ルールによる自動下地（斜度＝法線 Y／高度＝ワールド Y）──
        let world_y = world_origin[1] + pos[1];

        // ── 手ペイント分と合成（ペイント量 0 の頂点は完全にルール任せ）──
        let paint_slots = paint.get(i).copied().unwrap_or_default();
        let vertex_paint_amount = paint_amount.get(i).copied().unwrap_or(0.0);

        // スクラッチ経路（既定）と Vec 経路（レイヤ上限超えの防御）は同一の演算。
        // 後者でしか使わない一時 Vec は、借用を揃えるためここで宣言しておく。
        let fallback_w: Vec<f32>;
        let w: &[f32] = if use_scratch {
            layers.rule_weights_all_into(normal[1], world_y, &mut rule_buf);
            blend_rule_and_paint_all_into(
                &rule_buf[..rule_len],
                &paint_slots,
                vertex_paint_amount,
                &mut blend_buf,
            );
            &blend_buf[..rule_len]
        } else {
            let rule_w = layers.rule_weights_all(normal[1], world_y);
            fallback_w = blend_rule_and_paint_all(&rule_w, &paint_slots, vertex_paint_amount);
            &fallback_w
        };

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
    let mut colors: Vec<[f32; 4]> = Vec::with_capacity(vertex_count);
    for i in 0..vertex_count {
        let base = i * layer_count;

        // パレットが指すレイヤの重みだけを抜き出す。
        //
        // 【重複スロットの扱い】レイヤ定義が 4 層未満のとき `select_top_slots` は
        // 余ったスロットをレイヤ 0 で埋めるため、パレットに同じレイヤ番号が複数回
        // 現れる。そのまま各スロットへ同じ重みを入れると、シェーダ側でそのレイヤが
        // 重複して積算され（例: 2 層構成でレイヤ 0 が 3 倍）、ブレンド比が歪む。
        // よって 2 回目以降に現れたレイヤのスロットは重み 0 に落とす。
        let mut w = [0.0f32; TERRAIN_BLEND_SLOTS];
        let mut sum = 0.0f32;
        // 既にどのスロットへ載せたレイヤ番号か（重複検出用。要素数 4 なので線形探索で十分）。
        let mut assigned: [Option<u32>; TERRAIN_BLEND_SLOTS] = [None; TERRAIN_BLEND_SLOTS];
        for slot in 0..TERRAIN_BLEND_SLOTS {
            let layer_id = palette[slot];
            let layer = layer_id as usize;
            // パレットの層がレイヤ定義の範囲外（定義が減った等）なら 0 のまま。
            // 既出レイヤ（重複スロット）も 0 のままにする。
            if layer < layer_count && !assigned.contains(&Some(layer_id)) {
                assigned[slot] = Some(layer_id);
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

        // 頂点カラー = パレット内スロット重み（RGBA = palette[0..3] の各層）。
        colors.push([w[0], w[1], w[2], w[3]]);
    }

    (colors, palette)
}

/// 既存の地形チャンク Model から「頂点カラーとパレットだけを差し替えた」新しい Model を作る。
///
/// 【なぜ新規に作るのか — `Arc::make_mut` が使えない理由】
///   ペイント高速パスは CPU 側モデル（`ModelComponent::model: Arc<Model>`）の頂点カラーも
///   更新しなければならない。`slot_ops.rs` のマテリアルオーバーライド設定経路が
///   `mc.model` から `upload_model_with_overrides` で GPU リソースを作り直すため、
///   CPU 側の色が古いままだと、後でオーバーライドを付けた瞬間に色が巻き戻るからである。
///   ところが `Model` / `Mesh` / `Primitive` は `Clone` を実装していないため
///   `Arc::make_mut` は使えず、また `Arc` は共有されうる（統合バッチキャッシュ等）ので
///   `Arc::get_mut` も成功が保証されない。`get_mut` の成否で挙動が分かれるのは
///   非決定的で危ういため、**常に新しい Model を組み立てて丸ごと差し替える**方式に統一する。
///
///   コストは「頂点列＋インデックス列の memcpy」だけで、マーチングキューブス
///   （形状生成・法線の勾配評価・辺キャッシュ）は一切走らない。地形チャンクは
///   LOD もメッシュレットも持たないため、それ以外に複製すべき重いデータも無い。
///
/// - `src_vertices`: 元モデルの頂点列。位置・法線・接線・UV はそのまま引き継ぐ。
/// - `indices`:      元モデルのインデックス列（ペイントでは不変）。
/// - `colors`:       新しい頂点カラー。`src_vertices` と同じ長さでなければならない。
/// - `layers`:       レイヤ定義一式（チャンク平均アルベドの再計算に使う）。
///
/// 長さが食い違う場合は `None` を返す（呼び出し側はフル再メッシュへフォールバックする）。
///
/// 【平均アルベドも必ず作り直すこと】ペイントでレイヤ重みが変われば、RT 反射・水面反射・
/// DDGI が使うチャンク平均色も変わる。ここで再計算しないと、塗った直後は見た目だけが
/// 変わり、水面に映る色が塗る前のまま取り残される。
pub fn rebuild_terrain_model_with_colors(
    src_vertices: &[Vertex],
    indices: &[u32],
    name: &str,
    colors: &[[f32; 4]],
    palette: [u32; TERRAIN_BLEND_SLOTS],
    layers: &TerrainLayerSet,
) -> Option<Model> {
    if src_vertices.len() != colors.len() {
        return None;
    }
    let vertices: Vec<Vertex> = src_vertices
        .iter()
        .zip(colors.iter())
        .map(|(v, c)| Vertex { color: *c, ..*v })
        .collect();
    let avg_albedo = chunk_avg_albedo(colors, palette, layers);
    let (model, _palette) =
        build_terrain_model(vertices, indices.to_vec(), name, palette, avg_albedo);
    Some(model)
}

// ============================================================
//  カバー場（I3.1）の頂点への焼き込み
// ============================================================

/// カバー場を頂点へ焼き込んだ地形チャンク Model を組み立てる。
///
/// 【なぜ頂点へ焼くのか（設計判断）】
///   カバーの見た目には「色・粗さの上書き」と「量に応じた盛り上げ（変位）」の 2 つがある。
///   後者を **頂点シェーダ**でやると、その頂点シェーダは G-Buffer パス専用であり、
///   シャドウマップ・深度プリパス・ID パス・RT の BLAS が持つ形状とズレる
///   （雪が影を落とさない・雪の下に地面が透ける）。
///   位置そのものへ焼けば、頂点バッファを共有する **全パスが自動的に一致**する。
///
///   色・粗さの側は逆にシェーダで解決する。頂点へ焼くと素材の粗さが運べず、
///   地形レイヤとのブレンドもできないためである。頂点は「量」と「素材番号」だけを
///   運び（未使用だった `uv0` を転用）、実際の合成は terrain_gbuffer_write.wgsl が行う。
///     uv0.x = 量（0..1）  … 0 のとき従来と完全に同一の絵になる
///     uv0.y = 素材添字     … フラグメント側で round() して uniform の素材表を引く
///
/// 【引数】
/// - `src_vertices`: 現在の CPU モデルの頂点列（法線・頂点カラーはそのまま引き継ぐ）
/// - `base_positions`: **カバー適用前**の頂点位置。ここへ毎回変位を足し直すことで、
///   適用を繰り返しても変位が累積しない（前回の変位を引き算する必要が無い）
/// - `base_avg_albedo`: カバー適用前のチャンク平均アルベド（RGB）
/// - `chunk_extent`: チャンク 1 辺のワールド長（頂点のチャンクローカル座標 → カバー UV）
///
/// 長さが食い違う場合は `None`（呼び出し側はフル再メッシュへフォールバックする）。
pub fn rebuild_terrain_model_with_cover(
    src_vertices: &[Vertex],
    indices: &[u32],
    name: &str,
    palette: [u32; TERRAIN_BLEND_SLOTS],
    base_positions: &[[f32; 3]],
    base_avg_albedo: [f32; 3],
    field: &CoverField,
    materials: &CoverMaterialSet,
    chunk_extent: f32,
) -> Option<Model> {
    if src_vertices.len() != base_positions.len() || !(chunk_extent > 0.0) {
        return None;
    }

    // ─── 平均アルベドの加重平均用アキュムレータ ───
    //   RT 反射・水面反射・DDGI が使うチャンク平均色。雪が積もれば水面へ映る色も
    //   白くならなければならないので、ここで量に比例して寄せる。
    let mut cover_color_sum = [0.0f32; 3];
    let mut cover_weight_sum = 0.0f32;

    let mut vertices: Vec<Vertex> = Vec::with_capacity(src_vertices.len());
    for (v, base) in src_vertices.iter().zip(base_positions.iter()) {
        // ─── 頂点のチャンクローカル位置 → カバー場 UV ───
        //   地形チャンクの頂点はチャンク最小コーナー原点のローカル座標である
        //   （ワールド化はノード／インスタンス行列の担当）。
        let (amount, material_index) =
            field.sample(base[0] / chunk_extent, base[2] / chunk_extent);

        // ─── 素材が引けない（未定義添字・素材セットが空）なら「カバー無し」へ縮退 ───
        let Some(mat) = materials.get(material_index as usize).filter(|_| amount > 0.0) else {
            vertices.push(Vertex {
                position: *base,
                uv0: [0.0, 0.0],
                ..*v
            });
            continue;
        };

        // ─── 変位: 基準位置から法線方向へ「量 × 素材の変位高さ」だけ持ち上げる ───
        let lift = amount * mat.displacement;
        let position = [
            base[0] + v.normal[0] * lift,
            base[1] + v.normal[1] * lift,
            base[2] + v.normal[2] * lift,
        ];

        // 平均アルベドの寄与を溜める。
        for c in 0..3 {
            cover_color_sum[c] += mat.albedo[c] * amount;
        }
        cover_weight_sum += amount;

        vertices.push(Vertex {
            position,
            // uv0 は地形では未使用だった（常に [0,0]）。ここでカバー情報の運び手に転用する。
            uv0: [amount, material_index as f32],
            ..*v
        });
    }

    // ─── 平均アルベド: 「全頂点の平均被覆率」でカバー色へ寄せる ───
    let avg_albedo = if cover_weight_sum > 0.0 && !vertices.is_empty() {
        let coverage = (cover_weight_sum / vertices.len() as f32).clamp(0.0, 1.0);
        let cover_color = [
            cover_color_sum[0] / cover_weight_sum,
            cover_color_sum[1] / cover_weight_sum,
            cover_color_sum[2] / cover_weight_sum,
        ];
        [
            base_avg_albedo[0] + (cover_color[0] - base_avg_albedo[0]) * coverage,
            base_avg_albedo[1] + (cover_color[1] - base_avg_albedo[1]) * coverage,
            base_avg_albedo[2] + (cover_color[2] - base_avg_albedo[2]) * coverage,
        ]
    } else {
        base_avg_albedo
    };

    let (model, _palette) =
        build_terrain_model(vertices, indices.to_vec(), name, palette, avg_albedo);
    Some(model)
}

/// 頂点列・インデックス列・パレットから、地形チャンク 1 個ぶんの Model を組み立てる。
///
/// Model の骨組み（単一ノード・単一メッシュ・地形マテリアル）はレイヤ計算とは
/// 独立した関心事なので、`terrain_mesh_to_model` から分離してある。
///
/// - `avg_albedo`: このチャンクの実効平均アルベド（リニア RGB）。RT 反射・水面反射・
///   DDGI・RT 色付き影が「テクスチャを引けないヒット」で使う縮退色になる。
fn build_terrain_model(
    vertices: Vec<Vertex>,
    indices: Vec<u32>,
    name: &str,
    palette: [u32; TERRAIN_BLEND_SLOTS],
    avg_albedo: [f32; 3],
) -> (Model, [u32; TERRAIN_BLEND_SLOTS]) {
    // ─── 1 プリミティブ（1 マテリアル）を構築する ───
    //   skin_vertices は必ず空（地形はスキニング非対応）。LOD・メッシュレットも未生成。
    let primitive = Primitive {
        vertices,
        skin_vertices: Vec::new(),
        indices,
        material_index: Some(0),
        lod_indices: Vec::new(),
        meshlets: Vec::new(),
        meshlet_vertices: Vec::new(),
        meshlet_triangles: Vec::new(),
    };

    let engine_mesh = Mesh {
        name: name.to_string(),
        primitives: vec![primitive],
    };

    // ─── 単一ノード（恒等ローカル変換・mesh_index=0）を構築する ───
    let node = ModelNode {
        name: name.to_string(),
        local_matrix: ModelNode::identity_matrix(),
        translation: [0.0, 0.0, 0.0],
        rotation: [0.0, 0.0, 0.0, 1.0],
        scale: [1.0, 1.0, 1.0],
        mesh_index: Some(0),
        skin_index: None,
        children: Vec::new(),
        parent: None,
    };

    // ─── 地形マテリアル（レイヤブレンド＋通常の背面カリング）───
    //   マーチングキューブス側（marching_cubes.rs::push_triangle）が
    //   エンジン規約（左手系 / front_face=Ccw / cull_mode=Back）どおりの巻き順で
    //   三角形を出すようになったため、通常の背面カリングで正しく描ける。
    //
    //   【なぜ両面描画を止めたか】
    //   両面（cull_face=None）だと地表フラグメントが裏面判定になり、
    //   terrain_gbuffer_write.wgsl の front_facing 反転（facing_sign = -1）で
    //   法線が丸ごと反転する。結果、ライト方向に対する陰影が逆転し
    //   （上向きライトで地形が明るくなる）、シャドウの法線オフセットバイアスも
    //   逆方向に効いて斑状のシャドウアクネを生む。片面に戻すことが本質的な修正。
    //
    //   terrain_layers=true が G-Buffer ジオメトリパスでの地形専用パイプライン選択の
    //   唯一のスイッチ（gbuffer.rs::draw_gbuffer_indirect を参照）。
    //   フォワード経路（deferred 無効時）へ落ちた場合は頂点カラー＝レイヤ重みが
    //   そのまま base_color へ乗算されるため、レイヤ色にはならないが黒落ちもしない。
    //
    //   【重要】terrain_palette は必ずここで設定する。頂点カラーの 4 成分は
    //   「レイヤ番号」ではなく「このチャンクのパレット内スロット」を意味するため、
    //   パレットをマテリアルへ載せ忘れると描画側が既定パレット（恒等 [0,1,2,3]）で
    //   解決してしまい、チャンクごとに層が入れ替わって描かれる（T2b の回帰点）。
    //   このフィールドは upload_model → GpuMaterial → gbuffer の group3 選択まで運ばれる。
    //
    //   【平均アルベド（RT 反射／水面反射／DDGI／RT 色付き影）】
    //   地形はレイヤブレンドで色を決めるため単一のベースカラーテクスチャを持たず、
    //   bindless のヒット実サンプル経路（water_reflection_hit_on.wgsl 等）へは乗れない。
    //   それらのシェーダはテクスチャを引けないインスタンスを
    //   `BindlessInstanceRecord.avg_albedo` へ縮退させるので、ここへチャンクの実効平均色を
    //   焼いておかないと、水面に映る画面外の地形が**白／灰色のベタ塗り**になる。
    //   - `avg_albedo.rgb` = チャンク平均色、`.a` = 1.0（不透明。色付き影のアルファ源）。
    //   - `base_color_tex_avg` にも同じ値を入れる。これは「base_color_factor を掛ける前の
    //     テクスチャ平均」という定義のフィールドで、`base_color_factor` が白の地形では
    //     avg_albedo と一致する。マテリアルオーバーライドが掛かったとき
    //     `eff_avg_albedo`（gpu_resources.rs）が **テクスチャ平均 × 新 factor** で
    //     再計算するため、ここを白のままにするとオーバーライドの瞬間に地形色が失われる。
    let material = Material {
        double_sided: false,
        cull_face: CullFace::Back,
        terrain_layers: true,
        terrain_palette: palette,
        avg_albedo: [avg_albedo[0], avg_albedo[1], avg_albedo[2], 1.0],
        base_color_tex_avg: avg_albedo,
        ..Material::default()
    };

    // ─── 最小構成の Model（テクスチャ・アニメ・スキンなし・地形マテリアル 1 枚）───
    let model = Model {
        name: name.to_string(),
        nodes: vec![node],
        root_nodes: vec![0],
        meshes: vec![engine_mesh],
        materials: vec![material],
        textures: Vec::new(),
        animations: Vec::new(),
        skins: Vec::new(),
    };

    (model, palette)
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::terrain::layers::{BlendSlots, TerrainLayer, TerrainLayerSet};

    /// テスト用チャンク原点（高度ルールの影響を切るため常に 0）。
    const TEST_ORIGIN: [f32; 3] = [0.0, 0.0, 0.0];
    /// 重み比較の許容誤差（f32 の正規化 2 回ぶん）。
    const EPS: f32 = 1.0e-5;

    /// 指定レイヤを 100% 手ペイントした 3 頂点（1 三角形）のメッシュを作る。
    ///
    /// `paint_amount = 1.0` にすることでルール自動下地の寄与を完全に排除でき、
    /// 「どのレイヤを塗ったか」と「復元されたレイヤ」を 1 対 1 で突き合わせられる。
    fn painted_mesh(layer: u32) -> TerrainMesh {
        let mut paint = BlendSlots::default();
        paint.index = [layer, 0, 0, 0];
        paint.weight = [1.0, 0.0, 0.0, 0.0];
        TerrainMesh {
            positions: vec![[0.0, 0.0, 0.0], [1.0, 0.0, 0.0], [0.0, 0.0, 1.0]],
            normals: vec![[0.0, 1.0, 0.0]; 3],
            indices: vec![0, 1, 2],
            paint: vec![paint; 3],
            paint_amount: vec![1.0; 3],
            // 由来辺（edges）はペイント差分更新でしか使わないので、ここでは空で良い。
            ..Default::default()
        }
    }

    /// GPU 側（terrain_gbuffer_write.wgsl）と同じ手順で
    /// 「頂点カラー（スロット重み）＋パレット」からレイヤ別の重みを復元する。
    ///
    /// シェーダは `weight[slot]` を `layers[palette[slot]]` へ適用するだけなので、
    /// CPU 側でも同じ加算をすれば描画結果のレイヤ配分を検証できる。
    fn resolve_layer_weights(
        color: [f32; 4],
        palette: [u32; TERRAIN_BLEND_SLOTS],
        layer_count: usize,
    ) -> Vec<f32> {
        let mut out = vec![0.0f32; layer_count];
        for slot in 0..TERRAIN_BLEND_SLOTS {
            let layer = palette[slot] as usize;
            if layer < layer_count {
                out[layer] += color[slot];
            }
        }
        out
    }

    /// 最も重みの大きいレイヤ番号を返す。
    fn dominant_layer(w: &[f32]) -> usize {
        w.iter()
            .enumerate()
            .max_by(|a, b| a.1.partial_cmp(b.1).unwrap())
            .map(|(i, _)| i)
            .unwrap()
    }

    /// 単色 n 層のレイヤセットを作る（ルールは既定＝全レイヤ同条件）。
    fn flat_layer_set(n: usize) -> TerrainLayerSet {
        TerrainLayerSet {
            layers: (0..n)
                .map(|i| TerrainLayer {
                    name: format!("layer{i}"),
                    ..TerrainLayer::default()
                })
                .collect(),
        }
    }

    /// 【回帰テストの本体】チャンクごとにパレットが違っても、
    /// 復元されるレイヤは「塗ったレイヤ」と一致すること。
    ///
    /// パレットをマテリアルへ載せ忘れる（＝描画側が恒等パレットで解決する）と
    /// この対応が崩れ、チャンクによって層が入れ替わって描かれる。
    #[test]
    fn painted_layer_survives_per_chunk_palette_permutation() {
        let layers = TerrainLayerSet::default(); // 既定 4 層（grass/dirt/rock/sand）
        let layer_count = layers.layers.len();

        // 各レイヤを塗ったチャンクを 1 つずつ作り、復元結果を突き合わせる。
        for painted in 0..layer_count as u32 {
            let mesh = painted_mesh(painted);
            let (model, palette) = terrain_mesh_to_model(&mesh, "chunk", TEST_ORIGIN, &layers);

            // ① パレットはマテリアルへ載っていること（これが抜けると描画側が恒等になる）。
            assert_eq!(
                model.materials[0].terrain_palette, palette,
                "レイヤ {painted}: パレットが material.terrain_palette へ載っていない"
            );
            assert!(model.materials[0].terrain_layers);

            // ② 頂点カラー＋パレットから復元したレイヤが、塗ったレイヤと一致すること。
            let color = model.meshes[0].primitives[0].vertices[0].color;
            let resolved = resolve_layer_weights(color, palette, layer_count);
            assert_eq!(
                dominant_layer(&resolved),
                painted as usize,
                "レイヤ {painted} を塗ったのに復元結果が違う: color={color:?} palette={palette:?}"
            );
            assert!(
                (resolved[painted as usize] - 1.0).abs() < EPS,
                "塗ったレイヤの重みが 1 でない: {resolved:?}"
            );
        }
    }

    /// パレットが恒等でないチャンクが実際に生じること（テスト自体が形骸化しない担保）。
    ///
    /// 併せて「恒等パレットで解決すると誤ったレイヤになる」ことも示し、
    /// パレット結線が本当に必要であることを固定する。
    #[test]
    fn non_identity_palette_is_produced_and_identity_would_be_wrong() {
        let layers = TerrainLayerSet::default();
        let layer_count = layers.layers.len();

        // レイヤ 2（rock）を塗ったチャンク。上位スロットは 2 から始まる＝非恒等。
        const PAINTED: u32 = 2;
        let mesh = painted_mesh(PAINTED);
        let (model, palette) = terrain_mesh_to_model(&mesh, "chunk", TEST_ORIGIN, &layers);

        assert_ne!(
            palette,
            [0, 1, 2, 3],
            "非恒等パレットが生じていない（前提の崩れ）"
        );
        assert_eq!(
            palette[0], PAINTED,
            "最重要スロットは塗ったレイヤであるべき"
        );

        let color = model.meshes[0].primitives[0].vertices[0].color;
        // 恒等パレットで解決すると（＝バグ時の描画）別レイヤになる。
        let wrong = resolve_layer_weights(color, [0, 1, 2, 3], layer_count);
        assert_ne!(
            dominant_layer(&wrong),
            PAINTED as usize,
            "恒等パレットでも正しく出てしまうと、この回帰テストは何も守れていない"
        );
    }

    /// 隣り合う 2 チャンクが別パレットでも、同じレイヤを塗れば同じレイヤに解決されること。
    #[test]
    fn two_chunks_with_different_palettes_agree_on_layer() {
        let layers = TerrainLayerSet::default();
        let layer_count = layers.layers.len();

        let (model_a, pal_a) = terrain_mesh_to_model(&painted_mesh(0), "a", TEST_ORIGIN, &layers);
        let (model_b, pal_b) = terrain_mesh_to_model(&painted_mesh(2), "b", TEST_ORIGIN, &layers);
        assert_ne!(
            pal_a, pal_b,
            "別レイヤを塗ったチャンクは別パレットになるはず"
        );

        let res_a = resolve_layer_weights(
            model_a.meshes[0].primitives[0].vertices[0].color,
            pal_a,
            layer_count,
        );
        let res_b = resolve_layer_weights(
            model_b.meshes[0].primitives[0].vertices[0].color,
            pal_b,
            layer_count,
        );

        assert_eq!(dominant_layer(&res_a), 0);
        assert_eq!(dominant_layer(&res_b), 2);
    }

    /// `compute_layer_colors` を単体で呼んだ結果が、`terrain_mesh_to_model` が組み立てた
    /// `Vertex.color` および `material.terrain_palette` と完全一致すること。
    ///
    /// ペイント時の頂点カラー差分更新は `compute_layer_colors` だけを呼ぶ設計なので、
    /// この一致が崩れると「差分更新した箇所とフル再生成した箇所で色が食い違う」
    /// （＝再メッシュのたびに色がちらつく）という形で表面化する。
    #[test]
    fn compute_layer_colors_matches_terrain_mesh_to_model() {
        let layers = TerrainLayerSet::default();

        // レイヤごとに塗り分けたメッシュを一通り試す（パレットの並びが毎回変わる）。
        for painted in 0..layers.layers.len() as u32 {
            let mesh = painted_mesh(painted);
            let (model, model_palette) =
                terrain_mesh_to_model(&mesh, "chunk", TEST_ORIGIN, &layers);

            let (colors, palette) = compute_layer_colors(
                &mesh.positions,
                &mesh.normals,
                &mesh.paint,
                &mesh.paint_amount,
                TEST_ORIGIN,
                &layers,
            );

            // ① パレットが一致すること（Model 側・Material 側の双方）。
            assert_eq!(palette, model_palette, "レイヤ {painted}: パレットが不一致");
            assert_eq!(
                palette, model.materials[0].terrain_palette,
                "レイヤ {painted}: material.terrain_palette と不一致"
            );

            // ② 頂点カラーが 1 ビット違わず一致すること。
            let verts = &model.meshes[0].primitives[0].vertices;
            assert_eq!(
                colors.len(),
                verts.len(),
                "レイヤ {painted}: 頂点数が不一致"
            );
            for (i, (c, v)) in colors.iter().zip(verts.iter()).enumerate() {
                for k in 0..TERRAIN_BLEND_SLOTS {
                    assert_eq!(
                        c[k].to_bits(),
                        v.color[k].to_bits(),
                        "レイヤ {painted}: 頂点 {i} スロット {k} の色がビット不一致 \
                         ({} vs {})",
                        c[k],
                        v.color[k]
                    );
                }
            }
        }
    }

    // ============================================================
    //  ペイント高速パス（メッシュ再生成なしの頂点カラー差し替え）の検証
    //
    //  GPU を持たない環境でも回るよう、App / DrawContext には一切触れず
    //  「由来辺（TerrainVertexEdge）＋ interp_vertex_paint ＋ compute_layer_colors」
    //  という高速パスの**計算部分そのもの**を組み立てて、フル再生成と突き合わせる。
    // ============================================================

    use crate::engine::terrain::marching_cubes::{generate_standalone, interp_vertex_paint};
    use crate::engine::terrain::paint::{PaintField, apply_paint};
    use crate::engine::terrain::{
        BlendSlots as TerrainBlendSlots, ChunkCoord, SphereBrush, TerrainChunkData, TerrainSettings,
    };

    /// テスト球 SDF の中心（チャンクローカル、メートル）。チャンク中央付近に置く。
    const SPHERE_CENTER: [f32; 3] = [8.0, 8.0, 8.0];
    /// テスト球 SDF の半径（メートル）。
    const SPHERE_RADIUS: f32 = 5.0;
    /// ペイントブラシの離散適用時間（terrain_ops.rs の BRUSH_DT と同じ 1 回ぶん）。
    const TEST_BRUSH_DT: f32 = 1.0;
    /// ペイントブラシ半径（メートル）。球表面の一部だけを覆う大きさにする
    /// （全面を覆うとチャンク合計が一様になり、パレット変化の検出テストが鈍る）。
    const TEST_PAINT_RADIUS: f32 = 4.0;
    /// ペイントブラシ強度（1 回で paint_amount がほぼ 1 に達する値）。
    const TEST_PAINT_STRENGTH: f32 = 2.0;
    /// パレット変化テストで塗る「まだそのチャンクに無いレイヤ」の番号。
    const TEST_NEW_LAYER: u32 = 5;
    /// パレット変化テストのレイヤ定義数（TEST_NEW_LAYER を含められる数）。
    const TEST_WIDE_LAYER_COUNT: usize = 6;

    /// 単一チャンクだけを対象とする最小の `PaintField` 実装。
    ///
    /// テスト対象は 1 チャンク（座標 (0,0,0)）だけなので、グローバルサンプル座標は
    /// そのままローカル添字になる。範囲外は「未ペイント」を返し、書き込みは無視する。
    struct SingleChunkPaintField<'a> {
        settings: &'a TerrainSettings,
        chunk: &'a mut TerrainChunkData,
    }

    impl<'a> SingleChunkPaintField<'a> {
        /// グローバルサンプル座標がこのチャンクの範囲内なら添字を返す。
        fn local(&self, gx: i32, gy: i32, gz: i32) -> Option<(usize, usize, usize)> {
            let s = self.settings.samples_per_axis() as i32;
            if gx < 0 || gy < 0 || gz < 0 || gx >= s || gy >= s || gz >= s {
                return None;
            }
            Some((gx as usize, gy as usize, gz as usize))
        }
    }

    impl<'a> PaintField for SingleChunkPaintField<'a> {
        fn settings(&self) -> &TerrainSettings {
            self.settings
        }
        fn read_paint_global(&self, gx: i32, gy: i32, gz: i32) -> (TerrainBlendSlots, f32) {
            match self.local(gx, gy, gz) {
                Some((x, y, z)) => (
                    self.chunk.paint_slots(x, y, z),
                    self.chunk.paint_amount(x, y, z),
                ),
                None => (TerrainBlendSlots::default(), 0.0),
            }
        }
        fn write_paint_global(
            &mut self,
            gx: i32,
            gy: i32,
            gz: i32,
            slots: &TerrainBlendSlots,
            amount: f32,
        ) {
            if let Some((x, y, z)) = self.local(gx, gy, gz) {
                self.chunk.set_paint_slots(x, y, z, slots);
                self.chunk.set_paint_amount(x, y, z, amount);
            }
        }
        fn world_of_global(&self, gx: i32, gy: i32, gz: i32) -> [f32; 3] {
            let v = self.settings.voxel_size;
            [gx as f32 * v, gy as f32 * v, gz as f32 * v]
        }
    }

    /// 球 SDF を密度として持つチャンクを作る（表面＝球面に三角形が出る）。
    fn sphere_chunk(settings: &TerrainSettings) -> TerrainChunkData {
        let mut chunk = TerrainChunkData::new_filled(settings, 0.0);
        let s = settings.samples_per_axis();
        let voxel = settings.voxel_size;
        for iz in 0..s {
            for iy in 0..s {
                for ix in 0..s {
                    let p = [ix as f32 * voxel, iy as f32 * voxel, iz as f32 * voxel];
                    let d = [
                        p[0] - SPHERE_CENTER[0],
                        p[1] - SPHERE_CENTER[1],
                        p[2] - SPHERE_CENTER[2],
                    ];
                    let dist = (d[0] * d[0] + d[1] * d[1] + d[2] * d[2]).sqrt();
                    chunk.set_sample(ix, iy, iz, dist - SPHERE_RADIUS);
                }
            }
        }
        chunk
    }

    /// 高速パスの計算部分そのもの：保存しておいた由来辺からスプラットを引き直し、
    /// 既存メッシュの位置・法線と合わせて頂点カラーとパレットを再構築する。
    ///
    /// ランタイム側（`App::apply_terrain_paint_colors`）が行う計算と同一の手順・同一の
    /// 関数呼び出しであり、違いは「位置・法線を CPU モデルから取るか TerrainMesh から
    /// 取るか」だけ（どちらも同じ値）。
    fn fast_path_colors(
        chunk: &TerrainChunkData,
        edges: &[crate::engine::terrain::TerrainVertexEdge],
        positions: &[[f32; 3]],
        normals: &[[f32; 3]],
        world_origin: [f32; 3],
        layers: &TerrainLayerSet,
    ) -> (Vec<[f32; 4]>, [u32; TERRAIN_BLEND_SLOTS]) {
        let interpolated: Vec<(BlendSlots, f32)> = edges
            .iter()
            .map(|e| interp_vertex_paint(chunk, e))
            .collect();
        let paint: Vec<BlendSlots> = interpolated.iter().map(|p| p.0).collect();
        let paint_amount: Vec<f32> = interpolated.iter().map(|p| p.1).collect();
        compute_layer_colors(
            positions,
            normals,
            &paint,
            &paint_amount,
            world_origin,
            layers,
        )
    }

    /// 【この最適化の正しさの中核】
    /// ペイント後に「由来辺から再構築した頂点カラー・パレット」が、
    /// 「ペイント後にフルで作り直したメッシュ」の頂点カラー・パレットと **f32 ビット一致**すること。
    ///
    /// ここが崩れると、高速パスで塗ったチャンクと（フォールバックで）再メッシュした
    /// チャンクとで色が食い違い、ストローク中に色がちらつく。
    #[test]
    fn paint_fast_path_colors_match_full_regeneration() {
        let settings = TerrainSettings::default();
        let layers = TerrainLayerSet::default();
        let coord = ChunkCoord::new(0, 0, 0);
        let world_origin = coord.world_origin(&settings);

        // ── (a) ペイント前のフル生成。由来辺をここで保存しておく（＝ランタイムのキャッシュ相当）──
        let mut chunk = sphere_chunk(&settings);
        let mesh_before = generate_standalone(&chunk, &settings);
        assert!(
            !mesh_before.positions.is_empty(),
            "球メッシュが空（前提の崩れ）"
        );
        let edges = mesh_before.edges.clone();
        let positions = mesh_before.positions.clone();
        let normals = mesh_before.normals.clone();
        let (colors_before, palette_before) = compute_layer_colors(
            &positions,
            &normals,
            &mesh_before.paint,
            &mesh_before.paint_amount,
            world_origin,
            &layers,
        );

        // ── ペイントを当てる（密度は変えず、スプラット場だけが書き換わる）──
        //   既定レイヤ 4 層の範囲内（レイヤ 1）を塗り、パレットが変わらないケースを作る。
        //   ブラシ中心は球「面」上の点にする。球の中心に置くと半径 4 のブラシが
        //   半径 5 の球面へ届かず、頂点が 1 つも塗られない（＝テストが空回りする）。
        let brush = SphereBrush {
            center: [
                SPHERE_CENTER[0] + SPHERE_RADIUS,
                SPHERE_CENTER[1],
                SPHERE_CENTER[2],
            ],
            radius: TEST_PAINT_RADIUS,
            strength: TEST_PAINT_STRENGTH,
        };
        {
            let mut field = SingleChunkPaintField {
                settings: &settings,
                chunk: &mut chunk,
            };
            let affected = apply_paint(&mut field, &brush, 1, TEST_BRUSH_DT);
            assert!(
                !affected.is_empty(),
                "ペイントが 1 サンプルにも当たっていない（前提の崩れ）"
            );
        }

        // ── (b) 高速パス: 保存しておいた由来辺から再構築 ──
        let (colors_fast, palette_fast) =
            fast_path_colors(&chunk, &edges, &positions, &normals, world_origin, &layers);

        // ── (c) フル再生成: ペイント後のチャンクからメッシュごと作り直す ──
        let mesh_after = generate_standalone(&chunk, &settings);
        let (model_full, palette_full) =
            terrain_mesh_to_model(&mesh_after, "chunk", world_origin, &layers);
        let verts_full = &model_full.meshes[0].primitives[0].vertices;

        // ── 前提: ペイントで形状は変わっていない（＝高速パスが成立する条件）──
        assert_eq!(
            mesh_after.positions.len(),
            positions.len(),
            "ペイントで頂点数が変わった（密度を触っている＝高速パスの前提が崩れている）"
        );

        // ── ① パレットが一致すること ──
        assert_eq!(
            palette_fast, palette_full,
            "高速パスとフル再生成でパレットが不一致"
        );
        // このケースはパレット不変（＝フォールバックしない）であることも確認する。
        assert_eq!(
            palette_fast, palette_before,
            "既存レイヤを塗っただけでパレットが変わった（このテストの想定外）"
        );

        // ── ② ペイントが実際に色を変えていること（テストが形骸化していない担保）──
        //   ここが変わらないなら「何も塗れていない」ので、下の一致比較は無意味になる。
        assert!(
            colors_fast
                .iter()
                .zip(colors_before.iter())
                .any(|(a, b)| a != b),
            "ペイント前後で頂点カラーが 1 つも変わっていない（テストが何も守れていない）"
        );

        // ── ③ 頂点カラーが 1 ビット違わず一致すること ──
        assert_eq!(colors_fast.len(), verts_full.len(), "頂点数が不一致");
        for (i, (c, v)) in colors_fast.iter().zip(verts_full.iter()).enumerate() {
            for k in 0..TERRAIN_BLEND_SLOTS {
                assert_eq!(
                    c[k].to_bits(),
                    v.color[k].to_bits(),
                    "頂点 {i} スロット {k} の色がビット不一致 ({} vs {})",
                    c[k],
                    v.color[k]
                );
            }
        }
    }

    /// フォールバック条件（パレット変化）が現実的な経路で実際に発火すること。
    ///
    /// そのチャンクにまだ無いレイヤを強く塗ると、チャンク合計の上位 4 層の顔ぶれが変わり、
    /// 頂点カラー 4 成分の *意味* が変わる。ランタイムはこれを検出してフル再メッシュへ
    /// フォールバックする。検出できなければ「旧パレットで新しい重みを描く」＝誤色になる。
    #[test]
    fn paint_introducing_new_layer_changes_palette() {
        let settings = TerrainSettings::default();
        // 既定の 4 層では「まだ無い 5 番目の層」を作れないので、6 層構成にする。
        let layers = flat_layer_set(TEST_WIDE_LAYER_COUNT);
        let coord = ChunkCoord::new(0, 0, 0);
        let world_origin = coord.world_origin(&settings);

        let mut chunk = sphere_chunk(&settings);
        let mesh_before = generate_standalone(&chunk, &settings);
        let edges = mesh_before.edges.clone();
        let positions = mesh_before.positions.clone();
        let normals = mesh_before.normals.clone();
        let (_c0, palette_before) = compute_layer_colors(
            &positions,
            &normals,
            &mesh_before.paint,
            &mesh_before.paint_amount,
            world_origin,
            &layers,
        );
        assert!(
            !palette_before.contains(&TEST_NEW_LAYER),
            "前提の崩れ: 塗る前からレイヤ {TEST_NEW_LAYER} がパレットに居る"
        );

        // ── まだ使われていないレイヤを、球全体を覆う大きなブラシで強く塗る ──
        let brush = SphereBrush {
            center: SPHERE_CENTER,
            radius: SPHERE_RADIUS * 2.0,
            strength: TEST_PAINT_STRENGTH,
        };
        {
            let mut field = SingleChunkPaintField {
                settings: &settings,
                chunk: &mut chunk,
            };
            let affected = apply_paint(&mut field, &brush, TEST_NEW_LAYER, TEST_BRUSH_DT);
            assert!(
                !affected.is_empty(),
                "ペイントが当たっていない（前提の崩れ）"
            );
        }

        // ── 高速パスの再構築が返すパレットが変わっている＝フォールバック条件が発火する ──
        let (_colors_fast, palette_fast) =
            fast_path_colors(&chunk, &edges, &positions, &normals, world_origin, &layers);
        assert_ne!(
            palette_fast, palette_before,
            "新レイヤを塗ってもパレットが変わらない（フォールバック条件が死んでいる）"
        );
        assert!(
            palette_fast.contains(&TEST_NEW_LAYER),
            "塗った新レイヤ {TEST_NEW_LAYER} がパレットへ入っていない: {palette_fast:?}"
        );
    }

    /// `rebuild_terrain_model_with_colors` が「色だけ差し替えた」モデルを返すこと。
    ///
    /// 位置・法線・インデックス・パレットが元と一致し、色だけが指定どおりに変わることを固定する。
    /// ここが崩れると、高速パスが CPU モデルを壊して以後の再アップロードで形状が化ける。
    #[test]
    fn rebuild_terrain_model_replaces_only_colors() {
        let layers = TerrainLayerSet::default();
        let mesh = painted_mesh(1);
        let (model, palette) = terrain_mesh_to_model(&mesh, "chunk", TEST_ORIGIN, &layers);
        let src = &model.meshes[0].primitives[0];

        // 元と明確に違う色を入れる（差し替えが本当に効いているかを見るため）。
        let new_colors: Vec<[f32; 4]> = (0..src.vertices.len())
            .map(|i| [i as f32, 0.25, 0.5, 0.75])
            .collect();
        let rebuilt = rebuild_terrain_model_with_colors(
            &src.vertices,
            &src.indices,
            "chunk",
            &new_colors,
            palette,
            &layers,
        )
        .expect("長さが一致しているのに None が返った");

        let dst = &rebuilt.meshes[0].primitives[0];
        assert_eq!(dst.indices, src.indices, "インデックスが変わった");
        assert_eq!(
            rebuilt.materials[0].terrain_palette, palette,
            "パレットが変わった"
        );
        for (i, (a, b)) in src.vertices.iter().zip(dst.vertices.iter()).enumerate() {
            assert_eq!(a.position, b.position, "頂点 {i} の位置が変わった");
            assert_eq!(a.normal, b.normal, "頂点 {i} の法線が変わった");
            assert_eq!(b.color, new_colors[i], "頂点 {i} の色が差し替わっていない");
        }

        // 長さ不一致は None（呼び出し側はフル再メッシュへフォールバックする）。
        assert!(
            rebuild_terrain_model_with_colors(
                &src.vertices,
                &src.indices,
                "chunk",
                &new_colors[..1],
                palette,
                &layers,
            )
            .is_none(),
            "長さ不一致を検出できていない"
        );
    }

    /// 地形チャンクのマテリアル平均アルベドが「塗ったレイヤの色」になること。
    ///
    /// これが白（`Material::default()` のまま）だと、水面反射・RT 反射・DDGI が
    /// テクスチャを引けないヒットで白のベタ塗りへ縮退し、**地形が灰色の板として映る**
    /// （本修正の主眼）。`base_color_tex_avg` も同値でなければ、マテリアル
    /// オーバーライドが掛かった瞬間に `eff_avg_albedo` が地形色を失う。
    #[test]
    fn terrain_material_avg_albedo_matches_painted_layer_color() {
        let layers = TerrainLayerSet::default(); // 既定 4 層（grass/dirt/rock/sand）
        for painted in 0..layers.layers.len() as u32 {
            let mesh = painted_mesh(painted);
            let (model, _palette) = terrain_mesh_to_model(&mesh, "chunk", TEST_ORIGIN, &layers);
            let mat = &model.materials[0];
            let expect = layers.layers[painted as usize].base_color; // テクスチャ無しレイヤ

            for ch in 0..3 {
                assert!(
                    (mat.avg_albedo[ch] - expect[ch]).abs() < EPS,
                    "レイヤ {painted}: avg_albedo が塗ったレイヤ色と不一致 \
                     got={:?} want={expect:?}",
                    mat.avg_albedo
                );
                assert!(
                    (mat.base_color_tex_avg[ch] - expect[ch]).abs() < EPS,
                    "レイヤ {painted}: base_color_tex_avg が avg_albedo と食い違う"
                );
            }
            // .a は不透明（色付き影のアルファ源。半透明扱いされると影が消える）。
            assert_eq!(mat.avg_albedo[3], 1.0, "レイヤ {painted}: .a が 1.0 でない");
            // 既定 4 層はいずれも白ではない＝白ベタ塗りへの退行検出。
            assert!(
                mat.avg_albedo[..3].iter().any(|v| *v < 0.9),
                "レイヤ {painted}: 平均アルベドが白に張り付いている"
            );
        }
    }

    /// ペイント高速パス（`rebuild_terrain_model_with_colors`）でも平均アルベドが追従すること。
    ///
    /// 追従しないと「塗り替えた地形の見た目は変わったのに、水面に映る色だけ塗る前のまま」
    /// という形で表面化する。
    #[test]
    fn rebuild_terrain_model_updates_avg_albedo() {
        let layers = TerrainLayerSet::default();
        let mesh = painted_mesh(0);
        let (model, palette) = terrain_mesh_to_model(&mesh, "chunk", TEST_ORIGIN, &layers);
        let src = &model.meshes[0].primitives[0];

        // パレット内で「レイヤ 0 以外のスロット」へ 100% 寄せた色に差し替える。
        // 既定 4 層のパレットは全層を含むので、スロット 1 は必ずレイヤ 0 とは別層になる。
        let target_slot = 1usize;
        let target_layer = palette[target_slot] as usize;
        assert_ne!(target_layer, palette[0] as usize, "テスト前提が崩れている");

        let mut c = [0.0f32; TERRAIN_BLEND_SLOTS];
        c[target_slot] = 1.0;
        let new_colors: Vec<[f32; 4]> = vec![c; src.vertices.len()];

        let rebuilt = rebuild_terrain_model_with_colors(
            &src.vertices,
            &src.indices,
            "chunk",
            &new_colors,
            palette,
            &layers,
        )
        .expect("長さが一致しているのに None が返った");

        let expect = layers.layers[target_layer].base_color;
        let got = rebuilt.materials[0].avg_albedo;
        for ch in 0..3 {
            assert!(
                (got[ch] - expect[ch]).abs() < EPS,
                "ペイント後の平均アルベドが追従していない got={got:?} want={expect:?}"
            );
        }
    }

    /// レイヤ定義が 4 層未満のとき、パレットの重複スロットで重みが二重計上されないこと。
    ///
    /// `select_top_slots` は余りスロットをレイヤ 0 で埋めるため、素直に射影すると
    /// レイヤ 0 が 3 回積算されてブレンド比が歪む（例: 2 層構成）。
    #[test]
    fn duplicate_palette_slots_do_not_double_count() {
        let layers = flat_layer_set(2);
        let layer_count = layers.layers.len();

        // レイヤ 1 を 25%・レイヤ 0 を 75% で塗る（重複計上が起きれば比率が崩れる）。
        let mut paint = BlendSlots::default();
        paint.index = [0, 1, 0, 0];
        paint.weight = [0.75, 0.25, 0.0, 0.0];
        let mesh = TerrainMesh {
            positions: vec![[0.0, 0.0, 0.0]; 3],
            normals: vec![[0.0, 1.0, 0.0]; 3],
            indices: vec![0, 1, 2],
            paint: vec![paint; 3],
            paint_amount: vec![1.0; 3],
            ..Default::default()
        };

        let (model, palette) = terrain_mesh_to_model(&mesh, "chunk", TEST_ORIGIN, &layers);
        let color = model.meshes[0].primitives[0].vertices[0].color;
        let resolved = resolve_layer_weights(color, palette, layer_count);

        // 総和は 1（エネルギー保存）。
        let sum: f32 = resolved.iter().sum();
        assert!(
            (sum - 1.0).abs() < EPS,
            "重みの総和が 1 でない: {resolved:?}"
        );
        // 比率は塗ったとおり（重複計上があればレイヤ 0 が 0.9 前後まで膨らむ）。
        assert!(
            (resolved[0] - 0.75).abs() < EPS,
            "レイヤ 0 の比率が崩れた: {resolved:?}"
        );
        assert!(
            (resolved[1] - 0.25).abs() < EPS,
            "レイヤ 1 の比率が崩れた: {resolved:?}"
        );
    }
}
