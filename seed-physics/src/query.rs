// ============================================================
//  query.rs — 物理クエリ（Raycast / Overlap）
//
//  【提供する機能】
//    raycast()       : 光線と全コライダーの交差判定（最近接ヒット）
//    overlap_sphere(): 球と重なるコライダーを全列挙
//    overlap_box()   : ボックスと重なるコライダーを全列挙
// ============================================================

use crate::math::{Vector3, Quaternion};
use crate::collider::{ColliderShape, rotate_vec3};

// ─── RaycastHit ──────────────────────────────────────────────────────────────

/// レイキャストのヒット情報。
#[derive(Debug, Clone)]
pub struct RaycastHit {
    /// ヒットしたオブジェクトの entity_id
    pub entity_id: u64,
    /// ヒット点のワールド座標
    pub point:     [f32; 3],
    /// ヒット面の法線（正規化済み）
    pub normal:    [f32; 3],
    /// 光線起点からのヒット距離
    pub distance:  f32,
}

// ─── PhysicsQuery ────────────────────────────────────────────────────────────

/// 物理クエリ実行器。PhysicsWorld が保持し、外部から借用して使う。
pub struct PhysicsQuery;

impl PhysicsQuery {
    /// 光線を照射し、最近接ヒットを返す。
    pub fn raycast(
        origin:     Vector3<f32>,
        direction:  Vector3<f32>,
        max_dist:   f32,
        layer_mask: u32,
        objects:    &[(u64, Vector3<f32>, Quaternion, Vector3<f32>, &ColliderShape, u32)],
    ) -> Option<RaycastHit> {
        let dir_len = direction.length();
        if dir_len < 1e-10 { return None; }
        let dir = direction / dir_len;

        let mut best: Option<RaycastHit> = None;

        for &(entity_id, pos, rot, scale, shape, layer) in objects {
            if layer_mask != 0 && (layer & layer_mask) == 0 { continue; }

            if let Some(hit) = ray_vs_shape(origin, dir, max_dist, pos, rot, scale, shape) {
                let keep = best.as_ref().map_or(true, |b| hit.distance < b.distance);
                if keep {
                    best = Some(RaycastHit {
                        entity_id,
                        point:    hit.point,
                        normal:   hit.normal,
                        distance: hit.distance,
                    });
                }
            }
        }
        best
    }

    /// 球と重なる全オブジェクトの entity_id を返す。
    pub fn overlap_sphere(
        center:     Vector3<f32>,
        radius:     f32,
        layer_mask: u32,
        objects:    &[(u64, Vector3<f32>, Quaternion, Vector3<f32>, &ColliderShape, u32)],
    ) -> Vec<u64> {
        objects.iter()
            .filter(|&&(_, pos, rot, scale, shape, layer)| {
                if layer_mask != 0 && (layer & layer_mask) == 0 { return false; }
                sphere_overlaps_shape(center, radius, pos, rot, scale, shape)
            })
            .map(|&(id, ..)| id)
            .collect()
    }

    /// ボックスと重なる全オブジェクトの entity_id を返す。
    pub fn overlap_box(
        center:      Vector3<f32>,
        half_extent: Vector3<f32>,
        rotation:    Quaternion,
        layer_mask:  u32,
        objects:     &[(u64, Vector3<f32>, Quaternion, Vector3<f32>, &ColliderShape, u32)],
    ) -> Vec<u64> {
        objects.iter()
            .filter(|&&(_, pos, rot, scale, shape, layer)| {
                if layer_mask != 0 && (layer & layer_mask) == 0 { return false; }
                box_overlaps_shape(center, half_extent, rotation, pos, rot, scale, shape)
            })
            .map(|&(id, ..)| id)
            .collect()
    }
}

// ─── Raycast 実装 ────────────────────────────────────────────────────────────

struct RawHit { point: [f32; 3], normal: [f32; 3], distance: f32 }

fn ray_vs_shape(
    origin: Vector3<f32>, dir: Vector3<f32>, max_dist: f32,
    pos: Vector3<f32>, rot: Quaternion, scale: Vector3<f32>,
    shape: &ColliderShape,
) -> Option<RawHit> {
    match shape {
        ColliderShape::Box { half_extents } => {
            let he = Vector3::new(
                half_extents.x * scale.x.abs(),
                half_extents.y * scale.y.abs(),
                half_extents.z * scale.z.abs(),
            );
            ray_vs_obb(origin, dir, max_dist, pos, rot, he)
        }
        ColliderShape::Sphere { radius } => {
            let r = radius * scale.x.abs().max(scale.y.abs()).max(scale.z.abs());
            ray_vs_sphere(origin, dir, max_dist, pos, r)
        }
        ColliderShape::Capsule { radius, half_height } => {
            let r  = radius * scale.x.abs().max(scale.z.abs());
            let hh = half_height * scale.y.abs();
            ray_vs_capsule(origin, dir, max_dist, pos, rot, r, hh)
        }
        ColliderShape::ConvexHull { vertices } => {
            ray_vs_convex_hull(origin, dir, max_dist, pos, scale, vertices)
        }
        ColliderShape::TriangleMesh { triangles } => {
            ray_vs_triangle_mesh(origin, dir, max_dist, pos, rot, scale, triangles)
        }
    }
}

