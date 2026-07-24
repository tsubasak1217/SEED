// ============================================================
//  audio_component.rs — オーディオソースコンポーネント
//
//  Actor に音源を持たせるコンポーネント。エディタのインスペクタで
//  設定でき、スクリプトからは gameObject.AudioSource で操作する。
//
//  【再生モード】
//    spatial = false: 2D 再生（pan で左右バランスを手動指定）
//    spatial = true : 3D 再生（メインカメラとの距離で減衰し、
//                     方向に応じて自動でパンが振られる）
//
//  【減衰モデル（spatial 時）】
//    距離 d に対して線形減衰:
//      d <= min_distance          → 音量 100%
//      min_distance < d < max    → 線形に減少
//      d >= max_distance          → 無音
// ============================================================

use crate::engine::ecs::Component;
use serde::{Deserialize, Serialize};

// ─── デフォルト値関数 ─────────────────────────────────────────

fn default_volume() -> f32 {
    1.0
}
fn default_min_distance() -> f32 {
    2.0
}
fn default_max_distance() -> f32 {
    50.0
}

// ─── AudioComponentData ──────────────────────────────────────

/// AudioComponent のシリアライズ用データ。
#[derive(Clone, Serialize, Deserialize)]
pub struct AudioComponentData {
    /// 音声ファイルパス（assets:// 仮想パス。空 = 未設定）
    #[serde(default)]
    pub audio_path: String,
    /// 音量（1.0 = 等倍）
    #[serde(default = "default_volume")]
    pub volume: f32,
    /// ループ再生するか
    #[serde(default, rename = "loop")]
    pub looped: bool,
    /// Play 開始時に自動再生するか
    #[serde(default)]
    pub play_on_start: bool,
    /// 3D 空間再生（距離減衰 + 方向パン）を使うか
    #[serde(default)]
    pub spatial: bool,
    /// 減衰開始距離（これ以内は音量 100%）
    #[serde(default = "default_min_distance")]
    pub min_distance: f32,
    /// 無音距離（これ以遠は聞こえない）
    #[serde(default = "default_max_distance")]
    pub max_distance: f32,
    /// 左右パン（-1.0=左 〜 0=中央 〜 1.0=右。spatial=false 時のみ有効）
    #[serde(default)]
    pub pan: f32,
}

impl Default for AudioComponentData {
    fn default() -> Self {
        Self {
            audio_path: String::new(),
            volume: default_volume(),
            looped: false,
            play_on_start: false,
            spatial: false,
            min_distance: default_min_distance(),
            max_distance: default_max_distance(),
            pan: 0.0,
        }
    }
}

// ─── AudioComponent ──────────────────────────────────────────

/// オーディオソースコンポーネント（ECS 実体）。
/// フィールド構成はシリアライズ用データと同一。
#[derive(Clone)]
pub struct AudioComponent {
    /// 音声ファイルパス（assets:// 仮想パス。空 = 未設定）
    pub audio_path: String,
    /// 音量（1.0 = 等倍）
    pub volume: f32,
    /// ループ再生するか
    pub looped: bool,
    /// Play 開始時に自動再生するか
    pub play_on_start: bool,
    /// 3D 空間再生（距離減衰 + 方向パン）を使うか
    pub spatial: bool,
    /// 減衰開始距離（これ以内は音量 100%）
    pub min_distance: f32,
    /// 無音距離（これ以遠は聞こえない）
    pub max_distance: f32,
    /// 左右パン（-1.0=左 〜 1.0=右。spatial=false 時のみ有効）
    pub pan: f32,
}

impl AudioComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    pub fn from_data(data: AudioComponentData) -> Self {
        Self {
            audio_path: data.audio_path,
            volume: data.volume,
            looped: data.looped,
            play_on_start: data.play_on_start,
            spatial: data.spatial,
            min_distance: data.min_distance,
            max_distance: data.max_distance,
            pan: data.pan,
        }
    }

    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> AudioComponentData {
        AudioComponentData {
            audio_path: self.audio_path.clone(),
            volume: self.volume,
            looped: self.looped,
            play_on_start: self.play_on_start,
            spatial: self.spatial,
            min_distance: self.min_distance,
            max_distance: self.max_distance,
            pan: self.pan,
        }
    }
}

impl Default for AudioComponent {
    fn default() -> Self {
        Self::from_data(AudioComponentData::default())
    }
}

impl Component for AudioComponent {}
