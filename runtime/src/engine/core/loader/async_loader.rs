// ============================================================
//  async_loader.rs — モデルの非同期ストリーミングロード
//
//  【何のためにあるか】
//  プレイ中に新しいアクタ（魚・エフェクト等）が生成されるたび、そのモデルは
//  これまで **メインスレッドで同期的に** 読まれていた。内蔵 NVMe では派生キャッシュ
//  （`cache/*.smdl`）1 件あたり 0.2〜0.5ms で終わるため気付かなかったが、
//  USB 外付け SSD では 1 件 13〜50ms かかり、出現のたびにフレームが飛んでいた。
//  配布版でもプレイヤーのドライブが遅ければ同じことが起きる（PAK 読み出しも同期）。
//
//  【方針】
//  「ディスク読み＋CPU デコード」をワーカースレッドへ追い出し、メインスレッドは
//  毎フレーム決まった位置で完成品を受け取り **GPU アップロードだけ** を行う。
//
//     [メイン] request(path)  ──► [キュー] ──► [ワーカー] load_model()
//                                                   │ 完成した Model
//                                                   ▼
//     [メイン] pump() ◄── crossbeam channel ◄───────┘
//                │ RAM キャッシュ（LRU）へ格納
//                ▼
//     [メイン] take_ready(path) → GPU アップロード（予算内で N 件/フレーム）
//
//  【このモジュールの責務】
//  - ジョブキュー（優先度 2 段・重複排除）
//  - ワーカースレッドの起動と実行ループ
//  - 完成モデルの RAM キャッシュ（バイト数上限つき LRU）
//  - プリフェッチ（`.actor` を走査して参照モデルを低優先度で先読み）
//  - 設定（`project_settings.json` の `streaming` 節）の解釈
//
//  【このモジュールの責務ではないこと】
//  - GPU アップロード（`DrawContext` が要るので `app/model_streaming.rs` 側）
//  - ECS への差し込み（同上）
// ============================================================

use std::collections::{HashMap, HashSet, VecDeque};
use std::path::Path;
use std::sync::{Arc, Condvar, Mutex, OnceLock};
use std::time::Instant;

use super::model::{Model, TextureSource};

// ============================================================
//  定数（マジックナンバー禁止のため全てここに集約）
// ============================================================

/// `project_settings.json` のストリーミング設定オブジェクトのキー名。
const SETTINGS_KEY_STREAMING: &str = "streaming";
/// 非同期ロードそのものの有効／無効を切り替えるキー名。
const SETTINGS_KEY_ENABLED: &str = "enabled";
/// ワーカースレッド数のキー名。
const SETTINGS_KEY_WORKER_THREADS: &str = "worker_threads";
/// 1 フレームあたりの GPU アップロード時間上限（ミリ秒）のキー名。
const SETTINGS_KEY_UPLOAD_BUDGET_MS: &str = "upload_budget_ms";
/// 1 フレームあたりの GPU アップロード件数上限のキー名。
const SETTINGS_KEY_MAX_UPLOADS_PER_FRAME: &str = "max_uploads_per_frame";
/// 統合バッチ／BLAS の常駐時間（秒）のキー名。
const SETTINGS_KEY_KEEP_ALIVE_SECS: &str = "keep_alive_secs";
/// プリフェッチ有効フラグのキー名。
const SETTINGS_KEY_PREFETCH: &str = "prefetch";
/// プリフェッチ対象ディレクトリ（アセットルート相対）のキー名。空/未指定ならルート全体。
const SETTINGS_KEY_PREFETCH_DIRS: &str = "prefetch_dirs";
/// CPU モデル RAM キャッシュの上限（MiB）のキー名。
const SETTINGS_KEY_RAM_CACHE_MB: &str = "ram_cache_mb";

/// ストリーミングの挙動をプロジェクト設定より優先して上書きする環境変数名。
///
/// 受け付ける値（大文字小文字・前後空白は無視）:
/// - `0` / `off` / `false` / `no` … **完全に従来どおりの同期ロード**へ戻す
///   （プリフェッチも止まる）。不具合の切り分けと、配布版での退避手段。
/// - `noprefetch` / `no_prefetch` … 非同期ロードは使うが先読みだけ止める。
///   「先読みで隠れていないストリーミング経路そのもの」を検証したいとき、
///   および RAM の余裕が無い環境向け。
/// - それ以外 … プロジェクト設定どおり（上書きしない）。
pub const ENV_STREAMING_OVERRIDE: &str = "SEED_STREAMING";
/// `ENV_STREAMING_OVERRIDE` で「先読みだけ止める」を指示する値。
const ENV_VALUE_NO_PREFETCH: [&str; 2] = ["noprefetch", "no_prefetch"];

/// 非同期ロードの既定有効／無効。
pub const DEFAULT_STREAMING_ENABLED: bool = true;

/// ワーカースレッド数の既定値。
///
/// 1 本で足りる理由: ボトルネックは USB のシーク待ちであり、同じデバイスへ
/// 並列に投げても総スループットはほぼ伸びない。むしろ 2 本以上にすると
/// 低優先度のプリフェッチが高優先度の要求を巻き込んで待たせやすくなる。
pub const DEFAULT_WORKER_THREADS: usize = 1;
/// ワーカースレッド数の上限（設定値のクランプ）。
pub const MAX_WORKER_THREADS: usize = 4;

/// 1 フレームで GPU アップロードに使ってよい時間の既定上限（ミリ秒）。
pub const DEFAULT_UPLOAD_BUDGET_MS: f32 = 4.0;
/// 1 フレームで GPU アップロードしてよい件数の既定上限。
pub const DEFAULT_MAX_UPLOADS_PER_FRAME: usize = 2;

/// 統合バッチ／BLAS キャッシュの常駐時間の既定値（秒）。
///
/// 旧実装は 60 フレーム固定（60fps で約 1 秒）だった。画面外へ出た直後に解放され、
/// 戻ってくるたびにバッチと BLAS を組み直していたため、出入りの多いオブジェクト
/// （魚・波打ち際の漂流物）で無駄な再構築が続いていた。30 秒あればプレイ中の
/// 出入りはほぼ吸収できる。
pub const DEFAULT_KEEP_ALIVE_SECS: f32 = 30.0;
/// 常駐時間の下限（秒）。0 以下は誤設定とみなしてここへ丸める。
const MIN_KEEP_ALIVE_SECS: f32 = 0.1;
/// 常駐時間の上限（秒）。これ以上は事実上「解放しない」と同じで VRAM 事故のもと。
const MAX_KEEP_ALIVE_SECS: f32 = 600.0;
/// 常駐時間をフレーム数へ換算するときの基準フレームレート
/// （目標 fps が無制限（0）に設定されている場合のフォールバック）。
pub const KEEP_ALIVE_FALLBACK_FPS: f32 = 60.0;

