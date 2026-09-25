// ============================================================
//  hot_reload/wire.rs — 実行中の差し替えの IPC の書式（命令の解釈・応答の組み立て。純粋な処理）
//
//  【命令】（エディタ／SeedAndroid → ランタイム。1 行 1 命令。PC の名前付きパイプと Android の TCP で同じ）
//    RELOAD_SCENE                … 今のシーンを読み直す（無条件）
//    RELOAD_SCENE:<相対パス>      … 今のシーンがそのパスのときだけ読み直す（違えば RELOAD_SKIPPED で今のシーンを返す）
//    RELOAD_ASSET:<相対パス>      … そのアセットを差し替える（キャッシュを捨て、種類に応じて取り込み直す）
//  <相対パス> はアセットルートからの相対パス（区切り / か \）か assets://<相対パス>。絶対パス・.. は受け付けない
//  （アプリのアセットの外を指せないようにする。受け付けないときも RELOAD_FAILED で理由を返す＝相手を待たせない）。
//
//  【応答】（ランタイム → エディタ。欄の区切りは | ＝ Windows のファイル名に使えない文字なのでパスと混ざらない）
//    RELOAD_DONE:<宛先>|<所要ミリ秒>|<詳細>   … 適用した（詳細: 読み直したシーン・inplace・scene 等）
//    RELOAD_SKIPPED:<宛先>|<理由>             … 適用しなかった（今のシーンが違う等。失敗ではない）
//    RELOAD_FAILED:<宛先>|<理由>              … 失敗した
//  <宛先> は命令ごとに決まる文字列で、エディタは送った命令の宛先と照合して応答を待つ:
//    RELOAD_SCENE → scene ／ RELOAD_SCENE:<p> → scene:<p> ／ RELOAD_ASSET:<p> → asset:<p>（<p> は正規化した相対パス）
//  書式の正典はここ。エディタ側は editor/src/Ipc/RuntimeIpcCommands.cs（値を変えるときは両方を直す）。
// ============================================================

use crate::engine::asset_fs::ASSETS_SCHEME;

/// 今のシーンを読み直す命令（引数なし）。
pub const RELOAD_SCENE_COMMAND: &str = "RELOAD_SCENE";

/// 今のシーンが指定のシーンのときだけ読み直す命令の接頭辞（RELOAD_SCENE:<相対パス>）。
pub const RELOAD_SCENE_PREFIX: &str = "RELOAD_SCENE:";

/// アセットを差し替える命令の接頭辞（RELOAD_ASSET:<相対パス>）。
pub const RELOAD_ASSET_PREFIX: &str = "RELOAD_ASSET:";

/// 適用した応答の接頭辞。
pub const RELOAD_DONE_PREFIX: &str = "RELOAD_DONE:";

/// 適用しなかった（失敗ではない）応答の接頭辞。
pub const RELOAD_SKIPPED_PREFIX: &str = "RELOAD_SKIPPED:";

/// 失敗した応答の接頭辞。
pub const RELOAD_FAILED_PREFIX: &str = "RELOAD_FAILED:";

/// 応答の欄の区切り（Windows のファイル名に使えない文字。パスの中に現れない）。
pub const FIELD_SEPARATOR: char = '|';

/// 無条件のシーンの読み直しの宛先。
pub const SCENE_TARGET: &str = "scene";

/// 条件付きのシーンの読み直しの宛先の接頭辞（scene:<相対パス>）。
pub const SCENE_TARGET_PREFIX: &str = "scene:";

/// アセットの差し替えの宛先の接頭辞（asset:<相対パス>）。
pub const ASSET_TARGET_PREFIX: &str = "asset:";

/// 応答の本文に入れられない文字を置き換える文字（区切り・改行を潰して 1 行・欄の数を保つ）。
const SANITIZED_CHAR: char = ' ';

/// 相対パスの区切り（仮想パス・pak のエントリと同じ）。
const PATH_SEPARATOR: char = '/';

/// Windows の区切り（受け取ったら / にする）。
const BACKSLASH: char = '\\';

/// 親フォルダ（アセットルートの外へ出るので受け付けない）。
const PARENT_SEGMENT: &str = "..";

/// 今のフォルダ（読み飛ばす）。
const CURRENT_SEGMENT: &str = ".";

/// ドライブ名の区切り（相対パスには現れない。絶対パスの取り違えを弾く）。
const DRIVE_SEPARATOR: char = ':';

/// 実行中の差し替えの要求 1 件（IPC の 1 行を解釈したもの）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum HotReloadRequest {
    /// シーンの読み直し。`only_if` が Some なら、今のシーンがその相対パスのときだけ読み直す。
    Scene { only_if: Option<String> },
    /// アセットの差し替え（`relative` は正規化したアセットルートからの相対パス）。
    Asset { relative: String },
    /// 書式の誤り（`target` の宛先へ `reason` を RELOAD_FAILED で返す。相手を応答待ちのまま待たせない）。
    Invalid { target: String, reason: String },
}

