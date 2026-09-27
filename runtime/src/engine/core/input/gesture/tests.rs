// ============================================================
//  gesture/tests.rs — 合成の指の列でアリーナを通して確かめる試験（W2-2）
//
//  当たり判定の材料は hit_slop.rs の試験と同じ「軸にそろった矩形のノード」（キャンバスの画素）。
//  指のイベントは pointer_log の記録と同じ形で、時刻（秒）を明示して流す（フレームの時刻は `now` だけ）。
//  確かめる規則は docs/input_gestures.md §2〜§4（アリーナの勝ち負け・押下の見た目・閾値・捕捉・取り消し・時刻）。
// ============================================================

use crate::engine::components::{CanvasGestureComponent, GestureDragAxis};

use super::arena_set::GestureArenaSet;
use super::events::{GestureEmit, GestureEventKind, GestureEventKind::*};
use super::hit_slop::tests::rect_node;
use super::hit_slop::GestureHitNode;
use super::pointer_log::{PointerInputEvent, PointerKey, PointerLogEntry, PointerPhase};
use super::scene::GestureHitScene;
use super::thresholds::{GestureMetrics, GestureThresholds};

// ─── 試験の道具 ──────────────────────────────────────────

/// 指のイベント 1 件。
fn ev(pointer: PointerKey, phase: PointerPhase, x: f32, y: f32, time: f64) -> PointerLogEntry {
    PointerLogEntry::Pointer(PointerInputEvent { pointer, phase, position: [x, y], time })
}
/// 触れた。
fn down(p: PointerKey, x: f32, y: f32, t: f64) -> PointerLogEntry {
    ev(p, PointerPhase::Down, x, y, t)
}
/// 動いた。
fn mv(p: PointerKey, x: f32, y: f32, t: f64) -> PointerLogEntry {
    ev(p, PointerPhase::Move, x, y, t)
}
/// 離れた。
fn up(p: PointerKey, x: f32, y: f32, t: f64) -> PointerLogEntry {
    ev(p, PointerPhase::Up, x, y, t)
}

/// 設定を変えたノード（大きな矩形。最小のヒット領域が効かない大きさ）。
fn node(index: u32, rect: [f32; 4], dfs: usize, settings: CanvasGestureComponent) -> GestureHitNode {
    let mut n = rect_node(index, rect[0], rect[1], rect[2], rect[3], dfs);
    n.settings = settings;
    n
}

/// ボタン（タップと押下の見た目。既定）。
fn button() -> CanvasGestureComponent {
    CanvasGestureComponent::default()
}

/// ドラッグだけを受けるノード（スクロールの領域）。
fn scroller(axis: GestureDragAxis) -> CanvasGestureComponent {
    CanvasGestureComponent { tap: false, drag: true, fling: true, drag_axis: axis, ..CanvasGestureComponent::default() }
}

/// アリーナと世界をまとめた試験台。
struct Rig {
    /// アリーナ。
    set: GestureArenaSet,
    /// ノードの世界。
    scene: GestureHitScene,
    /// 閾値（画素・秒）。
    metrics: GestureMetrics,
}

impl Rig {
    /// ノードの一覧と dp の倍率から作る。
    fn new(nodes: Vec<GestureHitNode>, dp_scale: f32) -> Self {
        Self {
            set: GestureArenaSet::new(),
            scene: GestureHitScene::new(nodes, dp_scale),
            metrics: GestureThresholds::default().metrics(dp_scale),
        }
    }

    /// 閾値の表を差し替えた試験台。
    fn with_thresholds(nodes: Vec<GestureHitNode>, dp_scale: f32, t: GestureThresholds) -> Self {
        Self { metrics: t.metrics(dp_scale), ..Self::new(nodes, dp_scale) }
    }

    /// 1 フレームぶん流して、出たイベントをそのまま返す。
    fn frame(&mut self, entries: &[PointerLogEntry], now: f64) -> Vec<GestureEmit> {
        self.set.process(entries, now, &self.scene, &self.metrics)
    }

    /// 1 フレームぶん流して、(ノードの番号, 種類) の列を返す。
    fn run(&mut self, entries: &[PointerLogEntry], now: f64) -> Vec<(u32, GestureEventKind)> {
        summary(&self.frame(entries, now))
    }
}

