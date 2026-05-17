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
    /// 子UIのサイズをキャンバスのスケールに追従させるか。false = サイズ固定。
    #[serde(default)]
    pub scale_size:      bool,
    /// 子UIのトランスフォーム（位置）をキャンバスのスケールに追従させるか。false = 絶対座標固定。
    #[serde(default)]
    pub scale_transform: bool,
    /// 画面サイズに自動スケール。親キャンバスを持たないルートキャンバスにのみ有効。
    /// true のとき、ビューポートサイズ変化に応じて子 UI を proportional にスケールする。
    #[serde(default = "default_auto_scale")]
    pub auto_scale: bool,
}

fn default_auto_scale() -> bool { true }

// ─── CanvasComponent ─────────────────────────────────────────────────────────

/// UI キャンバスコンポーネント。
///
/// Actor2D にアタッチして UI レイアウトの基準サイズを指定する。
/// エディタ上では CanvasTransform.position を中心に width × height の
/// 矩形アウトラインが表示される。
///
/// # スケールモード
/// キャンバスのスケール（CanvasTransform.scale）が変化したとき、
/// 直接の子 UI の挙動を以下の 2 フラグで制御する。
///   - scale_transform: true → 子の位置にスケールを乗算（10,10 → 20,20）
///   - scale_size:      true → 子のサイズにスケールを乗算（100px → 200px）
/// 回転は常に追従する。デフォルトは両方 false（絶対座標・絶対サイズ）。
///
/// # 画面サイズ自動スケール
/// auto_scale=true（デフォルト）かつ親キャンバスを持たないルートキャンバスのとき、
/// ビューポートサイズに応じて子 UI を proportional にスケールする。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CanvasComponent {
    /// キャンバスの基準幅（ワールドユニット）
    pub width:  f32,
    /// キャンバスの基準高さ（ワールドユニット）
    pub height: f32,
    /// 子UIのサイズをキャンバスのスケールに追従させるか。false = サイズ固定。
    #[serde(default)]
    pub scale_size:      bool,
    /// 子UIのトランスフォーム（位置）をキャンバスのスケールに追従させるか。false = 絶対座標固定。
    #[serde(default)]
    pub scale_transform: bool,
    /// 画面サイズに自動スケール（デフォルト true）。
    /// 親キャンバスを持たないルートキャンバスにのみ有効。
    #[serde(default = "default_auto_scale")]
    pub auto_scale: bool,
}

impl CanvasComponent {
    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> CanvasComponentData {
        CanvasComponentData {
            width:           self.width,
            height:          self.height,
            scale_size:      self.scale_size,
            scale_transform: self.scale_transform,
            auto_scale:      self.auto_scale,
        }
    }
}

impl Default for CanvasComponent {
    fn default() -> Self {
        // デフォルトは 1920 × 1080（一般的な UI デザイン解像度）
        // スケールモードはデフォルトで両方 false（絶対座標・絶対サイズ）
        Self { width: 1920.0, height: 1080.0, scale_size: false, scale_transform: false, auto_scale: true }
    }
}

impl Component for CanvasComponent {}
