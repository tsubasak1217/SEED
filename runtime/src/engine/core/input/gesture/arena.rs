// ============================================================
//  gesture/arena.rs — 指 1 本のジェスチャーアリーナ（認識器の競いと押下の見た目。W2-2）
//
//  【何をするか】
//  指が触れた位置の当たり判定の経路（葉 → 根。scene.rs）の各ノードが受けたいジェスチャーの認識器を、
//  経路の順（葉が先。同じノードの中はタップ → 長押し → ドラッグ）に「参加者」として並べて競わせる。
//
//  【勝ち負けの規則】（docs/input_gestures.md §2。Flutter の GestureArena と同じ考え方）
//    1. 参加者が勝ちを申し出たら（ドラッグ: slop を超えた・長押し: 時間に達した・タップ: 離した）、
//       まだ勝者がいなければその参加者が勝ち、残りはすべて負ける。同じイベントで複数が申し出たら並びで先のもの
//    2. 参加者は自分で降りる（タップ・長押し: 動きすぎた・ノードの外へ出た。長押し・ドラッグ: 勝つ前に離した）
//    3. 離したときに勝者がいなければ、並びで最初の（まだ競っている）タップが勝つ（子のタップが親より先）
//    4. 勝った参加者は指を「捕捉」する: 以後のその指の移動・離しはすべて勝ったノードへ届く（外へ出ても）
//
//  【押下の見た目】（ノードごと。認識器ではない）
//    - 押下の候補 = まだ競っているタップ・長押しのうち並びで最初のもののノード（子が先。親は光らない）
//    - PressDown: 候補が「押下の見た目」を受けるとき、競いにまだドラッグがいなければすぐ、いれば押下の待ち
//      （既定 100ms。Android の TAP_TIMEOUT）が過ぎたら。候補のタップ・長押しが勝ったときもその場で（まだなら）出す
//    - PressUp: 押したノードのタップが勝った（離した）・長押しの後に離した
//    - PressCancel: 押したノードのタップ・長押しがすべて負けた（スクロールに負けた・外へ出た・動きすぎた・
//      複数指・取り消し）。スクリプトはこれで押下の見た目を戻す
//
//  状態はこの型の中だけにあり、時刻はすべてイベントの時刻（秒）。World には触れない（純ロジック）。
// ============================================================

use crate::engine::components::CanvasGestureComponent;
use crate::engine::ecs::Entity;

use super::events::{GestureEmit, GestureEventKind};
use super::pointer_track::PointerTrack;
use super::recognizers::{drag, fling, long_press, tap, MemberState, RecognizerKind};
use super::scene::GestureScene;
use super::thresholds::GestureMetrics;

/// アリーナに並ぶノード（経路の 1 つ）。
#[derive(Clone, Debug)]
pub struct ArenaNode {
    /// ノード（アクターの entity）。
    pub key: Entity,
    /// 受けるジェスチャーの設定（押したときの値。途中で変えても、この指の間は変わらない）。
    pub settings: CanvasGestureComponent,
    /// 押下の見た目を出している（PressDown を出して、まだ PressUp / PressCancel を出していない）。
    pub pressed: bool,
}

/// 参加者（認識器 1 つ）。
#[derive(Clone, Copy, Debug)]
pub struct Member {
    /// ノードの添字（`PointerArena::nodes`）。
    pub node: usize,
    /// 認識器の種類。
    pub kind: RecognizerKind,
    /// 状態。
    pub state: MemberState,
}

/// 複数指の規則で、そのノードの認識器を入れないか（arena_set.rs が決める）。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NodeBlock {
    /// タップ・長押しを入れない（別の指がそのノードを押している＝複数指になった）。
    pub press: bool,
    /// ドラッグを入れない（別の指がそのノードか祖先をドラッグで捕捉している。1 ノードのドラッグは 1 本の指まで）。
    pub drag: bool,
}

/// 時刻で起こる出来事の種類。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DeadlineKind {
    /// 長押しの時間に達した。
    LongPress,
    /// 押下の待ちが過ぎた。
    PressDelay,
}

