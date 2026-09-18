// ============================================================
//  registry.rs — (形式, 変換元の版) → 変換関数 の表
//
//  【役割】
//  連鎖の実行（runner）から「次の 1 段」を引くための表。段を足すときに
//  触るのはこの表の 1 行と `kind.rs` の現行版だけになるようにしてある。
//
//  【抜け・重複の検出】
//  表が 1 から現行版まで途切れなく並んでいることは**テストで固定**する
//  （`registry_covers_every_version_without_gaps`）。段を足し忘れて現行版だけ
//  上げた場合、実行時に `MigrationError::MissingStep` になる前に
//  `cargo test` が落ちる。
// ============================================================

use serde_json::Value;

use super::kind::{FormatKind, IMPLICIT_FIRST_VERSION};
use super::steps;

// ── 表の型 ───────────────────────────────────────────────────────

/// 変換 1 段の関数型。
///
/// `&mut Value` を現行版へ 1 段だけ持ち上げる純関数。失敗理由は人が読む文字列
/// （`MigrationError::StepFailed` の `detail` に入る）。
pub type StepFn = fn(&mut Value) -> Result<(), String>;

/// 表の 1 行（どの形式の、どの版からの段か）。
pub struct StepEntry {
    /// 対象の形式。
    pub kind: FormatKind,
    /// 変換元の版（この段は `from_version` → `from_version + 1` を行う）。
    pub from_version: u32,
    /// 変換の実体。
    pub func: StepFn,
}

// ── 表 ───────────────────────────────────────────────────────────

/// 登録済みの変換段。形式ごとに `from_version` の昇順で並べる（可読性のため。
/// 実行時の探索は `lookup` が行うので順序に依存はしない）。
pub const STEPS: &[StepEntry] = &[
    StepEntry {
        kind: FormatKind::Scene,
        from_version: 1,
        func: steps::scene::v1_to_v2::migrate,
    },
    StepEntry {
        kind: FormatKind::Actor,
        from_version: 1,
        func: steps::actor::v1_to_v2::migrate,
    },
    StepEntry {
        kind: FormatKind::InputMap,
        from_version: 1,
        func: steps::inputmap::v1_to_v2::migrate,
    },
];

/// `(形式, 変換元の版)` に対応する変換関数を返す。未登録なら `None`。
pub fn lookup(kind: FormatKind, from_version: u32) -> Option<StepFn> {
    STEPS
        .iter()
        .find(|e| e.kind == kind && e.from_version == from_version)
        .map(|e| e.func)
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 表の網羅性: どの形式も 1 から現行版まで、抜けも重複もなく段が並んでいること。
    ///
    /// これが落ちるのは「`kind.rs` の現行版を上げたが `steps` を足していない」
    /// または「同じ版の段を二重に登録した」とき。
    #[test]
    fn registry_covers_every_version_without_gaps() {
        for kind in FormatKind::ALL.iter().copied() {
            let current = kind.current_version();
            for from in IMPLICIT_FIRST_VERSION..current {
                let matches = STEPS
                    .iter()
                    .filter(|e| e.kind == kind && e.from_version == from)
                    .count();
                assert_eq!(
                    matches, 1,
                    "{kind} の v{from} → v{} の段が {matches} 件登録されている（1 件であること）",
                    from + 1
                );
            }
        }
    }

    /// 現行版以上からの段が登録されていないこと（前進のみ・終着点は現行版）。
    #[test]
    fn registry_has_no_step_at_or_beyond_current_version() {
        for entry in STEPS {
            assert!(
                entry.from_version < entry.kind.current_version(),
                "{} の v{} からの段は現行版 {} を超えて進んでしまう",
                entry.kind,
                entry.from_version,
                entry.kind.current_version()
            );
            assert!(
                entry.from_version >= IMPLICIT_FIRST_VERSION,
                "{} に v{} からの段がある（最古は v{IMPLICIT_FIRST_VERSION}）",
                entry.kind,
                entry.from_version
            );
        }
    }

    /// 表に載っている形式は `FormatKind::ALL` に含まれていること（列挙漏れの検出）。
    #[test]
    fn every_registered_kind_is_listed_in_all() {
        for entry in STEPS {
            assert!(
                FormatKind::ALL.contains(&entry.kind),
                "{} が FormatKind::ALL に無い",
                entry.kind
            );
        }
    }

    /// `lookup` が表と一致すること。
    #[test]
    fn lookup_returns_registered_steps_only() {
        assert!(lookup(FormatKind::Scene, 1).is_some());
        assert!(lookup(FormatKind::Actor, 1).is_some());
        assert!(lookup(FormatKind::InputMap, 1).is_some());
        // 現行版からの段は無い
        assert!(lookup(FormatKind::Scene, FormatKind::Scene.current_version()).is_none());
        // 0 版という概念は無い
        assert!(lookup(FormatKind::Actor, 0).is_none());
        // 実変換が無い形式（現行版 1）には段が 1 つも無い
        assert!(lookup(FormatKind::Anim, 1).is_none());
        assert!(lookup(FormatKind::ProjectSettings, 1).is_none());
    }
}
