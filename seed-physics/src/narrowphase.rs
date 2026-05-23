// ============================================================
//  narrowphase.rs — ナローフェーズ衝突検出
//
//  【形状ペアと使用アルゴリズム】
//    Box      vs Box       : SAT (15 軸テスト)
//    Sphere   vs Sphere    : 解析的（距離比較）
//    Box      vs Sphere    : 最近接点法
//    Capsule  vs Capsule   : 線分間最近接点 + 球判定
//    Box      vs Capsule   : 線分 vs Box の最近接点 + 半径
//    Sphere   vs Capsule   : 点 vs 線分 + 半径
//    ConvexHull vs *       : GJK（交差判定）+ EPA（深度・法線）
//    TriangleMesh vs *     : 各三角形に対し SAT を実行
// ============================================================

use crate::math::{Vector3, Quaternion};
use crate::collider::{ColliderShape, rotate_vec3};
use crate::events::ContactInfo;

// ─── ContactManifold ─────────────────────────────────────────────────────────

/// 衝突の接触情報まとめ。1 衝突ペアにつき 1 つ生成される。
#[derive(Debug, Clone)]
pub struct ContactManifold {
    /// A から B 方向を向く衝突法線（正規化済み）
    pub normal:   Vector3<f32>,
    /// 最大貫通深度（正値）
    pub depth:    f32,
    /// 接触点リスト（最大 4 点）
    pub contacts: Vec<ContactInfo>,
}

// ─── 公開エントリポイント ────────────────────────────────────────────────────

/// 2 つの物理オブジェクト間の接触を計算する。
///
/// 衝突していない場合は None を返す。
/// position/rotation/scale はコライダーオフセット適用済みのワールド値を渡す。
#[allow(clippy::too_many_arguments)]
pub fn compute_contacts(
    shape_a: &ColliderShape, pos_a: Vector3<f32>, rot_a: Quaternion, scale_a: Vector3<f32>,
    shape_b: &ColliderShape, pos_b: Vector3<f32>, rot_b: Quaternion, scale_b: Vector3<f32>,
) -> Option<ContactManifold> {
    use ColliderShape::*;
    match (shape_a, shape_b) {
        // ── 基本形状ペア ──────────────────────────────────────
        (Box { half_extents: he_a }, Box { half_extents: he_b }) => {
            let ha = scale_vec(*he_a, scale_a);
            let hb = scale_vec(*he_b, scale_b);
            box_vs_box(pos_a, rot_a, ha, pos_b, rot_b, hb)
        }
        (Sphere { radius: ra }, Sphere { radius: rb }) => {
            let ra = ra * scale_max(scale_a);
            let rb = rb * scale_max(scale_b);
            sphere_vs_sphere(pos_a, ra, pos_b, rb)
        }
        (Box { half_extents: he }, Sphere { radius: r }) => {
            let ha = scale_vec(*he, scale_a);
            let r  = r * scale_max(scale_b);
            box_vs_sphere(pos_a, rot_a, ha, pos_b, r)
        }
        (Sphere { radius: r }, Box { half_extents: he }) => {
            let ha = scale_vec(*he, scale_b);
            let r  = r * scale_max(scale_a);
            box_vs_sphere(pos_b, rot_b, ha, pos_a, r).map(|mut m| {
                m.normal = -m.normal;
                m.contacts.iter_mut().for_each(|c| { c.normal = [-c.normal[0], -c.normal[1], -c.normal[2]]; });
                m
            })
        }
        (Capsule { radius: ra, half_height: hha }, Capsule { radius: rb, half_height: hhb }) => {
            capsule_vs_capsule(pos_a, rot_a, *ra * scale_max(scale_a), *hha * scale_a.y.abs(),
                               pos_b, rot_b, *rb * scale_max(scale_b), *hhb * scale_b.y.abs())
        }
        (Sphere { radius: r }, Capsule { radius: rc, half_height: hh }) => {
            let r  = r  * scale_max(scale_a);
            let rc = rc * scale_max(scale_b);
            let hh = hh * scale_b.y.abs();
            sphere_vs_capsule(pos_a, r, pos_b, rot_b, rc, hh)
        }
        (Capsule { radius: rc, half_height: hh }, Sphere { radius: r }) => {
            let r  = r  * scale_max(scale_b);
            let rc = rc * scale_max(scale_a);
            let hh = hh * scale_a.y.abs();
            sphere_vs_capsule(pos_b, r, pos_a, rot_a, rc, hh).map(|mut m| {
                m.normal = -m.normal;
                m.contacts.iter_mut().for_each(|c| { c.normal = [-c.normal[0], -c.normal[1], -c.normal[2]]; });
                m
            })
        }
        (Box { half_extents: he }, Capsule { radius: r, half_height: hh }) => {
            let ha = scale_vec(*he, scale_a);
            let r  = r  * scale_max(scale_b);
            let hh = hh * scale_b.y.abs();
            box_vs_capsule(pos_a, rot_a, ha, pos_b, rot_b, r, hh)
        }
        (Capsule { radius: r, half_height: hh }, Box { half_extents: he }) => {
            let ha = scale_vec(*he, scale_b);
            let r  = r  * scale_max(scale_a);
            let hh = hh * scale_a.y.abs();
            box_vs_capsule(pos_b, rot_b, ha, pos_a, rot_a, r, hh).map(|mut m| {
                m.normal = -m.normal;
                m.contacts.iter_mut().for_each(|c| { c.normal = [-c.normal[0], -c.normal[1], -c.normal[2]]; });
                m
            })
        }

        // ── TriangleMesh ─────────────────────────────────────
        (TriangleMesh { triangles }, other) => {
            trimesh_vs_shape(triangles, pos_a, rot_a, scale_a, other, pos_b, rot_b, scale_b, false)
        }
        (other, TriangleMesh { triangles }) => {
            trimesh_vs_shape(triangles, pos_b, rot_b, scale_b, other, pos_a, rot_a, scale_a, true)
        }

        // ── ConvexHull（GJK/EPA）─────────────────────────────
        _ => gjk_epa(shape_a, pos_a, rot_a, scale_a, shape_b, pos_b, rot_b, scale_b),
    }
}