/// 指 1 本のアリーナ。
#[derive(Clone, Debug)]
pub struct PointerArena {
    /// 指の追跡（押した位置・時刻・今の位置・速度）。
    pub track: PointerTrack,
    /// 経路のノード（葉 → 根）。
    pub nodes: Vec<ArenaNode>,
    /// 参加者（並びの順が優先の順）。
    pub members: Vec<Member>,
    /// 勝った参加者（`members` の添字）。
    pub winner: Option<usize>,
    /// 押下の待ちが過ぎたか。
    pub press_delay_passed: bool,
    /// ドラッグのイベントを最後に出した位置（DragUpdate の移動量の起点）。
    pub last_drag_position: [f32; 2],
}

impl PointerArena {
    /// 経路から参加者を並べてアリーナを作る（まだイベントは出さない。呼び出し側が `refresh_press` する）。
    ///
    /// # 引数
    /// * `track`  - 押したところから始めた指の追跡
    /// * `path`   - 当たり判定の経路（葉 → 根）とノードの設定
    /// * `blocks` - 経路の各ノードについて、複数指の規則で入れない認識器（`path` と同じ並び）
    pub fn new(track: PointerTrack, path: Vec<(Entity, CanvasGestureComponent)>, blocks: &[NodeBlock]) -> Self {
        let mut nodes = Vec::with_capacity(path.len());
        let mut members = Vec::new();
        for (i, (key, settings)) in path.into_iter().enumerate() {
            let block = blocks.get(i).copied().unwrap_or_default();
            if settings.enabled {
                // 同じノードの中の並び: タップ → 長押し → ドラッグ
                if settings.tap && !block.press {
                    members.push(Member { node: i, kind: RecognizerKind::Tap, state: MemberState::Possible });
                }
                if settings.long_press && !block.press {
                    members.push(Member { node: i, kind: RecognizerKind::LongPress, state: MemberState::Possible });
                }
                if settings.wants_drag_recognizer() && !block.drag {
                    members.push(Member { node: i, kind: RecognizerKind::Drag, state: MemberState::Possible });
                }
            }
            nodes.push(ArenaNode { key, settings, pressed: false });
        }
        let last_drag_position = track.position;
        Self { track, nodes, members, winner: None, press_delay_passed: false, last_drag_position }
    }

    // ─── 問い合わせ ─────────────────────────────────────────

    /// 指の鍵。
    pub fn key(&self) -> u64 {
        self.track.key
    }

    /// まだ競っているドラッグがいるか（いれば押下の見た目を待つ）。
    pub fn has_possible_drag(&self) -> bool {
        self.members.iter().any(|m| m.kind == RecognizerKind::Drag && m.state == MemberState::Possible)
    }

    /// 押下の候補のノード（まだ競っているタップ・長押しのうち並びで最初のもののノード）。
    pub fn press_candidate(&self) -> Option<usize> {
        self.members
            .iter()
            .find(|m| m.kind.is_press() && m.state == MemberState::Possible)
            .map(|m| m.node)
    }

    /// この指がドラッグで捕捉しているノード（勝ったのがドラッグなら）。
    pub fn dragging_node(&self) -> Option<Entity> {
        let w = self.winner?;
        (self.members[w].kind == RecognizerKind::Drag).then(|| self.nodes[self.members[w].node].key)
    }

    /// そのノードの押下（タップ・長押しの競い、または長押しの後の押している間）が続いているか。
    pub fn has_press_session(&self, key: Entity) -> bool {
        self.members.iter().any(|m| {
            self.nodes[m.node].key == key
                && m.kind.is_press()
                && (m.state == MemberState::Possible || (m.state == MemberState::Won && self.nodes[m.node].pressed))
        })
    }

