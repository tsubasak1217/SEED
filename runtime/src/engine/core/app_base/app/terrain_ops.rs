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
use std::time::{Duration, Instant};

use rayon::prelude::*;

use crate::engine::ecs::Entity;
use crate::engine::components::{
    ColliderComponent, ColliderShapeData,
    ComponentKind, InstanceMeta, ModelComponent, TerrainChunkComponent,
    Transform as ActorTransform, GROUP_ID_BASE, next_batch_instance_id,
};
use crate::engine::core::loader::model::Model;
use crate::engine::methods::drawer::{DrawContext, GpuModel, InstancedModelBatch};
use crate::engine::physics::{ColliderShape, PhysicsObject, CharacterWorld};
use crate::engine::physics::char_world::PrebuiltMirrorCollider;
use crate::engine::structs::objects::Actor;
use crate::engine::terrain::{
    self, interp_vertex_paint, BlendSlots, BrushOp, ChunkCoord, PaintField, SampleField,
    SphereBrush, TerrainChunkData, TerrainLayerSet, TerrainSettings, TerrainVertexEdge,
    TERRAIN_BLEND_SLOTS, TERRAIN_MAX_LAYERS, tvox,
};

// 散布プロップ（Terrain T3）。状態の器だけをここで持ち、処理は terrain_scatter_ops.rs にある。
use crate::engine::core::renderer::grass_gbuffer::GrassInstanceBuffer;
use crate::engine::terrain::scatter::{ScatterInstance, TerrainPropSet};
// カバーブラシ（地形編集モード）の Undo スナップショットが `TerrainEdit` に同居するため。
use crate::engine::terrain::cover::CoverField;
// kind=Model 散布プロップの GPU リソース型（定義・処理とも terrain_scatter_ops.rs）。
use super::terrain_scatter_ops::ScatterModelResource;

use super::App;
use super::terrain_mesh_build::{
    compute_layer_colors, rebuild_terrain_model_with_colors, terrain_mesh_to_model,
};

// ─── 名前・調整用の名前付き定数（マジックナンバー禁止） ────────────────────────

/// 地形レイヤ定義アセットの仮想パス（データドリブン。ここを差し替えれば層構成が変わる）。
const TERRAIN_LAYERS_ASSET: &str = "assets://terrain/layers.json";

/// レイヤ定義の読み込み元を差し替える環境変数名。
///
/// 検証・デバッグ用の常設フック。絶対パスを渡すと `TERRAIN_LAYERS_ASSET` の代わりに
/// そのファイルを読む（`asset_fs` は絶対パスをそのまま読める）。
/// プロジェクトの `assets/terrain/layers.json` を書き換えずに、
/// 別のレイヤ構成（テクスチャ付き・detile 設定違いなど）を実機で試すために使う。
/// 未設定時は従来どおりアセットから読むため、通常運用の挙動は一切変わらない。
const TERRAIN_LAYERS_PATH_ENV: &str = "SEED_TERRAIN_LAYERS";

/// 地形ルートアクターの名前。
pub(super) const TERRAIN_ROOT_NAME: &str = "terrain";
/// 各チャンクのメッシュを載せるアクターの名前。
const TERRAIN_MESH_NAME: &str = "mesh";
/// メッシュアクターの ModelComponent スロット名。
const TERRAIN_MODEL_SLOT_NAME: &str = "mesh";
/// メッシュアクターの TerrainChunkComponent スロット名。
const TERRAIN_CHUNK_SLOT_NAME: &str = "chunk";

/// クリック 1 回ぶんのブラシ適用時間（離散編集なので 1.0 秒相当）。
const BRUSH_DT: f32 = 1.0;

/// ストローク中の付随処理（コライダー再構築・散布再接地・RT BLAS prune）を、
/// マウスアップを待たずに確定させる「無操作」猶予（ミリ秒）。
///
/// 【なぜ遅延するのか】
///   これらの付随処理は 1 フレームあたり数〜十数 ms を要する（特にキャラコン衝突ミラーの
///   trimesh QBVH 構築はメインスレッド同期）。ドラッグ中は毎フレーム remesh が走るため、
///   付随処理を毎フレーム重ねるとストローク中のフレーム時間をこれらが支配してしまう。
///   そこでストローク中はスキップして汚れチャンクを溜め、確定タイミングで一括適用する。
/// 【確定タイミング】マウスアップ（stroke 非アクティブ化）か、最後のブラシ適用から
///   この時間だけ操作が途切れたとき（＝ユーザがドラッグを止めて眺めている）、の早い方。
const STROKE_IDLE_FLUSH_MS: u64 = 300;

// ─── 地形編集の計測ログ（[PERF terrain]） ────────────────────────────────
/// 計測ログを有効化する環境変数名。frame_renderer.rs の `[PERF]` ログと**同じ**変数を使い、
/// 「SEED_PERF_LOG を付ければ全系統の PERF 行が出る」という既存の流儀を崩さない。
const PERF_LOG_ENV: &str = "SEED_PERF_LOG";

/// `[PERF terrain]` 行を出力するかどうか（既定は無効）。
///
/// フレーム描画側は 60 フレームに 1 回へ間引いているが、地形編集は間欠的（ドラッグ中のみ）で
/// 1 回 1 回が重いため、**再メッシュが起きたら毎回出す**。間引くと肝心のスパイクを取り逃す。
pub(super) static PERF_TERRAIN_LOG_ENABLED: std::sync::LazyLock<bool> =
    std::sync::LazyLock::new(|| std::env::var_os(PERF_LOG_ENV).is_some());

/// 地形コライダー生成の計測ログ（`[PERF terrain phys]` 行）を出すか。`SEED_PERF_LOG` で有効化。
///
/// `register_all_terrain_colliders`（物理開始時の全チャンク登録）が支配的コストだったため、
/// 「描画メッシュ再利用（MC なし）／MC フォールバック」の内訳と所要時間を数値化する。
static PERF_TERRAIN_PHYS_LOG_ENABLED: std::sync::LazyLock<bool> =
    std::sync::LazyLock::new(|| std::env::var_os(PERF_LOG_ENV).is_some());

/// 秒 → ミリ秒の換算係数（マジックナンバー回避）。
const MILLIS_PER_SEC: f64 = 1000.0;

// ─── チャンク単位 地形 LOD（遠いチャンクを低ポリ MC で描く）の調整値 ───────────
//
// 【データドリブンの余地】距離しきい値は当面 env 上書き付きの名前付き定数で持つ。
// 将来は TerrainSettings（props/settings）へ移し、プロジェクト単位で調整できるようにする。

/// LOD1（頂点 ≒ 1/4）へ落とすチャンク最近点距離の既定（m）。これ未満は LOD0（フル）。
const TERRAIN_LOD1_DISTANCE_DEFAULT: f32 = 60.0;
/// LOD2（頂点 ≒ 1/16）へ落とすチャンク最近点距離の既定（m）。これ以上は最粗段。
const TERRAIN_LOD2_DISTANCE_DEFAULT: f32 = 140.0;
/// LOD1 しきい値の env 上書き（m）。俯瞰スモークで段階を作るための恒常フック。
const TERRAIN_LOD1_DISTANCE_ENV: &str = "SEED_TERRAIN_LOD1_DIST";
/// LOD2 しきい値の env 上書き（m）。
const TERRAIN_LOD2_DISTANCE_ENV: &str = "SEED_TERRAIN_LOD2_DIST";
/// LOD 機能そのものを切る env（"1" で全チャンク LOD0＝before 計測用）。
const TERRAIN_LOD_DISABLED_ENV: &str = "SEED_TERRAIN_LOD_DISABLED";

/// LOD 遷移の「ばたつき」を防ぐヒステリシス幅（しきい値に対する割合）。
/// 粗くするのはしきい値×(1+H) を超えたとき、細かくするのは×(1−H) を下回ったとき。
const TERRAIN_LOD_HYSTERESIS: f32 = 0.12;
/// 1 フレームで処理する LOD 遷移（再メッシュ）の最大チャンク数（件数側のハード上限ガード）。
///
/// 主制約は下の時間バジェット（`TERRAIN_LOD_BUDGET_MS`）だが、万一 remesh が極端に軽く済む
/// フレーム（既にウォームなチャンクばかり等）で際限なく処理して他工程を圧迫しないよう、
/// 件数側の安全上限も併置する。通常はバジェットが先に効くため、この上限はまず発火しない。
const TERRAIN_LOD_TRANSITIONS_PER_FRAME: usize = 8;

/// LOD 再メッシュに 1 フレームで費やしてよい時間の上限（ms）。
///
/// 【配分根拠】目標フレーム 30fps（≒ 33.3ms/フレーム）の約 2 割（6ms）を地形 LOD の収束へ
/// 割く。残り約 8 割を描画・物理・スクリプト等へ残すことで、Play 開始直後にカメラがメイン
/// カメラ位置へ飛んで大量チャンクの目標 LOD が一斉に跨ぐ場面でも、フレーム時間の暴走
/// （実測: フレーム先頭で 250〜360ms を LOD 再メッシュに消費し約 3fps へ張り付く）を防ぐ。
/// バジェットを超えたぶんの遷移は次フレームへ繰り越し、数フレーム〜数秒かけて滑らかに収束する。
///
/// 【1 チャンクがバジェットより重い場合】1 チャンクの再メッシュが数十 ms に達しバジェット単独で
/// 超過することもあるが、その場合でも「最低 1 バッチは必ず処理」する前進保証（下の処理ループ）
/// により飢餓せず収束する。バジェットは「軽いフレームで詰め込みすぎない上限」として働く。
const TERRAIN_LOD_BUDGET_MS: f64 = 6.0;

/// LOD 再メッシュを小分けする 1 バッチあたりのチャンク数（時間計測の粒度）。
///
/// `remesh_chunks` は 1 呼び出しごとに固定オーバーヘッドを払う。支配的なのは GPU アイドル待ち
/// `device.poll(Wait)`（フェーズ B の全遅延破棄確定）で、これは 1 呼び出しに 1 回のバリアである。
/// ほかに settings/layers のクローンや派生キャッシュ invalidate（統合バッチ HashMap 除去＋
/// BLAS prune）も呼び出し単位で走る。invalidate 自体は HashMap 除去中心で軽いが、poll バリアは
/// 呼び出し回数ぶん積み上がるため、1 チャンクずつではなく **2 チャンクずつ**まとめて呼び、
/// 呼び出し回数（＝poll バリア回数）を半減させつつ CPU メッシュ生成の rayon 並列も 2 way 効かせる。
/// 一方でバッチを大きくすると「最低保証で必ず処理する 1 バッチ」の下限時間も増え、重チャンク時の
/// FPS 下限を損なうため、下限保護とオーバーヘッド償却の折衷として小さめの 2 に留める。
const TERRAIN_LOD_BATCH: usize = 2;

// ============================================================
//  RemeshOptions — 再メッシュの付随処理をどこまでやるか
// ============================================================

/// `App::remesh_chunks` の付随処理の指定。
///
/// 【なぜ真偽値の並びをやめたのか】
///   付随処理の軸が 3 つ（付随処理の遅延・GPU 解放の遅延・コライダー追従）になり、
///   呼び出し側が `remesh_chunks(&coords, false, true, false)` のような
///   「意味の読めない真偽値の列」になった。名前付きコンストラクタで
///   **呼び出し側に経路の名前が残る**ようにする。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct RemeshOptions {
    /// 付随処理（RT BLAS prune）をストローク確定まで遅らせるか。
    ///
    /// true のときは `rt_blas_prune_pending` へ積むだけにする（毎フレームの prune で
    /// BLAS 再構築上限を食い潰さないため）。統合バッチ無効化は描画に必須なので常に走る。
    pub defer_side_effects: bool,
    /// 旧 GPU リソースを即 drop + `poll(Wait)` せず、退役キューへ回すか。
    ///
    /// true にすると GPU アイドル待ちのバリアを張らない（移動時スパイクの排除）。
    pub defer_gpu_release: bool,
    /// 地形の物理コライダーを作り直すか。
    ///
    /// 【false にしてよいのは「密度場が 1 ビットも変わっていない」再メッシュだけ】
    ///   コライダーは表示 LOD に関係なく常にフル解像度（LOD0）で作られる。
    ///   よって **表示 LOD の切り替えだけで再メッシュしたチャンクの当たり判定は、
    ///   再構築しても 1 ビットも変わらない**。にもかかわらず従来はここで
    ///   「密度からフル解像度のメッシュを作り直す（LOD>0 のときは MC 再実行）＋
    ///   Rapier トライメッシュの QBVH をメインスレッドで同期構築」を払っており、
    ///   Play 中にカメラが動くと毎フレーム数十 ms を消費していた（実測 91ms）。
    pub sync_colliders: bool,
}

impl RemeshOptions {
    /// 即時経路（ブラシ確定・undo/redo・チャンク追加・一括収束）。
    ///
    /// 付随処理も GPU 解放もコライダー追従も、その場ですべて行う。
    pub(super) fn immediate() -> Self {
        Self { defer_side_effects: false, defer_gpu_release: false, sync_colliders: true }
    }

    /// LOD 遷移経路（`tick_terrain_lod`。毎フレーム・密度場は不変）。
    ///
    /// GPU 解放は退役キューへ回し、コライダーは触らない（形状が変わらないため）。
    pub(super) fn lod_transition() -> Self {
        Self { defer_side_effects: false, defer_gpu_release: true, sync_colliders: false }
    }

    /// ストローク中の遅延を切り替えた版を返す（ブラシ経路のみ動的に決まるため）。
    pub(super) fn with_deferred_side_effects(mut self, defer: bool) -> Self {
        self.defer_side_effects = defer;
        // ストローク中はコライダー追従も確定時（`finalize_stroke_deferred`）へ回す。
        self.sync_colliders = !defer;
        self
    }
}

/// このフレームで「あと何チャンク切り出してよいか」を返す（純関数）。
///
/// 【予算の 2 段構え】
///   ① 件数のハード上限 `TERRAIN_LOD_TRANSITIONS_PER_FRAME`（本関数が担当）
///   ② 時間バジェット `TERRAIN_LOD_BUDGET_MS`（呼び出し側が 1 バッチ処理ごとに判定）
///   本関数が 0 を返す＝件数上限に到達で、残りは次フレームへ**繰り越す**（捨てない）。
///
/// 【最低 1 バッチは必ず前進する】
///   `processed == 0` のときは必ず正の値を返す（上限は 1 以上の定数）。
///   時間バジェット側の判定も「1 バッチ処理したあと」に行うため、
///   1 チャンクが極端に重いフレームでも飢餓せず収束する。
///
/// - `processed`: このフレームで既に再メッシュ済みのチャンク数
pub(super) fn lod_batch_size(processed: usize) -> usize {
    TERRAIN_LOD_BATCH.min(TERRAIN_LOD_TRANSITIONS_PER_FRAME.saturating_sub(processed))
}

/// LOD 遷移で差し替えた旧チャンク GPU リソース（`GpuModel`／`InstancedModelBatch`）を、
/// その場で drop せず「退役キュー」で保持するフレーム数。
///
/// 【なぜ即 drop + `poll(Wait)` を避けるのか — 移動時スパイクの真因】
///   `remesh_chunks` の従来経路は「旧 GpuModel を drop → `device.poll(Wait)`」で解放を
///   同期確定していた。この `poll(Wait)` は GPU が **全 in-flight 提出を完了** するまで
///   ブロックするため、ゲーム中（GPU が重い RT 影・反射を処理中）は 1 回で 80〜130ms 停止する
///   （実測: Play 開始前＝GPU アイドル時の一括収束は 564 チャンクでも 502ms＝0.9ms/chunk なのに、
///    ゲーム中の 2 チャンク遷移で done_ms≈100ms。差はメッシュ生成ではなく poll バリアの GPU 待ち）。
///   `poll(Wait)` 自体は「in-flight リソースを drop した際の遅延破棄を、フレーム末尾の
///   `queue.submit()`（snatch read lock 保持）が処理して write lock 再帰でパニックする」のを
///   防ぐための安全点フラッシュである（詳細は grass_gbuffer.rs `update` のコメント参照）。
///
/// 【本方式（遅延退役）】旧リソースを即 drop せず本数フレーム保持する。保持している間に
///   そのリソースを参照した提出は GPU 上で完了する（in-flight 深度＝スワップチェーン画像数で
///   上限されるので、時間ではなく **提出深度** で決まる。よって数フレームで十分）。完了後に
///   フレーム先頭（read lock 非保持の安全点）で drop → 非ブロッキングの `poll(Poll)` で遅延破棄を
///   確定する。これによりゲーム中でも LOD 遷移フレームが数 ms で済み、スパイクが消える。
///
/// 【値の根拠】ダブル／トリプルバッファ＋present キュー深度（通常 2〜3）に安全余裕を持たせて 4。
/// これは時間ではなく提出深度に対する余裕なので、低 FPS でも panic 安全側に働く。
const TERRAIN_GPU_RETIRE_FRAMES: u64 = 4;

/// ストローク遅延付随処理を「今」確定（一括適用）すべきかを判定する純粋関数。
///
/// - `deferred_empty`: 遅延チャンク集合が空なら確定するものが無い（常に false）。
/// - `stroke_active`: マウスアップ済みなら（false）即確定してよい。
/// - `idle_elapsed`: ストローク継続中でも、最後のブラシから一定時間途切れたら確定する。
///
/// 確定条件は「溜まったチャンクがある」かつ「マウスアップ済み **または** 無操作タイムアウト」。
/// remesh_chunks を経由しない副作用の無い分岐判定なので、単体テストで網羅できるよう関数化する。
fn should_finalize_stroke(deferred_empty: bool, stroke_active: bool, idle_elapsed: bool) -> bool {
    !deferred_empty && (!stroke_active || idle_elapsed)
}

/// RT BLAS 再構築待ちから「このフレームで消化する分」を選ぶ純粋関数。
///
/// - 座標順（x, y, z）に並べる。`HashSet` の走査順は実行ごとに変わるため、
///   ソートしないと同じ操作でも捨てられるチャンクの組が毎回変わり、再現性が無くなる。
/// - `budget` 個で頭打ちにする（0 なら何も返さない）。残りは呼び出し側の集合に残り、
///   次フレーム以降で消化される。
///
/// 副作用を持たない選択ロジックなので、単体テストで決定性と予算を直接検証できるよう関数化する。
fn select_rt_prune_batch(pending: &HashSet<ChunkCoord>, budget: usize) -> Vec<ChunkCoord> {
    let mut coords: Vec<ChunkCoord> = pending.iter().copied().collect();
    coords.sort_by_key(|c| (c.x, c.y, c.z));
    coords.truncate(budget);
    coords
}

/// LOD 距離しきい値（env 上書き反映済み）。`(lod1, lod2)` を返す。
fn terrain_lod_distances() -> (f32, f32) {
    let parse = |name: &str, default: f32| -> f32 {
        std::env::var(name)
            .ok()
            .and_then(|v| v.trim().parse::<f32>().ok())
            .filter(|v| v.is_finite() && *v > 0.0)
            .unwrap_or(default)
    };
    let d1 = parse(TERRAIN_LOD1_DISTANCE_ENV, TERRAIN_LOD1_DISTANCE_DEFAULT);
    let d2 = parse(TERRAIN_LOD2_DISTANCE_ENV, TERRAIN_LOD2_DISTANCE_DEFAULT);
    // d2 は必ず d1 以上（逆転設定を防ぐ）。
    (d1, d1.max(d2))
}

/// LOD 機能が無効か（before 計測用）。
static TERRAIN_LOD_DISABLED: std::sync::LazyLock<bool> =
    std::sync::LazyLock::new(|| std::env::var(TERRAIN_LOD_DISABLED_ENV).as_deref() == Ok("1"));

/// 現在の LOD と最近点距離から、ヒステリシス込みで目標 LOD を決める純関数。
///
/// - `current`: そのチャンクの現在 LOD（0/1/2）。
/// - `dist`: チャンク AABB とカメラの最近点距離（m）。
/// - `(d1, d2)`: LOD1/LOD2 のしきい値（m・d1<=d2）。
/// 境界付近での往復（再メッシュのばたつき）を防ぐため、上げ／下げでしきい値をずらす。
fn desired_lod_for_distance(current: u8, dist: f32, d1: f32, d2: f32) -> u8 {
    let h = TERRAIN_LOD_HYSTERESIS;
    // 粗→細（LOD を下げる＝より高精細に）は下側しきい値、細→粗（上げる）は上側しきい値を使う。
    let up1 = d1 * (1.0 + h);
    let dn1 = d1 * (1.0 - h);
    let up2 = d2 * (1.0 + h);
    let dn2 = d2 * (1.0 - h);
    // 素の距離帯から「素の目標」を求める。
    let raw: u8 = if dist >= d2 {
        2
    } else if dist >= d1 {
        1
    } else {
        0
    };
    let mut t = current;
    if raw > current {
        // より粗くしたい: 上側しきい値を確実に超えたぶんだけ 1 段ずつ上げる（2 段跨ぎも可）。
        if t == 0 && dist > up1 {
            t = 1;
        }
        if t == 1 && dist > up2 {
            t = 2;
        }
    } else if raw < current {
        // より細かくしたい: 下側しきい値を確実に下回ったぶんだけ 1 段ずつ下げる。
        if t == 2 && dist < dn2 {
            t = 1;
        }
        if t == 1 && dist < dn1 {
            t = 0;
        }
    }
    t
}

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
// ─── スモークの検証用方向光（影の確認に必須） ───────────────────────
/// スモークが置く方向光アクターの名前。
const SMOKE_LIGHT_ACTOR_NAME: &str = "SmokeSun";
/// スモークが置く方向光の Light スロット名。
const SMOKE_LIGHT_SLOT_NAME: &str = "Light";
/// 方向光の X 回転（度）。正で forward.y が負＝下向きになる（斜め上から照らす）。
const SMOKE_LIGHT_PITCH_DEG: f32 = 55.0;
/// 方向光の Y 回転（度）。真横からではなく斜めに振り、穴の側面に陰影差を作る。
const SMOKE_LIGHT_YAW_DEG: f32 = 30.0;
/// 方向光の色（白）。
const SMOKE_LIGHT_COLOR: [f32; 3] = [1.0, 1.0, 1.0];
/// 方向光の強度。
const SMOKE_LIGHT_INTENSITY: f32 = 3.0;
/// 方向光の range（平行光では未使用だが 0 除算回避のため正値を入れる）。
const SMOKE_LIGHT_RANGE: f32 = 100.0;
/// 方向光のソフト影角径（度）。0 にするとハードシャドウになる。
const SMOKE_LIGHT_SOFT_RADIUS_DEG: f32 = 0.25;

// ─── スモークの「描画開始後の編集」検証 ─────────────────────────────
// 本体のスモークは初期化フェーズ（1 フレーム目より前）で完結するため、
// 「既に描画されているシーンをリアルタイムに掘る」状況を再現できない。
// レンダラ側の派生キャッシュ（BLAS・統合バッチ）が絡む不具合はその状況でしか
// 出ないため、数フレーム描画してから掘るステップを別に持つ。
/// 遅延掘削を発火させるフレーム番号（描画が数フレーム進み、加速構造が構築済みになった後）。
const SMOKE_DEFERRED_DIG_FRAME: u32 = 30;
/// 遅延掘削の半径（メートル）。穴の内部が画面上ではっきり見える大きさにする。
const SMOKE_DEFERRED_DIG_RADIUS: f32 = 14.0;
/// 遅延掘削の強度。
const SMOKE_DEFERRED_DIG_STRENGTH: f32 = 1.0;
/// 遅延掘削を縦に積む段数（地表 Y が未知でも確実に貫くため）。
const SMOKE_DEFERRED_DIG_COLUMN_STEPS: u32 = 6;
/// 遅延掘削を縦に積むときの 1 段あたりの高さ（メートル）。
const SMOKE_DEFERRED_DIG_COLUMN_STEP_Y: f32 = 3.0;
// ── 遅延ペイント（ペイント高速パスの実機検証用）─────────────────────────────
//
// 【なぜ init 時のペイントと別に要るか】
//   `run_terrain_smoke` は初期化・掘削・ペイントを同一フレーム内で連続して行う。
//   ダーティ集約により、そのフレームでは触れたチャンクの多くが「密度も変わった」
//   ＝ pending_remesh 側に入り、ペイント高速パス（pending_paint）は
//   「密度が変わっていないチャンク」だけを受け取る。結果、init 時のペイントは
//   高速パスをほとんど通らず、効果を実機で確認できない。
//   そこで、地形が完全にメッシュ化・描画済みになったフレームで
//   **密度を触らないペイントだけ**を連続して当てるステップを別に持つ。
//   1 回目はパレットが変わり得るのでフォールバックし、2 回目以降が高速パスに乗る
//   （＝`[PERF terrain] paint ... fast=N` が立つ）ことをログで確認できる。
/// 遅延ペイントを発火させるフレーム番号（掘削のあと、地形が再メッシュ済みになってから）。
const SMOKE_DEFERRED_PAINT_FRAMES: [u32; 3] = [40, 45, 50];
/// 遅延ペイントで塗るレイヤ番号。
const SMOKE_DEFERRED_PAINT_LAYER: usize = 2;
/// 遅延ペイントの半径（メートル）。
const SMOKE_DEFERRED_PAINT_RADIUS: f32 = 10.0;
/// 遅延ペイントの強度。
const SMOKE_DEFERRED_PAINT_STRENGTH: f32 = 1.0;
/// 遅延ペイントを縦に積む段数（地表 Y が未知でも確実に地表を貫くため）。
const SMOKE_DEFERRED_PAINT_COLUMN_STEPS: u32 = 6;
/// 遅延ペイントを縦に積むときの 1 段あたりの高さ（メートル）。
const SMOKE_DEFERRED_PAINT_COLUMN_STEP_Y: f32 = 3.0;
/// 遅延ペイントの中心を掘削中心からずらす距離（掘った穴の外の地表を塗るため）。
const SMOKE_DEFERRED_PAINT_OFFSET: f32 = 20.0;

/// 遅延掘削の状態カウンタ（0 = 無効／1 以上 = スモーク有効時のフレーム計数）。
/// App にフィールドを増やさずデバッグフックを閉じ込めるための静的状態。
static SMOKE_DEFERRED_FRAME_COUNTER: std::sync::atomic::AtomicU32 =
    std::sync::atomic::AtomicU32::new(0);
/// 遅延掘削が有効か（run_terrain_smoke が立てる）。
static SMOKE_DEFERRED_ENABLED: std::sync::atomic::AtomicBool =
    std::sync::atomic::AtomicBool::new(false);

// ── スモークのクローズアップカメラ（草の 1 本 1 本を画面上で解像させるための構図）──
//   広角の全景では草丈 0.4m が 1 画素未満になり、風で揺れても目視できない。
//   指定フレームで散布データを基準に近接カメラへ切り替え、葉の動きを撮れるようにする。

/// クローズアップへ切り替えるフレーム番号を指定する環境変数名（未指定なら切り替えない）。
const ENV_SMOKE_CLOSEUP_FRAME: &str = "SEED_SMOKE_CLOSEUP_FRAME";
/// クローズアップの被写体プロップ添字を指定する環境変数名（未指定なら `SMOKE_CLOSEUP_PROP_INDEX`）。
/// 高ポリ散布モデル（例: 木）を近接 LOD0 で写して描画コストを計測する用途で使う。
const ENV_SMOKE_CLOSEUP_PROP: &str = "SEED_SMOKE_CLOSEUP_PROP";
/// クローズアップの水平距離を上書きする環境変数名（未指定なら `SMOKE_CLOSEUP_DISTANCE`）。
/// 木のような大きい被写体は既定 1.1m では近すぎるため、env で引ける（例: 十数本を画面に収める）。
const ENV_SMOKE_CLOSEUP_DISTANCE: &str = "SEED_SMOKE_CLOSEUP_DIST";
/// クローズアップ時、注視点からカメラまでの水平距離（メートル）の既定値。
const SMOKE_CLOSEUP_DISTANCE: f32 = 1.1;
/// クローズアップ時、注視点に対するカメラの高さ（メートル）。ほぼ水平に見る。
const SMOKE_CLOSEUP_EYE_HEIGHT: f32 = 0.30;
/// クローズアップ時、接地点から注視点を持ち上げる量（メートル）。草丈の半分程度。
const SMOKE_CLOSEUP_TARGET_LIFT: f32 = 0.20;
/// クローズアップ時の垂直画角（度）。狭めにして被写体を大きく写す。
const SMOKE_CLOSEUP_FOV_DEG: f32 = 35.0;
/// クローズアップ時のカメラ遠クリップ（メートル）。近接なので短くてよい。
const SMOKE_CLOSEUP_FAR: f32 = 200.0;
/// クローズアップ時のデバッグカメラ移動速度（メートル/秒）。構図固定なので使わないが必須項目。
const SMOKE_CLOSEUP_SPEED: f32 = 2.0;
/// クローズアップの被写体に選ぶプロップ添字の既定値（0 = props.json の先頭 = grass_field）。
const SMOKE_CLOSEUP_PROP_INDEX: u32 = 0;

/// クローズアップ切替フレーム（環境変数 `SEED_SMOKE_CLOSEUP_FRAME`）。未指定なら `None`。
static SMOKE_CLOSEUP_FRAME: std::sync::LazyLock<Option<u32>> =
    std::sync::LazyLock::new(|| {
        std::env::var(ENV_SMOKE_CLOSEUP_FRAME).ok()?.trim().parse::<u32>().ok()
    });
/// クローズアップの被写体プロップ添字（環境変数 `SEED_SMOKE_CLOSEUP_PROP` 優先・既定 `SMOKE_CLOSEUP_PROP_INDEX`）。
static SMOKE_CLOSEUP_PROP: std::sync::LazyLock<u32> =
    std::sync::LazyLock::new(|| {
        std::env::var(ENV_SMOKE_CLOSEUP_PROP)
            .ok()
            .and_then(|v| v.trim().parse::<u32>().ok())
            .unwrap_or(SMOKE_CLOSEUP_PROP_INDEX)
    });
/// クローズアップの水平距離（環境変数 `SEED_SMOKE_CLOSEUP_DIST` 優先・既定 `SMOKE_CLOSEUP_DISTANCE`）。
static SMOKE_CLOSEUP_DIST: std::sync::LazyLock<f32> =
    std::sync::LazyLock::new(|| {
        std::env::var(ENV_SMOKE_CLOSEUP_DISTANCE)
            .ok()
            .and_then(|v| v.trim().parse::<f32>().ok())
            .filter(|d| d.is_finite() && *d > 0.0)
            .unwrap_or(SMOKE_CLOSEUP_DISTANCE)
    });
/// クローズアップ判定用のフレームカウンタ（描画開始後のフレーム数）。
static SMOKE_CLOSEUP_COUNTER: std::sync::atomic::AtomicU32 =
    std::sync::atomic::AtomicU32::new(0);
/// クローズアップへの切り替えが済んだか（1 度だけ実行するためのラッチ）。
static SMOKE_CLOSEUP_DONE: std::sync::atomic::AtomicBool =
    std::sync::atomic::AtomicBool::new(false);

/// スモークのプレビュー球（ワイヤスフィア）半径（メートル）。
const SMOKE_PREVIEW_RADIUS: f32 = 5.0;
/// スモークのプレビュー球の強度（色の確認用。0.85=高強度寄りのオレンジに近い色になる）。
const SMOKE_PREVIEW_STRENGTH: f32 = 0.85;

/// スモークテストの散布対象 prop_id。空文字 = props.json の全プロップ。
const SMOKE_SCATTER_ALL_PROPS: &str = "";
/// スモークテストのルール散布シード（結果を毎回同一にするための固定値）。
const SMOKE_SCATTER_SEED: u64 = 0x5EED_5CA7_0000_0001;
/// スモークの undo/redo 確認用ストロークをフットプリント中心からずらす量（メートル）。
const SMOKE_UNDO_TEST_OFFSET: f32 = 16.0;
/// スモークの undo/redo 確認ストロークで適用するブラシ回数。
const SMOKE_UNDO_TEST_BRUSH_COUNT: u32 = 3;
/// スモークで書き出すテスト用ハイトマップ画像の 1 辺のピクセル数。
const SMOKE_HEIGHTMAP_SIZE: u32 = 64;
/// スモークのハイトマップ読込で使う高さスケール（メートル）。
const SMOKE_HEIGHTMAP_HEIGHT_SCALE: f32 = 10.0;
/// スモークのレイヤペイント確認で塗るレイヤ番号（既定 layers.json では 3 = sand＝砂色）。
/// 自動下地（草／土／岩）と明確に色が違う層を選び、手ペイントが効いていることを一目で示す。
const SMOKE_PAINT_LAYER: usize = 3;
/// スモークのレイヤペイント半径（メートル）。
const SMOKE_PAINT_RADIUS: f32 = 7.0;
/// スモークのレイヤペイント強度（1 回で paint_amount がほぼ 1 へ達する値）。
const SMOKE_PAINT_STRENGTH: f32 = 2.0;
/// スモークのレイヤペイント中心を footprint 中心からずらす量（メートル）。
const SMOKE_PAINT_OFFSET: f32 = 24.0;
/// スモークのレイヤペイントを縦方向に積む段数。ハイトマップ適用後の地表 Y は
/// スクリーンレイを使わずには求められないため、垂直に積んで確実に地表を覆う。
const SMOKE_PAINT_COLUMN_STEPS: u32 = 12;
/// レイヤペイント縦積みの 1 段あたりの高さ（メートル）。
const SMOKE_PAINT_COLUMN_STEP_Y: f32 = 1.5;
/// スモークの「急斜面デモ」ブラシ強度。斜度ルール（38 度以上＝岩）が確実に立つよう
/// 通常のスモークブラシより強く盛って切り立った山を作る。
const SMOKE_STEEP_STRENGTH: f32 = 40.0;
/// 急斜面デモの山／谷を footprint 中心からずらす量（メートル）。
const SMOKE_STEEP_OFFSET: f32 = 14.0;

/// スモークの「構成指定つき初期化」で使う 1 軸あたりのチャンク数（小さめの構成）。
const SMOKE_CONFIG_CHUNKS: u32 = 2;
/// スモークの「構成指定つき初期化」で使うチャンク分割数（既定 32 と違う値にして効果を確認する）。
const SMOKE_CONFIG_CHUNK_CELLS: u32 = 16;
/// スモークの「構成指定つき初期化」で使うボクセルサイズ（メートル）。
const SMOKE_CONFIG_VOXEL_SIZE: f32 = 0.5;
/// スモーク本編（構図を既定に戻す再初期化）で使う 1 軸あたりのチャンク数。
const SMOKE_DEFAULT_CHUNKS: u32 = 4;
/// スモーク本編のチャンク数を上書きする環境変数（描画カリング計測用）。
///
/// `SEED_SMOKE_CHUNKS=16` を渡すと 16×16 の広い地形でスモークを回せる（地形のみで
/// 描画チャンク数・main_pass ms を before/after 計測するための恒常フック）。未設定・不正なら
/// `SMOKE_DEFAULT_CHUNKS`。値は `apply_chunk_config` が 1..=32 にクランプする。
const SMOKE_CHUNKS_ENV: &str = "SEED_SMOKE_CHUNKS";
/// スモーク本編で使うチャンク分割数。
const SMOKE_DEFAULT_CHUNK_CELLS: u32 = 32;
/// スモーク本編で使うボクセルサイズ（メートル）。
const SMOKE_DEFAULT_VOXEL_SIZE: f32 = 0.5;

/// terrain 専用 undo スタックの最大保持数。これを超えたら最古のエントリを破棄する
/// （無制限に保持すると 1 チャンク ≒ 143KB のスナップショットが積み上がり続けるため）。
const TERRAIN_UNDO_MAX: usize = 32;

// ============================================================
//  TerrainState — 地形の実行時状態
// ============================================================

/// terrain 専用 undo/redo の 1 エントリ（1 ストローク分の編集）。
///
/// シーン全体の undo.rs（Command トレイト/UndoHistory）は Scene（ECS World）を対象とするが、
/// 地形密度は App.terrain（TerrainState, Scene 外の HashMap<ChunkCoord,TerrainChunkData>）に
/// あるため、既存の undo 機構へ統合できない。そのため地形専用の軽量スタックを別に持つ。
///
/// 触ったチャンクのみを before/after として保持する（全チャンクをスナップショットすると
/// 1 チャンクあたり 33³×4byte ≒ 143KB と重いため、ストロークで実際に触れたチャンクに限定する）。
///
/// 【カバー場の扱い — 「操作した場所で戻る」で線引きする】
///   地表カバー場（積雪・落ち葉。I3.1）のうち、**地形編集モードのカバーブラシ**で
///   手編集したぶんはここに載る（`cover_before` / `cover_after`）。
///   一方、**エミッタのシミュレート・全消去**（入口は `CoverEmitterComponent` の
///   インスペクタ）はメイン履歴（`undo.rs::CoverFieldEditCommand`）の管轄である。
///
///   分ける基準は「どこで操作したか」である。エディタが `TERRAIN_UNDO` を送るのは
///   地形編集モード中の Ctrl+Z だけ（`MainWindow.Input.cs`）なので、
///   地形編集モードの道具で行った編集を地形スタックへ、
///   インスペクタで行った編集をメイン履歴へ載せると、
///   ユーザーから見て「操作したのと同じ場所で 1 回ずつ戻る」ようになる。
///   管轄表は docs/cover_field.md §5。
pub struct TerrainEdit {
    /// ストローク開始時点のチャンク状態（chunk coord -> スナップショット）。
    pub before: HashMap<ChunkCoord, ChunkSnapshot>,
    /// ストローク終了時点のチャンク状態（chunk coord -> スナップショット）。
    pub after: HashMap<ChunkCoord, ChunkSnapshot>,
    /// ストローク開始時点のカバー場（カバーブラシで**実際に変化した**チャンクのみ）。
    ///
    /// 密度スナップショット（143KB/チャンク）と別マップにしてあるのは、
    /// カバー場が 2KB しかないためである。雪を消しただけのストロークで
    /// 密度まで控えるのは純粋な無駄であり、逆も同じ。
    /// 密度ブラシだけのストロークではこのマップは空になる。
    pub cover_before: HashMap<ChunkCoord, CoverField>,
    /// ストローク終了時点のカバー場（`cover_before` と同じキー集合）。
    pub cover_after: HashMap<ChunkCoord, CoverField>,
}

