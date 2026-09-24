// ============================================================
//  dotnet_runtime/native_library_dir.rs — nativeLibraryDir（APK の .so の展開先）を求める
//
//  【何に使うか】
//  同梱 .NET の .so（libhostfxr.so・libcoreclr.so など）は APK の lib/<ABI>/ に入り、インストール時に OS が
//  nativeLibraryDir（/data/app/~~…/com.seedengine.runtime-…/lib/<arch>/）へ展開する
//  （app/build.gradle.kts の useLegacyPackaging = true。既定の「APK から直接読み込む」形では展開されない）。
//  ランタイムは files/dotnet/ の dotnet-root からそこの .so へシンボリックリンクを張る（既定。または複製する。
//  embedded_runtime::install）。
//
//  【求め方】
//  libSEED.so 自身も同じ nativeLibraryDir にあるので、この .so の中の関数のアドレスを dladdr に渡し、
//  読み込まれたファイルのパス（bionic は実パスを返す）の親フォルダを取る。Java（JNI）を呼ばずに済む。
//  パスに "!/" が入るのは .so を APK から直接読み込んでいる（展開されていない）とき。
// ============================================================

use std::ffi::{CStr, c_void};
use std::path::PathBuf;

/// APK の中の .so を直接読み込んだときのパスの区切り（`…/base.apk!/lib/<ABI>/libSEED.so`）。
const APK_ZIP_PATH_SEPARATOR: &str = "!/";

/// dladdr に渡す目印（この .so の中にある関数ならどれでもよい）。
extern "C" fn anchor() {}

/// libSEED.so のあるフォルダ（＝nativeLibraryDir）を返す。
///
/// # 戻り値
/// 求められなければ理由（ログに出す）。
pub fn find() -> Result<PathBuf, String> {
    // SAFETY: Dl_info はすべて整数とポインタで、ゼロ埋めは有効な初期値。
    let mut info: libc::Dl_info = unsafe { std::mem::zeroed() };
    // SAFETY: anchor はこの .so の中の関数で、dladdr は info へ書くだけ（dli_fname はローダーが持つ文字列）。
    let found = unsafe { libc::dladdr(anchor as *const c_void, &mut info) };
    if found == 0 || info.dli_fname.is_null() {
        return Err("dladdr で libSEED.so の場所を取得できませんでした".into());
    }
    // SAFETY: dladdr が成功したとき dli_fname は NUL 終端の文字列（.so が読み込まれている間有効）。
    let path = unsafe { CStr::from_ptr(info.dli_fname) }.to_string_lossy().into_owned();
    if path.contains(APK_ZIP_PATH_SEPARATOR) {
        return Err(format!(
            "libSEED.so が APK から直接読み込まれています（{path}）。.so がファイルとして展開されていないため、\
             同梱 .NET の .so を写せません。app/build.gradle.kts の packaging.jniLibs.useLegacyPackaging = true を確認してください"
        ));
    }
    PathBuf::from(&path)
        .parent()
        .map(PathBuf::from)
        .ok_or_else(|| format!("libSEED.so のパスに親フォルダがありません: {path}"))
}
