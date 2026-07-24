// ============================================================
//  physics_timeline.rs — 編集時物理シミュレーション タイムライン
//
//  【責務】
//    - 物理シミュレーション有効時のフレームスナップショット管理
//    - 再生・停止・フレーム移動（◁▷）の実装
//    - 「変化なしフレームはスキップ」「最新フレームで停止したらシミュレーションしない」
//
//  【スナップショット内容】
//    ActorTransform（3D）と CanvasTransform（2D）のみを保存する。
//    物理エンジン内部状態（速度・角速度）は保存しない。
//    戻って再生する場合は現在のTransform状態で物理エンジンを再起動する。
// ============================================================

use super::{App, RuntimeMode};
use crate::engine::components::{
    CanvasTransform, ComponentKind, ModelComponent, Transform as ActorTransform,
};
use crate::engine::ecs::Entity;
use crate::engine::physics::{PhysicsCommand, PhysicsCommand2d};
use std::collections::HashSet;
use std::sync::atomic::{AtomicU32, Ordering};

// スナップショット最大保持数（変化なしフレームはスキップするため実際はもっと少ない）
const MAX_SNAPSHOTS: usize = 7200;

// 変化なしが何フレーム連続したら収束とみなすか。
const NO_CHANGE_STOP_THRESHOLD: u32 = 5;

// 物理エンジン再起動後に「変化なし停止判定」をスキップするフレーム数。
// 過去フレームから再生する際に stop_physics → start_physics を行うと、
// 物理スレッドが最初の ECS 更新を返すまでに数フレームのラグが生じる。
// その間に NO_CHANGE_STOP_THRESHOLD に達して即停止するのを防ぐ。
const PHYSICS_WARMUP_FRAMES: u32 = 60;

// 変化検出の閾値（これ以下の差分はスキップ）
const CHANGE_EPSILON: f32 = 1e-5;

// 「静止した」とみなす並進速度の閾値（m/s）。
// これ未満なら収束停止の候補とする。空中で緩慢に落下・回転している物体
// （速度はあるが 1 フレームの位置差分が小さい）を誤って止めないための下限。
const REST_LINEAR_SPEED_EPSILON: f32 = 0.03;

// 「静止した」とみなす角速度の閾値（rad/s）。約 1.7°/s。
const REST_ANGULAR_SPEED_EPSILON: f32 = 0.03;

// ─── 直近フレームの最大速度（収束停止判定用）─────────────────────────────────
// 物理スレッドが PhysicsResult(2d) で送る「全 Dynamic ボディの最大速度」を、
// update_physics / update_physics_2d が毎フレームここへ退避する。
// try_record_physics_snapshot（stop 判定）が edit_physics_bodies_at_rest 経由で参照する。
// App にフィールドを増やさずに 3D/2D 更新→stop 判定間で受け渡すためのモジュール静的。
// f32 をビット表現（to_bits/from_bits）で保持する。App は単一インスタンスのため競合しない。
static LAST_MAX_LIN_SPEED_3D: AtomicU32 = AtomicU32::new(0);
static LAST_MAX_ANG_SPEED_3D: AtomicU32 = AtomicU32::new(0);
static LAST_MAX_LIN_SPEED_2D: AtomicU32 = AtomicU32::new(0);
static LAST_MAX_ANG_SPEED_2D: AtomicU32 = AtomicU32::new(0);

// ─── PhysicsSnapshot ─────────────────────────────────────────────────────────

/// 1フレーム分の物理シミュレーション状態スナップショット。
///
/// ActorTransform（3D）と CanvasTransform（2D）を保持する。
/// 物理エンジン内部の速度・角速度は保持しないため、途中から再生すると
/// 初速ゼロで物理が再開される。
#[derive(Clone)]
pub struct PhysicsSnapshot {
    /// 3D アクターの Transform（Entity → Transform）
    pub transforms_3d: Vec<(Entity, ActorTransform)>,
    /// 2D アクターの CanvasTransform（Entity → CanvasTransform）
    pub transforms_2d: Vec<(Entity, CanvasTransform)>,
    /// このフレーム時点で 3D 接触中だったエンティティ DFS ID 集合（コライダー色復元用）。
    /// シーク・ステップ・プレイバック時にこれを active_collision_dfs_ids へ書き戻すことで、
    /// タイムライン上のどのフレームでも「そのフレーム時点の接触色」を正しく再現する。
    pub colliding_3d: HashSet<u64>,
    /// このフレーム時点で 2D 接触中だったエンティティ DFS ID 集合（コライダー色復元用）。
    pub colliding_2d: HashSet<u64>,
    /// このフレームのシミュレーション累積時間（秒）
    pub time_secs: f64,
}

// ─── App impl ─────────────────────────────────────────────────────────────────

impl App {
    // ─── 物理スレッド Pause/Resume 共通ヘルパー ────────────────────────────────

    /// 3D / 2D 両方の物理スレッドへ Pause を送信する。
    ///
    /// タイムラインを停止させる各所（初期化・適用・シーク・収束停止）で共用する。
    /// スレッドが未起動なら何もしない。
    pub(super) fn send_pause_to_physics_threads(&self) {
        if let Some(thread) = &self.physics_thread {
            thread.send(PhysicsCommand::Pause);
        }
        if let Some(thread) = &self.physics_thread_2d {
            thread.send(PhysicsCommand2d::Pause);
        }
    }

