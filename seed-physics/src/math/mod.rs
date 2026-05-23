// ============================================================
//  math/mod.rs — 物理エンジン用数学型モジュール
//
//  SEED エンジン本体への依存を持たない独立した数学型。
//  PhysX / Rapier スタイルで組み込み可能。
// ============================================================

pub mod vector3;
pub mod quaternion;
pub mod mat3x3;
pub mod aabb;

// 主要型を再公開
pub use vector3::Vector3;
pub use quaternion::Quaternion;
pub use mat3x3::Mat3x3;
pub use aabb::Aabb;
