// ============================================================
//  migration — アセット形式のバージョンとマイグレーション
//
//  設計の正典は **docs/asset_migration.md**。要点だけ再掲する:
//
//  - 版は**形式ごと**（`.scene` / `.actor` / …）。JSON はトップレベルの整数
//    `format_version` で表し、**欄が無いファイルは 1 版**とみなす。
//  - 変換は**前進のみ**。N → N+1 の純関数を 1 段ずつ連鎖させる。逆変換は作らない。
//  - **未来の版は読み込みを拒否**する（新しいエンジンで保存されたファイル）。
//  - 変換は**ランタイム（Rust）に一本化**する。エディタ・CLI からは同じものを呼ぶ。
//  - 読み込み時は**メモリ上でだけ**変換する。ファイルを書き換えるのは
//    (a) 普通に保存したとき (b) 一括アップグレード（`--upgrade-project`）のときだけ。
//
//  【ファイル構成】
//  | ファイル      | 責務 |
//  |---------------|------|
//  | `kind.rs`     | 形式ごとの現行版・版の欄名・対象拡張子の表 |
//  | `registry.rs` | (形式, 変換元の版) → 変換関数 の表（網羅性はテストで固定） |
//  | `runner.rs`   | 連鎖の実行と `MigrationReport` |
//  | `error.rs`    | `MigrationError`（未来版・段の欠落・変換失敗） |
//  | `json_walk.rs`| アクタ木とコンポーネントを辿る共通ヘルパ |
//  | `steps/`      | 変換 1 段（純関数）の実体 |
//  | `upgrade/`    | 一括アップグレード（列挙・dry-run・safe_write・レポート） |
//
//  【変換を 1 段足す手順】→ docs/asset_migration.md 4 章
//  (1) `kind.rs` の現行版を +1、(2) `steps/<形式>/vN_to_vM.rs` を足して
//  `registry.rs` に 1 行登録、(3) `tests/fixtures/migration/<形式>/` に
//  変換前後の見本を置いて `golden.rs` に 1 行足す。
// ============================================================

/// `--upgrade-project` / `--migrate-json` のコマンドライン入口。
pub mod cli;
/// マイグレーションのエラー型。
pub mod error;
/// ゴールデンテスト（変換前後の見本の照合）。
#[cfg(test)]
mod golden;
/// アクタ木とコンポーネントを辿る共通ヘルパ。
pub mod json_walk;
/// 形式ごとの現行版・版の欄名・対象拡張子の表。
pub mod kind;
/// 標準入出力 1 件だけを変換する CLI（`SEED.exe --migrate-json <kind>`）。
pub mod migrate_json;
/// (形式, 変換元の版) → 変換関数 の表。
pub mod registry;
/// 連鎖の実行と結果レポート。
pub mod runner;
/// 変換 1 段（純関数）の実体。
pub mod steps;
/// 一括アップグレード（`SEED.exe --upgrade-project`）。
pub mod upgrade;

use serde::de::DeserializeOwned;
use serde::Serialize;
use serde_json::Value;

pub use error::MigrationError;
pub use kind::{FormatKind, VersionKey, JSON_VERSION_KEY};
// 実行結果の型（`MigrationReport` / `AppliedStep`）は `runner` に置いてある。
// 必要な呼び出し側だけが `migration::runner::MigrationReport` として使う。
pub use runner::migrate_to_current;

// ── 版の欄名とコード上の綴りの結び付け ───────────────────────────
//
//  serde の derive は欄名にリテラルしか書けない。そこで**欄名 1 つにつき
//  先読み用の型と刻印用の型を 1 組ずつ**用意し、`VersionKey` の網羅 match で
//  振り分ける。表に欄名を足したら match が埋まらずビルドが落ちるので、
//  「表に足したがコード側の型を足し忘れた」事故が起きない。

/// 版だけを先読みするための最小の型（欄名 `format_version`）。
///
/// 本体のデシリアライズより先に版を見て「現行版ならそのまま読む」経路を作るために使う。
/// 値の型は検証しないで受け取り（`Value`）、解釈は `runner` に任せる。
#[derive(serde::Deserialize)]
struct PeekFormatVersion {
    #[serde(default)]
    format_version: Option<Value>,
}

/// 版だけを先読みするための最小の型（欄名 `version`）。
#[derive(serde::Deserialize)]
struct PeekVersion {
    #[serde(default)]
    version: Option<Value>,
}

/// 版を先頭に刻んだうえで本体を直列化するためのラッパー（欄名 `format_version`）。
///
/// `#[serde(flatten)]` により、本体（構造体でも `serde_json::Map` でも可）の欄が
/// そのまま同じオブジェクトへ並ぶ。**本体の構造体に版の欄を足さずに済む**のが要点で、
/// `ActorData` のようにシーン内へ入れ子で使われる型にも安全に刻める。
#[derive(Serialize)]
struct StampedFormatVersion<'a, T: Serialize> {
    format_version: u32,
    #[serde(flatten)]
    body: &'a T,
}

