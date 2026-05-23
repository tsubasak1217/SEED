// ============================================================
//  solver.rs — Impulse ベース衝突応答ソルバー
//
//  【アルゴリズム】
//    1. 相対速度を接触点で計算
//    2. 法線方向 Impulse を Baumgarte バイアス付きで計算
//    3. 摩擦 Impulse を Coulomb 錐でクランプして計算
//    4. 両 Rigidbody に速度変化を適用
//    5. 位置補正（Baumgarte 安定化）
//
//  is_kinematic / static (inv_mass=0) のボディは速度更新をスキップする。
// ============================================================

use crate::math::{Vector3, Mat3x3, Quaternion};
use crate::narrowphase::ContactManifold;
use crate::rigidbody::RigidBodyState;

/// Baumgarte 安定化係数。貫通深度のうち 1 ステップで補正する割合。
const BAUMGARTE: f32 = 0.2;

/// 位置補正を開始する最小貫通深度（スリーピング防止のための不感帯）。
const SLOP: f32 = 0.01;

/// 摩擦係数のデフォルト値。
const DEFAULT_FRICTION: f32 = 0.5;

/// 1 接触マニフォールドの衝突応答を解く。
///
/// `rb_a` / `rb_b` のどちらかが None（Static）でも動作する。
#[allow(clippy::too_many_arguments)]
pub fn solve_contact(
    manifold: &ContactManifold,
    mut rb_a: Option<&mut RigidBodyState>,
    pos_a: &mut Vector3<f32>, rot_a: Quaternion,
    mut rb_b: Option<&mut RigidBodyState>,
    pos_b: &mut Vector3<f32>, rot_b: Quaternion,
    dt:   f32,
) {
    for contact in &manifold.contacts {
        let n = Vector3::new(contact.normal[0], contact.normal[1], contact.normal[2]);
        let pt = Vector3::new(contact.point[0], contact.point[1], contact.point[2]);

        // 重心から接触点へのベクトル
        let ra  = pt - *pos_a;
        let r_b = pt - *pos_b;

        // ── 相対速度の計算 ─────────────────────────────────
        let va = rb_a.as_ref().map_or(Vector3::zero(), |rb| {
            if rb.is_kinematic { Vector3::zero() }
            else { rb.linear_velocity + rb.angular_velocity.cross(ra) }
        });
        let vb = rb_b.as_ref().map_or(Vector3::zero(), |rb| {
            if rb.is_kinematic { Vector3::zero() }
            else { rb.linear_velocity + rb.angular_velocity.cross(r_b) }
        });
        let rel_vel = vb - va;
        let vn = rel_vel.dot(n);

        // 分離方向ならスキップ
        if vn > 0.0 { continue; }

        // 反発係数（両者の最小値）
        let e = rb_a.as_ref().map_or(0.0, |rb| rb.restitution)
            .min(rb_b.as_ref().map_or(0.0, |rb| rb.restitution));

        // ── 法線 Impulse ─────────────────────────────────────
        let inv_mass_a = rb_a.as_ref().map_or(0.0, |rb| if rb.is_kinematic { 0.0 } else { rb.inv_mass });
        let inv_mass_b = rb_b.as_ref().map_or(0.0, |rb| if rb.is_kinematic { 0.0 } else { rb.inv_mass });

        let i_a = rb_a.as_ref().map_or(
            Mat3x3::<f32>::zero(),
            |rb| if rb.is_kinematic { Mat3x3::<f32>::zero() }
                 else { rb.world_inv_inertia(rot_a) },
        );
        let i_b = rb_b.as_ref().map_or(
            Mat3x3::<f32>::zero(),
            |rb| if rb.is_kinematic { Mat3x3::<f32>::zero() }
                 else { rb.world_inv_inertia(rot_b) },
        );

        let ra_cross_n = ra.cross(n);
        let rb_cross_n = r_b.cross(n);
        let denom = inv_mass_a + inv_mass_b
            + ra_cross_n.dot(i_a * ra_cross_n)
            + rb_cross_n.dot(i_b * rb_cross_n);
        if denom < 1e-10 { continue; }

        // Baumgarte バイアス（貫通補正）
        let bias = BAUMGARTE / dt * (contact.depth - SLOP).max(0.0);
        let j_n = (-(1.0 + e) * vn + bias) / denom;
        let j_n = j_n.max(0.0);

        let impulse_n = n * j_n;
        if let Some(rb) = rb_a.as_mut() {
            if !rb.is_kinematic && rb.inv_mass > 0.0 {
                rb.linear_velocity  = rb.linear_velocity  - impulse_n * rb.inv_mass;
                rb.angular_velocity = rb.angular_velocity - i_a * ra.cross(impulse_n);
                apply_freeze(rb);
            }
        }
        if let Some(rb) = rb_b.as_mut() {
            if !rb.is_kinematic && rb.inv_mass > 0.0 {
                rb.linear_velocity  = rb.linear_velocity  + impulse_n * rb.inv_mass;
                rb.angular_velocity = rb.angular_velocity + i_b * r_b.cross(impulse_n);
                apply_freeze(rb);
            }
        }

        // ── 摩擦 Impulse ─────────────────────────────────────
        let rel_vel2 = {
            let va2 = rb_a.as_ref().map_or(Vector3::zero(), |rb| {
                if rb.is_kinematic { Vector3::zero() }
                else { rb.linear_velocity + rb.angular_velocity.cross(ra) }
            });
            let vb2 = rb_b.as_ref().map_or(Vector3::zero(), |rb| {
                if rb.is_kinematic { Vector3::zero() }
                else { rb.linear_velocity + rb.angular_velocity.cross(r_b) }
            });
            vb2 - va2
        };
        let vt = rel_vel2 - n * rel_vel2.dot(n);
        let vt_len = vt.length();
        if vt_len < 1e-7 { continue; }

        let tangent = vt / vt_len;
        let ra_cross_t = ra.cross(tangent);
        let rb_cross_t = r_b.cross(tangent);
        let denom_t = inv_mass_a + inv_mass_b
            + ra_cross_t.dot(i_a * ra_cross_t)
            + rb_cross_t.dot(i_b * rb_cross_t);
        if denom_t < 1e-10 { continue; }

        let mu = rb_a.as_ref().map_or(DEFAULT_FRICTION, |rb| rb.friction)
            * rb_b.as_ref().map_or(DEFAULT_FRICTION, |rb| rb.friction);
        let mu = mu.sqrt();

        // Coulomb 摩擦: |jt| ≤ μ * jn
        let j_t = (-rel_vel2.dot(tangent) / denom_t).clamp(-mu * j_n, mu * j_n);
        let impulse_t = tangent * j_t;

        if let Some(rb) = rb_a.as_mut() {
            if !rb.is_kinematic && rb.inv_mass > 0.0 {
                rb.linear_velocity  = rb.linear_velocity  - impulse_t * rb.inv_mass;
                rb.angular_velocity = rb.angular_velocity - i_a * ra.cross(impulse_t);
                apply_freeze(rb);
            }
        }
        if let Some(rb) = rb_b.as_mut() {
            if !rb.is_kinematic && rb.inv_mass > 0.0 {
                rb.linear_velocity  = rb.linear_velocity  + impulse_t * rb.inv_mass;
                rb.angular_velocity = rb.angular_velocity + i_b * r_b.cross(impulse_t);
                apply_freeze(rb);
            }
        }

        // ── 位置補正（貫通解消）─────────────────────────────
        let correction = contact.depth.max(0.0) * BAUMGARTE;
        if correction > SLOP {
            let total_inv = inv_mass_a + inv_mass_b;
            if total_inv > 1e-10 {
                let ca = correction * inv_mass_a / total_inv;
                let cb = correction * inv_mass_b / total_inv;
                if rb_a.as_ref().map_or(false, |rb| !rb.is_kinematic && rb.inv_mass > 0.0) {
                    *pos_a = *pos_a - n * ca;
                }
                if rb_b.as_ref().map_or(false, |rb| !rb.is_kinematic && rb.inv_mass > 0.0) {
                    *pos_b = *pos_b + n * cb;
                }
            }
        }
    }
}

/// freeze_position / freeze_rotation に従い速度成分をゼロに固定する。
fn apply_freeze(rb: &mut RigidBodyState) {
    let [fx, fy, fz] = rb.freeze_position;
    if fx { rb.linear_velocity.x = 0.0; }
    if fy { rb.linear_velocity.y = 0.0; }
    if fz { rb.linear_velocity.z = 0.0; }

    let [rx, ry, rz] = rb.freeze_rotation;
    if rx { rb.angular_velocity.x = 0.0; }
    if ry { rb.angular_velocity.y = 0.0; }
    if rz { rb.angular_velocity.z = 0.0; }
}
