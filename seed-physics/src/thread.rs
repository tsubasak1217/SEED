// ============================================================
//  thread.rs — 固定ステップ物理スレッド
//
//  【スレッド構成】
//    メインスレッド ──[command_tx]──> 物理スレッド
//    メインスレッド <──[result_tx]── 物理スレッド
//
//  【固定ステップ】
//    PHYSICS_FIXED_STEP (1/60 秒) ごとに physics_step() を実行する。
//
//  【コマンド一覧】
//    AddObject     : オブジェクト追加
//    RemoveObject  : オブジェクト削除
//    UpdateKinematic: Kinematic オブジェクトの Transform を更新
//    SetGravity    : 重力ベクトル変更
//    ApplyForce    : 任意オブジェクトに力を加える
//    ApplyTorque   : 任意オブジェクトにトルクを加える
//    ApplyImpulse  : 瞬間的な速度変化
//    Stop          : スレッド終了
// ============================================================

use std::sync::mpsc;
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use crate::math::{Vector3, Quaternion};
use crate::world::{PhysicsWorld, PhysicsObject};
use crate::events::{CollisionEvent, TriggerEvent};

/// 物理演算の固定タイムステップ（秒）。
pub const PHYSICS_FIXED_STEP: f64 = 1.0 / 60.0;

// ─── PhysicsCommand ──────────────────────────────────────────────────────────

/// メインスレッド → 物理スレッドへ送るコマンド。
#[derive(Debug)]
pub enum PhysicsCommand {
    /// 物理オブジェクトを追加する
    AddObject(PhysicsObject),
    /// 物理オブジェクトを削除する
    RemoveObject { entity_id: u64 },
    /// Kinematic オブジェクトの Transform を更新する
    UpdateKinematic {
        entity_id: u64,
        position:  Vector3<f32>,
        rotation:  Quaternion,
    },
    /// 重力ベクトルを変更する
    SetGravity { gravity: Vector3<f32> },
    /// 力を加える（蓄積 → 次ステップで速度に積分）
    ApplyForce { entity_id: u64, force: Vector3<f32> },
    /// トルクを加える
    ApplyTorque { entity_id: u64, torque: Vector3<f32> },
    /// 瞬間的な線形速度変化
    ApplyImpulse { entity_id: u64, impulse: Vector3<f32> },
    /// スレッドを終了させる
    Stop,
}

// ─── PhysicsResult ───────────────────────────────────────────────────────────

/// 物理スレッド → メインスレッドへ送る結果。
#[derive(Debug)]
pub struct PhysicsResult {
    /// 動的 Rigidbody の Transform 更新 (entity_id, new_position, new_rotation)
    pub transform_updates: Vec<(u64, Vector3<f32>, Quaternion)>,
    /// 衝突イベント（Enter / Stay / Exit）
    pub collision_events:  Vec<CollisionEvent>,
    /// トリガーイベント（Enter / Exit）
    pub trigger_events:    Vec<TriggerEvent>,
}

// ─── PhysicsThread ───────────────────────────────────────────────────────────

/// 物理スレッドのハンドル。メインスレッドが所有する。
pub struct PhysicsThread {
    /// コマンド送信チャンネル
    pub command_tx: mpsc::Sender<PhysicsCommand>,
    /// 結果受信チャンネル
    pub result_rx:  mpsc::Receiver<PhysicsResult>,
    /// スレッドハンドル
    handle: Option<JoinHandle<()>>,
}

impl PhysicsThread {
    /// 物理スレッドを起動して PhysicsThread を返す。
    pub fn spawn() -> Self {
        let (cmd_tx, cmd_rx) = mpsc::channel::<PhysicsCommand>();
        let (res_tx, res_rx) = mpsc::channel::<PhysicsResult>();

        let handle = thread::Builder::new()
            .name("physics".to_owned())
            .spawn(move || {
                physics_thread_main(cmd_rx, res_tx);
            })
            .expect("[Physics] スレッド起動に失敗しました");

        Self {
            command_tx: cmd_tx,
            result_rx:  res_rx,
            handle:     Some(handle),
        }
    }

    /// コマンドを送信する（失敗時は黙殺）。
    #[inline]
    pub fn send(&self, cmd: PhysicsCommand) {
        let _ = self.command_tx.send(cmd);
    }

    /// 最新の結果を取り出す。複数結果がある場合は最後の 1 件のみ使用する。
    pub fn recv_latest(&self) -> Option<PhysicsResult> {
        let mut latest = None;
        while let Ok(result) = self.result_rx.try_recv() {
            latest = Some(result);
        }
        latest
    }
}

impl Drop for PhysicsThread {
    fn drop(&mut self) {
        let _ = self.command_tx.send(PhysicsCommand::Stop);
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }
}

// ─── 物理スレッドメイン ──────────────────────────────────────────────────────

/// 物理スレッドのメインループ。固定 60Hz で step() を実行する。
fn physics_thread_main(
    cmd_rx: mpsc::Receiver<PhysicsCommand>,
    res_tx: mpsc::Sender<PhysicsResult>,
) {
    let mut world       = PhysicsWorld::new();
    let     step_dur    = Duration::from_secs_f64(PHYSICS_FIXED_STEP);
    let mut next_step   = Instant::now() + step_dur;

    loop {
        // ── コマンドを処理 ───────────────────────────────
        while let Ok(cmd) = cmd_rx.try_recv() {
            match cmd {
                PhysicsCommand::Stop => return,

                PhysicsCommand::AddObject(obj) => {
                    world.add_object(obj);
                }
                PhysicsCommand::RemoveObject { entity_id } => {
                    world.remove_object(entity_id);
                }
                PhysicsCommand::UpdateKinematic { entity_id, position, rotation } => {
                    world.update_kinematic(entity_id, position, rotation);
                }
                PhysicsCommand::SetGravity { gravity } => {
                    world.gravity = gravity;
                }
                PhysicsCommand::ApplyForce { entity_id, force } => {
                    if let Some(obj) = world.objects.get_mut(&entity_id) {
                        if let Some(rb) = &mut obj.rigidbody {
                            rb.add_force(force);
                        }
                    }
                }
                PhysicsCommand::ApplyTorque { entity_id, torque } => {
                    if let Some(obj) = world.objects.get_mut(&entity_id) {
                        if let Some(rb) = &mut obj.rigidbody {
                            rb.add_torque(torque);
                        }
                    }
                }
                PhysicsCommand::ApplyImpulse { entity_id, impulse } => {
                    if let Some(obj) = world.objects.get_mut(&entity_id) {
                        if let Some(rb) = &mut obj.rigidbody {
                            rb.linear_velocity = rb.linear_velocity + impulse * rb.inv_mass;
                        }
                    }
                }
            }
        }

        // ── 物理ステップ実行 ──────────────────────────────
        world.step(PHYSICS_FIXED_STEP as f32);

        // ── 結果を送信 ──────────────────────────────────
        let result = world.collect_results();
        if res_tx.send(result).is_err() {
            return;
        }

        // ── 次ステップまで睡眠 ──────────────────────────
        let now = Instant::now();
        if next_step > now {
            thread::sleep(next_step - now);
        }
        next_step += step_dur;
    }
}
