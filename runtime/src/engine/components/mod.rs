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
/// 「子を切り抜く」コンポーネント（UI の切り抜き。W2-1a）
pub mod canvas_clip_component;
/// レイアウトの部品が共有する値の型（向き・揃え・余白。W2-1b）
pub mod canvas_layout_params;
/// 縦・横に並べるコンテナ（W2-1b）
pub mod canvas_stack_component;
/// 折り返して並べるコンテナ（W2-1b）
pub mod canvas_wrap_component;
/// 格子に並べるコンテナ（W2-1b）
pub mod canvas_grid_component;
/// レイアウトの子の側の指定（伸ばす重み・大きさの上下限・揃えの上書き・無視・親に合わせる。W2-1b）
pub mod canvas_layout_item_component;
/// 安全領域の部品（ノードの領域を Screen.SafeArea の内側へ縮める。W2-1b）
pub mod canvas_safe_area_component;
/// ジェスチャーを受けるノード（タップ・長押し・ドラッグ・フリックの旗・ドラッグの軸・押下の見た目・最小のヒット領域。W2-2）
pub mod canvas_gesture_component;
/// スクロールの領域（向き・端の跳ね返りか止める・慣性・スナップ・入れ子・中身の大きさ・見える範囲の外を飛ばす。W2-3）
pub mod canvas_scroll_component;
pub mod sprite_component;
/// メッシュ変形スキニング 2D スプライト（Phase A1: Spine 風メッシュ変形の土台）
pub mod skinned_sprite_component;
pub mod inputmap_component;
pub mod camera_component;
pub mod plugin_component;
pub mod collider_component;
pub mod collider2d_component;
pub mod rigidbody_component;
pub mod audio_component;
/// 音声辞書（「グループ/用途」キー → 音声ファイル・既定音量の対応表）
pub mod audio_dictionary_component;
pub mod animator_component;
pub mod light_component;
pub mod jointattach_component;
pub mod skybox_component;
pub mod particle_emitter_component;
/// マテリアルオーバーライド（Phase R7: .mat マテリアル＋マルチマテリアル編集）
pub mod material_override;
/// 地形チャンク（ボクセル地形の 1 チャンク識別＋.tvox 永続化リンク・内部管理用）
pub mod terrain_component;
/// 水ボリューム（Phase W: 大洋 / 直方体水塊 / 川スプライン(W4) の定義）
pub mod water_volume_component;
// 水位グラフのリンク（開口。Phase W2.5）
pub mod water_link_component;
/// インタラクションソース（Phase I1: 動く物が草・水・雪泥の共有場へ書き込む宣言）
pub mod interaction_source_component;
/// カバーエミッタ（Phase I3.1: 地表カバー場へ雪・落ち葉・濡れを降らせる宣言）
pub mod cover_emitter_component;
/// コントロールポイント（汎用パス: 川・巡回ルート・カメラパスの共通土台）
pub mod control_point_component;
/// 3D ポリライン描画（釣り糸・ロープ・軌跡。スクリプトから毎フレーム点列を差し替える）
pub mod line_renderer_component;
/// キャンバス用テキスト表示（HUD の数値・ラベル。SDF フォント描画を流用）
pub mod text_component;
pub mod text_slots;

