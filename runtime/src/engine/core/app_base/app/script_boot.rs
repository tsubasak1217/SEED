// ============================================================
//  app/script_boot.rs — 起動時のスクリプトホスト（CLR）の用意とユーザースクリプトの読み込み
//
//  【スクリプトホスト（CLR ＋ SEEDScripting.dll）の用意】データの有無とプラットフォームの特性で決める。
//    1. LaunchArgs.embedded_clr がある … 同梱 .NET で起動する（Android。ScriptingHost::load_embedded）
//    2. 無い・PlatformTraits::script_host_source = SearchFiles（PC）
//         … 開発ビルド出力か実行ファイルの bin/ の SEEDScripting.dll を探して起動する（ScriptingHost::load。従来どおり）
//    3. 無い・EmbeddedOnly（Android）… 糊が同梱 .NET を用意できなかった。理由は糊のログにあるので 1 行残して
//       スクリプト無しで起動する（PC の探索は Android では見当違いの案内しか出さないので行わない）
//
//  【ユーザースクリプトの読み込み】シーンの ScriptComponent 生成（型解決）より前に行う。
//    ① 同梱 .NET … 起動材料の DLL の置き場から SEEDUserScripts.dll をバイト列で読む（その場コンパイルはしない。
//                   Android にはソースも Roslyn の参照アセンブリも無い）
//    ② assets_root あり（PC のエディタ / Play）… その場で .cs をコンパイルする（ホットリロード可）
//    ③ assets_root なし（PC の配布物）… 実行ファイルの bin/SEEDUserScripts.dll を読むだけ
//  ①〜③のどれでも、見つからない・失敗したときは理由を 1 行残してスクリプト無しで起動を続ける。
//
//  以前は App::new の中にあった処理（②③と 2 の分岐は同じ内容のまま移した）。
// ============================================================

use std::sync::Arc;
use std::time::Instant;

use crate::engine::core::scripting::{
    self, EmbeddedClrHost, ScriptingHost, PRECOMPILED_SCRIPTS_DLL_NAME,
};
use crate::engine::platform::{self, ScriptHostSource};

use super::{App, LaunchArgs};

/// 秒 → ミリ秒（ログ用）。
const MILLIS_PER_SECOND: f64 = 1000.0;

/// バイト → KiB（ログ用）。
const BYTES_PER_KIB: usize = 1024;

impl App {
    /// スクリプトホスト（CLR ＋ SEEDScripting.dll）を用意し、関数ポインタ表（HOST_API）を登録する。
    ///
    /// # 引数
    /// * `args` - 起動引数（同梱 .NET の起動材料を見る）
    ///
    /// # 戻り値
    /// 用意できたスクリプトホスト。用意できなければ None（スクリプト無しで起動を続ける）。
    pub(super) fn boot_scripting_host(args: &LaunchArgs) -> Option<Arc<ScriptingHost>> {
        let host = match (&args.embedded_clr, platform::CURRENT.script_host_source) {
            (Some(embedded), _) => Self::boot_embedded_host(embedded),
            (None, ScriptHostSource::SearchFiles) => Self::boot_desktop_host(),
            (None, ScriptHostSource::EmbeddedOnly) => {
                eprintln!(
                    "[SEED] 同梱 .NET を用意できなかったため、C# スクリプト無しで起動します（理由は上の [SEED DOTNET] の行。docs/android.md §17）。"
                );
                None
            }
        }?;
        // コンポーネントアクセス用の関数ポインタ表を C# へ登録する
        // （これ以降 transform.Position などのスクリプトアクセスが有効になる）
        host.install_host_api();
        Some(host)
    }

    /// 同梱 .NET（起動材料）で CLR を起動する。失敗したら理由を残して None。
    fn boot_embedded_host(embedded: &EmbeddedClrHost) -> Option<Arc<ScriptingHost>> {
        match ScriptingHost::load_embedded(embedded) {
            Ok(host) => Some(host),
            Err(err) => {
                eprintln!("[SEED] scripting host failed to load（同梱 .NET: {}）: {err}", embedded.label);
                eprintln!("[SEED]   スクリプト無しで起動を続けます。");
                None
            }
        }
    }

    /// PC: SEEDScripting.dll を探して CLR を起動する（従来の App::new の処理そのまま）。
    fn boot_desktop_host() -> Option<Arc<ScriptingHost>> {
        let host_location = ScriptingHost::resolve_dll_path();
        if host_location.dll_path.exists() {
            // DLL が存在する場合のみ CLR ロードを試みる（存在しない場合は hostfxr 検索で遅延するため）
            match ScriptingHost::load(&host_location) {
                Ok(host) => Some(host),
                Err(err) => {
                    // CLR の初期化に失敗しても起動自体は続ける（スクリプト無しで動く）。
                    // ただし黙って落とすと「配布先でだけ何も動かない」の原因が追えないため、
                    // 原因と対処を必ず stderr に残す。
                    //
                    // 実際に一番多いのは「配布先に .NET ランタイムが入っていない」ケース。
                    // 利用者向けのダイアログは出していない（ランタイムに MessageBox の
                    // 共通ヘルパが無く、起動経路にモーダルを足す判断は別途必要なため）。
                    eprintln!("[SEED] scripting host failed to load: {err}");
                    eprintln!(
                        "[SEED]   {label} ランタイムが見つからない可能性があります。\
                         パッケージ化で「.NET ランタイムを同梱」を有効にして dotnet/ フォルダを\
                         実行ファイルの隣に置くか、実行する PC に {label} をインストールしてください。",
                        label = scripting::REQUIRED_DOTNET_RUNTIME_LABEL,
                    );
                    eprintln!("[SEED]   スクリプト無しで起動を続けます。");
                    None
                }
            }
        } else {
            // DLL が見つからない場合も黙らない。開発時は作業ディレクトリ（runtime/）相対で
            // 探すため、エディタが渡す作業ディレクトリを間違えると「ゲームロジックが一切
            // 動かない・入力が効かない」症状だけが出て原因が追えない（develop 構成で実際に起きた）。
            eprintln!(
                "[SEED] scripting host not found: {}  （cwd={}）— C# スクリプトは動きません。                 開発時は作業ディレクトリが runtime/ であること、配布時は bin/ に SEEDScripting.dll があることを確認してください。",
                host_location.dll_path.display(),
                std::env::current_dir().map(|d| d.display().to_string()).unwrap_or_default(),
            );
            None
        }
    }

