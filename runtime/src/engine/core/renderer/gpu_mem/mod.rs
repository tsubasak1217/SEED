// ============================================================
//  gpu_mem/mod.rs — GPU メモリの内訳の計測（デバッグ用・既定オフ）
//
//  【何のためか】
//  Android の `dumpsys meminfo` の "GL mtrack"（＝プロセスが確保した GPU メモリ）が、2D の UI だけの
//  アプリ（Wake or Pay）でも 700 MB を超えた。どの資源（G-Buffer・影・GI・HDR の中間・文字…）が
//  どれだけ占めているかを「測って」から減らすための仕組み。
//
//  【何を測るか】（3 つの数字を並べて出す。docs/rendering_profiles.md §4）
//    1. 追跡した資源の内訳 … エンジンの `create_texture` / `create_buffer` / `create_buffer_init` /
//       `create_texture_with_data` を、この下の `GpuMemDeviceExt`（`*_tracked`）経由で呼ぶ。
//       呼び出し元のソースの場所（#[track_caller]）とラベルから分類（category.rs）を決め、
//       大きさ（size.rs。形式・寸法・ミップ・層から論理的に計算した値）を記録する（registry.rs）。
//    1'. wgpu-hal の計数 … Cargo の wgpu の counters 機能で、資源ごとに確保したブロック（アロケータの
//       2 の冪への切り上げ込み）の合計と、GPU メモリの確保（vkAllocateMemory）の回数を読む。
//    2. 実際の確保 … Vulkan の VK_EXT_memory_budget が返す「このプロセスがヒープで使っている量」
//       （heap_budget.rs）。アロケータのまとめ取り（大きな塊で確保して切り分ける）・2 の冪への切り上げ・
//       スワップチェイン・ドライバ内部の分まで含む。1 との差が「資源以外」の量。
//    3. スワップチェインの見積り … 幅 × 高さ × 1 画素のバイト数 × 枚数（`Renderer` が渡す）。
//
//  【有効にする方法】（起動時に決まる。既定は無効＝記録もしない＝従来と同じ速さ）
//    PC      … 環境変数 SEED_GPU_MEM_LOG=1、または起動引数 --gpu-mem-log
//    Android … 起動オプション am start --es seed.gpu_mem_log 1
//  有効なら、起動して描画が落ち着いたとき（最初の待機・または AUTO_REPORT_FRAME フレーム目）・
//  背面へ回ったとき・IPC `GPU_MEM_REPORT` を受けたときに `[SEED GPU MEM] …` の内訳をログへ出す。
//
//  【構成】
//    category.rs    … 分類（G-Buffer・影…）と、ラベル・ソースの場所から分類を決める規則の表（データ）
//    size.rs        … テクスチャ・バッファの大きさ（バイト）の計算（純関数・単体テスト）
//    registry.rs    … 記録の表（作り直しの判定・合計）
//    track.rs       … `wgpu::Device` の拡張（*_tracked）。呼び出し元の場所を拾って記録する
//    report.rs      … 内訳の集計・ログの行・JSON
//    heap_budget.rs … Vulkan のヒープの実使用量（VK_EXT_memory_budget）
//
//  【限界】（docs/rendering_profiles.md §4.3）
//    - wgpu は資源の破棄を外へ知らせないので、「同じ場所・同じラベルで後の世代（フレームの始め・窓の大きさの
//      変化で進む）に作り直したら前のものは捨てた」とみなして生存を推定する（registry.rs）。資産ごとに作る場所（スプライト・モデル）は
//      作り直しでなく積み増しとして数える（解放は追えない＝作った累計）。
//    - 大きさは論理値（ドライバの整列・圧縮・タイルの余白は含まない）。実際の確保は 2 で見る。
// ============================================================

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::OnceLock;

pub mod category;
pub mod heap_budget;
pub mod registry;
pub mod report;
pub mod size;
mod track;

pub use category::GpuMemCategory;
pub use heap_budget::{query_heap_usage, HeapUsage};
pub use report::{build_report, GpuMemReport, HalMemory, SwapchainEstimate};
pub use track::GpuMemDeviceExt;

/// 計測を有効にする環境変数（値が 1 のとき。起動引数 --gpu-mem-log・Android の起動オプション seed.gpu_mem_log と同じ）。
pub const GPU_MEM_LOG_ENV: &str = "SEED_GPU_MEM_LOG";

/// GPU_MEM_LOG_ENV を有効とみなす値。
pub const GPU_MEM_LOG_ENV_ON: &str = "1";

/// 起動後の自動の内訳を出すフレーム（描画が待機へ入らずに回り続けるとき＝continuous の保険）。
/// on_demand のアプリは最初に待機へ入ったときに出す（redraw_hooks.rs）。60 fps で約 2 秒。
pub const AUTO_REPORT_FRAME: u64 = 120;

/// ログの行頭の印。
pub const LOG_TAG: &str = "[SEED GPU MEM]";

/// 計測が有効か（起動時に 1 回だけ書く）。
static ENABLED: AtomicBool = AtomicBool::new(false);

