// ============================================================
//  apk_package/mod.rs — APK 内の配布物の読み口（ApkPackageSource）
//
//  【APK 内のレイアウト】
//  Windows のパッケージ出力（{ゲーム名}/ の中身。実行ファイルを除く）と同じ相対構成を
//  APK の assets/seed/ に置く。構成の正典はエンジンの core::package_layout。
//
//    assets/seed/                  … 配布物のルート（APK_PACKAGE_ROOT）
//      assets.pak                  … アセット（SeedPak ツール／パッケージ化ウィンドウの出力と同じもの）
//      assets/<相対パス>            … PAK に入れずに置くアセット（任意。PAK に無いときのフォールバック先）
//
//  ソース側は runtime/android/app/src/main/assets/seed/（生成物・追跡しない）。Gradle が APK の
//  assets/seed/ へ詰める。pak は app/build.gradle.kts の noCompress で非圧縮のまま格納する
//  （圧縮されていると後ろ向きの Seek のたびに先頭から展開し直すことになり遅い）。
//  置くのは SeedAndroid の --project（build_and_run.ps1 -ProjectDir。SeedPak で pak を作って置く）。
//
//  【読み方】
//  アプリ全体の AAssetManager（スレッド安全）から「seed/<相対パス>」を開き、
//  ApkAsset（Send にした包み）として返す。エンジンの PakReader がそのまま Read + Seek する。
//  APK 内のパスは大文字小文字を区別する（PAK のエントリ検索だけは区別しない）。
// ============================================================

mod apk_asset;

use std::ffi::CString;
use std::io;

use seed_engine::engine::core::package_layout;
use seed_engine::engine::package_source::PackageSource;
use seed_engine::engine::pak::PakSource;
use winit::platform::android::activity::ndk::asset::AssetManager;

use apk_asset::ApkAsset;

/// APK の assets/ の中で、配布物のルートにするフォルダ名。
///
/// SeedAndroid の置き場（app/src/main/assets/seed。editor/src/Android/Common/AndroidRuntimeContract.cs の
/// ApkPackageRootName）と一致させること。
pub const APK_PACKAGE_ROOT: &str = "seed";

/// APK 内の assets.pak を調べた結果（起動モードの判定とログ用）。
pub struct PakProbe {
    /// pak のバイト数。
    pub size: u64,
    /// 非圧縮で格納されているときの、APK ファイル内での開始位置（圧縮されていれば None）。
    ///
    /// 非圧縮かどうかは「ファイル記述子で直接読めるか」（AAsset_openFileDescriptor64 の成否）で判定する。
    pub uncompressed_offset: Option<u64>,
}

/// APK の assets/seed/ を配布物のルートとして読む読み口。
pub struct ApkPackageSource {
    /// アプリ全体の AssetManager（android-activity がプロセスの終わりまで保持する。スレッド安全）。
    manager: AssetManager,
}

impl ApkPackageSource {
    /// AssetManager から読み口を作る。
    ///
    /// # 引数
    /// * `manager` - `AndroidApp::asset_manager()` の戻り値
    pub fn new(manager: AssetManager) -> Self {
        Self { manager }
    }

    /// APK に assets.pak があるかを調べ、あれば大きさと格納のされ方を返す（無ければ None）。
    pub fn probe_pak(&self) -> Option<PakProbe> {
        let path = apk_path_of(package_layout::PAK_FILE_NAME);
        let asset = self.manager.open(&CString::new(path).ok()?)?;
        let size = asset.length() as u64;
        // 開いた記述子はすぐ閉じる（判定に使うだけ。読み出しは ApkAsset の Read + Seek で行う）。
        let uncompressed_offset = asset.open_file_descriptor().ok().map(|fd| fd.offset as u64);
        Some(PakProbe { size, uncompressed_offset })
    }
}

impl PackageSource for ApkPackageSource {
    fn open(&self, relative: &str) -> io::Result<Box<dyn PakSource>> {
        let path = apk_path_of(relative);
        let c_path = CString::new(path.clone())
            .map_err(|e| io::Error::new(io::ErrorKind::InvalidInput, e))?;
        match self.manager.open(&c_path) {
            Some(asset) => Ok(Box::new(ApkAsset::new(asset))),
            None => Err(io::Error::new(
                io::ErrorKind::NotFound,
                format!("APK 内に見つかりません: {path}"),
            )),
        }
    }

    fn describe(&self, relative: &str) -> String {
        format!("apk:{}", apk_path_of(relative))
    }
}

/// 配布物のルートからの相対パスを、AAssetManager に渡す APK 内のパス（`seed/<相対パス>`）にする【純関数】。
///
/// 区切りは `/` に揃え、先頭の `/` は落とす（AAssetManager は先頭 `/` のパスを開けない）。
fn apk_path_of(relative: &str) -> String {
    let rel = relative.replace('\\', "/");
    format!("{APK_PACKAGE_ROOT}/{}", rel.trim_start_matches('/'))
}
