// ============================================================
//  font/inline/icon_set.rs — アイコンセット（.icons）の定義とローダ
//
//  【役割】
//  `[icon:名前]` 記法の「名前 → 画像パス（＋既定の高さ倍率）」を与えるデータ表。
//  ファイルは JSON で、`asset_fs` 経由で読むため PAK パッケージ版でもそのまま動く。
//
//  【ファイル形式（.icons / JSON）】
//  ```json
//  {
//    "icons": {
//      "key_w":   "assets://ui/key_w.png",
//      "mouse_l": { "path": "assets://ui/mouse_l.png", "h": 1.2 }
//    }
//  }
//  ```
//  値は **文字列**（パスのみ）か **オブジェクト**（`path` と既定倍率 `h`）のどちらでもよい。
//  文字列形式は「倍率は既定（1.0）でよい」大多数のアイコンを 1 行で書くための省略形。
//
//  【データドリブンの意図】
//  アイコンの追加・差し替えは .icons を書き換えるだけで済み、
//  シーンやスクリプトの本文（`[icon:名前]`）は一切変更しなくてよい。
//
//  【キャッシュ方針】
//  読み込み・パースの結果はパス単位でプロセス内にキャッシュする。
//  **失敗も記録する**（毎フレーム走る経路なので、失敗を記録しないと
//  ディスク I/O と警告ログが溢れる）。アセットのホットリロード時は
//  `invalidate` / `invalidate_all` で破棄する。
// ============================================================

use std::collections::HashMap;
use std::sync::{Arc, Mutex, OnceLock};

use serde::Deserialize;

use crate::engine::asset_fs;

/// アイコンセットファイルの拡張子（インスペクタのファイル参照フィルタと共有する）。
pub const ICON_SET_EXTENSION: &str = ".icons";

// ─── JSON スキーマ ────────────────────────────────────────────

/// `.icons` のトップレベル。
///
/// 未知のキーは無視する（将来の拡張で古いランタイムが壊れないように）。
#[derive(Debug, Deserialize)]
struct IconSetFile {
    /// 名前 → エントリの表。省略時は空（＝すべて未解決になる）。
    #[serde(default)]
    icons: HashMap<String, IconEntryJson>,
}

/// アイコン 1 件の JSON 表現（文字列の省略形とオブジェクト形の両対応）。
#[derive(Debug, Deserialize)]
#[serde(untagged)]
enum IconEntryJson {
    /// 省略形: 画像パスだけを書く。
    Path(String),
    /// 詳細形: パスと既定の高さ倍率。
    Detailed {
        /// 画像の assets:// パス。
        path: String,
        /// 既定の高さ倍率（フォントサイズに対する倍率）。省略可。
        #[serde(default)]
        h: Option<f32>,
    },
}

// ─── 公開型 ────────────────────────────────────────────────────

/// アイコン 1 件の解決済み定義。
#[derive(Clone, Debug, PartialEq)]
pub struct IconEntry {
    /// 画像の assets:// パス。
    pub path: String,
    /// このアイコン固有の既定高さ倍率。`None` = 全体既定（1.0）。
    ///
    /// 本文側の `h=` 指定があればそちらが優先される（記法 > セット既定 > 全体既定）。
    pub height_scale: Option<f32>,
}

/// アイコンセット（名前 → 定義）。
#[derive(Debug, Default)]
pub struct IconSet {
    /// 名前 → 定義。
    entries: HashMap<String, IconEntry>,
}

impl IconSet {
    /// JSON 文字列を解析してアイコンセットを作る（ファイル I/O なし＝テスト可能）。
    ///
    /// 高さ倍率は記法パーサと同じ上下限（`markup::MIN_HEIGHT_SCALE` /
    /// `MAX_HEIGHT_SCALE`）へ丸める。丸めの規則を 2 か所に持たないため、
    /// 定数は `markup` のものをそのまま使う。
    pub fn parse(json: &str) -> Result<Self, serde_json::Error> {
        let file: IconSetFile = serde_json::from_str(json)?;
        let mut entries = HashMap::with_capacity(file.icons.len());
        for (name, raw) in file.icons {
            let entry = match raw {
                IconEntryJson::Path(path) => IconEntry {
                    path,
                    height_scale: None,
                },
                IconEntryJson::Detailed { path, h } => IconEntry {
                    path,
                    height_scale: h.filter(|v| v.is_finite()).map(|v| {
                        v.clamp(
                            super::markup::MIN_HEIGHT_SCALE,
                            super::markup::MAX_HEIGHT_SCALE,
                        )
                    }),
                },
            };
            entries.insert(name, entry);
        }
        Ok(Self { entries })
    }

    /// 名前から定義を引く。未登録は `None`（呼び出し側が未解決として扱う）。
    pub fn get(&self, name: &str) -> Option<&IconEntry> {
        self.entries.get(name)
    }

    /// 登録件数（テスト・診断用）。
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    /// 1 件も登録が無いか。
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }
}

// ─── プロセス内キャッシュ ──────────────────────────────────────

/// パス → 解析済みアイコンセット。`None` = 読み込み／解析に失敗済み。
type IconSetCache = HashMap<String, Option<Arc<IconSet>>>;

