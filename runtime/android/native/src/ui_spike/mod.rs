// ============================================================
//  ui_spike/ — アプリ基盤 W2-0 のスパイク（Android の糊の側。既定で無効。docs/app_platform_roadmap.md §3.8）
//
//  【目的】E-06（Android の文字入力）を決めるために、GameActivity の文字入力（GameTextInput）を
//  winit の外から android-activity の API（AndroidApp の show_soft_input / hide_soft_input / set_ime_editor_info /
//  text_input_state / set_text_input_state）で使えるかを確かめる。winit は AndroidApp を EventLoop に握らせたまま。
//    ime_probe … 命令のファイルでソフトキーボードを出し入れし、入力の状態（文字列・選択・変換中の区間）の変化を logcat へ出す
//  Java 側の通知（状態の変化・完了などのアクション・キーボードの表示・IME の高さ）は MainActivity の上書きが
//  ImeSpikeLog（Java）で logcat へ出す（winit は TextEvent / TextAction を読み捨てるため。§3.8）。
//
//  【有効にする方法】am start … --es seed.ui_spike 'ime'（起動オプションを渡すのはデバッグ版の APK だけ）。
//  書式はエンジンの engine::core::ui_spike::config（同じ純関数で読む）。指定が無ければ何もしない。
// ============================================================

mod ime_probe;

use seed_engine::engine::core::ui_spike::UiSpikeConfig;
use winit::platform::android::activity::AndroidApp;

use crate::logcat;

/// 指定に ime があれば文字入力の試作を始める（android_main で EventLoop を作る前に 1 回だけ呼ぶ）。
///
/// # 引数
/// * `app`  - android_main が受け取った AndroidApp（複製して試作のスレッドへ渡す。winit へ渡す元はそのまま）
/// * `spec` - 起動オプション seed.ui_spike の値（無ければ None＝何もしない）
pub fn start_if_requested(app: &AndroidApp, spec: Option<&str>) {
    let Some(spec) = spec else { return };
    let (config, _warnings) = UiSpikeConfig::parse_lenient(spec);
    if !config.ime {
        return;
    }
    logcat::info("[SEED IME SPIKE] 文字入力（IME）の試作を始めます（seed.ui_spike に ime）");
    ime_probe::spawn(app.clone());
}
