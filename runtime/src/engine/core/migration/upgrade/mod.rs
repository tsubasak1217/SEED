// ============================================================
//  upgrade — プロジェクトの一括アップグレード（`SEED.exe --upgrade-project`）
//
//  【何をするか】
//  アセットルート配下の「版を持つ形式」のファイル（`kind.rs` の表にあるもの全部）を
//  列挙し、**古いもの・版の欄が無いもの**を現行版へ直して `safe_write`
//  （旧版を `.backup/` へ退避 → `.tmp` へ書き切って rename）で書き戻す。
//  結果は 1 ファイル 1 行の JSON で出す。
//
//  【「版の欄が無いもの」も書き換える理由】
//  実変換がまだ無い形式（現行版 1）は、欄が無くても意味としては現行版である。
//  それでも版の 1 行を刻んでおくと、次に版を上げたときに
//  「欄なし＝v1」という暗黙の規約に頼らずに済む。差分は**その 1 行だけ**になる。
//
//  【運用】
//  開いただけでは誰のファイルも書き換わらない（読み込み時の変換はメモリ上だけ）。
//  版を上げたエンジンを配ったら、**オーナーが 1 回これを実行して送信する**。
//  その差分は VCS の変更として全員へ届く（docs/asset_migration.md 1 章）。
//
//  【ウィンドウも GPU も作らない】
//  `main` の入口で `--upgrade-project` を見つけたら、`App::run` へ行く前に
//  ここで完結して終了コードを返す。描画資源を一切初期化しないので、
//  エディタが起動中の環境でも安全に実行できる。
//
//  【ファイル構成】
//  | ファイル            | 責務 |
//  |---------------------|------|
//  | `mod.rs`            | 引数の解釈・1 件ずつの処理・レポート出力 |
//  | `target.rs`         | アセットルートの決定と対象ファイルの列挙 |
//  | `canonical.rs`      | 形式ごとの「保存と同じテキスト化」と読めることの検証 |
//  | `prefab_rehash.rs`  | 変換後の `prefab_hash` 貼り直し |
//  | `report.rs`         | 結果の表現（JSON Lines と集計） |
// ============================================================

/// 形式ごとの「保存と同じテキスト化」と、読めることの検証。
pub mod canonical;
/// 変換後の `prefab_hash` 貼り直し。
pub mod prefab_rehash;
/// 結果の表現（JSON Lines と集計）。
pub mod report;
/// アセットルートの決定と対象ファイルの列挙。
pub mod target;

use std::collections::HashMap;
use std::io::Write;
use std::path::{Path, PathBuf};

use serde_json::Value;

use super::kind::FormatKind;
use super::{migrate_to_current, MigrationError};
use crate::engine::core::app_base::prefab_hash::content_hash;
use crate::engine::core::app_base::safe_write;
use report::{FileReport, PrefabRehashReport, UpgradeStatus, UpgradeSummary};

// ── 定数 ─────────────────────────────────────────────────────────

/// 一括アップグレードを要求するコマンドライン引数。
///
/// 値は空白区切り（`--upgrade-project <パス>`）でも `=` 区切り
/// （`--upgrade-project=<パス>`）でも受け付ける。
pub const UPGRADE_FLAG: &str = "--upgrade-project";

/// 書き込みを行わずに結果だけ出すフラグ。
pub const DRY_RUN_FLAG: &str = "--dry-run";

/// 版を判定できなかったときにレポートへ出す版番号。
const UNKNOWN_VERSION: u32 = 0;

/// レポートの `message` に複数の断りを並べるときの区切り。
const MESSAGE_SEPARATOR: &str = " / ";

/// 正常終了の終了コード。
const EXIT_OK: i32 = 0;
/// 対処が必要な結果があったときの終了コード。
const EXIT_PROBLEM: i32 = 1;
/// 引数が不正・対象が見つからないときの終了コード。
const EXIT_BAD_USAGE: i32 = 2;

// ── コマンドラインの入口 ─────────────────────────────────────────

/// 引数に `--upgrade-project` があれば一括アップグレードを実行し、終了コードを返す。
///
/// 無ければ `None` を返す（通常起動を続ける合図）。
pub fn run_cli_if_requested(args: &[String]) -> Option<i32> {
    let project = parse_project_arg(args)?;
    let dry_run = args.iter().any(|a| a == DRY_RUN_FLAG);

    let stdout = std::io::stdout();
    let mut out = std::io::BufWriter::new(stdout.lock());

    let assets_root = match target::resolve_assets_root(Path::new(&project)) {
        Ok(p) => p,
        Err(e) => {
            // 失敗もレポートと同じ 1 行 JSON で出す（エディタが読む先を分けないため）。
            write_line(
                &mut out,
                &serde_json::json!({ "kind": "error", "message": e }),
            );
            let _ = out.flush();
            return Some(EXIT_BAD_USAGE);
        }
    };

    // バックアップを `<assets>/.backup/<相対パス>` へ集めるため、アセット層を初期化する。
    // 未初期化のままだと safe_write は各ファイルの隣へ `.backup` を作ってしまう。
    // このプロセスは変換だけで終了するので、ここで初期化して問題ない。
    crate::engine::asset_fs::init(assets_root.clone(), None);

    let summary = upgrade_project(&assets_root, dry_run, &mut out);
    let _ = out.flush();

    Some(if summary.has_problem() {
        EXIT_PROBLEM
    } else {
        EXIT_OK
    })
}

