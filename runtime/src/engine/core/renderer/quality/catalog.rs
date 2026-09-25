// ============================================================
//  quality/catalog.rs — 組み込みの描画品質プリセット（runtime/config/render_presets.json）
//
//  【役割】
//  プリセットの定義（名前・表示名・説明・つまみ）はデータファイル 1 つに置き、ビルド時に埋め込む。
//  プリセットを足す・中身を変えるときは JSON だけを書き換えればよい（コードは触らない）。
//  エディタ（プロジェクト設定 → レンダリング品質）も同じファイルを埋め込んで一覧を出すので、
//  ランタイムとエディタで名前・中身が食い違わない。
//
//  【壊れていたら】
//  埋め込みのファイルが読めない（書式の誤り）ときは、プリセットを 1 つも持たない一覧にして警告を残す。
//  そのときは全プラットフォームが「何も下げない」になる（デスクトップの従来の描画と同じ。起動は止めない）。
//  書式の誤りは単体テスト（builtin_catalog_is_valid）がビルド前に止める。
// ============================================================

use std::sync::LazyLock;

use serde_json::Value;

use super::knobs::{parse_knobs, QualityKnobs};

/// 埋め込みのプリセット定義（runtime/config/render_presets.json）。
const BUILTIN_PRESETS_JSON: &str = include_str!("../../../../../config/render_presets.json");

/// プリセット定義の書式の版の欄。
const FORMAT_VERSION_KEY: &str = "format_version";
/// このランタイムが読めるプリセット定義の書式の版。
pub const PRESET_FORMAT_VERSION: u64 = 1;
/// プリセットの配列の欄。
const PRESETS_KEY: &str = "presets";
/// プリセットの名前の欄。
const NAME_KEY: &str = "name";
/// プリセットの表示名の欄。
const LABEL_KEY: &str = "label";
/// プリセットの説明の欄。
const DESCRIPTION_KEY: &str = "description";
/// プリセットのつまみの欄。
const KNOBS_KEY: &str = "knobs";

/// プリセット 1 つ。
#[derive(Debug, Clone, PartialEq)]
pub struct PresetEntry {
    /// 名前（設定・起動オプションで指すもの。例 `mobile`）。
    pub name: String,
    /// 表示名（エディタの一覧・ログ用）。
    pub label: String,
    /// 説明（エディタの一覧用）。
    pub description: String,
    /// つまみ（書かれていないつまみは「設定のまま」）。
    pub knobs: QualityKnobs,
}

/// プリセットの一覧。
#[derive(Debug, Clone, Default, PartialEq)]
pub struct PresetCatalog {
    /// 定義順のプリセット。
    pub presets: Vec<PresetEntry>,
    /// 読み取り中の警告（読めないつまみ・重複した名前など）。
    pub warnings: Vec<String>,
}

impl PresetCatalog {
    /// 名前でプリセットを引く（前後の空白・大文字小文字は区別しない）。
    pub fn find(&self, name: &str) -> Option<&PresetEntry> {
        let wanted = name.trim();
        self.presets.iter().find(|preset| preset.name.eq_ignore_ascii_case(wanted))
    }

    /// プリセットの名前の一覧（定義順。ログ・警告の案内用）。
    pub fn names(&self) -> Vec<&str> {
        self.presets.iter().map(|preset| preset.name.as_str()).collect()
    }

    /// プリセット定義の JSON を読む【純関数】。
    ///
    /// 書式として読めない（JSON の誤り・版の違い・配列が無い）ときは空の一覧と理由を返す。
    /// 個々のプリセットの読めない欄は、その欄だけを捨てて警告に残す。
    pub fn parse(json: &str) -> Self {
        let mut catalog = Self::default();
        let root: Value = match serde_json::from_str(json) {
            Ok(value) => value,
            Err(err) => {
                catalog.warnings.push(format!("プリセット定義を JSON として読めません: {err}"));
                return catalog;
            }
        };
        match root.get(FORMAT_VERSION_KEY).and_then(Value::as_u64) {
            Some(PRESET_FORMAT_VERSION) => {}
            other => {
                catalog.warnings.push(format!(
                    "プリセット定義の版 {other:?} は読めません（このランタイムは {PRESET_FORMAT_VERSION}）"
                ));
                return catalog;
            }
        }
        let Some(entries) = root.get(PRESETS_KEY).and_then(Value::as_array) else {
            catalog.warnings.push(format!("プリセット定義に {PRESETS_KEY} の配列がありません"));
            return catalog;
        };
        for (index, entry) in entries.iter().enumerate() {
            let Some(name) = entry.get(NAME_KEY).and_then(Value::as_str).map(str::trim) else {
                catalog.warnings.push(format!("{index} 番目のプリセットに名前がありません（読み飛ばしました）"));
                continue;
            };
            if name.is_empty() || catalog.find(name).is_some() {
                catalog.warnings.push(format!("プリセット名 '{name}' が空か重複しています（読み飛ばしました）"));
                continue;
            }
            let context = format!("プリセット {name}");
            let knobs = match entry.get(KNOBS_KEY) {
                Some(Value::Object(map)) => parse_knobs(map, &[], &context, &mut catalog.warnings),
                None => QualityKnobs::NONE,
                Some(other) => {
                    catalog.warnings.push(format!("{context}: {KNOBS_KEY} がオブジェクトではありません（{other}）"));
                    QualityKnobs::NONE
                }
            };
            let text = |key: &str| entry.get(key).and_then(Value::as_str).unwrap_or_default().to_string();
            catalog.presets.push(PresetEntry {
                name: name.to_string(),
                label: text(LABEL_KEY),
                description: text(DESCRIPTION_KEY),
                knobs,
            });
        }
        catalog
    }
}

