// ============================================================
//  quality/mod.rs — 描画品質プリセット（データドリブン。Android 段階D-2）
//
//  【目的】
//  デスクトップ向けに作った描画経路（デファード・MRT・SSGI・シャドウ 2048 等）を、端末の性能に合わせて
//  「データの差し替えだけで」軽くする。プリセットの中身は runtime/config/render_presets.json、
//  どのプリセットを使うかはプラットフォームの既定（PlatformTraits::default_render_quality）と
//  project_settings.json の render_quality（プラットフォームごと）で決める。
//
//  【構成】
//    knobs.rs   … つまみの集合（QualityKnobs）と JSON からの読み取り・値域
//    catalog.rs … 組み込みプリセットの一覧（JSON を埋め込み、1 回だけ読む）
//    resolve.rs … 実効の品質（RenderQuality）を決める（既定 ← プロジェクト設定 ← 起動オプション）
//    apply.rs   … 実効の品質を既存の設定値へ当てる（可否・上限）
//    scale.rs   … 描画スケール（ゲーム画面だけを縮小して描く）の寸法計算
//
//  【流れ】
//    app_init.rs（起動時 1 回）… resolve_render_quality → App.render_quality。シャドウ品質・目標 fps へ上限を当てる
//    frame_renderer.rs（毎フレーム）… 描画スケールを Renderer へ、機能の上限を resolve_with_caps へ、
//                                      デファード・後処理・水面の可否を各ゲートへ
//  デスクトップの既定プリセット（desktop）はつまみを 1 つも持たないので、どの経路も従来と同じ値になる。
//
//  正典: docs/rendering_roadmap.md の「描画品質プリセット」節、端末での計測は docs/android.md §22。
// ============================================================

pub mod apply;
pub mod catalog;
pub mod knobs;
pub mod resolve;
pub mod scale;

pub use catalog::{builtin_catalog, PresetCatalog, PresetEntry};
pub use knobs::{QualityKnobs, KNOWN_KEYS, MAX_RENDER_SCALE, MIN_RENDER_SCALE};
pub use resolve::{resolve_render_quality, QualityLaunchOverrides, RenderQuality, PRESET_KEY, RENDER_QUALITY_KEY};
pub use scale::{
    is_native_scale, logical_rect_to_render, render_ratio, sanitize_render_scale, scaled_render_size,
};
