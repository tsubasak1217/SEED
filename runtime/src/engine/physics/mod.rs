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

mod types;
mod types2d;
pub mod thread;
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
    // キャラクターコントローラー
    CharacterMoveResult,
};

pub use thread::PhysicsThread;

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