// ─── Box vs Box (SAT) ────────────────────────────────────────────────────────

/// SAT による Box vs Box 衝突検出（15 軸テスト）。
fn box_vs_box(
    pos_a: Vector3<f32>, rot_a: Quaternion, he_a: Vector3<f32>,
    pos_b: Vector3<f32>, rot_b: Quaternion, he_b: Vector3<f32>,
) -> Option<ContactManifold> {
    let ax = [
        rotate_vec3(rot_a, Vector3::new(1.0, 0.0, 0.0)),
        rotate_vec3(rot_a, Vector3::new(0.0, 1.0, 0.0)),
        rotate_vec3(rot_a, Vector3::new(0.0, 0.0, 1.0)),
    ];
    let bx = [
        rotate_vec3(rot_b, Vector3::new(1.0, 0.0, 0.0)),
        rotate_vec3(rot_b, Vector3::new(0.0, 1.0, 0.0)),
        rotate_vec3(rot_b, Vector3::new(0.0, 0.0, 1.0)),
    ];

    let d = pos_b - pos_a;

    let mut min_depth = f32::MAX;
    let mut best_normal = Vector3::new(0.0, 1.0, 0.0);

    for i in 0..3 {
        let axis = ax[i];
        let depth = sat_depth(axis, d, &ax, he_a, &bx, he_b);
        if depth < 0.0 { return None; }
        if depth < min_depth {
            min_depth = depth;
            best_normal = if d.dot(axis) >= 0.0 { axis } else { -axis };
        }
    }
    for i in 0..3 {
        let axis = bx[i];
        let depth = sat_depth(axis, d, &ax, he_a, &bx, he_b);
        if depth < 0.0 { return None; }
        if depth < min_depth {
            min_depth = depth;
            best_normal = if d.dot(axis) >= 0.0 { axis } else { -axis };
        }
    }
    for i in 0..3 {
        for j in 0..3 {
            let cross = ax[i].cross(bx[j]);
            let len   = cross.length();
            if len < 1e-6 { continue; }
            let axis = cross / len;
            let depth = sat_depth(axis, d, &ax, he_a, &bx, he_b);
            if depth < 0.0 { return None; }
            if depth < min_depth {
                min_depth = depth;
                best_normal = if d.dot(axis) >= 0.0 { axis } else { -axis };
            }
        }
    }

    let contact_pt = box_support_world(pos_a, rot_a, he_a, best_normal);

    Some(ContactManifold {
        normal:   best_normal,
        depth:    min_depth,
        contacts: vec![ContactInfo {
            point:  [contact_pt.x, contact_pt.y, contact_pt.z],
            normal: [best_normal.x, best_normal.y, best_normal.z],
            depth:  min_depth,
        }],
    })
}

