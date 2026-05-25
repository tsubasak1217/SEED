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
    App::run(parse_args());
}

fn parse_args() -> LaunchArgs {
    let raw: Vec<String> = std::env::args().collect();

    let parent_hwnd = raw.iter()
        .find(|a| a.starts_with("--parent-hwnd="))
        .and_then(|a| a["--parent-hwnd=".len()..].parse::<isize>().ok());

    // エディタが渡す親プロセス PID。このプロセスが終了したら SEED.exe も自動終了する。
    let parent_pid = raw.iter()
        .find(|a| a.starts_with("--parent-pid="))
        .and_then(|a| a["--parent-pid=".len()..].parse::<u32>().ok());

    let mode = raw.iter()
        .find(|a| a.starts_with("--mode="))
        .map(|a| match &a["--mode=".len()..] {
            "play" => RuntimeMode::Play,
            _      => RuntimeMode::Edit,
        })
        .unwrap_or(if parent_hwnd.is_some() { RuntimeMode::Edit } else { RuntimeMode::Play });

    let pipe_name = raw.iter()
        .find(|a| a.starts_with("--pipe="))
        .map(|a| a["--pipe=".len()..].to_string());

    let assets_root = raw.iter()
        .find(|a| a.starts_with("--assets-root="))
        .map(|a| a["--assets-root=".len()..].to_string());

    let editor_resources = raw.iter()
        .find(|a| a.starts_with("--editor-resources="))
        .map(|a| a["--editor-resources=".len()..].to_string());

    let scene_path = raw.iter()
        .find(|a| a.starts_with("--scene="))
        .map(|a| a["--scene=".len()..].to_string());

    // Play 起動時にエディタから渡されるフラグ。SyncViewportSettings の到着前から有効にする。
    let play_collider_draw = raw.iter().any(|a| a == "--play-collider-draw=1");

    LaunchArgs { parent_hwnd, parent_pid, mode, pipe_name, assets_root, editor_resources, scene_path, play_collider_draw }
}
