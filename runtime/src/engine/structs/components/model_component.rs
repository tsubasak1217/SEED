use std::any::Any;
use serde::{Deserialize, Serialize};
use crate::engine::core::loader::model::Model;
use crate::engine::methods::drawer::{GpuModel, InstancedModelBatch};
use super::{Component, ComponentData};

// ============================================================
//  ModelComponentData — シリアライズ用
// ============================================================

/// `ModelComponent` のシリアライズ表現。
/// GPU リソースは保存せず、ロード時に再構築するために必要な情報のみ持つ。
#[derive(Serialize, Deserialize)]
pub struct ModelComponentData {
    /// モデルファイルの相対パス
    pub model_path: String,
    /// 各インスタンスのルート変換行列（行優先）
    pub instances:  Vec<[[f32; 4]; 4]>,
}

// ============================================================
//  ModelComponent
// ============================================================

/// モデル描画に必要なリソースをまとめたコンポーネント。
pub struct ModelComponent {
    /// モデルファイルの相対パス（シーン保存・再ロード用）
    pub source_path:     String,
    pub model:           Model,
    pub gpu_model:       GpuModel,
    pub instanced_batch: InstancedModelBatch,
    /// 各インスタンスのルート変換行列（行優先）
    pub instance_mats:   Vec<[[f32; 4]; 4]>,
}

impl Component for ModelComponent {
    fn as_any(&self) -> &dyn Any { self }
    fn as_any_mut(&mut self) -> &mut dyn Any { self }

    fn to_data(&self) -> ComponentData {
        ComponentData::ModelComponent(ModelComponentData {
            model_path: self.source_path.clone(),
            instances:  self.instance_mats.clone(),
        })
    }
}
