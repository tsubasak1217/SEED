// ============================================================
//  terrain_scatter_ops.rs — 地形プロップ散布のエンジン統合層
//
//  【責務】
//    純粋データ層（engine/terrain/scatter/）と、実行中のエンジン
//    （ECS チャンク管理・GPU・ファイル IO・IPC）を繋ぐ層。
//    アルゴリズムそのものは一切持たない（持たせない）。
//      * 散布の生成規則   → scatter/generate.rs
//      * プロップ定義      → scatter/props.rs
//      * 永続化フォーマット → scatter/tscatter.rs
//      * 草の描画          → renderer/grass_gbuffer.rs
//    本ファイルがやるのは「それらをどこから呼び、結果をどこへ置くか」だけである。
//
//  【terrain_ops.rs と分けた理由】
//    terrain_ops.rs は既に 3000 行を超えており、密度編集・ペイント・メッシュ化
//    という 3 つの関心事で飽和している。散布は「地形の上に載る別レイヤ」であり
//    密度グリッドとは更新頻度も永続化ファイルも独立しているため、
//    単一責任原則に従って別ファイルへ分離した。
//
//  【本ファイルが提供するもの】
//    * TerrainScatterField — チャンクマップ上の `ScatterField` 実装
//    * App::ensure_terrain_props        — props.json の読み込み
//    * App::handle_terrain_scatter_rules — ルール自動散布（IPC）
//    * App::handle_terrain_scatter_brush — ブラシ散布（IPC）
//    * App::restick_scatter_for_chunks  — 地形編集後の再接地
//    * App::rebuild_grass_gpu           — 草 GPU バッファの再構築
//    * .tscatter の保存／読み込みヘルパ
// ============================================================

use std::collections::HashMap;

// ルール散布のチャンク並列化に使う（terrain_ops.rs の再メッシュ並列化と同じ流儀）。
use rayon::prelude::*;

use super::terrain_ops::{find_owner, sample_density_world, TerrainState};
use super::App;
use crate::engine::core::renderer::grass_gbuffer::{
    GrassChunkSpan, GrassInstanceBuffer, GrassInstanceGpu, GrassUniformGpu,
};
use crate::engine::terrain::chunk_coord::ChunkCoord;
use crate::engine::terrain::chunk_data::TerrainChunkData;
use crate::engine::terrain::layers::{blend_rule_and_paint_all, TerrainLayerSet};
// 散布データ層は mod.rs の再エクスポート（＝モジュールの公開 API）経由で参照する。
// 各サブモジュールを直接指さないのは、公開 API を 1 か所に集約しておくためである。
use crate::engine::terrain::scatter::{
    read_chunk, restick_instances, scatter_brush, scatter_chunk_by_rules, write_chunk,
    PropKind, ScatterField, ScatterInstance, TerrainProp, TerrainPropSet,
};
use crate::engine::terrain::settings::TerrainSettings;
// kind=Model 散布プロップのロード・GPU 化・インスタンス描画に使う既存 API。
use crate::engine::core::loader::{load_model, model::Model};
use crate::engine::methods::drawer::{DrawContext, GpuModel, InstancedModelBatch};

// ============================================================
//  定数（マジックナンバー禁止）
// ============================================================

/// 散布プロップ定義アセットの仮想パス（データドリブン。ここを差し替えれば草が変わる）。
const TERRAIN_PROPS_ASSET: &str = "assets://terrain/props.json";

/// プロップ定義の読み込み元を差し替える環境変数名（layers.json と同じ流儀）。
const TERRAIN_PROPS_PATH_ENV: &str = "SEED_TERRAIN_PROPS";

/// 地形編集後の再接地で、元の高さから上下どれだけ探索するか（ボクセル数）。
///
/// ブラシ 1 ストロークが地面を動かす量はボクセル数個ぶんに収まるため、
/// 4 ボクセル（既定 0.5m × 4 = 2m）あれば通常の盛り／掘りには追従できる。
/// これを大きくすると、崖の下に落ちた草が遠くの地面へ吸着して不自然になる。
const RESTICK_Y_SEARCH_VOXELS: f32 = 4.0;

/// `TERRAIN_SCATTER_RULES` の prop_id が空文字のときの意味（= 全プロップ対象）。
const SCATTER_ALL_PROPS: &str = "";

/// ミリ秒換算係数（terrain_ops.rs と同じ）。
const MILLIS_PER_SEC: f64 = 1000.0;

/// 法線を求める中心差分の刻み幅（voxel_size に対する比率）。
///
/// scatter/generate.rs の `NORMAL_GRADIENT_EPS_FRACTION` と同じ値にしてある。
/// 散布ルールの斜度判定（generate 側）とレイヤ重み判定（本ファイル側）で
/// 法線の求め方がずれると、「斜度は通ったのにレイヤ重みが 0」という
/// 説明の付かない禿げが出るため、刻み幅を揃えている。
const NORMAL_GRADIENT_EPS_FRACTION: f32 = 0.5;

/// 勾配長がこの値以下なら「勾配なし」とみなして真上を向く（0 除算回避）。
const NORMAL_GRADIENT_EPSILON: f32 = 1.0e-12;

/// 勾配が縮退したときのフォールバック法線（真上）。
const NORMAL_FALLBACK_UP: [f32; 3] = [0.0, 1.0, 0.0];

/// 散布モデルの姿勢基底を組むとき「up が Y 軸と平行すぎる」と判定する |y| しきい値。
///
/// これを超えたら参照ベクトルを X 軸へ切り替える（外積の縮退回避）。
/// generate.rs の `AXIS_PARALLEL_THRESHOLD` と同値にしてある（tilt 生成と姿勢構築で
/// 基底の作り方がずれないようにするため）。
const MODEL_BASIS_PARALLEL_THRESHOLD: f32 = 0.9;

/// ベクトル長がこの二乗以下なら縮退とみなす（0 除算回避）。
const MODEL_NORMALIZE_EPSILON: f32 = 1.0e-12;

/// 散布モデルの統合バッチに確保する最小インスタンス容量。
///
/// 統合モデルバッチ（`shared_model_batches`）の容量規約 `.max(4)` に合わせる。
/// 0 本でもバッファ生成がパニックしないよう最低 4 は確保する。
const SCATTER_MODEL_MIN_CAPACITY: usize = 4;

// ─── チャンク単位カリング（Terrain T3 描画最適化）─────────────────────────────
//
// 【背景】散布は 1 チャンク（16m 角）あたり草を数百〜数千本、木（実メッシュ）を
//   数十〜数百本置く。カリング無しだと画面外・遠方のチャンクまで毎フレーム全ポリゴン
//   描画され、大量散布で 1fps 級に落ちる。そこで各チャンクのワールド AABB を
//   カメラ視錐台＋距離でテストし、可視チャンクぶんだけ描く。
//
// 【なぜ距離をプロップ種別で分けるか】草は近景を埋めるためのもので遠くでは
//   1 ピクセル未満に潰れて見えないので近めで打ち切ってよい。木は輪郭が遠方でも
//   効くのでもう少し遠くまで描く。値はここで名前付き定数として持つ（将来 props.json
//   の per-prop フィールドへ移せるよう、意味を 1 か所に集約している）。
//
// 【AABB マージン＝カリングし過ぎ防止の要（ただし水平は小さく保つ）】
//   インスタンスの原点はチャンク内でも、草は上へ height 分、木は樹高・樹冠分だけ
//   チャンク境界の外へはみ出す。厳密な 16m 立方体で判定すると、原点チャンクが視錐台の
//   縁で切れた瞬間に、まだ見えている樹冠や葉先が消える（偽陽性）。これを防ぐため AABB を
//   マージン分ふくらませる。
//   ただしマージンは**水平は小さく・垂直（上）だけ樹高ぶん大きく**する。水平マージンを
//   大きくすると AABB がチャンク（16m）の数倍に膨れ、カメラ背後や画面外のチャンクまで
//   視錐台／距離テストを通過してしまい（AABB 同士が重なりカメラを包む）、カリングが
//   まったく効かなくなる。木は「縦に高いが横幅は狭い」ので、上方向だけ樹高分を確保し、
//   水平は樹冠のはみ出し程度（数 m）に留めるのが正しい。

/// 草チャンクの距離カリング閾値（メートル・既定）。最近点距離がこれを超えたら描かない。
const GRASS_CULL_DISTANCE_DEFAULT: f32 = 90.0;
/// 散布モデル（木）チャンクの距離カリング閾値（メートル・既定）。
const SCATTER_MODEL_CULL_DISTANCE_DEFAULT: f32 = 220.0;

/// 環境変数で距離閾値を上書きするヘルパ（未設定・不正なら既定値）。
///
/// データドリブン化の第一歩＝定数のチューニングを再ビルド無しで行えるようにする
/// （将来 props.json の per-prop フィールドへ移す前提。名前は SEED_ プレフィクス）。
fn cull_distance_env(var: &str, default: f32) -> f32 {
    std::env::var(var)
        .ok()
        .and_then(|s| s.parse::<f32>().ok())
        .filter(|v| *v > 0.0)
        .unwrap_or(default)
}

/// 草の距離カリング閾値（メートル）。`SEED_GRASS_CULL_DIST` で上書き可。
///
/// 描画側（frame_renderer の草ドロー）も同じ閾値を使うため公開する。
pub(super) static GRASS_CULL_DISTANCE: std::sync::LazyLock<f32> =
    std::sync::LazyLock::new(|| cull_distance_env("SEED_GRASS_CULL_DIST", GRASS_CULL_DISTANCE_DEFAULT));
/// 散布モデルの距離カリング閾値（メートル）。`SEED_MODEL_CULL_DIST` で上書き可。
static SCATTER_MODEL_CULL_DISTANCE: std::sync::LazyLock<f32> =
    std::sync::LazyLock::new(|| cull_distance_env("SEED_MODEL_CULL_DIST", SCATTER_MODEL_CULL_DISTANCE_DEFAULT));

// ─── 遠景密度減衰（植生 LOD 第1段）─────────────────────────────────────────────
//
// 【背景】チャンク距離カリング（上）を通ったチャンクでも、俯瞰では大量のチャンクが
//   可視になり、近景と同じ全密度で描くと重い。そこで**可視チャンクを距離帯で分け、
//   遠いほど描画インスタンスを間引く**（近=全数 / 中=1/2 / 遠=1/4）。間引きは描画時の
//   「先頭 kept 本だけ描く」方式で、散布データ自体は変えない（`gpu_resources::
//   density_kept_count`）。均一に薄くするため、GPU バッファ／行列列はチャンク内で
//   `scatter_thin_key` のハッシュ順に並べておく（プレフィクスが空間的に均一になる）。
//
// 【しきい値はカリング距離の内側に置く】各帯境界（near < mid < cull_distance）。
//   将来 props.json の per-prop フィールドへ移せるよう、意味をここへ集約する。

/// 草の密度減衰・近距離帯の上端（メートル・既定）。これ以内は全密度。
const GRASS_DECAY_NEAR_DEFAULT: f32 = 30.0;
/// 草の密度減衰・中距離帯の上端（メートル・既定）。ここまでは 1/2、以遠は 1/4。
const GRASS_DECAY_MID_DEFAULT: f32 = 55.0;
/// 散布モデル（木）の密度減衰・近距離帯の上端（メートル・既定）。これ以内は全密度。
const SCATTER_MODEL_DECAY_NEAR_DEFAULT: f32 = 70.0;
/// 散布モデル（木）の密度減衰・中距離帯の上端（メートル・既定）。ここまでは 1/2、以遠は 1/4。
const SCATTER_MODEL_DECAY_MID_DEFAULT: f32 = 130.0;

/// 草の密度減衰・近距離帯上端（メートル）。`SEED_GRASS_DECAY_NEAR` で上書き可。
/// 描画側（frame_renderer の草ドロー）が二乗して使うため公開する。
pub(super) static GRASS_DECAY_NEAR: std::sync::LazyLock<f32> =
    std::sync::LazyLock::new(|| cull_distance_env("SEED_GRASS_DECAY_NEAR", GRASS_DECAY_NEAR_DEFAULT));
/// 草の密度減衰・中距離帯上端（メートル）。`SEED_GRASS_DECAY_MID` で上書き可。
pub(super) static GRASS_DECAY_MID: std::sync::LazyLock<f32> =
    std::sync::LazyLock::new(|| cull_distance_env("SEED_GRASS_DECAY_MID", GRASS_DECAY_MID_DEFAULT));
/// 散布モデルの密度減衰・近距離帯上端（メートル）。`SEED_MODEL_DECAY_NEAR` で上書き可。
static SCATTER_MODEL_DECAY_NEAR: std::sync::LazyLock<f32> = std::sync::LazyLock::new(|| {
    cull_distance_env("SEED_MODEL_DECAY_NEAR", SCATTER_MODEL_DECAY_NEAR_DEFAULT)
});
/// 散布モデルの密度減衰・中距離帯上端（メートル）。`SEED_MODEL_DECAY_MID` で上書き可。
static SCATTER_MODEL_DECAY_MID: std::sync::LazyLock<f32> = std::sync::LazyLock::new(|| {
    cull_distance_env("SEED_MODEL_DECAY_MID", SCATTER_MODEL_DECAY_MID_DEFAULT)
});

/// 散布インスタンスを間引き順に安定ソートするためのハッシュ（seed → 撹拌値）。
///
/// 密度減衰は「先頭 kept 本だけ描く」方式のため、インスタンス列をこのハッシュ順へ
/// 並べておくと、任意のプレフィクスが空間的に均一なサブセットになる（＝遠景を
/// 間引いても穴が空かず均等に薄くなる）。`seed` はインスタンスごとに固定の疑似乱数で、
/// 決定的なので毎回同じ並びになり、フレーム間でも保存/ロード間でも間引かれる個体が
/// 変わらない（ちらつき防止）。splitmix32 相当の全単射撹拌で下位ビットの偏りを消す。
#[inline]
pub(super) fn scatter_thin_key(seed: u32) -> u32 {
    let mut z = seed.wrapping_add(0x9E37_79B9);
    z = (z ^ (z >> 16)).wrapping_mul(0x85EB_CA6B);
    z = (z ^ (z >> 13)).wrapping_mul(0xC2B2_AE35);
    z ^ (z >> 16)
}

/// 草チャンク AABB の水平マージン（メートル）。葉先の横はみ出し分。
const GRASS_MARGIN_HORIZ: f32 = 1.0;
/// 草チャンク AABB の上方向マージン（メートル）。草丈＋風の揺れ分。
const GRASS_MARGIN_UP: f32 = 2.0;
/// 散布モデルチャンク AABB の水平マージン（メートル）。樹冠の横はみ出し分（小さく保つ）。
const SCATTER_MODEL_MARGIN_HORIZ: f32 = 4.0;
/// 散布モデルチャンク AABB の上方向マージン（メートル）。想定樹高ぶん（縦だけ大きく）。
const SCATTER_MODEL_MARGIN_UP: f32 = 16.0;

/// 散布プロップ定義の読み込み失敗を警告するのは 1 回だけにするためのフラグ。
///
/// props.json が無い環境で毎フレーム／毎コマンド警告が出るとログが埋まるため。
static PROPS_LOAD_WARNED: std::sync::atomic::AtomicBool =
    std::sync::atomic::AtomicBool::new(false);

/// 草 GPU 再構築のログを出すかどうか（terrain_ops.rs の PERF ゲートと同じ環境変数）。
/// 草描画の間引き計測ログ（frame_renderer）でも参照するため公開する。
pub(super) static PERF_TERRAIN_LOG_ENABLED: std::sync::LazyLock<bool> =
    std::sync::LazyLock::new(|| std::env::var_os("SEED_PERF_TERRAIN").is_some());

/// 散布（草・モデル）のチャンク単位カリングを無効化するデバッグスイッチ。
///
/// `SEED_SCATTER_NOCULL=1` のとき true。true なら視錐台／距離テストを一切行わず、
/// 全チャンクの散布を毎フレーム描く（カリング導入前の挙動と等価）。カリングの
/// before/after を同一バイナリ・同一シーンで fps 比較するための計測用フックであり、
/// 通常実行では設定しない（未設定＝カリング有効）。
pub(super) static SCATTER_CULL_DISABLED: std::sync::LazyLock<bool> =
    std::sync::LazyLock::new(|| std::env::var_os("SEED_SCATTER_NOCULL").is_some());

// ─── 地形チャンク（メッシュ本体）単位カリング（Terrain 描画最適化）─────────────
//
// 【背景】地形は 16×16 チャンク（256 水平 × 高さ層）を「terrain:// の独立 ModelComponent
//   バッチ」として持つ。以前は視界外・背後のチャンクまで毎フレーム全ポリゴン描画しており
//   （オブジェクト単位フラスタムカリングは 00dbe29 で撤去済み・メッシュレットカリングは
//   MULTI_DRAW_INDIRECT_COUNT 非対応 GPU では無効）、地形を置いただけで 30fps を切っていた。
//   そこで各チャンクバッチのワールドメッシュ AABB（`InstancedModelBatch::world_bounds`＝
//   実ジオメトリを厳密に包む）をメインカメラ視錐台＋距離でテストし、完全に外側のチャンクを
//   G-Buffer 描画・メッシュレットカリング前処理からスキップする。判定は散布と同じ
//   `aabb_outside_frustum`（p-vertex 法・偽陽性ゼロ）を使うため、視界内チャンクを誤って消す
//   ことは無い（撤去された旧オブジェクトカリングの誤棄却問題は再発しない）。

