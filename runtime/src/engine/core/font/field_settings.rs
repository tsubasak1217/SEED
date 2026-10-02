// ============================================================
//  font/field_settings.rs — 文字の距離場の設定（sdf / mtsdf・辺の色分け）を決めてプロセスへ登録する
//
//  【データ】
//    project_settings.json の "font": { "distance_field": "mtsdf" | "sdf", "msdf_coloring": "ink_trap" | "simple" }
//    起動オプション（検証・A/B 用。PC は --font-distance-field=<sdf|mtsdf>）: 設定より優先する。
//  既定は mtsdf・ink_trap（キーが無い・読めない値は既定のまま＋警告）。
//
//  【効く場所】
//  キャンバスの文字の描画器（canvas_text.rs の FontSystem。FontConfig::canvas）が作られるときに 1 回読む
//  （アトラスの形式とシェーダーの入口が決まるので、実行中は変わらない。描画の構成 render.profile と同じ流儀）。
//  ギズモ・操作ガイドの文字（ラテン数十字・小さい）は従来どおり 1 チャネルの SDF のまま（FontConfig::default）。
// ============================================================

use std::sync::RwLock;

use serde_json::Value;

use super::glyph_field::DistanceFieldKind;
use super::msdf::ColoringStrategy;

/// project_settings.json の文字の節のキー。
pub const FONT_KEY: &str = "font";
/// 距離場の種類のキー（font 節の中）。
pub const DISTANCE_FIELD_KEY: &str = "distance_field";
/// MSDF の辺の色分けのキー（font 節の中）。
pub const COLORING_KEY: &str = "msdf_coloring";
/// 起動ログの行頭の印。
pub const LOG_TAG: &str = "[SEED FONT]";
/// 1 グリフごとの焼き時間・検査の結果をログへ出す環境変数（値が 1 のとき。計測用）。
pub const FIELD_LOG_ENV: &str = "SEED_FONT_FIELD_LOG";

/// 文字の距離場の設定。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct FontFieldSettings {
    /// 距離場の種類。
    pub kind: DistanceFieldKind,
    /// MSDF の辺の色分け。
    pub coloring: ColoringStrategy,
}

impl FontFieldSettings {
    /// 起動ログの 1 行。
    pub fn log_line(&self, source: &str) -> String {
        format!("distance_field={} msdf_coloring={} source={source}", self.kind.as_str(), self.coloring.as_str())
    }
}

/// 設定を決める【純関数】: 既定 ← project_settings.json の font 節 ← 起動オプション。
///
/// * `settings_json` … project_settings.json の中身（読めなければ空文字列＝節なし）
/// * `launch`        … 起動オプションの距離場の種類（無ければ None）
///
/// 返り値は（設定・出どころの名前・警告）。
pub fn resolve_font_field_settings(settings_json: &str, launch: Option<&str>) -> (FontFieldSettings, &'static str, Vec<String>) {
    let mut settings = FontFieldSettings::default();
    let mut source = "既定";
    let mut warnings = Vec::new();
    if let Ok(root) = serde_json::from_str::<Value>(settings_json) {
        if let Some(font) = root.get(FONT_KEY).and_then(Value::as_object) {
            if let Some(v) = font.get(DISTANCE_FIELD_KEY) {
                match v.as_str().and_then(DistanceFieldKind::parse) {
                    Some(kind) => {
                        settings.kind = kind;
                        source = "project_settings.json";
                    }
                    None => warnings.push(format!("{FONT_KEY}.{DISTANCE_FIELD_KEY} = {v} は知りません（sdf / mtsdf）。既定のまま続けます")),
                }
            }
            if let Some(v) = font.get(COLORING_KEY) {
                match v.as_str().and_then(ColoringStrategy::parse) {
                    Some(c) => settings.coloring = c,
                    None => warnings.push(format!("{FONT_KEY}.{COLORING_KEY} = {v} は知りません（ink_trap / simple）。既定のまま続けます")),
                }
            }
        }
    }
    if let Some(launch) = launch {
        match DistanceFieldKind::parse(launch) {
            Some(kind) => {
                settings.kind = kind;
                source = "起動オプション";
            }
            None => warnings.push(format!("起動オプションの距離場 '{launch}' は知りません（sdf / mtsdf）。無視します")),
        }
    }
    (settings, source, warnings)
}

/// 今の設定（起動時に 1 回 install する。それまでは既定）。
static ACTIVE: RwLock<Option<FontFieldSettings>> = RwLock::new(None);

/// 今の設定をプロセスへ登録する（キャンバスの文字の描画器を作る前に 1 回）。
pub fn install(settings: FontFieldSettings) {
    let mut slot = ACTIVE.write().unwrap_or_else(|poisoned| poisoned.into_inner());
    *slot = Some(settings);
}

/// 今の設定（install 前は既定 = mtsdf・ink_trap）。
pub fn active() -> FontFieldSettings {
    let slot = ACTIVE.read().unwrap_or_else(|poisoned| poisoned.into_inner());
    slot.unwrap_or_default()
}

/// 1 グリフごとのログを出すか（環境変数 SEED_FONT_FIELD_LOG=1）。
pub fn per_glyph_log_enabled() -> bool {
    std::env::var(FIELD_LOG_ENV).is_ok_and(|v| v.trim() == "1")
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 節が無ければ既定（mtsdf・ink_trap）、設定で sdf・simple、起動オプションが設定より優先。読めない値は警告して既定。
    #[test]
    fn resolves_in_order() {
        let (s, src, w) = resolve_font_field_settings("", None);
        assert_eq!(s, FontFieldSettings { kind: DistanceFieldKind::Mtsdf, coloring: ColoringStrategy::InkTrap });
        assert_eq!(src, "既定");
        assert!(w.is_empty());

        let json = r#"{"font":{"distance_field":"sdf","msdf_coloring":"simple"}}"#;
        let (s, src, _) = resolve_font_field_settings(json, None);
        assert_eq!(s.kind, DistanceFieldKind::Sdf);
        assert_eq!(s.coloring, ColoringStrategy::Simple);
        assert_eq!(src, "project_settings.json");

        let (s, src, _) = resolve_font_field_settings(json, Some("mtsdf"));
        assert_eq!(s.kind, DistanceFieldKind::Mtsdf, "起動オプションが優先");
        assert_eq!(src, "起動オプション");

        let (s, _, w) = resolve_font_field_settings(r#"{"font":{"distance_field":"bitmap"}}"#, Some("nope"));
        assert_eq!(s.kind, DistanceFieldKind::Mtsdf);
        assert_eq!(w.len(), 2, "設定と起動オプションの両方を警告: {w:?}");
    }
}
