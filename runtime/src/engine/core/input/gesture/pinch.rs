// ============================================================
//  gesture/pinch.rs — 2 本指のピンチ（拡大縮小。W2-8）
//
//  【何をするか】（規則の正典は docs/input_gestures.md §2.6）
//  CanvasGestureComponent の `pinch` を付けたノード（グラフの拡大縮小など）へ、2 本の指の間の距離の変化を
//  PinchStart / PinchUpdate / PinchEnd として届ける。指ごとのアリーナ（arena.rs）とは別に、すべての指を見て決める
//  （2 本目の指は「1 ノードのドラッグは 1 本まで」の規則でアリーナに入らないことがあるので、アリーナの中では数えられない）。
//
//  【規則】
//    1. 指が触れたら、当たり判定の経路（葉 → 根）でいちばん葉に近い「ピンチを受けるノード」と、それより根の側の
//       ノード（外側）を覚える
//    2. 同じノードに 2 本目の指が触れたら（1 本目がまだ触れていて、そのノードのピンチの組がまだ無ければ）、
//       その 2 本で候補の組を作る（3 本目以降は組にしない）
//    3. 候補の 2 本の指の間の距離が、組ができたときから touch slop（8 dp）を超えて変わったら「始められる」。
//       arena_set.rs が、どちらの指も外側のノードのドラッグに取られていないことを確かめてから始める
//       （縦の一覧をスクロールしている指ではピンチにしない）。始めたら 2 本の指のアリーナは取り消され
//       （PressCancel・取り消しの DragEnd）、以後この 2 本の指の動きはピンチだけが受ける
//    4. 倍率 = 今の指の間の距離 ÷ 始めたときの距離（全体・横・縦。始めたときの幅が 1 画素以下の向きは 1）。
//       位置 = 2 本の指の中点（フォーカス）、移動量 = 前のピンチのイベントからの中点の移動、押してからの時間 = 始めてから
//    5. どちらかの指を離す・取り消されると PinchEnd（取り消しなら canceled）。残った指は離すまで何もしない
//       （そのノードに次の指が触れれば、残った指と新しい組を作れる）
//  ピンチのイベントの指の番号（pointer_id）は 0 に固定する（2 本の指の組で 1 つのイベント）。速度は 0。
//
//  状態はこの型の中だけにあり、World には触れない（純ロジック）。座標はキャンバスの画素、時刻は pointer_log の時計の秒。
// ============================================================

use crate::engine::components::CanvasGestureComponent;
use crate::engine::ecs::Entity;

use super::events::{GestureEmit, GestureEventKind, NO_SCALE};
use super::pointer_log::PointerKey;

/// 倍率の分母にする幅の下限（画素）。これ以下の向き（指が縦に並んだときの横の幅など）の倍率は 1 にする
/// （Flutter の ScaleGestureRecognizer が初めの幅 0 の向きの倍率を 1 にするのと同じ考え方）。
const MIN_BASE_SPAN_PX: f32 = 1.0;
/// ピンチのイベントの指の番号（2 本の指の組で 1 つのイベント）。
const PINCH_POINTER_ID: u32 = 0;
/// 倍率の成分の数（全体・横・縦）。
const SCALE_COMPONENTS: usize = 3;

/// ピンチを受けるノードに触れている指 1 本。
#[derive(Clone, Debug)]
struct PinchFinger {
    /// 指の鍵。
    key: PointerKey,
    /// 経路でいちばん葉に近いピンチを受けるノード。
    node: Entity,
    /// そのノードより根の側の経路のノード（外側。これのドラッグに取られている指ではピンチにしない）。
    outer: Vec<Entity>,
    /// 今の位置（キャンバスの画素）。
    position: [f32; 2],
}

/// ピンチの組（ノード 1 つと 2 本の指）。
#[derive(Clone, Debug)]
struct PinchSession {
    /// ピンチを受けるノード。
    node: Entity,
    /// 組の指（触れた順）。
    fingers: [PointerKey; 2],
    /// 組ができたときの指の間の距離（始めるかの比べる元）。
    candidate_span: f32,
    /// 始まったか。
    started: bool,
    /// 始めたときの幅（全体の距離・横の幅・縦の幅。倍率の分母）。
    base: [f32; SCALE_COMPONENTS],
    /// 始めたときの中点。
    start_focal: [f32; 2],
    /// 前のイベントの中点（移動量の起点）。
    last_focal: [f32; 2],
    /// 始めた時刻（秒）。
    start_time: f64,
}