pub use transform::Transform;
pub use canvas_transform::CanvasTransform;
pub use model_component::{
    ModelComponent, ModelComponentData, ModelAnimDrive,
    InstanceMeta, GroupMeta, GROUP_ID_BASE, next_batch_instance_id,
};
pub use material_override::{MaterialOverride, MaterialOverrideKind, overrides_signature};
pub use script_component::{
    ScriptComponent, PlaceholderScriptSlot, ScriptComponentData,
};
pub use canvas_component::{CanvasComponent, CanvasComponentData, CanvasViewportRef, AspectRatioAxis, GravityMode, CanvasDrawZone, CanvasUnit};
pub use canvas_clip_component::{CanvasClipComponent, CanvasClipComponentData};
pub use canvas_layout_params::{
    CanvasPadding, CrossAlign, HiddenChildren, IndexedEnum, ItemAlign, LayoutDirection, MainAlign,
};
pub use canvas_stack_component::{CanvasStackComponent, CanvasStackComponentData};
pub use canvas_wrap_component::{CanvasWrapComponent, CanvasWrapComponentData};
pub use canvas_grid_component::{CanvasGridComponent, CanvasGridComponentData};
pub use canvas_layout_item_component::{CanvasLayoutItemComponent, CanvasLayoutItemComponentData};
pub use canvas_safe_area_component::{CanvasSafeAreaComponent, CanvasSafeAreaComponentData};
pub use canvas_gesture_component::{
    CanvasGestureComponent, CanvasGestureComponentData, GestureDragAxis, DEFAULT_MIN_HIT_SIZE_DP,
};
pub use canvas_scroll_component::{
    CanvasScrollComponent, CanvasScrollComponentData, ScrollContentSize, ScrollDirection, ScrollEdge, ScrollSnap,
};
pub use sprite_component::{SpriteComponent, SpriteComponentData};
pub use skinned_sprite_component::{SkinnedSpriteComponent, SkinnedSpriteComponentData};
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
pub use audio_dictionary_component::{
    AudioDictEntry, AudioDictGroup, AudioDictionaryComponent, AudioDictionaryComponentData,
    AUDIO_DICT_KEY_SEPARATOR, DEFAULT_AUDIO_DICT_VOLUME,
    MAX_AUDIO_DICT_ENTRIES_PER_GROUP, MAX_AUDIO_DICT_GROUPS,
};
pub use terrain_component::{TerrainChunkComponent, TerrainChunkComponentData, TERRAIN_SOURCE_SCHEME};
pub use animator_component::{AnimatorComponent, AnimatorComponentData, AnimClipRef, AnimClipKind, AnimClipLoop};
pub use light_component::{LightComponent, LightComponentData, LightKind};
pub use jointattach_component::{JointAttachComponent, JointAttachComponentData};
pub use skybox_component::{
    SkyboxComponent, SkyboxComponentData, SkyboxMode,
    SKY_ADJUST_MAX, SKY_ADJUST_MIN, SKY_HUE_SHIFT_MAX_DEG, SKY_HUE_SHIFT_MIN_DEG,
};
pub use water_volume_component::{WaterVolumeComponent, WaterVolumeComponentData, WaterVolumeKind};
pub use water_link_component::{WaterLinkComponent, WaterLinkComponentData};
pub use interaction_source_component::{InteractionSourceComponent, InteractionSourceComponentData};
pub use cover_emitter_component::{
    CoverEmitterComponent, CoverEmitterComponentData, CoverEmitterRangeKind,
};
pub use control_point_component::{
    ControlPoint, ControlPointComponent, ControlPointComponentData, ControlPointInterp,
    DEFAULT_TIME_STEP as CONTROL_POINT_DEFAULT_TIME_STEP, MAX_CONTROL_POINTS,
};
pub use line_renderer_component::{
    LineRendererComponent, LineRendererComponentData, MAX_LINE_POINTS,
};
pub use text_slots::{
    SlotValue, TextSlotData, TextSlotKind, remap_slots,
    DEFAULT_SLOT_COLOR, DEFAULT_SLOT_NUM, SLOT_COLOR_COMPONENTS,
};
pub use text_component::{
    TextComponent, TextComponentData, TextAlign, TextVerticalAlign, MAX_TEXT_CHARS,
    MAX_OUTLINE_WIDTH, MIN_OUTLINE_WIDTH,
    // 枠・折り返し・太さ・ドロップシャドウの入力範囲（IPC の clamp が参照する）
    MIN_BOX_SIZE, MAX_BOX_SIZE, MAX_TEXT_WEIGHT, MAX_SHADOW_OFFSET,
    MIN_SHADOW_SOFTNESS, MAX_SHADOW_SOFTNESS,
};
pub use particle_emitter_component::{
    ParticleEmitterComponent, ParticleEmitterComponentData,
    ParticleBlend, ParticleSimSpace, ParticleShape, SpawnVolume, EmitMode,
    ParamCurve, CurveChannel, CurveKey, CurveInterp,
    MAX_PARTICLES_PER_EMITTER, MAX_PARTICLE_MODEL_VERTS, CURVE_LUT_SAMPLES,
    DIRECTION_RANDOMNESS_MAX_HALF_ANGLE_DEG, MAX_PARTICLE_TEXTURES,
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
    /// メッシュ変形スキニング 2D スプライト（.sprite_mesh + ボーン子アクター）
    SkinnedSprite,
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
    /// 音声辞書（「グループ/用途」キー → 音声ファイル・既定音量の対応表）
    AudioDictionary,
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
    /// 水ボリューム（大洋 / 直方体水塊 / 川スプライン(W4)）
    WaterVolume,
    /// 水位グラフのリンク＝開口（扉・窓・穴・バルブ。W2.5）
    WaterLink,
    /// インタラクションソース（動く物 → 瞬発場への書き手。草の揺れ・水の波紋を駆動）
    InteractionSource,
    /// カバーエミッタ（地表カバー場への書き手。雪・落ち葉・濡れを降らせる。I3.1）
    CoverEmitter,
    /// コントロールポイント（汎用パス。川・巡回ルート・カメラパスが共用する点列）
    ControlPoint,
    /// 3D ポリライン描画（釣り糸・ロープ・軌跡）
    LineRenderer,
    /// キャンバス用テキスト表示（HUD の数値・ラベル）
    Text,
    /// 子を切り抜く（ノードのレイアウトの矩形で子孫を切り抜く。UI のスクロール領域・一覧用。W2-1a）
    CanvasClip,
    /// 縦・横に並べるコンテナ（W2-1b）
    CanvasStack,
    /// 折り返して並べるコンテナ（W2-1b）
    CanvasWrap,
    /// 格子に並べるコンテナ（W2-1b）
    CanvasGrid,
    /// レイアウトの子の側の指定（W2-1b）
    CanvasLayoutItem,
    /// 安全領域の部品（W2-1b）
    CanvasSafeArea,
    /// ジェスチャーを受けるノード（W2-2）
    CanvasGesture,
    /// スクロールの領域（W2-3）
    CanvasScroll,
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
            Self::SkinnedSprite => "SkinnedSpriteComponent",
            Self::InputMap    => "InputMapComponent",
            Self::Camera      => "CameraComponent",
            Self::Plugin      => "PluginComponent",
            Self::Collider    => "ColliderComponent",
            Self::Collider2d  => "Collider2dComponent",
            Self::Audio       => "AudioComponent",
            Self::AudioDictionary => "AudioDictionaryComponent",
            Self::Animator    => "AnimatorComponent",
            Self::Light       => "LightComponent",
            Self::JointAttach => "JointAttachComponent",
            Self::ParticleEmitter => "ParticleEmitterComponent",
            Self::Skybox      => "SkyboxComponent",
            Self::TerrainChunk => "TerrainChunkComponent",
            Self::WaterVolume => "WaterVolumeComponent",
            Self::WaterLink   => "WaterLinkComponent",
            Self::InteractionSource => "InteractionSourceComponent",
            Self::CoverEmitter => "CoverEmitterComponent",
            Self::ControlPoint => "ControlPointComponent",
            Self::LineRenderer => "LineRendererComponent",
            Self::Text        => "TextComponent",
            Self::CanvasClip  => "CanvasClipComponent",
            Self::CanvasStack => "CanvasStackComponent",
            Self::CanvasWrap  => "CanvasWrapComponent",
            Self::CanvasGrid  => "CanvasGridComponent",
            Self::CanvasLayoutItem => "CanvasLayoutItemComponent",
            Self::CanvasSafeArea => "CanvasSafeAreaComponent",
            Self::CanvasGesture => "CanvasGestureComponent",
            Self::CanvasScroll => "CanvasScrollComponent",
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
    /// メッシュ変形スキニング 2D スプライト（Phase A1）
    SkinnedSpriteComponent(SkinnedSpriteComponentData),
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
    /// 音声辞書（「グループ/用途」キー → 音声ファイル・既定音量の対応表）
    AudioDictionaryComponent(AudioDictionaryComponentData),
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
    /// 水ボリューム（大洋 / 直方体水塊 / 川スプライン(W4)）
    WaterVolumeComponent(WaterVolumeComponentData),
    /// 水位グラフのリンク＝開口（扉・窓・穴・バルブ。W2.5）
    WaterLinkComponent(WaterLinkComponentData),
    /// インタラクションソース（瞬発場への書き手）
    InteractionSourceComponent(InteractionSourceComponentData),
    /// カバーエミッタ（地表カバー場への書き手。I3.1）
    CoverEmitterComponent(CoverEmitterComponentData),
    /// コントロールポイント（汎用パスの点列）
    ControlPointComponent(ControlPointComponentData),
    /// 3D ポリライン描画（釣り糸・ロープ・軌跡）
    LineRendererComponent(LineRendererComponentData),
    /// キャンバス用テキスト表示（HUD の数値・ラベル）
    TextComponent(TextComponentData),
    /// 子を切り抜く（UI の切り抜き。W2-1a）
    CanvasClipComponent(CanvasClipComponentData),
    /// 縦・横に並べるコンテナ（W2-1b）
    CanvasStackComponent(CanvasStackComponentData),
    /// 折り返して並べるコンテナ（W2-1b）
    CanvasWrapComponent(CanvasWrapComponentData),
    /// 格子に並べるコンテナ（W2-1b）
    CanvasGridComponent(CanvasGridComponentData),
    /// レイアウトの子の側の指定（W2-1b）
    CanvasLayoutItemComponent(CanvasLayoutItemComponentData),
    /// 安全領域の部品（W2-1b）
    CanvasSafeAreaComponent(CanvasSafeAreaComponentData),
    /// ジェスチャーを受けるノード（W2-2）
    CanvasGestureComponent(CanvasGestureComponentData),
    /// スクロールの領域（W2-3）
    CanvasScrollComponent(CanvasScrollComponentData),
}