    /// ユーザースクリプトを CLR 側で使えるようにする（シーンの ScriptComponent 生成より前に呼ぶ）。
    ///
    /// # 引数
    /// * `host` - 用意できたスクリプトホスト
    /// * `args` - 起動引数（同梱 .NET の起動材料・アセットルート）
    pub(super) fn load_user_scripts(host: &Arc<ScriptingHost>, args: &LaunchArgs) {
        // ① 同梱 .NET: 事前コンパイル DLL を起動材料の置き場からバイト列で読む。
        if let Some(embedded) = &args.embedded_clr {
            Self::load_embedded_user_scripts(host, embedded);
            return;
        }
        // ② assets_root あり（エディタ / Play）… その場で .cs をコンパイルする。
        //    コンパイルエラーは C# 側が stderr に出し、エディタの Output パネルに載る。
        // ③ assets_root なし（パッケージ版）… 実行ファイルの隣に置かれた事前コンパイル済み DLL を読むだけにする。
        //    配布物にソースと Roslyn を同梱しないため、ここでコンパイルすることはできない。
        match &args.assets_root {
            Some(root) => {
                let count = host.compile_scripts(root);
                if count >= 0 {
                    eprintln!("[SEED] user scripts compiled: {count} type(s)");
                } else {
                    eprintln!("[SEED] user script compilation failed (see errors above)");
                }
            }
            None => Self::load_precompiled_user_scripts(host),
        }
    }

    /// 同梱 .NET の起動材料の置き場（files/bin/ か APK の bin/）から SEEDUserScripts.dll を読む。
    ///
    /// 無いのは「スクリプトを 1 つも使っていないゲーム」でも起こり得るので、エラーにせず 1 行残す。
    fn load_embedded_user_scripts(host: &Arc<ScriptingHost>, embedded: &EmbeddedClrHost) {
        let started = Instant::now();
        let location = embedded.binaries.describe(PRECOMPILED_SCRIPTS_DLL_NAME);
        let assembly = match embedded.binaries.read(PRECOMPILED_SCRIPTS_DLL_NAME) {
            Ok(bytes) => bytes,
            Err(err) if err.kind() == std::io::ErrorKind::NotFound => {
                eprintln!("[SEED] precompiled scripts not found: {location} (スクリプト無しで起動します)");
                return;
            }
            Err(err) => {
                eprintln!("[SEED] precompiled scripts: {location} を読めません: {err}（スクリプト無しで起動します）");
                return;
            }
        };
        let count = host.load_precompiled_scripts_from_bytes(&assembly, &location);
        let elapsed_ms = started.elapsed().as_secs_f64() * MILLIS_PER_SECOND;
        if count >= 0 {
            eprintln!(
                "[SEED] precompiled scripts loaded: {count} type(s)  （{location}・{} KiB・{elapsed_ms:.1} ms）",
                assembly.len() / BYTES_PER_KIB
            );
        } else {
            eprintln!("[SEED] precompiled scripts failed to load: {location} (see errors above)");
        }
    }

    /// 実行ファイルの隣に置かれた事前コンパイル済みユーザースクリプト DLL を読み込む。
    ///
    /// パッケージ版（`--assets-root` 無しで起動された配布物）専用の経路。
    /// DLL が無いのは「スクリプトを 1 つも使っていないゲーム」や
    /// 「スクリプト同梱前にビルドされた古いパッケージ」でも起こり得るので、
    /// 見つからないこと自体はエラーにせず 1 行だけ残して起動を続ける。
    ///
    /// # 引数
    /// * `host` - ロード済みのスクリプティングホスト
    fn load_precompiled_user_scripts(host: &Arc<ScriptingHost>) {
        use crate::engine::core::package_layout;

        // 実行ファイルの bin/ 直下（cwd はショートカット等で変わるため exe 基準で探す）。
        // 配布物のフォルダ構成の正典は core::package_layout。
        let Some(dll_path) = std::env::current_exe()
            .ok()
            .and_then(|exe| exe.parent().map(package_layout::bin_dir))
            .map(|bin| bin.join(PRECOMPILED_SCRIPTS_DLL_NAME))
        else {
            eprintln!("[SEED] precompiled scripts: 実行ファイルのパスが取得できません");
            return;
        };

        if !dll_path.exists() {
            eprintln!(
                "[SEED] precompiled scripts not found: {} (スクリプト無しで起動します)",
                dll_path.display()
            );
            return;
        }

        let count = host.load_precompiled_scripts(&dll_path);
        if count >= 0 {
            eprintln!("[SEED] precompiled scripts loaded: {count} type(s)");
        } else {
            eprintln!("[SEED] precompiled scripts failed to load (see errors above)");
        }
    }
}
