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
use super::thresholds::{GestureMetrics, GestureThresholds, MILLIS_PER_SECOND};
use super::velocity::ReleaseRule;

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
///
/// 最後の払いの 2 標本は 20 ms 離す（2026-09-29 に 0.3 → 0.29 秒へ変えた。R3〈推定の幅が 1 フレーム 16.7 ms 未満なら 0〉のため。
/// 元の 10 ms 間隔の 2 標本は、その前の 180 ms に標本が 1 つも無いまま 17 px 動いた作りの列で、実機の指では起きない）。
#[test]
fn results_do_not_depend_on_frame_boundaries() {
    let settings = CanvasGestureComponent { long_press: true, drag: true, fling: true, ..button() };
    let entries = vec![
        down(1, 50.0, 50.0, 0.0),
        mv(1, 52.0, 50.0, 0.05),
        mv(1, 53.0, 50.0, 0.12),
        mv(1, 70.0, 50.0, 0.29),
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

// ─── スクロールの窓との組み合わせ（W2-3）─────────────────────

/// 縦スクロールの窓（ノード 1）の中のボタン（ノード 2）。
fn scroll_with_button() -> Vec<GestureHitNode> {
    let mut child = node(2, [0.0, 0.0, 400.0, 100.0], 1, button());
    child.ancestors = vec![0];
    vec![node(1, [0.0, 0.0, 400.0, 400.0], 0, scroller(GestureDragAxis::Vertical)), child]
}

/// 慣性中のスクロールの窓（吸い込むノード）は、触れた指を中の子へ渡さない（タップは起きない）。
/// 触れた指の経路（take_touched）には窓が入る（スクロールのシステムが「触れて止める」）。
#[test]
fn absorbing_scroll_window_keeps_children_from_the_pointer() {
    let mut r = Rig::new(scroll_with_button(), 1.0);
    let taps = r.run(&[down(1, 50.0, 50.0, 0.0), up(1, 50.0, 50.0, 0.05)], 0.05);
    assert_eq!(taps, vec![(2, PressDown), (2, PressUp), (2, Tap)], "止まっている窓の中のボタンは押せる");
    let window = crate::engine::ecs::Entity::from_raw(1, 0);
    let mut r = Rig::new(scroll_with_button(), 1.0);
    r.scene = r.scene.clone().with_absorbing(std::iter::once(window).collect());
    let out = r.run(&[down(1, 50.0, 50.0, 0.0)], 0.0);
    assert!(out.is_empty(), "ボタンは参加しない: {out:?}");
    assert_eq!(r.set.take_touched(), vec![window]);
    assert!(r.set.nodes_under_pointers().contains(&window));
    assert!(r.run(&[up(1, 50.0, 50.0, 0.05)], 0.05).is_empty(), "離してもタップにならない");
    assert!(r.set.nodes_under_pointers().is_empty());
    // 触れた指がそのまま動けば、窓のドラッグになる
    let mut r = Rig::new(scroll_with_button(), 1.0);
    r.scene = r.scene.clone().with_absorbing(std::iter::once(window).collect());
    let out = r.run(&[down(1, 50.0, 50.0, 0.0), mv(1, 50.0, 70.0, 0.05)], 0.05);
    assert_eq!(out, vec![(1, DragStart)]);
}

/// 行の使い回し（cancel_nodes）: 押しているボタンは PressCancel で戻り、その後に離しても Tap にならない。
#[test]
fn cancel_nodes_cancels_press_and_prevents_tap() {
    let mut r = Rig::new(scroll_with_button(), 1.0);
    // スクロールの中のボタンは押下の待ち（100ms）の後に PressDown
    let out = r.run(&[down(1, 50.0, 50.0, 0.0)], 0.15);
    assert_eq!(out, vec![(2, PressDown)]);
    let row = crate::engine::ecs::Entity::from_raw(2, 0);
    let canceled = r.set.cancel_nodes(&std::iter::once(row).collect(), 0.2);
    assert_eq!(summary(&canceled), vec![(2, PressCancel)]);
    assert!(r.run(&[up(1, 50.0, 50.0, 0.25)], 0.25).is_empty(), "取り消した押下は Tap にならない");
}

/// 行の使い回し（cancel_nodes）: 行が捕捉しているドラッグ（スワイプ）は取り消しの DragEnd で終わり、以後の移動は届かない。
#[test]
fn cancel_nodes_ends_captured_drag() {
    let swipe = CanvasGestureComponent { tap: false, drag: true, fling: true, drag_axis: GestureDragAxis::Horizontal, ..button() };
    let mut r = Rig::new(vec![node(3, [0.0, 0.0, 400.0, 100.0], 0, swipe)], 1.0);
    let out = r.run(&[down(1, 50.0, 50.0, 0.0), mv(1, 80.0, 50.0, 0.05)], 0.05);
    assert_eq!(out, vec![(3, DragStart)]);
    let canceled = r.set.cancel_nodes(&std::iter::once(crate::engine::ecs::Entity::from_raw(3, 0)).collect(), 0.06);
    assert_eq!(summary(&canceled), vec![(3, DragEnd)]);
    assert!(canceled[0].canceled);
    assert!(r.run(&[mv(1, 120.0, 50.0, 0.08), up(1, 120.0, 50.0, 0.1)], 0.1).is_empty());
    assert!(r.set.is_idle());
}

// ─── ピンチ（W2-8。docs/input_gestures.md §2.6）──────────────

/// グラフのような「タップ・横のドラッグ・フリック・ピンチ」を受けるノード。
fn chart() -> CanvasGestureComponent {
    CanvasGestureComponent {
        drag: true,
        fling: true,
        drag_axis: GestureDragAxis::Horizontal,
        pinch: true,
        press_feedback: false,
        ..button()
    }
}

/// 2 本の指を広げる: 距離の変化が slop（8 dp）を超えたら PinchStart（倍率 1）、以後は始めたときの距離との比。
/// 離すと PinchEnd。2 本とも同じノードなのでタップにはならない（複数指の規則）。
#[test]
fn pinch_starts_after_slop_and_reports_scale_from_start() {
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 400.0, 200.0], 0, chart())], 1.0);
    // 2 本の指を 100 画素あけて置く（横に並ぶ）
    let out = r.run(&[down(1, 150.0, 100.0, 0.0), down(2, 250.0, 100.0, 0.01)], 0.01);
    assert!(out.is_empty(), "まだ何も起きない（押下の見た目は受けない設定）: {out:?}");
    // 片方を 6 画素動かす（距離 106。変化 6 < slop 8）→ まだ始まらない。横のドラッグの slop（8）にも届かない
    assert!(r.run(&[mv(2, 256.0, 100.0, 0.02)], 0.02).is_empty());
    // さらに 4 画素（距離 110。変化 10 > 8）→ 始まる。始めたときの距離 110 が倍率の分母
    let emits = r.frame(&[mv(2, 260.0, 100.0, 0.03)], 0.03);
    assert_eq!(summary(&emits), vec![(1, PinchStart)], "ドラッグ（片方の指の横 10 画素）より先にピンチが取る: {emits:?}");
    assert_eq!(emits[0].scale, [1.0, 1.0, 1.0]);
    assert_eq!(emits[0].position, [205.0, 100.0], "位置は 2 本の指の中点");
    // 指の間を 220 へ（両方を 55 画素ずつ外へ）→ 倍率 2（横も 2、縦は幅 0 なので 1）
    let emits = r.frame(&[mv(1, 95.0, 100.0, 0.05), mv(2, 315.0, 100.0, 0.05)], 0.05);
    assert_eq!(summary(&emits), vec![(1, PinchUpdate)], "1 フレームの途中は 1 件にまとまる");
    assert!((emits[0].scale[0] - 2.0).abs() < 1e-5 && (emits[0].scale[1] - 2.0).abs() < 1e-5, "{:?}", emits[0].scale);
    assert_eq!(emits[0].scale[2], 1.0, "始めたときの縦の幅が 0 の向きは 1");
    assert_eq!(emits[0].delta, [0.0, 0.0], "中点は動いていない");
    assert!((emits[0].duration - 0.02).abs() < 1e-9, "時間はピンチを始めてから");
    // 1 本離す → PinchEnd（取り消しではない）。残った指は離すまで何も起こさない
    let emits = r.frame(&[up(1, 95.0, 100.0, 0.06)], 0.06);
    assert_eq!(summary(&emits), vec![(1, PinchEnd)]);
    assert!(!emits[0].canceled);
    assert!(r.run(&[mv(2, 400.0, 100.0, 0.07), up(2, 400.0, 100.0, 0.08)], 0.08).is_empty(), "残った指はタップもドラッグもしない");
    assert!(r.set.is_idle());
}

