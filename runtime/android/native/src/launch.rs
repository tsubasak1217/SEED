// ============================================================
//  launch.rs — 起動モードを決めて、エンジンの起動引数（LaunchArgs）を組み立てる
//
//  【起動モードの決め方】データの有無だけで決める（設定フラグは持たない）。
//    1. パッケージ実行 … APK の assets/seed/assets.pak があるとき（配布版。リリース版 APK でも動く）。
//                         アセットは APK 内の pak から読む（apk_package / engine::package_source）。
//    2. 開発用の置き場 … APK に pak が無いとき。端末のアプリ専用フォルダの assets/ から読む
//                         （build_and_run.ps1 -AssetsDir が run-as で送る。デバッグ版 APK でしか使えない）。
//
//  【データの置き場】
//  PC の開発時レイアウト（projects/<Name>/assets）を端末のアプリ専用フォルダへ写した形にする。
//
//    <data_root>/                       … アプリ専用フォルダ（下の「データルートの選び方」）
//      assets/                          … アセットルート（project_settings.json もここ）
//        project_settings.json
//        scenes/Main.scene ...
//
//  パッケージ実行でも、アセットルートは内部アプリ専用フォルダの assets/ にしておく
//  （PAK にも APK にも無いアセットのフォールバック先。フォルダは作らない＝空で正常）。
//  セーブ（files/save/）・キャッシュ（/data/user/0/<パッケージ名>/cache）の置き場は起動モードに関係なく
//  app_dirs.rs がエンジンへ設定する（engine::platform::paths。データルートの選び方とは独立）。
//
//  【データルートの選び方（開発用の置き場）】候補を次の順に見て、assets/project_settings.json がある
//  最初のものを使う。どれにも無ければ先頭（内部フォルダ）を使い、空の assets/ を作ってエンジンは既定値で起動する。
//    1. 内部アプリ専用フォルダ（/data/user/0/<パッケージ名>/files）
//       … build_and_run.ps1 -AssetsDir が run-as（デバッグ版 APK の権限）で書き込む先。
//         実機でもエミュレータでも確実にアプリから読める。
//    2. 外部アプリ専用フォルダ（/sdcard/Android/data/<パッケージ名>/files）
//       … 手で adb push した場合の置き場。エミュレータでは読めるが、実機（Android 11 以降）では
//         adb push が作ったフォルダは shell の所有になりアプリから読めない（Permission denied）。
//
//  【起動するシーン（段階C-3）】
//  起動オプション（am start の extra seed.scene → MainActivity → JNI。launch_options.rs）にシーンがあれば、
//  そのシーン（assets://<相対パス>）を LaunchArgs.scene_path に入れる（エディタの「開いているシーンから実行」）。
//  あるかは、エンジンが読むのと同じ順（pak → APK の PAK 外 → アプリ専用フォルダの assets/）で確かめ、
//  どこにも無い・パスが読めないときは logcat に警告を出して開始シーン（project_settings.json の start_scene）で起動する。
//  判断そのものは engine::platform::launch_options::choose_scene（純粋な処理・単体テスト付き）。
//
//  【エディタとの通信路（段階D-1）】
//  起動オプションに ipc_port と ipc_token（am start の extra seed.ipc_port・seed.ipc_token）があれば LaunchArgs へ入れ、
//  エンジンが 127.0.0.1:<ポート> で待ち受ける（エディタ／SeedAndroid が adb forward 越しにつなぎ、最初の行でトークンを
//  照合してから一時停止・再開を送る。engine::core::app_base::ipc_transport）。無ければ待ち受けない（従来どおり）。
//  読めない値は警告して待ち受けない（トークンの値は logcat へ出さない）。
//
//  【上書き層（実行中の差し替え。docs/android.md §23）】
//  デバッグ版の APK（起動オプションが届いた＝launch_options::was_delivered）のパッケージ実行では、内部フォルダ
//  files/assets を pak より先に読む「上書き層」にする（LaunchArgs.asset_overlay。エディタ／SeedAndroid が run-as で送った
//  差し替えのアセットを優先させる。置いたファイルだけが優先され、無いものは従来どおり pak から読む。起動の後に送られても効く）。
//  配布版は従来どおり pak → APK の PAK 外 → files/assets。判断は engine::asset_fs::overlay_allowed（純粋な処理・単体テスト付き）。
//  上書きは run（APK の内容を正とする）が起動の前に消す。
// ============================================================