fn sat_depth(
    axis: Vector3<f32>, d: Vector3<f32>,
    ax: &[Vector3<f32>; 3], he_a: Vector3<f32>,
    bx: &[Vector3<f32>; 3], he_b: Vector3<f32>,
) -> f32 {
    let proj_a = (ax[0].dot(axis)).abs() * he_a.x
               + (ax[1].dot(axis)).abs() * he_a.y
               + (ax[2].dot(axis)).abs() * he_a.z;
    let proj_b = (bx[0].dot(axis)).abs() * he_b.x
               + (bx[1].dot(axis)).abs() * he_b.y
               + (bx[2].dot(axis)).abs() * he_b.z;
    let dist = d.dot(axis).abs();
    proj_a + proj_b - dist
}

fn box_support_world(pos: Vector3<f32>, rot: Quaternion, he: Vector3<f32>, dir: Vector3<f32>) -> Vector3<f32> {
    let local_dir = rotate_vec3(rot.conjugate(), dir);
    let lp = Vector3::new(
        if local_dir.x >= 0.0 { he.x } else { -he.x },
        if local_dir.y >= 0.0 { he.y } else { -he.y },
        if local_dir.z >= 0.0 { he.z } else { -he.z },
    );
    pos + rotate_vec3(rot, lp)
}

// ─── Sphere vs Sphere ────────────────────────────────────────────────────────

fn sphere_vs_sphere(
    pos_a: Vector3<f32>, ra: f32,
    pos_b: Vector3<f32>, rb: f32,
) -> Option<ContactManifold> {
    let diff  = pos_b - pos_a;
    let dist2 = diff.length_sq();
    let sum_r = ra + rb;
    if dist2 >= sum_r * sum_r { return None; }

    let dist   = dist2.sqrt();
    let normal = if dist > 1e-6 { diff / dist } else { Vector3::new(0.0, 1.0, 0.0) };
    let depth  = sum_r - dist;
    let point  = pos_a + normal * ra;

    Some(ContactManifold {
        normal,
        depth,
        contacts: vec![ContactInfo {
            point:  [point.x, point.y, point.z],
            normal: [normal.x, normal.y, normal.z],
            depth,
        }],
    })
}

// ─── Box vs Sphere ───────────────────────────────────────────────────────────

fn box_vs_sphere(
    pos_box: Vector3<f32>, rot_box: Quaternion, he: Vector3<f32>,
    pos_sph: Vector3<f32>, r: f32,
) -> Option<ContactManifold> {
    let local_center = rotate_vec3(rot_box.conjugate(), pos_sph - pos_box);

    let closest = Vector3::new(
        local_center.x.clamp(-he.x, he.x),
        local_center.y.clamp(-he.y, he.y),
        local_center.z.clamp(-he.z, he.z),
    );

    let diff  = local_center - closest;
    let dist2 = diff.length_sq();
    if dist2 >= r * r { return None; }

    let dist = dist2.sqrt();
    let local_normal = if dist > 1e-6 {
        diff / dist
    } else {
        box_interior_normal(local_center, he)
    };
    let depth  = r - dist;
    let normal = rotate_vec3(rot_box, local_normal);
    let pt_w   = pos_box + rotate_vec3(rot_box, closest);

    Some(ContactManifold {
        normal,
        depth,
        contacts: vec![ContactInfo {
            point:  [pt_w.x, pt_w.y, pt_w.z],
            normal: [normal.x, normal.y, normal.z],
            depth,
        }],
    })
}