/// 1 本目の指でグラフを横にドラッグしている途中に 2 本目が触れて広げると、ドラッグは取り消しの DragEnd で終わり、ピンチが取る。
#[test]
fn pinch_takes_over_the_nodes_own_drag() {
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 400.0, 200.0], 0, chart())], 1.0);
    let out = r.run(&[down(1, 100.0, 100.0, 0.0), mv(1, 120.0, 100.0, 0.02)], 0.02);
    assert_eq!(out, vec![(1, DragStart)]);
    // 2 本目はドラッグ中のノードのアリーナには入らないが、ピンチの組には入る
    assert!(r.run(&[down(2, 300.0, 100.0, 0.03)], 0.03).is_empty());
    let emits = r.frame(&[mv(2, 330.0, 100.0, 0.05)], 0.05);
    assert_eq!(summary(&emits), vec![(1, DragEnd), (1, PinchStart)]);
    assert!(emits[0].canceled, "ドラッグは取り消しで終わる（フリックにしない）");
    // 以後 1 本目の動きはピンチだけ
    let out = r.run(&[mv(1, 60.0, 100.0, 0.07)], 0.07);
    assert_eq!(out, vec![(1, PinchUpdate)]);
}

/// 縦の一覧（外側）が 1 本目の指でスクロールしている間に、中のグラフへ 2 本目が触れて広げてもピンチにしない。
#[test]
fn pinch_does_not_start_when_outer_scroll_owns_a_finger() {
    let list = node(1, [0.0, 0.0, 400.0, 800.0], 0, scroller(GestureDragAxis::Vertical));
    let mut graph = node(2, [0.0, 100.0, 400.0, 300.0], 1, chart());
    graph.ancestors = vec![0];
    let mut r = Rig::new(vec![list, graph], 1.0);
    let out = r.run(&[down(1, 100.0, 200.0, 0.0), mv(1, 100.0, 230.0, 0.02)], 0.02);
    assert_eq!(out, vec![(1, DragStart)], "縦の動きは一覧が取る");
    // 2 本目はグラフの上（一覧の子。一覧がドラッグ中なのでアリーナには入らない）
    assert!(r.run(&[down(2, 300.0, 200.0, 0.03)], 0.03).is_empty());
    let out = r.run(&[mv(2, 340.0, 200.0, 0.05), mv(1, 60.0, 240.0, 0.05)], 0.05);
    assert!(out.iter().all(|(n, k)| *n == 1 && *k == DragUpdate), "ピンチにならず一覧のスクロールが続く: {out:?}");
}

