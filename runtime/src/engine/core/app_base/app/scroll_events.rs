// ============================================================
//  scroll_events.rs — Play 中のスクロール（ドラッグ・慣性・跳ね返り・入れ子・ScrollTo を進めて配る。W2-3）
//
//  【1 フレームの流れ】（frame_renderer.rs のゲームロジックの頭。ジェスチャーの直後・スクリプトより前）
//    0. シーンに CanvasScrollComponent が 1 つも無ければ何もしない（スクロールを使わないゲームの費用は 0 に近い）
//    1. 窓（スクロールのノード）の引き表を木から作り、状態 CanvasScrollState が無ければ置く
//    2. 触れて止める: このフレームに触れた指の経路にある、指に触れずに動いている窓（慣性・ScrollTo）を止める
//    3. ジェスチャー（update_gestures が受け渡した）のドラッグ・フリックを時刻の順に当てる（入れ子は nesting.rs の規則で
//       内側 → 外側へ受け渡す）。離した指は、フリックを受ける窓（fling_receiver）へ速度を、他の関わった窓へ速度 0 を渡して
//       離した後の動きを作る
//    4. 触れて止めた窓から指が離れたら、速度 0 で離す（はみ出しの戻り・スナップ）
//    5. スクリプトの要求（Jump・ScrollTo）を当てる
//    6. 窓・中身の大きさが変わった窓を手当てする（範囲の外なら戻す・慣性は新しい範囲で作り直す）
//    7. 慣性・跳ね返り・ScrollTo を実時間の dt で進める
//    8. 状態の変化からイベント（開始・位置・終了）を作り、ノードのスクリプトの OnScroll* へ配る
//  レイアウトの走査は描画のときに状態の位置を読み、中身をずらす。窓・中身の大きさは描画の表から `metrics` に写す
//  （`apply_scroll_metrics`。次のフレームの物理が使う）。動いている間は「描く理由」の motion（redraw_hooks.rs）。
// ============================================================

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use crate::engine::components::{CanvasScrollComponent, ScriptComponent, ScrollEdge};
use crate::engine::core::app_base::scene::Scene;
use crate::engine::core::canvas_layout::scroll_view::scroll_of;
use crate::engine::core::canvas_layout::{CanvasLayoutTable, CanvasScrollRegion};
use crate::engine::core::canvas_scroll::constants::EPSILON;
use crate::engine::core::canvas_scroll::controller::{
    advance, apply_request, begin_drag, hold, on_range_changed, release, scroll_axes,
};
use crate::engine::core::canvas_scroll::nesting::{effective_len, fling_receiver, route_drag, ChainLink};
use crate::engine::core::canvas_scroll::state::AXES;
use crate::engine::core::canvas_scroll::{
    collect_events, CanvasScrollState, ScrollEmit, ScrollMetrics, ScrollPhase,
};
use crate::engine::core::input::gesture::{GestureEmit, GestureEventKind};
use crate::engine::core::scripting::scroll_ffi::RawScrollEvent;
use crate::engine::core::scripting::{publish_input, publish_physics_sender, with_actors, with_world, ScriptingHost};
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

use super::canvas_screen_env::current_canvas_screen;
use super::gesture_events::build_entity_script_map;
use super::App;

/// 入口の無い SEEDScripting.dll を 1 度だけ警告したか（ログ爆発防止）。
static MISSING_ENTRY_WARNED: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

/// スクロールのシステムのフレーム間の状態（App が 1 つ持つ）。
#[derive(Default)]
pub(super) struct ScrollSystemState {
    /// ジェスチャーから届いた、窓宛てのドラッグ・フリック（時刻の順。次の update_scrolls で使って空にする）。
    inbox: Vec<GestureEmit>,
    /// 触れた指の経路のノード（次の update_scrolls で使って空にする）。
    touched: HashSet<Entity>,
    /// 今触れている指の経路のノード（ジェスチャーの処理のたびに書き直す）。
    under_pointers: HashSet<Entity>,
    /// ドラッグの途中（指の番号 → 受け渡しの記録）。
    sessions: HashMap<u32, DragSession>,
    /// 前のフレームの描画の表のスクロールの領域（窓・中身の大きさ。次の update_scrolls が状態の metrics へ写す）。
    layout_regions: Vec<CanvasScrollRegion>,
}