    /// 3D / 2D 両方の物理スレッドへ Resume を送信する。
    pub(super) fn send_resume_to_physics_threads(&self) {
        if let Some(thread) = &self.physics_thread {
            thread.send(PhysicsCommand::Resume);
        }
        if let Some(thread) = &self.physics_thread_2d {
            thread.send(PhysicsCommand2d::Resume);
        }
    }

    // ─── 常時押し戻しモード判定 ────────────────────────────────────────────────

    /// RigidBody 無効の「常時押し戻しモード」かどうかを返す。
    ///
    /// 有効になっている編集時物理（3D/2D）がすべて RigidBody 無効（コライダーのみ）の場合に true。
    /// このモードではタイムライン（スナップショット記録・再生・Pause）を使わず、
    /// 物理を毎フレーム走らせてドラッグ／インスペクタ編集による押し戻しを常時有効にする。
    /// 逆に、一つでも RigidBody 有効（ダイナミクスあり）の物理があればタイムラインを優先する。
    pub(super) fn is_edit_physics_pushback_mode(&self) -> bool {
        if self.mode != RuntimeMode::Edit {
            return false;
        }
        let any_enabled = self.edit_physics_enabled || self.edit_physics_2d_enabled;
        if !any_enabled {
            return false;
        }
        // RigidBody（タイムライン）モードの物理が一つでもあればタイムライン優先＝押し戻しモードではない
        let timeline_3d = self.edit_physics_enabled && self.edit_physics_with_rigidbody;
        let timeline_2d = self.edit_physics_2d_enabled && self.edit_physics_2d_with_rigidbody;
        !timeline_3d && !timeline_2d
    }

    /// 常時押し戻しモードへ移行する。
    ///
    /// タイムライン状態をリセットし、接触色をクリアするだけで、物理スレッドには
    /// Pause を送らない（起動直後の paused=false のまま走り続ける）。
    /// これによりドラッグ／インスペクタ編集の押し戻しが即時・常時効くようになる。
    pub(super) fn enter_edit_physics_pushback(&mut self) {
        self.reset_physics_timeline();
        // 前回のライブシミュレーション時の接触色が残らないようクリアする
        self.active_collision_dfs_ids.clear();
        self.active_collision_2d_dfs_ids.clear();
        // エディタへタイムライン非稼働状態を通知する（スライダーはエディタ側で非表示）
        self.send_physics_timeline_state();
    }

    /// ギズモ／インスペクタによる Transform ドラッグが進行中かどうかを返す。
    ///
    /// RigidBody タイムラインモードで最新フレーム停止中のドラッグ検知ステップの
    /// トリガー判定（should_step_edit_physics）に使用する。
    pub(super) fn edit_physics_drag_active(&self) -> bool {
        self.drag.gizmo_drag.is_some() || self.inspector_transform_drag.is_some()
    }

    // ─── 収束停止のための速度スナップショット ───────────────────────────────

    /// 3D 物理スレッドが送った「全 Dynamic ボディの最大速度」を退避する。
    /// update_physics が結果受信時に毎フレーム呼ぶ。
    pub(super) fn store_edit_physics_rest_speeds_3d(&self, max_lin: f32, max_ang: f32) {
        LAST_MAX_LIN_SPEED_3D.store(max_lin.to_bits(), Ordering::Relaxed);
        LAST_MAX_ANG_SPEED_3D.store(max_ang.to_bits(), Ordering::Relaxed);
    }

    /// 2D 物理スレッドが送った「全 Dynamic ボディの最大速度」を退避する。
    /// update_physics_2d が結果受信時に毎フレーム呼ぶ。
    pub(super) fn store_edit_physics_rest_speeds_2d(&self, max_lin: f32, max_ang: f32) {
        LAST_MAX_LIN_SPEED_2D.store(max_lin.to_bits(), Ordering::Relaxed);
        LAST_MAX_ANG_SPEED_2D.store(max_ang.to_bits(), Ordering::Relaxed);
    }

    /// 全 Dynamic ボディ（有効な 3D・2D 物理）が「実際に静止した」かを速度で判定する。
    ///
    /// 直近フレームに物理スレッドが報告した最大並進・角速度が、
    /// いずれも静止閾値未満のとき true。収束停止（自動 Pause）を
    /// 「位置・回転が変化しない」だけでなく「速度がほぼ 0」でもゲートすることで、
    /// 空中で緩慢に落下・回転している最中に停止してしまうのを防ぐ。
    ///
    /// 無効な側（3D または 2D）は update_physics(_2d) が走らず速度が更新されないため、
    /// 判定から除外する（有効な側だけを見る）。これを怠ると、無効側の残留値
    /// （mark_edit_physics_moving の ∞）で永久に静止と判定されず収束停止しなくなる。
    fn edit_physics_bodies_at_rest(&self) -> bool {
        let rest_3d = if self.edit_physics_enabled {
            let lin = f32::from_bits(LAST_MAX_LIN_SPEED_3D.load(Ordering::Relaxed));
            let ang = f32::from_bits(LAST_MAX_ANG_SPEED_3D.load(Ordering::Relaxed));
            lin < REST_LINEAR_SPEED_EPSILON && ang < REST_ANGULAR_SPEED_EPSILON
        } else {
            true
        };
        let rest_2d = if self.edit_physics_2d_enabled {
            let lin = f32::from_bits(LAST_MAX_LIN_SPEED_2D.load(Ordering::Relaxed));
            let ang = f32::from_bits(LAST_MAX_ANG_SPEED_2D.load(Ordering::Relaxed));
            lin < REST_LINEAR_SPEED_EPSILON && ang < REST_ANGULAR_SPEED_EPSILON
        } else {
            true
        };
        rest_3d && rest_2d
    }

