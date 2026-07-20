// ============================================================
//  terrain_ops.rs — ボクセル地形ランタイム（terrain ライブラリ ⇄ ECS/GPU 橋渡し）
//
//  【責務】
//    エンジン非依存の terrain ライブラリ（密度グリッド・マーチングキューブス・
//    球ブラシ・.tvox 永続化）を、SEED の ECS（Actor/ModelComponent）と GPU
//    （DrawContext）へ接続する統合層。
//
//    - TerrainState:  地形の実行時状態（設定・全チャンク密度・チャンク→メッシュ
//                     スロット対応・編集ダーティ集合）を App に 1 つ保持する。
//    - FieldView:     terrain::brush::apply が編集するための SampleField 実装。
//                     グローバルサンプル座標 ⇄ チャンク格納の変換と境界重複同期を隠蔽。
//    - handle_terrain_init:        地形ツリー（root/フォルダ/メッシュアクター）を生成し
//                                  初期地面を敷いてメッシュ化・GPU アップロードする。
//    - handle_terrain_brush:       スクリーン座標からレイマーチで着弾点を求め編集する。
//    - handle_terrain_brush_world: ワールド座標中心で球ブラシ編集＋影響チャンク再メッシュ化。
//    - handle_terrain_save:        全チャンクを .tvox としてアセット配下へ書き出す。
//    - rebuild_terrain_after_load: シーンロード後に .tvox からチャンクを復元しメッシュを再生成。
//
//  【密度の規約】density < iso ⇒ SOLID、> iso ⇒ AIR。平坦地面 density(p)=p.y。
// ============================================================

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use crate::engine::ecs::Entity;
use crate::engine::components::{
    ComponentKind, InstanceMeta, ModelComponent, TerrainChunkComponent,
    Transform as ActorTransform, GROUP_ID_BASE, next_batch_instance_id,
};
use crate::engine::core::loader::model::Model;
use crate::engine::methods::drawer::{DrawContext, GpuModel, InstancedModelBatch};
use crate::engine::structs::objects::Actor;
use crate::engine::terrain::{
    self, BrushOp, ChunkCoord, SampleField, SphereBrush, TerrainChunkData, TerrainSettings, tvox,
};

use super::App;
use super::terrain_mesh_build::terrain_mesh_to_model;

// ─── 名前・調整用の名前付き定数（マジックナンバー禁止） ────────────────────────

/// 地形ルートアクターの名前。
const TERRAIN_ROOT_NAME: &str = "terrain";
/// 各チャンクのメッシュを載せるアクターの名前。
const TERRAIN_MESH_NAME: &str = "mesh";
/// メッシュアクターの ModelComponent スロット名。
const TERRAIN_MODEL_SLOT_NAME: &str = "mesh";
/// メッシュアクターの TerrainChunkComponent スロット名。
const TERRAIN_CHUNK_SLOT_NAME: &str = "chunk";

/// クリック 1 回ぶんのブラシ適用時間（離散編集なので 1.0 秒相当）。
const BRUSH_DT: f32 = 1.0;

/// レイマーチのステップ幅（voxel_size に対する割合）。0.5 = 半ボクセルずつ進む。
const RAYMARCH_STEP_FRACTION: f32 = 0.5;
/// レイマーチの最大距離（メートル）。これを超えたら未命中とする。
const RAYMARCH_MAX_DISTANCE: f32 = 500.0;
/// 交差区間を二分探索で詰める反復回数。
const RAYMARCH_BISECT_ITERS: u32 = 8;

/// スモークテスト（SEED_TERRAIN_SMOKE=1）でカメラを引く距離のフットプリント倍率。
const SMOKE_CAM_BACK_RATIO: f32 = 0.75;
/// スモークテストでカメラを上げる高さのフットプリント倍率。
const SMOKE_CAM_UP_RATIO: f32 = 0.75;
/// スモークテストのデバッグカメラ FOV（度）。
const SMOKE_CAM_FOV_DEG: f32 = 55.0;
/// スモークテストのデバッグカメラ far clip（メートル）。
const SMOKE_CAM_FAR: f32 = 2000.0;
/// スモークテストのデバッグカメラ移動速度。
const SMOKE_CAM_SPEED: f32 = 20.0;
/// スモークテストのブラシ半径（メートル）。
const SMOKE_BRUSH_RADIUS: f32 = 6.0;
/// スモークテストのブラシ強度。
const SMOKE_BRUSH_STRENGTH: f32 = 8.0;
/// スモークテストで盛り／掘りの中心を footprint 中心から左右へずらす量（メートル）。
const SMOKE_BRUSH_OFFSET: f32 = 8.0;
/// スモークの連続ストローク（畝）の適用回数（線を引くように点を並べる）。
const SMOKE_STROKE_STEPS: u32 = 10;
/// スモークの連続ストロークで 1 ステップあたり進む距離（メートル）。
const SMOKE_STROKE_SPACING: f32 = 2.0;
/// スモークのプレビュー球（ワイヤスフィア）半径（メートル）。
const SMOKE_PREVIEW_RADIUS: f32 = 5.0;

// ============================================================
//  TerrainState — 地形の実行時状態
// ============================================================

