// ============================================================
//  touch/state.rs — 複数指のタッチ状態（フレーム単位の状態機械・純ロジック）
//
//  【役割】
//  OS（winit）から届く生のタッチイベント（Started / Moved / Ended / Cancelled）を、
//  スクリプトが 1 フレームの間ずっと同じ値で読める「指の一覧」へまとめる。
//  winit のウィンドウや MouseState には触れない（相互変換は bridge.rs の責務）。
//
//  【フレームの区切り】
//  イベントはフレームとフレームの間（イベントハンドラ）で届き、スクリプトはフレームの中で読む。
//  「今フレーム」＝前回の end_frame 以降に届いたイベントを反映した状態。
//  end_frame はフレーム末（スクリプトが読み終えた後）に 1 回呼ぶ。
//
//  【Unity と同じ約束】
//  - Began は触れ始めたフレームだけ。動かなかった指は Stationary、動いた指は Moved。
//  - Ended / Canceled の指はそのフレームの間は一覧に残り、次フレームで消える。
//  - Moved / Stationary は「前フレーム末からの位置の差」で決める
//    （Android は 1 本が動くと全指ぶんの Moved を送るため、イベントの有無では決めない）。
//
//  【1 フレームに 1 段階だけ見せる（取りこぼし防止）】
//  - 触れ始めたフレームのうちに離れた指（素早いタップ・adb の input tap）は、そのフレームは Began、
//    次フレームで Ended として見せる。Began と Ended を同じフレームに潰すと、どちらかを調べる
//    スクリプトがタップを見落とすため。
//  - このフレームで離れた指と同じ生 ID の触れ始めが同じフレームに来たら、次フレームへ回す
//    （一覧に同じ指が 2 度現れないようにする）。その指の後続イベントも順序を保って一緒に回す。
//
//  【指番号（finger_id）】
//  スクリプトへ見せる番号は OS の生 ID ではなく、一覧で空いている最小の番号（0 起点）を割り当てる。
//  プラットフォームごとに生 ID の範囲が違っても（Android は 0〜、Windows は大きな値）、
//  スクリプトからは常に小さな整数に見える。
//
//  【指0（主たる指）】
//  他に触れている指が無い状態で触れ始めた指を「指0」として覚える（`primary`）。
//  マウスを駆動するのはこの指だけ（bridge.rs）。指番号（finger_id）が 0 かどうかとは別の概念。
// ============================================================

use winit::event::TouchPhase as RawTouchPhase;

use crate::engine::structs::tensor::Vector2;

use super::phase::TouchPhase;

/// 同時に一覧へ載せる指の最大本数。
///
/// 一覧が埋まっている間に触れ始めた指は、離すまで無視する（後から空きができても途中参加させない）。
pub const MAX_TOUCHES: usize = 10;

// ─── 公開する値型 ──────────────────────────────────────────

/// スクリプト・診断へ見せる指 1 本のスナップショット（このフレームの値）。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TouchPoint {
    /// 指番号（0 起点。同じフレームに一覧へ載っている指の間で一意）。
    pub finger_id: u32,
    /// 位置（入力座標系。`Input::mouse_position` と同じ単位・原点）。
    pub position: Vector2<f32>,
    /// 前フレーム末からの移動量（触れ始めたフレームは「触れ始めた位置」からの移動量）。
    pub delta: Vector2<f32>,
    /// このフレームでの段階。
    pub phase: TouchPhase,
}

/// 指0（主たる指）の状態。bridge.rs が MouseState を駆動するときに使う。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PrimaryFinger {
    /// OS の生 ID（イベントの突き合わせ用）。
    pub raw_id: u64,
    /// 最新の位置（入力座標系）。
    pub position: Vector2<f32>,
    /// このフレームの一覧で「触れている」段階（Began / Moved / Stationary）か。
    /// Ended / Canceled として見えているフレームは false。
    pub down: bool,
}

// ─── 内部表現 ──────────────────────────────────────────────

/// 生のタッチイベント 1 件（次フレームへ回すときの保存形）。
#[derive(Debug, Clone, Copy)]
struct RawTouchEvent {
    /// OS が付けた指の ID。
    raw_id: u64,
    /// イベントの種類。
    kind: RawTouchPhase,
    /// 位置（入力座標系へ写像済み）。
    position: Vector2<f32>,
}

