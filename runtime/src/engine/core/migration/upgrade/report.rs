// ============================================================
//  upgrade/report.rs — 一括アップグレードの結果表現
//
//  【出力の約束】
//  1 ファイル 1 行の JSON（JSON Lines）。最後に集計 1 行。
//  エディタ（M2）はこれを 1 行ずつ読んで一覧に並べるので、
//  **人向けの装飾行を混ぜない**こと。
//
//    {"kind":"scene","path":"assets/…","from":1,"to":2,"status":"upgraded","message":""}
//    {"kind":"prefab_hash","path":"assets/…","updated":3,"message":""}
//    {"kind":"summary","total":12,"upgraded":3,…}
//
//  行の種別は必ず `kind` で見分ける。`kind` は形式名（`scene` / `actor` / `anim` …）か、
//  `prefab_hash`（プレハブの版の貼り直し）か、`summary`（集計）か、`error`（致命的な失敗）。
// ============================================================

use serde::Serialize;

use super::prefab_rehash::SceneRestamp;
use crate::engine::core::migration::kind::FormatKind;

// ── 定数 ─────────────────────────────────────────────────────────

/// プレハブの版の貼り直し行を見分ける印。
const PREFAB_REHASH_KIND: &str = "prefab_hash";

/// 集計行を見分ける印。
const SUMMARY_KIND: &str = "summary";

// ── 1 件の結果 ───────────────────────────────────────────────────

/// 1 ファイルの処理結果。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UpgradeStatus {
    /// 変換して書き込んだ（`--dry-run` のときは「書き込む予定」）。
    Upgraded,
    /// 既に現行版なので何もしなかった。
    UpToDate,
    /// このエンジンより新しい版なので触れなかった。
    FutureVersion,
    /// 読み取り・変換・書き込みのいずれかに失敗した。
    Failed,
}

impl UpgradeStatus {
    /// レポート JSON に出す値。
    pub fn as_str(self) -> &'static str {
        match self {
            UpgradeStatus::Upgraded => "upgraded",
            UpgradeStatus::UpToDate => "up_to_date",
            UpgradeStatus::FutureVersion => "future_version",
            UpgradeStatus::Failed => "failed",
        }
    }

}

/// レポートの 1 行（JSON Lines の 1 件分）。
#[derive(Serialize)]
pub struct FileReport {
    /// 形式（`scene` / `actor`）。
    pub kind: &'static str,
    /// アセットルートの親から見た相対パス（スラッシュ区切り）。
    pub path: String,
    /// 読み込んだときの版（判定できなければ 0）。
    pub from: u32,
    /// 変換後の版（変換していなければ `from` と同じ）。
    pub to: u32,
    /// 結果。
    pub status: &'static str,
    /// 補足（失敗理由・警告。無ければ空文字）。
    pub message: String,
}

impl FileReport {
    /// 1 件分のレポートを組み立てる。
    pub fn new(
        kind: FormatKind,
        path: String,
        from: u32,
        to: u32,
        status: UpgradeStatus,
        message: impl Into<String>,
    ) -> Self {
        Self {
            kind: kind.label(),
            path,
            from,
            to,
            status: status.as_str(),
            message: message.into(),
        }
    }
}

// ── プレハブの版の貼り直し ───────────────────────────────────────

/// プレハブの版（`prefab_hash`）を貼り直したシーン 1 件ぶんのレポート行。
///
/// アップグレードで `.actor` の生テキストが変わると、シーンに焼き込まれた
/// `prefab_hash` が古くなる。その貼り直し結果をファイルの行とは別の種別で出す
/// （同じシーンについて「アップグレード」と「貼り直し」の 2 行が出ることがある）。
#[derive(Serialize)]
pub struct PrefabRehashReport {
    /// 常に `"prefab_hash"`。
    pub kind: &'static str,
    /// アセットルートの親から見た相対パス（スラッシュ区切り）。
    pub path: String,
    /// 貼り直したインスタンス数。
    pub updated: usize,
    /// 補足（失敗理由・警告。無ければ空文字）。
    pub message: String,
}

impl From<SceneRestamp> for PrefabRehashReport {
    fn from(r: SceneRestamp) -> Self {
        Self {
            kind: PREFAB_REHASH_KIND,
            path: r.path,
            updated: r.updated,
            message: r.message,
        }
    }
}

// ── 集計 ─────────────────────────────────────────────────────────

