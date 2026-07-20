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
use std::time::Instant;

use rayon::prelude::*;

use crate::engine::ecs::Entity;
use crate::engine::components::{
    ComponentKind, InstanceMeta, ModelComponent, TerrainChunkComponent,
    Transform as ActorTransform, GROUP_ID_BASE, next_batch_instance_id,
};
use crate::engine::core::loader::model::Model;
use crate::engine::methods::drawer::{DrawContext, GpuModel, InstancedModelBatch};
use crate::engine::structs::objects::Actor;
use crate::engine::terrain::{
    self, interp_vertex_paint, BlendSlots, BrushOp, ChunkCoord, PaintField, SampleField,
    SphereBrush, TerrainChunkData, TerrainLayerSet, TerrainSettings, TerrainVertexEdge,
    TERRAIN_BLEND_SLOTS, TERRAIN_MAX_LAYERS, tvox,
};

// 散布プロップ（Terrain T3）。状態の器だけをここで持ち、処理は terrain_scatter_ops.rs にある。
use crate::engine::core::renderer::grass_gbuffer::GrassInstanceBuffer;
use crate::engine::terrain::scatter::{ScatterInstance, TerrainPropSet};

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
const TERRAIN_ROOT_NAME: &str = "terrain";
/// 各チャンクのメッシュを載せるアクターの名前。
const TERRAIN_MESH_NAME: &str = "mesh";
/// メッシュアクターの ModelComponent スロット名。
const TERRAIN_MODEL_SLOT_NAME: &str = "mesh";
/// メッシュアクターの TerrainChunkComponent スロット名。
const TERRAIN_CHUNK_SLOT_NAME: &str = "chunk";

/// クリック 1 回ぶんのブラシ適用時間（離散編集なので 1.0 秒相当）。
const BRUSH_DT: f32 = 1.0;

// ─── 地形編集の計測ログ（[PERF terrain]） ────────────────────────────────
/// 計測ログを有効化する環境変数名。frame_renderer.rs の `[PERF]` ログと**同じ**変数を使い、
/// 「SEED_PERF_LOG を付ければ全系統の PERF 行が出る」という既存の流儀を崩さない。
const PERF_LOG_ENV: &str = "SEED_PERF_LOG";

/// `[PERF terrain]` 行を出力するかどうか（既定は無効）。
///
/// フレーム描画側は 60 フレームに 1 回へ間引いているが、地形編集は間欠的（ドラッグ中のみ）で
/// 1 回 1 回が重いため、**再メッシュが起きたら毎回出す**。間引くと肝心のスパイクを取り逃す。
static PERF_TERRAIN_LOG_ENABLED: std::sync::LazyLock<bool> =
    std::sync::LazyLock::new(|| std::env::var_os(PERF_LOG_ENV).is_some());

/// 秒 → ミリ秒の換算係数（マジックナンバー回避）。
const MILLIS_PER_SEC: f64 = 1000.0;

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
/// クローズアップ時、注視点からカメラまでの水平距離（メートル）。
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
/// クローズアップの被写体に選ぶプロップ添字（0 = props.json の先頭 = grass_field）。
const SMOKE_CLOSEUP_PROP_INDEX: u32 = 0;