fn box_interior_normal(center: Vector3<f32>, he: Vector3<f32>) -> Vector3<f32> {
    let dx_pos = he.x - center.x;
    let dx_neg = center.x + he.x;
    let dy_pos = he.y - center.y;
    let dy_neg = center.y + he.y;
    let dz_pos = he.z - center.z;
    let dz_neg = center.z + he.z;

    let min_dist = dx_pos.min(dx_neg).min(dy_pos).min(dy_neg).min(dz_pos).min(dz_neg);
    if (min_dist - dx_pos).abs() < 1e-6 { return Vector3::new( 1.0, 0.0, 0.0); }
    if (min_dist - dx_neg).abs() < 1e-6 { return Vector3::new(-1.0, 0.0, 0.0); }
    if (min_dist - dy_pos).abs() < 1e-6 { return Vector3::new( 0.0, 1.0, 0.0); }
    if (min_dist - dy_neg).abs() < 1e-6 { return Vector3::new( 0.0,-1.0, 0.0); }
    if (min_dist - dz_pos).abs() < 1e-6 { return Vector3::new( 0.0, 0.0, 1.0); }
    Vector3::new(0.0, 0.0, -1.0)
}

// ─── Capsule vs Capsule ──────────────────────────────────────────────────────

fn capsule_vs_capsule(
    pos_a: Vector3<f32>, rot_a: Quaternion, ra: f32, hha: f32,
    pos_b: Vector3<f32>, rot_b: Quaternion, rb: f32, hhb: f32,
) -> Option<ContactManifold> {
    let up_a = rotate_vec3(rot_a, Vector3::new(0.0, hha, 0.0));
    let up_b = rotate_vec3(rot_b, Vector3::new(0.0, hhb, 0.0));
    let (a1, a2) = (pos_a - up_a, pos_a + up_a);
    let (b1, b2) = (pos_b - up_b, pos_b + up_b);

    let (pa, pb) = closest_point_segment_segment(a1, a2, b1, b2);
    let diff  = pb - pa;
    let dist2 = diff.length_sq();
    let sum_r = ra + rb;
    if dist2 >= sum_r * sum_r { return None; }

    let dist   = dist2.sqrt();
    let normal = if dist > 1e-6 { diff / dist } else { Vector3::new(0.0, 1.0, 0.0) };
    let depth  = sum_r - dist;
    let point  = pa + normal * ra;

    Some(ContactManifold {
        normal,
        depth,
        contacts: vec![ContactInfo {
            point:  [point.x, point.y, point.z],
            normal: [normal.x, normal.y, normal.z],
            depth,
        }],
    })
}

// ─── Sphere vs Capsule ───────────────────────────────────────────────────────

fn sphere_vs_capsule(
    pos_sph: Vector3<f32>, r_sph: f32,
    pos_cap: Vector3<f32>, rot_cap: Quaternion, r_cap: f32, hh_cap: f32,
) -> Option<ContactManifold> {
    let up  = rotate_vec3(rot_cap, Vector3::new(0.0, hh_cap, 0.0));
    let p1  = pos_cap - up;
    let p2  = pos_cap + up;
    let pt  = closest_point_on_segment(pos_sph, p1, p2);
    sphere_vs_sphere(pos_sph, r_sph, pt, r_cap)
}

// ─── Box vs Capsule ──────────────────────────────────────────────────────────

fn box_vs_capsule(
    pos_box: Vector3<f32>, rot_box: Quaternion, he: Vector3<f32>,
    pos_cap: Vector3<f32>, rot_cap: Quaternion, r: f32, hh: f32,
) -> Option<ContactManifold> {
    let up = rotate_vec3(rot_cap, Vector3::new(0.0, hh, 0.0));
    let p1 = pos_cap - up;
    let p2 = pos_cap + up;

    let rot_inv = rot_box.conjugate();
    let lp1 = rotate_vec3(rot_inv, p1 - pos_box);
    let lp2 = rotate_vec3(rot_inv, p2 - pos_box);

    let t = capsule_axis_box_closest_t(lp1, lp2, he);
    let t = t.clamp(0.0, 1.0);
    let local_pt_on_seg = lp1 + (lp2 - lp1) * t;
    let local_closest_on_box = Vector3::new(
        local_pt_on_seg.x.clamp(-he.x, he.x),
        local_pt_on_seg.y.clamp(-he.y, he.y),
        local_pt_on_seg.z.clamp(-he.z, he.z),
    );

    let world_seg_pt = pos_box + rotate_vec3(rot_box, local_pt_on_seg);
    let world_box_pt = pos_box + rotate_vec3(rot_box, local_closest_on_box);

    let diff  = world_seg_pt - world_box_pt;
    let dist2 = diff.length_sq();
    if dist2 >= r * r { return None; }

    let dist   = dist2.sqrt();
    let normal = if dist > 1e-6 { diff / dist } else { Vector3::new(0.0, 1.0, 0.0) };
    let depth  = r - dist;
    let point  = world_box_pt;

    Some(ContactManifold {
        normal,
        depth,
        contacts: vec![ContactInfo {
            point:  [point.x, point.y, point.z],
            normal: [normal.x, normal.y, normal.z],
            depth,
        }],
    })
}

