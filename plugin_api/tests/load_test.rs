// ============================================================
//  plugin_api/tests/load_test.rs — DLL ロードの統合テスト
//
//  cargo test -p seed-plugin-api で実行。
//  事前に sample_plugin DLL が target/debug/ に存在すること。
// ============================================================

use seed_plugin_api::{Plugin, PluginCreateFn, PLUGIN_ENTRY_FN};

/// DLL をロードして Plugin トレイトのメソッドが呼び出せることを確認する。
#[test]
fn test_load_sample_plugin() {
    // target/debug/sample_plugin.dll のパスを解決する
    let dll_path = {
        let exe = std::env::current_exe().expect("exe path");
        // target/debug/deps/... → target/debug/
        exe.parent().unwrap()
           .parent().unwrap()
           .join("sample_plugin.dll")
    };

    if !dll_path.exists() {
        eprintln!("スキップ: {} が見つかりません（先に cargo build -p sample_plugin を実行してください）", dll_path.display());
        return;
    }

    // DLL をロードして seed_create_plugin を呼ぶ
    let lib = unsafe { libloading::Library::new(&dll_path) }
        .expect("DLL ロード失敗");

    let create_fn: libloading::Symbol<PluginCreateFn> = unsafe {
        lib.get(PLUGIN_ENTRY_FN)
    }.expect("エントリ関数取得失敗");

    let raw = unsafe { create_fn() };
    assert!(!raw.is_null(), "seed_create_plugin が null を返した");

    let plugin: Box<dyn Plugin> = unsafe {
        *Box::from_raw(raw as *mut Box<dyn Plugin>)
    };

    assert_eq!(plugin.name(),        "SamplePlugin");
    assert_eq!(plugin.version(),     "0.1.0");
    assert!(!plugin.description().is_empty());

    let defs = plugin.field_defs();
    assert!(!defs.is_empty(), "フィールド定義が空");
    println!("フィールド定義 ({} 件):", defs.len());
    for d in &defs {
        println!("  - {} ({:?}): デフォルト={}", d.label, d.kind, d.default_value);
    }

    // _lib を最後に drop することで plugin より DLL が先に解放されないよう保持
    drop(plugin);
    drop(lib);
}
