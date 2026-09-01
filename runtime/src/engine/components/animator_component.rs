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

use crate::engine::animation::AnimationClip;
use crate::engine::ecs::Component;

// ─── デフォルト値関数 ─────────────────────────────────────────

/// speed の既定値（等倍）。
fn default_speed() -> f32 {
    1.0
}
/// play_on_start の既定値（true = Play 開始時に自動再生）。
fn default_play_on_start() -> bool {
    true
}
/// default_fade_seconds の既定値（0 = 即時切替。従来と同じ挙動）。
fn default_fade_seconds() -> f32 {
    0.0
}

// ─── AnimClipKind / AnimClipLoop ─────────────────────────────

/// クリップの種別。
///
/// - `Keyframe`: `.anim` アセット（自作キーフレームトラック）。従来のクリップ。
/// - `Model`   : glTF モデル内蔵アニメ（Model スロットのスキニングを駆動）。
///
/// 旧シーン（`kind` フィールドなし）は `#[serde(default)]` により `Keyframe`
/// として読み込まれる（後方互換）。
#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AnimClipKind {
    /// .anim キーフレームクリップ
    Keyframe,
    /// glTF モデル内蔵アニメ
    Model,
}

impl Default for AnimClipKind {
    /// kind 省略時の既定は keyframe（旧シーン後方互換）。
    fn default() -> Self {
        AnimClipKind::Keyframe
    }
}

/// Model クリップのループ種別。
///
/// Keyframe クリップは `.anim` 内の `loop_mode` を使うためこの値は無視される。
/// Model クリップは `.anim` を持たないため、ここでループ挙動を指定する。
#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AnimClipLoop {
    /// 末尾に達したら先頭へ戻って繰り返す（Model の既定）
    Loop,
    /// 一度だけ再生し、末尾で停止する
    Once,
}

impl Default for AnimClipLoop {
    /// loop_mode 省略時の既定は loop（Model アニメは繰り返し再生が自然）。
    fn default() -> Self {
        AnimClipLoop::Loop
    }
}

// ─── AnimClipRef ─────────────────────────────────────────────

/// アニメーターが参照する 1 クリップのエントリ。
///
/// name は再生時の識別キー（default_clip / スクリプトからの指定に使う）。
/// kind によって解決先が変わる:
///   - `Keyframe`: `path`（.anim アセット）をロードして評価する。
///   - `Model`   : 同アクターの Model スロットの glTF 内蔵アニメ `anim`（名前）を駆動する。
#[derive(Clone, Serialize, Deserialize)]
pub struct AnimClipRef {
    /// 再生時の識別名（空なら path のクリップ名にフォールバック）
    #[serde(default)]
    pub name: String,
    /// クリップ種別（省略時は Keyframe：旧シーン後方互換）
    #[serde(default)]
    pub kind: AnimClipKind,
    /// .anim アセットパス（assets:// 仮想パス）。kind=Keyframe で使用。
    #[serde(default)]
    pub path: String,
    /// glTF モデル内蔵アニメ名。kind=Model で使用。
    #[serde(default)]
    pub anim: String,
    /// Model クリップのループ種別（kind=Model で使用。既定 loop）
    #[serde(default)]
    pub loop_mode: AnimClipLoop,
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
    /// `Play`（フェード時間の明示指定なし）で使う既定クロスフェード時間（秒）。
    /// 既定 0 = 即時切替（従来挙動）。`CrossFade` は常に明示値を優先する。
    #[serde(default = "default_fade_seconds")]
    pub default_fade_seconds: f32,
}

