// ============================================================
//  gpu_timing/mod.rs — パスごとの GPU 時間の計測（タイムスタンプ。計測用・既定オフ。Android 段階D-2）
//
//  【何のためか】
//  CPU 側の計測（[PERF] の bf / finish、[SEED HEARTBEAT] の fps）では「GPU が重い」までしか分からない。
//  どのパス（影・G-Buffer・ライティング・SSGI・後処理…）が重いかを GPU のタイムスタンプで測り、
//  描画品質プリセットのつまみを選ぶ根拠にする（docs/android.md §22）。
//
//  【有効にする方法】（起動時に決まる。既定は無効＝デバイスの feature も要求しない＝従来どおり）
//    PC      … 環境変数 SEED_GPU_TIMING=1、または起動引数 --gpu-timing
//    Android … 起動オプション am start --es seed.gpu_timing 1
//  有効なら Renderer が TIMESTAMP_QUERY と TIMESTAMP_QUERY_INSIDE_ENCODERS を（アダプタが対応していれば）要求し、
//  App が GpuPassTimer を作る。3 秒ごとに `[SEED GPU] …` の 1 行（CPU のフレーム時間・取得待ち・提示と、
//  区間ごとの GPU 時間の平均。単位 ms）を標準エラー（Android は logcat）へ出す。
//
//  【構成】
//    segments.rs … 区間名（frame_renderer が節目で使う）
//    report.rs   … タイムスタンプの区間化と集計（純粋な処理・単体テスト）
//    timer.rs    … wgpu のクエリセット・読み戻し
// ============================================================

use std::sync::atomic::{AtomicBool, Ordering};

pub mod report;
pub mod segments;
mod timer;

pub use report::CpuFrameSample;
pub use timer::GpuPassTimer;

/// 計測の要求（起動時に 1 回だけ書く。Renderer::new が feature を要求するかの判断に読む）。
static REQUESTED: AtomicBool = AtomicBool::new(false);

/// 計測を要求する（Renderer::new より前に呼ぶこと。後から呼んでもデバイスの feature は増えない）。
pub fn request(enabled: bool) {
    REQUESTED.store(enabled, Ordering::Relaxed);
}

/// 計測が要求されているか。
pub fn requested() -> bool {
    REQUESTED.load(Ordering::Relaxed)
}

/// 計測に要るデバイスの feature。
pub const REQUIRED_FEATURES: wgpu::Features = wgpu::Features::TIMESTAMP_QUERY
    .union(wgpu::Features::TIMESTAMP_QUERY_INSIDE_ENCODERS);