    /// 収束停止用の速度スナップショットを「移動中」（∞）へ初期化する。
    /// ライブシミュレーション開始・再開時に呼ぶ。物理スレッドが最初の実測速度を
    /// 報告するまでは edit_physics_bodies_at_rest() が false を返すため、
    /// 実データ受信前に（前回シミュレーションの残留 0 速度などで）誤って
    /// 収束停止してしまうのを防ぐ。
    pub(super) fn mark_edit_physics_moving(&self) {
        let inf = f32::INFINITY.to_bits();
        LAST_MAX_LIN_SPEED_3D.store(inf, Ordering::Relaxed);
        LAST_MAX_ANG_SPEED_3D.store(inf, Ordering::Relaxed);
        LAST_MAX_LIN_SPEED_2D.store(inf, Ordering::Relaxed);
        LAST_MAX_ANG_SPEED_2D.store(inf, Ordering::Relaxed);
    }

    /// RigidBody タイムラインモードで最新フレーム停止中にドラッグが開始されたとき、
    /// 物理シミュレーションを「続き」として再開する（ドラッグ中ライブシミュレーション）。
    ///
    /// 物理スレッドは再起動せず Resume のみ行うため、他の Dynamic ボディの速度は
    /// 保持されたまま継続する。ドラッグ中のオブジェクト自身は SetBodyKinematic(true) で
    /// kinematic 化済みのためギズモの Transform に毎フレーム追従し（UpdateKinematic）、
    /// 力学的影響（回転・跳ね返り）を受けずに他の Dynamic ボディを押しのける。
    ///
    /// paused=false になることで try_record_physics_snapshot が記録を継続し、
    /// フレームが連番で加算されて EDIT_PHYSICS_STATE 通知によりエディタの
    /// スライダー範囲も伸び続ける。
    ///
    /// 注意: at_latest は true のまま維持する。frame_renderer は !at_latest で
    /// ギズモを非表示にするため、ここで false にするとドラッグ中のギズモが消えてしまう
    /// （記録のたびに try_record 側でも true が設定される）。
    pub(super) fn begin_edit_physics_drag_live_sim(&mut self) {
        // 既に再生中なら何もしない（冪等）
        if !self.edit_physics_paused {
            return;
        }
        self.edit_physics_paused = false;
        self.edit_physics_in_playback = false;
        self.edit_physics_no_change_count = 0;
        // 実測速度を受信するまで「移動中」とみなす（実データ前の誤停止防止）
        self.mark_edit_physics_moving();
        // 再起動ではなく Resume なので受信ラグはほぼ無い（ウォームアップ不要）
        self.send_resume_to_physics_threads();
        self.send_physics_timeline_state();
    }

    /// 編集時物理タイムラインを初期化する。
    ///
    /// `edit_physics_enabled` が true になった時点で呼ぶ。
    /// 現在の ECS 状態を初期スナップショット（フレーム 0）として記録する。
    /// 物理スレッドには Pause を送信して、再生ボタンが押されるまでシミュレーションを止める。
    pub(super) fn init_physics_timeline(&mut self) {
        self.edit_physics_snapshots.clear();
        self.edit_physics_current_frame = 0;
        self.edit_physics_paused = true;
        self.edit_physics_at_latest = true;
        self.edit_physics_sim_time = 0.0;
        self.edit_physics_no_change_count = 0;
        self.edit_physics_warmup_frames = 0;
        self.edit_physics_in_playback = false;

        // 初期状態をフレーム 0 として記録する
        if let Some(snap) = self.capture_current_snapshot(0.0) {
            self.edit_physics_snapshots.push(snap);
        }

        // 物理スレッドを Pause して、再生ボタンを押すまでシミュレーションが進まないようにする
        // （初期化直後から物理が走ると倒れた状態でスタートしてしまう）
        self.send_pause_to_physics_threads();

        self.send_physics_timeline_state();
    }

    /// 編集時物理タイムラインをリセットする。
    ///
    /// `edit_physics_enabled` が false になったとき呼ぶ。
    pub(super) fn reset_physics_timeline(&mut self) {
        self.edit_physics_snapshots.clear();
        self.edit_physics_current_frame = 0;
        self.edit_physics_paused = true;
        self.edit_physics_at_latest = true;
        self.edit_physics_sim_time = 0.0;
        self.edit_physics_warmup_frames = 0;
        self.edit_physics_in_playback = false;
    }

