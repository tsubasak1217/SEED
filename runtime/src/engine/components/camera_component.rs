// ============================================================
//  camera_component.rs — CameraComponent
//
//  シーン内の 3D カメラを表すコンポーネント。
//  Actor に貼り付けると、その Actor の Transform が
//  カメラの位置・向きとして使われる。
//
//  is_main = true のカメラが Play モードのメインカメラになる。
//  複数ある場合は DFS 順で最初に見つかったものを優先する。
// ============================================================

use serde::{Deserialize, Serialize};
use crate::engine::ecs::Component;

// ─── デフォルト値関数 ─────────────────────────────────────────────────────────

fn default_fov()         -> f32      { 45.0 }
fn default_near()        -> f32      { 0.1 }
fn default_far()         -> f32      { 1000.0 }
fn default_clear_color() -> [f32; 4] { [0.1, 0.1, 0.1, 1.0] }

// ─── CameraComponentData ─────────────────────────────────────────────────────

/// シリアライズ用データ（JSON 保存・Undo スナップショット）。
#[derive(Clone, Serialize, Deserialize)]
pub struct CameraComponentData {
    /// 垂直視野角（度）
    #[serde(default = "default_fov")]
    pub fov_y_deg:   f32,
    /// ニアクリップ距離
    #[serde(default = "default_near")]
    pub near:        f32,
    /// ファークリップ距離
    #[serde(default = "default_far")]
    pub far:         f32,
    /// Play モードで使用するメインカメラか
    #[serde(default)]
    pub is_main:     bool,
    /// 背景クリアカラー（RGBA, linear）
    #[serde(default = "default_clear_color")]
    pub clear_color: [f32; 4],
}

impl Default for CameraComponentData {
    fn default() -> Self {
        Self {
            fov_y_deg:   default_fov(),
            near:        default_near(),
            far:         default_far(),
            is_main:     false,
            clear_color: default_clear_color(),
        }
    }
}

// ─── CameraComponent ─────────────────────────────────────────────────────────

/// Actor にアタッチするカメラコンポーネント。
///
/// Actor の Transform（position / rotation）がカメラの視点になる。
/// `is_main = true` にした Camera Actor が Play モード・スタンドアロンモードで
/// 描画に使用されるメインカメラとして機能する。
pub struct CameraComponent {
    /// 垂直視野角（度）
    pub fov_y_deg:   f32,
    /// ニアクリップ距離
    pub near:        f32,
    /// ファークリップ距離
    pub far:         f32,
    /// Play モードで使用するメインカメラか
    pub is_main:     bool,
    /// 背景クリアカラー（RGBA, linear）— 将来の Clear Pass 対応用
    pub clear_color: [f32; 4],
}

impl CameraComponent {
    /// CameraComponentData からコンポーネントを構築する。
    pub fn from_data(data: CameraComponentData) -> Self {
        Self {
            fov_y_deg:   data.fov_y_deg,
            near:        data.near,
            far:         data.far,
            is_main:     data.is_main,
            clear_color: data.clear_color,
        }
    }

    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> CameraComponentData {
        CameraComponentData {
            fov_y_deg:   self.fov_y_deg,
            near:        self.near,
            far:         self.far,
            is_main:     self.is_main,
            clear_color: self.clear_color,
        }
    }
}

impl Default for CameraComponent {
    fn default() -> Self {
        Self::from_data(CameraComponentData::default())
    }
}

// ECS コンポーネントとして登録
impl Component for CameraComponent {}
