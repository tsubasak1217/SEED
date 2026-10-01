// ============================================================
//  render_profile/resolve.rs — 実効の描画の構成を決める（既定 ← project_settings.json ← 起動オプション）
//
//  【重ねる順】
//    1. 定義の既定の構成（render_profiles.json の default_profile。full）
//    2. project_settings.json の render 節:
//         "render": { "profile": "ui", "post": true }
//       profile で構成を選び（知らない名前は警告して既定のまま）、ほかのキーは旗の上書き（flags.rs）
//    3. 起動オプション（検証用。PC は --render-profile=、Android は seed.render_profile）:
//         "<名前>[,キー=値,...]"   例: "full" / "ui,post=true" / ",memory_hint=memory_usage"（名前なし＝構成はそのまま）
//       名前があれば構成ごと置き換える（プロジェクトの上書きは捨てる＝比べるときに素の構成になる）。
//  読めない値はその項目だけを捨てて警告に残す（起動ログ [SEED RENDER PROFILE][WARN]）。
// ============================================================

use serde_json::Value;

use super::catalog::{ProfileCatalog, FALLBACK_PROFILE_NAME};
use super::flags::{apply_flag, overlay_flags, RenderProfileFlags};

/// project_settings.json の描画の構成の節のキー。
pub const RENDER_KEY: &str = "render";
/// 描画の構成の名前のキー（render 節の中）。
pub const PROFILE_KEY: &str = "profile";
/// 起動オプションの「名前,キー=値,…」の区切り。
const LAUNCH_SEPARATOR: char = ',';
/// 起動オプションのキーと値の区切り。
const LAUNCH_ASSIGN: char = '=';

/// 決めた構成の出どころ（ログ用）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProfileSource {
    /// 定義の既定（設定にも起動オプションにも指定が無い）。
    Default,
    /// project_settings.json の render 節。
    ProjectSettings,
    /// 起動オプション（--render-profile= / seed.render_profile）。
    Launch,
}

impl ProfileSource {
    /// ログに出す名前。
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Default => "既定",
            Self::ProjectSettings => "project_settings.json",
            Self::Launch => "起動オプション",
        }
    }
}

/// 実効の描画の構成。
#[derive(Debug, Clone, PartialEq)]
pub struct RenderProfile {
    /// 構成の名前（定義の名前。例 `ui`）。
    pub name: String,
    /// 構成の表示名（定義の label）。
    pub label: String,
    /// 実効の旗（構成 ← プロジェクトの上書き ← 起動オプションの上書き）。
    pub flags: RenderProfileFlags,
    /// 決めた出どころ（名前を最後に決めたもの）。
    pub source: ProfileSource,
    /// 決める途中の警告（起動ログへ出す）。
    pub warnings: Vec<String>,
}

impl Default for RenderProfile {
    /// full（すべて用意する。App の初期値。起動時に resolve の結果で置き換わる）。
    fn default() -> Self {
        Self {
            name: FALLBACK_PROFILE_NAME.to_string(),
            label: String::new(),
            flags: RenderProfileFlags::FULL,
            source: ProfileSource::Default,
            warnings: Vec::new(),
        }
    }
}

impl RenderProfile {
    /// 起動ログの 1 行（[SEED RENDER PROFILE] の後ろ）。
    pub fn log_line(&self) -> String {
        format!(
            "profile={}{} source={} flags: {}",
            self.name,
            if self.label.is_empty() { String::new() } else { format!("（{}）", self.label) },
            self.source.as_str(),
            self.flags.describe()
        )
    }
}

/// 構成の名前から構成を選ぶ（知らない名前は警告して None）。
fn pick<'a>(catalog: &'a ProfileCatalog, name: &str, context: &str, warnings: &mut Vec<String>) -> Option<&'a super::catalog::ProfileEntry> {
    let found = catalog.find(name);
    if found.is_none() {
        warnings.push(format!(
            "{context}: 構成 '{}' は知りません（使えるのは {}）。そのまま続けます",
            name.trim(),
            catalog.names().join(" / ")
        ));
    }
    found
}

