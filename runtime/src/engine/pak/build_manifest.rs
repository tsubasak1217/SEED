// ============================================================
//  pak/build_manifest.rs — pak に入った「ビルドの印」（開発用のビルドか）の読み取り【中身の形式の正典】
//
//  【何のためか】
//  「pak 実行（assets.pak を読んで動いている）＝配布版」という前提が、開発用のビルド（SeedAndroid の
//  debug の APK・パッケージ化ウィンドウの Debug のビルド）に合わない。開発用のビルドでもデバッグの命令・
//  開発用の機能を使えるように、パッケージ化が pak に「開発用」の印を入れ、ランタイムが起動時に読む
//  （app_env::is_debug_build → スクリプト API SEED.Application.IsDebugBuild / IsDebugAllowed）。
//
//  【形式】
//  エントリ名は core::package_layout::BUILD_MANIFEST_ENTRY（".seed/build.json"）。中身は UTF-8 の JSON オブジェクト:
//      { "format": 1, "debug": true }
//    format … 形式の版（SUPPORTED_FORMAT と同じでなければ「読めない」扱い）
//    debug  … 開発用のビルドか
//  書き手はエディタの editor/src/Packaging/Pak/PakBuildManifest.cs。**開発用のビルドのときだけ書き、配布用（release）
//  では書かない**（配布前の検査〈editor/src/Android/Release/AndroidArtifactChecks.cs〉は、配布物の pak にこのエントリが
//  あれば不合格にする）。知らないキーは読み飛ばす（後から項目を足せるように）。
//
//  【安全側の既定】
//  エントリが無い・読めない・知らない版・debug が true でない → 開発用ではない（配布版として扱う）。
//  印の読み違いが「開発用の機能を開かない」側にしか倒れないので、配布物で開発用の機能が開くことは無い。
// ============================================================

use serde::Deserialize;

use super::PakReader;
use crate::engine::core::package_layout::BUILD_MANIFEST_ENTRY;

/// 読める形式の版（エディタ側 `PakBuildManifest.FormatVersion` と一致必須）。
pub const SUPPORTED_FORMAT: u32 = 1;

// ============================================================
//  データ
// ============================================================

/// ビルドの印の中身（JSON の 1 オブジェクト）。
///
/// どのキーも欠けてよい（`#[serde(default)]`）。欠けた `format` は 0 になり、`parse` が知らない版として弾く。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize)]
pub struct BuildManifest {
    /// 形式の版（`SUPPORTED_FORMAT` と同じであること）。
    #[serde(default)]
    pub format: u32,
    /// 開発用のビルドか（欠けていれば false＝配布版として扱う）。
    #[serde(default)]
    pub debug: bool,
}

/// 開いた pak からビルドの印を読んだ結果。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum BuildManifestRead {
    /// 印のエントリが無い（配布用のビルド・印の仕組みより前に作った pak）。
    Absent,
    /// 読めた。
    Present(BuildManifest),
    /// エントリはあるが読めない（JSON でない・知らない版・読み出しの失敗）。理由を持つ。
    Invalid(String),
}

impl BuildManifestRead {
    /// 開発用のビルドか（読めて `debug` が true のときだけ true。それ以外は安全側の false）。
    pub fn is_debug_build(&self) -> bool {
        matches!(self, Self::Present(manifest) if manifest.debug)
    }

    /// 起動ログに出す 1 行の説明（どの判定になったかと、その理由）。
    pub fn describe(&self) -> String {
        match self {
            Self::Absent => format!("{BUILD_MANIFEST_ENTRY} なし（配布用のビルドとして扱う）"),
            Self::Present(manifest) if manifest.debug => {
                format!("{BUILD_MANIFEST_ENTRY} あり・debug=true（開発用のビルド。開発用の機能を開く）")
            }
            Self::Present(_) => format!("{BUILD_MANIFEST_ENTRY} あり・debug=false（配布用のビルドとして扱う）"),
            Self::Invalid(reason) => {
                format!("{BUILD_MANIFEST_ENTRY} を読めません（{reason}）。配布用のビルドとして扱う")
            }
        }
    }
}

// ============================================================
//  読み取り
// ============================================================

/// ビルドの印の JSON を読む【純関数】。
///
/// # 引数
/// * `bytes` - エントリの中身（UTF-8 の JSON）
///
/// # 戻り値
/// 読めた中身。JSON のオブジェクトでない・知らない版（`format` が `SUPPORTED_FORMAT` でない・欠けている）なら理由。
pub fn parse(bytes: &[u8]) -> Result<BuildManifest, String> {
    let manifest: BuildManifest =
        serde_json::from_slice(bytes).map_err(|err| format!("JSON のオブジェクトとして読めません: {err}"))?;
    if manifest.format != SUPPORTED_FORMAT {
        return Err(format!(
            "知らない形式の版です（format={}。読めるのは {SUPPORTED_FORMAT}）",
            manifest.format
        ));
    }
    Ok(manifest)
}