/// 地形チャンク単位カリングの距離閾値（メートル・既定）。最近点距離がこれを超えたら描かない。
///
/// 地形は草木より遠くまで見えるべきなので緩めに取る（実質フラスタムカリングが主役で、
/// 距離は「地平線の彼方まで続く巨大ワールド」でのみ効く保険）。16×16 スモーク（≈256m 角）
/// では距離では 1 枚も落ちず、フラスタムのみで効く。`SEED_TERRAIN_CULL_DIST` で上書き可。
const TERRAIN_CHUNK_CULL_DISTANCE_DEFAULT: f32 = 4000.0;

/// 地形チャンク単位カリングの距離閾値（メートル）。`SEED_TERRAIN_CULL_DIST` で上書き可。
pub(super) static TERRAIN_CHUNK_CULL_DISTANCE: std::sync::LazyLock<f32> =
    std::sync::LazyLock::new(|| {
        cull_distance_env("SEED_TERRAIN_CULL_DIST", TERRAIN_CHUNK_CULL_DISTANCE_DEFAULT)
    });

/// 地形チャンク単位カリングを無効化するデバッグスイッチ（before/after の fps 比較計測用）。
///
/// `SEED_TERRAIN_NOCULL=1` のとき true。全地形チャンクを毎フレーム描く（カリング導入前の
/// 挙動と等価）。散布側の `SEED_SCATTER_NOCULL` と対になる計測用フックであり、通常実行では
/// 設定しない（未設定＝カリング有効）。
pub(super) static TERRAIN_CULL_DISABLED: std::sync::LazyLock<bool> =
    std::sync::LazyLock::new(|| std::env::var_os("SEED_TERRAIN_NOCULL").is_some());

/// Hi-Z オクルージョンカリング（地形チャンク）の有効化スイッチ（**既定 OFF・opt-in**）。
///
/// `SEED_OCCLUSION_CULL=1` のとき true。true なら、深度プリパス相当（G-Buffer 深度）から
/// 生成した Hi-Z ピラミッドに各地形チャンクのワールド AABB を投影・比較し、「AABB 全体が
/// 既存オクルーダの背後（＝完全遮蔽）」のチャンクを既存のフラスタム／距離カリング集合へ
/// 追加してスキップする（1 フレーム遅延・GPU→CPU 読み戻し方式）。
///
/// 【既定 OFF の理由】1 フレーム遅延方式は、カメラが動いて新たに見えたチャンクが最大 1
/// フレームだけ描画されない過渡（reveal ホール）を持つ。誤棄却をユーザーが強く嫌うため、
/// まず opt-in で実機検証し、問題なければ既定 ON を検討する。判定自体は保守側（AABB が
/// 少しでも見える／カメラ背後／フラスタム外なら必ず描く）で、静止画では偽陽性ゼロ。
pub(super) static HIZ_OCCLUSION_ENABLED: std::sync::LazyLock<bool> =
    std::sync::LazyLock::new(|| {
        matches!(std::env::var("SEED_OCCLUSION_CULL").as_deref(), Ok("1"))
    });

/// 散布モデル（kind=Model プロップ）の GPU メッシュレットカリングを有効にするか。
/// 既定 ON。環境変数 `SEED_SCATTER_MESHLET=0` で無効化できる（無効時は G-Buffer を
/// 通常のインスタンス描画で焼く＝従来挙動）。近景の高ポリ木の描画コスト計測（before/after）を
/// 同一バイナリで取るためのトグルであり、通常運用では ON のまま使う。
/// 無効化しても描画結果は同じ（メッシュレットカリングは可視部分だけを描くカリングであり、
/// 見た目は変えない）。
pub(super) static SCATTER_MESHLET_CULL_ENABLED: std::sync::LazyLock<bool> =
    std::sync::LazyLock::new(|| {
        !matches!(std::env::var("SEED_SCATTER_MESHLET").as_deref(), Ok("0"))
    });

// ============================================================
//  TerrainScatterField — チャンクマップ上の ScatterField 実装
// ============================================================

/// 実行中の地形チャンクマップの上に `ScatterField` を実装するアダプタ。
///
/// 純粋層（scatter/generate.rs）は `HashMap<ChunkCoord, TerrainChunkData>` を
/// 知らない。本構造体がその橋渡しをすることで、散布アルゴリズムを
/// エンジンから完全に切り離したままにできる。
///
/// 借用のみを持つ（所有しない）ので、生成コストはゼロ。
/// 散布 1 回ごとに作り捨ててよい。
pub(super) struct TerrainScatterField<'a> {
    /// 密度・ペイントを読むチャンクマップ。
    chunks: &'a HashMap<ChunkCoord, TerrainChunkData>,
    /// ボクセルサイズ・チャンク分割・iso_level。
    settings: &'a TerrainSettings,
    /// レイヤ定義（レイヤ名 → 添字の解決とルール重みの評価に使う）。
    layers: &'a TerrainLayerSet,
}

/// `fast_density_at` の局所トライリニアが成立する上限ローカル添字。
///
/// あるサンプル軸のローカル添字 `l = g.rem_euclid(cells)` に対し、トライリニアは
/// `l` と `l+1` の 2 サンプルを読む。`l` が `[0, cells-2]` の範囲にあれば
/// `l+1 <= cells-1` となり、両サンプルとも**同一チャンクの内部**（遠端境界
/// `local == cells` を踏まない）に収まる。このとき `find_owner` は必ずそのチャンクを
/// primary として返すため、局所読みは汎用パス（`sample_density_world`）と
/// **ビット単位で一致**する。`l == cells-1` は上側サンプルが遠端境界に乗り、
/// 隣チャンク優先の解決になりうるので局所パスを使わず汎用へ退避する。
///
/// この定数は「引く量」（cells から幾つ内側までが安全か）を表し、実際の上限は
/// 呼び出し側で `cells - FAST_DENSITY_INTERIOR_MARGIN` として求める。
const FAST_DENSITY_INTERIOR_MARGIN: i32 = 1;

impl<'a> TerrainScatterField<'a> {
    /// チャンクマップ・設定・レイヤ定義からアダプタを作る。
    pub(super) fn new(
        chunks: &'a HashMap<ChunkCoord, TerrainChunkData>,
        settings: &'a TerrainSettings,
        layers: &'a TerrainLayerSet,
    ) -> Self {
        Self { chunks, settings, layers }
    }

    /// `TerrainState` からアダプタを作る便宜コンストラクタ。
    ///
    /// 呼び出し側で 3 つのフィールドを個別に借りると、同じ `terrain` から
    /// 可変借用を取りたい場面（散布結果の書き戻し）と衝突しやすい。
    /// 読み取り専用の借用をここで 1 か所に閉じ込めておく。
    pub(super) fn from_state(terrain: &'a TerrainState) -> Self {
        Self::new(&terrain.chunks, &terrain.settings, &terrain.layers)
    }

    /// 密度のトライリニア補間（`sample_density_world` の高速版）。
    ///
    /// 【なぜ必要か — 計測で判明した支配項】
    ///   ルール散布は 1 チャンクあたり数千の候補柱を上から下へ 0.25m 刻みで
    ///   マーチし、各ステップで密度を 1 回サンプルする。汎用の
    ///   `sample_density_world` は 8 コーナーそれぞれに `find_owner` を呼び、
    ///   `find_owner` は `ChunkCoord` を **SipHash の HashMap** で引く。
    ///   つまり 1 サンプル = 最悪 8 回の SipHash 探索。散布全体では数千万回に
    ///   達し、これが CPU 100% 張り付き・エディタ硬直の実測上の主因だった。
    ///
    /// 【最適化】
    ///   トライリニアの 8 コーナーは連続する 2×2×2 サンプルなので、点が
    ///   チャンク内部（各軸のローカル添字が `[0, cells-1)` の帯）にあれば
    ///   **8 コーナーすべてが単一チャンクに収まる**。その場合は所有チャンクを
    ///   1 回だけ引き、ローカル配列から 8 値を直接読む（SipHash 8→1）。
    ///   チャンク境界（遠端サンプル）や地形外に掛かる点だけ汎用パスへ退避する。
    ///
    /// 【正しさ — ビット単位で汎用パスと一致】
    ///   退避条件は `FAST_DENSITY_INTERIOR_MARGIN` のコメントで証明したとおり、
    ///   局所読みが `find_owner` の primary 解決と一致する範囲に限定している。
    ///   したがって決定性（このモジュールの最重要不変条件）は一切損なわれない。
    #[inline]
    fn fast_density_at(&self, p: [f32; 3]) -> f32 {
        let cells = self.settings.chunk_cells as i32;
        let inv = 1.0 / self.settings.voxel_size;
        let fx = p[0] * inv;
        let fy = p[1] * inv;
        let fz = p[2] * inv;
        let x0 = fx.floor();
        let y0 = fy.floor();
        let z0 = fz.floor();
        let ix = x0 as i32;
        let iy = y0 as i32;
        let iz = z0 as i32;

        // ─── 各軸のローカル添字。内部帯 [0, cells-2] なら局所パスが安全 ───
        let lx = ix.rem_euclid(cells);
        let ly = iy.rem_euclid(cells);
        let lz = iz.rem_euclid(cells);
        let limit = cells - FAST_DENSITY_INTERIOR_MARGIN; // = cells-1
        let interior = lx < limit && ly < limit && lz < limit;

        if interior {
            // ─── 8 コーナーを収める単一チャンクを 1 回だけ引く ───
            let coord = ChunkCoord::new(
                ix.div_euclid(cells),
                iy.div_euclid(cells),
                iz.div_euclid(cells),
            );
            if let Some(chunk) = self.chunks.get(&coord) {
                let (lx, ly, lz) = (lx as usize, ly as usize, lz as usize);
                // ローカル添字 l と l+1（interior 帯なので l+1 <= cells-1 で範囲内）。
                let c000 = chunk.sample(lx, ly, lz);
                let c100 = chunk.sample(lx + 1, ly, lz);
                let c010 = chunk.sample(lx, ly + 1, lz);
                let c110 = chunk.sample(lx + 1, ly + 1, lz);
                let c001 = chunk.sample(lx, ly, lz + 1);
                let c101 = chunk.sample(lx + 1, ly, lz + 1);
                let c011 = chunk.sample(lx, ly + 1, lz + 1);
                let c111 = chunk.sample(lx + 1, ly + 1, lz + 1);
                // 補間係数（汎用パスと同一の順序・式）。
                let tx = fx - x0;
                let ty = fy - y0;
                let tz = fz - z0;
                let c00 = c000 + (c100 - c000) * tx;
                let c10 = c010 + (c110 - c010) * tx;
                let c01 = c001 + (c101 - c001) * tx;
                let c11 = c011 + (c111 - c011) * tx;
                let c0 = c00 + (c10 - c00) * ty;
                let c1 = c01 + (c11 - c01) * ty;
                return c0 + (c1 - c0) * tz;
            }
            // base チャンクが無い＝地形外。汎用パスも clamp を返すので退避する。
        }

        // ─── 境界／地形外は汎用パスへ（find_owner の境界解決に委ねる）───
        sample_density_world(self.chunks, self.settings, p)
    }

    /// ワールド座標 `p` における密度場の外向き単位法線（中心差分）。
    ///
    /// 密度は「外へ行くほど増える」規約なので、勾配 ∇density が
    /// そのまま外向きを指す（符号反転しない）。これは
    /// marching_cubes.rs の `gradient_normal` および
    /// scatter/generate.rs の `surface_normal` と同一の規約である。
    fn normal_at(&self, p: [f32; 3]) -> [f32; 3] {
        let h = NORMAL_GRADIENT_EPS_FRACTION * self.settings.voxel_size;

        // ─── 各軸の偏微分（中心差分）───
        let dx = self.density_at([p[0] + h, p[1], p[2]]) - self.density_at([p[0] - h, p[1], p[2]]);
        let dy = self.density_at([p[0], p[1] + h, p[2]]) - self.density_at([p[0], p[1] - h, p[2]]);
        let dz = self.density_at([p[0], p[1], p[2] + h]) - self.density_at([p[0], p[1], p[2] - h]);

        // ─── 正規化。勾配が縮退していたら真上を向く ───
        let len_sq = dx * dx + dy * dy + dz * dz;
        if len_sq <= NORMAL_GRADIENT_EPSILON {
            return NORMAL_FALLBACK_UP;
        }
        let inv = 1.0 / len_sq.sqrt();
        [dx * inv, dy * inv, dz * inv]
    }
}

impl ScatterField for TerrainScatterField<'_> {
    /// 地形設定をそのまま返す。
    fn settings(&self) -> &TerrainSettings {
        self.settings
    }

    /// ワールド座標の密度（トライリニア補間）。
    ///
    /// terrain_ops.rs の `sample_density_world` をそのまま使う。
    /// レイマーチ（ブラシの着弾判定）と散布の接地判定で別実装を持つと、
    /// 「ブラシは当たったのに草が生えない」というずれが出るため、
    /// 意図的に同一関数を共有している。
    /// 地形外は `density_clamp`（＝ AIR 相当）が返るので、未生成領域の
    /// 境界に草が生えることはない。
    fn density_at(&self, p: [f32; 3]) -> f32 {
        // 局所トライリニアの高速版（境界・地形外は sample_density_world へ退避）。
        // 結果は sample_density_world とビット単位で一致する（決定性を保つ）。
        self.fast_density_at(p)
    }

    /// ワールド座標 `p` におけるレイヤ名 → 重み（0..1）。
    ///
    /// 【計算式は terrain_mesh_build.rs の `compute_layer_colors` と一致させてある】
    ///   1. 密度勾配から地表法線を求める
    ///   2. `TerrainLayerSet::rule_weights_all(normal.y, world_y)` でルール重みを得る
    ///   3. 最近傍サンプルの `BlendSlots` と `paint_amount` を読む
    ///   4. `blend_rule_and_paint_all` でルールと手ペイントを合成する
    ///   得られる密重みベクトルは `compute_layer_colors` の `dense[]` と同じ値である。
    ///
    /// 【なぜパレット射影（上位 4 層への正規化）まで真似ないのか】
    ///   パレット射影はチャンク全体の重み合計に依存する「描画上の都合」であり、
    ///   同じ地点でも隣のチャンクを編集すると値が変わりうる。散布ルールが
    ///   それに引きずられると、無関係な場所を彫っただけで草の生え方が変わって
    ///   しまう。射影前の `dense[]` こそが「その地点に実際に塗られている量」
    ///   であり、散布判定にはこちらが正しい。
    ///
    /// 【未知のレイヤ名】
    ///   layers.json に存在しないレイヤ名を props.json が参照していた場合は
    ///   0.0 を返す（＝そのレイヤ条件は不成立になり、プロップは生えない）。
    ///   タイポで草が消えるのは分かりやすい失敗であり、逆に 1.0 を返して
    ///   「条件を書いたのに全面に生える」ほうが発見しにくいと判断した。
    fn layer_weight_at(&self, p: [f32; 3], layer: &str) -> f32 {
        // ─── ① レイヤ名を添字へ解決する（未知なら即 0）───
        let Some(layer_index) = self.layers.layers.iter().position(|l| l.name == layer) else {
            return 0.0;
        };

        // ─── ② 地表法線 → ルール重み（斜度・高度による自動下地）───
        let normal = self.normal_at(p);
        let rule_w = self.layers.rule_weights_all(normal[1], p[1]);

        // ─── ③ 最近傍サンプルの手ペイント情報を読む ───
        //   ペイントは頂点ではなくサンプル格子に載っているので、
        //   p を最寄りのグローバルサンプル座標へ丸めて所有チャンクを引く。
        let inv = 1.0 / self.settings.voxel_size;
        let gx = (p[0] * inv).round() as i32;
        let gy = (p[1] * inv).round() as i32;
        let gz = (p[2] * inv).round() as i32;
        let cells = self.settings.chunk_cells as i32;

        let (paint_slots, paint_amount) = match find_owner(self.chunks, cells, gx, gy, gz) {
            Some((chunk, lx, ly, lz)) => {
                (chunk.paint_slots(lx, ly, lz), chunk.paint_amount(lx, ly, lz))
            }
            // 地形外にはペイントが存在しない＝ルール重みがそのまま通る。
            None => (Default::default(), 0.0),
        };

        // ─── ④ ルールと手ペイントを合成する（compute_layer_colors と同じ関数）───
        let dense = blend_rule_and_paint_all(&rule_w, &paint_slots, paint_amount);
        dense.get(layer_index).copied().unwrap_or(0.0)
    }
}

// ============================================================
//  自由関数ヘルパ
// ============================================================

