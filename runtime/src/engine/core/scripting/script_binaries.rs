// ============================================================
//  scripting/script_binaries.rs — スクリプトの DLL 一式の「読み口」と、置き場の選び方
//
//  【何を読むか】
//  同梱 .NET（埋め込み CLR。Android）でスクリプトを動かすときに要る 3 つのファイル。
//    SEEDScripting.dll                  … スクリプトホスト（ScriptBridge・SEED.* API）
//    SEEDUserScripts.dll                … ユーザースクリプトの事前コンパイル DLL（ScriptPackager / SeedPak --scripts）
//    SEEDScripting.runtimeconfig.json   … CLR の初期化に使う runtimeconfig（デスクトップと同じファイルをそのまま使う）
//  3 つは同じビルドで作られた組なので、必ず同じ置き場からまとめて読む（混ぜると版がずれる）。
//
//  【置き場の候補（優先順。Android の糊 runtime/android/native の dotnet_runtime が並べる）】
//    1. 内部アプリ専用フォルダの files/bin/   … build_and_run.ps1 -PushScripts（run-as で送る。実機でも読める）
//    2. 外部アプリ専用フォルダの files/bin/   … 手で adb push した置き場（エミュレータ向け。実機は読めないことが多い）
//    3. 配布物の bin/（APK の assets/seed/bin/） … build_and_run.ps1 -ProjectDir（SeedPak --scripts）が APK に入れたもの
//  「スクリプトホスト（SEEDScripting.dll）がある最初の置き場」を使う（データの有無で決める。設定フラグは無い）。
//  開発中は APK を作り直さずに DLL だけ差し替えられ、差し替えを消せば APK 内のものへ戻る。
//
//  デスクトップはこの仕組みを使わない（従来どおり開発ビルド出力か実行ファイルの bin/ をパスで読む。mod.rs）。
//  全体像は docs/android.md §17。
// ============================================================

use std::io;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use crate::engine::core::package_layout;
use crate::engine::package_source::PackageSource;

use super::{PRECOMPILED_SCRIPTS_DLL_NAME, SCRIPTING_HOST_DLL_NAME};

/// スクリプトホストの runtimeconfig のファイル名（`SEEDScripting.csproj` のビルド出力・ScriptPackager が bin/ へ写すもの）。
pub const SCRIPTING_HOST_RUNTIME_CONFIG_NAME: &str = "SEEDScripting.runtimeconfig.json";

/// スクリプトの DLL 一式を読む読み口。
///
/// CLR の起動（`clr_host::embedded`）とユーザースクリプトのロード（`app/script_boot.rs`）の 2 か所から
/// 使うので `Arc` で共有する。読み口の実体はフォルダ（ファイルシステム）か配布物（APK）。
pub trait ScriptBinarySource: Send + Sync {
    /// ファイルを丸ごと読む。無ければ `io::ErrorKind::NotFound`。
    fn read(&self, file_name: &str) -> io::Result<Vec<u8>>;

    /// ファイルがあるか（置き場の選択に使う。中身は読まない）。
    fn contains(&self, file_name: &str) -> bool;

    /// ファイルの場所を人が読める形で返す（ログ用）。
    fn describe(&self, file_name: &str) -> String;
}

// ============================================================
//  実装 1: フォルダ（端末のファイルシステム上の files/bin/）
// ============================================================

/// フォルダに置いた DLL 一式（`-PushScripts` の置き場など）。
pub struct DirectoryBinaries {
    /// DLL を置いたフォルダ。
    dir: PathBuf,
}

impl DirectoryBinaries {
    /// フォルダを読み口にする（フォルダが無くても作れる。無ければ `contains` が false になるだけ）。
    ///
    /// # 引数
    /// * `dir` - DLL を置いたフォルダ
    pub fn new(dir: impl Into<PathBuf>) -> Self {
        Self { dir: dir.into() }
    }

    /// 読み口のフォルダ。
    pub fn dir(&self) -> &Path {
        &self.dir
    }
}

