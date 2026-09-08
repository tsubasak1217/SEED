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
//
//  【ライブ編集の自動反映（ポーリング）】
//  上記の「1 度読んだら二度と読まない」方針のままだと、`.icons` を
//  外部エディタで書き換えてもプロセス再起動まで反映されない問題がある。
//  そこで各エントリに「解決済みの実ファイルパス」と「読み込み時点の
//  更新時刻（`asset_fs::mtime`）」を持たせ、`poll_changes` が定期的に
//  （呼び出し頻度そのものの間引きは `inline::poll_asset_changes` の責務）
//  更新時刻を比較する。
//  変化を検知したエントリだけ再読込し、**解析に失敗した場合
//  （保存の途中で不完全な JSON になっている等）は前回の内容を保持**する。
//  保持したエントリは記録 mtime を更新しないため、次回のポーリングでも
//  再読込が試みられる＝保存が完了した瞬間に自然と反映される。
// ============================================================

use std::collections::HashMap;
use std::sync::{Arc, Mutex, OnceLock};
use std::time::Duration;

use serde::Deserialize;

use crate::engine::asset_fs;

/// `.icons` および画像寸法キャッシュのライブ編集ポーリング間隔。
///
/// `inline::poll_asset_changes` はフレーム頭で毎回呼ばれる想定だが、
/// この定数より短い間隔での実確認（ディスク I/O）は間引かれる。
/// 秒単位でしか変化しないファイルの更新確認を 1 フレームごとに行うのは
/// 無意味な I/O 負荷になるため、確認頻度そのものをここで定数化する。
pub const ICON_SET_POLL_INTERVAL: Duration = Duration::from_secs(1);

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

/// キャッシュ 1 件分の内容。
///
/// `resolved_path` と `mtime` はポーリングで「差し替えられたか」を
/// 判定するためだけの付帯情報で、解決結果そのものではない。
struct CacheEntry {
    /// `asset_fs::normalize_asset_path` 済みの実ファイルパス
    /// （`asset_fs::mtime` / `asset_fs::read_string` へそのまま渡せる形）。
    resolved_path: String,
    /// 読み込み時点の更新時刻（UNIX 秒）。`asset_fs::mtime` が返す値をそのまま持つ。
    ///
    /// `0` は「PAK 内アセット、または取得不能」を意味し、ポーリングでの
    /// 変化検出は行わない（`asset_fs::mtime` の契約に合わせる）。
    mtime: u64,
    /// 解析済みアイコンセット。`None` = 読み込み／解析に失敗済み。
    set: Option<Arc<IconSet>>,
}

/// パス（`load_cached` に渡された生の文字列）→ キャッシュ内容。
type IconSetCache = HashMap<String, CacheEntry>;

