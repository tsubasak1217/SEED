// ============================================================
//  physics/thread.rs — Rapier3D バックエンド物理スレッド
//
//  【責務】
//    60Hz 固定タイムステップで rapier3d の物理シミュレーションを実行し、
//    メインスレッドとの間でコマンド・結果をチャンネル経由でやり取りする。
//
//  【設計】
//    ┌ メインスレッド ─────────────────────────────────────┐
//    │  PhysicsThread::send(cmd)    →  cmd_tx              │
//    │  PhysicsThread::recv_latest()←  res_rx              │
//    └───────────────────────────────────────────────────┘
//         ↕ crossbeam-channel
//    ┌ 物理スレッド (run_physics_loop) ───────────────────────┐
//    │  コマンド処理 → Rapier ステップ → 結果収集 → 送信    │
//    └───────────────────────────────────────────────────┘
//
//  【entity_id 管理】
//    DFS 順カウンタ（u64）を Rapier の RigidBodyHandle / ColliderHandle と
//    双方向 HashMap でマッピングする。
// ============================================================

// rapier3d のプレリュードをインポート（RigidBodySet, ColliderSet 等の物理型）
use rapier3d::prelude::*;
// nalgebra の UnitQuaternion は rapier3d プレリュードに含まれないため直接インポートする
use nalgebra::UnitQuaternion;
use std::collections::{HashMap, HashSet};
use crossbeam_channel::{Receiver, Sender, TryRecvError, unbounded};
use std::time::{Duration, Instant};

// rapier3d の control モジュール（キャラクターコントローラー）は prelude に含まれないため直接インポートする
use rapier3d::control::{CharacterAutostep, CharacterLength, KinematicCharacterController};

// SEED 側の物理型（Rapier の CollisionEvent と名前が衝突するため別名でインポートする）
use super::types::{
    CharacterMoveResult,
    ColliderShape,
    CollisionEvent as SeedCollisionEvent, CollisionPhase,
    PhysicsCommand, PhysicsObject, PhysicsResult,
    TriggerEvent as SeedTriggerEvent, TriggerPhase,
    DEFAULT_GRAVITY, PHYSICS_FIXED_STEP,
};

// ─── キャラクターコントローラー診断ログ ──────────────────────────────────────
//
//  ResolveCharacter（キャラクター同期解決）の切り分け用ログ。既定オフ。
//  環境変数 SEED_CHAR_LOG を設定したときだけ、最初の N 回だけ stderr へ出す。
//  既存の PHYS_LOG_ENABLED（physics_ops.rs）と同じ作法（既定オフ・件数上限で洪水防止）。

/// キャラクター診断ログ（`[CharCtl]` 行）を出力するかどうか。
/// 【一時診断】貫通バグ切り分けのため常時 ON（上限 CHAR_LOG_MAX_FRAMES で洪水防止）。原因特定後に env ゲートへ戻す。
static CHAR_LOG_ENABLED: std::sync::LazyLock<bool> =
    std::sync::LazyLock::new(|| true || std::env::var_os("SEED_CHAR_LOG").is_some());

/// これまでに出力した `[CharCtl]` 行の件数（洪水防止の上限判定に使う）。
static CHAR_LOG_FRAMES: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

/// `[CharCtl]` 行を出す最大件数（起動直後の切り分けに十分な数だけ出して以降は黙る）。
const CHAR_LOG_MAX_FRAMES: u32 = 240;

// ─── キャラクターコントローラー（KCC）パラメータ定数 ──────────────────────────
//
//  キャラクターコントローラー（StepCharacter による自動押し戻し）が使う KCC の設定値。
//  rapier3d の既定値をベースに、SEED のメートル単位（1 unit = 1m）に合わせて
//  段差・スロープ・スナップの各しきい値を絶対値で固定する（データドリブンに
//  差し替えたくなったら将来コンポーネント化する想定で、まずは定数化する）。

/// キャラクターと周囲の間に保つ微小な隙間（メートル・スキン幅）。
/// 0 だと数値的に不安定になるため小さな正の値を絶対指定する（1cm）。
const KCC_SKIN_OFFSET_M: Real = 0.01;

/// 登れる最大スロープ角（度）。これを超える斜面は壁として扱い、登らず滑る。
/// 45 度は rapier 既定（π/4）と同じで、一般的なキャラ移動の妥当値。
const KCC_MAX_SLOPE_CLIMB_DEG: Real = 45.0;

/// 自動で滑り落ち始める最小スロープ角（度）。これ未満の斜面には立てる。
/// climb と同じ 45 度にし、「登れる限界＝立てる限界」で挙動を一貫させる。
const KCC_MIN_SLOPE_SLIDE_DEG: Real = 45.0;

/// 自動で乗り越えられる段差（階段）の最大高さ（メートル・絶対値）。
/// 0.3m は一般的な階段 1 段ぶんに相当し、キャラが引っかからず登れる。
const KCC_AUTOSTEP_MAX_HEIGHT_M: Real = 0.3;

/// 段差を登った先に必要な最小の平地幅（メートル・絶対値）。
/// これ未満の狭い足場には段差昇降しない（縁に爪先立ちして揺れるのを防ぐ）。
const KCC_AUTOSTEP_MIN_WIDTH_M: Real = 0.05;

/// 地面から足がこの距離以内なら接地状態へスナップする（メートル・絶対値）。
/// 下り坂・階段を降りるときにキャラが浮かず地面に吸着する。0.3m は段差高さと同程度。
const KCC_SNAP_TO_GROUND_M: Real = 0.3;

/// SEED のキャラクターコントローラー設定を構築する。
///
/// 上記の KCC_* 定数から `KinematicCharacterController` を組み立てる純粋関数。
/// 物理スレッド起動時に 1 度だけ生成し、StepCharacter コマンドで使い回す。
fn make_character_controller() -> KinematicCharacterController {
    KinematicCharacterController {
        // 既定の上方向（+Y）・壁ずり有効はそのまま使う
        up:     Vector::y_axis(),
        slide:  true,
        offset: CharacterLength::Absolute(KCC_SKIN_OFFSET_M),
        // 段差の自動乗り越え（階段）を有効化する。
        // ※ rapier のドキュメント上 autostep は計算コストが高い機能だが、
        //   本 API は 1 フレーム 1 回の同期クエリのため許容範囲。段差昇降は要件。
        autostep: Some(CharacterAutostep {
            max_height:             CharacterLength::Absolute(KCC_AUTOSTEP_MAX_HEIGHT_M),
            min_width:              CharacterLength::Absolute(KCC_AUTOSTEP_MIN_WIDTH_M),
            // 動的ボディの上には段差昇降しない（意図しない乗り上げを防ぐ）
            include_dynamic_bodies: false,
        }),
        max_slope_climb_angle: (KCC_MAX_SLOPE_CLIMB_DEG as Real).to_radians(),
        min_slope_slide_angle: (KCC_MIN_SLOPE_SLIDE_DEG as Real).to_radians(),
        snap_to_ground:        Some(CharacterLength::Absolute(KCC_SNAP_TO_GROUND_M)),
        // すり抜け防止の法線ナッジは既定値
        ..KinematicCharacterController::default()
    }
}

// ─── スムーズドラッグ速度クランプ定数 ────────────────────────────────────────

/// スムーズドラッグ中の kinematic ボディが目標へ追従する最大並進速度（m/s）。
///
/// set_next_kinematic_position は「(目標 - 現在)/dt」を接触相手へ伝える速度とするため、
/// エディタフレームが渡す 1 フレーム分の移動 Δ でも Δ/dt が大きくなり、床の上に乗った
/// Dynamic ボディを吹き飛ばす。1 ステップの移動量を MAX_DRAG_LINEAR_SPEED*dt に制限すると
/// 伝わる速度がこの値で頭打ちになり、乗っているボディは滑らかに押されるだけになる。
/// （dt=1/60 のとき 1 ステップ上限 = 約 0.083m。標準的な編集ドラッグはこれ未満で追従し、
///  素早い/フレーム落ちによる巨大ジャンプのみがクランプされる）
const MAX_DRAG_LINEAR_SPEED: Real = 5.0;

/// スムーズドラッグ中の kinematic ボディが目標へ追従する最大角速度（rad/s）。
/// 並進と同様、回転差分 Δθ/dt が過大にならないよう 1 ステップの回転量を制限する。
const MAX_DRAG_ANGULAR_SPEED: Real = 6.0;

// ─── PhysicsThread（公開 API）────────────────────────────────────────────────

/// Rapier 物理シミュレーションを実行するバックグラウンドスレッドのハンドル。
///
/// Drop 時に Stop コマンドを送信してスレッドを安全に終了させる。
pub struct PhysicsThread {
    /// コマンド送信チャンネル（メイン → 物理スレッド）
    cmd_tx: Sender<PhysicsCommand>,
    /// 結果受信チャンネル（物理スレッド → メイン）
    res_rx: Receiver<PhysicsResult>,
    /// スレッドハンドル（Drop 時に join する）
    handle: Option<std::thread::JoinHandle<()>>,
}

impl PhysicsThread {
    /// 物理スレッドを起動して PhysicsThread ハンドルを返す。
    pub fn spawn() -> Self {
        let (cmd_tx, cmd_rx) = unbounded::<PhysicsCommand>();
        let (res_tx, res_rx) = unbounded::<PhysicsResult>();

        let handle = std::thread::spawn(move || {
            run_physics_loop(cmd_rx, res_tx);
        });

        PhysicsThread { cmd_tx, res_rx, handle: Some(handle) }
    }

    /// コマンドを物理スレッドへ送信する。
    pub fn send(&self, cmd: PhysicsCommand) {
        let _ = self.cmd_tx.send(cmd);
    }

    /// コマンド送信チャンネルのクローンを返す。
    /// スクリプトの Physics.Raycast（host_api）が物理スレッドへ
    /// 同期問い合わせするために使用する。
    pub fn command_sender(&self) -> Sender<PhysicsCommand> {
        self.cmd_tx.clone()
    }

    /// 蓄積された結果から最新の 1 件を取得し、古い結果は破棄する。
    ///
    /// 結果がなければ `None` を返す。
    pub fn recv_latest(&self) -> Option<PhysicsResult> {
        let mut latest = None;
        loop {
            match self.res_rx.try_recv() {
                Ok(result)                     => { latest = Some(result); }
                Err(TryRecvError::Empty)        => break,
                Err(TryRecvError::Disconnected) => break,
            }
        }
        latest
    }
}

impl Drop for PhysicsThread {
    fn drop(&mut self) {
        // Stop コマンドを送信してスレッドを終了させ、join する
        let _ = self.cmd_tx.send(PhysicsCommand::Stop);
        if let Some(handle) = self.handle.take() {
            let _ = handle.join();
        }
    }
}