/// (ノードの番号, 種類) の列にする。
fn summary(emits: &[GestureEmit]) -> Vec<(u32, GestureEventKind)> {
    emits.iter().map(|e| (e.node.index(), e.kind)).collect()
}

// ─── タップと長押し ───────────────────────────────────────

/// タップと長押しの境目: 長押しの時間（500ms）の直前に離せばタップ、過ぎれば長押し。
/// 判定はイベントの時刻で決まり、フレームの時刻（now）が遅れても変わらない。
#[test]
fn tap_long_press_boundary_uses_event_time() {
    let settings = CanvasGestureComponent { long_press: true, ..button() };
    // 499ms で離す。フレームは 2 秒後にまとめて処理（フレームが大きく遅れた）→ それでもタップ
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 200.0, 200.0], 0, settings.clone())], 1.0);
    let out = r.run(&[down(1, 50.0, 50.0, 0.0), up(1, 50.0, 50.0, 0.499)], 2.0);
    assert_eq!(out, vec![(1, PressDown), (1, PressUp), (1, Tap)]);

    // 501ms で離す → 500ms で長押し（PressDown → LongPress）、離して PressUp。タップは出ない
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 200.0, 200.0], 0, settings.clone())], 1.0);
    let out = r.run(&[down(1, 50.0, 50.0, 0.0), up(1, 50.0, 50.0, 0.501)], 2.0);
    assert_eq!(out, vec![(1, PressDown), (1, LongPress), (1, PressUp)]);

    // フレームを分けても同じ: 押した後のフレーム（0.3 秒）では何も起きず、0.6 秒のフレームで長押し
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 200.0, 200.0], 0, settings)], 1.0);
    assert_eq!(r.run(&[down(1, 50.0, 50.0, 0.0)], 0.3), vec![(1, PressDown)], "競いにドラッグが無いのですぐ押下");
    let emits = r.frame(&[], 0.6);
    assert_eq!(summary(&emits), vec![(1, LongPress)]);
    assert!((emits[0].time - 0.5).abs() < 1e-9, "長押しの時刻は期限の時刻（フレームの時刻ではない）");
    assert!((emits[0].duration - 0.5).abs() < 1e-9);
    assert_eq!(r.run(&[up(1, 50.0, 50.0, 0.7)], 0.7), vec![(1, PressUp)]);
}

/// タップの時間の上限（データで 300ms にした）: 超えて離すとタップにならず、押下は取り消し。
#[test]
fn tap_max_duration_from_thresholds() {
    let t = GestureThresholds { tap_max_ms: 300.0, ..GestureThresholds::default() };
    let mut r = Rig::with_thresholds(vec![node(1, [0.0, 0.0, 200.0, 200.0], 0, button())], 1.0, t);
    assert_eq!(r.run(&[down(1, 5.0, 5.0, 0.0), up(1, 5.0, 5.0, 0.3)], 0.3), vec![(1, PressDown), (1, PressUp), (1, Tap)]);
    assert_eq!(r.run(&[down(1, 5.0, 5.0, 1.0), up(1, 5.0, 5.0, 1.31)], 1.31), vec![(1, PressDown), (1, PressCancel)]);
}

/// 長押しの後に動いてもドラッグにはならない（長押しが指を捕捉している）。
#[test]
fn long_press_captures_the_pointer() {
    let settings = CanvasGestureComponent { long_press: true, drag: true, ..button() };
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 200.0, 200.0], 0, settings)], 1.0);
    let out = r.run(&[down(1, 50.0, 50.0, 0.0), mv(1, 50.0, 51.0, 0.55), mv(1, 150.0, 51.0, 0.6), up(1, 150.0, 51.0, 0.7)], 0.7);
    assert_eq!(out, vec![(1, PressDown), (1, LongPress), (1, PressUp)], "押下は待ち（100ms）の後、長押しの後はドラッグしない");
}

// ─── slop（dp の倍率 1・2・3）────────────────────────────

