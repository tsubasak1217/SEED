// ============================================================
//  gesture/pointer_log.rs — 時刻つきの指（ポインタ）のイベントの記録（W2-2）
//
//  【何のためか】
//  TouchState（touch/state.rs）はスクリプトが 1 フレームの間ずっと同じ値で読む「指の一覧」で、
//  フレームの間に届いた途中の移動と、それぞれのイベントの時刻を持たない。ジェスチャーの判定
//  （長押しの時間・速度の推定・タップと長押しの境目）はフレームの時刻ではなく**入力イベントの時刻**で
//  決めたいので、指の単位のイベントを届いた順に時刻つきで積んでおき、アリーナ（arena_set.rs）が
//  フレームの中でまとめて読む。
//
//  【何を積むか】（Input が積む。core/input/mod.rs）
//    - 実タッチの指・マウスの左ボタンから合成した指（PointerBridge が TouchState へ入れたのと同じ指の単位の
//      イベント。入力源の調停は bridge.rs の結果に従う）
//    - 外部（エディタ・MCP）から注入したマウスの左ボタンと座標（INJECTED_POINTER_KEY の 1 本の指として）
//    - 全部の取り消し（フォーカスを失った・アプリが背面へ回った）
//  位置は入力座標（描画ターゲットの画素・左上原点。Input::mouse_position と同じ）。
//
//  【時刻】`pointer_clock_secs` の秒（プロセスで 1 つの単調な時計の起点からの秒）。
//    - PC（マウスの合成の指）: イベントを受け取った瞬間（winit のイベントハンドラ）の時刻
//    - Android（実タッチ）: MainActivity の processMotionEvent が控えた MotionEvent の時刻（eventTime）と履歴の標本
//      （input/touch/os_timing/。winit 0.30.13 は WindowEvent::Touch に時刻も履歴も渡さないので、Java で控えて突き合わせる）。
//      控えが見つからないイベントは受け取った時刻に戻る。指ごとに時刻が逆行しないよう time_floor.rs で切り上げる
//    - 注入のシーケンス: 予定の時刻（シーケンスの t）から逆算した時刻（フレームの刻みに依らない）
//  時計の起点は App の初期化の最初（app/mod.rs の App::new）で決める（起点より前の時刻は 0 に潰れるので、控えの時刻より先に）。
//
//  【寿命】積んだイベントはアリーナが取り出す（take）。取り出されないまま（Edit・一時停止）フレームが
//  終わったら Input::end_frame が捨てる（clear）。上限を超えた移動は捨てる（押す・離す・取り消しは残す）。
// ============================================================

use std::sync::OnceLock;
use std::time::Instant;

/// 指（ポインタ）の鍵（OS の生 ID。マウスの合成の指・注入の指は予約値）。
pub type PointerKey = u64;

/// 外部から注入したマウスの左ボタンの指の鍵（OS の生 ID・マウスの合成の指 `u64::MAX` と重ならない予約値）。
pub const INJECTED_POINTER_KEY: PointerKey = u64::MAX - 1;

/// 1 フレームに積む記録の上限（これを超えた移動は捨てる。人の操作では届かない数）。
pub const MAX_LOG_ENTRIES: usize = 4096;

/// 指のイベントの段階。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PointerPhase {
    /// 触れた（押した）。
    Down,
    /// 動いた。
    Move,
    /// 離れた。
    Up,
    /// OS に取り消された（着信・システムのジェスチャーへの横取りなど）。
    Cancel,
}

/// 指のイベント 1 件。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PointerInputEvent {
    /// 指の鍵。
    pub pointer: PointerKey,
    /// 段階。
    pub phase: PointerPhase,
    /// 位置（入力座標: 描画ターゲットの画素・左上原点）。
    pub position: [f32; 2],
    /// 時刻（`pointer_clock_secs` の秒）。
    pub time: f64,
}

/// 記録の 1 件。
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum PointerLogEntry {
    /// 指のイベント。
    Pointer(PointerInputEvent),
    /// すべての指の取り消し（フォーカスを失った・アプリが背面へ回った）。
    CancelAll {
        /// 時刻（秒）。
        time: f64,
    },
}

impl PointerLogEntry {
    /// 記録の時刻（秒）。
    pub fn time(&self) -> f64 {
        match self {
            PointerLogEntry::Pointer(e) => e.time,
            PointerLogEntry::CancelAll { time } => *time,
        }
    }
}

