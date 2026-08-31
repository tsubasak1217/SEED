// ============================================================
//  terrain_cover_ops.rs — 地表カバー場のエンジン統合層（Phase I3.1）
//
//  【責務】
//    純粋データ層（`engine::terrain::cover`）と ECS / GPU / ファイル IO を橋渡しする。
//      ① cover_materials.json の読み込み（データドリブンの入口）
//      ② `CoverEmitterComponent` → `CoverEmitSpec` のワールド解決（マスク画像のロード込み）
//      ③ 積算の駆動（Edit のシミュレートボタン / Play 中の毎フレーム）
//      ④ カバー場 → 地形メッシュ頂点への焼き込み（描画への反映）
//      ⑤ .tcover の保存・読み込み（.tvox / .tscatter と同じディレクトリ・同じ流儀）
//      ⑥ Play 中の積算を揮発させるスナップショット / 復元
//
//  【CPU で積算する理由（GPU コンピュートを採らなかった判断）】
//    1 チャンクのカバー場は 32×32 = 1024 テクセルしか無い。既定の地面 48 チャンクでも
//    5 万テクセル弱で、1 テクセルあたりの計算は数個の乗算と比較だけである。
//    一方 GPU 化すると、保存（.tcover）のたびにテクスチャの読み戻し（非同期マップ）が
//    要り、「シミュレートボタンを押して N 秒ぶん即時計算する」も GPU の往復回数分の
//    フレームを跨ぐことになる。積算・保存・テストを 1 本の同期コードで完結させる
//    ほうが、この規模では明確に有利と判断した。
//    実測は `SEED_PERF_LOG=1` の `[PERF f=...] cover=...ms` で監視できる。
//
//  【描画への反映が「頂点の焼き直し」である理由】
//    docs/cover_field.md と terrain_mesh_build.rs::rebuild_terrain_model_with_cover の
//    コメントを参照。要約すると、変位を頂点位置へ焼くことで
//    シャドウ・深度・ID・RT のすべてが自動的に一致するためである。
//    頂点の焼き直しは 32×32 の積算より重いので、`COVER_APPLY_INTERVAL_SEC` で間引く。
// ============================================================

use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use std::time::Instant;

use crate::engine::components::cover_emitter_component::{
    CoverEmitterComponent, CoverEmitterRangeKind,
};
use crate::engine::components::{ComponentKind, ModelComponent, Transform};
use crate::engine::core::loader::model::Model;
use crate::engine::structs::objects::Actor;
use crate::engine::terrain::chunk_coord::ChunkCoord;
use crate::engine::terrain::cover::{
    accumulate_chunk, advance_accumulate_tick, chunk_has_active_emitter, cover_y_match_tolerance,
    plan_cover_bake, read_cover_chunk, resolve_forward_xz, stamp_cover_chunk_tracked,
    write_cover_chunk, CoverEmitRange, CoverEmitSpec, CoverField, CoverMask, CoverMaterialSet,
    CoverNeighborhood, CoverStampShape, CoverStampSpec, CoverSurface,
    COVER_BAKE_CHUNK_BUDGET_PER_FRAME,
};
use crate::engine::components::interaction_source_component::{
    InteractionSourceComponent, InteractionStampShape,
};

use crate::engine::core::app_base::undo::CoverFieldEditCommand;

use super::terrain_mesh_build::rebuild_terrain_model_with_cover;
use super::App;

// ─── アセットの所在（props.json / layers.json と同じ流儀）─────────────────────

/// カバー素材定義アセットの既定パス。
const COVER_MATERIALS_ASSET: &str = "assets://terrain/cover_materials.json";

/// カバー素材定義の差し替え用環境変数（開発時の実験用）。
const COVER_MATERIALS_PATH_ENV: &str = "SEED_TERRAIN_COVER_MATERIALS";

// ─── 調整用定数（マジックナンバー禁止）───────────────────────────────────────

/// カバー場を頂点へ焼き直す最短間隔（秒）。
///
/// 積算そのもの（32×32 の加算）は毎フレームやっても無視できるが、頂点の焼き直しは
/// 「チャンクの全頂点を作り直して GPU へ再アップロード」であり、可視チャンク数に
/// 比例して重い。雪が積もる速さに対して 10Hz で十分に滑らかに見えるため、
/// ここで間引く（1 フレームぶんの積み増しは肉眼では判別できない）。
/// （undo/redo の巻き戻しでも同じ即時反映の流儀を使うため `pub(super)`。）
pub(super) const COVER_APPLY_INTERVAL_SEC: f32 = 0.1;

/// Edit のシミュレートボタンで「秒数指定」したときの標準の 1 ステップ時間（秒）。
///
/// 指定秒数を一気に 1 ステップで計算すると、素材置き換え規則
/// （古い素材を削ってから新素材が乗る）が 1 回しか適用されず、
/// 「落ち葉の上に雪」の中間状態が飛ぶ。Play と同じ 60Hz 相当で刻む。
const COVER_SIMULATE_STEP_SEC: f32 = 1.0 / 60.0;

/// 秒数指定シミュレートの 1 回あたりのステップ数上限。
///
/// 【なぜ上限を切るのか】1 ステップは「全チャンク × 32×32 テクセル」を舐める。
/// 既定の 48 チャンクで約 5 万テクセルなので、60Hz 刻みのまま 1 時間ぶんを
/// 指定されると 21 万ステップ＝100 億テクセル演算となり、エディタが数分固まる。
///
/// 【刻みを粗くしても壊れない理由】自然減衰が無く、積算は
/// `量 += 被覆率 × 強度 × dt × 傾斜` の単調加算なので、刻み幅を変えても
/// **飽和前の合計量は厳密に同じ**である。刻み数が効くのは異素材の
/// 置き換え（削り → 乗せ）の途中経過だけで、そこには 600 ステップあれば十分。
const COVER_SIMULATE_MAX_STEPS: u32 = 600;

/// 秒数指定シミュレートで受け付ける秒数の上限。
///
/// 自然減衰が無いため、どんな強度でもこれだけ回せば必ず飽和している
/// （既定強度 0.2/秒 なら 5 秒で満量）。非有限値・負値の防波堤も兼ねる。
const COVER_SIMULATE_MAX_SECONDS: f32 = 3600.0;

/// 即時ステップ（`TERRAIN_COVER_STEP`）で意味を持つ最小の秒数。
///
/// これ以下（0 秒・負値）が来ても何も積もらないので、計算も通知もせず素通しする。
const COVER_STEP_MIN_SECONDS: f32 = 0.0;

/// 連続シミュレート中の 1 フレームあたりの時間刻み上限（秒）。
///
/// フレーム落ちした瞬間に何秒ぶんも一気に積もると、押した時間と結果が対応しなくなる。
const COVER_SIMULATE_MAX_DT: f32 = 1.0 / 20.0;

/// ミリ秒換算（計測ログ用）。
const MILLIS_PER_SEC: f64 = 1000.0;

// ─── 轍スタンプ（I3.2）の調整用定数 ──────────────────────────────────────────

/// スタンプを行う最小の移動量（メートル / フレーム）。
///
/// 【なぜ「移動時のみ」なのか】
///   その場に立っているだけのアクタで毎フレーム 32×32 を舐めるのは無駄であり、
///   踏み固めは最大値更新なので静止中に走らせても結果が 1 ビットも変わらない。
///   閾値は 60Hz で 0.06m/s（＝ほぼ静止）に相当する小さな値にしてあり、
///   ゆっくり歩いても轍は途切れない。
const COVER_STAMP_MIN_MOVE: f32 = 0.001;

/// 轍スタンプ源の「作用中」表示を保持する時間（秒）。
///
/// 【なぜ保持が要るのか】
///   スタンプが実際にカバー場を変えるのは「新しいテクセルへ踏み込んだ」フレーム
///   だけであり、同じ場所を踏み続けている間は 1 ビットも変わらない（最大値更新のため）。
///   その生の真偽値をそのまま色にすると、ギズモが 60Hz で激しく明滅して
///   「効いているのか」がまったく読み取れない。直近に押した事実を短時間だけ
///   保持することで、動いている間は安定して作用色に見えるようにする。
const COVER_STAMP_DEBUG_HOLD_SEC: f32 = 0.25;

// ============================================================
//  カバー適用前のメッシュ基準値（変位の累積を防ぐキャッシュ）
// ============================================================

/// カバーを適用する前のチャンクメッシュの基準値。
///
/// 【なぜ必要か】
///   変位は「基準位置 ＋ 法線 × 量 × 高さ」で作る。もし現在の（既に変位済みの）
///   位置へ足し込むと、適用のたびに雪が伸び続けて地形が破裂する。
///   毎回この基準へ戻してから足し直すことで、適用回数に依らず結果が一意になる。
///
///   平均アルベドも同じ理由で「カバー無しの値」を覚えておく必要がある
///   （カバー色へ寄せた値を基準にすると、適用のたびに白へ漸近してしまう）。
pub struct CoverBaseMesh {
    /// カバー適用前の頂点位置（チャンクローカル座標。頂点列と同順・同長）。
    positions: Arc<Vec<[f32; 3]>>,
    /// カバー適用前の頂点法線（頂点列と同順・同長）。
    ///
    /// 位置とまったく同じ理由で基準値が要る。カバーの高さ場の勾配で法線を摂動する
    /// （＝足跡の壁に陰影を出す）ため、現在の法線を入力にすると適用のたびに
    /// 摂動が積み重なって法線が倒れ続けてしまう。
    normals: Arc<Vec<[f32; 3]>>,
    /// カバー適用前のチャンク平均アルベド（リニア RGB）。
    avg_albedo: [f32; 3],
}

// ============================================================
//  App — カバー場のエンジン統合
// ============================================================

impl App {
    // ─── ① 素材定義の読み込み ───────────────────────────────────────────────

    /// cover_materials.json を読み込んで `terrain.cover_materials` へ格納する。
    ///
    /// 読み込み元は環境変数 `SEED_TERRAIN_COVER_MATERIALS` >
    /// `assets://terrain/cover_materials.json` の順で解決する
    /// （`ensure_terrain_props` / `ensure_terrain_layers` と完全に同じ流儀）。
    /// 読めなければ `CoverMaterialSet::default()`（雪・落ち葉・濡れの 3 種）へ
    /// フォールバックし、警告は 1 回だけ出す。
    pub(super) fn ensure_cover_materials(&mut self) {
        let source = std::env::var(COVER_MATERIALS_PATH_ENV)
            .ok()
            .filter(|p| !p.is_empty())
            .unwrap_or_else(|| COVER_MATERIALS_ASSET.to_string());

        let set = match crate::engine::asset_fs::read_string(&source) {
            Ok(text) => match CoverMaterialSet::from_json_str(&text) {
                Ok(set) => set,
                Err(e) => {
                    self.warn_cover_materials_once(&format!("parse failed ({source}): {e}"));
                    CoverMaterialSet::default()
                }
            },
            Err(_) => {
                self.warn_cover_materials_once(&format!("not found: {source}（既定セットを使用）"));
                CoverMaterialSet::default()
            }
        };
        self.terrain.cover_materials = set;
    }

    /// カバー素材定義の警告を 1 回だけ出す（ログを埋めないため）。
    fn warn_cover_materials_once(&mut self, message: &str) {
        if self.terrain.cover_materials_warned {
            return;
        }
        self.terrain.cover_materials_warned = true;
        eprintln!("[SEED terrain] cover_materials.json {message}");
    }

    // ─── ② エミッタのワールド解決 ───────────────────────────────────────────

