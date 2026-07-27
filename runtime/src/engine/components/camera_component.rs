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

use crate::engine::ecs::Component;
use serde::{Deserialize, Serialize};

// ─── デフォルト値関数 ─────────────────────────────────────────────────────────

fn default_fov() -> f32 {
    45.0
}
fn default_near() -> f32 {
    0.1
}
fn default_far() -> f32 {
    1000.0
}
fn default_clear_color() -> [f32; 4] {
    [0.1, 0.1, 0.1, 1.0]
}
fn default_bar_color() -> [f32; 4] {
    [0.0, 0.0, 0.0, 1.0]
}
fn default_target_width() -> u32 {
    1920
}
fn default_target_height() -> u32 {
    1080
}
/// 正射投影時の縦方向の描画範囲（ワールド単位・全高）のデフォルト。
fn default_ortho_height() -> f32 {
    10.0
}

// ─── CameraProjection ────────────────────────────────────────────────────────

/// カメラの投影方式。
///
/// - `Perspective` : 透視投影（遠近感あり。fov_y_deg で画角を決める）
/// - `Orthographic`: 正射投影（平行投影・遠近感なし。ortho_height で縦の描画範囲を決める）
#[derive(Clone, Copy, Serialize, Deserialize, Default, PartialEq, Eq, Debug)]
#[serde(rename_all = "snake_case")]
pub enum CameraProjection {
    #[default]
    Perspective,
    Orthographic,
}

impl CameraProjection {
    /// serde/IPC で使用する文字列表現を返す。
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Perspective => "perspective",
            Self::Orthographic => "orthographic",
        }
    }

    /// 文字列表現から変換する。不明な値は `Perspective` にフォールバックする。
    pub fn from_str(s: &str) -> Self {
        match s.trim() {
            "orthographic" => Self::Orthographic,
            _ => Self::Perspective,
        }
    }
}

// ─── ScalingMode ─────────────────────────────────────────────────────────────

/// カメラのスケーリングモード。
///
/// ウィンドウサイズとゲーム解像度が異なる場合に、
/// ゲーム画面をウィンドウへどう収めるかを指定する。
#[derive(Clone, Serialize, Deserialize, Default, PartialEq, Eq, Debug)]
#[serde(rename_all = "snake_case")]
pub enum ScalingMode {
    /// 縦 FOV 固定・横をウィンドウに合わせて伸縮（デフォルト）
    #[default]
    VertMinus,
    /// 横 FOV 固定・縦をウィンドウに合わせて伸縮（target_width/height でベース横 FOV を決定）
    HorPlus,
    /// 横幅フィット・上下黒帯（target_width/height が必要）
    LetterBox,
    /// 縦幅フィット・左右黒帯（target_width/height が必要）
    PillarBox,
    /// アスペクト比保持・自動帯（横が広ければ左右帯、縦が長ければ上下帯）
    LetterPillarBox,
    /// target 比率のままウィンドウ全体に伸縮（黒帯なし・比率変化あり）
    FullScale,
}

impl ScalingMode {
    /// serde/IPC で使用する文字列表現を返す。
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::VertMinus => "vert_minus",
            Self::HorPlus => "hor_plus",
            Self::LetterBox => "letter_box",
            Self::PillarBox => "pillar_box",
            Self::LetterPillarBox => "letter_pillar_box",
            Self::FullScale => "full_scale",
        }
    }

    /// 文字列表現から変換する。不明な値は `VertMinus` にフォールバックする。
    pub fn from_str(s: &str) -> Self {
        match s.trim() {
            "hor_plus" => Self::HorPlus,
            "letter_box" => Self::LetterBox,
            "pillar_box" => Self::PillarBox,
            "letter_pillar_box" => Self::LetterPillarBox,
            "full_scale" => Self::FullScale,
            _ => Self::VertMinus,
        }
    }
}

// ─── CameraComponentData ─────────────────────────────────────────────────────