/// ドラッグの slop（8 dp）の内側で離せばタップ、超えればドラッグ（倍率 1・2・3 で 8・16・24 画素）。
#[test]
fn drag_slop_in_and_out_for_dp_scales() {
    let settings = CanvasGestureComponent { drag: true, ..button() };
    for scale in [1.0f32, 2.0, 3.0] {
        let slop = 8.0 * scale;
        // 内側（slop ちょうどまで）→ タップ
        let mut r = Rig::new(vec![node(1, [0.0, 0.0, 400.0, 400.0], 0, settings.clone())], scale);
        let out = r.run(&[down(1, 100.0, 100.0, 0.0), mv(1, 100.0 + slop, 100.0, 0.05), up(1, 100.0 + slop, 100.0, 0.06)], 0.06);
        assert_eq!(out, vec![(1, PressDown), (1, PressUp), (1, Tap)], "倍率 {scale}: slop の内側はタップ");
        // 外側（slop + 0.5 画素）→ ドラッグ（押下は待ちの前なので出ていない）
        let mut r = Rig::new(vec![node(1, [0.0, 0.0, 400.0, 400.0], 0, settings.clone())], scale);
        let emits = r.frame(&[down(1, 100.0, 100.0, 0.0), mv(1, 100.0 + slop + 0.5, 100.0, 0.05), up(1, 100.0 + slop + 0.5, 100.0, 0.06)], 0.06);
        assert_eq!(summary(&emits), vec![(1, DragStart), (1, DragEnd)], "倍率 {scale}: slop の外側はドラッグ");
        assert!((emits[0].delta[0] - (slop + 0.5)).abs() < 1e-4, "DragStart の移動量は押した位置から");
    }
}

/// タップの許容移動（18 dp）: ドラッグの無いノードでは 18 dp までの揺れはタップ、超えると押下の取り消し。
#[test]
fn tap_slop_without_drag() {
    for scale in [1.0f32, 2.0, 3.0] {
        let tap_slop = 18.0 * scale;
        let mut r = Rig::new(vec![node(1, [0.0, 0.0, 400.0, 400.0], 0, button())], scale);
        let out = r.run(&[down(1, 100.0, 100.0, 0.0), mv(1, 100.0, 100.0 + tap_slop, 0.05), up(1, 100.0, 100.0 + tap_slop, 0.06)], 0.06);
        assert_eq!(out, vec![(1, PressDown), (1, PressUp), (1, Tap)], "倍率 {scale}: 内側");
        let mut r = Rig::new(vec![node(1, [0.0, 0.0, 400.0, 400.0], 0, button())], scale);
        let out = r.run(&[down(1, 100.0, 100.0, 0.0), mv(1, 100.0, 100.0 + tap_slop + 0.5, 0.05), up(1, 100.0, 100.0, 0.06)], 0.06);
        assert_eq!(out, vec![(1, PressDown), (1, PressCancel)], "倍率 {scale}: 外側は取り消し（戻ってもタップにしない）");
    }
}

// ─── ドラッグの軸と入れ子 ─────────────────────────────────

/// 縦スクロールの親の中の横ドラッグの子: 縦に動けば親が勝ち、横に動けば子が勝つ。斜めちょうどは子（並びで先）。
#[test]
fn horizontal_child_loses_vertical_move_to_vertical_parent() {
    let parent = node(1, [0.0, 0.0, 400.0, 800.0], 0, scroller(GestureDragAxis::Vertical));
    let mut child = node(2, [0.0, 100.0, 400.0, 200.0], 1, scroller(GestureDragAxis::Horizontal));
    child.ancestors = vec![0];
    let nodes = vec![parent, child];

    // 縦へ 9 画素 → 親の縦ドラッグが勝つ（子の横ドラッグは縦の動きで申し出ない）
    let mut r = Rig::new(nodes.clone(), 1.0);
    let emits = r.frame(&[down(1, 200.0, 200.0, 0.0), mv(1, 201.0, 209.0, 0.02), mv(1, 202.0, 240.0, 0.04), up(1, 202.0, 240.0, 0.05)], 0.05);
    assert_eq!(summary(&emits), vec![(1, DragStart), (1, DragUpdate), (1, DragEnd), (1, Fling)]);
    assert_eq!(emits[0].delta, [0.0, 9.0], "縦だけのドラッグは移動量の x を捨てる");
    assert_eq!(emits[1].delta, [0.0, 31.0]);

    // 横へ 9 画素 → 子の横ドラッグ
    let mut r = Rig::new(nodes.clone(), 1.0);
    let out = r.run(&[down(1, 200.0, 200.0, 0.0), mv(1, 209.0, 201.0, 0.02), up(1, 209.0, 201.0, 0.1)], 0.1);
    assert_eq!(out, vec![(2, DragStart), (2, DragEnd)]);

    // 斜めちょうど（同じ移動で両方が申し出る）→ 並びで先の子
    let mut r = Rig::new(nodes, 1.0);
    let out = r.run(&[down(1, 200.0, 200.0, 0.0), mv(1, 209.0, 209.0, 0.02), up(1, 209.0, 209.0, 0.1)], 0.1);
    assert_eq!(out, vec![(2, DragStart), (2, DragEnd)]);
}