/// 指が動いたときの、ピンチ側の判定の結果。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PinchMove {
    /// ピンチに関係しない（アリーナで処理する）。
    Ignored,
    /// 候補の組の距離が slop を超えて変わった（組の添字）。呼び出し側が始められるかを確かめて `start` を呼ぶ。
    /// 始めないならアリーナで処理を続ける。
    Ready(usize),
    /// 始まったピンチの指（PinchUpdate を出した。アリーナでは処理しない）。
    Captured,
}

/// すべての指のピンチの追跡（arena_set.rs が 1 つ持つ）。
#[derive(Clone, Debug, Default)]
pub struct PinchTracker {
    /// ピンチを受けるノードに触れている指（触れた順）。
    fingers: Vec<PinchFinger>,
    /// ピンチの組（候補と始まったもの）。
    sessions: Vec<PinchSession>,
}

/// 2 点の中点。
fn midpoint(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    [(a[0] + b[0]) * 0.5, (a[1] + b[1]) * 0.5]
}

/// 2 点の差（a − b）。
fn sub(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    [a[0] - b[0], a[1] - b[1]]
}

/// 2 点の距離・横の幅・縦の幅。
fn spans(a: [f32; 2], b: [f32; 2]) -> [f32; SCALE_COMPONENTS] {
    let d = sub(a, b);
    [(d[0] * d[0] + d[1] * d[1]).sqrt(), d[0].abs(), d[1].abs()]
}

/// 今の幅と始めたときの幅から倍率を作る【純関数】（始めたときの幅が下限以下の成分は 1）。
pub fn pinch_scale(current: [f32; SCALE_COMPONENTS], base: [f32; SCALE_COMPONENTS]) -> [f32; SCALE_COMPONENTS] {
    let mut out = NO_SCALE;
    for i in 0..SCALE_COMPONENTS {
        if base[i] > MIN_BASE_SPAN_PX {
            out[i] = current[i] / base[i];
        }
    }
    out
}

impl PinchTracker {
    /// 空の状態で作る。
    pub fn new() -> Self {
        Self::default()
    }

    /// すべて捨てる（イベントは出さない。Play の開始・終了・シーンの切り替え）。
    pub fn reset(&mut self) {
        self.fingers.clear();
        self.sessions.clear();
    }

    /// 追跡している指が無いか。
    pub fn is_idle(&self) -> bool {
        self.fingers.is_empty()
    }

    /// 始まったピンチが捕捉している指か（その指はアリーナで処理しない）。
    pub fn captures(&self, key: PointerKey) -> bool {
        self.sessions.iter().any(|s| s.started && s.fingers.contains(&key))
    }

    /// ノードが始まったピンチを受けているか（3 本目の指をそのノードと子孫に参加させないため）。
    pub fn is_pinching(&self, node: Entity) -> bool {
        self.sessions.iter().any(|s| s.started && s.node == node)
    }

    /// 始まったピンチが捕捉している指の数（「動いている」の申告）。
    pub fn captured_pointer_count(&self) -> usize {
        self.sessions.iter().filter(|s| s.started).map(|s| s.fingers.len()).sum()
    }

    /// 組の指（`PinchMove::Ready` の添字から）。
    pub fn session_fingers(&self, session: usize) -> Option<[PointerKey; 2]> {
        self.sessions.get(session).map(|s| s.fingers)
    }

    /// 指の外側のノード（その指がピンチを受けるノードに触れていなければ空）。
    pub fn outer_of(&self, key: PointerKey) -> &[Entity] {
        self.fingers.iter().find(|f| f.key == key).map_or(&[], |f| f.outer.as_slice())
    }

    /// 指がどれかの組に入っているか。
    fn in_session(&self, key: PointerKey) -> bool {
        self.sessions.iter().any(|s| s.fingers.contains(&key))
    }

    /// 指の今の位置。
    fn position_of(&self, key: PointerKey) -> Option<[f32; 2]> {
        self.fingers.iter().find(|f| f.key == key).map(|f| f.position)
    }