/// undo/redo 用の 1 チャンク分スナップショット（密度＋スプラット）。
///
/// 密度ブラシ（TERRAIN_BRUSH）とペイントブラシ（TERRAIN_PAINT）を同じ undo スタックに
/// 載せるため、両方の状態をひとまとめに控える。片方しか変わらないストロークでも
/// もう片方をそのまま控えるだけなので、復元は常に「丸ごと書き戻す」で済む（分岐不要）。
#[derive(Clone)]
pub struct ChunkSnapshot {
    /// 密度サンプル（f32 × samples³）。
    pub density: Vec<f32>,
    /// 手ペイントスロットのレイヤ番号（u8 × TERRAIN_BLEND_SLOTS × samples³）。
    pub paint_index: Vec<[u8; TERRAIN_BLEND_SLOTS]>,
    /// 手ペイントスロットの重み（u8 × TERRAIN_BLEND_SLOTS × samples³）。
    pub paint_weight: Vec<[u8; TERRAIN_BLEND_SLOTS]>,
    /// ペイント量（u8 × samples³）。
    pub paint_amount: Vec<u8>,
}

impl ChunkSnapshot {
    /// チャンクの現在状態を控える。
    pub fn capture(chunk: &TerrainChunkData) -> Self {
        Self {
            density:      chunk.raw_density().to_vec(),
            paint_index:  chunk.raw_paint_index().to_vec(),
            paint_weight: chunk.raw_paint_weight().to_vec(),
            paint_amount: chunk.raw_paint_amount().to_vec(),
        }
    }

    /// 控えた状態をチャンクへ書き戻す。
    pub fn restore(&self, chunk: &mut TerrainChunkData) {
        chunk.set_raw_density(self.density.clone());
        chunk.set_raw_paint_index(self.paint_index.clone());
        chunk.set_raw_paint_weight(self.paint_weight.clone());
        chunk.set_raw_paint_amount(self.paint_amount.clone());
    }
}

/// ボクセル地形の実行時状態。App に 1 つ保持する。
pub struct TerrainState {
    /// 地形の調整設定（voxel_size / chunk_cells / iso / density_clamp 等）。
    pub settings: TerrainSettings,
    /// 全チャンクの密度グリッド（キー = チャンク格子座標）。
    pub chunks: HashMap<ChunkCoord, TerrainChunkData>,
    /// チャンク → そのメッシュを載せる ModelComponent スロットの entity。
    /// 再メッシュ化（GPU 差し替え）時に対象コンポーネントを引くために使う。
    pub chunk_slot_entity: HashMap<ChunkCoord, Entity>,
    /// チャンク → 現在アップロード済みの LOD レベル（0=フル・1・2…）。未登録は 0 とみなす。
    ///
    /// `tick_terrain_lod` がカメラ距離から目標 LOD を決め、現在値と異なるチャンクだけを
    /// `pending_remesh` へ積んで LOD を切り替える。`remesh_chunks` はここを読んで
    /// `build_chunk_cpu_model` に渡す LOD を決める（＝どの解像度でメッシュ化するか）。
    /// 地形を作り直す経路（`TerrainState::default()`）で丸ごと消え、全チャンク LOD0 から始まる。
    pub chunk_lod: HashMap<ChunkCoord, u8>,
    /// 現在の地形が属するシーン名（.tvox の保存フォルダ・合成 source_path に使う）。
    pub scene_name: String,
    /// 編集されて未保存のチャンク集合（handle_terrain_save でクリア）。
    pub dirty: HashSet<ChunkCoord>,
    /// **再メッシュ待ち**のチャンク集合（ダーティ集約）。
    ///
    /// ドラッグ中は 1 フレームに複数の TERRAIN_BRUSH / TERRAIN_PAINT が届き、
    /// そのたびに同じチャンクを何度も再メッシュしていた（1 チャンク数 ms × 重複回数）。
    /// ブラシ適用（＝密度・スプラットの書き換え）はそのまま即時に行い、
    /// **メッシュ化だけ**をここへ積んで 1 フレーム 1 回へ潰す。
    /// `App::flush_terrain_pending_remesh` が IPC コマンドループ直後に消化する。
    /// 集合なので同一チャンクの重複は自然に 1 回へ畳まれる。
    pub pending_remesh: HashSet<ChunkCoord>,
    /// **頂点カラーだけの更新待ち**チャンク集合（ペイント高速パス）。
    ///
    /// レイヤペイント（TERRAIN_PAINT）は密度を一切変えないため、頂点位置・法線・
    /// インデックス・三角形数はすべて不変であり、変わるのは頂点カラー（レイヤ重み）と
    /// チャンクのパレットだけである。よってマーチングキューブスを回す必要が無い。
    /// `pending_remesh` とは別の集合に積み、`apply_terrain_paint_colors` が
    /// 「由来辺キャッシュから重みを引き直して頂点カラーを差し替えるだけ」で消化する。
    ///
    /// 【優先順位】同一フレームで同じチャンクが `pending_remesh` にも入った場合は
    /// **`pending_remesh` が勝つ**（フル再メッシュすれば頂点カラーも当然作り直されるため、
    /// ペイント高速パスを重ねて走らせるのは純粋な無駄になる）。
    pub pending_paint: HashSet<ChunkCoord>,
    /// チャンク → そのメッシュ頂点の「由来辺」記述子（positions と同順・同長）。
    ///
    /// ペイント高速パスがマーチングキューブスを再実行せずに頂点ごとのレイヤ重みを
    /// 引き直すために使う（`interp_vertex_paint` の入力）。`remesh_chunks` が
    /// メッシュを作り直すたびに更新され、地形を作り直す経路（`TerrainState::default()` で
    /// 丸ごと差し替わる `build_terrain_with` / `rebuild_terrain_after_load`）では
    /// チャンクごと消える。
    ///
    /// 【メモリ見積り】`TerrainVertexEdge` は 16 バイト／頂点（lo:[u16;3]=6 + axis:u8=1 +
    /// パディング1 + t:f32=4 → アラインメント込み 16）。cells=64 の実測 17,173 頂点で
    /// 約 275 KB/チャンク。既定の cells=32 ではその 1/4 程度で収まる。
    pub chunk_vertex_edges: HashMap<ChunkCoord, Arc<Vec<TerrainVertexEdge>>>,
    /// ブラシプレビュー（Edit モードのホバー位置に描くワイヤスフィア）の
    /// (ワールド中心, 半径, 強度)。`None` のとき非表示。frame_renderer が描画に使う。
    /// 強度はプレビュー球の色（低強度=水色〜高強度=オレンジ）に反映される。
    /// レイがヒットしない（空を指す）フレームは `None` へクリアされる。
    pub brush_preview: Option<([f32; 3], f32, f32)>,
    /// 現在ブラシストローク中かどうか。TERRAIN_BRUSH（ドラッグ中の連続適用）の
    /// 最初の 1 回で暗黙的に true になり、TERRAIN_STROKE_END で false に戻る。
    pub stroke_active: bool,
    /// 現在のストローク中に初めて触れたチャンクの「編集前」スナップショット。
    /// ストローク確定（handle_terrain_stroke_end）時に TerrainEdit::before として消費される。
    pub stroke_before: HashMap<ChunkCoord, ChunkSnapshot>,
    /// ストローク編集で汚れたが、付随処理（コライダー再構築・散布再接地・RT BLAS prune）を
    /// **まだ適用していない**チャンク集合。
    ///
    /// ストローク中の flush は remesh（描画メッシュ／GPU 差し替え。これは必須）だけを行い、
    /// 重い付随処理はここへ積んで先送りする。確定（`finalize_stroke_deferred`）で一括処理し
    /// クリアする。集合なので同一チャンクの重複は自然に 1 回へ畳まれる。
    /// 詳細な理由は `STROKE_IDLE_FLUSH_MS` のコメントを参照。
    pub stroke_deferred_chunks: HashSet<ChunkCoord>,
    /// 最後にブラシ（密度編集）を適用した時刻。無操作タイムアウト（`STROKE_IDLE_FLUSH_MS`）の
    /// 判定に使う。`None` はストローク未開始または確定済み（付随処理を溜めていない状態）。
    pub last_brush_apply: Option<Instant>,
    /// コライダー再構築の計測中フラグ。`true` の間だけ `physics_add_object` /
    /// `physics_remove_object` が「ミラー（QBVH 構築）」と「物理スレッド送信」の所要時間を
    /// 下の 2 フィールドへ積む。既定 `false` で計測オフ時はゼロコスト。
    pub perf_collider_measuring: bool,
    /// 計測中に積算したキャラコン衝突ミラー（QBVH 構築等）の所要時間。
    pub perf_collider_mirror: Duration,
    /// 計測中に積算した物理スレッドへの Remove/Add 送信の所要時間。
    pub perf_collider_send: Duration,
    /// 地形マテリアルレイヤ定義（assets/terrain/layers.json 由来。読めなければ既定セット）。
    /// 斜度／高度ルールの供給元であり、GPU のレイヤ uniform／テクスチャの元でもある。
    pub layers: TerrainLayerSet,
    /// レイヤ定義の GPU リソース一式（group3）。地形描画時に G-Buffer パスへ渡す。
    /// パレット（レイヤ番号 4 つ）別のバインドグループをキャッシュする（Terrain T2b）。
    /// `None` のときは地形専用パイプラインへ切り替えない（通常マテリアル描画へフォールバック）。
    pub layer_resources: Option<
        crate::engine::core::renderer::terrain_gbuffer::TerrainLayerResources,
    >,
    /// ペイントブラシで塗る対象レイヤ番号（エディタのレイヤ選択 UI と対応）。
    pub paint_layer: usize,
    /// terrain 専用 undo スタック（末尾が最新）。上限 TERRAIN_UNDO_MAX。
    pub undo_stack: Vec<TerrainEdit>,
    /// terrain 専用 redo スタック（末尾が最新）。undo 実行で積まれ、新規編集で clear される。
    pub redo_stack: Vec<TerrainEdit>,

    // ─── 散布プロップ（Terrain T3。実装は terrain_scatter_ops.rs）──────────────
    /// 散布プロップ定義（assets/terrain/props.json 由来。読めなければ既定セット）。
    pub props: TerrainPropSet,
    /// チャンク → 散布インスタンス配列（.tscatter の実体）。
    pub scatter: HashMap<ChunkCoord, Vec<ScatterInstance>>,
    /// 散布が編集されて未保存のチャンク集合（handle_terrain_save でクリア）。
    pub scatter_dirty: HashSet<ChunkCoord>,
    /// GPU 側の草インスタンスバッファ（プロップ添字 -> バッファ）。再構築待ちなら None。
    pub grass_buffers: HashMap<usize, GrassInstanceBuffer>,
    /// 草 GPU バッファの再構築が必要かどうか（散布が変わったら true）。
    ///
    /// このフラグは草（grass_buffers）と散布モデル（scatter_models）の**両方**の
    /// 再構築トリガを兼ねる。散布データ（`scatter`）は草と model で共有の集合であり、
    /// 別フラグを持つと散布操作 5 か所すべてで二重に立てる必要が出て DRY を損なうため、
    /// 意図的に 1 本に集約している（実際の再構築順序は frame_renderer.rs 側で固定）。
    pub grass_gpu_dirty: bool,
    /// GPU 側の散布モデルリソース（kind=Model プロップ。プロップ添字 -> リソース）。
    ///
    /// 草（`grass_buffers`）と対を成す。草は props の数値から手続き生成するが、model は
    /// `model_path` の実アセットをロードして通常メッシュとしてインスタンス描画する。
    /// GpuModel は ECS アクターに紐付かず本マップが所有する（frame_renderer の
    /// 60 フレーム stale prune の対象外＝散布が変わるまで保持され続ける）。
    pub scatter_models: HashMap<usize, ScatterModelResource>,
    /// 散布モデルのロードに失敗したプロップ（プロップ添字 -> 失敗した model_path）。
    ///
    /// 毎フレーム同じ壊れたパスを読み直して警告を撒かないための記録。props リロードで
    /// model_path が変われば値が一致しなくなるので、自動的に再試行される。
    pub scatter_model_failed: HashMap<usize, String>,
    /// 散布ブラシで使う現在のプロップ添字（エディタの選択と対応）。
    pub scatter_prop: usize,
    /// ルール散布の大域シード（決定性の要。UI から変えられる）。
    pub scatter_seed: u64,

    // ─── 地表カバー場（Terrain I3.1。実装は terrain_cover_ops.rs）───────────────
    /// カバー素材定義（assets/terrain/cover_materials.json 由来。読めなければ既定 3 種）。
    pub cover_materials: crate::engine::terrain::cover::CoverMaterialSet,
    /// cover_materials.json の警告を出したか（毎フレーム同じ警告でログを埋めないため）。
    pub cover_materials_warned: bool,
    /// チャンク → カバー場（.tcover の実体。素材添字＋量の 1 層）。
    pub cover: HashMap<ChunkCoord, crate::engine::terrain::cover::CoverField>,
    /// カバー場が編集されて未保存のチャンク集合（handle_terrain_save でクリア）。
    pub cover_dirty: HashSet<ChunkCoord>,
    /// カバー場を頂点へ焼き直す必要があるチャンク集合（apply_pending_cover が消化）。
    pub cover_pending_apply: HashSet<ChunkCoord>,
    /// 上記のうち **轍スタンプ由来**（接地への応答性が最優先）のチャンク集合。
    ///
    /// 焼き直しはフレーム予算で分散されるが、轍だけは「踏んだ瞬間に跡が付く」ことが
    /// 体感の核なので、予算を無視して必ず今フレームに焼く（`plan_cover_bake` の
    /// `Immediate` 優先度）。繰り越されたチャンクぶんはこの集合にも持ち越される。
    pub cover_immediate_apply: HashSet<ChunkCoord>,
    /// 頂点が動いたため **RT 加速構造（BLAS）を作り直すべき**チャンク集合。
    ///
    /// カバーの焼き直し（`apply_pending_cover`）と、ストローク中の密度ブラシ再メッシュ
    /// （`remesh_chunks(defer_side_effects=true)`）の**両方**がここへ積む。どちらも
    /// 「ラスタの地表が動いたのに BLAS が古い形のまま残る」というまったく同じ不整合であり、
    /// 別々の器で管理する理由が無いため 1 本に統合している。
    /// 消化するのは `flush_rt_blas_prune`（毎フレーム・件数予算つき）。
    ///
    /// 【なぜ必要か（地面が真っ黒になるバグの主因）】
    ///   `apply_cover_to_chunk` は GPU 頂点バッファを直接書き換えるが、
    ///   `RtShadowResources::blas_cache` は `source_path`（＝チャンクの batch_key）を
    ///   キーにした**一度作ったら作り直さない**キャッシュである。よってカバーの変位で
    ///   ラスタの地表が動いても、レイトレが辿る地形は BLAS を作った時点の形のままになる。
    ///   フレームの実行順は「カバー焼き込み（更新）→ BLAS 構築（描画）」なので、
    ///   カバーの載ったシーンをロードすると **BLAS は「雪が積もった高さ」で作られる**。
    ///   その状態で消しゴムを掛けるとラスタの地表だけが素の高さ（既定の雪で 22cm 下）へ戻り、
    ///   RT 影のレイ原点が「まだ在ることになっている雪」の**内側**に沈む。
    ///   レイ原点のバイアスは 2.5cm しかないので、なぞった領域は全面遮蔽＝真っ黒になる
    ///   （密度ブラシで穴を掘ったときの黒落ちと同じ機構。`invalidate_geometry_caches` 参照）。
    ///
    /// 【なぜ「落ち着くまで待つ」のをやめたか】
    ///   初版は「ストローク中でない ＆ 焼き直し待ちが空」の瞬間にまとめて消化していたが、
    ///   ①ストロークをなぞっている最中はずっと黒いまま ②消化処理がカバー焼き直しの
    ///   内側からしか呼ばれず、マウスを離した時点で焼き直し待ちが空だと二度と発火しない、
    ///   という 2 つの欠陥があった（②が「たまに直る／たいてい直らない」の正体）。
    ///   現在は毎フレーム件数予算つきで消化する。捨ててからまだ作り直されていない
    ///   チャンクは TLAS 登録がスキップされる（`rt_shadow::prepare_and_build` は
    ///   `blas_cache.get()==None` を素通りする）ため、**古い形で誤遮蔽するのではなく
    ///   一時的に影を落とさない**という安全側へ倒れる。
    pub rt_blas_prune_pending: HashSet<ChunkCoord>,
    /// チャンク → 地表情報（密度場からの派生キャッシュ。再メッシュで捨てる）。
    pub cover_surface: HashMap<ChunkCoord, crate::engine::terrain::cover::CoverSurface>,
    /// チャンク → カバー適用前のメッシュ基準値（変位の累積を防ぐ。再メッシュで捨てる）。
    pub cover_base_mesh: HashMap<ChunkCoord, super::terrain_cover_ops::CoverBaseMesh>,
    /// マスク画像パス → デコード済みグレースケール（読み込み失敗も「無効」として記録する）。
    ///
    /// 【カバー専用ではなく地形共通のキャッシュである点に注意】
    ///   カバーエミッタの `TextureMask` 範囲・轍スタンプの `Texture` 形状に加え、
    ///   地形ペイント系ブラシの形状マスク（`brush_mask_path`）も同じ器を使う。
    ///   デコード結果（`CoverMask`）は幅・高さ・グレー値だけを持つ汎用データであり、
    ///   同じ画像を 2 つの用途で二重にデコードする理由が無いためである。
    pub mask_cache: HashMap<String, crate::engine::terrain::cover::CoverMask>,
    /// 地形ペイント系ブラシに適用する形状マスク画像のパス（空文字＝未指定）。
    ///
    /// 【ここ（TerrainState）に持たせる理由】
    ///   半径・強度と同じ「ツールの現在設定」であり、ブラシ 1 発ごとに IPC で
    ///   運ぶ種類の情報ではない（Windows のファイルパスはカンマを含みうるため、
    ///   カンマ区切りのブラシコマンドへ足すこともできない）。
    ///   エディタは `TERRAIN_BRUSH_MASK:{path}` で設定・解除だけを送る。
    ///
    /// 対象は **レイヤペイントブラシ（TERRAIN_PAINT）とカバーブラシ
    /// （TERRAIN_COVER_BRUSH の塗り／消去）** の 2 つ。密度ブラシは対象外
    /// （形状マスクは「面に絵を貼る」道具であり、3D の掘削とは相性が悪い）。
    pub brush_mask_path: String,
    /// Edit の連続シミュレート（インスペクタのシミュレートボタン）が動作中か。
    pub cover_sim_running: bool,
    /// 頂点焼き直しの間引きタイマー（秒）。
    pub cover_apply_timer: f32,
    /// 積算ティックの未消化経過時間（秒）。
    ///
    /// `CoverMaterialSet::accumulate_interval_sec()` に達したフレームで、
    /// **貯めた全量をまとめて**積算へ渡して 0 へ戻す（`advance_accumulate_tick`）。
    /// 貯めてから一括で入れるため、積算総量は毎フレーム積算していたときと一致する。
    pub cover_accum_timer: f32,
    /// Play 開始時に退避した Edit のカバー場（Stop で書き戻す＝Play 中の積算は揮発）。
    pub cover_play_snapshot: Option<super::terrain_cover_ops::CoverFieldMap>,
    /// 現在のブラシストロークで**カバーブラシが触れた**チャンクの「編集前」カバー場。
    ///
    /// 密度側の `stroke_before` と対を成し、`handle_terrain_stroke_end` で
    /// `TerrainEdit::cover_before` として消費される（＝1 ストローク = 1 Undo 単位）。
    /// カバー場が無かったチャンクは空の `CoverField` を控える
    /// （「場が無い」と「全テクセル量 0」は保存規約の上で等価であるため）。
    ///
    /// エミッタのシミュレート用セッション（`cover_undo_session_before`）とは
    /// **別物**である。あちらはメイン履歴、こちらは terrain 専用スタックへ載る。
    pub cover_stroke_before: HashMap<ChunkCoord, CoverField>,
    /// カバー場編集セッションの開始時スナップショット（None = セッション未開始）。
    ///
    /// 「1 操作 = 1 Undo 単位」にするための控えである。連続シミュレートは
    /// 「開始〜停止」の間ぜんぶを 1 セッション（= undo スタックの 1 エントリ）として扱うため、
    /// 開始時点の全カバー場をここへ複製し、停止時に現在値と突き合わせて差分を取る。
    pub cover_undo_session_before: Option<super::terrain_cover_ops::CoverFieldMap>,
    /// 轍スタンプ源（InteractionSource）の追跡情報（I3.2）。
    ///
    /// キーはアクタ DFS 連番＋スロット添字（`interaction::source_key`）。
    /// 前フレーム位置と直近の進行方向を持ち、
    ///   ・動いたか（＝踏んだか）の判定
    ///   ・テクスチャ形状を回す向き
    /// に使う。Play の開始・終了で必ず捨てる（揮発するゲーム状態）。
    pub cover_stamp_tracks: HashMap<u64, super::terrain_cover_ops::CoverStampTrack>,
    /// 轍スタンプ源の「今この瞬間の作用状況」（I3.2 デバッグ描画）。
    ///
    /// キーは `cover_stamp_tracks` と同じソースキー。選択中アクターのギズモを
    /// 状態に応じて色替えするためだけに持つ、純粋な観測用の状態である
    /// （シミュレーション結果には一切影響しない）。
    /// `cover_stamp_tracks` と同じく Play の開始・終了で必ず捨てる。
    pub cover_stamp_debug: HashMap<u64, super::terrain_cover_ops::CoverStampDebug>,

    // ─── 物理コリジョン（地形の静的トライメッシュコライダー）─────────────────
    /// チャンク → そのチャンクに対応する物理コライダーの entity_id。
    ///
    /// 地形コライダーは ECS の `ColliderComponent` ではなく terrain 側で内部管理する。
    /// Play 開始（`start_physics`）で全チャンクぶんを静的コライダーとして物理ワールドへ
    /// 登録し、変形（remesh）のたびに Remove→Add で作り直す。その対応付けキーがこれ。
    /// 物理停止中は使われず、次の Play で同じチャンクは同じ id を再利用する。
    pub chunk_collider_ids: HashMap<ChunkCoord, u64>,
    /// 地形コライダー entity_id の単調割り当てカウンタ（次に配る値）。
    /// アクター DFS の entity_id 空間（1 始まり・アクター数ぶん）と絶対に衝突しない
    /// 高位ベース（`TERRAIN_COLLIDER_ENTITY_BASE`）から採番する。
    pub next_terrain_collider_id: u64,

    // ─── LOD 遷移 GPU リソースの遅延退役（移動時スパイク対策）─────────────────
    /// LOD 遷移で差し替えた旧チャンク GPU リソースの退役キュー。
    ///
    /// `remesh_chunks(defer_gpu_release=true)`（＝毎フレームの `tick_terrain_lod` 経路）が、
    /// 旧 `GpuModel`／`InstancedModelBatch` をその場で drop せずここへ積む。`process_terrain_gpu_retire`
    /// がフレーム先頭（snatch read lock 非保持の安全点）で `TERRAIN_GPU_RETIRE_FRAMES` フレーム経過分を
    /// drop → `poll(Poll)` で確定する。狙いは `poll(Wait)` の GPU 同期ストール（80〜130ms）の排除。
    /// 各エントリは `(退役フレーム番号, 旧 GpuModel, 旧 InstancedModelBatch)`。
    pub gpu_retire_queue: std::collections::VecDeque<(u64, Option<GpuModel>, Option<InstancedModelBatch>)>,
    /// 遅延退役の判定に使う単調フレームカウンタ（`process_terrain_gpu_retire` が毎フレーム +1）。
    pub gpu_retire_frame: u64,
}

/// 地形コライダー用 entity_id のベース値。
///
/// アクターの物理 entity_id は DFS カウンタ（1 始まり、シーンのアクター数ぶんで現実的に
/// 数百万未満）である。地形コライダーはそれと同じ物理ワールドに同居するため、両者の id が
/// 決して衝突しないよう、十分に高い位（2^48）から採番する。
const TERRAIN_COLLIDER_ENTITY_BASE: u64 = 1 << 48;

impl Default for TerrainState {
    fn default() -> Self {
        Self {
            settings: TerrainSettings::default(),
            chunks: HashMap::new(),
            chunk_slot_entity: HashMap::new(),
            chunk_lod: HashMap::new(),
            scene_name: String::new(),
            dirty: HashSet::new(),
            pending_remesh: HashSet::new(),
            pending_paint: HashSet::new(),
            chunk_vertex_edges: HashMap::new(),
            brush_preview: None,
            stroke_active: false,
            stroke_before: HashMap::new(),
            stroke_deferred_chunks: HashSet::new(),
            last_brush_apply: None,
            perf_collider_measuring: false,
            perf_collider_mirror: Duration::ZERO,
            perf_collider_send: Duration::ZERO,
            layers: TerrainLayerSet::default(),
            layer_resources: None,
            paint_layer: 0,
            undo_stack: Vec::new(),
            redo_stack: Vec::new(),

            // ─── 散布（Terrain T3）───
            //   props は「空」で始める。実際の props.json 読み込みは
            //   最初の散布コマンド（ensure_terrain_props）まで遅延させる。
            //   TerrainState::default() は地形リセットのたびに呼ばれるため、
            //   ここでファイル IO を走らせると無駄が多いという判断である。
            props: TerrainPropSet { props: Vec::new() },
            scatter: HashMap::new(),
            scatter_dirty: HashSet::new(),
            grass_buffers: HashMap::new(),
            grass_gpu_dirty: false,
            scatter_models: HashMap::new(),
            scatter_model_failed: HashMap::new(),
            scatter_prop: 0,
            scatter_seed: DEFAULT_SCATTER_SEED,

            cover_materials: crate::engine::terrain::cover::CoverMaterialSet::default(),
            cover_materials_warned: false,
            cover: HashMap::new(),
            cover_dirty: HashSet::new(),
            cover_pending_apply: HashSet::new(),
            cover_immediate_apply: HashSet::new(),
            rt_blas_prune_pending: HashSet::new(),
            cover_surface: HashMap::new(),
            cover_base_mesh: HashMap::new(),
            mask_cache: HashMap::new(),
            // ブラシ形状マスクは既定で未指定＝従来どおりの円形フォールオフ。
            brush_mask_path: String::new(),
            cover_sim_running: false,
            cover_apply_timer: 0.0,
            cover_accum_timer: 0.0,
            cover_play_snapshot: None,
            cover_stroke_before: HashMap::new(),
            cover_undo_session_before: None,
            cover_stamp_tracks: HashMap::new(),
            cover_stamp_debug: HashMap::new(),
            chunk_collider_ids: HashMap::new(),
            next_terrain_collider_id: TERRAIN_COLLIDER_ENTITY_BASE,

            // 遅延退役キューは空・フレーム 0 から始める。地形リセット（default 再生成）で
            // 破棄されるが、そのタイミングでは対象 GpuModel は既に GPU 完了済みのため安全。
            gpu_retire_queue: std::collections::VecDeque::new(),
            gpu_retire_frame: 0,
        }
    }
}

/// ルール散布の既定グローバルシード。
///
/// 0 だと「未設定」と紛らわしいので、意味のない固定値を置く。
/// これを変えると既存シーンの草が全て生え変わるため変更禁止。
const DEFAULT_SCATTER_SEED: u64 = 0x5EED_5CA7_7E12_0001;

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

/// グローバルサンプル座標を所有する既存チャンクを探し、`(チャンク, ローカル添字)` を返す。
///
/// 主チャンクが存在すればそれを、無ければ境界重複する近傍チャンクを試す。
/// どのチャンクも存在しない（地形外）場合は `None`。
///
/// 「地形外を AIR とみなす」か「地形外だと分かりたい」かは呼び出し側で分かれるため、
/// 所有チャンク探索だけをここへ切り出して両者で共有する（DRY）。
///
/// `pub(super)`: 散布のレイヤ重み判定（terrain_scatter_ops.rs）が、ワールド座標の
/// 最近傍サンプルから手ペイント情報（BlendSlots / paint_amount）を読むために使う。
#[inline]
pub(super) fn find_owner<'a>(
    chunks: &'a HashMap<ChunkCoord, TerrainChunkData>,
    cells: i32,
    gx: i32,
    gy: i32,
    gz: i32,
) -> Option<(&'a TerrainChunkData, usize, usize, usize)> {
    let (ox, nx) = axis_owners(gx, cells);
    let (oy, ny) = axis_owners(gy, cells);
    let (oz, nz) = axis_owners(gz, cells);
    // primary 組み合わせ（[0][0][0]）を最初に試すため、そのままの順で走査する。
    for i in 0..nx {
        for j in 0..ny {
            for k in 0..nz {
                let coord = ChunkCoord::new(ox[i].0, oy[j].0, oz[k].0);
                if let Some(chunk) = chunks.get(&coord) {
                    return Some((chunk, ox[i].1, oy[j].1, oz[k].1));
                }
            }
        }
    }
    None
}

/// グローバルサンプル座標の密度を読む（terrain ライブラリと同じ所有規約）。
///
/// どのチャンクも存在しない（地形外）場合は `clamp`（＝AIR 側）を返す。
fn read_global_impl(
    chunks: &HashMap<ChunkCoord, TerrainChunkData>,
    cells: i32,
    clamp: f32,
    gx: i32,
    gy: i32,
    gz: i32,
) -> f32 {
    match find_owner(chunks, cells, gx, gy, gz) {
        Some((chunk, lx, ly, lz)) => chunk.sample(lx, ly, lz),
        // 地形外 = AIR（clamp は density_clamp = 正の大きな値）。
        None => clamp,
    }
}

