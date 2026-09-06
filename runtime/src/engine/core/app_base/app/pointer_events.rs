// ============================================================
//  pointer_events.rs — Play 中のキャンバス UI ポインタイベント
//
//  毎フレーム、カーソルのスクリーン座標をスクリーンスペースキャンバスの
//  ortho 座標（画面中央が原点・Y 下向き・1 単位 = 1px）へ変換し、
//  `raycast_target = true` のスプライトへ CPU ヒットテストを行う。
//  当たったアクターのスクリプトへ OnPointerEnter / Exit / Down / Up / Click を配信する。
//
//  【設計の柱】
//  1. 座標系・レイアウトはピッキング/描画/2D 物理と同じ正典を共有する
//     （compute_viewport_size_2d + build_ss_layout_maps + walk_pick_candidates_2d）。
//     判定と見た目がズレないことを、式の重複ではなく「同じ関数を呼ぶ」ことで担保する。
//  2. 状態遷移（Enter/Exit/Down/Up/Click）は World に触れない純関数
//     `resolve_pointer_events` に閉じ込め、単体テストで検証する。
//  3. 配信経路は物理イベント（OnCollisionEnter 等）と同一の FFI
//     （`ScriptComponent::run_physics_event_raw`）に相乗りする。新しい FFI 関数は追加しない。
//
//  【対応範囲】
//  スクリーンスペースキャンバス（Actor2D + CanvasComponent）のみ。
//  3D ワールド内キャンバス（Actor3D + CanvasComponent）は未対応
//  （カメラレイとキャンバス面の交差からローカル px を出す別経路が必要なため）。
// ============================================================

use std::collections::HashMap;
use std::sync::Arc;

use winit::event::MouseButton;

use crate::engine::components::{ComponentKind, ScriptComponent};
use crate::engine::core::scripting::{
    publish_canvas_mouse_position, publish_input, publish_physics_sender, with_actors, with_world,
    ScriptingHost, POINTER_EVENT_CLICK, POINTER_EVENT_DOWN, POINTER_EVENT_ENTER,
    POINTER_EVENT_EXIT, POINTER_EVENT_UP,
};
use crate::engine::core::app_base::scene::Scene;
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

use super::pick_2d::{walk_pick_candidates_2d, zone_rank, PickCand2d, PickFilter2d};
use super::canvas_text_bounds::TextBoundsMap;
use super::App;

// ─── ポインタ状態（フレーム間で持ち越す最小限）──────────────────

/// ポインタイベントのフレーム間状態。
///
/// 「今どこをホバーしているか」と「どこで押し始めたか」だけを持つ。
/// これ以上の状態（ドラッグ距離やダブルクリック）はゲーム側スクリプトの責務とする。
#[derive(Clone, Copy, Default)]
pub(super) struct PointerState {
    /// 直前フレームにホバーしていたアクター（None = 何にも乗っていない）。
    hovered: Option<Entity>,
    /// 左ボタンを押し始めたアクター（None = 押していない、または UI 外で押した）。
    pressed: Option<Entity>,
}

impl PointerState {
    /// 状態を初期化する（Play 開始/終了・シーン遷移時に呼ぶ）。
    ///
    /// 破棄済みエンティティを掴んだまま次の Play へ持ち越すと、
    /// 世代違いの別アクターへ Exit が飛ぶ。区切りで必ず捨てる。
    pub(super) fn reset(&mut self) {
        *self = Self::default();
    }
}

/// 1 フレーム分の入力（純関数 `resolve_pointer_events` への入力）。
#[derive(Clone, Copy)]
pub(super) struct PointerFrameInput<T> {
    /// このフレームの最前面ヒット対象（None = UI に当たっていない）。
    pub hit: Option<T>,
    /// 左ボタンが押された瞬間か。
    pub down: bool,
    /// 左ボタンが離された瞬間か。
    pub up: bool,
}

/// 発火するポインタイベント（対象, 種別 ID）。種別 ID は `POINTER_EVENT_*`。
pub(super) type PointerEmit<T> = (T, i32);