    /// 現在の ECS 状態からスナップショットを生成して返す。
    fn capture_current_snapshot(&self, time_secs: f64) -> Option<PhysicsSnapshot> {
        let scene = self.scene.as_ref()?;
        let wl = self.active_world_line;

        let mut transforms_3d = Vec::new();
        let mut transforms_2d = Vec::new();

        // DFS でアクターツリーを走査してTransformを収集する
        let mut stack = scene
            .actors
            .iter()
            .filter(|a| a.world_line == wl)
            .collect::<Vec<_>>();

        while let Some(actor) = stack.pop() {
            for child in &actor.children {
                stack.push(child);
            }

            if actor.is_2d() {
                if let Some(ct) = scene.world.get::<CanvasTransform>(actor.entity) {
                    transforms_2d.push((actor.entity, ct.clone()));
                }
            } else {
                if let Some(tf) = scene.world.get::<ActorTransform>(actor.entity) {
                    transforms_3d.push((actor.entity, tf.clone()));
                }
            }
        }

        Some(PhysicsSnapshot {
            transforms_3d,
            transforms_2d,
            // 現在の接触状態（コライダー色）もフレームに紐付けて保存する。
            colliding_3d: self.active_collision_dfs_ids.clone(),
            colliding_2d: self.active_collision_2d_dfs_ids.clone(),
            time_secs,
        })
    }

    /// 物理シミュレーションの1ステップ後にスナップショットを試みる。
    ///
    /// 前スナップショットと比較して変化がなければ記録しない。
    /// 再生中（`edit_physics_paused = false`）かつ最新フレームにいるときのみ呼ぶ。
    pub(super) fn try_record_physics_snapshot(&mut self, dt: f64) {
        // 停止中はタイムラインへ記録しない。
        // ・常時押し戻しモード（RigidBody 無効）: タイムライン自体を使わない。
        // ・RigidBody タイムラインモードで最新フレーム停止中のドラッグ検知ステップ:
        //   ドラッグ終了自動再開のためだけに物理をステップさせており、記録はしない。
        // 再生中（paused=false）のみ記録する。
        if self.edit_physics_paused {
            return;
        }

        self.edit_physics_sim_time += dt;
        let time = self.edit_physics_sim_time;

        // ウォームアップ期間中は変化なし停止判定をスキップして記録のみ行う。
        // 過去フレームからの再生で物理エンジンを再起動した直後、
        // スレッドが ECS を更新し始めるまでのラグで即停止するのを防ぐ。
        if self.edit_physics_warmup_frames > 0 {
            self.edit_physics_warmup_frames -= 1;
            // ウォームアップ中でもスナップショット記録は行う（変化ありの場合のみ）
            if let Some(new_snap) = self.capture_current_snapshot(time) {
                if let Some(prev) = self.edit_physics_snapshots.last() {
                    if snapshot_has_change(prev, &new_snap) {
                        self.edit_physics_no_change_count = 0;
                        self.edit_physics_snapshots.push(new_snap);
                        self.edit_physics_current_frame = self.edit_physics_snapshots.len() - 1;
                        self.edit_physics_at_latest = true;
                        self.send_physics_timeline_state();
                    }
                }
            }
            return;
        }

        let Some(new_snap) = self.capture_current_snapshot(time) else {
            return;
        };

        // 前スナップショットとの差分チェック
        let has_change = if let Some(prev) = self.edit_physics_snapshots.last() {
            snapshot_has_change(prev, &new_snap)
        } else {
            true // スナップショットがなければ必ず記録
        };

        if !has_change {
            // 【ドラッグ中ライブシミュレーション】ドラッグ操作中は収束停止（自動 Pause）を
            // 抑止する。ドラッグ中に手を止めると Transform 変化がなくなり閾値に達して
            // 物理が Pause され、その後のドラッグで押しのけ・押し戻しが効かなくなるため。
            // ドラッグ終了後に静止していれば通常どおり収束停止する。
            if self.edit_physics_drag_active()
                || self.dragging_physics_entity_id.is_some()
                || self.dragging_physics_2d_entity_id.is_some()
            {
                self.edit_physics_no_change_count = 0;
                return;
            }
            // 【速度ゲート】位置・回転の 1 フレーム差分が微小でも、Dynamic ボディが
            // まだ速度を持っている（空中で緩慢に落下・回転している等）間は収束停止しない。
            // 物理スレッドが報告した全 Dynamic ボディの最大速度が静止閾値未満になって
            // はじめて「実際に静止した」とみなし、収束カウントを進める。
            if !self.edit_physics_bodies_at_rest() {
                self.edit_physics_no_change_count = 0;
                return;
            }
            // 変化なし & 全ボディ静止: 連続カウントを増やす。閾値を超えたら収束とみなして停止する。
            self.edit_physics_no_change_count += 1;
            if self.edit_physics_no_change_count < NO_CHANGE_STOP_THRESHOLD {
                return; // まだ猶予中: 停止しない
            }
            self.edit_physics_paused = true;
            self.edit_physics_at_latest = true;
            self.edit_physics_no_change_count = 0;
            // 速度を保持したまま物理スレッドを停止する
            self.send_pause_to_physics_threads();
            self.send_physics_timeline_state();
            return;
        }
        // 変化があればカウンターをリセットする
        self.edit_physics_no_change_count = 0;

        // 最大フレーム数を超えたら古いものを削除する
        if self.edit_physics_snapshots.len() >= MAX_SNAPSHOTS {
            self.edit_physics_snapshots.remove(0);
            if self.edit_physics_current_frame > 0 {
                self.edit_physics_current_frame -= 1;
            }
        }

        self.edit_physics_snapshots.push(new_snap);
        self.edit_physics_current_frame = self.edit_physics_snapshots.len() - 1;
        self.edit_physics_at_latest = true;

        self.send_physics_timeline_state();
    }