    /// シーンの全アクタを走査し、有効なカバーエミッタをワールド解決して集める。
    ///
    /// スキップ規則は他の収集処理（`collect_interaction_sources` / `collect_water_volumes`）と
    /// 完全に揃える:
    ///   ・world_line が一致しないルートは対象外
    ///   ・active=false のアクターはサブツリーごとスキップ（祖先の非アクティブも伝播）
    ///   ・enabled=false のスロットはスキップ
    ///   ・コンポーネント側の enabled=false もスキップ
    ///   ・強度 0 以下・未定義の素材 ID はスキップ（積算で何も起きないため）
    pub(super) fn collect_cover_emitters(&mut self) -> Vec<CoverEmitSpec> {
        // ─── マスク画像の要求パスを先に洗い出して読み込む ───
        //   `&mut self`（マスクキャッシュ）と `&self.scene`（走査）を同時に借りられないため、
        //   「必要なパスの収集 → ロード → 仕様の組み立て」の 3 段に分ける。
        let raw = {
            let Some(scene) = self.scene.as_ref() else { return Vec::new() };
            let wl = self.active_world_line;
            let mut out: Vec<RawCoverEmitter> = Vec::new();
            for root in scene.actors.iter().filter(|a| a.world_line == wl) {
                collect_cover_in_actor(root, &scene.world, &mut out, true);
            }
            out
        };
        if raw.is_empty() {
            return Vec::new();
        }

        // ─── マスク画像をキャッシュへ確保する ───
        for r in &raw {
            if r.range_kind == CoverEmitterRangeKind::TextureMask && !r.mask_path.is_empty() {
                self.ensure_terrain_mask(&r.mask_path);
            }
        }

        // ─── 素材 ID を添字へ解決し、仕様を組み立てる ───
        let mut out = Vec::with_capacity(raw.len());
        for r in raw {
            let Some(material_index) = self.terrain.cover_materials.index_of(&r.material_id)
            else {
                // 未定義の素材 ID。何も積もらないので落とす（警告は出さない＝
                // アセット編集中に ID を打ち替えている最中にログが埋まるのを避ける）。
                continue;
            };
            let range = match r.range_kind {
                CoverEmitterRangeKind::Global => CoverEmitRange::Global,
                CoverEmitterRangeKind::Region => CoverEmitRange::Region {
                    center: r.world_pos,
                    half_extents: r.extents,
                    fade: r.fade,
                },
                CoverEmitterRangeKind::TextureMask => {
                    let mask = self
                        .terrain
                        .mask_cache
                        .get(&r.mask_path)
                        .cloned()
                        .unwrap_or_else(CoverMask::empty);
                    // マスクが無効（未設定・読み込み失敗）ならこのエミッタは何も降らせない。
                    if !mask.is_valid() {
                        continue;
                    }
                    CoverEmitRange::TextureMask {
                        center: r.world_pos,
                        size_xz: r.mask_size,
                        mask,
                    }
                }
            };
            out.push(CoverEmitSpec {
                range,
                material_index: material_index as u8,
                rate: r.strength,
            });
        }
        out
    }

    /// マスク画像をデコードしてキャッシュへ入れる（既にあれば何もしない）。
    ///
    /// 読み込みに失敗した場合も「無効なマスク」をキャッシュへ入れる。
    /// こうしないと毎フレーム同じ壊れたパスを読み直してしまう
    /// （`scatter_model_failed` と同じ考え方）。
    ///
    /// 【カバー専用ではない】
    ///   カバーエミッタ・轍スタンプに加え、地形ペイント系ブラシの形状マスク
    ///   （`terrain_brush_mask_ops.rs`）も本関数を通してデコードする。
    ///   デコード結果は用途を問わない汎用のグレースケールなので、
    ///   キャッシュ（`TerrainState::mask_cache`）ごと共用する。
    pub(super) fn ensure_terrain_mask(&mut self, path: &str) {
        if self.terrain.mask_cache.contains_key(path) {
            return;
        }
        let mask = match crate::engine::asset_fs::read_image_result(path) {
            Ok(img) => {
                let (w, h) = (img.width() as usize, img.height() as usize);
                // RGBA から輝度を取らず **R チャンネル**をそのまま使う。
                // グレースケール画像では R=G=B であり、カラー画像を入れられた場合も
                // 「赤成分をマスクとして使う」という単純で予測可能な規則になる。
                let pixels: Vec<u8> = img.pixels().map(|p| p.0[0]).collect();
                CoverMask { width: w, height: h, pixels }
            }
            Err(e) => {
                eprintln!("[SEED terrain] cover mask load failed: {path} err={e}");
                CoverMask::empty()
            }
        };
        self.terrain.mask_cache.insert(path.to_string(), mask);
    }

    // ─── ③ 積算の駆動 ───────────────────────────────────────────────────────

    /// カバー場を `dt` 秒ぶん進める（毎フレーム呼ばれる）。
    ///
    /// - `play_running`: Play かつ非ポーズか。true なら Play の毎フレーム積算を行う。
    ///
    /// Edit 中は `terrain.cover_sim_running`（シミュレートボタン）が立っているあいだだけ進む。
    /// **どちらも false のフレームは 1 命令も走らない**（エミッタ収集すら行わない）。
    ///
    /// 【積算はティック、轍スタンプは毎フレーム】
    ///   エミッタからの積算（降雪など）は `CoverMaterialSet::accumulate_interval_sec()` の
    ///   間隔でしか走らない。1 フレームぶんの積み増しは量の量子化（1/255）より細かく、
    ///   絵が 1 ピクセルも変わらないのに全チャンク走査とエミッタ収集を払っていたためである。
    ///   一方 `InteractionSource` による轍スタンプは「踏んだ瞬間に凹む」応答性が要るので
    ///   **毎フレームのまま**にする（変化したチャンクだけが焼き直される点はスタンプ側で担保）。
    ///
    /// 戻り値は積算に要した時間（ミリ秒。計測ログ用）。
    pub(super) fn tick_terrain_cover(&mut self, dt: f32, play_running: bool) -> f64 {
        let running = play_running || self.terrain.cover_sim_running;
        if !running || self.terrain.chunks.is_empty() {
            return 0.0;
        }
        let t = Instant::now();
        // フレーム落ち時に何秒ぶんも一気に積もらないよう刻みを制限する。
        let step = dt.clamp(0.0, COVER_SIMULATE_MAX_DT);
        // ─── 積算ティック（性能）───
        //   貯めた時間が間隔に達したフレームだけ、貯めた全量をまとめて積む。
        //   返る秒数はタイマの全量なので、積算総量は毎フレーム実行時と厳密に一致する。
        let interval = self.terrain.cover_materials.accumulate_interval_sec();
        if let Some(tick_dt) =
            advance_accumulate_tick(&mut self.terrain.cover_accum_timer, step, interval)
        {
            // ティックが来たフレームだけ計上される（来ないフレームはスコープ自体が無い）。
            crate::profile_scope!("地形/カバー積算ティック");
            let emitters = self.collect_cover_emitters();
            self.accumulate_cover(&emitters, tick_dt);
        }
        // ─── 轍のスタンプ（I3.2。Play 中のみ）───
        //   Edit 中の轍オーサリングはスコープ外なので、シミュレートボタンでは動かさない
        //   （エミッタと違って「どこを歩いたか」は Play でしか生まれない情報である）。
        if play_running && step > 0.0 {
            // 轍スタンプは毎フレーム走る（応答性優先）ので、積算ティックとは別枠で計測する。
            crate::profile_scope!("地形/轍スタンプ");
            // 前フレームの「作用中」表示を先に減衰させる。押した事実はこのあと
            // 上書きで立て直されるので、順序はこちらが先でなければならない。
            self.decay_cover_stamp_debug(step);
            let jobs = self.collect_cover_stamps(step);
            self.apply_cover_stamps(&jobs);
        }
        t.elapsed().as_secs_f64() * MILLIS_PER_SEC
    }

    /// 積算ティックの未消化端数を、いま 1 回だけ積んでタイマを空にする。
    ///
    /// シミュレート停止のように「ここで時間の進行が終わる」場面から呼ぶ。
    /// 端数が 0 のときは何もしない（エミッタ収集も走らない）。
    fn flush_cover_accum_residual(&mut self) {
        let residual = self.terrain.cover_accum_timer;
        self.terrain.cover_accum_timer = 0.0;
        if !(residual > 0.0) || self.terrain.chunks.is_empty() {
            return;
        }
        let emitters = self.collect_cover_emitters();
        self.accumulate_cover(&emitters, residual);
    }

    /// 轍スタンプ源の「作用中」表示を `dt` 秒ぶん減衰させる（デバッグ描画用）。
    ///
    /// 保持時間を使い切ったソースはマップから消す（＝ギズモは通常色へ戻る）。
    /// 観測用の状態しか触らないので、シミュレーション結果には影響しない。
    fn decay_cover_stamp_debug(&mut self, dt: f32) {
        if self.terrain.cover_stamp_debug.is_empty() {
            return;
        }
        let dt = dt.max(0.0);
        self.terrain.cover_stamp_debug.retain(|_, d| {
            d.stamped_hold = (d.stamped_hold - dt).max(0.0);
            d.moving_hold = (d.moving_hold - dt).max(0.0);
            d.stamped_hold > 0.0 || d.moving_hold > 0.0
        });
    }

    // ─── ③-2 轍のスタンプ（I3.2）─────────────────────────────────────────────

