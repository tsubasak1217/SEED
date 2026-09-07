// ============================================================
//  screenshot_ops.rs — IPC 駆動スクリーンショット（SCREENSHOT:）のアプリ側処理
// ------------------------------------------------------------
//  役割:
//    エディタ（＝ MCP 経由の AI）から届く `SCREENSHOT:{target},{path}` を受け取り、
//    「次に描いたフレームの提示テクスチャを PNG に落として結果を返す」までを面倒見る。
//
//  なぜランタイム側で撮るのか:
//    従来のエディタ側キャプチャは画面 DC からの BitBlt で、ウィンドウが画面に
//    映っていること（前面・最小化していない・ロックしていない）が前提だった。
//    ヘッドレス運用（ウィンドウを画面外に置いて AI だけが操作する）では成立しない。
//    GPU から直接読み戻せば、表示状態に一切依存せず撮れる。
//
//  処理の流れ:
//    1. process_ipc → handle_screenshot_request()
//         renderer::screenshot のグローバル要求キューへ積み、再描画を要求する。
//    2. フレーム末尾 RenderFrame::finish()
//         要求が残っていれば提示テクスチャ → 読み戻しバッファのコピーを積み、
//         present 後にマップして PNG を書き出し、結果キューへ積む。
//    3. about_to_wait → poll_screenshot_outcomes()
//         結果キューを吸い出して IPC 応答（SCREENSHOT_DONE / SCREENSHOT_ERROR）を送る。
//
//  「隠れていても撮れる」ことの担保:
//    ウィンドウが他ウィンドウの裏・画面外・最小化にあると OS が WM_PAINT を配送せず、
//    winit の RedrawRequested が止まる＝フレームが回らない＝撮影も完了しない。
//    そこで pump_frame_when_redraw_stalled() が、撮影要求が残っている間だけ
//    about_to_wait から直接フレームを回す（イベントループ自体は回っているため可能）。
// ============================================================

use winit::event_loop::ActiveEventLoop;

use crate::engine::core::renderer::screenshot::{self, CaptureOutcome};

use super::App;

/// 撮影完了応答の接頭辞。エディタ側パーサ（RuntimeManager.OnPipeMessage）と対になる。
const REPLY_DONE_PREFIX: &str = "SCREENSHOT_DONE:";
/// 撮影失敗応答の接頭辞。
const REPLY_ERROR_PREFIX: &str = "SCREENSHOT_ERROR:";
/// 応答内のフィールド区切り文字。
const REPLY_SEPARATOR: char = ',';

/// 撮影要求が残っている間、about_to_wait から強制的にフレームを回す最小間隔 [ms]。
///
/// 通常描画（60FPS ≒ 16ms）と同程度にしておけば、表示中は自然な再描画が先に走って
/// このポンプは不発になり、非表示時だけ実質のフレーム駆動になる。
const FORCED_FRAME_INTERVAL_MS: u64 = 16;

/// ヘッドレス動作を指示する環境変数名（エディタが子プロセス起動時に渡す）。
const ENV_HEADLESS: &str = "SEED_HEADLESS";
/// ヘッドレス有効とみなす環境変数の値。
const ENV_HEADLESS_ENABLED: &str = "1";

/// ヘッドレス動作かどうか（プロセス起動時に 1 度だけ解決する）。
///
/// ヘッドレスではエディタのウィンドウが画面外にあり、OS が WM_PAINT を配送しないため
/// `RedrawRequested` によるフレームループが最初から成立しない。撮影要求の有無に関わらず
/// イベントループ側からフレームを回し続けないと、Play しても時間が進まない。
static HEADLESS: std::sync::LazyLock<bool> = std::sync::LazyLock::new(|| {
    let enabled = std::env::var(ENV_HEADLESS).as_deref() == Ok(ENV_HEADLESS_ENABLED);
    if enabled {
        eprintln!("[SEED headless] {ENV_HEADLESS}={ENV_HEADLESS_ENABLED}: イベントループからフレームを常時駆動します");
    }
    enabled
});