/// ワールド座標を含むチャンクの格子座標を返す。
///
/// 【境界の扱い（重要）】
///   チャンク座標 c は `[c*extent, (c+1)*extent)` を占める（上端は開区間）。
///   `floor()` はこの規約をそのまま実装しており、負座標でも正しく動く
///   （例 extent=16 のとき -0.001 → floor(-0.0000625) = -1）。
///   ここを `as i32`（0 方向への切り捨て）で書くと -0.001 が 0 になり、
///   x<0 の一列ぶんの草が隣のチャンクへ紛れて継ぎ目で消える。
///   単体テスト `owning_chunk_handles_negative_and_boundary` が固定している。
pub(super) fn owning_chunk_coord(settings: &TerrainSettings, pos: [f32; 3]) -> ChunkCoord {
    let extent = settings.chunk_extent();
    ChunkCoord::new(
        (pos[0] / extent).floor() as i32,
        (pos[1] / extent).floor() as i32,
        (pos[2] / extent).floor() as i32,
    )
}

/// チャンク格子座標から、そのチャンクを包むワールド AABB を返す（カリング判定範囲）。
///
/// チャンク c は各軸 `[c*extent, (c+1)*extent)` を占める。散布インスタンスの原点は
/// この範囲内だが、見た目は草丈・樹高で上方へ、樹冠・葉先で側方へはみ出す。そこで
/// **水平（X/Z）は `margin_h`・下方向も `margin_h`・上方向（+Y）だけ `margin_up`**
/// でふくらませる。水平を小さく保つことで AABB がチャンク寸法の数倍に膨れてカリングが
/// 効かなくなるのを防ぎ（コメント上部参照）、縦は樹高ぶん確保して樹冠の pop を防ぐ。
fn chunk_world_aabb(
    settings: &TerrainSettings,
    coord: ChunkCoord,
    margin_h: f32,
    margin_up: f32,
) -> ([f32; 3], [f32; 3]) {
    let e = settings.chunk_extent();
    let min = [
        coord.x as f32 * e - margin_h,
        coord.y as f32 * e - margin_h,
        coord.z as f32 * e - margin_h,
    ];
    let max = [
        (coord.x + 1) as f32 * e + margin_h,
        (coord.y + 1) as f32 * e + margin_up,
        (coord.z + 1) as f32 * e + margin_h,
    ];
    (min, max)
}

/// 散布インスタンスを草 GPU インスタンスへ変換する。
///
/// `prop_id` は GPU 側では使わない（プロップ種別ごとに別バッファ・別 uniform を
/// 持つため、バッファに入った時点でどのプロップかは確定している）。
pub(super) fn scatter_instance_to_gpu(inst: &ScatterInstance) -> GrassInstanceGpu {
    GrassInstanceGpu {
        pos:    inst.pos,
        yaw:    inst.yaw,
        normal: inst.normal,
        scale:  inst.scale,
        seed:   inst.seed,
        _pad:   [0; 3],
    }
}

/// プロップ定義から草の GPU uniform を組み立てる。
///
/// `time` は 0 で初期化する（毎フレーム `update_time` が上書きするため）。
/// `segments` は `clamped_segments()` 経由で取る（props.json に 0 や 1000 が
/// 書かれても頂点バッファが破綻しないようにするための規約）。
pub(super) fn grass_uniform_from_prop(prop: &TerrainProp) -> GrassUniformGpu {
    let g = &prop.grass;
    let w = &prop.wind;
    GrassUniformGpu {
        color_bottom: g.color_bottom,
        width:        g.width,
        color_top:    g.color_top,
        height:       g.height,

        wind_strength:  w.strength,
        wind_speed:     w.speed,
        wind_frequency: w.frequency,
        gust_strength:  w.gust_strength,
        gust_speed:     w.gust_speed,
        time:           0.0,
        bend:           g.bend,
        roughness:      g.roughness,

        segments:         g.clamped_segments(),
        // WGSL 側は 1 or 2 の枚数として読むので bool を枚数へ展開する。
        cross_planes:     if g.cross_planes { 2 } else { 1 },
        tip_alpha_cutoff: g.tip_alpha_cutoff,
        // 法線の地表寄せ量。不正値（NaN・範囲外）が props.json に書かれても
        // シェーダ側の mix が壊れないよう、ここで 0..1 へ丸めておく。
        normal_up_blend:  g.normal_up_blend.clamp(0.0, 1.0),
    }
}

// ============================================================
//  散布モデル（kind=Model）— GPU リソースと姿勢行列
// ============================================================

/// kind=Model 散布プロップ 1 種ぶんの GPU 描画リソース。
///
/// 草（`GrassInstanceBuffer`）と違い、model は実アセット（glTF/obj）をロードして
/// **通常のメッシュ G-Buffer パイプライン**でインスタンス描画する。GpuModel は
/// ECS アクターに紐付かず本構造体が所有する（散布が変わるまで保持され続け、
/// frame_renderer の 60 フレーム stale prune の対象外）。
///
/// 【なぜ CPU モデルも持つのか】
///   `InstancedModelBatch::update` はノード階層を毎回展開してワールド行列を組むため
///   CPU 側の `Model`（`Arc` 共有）を必要とする（統合バッチと同じ制約）。
pub(crate) struct ScatterModelResource {
    /// このリソースをロードした `model_path`（props リロードでの差し替え検出に使う）。
    pub model_path: String,
    /// CPU モデル（`InstancedModelBatch::update` がノード階層展開に必要）。
    pub cpu_model: std::sync::Arc<Model>,
    /// GPU モデル（頂点/インデックス/マテリアル/テクスチャ）。本構造体が所有する。
    pub gpu_model: GpuModel,
    /// インスタンス行列を供給する統合バッチ（草の `GrassInstanceBuffer` 相当）。
    pub batch: InstancedModelBatch,
    /// `batch` に確保済みのインスタンス容量（不足したら作り直す）。
    pub capacity: usize,
    /// チャンク単位カリング用の「チャンクごとのワールド行列＋AABB」。
    ///
    /// `rebuild_scatter_models_gpu`（散布が変わったときだけ）で構築し、毎フレームの
    /// `cull_and_update_scatter_models` が視錐台＋距離で可視チャンクを選び、その行列
    /// だけを `batch.update` へ流す。これによりバッチには**可視インスタンスだけ**が
    /// 載り、G-Buffer パスもシャドウパスも可視ぶんだけを描く。
    pub chunk_spans: Vec<ScatterModelChunkSpan>,
    /// 毎フレームの `batch.update` を入力不変フレームで省くためのダーティゲート。
    ///
    /// 散布モデルは静的なので、可視チャンクの連結結果（`visible`）と距離 LOD の
    /// 振り分けが前フレームと完全一致すれば、`update` の出力は 1 ビットも変わらない。
    /// 省略できると CPU 時間が浮くだけでなく、バッチの内容世代（`content_generation`）が
    /// 据え置かれるためシャドウ深度パスの静的カスケードスキップも成立するようになる。
    pub merge_gate: super::merge_batch_gate::MergeBatchGate,
}

/// 散布モデル 1 チャンク分の「ワールド行列列＋ワールド AABB」。
///
/// チャンク単位カリング（`cull_and_update_scatter_models`）の判定単位。行列は
/// `scatter_instance_to_model_matrix` で事前計算済みで、可視ならそのまま `batch.update`
/// へ連結する（毎フレームの再計算は視錐台テストだけで、行列は使い回す）。
pub struct ScatterModelChunkSpan {
    /// このチャンクの木を包むワールド AABB 下端（樹高マージン込み）。
    pub aabb_min: [f32; 3],
    /// このチャンクの木を包むワールド AABB 上端（樹高マージン込み）。
    pub aabb_max: [f32; 3],
    /// このチャンクに属するインスタンスのワールド行列（4x4・行優先）。
    pub mats: Vec<[[f32; 4]; 4]>,
}

/// 3 次元外積。
#[inline]
fn cross3(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]
}

/// 正規化。長さが縮退していたら `fallback` を返す。
#[inline]
fn normalize_or(v: [f32; 3], fallback: [f32; 3]) -> [f32; 3] {
    let len_sq = v[0] * v[0] + v[1] * v[1] + v[2] * v[2];
    if len_sq <= MODEL_NORMALIZE_EPSILON {
        return fallback;
    }
    let inv = 1.0 / len_sq.sqrt();
    [v[0] * inv, v[1] * inv, v[2] * inv]
}

/// 散布インスタンス 1 件を、通常メッシュ描画用のワールド行列（4x4）へ変換する。
///
/// 【姿勢の組み立て】
///   `ScatterInstance` は接地点 `pos`・上方向 `normal`（tilt 適用済みの単位ベクトル）・
///   `yaw`（up まわりの回転）・一様 `scale` を持つ。これを
///     ワールド = T(pos) · R(up=normal, yaw) · S(scale)
///   の 4x4 行列へ組む。モデルのローカル +Y を `normal` へ向け、その軸まわりに
///   `yaw` だけ回し、全軸へ `scale` を掛ける（草シェーダが normal+yaw から板を
///   立てるのと同じ姿勢規約を、CPU 行列で再現している）。
///
/// 【行列レイアウト】
///   `Transform::to_mat4` と同一規約（row-major・平行移動は各行の第 4 要素、
///   列 j がローカル基底ベクトル j のワールド像）。この規約でないと
///   `InstancedModelBatch::update`／頂点シェーダの解釈とズレて、モデルが
///   転置された姿勢で描かれる。
pub(super) fn scatter_instance_to_model_matrix(inst: &ScatterInstance) -> [[f32; 4]; 4] {
    // ── 上方向（正規化。縮退時は真上）──
    let up = normalize_or(inst.normal, NORMAL_FALLBACK_UP);

    // ── up に直交する安定な基底を作る（up が Y と平行に近ければ X を参照軸に）──
    //   平行なベクトル同士の外積は 0 ベクトルになり基底が作れないため参照軸を切り替える。
    let reference = if up[1].abs() > MODEL_BASIS_PARALLEL_THRESHOLD {
        [1.0, 0.0, 0.0]
    } else {
        [0.0, 1.0, 0.0]
    };
    let right0 = normalize_or(cross3(reference, up), [1.0, 0.0, 0.0]);
    // right0 も up も単位長で互いに直交するので、その外積は正規化不要で単位長。
    // 右手系（列 X × 列 Y = 列 Z ⇒ right × up = fwd）にするため cross(right0, up) を取る。
    // cross(up, right0) だと符号が反転して鏡映（左手系）になる。
    let fwd0 = cross3(right0, up);

    // ── yaw を up まわりに適用（right/fwd を回す）──
    let (sy, cy) = inst.yaw.sin_cos();
    let right = [
        right0[0] * cy + fwd0[0] * sy,
        right0[1] * cy + fwd0[1] * sy,
        right0[2] * cy + fwd0[2] * sy,
    ];
    let fwd = [
        -right0[0] * sy + fwd0[0] * cy,
        -right0[1] * sy + fwd0[1] * cy,
        -right0[2] * sy + fwd0[2] * cy,
    ];

    let s = inst.scale;
    let p = inst.pos;
    // 列0=right・列1=up・列2=fwd（各 scale 倍）、列3=平行移動。
    // [row][col] 表記で translation は各行の col=3 に入る（Transform::to_mat4 と同一）。
    [
        [right[0] * s, up[0] * s, fwd[0] * s, p[0]],
        [right[1] * s, up[1] * s, fwd[1] * s, p[1]],
        [right[2] * s, up[2] * s, fwd[2] * s, p[2]],
        [0.0, 0.0, 0.0, 1.0],
    ]
}

/// 全チャンクの散布インスタンスから、kind=Model プロップぶんのワールド行列を
/// **プロップ添字ごと・チャンクごと**に束ねる（チャンク単位カリングの入力）。
///
/// 対象になるのは `kind == Model` かつ `model_path` が非空のプロップだけ。
/// 草・孤児（props.json から消えた prop_id）・model_path 未設定は除外する。
///
/// 各チャンク span はそのチャンクのワールド AABB（樹高マージン込み）と、属する
/// インスタンスの事前計算ワールド行列を持つ。チャンク座標順にソートして返すため、
/// 毎フレームのカリング結果（可視 span 連結）は決定的になる。
fn gather_scatter_model_chunks(
    scatter: &HashMap<ChunkCoord, Vec<ScatterInstance>>,
    props: &TerrainPropSet,
    settings: &TerrainSettings,
) -> HashMap<usize, Vec<ScatterModelChunkSpan>> {
    // まず (prop, chunk) ごとに (間引きキー, 行列) を集める。
    //   キーはあとでチャンク内をハッシュ順へ並べ替えるために持つ（遠景密度減衰で
    //   「先頭 kept 本」を均一なサブセットにするため。scatter_thin_key 参照）。
    let mut per_prop_chunk: HashMap<usize, HashMap<ChunkCoord, Vec<(u32, [[f32; 4]; 4])>>> =
        HashMap::new();
    for (&coord, instances) in scatter {
        for inst in instances {
            let prop_index = inst.prop_id as usize;
            let Some(prop) = props.props.get(prop_index) else {
                // props.json から消えた孤児インスタンス。描かない。
                continue;
            };
            if prop.kind != PropKind::Model {
                // 草は rebuild_grass_gpu が担当する。
                continue;
            }
            // model_path 未設定のプロップは描けない（データはあるが実体が無い）。
            if prop.model_path.as_deref().unwrap_or("").is_empty() {
                continue;
            }
            per_prop_chunk
                .entry(prop_index)
                .or_default()
                .entry(coord)
                .or_default()
                .push((scatter_thin_key(inst.seed), scatter_instance_to_model_matrix(inst)));
        }
    }

    // チャンク座標順にソートして span 列へ変換する（決定的な描画順のため）。
    let mut by_prop: HashMap<usize, Vec<ScatterModelChunkSpan>> = HashMap::new();
    for (prop_index, chunks) in per_prop_chunk {
        let mut coords: Vec<ChunkCoord> = chunks.keys().copied().collect();
        coords.sort_by_key(|c| (c.x, c.y, c.z));
        let spans = coords
            .into_iter()
            .map(|coord| {
                let (aabb_min, aabb_max) = chunk_world_aabb(
                    settings, coord, SCATTER_MODEL_MARGIN_HORIZ, SCATTER_MODEL_MARGIN_UP,
                );
                // チャンク内をハッシュキー順へ並べ替え、行列だけを取り出す。
                //   これで span.mats の任意プレフィクスが空間的に均一なサブセットになり、
                //   密度減衰（先頭 kept 本だけ描く）で穴が空かない。第2キーに行列先頭要素を
                //   置き、同一 seed でも並びが決定的になるようにする（ちらつき防止）。
                let mut keyed = chunks.get(&coord).cloned().unwrap_or_default();
                keyed.sort_by(|a, b| {
                    a.0.cmp(&b.0)
                        .then(a.1[0][3].total_cmp(&b.1[0][3]))
                        .then(a.1[2][3].total_cmp(&b.1[2][3]))
                });
                let mats = keyed.into_iter().map(|(_, m)| m).collect();
                ScatterModelChunkSpan { aabb_min, aabb_max, mats }
            })
            .collect();
        by_prop.insert(prop_index, spans);
    }
    by_prop
}

/// `model_path`（assets 相対 / 仮想 / 絶対）から CPU+GPU モデルをロードする。
///
/// 既存のモデルロードと同じ規約でパスを解決する（相対→`assets://`→実パス）。
/// 失敗（パス解決不可・パース失敗）は `Err(理由文字列)` を返し、呼び出し側が
/// **1 回だけ**警告してそのプロップをスキップする。
fn load_scatter_model(
    ctx: &DrawContext,
    model_path: &str,
) -> Result<(std::sync::Arc<Model>, GpuModel), String> {
    // アセット相対 → 仮想パス → 実パス（ギズモ／ECS モデルと同じ解決規約）。
    let virtual_path = crate::engine::asset_fs::normalize_asset_path(model_path);
    let abs = crate::engine::asset_fs::resolve(&virtual_path);
    let model = load_model(&abs)
        .map_err(|e| format!("{e:?} (resolved: {})", abs.display()))?;
    // GpuModel は DrawContext が device/queue/pipelines/defaults を保持しているので
    // モデルだけ渡せば構築できる（ECS の ModelComponent ロードと同じ入口）。
    let gpu_model = ctx.upload_model(&model);
    Ok((std::sync::Arc::new(model), gpu_model))
}

/// チャンクの .tscatter ファイル名（`chunk_X_Y_Z.tscatter`）を返す。
///
/// terrain_ops.rs の `tvox_file_name` と同じ命名規則にしてある
/// （同じディレクトリに拡張子違いで隣り合うため）。
pub(super) fn tscatter_file_name(coord: ChunkCoord) -> String {
    use crate::engine::terrain::dir_ref;
    format!("{}{}", dir_ref::chunk_stem(coord), dir_ref::TSCATTER_EXT)
}

