// ============================================================
//  touch/test_sequence.rs — 検証用の合成タッチ列（複数指を実機で確かめるためのフック）
//
//  【目的】
//  adb から複数指を注入できない端末（非 root の実機。`sendevent` は SELinux で拒否され、
//  `input` コマンドは 1 本指しか出せない）でも、複数指の追跡（state.rs）と
//  指0 → マウスの駆動（bridge.rs）を実機で確かめられるようにする。
//  合成したイベントは実タッチと同じ `Input::process_touch` を通る
//  （座標の写像・状態機械・相互変換をそのまま通すので、確認の対象が本番と同じになる）。
//
//  【有効化】
//  Android の糊（runtime/android/native/src/debug_hooks.rs）が、システムプロパティ
//  `debug.seed.touch_test=1` のときだけ起動時に `request()` を呼ぶ。
//  要求が無ければ `take_due` はアトミック変数を 1 回読んで空を返すだけで、本番経路には影響しない。
//
//  【列の中身】`TEST_STEPS`（開始からの時刻・指・種類・画面比の位置）。最初に `take_due` が
//  呼ばれた時刻（＝最初のフレーム）から `START_DELAY_MS` 待ってから 1 回だけ流す。
//  3 本の指が同時に触れ、指0（A）が静止したまま他の指が動き、A が先に離れる（マウスの左ボタンは
//  A に追従して離れ、残った B / C はマウスを動かさない）ことを 1 度の再生で確かめられる並びにしてある。
// ============================================================

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;
use std::time::{Duration, Instant};

use winit::event::TouchPhase as RawTouchPhase;

/// 合成した指の生 ID の基点（OS の生 ID と衝突しない大きな値。Android のポインタ ID は 0〜数十）。
const SYNTHETIC_RAW_ID_BASE: u64 = 0x5EED_0000;

/// 最初のフレームから再生開始までの待ち時間（起動直後の重いフレームを避ける）。
const START_DELAY_MS: u64 = 2_000;

/// 列の 1 手。
struct Step {
    /// 再生開始からの時刻（ミリ秒）。
    at_ms: u64,
    /// 指の番号（生 ID は `SYNTHETIC_RAW_ID_BASE + finger`）。
    finger: u64,
    /// イベントの種類。
    phase: RawTouchPhase,
    /// 位置（ウィンドウ幅に対する比 0〜1）。
    fx: f32,
    /// 位置（ウィンドウ高さに対する比 0〜1）。
    fy: f32,
}

/// 指 A（最初に触れる＝指0）。
const FINGER_A: u64 = 0;
/// 指 B（2 本目。触れたまま動く）。
const FINGER_B: u64 = 1;
/// 指 C（3 本目）。
const FINGER_C: u64 = 2;

/// 再生する列（時刻順）。
const TEST_STEPS: &[Step] = &[
    Step { at_ms: 0,    finger: FINGER_A, phase: RawTouchPhase::Started, fx: 0.30, fy: 0.40 },
    Step { at_ms: 250,  finger: FINGER_B, phase: RawTouchPhase::Started, fx: 0.70, fy: 0.40 },
    Step { at_ms: 400,  finger: FINGER_B, phase: RawTouchPhase::Moved,   fx: 0.70, fy: 0.45 },
    Step { at_ms: 550,  finger: FINGER_B, phase: RawTouchPhase::Moved,   fx: 0.70, fy: 0.50 },
    Step { at_ms: 600,  finger: FINGER_C, phase: RawTouchPhase::Started, fx: 0.50, fy: 0.70 },
    Step { at_ms: 700,  finger: FINGER_B, phase: RawTouchPhase::Moved,   fx: 0.70, fy: 0.55 },
    Step { at_ms: 850,  finger: FINGER_B, phase: RawTouchPhase::Moved,   fx: 0.70, fy: 0.60 },
    Step { at_ms: 900,  finger: FINGER_C, phase: RawTouchPhase::Moved,   fx: 0.55, fy: 0.70 },
    Step { at_ms: 1100, finger: FINGER_A, phase: RawTouchPhase::Ended,   fx: 0.30, fy: 0.40 },
    Step { at_ms: 1300, finger: FINGER_C, phase: RawTouchPhase::Ended,   fx: 0.55, fy: 0.70 },
    Step { at_ms: 1500, finger: FINGER_B, phase: RawTouchPhase::Ended,   fx: 0.70, fy: 0.60 },
];

/// 合成した 1 件（ウィンドウのクライアント座標・物理ピクセル。`Input::process_touch` へそのまま渡す）。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SyntheticTouch {
    /// 生 ID。
    pub raw_id: u64,
    /// イベントの種類。
    pub phase: RawTouchPhase,
    /// x（ウィンドウのクライアント座標）。
    pub x: f32,
    /// y（ウィンドウのクライアント座標）。
    pub y: f32,
}

/// 再生が要求されたか（糊が起動時に立てる。以後は下ろさない）。
static REQUESTED: AtomicBool = AtomicBool::new(false);