/// 今のフレーム番号（フレームの始めに 1 ずつ進める。起動中の確保は 0）。表示と自動の内訳の時期に使う。
static FRAME: AtomicU64 = AtomicU64::new(0);

/// 今の世代（フレームの始めと、資源を作り直す節目〈窓の大きさの変化〉で 1 ずつ進める）。
/// 「同じ場所・同じラベルで後の世代に作り直した＝前のものを捨てた」の判定に使う（registry.rs）。
/// 同じ世代の中で同じ場所に作ったものは並んで生きているとみなす（ループ・2 つの持ち主）。
static GENERATION: AtomicU64 = AtomicU64::new(0);

/// 起動後の自動の内訳をもう出したか（1 回だけ出すため）。
static AUTO_REPORTED: AtomicBool = AtomicBool::new(false);

/// 節目の 1 行（checkpoint）で使うアダプタとデバイス（計測が有効なときだけ Renderer::new が登録する）。
/// 深い所（DrawContext::new の途中など）でもヒープの使用量・wgpu-hal の計数を読めるようにするため。
static PROBE: OnceLock<(wgpu::Adapter, wgpu::Device)> = OnceLock::new();

/// 計測を有効にする（Renderer::new より前に呼ぶこと。後から有効にすると、それまでの確保は記録されない）。
///
/// 環境変数 SEED_GPU_MEM_LOG=1 も見る（起動引数・起動オプションのどれか 1 つで有効）。
pub fn request(enabled_by_launch: bool) {
    let by_env = std::env::var(GPU_MEM_LOG_ENV).is_ok_and(|v| v.trim() == GPU_MEM_LOG_ENV_ON);
    let enabled = enabled_by_launch || by_env;
    ENABLED.store(enabled, Ordering::Relaxed);
    if enabled {
        eprintln!(
            "{LOG_TAG} 計測を有効にしました（資源の作成を記録し、起動後・背面へ回るとき・IPC GPU_MEM_REPORT で内訳を出します）"
        );
    }
}

/// 計測が有効か。
pub fn enabled() -> bool {
    ENABLED.load(Ordering::Relaxed)
}

/// フレームの始めに呼ぶ（フレーム番号と世代を 1 ずつ進める）。無効でも数えるだけなので安い。
pub fn advance_frame() -> u64 {
    GENERATION.fetch_add(1, Ordering::Relaxed);
    FRAME.fetch_add(1, Ordering::Relaxed) + 1
}

/// 資源を作り直す節目（窓の大きさの変化など）で呼ぶ（世代だけを 1 進める）。
///
/// 起動中（最初のフレームの前）には窓の大きさの知らせが何度か届き、そのたびに同じ資源（ID バッファなど）を
/// 作り直す。世代を進めておくと、前のものを「捨てた」と正しく数えられる。
pub fn advance_generation() {
    GENERATION.fetch_add(1, Ordering::Relaxed);
}

/// 今のフレーム番号。
pub fn current_frame() -> u64 {
    FRAME.load(Ordering::Relaxed)
}

/// 今の世代。
pub fn current_generation() -> u64 {
    GENERATION.load(Ordering::Relaxed)
}

/// 節目の 1 行のためにアダプタとデバイスを登録する（Renderer::new がデバイスを作った直後に 1 回。計測が無効なら何もしない）。
pub fn install_probe(adapter: &wgpu::Adapter, device: &wgpu::Device) {
    if enabled() {
        let _ = PROBE.set((adapter.clone(), device.clone()));
    }
}

/// 節目の 1 行をログへ出す（計測が無効・登録前なら何もしない）。
///
/// 「実際の確保（ヒープの使用量）」が資源の合計より大きいとき、どの処理の前後で増えたかを切り分けるために、
/// 追跡した合計・wgpu-hal の確保ブロック（と確保の回数）・ヒープの使用量を並べる。
pub fn checkpoint(label: &str) {
    if !enabled() {
        return;
    }
    let Some((adapter, device)) = PROBE.get() else {
        return;
    };
    let (entries, _) = registry::snapshot_global();
    let tracked: u64 = entries.iter().map(|e| e.bytes).sum();
    let hal = HalMemory::from_counters(&device.get_internal_counters());
    let heap = query_heap_usage(adapter)
        .map(|heaps| report::mib(heap_budget::total_usage(&heaps)))
        .unwrap_or_else(|| "取れません".to_string());
    eprintln!(
        "{LOG_TAG} 節目 {label}: 追跡 {}・wgpu-hal {}（確保 {} 回）・ヒープの使用量 {heap}",
        report::mib(tracked),
        report::mib(hal.total_bytes()),
        hal.memory_allocations,
    );
}

/// 起動後の自動の内訳をまだ出していなければ true を返し、出したことにする（1 回だけ true）。
/// 計測が無効なら常に false。
pub fn take_auto_report_turn() -> bool {
    enabled() && !AUTO_REPORTED.swap(true, Ordering::Relaxed)
}