/// クローズアップ切替フレーム（環境変数 `SEED_SMOKE_CLOSEUP_FRAME`）。未指定なら `None`。
static SMOKE_CLOSEUP_FRAME: std::sync::LazyLock<Option<u32>> =
    std::sync::LazyLock::new(|| {
        std::env::var(ENV_SMOKE_CLOSEUP_FRAME).ok()?.trim().parse::<u32>().ok()
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
pub struct TerrainEdit {
    /// ストローク開始時点のチャンク状態（chunk coord -> スナップショット）。
    pub before: HashMap<ChunkCoord, ChunkSnapshot>,
    /// ストローク終了時点のチャンク状態（chunk coord -> スナップショット）。
    pub after: HashMap<ChunkCoord, ChunkSnapshot>,
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
    pub grass_gpu_dirty: bool,
    /// 散布ブラシで使う現在のプロップ添字（エディタの選択と対応）。
    pub scatter_prop: usize,
    /// ルール散布の大域シード（決定性の要。UI から変えられる）。
    pub scatter_seed: u64,
}

impl Default for TerrainState {
    fn default() -> Self {
        Self {
            settings: TerrainSettings::default(),
            chunks: HashMap::new(),
            chunk_slot_entity: HashMap::new(),
            scene_name: String::new(),
            dirty: HashSet::new(),
            pending_remesh: HashSet::new(),
            pending_paint: HashSet::new(),
            chunk_vertex_edges: HashMap::new(),
            brush_preview: None,
            stroke_active: false,
            stroke_before: HashMap::new(),
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
            scatter_prop: 0,
            scatter_seed: DEFAULT_SCATTER_SEED,
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
    let (model, is_empty, edges) = build_chunk_cpu_model(chunks, settings, layers, coord)?;
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
) -> Option<(Arc<Model>, bool, Arc<Vec<TerrainVertexEdge>>)> {
    let chunk = chunks.get(&coord)?;
    let cells = settings.chunk_cells as i32;
    let clamp = settings.density_clamp;
    // このチャンクのローカルサンプル (lx,ly,lz) → グローバルサンプル座標 = coord*cells + local。
    let base = [coord.x * cells, coord.y * cells, coord.z * cells];
    let mut mesh = terrain::generate(chunk, settings, |lx, ly, lz| {
        read_global_impl(chunks, cells, clamp, base[0] + lx, base[1] + ly, base[2] + lz)
    });
    // 由来辺（頂点がどの辺のどこで生まれたか）はここでしか手に入らないので取り出す。
    // ペイント高速パスがマーチングキューブスを再実行せずにスプラットを引き直すための唯一の手掛かり。
    // `Vec` を丸ごと move して以後の再確保・コピーを避ける（メッシュ側では使わない）。
    let edges = Arc::new(std::mem::take(&mut mesh.edges));
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
        let settings = self.terrain.settings.clone();
        self.terrain = TerrainState::default();
        self.terrain.settings = settings.clone();
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
        {
            let ctx = self.draw_ctx.as_ref().unwrap();
            for &coord in &coords {
                // 空メッシュチャンクも Some(model, None, None) で返るため、全チャンクが
                // アクター＋MC スロットを得る（掘削で後から表面が出ても差し替えられる）。
                if let Some((model, gpu, batch, edges)) = build_chunk_render(&self.terrain.chunks, &settings, &layers, ctx, coord) {
                    prebuilt.push((coord, model, gpu, batch));
                    prebuilt_edges.push((coord, edges));
                }
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
        self.invalidate_geometry_caches(&rebuilt_keys);

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

        // ── ② 全チャンクを再メッシュ化する ──
        //   remesh_chunks が &mut self を取るため、対象座標は先に確定させておく。
        let coords: Vec<ChunkCoord> = self.terrain.chunk_slot_entity.keys().copied().collect();
        self.remesh_chunks(&coords);

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
            let mut view = FieldView {
                settings: &terrain.settings,
                chunks: &mut terrain.chunks,
            };
            terrain::paint::apply_paint(&mut view, &brush, layer as u32, BRUSH_DT)
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
        self.remesh_chunks(&neighbor_list);

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

        // ── ① undo 用ストローク開始（暗黙）＆ 編集前スナップショット ──
        //   ストローク開始は「stroke_active でない状態で最初の TERRAIN_BRUSH（＝本メソッド呼び出し）
        //   が来たとき」に暗黙的に始まる（専用の開始 IPC は無い）。
        //   ブラシ適用前に「このブラシが触りうるチャンク集合」（superset で可）の各既存チャンクを
        //   走査し、ストローク中でまだ控えていないものだけ現在の密度をスナップショットする
        //   （2 発目以降のブラシで上書きしてしまうと「ストローク開始時点」の密度でなくなるため）。
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

        // ── ② 球ブラシを密度場へ適用（settings と chunks を分割借用して FieldView を作る）──
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
    pub(super) fn flush_terrain_pending_remesh(&mut self) {
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
            self.remesh_chunks(&coords);
            // ── 密度編集で地面が動いた → 散布プロップを新しい地表へ貼り直す ──
            //   ここは密度ブラシ由来の経路（pending_remesh）専用である。
            //   ペイント高速パス（下の ②）では **意図的に呼ばない**：
            //   ペイントは密度グリッドを一切変えないため頂点が動かず、
            //   草が宙に浮くことも埋まることも構造的に起こり得ない。
            //   一方でペイントは 1 ストロークで何十回も飛んでくるので、
            //   そこで全インスタンスの柱探索を走らせると目に見えて重くなる。
            self.restick_scatter_for_chunks(&coords);
        }

        // ── ② 頂点カラーだけの更新待ちを消化する（ペイント高速パス）──
        //   決定性の担保は ① と同じ理由でソートする（`HashSet` の走査順は実行ごとに変わる）。
        if !self.terrain.pending_paint.is_empty() {
            let mut coords: Vec<ChunkCoord> =
                std::mem::take(&mut self.terrain.pending_paint).into_iter().collect();
            coords.sort_by_key(|c| (c.x, c.y, c.z));
            self.apply_terrain_paint_colors(&coords);
        }
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
    fn remesh_chunks(&mut self, coords: &[ChunkCoord]) {
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
            .map(|&coord| build_chunk_cpu_model(&self.terrain.chunks, &settings, &layers, coord))
            .collect();
        let cpu_ms = t_cpu.elapsed().as_secs_f64() * MILLIS_PER_SEC;

        // 地形チャンクが使うパレットを group3 へ登録する（描画前に済ませる必要がある）。
        // パレット用バインドグループはメッシュ VRAM とは別枠なので、フェーズ A より前でよい。
        self.ensure_terrain_palettes(
            cpu_models.iter().filter_map(|m| m.as_ref()).map(|(model, _, _)| model.as_ref()),
        );

        // ── フェーズ A: 対象全チャンクの旧 GpuModel を drop する ──
        //   ここで手放すのはチャンク専有のリソースなので、他の描画には影響しない。
        let t_swap = Instant::now();
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
                    mc.gpu_model = None;
                }
            }
        }

        // ── フェーズ B: 遅延破棄をここで 1 回だけ確定させる（GPU アイドル待ち） ──
        //   wgpu 25 の poll API。ループ外に置くことがこの関数の最大の要点。
        let t_poll = Instant::now();
        if let Some(ctx) = self.draw_ctx.as_ref() {
            let _ = ctx.device.poll(wgpu::PollType::Wait);
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
                    mc.gpu_model = gpu;
                    mc.instanced_batch = batch;
                    // バッチ更新をマークする。
                    mc.mark_batch_dirty();
                    swapped_keys.push(mc.source_path.clone());
                }
            }
            self.terrain.dirty.insert(coord);
        }

        // ── フェーズ C-3: ジオメトリ由来の派生キャッシュを破棄する（下記メソッドの説明を参照） ──
        self.invalidate_geometry_caches(&swapped_keys);
        let swap_ms = t_swap.elapsed().as_secs_f64() * MILLIS_PER_SEC;

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
                    &prim.vertices, &prim.indices, &model.name, &colors, palette,
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
        }

        // ── ⑨ フォールバック対象はまとめてフル再メッシュへ回す ──
        if !fallback.is_empty() {
            self.remesh_chunks(&fallback);
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
    fn invalidate_geometry_caches(&mut self, keys: &[String]) {
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
        if let Some(ctx) = self.draw_ctx.as_ref() {
            if let Some(rt_cell) = ctx.rt_shadow.as_ref() {
                rt_cell.borrow_mut().prune_source_paths(keys);
            }
        }
    }

    /// 現在進行中のブラシストロークを 1 つの undo エントリとして確定する（TERRAIN_STROKE_END）。
    ///
    /// stroke_before（ストローク開始時点のスナップショット）が空でなければ、その各チャンクの
    /// 現在密度を after として集めて TerrainEdit を undo_stack へ push し、redo_stack をクリアする
    /// （新しい編集が確定したら、それより後の未来を指していた redo 履歴は無効になるため）。
    /// stroke_before が空（＝実質何も変化しないまま終わった）場合は単に stroke_active を戻すだけ。
    pub(super) fn handle_terrain_stroke_end(&mut self) {
        if !self.terrain.stroke_before.is_empty() {
            // before を丸ごと取り出す（以後 stroke_before は空に戻る）。
            let before = std::mem::take(&mut self.terrain.stroke_before);

            // ── before と同じチャンク集合について、現在（ストローク終了時点）の密度を after として集める ──
            let mut after: HashMap<ChunkCoord, ChunkSnapshot> = HashMap::with_capacity(before.len());
            for &coord in before.keys() {
                if let Some(chunk) = self.terrain.chunks.get(&coord) {
                    after.insert(coord, ChunkSnapshot::capture(chunk));
                }
            }

            self.terrain.undo_stack.push(TerrainEdit { before, after });
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
        self.remesh_chunks(&touched);
        // ── 密度を戻すと地面も戻るので、散布プロップを新しい地表へ貼り直す ──
        //   【散布そのものは undo されない（T3 第1段のスコープ外）】
        //   undo/redo スタックが持つのは密度とスプラットのスナップショットだけで、
        //   .tscatter の内容は含まれない。したがって「草を生やす → undo」で
        //   草は消えない。
        //   それでも再接地だけは掛ける。掛けないと「undo したら草だけ空中に
        //   取り残される」という明確に壊れた見た目になるからである。
        //   再接地により「草は常に今の地面に載っている」という不変条件は保たれる。
        self.restick_scatter_for_chunks(&touched);
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
        self.remesh_chunks(&touched);
        // undo と同じ理由で再接地する（散布自体は redo の対象外）。
        self.restick_scatter_for_chunks(&touched);
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
        let (scatter_written, scatter_removed) = self.save_terrain_scatter(&dir);

        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("TERRAIN_SAVE_OK:{count}"));
        }
        if *PERF_TERRAIN_LOG_ENABLED {
            eprintln!(
                "[SEED terrain] save: tvox={count} tscatter written={scatter_written} removed={scatter_removed}"
            );
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
        //
        //   【チャンク構成の復元】
        //     .tvox ヘッダは samples_per_axis（= chunk_cells + 1）と voxel_size を持つ。
        //     チャンク分割数はエディタから変更できるため、既定値（32 / 0.5m）とは限らない。
        //     そこで **最初に読めたチャンクのヘッダを正として settings へ取り込み**、
        //     以降のチャンクはそのヘッダと一致するものだけを受け入れる。
        //     不一致チャンク（＝分割数を変えた後に一部だけ古い .tvox が残っている状態）は
        //     読み込むと密度配列の長さが食い違って描画・編集が破綻するため、警告して捨てる。
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

        // ── フェーズ 1.5: 散布データ（.tscatter）を読む ──
        //   ファイルが無いのは **エラーではない**（散布機能より前に保存された
        //   シーンには存在しない）。欠落チャンクは単に散布 0 本として扱う。
        //   プロップ定義も併せて読む（インスタンスの prop_id を解決するのに要る）。
        self.ensure_terrain_props();
        self.load_terrain_scatter(&scatter_paths);

        // ── フェーズ 2: 全チャンク読込後にメッシュ化（隣接読みが揃った状態で継ぎ目を正しく作る）──
        //   レイヤ定義もここで読み直す（.tvox v1 のようにスプラットを持たないデータでも
        //   ルール自動生成が効くようにするため、定義は常に手元に必要）。
        self.ensure_terrain_layers();
        let settings = self.terrain.settings.clone();
        let layers = self.terrain.layers.clone();
        let mut prebuilt: Vec<(Entity, Arc<Model>, Option<GpuModel>, Option<InstancedModelBatch>)> = Vec::new();
        // 由来辺は借用の都合で一旦ローカルへ溜め、この後 self.terrain へ入れる。
        let mut prebuilt_edges: Vec<(ChunkCoord, Arc<Vec<TerrainVertexEdge>>)> = Vec::new();
        {
            let ctx = self.draw_ctx.as_ref().unwrap();
            for (coord, mc_slot) in &loaded {
                // 空メッシュチャンクは gpu/batch=None で返る（非描画のまま MC を埋める）。
                if let Some((model, gpu, batch, edges)) = build_chunk_render(&self.terrain.chunks, &settings, &layers, ctx, *coord) {
                    prebuilt.push((*mc_slot, model, gpu, batch));
                    prebuilt_edges.push((*coord, edges));
                }
            }
        }
        // 由来辺キャッシュを登録する（`self.terrain` はこの関数の冒頭で default に
        // 差し替わっているため、前のシーンの辺が混ざることはない）。
        for (coord, edges) in prebuilt_edges {
            self.terrain.chunk_vertex_edges.insert(coord, edges);
        }
        // 地形チャンクが使うパレットを group3 へ登録する（描画前に済ませる必要がある）。
        self.ensure_terrain_palettes(prebuilt.iter().map(|p| p.1.as_ref()));

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
        self.invalidate_geometry_caches(&loaded_keys);
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
        self.handle_terrain_init(Some(TerrainChunkConfig {
            chunks_x:    SMOKE_DEFAULT_CHUNKS,
            chunks_z:    SMOKE_DEFAULT_CHUNKS,
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
        let cam = crate::engine::core::app_base::scene::DebugCameraData {
            position: eye,
            yaw,
            pitch,
            fov_deg: SMOKE_CAM_FOV_DEG,
            far: SMOKE_CAM_FAR,
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
        {
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

        // 対象プロップのインスタンスが最も多いチャンクを選ぶ（草が濃い場所＝絵になる場所）。
        let Some((_, instances)) = self
            .terrain
            .scatter
            .iter()
            .map(|(coord, list)| {
                let count = list.iter().filter(|i| i.prop_id == SMOKE_CLOSEUP_PROP_INDEX).count();
                (coord, list, count)
            })
            .max_by_key(|(_, _, count)| *count)
            .map(|(coord, list, _)| (coord, list))
        else {
            eprintln!("[SEED terrain] smoke: closeup 対象の散布データがありません");
            return;
        };
        // そのチャンク内の対象プロップ重心に最も近い 1 本を被写体にする
        // （重心そのものは草が無い窪みに落ちることがあるため、実在インスタンスへ吸着させる）。
        let picked: Vec<[f32; 3]> = instances
            .iter()
            .filter(|i| i.prop_id == SMOKE_CLOSEUP_PROP_INDEX)
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
            target[2] - SMOKE_CLOSEUP_DISTANCE,
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
}

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