/// ピンチを受けないノードでは 2 本の指を広げても何も起きない（従来どおり。W2-2 の複数指の規則）。
#[test]
fn nodes_without_pinch_get_no_pinch_events() {
    let settings = CanvasGestureComponent { pinch: false, ..chart() };
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 400.0, 200.0], 0, settings)], 1.0);
    let out = r.run(&[down(1, 150.0, 100.0, 0.0), down(2, 250.0, 100.0, 0.01), mv(2, 250.0, 150.0, 0.03)], 0.03);
    assert!(out.iter().all(|(_, k)| !matches!(k, PinchStart | PinchUpdate | PinchEnd)), "{out:?}");
}

/// 始まったピンチのノードには 3 本目の指は参加しない（タップにならない）。取り消し（CancelAll）は取り消しの PinchEnd。
#[test]
fn third_finger_is_blocked_and_cancel_all_ends_pinch() {
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 400.0, 200.0], 0, chart())], 1.0);
    let out = r.run(&[down(1, 150.0, 100.0, 0.0), down(2, 250.0, 100.0, 0.01), mv(2, 270.0, 100.0, 0.02)], 0.02);
    assert_eq!(out, vec![(1, PinchStart)]);
    let out = r.run(&[down(3, 350.0, 150.0, 0.03), up(3, 350.0, 150.0, 0.05)], 0.05);
    assert!(out.is_empty(), "3 本目はタップにならない: {out:?}");
    let emits = r.frame(&[PointerLogEntry::CancelAll { time: 0.06 }], 0.06);
    assert_eq!(summary(&emits), vec![(1, PinchEnd)]);
    assert!(emits[0].canceled);
    assert!(r.set.is_idle());
}

/// ピンチの間は「動いている」（W2-10a）: ピンチが捕捉した 2 本の指を数える。
#[test]
fn pinch_counts_as_activity() {
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 400.0, 200.0], 0, chart())], 1.0);
    r.run(&[down(1, 150.0, 100.0, 0.0), down(2, 250.0, 100.0, 0.01), mv(2, 280.0, 100.0, 0.02)], 0.02);
    let a = r.set.activity(&r.metrics);
    assert_eq!((a.active_pointers, a.dragging_pointers), (2, 2));
    assert!(a.is_active());
}

/// 縦に並んだ 2 本の指のピンチ: 全体・縦の倍率は変わり、横は始めたときの幅が 0 なので 1。倍率 1 未満（すぼめる）も出る。
#[test]
fn pinch_scale_components_and_shrinking() {
    let mut r = Rig::new(vec![node(1, [0.0, 0.0, 400.0, 400.0], 0, chart())], 1.0);
    r.run(&[down(1, 200.0, 100.0, 0.0), down(2, 200.0, 300.0, 0.01), mv(2, 200.0, 280.0, 0.02)], 0.02);
    let emits = r.frame(&[mv(2, 200.0, 190.0, 0.04)], 0.04);
    assert_eq!(summary(&emits), vec![(1, PinchUpdate)]);
    assert!((emits[0].scale[0] - 0.5).abs() < 1e-5 && (emits[0].scale[2] - 0.5).abs() < 1e-5, "{:?}", emits[0].scale);
    assert_eq!(emits[0].scale[1], 1.0);
    assert_eq!(emits[0].delta, [0.0, -45.0], "中点の移動（190 と 100 の中点 145 ← 280 と 100 の中点 190）");
}

// ─── 離しの速度の頑健化（R1〜R4。2026-09-29。docs/input_gestures.md §5.1）──────────
//
//  時刻ホイール（縦のドラッグ・フリック）を Pixel 6a の dp の倍率で模す。位置は画素（dp × 2.625）、速度の比べは dp/秒。
//  「規則が無い場合」は R2〜R4 を 0 にした閾値（従来の推定）で同じ列を流した値（R1 は従来から有る）。
//  参考の値は同じ手順の Python の試算（リポジトリ外の作業フォルダ tmp/w2_fix1/item1/ の sim_plan.py）で求め、試験でも規則を切って確かめる。