fn capsule_axis_box_closest_t(lp1: Vector3<f32>, lp2: Vector3<f32>, _he: Vector3<f32>) -> f32 {
    let dir = lp2 - lp1;
    let len2 = dir.length_sq();
    if len2 < 1e-10 { return 0.0; }
    let t = -lp1.dot(dir) / len2;
    t.clamp(0.0, 1.0)
}

// ─── TriangleMesh vs Shape ───────────────────────────────────────────────────

fn trimesh_vs_shape(
    triangles: &[[Vector3<f32>; 3]],
    mesh_pos: Vector3<f32>, mesh_rot: Quaternion, mesh_scale: Vector3<f32>,
    other:    &ColliderShape,
    other_pos: Vector3<f32>, other_rot: Quaternion, other_scale: Vector3<f32>,
    flip:     bool,
) -> Option<ContactManifold> {
    let mut best: Option<ContactManifold> = None;

    for tri_local in triangles {
        let tri_world: [Vector3<f32>; 3] = std::array::from_fn(|i| {
            let sv = Vector3::new(
                tri_local[i].x * mesh_scale.x,
                tri_local[i].y * mesh_scale.y,
                tri_local[i].z * mesh_scale.z,
            );
            mesh_pos + rotate_vec3(mesh_rot, sv)
        });

        if let Some(mut m) = triangle_vs_shape(&tri_world, other, other_pos, other_rot, other_scale) {
            if flip {
                m.normal = -m.normal;
                for c in &mut m.contacts {
                    c.normal = [-c.normal[0], -c.normal[1], -c.normal[2]];
                }
            }
            if best.as_ref().map_or(true, |b| m.depth > b.depth) {
                best = Some(m);
            }
        }
    }
    best
}

fn triangle_vs_shape(
    tri:   &[Vector3<f32>; 3],
    shape: &ColliderShape,
    pos:   Vector3<f32>, rot: Quaternion, scale: Vector3<f32>,
) -> Option<ContactManifold> {
    let e1 = tri[1] - tri[0];
    let e2 = tri[2] - tri[0];
    let n  = e1.cross(e2);
    let nl = n.length();
    if nl < 1e-7 { return None; }
    let tri_normal = n / nl;

    let support = shape.support_world(pos, rot, scale, -tri_normal);
    let d       = (support - tri[0]).dot(tri_normal);
    if d >= 0.0 { return None; }

    let depth = (-d).abs();
    let pt    = support;

    Some(ContactManifold {
        normal:   tri_normal,
        depth,
        contacts: vec![ContactInfo {
            point:  [pt.x, pt.y, pt.z],
            normal: [tri_normal.x, tri_normal.y, tri_normal.z],
            depth,
        }],
    })
}

// ─── GJK / EPA ───────────────────────────────────────────────────────────────

