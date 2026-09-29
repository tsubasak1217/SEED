// ============================================================
//  touch/os_timing/stamp.rs — OS のタッチの時刻の控え 1 件の型（Android の MotionEvent の 1 本の指）
//
//  Java（MainActivity の processMotionEvent → input/TouchTimeline）が MotionEvent 1 つごとに、winit が WindowEvent::Touch に
//  する指について控えを送る。1 件の控え = winit の Touch 1 件（同じ指・同じ段階・同じ位置）に対応し、MotionEvent の時刻
//  （eventTime）と、Moved なら履歴の標本（MotionEvent の historical。winit が捨てる途中の標本）を持つ。
//  段階の番号は Java の `TouchTimeline.PHASE_*` と native（runtime/android/native/src/touch_timeline.rs）と一致させる。
// ============================================================

use std::time::Instant;

use winit::event::TouchPhase;

/// 段階の番号: 触れた（ACTION_DOWN・ACTION_POINTER_DOWN。winit の Started）。
pub const OS_PHASE_STARTED: i32 = 0;
/// 段階の番号: 動いた（ACTION_MOVE。winit の Moved）。
pub const OS_PHASE_MOVED: i32 = 1;
/// 段階の番号: 離れた（ACTION_UP・ACTION_POINTER_UP。winit の Ended）。
pub const OS_PHASE_ENDED: i32 = 2;
/// 段階の番号: 取り消された（ACTION_CANCEL。winit の Cancelled）。
pub const OS_PHASE_CANCELLED: i32 = 3;

/// 段階の番号を winit の段階へ直す【純関数】（知らない番号は None）。
pub fn phase_from_code(code: i32) -> Option<TouchPhase> {
    match code {
        OS_PHASE_STARTED => Some(TouchPhase::Started),
        OS_PHASE_MOVED => Some(TouchPhase::Moved),
        OS_PHASE_ENDED => Some(TouchPhase::Ended),
        OS_PHASE_CANCELLED => Some(TouchPhase::Cancelled),
        _ => None,
    }
}

/// 控えの標本 1 つ（位置と時刻）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct OsTouchSample {
    /// 位置（winit の Touch と同じ値: ウィンドウのクライアント座標の物理ピクセル。MotionEvent の AXIS_X / AXIS_Y の float）。
    pub position: [f32; 2],
    /// 時刻（CLOCK_MONOTONIC の ns を Instant へ直したもの。clock.rs）。
    pub time: Instant,
    /// 時刻（CLOCK_MONOTONIC の ns。Java から届いた元の値。診断ログ用）。
    pub monotonic_ns: i64,
}

/// MotionEvent 1 つの、1 本の指の控え（winit の Touch 1 件に対応）。
#[derive(Clone, Debug, PartialEq)]
pub struct OsTouchStamp {
    /// 指の ID（MotionEvent の pointer id を winit と同じく u64 へ直したもの）。
    pub pointer_id: u64,
    /// 段階（winit の Touch と同じ）。
    pub phase: TouchPhase,
    /// 今の標本（winit の Touch の位置と、MotionEvent の eventTime）。
    pub current: OsTouchSample,
    /// 履歴の標本（古い順。Moved のときだけ。winit が捨てる途中の標本）。
    pub history: Vec<OsTouchSample>,
}

impl OsTouchStamp {
    /// winit の Touch と同じ指の同じイベントか（ID・段階・位置の f32 のビットがすべて一致）【純関数】。
    ///
    /// 位置は winit が MotionEvent の float を f64 にしたもの（App が f32 へ戻す。f32 → f64 → f32 は元のビットに戻る）と、
    /// Java が同じ MotionEvent から読んだ float（GameActivity と同じ getAxisValue）を比べるので、ビットで一致する。
    pub fn matches(&self, pointer_id: u64, phase: TouchPhase, x: f32, y: f32) -> bool {
        self.pointer_id == pointer_id
            && self.phase == phase
            && self.current.position[0].to_bits() == x.to_bits()
            && self.current.position[1].to_bits() == y.to_bits()
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 段階の番号の対応（Java・native と同じ）。知らない番号は None。
    #[test]
    fn phase_codes_map_to_winit() {
        assert_eq!(phase_from_code(0), Some(TouchPhase::Started));
        assert_eq!(phase_from_code(1), Some(TouchPhase::Moved));
        assert_eq!(phase_from_code(2), Some(TouchPhase::Ended));
        assert_eq!(phase_from_code(3), Some(TouchPhase::Cancelled));
        assert_eq!(phase_from_code(4), None);
        assert_eq!(phase_from_code(-1), None);
    }

    /// 一致は ID・段階・位置のビット（-0.0 と 0.0 は別）。
    #[test]
    fn matches_by_bits() {
        let sample = OsTouchSample { position: [10.5, 0.0], time: Instant::now(), monotonic_ns: 1 };
        let s = OsTouchStamp { pointer_id: 2, phase: TouchPhase::Moved, current: sample, history: Vec::new() };
        assert!(s.matches(2, TouchPhase::Moved, 10.5, 0.0));
        assert!(!s.matches(2, TouchPhase::Ended, 10.5, 0.0), "段階が違う");
        assert!(!s.matches(3, TouchPhase::Moved, 10.5, 0.0), "指が違う");
        assert!(!s.matches(2, TouchPhase::Moved, 10.500001, 0.0), "位置が違う");
        assert!(!s.matches(2, TouchPhase::Moved, 10.5, -0.0), "ビットで比べる");
    }
}
