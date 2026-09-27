// ============================================================
//  gesture/pointer_track.rs — 指 1 本の追跡（押した位置・時刻・今の位置・速度。W2-2）
//
//  アリーナ（arena.rs）が指ごとに 1 つ持つ。認識器（recognizers/）はここから
//  「押した位置からどれだけ動いたか」「押してから何秒か」「今の速度」を読む。
//  位置はキャンバスの画素（画面の中央が原点・Y 下向き。pick_2d・Input.MousePositionCanvas と同じ）。
// ============================================================

use super::pointer_log::PointerKey;
use super::thresholds::VelocityParams;
use super::velocity::VelocityTracker;

/// 指 1 本の追跡の状態。
#[derive(Clone, Debug)]
pub struct PointerTrack {
    /// 指の鍵（OS の生 ID・予約値）。
    pub key: PointerKey,
    /// スクリプトへ見せる指の番号（0 起点。触れている指の間で空いている最小の番号）。
    pub id: u32,
    /// 押した位置（キャンバスの画素）。
    pub down_position: [f32; 2],
    /// 押した時刻（秒）。
    pub down_time: f64,
    /// 今の位置。
    pub position: [f32; 2],
    /// 今の位置の時刻（秒）。
    pub time: f64,
    /// 速度の推定（押した位置と移動の標本）。
    pub velocity: VelocityTracker,
}

impl PointerTrack {
    /// 押したところから追跡を始める（押した位置も速度の標本にする）。
    pub fn new(key: PointerKey, id: u32, position: [f32; 2], time: f64, params: &VelocityParams) -> Self {
        let mut velocity = VelocityTracker::new();
        velocity.add(time, position, params);
        Self { key, id, down_position: position, down_time: time, position, time, velocity }
    }

    /// 移動を反映する（速度の標本にも足す）。
    pub fn move_to(&mut self, position: [f32; 2], time: f64, params: &VelocityParams) {
        self.position = position;
        self.time = time;
        self.velocity.add(time, position, params);
    }

    /// 離れた・取り消された位置と時刻を反映する（速度の標本には足さない。Android・Flutter と同じく、
    /// 離れたイベントの位置は最後の移動と同じなので、足すと止まった標本として速度を下げてしまう）。
    pub fn finish_at(&mut self, position: [f32; 2], time: f64) {
        self.position = position;
        self.time = time;
    }

    /// 押した位置からの移動（今の位置 − 押した位置）。
    pub fn total_delta(&self) -> [f32; 2] {
        [self.position[0] - self.down_position[0], self.position[1] - self.down_position[1]]
    }

    /// 押した位置からの距離（画素）。
    pub fn distance_from_down(&self) -> f32 {
        let [dx, dy] = self.total_delta();
        (dx * dx + dy * dy).sqrt()
    }

    /// 押してからの時間（秒。`at` は時刻）。
    pub fn elapsed(&self, at: f64) -> f64 {
        (at - self.down_time).max(0.0)
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::input::gesture::thresholds::GestureMetrics;

    /// 移動の量・距離・経過時間。
    #[test]
    fn tracks_delta_distance_and_elapsed() {
        let p = GestureMetrics::default().velocity;
        let mut t = PointerTrack::new(7, 0, [10.0, 20.0], 1.0, &p);
        t.move_to([13.0, 24.0], 1.05, &p);
        assert_eq!(t.total_delta(), [3.0, 4.0]);
        assert_eq!(t.distance_from_down(), 5.0);
        assert!((t.elapsed(1.25) - 0.25).abs() < 1e-9);
        assert_eq!(t.velocity.len(), 2, "押した位置と移動");
        t.finish_at([13.0, 24.0], 1.06);
        assert_eq!(t.velocity.len(), 2, "離れた位置は標本にしない");
    }
}
