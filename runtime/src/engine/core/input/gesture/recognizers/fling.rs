// ============================================================
//  gesture/recognizers/fling.rs — フリックの規則（離した時点の速度。W2-2）
//
//  ドラッグに勝った指を離したとき、その時点の速度（velocity.rs が直近の標本から推定）を軸へ射影し、
//  速さ（長さ）がフリックの最小の速度（既定 50 dp/秒）以上ならフリックとする。速さが上限（既定 8000 dp/秒）を
//  超えていたら、向きを保ったまま上限の長さへ切り詰める（Android の VelocityTracker.computeCurrentVelocity の
//  maxVelocity・Flutter の kMaxFlingVelocity と同じ）。ドラッグの終わり（DragEnd）の速度にも同じ切り詰めを使う。
// ============================================================

use crate::engine::components::GestureDragAxis;

use super::super::thresholds::GestureMetrics;
use super::drag::project;

/// ドラッグの終わりの速度（軸へ射影し、上限で切り詰めたもの。画素/秒）【純関数】。
pub fn release_velocity(estimate: [f32; 2], axis: GestureDragAxis, metrics: &GestureMetrics) -> [f32; 2] {
    let v = project(estimate, axis);
    let speed = (v[0] * v[0] + v[1] * v[1]).sqrt();
    if speed.is_finite() && speed > metrics.max_fling_px && speed > 0.0 {
        let k = metrics.max_fling_px / speed;
        [v[0] * k, v[1] * k]
    } else if speed.is_finite() {
        v
    } else {
        [0.0, 0.0]
    }
}

/// 離したときの速度がフリックか（速さが最小の速度以上）【純関数】。
///
/// # 引数
/// * `velocity` - `release_velocity` の結果
pub fn is_fling(velocity: [f32; 2], metrics: &GestureMetrics) -> bool {
    let speed = (velocity[0] * velocity[0] + velocity[1] * velocity[1]).sqrt();
    speed >= metrics.min_fling_px && speed > 0.0
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
}