use std::path::{Path, PathBuf};
use std::sync::Arc;

use seed_engine::engine::asset_fs;
use seed_engine::engine::core::app_base::{LaunchArgs, RuntimeMode};
use seed_engine::engine::core::package_layout;
use seed_engine::engine::package_source::{self, PackageSource};
use seed_engine::engine::platform::launch_options::{self as options_rules, LaunchOptions, SceneChoice};
use winit::platform::android::activity::AndroidApp;

use crate::apk_package::{ApkPackageSource, PakProbe};
use crate::{launch_options, logcat};

/// データルート直下のアセットルートのフォルダ名（PC のプロジェクトの assets/ と同じ名前）。
const ASSETS_DIR_NAME: &str = "assets";

/// アセットルートに必ずある目印のファイル（エンジンのプロジェクト設定）。
const PROJECT_SETTINGS_FILE_NAME: &str = "project_settings.json";

/// ログに出すバイト数を MiB へ直す除数。
const BYTES_PER_MIB: f64 = 1024.0 * 1024.0;

/// 起動オプションから決めた、エディタとの IPC の待ち受け（段階D-1）。
#[derive(Debug, Clone, Default)]
struct IpcLaunch {
    /// 待ち受けるポート（None なら待ち受けない）。
    port: Option<u16>,
    /// 接続トークン（None ならポートがあっても待ち受けない）。
    token: Option<String>,
}

/// 起動モードを決めて、エンジンの起動引数を組み立てる。
///
/// 端末上では常にゲームとして起動する（エディタ埋め込み・名前付きパイプ・親プロセス監視は無い）。
/// 起動するシーンは起動オプションのシーン（あれば）、無ければ project_settings.json の start_scene
/// （パッケージ実行では PAK の中のもの）。起動オプションに IPC のポートがあれば TCP で待ち受ける（段階D-1）。
pub fn launch_args(app: &AndroidApp) -> LaunchArgs {
    // JNI で受け取った起動オプション（MainActivity.onCreate が android_main より前に渡す）
    let options = launch_options::take();
    let mut args = launch_args_for(app, &options);
    // 計測・検証用の描画品質の指定（段階D-2。am start --es seed.quality / seed.quality_overrides / seed.gpu_timing）。
    // どの起動モードでも同じに効く。無ければ project_settings.json の render_quality かプラットフォームの既定（mobile）。
    args.render_quality = options.quality_launch();
    args.gpu_timing = options.gpu_timing_enabled();
    if args.render_quality != Default::default() || args.gpu_timing {
        logcat::info(&format!(
            "起動オプションの描画品質: プリセット={:?} つまみ={:?} GPU 計測={}",
            args.render_quality.preset, args.render_quality.knobs, args.gpu_timing
        ));
    }
    args
}

/// 起動オプション `options` から、起動モード（パッケージ実行／開発用の置き場）を決めて LaunchArgs を組み立てる。
fn launch_args_for(app: &AndroidApp, options: &LaunchOptions) -> LaunchArgs {
    let internal = app.internal_data_path();
    let ipc = ipc_endpoint_logged(options);

    // ── 1. APK に配布物の pak があればパッケージ実行 ──
    let package = ApkPackageSource::new(app.asset_manager());
    if let Some(probe) = package.probe_pak() {
        return packaged_launch_args(internal.as_deref(), package, &probe, options, ipc);
    }
    logcat::info(&format!(
        "APK に {} がありません。開発用の置き場（アプリ専用フォルダの assets/）から読みます",
        package.describe(package_layout::PAK_FILE_NAME)
    ));

    // ── 2. 開発用の置き場（run-as で送った内部フォルダ → 外部フォルダ）──
    let external = app.external_data_path();
    let candidates: Vec<&Path> = [internal.as_deref(), external.as_deref()]
        .into_iter()
        .flatten()
        .collect();
    warn_unreadable_assets(&candidates);

    let data_root = choose_data_root(&candidates, has_project_settings);
    let assets_root = data_root.map(|root| {
        let assets = assets_root_of(root);
        ensure_assets_dir(&assets);
        assets
    });
    match &assets_root {
        Some(path) => logcat::info(&format!("アセットルート: {}", path.display())),
        None => logcat::warn("アプリ専用データフォルダが取得できません。アセット無しの既定値で起動します"),
    }

    // 開発用の置き場では、シーンはアセットルート（run-as で送ったフォルダ）のファイル
    let scene_path = choose_scene_logged(options, |relative| {
        assets_root.as_deref().is_some_and(|root| root.join(relative).is_file())
    });
    // pak が無いので files/assets は元から唯一の層（上書き層にする意味が無い）
    play_launch_args(assets_root, None, scene_path, ipc, false)
}

