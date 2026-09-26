// ============================================================
//  platform/bridge/alarm/sound_export.rs — 目覚ましの音源の書き出し（W1-3）
//
//  【なぜ要るか】鳴動（W1-4 の RingService）は Java だけの別プロセス :seed_platform で動き、エンジンの pak・APK の中の
//  アセットを読めない（asset_fs はメインプロセスのエンジンの中にある）。そこでスクリプトが "assets://…" の音を渡したら、
//  予約を :seed_platform へ送る前に、メインプロセスがアセットのバイト列を実ファイルへ書き出し、その絶対パスを予約に入れる。
//
//  【流れ】（Android の糊 runtime/android/native/src/platform_bridge/alarm_prep.rs が alarm.schedule のたびに呼ぶ）
//    予約の JSON の sound_asset を見て:
//      空                    → sound_path = ""（既定の音）
//      "/" で始まる           → 端末のファイルの絶対パスとしてそのまま sound_path へ
//      "assets://…"・相対パス → アセットを読み、<sounds_dir>/<内容のハッシュ>.<拡張子> へ書き出して sound_path へ
//                              （同じ名前・同じ大きさのファイルがあれば書かない。名前が内容のハッシュなので同じ中身）
//    書き出せない（書き出し先が分からない・アセットが無い・書けない）→ sound_path = "" にして警告（予約そのものは通す。
//    鳴らないより既定の音で鳴るほうがよい）。sound_asset は消して送る（:seed_platform が二重に警告しないように）。
//  sounds_dir は :seed_platform の platform.paths が教える端末保護ストレージの files/seed_platform/sounds（同じアプリの UID
//  なのでメインプロセスも書ける。再起動の後・ロック解除の前でも読める）。
//  書き方は一時ファイル → sync_all → rename（→ Unix ではフォルダも sync_all）。ハッシュは FNV-1a 64bit（prefab_hash と同じ。
//  暗号の強さは要らない。衝突は大きさの比較でも見分ける）。書き出したファイルの掃除はまだしない（docs/backlog.md）。
// ============================================================

use std::fs::{self, File};
use std::io::{self, Write};
use std::path::{Path, PathBuf};

use serde_json::Value;

use crate::engine::asset_fs;
use crate::engine::core::app_base::prefab_hash::content_hash_bytes;
use crate::engine::platform::bridge::wire::{self, alarm as names};

/// 端末のファイルの絶対パスの先頭（Android のパス）。
const DEVICE_ABSOLUTE_PREFIX: &str = "/";

/// 拡張子が読めないときに付ける拡張子（中身は MediaPlayer が見て判断する）。
const FALLBACK_EXTENSION: &str = "bin";

/// 受け付ける拡張子の最大の長さ（英数字だけ。それ以外の形は FALLBACK_EXTENSION）。
const MAX_EXTENSION_LEN: usize = 8;

/// 一時ファイルの接尾辞（本体と同じフォルダ＝同じファイルシステムなので rename が原子的）。
const TEMP_SUFFIX: &str = ".tmp";

/// 音源の指定の種類。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SoundSource {
    /// 指定なし（既定の音）。
    Default,
    /// 端末のファイルの絶対パス（そのまま通す）。
    DeviceFile(String),
    /// アセット（"assets://…"。相対パスには assets:// を補った形）。
    Asset(String),
}

/// 予約の JSON を :seed_platform へ送る形にした結果。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PreparedSchedule {
    /// 送る JSON（sound_asset を消し、sound_path を入れたもの。読めない JSON は元のまま）。
    pub json: String,
    /// 入れた sound_path（空なら既定の音）。
    pub sound_path: String,
    /// 書き出せなかったときの警告（ログへ出す）。
    pub warning: Option<String>,
}

/// :seed_platform の platform.paths の返答を読んだ結果。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PathsReply {
    /// 書き出し先（sounds_dir の絶対パス）。
    SoundsDir(PathBuf),
    /// まだつないでいる途中（Android の最初の呼び出し。予約も同じく connecting になるので、呼び出し元はそのまま返す）。
    Connecting,
    /// 取れなかった（古い APK で paths が無い・返答が読めない等。理由の説明）。
    Failed(String),
}

