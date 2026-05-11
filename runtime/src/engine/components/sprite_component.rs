// ============================================================
//  sprite_component.rs — 2D スプライトコンポーネント
//
//  2D キャンバス上に画像（テクスチャ）を表示するコンポーネント。
//  必ず CanvasComponent を持つアクターの子アクターにアタッチする。
//  CanvasTransform と組み合わせてキャンバス座標系内の
//  位置・回転・スケールを制御する。
//
//  【配置ルール】
//  - 単体アクターに追加 → 自動で Canvas を追加し、子アクターを生成してそこに配置
//  - Canvas 持ちアクターを選択して追加 → 新規子アクターを生成してそこに配置
//  - Canvas の子アクターを選択して追加 → 選択アクターに直接追加
// ============================================================

use serde::{Deserialize, Serialize};
use crate::engine::ecs::Component;

// ─── SpriteComponentData ─────────────────────────────────────────────────────

/// SpriteComponent のシリアライズ用データ。
#[derive(Clone, Serialize, Deserialize)]
pub struct SpriteComponentData {
    /// テクスチャファイルパス（空文字列 = テクスチャなし、単色表示）
    pub texture_path: String,
    /// 表示カラー（RGBA 正規化値 [r, g, b, a]）
    pub color: [f32; 4],
    /// スプライト表示幅（キャンバスユニット）
    pub width: f32,
    /// スプライト表示高さ（キャンバスユニット）
    pub height: f32,
}

// ─── SpriteComponent ─────────────────────────────────────────────────────────

/// 2D スプライトコンポーネント。
///
/// Actor2D にアタッチして、キャンバス上に画像を表示する。
/// CanvasTransform が示す位置・回転・スケールで描画領域が決まる。
/// 親アクターに CanvasComponent が必要。
///
/// # 座標系
/// CanvasTransform の position は親 Canvas を基準とした相対座標。
/// to_mat4_sized(width, height) でスプライト領域の変換行列を計算する。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SpriteComponent {
    /// テクスチャファイルパス（空文字列 = テクスチャなし、単色表示）
    pub texture_path: String,
    /// 表示カラー（RGBA 正規化値 [r, g, b, a]）
    pub color: [f32; 4],
    /// スプライト表示幅（キャンバスユニット）
    pub width: f32,
    /// スプライト表示高さ（キャンバスユニット）
    pub height: f32,
}

impl SpriteComponent {
    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> SpriteComponentData {
        SpriteComponentData {
            texture_path: self.texture_path.clone(),
            color:        self.color,
            width:        self.width,
            height:       self.height,
        }
    }
}

impl Default for SpriteComponent {
    fn default() -> Self {
        // デフォルトは白色・100×100 キャンバスユニット・テクスチャなし
        Self {
            texture_path: String::new(),
            color:        [1.0, 1.0, 1.0, 1.0],
            width:        100.0,
            height:       100.0,
        }
    }
}

impl Component for SpriteComponent {}