// ─── 物理スレッド内部エントリ ────────────────────────────────────────────────

/// 物理ワールドに登録した 1 オブジェクトの Rapier ハンドル情報。
struct PhysicsEntry {
    /// Rapier リジッドボディハンドル（Static の場合は None）
    rb_handle:  Option<RigidBodyHandle>,
    /// Rapier コライダーハンドル
    col_handle: ColliderHandle,
    /// Dynamic Rigidbody かどうか（Transform 結果送信の対象か）
    is_dynamic: bool,
    /// Static コライダー（rb_handle=None）のローカルオフセット [x, y, z]。
    /// UpdateKinematic でコライダー位置を直接更新するときに使用する。
    col_offset: [f32; 3],
    /// ドラッグ押し戻し用に動的生成した KinematicPositionBased リジッドボディかどうか。
    /// true の場合、SetBodyKinematic(false) でリジッドボディを削除して
    /// standalone static コライダーに戻す。
    rb_created_for_drag: bool,
    /// Static コライダーのドラッグ終了後の再生成に必要なデータ。
    /// rb_handle=None のときのみ Some になる。
    col_shape_data: Option<StaticColliderData>,
    /// キャラクターコントローラーかどうか（StepCharacter による差分解決の対象か）。
    is_character: bool,
    /// キャラクターコントローラーの「前回解決済みワールド位置」[x, y, z]。
    /// ResolveCharacter が (希望位置 - この値) を moveVector として解決し、補正後位置で更新する。
    /// 初期値はコライダー登録時のアクターワールド位置。
    char_last_pos: [f32; 3],
    // 【撤去】接地判定は ResolveCharacter の reply でその場返しするため、entry に保持しない。
}

/// SetBodyKinematic によるコライダー再生成のためのデータ。
///
/// 静的コライダーをドラッグ中に一時的に Kinematic RB に変換し、
/// ドラッグ終了後に元の静的コライダーに戻すために使用する。
#[derive(Clone)]
struct StaticColliderData {
    shape:       ColliderShape,
    scale:       [f32; 3],
    friction:    f32,
    restitution: f32,
    is_trigger:  bool,
}

// ─── メインループ ────────────────────────────────────────────────────────────

/// 物理スレッドのメインループ。
///
/// コマンドを処理し、固定タイムステップで Rapier シミュレーションを進め、
/// 結果をメインスレッドへ送り続ける。`Stop` コマンドを受信したら終了する。
fn run_physics_loop(
    cmd_rx: Receiver<PhysicsCommand>,
    res_tx: Sender<PhysicsResult>,
) {
    // ── Rapier 物理ワールドオブジェクト ──────────────────────────────────────
    let mut rigid_body_set      = RigidBodySet::new();
    let mut collider_set        = ColliderSet::new();
    let mut physics_pipeline    = PhysicsPipeline::new();
    let mut island_manager      = IslandManager::new();
    let mut broad_phase         = DefaultBroadPhase::new();
    let mut narrow_phase        = NarrowPhase::new();
    let mut impulse_joint_set   = ImpulseJointSet::new();
    let mut multibody_joint_set = MultibodyJointSet::new();
    let mut ccd_solver          = CCDSolver::new();
    // レイキャスト・キャラクター移動用クエリパイプライン（step のたびに自動更新される）
    let mut query_pipeline      = QueryPipeline::new();

    // キャラクターコントローラー（StepCharacter の自動押し戻し用）。
    // 定数から 1 度だけ生成して StepCharacter コマンドで使い回す（状態を持たない）。
    let character_controller = make_character_controller();

    // 重力ベクトル（SetGravity コマンドで変更可能）
    let mut gravity = vector![DEFAULT_GRAVITY[0], DEFAULT_GRAVITY[1], DEFAULT_GRAVITY[2]];

    // 統合パラメータ（固定ステップ時間を設定）
    let mut integration_params = IntegrationParameters::default();
    integration_params.dt = PHYSICS_FIXED_STEP as Real;

    // ── entity_id ↔ Rapier ハンドル マッピング ──────────────────────────────
    let mut entries:         HashMap<u64, PhysicsEntry>  = HashMap::new();
    let mut col_to_entity:   HashMap<ColliderHandle, u64> = HashMap::new();
    let mut trigger_set:     HashSet<u64>                 = HashSet::new();
    // 継続中の衝突ペア（Stay/Exit 検出用。フレーム間で維持する）
    let mut active_contacts: HashSet<(u64, u64)>          = HashSet::new();
    let mut active_triggers: HashSet<(u64, u64)>          = HashSet::new();

    // ── スムーズドラッグ状態 ────────────────────────────────────────────────
    // SetBodyKinematic(is_kinematic=true, smooth=true) された「スムーズドラッグ中」
    // ボディの entity_id → 目標ワールド姿勢のマップ。
    // このマップにキーが存在する間、UpdateKinematic は即時反映せず目標だけを更新し、
    // 毎ステップ直前に advance_smooth_drag_targets が最大速度クランプ付きで目標へ
    // 追従させる。これによりエディタフレームの目標ジャンプ（Δ）がそのまま
    // set_next_kinematic_position され「Δ/dt = 無制限の伝達速度」になるのを防ぐ。
    // （キーの有無がスムーズドラッグ中フラグを兼ねる）
    let mut drag_targets:    HashMap<u64, Isometry<Real>> = HashMap::new();

    // ── Rapier イベントコレクター ────────────────────────────────────────────
    // ChannelEventCollector は接触開始・終了イベントをチャンネル経由で提供する
    // rapier3d 0.22 の CollisionEvent は rapier3d::prelude から使用する
    let (col_evt_tx, col_evt_rx) = unbounded::<CollisionEvent>();
    let (force_evt_tx, _force_rx) = unbounded::<ContactForceEvent>();
    let event_handler = ChannelEventCollector::new(col_evt_tx, force_evt_tx);

    // ── タイムステップ制御 ────────────────────────────────────────────────────
    let step_duration = Duration::from_secs_f64(PHYSICS_FIXED_STEP);
    let mut next_step = Instant::now();

    // Pause/Resume 状態（true のとき物理ステップをスキップ、速度は保持される）
    let mut paused = false;

    loop {
        // ── コマンド処理（全キューをフラッシュ）────────────────────────────
        loop {
            match cmd_rx.try_recv() {
                Ok(PhysicsCommand::Stop)   => return,
                Ok(PhysicsCommand::Pause)  => { paused = true; }
                Ok(PhysicsCommand::Resume) => {
                    paused = false;
                    // Resume 直後にタイムステップをリセットして即座に次ステップを実行する
                    next_step = Instant::now();
                }
                Ok(PhysicsCommand::Raycast { origin, direction, max_distance, reply }) => {
                    // 同期レイキャスト問い合わせ（スクリプトの Physics.Raycast）。
                    // query_pipeline は step のたびに更新済み。
                    let hit = perform_raycast(
                        &query_pipeline, &rigid_body_set, &collider_set, &col_to_entity,
                        origin, direction, max_distance,
                    );
                    let _ = reply.send(hit);
                }
                Ok(PhysicsCommand::ResolveCharacter { entity_id, desired_position, rotation, reply }) => {
                    // キャラクターコントローラーの差分解決（同期問い合わせ・フレーム 1 回）。
                    // query_pipeline は step のたびに更新済み。前回解決済み位置との差分を
                    // KCC で衝突解決し、entry の char_last_pos を更新すると同時に
                    // 物理ワールドのコライダー位置も補正後位置へ同期する。
                    // 補正後位置＋接地を reply でその場返しし、メインが描画前に ECS へ書き戻す。
                    //
                    // 【診断ログ】SEED_CHAR_LOG 設定時のみ、最初の N 回だけ切り分け情報を出す。
                    // 物理 entry の有無・desired・motion・KCC 補正・corrected・grounded に加え、
                    // 「物理ワールドに Static コライダー（地形）が登録されているか」を出力し、
                    // 地形が物理世界に無くて検出できないケースを切り分けられるようにする。
                    let diag = *CHAR_LOG_ENABLED
                        && CHAR_LOG_FRAMES.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
                            < CHAR_LOG_MAX_FRAMES;
                    // 補正前の前回位置（motion 算出・ログ用）を退避する。
                    let last_before = entries.get(&entity_id).map(|e| e.char_last_pos);

                    let resolved = perform_character_step(
                        &character_controller, &query_pipeline,
                        &mut rigid_body_set, &mut collider_set, &mut entries,
                        entity_id, desired_position, rotation,
                        PHYSICS_FIXED_STEP as Real,
                    );

                    if diag {
                        // Static コライダー（RigidBody を持たない＝地形・静的プロップ）の登録数。
                        // 0 なら「地形が物理ワールドに無い」＝キャラが検出できないケース。
                        let static_colliders = entries.values()
                            .filter(|e| e.rb_handle.is_none() && !e.is_character)
                            .count();
                        let total_colliders = collider_set.len();
                        // 【追加診断】キャラ位置から真下(-Y)へレイを撃ち、地形表面までの距離を測る。
                        //   Some(d)=直下 d[m] に地形あり（キャラは地形の上空に浮いている）
                        //   None   =直下に地形が無い（地形内部に埋没 or 地形が検出されない）
                        // これで「浮遊」「埋没」「未検出」を切り分ける。
                        let down_hit: Option<f32> = last_before.and_then(|last| {
                            let ray = Ray::new(
                                point![last[0], last[1], last[2]],
                                vector![0.0, -1.0, 0.0],
                            );
                            let filter = entries.get(&entity_id)
                                .map(|e| QueryFilter::default().exclude_collider(e.col_handle))
                                .unwrap_or_default();
                            query_pipeline
                                .cast_ray(&rigid_body_set, &collider_set, &ray, 500.0, true, filter)
                                .map(|(_, toi)| toi)
                        });
                        match (last_before, resolved) {
                            (Some(last), Some((corrected, grounded))) => {
                                let motion = [
                                    desired_position[0] - last[0],
                                    desired_position[1] - last[1],
                                    desired_position[2] - last[2],
                                ];
                                let applied = [
                                    corrected[0] - last[0],
                                    corrected[1] - last[1],
                                    corrected[2] - last[2],
                                ];
                                eprintln!(
                                    "[CharCtl] id={id} entry=found desired=({dx:.3},{dy:.3},{dz:.3}) \
                                     last=({lx:.3},{ly:.3},{lz:.3}) motion=({mx:.3},{my:.3},{mz:.3}) \
                                     kcc_move=({ax:.3},{ay:.3},{az:.3}) corrected=({cx:.3},{cy:.3},{cz:.3}) \
                                     grounded={g} down_hit={dh:?} | colliders total={tc} static={sc}",
                                    id = entity_id,
                                    dx = desired_position[0], dy = desired_position[1], dz = desired_position[2],
                                    lx = last[0], ly = last[1], lz = last[2],
                                    mx = motion[0], my = motion[1], mz = motion[2],
                                    ax = applied[0], ay = applied[1], az = applied[2],
                                    cx = corrected[0], cy = corrected[1], cz = corrected[2],
                                    g = grounded, dh = down_hit, tc = total_colliders, sc = static_colliders,
                                );
                            }
                            _ => {
                                eprintln!(
                                    "[CharCtl] id={id} entry={found} 解決失敗（キャラ未登録/コライダー無し）\
                                     desired=({dx:.3},{dy:.3},{dz:.3}) | colliders total={tc} static={sc}",
                                    id = entity_id,
                                    found = if last_before.is_some() { "found" } else { "MISSING" },
                                    dx = desired_position[0], dy = desired_position[1], dz = desired_position[2],
                                    tc = total_colliders, sc = static_colliders,
                                );
                            }
                        }
                    }

                    let _ = reply.send(resolved);
                }
                Ok(PhysicsCommand::CheckKinematicOverlap { entity_id, position, rotation, reply }) => {
                    // 同期オーバーラップ問い合わせ（編集時ドラッグの押し戻し判定）。
                    // Pause 中もコマンドドレインは回り続けるため必ず応答できる。
                    let overlapping = check_kinematic_overlap(
                        &query_pipeline, &rigid_body_set, &collider_set,
                        &entries, &trigger_set,
                        entity_id, position, rotation,
                    );
                    let _ = reply.send(overlapping);
                }
                Ok(cmd) => handle_command(
                    cmd,
                    &mut rigid_body_set, &mut collider_set,
                    &mut island_manager, &mut impulse_joint_set, &mut multibody_joint_set,
                    &mut entries, &mut col_to_entity,
                    &mut trigger_set, &mut active_contacts, &mut active_triggers,
                    &mut gravity, &mut drag_targets,
                ),
                Err(TryRecvError::Empty)          => break,
                Err(TryRecvError::Disconnected)   => return,
            }
        }

        // Pause 中は物理ステップをスキップ（速度・内部状態は保持）
        if paused {
            std::thread::sleep(Duration::from_millis(5));
            continue;
        }

        // ── 物理ステップ ─────────────────────────────────────────────────────
        let now = Instant::now();
        if now < next_step {
            // 次ステップまで 1ms 以内でスリープしてコマンド受信を維持する
            let remaining = next_step - now;
            std::thread::sleep(remaining.min(Duration::from_millis(1)));
            continue;
        }

        // スムーズドラッグ: このステップの次目標位置を最大速度クランプ付きで更新する。
        // ステップ直前・かつ実際にステップを実行するタイミングでのみ前進させることで、
        // 1 ステップあたりの移動量（= 伝達速度 × dt）を確実に上限内に収める。
        advance_smooth_drag_targets(&mut rigid_body_set, &entries, &drag_targets, PHYSICS_FIXED_STEP as Real);

        physics_pipeline.step(
            &gravity,
            &integration_params,
            &mut island_manager,
            &mut broad_phase,
            &mut narrow_phase,
            &mut rigid_body_set,
            &mut collider_set,
            &mut impulse_joint_set,
            &mut multibody_joint_set,
            &mut ccd_solver,
            Some(&mut query_pipeline), // レイキャスト用に毎ステップ更新する
            &(),    // PhysicsHooks（デフォルト: フィルタリングなし）
            &event_handler,
        );

        // 次ステップ時刻を進める（スキップ蓄積を防ぐために now + duration）
        next_step = now + step_duration;

        // ── 結果収集・送信 ────────────────────────────────────────────────────
        let result = collect_results(
            &rigid_body_set,
            &narrow_phase,
            &entries,
            &col_evt_rx,
            &col_to_entity,
            &trigger_set,
            &mut active_contacts,
            &mut active_triggers,
        );

        let _ = res_tx.send(result);
    }
}

