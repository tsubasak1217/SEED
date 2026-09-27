// ============================================================
//  gesture_events.rs — Play 中のジェスチャー（アリーナを回してスクリプトへ配る。W2-2）
//
//  【1 フレームの流れ】（frame_renderer.rs のゲームロジックの頭、ポインタイベントの直後・スクリプトより前）
//    1. Input が積んだ時刻つきの指のイベント（gesture/pointer_log.rs）を取り出す
//    2. 指が無く、触れている指も配り残しも無ければ何もしない（ジェスチャーを使わないゲームの費用は 0 に近い）
//    3. ポインタイベントと同じ文脈でレイアウトの表を作り、ジェスチャーの当たり判定の材料にする（gesture_scene.rs）
//    4. 位置を入力座標（画面の画素・左上原点）→ キャンバスの画素（画面の中央が原点）へ直し、アリーナで処理する
//    5. 出たイベントを、ノードのスクリプトの OnGesture* へ配る（ScriptBridge.OnGestureEvent。物理・ポインタのイベントと
//       同じく World の借用を持たずに呼ぶ）
//  一時停止のフレームでは、触れている指をすべて取り消し、取り消しのイベント（PressCancel・DragEnd）は
//  再開した最初のフレームで配る（スクリプトが動かないフレームでは配らない）。
//
//  【スクロール（W2-3）】CanvasScrollComponent を持つノードも参加する（gesture_scene.rs の participation_of）。
//  配る前に、ドラッグ・フリックのイベントと触れた指の経路をスクロールのシステムへ渡す（scroll_events.rs の update_scrolls が
//  同じフレームのスクリプトより前に使う）。一覧の行を使い回すときの取り消し（GameObject.CancelGestures）は
//  `cancel_gestures_under` が行と子孫の押下・ドラッグを取り消し、イベントは次のフレームの配り残しとして届く。
//
//  【既存のポインタのイベントとの関係】（docs/input_gestures.md §6）
//    - CanvasGestureComponent を付けていないノードは参加しない。OnPointer* の判定（pointer_events.rs）は従来のまま
//    - 両方あるノード（raycast_target の Sprite と有効な CanvasGesture）は、OnPointerDown / Up / Click を受けない
//      （押す・離す・クリックはジェスチャーの PressDown / PressUp / Tap が受け持つ。スクロールに負けた押下で
//      クリックが起きる取り違えを防ぐ）。OnPointerEnter / Exit（カーソルが乗った・外れた）は従来どおり届く
// ============================================================

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use crate::engine::components::{CanvasGestureComponent, ComponentKind, ScriptComponent};
use crate::engine::core::input::gesture::{
    pointer_clock_now, GestureArenaSet, GestureEmit, GestureHitScene, GestureThresholds, PointerLogEntry,
};
use crate::engine::core::scripting::gesture_ffi::RawGestureEvent;
use crate::engine::core::scripting::{publish_input, publish_physics_sender, with_actors, with_world, ScriptingHost};
use crate::engine::core::app_base::scene::Scene;
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

use super::canvas_screen_env::current_canvas_screen;
use super::gesture_scene::{build_gesture_hit_scene, gesture_of};
use super::pointer_events::screen_to_canvas_px;
use super::App;

/// 入口の無い SEEDScripting.dll を 1 度だけ警告したか（ログ爆発防止）。
static MISSING_ENTRY_WARNED: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

/// ジェスチャーのフレーム間の状態（App が 1 つ持つ）。
#[derive(Default)]
pub(super) struct GestureState {
    /// すべての指のアリーナ。
    arenas: GestureArenaSet,
    /// 閾値の表（project_settings.json の "gestures"。起動時に読む）。
    thresholds: GestureThresholds,
    /// スクリプトが動かないフレーム（一時停止）で出た取り消しのイベント。次に動いたフレームで配る。
    pending: Vec<GestureEmit>,
}