impl Default for AnimatorComponentData {
    fn default() -> Self {
        Self {
            clips: Vec::new(),
            default_clip: String::new(),
            play_on_start: default_play_on_start(),
            speed: default_speed(),
            default_fade_seconds: default_fade_seconds(),
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
    /// Play の既定クロスフェード時間（秒。0 = 即時切替）
    pub default_fade_seconds: f32,

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

    // ── クロスフェード状態（揮発・保存しない）──
    /// フェード元クリップ名（None = フェードしていない）
    pub fade_from_clip: Option<String>,
    /// フェード元クリップの再生時刻（秒。ループ正規化前の累積値）
    pub fade_from_time: f32,
    /// ブレンド率（0 = フェード元のみ / 1 = 現在クリップのみ）。
    /// `fade_from_clip` が None のときは常に 1.0。
    pub fade_weight: f32,
    /// weight の進行速度（1/秒）。fade_seconds の逆数。
    pub fade_rate: f32,
}

impl AnimatorComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    pub fn from_data(data: AnimatorComponentData) -> Self {
        Self {
            clips: data.clips,
            default_clip: data.default_clip,
            play_on_start: data.play_on_start,
            speed: data.speed,
            default_fade_seconds: data.default_fade_seconds,
            current_clip: None,
            time: 0.0,
            playing: false,
            initialized: false,
            cache: HashMap::new(),
            fade_from_clip: None,
            fade_from_time: 0.0,
            fade_weight: 1.0,
            fade_rate: 0.0,
        }
    }

    /// シリアライズ用データへ変換する（揮発状態は保存しない）。
    pub fn to_data(&self) -> AnimatorComponentData {
        AnimatorComponentData {
            clips: self.clips.clone(),
            default_clip: self.default_clip.clone(),
            play_on_start: self.play_on_start,
            speed: self.speed,
            default_fade_seconds: self.default_fade_seconds,
        }
    }

    // ── 再生状態機械（クロスフェード込み）─────────────────────
    //
    // ここは「どのクリップをどの時刻でどれだけ混ぜるか」だけを決める純ロジックで、
    // World もモデルも触らない（＝単体テストできる）。実際のポーズ生成は
    // animation_ops → ModelAnimDrive → GPU スキニングが行う。

    /// クリップ名からクリップ定義を引く。
    pub fn find_clip(&self, name: &str) -> Option<&AnimClipRef> {
        self.clips.iter().find(|c| c.name == name)
    }

    /// 2 つのクリップ間でクロスフェードできるか。
    ///
    /// 補間対象は **glTF 内蔵アニメ（kind=Model）同士だけ**。`.anim` キーフレームクリップは
    /// トラック単位でターゲットが異なり得るため補間対象外で、Model↔Keyframe の切替も
    /// 即時とする（警告は出さない＝仕様）。
    fn can_cross_fade(&self, from: &str, to: &str) -> bool {
        let kind_of = |n: &str| self.find_clip(n).map(|c| c.kind);
        matches!(kind_of(from), Some(AnimClipKind::Model))
            && matches!(kind_of(to), Some(AnimClipKind::Model))
    }

    /// 指定クリップの再生を開始する（`fade_seconds > 0` ならクロスフェード）。
    ///
    /// 【フェード中に再度切替が来たとき】3 本以上を同時に保持しない。
    /// 「今の主クリップ（現在クリップ）」を新しいフェード元に差し替え、weight を 0 から
    /// 再開する近似を採る。直前のフェード元（さらに古いクリップ）はそこで捨てる。
    /// 高速に切り替え続けた場合、捨てたぶんのポーズはブレンドに寄与しなくなるが、
    /// 実用上の見た目は連続に保たれる。
    pub fn begin_clip(&mut self, name: &str, fade_seconds: f32) {
        let prev = self.current_clip.clone();
        let prev_time = self.time;

        // フェード可能条件: 正のフェード時間 + 直前クリップが存在 + 双方が Model クリップ
        let fade = fade_seconds > 0.0
            && prev.as_deref().is_some_and(|p| self.can_cross_fade(p, name));

        if fade {
            self.fade_from_clip = prev;
            self.fade_from_time = prev_time;
            self.fade_weight = 0.0;
            self.fade_rate = 1.0 / fade_seconds;
        } else {
            // 即時切替: フェード状態を完全に破棄する
            self.fade_from_clip = None;
            self.fade_from_time = 0.0;
            self.fade_weight = 1.0;
            self.fade_rate = 0.0;
        }

        self.current_clip = Some(name.to_string());
        self.time = 0.0;
        self.playing = true;
    }

    /// フェードを 1 フレーム進める（再生中のみ）。
    ///
    /// weight が 1 に達したらフェード元を破棄して通常再生へ戻る。
    /// 一時停止中（`playing == false`）は weight もフェード元時刻も進めない。
    pub fn advance_fade(&mut self, dt: f32) {
        if self.fade_from_clip.is_none() {
            self.fade_weight = 1.0;
            return;
        }
        if !self.playing {
            return;
        }
        // フェード元クリップも再生を続ける（止めるとブレンド中だけ動きが固まって見える）。
        self.fade_from_time += dt * self.speed;
        self.fade_weight += dt * self.fade_rate;
        if self.fade_weight >= 1.0 {
            self.fade_weight = 1.0;
            self.fade_from_clip = None;
            self.fade_from_time = 0.0;
            self.fade_rate = 0.0;
        }
    }

    /// 再生を停止して先頭へ戻す（フェード状態も破棄する）。
    pub fn stop(&mut self) {
        self.playing = false;
        self.time = 0.0;
        self.fade_from_clip = None;
        self.fade_from_time = 0.0;
        self.fade_weight = 1.0;
        self.fade_rate = 0.0;
    }

    /// 再生位置とフェード状態を保持したまま一時停止する。
    pub fn pause(&mut self) {
        self.playing = false;
    }

    /// 一時停止を再開する（再生対象クリップが無ければ何もしない）。
    pub fn resume(&mut self) {
        if self.current_clip.is_some() {
            self.playing = true;
        }
    }
}

impl Default for AnimatorComponent {
    fn default() -> Self {
        Self::from_data(AnimatorComponentData::default())
    }
}

impl Component for AnimatorComponent {}

// ============================================================
//  テスト（フェード状態機械・旧シーン互換）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// Model クリップ 2 本を持つアニメーターを作る。
    fn model_animator() -> AnimatorComponent {
        let mk = |n: &str| AnimClipRef {
            name: n.to_string(),
            kind: AnimClipKind::Model,
            path: String::new(),
            anim: n.to_string(),
            loop_mode: AnimClipLoop::Loop,
        };
        let mut a = AnimatorComponent::from_data(AnimatorComponentData {
            clips: vec![mk("Idle"), mk("Walk")],
            default_clip: "Idle".into(),
            play_on_start: true,
            speed: 1.0,
            default_fade_seconds: 0.0,
        });
        a.initialized = true;
        a
    }