/// platform.paths の返答を読む（Android の糊は serde_json を持たないので、読み方はここに置く）。
pub fn parse_paths_reply(reply: &str) -> PathsReply {
    let Ok(value) = serde_json::from_str::<Value>(reply) else {
        return PathsReply::Failed(format!("返答が JSON でありません: {reply}"));
    };
    if value.get(wire::KEY_OK).and_then(Value::as_bool) != Some(true) {
        let error = value.get(wire::KEY_ERROR).and_then(Value::as_str).unwrap_or_default();
        return if error == wire::ERROR_CONNECTING { PathsReply::Connecting } else { PathsReply::Failed(error.to_string()) };
    }
    match value.get(wire::KEY_SOUNDS_DIR).and_then(Value::as_str) {
        Some(dir) if !dir.is_empty() => PathsReply::SoundsDir(PathBuf::from(dir)),
        _ => PathsReply::Failed(format!("返答に {} がありません", wire::KEY_SOUNDS_DIR)),
    }
}

/// 予約の JSON が音源の書き出しを要るか（sound_path が空で、sound_asset がアセットを指す）。書き出し先を取りに行く前に見る。
pub fn requires_export(json: &str) -> bool {
    let Ok(Value::Object(request)) = serde_json::from_str::<Value>(json) else {
        return false;
    };
    let given_path = request.get(names::KEY_SOUND_PATH).and_then(Value::as_str).unwrap_or_default();
    let asset = request.get(names::KEY_SOUND_ASSET).and_then(Value::as_str).unwrap_or_default();
    given_path.is_empty() && matches!(classify(asset), SoundSource::Asset(_))
}

/// 音源の指定を分ける（空白だけは指定なし）。
pub fn classify(sound: &str) -> SoundSource {
    let sound = sound.trim();
    if sound.is_empty() {
        SoundSource::Default
    } else if asset_fs::is_virtual(sound) {
        SoundSource::Asset(sound.to_string())
    } else if sound.starts_with(DEVICE_ABSOLUTE_PREFIX) {
        SoundSource::DeviceFile(sound.to_string())
    } else {
        // アセットルート相対（データ側の標準表記）。区切りを / にそろえ、先頭の ./ を落として assets:// を補う
        let relative = sound.replace('\\', "/");
        SoundSource::Asset(format!("{}{}", asset_fs::ASSETS_SCHEME, relative.trim_start_matches("./")))
    }
}

/// alarm.schedule の JSON の音源を、:seed_platform が読める sound_path にする。
///
/// # 引数
/// * `json`       - スクリプトからの予約の JSON
/// * `sounds_dir` - 書き出し先（:seed_platform の platform.paths の sounds_dir。分からなければ None）
/// * `read_asset` - アセットのバイト列を読む関数（本番は `asset_fs::read_bytes`。テストは差し替える）
pub fn prepare_schedule_json(
    json: &str,
    sounds_dir: Option<&Path>,
    read_asset: impl Fn(&str) -> io::Result<Vec<u8>>,
) -> PreparedSchedule {
    let unchanged = || PreparedSchedule { json: json.to_string(), sound_path: String::new(), warning: None };
    let Ok(Value::Object(mut request)) = serde_json::from_str::<Value>(json) else {
        // 読めない JSON はそのまま送る（:seed_platform が invalid_json / invalid_argument で断る）
        return unchanged();
    };
    let given_path = request.get(names::KEY_SOUND_PATH).and_then(Value::as_str).unwrap_or_default();
    if !given_path.is_empty() {
        // 既に sound_path がある（端末のファイルを直接指定）。:seed_platform が検査する
        return PreparedSchedule { json: json.to_string(), sound_path: given_path.to_string(), warning: None };
    }
    let asset = request.remove(names::KEY_SOUND_ASSET).and_then(|value| value.as_str().map(str::to_string)).unwrap_or_default();
    let (sound_path, warning) = match classify(&asset) {
        SoundSource::Default => (String::new(), None),
        SoundSource::DeviceFile(path) => (path, None),
        SoundSource::Asset(virtual_path) => match sounds_dir {
            None => (String::new(), Some(format!("音源 {virtual_path} の書き出し先が分からないので既定の音にします"))),
            Some(dir) => match read_asset(&virtual_path).and_then(|bytes| export_sound(&virtual_path, &bytes, dir)) {
                Ok(path) => (path.to_string_lossy().into_owned(), None),
                Err(err) => (String::new(), Some(format!("音源 {virtual_path} を書き出せないので既定の音にします: {err}"))),
            },
        },
    };
    request.insert(names::KEY_SOUND_PATH.to_string(), Value::String(sound_path.clone()));
    PreparedSchedule { json: Value::Object(request).to_string(), sound_path, warning }
}

