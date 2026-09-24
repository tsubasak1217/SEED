// ============================================================
//  clr_host/mod.rs — CLR（.NET）の起動
//
//  【2 つの起動経路】（どちらも hostfxr の initialize_for_runtime_config から始まる）
//    desktop  … PC。インストール済み（または実行ファイルの bin/dotnet に同梱）の .NET を nethost で探し、
//               SEEDScripting.dll をパスで読む（ScriptingHost::load。従来どおり）
//    embedded … アプリに同梱した .NET（Android）。展開済みの libhostfxr.so を直接読み、
//               SEEDScripting.dll をバイト列で読む（ScriptingHost::load_embedded）
//  関数ポインタの取り出し口を得た後（エントリポイントの取り出し）は entry_points.rs で共通。
//  どちらを使うかは App（app/script_boot.rs）が「起動材料（LaunchArgs.embedded_clr）の有無」と
//  プラットフォームの特性（PlatformTraits::script_host_source）で決める。
//
//  heap_tagging … Android の実機（arm64）で CLR を起動する前に要るヒープポインタのタグ付けの無効化
// ============================================================

#[cfg(not(target_os = "android"))]
mod desktop;
pub mod embedded;
pub(crate) mod entry_points;
#[cfg(target_os = "android")]
mod heap_tagging;

pub use embedded::EmbeddedClrHost;

/// CLR（hostfxr）の初期化済みコンテキストの型（どちらの起動経路でも同じ）。
///
/// ScriptingHost が保持する（Drop されると全マネージドオブジェクトが無効になる）。
pub(super) type ClrContext =
    netcorehost::hostfxr::HostfxrContext<netcorehost::hostfxr::InitializedForRuntimeConfig>;

/// Android には PC の探索経路（nethost・開発ビルド出力のパス）が無い。呼ばれても panic せず理由を返す。
///
/// 通常は PlatformTraits::script_host_source（Android は EmbeddedOnly）で分岐するので呼ばれない。
/// シグネチャをデスクトップ版と同じにして、呼び出し側を cfg で分けずに済ませている。
#[cfg(target_os = "android")]
impl super::ScriptingHost {
    pub fn load(
        _location: &super::ScriptingHostLocation,
    ) -> Result<std::sync::Arc<Self>, Box<dyn std::error::Error>> {
        Err("Android では PC の探索経路を使いません（同梱 .NET の起動材料 LaunchArgs.embedded_clr を渡してください。docs/android.md §17）".into())
    }
}