/// Pixel 6a の dp の倍率（420 dpi ÷ 160）。
const PIXEL6A_DP: f32 = 2.625;

/// ホイールの指の横の位置（画素。ノードの内側）。
const WHEEL_X: f32 = 1000.0;

/// ホイールの指の縦の基準の位置（画素。ノードの内側）。
const WHEEL_Y: f32 = 1000.0;

/// 1 フレーム（60 Hz）の秒。受け取りの時刻を模す列のフレームの間隔。
const FRAME_SECS: f64 = 1.0 / 60.0;

/// 120 Hz の走査の標本の間隔（秒）。本当の時刻を模す列。
const SCAN_120HZ_SECS: f64 = 1.0 / 120.0;

/// 本当の時刻を模す列の標本の間隔（8 ms。docs の「8 ms 間隔」）。
const SAMPLE_8MS: f64 = 0.008;

/// 受け取りのまとまりの中の標本の間隔（0.05 ms）。
const BATCH_GAP_SECS: f64 = 0.000_05;

/// 止めた指の揺れ（dp。どれも 1 dp 未満。実機の止めかけの指のフレームの移動 0.2〜0.8 dp より小さい）。
const HOLD_JITTER_DP: [f32; 15] = [0.2, -0.1, 0.1, -0.2, 0.15, -0.05, 0.2, -0.15, 0.05, -0.1, 0.2, -0.2, 0.1, -0.1, 0.15];

/// ホイールのノード（縦のドラッグ・フリック。指の列が収まる大きな矩形）。
fn wheel() -> GestureHitNode {
    node(1, [0.0, 0.0, 2000.0, 4000.0], 0, scroller(GestureDragAxis::Vertical))
}

/// dp の縦の位置を画素へ（基準から下向き）。
fn wheel_y(dy_dp: f32) -> f32 {
    WHEEL_Y + dy_dp * PIXEL6A_DP
}

/// R2〜R4 を切った閾値（規則が無い場合の値を比べる）。
fn without_robust_rules() -> GestureThresholds {
    GestureThresholds {
        velocity_lift_off_ms: 0.0,
        velocity_min_span_ms: 0.0,
        velocity_min_sample_interval_ms: 0.0,
        ..GestureThresholds::default()
    }
}

/// 離した結果（dp/秒）。
#[derive(Debug)]
struct Released {
    /// DragEnd の速度（dp/秒）。
    velocity_dp: [f32; 2],
    /// Fling が出たか。
    fling: bool,
    /// 0 にした理由。
    rule: ReleaseRule,
}

impl Released {
    /// 速さ（dp/秒）。
    fn speed_dp(&self) -> f32 {
        (self.velocity_dp[0] * self.velocity_dp[0] + self.velocity_dp[1] * self.velocity_dp[1]).sqrt()
    }
}

/// ホイールへ指の列（(秒, x, y) 画素。最初が押した位置）を 1 フレームで流し、`up_time` に最後の位置で離す。
fn release_on_wheel(thresholds: GestureThresholds, samples: &[(f64, f32, f32)], up_time: f64) -> Released {
    let mut r = Rig::with_thresholds(vec![wheel()], PIXEL6A_DP, thresholds);
    let (t0, x0, y0) = samples[0];
    let mut entries = vec![down(1, x0, y0, t0)];
    entries.extend(samples[1..].iter().map(|&(t, x, y)| mv(1, x, y, t)));
    let &(_, xl, yl) = samples.last().expect("標本がある");
    entries.push(up(1, xl, yl, up_time));
    let emits = r.frame(&entries, up_time);
    let end = emits.iter().find(|e| e.kind == DragEnd).expect("ドラッグが勝って離した");
    let reports = r.set.take_release_reports();
    assert_eq!(reports.len(), 1, "離した結果を 1 件控える");
    assert_eq!(reports[0].outcome.velocity, end.velocity, "控えの速度は DragEnd と同じ");
    Released {
        velocity_dp: [end.velocity[0] / PIXEL6A_DP, end.velocity[1] / PIXEL6A_DP],
        fling: emits.iter().any(|e| e.kind == Fling),
        rule: reports[0].outcome.rule,
    }
}

/// 払ってから止める列: 8 ms ごとに `flick_dp`（dp/秒）で 144 ms 下へ払い、`hold_ms` の間 8 ms ごとに揺れの標本（1 dp 未満）、
/// 最後の標本の 1 ms 後に離す。戻り値は (標本, 離した時刻)。
fn flick_then_hold(flick_dp: f32, hold_ms: u32) -> (Vec<(f64, f32, f32)>, f64) {
    /// 払いの標本の数（8 ms × 18 = 144 ms）。
    const FLICK_SAMPLES: u32 = 18;
    /// 最後の標本から離すまで（秒）。
    const UP_AFTER: f64 = 0.001;
    let mut samples = vec![(0.0, WHEEL_X, wheel_y(0.0))];
    let (mut t, mut y) = (0.0f64, 0.0f32);
    for _ in 0..FLICK_SAMPLES {
        t += SAMPLE_8MS;
        y += flick_dp * SAMPLE_8MS as f32;
        samples.push((t, WHEEL_X, wheel_y(y)));
    }
    let holds = (f64::from(hold_ms) / f64::from(MILLIS_PER_SECOND) / SAMPLE_8MS).round() as usize;
    for k in 0..holds {
        t += SAMPLE_8MS;
        samples.push((t, WHEEL_X, wheel_y(y + HOLD_JITTER_DP[k % HOLD_JITTER_DP.len()])));
    }
    (samples, t + UP_AFTER)
}