/// グローバルサンプル座標の (密度, 手ペイントスロット, ペイント量) を読む。
/// 地形外（どのチャンクも所有しない）の場合は `None`。
///
/// チャンク追加時に「既存チャンクと共有する境界サンプル」を引き写すために使う。
/// 「値が無い」ことを AIR 相当の既定値と区別する必要があるため、`read_global_impl`
/// ではなくこちらを使う（既定値で上書きすると継ぎ目に段差が出る）。
fn try_read_sample_global(
    chunks: &HashMap<ChunkCoord, TerrainChunkData>,
    cells: i32,
    gx: i32,
    gy: i32,
    gz: i32,
) -> Option<(f32, BlendSlots, f32)> {
    let (chunk, lx, ly, lz) = find_owner(chunks, cells, gx, gy, gz)?;
    Some((
        chunk.sample(lx, ly, lz),
        chunk.paint_slots(lx, ly, lz),
        chunk.paint_amount(lx, ly, lz),
    ))
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

/// グローバルサンプル座標の (手ペイント重み, ペイント量) を読む。
///
/// 密度の read_global_impl と同じ所有規約（境界サンプルは複数チャンクが重複所有）。
/// どのチャンクも存在しない（地形外）場合は「未ペイント」を返す。
fn read_paint_global_impl(
    chunks: &HashMap<ChunkCoord, TerrainChunkData>,
    cells: i32,
    gx: i32,
    gy: i32,
    gz: i32,
) -> (BlendSlots, f32) {
    match find_owner(chunks, cells, gx, gy, gz) {
        Some((chunk, lx, ly, lz)) => (chunk.paint_slots(lx, ly, lz), chunk.paint_amount(lx, ly, lz)),
        // 地形外 = 未ペイント（＝ルール自動生成に従う）。重みは全 0 で返す。
        None => (
            BlendSlots { index: [0; TERRAIN_BLEND_SLOTS], weight: [0.0; TERRAIN_BLEND_SLOTS] },
            0.0,
        ),
    }
}

/// グローバルサンプル座標へ (手ペイント重み, ペイント量) を書く。
///
/// 境界で重複する全チャンクへ同一値を書き込む（同期）。これを怠るとチャンク境界で
/// レイヤの塗り分けが食い違い、継ぎ目に色の段差が出る。
fn write_paint_global_impl(
    chunks: &mut HashMap<ChunkCoord, TerrainChunkData>,
    cells: i32,
    gx: i32,
    gy: i32,
    gz: i32,
    slots: &BlendSlots,
    amount: f32,
) {
    let (ox, nx) = axis_owners(gx, cells);
    let (oy, ny) = axis_owners(gy, cells);
    let (oz, nz) = axis_owners(gz, cells);
    for i in 0..nx {
        for j in 0..ny {
            for k in 0..nz {
                let coord = ChunkCoord::new(ox[i].0, oy[j].0, oz[k].0);
                if let Some(chunk) = chunks.get_mut(&coord) {
                    chunk.set_paint_slots(ox[i].1, oy[j].1, oz[k].1, slots);
                    chunk.set_paint_amount(ox[i].1, oy[j].1, oz[k].1, amount);
                }
            }
        }
    }
}

/// ワールド座標 `p` の密度をトライリニア補間で求める（レイマーチ用）。
///
/// `pub(super)`: 散布の接地判定（terrain_scatter_ops.rs の `ScatterField` 実装）が
/// **同一の密度サンプリング**を使う必要があるため公開している。
/// 別実装にすると「ブラシは当たったのに草が生えない」ずれが出る。
pub(super) fn sample_density_world(
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

/// ペイントブラシ（terrain::paint::apply_paint）用の PaintField 実装。
///
/// 密度用の SampleField と同じ FieldView に相乗りさせている（設定とチャンク集合という
/// 依存が完全に同じで、分けると分割借用のボイラープレートが二重になるだけのため）。
impl<'a> PaintField for FieldView<'a> {
    fn settings(&self) -> &TerrainSettings {
        self.settings
    }

    fn read_paint_global(&self, gx: i32, gy: i32, gz: i32) -> (BlendSlots, f32) {
        let cells = self.settings.chunk_cells as i32;
        read_paint_global_impl(self.chunks, cells, gx, gy, gz)
    }

    fn write_paint_global(&mut self, gx: i32, gy: i32, gz: i32, slots: &BlendSlots, amount: f32) {
        let cells = self.settings.chunk_cells as i32;
        write_paint_global_impl(self.chunks, cells, gx, gy, gz, slots, amount);
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
    layers: &TerrainLayerSet,
    ctx: &DrawContext,
    coord: ChunkCoord,
) -> Option<(Arc<Model>, Option<GpuModel>, Option<InstancedModelBatch>, Arc<Vec<TerrainVertexEdge>>)> {
    // CPU メッシュ生成（純粋部）とアップロード（GPU 部）は分離してある。
    // ここは「1 チャンクを単独で作り直す」旧来の呼び出し側（初期化・チャンク追加・
    // シーンロード復元）向けの薄いラッパで、両者を続けて実行するだけ。
    //
    // 4 番目の戻り値は頂点の由来辺（`TerrainState::chunk_vertex_edges` へ入れる）。
    // これを登録しておくと、そのチャンクは最初のペイントから高速パスに乗れる。
    // 単発ビルド（初期化・チャンク追加・ロード復元）は常に LOD0 で作る。
    // 遠近に応じた LOD 切替は毎フレームの `tick_terrain_lod` が担う。
    let (model, is_empty, edges) = build_chunk_cpu_model(chunks, settings, layers, coord, 0)?;
    let (gpu, batch) = upload_chunk_model(ctx, &model, is_empty);
    Some((model, gpu, batch, edges))
}

/// 1 チャンクの CPU メッシュだけを生成し、`(CPU モデル, メッシュが空か)` を返す。
///
/// 【純粋関数であること — rayon 並列化の前提】
///   引数は共有参照（`&HashMap` / `&TerrainSettings` / `&TerrainLayerSet`）のみで、
///   グローバル状態も GPU リソースも一切触らない。あるチャンクの結果は他チャンクの
///   結果に依存せず（近傍サンプルは「編集後の密度場」を読むだけで、他チャンクの
///   *メッシュ* には依存しない）、副作用も無い。
///   よって `par_iter().map(...)` で並列実行しても、各要素の値は逐次実行と完全に一致する。
///   さらに rayon の `IndexedParallelIterator` は `collect::<Vec<_>>()` で**入力順を保存する**
///   ことを保証するため、出力 Vec の並びもスレッド数・スケジューリングに依らず決定的である。
///
/// 継ぎ目の勾配（法線）を隣接チャンクと連続させるため、`generate` の neighbor_sampler で
/// グローバル密度場を読む（チャンク境界の外側 1 サンプルも正しい値を返す）。
///
/// 戻り値の 2 番目 `is_empty` は「三角形 0 個（全 AIR / 全 SOLID）」を表す。
/// `true` のチャンクは GPU リソースを一切作ってはならない（理由は `build_chunk_render` の
/// ドキュメント【空メッシュ対策】を参照）。
fn build_chunk_cpu_model(
    chunks: &HashMap<ChunkCoord, TerrainChunkData>,
    settings: &TerrainSettings,
    layers: &TerrainLayerSet,
    coord: ChunkCoord,
    lod: u8,
) -> Option<(Arc<Model>, bool, Arc<Vec<TerrainVertexEdge>>)> {
    let chunk = chunks.get(&coord)?;
    let cells = settings.chunk_cells as i32;
    let clamp = settings.density_clamp;
    // このチャンクのローカルサンプル (lx,ly,lz) → グローバルサンプル座標 = coord*cells + local。
    let base = [coord.x * cells, coord.y * cells, coord.z * cells];

    // ─── LOD メッシュの選択 ──────────────────────────────────────────────
    //   lod>=1: 間引き＋スカートの低解像度メッシュ（terrain::lod）。stride が chunk_cells を
    //           割り切らない構成では 1 段細かい LOD へ落とし、最終的に LOD0（下）へ帰着する。
    //   lod==0: 従来経路（隣接サンプラ付きフル解像度 MC）。境界勾配が隣と連続し水密。
    //   低解像度メッシュは頂点の「由来辺」を持たない（ペイント高速パス非対象）。編集は
    //   カメラ至近＝LOD0 のチャンクでしか起きないため、これは実害にならない。
    let mut lod_mesh: Option<terrain::TerrainMesh> = None;
    let mut try_lod = lod;
    while try_lod >= 1 {
        let stride = terrain::stride_for_lod(try_lod as usize);
        if let Some(m) = terrain::generate_lod_mesh(chunk, settings, stride) {
            lod_mesh = Some(m);
            break;
        }
        try_lod -= 1;
    }

    let (mut mesh, is_lod0) = match lod_mesh {
        Some(m) => (m, false),
        None => {
            // LOD0（フル解像度・隣接サンプラで境界勾配を連続化）。
            let m = terrain::generate(chunk, settings, |lx, ly, lz| {
                read_global_impl(chunks, cells, clamp, base[0] + lx, base[1] + ly, base[2] + lz)
            });
            (m, true)
        }
    };

    // 由来辺（頂点がどの辺のどこで生まれたか）は LOD0 のときだけ意味を持つ。
    // ペイント高速パスがマーチングキューブスを再実行せずにスプラットを引き直すための唯一の手掛かり。
    // `Vec` を丸ごと move して以後の再確保・コピーを避ける（メッシュ側では使わない）。
    // LOD>0 のメッシュは由来辺を持てない（間引き・スカートで頂点が MC 辺と 1:1 対応しない）ため
    // 空にする。呼び出し側（apply_terrain_paint_colors）は長さ不一致でフル再メッシュへ落ちる。
    let edges = if is_lod0 {
        Arc::new(std::mem::take(&mut mesh.edges))
    } else {
        Arc::new(Vec::new())
    };
    // レイヤ重みはワールド Y（高度ルール）を要するため、チャンク原点を渡す。
    // 第 2 戻り値はこのチャンクのレイヤパレット（頂点カラー各成分が指すレイヤ番号）。
    // パレットは model.materials[0].terrain_palette へも載っており、GPU へは
    // upload_model → GpuMaterial → gbuffer の group3 選択という経路で運ばれる。
    // 呼び出し側で個別に持つ必要はないため、ここでは戻り値を使わない。
    let (model, _palette) = terrain_mesh_to_model(
        &mesh,
        &format!("terrain_{}_{}_{}", coord.x, coord.y, coord.z),
        coord.world_origin(settings),
        layers,
    );
    Some((Arc::new(model), mesh.indices.is_empty(), edges))
}

/// 1 チャンクの**物理コライダー形状**（チャンクローカル座標の三角形メッシュ）を生成する。
///
/// 描画メッシュ（`build_chunk_cpu_model`）と同じ `terrain::generate`＋隣接サンプラを使うため、
/// コライダーは見た目のメッシュと頂点単位で一致する（＝めり込み・浮きが原理的に出ない）。
/// 洞窟・オーバーハングを扱うので heightfield ではなく三角形メッシュを採る。
///
/// 頂点は共有頂点のまま `TriangleMeshIndexed` に載せる（三角形ごとの複製をしないので
/// 地形の大規模メッシュでもメモリ効率が良い）。三角形が 0 個（全 AIR／全 SOLID の
/// 空チャンク）の場合は `None` を返し、呼び出し側はコライダーを登録しない。
///
/// 純粋関数（共有参照のみ・副作用なし）なのでユニットテストできる。
fn build_chunk_collider_shape(
    chunks: &HashMap<ChunkCoord, TerrainChunkData>,
    settings: &TerrainSettings,
    coord: ChunkCoord,
) -> Option<ColliderShape> {
    let chunk = chunks.get(&coord)?;
    let cells = settings.chunk_cells as i32;
    let clamp = settings.density_clamp;
    // ローカルサンプル (lx,ly,lz) → グローバル = coord*cells + local（描画経路と同一）。
    let base = [coord.x * cells, coord.y * cells, coord.z * cells];
    let mesh = terrain::generate(chunk, settings, |lx, ly, lz| {
        read_global_impl(chunks, cells, clamp, base[0] + lx, base[1] + ly, base[2] + lz)
    });
    // 空メッシュ（三角形 0）はコライダーを作らない。
    if mesh.indices.is_empty() {
        return None;
    }
    // 平坦なインデックス列（3 個で 1 三角形）を三つ組へまとめる。
    // マーチングキューブスは常に 3 の倍数個のインデックスを出すため端数は出ない。
    let indices: Vec<[u32; 3]> = mesh
        .indices
        .chunks_exact(3)
        .map(|t| [t[0], t[1], t[2]])
        .collect();
    Some(ColliderShape::TriangleMeshIndexed { vertices: mesh.positions, indices })
}

/// **既に生成済みの描画メッシュ（`Model`）から**物理コライダー形状を取り出す。
///
/// 【なぜこれが本命か — MC 二重実行の撤廃】
///   地形チャンクの描画メッシュ（`build_chunk_cpu_model` → `terrain_mesh_to_model`）は
///   マーチングキューブス（`terrain::generate`）の出力 `mesh.positions` / `mesh.indices` を
///   **そのまま**保持する（`terrain_mesh_build.rs`: `position: *pos` と `mesh.indices.clone()`）。
///   一方 `build_chunk_collider_shape` は同じ MC を**もう一度**回して同一の頂点・インデックスを
///   作り直していた。地形は初期化・ロード時点で全チャンクがメッシュ化済みなので、その
///   `Model` から頂点位置とインデックスを写すだけで、MC を一切走らせずに同一形状の
///   コライダーが得られる（実測 MC は cells=32 で約 53ms/チャンク・cells=64 で約 221ms/チャンク
///   と支配的なので、この二重実行の撤廃が `start_physics` のフリーズ解消に直結する）。
///
/// 地形チャンクの `Model` は「1 メッシュ・1 プリミティブ・スキンなし・LOD/メッシュレット非分離」
/// で構築される（`build_terrain_model`）。頂点位置はチャンクローカル座標であり、ワールド配置は
/// `PhysicsObject.position`（＝チャンク原点）が担う。三角形が 0 個（空メッシュチャンク）の
/// 場合は `None` を返し、呼び出し側はコライダーを登録しない（`build_chunk_collider_shape` と同挙動）。
///
/// メッシュレット生成等で将来頂点順序が変わっても、インデックスは同じ頂点配列を指すため
/// トライメッシュの表す**面は不変**であり、コライダーとしての正しさは保たれる。
fn collider_shape_from_model(model: &Model) -> Option<ColliderShape> {
    // 地形モデルは 1 メッシュ・1 プリミティブ（`build_terrain_model`）。
    let prim = model.meshes.first()?.primitives.first()?;
    // 空メッシュ（三角形 0）はコライダーを作らない。
    if prim.indices.is_empty() {
        return None;
    }
    // 頂点位置（チャンクローカル）を写す。
    let vertices: Vec<[f32; 3]> = prim.vertices.iter().map(|v| v.position).collect();
    // 平坦なインデックス列（3 個で 1 三角形）を三つ組へまとめる。
    // マーチングキューブスは常に 3 の倍数個のインデックスを出すため端数は出ない。
    let indices: Vec<[u32; 3]> = prim
        .indices
        .chunks_exact(3)
        .map(|t| [t[0], t[1], t[2]])
        .collect();
    Some(ColliderShape::TriangleMeshIndexed { vertices, indices })
}

/// 地形チャンク用の静的トライメッシュ `PhysicsObject` を組み立てる。
///
/// 地形メッシュアクターは「チャンク原点への平行移動のみ」（回転・スケール無し。
/// `spawn_chunk_actor` の Transform 参照）なので、コライダーも回転単位・スケール 1・
/// オフセット 0 で、位置＝チャンクのワールド原点にする。`rigidbody = None` により
/// RigidBody 無しの Static コライダー（ワールド固定）として登録される。
fn terrain_collider_object(entity_id: u64, position: [f32; 3], shape: ColliderShape) -> PhysicsObject {
    PhysicsObject {
        entity_id,
        position,
        rotation: [0.0, 0.0, 0.0, 1.0],
        scale: [1.0, 1.0, 1.0],
        collider: shape,
        collider_offset: [0.0, 0.0, 0.0],
        rigidbody: None,
        is_trigger: false,
        // レイヤ 1（既定コライダーと同じ）／マスク 0（全レイヤと衝突）。
        physics_layer: 1,
        layer_mask: 0,
        // 地形はキャラクターコントローラーではない（衝突相手側の Static コライダー）。
        is_character_controller: false,
    }
}

/// CPU モデルを GPU へアップロードし、`(GpuModel?, インスタンスバッチ?)` を返す。
///
/// 【シリアル固定】`DrawContext` は `RefCell`（`rt_shadow` / `model_cache` 等）を内部に持つため
/// `Sync` ではなく、`&DrawContext` を複数スレッドへ配れない。よってアップロードは並列化せず、
/// 並列化するのは純粋 CPU 部（`build_chunk_cpu_model`）だけに限る。
///
/// `is_empty` が真のチャンクは GPU リソースを作らず `(None, None)` を返す
/// （サイズ 0 バッファ由来のパニック回避。詳細は `build_chunk_render` のドキュメント）。
fn upload_chunk_model(
    ctx: &DrawContext,
    model: &Arc<Model>,
    is_empty: bool,
) -> (Option<GpuModel>, Option<InstancedModelBatch>) {
    if is_empty {
        return (None, None);
    }
    // オーバーライド無しでアップロード（source_path とビット一致のバッチキーになる）。
    let gpu = ctx.upload_model_with_overrides(model, &[]);
    let batch = ctx.create_instanced_batch(model, 1);
    (Some(gpu), Some(batch))
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
        // 地形チャンクはセマンティックタグを持たない（合成側は地形を別経路で判別する）。
        render_tag: crate::engine::core::renderer::surface_id::RENDER_TAG_NONE,
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

/// 追加した新規チャンクの**境界サンプル**を、既存チャンクが持つ値で上書きして
/// 継ぎ目を一致させる（密度・手ペイントスロット・ペイント量の 3 点すべて）。
///
/// 【なぜ必要か】
///   グローバルサンプル座標の規約上、隣り合うチャンクは接する面のサンプルを
///   **重複所有**する（`axis_owners` 参照）。ブラシ編集は `write_global_impl` が
///   全所有チャンクへ同じ値を書くのでこの重複は常に一致しているが、新しく作った
///   チャンクは平地の初期値を持つため、隣が編集済み（盛り／掘り／ペイント）だと
///   同じ座標のサンプルが 2 つの異なる値を持つことになる。
///   結果、マーチングキューブスが両側で別々の等値面を出して継ぎ目に穴／段差が出る。
///   そこで「既存側の値が正」として新規チャンクへ引き写し、重複所有の不変条件を回復する。
///
/// 【呼び出しタイミング】
///   `existing` には**まだ新規チャンクを入れない**こと。入れてしまうと、新規チャンク
///   自身が主所有者として見つかり、自分の初期値で自分を上書きするだけの無意味な処理になる。
///
/// 走査するのは 6 面の境界サンプル（ローカル添字が 0 または cells のもの）のみ。
/// 内部サンプルは他チャンクと共有しないため触らない。
fn sync_new_chunk_boundary(
    existing: &HashMap<ChunkCoord, TerrainChunkData>,
    settings: &TerrainSettings,
    coord: ChunkCoord,
    data: &mut TerrainChunkData,
) {
    let cells = settings.chunk_cells as i32;
    let samples = settings.samples_per_axis();
    // このチャンクのローカル添字 → グローバルサンプル座標のオフセット。
    let base = [coord.x * cells, coord.y * cells, coord.z * cells];

    for lz in 0..samples {
        let on_z = lz == 0 || lz as i32 == cells;
        for ly in 0..samples {
            let on_y = ly == 0 || ly as i32 == cells;
            for lx in 0..samples {
                let on_x = lx == 0 || lx as i32 == cells;
                // どの軸でも境界に接していないサンプルは他チャンクと共有しない。
                if !(on_x || on_y || on_z) {
                    continue;
                }
                let g = [base[0] + lx as i32, base[1] + ly as i32, base[2] + lz as i32];
                // 既存チャンクがこのサンプルを所有していれば、その値を正として引き写す。
                if let Some((density, slots, amount)) =
                    try_read_sample_global(existing, cells, g[0], g[1], g[2])
                {
                    data.set_sample(lx, ly, lz, density);
                    data.set_paint_slots(lx, ly, lz, &slots);
                    data.set_paint_amount(lx, ly, lz, amount);
                }
            }
        }
    }
}

/// チャンク追加要求（X/Z 範囲・両端含む）から「まだ存在しないチャンク座標」を列挙する。
///
/// 縦方向（Y）は設定の `ground_chunk_y_min..=ground_chunk_y_max` を敷き詰める。
/// **既存チャンクは結果に含めない**＝呼び出し側が上書きしようがないため、
/// 「既存地形の温存」がこの関数の戻り値だけで保証される（純粋関数なのでテストできる）。
///
/// 範囲は反転指定（min > max）でも受け付けられるよう内部で正規化する。
fn collect_new_chunk_coords(
    existing: &HashMap<ChunkCoord, TerrainChunkData>,
    settings: &TerrainSettings,
    min_x: i32,
    min_z: i32,
    max_x: i32,
    max_z: i32,
) -> Vec<ChunkCoord> {
    let (lo_x, hi_x) = (min_x.min(max_x), min_x.max(max_x));
    let (lo_z, hi_z) = (min_z.min(max_z), min_z.max(max_z));
    let mut out = Vec::new();
    for x in lo_x..=hi_x {
        for z in lo_z..=hi_z {
            for y in settings.ground_chunk_y_min..=settings.ground_chunk_y_max {
                let coord = ChunkCoord::new(x, y, z);
                if !existing.contains_key(&coord) {
                    out.push(coord);
                }
            }
        }
    }
    out
}

/// 1 チャンク分のアクター（チャンクフォルダ + メッシュアクター）を組み立て、
/// World へ Transform / ModelComponent / TerrainChunkComponent を挿入する。
///
/// 戻り値は `(親へぶら下げるフォルダアクター, ModelComponent スロットの entity)`。
/// 呼び出し側が terrain ルートへ `add_child` して、スロット entity を
/// `chunk_slot_entity` へ登録する。
///
/// 地形の新規構築（`build_terrain_with`）とチャンク追加（`handle_terrain_add_chunks`）の
/// 双方から使う共通経路（両者でアクター構造が食い違わないよう 1 箇所に集約する）。
fn spawn_chunk_actor(
    world: &mut crate::engine::ecs::World,
    scene_name: &str,
    settings: &TerrainSettings,
    coord: ChunkCoord,
    model: Arc<Model>,
    gpu: Option<GpuModel>,
    batch: Option<InstancedModelBatch>,
) -> (Actor, Entity) {
    // ── チャンクフォルダノード（描画なし・整理用・Transform 非保持）──
    let folder_entity = world.spawn();
    let mut folder = Actor::new_folder(
        folder_entity,
        format!("chunk_{}_{}_{}", coord.x, coord.y, coord.z),
    );

    // ── メッシュアクター（チャンク原点に配置）──
    let mesh_entity = world.spawn();
    let mesh_tf = ActorTransform {
        position: coord.world_origin(settings),
        rotation: [0.0, 0.0, 0.0],
        scale: [1.0, 1.0, 1.0],
    };
    let world_mat = mesh_tf.to_mat4();
    world.insert(mesh_entity, mesh_tf);
    let mut mesh_actor = Actor::new(mesh_entity, TERRAIN_MESH_NAME);

    // ── ModelComponent スロット（合成 source_path で描画＋RT キャスタ化）──
    let mc_slot = world.spawn();
    let source_path = terrain_source_path(scene_name, coord);
    world.insert(
        mc_slot,
        make_terrain_model_component(source_path, model, gpu, batch, world_mat),
    );
    mesh_actor.add_slot_typed::<ModelComponent>(
        TERRAIN_MODEL_SLOT_NAME, ComponentKind::Model, mc_slot,
    );

    // ── TerrainChunkComponent スロット（座標＋.tvox リンク・ロード時復元の手掛かり）──
    let tc_slot = world.spawn();
    world.insert(
        tc_slot,
        TerrainChunkComponent {
            chunk_x: coord.x,
            chunk_y: coord.y,
            chunk_z: coord.z,
            tvox_path: tvox_virtual_path(scene_name, coord),
        },
    );
    mesh_actor.add_slot_typed::<TerrainChunkComponent>(
        TERRAIN_CHUNK_SLOT_NAME, ComponentKind::TerrainChunk, tc_slot,
    );

    folder.add_child(mesh_actor);
    (folder, mc_slot)
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
    /// 地形ツリー（root/フォルダ/メッシュアクター）を「地面全チャンクの密度を作る関数」を
    /// 差し替え可能な形で構築する共通経路。
    ///
    /// handle_terrain_init（平坦地面）と handle_terrain_heightmap（ハイトマップ起伏）の
    /// 両方から使う。既存の terrain ルートを冪等に除去してから作り直し、フェーズ1（密度充填）
    /// →フェーズ2（メッシュ化＋GPU アップロード）→フェーズ3（アクターツリー構築）の順に進める。
    ///
    /// `fill`: チャンク座標 → そのチャンクの初期密度データを返す関数（from_ground_plane や
    /// from_fn(...) をラップして渡す）。
    ///
    /// 戻り値: draw_ctx が無い（GPU 未初期化）場合は何もせず false を返す。成功したら true。
    /// IPC 応答（TERRAIN_INIT_OK / TERRAIN_HEIGHTMAP_OK 等）は呼び出し側が送る。
    fn build_terrain_with<F>(&mut self, fill: F) -> bool
    where
        F: Fn(ChunkCoord, &TerrainSettings) -> TerrainChunkData,
    {
        if self.draw_ctx.is_none() {
            return false;
        }
        // 地形を丸ごと作り直す＝ショアフィールドは全面的に無効（Phase W1.5）。
        self.terrain_edit_version += 1;
        // シーンが無ければ空シーンを作る（スモーク単独起動・地形専用編集を許容する）。
        if self.scene.is_none() {
            self.scene = Some(crate::engine::core::app_base::scene::Scene::new("terrain"));
        }

        // ── 冪等化: 既存の terrain ルートを除去してから作り直す（二重生成防止）──
        //   本メソッドは毎回新しい terrain ルートを scene.actors へ push するため、
        //   除去しないと 2 回叩くとヒエラルキーに terrain ルートが重複し、
        //   古いチャンクアクター群がシーンに残って保存もされてしまう（オーファン）。
        //   同名（TERRAIN_ROOT_NAME）のトップレベルルートとそのサブツリーの全エンティティを
        //   despawn してから作り直すことで、再初期化・ハイトマップ再読込でも重複を生じさせない。
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

        // ── 状態をリセットしてシーン名を取り込む ──
        //   TerrainState::default() により undo_stack/redo_stack/stroke_before/stroke_active も
        //   まとめてクリアされる。地形全体を作り直す（全チャンク密度が入れ替わる）ため、
        //   古い undo 履歴は対象チャンクごと消え去り整合性が保てなくなる。よってここで
        //   確実に破棄する（中途半端な undo エントリを残さない）。
        //
        //   ただし **設定（TerrainSettings）はリセットしてはならない**。エディタから
        //   TERRAIN_INIT の引数で渡されたチャンク構成（枚数・分割数・ボクセルサイズ）は
        //   この呼び出しの直前に self.terrain.settings へ反映済みであり、既定値へ
        //   戻すとその指定が丸ごと無視されるため、退避して復元する。
        //
        //   **ブラシ形状マスクのパスも同じ理由で持ち越す**。あれは地形データではなく
        //   「今エディタで選んでいる道具の形」であり、半径・強度スライダーと同じ性質を持つ。
        //   地形を作り直したらブラシの形だけ黙って円へ戻る、という挙動は説明がつかない。
        //   デコード済みキャッシュ（mask_cache）のほうは捨ててよい（次のストロークで
        //   `ensure_terrain_brush_mask` が読み直す）。
        let settings = self.terrain.settings.clone();
        let brush_mask_path = std::mem::take(&mut self.terrain.brush_mask_path);
        self.terrain = TerrainState::default();
        self.terrain.settings = settings.clone();
        self.terrain.brush_mask_path = brush_mask_path;
        let scene_name = self.scene.as_ref().map(|s| s.name.clone()).unwrap_or_default();
        self.terrain.scene_name = scene_name.clone();

        // ── レイヤ定義（layers.json）を読み込み、GPU バインドグループを用意する ──
        //   TerrainState::default() でクリアされているため毎回作り直す。
        //   アセットが無ければ既定セット（単色 4 層）にフォールバックする。
        self.ensure_terrain_layers();
        let layers = self.terrain.layers.clone();

        // ── フェーズ 1: 全チャンクの初期密度を敷き詰める（fill クロージャに委譲） ──
        // 先に全チャンクを map へ入れておくことで、後段のメッシュ化で境界の
        // 隣接サンプル（neighbor_sampler）が正しい値を返す。
        let coords = ground_chunk_coords(&settings);
        for &coord in &coords {
            let data = fill(coord, &settings);
            self.terrain.chunks.insert(coord, data);
        }

        // ── フェーズ 2: 各チャンクをメッシュ化して GPU アップロード（描画リソースを先に作る）──
        //   self.terrain.chunks（不変）と self.draw_ctx（不変）を同時借用する（別フィールドなので可）。
        let mut prebuilt: Vec<(ChunkCoord, Arc<Model>, Option<GpuModel>, Option<InstancedModelBatch>)> = Vec::new();
        // 由来辺は借用の都合で一旦ローカルへ溜め、フェーズ 3 の後で self.terrain へ入れる。
        let mut prebuilt_edges: Vec<(ChunkCoord, Arc<Vec<TerrainVertexEdge>>)> = Vec::new();

        // ── フェーズ 2a: CPU メッシュ生成をチャンク間で rayon 並列化する ──
        //   ロード経路（`rebuild_terrain_after_load`）・編集経路（`remesh_chunks` フェーズ0）と
        //   同一の並列化。`build_chunk_cpu_model` は共有参照のみの純粋関数で、
        //   `par_iter().map().collect()` は入力順を保存するため出力は逐次実行と完全一致（決定的）。
        let cpu_models: Vec<Option<(Arc<Model>, bool, Arc<Vec<TerrainVertexEdge>>)>> = coords
            .par_iter()
            .map(|&coord| build_chunk_cpu_model(&self.terrain.chunks, &settings, &layers, coord, 0))
            .collect();

        // ── フェーズ 2b: GPU アップロードは直列（DrawContext は Sync でないため並列化しない）──
        {
            let ctx = self.draw_ctx.as_ref().unwrap();
            for (&coord, cpu) in coords.iter().zip(cpu_models.into_iter()) {
                // 空メッシュチャンクも gpu/batch=None で積まれ、全チャンクがアクター＋MC スロットを
                // 得る（掘削で後から表面が出ても差し替えられる）。
                let Some((model, is_empty, edges)) = cpu else { continue };
                let (gpu, batch) = upload_chunk_model(ctx, &model, is_empty);
                prebuilt.push((coord, model, gpu, batch));
                prebuilt_edges.push((coord, edges));
            }
        }
        // 由来辺キャッシュを登録する（`self.terrain` は上で default に差し替わっているので空）。
        for (coord, edges) in prebuilt_edges {
            self.terrain.chunk_vertex_edges.insert(coord, edges);
        }
        // 地形チャンクが使うパレットを group3 へ登録する（描画前に済ませる必要がある）。
        self.ensure_terrain_palettes(prebuilt.iter().map(|p| p.1.as_ref()));

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
                // チャンク 1 枚分のアクター構造は spawn_chunk_actor へ集約している
                // （チャンク追加（TERRAIN_ADD_CHUNKS）と完全に同じ構造にするため）。
                let (folder, mc_slot) = spawn_chunk_actor(
                    &mut scene.world, &scene_name, &settings, coord, model, gpu, batch,
                );
                slot_map.push((coord, mc_slot));
                root_actor.add_child(folder);
            }

            scene.actors.push(root_actor);
        }

        // チャンク → メッシュスロット対応を反映する。
        let mut rebuilt_keys: Vec<String> = Vec::with_capacity(slot_map.len());
        for (coord, entity) in slot_map {
            self.terrain.chunk_slot_entity.insert(coord, entity);
            rebuilt_keys.push(terrain_source_path(&scene_name, coord));
        }
        // 同一シーンで 2 回目以降の初期化／ハイトマップ再読込を行うと、チャンクの
        // batch_key（合成 source_path）が 1 回目と完全に一致する。ジオメトリ由来の
        // 派生キャッシュ（BLAS・統合バッチ）は前回のまま残るため、ここで破棄する。
        self.invalidate_geometry_caches(&rebuilt_keys, true);

        self.send_hierarchy();
        true
    }

    /// レイヤ定義（layers.json）を読み込み、GPU バインドグループを用意する。
    ///
    /// 冪等（何度呼んでもよい）。読み込み順:
    ///   1. `assets://terrain/layers.json` を読んでパースする
    ///   2. 読めない／壊れている場合は `TerrainLayerSet::default()`（単色 4 層）へフォールバックする
    ///   3. レイヤ定義から group3 のバインドグループ（uniform + レイヤテクスチャ 4 枚）を作る
    ///
    /// GPU 未初期化（draw_ctx なし）のときはバインドグループを作らず CPU 側の定義だけ持つ。
    /// この場合 G-Buffer パスは地形専用パイプラインへ切り替えないため、
    /// レイヤ色は出ないが描画自体は通常マテリアルで成立する（安全側）。
    pub(super) fn ensure_terrain_layers(&mut self) {
        // ── ⓪ カバー素材定義も読み直す（I3.1）──
        //   カバー素材表はレイヤ uniform（group3）へ同居させているため、
        //   レイヤ GPU リソースを作り直すこの経路が唯一の反映点である。
        //   ここで読まないと cover_materials.json の差し替えが色・粗さに効かない。
        self.ensure_cover_materials();

        // ── ① 定義を読む（アセットが無ければ既定セット）──
        //   環境変数 SEED_TERRAIN_LAYERS が指定されていればそちらを優先する（検証用フック）。
        let source = std::env::var(TERRAIN_LAYERS_PATH_ENV)
            .ok()
            .filter(|p| !p.is_empty())
            .unwrap_or_else(|| TERRAIN_LAYERS_ASSET.to_string());
        let set = match crate::engine::asset_fs::read_string(&source) {
            Ok(text) => match TerrainLayerSet::from_json_str(&text) {
                Ok(set) => set,
                Err(e) => {
                    eprintln!(
                        "[SEED terrain] layers.json parse failed ({e}); 既定レイヤセットで続行します"
                    );
                    TerrainLayerSet::default()
                }
            },
            Err(_) => {
                // ファイルが無いのは正常な運用（未整備プロジェクト）なのでログレベルを落とす。
                eprintln!(
                    "[SEED terrain] {source} が見つかりません; 既定レイヤセット（単色 4 層）を使用します"
                );
                TerrainLayerSet::default()
            }
        };

        // ── ② 旧レイヤリソースを先に解放する（VRAM 2 倍スパイク回避）──
        //   レイヤテクスチャ配列は base_color / normal / roughness の 3 本を
        //   全レイヤぶん抱えるため、旧を保持したまま新を確保すると瞬間的に
        //   2 倍の VRAM を要求する。remesh_chunks / slot_ops と同じ
        //   「旧 drop → device.poll(Wait) で解放確定 → 新規確保」の順序に従う。
        //   （初回呼び出しでは None なので実質ノーオペ）
        if self.terrain.layer_resources.take().is_some() {
            if let Some(ctx) = self.draw_ctx.as_ref() {
                let _ = ctx.device.poll(wgpu::PollType::Wait);
            }
        }

        // ── ③ GPU リソースを作る（レイヤテクスチャ配列の読み込み／リサイズを伴う）──
        //   パレット別バインドグループのキャッシュもここで初期化される
        //   （既定パレット＝レイヤ 0..3 の素通しぶんは必ず作られる）。
        let res = self.draw_ctx.as_ref().map(|ctx| {
            crate::engine::core::renderer::terrain_gbuffer::TerrainLayerResources::new(
                &ctx.device,
                &ctx.queue,
                &ctx.pipelines.gbuffer.terrain.layer_bgl,
                &set,
                // カバー素材表（I3.1）も同じ uniform に載る。レイヤと同時に作り直す。
                &self.terrain.cover_materials,
            )
        });

        self.terrain.layers = set;
        self.terrain.layer_resources = res;
    }

    /// レイヤ定義（layers.json）を再読込し、シーンビューへ即時反映する（TERRAIN_RELOAD_LAYERS）。
    ///
    /// エディタの地形設定ウィンドウが layers.json を保存した直後に送られる。
    /// 手順:
    ///   1. `ensure_terrain_layers()` で JSON を読み直し、レイヤテクスチャ配列を作り直す
    ///      （旧リソースの drop → poll(Wait) → 新規確保は ensure_terrain_layers 内で担保）
    ///   2. 既存の全チャンクを再メッシュ化する（ルール変更で頂点のレイヤ重みが変わるため、
    ///      密度が同じでも頂点バッファの作り直しが必要）
    ///
    /// 地形が未生成（チャンク 0 個）の場合はレイヤ定義の読み直しだけ行う。
    /// 完了は `TERRAIN_RELOAD_LAYERS_OK:{再メッシュしたチャンク数}` で通知する。
    pub(super) fn handle_terrain_reload_layers(&mut self) {
        // ── ① レイヤ定義とレイヤテクスチャ配列を作り直す ──
        self.ensure_terrain_layers();

        // ── ①' プロップ定義（props.json）も読み直し、草 uniform を作り直させる ──
        //   「保存して適用」は layers.json と props.json を両方ディスクへ書いてから
        //   TERRAIN_RELOAD_LAYERS を送る。ここで props も読み直しておくと、草丈・幅・色・
        //   風のような「散布位置に影響しない見た目パラメータ」は**再散布せずとも**即座に
        //   Edit モードへ反映される（これらは grass uniform だけの値で、位置の再生成を要さない）。
        //   grass_gpu_dirty を立てて次フレームの rebuild_grass_gpu に uniform を組み直させる。
        //   （Edit で props 変更が描画に効かない根因対策。Play はシーン再ロードで別途反映される。）
        self.ensure_terrain_props();
        self.terrain.grass_gpu_dirty = true;

        // ── ② 全チャンクを再メッシュ化する ──
        //   remesh_chunks が &mut self を取るため、対象座標は先に確定させておく。
        let coords: Vec<ChunkCoord> = self.terrain.chunk_slot_entity.keys().copied().collect();
        self.remesh_chunks(&coords, RemeshOptions::immediate());

        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("TERRAIN_RELOAD_LAYERS_OK:{}", coords.len()));
        }
    }

    /// 地形チャンクのモデル群が使うパレットを group3 のバインドグループとして登録する。
    ///
    /// 描画中（RenderPass 生存中）は `&self` でしか触れないため、**チャンク構築直後に**
    /// ここで登録しておく必要がある。未登録のパレットは描画時に既定パレットへ
    /// フォールバックする（レイヤ割り当てがずれるだけで、描画は落ちない）。
    pub(super) fn ensure_terrain_palettes<'a>(
        &mut self,
        models: impl IntoIterator<Item = &'a Model>,
    ) {
        // draw_ctx とレイヤリソースの両方が揃っているときだけ意味を持つ。
        let Some(ctx) = self.draw_ctx.as_ref() else { return };
        let Some(res) = self.terrain.layer_resources.as_mut() else { return };
        let layout = &ctx.pipelines.gbuffer.terrain.layer_bgl;
        for model in models {
            res.ensure_palettes_from_model(&ctx.device, layout, model);
        }
    }

    /// レイヤペイントブラシ（TERRAIN_PAINT）。
    ///
    /// スクリーン座標からレイマーチで地表の着弾点を求め、そこを中心とした球ブラシで
    /// `layer` の重みを押し上げる（他レイヤは正規化で減衰する）。密度は一切変えない。
    /// undo は密度ブラシと同じストローク単位（TERRAIN_STROKE_END で確定）に載る。
    pub(super) fn handle_terrain_paint(
        &mut self,
        layer: usize,
        screen_x: f32,
        screen_y: f32,
        radius: f32,
        strength: f32,
    ) {
        // 地形未初期化なら何もしない。
        if self.terrain.chunks.is_empty() {
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_PAINT_MISS");
            }
            return;
        }
        let Some(center) = self.terrain_raymarch_hit(screen_x, screen_y) else {
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_PAINT_MISS");
            }
            return;
        };

        // 定義済みレイヤ数でクランプする（T2b でレイヤ総数は可変になったため、
        // 同時ブレンド数 TERRAIN_BLEND_SLOTS ではなく定義数が上限になる）。
        let max_layer = self.terrain.layers.layers.len().min(TERRAIN_MAX_LAYERS).saturating_sub(1);
        self.terrain.paint_layer = layer.min(max_layer);
        self.handle_terrain_paint_world(self.terrain.paint_layer, center, radius, strength);

        // プレビュー球をペイント着弾点へ追従させる（密度ブラシと同じ扱い）。
        self.terrain.brush_preview = Some((center, radius, strength));

        if let Some(ipc) = &self.ipc {
            ipc.send(&format!(
                "TERRAIN_PAINT_OK:{},{},{},{}",
                self.terrain.paint_layer, center[0], center[1], center[2]
            ));
        }
    }

    /// ワールド座標中心でレイヤペイントを適用し、影響チャンクを再メッシュ化する。
    ///
    /// レイキャスト（handle_terrain_paint）とスモークフックの双方から呼ばれる共通経路。
    /// 手順は handle_terrain_brush_world と対称（ストローク開始スナップショット →
    /// ペイント適用 → 影響チャンク再メッシュ）。
    pub(super) fn handle_terrain_paint_world(
        &mut self,
        layer: usize,
        center: [f32; 3],
        radius: f32,
        strength: f32,
    ) {
        if self.draw_ctx.is_none() || self.terrain.chunks.is_empty() {
            return;
        }
        // ブラシ形状マスクを（指定されていれば）デコード済みにしておく。
        // 未指定なら 1 命令も走らず、この先の挙動は従来とまったく同じになる。
        self.ensure_terrain_brush_mask();
        let settings = self.terrain.settings.clone();
        let brush = SphereBrush { center, radius, strength };

        // ── ① undo 用ストローク開始（暗黙）＆ 編集前スナップショット ──
        //   密度ブラシと同じ stroke_before を共有するため、密度編集とペイントを
        //   混ぜたストロークも 1 エントリとして正しく巻き戻せる。
        self.terrain.stroke_active = true;
        {
            let touch_candidates = terrain::brush::chunks_in_brush_aabb(&brush, &settings);
            let terrain = &mut self.terrain;
            for coord in touch_candidates {
                if terrain.stroke_before.contains_key(&coord) {
                    continue;
                }
                if let Some(chunk) = terrain.chunks.get(&coord) {
                    terrain.stroke_before.insert(coord, ChunkSnapshot::capture(chunk));
                }
            }
        }

        // ── ② 球ブラシをスプラット場へ適用 ──
        let affected: Vec<ChunkCoord> = {
            let terrain = &mut self.terrain;
            // 形状マスクの参照と `chunks` の可変借用は **別フィールド**なので同時に持てる
            // （`resolve_brush_mask` がフィールド単位の引数を取るのはこのため）。
            let mask = super::terrain_brush_mask_ops::resolve_brush_mask(
                &terrain.mask_cache,
                &terrain.brush_mask_path,
            );
            let mut view = FieldView {
                settings: &terrain.settings,
                chunks: &mut terrain.chunks,
            };
            terrain::paint::apply_paint_with_mask(&mut view, &brush, layer as u32, BRUSH_DT, mask)
        };
        if affected.is_empty() {
            return;
        }

        // ── ③ 影響チャンクを「頂点カラー更新待ち」へ積む（ペイント高速パス） ──
        //   ペイントは密度を一切変えないため、頂点位置・法線・インデックス・三角形数は
        //   すべて不変であり、変わるのは頂点カラー（レイヤ重み）とチャンクのパレットだけ。
        //   よってマーチングキューブスを回す必要はまったく無く、`pending_remesh` ではなく
        //   `pending_paint` へ積む。実際の反映は密度ブラシと同じく
        //   `flush_terrain_pending_remesh` が 1 フレーム 1 回にまとめて行う。
        //   未保存マーク（dirty）は編集時点で立てる（理由は handle_terrain_brush_world と同じ）。
        self.terrain.dirty.extend(affected.iter().copied());
        self.terrain.pending_paint.extend(affected);
    }

    /// エディタから届いたチャンク構成を現在の TerrainSettings へ反映する。
    ///
    /// `config` が `None`（旧形式の引数なしコマンド）なら何もしない＝現在の設定を維持する。
    /// 値の検証・クランプは `TerrainSettings::apply_chunk_config` が担う。
    ///
    /// 【安全策 — 分割数変更は地形の作り直しとセットでのみ許す】
    ///   chunk_cells / voxel_size を変えると 1 チャンクのサンプル数・実寸が変わり、
    ///   既存の密度配列（および保存済み .tvox）とサイズが噛み合わなくなる。
    ///   よって本メソッドは **地形を丸ごと作り直す経路（TERRAIN_INIT / TERRAIN_HEIGHTMAP）
    ///   からのみ** 呼ぶ。チャンク追加（TERRAIN_ADD_CHUNKS）は構成を一切変更しない。
    fn apply_terrain_chunk_config(
        &mut self,
        config: Option<crate::engine::core::app_base::ipc::TerrainChunkConfig>,
    ) {
        let Some(c) = config else { return };
        self.terrain.settings.apply_chunk_config(
            c.chunks_x, c.chunks_z, c.chunk_cells, c.voxel_size,
        );
        let s = &self.terrain.settings;
        eprintln!(
            "[SEED terrain] chunk config applied: {}x{} chunks, cells={}, voxel={}m (chunk extent={}m)",
            s.ground_chunks_x, s.ground_chunks_z, s.chunk_cells, s.voxel_size, s.chunk_extent()
        );
    }

    /// 地形を初期化する。地形ツリーを生成し、初期地面（平坦地面）を敷いてメッシュ化・GPU アップロードする。
    ///
    /// TERRAIN_INIT コマンド・スモークフックから呼ばれる。
    /// `config` が `Some` のときはチャンク構成（枚数・分割数・ボクセルサイズ）を先に反映する。
    /// 既存地形は丸ごと破棄されるため、この経路でのみ分割数の変更が安全に行える。
    pub(super) fn handle_terrain_init(
        &mut self,
        config: Option<crate::engine::core::app_base::ipc::TerrainChunkConfig>,
    ) {
        self.apply_terrain_chunk_config(config);
        let ok = self.build_terrain_with(|coord, settings| {
            TerrainChunkData::from_ground_plane(settings, coord)
        });
        if !ok {
            return;
        }
        if let Some(ipc) = &self.ipc {
            ipc.send("TERRAIN_INIT_OK");
        }
    }

    /// 編集中の地形へチャンクを追加する（TERRAIN_ADD_CHUNKS）。
    ///
    /// 指定されたチャンク座標範囲 `[min_x, max_x] × [min_z, max_z]`（両端含む）を、
    /// 現在の縦方向範囲（`ground_chunk_y_min..=ground_chunk_y_max`）ぶん敷き詰める。
    ///
    /// 【既存チャンクの温存】
    ///   範囲内に既にあるチャンクは**一切触らない**（密度もペイントもアクターもそのまま）。
    ///   よって「今の地形を保ったまま外側へ広げる」用途に使える。
    ///
    /// 【継ぎ目の連続性】
    ///   新規チャンクは平坦地面で初期化した上で、既存チャンクと重複所有する境界サンプルを
    ///   `sync_new_chunk_boundary` で既存側の値に揃える。さらに新規チャンクの 26 近傍にある
    ///   既存チャンクも再メッシュ化する（外側 1 サンプルの読み値が「地形外＝AIR」から
    ///   実際の密度へ変わるため、境界の三角形と法線が変化する）。
    ///
    /// 【変更しないもの】
    ///   chunk_cells / voxel_size は変更しない（既存チャンクと非互換になるため。
    ///   構成変更は TERRAIN_INIT / TERRAIN_HEIGHTMAP による作り直しでのみ行う）。
    pub(super) fn handle_terrain_add_chunks(
        &mut self,
        min_x: i32,
        min_z: i32,
        max_x: i32,
        max_z: i32,
    ) {
        // ── ① 前提条件の検査 ──
        if self.draw_ctx.is_none() {
            self.send_add_chunks_error("draw context unavailable");
            return;
        }
        if self.terrain.chunks.is_empty() {
            // 地形が無い状態での「追加」は意味が定まらない（ツリーも設定も未確定）。
            // 先に初期化させる方が挙動が明快なのでエラーにする。
            self.send_add_chunks_error("terrain not initialized");
            return;
        }
        let settings = self.terrain.settings.clone();

        // ── ② 追加対象（まだ存在しないチャンク）を列挙する ──
        //   既存チャンクは列挙されない＝以降の処理で上書きされない（＝温存される）。
        let new_coords =
            collect_new_chunk_coords(&self.terrain.chunks, &settings, min_x, min_z, max_x, max_z);
        if new_coords.is_empty() {
            // 追加すべきものが無いのは正常（指定範囲がすべて既存）。0 件で成功を返す。
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_ADD_CHUNKS_OK:0,0");
            }
            return;
        }
        // チャンク総数の安全弁（1 チャンクは数 MB あるため、暴走すると即メモリ枯渇する）。
        if self.terrain.chunks.len() + new_coords.len() > terrain::MAX_TOTAL_CHUNKS {
            self.send_add_chunks_error(&format!(
                "chunk limit exceeded ({} existing + {} new > {})",
                self.terrain.chunks.len(), new_coords.len(), terrain::MAX_TOTAL_CHUNKS
            ));
            return;
        }

        // ── ③ 新規チャンクの密度を作り、境界を既存チャンクへ揃える ──
        //   sync_new_chunk_boundary は「まだ新規チャンクを含まない chunks」を見る必要が
        //   あるため、全チャンクを作り終えてからまとめて insert する。
        let mut created: Vec<(ChunkCoord, TerrainChunkData)> = Vec::with_capacity(new_coords.len());
        for &coord in &new_coords {
            let mut data = TerrainChunkData::from_ground_plane(&settings, coord);
            sync_new_chunk_boundary(&self.terrain.chunks, &settings, coord, &mut data);
            created.push((coord, data));
        }
        for (coord, data) in created {
            self.terrain.chunks.insert(coord, data);
        }

        // ── ④ 新規チャンクをメッシュ化して GPU アップロードする ──
        //   全チャンクを map へ入れ終えた後に行うことで、隣接読み（neighbor_sampler）が
        //   新規チャンク同士の境界でも正しい値を返す。
        let layers = self.terrain.layers.clone();
        let mut prebuilt: Vec<(ChunkCoord, Arc<Model>, Option<GpuModel>, Option<InstancedModelBatch>)> =
            Vec::with_capacity(new_coords.len());
        // 由来辺は、アクター構築が成功して初めてキャッシュへ入れる（下の ⑤ 参照）。
        // ここで先に入れてしまうと、terrain ルート不在で中断する経路（チャンクを
        // `chunks` から取り消す）で、実体の無いチャンクの辺だけが残ってしまう。
        let mut prebuilt_edges: Vec<(ChunkCoord, Arc<Vec<TerrainVertexEdge>>)> =
            Vec::with_capacity(new_coords.len());
        {
            let ctx = self.draw_ctx.as_ref().unwrap();
            for &coord in &new_coords {
                if let Some((model, gpu, batch, edges)) =
                    build_chunk_render(&self.terrain.chunks, &settings, &layers, ctx, coord)
                {
                    prebuilt.push((coord, model, gpu, batch));
                    prebuilt_edges.push((coord, edges));
                }
            }
        }
        self.ensure_terrain_palettes(prebuilt.iter().map(|p| p.1.as_ref()));

        // ── ⑤ アクターを組み立てて既存の terrain ルートへぶら下げる ──
        //   World への spawn/insert（&mut scene.world）と、ルートアクターの探索
        //   （&mut scene.actors）は別フィールドなので順に行えばよい。
        let scene_name = self.terrain.scene_name.clone();
        let mut slot_map: Vec<(ChunkCoord, Entity)> = Vec::with_capacity(prebuilt.len());
        {
            let Some(scene) = self.scene.as_mut() else {
                self.send_add_chunks_error("no scene");
                return;
            };
            let mut folders: Vec<Actor> = Vec::with_capacity(prebuilt.len());
            for (coord, model, gpu, batch) in prebuilt {
                let (folder, mc_slot) = spawn_chunk_actor(
                    &mut scene.world, &scene_name, &settings, coord, model, gpu, batch,
                );
                slot_map.push((coord, mc_slot));
                folders.push(folder);
            }
            // terrain ルート（トップレベルの同名フォルダ）へ追加する。
            match scene.actors.iter_mut().find(|a| a.name == TERRAIN_ROOT_NAME) {
                Some(root) => {
                    for folder in folders {
                        root.add_child(folder);
                    }
                }
                None => {
                    // ルートが無い＝地形ツリーが壊れている。作ったエンティティを掃除して中断する。
                    for folder in &folders {
                        let mut entities: Vec<Entity> = Vec::new();
                        collect_subtree_entities(folder, &mut entities);
                        for e in entities {
                            scene.world.despawn(e);
                        }
                    }
                    for &coord in &new_coords {
                        self.terrain.chunks.remove(&coord);
                    }
                    self.send_add_chunks_error("terrain root actor not found");
                    return;
                }
            }
        }
        for (coord, entity) in slot_map {
            self.terrain.chunk_slot_entity.insert(coord, entity);
            // 追加直後は未保存なので、TERRAIN_SAVE の対象になるようダーティにする。
            self.terrain.dirty.insert(coord);
        }
        // 地形の広がりが変わった＝ショアフィールドの焼き直し対象（Phase W1.5）。
        self.terrain_edit_version += 1;
        // アクター構築まで成功したので、由来辺キャッシュを登録する
        // （これで追加チャンクも最初のペイントからマーチングキューブス無しで塗れる）。
        for (coord, edges) in prebuilt_edges {
            self.terrain.chunk_vertex_edges.insert(coord, edges);
        }

        // ── ⑥ 新規チャンクに接する既存チャンクを再メッシュ化する ──
        //   既存チャンクのサンプル自体は変わらないが、メッシュ生成時に読む「外側 1 サンプル」が
        //   地形外（＝AIR 相当の density_clamp）から実際の密度へ変わるため、境界の三角形と
        //   法線が変化する。これを怠ると継ぎ目に隙間や陰影の段差が残る。
        let new_set: HashSet<ChunkCoord> = new_coords.iter().copied().collect();
        let mut neighbors: HashSet<ChunkCoord> = HashSet::new();
        for &coord in &new_coords {
            for dx in -1..=1 {
                for dy in -1..=1 {
                    for dz in -1..=1 {
                        let n = ChunkCoord::new(coord.x + dx, coord.y + dy, coord.z + dz);
                        // 新規チャンクは ④ で既にメッシュ化済みなので除外する。
                        if !new_set.contains(&n) && self.terrain.chunk_slot_entity.contains_key(&n) {
                            neighbors.insert(n);
                        }
                    }
                }
            }
        }
        let neighbor_list: Vec<ChunkCoord> = neighbors.into_iter().collect();
        self.remesh_chunks(&neighbor_list, RemeshOptions::immediate());

        self.send_hierarchy();
        eprintln!(
            "[SEED terrain] add chunks: +{} (remeshed neighbors={}, total={})",
            new_coords.len(), neighbor_list.len(), self.terrain.chunks.len()
        );
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!(
                "TERRAIN_ADD_CHUNKS_OK:{},{}",
                new_coords.len(), neighbor_list.len()
            ));
        }
    }

    /// チャンク追加の失敗をエディタへ通知する（メッセージ組み立ての重複を避けるヘルパ）。
    fn send_add_chunks_error(&self, message: &str) {
        eprintln!("[SEED terrain] add chunks failed: {message}");
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("TERRAIN_ADD_CHUNKS_ERROR:{message}"));
        }
    }

    /// ハイトマップ画像から地形を敷き直す（TERRAIN_HEIGHTMAP コマンド）。
    ///
    /// 画像を読み込んでグレースケール化し、初期地面フットプリント（world x,z ∈
    /// [0, ground_chunks_x/z * chunk_extent]）へバイリニアマッピングして高さ場を作る。
    /// 密度 = worldY - height（規約どおり density<iso で SOLID）。
    /// build_terrain_with を通すため、既存の地形（undo 履歴含む）は丸ごと作り直しになる。
    ///
    /// `config` が `Some` のときはチャンク構成（枚数・分割数・ボクセルサイズ）を先に反映する。
    /// フットプリントもその構成から決まるため、画像は新しい範囲へマッピングされる。
    pub(super) fn handle_terrain_heightmap(
        &mut self,
        path: String,
        height_scale: f32,
        config: Option<crate::engine::core::app_base::ipc::TerrainChunkConfig>,
    ) {
        let start = std::time::Instant::now();

        // 画像のマッピング先フットプリントを決めるので、構成の反映は必ず先に行う。
        self.apply_terrain_chunk_config(config);
        let settings = self.terrain.settings.clone();
        let extent = settings.chunk_extent();
        let footprint_w = settings.ground_chunks_x as f32 * extent;
        let footprint_d = settings.ground_chunks_z as f32 * extent;

        // ── 画像読込 → グレースケール化 → luma01（0..1 正規化）へ変換 ──
        //   path はエディタから渡される実ファイルシステム絶対パス（asset_fs 不要）。
        let img = match image::open(&path) {
            Ok(img) => img,
            Err(e) => {
                if let Some(ipc) = &self.ipc {
                    ipc.send(&format!("TERRAIN_HEIGHTMAP_ERROR:{e}"));
                }
                return;
            }
        };
        let gray = img.to_luma8();
        let (w, h) = (gray.width() as usize, gray.height() as usize);
        if w == 0 || h == 0 {
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_HEIGHTMAP_ERROR:empty image");
            }
            return;
        }
        let luma01: Vec<f32> = gray.into_raw().iter().map(|&b| b as f32 / 255.0).collect();
        let field = terrain::HeightmapField {
            luma01,
            w,
            h,
            footprint_w,
            footprint_d,
            height_scale,
        };

        // ── build_terrain_with を通して地形を丸ごと敷き直す ──
        //   from_fn の density_fn は HeightmapField::density_at をそのまま渡す。
        let ok = self.build_terrain_with(|coord, settings| {
            TerrainChunkData::from_fn(settings, coord, |wx, wy, wz| field.density_at(wx, wy, wz))
        });
        if !ok {
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_HEIGHTMAP_ERROR:draw context unavailable");
            }
            return;
        }

        let ms = start.elapsed().as_millis();
        // 重い処理（画像デコード＋全チャンク再メッシュ）なので、IPC 未接続時（スモーク等）
        // でも進捗が追えるよう常に eprintln する。
        eprintln!("[SEED terrain] heightmap applied: {path} ({w}x{h}, scale={height_scale}) in {ms}ms");
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("TERRAIN_HEIGHTMAP_OK:{ms}"));
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

        // ── ④ プレビュー球をブラシ着弾点へ追従させる ──
        //   ドラッグ中（ストローク中）はエディタが TERRAIN_BRUSH_PREVIEW を送らないため、
        //   プレビューが更新されずカーソルから取り残されて見えていた。追加のレイマーチは
        //   不要（handle_terrain_brush_world 内で使った着弾点をそのまま使い回す）。
        self.terrain.brush_preview = Some((center, radius, strength));

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
    /// 当たれば `terrain.brush_preview` に (着弾点, 半径, 強度) をセットし、当たらなければ
    /// `None`（非表示）にする。押下していないホバー中に高頻度で呼ばれるため IPC 応答は返さない。
    /// strength は frame_renderer 側でプレビュー球の色（低強度=水色〜高強度=オレンジ）に使われる。
    pub(super) fn handle_terrain_brush_preview(&mut self, screen_x: f32, screen_y: f32, radius: f32, strength: f32) {
        self.terrain.brush_preview = self
            .terrain_raymarch_hit(screen_x, screen_y)
            .map(|center| (center, radius, strength));
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
        let t_brush = Instant::now(); // brush_apply 全体（計測用）

        // ── ① undo 用ストローク開始（暗黙）＆ 編集前スナップショット ──
        //   ストローク開始は「stroke_active でない状態で最初の TERRAIN_BRUSH（＝本メソッド呼び出し）
        //   が来たとき」に暗黙的に始まる（専用の開始 IPC は無い）。
        //   ブラシ適用前に「このブラシが触りうるチャンク集合」（superset で可）の各既存チャンクを
        //   走査し、ストローク中でまだ控えていないものだけ現在の密度をスナップショットする
        //   （2 発目以降のブラシで上書きしてしまうと「ストローク開始時点」の密度でなくなるため）。
        let t_snapshot = Instant::now();
        self.terrain.stroke_active = true;
        {
            let touch_candidates = terrain::brush::chunks_in_brush_aabb(&brush, &settings);
            let terrain = &mut self.terrain;
            for coord in touch_candidates {
                if terrain.stroke_before.contains_key(&coord) {
                    continue;
                }
                if let Some(chunk) = terrain.chunks.get(&coord) {
                    terrain.stroke_before.insert(coord, ChunkSnapshot::capture(chunk));
                }
            }
        }
        let snapshot_ms = t_snapshot.elapsed().as_secs_f64() * MILLIS_PER_SEC;

        // ── ② 球ブラシを密度場へ適用（settings と chunks を分割借用して FieldView を作る）──
        //   ブラシは球内ボクセルを走査（レイマーチ相当）して密度を書き換える。
        let t_raymarch = Instant::now();
        let affected: Vec<ChunkCoord> = {
            let terrain = &mut self.terrain;
            let mut view = FieldView {
                settings: &terrain.settings,
                chunks: &mut terrain.chunks,
            };
            terrain::brush::apply(&mut view, &brush, op, BRUSH_DT)
        };
        let raymarch_ms = t_raymarch.elapsed().as_secs_f64() * MILLIS_PER_SEC;
        if affected.is_empty() {
            return;
        }

        // ── ③ 影響チャンクを「再メッシュ待ち」へ積む（ダーティ集約） ──
        //   密度場の書き換えは上で完了しているので、ここでのメッシュ化は先送りしてよい。
        //   ドラッグ中は 1 フレームに複数回ここへ来るため、即時に再メッシュすると
        //   同じチャンクを何度も作り直すことになる（1 チャンク数 ms の実測値）。
        //   `process_ipc` が全コマンド処理後に flush_terrain_pending_remesh で 1 回だけ消化する。
        //   未保存マーク（dirty）は再メッシュではなく**編集**に対応する情報なので、
        //   ここで先に立てる。こうしないと同一フレーム内で編集直後に TERRAIN_SAVE が
        //   届いたとき、まだ flush 前で dirty が空＝保存対象から漏れる。
        self.terrain.dirty.extend(affected.iter().copied());
        self.terrain.pending_remesh.extend(affected);
        // 岸波のショアフィールド（Phase W1.5）へ「地形が変わった」を伝える。
        // 実際の再ベイクはデバウンス付きで frame_renderer 側が行うので、
        // ドラッグ中に数百 ms のベイクが挟まることはない。
        self.terrain_edit_version += 1;

        // ── ④ 無操作タイムアウト判定の基準時刻を更新する ──
        //   ストローク中の付随処理はここでは走らせず遅延する（flush 側で蓄積）。
        //   最後にブラシを当てた時刻を控えておき、一定時間操作が途切れたら
        //   `flush_terrain_pending_remesh` が確定処理を起動する。
        self.terrain.last_brush_apply = Some(Instant::now());

        // ── 計測ログ（SEED_PERF_LOG 有効時のみ。既存 [PERF terrain] 書式に揃える）──
        if *PERF_TERRAIN_LOG_ENABLED {
            let brush_ms = t_brush.elapsed().as_secs_f64() * MILLIS_PER_SEC;
            eprintln!(
                "[PERF terrain] brush_apply={:.2}ms (raymarch={:.2}ms snapshot={:.2}ms)",
                brush_ms, raymarch_ms, snapshot_ms
            );
        }
    }

    /// 保留中（`terrain.pending_remesh`）の再メッシュをまとめて 1 回で消化する。
    ///
    /// `process_ipc` の**コマンドループ直後**に毎フレーム 1 回だけ呼ばれる。
    /// ドラッグ中に届いた複数のブラシ／ペイントで積まれたチャンク集合は、ここで
    /// 重複無しの 1 リストへ畳まれ、`remesh_chunks` を **1 回**通るだけになる。
    ///
    /// 【決定性】`HashSet` のイテレーション順は（ハッシュシードにより）実行ごとに変わりうる。
    ///   差し替え順が変わっても最終的な描画結果は同じだが、ログ・GPU コマンド列を
    ///   再現可能にするため必ず座標でソートしてから渡す。
    ///   `ChunkCoord` は `Ord` を実装していない（terrain ライブラリ側の型なので
    ///   ここでは変更しない）ため、`(x, y, z)` のタプルをキーに全順序を与える。
    /// 【チャンク単位 地形 LOD】カメラ距離に応じて各チャンクの目標 LOD を選び、
    /// 現在 LOD と異なるチャンクだけを近い順に小分けで再メッシュ（GPU 差し替え）する。
    ///
    /// フレーム先頭（`handle_redraw_requested`）で **前フレームのメインカメラ位置**
    /// (`self.last_camera_pos`) を使って呼ぶ。カメラは 1 フレームで大きく動かないため
    /// 1 フレーム遅れは体感できず、描画中の借用衝突（scene/draw_ctx 可変借用）も避けられる。
    ///
    /// 遠いチャンクを低ポリ（間引き＋スカート）へ落として毎フレームの描画三角形数を削減する。
    /// 物理コライダーは常にフル解像度（`register_all_terrain_colliders` 側で LOD0 強制）。
    pub(super) fn tick_terrain_lod(&mut self) {
        // 地形無し・GPU 無しなら何もしない（計測ログも意味を持たない）。
        if self.draw_ctx.is_none() || self.terrain.chunk_slot_entity.is_empty() {
            return;
        }
        let settings = self.terrain.settings.clone();
        let extent = settings.chunk_extent();
        let cam = self.last_camera_pos;
        let (d1, d2) = terrain_lod_distances();
        let max_lod = (terrain::lod_count().saturating_sub(1)) as u8;

        // LOD 無効（before 計測）のときは遷移処理を丸ごと飛ばすが、下の計測ログは
        // 全チャンク LOD0 の総三角形数を出すために引き続き実行する（on/off 比較の分母）。
        if !*TERRAIN_LOD_DISABLED {
        // ── 各チャンクの目標 LOD を求め、変化するものを (最近点距離, coord, 目標LOD) で集める ──
        let mut changes: Vec<(f32, ChunkCoord, u8)> = Vec::new();
        for &coord in self.terrain.chunk_slot_entity.keys() {
            let origin = coord.world_origin(&settings);
            let min = origin;
            let max = [origin[0] + extent, origin[1] + extent, origin[2] + extent];
            let dist_sq =
                crate::engine::core::renderer::gpu_resources::aabb_distance_sq(min, max, cam);
            let dist = dist_sq.sqrt();
            let current = self.terrain.chunk_lod.get(&coord).copied().unwrap_or(0);
            let desired = desired_lod_for_distance(current, dist, d1, d2).min(max_lod);
            if desired != current {
                changes.push((dist, coord, desired));
            }
        }

        if !changes.is_empty() {
            // 近いチャンクほど見た目への影響が大きいので、近い順に優先して処理する。
            changes.sort_by(|a, b| a.0.partial_cmp(&b.0).unwrap_or(std::cmp::Ordering::Equal));

            // ── 時間バジェット制で LOD 再メッシュを小分けする ──
            //   遷移候補（近い順に整列済み）を `TERRAIN_LOD_BATCH` チャンクずつ小バッチで
            //   `remesh_chunks` へ渡し、1 バッチ処理するごとに累積経過時間を測る。
            //   `TERRAIN_LOD_BUDGET_MS` を超えたら残りは次フレームへ繰り越して打ち切る。
            //   これにより Play 開始直後の一斉遷移でもフレーム時間の上限を約束できる。
            let total_pending = changes.len(); // このフレームの遷移候補総数（backlog 算出用）
            let budget = Duration::from_secs_f64(TERRAIN_LOD_BUDGET_MS / MILLIS_PER_SEC);
            let t_budget = Instant::now(); // バジェット計測の起点
            let mut processed = 0usize; // このフレームで実際に再メッシュしたチャンク数

            // 近い順に並んだ候補を先頭から小バッチへ切り出して逐次処理する。
            let mut iter = changes.into_iter();
            loop {
                // このバッチぶんの座標を最大 `TERRAIN_LOD_BATCH` 件、かつ件数側の
                // ハード上限（`TERRAIN_LOD_TRANSITIONS_PER_FRAME`）を超えない範囲で集める。
                let take = lod_batch_size(processed);
                let mut coords: Vec<ChunkCoord> = Vec::with_capacity(take);
                while coords.len() < take {
                    let Some((_dist, coord, desired)) = iter.next() else {
                        break; // 候補を出し切った
                    };
                    // 先に目標 LOD を確定してから再メッシュする（remesh_chunks がこの値を読む）。
                    self.terrain.chunk_lod.insert(coord, desired);
                    coords.push(coord);
                }
                if coords.is_empty() {
                    break; // 候補を出し切った / 件数ハード上限に到達した
                }
                // 既存の VRAM 安全な再メッシュ機構をそのまま使う（gpu_model と instanced_batch を
                // 同時に作り直し、派生キャッシュ＝統合バッチ・BLAS も破棄される）。
                //   defer_gpu_release=true: 毎フレーム経路なので旧 GPU リソースは即 drop せず遅延退役し、
                //   poll(Wait) の GPU 待ちストール（移動時 80〜130ms スパイクの真因）を張らない。
                self.remesh_chunks(&coords, RemeshOptions::lod_transition());
                processed += coords.len();

                // ── バジェット判定は必ず「1 バッチ処理したあと」に行う ──
                //   こうすることで、1 チャンクがバジェットより重いフレームでも最低 1 バッチは
                //   必ず前進する（飢餓防止）。超過していれば残りは次フレームへ繰り越す。
                if t_budget.elapsed() >= budget {
                    break;
                }
            }

            // ── 効果測定ログ（低頻度・常時 ON）──
            //   打ち切りで積み残し（backlog）が出ているフレームを 1 秒に 1 回まで間引いて出し、
            //   加えて積み残しが解消した瞬間（backlog>0 → 0 の立ち下がり 1 回）も出して収束点を
            //   可視化する。毎フレームは出さない。
            let done_ms = t_budget.elapsed().as_secs_f64() * MILLIS_PER_SEC; // このフレームの LOD 消費時間
            let backlog = total_pending - processed; // 次フレームへ繰り越した候補数
            {
                use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
                // ログ間引きの基準時刻（プロセス開始からの経過 ms を測る原点）。
                static LOG_EPOCH: std::sync::LazyLock<Instant> =
                    std::sync::LazyLock::new(Instant::now);
                // 最後にログを出した時刻（LOG_EPOCH からの経過 ms）。
                static LAST_LOG_MS: AtomicU64 = AtomicU64::new(0);
                // 直前フレームで積み残しがあったか（backlog>0 → 0 の立ち下がりを 1 回だけ出す）。
                static HAD_BACKLOG: AtomicBool = AtomicBool::new(false);
                // ログ最小間隔（ms）。毎フレーム出さないための間引き幅（1 秒に 1 回）。
                const LOG_INTERVAL_MS: u64 = 1000;

                let now_ms = LOG_EPOCH.elapsed().as_secs_f64() * MILLIS_PER_SEC;
                let had = HAD_BACKLOG.swap(backlog > 0, Ordering::Relaxed);
                let cleared = had && backlog == 0; // 積み残しがこのフレームで解消した
                let last = LAST_LOG_MS.load(Ordering::Relaxed);
                let interval_ok = (now_ms as u64).saturating_sub(last) >= LOG_INTERVAL_MS;
                if (backlog > 0 && interval_ok) || cleared {
                    LAST_LOG_MS.store(now_ms as u64, Ordering::Relaxed);
                    eprintln!("[FPS_PHASE] terrain_lod backlog={backlog} done_ms={done_ms:.1}");
                }
            }
        }
        } // if !*TERRAIN_LOD_DISABLED（遷移処理ここまで。以降の計測ログは on/off 共通）

        // ── 計測ログ（60 フレームに 1 回）: 現在アップロード済みの地形総三角形数と LOD 内訳 ──
        //   LOD 有無での before/after 比較用。カリング前の「保持している総三角形」を数える。
        if *PERF_TERRAIN_LOG_ENABLED {
            use std::sync::atomic::{AtomicU64, Ordering};
            static LN: AtomicU64 = AtomicU64::new(0);
            if LN.fetch_add(1, Ordering::Relaxed) % 60 == 0 {
                let mut lod_hist = [0u32; 8];
                let mut total_tris: u64 = 0;
                if let Some(scene) = self.scene.as_ref() {
                    for (&coord, &slot) in self.terrain.chunk_slot_entity.iter() {
                        let lod = self.terrain.chunk_lod.get(&coord).copied().unwrap_or(0);
                        lod_hist[(lod as usize).min(7)] += 1;
                        if let Some(mc) = scene.world.get::<ModelComponent>(slot) {
                            if let Some(model) = mc.model.as_ref() {
                                if let Some(prim) =
                                    model.meshes.first().and_then(|m| m.primitives.first())
                                {
                                    total_tris += (prim.indices.len() / 3) as u64;
                                }
                            }
                        }
                    }
                }
                eprintln!(
                    "[PERF terrain] lod: total_tris={total_tris} lod0={} lod1={} lod2={} (d1={d1} d2={d2})",
                    lod_hist[0], lod_hist[1], lod_hist[2]
                );
            }
        }
    }

    /// Play 開始直前に、指定カメラ位置基準で全チャンクの目標 LOD を **1 回でまとめて** 収束させる
    /// （ブロッキング）。
    ///
    /// 【目的】通常の毎フレーム LOD 収束 `tick_terrain_lod` は時間バジェット制
    /// （`TERRAIN_LOD_BUDGET_MS`）で 1 フレームあたりごく少数のチャンクしか再メッシュしないため、
    /// Play 開始時にメインカメラ位置へ視点が飛んで数百チャンクの目標 LOD が一斉に跨ぐと、
    /// backlog が解消するまで約 20 秒間 8〜10fps に律速される（実測）。この関数は Play 開始の
    /// **最初の描画フレームより前**に呼び、変化する全チャンクを一括再メッシュして backlog を
    /// ゼロにすることで、「Play 開始直後からぬるっと動ける」状態を作る（起動時間は増える）。
    ///
    /// 【1 回呼びにする理由（速度の要点）】`remesh_chunks` は呼び出しごとに GPU アイドル待ち
    /// `device.poll(Wait)` を 1 回払う（フェーズ B）。フェーズ 0 の CPU メッシュ生成は
    /// `par_iter()` によりチャンク間で rayon 並列に走るため、全チャンクを 1 リストで渡せば
    /// poll バリアが 1 回で済み、メッシュ生成の並列度も全チャンクに効く。毎フレーム
    /// `TERRAIN_LOD_BATCH`（=2）ずつ分割してバリアを何百回も払う `tick_terrain_lod` 経路より
    /// 総時間が大幅に短くなる。
    ///
    /// 【カメラ位置】呼び出し側が Play のメインカメラ位置を渡す（見つからなければ
    /// `last_camera_pos` フォールバック）。LOD の距離しきい値・ヒステリシスは通常経路と同一
    /// （`desired_lod_for_distance`）なので、収束結果は毎フレーム収束の最終形と一致する。
    ///
    /// 所要時間は `[FPS_PHASE] terrain_lod pre-converge total=Xms chunks=N` で常時ログする。
    pub(super) fn converge_terrain_lod_blocking(&mut self, cam_pos: [f32; 3]) {
        // 地形無し・GPU 無しなら何もしない。
        if self.draw_ctx.is_none() || self.terrain.chunk_slot_entity.is_empty() {
            return;
        }
        // LOD 無効（before 計測）時は収束処理を丸ごと飛ばす（全チャンク LOD0 のまま）。
        if *TERRAIN_LOD_DISABLED {
            return;
        }

        let settings = self.terrain.settings.clone();
        let extent = settings.chunk_extent();
        let (d1, d2) = terrain_lod_distances();
        let max_lod = (terrain::lod_count().saturating_sub(1)) as u8;

        let t_total = Instant::now(); // 事前収束の総所要時間計測の起点

        // ── 各チャンクの目標 LOD を求め、現在 LOD と異なるものだけ集める ──
        //   同時に目標 LOD を `chunk_lod` へ確定しておく（remesh_chunks フェーズ0 がこの値を読む）。
        let mut coords: Vec<ChunkCoord> = Vec::new();
        for &coord in self.terrain.chunk_slot_entity.keys() {
            let origin = coord.world_origin(&settings);
            let min = origin;
            let max = [origin[0] + extent, origin[1] + extent, origin[2] + extent];
            let dist_sq =
                crate::engine::core::renderer::gpu_resources::aabb_distance_sq(min, max, cam_pos);
            let dist = dist_sq.sqrt();
            let current = self.terrain.chunk_lod.get(&coord).copied().unwrap_or(0);
            let desired = desired_lod_for_distance(current, dist, d1, d2).min(max_lod);
            if desired != current {
                self.terrain.chunk_lod.insert(coord, desired);
                coords.push(coord);
            }
        }

        let n = coords.len(); // 事前収束で再メッシュするチャンク数（ログ用）
        if n > 0 {
            // 決定性のため座標順にソートしてから、1 回の呼び出しでまとめて再メッシュする。
            //   （HashMap 走査順は実行ごとに変わる。GPU コマンド列・ログを再現可能にするため）。
            coords.sort_by_key(|c| (c.x, c.y, c.z));
            // 【本命】バジェット分割せず全チャンクを 1 回で処理する。defer_side_effects=false で
            //   従来どおりコライダー追従・RT prune も即時に行う（Play 開始時は物理未起動なので
            //   コライダー追従は no-op、統合バッチ無効化・BLAS prune は必要ぶんだけ走る）。
            //   defer_gpu_release=false: 一括収束は Play 開始前（GPU アイドル）なので poll(Wait) は軽く、
            //   即時解放で VRAM ピークを抑える方が有利（数百チャンク分の旧リソースを溜めない）。
            self.remesh_chunks(&coords, RemeshOptions::immediate());
        }

        // ── 所要時間ログ（常時 ON）──
        let total_ms = t_total.elapsed().as_secs_f64() * MILLIS_PER_SEC;
        eprintln!("[FPS_PHASE] terrain_lod pre-converge total={total_ms:.0}ms chunks={n}");
    }

    pub(super) fn flush_terrain_pending_remesh(&mut self) {
        let t_flush = Instant::now();
        // このフレームで何か実処理が起きたか（起きたフレームだけ flush 合計ログを出す）。
        let mut did_work = false;

        // ── ストローク進行中は付随処理を遅延する ──
        //   ドラッグ中（stroke_active）は描画メッシュ（remesh）だけを毎フレーム更新し、
        //   コライダー再構築・散布再接地・RT BLAS prune といった重い付随処理はスキップして
        //   `stroke_deferred_chunks` へ溜める。これらは毎フレーム走らせるとストローク中の
        //   フレーム時間を支配してしまうため（詳細は STROKE_IDLE_FLUSH_MS のコメント）。
        //   ストロークが確定（マウスアップ／無操作タイムアウト）したときに一括で追従させる。
        //   マウスアップ後の flush では stroke_active=false なので即時（従来どおり）実行される。
        let deferring = self.terrain.stroke_active;

        // ── ① フル再メッシュ待ちを優先して消化する ──
        //   同一フレームで同じチャンクが `pending_remesh` と `pending_paint` の両方に
        //   入りうる（密度ブラシとペイントを混ぜたストローク）。フル再メッシュは
        //   頂点カラーも作り直すため、その場合は再メッシュだけを行えば十分であり、
        //   ペイント高速パスを重ねて走らせるのは純粋な無駄になる。
        //   よって先に `pending_paint` から再メッシュ対象を差し引く。
        if !self.terrain.pending_paint.is_empty() {
            let remesh = std::mem::take(&mut self.terrain.pending_remesh);
            self.terrain.pending_paint.retain(|c| !remesh.contains(c));
            self.terrain.pending_remesh = remesh;
        }

        if !self.terrain.pending_remesh.is_empty() {
            let mut coords: Vec<ChunkCoord> =
                std::mem::take(&mut self.terrain.pending_remesh).into_iter().collect();
            coords.sort_by_key(|c| (c.x, c.y, c.z));
            // 描画メッシュ／GPU 差し替えは必須なので必ず行う。`deferring` のときは
            // remesh_chunks 内でフェーズD（コライダー）と RT BLAS prune だけをスキップする
            // （統合バッチ無効化は描画に必要なので毎回維持される）。
            //   defer_gpu_release=false: ブラシ経路は挙動不変（旧 GPU 即解放 + poll(Wait)）。
            //   ブラシ中の poll ストールは本タスクの対象外（移動時 LOD スパイクとは別問題）。
            self.remesh_chunks(&coords, RemeshOptions::immediate().with_deferred_side_effects(deferring));
            if deferring {
                // ── 付随処理は確定時にまとめて処理する。ここではチャンクを積むだけ ──
                self.terrain.stroke_deferred_chunks.extend(coords.iter().copied());
            } else {
                // ── 即時経路（従来どおり）：密度編集で地面が動いた → 散布を貼り直す ──
                //   ここは密度ブラシ由来の経路（pending_remesh）専用である。
                //   ペイント高速パス（下の ②）では **意図的に呼ばない**：
                //   ペイントは密度グリッドを一切変えないため頂点が動かず、
                //   草が宙に浮くことも埋まることも構造的に起こり得ない。
                //   一方でペイントは 1 ストロークで何十回も飛んでくるので、
                //   そこで全インスタンスの柱探索を走らせると目に見えて重くなる。
                self.restick_scatter_for_chunks(&coords);
            }
            did_work = true;
        }

        // ── ② 頂点カラーだけの更新待ちを消化する（ペイント高速パス）──
        //   決定性の担保は ① と同じ理由でソートする（`HashSet` の走査順は実行ごとに変わる）。
        if !self.terrain.pending_paint.is_empty() {
            let mut coords: Vec<ChunkCoord> =
                std::mem::take(&mut self.terrain.pending_paint).into_iter().collect();
            coords.sort_by_key(|c| (c.x, c.y, c.z));
            self.apply_terrain_paint_colors(&coords);
            did_work = true;
        }

        // ── ③ ストローク確定判定 → 遅延していた付随処理を一括適用する ──
        //   確定条件は「マウスアップ済み（stroke_active=false）」または
        //   「最後のブラシ適用から STROKE_IDLE_FLUSH_MS 以上操作が途切れた（無操作）」の
        //   早い方。無操作確定はストロークを止めて眺めているときにコライダー等を追従させる。
        //   遅延チャンクが空なら何もしない（純粋なホバー・ペイントのみのフレーム）。
        {
            let idle_elapsed = self
                .terrain
                .last_brush_apply
                .map(|t| t.elapsed() >= Duration::from_millis(STROKE_IDLE_FLUSH_MS))
                .unwrap_or(false);
            if should_finalize_stroke(
                self.terrain.stroke_deferred_chunks.is_empty(),
                self.terrain.stroke_active,
                idle_elapsed,
            ) {
                self.finalize_stroke_deferred();
                did_work = true;
            }
        }

        // ── flush 全体の合計ログ（実処理が起きたフレームだけ。毎フレーム出すとスパムになる）──
        if did_work && *PERF_TERRAIN_LOG_ENABLED {
            let total_ms = t_flush.elapsed().as_secs_f64() * MILLIS_PER_SEC;
            eprintln!("[PERF terrain] flush total={total_ms:.2}ms (deferring={deferring})");
        }
    }

    /// ストローク中に遅延していた付随処理（コライダー再構築・散布再接地・RT BLAS prune）を
    /// 蓄積チャンク集合に対してまとめて実行し、集合をクリアする。
    ///
    /// `flush_terrain_pending_remesh` の確定判定から呼ばれる（マウスアップ or 無操作タイムアウト）。
    /// 描画メッシュ自体はストローク中も毎フレーム最新化されているため、ここで読む `mc.model` は
    /// 常に確定時点の最新形状であり、そこからコライダー形状を写せる。
    ///
    /// 【許容する一瞬の遅れ（監督判断済み）】
    ///   物理稼働中（Play/編集物理）にストロークすると、確定するまではコライダー・散布・RT 影が
    ///   「1 つ前に確定した形状」のままになる。ストローク中の毎フレーム同期 QBVH 構築が
    ///   フレーム時間を支配するのを避けるための意図的なトレードオフであり、確定時に必ず追従する。
    fn finalize_stroke_deferred(&mut self) {
        if self.terrain.stroke_deferred_chunks.is_empty() {
            self.terrain.last_brush_apply = None;
            return;
        }
        // 決定性のため座標でソートしてから処理する（HashSet 走査順は実行ごとに変わる）。
        let mut coords: Vec<ChunkCoord> =
            std::mem::take(&mut self.terrain.stroke_deferred_chunks).into_iter().collect();
        coords.sort_by_key(|c| (c.x, c.y, c.z));

        // ── ① フェーズD 物理コライダー再構築（物理稼働中のみ）──
        //   ミラー（キャラコン衝突 QBVH 構築）と物理スレッド送信を分離計測する。
        let t_col = Instant::now();
        self.terrain.perf_collider_measuring = *PERF_TERRAIN_LOG_ENABLED;
        self.terrain.perf_collider_mirror = Duration::ZERO;
        self.terrain.perf_collider_send = Duration::ZERO;
        if self.physics_thread.is_some() {
            for &coord in &coords {
                self.sync_terrain_chunk_collider(coord);
            }
        }
        self.terrain.perf_collider_measuring = false;
        if *PERF_TERRAIN_LOG_ENABLED {
            let collider_ms = t_col.elapsed().as_secs_f64() * MILLIS_PER_SEC;
            let mirror_ms = self.terrain.perf_collider_mirror.as_secs_f64() * MILLIS_PER_SEC;
            let send_ms = self.terrain.perf_collider_send.as_secs_f64() * MILLIS_PER_SEC;
            eprintln!(
                "[PERF terrain] collider_rebuild={:.2}ms (mirror={:.2}ms send={:.2}ms) chunks={}",
                collider_ms, mirror_ms, send_ms, coords.len()
            );
        }

        // ── ② 散布再接地（触れたチャンクの散布インスタンスを新しい地表へ貼り直す）──
        let t_rs = Instant::now();
        let insts: usize =
            coords.iter().filter_map(|c| self.terrain.scatter.get(c)).map(|v| v.len()).sum();
        self.restick_scatter_for_chunks(&coords);
        if *PERF_TERRAIN_LOG_ENABLED {
            let restick_ms = t_rs.elapsed().as_secs_f64() * MILLIS_PER_SEC;
            eprintln!(
                "[PERF terrain] restick={:.2}ms insts={} chunks={}",
                restick_ms, insts, coords.len()
            );
        }

        // ── ③ RT BLAS の追従は**ここでは行わない**（意図的な削除。旧実装との差分）──
        //   旧実装はここで確定チャンクをまとめて prune していたが、それでは
        //   「なぞっている最中はずっと黒いまま」という症状が残る。
        //   現在は `remesh_chunks(defer_side_effects=true)` が触れたチャンクを
        //   `rt_blas_prune_pending` へ積み、`flush_rt_blas_prune` が毎フレーム消化するため、
        //   ストローク最後のブラシ適用フレームで最終形状の BLAS へ必ず追従済みである。
        //   ここで prune し直すと、その最新 BLAS を捨てて確定直後に無駄な再構築を
        //   全チャンクぶん走らせるだけになる（＝二重コスト）。

        // 確定済み → 無操作タイムアウトの基準をリセットする。
        self.terrain.last_brush_apply = None;
    }

    /// 指定チャンク群を再メッシュ化し、GPU リソースを VRAM 安全な手順で差し替える。
    ///
    /// `flush_terrain_pending_remesh`（ブラシ／ペイントの集約消化）と
    /// handle_terrain_undo/handle_terrain_redo・チャンク追加（密度を書き換えた直後に
    /// 同期で作り直す経路）の双方から呼ばれる共通処理（DRY）。
    ///
    /// 【フェーズ構成 — poll(Wait) は全体で 1 回だけ】
    ///   0. CPU メッシュ生成（rayon で**チャンク間並列**。GPU に一切触らない純粋処理）
    ///   A. 対象全チャンクの旧 `GpuModel` を drop（`gpu_model = None`）
    ///   B. `device.poll(Wait)` を **1 回だけ** 呼び、遅延破棄をまとめて確定させる
    ///   C. 新 GPU リソースをアップロードして書き戻し → `mark_batch_dirty` →
    ///      派生キャッシュ破棄
    ///
    ///   `poll(Wait)` は GPU が完全にアイドルになるまでブロックする同期点である。
    ///   旧実装はこれを**チャンクごとにループ内**で呼んでいたため、1 ブラシで 4〜8 チャンクが
    ///   触れる典型ケースでは同じ全体同期を 4〜8 回繰り返していた。解放を確定させたいのは
    ///   「全チャンクの旧リソースを手放した後に 1 度」で十分なので、ループの外へ出している。
    ///
    /// 【旧実装の矛盾（発見メモ）】
    ///   旧コードのドキュメントは「旧解放前に新規を確保すると瞬間 VRAM 2 倍需要になる」ため
    ///   drop → poll → 書き戻しの順にする、と説明していた。しかし実際には
    ///   `build_chunk_render` を先に全チャンクぶん回して**新しい GPU リソースを作り切ってから**
    ///   旧を drop していたので、VRAM 2 倍のピークは既に発生しており、コメントの意図は
    ///   守られていなかった。本実装では GPU アップロードをフェーズ C へ移し、
    ///   「CPU メッシュ生成 → 旧 drop → poll 1 回 → 新規アップロード」という順序にしたため、
    ///   VRAM ピークも実際に下がる（CPU 側のメッシュはシステムメモリなので二重に持ってよい）。
    ///
    /// 【付随処理の指定は `RemeshOptions`】各フラグの意味と「どの経路がどれを使うか」は
    ///   `RemeshOptions` の定義（本ファイル冒頭）に集約してある。要約すると:
    ///     ・`immediate()`      … ブラシ確定・undo/redo・チャンク追加・一括収束（全部その場でやる）
    ///     ・`lod_transition()` … 毎フレームの LOD 遷移（GPU 解放は退役キュー・コライダーは触らない）
    ///     ・`with_deferred_side_effects(true)` … ストローク中（付随処理を確定時へ回す）
    fn remesh_chunks(&mut self, coords: &[ChunkCoord], opts: RemeshOptions) {
        let RemeshOptions { defer_side_effects, defer_gpu_release, sync_colliders } = opts;
        if self.draw_ctx.is_none() || coords.is_empty() {
            return;
        }
        // 【保留集合との整合】ここで作り直すチャンクは保留リストから取り除く。
        //   undo/redo・チャンク追加は同期で `remesh_chunks` を直接呼ぶため、
        //   保留が残っていると「同じチャンクを直後にもう一度メッシュ化する」無駄が出る。
        //   入口で flush する案もあるが、それだと undo が「直前のブラシ結果を一度描いてから
        //   巻き戻す」ことになり、無駄な GPU 差し替えを 1 往復ぶん増やす。
        //   除去方式なら常に最新の密度場から 1 回だけ作られるので、こちらを採用する。
        for coord in coords {
            self.terrain.pending_remesh.remove(coord);
            // ペイント保留も同様に取り消す。フル再メッシュは頂点カラーも作り直すため、
            // 直後にペイント高速パスを走らせるのは完全に無駄（結果も同一）になる。
            self.terrain.pending_paint.remove(coord);
            // カバー場（I3.1）の派生データ（地表情報・メッシュ基準値）を捨てる。
            // 積もった量そのものは保持する（少し掘っただけで積雪が消えるのは直感に反する）。
            self.invalidate_cover_for_remesh(*coord);
        }

        let t_total = Instant::now();
        let settings = self.terrain.settings.clone();
        let layers = self.terrain.layers.clone();

        // ── フェーズ 0: CPU メッシュ生成（rayon でチャンク間並列） ──
        //   `build_chunk_cpu_model` は共有参照しか取らない純粋関数なので、複数チャンクを
        //   同時に走らせても互いに干渉しない。`par_iter().map().collect::<Vec<_>>()` は
        //   rayon の IndexedParallelIterator により**入力順を保存する**ため、
        //   出力の並びは並列度・スケジューリングに依らず完全に決定的である。
        let t_cpu = Instant::now();
        let cpu_models: Vec<Option<(Arc<Model>, bool, Arc<Vec<TerrainVertexEdge>>)>> = coords
            .par_iter()
            .map(|&coord| {
                // このチャンクの現在の目標 LOD（未登録＝LOD0）でメッシュ化する。
                let lod = self.terrain.chunk_lod.get(&coord).copied().unwrap_or(0);
                build_chunk_cpu_model(&self.terrain.chunks, &settings, &layers, coord, lod)
            })
            .collect();
        let cpu_ms = t_cpu.elapsed().as_secs_f64() * MILLIS_PER_SEC;

        // 地形チャンクが使うパレットを group3 へ登録する（描画前に済ませる必要がある）。
        // パレット用バインドグループはメッシュ VRAM とは別枠なので、フェーズ A より前でよい。
        self.ensure_terrain_palettes(
            cpu_models.iter().filter_map(|m| m.as_ref()).map(|(model, _, _)| model.as_ref()),
        );

        // ── フェーズ A: 対象全チャンクの旧 GpuModel を手放す ──
        //   ここで手放すのはチャンク専有のリソースなので、他の描画には影響しない。
        //   即時経路（defer_gpu_release=false）: `None` 代入で即 drop（下のフェーズ B で解放確定）。
        //   遅延経路（true）: 即 drop せず退役キューへ move する（GPU 待ちバリアを避けるため）。
        let t_swap = Instant::now();
        // 遅延退役で保持する旧リソース（true のときだけ積む）。旧 InstancedModelBatch は
        // フェーズ C-2 で take するため、ここでは旧 GpuModel だけを先に集める。
        let mut retired: Vec<(Option<GpuModel>, Option<InstancedModelBatch>)> = Vec::new();
        for (&coord, cpu) in coords.iter().zip(cpu_models.iter()) {
            // メッシュ生成に失敗した（チャンクが存在しない）ものは触らない。
            if cpu.is_none() {
                continue;
            }
            let Some(&slot_entity) = self.terrain.chunk_slot_entity.get(&coord) else {
                continue;
            };
            if let Some(scene) = self.scene.as_mut() {
                if let Some(mc) = scene.world.get_mut::<ModelComponent>(slot_entity) {
                    if defer_gpu_release {
                        // in-flight の旧リソースを即 drop すると遅延破棄がフレーム末尾の
                        // submit（snatch read lock 保持）で処理され write lock 再帰でパニックする。
                        // よって drop せず退役キューへ move し、数フレーム後の安全点で解放する。
                        retired.push((mc.gpu_model.take(), None));
                    } else {
                        mc.gpu_model = None;
                    }
                }
            }
        }

        // ── フェーズ B: 遅延破棄をここで 1 回だけ確定させる（GPU アイドル待ち） ──
        //   wgpu 25 の poll API。ループ外に置くことがこの関数の最大の要点。
        //   遅延退役経路（defer_gpu_release=true）ではここで drop していない（退役キューへ move 済み）ため、
        //   確定すべき遅延破棄が無い。よって GPU 待ちバリアを張らない（＝移動時スパイクの排除）。
        let t_poll = Instant::now();
        if !defer_gpu_release {
            if let Some(ctx) = self.draw_ctx.as_ref() {
                let _ = ctx.device.poll(wgpu::PollType::Wait);
            }
        }
        let poll_ms = t_poll.elapsed().as_secs_f64() * MILLIS_PER_SEC;

        // ── フェーズ C-1: 新しい GPU リソースをアップロードする（シリアル） ──
        //   DrawContext は内部可変性（RefCell）を持ち Sync ではないため並列化しない。
        //   `self.draw_ctx` の借用と `self.scene` の可変借用が衝突しないよう、
        //   アップロードだけを先にまとめて済ませてから書き戻す。
        let mut uploaded: Vec<(
            ChunkCoord,
            Arc<Model>,
            Option<GpuModel>,
            Option<InstancedModelBatch>,
            Arc<Vec<TerrainVertexEdge>>,
        )> = Vec::with_capacity(cpu_models.len());
        {
            let ctx = self.draw_ctx.as_ref().unwrap();
            for (&coord, cpu) in coords.iter().zip(cpu_models.into_iter()) {
                // 空メッシュのチャンクは gpu/batch=None で積まれ、下で非描画に差し替わる。
                let Some((model, is_empty, edges)) = cpu else { continue };
                let (gpu, batch) = upload_chunk_model(ctx, &model, is_empty);
                uploaded.push((coord, model, gpu, batch, edges));
            }
        }

        // ── フェーズ C-2: 全チャンクへ書き戻す ──
        // 差し替えたチャンクの batch_key（= ModelComponent::source_path。地形チャンクは
        // マテリアルオーバーライドを持たないため batch_key とビット一致）を集め、
        // 後段でジオメトリ由来の派生キャッシュを破棄する。
        let mut swapped_keys: Vec<String> = Vec::new();
        for (coord, model, gpu, batch, edges) in uploaded {
            // 由来辺キャッシュを最新メッシュのものへ更新する。ここを怠ると、
            // 掘削でメッシュが変わったチャンクを次にペイントしたとき、古い辺で
            // 重みを引き直して頂点数不一致（＝フォールバック）や誤色を招く。
            self.terrain.chunk_vertex_edges.insert(coord, edges);
            let Some(&slot_entity) = self.terrain.chunk_slot_entity.get(&coord) else {
                continue;
            };
            if let Some(scene) = self.scene.as_mut() {
                if let Some(mc) = scene.world.get_mut::<ModelComponent>(slot_entity) {
                    mc.model = Some(model);
                    if defer_gpu_release {
                        // 旧 GpuModel はフェーズ A で take 済み（mc.gpu_model は None）。
                        // 旧 InstancedModelBatch も即 drop せず退役キューへ回す（旧 GpuModel と同じ理由）。
                        let old_batch = std::mem::replace(&mut mc.instanced_batch, batch);
                        mc.gpu_model = gpu;
                        retired.push((None, old_batch));
                    } else {
                        mc.gpu_model = gpu;
                        mc.instanced_batch = batch;
                    }
                    // バッチ更新をマークする。
                    mc.mark_batch_dirty();
                    swapped_keys.push(mc.source_path.clone());
                }
            }
            self.terrain.dirty.insert(coord);
        }

        // ── 遅延退役: 旧リソースを退役キューへ積む（現フレーム番号でタグ付け）──
        //   即 drop しないことで GPU 待ちバリアを避ける。実際の解放は process_terrain_gpu_retire が
        //   TERRAIN_GPU_RETIRE_FRAMES フレーム後の安全点で行う。
        if defer_gpu_release && !retired.is_empty() {
            let f = self.terrain.gpu_retire_frame;
            for (g, b) in retired {
                // 中身が両方 None のエントリは積まない（無駄な保持を避ける）。
                if g.is_some() || b.is_some() {
                    self.terrain.gpu_retire_queue.push_back((f, g, b));
                }
            }
        }

        // ── フェーズ C-3: ジオメトリ由来の派生キャッシュを破棄する（下記メソッドの説明を参照） ──
        //   統合バッチ無効化は描画に必須なので常に行う。
        //
        //   RT BLAS prune は、ストローク遅延中（defer_side_effects）はここで直接行わず
        //   `rt_blas_prune_pending` へ積み、`flush_rt_blas_prune` が毎フレーム予算つきで消化する。
        //   ここで無条件に prune すると 1 ストロークで触れた全チャンクが毎フレーム捨てられ、
        //   BLAS 再構築の上限（MAX_BLAS_BUILDS_PER_FRAME）を越えて影が長く抜けるためである。
        //   **遅らせてはいけない**のが要点で、掘っている最中に BLAS が古い（掘る前の高い）形の
        //   ままだと、レイ原点が地中に沈んで掘った跡が真っ黒になる。
        self.invalidate_geometry_caches(&swapped_keys, !defer_side_effects);
        if defer_side_effects {
            self.terrain.rt_blas_prune_pending.extend(coords.iter().copied());
        }
        let swap_ms = t_swap.elapsed().as_secs_f64() * MILLIS_PER_SEC;

        // ── フェーズ D: 物理稼働中なら地形コライダーを追従再構成する ──
        //   Play（または RigidBody 有効の編集物理）中のみ機能する。物理停止中は no-op。
        //   ジオメトリが変わったチャンクだけを Remove→Add で作り直す（掘り切って空に
        //   なったチャンクはコライダー削除、掘って表面が出たチャンクは新規登録される）。
        //   ペイントは形状不変なので `apply_terrain_paint_colors` 経由でここには来ない。
        //
        //   【遅延】ストローク中（`with_deferred_side_effects(true)`）はスキップする。コライダーの
        //   同期 QBVH 構築（キャラコンミラー）が毎フレームだとフレーム時間を支配するため。
        //   スキップぶんはストローク確定時に `finalize_stroke_deferred` が追従する。
        //   物理稼働中にストロークすると確定までコライダーが 1 つ前の形状のままになるが、
        //   これは監督判断済みの許容トレードオフである。
        //
        //   【LOD 遷移では**そもそも呼ばない**（`lod_transition()`）】
        //   コライダーは表示 LOD に関係なく常にフル解像度（LOD0）で作られるため、
        //   表示 LOD が変わっただけのチャンクを作り直しても結果は 1 ビットも変わらない。
        //   それどころか LOD>0 のチャンクでは描画メッシュを流用できず
        //   `build_chunk_collider_shape`（密度からのフル解像度 MC 再実行）へ落ちるため、
        //   「まったく同じ形を、いちばん高い経路で作り直す」という純粋な浪費だった。
        //   Play 中はカメラが動くたびに LOD 遷移が起きるので、これがフレームを支配していた。
        //   遅延ではなく**不要**なので、確定時の追従（`finalize_stroke_deferred`）も要らない。
        if sync_colliders && self.physics_thread.is_some() {
            // QBVH の同期構築を含む重い区間。呼ばれたフレームだけ計上される。
            crate::profile_scope!("物理/地形コライダー追従");
            for &coord in coords {
                self.sync_terrain_chunk_collider(coord);
            }
        }

        // ── 計測ログ（編集が起きたフレームは毎回出す。間引くとスパイクを取り逃すため） ──
        if *PERF_TERRAIN_LOG_ENABLED {
            let total_ms = t_total.elapsed().as_secs_f64() * MILLIS_PER_SEC;
            // gpu_ms は「アップロード＋書き戻し」から poll 待ちを除いた実作業時間。
            let gpu_ms = (swap_ms - poll_ms).max(0.0);
            eprintln!(
                "[PERF terrain] remesh chunks={} cpu_mesh={:.2}ms gpu_swap={:.2}ms poll_wait={:.2}ms total={:.2}ms",
                coords.len(), cpu_ms, gpu_ms, poll_ms, total_ms
            );
        }
    }

    /// LOD 遷移で遅延退役した旧チャンク GPU リソースを、安全点で解放する。
    ///
    /// 【呼び出し位置（最重要・snatch lock 安全）】必ずフレーム先頭（`handle_redraw_requested` の
    ///   `begin_frame`＝描画コマンド記録より前）で呼ぶこと。この時点では wgpu の snatch **read** lock を
    ///   誰も保持していないため、旧リソースを drop → `poll(Poll)` で遅延破棄を確定しても、フレーム末尾の
    ///   `queue.submit()`（read lock 保持）が破棄を処理して write lock 再帰でパニックする経路を踏まない。
    ///
    /// 【処理】
    ///   1. 退役フレームカウンタを +1 する。
    ///   2. `TERRAIN_GPU_RETIRE_FRAMES` フレーム以上前に退役したリソースを drop する。これらを参照した
    ///      GPU 提出は既に完了している（in-flight 深度はスワップチェーン画像数で上限されるため、
    ///      数フレームで確実に完了）。よって drop は「完了済みリソースの解放」であり安全。
    ///   3. `device.poll(PollType::Poll)` を **1 回だけ**（非ブロッキング）呼び、直前の drop で生じた
    ///      遅延破棄を、read lock 非保持の今このタイミングで確定する。`Poll` は GPU 完了を待たないため
    ///      ストールしない（＝`poll(Wait)` の 80〜130ms を払わない）。完了済みリソースなので確実に reap される。
    ///
    /// 【非ブロッキングで安全な理由】保持しているのは全て `TERRAIN_GPU_RETIRE_FRAMES` フレーム前以前の
    ///   リソースなので、`poll(Poll)` の 1 パス（triage_suspected）で完了扱いとなり解放される。
    ///   まだ完了していないリソースは drop 対象に含めないため、submit が処理して panic する遅延破棄は残らない。
    pub(super) fn process_terrain_gpu_retire(&mut self) {
        // 退役キューが空でもフレームカウンタは前進させる（次の退役の基準を正しく保つ）。
        self.terrain.gpu_retire_frame = self.terrain.gpu_retire_frame.wrapping_add(1);
        let now = self.terrain.gpu_retire_frame;

        // GPU が無い（ヘッドレス等）なら解放しようがない。キューは通常空だが、念のため drop はする
        //   （device が無ければ遅延破棄も submit も走らないので単純 drop で問題ない）。
        let has_ctx = self.draw_ctx.is_some();

        // 解放期限に達したエントリだけを先頭から取り出して drop する。
        //   キューは push 順＝退役フレーム昇順なので、期限未到達に当たった時点で打ち切れる。
        let mut released_any = false;
        while let Some(&(retire_frame, _, _)) = self.terrain.gpu_retire_queue.front() {
            // now - retire_frame >= TERRAIN_GPU_RETIRE_FRAMES で期限到達。
            //   wrapping 前提だが u64 で現実に一周しないため単純減算でよい（安全側に飽和も付ける）。
            if now.saturating_sub(retire_frame) < TERRAIN_GPU_RETIRE_FRAMES {
                break;
            }
            // ここで front を取り出すと、タプル内の GpuModel / InstancedModelBatch が drop される。
            let _dropped = self.terrain.gpu_retire_queue.pop_front();
            released_any = true;
        }

        // drop で生じた遅延破棄を、read lock 非保持の今このタイミングで非ブロッキングに確定する。
        //   何も解放していないフレームでは poll すら不要（無駄な maintain を避ける）。
        if released_any && has_ctx {
            if let Some(ctx) = self.draw_ctx.as_ref() {
                let _ = ctx.device.poll(wgpu::PollType::Poll);
            }
        }
    }

    // ─── 地形の物理コリジョン（静的トライメッシュコライダー）────────────────────

    /// 物理開始時に、全地形チャンクの静的トライメッシュコライダーを物理ワールドへ登録する。
    ///
    /// `start_physics` の末尾（物理スレッド起動＝`physics_thread` が Some になった直後）から
    /// 呼ぶ。空メッシュ（全 AIR／全 SOLID）のチャンクはコライダーを作らずスキップする。
    /// 物理未起動時は no-op。
    pub(super) fn register_all_terrain_colliders(&mut self) {
        if self.physics_thread.is_none() {
            return;
        }
        let t_total = Instant::now();
        let settings = self.terrain.settings.clone();

        // ── ① 各チャンクの (entity_id, position, 描画Model?) をシリアルに集める ──
        //   entity_id 採番（`alloc_terrain_collider_id`）は self.terrain を可変借用するため
        //   並列化できない。ここでは軽い処理（HashMap 参照・Arc ポインタ複製）だけを行い、
        //   重い形状構築は次段の並列パートへ回す。
        //   決定性のため coord をソートしてから採番する（HashMap 走査順は実行ごとに変わる）。
        let mut coords: Vec<ChunkCoord> = self.terrain.chunks.keys().copied().collect();
        coords.sort_by_key(|c| (c.x, c.y, c.z));

        // (entity_id, ワールド原点, coord, 描画Model の複製 or None)
        let mut jobs: Vec<(u64, [f32; 3], ChunkCoord, Option<Arc<Model>>)> =
            Vec::with_capacity(coords.len());
        for coord in coords {
            // 既に生成済みの描画メッシュ（Arc<Model>）を引く。これがあれば MC を再実行せず
            // 頂点・インデックスを写すだけでコライダーを作れる（MC 二重実行の撤廃＝本命）。
            //   ※ self.scene / self.terrain の借用を分けるため Model 取得と id 採番は別文にする。
            //
            // 【コライダーは常に LOD0（フル解像度）】当たり判定は精度優先なので、遠方で
            // 表示 LOD を落としているチャンク（chunk_lod>0）では描画メッシュを流用せず、
            // 下段の MC フォールバック（`build_chunk_collider_shape`＝密度からフル解像度で生成）
            // へ回す。表示だけ粗く・衝突はフル、という分離を保つ。
            let display_lod = self.terrain.chunk_lod.get(&coord).copied().unwrap_or(0);
            let model: Option<Arc<Model>> = if display_lod == 0 {
                self.terrain
                    .chunk_slot_entity
                    .get(&coord)
                    .copied()
                    .and_then(|slot| {
                        self.scene
                            .as_ref()
                            .and_then(|s| s.world.get::<ModelComponent>(slot))
                            .and_then(|mc| mc.model.clone())
                    })
            } else {
                None
            };
            let entity_id = self.alloc_terrain_collider_id(coord);
            let position = coord.world_origin(&settings);
            jobs.push((entity_id, position, coord, model));
        }

        // ── ② コライダー形状＋ミラー用 Rapier コライダー（QBVH 込み）をチャンク間並列で構築する ──
        //   【Play 開始凍結の主因対策】従来は形状（SEED `ColliderShape`）だけを並列構築し、
        //   支配的に重い Rapier トライメッシュ QBVH 構築（`CharacterWorld::build_collider`）は
        //   ③ の直列ループ内（`physics_add_object`→`cw.add_object`）でメインスレッドを占有していた。
        //   地形 322 チャンクぶんの QBVH 直列構築が物理起動時（Play 初回フレーム末）に数秒の凍結を
        //   生む。そこで QBVH 構築もこの並列パスへ移し、③ は「軽い挿入＋スレッド送信」だけにする。
        //   ミラーは③完了時点で従来どおり完全構築されるため、KCC の正しさには影響しない。
        //
        //   Model 再利用は memcpy 相当で軽いが、MC フォールバックが混ざると重くなるため一律に
        //   rayon で並列化する。`par_iter().map().collect::<Vec<_>>()` は入力順を保存するため、
        //   送信順（＝id 割り当て順）は決定的。
        let chunks = &self.terrain.chunks;
        let mut n_reused = 0usize; // 描画メッシュ再利用でコライダーを作った数
        let mut n_mc = 0usize; // MC フォールバックで作った数
        // (物理スレッド送信用 PhysicsObject, ミラー用構築済みコライダー, MC フォールバックだったか)
        let built: Vec<Option<(PhysicsObject, PrebuiltMirrorCollider, bool)>> = jobs
            .par_iter()
            .map(|(id, pos, coord, model)| {
                // bool = 「MC フォールバックだったか」（計測用の内訳集計に使う）。
                let (shape, used_mc) = match model {
                    // 本命: 描画メッシュから写す（MC なし）。空メッシュなら None。
                    Some(m) => (collider_shape_from_model(m)?, false),
                    // フォールバック: 描画メッシュが無いチャンクだけ MC で作る。
                    None => (build_chunk_collider_shape(chunks, &settings, *coord)?, true),
                };
                let obj = terrain_collider_object(*id, *pos, shape);
                // ここで Rapier トライメッシュ QBVH を並列構築する（従来はメインスレッド直列だった）。
                // 地形は trigger ではないため build_collider は必ず Some を返す。
                let pre = CharacterWorld::build_collider(&obj)?;
                Some((obj, pre, used_mc))
            })
            .collect();

        // ── ③ 物理ワールドへ登録する（直列。ミラー挿入＋スレッド送信の軽処理のみ）──
        for entry in built.into_iter().flatten() {
            let (obj, pre, used_mc) = entry;
            if used_mc { n_mc += 1; } else { n_reused += 1; }
            // 物理スレッドとキャラクター衝突ミラーの両方へ集約ヘルパで登録する（並列構築済みコライダー版）。
            self.physics_add_prebuilt(obj, pre);
        }

        // ── 計測ログ ──
        //   [FPS_PHASE]: Play 開始直後の凍結（0fps 区間）の主因である地形コライダー登録の所要時間を、
        //   before/after 比較できるよう **常時 1 行**（1 Play 起動につき 1 回・1/秒未満）で出す。
        //   並列 QBVH 化の効果はこの total_ms の低下で確認できる。
        let total_ms = t_total.elapsed().as_secs_f64() * MILLIS_PER_SEC;
        eprintln!(
            "[FPS_PHASE] terrain_collider_register total={:.1}ms chunks={} (reused_mesh={} mc_fallback={}) mirror_qbvh=parallel",
            total_ms, n_reused + n_mc, n_reused, n_mc
        );
        // 詳細内訳が要るときのプロファイル用（従来ログは SEED_PERF_LOG ゲートのまま残す）。
        if *PERF_TERRAIN_PHYS_LOG_ENABLED {
            eprintln!(
                "[PERF terrain phys] register colliders total={:.2}ms registered={} (reused_mesh={} mc_fallback={})",
                total_ms, n_reused + n_mc, n_reused, n_mc
            );
        }
    }

    /// 地形変形（remesh）に追従して、1 チャンクの地形コライダーを作り直す。
    ///
    /// 物理稼働中のみ機能する（`remesh_chunks` から `physics_thread.is_some()` ゲート付きで
    /// 呼ばれる）。既存コライダーがあれば必ず RemoveObject し、新メッシュが空でなければ同じ
    /// entity_id で AddObject する。掘り切って空になったチャンクは削除のみ（再登録しない）。
    /// 物理スレッドはコマンドを 1 ドレインで Remove→Add の順に処理するため、同一 id の
    /// 削除→追加が安全に成立する。
    fn sync_terrain_chunk_collider(&mut self, coord: ChunkCoord) {
        if self.physics_thread.is_none() {
            return;
        }
        let settings = self.terrain.settings.clone();
        // 既存コライダーを一旦削除する（空チャンク化した場合もこれで消える）。
        if let Some(&old_id) = self.terrain.chunk_collider_ids.get(&coord) {
            // 物理スレッドとキャラクター衝突ミラーの両方から削除する。
            self.physics_remove_object(old_id);
        }
        // 新メッシュを構築。空（掘り切り・チャンク消滅）なら再登録しない。
        //
        // このメソッドは `remesh_chunks` のフェーズ D から呼ばれ、その直前（フェーズ C-2）で
        // 対象チャンクの描画メッシュ（Model）が既に作り直され ModelComponent へ書き戻し済み。
        // よって MC を再実行せず、その最新 Model から形状を写す（＝編集中の三重 MC を撤廃）。
        // Model が引けない稀なケースだけ MC フォールバックする。
        // コライダーは常に LOD0（フル解像度）。表示 LOD を落としているチャンクでは
        // 描画メッシュを流用せず密度からフル解像度で作り直す（当たり判定は精度優先）。
        let display_lod = self.terrain.chunk_lod.get(&coord).copied().unwrap_or(0);
        let model: Option<Arc<Model>> = if display_lod == 0 {
            self.terrain
                .chunk_slot_entity
                .get(&coord)
                .copied()
                .and_then(|slot| {
                    self.scene
                        .as_ref()
                        .and_then(|s| s.world.get::<ModelComponent>(slot))
                        .and_then(|mc| mc.model.clone())
                })
        } else {
            None
        };
        let shape = match model.as_deref() {
            Some(m) => collider_shape_from_model(m),
            None => build_chunk_collider_shape(&self.terrain.chunks, &settings, coord),
        };
        let Some(shape) = shape else {
            return;
        };
        let entity_id = self.alloc_terrain_collider_id(coord);
        let position = coord.world_origin(&settings);
        let obj = terrain_collider_object(entity_id, position, shape);
        // 物理スレッドとキャラクター衝突ミラーの両方へ集約ヘルパで登録する。
        self.physics_add_object(obj);
    }

    /// チャンクの地形コライダー entity_id を取得する（未割り当てなら採番して記録する）。
    ///
    /// 同じチャンクには常に同じ id を返すので、Remove→Add の作り直しで id が安定する。
    fn alloc_terrain_collider_id(&mut self, coord: ChunkCoord) -> u64 {
        if let Some(&id) = self.terrain.chunk_collider_ids.get(&coord) {
            return id;
        }
        let id = self.terrain.next_terrain_collider_id;
        self.terrain.next_terrain_collider_id += 1;
        self.terrain.chunk_collider_ids.insert(coord, id);
        id
    }

    /// レイヤペイント専用の高速パス。**メッシュを一切再生成せず**、頂点カラー（レイヤ重み）
    /// と GPU 頂点バッファだけを差し替える。
    ///
    /// 【成立する理由】
    ///   ペイント（TERRAIN_PAINT）はスプラット場（手ペイント重み・ペイント量）しか
    ///   書き換えず、密度場には一切触れない。マーチングキューブスの出力（頂点位置・法線・
    ///   インデックス・三角形数・頂点の並び順）は密度場だけで決まるので、これらはすべて不変。
    ///   変わるのは頂点カラーとチャンクパレットだけである。
    ///   そこで、メッシュ生成時に記録しておいた「頂点の由来辺」(`chunk_vertex_edges`) から
    ///   `interp_vertex_paint` でスプラットを引き直し、`compute_layer_colors` で色を作る。
    ///   どちらも **フル生成経路（`generate_core` / `terrain_mesh_to_model`）が使うのと
    ///   同一関数**なので、この高速パスの結果はフル再メッシュの結果とビット一致する。
    ///
    /// 【フォールバック条件（フル再メッシュへ回す）】
    ///   1. 由来辺キャッシュが無い（メッシュ化前・キャッシュが失われた）
    ///   2. ModelComponent が引けない／`model` または `gpu_model` が `None`
    ///      （＝空メッシュチャンク。GPU リソースを持たないので書き換え先が無い）
    ///   3. 由来辺の数と CPU モデルの頂点数が食い違う（防御的。起きないはずだが、
    ///      黙って壊れた色を描くより作り直したほうが安全）
    ///   4. **パレットが変わった**。頂点カラー 4 成分は「レイヤ番号」ではなく
    ///      「このチャンクのパレット内スロット」を意味するため、パレットが変われば
    ///      成分の *意味* が変わる。頂点カラーだけ差し替えても描画側は旧パレットで
    ///      解決してしまうので正しく描けない。
    ///      これは「そのチャンクで初めて塗るレイヤが上位 4 層へ入り込んだ瞬間」だけ起き、
    ///      ストローク 1 発目に 1 回起きたあとは同じパレットが続くので以降は高速パスに乗る。
    ///
    /// 【呼ばないもの — この最適化の要点】
    ///   - `invalidate_geometry_caches`: **絶対に呼ばない**。あれはジオメトリが変化した
    ///     ときだけのための処理で（コミット 44bf6a3）、BLAS 再構築と統合バッチ再構築を
    ///     誘発する。形状不変のペイントで毎回呼ぶと BLAS 再構築が走り、この最適化の
    ///     意味が完全に消える。
    ///   - `mark_batch_dirty`: 呼ばない。インスタンス行列（`instance_mats`）は不変であり、
    ///     このフラグはインスタンスデータの再アップロード用だから。
    ///     【描画へ反映される根拠（コード確認済み）】`frame_renderer.rs` は頂点／インデックスを
    ///     `gpu_model_by_path`（＝各 `ModelComponent::gpu_model`。ここで書き換えている実体）から
    ///     引き、`shared_model_batches` はインスタンス行列とカリング用 `cpu_model` しか持たない。
    ///     よって頂点バッファを書き換えれば次のドローで新しい色が出る。
    ///     RT の BLAS も頂点**位置**しか読まないため、色の変更で作り直す必要は無い。
    fn apply_terrain_paint_colors(&mut self, coords: &[ChunkCoord]) {
        if self.draw_ctx.is_none() || coords.is_empty() {
            return;
        }
        let t_total = Instant::now();
        let settings = self.terrain.settings.clone();
        let layers = self.terrain.layers.clone();

        // フル再メッシュへ回すチャンク（フォールバック条件のいずれかに該当したもの）。
        let mut fallback: Vec<ChunkCoord> = Vec::new();
        // 高速パスで実際に色を差し替えたチャンク（未保存マーク用。借用の都合で後回し）。
        let mut painted: Vec<ChunkCoord> = Vec::new();
        // 各フェーズの累積時間（ミリ秒）。
        let mut recalc_ms = 0.0f64;
        let mut colors_ms = 0.0f64;
        let mut upload_ms = 0.0f64;
        // フォールバック理由の内訳（[PERF terrain] paint に出す診断値）。
        //
        // 【なぜ理由まで出すか】
        //   この高速パスは「フォールバックしていないこと」が効果の前提であり、
        //   fallback だけを数えても「なぜ落ちたか」が分からず最適化が効いているのか
        //   判断できない。理由別に数えておけば、ログ 1 行で
        //   「パレット変化（＝仕様どおり・ストローク 1 発目だけ）」なのか
        //   「キャッシュ欠落（＝配線の不備）」なのかを切り分けられる。
        let mut fb_no_edges = 0usize;   // ① 由来辺キャッシュが無い
        let mut fb_no_slot = 0usize;    // ③ スロット／ModelComponent が引けない
        let mut fb_no_gpu = 0usize;     // 空メッシュチャンク（GPU リソース不在）
        let mut fb_vert_mismatch = 0usize; // ④ 頂点数不一致
        let mut fb_palette = 0usize;    // ⑦ パレット変化

        for &coord in coords {
            // ── ① 由来辺キャッシュ（無ければフル再メッシュ）──
            let Some(edges) = self.terrain.chunk_vertex_edges.get(&coord).cloned() else {
                fb_no_edges += 1;
                fallback.push(coord);
                continue;
            };
            // ── ② 密度・スプラットの実体（無い＝既に消えたチャンク。何もしない）──
            if !self.terrain.chunks.contains_key(&coord) {
                continue;
            }
            // ── ③ メッシュを載せている ModelComponent スロット ──
            let Some(&slot_entity) = self.terrain.chunk_slot_entity.get(&coord) else {
                fb_no_slot += 1;
                fallback.push(coord);
                continue;
            };

            // ── ⑤ 由来辺からスプラットを引き直す（rayon でチャンク内並列） ──
            //   `interp_vertex_paint` は `&TerrainChunkData` しか触らない純粋関数なので
            //   並列に走らせても互いに干渉しない。`par_iter().map().collect::<Vec<_>>()` は
            //   rayon の IndexedParallelIterator により**入力順を保存する**ため、
            //   出力の並びはスレッド数・スケジューリングに依らず完全に決定的である。
            let t_recalc = Instant::now();
            let interpolated: Vec<(BlendSlots, f32)> = {
                // `unwrap` は ② の存在確認済みなので安全。
                let chunk = self.terrain.chunks.get(&coord).unwrap();
                edges.par_iter().map(|edge| interp_vertex_paint(chunk, edge)).collect()
            };
            let paint: Vec<BlendSlots> = interpolated.iter().map(|p| p.0).collect();
            let paint_amount: Vec<f32> = interpolated.iter().map(|p| p.1).collect();
            recalc_ms += t_recalc.elapsed().as_secs_f64() * MILLIS_PER_SEC;

            // ── ⑥⑦ 頂点カラーとパレットを求め、パレット変化を判定する ──
            //   `self.scene`（不変借用）と `self.draw_ctx`（不変借用）は別フィールドなので同時に持てる。
            let world_origin = coord.world_origin(&settings);
            let t_colors = Instant::now();
            let rebuilt: Option<Model> = {
                let Some(scene) = self.scene.as_ref() else {
                    fb_no_slot += 1;
                    fallback.push(coord);
                    continue;
                };
                let Some(mc) = scene.world.get::<ModelComponent>(slot_entity) else {
                    fb_no_slot += 1;
                    fallback.push(coord);
                    continue;
                };
                // CPU モデルが無い（まだ一度も構築されていない）ならフル生成に任せる。
                let Some(model) = mc.model.as_ref() else {
                    fb_no_slot += 1;
                    fallback.push(coord);
                    continue;
                };
                // ── 空メッシュチャンクは「何もしない」が正解（フォールバックしない）──
                //   全 AIR / 全 SOLID のチャンクは表面三角形が 0 個で、GPU リソースも
                //   持たない（build_chunk_render が gpu=None を返す）。頂点が 1 つも
                //   無いのだから塗り替える頂点カラーも存在せず、フル再メッシュしても
                //   やはり空メッシュができるだけで画面は 1 ピクセルも変わらない。
                //   ここでフォールバックさせると、ブラシ半径に掛かった空チャンクのぶん
                //   毎ストローク無駄な再メッシュ（＋GPU 差し替え）が走り続ける
                //   （実測: 遅延ペイントの fallback 4 件がすべてこれだった）。
                //   よって単にスキップする。
                if mc.gpu_model.is_none() {
                    continue;
                }
                // 地形チャンクは単一メッシュ・単一プリミティブ・単一マテリアル。
                let (Some(mesh), Some(material)) =
                    (model.meshes.first(), model.materials.first())
                else {
                    fb_no_gpu += 1;
                    fallback.push(coord);
                    continue;
                };
                let Some(prim) = mesh.primitives.first() else {
                    fb_no_gpu += 1;
                    fallback.push(coord);
                    continue;
                };
                // ── ④ 頂点数の整合（防御的。ありえないはずだが黙って壊れるより作り直す）──
                if edges.len() != prim.vertices.len() {
                    fb_vert_mismatch += 1;
                    fallback.push(coord);
                    continue;
                }

                let positions: Vec<[f32; 3]> = prim.vertices.iter().map(|v| v.position).collect();
                let normals: Vec<[f32; 3]> = prim.vertices.iter().map(|v| v.normal).collect();
                let (colors, palette) = compute_layer_colors(
                    &positions, &normals, &paint, &paint_amount, world_origin, &layers,
                );

                // ── ⑦ パレット変化＝頂点カラー成分の意味が変わる → フル再メッシュ ──
                if palette != material.terrain_palette {
                    fb_palette += 1;
                    fallback.push(coord);
                    continue;
                }
                rebuild_terrain_model_with_colors(
                    &prim.vertices, &prim.indices, &model.name, &colors, palette, &layers,
                )
            };
            colors_ms += t_colors.elapsed().as_secs_f64() * MILLIS_PER_SEC;

            let Some(new_model) = rebuilt else {
                // 長さ不一致（④ で弾いているので実質到達しない）。念のため作り直す。
                fallback.push(coord);
                continue;
            };

            // ── ⑧ CPU モデルの差し替え ＋ GPU 頂点バッファの丸ごと書き換え ──
            let t_upload = Instant::now();
            {
                let ctx = self.draw_ctx.as_ref().unwrap();
                let Some(scene) = self.scene.as_mut() else { continue };
                let Some(mc) = scene.world.get_mut::<ModelComponent>(slot_entity) else {
                    continue;
                };
                // CPU 側も更新する。`slot_ops.rs` のマテリアルオーバーライド設定経路が
                // `mc.model` から GPU リソースを作り直すため、ここが古いままだと
                // 後でオーバーライドを付けた瞬間に色が巻き戻る。
                mc.model = Some(Arc::new(new_model));

                // ── GPU 側の平均アルベドも追従させる（RT 反射／水面反射／DDGI／色付き影）──
                //   この高速パスは `GpuModel` を作り直さず頂点バッファだけを書き換えるため、
                //   `gpu_model.avg_albedos`（`GpuModel::upload` が CPU マテリアルから焼いた値）は
                //   放置すると塗る前のチャンク平均色で固定される。結果、地形を塗り替えても
                //   水面に映る色・GI のバウンス色だけが古いまま取り残される。
                //   ここで書き戻すと rt_shadow.rs の静止スキップ シグネチャ
                //  （primitive_avg_albedo をハッシュしている）が変わり、次フレームで
                //   TLAS と instance_table が確実に再構築されて反映される。
                //   地形チャンクはマテリアル 1 枚（material_index=0）固定。
                if let Some(avg) = mc
                    .model
                    .as_ref()
                    .and_then(|m| m.materials.first())
                    .map(|mat| mat.avg_albedo)
                {
                    if let Some(gpu) = mc.gpu_model.as_mut() {
                        if let Some(slot) = gpu.avg_albedos.first_mut() {
                            *slot = avg;
                        }
                    }
                }

                // GPU 頂点バッファは **1 回の write_buffer で全頂点を丸ごと**書き直す。
                //   頂点あたり 16 バイト（color の offset 56）だけを書く方式もあるが、
                //   それだと 17,000 頂点で 17,000 回の write_buffer 呼び出しになり、
                //   1 回あたりの固定コスト（ステージングバッファ確保・コマンド記録）が
                //   支配的になってかえって遅い。72 B × 頂点数（cells=64 で約 1.2 MB）を
                //   `bytemuck::cast_slice` で 1 回転送するほうが速い。
                //   頂点バッファには COPY_DST usage が付いている（gpu_resources.rs）。
                if let (Some(model), Some(gpu)) = (mc.model.as_ref(), mc.gpu_model.as_ref()) {
                    if let (Some(prim), Some(gpu_prim)) = (
                        model.meshes.first().and_then(|m| m.primitives.first()),
                        gpu.meshes.first().and_then(|m| m.primitives.first()),
                    ) {
                        ctx.queue.write_buffer(
                            &gpu_prim.vertex_buffer,
                            0,
                            bytemuck::cast_slice(&prim.vertices),
                        );
                    }
                }
            }
            upload_ms += t_upload.elapsed().as_secs_f64() * MILLIS_PER_SEC;
            painted.push(coord);
        }

        // 未保存マーク（借用が解けてから立てる）。
        for coord in &painted {
            self.terrain.dirty.insert(*coord);
            // ── カバー場（I3.1）の平均アルベドを取り戻す ──
            //   高速パスは `chunk_avg_albedo`（レイヤ重みだけから作る値）で
            //   マテリアルの平均アルベドを作り直すため、カバーの寄与が消える。
            //   頂点位置・uv0（カバー情報）は `..*v` で引き継がれるので絵は変わらないが、
            //   水面反射・RT 反射・DDGI が使う縮退色だけが「雪の無い地面」に戻ってしまう。
            //   カバーが乗っているチャンクは焼き直しへ回して整合を取り戻す
            //   （基準メッシュは無効化しない＝再メッシュではないので基準は今も正しい）。
            if self.terrain.cover.get(coord).is_some_and(|f| !f.is_empty()) {
                self.terrain.cover_pending_apply.insert(*coord);
            }
        }

        // ── ⑨ フォールバック対象はまとめてフル再メッシュへ回す ──
        if !fallback.is_empty() {
            self.remesh_chunks(&fallback, RemeshOptions::immediate());
        }

        // ── 計測ログ（`[PERF terrain] remesh ...` と同じ流儀・同じゲート）──
        if *PERF_TERRAIN_LOG_ENABLED {
            let total_ms = t_total.elapsed().as_secs_f64() * MILLIS_PER_SEC;
            eprintln!(
                "[PERF terrain] paint chunks={} fast={} fallback={} (edges={} slot={} gpu={} verts={} palette={}) recalc={:.2}ms \
                 colors={:.2}ms upload={:.2}ms total={:.2}ms",
                coords.len(), painted.len(), fallback.len(),
                fb_no_edges, fb_no_slot, fb_no_gpu, fb_vert_mismatch, fb_palette,
                recalc_ms, colors_ms, upload_ms, total_ms
            );
        }
    }

    /// 同一 batch_key のままメッシュを差し替えたときに、レンダラ側の
    /// 「batch_key をキーにしたジオメトリ派生キャッシュ」を破棄する。
    ///
    /// 【なぜ必要か】
    ///   地形チャンクの `ModelComponent::source_path`（= `terrain://<scene>/chunk_X_Y_Z`）は
    ///   掘削・盛り上げで再メッシュ化しても不変であり、マテリアルオーバーライドも持たないため
    ///   `batch_key()` は編集の前後で完全に一致する。レンダラは batch_key をキーにして
    ///   ジオメトリ由来のリソースをキャッシュしているので、キーが変わらない差し替えでは
    ///   キャッシュヒットして「古い形状」が使われ続ける。
    ///
    ///   - `RtShadowResources::blas_cache`（BlasKey.source_path == batch_key）
    ///     古い BLAS が残るため、レイトレ影は編集前の地形を遮蔽物として辿る。掘った穴の
    ///     内部が「まだ地面がある」と判定されて全面遮蔽＝真っ黒になる（本バグの主因）。
    ///     さらに TLAS の静止スキップ判定（シグネチャ）も変換・マテリアル依存で
    ///     ジオメトリを含まないため、放置すると TLAS 再構築すら走らない。
    ///     エントリを消せば次フレームに新頂点で BLAS が再構築され、
    ///     `new_blas_built = true` により TLAS も必ず再構築される。
    ///   - `App::shared_model_batches`（キー = batch_key）
    ///     `SharedModelData.cpu_model` は容量不足時しか作り直されないため、古い CPU モデル
    ///     （＝古いローカル AABB・ノード/プリミティブ構成）でカリング用データが計算され続ける。
    ///     エントリを消せば次フレームに新メッシュで統合バッチが作り直される。
    ///
    ///   シーンを開き直すと直るのは、これらのキャッシュが起動/ロード時に空だからである。
    ///
    /// `keys` は差し替えたモデルの batch_key 一覧（空なら何もしない）。
    ///
    /// 【`prune_rt`】RT 加速構造の BLAS を prune するかどうか。統合バッチ（①）は描画に必須なので
    ///   常に破棄するが、RT BLAS（②）はストローク中は遅延したい。ストローク遅延経路は
    ///   `prune_rt=false` で呼んで①だけを毎フレーム行い、確定時に `prune_rt=true` で②を追従させる。
    ///   ブラシ以外の全経路は `prune_rt=true`（従来どおり両方破棄）。
    pub(super) fn invalidate_geometry_caches(&mut self, keys: &[String], prune_rt: bool) {
        if keys.is_empty() {
            return;
        }
        // ① 統合バッチキャッシュ（cpu_model を焼き込み済み）を破棄する。
        //    不在フレーム計数も一緒に消し、遅延 prune の状態を持ち越さない。
        for key in keys {
            self.shared_model_batches.remove(key);
            self.batch_absent_frames.remove(key);
        }
        // ② RT 加速構造の BLAS キャッシュ（＋用途警告集合）を破棄する。
        //    RT 非対応 GPU では draw_ctx.rt_shadow が None のため何もしない。
        //    ストローク遅延中（prune_rt=false）はスキップし、確定時にまとめて prune する。
        if prune_rt {
            if let Some(ctx) = self.draw_ctx.as_ref() {
                if let Some(rt_cell) = ctx.rt_shadow.as_ref() {
                    rt_cell.borrow_mut().prune_source_paths(keys);
                }
            }
        }
    }

    /// 頂点が動いたチャンクの RT 加速構造（BLAS）を、毎フレーム件数予算つきで作り直させる。
    ///
    /// 【解決している不具合】
    ///   ラスタの地表（GPU 頂点バッファ）はカバー焼き込み・密度ブラシ再メッシュで即座に
    ///   動くが、`RtShadowResources::blas_cache` は `source_path` をキーにした
    ///   「一度作ったら作り直さない」キャッシュなので、レイトレが辿る地形は古い形のまま残る。
    ///   地表が**下がった**場合（カバー消去・掘削）、RT 影のレイ原点が古い形状の内側へ沈み、
    ///   レイ原点バイアス（数センチ）では抜け出せないので全面遮蔽＝真っ黒になる。
    ///
    /// 【なぜ毎フレームなのか】
    ///   ストローク中の黒はストローク終了後に直しても遅い（なぞっている最中がいちばん見える）。
    ///   よってストローク中かどうかで一切分岐せず、積まれた端から消化する。
    ///
    /// 【予算をつける理由】
    ///   カバー焼き直しは 27 近傍をまとめて焼くため、1 回で 27 チャンクぶん積まれうる。
    ///   一方 BLAS の再構築は `MAX_BLAS_BUILDS_PER_FRAME` 個／フレームに絞られている
    ///   （超えると 1 submit の GPU 占有が TDR しきい値を越えてデバイスロストする）。
    ///   したがって捨てる側も同じ数で頭打ちにするのが正しい。これより多く捨てても
    ///   再構築が追いつかず、影が抜けている時間が伸びるだけである。
    ///   予算を超えたぶんは集合に残り、次フレーム以降で消化される。
    ///
    /// 【捨ててから作り直されるまでの見え方】
    ///   `prepare_and_build` の TLAS 詰め直しは `blas_cache.get()==None` のインスタンスを
    ///   素通りするため、その数フレームは当該チャンクが RT 影のオクルーダから外れる。
    ///   RT 影がそのチャンクぶんだけ薄くなるが、古い形で誤遮蔽して真っ黒になるより
    ///   はるかに軽微であり、意図した安全側の縮退である。
    pub(super) fn flush_rt_blas_prune(&mut self) {
        if self.terrain.rt_blas_prune_pending.is_empty() {
            return;
        }
        // ── ① このフレームで消化する分を選ぶ（決定性のため座標順・予算で頭打ち）──
        let batch = select_rt_prune_batch(
            &self.terrain.rt_blas_prune_pending,
            crate::engine::core::renderer::rt_shadow::MAX_BLAS_BUILDS_PER_FRAME,
        );
        for coord in &batch {
            self.terrain.rt_blas_prune_pending.remove(coord);
        }

        // ── ② チャンクスロットの `source_path`（= batch_key = BlasKey.source_path）を集める ──
        let mut keys: Vec<String> = Vec::with_capacity(batch.len());
        if let Some(scene) = self.scene.as_ref() {
            for &coord in &batch {
                if let Some(&slot) = self.terrain.chunk_slot_entity.get(&coord) {
                    if let Some(mc) = scene.world.get::<ModelComponent>(slot) {
                        keys.push(mc.source_path.clone());
                    }
                }
            }
        }
        // prune_rt=true: RT BLAS を捨てる。次フレームに最新頂点で作り直され、
        // `new_blas_built=true` により TLAS も必ず組み直される。
        let t = Instant::now();
        self.invalidate_geometry_caches(&keys, true);
        if *PERF_TERRAIN_LOG_ENABLED && !keys.is_empty() {
            eprintln!(
                "[PERF terrain] rt_blas_prune chunks={} remain={} take={:.2}ms",
                keys.len(),
                self.terrain.rt_blas_prune_pending.len(),
                t.elapsed().as_secs_f64() * MILLIS_PER_SEC
            );
        }
    }

    /// 現在進行中のブラシストロークを 1 つの undo エントリとして確定する（TERRAIN_STROKE_END）。
    ///
    /// stroke_before（ストローク開始時点のスナップショット）が空でなければ、その各チャンクの
    /// 現在密度を after として集めて TerrainEdit を undo_stack へ push し、redo_stack をクリアする
    /// （新しい編集が確定したら、それより後の未来を指していた redo 履歴は無効になるため）。
    /// stroke_before が空（＝実質何も変化しないまま終わった）場合は単に stroke_active を戻すだけ。
    ///
    /// 【遅延付随処理の確定】ここで `stroke_active=false` にすると、直後に必ず呼ばれる
    /// `flush_terrain_pending_remesh`（IPC ループ末尾で毎フレーム実行）の確定判定が真になり、
    /// ストローク中に溜めたコライダー再構築・散布再接地・RT BLAS prune が一括適用される。
    /// このメソッド自身は付随処理を起動しない（flush が最終形状で 1 回だけ行うのが正しい）。
    pub(super) fn handle_terrain_stroke_end(&mut self) {
        // ── カバーブラシのぶんを先に取り出す（実際に変化したチャンクだけが残る）──
        //   密度・ペイントとカバーは同じストロークに混ざりうる（ツールは排他だが、
        //   ツールを切り替えても左ボタンを離すまでは 1 ストロークである）。
        //   どちらか一方でも変化があれば 1 つの Undo エントリとして積む。
        let (cover_before, cover_after) = self.take_cover_stroke_snapshots();

        if !self.terrain.stroke_before.is_empty() || !cover_before.is_empty() {
            // before を丸ごと取り出す（以後 stroke_before は空に戻る）。
            let before = std::mem::take(&mut self.terrain.stroke_before);

            // ── before と同じチャンク集合について、現在（ストローク終了時点）の密度を after として集める ──
            let mut after: HashMap<ChunkCoord, ChunkSnapshot> = HashMap::with_capacity(before.len());
            for &coord in before.keys() {
                if let Some(chunk) = self.terrain.chunks.get(&coord) {
                    after.insert(coord, ChunkSnapshot::capture(chunk));
                }
            }

            self.terrain.undo_stack.push(TerrainEdit { before, after, cover_before, cover_after });
            // 上限を超えたら最古のエントリを破棄する（無制限にメモリを食わせない）。
            if self.terrain.undo_stack.len() > TERRAIN_UNDO_MAX {
                self.terrain.undo_stack.remove(0);
            }
            // 新しい編集が確定したので、以前の undo から辿れた redo 履歴は無効化する
            // （シーン全体の UndoHistory と同じ規約）。
            self.terrain.redo_stack.clear();
        }
        self.terrain.stroke_active = false;
    }

    /// terrain 専用 undo（TERRAIN_UNDO）。undo_stack から直近のエントリを取り出し、
    /// 各チャンクの密度を before（編集前）へ書き戻して再メッシュ化し、redo_stack へ積む。
    ///
    /// **戻すカバー場はカバーブラシで手編集したぶんだけ**である。エミッタの
    /// シミュレート・全消去はメイン履歴（`UNDO`）の管轄であり、そちらはここでは戻らない
    /// （両方に載せると 1 回の操作を 2 回戻せてしまう）。線引きは `TerrainEdit` の
    /// コメントと docs/cover_field.md §5 を参照。
    pub(super) fn handle_terrain_undo(&mut self) {
        let Some(edit) = self.terrain.undo_stack.pop() else {
            return;
        };
        let mut touched: Vec<ChunkCoord> = Vec::with_capacity(edit.before.len());
        for (&coord, snapshot) in &edit.before {
            // チャンクが存在する場合のみ書き戻す（ハイトマップ再読込等で消えている可能性に備える）。
            // 密度とスプラットを丸ごと戻すため、密度ブラシとペイントブラシが混在した
            // ストロークでも 1 回の undo で完全に元へ戻る。
            if let Some(chunk) = self.terrain.chunks.get_mut(&coord) {
                snapshot.restore(chunk);
                touched.push(coord);
            }
        }
        self.remesh_chunks(&touched, RemeshOptions::immediate());
        // ── 密度を戻すと地面も戻るので、散布プロップを新しい地表へ貼り直す ──
        //   【散布そのものは undo されない（T3 第1段のスコープ外）】
        //   undo/redo スタックが持つのは密度とスプラットのスナップショットだけで、
        //   .tscatter の内容は含まれない。したがって「草を生やす → undo」で
        //   草は消えない。
        //   それでも再接地だけは掛ける。掛けないと「undo したら草だけ空中に
        //   取り残される」という明確に壊れた見た目になるからである。
        //   再接地により「草は常に今の地面に載っている」という不変条件は保たれる。
        self.restick_scatter_for_chunks(&touched);
        // ── カバーブラシで手編集したぶんを書き戻す ──
        //   書き戻し・.tcover のダーティ化・頂点の焼き直し予約は
        //   メイン履歴の undo と同じ 1 本の経路（`restore_cover_snapshots`）を通す。
        //   カバー場は密度を一切変えないので、ここで再メッシュは要らない。
        self.restore_cover_snapshots(&edit.cover_before);
        // 密度が戻った＝ショアフィールドの焼き直し対象（Phase W1.5）。
        // カバーだけのエントリ（カバーブラシのストローク）では密度が 1 ビットも
        // 変わっていないので、焼き直しを要求しない（無駄な再計算を誘発しない）。
        if !touched.is_empty() {
            self.terrain_edit_version += 1;
        }
        self.terrain.redo_stack.push(edit);
    }

    /// terrain 専用 redo（TERRAIN_REDO）。redo_stack から直近のエントリを取り出し、
    /// 各チャンクの密度を after（編集後）へ書き戻して再メッシュ化し、undo_stack へ積み直す。
    pub(super) fn handle_terrain_redo(&mut self) {
        let Some(edit) = self.terrain.redo_stack.pop() else {
            return;
        };
        let mut touched: Vec<ChunkCoord> = Vec::with_capacity(edit.after.len());
        for (&coord, snapshot) in &edit.after {
            if let Some(chunk) = self.terrain.chunks.get_mut(&coord) {
                snapshot.restore(chunk);
                touched.push(coord);
            }
        }
        self.remesh_chunks(&touched, RemeshOptions::immediate());
        // undo と同じ理由で再接地する（散布自体は redo の対象外）。
        self.restick_scatter_for_chunks(&touched);
        // カバーブラシのぶんを「編集後」へ進める（undo の対称）。
        self.restore_cover_snapshots(&edit.cover_after);
        // undo と同じ理由（Phase W1.5。カバーだけのエントリでは密度が変わらない）。
        if !touched.is_empty() {
            self.terrain_edit_version += 1;
        }
        self.terrain.undo_stack.push(edit);
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

        // ── 散布データ（.tscatter）を .tvox の隣へ保存する ──
        //   密度と散布は更新頻度が独立しているため別ファイルだが、保存は同時に行う
        //   （片方だけ保存できると地形と草がずれた状態がディスクに残るため）。
        //   インスタンスが 0 本のチャンクはファイルを **削除** する。残すと
        //   次回ロードで消したはずの草が復活する。
        let (scatter_written, scatter_removed) = self.save_terrain_scatter(&dir, false);

        // ── カバー場（.tcover）を .tvox の隣へ保存する（I3.1）──
        //   散布と同じ理由で別ファイル・同時保存。量 0 のチャンクはファイルを **削除** する
        //   （残すと次回ロードで消したはずの雪が復活する）。
        let (cover_written, cover_removed) = self.save_terrain_cover(&dir, false);

        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("TERRAIN_SAVE_OK:{count}"));
        }
        if *PERF_TERRAIN_LOG_ENABLED {
            eprintln!(
                "[SEED terrain] save: tvox={count} tscatter written={scatter_written} removed={scatter_removed}                  tcover written={cover_written} removed={cover_removed}"
            );
        }
    }

    /// シーン保存（Ctrl+S）に相乗りして、**変更のあった地形チャンクだけ**を書き出す。
    ///
    /// 【なぜシーン保存へ足すのか】
    ///   地形の実体（密度 .tvox / 散布 .tscatter / カバー .tcover）は .scene の外にあり、
    ///   従来は「地形を保存」ボタン（TERRAIN_SAVE）を別に押さないとディスクへ落ちなかった。
    ///   掘った・草を生やした・雪を積もらせた直後に Ctrl+S だけしてエディタを閉じると
    ///   その作業が丸ごと消える、という取り返しのつかない失われ方をする。
    ///   「保存」と言われたら地形も保存されるのが唯一素直な挙動である。
    ///
    /// 【`handle_terrain_save` と分けてある理由】
    ///   あちらは *全チャンク*を無条件に書く（ロード時の確実な復元が目的）。
    ///   シーン保存のたびに全チャンクを書き直すと、地形を一切触っていない
    ///   セッションでも Ctrl+S が地形の規模に比例して遅くなる。
    ///   こちらはダーティ集合だけを書き、**ダーティが空なら 1 バイトも触らない**
    ///   （ディレクトリ作成すら行わない＝完全にゼロコスト）。
    ///
    /// 【IPC を送らない理由】
    ///   呼び出し元（SaveScene ハンドラ）が SAVE_OK / SAVE_ERROR を送る。
    ///   ここでも TERRAIN_SAVE_OK を送るとエディタの保存完了通知が二重に走るため、
    ///   結果は戻り値で返し、通知の作法は呼び出し元に委ねる。
    ///
    /// 戻り値は「書き出した／削除したファイルの総数」。失敗時は `Err(理由)`。
    ///
    /// 【Play 中はカバー場を書かない】
    ///   Play 中の積算・轍は**揮発**が仕様であり（Stop で Edit の保存状態へ戻る）、
    ///   Play 中の Ctrl+S でそれをディスクへ焼くと「消えるはずの雪が永久に残る」。
    ///   スナップショット（`cover_play_snapshot`）が生きている＝Play 中の判定である。
    ///   `cover_dirty` は消さずに残すので、Stop 後の保存でちゃんと書かれる。
    pub(super) fn flush_dirty_terrain(&mut self) -> Result<u32, String> {
        // Play 中のカバー場は揮発なので保存対象から外す。
        let cover_persistable = self.terrain.cover_play_snapshot.is_none();

        // ─── ダーティが 1 つも無ければ即座に帰る（保存経路を遅くしない）───
        if self.terrain.dirty.is_empty()
            && self.terrain.scatter_dirty.is_empty()
            && (self.terrain.cover_dirty.is_empty() || !cover_persistable)
        {
            return Ok(0);
        }

        let settings = self.terrain.settings.clone();
        let scene = self.terrain.scene_name.clone();
        let Some(root) = crate::engine::asset_fs::root() else {
            return Err("assets root unresolved".to_string());
        };
        let dir = root.join("terrain").join(&scene);
        if let Err(e) = std::fs::create_dir_all(&dir) {
            return Err(e.to_string());
        }

        // ─── 密度（.tvox）: ダーティなチャンクだけ ───
        let mut voxel_written = 0u32;
        let dirty: Vec<ChunkCoord> = self.terrain.dirty.iter().copied().collect();
        for coord in dirty {
            let Some(chunk) = self.terrain.chunks.get(&coord) else { continue };
            let bytes = tvox::write_chunk(chunk, coord, &settings);
            let path = dir.join(tvox_file_name(coord));
            match std::fs::write(&path, &bytes) {
                Ok(()) => voxel_written += 1,
                Err(e) => eprintln!("[SEED terrain] save failed: {path:?} err={e}"),
            }
        }
        self.terrain.dirty.clear();

        // ─── 散布（.tscatter）・カバー（.tcover）も同じくダーティ分だけ ───
        //   片方だけ保存できると地形と草・雪がずれた状態がディスクに残るため、
        //   `handle_terrain_save` と同じく必ず 3 種そろえて書く。
        let (scatter_written, scatter_removed) = self.save_terrain_scatter(&dir, true);
        let (cover_written, cover_removed) =
            if cover_persistable { self.save_terrain_cover(&dir, true) } else { (0, 0) };
        let total =
            voxel_written + scatter_written + scatter_removed + cover_written + cover_removed;

        if *PERF_TERRAIN_LOG_ENABLED {
            eprintln!(
                "[SEED terrain] scene-save flush: tvox={voxel_written} tscatter written={scatter_written} removed={scatter_removed} tcover written={cover_written} removed={cover_removed}"
            );
        }
        Ok(total)
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
        // シーンロードで地形が丸ごと入れ替わる＝ショアフィールドは全面的に無効（Phase W1.5）。
        // `TerrainState` はここで作り直されるためカウンタを持たせられず、App 側に置いてある。
        self.terrain_edit_version += 1;
        // 地形状態をリセットしてシーン名を取り込む。
        // ブラシ形状マスクのパスは「道具の設定」なので持ち越す（handle_terrain_init と同じ理由）。
        let brush_mask_path = std::mem::take(&mut self.terrain.brush_mask_path);
        self.terrain = TerrainState::default();
        self.terrain.brush_mask_path = brush_mask_path;
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
        //
        //   【チャンク構成の復元】
        //     .tvox ヘッダは samples_per_axis（= chunk_cells + 1）と voxel_size を持つ。
        //     チャンク分割数はエディタから変更できるため、既定値（32 / 0.5m）とは限らない。
        //     そこで **最初に読めたチャンクのヘッダを正として settings へ取り込み**、
        //     以降のチャンクはそのヘッダと一致するものだけを受け入れる。
        //     不一致チャンク（＝分割数を変えた後に一部だけ古い .tvox が残っている状態）は
        //     読み込むと密度配列の長さが食い違って描画・編集が破綻するため、警告して捨てる。
        // ロード全体の計測開始（[PERF terrain load]。SEED_PERF_LOG で有効化）。
        //   走査（walk）は軽いので、支配項である tvox I/O 以降を total の起点にする。
        let t_total = Instant::now();
        let t_io = Instant::now();
        let mut adopted: Option<tvox::TvoxHeader> = None;
        let mut mismatched = 0u32;
        let mut loaded: Vec<(ChunkCoord, Entity)> = Vec::new();
        // 散布データ読み込み用の (チャンク座標, .tvox 仮想パス) 対。
        // .tscatter のパスは .tvox の拡張子を差し替えて導く（規則を 1 か所に閉じる）。
        let mut scatter_paths: Vec<(ChunkCoord, String)> = Vec::new();
        for (coord, path, mc_slot) in &found {
            let bytes = match crate::engine::asset_fs::read_bytes(path) {
                Ok(b) => b,
                Err(e) => {
                    eprintln!("[SEED terrain] tvox missing, skip: {path} err={e}");
                    continue;
                }
            };
            // 本体を読む前にヘッダで構成を突き合わせる（不一致なら本体を読む必要がない）。
            let header = match tvox::read_header(&bytes) {
                Ok(h) => h,
                Err(e) => {
                    eprintln!("[SEED terrain] tvox header invalid, skip: {path} err={e:?}");
                    continue;
                }
            };
            match adopted {
                None => {
                    // 最初の 1 枚の構成を地形全体の構成として採用する。
                    self.terrain.settings.apply_chunk_config(
                        self.terrain.settings.ground_chunks_x,
                        self.terrain.settings.ground_chunks_z,
                        header.chunk_cells(),
                        header.voxel_size,
                    );
                    eprintln!(
                        "[SEED terrain] adopted chunk config from tvox: cells={}, voxel={}m",
                        self.terrain.settings.chunk_cells, self.terrain.settings.voxel_size
                    );
                    adopted = Some(header);
                }
                Some(first) => {
                    if header.samples_per_axis != first.samples_per_axis
                        || header.voxel_size != first.voxel_size
                    {
                        mismatched += 1;
                        eprintln!(
                            "[SEED terrain] tvox chunk config mismatch, skip: {path} \
                             (samples={} voxel={} / expected samples={} voxel={})",
                            header.samples_per_axis, header.voxel_size,
                            first.samples_per_axis, first.voxel_size
                        );
                        continue;
                    }
                }
            }
            match tvox::read_chunk(&bytes) {
                Ok((chunk, _stored_coord)) => {
                    self.terrain.chunks.insert(*coord, chunk);
                    self.terrain.chunk_slot_entity.insert(*coord, *mc_slot);
                    loaded.push((*coord, *mc_slot));
                    // 散布データ（.tscatter）は .tvox の隣に置かれている。
                    // 読み込みは密度が全チャンク揃ってから行う（下のフェーズ 1.5）。
                    scatter_paths.push((*coord, path.clone()));
                }
                Err(e) => {
                    eprintln!("[SEED terrain] tvox decode failed, skip: {path} err={e:?}");
                }
            }
        }
        if mismatched > 0 {
            eprintln!(
                "[SEED terrain] {mismatched} chunk(s) skipped due to incompatible voxel config; \
                 地形を初期化し直して保存してください"
            );
        }
        if loaded.is_empty() {
            return;
        }
        let io_ms = t_io.elapsed().as_secs_f64() * MILLIS_PER_SEC;

        // ── フェーズ 1.5: 散布データ（.tscatter）を読む ──
        //   ファイルが無いのは **エラーではない**（散布機能より前に保存された
        //   シーンには存在しない）。欠落チャンクは単に散布 0 本として扱う。
        //   プロップ定義も併せて読む（インスタンスの prop_id を解決するのに要る）。
        self.ensure_terrain_props();
        self.load_terrain_scatter(&scatter_paths);

        // ── フェーズ 1.6: カバー場（.tcover）を読む（I3.1）──
        //   散布と同じく、ファイルが無いのはエラーではない（カバー機能より前に
        //   保存されたシーンには存在しない）。素材定義（cover_materials.json）は
        //   このあとのフェーズ 2 で `ensure_terrain_layers` が読み直す
        //   （素材表は GPU のレイヤ uniform に同居しており、CPU と GPU の両方を
        //     同時に更新できる経路がそこしか無いため）。
        self.load_terrain_cover(&scatter_paths);

        // ── フェーズ 2: 全チャンク読込後にメッシュ化（隣接読みが揃った状態で継ぎ目を正しく作る）──
        //   レイヤ定義もここで読み直す（.tvox v1 のようにスプラットを持たないデータでも
        //   ルール自動生成が効くようにするため、定義は常に手元に必要）。
        let t_layers = Instant::now();
        self.ensure_terrain_layers();
        let layers_ms = t_layers.elapsed().as_secs_f64() * MILLIS_PER_SEC;
        let settings = self.terrain.settings.clone();
        let layers = self.terrain.layers.clone();
        let mut prebuilt: Vec<(Entity, Arc<Model>, Option<GpuModel>, Option<InstancedModelBatch>)> = Vec::new();
        // 由来辺は借用の都合で一旦ローカルへ溜め、この後 self.terrain へ入れる。
        let mut prebuilt_edges: Vec<(ChunkCoord, Arc<Vec<TerrainVertexEdge>>)> = Vec::new();

        // ── フェーズ 2a: CPU メッシュ生成をチャンク間で rayon 並列化する ──
        //   `build_chunk_cpu_model` は共有参照しか取らない純粋関数（他チャンクのメッシュに
        //   依存せず副作用も無い）。48 チャンクを逐次に回すと MC（実測 cells=32 で約 30ms、
        //   cells=64 で約 140ms/チャンク）が支配的にロード時間を食う。これは編集経路の
        //   `remesh_chunks` フェーズ 0 と同一の並列化であり、`par_iter().map().collect()` は
        //   rayon の IndexedParallelIterator により**入力順を保存する**ため、出力の並びは
        //   並列度・スケジューリングに依らず逐次実行と完全に一致する（決定的）。
        let t_mc = Instant::now();
        let cpu_models: Vec<Option<(Arc<Model>, bool, Arc<Vec<TerrainVertexEdge>>)>> = loaded
            .par_iter()
            .map(|(coord, _mc_slot)| build_chunk_cpu_model(&self.terrain.chunks, &settings, &layers, *coord, 0))
            .collect();
        let mc_ms = t_mc.elapsed().as_secs_f64() * MILLIS_PER_SEC;

        // ── フェーズ 2b: GPU アップロードは直列（DrawContext は Sync でないため並列化しない）──
        //   入力順（loaded 順）を保った zip で、空メッシュ（gpu/batch=None）も含めて畳む。
        let t_upload = Instant::now();
        {
            let ctx = self.draw_ctx.as_ref().unwrap();
            for ((coord, mc_slot), cpu) in loaded.iter().zip(cpu_models.into_iter()) {
                // 空メッシュチャンクは gpu/batch=None で返る（非描画のまま MC を埋める）。
                let Some((model, is_empty, edges)) = cpu else { continue };
                let (gpu, batch) = upload_chunk_model(ctx, &model, is_empty);
                prebuilt.push((*mc_slot, model, gpu, batch));
                prebuilt_edges.push((*coord, edges));
            }
        }
        let upload_ms = t_upload.elapsed().as_secs_f64() * MILLIS_PER_SEC;
        // 由来辺キャッシュを登録する（`self.terrain` はこの関数の冒頭で default に
        // 差し替わっているため、前のシーンの辺が混ざることはない）。
        for (coord, edges) in prebuilt_edges {
            self.terrain.chunk_vertex_edges.insert(coord, edges);
        }
        // 地形チャンクが使うパレットを group3 へ登録する（描画前に済ませる必要がある）。
        let t_palettes = Instant::now();
        self.ensure_terrain_palettes(prebuilt.iter().map(|p| p.1.as_ref()));
        let palettes_ms = t_palettes.elapsed().as_secs_f64() * MILLIS_PER_SEC;

        // ── フェーズ 3: ロード時に model=None で作られた ModelComponent を埋める ──
        let mut loaded_keys: Vec<String> = Vec::new();
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
                    loaded_keys.push(mc.source_path.clone());
                }
            }
        }
        // 同一シーンを同一セッション中に再ロードすると batch_key が前回と一致するため、
        // ジオメトリ由来の派生キャッシュ（BLAS・統合バッチ）を破棄して作り直させる。
        self.invalidate_geometry_caches(&loaded_keys, true);

        // ── 計測ログ（[PERF terrain load]。SEED_PERF_LOG で有効化。ロードは 1 シーン 1 回なので毎回出す）──
        //   内訳: tvox I/O（フェーズ1）/ layers 読込 / MC 生成（並列）/ GPU アップロード / パレット登録。
        if *PERF_TERRAIN_LOG_ENABLED {
            let total_ms = t_total.elapsed().as_secs_f64() * MILLIS_PER_SEC;
            eprintln!(
                "[PERF terrain load] chunks={} cells={} tvox_io={:.2}ms layers={:.2}ms \
                 mc_mesh={:.2}ms gpu_upload={:.2}ms palettes={:.2}ms total={:.2}ms",
                loaded.len(), settings.chunk_cells,
                io_ms, layers_ms, mc_ms, upload_ms, palettes_ms, total_ms
            );
        }
    }

    /// スモークテスト（環境変数 SEED_TERRAIN_SMOKE=1）専用の常設デバッグフック。
    ///
    /// 地形を初期化し、デバッグカメラを地形フットプリント全体が見える位置へ向け、
    /// 明確に地形を変形させる（盛り 1・掘り 1）。通常の Play/Edit では呼ばれない。
    pub(super) fn run_terrain_smoke(&mut self) {
        use crate::engine::core::app_base::ipc::TerrainChunkConfig;

        // ── ① チャンク構成の指定つき初期化 → チャンク追加 の確認 ──
        //   小さめの構成（分割数も既定と違う値）で作り、そこへチャンクを足して
        //   「構成指定が効くこと」「既存を保ったまま広げられること」を実機で通す。
        //   本編のスクリーンショット構図に影響しないよう、確認後に既定構成で作り直す。
        self.handle_terrain_init(Some(TerrainChunkConfig {
            chunks_x:    SMOKE_CONFIG_CHUNKS,
            chunks_z:    SMOKE_CONFIG_CHUNKS,
            chunk_cells: SMOKE_CONFIG_CHUNK_CELLS,
            voxel_size:  SMOKE_CONFIG_VOXEL_SIZE,
        }));
        let small_chunks = self.terrain.chunks.len();
        let small_cells = self.terrain.settings.chunk_cells;
        // +X 側へ 1 列ぶんチャンクを足す（既存 0..chunks-1 の外側）。
        let add_from = SMOKE_CONFIG_CHUNKS as i32;
        self.handle_terrain_add_chunks(add_from, 0, add_from, SMOKE_CONFIG_CHUNKS as i32 - 1);
        eprintln!(
            "[SEED terrain] smoke: config init cells={small_cells} chunks={small_chunks} \
             -> after add_chunks chunks={}",
            self.terrain.chunks.len()
        );

        // ── ② 既定構成へ戻して本編のスモークを始める ──
        //   引数なし（None）ではなく明示的に既定値を渡す。①で settings が
        //   小さい構成へ書き換わっているため、None だとそれが引き継がれてしまう。
        // チャンク数は SEED_SMOKE_CHUNKS で上書き可（描画カリング計測用に 16×16 等へ拡大）。
        let smoke_chunks = std::env::var(SMOKE_CHUNKS_ENV)
            .ok()
            .and_then(|s| s.parse::<u32>().ok())
            .filter(|v| *v > 0)
            .unwrap_or(SMOKE_DEFAULT_CHUNKS);
        self.handle_terrain_init(Some(TerrainChunkConfig {
            chunks_x:    smoke_chunks,
            chunks_z:    smoke_chunks,
            chunk_cells: SMOKE_DEFAULT_CHUNK_CELLS,
            voxel_size:  SMOKE_DEFAULT_VOXEL_SIZE,
        }));

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
        // 一人称視点の上書き（描画カリング計測用）: `SEED_SMOKE_FPV=1` で、地表付近に立って
        //   水平を見る構図にする。俯瞰は全チャンクが視界に入り（カリングが効かない）が、一人称では
        //   背後・側方のチャンクが視錐台外になり、地形チャンク描画カリングの効果が顕著に出る。
        let (eye, yaw, pitch, fov_deg, far) = if std::env::var_os("SEED_SMOKE_LOOKAWAY").is_some() {
            // 視界外（描画ほぼゼロ）計測用（`SEED_SMOKE_LOOKAWAY=1`）: フットプリントの外
            //   （-Z 側へ span ぶん離れた位置）に立ち、地形と反対（-Z 方向）を向く。地形は全
            //   チャンクがカメラ背後に来るため視錐台カリングで 1 枚も残らず（drawn≈0）、
            //   「地形を全く見ていない」構図を厳密に再現できる。これにより視界に関係なく毎フレーム
            //   走る処理（merge_map 再構築・tick_terrain_lod・カリング判定など）だけを [PERF] で
            //   切り出せる。※Play/スタンドアロンはゲームカメラ描画のためデバッグカメラの向きが
            //   効かない。この構図で描画するには `--mode=edit` で起動すること。
            const LOOKAWAY_EYE_HEIGHT: f32 = 12.0;
            (
                [center[0], center[1] + LOOKAWAY_EYE_HEIGHT, -span],
                std::f32::consts::PI,       // yaw=180°（-Z 方向＝地形と反対を向く）
                0.0f32,                     // 水平（地形は真後ろ）
                SMOKE_CAM_FOV_DEG,
                SMOKE_CAM_FAR,
            )
        } else if std::env::var_os("SEED_SMOKE_FPV").is_some() {
            // フットプリント中心に立ち、ほぼ水平（やや下 8°）で +Z 方向を見る。目線高さは
            // ハイトマップ最大起伏（10m）＋急斜面デモより上に取り、地形へ潜らないようにする
            // （一人称でも視錐台外の背後・側方チャンクが落ちる構図であればカリング検証には十分）。
            const FPV_EYE_HEIGHT: f32 = 12.0;
            const FPV_PITCH_DEG: f32 = 8.0;
            let deg2rad = std::f32::consts::PI / 180.0;
            // オクルージョンカリング検証用の任意上書き（既定は従来値）。低い目線で起伏へ
            // 向かせると、手前の尾根が奥のチャンクを完全遮蔽する構図を作れる。
            //   SEED_SMOKE_FPV_EYE=<m>   … 目線高さ（既定 12m）
            //   SEED_SMOKE_FPV_YAW=<deg> … 向き（既定 0=+Z。ハイトマップの傾斜は X 方向なので
            //                              90 で +X の上り坂へ向く＝尾根越しの遮蔽が出る）
            let eye_h = std::env::var("SEED_SMOKE_FPV_EYE").ok()
                .and_then(|s| s.parse::<f32>().ok()).unwrap_or(FPV_EYE_HEIGHT);
            let yaw_fpv = std::env::var("SEED_SMOKE_FPV_YAW").ok()
                .and_then(|s| s.parse::<f32>().ok()).map(|d| d * deg2rad).unwrap_or(0.0);
            (
                [center[0], center[1] + eye_h, center[2]],
                yaw_fpv,
                FPV_PITCH_DEG * deg2rad,    // わずかに下向き（地面が見える）
                SMOKE_CAM_FOV_DEG,
                SMOKE_CAM_FAR,
            )
        } else {
            (eye, yaw, pitch, SMOKE_CAM_FOV_DEG, SMOKE_CAM_FAR)
        };
        let cam = crate::engine::core::app_base::scene::DebugCameraData {
            position: eye,
            yaw,
            pitch,
            fov_deg,
            far,
            speed: SMOKE_CAM_SPEED,
        };
        self.apply_camera_data(&cam);

        // ── 影を落とす方向光を 1 灯置く（RT 影／シャドウマップの実機確認用）──
        //   ライト不在シーンのフォールバック光は shadow_index=-1（影なし）で入るため、
        //   影の検証には実体の Light スロットが要る。掘った穴の内部が正しく明るいか／
        //   古い加速構造で黒く潰れないかは、この光が無いと画面に出ない。
        //   向きは「斜め上から見下ろす」= forward が下向き＋わずかに傾く姿勢にする。
        if let Some(scene) = self.scene.as_mut() {
            let light_entity = scene.world.spawn();
            scene.world.insert(light_entity, ActorTransform {
                position: [center[0], span * SMOKE_CAM_UP_RATIO, center[2]],
                rotation: [SMOKE_LIGHT_PITCH_DEG, SMOKE_LIGHT_YAW_DEG, 0.0],
                scale:    [1.0, 1.0, 1.0],
            });
            let mut light_actor = Actor::new(light_entity, SMOKE_LIGHT_ACTOR_NAME);
            let light_slot = scene.world.spawn();
            scene.world.insert(light_slot, crate::engine::components::LightComponent {
                kind:             crate::engine::components::LightKind::Directional,
                color:            SMOKE_LIGHT_COLOR,
                intensity:        SMOKE_LIGHT_INTENSITY,
                range:            SMOKE_LIGHT_RANGE,
                inner_angle_deg:  0.0,
                outer_angle_deg:  0.0,
                rect_width:       0.0,
                rect_height:      0.0,
                cast_shadows:     true,
                soft_radius:      SMOKE_LIGHT_SOFT_RADIUS_DEG,
                bounce_intensity: 0.0,
            });
            light_actor.add_slot_typed::<crate::engine::components::LightComponent>(
                SMOKE_LIGHT_SLOT_NAME, ComponentKind::Light, light_slot,
            );
            scene.actors.push(light_actor);
        }

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

        // 上の盛り/掘り/畝はすべて同じ暗黙ストローク中（handle_terrain_brush_world の最初の
        // 呼び出しで stroke_active になって以降、TERRAIN_STROKE_END 相当がまだ来ていない）。
        // ここで一旦確定させ、以降の undo/redo テストを独立したストロークとして行えるようにする。
        self.handle_terrain_stroke_end();

        // ── undo/redo 往復の実機確認 ──
        //   専用の 1 ストローク（ブラシを複数回適用 → ストローク確定）を作り、
        //   undo で密度がストローク前へ戻り、redo で再適用されることを検証する。
        //   footprint 内の未使用領域（盛り/掘り/畝と重ならない位置）を使う。
        let undo_test_center = [center[0], 0.0, center[2] + SMOKE_UNDO_TEST_OFFSET];
        let undo_test_coord = ChunkCoord::new(
            (undo_test_center[0] / extent).floor() as i32,
            0,
            (undo_test_center[2] / extent).floor() as i32,
        );
        for _ in 0..SMOKE_UNDO_TEST_BRUSH_COUNT {
            self.handle_terrain_brush_world(BrushOp::Add, undo_test_center, SMOKE_BRUSH_RADIUS, SMOKE_BRUSH_STRENGTH);
        }
        let density_after_stroke = self.terrain.chunks.get(&undo_test_coord).map(|c| c.raw_density().to_vec());
        self.handle_terrain_stroke_end();

        self.handle_terrain_undo();
        let density_after_undo = self.terrain.chunks.get(&undo_test_coord).map(|c| c.raw_density().to_vec());
        eprintln!(
            "[SEED terrain] smoke: undo reverted density = {} (undo_stack={}, redo_stack={})",
            density_after_undo != density_after_stroke && density_after_undo.is_some(),
            self.terrain.undo_stack.len(),
            self.terrain.redo_stack.len(),
        );

        self.handle_terrain_redo();
        let density_after_redo = self.terrain.chunks.get(&undo_test_coord).map(|c| c.raw_density().to_vec());
        eprintln!(
            "[SEED terrain] smoke: redo reapplied density = {} (undo_stack={}, redo_stack={})",
            density_after_redo == density_after_stroke,
            self.terrain.undo_stack.len(),
            self.terrain.redo_stack.len(),
        );

        // ── ハイトマップ読込の実機確認 ──
        //   temp_dir に小さな左右グラデーション PNG を書き出し、handle_terrain_heightmap を
        //   通して起伏が出ること・処理時間（ms）をログする。build_terrain_with 経由で地形全体が
        //   作り直されるため、以降 self.terrain の undo 履歴はクリアされる（想定どおり）。
        let heightmap_path = std::env::temp_dir().join("seed_terrain_smoke_heightmap.png");
        {
            use image::{ImageBuffer, Luma};
            let denom = (SMOKE_HEIGHTMAP_SIZE - 1).max(1);
            let img: ImageBuffer<Luma<u8>, Vec<u8>> = ImageBuffer::from_fn(
                SMOKE_HEIGHTMAP_SIZE,
                SMOKE_HEIGHTMAP_SIZE,
                |x, _y| Luma([(x * 255 / denom) as u8]),
            );
            if let Err(e) = img.save(&heightmap_path) {
                eprintln!("[SEED terrain] smoke: heightmap PNG write failed: {e}");
            }
        }
        let heightmap_start = std::time::Instant::now();
        // 構成は②で既定へ戻した状態を維持したいので config は None（現行設定を使う）。
        self.handle_terrain_heightmap(
            heightmap_path.to_string_lossy().to_string(),
            SMOKE_HEIGHTMAP_HEIGHT_SCALE,
            None,
        );
        eprintln!(
            "[SEED terrain] smoke: heightmap load done in {:?} (chunks={})",
            heightmap_start.elapsed(),
            self.terrain.chunks.len()
        );

        // ── 斜度ルール（自動下地）の実機確認: 切り立った山と谷を作る ──
        //   ハイトマップの起伏は緩やかで斜度が 20 度に届かず、全面が草地レイヤになる。
        //   ルールによる塗り分け（平地=草／中傾斜=土／急斜面=岩）を目視するために、
        //   強い盛り／掘りで確実に 38 度超の斜面を作る。
        let steep_up = [center[0] - SMOKE_STEEP_OFFSET, 0.0, center[2]];
        let steep_dn = [center[0] + SMOKE_STEEP_OFFSET, 0.0, center[2]];
        self.handle_terrain_brush_world(BrushOp::Add, steep_up, SMOKE_BRUSH_RADIUS, SMOKE_STEEP_STRENGTH);
        self.handle_terrain_brush_world(BrushOp::Subtract, steep_dn, SMOKE_BRUSH_RADIUS, SMOKE_STEEP_STRENGTH);
        self.handle_terrain_stroke_end();

        // ── レイヤペイント（手ペイント）の実機確認 ──
        //   自動下地（斜度／高度ルール）とは別に、明示的に砂レイヤを丸く塗る。
        //   スクリーンショットで「ルールによる草／岩の塗り分け」と「手ペイントの丸い砂」の
        //   両方が同時に見えることを狙う。
        //   ハイトマップ適用後の地表 Y はスクリーンレイ無しには求められないため、
        //   同じ XZ で Y を変えながら縦にペイント球を積み、確実に地表を貫かせる。
        for i in 0..SMOKE_PAINT_COLUMN_STEPS {
            let y = i as f32 * SMOKE_PAINT_COLUMN_STEP_Y;
            let paint_center = [center[0] + SMOKE_PAINT_OFFSET, y, center[2]];
            self.handle_terrain_paint_world(
                SMOKE_PAINT_LAYER, paint_center, SMOKE_PAINT_RADIUS, SMOKE_PAINT_STRENGTH,
            );
        }
        self.handle_terrain_stroke_end();
        {
            // 塗れたかを数値で確認する（ペイント量 > 0 のサンプル数）。
            let painted: usize = self
                .terrain
                .chunks
                .values()
                .map(|c| c.raw_paint_amount().iter().filter(|&&a| a > 0).count())
                .sum();
            eprintln!(
                "[SEED terrain] smoke: layer paint (layer={SMOKE_PAINT_LAYER}) painted_samples={painted}, layers={}",
                self.terrain.layers.active_count()
            );
        }

        // ── 散布プロップ（草）のルール自動散布（Terrain T3）──
        //   目視確認のための本命。TERRAIN_SCATTER_RULES が通るのと同じ経路
        //   （handle_terrain_scatter_rules）をそのまま呼ぶ。
        //   prop_id を空文字にして全プロップを対象にする（草地レイヤに grass_field、
        //   土レイヤに grass_dry が乗るので、レイヤの塗り分けと草の生え分けが
        //   一致しているかを 1 枚のスクリーンショットで検証できる）。
        //   `SEED_SMOKE_NO_SCATTER=1` のときは散布を丸ごと省く（地形メッシュ描画だけの
        //   純粋な負荷を計測するため——地形チャンク描画カリングの before/after 計測用）。
        if std::env::var_os("SEED_SMOKE_NO_SCATTER").is_none() {
            let scatter_start = std::time::Instant::now();
            self.handle_terrain_scatter_rules(
                SMOKE_SCATTER_ALL_PROPS.to_string(),
                SMOKE_SCATTER_SEED,
            );
            let total: usize = self.terrain.scatter.values().map(|v| v.len()).sum();
            eprintln!(
                "[SEED terrain] smoke: scattered {total} instances in {:?} (chunks={}, props={})",
                scatter_start.elapsed(),
                self.terrain.scatter.len(),
                self.terrain.props.active_count(),
            );
        } else {
            eprintln!("[SEED terrain] smoke: SEED_SMOKE_NO_SCATTER 設定により散布をスキップ（地形のみ計測）");
        }

        // ── プレビュー球の模擬 ──
        //   エディタ経由でしか出ないワイヤスフィアを、スモークでも直接セットして映す。
        //   footprint 中心の地表付近に置く（レイマーチのヒット点に相当）。
        //   strength を高め（⑥の色分岐が視認できる値）にセットする。
        self.terrain.brush_preview = Some(([center[0], 0.0, center[2]], SMOKE_PREVIEW_RADIUS, SMOKE_PREVIEW_STRENGTH));

        // ── カメラを再適用する ──
        //   ハイトマップと急斜面デモで地形が上へ伸びたため、初期化時の画角のままでは
        //   手前の斜面が画面を覆って塗り分けが見えない。同じ計算式で組み直して構図を戻す。
        self.apply_camera_data(&cam);

        eprintln!(
            "[SEED terrain] smoke: init + deform + stroke({}) + undo/redo + heightmap + layers + preview done (chunks={})",
            SMOKE_STROKE_STEPS,
            self.terrain.chunks.len()
        );

        // ここまではすべて 1 フレーム目より前の処理。描画開始後の編集を検証するため、
        // 遅延掘削ステップを有効化する（tick_terrain_smoke_deferred が毎フレーム見る）。
        SMOKE_DEFERRED_ENABLED.store(true, std::sync::atomic::Ordering::Relaxed);
    }

    /// スモークの「クローズアップカメラへ切り替える」ステップ（毎フレーム先頭の自己ゲート付きフック）。
    ///
    /// 環境変数 `SEED_SMOKE_CLOSEUP_FRAME` が未指定なら何もしない。指定されていれば
    /// そのフレームで 1 度だけ、実際に散布された草インスタンスを被写体にして
    /// デバッグカメラを近接姿勢へ移す。全景では 1 画素未満になる草の葉を画面上で
    /// 解像させ、風アニメーションの動きをスクリーンショットで比較できるようにするため。
    ///
    /// 被写体は「対象プロップのインスタンスが最も多いチャンクの重心付近の 1 本」を選ぶ。
    /// ハードコードした座標ではなく散布データから引くので、地形やルールを変えても追従する。
    pub(super) fn tick_terrain_smoke_closeup(&mut self) {
        use std::sync::atomic::Ordering;
        let Some(target_frame) = *SMOKE_CLOSEUP_FRAME else { return };
        if SMOKE_CLOSEUP_DONE.load(Ordering::Relaxed) {
            return;
        }
        let n = SMOKE_CLOSEUP_COUNTER.fetch_add(1, Ordering::Relaxed);
        if n != target_frame {
            return;
        }
        SMOKE_CLOSEUP_DONE.store(true, Ordering::Relaxed);

        // 被写体プロップ添字（env 上書き可）。木の高ポリ散布モデルを近接 LOD0 で写す計測に使う。
        let closeup_prop = *SMOKE_CLOSEUP_PROP;

        // 対象プロップのインスタンスが最も多いチャンクを選ぶ（草が濃い場所＝絵になる場所）。
        let Some((_, instances)) = self
            .terrain
            .scatter
            .iter()
            .map(|(coord, list)| {
                let count = list.iter().filter(|i| i.prop_id == closeup_prop).count();
                (coord, list, count)
            })
            // タイブレークをチャンク座標で決定論化する。scatter は HashMap で、イテレーション順が
            // 実行ごとに変わるため、同数チャンクが複数あると max_by_key が選ぶチャンクが run 依存に
            // なる（＝計測用クローズアップの構図が run ごとにブレて before/after 比較が壊れる）。
            // 座標を副キーに入れて常に同じチャンクを選ぶ。
            .max_by_key(|(coord, _, count)| (*count, coord.x, coord.y, coord.z))
            .map(|(coord, list, _)| (coord, list))
        else {
            eprintln!("[SEED terrain] smoke: closeup 対象の散布データがありません");
            return;
        };
        // そのチャンク内の対象プロップ重心に最も近い 1 本を被写体にする
        // （重心そのものは草が無い窪みに落ちることがあるため、実在インスタンスへ吸着させる）。
        let picked: Vec<[f32; 3]> = instances
            .iter()
            .filter(|i| i.prop_id == closeup_prop)
            .map(|i| i.pos)
            .collect();
        if picked.is_empty() {
            eprintln!("[SEED terrain] smoke: closeup 対象プロップのインスタンスがありません");
            return;
        }
        let inv = 1.0 / picked.len() as f32;
        let centroid = picked.iter().fold([0.0f32; 3], |acc, p| {
            [acc[0] + p[0] * inv, acc[1] + p[1] * inv, acc[2] + p[2] * inv]
        });
        let subject = *picked
            .iter()
            .min_by(|a, b| {
                let d = |p: &[f32; 3]| {
                    (p[0] - centroid[0]).powi(2)
                        + (p[1] - centroid[1]).powi(2)
                        + (p[2] - centroid[2]).powi(2)
                };
                d(a).total_cmp(&d(b))
            })
            .expect("空でないことは上で確認済み");

        // 注視点は被写体の少し上（草の中ほど）。カメラは -Z 側からほぼ水平に見る。
        let target = [subject[0], subject[1] + SMOKE_CLOSEUP_TARGET_LIFT, subject[2]];
        let eye = [
            target[0],
            subject[1] + SMOKE_CLOSEUP_EYE_HEIGHT,
            target[2] - *SMOKE_CLOSEUP_DIST,
        ];
        // yaw/pitch は debug_camera の規約（forward → yaw = atan2(fwd.x, fwd.z), pitch = asin(-fwd.y)）。
        let dir = [target[0] - eye[0], target[1] - eye[1], target[2] - eye[2]];
        let len = (dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]).sqrt().max(f32::EPSILON);
        let fwd = [dir[0] / len, dir[1] / len, dir[2] / len];
        let cam = crate::engine::core::app_base::scene::DebugCameraData {
            position: eye,
            yaw:      fwd[0].atan2(fwd[2]),
            pitch:    (-fwd[1]).clamp(-1.0, 1.0).asin(),
            fov_deg:  SMOKE_CLOSEUP_FOV_DEG,
            far:      SMOKE_CLOSEUP_FAR,
            speed:    SMOKE_CLOSEUP_SPEED,
        };
        self.apply_camera_data(&cam);
        // ブラシのプレビュー球は近接では画面を覆ってしまうので消す。
        self.terrain.brush_preview = None;
        eprintln!(
            "[SEED terrain] smoke: closeup camera at frame {n} eye={eye:?} target={target:?} \
             (subject instances in chunk={})",
            picked.len()
        );
    }

    /// スモークの「描画開始後に掘る」ステップ（毎フレーム先頭から呼ばれる自己ゲート付きフック）。
    ///
    /// スモークが無効なら即 return する。有効なときはフレームを数え、
    /// `SMOKE_DEFERRED_DIG_FRAME` 到達フレームで 1 度だけフットプリント中心を掘る。
    /// エディタでドラッグして掘る操作と同じ経路（handle_terrain_brush_world → remesh_chunks）を通り、
    /// 「既に BLAS／統合バッチが構築済みの状態でメッシュが差し替わる」状況を再現する。
    pub(super) fn tick_terrain_smoke_deferred(&mut self) {
        use std::sync::atomic::Ordering;
        if !SMOKE_DEFERRED_ENABLED.load(Ordering::Relaxed) {
            return;
        }
        let n = SMOKE_DEFERRED_FRAME_COUNTER.fetch_add(1, Ordering::Relaxed);
        // 掘削フレームでもペイントフレームでもなければ何もしない。
        let is_dig = n == SMOKE_DEFERRED_DIG_FRAME;
        let is_paint = SMOKE_DEFERRED_PAINT_FRAMES.contains(&n);
        if !is_dig && !is_paint {
            return;
        }
        // フットプリント中心（run_terrain_smoke のカメラ注視点と同じ計算式）。
        let settings = self.terrain.settings.clone();
        let extent = settings.chunk_extent();
        let center = [
            settings.ground_chunks_x as f32 * extent * 0.5,
            0.0,
            settings.ground_chunks_z as f32 * extent * 0.5,
        ];

        // ── 遅延ペイント: 密度を触らずレイヤだけを塗る（高速パスの実機検証）──
        //   同じ場所を複数フレームに分けて塗ることで、
        //   1 回目 = パレット確定（フォールバックし得る）／2 回目以降 = 高速パス
        //   という遷移が `[PERF terrain] paint` のログで観測できる。
        if is_paint {
            for i in 0..SMOKE_DEFERRED_PAINT_COLUMN_STEPS {
                let y = i as f32 * SMOKE_DEFERRED_PAINT_COLUMN_STEP_Y;
                self.handle_terrain_paint_world(
                    SMOKE_DEFERRED_PAINT_LAYER,
                    [center[0] + SMOKE_DEFERRED_PAINT_OFFSET, y, center[2]],
                    SMOKE_DEFERRED_PAINT_RADIUS,
                    SMOKE_DEFERRED_PAINT_STRENGTH,
                );
            }
            self.handle_terrain_stroke_end();
            eprintln!("[SEED terrain] smoke: deferred paint at frame {n}");
            // 最終ペイントフレームまで来たらフックを畳む。
            if Some(&n) == SMOKE_DEFERRED_PAINT_FRAMES.last() {
                SMOKE_DEFERRED_ENABLED.store(false, Ordering::Relaxed);
            }
            return;
        }
        // ハイトマップ適用後の地表 Y はレイ無しには求まらないため、同じ XZ で Y を変えながら
        // 縦に掘削球を積み、確実に地表を貫いて穴の内部（側面・底）を露出させる。
        for i in 0..SMOKE_DEFERRED_DIG_COLUMN_STEPS {
            let y = i as f32 * SMOKE_DEFERRED_DIG_COLUMN_STEP_Y;
            self.handle_terrain_brush_world(
                BrushOp::Subtract,
                [center[0], y, center[2]],
                SMOKE_DEFERRED_DIG_RADIUS,
                SMOKE_DEFERRED_DIG_STRENGTH,
            );
        }
        self.handle_terrain_stroke_end();
        // 掘った位置が見えるよう、プレビュー球は消しておく（穴の内部を覆わないため）。
        self.terrain.brush_preview = None;
        eprintln!(
            "[SEED terrain] smoke: deferred dig at frame {n} center={center:?} r={SMOKE_DEFERRED_DIG_RADIUS}"
        );
        // ここでは畳まない。後続の遅延ペイントフレームまでフックを生かしておく。
    }

    // ─── 物理コリジョンのスモーク（SEED_TERRAIN_PHYS_SMOKE=1）─────────────────

    /// 地形の物理コリジョンを実機で検証するための常設デバッグフック（自己ゲート）。
    ///
    /// 環境変数 `SEED_TERRAIN_PHYS_SMOKE=1`＋起動引数 `--mode=play` のときだけ、
    /// フラット地形＋中央に小山を作り、その上空へ落下する Dynamic 球コライダーを数個置く。
    /// Play モードなのでフレーム末尾で物理が自動起動し、`register_all_terrain_colliders` が
    /// 地形の静的トライメッシュコライダーを登録する。各球が地形表面で静止する（すり抜けない）
    /// ことを `tick_terrain_physics_smoke` が毎フレーム Y 座標のログで検証する。
    ///
    /// 球には描画メッシュを付けないため画面には出ない（可視化には別途プリミティブ生成が要る）。
    /// 検証は「球の中心 Y が地表付近で下げ止まる＝コリジョン成立」を数値で行う。
    pub(super) fn run_terrain_physics_smoke(&mut self) {
        // ── フラット地形を既定構成で作る ──
        self.handle_terrain_init(None);

        // フットプリント中心（run_terrain_smoke と同じ計算式）。
        let settings = self.terrain.settings.clone();
        let extent = settings.chunk_extent();
        let footprint_w = settings.ground_chunks_x as f32 * extent;
        let footprint_d = settings.ground_chunks_z as f32 * extent;
        let span = footprint_w.max(footprint_d);
        let center = [footprint_w * 0.5, 0.0, footprint_d * 0.5];

        // ── 中央に小山を盛る（球が転がる斜面を作る）──
        self.handle_terrain_brush_world(
            BrushOp::Add, center, PHYS_SMOKE_MOUND_RADIUS, PHYS_SMOKE_MOUND_STRENGTH,
        );
        self.handle_terrain_stroke_end();
        // ブラシ由来の pending_remesh をここで確実に消化して地形メッシュを確定させる
        // （物理登録時に最新形状で登録されるように）。
        self.flush_terrain_pending_remesh();

        // ── デバッグカメラを俯瞰位置へ ──
        let eye = [
            center[0],
            center[1] + span * SMOKE_CAM_UP_RATIO,
            center[2] - span * SMOKE_CAM_BACK_RATIO,
        ];
        let dir = [center[0] - eye[0], center[1] - eye[1], center[2] - eye[2]];
        let len = (dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]).sqrt().max(f32::EPSILON);
        let fwd = [dir[0] / len, dir[1] / len, dir[2] / len];
        let cam = crate::engine::core::app_base::scene::DebugCameraData {
            position: eye,
            yaw: fwd[0].atan2(fwd[2]),
            pitch: (-fwd[1]).clamp(-1.0, 1.0).asin(),
            fov_deg: SMOKE_CAM_FOV_DEG,
            far: SMOKE_CAM_FAR,
            speed: SMOKE_CAM_SPEED,
        };
        self.apply_camera_data(&cam);

        // ── 落下する Dynamic 球コライダーを数個スポーンする ──
        //   XZ を少しずつ散らし、上空 PHYS_SMOKE_DROP_Y から落とす。中央の球は小山へ、
        //   周囲の球は平地へ落ちるので「乗る」「転がって止まる」の両方を観察できる。
        let mut spawned: Vec<crate::engine::ecs::Entity> = Vec::new();
        if let Some(scene) = self.scene.as_mut() {
            for i in 0..PHYS_SMOKE_BALL_COUNT {
                let angle = i as f32 * PHYS_SMOKE_BALL_ANGLE_STEP;
                let x = center[0] + angle.cos() * PHYS_SMOKE_BALL_SPREAD;
                let z = center[2] + angle.sin() * PHYS_SMOKE_BALL_SPREAD;
                let y = PHYS_SMOKE_DROP_Y + i as f32 * PHYS_SMOKE_DROP_Y_STEP;

                let ball_entity = scene.world.spawn();
                scene.world.insert(ball_entity, ActorTransform {
                    position: [x, y, z],
                    rotation: [0.0, 0.0, 0.0],
                    scale: [1.0, 1.0, 1.0],
                });
                let mut ball_actor = Actor::new(ball_entity, PHYS_SMOKE_BALL_ACTOR_NAME);

                let col_slot = scene.world.spawn();
                scene.world.insert(col_slot, ColliderComponent {
                    shape: ColliderShapeData::Sphere { radius: PHYS_SMOKE_BALL_RADIUS },
                    use_rigidbody: true,
                    is_kinematic: false,
                    mass: PHYS_SMOKE_BALL_MASS,
                    restitution: PHYS_SMOKE_BALL_RESTITUTION,
                    friction: PHYS_SMOKE_BALL_FRICTION,
                    ..ColliderComponent::default()
                });
                ball_actor.add_slot_typed::<ColliderComponent>(
                    PHYS_SMOKE_BALL_SLOT_NAME, ComponentKind::Collider, col_slot,
                );
                scene.actors.push(ball_actor);
                spawned.push(ball_entity);
            }
        }

        // スポーンした球の Entity を tick 側へ引き継ぐ（毎フレームの Y 監視に使う）。
        *PHYS_SMOKE_BALLS.lock().unwrap() = spawned;
        PHYS_SMOKE_ENABLED.store(true, std::sync::atomic::Ordering::Relaxed);
        eprintln!(
            "[SEED phys-smoke] setup done: {} balls dropped over terrain center={center:?} \
             (surface≈y=0, mound r={PHYS_SMOKE_MOUND_RADIUS})",
            PHYS_SMOKE_BALL_COUNT
        );
    }

    /// 物理スモークの毎フレームフック（自己ゲート）。スポーンした球の Y 座標を追跡し、
    /// 規定フレームで「地表付近で下げ止まった＝地形コライダーに乗った」ことを判定・ログする。
    ///
    /// `frame_renderer` のフレーム先頭から毎フレーム呼ぶ（`tick_terrain_smoke_deferred` と同様）。
    pub(super) fn tick_terrain_physics_smoke(&mut self) {
        use std::sync::atomic::Ordering;
        if !PHYS_SMOKE_ENABLED.load(Ordering::Relaxed) {
            return;
        }
        let n = PHYS_SMOKE_FRAME_COUNTER.fetch_add(1, Ordering::Relaxed);
        // 規定の観測フレーム以外は何もしない。
        if !PHYS_SMOKE_SAMPLE_FRAMES.contains(&n) {
            return;
        }
        let balls = PHYS_SMOKE_BALLS.lock().unwrap().clone();
        let Some(scene) = self.scene.as_ref() else { return };

        // 各球の現在 Y を収集する。
        let ys: Vec<f32> = balls.iter().map(|&e| {
            scene.world.get::<ActorTransform>(e).map(|t| t.position[1]).unwrap_or(f32::NAN)
        }).collect();

        let min_y = ys.iter().cloned().filter(|y| y.is_finite()).fold(f32::INFINITY, f32::min);
        let max_y = ys.iter().cloned().filter(|y| y.is_finite()).fold(f32::NEG_INFINITY, f32::max);
        eprintln!(
            "[SEED phys-smoke] frame {n}: ball Y = {ys:?} (min={min_y:.3}, max={max_y:.3})"
        );

        // 最終観測フレームで合否判定する。
        if Some(&n) == PHYS_SMOKE_SAMPLE_FRAMES.last() {
            // すべての球が「地表付近で静止」＝ Y が下限より上（すり抜けていない）かつ
            // 落下開始位置よりは十分下（実際に落ちて着地した）。
            let all_rested = ys.iter().all(|&y| {
                y.is_finite() && y > PHYS_SMOKE_REST_Y_MIN && y < PHYS_SMOKE_REST_Y_MAX
            });
            eprintln!(
                "[SEED phys-smoke] RESULT: all_balls_rested_on_terrain = {all_rested} \
                 (期待範囲 {PHYS_SMOKE_REST_Y_MIN}..{PHYS_SMOKE_REST_Y_MAX}; \
                 Y<0 はすり抜け＝コリジョン不成立の徴候)"
            );
            PHYS_SMOKE_ENABLED.store(false, Ordering::Relaxed);
        }
    }
}

