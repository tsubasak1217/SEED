// ============================================================
//  upgrade/prefab_rehash.rs — 一括アップグレード後の `prefab_hash` 貼り直し
//
//  【なぜ必要か】
//  シーン内のプレハブインスタンスは、取り込んだ時点の `.actor` の**生テキストの
//  ハッシュ**を `prefab_hash` として焼き込んでいる（`app_base::prefab_hash`）。
//  一括アップグレードが `.actor` を書き換えると――たとえ足したのが版の 1 行だけでも
//  ――生テキストが変わるのでハッシュも変わる。その結果、シーン側に焼き込まれた
//  `prefab_hash` が古くなり、エディタで「プレハブが更新された」と一斉に表示される。
//  壊れはしないが、本当に更新された 1 件が埋もれてしまう。
//
//  そこで一括アップグレードの最後に、**アップグレード前と同期していた**
//  インスタンスの `prefab_hash` だけを新しい値へ貼り直す。
//
//  【同期していたものだけを貼り直す】
//  焼き込まれた値が「アップグレード前のファイルのハッシュ」と一致する場合だけ
//  貼り直す。一致しないインスタンスは、アップグレードとは無関係に**本当に古い**
//  （ユーザーがまだ取り込み直していない）ので、そのまま古い値で残す。
//  ここで無条件に貼り直すと、本物の更新通知を握りつぶしてしまう。
//
//  【差分を最小にする】
//  シーンを `SceneData` 経由で書き直すと、既定値の明示化や欄の並びで数千行の差分に
//  なる。ここでは**テキストの上で該当する値だけ**を差し替える。
//  結果、差分は `"prefab_hash": "…"` の行だけになる。
// ============================================================

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use crate::engine::core::app_base::prefab_hash::PREFAB_HASH_HEX_DIGITS;
use crate::engine::core::app_base::safe_write;

// ── 定数 ─────────────────────────────────────────────────────────

/// シーン JSON でプレハブの版を持つキー（`ActorData::prefab_hash`）。
const KEY_PREFAB_HASH: &str = "prefab_hash";

// ── 出力 ─────────────────────────────────────────────────────────

/// 1 シーンぶんの貼り直し結果。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SceneRestamp {
    /// レポートに出すパス（アセットルートの親から見た相対）。
    pub path: String,
    /// 貼り直した件数。
    pub updated: usize,
    /// 失敗理由（成功なら空文字）。
    pub message: String,
}

// ── 実行 ─────────────────────────────────────────────────────────

/// アップグレードで変わった `.actor` を参照するシーンの `prefab_hash` を貼り直す。
///
/// * `assets_root` … レポート用のパス整形に使う
/// * `scenes`      … 走査対象の `.scene` の実パス（アップグレード対象の列挙をそのまま使う）
/// * `changes`     … 「旧ハッシュ → 新ハッシュ」の対応表
/// * `dry_run`     … true なら 1 バイトも書かず、件数だけ数える
///
/// 戻り値は**貼り直しが 1 件以上あったシーン**と、**失敗したシーン**だけ。
/// 変化の無いシーンは結果に入らない（レポートを静かに保つため）。
pub fn restamp_scenes(
    assets_root: &Path,
    scenes: &[PathBuf],
    changes: &HashMap<String, String>,
    dry_run: bool,
) -> Vec<SceneRestamp> {
    let mut results = Vec::new();
    if changes.is_empty() {
        return results;
    }

    for scene in scenes {
        let display = super::target::display_path(assets_root, scene);
        let text = match std::fs::read_to_string(scene) {
            Ok(t) => t,
            Err(e) => {
                results.push(SceneRestamp {
                    path: display,
                    updated: 0,
                    message: format!("読み込み失敗: {e}"),
                });
                continue;
            }
        };

        let (replaced, updated) = replace_prefab_hashes(&text, changes);
        if updated == 0 {
            continue;
        }
        if dry_run {
            results.push(SceneRestamp {
                path: display,
                updated,
                message: "dry-run のため書き込んでいません".to_string(),
            });
            continue;
        }
        match safe_write::write_atomic_with_backup(scene, &replaced) {
            Ok(warning) => results.push(SceneRestamp {
                path: display,
                updated,
                message: warning.unwrap_or_default(),
            }),
            Err(e) => results.push(SceneRestamp {
                path: display,
                updated: 0,
                message: format!("書き込み失敗: {e}"),
            }),
        }
    }
    results
}