/// 「止めて離す」: 払ってから 40 ms 以上ほぼ止め（1 dp 未満の揺れの標本が 8 ms ごと）、そのまま離す → フリックなし・速度 0。
/// 揺れの標本が続くので R1（最新の標本から 40 ms 以上）は効かず、R2（離す前の区間 [T − 40, T − 16.7] ms でほぼ止まっていた）で 0 になる。
/// 規則が無いと、2 次の当てはめの端の傾きが止める前の払いに引かれて**逆向き**のフリックになる（実機の記録の「逆向き」の離し）:
/// 600 dp/秒の払いで止め 40 ms → −134・48 ms → −230・56 ms → −270・64 ms → −269・80 ms → −144 dp/秒（100 ms 止めると +2。
/// 窓 100 ms から払いが外れる）。値は規則を切ってこの試験の列を流して確かめた（Python の試算とも一致）。
#[test]
fn stop_then_lift_is_not_a_fling() {
    for hold_ms in [40u32, 48, 56, 64, 80, 100] {
        for flick_dp in [300.0f32, 600.0, 1500.0] {
            let (samples, up_time) = flick_then_hold(flick_dp, hold_ms);
            let got = release_on_wheel(GestureThresholds::default(), &samples, up_time);
            assert!(
                !got.fling && got.velocity_dp == [0.0, 0.0] && got.rule == ReleaseRule::LiftOff,
                "止め {hold_ms} ms・払い {flick_dp} dp/秒: {got:?}"
            );
        }
    }
    // 規則が無い場合: 止め 56 ms・払い 600 dp/秒は逆向き（上向き＝負）のフリックになっていた
    let (samples, up_time) = flick_then_hold(600.0, 56);
    let old = release_on_wheel(without_robust_rules(), &samples, up_time);
    assert!(old.fling && old.velocity_dp[1] < -200.0, "規則が無い場合の値: {old:?}");
}

/// 止めた後に最後の標本だけ飛ぶ列（dp の飛びは上向き）。`receive_time` なら受け取りの時刻を模す（フレームごとに 1 標本・
/// 離したフレームは止めた位置の標本と、その 0.05 ms 後の飛びの標本・その直後に離す）。そうでなければ本当の時刻（8 ms ごと・
/// 飛びは前の標本の 8 ms 後・離しはその 0.5 ms 後）。払いはどちらも 300 dp/秒で 200 ms、止めはフレーム 4 回分（約 67 ms）。
fn hold_then_jump(jump_dp: f32, receive_time: bool) -> (Vec<(f64, f32, f32)>, f64) {
    /// 受け取りの時刻の列: 払いのフレームの数と 1 フレームの移動（dp）。
    const DRAG_FRAMES: usize = 12;
    const DRAG_STEP_FRAME_DP: f32 = 5.0;
    /// 受け取りの時刻の列: 止めのフレームの数。
    const HOLD_FRAMES: usize = 4;
    /// 本当の時刻の列: 払いの標本の数と 1 標本の移動（dp。8 ms で 2.4 dp = 300 dp/秒）。
    const DRAG_SAMPLES: usize = 24;
    const DRAG_STEP_SAMPLE_DP: f32 = 2.4;
    /// 本当の時刻の列: 止めの標本の数（8 ms × 8 = 64 ms）。
    const HOLD_SAMPLES: usize = 8;
    /// 飛びの標本から離すまで（秒）: 受け取りの時刻の列はすぐ（0.01 ms）、本当の時刻の列は 0.5 ms。
    const UP_AFTER_BATCH: f64 = 0.000_01;
    const UP_AFTER_SCAN: f64 = 0.0005;
    let mut samples = vec![(0.0, WHEEL_X, wheel_y(0.0))];
    let (mut t, mut y) = (0.0f64, 0.0f32);
    if receive_time {
        for _ in 0..DRAG_FRAMES {
            t += FRAME_SECS;
            y += DRAG_STEP_FRAME_DP;
            samples.push((t, WHEEL_X, wheel_y(y)));
        }
        for jitter in HOLD_JITTER_DP.iter().take(HOLD_FRAMES) {
            t += FRAME_SECS;
            samples.push((t, WHEEL_X, wheel_y(y + jitter)));
        }
        t += FRAME_SECS;
        samples.push((t, WHEEL_X, wheel_y(y)));
        t += BATCH_GAP_SECS;
        samples.push((t, WHEEL_X, wheel_y(y - jump_dp)));
        (samples, t + UP_AFTER_BATCH)
    } else {
        for _ in 0..DRAG_SAMPLES {
            t += SAMPLE_8MS;
            y += DRAG_STEP_SAMPLE_DP;
            samples.push((t, WHEEL_X, wheel_y(y)));
        }
        for jitter in HOLD_JITTER_DP.iter().take(HOLD_SAMPLES) {
            t += SAMPLE_8MS;
            samples.push((t, WHEEL_X, wheel_y(y + jitter)));
        }
        t += SAMPLE_8MS;
        samples.push((t, WHEEL_X, wheel_y(y - jump_dp)));
        (samples, t + UP_AFTER_SCAN)
    }
}