/// 1 本の指のスクロールのドラッグ。
struct DragSession {
    /// 指を取った窓（アクターの entity）。
    node: Entity,
    /// ドラッグで動いた・離した後の動きを作る窓のスロット（指を取った窓を含む）。
    involved: Vec<Entity>,
    /// 離した（DragEnd を受けた）。
    released: bool,
    /// 離した速度（キャンバスの画素/秒。指の向き。フリックが届けばその速度）。
    release_velocity: [f32; 2],
}

impl ScrollSystemState {
    /// 状態を捨てる（Play の開始・終了・シーンの切り替え）。
    pub(super) fn reset(&mut self) {
        *self = Self::default();
    }

    /// フレームの描画の表のスクロールの領域を控える（frame_renderer がメインの 2D キャンバスの表を作った直後に呼ぶ）。
    pub(super) fn store_layout_regions(&mut self, table: &CanvasLayoutTable) {
        self.layout_regions.clear();
        self.layout_regions.extend_from_slice(&table.scroll_regions);
        if !table.scroll_regions.is_empty() {
            log_scroll_layout_stats(table);
        }
    }

    /// ジェスチャーの処理の結果を受け取る（update_gestures が配る前に呼ぶ）。
    ///
    /// # 引数
    /// * `emits`   - このフレームのジェスチャーのイベント（ドラッグ・フリックだけを控える。窓かどうかは update_scrolls が見る）
    /// * `touched` - このフレームに触れた指の経路のノード
    /// * `under`   - 今触れている指の経路のノード
    pub(super) fn receive_gestures(&mut self, emits: &[GestureEmit], touched: Vec<Entity>, under: HashSet<Entity>) {
        self.inbox.extend(emits.iter().copied().filter(|e| {
            matches!(
                e.kind,
                GestureEventKind::DragStart | GestureEventKind::DragUpdate | GestureEventKind::DragEnd | GestureEventKind::Fling
            )
        }));
        self.touched.extend(touched);
        self.under_pointers = under;
    }
}

/// 窓 1 つの引き表の行（毎フレーム木から作る）。
struct ScrollNodeEntry {
    /// 窓のノード（アクターの entity）。
    node: Entity,
    /// CanvasScrollComponent のスロットのエンティティ（状態の置き場）。
    slot: Entity,
    /// 祖先の窓（この一覧の添字。近い順）。
    ancestors: Vec<usize>,
}

/// 世界線の中の有効な窓を、深さ優先の並びで集める（祖先の窓の添字つき。非アクティブの部分木は除く）。
fn collect_scroll_nodes(actors: &[Actor], world: &World, wl: u32) -> Vec<ScrollNodeEntry> {
    /// 再帰でたどる。`stack` は祖先の窓の添字（遠い順）。
    fn walk(actor: &Actor, world: &World, stack: &mut Vec<usize>, out: &mut Vec<ScrollNodeEntry>) {
        if !actor.active {
            return;
        }
        let mine = scroll_of(actor, world).map(|s| {
            out.push(ScrollNodeEntry { node: actor.entity, slot: s.slot, ancestors: stack.iter().rev().copied().collect() });
            out.len() - 1
        });
        if let Some(index) = mine {
            stack.push(index);
        }
        for child in &actor.children {
            walk(child, world, stack, out);
        }
        if mine.is_some() {
            stack.pop();
        }
    }
    let mut out = Vec::new();
    let mut stack = Vec::new();
    for root in actors.iter().filter(|a| a.world_line == wl) {
        walk(root, world, &mut stack, &mut out);
    }
    out
}