/// 入れ子: 子のタップと親の縦スクロール。速いタップは離したときに子が勝つ（押下もそのとき）。
/// 押したまま待てば押下の待ち（100ms）で PressDown、その後スクロールすれば PressCancel → 親の DragStart。
#[test]
fn child_tap_competes_with_parent_scroll() {
    let parent = node(1, [0.0, 0.0, 400.0, 800.0], 0, scroller(GestureDragAxis::Vertical));
    let mut child = node(2, [100.0, 100.0, 200.0, 60.0], 1, button());
    child.ancestors = vec![0];
    let nodes = vec![parent, child];

    // 速いタップ（50ms）→ 離したときに PressDown → PressUp → Tap（親には何も届かない）
    let mut r = Rig::new(nodes.clone(), 1.0);
    let out = r.run(&[down(1, 150.0, 120.0, 0.0), up(1, 151.0, 121.0, 0.05)], 0.05);
    assert_eq!(out, vec![(2, PressDown), (2, PressUp), (2, Tap)]);

    // 押したまま 150ms → 100ms で PressDown、離して PressUp → Tap
    let mut r = Rig::new(nodes.clone(), 1.0);
    let emits = r.frame(&[down(1, 150.0, 120.0, 0.0)], 0.15);
    assert_eq!(summary(&emits), vec![(2, PressDown)]);
    assert!((emits[0].time - 0.1).abs() < 1e-9, "押下の待ちの時刻");
    assert_eq!(r.run(&[up(1, 150.0, 120.0, 0.2)], 0.2), vec![(2, PressUp), (2, Tap)]);

    // 押下の後にスクロール → PressCancel が先、次に親の DragStart。子のタップは起きない
    let mut r = Rig::new(nodes, 1.0);
    assert_eq!(r.run(&[down(1, 150.0, 120.0, 0.0)], 0.15), vec![(2, PressDown)]);
    let out = r.run(&[mv(1, 150.0, 135.0, 0.2), mv(1, 150.0, 160.0, 0.22), up(1, 150.0, 160.0, 0.3)], 0.3);
    assert_eq!(out, vec![(2, PressCancel), (1, DragStart), (1, DragUpdate), (1, DragEnd)], "止まってから離したのでフリックなし");
}

/// 入れ子のタップ（カードの中のボタン）: 押下の見た目は子だけ（親は光らない）、離すと子のタップ。
#[test]
fn nested_buttons_press_only_the_innermost() {
    let card = node(1, [0.0, 0.0, 400.0, 400.0], 0, CanvasGestureComponent { long_press: true, ..button() });
    let mut inner = node(2, [100.0, 100.0, 100.0, 100.0], 1, button());
    inner.ancestors = vec![0];
    let mut r = Rig::new(vec![card, inner], 1.0);
    let out = r.run(&[down(1, 150.0, 150.0, 0.0), up(1, 150.0, 150.0, 0.2)], 0.2);
    assert_eq!(out, vec![(2, PressDown), (2, PressUp), (2, Tap)]);
    // 長押しは親（カード）が受ける: 子の押下を取り消してから、カードの PressDown → LongPress
    let out = r.run(&[down(1, 150.0, 150.0, 1.0)], 1.6);
    assert_eq!(out, vec![(2, PressDown), (2, PressCancel), (1, PressDown), (1, LongPress)]);
}

// ─── フリック（速度の推定）───────────────────────────────

