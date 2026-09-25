// ============================================================
//  scene_snapshot/wire.rs — シーンの写しと写しの閲覧の IPC の書式（命令の解釈・応答の組み立て。純粋な処理）
//
//  【命令】（エディタ／SeedAndroid → ランタイム。1 行 1 命令。PC の名前付きパイプと Android の TCP で同じ）
//    SNAPSHOT_SCENE:<ランタイム側の絶対パス（.scene）>
//        … いまの世界（スクリプトが生成したアクタを含む）を既存のシーン形式で書き出す（読み込み中のシーンのパスは変えない・
//          地形のファイルは書かない）。Play 中の端末のアプリで使う（Edit でも動く）
//    SNAPSHOT_VIEW_BEGIN:<PC の写しのパス>[|file_ok]
//        … エディタの編集用ランタイム（Edit のときだけ）: 編集中のシーンをメモリへ退避して写しを読み込み、閲覧専用にする。
//          file_ok は「メモリへ退避できなければ、戻すときに保存済みのファイルを読み直してよい」（未保存の変更が無いときだけ）
//    SNAPSHOT_VIEW_END
//        … 退避した編集中のシーンへ戻す（表示していなければ何もしない）
//
//  【応答】（ランタイム → エディタ。欄の区切りは | ＝ Windows のファイル名に使えない文字なのでパスと混ざらない）
//    SNAPSHOT_DONE:<パス>|actors=<書いたアクタ数（子を含む）>|skipped=<飛ばしたアクタ数>|ms=<所要ミリ秒>|cam=<px>,<py>,<pz>,<ex>,<ey>,<ez>
//        cam はメインカメラの位置（ワールド）と向き（Transform と同じ YXZ オイラー角・度）。メインカメラが無ければ cam=none
//    SNAPSHOT_FAILED:<パス>|<理由>
//    SNAPSHOT_VIEW_READY:<パス>|actors=<読み込んだアクタ数>|ms=<所要ミリ秒>
//    SNAPSHOT_VIEW_FAILED:<パス>|<段階: mode / load / stash>|<理由>   … 何も変えていない
//    SNAPSHOT_VIEW_ENDED:<戻し方: memory / file / none / failed>|<戻したシーンのパス、または理由>|ms=<所要ミリ秒>
//    SNAPSHOT_VIEW_REFUSED:<命令の名前>   … 写しの表示中に編集・保存の命令を捨てた（保存は加えて SAVE_ERROR:snapshot_view）
//  書式の正典はここ。エディタ側は editor/src/SceneSnapshot/SceneSnapshotWire.cs（値を変えるときは両方を直す）。
// ============================================================

use std::path::Path;

/// 写しを書き出す命令の接頭辞。
pub const SNAPSHOT_SCENE_PREFIX: &str = "SNAPSHOT_SCENE:";

/// 写しを書き出した応答の接頭辞。
pub const SNAPSHOT_DONE_PREFIX: &str = "SNAPSHOT_DONE:";

/// 写しを書き出せなかった応答の接頭辞。
pub const SNAPSHOT_FAILED_PREFIX: &str = "SNAPSHOT_FAILED:";

/// 写しの表示を始める命令の接頭辞。
pub const VIEW_BEGIN_PREFIX: &str = "SNAPSHOT_VIEW_BEGIN:";

/// 写しの表示をやめる命令。
pub const VIEW_END_COMMAND: &str = "SNAPSHOT_VIEW_END";

/// 写しを表示した応答の接頭辞。
pub const VIEW_READY_PREFIX: &str = "SNAPSHOT_VIEW_READY:";

/// 写しを表示できなかった応答の接頭辞。
pub const VIEW_FAILED_PREFIX: &str = "SNAPSHOT_VIEW_FAILED:";

/// 写しの表示をやめた応答の接頭辞。
pub const VIEW_ENDED_PREFIX: &str = "SNAPSHOT_VIEW_ENDED:";

/// 写しの表示中に命令を捨てた知らせの接頭辞。
pub const VIEW_REFUSED_PREFIX: &str = "SNAPSHOT_VIEW_REFUSED:";

/// 写しの表示中に保存を拒んだときの SAVE_ERROR（エディタの保存完了の待ち合わせを失敗で解くため）。
pub const SAVE_ERROR_SNAPSHOT_VIEW: &str = "SAVE_ERROR:snapshot_view";

/// 「メモリへ退避できなければファイルの読み直しで戻してよい」の印。
pub const FILE_RESTORE_OPTION: &str = "file_ok";

/// 欄の区切り（Windows のファイル名に使えない文字）。
pub const FIELD_SEPARATOR: char = '|';

/// 名前付きの欄の名前と値の区切り。
pub const KEY_VALUE_SEPARATOR: char = '=';

