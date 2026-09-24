// ============================================================
//  embedded_runtime/install.rs — 同梱 .NET を端末のファイルとして並べる（初回起動時に 1 回）
//
//  【なぜ展開するのか】
//  hostfxr / hostpolicy / CoreCLR は「dotnet-root 形式のフォルダに実ファイルが並んでいること」を前提にする
//  （shared/Microsoft.NETCore.App/<版>/ の deps.json から TPA を組み、System.Private.CoreLib.dll は
//  libcoreclr.so と同じフォルダから読む）。APK の中身はファイルとして見えないので、目録（bundle.json）どおりに
//  アプリの内部フォルダ files/dotnet/<種類>-<版>-<content_id>/ へ並べる。
//    asset          … APK の assets/seed/dotnet/<ABI>/<相対パス> を複製（BCL の DLL・deps.json・runtimeconfig）
//    native_library … nativeLibraryDir（APK の lib/<ABI>/ を OS がインストール時に展開した先）の .so を
//                     シンボリックリンク（既定）か複製（目録の native_library_mode。docs/android.md §17.4）
//
//  【2 回目以降】
//  完了の印（.seed_bundle_complete。中身は content_id）があり、asset（BCL 等）が全部揃っていれば展開し直さない。
//  .so（native_library）だけは毎回確かめ、取り出し元と食い違うものだけを置き直す（修復）。アプリを入れ直すと
//  nativeLibraryDir の場所（/data/app/~~<乱数>/…）が変わり、シンボリックリンクは切れ、.so の中身も変わり得るため。
//  58 MB の BCL を入れ直しのたびに展開し直さずに済む。
//  印は asset を並べ終えた最後に書くので、展開の途中でプロセスが殺されても、次回は最初から展開し直す。
//  展開し終えたら、同じ置き場にある古い版（別の content_id）のフォルダを消す。
//
//  読み書きのすべては引数（配布物の読み口・フォルダ）で受けるので、デスクトップの単体テストで確かめられる。
// ============================================================

use std::fmt;
use std::fs;
use std::io::{self, BufWriter};
use std::path::{Path, PathBuf};

use crate::engine::package_source::PackageSource;

use super::manifest::{BundleFile, BundleFileSource, BundleManifest, NativeLibraryMode};

/// 展開が完了したことを示す印のファイル名（中身は content_id）。
pub const COMPLETE_MARKER_FILE_NAME: &str = ".seed_bundle_complete";

/// 展開先の中で、CLR の起動に使う runtimeconfig を置くフォルダ名（中身は runtimeconfig 1 つだけ）。
///
/// runtimeconfig のフォルダは hostfxr にとって「アプリのフォルダ」になる。DLL の並ぶ files/bin/ の
/// runtimeconfig をそのまま渡すと、そのフォルダの DLL（SEEDScripting.dll・SEEDUserScripts.dll）が
/// TPA に載り、バイト列から読み込む SEEDScripting と二重になるため、空のフォルダへ写してから渡す。
pub const HOST_CONFIG_DIR_NAME: &str = "app";

/// 展開の依頼。
pub struct InstallRequest<'a> {
    /// 目録。
    pub manifest: &'a BundleManifest,
    /// 展開先の親（`files/dotnet`）。ここに `<種類>-<版>-<content_id>/` を作る。
    pub install_root: &'a Path,
    /// 配布物の読み口（Android は APK の assets/seed/ を読む ApkPackageSource）。
    pub package: &'a dyn PackageSource,
    /// 配布物のルートから見た、この ABI の同梱 .NET のフォルダ（`dotnet/<ABI>`）。
    pub bundle_dir: &'a str,
    /// nativeLibraryDir（native_library の .so の取り出し元）。取れなければ None。
    pub native_library_dir: Option<&'a Path>,
}

