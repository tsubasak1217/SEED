// ============================================================
//  gpu_mem_ops.rs — GPU メモリの内訳の計測（renderer/gpu_mem）の App 側の口
//
//  【担当】
//  - フレームの始め・終わりの印（フレーム番号を進める・起動後の自動の内訳）
//  - 内訳を出すきっかけ（起動後〈最初の待機か AUTO_REPORT_FRAME フレーム目〉・背面へ回るとき・IPC GPU_MEM_REPORT）
//  - 内訳を組み立てる（記録の写し＋wgpu-hal の計数＋Vulkan のヒープの実使用量＋スワップチェインの見積り＋描画の構成）
//  計測が無効（既定）なら、どの口も旗を 1 つ読んで戻るだけ。仕組みの全体は docs/rendering_profiles.md §4。
// ============================================================

use crate::engine::core::renderer::gpu_mem;

use super::App;

/// IPC の応答の接頭辞（内訳の JSON を書いたファイルのパス）。
pub(super) const GPU_MEM_REPORT_DONE: &str = "GPU_MEM_REPORT_DONE:";
/// IPC の応答の接頭辞（出せなかった理由）。
pub(super) const GPU_MEM_REPORT_ERROR: &str = "GPU_MEM_REPORT_ERROR:";
/// 内訳の JSON を一時フォルダへ書くときのファイル名の接頭辞。
const REPORT_FILE_PREFIX: &str = "seed_gpu_mem_";

impl App {
    /// フレームの始めに呼ぶ（フレーム番号を進める。作り直しの判定に使う）。
    pub(super) fn gpu_mem_frame_start(&mut self) {
        gpu_mem::advance_frame();
    }

    /// フレームの終わりに呼ぶ。描画が回り続けるアプリ（continuous）は AUTO_REPORT_FRAME フレーム目に
    /// 起動後の内訳を 1 回出す（on_demand のアプリは先に最初の待機で出る＝gpu_mem_on_idle）。
    pub(super) fn gpu_mem_frame_end(&mut self) {
        if gpu_mem::current_frame() >= gpu_mem::AUTO_REPORT_FRAME && gpu_mem::take_auto_report_turn() {
            self.log_gpu_mem_report("起動後（自動）");
        }
    }

    /// 描画が待機へ入ったときに呼ぶ（最初の 1 回だけ起動後の内訳を出す）。
    pub(super) fn gpu_mem_on_idle(&mut self) {
        if gpu_mem::take_auto_report_turn() {
            self.log_gpu_mem_report("起動後（描画が待機へ入った）");
        }
    }

    /// 背面へ回るときに呼ぶ（計測が有効なら、背面に残る資源の内訳を出す）。
    pub(super) fn gpu_mem_on_background(&mut self) {
        if gpu_mem::enabled() {
            self.log_gpu_mem_report("背面へ回る");
        }
    }

    /// 今の内訳を組み立てる（計測が無効なら None）。
    pub(super) fn build_gpu_mem_report(&self, reason: &str) -> Option<gpu_mem::GpuMemReport> {
        if !gpu_mem::enabled() {
            return None;
        }
        let (entries, stats) = gpu_mem::registry::snapshot_global();
        let hal = self.renderer.as_ref().map(|r| r.gpu_hal_memory());
        let heaps = self.renderer.as_ref().and_then(|r| r.gpu_memory_heaps());
        let swapchain = self.renderer.as_ref().and_then(|r| r.swapchain_estimate());
        Some(gpu_mem::build_report(
            reason,
            gpu_mem::current_frame(),
            &self.render_profile_summary(),
            entries,
            stats,
            hal,
            heaps,
            swapchain,
        ))
    }

    /// 今の内訳をログへ出す（計測が無効なら何もしない）。
    pub(super) fn log_gpu_mem_report(&self, reason: &str) {
        if let Some(report) = self.build_gpu_mem_report(reason) {
            report.log();
        }
    }

    /// IPC `GPU_MEM_REPORT[:<パス>]` の本体。内訳をログへ出し、JSON をファイルへ書いて応答の 1 行を返す。
    ///
    /// * `path` … 書き出し先（空なら一時フォルダの seed_gpu_mem_<時刻>.json）
    pub(super) fn handle_gpu_mem_report_ipc(&self, path: &str) -> String {
        let Some(report) = self.build_gpu_mem_report("IPC GPU_MEM_REPORT") else {
            return format!(
                "{GPU_MEM_REPORT_ERROR}計測が無効です（環境変数 {}=1 か起動引数 --gpu-mem-log で起動してください）",
                gpu_mem::GPU_MEM_LOG_ENV
            );
        };
        report.log();
        let target = if path.trim().is_empty() {
            // 同じ秒に何度出しても衝突しないよう、ナノ秒までをファイル名に入れる。
            let stamp = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0);
            std::env::temp_dir().join(format!("{REPORT_FILE_PREFIX}{stamp}.json"))
        } else {
            std::path::PathBuf::from(path.trim())
        };
        match std::fs::write(&target, report.to_json()) {
            Ok(()) => format!("{GPU_MEM_REPORT_DONE}{}", target.display()),
            Err(e) => format!("{GPU_MEM_REPORT_ERROR}書き出しに失敗しました（{}）: {e}", target.display()),
        }
    }
}