    /// 再生/停止をトグルする。
    ///
    /// - 停止中 → 再生: `start_edit_physics_play` に委譲
    /// - 再生中 → 停止
    pub(super) fn handle_edit_physics_play_pause(&mut self) {
        if self.edit_physics_paused {
            self.start_edit_physics_play();
        } else {
            let was_in_playback = self.edit_physics_in_playback;
            self.edit_physics_paused = true;
            self.edit_physics_in_playback = false;
            self.edit_physics_at_latest =
                self.edit_physics_current_frame + 1 >= self.edit_physics_snapshots.len();
            // プレイバック中は物理エンジンが動いていないため Pause を送らない
            if !was_in_playback {
                self.send_pause_to_physics_threads();
            }
            self.send_physics_timeline_state();
        }
    }

    /// 再生を開始する。
    ///
    /// - 最新フレームから再開・編集なし: フレーム 0 に戻って物理を再スタート（ループ再生）
    /// - 最新フレームから再開・編集あり: 現在の ECS 状態から物理を再起動（速度 0 で再開）
    /// - 過去フレームからの再開: 記録済みスナップショットをコマ送り再生
    fn start_edit_physics_play(&mut self) {
        let cur = self.edit_physics_current_frame;
        let max_frame = self.edit_physics_snapshots.len().saturating_sub(1);
        // 最新フレームにいるかどうか（シーク操作なしで停止していた場合 or 最終フレームを指す場合）
        let is_at_latest = cur >= max_frame;

        self.edit_physics_paused = false;
        self.edit_physics_at_latest = false;
        self.edit_physics_no_change_count = 0;
        // 実測速度を受信するまで「移動中」とみなす（実データ前の誤停止防止）
        self.mark_edit_physics_moving();

        if is_at_latest {
            let ecs_matches_snap = self.current_ecs_matches_latest_snapshot();
            if ecs_matches_snap {
                if self.edit_physics_snapshots.len() > 1 {
                    // ── 変化なし・既存履歴あり: フレーム 0 からプレイバック ──
                    // 再生が最後まで到達した後に再生ボタンを押した場合、
                    // 記録済みスナップショットをそのままフレーム 0 から再生する。
                    // 物理エンジンは起動せず、履歴も破棄しない。
                    self.edit_physics_current_frame = 0;
                    self.restore_snapshot(0);
                    self.edit_physics_in_playback = true;
                } else {
                    // ── 初回再生（スナップショットがフレーム 0 のみ）: 物理スレッドを起動 ──
                    // init_physics_timeline で Pause 済みの物理スレッドを Resume して
                    // シミュレーションを開始する。
                    self.send_resume_to_physics_threads();
                }
            } else {
                // ── 移動あり: 現在の ECS 状態から再起動（速度 0 で再開）────
                // 停止中に Gizmo でアクターを移動させた場合。
                // 移動後の位置から物理エンジンをリスタートし、新たな履歴を記録する。
                self.resume_edit_physics_from_current_ecs();
            }
        } else {
            // ── 過去フレームからの再開: スナップショットプレイバック ─────
            // 記録済みのスナップショットをコマ送りで再生するだけで、
            // 物理エンジンの再起動・truncate・変化なし停止判定はすべて不要。
            self.edit_physics_in_playback = true;
        }

        self.send_physics_timeline_state();
    }

    /// 現在の ECS 状態から物理を再起動して「続き」のシミュレーションを開始する。
    ///
    /// 最新フレームで停止していた状態から、Gizmo／インスペクタでアクターを移動させた後に
    /// 呼び出す。移動後の位置を最新スナップショットへ上書きし、物理エンジンを現在の ECS 位置で
    /// 再起動する。スナップショット履歴は破棄せず追記されるため、フレーム番号は連番で継続する。
    ///
    /// 呼び出し元:
    ///   - start_edit_physics_play（最新フレームで再生ボタン＋移動あり）
    ///   - update_physics（RigidBody タイムラインモードでのドラッグ終了自動再開）
    pub(super) fn resume_edit_physics_from_current_ecs(&mut self) {
        // 移動後の現在位置を最新スナップショットへ上書きする（続きの起点にする）
        if let Some(snap) = self.capture_current_snapshot(self.edit_physics_sim_time) {
            if let Some(last) = self.edit_physics_snapshots.last_mut() {
                *last = snap;
            }
        }
        self.edit_physics_paused = false;
        self.edit_physics_in_playback = false;
        self.edit_physics_at_latest = false;
        self.edit_physics_warmup_frames = PHYSICS_WARMUP_FRAMES;
        self.edit_physics_no_change_count = 0;
        // 実測速度を受信するまで「移動中」とみなす（実データ前の誤停止防止）
        self.mark_edit_physics_moving();
        // 物理エンジンを現在の ECS 位置で再起動する（速度 0 で再開）
        self.stop_physics();
        self.stop_physics_2d();
        if self.edit_physics_enabled {
            self.start_physics();
        }
        if self.edit_physics_2d_enabled {
            self.start_physics_2d();
        }
        self.send_physics_timeline_state();
    }