/// 埋め込みのプリセット一覧（プロセスで 1 回だけ読む）。
pub fn builtin_catalog() -> &'static PresetCatalog {
    static CATALOG: LazyLock<PresetCatalog> =
        LazyLock::new(|| PresetCatalog::parse(BUILTIN_PRESETS_JSON));
    &CATALOG
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::renderer::render_features::GiMode;
    use crate::engine::platform;

    /// 埋め込みの定義が警告なしで読め、各プラットフォームの既定プリセットがあること。
    #[test]
    fn builtin_catalog_is_valid() {
        let catalog = builtin_catalog();
        assert!(catalog.warnings.is_empty(), "{:?}", catalog.warnings);
        for traits in platform::ALL {
            assert!(
                catalog.find(traits.default_render_quality).is_some(),
                "既定プリセット {} が定義に無い",
                traits.default_render_quality
            );
        }
        // すべてのプリセットに表示名と説明がある（エディタの一覧に出す）。
        for preset in &catalog.presets {
            assert!(!preset.label.is_empty(), "{} の表示名が無い", preset.name);
            assert!(!preset.description.is_empty(), "{} の説明が無い", preset.name);
        }
    }

    /// デスクトップの既定プリセットは何も下げない（従来の描画そのもの）。
    #[test]
    fn desktop_preset_changes_nothing() {
        let desktop = builtin_catalog().find(platform::DESKTOP.default_render_quality).unwrap();
        assert!(desktop.knobs.is_empty(), "desktop は空でなければならない: {:?}", desktop.knobs);
    }

    /// モバイルの既定プリセットは描画スケールを下げ、重い後処理を止める（計測に基づく軽量化の中身）。
    #[test]
    fn mobile_preset_lowers_the_heavy_parts() {
        let mobile = builtin_catalog().find(platform::ANDROID.default_render_quality).unwrap();
        let knobs = mobile.knobs;
        assert!(knobs.render_scale.is_some_and(|s| s < 1.0), "{knobs:?}");
        assert_eq!(knobs.gi, Some(GiMode::Flat), "SSGI を止める");
        assert_eq!(knobs.deferred, Some(false), "G-Buffer を使わない");
        assert_eq!(knobs.bloom, Some(false));
    }

    /// 名前の引き方は空白・大文字小文字を区別しない。
    #[test]
    fn find_is_case_insensitive() {
        let catalog = builtin_catalog();
        assert!(catalog.find(" Mobile ").is_some());
        assert!(catalog.find("no_such_preset").is_none());
        assert!(catalog.names().contains(&"desktop"));
    }

    /// 壊れた定義は空の一覧＋理由（起動は止めない）。重複した名前・読めない欄は飛ばす。
    #[test]
    fn broken_definitions_are_reported() {
        let broken = PresetCatalog::parse("not json");
        assert!(broken.presets.is_empty());
        assert_eq!(broken.warnings.len(), 1);

        let wrong_version = PresetCatalog::parse(r#"{"format_version": 99, "presets": []}"#);
        assert!(wrong_version.presets.is_empty());
        assert!(wrong_version.warnings[0].contains("版"));

        let partial = PresetCatalog::parse(
            r#"{"format_version": 1, "presets": [
                {"name": "a", "label": "A", "description": "d", "knobs": {"bloom": false, "oops": 1}},
                {"name": "A", "knobs": {}},
                {"label": "名前なし"},
                {"name": "b", "knobs": 5}
            ]}"#,
        );
        assert_eq!(partial.names(), vec!["a", "b"]);
        assert_eq!(partial.find("a").unwrap().knobs.bloom, Some(false));
        assert!(partial.find("b").unwrap().knobs.is_empty());
        assert_eq!(partial.warnings.len(), 4, "{:?}", partial.warnings);
    }
}