/// 展開の結果。
#[derive(Debug)]
pub struct InstalledRuntime {
    /// dotnet-root（hostfxr へ渡すフォルダ）。
    pub dotnet_root: PathBuf,
    /// libhostfxr.so の場所。
    pub hostfxr_path: PathBuf,
    /// 既存の展開（asset）をそのまま使ったか（false なら今回すべて展開した）。
    pub reused: bool,
    /// 書いたファイル数（全展開なら asset と native_library の合計。使い回したなら修復した .so の数）。
    pub written_files: usize,
    /// 書いた asset の合計バイト数（使い回したなら 0）。
    pub written_asset_bytes: u64,
    /// 使い回したときに置き直した .so の数（取り出し元と食い違っていたもの）。
    pub repaired_native_libraries: usize,
    /// 消した古い版のフォルダ。
    pub removed_stale: Vec<PathBuf>,
    /// 古い版を消せなかった理由（起動は続けるのでログにだけ出す）。
    pub stale_errors: Vec<String>,
}

/// 展開できなかった理由。
#[derive(Debug)]
pub enum InstallError {
    /// native_library のファイルがあるのに nativeLibraryDir が分からない。
    NativeLibraryDirUnknown,
    /// ファイル操作の失敗（どのパスで起きたか）。
    Io { path: PathBuf, source: io::Error },
    /// 書いた大きさが目録と違う（APK の中身と目録の食い違い・書き込みの途中失敗）。
    SizeMismatch { path: PathBuf, expected: u64, actual: u64 },
}

impl fmt::Display for InstallError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NativeLibraryDirUnknown => write!(
                f,
                "nativeLibraryDir が分かりません（APK の .so が展開されていない。app/build.gradle.kts の useLegacyPackaging を確認）"
            ),
            Self::Io { path, source } => write!(f, "{}: {source}", path.display()),
            Self::SizeMismatch { path, expected, actual } => {
                write!(f, "{}: 大きさが目録と違います（期待 {expected}・実際 {actual}）", path.display())
            }
        }
    }
}

impl std::error::Error for InstallError {}

/// io::Error にパスを添える。
fn io_at(path: &Path) -> impl FnOnce(io::Error) -> InstallError + '_ {
    move |source| InstallError::Io { path: path.to_path_buf(), source }
}

/// 同梱 .NET を展開する（済んでいれば何もしない）。
///
/// # 引数
/// * `request` - 展開の依頼
///
/// # 戻り値
/// dotnet-root と hostfxr の場所。失敗したら理由（呼び出し側はスクリプト無しで起動を続ける）。
pub fn install(request: &InstallRequest) -> Result<InstalledRuntime, InstallError> {
    let manifest = request.manifest;
    let target = request.install_root.join(manifest.install_dir_name());
    let hostfxr_path = target.join(&manifest.hostfxr);

    let reused = assets_complete(&target, request);
    let (written_files, written_asset_bytes, repaired_native_libraries) = if reused {
        let repaired = repair_native_libraries(&target, request)?;
        (repaired, 0, repaired)
    } else {
        let (files, bytes) = extract_all(&target, request)?;
        (files, bytes, 0)
    };

    let (removed_stale, stale_errors) = remove_stale(request.install_root, &target);
    Ok(InstalledRuntime {
        dotnet_root: target,
        hostfxr_path,
        reused,
        written_files,
        written_asset_bytes,
        repaired_native_libraries,
        removed_stale,
        stale_errors,
    })
}

/// asset（BCL・deps.json・runtimeconfig）の展開が済んでいて、全部揃っているか。
///
/// 印の中身が content_id と一致し、asset が全部あって大きさも目録と一致すること。
/// .so（native_library）はここでは見ない（repair_native_libraries が毎回確かめて直す）。
fn assets_complete(target: &Path, request: &InstallRequest) -> bool {
    let marker = target.join(COMPLETE_MARKER_FILE_NAME);
    match fs::read_to_string(&marker) {
        Ok(content) if content.trim() == request.manifest.content_id => {}
        _ => return false,
    }
    request
        .manifest
        .files
        .iter()
        .filter(|file| file.source == BundleFileSource::Asset)
        .all(|file| {
            fs::metadata(target.join(&file.path))
                .is_ok_and(|meta| file.size.is_none_or(|size| meta.len() == size))
        })
}