// ── テキストの置換 ───────────────────────────────────────────────

/// シーンのテキスト中の `"prefab_hash": "<旧>"` を `"<新>"` へ差し替える。
///
/// 戻り値は（差し替え後のテキスト, 差し替えた件数）。
///
/// 【なぜ JSON を組み直さないのか】
/// `serde_json` で読み書きすると欄の並び・浮動小数の書式・既定値の明示化で
/// ファイル全体が差分になる。ここは値 1 つの貼り直しなので、テキストの上で
/// 該当箇所だけを置き換えるほうが差分が読める。
///
/// 【なぜ素朴な `replace` ではないのか】
/// ハッシュは 16 桁の 16 進数なので、たまたま同じ文字列が別のキー（名前・パス・
/// ユーザーデータ）に現れる可能性がゼロではない。**キーが `prefab_hash` である
/// ものだけ**を対象にするため、キーを見つけてから次の文字列トークンを読む。
fn replace_prefab_hashes(text: &str, changes: &HashMap<String, String>) -> (String, usize) {
    let key_token = format!("\"{KEY_PREFAB_HASH}\"");
    let mut out = String::with_capacity(text.len());
    let mut rest = text;
    let mut updated = 0usize;

    while let Some(key_at) = rest.find(&key_token) {
        // キーの手前までをそのまま写す。
        let after_key = key_at + key_token.len();
        out.push_str(&rest[..after_key]);
        rest = &rest[after_key..];

        // キーの直後にある値（文字列トークン）を探す。
        // 途中に許されるのは空白・改行・コロンだけ（それ以外なら値が文字列でない＝
        // `null` など。その場合は触らずに次のキーを探す）。
        let Some(value) = next_string_token(rest) else {
            continue;
        };
        let old = &rest[value.start + 1..value.end - 1];
        match changes.get(old) {
            Some(new_hash) => {
                out.push_str(&rest[..value.start]);
                out.push('"');
                out.push_str(new_hash);
                out.push('"');
                updated += 1;
            }
            None => out.push_str(&rest[..value.end]),
        }
        rest = &rest[value.end..];
    }
    out.push_str(rest);
    (out, updated)
}

/// 文字列トークンの範囲（開き引用符の位置と、閉じ引用符の次の位置）。
struct TokenSpan {
    start: usize,
    end: usize,
}