/// プリフェッチの既定有効／無効。
pub const DEFAULT_PREFETCH: bool = true;
/// CPU モデル RAM キャッシュの既定上限（MiB）。
pub const DEFAULT_RAM_CACHE_MB: usize = 256;

/// プリフェッチで走査する `.actor` ファイルの最大件数。
///
/// 走査そのものはワーカースレッドで行うためメインスレッドは止まらないが、
/// 巨大プロジェクトで無制限に読むとディスクを占有し続けるので上限を置く。
pub const PREFETCH_MAX_ACTOR_FILES: usize = 512;

/// プリフェッチ対象にするモデルの派生キャッシュサイズ上限（バイト）。
///
/// これを超えるモデル（Sponza 級で 1.8GB に達する）は「先読みしたところで
/// RAM キャッシュを一掃するだけ」なのでプリフェッチしない。実際に必要に
/// なったときは通常の非同期要求（高優先度）で読む。
pub const PREFETCH_MAX_MODEL_CACHE_BYTES: u64 = 64 * 1024 * 1024;

/// 地形チャンクの合成モデルパス接頭辞（実ファイルが無いのでロード対象外）。
const TERRAIN_SOURCE_SCHEME: &str = "terrain://";

/// `.actor` ファイルの拡張子（プリフェッチ走査の対象）。
const ACTOR_FILE_EXT: &str = "actor";

/// `.actor` / `.scene` JSON でモデルパスが入っているキー名。
const JSON_KEY_MODEL_PATH: &str = "model_path";

/// MiB → バイトの換算係数。
const BYTES_PER_MIB: usize = 1024 * 1024;

// ============================================================
//  設定
// ============================================================

/// ストリーミング（非同期ロード）の設定値。
///
/// `project_settings.json` の `streaming` 節で上書きできる。
/// 節ごと無い場合・キーが欠けている場合はすべて既定値になる（後方互換）。
///
/// ```json
/// "streaming": {
///   "enabled": true,
///   "worker_threads": 1,
///   "upload_budget_ms": 4.0,
///   "max_uploads_per_frame": 2,
///   "keep_alive_secs": 30,
///   "prefetch": true,
///   "prefetch_dirs": ["mainGame/actors"],
///   "ram_cache_mb": 256
/// }
/// ```
#[derive(Debug, Clone, PartialEq)]
pub struct StreamingConfig {
    /// 非同期ロードを使うか。false なら生成時のモデルも従来どおり同期で読み、
    /// プリフェッチも行わない（＝本機能導入前と完全に同じ挙動）。
    pub enabled: bool,
    /// ワーカースレッド数（1..=MAX_WORKER_THREADS）。
    pub worker_threads: usize,
    /// 1 フレームで GPU アップロードに使ってよい時間（ミリ秒）。
    pub upload_budget_ms: f32,
    /// 1 フレームで GPU アップロードしてよい件数。
    pub max_uploads_per_frame: usize,
    /// 統合バッチ／BLAS キャッシュの常駐時間（秒）。
    pub keep_alive_secs: f32,
    /// プリフェッチを行うか。
    pub prefetch: bool,
    /// プリフェッチで走査するディレクトリ（アセットルート相対）。
    /// 空ならアセットルート全体を走査する。
    pub prefetch_dirs: Vec<String>,
    /// CPU モデル RAM キャッシュの上限（MiB）。
    pub ram_cache_mb: usize,
}

impl Default for StreamingConfig {
    fn default() -> Self {
        Self {
            enabled: DEFAULT_STREAMING_ENABLED,
            worker_threads: DEFAULT_WORKER_THREADS,
            upload_budget_ms: DEFAULT_UPLOAD_BUDGET_MS,
            max_uploads_per_frame: DEFAULT_MAX_UPLOADS_PER_FRAME,
            keep_alive_secs: DEFAULT_KEEP_ALIVE_SECS,
            prefetch: DEFAULT_PREFETCH,
            prefetch_dirs: Vec::new(),
            ram_cache_mb: DEFAULT_RAM_CACHE_MB,
        }
    }
}

impl StreamingConfig {
    /// 常駐時間（秒）を、統合バッチの遅延解放に使うフレーム数へ換算する。
    ///
    /// `target_fps` が 0（無制限）の場合は `KEEP_ALIVE_FALLBACK_FPS` を基準にする。
    /// フレーム数での判定を残しているのは、既存の `compute_stale_batch_prune`
    /// （フレーム単位の不在カウンタ）をそのまま使うため。
    pub fn keep_alive_frames(&self, target_fps: u32) -> u32 {
        let fps = if target_fps == 0 {
            KEEP_ALIVE_FALLBACK_FPS
        } else {
            target_fps as f32
        };
        // 最低 1 フレームは猶予する（0 にすると即時解放＝スラッシングが復活する）。
        (self.keep_alive_secs * fps).round().max(1.0) as u32
    }

    /// RAM キャッシュ上限をバイト数で返す。
    pub fn ram_cache_bytes(&self) -> usize {
        self.ram_cache_mb.saturating_mul(BYTES_PER_MIB)
    }
}

/// `project_settings.json` のテキストからストリーミング設定を読む純関数。
///
/// JSON が壊れている・`streaming` 節が無い・キーが欠けている場合は既定値へ落ちる。
/// 値は有効範囲へクランプする（負値・0・極端な値で挙動が壊れないようにする）。
pub fn parse_streaming_config(json: &str) -> StreamingConfig {
    let mut cfg = parse_streaming_config_raw(json);
    // 環境変数が指定されていればプロジェクト設定より優先する（A/B 切り分け・退避手段）。
    if let Ok(raw) = std::env::var(ENV_STREAMING_OVERRIDE) {
        apply_env_override(&mut cfg, &raw);
    }
    cfg
}

/// 環境変数 `SEED_STREAMING` の値を設定へ適用する純関数（単体テスト対象）。
fn apply_env_override(cfg: &mut StreamingConfig, raw: &str) {
    let v = raw.trim().to_ascii_lowercase();
    if is_falsy_flag(&v) {
        cfg.enabled = false;
        cfg.prefetch = false;
        return;
    }
    if ENV_VALUE_NO_PREFETCH.contains(&v.as_str()) {
        cfg.enabled = true;
        cfg.prefetch = false;
    }
    // それ以外は何も上書きしない（プロジェクト設定を尊重する）。
}

/// "0"/"off"/"false"/"no"（正規化済み）を無効とみなす。
fn is_falsy_flag(normalized: &str) -> bool {
    matches!(normalized, "0" | "off" | "false" | "no")
}