/// キャッシュ実体（プロセス内で 1 つ）。
///
/// 描画・レイアウト・ピックの複数経路から毎フレーム引かれるため、
/// 「1 度読んだら二度と読まない」ことをここで保証する
/// （ライブ編集の反映は `poll_changes` が別途担当する）。
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
///   （ライブ編集で直った場合は `poll_changes` が拾う）
pub fn load_cached(path: &str) -> Option<Arc<IconSet>> {
    if path.is_empty() {
        return None;
    }
    let mut map = cache().lock().ok()?;
    if let Some(hit) = map.get(path) {
        return hit.set.clone();
    }
    // ── 未知のパス: 実際に読む ──
    // 相対パスはアセットルート基準へ寄せる（read_string の CWD 依存を避ける）。
    let resolved = asset_fs::normalize_asset_path(path);
    let mtime = asset_fs::mtime(&resolved);
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
    map.insert(
        path.to_string(),
        CacheEntry { resolved_path: resolved, mtime, set: loaded.clone() },
    );
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

// ─── ライブ編集ポーリング ────────────────────────────────────

/// 全エントリの更新時刻を確認し、変化したものだけ再読込する。
///
/// `mtime_fn` は「解決済みパス → 現在の更新時刻」を返す関数で、
/// 本番では `asset_fs::mtime` を渡す（`poll_changes` 参照）。
/// 単体テストではファイル I/O を介さず時刻を差し替えられるよう、
/// ここへ注入できる形にしてある。
///
/// 戻り値: 1 件でも再読込（内容の差し替え）が起きたか。
/// 解析失敗（編集途中の不完全な JSON 等）は警告を出すだけで、
/// 前回の内容と記録 mtime を維持する（次回のポーリングで再試行される）。
fn poll_changes_with(mtime_fn: &dyn Fn(&str) -> u64) -> bool {
    let Ok(mut map) = cache().lock() else { return false };
    let mut changed = false;
    for (path, entry) in map.iter_mut() {
        let current = mtime_fn(&entry.resolved_path);
        // 0 は「PAK 内 / 取得不能」＝ 変化を検出できないので確認しない。
        // 値が前回と同じなら当然ファイルは変わっていない。
        if current == 0 || current == entry.mtime {
            continue;
        }
        match asset_fs::read_string(&entry.resolved_path) {
            Ok(text) => match IconSet::parse(&text) {
                Ok(set) => {
                    entry.set = Some(Arc::new(set));
                    entry.mtime = current;
                    changed = true;
                }
                Err(e) => {
                    // 保存の途中で不完全な JSON を掴んだ可能性がある。
                    // mtime は更新しないので、次のポーリングで再度読み直しに来る。
                    eprintln!(
                        "[SEED TEXT] アイコンセットの再読込に失敗しました（前回の内容を維持します）: {path} ({e})"
                    );
                }
            },
            Err(e) => {
                eprintln!(
                    "[SEED TEXT] アイコンセットの再読込に失敗しました（前回の内容を維持します）: {path} ({e})"
                );
            }
        }
    }
    changed
}

/// 全エントリの更新時刻を確認し、変化したものだけ再読込する（本番用）。
///
/// 呼び出し頻度の間引きは `inline::poll_asset_changes` が行うため、
/// ここは呼ばれたら必ずディスクの更新時刻を確認する。
pub fn poll_changes() -> bool {
    poll_changes_with(&asset_fs::mtime)
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

    // ── ライブ編集ポーリング ──────────────────────────────────
    //
    // 実ファイルを一時ディレクトリへ書き、`load_cached` で通常どおり
    // 読み込んだあと、`poll_changes_with` へ「時刻だけ差し替えた」
    // 疑似 mtime を渡して再読込の分岐を検証する。
    // 絶対パスは `asset_fs::normalize_asset_path` を素通りする（`resolve`
    // も同様）ため、`asset_fs::init` を呼ばずにテストできる。

    use std::io::Write;

    /// テスト用一時ファイルを作り、絶対パス文字列を返す。
    fn write_temp_file(name: &str, content: &str) -> String {
        let mut path = std::env::temp_dir();
        // テスト間の衝突を避けるため、プロセス ID とファイル名で一意にする。
        path.push(format!("seed_icon_set_test_{}_{}", std::process::id(), name));
        let mut f = std::fs::File::create(&path).expect("一時ファイルを作成できる");
        f.write_all(content.as_bytes()).expect("書き込める");
        path.to_string_lossy().to_string()
    }

    const ICONS_A: &str = r#"{ "icons": { "a": "assets://a.png" } }"#;
    const ICONS_A_V2: &str = r#"{ "icons": { "a": "assets://a2.png", "b": "assets://b.png" } }"#;

    /// 更新時刻が変化したエントリだけが再読込され、他のエントリは無傷であること。
    #[test]
    fn poll_only_reloads_the_entry_whose_mtime_changed() {
        invalidate_all();
        let path_a = write_temp_file("a.icons", ICONS_A);
        let path_b = write_temp_file("b.icons", ICONS_A);
        let set_a_before = load_cached(&path_a).expect("読み込める");
        let set_b_before = load_cached(&path_b).expect("読み込める");
        // B の「変化なし」を表す mtime は、実際に記録された値をそのまま使う
        // （0 は「確認不能」として無条件スキップされてしまうため使えない）。
        let recorded_b_mtime = cache().lock().unwrap().get(&path_b).unwrap().mtime;

        // A の中身だけ書き換える（実ファイルの mtime 粒度に依存しないよう、
        // 変化の伝達はポーリングへ注入する疑似時刻で行う）。
        write_temp_file("a.icons", ICONS_A_V2);

        let resolved_a = asset_fs::normalize_asset_path(&path_a);
        let resolved_b = asset_fs::normalize_asset_path(&path_b);
        let changed = poll_changes_with(&move |p: &str| {
            if p == resolved_a {
                999_999 // A だけ「時刻が変わった」ことにする。
            } else if p == resolved_b {
                recorded_b_mtime // B は前回と同じ＝変化なし。
            } else {
                0
            }
        });

        assert!(changed, "A の内容変化を検出して再読込するはず");
        let set_a_after = load_cached(&path_a).expect("再読込後も読める");
        let set_b_after = load_cached(&path_b).expect("B は無傷のはず");
        assert_eq!(set_a_after.len(), 2, "A は新しい内容（2 件）に更新されている");
        assert!(Arc::ptr_eq(&set_b_before, &set_b_after), "B は再読込されず同一の Arc のまま");
        assert!(!Arc::ptr_eq(&set_a_before, &set_a_after), "A は新しい Arc に差し替わっている");

        let _ = std::fs::remove_file(&path_a);
        let _ = std::fs::remove_file(&path_b);
        invalidate_all();
    }

    /// 不完全な JSON への再読込は失敗し、前回の内容が維持されること。
    #[test]
    fn poll_keeps_previous_content_on_parse_failure() {
        invalidate_all();
        let path = write_temp_file("broken.icons", ICONS_A);
        let before = load_cached(&path).expect("最初は読める");

        // 保存の途中を模した不完全な JSON。
        write_temp_file("broken.icons", "{ \"icons\": { \"a\": ");
        let resolved = asset_fs::normalize_asset_path(&path);
        let changed = poll_changes_with(&move |p: &str| if p == resolved { 42 } else { 0 });

        assert!(!changed, "解析に失敗したので「変化した」扱いにはしない");
        let after = load_cached(&path).expect("前回の内容が維持されている");
        assert!(Arc::ptr_eq(&before, &after), "前回の Arc がそのまま維持される");

        // 記録 mtime が更新されていないので、次のポーリングでまた再試行される
        // ことを、正しい JSON へ戻した上でもう一度確認する。
        write_temp_file("broken.icons", ICONS_A_V2);
        let resolved2 = asset_fs::normalize_asset_path(&path);
        let changed2 = poll_changes_with(&move |p: &str| if p == resolved2 { 43 } else { 0 });
        assert!(changed2, "壊れていた内容が直ったので次回は再読込されるはず");
        let fixed = load_cached(&path).expect("直った内容が読める");
        assert_eq!(fixed.len(), 2);

        let _ = std::fs::remove_file(&path);
        invalidate_all();
    }
}