    /// 組の 2 本の指の今の位置。
    fn session_positions(&self, session: usize) -> Option<([f32; 2], [f32; 2])> {
        let s = self.sessions.get(session)?;
        Some((self.position_of(s.fingers[0])?, self.position_of(s.fingers[1])?))
    }

    /// 指が触れた（経路にピンチを受けるノードがあれば覚え、同じノードの 2 本目なら候補の組を作る）。
    ///
    /// # 引数
    /// * `key`      - 指の鍵
    /// * `path`     - 当たり判定の経路（葉 → 根）とノードの設定
    /// * `position` - 触れた位置（キャンバスの画素）
    pub fn on_down(&mut self, key: PointerKey, path: &[(Entity, CanvasGestureComponent)], position: [f32; 2]) {
        // 同じ鍵の古い記録（離れの取りこぼし）は捨てる（呼び出し側が先に取り消しのイベントを出している）
        self.fingers.retain(|f| f.key != key);
        let Some(k) = path.iter().position(|(_, s)| s.wants_pinch()) else { return };
        let node = path[k].0;
        let outer = path[k + 1..].iter().map(|(e, _)| *e).collect();
        // 同じノードにまだ組になっていない指があれば、その指と組む（そのノードの組が無いときだけ）
        let partner = if self.sessions.iter().any(|s| s.node == node) {
            None
        } else {
            self.fingers
                .iter()
                .find(|f| f.node == node && !self.in_session(f.key))
                .map(|f| (f.key, f.position))
        };
        self.fingers.push(PinchFinger { key, node, outer, position });
        if let Some((other, other_position)) = partner {
            let focal = midpoint(other_position, position);
            self.sessions.push(PinchSession {
                node,
                fingers: [other, key],
                candidate_span: spans(other_position, position)[0],
                started: false,
                base: [0.0; SCALE_COMPONENTS],
                start_focal: focal,
                last_focal: focal,
                start_time: 0.0,
            });
        }
    }

    /// 指が動いた。始まったピンチの指なら PinchUpdate を `out` へ足す。
    ///
    /// # 引数
    /// * `key`      - 指の鍵
    /// * `position` - 今の位置（キャンバスの画素）
    /// * `time`     - イベントの時刻（秒）
    /// * `slop_px`  - 始める距離の変化（画素。touch slop）
    /// * `out`      - 出すイベント
    pub fn on_move(
        &mut self,
        key: PointerKey,
        position: [f32; 2],
        time: f64,
        slop_px: f32,
        out: &mut Vec<GestureEmit>,
    ) -> PinchMove {
        let Some(finger) = self.fingers.iter_mut().find(|f| f.key == key) else { return PinchMove::Ignored };
        finger.position = position;
        let Some(si) = self.sessions.iter().position(|s| s.fingers.contains(&key)) else { return PinchMove::Ignored };
        let Some((a, b)) = self.session_positions(si) else { return PinchMove::Ignored };
        let session = &mut self.sessions[si];
        if session.started {
            let focal = midpoint(a, b);
            let delta = sub(focal, session.last_focal);
            session.last_focal = focal;
            let scale = pinch_scale(spans(a, b), session.base);
            out.push(Self::emit(session, GestureEventKind::PinchUpdate, focal, delta, scale, time, false));
            return PinchMove::Captured;
        }
        if (spans(a, b)[0] - session.candidate_span).abs() > slop_px {
            PinchMove::Ready(si)
        } else {
            PinchMove::Ignored
        }
    }

    /// 候補の組を始める（PinchStart を出す。倍率は 1）。呼び出し側は先に 2 本の指のアリーナを取り消しておく。
    pub fn start(&mut self, session: usize, time: f64, out: &mut Vec<GestureEmit>) {
        let Some((a, b)) = self.session_positions(session) else { return };
        let s = &mut self.sessions[session];
        if s.started {
            return;
        }
        let focal = midpoint(a, b);
        s.started = true;
        s.base = spans(a, b);
        s.start_focal = focal;
        s.last_focal = focal;
        s.start_time = time;
        out.push(Self::emit(s, GestureEventKind::PinchStart, focal, [0.0, 0.0], NO_SCALE, time, false));
    }