/// ポインタ状態遷移の純関数。World にも FFI にも触れない。
///
/// # 発火順序（同一フレーム内）
/// 1. `Exit`（前フレームの対象から出た）
/// 2. `Enter`（新しい対象へ入った）
/// 3. `Down`（対象の上で押された）
/// 4. `Up`（対象の上で離された）
/// 5. `Click`（押下と解放が同一アクターで完結した）
///
/// Unity と同じく、Up は「離したフレームにカーソルが乗っているアクター」へ送る。
/// Click は押した先と離した先が一致したときだけ送る（ボタンから指をずらして
/// 離せばキャンセルされる、という UI の常識に合わせる）。
///
/// ジェネリックにしてあるのはテストのため（本番は `Entity`、テストは `u32`）。
pub(super) fn resolve_pointer_events<T: Copy + PartialEq>(
    prev_hovered: Option<T>,
    prev_pressed: Option<T>,
    input: PointerFrameInput<T>,
) -> (Vec<PointerEmit<T>>, Option<T>, Option<T>) {
    let mut emits: Vec<PointerEmit<T>> = Vec::new();

    // ── ホバーの出入り ────────────────────────────────────────
    let hover_changed = match (prev_hovered, input.hit) {
        (Some(a), Some(b)) => a != b,
        (None, None) => false,
        _ => true,
    };
    if hover_changed {
        if let Some(prev) = prev_hovered {
            emits.push((prev, POINTER_EVENT_EXIT));
        }
        if let Some(cur) = input.hit {
            emits.push((cur, POINTER_EVENT_ENTER));
        }
    }

    // ── 押下 ─────────────────────────────────────────────────
    // UI 外で押した場合も pressed = None を記録する（＝以後の Click を無効化）。
    let mut pressed = prev_pressed;
    if input.down {
        pressed = input.hit;
        if let Some(cur) = input.hit {
            emits.push((cur, POINTER_EVENT_DOWN));
        }
    }

    // ── 解放 ─────────────────────────────────────────────────
    if input.up {
        if let Some(cur) = input.hit {
            emits.push((cur, POINTER_EVENT_UP));
            // 押した先と離した先が同一アクターのときだけクリック成立
            if pressed == Some(cur) {
                emits.push((cur, POINTER_EVENT_CLICK));
            }
        }
        pressed = None;
    }

    (emits, input.hit, pressed)
}

// ─── 座標変換 ─────────────────────────────────────────────────

/// スクリーン座標（ウィンドウ左上原点・Y 下向き・ピクセル）を
/// キャンバス ortho 座標（画面中央が原点・Y 下向き・1 単位 = 1px）へ変換する。
///
/// Play のスクリーンスペースキャンバスは「ウィンドウ全体を覆う ortho カメラ」で
/// 描画される（frame_renderer の canvas_overlay_camera_buf: half = win/2）。
/// したがって変換は中心へのオフセットだけで、レターボックスの黒帯はここでは効かない。
///
/// # レターボックスはどこで効くのか
/// 黒帯はルートキャンバスの**アンカー基準ビューポート**として効く
/// （`root_anchor_offset` に `compute_game_viewport` で求めた実効表示サイズが渡る）。
/// つまり「画面中心を原点にする」のは常に一定で、キャンバス側の原点が
/// 帯の分だけ内側へ寄る、という分担になっている。ここでその補正を二重に掛けてはいけない。
///
/// `collect_2d_screen_positions`（キャンバス → スクリーン）の厳密な逆変換。
#[inline]
pub(super) fn screen_to_canvas_px(cursor: [f32; 2], window: [f32; 2]) -> [f32; 2] {
    [cursor[0] - window[0] * 0.5, cursor[1] - window[1] * 0.5]
}

// ─── 最前面の決定 ─────────────────────────────────────────────