/// 開いた pak からビルドの印を読む。
///
/// エントリ表を引くだけで、印が無ければ中身は読まない（起動時に 1 回だけ呼ばれる）。
///
/// # 引数
/// * `pak` - 開いた pak（読み出しで Seek するので可変参照）
pub fn read_from_pak(pak: &mut PakReader) -> BuildManifestRead {
    if !pak.contains(BUILD_MANIFEST_ENTRY) {
        return BuildManifestRead::Absent;
    }
    match pak.read(BUILD_MANIFEST_ENTRY) {
        None => BuildManifestRead::Invalid("エントリを読み出せません".to_string()),
        Some(bytes) => match parse(&bytes) {
            Ok(manifest) => BuildManifestRead::Present(manifest),
            Err(reason) => BuildManifestRead::Invalid(reason),
        },
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::pak::tests::build_pak_bytes;
    use std::io::Cursor;

    /// エディタの PakBuildManifest が開発用のビルドで書く中身（同じ形）。
    const DEVELOPMENT_JSON: &[u8] = br#"{"format":1,"debug":true}"#;

    /// メモリ上の pak を開く。
    fn memory_pak(entries: &[(&str, &[u8])]) -> PakReader {
        PakReader::from_source(Cursor::new(build_pak_bytes(entries))).unwrap()
    }

    /// 開発用の印は debug=true として読める。知らないキーは読み飛ばす。
    #[test]
    fn parse_reads_development_mark() {
        let manifest = parse(DEVELOPMENT_JSON).unwrap();
        assert_eq!(manifest, BuildManifest { format: SUPPORTED_FORMAT, debug: true });
        let extended = parse(br#"{"format":1,"debug":true,"built_by":"SeedPak"}"#).unwrap();
        assert!(extended.debug, "後から足した項目があっても読める");
    }

    /// debug が false・欠けているときは開発用ではない。
    #[test]
    fn parse_treats_missing_or_false_debug_as_release() {
        assert!(!parse(br#"{"format":1,"debug":false}"#).unwrap().debug);
        assert!(!parse(br#"{"format":1}"#).unwrap().debug, "debug が欠けていれば false");
    }

    /// 知らない版・版の欠け・JSON でないものは読めない（理由付き）。
    #[test]
    fn parse_rejects_unknown_format_and_garbage() {
        assert!(parse(br#"{"format":2,"debug":true}"#).unwrap_err().contains("format=2"));
        assert!(parse(br#"{"debug":true}"#).unwrap_err().contains("format=0"), "版が欠けていれば読めない");
        assert!(parse(b"not json").is_err());
        assert!(parse(b"[1,2]").is_err(), "オブジェクトでない");
        assert!(parse(b"").is_err(), "空");
    }

    /// pak に印があれば開発用のビルド。
    #[test]
    fn read_from_pak_finds_development_mark() {
        let mut pak = memory_pak(&[("scenes/Main.scene", b"{}"), (BUILD_MANIFEST_ENTRY, DEVELOPMENT_JSON)]);
        let read = read_from_pak(&mut pak);
        assert!(read.is_debug_build(), "{read:?}");
        assert!(read.describe().contains("debug=true"));
    }

    /// 印が無い pak（配布用・仕組みより前の pak）は開発用ではない（既定は false）。
    #[test]
    fn read_from_pak_without_mark_is_release() {
        let mut pak = memory_pak(&[("scenes/Main.scene", b"{}")]);
        let read = read_from_pak(&mut pak);
        assert_eq!(read, BuildManifestRead::Absent);
        assert!(!read.is_debug_build());
    }

    /// 印のエントリが壊れていても開発用の機能は開かない（安全側）。debug=false の印も同じ。
    #[test]
    fn read_from_pak_with_broken_or_false_mark_is_release() {
        let mut broken = memory_pak(&[(BUILD_MANIFEST_ENTRY, b"{ broken")]);
        let read = read_from_pak(&mut broken);
        assert!(matches!(read, BuildManifestRead::Invalid(_)), "{read:?}");
        assert!(!read.is_debug_build());

        let mut release = memory_pak(&[(BUILD_MANIFEST_ENTRY, br#"{"format":1,"debug":false}"#)]);
        assert!(!read_from_pak(&mut release).is_debug_build());
    }
}