/// 欄: アクタの数（子を含む）。
pub const ACTORS_KEY: &str = "actors";

/// 欄: 飛ばしたアクタの数（子を含む）。
pub const SKIPPED_KEY: &str = "skipped";

/// 欄: 所要ミリ秒。
pub const MILLISECONDS_KEY: &str = "ms";

/// 欄: メインカメラの位置と向き。
pub const CAMERA_KEY: &str = "cam";

/// メインカメラが無いことを表す値。
pub const CAMERA_NONE: &str = "none";

/// カメラの数値の区切り（CAM_TRANSFORM と同じ）。
pub const CAMERA_VALUE_SEPARATOR: char = ',';

/// 写しのファイルの拡張子（ほかのファイルを上書きさせないため、これ以外は受け付けない）。
pub const SCENE_EXTENSION: &str = "scene";

/// 応答の本文に入れられない文字を置き換える文字（区切り・改行を潰して 1 行・欄の数を保つ）。
const SANITIZED_CHAR: char = ' ';

/// 所要ミリ秒の小数の桁数（応答の表示用）。
const MILLISECONDS_DECIMALS: usize = 1;

/// 写しの命令（IPC の 1 行を解釈したもの）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SnapshotCommand {
    /// いまの世界を `path` へ書き出す。
    Snapshot { path: String },
    /// 編集中のシーンを退避して `path` の写しを閲覧専用で読み込む。
    ViewBegin { path: String, allow_file_restore: bool },
    /// 退避した編集中のシーンへ戻す。
    ViewEnd,
}

/// 写しの表示を始められなかった段階。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ViewFailStage {
    /// 編集用ランタイムが Edit でない（Play 中等）。
    Mode,
    /// 写しのファイルを読めない。
    Load,
    /// 編集中のシーンをメモリへ退避できない。
    Stash,
}

impl ViewFailStage {
    /// 応答に書く名前。
    pub fn wire_name(self) -> &'static str {
        match self {
            Self::Mode => "mode",
            Self::Load => "load",
            Self::Stash => "stash",
        }
    }
}

/// 写しの表示をやめたときの戻し方。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RestoreKind {
    /// メモリへ退避した編集中のシーンへ戻した（未保存の変更も戻る）。
    Memory,
    /// 保存済みのファイルを読み直した（メモリの退避を使えなかった）。
    File,
    /// 表示していなかった。
    None,
    /// 戻せなかった（写しが残っている）。
    Failed,
}

impl RestoreKind {
    /// 応答に書く名前。
    pub fn wire_name(self) -> &'static str {
        match self {
            Self::Memory => "memory",
            Self::File => "file",
            Self::None => "none",
            Self::Failed => "failed",
        }
    }
}

/// メインカメラの位置（ワールド）と向き（YXZ オイラー角・度）。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CameraPose {
    /// 位置（ワールド）。
    pub position: [f32; 3],
    /// 向き（Transform と同じ YXZ オイラー角・度）。
    pub rotation: [f32; 3],
}

impl CameraPose {
    /// 値がすべて有限なら姿勢を作る（NaN・無限大を含むなら None＝応答では cam=none）。
    pub fn new_finite(position: [f32; 3], rotation: [f32; 3]) -> Option<Self> {
        let finite = position.iter().chain(rotation.iter()).all(|v| v.is_finite());
        finite.then_some(Self { position, rotation })
    }

    /// 応答の欄の値（px,py,pz,ex,ey,ez）。
    fn wire_value(&self) -> String {
        let [px, py, pz] = self.position;
        let [ex, ey, ez] = self.rotation;
        [px, py, pz, ex, ey, ez]
            .iter()
            .map(|v| v.to_string())
            .collect::<Vec<_>>()
            .join(&CAMERA_VALUE_SEPARATOR.to_string())
    }
}

/// 写しの命令の行か。
///
/// # 引数
/// * `line` - 前後の空白を落とした 1 行
pub fn is_snapshot_line(line: &str) -> bool {
    line.starts_with(SNAPSHOT_SCENE_PREFIX) || line.starts_with(VIEW_BEGIN_PREFIX) || line == VIEW_END_COMMAND
}

/// 写しの命令を解釈する（写しの命令でない・パスが空なら None）。
///
/// # 引数
/// * `line` - 前後の空白を落とした 1 行
pub fn parse(line: &str) -> Option<SnapshotCommand> {
    if line == VIEW_END_COMMAND {
        return Some(SnapshotCommand::ViewEnd);
    }
    if let Some(rest) = line.strip_prefix(SNAPSHOT_SCENE_PREFIX) {
        let path = rest.trim();
        return (!path.is_empty()).then(|| SnapshotCommand::Snapshot { path: path.to_string() });
    }
    if let Some(rest) = line.strip_prefix(VIEW_BEGIN_PREFIX) {
        let mut fields = rest.split(FIELD_SEPARATOR);
        let path = fields.next().unwrap_or_default().trim();
        if path.is_empty() {
            return None;
        }
        let allow_file_restore = fields.any(|field| field.trim() == FILE_RESTORE_OPTION);
        return Some(SnapshotCommand::ViewBegin { path: path.to_string(), allow_file_restore });
    }
    None
}

