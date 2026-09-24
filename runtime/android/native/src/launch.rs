// ============================================================
//  launch.rs — アプリ専用データフォルダからエンジンの起動引数を組み立てる
//
//  【データの置き場（段階0）】
//  PC の開発時レイアウト（projects/<Name>/assets と、その隣の save/・cache/）をそのまま
//  端末のアプリ専用フォルダへ写した形にする。
//
//    <data_root>/                       … アプリ専用フォルダ（下の「データルートの選び方」）
//      assets/                          … アセットルート（project_settings.json もここ）
//        project_settings.json
//        scenes/Main.scene ...
//      save/                            … セーブデータ（エンジンが自動で作る）
//      cache/                           … 派生データキャッシュ（エンジンが自動で作る）
//
//  【データルートの選び方】候補を次の順に見て、assets/project_settings.json がある最初のものを使う。
//  どれにも無ければ先頭（内部フォルダ）を使い、空の assets/ を作ってエンジンは既定値で起動する。
//    1. 内部アプリ専用フォルダ（/data/user/0/<パッケージ名>/files）
//       … build_and_run.ps1 -AssetsDir が run-as（デバッグ版 APK の権限）で書き込む先。
//         実機でもエミュレータでも確実にアプリから読める。
//    2. 外部アプリ専用フォルダ（/sdcard/Android/data/<パッケージ名>/files）
//       … 手で adb push した場合の置き場。エミュレータでは読めるが、実機（Android 11 以降）では
//         adb push が作ったフォルダは shell の所有になりアプリから読めない（Permission denied）。
//
//  段階A で APK 内の pak（AssetManager 経由）と保存先の振替へ置き換える予定（docs/android.md）。
// ============================================================

use std::path::{Path, PathBuf};

use seed_engine::engine::core::app_base::{LaunchArgs, RuntimeMode};
use winit::platform::android::activity::AndroidApp;

use crate::logcat;

/// データルート直下のアセットルートのフォルダ名（PC のプロジェクトの assets/ と同じ名前）。
const ASSETS_DIR_NAME: &str = "assets";

/// アセットルートに必ずある目印のファイル（エンジンのプロジェクト設定）。
const PROJECT_SETTINGS_FILE_NAME: &str = "project_settings.json";

/// アプリ専用データフォルダからエンジンの起動引数を組み立てる。
///
/// 端末上では常にゲームとして起動する（エディタ埋め込み・IPC・親プロセス監視は無い）。
/// 開始シーンは assets/project_settings.json の start_scene に任せる。
pub fn launch_args(app: &AndroidApp) -> LaunchArgs {
    let internal = app.internal_data_path();
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

    LaunchArgs {
        parent_hwnd: None,
        parent_pid: None,
        mode: RuntimeMode::Play,
        pipe_name: None,
        assets_root: assets_root.map(|path| path.to_string_lossy().into_owned()),
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
