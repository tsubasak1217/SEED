// ============================================================
//  runner.rs — 変換の連鎖を実行する
//
//  【規則（docs/asset_migration.md 1 章）】
//  - 版の欄が無いファイルは 1 版とみなす
//  - 現行版なら何もしない
//  - 現行版より新しい版は**読み込みを拒否**する（前進のみの前提を守るため）
//  - 版は 1 段ずつ連鎖させる。途中の段が無ければエラー（黙って飛ばさない）
//  - 実行した段は `MigrationReport` で呼び出し側へ返す
// ============================================================

use serde_json::Value;

use super::error::MigrationError;
use super::kind::{FormatKind, IMPLICIT_FIRST_VERSION};
use super::registry;

// ── レポート ─────────────────────────────────────────────────────

/// 実行した変換 1 段の記録。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AppliedStep {
    /// 変換元の版。
    pub from: u32,
    /// 変換先の版（常に `from + 1`）。
    pub to: u32,
}

impl std::fmt::Display for AppliedStep {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "v{} → v{}", self.from, self.to)
    }
}

/// 1 ファイル分の変換結果。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MigrationReport {
    /// 対象の形式。
    pub kind: FormatKind,
    /// 読み込んだときの版（欄が無ければ `IMPLICIT_FIRST_VERSION`）。
    pub from_version: u32,
    /// 変換後の版（＝現行版）。
    pub to_version: u32,
    /// 実行した段の一覧（実行順）。
    pub steps: Vec<AppliedStep>,
}

impl MigrationReport {
    /// 実際に変換が行われたか（＝ファイルを書き換える価値があるか）。
    pub fn changed(&self) -> bool {
        !self.steps.is_empty()
    }
}

// ── 版の読み取り ─────────────────────────────────────────────────

/// JSON から版を読む。欄が無ければ `IMPLICIT_FIRST_VERSION`。
///
/// トップレベルがオブジェクトでない／版の欄が非負整数でない場合はエラー。
pub fn read_version(kind: FormatKind, value: &Value) -> Result<u32, MigrationError> {
    let Some(obj) = value.as_object() else {
        return Err(MigrationError::NotObject { kind });
    };
    interpret_version(kind, obj.get(kind.version_key()))
}

/// 版の欄の値（無ければ `None`）を版番号として解釈する。
///
/// `Value` を組み立てずに版だけ先読みする経路（`migration::load_json`）と
/// `read_version` で解釈を 1 か所に揃えるために切り出してある。
pub fn interpret_version(kind: FormatKind, raw: Option<&Value>) -> Result<u32, MigrationError> {
    let Some(raw) = raw else {
        return Ok(IMPLICIT_FIRST_VERSION);
    };
    // 非負整数だけを版として認める（浮動小数・文字列・null は形式違い）。
    match raw.as_u64() {
        Some(v) if v <= u32::MAX as u64 => Ok(v as u32),
        _ => Err(MigrationError::InvalidVersion {
            kind,
            found: raw.to_string(),
        }),
    }
}

// ── 連鎖の実行 ───────────────────────────────────────────────────