/// パッケージ実行（APK 内の pak）の起動引数を組み立てる。
///
/// # 引数
/// * `internal` - 内部アプリ専用フォルダ（アセットルートのフォールバック先を作るのに使う）
/// * `package`  - APK の配布物の読み口（エンジンの asset_fs へ渡す）
/// * `probe`    - pak を調べた結果（ログ用）
/// * `options`  - 起動オプション（起動するシーン）
/// * `ipc`      - エディタとの IPC の待ち受け（ポートとトークン。無ければ待ち受けない）
fn packaged_launch_args(
    internal: Option<&Path>,
    package: ApkPackageSource,
    probe: &PakProbe,
    options: &LaunchOptions,
    ipc: IpcLaunch,
) -> LaunchArgs {
    let location = package.describe(package_layout::PAK_FILE_NAME);
    match probe.uncompressed_offset {
        Some(offset) => logcat::info(&format!(
            "APK 内の pak で起動します（パッケージ実行）: {location}  {:.1} MiB・非圧縮（APK 内の位置 {offset}）",
            probe.size as f64 / BYTES_PER_MIB
        )),
        None => {
            logcat::info(&format!(
                "APK 内の pak で起動します（パッケージ実行）: {location}  {:.1} MiB",
                probe.size as f64 / BYTES_PER_MIB
            ));
            logcat::warn(&format!(
                "{location} が APK 内で圧縮されています。読めますが Seek のたびに展開し直すため遅くなります。\
                 app/build.gradle.kts の androidResources.noCompress に \"pak\" が入っているか確認してください"
            ));
        }
    }

    // PAK にも APK にも無いアセットのフォールバック先（開発用の置き場と同じ場所）。作らない。
    let assets_root = internal.map(assets_root_of);
    if let Some(path) = &assets_root {
        logcat::info(&format!("アセットルート（PAK に無いアセットのフォールバック先）: {}", path.display()));
    }

    // 上書き層: デバッグ版で files/assets があれば pak より先に読む（差し替え。§23）。シーンの有無の確かめ方も同じ順になる
    let asset_overlay = overlay_logged(assets_root.as_deref());
    let scene_path = choose_scene_logged(options, |relative| {
        packaged_scene_exists(&package, assets_root.as_deref(), relative)
    });
    let package: Arc<dyn PackageSource> = Arc::new(package);
    play_launch_args(assets_root, Some(package), scene_path, ipc, asset_overlay)
}

/// パッケージ実行で上書き層（files/assets を pak より先に読む）を使うかを決め、決めた内容を logcat へ残す。
///
/// # 引数
/// * `assets_root` - 内部フォルダの assets/（上書き層の置き場。取得できなければ None＝使えない）
///
/// # 戻り値
/// 使うなら true（デバッグ版の APK のときだけ。判断は engine::asset_fs::overlay_allowed）。
fn overlay_logged(assets_root: Option<&Path>) -> bool {
    let debug_build = launch_options::was_delivered();
    let Some(root) = assets_root else {
        return false;
    };
    let overlay = asset_fs::overlay_allowed(debug_build, true);
    if overlay {
        let state = if root.is_dir() { "差し替えが置かれています" } else { "まだ差し替えはありません" };
        logcat::info(&format!(
            "上書き層: {} に置いたアセットを pak より先に読みます（デバッグ版の差し替え・{state}。run で起動し直すと消えます。docs/android.md §23）",
            root.display()
        ));
    } else {
        logcat::info("上書き層: 使いません（配布版の APK。pak を優先します）");
    }
    overlay
}