impl GestureState {
    /// 状態を捨てる（Play の開始・終了・シーンの切り替え）。破棄済みのノードへイベントを配らない。
    pub(super) fn reset(&mut self) {
        self.arenas.reset();
        self.pending.clear();
    }

    /// 閾値の表を差し替える（起動時に project_settings.json から）。
    pub(super) fn set_thresholds(&mut self, thresholds: GestureThresholds) {
        self.thresholds = thresholds.sanitized();
    }

    /// 閾値の表。
    pub(super) fn thresholds(&self) -> &GestureThresholds {
        &self.thresholds
    }

    /// ノード（行とその子孫）の押下・ドラッグを取り消し、取り消しのイベントを配り残しへ積む（W2-3。次のフレームで配る）。
    pub(super) fn cancel_nodes(&mut self, nodes: &HashSet<Entity>) {
        let emits = self.arenas.cancel_nodes(nodes, pointer_clock_now());
        self.pending.extend(emits);
    }

    /// 「動いている」かの申告（W2-10a の描く理由。指が触れている間は描き続け、次の時刻の出来事で WaitUntil。
    /// 読み手は app/redraw_hooks.rs のフレームの末尾の判定）。
    pub(super) fn activity(&self) -> crate::engine::core::input::gesture::GestureActivity {
        self.arenas.activity(&self.thresholds.metrics(current_canvas_screen().dp_scale()))
    }
}

/// 入力座標（画面の画素・左上原点）の記録を、キャンバスの画素（画面の中央が原点）へ直す【純関数】。
pub(super) fn entries_to_canvas(entries: Vec<PointerLogEntry>, window: [f32; 2]) -> Vec<PointerLogEntry> {
    entries
        .into_iter()
        .map(|entry| match entry {
            PointerLogEntry::Pointer(mut e) => {
                e.position = screen_to_canvas_px(e.position, window);
                PointerLogEntry::Pointer(e)
            }
            other => other,
        })
        .collect()
}

/// world_line 内の全アクターについて「ルートエンティティ → 有効スクリプト群」を作る（pointer_events.rs と同じ規約。
/// 実効非アクティブなスクリプト（`sc.active = false`）へは配らない）。
pub(super) fn build_entity_script_map(actors: &[Actor], world: &World, wl: u32) -> HashMap<Entity, Vec<(Arc<ScriptingHost>, isize)>> {
    /// 再帰走査（子は世界線を問わずたどる）。
    fn walk(actor: &Actor, world: &World, map: &mut HashMap<Entity, Vec<(Arc<ScriptingHost>, isize)>>) {
        let handles: Vec<_> = actor
            .slots()
            .iter()
            .filter(|slot| slot.kind == ComponentKind::Script)
            .filter_map(|slot| world.get::<ScriptComponent>(slot.entity))
            .filter(|sc| sc.active)
            .map(|sc| (Arc::clone(&sc.host), sc.handle))
            .collect();
        if !handles.is_empty() {
            map.insert(actor.entity, handles);
        }
        for child in actor.children() {
            walk(child, world, map);
        }
    }
    let mut map = HashMap::new();
    for root in actors.iter().filter(|a| a.world_line == wl) {
        walk(root, world, &mut map);
    }
    map
}

/// エンティティ → アクターを引く（ポインタのイベントの絞り込みで、ノードがジェスチャーを受けるかを調べる）。
fn find_actor<'a>(actors: &'a [Actor], entity: Entity) -> Option<&'a Actor> {
    for a in actors {
        if a.entity == entity {
            return Some(a);
        }
        if let Some(found) = find_actor(&a.children, entity) {
            return Some(found);
        }
    }
    None
}

