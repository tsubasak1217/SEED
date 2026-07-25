// ============================================================
//  physics/shape.rs — Rapier コライダー形状ビルダー（物理スレッド／キャラ世界の共通部品）
//
//  【責務】
//    SEED の `ColliderShape`（外部依存なしの純粋型）から Rapier の `ColliderBuilder`／
//    `Isometry` を構築する変換関数を提供する。物理スレッド（thread.rs）と
//    メインスレッドのキャラクター衝突ミラー（char_world.rs）の**両方**が同じ形状構築を
//    使うため、片方に埋め込まず共通モジュールへ切り出して単一責任にする。
//
//  【なぜ共通化するか】
//    キャラクターコントローラーの押し戻しは、物理スレッドのミラーと**寸分違わぬ形状**の
//    コライダーをメインスレッド側にも構築して KCC で解決する（char_world.rs 冒頭の設計解説参照）。
//    形状構築ロジックが二重化すると、片方だけスケール適用や軸順を直して押し戻しがズレる
//    危険があるため、ここ 1 箇所に集約する。
// ============================================================

use rapier3d::prelude::*;
use nalgebra::UnitQuaternion;

use super::types::ColliderShape;

/// SEED の `[x, y, z, w]` クォータニオンと位置を Rapier の `Isometry` に変換する。
///
/// nalgebra の `Quaternion::new(w, i, j, k)` は w が先頭引数である点に注意。
/// SEED 規約は `rotation = [x(i), y(j), z(k), w]`。
pub(crate) fn to_isometry(position: [f32; 3], rotation: [f32; 4]) -> Isometry<Real> {
    let translation = Translation::new(position[0], position[1], position[2]);
    let nq = UnitQuaternion::new_normalize(nalgebra::Quaternion::new(
        rotation[3], // w
        rotation[0], // i
        rotation[1], // j
        rotation[2], // k
    ));
    Isometry::from_parts(translation, nq)
}

/// `ColliderShape` から Rapier の `ColliderBuilder` を構築する。
///
/// `scale` を各頂点・半辺長へ乗算してワールドスケールを反映する。
/// Static トライメッシュ（地形）用の共有頂点＋インデックス版（`TriangleMeshIndexed`）を含め、
/// 全バリアントを 1 箇所で扱う。
pub(crate) fn build_collider_shape(shape: &ColliderShape, scale: &[f32; 3]) -> ColliderBuilder {
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
