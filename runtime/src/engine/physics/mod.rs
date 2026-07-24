// ============================================================
//  engine/physics/mod.rs — 物理エンジンモジュール（Rapier3D / Rapier2D バックエンド）
//
//  【構成】
//    types.rs   — 3D メインスレッド・物理スレッド間の共通型定義
//    thread.rs  — Rapier3D を使用した 3D 物理スレッド実装
//    types2d.rs — 2D メインスレッド・物理スレッド間の共通型定義
//    thread2d.rs — Rapier2D を使用した 2D 物理スレッド実装
//
//  【エクスポート方針】
//    利用側は `use crate::engine::physics::*` で取得できるよう
//    主要な型・定数をすべてこのモジュールから再エクスポートする。
// ============================================================

pub mod thread;
pub mod thread2d;
mod types;
mod types2d;

// ── 3D 型・定数の再エクスポート ─────────────────────────────────────────────

pub use types::{
    // 形状・RB 状態
    ColliderShape,
    // イベント
    CollisionEvent,
    CollisionPhase,
    DEFAULT_GRAVITY,
    // 定数
    PHYSICS_FIXED_STEP,
    PhysicsCommand,
    // スレッド通信型
    PhysicsObject,
    PhysicsResult,
    // クエリ
    RaycastHit,
    RigidBodyState,
    TriggerEvent,
    TriggerPhase,
};

pub use thread::PhysicsThread;

// ── 2D 型・定数の再エクスポート ─────────────────────────────────────────────

pub use types2d::{
    // 形状・RB 状態
    ColliderShape2d,
    // イベント
    CollisionEvent2d,
    CollisionPhase2d,
    DEFAULT_GRAVITY_2D,
    // 定数
    PHYSICS_2D_FIXED_STEP,
    PIXELS_PER_METER,
    PhysicsCommand2d,
    // スレッド通信型
    PhysicsObject2d,
    PhysicsResult2d,
    RigidBodyState2d,
    TriggerEvent2d,
    TriggerPhase2d,
};

pub use thread2d::PhysicsThread2d;
