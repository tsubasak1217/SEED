// ============================================================
//  launch.rs — アプリ専用データフォルダからエンジンの起動引数を組み立てる
//
//  【データの置き場（段階0）】
//  PC の開発時レイアウト（projects/<Name>/assets と、その隣の save/・cache/）をそのまま
//  端末のアプリ専用フォルダへ写した形にする。
//
//    <data_root>/                       … 既定は外部アプリ専用フォルダ
//      assets/                          … アセットルート（adb push 先。project_settings.json もここ）
//        project_settings.json
//        scenes/Main.scene ...
//      save/                            … セーブデータ（エンジンが自動で作る）
//      cache/                           … 派生データキャッシュ（エンジンが自動で作る）
//
//  <data_root> は外部アプリ専用フォルダ（/sdcard/Android/data/<パッケージ名>/files）を優先する。
//  adb から push でき、アプリのアンインストールで一緒に消える。取れない端末では内部フォルダを使う。
//  assets/ に何も無ければ、エンジンは既定値（空のシーン）で起動してクリア色の描画まで行う。
//
//  段階A で APK 内の pak（AssetManager 経由）と保存先の振替へ置き換える予定（docs/android.md）。
// ============================================================

use std::path::{Path, PathBuf};

use seed_engine::engine::core::app_base::{LaunchArgs, RuntimeMode};
use winit::platform::android::activity::AndroidApp;

use crate::logcat;

/// データルート直下のアセットルートのフォルダ名（PC のプロジェクトの assets/ と同じ名前）。
const ASSETS_DIR_NAME: &str = "assets";

/// アプリ専用データフォルダからエンジンの起動引数を組み立てる。
///
/// 端末上では常にゲームとして起動する（エディタ埋め込み・IPC・親プロセス監視は無い）。
/// 開始シーンは assets/project_settings.json の start_scene に任せる。
pub fn launch_args(app: &AndroidApp) -> LaunchArgs {
    let external = app.external_data_path();
    let internal = app.internal_data_path();
    let data_root = choose_data_root(external.as_deref(), internal.as_deref());

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

/// データルートを選ぶ【純関数】。外部アプリ専用フォルダを優先し、無ければ内部フォルダ。
fn choose_data_root<'a>(external: Option<&'a Path>, internal: Option<&'a Path>) -> Option<&'a Path> {
    external.or(internal)
}

/// データルートからアセットルートのパスを作る【純関数】。
fn assets_root_of(data_root: &Path) -> PathBuf {
    data_root.join(ASSETS_DIR_NAME)
}

/// アセットルートのフォルダが無ければ空で作る。
///
/// 初回起動直後から adb push の置き場が見えるようにするため（無くてもエンジンは既定値で起動する）。
/// 作成に失敗しても起動は止めない（ログだけ残す）。
fn ensure_assets_dir(assets_root: &Path) {
    if assets_root.is_dir() {
        return;
    }
    match std::fs::create_dir_all(assets_root) {
        Ok(()) => logcat::info(&format!(
            "アセットルートが無かったため空で作成しました（ここへ adb push すると読み込みます）: {}",
            assets_root.display()
        )),
        Err(err) => logcat::warn(&format!(
            "アセットルートを作成できませんでした: {} — {err}",
            assets_root.display()
        )),
    }
}
