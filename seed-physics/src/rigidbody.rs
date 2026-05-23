// ============================================================
//  rigidbody.rs — Rigidbody 運動状態
//
//  RigidBodyState は物理演算で更新される運動量・姿勢変化量を保持する。
//  位置・回転は PhysicsObject が持ち、RigidBodyState は速度・力を担当する。
//
//  【慣性テンソル】
//    ローカル空間の逆慣性テンソル (inv_inertia_local) を保持し、
//    world_inv_inertia() でワールド空間に変換して使用する:
//      I_world_inv = R * I_local_inv * R^T
// ============================================================

use crate::math::{Vector3, Mat3x3, Quaternion};
use crate::collider::ColliderShape;

// ─── RigidBodyState ──────────────────────────────────────────────────────────

/// Rigidbody の運動状態。
///
/// 物理ステップごとに積分・更新される。
/// is_kinematic=true のときは速度積分を行わず、外部からの位置設定のみ受け付ける。
#[derive(Debug, Clone)]
pub struct RigidBodyState {
    /// 線形速度（ワールド空間、m/s）
    pub linear_velocity:  Vector3<f32>,
    /// 角速度（ワールド空間、rad/s）
    pub angular_velocity: Vector3<f32>,
    /// 蓄積された力（フレーム末尾でクリア）
    pub force_accum:      Vector3<f32>,
    /// 蓄積されたトルク（フレーム末尾でクリア）
    pub torque_accum:     Vector3<f32>,
    /// 質量（kg）。0 以下なら無限大（Static 扱い）
    pub mass:             f32,
    /// 質量の逆数（0 = 無限大質量）
    pub inv_mass:         f32,
    /// ローカル空間の逆慣性テンソル
    pub inv_inertia_local: Mat3x3<f32>,
    /// 反発係数（0 = 完全非弾性, 1 = 完全弾性）
    pub restitution:      f32,
    /// 摩擦係数（0 = 摩擦なし）
    pub friction:         f32,
    /// 線形速度の減衰係数（0 = 減衰なし）
    pub linear_damping:   f32,
    /// 角速度の減衰係数
    pub angular_damping:  f32,
    /// 重力スケール（1.0 = 通常重力、0.0 = 無重力）
    pub gravity_scale:    f32,
    /// true のとき物理による位置更新を行わない（スクリプト直接制御）
    pub is_kinematic:     bool,
    /// 微小運動が続くと true になり、物理演算をスキップして省エネ
    pub is_sleeping:      bool,
    /// 位置のフリーズ（X/Y/Z 軸個別）
    pub freeze_position:  [bool; 3],
    /// 回転のフリーズ（X/Y/Z 軸個別）
    pub freeze_rotation:  [bool; 3],
    /// 睡眠判定用タイマー（静止状態が続いた秒数）
    pub(crate) sleep_timer: f32,
}

impl RigidBodyState {
    /// 質量を指定して生成する。慣性テンソルは別途 set_*_inertia() で設定すること。
    pub fn new(mass: f32) -> Self {
        let inv_mass = if mass > 0.0 { 1.0 / mass } else { 0.0 };
        Self {
            linear_velocity:   Vector3::zero(),
            angular_velocity:  Vector3::zero(),
            force_accum:       Vector3::zero(),
            torque_accum:      Vector3::zero(),
            mass,
            inv_mass,
            inv_inertia_local: Mat3x3::identity(),
            restitution:       0.3,
            friction:          0.5,
            linear_damping:    0.01,
            angular_damping:   0.05,
            gravity_scale:     1.0,
            is_kinematic:      false,
            is_sleeping:       false,
            freeze_position:   [false; 3],
            freeze_rotation:   [false; 3],
            sleep_timer:       0.0,
        }
    }

    // ─── 慣性テンソル設定 ──────────────────────────────────────

    /// Box コライダー用の均一密度慣性テンソルを設定する。
    pub fn set_box_inertia(&mut self, half_extents: Vector3<f32>) {
        if self.inv_mass <= 0.0 { return; }
        let (w, h, d) = (half_extents.x * 2.0, half_extents.y * 2.0, half_extents.z * 2.0);
        let m  = self.mass;
        let ixx = m * (h * h + d * d) / 12.0;
        let iyy = m * (w * w + d * d) / 12.0;
        let izz = m * (w * w + h * h) / 12.0;
        self.inv_inertia_local = Mat3x3::new(
            1.0 / ixx, 0.0, 0.0,
            0.0, 1.0 / iyy, 0.0,
            0.0, 0.0, 1.0 / izz,
        );
    }