/// 指の段階の元になる内部状態（Moved / Stationary は位置の差から後で決める）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Stage {
    /// 今フレームで触れ始めた。
    Began,
    /// 前フレーム以前から触れている。
    Down,
    /// 今フレームで離れた。
    Ended,
    /// 今フレームで取り消された。
    Canceled,
}

/// 触れ始めたフレームのうちに離れた指の、次フレームで見せる終わり方。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum EndKind {
    /// 通常の離れ。
    Ended,
    /// OS による取り消し。
    Canceled,
}

impl EndKind {
    /// 終わり方を一覧上の段階へ直す。
    fn stage(self) -> Stage {
        match self {
            EndKind::Ended => Stage::Ended,
            EndKind::Canceled => Stage::Canceled,
        }
    }
}

/// 一覧に載っている指 1 本の記録。
#[derive(Debug, Clone, Copy)]
struct Finger {
    /// OS が付けた ID（イベントの突き合わせにだけ使う）。
    raw_id: u64,
    /// スクリプトへ見せる指番号。
    finger_id: u32,
    /// 最新の位置。
    position: Vector2<f32>,
    /// 前フレーム末の位置（今フレームで触れ始めた指は触れ始めた位置）。
    prev_position: Vector2<f32>,
    /// このフレームで見せる段階の元。
    stage: Stage,
    /// 触れ始めたフレームのうちに離れたときの終わり方（次の end_frame で段階へ反映する）。
    pending_end: Option<EndKind>,
}

impl Finger {
    /// 物理的に触れているか（離れが届いた指は、見せ方が Began のままでも false）。
    fn is_down(&self) -> bool {
        matches!(self.stage, Stage::Began | Stage::Down) && self.pending_end.is_none()
    }

    /// 一覧上で「触れている」段階として見えているか（Began / Moved / Stationary）。
    fn is_visibly_down(&self) -> bool {
        matches!(self.stage, Stage::Began | Stage::Down)
    }

    /// 前フレーム末からの移動量。
    fn delta(&self) -> Vector2<f32> {
        self.position - self.prev_position
    }

    /// このフレームで見せる段階。
    fn phase(&self) -> TouchPhase {
        match self.stage {
            Stage::Began => TouchPhase::Began,
            Stage::Down => {
                // 位置が 1 成分でも変わっていれば Moved（往復して元の位置へ戻った場合は Stationary）。
                if self.position != self.prev_position {
                    TouchPhase::Moved
                } else {
                    TouchPhase::Stationary
                }
            }
            Stage::Ended => TouchPhase::Ended,
            Stage::Canceled => TouchPhase::Canceled,
        }
    }

    /// 公開用のスナップショットを作る。
    fn snapshot(&self) -> TouchPoint {
        TouchPoint {
            finger_id: self.finger_id,
            position: self.position,
            delta: self.delta(),
            phase: self.phase(),
        }
    }
}

// ─── TouchState ────────────────────────────────────────────

/// 複数指のタッチ状態。
///
/// | 呼び出し元のタイミング              | 呼ぶメソッド       |
/// |-------------------------------------|--------------------|
/// | `WindowEvent::Touch`（写像済み座標）| `apply`            |
/// | フォーカス喪失など（安全弁）        | `cancel_matching`  |
/// | フレーム末                          | `end_frame`        |
pub struct TouchState {
    /// 一覧（触れ始めた順）。
    fingers: Vec<Finger>,
    /// 次フレームへ回したイベント（到着順）。
    deferred: Vec<RawTouchEvent>,
    /// 指0 の生 ID（他に触れている指が無い状態で触れ始めた指）。一覧から消えたら None。
    primary: Option<u64>,
}

impl Default for TouchState {
    fn default() -> Self {
        Self::new()
    }
}

impl TouchState {
    /// 指が 1 本も無い状態で作る。
    pub fn new() -> Self {
        Self {
            fingers: Vec::with_capacity(MAX_TOUCHES),
            deferred: Vec::new(),
            primary: None,
        }
    }

    // ─── イベント処理 ──────────────────────────────────────

    /// 生のタッチイベントを 1 件反映する。
    ///
    /// # 引数
    /// - `raw_id`   … OS が付けた指の ID（winit の `Touch::id`）
    /// - `kind`     … イベントの種類（winit の `TouchPhase`）
    /// - `position` … 位置（呼び出し側で入力座標系へ写像済みのもの）
    pub fn apply(&mut self, raw_id: u64, kind: RawTouchPhase, position: Vector2<f32>) {
        self.apply_event(RawTouchEvent { raw_id, kind, position });
    }