/// 展開済みの dotnet-root の .so（native_library）を確かめ、取り出し元と食い違うものだけを置き直す。
///
/// - 複製（copy）: 無い・大きさが取り出し元と違う → 複製し直す
/// - シンボリックリンク（symlink）: 無い・リンク先が今の nativeLibraryDir でない → 張り直す
///
/// nativeLibraryDir が分からないときは、ファイルがあれば良しとする（無ければ直せないので失敗）。
///
/// # 戻り値
/// 置き直した数。
fn repair_native_libraries(target: &Path, request: &InstallRequest) -> Result<usize, InstallError> {
    let mut repaired = 0;
    for file in request
        .manifest
        .files
        .iter()
        .filter(|file| file.source == BundleFileSource::NativeLibrary)
    {
        let dest = target.join(&file.path);
        let Some(native_dir) = request.native_library_dir else {
            if fs::metadata(&dest).is_ok() {
                continue;
            }
            return Err(InstallError::NativeLibraryDirUnknown);
        };
        let source = native_dir.join(file.file_name());
        let up_to_date = match request.manifest.native_library_mode {
            NativeLibraryMode::Copy => match (fs::symlink_metadata(&dest), fs::metadata(&source)) {
                // リンクではない実ファイルで、取り出し元と大きさが同じなら使い回す。
                (Ok(dest_meta), Ok(src_meta)) => dest_meta.is_file() && dest_meta.len() == src_meta.len(),
                _ => false,
            },
            NativeLibraryMode::Symlink => fs::read_link(&dest).is_ok_and(|link| link == source),
        };
        if up_to_date {
            continue;
        }
        // 古いファイル・切れたリンクを消してから置き直す（無ければそのまま）。
        match fs::remove_file(&dest) {
            Ok(()) => {}
            Err(e) if e.kind() == io::ErrorKind::NotFound => {}
            Err(e) => return Err(InstallError::Io { path: dest, source: e }),
        }
        if let Some(parent) = dest.parent() {
            fs::create_dir_all(parent).map_err(io_at(parent))?;
        }
        place_native_library(file, &dest, request)?;
        repaired += 1;
    }
    Ok(repaired)
}

/// 展開先を作り直して全ファイルを並べ、最後に完了の印を書く。
///
/// # 戻り値
/// (書いたファイル数, 書いた asset の合計バイト数)
fn extract_all(target: &Path, request: &InstallRequest) -> Result<(usize, u64), InstallError> {
    // 途中まで書いた前回の残り・中身の違う同名フォルダを消してから始める。
    match fs::remove_dir_all(target) {
        Ok(()) => {}
        Err(e) if e.kind() == io::ErrorKind::NotFound => {}
        Err(e) => return Err(InstallError::Io { path: target.to_path_buf(), source: e }),
    }
    fs::create_dir_all(target).map_err(io_at(target))?;

    let needs_native_dir = request
        .manifest
        .files
        .iter()
        .any(|file| file.source == BundleFileSource::NativeLibrary);
    if needs_native_dir && request.native_library_dir.is_none() {
        return Err(InstallError::NativeLibraryDirUnknown);
    }

    let mut asset_bytes = 0u64;
    for file in &request.manifest.files {
        let dest = target.join(&file.path);
        if let Some(parent) = dest.parent() {
            fs::create_dir_all(parent).map_err(io_at(parent))?;
        }
        match file.source {
            BundleFileSource::Asset => asset_bytes += copy_asset(file, &dest, request)?,
            BundleFileSource::NativeLibrary => place_native_library(file, &dest, request)?,
        }
    }

    // 印は最後（ここまで来たら全ファイルが揃っている）。
    let marker = target.join(COMPLETE_MARKER_FILE_NAME);
    fs::write(&marker, &request.manifest.content_id).map_err(io_at(&marker))?;
    Ok((request.manifest.files.len(), asset_bytes))
}

/// asset を配布物から複製する。
///
/// # 戻り値
/// 書いたバイト数（目録に大きさがあれば一致を確かめる）。
fn copy_asset(file: &BundleFile, dest: &Path, request: &InstallRequest) -> Result<u64, InstallError> {
    let relative = format!("{}/{}", request.bundle_dir, file.path);
    let mut reader = request.package.open(&relative).map_err(|source| InstallError::Io {
        path: PathBuf::from(request.package.describe(&relative)),
        source,
    })?;
    let out = fs::File::create(dest).map_err(io_at(dest))?;
    let mut writer = BufWriter::new(out);
    let written = io::copy(&mut reader, &mut writer).map_err(io_at(dest))?;
    // BufWriter の残りを確実に書き切る（Drop の書き出しはエラーを握りつぶすため明示する）。
    io::Write::flush(&mut writer).map_err(io_at(dest))?;
    if let Some(expected) = file.size {
        if written != expected {
            return Err(InstallError::SizeMismatch { path: dest.to_path_buf(), expected, actual: written });
        }
    }
    Ok(written)
}