/// 再生器（最初の `take_due` で作る）。
static PLAYER: Mutex<Option<Player>> = Mutex::new(None);

/// 再生を要求する（Android の糊がシステムプロパティを見て起動時に 1 回呼ぶ）。
pub fn request() {
    REQUESTED.store(true, Ordering::Release);
}

/// 再生が要求されているか（呼び出し側が、要求の無い通常時に余計な準備をしないための早期判定）。
#[inline]
pub fn is_requested() -> bool {
    REQUESTED.load(Ordering::Acquire)
}

/// 時刻 `now` までに予定された合成イベントを取り出す（毎フレームの先頭で呼ぶ）。
///
/// 要求されていなければ何もせず空を返す（アトミック変数を 1 回読むだけ）。
///
/// # 引数
/// - `now`    … 現在時刻
/// - `window` … ウィンドウのクライアント領域の大きさ（物理ピクセル。画面比の位置をここで座標にする）
pub fn take_due(now: Instant, window: (u32, u32)) -> Vec<SyntheticTouch> {
    if !is_requested() {
        return Vec::new();
    }
    // 検証専用なので、毒された Mutex もそのまま使う（前回の panic で再生が止まるだけで害は無い）。
    let mut guard = PLAYER.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    guard
        .get_or_insert_with(|| Player::new(now))
        .take_due(now, window)
}

/// 列の再生位置。
struct Player {
    /// 再生開始時刻（最初のフレーム + 待ち時間）。
    start: Instant,
    /// 次に出す手の位置。
    next: usize,
}

impl Player {
    /// 最初のフレームの時刻から作る。
    fn new(first_frame: Instant) -> Self {
        Self {
            start: first_frame + Duration::from_millis(START_DELAY_MS),
            next: 0,
        }
    }

    /// `now` までに予定された手を順に取り出す。
    fn take_due(&mut self, now: Instant, window: (u32, u32)) -> Vec<SyntheticTouch> {
        let mut due = Vec::new();
        // 開始前（now < start）は経過時間が 0 に丸められ、時刻 0 の手が早出しされるので先に弾く。
        if now < self.start {
            return due;
        }
        let elapsed_ms = now.duration_since(self.start).as_millis() as u64;
        while let Some(step) = TEST_STEPS.get(self.next) {
            if step.at_ms > elapsed_ms {
                break;
            }
            due.push(SyntheticTouch {
                raw_id: SYNTHETIC_RAW_ID_BASE + step.finger,
                phase: step.phase,
                x: step.fx * window.0 as f32,
                y: step.fy * window.1 as f32,
            });
            self.next += 1;
        }
        due
    }
}

// ============================================================
//  テスト（再生の時刻・座標変換。グローバル状態は使わず Player を直接試す）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 開始前は何も出さず、開始後は時刻までの手を 1 度ずつ順に出す。座標は画面比 × 大きさ。
    #[test]
    fn player_emits_steps_in_time_order_once() {
        let t0 = Instant::now();
        let mut p = Player::new(t0);
        let window = (1000, 2000);
        assert!(p.take_due(t0, window).is_empty(), "待ち時間の間は出さない");

        let start = t0 + Duration::from_millis(START_DELAY_MS);
        let first = p.take_due(start, window);
        assert_eq!(first.len(), 1);
        assert_eq!(first[0].raw_id, SYNTHETIC_RAW_ID_BASE + FINGER_A);
        assert_eq!(first[0].phase, RawTouchPhase::Started);
        assert_eq!((first[0].x, first[0].y), (300.0, 800.0));

        // 同じ時刻にもう一度呼んでも重複して出ない
        assert!(p.take_due(start, window).is_empty());

        // 最後まで進めると残り全部が出て、以後は空
        let end = start + Duration::from_millis(TEST_STEPS.last().unwrap().at_ms);
        let rest = p.take_due(end, window);
        assert_eq!(rest.len(), TEST_STEPS.len() - 1);
        assert!(p.take_due(end + Duration::from_secs(10), window).is_empty());
    }

    /// 列の整合: 時刻順・各指は Started で始まり Ended で終わる・同時に 3 本触れている瞬間がある。
    #[test]
    fn steps_are_well_formed() {
        assert!(TEST_STEPS.windows(2).all(|w| w[0].at_ms <= w[1].at_ms), "時刻順");
        let mut down = std::collections::HashSet::new();
        let mut max_down = 0;
        for s in TEST_STEPS {
            match s.phase {
                RawTouchPhase::Started => assert!(down.insert(s.finger), "二重の Started"),
                RawTouchPhase::Moved => assert!(down.contains(&s.finger), "触れていない指の Moved"),
                RawTouchPhase::Ended | RawTouchPhase::Cancelled => {
                    assert!(down.remove(&s.finger), "触れていない指の Ended")
                }
            }
            max_down = max_down.max(down.len());
        }
        assert!(down.is_empty(), "全指が離れて終わる");
        assert_eq!(max_down, 3, "3 本同時に触れている瞬間がある");
    }
}
