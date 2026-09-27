// ============================================================
//  redraw/ — 「描く理由」の API と判定（アプリ基盤 W2-10a。正典は docs/redraw_policy.md）
//
//  【目的】（roadmap §3.8.3・§3.8.4・X-2）
//  アプリの画面はほとんど止まっている。毎フレーム描き続けると Pixel 6a で CPU 約 83%・GPU 約 290 ms/秒を使う
//  （W2-0 の実機 R-10）。描く理由が無いフレームが続いたら次のフレームを要求せず、イベントループを眠らせる（R-11 で CPU 6%）。
//
//  【既定は今のまま】project_settings.json の render_policy が "on_demand" のプロジェクトだけが止まる。
//  既定（キーなし・"continuous"）は毎フレーム描く＝既存のゲーム（WarashibeFishing）の動きは変わらない。
//
//  【構成】（どれも wgpu・winit に触れない。App への組み込みは app_base/app/redraw_hooks.rs）
//    reason.rs          … 描く理由の種類（RedrawReason）と集合（RedrawReasons。u32 のビット）
//    policy.rs          … 方針（continuous / on_demand）と止めるまでのフレームの数（project_settings.json）
//    gate.rs            … フレームの末尾の判定・止める・起こす（RedrawGate。純粋な状態機械）
//    schedule.rs        … 次に起きる時刻の合成と、別の時計（ジェスチャー・UTC）の予定の換算
//    script_requests.rs … スクリプトの要求（SEED.Redraw の Request・RequestAfter・KeepAlive・SetContinuous・Policy）
//    wake.rs            … 他のスレッド（IPC・JNI）から理由を積み、眠っているイベントループを起こす口
//
//  【止めている間の約束】（docs/redraw_policy.md §5）
//    - スクリプトの Update などのフェーズは呼ばない（フレームを回さない）
//    - SEED.Platform のイベント・IPC の命令は待ち行列に残り、起きた最初のフレームで処理する（届いた口が起こすので遅れない）
//    - 止めていた時間はゲームの時間（Time.DeltaTime・ElapsedTime）に入れない（起きた最初のフレームの dt を
//      RESUME_MAX_DELTA_SECS で切り詰める。背面から戻ったときの Clock::forget_elapsed と同じ考え）
//    - 物理のスレッドは止めない（W2-10 で決める。結果の待ち行列は上限で古いものから捨てる。physics/result_backlog.rs）
// ============================================================

pub mod gate;
pub mod policy;
pub mod reason;
pub mod schedule;
pub mod script_requests;
pub mod wake;

pub use gate::{NextFrame, RedrawGate, RedrawStats, WakeRecord};
pub use policy::{parse_redraw_settings, RedrawSettings, RenderPolicy};
pub use reason::{RedrawReason, RedrawReasons};

/// ログの印（`[SEED REDRAW]`。Android の logcat でもこの印で探す）。
pub const LOG_PREFIX: &str = "[SEED REDRAW]";

/// 止めていた後に起きた最初のフレームの dt の上限（秒）。
///
/// 止めていた時間（数秒〜数時間）をゲームの時間へ入れないため、起きた最初のフレームの dt をこの値で切り詰める。
/// 固定ステップ（ConstantUpdate）の 1 回ぶんと同じ長さ（1/60 秒）なので、起きた直後に固定ステップが取り戻しで連続して回らない。
pub const RESUME_MAX_DELTA_SECS: f32 = crate::engine::core::clock::FIXED_DELTA;