    /// 次に時刻で起こる出来事（勝者がいれば無い）。
    pub fn next_deadline(&self, metrics: &GestureMetrics) -> Option<(f64, DeadlineKind)> {
        if self.winner.is_some() {
            return None;
        }
        let long_press = self
            .members
            .iter()
            .any(|m| m.kind == RecognizerKind::LongPress && m.state == MemberState::Possible)
            .then(|| (long_press::deadline(&self.track, metrics), DeadlineKind::LongPress));
        let press_delay = (!self.press_delay_passed && self.has_possible_drag())
            .then_some(())
            .and_then(|_| self.press_candidate())
            .filter(|&n| self.nodes[n].settings.press_feedback && !self.nodes[n].pressed)
            .map(|_| (self.track.down_time + metrics.press_delay_secs, DeadlineKind::PressDelay));
        match (long_press, press_delay) {
            (Some(a), Some(b)) => Some(if b.0 <= a.0 { b } else { a }),
            (a, b) => a.or(b),
        }
    }

    // ─── イベントの発行 ─────────────────────────────────────

    /// ノードへ 1 件出す（位置・押した位置・時間は追跡から）。
    fn emit(
        &self,
        node: usize,
        kind: GestureEventKind,
        time: f64,
        delta: [f32; 2],
        velocity: [f32; 2],
        canceled: bool,
        out: &mut Vec<GestureEmit>,
    ) {
        out.push(GestureEmit {
            node: self.nodes[node].key,
            kind,
            pointer_id: self.track.id,
            position: self.track.position,
            start_position: self.track.down_position,
            delta,
            velocity,
            time,
            duration: self.track.elapsed(time),
            canceled,
        });
    }

    /// 移動量・速度の無いイベントを出す。
    fn emit_plain(&self, node: usize, kind: GestureEventKind, time: f64, out: &mut Vec<GestureEmit>) {
        self.emit(node, kind, time, [0.0, 0.0], [0.0, 0.0], false, out);
    }

    /// ノードの押下の見た目を戻す（押していれば PressCancel）。
    fn cancel_press(&mut self, node: usize, time: f64, out: &mut Vec<GestureEmit>) {
        if self.nodes[node].pressed {
            self.nodes[node].pressed = false;
            self.emit_plain(node, GestureEventKind::PressCancel, time, out);
        }
    }

    // ─── 勝ち・負け ─────────────────────────────────────────

    /// 参加者を負けにする。押下の見た目を出しているノードのタップ・長押しがすべて負けたら PressCancel。
    pub fn lose(&mut self, i: usize, time: f64, out: &mut Vec<GestureEmit>) {
        if self.members[i].state != MemberState::Possible {
            return;
        }
        self.members[i].state = MemberState::Lost;
        let node = self.members[i].node;
        if self.members[i].kind.is_press() {
            let still_pressing = self
                .members
                .iter()
                .any(|m| m.node == node && m.kind.is_press() && m.state != MemberState::Lost);
            if !still_pressing {
                self.cancel_press(node, time, out);
            }
        }
    }

    /// 参加者を勝ちにする（残りはすべて負け）。勝ったときのイベントを出す。
    pub fn win(&mut self, i: usize, time: f64, metrics: &GestureMetrics, out: &mut Vec<GestureEmit>) {
        if self.winner.is_some() || self.members[i].state != MemberState::Possible {
            return;
        }
        self.members[i].state = MemberState::Won;
        self.winner = Some(i);
        // 残りを負けにする（押していたノードの PressCancel は勝ちのイベントより先に出る）
        for j in 0..self.members.len() {
            if j != i {
                self.lose(j, time, out);
            }
        }
        let node = self.members[i].node;
        let settings = self.nodes[node].settings.clone();
        match self.members[i].kind {
            RecognizerKind::Tap => {
                // 押下の見た目: まだなら出してから戻す（速いタップでも PressDown → PressUp → Tap の順に届く）
                if settings.press_feedback {
                    if !self.nodes[node].pressed {
                        self.emit_plain(node, GestureEventKind::PressDown, time, out);
                    }
                    self.nodes[node].pressed = false;
                    self.emit_plain(node, GestureEventKind::PressUp, time, out);
                }
                self.emit_plain(node, GestureEventKind::Tap, time, out);
            }
            RecognizerKind::LongPress => {
                if settings.press_feedback && !self.nodes[node].pressed {
                    self.nodes[node].pressed = true;
                    self.emit_plain(node, GestureEventKind::PressDown, time, out);
                }
                self.emit_plain(node, GestureEventKind::LongPress, time, out);
            }
            RecognizerKind::Drag => {
                self.last_drag_position = self.track.position;
                if settings.drag {
                    let axis = settings.drag_axis;
                    let delta = drag::project(self.track.total_delta(), axis);
                    let velocity = fling::release_velocity(
                        self.track.velocity.estimate(time, &metrics.velocity),
                        axis,
                        metrics,
                    );
                    self.emit(node, GestureEventKind::DragStart, time, delta, velocity, false, out);
                }
            }
        }
    }

