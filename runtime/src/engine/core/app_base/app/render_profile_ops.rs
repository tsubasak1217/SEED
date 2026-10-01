// ============================================================
//  render_profile_ops.rs — 描画の構成（render.profile。full / ui）の App 側の窓口
//
//  【担当】
//  - 起動時: project_settings.json の render 節と起動オプション（--render-profile= / seed.render_profile）から
//    実効の構成を決めて App.render_profile へ入れ、プロセスへ登録し（Renderer::new・DrawContext::new が読む）、
//    描画品質へ「止める」上限を足し、起動ログ `[SEED RENDER PROFILE]` を出す（決め方の正典は renderer/render_profile）。
//  - Play のピッキングの ID バッファを確保するかの判断（app_init・on_resize）。
//  - 3D を描かない構成で、3D の中身（3D のカメラ・モデル・水・天球・散布・草・SEED.Draw3D）を描かなかったことを
//    種類ごとに 1 回だけ警告する（黙って消えたように見えないため）。
//  - GPU メモリの内訳（gpu_mem）の文脈に出す 1 行。
// ============================================================

use std::collections::HashSet;
use std::sync::Mutex;

use crate::engine::core::renderer::render_profile::{
    self, builtin_profile_catalog, resolve_render_profile, LOG_TAG,
};

use super::{App, RuntimeMode};

/// 3D を描かなかったことを、もう警告した中身の種類（種類ごとにプロセスで 1 回だけ出す）。
static SKIPPED_3D_WARNED: Mutex<Option<HashSet<&'static str>>> = Mutex::new(None);

/// 3D を描かない構成で、3D の中身を描かなかったことを種類ごとに 1 回だけ警告する。
///
/// * `kind`  … 中身の種類（"3D のカメラ"・"3D のモデル" など。種類ごとに 1 回）
/// * `count` … このフレームで描かなかった数（0 なら何もしない）
pub(super) fn warn_3d_skipped_once(kind: &'static str, count: usize) {
    if count == 0 {
        return;
    }
    let mut guard = SKIPPED_3D_WARNED.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    let warned = guard.get_or_insert_with(HashSet::new);
    if warned.insert(kind) {
        eprintln!(
            "{LOG_TAG}[WARN] 描画の構成 {} は 3D のシーンを描きません（scene_3d=false）: {kind} {count} 個を描きませんでした。\
             3D を描くには project_settings.json の render.profile を full にしてください（docs/rendering_profiles.md）",
            render_profile::active_name()
        );
    }
}

impl App {
    /// 実効の描画の構成を決め、プロセスへ登録し、描画品質へ上限を足す（handle_resumed で 1 回。Renderer::new より前）。
    ///
    /// `settings_json` は project_settings.json の中身（読めなければ空文字列＝節なし＝full）。
    /// 描画品質（resolve_render_quality）を決めた後に呼ぶこと（上限を重ねるため）。
    pub(super) fn resolve_render_profile(&mut self, settings_json: &str) {
        let catalog = builtin_profile_catalog();
        let profile = resolve_render_profile(settings_json, catalog, self.render_profile_launch.as_deref());
        // 構成の定義そのものの警告（埋め込みの JSON の誤り）と、決める途中の警告を出す。
        for warning in catalog.warnings.iter().chain(profile.warnings.iter()) {
            eprintln!("{LOG_TAG}[WARN] {warning}");
        }
        eprintln!("{LOG_TAG} {}", profile.log_line());

        // 止めた旗を描画品質の上限として重ねる（毎フレームの可否の判定は品質のつまみが受け持つ）。
        // full は何も止めないので品質はそのまま（従来どおり）。
        let caps = profile.flags.quality_caps();
        if !caps.is_empty() {
            self.render_quality.knobs = self.render_quality.knobs.overlaid(&caps);
            eprintln!(
                "{LOG_TAG} 描画品質へ上限を足しました: {}（実効のつまみ: {}）",
                caps.describe(),
                self.render_quality.knobs.describe()
            );
        }

        // 深い所（Renderer::new・DrawContext::new）が読めるようプロセスへ登録する。
        render_profile::install(&profile);
        self.render_profile = profile;
    }

    /// GPU メモリの内訳（gpu_mem）の文脈に出す 1 行（構成の名前と、止めている旗）。
    pub(super) fn render_profile_summary(&self) -> String {
        format!("（描画の構成 {}: {}）", self.render_profile.name, self.render_profile.flags.describe())
    }

    /// ピッキングの ID バッファ（画面と同じ大きさの Rgba32Float）を確保するか。
    ///
    /// Edit（シーンビューのピック）・エディタに埋め込んだ Play（一時停止でエディタの見た目に切り替わりピックする）・
    /// Play 中も ID パスを描く指定（SEED_ID_PASS_IN_PLAY）では常に確保する。それ以外の Play（単体の実行・Android）は
    /// 構成の picking の旗に従う（full は従来どおり確保する）。
    pub(super) fn id_buffer_wanted(&self) -> bool {
        self.mode == RuntimeMode::Edit
            || self.is_embedded()
            || crate::engine::methods::drawer::id_pass::id_pass_enabled_in_play()
            || self.render_profile.flags.picking
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 数 0 は警告しない（中身が無いフレームで印を立てない）。同じ種類は 2 回目から黙る。
    #[test]
    fn warns_once_per_kind_and_ignores_zero() {
        warn_3d_skipped_once("テスト用の種類", 0);
        {
            let guard = SKIPPED_3D_WARNED.lock().unwrap();
            assert!(guard.as_ref().is_none_or(|set| !set.contains("テスト用の種類")), "0 個では印を立てない");
        }
        warn_3d_skipped_once("テスト用の種類", 2);
        warn_3d_skipped_once("テスト用の種類", 5);
        let guard = SKIPPED_3D_WARNED.lock().unwrap();
        assert!(guard.as_ref().is_some_and(|set| set.contains("テスト用の種類")));
    }
}
