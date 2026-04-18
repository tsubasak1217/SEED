mod application;
mod engine;

use application::App;
use winit::event_loop::{ControlFlow, EventLoop};

/// アプリケーションのエントリポイント。
///
/// # 処理フロー
/// 1. コマンドライン引数から `--parent-hwnd=<値>` を解析する
///    - 指定ありの場合: エディタ埋込みモード（子ウィンドウとして動作）
///    - 指定なしの場合: スタンドアロンモード（通常ウィンドウとして動作）
/// 2. `EventLoop` を生成する
/// 3. 制御フローを設定する
///    - スタンドアロン: `Wait`（イベント待機でCPUを節約）
///    - 埋込みモード  : `Poll`（連続描画ループ）
/// 4. `App` を初期化してイベントループを開始する
fn main() {
    let parent_hwnd = parse_parent_hwnd();

    let event_loop: EventLoop<()> = EventLoop::new().expect("Failed to create event loop");

    // 埋込みモードでは Poll で連続描画し、スタンドアロンでは Wait で省電力にする。
    if parent_hwnd.is_some() {
        event_loop.set_control_flow(ControlFlow::Poll);
    } else {
        event_loop.set_control_flow(ControlFlow::Wait);
    }

    let mut app = App::new(parent_hwnd);

    event_loop.run_app(&mut app).expect("Failed to run app");
}

/// `--parent-hwnd=<値>` 引数を解析して HWND 値を返す。
fn parse_parent_hwnd() -> Option<isize> {
    std::env::args()
        .find(|arg| arg.starts_with("--parent-hwnd="))
        .and_then(|arg| arg["--parent-hwnd=".len()..].parse::<isize>().ok())
}