/// ボクセル地形の実行時状態。App に 1 つ保持する。
pub struct TerrainState {
    /// 地形の調整設定（voxel_size / chunk_cells / iso / density_clamp 等）。
    pub settings: TerrainSettings,
    /// 全チャンクの密度グリッド（キー = チャンク格子座標）。
    pub chunks: HashMap<ChunkCoord, TerrainChunkData>,
    /// チャンク → そのメッシュを載せる ModelComponent スロットの entity。
    /// 再メッシュ化（GPU 差し替え）時に対象コンポーネントを引くために使う。
    pub chunk_slot_entity: HashMap<ChunkCoord, Entity>,
    /// 現在の地形が属するシーン名（.tvox の保存フォルダ・合成 source_path に使う）。
    pub scene_name: String,
    /// 編集されて未保存のチャンク集合（handle_terrain_save でクリア）。
    pub dirty: HashSet<ChunkCoord>,
    /// ブラシプレビュー（Edit モードのホバー位置に描くワイヤスフィア）の
    /// (ワールド中心, 半径)。`None` のとき非表示。frame_renderer が描画に使う。
    /// レイがヒットしない（空を指す）フレームは `None` へクリアされる。
    pub brush_preview: Option<([f32; 3], f32)>,
}

impl Default for TerrainState {
    fn default() -> Self {
        Self {
            settings: TerrainSettings::default(),
            chunks: HashMap::new(),
            chunk_slot_entity: HashMap::new(),
            scene_name: String::new(),
            dirty: HashSet::new(),
            brush_preview: None,
        }
    }
}

// ============================================================
//  グローバルサンプル座標 ⇄ チャンク格納の変換ヘルパー
// ============================================================

/// 指定軸のグローバルサンプル座標 `g` を所有する (チャンクインデックス, ローカルインデックス) を返す。
///
/// 主となるチャンクは `g.div_euclid(cells)`・ローカル `g.rem_euclid(cells)`。
/// 境界サンプル（rem==0）は 1 つ手前のチャンクがローカル末尾（=cells）として重複所有する。
/// 戻り値は `([primary, boundary], count)`。count=1（内部）または 2（境界）。
#[inline]
fn axis_owners(g: i32, cells: i32) -> ([(i32, usize); 2], usize) {
    let primary_c = g.div_euclid(cells);
    let primary_l = g.rem_euclid(cells);
    let mut out = [(primary_c, primary_l as usize), (0, 0)];
    if primary_l == 0 {
        // 境界サンプル: 1 つ手前のチャンクの末尾サンプル（ローカル cells）としても存在する。
        out[1] = (primary_c - 1, cells as usize);
        (out, 2)
    } else {
        (out, 1)
    }
}

/// グローバルサンプル座標の密度を読む（terrain ライブラリと同じ所有規約）。
///
/// 主チャンクが存在すればそれを、無ければ境界重複する近傍チャンクを試す。
/// どのチャンクも存在しない（地形外）場合は `clamp`（＝AIR 側）を返す。
fn read_global_impl(
    chunks: &HashMap<ChunkCoord, TerrainChunkData>,
    cells: i32,
    clamp: f32,
    gx: i32,
    gy: i32,
    gz: i32,
) -> f32 {
    let (ox, nx) = axis_owners(gx, cells);
    let (oy, ny) = axis_owners(gy, cells);
    let (oz, nz) = axis_owners(gz, cells);
    // primary 組み合わせ（[0][0][0]）を最初に試すため、そのままの順で走査する。
    for i in 0..nx {
        for j in 0..ny {
            for k in 0..nz {
                let coord = ChunkCoord::new(ox[i].0, oy[j].0, oz[k].0);
                if let Some(chunk) = chunks.get(&coord) {
                    return chunk.sample(ox[i].1, oy[j].1, oz[k].1);
                }
            }
        }
    }
    // 地形外 = AIR（clamp は density_clamp = 正の大きな値）。
    clamp
}

/// グローバルサンプル座標へ密度を書く。境界で重複する全チャンクへ同一値を書き込む（同期）。
/// 存在しないチャンクはスキップする。
fn write_global_impl(
    chunks: &mut HashMap<ChunkCoord, TerrainChunkData>,
    cells: i32,
    gx: i32,
    gy: i32,
    gz: i32,
    v: f32,
) {
    let (ox, nx) = axis_owners(gx, cells);
    let (oy, ny) = axis_owners(gy, cells);
    let (oz, nz) = axis_owners(gz, cells);
    for i in 0..nx {
        for j in 0..ny {
            for k in 0..nz {
                let coord = ChunkCoord::new(ox[i].0, oy[j].0, oz[k].0);
                if let Some(chunk) = chunks.get_mut(&coord) {
                    chunk.set_sample(ox[i].1, oy[j].1, oz[k].1, v);
                }
            }
        }
    }
}

/// ワールド座標 `p` の密度をトライリニア補間で求める（レイマーチ用）。
fn sample_density_world(
    chunks: &HashMap<ChunkCoord, TerrainChunkData>,
    settings: &TerrainSettings,
    p: [f32; 3],
) -> f32 {
    let cells = settings.chunk_cells as i32;
    let clamp = settings.density_clamp;
    // world = g * voxel_size より g = world / voxel_size（連続サンプル座標）。
    let inv = 1.0 / settings.voxel_size;
    let fx = p[0] * inv;
    let fy = p[1] * inv;
    let fz = p[2] * inv;
    let x0 = fx.floor();
    let y0 = fy.floor();
    let z0 = fz.floor();
    let tx = fx - x0;
    let ty = fy - y0;
    let tz = fz - z0;
    let ix = x0 as i32;
    let iy = y0 as i32;
    let iz = z0 as i32;
    let r = |dx: i32, dy: i32, dz: i32| read_global_impl(chunks, cells, clamp, ix + dx, iy + dy, iz + dz);
    // 8 コーナー → x → y → z の順で線形補間。
    let c000 = r(0, 0, 0);
    let c100 = r(1, 0, 0);
    let c010 = r(0, 1, 0);
    let c110 = r(1, 1, 0);
    let c001 = r(0, 0, 1);
    let c101 = r(1, 0, 1);
    let c011 = r(0, 1, 1);
    let c111 = r(1, 1, 1);
    let c00 = c000 + (c100 - c000) * tx;
    let c10 = c010 + (c110 - c010) * tx;
    let c01 = c001 + (c101 - c001) * tx;
    let c11 = c011 + (c111 - c011) * tx;
    let c0 = c00 + (c10 - c00) * ty;
    let c1 = c01 + (c11 - c01) * ty;
    c0 + (c1 - c0) * tz
}

