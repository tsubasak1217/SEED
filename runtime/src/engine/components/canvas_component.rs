// ============================================================
//  canvas_component.rs — UI キャンバスコンポーネント
//
//  Actor2D にアタッチすることでキャンバスの基準サイズを定義する。
//  エディタ上では width × height の矩形アウトラインが描画される。
//  ゲームランタイムではこのサイズをレイアウト基準として UI を配置する。
// ============================================================

use serde::{Deserialize, Serialize};
use crate::engine::ecs::Component;

// ─── CanvasComponentData ──────────────────────────────────────────────────────

/// CanvasComponent のシリアライズ用データ。
#[derive(Clone, Serialize, Deserialize)]
pub struct CanvasComponentData {
    /// キャンバスの基準幅（ワールドユニット）
    pub width:  f32,
    /// キャンバスの基準高さ（ワールドユニット）
    pub height: f32,
}

// ─── CanvasComponent ─────────────────────────────────────────────────────────

/// UI キャンバスコンポーネント。
///
/// Actor2D にアタッチして UI レイアウトの基準サイズを指定する。
/// エディタ上では CanvasTransform.position を中心に width × height の
/// 矩形アウトラインが表示される。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CanvasComponent {
    /// キャンバスの基準幅（ワールドユニット）
    pub width:  f32,
    /// キャンバスの基準高さ（ワールドユニット）
    pub height: f32,
}

impl CanvasComponent {
    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> CanvasComponentData {
        CanvasComponentData { width: self.width, height: self.height }
    }
}

impl Default for CanvasComponent {
    fn default() -> Self {
        // デフォルトは 1920 × 1080（一般的な UI デザイン解像度）
        Self { width: 1920.0, height: 1080.0 }
    }
}

impl Component for CanvasComponent {}