// ─── レイキャスト ────────────────────────────────────────────────────────────

/// クエリパイプラインでレイキャストを実行し、最初のヒットを返す。
///
/// スクリプトの Physics.Raycast 用。ヒットしたコライダーは col_to_entity で
/// entity_id（DFS 順 ID）へ逆引きする。ヒットなし・逆引き失敗は None。
fn perform_raycast(
    query_pipeline: &QueryPipeline,
    rb_set:         &RigidBodySet,
    col_set:        &ColliderSet,
    col_to_entity:  &HashMap<ColliderHandle, u64>,
    origin:         [f32; 3],
    direction:      [f32; 3],
    max_distance:   f32,
) -> Option<crate::engine::physics::types::RaycastHit> {
    let ray = Ray::new(
        point![origin[0], origin[1], origin[2]],
        vector![direction[0], direction[1], direction[2]],
    );
    // solid=true: レイ始点がコライダー内部にある場合は距離 0 でヒットさせる
    let (handle, intersection) = query_pipeline.cast_ray_and_get_normal(
        rb_set, col_set, &ray, max_distance, true, QueryFilter::default(),
    )?;
    let entity_id = *col_to_entity.get(&handle)?;
    let point  = ray.point_at(intersection.time_of_impact);
    let normal = intersection.normal;
    Some(crate::engine::physics::types::RaycastHit {
        entity_id,
        point:    [point.x, point.y, point.z],
        normal:   [normal.x, normal.y, normal.z],
        distance: intersection.time_of_impact,
    })
}

// ─── キャラクターコントローラー移動 ──────────────────────────────────────────

/// キャラクターコントローラーで希望移動量を衝突解決し、実効移動＋接地を返す。
///
/// StepCharacter（キャラクター自動押し戻し）の内部で使う。対象アクターの既存カプセルコライダー形状を
/// KCC のシェイプに使い、地形・静的コライダーとの衝突を rapier の
/// `move_shape` に解決させる（壁ずり・段差・スロープは KCC の標準機能）。
///
/// - 対象自身のコライダー／リジッドボディは自己衝突しないよう除外する。
/// - センサー（トリガー）は応答なしのため除外する。
/// - move_shape はクエリのみでコライダー位置を変更しない。実際の位置反映（ECS 更新）は
///   呼び出し元 FFI が集約関数 `set_actor_world_transform` 経由で行う。
///
/// 対象 entity_id が未登録・コライダーが引けない場合は None を返す。
#[allow(clippy::too_many_arguments)]
fn perform_character_move(
    kcc:            &KinematicCharacterController,
    query_pipeline: &QueryPipeline,
    rb_set:         &RigidBodySet,
    col_set:        &ColliderSet,
    entries:        &HashMap<u64, PhysicsEntry>,
    entity_id:      u64,
    position:       [f32; 3],
    rotation:       [f32; 4],
    motion:         [f32; 3],
    dt:             Real,
) -> Option<CharacterMoveResult> {
    // 対象アクターのコライダーを解決し、その形状を KCC シェイプに使う
    let entry = entries.get(&entity_id)?;
    let col   = col_set.get(entry.col_handle)?;
    let shape = col.shared_shape().clone();

    // シェイプの基準姿勢 = アクターワールド姿勢 × コライダーローカルオフセット。
    // motion はワールド空間の希望移動量なので姿勢に依存しない。
    let o          = entry.col_offset;
    let offset_iso = Isometry::translation(o[0], o[1], o[2]);
    let char_pos   = to_isometry(position, rotation) * offset_iso;
    let desired    = vector![motion[0], motion[1], motion[2]];

    // 自己コライダー・自己 RB・センサーを衝突候補から除外する
    let mut filter = QueryFilter::default()
        .exclude_sensors()
        .exclude_collider(entry.col_handle);
    if let Some(rb_h) = entry.rb_handle {
        filter = filter.exclude_rigid_body(rb_h);
    }

    // KCC に衝突解決を委ねる（イベントは使わないので空クロージャ）。
    let movement = kcc.move_shape(
        dt, rb_set, col_set, query_pipeline,
        &*shape, &char_pos, desired, filter, |_| {},
    );

    let t = movement.translation;
    Some(CharacterMoveResult {
        translation: [t.x, t.y, t.z],
        grounded:    movement.grounded,
    })
}

// ─── キャラクターコントローラー差分ステップ ──────────────────────────────────

/// キャラクターコントローラーの「希望位置」を前回解決済み位置との差分で衝突解決する。
///
/// ResolveCharacter コマンド（同期解決）の実体。次を行う純粋手続き:
///   1. moveVector = 希望位置 - 前回解決済み位置（char_last_pos）
///   2. `perform_character_move` で KCC が地形・静的コライダーと衝突解決（壁ずり・段差・スロープ）
///   3. 補正後位置 = 前回位置 + 補正移動量 を求め、char_last_pos を更新
///   4. 物理ワールドのコライダー姿勢も補正後位置へ同期（他オブジェクトのクエリ整合）
///
/// 補正後位置と接地を `Some((corrected, grounded))` で返す。ResolveCharacter の同期 reply が
/// これをそのままメインへ返し、メインが描画前に ECS へ書き戻す。
/// 対象が未登録・キャラクター指定でない・コライダーが引けない場合は `None`（何もしない）。
#[allow(clippy::too_many_arguments)]
fn perform_character_step(
    kcc:              &KinematicCharacterController,
    query_pipeline:   &QueryPipeline,
    rb_set:           &mut RigidBodySet,
    col_set:          &mut ColliderSet,
    entries:          &mut HashMap<u64, PhysicsEntry>,
    entity_id:        u64,
    desired_position: [f32; 3],
    rotation:         [f32; 4],
    dt:               Real,
) -> Option<([f32; 3], bool)> {
    // 前回解決済み位置とキャラクター判定を取得する（キャラでなければ何もしない）
    let (last, is_char) = match entries.get(&entity_id) {
        Some(e) => (e.char_last_pos, e.is_character),
        None    => return None,
    };
    if !is_char { return None; }

    // 希望移動量 = 希望位置 - 前回解決済み位置
    let motion = [
        desired_position[0] - last[0],
        desired_position[1] - last[1],
        desired_position[2] - last[2],
    ];

    // KCC 衝突解決（読み取り専用クエリ）。基準位置は前回解決済み位置。
    // rb_set / col_set は &mut を immutable に自動再借用して渡す。
    let res = perform_character_move(
        kcc, query_pipeline, rb_set, col_set, entries,
        entity_id, last, rotation, motion, dt,
    )?;

    // 補正後位置 = 前回位置 + 補正移動量
    let corrected = [
        last[0] + res.translation[0],
        last[1] + res.translation[1],
        last[2] + res.translation[2],
    ];

    // 前回位置を更新し、物理ワールドのコライダー姿勢も補正後位置へ同期する
    if let Some(entry) = entries.get_mut(&entity_id) {
        entry.char_last_pos = corrected;
        sync_character_collider_pose(rb_set, col_set, entry, corrected, rotation);
    }
    Some((corrected, res.grounded))
}

