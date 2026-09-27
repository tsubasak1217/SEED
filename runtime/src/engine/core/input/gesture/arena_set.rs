// ============================================================
//  gesture/arena_set.rs — すべての指のアリーナ（時刻の順の処理・複数指の規則・時刻の出来事。W2-2）
//
//  【1 フレームの流れ】（app/gesture_events.rs が Play の動いているフレームに 1 回呼ぶ）
//    1. 指のイベントの記録（pointer_log.rs。時刻の順）を 1 件ずつ処理する。各イベントの前に、その時刻までに
//       期限の来た出来事（長押しの時間・押下の待ち）を期限の順に起こす
//    2. 最後にフレームの今の時刻までの出来事を起こす
//    3. 出たイベントのドラッグの途中を 1 フレームぶんまとめて返す
//  判定はイベントの時刻だけで決まる（フレームが遅れても、離した時刻が長押しの期限より前ならタップ）。
//
//  【複数指の規則】（docs/input_gestures.md §2.4）
//    - 指ごとに別のアリーナ（2 本の指が別々のノードを同時にタップできる。ピンチは今回は無い）
//    - 別の指が押しているノード（タップ・長押しの競い中、または長押しの後に押している間）に次の指が触れたら
//      「複数指になった」として、前の指のそのノードのタップ・長押しを降ろし（押していれば PressCancel）、
//      次の指のアリーナにもそのノードのタップ・長押しを入れない
//    - 別の指がドラッグで捕捉しているノードと、その子孫には、次の指は参加しない（1 ノードのドラッグは 1 本まで。
//      スクロール中の一覧の中のボタンを別の指で押しても反応しない＝Flutter でスクロール中の一覧が 2 本目の指を取るのと同じ結果）
//
//  【「動いている」の申告】（W2-10a の「描く理由」の口）`activity` が、触れている指の数・ドラッグ中の指の数・
//  次に時刻で起こる出来事の時刻を返す。何も触れていなければ止まっていてよい。
// ============================================================

use crate::engine::ecs::Entity;

use super::arena::{NodeBlock, PointerArena};
use super::events::{coalesce_drag_updates, GestureEmit};
use super::pointer_log::{PointerInputEvent, PointerKey, PointerLogEntry, PointerPhase};
use super::pointer_track::PointerTrack;
use super::scene::GestureScene;
use super::thresholds::GestureMetrics;

/// ジェスチャーが「動いている」かの申告（W2-10a の描く理由に使う）。
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct GestureActivity {
    /// アリーナに参加している（ジェスチャーを受けるノードに触れている）指の数。
    pub active_pointers: usize,
    /// ドラッグで捕捉している指の数。
    pub dragging_pointers: usize,
    /// 次に時刻で起こる出来事（長押し・押下の待ち）の時刻（秒。pointer_log の時計）。無ければ None。
    pub next_deadline: Option<f64>,
}

impl GestureActivity {
    /// 何かが動いている（指が触れている）か。
    pub fn is_active(&self) -> bool {
        self.active_pointers > 0
    }
}

/// すべての指のアリーナ（App が 1 つ持つ）。
#[derive(Clone, Debug, Default)]
pub struct GestureArenaSet {
    /// 触れている指のアリーナ（触れた順）。
    arenas: Vec<PointerArena>,
}

impl GestureArenaSet {
    /// 空の状態で作る。
    pub fn new() -> Self {
        Self::default()
    }

    /// すべて捨てる（イベントは出さない。Play の開始・終了・シーンの切り替え）。
    pub fn reset(&mut self) {
        self.arenas.clear();
    }

    /// 触れている指が無いか。
    pub fn is_idle(&self) -> bool {
        self.arenas.is_empty()
    }

    /// 「動いている」かの申告。
    pub fn activity(&self, metrics: &GestureMetrics) -> GestureActivity {
        GestureActivity {
            active_pointers: self.arenas.len(),
            dragging_pointers: self.arenas.iter().filter(|a| a.dragging_node().is_some()).count(),
            next_deadline: self
                .arenas
                .iter()
                .filter_map(|a| a.next_deadline(metrics).map(|(t, _)| t))
                .min_by(|a, b| a.total_cmp(b)),
        }
    }

    /// 1 フレームぶんの指のイベントを処理する。
    ///
    /// # 引数
    /// * `entries` - 指のイベントの記録（時刻の順。位置はキャンバスの画素へ直したもの）
    /// * `now`     - フレームの今の時刻（秒。これまでに期限の来た出来事を起こす）
    /// * `scene`   - 当たり判定の経路と押下の領域の問い合わせ先
    /// * `metrics` - 画素・秒へ直した閾値
    pub fn process(
        &mut self,
        entries: &[PointerLogEntry],
        now: f64,
        scene: &dyn GestureScene,
        metrics: &GestureMetrics,
    ) -> Vec<GestureEmit> {
        let mut out = Vec::new();
        for entry in entries {
            self.advance_to(entry.time(), metrics, &mut out);
            match entry {
                PointerLogEntry::CancelAll { time } => self.cancel_all_into(*time, &mut out),
                PointerLogEntry::Pointer(e) => match e.phase {
                    PointerPhase::Down => self.pointer_down(e, scene, metrics, &mut out),
                    PointerPhase::Move => self.pointer_move(e, scene, metrics, &mut out),
                    PointerPhase::Up => self.pointer_up(e, metrics, &mut out),
                    PointerPhase::Cancel => self.pointer_cancel(e, &mut out),
                },
            }
        }
        self.advance_to(now, metrics, &mut out);
        coalesce_drag_updates(out)
    }