/// 窓の設定と状態を写しで読む（入れ子の鎖を作るとき、複数の窓を同時に読むため）。
fn snapshot(world: &World, slot: Entity) -> Option<(CanvasScrollComponent, CanvasScrollState)> {
    Some((world.get::<CanvasScrollComponent>(slot)?.clone(), world.get::<CanvasScrollState>(slot)?.clone()))
}

/// 鎖の 1 つを作る（大きさが分からない窓は None＝鎖に入れない）。
fn chain_link(settings: &CanvasScrollComponent, state: &CanvasScrollState, axis: usize) -> Option<ChainLink> {
    let metrics = state.metrics?;
    Some(ChainLink {
        position: state.position[axis],
        range: metrics.range(axis),
        px_per_unit: metrics.unit_scale(axis).px_per_unit,
        bounce: settings.edge == ScrollEdge::Bounce,
        hand_off_to_parent: settings.hand_off_to_parent,
    })
}

/// 軸の鎖（指を取った窓 → 同じ軸をスクロールする祖先の窓。`hand_off_to_parent` で切れる所まで）のスロットと写し。
fn axis_chain(world: &World, entries: &[ScrollNodeEntry], index: usize, axis: usize) -> Vec<(Entity, ChainLink)> {
    let mut chain = Vec::new();
    for i in std::iter::once(index).chain(entries[index].ancestors.iter().copied()) {
        let Some((settings, state)) = snapshot(world, entries[i].slot) else { continue };
        if !settings.direction.scrolls_axis(axis) {
            continue;
        }
        if let Some(link) = chain_link(&settings, &state, axis) {
            chain.push((entries[i].slot, link));
        }
    }
    let links: Vec<ChainLink> = chain.iter().map(|(_, l)| *l).collect();
    chain.truncate(effective_len(&links));
    chain
}

impl App {
    /// シーンに CanvasScrollComponent が 1 つでもあるか（無効なものも数える。ストレージを 1 つ引くだけ）。
    pub(super) fn scene_has_scroll_nodes(&self) -> bool {
        self.scene
            .as_ref()
            .is_some_and(|s| s.world.query::<CanvasScrollComponent>().next().is_some())
    }

    /// 動いているスクロールがあるか（W2-10a の「描く理由」の motion。慣性・ScrollTo・要求の処理待ち）。
    pub(super) fn scroll_motion_active(&self) -> bool {
        self.scene
            .as_ref()
            .is_some_and(|s| s.world.query::<CanvasScrollState>().any(|(_, st)| st.is_active()))
    }