/// `--upgrade-project` の値を取り出す。指定が無ければ `None`。
///
/// `--upgrade-project=<パス>` と `--upgrade-project <パス>` の両方を受け付ける。
fn parse_project_arg(args: &[String]) -> Option<String> {
    let eq_prefix = format!("{UPGRADE_FLAG}=");
    for (i, arg) in args.iter().enumerate() {
        if let Some(rest) = arg.strip_prefix(&eq_prefix) {
            return Some(rest.to_string());
        }
        if arg == UPGRADE_FLAG {
            // 次の引数が値（別のフラグなら値なしとみなし、空文字を返して usage エラーにする）。
            let value = args
                .get(i + 1)
                .filter(|v| !v.starts_with("--"))
                .cloned()
                .unwrap_or_default();
            return Some(value);
        }
    }
    None
}

// ── 実行本体 ─────────────────────────────────────────────────────

/// アセットルート配下を一括アップグレードし、結果を `out` へ 1 行 1 件で書く。
///
/// * `dry_run` … true ならファイルを一切書かない（結果だけ出す）
///
/// 戻り値は集計。呼び出し側が終了コードの判定に使う。
/// この関数は `asset_fs` を初期化しない（テストから何度でも呼べるようにするため）。
///
/// 【処理の順序】
/// (1) 全ファイルを 1 件ずつ変換する
/// (2) `.actor` の内容が変わったぶんだけ、シーンの `prefab_hash` を貼り直す
///
/// (2) は (1) の後でなければならない。貼り直しに使う新しいハッシュは
/// **書き込み後のファイルの内容**から取るため。
pub fn upgrade_project(assets_root: &Path, dry_run: bool, out: &mut dyn Write) -> UpgradeSummary {
    let mut summary = UpgradeSummary::new(dry_run);
    // `.actor` の「旧ハッシュ → 新ハッシュ」。貼り直しの対象を絞るために使う。
    let mut prefab_hash_changes: HashMap<String, String> = HashMap::new();
    // 貼り直しの走査対象になるシーン（列挙をもう一度やり直さないよう控える）。
    let mut scenes: Vec<PathBuf> = Vec::new();

    for t in target::collect_targets(assets_root) {
        if t.kind == FormatKind::Scene {
            scenes.push(t.path.clone());
        }
        let display = target::display_path(assets_root, &t.path);
        let outcome = upgrade_one(&t.path, t.kind, display, dry_run);
        if let Some((old_hash, new_hash)) = outcome.prefab_hash_change {
            prefab_hash_changes.insert(old_hash, new_hash);
        }
        summary.count(outcome.status);
        write_line(out, &outcome.report);
    }

    // ── プレハブの版（prefab_hash）の貼り直し ──
    for restamp in prefab_rehash::restamp_scenes(assets_root, &scenes, &prefab_hash_changes, dry_run)
    {
        summary.count_prefab_rehash(restamp.updated);
        write_line(out, &PrefabRehashReport::from(restamp));
    }

    write_line(out, &summary);
    summary
}

