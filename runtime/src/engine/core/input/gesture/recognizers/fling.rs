// ============================================================
//  gesture/recognizers/fling.rs — フリックの規則（離した時点の速度。W2-2。R2 は 2026-09-29）
//
//  ドラッグに勝った指を離したとき、その時点の速度（velocity.rs が直近の標本から推定）を軸へ射影し、
//  速さ（長さ）がフリックの最小の速度（既定 50 dp/秒）以上ならフリックとする。速さが上限（既定 8000 dp/秒）を
//  超えていたら、向きを保ったまま上限の長さへ切り詰める（Android の VelocityTracker.computeCurrentVelocity の
//  maxVelocity・Flutter の kMaxFlingVelocity と同じ）。ドラッグの終わり（DragEnd）の速度にも同じ切り詰めを使う。
//
//  【離しの速度の規則】（docs/input_gestures.md §5.1）R1（止まっていた）・R3（幅が短すぎる）は velocity.rs が当てる。
//  R2（持ち上げの揺れ）はここで当てる: 離す前の区間の速度（velocity.rs の `before_lift_off`）を同じ軸へ射影し、
//  最小のフリックの速度より遅ければ離しの速度を 0 にする（軸を知っているのがここなので。横だけのスワイプで
//  縦に揺れていても、横に止まっていれば止まっていた）。0 にした理由は `ReleaseOutcome::rule`（診断ログの rule=）。
// ============================================================

use crate::engine::components::GestureDragAxis;

use super::super::thresholds::GestureMetrics;
use super::super::velocity::{ReleaseEstimate, ReleaseRule};
use super::drag::project;

/// 離した結果（DragEnd・Fling の速度と、診断の材料）。速度は画素/秒・軸へ射影して上限で切り詰めたもの。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ReleaseOutcome {
    /// DragEnd・Fling に使う速度（規則に当たれば 0）。
    pub velocity: [f32; 2],
    /// 規則（R1〜R3）を当てない推定（診断用。射影・切り詰め済み）。
    pub raw: [f32; 2],
    /// 0 にした理由（当たらなければ Ok）。
    pub rule: ReleaseRule,
    /// R2 の材料（離す前の区間の速度。射影済み。決められなければ None）。
    pub lift_off_probe: Option<[f32; 2]>,
}

/// ドラッグの終わりの速度（軸へ射影し、上限で切り詰めたもの。画素/秒）【純関数】。
pub fn release_velocity(estimate: [f32; 2], axis: GestureDragAxis, metrics: &GestureMetrics) -> [f32; 2] {
    let v = project(estimate, axis);
    let speed = length(v);
    if speed.is_finite() && speed > metrics.max_fling_px && speed > 0.0 {
        let k = metrics.max_fling_px / speed;
        [v[0] * k, v[1] * k]
    } else if speed.is_finite() {
        v
    } else {
        [0.0, 0.0]
    }
}

/// 離した結果を決める【純関数】（R1・R3 は推定の理由のまま、R2 はここで当てる）。
///
/// # 引数
/// * `estimate` - velocity.rs の `release_estimate` の結果
/// * `axis`     - ドラッグの軸（射影する向き）
/// * `metrics`  - 最小のフリックの速度（R2 の閾値）と上限
pub fn release_outcome(estimate: &ReleaseEstimate, axis: GestureDragAxis, metrics: &GestureMetrics) -> ReleaseOutcome {
    let raw = release_velocity(estimate.raw, axis, metrics);
    let lift_off_probe = estimate.before_lift_off.map(|p| project(p, axis));
    let zero = |rule| ReleaseOutcome { velocity: [0.0, 0.0], raw, rule, lift_off_probe };
    if estimate.rule != ReleaseRule::Ok {
        return zero(estimate.rule);
    }
    // R2: 離す前の区間で指がほぼ止まっていた（区間の速さが最小のフリックの速度未満）→ 最後の動きは持ち上げの揺れ
    if lift_off_probe.is_some_and(|p| length(p) < metrics.min_fling_px) {
        return zero(ReleaseRule::LiftOff);
    }
    ReleaseOutcome { velocity: release_velocity(estimate.velocity, axis, metrics), raw, rule: ReleaseRule::Ok, lift_off_probe }
}