    /// スクロールを 1 フレームぶん処理して配る（Play の動いているフレームだけ。ジェスチャーの直後に呼ぶ）。
    ///
    /// # 引数
    /// * `dt` - 経過時間（秒。実時間＝Time.Scale の影響を受けない）
    pub(super) fn update_scrolls(&mut self, dt: f64) {
        let inbox = std::mem::take(&mut self.canvas_scroll.inbox);
        let touched = std::mem::take(&mut self.canvas_scroll.touched);
        if !self.scene_has_scroll_nodes() {
            self.canvas_scroll.sessions.clear();
            return;
        }
        let dp_scale = current_canvas_screen().dp_scale();
        let touch_slop_px = self.gestures.thresholds().metrics(dp_scale).touch_slop_px;
        let wl = self.active_world_line;
        let Some(scene) = self.scene.as_mut() else { return };

        // ── 1. 窓の引き表と状態（前のフレームの描画が測った大きさを写す）──
        let entries = collect_scroll_nodes(&scene.actors, &scene.world, wl);
        let index_of: HashMap<Entity, usize> = entries.iter().enumerate().map(|(i, e)| (e.node, i)).collect();
        for entry in &entries {
            if scene.world.get::<CanvasScrollState>(entry.slot).is_none() {
                scene.world.insert(entry.slot, CanvasScrollState::default());
            }
            if let Some(state) = scene.world.get_mut::<CanvasScrollState>(entry.slot) {
                state.owner = Some(entry.node);
            }
        }
        apply_scroll_metrics(&mut scene.world, &self.canvas_scroll.layout_regions);

        // ── 2. 触れて止める（指に触れずに動いている窓）──
        for node in &touched {
            if let Some(&i) = index_of.get(node) {
                if let Some(state) = scene.world.get_mut::<CanvasScrollState>(entries[i].slot) {
                    hold(state);
                }
            }
        }

        // ── 3. ドラッグ・フリック ──
        let sessions = &mut self.canvas_scroll.sessions;
        for emit in &inbox {
            let Some(&index) = index_of.get(&emit.node) else { continue };
            handle_drag_event(&mut scene.world, &entries, index, emit, touch_slop_px, sessions);
        }
        let released: Vec<u32> = sessions.iter().filter(|(_, s)| s.released).map(|(k, _)| *k).collect();
        for pointer in released {
            if let Some(session) = sessions.remove(&pointer) {
                finish_drag(&mut scene.world, &entries, &index_of, &session);
            }
        }

        // ── 4. 触れて止めた窓から指が離れた（ドラッグにならなかった）──
        let dragging_nodes: HashSet<Entity> = sessions.values().map(|s| s.node).collect();
        for entry in &entries {
            if self.canvas_scroll.under_pointers.contains(&entry.node) || dragging_nodes.contains(&entry.node) {
                continue;
            }
            let Some(settings) = scene.world.get::<CanvasScrollComponent>(entry.slot).cloned() else { continue };
            if let Some(state) = scene.world.get_mut::<CanvasScrollState>(entry.slot) {
                if state.phase == ScrollPhase::Held || (state.phase == ScrollPhase::Dragging && state.drag_pointer.is_some()) {
                    release(&settings, state, [0.0; AXES]);
                }
            }
        }

        // ── 5〜7. 要求・大きさの変化・進める ──
        for entry in &entries {
            let Some(settings) = scene.world.get::<CanvasScrollComponent>(entry.slot).cloned() else { continue };
            let Some(state) = scene.world.get_mut::<CanvasScrollState>(entry.slot) else { continue };
            apply_request(&settings, state);
            if let Some(metrics) = state.metrics {
                let max = [0, 1].map(|a| metrics.max_position(a));
                if state.applied_max != Some(max) {
                    let changed = state.applied_max.is_some();
                    state.applied_max = Some(max);
                    let out_of_range = scroll_axes(&settings).any(|a| metrics.range(a).out_of_range(state.position[a]));
                    if changed || out_of_range {
                        on_range_changed(&settings, state);
                    }
                }
            }
            if !matches!(state.phase, ScrollPhase::Dragging | ScrollPhase::Held) {
                advance(&settings, state, dt);
            }
        }

        // ── 8. イベント ──
        let mut emits: Vec<ScrollEmit> = Vec::new();
        for entry in &entries {
            if let Some(state) = scene.world.get_mut::<CanvasScrollState>(entry.slot) {
                collect_events(entry.node, state, &mut emits);
            }
        }
        self.dispatch_scroll_events(emits);
    }

