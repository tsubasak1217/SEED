use std::any::Any;
use serde::{Deserialize, Serialize};
use crate::engine::core::clock::FrameContext;
use crate::engine::core::scripting::ScriptComponentData;

pub mod model_component;

pub use model_component::{ModelComponent, ModelComponentData};

// ============================================================
//  Component トレイト
// ============================================================

/// ECS コンポーネントの基底トレイト。
/// `Actor` に付与できる全コンポーネントはこのトレイトを実装する。
///
/// ライフサイクルメソッドはすべてデフォルト no-op。
/// 必要なものだけオーバーライドする。
pub trait Component: Any + Send + Sync {
    fn as_any(&self) -> &dyn Any;
    fn as_any_mut(&mut self) -> &mut dyn Any;
    /// シリアライズ用データに変換する。
    fn to_data(&self) -> ComponentData;

    // ── フレームライフサイクル ─────────────────────────────
    /// フレーム開始時処理。
    fn begin_frame(&mut self, _ctx: &FrameContext) {}
    /// 早期更新（入力受付・アニメーション先行処理など）。
    fn early_update(&mut self, _ctx: &FrameContext) {}
    /// 通常更新。
    fn update(&mut self, _ctx: &FrameContext) {}
    /// 固定タイムステップ更新（物理など）。delta_time は FIXED_DELTA 固定。
    fn constant_update(&mut self, _ctx: &FrameContext) {}
    /// 遅延更新（他コンポーネントの Update 完了後に行う処理）。
    fn late_update(&mut self, _ctx: &FrameContext) {}
    /// 描画コマンド積み上げ前の準備処理。
    fn render(&mut self, _ctx: &FrameContext) {}
    /// フレーム終了時処理。
    fn end_frame(&mut self, _ctx: &FrameContext) {}
}

// ============================================================
//  ComponentData — シリアライズ用 enum
// ============================================================

/// 各コンポーネントのシリアライズ表現。
/// `"type"` フィールドで種別を識別し、内容を `"data"` に格納する。
#[derive(Clone, Serialize, Deserialize)]
#[serde(tag = "type", content = "data")]
pub enum ComponentData {
    ModelComponent(ModelComponentData),
    ScriptComponent(ScriptComponentData),
}