/// ゲームとして起動する LaunchArgs を組み立てる（両モード共通）。
///
/// # 引数
/// * `assets_root`    - アセットルート（ファイルシステム）
/// * `package_source` - 配布物の読み口（パッケージ実行のときだけ Some）
/// * `scene_path`     - 起動するシーン（仮想パス。None なら project_settings.json の start_scene）
/// * `ipc`            - エディタとの IPC の待ち受け（127.0.0.1 だけ。ポートとトークンが揃わなければ待ち受けない）
/// * `asset_overlay`  - アセットルートを上書き層として pak より先に読むか（実行中の差し替え。§23）
fn play_launch_args(
    assets_root: Option<PathBuf>,
    package_source: Option<Arc<dyn PackageSource>>,
    scene_path: Option<String>,
    ipc: IpcLaunch,
    asset_overlay: bool,
) -> LaunchArgs {
    LaunchArgs {
        parent_hwnd: None,
        parent_pid: None,
        mode: RuntimeMode::Play,
        pipe_name: None,
        ipc_port: ipc.port,
        ipc_token: ipc.token,
        assets_root: assets_root.map(|path| path.to_string_lossy().into_owned()),
        package_source,
        editor_resources: None,
        scene_path,
        play_collider_draw: false,
        // 同梱 .NET の起動材料は entry.rs が dotnet_runtime::prepare で作って入れる（起動モードとは独立）。
        embedded_clr: None,
        // 描画品質の指定・GPU 計測は launch_args が起動オプションから入れる（起動モードとは独立）。
        render_quality: Default::default(),
        gpu_timing: false,
        asset_overlay,
    }
}

/// 起動するシーンを決め、決めた内容を logcat へ残す。
///
/// # 引数
/// * `options` - 起動オプション
/// * `exists`  - アセットルートからの相対パスのシーンがあるか（起動モードごとの確かめ方）
///
/// # 戻り値
/// LaunchArgs.scene_path へ入れる仮想パス（開始シーンで起動するなら None）。
fn choose_scene_logged(options: &LaunchOptions, exists: impl Fn(&str) -> bool) -> Option<String> {
    let choice = options_rules::choose_scene(options, exists);
    match &choice {
        SceneChoice::StartScene => {
            logcat::info("起動するシーン: 開始シーン（project_settings.json の start_scene。起動オプションにシーンの指定なし）");
        }
        SceneChoice::Requested { virtual_path } => {
            logcat::info(&format!("起動するシーン: {virtual_path}（起動オプションの指定。エディタで開いているシーン）"));
        }
        SceneChoice::Missing { relative } => logcat::warn(&format!(
            "起動オプションのシーン {relative} が見つかりません（pak・APK・アプリ専用フォルダのどれにもありません）。開始シーンで起動します"
        )),
        SceneChoice::Invalid { requested, reason } => logcat::warn(&format!(
            "起動オプションのシーン {requested} を使えません（{reason}）。開始シーンで起動します"
        )),
    }
    choice.scene_path()
}

/// 起動オプションの IPC のポートと接続トークンを確かめ、決めた内容を logcat へ残す（段階D-1。トークンの値は出さない）。
///
/// # 引数
/// * `options` - 起動オプション
///
/// # 戻り値
/// 待ち受け（ポートとトークン。指定なし・読めない値なら None ＝ 待ち受けない）。
fn ipc_endpoint_logged(options: &LaunchOptions) -> IpcLaunch {
    let port = match options.ipc_port() {
        None => {
            logcat::info("エディタとの通信路: 起動オプションに ipc_port が無いため待ち受けません（一時停止などのエディタからの操作は使えません）");
            return IpcLaunch::default();
        }
        Some(Ok(port)) => port,
        Some(Err(reason)) => {
            logcat::warn(&format!("エディタとの通信路を開きません: {reason}"));
            return IpcLaunch::default();
        }
    };
    let token = match options.ipc_token() {
        Some(Ok(token)) => token,
        Some(Err(reason)) => {
            logcat::warn(&format!("エディタとの通信路を開きません（接続トークン）: {reason}"));
            return IpcLaunch::default();
        }
        None => {
            logcat::warn("エディタとの通信路を開きません: 起動オプションに接続トークン（ipc_token）が無いため待ち受けません（エディタ／SeedAndroid の run で起動し直してください）");
            return IpcLaunch::default();
        }
    };
    logcat::info(&format!(
        "エディタとの通信路: 127.0.0.1:{port} で待ち受けます（起動オプションの ipc_port。adb forward 越しにつなぎ、接続トークンを照合する）"
    ));
    IpcLaunch { port: Some(port), token: Some(token) }
}