/// キャラクターコントローラーのコライダー姿勢を物理ワールドへ同期する。
///
/// - kinematic RigidBody を持つ場合: 次ステップの目標位置として set_next_kinematic_position。
/// - Static コライダー（RB なし）の場合: コライダー位置を直接更新（ワールド姿勢 × ローカルオフセット）。
/// - Dynamic の場合（保険）: 位置を直接セットする。
///
/// これにより、他オブジェクトのレイキャスト・衝突判定がキャラクターの補正後位置を参照できる。
fn sync_character_collider_pose(
    rb_set:   &mut RigidBodySet,
    col_set:  &mut ColliderSet,
    entry:    &PhysicsEntry,
    position: [f32; 3],
    rotation: [f32; 4],
) {
    let world_iso = to_isometry(position, rotation);
    if let Some(rb_h) = entry.rb_handle {
        if let Some(rb) = rb_set.get_mut(rb_h) {
            if rb.is_kinematic() {
                rb.set_next_kinematic_position(world_iso);
            } else {
                rb.set_position(world_iso, true);
            }
        }
    } else if let Some(col) = col_set.get_mut(entry.col_handle) {
        // Static コライダー: ワールド位置 = アクターワールド姿勢 × ローカルオフセット
        let o = entry.col_offset;
        let offset_iso = Isometry::from_parts(
            Translation::new(o[0], o[1], o[2]),
            UnitQuaternion::identity(),
        );
        col.set_position(world_iso * offset_iso);
    }
}

// ─── ドラッグ押し戻し用オーバーラップ判定 ────────────────────────────────────

/// ドラッグ押し戻し判定で「めり込み」とみなす侵入深さ（メートル）。
///
/// Rapier のソルバーは静止接触に微小な残留侵入（allowed_linear_error 既定 ≒1mm）を
/// 許容するため、床に載っているだけのオブジェクトも厳密にはわずかに交差している。
/// これを「衝突中」と判定すると水平ドラッグが常に押し戻されて動かせなくなるので、
/// 許容値より十分大きい深さを超えた場合のみブロッキングとみなす。
const DRAG_OVERLAP_PENETRATION_TOLERANCE: f32 = 0.005;

/// 指定ボディを「提案位置」へ置いた場合に、他の非 Dynamic・非センサーコライダーと
/// 許容値を超えて交差する（めり込む）かを判定する。
///
/// CheckKinematicOverlap コマンド（編集時ドラッグの押し戻し）用。
///
/// - 自分自身のコライダーは除外する。
/// - センサー（トリガー）は応答なしのため除外する。自身がトリガーの場合も対象外。
/// - Dynamic ボディは除外する: RigidBody 有効モードではドラッグ中の kinematic ボディが
///   Dynamic を押しのける（Rapier の kinematic→dynamic 相互作用に任せる）ため、
///   Dynamic との一時的な重なりは押し戻し対象にしない。
/// - broad phase 候補は query_pipeline（step のたびに更新済み）で絞り込み、
///   parry の contact クエリで正確な侵入深さを計測して許容値と比較する。
fn check_kinematic_overlap(
    query_pipeline: &QueryPipeline,
    rb_set:         &RigidBodySet,
    col_set:        &ColliderSet,
    entries:        &HashMap<u64, PhysicsEntry>,
    trigger_set:    &HashSet<u64>,
    entity_id:      u64,
    position:       [f32; 3],
    rotation:       [f32; 4],
) -> bool {
    // トリガー（センサー）自身のドラッグは押し戻し対象外
    if trigger_set.contains(&entity_id) { return false; }
    let Some(entry)   = entries.get(&entity_id) else { return false };
    let Some(own_col) = col_set.get(entry.col_handle) else { return false };

    // 提案位置でのコライダーポーズ = アクターワールド姿勢 × ローカルオフセット
    let own_shape  = own_col.shared_shape().clone();
    let o          = entry.col_offset;
    let offset_iso = Isometry::translation(o[0], o[1], o[2]);
    let pose       = to_isometry(position, rotation) * offset_iso;

    // 自分自身・センサー・Dynamic ボディを候補から除外する
    let filter = QueryFilter {
        flags: QueryFilterFlags::EXCLUDE_SENSORS | QueryFilterFlags::EXCLUDE_DYNAMIC,
        exclude_collider: Some(entry.col_handle),
        ..QueryFilter::default()
    };

    // 交差候補ごとに侵入深さを計測し、許容値を超えたら押し戻しが必要と判定する
    let mut blocking = false;
    query_pipeline.intersections_with_shape(
        rb_set, col_set, &pose, &*own_shape, filter,
        |other_handle| {
            if let Some(other) = col_set.get(other_handle) {
                // parry の contact クエリ（prediction=0: 交差時のみ Some）で深さを取得する。
                // dist が負 = 侵入。許容値（静止接触の残留侵入ぶん）を超えたらブロッキング。
                if let Ok(Some(contact)) = rapier3d::parry::query::contact(
                    &pose, &*own_shape, other.position(), other.shape(), 0.0,
                ) {
                    if contact.dist < -DRAG_OVERLAP_PENETRATION_TOLERANCE {
                        blocking = true;
                        return false; // 探索を打ち切る
                    }
                }
            }
            true // 次の候補へ
        },
    );
    blocking
}

// ─── スムーズドラッグ前進 ────────────────────────────────────────────────────

/// スムーズドラッグ中の各 kinematic ボディについて、このステップの次目標位置を
/// 現在位置から目標へ「最大速度クランプ付き」で前進させる。
///
/// set_next_kinematic_position(next) で Rapier に渡す (next - current) を
/// MAX_DRAG_LINEAR_SPEED*dt / MAX_DRAG_ANGULAR_SPEED*dt に制限することで、
/// 接触相手へ伝わる速度 (next-current)/dt がこの上限で頭打ちになる。
/// 目標に十分近づくと残差 = 移動量となり速度は自然にゼロへ収束するため、
/// ドラッグ停止時に乗っているボディへ残る速度も小さく抑えられる。
fn advance_smooth_drag_targets(
    rb_set:       &mut RigidBodySet,
    entries:      &HashMap<u64, PhysicsEntry>,
    drag_targets: &HashMap<u64, Isometry<Real>>,
    dt:           Real,
) {
    let max_lin = MAX_DRAG_LINEAR_SPEED  * dt;
    let max_ang = MAX_DRAG_ANGULAR_SPEED * dt;
    for (entity_id, target) in drag_targets.iter() {
        let Some(entry) = entries.get(entity_id) else { continue };
        let Some(rb_h)  = entry.rb_handle else { continue };
        let Some(rb)    = rb_set.get_mut(rb_h) else { continue };
        if !rb.is_kinematic() { continue; }
        let current = *rb.position();
        let next    = step_isometry_toward(&current, target, max_lin, max_ang);
        rb.set_next_kinematic_position(next);
    }
}

/// `current` から `target` へ、並進を最大 `max_lin`、回転を最大 `max_ang`（ラジアン）
/// だけ進めた中間 Isometry を返す。どちらも上限未満なら target 側の値をそのまま使う。
fn step_isometry_toward(
    current: &Isometry<Real>,
    target:  &Isometry<Real>,
    max_lin: Real,
    max_ang: Real,
) -> Isometry<Real> {
    // 並進クランプ
    let delta = target.translation.vector - current.translation.vector;
    let dist  = delta.norm();
    let new_trans = if dist > max_lin && dist > 1e-9 {
        current.translation.vector + delta * (max_lin / dist)
    } else {
        target.translation.vector
    };

    // 回転クランプ（現在→目標の角度差を max_ang に制限して slerp）
    let angle = current.rotation.angle_to(&target.rotation);
    let new_rot = if angle > max_ang && angle > 1e-9 {
        current.rotation.slerp(&target.rotation, max_ang / angle)
    } else {
        target.rotation
    };

    Isometry::from_parts(Translation::from(new_trans), new_rot)
}

// ─── コマンド処理 ────────────────────────────────────────────────────────────