fn gjk_epa(
    shape_a: &ColliderShape, pos_a: Vector3<f32>, rot_a: Quaternion, scale_a: Vector3<f32>,
    shape_b: &ColliderShape, pos_b: Vector3<f32>, rot_b: Quaternion, scale_b: Vector3<f32>,
) -> Option<ContactManifold> {
    let support = |dir: Vector3<f32>| -> Vector3<f32> {
        shape_a.support_world(pos_a, rot_a, scale_a, dir)
        - shape_b.support_world(pos_b, rot_b, scale_b, -dir)
    };

    let mut simplex: Vec<Vector3<f32>> = Vec::with_capacity(4);
    let mut dir = pos_a - pos_b;
    if dir.length_sq() < 1e-10 { dir = Vector3::new(0.0, 1.0, 0.0); }

    simplex.push(support(dir));
    dir = -simplex[0];

    const MAX_ITER: usize = 32;
    for _ in 0..MAX_ITER {
        if dir.length_sq() < 1e-10 { break; }
        let a = support(dir);
        if a.dot(dir) < 0.0 { return None; }
        simplex.push(a);
        if do_simplex(&mut simplex, &mut dir) {
            return epa(simplex, support);
        }
    }
    None
}

fn do_simplex(simplex: &mut Vec<Vector3<f32>>, dir: &mut Vector3<f32>) -> bool {
    match simplex.len() {
        2 => do_simplex_line(simplex, dir),
        3 => do_simplex_triangle(simplex, dir),
        4 => do_simplex_tetrahedron(simplex, dir),
        _ => false,
    }
}

fn do_simplex_line(s: &mut Vec<Vector3<f32>>, dir: &mut Vector3<f32>) -> bool {
    let (b, a) = (s[0], s[1]);
    let ab = b - a;
    let ao = -a;
    if ab.dot(ao) > 0.0 {
        *dir = ab.cross(ao).cross(ab);
    } else {
        s.retain(|_| false);
        s.push(a);
        *dir = ao;
    }
    false
}

fn do_simplex_triangle(s: &mut Vec<Vector3<f32>>, dir: &mut Vector3<f32>) -> bool {
    let (c, b, a) = (s[0], s[1], s[2]);
    let ab = b - a; let ac = c - a; let ao = -a;
    let abc = ab.cross(ac);

    if abc.cross(ac).dot(ao) > 0.0 {
        if ac.dot(ao) > 0.0 {
            *s = vec![c, a];
            *dir = ac.cross(ao).cross(ac);
        } else {
            return do_simplex_line(&mut vec![b, a].into_iter().collect(), dir)
                .then(|| { *s = vec![b, a]; true }).unwrap_or(false);
        }
    } else if ab.cross(abc).dot(ao) > 0.0 {
        *s = vec![b, a];
        return do_simplex_line(s, dir);
    } else {
        if abc.dot(ao) > 0.0 {
            *dir = abc;
        } else {
            *s = vec![b, c, a];
            *dir = -abc;
        }
    }
    false
}

fn do_simplex_tetrahedron(s: &mut Vec<Vector3<f32>>, dir: &mut Vector3<f32>) -> bool {
    let (d, c, b, a) = (s[0], s[1], s[2], s[3]);
    let ab = b - a; let ac = c - a; let ad = d - a; let ao = -a;
    let abc = ab.cross(ac);
    let acd = ac.cross(ad);
    let adb = ad.cross(ab);

    if abc.dot(ao) > 0.0 { *s = vec![c, b, a]; return do_simplex_triangle(s, dir); }
    if acd.dot(ao) > 0.0 { *s = vec![d, c, a]; return do_simplex_triangle(s, dir); }
    if adb.dot(ao) > 0.0 { *s = vec![b, d, a]; return do_simplex_triangle(s, dir); }
    true
}

