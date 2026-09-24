// ============================================================
//  surface_lifecycle.rs — 描画サーフェスの破棄と再生成
//
//  【担当】
//  - ApplicationHandler::suspended の実装本体（handle_suspended）
//  - 2 回目以降の ApplicationHandler::resumed の実装本体（handle_surface_resumed）
//    ※ 初回の resumed（ウィンドウ・GPU・シーンの初期化）は app_init.rs の handle_resumed
//  - 「サーフェスが無いのでフレームを描けない」判定（surface_missing）
//  - イベントループの制御モード（描画中 / 待機中）の定義
//  サーフェス以外の出入りの処理（セーブ・パイプラインキャッシュの書き出し、物理スレッドの停止・再開）は
//  background_lifecycle.rs が受け持つ（呼び分けは render.rs の suspended / resumed）。
//
//  【背景】
//  Android はアプリがバックグラウンドへ回るとネイティブウィンドウ（ANativeWindow）を破棄し、
//  前面へ戻ると新しいウィンドウを作り直す。winit はこれを suspended / resumed として通知する。
//    ・suspended : 描画サーフェス（wgpu::Surface）を必ず手放す。破棄済みウィンドウを指す
//                  サーフェスを持ち続けると、次の present でドライバがクラッシュする。
//    ・resumed   : 同じ winit Window から新しいサーフェスを作り直す。デバイス・パイプライン・
//                  シーンなどの GPU 資源は使い回し、作り直すのはサーフェスだけ。
//  デスクトップ（Windows）では resumed は起動時の 1 回だけで suspended は届かないため、
//  このファイルの処理は一切走らない（従来の SEED.exe の振る舞いは変わらない）。
// ============================================================

use winit::event_loop::{ActiveEventLoop, ControlFlow};

use super::App;

/// 描画中のイベントループ制御。毎フレーム request_redraw で回すため Poll（従来の値）。
pub(super) const ACTIVE_CONTROL_FLOW: ControlFlow = ControlFlow::Poll;

/// サーフェスが無い間（バックグラウンド中）のイベントループ制御。
///
/// Poll のままだと描画もしないのにイベントループが空回りし、端末の CPU と電池を使い続ける。
/// 次のイベント（resumed など）が届くまで眠らせる。
pub(super) const SUSPENDED_CONTROL_FLOW: ControlFlow = ControlFlow::Wait;

impl App {
    /// ApplicationHandler::suspended の実装本体。
    ///
    /// 描画サーフェスを破棄し、イベントループを待機モードへ切り替える。
    /// GPU デバイス・パイプライン・シーンなどはそのまま保持する（復帰を速くするため）。
    pub(super) fn handle_suspended(&mut self, event_loop: &ActiveEventLoop) {
        eprintln!("[SEED LIFECYCLE] suspended: 描画サーフェスを破棄して復帰を待ちます");
        if let Some(renderer) = &mut self.renderer {
            renderer.release_surface();
        }
        event_loop.set_control_flow(SUSPENDED_CONTROL_FLOW);
    }

    /// 2 回目以降の ApplicationHandler::resumed の実装本体。
    ///
    /// 既存の winit Window から描画サーフェスを作り直し、サイズ依存の状態を合わせ直して
    /// 描画ループを再開する。作り直せなかったときは描画を止めたまま次の resumed を待つ。
    ///
    /// # 戻り値
    /// サーフェスを作り直して描画を再開したら true（呼び出し元はシミュレーションも再開する）。
    /// 作り直せなかったら false（描画もシミュレーションも止めたまま）。
    pub(super) fn handle_surface_resumed(&mut self, event_loop: &ActiveEventLoop) -> bool {
        let Some(window) = self.window.clone() else {
            eprintln!("[SEED LIFECYCLE][WARN] resumed: ウィンドウが未生成のためサーフェスを作れません");
            return false;
        };

        let recreated = self
            .renderer
            .as_mut()
            .is_some_and(|renderer| renderer.recreate_surface(window.clone()));
        if !recreated {
            eprintln!("[SEED LIFECYCLE][WARN] resumed: サーフェスを作り直せませんでした。次の resumed を待ちます");
            return false;
        }

        // バックグラウンド中に端末が回転していれば、ウィンドウの大きさが変わっている。
        // カメラのアスペクト比・ID バッファ・入力の写像など、サイズ依存の状態を実サイズへ揃え直す。
        let size = window.inner_size();
        eprintln!("[SEED LIFECYCLE] resumed: サーフェスを再生成しました {}x{}", size.width, size.height);
        self.on_resize(size);

        event_loop.set_control_flow(ACTIVE_CONTROL_FLOW);
        window.request_redraw();
        true
    }

    /// 描画サーフェスが（一時的に）無く、フレームを描けない状態か。
    ///
    /// レンダラーはあるのにサーフェスが無い＝ Android でバックグラウンドへ回っている間だけ true。
    /// レンダラー自体が未初期化のときは false（従来どおりの経路に任せる）。
    pub(super) fn surface_missing(&self) -> bool {
        self.renderer
            .as_ref()
            .is_some_and(|renderer| !renderer.has_surface())
    }
}