    /// 動いている `InteractionSource` から、このフレームのスタンプ仕様を組み立てる。
    ///
    /// 【インタラクションフィールド（草・波紋）と収集を共有しない理由】
    ///   あちらの収集はフレーム後半（描画ブロックの中）で走り、速度トラッカーは
    ///   1 フレーム 1 回しか更新できない状態を持つ。ここから呼ぶと二重更新になって
    ///   草の速度が壊れる。走査自体はアクタ木の DFS だけで軽く、
    ///   前フレーム位置も轍専用に持ったほうが「移動時のみスタンプ」を素直に書ける。
    ///   ＝**同じデータ（InteractionSource）を、別々の消費者が別々に読む**形にしてある。
    fn collect_cover_stamps(&mut self, dt: f32) -> Vec<CoverStampJob> {
        // ─── ① シーン走査（読み取りのみ）───
        let raw = {
            let Some(scene) = self.scene.as_ref() else { return Vec::new() };
            let wl = self.active_world_line;
            let mut out: Vec<RawStampSource> = Vec::new();
            let mut dfs = 0u32;
            for root in scene.actors.iter().filter(|a| a.world_line == wl) {
                collect_stamp_in_actor(root, &scene.world, &mut out, &mut dfs, true);
            }
            out
        };

        // ─── ② 消えたソースの追跡情報を捨てる（Play を跨いだ蓄積を防ぐ）───
        if raw.is_empty() {
            self.terrain.cover_stamp_tracks.clear();
            self.terrain.cover_stamp_debug.clear();
            return Vec::new();
        }
        let alive: HashSet<u64> = raw.iter().map(|r| r.key).collect();
        self.terrain.cover_stamp_tracks.retain(|k, _| alive.contains(k));
        self.terrain.cover_stamp_debug.retain(|k, _| alive.contains(k));

        // ─── ③ マスク画像を確保する（エミッタのマスクと同じキャッシュを使う）───
        for r in &raw {
            if r.shape == InteractionStampShape::Texture && !r.mask_path.is_empty() {
                self.ensure_terrain_mask(&r.mask_path);
            }
        }

        // ─── ④ 移動量から向きを決めてスタンプ仕様を作る ───
        let mut out = Vec::new();
        for r in raw {
            let track = self.terrain.cover_stamp_tracks.get(&r.key).copied();
            let previous_forward = track.map(|t| t.forward);
            let delta_xz = match track {
                Some(t) => [r.world_pos[0] - t.last_pos[0], r.world_pos[2] - t.last_pos[2]],
                // 初出のソースは「動いていない」扱い（出現した瞬間に痕を残さない）。
                None => [0.0, 0.0],
            };
            let moved = (delta_xz[0] * delta_xz[0] + delta_xz[1] * delta_xz[1]).sqrt();
            let forward = resolve_forward_xz(delta_xz, dt, previous_forward);

            // 追跡情報は動いていなくても必ず更新する（次フレームの差分の基準になる）。
            self.terrain
                .cover_stamp_tracks
                .insert(r.key, CoverStampTrack { last_pos: r.world_pos, forward });

            // 静止中はスタンプしない（結果が変わらないので走らせるだけ無駄）。
            if moved < COVER_STAMP_MIN_MOVE {
                continue;
            }

            // ─── デバッグ表示: 動いている＝インタラクションフィールド（草・波紋）へ
            //     書き込んでいる状態。轍が押されたかどうかとは独立に記録する。
            self.terrain
                .cover_stamp_debug
                .entry(r.key)
                .or_default()
                .moving_hold = COVER_STAMP_DEBUG_HOLD_SEC;

            let shape = match r.shape {
                InteractionStampShape::Circle => CoverStampShape::Circle,
                InteractionStampShape::Texture => {
                    let mask = self
                        .terrain
                        .mask_cache
                        .get(&r.mask_path)
                        .cloned()
                        .unwrap_or_else(CoverMask::empty);
                    // マスクが無効（未設定・読み込み失敗）なら痕を付けない。
                    if !mask.is_valid() {
                        continue;
                    }
                    CoverStampShape::Texture { size: r.stamp_size, forward_xz: forward, mask }
                }
            };
            out.push(CoverStampJob {
                key: r.key,
                spec: CoverStampSpec {
                    contact: r.world_pos,
                    radius: r.radius,
                    strength: r.strength,
                    shape,
                },
            });
        }
        out
    }

    /// スタンプ仕様を、接地しうるチャンクにだけ適用する。
    ///
    /// 【全チャンクを舐めない理由（性能）】
    ///   積算（天候）は世界全体に降りうるが、轍は接地点の周囲 1〜2 メートルの話である。
    ///   スタンプの届く AABB と交差するチャンクだけを選べば、
    ///   既定 48 チャンクのうち触るのは 1〜4 個で済む。
    ///   触ったチャンクだけを既存の `queue_cover_apply` 経路へ載せるので、
    ///   頂点の焼き直しも接地点の周りだけに閉じる。
    fn apply_cover_stamps(&mut self, jobs: &[CoverStampJob]) {
        if jobs.is_empty() {
            return;
        }
        // 純粋層へ渡すのは仕様だけ（キーはデバッグ記録の紐づけにしか使わない）。
        let stamps: Vec<CoverStampSpec> = jobs.iter().map(|j| j.spec.clone()).collect();
        // どのスタンプが実際に場を変えたか（ギズモの「作用中」色替えの根拠）。
        // 全チャンクぶんを OR で累積する。
        let mut hits = vec![false; stamps.len()];
        let t = Instant::now();
        let settings = self.terrain.settings.clone();
        let extent = settings.chunk_extent();
        let materials = self.terrain.cover_materials.clone();

        // ─── ① スタンプの AABB に触れるチャンクだけを選ぶ ───
        let coords: Vec<ChunkCoord> = self
            .terrain
            .chunks
            .keys()
            .copied()
            .filter(|coord| {
                let origin = coord.world_origin(&settings);
                let max = [origin[0] + extent, origin[1] + extent, origin[2] + extent];
                stamps.iter().any(|s| !s.is_outside_aabb(origin, max))
            })
            .collect();
        if coords.is_empty() {
            return;
        }

        // ─── ② 面情報を用意して押し当てる ───
        for coord in &coords {
            self.ensure_cover_surface(*coord);
        }
        let mut changed: Vec<ChunkCoord> = Vec::new();
        for coord in coords {
            // カバー場が無いチャンク（何も積もっていない）には轍も付かない。
            if !self.terrain.cover.contains_key(&coord) {
                continue;
            }
            let Some(surface) = self.terrain.cover_surface.get(&coord) else { continue };
            let origin = coord.world_origin(&settings);
            let Some(field) = self.terrain.cover.get_mut(&coord) else { continue };
            if stamp_cover_chunk_tracked(
                field, surface, origin, extent, &stamps, &materials, &mut hits,
            ) {
                changed.push(coord);
            }
        }
        for coord in changed {
            self.terrain.cover_dirty.insert(coord);
            self.queue_cover_apply(coord);
            // 轍は「踏んだ瞬間に跡が付く」ことが体感の核なので、焼き直しのフレーム予算から
            // 除外して必ず今フレームに焼く。境界整合のため、隣接して一緒に焼く必要がある
            // 26 近傍も同じ即時扱いにする（成分ごと即時になるため分割されない）。
            self.terrain.cover_immediate_apply.insert(coord);
        }

        // ─── ③ 「今まさに轍を押した」ソースを記録する（デバッグ描画用）───
        for (job, hit) in jobs.iter().zip(hits.iter()) {
            if !hit {
                continue;
            }
            self.terrain
                .cover_stamp_debug
                .entry(job.key)
                .or_default()
                .stamped_hold = COVER_STAMP_DEBUG_HOLD_SEC;
        }
    }

    /// 全チャンクのカバー場を `dt` 秒ぶん進める（純粋関数 `accumulate_chunk` の駆動）。
    ///
    /// 変化したチャンクだけを「未保存」と「頂点焼き直し待ち」にマークする。
    ///
    /// 【エミッタが届かないチャンクは一切触らない】
    ///   以前は全チャンクへ `entry().or_default()` で空のカバー場を作ってから
    ///   `accumulate_chunk` の早期棄却に頼っていた。これだと 1 テクセルも積もらない
    ///   遠方チャンクにも `CoverField`（4 配列 × 1024 テクセル）が居座り、
    ///   地表情報（`CoverSurface`）の生成まで走ってしまう。
    ///   `chunk_has_active_emitter`（純関数・早期棄却とまったく同じ条件）で
    ///   先に落とすことで、確保も走査も起きない。
    fn accumulate_cover(&mut self, emitters: &[CoverEmitSpec], dt: f32) {
        if emitters.is_empty() {
            return;
        }
        let settings = self.terrain.settings.clone();
        let extent = settings.chunk_extent();
        // 素材定義は積算中に `&mut self.terrain.cover` と同時に借りられないので複製する
        // （素材は十数件の小さな構造体であり、複製コストは無視できる）。
        let materials = self.terrain.cover_materials.clone();

        // エミッタが届くチャンクだけへ絞り込む（届かないチャンクは場も面情報も作らない）。
        let coords: Vec<ChunkCoord> = self
            .terrain
            .chunks
            .keys()
            .copied()
            .filter(|coord| {
                chunk_has_active_emitter(coord.world_origin(&settings), extent, emitters)
            })
            .collect();
        if coords.is_empty() {
            return;
        }
        // 地表情報（密度場の派生キャッシュ）を必要なぶんだけ用意する。
        for coord in &coords {
            self.ensure_cover_surface(*coord);
        }

        let mut changed: Vec<ChunkCoord> = Vec::new();
        for coord in coords {
            let Some(surface) = self.terrain.cover_surface.get(&coord) else { continue };
            let origin = coord.world_origin(&settings);
            let field = self.terrain.cover.entry(coord).or_default();
            if accumulate_chunk(field, surface, origin, extent, emitters, &materials, dt) {
                changed.push(coord);
            }
        }
        for coord in changed {
            self.terrain.cover_dirty.insert(coord);
            self.queue_cover_apply(coord);
        }
    }

    /// カバー場が変化したチャンクを「頂点への焼き直し待ち」へ入れる。
    ///
    /// 【隣接チャンクも一緒に入れる理由】
    ///   チャンク境界の頂点は隣のカバー場も読む（`CoverNeighborhood`）。
    ///   変化したチャンクだけを焼き直すと、隣のメッシュは古い値のまま残り、
    ///   境界の複製頂点どうしで変位が食い違って**そこだけ隙間が開く**。
    ///   境界の一致は「両側が同じ世代のカバー場を読んでいる」ことが前提なので、
    ///   26 近傍（XZ の 8 近傍 × 上下 3 段。角を含む）を必ず一緒に焼き直す。
    ///   存在しないチャンクは入れない（焼く対象が無い）。
    ///
    /// 【上下段も入れる理由（I3.2）】
    ///   Y 照合の導入で、境界面上の頂点は**上下段のカバー場も候補として読む**ように
    ///   なった。下段の雪が変わったのに上段のメッシュを焼き直さないと、
    ///   境界面上の複製頂点が世代違いの値を読んで筋が出る（横方向とまったく同じ理屈）。
    /// （カバーブラシ `terrain_cover_brush_ops.rs` も同じ経路を通すため `pub(super)`。）
    pub(super) fn queue_cover_apply(&mut self, coord: ChunkCoord) {
        self.terrain.cover_pending_apply.insert(coord);
        for dy in -1..=1i32 {
            for dz in -1..=1i32 {
                for dx in -1..=1i32 {
                    if dx == 0 && dy == 0 && dz == 0 {
                        continue;
                    }
                    let neighbor = ChunkCoord::new(coord.x + dx, coord.y + dy, coord.z + dz);
                    if self.terrain.chunks.contains_key(&neighbor) {
                        self.terrain.cover_pending_apply.insert(neighbor);
                    }
                }
            }
        }
    }

    /// 指定チャンクの地表情報（密度場からの派生）をキャッシュへ用意する。
    ///
    /// 地表情報を **新しく作った**ときは、そのチャンクのカバー場の基準 Y も
    /// 併せて同期する（I3.2）。基準 Y は「面のワールド高さ」であり、
    /// 地形が変われば当然変わる派生値なので、面情報と同じ寿命で扱う。
    /// （カバーブラシ `terrain_cover_brush_ops.rs` からも呼ぶため `pub(super)`。）
    pub(super) fn ensure_cover_surface(&mut self, coord: ChunkCoord) {
        if self.terrain.cover_surface.contains_key(&coord) {
            return;
        }
        let Some(chunk) = self.terrain.chunks.get(&coord) else { return };
        let origin_y = coord.world_origin(&self.terrain.settings)[1];
        let surface = CoverSurface::from_chunk(chunk, &self.terrain.settings, origin_y);
        if let Some(field) = self.terrain.cover.get_mut(&coord) {
            field.refresh_base_y(&surface);
        }
        self.terrain.cover_surface.insert(coord, surface);
    }

