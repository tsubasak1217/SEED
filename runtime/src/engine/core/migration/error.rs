// ============================================================
//  error.rs — マイグレーションのエラー型
//
//  【方針】
//  メッセージはそのままエディタのダイアログ／CLI のレポートに出る。
//  「何が起きたか」と「どうすればよいか」が利用者に分かる日本語で書く。
//  特に未来版の拒否は、ユーザーから見ると「開けない」だけなので、
//  原因（新しいエンジンで保存された）をはっきり伝える。
// ============================================================

use std::fmt;

use super::kind::FormatKind;

/// マイグレーション（版の判定・連鎖・変換）で起こりうる失敗。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MigrationError {
    /// JSON のトップレベルがオブジェクトでない（版の欄を置く場所が無い）。
    NotObject { kind: FormatKind },
    /// 版の欄があるが、非負整数として読めない。
    InvalidVersion { kind: FormatKind, found: String },
    /// このエンジンが対応する版より新しい（前進のみの前提により読み込めない）。
    FutureVersion {
        kind: FormatKind,
        found: u32,
        supported: u32,
    },
    /// 連鎖の途中の段が registry に登録されていない（実装漏れ）。
    MissingStep {
        kind: FormatKind,
        from: u32,
        target: u32,
    },
    /// 変換 1 段が失敗した（データの形が想定と違う）。
    StepFailed {
        kind: FormatKind,
        from: u32,
        to: u32,
        detail: String,
    },
    /// JSON としてパースできない（ファイルが壊れている／別形式）。
    Parse { kind: FormatKind, detail: String },
}

impl fmt::Display for MigrationError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            MigrationError::NotObject { kind } => write!(
                f,
                "{kind} ファイルのトップレベルが JSON オブジェクトではありません（形式が違うか壊れています）"
            ),
            MigrationError::InvalidVersion { kind, found } => write!(
                f,
                "{kind} ファイルのバージョン欄が数値ではありません（値: {found}）"
            ),
            MigrationError::FutureVersion {
                kind,
                found,
                supported,
            } => write!(
                f,
                "このファイルは新しいバージョンのエンジンで保存されています（{kind} 形式 {found}、対応は {supported} まで）"
            ),
            MigrationError::MissingStep { kind, from, target } => write!(
                f,
                "{kind} 形式の変換手順が足りません（v{from} → v{} が未実装のため v{target} まで持ち上げられません）",
                from + 1
            ),
            MigrationError::StepFailed {
                kind,
                from,
                to,
                detail,
            } => write!(
                f,
                "{kind} 形式の変換に失敗しました（v{from} → v{to}）: {detail}"
            ),
            MigrationError::Parse { kind, detail } => {
                write!(f, "{kind} ファイルの JSON を読めませんでした: {detail}")
            }
        }
    }
}

impl std::error::Error for MigrationError {}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 未来版のメッセージは「新しいエンジンで保存された」ことと版の数字を伝えること。
    #[test]
    fn future_version_message_names_versions() {
        let e = MigrationError::FutureVersion {
            kind: FormatKind::Scene,
            found: 5,
            supported: 2,
        };
        let msg = e.to_string();
        assert!(msg.contains("新しいバージョンのエンジン"), "{msg}");
        assert!(msg.contains('5') && msg.contains('2'), "{msg}");
    }

    /// 段の欠落メッセージは「どの段が無いか」を示すこと。
    #[test]
    fn missing_step_message_names_the_missing_step() {
        let e = MigrationError::MissingStep {
            kind: FormatKind::Actor,
            from: 2,
            target: 4,
        };
        let msg = e.to_string();
        assert!(msg.contains("v2 → v3"), "{msg}");
    }
}
