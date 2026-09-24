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
// ============================================================

use std::path::{Path, PathBuf};
use std::sync::Arc;

use seed_engine::engine::core::app_base::{LaunchArgs, RuntimeMode};
use seed_engine::engine::core::package_layout;
use seed_engine::engine::package_source::PackageSource;
use winit::platform::android::activity::AndroidApp;

use crate::apk_package::{ApkPackageSource, PakProbe};
use crate::logcat;

/// データルート直下のアセットルートのフォルダ名（PC のプロジェクトの assets/ と同じ名前）。
const ASSETS_DIR_NAME: &str = "assets";

/// アセットルートに必ずある目印のファイル（エンジンのプロジェクト設定）。
const PROJECT_SETTINGS_FILE_NAME: &str = "project_settings.json";

/// ログに出すバイト数を MiB へ直す除数。
const BYTES_PER_MIB: f64 = 1024.0 * 1024.0;

/// 起動モードを決めて、エンジンの起動引数を組み立てる。
///
/// 端末上では常にゲームとして起動する（エディタ埋め込み・IPC・親プロセス監視は無い）。
/// 開始シーンは project_settings.json の start_scene に任せる（パッケージ実行では PAK の中のもの）。
pub fn launch_args(app: &AndroidApp) -> LaunchArgs {
    let internal = app.internal_data_path();

    // ── 1. APK に配布物の pak があればパッケージ実行 ──
    let package = ApkPackageSource::new(app.asset_manager());
    if let Some(probe) = package.probe_pak() {
        return packaged_launch_args(internal.as_deref(), package, &probe);
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

    play_launch_args(assets_root, None)
}

/// パッケージ実行（APK 内の pak）の起動引数を組み立てる。
///
/// # 引数
/// * `internal` - 内部アプリ専用フォルダ（アセットルートのフォールバック先を作るのに使う）
/// * `package`  - APK の配布物の読み口（エンジンの asset_fs へ渡す）
/// * `probe`    - pak を調べた結果（ログ用）
fn packaged_launch_args(internal: Option<&Path>, package: ApkPackageSource, probe: &PakProbe) -> LaunchArgs {
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

    let package: Arc<dyn PackageSource> = Arc::new(package);
    play_launch_args(assets_root, Some(package))
}

/// ゲームとして起動する LaunchArgs を組み立てる（両モード共通）。
///
/// # 引数
/// * `assets_root`    - アセットルート（ファイルシステム）
/// * `package_source` - 配布物の読み口（パッケージ実行のときだけ Some）
fn play_launch_args(
    assets_root: Option<PathBuf>,
    package_source: Option<Arc<dyn PackageSource>>,
) -> LaunchArgs {
    LaunchArgs {
        parent_hwnd: None,
        parent_pid: None,
        mode: RuntimeMode::Play,
        pipe_name: None,
        assets_root: assets_root.map(|path| path.to_string_lossy().into_owned()),
        package_source,
        editor_resources: None,
        scene_path: None,
        play_collider_draw: false,
    }
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
