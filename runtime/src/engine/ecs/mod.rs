// ============================================================
//  ecs/mod.rs — ECS コアモジュール
//
//  外部から使う際の主要な型・トレイトを再エクスポートする。
// ============================================================

pub mod entity;
pub mod storage;
pub mod world;
pub mod system;
pub mod schedule;

pub use entity::Entity;
pub use storage::Component;
pub use world::World;
pub use system::{System, FnSystem};
pub use schedule::{Phase, Schedule};