    /// スナップショットプレイバックを1フレーム進める。
    ///
    /// `frame_renderer` から毎フレーム呼ばれる（`edit_physics_in_playback = true` のとき）。
    /// 次スナップショットを ECS に書き戻し、最終フレームに達したら自動停止する。
    pub(super) fn step_physics_playback(&mut self) {
        let next = self.edit_physics_current_frame + 1;
        if next >= self.edit_physics_snapshots.len() {
            // 最終フレームに達したら自動停止
            self.edit_physics_paused = true;
            self.edit_physics_in_playback = false;
            self.edit_physics_at_latest = true;
            self.hovered_gizmo_part = None;
            self.send_physics_timeline_state();
            return;
        }

        self.edit_physics_current_frame = next;
        self.restore_snapshot(next);
        self.edit_physics_at_latest = next + 1 >= self.edit_physics_snapshots.len();
        self.hovered_gizmo_part = None;
        self.send_physics_timeline_state();
    }

    /// フレームを N ステップ前後に移動する。
    ///
    /// - `step > 0`: 前進（未来フレームへ）。最新フレームより先には進めない。
    /// - `step < 0`: 後退（過去フレームへ）。フレーム 0 より前には戻れない。
    /// - 停止中のみ有効（再生中は無視）。
    pub(super) fn handle_edit_physics_step(&mut self, step: i32) {
        if !self.edit_physics_paused {
            return;
        }
        if self.edit_physics_snapshots.is_empty() {
            return;
        }

        let max_frame = self.edit_physics_snapshots.len() - 1;
        let cur = self.edit_physics_current_frame as i32;
        let next = (cur + step).clamp(0, max_frame as i32) as usize;

        if next == self.edit_physics_current_frame {
            return;
        }

        self.edit_physics_current_frame = next;
        self.restore_snapshot(next);
        self.edit_physics_at_latest = next >= max_frame;
        // シーク直後にGizmoホバー状態をクリアする（過去フレームでGizmoが残るのを防ぐ）
        self.hovered_gizmo_part = None;
        self.send_physics_timeline_state();
    }

    /// 指定フレームへシークする。
    ///
    /// 停止中はそのままシーク、再生中は自動的に一時停止してからシークする。
    /// これによりシークバーのドラッグ中（再生中）でも任意のフレームに移動できる。
    /// 一時停止後に再開するかどうかはエディタ（C# 側）が PLAY_PAUSE コマンドで制御する。
    pub(super) fn handle_edit_physics_seek(&mut self, frame: usize) {
        if self.edit_physics_snapshots.is_empty() {
            return;
        }

        // 再生中のシーク: 自動で一時停止してからシークする
        if !self.edit_physics_paused {
            let was_in_playback = self.edit_physics_in_playback;
            self.edit_physics_paused = true;
            self.edit_physics_in_playback = false;
            // ライブ物理シミュレーション中だった場合は物理スレッドも停止する
            // （スナップショットプレイバック中は物理スレッドが動いていないため不要）
            if !was_in_playback {
                self.send_pause_to_physics_threads();
            }
        }

        let clamped = frame.min(self.edit_physics_snapshots.len() - 1);
        self.edit_physics_current_frame = clamped;
        self.restore_snapshot(clamped);
        self.edit_physics_at_latest = clamped + 1 >= self.edit_physics_snapshots.len();
        // シーク直後にGizmoホバー状態をクリアする（過去フレームでGizmoが残るのを防ぐ）
        self.hovered_gizmo_part = None;
        self.send_physics_timeline_state();
    }