    /// 初回再生は（直前クリップが無いので）常に即時。
    #[test]
    fn first_play_is_immediate() {
        let mut a = model_animator();
        a.begin_clip("Idle", 0.5);
        assert_eq!(a.current_clip.as_deref(), Some("Idle"));
        assert!(a.fade_from_clip.is_none(), "フェード元が無いので即時");
        assert_eq!(a.fade_weight, 1.0);
        assert!(a.playing);
    }

    /// フェード時間 0 の切替は即時（フェード状態を持たない）。
    #[test]
    fn zero_fade_switches_instantly() {
        let mut a = model_animator();
        a.begin_clip("Idle", 0.0);
        a.time = 1.25;
        a.begin_clip("Walk", 0.0);
        assert_eq!(a.current_clip.as_deref(), Some("Walk"));
        assert!(a.fade_from_clip.is_none());
        assert_eq!(a.fade_weight, 1.0);
        assert_eq!(a.time, 0.0);
    }

    /// 開始 → 進行 → 完了（weight が 0 から 1 へ進み、到達でフェード元を破棄）。
    #[test]
    fn cross_fade_progresses_and_completes() {
        let mut a = model_animator();
        a.begin_clip("Idle", 0.0);
        a.time = 2.0;
        a.begin_clip("Walk", 0.4); // 0.4 秒でフェード

        assert_eq!(a.fade_from_clip.as_deref(), Some("Idle"));
        assert_eq!(a.fade_from_time, 2.0, "フェード元は切替時点の時刻から継続する");
        assert_eq!(a.fade_weight, 0.0);

        a.advance_fade(0.1);
        assert!((a.fade_weight - 0.25).abs() < 1e-5);
        assert!((a.fade_from_time - 2.1).abs() < 1e-5, "フェード元も再生を続ける");

        a.advance_fade(0.2);
        assert!((a.fade_weight - 0.75).abs() < 1e-5);
        assert!(a.fade_from_clip.is_some());

        a.advance_fade(0.2); // 合計 0.5 秒 > 0.4 秒
        assert_eq!(a.fade_weight, 1.0);
        assert!(a.fade_from_clip.is_none(), "完了したらフェード元を破棄する");
        assert_eq!(a.fade_rate, 0.0);
    }