// ============================================================
//  FieldView — terrain::brush::apply が編集する SampleField 実装
// ============================================================

/// ブラシ編集用の SampleField ラッパー。TerrainState の設定とチャンク集合を分割借用で束ねる。
struct FieldView<'a> {
    settings: &'a TerrainSettings,
    chunks: &'a mut HashMap<ChunkCoord, TerrainChunkData>,
}

impl<'a> SampleField for FieldView<'a> {
    fn settings(&self) -> &TerrainSettings {
        self.settings
    }

    fn read_global(&self, gx: i32, gy: i32, gz: i32) -> f32 {
        let cells = self.settings.chunk_cells as i32;
        read_global_impl(self.chunks, cells, self.settings.density_clamp, gx, gy, gz)
    }

    fn write_global(&mut self, gx: i32, gy: i32, gz: i32, v: f32) {
        let cells = self.settings.chunk_cells as i32;
        write_global_impl(self.chunks, cells, gx, gy, gz, v);
    }

    fn world_of_global(&self, gx: i32, gy: i32, gz: i32) -> [f32; 3] {
        let vs = self.settings.voxel_size;
        [gx as f32 * vs, gy as f32 * vs, gz as f32 * vs]
    }
}

// ============================================================
//  純粋ヘルパー（App 非依存）
// ============================================================

/// チャンクの合成 source_path（`terrain://<scene>/chunk_X_Y_Z`）を返す。
fn terrain_source_path(scene: &str, coord: ChunkCoord) -> String {
    format!(
        "{}{}/chunk_{}_{}_{}",
        crate::engine::components::TERRAIN_SOURCE_SCHEME,
        scene, coord.x, coord.y, coord.z
    )
}

/// チャンクの .tvox 仮想パス（`assets://terrain/<scene>/chunk_X_Y_Z.tvox`）を返す。
fn tvox_virtual_path(scene: &str, coord: ChunkCoord) -> String {
    format!(
        "{}terrain/{}/chunk_{}_{}_{}.tvox",
        crate::engine::asset_fs::ASSETS_SCHEME,
        scene, coord.x, coord.y, coord.z
    )
}

/// チャンクの .tvox ファイル名（`chunk_X_Y_Z.tvox`）を返す。
fn tvox_file_name(coord: ChunkCoord) -> String {
    format!("chunk_{}_{}_{}.tvox", coord.x, coord.y, coord.z)
}

/// 1 チャンクをメッシュ化して GPU アップロードし、(CPU モデル, GpuModel?, インスタンスバッチ?) を返す。
///
/// 継ぎ目の勾配（法線）を隣接チャンクと連続させるため、`generate` の neighbor_sampler で
/// グローバル密度場を読む（チャンク境界の外側 1 サンプルも正しい値を返す）。
///
/// 【空メッシュ対策】全 AIR / 全 SOLID のチャンクは表面三角形が 0 個になる。
/// この場合に GPU アップロードすると「サイズ 0 の頂点/インデックスバッファ」が作られ、
/// RT の BLAS 構築やドロー時の `buffer.slice(..)` で「offset 0 out of range for buffer of size 0」
/// パニックになる。よって空メッシュのときは GPU リソースを一切作らず `None` を返す
/// （呼び出し側は gpu_model=None のまま＝非描画・非 RT キャスタとして扱う。merge_map が
///  gpu_model.is_none() をスキップするため、スロットは保持したまま安全に非表示にできる）。
/// 掘削で後から表面が現れたチャンクは、再メッシュ時に改めてアップロードされる。
fn build_chunk_render(
    chunks: &HashMap<ChunkCoord, TerrainChunkData>,
    settings: &TerrainSettings,
    ctx: &DrawContext,
    coord: ChunkCoord,
) -> Option<(Arc<Model>, Option<GpuModel>, Option<InstancedModelBatch>)> {
    let chunk = chunks.get(&coord)?;
    let cells = settings.chunk_cells as i32;
    let clamp = settings.density_clamp;
    // このチャンクのローカルサンプル (lx,ly,lz) → グローバルサンプル座標 = coord*cells + local。
    let base = [coord.x * cells, coord.y * cells, coord.z * cells];
    let mesh = terrain::generate(chunk, settings, |lx, ly, lz| {
        read_global_impl(chunks, cells, clamp, base[0] + lx, base[1] + ly, base[2] + lz)
    });
    let model = terrain_mesh_to_model(
        &mesh,
        &format!("terrain_{}_{}_{}", coord.x, coord.y, coord.z),
    );
    // 空メッシュ（三角形 0）は GPU リソースを作らない（サイズ 0 バッファ由来のパニック回避）。
    if mesh.indices.is_empty() {
        return Some((Arc::new(model), None, None));
    }
    // オーバーライド無しでアップロード（source_path とビット一致のバッチキーになる）。
    let gpu = ctx.upload_model_with_overrides(&model, &[]);
    let batch = ctx.create_instanced_batch(&model, 1);
    Some((Arc::new(model), Some(gpu), Some(batch)))
}