    /// すべての指を取り消す（一時停止・アプリが背面へ）。取り消しのイベントを返す。
    pub fn cancel_all(&mut self, time: f64) -> Vec<GestureEmit> {
        let mut out = Vec::new();
        self.cancel_all_into(time, &mut out);
        out
    }

    // ─── 内部 ──────────────────────────────────────────────

    /// 指の鍵からアリーナの添字を引く。
    fn find(&self, key: PointerKey) -> Option<usize> {
        self.arenas.iter().position(|a| a.key() == key)
    }

    /// 触れている指の間で空いている最小の番号。
    fn lowest_free_id(&self) -> u32 {
        (0u32..)
            .find(|id| self.arenas.iter().all(|a| a.track.id != *id))
            .expect("使っている番号は有限個なので空きは必ず見つかる")
    }

    /// 時刻 `t` までに期限の来た出来事を、期限の順に起こす。
    fn advance_to(&mut self, t: f64, metrics: &GestureMetrics, out: &mut Vec<GestureEmit>) {
        loop {
            let due = self
                .arenas
                .iter()
                .enumerate()
                .filter_map(|(i, a)| a.next_deadline(metrics).map(|(d, k)| (d, k, i)))
                .filter(|(d, _, _)| *d <= t)
                .min_by(|a, b| a.0.total_cmp(&b.0));
            let Some((deadline, kind, i)) = due else { break };
            // 出来事は 1 回で消える（長押しは勝者が決まる・押下の待ちは印が付く）ので、この繰り返しは必ず終わる
            self.arenas[i].fire_deadline(kind, deadline, metrics, out);
        }
    }

    /// 触れた: 当たり判定の経路で参加者を並べる（複数指の規則を当てる）。
    fn pointer_down(
        &mut self,
        e: &PointerInputEvent,
        scene: &dyn GestureScene,
        metrics: &GestureMetrics,
        out: &mut Vec<GestureEmit>,
    ) {
        // 離れが届かないまま同じ指が触れた（イベントの取りこぼし）: 前のアリーナを取り消してからやり直す
        if let Some(i) = self.find(e.pointer) {
            let mut stale = self.arenas.remove(i);
            stale.cancel(e.time, out);
        }
        let path = scene.hit_path(e.position);
        if path.is_empty() {
            return;
        }
        // 別の指がドラッグで捕捉しているノードのうち、経路で最も根に近いもの（経路は葉 → 根なので添字が最大のもの）。
        // そのノードと、その子孫（経路でそれより前）には、この指は参加しない
        let captured_up_to = path
            .iter()
            .rposition(|(key, _)| self.arenas.iter().any(|a| a.dragging_node() == Some(*key)));
        let mut blocks = Vec::with_capacity(path.len());
        for (i, (key, _)) in path.iter().enumerate() {
            if captured_up_to.is_some_and(|k| i <= k) {
                blocks.push(NodeBlock { press: true, drag: true });
                continue;
            }
            let press_busy = self.arenas.iter().any(|a| a.has_press_session(*key));
            if press_busy {
                // 複数指になった: 前の指のこのノードの押下をやめる
                for arena in &mut self.arenas {
                    arena.reject_press_for_node(*key, e.time, out);
                }
            }
            blocks.push(NodeBlock { press: press_busy, drag: false });
        }
        let id = self.lowest_free_id();
        let track = PointerTrack::new(e.pointer, id, e.position, e.time, &metrics.velocity);
        let mut arena = PointerArena::new(track, path, &blocks);
        if arena.members.is_empty() {
            // 遮るだけのノード（受けるジェスチャーなし）や、複数指の規則で何も入らなかった: この指は何もしない
            return;
        }
        arena.refresh_press(e.time, out);
        self.arenas.push(arena);
    }

    /// 動いた。
    fn pointer_move(
        &mut self,
        e: &PointerInputEvent,
        scene: &dyn GestureScene,
        metrics: &GestureMetrics,
        out: &mut Vec<GestureEmit>,
    ) {
        let Some(i) = self.find(e.pointer) else { return };
        let dragging_elsewhere: Vec<Entity> = self
            .arenas
            .iter()
            .enumerate()
            .filter(|(j, _)| *j != i)
            .filter_map(|(_, a)| a.dragging_node())
            .collect();
        self.arenas[i].on_move(e.position, e.time, scene, metrics, &dragging_elsewhere, out);
    }

    /// 離れた。
    fn pointer_up(&mut self, e: &PointerInputEvent, metrics: &GestureMetrics, out: &mut Vec<GestureEmit>) {
        let Some(i) = self.find(e.pointer) else { return };
        let mut arena = self.arenas.remove(i);
        arena.on_up(e.position, e.time, metrics, out);
    }

    /// OS に取り消された。
    fn pointer_cancel(&mut self, e: &PointerInputEvent, out: &mut Vec<GestureEmit>) {
        let Some(i) = self.find(e.pointer) else { return };
        let mut arena = self.arenas.remove(i);
        arena.track.finish_at(e.position, e.time);
        arena.cancel(e.time, out);
    }

    /// すべての指を取り消す。
    fn cancel_all_into(&mut self, time: f64, out: &mut Vec<GestureEmit>) {
        for mut arena in std::mem::take(&mut self.arenas) {
            arena.cancel(time, out);
        }
    }
}