/// 環境変数を見ない純粋な JSON 解釈（単体テスト対象）。
///
/// 【版の扱い】
/// 通常の呼び出し元（`App::init_model_streaming`）が渡すテキストは共通ローダ
/// （`app_base::project_settings::load_text`）で変換済みだが、本関数は公開 API で
/// 単体テストからも直接呼ばれるため、ここでも `migration::load_json` を通す。
/// 二重に通しても連鎖は走らない（現行版なら何もしない）。
/// JSON が壊れている・未来版のときは既定設定へ落ちる（従来どおり）。
fn parse_streaming_config_raw(json: &str) -> StreamingConfig {
    let mut cfg = StreamingConfig::default();
    let Ok(v) = crate::engine::core::migration::load_json::<serde_json::Value>(
        crate::engine::core::migration::FormatKind::ProjectSettings,
        json,
    ) else {
        return cfg;
    };
    let Some(s) = v.get(SETTINGS_KEY_STREAMING) else {
        return cfg;
    };

    if let Some(b) = s[SETTINGS_KEY_ENABLED].as_bool() {
        cfg.enabled = b;
    }
    if let Some(n) = s[SETTINGS_KEY_WORKER_THREADS].as_i64() {
        cfg.worker_threads = (n.max(1) as usize).min(MAX_WORKER_THREADS);
    }
    if let Some(x) = s[SETTINGS_KEY_UPLOAD_BUDGET_MS].as_f64() {
        // 0 以下は「毎フレーム 1 件だけ」に相当させたいので下限を極小値に丸める。
        cfg.upload_budget_ms = (x as f32).max(f32::EPSILON);
    }
    if let Some(n) = s[SETTINGS_KEY_MAX_UPLOADS_PER_FRAME].as_i64() {
        cfg.max_uploads_per_frame = n.max(1) as usize;
    }
    if let Some(x) = s[SETTINGS_KEY_KEEP_ALIVE_SECS].as_f64() {
        cfg.keep_alive_secs = (x as f32).clamp(MIN_KEEP_ALIVE_SECS, MAX_KEEP_ALIVE_SECS);
    }
    if let Some(b) = s[SETTINGS_KEY_PREFETCH].as_bool() {
        cfg.prefetch = b;
    }
    if let Some(arr) = s[SETTINGS_KEY_PREFETCH_DIRS].as_array() {
        cfg.prefetch_dirs = arr
            .iter()
            .filter_map(|e| e.as_str())
            .filter(|s| !s.is_empty())
            .map(|s| s.to_string())
            .collect();
    }
    if let Some(n) = s[SETTINGS_KEY_RAM_CACHE_MB].as_i64() {
        cfg.ram_cache_mb = n.max(0) as usize;
    }
    cfg
}

// ============================================================
//  ジョブ
// ============================================================

/// ジョブの優先度。高優先度（要求）は低優先度（プリフェッチ）を必ず追い越す。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum JobPriority {
    /// いま画面に出そうとしているモデル。最優先。
    OnDemand,
    /// 将来使うかもしれないモデルの先読み。待ち行列の後ろ。
    Prefetch,
}

/// ワーカーが処理する仕事の種類。
#[derive(Debug, Clone)]
enum Job {
    /// モデル 1 体を読む（派生キャッシュヒット or 元ファイルからのパース）。
    /// `priority` は失敗時のログ文言の出し分けに使う（先読み失敗は実害が無い）。
    Model { path: String, priority: JobPriority },
    /// `.actor` を読んで、参照しているモデルを低優先度で積む。
    PrefabScan { path: String },
    /// アセット配下の `.actor` を列挙して `PrefabScan` を積む。
    DirScan { dirs: Vec<String> },
}

/// ワーカーからメインスレッドへ返す完成品。
struct Completion {
    /// 要求されたモデルパス（`assets://` 仮想パスまたは絶対パス）。
    path: String,
    /// 読み込み結果。Err はエラーメッセージ（ログ用）。
    result: Result<Model, String>,
    /// ワーカー側での所要時間（ミリ秒）。メインスレッドの待ち時間とは別物。
    worker_ms: f64,
    /// この仕事が先読み（プリフェッチ）由来か。失敗ログの強さを変えるのに使う。
    prefetch: bool,
}

/// `request` の結果。
///
/// `Model` は `Debug` を持たない（巨大な頂点配列を出力しても無意味なため）ので
/// この enum も `Debug` を導出しない。
pub enum RequestState {
    /// RAM キャッシュに完成品がある（そのまま使える）。
    Ready(Arc<Model>),
    /// ワーカーで処理中／待機中。完成したら `pump` 経由で受け取れる。
    Pending,
    /// 過去に読み込みへ失敗しているパス（再要求しても無駄）。
    Failed,
}

// ============================================================
//  ジョブキュー（優先度 2 段 + 重複排除）
// ============================================================

/// 待ち行列の中身。`Mutex` で保護し `Condvar` でワーカーを起こす。
struct QueueInner {
    /// 高優先度（OnDemand）の待ち行列。
    high: VecDeque<Job>,
    /// 低優先度（Prefetch / 走査）の待ち行列。
    low: VecDeque<Job>,
    /// 「キューに積んだ or ワーカーが処理中」のモデルパス集合（重複要求の排除）。
    /// メインスレッドの `pump` が完成を取り込んだ時点で取り除く。
    inflight_models: HashSet<String>,
    /// 走査済み `.actor` パス集合（同じプレハブを何度も読まない）。
    scanned_prefabs: HashSet<String>,
    /// ディレクトリ走査を既に投入したか（起動中 1 回だけ）。
    dir_scan_done: bool,
}

impl QueueInner {
    fn new() -> Self {
        Self {
            high: VecDeque::new(),
            low: VecDeque::new(),
            inflight_models: HashSet::new(),
            scanned_prefabs: HashSet::new(),
            dir_scan_done: false,
        }
    }

    /// 次に処理すべきジョブを取り出す（高優先度が常に先）。
    fn pop(&mut self) -> Option<Job> {
        self.high.pop_front().or_else(|| self.low.pop_front())
    }

    /// モデルジョブを積む。既に積まれている（＝処理中含む）なら何もせず false。
    fn push_model(&mut self, path: &str, priority: JobPriority) -> bool {
        if self.inflight_models.contains(path) {
            return false;
        }
        self.inflight_models.insert(path.to_string());
        let job = Job::Model {
            path: path.to_string(),
            priority,
        };
        match priority {
            JobPriority::OnDemand => self.high.push_back(job),
            JobPriority::Prefetch => self.low.push_back(job),
        }
        true
    }

    /// プレハブ走査ジョブを積む。同じ `.actor` は 1 回しか走査しない。
    fn push_prefab_scan(&mut self, path: &str) -> bool {
        if self.scanned_prefabs.contains(path) {
            return false;
        }
        self.scanned_prefabs.insert(path.to_string());
        self.low.push_back(Job::PrefabScan {
            path: path.to_string(),
        });
        true
    }