/// `rest` の先頭から「空白・コロンだけを読み飛ばした先にある文字列トークン」を探す。
///
/// ハッシュ値は 16 進数 `PREFAB_HASH_HEX_DIGITS` 桁なので、エスケープを含まない。
/// したがって最初の閉じ引用符までを値とみなしてよい（エスケープ解析は不要）。
/// 想定外の形（値が文字列でない・桁数が違う）なら `None` を返し、何も書き換えない。
fn next_string_token(rest: &str) -> Option<TokenSpan> {
    let bytes = rest.as_bytes();
    let mut i = 0usize;
    while i < bytes.len() && (bytes[i].is_ascii_whitespace() || bytes[i] == b':') {
        i += 1;
    }
    if i >= bytes.len() || bytes[i] != b'"' {
        return None;
    }
    let start = i;
    let close = rest[start + 1..].find('"')? + start + 1;
    // 値の長さがハッシュの桁数と違えば対象外（別の用途の文字列を触らない）。
    if close - start - 1 != PREFAB_HASH_HEX_DIGITS {
        return None;
    }
    Some(TokenSpan {
        start,
        end: close + 1,
    })
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用: 旧 → 新 の対応表を作る。
    fn changes(pairs: &[(&str, &str)]) -> HashMap<String, String> {
        pairs
            .iter()
            .map(|(o, n)| (o.to_string(), n.to_string()))
            .collect()
    }

    /// 同期していたインスタンスの値だけが貼り直され、他の行は 1 文字も変わらないこと。
    #[test]
    fn replaces_only_matching_hashes() {
        let text = concat!(
            "{\n",
            "  \"name\": \"Main\",\n",
            "  \"actors\": [\n",
            "    { \"name\": \"a\", \"prefab_source\": \"assets://p/A.actor\", \"prefab_hash\": \"1111111111111111\" },\n",
            "    { \"name\": \"b\", \"prefab_source\": \"assets://p/B.actor\", \"prefab_hash\": \"2222222222222222\" }\n",
            "  ]\n",
            "}"
        );
        let map = changes(&[("1111111111111111", "aaaaaaaaaaaaaaaa")]);
        let (out, updated) = replace_prefab_hashes(text, &map);

        assert_eq!(updated, 1);
        assert!(out.contains("\"prefab_hash\": \"aaaaaaaaaaaaaaaa\""), "{out}");
        assert!(
            out.contains("\"prefab_hash\": \"2222222222222222\""),
            "同期していないインスタンスまで貼り直している: {out}"
        );
        // 貼り直した 1 か所以外は 1 文字も変わっていないこと
        assert_eq!(
            out.replace("aaaaaaaaaaaaaaaa", "1111111111111111"),
            text,
            "値の置換以外の差分が入っている"
        );
    }

    /// 同じプレハブの複数インスタンスがすべて貼り直されること。
    #[test]
    fn replaces_every_instance_of_the_same_prefab() {
        let text = r#"{"actors":[{"prefab_hash":"1111111111111111"},{"prefab_hash":"1111111111111111"}]}"#;
        let map = changes(&[("1111111111111111", "bbbbbbbbbbbbbbbb")]);
        let (out, updated) = replace_prefab_hashes(text, &map);
        assert_eq!(updated, 2);
        assert!(!out.contains("1111111111111111"), "{out}");
    }

    /// `prefab_hash` **以外**のキーに同じ文字列があっても触らないこと。
    #[test]
    fn does_not_touch_the_same_string_under_other_keys() {
        let text = r#"{"note":"1111111111111111","prefab_hash":"1111111111111111"}"#;
        let map = changes(&[("1111111111111111", "cccccccccccccccc")]);
        let (out, updated) = replace_prefab_hashes(text, &map);
        assert_eq!(updated, 1);
        assert!(out.contains(r#""note":"1111111111111111""#), "{out}");
        assert!(out.contains(r#""prefab_hash":"cccccccccccccccc""#), "{out}");
    }

    /// 値が文字列でない（`null`）・桁数が違う場合は何もしないこと。
    #[test]
    fn ignores_non_hash_values() {
        let map = changes(&[("1111111111111111", "dddddddddddddddd")]);

        let null_value = r#"{"prefab_hash":null,"x":1}"#;
        let (out, updated) = replace_prefab_hashes(null_value, &map);
        assert_eq!(updated, 0);
        assert_eq!(out, null_value);

        let short = r#"{"prefab_hash":"1111"}"#;
        let (out, updated) = replace_prefab_hashes(short, &map);
        assert_eq!(updated, 0);
        assert_eq!(out, short);
    }

    /// 対応表に無いハッシュしか無ければ、テキストは 1 文字も変わらないこと。
    #[test]
    fn leaves_text_untouched_when_nothing_matches() {
        let text = r#"{"prefab_hash":"9999999999999999"}"#;
        let map = changes(&[("1111111111111111", "eeeeeeeeeeeeeeee")]);
        let (out, updated) = replace_prefab_hashes(text, &map);
        assert_eq!(updated, 0);
        assert_eq!(out, text);
    }

    /// `prefab_hash` が 1 つも無いシーンでも壊れないこと。
    #[test]
    fn handles_scenes_without_any_prefab_instance() {
        let text = r#"{"name":"S","actors":[{"name":"a"}]}"#;
        let map = changes(&[("1111111111111111", "ffffffffffffffff")]);
        let (out, updated) = replace_prefab_hashes(text, &map);
        assert_eq!(updated, 0);
        assert_eq!(out, text);
    }
}