/// 版を先頭に刻んだうえで本体を直列化するためのラッパー（欄名 `version`）。
#[derive(Serialize)]
struct StampedVersion<'a, T: Serialize> {
    version: u32,
    #[serde(flatten)]
    body: &'a T,
}

// ── 公開 API ─────────────────────────────────────────────────────

/// JSON テキストを読み、必要なら現行版へ変換してから `T` へデシリアライズする。
///
/// 読み込み経路（`.scene` / `.actor`）はすべてこの 1 本を通す。
/// 先頭の BOM は許容する（エディタや外部ツールが付けることがあるため）。
///
/// 【現行版のファイルで余計なコストを払わない】
/// まず版だけを先読みし、**現行版ならテキストから直接 `T` を読む**（従来と同じ経路・同じ費用）。
/// 古い版のときだけ `Value` を 1 回だけ組み立て、変換してから `T` にする。
/// `Value` を二度組み立てることはない。
pub fn load_json<T: DeserializeOwned>(kind: FormatKind, raw: &str) -> Result<T, MigrationError> {
    let json = strip_bom(raw);

    // 版の先読み。壊れた JSON はここで判る（本体のパースでも同じ結果になる）。
    let version = peek_version(kind, json)?;
    let current = kind.current_version();

    if version > current {
        return Err(MigrationError::FutureVersion {
            kind,
            found: version,
            supported: current,
        });
    }

    // 現行版: 変換の必要が無いのでそのままデシリアライズする。
    if version == current {
        return serde_json::from_str(json).map_err(|e| MigrationError::Parse {
            kind,
            detail: e.to_string(),
        });
    }

    // 古い版: Value へ 1 回だけ組み立て、メモリ上で現行版まで持ち上げてから読む。
    let mut value: Value = serde_json::from_str(json).map_err(|e| MigrationError::Parse {
        kind,
        detail: e.to_string(),
    })?;
    migrate_to_current(kind, &mut value)?;
    serde_json::from_value(value).map_err(|e| MigrationError::Parse {
        kind,
        detail: e.to_string(),
    })
}

/// 本体を「現行版の刻印を先頭に置いた」pretty JSON へ直列化する。
///
/// 保存経路（`.scene` / `.actor` / 一括アップグレード）はすべてこの 1 本を通す。
/// 版の欄が先頭に来るので、差分を見たときに「どの版のファイルか」がすぐ判る。
/// 欄名は形式ごと（`format_version` / `version`）に `kind.rs` の表が決める。
///
/// 本体の欄の並び・数値の書式は、本体を単独で `to_string_pretty` したときと同じになる
/// （`stamped_json_is_the_plain_body_with_one_extra_line` で固定）。
pub fn to_stamped_pretty_json<T: Serialize>(
    kind: FormatKind,
    body: &T,
) -> Result<String, serde_json::Error> {
    let version = kind.current_version();
    match kind.version_key_kind() {
        VersionKey::FormatVersion => serde_json::to_string_pretty(&StampedFormatVersion {
            format_version: version,
            body,
        }),
        VersionKey::Version => serde_json::to_string_pretty(&StampedVersion { version, body }),
    }
}

/// テキストから**版だけ**を読む（本体の `Value` を組み立てない）。
///
/// 欄名は形式ごとに違うので、`VersionKey` の網羅 match で先読み用の型を選ぶ。
/// 欄が無ければ `IMPLICIT_FIRST_VERSION`（`runner::interpret_version` の規約）。
fn peek_version(kind: FormatKind, json: &str) -> Result<u32, MigrationError> {
    let to_parse_error = |e: serde_json::Error| MigrationError::Parse {
        kind,
        detail: e.to_string(),
    };
    let raw: Option<Value> = match kind.version_key_kind() {
        VersionKey::FormatVersion => {
            let peek: PeekFormatVersion = serde_json::from_str(json).map_err(to_parse_error)?;
            peek.format_version
        }
        VersionKey::Version => {
            let peek: PeekVersion = serde_json::from_str(json).map_err(to_parse_error)?;
            peek.version
        }
    };
    runner::interpret_version(kind, raw.as_ref())
}