// ─── 物理スモークの状態・定数 ────────────────────────────────────────────────

/// 物理スモークが有効か（`run_terrain_physics_smoke` が立てる）。
static PHYS_SMOKE_ENABLED: std::sync::atomic::AtomicBool =
    std::sync::atomic::AtomicBool::new(false);
/// 物理スモークの経過フレーム数（描画開始後にカウント）。
static PHYS_SMOKE_FRAME_COUNTER: std::sync::atomic::AtomicU32 =
    std::sync::atomic::AtomicU32::new(0);
/// スポーンした球アクターの World Entity（毎フレームの Y 監視に使う）。
static PHYS_SMOKE_BALLS: std::sync::LazyLock<std::sync::Mutex<Vec<crate::engine::ecs::Entity>>> =
    std::sync::LazyLock::new(|| std::sync::Mutex::new(Vec::new()));

/// Y を観測・ログするフレーム番号（落下→静定を追える間隔）。末尾フレームで合否判定する。
const PHYS_SMOKE_SAMPLE_FRAMES: &[u32] = &[15, 45, 90, 150, 240];

/// 落下させる球の数。
const PHYS_SMOKE_BALL_COUNT: u32 = 5;
/// 球の半径（メートル）。静止時の中心 Y の目安になる。
const PHYS_SMOKE_BALL_RADIUS: f32 = 0.5;
/// 球の質量（kg）。
const PHYS_SMOKE_BALL_MASS: f32 = 1.0;
/// 球の反発係数（小さめ＝あまり跳ねずに静定しやすい）。
const PHYS_SMOKE_BALL_RESTITUTION: f32 = 0.2;
/// 球の摩擦係数（斜面で転がりつつ止まる程度）。
const PHYS_SMOKE_BALL_FRICTION: f32 = 0.6;
/// 球を中心から散らす半径（メートル）。中央の小山と周囲の平地に振り分ける。
const PHYS_SMOKE_BALL_SPREAD: f32 = 2.0;
/// 球を円周状に並べる角度ステップ（ラジアン）。
const PHYS_SMOKE_BALL_ANGLE_STEP: f32 = 1.2566; // ≈ 2π/5
/// 最初の球を落とす高さ（メートル、地表 y=0 の上空）。
const PHYS_SMOKE_DROP_Y: f32 = 4.0;
/// 球ごとに落下開始高さをずらす量（同時着地の重なりを避ける）。
const PHYS_SMOKE_DROP_Y_STEP: f32 = 0.5;
/// 静止判定の Y 下限（これ未満＝地形をすり抜けて落下＝失敗）。
const PHYS_SMOKE_REST_Y_MIN: f32 = -0.5;
/// 静止判定の Y 上限（これを超える＝まだ空中＝未着地）。小山の高さ＋半径を見込む。
const PHYS_SMOKE_REST_Y_MAX: f32 = 3.0;
/// 小山ブラシの半径・強度。
const PHYS_SMOKE_MOUND_RADIUS: f32 = 3.0;
const PHYS_SMOKE_MOUND_STRENGTH: f32 = 1.0;
/// 球アクター・スロットの名前。
const PHYS_SMOKE_BALL_ACTOR_NAME: &str = "phys_smoke_ball";
const PHYS_SMOKE_BALL_SLOT_NAME: &str = "collider";

