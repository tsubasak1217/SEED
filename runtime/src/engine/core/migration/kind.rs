// ============================================================
//  kind.rs — アセット形式の種類と、形式ごとの「現行版・版の欄・対象拡張子」の表
//
//  【役割】
//  マイグレーション機構で唯一の「表」。形式を 1 つ足す／版を 1 つ上げるときに
//  書き換えるのはこのファイルの表と registry.rs の登録行だけになるようにしてある
//  （docs/asset_migration.md 4 章のチェックリスト）。
//
//  【表に入れるもの】
//  - 現行版（このエンジンが書き出す版）
//  - 版を格納する JSON の欄名
//  - その形式に属するファイル拡張子（一括アップグレードの列挙に使う）
//
//  版の欄が無いファイルは 1 版とみなす（docs/asset_migration.md 1 章）。
// ============================================================

use std::fmt;
use std::path::Path;

// ── 定数 ─────────────────────────────────────────────────────────

/// JSON 形式が版を格納するトップレベルの欄名。
///
/// 現状 `.scene` / `.actor` とも同じ名前を使う。将来「別の欄名を持つ形式」を
/// 足せるように `FormatSpec::version_key` を経由して参照すること
/// （この定数を直接読むのは表の定義と刻印処理だけ）。
pub const JSON_VERSION_KEY: &str = "format_version";

/// 版の欄が無いファイルを何版とみなすか。
pub const IMPLICIT_FIRST_VERSION: u32 = 1;

// ── FormatSpec ───────────────────────────────────────────────────

/// 1 つのアセット形式の仕様（表の 1 行）。
pub struct FormatSpec {
    /// レポート・エラーメッセージに出す形式名（小文字・英字）。
    pub label: &'static str,
    /// 版を格納する JSON の欄名。
    pub version_key: &'static str,
    /// このエンジンが読み書きできる最新の版。保存時はこの版を刻む。
    pub current_version: u32,
    /// この形式に属するファイル拡張子（ドット無し・小文字）。
    pub extensions: &'static [&'static str],
}

/// `.scene`（シーン）の仕様。
///
/// - v1: 欄なしの従来形式
/// - v2: 旧 enum 表記（`blend: "alpha"/"additive"`・`shape: "point"`・
///       `gravity_mode: "screen_down"`）を現行表記へ正規化
const SCENE_SPEC: FormatSpec = FormatSpec {
    label: "scene",
    version_key: JSON_VERSION_KEY,
    current_version: 2,
    extensions: &["scene"],
};

/// `.actor` / `.actor2d`（アクタ＝プレハブ）の仕様。版の内容は `.scene` と同じ。
///
/// `.actor2d` は 2D アクタのプレハブで、中身は `.actor` と同じ `ActorData` JSON。
const ACTOR_SPEC: FormatSpec = FormatSpec {
    label: "actor",
    version_key: JSON_VERSION_KEY,
    current_version: 2,
    extensions: &["actor", "actor2d"],
};

// ── FormatKind ───────────────────────────────────────────────────

/// マイグレーションの対象となるアセット形式。
///
/// 形式を足すときは variant → `spec()` の腕 → `ALL` の 3 か所を更新する
/// （`spec()` は網羅 match なので腕の書き忘れはビルドが落として教えてくれる）。
#[derive(Clone, Copy, PartialEq, Eq, Debug, Hash)]
pub enum FormatKind {
    /// シーン（`.scene`）
    Scene,
    /// アクタ＝プレハブ（`.actor` / `.actor2d`）
    Actor,
}

impl FormatKind {
    /// 全形式の一覧（網羅テスト・一括アップグレードの列挙が使う）。
    pub const ALL: &'static [FormatKind] = &[FormatKind::Scene, FormatKind::Actor];

    /// この形式の仕様（表の 1 行）を返す。
    pub fn spec(self) -> &'static FormatSpec {
        match self {
            FormatKind::Scene => &SCENE_SPEC,
            FormatKind::Actor => &ACTOR_SPEC,
        }
    }

    /// レポート・エラーメッセージ用の形式名。
    pub fn label(self) -> &'static str {
        self.spec().label
    }

    /// 版を格納する JSON の欄名。
    pub fn version_key(self) -> &'static str {
        self.spec().version_key
    }

    /// このエンジンが書き出す版（＝連鎖の終着点）。
    pub fn current_version(self) -> u32 {
        self.spec().current_version
    }

    /// この形式に属するファイル拡張子（ドット無し・小文字）。
    pub fn extensions(self) -> &'static [&'static str] {
        self.spec().extensions
    }

    /// ファイルパスの拡張子から形式を判定する。対象外なら `None`。
    ///
    /// 拡張子の大小は無視する（Windows のファイルシステムは大小を区別しないため、
    /// `Foo.Scene` のような綴りが混ざりうる）。
    pub fn from_path(path: &Path) -> Option<FormatKind> {
        let ext = path.extension()?.to_string_lossy().to_ascii_lowercase();
        FormatKind::ALL
            .iter()
            .copied()
            .find(|k| k.extensions().contains(&ext.as_str()))
    }
}

impl fmt::Display for FormatKind {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.label())
    }
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 表の不変条件: 現行版は必ず「欄なし＝1 版」以上であること。
    /// （現行版 0 や負の版は連鎖の前提を壊す）
    #[test]
    fn current_versions_are_at_least_the_implicit_first_version() {
        for kind in FormatKind::ALL {
            assert!(
                kind.current_version() >= IMPLICIT_FIRST_VERSION,
                "{kind} の現行版が {} で、欄なしの既定 {IMPLICIT_FIRST_VERSION} を下回っている",
                kind.current_version()
            );
        }
    }

    /// 表の不変条件: 拡張子は形式間で重複しないこと（`from_path` の判定が一意になる）。
    #[test]
    fn extensions_are_unique_across_formats() {
        let mut seen: Vec<&str> = Vec::new();
        for kind in FormatKind::ALL {
            for ext in kind.extensions() {
                assert!(
                    !seen.contains(ext),
                    "拡張子 {ext} が複数の形式に登録されている"
                );
                assert_eq!(
                    *ext,
                    ext.to_ascii_lowercase(),
                    "拡張子 {ext} は小文字で登録すること（from_path が小文字で比較する）"
                );
                seen.push(ext);
            }
        }
    }

    /// 拡張子からの判定（大小無視・対象外は None）。
    #[test]
    fn from_path_matches_extension_ignoring_case() {
        assert_eq!(
            FormatKind::from_path(Path::new("a/b/Main.scene")),
            Some(FormatKind::Scene)
        );
        assert_eq!(
            FormatKind::from_path(Path::new("a/b/Main.SCENE")),
            Some(FormatKind::Scene)
        );
        assert_eq!(
            FormatKind::from_path(Path::new("Fish.actor")),
            Some(FormatKind::Actor)
        );
        assert_eq!(
            FormatKind::from_path(Path::new("Hud.actor2d")),
            Some(FormatKind::Actor)
        );
        assert_eq!(FormatKind::from_path(Path::new("note.txt")), None);
        assert_eq!(FormatKind::from_path(Path::new("noext")), None);
    }
}