/// 「離す瞬間の小さなずれ」: 止めた後、最後の標本だけ 1.5・2.3・4.2 dp 飛ぶ（実機の記録の飛び）。受け取りの時刻を模した間隔でも、
/// 本当の時刻（8 ms 間隔）でも速度 0・フリックなし（R2）。
/// 規則が無い場合（規則を切ってこの試験の列を流した値。dp/秒）: 受け取りの時刻 −30・−45・−82、本当の時刻 −142・−158・−197
/// （受け取りの時刻の 4.2 dp と、本当の時刻の 3 つはフリックになる）。
#[test]
fn small_jump_at_release_is_not_a_fling() {
    for jump_dp in [1.5f32, 2.3, 4.2] {
        for receive_time in [true, false] {
            let (samples, up_time) = hold_then_jump(jump_dp, receive_time);
            let got = release_on_wheel(GestureThresholds::default(), &samples, up_time);
            assert!(
                !got.fling && got.velocity_dp == [0.0, 0.0] && got.rule == ReleaseRule::LiftOff,
                "飛び {jump_dp} dp・受け取りの時刻 {receive_time}: {got:?}"
            );
        }
    }
    let (samples, up_time) = hold_then_jump(4.2, false);
    let old = release_on_wheel(without_robust_rules(), &samples, up_time);
    assert!(old.fling && old.velocity_dp[1] < -150.0, "規則が無い場合（本当の時刻・飛び 4.2 dp）: {old:?}");
}

/// 実機の記録（2026-09-29 の Pixel 6a。tmp/w2_feel/logcat_full.txt の `[SEED TOUCH FRAME]`）の、指をほぼ止めてから離して最速に近い
/// フリックになった 3 回（docs/app_platform_roadmap.md §3.9.2 の (c)・r3_release_jitter.txt の +109・+98・+109 分）の、離す前の
/// フレームの時刻（秒）と位置（画素）と、離した位置。
const REPLAY_ROWS: [(&[(f64, f32, f32)], (f64, f32, f32)); 3] = [
    // 1 回目: 最後の 3 回の移動 +0.6 +0.8 −0.2 dp、離した瞬間の飛び (+2.7, −4.2) dp
    (
        &[
            (0.144, 365.0, 940.3), (0.161, 365.0, 944.4), (0.176, 365.0, 947.5), (0.217, 365.0, 949.5), (0.225, 365.0, 951.0),
            (0.242, 365.0, 952.9), (0.259, 365.0, 954.0), (0.277, 365.0, 955.5), (0.293, 365.0, 956.0), (0.310, 365.0, 957.5),
            (0.343, 365.0, 958.0), (0.359, 365.0, 959.5), (0.394, 365.0, 961.5), (0.443, 366.0, 961.0),
        ],
        (0.451, 373.0, 950.0),
    ),
    // 2 回目: +0.8 +0.4 +0.2 dp、(0.0, −2.3) dp
    (
        &[
            (0.609, 347.0, 924.6), (0.626, 347.0, 933.0), (0.642, 347.0, 936.1), (0.659, 347.0, 942.4), (0.677, 347.0, 948.6),
            (0.692, 347.0, 960.7), (0.710, 345.5, 969.0), (0.726, 346.0, 977.0), (0.744, 346.0, 981.7), (0.760, 346.0, 987.7),
            (0.777, 346.0, 992.9), (0.794, 346.0, 999.1), (0.810, 346.0, 1003.2), (0.827, 347.0, 1006.3), (0.843, 347.0, 1007.5),
            (0.860, 349.5, 1009.5), (0.877, 349.0, 1010.5), (0.893, 349.0, 1011.0),
        ],
        (0.934, 349.0, 1005.0),
    ),
    // 3 回目: −0.2 −0.6 −0.2 dp、(0.0, −1.5) dp
    (
        &[
            (0.802, 379.0, 862.5), (0.820, 380.5, 862.0), (0.837, 380.0, 860.2), (0.852, 380.0, 857.2), (0.869, 380.0, 855.6),
            (0.885, 380.0, 853.0), (0.901, 380.0, 849.5), (0.918, 381.2, 849.0), (0.952, 383.5, 847.5), (0.968, 383.0, 846.5),
            (0.986, 383.0, 845.5), (1.021, 384.5, 843.5), (1.038, 384.0, 843.0), (1.053, 384.0, 841.5), (1.103, 384.0, 841.0),
        ],
        (1.126, 384.0, 837.0),
    ),
];