    /// 指定チャンクのカバー場の基準 Y を、地表情報から同期する（I3.2）。
    ///
    /// `ensure_cover_surface` は「面情報が無いときだけ」作るため、
    /// **面情報が先にあってカバー場が後から来た**場合（.tcover のロード・Undo の
    /// 書き戻し・Play スナップショットの復元）に基準 Y が未同期のまま残る。
    /// その 1 か所を埋めるのが本関数であり、カバー場を差し替えた経路は必ずここを通す。
    pub(super) fn sync_cover_base_y(&mut self, coord: ChunkCoord) {
        // ── カバー場が差し替わったので、進行中の焼き直し波（凍結データ）を捨てる ──
        //   本関数を呼ぶのはカバー場そのものを入れ替える経路（.tcover ロード / Undo /
        //   Play 復元）だけである。凍結データはその入れ替え**前**の内容なので、
        //   そのまま焼き続けると消えたはずの雪を頂点へ書き戻してしまう。
        //   未処理チャンクは待ち集合へ戻すので、次の波が新しい内容で焼き直す。
        //   （既に空の波なら空を返すだけ。ループから何度呼ばれても無害。）
        self.abort_cover_bake_wave();
        self.ensure_cover_surface(coord);
        let Some(surface) = self.terrain.cover_surface.get(&coord) else { return };
        let Some(field) = self.terrain.cover.get_mut(&coord) else { return };
        field.refresh_base_y(surface);
    }

    /// 地形が編集された（再メッシュされた）チャンクのカバー派生データを捨てる。
    ///
    /// `remesh_chunks` の入口から呼ばれる。捨てるのは
    ///   ・地表情報（密度場が変わったので法線・高さが変わる）
    ///   ・メッシュ基準値（頂点が作り直されるので古い基準は無意味）
    /// であり、**カバー場そのもの（積もった量）は保持する**
    /// （地形を少し掘っただけで積雪が消えるのは直感に反するため）。
    pub(super) fn invalidate_cover_for_remesh(&mut self, coord: ChunkCoord) {
        self.terrain.cover_surface.remove(&coord);
        self.terrain.cover_base_mesh.remove(&coord);
        // ── 進行中の焼き直し波から、このチャンクと 26 近傍を追い出す ──
        //   波は「カバー場を凍結して何フレームかに分けて焼く」仕組みで、凍結データには
        //   面のワールド高さ（基準 Y）も含まれる。再メッシュで面が動くとその前提が崩れる
        //   ので、当該チャンクと——境界頂点がそのカバー場を読む——26 近傍を波から外し、
        //   待ち集合へ戻して次の波（＝新しい凍結データ）で焼き直させる。
        let evicted = self.terrain.cover_bake_wave.evict_neighborhood(coord);
        for c in evicted {
            self.terrain.cover_pending_apply.insert(c);
        }
        // カバーが乗っているチャンクは、新しいメッシュへ焼き直す必要がある。
        // 隣にだけカバーがある場合も、境界の頂点はそれを読むので焼き直す
        // （焼き直すのは新しいメッシュを持つこのチャンクだけでよい）。
        if self.any_cover_in_neighborhood(coord, |f| !f.is_empty()) {
            self.terrain.cover_pending_apply.insert(coord);
        }
    }

    /// 自分または隣接 26 チャンク（XZ 8 近傍 × 上下 3 段）のカバー場のどれかが `pred` を満たすか。
    ///
    /// 境界の頂点は隣のカバー場も読むため、焼き直しの要否は必ずこの範囲で判断する
    /// （自分だけを見ると「隣に積もった雪が境界へはみ出す分」を取りこぼす）。
    /// 上下段まで見るのは Y 照合（I3.2）で上下段の面も候補になったためである。
    fn any_cover_in_neighborhood(
        &self,
        coord: ChunkCoord,
        pred: impl Fn(&CoverField) -> bool,
    ) -> bool {
        (-1..=1i32).any(|dy| {
            (-1..=1i32).any(|dz| {
                (-1..=1i32).any(|dx| {
                    self.terrain
                        .cover
                        .get(&ChunkCoord::new(coord.x + dx, coord.y + dy, coord.z + dz))
                        .is_some_and(&pred)
                })
            })
        })
    }

    /// 自分または隣接 26 チャンクのカバー場が、**進行中の波の凍結データ**に載っているか。
    ///
    /// `any_cover_in_neighborhood` の凍結データ版。焼き込みの要否判定はこちらを使う
    /// （焼き込み自体が凍結データしか読まないため、判定も同じ器で行わないと
    /// 「器はあるのに凍結されていない」チャンクを焼こうとして空の場で潰してしまう）。
    ///
    /// 「空でないか」ではなく「器があるか」で判定するのは元の判定と同じ理由である。
    /// 全消去（`TERRAIN_COVER_CLEAR`）の直後は全チャンクが空の器になるが、頂点には
    /// まだ変位が焼かれているので、**空の場で焼き直して基準位置へ戻す**必要がある。
    fn wave_has_cover_in_neighborhood(&self, coord: ChunkCoord) -> bool {
        (-1..=1i32).any(|dy| {
            (-1..=1i32).any(|dz| {
                (-1..=1i32).any(|dx| {
                    self.terrain
                        .cover_bake_wave
                        .has_field(ChunkCoord::new(coord.x + dx, coord.y + dy, coord.z + dz))
                })
            })
        })
    }

    // ─── ④ 頂点への焼き込み ─────────────────────────────────────────────────

    /// 焼き直し待ちのチャンクへカバー場を反映する（毎フレーム呼ばれる）。
    ///
    /// `COVER_APPLY_INTERVAL_SEC` で間引く。待ちが空のフレームは即座に返る。
    ///
    /// 【フレーム分散（予算つき）＋ カバー場の凍結】
    ///   発火フレームで待ちを全件焼くと、走行中の轍や広域の積雪で 1 フレームだけ
    ///   数十ミリ秒跳ねる。そこで「波（`CoverBakeWave`）」という作業単位を作り、
    ///   波を張った時点でカバー場を丸ごと凍結してから、`plan_cover_bake`（純粋層）の
    ///   予算に従って何フレームかに分けて焼く。
    ///
    ///   凍結が要点である。境界の複製頂点は隣のカバー場も読むため、隣り合うチャンクを
    ///   別フレームに焼くと世代がずれて段差が出る。旧実装はこれを「26 近傍で連結した
    ///   塊は分割しない」というスケジューリング規則で防いでいたが、全域降雪では全チャンクが
    ///   1 成分になって予算がまったく効かなかった（＝積算ティックのたびに全チャンクを
    ///   1 フレームで焼く）。凍結してしまえば分割は常に安全なので、予算が素直に効く。
    ///
    /// 【波が進行中のフレームは間引かない】
    ///   `COVER_APPLY_INTERVAL_SEC` の間引きは「新しい波を張るまでの待ち」にだけ効かせる。
    ///   波の消化まで間引くと、予算で割った回数ぶん反映が遅れる（スパイクを均すのではなく
    ///   反映が遅くなるという改悪になる）。
    ///
    /// 戻り値は焼き直しに要した時間（ミリ秒。計測ログ用）。
    pub(super) fn apply_pending_cover(&mut self, dt: f32) -> f64 {
        // ── ① 轍スタンプが来たら進行中の波を張り直す（接地への応答性が最優先）──
        //   波は凍結データで動くため、新しい轍は次の波まで反映されない。轍だけは
        //   「踏んだ瞬間に跡が付く」ことが体感の核なので、未処理分を待ち集合へ戻して
        //   即座に新しい波を張り直す（`plan_cover_bake` が轍を予算外で先に焼く）。
        if !self.terrain.cover_immediate_apply.is_empty()
            && self.terrain.cover_bake_wave.is_active()
        {
            let (rest, urgent) = self.terrain.cover_bake_wave.clear();
            self.terrain.cover_pending_apply.extend(rest);
            self.terrain.cover_immediate_apply.extend(urgent);
            // 間引きを待たずに今フレームで張り直す。
            self.terrain.cover_apply_timer = COVER_APPLY_INTERVAL_SEC;
        }

        // ── ② 波が無ければ、間引き間隔を見て新しい波を張る ──
        if !self.terrain.cover_bake_wave.is_active() {
            if self.terrain.cover_pending_apply.is_empty() {
                // 待ちが無いあいだはタイマーを進めない（次に積もった瞬間へ即反映するため）。
                self.terrain.cover_apply_timer = COVER_APPLY_INTERVAL_SEC;
                return 0.0;
            }
            self.terrain.cover_apply_timer += dt.max(0.0);
            if self.terrain.cover_apply_timer < COVER_APPLY_INTERVAL_SEC {
                return 0.0;
            }
            self.terrain.cover_apply_timer = 0.0;
            if self.draw_ctx.is_none() {
                return 0.0;
            }
            self.begin_cover_bake_wave();
            if !self.terrain.cover_bake_wave.is_active() {
                return 0.0;
            }
        } else if self.draw_ctx.is_none() {
            // 描画コンテキストが無いフレームは焼けない。波はそのまま次フレームへ持ち越す。
            return 0.0;
        }

        let t_total = Instant::now();
        // 焼き直しが実際に走るフレームだけ計上される（間引きで返るフレームは対象外）。
        crate::profile_scope!("地形/カバー頂点焼き直し");

        // ── ③ 今フレームに焼く分を予算つきで選ぶ（残りは波が保持したまま次フレームへ）──
        //   計画は純粋層（`plan_cover_bake`）が決める。ここは入出力の受け渡しに徹する。
        let settings = self.terrain.settings.clone();
        let camera   = self.last_camera_pos;
        let plan = plan_cover_bake(
            self.terrain.cover_bake_wave.remaining(),
            self.terrain.cover_bake_wave.immediate(),
            |c| {
                // チャンク中心 = ワールド原点 + 一辺の半分。
                let o = c.world_origin(&settings);
                let h = settings.chunk_extent() * 0.5;
                [o[0] + h, o[1] + h, o[2] + h]
            },
            camera,
            COVER_BAKE_CHUNK_BUDGET_PER_FRAME,
        );
        // 決定性のため座標順に処理する（ログ・GPU コマンド列を再現可能にする）。
        // `plan.bake` は純粋層でソート済み。
        let coords: Vec<ChunkCoord> = plan.bake;

        let extent = self.terrain.settings.chunk_extent();
        let materials = self.terrain.cover_materials.clone();
        let mut applied = 0usize;
        for coord in coords {
            if self.apply_cover_to_chunk(coord, extent, &materials) {
                applied += 1;
                // 頂点が動いた＝RT が辿る地形（BLAS）も作り直す必要がある。
                // 実際の消化は `flush_rt_blas_prune`（毎フレーム・予算つき）が行う。
                // **ここで消化してはいけない**: 本メソッドは焼き直し待ちが空のフレームでは
                // 先頭で return するため、ここに消化処理を置くと「マウスを離した時点で
                // 焼き直し待ちが空」のときに二度と発火しない（＝黒が残り続ける）。
                self.terrain.rt_blas_prune_pending.insert(coord);
            }
            // 焼けなかったチャンク（空メッシュ・カバー場が無い等）も波からは外す。
            // 残すと同じチャンクを毎フレーム再検討し続けて波が終わらない。
            self.terrain.cover_bake_wave.mark_baked(coord);
        }

        let total_ms = t_total.elapsed().as_secs_f64() * MILLIS_PER_SEC;
        if *super::terrain_ops::PERF_TERRAIN_LOG_ENABLED && applied > 0 {
            eprintln!(
                "[PERF terrain] cover apply chunks={applied} remain={} total={total_ms:.2}ms",
                self.terrain.cover_bake_wave.remaining().len(),
            );
        }
        total_ms
    }