/// レポート 1 件を JSON 1 行として書く（出力先が壊れていても処理は止めない）。
fn write_line<T: serde::Serialize>(out: &mut dyn Write, value: &T) {
    match serde_json::to_string(value) {
        Ok(text) => {
            let _ = writeln!(out, "{text}");
        }
        Err(e) => {
            let _ = writeln!(out, r#"{{"kind":"error","message":"レポートの生成に失敗: {e}"}}"#);
        }
    }
}

/// 1 ファイルの処理結果（出力用のレポートと、集計用の状態）。
struct FileOutcome {
    report: FileReport,
    status: UpgradeStatus,
    /// `.actor` を書き換えた場合の「旧ハッシュ → 新ハッシュ」。
    ///
    /// シーン側に焼き込まれた `prefab_hash` を貼り直すために使う。
    /// `.actor` 以外・書き換えなかった場合は `None`。
    prefab_hash_change: Option<(String, String)>,
}

impl FileOutcome {
    /// レポートと状態を組み立てる（状態を 2 か所に書かないためのヘルパ）。
    fn new(
        kind: FormatKind,
        display: String,
        from: u32,
        to: u32,
        status: UpgradeStatus,
        message: impl Into<String>,
    ) -> Self {
        Self {
            report: FileReport::new(kind, display, from, to, status, message),
            status,
            prefab_hash_change: None,
        }
    }

    /// プレハブの版の貼り直し情報を添える。
    fn with_prefab_hash_change(mut self, old_hash: String, new_hash: String) -> Self {
        self.prefab_hash_change = Some((old_hash, new_hash));
        self
    }
}

/// 1 ファイルを処理する。
///
/// 手順: 読む → JSON にする → 版を見る → 直す必要があれば変換して書く。
/// どの段階で失敗しても**そのファイルだけ**が `failed` になり、全体は続行する。
fn upgrade_one(path: &Path, kind: FormatKind, display: String, dry_run: bool) -> FileOutcome {
    let failed = |from: u32, message: String| {
        FileOutcome::new(
            kind,
            display.clone(),
            from,
            from,
            UpgradeStatus::Failed,
            message,
        )
    };

    // ── 読む ──
    let raw = match std::fs::read_to_string(path) {
        Ok(t) => t,
        Err(e) => return failed(UNKNOWN_VERSION, format!("読み込み失敗: {e}")),
    };
    let text = raw.strip_prefix('\u{FEFF}').unwrap_or(&raw);

    let mut value: Value = match serde_json::from_str(text) {
        Ok(v) => v,
        Err(e) => return failed(UNKNOWN_VERSION, format!("JSON として読めません: {e}")),
    };

    // ── 版を見る ──
    let from = match super::runner::read_version(kind, &value) {
        Ok(v) => v,
        Err(e) => return failed(UNKNOWN_VERSION, e.to_string()),
    };
    let current = kind.current_version();
    if from > current {
        return FileOutcome::new(
            kind,
            display,
            from,
            from,
            UpgradeStatus::FutureVersion,
            MigrationError::FutureVersion {
                kind,
                found: from,
                supported: current,
            }
            .to_string(),
        );
    }
    // 版の欄が物理的に書かれているか。
    // 「欄が無い＝v1」は暗黙の規約なので、実変換が要らない（from == current）場合でも
    // 版の 1 行だけは刻んでおく。刻んだ後は規約に頼らずに版が判る。
    let stamped = has_version_field(&value, kind);
    if from == current && stamped {
        return FileOutcome::new(kind, display, from, current, UpgradeStatus::UpToDate, "");
    }

    // ── 変換する ──
    // 変換前の姿を控えておく（中身が本当に変わったかの判定に使う）。
    let before = value.clone();
    if let Err(e) = migrate_to_current(kind, &mut value) {
        return failed(from, e.to_string());
    }

    // ── テキストにする ──
    // 本体の型（SceneData / ActorData など）を経由して、普通に保存したときと同じ並びで書く。
    // `Value` をそのまま書くと欄がアルファベット順に並び替わり、中身が変わらない
    // ファイルでも全行が差分になる（差分が読めず、次の保存でまた全行戻る）。
    // 書き手がエディタ（C#）の形式は正準テキストを作れないので、**読めることの検証だけ**行う。
    // dry-run でもここまでは必ず通す（＝書けないファイルを実行前に知らせる検証になる）。
    let canonical = match canonical::render_or_validate(kind, value.clone()) {
        Ok(c) => c,
        Err(e) => return failed(from, e),
    };

    // 変換段が 1 欄も直さなかったファイル（＝版の欄が付くだけ）は、元の整形を保ったまま
    // 版の行だけを差し込む。全行を書き直すと、既定値の明示化や入れ子の並び替えで
    // 数千行の差分になり、オーナーが差分を読めなくなる。
    let spliced = if content_is_unchanged(&before, &value, kind) {
        splice_version_line(text, &before, kind)
    } else {
        None
    };
    let (json, mut message) = match spliced {
        Some(text) => (text, String::new()),
        None => match canonical.text() {
            // 正準の書き手がある形式: 本体の型を経由したテキストで書き直す。
            Some(rendered) => {
                let note = normalization_note(&value, &rendered, kind);
                (rendered, note)
            }
            // 正準の書き手が無い形式（書き手は C#）: 変換後の値をそのまま書く。
            // 欄の並びは `serde_json` の既定に従うため、元の整形は保たれない。
            None => (
                match super::to_stamped_pretty_json(kind, &value) {
                    Ok(t) => t,
                    Err(e) => return failed(from, format!("JSON として書き出せません: {e}")),
                },
                VALUE_REWRITE_NOTE.to_string(),
            ),
        },
    };

    if dry_run {
        if !message.is_empty() {
            message.push_str(MESSAGE_SEPARATOR);
        }
        message.push_str("dry-run のため書き込んでいません");
        return FileOutcome::new(kind, display, from, current, UpgradeStatus::Upgraded, message);
    }

    // ── 書く ──
    match safe_write::write_atomic_with_backup(path, &json) {
        Ok(warning) => {
            if let Some(w) = warning {
                if !message.is_empty() {
                    message.push_str(MESSAGE_SEPARATOR);
                }
                message.push_str(&w);
            }
            let outcome =
                FileOutcome::new(kind, display, from, current, UpgradeStatus::Upgraded, message);
            // `.actor` は生テキストのハッシュがプレハブの版なので、書き換えた前後の値を控える。
            // シーン側の `prefab_hash` を貼り直すのに使う（貼り直しは全ファイルの処理後）。
            if kind == FormatKind::Actor {
                outcome.with_prefab_hash_change(content_hash(&raw), content_hash(&json))
            } else {
                outcome
            }
        }
        Err(e) => failed(from, format!("書き込み失敗: {e}")),
    }
}

/// 版の欄が JSON に**物理的に**書かれているか。
///
/// `read_version` は欄が無いファイルを「1 版」として返すので、
/// 「欄が無い」と「欄に 1 と書いてある」を区別できない。
/// 一括アップグレードは前者にも版の行を刻むため、ここで区別する。
fn has_version_field(value: &Value, kind: FormatKind) -> bool {
    value
        .as_object()
        .is_some_and(|o| o.contains_key(kind.version_key()))
}

/// 変換段がこのファイルの中身を 1 つも変えなかったか（版の欄は比較から外す）。
///
/// 真なら「版の行を足すだけ」で済むので、元のテキストをそのまま活かせる。
fn content_is_unchanged(before: &Value, after: &Value, kind: FormatKind) -> bool {
    let strip = |v: &Value| {
        let mut v = v.clone();
        if let Some(obj) = v.as_object_mut() {
            obj.remove(kind.version_key());
        }
        v
    };
    strip(before) == strip(after)
}

/// 元のテキストの整形を保ったまま、開き波括弧の直後へ版の行だけを差し込む。
///
/// * 版の欄が既にあるテキストには使わない（欄が二重になり、後勝ちで古い版が残る）
/// * トップレベルが空オブジェクト（`{}`）だと末尾カンマになるので `None` を返す
///
/// 字下げは 2 スペース（`serde_json::to_string_pretty` と同じ）を前提にしている。
/// 別の字下げで書かれたファイルでも JSON として正しいままだが、その 1 行だけ幅が揃わない。
///
/// 改行コードは**元のファイルに合わせる**（CRLF のファイルへ LF を混ぜない）。
/// 混ぜると差分に「1 行目が変わった」という無関係なノイズが出るうえ、
/// ファイル内で改行が不揃いになる。
fn splice_version_line(text: &str, parsed: &Value, kind: FormatKind) -> Option<String> {
    // 既に版の欄があるテキストには差し込まない（欄が二重になり、後勝ちで古い版が残る）。
    let obj = parsed.as_object()?;
    if obj.contains_key(kind.version_key()) {
        return None;
    }
    // トップレベルが空（`{}`）だと末尾カンマになるので対象外。
    if obj.is_empty() {
        return None;
    }
    let open = text.find('{')?;
    let rest = text.get(open + 1..)?;
    Some(format!(
        "{}{}{VERSION_LINE_INDENT}\"{}\": {},{}",
        &text[..=open],
        detect_line_ending(rest),
        kind.version_key(),
        kind.current_version(),
        rest
    ))
}

/// 開き波括弧の直後のテキストから、そのファイルが使っている改行コードを推定する。
///
/// 直後が CRLF ならそのファイルは CRLF で書かれているとみなす。
/// 改行が 1 つも無い（1 行 JSON）なら LF を使う（`to_string_pretty` と同じ）。
fn detect_line_ending(rest: &str) -> &'static str {
    match rest.find('\n') {
        Some(at) if at > 0 && rest.as_bytes()[at - 1] == b'\r' => CRLF,
        _ => LF,
    }
}