    /// 待ち行列の総数（ログ・テスト用）。
    fn len(&self) -> usize {
        self.high.len() + self.low.len()
    }
}

// ============================================================
//  RAM キャッシュ（バイト数上限つき LRU）
// ============================================================

/// RAM キャッシュ 1 件。
struct RamEntry {
    model: Arc<Model>,
    /// 推定バイト数（上限判定用）。
    bytes: usize,
    /// 最終アクセス時刻を表す単調カウンタ（LRU 判定用）。
    tick: u64,
}

/// 完成済み CPU モデルの RAM キャッシュ。
///
/// バイト数の合計が上限を超えたら、最後に触った時刻が古いものから捨てる。
/// 主にプリフェッチ済みで「まだ使われていない」モデルを抱えるための箱で、
/// 実際に使われ始めたモデルは `DrawContext::model_cache`（Arc 共有）へ移る。
pub struct RamModelCache {
    map: HashMap<String, RamEntry>,
    /// 現在の推定合計バイト数。
    bytes: usize,
    /// 上限バイト数（0 ならキャッシュしない）。
    budget_bytes: usize,
    /// LRU 用の単調カウンタ。
    tick: u64,
}

impl RamModelCache {
    /// 上限バイト数を指定して空のキャッシュを作る。
    pub fn new(budget_bytes: usize) -> Self {
        Self {
            map: HashMap::new(),
            bytes: 0,
            budget_bytes,
            tick: 0,
        }
    }

    /// 現在の推定合計バイト数。
    pub fn bytes(&self) -> usize {
        self.bytes
    }

    /// 保持件数。
    pub fn len(&self) -> usize {
        self.map.len()
    }

    /// 完成モデルを格納する（上限超過分は古い順に捨てる）。
    ///
    /// 単体で上限を超える巨大モデルは「入れた直後に自分自身が追い出される」ため
    /// 実質キャッシュされない。呼び出し側は戻り値で判断せず、必要なら `take` で
    /// 取り出してから使うこと（`take` は格納前の値を返さないので、
    /// 呼び出し側は Arc を自前で保持しておく）。
    pub fn insert(&mut self, path: &str, model: Arc<Model>, bytes: usize) {
        if self.budget_bytes == 0 {
            return;
        }
        self.tick += 1;
        let tick = self.tick;
        if let Some(old) = self.map.insert(
            path.to_string(),
            RamEntry {
                model,
                bytes,
                tick,
            },
        ) {
            self.bytes = self.bytes.saturating_sub(old.bytes);
        }
        self.bytes = self.bytes.saturating_add(bytes);
        self.evict_to_budget();
    }

    /// 参照して取り出す（LRU の最終アクセス時刻を更新する）。
    pub fn get(&mut self, path: &str) -> Option<Arc<Model>> {
        self.tick += 1;
        let tick = self.tick;
        let e = self.map.get_mut(path)?;
        e.tick = tick;
        Some(Arc::clone(&e.model))
    }

    /// キャッシュから取り除いて返す。
    ///
    /// 実際に使われ始めたモデルは `DrawContext::model_cache` が保持者になるので、
    /// ここから外して RAM 予算を空ける。
    pub fn take(&mut self, path: &str) -> Option<Arc<Model>> {
        let e = self.map.remove(path)?;
        self.bytes = self.bytes.saturating_sub(e.bytes);
        Some(e.model)
    }

    /// 上限に収まるまで最終アクセスが古いものから捨てる。
    fn evict_to_budget(&mut self) {
        while self.bytes > self.budget_bytes && !self.map.is_empty() {
            // 最小 tick のキーを探す（件数はたかだか数百なので線形探索で十分）。
            let Some(victim) = self
                .map
                .iter()
                .min_by_key(|(_, e)| e.tick)
                .map(|(k, _)| k.clone())
            else {
                break;
            };
            if let Some(e) = self.map.remove(&victim) {
                self.bytes = self.bytes.saturating_sub(e.bytes);
            }
        }
    }
}

// ============================================================
//  1 フレームのアップロード予算
// ============================================================

/// 1 フレームで GPU アップロードに使ってよい「件数」と「時間」の予算。
///
/// 非同期化しても、完成品を一度に全部アップロードしたらそこでフレームが飛ぶ。
/// 件数と時間の両方で頭打ちにして、残りは次フレームへ回す。
pub struct UploadBudget {
    /// 残り件数。
    remaining: usize,
    /// 予算の開始時刻。
    started: Instant,
    /// 時間上限（ミリ秒）。
    budget_ms: f32,
}

impl UploadBudget {
    /// 件数・時間の上限を指定して予算を開始する。
    pub fn new(max_count: usize, budget_ms: f32) -> Self {
        Self {
            remaining: max_count,
            started: Instant::now(),
            budget_ms,
        }
    }

    /// もう 1 件アップロードしてよいか。
    ///
    /// - 件数が尽きていれば false
    /// - 経過時間が上限を超えていれば false
    ///
    /// **1 件目は必ず許可する**（時間ゼロ予算でも前に進むようにするため）。
    pub fn allows(&self, uploaded_so_far: usize) -> bool {
        if self.remaining == 0 {
            return false;
        }
        if uploaded_so_far == 0 {
            return true;
        }
        self.started.elapsed().as_secs_f32() * 1000.0 < self.budget_ms
    }

    /// 1 件消費する。
    pub fn consume(&mut self) {
        self.remaining = self.remaining.saturating_sub(1);
    }
}

// ============================================================
//  モデルのおおよその CPU バイト数
// ============================================================

/// Model が抱える大きな配列の合計バイト数を概算する（RAM キャッシュ予算用）。
///
/// 名前・行列・マテリアル定数などの小さなメタデータは無視し、
/// 「頂点／インデックス／メッシュレット／テクスチャピクセル」だけを数える。
/// 予算判定にしか使わないので厳密さは不要。
pub fn estimate_model_bytes(model: &Model) -> usize {
    let mut total = 0usize;

    for td in &model.textures {
        total += match &td.source {
            TextureSource::Ready { mips, .. } => mips.iter().map(|m| m.len()).sum::<usize>(),
            TextureSource::Embedded { pixels, .. } => pixels.len(),
            TextureSource::EncodedBytes { bytes } => bytes.len(),
            TextureSource::FilePath(_) => 0,
        };
    }

    for mesh in &model.meshes {
        for prim in &mesh.primitives {
            total += std::mem::size_of_val(&prim.vertices[..]);
            total += std::mem::size_of_val(&prim.skin_vertices[..]);
            total += std::mem::size_of_val(&prim.indices[..]);
            for lod in &prim.lod_indices {
                total += std::mem::size_of_val(&lod[..]);
            }
            total += std::mem::size_of_val(&prim.meshlet_vertices[..]);
            total += prim.meshlet_triangles.len();
            total += std::mem::size_of_val(&prim.meshlets[..]);
        }
    }

    total
}