/// フリック: 等速 1000 px/s で動かしてすぐ離す → DragEnd の速度 ≈ 1000、Fling が続く。
/// 窓（100ms）の外の速い動きは速度に入らない。止まって（40ms 超）から離すとフリックなし。
#[test]
fn fling_velocity_from_recent_samples() {
    let settings = CanvasGestureComponent { tap: false, drag: true, fling: true, ..button() };
    let area = [0.0, 0.0, 2000.0, 400.0];

    // 8ms ごとに 8 画素（1000 px/s）
    let mut entries = vec![down(1, 100.0, 100.0, 0.0)];
    for i in 1..=12 {
        let t = f64::from(i) * 0.008;
        entries.push(mv(1, 100.0 + 1000.0 * t as f32, 100.0, t));
    }
    entries.push(up(1, 196.0, 100.0, 0.096));
    let mut r = Rig::new(vec![node(1, area, 0, settings.clone())], 1.0);
    let emits = r.frame(&entries, 0.1);
    let end = emits.iter().find(|e| e.kind == DragEnd).expect("DragEnd");
    assert!((end.velocity[0] - 1000.0).abs() < 2.0, "{:?}", end.velocity);
    assert_eq!(summary(&emits).last(), Some(&(1, Fling)));

    // 窓の外（0〜0.1 秒）は 5000 px/s、その後 0.1〜0.2 秒は 200 px/s（止まらずに離す）→ 約 200
    let mut entries = vec![down(1, 100.0, 100.0, 0.0)];
    let mut x = 100.0f32;
    for i in 1..=10 {
        x += 50.0;
        entries.push(mv(1, x, 100.0, f64::from(i) * 0.01));
    }
    for i in 1..=10 {
        x += 2.0;
        entries.push(mv(1, x, 100.0, 0.1 + f64::from(i) * 0.01));
    }
    entries.push(up(1, x, 100.0, 0.2));
    let mut r = Rig::new(vec![node(1, area, 0, settings.clone())], 1.0);
    let emits = r.frame(&entries, 0.2);
    let end = emits.iter().find(|e| e.kind == DragEnd).expect("DragEnd");
    assert!((end.velocity[0] - 200.0).abs() < 5.0, "窓の内側だけ: {:?}", end.velocity);

    // 止まって 50ms 後に離す → 速度 0・フリックなし
    let mut r = Rig::new(vec![node(1, area, 0, settings.clone())], 1.0);
    let out = r.run(&[down(1, 100.0, 100.0, 0.0), mv(1, 150.0, 100.0, 0.02), mv(1, 200.0, 100.0, 0.04), up(1, 200.0, 100.0, 0.09)], 0.09);
    assert_eq!(out, vec![(1, DragStart), (1, DragUpdate), (1, DragEnd)]);

    // 最小の速度（50 dp/s）未満 → フリックなし（倍率 2 なら 100 px/s 未満）
    let mut r = Rig::new(vec![node(1, area, 0, settings)], 2.0);
    let mut entries = vec![down(1, 100.0, 100.0, 0.0)];
    for i in 1..=30 {
        let t = f64::from(i) * 0.01;
        entries.push(mv(1, 100.0 + 90.0 * t as f32, 100.0, t)); // 90 px/s
    }
    entries.push(up(1, 127.0, 100.0, 0.3));
    let out = r.run(&entries, 0.3);
    assert!(!out.contains(&(1, Fling)), "{out:?}");
}

/// フリックだけを受けるノード: ドラッグのイベントは出ず、離したときの Fling だけ。
#[test]
fn fling_only_node() {
    let settings = CanvasGestureComponent { tap: false, fling: true, drag_axis: GestureDragAxis::Vertical, ..button() };
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 400.0, 800.0], 0, settings)], 1.0);
    let emits = r.frame(&[down(1, 100.0, 100.0, 0.0), mv(1, 100.0, 120.0, 0.01), mv(1, 100.0, 140.0, 0.02), up(1, 100.0, 140.0, 0.02)], 0.02);
    assert_eq!(summary(&emits), vec![(1, Fling)]);
    assert_eq!(emits[0].velocity[0], 0.0);
    assert!(emits[0].velocity[1] > 1000.0);
}

// ─── 取り消し ─────────────────────────────────────────────

/// 指がノードの外（押下の領域 + ドラッグの slop）へ出たら取り消し。戻って離してもタップにしない。
#[test]
fn leaving_the_node_cancels_the_press() {
    // タップの許容移動を大きくして、「外へ出た」だけで取り消されることを確かめる
    let t = GestureThresholds { tap_slop_dp: 1000.0, ..GestureThresholds::default() };
    let mut r = Rig::with_thresholds(vec![node(1, [0.0, 0.0, 100.0, 100.0], 0, button())], 1.0, t);
    // 右の縁から 8 画素（slop）までは内側
    let out = r.run(&[down(1, 95.0, 50.0, 0.0), mv(1, 108.0, 50.0, 0.05)], 0.05);
    assert_eq!(out, vec![(1, PressDown)]);
    let out = r.run(&[mv(1, 108.5, 50.0, 0.06), mv(1, 50.0, 50.0, 0.07), up(1, 50.0, 50.0, 0.08)], 0.08);
    assert_eq!(out, vec![(1, PressCancel)]);
}