fn ray_vs_obb(
    origin: Vector3<f32>, dir: Vector3<f32>, max_dist: f32,
    pos: Vector3<f32>, rot: Quaternion, he: Vector3<f32>,
) -> Option<RawHit> {
    let rot_inv     = rot.conjugate();
    let local_origin = rotate_vec3(rot_inv, origin - pos);
    let local_dir    = rotate_vec3(rot_inv, dir);

    let (t_min, t_max, axis_min) = slab_test(local_origin, local_dir, he)?;

    if t_min > max_dist || t_max < 0.0 { return None; }
    let t = if t_min >= 0.0 { t_min } else { t_max };
    if t < 0.0 || t > max_dist { return None; }

    let normal_w = rotate_vec3(rot, axis_min);
    let hit_pt   = origin + dir * t;

    Some(RawHit {
        point:    [hit_pt.x, hit_pt.y, hit_pt.z],
        normal:   [normal_w.x, normal_w.y, normal_w.z],
        distance: t,
    })
}

fn slab_test(
    origin: Vector3<f32>, dir: Vector3<f32>, he: Vector3<f32>,
) -> Option<(f32, f32, Vector3<f32>)> {
    let mut t_min = f32::NEG_INFINITY;
    let mut t_max = f32::INFINITY;
    let mut normal_min = Vector3::new(0.0, 1.0, 0.0);

    for i in 0..3 {
        let (o, d, h) = match i {
            0 => (origin.x, dir.x, he.x),
            1 => (origin.y, dir.y, he.y),
            _ => (origin.z, dir.z, he.z),
        };
        if d.abs() < 1e-10 {
            if o < -h || o > h { return None; }
        } else {
            let inv = 1.0 / d;
            let mut t1 = (-h - o) * inv;
            let mut t2 = ( h - o) * inv;
            let mut n  = Vector3::zero();
            match i { 0 => n.x = -1.0, 1 => n.y = -1.0, _ => n.z = -1.0 }
            if t1 > t2 { std::mem::swap(&mut t1, &mut t2); n = -n; }
            if t1 > t_min { t_min = t1; normal_min = n; }
            t_max = t_max.min(t2);
            if t_min > t_max { return None; }
        }
    }
    Some((t_min, t_max, normal_min))
}

fn ray_vs_sphere(
    origin: Vector3<f32>, dir: Vector3<f32>, max_dist: f32,
    center: Vector3<f32>, radius: f32,
) -> Option<RawHit> {
    let oc = origin - center;
    let b  = oc.dot(dir);
    let c  = oc.length_sq() - radius * radius;
    let disc = b * b - c;
    if disc < 0.0 { return None; }
    let sqrt_d = disc.sqrt();
    let t = -b - sqrt_d;
    let t = if t < 0.0 { -b + sqrt_d } else { t };
    if t < 0.0 || t > max_dist { return None; }

    let hit_pt = origin + dir * t;
    let normal = (hit_pt - center).normalize();
    Some(RawHit {
        point:    [hit_pt.x, hit_pt.y, hit_pt.z],
        normal:   [normal.x, normal.y, normal.z],
        distance: t,
    })
}

fn ray_vs_capsule(
    origin: Vector3<f32>, dir: Vector3<f32>, max_dist: f32,
    pos: Vector3<f32>, rot: Quaternion, radius: f32, half_height: f32,
) -> Option<RawHit> {
    let up  = rotate_vec3(rot, Vector3::new(0.0, half_height, 0.0));
    let p1  = pos - up;
    let p2  = pos + up;

    let axis = p2 - p1;
    let axis_len = axis.length();
    let axis_dir = if axis_len > 1e-6 { axis / axis_len } else { Vector3::new(0.0, 1.0, 0.0) };

    let rc = origin - p1;
    let d  = dir - axis_dir * dir.dot(axis_dir);
    let f  = rc - axis_dir * rc.dot(axis_dir);

    let a = d.length_sq();
    let b = 2.0 * f.dot(d);
    let c = f.length_sq() - radius * radius;
    let disc = b * b - 4.0 * a * c;

    if a < 1e-10 { return None; }
    if disc < 0.0 { return None; }
    let sqrt_d = disc.sqrt();
    let t0 = (-b - sqrt_d) / (2.0 * a);
    let t  = if t0 < 0.0 { (-b + sqrt_d) / (2.0 * a) } else { t0 };
    if t < 0.0 || t > max_dist { return None; }

    let hit_pt  = origin + dir * t;
    let proj    = (hit_pt - p1).dot(axis_dir);
    let hit_pt2 = if proj < 0.0 || proj > axis_len {
        let end = if proj < 0.0 { p1 } else { p2 };
        return ray_vs_sphere(origin, dir, max_dist, end, radius);
    } else { hit_pt };

    let on_axis = p1 + axis_dir * proj;
    let normal  = (hit_pt2 - on_axis).normalize();
    Some(RawHit {
        point:    [hit_pt2.x, hit_pt2.y, hit_pt2.z],
        normal:   [normal.x, normal.y, normal.z],
        distance: t,
    })
}

