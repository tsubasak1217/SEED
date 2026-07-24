// ============================================================
//  ecs/mod.rs — ECS コアモジュール
//
//  外部から使う際の主要な型・トレイトを再エクスポートする。
// ============================================================

pub mod entity;
pub mod schedule;
pub mod storage;
pub mod system;
pub mod world;

pub use entity::Entity;
pub use schedule::{Phase, Schedule};
pub use storage::Component;
pub use system::{FnSystem, System};
pub use world::World;