/// シリアライズ用データ（JSON 保存・Undo スナップショット）。
#[derive(Clone, Serialize, Deserialize)]
pub struct CameraComponentData {
    /// 垂直視野角（度）
    #[serde(default = "default_fov")]
    pub fov_y_deg: f32,
    /// ニアクリップ距離
    #[serde(default = "default_near")]
    pub near: f32,
    /// ファークリップ距離
    #[serde(default = "default_far")]
    pub far: f32,
    /// Play モードで使用するメインカメラか
    #[serde(default)]
    pub is_main: bool,
    /// 背景クリアカラー（RGBA, linear）
    #[serde(default = "default_clear_color")]
    pub clear_color: [f32; 4],
    /// スケーリングモード（ウィンドウとゲーム解像度の関係を指定）
    #[serde(default)]
    pub scaling_mode: ScalingMode,
    /// スケーリングのベース解像度（横）。HorPlus / LetterBox / PillarBox / FullScale 時に使用する。
    #[serde(default = "default_target_width")]
    pub target_width: u32,
    /// スケーリングのベース解像度（縦）。HorPlus / LetterBox / PillarBox / FullScale 時に使用する。
    #[serde(default = "default_target_height")]
    pub target_height: u32,
    /// LetterBox / PillarBox 時の帯カラー（RGBA, linear）。デフォルト黒。
    #[serde(default = "default_bar_color")]
    pub bar_color: [f32; 4],
    /// 投影方式（透視 / 正射）。
    #[serde(default)]
    pub projection: CameraProjection,
    /// 正射投影時の縦方向の描画範囲（ワールド単位・全高）。透視時は未使用。
    #[serde(default = "default_ortho_height")]
    pub ortho_height: f32,
    /// このカメラで描画するときに使うシェーディングアセット（WGSL ファイル）のパス。
    /// None のときはシーン既定 → 組み込み標準 PBR へフォールバックする。
    /// パスは `assets://` 仮想パスまたは絶対パス（engine/asset_fs.rs の規約）。
    #[serde(default)]
    pub shading_asset: Option<String>,
}

impl Default for CameraComponentData {
    fn default() -> Self {
        Self {
            fov_y_deg: default_fov(),
            near: default_near(),
            far: default_far(),
            is_main: false,
            clear_color: default_clear_color(),
            scaling_mode: ScalingMode::default(),
            target_width: default_target_width(),
            target_height: default_target_height(),
            bar_color: default_bar_color(),
            projection: CameraProjection::default(),
            ortho_height: default_ortho_height(),
            // 既定は未指定（シーン既定 → 組み込み標準 PBR にフォールバックする）
            shading_asset: None,
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
    pub fov_y_deg: f32,
    /// ニアクリップ距離
    pub near: f32,
    /// ファークリップ距離
    pub far: f32,
    /// Play モードで使用するメインカメラか
    pub is_main: bool,
    /// 背景クリアカラー（RGBA, linear）
    pub clear_color: [f32; 4],
    /// スケーリングモード（ウィンドウとゲーム解像度の関係を指定）
    pub scaling_mode: ScalingMode,
    /// スケーリングのベース解像度（横）
    pub target_width: u32,
    /// スケーリングのベース解像度（縦）
    pub target_height: u32,
    /// LetterBox / PillarBox 時の帯カラー（RGBA, linear）
    pub bar_color: [f32; 4],
    /// 投影方式（透視 / 正射）
    pub projection: CameraProjection,
    /// 正射投影時の縦方向の描画範囲（ワールド単位・全高）
    pub ortho_height: f32,
    /// このカメラで描画するときに使うシェーディングアセット（WGSL ファイル）のパス。
    /// None のときはシーン既定 → 組み込み標準 PBR へフォールバックする。
    /// パスは `assets://` 仮想パスまたは絶対パス（engine/asset_fs.rs の規約）。
    pub shading_asset: Option<String>,
}

impl CameraComponent {
    /// CameraComponentData からコンポーネントを構築する。
    pub fn from_data(data: CameraComponentData) -> Self {
        Self {
            fov_y_deg: data.fov_y_deg,
            near: data.near,
            far: data.far,
            is_main: data.is_main,
            clear_color: data.clear_color,
            scaling_mode: data.scaling_mode,
            target_width: data.target_width,
            target_height: data.target_height,
            bar_color: data.bar_color,
            projection: data.projection,
            ortho_height: data.ortho_height,
            shading_asset: data.shading_asset,
        }
    }

    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> CameraComponentData {
        CameraComponentData {
            fov_y_deg: self.fov_y_deg,
            near: self.near,
            far: self.far,
            is_main: self.is_main,
            clear_color: self.clear_color,
            scaling_mode: self.scaling_mode.clone(),
            target_width: self.target_width,
            target_height: self.target_height,
            bar_color: self.bar_color,
            projection: self.projection,
            ortho_height: self.ortho_height,
            shading_asset: self.shading_asset.clone(),
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