    /// イベント 1 件の本体（次フレームへ回したイベントの再適用もここを通る）。
    fn apply_event(&mut self, ev: RawTouchEvent) {
        // 同じ指の先行イベントを次フレームへ回している最中なら、順序を保つため後続もすべて回す。
        if self.deferred.iter().any(|d| d.raw_id == ev.raw_id) {
            self.deferred.push(ev);
            return;
        }
        match ev.kind {
            RawTouchPhase::Started => self.begin(ev),
            RawTouchPhase::Moved => self.move_to(ev.raw_id, ev.position),
            RawTouchPhase::Ended => self.finish(ev.raw_id, ev.position, EndKind::Ended),
            RawTouchPhase::Cancelled => self.finish(ev.raw_id, ev.position, EndKind::Canceled),
        }
    }

    /// 触れ始め。
    fn begin(&mut self, ev: RawTouchEvent) {
        if let Some(index) = self.index_of(ev.raw_id) {
            if self.fingers[index].is_down() {
                // 離れが届かないまま同じ ID の触れ始めが来た（イベントの取りこぼし）。
                // 新しい指にはせず、位置の更新として扱う。
                self.fingers[index].position = ev.position;
            } else {
                // このフレームで離れた指と同じ ID の再タッチ。
                // 一覧に同じ指が 2 度現れないよう、次フレームへ回す。
                self.deferred.push(ev);
            }
            return;
        }

        // 上限超過の指は離すまで無視する（後続の Moved / Ended も「知らない ID」として捨てられる）。
        if self.fingers.len() >= MAX_TOUCHES {
            return;
        }

        // 他に触れている指が無ければ、この指が指0 になる（先に数えてから追加する）。
        let alone = self.down_count() == 0;
        let finger_id = self.lowest_free_finger_id();
        self.fingers.push(Finger {
            raw_id: ev.raw_id,
            finger_id,
            position: ev.position,
            prev_position: ev.position,
            stage: Stage::Began,
            pending_end: None,
        });
        if alone {
            self.primary = Some(ev.raw_id);
        }
    }

    /// 移動。触れている指の位置だけを更新する（段階は end_frame とクエリ時に決まる）。
    fn move_to(&mut self, raw_id: u64, position: Vector2<f32>) {
        if let Some(finger) = self.finger_mut(raw_id) {
            if finger.is_down() {
                finger.position = position;
            }
        }
    }

    /// 離れ・取り消し。
    fn finish(&mut self, raw_id: u64, position: Vector2<f32>, end: EndKind) {
        let Some(finger) = self.finger_mut(raw_id) else { return };
        // 既に離れている指への重複した終わりは無視する。
        if !finger.is_down() {
            return;
        }
        finger.position = position;
        if finger.stage == Stage::Began {
            // 触れ始めたフレーム: 今フレームは Began のまま見せ、終わりは次フレームで見せる。
            finger.pending_end = Some(end);
        } else {
            finger.stage = end.stage();
        }
    }

    /// 条件に合う生 ID の「触れている指」をすべて取り消す（安全弁）。
    ///
    /// フォーカス喪失などで OS の離れ・取り消しが届かないまま指が残り続けるのを防ぐ。
    /// 触れ始めたフレームの指は、今フレームは Began のまま・次フレームで Canceled になる。
    /// 条件に合う指の、次フレームへ回していたイベントも捨てる（始まっても離れが届かない恐れがあるため）。
    pub fn cancel_matching(&mut self, matches: impl Fn(u64) -> bool) {
        self.deferred.retain(|ev| !matches(ev.raw_id));
        for finger in self.fingers.iter_mut().filter(|f| matches(f.raw_id)) {
            if !finger.is_down() {
                continue;
            }
            if finger.stage == Stage::Began {
                finger.pending_end = Some(EndKind::Canceled);
            } else {
                finger.stage = Stage::Canceled;
            }
        }
    }