/// Stop 以外のコマンドを処理する（Stop は呼び出し元のマッチアームで処理済み）。
#[allow(clippy::too_many_arguments)]
fn handle_command(
    cmd:               PhysicsCommand,
    rb_set:            &mut RigidBodySet,
    col_set:           &mut ColliderSet,
    island_manager:    &mut IslandManager,
    impulse_joints:    &mut ImpulseJointSet,
    multibody_joints:  &mut MultibodyJointSet,
    entries:           &mut HashMap<u64, PhysicsEntry>,
    col_to_entity:     &mut HashMap<ColliderHandle, u64>,
    trigger_set:       &mut HashSet<u64>,
    active_contacts:   &mut HashSet<(u64, u64)>,
    active_triggers:   &mut HashSet<(u64, u64)>,
    gravity:           &mut nalgebra::Vector3<Real>,
    drag_targets:      &mut HashMap<u64, Isometry<Real>>,
) {
    match cmd {
        PhysicsCommand::Stop   => { /* 呼び出し元で処理済み */ }
        PhysicsCommand::Pause  => { /* ループ側で処理済み */ }
        PhysicsCommand::Resume => { /* ループ側で処理済み */ }
        PhysicsCommand::Raycast { .. } => { /* ループ側（コマンドドレイン）で処理済み */ }
        PhysicsCommand::ResolveCharacter { .. } => { /* ループ側（コマンドドレイン）で処理済み */ }
        PhysicsCommand::CheckKinematicOverlap { .. } => { /* ループ側（コマンドドレイン）で処理済み */ }

        PhysicsCommand::TeleportCharacter { entity_id, position, rotation } => {
            // キャラクターの瞬間移動（衝突無視）。前回解決済み位置を強制的に position へ上書きし、
            // 次の StepCharacter で差分ゼロ（= 押し戻されない）にする。コライダー位置も即反映する。
            if let Some(entry) = entries.get_mut(&entity_id) {
                if !entry.is_character { return; }
                entry.char_last_pos = position;
                sync_character_collider_pose(rb_set, col_set, entry, position, rotation);
            }
        }

        PhysicsCommand::AddObject(obj) => {
            add_object(obj, rb_set, col_set, entries, col_to_entity, trigger_set);
        }

        PhysicsCommand::RemoveObject { entity_id } => {
            drag_targets.remove(&entity_id);
            if let Some(entry) = entries.remove(&entity_id) {
                col_to_entity.remove(&entry.col_handle);
                trigger_set.remove(&entity_id);
                // 削除エンティティを含む継続衝突ペアを除去する
                active_contacts.retain(|&(a, b)| a != entity_id && b != entity_id);
                active_triggers.retain(|&(t, o)| t != entity_id && o != entity_id);

                if let Some(rb_h) = entry.rb_handle {
                    // RB ごと削除（アタッチされたコライダーも連動削除）
                    rb_set.remove(rb_h, island_manager, col_set, impulse_joints, multibody_joints, true);
                } else {
                    // Static コライダーのみ削除
                    col_set.remove(entry.col_handle, island_manager, rb_set, false);
                }
            }
        }

        PhysicsCommand::UpdateKinematic { entity_id, position, rotation } => {
            // スムーズドラッグ中のボディは即時反映せず「目標」だけ更新する。
            // 実際の前進（速度クランプ付き）はステップ直前の advance_smooth_drag_targets が行う。
            if let Some(target) = drag_targets.get_mut(&entity_id) {
                *target = to_isometry(position, rotation);
                return;
            }
            if let Some(entry) = entries.get(&entity_id) {
                if let Some(rb_h) = entry.rb_handle {
                    // RigidBody がある場合: Kinematic ボディの次ステップ目標位置を設定する
                    // Fixed rb（drag 後に一時的に生成されたものが残っている場合）は更新不要
                    if let Some(rb) = rb_set.get_mut(rb_h) {
                        if rb.is_kinematic() {
                            rb.set_next_kinematic_position(to_isometry(position, rotation));
                        }
                    }
                } else {
                    // Static コライダー（RigidBody なし）の場合:
                    // コライダー位置を直接更新することで Dynamic ボディを押しのけられるようにする。
                    // offset はアクターローカル空間の平行移動なのでワールド回転を適用して合成する。
                    if let Some(col) = col_set.get_mut(entry.col_handle) {
                        let world_iso  = to_isometry(position, rotation);
                        let o = entry.col_offset;
                        let offset_iso = Isometry::from_parts(
                            Translation::new(o[0], o[1], o[2]),
                            UnitQuaternion::identity(),
                        );
                        col.set_position(world_iso * offset_iso);
                    }
                }
            }
        }

        PhysicsCommand::SetBodyKinematic { entity_id, is_kinematic, final_position, smooth } => {
            // Dynamic 復帰・kinematic 解除時はスムーズドラッグ登録を必ず解除する。
            // （最終目標は下で set_position するため、以降のクランプ追従は不要）
            if !is_kinematic {
                drag_targets.remove(&entity_id);
            }
            // ボディタイプを KinematicPositionBased ↔ Dynamic に切り替える。
            // ギズモドラッグ開始時に kinematic=true、終了時に false を送信する。
            //
            // 【static コライダー専用処理】
            //   rb_handle=None (static) + is_kinematic=true:
            //     KinematicPositionBased rb を動的生成してコライダーをアタッチする。
            //     これにより kinematic vs static 間の衝突イベントが有効になる。
            //   rb_created_for_drag=true + is_kinematic=false:
            //     動的生成 rb を削除し standalone static コライダーを再作成する。
            if let Some(entry) = entries.get_mut(&entity_id) {
                let rb_h_opt = entry.rb_handle;
                let created_for_drag = entry.rb_created_for_drag;

                if let Some(rb_h) = rb_h_opt {
                    // ── 既存 RB がある場合 ─────────────────────────────────────────
                    if created_for_drag && !is_kinematic {
                        // 動的生成 rb の解放: rb 削除 → static コライダー再作成
                        let shape_data = entry.col_shape_data.clone();
                        let col_offset = entry.col_offset;

                        // 最終位置を決定する（final_position 優先、なければ rb の現在位置）
                        let final_iso = if let Some((pos, rot)) = final_position {
                            to_isometry(pos, rot)
                        } else {
                            rb_set.get(rb_h).map(|rb| *rb.position()).unwrap_or_else(Isometry::identity)
                        };

                        // rb を削除する（アタッチされたコライダーも削除）
                        col_to_entity.remove(&entry.col_handle);
                        rb_set.remove(rb_h, island_manager, col_set, impulse_joints, multibody_joints, true);

                        if let Some(sd) = shape_data {
                            // ワールド位置 = rb位置 * offset
                            let offset_iso = Isometry::translation(col_offset[0], col_offset[1], col_offset[2]);
                            let col_world_pos = final_iso * offset_iso;

                            // standalone static コライダーを再作成する
                            let new_col = build_collider_shape(&sd.shape, &sd.scale)
                                .position(col_world_pos)
                                .friction(sd.friction)
                                .restitution(sd.restitution)
                                .sensor(sd.is_trigger)
                                .active_events(ActiveEvents::COLLISION_EVENTS)
                                .active_collision_types(ActiveCollisionTypes::all())
                                .build();
                            let new_col_h = col_set.insert(new_col);

                            col_to_entity.insert(new_col_h, entity_id);
                            if let Some(entry) = entries.get_mut(&entity_id) {
                                entry.rb_handle = None;
                                entry.col_handle = new_col_h;
                                entry.rb_created_for_drag = false;
                            }
                        }
                    } else if !created_for_drag {
                        // 通常の Dynamic ↔ Kinematic 切り替え
                        if let Some(rb) = rb_set.get_mut(rb_h) {
                            let new_type = if is_kinematic {
                                RigidBodyType::KinematicPositionBased
                            } else {
                                RigidBodyType::Dynamic
                            };
                            rb.set_body_type(new_type, true);
                            if is_kinematic {
                                // Kinematic 化: 開始位置を確定する。
                                // final_position が提供されている場合（ドラッグ開始時に ECS 位置を渡す）は
                                // その位置を使用する。こうすることで Rapier 内部位置と ECS 位置の
                                // わずかなズレによる持ち上げ時の回転ジャークを防ぐ。
                                // final_position がない場合は rb の現在位置をそのまま維持する。
                                let lock_iso = if let Some((pos, rot)) = final_position {
                                    to_isometry(pos, rot)
                                } else {
                                    *rb.position()
                                };
                                rb.set_next_kinematic_position(lock_iso);
                                // スムーズドラッグ登録: 以降の UpdateKinematic は目標のみ更新し、
                                // advance_smooth_drag_targets が速度クランプ付きで追従させる。
                                // 現在位置を初期目標にして開始（ドラッグ開始フレームの移動をゼロに）。
                                if smooth {
                                    drag_targets.insert(entity_id, lock_iso);
                                }
                            } else {
                                // Dynamic 復帰: 最終座標を明示セット + 速度ゼロリセット
                                if let Some((pos, rot)) = final_position {
                                    rb.set_position(to_isometry(pos, rot), true);
                                }
                                rb.set_linvel(vector![0.0, 0.0, 0.0], true);
                                rb.set_angvel(vector![0.0, 0.0, 0.0], true);
                            }
                        }
                        // ボディタイプ変更後に is_dynamic フラグを同期する。
                        // これがないと kinematic 化後も transform_updates に含まれ続け、
                        // メインスレッドでスキップ漏れが生じた際に ECS が汚染される。
                        entry.is_dynamic = !is_kinematic;
                    }
                } else if is_kinematic {
                    // ── rb_handle=None (static) → KinematicPositionBased に昇格 ───────
                    // コライダーのみモードでドラッグ開始: 衝突検出を有効にするため kinematic rb を動的生成する
                    let shape_data = entry.col_shape_data.clone();
                    let col_offset = entry.col_offset;

                    if let Some(sd) = shape_data {
                        // 現在の standalone コライダー位置を取得する（= actor world + offset）
                        let col_world_pos = col_set.get(entry.col_handle)
                            .map(|c| *c.position())
                            .unwrap_or_else(Isometry::identity);

                        // actor world 位置 = col_world_pos * offset^-1
                        let offset_iso = Isometry::translation(col_offset[0], col_offset[1], col_offset[2]);
                        let actor_world_pos = col_world_pos * offset_iso.inverse();

                        // 既存の standalone コライダーを削除する
                        col_to_entity.remove(&entry.col_handle);
                        col_set.remove(entry.col_handle, island_manager, rb_set, false);

                        // Kinematic リジッドボディを生成する
                        let rb = RigidBodyBuilder::kinematic_position_based()
                            .position(actor_world_pos)
                            .build();
                        let rb_h = rb_set.insert(rb);

                        // コライダーをローカルオフセット付きで RB にアタッチする
                        let new_col = build_collider_shape(&sd.shape, &sd.scale)
                            .position(offset_iso)
                            .friction(sd.friction)
                            .restitution(sd.restitution)
                            .sensor(sd.is_trigger)
                            .active_events(ActiveEvents::COLLISION_EVENTS)
                            // kinematic vs static 接触を有効にする
                            .active_collision_types(ActiveCollisionTypes::all())
                            .build();
                        let new_col_h = col_set.insert_with_parent(new_col, rb_h, rb_set);

                        // 現在位置を kinematic 次ステップ目標として登録する
                        if let Some(rb) = rb_set.get_mut(rb_h) {
                            rb.set_next_kinematic_position(actor_world_pos);
                        }
                        // スムーズドラッグ指定時は目標追従に切り替える（通常は
                        // コライダーのみ押し戻しモードで smooth=false のためスキップ）
                        if smooth {
                            drag_targets.insert(entity_id, actor_world_pos);
                        }

                        col_to_entity.insert(new_col_h, entity_id);
                        if let Some(entry) = entries.get_mut(&entity_id) {
                            entry.rb_handle = Some(rb_h);
                            entry.col_handle = new_col_h;
                            entry.rb_created_for_drag = true;
                        }
                    }
                }
            }
        }

        PhysicsCommand::SetGravity { gravity: g } => {
            *gravity = vector![g[0], g[1], g[2]];
        }

        PhysicsCommand::ApplyForce { entity_id, force } => {
            if let Some(entry) = entries.get(&entity_id) {
                if let Some(rb_h) = entry.rb_handle {
                    if let Some(rb) = rb_set.get_mut(rb_h) {
                        rb.add_force(vector![force[0], force[1], force[2]], true);
                    }
                }
            }
        }

        PhysicsCommand::ApplyTorque { entity_id, torque } => {
            if let Some(entry) = entries.get(&entity_id) {
                if let Some(rb_h) = entry.rb_handle {
                    if let Some(rb) = rb_set.get_mut(rb_h) {
                        rb.add_torque(vector![torque[0], torque[1], torque[2]], true);
                    }
                }
            }
        }

        PhysicsCommand::ApplyImpulse { entity_id, impulse } => {
            if let Some(entry) = entries.get(&entity_id) {
                if let Some(rb_h) = entry.rb_handle {
                    if let Some(rb) = rb_set.get_mut(rb_h) {
                        rb.apply_impulse(vector![impulse[0], impulse[1], impulse[2]], true);
                    }
                }
            }
        }
    }
}

