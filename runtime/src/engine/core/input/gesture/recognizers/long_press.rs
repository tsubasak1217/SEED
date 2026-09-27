// ============================================================
//  gesture/recognizers/long_press.rs — 長押しの規則（一定時間・動いたら不成立。W2-2）
//
//  - 押した時刻 + 長押しの時間（long_press。既定 500ms）に、まだ競っていれば勝ちを申し出る
//    （判定はイベントの時刻で行う: その時刻より前に離したイベントがあればタップ、無ければ長押し。
//      フレームがどれだけ遅れても境目は変わらない）
//  - 押した位置からタップの許容移動（tap_slop）を超えて動いたら成り立たない（タップと同じ）
//  - ノードの押下の領域の外へ出たら成り立たない（タップと同じ。アリーナが問い合わせる）
//  - 時間の前に離したら成り立たない
// ============================================================

use super::super::pointer_track::PointerTrack;
use super::super::thresholds::GestureMetrics;

/// 長押しが成り立つ時刻（秒）【純関数】。
pub fn deadline(track: &PointerTrack, metrics: &GestureMetrics) -> f64 {
    track.down_time + metrics.long_press_secs
}

/// 動いたので長押しが成り立たなくなったか（タップと同じ許容移動）【純関数】。
pub fn fails_on_move(track: &PointerTrack, metrics: &GestureMetrics) -> bool {
    track.distance_from_down() > metrics.tap_slop_px
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 期限は押した時刻 + 500ms、動きの許容はタップと同じ。
    #[test]
    fn deadline_and_slop() {
        let m = GestureMetrics::default();
        let mut t = PointerTrack::new(1, 0, [0.0, 0.0], 2.0, &m.velocity);
        assert!((deadline(&t, &m) - 2.5).abs() < 1e-9);
        t.move_to([0.0, 17.0], 2.1, &m.velocity);
        assert!(!fails_on_move(&t, &m));
        t.move_to([0.0, 19.0], 2.2, &m.velocity);
        assert!(fails_on_move(&t, &m));
    }
}
