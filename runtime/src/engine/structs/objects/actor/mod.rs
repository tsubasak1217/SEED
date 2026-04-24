use std::any::TypeId;
use serde::{Deserialize, Serialize};
use crate::engine::structs::components::{Component, ComponentData};
use crate::engine::core::clock::FrameContext;

// ============================================================
//  ActorTransform
// ============================================================

#[derive(Clone, PartialEq, Serialize, Deserialize)]
pub struct ActorTransform {
    #[serde(default)]
    pub position: [f32; 3],
    /// YXZ オイラー角（度）
    #[serde(default = "default_euler")]
    pub rotation: [f32; 3],
    #[serde(default = "default_scale")]
    pub scale:    [f32; 3],
}

fn default_euler() -> [f32; 3] { [0.0; 3] }
fn default_scale() -> [f32; 3] { [1.0, 1.0, 1.0] }

impl Default for ActorTransform {
    fn default() -> Self {
        Self { position: [0.0; 3], rotation: [0.0; 3], scale: [1.0, 1.0, 1.0] }
    }
}

impl ActorTransform {
    /// TRS 行列（列優先ではなく行優先・GPU 慣習）を生成する。
    pub fn to_mat4(&self) -> [[f32; 4]; 4] {
        let [ex, ey, ez] = self.rotation.map(f32::to_radians);
        let (cx, sx) = (ex.cos(), ex.sin());
        let (cy, sy) = (ey.cos(), ey.sin());
        let (cz, sz) = (ez.cos(), ez.sin());

        // YXZ: Ry * Rx * Rz
        let r00 = cy * cz + sy * sx * sz;
        let r01 = -cy * sz + sy * sx * cz;
        let r02 = sy * cx;
        let r10 = cx * sz;
        let r11 = cx * cz;
        let r12 = -sx;
        let r20 = -sy * cz + cy * sx * sz;
        let r21 = sy * sz + cy * sx * cz;
        let r22 = cy * cx;

        let [svx, svy, svz] = self.scale;
        let [tx, ty, tz]    = self.position;

        [
            [r00 * svx, r01 * svy, r02 * svz, tx],
            [r10 * svx, r11 * svy, r12 * svz, ty],
            [r20 * svx, r21 * svy, r22 * svz, tz],
            [0.0,       0.0,       0.0,        1.0],
        ]
    }

    /// 4x4 行列から位置・YXZ オイラー角（度）・スケールを取り出す（近似）。
    pub fn from_mat4(m: &[[f32; 4]; 4]) -> Self {
        let tx = m[0][3];
        let ty = m[1][3];
        let tz = m[2][3];

        let sx = (m[0][0]*m[0][0] + m[1][0]*m[1][0] + m[2][0]*m[2][0]).sqrt();
        let sy = (m[0][1]*m[0][1] + m[1][1]*m[1][1] + m[2][1]*m[2][1]).sqrt();
        let sz = (m[0][2]*m[0][2] + m[1][2]*m[1][2] + m[2][2]*m[2][2]).sqrt();

        let r02 = m[0][2] / sz;
        let r12 = m[1][2] / sz;
        let r22 = m[2][2] / sz;
        let r00 = m[0][0] / sx;
        let r01 = m[0][1] / sy;
        let r10 = m[1][0] / sx;
        let r11 = m[1][1] / sy;

        let ey = r02.asin();
        let (ex, ez) = if ey.cos().abs() > 1e-4 {
            ((-r12).atan2(r22), (-r01).atan2(r00))
        } else {
            (r10.atan2(r11), 0.0)
        };

        const DEG: f32 = 180.0 / std::f32::consts::PI;
        Self {
            position: [tx, ty, tz],
            rotation: [ex * DEG, ey * DEG, ez * DEG],
            scale:    [sx, sy, sz],
        }
    }
}

// ============================================================
//  ComponentSlot — 名前付きコンポーネント
// ============================================================

pub struct ComponentSlot {
    pub name:      String,
    pub component: Box<dyn Component>,
}

// ============================================================
//  ComponentSlotData — シリアライズ用（新旧フォーマット両対応）
// ============================================================

/// 新フォーマット: {"name":"...", "component":{"type":"...","data":{...}}}
/// 旧フォーマット: {"type":"...","data":{...}}
#[derive(Clone, Serialize)]
pub struct ComponentSlotData {
    pub name:      String,
    pub component: ComponentData,
}

impl<'de> Deserialize<'de> for ComponentSlotData {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where D: serde::Deserializer<'de> {
        let value = serde_json::Value::deserialize(deserializer)?;

        if value.get("component").is_some() {
            // 新フォーマット
            let name = value["name"].as_str().unwrap_or("Component").to_string();
            let comp: ComponentData = serde_json::from_value(value["component"].clone())
                .map_err(serde::de::Error::custom)?;
            return Ok(Self { name, component: comp });
        }

        // 旧フォーマット（ComponentData がそのまま）
        let comp: ComponentData = serde_json::from_value(value)
            .map_err(serde::de::Error::custom)?;
        let name = match &comp {
            ComponentData::ModelComponent(_)  => "Model",
            ComponentData::ScriptComponent(_) => "Script",
        }.to_string();
        Ok(Self { name, component: comp })
    }
}