/// アセットのバイト列を `<sounds_dir>/<内容のハッシュ>.<拡張子>` へ書き出す（同じ名前・同じ大きさのファイルがあれば書かない）。
///
/// # 戻り値
/// 書き出した（または既にあった）ファイルの絶対パス
pub fn export_sound(asset_path: &str, bytes: &[u8], sounds_dir: &Path) -> io::Result<PathBuf> {
    fs::create_dir_all(sounds_dir)?;
    let target = sounds_dir.join(format!("{}.{}", content_hash_bytes(bytes), extension_of(asset_path)));
    if fs::metadata(&target).is_ok_and(|meta| meta.is_file() && meta.len() == bytes.len() as u64) {
        return Ok(target);
    }
    write_atomically(&target, bytes)?;
    Ok(target)
}

/// アセットのパスの拡張子（小文字の英数字 1〜MAX_EXTENSION_LEN 文字。それ以外は FALLBACK_EXTENSION）。
fn extension_of(asset_path: &str) -> String {
    let file_name = asset_path.rsplit(['/', '\\']).next().unwrap_or_default();
    match file_name.rsplit_once('.') {
        Some((stem, extension))
            if !stem.is_empty()
                && !extension.is_empty()
                && extension.len() <= MAX_EXTENSION_LEN
                && extension.bytes().all(|byte| byte.is_ascii_alphanumeric()) =>
        {
            extension.to_ascii_lowercase()
        }
        _ => FALLBACK_EXTENSION.to_string(),
    }
}

/// 一時ファイル → sync_all → rename（→ Unix ではフォルダも sync_all）で書く。失敗したら一時ファイルを消す。
fn write_atomically(target: &Path, bytes: &[u8]) -> io::Result<()> {
    let temp = PathBuf::from(format!("{}{TEMP_SUFFIX}", target.display()));
    let written = (|| {
        let mut file = File::create(&temp)?;
        file.write_all(bytes)?;
        file.sync_all()?;
        drop(file);
        // std::fs::rename は Unix・Windows とも既存の宛先を置き換える
        fs::rename(&temp, target)
    })();
    if written.is_err() {
        let _ = fs::remove_file(&temp);
    }
    written?;
    sync_parent_dir(target);
    Ok(())
}

/// フォルダを sync_all する（rename の結果を記憶装置へ。Unix だけ。失敗しても続ける）。
#[cfg(unix)]
fn sync_parent_dir(target: &Path) {
    if let Some(parent) = target.parent() {
        if let Err(err) = File::open(parent).and_then(|dir| dir.sync_all()) {
            eprintln!("{} 音源のフォルダを sync できませんでした（続けます）: {err}", crate::engine::platform::bridge::LOG_PREFIX);
        }
    }
}

/// フォルダの sync は Unix だけ（Windows はフォルダを開いて sync できない。デスクトップでは単体テストだけが通る）。
#[cfg(not(unix))]
fn sync_parent_dir(_target: &Path) {}

