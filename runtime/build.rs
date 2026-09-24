fn main() {
    // このビルドスクリプトの入力は本ファイルだけなので、再実行判定の対象を明示する。
    // 指定が無いと cargo はパッケージ配下の全ファイル（かつて runtime/assets にあった
    // 数百 MB のアセット等）を毎ビルド走査し、遅いうえに権限の壊れたフォルダがあると
    // 「failed to determine package fingerprint」で失敗する。
    println!("cargo:rerun-if-changed=build.rs");
    // NvOptimusEnablement / AmdPowerXpressRequestHighPerformance を PE エクスポートテーブルに載せる。
    // #[no_mangle] だけでは EXE のエクスポートテーブルに入らないため、
    // NVIDIA/AMD ドライバーが GetProcAddress で見つけられない。
    // /EXPORT リンカーフラグで強制的にエクスポートテーブルに追加する。
    //
    // 【-bins に限定する理由】両シンボルは bin（src/main.rs）だけが定義する。
    // エンジン本体をライブラリ（seed_engine）へ分けたため、`rustc-link-arg`（全リンク対象）の
    // ままだとライブラリの単体テスト実行ファイルにも /EXPORT が付き、未定義シンボルで
    // リンクに失敗する（cargo test が通らなくなる）。SEED.exe への効果は従来と同じ。
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        println!("cargo:rustc-link-arg-bins=/EXPORT:NvOptimusEnablement");
        println!("cargo:rustc-link-arg-bins=/EXPORT:AmdPowerXpressRequestHighPerformance");
    }
}