/// 2 本目の指が同じボタンに触れた（複数指）→ 1 本目の押下を取り消し、どちらもタップにしない。
#[test]
fn second_finger_on_the_same_node_cancels() {
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 200.0, 200.0], 0, button())], 1.0);
    assert_eq!(r.run(&[down(1, 50.0, 50.0, 0.0)], 0.0), vec![(1, PressDown)]);
    let out = r.run(&[down(2, 150.0, 150.0, 0.05), up(1, 50.0, 50.0, 0.1), up(2, 150.0, 150.0, 0.1)], 0.1);
    assert_eq!(out, vec![(1, PressCancel)]);
}

/// アプリが背面へ回った（全部の取り消し）: 押下は PressCancel、ドラッグは速度 0・canceled の DragEnd（フリックなし）。
#[test]
fn cancel_all_on_background() {
    let a = node(1, [0.0, 0.0, 100.0, 100.0], 0, button());
    let b = node(2, [200.0, 0.0, 400.0, 400.0], 1, scroller(GestureDragAxis::Any));
    let mut r = Rig::new(vec![a, b], 1.0);
    let out = r.run(&[down(1, 50.0, 50.0, 0.0), down(2, 300.0, 100.0, 0.0), mv(2, 340.0, 100.0, 0.02)], 0.02);
    assert_eq!(out, vec![(1, PressDown), (2, DragStart)]);
    let emits = r.frame(&[PointerLogEntry::CancelAll { time: 0.03 }, up(1, 50.0, 50.0, 0.04), up(2, 340.0, 100.0, 0.04)], 0.04);
    assert_eq!(summary(&emits), vec![(1, PressCancel), (2, DragEnd)]);
    assert!(emits[1].canceled && emits[1].velocity == [0.0, 0.0]);
    assert!(r.set.is_idle(), "取り消した指の離れは無視");
}

/// OS の取り消し（Cancel）もその指だけを取り消す。
#[test]
fn os_cancel_for_one_pointer() {
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 100.0, 100.0], 0, button()), node(2, [200.0, 0.0, 100.0, 100.0], 1, button())], 1.0);
    let out = r.run(
        &[down(1, 50.0, 50.0, 0.0), down(2, 250.0, 50.0, 0.0), ev(1, PointerPhase::Cancel, 50.0, 50.0, 0.1), up(2, 250.0, 50.0, 0.1)],
        0.1,
    );
    assert_eq!(out, vec![(1, PressDown), (2, PressDown), (1, PressCancel), (2, PressUp), (2, Tap)]);
}

// ─── 捕捉 ─────────────────────────────────────────────────

/// ドラッグが勝ったら、その指の以後の移動はノードの外へ出ても勝ったノードへ届く（離れも）。
#[test]
fn drag_capture_follows_outside_the_node() {
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 100.0, 100.0], 0, scroller(GestureDragAxis::Any))], 1.0);
    let emits = r.frame(
        &[down(1, 50.0, 50.0, 0.0), mv(1, 60.0, 50.0, 0.01), mv(1, 500.0, 900.0, 0.2), up(1, 500.0, 900.0, 0.3)],
        0.3,
    );
    assert_eq!(summary(&emits), vec![(1, DragStart), (1, DragUpdate), (1, DragEnd)]);
    assert_eq!(emits[1].position, [500.0, 900.0], "外でも届く");
    assert_eq!(emits[1].delta, [440.0, 850.0], "前のドラッグのイベントからの移動");
}

/// 1 ノードのドラッグは 1 本の指まで: ドラッグ中のノードへ 2 本目が触れても、2 本目はそのノードをドラッグしない。
#[test]
fn one_drag_per_node() {
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 400.0, 400.0], 0, scroller(GestureDragAxis::Any))], 1.0);
    let out = r.run(&[down(1, 50.0, 50.0, 0.0), mv(1, 70.0, 50.0, 0.01), down(2, 300.0, 300.0, 0.02), mv(2, 330.0, 300.0, 0.03)], 0.03);
    assert_eq!(out, vec![(1, DragStart)]);
    assert_eq!(r.set.activity(&r.metrics).dragging_pointers, 1);
}