// ============================================================
//  ModelStreamer — 本体
// ============================================================

/// ストリーミングロードの統計（ログ用）。
#[derive(Debug, Default, Clone, Copy)]
pub struct StreamStats {
    /// ワーカーが完了したモデル件数（累計）。
    pub completed: u64,
    /// 読み込みに失敗したモデル件数（累計）。
    pub failed: u64,
    /// ワーカー側の累計所要時間（ミリ秒）。
    pub worker_ms_total: f64,
}

/// モデル非同期ロードの司令塔。
///
/// プロセス全体で 1 つ（`init` / `get` のグローバル）。内部は自前で同期しているので
/// `&self` で全操作できる。
pub struct ModelStreamer {
    /// 設定（起動時に確定し、以後変わらない）。
    config: StreamingConfig,
    /// 待ち行列（ワーカーと共有）。
    queue: Arc<(Mutex<QueueInner>, Condvar)>,
    /// ワーカー → メインの完成品チャネル。
    rx: Mutex<crossbeam_channel::Receiver<Completion>>,
    /// 完成済み CPU モデル（メインスレッドのみが触る。Mutex は Sync 要件のため）。
    ready: Mutex<RamModelCache>,
    /// 読み込みに失敗したパス（再要求を無駄に繰り返さないための記憶）。
    failed: Mutex<HashSet<String>>,
    /// 統計。
    stats: Mutex<StreamStats>,
}

impl ModelStreamer {
    /// ワーカースレッドを起動してストリーマを作る。
    pub fn new(config: StreamingConfig) -> Self {
        let queue = Arc::new((Mutex::new(QueueInner::new()), Condvar::new()));
        let (tx, rx) = crossbeam_channel::unbounded::<Completion>();

        for i in 0..config.worker_threads.clamp(1, MAX_WORKER_THREADS) {
            let q = Arc::clone(&queue);
            let tx = tx.clone();
            let prefetch_limit = PREFETCH_MAX_MODEL_CACHE_BYTES;
            std::thread::Builder::new()
                .name(format!("seed-model-loader-{i}"))
                .spawn(move || worker_loop(q, tx, prefetch_limit))
                .expect("モデルロード用ワーカースレッドの起動に失敗しました");
        }

        eprintln!(
            "[SEED stream] 非同期モデルロード開始 workers={} upload={}件/{:.1}ms keep_alive={:.0}s prefetch={} ram_cache={}MiB",
            config.worker_threads,
            config.max_uploads_per_frame,
            config.upload_budget_ms,
            config.keep_alive_secs,
            config.prefetch,
            config.ram_cache_mb,
        );

        let ready = RamModelCache::new(config.ram_cache_bytes());
        Self {
            config,
            queue,
            rx: Mutex::new(rx),
            ready: Mutex::new(ready),
            failed: Mutex::new(HashSet::new()),
            stats: Mutex::new(StreamStats::default()),
        }
    }

    /// 設定を参照する。
    pub fn config(&self) -> &StreamingConfig {
        &self.config
    }

    /// 統計のスナップショットを返す。
    pub fn stats(&self) -> StreamStats {
        *self.stats.lock().unwrap()
    }

    /// 待ち行列に残っているジョブ数（ログ用）。
    pub fn queued(&self) -> usize {
        self.queue.0.lock().unwrap().len()
    }

    /// モデルの読み込みを要求する。
    ///
    /// 既に RAM キャッシュにあれば即座に `Ready` を返すので、呼び出し側は
    /// 「非同期になったせいで 1 フレーム遅れる」ことを避けられる。
    pub fn request(&self, path: &str, priority: JobPriority) -> RequestState {
        if let Some(m) = self.ready.lock().unwrap().get(path) {
            return RequestState::Ready(m);
        }
        // 【失敗の扱い】
        // - 先読み（Prefetch）: 一度失敗したパスは二度と積まない（無駄な再試行と
        //   ログの氾濫を避ける）。
        // - 実使用（OnDemand）: 失敗の記憶を消して**必ず読み直す**。従来の同期ロードは
        //   生成のたびに読みに行っていたので、その挙動と揃える。先読みが一時的な理由で
        //   失敗しただけのモデルが「永久に出てこない」事故も、これで構造的に防げる。
        {
            let mut failed = self.failed.lock().unwrap();
            if failed.contains(path) {
                match priority {
                    JobPriority::Prefetch => return RequestState::Failed,
                    JobPriority::OnDemand => {
                        failed.remove(path);
                    }
                }
            }
        }
        let (lock, cv) = &*self.queue;
        let mut q = lock.lock().unwrap();
        if q.push_model(path, priority) {
            cv.notify_one();
        }
        RequestState::Pending
    }

    /// 完成品を RAM キャッシュへ取り込む（毎フレーム 1 回、メインスレッドで呼ぶ）。
    ///
    /// 戻り値は取り込んだ件数。**ここではディスクアクセスも GPU 操作も行わない**
    /// （チャネルから受け取って HashMap へ入れるだけ）。
    pub fn pump(&self) -> usize {
        let mut taken = 0usize;
        // チャネルは空になるまで一気に吸い出す（溜め込むと RAM を無駄に握る）。
        loop {
            let msg = {
                let rx = self.rx.lock().unwrap();
                match rx.try_recv() {
                    Ok(m) => m,
                    Err(_) => break,
                }
            };
            // 重複排除の印を外す（以後の再要求は改めてキューへ積まれる）。
            {
                let (lock, _) = &*self.queue;
                lock.lock().unwrap().inflight_models.remove(&msg.path);
            }
            let mut st = self.stats.lock().unwrap();
            st.worker_ms_total += msg.worker_ms;
            match msg.result {
                Ok(model) => {
                    st.completed += 1;
                    drop(st);
                    let bytes = estimate_model_bytes(&model);
                    self.ready
                        .lock()
                        .unwrap()
                        .insert(&msg.path, Arc::new(model), bytes);
                }
                Err(e) => {
                    st.failed += 1;
                    drop(st);
                    if msg.prefetch {
                        // 先読みの失敗は実害が無い（そのアクタが実際に生成された
                        // ときに改めて読みに行く）。ゲームの不具合と紛らわしいので
                        // 「先読み」と明示し、注意喚起の温度を下げる。
                        eprintln!(
                            "[SEED stream] 先読みできませんでした（実使用時に再試行します）: {} err={e}",
                            msg.path,
                        );
                    } else {
                        // 従来（同期ロード）と同じ粒度でエラーを出す。
                        eprintln!("[SEED stream] モデルの非同期ロードに失敗: {} err={e}", msg.path);
                    }
                    self.failed.lock().unwrap().insert(msg.path.clone());
                }
            }
            taken += 1;
        }
        taken
    }

