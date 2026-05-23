// ============================================================
//  seed-physics — 独立した物理エンジンクレート
//
//  SEED エンジン本体への依存を持たず、任意のプロジェクトに
//  組み込める PhysX / Rapier スタイルの物理ライブラリ。
//
//  【モジュール構成】
//    math/     : 物理演算用最小限数学型（Vector3, Quaternion, Mat3x3, Aabb）
//    world     : PhysicsWorld（メイン物理ワールド）
//    thread    : PhysicsThread（固定ステップスレッド）+ PhysicsCommand / PhysicsResult
//    collider  : ColliderShape（コライダー形状）
//    rigidbody : RigidBodyState（Rigidbody 運動状態）
//    events    : CollisionEvent / TriggerEvent
//    query     : PhysicsQuery / RaycastHit
//    broadphase: AABB ブロードフェーズ
//    narrowphase: ナローフェーズ衝突検出（ContactManifold）
//    solver    : Impulse ベース衝突応答ソルバー
// ============================================================

pub mod math;
pub mod world;
pub mod thread;
pub mod collider;
pub mod rigidbody;
pub mod events;
pub mod query;
pub mod broadphase;
pub mod narrowphase;
pub mod solver;

// ─── よく使う型を最上位から再公開 ────────────────────────────────────────────

/// 物理演算用数学型（Vec3, Quat, Mat3, Aabb）
pub use math::{Vector3, Quaternion, Mat3x3, Aabb};

/// 物理ワールドとオブジェクト
pub use world::{PhysicsWorld, PhysicsObject, DEFAULT_GRAVITY};

/// 物理スレッドとチャンネル型
pub use thread::{PhysicsThread, PhysicsCommand, PhysicsResult, PHYSICS_FIXED_STEP};

/// コライダー形状
pub use collider::ColliderShape;

/// Rigidbody 運動状態
pub use rigidbody::RigidBodyState;

/// 衝突・トリガーイベント
pub use events::{
    CollisionEvent, CollisionPhase,
    TriggerEvent, TriggerPhase,
    ContactInfo,
};

/// 空間クエリ
pub use query::{PhysicsQuery, RaycastHit};