/// スクロール中（別の指がドラッグで捕捉している）の一覧の中のボタンを別の指で押しても反応しない。
/// 一覧の外（捕捉しているノードの子孫でない所）のボタンは別の指でも押せる。
#[test]
fn second_finger_inside_a_scrolling_list_is_absorbed() {
    let list = node(1, [0.0, 0.0, 400.0, 600.0], 0, scroller(GestureDragAxis::Vertical));
    let mut row = node(2, [0.0, 300.0, 400.0, 80.0], 1, button());
    row.ancestors = vec![0];
    let outside = node(3, [500.0, 0.0, 100.0, 100.0], 2, button());
    let mut r = Rig::new(vec![list, row, outside], 1.0);
    // 指 1 で一覧をスクロール中
    assert_eq!(r.run(&[down(1, 100.0, 100.0, 0.0), mv(1, 100.0, 130.0, 0.02)], 0.02), vec![(1, DragStart)]);
    // 指 2 で一覧の中の行を押して離す → 何も起きない（一覧にも 2 本目のドラッグは入らない）
    assert!(r.run(&[down(2, 200.0, 340.0, 0.05), up(2, 200.0, 340.0, 0.1)], 0.1).is_empty());
    // 指 3 で一覧の外のボタン → タップ
    assert_eq!(r.run(&[down(3, 550.0, 50.0, 0.2), up(3, 550.0, 50.0, 0.25)], 0.25), vec![(3, PressDown), (3, PressUp), (3, Tap)]);
}

// ─── マルチタッチ ─────────────────────────────────────────

/// 2 本の指が別々のボタンを同時にタップする（別のアリーナ。指の番号は 0 と 1）。
#[test]
fn two_fingers_tap_two_nodes_at_once() {
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 100.0, 100.0], 0, button()), node(2, [200.0, 0.0, 100.0, 100.0], 1, button())], 1.0);
    let emits = r.frame(&[down(7, 50.0, 50.0, 0.0), down(9, 250.0, 50.0, 0.01), up(7, 50.0, 50.0, 0.1), up(9, 250.0, 50.0, 0.1)], 0.1);
    assert_eq!(summary(&emits), vec![(1, PressDown), (2, PressDown), (1, PressUp), (1, Tap), (2, PressUp), (2, Tap)]);
    let ids: Vec<u32> = emits.iter().filter(|e| e.kind == Tap).map(|e| e.pointer_id).collect();
    assert_eq!(ids, vec![0, 1]);
}

// ─── 当たり判定（切り抜き・最小のヒット領域・遮り）──────────

/// 切り抜きの外で押した指は参加しない（ノードの見た目の中でも）。
#[test]
fn clipped_press_does_not_join() {
    let mut n = node(1, [0.0, 0.0, 200.0, 200.0], 0, button());
    n.clip_aabbs = vec![([0.0, 0.0], [200.0, 100.0])];
    let mut r = Rig::new(vec![n], 1.0);
    assert!(r.run(&[down(1, 50.0, 150.0, 0.0), up(1, 50.0, 150.0, 0.05)], 0.05).is_empty());
    assert_eq!(r.run(&[down(1, 50.0, 50.0, 0.1), up(1, 50.0, 50.0, 0.15)], 0.15), vec![(1, PressDown), (1, PressUp), (1, Tap)]);
}

/// 48 dp の最小ヒット領域: 20×20 の小さなボタンは見た目の外でも 48 dp（倍率 2 なら 96 画素）の内側ならタップ。
/// 2 つの小さなボタンの広げた領域が重なる所では、見た目に近い方。
#[test]
fn min_hit_size_and_overlap_through_the_arena() {
    for scale in [1.0f32, 2.0, 3.0] {
        let half = 24.0 * scale;
        let mut r = Rig::new(vec![node(1, [0.0, 0.0, 20.0, 20.0], 0, button())], scale);
        let x = 10.0 + half - 1.0;
        assert_eq!(r.run(&[down(1, x, 10.0, 0.0), up(1, x, 10.0, 0.05)], 0.05), vec![(1, PressDown), (1, PressUp), (1, Tap)], "倍率 {scale}");
        let x = 10.0 + half + 1.0;
        assert!(r.run(&[down(1, x, 10.0, 0.1), up(1, x, 10.0, 0.15)], 0.15).is_empty(), "倍率 {scale}: 外");
    }
    // 20×20 を 10 画素あけて並べる（倍率 1）: 間の点は近い方
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 20.0, 20.0], 0, button()), node(2, [30.0, 0.0, 20.0, 20.0], 1, button())], 1.0);
    assert_eq!(r.run(&[down(1, 22.0, 10.0, 0.0), up(1, 22.0, 10.0, 0.05)], 0.05).last(), Some(&(1, Tap)));
    assert_eq!(r.run(&[down(1, 28.0, 10.0, 0.1), up(1, 28.0, 10.0, 0.15)], 0.15).last(), Some(&(2, Tap)));
}

