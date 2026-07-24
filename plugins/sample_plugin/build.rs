// ============================================================
//  build.rs — ビルド後に DLL を自動デプロイするスクリプト
//
//  cargo build -p sample_plugin 実行後、ビルド成果物を
//  runtime/plugins/SamplePlugin/ に自動コピーする。
//  plugin.json も同フォルダに配置する。
// ============================================================

use std::{env, fs, path::PathBuf};

fn main() {
    // Cargo が教えてくれるビルド成果物ディレクトリ（target/debug/ または target/release/）
    let out_dir = env::var("OUT_DIR").unwrap_or_default();
    // OUT_DIR は target/debug/build/sample_plugin-.../out の形式なので 3 階層上が target/debug/
    let target_dir = PathBuf::from(&out_dir)
        .ancestors()
        .nth(3)
        .map(|p| p.to_path_buf())
        .unwrap_or_else(|| PathBuf::from("."));

    // DLL ファイル名（Windows は .dll、Linux は .so、macOS は .dylib）
    let dll_name = if cfg!(target_os = "windows") {
        "sample_plugin.dll"
    } else if cfg!(target_os = "macos") {
        "libsample_plugin.dylib"
    } else {
        "libsample_plugin.so"
    };

    let dll_src = target_dir.join(dll_name);

    // デプロイ先: runtime/plugins/SamplePlugin/
    // __FILE__ から相対パスで workspace ルートを求める
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap_or_default());
    let plugin_dir = manifest_dir
        .ancestors()
        .nth(2) // plugins/ → SEED root
        .map(|p| p.join("runtime").join("plugins").join("SamplePlugin"))
        .unwrap_or_else(|| PathBuf::from("runtime/plugins/SamplePlugin"));

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
