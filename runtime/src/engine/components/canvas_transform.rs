// ============================================================
//  canvas_transform.rs — 2D キャンバス空間トランスフォーム
//
//  Actor2D のデフォルトコンポーネント。
//  XY 平面上の位置・回転・スケールを保持する。
//  3D の Transform に相当するが Z 軸方向の移動を持たない。
// ============================================================

use serde::{Deserialize, Serialize};
use crate::engine::ecs::Component;

/// 2D キャンバス空間のトランスフォーム。
///
/// Actor2D が持つデフォルトコンポーネント。
/// 単位: position はワールドユニット（ピクセル換算はカメラの ortho スケールで決まる）。
/// rotation は Z 軸周りの回転（度）。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CanvasTransform {
    /// XY 平面上の位置（ワールドユニット）
    pub position: [f32; 2],
    /// Z 軸周りの回転（度）
    pub rotation: f32,
    /// XY スケール
    pub scale: [f32; 2],
}

impl CanvasTransform {
    pub fn new(position: [f32; 2], rotation: f32, scale: [f32; 2]) -> Self {
        Self { position, rotation, scale }
    }
}

impl Default for CanvasTransform {
    fn default() -> Self {
        Self {
            position: [0.0, 0.0],
            rotation: 0.0,
            scale:    [1.0, 1.0],
        }
    }
}

impl Component for CanvasTransform {}