    /// フレーム末に呼ぶ。終わった指を一覧から外し、残りを次フレームの段階へ進める。
    ///
    /// 手順:
    /// 1. 今フレームで Ended / Canceled として見せた指を外す
    /// 2. 残った指の「前フレーム末の位置」を今の位置にし、段階を進める
    ///    （触れ始めたフレームに離れていた指は、ここで Ended / Canceled になる）
    /// 3. 次フレームへ回しておいたイベントを到着順に適用し直す
    pub fn end_frame(&mut self) {
        // ① 終わった指を外す
        self.fingers
            .retain(|f| !matches!(f.stage, Stage::Ended | Stage::Canceled));
        // 指0 が一覧から消えたら忘れる
        if let Some(raw_id) = self.primary {
            if self.index_of(raw_id).is_none() {
                self.primary = None;
            }
        }

        // ② 段階を進める
        for finger in &mut self.fingers {
            finger.prev_position = finger.position;
            finger.stage = match finger.pending_end.take() {
                Some(end) => end.stage(),
                None => Stage::Down,
            };
        }

        // ③ 回しておいたイベントを再適用する（再び回されるものは新しい待ち行列へ入る）
        let queued = std::mem::take(&mut self.deferred);
        for ev in queued {
            self.apply_event(ev);
        }
    }

    // ─── クエリ ──────────────────────────────────────────────

    /// 一覧の本数（このフレームで Ended / Canceled の指も含む）。
    #[inline]
    pub fn count(&self) -> usize {
        self.fingers.len()
    }

    /// 一覧の `index` 番目（触れ始めた順）。範囲外は None。
    pub fn get(&self, index: usize) -> Option<TouchPoint> {
        self.fingers.get(index).map(Finger::snapshot)
    }

    /// 一覧を触れ始めた順に列挙する。
    pub fn iter(&self) -> impl Iterator<Item = TouchPoint> + '_ {
        self.fingers.iter().map(Finger::snapshot)
    }

    /// 物理的に触れている指の本数（離れが届いた指は、見せ方が Began のままでも数えない）。
    pub fn down_count(&self) -> usize {
        self.fingers.iter().filter(|f| f.is_down()).count()
    }

    /// 指0 の状態。指0 が一覧に無ければ None。
    pub fn primary(&self) -> Option<PrimaryFinger> {
        let raw_id = self.primary?;
        let finger = self.fingers.iter().find(|f| f.raw_id == raw_id)?;
        Some(PrimaryFinger {
            raw_id,
            position: finger.position,
            down: finger.is_visibly_down(),
        })
    }

    // ─── 内部ヘルパ ────────────────────────────────────────

    /// 生 ID から一覧の位置を引く。
    fn index_of(&self, raw_id: u64) -> Option<usize> {
        self.fingers.iter().position(|f| f.raw_id == raw_id)
    }

    /// 生 ID から一覧の指を可変で引く。
    fn finger_mut(&mut self, raw_id: u64) -> Option<&mut Finger> {
        self.fingers.iter_mut().find(|f| f.raw_id == raw_id)
    }

    /// 一覧で使われていない最小の指番号。
    ///
    /// 使用中の番号は高々 `fingers.len()` 個なので、0 から数えれば `len` 回以内に必ず空きが見つかる。
    fn lowest_free_finger_id(&self) -> u32 {
        (0u32..)
            .find(|id| self.fingers.iter().all(|f| f.finger_id != *id))
            .expect("使用中の指番号は有限個なので空きは必ず見つかる")
    }
}