    /// フェード中の再切替は「現在の主クリップ」を新しいフェード元にして weight を 0 から再開する。
    #[test]
    fn switching_during_fade_replaces_source() {
        let mut a = model_animator();
        a.begin_clip("Idle", 0.0);
        a.begin_clip("Walk", 1.0);
        a.advance_fade(0.5);
        assert!((a.fade_weight - 0.5).abs() < 1e-5);
        a.time = 0.5;

        a.begin_clip("Idle", 0.25);
        assert_eq!(a.fade_from_clip.as_deref(), Some("Walk"), "直前の主クリップが新フェード元");
        assert_eq!(a.fade_from_time, 0.5);
        assert_eq!(a.fade_weight, 0.0);
        assert_eq!(a.current_clip.as_deref(), Some("Idle"));
    }

    /// Keyframe クリップが絡む切替はフェードせず即時（警告なし）。
    #[test]
    fn keyframe_clips_never_cross_fade() {
        let mut a = model_animator();
        a.clips.push(AnimClipRef {
            name: "Kf".into(),
            kind: AnimClipKind::Keyframe,
            path: "assets://a.anim".into(),
            anim: String::new(),
            loop_mode: AnimClipLoop::Loop,
        });
        a.begin_clip("Idle", 0.0);
        a.begin_clip("Kf", 0.5); // Model → Keyframe
        assert!(a.fade_from_clip.is_none());
        a.begin_clip("Walk", 0.5); // Keyframe → Model
        assert!(a.fade_from_clip.is_none());
    }

    /// Pause 中は weight もフェード元時刻も進まない。Resume で再開する。
    #[test]
    fn pause_freezes_fade_progress() {
        let mut a = model_animator();
        a.begin_clip("Idle", 0.0);
        a.begin_clip("Walk", 1.0);
        a.pause();
        a.advance_fade(0.5);
        assert_eq!(a.fade_weight, 0.0, "一時停止中は進まない");
        assert_eq!(a.fade_from_time, 0.0);
        a.resume();
        a.advance_fade(0.5);
        assert!((a.fade_weight - 0.5).abs() < 1e-5);
    }

    /// Stop はフェード状態も破棄する（再開時に古いブレンドが復活しない）。
    #[test]
    fn stop_discards_fade_state() {
        let mut a = model_animator();
        a.begin_clip("Idle", 0.0);
        a.begin_clip("Walk", 1.0);
        a.advance_fade(0.3);
        a.stop();
        assert!(a.fade_from_clip.is_none());
        assert_eq!(a.fade_weight, 1.0);
        assert_eq!(a.time, 0.0);
        assert!(!a.playing);
    }

    /// 旧シーン（default_fade_seconds / kind / anim なし）が既定値付きで読める。
    #[test]
    fn legacy_scene_json_deserializes_with_defaults() {
        let json = r#"{
            "clips": [{"name":"Walk","path":"assets://walk.anim"}],
            "default_clip": "Walk",
            "play_on_start": true,
            "speed": 1.0
        }"#;
        let d: AnimatorComponentData = serde_json::from_str(json).expect("旧シーンが読めること");
        assert_eq!(d.default_fade_seconds, 0.0, "未指定は即時切替（従来挙動）");
        assert!(matches!(d.clips[0].kind, AnimClipKind::Keyframe));
        assert_eq!(d.clips[0].anim, "", "anim 未指定は空 = index 0 フォールバック");
    }

    /// kind=model で anim 未指定の旧シーンも読め、既定は loop。
    #[test]
    fn legacy_model_clip_without_anim_name_deserializes() {
        let json = r#"{"clips":[{"name":"Run","kind":"model"}],"default_clip":"Run"}"#;
        let d: AnimatorComponentData = serde_json::from_str(json).expect("旧 model クリップが読めること");
        assert!(matches!(d.clips[0].kind, AnimClipKind::Model));
        assert!(d.clips[0].anim.is_empty());
        assert!(matches!(d.clips[0].loop_mode, AnimClipLoop::Loop));
        assert_eq!(d.speed, 1.0, "speed 未指定は 1.0");
    }
}
