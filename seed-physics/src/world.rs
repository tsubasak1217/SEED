// ============================================================
//  world.rs — 物理ワールド（PhysicsWorld）
//
//  PhysicsWorld は物理オブジェクトのコレクションを管理し、
//  固定ステップで以下の処理を実行する:
//
//    1. 重力・力の積分  → 速度更新
//    2. 速度の積分      → 位置・回転の更新
//    3. ブロードフェーズ → 衝突候補ペアの絞り込み
//    4. ナローフェーズ  → 接触マニフォールドの計算
//    5. ソルバー        → 衝突応答（Impulse 法）
//    6. イベント生成    → Enter / Stay / Exit 判定
//    7. 力リセット・睡眠チェック
// ============================================================

use std::collections::{HashMap, HashSet};

use crate::math::{Vector3, Quaternion, Aabb};
use crate::collider::ColliderShape;
use crate::rigidbody::RigidBodyState;
use crate::events::{CollisionEvent, CollisionPhase, TriggerEvent, TriggerPhase};
use crate::thread::PhysicsResult;
use crate::query::{PhysicsQuery, RaycastHit};
use crate::{broadphase, narrowphase, solver};

/// デフォルト重力加速度（m/s²）。
pub const DEFAULT_GRAVITY: Vector3<f32> = Vector3 { x: 0.0, y: -9.81, z: 0.0 };

/// 睡眠判定: この速度以下が SLEEP_TIME_THRESHOLD 秒続いたら睡眠。
const SLEEP_LINEAR_THRESHOLD:  f32 = 0.02;
const SLEEP_ANGULAR_THRESHOLD: f32 = 0.05;
const SLEEP_TIME_THRESHOLD:    f32 = 0.5;

/// 速度減衰の最大値（不安定防止）。
const MAX_VELOCITY: f32 = 200.0;

// ─── PhysicsObject ───────────────────────────────────────────────────────────

/// 物理ワールド内の 1 オブジェクト。ECS の Actor エンティティに 1 対 1 で対応する。
#[derive(Debug, Clone)]
pub struct PhysicsObject {
    /// 対応するアクターの DFS ID（IPC 通信での識別子）
    pub entity_id:       u64,
    /// ワールド位置（コライダーオフセット適用前）
    pub position:        Vector3<f32>,
    /// ワールド回転
    pub rotation:        Quaternion,
    /// スケール
    pub scale:           Vector3<f32>,
    /// コライダー形状
    pub collider:        ColliderShape,
    /// コライダーの Transform からのオフセット（ローカル空間）
    pub collider_offset: Vector3<f32>,
    /// Rigidbody 状態（None = Static）
    pub rigidbody:       Option<RigidBodyState>,
    /// true なら衝突イベントのみ発火・物理応答なし
    pub is_trigger:      bool,
    /// 所属物理レイヤービット
    pub physics_layer:   u32,
    /// 衝突するレイヤーマスク（0 = 全レイヤーと衝突）
    pub layer_mask:      u32,
}

impl PhysicsObject {
    /// コライダーの実効ワールド位置を返す（offset 適用済み）。
    pub fn collider_world_pos(&self) -> Vector3<f32> {
        use crate::collider::rotate_vec3;
        self.position + rotate_vec3(self.rotation, self.collider_offset)
    }

    /// ワールド空間での AABB を返す。
    pub fn world_aabb(&self) -> Aabb {
        let pos = self.collider_world_pos();
        self.collider.compute_aabb(pos, self.rotation, self.scale)
    }
}

// ─── PhysicsWorld ────────────────────────────────────────────────────────────

/// 物理ワールド。物理スレッド上で動作する。
pub struct PhysicsWorld {
    /// 全物理オブジェクト（entity_id をキーとする）
    pub objects: HashMap<u64, PhysicsObject>,
    /// 重力加速度
    pub gravity: Vector3<f32>,
    /// 前ステップに衝突していたペア（Enter/Stay/Exit 判定用）
    prev_collision_pairs: HashSet<(u64, u64)>,
    /// 前ステップにトリガー重なりがあったペア
    prev_trigger_pairs:   HashSet<(u64, u64)>,
    /// 今ステップの衝突イベントバッファ
    collision_events: Vec<CollisionEvent>,
    /// 今ステップのトリガーイベントバッファ
    trigger_events:   Vec<TriggerEvent>,
}