/// native_library の .so を nativeLibraryDir から dotnet-root へ置く（複製かシンボリックリンク）。
fn place_native_library(file: &BundleFile, dest: &Path, request: &InstallRequest) -> Result<(), InstallError> {
    let native_dir = request.native_library_dir.ok_or(InstallError::NativeLibraryDirUnknown)?;
    let source = native_dir.join(file.file_name());
    match request.manifest.native_library_mode {
        NativeLibraryMode::Copy => {
            fs::copy(&source, dest).map_err(io_at(&source))?;
        }
        NativeLibraryMode::Symlink => symlink_file(&source, dest).map_err(io_at(dest))?,
    }
    Ok(())
}

/// ファイルへのシンボリックリンクを作る（Unix 系だけ。Android の実行時に使う）。
#[cfg(unix)]
fn symlink_file(source: &Path, link: &Path) -> io::Result<()> {
    std::os::unix::fs::symlink(source, link)
}

/// ファイルへのシンボリックリンクを作る（Unix 系以外では使わない。権限の要る Windows では未対応にする）。
#[cfg(not(unix))]
fn symlink_file(_source: &Path, _link: &Path) -> io::Result<()> {
    Err(io::Error::new(io::ErrorKind::Unsupported, "シンボリックリンクはこのプラットフォームでは使いません"))
}

/// 展開先の親から、今回の展開先以外（古い版・壊れた残り）を消す。
///
/// # 戻り値
/// (消したもの, 消せなかった理由)
fn remove_stale(install_root: &Path, keep: &Path) -> (Vec<PathBuf>, Vec<String>) {
    let mut removed = Vec::new();
    let mut errors = Vec::new();
    let Ok(entries) = fs::read_dir(install_root) else {
        return (removed, errors);
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path == keep {
            continue;
        }
        let result = match entry.file_type() {
            Ok(kind) if kind.is_dir() => fs::remove_dir_all(&path),
            _ => fs::remove_file(&path),
        };
        match result {
            Ok(()) => removed.push(path),
            Err(e) => errors.push(format!("{}: {e}", path.display())),
        }
    }
    (removed, errors)
}