    /// 進行中の焼き直し波を捨て、未処理チャンクを待ち集合へ戻す。
    ///
    /// 凍結データの前提（＝波を張った時点のカバー場）が崩れた経路から呼ぶ。
    /// 取りこぼしを防ぐため未処理分は必ず待ち集合へ、轍マークは即時集合へ戻す。
    pub(super) fn abort_cover_bake_wave(&mut self) {
        if !self.terrain.cover_bake_wave.is_active() {
            return;
        }
        let (rest, urgent) = self.terrain.cover_bake_wave.clear();
        self.terrain.cover_pending_apply.extend(rest);
        self.terrain.cover_immediate_apply.extend(urgent);
    }

    /// 新しい焼き直しの波を張る（待ち集合を対象へ移し、カバー場を凍結する）。
    ///
    /// 【凍結する範囲】
    ///   対象チャンクだけでなく **その 26 近傍まで**凍結する。境界の複製頂点は隣接
    ///   チャンクのカバー場を読むためで、欠けていると「量 0」に縮退して段差が出る。
    ///
    /// 【凍結の前に面情報を同期する理由】
    ///   凍結データには「積もっている面のワールド Y（基準 Y）」が含まれる。これは
    ///   密度場から導く派生値なので、凍結前に `ensure_cover_surface` で同期しておかないと
    ///   古い基準 Y を波の全期間にわたって読み続けることになる。
    fn begin_cover_bake_wave(&mut self) {
        let targets: HashSet<ChunkCoord> = std::mem::take(&mut self.terrain.cover_pending_apply);
        let immediate: HashSet<ChunkCoord> =
            std::mem::take(&mut self.terrain.cover_immediate_apply);

        // ── 凍結対象（対象 ∪ その 26 近傍のうち、カバー場の器があるもの）を集める ──
        //   決定性のため座標順にそろえてから重複を除く。
        let mut freeze: Vec<ChunkCoord> = Vec::new();
        for &c in &targets {
            for dy in -1..=1i32 {
                for dz in -1..=1i32 {
                    for dx in -1..=1i32 {
                        let n = ChunkCoord::new(c.x + dx, c.y + dy, c.z + dz);
                        if self.terrain.cover.contains_key(&n) {
                            freeze.push(n);
                        }
                    }
                }
            }
        }
        freeze.sort_by_key(|c| (c.x, c.y, c.z));
        freeze.dedup();

        // 面情報（と基準 Y）を先に同期してから凍結する。
        for &n in &freeze {
            self.ensure_cover_surface(n);
        }
        let mut snapshot: HashMap<ChunkCoord, CoverField> = HashMap::with_capacity(freeze.len());
        for &n in &freeze {
            if let Some(field) = self.terrain.cover.get(&n) {
                snapshot.insert(n, field.clone());
            }
        }
        self.terrain.cover_bake_wave.start(targets, immediate, snapshot);
    }

    /// 1 チャンクぶんのカバー場を頂点へ焼き込み、GPU 頂点バッファを書き換える。
    ///
    /// 【その場更新であり、下流を作り直さない（docs/cover_field.md §2.8）】
    ///   カバーの変位は「既存頂点への上書き」であって、頂点数もインデックス列も変わらない
    ///   （回帰テスト `cover_bake_preserves_vertex_count_and_indices`）。したがって
    ///   Marching Cubes の再実行・コライダー（Rapier QBVH）再構築・LOD 再生成はいずれも
    ///   通さず、CPU モデルを差し替えて `queue.write_buffer` 1 回で GPU 頂点バッファを
    ///   書き直すだけで済む。ラスタ経路（G-Buffer・シャドウマップ・深度・ID）は
    ///   同じ頂点バッファを読むためすべて自動的に一致する。
    ///
    ///   統合バッチ（`shared_model_batches`）も**捨てない**。バッチが持つのは
    ///   「どのインスタンスをどの行列で描くか」という構成だけで、それは 1 ビットも
    ///   変わらないためである（捨てるとバッチ再構築＋初回 update() ＋影の静的スキップ解除の
    ///   連鎖になる）。形状が変わったことは `flush_rt_blas_prune` が
    ///   `note_geometry_content_changed`（内容世代の更新）で影へ伝える。
    ///
    ///   RT 加速構造（BLAS）だけは `rt_blas_prune_pending` へ積んで追従させる
    ///   （キャッシュが `source_path` キーで「一度作ったら作り直さない」ため）。
    ///
    /// 戻り値は実際に書き換えたか。
    fn apply_cover_to_chunk(
        &mut self,
        coord: ChunkCoord,
        extent: f32,
        materials: &CoverMaterialSet,
    ) -> bool {
        // ─── 焼く価値があるか（自分＋XZ 隣接 8 チャンクのどこかにカバー場があるか）───
        //   **自分のカバー場だけを見てはいけない**。境界の頂点は隣のカバー場も読むので、
        //   「自分には何も積もっていないが隣には積もっている」チャンクも焼き直さないと、
        //   隣のメッシュだけが持ち上がって境界に隙間が開く。
        //
        //   ここでは「空でないか」ではなく「カバー場の器があるか」で判定する。
        //   全消去（`TERRAIN_COVER_CLEAR`）の直後は全チャンクが空の器になるが、
        //   頂点にはまだ変位が焼かれているので、**空の場で焼き直して基準位置へ戻す**
        //   必要があるためである。
        //
        //   判定も焼き込みも読むのは**波の凍結データだけ**である（実データではない）。
        //   面情報の同期（I3.2 の Y 照合が要る基準 Y）は波を張るときに
        //   `begin_cover_bake_wave` が 27 近傍ぶんまとめて済ませている。
        if !self.wave_has_cover_in_neighborhood(coord) {
            return false;
        }
        let Some(&slot_entity) = self.terrain.chunk_slot_entity.get(&coord) else { return false };

        // 頂点はチャンクローカル座標なので、Y 照合に使うワールド Y の基準を控える。
        let chunk_origin_y = coord.world_origin(&self.terrain.settings)[1];

        // ─── ① 基準メッシュ（カバー適用前の頂点位置・平均アルベド）を用意する ───
        //   `&mut self` を要するのはここだけなので、先に済ませてしまう。
        //   こうすると ② では近傍カバー場への参照（`&self.terrain.cover`）を
        //   借用競合なしに持てる（クローンを取らずに 9 チャンク分を読める）。
        if !self.ensure_cover_base_mesh(coord, slot_entity) {
            return false;
        }

        // ─── ② カバー場を頂点へ焼く（読み取りだけの段）───
        let new_model: Option<Model> = {
            let Some(scene) = self.scene.as_ref() else { return false };
            let Some(mc) = scene.world.get::<ModelComponent>(slot_entity) else { return false };
            let Some(model) = mc.model.as_ref() else { return false };
            let (Some(mesh), Some(material)) = (model.meshes.first(), model.materials.first())
            else {
                return false;
            };
            let Some(prim) = mesh.primitives.first() else { return false };
            let Some(base) = self.terrain.cover_base_mesh.get(&coord) else { return false };

            // 自チャンク＋隣接 26 チャンク（XZ 8 近傍 × 上下 3 段）のカバー場ビュー。
            // 境界上の頂点が「隣のメッシュから読んでも同じ値」になるために必須である。
            //
            // 【上下段も含める理由（I3.2）】
            //   地表が Y のチャンク境界面を横切る場所では、面が上段にある列と
            //   下段にある列が隣り合う。境界面上の複製頂点は上下段のメッシュへ
            //   複製されているので、上下段の面も候補に入れて
            //   **頂点のワールド Y に最も近い面**を選ばないと、両者が別の値を読んで
            //   段差になる（I3.1 §7-8 の既知の制限）。選び方はワールド位置の純関数
            //   なので、どちらのメッシュから読んでも同じ面に行き着く。
            //
            // 【実データではなく波の凍結データを読む理由（境界整合の要）】
            //   波に属するチャンクは何フレームかに分けて焼かれる。実データを読むと、
            //   その間に積算が進んだぶんだけ「先に焼いたチャンク」と「後で焼いたチャンク」で
            //   世代がずれ、境界の複製頂点が食い違って段差になる。凍結データなら
            //   波の全期間を通じて同一なので、分割して焼いても bit 単位で一致する。
            let wave = &self.terrain.cover_bake_wave;
            let y_tolerance = cover_y_match_tolerance(extent);
            let neighborhood = CoverNeighborhood::from_lookup(y_tolerance, |dx, dy, dz| {
                wave.field(ChunkCoord::new(coord.x + dx, coord.y + dy, coord.z + dz))
            });

            rebuild_terrain_model_with_cover(
                &prim.vertices,
                &prim.indices,
                &model.name,
                material.terrain_palette,
                &base.positions,
                &base.normals,
                base.avg_albedo,
                &neighborhood,
                materials,
                extent,
                chunk_origin_y,
            )
        };
        let Some(new_model) = new_model else { return false };

        // ─── CPU モデル差し替え ＋ GPU 頂点バッファの丸ごと書き換え ───
        //   `apply_terrain_paint_colors` の ⑧ と同一の手順（1 回の write_buffer で全頂点）。
        let ctx = self.draw_ctx.as_ref().unwrap();
        let Some(scene) = self.scene.as_mut() else { return false };
        let Some(mc) = scene.world.get_mut::<ModelComponent>(slot_entity) else { return false };
        mc.model = Some(Arc::new(new_model));

        // 平均アルベドを GPU 側へも反映する（RT 反射・水面反射・DDGI が読む値）。
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
        true
    }

    /// カバー適用前の基準メッシュ（頂点位置・平均アルベド）をキャッシュへ用意する。
    ///
    /// 無ければ現在の CPU モデルから作る。`invalidate_cover_for_remesh` が再メッシュの
    /// たびに捨てるので、ここで拾うのは常に「カバー未適用のメッシュ」である。
    /// 頂点数が食い違うキャッシュ（再メッシュ直後）も作り直す。
    ///
    /// 戻り値は「基準メッシュが使える状態になったか」。
    /// 空メッシュチャンク（全 AIR / 全 SOLID）は書き換える頂点が無いので false。
    fn ensure_cover_base_mesh(
        &mut self,
        coord: ChunkCoord,
        slot_entity: crate::engine::ecs::Entity,
    ) -> bool {
        // ─── 現在の CPU モデルから基準値を取り出す（&self の段）───
        let base = {
            let Some(scene) = self.scene.as_ref() else { return false };
            let Some(mc) = scene.world.get::<ModelComponent>(slot_entity) else { return false };
            // 空メッシュチャンクは焼き込む頂点そのものが無い。
            if mc.gpu_model.is_none() {
                return false;
            }
            let Some(model) = mc.model.as_ref() else { return false };
            let (Some(mesh), Some(material)) = (model.meshes.first(), model.materials.first())
            else {
                return false;
            };
            let Some(prim) = mesh.primitives.first() else { return false };
            // 既に同じ頂点数の基準値があるなら作り直さない。
            if self
                .terrain
                .cover_base_mesh
                .get(&coord)
                .is_some_and(|b| b.positions.len() == prim.vertices.len())
            {
                return true;
            }
            let positions: Vec<[f32; 3]> = prim.vertices.iter().map(|v| v.position).collect();
            let normals: Vec<[f32; 3]> = prim.vertices.iter().map(|v| v.normal).collect();
            let avg = material.avg_albedo;
            CoverBaseMesh {
                positions: Arc::new(positions),
                normals: Arc::new(normals),
                avg_albedo: [avg[0], avg[1], avg[2]],
            }
        };
        self.terrain.cover_base_mesh.insert(coord, base);
        true
    }

    // ─── Undo セッション（1 操作 = 1 Undo 単位）──────────────────────────────