/// チャンクの .tscatter 仮想パス（`assets://terrain/<scene>/chunk_X_Y_Z.tscatter`）。
///
/// 【現状どこからも呼ばれていない件】
///   保存は `std::fs` の実パス（`tscatter_file_name`）、読み込みは .tvox パスからの
///   拡張子差し替え（`tscatter_path_from_tvox`）で足りているため、第1段では
///   出番が無い。それでも残すのは `tvox_virtual_path` と対になる API であり、
///   エディタへ散布ファイルの所在を通知する段（T3 第2段）で必要になるためである。
///   テストでは命名規則の固定に使っている。
#[allow(dead_code)]
pub(super) fn tscatter_virtual_path(scene: &str, coord: ChunkCoord) -> String {
    format!(
        "{}terrain/{}/{}",
        crate::engine::asset_fs::ASSETS_SCHEME,
        scene,
        tscatter_file_name(coord)
    )
}

/// .tvox の仮想パスから、隣に置かれた .tscatter の仮想パスを導く。
///
/// ロード時は `TerrainChunkComponent::tvox_path` しか手掛かりが無い
/// （シーン名を別途組み立てるとパス生成の規則が 2 か所に分かれて壊れやすい）。
/// 拡張子だけを差し替えることで、tvox 側のパス規則に自動で追従する。
pub(super) fn tscatter_path_from_tvox(tvox_path: &str) -> String {
    crate::engine::terrain::dir_ref::sibling_path(
        tvox_path,
        crate::engine::terrain::dir_ref::TSCATTER_EXT,
    )
}


// ============================================================
//  App — 散布のエンジン統合
// ============================================================

impl App {
    // ─── プロップ定義の読み込み ─────────────────────────────────────────────

    /// props.json を読み込んで `terrain.props` へ格納する。
    ///
    /// 読み込み元は環境変数 `SEED_TERRAIN_PROPS` > `assets://terrain/props.json`
    /// の順で解決する（layers.json の `ensure_terrain_layers` と完全に同じ流儀）。
    /// 何らかの理由で読めなければ `TerrainPropSet::default()` へフォールバックし、
    /// 警告は 1 回だけ出す（props.json が無い環境でログを埋めないため）。
    pub(super) fn ensure_terrain_props(&mut self) {
        let source = std::env::var(TERRAIN_PROPS_PATH_ENV)
            .ok()
            .filter(|p| !p.is_empty())
            .unwrap_or_else(|| TERRAIN_PROPS_ASSET.to_string());

        let set = match crate::engine::asset_fs::read_string(&source) {
            Ok(text) => match TerrainPropSet::from_json_str(&text) {
                Ok(set) => set,
                Err(e) => {
                    warn_props_once(&format!(
                        "[SEED terrain] props.json parse failed ({e}); 既定プロップセットで続行します"
                    ));
                    TerrainPropSet::default()
                }
            },
            Err(_) => {
                warn_props_once(&format!(
                    "[SEED terrain] {source} が見つかりません; 既定プロップセット（草1種+木1種）を使用します"
                ));
                TerrainPropSet::default()
            }
        };

        self.terrain.props = set;
    }

    /// prop_id 文字列を散布対象のプロップ添字リストへ解決する。
    ///
    /// 空文字（`SCATTER_ALL_PROPS`）は「全プロップ」を意味する。
    /// 未知の ID は空リストを返す（呼び出し側が 0 件として扱う）。
    fn resolve_scatter_prop_indices(&self, prop_id: &str) -> Vec<usize> {
        if prop_id == SCATTER_ALL_PROPS {
            return (0..self.terrain.props.active_count()).collect();
        }
        match self.terrain.props.find_by_id(prop_id) {
            Some((index, _)) => vec![index],
            None => Vec::new(),
        }
    }

    // ─── ルール自動散布 ─────────────────────────────────────────────────────

    /// `TERRAIN_SCATTER_RULES` の実処理。全チャンクをルールで散布し直す。
    ///
    /// 【既存インスタンスの扱い】
    ///   対象プロップのインスタンスだけを全チャンクから取り除いてから
    ///   生成し直す（他プロップとブラシ散布ぶんは温存する）。
    ///   ブラシで描いた草も対象プロップなら消える点は仕様である
    ///   ——「ルールで敷き直す」とは自動生成の結果で置き換えることであり、
    ///   手描きだけを見分けて残す情報をインスタンスは持っていない。
    ///
    /// 【決定性】
    ///   同じ (seed, 地形, props.json) なら必ず同じ結果になる。
    ///   seed をシーンに保存しておけば、未保存チャンクを実行時に再生成しても
    ///   保存済みチャンクと継ぎ目なく繋がる。
    pub(super) fn handle_terrain_scatter_rules(&mut self, prop_id: String, seed: u64) {
        // ─── props.json を毎回読み直してから撒く（最新の編集を必ず反映する）───
        //   ここを「未読のときだけ」にすると、一度メモリへ載った古い props を
        //   Edit モードでは二度と更新できず、再散布しても草丈・幅・色が変わらない。
        //   Play はシーン再ロードで `ensure_terrain_props` を無条件に通るため反映され、
        //   この Edit/Play 差がまさに「Edit だと常に短い草のまま」というバグの根因だった。
        //   エディタは「保存して適用」で props.json をディスクへ書いてから再散布を送るので、
        //   ここで無条件に読み直せば Edit でも最新定義で撒ける。読み直しは小さな JSON の
        //   パースだけで安く、Play 側の挙動と一致させる意味でも常時行う。
        self.ensure_terrain_props();

        let prop_indices = self.resolve_scatter_prop_indices(&prop_id);
        if prop_indices.is_empty() {
            if let Some(ipc) = &self.ipc {
                ipc.send(&format!("TERRAIN_SCATTER_ERROR:unknown prop_id '{prop_id}'"));
            }
            return;
        }

        self.terrain.scatter_seed = seed;
        // 生成時間の計測開始（SEED_PERF_TERRAIN 有効時のみログ出力）。
        let t_gen_start = std::time::Instant::now();

        // ─── 全チャンクを走査して散布し直す ───
        //   ScatterField は terrain を不変借用するため、書き戻しは
        //   走査後にまとめて行う（借用の衝突を避ける）。
        //   チャンク走査順を固定するため、座標をソートしてから並列化する
        //   （HashMap のキー順は実行ごとに変わるため、そのままでは
        //     「書き戻し順」が非決定的になり、同一チャンク内のインスタンス
        //     並びがブラシ散布ぶんと混ざったときに再現性を失う）。
        let mut coords: Vec<ChunkCoord> = self.terrain.chunks.keys().copied().collect();
        coords.sort_by_key(|c| (c.x, c.y, c.z));

        // ─── チャンクごとの生成は並列化する ───
        //   `scatter_chunk_by_rules` はチャンク座標とシードだけから乱数列を
        //   導出する（セルごとに独立ストリーム）ため、実行順・スレッド数に
        //   関わらずビット単位で同じ結果になる。48 チャンク×3 プロップの
        //   実測で 34 秒かかっており、エディタ操作としては固まって見えるため
        //   ここを並列化する。`map().collect()` は入力順を保つので、
        //   書き戻し順は上でソートした座標順のまま決定的である。
        let generated: Vec<(ChunkCoord, Vec<ScatterInstance>)> = {
            let field = TerrainScatterField::from_state(&self.terrain);
            coords
                .par_iter()
                .map(|&coord| {
                    let fresh = scatter_chunk_by_rules(
                        &field,
                        &self.terrain.props,
                        &prop_indices,
                        coord,
                        seed,
                    );
                    (coord, fresh)
                })
                .collect()
        };

        // ─── 生成の内訳を計測（並列生成が支配項か切り分けるため）───
        let gen_ms = t_gen_start.elapsed().as_secs_f64() * MILLIS_PER_SEC;
        let generated_count: usize = generated.iter().map(|(_, v)| v.len()).sum();
        let t_writeback_start = std::time::Instant::now();

        // ─── kind=Actor プロップのインスタンスを横取りしてアクタ生成へ回す ───
        //   アクタ散布は .tscatter / GPU 描画に載せず、シーンの実アクタとして
        //   永続化する（terrain_scatter_actor_ops.rs）。ルール散布なので
        //   既存生成アクタを全消しして敷き直す（replace = true）。
        //   prop_indices を渡すことで、対象の kind=Actor プロップは生成 0 件でも
        //   「敷き直し」（全消しのみ）の対象に含まれる。
        //   プレハブは編集されている可能性があるため、キャッシュを捨てて読み直す
        //   （props.json を毎回読み直すのと同じ理由）。
        self.scatter_prefab_cache.clear();
        let mut generated = generated;
        let actor_instances = {
            let mut lists: Vec<&mut Vec<ScatterInstance>> =
                generated.iter_mut().map(|(_, v)| v).collect();
            self.extract_actor_scatter_instances(&mut lists, &prop_indices)
        };
        let spawned_actors = self.apply_actor_scatter(actor_instances, true);

        // ─── 書き戻し: 対象プロップの旧インスタンスを捨てて新しいものを足す ───
        // total は「.tscatter に載る草・モデルのインスタンス総数」。
        // 生成した散布アクタ数（spawned_actors）とは単位が違うため別に持ち、
        // エディタへの OK 通知でのみ合算する（表示上の総配置数として）。
        let mut total = 0usize;
        for (coord, fresh) in generated {
            let slot = self.terrain.scatter.entry(coord).or_default();
            // 対象プロップぶんだけを取り除く（他プロップは温存）。
            slot.retain(|inst| !prop_indices.contains(&(inst.prop_id as usize)));
            slot.extend(fresh);
            total += slot.len();
            self.terrain.scatter_dirty.insert(coord);
        }
        self.terrain.grass_gpu_dirty = true;

        // ─── 計測ログ（生成 vs 書き戻し。GPU 構築は rebuild_grass_gpu 側で別途ログ）───
        if *PERF_TERRAIN_LOG_ENABLED {
            let writeback_ms = t_writeback_start.elapsed().as_secs_f64() * MILLIS_PER_SEC;
            let chunk_count = coords.len();
            eprintln!(
                "[PERF terrain] scatter rules: gen={gen_ms:.1}ms writeback={writeback_ms:.1}ms \
                 chunks={chunk_count} props={} generated={generated_count} total_after={total} \
                 spawned_actors={spawned_actors}",
                prop_indices.len()
            );
        }

        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("TERRAIN_SCATTER_OK:{}", total + spawned_actors));
        }
    }

    // ─── ブラシ散布 ─────────────────────────────────────────────────────────

    /// `TERRAIN_SCATTER_BRUSH` の実処理。画面座標から地形へレイを飛ばして散布する。
    ///
    /// 着弾点の求め方は密度ブラシ（`handle_terrain_brush`）と完全に同じ
    /// `terrain_raymarch_hit` を使う。別実装にするとブラシプレビューの球と
    /// 実際に草が生える位置がずれるため。
    pub(super) fn handle_terrain_scatter_brush(
        &mut self,
        prop_id: String,
        screen_x: f32,
        screen_y: f32,
        radius: f32,
        density: f32,
        erase: bool,
    ) {
        if self.terrain.props.props.is_empty() {
            self.ensure_terrain_props();
        }
        if self.terrain.chunks.is_empty() {
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_SCATTER_BRUSH_MISS");
            }
            return;
        }

        let Some(center) = self.terrain_raymarch_hit(screen_x, screen_y) else {
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_SCATTER_BRUSH_MISS");
            }
            return;
        };

        // ─── 消去はプロップ添字を問わないので、未知 ID でも通す ───
        //   （scatter_brush の消去は半径内の全プロップを消す意味論のため）。
        let prop_index = match self.terrain.props.find_by_id(&prop_id) {
            Some((index, _)) => index,
            None if erase => 0,
            None => {
                if let Some(ipc) = &self.ipc {
                    ipc.send(&format!("TERRAIN_SCATTER_ERROR:unknown prop_id '{prop_id}'"));
                }
                return;
            }
        };
        self.terrain.scatter_prop = prop_index;

        self.handle_terrain_scatter_brush_world(prop_index, center, radius, density, erase);

        if let Some(ipc) = &self.ipc {
            ipc.send(&format!(
                "TERRAIN_SCATTER_BRUSH_OK:{},{},{}",
                center[0], center[1], center[2]
            ));
        }
    }

    /// ワールド座標を直接指定するブラシ散布（スモークテストと IPC の共通実体）。
    ///
    /// 【チャンクを跨ぐブラシの扱い】
    ///   ブラシは容易にチャンク境界を跨ぐ。インスタンスは必ず
    ///   **自分の XZ 位置を所有するチャンク** へ格納しなければならない
    ///   （そうしないと保存時に別チャンクの .tscatter へ書かれ、
    ///    そのチャンクだけを読み直したときに草が消える／二重に出る）。
    ///   そこで一旦フラットな作業配列で散布し、位置に基づいて仕分け直す。
    pub(super) fn handle_terrain_scatter_brush_world(
        &mut self,
        prop_index: usize,
        center: [f32; 3],
        radius: f32,
        density: f32,
        erase: bool,
    ) {
        let settings = self.terrain.settings.clone();

        // ─── ① ブラシ球が触れうるチャンクを列挙する ───
        let touched = chunks_in_sphere(&settings, center, radius);

        // ─── ② 対象チャンクのインスタンスを 1 本の作業配列へ集める ───
        //   `scatter_brush` は重なり判定を配列全体に対して行うため、
        //   チャンクごとに別々に呼ぶと境界付近で重複が防げない。
        let mut work: Vec<ScatterInstance> = Vec::new();
        for coord in &touched {
            if let Some(list) = self.terrain.scatter.remove(coord) {
                work.extend(list);
            }
        }
        let before_len = work.len();

        // ─── ③ 散布／消去（純粋層へ委譲）───
        let changed = {
            let field = TerrainScatterField::from_state(&self.terrain);
            scatter_brush(
                &field,
                &self.terrain.props,
                prop_index,
                &mut work,
                center,
                radius,
                density,
                erase,
                self.terrain.scatter_seed,
            )
        };

        // ─── ③′ kind=Actor プロップのぶんを横取りしてアクタ生成へ回す ───
        //   ブラシ経路は「追加」なので replace = false（既存生成アクタは温存）。
        //   消去ブラシは半径内の全散布アクタを削除する（草・モデルの
        //   「半径内の全プロップを消す」意味論と同じ）。
        let mut work = work;
        {
            // work はチャンク仕分け前のフラットな作業配列（既存＋新規が混在）。
            // 既存に kind=Actor は構造的に含まれない（.tscatter へ保存しないため）ので、
            // ここで抜けるのはこのブラシで新規に撒かれたぶんだけである。
            // ブラシは「追加」なので敷き直し対象（第 2 引数）は無し。
            let mut lists: Vec<&mut Vec<ScatterInstance>> = vec![&mut work];
            let actor_instances = self.extract_actor_scatter_instances(&mut lists, &[]);
            self.apply_actor_scatter(actor_instances, false);
        }
        if erase {
            self.erase_scatter_actors_in_radius(center, radius);
        }

        // ─── ④ 位置に基づいて所有チャンクへ仕分け直す ───
        //   触れたチャンクは中身が空でもエントリを作っておく。
        //   そうしないと「全部消した」チャンクが dirty 集合に載らず、
        //   保存時に古い .tscatter が消えずに残ってしまう。
        for &coord in &touched {
            self.terrain.scatter.entry(coord).or_default();
        }
        for inst in work {
            let coord = owning_chunk_coord(&settings, inst.pos);
            self.terrain.scatter.entry(coord).or_default().push(inst);
        }

        // ─── ⑤ 変化があったチャンクだけを dirty にする ───
        if changed || before_len > 0 {
            for &coord in &touched {
                self.terrain.scatter_dirty.insert(coord);
            }
            self.terrain.grass_gpu_dirty = true;
        }
    }

    // ─── 地形編集後の再接地 ─────────────────────────────────────────────────

    /// 指定チャンクの散布インスタンスを、編集後の地表へ貼り直す。
    ///
    /// 【なぜ密度編集のときだけ呼ぶのか】
    ///   ペイント（`handle_terrain_paint_world`）は密度グリッドを一切変えない。
    ///   頂点が動かない以上、草が宙に浮くことも埋まることも構造的に起こり得ない。
    ///   ペイントは 1 ストロークで何十回も飛んでくるので、そこで
    ///   全インスタンスの柱探索（1 本あたり数十回の密度サンプル）を走らせると
    ///   目に見えて重くなる。よって再接地は密度編集経路にのみ挿す。
    ///
    /// 【undo/redo でも呼ぶ理由】
    ///   密度スナップショットを戻すと地面も戻る。散布そのものは undo されない
    ///   （T3 第1段のスコープ外）が、再接地だけは掛けておかないと
    ///   「undo したら草だけ空中に取り残される」という壊れた見た目になる。
    ///   再接地後の草は「今の地面」に必ず載っている、という不変条件を保つ。
    ///
    /// 戻り値は再接地の結果 1 本でも変化したチャンクがあったかどうか。
    pub(super) fn restick_scatter_for_chunks(&mut self, coords: &[ChunkCoord]) -> bool {
        if coords.is_empty() || self.terrain.scatter.is_empty() {
            return false;
        }
        let y_search = RESTICK_Y_SEARCH_VOXELS * self.terrain.settings.voxel_size;

        // ─── ScatterField は terrain を不変借用するので、対象リストを先に抜く ───
        let mut taken: Vec<(ChunkCoord, Vec<ScatterInstance>)> = Vec::new();
        for &coord in coords {
            if let Some(list) = self.terrain.scatter.remove(&coord) {
                if !list.is_empty() {
                    taken.push((coord, list));
                }
            }
        }
        if taken.is_empty() {
            return false;
        }

        // ─── 再接地（純粋層へ委譲）───
        let mut any_changed = false;
        {
            let field = TerrainScatterField::from_state(&self.terrain);
            for (_, list) in taken.iter_mut() {
                let before: Vec<[f32; 3]> = list.iter().map(|i| i.pos).collect();
                restick_instances(&field, &self.terrain.props, list, y_search);
                // 削除されたか、1 本でも動いたら変化ありとみなす。
                let moved = list.len() != before.len()
                    || list.iter().zip(before.iter()).any(|(i, &p)| i.pos != p);
                any_changed |= moved;
            }
        }

        // ─── 書き戻し（再接地で位置が変わったので所有チャンクも変わりうる）───
        //   盛土で 1 チャンクぶん上へ押し上げられた草は上のチャンクへ移す。
        for (coord, list) in taken {
            // 元のチャンクは（空でも）エントリを残す＝保存時に旧ファイルを消せる。
            self.terrain.scatter.entry(coord).or_default();
            for inst in list {
                let owner = owning_chunk_coord(&self.terrain.settings, inst.pos);
                self.terrain.scatter.entry(owner).or_default().push(inst);
            }
        }

        if any_changed {
            for &coord in coords {
                self.terrain.scatter_dirty.insert(coord);
            }
            self.terrain.grass_gpu_dirty = true;
        }
        any_changed
    }

    // ─── 永続化 ─────────────────────────────────────────────────────────────

    /// 全チャンクの散布データを .tscatter として保存する。
    ///
    /// 【空チャンクのファイルを消す理由】
    ///   インスタンスが 0 本になったチャンクのファイルを残すと、次回ロード時に
    ///   古い草が復活する（消したはずの草が戻る＝もっとも分かりにくい部類のバグ）。
    ///   よって「0 本 = ファイルを消す」を保存の不変条件とする。
    ///
    /// - `only_dirty`: true なら **変更のあったチャンクだけ**を書き出す
    ///   （シーン保存に相乗りするフラッシュ用。`save_terrain_cover` と同じ規約）。
    ///
    /// 戻り値は (書き出したファイル数, 削除したファイル数)。
    pub(super) fn save_terrain_scatter(
        &mut self,
        dir: &std::path::Path,
        only_dirty: bool,
    ) -> (u32, u32) {
        let mut written = 0u32;
        let mut removed = 0u32;

        for (&coord, instances) in &self.terrain.scatter {
            if only_dirty && !self.terrain.scatter_dirty.contains(&coord) {
                continue;
            }
            let path = dir.join(tscatter_file_name(coord));
            if instances.is_empty() {
                // ─── 空 → 既存ファイルを削除する（無ければ何もしない）───
                match std::fs::remove_file(&path) {
                    Ok(()) => removed += 1,
                    Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
                    Err(e) => eprintln!("[SEED terrain] tscatter remove failed: {path:?} err={e}"),
                }
                continue;
            }
            let bytes = write_chunk(instances, coord);
            match std::fs::write(&path, &bytes) {
                Ok(()) => written += 1,
                Err(e) => eprintln!("[SEED terrain] tscatter save failed: {path:?} err={e}"),
            }
        }

        self.terrain.scatter_dirty.clear();
        (written, removed)
    }

    /// .tvox の隣にある .tscatter を読み込んで `terrain.scatter` を埋める。
    ///
    /// 【ファイルが無いのはエラーではない】
    ///   散布機能より前に保存されたシーンには .tscatter が存在しない。
    ///   欠落を空配列として扱うことで、旧シーンもそのまま開ける
    ///   （ここでエラーにすると既存プロジェクトが全部開けなくなる）。
    pub(super) fn load_terrain_scatter(&mut self, chunk_paths: &[(ChunkCoord, String)]) {
        for (coord, tvox_path) in chunk_paths {
            let path = tscatter_path_from_tvox(tvox_path);
            let Ok(bytes) = crate::engine::asset_fs::read_bytes(&path) else {
                // 未保存／旧シーン。空として扱う（エラーではない）。
                continue;
            };
            match read_chunk(&bytes) {
                Ok((instances, _stored_coord)) => {
                    self.terrain.scatter.insert(*coord, instances);
                }
                Err(e) => {
                    eprintln!("[SEED terrain] tscatter decode failed, skip: {path} err={e:?}");
                }
            }
        }
        self.terrain.grass_gpu_dirty = true;
    }

    // ─── GPU ────────────────────────────────────────────────────────────────

    /// 草の GPU インスタンスバッファをプロップ種別ごとに作り直す。
    ///
    /// `grass_gpu_dirty` が立っていなければ即座に返る（毎フレーム呼んでよい）。
    ///
    /// 【なぜ全チャンクを毎回舐めるのか】
    ///   GPU バッファはプロップ種別ごとに 1 本の連続配列であり、
    ///   チャンク単位の部分更新をするには「どのチャンクがバッファのどの範囲か」
    ///   という対応表の維持が要る。散布が変わる頻度（ブラシ操作時のみ）に対して
    ///   その複雑さは見合わないと判断し、まるごと作り直す方式にした。
    ///   コストはログ（`[PERF terrain] grass gpu rebuild`）で監視できる。
    ///
    /// 【model 種別を飛ばす理由】
    ///   kind=Model のプロップは草とは別リソース・別パイプラインで描く
    ///   （`TerrainState::rebuild_scatter_models_gpu`）。ここは手続き生成の草だけを
    ///   担当し、model はスキップする（両者は同じ `grass_gpu_dirty` で再構築される）。
    pub(super) fn rebuild_grass_gpu(&mut self, device: &wgpu::Device, queue: &wgpu::Queue) {
        if !self.terrain.grass_gpu_dirty {
            return;
        }
        let t_start = std::time::Instant::now();

        // ─── ① 全チャンクのインスタンスをプロップ添字ごとに、かつチャンク順で束ねる ───
        //   チャンク単位カリング（描画時）のため、各プロップのバッファは**チャンク座標順**に
        //   詰め、各チャンクの連続区間（span: AABB＋first＋count）を記録する。チャンク座標を
        //   ソートしてから詰めることで、span 列も描画区間も決定的になる。
        let settings = self.terrain.settings.clone();
        let mut coords: Vec<ChunkCoord> = self.terrain.scatter.keys().copied().collect();
        coords.sort_by_key(|c| (c.x, c.y, c.z));

        // prop -> (連続インスタンス配列, チャンク span 列)
        let mut by_prop: HashMap<usize, (Vec<GrassInstanceGpu>, Vec<GrassChunkSpan>)> =
            HashMap::new();
        for &coord in &coords {
            let Some(instances) = self.terrain.scatter.get(&coord) else { continue };
            let (aabb_min, aabb_max) =
                chunk_world_aabb(&settings, coord, GRASS_MARGIN_HORIZ, GRASS_MARGIN_UP);
            // このチャンクで各プロップ用に開いた span の添字（entry.1 内）。
            //   チャンク境界を跨いで span を伸ばさない（伸ばすと隣チャンクの草に
            //   このチャンクの AABB が付き、カリングが破綻する）ため、チャンクごとに
            //   最初の 1 本で新規 span を開き、以降は同じ span を伸ばす。
            let mut opened_span: HashMap<usize, usize> = HashMap::new();
            // チャンク内を (プロップ, 間引きハッシュ) 順へ並べる。同一プロップが連続し、
            //   かつプロップ内はハッシュ順になるので、各 span（＝バッファの連続区間）の
            //   任意プレフィクスが空間的に均一なサブセットになる。これが遠景密度減衰
            //   （draw_grass_culled が先頭 kept 本だけ描く）で穴を空けないための前提。
            let mut ordered: Vec<&ScatterInstance> = instances.iter().collect();
            ordered.sort_by_key(|i| (i.prop_id, scatter_thin_key(i.seed)));
            for inst in ordered {
                let prop_index = inst.prop_id as usize;
                let Some(prop) = self.terrain.props.props.get(prop_index) else {
                    // props.json からプロップが消えた孤児インスタンス。描かない。
                    continue;
                };
                if prop.kind != PropKind::Grass {
                    // kind=Model は草とは別経路（rebuild_scatter_models_gpu）で描く。
                    continue;
                }
                let entry = by_prop.entry(prop_index).or_default();
                let first = entry.0.len() as u32;
                entry.0.push(scatter_instance_to_gpu(inst));
                match opened_span.get(&prop_index) {
                    Some(&span_idx) => {
                        // このチャンクで既に開いている span を伸ばす。
                        entry.1[span_idx].count += 1;
                    }
                    None => {
                        // このチャンク・このプロップの最初の 1 本 → 新規 span を開く。
                        let span_idx = entry.1.len();
                        entry.1.push(GrassChunkSpan {
                            aabb_min,
                            aabb_max,
                            first,
                            count: 1,
                        });
                        opened_span.insert(prop_index, span_idx);
                    }
                }
            }
        }

        // ─── ② インスタンスが 0 本になったプロップのバッファを捨てる ───
        //   （VRAM を掴んだままにしない）
        //   【snatch lock 再帰の防止】捨てる GrassInstanceBuffer が持つバッファは前フレームの
        //   submit が in-flight で参照中のため、drop しても wgpu は即座に破棄せず遅延破棄キューへ
        //   積む。この破棄がフレーム末尾 submit（snatch read lock 保持）中に処理されると write lock
        //   を再帰取得してパニックする。本メソッドは begin_frame より前（read lock 非保持）で走る
        //   ので、削除が起きたら drop 直後に poll(Wait) して遅延破棄をここで確定させる
        //   （rebuild_scatter_models_gpu の scatter_models.retain と同一手順）。
        let grass_buffers_before = self.terrain.grass_buffers.len();
        self.terrain
            .grass_buffers
            .retain(|prop_index, _| by_prop.contains_key(prop_index));
        if self.terrain.grass_buffers.len() != grass_buffers_before {
            let _ = device.poll(wgpu::PollType::Wait);
        }

        // ─── ③ プロップ種別ごとにバッファを作る／更新する ───
        let Some(ctx) = self.draw_ctx.as_ref() else {
            // 描画コンテキストが無い（ヘッドレス等）。フラグは寝かせて次回に備える。
            self.terrain.grass_gpu_dirty = false;
            return;
        };
        let pipeline = &ctx.pipelines.gbuffer.grass;

        // ─── 単一 storage バインド上限（既定 128MB）に収まる最大本数 ───
        //   草バッファはプロップ種別ごとに 1 本・全域バインドなので、総本数がこの値を
        //   超えるとバインドグループ生成でパニックする。16×16 高密度散布で 1 プロップが
        //   約 400 万本（≒192MB）に達しクラッシュしていた。本数と span をここで頭打ちに
        //   して、確保するバッファが上限を超えないことを構造的に保証する。
        let max_instances = crate::engine::core::renderer::grass_gbuffer::max_grass_instances(device);

        let mut total = 0usize;
        for (prop_index, (instances, spans)) in &mut by_prop {
            let Some(prop) = self.terrain.props.props.get(*prop_index) else {
                continue;
            };
            let uniform = grass_uniform_from_prop(prop);

            // 上限超過ぶんを切り詰める（span も同時に整合させる）。切り捨てはチャンク座標
            // ソートの末尾から起きる。切り捨てが発生したら 1 プロップ分の内訳を警告する。
            let dropped = crate::engine::core::renderer::grass_gbuffer::clamp_instances_and_spans(
                instances, spans, max_instances,
            );
            if dropped > 0 {
                eprintln!(
                    "[SEED terrain] 草プロップ #{prop_index} の散布 {} 本が単一バインド上限 {max_instances} 本を超過。\
                     {dropped} 本を描画対象から除外しました（クラッシュ回避）。密度を下げるか散布範囲を狭めてください。",
                    instances.len() + dropped
                );
            }
            total += instances.len();

            match self.terrain.grass_buffers.get_mut(prop_index) {
                // 既存バッファは中身を差し替える（容量が足りていれば再確保しない）。
                Some(buf) => {
                    buf.update(device, queue, pipeline, instances, uniform);
                    buf.set_spans(spans.clone());
                }
                None => {
                    let mut buf = GrassInstanceBuffer::new(device, pipeline, instances, uniform);
                    buf.set_spans(spans.clone());
                    self.terrain.grass_buffers.insert(*prop_index, buf);
                }
            }
        }

        self.terrain.grass_gpu_dirty = false;

        if *PERF_TERRAIN_LOG_ENABLED {
            let ms = t_start.elapsed().as_secs_f64() * MILLIS_PER_SEC;
            eprintln!("[PERF terrain] grass gpu rebuild: {total} instances in {ms:.2}ms");
        }
    }
}