    /// スクロールのイベントを C# スクリプトへ配る（ジェスチャーのイベントの dispatch と同じ規約）。
    fn dispatch_scroll_events(&mut self, emits: Vec<ScrollEmit>) {
        if emits.is_empty() {
            return;
        }
        if scroll_diag_enabled() {
            for e in &emits {
                eprintln!(
                    "[SEED SCROLL] {} node={} pos=({:.1},{:.1}) delta=({:.1},{:.1}) vel=({:.0},{:.0}) max=({:.1},{:.1}) viewport=({:.0},{:.0}) content=({:.0},{:.0}){}",
                    e.kind.label(),
                    e.node,
                    e.position[0],
                    e.position[1],
                    e.delta[0],
                    e.delta[1],
                    e.velocity[0],
                    e.velocity[1],
                    e.max_position[0],
                    e.max_position[1],
                    e.viewport[0],
                    e.viewport[1],
                    e.content[0],
                    e.content[1],
                    if e.dragging { " dragging" } else { "" },
                );
            }
        }
        publish_input(Some(&self.input));
        publish_physics_sender(self.physics_thread.as_ref().map(|t| t.command_sender()));
        let Some(scene) = &mut self.scene else {
            publish_input(None);
            publish_physics_sender(None);
            return;
        };
        let map = build_entity_script_map(&scene.actors, &scene.world, self.active_world_line);
        let mut invocations: Vec<(Arc<ScriptingHost>, isize, RawScrollEvent)> = Vec::new();
        for e in &emits {
            let Some(handles) = map.get(&e.node) else { continue };
            let raw = RawScrollEvent::from_emit(e);
            for (host, handle) in handles {
                invocations.push((Arc::clone(host), *handle, raw));
            }
        }
        if !invocations.is_empty() {
            let Scene { actors, world, .. } = scene;
            with_actors(actors, || {
                with_world(world, || {
                    for (host, handle, raw) in &invocations {
                        if !ScriptComponent::run_scroll_event_raw(host, *handle, raw)
                            && !MISSING_ENTRY_WARNED.swap(true, std::sync::atomic::Ordering::Relaxed)
                        {
                            eprintln!(
                                "[SEED SCROLL] SEEDScripting.dll に OnScrollEvent がありません（古い DLL）。スクロールのイベントは届きません（スクロール自体は動きます）。scripting を再ビルドしてください"
                            );
                        }
                    }
                });
            });
        }
        publish_input(None);
        publish_physics_sender(None);
    }
}

/// ドラッグ・フリックのイベント 1 件を窓へ当てる。
///
/// # 引数
/// * `index`         - 指を取った窓（`entries` の添字）
/// * `touch_slop_px` - ドラッグが始まる移動（DragStart の移動量のうち slop を超えた分だけを当てる）
fn handle_drag_event(
    world: &mut World,
    entries: &[ScrollNodeEntry],
    index: usize,
    emit: &GestureEmit,
    touch_slop_px: f32,
    sessions: &mut HashMap<u32, DragSession>,
) {
    let slot = entries[index].slot;
    let new_session = |node: Entity| DragSession {
        node,
        involved: vec![slot],
        released: false,
        release_velocity: [0.0, 0.0],
    };
    match emit.kind {
        GestureEventKind::DragStart => {
            if let Some(state) = world.get_mut::<CanvasScrollState>(slot) {
                begin_drag(state, emit.pointer_id);
            }
            let mut session = new_session(emit.node);
            // slop を超えた分だけ当てる（押した位置から飛ばない。Android の ScrollView・Flutter の DragStartBehavior.start と同じ）
            let len = (emit.delta[0] * emit.delta[0] + emit.delta[1] * emit.delta[1]).sqrt();
            if len > touch_slop_px {
                let keep = 1.0 - touch_slop_px / len;
                apply_drag_delta(world, entries, index, [emit.delta[0] * keep, emit.delta[1] * keep], &mut session);
            }
            sessions.insert(emit.pointer_id, session);
        }
        GestureEventKind::DragUpdate => {
            let session = sessions.entry(emit.pointer_id).or_insert_with(|| new_session(emit.node));
            if let Some(state) = world.get_mut::<CanvasScrollState>(slot) {
                if state.phase != ScrollPhase::Dragging {
                    begin_drag(state, emit.pointer_id);
                }
            }
            apply_drag_delta(world, entries, index, emit.delta, session);
            // ドラッグ中の速度（位置の向き。イベント・スクリプトの Velocity）
            if let Some(state) = world.get_mut::<CanvasScrollState>(slot) {
                if let Some(metrics) = state.metrics {
                    let v = metrics.canvas_to_units(emit.velocity);
                    state.velocity = [-v[0], -v[1]];
                }
            }
        }
        GestureEventKind::DragEnd => {
            let session = sessions.entry(emit.pointer_id).or_insert_with(|| new_session(emit.node));
            session.released = true;
            // 取り消し（OS・一時停止）は速度 0。フリックでなければ速度 0（Flutter の ScrollDragController.end と同じ）
            session.release_velocity = [0.0, 0.0];
        }
        GestureEventKind::Fling => {
            let session = sessions.entry(emit.pointer_id).or_insert_with(|| new_session(emit.node));
            session.released = true;
            session.release_velocity = emit.velocity;
        }
        _ => {}
    }
}

