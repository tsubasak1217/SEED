// ============================================================
//  render_quality.rs — 描画品質プリセットの App 側の窓口（Android 段階D-2）
//
//  【役割】
//  - 起動時: プラットフォームの既定・project_settings.json・起動オプションから実効の描画品質を決めて
//    App.render_quality へ入れ、起動ログ `[SEED QUALITY]` を出す（決め方の正典は renderer/quality/resolve.rs）。
//  - 毎フレーム: 描画スケールを「このフレームで効かせてよいか」を判断する（render_scale_for_frame）。
//
//  【描画スケールを効かせない条件】（等倍で描く。どれもエディタの操作・読み戻しが 3D の深度と同じ大きさの
//  バッファを前提にしているため）
//    - Play でない（Edit のシーンビュー）・Play の一時停止中（エディタの見た目＝ID パスでのピックが動く）
//    - サムネイルの撮影中（撮影は描画解像度の 1 枚のターゲットへ描く）
//    - Play 中も ID パスを毎フレーム描く指定（環境変数 SEED_ID_PASS_IN_PLAY。ID バッファは論理サイズ）
//  Android の一時停止（IPC の remote_paused）はゲームの画面のままなので効かせたままにする。
// ============================================================

use std::sync::atomic::{AtomicBool, Ordering};

use crate::engine::core::renderer::quality::{self, builtin_catalog, resolve_render_quality};
use crate::engine::platform;

use super::{App, RuntimeMode};

/// 「品質が前方描画にしたのでシェーディングアセットが効かない」をもう知らせたか（プロセスで 1 回だけ出す）。
static SHADING_ASSET_IGNORED_WARNED: AtomicBool = AtomicBool::new(false);

/// 描画品質が deferred を止めた（前方描画にした）ために、要求されているシェーディングアセット（L3。デファードの
/// ライティングパスにしか効かない）が効かなくなることを、プロセスで 1 回だけ警告する。
///
/// 見た目が黙って変わるのを防ぐための知らせ（止める判断は品質の設定に任せる。戻すには render_quality の
/// `deferred: true`）。`requested_deferred` は品質を当てる前の設定、`effective_deferred` は当てた後。
pub(super) fn warn_if_shading_asset_ignored(
    requested_deferred: bool,
    effective_deferred: bool,
    shading_asset: Option<&str>,
    preset: &str,
) {
    let Some(path) = shading_asset else { return };
    if !requested_deferred || effective_deferred {
        return;
    }
    if !SHADING_ASSET_IGNORED_WARNED.swap(true, Ordering::Relaxed) {
        eprintln!(
            "[SEED QUALITY][WARN] 描画品質 {preset} が前方描画（deferred: false）にしているため、シェーディングアセット {path} は効きません\
             （デファードのライティング専用。効かせるには project_settings.json の render_quality で deferred: true）"
        );
    }
}

/// このフレームの描画スケールを決める【純関数】（効かせない条件ではいつでも等倍）。
///
/// # 引数
/// * `requested`        - 描画品質が決めた描画スケール（`RenderQuality::render_scale`）
/// * `is_play`          - Play か（Edit のシーンビューは等倍）
/// * `editor_view`      - Play の一時停止でエディタの見た目に切り替わっているか（等倍）
/// * `thumbnail`        - サムネイルの撮影中か（等倍）
/// * `id_pass_in_play`  - Play 中も ID パスを毎フレーム描く指定か（等倍）
pub(super) fn render_scale_for_frame(
    requested: f32,
    is_play: bool,
    editor_view: bool,
    thumbnail: bool,
    id_pass_in_play: bool,
) -> f32 {
    if !is_play || editor_view || thumbnail || id_pass_in_play {
        return quality::MAX_RENDER_SCALE;
    }
    quality::sanitize_render_scale(requested)
}

impl App {
    /// 実効の描画品質を決めて App.render_quality へ入れ、起動ログを出す（handle_resumed で 1 回）。
    ///
    /// `settings_json` は project_settings.json の中身（読めなければ空文字列＝節なし）。
    pub(super) fn resolve_render_quality(&mut self, settings_json: &str) {
        let catalog = builtin_catalog();
        self.render_quality =
            resolve_render_quality(settings_json, &platform::CURRENT, catalog, &self.quality_launch);
        // プリセット定義そのものの警告（埋め込みの JSON の誤り）と、決める途中の警告を出す。
        for warning in catalog.warnings.iter().chain(self.render_quality.warnings.iter()) {
            eprintln!("[SEED QUALITY][WARN] {warning}");
        }
        eprintln!("[SEED QUALITY] {}", self.render_quality.log_line());
    }

    /// このフレームの描画スケール（Renderer::set_render_scale へ渡す値。等倍 = 1.0）。
    pub(super) fn effective_render_scale(&self) -> f32 {
        render_scale_for_frame(
            self.render_quality.render_scale(),
            self.mode == RuntimeMode::Play,
            self.paused,
            self.thumbnail_session.is_some(),
            crate::engine::methods::drawer::id_pass::id_pass_enabled_in_play(),
        )
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// Play で効かせない条件が無ければ要求どおり、1 つでもあれば等倍。
    #[test]
    fn scale_applies_only_in_plain_play() {
        assert_eq!(render_scale_for_frame(0.5, true, false, false, false), 0.5);
        assert_eq!(render_scale_for_frame(0.5, false, false, false, false), 1.0, "Edit は等倍");
        assert_eq!(render_scale_for_frame(0.5, true, true, false, false), 1.0, "一時停止（エディタの見た目）は等倍");
        assert_eq!(render_scale_for_frame(0.5, true, false, true, false), 1.0, "サムネイル撮影は等倍");
        assert_eq!(render_scale_for_frame(0.5, true, false, false, true), 1.0, "ID パス毎フレームは等倍");
    }

    /// 要求は値域へ収める（壊れた値でも 0.5〜1.0）。等倍の要求は等倍（デスクトップの既定）。
    #[test]
    fn requested_scale_is_sanitized() {
        assert_eq!(render_scale_for_frame(1.0, true, false, false, false), 1.0);
        assert_eq!(render_scale_for_frame(0.1, true, false, false, false), quality::MIN_RENDER_SCALE);
        assert_eq!(render_scale_for_frame(f32::NAN, true, false, false, false), 1.0);
    }
}