// ============================================================
//  ユニットテスト（App・GPU 非依存の純粋ヘルパーのみ）
//
//  チャンク追加の中核である「追加対象の列挙（＝既存の温存）」と
//  「境界サンプルの引き写し（＝継ぎ目の連続性）」は App も wgpu も要らない
//  純粋関数へ切り出してあるため、ここで直接検証できる。
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    // ─── LOD 再メッシュの件数予算（純粋ロジック）のテスト ──────────────────────

    /// 何も処理していないフレームでは必ず 1 バッチぶん切り出せること（前進保証）。
    ///
    /// ここが 0 を返すと、重いチャンクが 1 つあるだけで LOD が永久に収束しない。
    #[test]
    fn lod_batch_always_advances_from_zero() {
        assert_eq!(lod_batch_size(0), TERRAIN_LOD_BATCH);
        assert!(lod_batch_size(0) > 0, "先頭バッチは必ず正であること");
    }

    /// 件数上限に達したら 0 を返し、残りは次フレームへ繰り越されること。
    #[test]
    fn lod_batch_stops_at_frame_cap() {
        // 上限ちょうど / 超過のどちらでも 0（飽和減算で負にならない）。
        assert_eq!(lod_batch_size(TERRAIN_LOD_TRANSITIONS_PER_FRAME), 0);
        assert_eq!(lod_batch_size(TERRAIN_LOD_TRANSITIONS_PER_FRAME + 100), 0);
    }

    /// 上限直前では「上限を跨がないぶんだけ」に切り詰められること。
    #[test]
    fn lod_batch_clips_to_remaining_cap() {
        let remaining = 1usize;
        let processed = TERRAIN_LOD_TRANSITIONS_PER_FRAME - remaining;
        assert_eq!(
            lod_batch_size(processed),
            remaining.min(TERRAIN_LOD_BATCH),
            "上限を跨いで処理しないこと"
        );
    }

    /// 予算を回し切ると、処理数の合計はちょうど件数上限で頭打ちになること
    /// （＝1 フレームで際限なく再メッシュしない／余りは次フレームへ残る）。
    #[test]
    fn lod_batch_sum_saturates_at_cap() {
        let mut processed = 0usize;
        // 候補が無限にあると仮定して、切り出せなくなるまで回す。
        loop {
            let take = lod_batch_size(processed);
            if take == 0 {
                break;
            }
            processed += take;
        }
        assert_eq!(processed, TERRAIN_LOD_TRANSITIONS_PER_FRAME);
    }

    // ─── 再メッシュ経路ごとの付随処理指定（挙動の契約）のテスト ────────────────

    /// LOD 遷移経路は**コライダーを作り直さない**こと。
    ///
    /// 表示 LOD が変わっただけのチャンクは密度場が 1 ビットも変わっていない。
    /// コライダーは常にフル解像度（LOD0）なので、作り直しても結果は同一であり、
    /// QBVH の同期構築ぶんだけフレーム時間を捨てることになる。
    #[test]
    fn lod_transition_never_syncs_colliders() {
        let o = RemeshOptions::lod_transition();
        assert!(!o.sync_colliders, "LOD 遷移でコライダーを作り直さないこと");
        assert!(o.defer_gpu_release, "旧 GPU リソースは退役キューへ回すこと");
        assert!(!o.defer_side_effects, "RT BLAS prune は従来どおり即時であること");
    }

    /// 即時経路（ブラシ確定・undo/redo・チャンク追加）は従来どおり全部やること。
    #[test]
    fn immediate_path_does_everything() {
        let o = RemeshOptions::immediate();
        assert!(o.sync_colliders);
        assert!(!o.defer_gpu_release);
        assert!(!o.defer_side_effects);
    }

    /// ストローク中はコライダー追従も付随処理も確定時へ回すこと（従来挙動の維持）。
    #[test]
    fn stroke_defers_colliders_with_side_effects() {
        let deferred = RemeshOptions::immediate().with_deferred_side_effects(true);
        assert!(deferred.defer_side_effects);
        assert!(!deferred.sync_colliders, "遅延中はコライダーも触らない（従来と同じ）");

        let immediate = RemeshOptions::immediate().with_deferred_side_effects(false);
        assert!(!immediate.defer_side_effects);
        assert!(immediate.sync_colliders);
    }

    // ─── ストローク遅延付随処理の確定判定（純粋ロジック）のテスト ──────────────

    /// 遅延チャンクが空なら、どんな状態でも確定は起きない。
    #[test]
    fn finalize_never_when_deferred_empty() {
        // (stroke_active, idle_elapsed) の全組み合わせで false であること。
        for stroke_active in [false, true] {
            for idle_elapsed in [false, true] {
                assert!(
                    !should_finalize_stroke(true, stroke_active, idle_elapsed),
                    "empty 集合では確定しないはず (active={stroke_active}, idle={idle_elapsed})"
                );
            }
        }
    }

    // ─── RT BLAS 再構築待ちの消化バッチ選択（純粋ロジック）のテスト ────────────

    /// 予算より多く溜まっていても、1 回で取り出すのは予算ぶんだけ。
    /// かつ座標順（x, y, z）で決定的に並ぶ（HashSet の走査順に依存しない）。
    #[test]
    fn rt_prune_batch_is_budgeted_and_deterministic() {
        // 座標順が挿入順と一致しないように、わざと逆順・飛び番で入れる。
        let pending: HashSet<ChunkCoord> = [
            ChunkCoord::new(2, 0, 0),
            ChunkCoord::new(0, 0, 1),
            ChunkCoord::new(0, 1, 0),
            ChunkCoord::new(0, 0, 0),
            ChunkCoord::new(1, 0, 0),
        ]
        .into_iter()
        .collect();

        let batch = select_rt_prune_batch(&pending, 3);
        assert_eq!(batch.len(), 3, "予算 3 で頭打ちになるはず");
        assert_eq!(
            batch,
            vec![
                ChunkCoord::new(0, 0, 0),
                ChunkCoord::new(0, 0, 1),
                ChunkCoord::new(0, 1, 0),
            ],
            "(x, y, z) の昇順で選ばれるはず"
        );

        // 何度呼んでも同じ結果（HashSet の走査順が変わっても揺れない）。
        for _ in 0..8 {
            assert_eq!(select_rt_prune_batch(&pending, 3), batch);
        }
    }

    /// 予算が集合サイズ以上なら全部取り出す。予算 0 なら 1 つも取り出さない。
    #[test]
    fn rt_prune_batch_edge_budgets() {
        let pending: HashSet<ChunkCoord> =
            [ChunkCoord::new(0, 0, 0), ChunkCoord::new(1, 0, 0)].into_iter().collect();

        assert_eq!(select_rt_prune_batch(&pending, 0).len(), 0, "予算 0 では何も選ばない");
        assert_eq!(select_rt_prune_batch(&pending, 2).len(), 2);
        assert_eq!(select_rt_prune_batch(&pending, 99).len(), 2, "予算超過でも集合サイズ止まり");
        assert_eq!(select_rt_prune_batch(&HashSet::new(), 8).len(), 0, "空集合は空バッチ");
    }

    /// 消化予算は BLAS の 1 フレーム再構築上限と一致していること。
    ///
    /// 予算のほうが大きいと、再構築が追いつかないぶんだけ「BLAS が捨てられて
    /// TLAS から抜けている（＝影が落ちない）」チャンクが増え続ける。
    /// 根拠を 2 か所に書かないための単一情報源の担保である。
    #[test]
    fn rt_prune_budget_matches_blas_build_limit() {
        assert_eq!(
            crate::engine::core::renderer::rt_shadow::MAX_BLAS_BUILDS_PER_FRAME,
            8,
            "BLAS 再構築上限が変わったら、地形側の prune 予算の妥当性を再検討すること"
        );
    }

    // ─── 契約テスト: RT BLAS 追従の呼び出し位置 ────────────────────────────────

    /// **回帰テスト（黒落ちが「たまにしか直らない」不具合の固定）**
    ///
    /// 旧実装は消化処理を `apply_pending_cover` の内側から呼んでいた。あのメソッドは
    /// 「焼き直し待ちが空なら先頭で return」するため、マウスを離した時点で待ちが空だと
    /// 消化が二度と発火せず、BLAS が古い形のまま残って地面が黒いままになっていた
    /// （待ちが残っていたときだけ偶然直る＝「たまに解消する」の正体）。
    ///
    /// よって消化 `flush_rt_blas_prune` は
    ///   ・毎フレーム無条件に走る `frame_renderer.rs` から呼ばれること
    ///   ・条件付きで早期 return するカバー系（`terrain_cover_ops.rs`）から呼ばれないこと
    /// を契約として固定する。改行コードに依存しないよう `lines()` で走査する。
    #[test]
    fn rt_blas_prune_is_flushed_from_frame_loop_only() {
        const FLUSH_FN: &str = "flush_rt_blas_prune";
        let frame_src = include_str!("frame_renderer.rs");
        let cover_src = include_str!("terrain_cover_ops.rs");

        // 呼び出し行だけを数える（定義・doc コメントを拾わないよう `self.` 付きで見る）。
        let calls_in = |src: &str| -> usize {
            src.lines().filter(|l| l.contains(&format!("self.{FLUSH_FN}("))).count()
        };

        assert_eq!(
            calls_in(frame_src),
            1,
            "frame_renderer.rs から毎フレームちょうど 1 回呼ばれること"
        );
        assert_eq!(
            calls_in(cover_src),
            0,
            "早期 return するカバー処理の内側から呼んではいけない（旧実装の不具合）"
        );
    }

    /// マウスアップ済み（stroke_active=false）かつ溜まっていれば、無操作でなくても確定する。
    #[test]
    fn finalize_on_mouse_up() {
        assert!(should_finalize_stroke(false, false, false));
        assert!(should_finalize_stroke(false, false, true));
    }

    /// ストローク継続中（stroke_active=true）は、無操作タイムアウトが来て初めて確定する。
    /// ＝ドラッグ中は付随処理を遅延し、手が止まったら追従する、というブラシ経路の意図。
    #[test]
    fn defer_while_active_until_idle() {
        // 継続中かつ操作継続中 → 遅延（確定しない）。
        assert!(!should_finalize_stroke(false, true, false));
        // 継続中でも無操作が続いたら確定する。
        assert!(should_finalize_stroke(false, true, true));
    }

    /// 遅延集合が「蓄積 → 確定でクリア」されるロジック（HashSet の畳み込みと take）。
    /// ブラシ経路のフレーム跨ぎで同じチャンクが何度積まれても 1 回に畳まれ、確定（take）で
    /// 空になることを、`flush`/`finalize` が使うのと同じ集合操作で検証する。
    #[test]
    fn deferred_set_accumulates_and_clears() {
        let mut deferred: HashSet<ChunkCoord> = HashSet::new();
        let a = ChunkCoord::new(0, 0, 0);
        let b = ChunkCoord::new(1, 0, 0);
        // フレーム1: a,b を積む。
        deferred.extend([a, b]);
        // フレーム2: a を再度積む（同一ストロークで同じチャンクを触り続ける典型）。
        deferred.extend([a]);
        assert_eq!(deferred.len(), 2, "重複は 1 回に畳まれるはず");
        // 確定: take で取り出すと集合は空になる（finalize_stroke_deferred と同じ操作）。
        let taken: HashSet<ChunkCoord> = std::mem::take(&mut deferred);
        assert_eq!(taken.len(), 2);
        assert!(deferred.is_empty(), "確定後は遅延集合が空になるはず");
    }

    /// テスト用の小さめ設定（33³ ではなく 9³ サンプルにして実行時間を抑える）。
    /// Y 範囲は 1 段だけにして、列挙件数の期待値を素直に数えられるようにする。
    fn test_settings() -> TerrainSettings {
        let mut s = TerrainSettings::default();
        s.apply_chunk_config(2, 2, TEST_CHUNK_CELLS, TEST_VOXEL_SIZE);
        s.ground_chunk_y_min = 0;
        s.ground_chunk_y_max = 0;
        s
    }

    /// テスト用のチャンク分割数（小さいほどテストが速い）。
    const TEST_CHUNK_CELLS: u32 = 8;
    /// テスト用のボクセルサイズ（メートル）。
    const TEST_VOXEL_SIZE: f32 = 0.5;
    /// 既存チャンクの境界面へ書き込む「編集済み」を表す目印の密度値。
    /// 平坦地面（density = world_y）では絶対に現れない値を選ぶ。
    const MARKER_DENSITY: f32 = -12.5;
    /// 既存チャンクの境界面へ書き込む目印のペイント量。
    const MARKER_PAINT_AMOUNT: f32 = 1.0;
    /// 目印のペイントレイヤ番号。
    const MARKER_PAINT_LAYER: u32 = 3;

    /// 既存チャンク 1 枚だけを持つ地形を作る（座標 (0,0,0)、平坦地面）。
    fn ground_map(settings: &TerrainSettings) -> HashMap<ChunkCoord, TerrainChunkData> {
        let mut chunks = HashMap::new();
        chunks.insert(
            ChunkCoord::new(0, 0, 0),
            TerrainChunkData::from_ground_plane(settings, ChunkCoord::new(0, 0, 0)),
        );
        chunks
    }

    // ─── 物理コライダー生成のテスト ─────────────────────────────────────────

    /// 全サンプルが AIR（density > iso=0）のチャンクは三角形 0 でコライダーを作らない。
    #[test]
    fn collider_shape_all_air_is_none() {
        let settings = test_settings();
        let coord = ChunkCoord::new(0, 0, 0);
        let mut chunks = HashMap::new();
        // +1 で埋める＝全 AIR。等値面が生じないので None が返るべき。
        chunks.insert(coord, TerrainChunkData::new_filled(&settings, 1.0));
        assert!(
            build_chunk_collider_shape(&chunks, &settings, coord).is_none(),
            "空メッシュのチャンクはコライダーを持たない"
        );
    }

    /// 内部に等値面を持つチャンクは、有効な共有頂点＋インデックスのトライメッシュを返す。
    /// 全インデックスが頂点範囲内であること（Rapier trimesh の前提）も検証する。
    #[test]
    fn collider_shape_surface_chunk_is_valid_indexed_mesh() {
        let settings = test_settings();
        let coord = ChunkCoord::new(0, 0, 0);
        // 全 AIR から下半分を SOLID（負）にして、内部に必ず等値面を作る。
        let mut chunk = TerrainChunkData::new_filled(&settings, 1.0);
        let s = chunk.samples_per_axis();
        for iz in 0..s {
            for iy in 0..(s / 2) {
                for ix in 0..s {
                    chunk.set_sample(ix, iy, iz, -1.0);
                }
            }
        }
        let mut chunks = HashMap::new();
        chunks.insert(coord, chunk);

        let shape = build_chunk_collider_shape(&chunks, &settings, coord)
            .expect("等値面のあるチャンクはコライダーを返す");
        let ColliderShape::TriangleMeshIndexed { vertices, indices } = shape else {
            panic!("地形コライダーは TriangleMeshIndexed でなければならない");
        };
        assert!(!vertices.is_empty(), "頂点が存在する");
        assert!(!indices.is_empty(), "三角形が存在する");
        let n = vertices.len() as u32;
        for tri in &indices {
            for &i in tri {
                assert!(i < n, "インデックス {i} が頂点数 {n} を超えている");
            }
        }
    }

    /// コライダーの頂点はチャンクローカル座標（原点 0 付近）であり、ワールド配置は
    /// PhysicsObject.position（＝チャンク原点）で行われること。ローカル頂点は
    /// チャンクの寸法（cells*voxel = chunk_extent）を大きく超えない。
    #[test]
    fn collider_shape_vertices_are_chunk_local() {
        let settings = test_settings();
        let coord = ChunkCoord::new(3, 0, -2); // 原点から離れたチャンク
        let mut chunk = TerrainChunkData::new_filled(&settings, 1.0);
        let s = chunk.samples_per_axis();
        for iz in 0..s {
            for iy in 0..(s / 2) {
                for ix in 0..s {
                    chunk.set_sample(ix, iy, iz, -1.0);
                }
            }
        }
        let mut chunks = HashMap::new();
        chunks.insert(coord, chunk);
        let ColliderShape::TriangleMeshIndexed { vertices, .. } =
            build_chunk_collider_shape(&chunks, &settings, coord).expect("有効メッシュ")
        else {
            panic!("TriangleMeshIndexed");
        };
        // チャンクは原点(3,0,-2)から離れているが、頂点はローカルなので [0, extent] 近傍に収まる。
        let extent = settings.chunk_extent();
        for v in &vertices {
            for &c in v {
                assert!(
                    c >= -1.0 && c <= extent + 1.0,
                    "頂点座標 {c} がチャンクローカル範囲(0..{extent})から外れている（ワールド座標が混入した疑い）"
                );
            }
        }
    }

    /// 【計測専用】物理開始時の全チャンクコライダー生成コストを、旧経路（直列 MC）と
    /// 新経路（描画メッシュ再利用）で比較する。
    ///
    ///   cargo test -p SEED terrain_ops::tests::bench_register_colliders -- --ignored --nocapture
    ///
    /// 48 チャンク（4×4×3。既定地面と同数）ぶんを、cells=32/64 で計測する。
    /// 各チャンクはサイン波起伏を入れて必ず等値面を持たせる（＝全チャンクにコライダーが付く
    /// 最悪ケース。ユーザーが掘って凹凸を付けた地形に相当する）。
    #[test]
    #[ignore = "計測専用。--ignored --nocapture で実行"]
    fn bench_register_colliders() {
        use std::time::Instant;
        // 既定地面と同じチャンク数（4×4、Y=-1..=1 の 3 段＝48 枚）。
        const NX: i32 = 4;
        const NZ: i32 = 4;
        const NY: [i32; 3] = [-1, 0, 1];

        for cells in [32u32, 64u32] {
            let mut settings = TerrainSettings::default();
            settings.apply_chunk_config(NX as u32, NZ as u32, cells, settings.voxel_size);
            let extent = settings.chunk_extent();
            let freq = std::f32::consts::TAU / (extent * 0.25);

            // 全チャンクを起伏付きで作る（必ず表面が横切るよう density = y - height）。
            let mut chunks: HashMap<ChunkCoord, TerrainChunkData> = HashMap::new();
            let mut coords: Vec<ChunkCoord> = Vec::new();
            for x in 0..NX {
                for z in 0..NZ {
                    for &y in &NY {
                        let coord = ChunkCoord::new(x, y, z);
                        // from_fn はワールド座標 (wx, wy, wz) を渡す。density = y - height。
                        let chunk = TerrainChunkData::from_fn(&settings, coord, |wx, wy, wz| {
                            let h = (wx * freq).sin() * 3.0 + (wz * freq).cos() * 3.0;
                            wy - h
                        });
                        chunks.insert(coord, chunk);
                        coords.push(coord);
                    }
                }
            }
            coords.sort_by_key(|c| (c.x, c.y, c.z));
            let layers = TerrainLayerSet::default();
            let cell_i = settings.chunk_cells as i32;
            let clamp = settings.density_clamp;

            // 事前に全チャンクの描画 Model を作る（実機では init/ロードで生成済みの状態に相当）。
            let models: HashMap<ChunkCoord, Model> = coords
                .iter()
                .map(|&coord| {
                    let base = [coord.x * cell_i, coord.y * cell_i, coord.z * cell_i];
                    let mesh = terrain::generate(chunks.get(&coord).unwrap(), &settings, |lx, ly, lz| {
                        read_global_impl(&chunks, cell_i, clamp, base[0] + lx, base[1] + ly, base[2] + lz)
                    });
                    let (m, _) = terrain_mesh_to_model(&mesh, "b", coord.world_origin(&settings), &layers);
                    (coord, m)
                })
                .collect();

            // ── (A) 旧経路: 直列 MC（build_chunk_collider_shape を 1 枚ずつ）──
            let t = Instant::now();
            let mut a_count = 0usize;
            for &coord in &coords {
                if build_chunk_collider_shape(&chunks, &settings, coord).is_some() {
                    a_count += 1;
                }
            }
            let a_ms = t.elapsed().as_secs_f64() * MILLIS_PER_SEC;

            // ── (B) 参考: 並列 MC（rayon）──
            let t = Instant::now();
            let b: Vec<_> = coords
                .par_iter()
                .map(|&coord| build_chunk_collider_shape(&chunks, &settings, coord))
                .collect();
            let b_ms = t.elapsed().as_secs_f64() * MILLIS_PER_SEC;
            let b_count = b.iter().filter(|s| s.is_some()).count();

            // ── (C) 新経路: 描画メッシュ再利用（MC なし・並列）──
            let t = Instant::now();
            let c: Vec<_> = coords
                .par_iter()
                .map(|&coord| collider_shape_from_model(models.get(&coord).unwrap()))
                .collect();
            let c_ms = t.elapsed().as_secs_f64() * MILLIS_PER_SEC;
            let c_count = c.iter().filter(|s| s.is_some()).count();

            assert_eq!(a_count, b_count);
            assert_eq!(a_count, c_count, "再利用経路のコライダー数が MC 経路と一致する");

            println!(
                "[BENCH phys] cells={cells:>3} chunks={} with_collider={a_count} | \
                 (A)serial_MC={a_ms:.1}ms (B)par_MC={b_ms:.1}ms (C)reuse_mesh={c_ms:.2}ms  \
                 speedup A/C={:.0}x",
                coords.len(),
                a_ms / c_ms.max(0.0001),
            );
        }
    }

    /// 描画メッシュ（Model）から取り出したコライダーが、MC を回して作る正典
    /// （`build_chunk_collider_shape`）とビット一致すること。
    ///
    /// これが成り立つ限り、`register_all_terrain_colliders` は MC を二重に回さず描画メッシュを
    /// 再利用できる（＝物理開始時フリーズの根治）。`build_terrain_model` は頂点順・インデックスを
    /// 保つため、頂点位置列・インデックス列まで完全一致する。
    #[test]
    fn collider_from_model_matches_mc_build() {
        let settings = test_settings();
        let coord = ChunkCoord::new(0, 0, 0);
        // 下半分を SOLID にして内部に等値面を作る（既存テストと同じ作り）。
        let mut chunk = TerrainChunkData::new_filled(&settings, 1.0);
        let s = chunk.samples_per_axis();
        for iz in 0..s {
            for iy in 0..(s / 2) {
                for ix in 0..s {
                    chunk.set_sample(ix, iy, iz, -1.0);
                }
            }
        }
        let mut chunks = HashMap::new();
        chunks.insert(coord, chunk);

        // ── 正典: MC 経路のコライダー ──
        let ColliderShape::TriangleMeshIndexed { vertices: mc_v, indices: mc_i } =
            build_chunk_collider_shape(&chunks, &settings, coord).expect("有効メッシュ")
        else {
            panic!("TriangleMeshIndexed");
        };

        // ── 検証対象: 描画メッシュ（Model）から取り出したコライダー ──
        // `build_chunk_cpu_model` と同じ手順で Model を作る。
        let cells = settings.chunk_cells as i32;
        let clamp = settings.density_clamp;
        let base = [coord.x * cells, coord.y * cells, coord.z * cells];
        let mesh = terrain::generate(chunks.get(&coord).unwrap(), &settings, |lx, ly, lz| {
            read_global_impl(&chunks, cells, clamp, base[0] + lx, base[1] + ly, base[2] + lz)
        });
        let layers = TerrainLayerSet::default();
        let (model, _) =
            terrain_mesh_to_model(&mesh, "test_chunk", coord.world_origin(&settings), &layers);
        let ColliderShape::TriangleMeshIndexed { vertices: m_v, indices: m_i } =
            collider_shape_from_model(&model).expect("Model からコライダーを取り出せる")
        else {
            panic!("TriangleMeshIndexed");
        };

        assert_eq!(mc_v, m_v, "頂点位置（チャンクローカル）が MC 経路と一致する");
        assert_eq!(mc_i, m_i, "三角形インデックスが MC 経路と一致する");
    }

    /// 空メッシュ（全 AIR）チャンクの Model からはコライダーを作らない（None）。
    #[test]
    fn collider_from_model_empty_is_none() {
        let settings = test_settings();
        let coord = ChunkCoord::new(0, 0, 0);
        let mut chunks = HashMap::new();
        // 全 AIR（+1）＝等値面なし → 三角形 0 の Model。
        chunks.insert(coord, TerrainChunkData::new_filled(&settings, 1.0));
        let cells = settings.chunk_cells as i32;
        let clamp = settings.density_clamp;
        let mesh = terrain::generate(chunks.get(&coord).unwrap(), &settings, |lx, ly, lz| {
            read_global_impl(&chunks, cells, clamp, lx, ly, lz)
        });
        let layers = TerrainLayerSet::default();
        let (model, _) =
            terrain_mesh_to_model(&mesh, "empty", coord.world_origin(&settings), &layers);
        assert!(
            collider_shape_from_model(&model).is_none(),
            "空メッシュ Model はコライダーを持たない"
        );
    }

    /// 地形コライダーの PhysicsObject が「回転なし・スケール 1・RigidBody なし（Static）」で
    /// 位置がそのまま渡ること。
    #[test]
    fn terrain_collider_object_is_static_untransformed() {
        let shape = ColliderShape::TriangleMeshIndexed {
            vertices: vec![[0.0; 3], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0]],
            indices: vec![[0, 1, 2]],
        };
        let obj = terrain_collider_object(42, [3.0, 0.0, -5.0], shape);
        assert_eq!(obj.entity_id, 42);
        assert_eq!(obj.position, [3.0, 0.0, -5.0]);
        assert_eq!(obj.rotation, [0.0, 0.0, 0.0, 1.0], "回転は単位クォータニオン");
        assert_eq!(obj.scale, [1.0, 1.0, 1.0], "スケールは 1");
        assert_eq!(obj.collider_offset, [0.0, 0.0, 0.0]);
        assert!(obj.rigidbody.is_none(), "地形は Static（RigidBody なし）");
        assert!(!obj.is_trigger);
    }

    /// 追加対象の列挙が「既存チャンクを含まない」＝既存地形を温存すること。
    #[test]
    fn collect_new_chunks_excludes_existing() {
        let settings = test_settings();
        let chunks = ground_map(&settings);
        // (0..=1) × (0..=0) の 2 枚を要求するが、(0,0,0) は既存なので 1 枚だけ返るはず。
        let new = collect_new_chunk_coords(&chunks, &settings, 0, 0, 1, 0);
        assert_eq!(new, vec![ChunkCoord::new(1, 0, 0)], "既存チャンクは列挙されてはならない");
    }

    /// 範囲が反転（min > max）していても正規化されて同じ結果になること。
    #[test]
    fn collect_new_chunks_normalizes_reversed_range() {
        let settings = test_settings();
        let chunks = ground_map(&settings);
        let forward  = collect_new_chunk_coords(&chunks, &settings, 1, 0, 2, 1);
        let reversed = collect_new_chunk_coords(&chunks, &settings, 2, 1, 1, 0);
        assert_eq!(forward, reversed);
        assert_eq!(forward.len(), 4, "2×2 枚が新規として列挙される");
    }

    /// Y 範囲の段数ぶんチャンクが積まれること（縦方向は設定に従う）。
    #[test]
    fn collect_new_chunks_spans_configured_y_range() {
        let mut settings = test_settings();
        settings.ground_chunk_y_min = -1;
        settings.ground_chunk_y_max = 1;
        let chunks = HashMap::new();
        let new = collect_new_chunk_coords(&chunks, &settings, 5, 5, 5, 5);
        assert_eq!(new.len(), 3, "y=-1,0,1 の 3 段が作られる");
    }

    /// 追加チャンクの接する面が、既存チャンクの境界サンプルとビット一致すること。
    ///
    /// これが崩れると、同じグローバルサンプル座標に 2 つの異なる密度が存在することになり、
    /// マーチングキューブスが両側で違う等値面を出して継ぎ目に穴／段差が生じる。
    #[test]
    fn new_chunk_boundary_matches_existing_neighbor() {
        let settings = test_settings();
        let cells = settings.chunk_cells as usize;
        let samples = settings.samples_per_axis();
        let mut chunks = ground_map(&settings);

        // 既存チャンク (0,0,0) の +X 面（ローカル lx = cells）を「編集済み」に見せかける。
        // 追加チャンク (1,0,0) の -X 面（lx = 0）と重複所有する面である。
        {
            let existing = chunks.get_mut(&ChunkCoord::new(0, 0, 0)).unwrap();
            let marker_slots = BlendSlots {
                index:  [MARKER_PAINT_LAYER, 0, 0, 0],
                weight: [1.0, 0.0, 0.0, 0.0],
            };
            for lz in 0..samples {
                for ly in 0..samples {
                    existing.set_sample(cells, ly, lz, MARKER_DENSITY);
                    existing.set_paint_slots(cells, ly, lz, &marker_slots);
                    existing.set_paint_amount(cells, ly, lz, MARKER_PAINT_AMOUNT);
                }
            }
        }

        // 追加チャンクを平坦地面で作り、境界を既存へ揃える。
        let new_coord = ChunkCoord::new(1, 0, 0);
        let mut new_chunk = TerrainChunkData::from_ground_plane(&settings, new_coord);
        sync_new_chunk_boundary(&chunks, &settings, new_coord, &mut new_chunk);

        let existing = chunks.get(&ChunkCoord::new(0, 0, 0)).unwrap();
        for lz in 0..samples {
            for ly in 0..samples {
                // 密度: 共有面がビット一致すること。
                assert_eq!(
                    new_chunk.sample(0, ly, lz), existing.sample(cells, ly, lz),
                    "共有面の密度が一致しない (ly={ly}, lz={lz})"
                );
                // ペイント量・レイヤ番号も引き継がれること（色の継ぎ目も出さない）。
                assert_eq!(
                    new_chunk.paint_amount(0, ly, lz), existing.paint_amount(cells, ly, lz),
                    "共有面のペイント量が一致しない (ly={ly}, lz={lz})"
                );
                assert_eq!(
                    new_chunk.paint_slots(0, ly, lz).index[0], MARKER_PAINT_LAYER,
                    "共有面のペイントレイヤ番号が引き継がれていない (ly={ly}, lz={lz})"
                );
            }
        }
    }

    /// 隣接チャンクが無い面と内部サンプルは、平坦地面の初期値のまま保たれること。
    ///
    /// 「境界を揃える」処理が広く塗り潰してしまうと、新しい地面が既存の編集内容で
    /// 汚染される（例: 端の高さが地形全体へ波及する）。触る範囲を境界面に限る回帰テスト。
    #[test]
    fn new_chunk_keeps_ground_plane_where_no_neighbor() {
        let settings = test_settings();
        let cells = settings.chunk_cells as usize;
        let samples = settings.samples_per_axis();
        let mut chunks = ground_map(&settings);
        {
            let existing = chunks.get_mut(&ChunkCoord::new(0, 0, 0)).unwrap();
            for lz in 0..samples {
                for ly in 0..samples {
                    existing.set_sample(cells, ly, lz, MARKER_DENSITY);
                }
            }
        }

        let new_coord = ChunkCoord::new(1, 0, 0);
        let pristine = TerrainChunkData::from_ground_plane(&settings, new_coord);
        let mut new_chunk = pristine.clone();
        sync_new_chunk_boundary(&chunks, &settings, new_coord, &mut new_chunk);

        for lz in 0..samples {
            for ly in 0..samples {
                for lx in 0..samples {
                    // 既存チャンクと共有するのは -X 面（lx=0）だけ。それ以外は不変であること。
                    if lx == 0 {
                        continue;
                    }
                    assert_eq!(
                        new_chunk.sample(lx, ly, lz), pristine.sample(lx, ly, lz),
                        "隣接の無いサンプルが書き換えられた ({lx},{ly},{lz})"
                    );
                }
            }
        }
    }

    /// 隣接チャンクが 1 枚も無ければ、追加チャンクは完全に平坦地面のままであること。
    #[test]
    fn isolated_new_chunk_is_untouched() {
        let settings = test_settings();
        let chunks: HashMap<ChunkCoord, TerrainChunkData> = HashMap::new();
        let new_coord = ChunkCoord::new(9, 0, 9);
        let pristine = TerrainChunkData::from_ground_plane(&settings, new_coord);
        let mut new_chunk = pristine.clone();
        sync_new_chunk_boundary(&chunks, &settings, new_coord, &mut new_chunk);
        assert_eq!(new_chunk.raw_density(), pristine.raw_density());
    }
}
