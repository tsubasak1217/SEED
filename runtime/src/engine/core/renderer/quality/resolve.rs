// ============================================================
//  quality/resolve.rs — 実効の描画品質を決める（プラットフォーム既定・プロジェクト設定・起動オプション）
//
//  【決め方（後に書いたものが勝つ）】
//    1. プリセット名 … 起動オプションの指定 ＞ project_settings.json の render_quality.<プラットフォーム>.preset
//                      ＞ プラットフォームの既定（PlatformTraits::default_render_quality。Android は mobile）
//    2. つまみ       … プリセットのつまみ ← プロジェクト設定の同じ節のつまみ ← 起動オプションのつまみ
//  知らないプリセット名はプラットフォームの既定へ戻す（警告を残す）。
//
//  【project_settings.json の書き方】
//    "render_quality": {
//      "desktop": { "preset": "desktop" },
//      "android": { "preset": "mobile", "render_scale": 0.75 }
//    }
//  節の名前は PlatformTraits::quality_platform_key（desktop / android）。節が無ければプラットフォームの既定。
//
//  【起動オプション（計測・検証用。docs/android.md §22）】
//    プリセット名         … Android: am start --es seed.quality <名前> ／ PC: --render-quality=<名前>
//    つまみの上書き       … Android: --es seed.quality_overrides 'render_scale=0.75,deferred=true'
//                           PC: --render-quality-overrides=render_scale=0.75,deferred=true
//    書式は「キー=値」をカンマで区切った並び。値は JSON として読めればその値（数・true/false）、
//    読めなければ文字列（gi=flat など）。
//
//  すべて純粋な処理（ファイル・OS に触らない）。起動時に 1 回だけ呼ばれる（app_init.rs）。
// ============================================================

use serde_json::Value;

use crate::engine::platform::PlatformTraits;

use super::catalog::PresetCatalog;
use super::knobs::{apply_knob, parse_knobs, QualityKnobs, MAX_RENDER_SCALE};

/// project_settings.json の描画品質の節のキー。
pub const RENDER_QUALITY_KEY: &str = "render_quality";

/// プラットフォームの節の中でプリセット名を書く欄。
pub const PRESET_KEY: &str = "preset";

/// 起動オプションのつまみの上書きの区切り（キー=値 の組の間）。
const OVERRIDE_PAIR_SEPARATOR: char = ',';

/// 起動オプションのつまみの上書きの区切り（キーと値の間）。
const OVERRIDE_KEY_VALUE_SEPARATOR: char = '=';

/// 起動オプションで渡す描画品質の指定（計測・検証用。無ければ設定どおり）。
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct QualityLaunchOverrides {
    /// プリセット名（空・空白だけなら指定なし）。
    pub preset: Option<String>,
    /// つまみの上書き（`キー=値,キー=値`。空なら指定なし）。
    pub knobs: Option<String>,
}

/// 実効の描画品質（このプロセスで使うプリセットとつまみ）。
#[derive(Debug, Clone, PartialEq)]
pub struct RenderQuality {
    /// 使うプリセットの名前。
    pub preset: String,
    /// 実効のつまみ（プリセット ← プロジェクト設定 ← 起動オプション の順に重ねたもの）。
    pub knobs: QualityKnobs,
    /// 決める途中の警告（知らないプリセット名・読めないつまみなど。起動ログへ出す）。
    pub warnings: Vec<String>,
}

impl Default for RenderQuality {
    /// 何も下げない品質（App の初期値。起動時に `resolve_render_quality` の結果で置き換わる）。
    fn default() -> Self {
        Self {
            preset: crate::engine::platform::DESKTOP_DEFAULT_RENDER_QUALITY.to_string(),
            knobs: QualityKnobs::NONE,
            warnings: Vec::new(),
        }
    }
}

