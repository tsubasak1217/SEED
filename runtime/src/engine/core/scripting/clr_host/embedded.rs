// ============================================================
//  clr_host/embedded.rs — アプリに同梱した .NET（埋め込み CLR）の起動（Android）
//
//  【流れ】（docs/android.md §11.2 のスパイクで Android の CoreCLR / Mono の両方で動いた経路）
//    0. （Android）ヒープポインタのタグ付けを無効にする（heap_tagging.rs。実機 arm64 の coreclr_initialize の SIGSEGV 対策）
//    1. Hostfxr::load_from_path（展開した dotnet-root の host/fxr/<版>/libhostfxr.so。nethost は使わない）
//    2. initialize_for_runtime_config_with_dotnet_root
//         runtimeconfig … SEEDScripting.runtimeconfig.json（デスクトップと同じファイル）を空のフォルダへ写したもの
//         dotnet_root   … 明示する（hostfxr が自分の場所から推す dotnet-root はシンボリックリンク越しだと当てにならない）
//    3. set_runtime_property_value（目録の runtime_properties。System.Globalization.Invariant=true 等。
//       runtimeconfig を書き換えないので、同じ runtimeconfig を使うデスクトップには影響しない）
//    4. get_delegate_loader（ここでランタイム本体が起動する＝coreclr_initialize）
//    5. load_assembly_from_bytes（SEEDScripting.dll を Default の AssemblyLoadContext へ。
//       パス指定の get_delegate_loader_for_assembly は Android 版 CoreCLR で PlatformNotSupportedException）
//    6. エントリポイントの取り出し（entry_points.rs。デスクトップと共通。以降は既存コードと同じ経路）
//
//  起動材料（EmbeddedClrHost）は Android の糊（runtime/android/native の dotnet_runtime）が APK から
//  展開して作り、LaunchArgs.embedded_clr で渡す。このファイル自体はプラットフォームに依存しない
//  （デスクトップでも起動材料を渡せば同じ経路で動く）。
// ============================================================

use std::ffi::OsStr;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant};

use netcorehost::{hostfxr::Hostfxr, pdcstring::PdCString};

use super::super::script_binaries::ScriptBinarySource;
use super::super::{ScriptingHost, SCRIPTING_HOST_DLL_NAME};
use super::entry_points::assemble_scripting_host;

/// 同梱 .NET（埋め込み CLR）の起動材料。
pub struct EmbeddedClrHost {
    /// libhostfxr.so（展開した dotnet-root の host/fxr/<版>/）。
    pub hostfxr_path: PathBuf,
    /// dotnet-root（`shared/Microsoft.NETCore.App/<版>/` を持つフォルダ）。
    pub dotnet_root: PathBuf,
    /// CLR の初期化に使う runtimeconfig（`SEEDScripting.runtimeconfig.json` を空のフォルダへ写したもの）。
    pub runtime_config_path: PathBuf,
    /// 起動前に設定するランタイムプロパティ（名前, 値）。
    pub runtime_properties: Vec<(String, String)>,
    /// スクリプトの DLL 一式の読み口（SEEDScripting.dll・SEEDUserScripts.dll）。
    pub binaries: Arc<dyn ScriptBinarySource>,
    /// 置き場の候補（優先順。`binaries` はこの中から選んだもの）。実行中の差し替え（RELOAD_SCRIPTS）で、起動の後に
    /// files/bin/ へ送られた DLL を選び直すのに使う（scripting/script_reload.rs。§23）。空なら `binaries` だけを候補にする。
    pub reload_candidates: Vec<Arc<dyn ScriptBinarySource>>,
    /// ログ用の説明（例 `coreclr 10.0.12（x86_64）`）。
    pub label: String,
}

/// 起動の各段階の所要時間（ログ用）。
struct BootTimings {
    /// hostfxr の読み込み（dlopen）。
    load_hostfxr: Duration,
    /// runtimeconfig の解決とランタイムプロパティの設定。
    initialize: Duration,
    /// ランタイム本体の起動（coreclr_initialize）。
    start_runtime: Duration,
    /// SEEDScripting.dll の読み取りとロード。
    load_host_assembly: Duration,
    /// エントリポイントの取り出し。
    resolve_entry_points: Duration,
}

