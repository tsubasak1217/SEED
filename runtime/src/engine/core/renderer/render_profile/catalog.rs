// ============================================================
//  render_profile/catalog.rs — 組み込みの描画の構成（runtime/config/render_profiles.json）
//
//  【役割】
//  構成の定義（名前・表示名・説明・旗）はデータファイル 1 つに置き、ビルド時に埋め込む。
//  構成を足す・中身を変えるときは JSON だけを書き換えればよい（コードは触らない）。
//
//  【壊れていたら】
//  埋め込みのファイルが読めない（書式の誤り）ときは、構成を 1 つも持たない一覧にして警告を残す。
//  そのときは full（すべて用意する＝従来の描画）で動く（起動は止めない）。
//  書式の誤りは単体テスト（builtin_catalog_is_valid）がビルド前に止める。
// ============================================================

use std::sync::LazyLock;

use serde_json::Value;

use super::flags::{overlay_flags, RenderProfileFlags};

/// 埋め込みの構成の定義（runtime/config/render_profiles.json）。
const BUILTIN_PROFILES_JSON: &str = include_str!("../../../../../config/render_profiles.json");

/// 構成の定義の書式の版の欄。
const FORMAT_VERSION_KEY: &str = "format_version";
/// このランタイムが読める構成の定義の書式の版。
pub const PROFILE_FORMAT_VERSION: u64 = 1;
/// 既定の構成の名前の欄。
const DEFAULT_PROFILE_KEY: &str = "default_profile";
/// 構成の配列の欄。
const PROFILES_KEY: &str = "profiles";
/// 構成の名前の欄。
const NAME_KEY: &str = "name";
/// 構成の表示名の欄。
const LABEL_KEY: &str = "label";
/// 構成の説明の欄。
const DESCRIPTION_KEY: &str = "description";
/// 構成の旗の欄。
const FLAGS_KEY: &str = "flags";

/// 定義に既定の構成の名前が無いときの名前（full＝すべて用意する）。
pub const FALLBACK_PROFILE_NAME: &str = "full";

/// 構成 1 つ。
#[derive(Debug, Clone, PartialEq)]
pub struct ProfileEntry {
    /// 名前（設定・起動オプションで指すもの。例 `ui`）。
    pub name: String,
    /// 表示名（ログ用）。
    pub label: String,
    /// 説明（ドキュメント・ログ用）。
    pub description: String,
    /// 旗（書かれていない旗は既定＝用意する）。
    pub flags: RenderProfileFlags,
}

/// 構成の一覧。
#[derive(Debug, Clone, PartialEq)]
pub struct ProfileCatalog {
    /// 既定の構成の名前（project_settings.json に render.profile が無いとき）。
    pub default_profile: String,
    /// 定義順の構成。
    pub profiles: Vec<ProfileEntry>,
    /// 読み取り中の警告（読めない旗・重複した名前など）。
    pub warnings: Vec<String>,
}

impl Default for ProfileCatalog {
    fn default() -> Self {
        Self {
            default_profile: FALLBACK_PROFILE_NAME.to_string(),
            profiles: Vec::new(),
            warnings: Vec::new(),
        }
    }
}

impl ProfileCatalog {
    /// 名前で構成を引く（前後の空白・大文字小文字は区別しない）。
    pub fn find(&self, name: &str) -> Option<&ProfileEntry> {
        let wanted = name.trim();
        self.profiles.iter().find(|profile| profile.name.eq_ignore_ascii_case(wanted))
    }

    /// 構成の名前の一覧（定義順。警告の案内用）。
    pub fn names(&self) -> Vec<&str> {
        self.profiles.iter().map(|profile| profile.name.as_str()).collect()
    }

