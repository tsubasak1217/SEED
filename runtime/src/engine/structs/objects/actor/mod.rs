use std::any::TypeId;
use std::collections::HashMap;
use serde::{Deserialize, Serialize};
use crate::engine::structs::components::{Component, ComponentData};
use crate::engine::core::clock::FrameContext;

// ============================================================
//  ActorData — シリアライズ用
// ============================================================

/// `Actor` のシリアライズ表現。
/// コンポーネントを先に列挙し、子 Actor を再帰的に持つ。
#[derive(Serialize, Deserialize)]
pub struct ActorData {
    pub name:       String,
    pub components: Vec<ComponentData>,
    pub children:   Vec<ActorData>,
}

// ============================================================
//  Actor
// ============================================================

/// シーン上のオブジェクト。
/// `Component` を動的に付与できる ECS エンティティ相当。
/// 子 Actor を所有することで親子ツリーを構成する。
pub struct Actor {
    pub name:     String,
    components:   HashMap<TypeId, Box<dyn Component>>,
    pub children: Vec<Actor>,
}

impl Actor {
    pub fn new() -> Self {
        Self::with_name("Actor")
    }

    pub fn with_name(name: impl Into<String>) -> Self {
        Self {
            name:       name.into(),
            components: HashMap::new(),
            children:   Vec::new(),
        }
    }

    // ─── コンポーネント操作 ─────────────────────────────────

    /// コンポーネントを付与する。同型がすでに存在する場合は上書き。
    pub fn add_component<T: Component + 'static>(&mut self, component: T) {
        self.components.insert(TypeId::of::<T>(), Box::new(component));
    }

    pub fn get_component<T: Component + 'static>(&self) -> Option<&T> {
        self.components
            .get(&TypeId::of::<T>())
            .and_then(|c| c.as_any().downcast_ref::<T>())
    }

    pub fn get_component_mut<T: Component + 'static>(&mut self) -> Option<&mut T> {
        self.components
            .get_mut(&TypeId::of::<T>())
            .and_then(|c| c.as_any_mut().downcast_mut::<T>())
    }

    pub fn has_component<T: Component + 'static>(&self) -> bool {
        self.components.contains_key(&TypeId::of::<T>())
    }

    // ─── 親子関係 ───────────────────────────────────────────

    pub fn add_child(&mut self, child: Actor) {
        self.children.push(child);
    }

    pub fn children(&self) -> &[Actor] {
        &self.children
    }

    pub fn children_mut(&mut self) -> &mut Vec<Actor> {
        &mut self.children
    }

    // ─── フレームライフサイクル ──────────────────────────────
    // 各メソッドは「自身のコンポーネント全件 → 子 Actor 全件」の順で伝播する。

    pub fn begin_frame(&mut self, ctx: &FrameContext) {
        for c in self.components.values_mut() { c.begin_frame(ctx); }
        for child in self.children.iter_mut()  { child.begin_frame(ctx); }
    }

    pub fn early_update(&mut self, ctx: &FrameContext) {
        for c in self.components.values_mut() { c.early_update(ctx); }
        for child in self.children.iter_mut()  { child.early_update(ctx); }
    }

    pub fn update(&mut self, ctx: &FrameContext) {
        for c in self.components.values_mut() { c.update(ctx); }
        for child in self.children.iter_mut()  { child.update(ctx); }
    }

    pub fn constant_update(&mut self, ctx: &FrameContext) {
        for c in self.components.values_mut() { c.constant_update(ctx); }
        for child in self.children.iter_mut()  { child.constant_update(ctx); }
    }

    pub fn late_update(&mut self, ctx: &FrameContext) {
        for c in self.components.values_mut() { c.late_update(ctx); }
        for child in self.children.iter_mut()  { child.late_update(ctx); }
    }

    pub fn render(&mut self, ctx: &FrameContext) {
        for c in self.components.values_mut() { c.render(ctx); }
        for child in self.children.iter_mut()  { child.render(ctx); }
    }

    pub fn end_frame(&mut self, ctx: &FrameContext) {
        for c in self.components.values_mut() { c.end_frame(ctx); }
        for child in self.children.iter_mut()  { child.end_frame(ctx); }
    }

    // ─── シリアライズ ────────────────────────────────────────

    /// このアクターとその子孫全体をシリアライズ用データに変換する。
    pub fn to_data(&self) -> ActorData {
        ActorData {
            name:       self.name.clone(),
            components: self.components.values().map(|c| c.to_data()).collect(),
            children:   self.children.iter().map(|c| c.to_data()).collect(),
        }
    }
}

impl Default for Actor {
    fn default() -> Self { Self::new() }
}
