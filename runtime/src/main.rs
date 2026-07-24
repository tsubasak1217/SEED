// リリースビルド時はコンソールウィンドウを非表示にする。
// デバッグビルドはコンソールを残し、ログを確認できるようにする。
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod engine;

use engine::core::app_base::{App, LaunchArgs, RuntimeMode};

// NVIDIA Optimus / AMD PowerXpress に対して dGPU を強制使用させるシンボル。
// ドライバーが実行ファイル内のこれらのシンボルを参照して GPU を選択する。
#[unsafe(no_mangle)]
#[used]
pub static NvOptimusEnablement: u32 = 1;
#[unsafe(no_mangle)]
#[used]
pub static AmdPowerXpressRequestHighPerformance: u32 = 1;

fn main() {
    // Windows のシステムタイマ分解能を 1ms に引き上げる（既定は約 15.6ms）。
    //
    // 【理由】実測 [PERF] ログで、物理更新（3d/snap/2d の各同期セクション）のフレーム時間が
    //   間欠的に約 22ms / 44ms（＝15.6ms の 1.4 倍・2.8 倍）へスパイクしていた。値が
    //   スケジューラ量子の倍数であること・特定処理ではなく全セクション横断で起きることから、
    //   原因は物理コードではなく OS がレンダースレッドを 1〜3 量子ぶんデスケジュールしていること。
    //   timeBeginPeriod(1) で量子を細かくすると、スレッド起床・プリエンプションの粒度が上がり、
    //   これらのスパイクと、物理スレッドの sleep(1ms) 膨張・押し戻し recv_timeout 膨張が緩和される。
    //   副作用は消費電力/ウェイクアップの微増のみ（リアルタイムレンダラでは定番の対処）。
    raise_timer_resolution();

    App::run(parse_args());
}

/// システムタイマ分解能を 1ms に設定する（Windows のみ）。
///
/// プロセス終了時に OS が自動で元へ戻すため timeEndPeriod は呼ばない
/// （App::run はウィンドウが閉じるまで返らず、その時点でプロセスも終了する）。
fn raise_timer_resolution() {
    #[cfg(target_os = "windows")]
    unsafe {
        // winmm.dll の timeBeginPeriod。要求する最小分解能（ミリ秒）。
        const TIMER_RESOLUTION_MS: u32 = 1;
        windows_sys::Win32::Media::timeBeginPeriod(TIMER_RESOLUTION_MS);
    }
}

fn parse_args() -> LaunchArgs {
    let raw: Vec<String> = std::env::args().collect();

    let parent_hwnd = raw
        .iter()
        .find(|a| a.starts_with("--parent-hwnd="))
        .and_then(|a| a["--parent-hwnd=".len()..].parse::<isize>().ok());

    // エディタが渡す親プロセス PID。このプロセスが終了したら SEED.exe も自動終了する。
    let parent_pid = raw
        .iter()
        .find(|a| a.starts_with("--parent-pid="))
        .and_then(|a| a["--parent-pid=".len()..].parse::<u32>().ok());

    let mode = raw
        .iter()
        .find(|a| a.starts_with("--mode="))
        .map(|a| match &a["--mode=".len()..] {
            "play" => RuntimeMode::Play,
            _ => RuntimeMode::Edit,
        })
        .unwrap_or(if parent_hwnd.is_some() {
            RuntimeMode::Edit
        } else {
            RuntimeMode::Play
        });

    let pipe_name = raw
        .iter()
        .find(|a| a.starts_with("--pipe="))
        .map(|a| a["--pipe=".len()..].to_string());

    let assets_root = raw
        .iter()
        .find(|a| a.starts_with("--assets-root="))
        .map(|a| a["--assets-root=".len()..].to_string());

    let editor_resources = raw
        .iter()
        .find(|a| a.starts_with("--editor-resources="))
        .map(|a| a["--editor-resources=".len()..].to_string());

    let scene_path = raw
        .iter()
        .find(|a| a.starts_with("--scene="))
        .map(|a| a["--scene=".len()..].to_string());

    // Play 起動時にエディタから渡されるフラグ。SyncViewportSettings の到着前から有効にする。
    let play_collider_draw = raw.iter().any(|a| a == "--play-collider-draw=1");

    LaunchArgs {
        parent_hwnd,
        parent_pid,
        mode,
        pipe_name,
        assets_root,
        editor_resources,
        scene_path,
        play_collider_draw,
    }
}