/// ポインタのイベントのうち、ジェスチャーを受けるノードへの押す・離す・クリックを外す【純関数】
/// （両方あるノードの優先の規則。Enter / Exit は残す）。
///
/// # 引数
/// * `emits`      - ポインタのイベント（対象, 種別 ID）
/// * `is_gesture` - そのノードが有効な CanvasGesture を持つか
pub(super) fn filter_pointer_emits_for_gesture_nodes<T: Copy>(
    emits: Vec<(T, i32)>,
    is_gesture: impl Fn(T) -> bool,
) -> Vec<(T, i32)> {
    use crate::engine::core::scripting::{POINTER_EVENT_CLICK, POINTER_EVENT_DOWN, POINTER_EVENT_UP};
    emits
        .into_iter()
        .filter(|&(target, kind)| {
            let press_like = kind == POINTER_EVENT_DOWN || kind == POINTER_EVENT_UP || kind == POINTER_EVENT_CLICK;
            !(press_like && is_gesture(target))
        })
        .collect()
}

impl App {
    /// ジェスチャーを 1 フレームぶん処理して配る（Play の動いているフレームだけ。ポインタイベントの直後に呼ぶ）。
    pub(super) fn update_gestures(&mut self) {
        let entries = self.input.take_pointer_events();
        // 触れている指も配り残しも無く、新しい指のイベントが無いか、シーンにジェスチャーを受けるノードが 1 つも無ければ何もしない
        // （ジェスチャーを使わないゲームでは、記録を取り出して捨てるだけ。レイアウトの表も作らない）
        let idle = self.gestures.arenas.is_idle() && self.gestures.pending.is_empty();
        if idle && (entries.is_empty() || !self.scene_has_gesture_nodes()) {
            // 触れている指が無い（スクロールの「触れて止めた」窓は指を離したものとして扱える。W2-3）
            self.canvas_scroll.receive_gestures(&[], self.gestures.arenas.take_touched(), HashSet::new());
            return;
        }
        let now = pointer_clock_now();
        // スクリーンスペースのキャンバスの世界線でなければ、触れている指を取り消して配るだけ
        let Some(window) = self.compute_viewport_size_2d() else {
            let mut emits = std::mem::take(&mut self.gestures.pending);
            emits.extend(self.gestures.arenas.cancel_all(now));
            self.canvas_scroll.receive_gestures(&emits, self.gestures.arenas.take_touched(), HashSet::new());
            self.dispatch_gesture_events(emits, &GestureHitScene::default(), [0.0, 0.0]);
            return;
        };
        let metrics = self.gestures.thresholds.metrics(current_canvas_screen().dp_scale());
        let scene = self.build_gesture_scene_for_frame(window, metrics.dp_scale);
        let entries = entries_to_canvas(entries, window);
        let mut emits = std::mem::take(&mut self.gestures.pending);
        emits.extend(self.gestures.arenas.process(&entries, now, &scene, &metrics));
        // スクロールのシステムへ（ドラッグ・フリック・触れた指。W2-3）
        let touched = self.gestures.arenas.take_touched();
        let under = self.gestures.arenas.nodes_under_pointers();
        self.canvas_scroll.receive_gestures(&emits, touched, under);
        self.dispatch_gesture_events(emits, &scene, window);
    }

    /// アクター（一覧の行）とその子孫の押下・ドラッグを取り消す（GameObject.CancelGestures。W2-3）。
    /// 取り消しのイベント（PressCancel・取り消しの DragEnd）は次のフレームの配達で届く。
    pub(super) fn cancel_gestures_under(&mut self, root: Entity) {
        /// 部分木のエンティティを集める。
        fn collect(actor: &Actor, out: &mut HashSet<Entity>) {
            out.insert(actor.entity);
            for child in &actor.children {
                collect(child, out);
            }
        }
        let Some(scene) = self.scene.as_ref() else { return };
        let Some(actor) = find_actor(&scene.actors, root) else { return };
        let mut nodes = HashSet::new();
        collect(actor, &mut nodes);
        self.gestures.cancel_nodes(&nodes);
    }

