// ============================================================
//  build.rs — ビルド後に DLL を自動デプロイするスクリプト
//
//  cargo build -p game_tools 実行後、ビルド成果物をプロジェクトの plugins/GameTools/ に自動コピーする。
//  配置先は環境変数で決める（優先順）:
//    1. SEED_PLUGIN_DEPLOY_ROOT … プロジェクトの plugins/ フォルダを直接指定
//    2. SEED_PROJECT           … プロジェクトのフォルダ or .seedproj（エディタ／ランタイムと同じ変数）→ <project>/plugins/
//  どちらも無ければ配置しない（ゲームプロジェクトは SEED リポジトリの外にあるため、既定の場所は無い）。
//  plugin.json も同フォルダに配置する。
// ============================================================

use std::{env, fs, path::PathBuf};

/// デプロイ先の plugins/ フォルダを指定する環境変数名。
const DEPLOY_ROOT_ENV: &str = "SEED_PLUGIN_DEPLOY_ROOT";
/// プロジェクトの場所（フォルダ or .seedproj）を示す環境変数名（エディタ／ランタイムと共通）。
const PROJECT_ENV: &str = "SEED_PROJECT";
/// プロジェクトフォルダ内のプラグイン置き場の名前。
const PROJECT_PLUGINS_DIR_NAME: &str = "plugins";
/// プロジェクトファイルの拡張子（SEED_PROJECT にファイルが渡されたときの判定用）。
const PROJECT_FILE_EXT: &str = "seedproj";
/// この DLL を置くフォルダ名（plugin.json の name と一致させる）。
const PLUGIN_DIR_NAME: &str = "GameTools";

fn main() {
    // Cargo が教えてくれるビルド成果物ディレクトリ（target/debug/ または target/release/）
    let out_dir = env::var("OUT_DIR").unwrap_or_default();
    // OUT_DIR は target/debug/build/game_tools-.../out の形式なので 3 階層上が target/debug/
    let target_dir = PathBuf::from(&out_dir)
        .ancestors()
        .nth(3)
        .map(|p| p.to_path_buf())
        .unwrap_or_else(|| PathBuf::from("."));

    // DLL ファイル名（Windows は .dll、Linux は .so、macOS は .dylib）
    let dll_name = if cfg!(target_os = "windows") {
        "game_tools.dll"
    } else if cfg!(target_os = "macos") {
        "libgame_tools.dylib"
    } else {
        "libgame_tools.so"
    };

    let dll_src = target_dir.join(dll_name);

    // デプロイ先の決定（データドリブン）:
    //   1. 環境変数 SEED_PLUGIN_DEPLOY_ROOT（プロジェクトの plugins/ フォルダ）があればその配下の GameTools/。
    //   2. 無ければ環境変数 SEED_PROJECT（プロジェクトのフォルダ or .seedproj）から <project>/plugins/GameTools/。
    //   3. どちらも無ければ配置しない（警告だけ出す）。ゲームプロジェクトは SEED リポジトリの外にあり、
    //      リポジトリからの相対で決められる既定の場所は存在しない。
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap_or_default());
    println!("cargo:rerun-if-env-changed={DEPLOY_ROOT_ENV}");
    println!("cargo:rerun-if-env-changed={PROJECT_ENV}");
    let Some(plugins_root) = resolve_plugins_root() else {
        println!(
            "cargo:warning=配置先が未指定のため DLL を配置しません（{DEPLOY_ROOT_ENV} か {PROJECT_ENV} を設定してください）"
        );
        println!("cargo:rerun-if-changed=src/lib.rs");
        println!("cargo:rerun-if-changed=plugin.json");
        return;
    };
    let plugin_dir = plugins_root.join(PLUGIN_DIR_NAME);

    // デプロイ先フォルダを作成する
    if let Err(e) = fs::create_dir_all(&plugin_dir) {
        eprintln!("cargo:warning=プラグインフォルダ作成失敗: {e}");
        return;
    }

    // DLL をコピーする（ビルドが成功していなければファイルが存在しないので無視）
    if dll_src.exists() {
        let dll_dst = plugin_dir.join(dll_name);
        match fs::copy(&dll_src, &dll_dst) {
            Ok(_) => println!(
                "cargo:warning=DLL をデプロイしました: {}",
                dll_dst.display()
            ),
            Err(e) => eprintln!("cargo:warning=DLL コピー失敗: {e}"),
        }
    }

    // plugin.json をコピーする
    let json_src = manifest_dir.join("plugin.json");
    if json_src.exists() {
        let json_dst = plugin_dir.join("plugin.json");
        match fs::copy(&json_src, &json_dst) {
            Ok(_) => println!("cargo:warning=plugin.json をデプロイしました"),
            Err(e) => eprintln!("cargo:warning=plugin.json コピー失敗: {e}"),
        }
    }

    // Cargo に再ビルドのトリガーを伝える
    println!("cargo:rerun-if-changed=src/lib.rs");
    println!("cargo:rerun-if-changed=plugin.json");
}

/// 環境変数からプラグインの配置先（プロジェクトの plugins/ フォルダ）を決める。
///
/// 優先順: `SEED_PLUGIN_DEPLOY_ROOT`（そのまま plugins/ フォルダ）→ `SEED_PROJECT`
/// （プロジェクトのフォルダ、または .seedproj ファイル。ファイルならその親フォルダ）の `plugins/`。
/// どちらも空なら `None`（配置しない）。
fn resolve_plugins_root() -> Option<PathBuf> {
    if let Ok(root) = env::var(DEPLOY_ROOT_ENV) {
        if !root.trim().is_empty() {
            return Some(PathBuf::from(root.trim()));
        }
    }
    let project = env::var(PROJECT_ENV).ok()?;
    let project = project.trim();
    if project.is_empty() {
        return None;
    }
    let path = PathBuf::from(project);
    // .seedproj が渡されたときはその親フォルダがプロジェクトフォルダ
    let is_project_file = path
        .extension()
        .map(|e| e.eq_ignore_ascii_case(PROJECT_FILE_EXT))
        .unwrap_or(false);
    let project_dir = if is_project_file {
        path.parent().map(|p| p.to_path_buf())?
    } else {
        path
    };
    Some(project_dir.join(PROJECT_PLUGINS_DIR_NAME))
}