/// 時刻つきの指のイベントの記録（Input が 1 つ持つ）。
#[derive(Debug, Default)]
pub struct PointerEventLog {
    /// 届いた順の記録。
    entries: Vec<PointerLogEntry>,
    /// 上限を超えて捨てた移動の数（診断用。clear で 0 に戻す）。
    dropped_moves: usize,
}

impl PointerEventLog {
    /// 空の記録を作る。
    pub fn new() -> Self {
        Self::default()
    }

    /// 指のイベントを 1 件積む（上限を超えた移動は捨てる）。
    pub fn record(&mut self, pointer: PointerKey, phase: PointerPhase, position: [f32; 2], time: f64) {
        if self.entries.len() >= MAX_LOG_ENTRIES && phase == PointerPhase::Move {
            self.dropped_moves += 1;
            return;
        }
        self.entries.push(PointerLogEntry::Pointer(PointerInputEvent { pointer, phase, position, time }));
    }

    /// すべての指の取り消しを積む。
    pub fn record_cancel_all(&mut self, time: f64) {
        self.entries.push(PointerLogEntry::CancelAll { time });
    }

    /// 積んだ記録を時刻の順に取り出す（届いた順を保つ安定な並べ替え。注入のシーケンスの逆算した時刻が
    /// 実入力より前になることがあるため）。
    pub fn take(&mut self) -> Vec<PointerLogEntry> {
        let mut out = std::mem::take(&mut self.entries);
        out.sort_by(|a, b| a.time().total_cmp(&b.time()));
        self.dropped_moves = 0;
        out
    }

    /// 取り出されなかった記録を捨てる（フレームの終わり。Input::end_frame）。
    pub fn clear(&mut self) {
        self.entries.clear();
        self.dropped_moves = 0;
    }

    /// 積んである記録の数。
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    /// 記録が空か。
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// 上限を超えて捨てた移動の数。
    pub fn dropped_moves(&self) -> usize {
        self.dropped_moves
    }
}

// ─── 時計 ─────────────────────────────────────────────────

/// 時計の起点（プロセスで最初に時刻を求めたとき）。
static CLOCK_EPOCH: OnceLock<Instant> = OnceLock::new();

/// 指のイベントの時計: 起点からの秒（単調・プロセスで 1 つ）。
///
/// # 引数
/// * `at` - 時刻（起点より前なら 0 秒）
pub fn pointer_clock_secs(at: Instant) -> f64 {
    let epoch = *CLOCK_EPOCH.get_or_init(|| at);
    at.saturating_duration_since(epoch).as_secs_f64()
}

/// 今の時刻（`pointer_clock_secs(Instant::now())`。最初の呼び出しが時計の起点を決める）。
pub fn pointer_clock_now() -> f64 {
    pointer_clock_secs(Instant::now())
}

/// 指のイベントの時計の秒を Instant へ戻す（診断ログで CLOCK_MONOTONIC の ns へ直すため）。
///
/// # 戻り値
/// 起点がまだ決まっていない・秒が負や非有限・Instant で表せないときは None。
pub fn pointer_clock_instant(secs: f64) -> Option<Instant> {
    let epoch = *CLOCK_EPOCH.get()?;
    let offset = std::time::Duration::try_from_secs_f64(secs).ok()?;
    epoch.checked_add(offset)
}

// ─── 注入の指 ─────────────────────────────────────────────

/// 注入したマウスの左ボタンと座標を 1 本の指のイベントへ直す追跡【状態を持つ純ロジック】。
///
/// 注入の状態（inject/state.rs）は押下と座標を別々に持つので、「左ボタンが押された瞬間の座標で触れる・
/// 押している間に座標が変われば動く・離したら離れる」をここで作る。
#[derive(Debug, Default, Clone, Copy)]
pub struct InjectedPointerTracker {
    /// 注入の左ボタンで指が触れているか。
    down: bool,
    /// 最後に知らせた位置。
    last_position: Option<[f32; 2]>,
}