/// 全件の集計（最後の 1 行と、呼び出し側の終了コード判定に使う）。
#[derive(Serialize, Debug, Default, Clone, PartialEq, Eq)]
pub struct UpgradeSummary {
    /// 常に `"summary"`（1 行 1 件の中で集計行を見分けるための印）。
    pub kind: &'static str,
    /// 対象ファイル総数。
    pub total: usize,
    /// 変換した（する予定の）件数。
    pub upgraded: usize,
    /// 既に現行版だった件数。
    pub up_to_date: usize,
    /// 未来版で触れなかった件数。
    pub future_version: usize,
    /// 失敗した件数。
    pub failed: usize,
    /// `prefab_hash` を貼り直したシーン数。
    pub prefab_hash_scenes: usize,
    /// `prefab_hash` を貼り直したインスタンス総数。
    pub prefab_hash_updated: usize,
    /// 書き込みを行わない実行だったか。
    pub dry_run: bool,
}

impl UpgradeSummary {
    /// 空の集計を作る。
    pub fn new(dry_run: bool) -> Self {
        Self {
            kind: SUMMARY_KIND,
            dry_run,
            ..Default::default()
        }
    }

    /// 1 件分の結果を数え上げる。
    pub fn count(&mut self, status: UpgradeStatus) {
        self.total += 1;
        match status {
            UpgradeStatus::Upgraded => self.upgraded += 1,
            UpgradeStatus::UpToDate => self.up_to_date += 1,
            UpgradeStatus::FutureVersion => self.future_version += 1,
            UpgradeStatus::Failed => self.failed += 1,
        }
    }

    /// 1 シーンぶんの `prefab_hash` 貼り直しを数え上げる。
    ///
    /// ファイルの総数（`total`）には足さない。貼り直しはアップグレードの
    /// **後処理**であり、対象ファイル数を二重に数えると集計が読めなくなる。
    pub fn count_prefab_rehash(&mut self, updated: usize) {
        self.prefab_hash_scenes += 1;
        self.prefab_hash_updated += updated;
    }

    /// 対処が必要な結果が 1 件でもあったか（終了コードを非 0 にする条件）。
    ///
    /// 失敗はもちろん、未来版も「このエンジンではアップグレードできなかった」という
    /// 未解決の状態なので非 0 にする（放置するとその後の読み込みで必ず拒否される）。
    pub fn has_problem(&self) -> bool {
        self.failed > 0 || self.future_version > 0
    }
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 集計が状態ごとに正しく積み上がり、問題の有無を判定できること。
    #[test]
    fn summary_counts_each_status() {
        let mut s = UpgradeSummary::new(false);
        s.count(UpgradeStatus::Upgraded);
        s.count(UpgradeStatus::UpToDate);
        s.count(UpgradeStatus::UpToDate);
        assert_eq!(s.total, 3);
        assert_eq!(s.upgraded, 1);
        assert_eq!(s.up_to_date, 2);
        assert!(!s.has_problem());

        s.count(UpgradeStatus::FutureVersion);
        assert!(s.has_problem(), "未来版は未解決として扱うこと");
    }

    /// `prefab_hash` の貼り直しは後処理なので、対象ファイル総数を増やさないこと。
    #[test]
    fn prefab_rehash_is_counted_separately_from_files() {
        let mut s = UpgradeSummary::new(false);
        s.count(UpgradeStatus::Upgraded);
        s.count_prefab_rehash(3);
        s.count_prefab_rehash(1);
        assert_eq!(s.total, 1, "貼り直しをファイル数に数えてはいけない");
        assert_eq!(s.prefab_hash_scenes, 2);
        assert_eq!(s.prefab_hash_updated, 4);
        assert!(!s.has_problem(), "貼り直しは問題ではない");
    }

    /// 貼り直し行が約束どおりの JSON になること。
    #[test]
    fn prefab_rehash_report_serializes_with_expected_keys() {
        let r = PrefabRehashReport::from(SceneRestamp {
            path: "assets/scenes/Main.scene".to_string(),
            updated: 2,
            message: String::new(),
        });
        let text = serde_json::to_string(&r).unwrap();
        assert_eq!(
            text,
            r#"{"kind":"prefab_hash","path":"assets/scenes/Main.scene","updated":2,"message":""}"#
        );
    }

    /// 1 件分のレポートが約束どおりの JSON になること。
    #[test]
    fn file_report_serializes_with_expected_keys() {
        let r = FileReport::new(
            FormatKind::Scene,
            "assets/scenes/Main.scene".to_string(),
            1,
            2,
            UpgradeStatus::Upgraded,
            "",
        );
        let text = serde_json::to_string(&r).unwrap();
        assert_eq!(
            text,
            r#"{"kind":"scene","path":"assets/scenes/Main.scene","from":1,"to":2,"status":"upgraded","message":""}"#
        );
    }
}
