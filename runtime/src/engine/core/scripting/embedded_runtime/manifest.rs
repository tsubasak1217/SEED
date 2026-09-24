// ============================================================
//  embedded_runtime/manifest.rs — 同梱 .NET の目録（bundle.json）の読み取りと検査
//
//  【何の目録か】
//  SeedAndroid（editor/src/Android/Dotnet/DotnetRuntimeBundle.cs。runtime/android/dotnet_runtime.json が設定の正典）が
//  ABI ごとに書き出す、
//  APK に同梱した .NET ランタイムの一覧。APK の assets/seed/dotnet/<ABI>/bundle.json に入る。
//    - 版と種類（coreclr / mono）・ABI・中身の識別子（content_id。展開先のフォルダ名に使う）
//    - ファイルの一覧（dotnet-root 形式の中での相対パス・取り出し元・大きさ）
//        asset          … APK の assets/seed/dotnet/<ABI>/<相対パス>（BCL の DLL・deps.json・runtimeconfig）
//        native_library … APK の lib/<ABI>/<ファイル名>（端末では nativeLibraryDir に展開される .so）
//    - CLR の起動時に設定するランタイムプロパティ（例 System.Globalization.Invariant=true）
//  ランタイムはこの目録どおりにファイルを files/dotnet/<種類>-<版>-<content_id>/ へ並べ（install.rs）、
//  hostfxr を読み込んで CLR を起動する（clr_host/embedded.rs）。
//
//  【書式を変えるとき】
//  書き出し側（DotnetRuntimeBundle.cs の ManifestFormatVersion と WriteManifest）と SUPPORTED_FORMAT_VERSION を一緒に上げる。
//  版の合わない目録は読まずにスクリプト無しで起動する（古い APK と新しいランタイムを混ぜない）。
// ============================================================

use std::collections::BTreeMap;
use std::fmt;

use serde::Deserialize;

/// 読める目録の書式の版（editor/src/Android/Dotnet/DotnetRuntimeBundle.cs の `ManifestFormatVersion` と一致させる）。
pub const SUPPORTED_FORMAT_VERSION: u32 = 1;

/// 相対パスの区切り（目録の中は OS に関係なく `/`）。
const PATH_SEPARATOR: char = '/';

/// 同梱 .NET の目録（bundle.json）。
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct BundleManifest {
    /// 目録の書式の版（`SUPPORTED_FORMAT_VERSION` と一致しなければ読まない）。
    pub format_version: u32,
    /// ランタイムの種類（`coreclr` / `mono`。ログと展開先の名前に使う）。
    pub runtime: String,
    /// 共有フレームワークの名前（`Microsoft.NETCore.App`）。
    pub framework: String,
    /// ランタイムの版（`10.0.12`）。
    pub version: String,
    /// この目録の ABI（`arm64-v8a` / `x86_64`）。
    pub abi: String,
    /// 中身の識別子（16 進）。中身が変われば変わり、展開先のフォルダ名に入る。
    pub content_id: String,
    /// hostfxr（libhostfxr.so）の dotnet-root 内の相対パス（`files` のどれか）。
    pub hostfxr: String,
    /// native_library の .so を dotnet-root へ置く方法（既定はシンボリックリンク）。
    #[serde(default)]
    pub native_library_mode: NativeLibraryMode,
    /// CLR の起動前に hostfxr へ設定するランタイムプロパティ（名前 → 値）。
    #[serde(default)]
    pub runtime_properties: BTreeMap<String, String>,
    /// dotnet-root に並べるファイルの一覧。
    pub files: Vec<BundleFile>,
}

/// native_library の .so を dotnet-root へ置く方法（docs/android.md §17.4。実機 Pixel 6a とエミュレータでどちらも動く）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum NativeLibraryMode {
    /// dotnet-root に nativeLibraryDir へのシンボリックリンクを置く（既定）。
    ///
    /// .so を複製しない（1 ABI あたり約 12 MB）、.so を OS が展開した場所（apk_data_file）から実行する、
    /// tombstone のバックトレースに関数名が出る（debuggerd は lib/ の下しか読めない）、そして
    /// Java が System.loadLibrary で JNI_OnLoad 済みの暗号ライブラリと CLR が同じ実体を使う（暗号 API が動く）。
    /// アプリを入れ直すと nativeLibraryDir の場所が変わるが、install が張り直す。
    #[default]
    Symlink,
    /// nativeLibraryDir から dotnet-root へ複製する（実ファイルにする）。
    ///
    /// シンボリックリンクが使えない端末への逃げ道。CLR が読む暗号ライブラリが Java の読み込んだものと別の実体になり、
    /// 初期化されないため、スクリプトの暗号 API（SHA256 等）はプロセスごと落ちる。
    Copy,
}

