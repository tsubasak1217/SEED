// ============================================================
//  skybox_component.rs — スカイボックス（天球）コンポーネント
//
//  Actor に「天球（背景の全周画像）」を持たせる ECS スロットコンポーネント。
//  本コンポーネントは天球のパラメータ（テクスチャ・配置モード・強度・色味）の
//  データのみを保持する（ECS 理念：データとロジックの分離）。実際の描画は
//  レンダラ側の renderer/skybox.rs（SkyboxSystem）＋ skybox_ops.rs が毎フレーム行う。
//
//  【テクスチャ形式】
//  v1 は equirectangular（正距円筒）画像 1 枚を推奨実装とする（assets:// 仮想パス）。
//  シェーダが「サンプル方向ベクトル → 緯度経度 UV」へ変換して球面へ貼る。
//  ※ キューブマップ 6 枚対応は将来 TODO（テクスチャ形式の分岐が必要）。
//
//  【配置モード（SkyboxMode）】
//   - CameraLocked（既定）: 常にカメラ位置を中心とする無限遠の天球。ビュー行列の
//     平行移動を除去し、深度は far 付近（depth write off・LessEqual）で描く。標準スカイボックス。
//   - WorldAnchored: アクターの Transform（位置・回転・スケール）で配置される内向き球として
//     ワールドに実在する（深度あり・接近／内外移動可能）。
//
//  【シーンに複数ある場合】
//   - CameraLocked は最初の 1 つだけが有効（2 つ目以降は警告して無視。skybox_ops で判定）。
//   - WorldAnchored は複数配置できる（それぞれワールド上の実体のため）。
//
//  【シリアライズ】
//  全フィールドに #[serde(default)]（非ゼロ既定は default fn）を付け、旧 .scene
//  （フィールド欠落）でも読み込みが失敗しないようにする。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

// ─── デフォルト値関数 ─────────────────────────────────────────
// マジックナンバー禁止のため、非ゼロ既定値はすべて関数に切り出す。

/// intensity（テクスチャ色への乗算強度）の既定値。1.0＝素の色。
/// HDR メインパスへ描くため 1.0 超で発光的になり Bloom と連動する。
fn default_intensity() -> f32 {
    1.0
}
/// tint（色味乗算）の既定値（白＝素通し）。
fn default_tint() -> [f32; 3] {
    [1.0, 1.0, 1.0]
}

// ─── SkyboxMode ──────────────────────────────────────────────

/// スカイボックスの配置モード。
///
/// - `CameraLocked` : カメラ位置中心・無限遠の標準スカイボックス（既定）。
/// - `WorldAnchored`: アクターの Transform で配置される内向き球（ワールドに実在）。
///
/// serde は snake_case でシリアライズする（`"camera_locked"` / `"world_anchored"`）。
/// 旧シーン（mode 欠落）は `#[serde(default)]` により CameraLocked になる。
#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Debug)]
#[serde(rename_all = "snake_case")]
pub enum SkyboxMode {
    /// カメラ位置中心・無限遠（既定）
    CameraLocked,
    /// アクター Transform で配置される内向き球
    WorldAnchored,
}

impl Default for SkyboxMode {
    /// mode 省略時の既定は CameraLocked（標準スカイボックス）。
    fn default() -> Self {
        SkyboxMode::CameraLocked
    }
}

impl SkyboxMode {
    /// GPU（シェーダ）へ渡すモードコード。skybox.wgsl の `SKYBOX_MODE_*` と一致させること。
    pub fn to_code(self) -> u32 {
        match self {
            SkyboxMode::CameraLocked => 0,
            SkyboxMode::WorldAnchored => 1,
        }
    }

    /// IPC 文字列（インスペクタのドロップダウン Tag）→ enum。
    pub fn from_str_opt(s: &str) -> Option<Self> {
        match s {
            "camera_locked" => Some(SkyboxMode::CameraLocked),
            "world_anchored" => Some(SkyboxMode::WorldAnchored),
            _ => None,
        }
    }

    /// インスペクタ／シリアライズへ送るモード文字列。
    pub fn as_str(self) -> &'static str {
        match self {
            SkyboxMode::CameraLocked => "camera_locked",
            SkyboxMode::WorldAnchored => "world_anchored",
        }
    }
}

// ─── SkyboxComponentData（シリアライズ用）─────────────────────

/// SkyboxComponent のシリアライズ用データ（.scene 保存・Undo スナップショット）。
#[derive(Clone, Serialize, Deserialize, Debug)]
pub struct SkyboxComponentData {
    /// equirectangular 画像への参照（assets:// 仮想パス。空は「未設定＝描画しない」）。
    #[serde(default)]
    pub texture_path: String,
    /// 配置モード（既定 CameraLocked）。
    #[serde(default)]
    pub mode: SkyboxMode,
    /// 強度（テクスチャ色への乗算。既定 1.0。>1 で発光的＝Bloom 連動）。
    #[serde(default = "default_intensity")]
    pub intensity: f32,
    /// 色味（リニア RGB 乗算。既定 白）。
    #[serde(default = "default_tint")]
    pub tint: [f32; 3],
}

impl Default for SkyboxComponentData {
    fn default() -> Self {
        Self {
            texture_path: String::new(),
            mode: SkyboxMode::default(),
            intensity: default_intensity(),
            tint: default_tint(),
        }
    }
}

// ─── SkyboxComponent（ECS 実体）──────────────────────────────

/// スカイボックスコンポーネント（ECS 実体）。
///
/// 保持するのは天球のパラメータのみ。位置・向き・スケール（WorldAnchored 用）は
/// Actor の Transform からレンダラが毎フレーム解決する。揮発状態は持たない。
#[derive(Clone, Debug)]
pub struct SkyboxComponent {
    pub texture_path: String,
    pub mode: SkyboxMode,
    pub intensity: f32,
    pub tint: [f32; 3],
}

impl SkyboxComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    pub fn from_data(data: SkyboxComponentData) -> Self {
        Self {
            texture_path: data.texture_path,
            mode: data.mode,
            intensity: data.intensity,
            tint: data.tint,
        }
    }

    /// シリアライズ用データへ変換する。
    pub fn to_data(&self) -> SkyboxComponentData {
        SkyboxComponentData {
            texture_path: self.texture_path.clone(),
            mode: self.mode,
            intensity: self.intensity,
            tint: self.tint,
        }
    }
}

impl Default for SkyboxComponent {
    fn default() -> Self {
        Self::from_data(SkyboxComponentData::default())
    }
}

impl Component for SkyboxComponent {}