/// 実機の記録の列を指の列にする。押した位置は最初のフレームの 30 画素上・0.2 秒前（ドラッグが勝つ）。
/// `receive_time` なら受け取りの時刻を模す（離す直前に、最後のフレームの位置と飛びの位置が 0.05 ms 差で並ぶ＝記録の Ended の
/// フレームの中のまとまり）。そうでなければ本当の時刻（飛びの標本は離す 0.5 ms 前）。
fn replay_row(frames: &[(f64, f32, f32)], end: (f64, f32, f32), receive_time: bool) -> (Vec<(f64, f32, f32)>, f64) {
    /// 押してから最初のフレームまで（秒）と、押した位置の上へのずれ（画素）。
    const PRESS_LEAD_SECS: f64 = 0.2;
    const PRESS_ABOVE_PX: f32 = 30.0;
    /// まとまりの最後の標本から離すまで（秒）と、本当の時刻の飛びから離すまで（秒）。
    const UP_AFTER_BATCH: f64 = 0.000_01;
    const UP_AFTER_SCAN: f64 = 0.0005;
    let t0 = frames[0].0 - PRESS_LEAD_SECS;
    let mut samples = vec![(0.0, frames[0].1, frames[0].2 - PRESS_ABOVE_PX)];
    samples.extend(frames.iter().map(|&(t, x, y)| (t - t0, x, y)));
    let (up_time, ex, ey) = (end.0 - t0, end.1, end.2);
    let &(_, lx, ly) = frames.last().expect("フレームがある");
    if receive_time {
        samples.push((up_time - UP_AFTER_BATCH - BATCH_GAP_SECS, lx, ly));
        samples.push((up_time - UP_AFTER_BATCH, ex, ey));
    } else {
        samples.push((up_time - UP_AFTER_SCAN, ex, ey));
    }
    (samples, up_time)
}

/// 実機の記録の 3 回を再生: 受け取りの時刻を模した列でも本当の時刻の列でも、速度 0・フリックなし。
/// 規則が無い場合（規則を切ってこの試験の列を流した縦の速さ。dp/秒）: 受け取りの時刻の列は 3 回とも上限の 8000（実機では
/// 5,800〜7,700 で流れた）、本当の時刻の列は 559・0・68（1 回目と 3 回目はフリック）。1・2 回目は、離す前に 49 ms・41 ms
/// 標本の無い間があり（40 ms を超える間で窓が切れる）、窓が飛びの標本と高々その前の 1 標本だけになる（R3: 幅 0〜8 ms で 0。
/// まとまりの 2 標本は R4 で 1 つ）。3 回目は R2 の区間の標本が 1 つで、その前の標本から 50 ms 空いた 0.5 画素の動き
/// （R2 がその時点までの新しい 2 標本で約 4 dp/秒と測って 0）。
#[test]
fn replay_of_recorded_releases() {
    for (row, (frames, end)) in REPLAY_ROWS.iter().enumerate() {
        for receive_time in [true, false] {
            let (samples, up_time) = replay_row(frames, *end, receive_time);
            let got = release_on_wheel(GestureThresholds::default(), &samples, up_time);
            assert!(
                !got.fling && got.velocity_dp == [0.0, 0.0],
                "{} 回目・受け取りの時刻 {receive_time}: {got:?}",
                row + 1
            );
            let old = release_on_wheel(without_robust_rules(), &samples, up_time);
            if receive_time {
                assert!(old.fling && old.speed_dp() > 7000.0, "{} 回目の規則が無い場合: {old:?}", row + 1);
            }
        }
    }
}

/// 試験用の決まった乱数（線形合同法。係数は Numerical Recipes の 32 ビットの組。種を変えて同じ列を再現できる）。
struct Lcg(u32);

impl Lcg {
    /// 掛ける数。
    const MUL: u32 = 1_664_525;
    /// 足す数。
    const INC: u32 = 1_013_904_223;

    /// 0〜1 の一様な値。
    fn unit(&mut self) -> f64 {
        self.0 = self.0.wrapping_mul(Self::MUL).wrapping_add(Self::INC);
        f64::from(self.0) / f64::from(u32::MAX)
    }

    /// −1〜1 の一様な値（0〜1 を 2 倍して 1 を引く）。
    fn signed_unit(&mut self) -> f64 {
        self.unit().mul_add(2.0, -1.0)
    }
}