/// 地形チャンク用の ModelComponent を組み立てる。
///
/// instance_mats[0] はメッシュアクターのワールド行列（＝チャンク原点への平行移動）。
/// メッシュ頂点はチャンクローカル座標なので、この行列でワールドへ配置される。
fn make_terrain_model_component(
    source_path: String,
    model: Arc<Model>,
    gpu: Option<GpuModel>,
    batch: Option<InstancedModelBatch>,
    world_mat: [[f32; 4]; 4],
) -> ModelComponent {
    ModelComponent {
        source_path,
        model: Some(model),
        // 空メッシュチャンクは gpu/batch=None（非描画）。掘削で表面が出たら再メッシュで埋まる。
        gpu_model: gpu,
        instanced_batch: batch,
        instance_mats: vec![world_mat],
        instance_meta: vec![InstanceMeta::new("chunk")],
        group_meta: Vec::new(),
        next_group_id: GROUP_ID_BASE,
        anim_drive: None,
        // 不透明 + 影キャストで RT 影・反射の対象になる。
        cast_shadows: true,
        material_overrides: Vec::new(),
        batch_instance_id: next_batch_instance_id(),
    }
}

/// アクターとその全子孫が保持する World エンティティ（本体＋スロット専用）を再帰収集する。
///
/// 既存の terrain ルートを再初期化前に despawn するために使う。`collect_entities_for_wl`
/// は world_line 単位でしか収集できず「terrain ルートのサブツリーだけ」を抜けないため、
/// 単一アクター起点の専用収集をここに置く（マジックナンバー・外部依存なし）。
fn collect_subtree_entities(actor: &Actor, out: &mut Vec<Entity>) {
    out.push(actor.entity);
    // スロット専用エンティティ（ModelComponent / TerrainChunkComponent など）も despawn 対象。
    for slot in actor.slots() {
        out.push(slot.entity);
    }
    for child in actor.children() {
        collect_subtree_entities(child, out);
    }
}

/// このチャンク範囲の全チャンク座標を列挙する（settings のグラウンド範囲に従う）。
fn ground_chunk_coords(settings: &TerrainSettings) -> Vec<ChunkCoord> {
    let mut coords = Vec::new();
    for x in 0..settings.ground_chunks_x as i32 {
        for z in 0..settings.ground_chunks_z as i32 {
            for y in settings.ground_chunk_y_min..=settings.ground_chunk_y_max {
                coords.push(ChunkCoord::new(x, y, z));
            }
        }
    }
    coords
}

// ============================================================
//  App メソッド（IPC ハンドラ・ライフサイクル）
// ============================================================

impl App {
    /// 地形を初期化する。地形ツリーを生成し、初期地面を敷いてメッシュ化・GPU アップロードする。
    ///
    /// TERRAIN_INIT コマンド・スモークフックから呼ばれる。
    pub(super) fn handle_terrain_init(&mut self) {
        if self.draw_ctx.is_none() {
            return;
        }
        // シーンが無ければ空シーンを作る（スモーク単独起動・地形専用編集を許容する）。
        if self.scene.is_none() {
            self.scene = Some(crate::engine::core::app_base::scene::Scene::new("terrain"));
        }

        // ── 冪等化: 既存の terrain ルートを除去してから作り直す（二重生成防止）──
        //   handle_terrain_init は毎回新しい terrain ルートを scene.actors へ push するため、
        //   除去しないと TERRAIN_INIT を 2 回叩くとヒエラルキーに terrain ルートが重複し、
        //   古いチャンクアクター群がシーンに残って保存もされてしまう（オーファン）。
        //   同名（TERRAIN_ROOT_NAME）のトップレベルルートとそのサブツリーの全エンティティを
        //   despawn してから作り直すことで、再初期化・ロード後の再初期化でも重複を生じさせない。
        if let Some(scene) = self.scene.as_mut() {
            let mut to_despawn: Vec<Entity> = Vec::new();
            scene.actors.retain(|a| {
                if a.name == TERRAIN_ROOT_NAME {
                    collect_subtree_entities(a, &mut to_despawn);
                    false // 除去する
                } else {
                    true
                }
            });
            for e in to_despawn {
                scene.world.despawn(e);
            }
        }

        // 状態をリセットしてシーン名を取り込む。
        self.terrain = TerrainState::default();
        let scene_name = self.scene.as_ref().map(|s| s.name.clone()).unwrap_or_default();
        self.terrain.scene_name = scene_name.clone();
        let settings = self.terrain.settings.clone();

        // ── フェーズ 1: 全チャンクの初期地面密度を敷き詰める ──
        // 先に全チャンクを map へ入れておくことで、後段のメッシュ化で境界の
        // 隣接サンプル（neighbor_sampler）が正しい値を返す。
        let coords = ground_chunk_coords(&settings);
        for &coord in &coords {
            let data = TerrainChunkData::from_ground_plane(&settings, coord);
            self.terrain.chunks.insert(coord, data);
        }

        // ── フェーズ 2: 各チャンクをメッシュ化して GPU アップロード（描画リソースを先に作る）──
        //   self.terrain.chunks（不変）と self.draw_ctx（不変）を同時借用する（別フィールドなので可）。
        let mut prebuilt: Vec<(ChunkCoord, Arc<Model>, Option<GpuModel>, Option<InstancedModelBatch>)> = Vec::new();
        {
            let ctx = self.draw_ctx.as_ref().unwrap();
            for &coord in &coords {
                // 空メッシュチャンクも Some(model, None, None) で返るため、全チャンクが
                // アクター＋MC スロットを得る（掘削で後から表面が出ても差し替えられる）。
                if let Some((model, gpu, batch)) = build_chunk_render(&self.terrain.chunks, &settings, ctx, coord) {
                    prebuilt.push((coord, model, gpu, batch));
                }
            }
        }

        // ── フェーズ 3: アクターツリー（root/フォルダ/メッシュ）を構築してコンポーネントを挿入 ──
        //   self.terrain への書き込みは借用衝突を避けるためローカルへ退避してから反映する。
        let mut slot_map: Vec<(ChunkCoord, Entity)> = Vec::new();
        {
            let scene = self.scene.as_mut().unwrap();
            // 地形ルートはフォルダノード（Transform 非保持・透過）で作る。
            // 子（チャンク・メッシュ）のワールド変換に一切影響しない整理専用ノード。
            let root_entity = scene.world.spawn();
            let mut root_actor = Actor::new_folder(root_entity, TERRAIN_ROOT_NAME);

            for (coord, model, gpu, batch) in prebuilt {
                // チャンクフォルダノード（描画なし・整理用・Transform 非保持）。
                let folder_entity = scene.world.spawn();
                let mut folder = Actor::new_folder(
                    folder_entity,
                    format!("chunk_{}_{}_{}", coord.x, coord.y, coord.z),
                );

                // メッシュアクター（チャンク原点に配置）。
                let mesh_entity = scene.world.spawn();
                let origin = coord.world_origin(&settings);
                let mesh_tf = ActorTransform {
                    position: origin,
                    rotation: [0.0, 0.0, 0.0],
                    scale: [1.0, 1.0, 1.0],
                };
                let world_mat = mesh_tf.to_mat4();
                scene.world.insert(mesh_entity, mesh_tf);
                let mut mesh_actor = Actor::new(mesh_entity, TERRAIN_MESH_NAME);

                // ModelComponent スロット（合成 source_path で描画＋RT キャスタ化）。
                let mc_slot = scene.world.spawn();
                let source_path = terrain_source_path(&scene_name, coord);
                scene.world.insert(
                    mc_slot,
                    make_terrain_model_component(source_path, model, gpu, batch, world_mat),
                );
                mesh_actor.add_slot_typed::<ModelComponent>(
                    TERRAIN_MODEL_SLOT_NAME, ComponentKind::Model, mc_slot,
                );

                // TerrainChunkComponent スロット（座標＋.tvox リンク・ロード時復元の手掛かり）。
                let tc_slot = scene.world.spawn();
                scene.world.insert(
                    tc_slot,
                    TerrainChunkComponent {
                        chunk_x: coord.x,
                        chunk_y: coord.y,
                        chunk_z: coord.z,
                        tvox_path: tvox_virtual_path(&scene_name, coord),
                    },
                );
                mesh_actor.add_slot_typed::<TerrainChunkComponent>(
                    TERRAIN_CHUNK_SLOT_NAME, ComponentKind::TerrainChunk, tc_slot,
                );

                slot_map.push((coord, mc_slot));
                folder.add_child(mesh_actor);
                root_actor.add_child(folder);
            }

            scene.actors.push(root_actor);
        }

        // チャンク → メッシュスロット対応を反映する。
        for (coord, entity) in slot_map {
            self.terrain.chunk_slot_entity.insert(coord, entity);
        }

        self.send_hierarchy();
        if let Some(ipc) = &self.ipc {
            ipc.send("TERRAIN_INIT_OK");
        }
    }

