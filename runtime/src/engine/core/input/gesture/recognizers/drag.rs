// ============================================================
//  gesture/recognizers/drag.rs — ドラッグの規則（全方向・横だけ・縦だけ。W2-2）
//
//  - 押した位置からの移動が、ドラッグの許容移動（touch_slop。既定 8 dp＝dp の倍率で画素へ）を
//    超えたら勝ちを申し出る。全方向は距離（ユークリッド）、横だけは |dx|、縦だけは |dy| で測る
//    （Flutter の HorizontalDrag / VerticalDrag / Pan と同じ考え方。ただし全方向の slop も 8 dp。Flutter の Pan は 2 倍）
//  - 横だけのドラッグは縦の動きでは勝ちを申し出ない → 親の縦スクロールが先に勝つ（最初の動きの向きで持ち主が決まる。
//    斜めちょうどなら同じ移動で両方が申し出るので、アリーナの並びで先の子が勝つ）
//  - 勝った後の移動量・速度は軸へ射影して知らせる（横だけなら y は 0）。位置は射影しない（指の本当の位置）
// ============================================================

use crate::engine::components::GestureDragAxis;

use super::super::pointer_track::PointerTrack;
use super::super::thresholds::GestureMetrics;

/// 押した位置からの移動が、軸に沿ってドラッグの許容移動を超えたか（勝ちを申し出るか）【純関数】。
pub fn claims(track: &PointerTrack, axis: GestureDragAxis, metrics: &GestureMetrics) -> bool {
    let [dx, dy] = track.total_delta();
    let moved = match axis {
        GestureDragAxis::Any => (dx * dx + dy * dy).sqrt(),
        GestureDragAxis::Horizontal => dx.abs(),
        GestureDragAxis::Vertical => dy.abs(),
    };
    moved > metrics.touch_slop_px
}

/// ベクトル（移動量・速度）を軸へ射影する【純関数】（全方向はそのまま）。
pub fn project(v: [f32; 2], axis: GestureDragAxis) -> [f32; 2] {
    match axis {
        GestureDragAxis::Any => v,
        GestureDragAxis::Horizontal => [v[0], 0.0],
        GestureDragAxis::Vertical => [0.0, v[1]],
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::input::gesture::thresholds::GestureThresholds;

    /// 軸ごとの測り方と、dp の倍率 1・2・3 での slop の内外。
    #[test]
    fn claims_by_axis_and_dp_scale() {
        for scale in [1.0f32, 2.0, 3.0] {
            let m = GestureThresholds::default().metrics(scale);
            let slop = 8.0 * scale;
            let mut t = PointerTrack::new(1, 0, [0.0, 0.0], 0.0, &m.velocity);
            // 横へ slop ちょうど（超えていない）→ どれも申し出ない
            t.move_to([slop, 0.0], 0.01, &m.velocity);
            assert!(!claims(&t, GestureDragAxis::Any, &m), "倍率 {scale}");
            assert!(!claims(&t, GestureDragAxis::Horizontal, &m));
            // 横へ slop を少し超えた → 全方向と横だけが申し出る
            t.move_to([slop + 0.5, 0.0], 0.02, &m.velocity);
            assert!(claims(&t, GestureDragAxis::Any, &m));
            assert!(claims(&t, GestureDragAxis::Horizontal, &m));
            assert!(!claims(&t, GestureDragAxis::Vertical, &m), "縦だけは横の動きで申し出ない");
            // 斜め（各軸は slop 未満・距離は slop 超え）→ 全方向だけ
            t.move_to([slop * 0.8, slop * 0.8], 0.03, &m.velocity);
            assert!(claims(&t, GestureDragAxis::Any, &m));
            assert!(!claims(&t, GestureDragAxis::Horizontal, &m));
            assert!(!claims(&t, GestureDragAxis::Vertical, &m));
        }
    }

    /// 射影。
    #[test]
    fn projects_onto_axis() {
        assert_eq!(project([3.0, 4.0], GestureDragAxis::Any), [3.0, 4.0]);
        assert_eq!(project([3.0, 4.0], GestureDragAxis::Horizontal), [3.0, 0.0]);
        assert_eq!(project([3.0, 4.0], GestureDragAxis::Vertical), [0.0, 4.0]);
    }
}
