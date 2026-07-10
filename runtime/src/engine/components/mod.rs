// ============================================================
//  components/mod.rs — ゲームコンポーネント一覧
//
//  新しいコンポーネントを追加する手順:
//    1. このディレクトリに <name>.rs を作成し Component を impl する
//    2. ComponentKind に variant を追加する
//    3. ComponentData に対応する Data 型を追加する
//    4. Actor::to_data() / build_actor() に対応処理を追加する
// ============================================================

pub mod transform;
pub mod canvas_transform;
pub mod model_component;
pub mod script_component;
pub mod canvas_component;
pub mod sprite_component;
pub mod inputmap_component;
pub mod camera_component;
pub mod plugin_component;
pub mod collider_component;
pub mod collider2d_component;
pub mod rigidbody_component;
pub mod audio_component;
pub mod animator_component;

pub use transform::Transform;
pub use canvas_transform::CanvasTransform;
pub use model_component::{
    ModelComponent, ModelComponentData, ModelAnimDrive,
    InstanceMeta, GroupMeta, GROUP_ID_BASE,
};
pub use script_component::{
    ScriptComponent, PlaceholderScriptSlot, ScriptComponentData,
};
pub use canvas_component::{CanvasComponent, CanvasComponentData, CanvasViewportRef, AspectRatioAxis, GravityMode, CanvasDrawZone};
pub use sprite_component::{SpriteComponent, SpriteComponentData};
pub use inputmap_component::{InputMapComponent, InputMapComponentData};
pub use camera_component::{CameraComponent, CameraComponentData, ScalingMode, CameraProjection};
pub use plugin_component::{PluginComponent, PluginComponentData};
pub use collider_component::{ColliderComponent, ColliderComponentData, ColliderShapeData};
pub use collider2d_component::{
    Collider2dComponent, Collider2dComponentData, ColliderShape2dData,
};
// RigidbodyComponentData は旧フォーマットシーンの後方互換デシリアライズ専用
pub use rigidbody_component::RigidbodyComponentData;
pub use audio_component::{AudioComponent, AudioComponentData};
pub use animator_component::{AnimatorComponent, AnimatorComponentData, AnimClipRef, AnimClipKind, AnimClipLoop};

use serde::{Deserialize, Serialize};

// ─── ComponentKind ────────────────────────────────────────────────────────────

/// ゲームコンポーネントの種別列挙。
///
/// ComponentSlot が「どの型のコンポーネントか」を型消去なしで識別するために使う。
/// TypeId と異なりシリアライズ・表示が容易。
/// 新コンポーネントを追加したらここに variant を足すこと。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug, Serialize, Deserialize)]
pub enum ComponentKind {
    /// 3D モデルのインスタンス管理
    Model,
    /// C# スクリプト
    Script,
    /// CLR 不使用のエディタ専用プレースホルダー
    Placeholder,
    /// UI キャンバス（基準サイズ定義・矩形表示）
    Canvas,
    /// 2D スプライト（テクスチャ画像・キャンバス上表示）
    Sprite,
    /// 入力マップアセット参照（.inputmap ファイルへのリンク）
    InputMap,
    /// ゲームカメラ（Play モードの視点）
    Camera,
    /// 動的プラグインコンポーネント（Plugin::field_defs() でフィールドを定義）
    Plugin,
    /// 物理コライダー（衝突形状の定義、リジッドボディ設定を内包）
    Collider,
    /// 2D 物理コライダー（キャンバスアクター用、衝突形状・リジッドボディ設定を内包）
    Collider2d,
    /// オーディオソース（BGM/SE 再生、3D 距離減衰・パン対応）
    Audio,
    /// アニメーター（キーフレームアニメーションクリップの再生）
    Animator,
}

impl ComponentKind {
    /// エディタ表示用の型名を返す。
    pub fn display_name(self) -> &'static str {
        match self {
            Self::Model       => "ModelComponent",
            Self::Script      => "ScriptComponent",
            Self::Placeholder => "ScriptComponent (placeholder)",
            Self::Canvas      => "CanvasComponent",
            Self::Sprite      => "SpriteComponent",
            Self::InputMap    => "InputMapComponent",
            Self::Camera      => "CameraComponent",
            Self::Plugin      => "PluginComponent",
            Self::Collider    => "ColliderComponent",
            Self::Collider2d  => "Collider2dComponent",
            Self::Audio       => "AudioComponent",
            Self::Animator    => "AnimatorComponent",
        }
    }
}

// ─── ComponentData ────────────────────────────────────────────────────────────

/// コンポーネントのシリアライズ表現。
/// シーンファイル保存・Undo スナップショットに使用する。
#[derive(Clone, Serialize, Deserialize)]
#[serde(tag = "type", content = "data")]
pub enum ComponentData {
    ModelComponent(ModelComponentData),
    ScriptComponent(ScriptComponentData),
    CanvasComponent(CanvasComponentData),
    SpriteComponent(SpriteComponentData),
    InputMapComponent(InputMapComponentData),
    CameraComponent(CameraComponentData),
    /// 動的プラグインコンポーネント（plugin_name + fields を保持）
    PluginComponent(PluginComponentData),
    /// 物理コライダー（リジッドボディ設定を内包）
    ColliderComponent(ColliderComponentData),
    /// 2D 物理コライダー（キャンバスアクター用）
    Collider2dComponent(Collider2dComponentData),
    /// 旧フォーマット互換 — 読み込み時に ColliderComponent へ移行される
    #[serde(rename = "RigidbodyComponent")]
    LegacyRigidbodyComponent(RigidbodyComponentData),
    /// オーディオソース（BGM/SE 再生、3D 距離減衰・パン対応）
    AudioComponent(AudioComponentData),
    /// アニメーター（キーフレームアニメーションクリップの再生）
    AnimatorComponent(AnimatorComponentData),
}
