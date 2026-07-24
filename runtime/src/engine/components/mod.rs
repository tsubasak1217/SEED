// ============================================================
//  components/mod.rs — ゲームコンポーネント一覧
//
//  新しいコンポーネントを追加する手順:
//    1. このディレクトリに <name>.rs を作成し Component を impl する
//    2. ComponentKind に variant を追加する
//    3. ComponentData に対応する Data 型を追加する
//    4. Actor::to_data() / build_actor() に対応処理を追加する
// ============================================================

pub mod animator_component;
pub mod audio_component;
pub mod camera_component;
pub mod canvas_component;
pub mod canvas_transform;
pub mod collider2d_component;
pub mod collider_component;
pub mod inputmap_component;
pub mod jointattach_component;
pub mod light_component;
/// マテリアルオーバーライド（Phase R7: .mat マテリアル＋マルチマテリアル編集）
pub mod material_override;
pub mod model_component;
pub mod particle_emitter_component;
pub mod plugin_component;
pub mod rigidbody_component;
pub mod script_component;
pub mod skybox_component;
pub mod sprite_component;
/// 地形チャンク（ボクセル地形の 1 チャンク識別＋.tvox 永続化リンク・内部管理用）
pub mod terrain_component;
pub mod transform;

pub use camera_component::{CameraComponent, CameraComponentData, CameraProjection, ScalingMode};
pub use canvas_component::{
    AspectRatioAxis, CanvasComponent, CanvasComponentData, CanvasDrawZone, CanvasViewportRef,
    GravityMode,
};
pub use canvas_transform::CanvasTransform;
pub use collider_component::{ColliderComponent, ColliderComponentData, ColliderShapeData};
pub use collider2d_component::{Collider2dComponent, Collider2dComponentData, ColliderShape2dData};
pub use inputmap_component::{InputMapComponent, InputMapComponentData};
pub use material_override::{MaterialOverride, MaterialOverrideKind, overrides_signature};
pub use model_component::{
    GROUP_ID_BASE, GroupMeta, InstanceMeta, ModelAnimDrive, ModelComponent, ModelComponentData,
    next_batch_instance_id,
};
pub use plugin_component::{PluginComponent, PluginComponentData};
pub use script_component::{PlaceholderScriptSlot, ScriptComponent, ScriptComponentData};
pub use sprite_component::{SpriteComponent, SpriteComponentData};
pub use transform::Transform;
// RigidbodyComponentData は旧フォーマットシーンの後方互換デシリアライズ専用
pub use animator_component::{
    AnimClipKind, AnimClipLoop, AnimClipRef, AnimatorComponent, AnimatorComponentData,
};
pub use audio_component::{AudioComponent, AudioComponentData};
pub use jointattach_component::{JointAttachComponent, JointAttachComponentData};
pub use light_component::{LightComponent, LightComponentData, LightKind};
pub use particle_emitter_component::{
    CURVE_LUT_SAMPLES, CurveChannel, CurveInterp, CurveKey,
    DIRECTION_RANDOMNESS_MAX_HALF_ANGLE_DEG, EmitMode, MAX_PARTICLE_MODEL_VERTS,
    MAX_PARTICLE_TEXTURES, MAX_PARTICLES_PER_EMITTER, ParamCurve, ParticleBlend,
    ParticleEmitterComponent, ParticleEmitterComponentData, ParticleShape, ParticleSimSpace,
    SpawnVolume,
};
pub use rigidbody_component::RigidbodyComponentData;
pub use skybox_component::{SkyboxComponent, SkyboxComponentData, SkyboxMode};
pub use terrain_component::{
    TERRAIN_SOURCE_SCHEME, TerrainChunkComponent, TerrainChunkComponentData,
};

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
    /// ライト（光源：directional / point / spot / rect）
    Light,
    /// ジョイントアタッチ（別モデルのジョイントへ追従するソケット機構）
    JointAttach,
    /// パーティクルエミッタ（GPU パーティクルの放出源）
    ParticleEmitter,
    /// スカイボックス（天球：equirectangular 背景・CameraLocked / WorldAnchored）
    Skybox,
    /// 地形チャンク（ボクセル地形の 1 チャンク・内部管理用。ユーザー追加不可）
    TerrainChunk,
}

impl ComponentKind {
    /// エディタ表示用の型名を返す。
    pub fn display_name(self) -> &'static str {
        match self {
            Self::Model => "ModelComponent",
            Self::Script => "ScriptComponent",
            Self::Placeholder => "ScriptComponent (placeholder)",
            Self::Canvas => "CanvasComponent",
            Self::Sprite => "SpriteComponent",
            Self::InputMap => "InputMapComponent",
            Self::Camera => "CameraComponent",
            Self::Plugin => "PluginComponent",
            Self::Collider => "ColliderComponent",
            Self::Collider2d => "Collider2dComponent",
            Self::Audio => "AudioComponent",
            Self::Animator => "AnimatorComponent",
            Self::Light => "LightComponent",
            Self::JointAttach => "JointAttachComponent",
            Self::ParticleEmitter => "ParticleEmitterComponent",
            Self::Skybox => "SkyboxComponent",
            Self::TerrainChunk => "TerrainChunkComponent",
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
    /// ライト（光源：directional / point / spot / rect）
    LightComponent(LightComponentData),
    /// ジョイントアタッチ（別モデルのジョイントへ追従するソケット機構）
    JointAttachComponent(JointAttachComponentData),
    /// パーティクルエミッタ（GPU パーティクルの放出源）
    ParticleEmitterComponent(ParticleEmitterComponentData),
    /// スカイボックス（天球：equirectangular 背景）
    SkyboxComponent(SkyboxComponentData),
    /// 地形チャンク（ボクセル地形の 1 チャンク識別＋.tvox リンク・内部管理用）
    TerrainChunkComponent(TerrainChunkComponentData),
}