/// 写しの書き先として受け付けるか（絶対パスで、拡張子が .scene）。受け付けないときは理由。
///
/// 書き先は相手（エディタ・SeedAndroid）が決めるが、ほかのファイル（実行ファイル・セーブ等）を
/// 取り違えて上書きしないよう、シーンのファイルだけに絞る。
pub fn validate_output_path(path: &str) -> Result<(), String> {
    let p = Path::new(path);
    if !p.is_absolute() {
        return Err(format!("書き先は絶対パスで指定してください: {path}"));
    }
    let is_scene = p
        .extension()
        .and_then(|e| e.to_str())
        .is_some_and(|e| e.eq_ignore_ascii_case(SCENE_EXTENSION));
    if !is_scene {
        return Err(format!("書き先の拡張子は .{SCENE_EXTENSION} にしてください: {path}"));
    }
    Ok(())
}

/// 写しを書き出した応答を作る。
///
/// # 引数
/// * `path` - 書いたパス
/// * `actors` - 書いたアクタ数（子を含む）
/// * `skipped` - 飛ばしたアクタ数（子を含む）
/// * `elapsed_ms` - 所要ミリ秒
/// * `camera` - メインカメラの姿勢（無ければ None）
pub fn format_done(path: &str, actors: usize, skipped: usize, elapsed_ms: f64, camera: Option<&CameraPose>) -> String {
    let cam = camera.map(CameraPose::wire_value).unwrap_or_else(|| CAMERA_NONE.to_string());
    format!(
        "{SNAPSHOT_DONE_PREFIX}{}{sep}{ACTORS_KEY}{kv}{actors}{sep}{SKIPPED_KEY}{kv}{skipped}{sep}{MILLISECONDS_KEY}{kv}{elapsed_ms:.prec$}{sep}{CAMERA_KEY}{kv}{cam}",
        sanitize(path),
        sep = FIELD_SEPARATOR,
        kv = KEY_VALUE_SEPARATOR,
        prec = MILLISECONDS_DECIMALS,
    )
}

/// 写しを書き出せなかった応答を作る。
pub fn format_failed(path: &str, reason: &str) -> String {
    format!("{SNAPSHOT_FAILED_PREFIX}{}{FIELD_SEPARATOR}{}", sanitize(path), sanitize(reason))
}

/// 写しを表示した応答を作る。
pub fn format_view_ready(path: &str, actors: usize, elapsed_ms: f64) -> String {
    format!(
        "{VIEW_READY_PREFIX}{}{sep}{ACTORS_KEY}{kv}{actors}{sep}{MILLISECONDS_KEY}{kv}{elapsed_ms:.prec$}",
        sanitize(path),
        sep = FIELD_SEPARATOR,
        kv = KEY_VALUE_SEPARATOR,
        prec = MILLISECONDS_DECIMALS,
    )
}

/// 写しを表示できなかった応答を作る。
pub fn format_view_failed(path: &str, stage: ViewFailStage, reason: &str) -> String {
    format!(
        "{VIEW_FAILED_PREFIX}{}{FIELD_SEPARATOR}{}{FIELD_SEPARATOR}{}",
        sanitize(path),
        stage.wire_name(),
        sanitize(reason)
    )
}

/// 写しの表示をやめた応答を作る。
///
/// # 引数
/// * `kind` - 戻し方
/// * `detail` - 戻したシーンのパス（memory / file）か理由（failed）。none は空でよい
/// * `elapsed_ms` - 所要ミリ秒
pub fn format_view_ended(kind: RestoreKind, detail: &str, elapsed_ms: f64) -> String {
    format!(
        "{VIEW_ENDED_PREFIX}{}{sep}{}{sep}{MILLISECONDS_KEY}{kv}{elapsed_ms:.prec$}",
        kind.wire_name(),
        sanitize(detail),
        sep = FIELD_SEPARATOR,
        kv = KEY_VALUE_SEPARATOR,
        prec = MILLISECONDS_DECIMALS,
    )
}

/// 写しの表示中に命令を捨てた知らせを作る。
pub fn format_view_refused(command: &str) -> String {
    format!("{VIEW_REFUSED_PREFIX}{}", sanitize(command))
}

