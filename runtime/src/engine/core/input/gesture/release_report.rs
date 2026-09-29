// ============================================================
//  gesture/release_report.rs — ドラッグの指を離した結果の控え（診断ログ `[SEED GESTURE] release` の材料。2026-09-29）
//
//  【何のためか】
//  離しの速度の規則（R1〜R3。docs/input_gestures.md §5.1）が実機でどう効いたかを確かめるため、ドラッグに勝った指を
//  離すたびに、使った速度・規則を当てない推定・0 にした理由をアリーナ（arena_set.rs）が控える。App（app/gesture_events.rs）が
//  フレームごとに取り出し、`platform::CURRENT.lifecycle_diag_log` が真（Android）のときだけ 1 行ずつ出す（PC では出さない）。
//  ここは World・ログの出力に触れない純ロジック（行の組み立てだけ）。
//
//  【行の形】
//    [SEED GESTURE] release id=<指の鍵> v_dp=(vx,vy) raw_dp=(rx,ry) rule=ok|stopped|lift_off|short_span lift_probe_dp=<速さ|->
//    v_dp … DragEnd・Fling に使った速度（dp/秒）。raw_dp … 規則を当てない推定（dp/秒）。
//    lift_probe_dp … R2 の材料（離す前の区間の速さ。決められなければ -）
// ============================================================

use super::pointer_log::PointerKey;
use super::recognizers::fling::ReleaseOutcome;

/// 診断ログの行の頭。
pub const RELEASE_LOG_TAG: &str = "[SEED GESTURE] release";

/// 決められない値の表記。
const NONE_MARK: &str = "-";

/// ドラッグの指を離した結果 1 件。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ReleaseReport {
    /// 指の鍵（OS の生 ID。Android は MotionEvent の pointer id。`[SEED TOUCH TIME] id=` と同じ）。
    pub pointer: PointerKey,
    /// 離した結果（画素/秒）。
    pub outcome: ReleaseOutcome,
    /// 1 dp の画素数（行を dp/秒で出すため）。
    pub dp_scale: f32,
}

impl ReleaseReport {
    /// 診断ログの 1 行を作る【純関数】（速度は dp/秒・小数なし）。
    pub fn diag_line(&self) -> String {
        let scale = if self.dp_scale.is_finite() && self.dp_scale > 0.0 { self.dp_scale } else { 1.0 };
        let dp = |v: [f32; 2]| [v[0] / scale, v[1] / scale];
        let v = dp(self.outcome.velocity);
        let raw = dp(self.outcome.raw);
        let probe = self.outcome.lift_off_probe.map_or_else(
            || NONE_MARK.to_string(),
            |p| {
                let p = dp(p);
                format!("{:.0}", (p[0] * p[0] + p[1] * p[1]).sqrt())
            },
        );
        format!(
            "{RELEASE_LOG_TAG} id={} v_dp=({:.0},{:.0}) raw_dp=({:.0},{:.0}) rule={} lift_probe_dp={probe}",
            self.pointer,
            v[0],
            v[1],
            raw[0],
            raw[1],
            self.outcome.rule.label(),
        )
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::input::gesture::velocity::ReleaseRule;

    /// 画素/秒を dp/秒へ直して 1 行にする（R2 の材料が無ければ -）。
    #[test]
    fn diag_line_is_in_dp() {
        let outcome = ReleaseOutcome {
            velocity: [0.0, 0.0],
            raw: [262.5, -525.0],
            rule: ReleaseRule::LiftOff,
            lift_off_probe: Some([0.0, 26.25]),
        };
        let r = ReleaseReport { pointer: 0, outcome, dp_scale: 2.625 };
        assert_eq!(
            r.diag_line(),
            "[SEED GESTURE] release id=0 v_dp=(0,0) raw_dp=(100,-200) rule=lift_off lift_probe_dp=10"
        );
        let r = ReleaseReport { outcome: ReleaseOutcome { lift_off_probe: None, rule: ReleaseRule::Ok, ..outcome }, ..r };
        assert!(r.diag_line().ends_with("rule=ok lift_probe_dp=-"), "{}", r.diag_line());
    }
}