    /// 完成済みモデルをキャッシュから取り出す（取り出した分は RAM 予算から外れる）。
    pub fn take_ready(&self, path: &str) -> Option<Arc<Model>> {
        self.ready.lock().unwrap().take(path)
    }

    /// `take_ready` した後に使い道が無くなったモデルを RAM キャッシュへ戻す。
    ///
    /// 差し込み先のアクタが既に破棄されていた場合に使う。捨ててしまうと
    /// 「生成 → 即破棄」を繰り返す演出でディスクから読み直し続けることになる。
    pub fn put_back(&self, path: &str, model: Arc<Model>) {
        let bytes = estimate_model_bytes(&model);
        self.ready.lock().unwrap().insert(path, model, bytes);
    }

    /// このパスの読み込みが失敗済みか。
    pub fn is_failed(&self, path: &str) -> bool {
        self.failed.lock().unwrap().contains(path)
    }

    /// RAM キャッシュの (件数, バイト数)。
    pub fn ram_usage(&self) -> (usize, usize) {
        let c = self.ready.lock().unwrap();
        (c.len(), c.bytes())
    }

    /// `.actor` を 1 件、低優先度で走査対象に積む（参照モデルを先読みする）。
    pub fn prefetch_prefab(&self, actor_path: &str) {
        if !self.config.prefetch {
            return;
        }
        let (lock, cv) = &*self.queue;
        let mut q = lock.lock().unwrap();
        if q.push_prefab_scan(actor_path) {
            cv.notify_one();
        }
    }

    /// アセット配下の `.actor` を走査してプリフェッチを開始する（起動中 1 回だけ）。
    ///
    /// 走査（`read_dir` / PAK エントリ列挙）自体もワーカーで行うため、
    /// メインスレッドはここで一切ディスクに触らない。
    pub fn start_dir_prefetch(&self) {
        if !self.config.prefetch {
            return;
        }
        let (lock, cv) = &*self.queue;
        let mut q = lock.lock().unwrap();
        if q.dir_scan_done {
            return;
        }
        q.dir_scan_done = true;
        q.low.push_back(Job::DirScan {
            dirs: self.config.prefetch_dirs.clone(),
        });
        cv.notify_one();
    }
}

// ============================================================
//  ワーカースレッド
// ============================================================

/// ワーカーのメインループ。ジョブが来るまで `Condvar` で寝る。
fn worker_loop(
    queue: Arc<(Mutex<QueueInner>, Condvar)>,
    tx: crossbeam_channel::Sender<Completion>,
    prefetch_size_limit: u64,
) {
    // このスレッドで出るローダーのログを `[SEED stream/worker]` 側へ振り分ける。
    // メインスレッドが止まっているのか、ワーカーが読んでいるだけなのかを
    // ログだけで判別できるようにするため（性能検証の要）。
    super::mark_current_thread_as_loader_worker();

    loop {
        let job = {
            let (lock, cv) = &*queue;
            let mut q = lock.lock().unwrap();
            loop {
                if let Some(j) = q.pop() {
                    break j;
                }
                q = cv.wait(q).unwrap();
            }
        };

        match job {
            Job::Model { path, priority } => {
                let t0 = Instant::now();
                let result = super::load_model(Path::new(&path)).map_err(|e| e.to_string());
                let worker_ms = t0.elapsed().as_secs_f64() * 1000.0;
                // 受信側（メインスレッド）が落ちている場合は送信失敗するが、
                // その状況はプロセス終了時のみなので黙って続行する。
                let _ = tx.send(Completion {
                    path,
                    result,
                    worker_ms,
                    prefetch: priority == JobPriority::Prefetch,
                });
            }
            Job::PrefabScan { path } => {
                scan_prefab_and_enqueue(&queue, &path, prefetch_size_limit);
            }
            Job::DirScan { dirs } => {
                scan_dirs_and_enqueue(&queue, &dirs);
            }
        }
    }
}

/// `.actor` を読み、参照しているモデルを低優先度で待ち行列へ積む。
///
/// 【形式のマイグレーションを通さない理由】
/// ここは `ActorData` を組み立てず、JSON を `Value` のまま走査して
/// `model_path` の値だけを集める（先読みの対象を決めるだけで、結果は捨てる）。
/// 版が古かろうがキーの名前と値は同じなので、変換を通す必要が無い。
/// **ただし `model_path` のキー名を変える変換を足すときは、ここも直すこと**
/// （変換を通していないので、旧版のファイルでは旧キーのままここに来る）。
/// 版に依存する解釈を足したくなったら、`core::app_base::actor_file` 経由に変える。
fn scan_prefab_and_enqueue(
    queue: &Arc<(Mutex<QueueInner>, Condvar)>,
    actor_path: &str,
    size_limit: u64,
) {
    let Ok(text) = crate::engine::asset_fs::read_string(actor_path) else {
        return;
    };
    let Ok(v) = serde_json::from_str::<serde_json::Value>(&text) else {
        return;
    };

    let mut paths = Vec::new();
    collect_model_paths(&v, &mut paths);

    let (lock, cv) = &**queue;
    let mut pushed = 0usize;
    {
        let mut q = lock.lock().unwrap();
        for p in paths {
            // 大きすぎるモデルは先読みしない（RAM キャッシュを一掃してしまうため）。
            if let Some(sz) = super::asset_cache::cached_model_file_size(Path::new(&p)) {
                if sz > size_limit {
                    continue;
                }
            }
            if q.push_model(&p, JobPriority::Prefetch) {
                pushed += 1;
            }
        }
    }
    for _ in 0..pushed {
        cv.notify_one();
    }
}

/// JSON を再帰的に走査して `model_path` の値を集める。
///
/// `.actor` はコンポーネント配列・子アクタ配列が入れ子になるため、キー名で
/// 再帰的に拾う方式にしてある（`ActorData` の構造変更に強い）。
/// 空文字と `terrain://`（地形チャンクの合成パス）は除外する。
fn collect_model_paths(v: &serde_json::Value, out: &mut Vec<String>) {
    match v {
        serde_json::Value::Object(map) => {
            for (k, val) in map {
                if k == JSON_KEY_MODEL_PATH {
                    if let Some(s) = val.as_str() {
                        if !s.is_empty() && !s.starts_with(TERRAIN_SOURCE_SCHEME) {
                            out.push(s.to_string());
                        }
                    }
                }
                collect_model_paths(val, out);
            }
        }
        serde_json::Value::Array(arr) => {
            for e in arr {
                collect_model_paths(e, out);
            }
        }
        _ => {}
    }
}