    /// 一時停止のフレーム: 触れている指をすべて取り消す（取り消しのイベントは次に動いたフレームで配る）。
    pub(super) fn hold_gestures_while_paused(&mut self) {
        if !self.gestures.arenas.is_idle() {
            let emits = self.gestures.arenas.cancel_all(pointer_clock_now());
            self.gestures.pending.extend(emits);
        }
    }

    /// このフレームのジェスチャーの当たり判定の材料を作る（ポインタイベントと同じレイアウトの文脈）。
    fn build_gesture_scene_for_frame(&self, window: [f32; 2], dp_scale: f32) -> GestureHitScene {
        let Some(scene) = self.scene.as_ref() else { return GestureHitScene::default() };
        let wl = self.active_world_line;
        let (overrides, root_auto) = self.build_ss_layout_maps(&scene.actors, &scene.world, wl, window[0], window[1], None);
        build_gesture_hit_scene(&scene.actors, &scene.world, wl, window, &overrides, &root_auto, dp_scale)
    }

    /// シーンに CanvasGestureComponent か CanvasScrollComponent（W2-3。スクロールの窓も参加する）が 1 つでもあるか
    /// （無効なものも数える。ストレージを引くだけ）。
    fn scene_has_gesture_nodes(&self) -> bool {
        self.scene
            .as_ref()
            .is_some_and(|s| s.world.query::<CanvasGestureComponent>().next().is_some())
            || self.scene_has_scroll_nodes()
    }

    /// ノードが有効な CanvasGesture を持つか（ポインタのイベントの絞り込み用）。
    /// シーンにジェスチャーを受けるノードが 1 つも無ければ、木をたどらずに false。
    pub(super) fn is_gesture_node(&self, entity: Entity) -> bool {
        if !self.scene_has_gesture_nodes() {
            return false;
        }
        let Some(scene) = self.scene.as_ref() else { return false };
        find_actor(&scene.actors, entity).is_some_and(|actor| gesture_of(actor, &scene.world).is_some())
    }

