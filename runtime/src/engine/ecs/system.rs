// ============================================================
//  system.rs — ECS システムトレイト
//
//  System = World を受け取り、コンポーネントを横断的に処理する関数。
//  コンポーネント自体にロジックを持たせず、System が担う。
//
//  例:
//    fn model_update(world: &mut World, ctx: &FrameContext) {
//        for (entity, mc) in world.query_mut::<ModelComponent>() { ... }
//    }
//    schedule.add_system(Phase::Update, FnSystem::new("model_update", model_update));
// ============================================================

use crate::engine::core::clock::FrameContext;
use super::world::World;

// ─── System トレイト ──────────────────────────────────────────────────────────

/// ECS システムのトレイト。
/// World を受け取り、コンポーネントを横断して処理を行う。
pub trait System: Send + Sync {
    fn name(&self) -> &'static str;
    fn run(&mut self, world: &mut World, ctx: &FrameContext);
}

// ─── FnSystem ────────────────────────────────────────────────────────────────

/// 関数またはクロージャを System として包むラッパー。
///
/// ```rust
/// let sys = FnSystem::new("my_system", |world, ctx| {
///     for (e, mc) in world.query_mut::<ModelComponent>() { ... }
/// });
/// schedule.add_system(Phase::Update, sys);
/// ```
pub struct FnSystem {
    name: &'static str,
    func: Box<dyn FnMut(&mut World, &FrameContext) + Send + Sync>,
}

impl FnSystem {
    pub fn new(
        name: &'static str,
        f: impl FnMut(&mut World, &FrameContext) + Send + Sync + 'static,
    ) -> Self {
        Self { name, func: Box::new(f) }
    }
}

impl System for FnSystem {
    fn name(&self) -> &'static str { self.name }
    fn run(&mut self, world: &mut World, ctx: &FrameContext) { (self.func)(world, ctx); }
}