/// アセット配下の `.actor` を列挙し、プレハブ走査ジョブを積む。
fn scan_dirs_and_enqueue(queue: &Arc<(Mutex<QueueInner>, Condvar)>, dirs: &[String]) {
    let files =
        crate::engine::asset_fs::list_by_extension(ACTOR_FILE_EXT, dirs, PREFETCH_MAX_ACTOR_FILES);
    if files.is_empty() {
        return;
    }
    eprintln!(
        "[SEED stream] プリフェッチ走査: .actor {} 件（対象 {}）",
        files.len(),
        if dirs.is_empty() {
            "アセットルート全体".to_string()
        } else {
            dirs.join(", ")
        },
    );
    let (lock, cv) = &**queue;
    let mut pushed = 0usize;
    {
        let mut q = lock.lock().unwrap();
        for f in files {
            if q.push_prefab_scan(&f) {
                pushed += 1;
            }
        }
    }
    for _ in 0..pushed {
        cv.notify_one();
    }
}

// ============================================================
//  プロセスグローバル
//
//  `build_actor` は `DrawContext` しか受け取らず App も持たないため、
//  ストリーマ参照を引数で通すと広範囲の署名変更になる。内部で同期している
//  ＝どこから触っても安全なので、プロセス 1 つのグローバルとして置く。
// ============================================================

/// グローバルなストリーマ（起動時に一度だけ初期化）。
static STREAMER: OnceLock<ModelStreamer> = OnceLock::new();

/// ストリーマを初期化する（アプリ起動時に一度だけ呼ぶ）。二度目以降は無視される。
pub fn init(config: StreamingConfig) {
    let _ = STREAMER.set(ModelStreamer::new(config));
}