    /// カバー場編集セッションを開始する。
    ///
    /// 開始時点の `terrain.cover` を丸ごと複製して控える（既定 48 チャンクで約 96KB
    /// ＝ 1 チャンク 2KB 強なので無視できる）。密度側の `stroke_before` が
    /// 「触ったチャンクだけ」を控えるのに対し丸ごと控えるのは、カバー積算が
    /// エミッタの届く全チャンクへ同時に効く（＝どこが変わるか事前に分からない）ためである。
    ///
    /// **既に開始済みなら何もしない**。連続シミュレート中に「N 秒進める」を押しても
    /// 開始時点の before を上書きせず、停止までを 1 つの undo 単位に畳み込むための規約。
    fn begin_cover_undo_session(&mut self) {
        if self.terrain.cover_undo_session_before.is_some() {
            return;
        }
        self.terrain.cover_undo_session_before = Some(self.terrain.cover.clone());
    }

    /// カバー場編集セッションを確定し、変化があればメイン Undo 履歴へ 1 コマンド積む。
    ///
    /// セッション未開始（`None`）なら何もしない。差分が 1 件も無い場合も
    /// **何も積まない**（「開始してすぐ停止した」で undo 履歴を汚さない）。
    ///
    /// 【地形専用スタックではなくメイン履歴へ積む理由】
    ///   カバーの操作入口は `CoverEmitterComponent` のインスペクタであり、ユーザーから見れば
    ///   コンポーネント操作の一部である。エディタが `TERRAIN_UNDO` を送るのは
    ///   **地形編集モード中だけ**（`MainWindow.Input.cs`）なので、地形専用スタックへ積むと
    ///   「コンポーネント追加 → シミュレート → Ctrl+Z」で雪が戻らず、代わりにコンポーネント
    ///   追加が取り消されてしまう。メイン履歴へ積めば操作した順に 1 本で戻せる。
    fn commit_cover_undo_session(&mut self) {
        let Some(before) = self.terrain.cover_undo_session_before.take() else {
            return;
        };
        let (cover_before, cover_after) = diff_cover_fields(&before, &self.terrain.cover);
        if cover_before.is_empty() {
            // 変化なし。空のコマンドを積むと Ctrl+Z が「何も起きない 1 回」を消費する。
            return;
        }
        // 直前のインスペクタ編集へマージされないよう、フィールド編集セッションを切る
        // （マージ判定は「自分が積んだ直後か」を past_len で見るため、割り込みを明示する）。
        self.reset_field_edit_session();
        self.undo_history.record(Box::new(CoverFieldEditCommand {
            before: cover_before,
            after: cover_after,
        }));
    }

    /// カバー場のスナップショット群を現在の地形へ書き戻す（undo / redo 共通）。
    ///
    /// 書き戻しに伴い次の 3 つを更新する:
    ///   ① `cover`          … 実体そのもの
    ///   ② `cover_dirty`    … .tcover の保存対象（巻き戻した状態を保存させる）
    ///   ③ `cover_pending_apply` … 頂点への焼き直し予約
    ///
    /// 【`remesh_chunks` を呼ばない理由】
    ///   カバー場は密度場を一切変えないので、メッシュを作り直す必要が無い。
    ///   むしろ再メッシュはカバーの派生キャッシュ（`cover_surface` / `cover_base_mesh`）を
    ///   捨てるため、無駄な再計算を招くだけである。頂点への反映は
    ///   `apply_pending_cover` の経路（②③）だけで完結する。
    pub(super) fn restore_cover_snapshots(&mut self, snapshots: &CoverFieldMap) {
        if snapshots.is_empty() {
            return;
        }
        for (&coord, field) in snapshots {
            // 密度側の undo と同じく、チャンクが存在する場合のみ書き戻す。
            // セッション中にハイトマップ再読込等でチャンク集合が作り直されると、
            // スナップショットに「今は存在しないチャンク」が残りうる。無条件に insert すると
            // 幽霊チャンクのカバー場が復活し、保存時に不要な .tcover を書いてしまう。
            if !self.terrain.chunks.contains_key(&coord) {
                continue;
            }
            self.terrain.cover.insert(coord, field.clone());
            // 書き戻した場の基準 Y を現在の地形へ同期する（スナップショットは
            // 撮った時点の地形に基づく。地形を編集してから undo しても破綻させない）。
            self.sync_cover_base_y(coord);
            self.terrain.cover_dirty.insert(coord);
            // 隣接チャンクの境界頂点もこのカバー場を読むので一緒に焼き直す。
            self.queue_cover_apply(coord);
        }
        // 巻き戻しは待たせる意味が無いので、次フレームで即座に焼き直す
        // （`handle_terrain_cover_clear` と同じ流儀）。
        self.terrain.cover_apply_timer = COVER_APPLY_INTERVAL_SEC;
    }

    // ─── IPC ハンドラ（連続開始 / 停止 / 即時ステップ / 全消去）───────────────

    /// カバー場のリアルタイム連続シミュレートを開始する（`TERRAIN_COVER_SIM_START`）。
    ///
    /// 停止コマンドが来るまで毎フレーム積算する（実際の積算は `tick_cover` が行う）。
    /// 結果は編集データとしてカバー場へ書かれ、`TERRAIN_SAVE` の保存対象になる。
    ///
    /// 【素材定義をここで読み直さない理由】
    ///   カバー素材の色・粗さは GPU uniform（group3）に載っており、その作り直しは
    ///   `ensure_terrain_layers` 経由でしか起きない。ここで CPU 側だけ読み直すと
    ///   「変位は新しい定義・色は古い定義」というちぐはぐが生じ、素材を増減させた
    ///   場合は添字までずれる。cover_materials.json の反映は layers.json と同じ
    ///   `TERRAIN_RELOAD_LAYERS`（地形設定ウィンドウの保存）に一本化する。
    pub(super) fn handle_terrain_cover_sim_start(&mut self) {
        // 開始〜停止のあいだ全部で 1 つの undo 単位にする。
        self.begin_cover_undo_session();
        // 前回セッションの端数を持ち越さない（押した瞬間から時間を数え直す）。
        self.terrain.cover_accum_timer = 0.0;
        self.terrain.cover_sim_running = true;
        if let Some(ipc) = &self.ipc {
            ipc.send("TERRAIN_COVER_SIM_STARTED");
        }
    }

