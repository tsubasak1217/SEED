// ============================================================
//  screen_publish.rs — フレームごとの画面情報（SEED.Screen）の公開
//
//  【役割】
//  スクリプトの `SEED.Screen`（Width / Height / SafeArea / Orientation / DPI）が読む値を、
//  フレームの決まった位置で 1 回だけ作って公開する。
//    入力: 描画面の実寸（親クライアント領域／ウィンドウ。1 回だけ読む）とそこから求めた描画ターゲットの寸法
//          （render_target_size_for。render_target_size_px と同じ規則）・
//          内部解像度固定のレターボックス（fixed_render_resolution）・OS の最新の報告
//          （platform::screen::report。描画面の大きさが一致するものだけ）・表示倍率（scale_factor）
//    計算: platform::screen::ScreenSnapshot::compute（純関数）
//    出力: core::scripting::screen_bridge::publish_screen_snapshot（スクリプトが読む写し）
//
//  【呼ぶ位置】frame_renderer.rs のゲームロジック（Play 中）の先頭。ポインタイベント（OnPointer*）と
//  スクリプトのフェーズより前なので、そのフレームのどのコールバックからも同じ値が見える。
//  回転・リサイズ（on_resize）や OS の報告がフレームの途中で届いても、写しは次のフレームまで変わらない。
// ============================================================

use crate::engine::core::scripting::screen_bridge;
use crate::engine::platform;
use crate::engine::platform::screen::{report, ScreenSnapshot, SnapshotInputs};

use super::App;

impl App {
    /// このフレームの画面情報を作ってスクリプトへ公開し、Android では変化を診断ログへ出す。
    pub(super) fn publish_screen_snapshot(&self) {
        let inputs = self.screen_snapshot_inputs();
        let snapshot = ScreenSnapshot::compute(&inputs);
        screen_bridge::publish_screen_snapshot(snapshot);
        super::screen_diag::observe(&snapshot, &inputs);
    }

    /// 画面情報の計算に要る値を App の状態から集める。
    fn screen_snapshot_inputs(&self) -> SnapshotInputs {
        // 描画面の実寸は on_resize と同じ規則（埋め込みなら親のクライアント領域、それ以外はウィンドウ）。
        // Android はウィンドウ＝サーフェス＝画面全体の物理ピクセル（OS の安全領域と同じ座標系）。
        // 実寸は 1 回だけ読み、描画ターゲットの寸法もそこから求める（リサイズ中に 2 回読むと、寸法と向きが
        // 別々の大きさから決まるフレームが出る。PC でウィンドウの大きさを変えた直後に 1 フレーム観測した）。
        let window_px = self.effective_window_size();
        let window_size = window_px.map(|size| (size.width, size.height));
        // 回転の直後は OS の報告と描画面の大きさが食い違うことがあるので、今の描画面の大きさの報告だけを使う
        // （選び方は report::select_for_frame。フレームごとに 1 回だけ呼ぶ前提の関数）。
        let report = window_size.and_then(|(width, height)| report::select_for_frame(width, height));
        SnapshotInputs {
            target_size: self.render_target_size_for(window_px),
            window_size,
            // 入力のレターボックス写像（sync_input_view_map）と同じ条件・同じ内部解像度。
            letterbox_internal: self.fixed_render_resolution(),
            report,
            scale_factor: self.window.as_ref().map(|window| window.scale_factor()),
            reference_dpi: platform::CURRENT.reference_dpi as f32,
        }
    }
}