/// パッケージ実行で、相対パスのシーンがあるか（エンジンの asset_fs が仮想パスを読むのと同じ順に見る）。
///
/// 1. APK 内の pak のエントリ（大文字小文字・区切りを問わない）
/// 2. APK の PAK 外（assets/seed/assets/<相対パス>）
/// 3. アプリ専用フォルダの assets/<相対パス>
///
/// pak のエントリ表はここで 1 回読む（中身は読まない。シーンの指定があるときだけ呼ばれる）。
fn packaged_scene_exists(package: &ApkPackageSource, assets_root: Option<&Path>, relative: &str) -> bool {
    match package_source::open_pak(package) {
        Ok(pak) if pak.contains(relative) => return true,
        Ok(_) => {}
        Err(err) => logcat::warn(&format!(
            "シーンを確かめるために {} を開けませんでした: {err}",
            package.describe(package_layout::PAK_FILE_NAME)
        )),
    }
    if package.open(&package_source::loose_asset_path(relative)).is_ok() {
        return true;
    }
    assets_root.is_some_and(|root| root.join(relative).is_file())
}

/// データルートを選ぶ【純関数】。
///
/// 候補（優先順）のうち、アセットルートに目印のファイルがある最初のものを返す。
/// どれにも無ければ先頭の候補（候補が空なら None）。
///
/// # 引数
/// * `candidates`   - データルートの候補（優先順）
/// * `has_settings` - アセットルートに project_settings.json があるか（テストのために注入する）
fn choose_data_root<'a>(
    candidates: &[&'a Path],
    has_settings: impl Fn(&Path) -> bool,
) -> Option<&'a Path> {
    candidates
        .iter()
        .copied()
        .find(|root| has_settings(&assets_root_of(root)))
        .or_else(|| candidates.first().copied())
}

/// データルートからアセットルートのパスを作る【純関数】。
fn assets_root_of(data_root: &Path) -> PathBuf {
    data_root.join(ASSETS_DIR_NAME)
}

/// アセットルートに project_settings.json があるか。
fn has_project_settings(assets_root: &Path) -> bool {
    assets_root.join(PROJECT_SETTINGS_FILE_NAME).is_file()
}

/// assets/ はあるのにアプリから読めない候補を警告する（実機で手の adb push をしたときの典型）。
///
/// 読めない assets/ は `choose_data_root` の対象から自然に外れるが、黙っていると
/// 「push したのに空のシーンで起動する」理由が分からないため、原因と対処を 1 行残す。
fn warn_unreadable_assets(candidates: &[&Path]) {
    for root in candidates {
        let assets = assets_root_of(root);
        if !assets.exists() {
            continue;
        }
        if let Err(err) = std::fs::read_dir(&assets) {
            logcat::warn(&format!(
                "{} はアプリから読めません（{err}）。実機では adb push で作ったフォルダはアプリの所有に\
                 ならないため、build_and_run.ps1 -AssetsDir（run-as で内部フォルダへ書き込む）を使ってください",
                assets.display()
            ));
        }
    }
}

/// アセットルートのフォルダが無ければ空で作る。
///
/// 初回起動直後から置き場が見えるようにするため（無くてもエンジンは既定値で起動する）。
/// 作成に失敗しても起動は止めない（ログだけ残す）。
fn ensure_assets_dir(assets_root: &Path) {
    if assets_root.is_dir() {
        return;
    }
    match std::fs::create_dir_all(assets_root) {
        Ok(()) => logcat::info(&format!(
            "アセットルートが無かったため空で作成しました: {}",
            assets_root.display()
        )),
        Err(err) => logcat::warn(&format!(
            "アセットルートを作成できませんでした: {} — {err}",
            assets_root.display()
        )),
    }
}