impl PhysicsWorld {
    pub fn new() -> Self {
        Self {
            objects:              HashMap::new(),
            gravity:              DEFAULT_GRAVITY,
            prev_collision_pairs: HashSet::new(),
            prev_trigger_pairs:   HashSet::new(),
            collision_events:     Vec::new(),
            trigger_events:       Vec::new(),
        }
    }

    // ─── オブジェクト管理 ──────────────────────────────────────

    pub fn add_object(&mut self, obj: PhysicsObject) {
        self.objects.insert(obj.entity_id, obj);
    }

    pub fn remove_object(&mut self, entity_id: u64) {
        self.objects.remove(&entity_id);
        self.prev_collision_pairs.retain(|&(a, b)| a != entity_id && b != entity_id);
        self.prev_trigger_pairs.retain(|&(a, b)| a != entity_id && b != entity_id);
    }

    /// Kinematic オブジェクトの Transform を外部から更新する。
    pub fn update_kinematic(&mut self, entity_id: u64, position: Vector3<f32>, rotation: Quaternion) {
        if let Some(obj) = self.objects.get_mut(&entity_id) {
            let is_kinematic = obj.rigidbody.as_ref().map_or(false, |rb| rb.is_kinematic);
            if is_kinematic || obj.rigidbody.is_none() {
                obj.position = position;
                obj.rotation = rotation;
            }
        }
    }

    // ─── メインステップ ───────────────────────────────────────