/// 「時刻の間隔の揺れ」: 一定の 1000 dp/秒で 100 ms 払い（120 Hz の走査）、記録の時刻に ±3 ms の揺れと受け取りのまとまり
/// （2〜3 標本が 0.02 ms 間隔。30% の確率）を混ぜ、同じ指の時刻は逆行させない（Input の time_floor.rs と同じ）。
/// 推定は真の速度の ±30% に入り、フリックと判定される。
/// 範囲の根拠: ±3 ms は標本の間隔 8.3 ms の ±36%。2 次の当てはめの端の傾きは時刻の揺れを増幅するので、同じ手順の Python の試算
/// （リポジトリ外の作業フォルダ tmp/w2_fix1/item1/ の sim3.py・sim_v7.py）で 2 万通りの誤差が −27%〜+27%、5 千通りで −24%〜+25% だった。
/// それを覆う ±30% とする。
#[test]
fn time_jitter_keeps_the_estimate_near_the_true_velocity() {
    /// 真の速さ（dp/秒）。
    const TRUE_SPEED_DP: f32 = 1000.0;
    /// 走査の標本の数（0〜100 ms）。
    const SCAN_SAMPLES: usize = 13;
    /// 時刻の揺れの幅（秒。±3 ms）。
    const JITTER_SECS: f64 = 0.003;
    /// まとまりになる確率と、まとまりの中の間隔（秒）。まとまりは 2 標本か 3 標本（半々）。
    const BATCH_PROBABILITY: f64 = 0.3;
    const BATCH_STEP_SECS: f64 = 0.000_02;
    const SMALL_BATCH: usize = 2;
    const LARGE_BATCH_PROBABILITY: f64 = 0.5;
    /// 許す誤差（真の速さに対する割合）。
    const TOLERANCE: f32 = 0.3;
    /// 試す列の数と乱数の種。
    const TRIALS: u32 = 300;
    const SEED: u32 = 20_260_929;
    /// 最後の標本から離すまで（秒）。
    const UP_AFTER: f64 = 0.0005;
    let mut rng = Lcg(SEED);
    for trial in 0..TRIALS {
        let mut samples: Vec<(f64, f32, f32)> = Vec::new();
        let mut i = 0;
        while i < SCAN_SAMPLES {
            // まとまりの大きさ（最初の押した標本は 1 つ）。まとまりの受け取りの時刻は最後の標本の時刻 ± 揺れ
            let size = if i > 0 && rng.unit() < BATCH_PROBABILITY {
                SMALL_BATCH + usize::from(rng.unit() < LARGE_BATCH_PROBABILITY)
            } else {
                1
            };
            let group: Vec<usize> = (i..(i + size).min(SCAN_SAMPLES)).collect();
            let last = *group.last().expect("1 つ以上");
            let base = last as f64 * SCAN_120HZ_SECS + rng.signed_unit() * JITTER_SECS;
            for (j, &k) in group.iter().enumerate() {
                let floor = samples.last().map_or(f64::NEG_INFINITY, |s| s.0);
                let t = (base + j as f64 * BATCH_STEP_SECS).max(floor);
                samples.push((t, WHEEL_X, wheel_y(TRUE_SPEED_DP * (k as f64 * SCAN_120HZ_SECS) as f32)));
            }
            i += size;
        }
        let up_time = samples.last().expect("標本がある").0 + UP_AFTER;
        let got = release_on_wheel(GestureThresholds::default(), &samples, up_time);
        let error = got.speed_dp() / TRUE_SPEED_DP - 1.0;
        assert!(got.fling && error.abs() <= TOLERANCE, "試行 {trial}: 誤差 {error:.3} {got:?}");
    }
}

/// 本物の速いフリック（3,000 dp/秒・120 Hz の標本。等速と、0 から加速）: R2・R3 で消えず、速度はほぼ真の値（±2%）。
/// 払いの長さは 33〜120 ms（120 Hz で 4〜14 標本）。R2 の区間でも指は速く、R3 の幅も 1 フレーム以上ある。
#[test]
fn real_fast_flick_survives_the_rules() {
    /// 真の終わりの速さ（dp/秒）。
    const END_SPEED_DP: f32 = 3000.0;
    /// 許す誤差（割合）。
    const TOLERANCE: f32 = 0.02;
    /// 最後の標本から離すまで（秒）。
    const UP_AFTER: f64 = 0.001;
    for duration_ms in [33u32, 50, 80, 120] {
        for accelerate in [false, true] {
            let steps = (f64::from(duration_ms) / f64::from(MILLIS_PER_SECOND) / SCAN_120HZ_SECS).round() as u32;
            let total = f64::from(steps) * SCAN_120HZ_SECS;
            let mut samples = vec![(0.0, WHEEL_X, wheel_y(0.0))];
            for i in 1..=steps {
                let t = f64::from(i) * SCAN_120HZ_SECS;
                // 等速: y = v t。加速: y = v t² / (2 T)（終わりの速さが v）
                let y = if accelerate { f64::from(END_SPEED_DP) * t * t / (2.0 * total) } else { f64::from(END_SPEED_DP) * t };
                samples.push((t, WHEEL_X, wheel_y(y as f32)));
            }
            let got = release_on_wheel(GestureThresholds::default(), &samples, total + UP_AFTER);
            assert!(got.fling && got.rule == ReleaseRule::Ok, "{duration_ms} ms・加速 {accelerate}: {got:?}");
            assert!((got.speed_dp() / END_SPEED_DP - 1.0).abs() <= TOLERANCE, "{duration_ms} ms・加速 {accelerate}: {got:?}");
        }
    }
}