/// `SCREENSHOT_DONE:` 応答文字列を組み立てる（書式をテストで固定するため関数化）。
pub(crate) fn format_screenshot_done(path: &str, width: u32, height: u32) -> String {
    format!("{REPLY_DONE_PREFIX}{path}{REPLY_SEPARATOR}{width}{REPLY_SEPARATOR}{height}")
}

/// `SCREENSHOT_ERROR:` 応答文字列を組み立てる。
pub(crate) fn format_screenshot_error(message: &str) -> String {
    format!("{REPLY_ERROR_PREFIX}{message}")
}

impl App {
    /// `SCREENSHOT:{target},{path}` を受理する（IPC ディスパッチから 1 行で呼ばれる）。
    ///
    /// 実際の読み戻しはフレーム末尾で行うため、ここでは要求を積んで再描画を促すだけ。
    /// target・パスの妥当性検証は `renderer::screenshot::request_capture` 側にあり、
    /// 不正なら即座に失敗結果が積まれる（次の `poll_screenshot_outcomes` で応答が飛ぶ）。
    pub(super) fn handle_screenshot_request(&mut self, target: &str, path: &str) {
        screenshot::request_capture(target, path);
        // 表示中ならこの要求で次フレームが来る。非表示中は配送されないが、
        // pump_frame_when_redraw_stalled() が代わりにフレームを回す。
        if let Some(window) = &self.window {
            window.request_redraw();
        }
    }

    /// 撮影結果をエディタへ返す（毎周 about_to_wait から呼ぶ。結果が無ければ即 return）。
    pub(super) fn poll_screenshot_outcomes(&mut self) {
        for outcome in screenshot::take_outcomes() {
            let reply = match outcome {
                CaptureOutcome::Done { path, width, height } => {
                    format_screenshot_done(&path.to_string_lossy(), width, height)
                }
                CaptureOutcome::Error { message } => format_screenshot_error(&message),
            };
            if let Some(ipc) = &self.ipc {
                ipc.send(&reply);
            }
        }
    }

    /// 表示由来の再描画が来ない状況で、イベントループ側からフレームを強制的に 1 枚回す。
    ///
    /// ウィンドウが画面外・最小化・他ウィンドウの裏にあると WM_PAINT が届かず
    /// RedrawRequested によるフレームループが停止する。
    /// 「イベントループは回っているが描画イベントだけが来ない」状態を、ここで補う。
    ///
    /// 発動条件は次のどちらか:
    ///   - ヘッドレス動作中（`SEED_HEADLESS=1`）… 常時。Play の時間も進める必要があるため。
    ///   - 撮影要求が残っている … 一時的。撮影が終わるまで。
    /// どちらでもない通常運転では即 return し、フレーム駆動に一切影響しない。
    ///
    /// 前提: `handle_resumed` 完了後（ウィンドウ・レンダラー初期化済み）にのみ動く。
    pub(super) fn pump_frame_when_redraw_stalled(&mut self, event_loop: &ActiveEventLoop) {
        // 図鑑サムネイル生成中も同じ理由でフレームを回す必要がある
        //（隔離ワールド線を描いて読み戻すまでジョブが進まないため）。
        if !*HEADLESS && !screenshot::has_pending_request() && self.thumbnail_job.is_none() {
            return;
        }
        // 初期化前・終了中はフレームを回せない。
        if self.window.is_none() || self.renderer.is_none() || event_loop.exiting() {
            return;
        }
        // 通常の再描画が回っているならそちらに任せる（二重描画を避ける）。
        if (self.last_frame_at.elapsed().as_millis() as u64) < FORCED_FRAME_INTERVAL_MS {
            return;
        }
        self.handle_redraw_requested(event_loop);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 応答書式がエディタ側の期待どおりであること（プロトコルの契約テスト）。
    #[test]
    fn reply_formats_match_protocol() {
        assert_eq!(
            format_screenshot_done(r"C:\temp\a.png", 1920, 1080),
            r"SCREENSHOT_DONE:C:\temp\a.png,1920,1080"
        );
        assert_eq!(
            format_screenshot_error("出力先を作成できません"),
            "SCREENSHOT_ERROR:出力先を作成できません"
        );
    }
}