/// 目録の 1 ファイル。
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct BundleFile {
    /// dotnet-root からの相対パス（`/` 区切り）。
    pub path: String,
    /// 取り出し元。
    pub source: BundleFileSource,
    /// 大きさ（バイト）。asset だけに入る（native_library は APK 作成時にシンボルが削られ得るので持たない）。
    #[serde(default)]
    pub size: Option<u64>,
}

/// ファイルの取り出し元。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum BundleFileSource {
    /// APK の assets/seed/dotnet/<ABI>/<相対パス>。
    Asset,
    /// APK の lib/<ABI>/<ファイル名>（端末では nativeLibraryDir に展開済み）。
    NativeLibrary,
}

/// 目録を読めない・中身がおかしい理由。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ManifestError {
    /// JSON として読めない・必須の項目が無い。
    Json(String),
    /// 書式の版が違う（APK とランタイムの食い違い）。
    UnsupportedFormat(u32),
    /// 相対パスが不正（絶対パス・`..`・空の区切り・`\`）。
    InvalidPath(String),
    /// フォルダ名に使う値（種類・版・content_id）に使えない文字がある。
    InvalidName(String),
    /// hostfxr がファイル一覧に無い。
    MissingHostfxr(String),
}

impl fmt::Display for ManifestError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Json(e) => write!(f, "bundle.json を読めません: {e}"),
            Self::UnsupportedFormat(v) => write!(
                f,
                "bundle.json の書式の版 {v} には対応していません（対応: {SUPPORTED_FORMAT_VERSION}。APK を作り直してください）"
            ),
            Self::InvalidPath(p) => write!(f, "bundle.json のパスが不正です: {p:?}"),
            Self::InvalidName(n) => write!(f, "bundle.json の名前に使えない文字があります: {n:?}"),
            Self::MissingHostfxr(p) => write!(f, "bundle.json の hostfxr（{p}）がファイル一覧にありません"),
        }
    }
}

impl std::error::Error for ManifestError {}

impl BundleManifest {
    /// bundle.json のバイト列を読んで検査する。
    ///
    /// # 引数
    /// * `bytes` - bundle.json の中身（UTF-8 の JSON）
    pub fn parse(bytes: &[u8]) -> Result<Self, ManifestError> {
        let manifest: Self = serde_json::from_slice(bytes).map_err(|e| ManifestError::Json(e.to_string()))?;
        manifest.validate()?;
        Ok(manifest)
    }

    /// 中身の検査（書式の版・パスの安全性・hostfxr の有無）。
    fn validate(&self) -> Result<(), ManifestError> {
        if self.format_version != SUPPORTED_FORMAT_VERSION {
            return Err(ManifestError::UnsupportedFormat(self.format_version));
        }
        for name in [&self.runtime, &self.version, &self.content_id] {
            if !is_safe_name(name) {
                return Err(ManifestError::InvalidName(name.clone()));
            }
        }
        for file in &self.files {
            if !is_safe_relative_path(&file.path) {
                return Err(ManifestError::InvalidPath(file.path.clone()));
            }
        }
        if !self.files.iter().any(|file| file.path == self.hostfxr) {
            return Err(ManifestError::MissingHostfxr(self.hostfxr.clone()));
        }
        Ok(())
    }

    /// 展開先のフォルダ名（`<種類>-<版>-<content_id>`）【純関数】。
    ///
    /// 版だけでなく中身の識別子を入れるので、同じ版のまま coreclr ⇔ mono を切り替えたときや
    /// 同梱の内容を変えたときも、古い展開を使い回さずに別のフォルダへ展開し直す。
    pub fn install_dir_name(&self) -> String {
        format!("{}-{}-{}", self.runtime, self.version, self.content_id)
    }

    /// ログ用の一行説明（例 `coreclr 10.0.12（x86_64）`）。
    pub fn describe(&self) -> String {
        format!("{} {}（{}）", self.runtime, self.version, self.abi)
    }

    /// asset の合計バイト数（ログ用）。
    pub fn asset_bytes(&self) -> u64 {
        self.files
            .iter()
            .filter(|file| file.source == BundleFileSource::Asset)
            .filter_map(|file| file.size)
            .sum()
    }
}

impl BundleFile {
    /// ファイル名（相対パスの最後の要素）。native_library は nativeLibraryDir の中のこの名前を指す。
    pub fn file_name(&self) -> &str {
        self.path.rsplit(PATH_SEPARATOR).next().unwrap_or(&self.path)
    }
}

/// 相対パスが安全か（dotnet-root の外を指さない）【純関数】。
///
/// 空・絶対パス・`\`・空の要素（`a//b`）・`.`・`..` を拒む。
pub fn is_safe_relative_path(path: &str) -> bool {
    !path.is_empty()
        && !path.starts_with(PATH_SEPARATOR)
        && !path.contains('\\')
        && path
            .split(PATH_SEPARATOR)
            .all(|part| !part.is_empty() && part != "." && part != "..")
}