/// ドラッグの移動（キャンバスの画素・指の向き）を、軸ごとに入れ子の鎖へ当てる。
fn apply_drag_delta(world: &mut World, entries: &[ScrollNodeEntry], index: usize, delta: [f32; 2], session: &mut DragSession) {
    let Some((settings, state)) = snapshot(world, entries[index].slot) else { return };
    let Some(metrics) = state.metrics else { return };
    let finger = metrics.canvas_to_units(delta);
    for axis in scroll_axes(&settings) {
        let mut chain = axis_chain(world, entries, index, axis);
        if chain.is_empty() {
            continue;
        }
        // 指の移動の逆が位置の移動（中身が指についてくる）。画素で受け渡す
        let delta_px = -finger[axis] * metrics.unit_scale(axis).px_per_unit;
        let mut links: Vec<ChainLink> = chain.iter().map(|(_, l)| *l).collect();
        route_drag(&mut links, delta_px);
        for ((slot, before), after) in chain.iter_mut().zip(&links) {
            if (after.position - before.position).abs() <= EPSILON {
                continue;
            }
            if let Some(st) = world.get_mut::<CanvasScrollState>(*slot) {
                st.position[axis] = after.position;
                // 受け渡しで動いた外側の窓もドラッグ中にする（慣性を止め、離したときに離した後の動きを作る）
                if st.phase != ScrollPhase::Dragging {
                    st.stop_motion();
                    st.phase = ScrollPhase::Dragging;
                }
            }
            if !session.involved.contains(slot) {
                session.involved.push(*slot);
            }
        }
    }
}

/// 離した指のドラッグを終える: フリックを受ける窓へ速度を、他の関わった窓へ速度 0 を渡して離した後の動きを作る。
fn finish_drag(world: &mut World, entries: &[ScrollNodeEntry], index_of: &HashMap<Entity, usize>, session: &DragSession) {
    let Some(&index) = index_of.get(&session.node) else {
        // 窓が消えた: 関わった窓を速度 0 で離すだけ
        release_all(world, &session.involved, &HashMap::new());
        return;
    };
    let mut velocities: HashMap<Entity, [f64; AXES]> = HashMap::new();
    let mut involved = session.involved.clone();
    if let Some((settings, state)) = snapshot(world, entries[index].slot) {
        if let Some(metrics) = state.metrics {
            let finger = metrics.canvas_to_units(session.release_velocity);
            for axis in scroll_axes(&settings) {
                let chain = axis_chain(world, entries, index, axis);
                if chain.is_empty() {
                    continue;
                }
                let velocity_px = -finger[axis] * metrics.unit_scale(axis).px_per_unit;
                if velocity_px.abs() <= EPSILON {
                    continue;
                }
                let links: Vec<ChainLink> = chain.iter().map(|(_, l)| *l).collect();
                let receiver = fling_receiver(&links, velocity_px);
                let (slot, link) = chain[receiver];
                velocities.entry(slot).or_insert([0.0; AXES])[axis] = velocity_px / link.px_per_unit;
                if !involved.contains(&slot) {
                    involved.push(slot);
                }
            }
        }
    }
    release_all(world, &involved, &velocities);
}

