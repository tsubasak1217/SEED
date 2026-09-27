// ============================================================
//  gesture/recognizers/tap.rs — タップの規則（押して・動かず・離す。W2-2）
//
//  - 押した位置からタップの許容移動（tap_slop。既定 18 dp）を超えて動いたら成り立たない（「動かず」）
//  - 指がノードの押下の領域（見た目 → 最小のヒット領域 → さらにドラッグの slop だけ広げた矩形）の外へ出たら
//    成り立たない（Android の View の「ボタンの外へ出たら押下をやめる」と同じ。アリーナが領域を問い合わせる）
//  - 離したときにまだ競っていれば勝ちを申し出る（アリーナの並びで最初のタップが勝つ＝子が親より先）
//  - タップの時間の上限（tap_max。既定 0 = 上限なし）を超えて押していたら、離しても成り立たない
// ============================================================

use super::super::pointer_track::PointerTrack;
use super::super::thresholds::GestureMetrics;

/// 動いたのでタップが成り立たなくなったか（押した位置からの距離が許容移動を超えた）【純関数】。
pub fn fails_on_move(track: &PointerTrack, metrics: &GestureMetrics) -> bool {
    track.distance_from_down() > metrics.tap_slop_px
}

/// 離した時刻でタップを認めるか（押していた時間が上限以内。上限 0 は無制限）【純関数】。
///
/// # 引数
/// * `up_time` - 離したイベントの時刻（秒）
pub fn accepts_on_up(track: &PointerTrack, up_time: f64, metrics: &GestureMetrics) -> bool {
    metrics.tap_max_secs <= 0.0 || track.elapsed(up_time) <= metrics.tap_max_secs
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::input::gesture::thresholds::GestureThresholds;

    /// 許容移動（dp の倍率を掛けた画素）の内側なら成り立ち、外なら成り立たない。
    #[test]
    fn slop_scales_with_dp() {
        for scale in [1.0f32, 2.0, 3.0] {
            let m = GestureThresholds::default().metrics(scale);
            let p = m.velocity;
            let mut t = PointerTrack::new(1, 0, [0.0, 0.0], 0.0, &p);
            t.move_to([18.0 * scale - 0.5, 0.0], 0.01, &p);
            assert!(!fails_on_move(&t, &m), "倍率 {scale}: 内側");
            t.move_to([18.0 * scale + 0.5, 0.0], 0.02, &p);
            assert!(fails_on_move(&t, &m), "倍率 {scale}: 外側");
        }
    }

    /// 時間の上限: 0 は無制限、正ならその秒数まで。
    #[test]
    fn tap_max_duration() {
        let unlimited = GestureThresholds::default().metrics(1.0);
        let t = PointerTrack::new(1, 0, [0.0, 0.0], 0.0, &unlimited.velocity);
        assert!(accepts_on_up(&t, 10.0, &unlimited));
        let limited = GestureThresholds { tap_max_ms: 300.0, ..GestureThresholds::default() }.metrics(1.0);
        assert!(accepts_on_up(&t, 0.3, &limited));
        assert!(!accepts_on_up(&t, 0.31, &limited));
    }
}