// ============================================================
//  ActorData — シリアライズ用
// ============================================================

#[derive(Clone, Serialize, Deserialize)]
pub struct ActorData {
    pub name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub transform:  Option<ActorTransform>,
    pub components: Vec<ComponentSlotData>,
    pub children:   Vec<ActorData>,
}

// ============================================================
//  Actor
// ============================================================

pub struct Actor {
    pub name:       String,
    /// 所属する世界線 (0=通常シーン, N=アクター編集タブ)。シリアライズ対象外。
    pub world_line: u32,
    pub transform:  ActorTransform,
    components:     Vec<ComponentSlot>,
    pub children:   Vec<Actor>,
}

impl Actor {
    pub fn new() -> Self { Self::with_name("Actor") }

    pub fn with_name(name: impl Into<String>) -> Self {
        Self {
            name:       name.into(),
            world_line: 0,
            transform:  ActorTransform::default(),
            components: Vec::new(),
            children:   Vec::new(),
        }
    }

    // ─── コンポーネント操作 ────────────────────────────────────

    /// 型名から自動生成した名前でコンポーネントを追加する。
    pub fn add_component<T: Component + 'static>(&mut self, component: T) {
        let base = std::any::type_name::<T>()
            .split("::").last().unwrap_or("Component").to_string();
        let count = self.components.iter()
            .filter(|s| s.component.as_any().is::<T>())
            .count();
        let name = if count == 0 { base } else { format!("{base}_{count}") };
        self.components.push(ComponentSlot { name, component: Box::new(component) });
    }

    pub fn add_component_named<T: Component + 'static>(&mut self, name: String, component: T) {
        self.components.push(ComponentSlot { name, component: Box::new(component) });
    }

    pub fn get_component<T: Component + 'static>(&self) -> Option<&T> {
        self.components.iter()
            .find_map(|s| s.component.as_any().downcast_ref::<T>())
    }

    pub fn get_component_mut<T: Component + 'static>(&mut self) -> Option<&mut T> {
        self.components.iter_mut()
            .find_map(|s| s.component.as_any_mut().downcast_mut::<T>())
    }

    pub fn has_component<T: Component + 'static>(&self) -> bool {
        self.components.iter().any(|s| s.component.as_any().is::<T>())
    }

    pub fn component_slots(&self) -> &[ComponentSlot] { &self.components }
    pub fn component_slots_mut(&mut self) -> &mut Vec<ComponentSlot> { &mut self.components }

    pub fn remove_component_at(&mut self, idx: usize) {
        if idx < self.components.len() { self.components.remove(idx); }
    }

    /// スロット一覧をまるごと置き換える（undo/redo 用）。
    pub fn replace_components(&mut self, slots: Vec<ComponentSlot>) {
        self.components = slots;
    }

    /// world_line を自身と全子孫に再帰的に設定する（undo 復元用）。
    pub fn set_world_line_recursive(&mut self, wl: u32) {
        self.world_line = wl;
        for child in &mut self.children { child.set_world_line_recursive(wl); }
    }

    // ─── 親子関係 ────────────────────────────────────────────

    pub fn add_child(&mut self, child: Actor) { self.children.push(child); }
    pub fn children(&self) -> &[Actor] { &self.children }
    pub fn children_mut(&mut self) -> &mut Vec<Actor> { &mut self.children }

    // ─── フレームライフサイクル ──────────────────────────────

    pub fn begin_frame(&mut self, ctx: &FrameContext) {
        for c in &mut self.components { c.component.begin_frame(ctx); }
        for child in &mut self.children { child.begin_frame(ctx); }
    }
    pub fn early_update(&mut self, ctx: &FrameContext) {
        for c in &mut self.components { c.component.early_update(ctx); }
        for child in &mut self.children { child.early_update(ctx); }
    }
    pub fn update(&mut self, ctx: &FrameContext) {
        for c in &mut self.components { c.component.update(ctx); }
        for child in &mut self.children { child.update(ctx); }
    }
    pub fn constant_update(&mut self, ctx: &FrameContext) {
        for c in &mut self.components { c.component.constant_update(ctx); }
        for child in &mut self.children { child.constant_update(ctx); }
    }
    pub fn late_update(&mut self, ctx: &FrameContext) {
        for c in &mut self.components { c.component.late_update(ctx); }
        for child in &mut self.children { child.late_update(ctx); }
    }
    pub fn render(&mut self, ctx: &FrameContext) {
        for c in &mut self.components { c.component.render(ctx); }
        for child in &mut self.children { child.render(ctx); }
    }
    pub fn end_frame(&mut self, ctx: &FrameContext) {
        for c in &mut self.components { c.component.end_frame(ctx); }
        for child in &mut self.children { child.end_frame(ctx); }
    }

    // ─── シリアライズ ────────────────────────────────────────

    pub fn to_data(&self) -> ActorData {
        ActorData {
            name:       self.name.clone(),
            transform:  Some(self.transform.clone()),
            components: self.components.iter().map(|s| ComponentSlotData {
                name:      s.name.clone(),
                component: s.component.to_data(),
            }).collect(),
            children:   self.children.iter().map(|c| c.to_data()).collect(),
        }
    }
}

impl Default for Actor {
    fn default() -> Self { Self::new() }
}