/// `value` を現行版まで 1 段ずつ持ち上げる（メモリ上のみ。ファイルは触らない）。
///
/// 成功時、`value` の版の欄は現行版に更新される（部分的に変換された値が
/// 古い版を名乗ったまま出回るのを防ぐため）。既に現行版なら段は 1 つも走らず、
/// 版の欄だけが明示的に書かれる。
pub fn migrate_to_current(
    kind: FormatKind,
    value: &mut Value,
) -> Result<MigrationReport, MigrationError> {
    let current = kind.current_version();
    let from_version = read_version(kind, value)?;

    // 未来の版は拒否する。前進のみ・1 段ずつという前提を守れないため。
    if from_version > current {
        return Err(MigrationError::FutureVersion {
            kind,
            found: from_version,
            supported: current,
        });
    }

    let mut steps = Vec::new();
    let mut version = from_version;
    while version < current {
        let Some(func) = registry::lookup(kind, version) else {
            return Err(MigrationError::MissingStep {
                kind,
                from: version,
                target: current,
            });
        };
        func(value).map_err(|detail| MigrationError::StepFailed {
            kind,
            from: version,
            to: version + 1,
            detail,
        })?;
        steps.push(AppliedStep {
            from: version,
            to: version + 1,
        });
        version += 1;
    }

    // 版の欄を現行版へ更新する（変換段は版の欄に触らない約束なのでここで行う）。
    if let Some(obj) = value.as_object_mut() {
        obj.insert(kind.version_key().to_string(), Value::from(current));
    }

    Ok(MigrationReport {
        kind,
        from_version,
        to_version: current,
        steps,
    })
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// 版の欄が無いファイルは 1 版として扱われること。
    #[test]
    fn missing_version_field_means_version_one() {
        let v = json!({ "name": "S", "actors": [] });
        assert_eq!(
            read_version(FormatKind::Scene, &v).unwrap(),
            IMPLICIT_FIRST_VERSION
        );
    }

    /// 版の欄が整数でなければエラーになること。
    #[test]
    fn non_integer_version_is_rejected() {
        for bad in [json!("2"), json!(2.5), json!(null), json!(-1)] {
            let v = json!({ "format_version": bad });
            let err = read_version(FormatKind::Scene, &v).unwrap_err();
            assert!(
                matches!(err, MigrationError::InvalidVersion { .. }),
                "{bad} が弾かれていない: {err}"
            );
        }
    }

    /// トップレベルがオブジェクトでなければエラーになること。
    #[test]
    fn non_object_top_level_is_rejected() {
        let v = json!([1, 2, 3]);
        assert!(matches!(
            read_version(FormatKind::Scene, &v).unwrap_err(),
            MigrationError::NotObject { .. }
        ));
    }

    /// 現行版のファイルは段が 1 つも走らないこと（`changed()` が false）。
    #[test]
    fn current_version_runs_no_steps() {
        let current = FormatKind::Scene.current_version();
        let mut v = json!({ "format_version": current, "name": "S", "actors": [] });
        let report = migrate_to_current(FormatKind::Scene, &mut v).unwrap();
        assert!(!report.changed());
        assert_eq!(report.from_version, current);
        assert_eq!(report.to_version, current);
        assert!(report.steps.is_empty());
    }

    /// 未来の版は拒否されること（メッセージに版の数字が入る）。
    #[test]
    fn future_version_is_rejected() {
        let future = FormatKind::Scene.current_version() + 1;
        let mut v = json!({ "format_version": future, "name": "S", "actors": [] });
        let err = migrate_to_current(FormatKind::Scene, &mut v).unwrap_err();
        match err {
            MigrationError::FutureVersion {
                found, supported, ..
            } => {
                assert_eq!(found, future);
                assert_eq!(supported, FormatKind::Scene.current_version());
            }
            other => panic!("未来版が拒否されていない: {other}"),
        }
    }

    /// 欄なし（v1）のファイルが現行版まで連鎖し、段が順番に記録されること。
    #[test]
    fn chains_from_version_one_to_current_in_order() {
        let mut v = json!({ "name": "S", "actors": [] });
        let report = migrate_to_current(FormatKind::Scene, &mut v).unwrap();
        let current = FormatKind::Scene.current_version();

        assert_eq!(report.from_version, IMPLICIT_FIRST_VERSION);
        assert_eq!(report.to_version, current);
        assert_eq!(report.steps.len(), (current - IMPLICIT_FIRST_VERSION) as usize);
        // 段は v1→v2, v2→v3, … と隙間なく並ぶ
        for (i, step) in report.steps.iter().enumerate() {
            assert_eq!(step.from, IMPLICIT_FIRST_VERSION + i as u32);
            assert_eq!(step.to, step.from + 1);
        }
        // 変換後の値は現行版を名乗る
        assert_eq!(v["format_version"], json!(current));
    }

    /// 段が欠けていると `MissingStep` で止まること（黙って飛ばさない）。
    ///
    /// 実表には穴が無いので、runner の分岐だけをその場の小さな表で確かめる。
    #[test]
    fn missing_step_is_detected() {
        // registry::lookup が None を返す版から始めたのと同じ状況を作るため、
        // 現行版より 2 つ古い「存在しない版」からの連鎖を試す。
        // 実表が v1 しか持たないので、v0 を名乗る値は最初の lookup で落ちる。
        let mut v = json!({ "format_version": 0, "name": "S", "actors": [] });
        let err = migrate_to_current(FormatKind::Scene, &mut v).unwrap_err();
        match err {
            MigrationError::MissingStep { from, target, .. } => {
                assert_eq!(from, 0);
                assert_eq!(target, FormatKind::Scene.current_version());
            }
            other => panic!("段の欠落が検出されていない: {other}"),
        }
    }

    /// 実際の変換（旧 enum 表記）が連鎖で適用されること。
    #[test]
    fn applies_real_conversion_through_the_chain() {
        let mut v = json!({
            "name": "S",
            "actors": [ { "name": "a", "components": [
                { "name": "FX", "component": { "type": "ParticleEmitterComponent",
                  "data": { "blend": "additive" } } }
            ], "children": [] } ]
        });
        let report = migrate_to_current(FormatKind::Scene, &mut v).unwrap();
        assert!(report.changed());
        assert_eq!(
            v["actors"][0]["components"][0]["component"]["data"]["blend"],
            json!("add")
        );
    }

    /// 2 回流しても結果が変わらないこと（一括アップグレードの再実行で壊れない）。
    #[test]
    fn migrating_twice_is_idempotent() {
        let mut v = json!({
            "name": "S",
            "actors": [ { "name": "a", "components": [
                { "name": "FX", "component": { "type": "ParticleEmitterComponent",
                  "data": { "blend": "alpha" } } }
            ], "children": [] } ]
        });
        migrate_to_current(FormatKind::Scene, &mut v).unwrap();
        let once = v.clone();
        let report = migrate_to_current(FormatKind::Scene, &mut v).unwrap();
        assert!(!report.changed());
        assert_eq!(v, once);
    }
}
