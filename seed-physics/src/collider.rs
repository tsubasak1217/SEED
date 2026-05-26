// ============================================================
//  collider.rs — コライダー形状定義と AABB 計算
//
//  【ポリゴン制限】
//    ConvexHull  : 頂点数  ≤ CONVEX_HULL_MAX_VERTICES  (64)
//    TriangleMesh: 三角形数 ≤ TRIANGLE_MESH_MAX_TRIANGLES (1024)
//
//  【Dynamic 使用可否】
//    Box / Sphere / Capsule / ConvexHull : Dynamic ○
//    TriangleMesh                        : Static 専用 ×
// ============================================================

use crate::math::{Vector3, Quaternion, Aabb};

/// ConvexHull コライダーの最大頂点数。
pub const CONVEX_HULL_MAX_VERTICES: usize = 64;

/// TriangleMesh コライダーの最大三角形数。
pub const TRIANGLE_MESH_MAX_TRIANGLES: usize = 1024;

// ─── ColliderShape ───────────────────────────────────────────────────────────

/// コライダー形状列挙。
#[derive(Debug, Clone)]
pub enum ColliderShape {
    /// 軸平行ボックス（ローカル空間の半サイズ）
    Box { half_extents: Vector3<f32> },
    /// 球
    Sphere { radius: f32 },
    /// カプセル（ローカル Y 軸方向、中心から上下に half_height 延伸 + 半径 radius）
    Capsule { radius: f32, half_height: f32 },
    /// シリンダー（ローカル Y 軸方向、半径 radius、高さ 2*half_height）
    Cylinder { radius: f32, half_height: f32 },
    /// コーン（ローカル Y 軸方向、半径 radius、高さ 2*half_height）
    Cone { radius: f32, half_height: f32 },
    /// 凸包（頂点数 ≤ CONVEX_HULL_MAX_VERTICES）
    ConvexHull { vertices: Vec<Vector3<f32>> },
    /// 三角形メッシュ（三角形数 ≤ TRIANGLE_MESH_MAX_TRIANGLES）。Static 専用。
    TriangleMesh { triangles: Vec<[Vector3<f32>; 3]> },
}

impl ColliderShape {
    // ─── ファクトリ ─────────────────────────────────────────────

    /// ConvexHull を生成する。頂点数超過時は切り詰める。
    pub fn new_convex_hull(vertices: Vec<Vector3<f32>>) -> Self {
        let verts = if vertices.len() > CONVEX_HULL_MAX_VERTICES {
            eprintln!(
                "[Physics] ConvexHull: 頂点数 {} > 上限 {}。切り詰めます。",
                vertices.len(), CONVEX_HULL_MAX_VERTICES
            );
            vertices[..CONVEX_HULL_MAX_VERTICES].to_vec()
        } else {
            vertices
        };
        Self::ConvexHull { vertices: verts }
    }

    /// TriangleMesh を生成する。三角形数超過時は切り詰める。
    pub fn new_triangle_mesh(triangles: Vec<[Vector3<f32>; 3]>) -> Self {
        let tris = if triangles.len() > TRIANGLE_MESH_MAX_TRIANGLES {
            eprintln!(
                "[Physics] TriangleMesh: 三角形数 {} > 上限 {}。切り詰めます。",
                triangles.len(), TRIANGLE_MESH_MAX_TRIANGLES
            );
            triangles[..TRIANGLE_MESH_MAX_TRIANGLES].to_vec()
        } else {
            triangles
        };
        Self::TriangleMesh { triangles: tris }
    }

    // ─── プロパティ ─────────────────────────────────────────────

    /// Dynamic Rigidbody として使用できる形状か判定する。
    /// TriangleMesh は凸でないため慣性テンソルの計算が困難。Static 専用。
    pub fn supports_dynamic(&self) -> bool {
        !matches!(self, Self::TriangleMesh { .. })
    }

    // ─── AABB 計算 ──────────────────────────────────────────────