/// キャッシュ実体（プロセス内で 1 つ）。
///
/// 描画・レイアウト・ピックの複数経路から毎フレーム引かれるため、
/// 「1 度読んだら二度と読まない」ことをここで保証する。
static CACHE: OnceLock<Mutex<IconSetCache>> = OnceLock::new();

/// キャッシュへの排他アクセスを得る。
fn cache() -> &'static Mutex<IconSetCache> {
    CACHE.get_or_init(|| Mutex::new(IconSetCache::new()))
}

/// アイコンセットを読み込む（キャッシュ済みならディスクに触らない）。
///
/// - `path` が空文字なら `None`（アイコンセット未設定）
/// - 読み込み・解析に失敗したら警告を **1 度だけ** 出して `None` を返し、
///   失敗そのものをキャッシュして再試行しない
pub fn load_cached(path: &str) -> Option<Arc<IconSet>> {
    if path.is_empty() {
        return None;
    }
    let mut map = cache().lock().ok()?;
    if let Some(hit) = map.get(path) {
        return hit.clone();
    }
    // ── 未知のパス: 実際に読む ──
    // 相対パスはアセットルート基準へ寄せる（read_string の CWD 依存を避ける）。
    let resolved = asset_fs::normalize_asset_path(path);
    let loaded = match asset_fs::read_string(&resolved) {
        Ok(text) => match IconSet::parse(&text) {
            Ok(set) => Some(Arc::new(set)),
            Err(e) => {
                eprintln!("[SEED TEXT] アイコンセットの解析に失敗しました: {path} ({e})");
                None
            }
        },
        Err(e) => {
            eprintln!("[SEED TEXT] アイコンセットの読み込みに失敗しました: {path} ({e})");
            None
        }
    };
    map.insert(path.to_string(), loaded.clone());
    loaded
}

/// 指定パスのキャッシュを捨てる（アセットのホットリロード用）。
pub fn invalidate(path: &str) {
    if let Ok(mut map) = cache().lock() {
        map.remove(path);
    }
}

/// 全キャッシュを捨てる（プロジェクト切り替え・アセットルート変更用）。
pub fn invalidate_all() {
    if let Ok(mut map) = cache().lock() {
        map.clear();
    }
}

// ============================================================
//  単体テスト（ファイル I/O なし。JSON 解析だけを検証する）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 文字列の省略形とオブジェクトの詳細形が両方読める。
    #[test]
    fn both_entry_forms_are_parsed() {
        let json = r#"{
            "icons": {
                "key_w":   "assets://ui/key_w.png",
                "mouse_l": { "path": "assets://ui/mouse_l.png", "h": 1.2 }
            }
        }"#;
        let set = IconSet::parse(json).expect("解析できる");
        assert_eq!(set.len(), 2);
        assert_eq!(
            set.get("key_w"),
            Some(&IconEntry {
                path: "assets://ui/key_w.png".into(),
                height_scale: None
            })
        );
        assert_eq!(
            set.get("mouse_l"),
            Some(&IconEntry {
                path: "assets://ui/mouse_l.png".into(),
                height_scale: Some(1.2)
            })
        );
    }

    /// `h` を省略したオブジェクト形は「セット既定なし」になる。
    #[test]
    fn detailed_form_without_h_has_no_scale() {
        let json = r#"{ "icons": { "a": { "path": "assets://a.png" } } }"#;
        let set = IconSet::parse(json).expect("解析できる");
        assert_eq!(set.get("a").map(|e| e.height_scale), Some(None));
    }

    /// 未登録の名前は None（呼び出し側が未解決として扱う）。
    #[test]
    fn unknown_name_is_none() {
        let set = IconSet::parse(r#"{ "icons": {} }"#).expect("解析できる");
        assert!(set.is_empty());
        assert!(set.get("nope").is_none());
    }

    /// `icons` キーが無くても失敗しない（空セットになる）。
    #[test]
    fn missing_icons_key_yields_empty_set() {
        let set = IconSet::parse(r#"{ "version": 1 }"#).expect("解析できる");
        assert!(set.is_empty());
    }

    /// 高さ倍率は記法パーサと同じ上下限へ丸められる。
    #[test]
    fn height_scale_is_clamped() {
        let json = r#"{ "icons": {
            "big":   { "path": "a.png", "h": 999.0 },
            "small": { "path": "b.png", "h": -1.0 }
        } }"#;
        let set = IconSet::parse(json).expect("解析できる");
        assert_eq!(
            set.get("big").unwrap().height_scale,
            Some(super::super::markup::MAX_HEIGHT_SCALE)
        );
        assert_eq!(
            set.get("small").unwrap().height_scale,
            Some(super::super::markup::MIN_HEIGHT_SCALE)
        );
    }

    /// 壊れた JSON はエラーになる（描画側は None として扱い、本文は崩れない）。
    #[test]
    fn broken_json_is_an_error() {
        assert!(IconSet::parse("{ this is not json").is_err());
    }

    /// 空パスはディスクへ触らず None を返す。
    #[test]
    fn empty_path_loads_nothing() {
        assert!(load_cached("").is_none());
    }
}