/// 窓を離す（速度は窓ごと・軸ごと。無ければ 0）。
fn release_all(world: &mut World, slots: &[Entity], velocities: &HashMap<Entity, [f64; AXES]>) {
    for slot in slots {
        let Some(settings) = world.get::<CanvasScrollComponent>(*slot).cloned() else { continue };
        let Some(state) = world.get_mut::<CanvasScrollState>(*slot) else { continue };
        let velocity = velocities.get(slot).copied().unwrap_or([0.0; AXES]);
        release(&settings, state, velocity);
    }
}

/// 描画の表のスクロールの領域から、スクロールの状態の大きさ（`metrics`）を書く（W2-3。物理が範囲に使う）。
///
/// 状態の無い窓（Play の最初のフレームの前）には何もしない。
pub(super) fn apply_scroll_metrics(world: &mut World, regions: &[CanvasScrollRegion]) {
    for region in regions {
        let Some(state) = world.get_mut::<CanvasScrollState>(region.slot) else { continue };
        let ppu = region.px_per_unit.map(|v| if v > 0.0 && v.is_finite() { f64::from(v) } else { 1.0 });
        state.metrics = Some(ScrollMetrics {
            viewport: [0, 1].map(|a| f64::from(region.viewport_px[a]) / ppu[a]),
            content: [0, 1].map(|a| f64::from(region.content_px[a]) / ppu[a]),
            px_per_unit: ppu,
            dp_scale: f64::from(region.dp_scale),
            axis_dirs: region.axis_dirs,
        });
    }
}

/// 診断ログ（SEED_SCROLL_LOG=1）: 描画の表の行の数・スクロールの窓の数・見える範囲の外として飛ばした行の数を、
/// 前に出した値から変わったときだけ出す（性能の確認用。1,000 行の一覧で飛ばした行の数を見る）。
pub(super) fn log_scroll_layout_stats(table: &CanvasLayoutTable) {
    /// 前に出した (行の数, 窓の数, 飛ばした行の数)。
    static LAST: std::sync::Mutex<Option<(usize, u32, u32)>> = std::sync::Mutex::new(None);
    if !scroll_diag_enabled() {
        return;
    }
    let now = (table.nodes.len(), table.stats.scrolls, table.stats.culled);
    let Ok(mut last) = LAST.lock() else { return };
    if *last != Some(now) {
        *last = Some(now);
        let drawn = table.nodes.iter().filter(|n| n.is_drawn_in_view()).count();
        eprintln!(
            "[SEED SCROLL] layout: nodes={} drawn_in_view={} scrolls={} culled={}",
            now.0, drawn, now.1, now.2
        );
    }
}

/// 診断ログ（SEED_SCROLL_LOG=1）: 2D キャンバスの描画アイテム（スプライト・テキスト）の数を、変わったときだけ出す
/// （見える範囲の外を飛ばした効果の確認。1,000 行の一覧で見えている行の分だけになること）。
pub(super) fn log_draw_item_counts(sprites: usize, texts: usize) {
    /// 前に出した (スプライト, テキスト)。
    static LAST: std::sync::Mutex<Option<(usize, usize)>> = std::sync::Mutex::new(None);
    if !scroll_diag_enabled() {
        return;
    }
    let Ok(mut last) = LAST.lock() else { return };
    if *last != Some((sprites, texts)) {
        *last = Some((sprites, texts));
        eprintln!("[SEED SCROLL] draw items: sprites={sprites} texts={texts}");
    }
}

/// スクロールの診断ログを出すか（環境変数 SEED_SCROLL_LOG が 1。PC の検証用。既定は出さない）。
pub(super) fn scroll_diag_enabled() -> bool {
    /// 環境変数の名前。
    const ENV_SCROLL_LOG: &str = "SEED_SCROLL_LOG";
    /// 有効を表す値。
    const ENABLED: &str = "1";
    static ENABLED_CACHE: std::sync::OnceLock<bool> = std::sync::OnceLock::new();
    *ENABLED_CACHE.get_or_init(|| std::env::var(ENV_SCROLL_LOG).is_ok_and(|v| v.trim() == ENABLED))
}