    /// ワールド空間での AABB を計算する。
    ///
    /// - `position` : コライダーオフセット適用後のワールド位置
    /// - `rotation` : ワールド回転
    /// - `scale`    : スケール（コライダー半サイズに乗算）
    pub fn compute_aabb(
        &self,
        position: Vector3<f32>,
        rotation: Quaternion,
        scale:    Vector3<f32>,
    ) -> Aabb {
        match self {
            Self::Box { half_extents } => {
                let he = Vector3::new(
                    half_extents.x * scale.x.abs(),
                    half_extents.y * scale.y.abs(),
                    half_extents.z * scale.z.abs(),
                );
                aabb_from_obb(position, rotation, he)
            }

            Self::Sphere { radius } => {
                let r = radius * scale.x.abs().max(scale.y.abs()).max(scale.z.abs());
                let r3 = Vector3::new(r, r, r);
                Aabb::new(position - r3, position + r3)
            }

            Self::Capsule { radius, half_height } => {
                let r  = radius * scale.x.abs().max(scale.z.abs());
                let hh = half_height * scale.y.abs();
                // カプセルの両端球中心（ローカル Y 軸方向を回転）
                let up = rotate_vec3(rotation, Vector3::new(0.0, hh, 0.0));
                let p1 = position + up;
                let p2 = position - up;
                let r3 = Vector3::new(r, r, r);
                let mn = Vector3::new(
                    p1.x.min(p2.x), p1.y.min(p2.y), p1.z.min(p2.z),
                ) - r3;
                let mx = Vector3::new(
                    p1.x.max(p2.x), p1.y.max(p2.y), p1.z.max(p2.z),
                ) + r3;
                Aabb::new(mn, mx)
            }

            Self::Cylinder { radius, half_height } | Self::Cone { radius, half_height } => {
                let r  = radius * scale.x.abs().max(scale.z.abs());
                let hh = half_height * scale.y.abs();
                // AABB はシリンダー/コーンを囲むボックスとして計算
                // Y軸の範囲: [-hh, hh]
                // XY/ZY平面の範囲: [-r, r]
                let extent = Vector3::new(r, hh, r);
                aabb_from_obb(position, rotation, extent)
            }

            Self::ConvexHull { vertices } => {
                if vertices.is_empty() {
                    return Aabb::new(position, position);
                }
                let mut mn = Vector3::new(f32::MAX, f32::MAX, f32::MAX);
                let mut mx = Vector3::new(-f32::MAX, -f32::MAX, -f32::MAX);
                for &v in vertices {
                    let sv = Vector3::new(v.x * scale.x, v.y * scale.y, v.z * scale.z);
                    let wv = position + rotate_vec3(rotation, sv);
                    mn.x = mn.x.min(wv.x);
                    mn.y = mn.y.min(wv.y);
                    mn.z = mn.z.min(wv.z);
                    mx.x = mx.x.max(wv.x);
                    mx.y = mx.y.max(wv.y);
                    mx.z = mx.z.max(wv.z);
                }
                Aabb::new(mn, mx)
            }

            Self::TriangleMesh { triangles } => {
                if triangles.is_empty() {
                    return Aabb::new(position, position);
                }
                let mut mn = Vector3::new(f32::MAX, f32::MAX, f32::MAX);
                let mut mx = Vector3::new(-f32::MAX, -f32::MAX, -f32::MAX);
                for tri in triangles {
                    for &v in tri.iter() {
                        let sv = Vector3::new(v.x * scale.x, v.y * scale.y, v.z * scale.z);
                        let wv = position + rotate_vec3(rotation, sv);
                        mn.x = mn.x.min(wv.x);
                        mn.y = mn.y.min(wv.y);
                        mn.z = mn.z.min(wv.z);
                        mx.x = mx.x.max(wv.x);
                        mx.y = mx.y.max(wv.y);
                        mx.z = mx.z.max(wv.z);
                    }
                }
                Aabb::new(mn, mx)
            }
        }
    }

