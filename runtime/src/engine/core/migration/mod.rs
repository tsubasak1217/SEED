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

/// マイグレーションのエラー型。
pub mod error;
/// ゴールデンテスト（変換前後の見本の照合）。
#[cfg(test)]
mod golden;
/// アクタ木とコンポーネントを辿る共通ヘルパ。
pub mod json_walk;
/// 形式ごとの現行版・版の欄名・対象拡張子の表。
pub mod kind;
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
pub use kind::{FormatKind, JSON_VERSION_KEY};
// 実行結果の型（`MigrationReport` / `AppliedStep`）は `runner` に置いてある。
// 必要な呼び出し側だけが `migration::runner::MigrationReport` として使う。
pub use runner::migrate_to_current;

// ── 版の欄名とコード上の綴りの結び付け ───────────────────────────
//
//  serde の derive は欄名にリテラルしか書けないため、`kind.rs` の表と
//  コード上の綴りが一致していることを**テストで固定**する
//  （`stamp_field_name_matches_every_kind`）。

/// 版の刻印・読み取りに使う構造体の欄名（`kind::JSON_VERSION_KEY` と一致させること）。
const STAMP_FIELD_NAME: &str = JSON_VERSION_KEY;

/// 版だけを先読みするための最小の型。
///
/// 本体のデシリアライズより先に版を見て「現行版ならそのまま読む」経路を作るために使う。
/// 値の型は検証しないで受け取り（`Value`）、解釈は `runner` に任せる。
#[derive(serde::Deserialize)]
struct VersionPeek {
    #[serde(default)]
    format_version: Option<Value>,
}

/// 版を先頭に刻んだうえで本体を直列化するためのラッパー。
///
/// `#[serde(flatten)]` により、本体（構造体でも `serde_json::Map` でも可）の欄が
/// そのまま同じオブジェクトへ並ぶ。**本体の構造体に版の欄を足さずに済む**のが要点で、
/// `ActorData` のようにシーン内へ入れ子で使われる型にも安全に刻める。
#[derive(Serialize)]
struct VersionStamped<'a, T: Serialize> {
    format_version: u32,
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
    let peek: VersionPeek =
        serde_json::from_str(json).map_err(|e| MigrationError::Parse {
            kind,
            detail: e.to_string(),
        })?;
    let version = runner::interpret_version(kind, peek.format_version.as_ref())?;
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
///
/// 本体の欄の並び・数値の書式は、本体を単独で `to_string_pretty` したときと同じになる
/// （`stamped_json_is_the_plain_body_with_one_extra_line` で固定）。
pub fn to_stamped_pretty_json<T: Serialize>(
    kind: FormatKind,
    body: &T,
) -> Result<String, serde_json::Error> {
    debug_assert_eq!(
        kind.version_key(),
        STAMP_FIELD_NAME,
        "版の欄名が kind.rs の表と食い違っている"
    );
    serde_json::to_string_pretty(&VersionStamped {
        format_version: kind.current_version(),
        body,
    })
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

    /// 版の欄名がコード上の綴りと表で一致していること。
    #[test]
    fn stamp_field_name_matches_every_kind() {
        for kind in FormatKind::ALL.iter().copied() {
            assert_eq!(
                kind.version_key(),
                STAMP_FIELD_NAME,
                "{kind} の版の欄名が刻印側の綴りと違う"
            );
        }
    }

    /// 刻印した JSON は「本体をそのまま pretty 出力したものの先頭に版の 1 行を足したもの」
    /// であること。
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
        let stamped = to_stamped_pretty_json(FormatKind::Actor, &body).unwrap();

        let version_line = format!(
            "  \"{STAMP_FIELD_NAME}\": {},",
            FormatKind::Actor.current_version()
        );
        let mut lines: Vec<&str> = stamped.lines().collect();
        assert_eq!(lines.get(1).copied(), Some(version_line.as_str()), "{stamped}");
        lines.remove(1);
        assert_eq!(lines.join("\n"), plain, "本体の出力が変わっている:\n{stamped}");
        // 浮動小数が f64 へ広がって書式が荒れていないこと
        assert!(stamped.contains("0.1"), "{stamped}");
    }

    /// `serde_json::Map` も刻印できること（一括アップグレードが Value を書くのに使う）。
    #[test]
    fn stamps_a_plain_json_map() {
        let mut map = serde_json::Map::new();
        map.insert("name".to_string(), json!("S"));
        let text = to_stamped_pretty_json(FormatKind::Scene, &map).unwrap();
        let back: Value = serde_json::from_str(&text).unwrap();
        assert_eq!(back[STAMP_FIELD_NAME], json!(FormatKind::Scene.current_version()));
        assert_eq!(back["name"], json!("S"));
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

    /// 未来の版は読み込みを拒否されること。
    #[test]
    fn refuses_future_version() {
        let future = FormatKind::Actor.current_version() + 1;
        let text = format!("{{\"{STAMP_FIELD_NAME}\":{future},\"name\":\"A\",\"scale\":1.0}}");
        let err = load_json::<Body>(FormatKind::Actor, &text).unwrap_err();
        assert!(
            matches!(err, MigrationError::FutureVersion { .. }),
            "{err}"
        );
    }

    /// 版の欄が整数でないファイルは弾かれること。
    #[test]
    fn refuses_non_integer_version() {
        let text = format!("{{\"{STAMP_FIELD_NAME}\":\"2\",\"name\":\"A\",\"scale\":1.0}}");
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
