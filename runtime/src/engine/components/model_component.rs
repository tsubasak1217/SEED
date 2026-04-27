// ============================================================
//  model_component.rs — ModelComponent
//
//  モデル描画に必要なデータを保持する純粋データコンポーネント。
//  ライフサイクルロジックを持たない（GPU バッチ更新は System が担う）。
//
//  1 エンティティにつき 1 つの ModelComponent を持てる。
//  複数のモデルが必要な場合は子 Actor を作成してそれぞれに持たせる。
// ============================================================

use std::any::Any;
use std::sync::Arc;
use serde::{Deserialize, Serialize};
use crate::engine::ecs::Component;
use crate::engine::core::loader::model::Model;
use crate::engine::methods::drawer::{GpuModel, InstancedModelBatch};

// ─── 定数 ─────────────────────────────────────────────────────────────────────

/// グループ ID はこの値以上（インスタンスインデックスと衝突しない）
pub const GROUP_ID_BASE: u32 = 1_000_000;

fn default_next_group_id() -> u32 { GROUP_ID_BASE }

// ─── InstanceMeta ─────────────────────────────────────────────────────────────

/// インスタンスごとのメタデータ（ヒエラルキー・アニメーション）。
#[derive(Clone, Serialize, Deserialize)]
pub struct InstanceMeta {
    pub name:      String,
    pub parent:    Option<u32>,
    /// 削除/追加でインデックスが変わっても変化しない安定アニメーション位相シード。
    #[serde(default)]
    pub anim_seed: u32,
}

impl InstanceMeta {
    pub fn new(name: impl Into<String>) -> Self {
        Self { name: name.into(), parent: None, anim_seed: 0 }
    }
}

// ─── GroupMeta ────────────────────────────────────────────────────────────────

/// グループフォルダのメタデータ（描画なし・ヒエラルキー整理用）。
#[derive(Clone, Serialize, Deserialize)]
pub struct GroupMeta {
    pub id:     u32,
    pub name:   String,
    pub parent: Option<u32>,
}

// ─── ModelComponentData ───────────────────────────────────────────────────────

/// シリアライズ用データ（JSON 保存・Undo スナップショット）。
#[derive(Clone, Serialize, Deserialize)]
pub struct ModelComponentData {
    pub model_path:    String,
    pub instances:     Vec<[[f32; 4]; 4]>,
    #[serde(default)]
    pub meta:          Vec<InstanceMeta>,
    #[serde(default)]
    pub groups:        Vec<GroupMeta>,
    #[serde(default = "default_next_group_id")]
    pub next_group_id: u32,
}

// ─── ModelComponent ───────────────────────────────────────────────────────────

/// Actor にアタッチするモデルコンポーネント。
/// GPU リソース (GpuModel, InstancedModelBatch) を含む純粋データ構造。
pub struct ModelComponent {
    pub source_path:     String,
    /// CPU モデルデータ。Arc 共有でモデルキャッシュを実現する（同一パスの GPU リソース再生成コスト削減）。
    pub model:           Option<Arc<Model>>,
    pub gpu_model:       Option<GpuModel>,
    pub instanced_batch: Option<InstancedModelBatch>,
    pub instance_mats:   Vec<[[f32; 4]; 4]>,
    pub instance_meta:   Vec<InstanceMeta>,
    pub group_meta:      Vec<GroupMeta>,
    pub next_group_id:   u32,
}

impl ModelComponent {
    /// モデルが未設定の空コンポーネントを作成する。
    pub fn empty() -> Self {
        Self {
            source_path:     String::new(),
            model:           None,
            gpu_model:       None,
            instanced_batch: None,
            instance_mats:   Vec::new(),
            instance_meta:   Vec::new(),
            group_meta:      Vec::new(),
            next_group_id:   GROUP_ID_BASE,
        }
    }

    pub fn is_loaded(&self) -> bool { self.model.is_some() }

    /// instanced_batch に「次回更新が必要」フラグを立てる。
    pub fn mark_batch_dirty(&mut self) {
        if let Some(b) = &mut self.instanced_batch { b.mark_dirty(); }
    }

    pub fn set_batch_anim_seeds(&mut self, seeds: &[u32]) {
        if let Some(b) = &mut self.instanced_batch { b.set_anim_seeds(seeds); }
    }

    pub fn rendering_refs(&self) -> Option<(&GpuModel, &InstancedModelBatch)> {
        match (&self.gpu_model, &self.instanced_batch) {
            (Some(gpu), Some(batch)) => Some((gpu, batch)),
            _ => None,
        }
    }

    // ─── インスタンス階層ヘルパー ──────────────────────────────

    /// 指定インスタンスの直接の子インスタンスインデックス一覧を返す。
    pub fn children_of(&self, idx: u32) -> Vec<u32> {
        self.instance_meta.iter().enumerate()
            .filter(|(_, m)| m.parent == Some(idx))
            .map(|(i, _)| i as u32)
            .collect()
    }

    /// 指定インスタンスの全子孫インデックスを BFS で収集する。
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
    pub fn collect_non_root_descendants(&self, roots: &[u32]) -> Vec<(u32, [[f32; 4]; 4])> {
        let roots_set: std::collections::HashSet<u32> = roots.iter().copied().collect();
        let mut result = Vec::new();
        for &root in roots {
            self.collect_desc_inner(root, &roots_set, &mut result);
        }
        result
    }

    fn collect_desc_inner(
        &self,
        idx:       u32,
        roots_set: &std::collections::HashSet<u32>,
        result:    &mut Vec<(u32, [[f32; 4]; 4])>,
    ) {
        for child in self.children_of(idx) {
            if !roots_set.contains(&child) {
                if let Some(&mat) = self.instance_mats.get(child as usize) {
                    result.push((child, mat));
                }
                self.collect_desc_inner(child, roots_set, result);
            }
        }
    }

    // ─── シリアライズ ─────────────────────────────────────────

    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> ModelComponentData {
        ModelComponentData {
            model_path:    self.source_path.clone(),
            instances:     self.instance_mats.clone(),
            meta:          self.instance_meta.clone(),
            groups:        self.group_meta.clone(),
            next_group_id: self.next_group_id,
        }
    }
}

// ECS コンポーネントとして登録
impl Component for ModelComponent {}