impl HotReloadRequest {
    /// 応答の宛先（送った命令ごとに決まる文字列。エディタはこれで応答を照合する）。
    pub fn target(&self) -> String {
        match self {
            Self::Scene { only_if: None } => SCENE_TARGET.to_string(),
            Self::Scene { only_if: Some(relative) } => format!("{SCENE_TARGET_PREFIX}{relative}"),
            Self::Asset { relative } => format!("{ASSET_TARGET_PREFIX}{relative}"),
            Self::Invalid { target, .. } => target.clone(),
        }
    }
}

/// 差し替えの命令の行か（RELOAD_SCRIPTS は従来の命令なので含まない）。
///
/// # 引数
/// * `line` - 前後の空白を落とした 1 行
pub fn is_reload_line(line: &str) -> bool {
    line == RELOAD_SCENE_COMMAND || line.starts_with(RELOAD_SCENE_PREFIX) || line.starts_with(RELOAD_ASSET_PREFIX)
}

/// 差し替えの命令を解釈する（差し替えの命令でなければ None）。
///
/// パスが受け付けられないときは `Invalid`（理由付き）を返す＝応答で知らせる。
///
/// # 引数
/// * `line` - 前後の空白を落とした 1 行
pub fn parse(line: &str) -> Option<HotReloadRequest> {
    if line == RELOAD_SCENE_COMMAND {
        return Some(HotReloadRequest::Scene { only_if: None });
    }
    if let Some(raw) = line.strip_prefix(RELOAD_SCENE_PREFIX) {
        return Some(match normalize_relative(raw) {
            Ok(relative) => HotReloadRequest::Scene { only_if: Some(relative) },
            Err(reason) => HotReloadRequest::Invalid { target: format!("{SCENE_TARGET_PREFIX}{}", raw.trim()), reason },
        });
    }
    if let Some(raw) = line.strip_prefix(RELOAD_ASSET_PREFIX) {
        return Some(match normalize_relative(raw) {
            Ok(relative) => HotReloadRequest::Asset { relative },
            Err(reason) => HotReloadRequest::Invalid { target: format!("{ASSET_TARGET_PREFIX}{}", raw.trim()), reason },
        });
    }
    None
}

/// 受け取ったパスを、アセットルートからの相対パス（区切り /・`.` を除いたもの）にする【純関数】。
///
/// `assets://` は外す。空・絶対パス（先頭の / か \ ・ドライブ名）・`..` を含むものは受け付けない
/// （アプリのアセットの外を指させない）。大文字小文字はそのまま（照合は asset_key.rs が大文字小文字を問わずに行う）。
///
/// # 引数
/// * `raw` - 受け取ったパス
///
/// # 戻り値
/// 正規化した相対パス。受け付けないときは理由。
pub fn normalize_relative(raw: &str) -> Result<String, String> {
    let trimmed = raw.trim();
    let without_scheme = trimmed.strip_prefix(ASSETS_SCHEME).unwrap_or(trimmed);
    let unified = without_scheme.replace(BACKSLASH, &PATH_SEPARATOR.to_string());
    if unified.is_empty() {
        return Err("パスが空です（アセットルートからの相対パスを指定してください）".to_string());
    }
    if unified.starts_with(PATH_SEPARATOR) || unified.contains(DRIVE_SEPARATOR) {
        return Err(format!("絶対パスは使えません（アセットルートからの相対パスを指定してください）: {trimmed}"));
    }
    let mut segments: Vec<&str> = Vec::new();
    for segment in unified.split(PATH_SEPARATOR) {
        match segment {
            "" | CURRENT_SEGMENT => continue,
            PARENT_SEGMENT => {
                return Err(format!("{PARENT_SEGMENT} を含むパスは使えません（アセットルートの外を指せません）: {trimmed}"));
            }
            other => segments.push(other),
        }
    }
    if segments.is_empty() {
        return Err(format!("ファイルを指していません: {trimmed}"));
    }
    Ok(segments.join(&PATH_SEPARATOR.to_string()))
}

/// 適用した応答の行（RELOAD_DONE:<宛先>|<所要ミリ秒>|<詳細>）。
///
/// # 引数
/// * `target`     - 宛先（`HotReloadRequest::target`）
/// * `elapsed_ms` - 所要時間（ミリ秒）
/// * `detail`     - 詳細（読み直したシーン・反映のしかた）
pub fn done_line(target: &str, elapsed_ms: f64, detail: &str) -> String {
    format!(
        "{RELOAD_DONE_PREFIX}{}{FIELD_SEPARATOR}{elapsed_ms:.1}{FIELD_SEPARATOR}{}",
        sanitize(target),
        sanitize(detail)
    )
}