/// 初期化済みストリーマを返す（未初期化なら None）。
pub fn get() -> Option<&'static ModelStreamer> {
    STREAMER.get()
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 空モデル（テスト用の軽量ダミー）。
    /// `Model` は `Default` を持たない（キャッシュ直列化対象なので派生を増やさない）ため
    /// ここで明示的に空の全フィールドを組む。
    fn dummy_model(name: &str) -> Model {
        Model {
            name: name.to_string(),
            nodes: Vec::new(),
            root_nodes: Vec::new(),
            meshes: Vec::new(),
            materials: Vec::new(),
            textures: Vec::new(),
            animations: Vec::new(),
            skins: Vec::new(),
        }
    }

    // ── 設定の解釈 ───────────────────────────────────────────

    /// JSON が空・壊れている・節が無い場合は既定値になること。
    #[test]
    fn config_defaults_when_absent() {
        assert_eq!(parse_streaming_config_raw(""), StreamingConfig::default());
        assert_eq!(parse_streaming_config_raw("{"), StreamingConfig::default());
        assert_eq!(parse_streaming_config_raw("{}"), StreamingConfig::default());
        assert_eq!(
            parse_streaming_config_raw(r#"{"target_fps":60}"#),
            StreamingConfig::default()
        );
    }

    /// 指定したキーだけが上書きされ、他は既定値のまま残ること（部分指定の許容）。
    #[test]
    fn config_partial_override() {
        let c = parse_streaming_config_raw(r#"{"streaming":{"keep_alive_secs":12.5}}"#);
        assert_eq!(c.keep_alive_secs, 12.5);
        assert_eq!(c.worker_threads, DEFAULT_WORKER_THREADS);
        assert_eq!(c.prefetch, DEFAULT_PREFETCH);
    }

    /// 範囲外・不正値がクランプされること。
    #[test]
    fn config_clamps_out_of_range() {
        let c = parse_streaming_config_raw(
            r#"{"streaming":{"worker_threads":99,"max_uploads_per_frame":0,
                 "keep_alive_secs":-5,"upload_budget_ms":-1,"ram_cache_mb":-8}}"#,
        );
        assert_eq!(c.worker_threads, MAX_WORKER_THREADS);
        assert_eq!(c.max_uploads_per_frame, 1);
        assert_eq!(c.keep_alive_secs, MIN_KEEP_ALIVE_SECS);
        assert!(c.upload_budget_ms > 0.0);
        assert_eq!(c.ram_cache_mb, 0);
    }

    /// `enabled` キーで非同期ロードそのものを切れること。
    #[test]
    fn config_reads_enabled_flag() {
        assert!(parse_streaming_config_raw("{}").enabled, "既定は有効");
        assert!(!parse_streaming_config_raw(r#"{"streaming":{"enabled":false}}"#).enabled);
        assert!(parse_streaming_config_raw(r#"{"streaming":{"enabled":true}}"#).enabled);
    }

    /// 環境変数 `SEED_STREAMING` の解釈。
    #[test]
    fn env_override_disables_streaming_for_falsy_values() {
        for f in ["0", "off", "false", "no", "OFF", " False "] {
            let mut c = StreamingConfig::default();
            apply_env_override(&mut c, f);
            assert!(!c.enabled, "{f} は無効化すべき");
            assert!(!c.prefetch, "{f} では先読みも止めるべき");
        }
    }

    /// `noprefetch` は非同期ロードを残したまま先読みだけ止めること。
    #[test]
    fn env_override_can_disable_prefetch_only() {
        for v in ["noprefetch", "NoPrefetch", " no_prefetch "] {
            let mut c = StreamingConfig::default();
            apply_env_override(&mut c, v);
            assert!(c.enabled, "{v} では非同期ロードは有効のまま");
            assert!(!c.prefetch, "{v} は先読みを止めるべき");
        }
    }

    /// 未知の値はプロジェクト設定を一切上書きしないこと。
    #[test]
    fn env_override_ignores_unknown_values() {
        let base = StreamingConfig::default();
        for v in ["1", "on", "true", "", "yes"] {
            let mut c = base.clone();
            apply_env_override(&mut c, v);
            assert_eq!(c, base, "{v} は設定を変えてはいけない");
        }
    }

    /// prefetch_dirs が読めること（空文字は落とす）。
    #[test]
    fn config_reads_prefetch_dirs() {
        let c = parse_streaming_config_raw(
            r#"{"streaming":{"prefetch_dirs":["mainGame/actors","",  "fx"]}}"#,
        );
        assert_eq!(c.prefetch_dirs, vec!["mainGame/actors", "fx"]);
    }

    /// 常駐秒数 → フレーム数の換算（無制限 fps は基準 fps へフォールバック）。
    #[test]
    fn keep_alive_seconds_to_frames() {
        let c = StreamingConfig {
            keep_alive_secs: 30.0,
            ..Default::default()
        };
        assert_eq!(c.keep_alive_frames(60), 1800);
        assert_eq!(c.keep_alive_frames(30), 900);
        // 0 = 無制限 → 基準 60fps 換算。
        assert_eq!(c.keep_alive_frames(0), 1800);
        // 極小設定でも 0 フレーム（＝即時解放）にはしない。
        let tiny = StreamingConfig {
            keep_alive_secs: 0.001,
            ..Default::default()
        };
        assert_eq!(tiny.keep_alive_frames(60), 1);
    }

    // ── ジョブキュー（重複排除・優先度）──────────────────────

    /// 同じパスを何度要求しても待ち行列には 1 件しか積まれないこと。
    #[test]
    fn queue_dedupes_same_path() {
        let mut q = QueueInner::new();
        assert!(q.push_model("a.glb", JobPriority::OnDemand));
        assert!(!q.push_model("a.glb", JobPriority::OnDemand));
        assert!(!q.push_model("a.glb", JobPriority::Prefetch));
        assert_eq!(q.len(), 1);
        // 取り込み（pump 相当）で印を外せば再度積める。
        q.inflight_models.remove("a.glb");
        q.pop();
        assert!(q.push_model("a.glb", JobPriority::OnDemand));
    }

    /// 高優先度（要求）が低優先度（プリフェッチ）を必ず追い越すこと。
    #[test]
    fn queue_high_priority_first() {
        let mut q = QueueInner::new();
        q.push_model("pre1.glb", JobPriority::Prefetch);
        q.push_model("pre2.glb", JobPriority::Prefetch);
        q.push_model("now.glb", JobPriority::OnDemand);
        let first = q.pop().unwrap();
        match first {
            Job::Model { path, priority } => {
                assert_eq!(path, "now.glb");
                assert_eq!(priority, JobPriority::OnDemand);
            }
            _ => panic!("Model ジョブのはず"),
        }
    }

    /// 同じ `.actor` は 1 回しか走査しないこと。
    #[test]
    fn queue_dedupes_prefab_scan() {
        let mut q = QueueInner::new();
        assert!(q.push_prefab_scan("assets://a.actor"));
        assert!(!q.push_prefab_scan("assets://a.actor"));
        assert_eq!(q.len(), 1);
    }

    // ── RAM キャッシュ（LRU）──────────────────────────────

    /// 上限を超えたら最終アクセスが古いものから捨てられること。
    #[test]
    fn ram_cache_evicts_least_recently_used() {
        // 1 件 100 バイト想定、上限 250 バイト = 2 件まで。
        let mut c = RamModelCache::new(250);
        c.insert("a", Arc::new(dummy_model("a")), 100);
        c.insert("b", Arc::new(dummy_model("b")), 100);
        // a を触って「最近使った」側にする。
        assert!(c.get("a").is_some());
        c.insert("c", Arc::new(dummy_model("c")), 100);
        // 追い出されるのは b（最終アクセスが最古）。
        assert!(c.get("a").is_some(), "a は残るべき");
        assert!(c.get("c").is_some(), "c は残るべき");
        assert!(c.get("b").is_none(), "b が追い出されるべき");
        assert_eq!(c.len(), 2);
        assert_eq!(c.bytes(), 200);
    }

    /// take で取り出すとバイト数が予算から外れること。
    #[test]
    fn ram_cache_take_releases_budget() {
        let mut c = RamModelCache::new(1000);
        c.insert("a", Arc::new(dummy_model("a")), 400);
        assert_eq!(c.bytes(), 400);
        let got = c.take("a");
        assert!(got.is_some());
        assert_eq!(c.bytes(), 0);
        assert!(c.take("a").is_none());
    }

    /// 上限 0（キャッシュ無効）では何も保持しないこと。
    #[test]
    fn ram_cache_zero_budget_holds_nothing() {
        let mut c = RamModelCache::new(0);
        c.insert("a", Arc::new(dummy_model("a")), 10);
        assert_eq!(c.len(), 0, "上限 0 なら何も保持しない");
    }

    /// 同じキーの再挿入でバイト数が二重計上されないこと。
    #[test]
    fn ram_cache_reinsert_replaces_bytes() {
        let mut c = RamModelCache::new(1000);
        c.insert("a", Arc::new(dummy_model("a")), 400);
        c.insert("a", Arc::new(dummy_model("a")), 100);
        assert_eq!(c.len(), 1);
        assert_eq!(c.bytes(), 100);
    }

    // ── アップロード予算 ─────────────────────────────────────

    /// 件数上限が効くこと。
    #[test]
    fn upload_budget_limits_count() {
        let mut b = UploadBudget::new(2, 1000.0);
        assert!(b.allows(0));
        b.consume();
        assert!(b.allows(1));
        b.consume();
        assert!(!b.allows(2), "件数上限を超えたら false");
    }

    /// 時間予算が 0 でも 1 件目は必ず通ること（前進保証）。
    #[test]
    fn upload_budget_always_allows_first() {
        let b = UploadBudget::new(8, 0.0);
        assert!(b.allows(0), "1 件目は時間予算 0 でも通す");
        assert!(!b.allows(1), "2 件目以降は時間予算で弾かれる");
    }

    // ── モデルのバイト数見積り ────────────────────────────────

    /// 空モデルは 0 バイトになること（見積りが破綻しない下限の確認）。
    #[test]
    fn estimate_bytes_of_empty_model_is_zero() {
        assert_eq!(estimate_model_bytes(&dummy_model("empty")), 0);
    }

    // ── JSON からのモデルパス収集 ─────────────────────────────

    /// 入れ子（コンポーネント配列・子アクタ）を跨いで model_path を拾えること。
    #[test]
    fn collect_model_paths_walks_nested_json() {
        let v: serde_json::Value = serde_json::from_str(
            r#"{
                "name":"root",
                "components":[{"component":{"data":{"model_path":"assets://a.glb"}}}],
                "children":[
                  {"components":[{"component":{"data":{"model_path":"assets://b.glb"}}}]}
                ]
            }"#,
        )
        .unwrap();
        let mut out = Vec::new();
        collect_model_paths(&v, &mut out);
        out.sort();
        assert_eq!(out, vec!["assets://a.glb", "assets://b.glb"]);
    }

    /// 空パスと terrain:// は拾わないこと（実ファイルが無くロードできないため）。
    #[test]
    fn collect_model_paths_skips_empty_and_terrain() {
        let v: serde_json::Value = serde_json::from_str(
            r#"[{"model_path":""},{"model_path":"terrain://chunk/0_0"},{"model_path":"assets://ok.glb"}]"#,
        )
        .unwrap();
        let mut out = Vec::new();
        collect_model_paths(&v, &mut out);
        assert_eq!(out, vec!["assets://ok.glb"]);
    }
}