    /// 押下の見た目を今の状態に合わせる（勝者がいなければ）。
    ///
    /// 候補のノードが「押下の見た目」を受け、競いにドラッグがいない（または押下の待ちが過ぎた）なら PressDown。
    /// 候補でなくなったのに押しているノードは PressCancel（通常は負けたときに出ているので、ここは保険）。
    pub fn refresh_press(&mut self, time: f64, out: &mut Vec<GestureEmit>) {
        if self.winner.is_some() {
            return;
        }
        let candidate = self.press_candidate();
        for n in 0..self.nodes.len() {
            if Some(n) != candidate && self.nodes[n].pressed {
                self.cancel_press(n, time, out);
            }
        }
        let Some(n) = candidate else { return };
        let ready = !self.has_possible_drag() || self.press_delay_passed;
        if ready && self.nodes[n].settings.press_feedback && !self.nodes[n].pressed {
            self.nodes[n].pressed = true;
            self.emit_plain(n, GestureEventKind::PressDown, time, out);
        }
    }

    // ─── 指のイベント ───────────────────────────────────────

    /// 指が動いた。
    ///
    /// # 引数
    /// * `dragging_elsewhere` - 別の指がドラッグで捕捉しているノード（そのノードのドラッグはこの指では勝てない）
    pub fn on_move(
        &mut self,
        position: [f32; 2],
        time: f64,
        scene: &dyn GestureScene,
        metrics: &GestureMetrics,
        dragging_elsewhere: &[Entity],
        out: &mut Vec<GestureEmit>,
    ) {
        self.track.move_to(position, time, &metrics.velocity);
        // 捕捉: 勝ったドラッグへだけ届ける（ノードの外でも）
        if let Some(w) = self.winner {
            if self.members[w].kind == RecognizerKind::Drag {
                let node = self.members[w].node;
                let settings = &self.nodes[node].settings;
                if settings.drag {
                    let axis = settings.drag_axis;
                    let raw = [position[0] - self.last_drag_position[0], position[1] - self.last_drag_position[1]];
                    let delta = drag::project(raw, axis);
                    let velocity = fling::release_velocity(
                        self.track.velocity.estimate(time, &metrics.velocity),
                        axis,
                        metrics,
                    );
                    self.emit(node, GestureEventKind::DragUpdate, time, delta, velocity, false, out);
                }
                self.last_drag_position = position;
            }
            return;
        }
        for i in 0..self.members.len() {
            if self.winner.is_some() {
                break;
            }
            if self.members[i].state != MemberState::Possible {
                continue;
            }
            let node = self.members[i].node;
            let key = self.nodes[node].key;
            match self.members[i].kind {
                RecognizerKind::Tap | RecognizerKind::LongPress => {
                    let moved_too_far = if self.members[i].kind == RecognizerKind::Tap {
                        tap::fails_on_move(&self.track, metrics)
                    } else {
                        long_press::fails_on_move(&self.track, metrics)
                    };
                    // ノードの押下の領域（最小のヒット領域 + ドラッグの slop）の外へ出た・ノードが消えた
                    let left = scene.press_region_contains(key, position, metrics.touch_slop_px) != Some(true);
                    if moved_too_far || left {
                        self.lose(i, time, out);
                    }
                }
                RecognizerKind::Drag => {
                    let axis = self.nodes[node].settings.drag_axis;
                    if drag::claims(&self.track, axis, metrics) && !dragging_elsewhere.contains(&key) {
                        self.win(i, time, metrics, out);
                    }
                }
            }
        }
        self.refresh_press(time, out);
    }