/// 応答の本文から区切り・改行を潰す（1 行・欄の数を保つ）。
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

    /// 3 種の命令を解釈できること（前後の空白は read_loop が落とすが、パスの前後の空白もここで落とす）。
    #[test]
    fn parses_commands() {
        assert_eq!(
            parse("SNAPSHOT_SCENE:/data/user/0/com.x/cache/s.scene"),
            Some(SnapshotCommand::Snapshot { path: "/data/user/0/com.x/cache/s.scene".into() })
        );
        assert_eq!(
            parse(r"SNAPSHOT_VIEW_BEGIN:C:\p\cache\android\snapshot\paused.scene"),
            Some(SnapshotCommand::ViewBegin { path: r"C:\p\cache\android\snapshot\paused.scene".into(), allow_file_restore: false })
        );
        assert_eq!(
            parse("SNAPSHOT_VIEW_BEGIN:C:/a.scene|file_ok"),
            Some(SnapshotCommand::ViewBegin { path: "C:/a.scene".into(), allow_file_restore: true })
        );
        assert_eq!(parse("SNAPSHOT_VIEW_END"), Some(SnapshotCommand::ViewEnd));
        // パスが空の命令・ほかの命令は受け付けない
        assert_eq!(parse("SNAPSHOT_SCENE:"), None);
        assert_eq!(parse("SNAPSHOT_VIEW_BEGIN: |file_ok"), None);
        assert_eq!(parse("SAVE_SCENE:C:/a.scene"), None);
        assert!(is_snapshot_line("SNAPSHOT_VIEW_END"));
        assert!(!is_snapshot_line("SNAPSHOT_VIEW_ENDX"));
    }

    /// 書き先はシーンのファイル（絶対パス・.scene）だけを受け付けること。
    #[test]
    fn validates_output_path() {
        #[cfg(windows)]
        let ok = r"C:\tmp\snap.scene";
        #[cfg(not(windows))]
        let ok = "/data/user/0/com.x/cache/snap.scene";
        assert!(validate_output_path(ok).is_ok());
        assert!(validate_output_path("snap.scene").is_err(), "相対パスは受け付けない");
        #[cfg(windows)]
        assert!(validate_output_path(r"C:\tmp\libSEED.so").is_err(), ".scene 以外は受け付けない");
        #[cfg(not(windows))]
        assert!(validate_output_path("/data/user/0/com.x/files/bin/a.dll").is_err(), ".scene 以外は受け付けない");
    }

    /// 書き出した応答の書式（欄の順・カメラの値・ミリ秒は小数 1 桁）。
    #[test]
    fn formats_done_reply_with_camera() {
        let cam = CameraPose::new_finite([1.0, 2.5, -3.0], [10.0, -20.0, 0.0]).unwrap();
        assert_eq!(
            format_done("/d/s.scene", 12, 1, 35.26, Some(&cam)),
            "SNAPSHOT_DONE:/d/s.scene|actors=12|skipped=1|ms=35.3|cam=1,2.5,-3,10,-20,0"
        );
        assert_eq!(format_done("/d/s.scene", 0, 0, 1.0, None), "SNAPSHOT_DONE:/d/s.scene|actors=0|skipped=0|ms=1.0|cam=none");
    }

    /// 有限でない値のカメラは作らない（応答は cam=none になる）。
    #[test]
    fn non_finite_camera_is_rejected() {
        assert!(CameraPose::new_finite([f32::NAN, 0.0, 0.0], [0.0; 3]).is_none());
        assert!(CameraPose::new_finite([0.0; 3], [0.0, f32::INFINITY, 0.0]).is_none());
    }

    /// 表示・失敗・戻しの応答の書式。本文の | と改行は潰して欄の数を保つ。
    #[test]
    fn formats_view_replies_and_sanitizes() {
        assert_eq!(format_view_ready("C:/a.scene", 5, 12.0), "SNAPSHOT_VIEW_READY:C:/a.scene|actors=5|ms=12.0");
        assert_eq!(
            format_view_failed("C:/a.scene", ViewFailStage::Stash, "json|error\nline2"),
            "SNAPSHOT_VIEW_FAILED:C:/a.scene|stash|json error line2"
        );
        assert_eq!(format_view_ended(RestoreKind::Memory, "C:/m.scene", 3.0), "SNAPSHOT_VIEW_ENDED:memory|C:/m.scene|ms=3.0");
        assert_eq!(format_view_ended(RestoreKind::None, "", 0.0), "SNAPSHOT_VIEW_ENDED:none||ms=0.0");
        assert_eq!(format_failed("/d/s.scene", "no|scene"), "SNAPSHOT_FAILED:/d/s.scene|no scene");
        assert_eq!(format_view_refused("SAVE_SCENE"), "SNAPSHOT_VIEW_REFUSED:SAVE_SCENE");
    }
}