// ============================================================
//  TerrainState — 散布モデルの GPU 再構築
//
//  App ではなく TerrainState のメソッドにしてあるのは、frame_renderer の描画
//  ブロックが `self.draw_ctx` を不変借用し続けたまま `self.terrain` を可変で
//  触れるようにするためである（`&mut self` メソッドだと draw_ctx の借用と
//  衝突する。terrain フィールドだけを可変借用する形にして分離する）。
// ============================================================

impl TerrainState {
    /// kind=Model 散布プロップの GPU リソース（モデルのロードとインスタンス行列）を
    /// 再構築する。草の `rebuild_grass_gpu` と対を成す。
    ///
    /// 【トリガと呼び出し順（重要）】
    ///   草と同じ `grass_gpu_dirty` を再構築トリガに使う（散布データは草と共有の集合
    ///   なので、フラグを分けると散布操作 5 か所すべてで二重管理になる）。
    ///   **本メソッドはフラグを寝かせない**——同フレーム後段の `rebuild_grass_gpu` が
    ///   クリアするため、必ず草再構築より前に呼ぶこと（順序は frame_renderer.rs で固定。
    ///   逆にすると model 側が毎回スキップされる）。
    ///
    /// 【モデルのロードはプロップごとに 1 回だけ】
    ///   散布インスタンス数ぶんロードしない。`model_path` 単位で GpuModel をキャッシュし、
    ///   props リロードで `model_path` が変わったときだけ読み直す。ロード失敗は 1 回だけ
    ///   警告してそのプロップを飛ばす（他プロップは描く）。
    ///
    /// 【VRAM スパイク回避】
    ///   容量が足りていればバッチは作り直さず `batch.update`（内部 `write_buffer`）で
    ///   行列だけ差し替える。容量不足時のみ作り直す（統合バッチ `shared_model_batches`
    ///   と同じ規約 `.max(SCATTER_MODEL_MIN_CAPACITY)`）。
    ///
    /// 【チャンク単位カリング（Terrain T3 描画最適化）】
    ///   本メソッドは散布が変わったとき（`grass_gpu_dirty`）だけ走り、プロップごとに
    ///   **チャンク単位の span（AABB＋事前計算ワールド行列）**を `res.chunk_spans` へ
    ///   構築する。実際の GPU アップロード（`batch.update`）は毎フレームの
    ///   `cull_and_update_scatter_models` が可視チャンクぶんだけに絞って行う。
    ///   ここでバッチへ全行列を流し込まないのは、カメラが動くと可視集合が変わるためで、
    ///   「rebuild=データ準備／毎フレーム=可視ぶんアップロード」に責務を分けている。
    ///
    /// - `camera_pos`: バッチ容量確保後の初回アップロードを兼ねて可視カリングを 1 回
    ///   走らせるための基準（実アップロードは呼び出し側の毎フレーム経路が担う）。
    pub(super) fn rebuild_scatter_models_gpu(&mut self, ctx: &DrawContext, _camera_pos: [f32; 3]) {
        if !self.grass_gpu_dirty {
            return;
        }
        let t_start = std::time::Instant::now();

        // ─── ① 全チャンクの Model インスタンスをプロップ×チャンクで束ねる ───
        let mats_by_prop = gather_scatter_model_chunks(&self.scatter, &self.props, &self.settings);

        // ─── ② 描画対象から消えたプロップのリソース／失敗記録を捨てる（VRAM 解放）───
        //   【snatch lock 再帰の防止】捨てた GpuModel／バッチのバッファは、前フレームの
        //   submit がまだ参照している（in-flight）ため、drop しても wgpu は即座には破棄せず
        //   「遅延破棄キュー」へ積む。この遅延破棄を、後段の queue.write_buffer や本フレーム
        //   末尾の queue.submit() が **snatch read lock を保持したまま** 処理すると、破棄側は
        //   snatch write lock を取りに行き「同一スレッドで snatch lock を再帰取得」して
        //   パニックする（wgpu-core resource.rs=破棄=write / global.rs=submit/write_buffer=read）。
        //   そこで drop 直後に poll(Wait) を挟み、read lock を誰も持っていないこの時点で
        //   遅延破棄を確定させる（slot_ops.rs / terrain_ops.rs の GpuModel 差し替えと同じ安全手順）。
        let scatter_models_before_retain = self.scatter_models.len();
        self.scatter_models.retain(|k, _| mats_by_prop.contains_key(k));
        self.scatter_model_failed.retain(|k, _| mats_by_prop.contains_key(k));
        if self.scatter_models.len() != scatter_models_before_retain {
            let _ = ctx.device.poll(wgpu::PollType::Wait);
        }

        // ─── ③ プロップごとに: モデルをロード（キャッシュ）→ チャンク span を格納 ───
        //   バッチへの行列アップロードはここでは行わない（毎フレームの
        //   cull_and_update_scatter_models が可視ぶんだけ流す）。容量だけ確保しておく。
        let mut total = 0usize;
        for (prop_index, spans) in mats_by_prop {
            let mat_count: usize = spans.iter().map(|s| s.mats.len()).sum();
            let Some(prop) = self.props.props.get(prop_index) else { continue };
            let want_path = match prop.model_path.as_deref() {
                Some(p) if !p.is_empty() => p.to_string(),
                _ => continue,
            };

            // 既存リソースの model_path が変わっていたら破棄して読み直す（props リロード）。
            if let Some(res) = self.scatter_models.get(&prop_index) {
                if res.model_path != want_path {
                    self.scatter_models.remove(&prop_index);
                    self.scatter_model_failed.remove(&prop_index);
                    // 旧リソースの遅延破棄を確定させてから読み直す（②と同じ snatch 再帰対策。
                    // in-flight バッファの破棄が後段の write_buffer／submit の read lock 下で
                    // 走ると再帰パニックするため、read lock 非保持のここで poll(Wait) する）。
                    let _ = ctx.device.poll(wgpu::PollType::Wait);
                }
            }

            // まだリソースが無ければロードを試みる（同じ壊れたパスは再試行しない）。
            if !self.scatter_models.contains_key(&prop_index) {
                let already_failed = self
                    .scatter_model_failed
                    .get(&prop_index)
                    .map(|p| p == &want_path)
                    .unwrap_or(false);
                if already_failed {
                    continue; // 警告済み。黙ってスキップ。
                }
                match load_scatter_model(ctx, &want_path) {
                    Ok((cpu_model, gpu_model)) => {
                        let capacity = (mat_count * 2).max(SCATTER_MODEL_MIN_CAPACITY);
                        // メッシュレットカリングを有効化して生成する。インスタンス数（capacity）が
                        // 上限内なら近景の高ポリ木が可視メッシュレットだけ描かれてアクター並みに軽くなる。
                        // capacity × メッシュレット数 が max_buffer_size を超える prim は
                        // InstancedModelBatch::new 側の防御でスロット未確保＝通常描画へ自動フォールバック
                        // （大量散布でもパニックしない）。
                        let batch = ctx.create_instanced_batch(&cpu_model, capacity as u32);
                        self.scatter_models.insert(
                            prop_index,
                            ScatterModelResource {
                                model_path: want_path.clone(),
                                cpu_model,
                                gpu_model,
                                batch,
                                capacity,
                                chunk_spans: Vec::new(),
                                // 新規バッチは未アップロード。初回 update で確定させる。
                                merge_gate: Default::default(),
                            },
                        );
                        self.scatter_model_failed.remove(&prop_index);
                    }
                    Err(err) => {
                        // ロード失敗。1 回だけ警告して記録（次フレーム以降は黙る）。
                        eprintln!(
                            "[SEED terrain] 散布モデルのロードに失敗しました: prop='{}' path='{}': {} \
                             （このプロップは描画をスキップします）",
                            prop.id, want_path, err
                        );
                        self.scatter_model_failed.insert(prop_index, want_path.clone());
                        continue;
                    }
                }
            }

            // ここまで来ればリソースは必ず存在する。容量不足なら作り直す。
            //   容量は「全インスタンス数」で確保する（毎フレームのカリングで可視ぶんへ
            //   絞るが、カメラ位置次第では全チャンクが可視になり得るため、最悪ケースの
            //   全数を収められる容量を持たせておく＝毎フレームの再確保＝snatch を避ける）。
            let res = self
                .scatter_models
                .get_mut(&prop_index)
                .expect("scatter model resource just ensured present");
            if mat_count > res.capacity {
                let capacity = (mat_count * 2).max(SCATTER_MODEL_MIN_CAPACITY);
                // 新バッチを作ると、旧バッチがこの代入で drop される。旧バッチの instance
                // バッファは前フレームの submit が参照中（in-flight）なので、drop は即時破棄
                // されず wgpu の遅延破棄キューへ積まれる。
                // 容量拡張時もメッシュレットカリング有効で作り直す（生成時と同じ方針）。
                res.batch = ctx.create_instanced_batch(&res.cpu_model, capacity as u32);
                res.capacity = capacity;
                // バッチ実体が入れ替わったので、ダーティゲートの前フレーム情報も捨てる
                // （新バッチは未アップロード＝必ず update させる）。
                res.merge_gate = Default::default();
                // 【snatch lock 再帰の防止】read lock 非保持のここで旧バッファ解放を確定させる。
                let _ = ctx.device.poll(wgpu::PollType::Wait);
            }
            // チャンク span を格納（毎フレームのカリングが読む）。行列アップロードはしない。
            res.chunk_spans = spans;
            total += mat_count;
        }

        // フラグはここではクリアしない（rebuild_grass_gpu が後段でクリアする）。

        if *PERF_TERRAIN_LOG_ENABLED {
            let ms = t_start.elapsed().as_secs_f64() * MILLIS_PER_SEC;
            eprintln!(
                "[PERF terrain] scatter model gpu rebuild: {total} instances across {} props in {ms:.2}ms",
                self.scatter_models.len()
            );
        }
    }

