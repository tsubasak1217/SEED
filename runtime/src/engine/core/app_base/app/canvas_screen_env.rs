// ============================================================
//  canvas_screen_env.rs — キャンバスのレイアウトが読む画面の情報（dp の倍率・安全領域）の公開（W2-1b）
//
//  【役割】
//  レイアウトの表（canvas_layout）は描画・エディタの選択・Play のポインタイベント・2D 物理がそれぞれ作る。
//  dp のルートキャンバスと安全領域の部品の結果がどの表でも同じになるよう、画面の情報をフレームの決まった
//  位置で 1 回だけ作って公開し、表を作るところ（CanvasLayoutEnv の組み立て）はそれを読むだけにする
//  （screen_publish.rs の SEED.Screen の写しと同じ流儀。値の出どころも同じ写し）。
//
//  【値】
//    - Play: dp の倍率 = 画面の写しの DPI ÷ 基準 DPI（表示倍率）、安全領域 = 写しの SafeArea をオーバーレイの
//            座標（描画ターゲットの中央が原点）へ写したもの。PC は報告が無いので画面全体（＝縮めない）。
//            環境変数 SEED_SIM_SAFE_AREA の模擬の切り欠きがあればそれ（platform/screen/simulated.rs）
//    - Edit（エディタ）: 使わない（CanvasScreenEnv::NONE。1 dp = 1 px・縮めない）
//  さらに表を作る側で、設計空間の表示（ビューポートのタブ）とビューポートの無い文脈（アクター編集タブ・
//  3D ワールドキャンバスの子）は NONE にする（`screen_env_for`）。
//
//  【呼ぶ位置】frame_renderer.rs のゲームロジックの前（Edit・一時停止）と、Play で画面の写しを更新した直後。
// ============================================================

use std::cell::Cell;

use crate::engine::core::canvas_layout::{dp_scale_from_dpi, target_rect_to_canvas_world, CanvasScreenEnv};
use crate::engine::core::scripting::screen_bridge::current_screen_snapshot;
use crate::engine::platform;
use crate::engine::platform::screen::ScreenSnapshot;

use super::{App, RuntimeMode};

thread_local! {
    /// このフレームのキャンバスの画面の情報（メインのスレッドの App だけが書き、表を作るところが読む）。
    static CANVAS_SCREEN_ENV: Cell<CanvasScreenEnv> = const { Cell::new(CanvasScreenEnv::NONE) };
}

/// このフレームのキャンバスの画面の情報（公開されていなければ NONE）。
pub(crate) fn current_canvas_screen() -> CanvasScreenEnv {
    CANVAS_SCREEN_ENV.with(Cell::get)
}

/// レイアウトの表を作る文脈に合わせた画面の情報。
///
/// ビューポートの無い文脈（アクター編集タブ・3D ワールドキャンバスの子）と設計空間の表示では使わない
/// （端末の画面ではないため。1 dp = 1 px・縮めない）。
///
/// # 引数
/// * `viewport_size` - 表の基準ビューポート（CanvasLayoutEnv.viewport_size）
/// * `design_space`  - ビューポートのタブの設計空間の表示か
pub(crate) fn screen_env_for(viewport_size: Option<[f32; 2]>, design_space: bool) -> CanvasScreenEnv {
    if viewport_size.is_none() || design_space {
        CanvasScreenEnv::NONE
    } else {
        current_canvas_screen()
    }
}

/// 画面の写しからキャンバスの画面の情報を作る【純関数】。
///
/// # 引数
/// * `snapshot`      - このフレームの画面の写し（SEED.Screen と同じ値）
/// * `reference_dpi` - 表示倍率 1.0 に当たる DPI（PlatformTraits::reference_dpi）
pub(crate) fn canvas_screen_from_snapshot(snapshot: &ScreenSnapshot, reference_dpi: f32) -> CanvasScreenEnv {
    let safe = snapshot.safe_area;
    CanvasScreenEnv {
        dp_scale: dp_scale_from_dpi(snapshot.dpi, reference_dpi),
        safe_area: Some(target_rect_to_canvas_world(
            [safe.x, safe.y, safe.width, safe.height],
            [snapshot.width, snapshot.height],
        )),
    }
}

impl App {
    /// このフレームのキャンバスの画面の情報を公開する（Play は画面の写しから、それ以外は NONE）。
    pub(super) fn publish_canvas_screen_env(&self) {
        let env = if self.mode == RuntimeMode::Play {
            canvas_screen_from_snapshot(&current_screen_snapshot(), platform::CURRENT.reference_dpi as f32)
        } else {
            CanvasScreenEnv::NONE
        };
        CANVAS_SCREEN_ENV.with(|cell| cell.set(env));
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::canvas_layout::CanvasRect;
    use crate::engine::platform::screen::{ScreenOrientation, ScreenRect};

    /// Pixel 6 相当の写し（1080×2400・420 dpi・上 136・下 63）→ 2.625 px/dp・中央原点の安全領域。
    #[test]
    fn snapshot_maps_to_dp_scale_and_centered_safe_area() {
        let snapshot = ScreenSnapshot {
            width: 1080.0,
            height: 2400.0,
            safe_area: ScreenRect { x: 0.0, y: 136.0, width: 1080.0, height: 2201.0 },
            orientation: ScreenOrientation::Portrait,
            dpi: 420.0,
        };
        let env = canvas_screen_from_snapshot(&snapshot, 160.0);
        assert_eq!(env.dp_scale, 2.625);
        assert_eq!(env.safe_area, Some(CanvasRect { min: [-540.0, -1064.0], max: [540.0, 1137.0] }));
    }

    /// 設計空間の表示・ビューポートの無い文脈では公開された値を使わない。
    #[test]
    fn design_space_and_no_viewport_ignore_the_screen() {
        CANVAS_SCREEN_ENV.with(|c| c.set(CanvasScreenEnv { dp_scale: 3.0, safe_area: None }));
        assert_eq!(screen_env_for(Some([100.0, 100.0]), false).dp_scale, 3.0);
        assert_eq!(screen_env_for(Some([100.0, 100.0]), true), CanvasScreenEnv::NONE);
        assert_eq!(screen_env_for(None, false), CanvasScreenEnv::NONE);
        CANVAS_SCREEN_ENV.with(|c| c.set(CanvasScreenEnv::NONE));
    }
}