impl BootTimings {
    /// 1 行の要約（ミリ秒）。
    fn describe(&self) -> String {
        let ms = |d: Duration| d.as_secs_f64() * MILLIS_PER_SECOND;
        let total = self.load_hostfxr + self.initialize + self.start_runtime + self.load_host_assembly + self.resolve_entry_points;
        format!(
            "hostfxr 読込 {:.1} ms / 初期化 {:.1} ms / ランタイム起動 {:.1} ms / SEEDScripting 読込 {:.1} ms / 関数の取り出し {:.1} ms / 合計 {:.1} ms",
            ms(self.load_hostfxr),
            ms(self.initialize),
            ms(self.start_runtime),
            ms(self.load_host_assembly),
            ms(self.resolve_entry_points),
            ms(total),
        )
    }
}

/// 秒 → ミリ秒。
const MILLIS_PER_SECOND: f64 = 1000.0;

/// 区間の計測（開始時刻から今までを返し、開始時刻を今へ進める）。
fn lap(mark: &mut Instant) -> Duration {
    let now = Instant::now();
    let elapsed = now - *mark;
    *mark = now;
    elapsed
}

impl ScriptingHost {
    /// 同梱 .NET で CLR を起動して ScriptingHost を構築する。
    ///
    /// 失敗したら理由を返す（呼び出し側はスクリプト無しで起動を続ける）。
    ///
    /// # 引数
    /// * `host` - 起動材料（Android の糊が APK から用意したもの）
    pub fn load_embedded(host: &EmbeddedClrHost) -> Result<Arc<Self>, Box<dyn std::error::Error>> {
        eprintln!(
            "[SEED DOTNET] CLR を起動します: {}  dotnet-root={}  runtimeconfig={}  スクリプト={}",
            host.label,
            host.dotnet_root.display(),
            host.runtime_config_path.display(),
            host.binaries.describe(SCRIPTING_HOST_DLL_NAME),
        );

        // 0. 実機（arm64・Android 11 以降）のヒープポインタのタグ付けを止める（CoreCLR / Mono の起動前に 1 回）。
        #[cfg(target_os = "android")]
        super::heap_tagging::disable_before_clr();

        let mut mark = Instant::now();

        // 1. hostfxr を読み込む。
        let hostfxr = Hostfxr::load_from_path(&host.hostfxr_path)?;
        let load_hostfxr = lap(&mut mark);

        // 2〜3. runtimeconfig の解決（フレームワークの選択）とランタイムプロパティの設定（まだランタイムは起動しない）。
        let mut context = hostfxr.initialize_for_runtime_config_with_dotnet_root(
            PdCString::from_os_str(host.runtime_config_path.as_os_str())?,
            PdCString::from_os_str(host.dotnet_root.as_os_str())?,
        )?;
        for (name, value) in &host.runtime_properties {
            context.set_runtime_property_value(
                PdCString::from_os_str(OsStr::new(name))?,
                PdCString::from_os_str(OsStr::new(value))?,
            )?;
            eprintln!("[SEED DOTNET] ランタイムプロパティ: {name}={value}");
        }
        let initialize = lap(&mut mark);

        // 4. ランタイム本体を起動する（最初のランタイムデリゲートの取得で coreclr_initialize が走る）。
        let loader = context.get_delegate_loader()?;
        let start_runtime = lap(&mut mark);

        // 5. スクリプトホストをバイト列から Default の AssemblyLoadContext へ読む。
        //    シンボル（PDB）は同梱しないので渡さない。
        let host_assembly = host.binaries.read(SCRIPTING_HOST_DLL_NAME)?;
        let no_symbols: &[u8] = &[];
        context.load_assembly_from_bytes(&host_assembly, no_symbols)?;
        let load_host_assembly = lap(&mut mark);

        // 6. エントリポイントの取り出し（デスクトップと共通）。
        let scripting_host = assemble_scripting_host!(context, loader);
        let resolve_entry_points = lap(&mut mark);

        let timings = BootTimings { load_hostfxr, initialize, start_runtime, load_host_assembly, resolve_entry_points };
        eprintln!(
            "[SEED DOTNET] CLR を起動しました: {}（SEEDScripting.dll {} KiB）  {}",
            host.label,
            host_assembly.len() / BYTES_PER_KIB,
            timings.describe()
        );
        Ok(Arc::new(scripting_host))
    }
}

/// バイト → KiB。
const BYTES_PER_KIB: usize = 1024;
