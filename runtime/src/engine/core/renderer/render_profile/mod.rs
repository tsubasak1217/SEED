// ============================================================
//  render_profile/mod.rs — 描画の構成（render profile。full / ui）
//
//  【目的】
//  2D の UI しか使わないアプリ（Wake or Pay）でも、エンジンは 3D の描画資源（bindless のメガバッファ 224 MiB・
//  シャドウマップ・GI のアトラス・Play のピッキングの ID バッファ・レイトレーシング）を確保していた。
//  プロジェクトの設定で「UI だけ」の構成を選ぶと、それらを作らず、GPU メモリも小さな塊で確保する。
//  既定（full）は従来と同じ（何も止めない）。
//
//  【データ】
//    runtime/config/render_profiles.json … 構成の定義（名前・旗）。構成を足す・変えるのはこの JSON だけ
//    project_settings.json の "render": { "profile": "ui", <旗の上書き> }
//    起動オプション --render-profile=<名前>[,キー=値…]（Android は seed.render_profile。検証用）
//
//  【構成】
//    flags.rs   … 旗（RenderProfileFlags）・GPU メモリの方針（MemoryHint）・JSON からの読み取り・品質への上限
//    catalog.rs … 組み込みの構成の一覧（JSON を埋め込み、1 回だけ読む）
//    resolve.rs … 実効の構成を決める（既定 ← プロジェクト設定 ← 起動オプション）
//
//  【効く場所】（旗ごとの詳細は docs/rendering_profiles.md）
//    起動時（App::resolve_render_profile → install）… 描画品質へ上限を足す（デファード・影・GI・後処理・水）
//    Renderer::new … レイトレーシング・bindless の機能を求めるか・GPU メモリの方針（MemoryHints）
//    DrawContext::new … シャドウマップ・GI のアトラスを 1x1 の置き場にするか
//    app_init / on_resize … Play のピッキングの ID バッファを確保するか
//    frame_renderer（毎フレーム）… 3D のシーンを描くか（描かないなら 3D の中身があるとき 1 回警告）
//  プロセスで 1 つの「今の構成」を install しておき、深い所（Renderer・DrawContext）は active_flags() で読む
//  （起動時に 1 回決まり、実行中は変わらない。shadow_settings の設定と同じ流儀）。
// ============================================================

use std::sync::RwLock;

pub mod catalog;
pub mod flags;
pub mod resolve;
/// 実 GPU で「ui の構成は 3D の資源を作らない・full は従来どおり」を確かめるテスト（--ignored）。
#[cfg(test)]
mod tests_gpu;

pub use catalog::{builtin_profile_catalog, ProfileCatalog, ProfileEntry};
pub use flags::{MemoryHint, RenderProfileFlags, KNOWN_FLAG_KEYS};
pub use resolve::{resolve_render_profile, ProfileSource, RenderProfile, PROFILE_KEY, RENDER_KEY};

/// 起動ログの行頭の印。
pub const LOG_TAG: &str = "[SEED RENDER PROFILE]";

/// 今の構成（名前と旗）。起動時に 1 回 install する（それまでは full）。
static ACTIVE: RwLock<Option<(String, RenderProfileFlags)>> = RwLock::new(None);

/// 今の構成をプロセスへ登録する（Renderer::new より前に 1 回呼ぶ）。
pub fn install(profile: &RenderProfile) {
    let mut slot = ACTIVE.write().unwrap_or_else(|poisoned| poisoned.into_inner());
    *slot = Some((profile.name.clone(), profile.flags));
}

/// 今の構成の旗（install 前は full）。
pub fn active_flags() -> RenderProfileFlags {
    let slot = ACTIVE.read().unwrap_or_else(|poisoned| poisoned.into_inner());
    slot.as_ref().map(|(_, flags)| *flags).unwrap_or(RenderProfileFlags::FULL)
}

/// 今の構成の名前（install 前は full）。
pub fn active_name() -> String {
    let slot = ACTIVE.read().unwrap_or_else(|poisoned| poisoned.into_inner());
    slot.as_ref()
        .map(|(name, _)| name.clone())
        .unwrap_or_else(|| catalog::FALLBACK_PROFILE_NAME.to_string())
}