    /// 指定インデックスのスナップショットを ECS に書き戻す。
    ///
    /// ActorTransform だけでなく、GPU 描画行列（ModelComponent.instance_mats）も同期する。
    /// これを怠ると 3D モデルの描画位置がスナップショット復元後もずれたままになる。
    fn restore_snapshot(&mut self, frame: usize) {
        let Some(snap) = self.edit_physics_snapshots.get(frame).cloned() else {
            return;
        };

        // ── Step 0: このフレーム時点の接触状態（コライダー色）を復元する ─────
        // シーク・ステップ・プレイバックのいずれでも、直前のライブシミュレーション時の
        // 接触状態が凍結・残留しないよう、フレームごとに保存した集合へ差し替える。
        self.active_collision_dfs_ids = snap.colliding_3d.clone();
        self.active_collision_2d_dfs_ids = snap.colliding_2d.clone();

        let Some(scene) = self.scene.as_mut() else {
            return;
        };
        let wl = self.active_world_line;

        // ── Step 1: ActorTransform / CanvasTransform を書き戻す ───────────
        for (entity, tf) in &snap.transforms_3d {
            if let Some(t) = scene.world.get_mut::<ActorTransform>(*entity) {
                *t = tf.clone();
            }
        }
        for (entity, ct) in &snap.transforms_2d {
            if let Some(c) = scene.world.get_mut::<CanvasTransform>(*entity) {
                *c = ct.clone();
            }
        }

        // ── Step 2: ModelComponent の GPU 行列を ActorTransform から再計算する ──
        // Actorツリーを走査してすべての (ActorEntity, ModelSlotEntity) を収集してから更新する。
        // 借用規則を満たすため、収集フェーズ（不変参照）と更新フェーズ（可変参照）を分離する。
        let pairs: Vec<(Entity, Entity)> = {
            let mut result = Vec::new();
            let mut stack: Vec<&_> = scene.actors.iter().filter(|a| a.world_line == wl).collect();
            while let Some(actor) = stack.pop() {
                for child in &actor.children {
                    stack.push(child);
                }
                for slot in actor.slots() {
                    if slot.kind == ComponentKind::Model {
                        result.push((actor.entity, slot.entity));
                    }
                }
            }
            result
        };

        for (actor_entity, slot_entity) in pairs {
            let mat = match scene.world.get::<ActorTransform>(actor_entity) {
                Some(tf) => tf.to_mat4(),
                None => continue,
            };
            if let Some(mc) = scene.world.get_mut::<ModelComponent>(slot_entity) {
                if let Some(m) = mc.instance_mats.first_mut() {
                    *m = mat;
                }
                mc.mark_batch_dirty();
            }
        }
    }

