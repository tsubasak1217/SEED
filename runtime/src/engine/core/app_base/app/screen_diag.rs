// ============================================================
//  screen_diag.rs — 画面情報（SEED.Screen）の診断ログ（Android 段階A の検証用）
//
//  【目的】
//  Android では段階B までスクリプトが動かないため、スクリプトの `SEED.Screen` が返すはずの値
//  （フレームごとに公開した写し）を、変化したフレームだけ標準エラー（Android では logcat）へ出して確かめる。
//  値を読んでログを出すだけで、画面情報の計算・公開には一切関与しない。
//
//  【出す条件】platform::CURRENT.lifecycle_diag_log が真（Android）で、写し・描画面の実寸・使った報告の
//  どれかが前回のログから変わったときだけ（回転・安全領域の変化・リサイズ）。
//
//  【書式】1 行:
//    [SEED SCREEN] size=<幅>x<高さ> safe=(<x>,<y>,<幅>,<高さ>) orientation=<向き> dpi=<DPI> window=<実寸> report=<使った報告>
//  report は「frame=<報告時の描画面> rot=<回転> natural=<表示の自然な向きの大きさ> insets=(左,上,右,下)」か、
//  今の描画面に一致する報告が無ければ「none」。
// ============================================================

use std::cell::Cell;

use crate::engine::platform;
use crate::engine::platform::screen::{ScreenReport, ScreenSnapshot, SnapshotInputs};

/// 診断ログの行頭（logcat で grep する印）。
const LINE_TAG: &str = "[SEED SCREEN]";

/// 前回ログへ出した内容（写し・描画面の実寸・使った報告）。
type LoggedState = (ScreenSnapshot, Option<(u32, u32)>, Option<ScreenReport>);

thread_local! {
    /// 前回ログへ出した内容（App はメインスレッドだけで動くのでスレッドローカルで足りる）。
    static LAST_LOGGED: Cell<Option<LoggedState>> = const { Cell::new(None) };
}

/// 公開した写しを観測し、前回から変わっていれば 1 行出す（screen_publish.rs が毎フレーム呼ぶ）。
pub(super) fn observe(snapshot: &ScreenSnapshot, inputs: &SnapshotInputs) {
    if !platform::CURRENT.lifecycle_diag_log {
        return;
    }
    let state: LoggedState = (*snapshot, inputs.window_size, inputs.report);
    let changed = LAST_LOGGED.with(|last| {
        let changed = last.get() != Some(state);
        last.set(Some(state));
        changed
    });
    if changed {
        eprintln!("{}", format_line(snapshot, inputs.window_size, inputs.report));
    }
}

/// ログ 1 行を組み立てる【純関数】。
fn format_line(
    snapshot: &ScreenSnapshot,
    window_size: Option<(u32, u32)>,
    report: Option<ScreenReport>,
) -> String {
    let window = match window_size {
        Some((width, height)) => format!("{width}x{height}"),
        None => "none".to_string(),
    };
    let report = match report {
        Some(r) => format!(
            "frame={}x{} rot={} natural={}x{} insets=({},{},{},{})",
            r.frame_width,
            r.frame_height,
            r.rotation_quarter_turns,
            r.natural_width,
            r.natural_height,
            r.insets.left,
            r.insets.top,
            r.insets.right,
            r.insets.bottom,
        ),
        None => "none".to_string(),
    };
    let safe = &snapshot.safe_area;
    format!(
        "{LINE_TAG} size={}x{} safe=({},{},{},{}) orientation={} dpi={} window={window} report={report}",
        snapshot.width,
        snapshot.height,
        safe.x,
        safe.y,
        safe.width,
        safe.height,
        snapshot.orientation.label(),
        snapshot.dpi,
    )
}

// ============================================================
//  テスト（書式。検証手順が grep する前提を固定する）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::platform::screen::{EdgeInsets, ScreenOrientation, ScreenRect};

    /// 報告ありの行: 値・描画面・報告の内訳が 1 行に並ぶ。
    #[test]
    fn line_contains_values_and_report() {
        let snapshot = ScreenSnapshot {
            width: 2400.0,
            height: 1080.0,
            safe_area: ScreenRect { x: 136.0, y: 0.0, width: 2264.0, height: 1017.0 },
            orientation: ScreenOrientation::LandscapeLeft,
            dpi: 420.0,
        };
        let report = ScreenReport {
            frame_width: 2400,
            frame_height: 1080,
            insets: EdgeInsets { left: 136, top: 0, right: 0, bottom: 63 },
            rotation_quarter_turns: 1,
            natural_width: 1080,
            natural_height: 2400,
        };
        assert_eq!(
            format_line(&snapshot, Some((2400, 1080)), Some(report)),
            "[SEED SCREEN] size=2400x1080 safe=(136,0,2264,1017) orientation=LandscapeLeft dpi=420 \
             window=2400x1080 report=frame=2400x1080 rot=1 natural=1080x2400 insets=(136,0,0,63)"
        );
    }

    /// 報告も描画面も無い行。
    #[test]
    fn line_without_report_says_none() {
        let snapshot = ScreenSnapshot::fallback([1280.0, 720.0], 160.0);
        assert_eq!(
            format_line(&snapshot, None, None),
            "[SEED SCREEN] size=1280x720 safe=(0,0,1280,720) orientation=LandscapeLeft dpi=160 window=none report=none"
        );
    }
}
