// ============================================================
//  golden.rs — ゴールデンテスト（変換前後の見本の照合）
//
//  【何を担保するか】
//  `runtime/tests/fixtures/migration/<形式>/` に置いた
//  **最古の版の見本**を変換すると、**期待結果の見本**と一致すること。
//  変換段の中身を書き換えたときに、意図しない差が出れば必ずここが落ちる。
//
//  【照合の粒度】
//  JSON の**意味**（`serde_json::Value` としての一致）で比べる。
//  ファイルの整形・欄の並びは見ない（並びは書き出し側の責務で、
//  `migration::tests::stamped_json_is_the_plain_body_with_one_extra_line` が固定している）。
//
//  【見本を足すとき】
//  (1) `tests/fixtures/migration/<形式>/vN.<拡張子>` と `vM.<拡張子>` を置く
//  (2) 下の `CASES` に 1 行足す
//  (3) `tests/fixtures/README.md` の対応表にも 1 行足す
// ============================================================

use serde_json::Value;

use super::kind::{FormatKind, IMPLICIT_FIRST_VERSION};
use super::runner;

/// ゴールデンの 1 件（変換前のテキストと期待結果のテキスト）。
struct GoldenCase {
    /// 対象の形式。
    kind: FormatKind,
    /// 見本の名前（失敗時のメッセージ用）。
    label: &'static str,
    /// 変換前（最古の版）のテキスト。
    before: &'static str,
    /// 期待結果（現行版）のテキスト。
    after: &'static str,
}

/// 照合する見本の一覧。
const CASES: &[GoldenCase] = &[
    GoldenCase {
        kind: FormatKind::Scene,
        label: "scene v1 → v2（旧 enum 表記の正規化）",
        before: include_str!("../../../../tests/fixtures/migration/scene/v1.scene"),
        after: include_str!("../../../../tests/fixtures/migration/scene/v2.scene"),
    },
    GoldenCase {
        kind: FormatKind::Actor,
        label: "actor v1 → v2（旧 enum 表記の正規化）",
        before: include_str!("../../../../tests/fixtures/migration/actor/v1.actor"),
        after: include_str!("../../../../tests/fixtures/migration/actor/v2.actor"),
    },
];

/// 変換前の見本を現行版まで持ち上げると、期待結果の見本と一致すること。
#[test]
fn converting_the_before_fixture_yields_the_after_fixture() {
    for case in CASES {
        let mut before: Value = serde_json::from_str(case.before)
            .unwrap_or_else(|e| panic!("{}: 変換前の見本が JSON として読めない: {e}", case.label));
        let expected: Value = serde_json::from_str(case.after)
            .unwrap_or_else(|e| panic!("{}: 期待結果の見本が JSON として読めない: {e}", case.label));

        let report = runner::migrate_to_current(case.kind, &mut before)
            .unwrap_or_else(|e| panic!("{}: 変換に失敗した: {e}", case.label));

        assert!(report.changed(), "{}: 変換が 1 段も走っていない", case.label);
        assert_eq!(
            before, expected,
            "{}: 変換結果が期待結果と違う\n--- 実際 ---\n{}\n--- 期待 ---\n{}",
            case.label,
            serde_json::to_string_pretty(&before).unwrap_or_default(),
            serde_json::to_string_pretty(&expected).unwrap_or_default(),
        );
    }
}

/// 見本そのものの前提: 変換前は最古の版、期待結果は現行版であること。
///
/// 現行版を上げたのに見本を差し替え忘れると、ここが「期待結果が古い」と教える。
#[test]
fn fixtures_declare_the_expected_versions() {
    for case in CASES {
        let before: Value = serde_json::from_str(case.before).expect("変換前の見本");
        let after: Value = serde_json::from_str(case.after).expect("期待結果の見本");

        assert_eq!(
            runner::read_version(case.kind, &before).expect("変換前の版"),
            IMPLICIT_FIRST_VERSION,
            "{}: 変換前の見本は最古の版（版の欄なし）であること",
            case.label
        );
        assert_eq!(
            runner::read_version(case.kind, &after).expect("期待結果の版"),
            case.kind.current_version(),
            "{}: 期待結果の見本の版が現行版と違う（見本を更新すること）",
            case.label
        );
    }
}

/// 期待結果の見本をもう一度変換しても何も変わらないこと（冪等）。
#[test]
fn after_fixture_is_already_up_to_date() {
    for case in CASES {
        let mut after: Value = serde_json::from_str(case.after).expect("期待結果の見本");
        let original = after.clone();
        let report = runner::migrate_to_current(case.kind, &mut after).expect("再変換");
        assert!(!report.changed(), "{}: 期待結果がまだ古い版のまま", case.label);
        assert_eq!(after, original, "{}: 再変換で中身が変わった", case.label);
    }
}

/// 変換後の見本が、本体のデシリアライズを通せること。
///
/// 「JSON としては直ったが、エンジンが読めない形になっていた」を防ぐ。
/// アクタは `ActorData` で、シーンは `actors` 配列を `Vec<ActorData>` として検証する
/// （`SceneData` は `scene.rs` の内部型なのでここからは触れない）。
#[test]
fn converted_fixture_deserializes_into_actor_data() {
    use crate::engine::structs::objects::actor::ActorData;

    for case in CASES {
        let mut value: Value = serde_json::from_str(case.before).expect("変換前の見本");
        runner::migrate_to_current(case.kind, &mut value).expect("変換");

        match case.kind {
            FormatKind::Actor => {
                serde_json::from_value::<ActorData>(value).unwrap_or_else(|e| {
                    panic!("{}: 変換後の見本が ActorData として読めない: {e}", case.label)
                });
            }
            FormatKind::Scene => {
                let actors = value
                    .get_mut("actors")
                    .map(Value::take)
                    .expect("シーンの見本には actors がある");
                serde_json::from_value::<Vec<ActorData>>(actors).unwrap_or_else(|e| {
                    panic!(
                        "{}: 変換後の見本のアクタが ActorData として読めない: {e}",
                        case.label
                    )
                });
            }
        }
    }
}
