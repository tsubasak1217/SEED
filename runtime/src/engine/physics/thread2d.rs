// ============================================================
//  physics/thread2d.rs — Rapier2D バックエンド物理スレッド
//
//  【責務】
//    60Hz 固定タイムステップで rapier2d の物理シミュレーションを実行し、
//    メインスレッドとの間でコマンド・結果をチャンネル経由でやり取りする。
//
//  【座標系】
//    外部（App / CanvasTransform）はキャンバスピクセル座標を使用する。
//    物理スレッド内部はメートル座標を使用する。
//    変換: AddObject 時に px → m（÷ PIXELS_PER_METER）、
//          結果送信時はメートルのまま返す（write_back_canvas_transform 側で × PIXELS_PER_METER）。
//
//  【Y 軸方向】
//    キャンバスは Y+ = 画面下方向。物理スレッドも Y+ = 下方向とする。
//    デフォルト重力は [0, +9.81] m/s²（下向き）。
// ============================================================

use rapier2d::prelude::*;
use std::collections::{HashMap, HashSet};
use crossbeam_channel::{Receiver, Sender, TryRecvError, unbounded};
use std::time::{Duration, Instant};

use super::types2d::{
    ColliderShape2d,
    CollisionEvent2d, CollisionPhase2d,
    PhysicsCommand2d, PhysicsObject2d, PhysicsResult2d,
    TriggerEvent2d, TriggerPhase2d,
    DEFAULT_GRAVITY_2D, PHYSICS_2D_FIXED_STEP, PIXELS_PER_METER,
};

// ─── スムーズドラッグ速度クランプ定数（2D）─────────────────────────────────
// 3D 版 thread.rs の MAX_DRAG_LINEAR_SPEED / MAX_DRAG_ANGULAR_SPEED と同じ意味。
// set_next_kinematic_position は「(目標 - 現在)/dt」を接触相手へ伝える速度とするため、
// 1 フレーム分の目標ジャンプ Δ でも Δ/dt が過大になり、乗っている Dynamic ボディを
// 吹き飛ばす。1 ステップの移動量を最大速度 × dt に制限して伝達速度を頭打ちにする。

/// スムーズドラッグ中の kinematic ボディが目標へ追従する最大並進速度（m/s）。
const MAX_DRAG_LINEAR_SPEED_2D: Real = 5.0;

/// スムーズドラッグ中の kinematic ボディが目標へ追従する最大角速度（rad/s）。
const MAX_DRAG_ANGULAR_SPEED_2D: Real = 6.0;

// ─── PhysicsThread2d（公開 API）──────────────────────────────────────────────

/// Rapier 2D 物理シミュレーションを実行するバックグラウンドスレッドのハンドル。
///
/// Drop 時に Stop コマンドを送信してスレッドを安全に終了させる。
pub struct PhysicsThread2d {
    /// コマンド送信チャンネル（メイン → 物理スレッド）
    cmd_tx: Sender<PhysicsCommand2d>,
    /// 結果受信チャンネル（物理スレッド → メイン）
    res_rx: Receiver<PhysicsResult2d>,
    /// スレッドハンドル（Drop 時に join する）
    handle: Option<std::thread::JoinHandle<()>>,
}

impl PhysicsThread2d {
    /// 2D 物理スレッドを起動して PhysicsThread2d ハンドルを返す。
    pub fn spawn() -> Self {
        let (cmd_tx, cmd_rx) = unbounded::<PhysicsCommand2d>();
        let (res_tx, res_rx) = unbounded::<PhysicsResult2d>();

        let handle = std::thread::spawn(move || {
            run_physics_loop_2d(cmd_rx, res_tx);
        });

        PhysicsThread2d { cmd_tx, res_rx, handle: Some(handle) }
    }

    /// コマンドを 2D 物理スレッドへ送信する。
    pub fn send(&self, cmd: PhysicsCommand2d) {
        let _ = self.cmd_tx.send(cmd);
    }