    /// カバー場の連続シミュレートを停止する（`TERRAIN_COVER_SIM_STOP`）。
    ///
    /// 停止と同時にセッションを確定し、開始時点からの差分を 1 つの undo エントリにする。
    ///
    /// 停止の瞬間に「ティック間隔に届かず未消化のまま残っていた端数時間」をここで積む。
    /// こうしないと、押していた時間の最後の最大 1 ティックぶんが失われ、
    /// 「N 秒押した結果」が毎フレーム積算だったときと一致しなくなる。
    pub(super) fn handle_terrain_cover_sim_stop(&mut self) {
        self.terrain.cover_sim_running = false;
        self.flush_cover_accum_residual();
        self.commit_cover_undo_session();
        if let Some(ipc) = &self.ipc {
            ipc.send("TERRAIN_COVER_SIM_STOPPED");
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// カバー場を指定秒数ぶん即時計算する（`TERRAIN_COVER_STEP:{seconds}`）。
    ///
    /// 「押したら計算して止まる」操作であり、連続シミュレートは開始しない。
    pub(super) fn handle_terrain_cover_step(&mut self, seconds: f32) {
        // 0 秒以下は何も積もらないので素通しする（空の undo エントリも作らない）。
        if !(seconds > COVER_STEP_MIN_SECONDS) {
            return;
        }

        // 連続シミュレート中なら既存セッションに畳み込まれる（多重開始しない）。
        self.begin_cover_undo_session();

        // ─── 有限ステップ数で一気に回す ───
        //   刻みは「60Hz 相当」を基本とし、指定秒数が長い場合だけ
        //   ステップ数を上限で頭打ちにして刻み幅のほうを伸ばす
        //   （合計量は刻み幅に依らないので結果は変わらない。定数コメント参照）。
        let t = Instant::now();
        let emitters = self.collect_cover_emitters();
        let total = seconds.min(COVER_SIMULATE_MAX_SECONDS);
        let steps = ((total / COVER_SIMULATE_STEP_SEC).ceil() as u32)
            .clamp(1, COVER_SIMULATE_MAX_STEPS);
        let step_dt = total / steps as f32;
        for _ in 0..steps {
            self.accumulate_cover(&emitters, step_dt);
        }
        // 即時計算なので焼き直しの間引きを待たずに反映する。
        self.terrain.cover_apply_timer = COVER_APPLY_INTERVAL_SEC;

        // 【セッションを確定するかどうかの判断】
        //   連続シミュレート中に押されたぶんは「再生の成果」の一部なので、
        //   ここでは確定せず停止時（SIM_STOP）の 1 コマンドに含める。
        //   停止中に押されたぶんだけが独立した 1 undo 単位になる。
        if !self.terrain.cover_sim_running {
            self.commit_cover_undo_session();
        }

        let ms = t.elapsed().as_secs_f64() * MILLIS_PER_SEC;
        eprintln!(
            "[SEED terrain] cover step: {seconds:.2}s in {steps} steps, \
             emitters={} chunks={} ({ms:.1}ms)",
            emitters.len(),
            self.terrain.cover.len()
        );
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("TERRAIN_COVER_STEP_OK:{steps}"));
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// 全チャンクのカバー場を消去する（`TERRAIN_COVER_CLEAR`）。
    ///
    /// 消したチャンクは「未保存」になるので、`TERRAIN_SAVE` で
    /// ディスク上の .tcover も削除される（消したはずの雪が復活しない）。
    ///
    /// 【連続シミュレート中に押された場合】
    ///   先に停止してそこまでの成果を 1 つの undo エントリとして確定してから消去する。
    ///   こうすると「再生の成果」と「全消去」が別々の Ctrl+Z になり、
    ///   消去だけを取り消して積もった状態へ戻れる。
    pub(super) fn handle_terrain_cover_clear(&mut self) {
        self.stop_cover_sim_and_commit();

        // 消去そのものを独立した 1 undo 単位にする。
        self.begin_cover_undo_session();

        let coords: Vec<ChunkCoord> = self.terrain.cover.keys().copied().collect();
        for coord in coords {
            if let Some(field) = self.terrain.cover.get_mut(&coord) {
                if field.is_empty() {
                    continue;
                }
                field.clear();
            }
            self.terrain.cover_dirty.insert(coord);
            // 消えたカバーは隣接チャンクの境界頂点にも効くので一緒に焼き直す。
            self.queue_cover_apply(coord);
        }
        // 消去は待たせる意味が無いので、次フレームで即座に焼き直す。
        self.terrain.cover_apply_timer = COVER_APPLY_INTERVAL_SEC;
        // 消去を 1 つの undo エントリとして確定する（消す物が無ければ何も積まれない）。
        self.commit_cover_undo_session();
        if let Some(ipc) = &self.ipc {
            ipc.send("TERRAIN_COVER_CLEARED");
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// 連続シミュレートが動いていれば停止し、そこまでの成果を undo エントリとして確定する。
    ///
    /// ユーザーの停止操作以外（全消去・Play 開始）で連続シミュレートが終わる場合の共通処理。
    /// エディタのトグルボタンを▶へ戻すため、**必ず** `TERRAIN_COVER_SIM_STOPPED` を送る。
    /// 動いていなければ何もしない（不要な通知を撒かない）。
    pub(super) fn stop_cover_sim_and_commit(&mut self) {
        if !self.terrain.cover_sim_running {
            return;
        }
        self.terrain.cover_sim_running = false;
        // 停止の瞬間に残っていた積算ティックの端数を積む
        // （`handle_terrain_cover_sim_stop` と同じ理由。押していた時間を取りこぼさない）。
        self.flush_cover_accum_residual();
        self.commit_cover_undo_session();
        if let Some(ipc) = &self.ipc {
            ipc.send("TERRAIN_COVER_SIM_STOPPED");
        }
    }

    // ─── ⑤ 永続化 ───────────────────────────────────────────────────────────

    /// 全チャンクのカバー場を .tcover として保存する。
    ///
    /// 【空チャンクのファイルを消す理由】
    ///   量 0 になったチャンクのファイルを残すと、次回ロード時に消したはずの雪が
    ///   復活する。`save_terrain_scatter` と同じく「空 = ファイルを消す」を
    ///   保存の不変条件とする。
    ///
    /// - `only_dirty`: true なら **変更のあったチャンクだけ**を書き出す
    ///   （シーン保存に相乗りするフラッシュ用。触っていない地形のぶんディスクを
    ///   叩かないので、Ctrl+S が地形の規模で遅くならない）。
    ///   false なら全チャンク（「地形を保存」ボタン＝ロード時の確実な復元を優先）。
    ///
    /// 戻り値は (書き出したファイル数, 削除したファイル数)。
    pub(super) fn save_terrain_cover(
        &mut self,
        dir: &std::path::Path,
        only_dirty: bool,
    ) -> (u32, u32) {
        let mut written = 0u32;
        let mut removed = 0u32;

        for (&coord, field) in &self.terrain.cover {
            if only_dirty && !self.terrain.cover_dirty.contains(&coord) {
                continue;
            }
            let path = dir.join(tcover_file_name(coord));
            if field.is_empty() {
                match std::fs::remove_file(&path) {
                    Ok(()) => removed += 1,
                    Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
                    Err(e) => eprintln!("[SEED terrain] tcover remove failed: {path:?} err={e}"),
                }
                continue;
            }
            let bytes = write_cover_chunk(field, coord);
            match std::fs::write(&path, &bytes) {
                Ok(()) => written += 1,
                Err(e) => eprintln!("[SEED terrain] tcover save failed: {path:?} err={e}"),
            }
        }

        self.terrain.cover_dirty.clear();
        (written, removed)
    }

    /// .tvox の隣にある .tcover を読み込んで `terrain.cover` を埋める。
    ///
    /// 【ファイルが無いのはエラーではない】
    ///   カバー機能より前に保存されたシーンには .tcover が存在しない。
    ///   欠落を「カバー無し」として扱うことで旧シーンもそのまま開ける。
    pub(super) fn load_terrain_cover(&mut self, chunk_paths: &[(ChunkCoord, String)]) {
        for (coord, tvox_path) in chunk_paths {
            let path = tcover_path_from_tvox(tvox_path);
            let Ok(bytes) = crate::engine::asset_fs::read_bytes(&path) else {
                // 未保存／旧シーン。カバー無しとして扱う（エラーではない）。
                continue;
            };
            match read_cover_chunk(&bytes) {
                Ok((field, _stored_coord)) => {
                    let has_cover = !field.is_empty();
                    self.terrain.cover.insert(*coord, field);
                    // 読み込んだカバーを頂点へ焼き直す（描画へ反映する）。
                    // 隣接チャンクの境界頂点もこの場を読むので一緒に予約する。
                    if has_cover {
                        // TCOVER v1 から読んだ場は基準 Y が「未知」なので、
                        // ここで地表情報から再計算して Y 照合が効く状態にする。
                        self.sync_cover_base_y(*coord);
                        self.queue_cover_apply(*coord);
                    }
                }
                Err(e) => {
                    eprintln!("[SEED terrain] tcover decode failed, skip: {path} err={e:?}");
                }
            }
        }
        // ロード直後は間引きを待たずに反映する（開いた瞬間に雪が乗っている状態にする）。
        self.terrain.cover_apply_timer = COVER_APPLY_INTERVAL_SEC;
    }

    // ─── ⑥ Play 中の積算を揮発させる ────────────────────────────────────────

    /// Play 開始時に Edit のカバー場を退避する。
    ///
    /// Play 中の積算はゲーム状態であって編集データではない（水位 `sim_level_y` と
    /// まったく同じ考え方）。Stop したら Edit 時の保存状態へ戻す必要がある。
    ///
    /// メモリは 1 チャンク 2KB 強（32×32×2 バイト）なので、丸ごと複製して構わない。
    pub(super) fn snapshot_cover_for_play(&mut self) {
        // Play 中に Edit のシミュレートが動き続けると二重に積もるので必ず止める。
        //
        // 【ここで undo セッションも確定する理由】
        //   Play 中の積算は揮発する（＝Undo 対象外）ため、セッションが Play を跨ぐと
        //   「Stop 後に Ctrl+Z したら Play 前の状態まで一気に戻る」ような
        //   ちぐはぐな 1 エントリが出来てしまう。Play へ入る前に締めておく。
        //   エディタのトグルボタンを▶へ戻すため SIM_STOPPED も送られる。
        self.stop_cover_sim_and_commit();
        // 轍の追跡情報（前フレーム位置・進行方向）は Play ごとに作り直す
        // （前回の Play の位置と突き合わせて巨大な移動量が出るのを防ぐ）。
        self.terrain.cover_stamp_tracks.clear();
        // デバッグ表示の作用状態も Play をまたいで残さない。
        self.terrain.cover_stamp_debug.clear();
        // 積算ティックの端数も Play の頭から数え直す（Edit の端数を持ち込まない）。
        self.terrain.cover_accum_timer = 0.0;
        self.terrain.cover_play_snapshot = Some(self.terrain.cover.clone());
    }

    /// Play 終了時に Edit のカバー場へ戻す。
    ///
    /// Play 中に積もったぶんは捨てられ、未保存フラグも立たない
    /// （Play しただけでシーンが変更済みになる、という挙動を避ける）。
    pub(super) fn restore_cover_after_play(&mut self) {
        let Some(snapshot) = self.terrain.cover_play_snapshot.take() else { return };
        // Play 中に貯まった積算ティックの端数は、積算結果ごと捨てる（Play の積算は揮発）。
        self.terrain.cover_accum_timer = 0.0;
        self.terrain.cover_stamp_tracks.clear();
        // Stop した瞬間にギズモを通常色へ戻す（作用中の色が残らないように）。
        self.terrain.cover_stamp_debug.clear();
        // Play 中に触れた（または Play 前から乗っていた）チャンクは全部焼き直す。
        let mut touched: HashSet<ChunkCoord> =
            self.terrain.cover.keys().copied().collect();
        touched.extend(snapshot.keys().copied());
        self.terrain.cover = snapshot;
        // 隣接チャンクの境界頂点も揮発ぶんを読んでいたので一緒に焼き直す。
        for coord in touched {
            self.sync_cover_base_y(coord);
            self.queue_cover_apply(coord);
        }
        self.terrain.cover_apply_timer = COVER_APPLY_INTERVAL_SEC;
    }
}

// ============================================================
//  ファイル名・パスの規則（.tvox / .tscatter と同じ命名）
// ============================================================

/// チャンクの .tcover ファイル名（`chunk_X_Y_Z.tcover`）を返す。
pub(super) fn tcover_file_name(coord: ChunkCoord) -> String {
    use crate::engine::terrain::dir_ref;
    format!("{}{}", dir_ref::chunk_stem(coord), dir_ref::TCOVER_EXT)
}

/// .tvox の仮想パスから、隣に置かれた .tcover の仮想パスを導く。
///
/// ロード時は `TerrainChunkComponent::tvox_path` しか手掛かりが無い。
/// 拡張子だけを差し替えることで tvox 側のパス規則に自動で追従する
/// （`tscatter_path_from_tvox` と同じ設計）。
pub(super) fn tcover_path_from_tvox(tvox_path: &str) -> String {
    crate::engine::terrain::dir_ref::sibling_path(
        tvox_path,
        crate::engine::terrain::dir_ref::TCOVER_EXT,
    )
}


// ============================================================
//  エミッタ収集（ECS 走査。ワールド解決前の生データ）
// ============================================================

/// アクタ走査で拾ったカバーエミッタの生データ（素材 ID はまだ添字に解決していない）。
struct RawCoverEmitter {
    /// アクターのワールド位置（Region / TextureMask の中心）。
    world_pos: [f32; 3],
    range_kind: CoverEmitterRangeKind,
    extents: [f32; 3],
    fade: f32,
    mask_path: String,
    mask_size: [f32; 2],
    material_id: String,
    strength: f32,
}

/// Transform を持たないアクター（フォルダノード等）の既定位置。
const FALLBACK_ACTOR_POSITION: [f32; 3] = [0.0, 0.0, 0.0];

/// `collect_cover_emitters` の再帰実装。
///
/// `parent_active` は祖先のアクティブ状態。自身または祖先が active=false の
/// アクターからは収集しない。
fn collect_cover_in_actor(
    actor: &Actor,
    world: &crate::engine::ecs::World,
    out: &mut Vec<RawCoverEmitter>,
    parent_active: bool,
) {
    let active = parent_active && actor.active;

    if active {
        // エミッタのワールド位置（Transform はワールド空間）。
        let pos = world
            .get::<Transform>(actor.entity)
            .map(|t| t.position)
            .unwrap_or(FALLBACK_ACTOR_POSITION);

        for slot in actor.slots().iter() {
            if slot.kind != ComponentKind::CoverEmitter || !slot.enabled {
                continue;
            }
            let Some(e) = world.get::<CoverEmitterComponent>(slot.entity) else { continue };
            // コンポーネント側の有効フラグ（ゲームロジックからの一時停止）。
            if !e.enabled {
                continue;
            }
            // 強度 0 以下は何も積もらせないので、ここで落とす
            // （積算ループにも入れない＝完全に無コスト）。
            if !(e.strength > 0.0) {
                continue;
            }
            out.push(RawCoverEmitter {
                world_pos: pos,
                range_kind: e.range_kind,
                extents: e.extents,
                fade: e.fade,
                mask_path: e.mask_path.clone(),
                mask_size: e.mask_size,
                material_id: e.material_id.clone(),
                strength: e.strength,
            });
        }
    }

    for child in actor.children() {
        collect_cover_in_actor(child, world, out, active);
    }
}

// ============================================================
//  轍スタンプのソース収集（ECS 走査。I3.2）
// ============================================================

/// 轍スタンプの追跡情報（フレームを跨いで持つ最小の状態）。
#[derive(Clone, Copy, Debug)]
pub struct CoverStampTrack {
    /// 前フレームのワールド位置（移動量＝踏んだかどうかの判定に使う）。
    pub last_pos: [f32; 3],
    /// 直近の進行方向（ワールド XZ の単位ベクトル）。静止中も保持する。
    pub forward: [f32; 2],
}

/// 轍スタンプ源の「今この瞬間の作用状況」（デバッグ描画専用の観測値）。
///
/// 【なぜ真偽値ではなく残り秒数なのか】
///   押した／動いたという事実はフレーム単位で途切れる。真偽値のまま色にすると
///   ギズモが 60Hz で明滅して読めないので、直近の事実を短時間だけ保持する
///   （`COVER_STAMP_DEBUG_HOLD_SEC`）。0 になったソースはマップから消える。
#[derive(Clone, Copy, Debug, Default)]
pub struct CoverStampDebug {
    /// 「轍を実際に押した」表示の残り時間（秒）。0 = 押していない。
    pub stamped_hold: f32,
    /// 「移動している（＝インタラクションフィールドへ書き込んでいる）」表示の
    /// 残り時間（秒）。0 = 静止。
    pub moving_hold: f32,
}

impl CoverStampDebug {
    /// 轍を押している最中か（ギズモの最優先の色）。
    pub fn is_stamping(&self) -> bool {
        self.stamped_hold > 0.0
    }

    /// 移動中か（草・波紋へ書き込んでいる状態）。
    pub fn is_moving(&self) -> bool {
        self.moving_hold > 0.0
    }
}

/// 1 ソース分のスタンプ依頼（純粋層へ渡す仕様 ＋ 記録用のソースキー）。
///
/// 純粋層（`trample.rs`）はキーを知る必要が無いので `CoverStampSpec` は
/// キーを持たない。エンジン層だけが「どのソースの押下だったか」を追う。
struct CoverStampJob {
    /// ソースキー（`interaction::source_key`）。デバッグ表示の紐づけに使う。
    key: u64,
    /// ワールド解決済みのスタンプ仕様。
    spec: CoverStampSpec,
}

/// アクタ走査で拾った轍スタンプ源の生データ。
struct RawStampSource {
    /// フレームを跨いで安定なキー（アクタ DFS 連番 ＋ スロット添字）。
    key: u64,
    /// アクターのワールド位置（＝接地点として扱う）。
    world_pos: [f32; 3],
    radius: f32,
    strength: f32,
    shape: InteractionStampShape,
    mask_path: String,
    stamp_size: [f32; 2],
}

/// `collect_cover_stamps` の再帰実装。
///
/// スキップ規則・DFS 連番の数え方は `interaction::collect_interaction_sources` と
/// 完全に同一にする（同じコンポーネントを読む以上、収集対象がずれてはならない）。
fn collect_stamp_in_actor(
    actor: &Actor,
    world: &crate::engine::ecs::World,
    out: &mut Vec<RawStampSource>,
    dfs_counter: &mut u32,
    parent_active: bool,
) {
    let dfs_id = *dfs_counter;
    *dfs_counter += 1;
    let active = parent_active && actor.active;

    if active {
        let pos = world
            .get::<Transform>(actor.entity)
            .map(|t| t.position)
            .unwrap_or(FALLBACK_ACTOR_POSITION);

        for (slot_index, slot) in actor.slots().iter().enumerate() {
            if slot.kind != ComponentKind::InteractionSource || !slot.enabled {
                continue;
            }
            let Some(src) = world.get::<InteractionSourceComponent>(slot.entity) else { continue };
            if !src.enabled || src.radius <= 0.0 || src.strength <= 0.0 {
                continue;
            }
            out.push(RawStampSource {
                key: crate::engine::interaction::source_key(dfs_id, slot_index as u32),
                world_pos: pos,
                radius: src.radius,
                strength: src.strength,
                shape: src.stamp_shape,
                mask_path: src.stamp_mask_path.clone(),
                stamp_size: src.stamp_size,
            });
        }
    }

    // 子孫へは常に再帰する（非アクティブでも DFS 連番は進める）。
    for child in actor.children() {
        collect_stamp_in_actor(child, world, out, dfs_counter, active);
    }
}

// ============================================================
//  TerrainState のカバー関連フィールド（型エイリアス）
// ============================================================

/// チャンク → カバー場のマップ（`.tcover` の実体）。
pub type CoverFieldMap = HashMap<ChunkCoord, CoverField>;

// ============================================================
//  Undo 差分抽出（純粋関数）
// ============================================================

/// カバー場セッションの before / after を突き合わせ、**変化したチャンクだけ**を抜き出す。
///
/// 戻り値は `(cover_before, cover_after)` で、どちらも同じキー集合を持つ。
/// これをそのまま `TerrainEdit` へ載せると undo/redo が対称に働く。
///
/// 【「そのチャンクにカバー場が無かった」の表現】
///   欠落キーは `CoverField::new()`（全ゼロ）で埋める。空のカバー場は保存時に
///   .tcover ファイルごと削除される（＝存在しないのと等価）ので、
///   「無かった」と「全部ゼロ」を区別する必要が無い。この規約のおかげで
///   新規に積もったチャンク／全消去されたチャンクも分岐なしに扱える。
///
/// App に依存しない純粋関数として切り出してあり、単体テストできる。
fn diff_cover_fields(
    before: &CoverFieldMap,
    after: &CoverFieldMap,
) -> (CoverFieldMap, CoverFieldMap) {
    let mut out_before = CoverFieldMap::new();
    let mut out_after = CoverFieldMap::new();

    // 両者のキーの **和集合** を走査する（新規追加も消滅も取りこぼさない）。
    let coords: HashSet<ChunkCoord> = before.keys().chain(after.keys()).copied().collect();
    for coord in coords {
        let before_field = before.get(&coord).cloned().unwrap_or_default();
        let after_field = after.get(&coord).cloned().unwrap_or_default();
        // CoverField: PartialEq（素材添字と量の完全一致）で比較する。
        if before_field == after_field {
            continue;
        }
        out_before.insert(coord, before_field);
        out_after.insert(coord, after_field);
    }
    (out_before, out_after)
}

// ─── ユニットテスト ──────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// .tcover のファイル名が .tvox / .tscatter と同じ命名規則であること。
    #[test]
    fn tcover_file_name_follows_convention() {
        assert_eq!(
            tcover_file_name(ChunkCoord::new(-1, 2, 3)),
            "chunk_-1_2_3.tcover"
        );
    }

    /// .tvox パスから .tcover パスが導けること（拡張子差し替え）。
    #[test]
    fn tcover_path_derives_from_tvox_path() {
        assert_eq!(
            tcover_path_from_tvox("assets://terrain/Scene1/chunk_0_0_0.tvox"),
            "assets://terrain/Scene1/chunk_0_0_0.tcover"
        );
        // 拡張子が想定外でも壊れない（読み込みに失敗して「カバー無し」になるだけ）。
        assert_eq!(tcover_path_from_tvox("weird"), "weird.tcover");
    }

    /// 秒数指定シミュレートのステップ数と刻み幅の決め方を固定する。
    ///
    /// ・短い指定は 60Hz 刻みそのまま（置き換えの途中経過が潰れない）
    /// ・長い指定でもステップ数は上限で頭打ち（エディタが固まらない）
    /// ・どちらの場合も `steps × step_dt` は指定秒数と一致する（合計量が変わらない）
    #[test]
    fn simulate_step_plan_is_bounded_and_conserves_total_time() {
        fn plan(seconds: f32) -> (u32, f32) {
            let total = seconds.min(COVER_SIMULATE_MAX_SECONDS);
            let steps = ((total / COVER_SIMULATE_STEP_SEC).ceil() as u32)
                .clamp(1, COVER_SIMULATE_MAX_STEPS);
            (steps, total / steps as f32)
        }

        // 5 秒 → 300 ステップ（= 60Hz 刻み）。
        let (steps, dt) = plan(5.0);
        assert_eq!(steps, 300);
        assert!((dt - COVER_SIMULATE_STEP_SEC).abs() < 1.0e-6);

        // 上限を超える指定でもステップ数は頭打ちになり、合計時間は保たれる。
        for seconds in [60.0f32, 3600.0, 1.0e9] {
            let (steps, dt) = plan(seconds);
            assert!(steps <= COVER_SIMULATE_MAX_STEPS, "ステップ数は上限以下");
            let total = seconds.min(COVER_SIMULATE_MAX_SECONDS);
            assert!(
                (steps as f32 * dt - total).abs() < 1.0e-2,
                "steps × dt が指定秒数と一致すること (seconds={seconds})"
            );
        }

        // 0 に近い指定でも 0 除算せず 1 ステップになる。
        let (steps, _) = plan(1.0e-6);
        assert_eq!(steps, 1);
    }

    // ─── Undo 差分抽出（diff_cover_fields）────────────────────────────────

    /// テスト用: 指定テクセルへ素材を積んだカバー場を作る。
    fn field_with_deposit(material_index: u8, amount: f32) -> CoverField {
        let mut f = CoverField::new();
        f.deposit(0, 0, material_index, amount);
        f
    }

    /// 変化していないチャンクは差分に含まれないこと（空コマンドを作らない）。
    #[test]
    fn diff_cover_fields_ignores_unchanged_chunks() {
        let coord = ChunkCoord::new(1, 0, 2);
        let mut before = CoverFieldMap::new();
        before.insert(coord, field_with_deposit(1, 0.5));
        // まったく同じ内容の after。
        let after = before.clone();

        let (b, a) = diff_cover_fields(&before, &after);
        assert!(b.is_empty(), "変化なしなら before 側は空");
        assert!(a.is_empty(), "変化なしなら after 側は空");
    }

    /// 新規に積もったチャンクは before が「空フィールド」として記録されること。
    ///
    /// これにより undo で「積もる前＝何も無い」状態へ正しく戻せる。
    #[test]
    fn diff_cover_fields_treats_missing_before_as_empty_field() {
        let coord = ChunkCoord::new(0, 0, 0);
        let before = CoverFieldMap::new(); // このチャンクにはカバー場が無かった
        let mut after = CoverFieldMap::new();
        after.insert(coord, field_with_deposit(2, 0.75));

        let (b, a) = diff_cover_fields(&before, &after);
        assert_eq!(b.len(), 1);
        assert_eq!(a.len(), 1);
        assert!(b[&coord].is_empty(), "before は全ゼロのカバー場");
        assert!(!a[&coord].is_empty(), "after は積もった状態");
    }

    /// 消去されたチャンクは after が「空フィールド」として記録されること。
    ///
    /// 全消去（`TERRAIN_COVER_CLEAR`）の undo が成立するための条件。
    #[test]
    fn diff_cover_fields_treats_cleared_after_as_empty_field() {
        let coord = ChunkCoord::new(-3, 0, 4);
        let mut before = CoverFieldMap::new();
        before.insert(coord, field_with_deposit(0, 1.0));
        let mut after = CoverFieldMap::new();
        after.insert(coord, CoverField::new()); // clear() 後の状態

        let (b, a) = diff_cover_fields(&before, &after);
        assert_eq!(b.len(), 1);
        assert!(!b[&coord].is_empty(), "before は積もった状態");
        assert!(a[&coord].is_empty(), "after は全ゼロのカバー場");
    }

    /// 変化したチャンクだけが抜き出され、before / after のキー集合が一致すること。
    #[test]
    fn diff_cover_fields_extracts_only_changed_chunks() {
        let unchanged = ChunkCoord::new(0, 0, 0);
        let changed = ChunkCoord::new(1, 0, 0);
        let mut before = CoverFieldMap::new();
        before.insert(unchanged, field_with_deposit(1, 0.5));
        before.insert(changed, field_with_deposit(1, 0.5));
        let mut after = before.clone();
        after.insert(changed, field_with_deposit(1, 0.9));

        let (b, a) = diff_cover_fields(&before, &after);
        assert_eq!(b.len(), 1, "変化した 1 チャンクだけ");
        assert!(b.contains_key(&changed));
        assert!(!b.contains_key(&unchanged));
        // undo/redo が対称に働くための不変条件。
        let keys_b: HashSet<ChunkCoord> = b.keys().copied().collect();
        let keys_a: HashSet<ChunkCoord> = a.keys().copied().collect();
        assert_eq!(keys_b, keys_a, "before と after のキー集合は一致する");
    }
}