    /// 【毎フレーム】散布モデルをチャンク単位で視錐台＋距離カリングし、可視チャンクの
    /// インスタンス行列だけをバッチへアップロードする（Terrain T3 描画最適化）。
    ///
    /// `rebuild_scatter_models_gpu` が用意した `res.chunk_spans` を走査し、各チャンクの
    /// AABB がメインカメラ視錐台の外、または距離カリング閾値より遠ければそのチャンクを
    /// 丸ごと飛ばす。生き残ったチャンクの事前計算行列を 1 本の可視配列へ連結し、
    /// `batch.update` へ流す。これでバッチには可視インスタンスだけが載り、G-Buffer パス
    /// もシャドウパスも可視ぶんだけを描く（描画コストが可視数に比例する）。
    ///
    /// 【毎フレーム update のコスト】`batch.update` は dirty 時に可視インスタンス分の
    /// ワールド行列を rayon で再計算する。カリング後の可視数は近傍チャンクぶんに限られる
    /// ため軽い。最悪（全チャンク可視）でも「全数を毎フレーム計算」に留まり、これは
    /// GPU で重い木を全数描くコストに比べれば桁違いに小さい。
    ///
    /// 【シャドウの扱い（既知の割り切り）】バッチはメイン描画とシャドウ描画で共有される
    /// ため、視錐台外のチャンクはシャドウキャスタからも外れる。画面のすぐ外の木が落とす
    /// 影が画面端で欠けうるが、AABB の水平マージン（`SCATTER_MODEL_MARGIN_HORIZ`）で縁を広げて
    /// 緩和している。全木を影に含める本格対応（シャドウ専用の広いカリング）は将来。
    ///
    /// - `planes`: `extract_frustum_planes(view_proj)` のメインカメラ 6 平面。
    /// - `camera_pos`: 距離カリングと距離 LOD の基準（ワールド）。
    pub(super) fn cull_and_update_scatter_models(
        &mut self,
        ctx: &DrawContext,
        planes: &[[f32; 4]; 6],
        camera_pos: [f32; 3],
    ) {
        use crate::engine::core::renderer::gpu_resources::{
            aabb_distance_sq, aabb_outside_frustum, density_kept_count,
        };
        let model_cull_dist = *SCATTER_MODEL_CULL_DISTANCE;
        let cull_dist_sq = model_cull_dist * model_cull_dist;
        // 遠景密度減衰の帯境界（二乗距離）。近=全数 / 中=1/2 / 遠=1/4。
        let decay_near_sq = *SCATTER_MODEL_DECAY_NEAR * *SCATTER_MODEL_DECAY_NEAR;
        let decay_mid_sq = *SCATTER_MODEL_DECAY_MID * *SCATTER_MODEL_DECAY_MID;

        let nocull = *SCATTER_CULL_DISABLED;
        let mut dbg_total = 0usize;
        let mut dbg_visible = 0usize;
        for res in self.scatter_models.values_mut() {
            // 可視チャンクの行列を連結する（スクラッチは毎フレーム作り捨て）。
            let mut visible: Vec<[[f32; 4]; 4]> = Vec::new();
            for span in &res.chunk_spans {
                dbg_total += span.mats.len();
                if span.mats.is_empty() {
                    continue;
                }
                // 計測用: NOCULL 指定時はテストせず全チャンク・全密度を含める（カリング前挙動）。
                let dist_sq = aabb_distance_sq(span.aabb_min, span.aabb_max, camera_pos);
                let kept = if nocull {
                    span.mats.len()
                } else {
                    if aabb_outside_frustum(planes, span.aabb_min, span.aabb_max) {
                        continue;
                    }
                    if dist_sq > cull_dist_sq {
                        continue;
                    }
                    // 遠景密度減衰: チャンク距離に応じて先頭 kept 本だけ描く（span.mats は
                    // ハッシュ順なのでプレフィクスが空間的に均一。gather_scatter_model_chunks）。
                    density_kept_count(span.mats.len() as u32, dist_sq, decay_near_sq, decay_mid_sq)
                        as usize
                };
                if kept == 0 {
                    continue;
                }
                visible.extend_from_slice(&span.mats[..kept]);
            }
            dbg_visible += visible.len();
            // ── ダーティゲート ────────────────────────────────────────
            // 散布モデルは静的で、タグもアニメ時刻も持たない（常に空スライス）。
            // 可視行列列と距離 LOD の振り分けが前フレームと完全一致するなら、
            // update の出力（ワールド行列キャッシュ・LOD バッファ・ID バッファ）は
            // 完全に同一になるので丸ごと省ける。
            // 速度バッファは下で毎フレーム reset している（＝常に prev=curr）ため、
            // スキップしても GPU 上の前フレーム行列は prev=curr のまま正しい。
            {
                let gate_inputs = super::merge_batch_gate::MergeBatchInputs {
                    mats:           &visible,
                    abs_ids:        &[],
                    render_tags:    &[],
                    pose_overrides: &[],
                };
                let lod_unchanged = res.batch.lod_buckets_unchanged(camera_pos);
                if res.merge_gate.decide(&gate_inputs, lod_unchanged, false) {
                    continue;
                }
            }
            // 可視ぶんだけをアップロード（dirty 化してワールド行列を再計算させる）。
            // visible が空なら update 内部で全 LOD カウントが 0 になり、何も描かれない。
            res.batch.mark_dirty();
            // 速度バッファ（モーションベクタ）: 散布モデルは **静的** であり、
            // 正しい速度は常に「カメラ由来ぶんのみ」である。一方 visible の並びは
            // チャンク距離カリングと遠景密度減衰の結果で毎フレーム変わり得るため、
            // 前フレームのスロットと今フレームのスロットが同じ株を指す保証が無い
            // （本数が偶然一致したまま中身だけズレるケースがある）。
            // 静的である以上 prev=curr が厳密に正しいので、毎フレームリセットして
            // スロット対応の問題そのものを消す（余計な比較も履歴も持たない）。
            res.batch.request_velocity_reset();
            // 散布オブジェクトはアクタ単位のタグを持たない（全インスタンス 0 扱い）。
            res.batch.update(&ctx.queue, &res.cpu_model, &visible, &[], camera_pos);
        }
        // 計測ログ（SEED_PERF_TERRAIN 有効時のみ・毎フレームだと五月蝿いので間引く）。
        if *PERF_TERRAIN_LOG_ENABLED && dbg_total > 0 {
            use std::sync::atomic::{AtomicU64, Ordering};
            static N: AtomicU64 = AtomicU64::new(0);
            if N.fetch_add(1, Ordering::Relaxed) % 60 == 0 {
                eprintln!(
                    "[PERF terrain] scatter model cull: visible={dbg_visible}/{dbg_total} \
                     (nocull={nocull})"
                );
            }
        }
    }
}

// ============================================================
//  内部ヘルパ
// ============================================================

/// props.json 読み込み失敗の警告を 1 回だけ出す。
fn warn_props_once(message: &str) {
    if !PROPS_LOAD_WARNED.swap(true, std::sync::atomic::Ordering::Relaxed) {
        eprintln!("{message}");
    }
}