/// 差し込む版の行の字下げ（`serde_json::to_string_pretty` と同じ 2 スペース）。
const VERSION_LINE_INDENT: &str = "  ";
/// Windows の改行コード。
const CRLF: &str = "\r\n";
/// Unix の改行コード（`serde_json::to_string_pretty` が使うもの）。
const LF: &str = "\n";

/// 正準の書き手が無い形式を `Value` から書き直したときの断り。
///
/// 欄の並びが `serde_json` の既定に従うため、元ファイルの整形は保たれない。
/// 差分を見る人に「全行が変わっているのは変換のせいではない」と伝える。
const VALUE_REWRITE_NOTE: &str =
    "この形式はランタイムに保存側の実装が無いため、欄の並びが書き直されています";

/// 本体の型を往復したことで、変換手順以外の差分が入ったかを調べる。
///
/// 入るのは「旧欄を新欄へ読み替える `serde` の互換処理」（例: パーティクルの
/// `emit_rate` → `emit_interval`）や、既定値で省略される欄など。害はないが、
/// 差分を見る人に「この差は変換手順の分ではない」と伝えられるようにしておく。
/// 差が無ければ空文字。
fn normalization_note(migrated: &Value, rendered: &str, kind: FormatKind) -> String {
    let Ok(mut back) = serde_json::from_str::<Value>(rendered) else {
        return String::new();
    };
    // 版の欄は刻印で必ず入るので、比較の前に両方から外す。
    if let Some(obj) = back.as_object_mut() {
        obj.remove(kind.version_key());
    }
    let mut expected = migrated.clone();
    if let Some(obj) = expected.as_object_mut() {
        obj.remove(kind.version_key());
    }
    if back == expected {
        String::new()
    } else {
        "保存形式の正規化により、変換手順以外の差分も含まれます".to_string()
    }
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::path::PathBuf;

    /// 旧表記を含む最小の `.scene`（版の欄なし＝v1）。
    const LEGACY_SCENE: &str = r#"{"name":"S","actors":[{"name":"a","components":[{"name":"FX","component":{"type":"ParticleEmitterComponent","data":{"blend":"additive"}}}],"children":[]}]}"#;

    /// 旧表記を含む最小の `.actor`（版の欄なし＝v1）。
    const LEGACY_ACTOR: &str = r#"{"name":"A","components":[{"name":"UI","component":{"type":"CanvasComponent","data":{"width":1.0,"height":1.0,"gravity_mode":"screen_down"}}}],"children":[]}"#;

    /// テスト用の一時アセットルートを作る。
    fn temp_assets(tag: &str) -> PathBuf {
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let assets = std::env::temp_dir()
            .join(format!("seed_migration_upgrade_{tag}_{nanos}"))
            .join("assets");
        fs::create_dir_all(&assets).unwrap();
        assets
    }

    /// レポート行（JSON Lines）を解析して Value の配列にする。
    fn parse_lines(out: &[u8]) -> Vec<Value> {
        String::from_utf8(out.to_vec())
            .unwrap()
            .lines()
            .map(|l| serde_json::from_str::<Value>(l).expect("各行が JSON であること"))
            .collect()
    }

    /// dry-run は 1 バイトも書かず、結果だけ出すこと。
    #[test]
    fn dry_run_reports_without_writing() {
        let assets = temp_assets("dry");
        let scene = assets.join("Main.scene");
        fs::write(&scene, LEGACY_SCENE).unwrap();

        let mut out = Vec::new();
        let summary = upgrade_project(&assets, true, &mut out);

        assert_eq!(summary.total, 1);
        assert_eq!(summary.upgraded, 1);
        assert!(summary.dry_run);
        assert!(!summary.has_problem());
        // ファイルは元のまま
        assert_eq!(fs::read_to_string(&scene).unwrap(), LEGACY_SCENE);
        // バックアップも作られていない
        assert!(!safe_write::backup_dir_for(&scene).unwrap().exists());

        let lines = parse_lines(&out);
        assert_eq!(lines.len(), 2, "1 件 + 集計行");
        assert_eq!(lines[0]["status"], Value::from("upgraded"));
        assert_eq!(lines[0]["from"], Value::from(1));
        assert_eq!(lines[0]["to"], Value::from(2));
        assert_eq!(lines[1]["kind"], Value::from("summary"));

        fs::remove_dir_all(assets.parent().unwrap()).ok();
    }

    /// 実行すると古いファイルだけが書き換わり、旧版がバックアップに残ること。
    /// 2 回目は全件 `up_to_date` になること（冪等）。
    #[test]
    fn upgrades_old_files_keeps_backup_and_is_idempotent() {
        let assets = temp_assets("run");
        let scene = assets.join("Main.scene");
        let actor = assets.join("prefabs/A.actor");
        fs::create_dir_all(actor.parent().unwrap()).unwrap();
        fs::write(&scene, LEGACY_SCENE).unwrap();
        fs::write(&actor, LEGACY_ACTOR).unwrap();

        // 既に現行版のファイル（書き換わらないことの確認用）
        let current = assets.join("Already.scene");
        let current_text = crate::engine::core::migration::to_stamped_pretty_json(
            FormatKind::Scene,
            &serde_json::json!({ "name": "C", "actors": [] })
                .as_object()
                .unwrap()
                .clone(),
        )
        .unwrap();
        fs::write(&current, &current_text).unwrap();

        let mut out = Vec::new();
        let summary = upgrade_project(&assets, false, &mut out);
        assert_eq!(summary.total, 3);
        assert_eq!(summary.upgraded, 2);
        assert_eq!(summary.up_to_date, 1);
        assert!(!summary.has_problem());

        // 変換後の中身: 版が刻まれ、旧表記が直っている
        let scene_now: Value = serde_json::from_str(&fs::read_to_string(&scene).unwrap()).unwrap();
        assert_eq!(
            scene_now["format_version"],
            Value::from(FormatKind::Scene.current_version())
        );
        assert_eq!(
            scene_now["actors"][0]["components"][0]["component"]["data"]["blend"],
            Value::from("add")
        );
        let actor_now: Value = serde_json::from_str(&fs::read_to_string(&actor).unwrap()).unwrap();
        assert_eq!(
            actor_now["components"][0]["component"]["data"]["gravity_mode"],
            Value::from("world_down")
        );
        // 版の欄は先頭に来ること（差分が読みやすいように）
        let scene_text = fs::read_to_string(&scene).unwrap();
        assert!(
            scene_text.lines().nth(1).unwrap_or_default().contains("format_version"),
            "版の欄が先頭にない:\n{scene_text}"
        );

        // 現行版のファイルは 1 バイトも変わっていない
        assert_eq!(fs::read_to_string(&current).unwrap(), current_text);

        // 旧版がバックアップに残っている
        let backup_dir = safe_write::backup_dir_for(&scene).unwrap();
        let backups: Vec<String> = fs::read_dir(&backup_dir)
            .expect("バックアップフォルダが作られているはず")
            .flatten()
            .map(|e| e.file_name().to_string_lossy().to_string())
            .filter(|n| safe_write::is_backup_name(n, "Main", ".scene"))
            .collect();
        assert_eq!(backups.len(), 1, "旧版のバックアップが 1 件あるはず");
        assert_eq!(
            fs::read_to_string(backup_dir.join(&backups[0])).unwrap(),
            LEGACY_SCENE
        );

        // 2 回目は全件 up_to_date（冪等）
        let mut out2 = Vec::new();
        let summary2 = upgrade_project(&assets, false, &mut out2);
        assert_eq!(summary2.total, 3);
        assert_eq!(summary2.up_to_date, 3);
        assert_eq!(summary2.upgraded, 0);

        fs::remove_dir_all(assets.parent().unwrap()).ok();
    }

    /// 未来版・壊れたファイルは個別に報告され、全体は止まらず、終了コードは非 0 になること。
    #[test]
    fn reports_future_version_and_broken_files_without_stopping() {
        let assets = temp_assets("problem");
        let future = FormatKind::Scene.current_version() + 1;
        fs::write(
            assets.join("Future.scene"),
            format!(r#"{{"format_version":{future},"name":"F","actors":[]}}"#),
        )
        .unwrap();
        fs::write(assets.join("Broken.scene"), "{ not json").unwrap();
        fs::write(assets.join("Old.scene"), LEGACY_SCENE).unwrap();

        let mut out = Vec::new();
        let summary = upgrade_project(&assets, false, &mut out);

        assert_eq!(summary.total, 3);
        assert_eq!(summary.upgraded, 1, "壊れたファイルがあっても他は処理する");
        assert_eq!(summary.future_version, 1);
        assert_eq!(summary.failed, 1);
        assert!(summary.has_problem(), "終了コードが非 0 になること");

        let lines = parse_lines(&out);
        let future_line = lines
            .iter()
            .find(|l| l["status"] == Value::from("future_version"))
            .expect("未来版の行");
        assert!(
            future_line["message"]
                .as_str()
                .unwrap_or_default()
                .contains("新しいバージョンのエンジン"),
            "{future_line}"
        );

        fs::remove_dir_all(assets.parent().unwrap()).ok();
    }

    /// 変換段が中身を 1 つも変えないファイルは、**版の 1 行だけ**が増えること。
    ///
    /// 全行を書き直すと（既定値の明示化・入れ子の並び替えで）数千行の差分になり、
    /// 一括アップグレードの差分が読めなくなる。実データ 58 本のほぼ全部がこの経路を通る。
    #[test]
    fn version_only_upgrade_adds_exactly_one_line() {
        let assets = temp_assets("minimal");
        // 旧 enum 表記を含まない＝変換段が何も直さない `.actor`
        let original = "{\n  \"name\": \"Plain\",\n  \"components\": [],\n  \"children\": []\n}";
        let actor = assets.join("Plain.actor");
        fs::write(&actor, original).unwrap();

        let mut out = Vec::new();
        let summary = upgrade_project(&assets, false, &mut out);
        assert_eq!(summary.upgraded, 1);

        let after = fs::read_to_string(&actor).unwrap();
        let expected = format!(
            "{{\n  \"format_version\": {},\n  \"name\": \"Plain\",\n  \"components\": [],\n  \"children\": []\n}}",
            FormatKind::Actor.current_version()
        );
        assert_eq!(after, expected, "版の行以外が書き換わっている");

        // 読み戻せること（差し込んだ JSON が壊れていないこと）
        let back: Value = serde_json::from_str(&after).unwrap();
        assert_eq!(
            back["format_version"],
            Value::from(FormatKind::Actor.current_version())
        );

        fs::remove_dir_all(assets.parent().unwrap()).ok();
    }

    /// 実変換を持たない形式（現行版 1）でも、版の欄が無ければ 1 行だけ刻むこと。
    ///
    /// 「欄が無い＝v1」は暗黙の規約なので、ここで明示しておくと次に版を上げたときに
    /// 規約へ頼らずに済む。差分はその 1 行だけであること。
    #[test]
    fn stamps_version_only_formats_that_have_no_version_field() {
        let assets = temp_assets("stamp_only");
        fs::create_dir_all(assets.join("terrain")).unwrap();

        // 版の欄を持たない `.anim` と `terrain/layers.json`
        let anim = assets.join("Swim.anim");
        let anim_text = "{\n  \"name\": \"Swim\",\n  \"duration\": 1.0,\n  \"tracks\": []\n}";
        fs::write(&anim, anim_text).unwrap();
        let layers = assets.join("terrain/layers.json");
        let layers_text = "{\n  \"layers\": [\n    { \"name\": \"Grass\" }\n  ]\n}";
        fs::write(&layers, layers_text).unwrap();

        let mut out = Vec::new();
        let summary = upgrade_project(&assets, false, &mut out);
        assert_eq!(summary.total, 2);
        assert_eq!(summary.upgraded, 2, "版の欄が無いファイルは刻む対象");

        let expected_anim = format!(
            "{{\n  \"{}\": {},\n  \"name\": \"Swim\",\n  \"duration\": 1.0,\n  \"tracks\": []\n}}",
            FormatKind::Anim.version_key(),
            FormatKind::Anim.current_version()
        );
        assert_eq!(fs::read_to_string(&anim).unwrap(), expected_anim);
        let after_layers = fs::read_to_string(&layers).unwrap();
        assert!(
            after_layers.contains(&format!("\"{}\": 1", FormatKind::TerrainLayers.version_key())),
            "{after_layers}"
        );
        assert!(after_layers.contains("\"Grass\""), "中身が失われている: {after_layers}");

        // 2 回目は全件 up_to_date（刻印済みなので触らない）
        let mut out2 = Vec::new();
        let summary2 = upgrade_project(&assets, false, &mut out2);
        assert_eq!(summary2.up_to_date, 2);
        assert_eq!(summary2.upgraded, 0);

        fs::remove_dir_all(assets.parent().unwrap()).ok();
    }

    /// `.inputmap` は既存の版番号（`version`）を尊重したまま v1 → v2 が走ること。
    ///
    /// 中身が変わる形式なので、版の 1 行差し込みではなく書き直しになる。
    #[test]
    fn upgrades_inputmap_using_its_own_version_key() {
        let assets = temp_assets("inputmap");
        let map = assets.join("Game.inputmap");
        fs::write(
            &map,
            r#"{"actions":[{"name":"Steer","value_type":1,
                "bindings":[{"platform":"PC","input_type":"WASD","value":"Horizontal"}]}]}"#,
        )
        .unwrap();

        let mut out = Vec::new();
        let summary = upgrade_project(&assets, false, &mut out);
        assert_eq!(summary.upgraded, 1);

        let after: Value = serde_json::from_str(&fs::read_to_string(&map).unwrap()).unwrap();
        assert_eq!(
            after[FormatKind::InputMap.version_key()],
            Value::from(FormatKind::InputMap.current_version()),
            "版の欄は version（format_version ではない）"
        );
        assert!(
            after.get("format_version").is_none(),
            "別の欄名で刻んではいけない: {after}"
        );
        assert_eq!(after["actions"][0]["positive"][0]["value"], Value::from("D"));

        // 2 回目は up_to_date（冪等）
        let mut out2 = Vec::new();
        assert_eq!(upgrade_project(&assets, false, &mut out2).up_to_date, 1);

        fs::remove_dir_all(assets.parent().unwrap()).ok();
    }

    /// `.actor` を書き換えたあと、シーン側の `prefab_hash` が新しい値へ貼り直されること。
    ///
    /// 貼り直すのは「アップグレード前のファイルと同期していた」インスタンスだけで、
    /// もともと古かったインスタンスは古いまま残ること（本物の更新通知を消さない）。
    #[test]
    fn restamps_prefab_hash_only_for_instances_that_were_in_sync() {
        use crate::engine::core::app_base::prefab_hash::content_hash;

        let assets = temp_assets("rehash");
        fs::create_dir_all(assets.join("prefabs")).unwrap();

        // 版の欄を持たない `.actor`（アップグレードで 1 行増える）
        let actor = assets.join("prefabs/Fish.actor");
        let actor_text = "{\n  \"name\": \"Fish\",\n  \"components\": [],\n  \"children\": []\n}";
        fs::write(&actor, actor_text).unwrap();
        let old_hash = content_hash(actor_text);
        let stale_hash = "0123456789abcdef"; // 取り込み直していないインスタンスの値

        // 同期していたインスタンス 2 つ ＋ もともと古いインスタンス 1 つ
        let scene = assets.join("Main.scene");
        let scene_text = format!(
            concat!(
                "{{\n",
                "  \"name\": \"Main\",\n",
                "  \"actors\": [\n",
                "    {{ \"name\": \"a\", \"components\": [], \"children\": [],",
                " \"prefab_source\": \"assets://prefabs/Fish.actor\", \"prefab_hash\": \"{0}\" }},\n",
                "    {{ \"name\": \"b\", \"components\": [], \"children\": [],",
                " \"prefab_source\": \"assets://prefabs/Fish.actor\", \"prefab_hash\": \"{0}\" }},\n",
                "    {{ \"name\": \"c\", \"components\": [], \"children\": [],",
                " \"prefab_source\": \"assets://prefabs/Fish.actor\", \"prefab_hash\": \"{1}\" }}\n",
                "  ]\n",
                "}}"
            ),
            old_hash, stale_hash
        );
        fs::write(&scene, &scene_text).unwrap();

        let mut out = Vec::new();
        let summary = upgrade_project(&assets, false, &mut out);
        assert_eq!(summary.upgraded, 2, "シーンとアクタの両方が刻まれる");
        assert_eq!(summary.prefab_hash_scenes, 1);
        assert_eq!(summary.prefab_hash_updated, 2, "同期していた 2 件だけ");

        // 新しいハッシュ＝書き換え後のファイルの内容ハッシュ
        let new_hash = content_hash(&fs::read_to_string(&actor).unwrap());
        let after = fs::read_to_string(&scene).unwrap();
        assert_eq!(
            after.matches(&new_hash).count(),
            2,
            "同期していたインスタンスが貼り直されていない:\n{after}"
        );
        assert!(
            after.contains(stale_hash),
            "もともと古いインスタンスまで貼り直している:\n{after}"
        );
        assert!(!after.contains(&old_hash), "旧ハッシュが残っている:\n{after}");

        // レポートに貼り直し行が出ていること
        let lines = parse_lines(&out);
        let rehash = lines
            .iter()
            .find(|l| l["kind"] == Value::from("prefab_hash"))
            .expect("貼り直しの行");
        assert_eq!(rehash["updated"], Value::from(2));

        fs::remove_dir_all(assets.parent().unwrap()).ok();
    }

    /// dry-run では `prefab_hash` の貼り直しも 1 バイトも書かないこと。
    #[test]
    fn prefab_hash_restamp_respects_dry_run() {
        use crate::engine::core::app_base::prefab_hash::content_hash;

        let assets = temp_assets("rehash_dry");
        fs::create_dir_all(assets.join("prefabs")).unwrap();
        let actor = assets.join("prefabs/Fish.actor");
        let actor_text = "{\n  \"name\": \"Fish\",\n  \"components\": [],\n  \"children\": []\n}";
        fs::write(&actor, actor_text).unwrap();

        let scene = assets.join("Main.scene");
        let scene_text = format!(
            "{{\n  \"name\": \"Main\",\n  \"actors\": [\n    {{ \"name\": \"a\", \"components\": [], \"children\": [], \"prefab_source\": \"assets://prefabs/Fish.actor\", \"prefab_hash\": \"{}\" }}\n  ]\n}}",
            content_hash(actor_text)
        );
        fs::write(&scene, &scene_text).unwrap();

        let mut out = Vec::new();
        let summary = upgrade_project(&assets, true, &mut out);
        assert!(summary.dry_run);
        // dry-run では `.actor` を書いていないので貼り直しの対象も出ない
        assert_eq!(summary.prefab_hash_updated, 0);
        assert_eq!(fs::read_to_string(&actor).unwrap(), actor_text);
        assert_eq!(fs::read_to_string(&scene).unwrap(), scene_text);

        fs::remove_dir_all(assets.parent().unwrap()).ok();
    }

    /// エンジンが読めないファイルは書き換えず `failed` になること（安全弁）。
    #[test]
    fn unreadable_content_is_reported_without_writing() {
        let assets = temp_assets("unreadable");
        // `.sprite_mesh` として整合しない中身（頂点が空）
        let mesh = assets.join("Broken.sprite_mesh");
        let original = r#"{"vertices":[],"uvs":[],"triangles":[],"bones":[],"weights":[]}"#;
        fs::write(&mesh, original).unwrap();

        let mut out = Vec::new();
        let summary = upgrade_project(&assets, false, &mut out);
        assert_eq!(summary.failed, 1);
        assert!(summary.has_problem());
        assert_eq!(
            fs::read_to_string(&mesh).unwrap(),
            original,
            "読めないファイルを書き換えている"
        );

        fs::remove_dir_all(assets.parent().unwrap()).ok();
    }

    /// 版の欄を明示的に持つ古いファイルには差し込まないこと（欄の二重定義を避ける）。
    #[test]
    fn does_not_splice_when_a_version_field_already_exists() {
        let text = "{\n  \"format_version\": 1,\n  \"name\": \"X\"\n}";
        let parsed: Value = serde_json::from_str(text).unwrap();
        assert!(splice_version_line(text, &parsed, FormatKind::Actor).is_none());

        // 空オブジェクトも対象外（末尾カンマになるため）
        let empty = "{}";
        let parsed: Value = serde_json::from_str(empty).unwrap();
        assert!(splice_version_line(empty, &parsed, FormatKind::Actor).is_none());
    }

    /// CRLF で書かれたファイルへ差し込むと、差し込んだ行も CRLF になること。
    ///
    /// LF を混ぜると「1 行目が変わった」という無関係な差分が出て、
    /// ファイル内の改行も不揃いになる（実データ 14 本がこの経路を通った）。
    #[test]
    fn spliced_line_uses_the_files_own_line_ending() {
        let crlf = "{\r\n  \"name\": \"X\",\r\n  \"components\": []\r\n}";
        let parsed: Value = serde_json::from_str(crlf).unwrap();
        let out = splice_version_line(crlf, &parsed, FormatKind::Actor).expect("差し込めること");
        assert!(
            out.starts_with("{\r\n  \"format_version\": 2,\r\n  \"name\""),
            "CRLF が保たれていない: {out:?}"
        );
        // 1 行目以降の元テキストがそのまま残っていること
        assert_eq!(out.matches("\r\n").count(), 4);
        assert!(!out.contains("\n\n"), "LF が混ざっている: {out:?}");

        // LF のファイルは従来どおり LF
        let lf = "{\n  \"name\": \"X\"\n}";
        let parsed: Value = serde_json::from_str(lf).unwrap();
        let out = splice_version_line(lf, &parsed, FormatKind::Actor).unwrap();
        assert!(!out.contains('\r'), "CR が混ざっている: {out:?}");

        // 改行が 1 つも無い 1 行 JSON は LF を使う
        let one_line = r#"{"name":"X"}"#;
        let parsed: Value = serde_json::from_str(one_line).unwrap();
        let out = splice_version_line(one_line, &parsed, FormatKind::Actor).unwrap();
        assert_eq!(out, "{\n  \"format_version\": 2,\"name\":\"X\"}");
    }

    /// 引数の解釈: `=` 区切りと空白区切りの両方を受け付けること。
    #[test]
    fn parses_project_argument_in_both_forms() {
        let eq = vec![
            "SEED.exe".to_string(),
            format!("{UPGRADE_FLAG}=D:/proj"),
            DRY_RUN_FLAG.to_string(),
        ];
        assert_eq!(parse_project_arg(&eq).as_deref(), Some("D:/proj"));

        let spaced = vec![
            "SEED.exe".to_string(),
            UPGRADE_FLAG.to_string(),
            "D:/proj".to_string(),
        ];
        assert_eq!(parse_project_arg(&spaced).as_deref(), Some("D:/proj"));

        // 値が無い（次がフラグ）→ 空文字（呼び出し側が usage エラーにする）
        let missing = vec![
            "SEED.exe".to_string(),
            UPGRADE_FLAG.to_string(),
            DRY_RUN_FLAG.to_string(),
        ];
        assert_eq!(parse_project_arg(&missing).as_deref(), Some(""));

        // 指定なし
        let none = vec!["SEED.exe".to_string(), "--mode=play".to_string()];
        assert_eq!(parse_project_arg(&none), None);
    }
}