    /// 指が離れた・取り消された（組が始まっていれば PinchEnd。組は解く）。
    ///
    /// # 引数
    /// * `key`      - 指の鍵
    /// * `time`     - イベントの時刻（秒）
    /// * `canceled` - 取り消しか（OS の取り消し・フォーカスを失った等）
    /// * `out`      - 出すイベント
    pub fn release(&mut self, key: PointerKey, time: f64, canceled: bool, out: &mut Vec<GestureEmit>) {
        if let Some(si) = self.sessions.iter().position(|s| s.fingers.contains(&key)) {
            let s = self.sessions.remove(si);
            if s.started {
                // 終わりの位置は最後のイベントの中点（離す瞬間の指の跳ねで中点が飛ばないように）
                let scale = match (self.position_of(s.fingers[0]), self.position_of(s.fingers[1])) {
                    (Some(a), Some(b)) => pinch_scale(spans(a, b), s.base),
                    _ => NO_SCALE,
                };
                out.push(Self::emit(&s, GestureEventKind::PinchEnd, s.last_focal, [0.0, 0.0], scale, time, canceled));
            }
        }
        self.fingers.retain(|f| f.key != key);
    }

    /// すべての指を取り消す（始まったピンチは取り消しの PinchEnd）。
    pub fn cancel_all(&mut self, time: f64, out: &mut Vec<GestureEmit>) {
        for s in std::mem::take(&mut self.sessions) {
            if s.started {
                out.push(Self::emit(&s, GestureEventKind::PinchEnd, s.last_focal, [0.0, 0.0], NO_SCALE, time, true));
            }
        }
        self.fingers.clear();
    }

    /// ノード（行とその子孫）のピンチを取り消す（W2-3 の一覧の行の使い回しと同じ扱い）。
    pub fn cancel_nodes(&mut self, nodes: &std::collections::HashSet<Entity>, time: f64, out: &mut Vec<GestureEmit>) {
        let mut keep = Vec::with_capacity(self.sessions.len());
        for s in std::mem::take(&mut self.sessions) {
            if nodes.contains(&s.node) {
                if s.started {
                    out.push(Self::emit(&s, GestureEventKind::PinchEnd, s.last_focal, [0.0, 0.0], NO_SCALE, time, true));
                }
            } else {
                keep.push(s);
            }
        }
        self.sessions = keep;
        self.fingers.retain(|f| !nodes.contains(&f.node));
    }

    /// ピンチのイベント 1 件を作る。
    fn emit(
        s: &PinchSession,
        kind: GestureEventKind,
        focal: [f32; 2],
        delta: [f32; 2],
        scale: [f32; SCALE_COMPONENTS],
        time: f64,
        canceled: bool,
    ) -> GestureEmit {
        GestureEmit {
            node: s.node,
            kind,
            pointer_id: PINCH_POINTER_ID,
            position: focal,
            start_position: s.start_focal,
            delta,
            velocity: [0.0, 0.0],
            time,
            duration: if s.started { (time - s.start_time).max(0.0) } else { 0.0 },
            canceled,
            scale,
        }
    }
}

// ============================================================
//  単体テスト（倍率の式。アリーナを通した試験は tests.rs）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 倍率は今の幅 ÷ 始めたときの幅。始めたときの幅が 1 画素以下の向きは 1。
    #[test]
    fn scale_is_ratio_of_spans_with_degenerate_axes_at_one() {
        let s = pinch_scale([200.0, 200.0, 0.0], [100.0, 100.0, 0.5]);
        assert_eq!(s, [2.0, 2.0, 1.0]);
        let s = pinch_scale([50.0, 30.0, 40.0], [100.0, 60.0, 80.0]);
        assert_eq!(s, [0.5, 0.5, 0.5]);
    }

    /// 経路にピンチを受けるノードが無い指は覚えない（アリーナだけで処理する）。
    #[test]
    fn fingers_without_pinch_node_are_ignored() {
        let mut t = PinchTracker::new();
        let node = Entity::from_raw(1, 0);
        t.on_down(7, &[(node, CanvasGestureComponent::default())], [0.0, 0.0]);
        assert!(t.is_idle());
        let mut out = Vec::new();
        assert_eq!(t.on_move(7, [50.0, 0.0], 0.1, 8.0, &mut out), PinchMove::Ignored);
        assert!(out.is_empty());
    }
}