    /// 物理ステップを 1 回実行する（固定 dt 秒）。
    pub fn step(&mut self, dt: f32) {
        self.collision_events.clear();
        self.trigger_events.clear();

        // ── 1. 重力・力 → 速度積分 ────────────────────────
        for obj in self.objects.values_mut() {
            let Some(rb) = &mut obj.rigidbody else { continue };
            if rb.is_kinematic || rb.is_sleeping || rb.inv_mass <= 0.0 { continue; }

            let gravity_force = self.gravity * (rb.gravity_scale / rb.inv_mass);
            rb.force_accum = rb.force_accum + gravity_force;

            rb.linear_velocity  = rb.linear_velocity  + rb.force_accum  * (rb.inv_mass * dt);
            rb.angular_velocity = rb.angular_velocity + rb.torque_accum * dt;

            rb.linear_velocity  = rb.linear_velocity  * (1.0 - rb.linear_damping).max(0.0);
            rb.angular_velocity = rb.angular_velocity * (1.0 - rb.angular_damping).max(0.0);

            clamp_velocity(&mut rb.linear_velocity,  MAX_VELOCITY);
            clamp_velocity(&mut rb.angular_velocity, MAX_VELOCITY);
        }

        // ── 2. 速度 → 位置積分 ────────────────────────────
        for obj in self.objects.values_mut() {
            let Some(rb) = &mut obj.rigidbody else { continue };
            if rb.is_kinematic || rb.is_sleeping || rb.inv_mass <= 0.0 { continue; }

            let [fx, fy, fz] = rb.freeze_position;
            if !fx { obj.position.x += rb.linear_velocity.x * dt; }
            if !fy { obj.position.y += rb.linear_velocity.y * dt; }
            if !fz { obj.position.z += rb.linear_velocity.z * dt; }

            let [rx, ry, rz] = rb.freeze_rotation;
            let av = rb.angular_velocity;
            let av_frozen = Vector3::new(
                if rx { 0.0 } else { av.x },
                if ry { 0.0 } else { av.y },
                if rz { 0.0 } else { av.z },
            );
            obj.rotation = integrate_rotation(obj.rotation, av_frozen, dt);
        }

        // ── 3. ブロードフェーズ ───────────────────────────
        let aabb_list: Vec<(u64, _)> = self.objects.values()
            .map(|obj| (obj.entity_id, obj.world_aabb()))
            .collect();
        let candidate_pairs = broadphase::find_candidate_pairs(&aabb_list);

        // ── 4 & 5. ナローフェーズ + ソルバー ─────────────
        let mut cur_collision_pairs: HashSet<(u64, u64)> = HashSet::new();
        let mut cur_trigger_pairs:   HashSet<(u64, u64)> = HashSet::new();

        let contacts: Vec<_> = candidate_pairs.iter().filter_map(|&(id_a, id_b)| {
            let obj_a = self.objects.get(&id_a)?;
            let obj_b = self.objects.get(&id_b)?;

            if !layers_collide(obj_a, obj_b) { return None; }

            let pos_a = obj_a.collider_world_pos();
            let pos_b = obj_b.collider_world_pos();

            narrowphase::compute_contacts(
                &obj_a.collider, pos_a, obj_a.rotation, obj_a.scale,
                &obj_b.collider, pos_b, obj_b.rotation, obj_b.scale,
            ).map(|m| (id_a, id_b, m, obj_a.is_trigger || obj_b.is_trigger))
        }).collect();

        for (id_a, id_b, manifold, is_trigger_pair) in contacts {
            let pair = (id_a.min(id_b), id_a.max(id_b));

            if is_trigger_pair {
                cur_trigger_pairs.insert(pair);
            } else {
                cur_collision_pairs.insert(pair);

                let (rot_a, rot_b) = {
                    let oa = self.objects.get(&id_a).unwrap();
                    let ob = self.objects.get(&id_b).unwrap();
                    (oa.rotation, ob.rotation)
                };

                let (mut pos_a, mut pos_b, mut rb_a_opt, mut rb_b_opt) = {
                    let oa = self.objects.get(&id_a).unwrap();
                    let ob = self.objects.get(&id_b).unwrap();
                    (oa.position, ob.position, oa.rigidbody.clone(), ob.rigidbody.clone())
                };

                solver::solve_contact(
                    &manifold,
                    rb_a_opt.as_mut(), &mut pos_a, rot_a,
                    rb_b_opt.as_mut(), &mut pos_b, rot_b,
                    dt,
                );

                if let Some(obj) = self.objects.get_mut(&id_a) {
                    obj.position = pos_a;
                    obj.rigidbody = rb_a_opt;
                }
                if let Some(obj) = self.objects.get_mut(&id_b) {
                    obj.position = pos_b;
                    obj.rigidbody = rb_b_opt;
                }
            }
        }

        // ── 6. イベント生成 ──────────────────────────────
        for &pair in &cur_collision_pairs {
            let phase = if self.prev_collision_pairs.contains(&pair) {
                CollisionPhase::Stay
            } else {
                CollisionPhase::Enter
            };
            self.collision_events.push(CollisionEvent {
                entity_a: pair.0,
                entity_b: pair.1,
                contacts: Vec::new(),
                phase,
            });
        }
        for &pair in &self.prev_collision_pairs {
            if !cur_collision_pairs.contains(&pair) {
                self.collision_events.push(CollisionEvent {
                    entity_a: pair.0,
                    entity_b: pair.1,
                    contacts: Vec::new(),
                    phase:    CollisionPhase::Exit,
                });
            }
        }

        for &pair in &cur_trigger_pairs {
            if !self.prev_trigger_pairs.contains(&pair) {
                self.trigger_events.push(TriggerEvent {
                    trigger_entity: pair.0,
                    other_entity:   pair.1,
                    phase: TriggerPhase::Enter,
                });
            }
        }
        for &pair in &self.prev_trigger_pairs {
            if !cur_trigger_pairs.contains(&pair) {
                self.trigger_events.push(TriggerEvent {
                    trigger_entity: pair.0,
                    other_entity:   pair.1,
                    phase: TriggerPhase::Exit,
                });
            }
        }

        self.prev_collision_pairs = cur_collision_pairs;
        self.prev_trigger_pairs   = cur_trigger_pairs;

        // ── 7. 力リセット・睡眠チェック ──────────────────
        for obj in self.objects.values_mut() {
            let Some(rb) = &mut obj.rigidbody else { continue };
            rb.clear_forces();
            update_sleep_state(rb, dt);
        }
    }

    // ─── 結果収集 ────────────────────────────────────────────

