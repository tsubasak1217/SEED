// ============================================================
//  engine/physics/mod.rs — 物理エンジンモジュール（Rapier3D バックエンド）
//
//  【構成】
//    types.rs  — メインスレッド・物理スレッド間の共通型定義
//    thread.rs — Rapier3D を使用した物理スレッド実装
//
//  【エクスポート方針】
//    physics_ops.rs など利用側は `use crate::engine::physics::*` で取得できるよう
//    主要な型・定数をすべてこのモジュールから再エクスポートする。
// ============================================================

mod types;
pub mod thread;

// ── 型・定数の再エクスポート ────────────────────────────────────────────────

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