    /// 蓄積された結果から最新の 1 件を取得し、古い結果は破棄する。
    ///
    /// 結果がなければ `None` を返す。
    pub fn recv_latest(&self) -> Option<PhysicsResult2d> {
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

impl Drop for PhysicsThread2d {
    fn drop(&mut self) {
        // Stop コマンドを送信してスレッドを終了させ、join する
        let _ = self.cmd_tx.send(PhysicsCommand2d::Stop);
        if let Some(handle) = self.handle.take() {
            let _ = handle.join();
        }
    }
}

// ─── 物理スレッド内部エントリ ────────────────────────────────────────────────

/// 物理ワールドに登録した 2D オブジェクトの Rapier ハンドル情報。
struct PhysicsEntry2d {
    /// Rapier リジッドボディハンドル（Static の場合は None）
    rb_handle:  Option<RigidBodyHandle>,
    /// Rapier コライダーハンドル
    col_handle: ColliderHandle,
    /// Dynamic Rigidbody かどうか（Transform 結果送信の対象か）
    is_dynamic: bool,
    /// Static コライダーのローカルオフセット [x, y]（メートル）
    col_offset: [f32; 2],
    /// ドラッグ押し戻し用に動的生成した KinematicPositionBased リジッドボディかどうか。
    rb_created_for_drag: bool,
    /// Static コライダーのドラッグ終了後の再生成に必要なデータ。
    col_shape_data: Option<StaticColliderData2d>,
}

/// SetBodyKinematic によるコライダー再生成のためのデータ（2D 版）。
#[derive(Clone)]
struct StaticColliderData2d {
    shape:       ColliderShape2d,
    scale:       [f32; 2],
    friction:    f32,
    restitution: f32,
    is_trigger:  bool,
}

// ─── メインループ ────────────────────────────────────────────────────────────

/// 2D 物理スレッドのメインループ。
fn run_physics_loop_2d(
    cmd_rx: Receiver<PhysicsCommand2d>,
    res_tx: Sender<PhysicsResult2d>,
) {
    // ── Rapier2D 物理ワールドオブジェクト ────────────────────────────────────
    let mut rigid_body_set      = RigidBodySet::new();
    let mut collider_set        = ColliderSet::new();
    let mut physics_pipeline    = PhysicsPipeline::new();
    let mut island_manager      = IslandManager::new();
    let mut broad_phase         = DefaultBroadPhase::new();
    let mut narrow_phase        = NarrowPhase::new();
    let mut impulse_joint_set   = ImpulseJointSet::new();
    let mut multibody_joint_set = MultibodyJointSet::new();
    let mut ccd_solver          = CCDSolver::new();

    // 重力ベクトル（SetGravity コマンドで変更可能）
    let mut gravity = vector![DEFAULT_GRAVITY_2D[0], DEFAULT_GRAVITY_2D[1]];

    // 統合パラメータ（固定ステップ時間を設定）
    let mut integration_params = IntegrationParameters::default();
    integration_params.dt = PHYSICS_2D_FIXED_STEP as Real;

    // ── entity_id ↔ Rapier ハンドル マッピング ──────────────────────────────
    let mut entries:         HashMap<u64, PhysicsEntry2d>  = HashMap::new();
    let mut col_to_entity:   HashMap<ColliderHandle, u64>   = HashMap::new();
    let mut trigger_set:     HashSet<u64>                   = HashSet::new();
    let mut active_contacts: HashSet<(u64, u64)>            = HashSet::new();
    let mut active_triggers: HashSet<(u64, u64)>            = HashSet::new();

    // ── Rapier イベントコレクター ────────────────────────────────────────────
    let (col_evt_tx, col_evt_rx) = unbounded::<CollisionEvent>();
    let (force_evt_tx, _force_rx) = unbounded::<ContactForceEvent>();
    let event_handler = ChannelEventCollector::new(col_evt_tx, force_evt_tx);

    // ── タイムステップ制御 ────────────────────────────────────────────────────
    let step_duration = Duration::from_secs_f64(PHYSICS_2D_FIXED_STEP);
    let mut next_step = Instant::now();

    // Pause/Resume 状態（true のとき物理ステップをスキップ、速度は保持される）
    let mut paused = false;

    // ── スムーズドラッグ状態（2D）──────────────────────────────────────────
    // SetBodyKinematic(is_kinematic=true, smooth=true) された「スムーズドラッグ中」
    // ボディの entity_id → 目標ワールド姿勢のマップ。キーが存在する間、UpdateKinematic は
    // 即時反映せず目標だけを更新し、毎ステップ直前に advance_smooth_drag_targets_2d が
    // 最大速度クランプ付きで目標へ追従させる（3D 版 thread.rs と同じ仕組み）。
    let mut drag_targets: HashMap<u64, Isometry<Real>> = HashMap::new();

    loop {
        // ── コマンド処理（全キューをフラッシュ）────────────────────────────
        loop {
            match cmd_rx.try_recv() {
                Ok(PhysicsCommand2d::Stop)   => return,
                Ok(PhysicsCommand2d::Pause)  => { paused = true; }
                Ok(PhysicsCommand2d::Resume) => {
                    paused = false;
                    next_step = Instant::now();
                }
                Ok(cmd) => handle_command_2d(
                    cmd,
                    &mut rigid_body_set, &mut collider_set,
                    &mut island_manager, &mut impulse_joint_set, &mut multibody_joint_set,
                    &mut entries, &mut col_to_entity,
                    &mut trigger_set, &mut active_contacts, &mut active_triggers,
                    &mut gravity, &mut drag_targets,
                ),
                Err(TryRecvError::Empty)        => break,
                Err(TryRecvError::Disconnected) => return,
            }
        }

        // Pause 中は物理ステップをスキップ
        if paused {
            std::thread::sleep(Duration::from_millis(5));
            continue;
        }

        // ── 物理ステップ ─────────────────────────────────────────────────────
        let now = Instant::now();
        if now < next_step {
            let remaining = next_step - now;
            std::thread::sleep(remaining.min(Duration::from_millis(1)));
            continue;
        }

        // スムーズドラッグ: このステップの次目標位置を最大速度クランプ付きで更新する。
        // ステップ直前・かつ実際にステップを実行するタイミングでのみ前進させることで、
        // 1 ステップあたりの移動量（= 伝達速度 × dt）を確実に上限内に収める。
        advance_smooth_drag_targets_2d(
            &mut rigid_body_set, &entries, &drag_targets, PHYSICS_2D_FIXED_STEP as Real,
        );

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
            None,
            &(),
            &event_handler,
        );

        next_step = now + step_duration;

        // ── 結果収集・送信 ────────────────────────────────────────────────────
        let result = collect_results_2d(
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

// ─── スムーズドラッグ前進（2D）──────────────────────────────────────────────

/// スムーズドラッグ中の各 kinematic ボディについて、このステップの次目標位置を
/// 現在位置から目標へ「最大速度クランプ付き」で前進させる（3D 版 thread.rs と同じ）。
///
/// set_next_kinematic_position(next) で Rapier に渡す (next - current) を
/// MAX_DRAG_LINEAR_SPEED_2D*dt / MAX_DRAG_ANGULAR_SPEED_2D*dt に制限することで、
/// 接触相手へ伝わる速度 (next-current)/dt がこの上限で頭打ちになる。
fn advance_smooth_drag_targets_2d(
    rb_set:       &mut RigidBodySet,
    entries:      &HashMap<u64, PhysicsEntry2d>,
    drag_targets: &HashMap<u64, Isometry<Real>>,
    dt:           Real,
) {
    let max_lin = MAX_DRAG_LINEAR_SPEED_2D  * dt;
    let max_ang = MAX_DRAG_ANGULAR_SPEED_2D * dt;
    for (entity_id, target) in drag_targets.iter() {
        let Some(entry) = entries.get(entity_id) else { continue };
        let Some(rb_h)  = entry.rb_handle else { continue };
        let Some(rb)    = rb_set.get_mut(rb_h) else { continue };
        if !rb.is_kinematic() { continue; }
        let current = *rb.position();
        let next    = step_isometry_toward_2d(&current, target, max_lin, max_ang);
        rb.set_next_kinematic_position(next);
    }
}

/// `current` から `target` へ、並進を最大 `max_lin`、回転を最大 `max_ang`（ラジアン）
/// だけ進めた中間 Isometry を返す。どちらも上限未満なら target 側の値をそのまま使う。
fn step_isometry_toward_2d(
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

    // 回転クランプ（現在→目標の角度差を max_ang に制限して補間）
    let angle = current.rotation.angle_to(&target.rotation);
    let new_rot = if angle > max_ang && angle > 1e-9 {
        current.rotation.slerp(&target.rotation, max_ang / angle)
    } else {
        target.rotation
    };

    Isometry::from_parts(Translation::from(new_trans), new_rot)
}

// ─── コマンド処理 ────────────────────────────────────────────────────────────

/// Stop 以外の 2D コマンドを処理する。
#[allow(clippy::too_many_arguments)]
fn handle_command_2d(
    cmd:               PhysicsCommand2d,
    rb_set:            &mut RigidBodySet,
    col_set:           &mut ColliderSet,
    island_manager:    &mut IslandManager,
    impulse_joints:    &mut ImpulseJointSet,
    multibody_joints:  &mut MultibodyJointSet,
    entries:           &mut HashMap<u64, PhysicsEntry2d>,
    col_to_entity:     &mut HashMap<ColliderHandle, u64>,
    trigger_set:       &mut HashSet<u64>,
    active_contacts:   &mut HashSet<(u64, u64)>,
    active_triggers:   &mut HashSet<(u64, u64)>,
    gravity:           &mut nalgebra::Vector2<Real>,
    drag_targets:      &mut HashMap<u64, Isometry<Real>>,
) {
    match cmd {
        PhysicsCommand2d::Stop   => { /* 呼び出し元で処理済み */ }
        PhysicsCommand2d::Pause  => { /* ループ側で処理済み */ }
        PhysicsCommand2d::Resume => { /* ループ側で処理済み */ }

        PhysicsCommand2d::AddObject(obj) => {
            add_object_2d(obj, rb_set, col_set, entries, col_to_entity, trigger_set);
        }

        PhysicsCommand2d::RemoveObject { entity_id } => {
            drag_targets.remove(&entity_id);
            if let Some(entry) = entries.remove(&entity_id) {
                col_to_entity.remove(&entry.col_handle);
                trigger_set.remove(&entity_id);
                active_contacts.retain(|&(a, b)| a != entity_id && b != entity_id);
                active_triggers.retain(|&(t, o)| t != entity_id && o != entity_id);

                if let Some(rb_h) = entry.rb_handle {
                    rb_set.remove(rb_h, island_manager, col_set, impulse_joints, multibody_joints, true);
                } else {
                    col_set.remove(entry.col_handle, island_manager, rb_set, false);
                }
            }
        }

        PhysicsCommand2d::UpdateKinematic { entity_id, position, rotation } => {
            // スムーズドラッグ中のボディは即時反映せず「目標」だけ更新する。
            // 実際の前進（速度クランプ付き）はステップ直前の advance_smooth_drag_targets_2d が行う。
            if let Some(target) = drag_targets.get_mut(&entity_id) {
                *target = to_isometry_2d(position, rotation);
                return;
            }
            if let Some(entry) = entries.get(&entity_id) {
                if let Some(rb_h) = entry.rb_handle {
                    if let Some(rb) = rb_set.get_mut(rb_h) {
                        if rb.is_kinematic() {
                            rb.set_next_kinematic_position(to_isometry_2d(position, rotation));
                        }
                    }
                } else {
                    // Static コライダーの位置を直接更新する
                    if let Some(col) = col_set.get_mut(entry.col_handle) {
                        let world_iso = to_isometry_2d(position, rotation);
                        let o = entry.col_offset;
                        let offset_iso = Isometry::translation(o[0], o[1]);
                        col.set_position(world_iso * offset_iso);
                    }
                }
            }
        }

        PhysicsCommand2d::SetBodyKinematic { entity_id, is_kinematic, final_position, smooth } => {
            // Dynamic 復帰・kinematic 解除時はスムーズドラッグ登録を必ず解除する。
            if !is_kinematic {
                drag_targets.remove(&entity_id);
            }
            if let Some(entry) = entries.get_mut(&entity_id) {
                let rb_h_opt = entry.rb_handle;
                let created_for_drag = entry.rb_created_for_drag;

                if let Some(rb_h) = rb_h_opt {
                    if created_for_drag && !is_kinematic {
                        // 動的生成 rb の解放: rb 削除 → static コライダー再作成
                        let shape_data = entry.col_shape_data.clone();
                        let col_offset = entry.col_offset;

                        let final_iso = if let Some((pos, rot)) = final_position {
                            to_isometry_2d(pos, rot)
                        } else {
                            rb_set.get(rb_h).map(|rb| *rb.position()).unwrap_or_else(Isometry::identity)
                        };

                        col_to_entity.remove(&entry.col_handle);
                        rb_set.remove(rb_h, island_manager, col_set, impulse_joints, multibody_joints, true);

                        if let Some(sd) = shape_data {
                            let offset_iso = Isometry::translation(col_offset[0], col_offset[1]);
                            let col_world_pos = final_iso * offset_iso;

                            let new_col = build_collider_shape_2d(&sd.shape, &sd.scale)
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
                                // ECS 位置を使って Rapier 内部との差異による回転ジャークを防ぐ
                                let lock_iso = if let Some((pos, rot)) = final_position {
                                    to_isometry_2d(pos, rot)
                                } else {
                                    *rb.position()
                                };
                                rb.set_next_kinematic_position(lock_iso);
                                // スムーズドラッグ登録: 以降の UpdateKinematic は目標のみ更新し、
                                // advance_smooth_drag_targets_2d が速度クランプ付きで追従させる。
                                if smooth {
                                    drag_targets.insert(entity_id, lock_iso);
                                }
                            } else {
                                // Dynamic 復帰: 最終座標セット + 速度ゼロリセット
                                if let Some((pos, rot)) = final_position {
                                    rb.set_position(to_isometry_2d(pos, rot), true);
                                }
                                rb.set_linvel(vector![0.0, 0.0], true);
                                rb.set_angvel(0.0, true);
                            }
                        }
                        // is_dynamic フラグを同期する
                        entry.is_dynamic = !is_kinematic;
                    }
                } else if is_kinematic {
                    // Static → KinematicPositionBased に昇格（ドラッグ開始時）
                    let shape_data = entry.col_shape_data.clone();
                    let col_offset = entry.col_offset;

                    if let Some(sd) = shape_data {
                        let col_world_pos = col_set.get(entry.col_handle)
                            .map(|c| *c.position())
                            .unwrap_or_else(Isometry::identity);

                        let offset_iso = Isometry::translation(col_offset[0], col_offset[1]);
                        let actor_world_pos = col_world_pos * offset_iso.inverse();

                        col_to_entity.remove(&entry.col_handle);
                        col_set.remove(entry.col_handle, island_manager, rb_set, false);

                        let rb = RigidBodyBuilder::kinematic_position_based()
                            .position(actor_world_pos)
                            .build();
                        let rb_h = rb_set.insert(rb);

                        let new_col = build_collider_shape_2d(&sd.shape, &sd.scale)
                            .position(offset_iso)
                            .friction(sd.friction)
                            .restitution(sd.restitution)
                            .sensor(sd.is_trigger)
                            .active_events(ActiveEvents::COLLISION_EVENTS)
                            .active_collision_types(ActiveCollisionTypes::all())
                            .build();
                        let new_col_h = col_set.insert_with_parent(new_col, rb_h, rb_set);

                        if let Some(rb) = rb_set.get_mut(rb_h) {
                            rb.set_next_kinematic_position(actor_world_pos);
                        }
                        // スムーズドラッグ指定時は目標追従に切り替える。
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

        PhysicsCommand2d::SetGravity { gravity: g } => {
            *gravity = vector![g[0], g[1]];
        }

        PhysicsCommand2d::ApplyForce { entity_id, force } => {
            if let Some(entry) = entries.get(&entity_id) {
                if let Some(rb_h) = entry.rb_handle {
                    if let Some(rb) = rb_set.get_mut(rb_h) {
                        rb.add_force(vector![force[0], force[1]], true);
                    }
                }
            }
        }

        PhysicsCommand2d::ApplyImpulse { entity_id, impulse } => {
            if let Some(entry) = entries.get(&entity_id) {
                if let Some(rb_h) = entry.rb_handle {
                    if let Some(rb) = rb_set.get_mut(rb_h) {
                        rb.apply_impulse(vector![impulse[0], impulse[1]], true);
                    }
                }
            }
        }

        PhysicsCommand2d::RescaleCollider { entity_id, shape, scale, collider_offset } => {
            // リジッドボディ付きコライダーの形状を差し替える。
            // 旧コライダーを削除して新しい形状・オフセットで再作成し、
            // リジッドボディの位置・速度・回転はそのまま保持する。
            // Static コライダー（rb_handle=None）は RemoveObject+AddObject で処理するため対象外。
            if let Some(entry) = entries.get(&entity_id) {
                let Some(rb_h) = entry.rb_handle else { return };
                let old_col_h  = entry.col_handle;

                // 旧コライダーから摩擦・反発係数・センサーフラグを継承する
                let (friction, restitution, is_trigger) = col_set.get(old_col_h)
                    .map(|c| (c.friction(), c.restitution(), c.is_sensor()))
                    .unwrap_or((0.5, 0.3, false));

                // 旧コライダーを削除する（rb は保持）。
                // wake_up=true でボディをスリープ解除しておく。
                // これをしないと差し替え後のボディが眠ったままになり重力・押し戻しが再開しない。
                col_to_entity.remove(&old_col_h);
                col_set.remove(old_col_h, island_manager, rb_set, true);

                // 新コライダーをローカルオフセット位置で作成して親 rb に attach する
                let offset_iso = Isometry::translation(collider_offset[0], collider_offset[1]);
                let new_col = build_collider_shape_2d(&shape, &scale)
                    .position(offset_iso)
                    .friction(friction)
                    .restitution(restitution)
                    .sensor(is_trigger)
                    .active_events(ActiveEvents::COLLISION_EVENTS)
                    .active_collision_types(ActiveCollisionTypes::all())
                    .build();

                let new_col_h = col_set.insert_with_parent(new_col, rb_h, rb_set);
                col_to_entity.insert(new_col_h, entity_id);

                // 新コライダー挿入後も念のりボディを明示的にウェイクアップする
                if let Some(rb) = rb_set.get_mut(rb_h) {
                    rb.wake_up(true);
                }

                // エントリを更新する
                if let Some(entry) = entries.get_mut(&entity_id) {
                    entry.col_handle = new_col_h;
                    entry.col_offset = collider_offset;
                }
            }
        }
    }
}

// ─── オブジェクト追加 ────────────────────────────────────────────────────────

/// `PhysicsObject2d` を Rapier ワールドへ登録する。
fn add_object_2d(
    obj:           PhysicsObject2d,
    rb_set:        &mut RigidBodySet,
    col_set:       &mut ColliderSet,
    entries:       &mut HashMap<u64, PhysicsEntry2d>,
    col_to_entity: &mut HashMap<ColliderHandle, u64>,
    trigger_set:   &mut HashSet<u64>,
) {
    let world_iso  = to_isometry_2d(obj.position, obj.rotation);
    let offset_iso = Isometry::translation(obj.collider_offset[0], obj.collider_offset[1]);

    let friction    = obj.rigidbody.as_ref().map_or(0.5, |rb| rb.friction);
    let restitution = obj.rigidbody.as_ref().map_or(0.3, |rb| rb.restitution);

    // ── リジッドボディ作成 ──────────────────────────────────────────────────
    let rb_handle = obj.rigidbody.as_ref().map(|rb_state| {
        let rb_type = if rb_state.is_kinematic {
            RigidBodyType::KinematicPositionBased
        } else {
            RigidBodyType::Dynamic
        };

        let mut rb_builder = RigidBodyBuilder::new(rb_type)
            .position(world_iso)
            .gravity_scale(rb_state.gravity_scale)
            .linear_damping(rb_state.linear_damping)
            .angular_damping(rb_state.angular_damping)
            .linvel(vector![rb_state.linear_velocity[0], rb_state.linear_velocity[1]])
            .angvel(rb_state.angular_velocity);

        // 軸フリーズの適用
        let mut locked = LockedAxes::empty();
        if rb_state.freeze_position[0] { locked |= LockedAxes::TRANSLATION_LOCKED_X; }
        if rb_state.freeze_position[1] { locked |= LockedAxes::TRANSLATION_LOCKED_Y; }
        if rb_state.freeze_rotation    { locked |= LockedAxes::ROTATION_LOCKED;      }
        rb_builder = rb_builder.locked_axes(locked);

        rb_set.insert(rb_builder.build())
    });

    // ── コライダー作成・登録 ─────────────────────────────────────────────────
    let collider_pos = if rb_handle.is_some() { offset_iso } else { world_iso * offset_iso };

    let collider = build_collider_shape_2d(&obj.collider, &obj.scale)
        .position(collider_pos)
        .friction(friction)
        .restitution(restitution)
        .sensor(obj.is_trigger)
        .active_events(ActiveEvents::COLLISION_EVENTS)
        .active_collision_types(ActiveCollisionTypes::all())
        .build();

    let col_handle = match rb_handle {
        Some(rb_h) => col_set.insert_with_parent(collider, rb_h, rb_set),
        None       => col_set.insert(collider),
    };

    let is_dynamic = obj.rigidbody.as_ref().map_or(false, |rb| !rb.is_kinematic);

    let col_shape_data = if rb_handle.is_none() {
        Some(StaticColliderData2d {
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
    entries.insert(obj.entity_id, PhysicsEntry2d {
        rb_handle,
        col_handle,
        is_dynamic,
        col_offset: obj.collider_offset,
        rb_created_for_drag: false,
        col_shape_data,
    });
}

// ─── 結果収集 ────────────────────────────────────────────────────────────────

/// 1 ステップ分の 2D 物理結果を収集して `PhysicsResult2d` を返す。
///
/// 結果の位置・回転はメートル → ピクセルに変換して返す。
fn collect_results_2d(
    rb_set:          &RigidBodySet,
    narrow_phase:    &NarrowPhase,
    entries:         &HashMap<u64, PhysicsEntry2d>,
    col_evt_rx:      &Receiver<CollisionEvent>,
    col_to_entity:   &HashMap<ColliderHandle, u64>,
    trigger_set:     &HashSet<u64>,
    active_contacts: &mut HashSet<(u64, u64)>,
    active_triggers: &mut HashSet<(u64, u64)>,
) -> PhysicsResult2d {
    // ── Dynamic Rigidbody の Transform・速度取得 ─────────────────────────────
    // 収束停止判定用に、全 Dynamic ボディの並進・角速度の最大の大きさも集計する。
    let mut transform_updates = Vec::new();
    let mut max_linear_speed:  f32 = 0.0;
    let mut max_angular_speed: f32 = 0.0;
    for (entity_id, entry) in entries.iter() {
        if !entry.is_dynamic { continue; }
        let Some(rb_h) = entry.rb_handle else { continue };
        let Some(rb)   = rb_set.get(rb_h) else { continue };

        let pos = rb.translation();
        let rot = rb.rotation().angle();
        // メートル単位のまま返す。
        // write_back_canvas_transform 側で × PIXELS_PER_METER するため、ここでは変換しない。
        // （二重変換すると位置が 100 倍になるバグが発生する）
        transform_updates.push((
            *entity_id,
            [pos.x, pos.y],
            rot,
        ));

        // 速度の大きさ（静止判定に使用）。2D の角速度はスカラー。
        max_linear_speed  = max_linear_speed.max(rb.linvel().norm());
        max_angular_speed = max_angular_speed.max(rb.angvel().abs());
    }

    // ── 衝突イベント処理 ─────────────────────────────────────────────────────
    let mut collision_events = Vec::new();
    let mut trigger_events   = Vec::new();

    while let Ok(evt) = col_evt_rx.try_recv() {
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
                    trigger_events.push(TriggerEvent2d {
                        trigger_entity: te, other_entity: oe, phase: TriggerPhase2d::Enter,
                    });
                }
            } else {
                let key = if ea < eb { (ea, eb) } else { (eb, ea) };
                if active_contacts.insert(key) {
                    collision_events.push(CollisionEvent2d {
                        entity_a: ea, entity_b: eb, phase: CollisionPhase2d::Enter,
                    });
                }
            }
        } else {
            if is_ta || is_tb {
                let (te, oe) = if is_ta { (ea, eb) } else { (eb, ea) };
                if active_triggers.remove(&(te, oe)) {
                    trigger_events.push(TriggerEvent2d {
                        trigger_entity: te, other_entity: oe, phase: TriggerPhase2d::Exit,
                    });
                }
            } else {
                let key = if ea < eb { (ea, eb) } else { (eb, ea) };
                if active_contacts.remove(&key) {
                    collision_events.push(CollisionEvent2d {
                        entity_a: ea, entity_b: eb, phase: CollisionPhase2d::Exit,
                    });
                }
            }
        }
    }

    // Stay イベントを毎ステップ送信する
    for &(ea, eb) in active_contacts.iter() {
        collision_events.push(CollisionEvent2d {
            entity_a: ea, entity_b: eb, phase: CollisionPhase2d::Stay,
        });
    }

    // ── NarrowPhase 直接クエリ ──────────────────────────────────────────────
    let mut active_contact_entity_ids: Vec<u64> = Vec::new();
    for pair in narrow_phase.contact_pairs() {
        if !pair.has_any_active_contact { continue; }
        let Some(ea) = col_to_entity.get(&pair.collider1).copied() else { continue };
        let Some(eb) = col_to_entity.get(&pair.collider2).copied() else { continue };
        if trigger_set.contains(&ea) || trigger_set.contains(&eb) { continue; }
        if !active_contact_entity_ids.contains(&ea) {
            active_contact_entity_ids.push(ea);
        }
        if !active_contact_entity_ids.contains(&eb) {
            active_contact_entity_ids.push(eb);
        }
    }

    // ── アクティブトリガーエンティティ収集 ──────────────────────────────────
    let mut active_trigger_entity_ids: Vec<u64> = Vec::new();
    for &(te, oe) in active_triggers.iter() {
        if !active_trigger_entity_ids.contains(&te) { active_trigger_entity_ids.push(te); }
        if !active_trigger_entity_ids.contains(&oe) { active_trigger_entity_ids.push(oe); }
    }

    PhysicsResult2d {
        transform_updates,
        collision_events,
        trigger_events,
        active_contact_entity_ids,
        active_trigger_entity_ids,
        max_linear_speed,
        max_angular_speed,
    }
}

// ─── 変換ユーティリティ ──────────────────────────────────────────────────────

/// SEED の 2D 位置（メートル）と回転（ラジアン）を Rapier の 2D Isometry に変換する。
fn to_isometry_2d(position: [f32; 2], rotation: f32) -> Isometry<Real> {
    Isometry::new(vector![position[0], position[1]], rotation)
}

/// `ColliderShape2d` から Rapier 2D の `ColliderBuilder` を構築する。
///
/// `scale` を半辺長・半径に乗算してワールドスケールを反映する。
/// 形状寸法はメートル単位で渡されることを前提とする。
fn build_collider_shape_2d(shape: &ColliderShape2d, scale: &[f32; 2]) -> ColliderBuilder {
    match shape {
        ColliderShape2d::Box { half_extents: [hx, hy] } => {
            ColliderBuilder::cuboid(hx * scale[0], hy * scale[1])
        }
        ColliderShape2d::Circle { radius } => {
            // 均等スケールを前提として X 軸スケールを使用する
            ColliderBuilder::ball(*radius * scale[0])
        }
        ColliderShape2d::Capsule { radius, half_height } => {
            // Y 軸方向カプセル（縦向き）
            ColliderBuilder::capsule_y(*half_height * scale[1], *radius * scale[0])
        }
        ColliderShape2d::ConvexHull { vertices } => {
            let pts: Vec<nalgebra::Point2<Real>> = vertices
                .iter()
                .map(|&[x, y]| nalgebra::Point2::new(x * scale[0], y * scale[1]))
                .collect();
            ColliderBuilder::convex_hull(&pts).unwrap_or_else(|| {
                eprintln!("[Physics2D] Warning: ConvexHull2d 生成失敗。代替 Ball を使用");
                ColliderBuilder::ball(0.01)
            })
        }
    }
}
