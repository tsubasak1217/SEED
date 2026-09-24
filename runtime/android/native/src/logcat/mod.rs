// ============================================================
//  logcat/mod.rs — ログを logcat（タグ SEED）へ集める
//
//  Android のアプリプロセスでは標準出力／標準エラーが /dev/null に捨てられるため、
//  エンジンの eprintln!（起動ログ・警告のほとんど）がそのままでは一切見えない。
//  ここで次の 3 経路をまとめて logcat へ流す:
//    1. log クレート（wgpu / winit / android-activity などの依存クレート）… android_logger
//    2. 標準出力・標準エラー（エンジンの println! / eprintln!）       … stdio_redirect（pipe + 転送スレッド）
//    3. panic                                                           … panic_hook（liblog へ同期で直接書く）
//
//  logcat の見方: `adb logcat -s SEED`（詳細は docs/android.md）。
// ============================================================

mod liblog;
mod panic_hook;
mod stdio_redirect;

use std::sync::Once;

use log::LevelFilter;

use liblog::Priority;

/// log クレート経由のログのうち logcat へ出す最低レベル。
/// Debug 以下は wgpu 等が大量に出すため、段階0 では Info 以上に絞る。
const LOG_CRATE_MAX_LEVEL: LevelFilter = LevelFilter::Info;

/// 既定より絞るクレート（モジュール接頭辞, 最低レベル）。
/// naga は SPIR-V を書き出すたびに「使わない関数を飛ばした」を Info で数行ずつ出し、
/// 起動時だけで数百行になって本当に見たいログが埋もれるため Warn 以上にする。
const QUIET_MODULES: &[(&str, LevelFilter)] = &[("naga", LevelFilter::Warn)];

/// 初期化を 1 度だけ行うためのガード（android_main は同一プロセスで再度呼ばれ得る）。
static INIT: Once = Once::new();

/// logcat への出力経路を初期化する（何度呼んでもよい。2 回目以降は何もしない）。
pub fn init() {
    INIT.call_once(|| {
        let mut filter = android_logger::FilterBuilder::new();
        filter.filter_level(LOG_CRATE_MAX_LEVEL);
        for &(module, level) in QUIET_MODULES {
            filter.filter_module(module, level);
        }
        android_logger::init_once(
            android_logger::Config::default()
                .with_max_level(LOG_CRATE_MAX_LEVEL)
                .with_tag(liblog::TAG_TEXT)
                .with_filter(filter.build()),
        );
        panic_hook::install();
        match stdio_redirect::redirect() {
            Ok(()) => info("logcat 出力を初期化しました（log / 標準出力 / 標準エラー / panic → タグ SEED）"),
            Err(err) => warn(&format!(
                "標準出力／標準エラーを logcat へ付け替えられませんでした（エンジンの eprintln! は見えません）: {err}"
            )),
        }
    });
}

/// 情報ログを 1 件出す（タグ SEED・優先度 INFO）。
pub fn info(text: &str) {
    liblog::write(Priority::Info, text);
}

/// 警告ログを 1 件出す（タグ SEED・優先度 WARN）。
pub fn warn(text: &str) {
    liblog::write(Priority::Warn, text);
}

/// エラーログを 1 件出す（タグ SEED・優先度 ERROR）。
pub fn error(text: &str) {
    liblog::write(Priority::Error, text);
}