    /// 現在フレームの状態をフレーム 0 として適用する。
    ///
    /// 履歴を全て削除し、現在フレームのスナップショットを新たなフレーム 0 として設定する。
    /// 物理エンジンをこの状態で再起動し、シミュレーション時間をリセットする。
    pub(super) fn handle_edit_physics_apply_frame(&mut self) {
        if self.edit_physics_snapshots.is_empty() {
            return;
        }

        let cur = self.edit_physics_current_frame;
        let Some(snap) = self.edit_physics_snapshots.get(cur).cloned() else {
            return;
        };

        // 現在フレームを time_secs=0.0 のフレーム 0 として再設定する。
        // 接触状態（コライダー色）も引き継いで、このフレーム時点の色を維持する。
        let new_snap = PhysicsSnapshot {
            transforms_3d: snap.transforms_3d,
            transforms_2d: snap.transforms_2d,
            colliding_3d: snap.colliding_3d.clone(),
            colliding_2d: snap.colliding_2d.clone(),
            time_secs: 0.0,
        };
        // 現在の接触色を適用フレームの状態へ揃える（残留した接触色を防ぐ）
        self.active_collision_dfs_ids = snap.colliding_3d;
        self.active_collision_2d_dfs_ids = snap.colliding_2d;

        self.edit_physics_snapshots.clear();
        self.edit_physics_snapshots.push(new_snap);
        self.edit_physics_current_frame = 0;
        self.edit_physics_sim_time = 0.0;
        self.edit_physics_paused = true;
        self.edit_physics_at_latest = true;
        self.edit_physics_no_change_count = 0;

        // 物理エンジンを現在の ECS 状態（適用後の Transform）で再起動する
        self.stop_physics();
        self.stop_physics_2d();
        if self.edit_physics_enabled {
            self.start_physics();
        }
        if self.edit_physics_2d_enabled {
            self.start_physics_2d();
        }

        // 【修正1】再起動した物理スレッドはデフォルトで paused=false のため、
        // Pause を送らないと適用直後から裏で実時間シミュレーションが進み、
        // 次の再生時に経過分だけ進んだ（最終フレーム相当の）状態へジャンプしてしまう。
        // init_physics_timeline と同様に、再起動直後に必ず Pause を送る。
        self.send_pause_to_physics_threads();

        self.hovered_gizmo_part = None;
        self.send_physics_timeline_state();
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// 現在の ECS 状態が最新スナップショットと一致するかを返す。
    ///
    /// 停止中に Gizmo でアクターを移動させた場合に差分が生じる。
    /// 一致すれば Resume（速度保持）、不一致なら再起動（速度リセット）を選択する。
    fn current_ecs_matches_latest_snapshot(&self) -> bool {
        let Some(latest) = self.edit_physics_snapshots.last() else {
            return true;
        };
        let Some(current) = self.capture_current_snapshot_ref() else {
            return true;
        };
        !snapshot_has_change(latest, &current)
    }

    /// 現在の ECS 状態から不変参照でスナップショットを生成する（time_secs=0 のダミー）。
    fn capture_current_snapshot_ref(&self) -> Option<PhysicsSnapshot> {
        self.capture_current_snapshot(0.0)
    }

    /// タイムライン状態を C# エディタへ送信する。
    ///
    /// フォーマット: `EDIT_PHYSICS_STATE:{paused},{current_frame},{total_frames},{time_sec}`
    pub(super) fn send_physics_timeline_state(&self) {
        let Some(ipc) = &self.ipc else { return };

        let paused = if self.edit_physics_paused { 1u8 } else { 0u8 };
        let at_latest = if self.edit_physics_at_latest {
            1u8
        } else {
            0u8
        };
        let cur = self.edit_physics_current_frame;
        let total = self.edit_physics_snapshots.len();
        let time = self.current_snapshot_time();

        ipc.send(&format!(
            "EDIT_PHYSICS_STATE:{paused},{at_latest},{cur},{total},{time:.4}"
        ));
    }

    /// 現在フレームのシミュレーション時間（秒）を返す。
    fn current_snapshot_time(&self) -> f64 {
        self.edit_physics_snapshots
            .get(self.edit_physics_current_frame)
            .map(|s| s.time_secs)
            .unwrap_or(0.0)
    }

    /// 編集時物理シミュレーションが実際にステップを進めるべきかどうかを返す。
    ///
    /// - `edit_physics_enabled` が false → false
    /// - `edit_physics_paused` が true  → false
    ///
    /// 注意: `edit_physics_at_latest` はここでは判定しない。
    /// at_latest は「現在フレームが最新スナップショットを指しているか」を示すだけで、
    /// 再生停止の判定は `edit_physics_paused` で管理する。
    /// at_latest をここで使うと、スナップショット記録のたびに物理が止まってしまう。
    pub(super) fn should_step_edit_physics(&self) -> bool {
        // いずれの編集時物理（3D/2D）も無効ならステップしない
        if !self.edit_physics_enabled && !self.edit_physics_2d_enabled {
            return false;
        }

        // 【変更3(a)】常時押し戻しモード（RigidBody 無効）:
        // タイムラインを使わず物理を毎フレーム走らせて、押し戻しを常時有効にする。
        if self.is_edit_physics_pushback_mode() {
            return true;
        }

        // 再生中は常にステップする
        if !self.edit_physics_paused {
            return true;
        }

        // 【変更3(b)】RigidBody タイムラインモードで最新フレーム停止中:
        // ドラッグ操作が行われている（またはドラッグ中として物理登録済みの）間だけ
        // ステップを許可する。これによりドラッグ開始が update_physics で検知され、
        // ライブシミュレーション（begin_edit_physics_drag_live_sim）が開始される。
        // ドラッグ登録が残っている間（終了直後の Dynamic 復帰送信）もステップを許可する。
        // 過去フレームへシーク中（!at_latest）は誤爆防止のため許可しない。
        if self.edit_physics_at_latest
            && (self.edit_physics_drag_active()
                || self.dragging_physics_entity_id.is_some()
                || self.dragging_physics_2d_entity_id.is_some())
        {
            return true;
        }

        false
    }
}

// ─── ユーティリティ ──────────────────────────────────────────────────────────

/// 2 つのスナップショットに変化があるかどうかを返す。
fn snapshot_has_change(old: &PhysicsSnapshot, new: &PhysicsSnapshot) -> bool {
    // 3D Transform の比較
    if old.transforms_3d.len() != new.transforms_3d.len() {
        return true;
    }
    for ((_, old_tf), (_, new_tf)) in old.transforms_3d.iter().zip(new.transforms_3d.iter()) {
        if transform_differs(old_tf, new_tf) {
            return true;
        }
    }
    // 2D Transform の比較
    if old.transforms_2d.len() != new.transforms_2d.len() {
        return true;
    }
    for ((_, old_ct), (_, new_ct)) in old.transforms_2d.iter().zip(new.transforms_2d.iter()) {
        if canvas_transform_differs(old_ct, new_ct) {
            return true;
        }
    }
    // 接触状態（コライダー色）の比較。
    // Transform が不変でも接触の有無が変われば「変化あり」として記録し、
    // そのフレーム時点の接触色を残す。Transform も接触も不変なら変化なし＝停止でよい。
    if old.colliding_3d != new.colliding_3d {
        return true;
    }
    if old.colliding_2d != new.colliding_2d {
        return true;
    }
    false
}

/// 3D Transform に有意な差異があるか。
fn transform_differs(a: &ActorTransform, b: &ActorTransform) -> bool {
    vec3_differs(a.position, b.position)
        || vec3_differs(a.rotation, b.rotation)
        || vec3_differs(a.scale, b.scale)
}

/// 2D CanvasTransform に有意な差異があるか。
fn canvas_transform_differs(a: &CanvasTransform, b: &CanvasTransform) -> bool {
    vec2_differs(a.position, b.position)
        || (a.rotation - b.rotation).abs() > CHANGE_EPSILON
        || vec2_differs(a.scale, b.scale)
}

fn vec3_differs(a: [f32; 3], b: [f32; 3]) -> bool {
    (a[0] - b[0]).abs() > CHANGE_EPSILON
        || (a[1] - b[1]).abs() > CHANGE_EPSILON
        || (a[2] - b[2]).abs() > CHANGE_EPSILON
}

fn vec2_differs(a: [f32; 2], b: [f32; 2]) -> bool {
    (a[0] - b[0]).abs() > CHANGE_EPSILON || (a[1] - b[1]).abs() > CHANGE_EPSILON
}