fn ray_vs_convex_hull(
    origin: Vector3<f32>, dir: Vector3<f32>, max_dist: f32,
    pos: Vector3<f32>, scale: Vector3<f32>,
    vertices: &[Vector3<f32>],
) -> Option<RawHit> {
    // 包含球で高速棄却
    let bounding_r = vertices.iter()
        .map(|v| {
            let sv = Vector3::new(v.x * scale.x, v.y * scale.y, v.z * scale.z);
            sv.length()
        })
        .fold(0.0_f32, f32::max);
    ray_vs_sphere(origin, dir, max_dist, pos, bounding_r)
}

fn ray_vs_triangle_mesh(
    origin: Vector3<f32>, dir: Vector3<f32>, max_dist: f32,
    pos: Vector3<f32>, rot: Quaternion, scale: Vector3<f32>,
    triangles: &[[Vector3<f32>; 3]],
) -> Option<RawHit> {
    let mut best: Option<RawHit> = None;

    for tri_local in triangles {
        let v: [Vector3<f32>; 3] = std::array::from_fn(|i| {
            let sv = Vector3::new(
                tri_local[i].x * scale.x,
                tri_local[i].y * scale.y,
                tri_local[i].z * scale.z,
            );
            pos + rotate_vec3(rot, sv)
        });

        if let Some(hit) = moller_trumbore(origin, dir, max_dist, v[0], v[1], v[2]) {
            if best.as_ref().map_or(true, |b| hit.distance < b.distance) {
                best = Some(hit);
            }
        }
    }
    best
}

/// Möller–Trumbore レイトライアングル交差判定。
fn moller_trumbore(
    origin: Vector3<f32>, dir: Vector3<f32>, max_dist: f32,
    v0: Vector3<f32>, v1: Vector3<f32>, v2: Vector3<f32>,
) -> Option<RawHit> {
    const EPS: f32 = 1e-7;
    let e1  = v1 - v0;
    let e2  = v2 - v0;
    let h   = dir.cross(e2);
    let det = e1.dot(h);
    if det.abs() < EPS { return None; }
    let inv_det = 1.0 / det;
    let s = origin - v0;
    let u = s.dot(h) * inv_det;
    if u < 0.0 || u > 1.0 { return None; }
    let q = s.cross(e1);
    let v = dir.dot(q) * inv_det;
    if v < 0.0 || u + v > 1.0 { return None; }
    let t = e2.dot(q) * inv_det;
    if t < EPS || t > max_dist { return None; }

    let hit_pt = origin + dir * t;
    let normal = e1.cross(e2).normalize();
    Some(RawHit {
        point:    [hit_pt.x, hit_pt.y, hit_pt.z],
        normal:   [normal.x, normal.y, normal.z],
        distance: t,
    })
}

// ─── Overlap 実装 ────────────────────────────────────────────────────────────

fn sphere_overlaps_shape(
    center: Vector3<f32>, radius: f32,
    pos: Vector3<f32>, rot: Quaternion, scale: Vector3<f32>,
    shape: &ColliderShape,
) -> bool {
    use crate::narrowphase::compute_contacts;
    let sphere = ColliderShape::Sphere { radius };
    compute_contacts(
        &sphere, center, Quaternion::identity(), Vector3::new(1.0, 1.0, 1.0),
        shape, pos, rot, scale,
    ).is_some()
}

fn box_overlaps_shape(
    center: Vector3<f32>, he: Vector3<f32>, box_rot: Quaternion,
    pos: Vector3<f32>, rot: Quaternion, scale: Vector3<f32>,
    shape: &ColliderShape,
) -> bool {
    use crate::narrowphase::compute_contacts;
    let query_box = ColliderShape::Box { half_extents: he };
    compute_contacts(
        &query_box, center, box_rot, Vector3::new(1.0, 1.0, 1.0),
        shape, pos, rot, scale,
    ).is_some()
}