/// フォルダ名の一部に使ってよい値か（英数字と `.` `_` `-` だけ）【純関数】。
fn is_safe_name(value: &str) -> bool {
    !value.is_empty()
        && value != "."
        && value != ".."
        && value.chars().all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '-'))
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の最小の目録（CoreCLR・x86_64）。
    const SAMPLE: &str = r#"{
        "format_version": 1,
        "runtime": "coreclr",
        "framework": "Microsoft.NETCore.App",
        "version": "10.0.12",
        "abi": "x86_64",
        "content_id": "0123456789abcdef",
        "hostfxr": "host/fxr/10.0.12/libhostfxr.so",
        "runtime_properties": { "System.Globalization.Invariant": "true" },
        "files": [
            { "path": "host/fxr/10.0.12/libhostfxr.so", "source": "native_library" },
            { "path": "shared/Microsoft.NETCore.App/10.0.12/System.Runtime.dll", "source": "asset", "size": 42 }
        ]
    }"#;

    /// 正しい目録が読め、既定値（シンボリックリンク）と展開先の名前が決まること。
    #[test]
    fn parses_sample_manifest() {
        let manifest = BundleManifest::parse(SAMPLE.as_bytes()).expect("読めるべき");
        assert_eq!(manifest.native_library_mode, NativeLibraryMode::Symlink);
        assert_eq!(manifest.install_dir_name(), "coreclr-10.0.12-0123456789abcdef");
        assert_eq!(manifest.describe(), "coreclr 10.0.12（x86_64）");
        assert_eq!(manifest.asset_bytes(), 42);
        assert_eq!(manifest.files[0].file_name(), "libhostfxr.so");
        assert_eq!(manifest.files[0].source, BundleFileSource::NativeLibrary);
        assert_eq!(
            manifest.runtime_properties.get("System.Globalization.Invariant").map(String::as_str),
            Some("true")
        );
    }

    /// 書式の版が違う目録は読まない（古い APK と新しいランタイムを混ぜない）。
    #[test]
    fn rejects_other_format_version() {
        let text = SAMPLE.replace("\"format_version\": 1", "\"format_version\": 2");
        assert_eq!(BundleManifest::parse(text.as_bytes()), Err(ManifestError::UnsupportedFormat(2)));
    }

    /// dotnet-root の外を指すパスは拒む。
    #[test]
    fn rejects_escaping_paths() {
        for bad in ["../x.dll", "/abs.dll", "a//b.dll", "a/./b.dll", "a\\b.dll", ""] {
            assert!(!is_safe_relative_path(bad), "通してはいけない: {bad:?}");
        }
        assert!(is_safe_relative_path("shared/Microsoft.NETCore.App/10.0.12/System.Private.CoreLib.dll"));

        let text = SAMPLE.replace(
            "shared/Microsoft.NETCore.App/10.0.12/System.Runtime.dll",
            "../../System.Runtime.dll",
        );
        assert!(matches!(BundleManifest::parse(text.as_bytes()), Err(ManifestError::InvalidPath(_))));
    }

    /// フォルダ名に使う値に区切り文字などが入っていたら拒む。
    #[test]
    fn rejects_unsafe_names() {
        let text = SAMPLE.replace("0123456789abcdef", "../evil");
        assert!(matches!(BundleManifest::parse(text.as_bytes()), Err(ManifestError::InvalidName(_))));
    }

    /// hostfxr がファイル一覧に無い目録は拒む（CLR を起動できない）。
    #[test]
    fn rejects_missing_hostfxr() {
        let text = SAMPLE.replace(
            "\"hostfxr\": \"host/fxr/10.0.12/libhostfxr.so\"",
            "\"hostfxr\": \"host/fxr/10.0.12/libother.so\"",
        );
        assert!(matches!(BundleManifest::parse(text.as_bytes()), Err(ManifestError::MissingHostfxr(_))));
    }

    /// 置き方の指定（copy / symlink）を読めること。
    #[test]
    fn reads_native_library_mode() {
        for (value, expected) in [("copy", NativeLibraryMode::Copy), ("symlink", NativeLibraryMode::Symlink)] {
            let text = SAMPLE.replace(
                "\"runtime_properties\"",
                &format!("\"native_library_mode\": \"{value}\", \"runtime_properties\""),
            );
            let manifest = BundleManifest::parse(text.as_bytes()).unwrap();
            assert_eq!(manifest.native_library_mode, expected, "{value}");
        }
    }

    /// JSON として壊れていれば Json エラー。
    #[test]
    fn rejects_broken_json() {
        assert!(matches!(BundleManifest::parse(b"{"), Err(ManifestError::Json(_))));
    }
}
