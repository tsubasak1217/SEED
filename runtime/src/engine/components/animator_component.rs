// ============================================================
//  animator_component.rs — アニメーターコンポーネント
//
//  Actor に AnimationClip 群を持たせ、Play モードで再生するコンポーネント。
//  実際の評価（毎フレームのトラック適用）は AnimationSystem
//  （core/app_base/app/animation_ops.rs）が行い、本コンポーネントは
//  「再生対象クリップの一覧」と「再生状態」のデータのみを保持する（ECS 理念）。
//
//  【シリアライズ】
//  clips / default_clip / play_on_start / speed のみ保存する。
//  再生時状態（current_clip / time / playing / initialized）とロード済みクリップ
//  キャッシュ（cache）は #[serde(skip)] で保存しない（Play ごとに初期化）。
// ============================================================

use std::collections::HashMap;
use std::sync::Arc;

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;
use crate::engine::animation::AnimationClip;

// ─── デフォルト値関数 ─────────────────────────────────────────

/// speed の既定値（等倍）。
fn default_speed() -> f32 { 1.0 }
/// play_on_start の既定値（true = Play 開始時に自動再生）。
fn default_play_on_start() -> bool { true }

// ─── AnimClipRef ─────────────────────────────────────────────

/// アニメーターが参照する 1 クリップのエントリ。
///
/// name は再生時の識別キー（default_clip / スクリプトからの指定に使う）。
/// path は .anim アセットの相対パス（"assets://..." 形式）。
#[derive(Clone, Serialize, Deserialize)]
pub struct AnimClipRef {
    /// 再生時の識別名（空なら path のクリップ名にフォールバック）
    #[serde(default)]
    pub name: String,
    /// .anim アセットパス（assets:// 仮想パス）
    #[serde(default)]
    pub path: String,
}

// ─── AnimatorComponentData（シリアライズ用）───────────────────

/// AnimatorComponent のシリアライズ用データ。
#[derive(Clone, Serialize, Deserialize)]
pub struct AnimatorComponentData {
    /// 参照クリップ一覧
    #[serde(default)]
    pub clips: Vec<AnimClipRef>,
    /// 既定クリップ名（空 = なし）。play_on_start 時にこれを再生する。
    #[serde(default)]
    pub default_clip: String,
    /// Play 開始時に default_clip を自動再生するか（既定 true）
    #[serde(default = "default_play_on_start")]
    pub play_on_start: bool,
    /// 再生速度倍率（既定 1.0）
    #[serde(default = "default_speed")]
    pub speed: f32,
}

impl Default for AnimatorComponentData {
    fn default() -> Self {
        Self {
            clips:         Vec::new(),
            default_clip:  String::new(),
            play_on_start: default_play_on_start(),
            speed:         default_speed(),
        }
    }
}

// ─── AnimatorComponent（ECS 実体）─────────────────────────────

/// アニメーターコンポーネント（ECS 実体）。
///
/// 保存対象フィールドに加え、再生時のみ意味を持つ揮発状態を保持する。
#[derive(Clone)]
pub struct AnimatorComponent {
    // ── 保存対象（Data と同一）──
    /// 参照クリップ一覧
    pub clips: Vec<AnimClipRef>,
    /// 既定クリップ名
    pub default_clip: String,
    /// Play 開始時に自動再生するか
    pub play_on_start: bool,
    /// 再生速度倍率
    pub speed: f32,

    // ── 揮発状態（保存しない）──
    /// 現在再生中のクリップ名（None = 停止・未選択）
    pub current_clip: Option<String>,
    /// 現在の再生時刻（秒。ループ正規化前の累積値）
    pub time: f32,
    /// 再生中フラグ
    pub playing: bool,
    /// Play 開始時の初期化（クリップロード + play_on_start 発火）が済んだか
    pub initialized: bool,
    /// ロード済みクリップキャッシュ（キー = AnimClipRef.name）
    pub cache: HashMap<String, Arc<AnimationClip>>,
}

impl AnimatorComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    pub fn from_data(data: AnimatorComponentData) -> Self {
        Self {
            clips:         data.clips,
            default_clip:  data.default_clip,
            play_on_start: data.play_on_start,
            speed:         data.speed,
            current_clip:  None,
            time:          0.0,
            playing:       false,
            initialized:   false,
            cache:         HashMap::new(),
        }
    }

    /// シリアライズ用データへ変換する（揮発状態は保存しない）。
    pub fn to_data(&self) -> AnimatorComponentData {
        AnimatorComponentData {
            clips:         self.clips.clone(),
            default_clip:  self.default_clip.clone(),
            play_on_start: self.play_on_start,
            speed:         self.speed,
        }
    }
}

impl Default for AnimatorComponent {
    fn default() -> Self { Self::from_data(AnimatorComponentData::default()) }
}

impl Component for AnimatorComponent {}