    /// 指が離れた（呼び出し側はこの後アリーナを捨てる）。
    pub fn on_up(&mut self, position: [f32; 2], time: f64, metrics: &GestureMetrics, out: &mut Vec<GestureEmit>) {
        self.track.finish_at(position, time);
        if let Some(w) = self.winner {
            let node = self.members[w].node;
            match self.members[w].kind {
                RecognizerKind::Drag => {
                    let settings = self.nodes[node].settings.clone();
                    let velocity = fling::release_velocity(
                        self.track.velocity.estimate(time, &metrics.velocity),
                        settings.drag_axis,
                        metrics,
                    );
                    if settings.drag {
                        self.emit(node, GestureEventKind::DragEnd, time, [0.0, 0.0], velocity, false, out);
                    }
                    if settings.fling && fling::is_fling(velocity, metrics) {
                        self.emit(node, GestureEventKind::Fling, time, [0.0, 0.0], velocity, false, out);
                    }
                }
                RecognizerKind::LongPress => {
                    if self.nodes[node].pressed {
                        self.nodes[node].pressed = false;
                        self.emit_plain(node, GestureEventKind::PressUp, time, out);
                    }
                }
                RecognizerKind::Tap => {}
            }
            return;
        }
        // 勝者がいない: 並びで最初の（まだ競っている）タップが、時間の上限の内なら勝つ
        for i in 0..self.members.len() {
            if self.winner.is_some() {
                break;
            }
            if self.members[i].state != MemberState::Possible {
                continue;
            }
            match self.members[i].kind {
                RecognizerKind::Tap if tap::accepts_on_up(&self.track, time, metrics) => self.win(i, time, metrics, out),
                _ => self.lose(i, time, out),
            }
        }
    }

    /// 指が取り消された（OS の取り消し・フォーカスを失った・アプリが背面へ・Play の停止。呼び出し側はこの後捨てる）。
    ///
    /// ドラッグの途中なら速度 0 の DragEnd（canceled = true。フリックにしない）、押下の見た目は PressCancel。
    pub fn cancel(&mut self, time: f64, out: &mut Vec<GestureEmit>) {
        if let Some(w) = self.winner {
            let node = self.members[w].node;
            match self.members[w].kind {
                RecognizerKind::Drag => {
                    if self.nodes[node].settings.drag {
                        self.emit(node, GestureEventKind::DragEnd, time, [0.0, 0.0], [0.0, 0.0], true, out);
                    }
                }
                RecognizerKind::LongPress => self.cancel_press(node, time, out),
                RecognizerKind::Tap => {}
            }
        }
        for i in 0..self.members.len() {
            self.lose(i, time, out);
        }
    }

    /// 複数指の規則: そのノードのタップ・長押しを降ろす（押していれば PressCancel。長押しの後に押している間も戻す）。
    pub fn reject_press_for_node(&mut self, key: Entity, time: f64, out: &mut Vec<GestureEmit>) {
        for i in 0..self.members.len() {
            let m = self.members[i];
            if self.nodes[m.node].key != key || !m.kind.is_press() {
                continue;
            }
            match m.state {
                MemberState::Possible => self.lose(i, time, out),
                MemberState::Won => self.cancel_press(m.node, time, out),
                MemberState::Lost => {}
            }
        }
        self.refresh_press(time, out);
    }

    /// 時刻の出来事を起こす（`next_deadline` が返したもの）。
    pub fn fire_deadline(&mut self, kind: DeadlineKind, time: f64, metrics: &GestureMetrics, out: &mut Vec<GestureEmit>) {
        match kind {
            DeadlineKind::LongPress => {
                // 同じ指の長押しは期限が同じなので、並びで最初のものが勝つ
                if let Some(i) = self
                    .members
                    .iter()
                    .position(|m| m.kind == RecognizerKind::LongPress && m.state == MemberState::Possible)
                {
                    self.win(i, time, metrics, out);
                }
            }
            DeadlineKind::PressDelay => {
                self.press_delay_passed = true;
                self.refresh_press(time, out);
            }
        }
    }
}