// ============================================================
//  テスト（状態機械の遷移）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の座標。
    fn p(x: f32, y: f32) -> Vector2<f32> {
        Vector2::new(x, y)
    }

    /// index 番目の (finger_id, phase) を取り出す。
    fn at(state: &TouchState, index: usize) -> (u32, TouchPhase) {
        let t = state.get(index).expect("index が範囲内であること");
        (t.finger_id, t.phase)
    }

    /// 1 本の指: Began → Stationary → Moved → Ended → 一覧から消える。
    #[test]
    fn single_finger_lifecycle() {
        let mut s = TouchState::new();
        s.apply(7, RawTouchPhase::Started, p(10.0, 20.0));
        assert_eq!(s.count(), 1);
        let t = s.get(0).unwrap();
        assert_eq!((t.finger_id, t.phase), (0, TouchPhase::Began));
        assert_eq!((t.position.x, t.position.y), (10.0, 20.0));
        assert_eq!((t.delta.x, t.delta.y), (0.0, 0.0), "触れ始めのフレームは差分 0");

        // 次フレーム: 動いていないので Stationary
        s.end_frame();
        assert_eq!(at(&s, 0), (0, TouchPhase::Stationary));

        // 動いたフレームは Moved（差分は前フレーム末から）
        s.end_frame();
        s.apply(7, RawTouchPhase::Moved, p(13.0, 16.0));
        let t = s.get(0).unwrap();
        assert_eq!(t.phase, TouchPhase::Moved);
        assert_eq!((t.delta.x, t.delta.y), (3.0, -4.0));

        // 離れたフレームは Ended として一覧に残る
        s.end_frame();
        s.apply(7, RawTouchPhase::Ended, p(13.0, 16.0));
        assert_eq!(s.count(), 1);
        assert_eq!(at(&s, 0), (0, TouchPhase::Ended));

        // 次フレームで消える
        s.end_frame();
        assert_eq!(s.count(), 0);
        assert!(s.get(0).is_none());
    }

    /// Android は 1 本が動くと全指ぶんの Moved を送る。位置が同じなら Stationary のまま。
    #[test]
    fn moved_event_without_position_change_is_stationary() {
        let mut s = TouchState::new();
        s.apply(0, RawTouchPhase::Started, p(5.0, 5.0));
        s.end_frame();
        s.apply(0, RawTouchPhase::Moved, p(5.0, 5.0));
        assert_eq!(at(&s, 0), (0, TouchPhase::Stationary));
        // 動いて元の位置へ戻った場合も差分 0 なので Stationary（Unity と同じく差分で決める）
        s.apply(0, RawTouchPhase::Moved, p(9.0, 5.0));
        s.apply(0, RawTouchPhase::Moved, p(5.0, 5.0));
        assert_eq!(at(&s, 0), (0, TouchPhase::Stationary));
    }

    /// 触れ始めたフレームのうちに動いた分は、そのフレームの差分に入る（段階は Began のまま）。
    #[test]
    fn began_frame_delta_includes_movement_within_frame() {
        let mut s = TouchState::new();
        s.apply(0, RawTouchPhase::Started, p(10.0, 10.0));
        s.apply(0, RawTouchPhase::Moved, p(15.0, 12.0));
        let t = s.get(0).unwrap();
        assert_eq!(t.phase, TouchPhase::Began);
        assert_eq!((t.position.x, t.position.y), (15.0, 12.0), "位置は最新");
        assert_eq!((t.delta.x, t.delta.y), (5.0, 2.0), "差分の総和＝触れ始めからの移動量");
    }

    /// 同じフレームで触れて離れた（素早いタップ）: そのフレームは Began、次フレームで Ended。
    #[test]
    fn tap_within_one_frame_is_seen_as_began_then_ended() {
        let mut s = TouchState::new();
        s.apply(3, RawTouchPhase::Started, p(100.0, 200.0));
        s.apply(3, RawTouchPhase::Ended, p(101.0, 200.0));
        assert_eq!(s.count(), 1);
        assert_eq!(at(&s, 0), (0, TouchPhase::Began), "Began を潰さない");
        assert_eq!(s.down_count(), 0, "物理的にはもう離れている");

        s.end_frame();
        let t = s.get(0).unwrap();
        assert_eq!(t.phase, TouchPhase::Ended, "次フレームで Ended");
        assert_eq!((t.position.x, t.position.y), (101.0, 200.0));
        assert_eq!((t.delta.x, t.delta.y), (0.0, 0.0));

        s.end_frame();
        assert_eq!(s.count(), 0);
    }

    /// 取り消し（Cancelled）は Canceled として 1 フレームだけ残る。触れ始めたフレームなら次フレームで。
    #[test]
    fn cancelled_is_reported_as_canceled() {
        let mut s = TouchState::new();
        s.apply(1, RawTouchPhase::Started, p(0.0, 0.0));
        s.end_frame();
        s.apply(1, RawTouchPhase::Cancelled, p(0.0, 0.0));
        assert_eq!(at(&s, 0), (0, TouchPhase::Canceled));
        s.end_frame();
        assert_eq!(s.count(), 0);

        s.apply(2, RawTouchPhase::Started, p(0.0, 0.0));
        s.apply(2, RawTouchPhase::Cancelled, p(0.0, 0.0));
        assert_eq!(at(&s, 0), (0, TouchPhase::Began));
        s.end_frame();
        assert_eq!(at(&s, 0), (0, TouchPhase::Canceled));
    }

    /// 複数指はそれぞれ独立に追跡される（段階・差分・指番号・並び順）。
    #[test]
    fn multiple_fingers_are_tracked_independently() {
        let mut s = TouchState::new();
        s.apply(10, RawTouchPhase::Started, p(10.0, 10.0));
        s.apply(11, RawTouchPhase::Started, p(50.0, 50.0));
        assert_eq!(s.count(), 2);
        assert_eq!(at(&s, 0), (0, TouchPhase::Began));
        assert_eq!(at(&s, 1), (1, TouchPhase::Began));

        // 片方だけ動く（Android は全指ぶんの Moved を送る）
        s.end_frame();
        s.apply(10, RawTouchPhase::Moved, p(10.0, 10.0));
        s.apply(11, RawTouchPhase::Moved, p(58.0, 44.0));
        assert_eq!(at(&s, 0), (0, TouchPhase::Stationary));
        assert_eq!(at(&s, 1), (1, TouchPhase::Moved));
        let d = s.get(1).unwrap().delta;
        assert_eq!((d.x, d.y), (8.0, -6.0));

        // 1 本目だけ離れる: 2 本目は触れたまま
        s.end_frame();
        s.apply(10, RawTouchPhase::Ended, p(10.0, 10.0));
        assert_eq!(at(&s, 0), (0, TouchPhase::Ended));
        assert_eq!(at(&s, 1), (1, TouchPhase::Stationary));
        assert_eq!(s.down_count(), 1);

        // 次フレーム: 1 本目は消え、2 本目が先頭へ（指番号は 1 のまま）
        s.end_frame();
        assert_eq!(s.count(), 1);
        assert_eq!(at(&s, 0), (1, TouchPhase::Stationary));

        // 3 本目は空いている最小の番号（0）を使う
        s.apply(12, RawTouchPhase::Started, p(0.0, 0.0));
        assert_eq!(at(&s, 1), (0, TouchPhase::Began));
    }

    /// 同時に一覧へ載るのは MAX_TOUCHES 本まで。超えた指は離すまで無視される。
    #[test]
    fn touches_beyond_max_are_ignored() {
        let mut s = TouchState::new();
        for raw in 0..(MAX_TOUCHES as u64 + 2) {
            s.apply(raw, RawTouchPhase::Started, p(raw as f32, 0.0));
        }
        assert_eq!(s.count(), MAX_TOUCHES);
        let ids: Vec<u32> = s.iter().map(|t| t.finger_id).collect();
        assert_eq!(ids, (0..MAX_TOUCHES as u32).collect::<Vec<_>>(), "指番号は 0..MAX-1");

        // 無視された指の移動・離れは何も起こさない
        let ignored = MAX_TOUCHES as u64;
        s.end_frame();
        s.apply(ignored, RawTouchPhase::Moved, p(999.0, 999.0));
        s.apply(ignored, RawTouchPhase::Ended, p(999.0, 999.0));
        assert_eq!(s.count(), MAX_TOUCHES);
        assert!(s.iter().all(|t| t.phase == TouchPhase::Stationary));

        // 1 本離れて一覧から消えれば、新しく触れた指は追加できる
        s.apply(0, RawTouchPhase::Ended, p(0.0, 0.0));
        s.end_frame();
        assert_eq!(s.count(), MAX_TOUCHES - 1);
        s.apply(100, RawTouchPhase::Started, p(1.0, 1.0));
        assert_eq!(s.count(), MAX_TOUCHES);
        let last = s.get(MAX_TOUCHES - 1).unwrap();
        assert_eq!((last.finger_id, last.phase), (0, TouchPhase::Began), "空いた番号 0 を再利用");
    }

    /// 指0 = 他に触れている指が無い状態で触れ始めた指。2 本目以降は指0 にならない。
    #[test]
    fn primary_is_the_finger_that_began_alone() {
        let mut s = TouchState::new();
        assert!(s.primary().is_none());

        s.apply(20, RawTouchPhase::Started, p(1.0, 1.0));
        s.apply(21, RawTouchPhase::Started, p(2.0, 2.0));
        let pr = s.primary().unwrap();
        assert_eq!((pr.raw_id, pr.down), (20, true));

        // 指0 が離れたフレームは down=false で残り、一覧から消えたら None
        s.end_frame();
        s.apply(20, RawTouchPhase::Ended, p(1.0, 1.0));
        let pr = s.primary().unwrap();
        assert_eq!((pr.raw_id, pr.down), (20, false));
        s.end_frame();
        assert!(s.primary().is_none(), "2 本目は指0 を引き継がない");

        // 2 本目がまだ触れている間に触れた指も指0 にならない
        s.apply(22, RawTouchPhase::Started, p(3.0, 3.0));
        assert!(s.primary().is_none());

        // 全部離れた後に触れた指は指0 になる
        s.end_frame();
        s.apply(21, RawTouchPhase::Ended, p(2.0, 2.0));
        s.apply(22, RawTouchPhase::Ended, p(3.0, 3.0));
        s.end_frame();
        s.end_frame();
        assert_eq!(s.count(), 0);
        s.apply(23, RawTouchPhase::Started, p(4.0, 4.0));
        assert_eq!(s.primary().map(|p| p.raw_id), Some(23));
    }

    /// 指0 が同じフレームで触れて離れた後、同じフレームで別の指が触れたら、その指が新しい指0。
    #[test]
    fn primary_moves_to_a_new_finger_when_nothing_is_down() {
        let mut s = TouchState::new();
        s.apply(1, RawTouchPhase::Started, p(1.0, 1.0));
        s.apply(1, RawTouchPhase::Ended, p(1.0, 1.0));
        s.apply(2, RawTouchPhase::Started, p(9.0, 9.0));
        assert_eq!(s.primary().map(|p| p.raw_id), Some(2));
        // 一覧には両方載る（1 本目は今フレーム Began・次フレーム Ended）
        assert_eq!(at(&s, 0), (0, TouchPhase::Began));
        assert_eq!(at(&s, 1), (1, TouchPhase::Began));
        s.end_frame();
        assert_eq!(at(&s, 0), (0, TouchPhase::Ended));
        assert_eq!(at(&s, 1), (1, TouchPhase::Stationary));
    }

    /// このフレームで離れた指と同じ生 ID の再タッチは次フレームへ回る（一覧に同じ指が 2 度出ない）。
    #[test]
    fn reuse_of_raw_id_in_the_same_frame_is_deferred() {
        let mut s = TouchState::new();
        s.apply(0, RawTouchPhase::Started, p(1.0, 1.0));
        s.end_frame();
        s.apply(0, RawTouchPhase::Ended, p(1.0, 1.0));
        // 同じフレームに同じ ID で触れ直し、少し動いた
        s.apply(0, RawTouchPhase::Started, p(50.0, 50.0));
        s.apply(0, RawTouchPhase::Moved, p(52.0, 50.0));
        assert_eq!(s.count(), 1, "今フレームは離れた指だけ");
        assert_eq!(at(&s, 0), (0, TouchPhase::Ended));

        // 次フレーム: 古い指が消え、回しておいた触れ始めが Began として現れる
        s.end_frame();
        assert_eq!(s.count(), 1);
        let t = s.get(0).unwrap();
        assert_eq!((t.finger_id, t.phase), (0, TouchPhase::Began));
        assert_eq!((t.position.x, t.position.y), (52.0, 50.0), "回したイベントは順序どおり適用");
        assert_eq!(s.primary().map(|p| p.raw_id), Some(0));
    }

    /// 実機（Pixel 6a）で観測した並び: 起動中（最初のフレームより前）に、戻るジェスチャのなぞり
    /// （Started → Moved×n → Ended）と、システムに横取りされた 2 回のタッチ（Started → Cancelled）が
    /// **すべて同じ ID 0 で 1 フレームのうちに**届いた。panic せず、各タッチが順に
    /// 「Began → Ended / Canceled」として 1 フレームずつ見えること。
    #[test]
    fn burst_of_reused_ids_within_one_frame_is_played_back_in_order() {
        let mut s = TouchState::new();
        s.apply(0, RawTouchPhase::Started, p(2377.0, 563.0));
        for i in 0..19 {
            s.apply(0, RawTouchPhase::Moved, p(2377.0 - i as f32 * 27.0, 563.0));
        }
        s.apply(0, RawTouchPhase::Ended, p(1866.0, 588.0));
        s.apply(0, RawTouchPhase::Started, p(2395.0, 444.0));
        s.apply(0, RawTouchPhase::Moved, p(2379.0, 448.0));
        s.apply(0, RawTouchPhase::Cancelled, p(2379.0, 448.0));
        s.apply(0, RawTouchPhase::Started, p(1345.0, 1056.0));
        for _ in 0..3 {
            s.apply(0, RawTouchPhase::Moved, p(1331.0, 1027.0));
        }
        s.apply(0, RawTouchPhase::Cancelled, p(1331.0, 1027.0));

        // フレームごとに (段階, 位置) を集める。同時に載るのは常に 1 本。
        let mut seen = Vec::new();
        for _ in 0..8 {
            assert!(s.count() <= 1, "同じ ID の指が同時に 2 本載らない");
            if let Some(t) = s.get(0) {
                seen.push((t.phase, t.position.x, t.position.y));
            }
            s.end_frame();
        }
        assert_eq!(
            seen,
            vec![
                (TouchPhase::Began, 1866.0, 588.0),
                (TouchPhase::Ended, 1866.0, 588.0),
                (TouchPhase::Began, 2379.0, 448.0),
                (TouchPhase::Canceled, 2379.0, 448.0),
                (TouchPhase::Began, 1331.0, 1027.0),
                (TouchPhase::Canceled, 1331.0, 1027.0),
            ]
        );
        assert_eq!(s.count(), 0);
        assert!(s.primary().is_none());
    }

    /// 取り消しの安全弁: 条件に合う触れている指だけが Canceled になる。
    #[test]
    fn cancel_matching_cancels_only_selected_fingers() {
        let mut s = TouchState::new();
        s.apply(1, RawTouchPhase::Started, p(0.0, 0.0));
        s.apply(2, RawTouchPhase::Started, p(0.0, 0.0));
        s.end_frame();
        s.apply(3, RawTouchPhase::Started, p(0.0, 0.0));
        s.cancel_matching(|raw| raw != 2);
        assert_eq!(at(&s, 0), (0, TouchPhase::Canceled));
        assert_eq!(at(&s, 1), (1, TouchPhase::Stationary), "条件外の指はそのまま");
        assert_eq!(at(&s, 2), (2, TouchPhase::Began), "触れ始めたフレームは Began のまま");
        s.end_frame();
        assert_eq!(s.count(), 2);
        assert_eq!(at(&s, 0), (1, TouchPhase::Stationary));
        assert_eq!(at(&s, 1), (2, TouchPhase::Canceled), "次フレームで Canceled");
        s.end_frame();
        assert_eq!(s.count(), 1);
    }

    /// 知らない ID の移動・離れ、離れた後の重複した離れは無視される。
    #[test]
    fn stray_events_are_ignored() {
        let mut s = TouchState::new();
        s.apply(5, RawTouchPhase::Moved, p(1.0, 1.0));
        s.apply(5, RawTouchPhase::Ended, p(1.0, 1.0));
        assert_eq!(s.count(), 0);

        s.apply(6, RawTouchPhase::Started, p(1.0, 1.0));
        s.end_frame();
        s.apply(6, RawTouchPhase::Ended, p(2.0, 2.0));
        s.apply(6, RawTouchPhase::Ended, p(3.0, 3.0));
        s.apply(6, RawTouchPhase::Moved, p(4.0, 4.0));
        let t = s.get(0).unwrap();
        assert_eq!(t.phase, TouchPhase::Ended);
        assert_eq!((t.position.x, t.position.y), (2.0, 2.0), "離れた後のイベントで位置は変わらない");
    }

    /// 離れが届かないまま同じ ID の触れ始めが来たら、新しい指にせず位置だけ更新する。
    #[test]
    fn duplicate_started_for_a_down_finger_updates_position() {
        let mut s = TouchState::new();
        s.apply(4, RawTouchPhase::Started, p(1.0, 1.0));
        s.end_frame();
        s.apply(4, RawTouchPhase::Started, p(6.0, 1.0));
        assert_eq!(s.count(), 1);
        assert_eq!(at(&s, 0), (0, TouchPhase::Moved));
    }

    /// クエリは状態を変えない（1 フレームに何度読んでも同じ値）。
    #[test]
    fn queries_are_stable_within_a_frame() {
        let mut s = TouchState::new();
        s.apply(0, RawTouchPhase::Started, p(1.0, 2.0));
        let first: Vec<TouchPoint> = s.iter().collect();
        let second: Vec<TouchPoint> = s.iter().collect();
        assert_eq!(first, second);
        assert_eq!(s.get(0), s.get(0));
        assert_eq!(s.count(), s.count());
    }
}
