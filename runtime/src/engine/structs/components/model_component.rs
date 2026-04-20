use std::any::Any;
use serde::{Deserialize, Serialize};
use crate::engine::core::loader::model::Model;
use crate::engine::methods::drawer::{GpuModel, InstancedModelBatch};
use super::{Component, ComponentData};

// ============================================================
//  InstanceMeta — インスタンスごとのメタデータ
// ============================================================

#[derive(Clone, Serialize, Deserialize)]
pub struct InstanceMeta {
    pub name:   String,
    pub parent: Option<u32>,
}

impl InstanceMeta {
    pub fn new(name: impl Into<String>) -> Self {
        Self { name: name.into(), parent: None }
    }
}

// ============================================================
//  ModelComponentData — シリアライズ用
// ============================================================

#[derive(Serialize, Deserialize)]
pub struct ModelComponentData {
    pub model_path: String,
    pub instances:  Vec<[[f32; 4]; 4]>,
    #[serde(default)]
    pub meta:       Vec<InstanceMeta>,
}

// ============================================================
//  ModelComponent
// ============================================================

pub struct ModelComponent {
    pub source_path:     String,
    pub model:           Model,
    pub gpu_model:       GpuModel,
    pub instanced_batch: InstancedModelBatch,
    pub instance_mats:   Vec<[[f32; 4]; 4]>,
    pub instance_meta:   Vec<InstanceMeta>,
}

impl ModelComponent {
    /// idx の直接の子インスタンスのインデックスを返す。
    pub fn children_of(&self, idx: u32) -> Vec<u32> {
        self.instance_meta.iter().enumerate()
            .filter(|(_, m)| m.parent == Some(idx))
            .map(|(i, _)| i as u32)
            .collect()
    }

    /// idx のすべての子孫インスタンスのインデックスを返す（BFS）。
    pub fn all_descendants(&self, root: u32) -> Vec<u32> {
        let mut result = Vec::new();
        let mut queue  = std::collections::VecDeque::new();
        queue.extend(self.children_of(root));
        while let Some(idx) = queue.pop_front() {
            result.push(idx);
            queue.extend(self.children_of(idx));
        }
        result
    }
}

impl Component for ModelComponent {
    fn as_any(&self) -> &dyn Any { self }
    fn as_any_mut(&mut self) -> &mut dyn Any { self }

    fn to_data(&self) -> ComponentData {
        ComponentData::ModelComponent(ModelComponentData {
            model_path: self.source_path.clone(),
            instances:  self.instance_mats.clone(),
            meta:       self.instance_meta.clone(),
        })
    }
}