/// 受けるジェスチャーの無いノード（遮る板）は、後ろのボタンへ指を渡さない。押下の見た目を切ったノードは Press* を出さない。
#[test]
fn blocker_and_press_feedback_off() {
    let back = node(1, [0.0, 0.0, 200.0, 200.0], 0, button());
    let blocker = node(2, [0.0, 0.0, 100.0, 100.0], 1, CanvasGestureComponent { tap: false, ..button() });
    let mut r = Rig::new(vec![back, blocker], 1.0);
    assert!(r.run(&[down(1, 50.0, 50.0, 0.0), up(1, 50.0, 50.0, 0.05)], 0.05).is_empty(), "板の上");
    assert_eq!(r.run(&[down(1, 150.0, 150.0, 0.1), up(1, 150.0, 150.0, 0.15)], 0.15).last(), Some(&(1, Tap)), "板の外");

    let quiet = node(3, [0.0, 0.0, 100.0, 100.0], 0, CanvasGestureComponent { press_feedback: false, ..button() });
    let mut r = Rig::new(vec![quiet], 1.0);
    assert_eq!(r.run(&[down(1, 50.0, 50.0, 0.0), up(1, 50.0, 50.0, 0.05)], 0.05), vec![(3, Tap)]);
}

// ─── 時刻・まとめ・申告 ────────────────────────────────────

/// 同じ指の列をフレームの区切りを変えて流しても、出るイベント（種類・時刻）は同じ（フレームの時刻に依らない）。
#[test]
fn results_do_not_depend_on_frame_boundaries() {
    let settings = CanvasGestureComponent { long_press: true, drag: true, fling: true, ..button() };
    let entries = vec![
        down(1, 50.0, 50.0, 0.0),
        mv(1, 52.0, 50.0, 0.05),
        mv(1, 53.0, 50.0, 0.12),
        mv(1, 70.0, 50.0, 0.3),
        mv(1, 90.0, 50.0, 0.31),
        up(1, 90.0, 50.0, 0.32),
    ];
    let make = || Rig::new(vec![node(1, [0.0, 0.0, 400.0, 400.0], 0, settings.clone())], 1.0);
    // 1 フレームにまとめる
    let mut r = make();
    let all = r.frame(&entries, 0.32);
    // 1 件ずつ別のフレーム（フレームの時刻はイベントの 5ms 後）
    let mut r = make();
    let mut split = Vec::new();
    for e in &entries {
        split.extend(r.frame(std::slice::from_ref(e), e.time() + 0.005));
    }
    let key = |v: &[GestureEmit]| v.iter().map(|e| (e.kind, (e.time * 1e6).round() as i64)).collect::<Vec<_>>();
    assert_eq!(key(&all), key(&split));
    assert_eq!(summary(&all), vec![(1, PressDown), (1, PressCancel), (1, DragStart), (1, DragUpdate), (1, DragEnd), (1, Fling)]);
}

/// 「動いている」の申告: 触れている指・ドラッグ中の指・次の期限（長押し・押下の待ち）。
#[test]
fn activity_reports_pointers_and_next_deadline() {
    let settings = CanvasGestureComponent { long_press: true, ..button() };
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 200.0, 200.0], 0, settings)], 1.0);
    assert!(!r.set.activity(&r.metrics).is_active());
    r.frame(&[down(1, 50.0, 50.0, 1.0)], 1.0);
    let a = r.set.activity(&r.metrics);
    assert_eq!((a.active_pointers, a.dragging_pointers), (1, 0));
    assert!((a.next_deadline.unwrap() - 1.5).abs() < 1e-9, "長押しの期限");
    r.frame(&[up(1, 50.0, 50.0, 1.1)], 1.1);
    assert!(!r.set.activity(&r.metrics).is_active());
}
