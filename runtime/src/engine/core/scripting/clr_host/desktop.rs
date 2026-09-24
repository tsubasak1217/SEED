// ============================================================
//  clr_host/desktop.rs — デスクトップ（Windows）の CLR 起動
//
//  【流れ】（従来の ScriptingHost::load をそのまま移したもの。振る舞いは変えていない）
//    1. SEEDScripting.dll の場所（mod.rs の resolve_dll_path が決める）。開発ビルド出力ならシャドウコピー
//    2. hostfxr を探す: 実行ファイルの bin/dotnet/host/fxr があれば同梱 .NET、無ければインストール済みの .NET（nethost）
//    3. initialize_for_runtime_config（SEEDScripting.runtimeconfig.json）
//    4. get_delegate_loader_for_assembly（パス指定。SEEDScripting.dll を専用の AssemblyLoadContext へ読み、
//       deps.json から Roslyn 等の依存を解決する。その場コンパイルが Roslyn を使うため、この経路のまま）
//    5. エントリポイントの取り出し（entry_points.rs。同梱 .NET と共通）
//
//  Android はこの経路を使わない（パス指定の読み込みは Android 版 CoreCLR で PlatformNotSupportedException になり、
//  nethost も無い）。Android は clr_host/embedded.rs。
// ============================================================

use std::path::{Path, PathBuf};
use std::sync::Arc;

use netcorehost::{nethost, pdcstring::PdCString};

use super::super::{bundled_dotnet_root, ScriptingHost, ScriptingHostLocation};
use super::entry_points::assemble_scripting_host;

impl ScriptingHost {
    /// 探索結果から CLR を初期化して ScriptingHost を構築する。
    ///
    /// ## シャドウコピーを掛ける／掛けないの判断
    /// 開発ビルド出力（`scripting/bin/...`）の DLL を直接ロードすると
    /// プロセス実行中ずっとファイルがロックされ、エディタ/VS からの再ビルドが
    /// 「別プロセスが使用中」で失敗する。そのため開発時だけ DLL 一式を
    /// プロセス専用のテンポラリへコピーし、そのコピーをロードする。
    ///
    /// 一方パッケージ版では DLL は `{exe のフォルダ}/bin/` にあり、そこには
    /// 同梱 .NET ランタイム（`bin/dotnet/`。実測 75 MB 超）も同居する。
    /// シャドウコピーはフォルダ直下の全ファイルを写すため、そのまま掛けると
    /// 起動のたびに配布物の副次ファイルをテンポラリへ複製することになる。
    /// 配布物は再ビルドされないのでロックしても実害が無く、コピーは不要。
    pub fn load(location: &ScriptingHostLocation) -> Result<Arc<Self>, Box<dyn std::error::Error>> {
        let dll_path = location.dll_path.as_path();

        // 開発ビルド出力のときだけシャドウコピー（失敗時は元のパスにフォールバック）
        let load_dll = if location.is_dev_build_output {
            Self::shadow_copy(dll_path).unwrap_or_else(|_| dll_path.to_path_buf())
        } else {
            dll_path.to_path_buf()
        };
        let config_path = load_dll.with_extension("runtimeconfig.json");

        // ── CLR（hostfxr）の探索先を決める ──
        // 実行ファイルの bin/ に dotnet/ を同梱していればそこを .NET ルートとして使い、
        // 無ければ PC にインストール済みの .NET を使う（従来どおり）。
        // どちらを使ったかは配布先での切り分けに直結するので必ず 1 行残す。
        let exe_dir = std::env::current_exe()
            .ok()
            .and_then(|exe| exe.parent().map(Path::to_path_buf));
        let bundled_root = bundled_dotnet_root(exe_dir.as_deref(), &|path| path.is_dir());

        let hostfxr = match &bundled_root {
            Some(root) => {
                eprintln!("[SEED] dotnet root: bundled {}", root.display());
                nethost::load_hostfxr_with_dotnet_root(PdCString::from_os_str(root.as_os_str())?)?
            }
            None => {
                eprintln!("[SEED] dotnet root: global");
                nethost::load_hostfxr()?
            }
        };

        let context = hostfxr.initialize_for_runtime_config(
            PdCString::from_os_str(config_path.as_os_str())?,
        )?;

        let loader = context.get_delegate_loader_for_assembly(
            PdCString::from_os_str(load_dll.as_os_str())?,
        )?;

        Ok(Arc::new(assemble_scripting_host!(context, loader)))
    }

    /// DLL とその関連ファイル一式を、プロセス専用のテンポラリディレクトリへ
    /// コピーし、コピー後の DLL パスを返す。
    ///
    /// ビルド出力ディレクトリをロックしないためのシャドウコピー。
    /// hostfxr は runtimeconfig.json / deps.json / 依存 DLL を DLL と同じ
    /// フォルダから解決するため、ディレクトリ内の全ファイルをコピーする。
    fn shadow_copy(dll_path: &Path) -> std::io::Result<PathBuf> {
        use std::fs;

        let src_dir = dll_path.parent().ok_or_else(|| {
            std::io::Error::new(std::io::ErrorKind::NotFound, "DLL の親ディレクトリが取得できません")
        })?;
        let file_name = dll_path.file_name().ok_or_else(|| {
            std::io::Error::new(std::io::ErrorKind::NotFound, "DLL ファイル名が取得できません")
        })?;

        // プロセス ID 単位のシャドウディレクトリ（多重起動でも衝突しない）
        let shadow_dir = std::env::temp_dir()
            .join("SEED_scripting_shadow")
            .join(std::process::id().to_string());

        // 既存の残骸を掃除してから作り直す
        let _ = fs::remove_dir_all(&shadow_dir);
        fs::create_dir_all(&shadow_dir)?;

        // ソースディレクトリ直下の全ファイルをコピーする
        for entry in fs::read_dir(src_dir)? {
            let entry = entry?;
            if !entry.file_type()?.is_file() { continue; }
            let dst = shadow_dir.join(entry.file_name());
            fs::copy(entry.path(), dst)?;
        }

        Ok(shadow_dir.join(file_name))
    }
}