impl ScriptBinarySource for DirectoryBinaries {
    fn read(&self, file_name: &str) -> io::Result<Vec<u8>> {
        std::fs::read(self.dir.join(file_name))
    }

    fn contains(&self, file_name: &str) -> bool {
        self.dir.join(file_name).is_file()
    }

    fn describe(&self, file_name: &str) -> String {
        self.dir.join(file_name).display().to_string()
    }
}

// ============================================================
//  実装 2: 配布物の bin/（Android は APK の assets/seed/bin/）
// ============================================================

/// 配布物の `bin/`（`core::package_layout::BIN_DIR_NAME`）に入った DLL 一式。
pub struct PackageBinaries {
    /// 配布物の読み口（Android は APK の assets/seed/ を読む ApkPackageSource）。
    package: Arc<dyn PackageSource>,
}

impl PackageBinaries {
    /// 配布物の読み口から作る。
    ///
    /// # 引数
    /// * `package` - 配布物の読み口
    pub fn new(package: Arc<dyn PackageSource>) -> Self {
        Self { package }
    }
}

impl ScriptBinarySource for PackageBinaries {
    fn read(&self, file_name: &str) -> io::Result<Vec<u8>> {
        self.package.read_all(&package_bin_path(file_name))
    }

    fn contains(&self, file_name: &str) -> bool {
        self.package.open(&package_bin_path(file_name)).is_ok()
    }

    fn describe(&self, file_name: &str) -> String {
        self.package.describe(&package_bin_path(file_name))
    }
}

/// 配布物のルートからの `bin/<ファイル名>` を作る【純関数】（区切りは `/`。APK 内のパスは `/` だけを受け付ける）。
pub fn package_bin_path(file_name: &str) -> String {
    format!("{}/{file_name}", package_layout::BIN_DIR_NAME)
}

// ============================================================
//  置き場の選択（純関数）
// ============================================================

/// 候補（優先順）のうち、スクリプトホスト（`SEEDScripting.dll`）がある最初の置き場の番号を返す【純関数】。
///
/// ホストの有無だけで決める（ユーザースクリプト DLL は無くてもよい＝スクリプトを使っていないゲーム）。
/// ホストの無い置き場を飛ばすので、ユーザースクリプトだけを手で置いた不完全な置き場は選ばれない
/// （版の違うホストと混ぜないため）。
///
/// # 引数
/// * `candidates` - 置き場の候補（優先順）
///
/// # 戻り値
/// 選んだ候補の番号。どれにもホストが無ければ None（スクリプト無しで起動する）。
pub fn choose_binaries(candidates: &[Arc<dyn ScriptBinarySource>]) -> Option<usize> {
    candidates.iter().position(|source| source.contains(SCRIPTING_HOST_DLL_NAME))
}