impl RenderQuality {
    /// ゲーム画面の描画スケール（指定が無ければ等倍）。
    pub fn render_scale(&self) -> f32 {
        self.knobs.render_scale.unwrap_or(MAX_RENDER_SCALE)
    }

    /// 起動ログ 1 行（`[SEED QUALITY]` の後ろ）。
    pub fn log_line(&self) -> String {
        format!("preset={} {}", self.preset, self.knobs.describe())
    }
}

/// 実効の描画品質を決める【純関数】。
///
/// # 引数
/// * `settings_json` - project_settings.json の中身（読めなければ空文字列。節が無いものとして扱う）
/// * `platform`      - このビルドのプラットフォームの特性（節の名前と既定のプリセット名）
/// * `catalog`       - プリセットの一覧（本番は `builtin_catalog()`）
/// * `launch`        - 起動オプションの指定（計測・検証用）
pub fn resolve_render_quality(
    settings_json: &str,
    platform: &PlatformTraits,
    catalog: &PresetCatalog,
    launch: &QualityLaunchOverrides,
) -> RenderQuality {
    let mut warnings: Vec<String> = Vec::new();

    // ── プロジェクト設定のこのプラットフォームの節 ──
    let settings: Value = serde_json::from_str(settings_json).unwrap_or(Value::Null);
    let section = platform_section(&settings, platform.quality_platform_key, &mut warnings);

    // ── 1. プリセット名 ──
    let project_preset = section
        .and_then(|map| map.get(PRESET_KEY))
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|name| !name.is_empty());
    let launch_preset = launch.preset.as_deref().map(str::trim).filter(|name| !name.is_empty());
    let requested = launch_preset.or(project_preset).unwrap_or(platform.default_render_quality);
    let preset = match catalog.find(requested) {
        Some(entry) => Some(entry),
        None => {
            warnings.push(format!(
                "プリセット '{requested}' はありません（{}）。既定の '{}' を使います",
                catalog.names().join(" / "),
                platform.default_render_quality
            ));
            catalog.find(platform.default_render_quality)
        }
    };
    let preset_name = preset
        .map(|entry| entry.name.clone())
        .unwrap_or_else(|| platform.default_render_quality.to_string());

    // ── 2. つまみ（プリセット ← プロジェクト ← 起動オプション）──
    let mut knobs = preset.map(|entry| entry.knobs).unwrap_or(QualityKnobs::NONE);
    if let Some(map) = section {
        let context = format!("project_settings.json の {RENDER_QUALITY_KEY}.{}", platform.quality_platform_key);
        let project = parse_knobs(map, &[PRESET_KEY], &context, &mut warnings);
        knobs = knobs.overlaid(&project);
    }
    if let Some(text) = launch.knobs.as_deref() {
        let launched = parse_override_list(text, &mut warnings);
        knobs = knobs.overlaid(&launched);
    }

    RenderQuality { preset: preset_name, knobs, warnings }
}

/// project_settings.json の `render_quality.<key>` をオブジェクトとして取り出す（無ければ None）。
/// 節の型が違うときは警告して無いものとして扱う。
fn platform_section<'a>(
    settings: &'a Value,
    key: &str,
    warnings: &mut Vec<String>,
) -> Option<&'a serde_json::Map<String, Value>> {
    let root = settings.get(RENDER_QUALITY_KEY)?;
    let Some(root) = root.as_object() else {
        warnings.push(format!("project_settings.json の {RENDER_QUALITY_KEY} がオブジェクトではありません（無視しました）"));
        return None;
    };
    match root.get(key)? {
        Value::Object(map) => Some(map),
        other => {
            warnings.push(format!(
                "project_settings.json の {RENDER_QUALITY_KEY}.{key} がオブジェクトではありません（{other}。無視しました）"
            ));
            None
        }
    }
}