    /// Sphere コライダー用の慣性テンソルを設定する（均一密度球: I = (2/5)mr²）。
    pub fn set_sphere_inertia(&mut self, radius: f32) {
        if self.inv_mass <= 0.0 { return; }
        let i = 2.0 * self.mass * radius * radius / 5.0;
        self.inv_inertia_local = Mat3x3::new(
            1.0 / i, 0.0, 0.0,
            0.0, 1.0 / i, 0.0,
            0.0, 0.0, 1.0 / i,
        );
    }

    /// Capsule コライダー用の慣性テンソルを設定する（円柱近似）。
    pub fn set_capsule_inertia(&mut self, radius: f32, half_height: f32) {
        if self.inv_mass <= 0.0 { return; }
        let h = half_height * 2.0;
        let r = radius;
        let m = self.mass;
        // 円柱近似: Ixx = Izz = m(3r²+h²)/12, Iyy = mr²/2
        let ixx = m * (3.0 * r * r + h * h) / 12.0;
        let iyy = m * r * r / 2.0;
        let izz = ixx;
        self.inv_inertia_local = Mat3x3::new(
            1.0 / ixx, 0.0, 0.0,
            0.0, 1.0 / iyy, 0.0,
            0.0, 0.0, 1.0 / izz,
        );
    }

    /// ConvexHull 用の近似慣性テンソルを AABB で計算して設定する。
    pub fn set_convex_hull_inertia_approx(&mut self, vertices: &[Vector3<f32>]) {
        if self.inv_mass <= 0.0 || vertices.is_empty() { return; }
        let mut mn = Vector3::new(f32::MAX, f32::MAX, f32::MAX);
        let mut mx = Vector3::new(-f32::MAX, -f32::MAX, -f32::MAX);
        for &v in vertices {
            mn.x = mn.x.min(v.x); mn.y = mn.y.min(v.y); mn.z = mn.z.min(v.z);
            mx.x = mx.x.max(v.x); mx.y = mx.y.max(v.y); mx.z = mx.z.max(v.z);
        }
        let half = (mx - mn) * 0.5;
        self.set_box_inertia(half);
    }

    /// コライダー形状に合わせて慣性テンソルを自動設定する。
    pub fn auto_set_inertia(&mut self, shape: &ColliderShape) {
        match shape {
            ColliderShape::Box { half_extents }     => self.set_box_inertia(*half_extents),
            ColliderShape::Sphere { radius }         => self.set_sphere_inertia(*radius),
            ColliderShape::Capsule { radius, half_height } =>
                self.set_capsule_inertia(*radius, *half_height),
            ColliderShape::ConvexHull { vertices }   =>
                self.set_convex_hull_inertia_approx(vertices),
            ColliderShape::TriangleMesh { .. }       => {
                // TriangleMesh は Static 専用なので慣性テンソル不要
            }
        }
    }

    // ─── ワールド空間慣性テンソル ──────────────────────────────

    /// ワールド空間での逆慣性テンソルを計算する: `I_world_inv = R * I_local_inv * R^T`
    pub fn world_inv_inertia(&self, rotation: Quaternion) -> Mat3x3<f32> {
        let r  = quat_to_mat3(rotation);
        let rt = r.transpose();
        r * self.inv_inertia_local * rt
    }

    // ─── 力・トルク操作 ──────────────────────────────────────

    /// 重心に力を加える（トルクなし）。
    #[inline]
    pub fn add_force(&mut self, force: Vector3<f32>) {
        self.force_accum = self.force_accum + force;
    }

    /// トルクを加える。
    #[inline]
    pub fn add_torque(&mut self, torque: Vector3<f32>) {
        self.torque_accum = self.torque_accum + torque;
    }

    /// ワールド座標の点 `point` に力を加える（トルクも発生）。
    pub fn add_force_at_point(
        &mut self,
        force:    Vector3<f32>,
        point:    Vector3<f32>,
        position: Vector3<f32>,
    ) {
        self.force_accum  = self.force_accum + force;
        self.torque_accum = self.torque_accum + (point - position).cross(force);
    }

    /// 蓄積された力・トルクをリセットする（物理ステップの末尾で呼ぶ）。
    #[inline]
    pub fn clear_forces(&mut self) {
        self.force_accum  = Vector3::zero();
        self.torque_accum = Vector3::zero();
    }
}

// ─── ユーティリティ ──────────────────────────────────────────────────────────

/// クォータニオンを 3x3 回転行列に変換する。
pub fn quat_to_mat3(q: Quaternion) -> Mat3x3<f32> {
    let q  = q.normalize();
    let (x, y, z, w) = (q.x, q.y, q.z, q.w);
    Mat3x3::new(
        1.0 - 2.0*(y*y + z*z),  2.0*(x*y - w*z),        2.0*(x*z + w*y),
        2.0*(x*y + w*z),        1.0 - 2.0*(x*x + z*z),  2.0*(y*z - w*x),
        2.0*(x*z - w*y),        2.0*(y*z + w*x),        1.0 - 2.0*(x*x + y*y),
    )
}