/// 先頭の BOM を取り除く。
fn strip_bom(raw: &str) -> &str {
    raw.strip_prefix('\u{FEFF}').unwrap_or(raw)
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde::Deserialize;
    use serde_json::json;

    /// テスト用の最小の本体（`skip_serializing_if` と f32 を含める）。
    #[derive(Serialize, Deserialize, PartialEq, Debug)]
    struct Body {
        name: String,
        scale: f32,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        note: Option<String>,
    }

    /// 刻印した JSON は「本体をそのまま pretty 出力したものの先頭に版の 1 行を足したもの」
    /// であること。**全形式**（欄名 `format_version` / `version` の両方）で確かめる。
    ///
    /// これが崩れると、保存のたびにファイル全体の差分が出る（欄の並び替え・
    /// 浮動小数の書式変化）。`#[serde(flatten)]` の挙動に依存しているので固定しておく。
    #[test]
    fn stamped_json_is_the_plain_body_with_one_extra_line() {
        let body = Body {
            name: "Fish".to_string(),
            scale: 0.1,
            note: None,
        };
        let plain = serde_json::to_string_pretty(&body).unwrap();

        for kind in FormatKind::ALL.iter().copied() {
            let stamped = to_stamped_pretty_json(kind, &body).unwrap();
            let version_line = format!(
                "  \"{}\": {},",
                kind.version_key(),
                kind.current_version()
            );
            let mut lines: Vec<&str> = stamped.lines().collect();
            assert_eq!(
                lines.get(1).copied(),
                Some(version_line.as_str()),
                "{kind}: {stamped}"
            );
            lines.remove(1);
            assert_eq!(
                lines.join("\n"),
                plain,
                "{kind}: 本体の出力が変わっている:\n{stamped}"
            );
            // 浮動小数が f64 へ広がって書式が荒れていないこと
            assert!(stamped.contains("0.1"), "{kind}: {stamped}");
        }
    }

    /// `serde_json::Map` も刻印できること（一括アップグレードが Value を書くのに使う）。
    #[test]
    fn stamps_a_plain_json_map() {
        let mut map = serde_json::Map::new();
        map.insert("name".to_string(), json!("S"));
        let text = to_stamped_pretty_json(FormatKind::Scene, &map).unwrap();
        let back: Value = serde_json::from_str(&text).unwrap();
        assert_eq!(
            back[FormatKind::Scene.version_key()],
            json!(FormatKind::Scene.current_version())
        );
        assert_eq!(back["name"], json!("S"));
    }

    /// 刻印したテキストを読み戻すと同じ版として認識されること（全形式）。
    ///
    /// 欄名が形式ごとに違うので、「刻む側」と「読む側」がずれると
    /// 保存したファイルが次回 v1 扱いになってしまう。その事故を固定で防ぐ。
    #[test]
    fn stamped_text_is_read_back_as_the_current_version() {
        let mut map = serde_json::Map::new();
        map.insert("name".to_string(), json!("X"));
        for kind in FormatKind::ALL.iter().copied() {
            let text = to_stamped_pretty_json(kind, &map).unwrap();
            assert_eq!(
                peek_version(kind, &text).unwrap(),
                kind.current_version(),
                "{kind}: 刻印した版が読み戻せていない:\n{text}"
            );
        }
    }

    /// 版の欄名は形式ごとに独立していること（`version` 形式に `format_version` を
    /// 書いても版としては読まれない、その逆も同じ）。
    #[test]
    fn version_keys_do_not_leak_across_formats() {
        // `.inputmap`（欄名 `version`）に `format_version` があっても無視される
        let text = r#"{"format_version":9,"actions":[]}"#;
        assert_eq!(
            peek_version(FormatKind::InputMap, text).unwrap(),
            kind::IMPLICIT_FIRST_VERSION
        );
        // `.scene`（欄名 `format_version`）に `version` があっても無視される
        let text = r#"{"version":9,"name":"S","actors":[]}"#;
        assert_eq!(
            peek_version(FormatKind::Scene, text).unwrap(),
            kind::IMPLICIT_FIRST_VERSION
        );
    }

    /// 現行版のテキストはそのまま読めること（変換経路を通らない）。
    #[test]
    fn loads_current_version_directly() {
        let text = to_stamped_pretty_json(
            FormatKind::Actor,
            &Body {
                name: "A".to_string(),
                scale: 1.0,
                note: None,
            },
        )
        .unwrap();
        let body: Body = load_json(FormatKind::Actor, &text).unwrap();
        assert_eq!(body.name, "A");
    }

    /// BOM 付き・版の欄なしのテキストも読めること。
    #[test]
    fn loads_legacy_text_with_bom() {
        let text = "\u{FEFF}{\"name\":\"A\",\"scale\":1.0,\"components\":[],\"children\":[]}";
        let body: Body = load_json(FormatKind::Actor, text).unwrap();
        assert_eq!(body.name, "A");
    }

    /// 未来の版は読み込みを拒否されること（**全形式**。欄名が違っても同じ扱い）。
    #[test]
    fn refuses_future_version() {
        for kind in FormatKind::ALL.iter().copied() {
            let future = kind.current_version() + 1;
            let text = format!(
                "{{\"{}\":{future},\"name\":\"A\",\"scale\":1.0}}",
                kind.version_key()
            );
            let err = load_json::<Body>(kind, &text).unwrap_err();
            assert!(
                matches!(err, MigrationError::FutureVersion { .. }),
                "{kind}: {err}"
            );
        }
    }

    /// 版の欄が整数でないファイルは弾かれること。
    #[test]
    fn refuses_non_integer_version() {
        let text = format!(
            "{{\"{}\":\"2\",\"name\":\"A\",\"scale\":1.0}}",
            FormatKind::Actor.version_key()
        );
        let err = load_json::<Body>(FormatKind::Actor, &text).unwrap_err();
        assert!(matches!(err, MigrationError::InvalidVersion { .. }), "{err}");
    }

    /// 壊れた JSON はパースエラーとして返ること。
    #[test]
    fn reports_broken_json_as_parse_error() {
        let err = load_json::<Body>(FormatKind::Actor, "{not json").unwrap_err();
        assert!(matches!(err, MigrationError::Parse { .. }), "{err}");
    }
}