/// CLR の起動に使う runtimeconfig を、展開先の `app/` へ書く（中身が同じなら書かない）。
///
/// # 引数
/// * `dotnet_root` - 展開先（`install` の戻り値の dotnet_root）
/// * `file_name`   - runtimeconfig のファイル名（`SEEDScripting.runtimeconfig.json`）
/// * `bytes`       - runtimeconfig の中身（スクリプトの DLL と同じ置き場のもの）
///
/// # 戻り値
/// 書いた（または同じ中身が既にあった）runtimeconfig のパス。
pub fn write_host_runtime_config(dotnet_root: &Path, file_name: &str, bytes: &[u8]) -> io::Result<PathBuf> {
    let dir = dotnet_root.join(HOST_CONFIG_DIR_NAME);
    fs::create_dir_all(&dir)?;
    let path = dir.join(file_name);
    if fs::read(&path).is_ok_and(|existing| existing == bytes) {
        return Ok(path);
    }
    fs::write(&path, bytes)?;
    Ok(path)
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::package_source::MemoryPackage;

    /// テストごとに独立した作業フォルダ（テスト名とプロセス ID で分ける）。
    fn work_dir(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("seed_embedded_install_{name}_{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    /// 目録を組み立てる（asset 2 つ・native_library 2 つ）。.so の置き方は複製（Windows でも走らせられる方）。
    fn manifest(content_id: &str) -> BundleManifest {
        let text = format!(
            r#"{{
            "format_version": 1, "runtime": "coreclr", "framework": "Microsoft.NETCore.App",
            "version": "10.0.12", "abi": "x86_64", "content_id": "{content_id}",
            "native_library_mode": "copy",
            "hostfxr": "host/fxr/10.0.12/libhostfxr.so",
            "files": [
                {{ "path": "host/fxr/10.0.12/libhostfxr.so", "source": "native_library" }},
                {{ "path": "shared/Microsoft.NETCore.App/10.0.12/libcoreclr.so", "source": "native_library" }},
                {{ "path": "shared/Microsoft.NETCore.App/10.0.12/System.Private.CoreLib.dll", "source": "asset", "size": 8 }},
                {{ "path": "shared/Microsoft.NETCore.App/10.0.12/Microsoft.NETCore.App.deps.json", "source": "asset", "size": 2 }}
            ]}}"#
        );
        BundleManifest::parse(text.as_bytes()).unwrap()
    }

    /// 目録に対応する APK の assets を模した配布物。
    fn package() -> MemoryPackage {
        MemoryPackage::new(&[
            ("dotnet/x86_64/shared/Microsoft.NETCore.App/10.0.12/System.Private.CoreLib.dll", b"corelib!"),
            ("dotnet/x86_64/shared/Microsoft.NETCore.App/10.0.12/Microsoft.NETCore.App.deps.json", b"{}"),
        ])
    }

    /// nativeLibraryDir を模したフォルダ（.so 2 つ）。
    fn native_dir(root: &Path) -> PathBuf {
        let dir = root.join("nativeLibraryDir");
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join("libhostfxr.so"), b"fxr").unwrap();
        fs::write(dir.join("libcoreclr.so"), b"coreclr").unwrap();
        dir
    }

    /// 初回は全ファイルを dotnet-root 形式で並べ、印を書く。2 回目は何も書かずに使い回す。
    #[test]
    fn installs_once_then_reuses() {
        let root = work_dir("once");
        let natives = native_dir(&root);
        let manifest = manifest("aaaa");
        let package = package();
        let install_root = root.join("files").join("dotnet");
        let request = InstallRequest {
            manifest: &manifest,
            install_root: &install_root,
            package: &package,
            bundle_dir: "dotnet/x86_64",
            native_library_dir: Some(&natives),
        };

        let first = install(&request).expect("展開できるべき");
        assert!(!first.reused);
        assert_eq!(first.written_files, 4);
        assert_eq!(first.written_asset_bytes, 10);
        assert_eq!(first.dotnet_root, install_root.join("coreclr-10.0.12-aaaa"));
        assert_eq!(fs::read(&first.hostfxr_path).unwrap(), b"fxr");
        let corelib = first.dotnet_root.join("shared/Microsoft.NETCore.App/10.0.12/System.Private.CoreLib.dll");
        assert_eq!(fs::read(corelib).unwrap(), b"corelib!");
        assert_eq!(
            fs::read_to_string(first.dotnet_root.join(COMPLETE_MARKER_FILE_NAME)).unwrap(),
            "aaaa"
        );

        let second = install(&request).expect("2 回目も成功するべき");
        assert!(second.reused, "揃っているので使い回すべき");
        assert_eq!(second.written_files, 0);
        assert_eq!(second.repaired_native_libraries, 0);

        let _ = fs::remove_dir_all(&root);
    }

    /// asset が欠けていれば（印があっても）全部展開し直す。
    #[test]
    fn reinstalls_when_asset_missing() {
        let root = work_dir("missing_asset");
        let natives = native_dir(&root);
        let manifest = manifest("bbbb");
        let package = package();
        let install_root = root.join("dotnet");
        let request = InstallRequest {
            manifest: &manifest,
            install_root: &install_root,
            package: &package,
            bundle_dir: "dotnet/x86_64",
            native_library_dir: Some(&natives),
        };
        let first = install(&request).unwrap();
        let corelib = first.dotnet_root.join("shared/Microsoft.NETCore.App/10.0.12/System.Private.CoreLib.dll");
        fs::remove_file(&corelib).unwrap();

        let again = install(&request).unwrap();
        assert!(!again.reused, "asset が欠けていたので展開し直すべき");
        assert_eq!(again.written_files, 4);
        assert_eq!(fs::read(corelib).unwrap(), b"corelib!");
        let _ = fs::remove_dir_all(&root);
    }

    /// .so だけが欠けていれば、asset は使い回して .so だけを置き直す。
    #[test]
    fn repairs_missing_native_library_only() {
        let root = work_dir("missing_native");
        let natives = native_dir(&root);
        let manifest = manifest("bbbc");
        let package = package();
        let install_root = root.join("dotnet");
        let request = InstallRequest {
            manifest: &manifest,
            install_root: &install_root,
            package: &package,
            bundle_dir: "dotnet/x86_64",
            native_library_dir: Some(&natives),
        };
        let first = install(&request).unwrap();
        fs::remove_file(first.dotnet_root.join("shared/Microsoft.NETCore.App/10.0.12/libcoreclr.so")).unwrap();

        let again = install(&request).unwrap();
        assert!(again.reused, "asset は揃っているので使い回すべき");
        assert_eq!(again.repaired_native_libraries, 1);
        assert_eq!(again.written_asset_bytes, 0);
        assert!(again.dotnet_root.join("shared/Microsoft.NETCore.App/10.0.12/libcoreclr.so").is_file());
        let _ = fs::remove_dir_all(&root);
    }

    /// 取り出し元の .so が変わった（アプリの更新）なら、その .so だけを置き直す。
    #[test]
    fn repairs_changed_native_library() {
        let root = work_dir("native_changed");
        let natives = native_dir(&root);
        let manifest = manifest("cccc");
        let package = package();
        let install_root = root.join("dotnet");
        let request = InstallRequest {
            manifest: &manifest,
            install_root: &install_root,
            package: &package,
            bundle_dir: "dotnet/x86_64",
            native_library_dir: Some(&natives),
        };
        install(&request).unwrap();
        fs::write(natives.join("libcoreclr.so"), b"coreclr-updated").unwrap();

        let again = install(&request).unwrap();
        assert!(again.reused);
        assert_eq!(again.repaired_native_libraries, 1);
        assert_eq!(
            fs::read(again.dotnet_root.join("shared/Microsoft.NETCore.App/10.0.12/libcoreclr.so")).unwrap(),
            b"coreclr-updated"
        );
        let _ = fs::remove_dir_all(&root);
    }

    /// シンボリックリンクの置き方: リンクを張り、nativeLibraryDir が変わったら（アプリの入れ直し）張り直す。
    #[cfg(unix)]
    #[test]
    fn symlink_mode_relinks_when_native_dir_moves() {
        let root = work_dir("symlink");
        let natives = native_dir(&root);
        let text = r#"{
            "format_version": 1, "runtime": "coreclr", "framework": "Microsoft.NETCore.App",
            "version": "10.0.12", "abi": "x86_64", "content_id": "dddd", "native_library_mode": "symlink",
            "hostfxr": "host/fxr/10.0.12/libhostfxr.so",
            "files": [ { "path": "host/fxr/10.0.12/libhostfxr.so", "source": "native_library" } ]
        }"#;
        let manifest = BundleManifest::parse(text.as_bytes()).unwrap();
        let package = package();
        let install_root = root.join("dotnet");
        let first = install(&InstallRequest {
            manifest: &manifest,
            install_root: &install_root,
            package: &package,
            bundle_dir: "dotnet/x86_64",
            native_library_dir: Some(&natives),
        })
        .unwrap();
        assert_eq!(fs::read_link(&first.hostfxr_path).unwrap(), natives.join("libhostfxr.so"));

        // 入れ直しで nativeLibraryDir の場所が変わった
        let moved = root.join("nativeLibraryDir2");
        fs::rename(&natives, &moved).unwrap();
        let again = install(&InstallRequest {
            manifest: &manifest,
            install_root: &install_root,
            package: &package,
            bundle_dir: "dotnet/x86_64",
            native_library_dir: Some(&moved),
        })
        .unwrap();
        assert!(again.reused);
        assert_eq!(again.repaired_native_libraries, 1);
        assert_eq!(fs::read_link(&again.hostfxr_path).unwrap(), moved.join("libhostfxr.so"));
        let _ = fs::remove_dir_all(&root);
    }

    /// 中身の識別子が変われば別のフォルダへ展開し、古い版のフォルダは消す。
    #[test]
    fn new_content_id_replaces_old_install() {
        let root = work_dir("stale");
        let natives = native_dir(&root);
        let package = package();
        let install_root = root.join("dotnet");
        let old = manifest("1111");
        install(&InstallRequest {
            manifest: &old,
            install_root: &install_root,
            package: &package,
            bundle_dir: "dotnet/x86_64",
            native_library_dir: Some(&natives),
        })
        .unwrap();

        let new = manifest("2222");
        let result = install(&InstallRequest {
            manifest: &new,
            install_root: &install_root,
            package: &package,
            bundle_dir: "dotnet/x86_64",
            native_library_dir: Some(&natives),
        })
        .unwrap();
        assert!(!result.reused);
        assert_eq!(result.removed_stale, vec![install_root.join("coreclr-10.0.12-1111")]);
        assert!(!install_root.join("coreclr-10.0.12-1111").exists());
        assert!(install_root.join("coreclr-10.0.12-2222").is_dir());
        let _ = fs::remove_dir_all(&root);
    }

    /// 印の無い（途中で止まった）展開は、最初からやり直す。
    #[test]
    fn interrupted_install_is_redone() {
        let root = work_dir("interrupted");
        let natives = native_dir(&root);
        let manifest = manifest("dddd");
        let package = package();
        let install_root = root.join("dotnet");
        let request = InstallRequest {
            manifest: &manifest,
            install_root: &install_root,
            package: &package,
            bundle_dir: "dotnet/x86_64",
            native_library_dir: Some(&natives),
        };
        let first = install(&request).unwrap();
        fs::remove_file(first.dotnet_root.join(COMPLETE_MARKER_FILE_NAME)).unwrap();
        assert!(!install(&request).unwrap().reused);
        let _ = fs::remove_dir_all(&root);
    }

    /// 配布物の中身が目録の大きさと違えば失敗し、印を書かない（次回もやり直す）。
    #[test]
    fn size_mismatch_fails_without_marker() {
        let root = work_dir("size");
        let natives = native_dir(&root);
        let manifest = manifest("eeee");
        let package = MemoryPackage::new(&[
            ("dotnet/x86_64/shared/Microsoft.NETCore.App/10.0.12/System.Private.CoreLib.dll", b"short"),
            ("dotnet/x86_64/shared/Microsoft.NETCore.App/10.0.12/Microsoft.NETCore.App.deps.json", b"{}"),
        ]);
        let install_root = root.join("dotnet");
        let err = install(&InstallRequest {
            manifest: &manifest,
            install_root: &install_root,
            package: &package,
            bundle_dir: "dotnet/x86_64",
            native_library_dir: Some(&natives),
        })
        .expect_err("大きさが違うので失敗するべき");
        assert!(matches!(err, InstallError::SizeMismatch { expected: 8, actual: 5, .. }), "{err}");
        assert!(!install_root.join("coreclr-10.0.12-eeee").join(COMPLETE_MARKER_FILE_NAME).exists());
        let _ = fs::remove_dir_all(&root);
    }

    /// native_library があるのに nativeLibraryDir が分からなければ失敗する。
    #[test]
    fn native_library_dir_is_required() {
        let root = work_dir("no_native");
        let manifest = manifest("ffff");
        let package = package();
        let err = install(&InstallRequest {
            manifest: &manifest,
            install_root: &root.join("dotnet"),
            package: &package,
            bundle_dir: "dotnet/x86_64",
            native_library_dir: None,
        })
        .expect_err("nativeLibraryDir が無いので失敗するべき");
        assert!(matches!(err, InstallError::NativeLibraryDirUnknown));
        let _ = fs::remove_dir_all(&root);
    }

    /// runtimeconfig は app/ へ書き、同じ中身なら書き直さない。
    #[test]
    fn host_runtime_config_is_written_once() {
        let root = work_dir("config");
        let path = write_host_runtime_config(&root, "SEEDScripting.runtimeconfig.json", b"{\"a\":1}").unwrap();
        assert_eq!(path, root.join(HOST_CONFIG_DIR_NAME).join("SEEDScripting.runtimeconfig.json"));
        let modified = fs::metadata(&path).unwrap().modified().unwrap();
        let again = write_host_runtime_config(&root, "SEEDScripting.runtimeconfig.json", b"{\"a\":1}").unwrap();
        assert_eq!(again, path);
        assert_eq!(fs::metadata(&path).unwrap().modified().unwrap(), modified, "同じ中身なら書かない");
        write_host_runtime_config(&root, "SEEDScripting.runtimeconfig.json", b"{\"a\":2}").unwrap();
        assert_eq!(fs::read(&path).unwrap(), b"{\"a\":2}");
        let _ = fs::remove_dir_all(&root);
    }
}