/// 適用しなかった応答の行（RELOAD_SKIPPED:<宛先>|<理由>）。
pub fn skipped_line(target: &str, reason: &str) -> String {
    format!("{RELOAD_SKIPPED_PREFIX}{}{FIELD_SEPARATOR}{}", sanitize(target), sanitize(reason))
}

/// 失敗した応答の行（RELOAD_FAILED:<宛先>|<理由>）。
pub fn failed_line(target: &str, reason: &str) -> String {
    format!("{RELOAD_FAILED_PREFIX}{}{FIELD_SEPARATOR}{}", sanitize(target), sanitize(reason))
}

/// 応答の欄に入れる文字列から、区切りと改行を空白へ置き換える（1 行・欄の数を保つ）。
fn sanitize(text: &str) -> String {
    text.chars()
        .map(|c| if c == FIELD_SEPARATOR || c == '\n' || c == '\r' { SANITIZED_CHAR } else { c })
        .collect()
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 引数なしの RELOAD_SCENE は無条件のシーンの読み直し（宛先 scene）。
    #[test]
    fn parses_unconditional_scene_reload() {
        let request = parse("RELOAD_SCENE").expect("差し替えの命令");
        assert_eq!(request, HotReloadRequest::Scene { only_if: None });
        assert_eq!(request.target(), "scene");
    }

    /// RELOAD_SCENE:<パス> は条件付き。assets:// と \ は正規化され、宛先は正規化したパスになる。
    #[test]
    fn parses_conditional_scene_reload() {
        let request = parse(r"RELOAD_SCENE:assets://scenes\Stage 2.scene").expect("差し替えの命令");
        assert_eq!(request, HotReloadRequest::Scene { only_if: Some("scenes/Stage 2.scene".to_string()) });
        assert_eq!(request.target(), "scene:scenes/Stage 2.scene");
    }

    /// RELOAD_ASSET:<パス>（日本語・./・重複した区切りを含む）を正規化する。
    #[test]
    fn parses_asset_reload() {
        let request = parse("RELOAD_ASSET:./textures//日本語 画像.png").expect("差し替えの命令");
        assert_eq!(request, HotReloadRequest::Asset { relative: "textures/日本語 画像.png".to_string() });
        assert_eq!(request.target(), "asset:textures/日本語 画像.png");
    }

    /// アセットの外を指すパス・空のパスは Invalid（応答で理由を返す。相手を待たせない）。
    #[test]
    fn rejects_paths_outside_assets() {
        for line in [
            "RELOAD_ASSET:../secret.txt",
            "RELOAD_ASSET:a/../../b.png",
            r"RELOAD_ASSET:C:\Users\x\a.png",
            "RELOAD_ASSET:/data/user/0/x/a.png",
            "RELOAD_ASSET:",
            "RELOAD_ASSET:./",
            "RELOAD_SCENE:../x.scene",
        ] {
            match parse(line) {
                Some(HotReloadRequest::Invalid { target, reason }) => {
                    assert!(!reason.is_empty(), "{line}: 理由が空");
                    assert!(target.starts_with("asset:") || target.starts_with("scene:"), "{line}: 宛先 {target}");
                }
                other => panic!("{line}: Invalid を期待したが {other:?}"),
            }
        }
    }

    /// 差し替えの命令ではない行（RELOAD_SCRIPTS を含む）は None・is_reload_line は false。
    #[test]
    fn other_lines_are_not_reload_commands() {
        for line in ["RELOAD_SCRIPTS", "RELOAD_SCENES", "PAUSE", "LOAD_SCENE:a.scene", "RELOAD_ASSETS:x"] {
            assert!(parse(line).is_none(), "{line}");
            assert!(!is_reload_line(line), "{line}");
        }
        assert!(is_reload_line("RELOAD_SCENE"));
        assert!(is_reload_line("RELOAD_SCENE:a.scene"));
        assert!(is_reload_line("RELOAD_ASSET:a.png"));
    }

    /// 応答の行の書式（欄は | 区切り・所要時間は小数 1 桁・本文の | と改行は空白へ）。
    #[test]
    fn reply_lines_have_stable_format() {
        assert_eq!(done_line("asset:a.png", 12.345, "inplace"), "RELOAD_DONE:asset:a.png|12.3|inplace");
        assert_eq!(skipped_line("scene:b.scene", "今のシーンは c.scene"), "RELOAD_SKIPPED:scene:b.scene|今のシーンは c.scene");
        assert_eq!(failed_line("scene", "壊れた|JSON\n2 行目"), "RELOAD_FAILED:scene|壊れた JSON 2 行目");
    }
}