    /// 現ステップの結果を PhysicsResult にまとめて返す。
    pub fn collect_results(&self) -> PhysicsResult {
        let transform_updates: Vec<_> = self.objects.values()
            .filter(|obj| {
                obj.rigidbody.as_ref().map_or(false, |rb| {
                    !rb.is_kinematic && rb.inv_mass > 0.0 && !rb.is_sleeping
                })
            })
            .map(|obj| (obj.entity_id, obj.position, obj.rotation))
            .collect();

        PhysicsResult {
            transform_updates,
            collision_events: self.collision_events.clone(),
            trigger_events:   self.trigger_events.clone(),
        }
    }

    // ─── クエリ ───────────────────────────────────────────────

    /// レイキャストを実行する。
    pub fn raycast(
        &self,
        origin:     Vector3<f32>,
        direction:  Vector3<f32>,
        max_dist:   f32,
        layer_mask: u32,
    ) -> Option<RaycastHit> {
        let objs: Vec<_> = self.objects.values()
            .map(|obj| (
                obj.entity_id,
                obj.collider_world_pos(),
                obj.rotation,
                obj.scale,
                &obj.collider,
                obj.physics_layer,
            ))
            .collect();
        PhysicsQuery::raycast(origin, direction, max_dist, layer_mask, &objs)
    }

    /// 球オーバーラップクエリを実行する。
    pub fn overlap_sphere(
        &self,
        center:     Vector3<f32>,
        radius:     f32,
        layer_mask: u32,
    ) -> Vec<u64> {
        let objs: Vec<_> = self.objects.values()
            .map(|obj| (
                obj.entity_id,
                obj.collider_world_pos(),
                obj.rotation,
                obj.scale,
                &obj.collider,
                obj.physics_layer,
            ))
            .collect();
        PhysicsQuery::overlap_sphere(center, radius, layer_mask, &objs)
    }
}

impl Default for PhysicsWorld {
    fn default() -> Self {
        Self::new()
    }
}

// ─── 内部ユーティリティ ──────────────────────────────────────────────────────

/// レイヤーマスクで 2 オブジェクトが衝突するか判定する。
fn layers_collide(a: &PhysicsObject, b: &PhysicsObject) -> bool {
    let a_hits_b = a.layer_mask == 0 || (a.layer_mask & b.physics_layer) != 0;
    let b_hits_a = b.layer_mask == 0 || (b.layer_mask & a.physics_layer) != 0;
    a_hits_b && b_hits_a
}

/// クォータニオンに角速度を積分する（1 次近似）。
fn integrate_rotation(q: Quaternion, omega: Vector3<f32>, dt: f32) -> Quaternion {
    let half_dt = dt * 0.5;
    let dq = Quaternion::new(
        omega.x * half_dt,
        omega.y * half_dt,
        omega.z * half_dt,
        0.0,
    );
    let qw = dq.w * q.w - dq.x * q.x - dq.y * q.y - dq.z * q.z;
    let qx = dq.w * q.x + dq.x * q.w + dq.y * q.z - dq.z * q.y;
    let qy = dq.w * q.y - dq.x * q.z + dq.y * q.w + dq.z * q.x;
    let qz = dq.w * q.z + dq.x * q.y - dq.y * q.x + dq.z * q.w;
    Quaternion::new(qx, qy, qz, qw).normalize()
}

/// ベクトルの長さを max_len でクランプする。
fn clamp_velocity(v: &mut Vector3<f32>, max_len: f32) {
    let len2 = v.length_sq();
    if len2 > max_len * max_len {
        let len = len2.sqrt();
        *v = *v * (max_len / len);
    }
}

/// 睡眠状態を更新する。静止が SLEEP_TIME_THRESHOLD 秒続いたら睡眠へ。
fn update_sleep_state(rb: &mut RigidBodyState, dt: f32) {
    if rb.is_kinematic { return; }

    let v_len  = rb.linear_velocity.length();
    let av_len = rb.angular_velocity.length();

    if v_len < SLEEP_LINEAR_THRESHOLD && av_len < SLEEP_ANGULAR_THRESHOLD {
        rb.sleep_timer += dt;
        if rb.sleep_timer >= SLEEP_TIME_THRESHOLD {
            rb.is_sleeping = true;
        }
    } else {
        rb.sleep_timer = 0.0;
        rb.is_sleeping = false;
    }
}
