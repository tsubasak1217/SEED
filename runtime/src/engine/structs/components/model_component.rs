use std::any::Any;
use serde::{Deserialize, Serialize};
use crate::engine::core::loader::model::Model;
use crate::engine::methods::drawer::{GpuModel, InstancedModelBatch};
use super::{Component, ComponentData};

// ============================================================
//  定数
// ============================================================

/// グループ ID はこの値以上（インスタンスインデックスと衝突しない）
pub const GROUP_ID_BASE: u32 = 1_000_000;

fn default_next_group_id() -> u32 { GROUP_ID_BASE }

// ============================================================
//  InstanceMeta — インスタンスごとのメタデータ
// ============================================================

#[derive(Clone, Serialize, Deserialize)]
pub struct InstanceMeta {
    pub name:   String,
    pub parent: Option<u32>,
    /// 削除/追加でインデックスが変わっても変化しない安定位相シード。
    /// アニメーション位相オフセット計算に使用する。
    #[serde(default)]
    pub anim_seed: u32,
}

impl InstanceMeta {
    pub fn new(name: impl Into<String>) -> Self {
        Self { name: name.into(), parent: None, anim_seed: 0 }
    }
}

// ============================================================
//  GroupMeta — グループフォルダのメタデータ（描画なし）
// ============================================================

#[derive(Clone, Serialize, Deserialize)]
pub struct GroupMeta {
    pub id:     u32,
    pub name:   String,
    pub parent: Option<u32>,
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
    #[serde(default)]
    pub groups:     Vec<GroupMeta>,
    #[serde(default = "default_next_group_id")]
    pub next_group_id: u32,
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
    pub group_meta:      Vec<GroupMeta>,
    pub next_group_id:   u32,
}

impl ModelComponent {
    pub fn children_of(&self, idx: u32) -> Vec<u32> {
        self.instance_meta.iter().enumerate()
            .filter(|(_, m)| m.parent == Some(idx))
            .map(|(i, _)| i as u32)
            .collect()
    }

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

    /// 選択セットのうち「他の選択インスタンスの子孫でないもの」を返す（ルート選択）。
    /// 親が選択されている場合は子を除外する（親が動けば子は自動追従するため）。
    pub fn filter_selection_roots(&self, selected: &[u32]) -> Vec<u32> {
        let set: std::collections::HashSet<u32> = selected.iter().copied().collect();
        selected.iter().copied().filter(|&idx| {
            let mut cur = self.instance_meta.get(idx as usize).and_then(|m| m.parent);
            while let Some(p) = cur {
                if set.contains(&p) { return false; }
                cur = self.instance_meta.get(p as usize).and_then(|m| m.parent);
            }
            true
        }).collect()
    }

    /// roots の全子孫のうち roots 自身に含まれないものを (index, start_mat) で収集する。
    /// 選択されているが roots に含まれない（= 先祖が選択済み）インスタンスも含む。
    pub fn collect_non_root_descendants(
        &self,
        roots: &[u32],
    ) -> Vec<(u32, [[f32; 4]; 4])> {
        let roots_set: std::collections::HashSet<u32> = roots.iter().copied().collect();
        let mut result = Vec::new();
        for &root in roots {
            self.collect_desc_inner(root, &roots_set, &mut result);
        }
        result
    }

    fn collect_desc_inner(
        &self,
        idx: u32,
        roots_set: &std::collections::HashSet<u32>,
        result: &mut Vec<(u32, [[f32; 4]; 4])>,
    ) {
        for child in self.children_of(idx) {
            if !roots_set.contains(&child) {
                if let Some(&mat) = self.instance_mats.get(child as usize) {
                    result.push((child, mat));
                }
                self.collect_desc_inner(child, roots_set, result);
            }
            // child がルートなら skip — child 自身の delta で処理される
        }
    }
}

impl Component for ModelComponent {
    fn as_any(&self) -> &dyn Any { self }
    fn as_any_mut(&mut self) -> &mut dyn Any { self }

    fn to_data(&self) -> ComponentData {
        ComponentData::ModelComponent(ModelComponentData {
            model_path:    self.source_path.clone(),
            instances:     self.instance_mats.clone(),
            meta:          self.instance_meta.clone(),
            groups:        self.group_meta.clone(),
            next_group_id: self.next_group_id,
        })
    }
}