/// ヒット候補の中から「一番手前に描かれているもの」を選ぶ。
///
/// 描画順（frame_renderer の 2D スプライトソート）と同じ規則で比較する:
/// 1. 描画ゾーン: Foreground が Background より手前
/// 2. `layer` が大きいほど手前（同一ゾーン内の安定ソートキー）
/// 3. 同 layer なら DFS 順で後のものが手前（後から描かれる）
pub(super) fn frontmost_candidate(cands: &[PickCand2d]) -> Option<Entity> {
    cands
        .iter()
        .max_by_key(|c| {
            // zone_rank は「小さいほど手前」なので反転して最大値比較に揃える
            (
                std::cmp::Reverse(zone_rank(c.zone)),
                c.layer,
                c.dfs,
            )
        })
        .map(|c| c.entity)
}

// ─── アクターエンティティ → スクリプトハンドル ────────────────

/// world_line 内の全アクターについて「ルートエンティティ → 有効スクリプト群」を作る。
///
/// 物理イベント配信（script_scene_ops の build_dfs_script_map）と同じく、
/// 実効非アクティブなスクリプト（`sc.active = false`）へは配信しない。
fn build_entity_script_map(
    actors: &[Actor],
    world: &World,
    wl: u32,
) -> HashMap<Entity, Vec<(Arc<ScriptingHost>, isize)>> {
    /// 再帰走査（親と同一世界線の子だけを辿る）
    fn walk(
        actor: &Actor,
        world: &World,
        map: &mut HashMap<Entity, Vec<(Arc<ScriptingHost>, isize)>>,
    ) {
        let mut handles = Vec::new();
        for slot in actor.slots() {
            if slot.kind == ComponentKind::Script {
                if let Some(sc) = world.get::<ScriptComponent>(slot.entity) {
                    if sc.active {
                        handles.push((Arc::clone(&sc.host), sc.handle));
                    }
                }
            }
        }
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

// ─── フレーム処理本体 ─────────────────────────────────────────

impl App {
    /// Play 中のキャンバス UI ポインタイベントを 1 フレーム分処理する。
    ///
    /// frame_renderer のゲームロジックブロック先頭（スクリプトフェーズより前）から呼ぶ。
    /// 物理イベントと同じく「スクリプトが動く前にイベントが届いている」状態にする。
    ///
    /// スクリーンスペースキャンバス世界線でないときは、ホバー状態を畳んでから何もしない
    /// （タブ切り替えでカーソルが外れたのに Exit が飛ばない、を防ぐ）。
    pub(super) fn update_pointer_events(&mut self) {
        // ── ① 座標系の決定（描画・2D 物理と同一の正典）────────────────────
        // None = スクリーンスペースキャンバスのレイアウトではない世界線。
        let Some([win_w, win_h]) = self.compute_viewport_size_2d() else {
            self.pointer.reset();
            publish_canvas_mouse_position([0.0, 0.0]);
            return;
        };

        // スクリーン座標（ウィンドウ左上原点）→ ortho 空間（画面中央原点・Y 下向き）。
        // collect_2d_screen_positions の逆変換（あちらは +win/2 で左上原点へ戻している）。
        let cursor = self
            .input
            .mouse_position(crate::engine::core::input::InputState::Current);
        let [canvas_x, canvas_y] = screen_to_canvas_px([cursor.x, cursor.y], [win_w, win_h]);
        // スクリプトの Input.MousePositionCanvas が同じ値を読めるよう公開する
        publish_canvas_mouse_position([canvas_x, canvas_y]);

        // ── ② ヒットテスト（raycast_target = true の可視スプライトのみ）──────
        let mesh_cache = self.sprite_mesh_cpu_handle();
        let wl = self.active_world_line;
        let mut cands: Vec<PickCand2d> = Vec::new();
        {
            let Some(scene) = self.scene.as_ref() else {
                self.pointer.reset();
                return;
            };
            let (overrides, root_auto) = self.build_ss_layout_maps(
                &scene.actors,
                &scene.world,
                wl,
                win_w,
                win_h,
                None,
            );
            let mesh_of =
                |path: &str| super::sprite_bone_ops::load_sprite_mesh_cached(&mesh_cache, path);

            const IDENTITY: [[f32; 4]; 4] = [
                [1.0, 0.0, 0.0, 0.0],
                [0.0, 1.0, 0.0, 0.0],
                [0.0, 0.0, 1.0, 0.0],
                [0.0, 0.0, 0.0, 1.0],
            ];
            let mut counter: u32 = 0;
            walk_pick_candidates_2d(
                &scene.actors,
                &scene.world,
                wl,
                canvas_x,
                canvas_y,
                &mut counter,
                IDENTITY,
                [1.0, 1.0],
                None,
                0,
                crate::engine::components::CanvasDrawZone::Foreground,
                Some([win_w, win_h]),
                &overrides,
                &root_auto,
                // Play の実合成は常に「画面中央原点」（設計空間表示は Edit 専用）。
                false,
                &mesh_of,
                // ポインタイベントはテキストを対象にしない（TextComponent は
                // raycast_target を持たない）。空の表を渡して明示する。
                &TextBoundsMap::new(),
                PickFilter2d::POINTER_EVENT,
                &mut cands,
            );
        }
        let hit = frontmost_candidate(&cands);

        // ── ③ 状態遷移 ────────────────────────────────────────────────
        let frame_input = PointerFrameInput {
            hit,
            down: self.input.is_trigger_mouse(MouseButton::Left),
            up: self.input.is_release_mouse(MouseButton::Left),
        };
        let (emits, next_hovered, next_pressed) =
            resolve_pointer_events(self.pointer.hovered, self.pointer.pressed, frame_input);
        self.pointer.hovered = next_hovered;
        self.pointer.pressed = next_pressed;
        if emits.is_empty() {
            return;
        }

        // ── ④ スクリプトへ配信（物理イベントと同一経路）────────────────
        self.dispatch_pointer_events(emits);
    }

    /// 解決済みのポインタイベント列を C# スクリプトへ配信する。
    ///
    /// 物理イベント配信（dispatch_physics_event_pairs）と同じ規約:
    /// - コールバック内から Input / Physics.Raycast が使えるよう公開してから実行する
    /// - World の借用を持たない状態で FFI を呼ぶ（コールバックが World を可変で触るため）
    fn dispatch_pointer_events(&mut self, emits: Vec<PointerEmit<Entity>>) {
        publish_input(Some(&self.input));
        publish_physics_sender(self.physics_thread.as_ref().map(|t| t.command_sender()));

        let Some(scene) = &mut self.scene else {
            publish_input(None);
            publish_physics_sender(None);
            return;
        };

        let wl = self.active_world_line;
        let map = build_entity_script_map(&scene.actors, &scene.world, wl);

        // 実行する呼び出しへ展開する（ここで World の借用は不要になる）
        let mut invocations: Vec<(Arc<ScriptingHost>, isize, i32, Entity)> = Vec::new();
        for (entity, kind) in emits {
            let Some(handles) = map.get(&entity) else { continue };
            for (host, handle) in handles {
                invocations.push((Arc::clone(host), *handle, kind, entity));
            }
        }

        if !invocations.is_empty() {
            let Scene { actors, world, .. } = scene;
            with_actors(actors, || {
                with_world(world, || {
                    for (host, handle, kind, self_e) in &invocations {
                        // 相手アクターの概念が無いため other = None（C# 側 Entity.None）
                        ScriptComponent::run_physics_event_raw(host, *handle, *kind, *self_e, None);
                    }
                });
            });
        }

        publish_input(None);
        publish_physics_sender(None);
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::CanvasDrawZone;
    use super::super::canvas_collect::root_anchor_offset;

    /// テスト用の入力ヘルパ（対象 ID は u32）。
    fn inp(hit: Option<u32>, down: bool, up: bool) -> PointerFrameInput<u32> {
        PointerFrameInput { hit, down, up }
    }

    // ── 状態遷移（Enter / Exit）────────────────────────────────

    /// 何も乗っていない状態から乗ると Enter だけが出る。
    #[test]
    fn enter_fires_on_first_hover() {
        let (e, hov, pre) = resolve_pointer_events(None, None, inp(Some(1), false, false));
        assert_eq!(e, vec![(1, POINTER_EVENT_ENTER)]);
        assert_eq!(hov, Some(1));
        assert_eq!(pre, None);
    }

    /// 同じ対象に乗り続けている間はイベントが出ない（Enter の連打をしない）。
    #[test]
    fn no_event_while_hover_unchanged() {
        let (e, hov, _) = resolve_pointer_events(Some(1), None, inp(Some(1), false, false));
        assert!(e.is_empty());
        assert_eq!(hov, Some(1));
    }

    /// 対象から外れると Exit が出る。
    #[test]
    fn exit_fires_when_leaving() {
        let (e, hov, _) = resolve_pointer_events(Some(1), None, inp(None, false, false));
        assert_eq!(e, vec![(1, POINTER_EVENT_EXIT)]);
        assert_eq!(hov, None);
    }

    /// 隣のボタンへ直接移ると Exit → Enter の順で 1 フレームに両方出る。
    #[test]
    fn exit_then_enter_when_moving_between_targets() {
        let (e, hov, _) = resolve_pointer_events(Some(1), None, inp(Some(2), false, false));
        assert_eq!(e, vec![(1, POINTER_EVENT_EXIT), (2, POINTER_EVENT_ENTER)]);
        assert_eq!(hov, Some(2));
    }

    // ── 状態遷移（Down / Up / Click）──────────────────────────

    /// 同一アクター上で押して離すと Down → Up → Click の順に出る。
    #[test]
    fn click_completes_on_same_actor() {
        // 押下フレーム
        let (e1, hov, pre) = resolve_pointer_events(Some(1), None, inp(Some(1), true, false));
        assert_eq!(e1, vec![(1, POINTER_EVENT_DOWN)]);
        assert_eq!(pre, Some(1));
        // 解放フレーム
        let (e2, _, pre2) = resolve_pointer_events(hov, pre, inp(Some(1), false, true));
        assert_eq!(e2, vec![(1, POINTER_EVENT_UP), (1, POINTER_EVENT_CLICK)]);
        assert_eq!(pre2, None, "解放後は押下対象を忘れる");
    }

    /// 押した先と離した先が違えば Click は出ない（Up だけ）。
    #[test]
    fn click_cancelled_when_released_elsewhere() {
        let (_, _, pre) = resolve_pointer_events(Some(1), None, inp(Some(1), true, false));
        let (e, _, pre2) = resolve_pointer_events(Some(2), pre, inp(Some(2), false, true));
        assert!(
            e.contains(&(2, POINTER_EVENT_UP)),
            "離した先には Up が届く"
        );
        assert!(
            !e.iter().any(|(_, k)| *k == POINTER_EVENT_CLICK),
            "Click は成立しない"
        );
        assert_eq!(pre2, None);
    }

    /// UI の外で押してからボタン上で離しても Click は成立しない。
    #[test]
    fn click_not_fired_when_pressed_outside_ui() {
        let (e1, _, pre) = resolve_pointer_events(None, None, inp(None, true, false));
        assert!(e1.is_empty(), "UI 外の押下ではイベントを出さない");
        assert_eq!(pre, None);
        let (e2, _, _) = resolve_pointer_events(None, pre, inp(Some(1), false, true));
        assert_eq!(
            e2,
            vec![(1, POINTER_EVENT_ENTER), (1, POINTER_EVENT_UP)],
            "Enter と Up は出るが Click は出ない"
        );
    }

    /// UI 外で押して UI 外で離しても何も起きない（押下対象は消える）。
    #[test]
    fn press_and_release_outside_ui_is_silent() {
        let (e, hov, pre) = resolve_pointer_events(None, None, inp(None, true, true));
        assert!(e.is_empty());
        assert_eq!(hov, None);
        assert_eq!(pre, None);
    }

    /// 押しっぱなしで対象外へ出た場合、Exit は出るが押下対象は保持される
    /// （戻ってきて離せばクリック成立する = ボタン UI の一般的な挙動）。
    #[test]
    fn press_survives_leaving_and_returning() {
        let (_, _, pre) = resolve_pointer_events(Some(1), None, inp(Some(1), true, false));
        let (e_out, hov_out, pre_out) = resolve_pointer_events(Some(1), pre, inp(None, false, false));
        assert_eq!(e_out, vec![(1, POINTER_EVENT_EXIT)]);
        assert_eq!(pre_out, Some(1), "押下対象は保持される");
        let (e_back, hov_back, pre_back) =
            resolve_pointer_events(hov_out, pre_out, inp(Some(1), false, false));
        assert_eq!(e_back, vec![(1, POINTER_EVENT_ENTER)]);
        assert_eq!(pre_back, Some(1));
        let (e_up, _, _) = resolve_pointer_events(hov_back, pre_back, inp(Some(1), false, true));
        assert_eq!(e_up, vec![(1, POINTER_EVENT_UP), (1, POINTER_EVENT_CLICK)]);
    }

    // ── 最前面選択 ────────────────────────────────────────────

    /// 候補生成ヘルパ（テストは entity の index だけで区別する）。
    fn cand(index: u32, dfs: usize, zone: CanvasDrawZone, layer: i32) -> PickCand2d {
        PickCand2d {
            dfs,
            entity: Entity::from_raw(index, 0),
            kind: super::super::pick_2d::PickKind2d::Sprite,
            zone,
            depth: 0,
            layer,
        }
    }

    /// 候補なしなら None。
    #[test]
    fn frontmost_of_empty_is_none() {
        assert!(frontmost_candidate(&[]).is_none());
    }

    /// 前景ゾーンは背景ゾーンより手前（layer が背景側で大きくても勝てない）。
    #[test]
    fn frontmost_prefers_foreground_zone() {
        let cands = vec![
            cand(10, 0, CanvasDrawZone::Background, 999),
            cand(11, 1, CanvasDrawZone::Foreground, 0),
        ];
        assert_eq!(frontmost_candidate(&cands), Some(Entity::from_raw(11, 0)));
    }

    /// 同一ゾーンでは layer が大きい方が手前（DFS 順より優先）。
    #[test]
    fn frontmost_prefers_higher_layer() {
        let cands = vec![
            cand(20, 5, CanvasDrawZone::Foreground, 1),
            cand(21, 0, CanvasDrawZone::Foreground, 7),
        ];
        assert_eq!(frontmost_candidate(&cands), Some(Entity::from_raw(21, 0)));
    }

    /// 同一ゾーン・同一 layer なら DFS 順で後のものが手前（後から描かれる）。
    #[test]
    fn frontmost_prefers_later_dfs_on_tie() {
        let cands = vec![
            cand(30, 3, CanvasDrawZone::Foreground, 2),
            cand(31, 9, CanvasDrawZone::Foreground, 2),
        ];
        assert_eq!(frontmost_candidate(&cands), Some(Entity::from_raw(31, 0)));
    }

    // ── 座標変換（スクリーン → キャンバス）────────────────────

    /// 画面中央はキャンバス原点、左上は -win/2 になる。
    #[test]
    fn screen_to_canvas_centers_the_origin() {
        let win = [1280.0, 720.0];
        assert_eq!(screen_to_canvas_px([640.0, 360.0], win), [0.0, 0.0]);
        assert_eq!(screen_to_canvas_px([0.0, 0.0], win), [-640.0, -360.0]);
        assert_eq!(screen_to_canvas_px([1280.0, 720.0], win), [640.0, 360.0]);
    }

    /// `collect_2d_screen_positions`（キャンバス → スクリーン: +win/2）の逆変換になっている。
    /// この 2 つがずれると「見えている位置」と「クリック判定」が横滑りする。
    #[test]
    fn screen_to_canvas_is_inverse_of_screen_position_api() {
        let win = [1600.0, 900.0];
        let canvas = [-123.5, 47.25];
        // ScreenPosition API と同じ式でスクリーンへ戻す
        let screen = [canvas[0] + win[0] * 0.5, canvas[1] + win[1] * 0.5];
        assert_eq!(screen_to_canvas_px(screen, win), canvas);
    }

    /// レターボックス（上下に黒帯）: ルートキャンバスの基準ビューポートが
    /// ウィンドウより低いとき、キャンバス上端・下端は画面中央に対して対称に置かれ、
    /// 上下の帯の高さが一致する。
    #[test]
    fn letterbox_bars_are_symmetric_top_and_bottom() {
        let win = [1600.0, 1000.0];
        // 16:9 を 16:10 のウィンドウへフィットさせた実効表示領域（上下に 50px ずつの帯）
        let vp = [1600.0, 900.0];
        // ルートキャンバス（anchor = 0,0）の原点は -vp/2（root_anchor_offset の design_space=false）
        let origin = root_anchor_offset([0.0, 0.0], vp[0], vp[1], false);
        // キャンバス左上・右下をスクリーン座標へ戻す（= screen_to_canvas_px の逆）
        let top = origin[1] + win[1] * 0.5;
        let bottom = origin[1] + vp[1] + win[1] * 0.5;
        assert_eq!(top, 50.0, "上の帯");
        assert_eq!(win[1] - bottom, 50.0, "下の帯");
        assert_eq!(top, win[1] - bottom, "上下の帯が対称");
        // 帯の中（y = 10px）はキャンバスの外側にある
        let inside_bar = screen_to_canvas_px([800.0, 10.0], win);
        assert!(inside_bar[1] < origin[1], "上の帯はキャンバス矩形の外");
    }

    /// ピラーボックス（左右に黒帯）: 同じ規則が横方向にも成り立つ。
    #[test]
    fn pillarbox_bars_are_symmetric_left_and_right() {
        let win = [1600.0, 900.0];
        // 4:3 を 16:9 のウィンドウへフィットさせた実効表示領域（左右に 200px ずつの帯）
        let vp = [1200.0, 900.0];
        let origin = root_anchor_offset([0.0, 0.0], vp[0], vp[1], false);
        let left = origin[0] + win[0] * 0.5;
        let right = origin[0] + vp[0] + win[0] * 0.5;
        assert_eq!(left, 200.0, "左の帯");
        assert_eq!(win[0] - right, 200.0, "右の帯");
        assert_eq!(left, win[0] - right, "左右の帯が対称");
    }

    // ── raycast_target のオプトイン（実際の走査を通す）──────────

    /// テスト用に「ルートキャンバス + スプライト子アクター 1 枚」のシーンを作る。
    ///
    /// キャンバスは `size`、スプライトはキャンバス左上（0,0）から `sprite_size` の矩形。
    /// 戻り値の Vec<Actor> と World をそのまま walk_pick_candidates_2d へ渡せる。
    fn build_one_sprite_scene(
        size: [f32; 2],
        sprite_size: [f32; 2],
        raycast_target: bool,
    ) -> (Vec<Actor>, World) {
        use crate::engine::components::{
            CanvasComponent, CanvasTransform, SpriteComponent,
        };

        let mut world = World::new();

        // ルート（キャンバス）
        let root_entity = world.spawn();
        world.insert(root_entity, CanvasTransform::default());
        let canvas_slot = world.spawn();
        world.insert(
            canvas_slot,
            CanvasComponent {
                width: size[0],
                height: size[1],
                ..CanvasComponent::default()
            },
        );
        let mut root = Actor::new_2d(root_entity, "Root");
        root.world_line = 0;
        root.add_slot_typed::<CanvasComponent>("Canvas", ComponentKind::Canvas, canvas_slot);

        // 子（スプライト）
        let child_entity = world.spawn();
        world.insert(child_entity, CanvasTransform::default());
        let sprite_slot = world.spawn();
        world.insert(
            sprite_slot,
            SpriteComponent {
                width: sprite_size[0],
                height: sprite_size[1],
                raycast_target,
                ..SpriteComponent::default()
            },
        );
        let mut child = Actor::new_2d(child_entity, "Button");
        child.world_line = 0;
        child.add_slot_typed::<SpriteComponent>("Sprite", ComponentKind::Sprite, sprite_slot);
        root.children_mut().push(child);

        (vec![root], world)
    }

    /// 指定キャンバス座標でポインタ用の候補収集を走らせる（実行時と同じ引数構成）。
    fn collect_pointer_hits(
        actors: &[Actor],
        world: &World,
        canvas_pt: [f32; 2],
        viewport: [f32; 2],
    ) -> Vec<PickCand2d> {
        const IDENTITY: [[f32; 4]; 4] = [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        let empty: HashMap<Entity, [f32; 2]> = HashMap::new();
        let mesh_of = |_: &str| None;
        let mut out = Vec::new();
        let mut counter: u32 = 0;
        walk_pick_candidates_2d(
            actors,
            world,
            0,
            canvas_pt[0],
            canvas_pt[1],
            &mut counter,
            IDENTITY,
            [1.0, 1.0],
            None,
            0,
            CanvasDrawZone::Foreground,
            Some(viewport),
            &empty,
            &empty,
            false,
            &mesh_of,
            &TextBoundsMap::new(),
            PickFilter2d::POINTER_EVENT,
            &mut out,
        );
        out
    }

    /// `raycast_target = true` のスプライトは、その矩形の中でヒットする。
    #[test]
    fn raycast_target_true_is_hit_inside_the_sprite() {
        let vp = [800.0, 600.0];
        let (actors, world) = build_one_sprite_scene(vp, [200.0, 100.0], true);
        // キャンバス左上は ortho 空間で -vp/2。スプライト中心はそこから (100, 50)。
        let center = [-vp[0] * 0.5 + 100.0, -vp[1] * 0.5 + 50.0];
        let hits = collect_pointer_hits(&actors, &world, center, vp);
        assert_eq!(hits.len(), 1, "スプライト 1 枚だけが候補になる");
        assert!(frontmost_candidate(&hits).is_some());
    }

    /// スプライト矩形の外はヒットしない（判定が矩形に収まっている）。
    #[test]
    fn raycast_target_true_misses_outside_the_sprite() {
        let vp = [800.0, 600.0];
        let (actors, world) = build_one_sprite_scene(vp, [200.0, 100.0], true);
        let outside = [-vp[0] * 0.5 + 300.0, -vp[1] * 0.5 + 50.0];
        assert!(collect_pointer_hits(&actors, &world, outside, vp).is_empty());
    }

    /// `raycast_target = false`（既定）のスプライトは、矩形の中でも一切ヒットしない。
    /// これが崩れると背景画像がボタンのクリックを食う。
    #[test]
    fn raycast_target_false_is_ignored() {
        let vp = [800.0, 600.0];
        let (actors, world) = build_one_sprite_scene(vp, [200.0, 100.0], false);
        let center = [-vp[0] * 0.5 + 100.0, -vp[1] * 0.5 + 50.0];
        assert!(
            collect_pointer_hits(&actors, &world, center, vp).is_empty(),
            "オプトインしていないスプライトは判定対象外"
        );
    }

    /// 非アクティブなアクターのスプライトは、raycast_target が true でもヒットしない
    /// （描画されていないものはクリックできない）。
    #[test]
    fn inactive_actor_is_not_hit() {
        let vp = [800.0, 600.0];
        let (mut actors, world) = build_one_sprite_scene(vp, [200.0, 100.0], true);
        actors[0].children_mut()[0].active = false;
        let center = [-vp[0] * 0.5 + 100.0, -vp[1] * 0.5 + 50.0];
        assert!(collect_pointer_hits(&actors, &world, center, vp).is_empty());
    }

    /// スロットを無効化（enabled = false）したスプライトもヒットしない。
    #[test]
    fn disabled_sprite_slot_is_not_hit() {
        let vp = [800.0, 600.0];
        let (mut actors, world) = build_one_sprite_scene(vp, [200.0, 100.0], true);
        actors[0].children_mut()[0].slots_mut()[0].enabled = false;
        let center = [-vp[0] * 0.5 + 100.0, -vp[1] * 0.5 + 50.0];
        assert!(collect_pointer_hits(&actors, &world, center, vp).is_empty());
    }
}