// ============================================================
//  ユニットテスト（一時フォルダに書く。アセットの読み口は差し替える）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    /// テストごとの一時フォルダの通し番号。
    static NEXT_DIR: AtomicUsize = AtomicUsize::new(0);

    /// テストごとの空の一時フォルダ（プロセス ID と通し番号で分ける）。
    fn temp_dir() -> PathBuf {
        let dir = std::env::temp_dir()
            .join(format!("seed_sound_export_{}_{}", std::process::id(), NEXT_DIR.fetch_add(1, Ordering::Relaxed)));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    /// 固定の中身を返すアセットの読み口（テスト用）。
    fn fixed_asset(bytes: &'static [u8]) -> impl Fn(&str) -> io::Result<Vec<u8>> {
        move |_| Ok(bytes.to_vec())
    }

    /// 送る JSON を読む（テスト用）。
    fn parse(text: &str) -> Value {
        serde_json::from_str(text).unwrap()
    }

    /// 指定の分け方: 空・assets://・端末の絶対パス・アセットルート相対。
    #[test]
    fn classify_sources() {
        assert_eq!(classify("  "), SoundSource::Default);
        assert_eq!(classify("assets://sounds/bell.ogg"), SoundSource::Asset("assets://sounds/bell.ogg".into()));
        assert_eq!(classify("/sdcard/Alarms/bell.mp3"), SoundSource::DeviceFile("/sdcard/Alarms/bell.mp3".into()));
        assert_eq!(classify("./sounds\\bell.wav"), SoundSource::Asset("assets://sounds/bell.wav".into()));
    }

    /// 拡張子: 小文字の英数字にそろえ、読めない形は bin。
    #[test]
    fn extension_rules() {
        assert_eq!(extension_of("assets://sounds/Alarm.OGG"), "ogg");
        assert_eq!(extension_of("assets://sounds/noext"), FALLBACK_EXTENSION);
        assert_eq!(extension_of("assets://sounds/.hidden"), FALLBACK_EXTENSION);
        assert_eq!(extension_of("assets://sounds/a.og-g"), FALLBACK_EXTENSION);
        assert_eq!(extension_of("assets://sounds/a.verylongext"), FALLBACK_EXTENSION);
    }

    /// 書き出し: 名前は内容のハッシュ.拡張子。同じ中身は同じファイル（書き直さない）、違う中身は別のファイル。一時ファイルは残らない。
    #[test]
    fn export_is_content_addressed() {
        let dir = temp_dir();
        let first = export_sound("assets://sounds/bell.ogg", b"RIFF-bell", &dir).unwrap();
        assert_eq!(first, dir.join(format!("{}.ogg", content_hash_bytes(b"RIFF-bell"))));
        assert_eq!(fs::read(&first).unwrap(), b"RIFF-bell");
        let modified = fs::metadata(&first).unwrap().modified().unwrap();
        let again = export_sound("assets://other/name.ogg", b"RIFF-bell", &dir).unwrap();
        assert_eq!(again, first, "同じ中身なら同じファイル");
        assert_eq!(fs::metadata(&again).unwrap().modified().unwrap(), modified, "同じ中身は書き直さない");
        let other = export_sound("assets://sounds/bell.ogg", b"RIFF-chime", &dir).unwrap();
        assert_ne!(other, first, "中身が違えば別のファイル");
        let leftovers: Vec<_> = fs::read_dir(&dir).unwrap().filter_map(Result::ok)
            .filter(|entry| entry.file_name().to_string_lossy().ends_with(TEMP_SUFFIX)).collect();
        assert!(leftovers.is_empty(), "一時ファイルが残っている");
        let _ = fs::remove_dir_all(&dir);
    }

    /// assets:// の音は書き出して sound_path に入り、sound_asset は消える（ほかの欄はそのまま）。
    #[test]
    fn asset_sound_is_exported_into_sound_path() {
        let dir = temp_dir();
        let json = r#"{"id":"morning","trigger_at_utc_ms":1790000000000,"sound_asset":"assets://sounds/bell.ogg","title":"朝"}"#;
        let prepared = prepare_schedule_json(json, Some(&dir), fixed_asset(b"OggS-bell"));
        assert_eq!(prepared.warning, None);
        let sent = parse(&prepared.json);
        assert!(sent.get(names::KEY_SOUND_ASSET).is_none(), "sound_asset が残っている");
        assert_eq!(sent[names::KEY_SOUND_PATH], Value::from(prepared.sound_path.clone()));
        assert_eq!(fs::read(&prepared.sound_path).unwrap(), b"OggS-bell");
        assert_eq!(sent["title"], Value::from("朝"));
        assert_eq!(sent[names::KEY_ID], Value::from("morning"));
        let _ = fs::remove_dir_all(&dir);
    }

    /// 端末の絶対パス・指定なし・既に sound_path があるものは書き出さない。
    #[test]
    fn device_paths_and_defaults_pass_through() {
        let never = |_: &str| -> io::Result<Vec<u8>> { panic!("読まないはず") };
        let device = prepare_schedule_json(r#"{"id":"a","sound_asset":"/sdcard/bell.mp3"}"#, None, never);
        assert_eq!(device.sound_path, "/sdcard/bell.mp3");
        assert_eq!(parse(&device.json)[names::KEY_SOUND_PATH], Value::from("/sdcard/bell.mp3"));
        let none = prepare_schedule_json(r#"{"id":"a"}"#, None, never);
        assert_eq!((none.sound_path.as_str(), none.warning.clone()), ("", None));
        assert_eq!(parse(&none.json)[names::KEY_SOUND_PATH], Value::from(""));
        let given = prepare_schedule_json(r#"{"id":"a","sound_path":"/data/x.ogg"}"#, None, never);
        assert_eq!(given.sound_path, "/data/x.ogg");
    }

    /// platform.paths の返答: 成功なら sounds_dir、connecting はそのまま、それ以外は失敗の理由。
    #[test]
    fn paths_reply_parsing() {
        let ok = r#"{"ok":true,"device_protected_files_dir":"/data/user_de/0/p/files","sounds_dir":"/data/user_de/0/p/files/seed_platform/sounds"}"#;
        assert_eq!(parse_paths_reply(ok), PathsReply::SoundsDir(PathBuf::from("/data/user_de/0/p/files/seed_platform/sounds")));
        assert_eq!(parse_paths_reply(r#"{"ok":false,"error":"connecting"}"#), PathsReply::Connecting);
        assert_eq!(parse_paths_reply(r#"{"ok":false,"error":"unknown_method"}"#), PathsReply::Failed("unknown_method".into()));
        assert!(matches!(parse_paths_reply(r#"{"ok":true}"#), PathsReply::Failed(_)));
        assert!(matches!(parse_paths_reply("not json"), PathsReply::Failed(_)));
    }

    /// 書き出しが要るのは「sound_path が空で sound_asset がアセット」のときだけ。
    #[test]
    fn requires_export_only_for_assets() {
        assert!(requires_export(r#"{"id":"a","sound_asset":"assets://sounds/bell.ogg"}"#));
        assert!(requires_export(r#"{"id":"a","sound_asset":"sounds/bell.ogg"}"#));
        assert!(!requires_export(r#"{"id":"a","sound_asset":"/sdcard/bell.ogg"}"#));
        assert!(!requires_export(r#"{"id":"a"}"#));
        assert!(!requires_export(r#"{"id":"a","sound_path":"/x.ogg","sound_asset":"assets://sounds/bell.ogg"}"#));
        assert!(!requires_export("{broken"));
    }

    /// 書き出せないときは既定の音（sound_path = ""）にして警告し、予約そのものは送る。読めない JSON は元のまま。
    #[test]
    fn failures_fall_back_to_default_sound() {
        let json = r#"{"id":"a","sound_asset":"assets://sounds/missing.ogg"}"#;
        let missing = prepare_schedule_json(json, Some(&temp_dir()), |_| Err(io::Error::new(io::ErrorKind::NotFound, "無い")));
        assert_eq!(missing.sound_path, "");
        assert!(missing.warning.as_deref().unwrap_or_default().contains("missing.ogg"));
        assert!(parse(&missing.json).get(names::KEY_SOUND_ASSET).is_none());
        let no_dir = prepare_schedule_json(json, None, fixed_asset(b"x"));
        assert_eq!(no_dir.sound_path, "");
        assert!(no_dir.warning.is_some());
        let broken = prepare_schedule_json("{broken", None, fixed_asset(b"x"));
        assert_eq!(broken.json, "{broken");
    }
}
