// ============================================================
//  animation/mod.rs — キーフレームアニメーション ランタイム基盤（Phase 1）
//
//  【構成（1 ファイル 1 責務）】
//    clip.rs     … .anim アセットのデータ型 & ロード（AnimationClip / Track / AnimValue）
//    sampler.rs  … トラックの補間評価（step / linear / bezier）
//    registry.rs … プロパティレジストリ（(component, property) → ECS get/set）
//    system.rs   … 評価システムのコア純関数（時刻正規化・アクタパス解決）
//
//  AnimatorComponent（ECS スロットコンポーネント）は components/animator_component.rs、
//  毎フレームの評価ループ（App 統合）は core/app_base/app/animation_ops.rs にある。
// ============================================================

pub mod clip;
pub mod sampler;
pub mod registry;
pub mod system;

// よく使う型・関数を再エクスポートする
pub use clip::{AnimationClip, AnimValue, ValueType, LoopMode, Interp, Track, Keyframe, TrackTarget, AnimEvent};
pub use sampler::sample_track;
pub use registry::{PropBinding, resolve_binding, apply_write, read_binding};
pub use system::{normalize_time, resolve_actor_path};