// ─── オブジェクト追加 ────────────────────────────────────────────────────────

/// `PhysicsObject` を Rapier ワールドへ登録する。
///
/// - Dynamic / Kinematic: RigidBody + アタッチされた Collider として登録
/// - Static (`use_rigidbody = false`): Collider のみ（ワールド固定）として登録
fn add_object(
    obj:           PhysicsObject,
    rb_set:        &mut RigidBodySet,
    col_set:       &mut ColliderSet,
    entries:       &mut HashMap<u64, PhysicsEntry>,
    col_to_entity: &mut HashMap<ColliderHandle, u64>,
    trigger_set:   &mut HashSet<u64>,
) {
    let world_iso  = to_isometry(obj.position, obj.rotation);
    let offset_iso = Isometry::translation(
        obj.collider_offset[0],
        obj.collider_offset[1],
        obj.collider_offset[2],
    );

    let friction    = obj.rigidbody.as_ref().map_or(0.5, |rb| rb.friction);
    let restitution = obj.rigidbody.as_ref().map_or(0.3, |rb| rb.restitution);

    // ── リジッドボディ作成 ──────────────────────────────────────────────────
    let rb_handle = obj.rigidbody.as_ref().map(|rb_state| {
        let rb_type = if rb_state.is_kinematic {
            RigidBodyType::KinematicPositionBased
        } else {
            RigidBodyType::Dynamic
        };
        let locked = build_locked_axes(rb_state.freeze_position, rb_state.freeze_rotation);

        let rb = RigidBodyBuilder::new(rb_type)
            .position(world_iso)
            .gravity_scale(rb_state.gravity_scale)
            .linear_damping(rb_state.linear_damping)
            .angular_damping(rb_state.angular_damping)
            .locked_axes(locked)
            .linvel(vector![
                rb_state.linear_velocity[0],
                rb_state.linear_velocity[1],
                rb_state.linear_velocity[2]
            ])
            .angvel(vector![
                rb_state.angular_velocity[0],
                rb_state.angular_velocity[1],
                rb_state.angular_velocity[2]
            ])
            .build();

        rb_set.insert(rb)
    });

    // ── コライダー作成・登録 ─────────────────────────────────────────────────
    // RB がある場合: コライダー位置はローカル空間（RB 位置が起点）
    // Static の場合: コライダー位置はワールド空間（アクタ位置 × オフセット）
    let collider_pos = if rb_handle.is_some() { offset_iso } else { world_iso * offset_iso };

    let collider = build_collider_shape(&obj.collider, &obj.scale)
        .position(collider_pos)
        .friction(friction)
        .restitution(restitution)
        .sensor(obj.is_trigger)
        // すべてのコライダーで衝突イベントを有効にする（kinematic vs static の検出に必要）
        .active_events(ActiveEvents::COLLISION_EVENTS)
        // kinematic vs static（KINEMATIC_FIXED）を含む全ペアで接触判定を有効にする。
        // デフォルトは KINEMATIC_FIXED を含まないため明示的に全フラグを設定する。
        .active_collision_types(ActiveCollisionTypes::all())
        .build();

    let col_handle = match rb_handle {
        Some(rb_h) => col_set.insert_with_parent(collider, rb_h, rb_set),
        None       => col_set.insert(collider),
    };

    let is_dynamic = obj.rigidbody.as_ref().map_or(false, |rb| !rb.is_kinematic);

    // Static コライダー（rb_handle=None）の場合のみ再生成データを保持する
    // ドラッグ押し戻し機能で kinematic rb に変換→解放するときに使用する
    let col_shape_data = if rb_handle.is_none() {
        Some(StaticColliderData {
            shape:       obj.collider.clone(),
            scale:       obj.scale,
            friction,
            restitution,
            is_trigger:  obj.is_trigger,
        })
    } else {
        None
    };

    if obj.is_trigger {
        trigger_set.insert(obj.entity_id);
    }

    col_to_entity.insert(col_handle, obj.entity_id);
    entries.insert(obj.entity_id, PhysicsEntry {
        rb_handle,
        col_handle,
        is_dynamic,
        col_offset: obj.collider_offset,
        rb_created_for_drag: false,
        col_shape_data,
        is_character: obj.is_character_controller,
        // 前回解決済み位置の初期値 = 登録時のアクターワールド位置。
        char_last_pos: obj.position,
    });
}

// ─── 結果収集 ────────────────────────────────────────────────────────────────

/// 1 ステップ分の物理結果を収集して `PhysicsResult` を返す。
///
/// `active_contacts` / `active_triggers` はフレーム間で状態を維持し、Stay/Exit の検出に使用する。
/// `narrow_phase` を直接クエリして、イベント未発火のケース（kinematic vs static）でも
/// アクティブ接触エンティティを正確に検出する。
fn collect_results(
    rb_set:          &RigidBodySet,
    narrow_phase:    &NarrowPhase,
    entries:         &HashMap<u64, PhysicsEntry>,
    col_evt_rx:      &Receiver<CollisionEvent>,
    col_to_entity:   &HashMap<ColliderHandle, u64>,
    trigger_set:     &HashSet<u64>,
    active_contacts: &mut HashSet<(u64, u64)>,
    active_triggers: &mut HashSet<(u64, u64)>,
) -> PhysicsResult {
    // ── Dynamic Rigidbody の Transform・速度取得 ─────────────────────────────
    // 収束停止判定用に、全 Dynamic ボディの並進・角速度の最大の大きさも集計する。
    let mut transform_updates = Vec::new();
    // タブ退避（続きから再開）用に、全 Dynamic ボディの現在速度も収集する。
    let mut body_velocities: Vec<(u64, [f32; 3], [f32; 3])> = Vec::new();
    let mut max_linear_speed:  f32 = 0.0;
    let mut max_angular_speed: f32 = 0.0;
    for (entity_id, entry) in entries.iter() {
        if !entry.is_dynamic { continue; }
        let Some(rb_h) = entry.rb_handle else { continue };
        let Some(rb)   = rb_set.get(rb_h) else { continue };

        let pos = rb.translation();
        let rot = rb.rotation().quaternion();
        // SEED 規約: クォータニオンは [x(i), y(j), z(k), w] の順
        transform_updates.push((*entity_id, [pos.x, pos.y, pos.z], [rot.i, rot.j, rot.k, rot.w]));

        // 速度の大きさ（静止判定に使用）
        let linvel = rb.linvel();
        let angvel = rb.angvel();
        max_linear_speed  = max_linear_speed.max(linvel.norm());
        max_angular_speed = max_angular_speed.max(angvel.norm());

        // 現在速度そのもの（タブ復帰時の初速復元に使用）
        body_velocities.push((
            *entity_id,
            [linvel.x, linvel.y, linvel.z],
            [angvel.x, angvel.y, angvel.z],
        ));
    }

    // ── 衝突イベント処理 ─────────────────────────────────────────────────────
    // Rapier は接触開始・終了時のみイベントを発火する（継続中はなし）
    let mut collision_events = Vec::new();
    let mut trigger_events   = Vec::new();

    while let Ok(evt) = col_evt_rx.try_recv() {
        // CollisionEvent のバリアントから ColliderHandle を取得する
        let (h1, h2, started) = match evt {
            CollisionEvent::Started(h1, h2, _flags) => (h1, h2, true),
            CollisionEvent::Stopped(h1, h2, _flags) => (h1, h2, false),
        };

        let Some(ea) = col_to_entity.get(&h1).copied() else { continue };
        let Some(eb) = col_to_entity.get(&h2).copied() else { continue };

        let is_ta = trigger_set.contains(&ea);
        let is_tb = trigger_set.contains(&eb);

        if started {
            if is_ta || is_tb {
                let (te, oe) = if is_ta { (ea, eb) } else { (eb, ea) };
                if active_triggers.insert((te, oe)) {
                    trigger_events.push(SeedTriggerEvent {
                        trigger_entity: te, other_entity: oe, phase: TriggerPhase::Enter,
                    });
                }
            } else {
                let key = if ea < eb { (ea, eb) } else { (eb, ea) };
                if active_contacts.insert(key) {
                    collision_events.push(SeedCollisionEvent {
                        entity_a: ea, entity_b: eb, phase: CollisionPhase::Enter,
                    });
                }
            }
        } else {
            // Stopped
            if is_ta || is_tb {
                let (te, oe) = if is_ta { (ea, eb) } else { (eb, ea) };
                if active_triggers.remove(&(te, oe)) {
                    trigger_events.push(SeedTriggerEvent {
                        trigger_entity: te, other_entity: oe, phase: TriggerPhase::Exit,
                    });
                }
            } else {
                let key = if ea < eb { (ea, eb) } else { (eb, ea) };
                if active_contacts.remove(&key) {
                    collision_events.push(SeedCollisionEvent {
                        entity_a: ea, entity_b: eb, phase: CollisionPhase::Exit,
                    });
                }
            }
        }
    }

    // Stay: 継続している衝突ペアに Stay イベントを毎ステップ送信する
    for &(ea, eb) in active_contacts.iter() {
        collision_events.push(SeedCollisionEvent { entity_a: ea, entity_b: eb, phase: CollisionPhase::Stay });
    }

    // ── NarrowPhase 直接クエリ ──────────────────────────────────────────────
    // CollisionEvent が発火しないケース（kinematic vs static 等）でも
    // 接触中エンティティを確実に収集する。
    // has_any_active_contact() = true のペアのみを対象にする。
    let mut active_contact_entity_ids: Vec<u64> = Vec::new();
    for pair in narrow_phase.contact_pairs() {
        if !pair.has_any_active_contact { continue; }
        let Some(ea) = col_to_entity.get(&pair.collider1).copied() else { continue };
        let Some(eb) = col_to_entity.get(&pair.collider2).copied() else { continue };
        // トリガーは除外する（トリガーは応答なしのため押し戻し不要）
        if trigger_set.contains(&ea) || trigger_set.contains(&eb) { continue; }
        if !active_contact_entity_ids.contains(&ea) {
            active_contact_entity_ids.push(ea);
        }
        if !active_contact_entity_ids.contains(&eb) {
            active_contact_entity_ids.push(eb);
        }
    }

    // ── アクティブトリガーエンティティ収集 ──────────────────────────────────
    // active_triggers は継続中のオーバーラップペアを保持する HashSet。
    // Enter/Exit イベントは遷移時のみ来るため、触れている間もカラーを維持するには
    // 毎フレームこの集合からエンティティ ID を収集してメインスレッドに送る必要がある。
    let mut active_trigger_entity_ids: Vec<u64> = Vec::new();
    for &(te, oe) in active_triggers.iter() {
        if !active_trigger_entity_ids.contains(&te) { active_trigger_entity_ids.push(te); }
        if !active_trigger_entity_ids.contains(&oe) { active_trigger_entity_ids.push(oe); }
    }

    // 【撤去】キャラクターの補正後位置・接地は `ResolveCharacter` の同期 reply でフレーム 1 回
    // 返すため、ここでステップ結果に相乗りさせて収集する処理は不要になった。

    PhysicsResult {
        transform_updates, collision_events, trigger_events,
        active_contact_entity_ids, active_trigger_entity_ids,
        max_linear_speed, max_angular_speed,
        body_velocities,
    }
}

