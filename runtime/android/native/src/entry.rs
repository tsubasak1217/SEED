// ============================================================
//  entry.rs — android_main（GameActivity から呼ばれるネイティブ側の入口）
//
//  【呼ばれ方】
//  Java の MainActivity（GameActivity 派生）が onCreate で libSEED.so を読み込み、
//  android-activity の glue（GameActivity_onCreate）が専用スレッドを立てて android_main を呼ぶ。
//  この関数が返るまでがネイティブ側の生存期間で、エンジンのイベントループはこのスレッドで回る。
//
//  【1 プロセス 1 回の制約】
//  winit の EventLoop はプロセス内で 1 度しか作れない（2 回目は RecreationAttempt エラー）。
//  Activity が破棄・再生成されると android_main が同じプロセスで再び呼ばれ得るため、
//    - Java 側（MainActivity.onDestroy）は破棄時にプロセスごと終了させる
//    - それでも 2 回目が来たら、ここでプロセスを終了して次回起動を新しいプロセスでやり直させる
//  という二段構えにしている（段階0 の方針。docs/android.md「現状の制限」）。
// ============================================================

use std::sync::atomic::{AtomicBool, Ordering};

use seed_engine::engine::core::app_base::App;
use winit::event_loop::EventLoop;
use winit::platform::android::EventLoopBuilderExtAndroid;
use winit::platform::android::activity::AndroidApp;

use crate::{debug_hooks, device_info, heartbeat, launch, logcat};

/// android_main に一度入ったか（同一プロセスでの 2 回目を検出する）。
static ANDROID_MAIN_ENTERED: AtomicBool = AtomicBool::new(false);

/// 同一プロセスで android_main が 2 回呼ばれたときの終了コード。
const EXIT_CODE_REENTERED: i32 = 1;

/// GameActivity から呼ばれるネイティブ側の入口（android-activity が extern "Rust" で参照する）。
///
/// # 引数
/// * `app` - この Activity に結び付いた AndroidApp（ウィンドウ・入力・データパスの窓口）
#[unsafe(no_mangle)]
fn android_main(app: AndroidApp) {
    // ログを最優先で整える（以降のエンジンの eprintln! と panic をすべて logcat へ流すため）。
    logcat::init();

    if ANDROID_MAIN_ENTERED.swap(true, Ordering::SeqCst) {
        logcat::error(
            "android_main が同一プロセスで 2 回目に呼ばれました。winit の EventLoop はプロセスで 1 度しか \
             作れないため、プロセスを終了して次回の起動を新しいプロセスでやり直させます。",
        );
        std::process::exit(EXIT_CODE_REENTERED);
    }

    logcat::info("android_main 開始");
    device_info::log(&app);

    // 検証用: システムプロパティ debug.seed.panic_test=1 のときだけ意図的に panic する
    //（panic が logcat に残ることの確認用。通常起動では何もしない）。
    debug_hooks::panic_if_requested();
    // 検証用: debug.seed.touch_test=1 のときだけ複数指の合成タッチ列を流す（通常起動では何もしない）。
    debug_hooks::request_touch_test_if_enabled();

    let args = launch::launch_args(&app);

    // Android の EventLoop は AndroidApp と結び付けて作る必要がある（素の EventLoop::new() は panic）。
    let event_loop = EventLoop::builder()
        .with_android_app(app)
        .build()
        .expect("EventLoop の生成に失敗しました");

    // 描画ループが回っているかを一定間隔で logcat へ出す（段階0 の生存確認）。
    heartbeat::spawn();

    App::run_with_event_loop(event_loop, args);
    logcat::info("イベントループが終了しました（android_main を抜けます）");
}