    /// 構成の定義の JSON を読む【純関数】。
    ///
    /// 書式として読めない（JSON の誤り・版の違い・配列が無い）ときは空の一覧と理由を返す。
    /// 個々の構成の読めない旗は、その旗だけを捨てて警告に残す。
    pub fn parse(json: &str) -> Self {
        let mut catalog = Self::default();
        let root: Value = match serde_json::from_str(json) {
            Ok(value) => value,
            Err(err) => {
                catalog.warnings.push(format!("描画の構成の定義を JSON として読めません: {err}"));
                return catalog;
            }
        };
        match root.get(FORMAT_VERSION_KEY).and_then(Value::as_u64) {
            Some(PROFILE_FORMAT_VERSION) => {}
            other => {
                catalog.warnings.push(format!(
                    "描画の構成の定義の版 {other:?} は読めません（このランタイムは {PROFILE_FORMAT_VERSION}）"
                ));
                return catalog;
            }
        }
        if let Some(name) = root.get(DEFAULT_PROFILE_KEY).and_then(Value::as_str) {
            catalog.default_profile = name.trim().to_string();
        }
        let Some(entries) = root.get(PROFILES_KEY).and_then(Value::as_array) else {
            catalog.warnings.push(format!("描画の構成の定義に {PROFILES_KEY} の配列がありません"));
            return catalog;
        };
        for (index, entry) in entries.iter().enumerate() {
            let Some(name) = entry.get(NAME_KEY).and_then(Value::as_str).map(str::trim) else {
                catalog.warnings.push(format!("{index} 番目の構成に名前がありません（読み飛ばしました）"));
                continue;
            };
            if name.is_empty() || catalog.find(name).is_some() {
                catalog.warnings.push(format!("構成の名前 '{name}' が空か重複しています（読み飛ばしました）"));
                continue;
            }
            let context = format!("構成 {name}");
            let mut flags = RenderProfileFlags::default();
            match entry.get(FLAGS_KEY) {
                Some(Value::Object(map)) => overlay_flags(&mut flags, map, &[], &context, &mut catalog.warnings),
                None => {}
                Some(other) => {
                    catalog.warnings.push(format!("{context}: {FLAGS_KEY} がオブジェクトではありません（{other}）"));
                }
            }
            let text = |key: &str| entry.get(key).and_then(Value::as_str).unwrap_or_default().to_string();
            catalog.profiles.push(ProfileEntry {
                name: name.to_string(),
                label: text(LABEL_KEY),
                description: text(DESCRIPTION_KEY),
                flags,
            });
        }
        if catalog.find(&catalog.default_profile).is_none() {
            catalog.warnings.push(format!(
                "既定の構成 '{}' が定義にありません（{FALLBACK_PROFILE_NAME} として動きます）",
                catalog.default_profile
            ));
        }
        catalog
    }
}

/// 組み込みの構成の一覧（プロセスで 1 回だけ読む）。
pub fn builtin_profile_catalog() -> &'static ProfileCatalog {
    static CATALOG: LazyLock<ProfileCatalog> = LazyLock::new(|| ProfileCatalog::parse(BUILTIN_PROFILES_JSON));
    &CATALOG
}

#[cfg(test)]
mod tests {
    use super::super::flags::MemoryHint;
    use super::*;

    /// 埋め込みの定義は警告なしで読め、full（何も止めない）と ui（3D の資源を作らない）を持つ。
    #[test]
    fn builtin_catalog_is_valid() {
        let catalog = ProfileCatalog::parse(BUILTIN_PROFILES_JSON);
        assert!(catalog.warnings.is_empty(), "警告: {:?}", catalog.warnings);
        assert_eq!(catalog.default_profile, "full");
        let full = catalog.find("full").expect("full がある");
        assert!(full.flags.is_full(), "full は既定の旗のまま（従来と同じ）");
        let ui = catalog.find("UI").expect("ui がある（大文字小文字は区別しない）");
        assert!(!ui.flags.scene_3d);
        assert!(!ui.flags.deferred);
        assert!(!ui.flags.shadows);
        assert!(!ui.flags.gi);
        assert!(!ui.flags.bindless);
        assert!(!ui.flags.ray_tracing);
        assert!(!ui.flags.post);
        assert!(!ui.flags.picking);
        assert_eq!(ui.flags.memory_hint, MemoryHint::MemoryUsage);
    }

    /// 壊れた定義は空の一覧と警告（起動は止めない）。重複した名前は後のものを捨てる。
    #[test]
    fn broken_or_duplicate_definitions_warn() {
        let broken = ProfileCatalog::parse("{");
        assert!(broken.profiles.is_empty());
        assert_eq!(broken.warnings.len(), 1);
        let wrong_version = ProfileCatalog::parse(r#"{"format_version":9,"profiles":[]}"#);
        assert!(wrong_version.profiles.is_empty());
        let dup = ProfileCatalog::parse(
            r#"{"format_version":1,"default_profile":"a","profiles":[{"name":"a"},{"name":"A","flags":{"gi":false}}]}"#,
        );
        assert_eq!(dup.profiles.len(), 1);
        assert!(dup.find("a").unwrap().flags.gi, "重複した後の定義は読まない");
        assert_eq!(dup.warnings.len(), 1);
    }
}