/// 起動オプションのつまみの上書き（`キー=値,キー=値`）を読む【純関数】。
///
/// 値は JSON として読めればその値（`0.75` / `true`）、読めなければ文字列（`flat`）として扱う。
/// 書式の誤り（`=` が無い）・読めないつまみは、その組だけを捨てて警告に残す。
pub fn parse_override_list(text: &str, warnings: &mut Vec<String>) -> QualityKnobs {
    let mut knobs = QualityKnobs::NONE;
    for pair in text.split(OVERRIDE_PAIR_SEPARATOR).map(str::trim).filter(|pair| !pair.is_empty()) {
        let Some((key, raw)) = pair.split_once(OVERRIDE_KEY_VALUE_SEPARATOR) else {
            warnings.push(format!("起動オプションの品質の上書き '{pair}' に = がありません（キー=値 で書いてください）"));
            continue;
        };
        let raw = raw.trim();
        let value = serde_json::from_str::<Value>(raw).unwrap_or_else(|_| Value::String(raw.to_string()));
        if let Err(reason) = apply_knob(&mut knobs, key.trim(), &value) {
            warnings.push(format!("起動オプション: {reason}"));
        }
    }
    knobs
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::renderer::quality::catalog::builtin_catalog;
    use crate::engine::core::renderer::render_features::GiMode;
    use crate::engine::platform::{ANDROID, DESKTOP};

    fn resolve(settings: &str, platform: &PlatformTraits, launch: &QualityLaunchOverrides) -> RenderQuality {
        resolve_render_quality(settings, platform, builtin_catalog(), launch)
    }

    /// 設定が無ければ: デスクトップは何も下げない（従来どおり）、Android は mobile。
    #[test]
    fn defaults_per_platform() {
        let none = QualityLaunchOverrides::default();
        let desktop = resolve("{}", &DESKTOP, &none);
        assert_eq!(desktop.preset, "desktop");
        assert!(desktop.knobs.is_empty(), "{:?}", desktop.knobs);
        assert!(desktop.warnings.is_empty());
        assert_eq!(desktop.render_scale(), 1.0);

        let android = resolve("{}", &ANDROID, &none);
        assert_eq!(android.preset, "mobile");
        assert_eq!(android.knobs, builtin_catalog().find("mobile").unwrap().knobs);
        assert!(android.render_scale() < 1.0);

        // 読めない JSON（空文字列を含む）も「節なし」と同じ。
        assert_eq!(resolve("", &DESKTOP, &none), desktop);
        assert_eq!(resolve("not json", &ANDROID, &none), android);
    }

    /// デスクトップの節に関係の無い他プラットフォームの設定があっても、デスクトップは何も下げない。
    #[test]
    fn other_platform_section_does_not_leak() {
        let settings = r#"{"render_quality": {"android": {"preset": "mobile_low", "render_scale": 0.6}}}"#;
        let desktop = resolve(settings, &DESKTOP, &QualityLaunchOverrides::default());
        assert_eq!(desktop.preset, "desktop");
        assert!(desktop.knobs.is_empty());
        let android = resolve(settings, &ANDROID, &QualityLaunchOverrides::default());
        assert_eq!(android.preset, "mobile_low");
        assert_eq!(android.knobs.render_scale, Some(0.6), "節のつまみがプリセットに勝つ");
        assert_eq!(android.knobs.shadows, Some(false), "上書きしないつまみはプリセットのまま");
    }

    /// プロジェクト設定の上書き: プリセットを変えずにつまみだけ上書きできる。
    #[test]
    fn project_overrides_on_default_preset() {
        let settings = r#"{"render_quality": {"android": {"render_scale": 0.75, "gi": "ssgi", "bloom": true}}}"#;
        let q = resolve(settings, &ANDROID, &QualityLaunchOverrides::default());
        assert_eq!(q.preset, "mobile");
        assert_eq!(q.knobs.render_scale, Some(0.75));
        assert_eq!(q.knobs.gi, Some(GiMode::Ssgi));
        assert_eq!(q.knobs.bloom, Some(true));
        assert!(q.warnings.is_empty(), "{:?}", q.warnings);

        // デスクトップでもプリセットを選べる（例: 非力な PC で mobile）。
        let settings = r#"{"render_quality": {"desktop": {"preset": "mobile"}}}"#;
        let q = resolve(settings, &DESKTOP, &QualityLaunchOverrides::default());
        assert_eq!(q.preset, "mobile");
        assert!(q.render_scale() < 1.0);
    }

    /// 起動オプションはプロジェクト設定に勝つ（プリセット名もつまみも）。
    #[test]
    fn launch_overrides_win() {
        let settings = r#"{"render_quality": {"android": {"preset": "mobile_low", "render_scale": 0.6}}}"#;
        let launch = QualityLaunchOverrides {
            preset: Some("desktop".to_string()),
            knobs: Some("render_scale=0.8, deferred=true ,gi=ssgi".to_string()),
        };
        let q = resolve(settings, &ANDROID, &launch);
        assert_eq!(q.preset, "desktop");
        // desktop（空）← プロジェクトの render_scale=0.6 ← 起動オプションの 0.8
        assert_eq!(q.knobs.render_scale, Some(0.8));
        assert_eq!(q.knobs.deferred, Some(true));
        assert_eq!(q.knobs.gi, Some(GiMode::Ssgi));
        assert!(q.warnings.is_empty(), "{:?}", q.warnings);
        // 空白だけのプリセット名は指定なし。
        let blank = QualityLaunchOverrides { preset: Some("  ".to_string()), knobs: None };
        assert_eq!(resolve("{}", &ANDROID, &blank).preset, "mobile");
    }

    /// 知らないプリセット名はプラットフォームの既定へ戻し、理由を残す。
    #[test]
    fn unknown_preset_falls_back_to_platform_default() {
        let settings = r#"{"render_quality": {"android": {"preset": "ultra"}}}"#;
        let q = resolve(settings, &ANDROID, &QualityLaunchOverrides::default());
        assert_eq!(q.preset, "mobile");
        assert_eq!(q.warnings.len(), 1);
        assert!(q.warnings[0].contains("ultra"), "{:?}", q.warnings);
    }

    /// 型の違う節は無いものとして扱う（起動は止めない）。
    #[test]
    fn wrong_section_types_are_ignored() {
        let q = resolve(r#"{"render_quality": "mobile"}"#, &DESKTOP, &QualityLaunchOverrides::default());
        assert!(q.knobs.is_empty());
        assert_eq!(q.warnings.len(), 1);
        let q = resolve(r#"{"render_quality": {"desktop": 3}}"#, &DESKTOP, &QualityLaunchOverrides::default());
        assert!(q.knobs.is_empty());
        assert_eq!(q.warnings.len(), 1);
    }

    /// 起動オプションの上書きの書式: 読めない組だけ捨てる。
    #[test]
    fn override_list_format() {
        let mut warnings = Vec::new();
        let knobs = parse_override_list("render_scale=0.7,bloom,,shadows=false, gi = flat ,x=1", &mut warnings);
        assert_eq!(knobs.render_scale, Some(0.7));
        assert_eq!(knobs.shadows, Some(false));
        assert_eq!(knobs.gi, Some(GiMode::Flat));
        assert_eq!(warnings.len(), 2, "{warnings:?}");
        assert!(parse_override_list("", &mut Vec::new()).is_empty());
    }

    /// ログ 1 行にプリセット名とつまみが出る。
    #[test]
    fn log_line_mentions_preset_and_knobs() {
        let q = resolve("{}", &ANDROID, &QualityLaunchOverrides::default());
        let line = q.log_line();
        assert!(line.starts_with("preset=mobile "), "{line}");
        assert!(line.contains("render_scale="), "{line}");
        let desktop = resolve("{}", &DESKTOP, &QualityLaunchOverrides::default());
        assert!(desktop.log_line().contains("下げるつまみなし"), "{}", desktop.log_line());
    }
}