    /// ConvexHull / TriangleMesh のサポート関数（GJK に使用）。
    pub fn support_world(
        &self,
        position: Vector3<f32>,
        rotation: Quaternion,
        scale:    Vector3<f32>,
        dir:      Vector3<f32>,
    ) -> Vector3<f32> {
        match self {
            Self::Box { half_extents } => {
                let he = Vector3::new(
                    half_extents.x * scale.x.abs(),
                    half_extents.y * scale.y.abs(),
                    half_extents.z * scale.z.abs(),
                );
                let local_dir = rotate_vec3(rotation.conjugate(), dir);
                let lp = Vector3::new(
                    if local_dir.x >= 0.0 { he.x } else { -he.x },
                    if local_dir.y >= 0.0 { he.y } else { -he.y },
                    if local_dir.z >= 0.0 { he.z } else { -he.z },
                );
                position + rotate_vec3(rotation, lp)
            }
            Self::Sphere { radius } => {
                let r = radius * scale.x.abs().max(scale.y.abs()).max(scale.z.abs());
                position + dir.normalize() * r
            }
            Self::Capsule { radius, half_height } => {
                let r  = radius * scale.x.abs().max(scale.z.abs());
                let hh = half_height * scale.y.abs();
                let up = rotate_vec3(rotation, Vector3::new(0.0, hh, 0.0));
                let tip = if dir.dot(up) >= 0.0 { position + up } else { position - -up };
                tip + dir.normalize() * r
            }
            Self::Cylinder { radius, half_height } => {
                let r  = radius * scale.x.abs().max(scale.z.abs());
                let hh = half_height * scale.y.abs();
                let local_dir = rotate_vec3(rotation.conjugate(), dir);
                // 円柱のサポート点
                let l_y = if local_dir.y >= 0.0 { hh } else { -hh };
                let l_xz = if (local_dir.x * local_dir.x + local_dir.z * local_dir.z) > 1e-6 {
                     let dir_xz = Vector3::new(local_dir.x, 0.0, local_dir.z).normalize();
                     dir_xz * r
                } else {
                    Vector3::zero()
                };
                let lp = Vector3::new(l_xz.x, l_y, l_xz.z);
                position + rotate_vec3(rotation, lp)
            }
            Self::Cone { radius, half_height } => {
                let r  = radius * scale.x.abs().max(scale.z.abs());
                let hh = half_height * scale.y.abs();
                let local_dir = rotate_vec3(rotation.conjugate(), dir);
                // コーンのサポート点：頂点 (0, hh, 0) か、底面周縁か
                let dot_v = local_dir.y * hh;
                let dot_b = -local_dir.y * hh + (local_dir.x * 0.0 + local_dir.z * 0.0) + r * (local_dir.x * local_dir.x + local_dir.z * local_dir.z).sqrt();
                // 実際はコーンの頂点 (0, hh, 0) または底面エッジ
                let lp = if dot_v > dot_b {
                    Vector3::new(0.0, hh, 0.0)
                } else {
                    // 底面周縁
                    let dir_xz = Vector3::new(local_dir.x, 0.0, local_dir.z).normalize();
                    dir_xz * r + Vector3::new(0.0, -hh, 0.0)
                };
                position + rotate_vec3(rotation, lp)
            }
            Self::ConvexHull { vertices } => {
                let local_dir = rotate_vec3(rotation.conjugate(), dir);
                let best = vertices.iter().copied().max_by(|a, b| {
                    let da = a.x * local_dir.x + a.y * local_dir.y + a.z * local_dir.z;
                    let db = b.x * local_dir.x + b.y * local_dir.y + b.z * local_dir.z;
                    da.partial_cmp(&db).unwrap()
                }).unwrap_or(Vector3::zero());
                let scaled = Vector3::new(best.x * scale.x, best.y * scale.y, best.z * scale.z);
                position + rotate_vec3(rotation, scaled)
            }
            Self::TriangleMesh { triangles } => {
                let local_dir = rotate_vec3(rotation.conjugate(), dir);
                let mut best_dot = f32::NEG_INFINITY;
                let mut best_v   = Vector3::zero();
                for tri in triangles {
                    for &v in tri.iter() {
                        let d = v.dot(local_dir);
                        if d > best_dot {
                            best_dot = d;
                            best_v   = v;
                        }
                    }
                }
                let scaled = Vector3::new(best_v.x * scale.x, best_v.y * scale.y, best_v.z * scale.z);
                position + rotate_vec3(rotation, scaled)
            }
        }
    }

}

// ─── 数学ユーティリティ ──────────────────────────────────────────────────────

/// クォータニオンでベクトルを回転させる（Rodrigues の公式）。
///
/// `v' = q * v * q^-1` を展開した形式。q が単位クォータニオンであることを前提とする。
#[inline]
pub fn rotate_vec3(q: Quaternion, v: Vector3<f32>) -> Vector3<f32> {
    let u = Vector3::new(q.x, q.y, q.z);
    let s = q.w;
    // v' = 2(u·v)u + (s²-|u|²)v + 2s(u×v)
    let uv  = u.dot(v);
    let uu  = u.dot(u);
    let ucv = u.cross(v);
    u * (2.0 * uv) + v * (s * s - uu) + ucv * (2.0 * s)
}

/// OBB（位置・回転・半サイズ）から AABB を計算する。
fn aabb_from_obb(
    center:       Vector3<f32>,
    rotation:     Quaternion,
    half_extents: Vector3<f32>,
) -> Aabb {
    // ローカル X/Y/Z 軸をワールド空間に変換
    let ax = rotate_vec3(rotation, Vector3::new(1.0, 0.0, 0.0));
    let ay = rotate_vec3(rotation, Vector3::new(0.0, 1.0, 0.0));
    let az = rotate_vec3(rotation, Vector3::new(0.0, 0.0, 1.0));

    // AABB 半サイズ = Σ |axis_i| * half_extent_i
    let ex = ax.x.abs() * half_extents.x + ay.x.abs() * half_extents.y + az.x.abs() * half_extents.z;
    let ey = ax.y.abs() * half_extents.x + ay.y.abs() * half_extents.y + az.y.abs() * half_extents.z;
    let ez = ax.z.abs() * half_extents.x + ay.z.abs() * half_extents.y + az.z.abs() * half_extents.z;

    let extent = Vector3::new(ex, ey, ez);
    Aabb::new(center - extent, center + extent)
}