/// 実効の描画の構成を決める【純関数】。
///
/// * `settings_json` … project_settings.json の中身（読めなければ空文字列＝節なし）
/// * `catalog`       … 構成の一覧（組み込みは `builtin_profile_catalog`）
/// * `launch`        … 起動オプションの指定（"<名前>[,キー=値,...]"。無ければ None）
pub fn resolve_render_profile(settings_json: &str, catalog: &ProfileCatalog, launch: Option<&str>) -> RenderProfile {
    let mut profile = RenderProfile::default();

    // ① 定義の既定。
    if let Some(entry) = catalog.find(&catalog.default_profile) {
        profile.name = entry.name.clone();
        profile.label = entry.label.clone();
        profile.flags = entry.flags;
    }

    // ② project_settings.json の render 節。
    if let Ok(root) = serde_json::from_str::<Value>(settings_json) {
        match root.get(RENDER_KEY) {
            None => {}
            Some(Value::Object(section)) => {
                let context = format!("project_settings.json の {RENDER_KEY}");
                match section.get(PROFILE_KEY) {
                    None => {}
                    Some(Value::String(name)) => {
                        if let Some(entry) = pick(catalog, name, &context, &mut profile.warnings) {
                            profile.name = entry.name.clone();
                            profile.label = entry.label.clone();
                            profile.flags = entry.flags;
                            profile.source = ProfileSource::ProjectSettings;
                        }
                    }
                    Some(other) => profile
                        .warnings
                        .push(format!("{context}.{PROFILE_KEY} は構成の名前（文字列）で指定してください（{other}）")),
                }
                overlay_flags(&mut profile.flags, section, &[PROFILE_KEY], &context, &mut profile.warnings);
            }
            Some(other) => profile
                .warnings
                .push(format!("project_settings.json の {RENDER_KEY} はオブジェクトで指定してください（{other}）")),
        }
    }

    // ③ 起動オプション（検証用）。
    if let Some(text) = launch.map(str::trim).filter(|t| !t.is_empty()) {
        let context = "起動オプションの描画の構成";
        let mut parts = text.split(LAUNCH_SEPARATOR);
        let name = parts.next().unwrap_or_default().trim();
        if !name.is_empty() {
            if let Some(entry) = pick(catalog, name, context, &mut profile.warnings) {
                profile.name = entry.name.clone();
                profile.label = entry.label.clone();
                profile.flags = entry.flags;
                profile.source = ProfileSource::Launch;
            }
        }
        for pair in parts.map(str::trim).filter(|p| !p.is_empty()) {
            let Some((key, value)) = pair.split_once(LAUNCH_ASSIGN) else {
                profile.warnings.push(format!("{context}: '{pair}' は キー=値 の形にしてください"));
                continue;
            };
            let value = Value::String(value.trim().to_string());
            if let Err(reason) = apply_flag(&mut profile.flags, key.trim(), &value) {
                profile.warnings.push(format!("{context}: {reason}"));
            }
        }
    }
    profile
}

#[cfg(test)]
mod tests {
    use super::super::catalog::builtin_profile_catalog;
    use super::super::flags::MemoryHint;
    use super::*;

    /// 設定に render 節が無ければ既定（full）＝すべて用意する（既存のプロジェクトは何も変わらない）。
    #[test]
    fn default_is_full_without_section() {
        let catalog = builtin_profile_catalog();
        for json in ["", "{}", r#"{"game_name":"x","deferred":true}"#, "壊れた JSON"] {
            let p = resolve_render_profile(json, catalog, None);
            assert_eq!(p.name, "full", "{json}");
            assert!(p.flags.is_full(), "{json}");
            assert_eq!(p.source, ProfileSource::Default);
            assert!(p.warnings.is_empty(), "{json}: {:?}", p.warnings);
        }
    }

    /// render.profile で ui を選ぶと 3D の資源を作らない旗になり、ほかのキーで旗を上書きできる。
    #[test]
    fn project_selects_ui_and_overrides_flags() {
        let catalog = builtin_profile_catalog();
        let p = resolve_render_profile(r#"{"render":{"profile":"ui","post":true}}"#, catalog, None);
        assert_eq!(p.name, "ui");
        assert_eq!(p.source, ProfileSource::ProjectSettings);
        assert!(!p.flags.scene_3d && !p.flags.bindless && !p.flags.shadows);
        assert!(p.flags.post, "上書きが効く");
        assert_eq!(p.flags.memory_hint, MemoryHint::MemoryUsage);
    }

    /// 知らない構成の名前・型の違いは警告して既定のまま。
    #[test]
    fn unknown_profile_warns_and_stays_full() {
        let catalog = builtin_profile_catalog();
        let p = resolve_render_profile(r#"{"render":{"profile":"tiny"}}"#, catalog, None);
        assert_eq!(p.name, "full");
        assert_eq!(p.warnings.len(), 1);
        let p = resolve_render_profile(r#"{"render":"ui"}"#, catalog, None);
        assert_eq!(p.name, "full");
        assert_eq!(p.warnings.len(), 1);
    }

    /// 起動オプションの名前は構成ごと置き換え（プロジェクトの上書きを捨てる）、キー=値 は旗を上書きする。
    #[test]
    fn launch_overrides_name_and_flags() {
        let catalog = builtin_profile_catalog();
        let settings = r#"{"render":{"profile":"ui","post":true}}"#;
        let p = resolve_render_profile(settings, catalog, Some("full"));
        assert_eq!(p.name, "full");
        assert_eq!(p.source, ProfileSource::Launch);
        assert!(p.flags.is_full(), "名前を指すと素の構成になる");
        let p = resolve_render_profile(settings, catalog, Some(",memory_hint=performance,bindless=true"));
        assert_eq!(p.name, "ui", "名前なしは構成をそのまま");
        assert!(p.flags.post, "プロジェクトの上書きは残る");
        assert!(p.flags.bindless);
        assert_eq!(p.flags.memory_hint, MemoryHint::Performance);
        let p = resolve_render_profile("{}", catalog, Some("ui,oops,gi=maybe"));
        assert_eq!(p.name, "ui");
        assert_eq!(p.warnings.len(), 2, "{:?}", p.warnings);
    }
}