// ─── 変換ユーティリティ ──────────────────────────────────────────────────────

/// SEED の [x, y, z, w] クォータニオンと位置を Rapier の Isometry に変換する。
fn to_isometry(position: [f32; 3], rotation: [f32; 4]) -> Isometry<Real> {
    let translation = Translation::new(position[0], position[1], position[2]);
    // nalgebra::Quaternion::new(w, i, j, k) — w が先頭
    // SEED 規約: rotation = [x(i), y(j), z(k), w]
    let nq = UnitQuaternion::new_normalize(nalgebra::Quaternion::new(
        rotation[3], // w
        rotation[0], // i
        rotation[1], // j
        rotation[2], // k
    ));
    Isometry::from_parts(translation, nq)
}

/// フリーズ軸設定を Rapier の `LockedAxes` ビットフラグに変換する。
fn build_locked_axes(freeze_pos: [bool; 3], freeze_rot: [bool; 3]) -> LockedAxes {
    let mut locked = LockedAxes::empty();
    if freeze_pos[0] { locked |= LockedAxes::TRANSLATION_LOCKED_X; }
    if freeze_pos[1] { locked |= LockedAxes::TRANSLATION_LOCKED_Y; }
    if freeze_pos[2] { locked |= LockedAxes::TRANSLATION_LOCKED_Z; }
    if freeze_rot[0] { locked |= LockedAxes::ROTATION_LOCKED_X; }
    if freeze_rot[1] { locked |= LockedAxes::ROTATION_LOCKED_Y; }
    if freeze_rot[2] { locked |= LockedAxes::ROTATION_LOCKED_Z; }
    locked
}

/// `ColliderShape` から Rapier の `ColliderBuilder` を構築する。
///
/// `scale` を各頂点・半辺長に乗算してワールドスケールを反映する。
fn build_collider_shape(shape: &ColliderShape, scale: &[f32; 3]) -> ColliderBuilder {
    match shape {
        ColliderShape::Box { half_extents: [hx, hy, hz] } => {
            ColliderBuilder::cuboid(hx * scale[0], hy * scale[1], hz * scale[2])
        }
        ColliderShape::Sphere { radius } => {
            // 球は均等スケールを前提として X 軸スケールを使用する
            ColliderBuilder::ball(*radius * scale[0])
        }
        ColliderShape::Capsule { radius, half_height } => {
            // Rapier: capsule_y(half_height, radius) の引数順
            ColliderBuilder::capsule_y(*half_height * scale[1], *radius * scale[0])
        }
        ColliderShape::Cylinder { radius, half_height } => {
            // Rapier: cylinder(half_height, radius) — Y 軸が長軸
            ColliderBuilder::cylinder(*half_height * scale[1], *radius * scale[0])
        }
        ColliderShape::Cone { radius, half_height } => {
            // Rapier: cone(half_height, radius) — Y 軸が長軸、頂点が +Y 側
            ColliderBuilder::cone(*half_height * scale[1], *radius * scale[0])
        }
        ColliderShape::ConvexHull { vertices } => {
            let pts: Vec<nalgebra::Point3<Real>> = vertices
                .iter()
                .map(|&[x, y, z]| nalgebra::Point3::new(x * scale[0], y * scale[1], z * scale[2]))
                .collect();
            // convex_hull は Option<ColliderBuilder> を返す
            ColliderBuilder::convex_hull(&pts).unwrap_or_else(|| {
                eprintln!("[Physics] Warning: ConvexHull 生成失敗。代替 Ball を使用");
                ColliderBuilder::ball(0.1)
            })
        }
        ColliderShape::TriangleMesh { triangles } => {
            let vertices: Vec<nalgebra::Point3<Real>> = triangles
                .iter()
                .flat_map(|tri| tri.iter())
                .map(|&[x, y, z]| nalgebra::Point3::new(x * scale[0], y * scale[1], z * scale[2]))
                .collect();
            let indices: Vec<[u32; 3]> = (0..triangles.len())
                .map(|i| { let b = (i * 3) as u32; [b, b + 1, b + 2] })
                .collect();
            // trimesh は rapier3d 0.22 で ColliderBuilder を直接返す（Result ではない）
            ColliderBuilder::trimesh(vertices, indices)
        }
        ColliderShape::TriangleMeshIndexed { vertices, indices } => {
            // 共有頂点をそのまま Point3 へ写し（ワールドスケール反映）、インデックスは複製する。
            // 展開版と違い頂点を三角形ごとに複製しないため、地形のような大規模メッシュを
            // 少ないメモリで登録できる。
            let pts: Vec<nalgebra::Point3<Real>> = vertices
                .iter()
                .map(|&[x, y, z]| nalgebra::Point3::new(x * scale[0], y * scale[1], z * scale[2]))
                .collect();
            ColliderBuilder::trimesh(pts, indices.clone())
        }
    }
}