fn epa(
    simplex: Vec<Vector3<f32>>,
    support: impl Fn(Vector3<f32>) -> Vector3<f32>,
) -> Option<ContactManifold> {
    const MAX_FACES: usize = 64;
    const MAX_ITER:  usize = 32;
    const TOLERANCE: f32   = 1e-4;

    let mut verts = simplex;
    while verts.len() < 4 {
        verts.push(support(Vector3::new(0.0, 1.0, 0.0)));
    }

    let mut faces: Vec<(usize, usize, usize)> = vec![
        (0, 1, 2), (0, 3, 1), (0, 2, 3), (1, 3, 2),
    ];

    for _ in 0..MAX_ITER {
        if faces.len() > MAX_FACES { break; }

        let (best_i, _, best_n, best_d) = faces.iter().enumerate()
            .map(|(i, &(a, b, c))| {
                let n = face_normal(&verts, a, b, c);
                let d = n.dot(verts[a]);
                (i, i, n, d)
            })
            .min_by(|x, y| x.3.partial_cmp(&y.3).unwrap())?;

        let p = support(best_n);
        let new_d = best_n.dot(p);

        if (new_d - best_d).abs() < TOLERANCE {
            let depth = best_d.abs();
            let idx = faces[best_i];
            let contact_pt = (verts[idx.0] + verts[idx.1] + verts[idx.2]) / 3.0;
            return Some(ContactManifold {
                normal: best_n,
                depth,
                contacts: vec![ContactInfo {
                    point:  [contact_pt.x, contact_pt.y, contact_pt.z],
                    normal: [best_n.x, best_n.y, best_n.z],
                    depth,
                }],
            });
        }

        let new_idx = verts.len();
        verts.push(p);

        let mut edges: Vec<(usize, usize)> = Vec::new();
        faces.retain(|&(a, b, c)| {
            let n = face_normal(&verts, a, b, c);
            if n.dot(p - verts[a]) > 0.0 {
                add_edge_unique(&mut edges, a, b);
                add_edge_unique(&mut edges, b, c);
                add_edge_unique(&mut edges, c, a);
                false
            } else {
                true
            }
        });

        for (ea, eb) in edges {
            faces.push((ea, eb, new_idx));
        }
    }
    None
}

fn face_normal(verts: &[Vector3<f32>], a: usize, b: usize, c: usize) -> Vector3<f32> {
    let ab = verts[b] - verts[a];
    let ac = verts[c] - verts[a];
    let n  = ab.cross(ac).normalize();
    if n.dot(verts[a]) < 0.0 { -n } else { n }
}

fn add_edge_unique(edges: &mut Vec<(usize, usize)>, a: usize, b: usize) {
    if let Some(pos) = edges.iter().position(|&(ea, eb)| ea == b && eb == a) {
        edges.swap_remove(pos);
    } else {
        edges.push((a, b));
    }
}

// ─── 線分ユーティリティ ──────────────────────────────────────────────────────

/// 線分 [p1, p2] 上の、点 `p` に最近接する点を返す。
pub fn closest_point_on_segment(p: Vector3<f32>, p1: Vector3<f32>, p2: Vector3<f32>) -> Vector3<f32> {
    let ab = p2 - p1;
    let len2 = ab.length_sq();
    if len2 < 1e-10 { return p1; }
    let t = ((p - p1).dot(ab) / len2).clamp(0.0, 1.0);
    p1 + ab * t
}

/// 2 本の線分の最近接点ペアを返す。
pub fn closest_point_segment_segment(
    a1: Vector3<f32>, a2: Vector3<f32>,
    b1: Vector3<f32>, b2: Vector3<f32>,
) -> (Vector3<f32>, Vector3<f32>) {
    let da = a2 - a1;
    let db = b2 - b1;
    let r  = a1 - b1;
    let a  = da.length_sq();
    let e  = db.length_sq();
    let f  = db.dot(r);

    let (s, t) = if a < 1e-10 && e < 1e-10 {
        (0.0, 0.0)
    } else if a < 1e-10 {
        (0.0, (f / e).clamp(0.0, 1.0))
    } else {
        let c = da.dot(r);
        if e < 1e-10 {
            ((-c / a).clamp(0.0, 1.0), 0.0)
        } else {
            let b_val = da.dot(db);
            let denom = a * e - b_val * b_val;
            let s = if denom.abs() > 1e-10 {
                ((b_val * f - c * e) / denom).clamp(0.0, 1.0)
            } else { 0.0 };
            let t = ((b_val * s + f) / e).clamp(0.0, 1.0);
            let s = ((-c + b_val * t) / a).clamp(0.0, 1.0);
            (s, t)
        }
    };
    (a1 + da * s, b1 + db * t)
}

// ─── スカラーユーティリティ ──────────────────────────────────────────────────

#[inline]
fn scale_vec(v: Vector3<f32>, s: Vector3<f32>) -> Vector3<f32> {
    Vector3::new(v.x * s.x.abs(), v.y * s.y.abs(), v.z * s.z.abs())
}

#[inline]
fn scale_max(s: Vector3<f32>) -> f32 {
    s.x.abs().max(s.y.abs()).max(s.z.abs())
}