/// 球（中心・半径）が触れうるチャンク格子座標を列挙する。
///
/// 球の AABB をチャンク格子へ写して全数挙げる（保守的＝取りこぼさない）。
/// 触れないチャンクが少し混ざるのは無害で、逆に取りこぼすと
/// ブラシがチャンク境界を跨いだときに草が片側にしか出ない。
fn chunks_in_sphere(
    settings: &TerrainSettings,
    center: [f32; 3],
    radius: f32,
) -> Vec<ChunkCoord> {
    let extent = settings.chunk_extent();
    if !(extent > 0.0) || !(radius >= 0.0) {
        return Vec::new();
    }
    // AABB の下端・上端をチャンク格子へ写す。
    let lo = owning_chunk_coord(
        settings,
        [center[0] - radius, center[1] - radius, center[2] - radius],
    );
    let hi = owning_chunk_coord(
        settings,
        [center[0] + radius, center[1] + radius, center[2] + radius],
    );

    let mut out = Vec::new();
    for z in lo.z..=hi.z {
        for y in lo.y..=hi.y {
            for x in lo.x..=hi.x {
                out.push(ChunkCoord::new(x, y, z));
            }
        }
    }
    out
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    // テストからのみ使うデータ層 API（実行時経路では使わないので上位では import しない）。
    use crate::engine::terrain::scatter::{surface_hit_down, GRASS_MAX_SEGMENTS};

    /// テスト用の地形設定（extent = 0.5 * 32 = 16.0 m）。
    fn test_settings() -> TerrainSettings {
        TerrainSettings::default()
    }

    /// 所有チャンクの計算が、負座標でも境界ちょうどでも正しいこと。
    ///
    /// ここが 1 ずれると、チャンクの継ぎ目一列ぶんの草が
    /// 隣のファイルへ書かれて静かに消える（発見が非常に難しいバグ）。
    #[test]
    fn owning_chunk_handles_negative_and_boundary() {
        let s = test_settings();
        let extent = s.chunk_extent();

        // ─── 原点はチャンク 0 ───
        assert_eq!(owning_chunk_coord(&s, [0.0, 0.0, 0.0]), ChunkCoord::new(0, 0, 0));

        // ─── 境界ちょうど（extent）は「次のチャンクの下端」＝ 1 ───
        //   区間は [c*extent, (c+1)*extent) の半開区間である。
        assert_eq!(
            owning_chunk_coord(&s, [extent, extent, extent]),
            ChunkCoord::new(1, 1, 1)
        );

        // ─── 境界のわずか下は 0 のまま ───
        let eps = 1.0e-3;
        assert_eq!(
            owning_chunk_coord(&s, [extent - eps, extent - eps, extent - eps]),
            ChunkCoord::new(0, 0, 0)
        );

        // ─── 負座標: -eps は chunk -1（0 方向切り捨てだと 0 になり間違う）───
        assert_eq!(
            owning_chunk_coord(&s, [-eps, -eps, -eps]),
            ChunkCoord::new(-1, -1, -1)
        );

        // ─── 負の境界ちょうど（-extent）は chunk -1 の下端 ───
        assert_eq!(
            owning_chunk_coord(&s, [-extent, -extent, -extent]),
            ChunkCoord::new(-1, -1, -1)
        );

        // ─── 負側でさらに 1 つ下 ───
        assert_eq!(
            owning_chunk_coord(&s, [-extent - eps, 0.0, -extent - eps]),
            ChunkCoord::new(-2, 0, -2)
        );
    }

    /// `ScatterInstance` → `GrassInstanceGpu` の変換が値を落とさないこと。
    ///
    /// フィールドの並び替えでサイレントに取り違えると、
    /// 「草が全部同じ向き」「スケールが seed になる」等の描画バグになる。
    #[test]
    fn scatter_instance_converts_to_gpu_losslessly() {
        let inst = ScatterInstance {
            pos:     [1.5, -2.25, 3.75],
            normal:  [0.0, 0.6, 0.8],
            yaw:     1.25,
            scale:   0.875,
            prop_id: 7,
            seed:    0xDEAD_BEEF,
        };
        let gpu = scatter_instance_to_gpu(&inst);

        assert_eq!(gpu.pos, inst.pos, "pos が一致しない");
        assert_eq!(gpu.normal, inst.normal, "normal が一致しない");
        assert_eq!(gpu.yaw, inst.yaw, "yaw が一致しない");
        assert_eq!(gpu.scale, inst.scale, "scale が一致しない");
        assert_eq!(gpu.seed, inst.seed, "seed が一致しない");
        assert_eq!(gpu._pad, [0; 3], "パディングはゼロ初期化であること");
    }

    /// 出荷する props.json が実際にパースできること。
    ///
    /// JSON を手で書き換えたときの構文ミス・型ミスを CI で捕まえる
    /// （壊れていても既定セットへフォールバックしてしまうため、
    ///  実行時には気付けない＝テストでしか守れない）。
    #[test]
    fn shipped_props_json_parses() {
        let text = include_str!("../../../../../assets/terrain/props.json");
        let set = TerrainPropSet::from_json_str(text)
            .expect("assets/terrain/props.json のパースに失敗した");

        // 既定セットへのフォールバックと区別するため、想定 ID の存在を確認する。
        for id in ["grass_field", "grass_dry", "tree_pine"] {
            assert!(
                set.find_by_id(id).is_some(),
                "props.json に '{id}' が定義されていない"
            );
        }

        // 草プロップは Grass 種別で、目視できる大きさであること。
        let (_, grass) = set.find_by_id("grass_field").unwrap();
        assert_eq!(grass.kind, PropKind::Grass, "grass_field は kind=grass であること");
        assert!(grass.grass.height > 0.0, "草の高さが 0 以下");
        assert!(grass.grass.width > 0.0, "草の幅が 0 以下");
        assert!(grass.scatter.density > 0.0, "草の散布密度が 0 以下");

        // 木プロップは Model 種別であること（第2段の描画対象）。
        let (_, tree) = set.find_by_id("tree_pine").unwrap();
        assert_eq!(tree.kind, PropKind::Model, "tree_pine は kind=model であること");
    }

    /// 出荷する props.json のレイヤ条件が layers.json に実在するレイヤを指すこと。
    ///
    /// レイヤ名のタイポは `layer_weight_at` が 0.0 を返すため
    /// 「なぜか一本も生えない」という無言の失敗になる。ここで捕まえる。
    #[test]
    fn shipped_props_reference_existing_layers() {
        let props_text = include_str!("../../../../../assets/terrain/props.json");
        let layers_text = include_str!("../../../../../assets/terrain/layers.json");
        let props = TerrainPropSet::from_json_str(props_text).unwrap();
        let layers = TerrainLayerSet::from_json_str(layers_text).unwrap();

        for prop in &props.props {
            for cond in &prop.rule.layer_conditions {
                assert!(
                    layers.layers.iter().any(|l| l.name == cond.layer),
                    "props.json のプロップ '{}' が未定義のレイヤ '{}' を参照している",
                    prop.id, cond.layer
                );
            }
        }
    }

    /// .tscatter がファイル経路で往復しても内容が保たれること。
    ///
    /// バイト列レベルの往復は tests_scatter.rs が見ているので、
    /// ここでは「保存ヘルパが組み立てるファイル名で書いて読み戻せる」ことを見る
    /// （ユーザーの assets を汚さないよう一時ディレクトリを使う）。
    #[test]
    fn tscatter_round_trips_through_file_path() {
        let dir = std::env::temp_dir().join(format!(
            "seed_tscatter_roundtrip_{}_{:?}",
            std::process::id(),
            std::thread::current().id()
        ));
        std::fs::create_dir_all(&dir).expect("一時ディレクトリを作れない");

        // 負座標のチャンクを使う（ファイル名生成の符号漏れも同時に見る）。
        let coord = ChunkCoord::new(-3, 1, 2);
        let instances = vec![
            ScatterInstance {
                pos: [1.0, 2.0, 3.0],
                normal: [0.0, 1.0, 0.0],
                yaw: 0.5,
                scale: 1.25,
                prop_id: 0,
                seed: 42,
            },
            ScatterInstance {
                pos: [-4.5, 0.25, 6.75],
                normal: [0.6, 0.8, 0.0],
                yaw: 2.5,
                scale: 0.75,
                prop_id: 1,
                seed: 7,
            },
        ];

        let path = dir.join(tscatter_file_name(coord));
        std::fs::write(&path, write_chunk(&instances, coord)).expect("書き出し失敗");

        let bytes = std::fs::read(&path).expect("読み込み失敗");
        let (restored, restored_coord) = read_chunk(&bytes).expect("デコード失敗");

        assert_eq!(restored_coord, coord, "チャンク座標が往復で変わった");
        assert_eq!(restored, instances, "インスタンス配列が往復で変わった");

        let _ = std::fs::remove_dir_all(&dir);
    }

    /// .tvox パスから .tscatter パスを導く規則が正しいこと。
    #[test]
    fn tscatter_path_derives_from_tvox_path() {
        assert_eq!(
            tscatter_path_from_tvox("assets://terrain/main/chunk_0_0_0.tvox"),
            "assets://terrain/main/chunk_0_0_0.tscatter"
        );
        // 負座標を含むパスでも壊れないこと。
        assert_eq!(
            tscatter_path_from_tvox("assets://terrain/s/chunk_-1_0_-2.tvox"),
            "assets://terrain/s/chunk_-1_0_-2.tscatter"
        );
        // 想定外の拡張子は素直に付け足す（読み込みに失敗して空になるだけ）。
        assert_eq!(tscatter_path_from_tvox("foo.bin"), "foo.bin.tscatter");
    }

    /// 仮想パスとファイル名が tvox と同じ規則で並ぶこと。
    #[test]
    fn tscatter_names_follow_tvox_convention() {
        let coord = ChunkCoord::new(1, -2, 3);
        assert_eq!(tscatter_file_name(coord), "chunk_1_-2_3.tscatter");
        assert_eq!(
            tscatter_virtual_path("main", coord),
            "assets://terrain/main/chunk_1_-2_3.tscatter"
        );
    }

    /// ブラシ球が跨るチャンクを取りこぼさないこと。
    ///
    /// 取りこぼすと、境界を跨いだブラシで片側にしか草が出ない。
    #[test]
    fn chunks_in_sphere_covers_straddling_brush() {
        let s = test_settings();
        let extent = s.chunk_extent();

        // 境界ちょうどを中心に、両側へ食い込む半径のブラシ。
        let coords = chunks_in_sphere(&s, [extent, 0.0, extent], extent * 0.25);

        // XZ の 4 チャンク（0,0 / 1,0 / 0,1 / 1,1 相当）をすべて含むこと。
        for (x, z) in [(0, 0), (1, 0), (0, 1), (1, 1)] {
            assert!(
                coords.contains(&ChunkCoord::new(x, 0, z)),
                "チャンク ({x},0,{z}) が列挙されていない"
            );
        }
    }

    /// 地形を掘ったあと、散布インスタンスが新しい地表まで降りてくること。
    ///
    /// 【これが守る不変条件】
    ///   「草は常に今の地面に載っている」。密度編集で地面が下がったのに草が
    ///   その場に残ると、空中に浮いた草が見える（もっとも目立つ壊れ方）。
    ///
    /// App を構築せず、チャンクマップ＋`TerrainScatterField` を直接組んで
    /// `restick_instances` を回す（GPU も ECS も要らない純粋な検証）。
    #[test]
    fn instances_follow_ground_down_after_subtract() {
        let settings = test_settings();
        let iso = settings.iso_level;
        let vs = settings.voxel_size;
        let samples = settings.samples_per_axis();
        let layers = TerrainLayerSet::default();

        // ─── 平坦な地面を作る: density = (world_y - ground_y) ───
        //   density < iso が SOLID なので、ground_y より下が地中になる。
        let build_ground = |ground_y: f32| {
            let mut chunk = TerrainChunkData::new_filled(&settings, 0.0);
            for iz in 0..samples {
                for iy in 0..samples {
                    for ix in 0..samples {
                        let world_y = iy as f32 * vs;
                        chunk.set_sample(ix, iy, iz, (world_y - ground_y) + iso);
                    }
                }
            }
            let mut chunks = HashMap::new();
            chunks.insert(ChunkCoord::new(0, 0, 0), chunk);
            chunks
        };

        // ─── ① 高さ 4.0m の地面へ 1 本置く ───
        let original_ground = 4.0f32;
        let chunks = build_ground(original_ground);
        let field = TerrainScatterField::new(&chunks, &settings, &layers);

        // 実際に接地点が取れることを先に確認する（テスト自身の前提検証）。
        let x = vs * 4.0;
        let z = vs * 4.0;
        let (hit, _n) = surface_hit_down(&field, x, z, settings.chunk_extent(), 0.0)
            .expect("平坦な地面で接地点が取れないのはテストの前提崩れ");
        assert!(
            (hit[1] - original_ground).abs() < vs,
            "接地点が想定の地面高さから離れている: {} vs {original_ground}", hit[1]
        );

        let props = TerrainPropSet::default();
        let mut instances = vec![ScatterInstance {
            pos: hit,
            normal: [0.0, 1.0, 0.0],
            yaw: 0.0,
            scale: 1.0,
            // 既定セットの先頭は grass_field（kind=Grass, align_to_normal=true）。
            prop_id: 0,
            seed: 1,
        }];

        // ─── ② 地面を 1.0m 掘り下げる（Subtract ブラシ相当）───
        let lowered_ground = original_ground - 1.0;
        let lowered = build_ground(lowered_ground);
        let lowered_field = TerrainScatterField::new(&lowered, &settings, &layers);

        let y_search = RESTICK_Y_SEARCH_VOXELS * vs;
        let removed = restick_instances(
            &lowered_field, &props, &mut instances, y_search,
        );

        assert_eq!(removed, 0, "地面はまだあるので削除されてはいけない");
        assert_eq!(instances.len(), 1, "インスタンスが消えた");
        assert!(
            (instances[0].pos[1] - lowered_ground).abs() < vs,
            "草が下がった地面へ追従していない: y={} 期待={lowered_ground}",
            instances[0].pos[1]
        );
        assert!(
            instances[0].pos[1] < hit[1],
            "草の高さが下がっていない（掘ったのに追従していない）"
        );
        // XZ は動かないこと（柱を真下に辿るだけなので水平位置は保存される）。
        assert_eq!(instances[0].pos[0], hit[0], "X がずれた");
        assert_eq!(instances[0].pos[2], hit[2], "Z がずれた");

        // ─── ③ 地面が探索窓の外まで消えたらインスタンスは削除されること ───
        //   全サンプルを AIR（density > iso）で埋めて「地面が無くなった」状態を作る。
        let mut empty_chunks = HashMap::new();
        empty_chunks.insert(
            ChunkCoord::new(0, 0, 0),
            TerrainChunkData::new_filled(&settings, iso + 1.0),
        );
        let empty_field = TerrainScatterField::new(&empty_chunks, &settings, &layers);

        let removed = restick_instances(
            &empty_field, &props, &mut instances, y_search,
        );
        assert_eq!(removed, 1, "足元の地面が消えたインスタンスは削除されること");
        assert!(instances.is_empty(), "宙に浮いた草が残っている");
    }

    /// 高速密度サンプラ `fast_density_at` が汎用 `sample_density_world` と
    /// **ビット単位で一致** することを保証する（境界・地形外・内部すべて）。
    ///
    /// これが散布最適化の安全網である。1 ビットでもずれると散布結果（＝草原の
    /// 見た目）が汎用パスと食い違い、保存済み／未保存チャンクで草が変わる。
    #[test]
    fn fast_density_matches_general_bit_exact() {
        let settings = TerrainSettings::default();
        let cells = settings.chunk_cells as i32;
        let vs = settings.voxel_size;
        let extent = settings.chunk_extent();
        let layers = TerrainLayerSet::default();

        // ─── 隣接する複数チャンク（穴あきも含む）を張る ───
        //   境界サンプルの共有と、地形外（欠けチャンク）への退避を両方踏ませる。
        let mut chunks: HashMap<ChunkCoord, TerrainChunkData> = HashMap::new();
        for cz in -1..=1 {
            for cy in -1..=1 {
                for cx in -1..=1 {
                    // (1,1,1) を意図的に欠けさせて地形外パスを踏ませる。
                    if (cx, cy, cz) == (1, 1, 1) {
                        continue;
                    }
                    let coord = ChunkCoord::new(cx, cy, cz);
                    let data = TerrainChunkData::from_fn(&settings, coord, |x, y, z| {
                        // 非自明な密度（各コーナーが違う値になるようにする）。
                        (x * 0.7).sin() + (y * 0.9).cos() * 1.3 + (z * 0.5).sin() * 0.6 + y * 0.05
                    });
                    chunks.insert(coord, data);
                }
            }
        }
        let field = TerrainScatterField::new(&chunks, &settings, &layers);

        // ─── 決定的な格子＋境界ちょうどの点を総当たりで比較 ───
        //   voxel の 1/3 刻みで内部・境界・チャンク跨ぎを網羅する。
        let step = vs / 3.0;
        let lo = -extent - vs;
        let hi = extent + vs;
        let mut n = 0u64;
        let mut p = lo;
        while p < hi {
            // 対角線上と、境界ちょうど（voxel/chunk 境界）を明示的に混ぜる。
            for &(x, y, z) in &[
                [p, p * 0.5 + 1.0, -p],
                [p, extent, p],           // y がチャンク境界ちょうど
                [extent, p, p],           // x がチャンク境界ちょうど
                [p, p, 0.0],              // z=0 境界
            ]
            .map(|a| (a[0], a[1], a[2]))
            {
                let fast = field.fast_density_at([x, y, z]);
                let slow = sample_density_world(&chunks, &settings, [x, y, z]);
                assert_eq!(
                    fast.to_bits(),
                    slow.to_bits(),
                    "fast≠slow at ({x},{y},{z}): fast={fast} slow={slow}"
                );
                n += 1;
            }
            p += step;
        }
        assert!(n > 500, "比較点が少なすぎる（テストの前提崩れ）: {n}");
        // cells は使っていることを明示（未使用警告回避＋境界網羅の意図）。
        assert!(cells > 0);
    }

    // ============================================================
    //  計測専用ベンチ（#[ignore]）— ルール散布のボトルネック特定
    // ============================================================

    /// ルール自動散布のホットパスを、実機と同じ `TerrainScatterField`
    /// （SipHash 付き `HashMap` バック）で計測する。
    ///
    /// `tests_scatter.rs` の `TestField` は密度を閉形式で返すため、
    /// 実機の支配項である「密度サンプルごとの HashMap 探索コスト」を一切
    /// 再現しない。ここでは本物のチャンクマップを組み、
    ///   * 生成時間（シリアル / rayon 並列）
    ///   * 生成インスタンス総数
    ///   * 1 インスタンスあたりの生成コスト
    /// を数値で出す。GPU アップロードと .tscatter 保存は別途 App 側の
    /// `SEED_PERF_TERRAIN` ログで測る（生成が支配項かを切り分けるため）。
    ///
    /// 実行:
    ///   cargo test -p seed_runtime terrain_scatter_ops::tests::bench_scatter_rules
    ///     -- --ignored --nocapture
    #[test]
    #[ignore = "計測専用。--ignored --nocapture で実行"]
    fn bench_scatter_rules_realistic() {
        use std::time::Instant;

        // ─── 実機と同じ既定設定（voxel 0.5m・chunk 32 セル・extent 16m）───
        let settings = TerrainSettings::default();
        let extent = settings.chunk_extent();
        let layers = TerrainLayerSet::default();

        // ─── 出荷 props.json をそのまま使う（実機の密度で測る）───
        let props = TerrainPropSet::from_json_str(include_str!(
            "../../../../../assets/terrain/props.json"
        ))
        .expect("props.json parse");
        let prop_indices: Vec<usize> = (0..props.active_count()).collect();

        // ─── 48 チャンク（XZ 8×6・単一 Y 層）に起伏地面を張る ───
        //   草は広い地面一面に生えるので、全チャンクが地表を含む配置が
        //   最も重い現実ケース（ユーザーが遭遇した「全面ルール散布」）。
        //   density = world_y - height（下が solid）。height は各チャンク
        //   中央付近（8m 前後）を通るサイン起伏。
        const CHUNKS_X: i32 = 8;
        const CHUNKS_Z: i32 = 6;
        let freq = std::f32::consts::TAU / (extent * 0.5);
        let mid = extent * 0.5;
        let mut chunks: HashMap<ChunkCoord, TerrainChunkData> = HashMap::new();
        for cz in 0..CHUNKS_Z {
            for cx in 0..CHUNKS_X {
                let coord = ChunkCoord::new(cx, 0, cz);
                // density(p) = p.y - h(x,z)。h は各チャンク中央付近を通るサイン起伏。
                let data = TerrainChunkData::from_fn(&settings, coord, |x, y, z| {
                    let h = mid
                        + (x * freq).sin() * (extent * 0.15)
                        + (z * freq).cos() * (extent * 0.15);
                    y - h
                });
                chunks.insert(coord, data);
            }
        }
        let coords: Vec<ChunkCoord> = {
            let mut v: Vec<ChunkCoord> = chunks.keys().copied().collect();
            v.sort_by_key(|c| (c.x, c.y, c.z));
            v
        };
        let seed: u64 = 0x5EED_1234_ABCD_0001;

        // ─── ① シリアル生成 ───
        let field = TerrainScatterField::new(&chunks, &settings, &layers);
        let t = Instant::now();
        let mut serial_total = 0usize;
        for &coord in &coords {
            serial_total +=
                scatter_chunk_by_rules(&field, &props, &prop_indices, coord, seed).len();
        }
        let serial_ms = t.elapsed().as_secs_f64() * 1000.0;

        // ─── ② rayon 並列生成（実機の handle_terrain_scatter_rules と同じ形）───
        let t = Instant::now();
        let par_total: usize = coords
            .par_iter()
            .map(|&coord| {
                scatter_chunk_by_rules(&field, &props, &prop_indices, coord, seed).len()
            })
            .sum();
        let par_ms = t.elapsed().as_secs_f64() * 1000.0;

        let chunk_count = coords.len();
        println!(
            "[BENCH scatter] chunks={chunk_count} props={} instances={serial_total} \
             serial={serial_ms:.1}ms parallel={par_ms:.1}ms \
             speedup={:.2}x per_inst_serial={:.2}us",
            prop_indices.len(),
            serial_ms / par_ms.max(0.001),
            serial_ms * 1000.0 / serial_total.max(1) as f64,
        );
        assert_eq!(serial_total, par_total, "並列と直列で本数が食い違う（決定性違反）");
    }

    /// 草 uniform がプロップ定義の値をそのまま運ぶこと。
    ///
    /// 特に `cross_planes` の bool → 枚数（1 or 2）展開を固定する。
    #[test]
    fn grass_uniform_maps_prop_fields() {
        let mut prop = TerrainProp::default();
        prop.grass.width = 0.04;
        prop.grass.height = 0.4;
        prop.grass.cross_planes = true;
        // 上限を超える分割数を書いても clamp されること。
        prop.grass.segments = 999;
        prop.wind.strength = 0.5;

        let u = grass_uniform_from_prop(&prop);
        assert_eq!(u.width, 0.04);
        assert_eq!(u.height, 0.4);
        assert_eq!(u.cross_planes, 2, "cross_planes=true は 2 枚");
        assert_eq!(
            u.segments,
            GRASS_MAX_SEGMENTS,
            "segments は GRASS_MAX_SEGMENTS へ clamp されること"
        );
        assert_eq!(u.wind_strength, 0.5);
        assert_eq!(u.time, 0.0, "time は 0 初期化（毎フレーム update_time が入れる）");

        prop.grass.cross_planes = false;
        assert_eq!(grass_uniform_from_prop(&prop).cross_planes, 1, "false は 1 枚");
    }

    // ============================================================
    //  散布モデル（kind=Model）— 姿勢行列と束ね
    // ============================================================

    /// 行列の列 j（ローカル基底ベクトル j のワールド像）を取り出す。
    fn col(m: &[[f32; 4]; 4], j: usize) -> [f32; 3] {
        [m[0][j], m[1][j], m[2][j]]
    }
    fn dot3(a: [f32; 3], b: [f32; 3]) -> f32 {
        a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
    }
    /// 3 基底ベクトルがそれぞれ長さ `scale`・互いに直交していることを検証する。
    fn assert_basis_orthonormal(m: &[[f32; 4]; 4], scale: f32) {
        let (x, y, z) = (col(m, 0), col(m, 1), col(m, 2));
        for (v, name) in [(x, "x"), (y, "y"), (z, "z")] {
            let len = dot3(v, v).sqrt();
            assert!((len - scale).abs() < 1.0e-5, "基底 {name} の長さ {len} != {scale}");
        }
        assert!(dot3(x, y).abs() < 1.0e-5, "x·y が非直交");
        assert!(dot3(y, z).abs() < 1.0e-5, "y·z が非直交");
        assert!(dot3(x, z).abs() < 1.0e-5, "x·z が非直交");
    }
    /// 3 基底の行列式（= x · (y × z)）。正なら右手系。
    fn basis_determinant(m: &[[f32; 4]; 4]) -> f32 {
        let (x, y, z) = (col(m, 0), col(m, 1), col(m, 2));
        let cr = [
            y[1] * z[2] - y[2] * z[1],
            y[2] * z[0] - y[0] * z[2],
            y[0] * z[1] - y[1] * z[0],
        ];
        dot3(x, cr)
    }

    /// 直立・無回転・等倍のとき、平行移動＝pos／up 列＝+Y／右手正規直交であること。
    #[test]
    fn model_matrix_upright_identity_pose() {
        let inst = ScatterInstance {
            pos: [1.0, 2.0, 3.0],
            normal: [0.0, 1.0, 0.0],
            yaw: 0.0,
            scale: 1.0,
            prop_id: 0,
            seed: 0,
        };
        let m = scatter_instance_to_model_matrix(&inst);
        // 平行移動は各行の col=3。
        assert_eq!([m[0][3], m[1][3], m[2][3]], [1.0, 2.0, 3.0], "平行移動が pos と不一致");
        assert_eq!(m[3], [0.0, 0.0, 0.0, 1.0], "最下行が [0,0,0,1] でない");
        // up 列（col=1）は +Y。
        let up = col(&m, 1);
        assert!(up[0].abs() < 1.0e-6 && (up[1] - 1.0).abs() < 1.0e-6 && up[2].abs() < 1.0e-6,
            "up={up:?}");
        assert_basis_orthonormal(&m, 1.0);
        assert!(basis_determinant(&m) > 0.0, "右手系でない（鏡映が入っている）");
    }

    /// 一様スケールが全基底に等しく掛かること（平行移動には掛からない）。
    #[test]
    fn model_matrix_applies_uniform_scale() {
        let inst = ScatterInstance {
            pos: [5.0, 0.0, -2.0],
            normal: [0.0, 1.0, 0.0],
            yaw: 0.7,
            scale: 2.5,
            prop_id: 0,
            seed: 0,
        };
        let m = scatter_instance_to_model_matrix(&inst);
        assert_basis_orthonormal(&m, 2.5);
        // 平行移動はスケール非依存。
        assert_eq!([m[0][3], m[1][3], m[2][3]], [5.0, 0.0, -2.0]);
    }

    /// 傾いた法線に対して up 列がその法線へ一致すること（斜面で寝る草・傾く木）。
    #[test]
    fn model_matrix_aligns_up_to_normal() {
        // 実装と同じ正規化を通してから渡す（テスト前提を実装に合わせる）。
        let n = normalize_or([0.3, 0.9, 0.2], NORMAL_FALLBACK_UP);
        let inst = ScatterInstance {
            pos: [0.0; 3],
            normal: n,
            yaw: 1.3,
            scale: 1.0,
            prop_id: 0,
            seed: 0,
        };
        let m = scatter_instance_to_model_matrix(&inst);
        let up = col(&m, 1);
        for i in 0..3 {
            assert!((up[i] - n[i]).abs() < 1.0e-5, "up[{i}]={} != normal[{i}]={}", up[i], n[i]);
        }
        assert_basis_orthonormal(&m, 1.0);
        assert!(basis_determinant(&m) > 0.0, "右手系でない");
    }

    /// yaw が up 軸まわりの回転になっていること（up は不変、右ベクトルが 90° 回る）。
    ///
    /// 基準となる forward の向きは実装の基底構築に依存する（プロップは乱数 yaw で
    /// 撒かれるので絶対向きに意味は無い）ため、テストは「up 不変」と「right が
    /// up 軸まわりに 90° 回転して元と直交する」という規約非依存の性質だけを見る。
    #[test]
    fn model_matrix_yaw_rotates_about_up() {
        use std::f32::consts::FRAC_PI_2;
        let base = ScatterInstance {
            pos: [0.0; 3],
            normal: [0.0, 1.0, 0.0],
            yaw: 0.0,
            scale: 1.0,
            prop_id: 0,
            seed: 0,
        };
        let m0 = scatter_instance_to_model_matrix(&base);
        let r0 = col(&m0, 0);
        // 直立時、右ベクトルは水平面内（up ⟂）にある。
        assert!(r0[1].abs() < 1.0e-5, "直立時の右ベクトルが水平でない: {r0:?}");

        let mut rot = base;
        rot.yaw = FRAC_PI_2;
        let m1 = scatter_instance_to_model_matrix(&rot);
        // up は回転で変わらない。
        let up = col(&m1, 1);
        assert!((up[1] - 1.0).abs() < 1.0e-5, "yaw で up が動いた: {up:?}");
        // right は up 軸まわりに 90° 回るので、元の right と直交する。
        let r1 = col(&m1, 0);
        assert!(dot3(r0, r1).abs() < 1.0e-5, "yaw=90° で右ベクトルが 90° 回っていない: {r0:?} -> {r1:?}");
        // 90° 回っても水平面内（up ⟂）を保つ。
        assert!(r1[1].abs() < 1.0e-5, "回転後の右ベクトルが水平から外れた: {r1:?}");
    }

    /// 縮退した法線（0 ベクトル）でも真上へフォールバックし、破綻しないこと。
    #[test]
    fn model_matrix_degenerate_normal_falls_back_up() {
        let inst = ScatterInstance {
            pos: [0.0; 3],
            normal: [0.0, 0.0, 0.0],
            yaw: 0.0,
            scale: 1.0,
            prop_id: 0,
            seed: 0,
        };
        let m = scatter_instance_to_model_matrix(&inst);
        let up = col(&m, 1);
        assert!((up[1] - 1.0).abs() < 1.0e-6, "縮退法線は真上へフォールバックすること: {up:?}");
        assert_basis_orthonormal(&m, 1.0);
    }

    /// 束ねは「kind=Model かつ model_path 非空」のプロップだけを対象にすること。
    ///
    /// 草・孤児（消えた prop_id）・model_path 未設定は除外される。
    #[test]
    fn gather_selects_only_model_props_with_path() {
        let props = TerrainPropSet {
            props: vec![
                TerrainProp { id: "g".into(), kind: PropKind::Grass, ..TerrainProp::default() },
                TerrainProp {
                    id: "m1".into(),
                    kind: PropKind::Model,
                    model_path: Some("models/A.gltf".into()),
                    ..TerrainProp::default()
                },
                TerrainProp {
                    id: "m2".into(),
                    kind: PropKind::Model,
                    model_path: None,
                    ..TerrainProp::default()
                },
            ],
        };
        let mk = |prop_id: u32| ScatterInstance {
            pos: [0.0; 3],
            normal: [0.0, 1.0, 0.0],
            yaw: 0.0,
            scale: 1.0,
            prop_id,
            seed: 0,
        };
        let mut scatter: HashMap<ChunkCoord, Vec<ScatterInstance>> = HashMap::new();
        // grass×1, model+path×2, model無path×1
        scatter.insert(ChunkCoord::new(0, 0, 0), vec![mk(0), mk(1), mk(1), mk(2)]);
        // 別チャンクに model+path×1 と孤児(prop_id=9)×1
        scatter.insert(ChunkCoord::new(1, 0, 0), vec![mk(1), mk(9)]);

        let settings = TerrainSettings::default();
        let by = gather_scatter_model_chunks(&scatter, &props, &settings);
        assert_eq!(by.len(), 1, "対象は model+path のプロップ 1 種のみ");
        // prop 1 は 2 チャンクに跨る（chunk(0,0,0)=2本, chunk(1,0,0)=1本）→ span 2 個・計 3 本。
        let spans = by.get(&1).expect("prop 1 の span 列");
        let total: usize = spans.iter().map(|s| s.mats.len()).sum();
        assert_eq!(total, 3, "prop 1 は全チャンク合計 3 本");
        assert_eq!(spans.len(), 2, "prop 1 は 2 チャンクに分かれる（span 2 個）");
        assert!(!by.contains_key(&0), "草プロップは除外されること");
        assert!(!by.contains_key(&2), "model_path 無しは除外されること");
        assert!(!by.contains_key(&9), "孤児 prop_id は除外されること");

        // span はチャンク座標順（(0,0,0) が先）。chunk(0,0,0) が 2 本、chunk(1,0,0) が 1 本。
        assert_eq!(spans[0].mats.len(), 2, "先頭 span は chunk(0,0,0) の 2 本");
        assert_eq!(spans[1].mats.len(), 1, "次 span は chunk(1,0,0) の 1 本");
        // chunk(0,0,0) の AABB はマージン込みで原点側に負のはみ出しを持つ。
        let e = settings.chunk_extent();
        assert!(spans[0].aabb_min[0] < 0.0, "AABB は margin ぶん負側へ広がる");
        assert!(spans[1].aabb_min[0] >= e - SCATTER_MODEL_MARGIN_HORIZ - 1.0,
            "chunk(1,0,0) の AABB は x 方向へ 1 チャンクぶんずれる");
    }

    // ============================================================
    //  遠景密度減衰（植生 LOD 第1段）— 間引き順の決定性
    // ============================================================

    /// 間引きハッシュが決定的で、隣接する種でも値が大きく撹拌されること。
    ///
    /// 決定性はちらつき防止の根拠（同じ seed は常に同じ順位＝間引かれる個体が不変）。
    /// 撹拌が効いていないと「seed の下位ビット順＝散布の生成順」に戻り、プレフィクスが
    /// 空間的に偏る（遠景に穴が空く）。
    #[test]
    fn scatter_thin_key_is_deterministic_and_spreads() {
        // 同じ入力は常に同じ出力。
        for s in [0u32, 1, 2, 100, 0xDEAD_BEEF, u32::MAX] {
            assert_eq!(scatter_thin_key(s), scatter_thin_key(s), "seed={s} が非決定的");
        }
        // 連番 seed でも出力は単調増加にならない（撹拌が効いている）＝生成順に戻らない。
        let keys: Vec<u32> = (0..64u32).map(scatter_thin_key).collect();
        let is_sorted = keys.windows(2).all(|w| w[0] <= w[1]);
        assert!(!is_sorted, "連番 seed のハッシュが単調（撹拌が効いていない）");
        // 衝突が起きていない（64 個の連番が全て相異なる）。
        let uniq: std::collections::HashSet<u32> = keys.iter().copied().collect();
        assert_eq!(uniq.len(), keys.len(), "連番 seed でハッシュ衝突が発生");
    }

    /// モデル束ねの `span.mats` 並びが入力順に依存せず決定的であること。
    ///
    /// 【これが守る不変条件】密度減衰は「先頭 kept 本だけ描く」ため、並びが実行ごとに
    /// 変われば「毎フレーム別個体が消える」＝ちらつく。散布データの格納順が変わっても
    /// （HashMap 走査順や保存/ロードの差で起こりうる）、ハッシュ順ソートにより
    /// `span.mats` は必ず同じ並びに落ち着くことを固定する。
    #[test]
    fn gather_orders_model_mats_deterministically() {
        let settings = TerrainSettings::default();
        let props = TerrainPropSet {
            props: vec![TerrainProp {
                id: "m".into(),
                kind: PropKind::Model,
                model_path: Some("models/A.gltf".into()),
                ..TerrainProp::default()
            }],
        };
        let mk = |x: f32, seed: u32| ScatterInstance {
            pos: [x, 0.0, 0.0],
            normal: [0.0, 1.0, 0.0],
            yaw: 0.0,
            scale: 1.0,
            prop_id: 0,
            seed,
        };
        // 同一チャンク内に 5 本。seed をわざとバラバラに与える。
        let base = [mk(1.0, 40), mk(2.0, 7), mk(3.0, 900), mk(4.0, 3), mk(5.0, 128)];

        // 入力順を変えた 2 つの scatter を作る。
        let mut a: HashMap<ChunkCoord, Vec<ScatterInstance>> = HashMap::new();
        a.insert(ChunkCoord::new(0, 0, 0), base.to_vec());
        let mut rev = base.to_vec();
        rev.reverse();
        let mut b: HashMap<ChunkCoord, Vec<ScatterInstance>> = HashMap::new();
        b.insert(ChunkCoord::new(0, 0, 0), rev);

        let ga = gather_scatter_model_chunks(&a, &props, &settings);
        let gb = gather_scatter_model_chunks(&b, &props, &settings);
        let ma = &ga.get(&0).unwrap()[0].mats;
        let mb = &gb.get(&0).unwrap()[0].mats;
        assert_eq!(ma.len(), 5);
        // 入力順に関わらず並びが一致する＝決定的。
        assert_eq!(ma, mb, "入力順で span.mats の並びが変わった（ちらつきの原因）");

        // 並びが seed のハッシュ順であること（先頭 = 最小ハッシュの個体の x 平行移動）。
        let mut expected: Vec<(u32, f32)> =
            base.iter().map(|i| (scatter_thin_key(i.seed), i.pos[0])).collect();
        expected.sort_by(|p, q| p.0.cmp(&q.0));
        for (k, (_, x)) in expected.iter().enumerate() {
            assert_eq!(ma[k][0][3], *x, "{k} 番目の個体がハッシュ順に並んでいない");
        }
    }
}