    /// スクリーン座標からレイマーチで地形表面を求め、その着弾点で球ブラシを適用する。
    ///
    /// TERRAIN_BRUSH コマンドから呼ばれる（op は BrushOp を u32 化した値）。
    pub(super) fn handle_terrain_brush(
        &mut self,
        op: BrushOp,
        screen_x: f32,
        screen_y: f32,
        radius: f32,
        strength: f32,
    ) {
        // 地形未初期化なら何もしない。
        if self.terrain.chunks.is_empty() {
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_BRUSH_MISS");
            }
            return;
        }

        let Some(center) = self.terrain_raymarch_hit(screen_x, screen_y) else {
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_BRUSH_MISS");
            }
            return;
        };

        self.handle_terrain_brush_world(op, center, radius, strength);
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("TERRAIN_BRUSH_OK:{},{},{}", center[0], center[1], center[2]));
        }
    }

    /// スクリーン座標からカメラレイを作り、密度場を SDF レイマーチして最初の
    /// AIR→SOLID 交差（地形表面）のワールド座標を返す。命中無しは `None`。
    ///
    /// ブラシ着弾点（handle_terrain_brush）とブラシプレビュー（handle_terrain_brush_preview）
    /// の双方から使う共通処理。地形未初期化・ウィンドウ無しでは `None`。
    pub(super) fn terrain_raymarch_hit(&self, screen_x: f32, screen_y: f32) -> Option<[f32; 3]> {
        if self.terrain.chunks.is_empty() {
            return None;
        }
        // ビューポートサイズを取得してレイを生成する（デバッグカメラの投影方式に追従）。
        let (vp_w, vp_h) = {
            let w = self.window.as_ref()?;
            let sz = w.inner_size();
            (sz.width.max(1) as f32, sz.height.max(1) as f32)
        };
        let (origin, dir) = self.editor_3d_ray(screen_x, screen_y, vp_w, vp_h);

        // ── レイマーチ：密度場の符号変化（AIR→SOLID）を検出して着弾点を求める ──
        let settings = self.terrain.settings.clone();
        let iso = settings.iso_level;
        let step = (settings.voxel_size * RAYMARCH_STEP_FRACTION).max(f32::EPSILON);
        let at = |t: f32| {
            [
                origin[0] + dir[0] * t,
                origin[1] + dir[1] * t,
                origin[2] + dir[2] * t,
            ]
        };
        let density_at = |t: f32| sample_density_world(&self.terrain.chunks, &settings, at(t));

        let mut prev_t = 0.0f32;
        let mut prev_d = density_at(prev_t);
        let mut t = step;
        while t <= RAYMARCH_MAX_DISTANCE {
            let d = density_at(t);
            // AIR（>=iso）→ SOLID（<iso）の交差を検出する。
            if prev_d >= iso && d < iso {
                // 区間 [prev_t, t] を二分探索で詰める。
                let (mut lo, mut hi) = (prev_t, t);
                for _ in 0..RAYMARCH_BISECT_ITERS {
                    let mid = 0.5 * (lo + hi);
                    if density_at(mid) < iso {
                        hi = mid;
                    } else {
                        lo = mid;
                    }
                }
                return Some(at(0.5 * (lo + hi)));
            }
            prev_t = t;
            prev_d = d;
            t += step;
        }
        None
    }

    /// ブラシプレビュー（ホバー位置のワイヤスフィア）の中心を更新する。
    ///
    /// TERRAIN_BRUSH_PREVIEW コマンドから呼ばれる。カーソル位置のレイが地形に
    /// 当たれば `terrain.brush_preview` に (着弾点, 半径) をセットし、当たらなければ
    /// `None`（非表示）にする。押下していないホバー中に高頻度で呼ばれるため IPC 応答は返さない。
    pub(super) fn handle_terrain_brush_preview(&mut self, screen_x: f32, screen_y: f32, radius: f32) {
        self.terrain.brush_preview = self
            .terrain_raymarch_hit(screen_x, screen_y)
            .map(|center| (center, radius));
    }

    /// ブラシプレビューを非表示にする（TERRAIN_BRUSH_PREVIEW_OFF・terrain モード離脱時）。
    pub(super) fn handle_terrain_brush_preview_off(&mut self) {
        self.terrain.brush_preview = None;
    }

    /// ワールド座標中心で球ブラシを適用し、影響を受けたチャンクを再メッシュ化する。
    ///
    /// レイキャスト（handle_terrain_brush）とスモークフックの双方から呼ばれる共通経路。
    pub(super) fn handle_terrain_brush_world(
        &mut self,
        op: BrushOp,
        center: [f32; 3],
        radius: f32,
        strength: f32,
    ) {
        if self.draw_ctx.is_none() || self.terrain.chunks.is_empty() {
            return;
        }
        let settings = self.terrain.settings.clone();
        let brush = SphereBrush { center, radius, strength };

        // ── 球ブラシを密度場へ適用（settings と chunks を分割借用して FieldView を作る）──
        let affected: Vec<ChunkCoord> = {
            let terrain = &mut self.terrain;
            let mut view = FieldView {
                settings: &terrain.settings,
                chunks: &mut terrain.chunks,
            };
            terrain::brush::apply(&mut view, &brush, op, BRUSH_DT)
        };
        if affected.is_empty() {
            return;
        }

        // ── 影響チャンクを再メッシュ化して GPU リソースを作り直す（描画リソースを先に生成）──
        let mut prebuilt: Vec<(ChunkCoord, Arc<Model>, Option<GpuModel>, Option<InstancedModelBatch>)> = Vec::new();
        {
            let ctx = self.draw_ctx.as_ref().unwrap();
            for &coord in &affected {
                // 編集後に空メッシュ化したチャンクは gpu/batch=None で返り、下で非描画に差し替わる。
                if let Some((model, gpu, batch)) = build_chunk_render(&self.terrain.chunks, &settings, ctx, coord) {
                    prebuilt.push((coord, model, gpu, batch));
                }
            }
        }

        // ── VRAM 安全な差し替え（slot_ops::handle_set_material_override と同じ手順）──
        //   旧 GpuModel を先に drop → device.poll(Wait) で解放を確定 → 新規を書き戻す。
        //   これをやらないと「旧解放前に新テクスチャ確保」で瞬間 VRAM 2 倍需要 → OOM になる。
        for (coord, model, gpu, batch) in prebuilt {
            let slot_entity = match self.terrain.chunk_slot_entity.get(&coord) {
                Some(&e) => e,
                None => continue,
            };
            // (1) 旧 GpuModel を drop（このチャンク専有のため他描画に影響なし）。
            if let Some(scene) = self.scene.as_mut() {
                if let Some(mc) = scene.world.get_mut::<ModelComponent>(slot_entity) {
                    mc.gpu_model = None;
                }
            }
            // (2) 遅延破棄を今ここで確定させる（GPU アイドル待ち）。wgpu 25 の poll API。
            if let Some(ctx) = self.draw_ctx.as_ref() {
                let _ = ctx.device.poll(wgpu::PollType::Wait);
            }
            // (3) 新 GpuModel / バッチ / CPU モデルを書き戻す（空メッシュなら None＝非描画）。
            if let Some(scene) = self.scene.as_mut() {
                if let Some(mc) = scene.world.get_mut::<ModelComponent>(slot_entity) {
                    mc.model = Some(model);
                    mc.gpu_model = gpu;
                    mc.instanced_batch = batch;
                    // (4) バッチ更新をマークする。
                    mc.mark_batch_dirty();
                }
            }
            self.terrain.dirty.insert(coord);
        }
    }

    /// 全チャンクを .tvox としてアセット配下（terrain/<scene>/）へ書き出す。
    ///
    /// TERRAIN_SAVE コマンドから呼ばれる。編集有無に関わらず全チャンクを保存し、
    /// ロード時に全チャンクが確実に復元できるようにする。保存後にダーティ集合をクリアする。
    pub(super) fn handle_terrain_save(&mut self) {
        let settings = self.terrain.settings.clone();
        let scene = self.terrain.scene_name.clone();

        // アセットルート直下の terrain/<scene>/ を保存先にする（asset_fs には書き込み API が
        // 無いため std::fs を直接使う。scene.rs の save と同じ流儀）。
        let Some(root) = crate::engine::asset_fs::root() else {
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_SAVE_ERROR:assets root unresolved");
            }
            return;
        };
        let dir = root.join("terrain").join(&scene);
        if let Err(e) = std::fs::create_dir_all(&dir) {
            if let Some(ipc) = &self.ipc {
                ipc.send(&format!("TERRAIN_SAVE_ERROR:{e}"));
            }
            return;
        }

        let mut count = 0u32;
        for (&coord, chunk) in &self.terrain.chunks {
            let bytes = tvox::write_chunk(chunk, coord, &settings);
            let path = dir.join(tvox_file_name(coord));
            match std::fs::write(&path, &bytes) {
                Ok(()) => count += 1,
                Err(e) => eprintln!("[SEED terrain] save failed: {path:?} err={e}"),
            }
        }
        self.terrain.dirty.clear();

        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("TERRAIN_SAVE_OK:{count}"));
        }
    }

    /// シーンロード後に、TerrainChunkComponent を持つアクターの .tvox を読み戻して
    /// 密度チャンクを復元し、各メッシュ（ModelComponent）を再構築する。
    ///
    /// LoadScene ハンドラ・load_play_scene の末尾から呼ぶ。.tvox が欠落していれば
    /// ログを出してスキップする（ロード全体は失敗させない）。
    pub(super) fn rebuild_terrain_after_load(&mut self) {
        if self.draw_ctx.is_none() {
            return;
        }
        // 地形状態をリセットしてシーン名を取り込む。
        self.terrain = TerrainState::default();
        let scene_name = match self.scene.as_ref() { Some(s) => s.name.clone(), None => return };
        self.terrain.scene_name = scene_name;

        // ── 旧シーン（アクター親子版 terrain）→ フォルダ版への移行 ──
        //   本機能導入前に保存された .scene では terrain ルート・チャンク器が
        //   「Transform を持つ通常アクター」として保存されている。ロード時にこれらを
        //   フォルダノード（is_folder=true・Transform 非保持）へ作り直し、以後の保存で
        //   フォルダ版へ移行させる。メッシュアクター（Model/TerrainChunk スロット持ち）は
        //   そのまま残す。対象は「name==TERRAIN_ROOT_NAME のトップレベルアクター」と
        //   「その直下のコンポーネント無しの器アクター（chunk_X_Y_Z）」のみ。
        //   既にフォルダ化済み（新規保存）のシーンでは何もしない（冪等）。
        {
            let scene = self.scene.as_mut().unwrap();
            let mut strip_tf: Vec<Entity> = Vec::new();
            for root in scene.actors.iter_mut() {
                if root.name != TERRAIN_ROOT_NAME {
                    continue;
                }
                if !root.is_folder {
                    root.is_folder = true;
                    strip_tf.push(root.entity);
                }
                // 直下の器（コンポーネント＝スロットを持たないアクター）だけをフォルダ化する。
                for child in root.children_mut().iter_mut() {
                    if child.slots().is_empty() && !child.is_folder {
                        child.is_folder = true;
                        strip_tf.push(child.entity);
                    }
                }
            }
            // フォルダ化したノードから Transform を取り除く（透過ノードの不変条件を回復）。
            for e in strip_tf {
                scene.world.remove::<ActorTransform>(e);
            }
        }

        // ── 走査: TerrainChunkComponent と同一アクター上の ModelComponent スロットを対にして集める ──
        // (チャンク座標, .tvox パス, メッシュ ModelComponent スロット entity)
        let mut found: Vec<(ChunkCoord, String, Entity)> = Vec::new();
        {
            let scene = self.scene.as_ref().unwrap();
            fn walk(
                actor: &Actor,
                world: &crate::engine::ecs::World,
                out: &mut Vec<(ChunkCoord, String, Entity)>,
            ) {
                // このアクターの TerrainChunk スロットと Model スロットを探す。
                let mut tc_info: Option<(ChunkCoord, String)> = None;
                let mut mc_slot: Option<Entity> = None;
                for slot in actor.slots() {
                    match slot.kind {
                        ComponentKind::TerrainChunk => {
                            if let Some(tc) = world.get::<TerrainChunkComponent>(slot.entity) {
                                tc_info = Some((
                                    ChunkCoord::new(tc.chunk_x, tc.chunk_y, tc.chunk_z),
                                    tc.tvox_path.clone(),
                                ));
                            }
                        }
                        ComponentKind::Model => {
                            if mc_slot.is_none() {
                                mc_slot = Some(slot.entity);
                            }
                        }
                        _ => {}
                    }
                }
                if let (Some((coord, path)), Some(mc)) = (tc_info, mc_slot) {
                    out.push((coord, path, mc));
                }
                for child in actor.children() {
                    walk(child, world, out);
                }
            }
            for actor in &scene.actors {
                walk(actor, &scene.world, &mut found);
            }
        }
        if found.is_empty() {
            return;
        }

        // ── フェーズ 1: 全チャンクの .tvox を読み込んで map へ入れる（欠落はスキップ）──
        let mut loaded: Vec<(ChunkCoord, Entity)> = Vec::new();
        for (coord, path, mc_slot) in &found {
            let bytes = match crate::engine::asset_fs::read_bytes(path) {
                Ok(b) => b,
                Err(e) => {
                    eprintln!("[SEED terrain] tvox missing, skip: {path} err={e}");
                    continue;
                }
            };
            match tvox::read_chunk(&bytes) {
                Ok((chunk, _stored_coord)) => {
                    self.terrain.chunks.insert(*coord, chunk);
                    self.terrain.chunk_slot_entity.insert(*coord, *mc_slot);
                    loaded.push((*coord, *mc_slot));
                }
                Err(e) => {
                    eprintln!("[SEED terrain] tvox decode failed, skip: {path} err={e:?}");
                }
            }
        }
        if loaded.is_empty() {
            return;
        }

        // ── フェーズ 2: 全チャンク読込後にメッシュ化（隣接読みが揃った状態で継ぎ目を正しく作る）──
        let settings = self.terrain.settings.clone();
        let mut prebuilt: Vec<(Entity, Arc<Model>, Option<GpuModel>, Option<InstancedModelBatch>)> = Vec::new();
        {
            let ctx = self.draw_ctx.as_ref().unwrap();
            for (coord, mc_slot) in &loaded {
                // 空メッシュチャンクは gpu/batch=None で返る（非描画のまま MC を埋める）。
                if let Some((model, gpu, batch)) = build_chunk_render(&self.terrain.chunks, &settings, ctx, *coord) {
                    prebuilt.push((*mc_slot, model, gpu, batch));
                }
            }
        }

        // ── フェーズ 3: ロード時に model=None で作られた ModelComponent を埋める ──
        if let Some(scene) = self.scene.as_mut() {
            for (mc_slot, model, gpu, batch) in prebuilt {
                if let Some(mc) = scene.world.get_mut::<ModelComponent>(mc_slot) {
                    mc.model = Some(model);
                    mc.gpu_model = gpu;
                    mc.instanced_batch = batch;
                    if mc.instance_mats.is_empty() {
                        // 念のため（通常はロード時に保存済みワールド行列が入っている）。
                        mc.instance_mats.push(ActorTransform::default().to_mat4());
                        mc.instance_meta.push(InstanceMeta::new("chunk"));
                    }
                    mc.mark_batch_dirty();
                }
            }
        }
    }

    /// スモークテスト（環境変数 SEED_TERRAIN_SMOKE=1）専用の常設デバッグフック。
    ///
    /// 地形を初期化し、デバッグカメラを地形フットプリント全体が見える位置へ向け、
    /// 明確に地形を変形させる（盛り 1・掘り 1）。通常の Play/Edit では呼ばれない。
    pub(super) fn run_terrain_smoke(&mut self) {
        // 地形を生成する（シーンが無ければ handle_terrain_init が空シーンを作る）。
        self.handle_terrain_init();

        // ── デバッグカメラをフットプリント全体が見える位置へ向ける ──
        //   フットプリント（ワールド）: x,z ∈ [0, chunks*extent]。中心は地面（y=0）。
        let settings = self.terrain.settings.clone();
        let extent = settings.chunk_extent();
        let footprint_w = settings.ground_chunks_x as f32 * extent;
        let footprint_d = settings.ground_chunks_z as f32 * extent;
        let span = footprint_w.max(footprint_d);
        let center = [footprint_w * 0.5, 0.0, footprint_d * 0.5];
        // 目線は中心の上・手前（-Z 側）から見下ろす。距離は footprint に比例（マジックナンバー回避）。
        let eye = [
            center[0],
            center[1] + span * SMOKE_CAM_UP_RATIO,
            center[2] - span * SMOKE_CAM_BACK_RATIO,
        ];
        // 視線方向 = 正規化(center - eye)。yaw/pitch は debug_camera の規約に合わせる
        //   （forward → yaw = atan2(fwd.x, fwd.z), pitch = asin(-fwd.y)）。
        let dir = [center[0] - eye[0], center[1] - eye[1], center[2] - eye[2]];
        let len = (dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]).sqrt().max(f32::EPSILON);
        let fwd = [dir[0] / len, dir[1] / len, dir[2] / len];
        let yaw = fwd[0].atan2(fwd[2]);
        let pitch = (-fwd[1]).clamp(-1.0, 1.0).asin();
        let cam = crate::engine::core::app_base::scene::DebugCameraData {
            position: eye,
            yaw,
            pitch,
            fov_deg: SMOKE_CAM_FOV_DEG,
            far: SMOKE_CAM_FAR,
            speed: SMOKE_CAM_SPEED,
        };
        self.apply_camera_data(&cam);

        // ── 地面を明確に変形させる：盛り（Add）1・掘り（Subtract）1 ──
        //   Add は密度を下げて solid を増やす（隆起）、Subtract は密度を上げて air を増やす（陥没/洞窟）。
        let bump_center = [center[0] - SMOKE_BRUSH_OFFSET, 0.0, center[2]];
        let hole_center = [center[0] + SMOKE_BRUSH_OFFSET, 0.0, center[2]];
        self.handle_terrain_brush_world(BrushOp::Add, bump_center, SMOKE_BRUSH_RADIUS, SMOKE_BRUSH_STRENGTH);
        self.handle_terrain_brush_world(BrushOp::Subtract, hole_center, SMOKE_BRUSH_RADIUS, SMOKE_BRUSH_STRENGTH);

        // ── 連続ストローク（畝）: エディタのドラッグ相当を模擬する ──
        //   -Z 方向へ点を並べて Add ブラシを連続適用し、線を引いたような盛り上がりを作る。
        //   1 ストローク中の複数ブラシがすべて反映され再メッシュが追従することを実機で示す。
        let stroke_x = center[0];
        let stroke_z0 = center[2] - (SMOKE_STROKE_STEPS as f32 * SMOKE_STROKE_SPACING) * 0.5;
        for i in 0..SMOKE_STROKE_STEPS {
            let sc = [stroke_x, 0.0, stroke_z0 + i as f32 * SMOKE_STROKE_SPACING];
            self.handle_terrain_brush_world(BrushOp::Add, sc, SMOKE_BRUSH_RADIUS * 0.6, SMOKE_BRUSH_STRENGTH);
        }

        // ── プレビュー球の模擬 ──
        //   エディタ経由でしか出ないワイヤスフィアを、スモークでも直接セットして映す。
        //   footprint 中心の地表付近に置く（レイマーチのヒット点に相当）。
        self.terrain.brush_preview = Some(([center[0], 0.0, center[2]], SMOKE_PREVIEW_RADIUS));

        eprintln!(
            "[SEED terrain] smoke: init + deform + stroke({}) + preview done (chunks={})",
            SMOKE_STROKE_STEPS,
            self.terrain.chunks.len()
        );
    }
}
