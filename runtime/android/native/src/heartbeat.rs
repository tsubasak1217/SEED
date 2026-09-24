// ============================================================
//  heartbeat.rs — 描画ループの生存確認ログ
//
//  一定間隔で「スワップチェーンへ提示したフレーム数」をエンジンから読み、
//  区間の増分と平均 fps を logcat へ出す。バックグラウンド中（サーフェス無し）は増分が 0 になり、
//  復帰すると再び増える — 回転・ホーム復帰の検証で「描画が止まっていないか」を一目で確認できる。
//
//  値は seed_engine の present_counter（present 直後にアトミック加算）から読むだけなので、
//  描画スレッドとは独立に動き、描画処理へ一切干渉しない。
// ============================================================

use std::time::{Duration, Instant};

use seed_engine::engine::core::renderer::present_counter;

use crate::logcat;

/// 生存確認ログを出す間隔。
const HEARTBEAT_INTERVAL: Duration = Duration::from_secs(3);

/// 生存確認スレッドの名前（logcat の tid 表示・デバッガでの識別用）。
const HEARTBEAT_THREAD_NAME: &str = "seed-heartbeat";

/// 生存確認スレッドを起動する（プロセス終了まで常駐する）。
pub fn spawn() {
    let spawned = std::thread::Builder::new()
        .name(HEARTBEAT_THREAD_NAME.to_string())
        .spawn(run);
    if let Err(err) = spawned {
        logcat::warn(&format!("生存確認スレッドを起動できませんでした（描画には影響しません）: {err}"));
    }
}

/// 生存確認スレッドの本体。
fn run() {
    let mut last_count = present_counter::presented_frame_count();
    let mut last_at = Instant::now();
    loop {
        std::thread::sleep(HEARTBEAT_INTERVAL);

        let count = present_counter::presented_frame_count();
        let elapsed = last_at.elapsed();
        let delta = count.saturating_sub(last_count);
        logcat::info(&format!(
            "[SEED HEARTBEAT] presented_frames total={count} +{delta} in {:.1}s ({:.1} fps)",
            elapsed.as_secs_f64(),
            average_fps(delta, elapsed),
        ));

        last_count = count;
        last_at = Instant::now();
    }
}

/// 区間の提示フレーム数と経過時間から平均 fps を求める【純関数】（経過 0 のときは 0）。
fn average_fps(frames: u64, elapsed: Duration) -> f64 {
    let secs = elapsed.as_secs_f64();
    if secs > 0.0 { frames as f64 / secs } else { 0.0 }
}