/// 置き場 1 つの中身の要約（ログ用。ホスト・ユーザースクリプト・runtimeconfig の有無）【純関数】。
///
/// # 引数
/// * `source` - 置き場
pub fn summarize(source: &dyn ScriptBinarySource) -> String {
    let mark = |name: &str| if source.contains(name) { "あり" } else { "なし" };
    format!(
        "{}={} / {}={} / {}={}",
        SCRIPTING_HOST_DLL_NAME,
        mark(SCRIPTING_HOST_DLL_NAME),
        PRECOMPILED_SCRIPTS_DLL_NAME,
        mark(PRECOMPILED_SCRIPTS_DLL_NAME),
        SCRIPTING_HOST_RUNTIME_CONFIG_NAME,
        mark(SCRIPTING_HOST_RUNTIME_CONFIG_NAME),
    )
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::package_source::MemoryPackage;
    use std::collections::HashMap;

    /// メモリ上のファイル表で置き場を模した読み口（このテスト専用）。
    struct MemoryBinaries {
        files: HashMap<String, Vec<u8>>,
    }

    impl MemoryBinaries {
        fn with(names: &[&str]) -> Arc<dyn ScriptBinarySource> {
            Arc::new(Self {
                files: names.iter().map(|n| (n.to_string(), n.as_bytes().to_vec())).collect(),
            })
        }
    }

    impl ScriptBinarySource for MemoryBinaries {
        fn read(&self, file_name: &str) -> io::Result<Vec<u8>> {
            self.files
                .get(file_name)
                .cloned()
                .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, file_name.to_string()))
        }
        fn contains(&self, file_name: &str) -> bool {
            self.files.contains_key(file_name)
        }
        fn describe(&self, file_name: &str) -> String {
            format!("memory:{file_name}")
        }
    }

    /// ホストのある最初の置き場を選ぶ（差し替え用の置き場が APK 内の bin/ より優先される）。
    #[test]
    fn first_candidate_with_host_wins() {
        let pushed = MemoryBinaries::with(&[SCRIPTING_HOST_DLL_NAME, PRECOMPILED_SCRIPTS_DLL_NAME]);
        let apk = MemoryBinaries::with(&[SCRIPTING_HOST_DLL_NAME, PRECOMPILED_SCRIPTS_DLL_NAME]);
        assert_eq!(choose_binaries(&[pushed, apk]), Some(0));
    }

    /// ホストの無い置き場（ユーザースクリプトだけ・空）は飛ばして次を見る。
    #[test]
    fn candidates_without_host_are_skipped() {
        let empty = MemoryBinaries::with(&[]);
        let user_only = MemoryBinaries::with(&[PRECOMPILED_SCRIPTS_DLL_NAME]);
        let apk = MemoryBinaries::with(&[SCRIPTING_HOST_DLL_NAME]);
        assert_eq!(choose_binaries(&[empty, user_only, apk]), Some(2));
    }

    /// どこにもホストが無ければ None（スクリプト無しで起動する）。
    #[test]
    fn no_host_anywhere_is_none() {
        let empty = MemoryBinaries::with(&[]);
        assert_eq!(choose_binaries(&[empty]), None);
        assert_eq!(choose_binaries(&[]), None);
    }

    /// 配布物の読み口は bin/<ファイル名> を読む（APK の assets/seed/bin/）。
    #[test]
    fn package_binaries_read_bin_dir() {
        let package = MemoryPackage::new(&[
            ("bin/SEEDScripting.dll", b"host"),
            ("bin/SEEDUserScripts.dll", b"user"),
        ]);
        let binaries = PackageBinaries::new(Arc::new(package));
        assert!(binaries.contains(SCRIPTING_HOST_DLL_NAME));
        assert!(!binaries.contains(SCRIPTING_HOST_RUNTIME_CONFIG_NAME));
        assert_eq!(binaries.read(PRECOMPILED_SCRIPTS_DLL_NAME).unwrap(), b"user");
        assert_eq!(
            binaries.read(SCRIPTING_HOST_RUNTIME_CONFIG_NAME).err().map(|e| e.kind()),
            Some(io::ErrorKind::NotFound)
        );
        assert_eq!(binaries.describe(SCRIPTING_HOST_DLL_NAME), "memory:bin/SEEDScripting.dll");
    }

    /// フォルダの読み口は実在するファイルだけを「ある」とする。
    #[test]
    fn directory_binaries_follow_files() {
        let dir = std::env::temp_dir().join(format!("seed_script_binaries_test_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(dir.join(SCRIPTING_HOST_DLL_NAME), b"host").unwrap();

        let binaries = DirectoryBinaries::new(&dir);
        assert!(binaries.contains(SCRIPTING_HOST_DLL_NAME));
        assert!(!binaries.contains(PRECOMPILED_SCRIPTS_DLL_NAME));
        assert_eq!(binaries.read(SCRIPTING_HOST_DLL_NAME).unwrap(), b"host");
        assert!(summarize(&binaries).contains("SEEDUserScripts.dll=なし"));

        let _ = std::fs::remove_dir_all(&dir);
    }

    /// 配布物の中のパスは bin/ の下・区切りは / になる。
    #[test]
    fn package_bin_path_uses_bin_dir() {
        assert_eq!(package_bin_path("SEEDScripting.dll"), "bin/SEEDScripting.dll");
    }
}
