fn main() {
    // NvOptimusEnablement / AmdPowerXpressRequestHighPerformance を PE エクスポートテーブルに載せる。
    // #[no_mangle] だけでは EXE のエクスポートテーブルに入らないため、
    // NVIDIA/AMD ドライバーが GetProcAddress で見つけられない。
    // /EXPORT リンカーフラグで強制的にエクスポートテーブルに追加する。
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        println!("cargo:rustc-link-arg=/EXPORT:NvOptimusEnablement");
        println!("cargo:rustc-link-arg=/EXPORT:AmdPowerXpressRequestHighPerformance");
    }
}