    /// ジェスチャーのイベントを C# スクリプトへ配る（ポインタのイベントの dispatch と同じ規約）。
    ///
    /// - コールバックの中から Input / Physics.Raycast が使えるよう公開してから呼ぶ
    /// - World の借用を持たない状態で FFI を呼ぶ（コールバックが World を可変で触るため）
    fn dispatch_gesture_events(&mut self, emits: Vec<GestureEmit>, hit_scene: &GestureHitScene, window: [f32; 2]) {
        if emits.is_empty() {
            return;
        }
        if gesture_diag_enabled() {
            for e in &emits {
                eprintln!(
                    "[SEED GESTURE] {} node={} pointer={} pos=({:.1},{:.1}) delta=({:.1},{:.1}) vel=({:.0},{:.0}) dur={:.3}s{}",
                    e.kind.label(),
                    e.node,
                    e.pointer_id,
                    e.position[0],
                    e.position[1],
                    e.delta[0],
                    e.delta[1],
                    e.velocity[0],
                    e.velocity[1],
                    e.duration,
                    if e.canceled { " canceled" } else { "" },
                );
            }
        }
        let dp_scale = if hit_scene.dp_scale > 0.0 { hit_scene.dp_scale } else { 1.0 };
        publish_input(Some(&self.input));
        publish_physics_sender(self.physics_thread.as_ref().map(|t| t.command_sender()));
        let Some(scene) = &mut self.scene else {
            publish_input(None);
            publish_physics_sender(None);
            return;
        };
        let map = build_entity_script_map(&scene.actors, &scene.world, self.active_world_line);
        // 呼び出しへ展開する（ここで World の借用は不要になる）
        let mut invocations: Vec<(Arc<ScriptingHost>, isize, RawGestureEvent)> = Vec::new();
        for e in &emits {
            let Some(handles) = map.get(&e.node) else { continue };
            let local = hit_scene.node(e.node).map_or([0.0, 0.0], |n| n.local_position_in_units(e.position));
            let raw = RawGestureEvent::from_emit(e, e.node, window, local, dp_scale);
            for (host, handle) in handles {
                invocations.push((Arc::clone(host), *handle, raw));
            }
        }
        if !invocations.is_empty() {
            let Scene { actors, world, .. } = scene;
            with_actors(actors, || {
                with_world(world, || {
                    for (host, handle, raw) in &invocations {
                        if !ScriptComponent::run_gesture_event_raw(host, *handle, raw)
                            && !MISSING_ENTRY_WARNED.swap(true, std::sync::atomic::Ordering::Relaxed)
                        {
                            eprintln!(
                                "[SEED GESTURE] SEEDScripting.dll に OnGestureEvent がありません（古い DLL）。ジェスチャーのイベントは届きません。scripting を再ビルドしてください"
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

/// ジェスチャーの診断ログを出すか（環境変数 SEED_GESTURE_LOG が 1。PC の検証用。既定は出さない）。
fn gesture_diag_enabled() -> bool {
    /// 環境変数の名前。
    const ENV_GESTURE_LOG: &str = "SEED_GESTURE_LOG";
    /// 有効を表す値。
    const ENABLED: &str = "1";
    static ENABLED_CACHE: std::sync::OnceLock<bool> = std::sync::OnceLock::new();
    *ENABLED_CACHE.get_or_init(|| std::env::var(ENV_GESTURE_LOG).is_ok_and(|v| v.trim() == ENABLED))
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::input::gesture::{PointerInputEvent, PointerPhase};
    use crate::engine::core::scripting::{
        POINTER_EVENT_CLICK, POINTER_EVENT_DOWN, POINTER_EVENT_ENTER, POINTER_EVENT_EXIT, POINTER_EVENT_UP,
    };

    /// 入力座標 → キャンバスの画素（中央原点）。全部の取り消しはそのまま。
    #[test]
    fn entries_are_converted_to_canvas_space() {
        let e = PointerLogEntry::Pointer(PointerInputEvent { pointer: 1, phase: PointerPhase::Down, position: [100.0, 50.0], time: 0.0 });
        let out = entries_to_canvas(vec![e, PointerLogEntry::CancelAll { time: 1.0 }], [800.0, 600.0]);
        match out[0] {
            PointerLogEntry::Pointer(p) => assert_eq!(p.position, [-300.0, -250.0]),
            _ => panic!("指のイベントのまま"),
        }
        assert_eq!(out[1], PointerLogEntry::CancelAll { time: 1.0 });
    }

    /// 両方あるノードの規則: ジェスチャーのノードへの Down / Up / Click だけを外し、Enter / Exit と、
    /// ジェスチャーの無いノードへのイベントは一切変えない（WarashibeFishing のボタンの振る舞いは不変）。
    #[test]
    fn pointer_emits_are_filtered_only_for_gesture_nodes() {
        let all = vec![
            (1u32, POINTER_EVENT_ENTER),
            (1, POINTER_EVENT_DOWN),
            (1, POINTER_EVENT_UP),
            (1, POINTER_EVENT_CLICK),
            (1, POINTER_EVENT_EXIT),
            (2, POINTER_EVENT_ENTER),
            (2, POINTER_EVENT_DOWN),
            (2, POINTER_EVENT_UP),
            (2, POINTER_EVENT_CLICK),
        ];
        // ジェスチャーのノードが無ければ、そのまま（同じ並び・同じ数）
        assert_eq!(filter_pointer_emits_for_gesture_nodes(all.clone(), |_| false), all);
        // ノード 2 だけがジェスチャーを受ける
        let out = filter_pointer_emits_for_gesture_nodes(all, |n| n == 2);
        assert_eq!(
            out,
            vec![
                (1, POINTER_EVENT_ENTER),
                (1, POINTER_EVENT_DOWN),
                (1, POINTER_EVENT_UP),
                (1, POINTER_EVENT_CLICK),
                (1, POINTER_EVENT_EXIT),
                (2, POINTER_EVENT_ENTER),
            ]
        );
    }
}