impl InjectedPointerTracker {
    /// 注入の操作のあとの状態から、指のイベントを作る。
    ///
    /// # 引数
    /// * `left_held` - 注入で左ボタンが押されているか（操作を当てた後）
    /// * `position`  - 今の位置（注入の座標があればそれ、無ければ実カーソル。入力座標）
    ///
    /// # 戻り値
    /// 起きた段階とその位置（無ければ None）。離れたときの位置は押していた間の最後の位置
    /// （全部の解放で注入の座標が消えても、離れた位置が実カーソルへ飛ばない）。
    pub fn observe(&mut self, left_held: bool, position: [f32; 2]) -> Option<(PointerPhase, [f32; 2])> {
        let event = match (self.down, left_held) {
            (false, true) => Some((PointerPhase::Down, position)),
            (true, false) => Some((PointerPhase::Up, self.last_position.unwrap_or(position))),
            (true, true) if self.last_position != Some(position) => Some((PointerPhase::Move, position)),
            _ => None,
        };
        self.down = left_held;
        self.last_position = Some(position);
        event
    }

    /// 注入を全部解放したとき（RELEASE_ALL・Play の停止）: 触れていれば離れたことにする。
    pub fn release(&mut self) -> bool {
        let was_down = self.down;
        self.down = false;
        was_down
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 取り出すと時刻の順（同じ時刻は届いた順）で、記録は空になる。
    #[test]
    fn take_sorts_by_time_and_empties() {
        let mut log = PointerEventLog::new();
        log.record(1, PointerPhase::Down, [0.0, 0.0], 0.2);
        log.record(2, PointerPhase::Down, [0.0, 0.0], 0.1);
        log.record(1, PointerPhase::Move, [1.0, 0.0], 0.2);
        log.record_cancel_all(0.3);
        let out = log.take();
        assert!(log.is_empty());
        let keys: Vec<_> = out
            .iter()
            .map(|e| match e {
                PointerLogEntry::Pointer(p) => (p.pointer, p.phase),
                PointerLogEntry::CancelAll { .. } => (0, PointerPhase::Cancel),
            })
            .collect();
        assert_eq!(
            keys,
            vec![(2, PointerPhase::Down), (1, PointerPhase::Down), (1, PointerPhase::Move), (0, PointerPhase::Cancel)]
        );
    }

    /// 上限を超えた移動は捨て、押す・離すは残す。
    #[test]
    fn cap_drops_moves_only() {
        let mut log = PointerEventLog::new();
        for _ in 0..MAX_LOG_ENTRIES {
            log.record(1, PointerPhase::Move, [0.0, 0.0], 0.0);
        }
        log.record(1, PointerPhase::Move, [0.0, 0.0], 0.0);
        log.record(1, PointerPhase::Up, [0.0, 0.0], 0.0);
        assert_eq!(log.len(), MAX_LOG_ENTRIES + 1);
        assert_eq!(log.dropped_moves(), 1);
        log.clear();
        assert!(log.is_empty());
    }

    /// 注入の左ボタンと座標から、触れる → 動く → 離れるを作る（座標が変わらなければ動かない）。
    #[test]
    fn injected_tracker_makes_down_move_up() {
        let mut t = InjectedPointerTracker::default();
        assert_eq!(t.observe(false, [1.0, 1.0]), None, "押していない座標の変化は指ではない");
        assert_eq!(t.observe(true, [1.0, 1.0]), Some((PointerPhase::Down, [1.0, 1.0])));
        assert_eq!(t.observe(true, [1.0, 1.0]), None);
        assert_eq!(t.observe(true, [5.0, 1.0]), Some((PointerPhase::Move, [5.0, 1.0])));
        assert_eq!(t.observe(false, [0.0, 0.0]), Some((PointerPhase::Up, [5.0, 1.0])), "離れた位置は最後の位置");
        assert!(!t.release(), "離した後の解放は何も起こさない");
        t.observe(true, [0.0, 0.0]);
        assert!(t.release(), "触れていれば解放で離れる");
    }

    /// 時計は単調（後の時刻は大きい）。
    #[test]
    fn clock_is_monotonic() {
        let a = Instant::now();
        let b = a + std::time::Duration::from_millis(250);
        let (sa, sb) = (pointer_clock_secs(a), pointer_clock_secs(b));
        assert!(sb >= sa);
    }

    /// 秒 → Instant → 秒の往復（1 ns 以内）。負の秒は None。
    #[test]
    fn clock_instant_round_trip() {
        let now = pointer_clock_now();
        let at = pointer_clock_instant(now).expect("起点は決まっている");
        assert!((pointer_clock_secs(at) - now).abs() < 1e-9);
        assert_eq!(pointer_clock_instant(-1.0), None);
        assert_eq!(pointer_clock_instant(f64::NAN), None);
    }
}
