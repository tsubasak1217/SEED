// ============================================================
//  font_field_ops.rs — 文字の距離場（font.distance_field。sdf / mtsdf）の App 側の窓口
//
//  【担当】
//  起動時に project_settings.json の font 節と起動オプション（--font-distance-field=）から実効の設定を決め、
//  プロセスへ登録し（キャンバスの文字の描画器 FontConfig::canvas が読む）、起動ログ `[SEED FONT]` を出す。
//  決め方の正典は font/field_settings.rs（docs/ui_components.md §12.10）。
// ============================================================

use crate::engine::core::font::field_settings::{self, resolve_font_field_settings, LOG_TAG};

use super::App;

impl App {
    /// 実効の文字の距離場の設定を決めてプロセスへ登録する（handle_resumed で 1 回。CanvasTextRenderer::new より前）。
    ///
    /// `settings_json` は project_settings.json の中身（読めなければ空文字列＝節なし＝既定の mtsdf）。
    pub(super) fn resolve_font_field(&mut self, settings_json: &str) {
        let (settings, source, warnings) = resolve_font_field_settings(settings_json, self.font_field_launch.as_deref());
        for warning in &warnings {
            eprintln!("{LOG_TAG}[WARN] {warning}");
        }
        eprintln!("{LOG_TAG} {}", settings.log_line(source));
        field_settings::install(settings);
    }
}