/// 離したときの速度がフリックか（速さが最小の速度以上）【純関数】。
///
/// # 引数
/// * `velocity` - `release_velocity`（`release_outcome` の velocity）の結果
pub fn is_fling(velocity: [f32; 2], metrics: &GestureMetrics) -> bool {
    let speed = length(velocity);
    speed >= metrics.min_fling_px && speed > 0.0
}

/// ベクトルの長さ【純関数】。
fn length(v: [f32; 2]) -> f32 {
    (v[0] * v[0] + v[1] * v[1]).sqrt()
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::input::gesture::thresholds::GestureThresholds;

    /// 最小の速度の境目（dp の倍率で画素/秒へ）。
    #[test]
    fn min_velocity_boundary() {
        let m = GestureThresholds::default().metrics(2.0);
        assert!(!is_fling([99.0, 0.0], &m), "50 dp/s × 2 = 100 px/s 未満");
        assert!(is_fling([100.0, 0.0], &m));
        assert!(!is_fling([0.0, 0.0], &m));
    }

    /// 上限で向きを保って切り詰め、軸へ射影する。
    #[test]
    fn clamps_and_projects() {
        let m = GestureMetrics::default();
        let v = release_velocity([30000.0, 40000.0], GestureDragAxis::Any, &m);
        assert!((v[0] - 4800.0).abs() < 0.5 && (v[1] - 6400.0).abs() < 0.5, "{v:?}");
        let h = release_velocity([300.0, 4000.0], GestureDragAxis::Horizontal, &m);
        assert_eq!(h, [300.0, 0.0], "横だけは縦の速度を捨てる");
        assert_eq!(release_velocity([f32::NAN, 1.0], GestureDragAxis::Any, &m), [0.0, 0.0]);
    }

    /// 推定の材料を作る。
    fn est(velocity: [f32; 2], rule: ReleaseRule, before_lift_off: Option<[f32; 2]>) -> ReleaseEstimate {
        ReleaseEstimate { velocity, raw: [900.0, 20.0], rule, before_lift_off }
    }

    /// R2: 離す前の区間の速さが最小の速度（50 px/s）未満なら 0・理由 lift_off。以上・決められない（None）なら推定のまま。
    /// R1・R3 の理由はそのまま出し、規則を当てない値は射影・切り詰めて残す。
    #[test]
    fn release_outcome_applies_lift_off_and_keeps_reasons() {
        let m = GestureMetrics::default();
        let any = GestureDragAxis::Any;
        let o = release_outcome(&est([900.0, 20.0], ReleaseRule::Ok, Some([30.0, 20.0])), any, &m);
        assert_eq!((o.velocity, o.rule), ([0.0, 0.0], ReleaseRule::LiftOff), "区間の速さ 36 < 50");
        assert_eq!(o.raw, [900.0, 20.0]);
        let o = release_outcome(&est([900.0, 20.0], ReleaseRule::Ok, Some([60.0, 0.0])), any, &m);
        assert_eq!((o.velocity, o.rule), ([900.0, 20.0], ReleaseRule::Ok), "区間でも動いていた");
        let o = release_outcome(&est([900.0, 20.0], ReleaseRule::Ok, None), any, &m);
        assert_eq!(o.rule, ReleaseRule::Ok, "決められなければ R2 は当てない");
        for rule in [ReleaseRule::Stopped, ReleaseRule::ShortSpan] {
            let o = release_outcome(&est([0.0, 0.0], rule, Some([500.0, 0.0])), any, &m);
            assert_eq!((o.velocity, o.rule), ([0.0, 0.0], rule));
        }
        // 横だけのスワイプ: 縦に 300 px/s 揺れていても、横に止まっていれば持ち上げの揺れ
        let o = release_outcome(&est([900.0, 20.0], ReleaseRule::Ok, Some([10.0, 300.0])), GestureDragAxis::Horizontal, &m);
        assert_eq!((o.velocity, o.rule), ([0.0, 0.0], ReleaseRule::LiftOff));
        assert_eq!(o.lift_off_probe, Some([10.0, 0.0]), "材料も軸へ射影する");
    }
}
