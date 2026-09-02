// ============================================================
//  engine/physics/mod.rs — 物理エンジンモジュール（Rapier3D / Rapier2D バックエンド）
//
//  【構成】
//    types.rs   — 3D メインスレッド・物理スレッド間の共通型定義
//    shape.rs   — ColliderShape → Rapier ビルダー変換（thread / char_world 共通部品）
//    thread.rs  — Rapier3D を使用した 3D 物理スレッド実装
//    char_world.rs — メインスレッド常駐のキャラクター衝突ミラー（KCC その場解決）
//    char_gravity.rs — キネマティックキャラへのノーコード重力適用（落下積分・接地リセット）
//    types2d.rs — 2D メインスレッド・物理スレッド間の共通型定義
//    thread2d.rs — Rapier2D を使用した 2D 物理スレッド実装
//
//  【エクスポート方針】
//    利用側は `use crate::engine::physics::*` で取得できるよう
//    主要な型・定数をすべてこのモジュールから再エクスポートする。
// ============================================================

pub(crate) mod types;
mod types2d;
mod shape;
pub mod thread;
pub mod char_world;
pub mod char_gravity;
pub mod thread2d;

// ── 3D 型・定数の再エクスポート ─────────────────────────────────────────────

pub use types::{
    // 定数
    PHYSICS_FIXED_STEP, DEFAULT_GRAVITY,
    // 形状・RB 状態
    ColliderShape, RigidBodyState,
    // スレッド通信型
    PhysicsObject, PhysicsCommand, PhysicsResult,
    // イベント
    CollisionEvent, CollisionPhase,
    TriggerEvent, TriggerPhase,
    // クエリ
    RaycastHit,
};

pub use thread::PhysicsThread;
pub use char_world::CharacterWorld;
pub use char_gravity::CharacterGravity;

// ── 2D 型・定数の再エクスポート ─────────────────────────────────────────────

pub use types2d::{
    // 定数
    PHYSICS_2D_FIXED_STEP, DEFAULT_GRAVITY_2D, PIXELS_PER_METER,
    // 形状・RB 状態
    ColliderShape2d, RigidBodyState2d,
    // スレッド通信型
    PhysicsObject2d, PhysicsCommand2d, PhysicsResult2d,
    // イベント
    CollisionEvent2d, CollisionPhase2d,
    TriggerEvent2d, TriggerPhase2d,
};

pub use thread2d::PhysicsThread2d;