// ─── テスト ──────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// `ColliderShape::TriangleMeshIndexed`（地形コライダーが使う共有頂点＋インデックス）で
    /// 作った静的トライメッシュの上に、落下する Dynamic 球が「乗って止まる」ことを
    /// 決定論的に検証する。
    ///
    /// 実スレッド（60Hz 実時間）ではなく `PhysicsPipeline` を固定 dt で直接回すため、
    /// 時間・スケジューリングに依らず結果は一定で、通常の `cargo test` で高速に走る。
    /// これは地形の物理コリジョン経路（`TriangleMeshIndexed` → `build_collider_shape`
    /// → Rapier trimesh → Dynamic 球の接触）の実効を CI で担保するための回帰テストである。
    #[test]
    fn dynamic_ball_rests_on_indexed_trimesh_floor() {
        // ── y=0 の平坦な床を 2 三角形（四角形）で共有頂点定義する ──
        //   4 頂点を 2 三角形が共有する（＝インデックス版が想定する形）。
        let floor = ColliderShape::TriangleMeshIndexed {
            vertices: vec![
                [-5.0, 0.0, -5.0],
                [ 5.0, 0.0, -5.0],
                [ 5.0, 0.0,  5.0],
                [-5.0, 0.0,  5.0],
            ],
            indices: vec![[0, 1, 2], [0, 2, 3]],
        };

        // ── Rapier ワールドを組む ──
        let mut rb_set   = RigidBodySet::new();
        let mut col_set  = ColliderSet::new();
        let mut pipeline = PhysicsPipeline::new();
        let mut islands  = IslandManager::new();
        let mut broad    = DefaultBroadPhase::new();
        let mut narrow   = NarrowPhase::new();
        let mut impulse  = ImpulseJointSet::new();
        let mut multibody= MultibodyJointSet::new();
        let mut ccd      = CCDSolver::new();
        let mut query    = QueryPipeline::new();
        let gravity = vector![DEFAULT_GRAVITY[0], DEFAULT_GRAVITY[1], DEFAULT_GRAVITY[2]];
        let mut params  = IntegrationParameters::default();
        params.dt = PHYSICS_FIXED_STEP as Real;

        // 床：RigidBody 無しの Static コライダー（build_collider_shape の新バリアント経由）。
        let floor_col = build_collider_shape(&floor, &[1.0, 1.0, 1.0]).build();
        col_set.insert(floor_col);

        // 球：半径 0.5、床の 3m 上から自由落下させる Dynamic ボディ。
        const BALL_RADIUS: Real = 0.5;
        const DROP_HEIGHT: Real = 3.0;
        let ball_rb = RigidBodyBuilder::dynamic()
            .translation(vector![0.0, DROP_HEIGHT, 0.0])
            .build();
        let ball_h = rb_set.insert(ball_rb);
        let ball_col = ColliderBuilder::ball(BALL_RADIUS).restitution(0.0).build();
        col_set.insert_with_parent(ball_col, ball_h, &mut rb_set);

        // ── 固定 dt で十分な回数ステップする（落下＋静定に足りる 3 秒相当）──
        for _ in 0..180 {
            pipeline.step(
                &gravity, &params,
                &mut islands, &mut broad, &mut narrow,
                &mut rb_set, &mut col_set,
                &mut impulse, &mut multibody, &mut ccd,
                Some(&mut query), &(), &(),
            );
        }

        let y = rb_set.get(ball_h).unwrap().translation().y;
        // 期待：球の中心は床（y=0）から半径ぶん上（≈0.5）で静止する。
        //   下限 0.3：床をすり抜けて落ち続けていない（コリジョンが効いている）。
        //   上限 0.7：床の上に正しく接地している（浮いていない・跳ね続けていない）。
        assert!(
            y > 0.3 && y < 0.7,
            "球はトライメッシュ床の上（y≈{BALL_RADIUS}）で静止するはずが y={y} だった\
             （y が負なら床をすり抜けている＝コリジョン不成立）"
        );
    }

    /// `make_character_controller` が定数どおりの KCC を構築することを検証する。
    ///
    /// マジックナンバーではなく KCC_* 定数から組み立てていること・単位（度→ラジアン）
    /// 変換が正しいことを、外部依存なしで確認する回帰テスト。
    #[test]
    fn character_controller_uses_configured_constants() {
        let kcc = make_character_controller();

        // スロープ角は度→ラジアン変換されている
        assert!((kcc.max_slope_climb_angle - (KCC_MAX_SLOPE_CLIMB_DEG as Real).to_radians()).abs() < 1e-6);
        assert!((kcc.min_slope_slide_angle - (KCC_MIN_SLOPE_SLIDE_DEG as Real).to_radians()).abs() < 1e-6);

        // スキン幅は絶対指定
        assert!(matches!(kcc.offset, CharacterLength::Absolute(v) if (v - KCC_SKIN_OFFSET_M).abs() < 1e-6));

        // 段差の自動乗り越えが有効で、絶対値の高さが設定されている
        let step = kcc.autostep.expect("autostep が有効であること");
        assert!(matches!(step.max_height, CharacterLength::Absolute(v) if (v - KCC_AUTOSTEP_MAX_HEIGHT_M).abs() < 1e-6));
        assert!(!step.include_dynamic_bodies, "動的ボディの上には段差昇降しない設定");

        // 接地スナップが有効
        assert!(matches!(kcc.snap_to_ground, Some(CharacterLength::Absolute(v)) if (v - KCC_SNAP_TO_GROUND_M).abs() < 1e-6));
    }

    /// `make_character_controller` の KCC で、カプセルを床へ向けて下方向に動かしたとき、
    /// 床をすり抜けず・接地判定が立つことを決定論的に検証する。
    ///
    /// スレッドを使わず `KinematicCharacterController::move_shape` を直接呼ぶため、
    /// 時間・スケジューリングに依らず結果は一定。キャラクターコントローラーが依存する
    /// KCC 統合経路（カプセルシェイプ → move_shape → 補正移動量＋grounded）の実効を
    /// CI で担保する。
    #[test]
    fn character_move_is_blocked_by_floor_and_reports_grounded() {
        // ── y=0 の平坦な床（共有頂点＋インデックスのトライメッシュ）──
        let floor = ColliderShape::TriangleMeshIndexed {
            vertices: vec![
                [-5.0, 0.0, -5.0],
                [ 5.0, 0.0, -5.0],
                [ 5.0, 0.0,  5.0],
                [-5.0, 0.0,  5.0],
            ],
            indices: vec![[0, 1, 2], [0, 2, 3]],
        };

        let mut rb_set  = RigidBodySet::new();
        let mut col_set = ColliderSet::new();
        let mut query   = QueryPipeline::new();

        // 床：Static コライダー
        let floor_col = build_collider_shape(&floor, &[1.0, 1.0, 1.0]).build();
        col_set.insert(floor_col);

        // キャラクター：半径 0.4・半高 0.5 のカプセル（Y 軸）。
        // カプセルの下端 = 中心 - (半高 + 半径) = 中心 - 0.9。中心を 1.0m に置くと
        // 下端は床（y=0）の 0.1m 上から始まる（＝床にめり込んでいない）。
        const CAP_RADIUS:      Real = 0.4;
        const CAP_HALF_HEIGHT: Real = 0.5;
        const START_Y:         Real = 1.0;
        let capsule = SharedShape::capsule_y(CAP_HALF_HEIGHT, CAP_RADIUS);
        let char_pos = Isometry::translation(0.0, START_Y, 0.0);

        // クエリパイプラインを床コライダーで更新する（move_shape が参照する）
        query.update(&col_set);

        let kcc = make_character_controller();
        // 下向きに 1m 動かそうとする（重力分をスクリプトが含める想定の代役）
        let movement = kcc.move_shape(
            PHYSICS_FIXED_STEP as Real,
            &rb_set, &col_set, &query,
            &*capsule, &char_pos, vector![0.0, -1.0, 0.0],
            QueryFilter::default().exclude_sensors(),
            |_| {},
        );

        // 実効移動後の中心 Y
        let result_y = START_Y + movement.translation.y;
        // カプセル下端が床（y=0）を大きく割り込んでいない（すり抜けていない）。
        //   下端 = result_y - (CAP_HALF_HEIGHT + CAP_RADIUS)。床上ならほぼ 0 以上。
        let foot_y = result_y - (CAP_HALF_HEIGHT + CAP_RADIUS);
        assert!(
            foot_y > -0.05,
            "カプセルが床をすり抜けた: foot_y={foot_y}（下向き移動が床で止まっていない）"
        );
        // 床に十分近い下向き移動なので接地判定が立つはず
        assert!(
            movement.grounded,
            "床の直上へ下向き移動したのに grounded=false（接地判定が機能していない）"
        );
    }

    /// `perform_character_step`（StepCharacter の実体）の差分解決を決定論的に検証する。
    ///
    /// キャラクター（カプセル）の「前回解決済み位置」を床の直上に置き、床を大きく貫く
    /// 希望位置を与える。moveVector = 希望位置 - 前回位置 が KCC で床に衝突解決され、
    /// entry.char_last_pos が床上へ押し戻される（希望位置まで沈まない）こと、接地判定が
    /// 立つこと、前回位置が更新されることを確認する。スレッド非依存で `cargo test` に乗る。
    #[test]
    fn character_step_pushes_back_from_floor_and_updates_last_pos() {
        // y=0 の平坦な床（共有頂点＋インデックスのトライメッシュ）
        let floor = ColliderShape::TriangleMeshIndexed {
            vertices: vec![
                [-5.0, 0.0, -5.0],
                [ 5.0, 0.0, -5.0],
                [ 5.0, 0.0,  5.0],
                [-5.0, 0.0,  5.0],
            ],
            indices: vec![[0, 1, 2], [0, 2, 3]],
        };

        let mut rb_set  = RigidBodySet::new();
        let mut col_set = ColliderSet::new();
        let mut query   = QueryPipeline::new();

        // 床: Static コライダー
        let floor_col = build_collider_shape(&floor, &[1.0, 1.0, 1.0]).build();
        col_set.insert(floor_col);

        // キャラクター: 半径 0.4・半高 0.5 のカプセル（Y 軸）を Static コライダーとして登録。
        // 中心を前回位置 y=1.0 に置くと下端は床の 0.1m 上（＝床にめり込んでいない）。
        const CAP_RADIUS:      Real = 0.4;
        const CAP_HALF_HEIGHT: Real = 0.5;
        const START_Y:         Real = 1.0;
        let char_col = ColliderBuilder::capsule_y(CAP_HALF_HEIGHT, CAP_RADIUS)
            .position(Isometry::translation(0.0, START_Y, 0.0))
            .build();
        let char_col_h = col_set.insert(char_col);

        // クエリパイプラインを両コライダーで更新する（perform_character_move が参照する）
        query.update(&col_set);

        // entries に character エントリを 1 件登録する（entity_id=1）
        const ENTITY_ID: u64 = 1;
        let mut entries: HashMap<u64, PhysicsEntry> = HashMap::new();
        entries.insert(ENTITY_ID, PhysicsEntry {
            rb_handle:  None,
            col_handle: char_col_h,
            is_dynamic: false,
            col_offset: [0.0, 0.0, 0.0],
            rb_created_for_drag: false,
            col_shape_data: None,
            is_character:  true,
            char_last_pos: [0.0, START_Y as f32, 0.0],
        });

        let kcc = make_character_controller();

        // 希望位置: 床を大きく貫く y=-1.0（スクリプトが Transform に書いた値の代役）
        let desired = [0.0, -1.0, 0.0];
        let resolved = perform_character_step(
            &kcc, &query, &mut rb_set, &mut col_set, &mut entries,
            ENTITY_ID, desired, [0.0, 0.0, 0.0, 1.0], PHYSICS_FIXED_STEP as Real,
        );

        // 同期 reply 値（補正後位置＋接地）が返ること
        let (corrected, grounded) = resolved.expect("キャラクター解決が Some を返すこと");
        // entry の前回位置も同じ補正後位置へ更新されている
        let entry = entries.get(&ENTITY_ID).unwrap();
        assert_eq!(
            entry.char_last_pos, corrected,
            "entry.char_last_pos と reply の corrected が一致すること"
        );
        let corrected_y = corrected[1];

        // 前回位置が更新されている（初期値 START_Y のままではない）
        assert!(
            (corrected_y - START_Y as f32).abs() > 1e-4 || corrected_y > desired[1] + 0.5,
            "char_last_pos が更新されていない: {corrected_y}"
        );
        // 希望位置(y=-1.0)まで沈まず、床上へ押し戻されている
        let foot_y = corrected_y - (CAP_HALF_HEIGHT as f32 + CAP_RADIUS as f32);
        assert!(
            foot_y > -0.05,
            "床に押し戻されていない: foot_y={foot_y}（char_last_pos.y={corrected_y}）"
        );
        // 床へ向かって沈もうとしたので接地判定が立つ
        assert!(
            grounded,
            "床へ押し付けたのに grounded=false（接地状態が返されていない）"
        );
    }

    /// `perform_character_step` が「対象未登録」「キャラクター指定でない」で `None` を返し、
    /// entry を書き換えないことを検証する。
    ///
    /// ResolveCharacter の同期 reply はこの `None` を「解決失敗」としてメインへ返し、
    /// メインは今フレームの補正を諦めて ECS をスクリプト値のまま据え置く。その据え置き判定の
    /// 拠り所となる純粋部分（None を返す条件）を外部依存なしで固定する回帰テスト。
    #[test]
    fn character_step_returns_none_for_missing_or_non_character() {
        let mut rb_set  = RigidBodySet::new();
        let mut col_set = ColliderSet::new();
        let query   = QueryPipeline::new();
        let kcc     = make_character_controller();

        // ── ① 未登録 entity_id: None を返す ──
        let mut empty: HashMap<u64, PhysicsEntry> = HashMap::new();
        let r = perform_character_step(
            &kcc, &query, &mut rb_set, &mut col_set, &mut empty,
            999, [0.0, 0.0, 0.0], [0.0, 0.0, 0.0, 1.0], PHYSICS_FIXED_STEP as Real,
        );
        assert!(r.is_none(), "未登録 entity は None を返すべき");

        // ── ② is_character=false の entry: None を返し、char_last_pos を書き換えない ──
        const ID: u64 = 7;
        const START: [f32; 3] = [1.0, 2.0, 3.0];
        let col_h = col_set.insert(ColliderBuilder::ball(0.5).build());
        let mut entries: HashMap<u64, PhysicsEntry> = HashMap::new();
        entries.insert(ID, PhysicsEntry {
            rb_handle:  None,
            col_handle: col_h,
            is_dynamic: false,
            col_offset: [0.0, 0.0, 0.0],
            rb_created_for_drag: false,
            col_shape_data: None,
            is_character:  false,      // ← キャラクターではない
            char_last_pos: START,
        });
        let r = perform_character_step(
            &kcc, &query, &mut rb_set, &mut col_set, &mut entries,
            ID, [10.0, 10.0, 10.0], [0.0, 0.0, 0.0, 1.0], PHYSICS_FIXED_STEP as Real,
        );
        assert!(r.is_none(), "非キャラクター entity は None を返すべき");
        assert_eq!(
            entries.get(&ID).unwrap().char_last_pos, START,
            "非キャラクターでは char_last_pos を書き換えないこと"
        );
    }
}
