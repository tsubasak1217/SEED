// ============================================================
//  canvas_scroll/tests.rs — スクロールの物理・段階・入れ子・イベントの単体テスト（W2-3）
//
//  値の根拠は constants.rs の表（Flutter・Android）。期待値は同じ式を別に書いて求める（Android の
//  getSplineFlingDistance の形で Clamp の距離を検算する等）。レイアウト（中身をずらす・見える範囲の外を飛ばす）の
//  テストは canvas_layout/scroll_tests.rs、当たり判定との組み合わせは app/gesture_scene.rs。
// ============================================================

use crate::engine::components::{CanvasScrollComponent, ScrollEdge, ScrollSnap};
use crate::engine::ecs::Entity;

use super::ballistic::{create_ballistic, page_target, AxisPhysics};
use super::constants::*;
use super::controller::{advance, apply_request, begin_drag, hold, on_range_changed, release};
use super::events::{collect_events, ScrollEventKind};
use super::nesting::{effective_len, fling_receiver, route_drag, ChainLink};
use super::overscroll::{apply_user_delta, overscroll_after_pull, overscroll_after_push, AxisRange};
use super::physics::{cubic_ease, BouncingSim, ClampingSim, FrictionSim, ScrollSim, SpringSim, UnitScale};
use super::state::{CanvasScrollState, ScrollMetrics, ScrollPhase, ScrollRequest};

/// 1 フレームの時間（60 Hz）。
const FRAME: f64 = 1.0 / 60.0;
/// 比べる許容（相対）。
const REL_TOL: f64 = 1e-9;

/// 相対誤差で等しい。
fn close(a: f64, b: f64, tol: f64) -> bool {
    (a - b).abs() <= tol * a.abs().max(b.abs()).max(1.0)
}

/// 既定の物理（Bounce・慣性あり・スナップなし）。
fn physics(edge: ScrollEdge) -> AxisPhysics {
    AxisPhysics {
        edge,
        inertia: true,
        snap: ScrollSnap::None,
        snap_interval: None,
        fling_friction: f64::from(crate::engine::components::canvas_scroll_component::DEFAULT_FLING_FRICTION),
        bounce_drag: f64::from(crate::engine::components::canvas_scroll_component::DEFAULT_BOUNCE_DRAG),
    }
}

/// シミュレーションを 60 Hz で終わるまで進め、(終わりの位置, 時間, 途中の最大・最小) を返す。
fn run(sim: &ScrollSim) -> (f64, f64, f64, f64) {
    /// 打ち切りの時間（秒。どの動きもこれより前に終わる）。
    const LIMIT: f64 = 30.0;
    let mut t = 0.0;
    let (mut lo, mut hi) = (f64::INFINITY, f64::NEG_INFINITY);
    while t < LIMIT {
        let x = sim.x(t);
        lo = lo.min(x);
        hi = hi.max(x);
        if sim.is_done(t) {
            return (x, t, lo, hi);
        }
        t += FRAME;
    }
    (sim.x(t), t, lo, hi)
}

// ─── 慣性（Clamp）───

/// Clamp の慣性の距離は Android の getSplineFlingDistance と一致し、時間・速度は Flutter の式のとおり。
#[test]
fn clamping_fling_matches_android_spline_distance() {
    let rate = clamping_deceleration_rate();
    assert!(close(rate, 0.78f64.ln() / 0.9f64.ln(), REL_TOL));
    let coeff = GRAVITY_EARTH * INCHES_PER_METER * DP_PER_INCH * PHYSICAL_COEFF_TUNING;
    let friction = 0.015;
    for &v in &[200.0, 1000.0, 4000.0, -2500.0] {
        let sim = ClampingSim::new(0.0, v, friction, UnitScale::IDENTITY);
        // Android: l = ln(INFLEXION·|v| / (friction·coeff))、距離 = friction·coeff·exp(rate/(rate−1)·l)
        let l = (CLAMPING_INFLEXION * f64::abs(v) / (friction * coeff)).ln();
        let android_distance = friction * coeff * (rate / (rate - 1.0) * l).exp();
        assert!(close(sim.distance().abs(), android_distance, 1e-9), "v={v}: {} vs {}", sim.distance(), android_distance);
        assert_eq!(sim.distance().signum(), f64::signum(v));
        // Flutter: 時間 = rate·INFLEXION·(|v| / 基準)^(1/(rate−1))、基準 = friction·coeff / INFLEXION
        let reference = friction * coeff / CLAMPING_INFLEXION;
        let duration = rate * CLAMPING_INFLEXION * (f64::abs(v) / reference).powf(1.0 / (rate - 1.0));
        assert!(close(sim.duration(), duration, REL_TOL));
        // 始めの速度は v、終わりで 0、終わりの位置は距離の分
        assert!(close(sim.dx(0.0), v, REL_TOL));
        assert!(sim.dx(sim.duration()).abs() < 1e-9);
        assert!(close(sim.x(sim.duration()), sim.distance(), REL_TOL));
    }
    // 1000 dp/s の値（docs/ui_scroll_list.md §3 の例）: 194.3 dp・0.458 秒
    let sim = ClampingSim::new(0.0, 1000.0, friction, UnitScale::IDENTITY);
    assert!((sim.distance() - 194.31).abs() < 0.01, "{}", sim.distance());
    assert!((sim.duration() - 0.4582).abs() < 0.0005, "{}", sim.duration());
}

/// 単位が dp でも画素でも、同じ指の速さなら同じ時間で同じ見た目の距離だけ進む（dp_scale と 1 単位の画素数の換算）。
#[test]
fn clamping_fling_is_unit_invariant() {
    let dp_scale = 2.625;
    let v_dp = 1500.0;
    let in_dp = ClampingSim::new(0.0, v_dp, 0.015, UnitScale::new(dp_scale, dp_scale));
    let in_px = ClampingSim::new(0.0, v_dp * dp_scale, 0.015, UnitScale::new(1.0, dp_scale));
    assert!(close(in_dp.duration(), in_px.duration(), 1e-12));
    assert!(close(in_dp.distance() * dp_scale, in_px.distance(), 1e-12));
    // 1 dp = 1 px の端末より、同じ dp/s でも同じ dp だけ進む（dp の手触りが端末に依らない）
    let base = ClampingSim::new(0.0, v_dp, 0.015, UnitScale::IDENTITY);
    assert!(close(base.distance(), in_dp.distance(), 1e-12));
}

/// 摩擦を大きくすると早く・短く止まる（データで手触りを変えられる）。
#[test]
fn larger_friction_stops_sooner() {
    let soft = ClampingSim::new(0.0, 1000.0, 0.015, UnitScale::IDENTITY);
    let hard = ClampingSim::new(0.0, 1000.0, 0.03, UnitScale::IDENTITY);
    assert!(hard.distance() < soft.distance() && hard.duration() < soft.duration());
}

// ─── 減衰・ばね・跳ね返り ───

/// 指数の減衰（drag 0.135）: 止まる位置・通る時刻・through が目標でちょうど終わる。
#[test]
fn friction_simulation_final_position_and_through() {
    let sim = FrictionSim::new(0.135, 10.0, 500.0, 20.0);
    let final_x = 10.0 - 500.0 / 0.135f64.ln();
    assert!(close(sim.final_x(), final_x, REL_TOL));
    let t = sim.time_at_x(100.0);
    assert!(t.is_finite() && (sim.x(t) - 100.0).abs() < 1e-9);
    assert!(sim.time_at_x(final_x + 1.0).is_infinite(), "止まる位置の先は通らない");
    // through: 0 → 250 を 900/s で始め、許容 20/s で終わる → 終わりの位置はちょうど 250
    let through = FrictionSim::through(0.0, 250.0, 900.0, 20.0);
    let (end, time, lo, hi) = run(&ScrollSim::Friction(through));
    assert_eq!(end, 250.0);
    assert!(lo >= 0.0 && hi <= 250.0 + 1e-9, "行き過ぎない: {lo}..{hi}");
    assert!(time > 0.0 && time < 5.0);
}

/// ばね（質量 0.5・硬さ 100・減衰比 1.1＝過減衰）: 行き過ぎずに終わりの位置へ着く。始めの位置・速度を守る。
#[test]
fn default_spring_is_overdamped_and_settles() {
    let scale = UnitScale::IDENTITY;
    let spring = SpringSim::new(100.0, 0.0, 0.0, scale);
    assert!(close(spring.x(0.0), 100.0, REL_TOL));
    assert!(spring.dx(0.0).abs() < 1e-9);
    let (end, time, lo, _) = run(&ScrollSim::Spring(spring));
    assert_eq!(end, 0.0);
    assert!(lo >= 0.0, "過減衰なので 0 を越えない: {lo}");
    assert!(time < 1.5, "1.5 秒以内に止まる: {time}");
    // 速度を持って始める（跳ね返りの乗り換え）: 始めの速度が効く
    let with_velocity = SpringSim::new(0.0, 0.0, 800.0, scale);
    assert!(close(with_velocity.dx(0.0), 800.0, 1e-9));
    let (end, _, _, hi) = run(&ScrollSim::Spring(with_velocity));
    assert_eq!(end, 0.0);
    assert!(hi > 0.0, "速度の分だけ外へ出てから戻る: {hi}");
}

/// 跳ね返り: 端を越える慣性はばねへ乗り換えて端へ戻る。越えない慣性はばね無し。範囲の外から始めるとばねだけ。
#[test]
fn bouncing_simulation_overshoots_and_returns() {
    let scale = UnitScale::IDENTITY;
    // 範囲 0..1000 の 900 から +3000/s: 減衰だけなら 900 + 3000/2.0 ≈ 2399 まで行くので端（1000）でばねへ
    let sim = BouncingSim::new(900.0, 3000.0, 0.0, 1000.0, 0.135, scale);
    assert!(sim.bounces());
    let (end, _, _, hi) = run(&ScrollSim::Bouncing(sim));
    assert_eq!(end, 1000.0, "端へ戻る");
    assert!(hi > 1000.0, "いったん端を越える（跳ね返る）: {hi}");
    // 越えない慣性（100 から +100/s）: 速度が許容（20/s）を下回った所で終わる（Flutter の FrictionSimulation.isDone。
    // x = x₀ + (許容 − v₀) / ln drag ≈ 139.95。止まる極限 150 までは行かない）
    let calm = BouncingSim::new(100.0, 100.0, 0.0, 1000.0, 0.135, scale);
    assert!(!calm.bounces());
    let (end, _, _, _) = run(&ScrollSim::Bouncing(calm));
    let at_tolerance = 100.0 + (TOLERANCE_VELOCITY_PX - 100.0) / 0.135f64.ln();
    assert!((end - at_tolerance).abs() < 1.0, "{end} vs {at_tolerance}");
    // 範囲の外（−80 = 上へ引っぱったまま離した）から始める
    let back = BouncingSim::new(-80.0, 0.0, 0.0, 1000.0, 0.135, scale);
    let (end, _, lo, _) = run(&ScrollSim::Bouncing(back));
    assert_eq!(end, 0.0);
    assert!(lo >= -80.0 - 1e-9);
}

// ─── 指のドラッグ（範囲・端の外の摩擦）───

/// Bounce: 端の外へ引く量は摩擦 0.52·(1−x/V)² の閉じた式。フレームの刻みに依らない。戻すと端で 1:1 に戻る。
#[test]
fn overscroll_pull_follows_friction_curve_and_is_frame_independent() {
    let viewport = 500.0;
    // 端から 100 引く: x = V(1 − 1/(1 + 0.52·100/V))
    let one_step = overscroll_after_pull(0.0, 100.0, viewport);
    let expected = viewport * (1.0 - 1.0 / (1.0 + BOUNCE_OVERSCROLL_FRICTION * 100.0 / viewport));
    assert!(close(one_step, expected, 1e-12));
    // 10 回に分けても同じ
    let mut x = 0.0;
    for _ in 0..10 {
        x = overscroll_after_pull(x, 10.0, viewport);
    }
    assert!(close(x, one_step, 1e-9), "{x} vs {one_step}");
    // 始めの傾きは 0.52（小さく引くと 0.52 倍）
    assert!(close(overscroll_after_pull(0.0, 0.01, viewport) / 0.01, BOUNCE_OVERSCROLL_FRICTION, 1e-4));
    // どれだけ引いても窓の長さを越えない
    assert!(overscroll_after_pull(0.0, 1e9, viewport) < viewport);
    // 戻す: 戻り切るのに要る量で 0 になり、残りを返す
    let (after, used) = overscroll_after_push(one_step, 1000.0, viewport);
    assert_eq!(after, 0.0);
    assert!(close(used, 100.0, 1e-9), "引いた量と同じだけ戻せば端へ戻る: {used}");
}

/// ドラッグを位置へ当てる: 範囲の中は 1:1、Clamp は端で止まり残りを返す、Bounce は外へ出る（使い切る）。
#[test]
fn user_delta_clamp_and_bounce() {
    let range = AxisRange::new(0.0, 1000.0, 500.0);
    // 範囲の中
    assert_eq!(apply_user_delta(100.0, 50.0, range, true, true), (150.0, 50.0));
    // Clamp: 端で止まり、使ったのは端までの 30
    assert_eq!(apply_user_delta(970.0, 100.0, range, false, true), (1000.0, 30.0));
    // Bounce で外へ出てよい: 端までは 1:1、残り 70 は摩擦つき
    let (p, used) = apply_user_delta(970.0, 100.0, range, true, true);
    assert!(close(p, 1000.0 + overscroll_after_pull(0.0, 70.0, 500.0), 1e-12));
    assert_eq!(used, 100.0);
    // Bounce でも外へ出てはいけない段（入れ子の途中）は端で止まる
    assert_eq!(apply_user_delta(970.0, 100.0, range, true, false), (1000.0, 30.0));
    // はみ出したところから戻る向き: 摩擦つきで端まで戻り、残りは範囲の中で 1:1
    let over = overscroll_after_pull(0.0, 100.0, 500.0);
    let (p, used) = apply_user_delta(-over, 150.0, range, true, false);
    assert!(close(p, 50.0, 1e-9), "{p}");
    assert!(close(used, 150.0, 1e-9));
}

// ─── 離した後の動きの選び方 ───

/// Clamp: 小さな速度・端でさらに外向きは動かない。範囲の外（中身が縮んだ）はばねで端へ。
#[test]
fn clamp_ballistic_selection() {
    let range = AxisRange::new(0.0, 1000.0, 500.0);
    let p = physics(ScrollEdge::Clamp);
    let s = UnitScale::IDENTITY;
    assert!(create_ballistic(500.0, 10.0, range, &p, s).is_none(), "許容（20 px/s）より遅い");
    assert!(create_ballistic(1000.0, 800.0, range, &p, s).is_none(), "端でさらに外向き");
    assert!(matches!(create_ballistic(500.0, 800.0, range, &p, s), Some(ScrollSim::Clamping(_))));
    assert!(matches!(create_ballistic(1200.0, 0.0, range, &p, s), Some(ScrollSim::Spring(_))));
    // 慣性を切ると速度 0 として選ぶ
    let no_inertia = AxisPhysics { inertia: false, ..p };
    assert!(create_ballistic(500.0, 800.0, range, &no_inertia, s).is_none());
}

/// Clamp の慣性は端に着いたら止まる（行き過ぎない）。
#[test]
fn clamp_fling_stops_at_edge() {
    let settings = CanvasScrollComponent { edge: ScrollEdge::Clamp, ..CanvasScrollComponent::default() };
    let mut state = state_with(400.0, 100.0);
    state.position[1] = 250.0;
    release(&settings, &mut state, [0.0, 3000.0]);
    assert_eq!(state.phase, ScrollPhase::Ballistic);
    let mut max_seen: f64 = 0.0;
    for _ in 0..600 {
        advance(&settings, &mut state, FRAME);
        max_seen = max_seen.max(state.position[1]);
        if state.phase == ScrollPhase::Idle {
            break;
        }
    }
    assert_eq!(state.phase, ScrollPhase::Idle);
    assert_eq!(state.position[1], 300.0, "最大（400 − 100）で止まる");
    assert!(max_seen <= 300.0);
}

/// ページのスナップ（PageScrollPhysics）: フリックの向きへ最大 1 ページ・止まっていれば最寄りのページ。
#[test]
fn page_snap_targets() {
    let tol = TOLERANCE_VELOCITY_PX;
    assert_eq!(page_target(130.0, 500.0, 100.0, tol), 200.0);
    assert_eq!(page_target(130.0, -500.0, 100.0, tol), 100.0);
    assert_eq!(page_target(130.0, 0.0, 100.0, tol), 100.0);
    assert_eq!(page_target(160.0, 0.0, 100.0, tol), 200.0);
    // 速いフリックでも 1 ページだけ（130 → 200。300 へは行かない）
    let range = AxisRange::new(0.0, 1000.0, 100.0);
    let p = AxisPhysics { snap: ScrollSnap::Page, ..physics(ScrollEdge::Bounce) };
    let sim = create_ballistic(130.0, 8000.0, range, &p, UnitScale::IDENTITY).unwrap();
    let (end, _, _, _) = run(&sim);
    assert_eq!(end, 200.0);
}

/// 間隔のスナップ: 慣性で止まる位置に最も近い倍数でちょうど止まる（向きに十分な速度なら減衰、遅ければばね）。
#[test]
fn interval_snap_lands_exactly_on_a_multiple() {
    let range = AxisRange::new(0.0, 10_000.0, 300.0);
    let p = AxisPhysics { snap: ScrollSnap::Interval, snap_interval: Some(48.0), ..physics(ScrollEdge::Bounce) };
    for &v in &[600.0, 1500.0, -900.0] {
        let start = 1000.0;
        let natural = start - v / 0.135f64.ln();
        let expected = (natural / 48.0).round() * 48.0;
        let sim = create_ballistic(start, v, range, &p, UnitScale::IDENTITY).unwrap();
        assert!(matches!(sim, ScrollSim::Friction(_)), "向きに速度があれば減衰で止める");
        let (end, _, _, _) = run(&sim);
        assert_eq!(end, expected, "v={v}");
    }
    // 止まっていて倍数から外れている（1030）→ ばねで 1008（=21×48）へ
    let sim = create_ballistic(1030.0, 0.0, range, &p, UnitScale::IDENTITY).unwrap();
    assert!(matches!(sim, ScrollSim::Spring(_)));
    assert_eq!(run(&sim).0, 1008.0);
}

// ─── 段階（ドラッグ・離す・触れて止める・ScrollTo・大きさの変化）───

/// 窓 `viewport`・中身 `content`（縦）の大きさの分かった状態。
fn state_with(content: f64, viewport: f64) -> CanvasScrollState {
    CanvasScrollState {
        metrics: Some(ScrollMetrics {
            viewport: [300.0, viewport],
            content: [300.0, content],
            px_per_unit: [1.0, 1.0],
            dp_scale: 1.0,
            axis_dirs: [[1.0, 0.0], [0.0, 1.0]],
        }),
        ..CanvasScrollState::default()
    }
}

/// フリック → 慣性で動いて止まる。途中で触れると止まり（Held）、指を離すと速度 0 で離す。
#[test]
fn fling_hold_and_release_phases() {
    let settings = CanvasScrollComponent::default();
    let mut state = state_with(5000.0, 500.0);
    begin_drag(&mut state, 0);
    assert_eq!(state.phase, ScrollPhase::Dragging);
    release(&settings, &mut state, [0.0, 2000.0]);
    assert_eq!(state.phase, ScrollPhase::Ballistic);
    for _ in 0..10 {
        advance(&settings, &mut state, FRAME);
    }
    let moved = state.position[1];
    assert!(moved > 0.0);
    assert!(hold(&mut state));
    assert_eq!(state.phase, ScrollPhase::Held);
    advance(&settings, &mut state, FRAME);
    assert_eq!(state.position[1], moved, "触れて止めたら動かない");
    release(&settings, &mut state, [0.0; 2]);
    assert_eq!(state.phase, ScrollPhase::Idle, "範囲の中で速度 0 なら止まる");
    // 止まっている窓は触れても Held にならない
    assert!(!hold(&mut state));
}

/// ScrollTo: 時間どおりに目標へ着き、途中は Curves.easeInOut（半分の時間で半分の距離）。範囲へ収める。
#[test]
fn scroll_to_animates_with_ease_in_out() {
    let settings = CanvasScrollComponent::default();
    let mut state = state_with(2000.0, 500.0);
    state.pending = Some(ScrollRequest::Animate { target: [0.0, 1000.0], duration: 0.4 });
    apply_request(&settings, &mut state);
    assert_eq!(state.phase, ScrollPhase::Animating);
    advance(&settings, &mut state, 0.2);
    assert!((state.position[1] - 500.0).abs() < 5.0, "対称な曲線なので半分: {}", state.position[1]);
    advance(&settings, &mut state, 0.2);
    assert_eq!(state.position[1], 1000.0);
    assert_eq!(state.phase, ScrollPhase::Idle);
    // 範囲の外の目標は最大（1500）へ収める
    state.pending = Some(ScrollRequest::Animate { target: [0.0, 9999.0], duration: 0.1 });
    apply_request(&settings, &mut state);
    advance(&settings, &mut state, 0.2);
    assert_eq!(state.position[1], 1500.0);
    // すぐ移す
    state.pending = Some(ScrollRequest::Jump([0.0, 20.0]));
    apply_request(&settings, &mut state);
    assert_eq!((state.position[1], state.phase), (20.0, ScrollPhase::Idle));
    // イージングの端と対称
    assert_eq!(cubic_ease(SCROLL_TO_CURVE, 0.0), 0.0);
    assert_eq!(cubic_ease(SCROLL_TO_CURVE, 1.0), 1.0);
    assert!((cubic_ease(SCROLL_TO_CURVE, 0.25) + cubic_ease(SCROLL_TO_CURVE, 0.75) - 1.0).abs() < 0.01);
}

/// 中身が縮んで範囲の外になった止まっている窓は、ばねで範囲へ戻る（Flutter の applyNewDimensions）。
#[test]
fn range_shrink_springs_back() {
    let settings = CanvasScrollComponent::default();
    let mut state = state_with(2000.0, 500.0);
    state.position[1] = 1400.0;
    state.metrics.as_mut().unwrap().content[1] = 1000.0; // 最大 500 に縮んだ
    on_range_changed(&settings, &mut state);
    assert_eq!(state.phase, ScrollPhase::Ballistic);
    for _ in 0..300 {
        advance(&settings, &mut state, FRAME);
    }
    assert_eq!(state.position[1], 500.0);
    assert_eq!(state.phase, ScrollPhase::Idle);
}

// ─── 入れ子 ───

/// 鎖の 1 つ（範囲 0..max・窓 500・1 単位 1 画素）。
fn link(position: f64, max: f64, bounce: bool, hand_off: bool) -> ChainLink {
    ChainLink {
        position,
        range: AxisRange::new(0.0, max, 500.0),
        px_per_unit: 1.0,
        bounce,
        hand_off_to_parent: hand_off,
    }
}

/// 同じ向きの入れ子: 内側が端に達した残りは外側へ渡る。渡さない設定なら内側が跳ね返りとして受ける。
#[test]
fn nested_drag_hands_off_remaining_to_parent() {
    // 内側は最大 200 の 180、外側は最大 1000 の 300。下向きに 50（位置の向き +50）
    let mut chain = vec![link(180.0, 200.0, true, true), link(300.0, 1000.0, true, true)];
    let rest = route_drag(&mut chain, 50.0);
    assert_eq!(rest, 0.0);
    assert_eq!(chain[0].position, 200.0, "内側は端まで");
    assert_eq!(chain[1].position, 330.0, "残り 30 は外側");
    // 外側も端: 内側が跳ね返りとして受ける
    let mut chain = vec![link(200.0, 200.0, true, true), link(1000.0, 1000.0, true, true)];
    route_drag(&mut chain, 40.0);
    assert_eq!(chain[1].position, 1000.0);
    assert!(chain[0].position > 200.0, "内側が端を越える: {}", chain[0].position);
    // 渡さない（hand_off_to_parent = false）: 外側は動かず、内側が跳ね返る
    let mut chain = vec![link(180.0, 200.0, true, false), link(300.0, 1000.0, true, true)];
    let len = effective_len(&chain);
    assert_eq!(len, 1);
    chain.truncate(len);
    route_drag(&mut chain, 50.0);
    assert!(chain[0].position > 200.0);
    // 内側が Clamp で外側も端: 残りは使われずに返る
    let mut chain = vec![link(200.0, 200.0, false, true), link(1000.0, 1000.0, false, true)];
    assert_eq!(route_drag(&mut chain, 40.0), 40.0);
}

/// フリックの受け手: 内側がその向きへ動ければ内側、端なら外側、はみ出していれば内側（戻る）。
#[test]
fn nested_fling_receiver() {
    let chain = [link(100.0, 200.0, true, true), link(300.0, 1000.0, true, true)];
    assert_eq!(fling_receiver(&chain, 500.0), 0);
    let at_end = [link(200.0, 200.0, true, true), link(300.0, 1000.0, true, true)];
    assert_eq!(fling_receiver(&at_end, 500.0), 1, "内側が端 → 外側");
    assert_eq!(fling_receiver(&at_end, -500.0), 0, "逆向きなら内側が動ける");
    let over = [link(230.0, 200.0, true, true), link(300.0, 1000.0, true, true)];
    assert_eq!(fling_receiver(&over, 500.0), 0, "はみ出した内側が戻る");
    let both_end = [link(200.0, 200.0, true, true), link(1000.0, 1000.0, true, true)];
    assert_eq!(fling_receiver(&both_end, 500.0), 0, "誰も動けなければ内側（跳ね返る）");
    let blocked = [link(200.0, 200.0, true, false), link(300.0, 1000.0, true, true)];
    assert_eq!(fling_receiver(&blocked, 500.0), 0, "渡さない設定なら外側へ行かない");
}

// ─── イベント ───

/// 開始 → 位置 → 終了。すぐ移す（止まったまま位置が変わる）は同じフレームで 3 つ。
#[test]
fn scroll_events_start_update_end() {
    let node = Entity::from_raw(5, 0);
    let settings = CanvasScrollComponent::default();
    let mut state = state_with(3000.0, 500.0);
    let mut out = Vec::new();
    begin_drag(&mut state, 0);
    collect_events(node, &mut state, &mut out);
    assert_eq!(out.iter().map(|e| e.kind).collect::<Vec<_>>(), vec![ScrollEventKind::Start]);
    assert!(out[0].dragging);
    out.clear();
    state.position[1] = 40.0;
    collect_events(node, &mut state, &mut out);
    assert_eq!(out.iter().map(|e| e.kind).collect::<Vec<_>>(), vec![ScrollEventKind::Update]);
    assert_eq!(out[0].delta[1], 40.0);
    assert_eq!(out[0].max_position[1], 2500.0);
    out.clear();
    release(&settings, &mut state, [0.0; 2]);
    collect_events(node, &mut state, &mut out);
    assert_eq!(out.iter().map(|e| e.kind).collect::<Vec<_>>(), vec![ScrollEventKind::End]);
    out.clear();
    state.pending = Some(ScrollRequest::Jump([0.0, 900.0]));
    apply_request(&settings, &mut state);
    collect_events(node, &mut state, &mut out);
    assert_eq!(
        out.iter().map(|e| e.kind).collect::<Vec<_>>(),
        vec![ScrollEventKind::Start, ScrollEventKind::Update, ScrollEventKind::End]
    );
    out.clear();
    collect_events(node, &mut state, &mut out);
    assert!(out.is_empty(), "変化が無ければ出さない");
}

/// ScrollTo の速度はイージングの傾きから求め（数値微分の揺れが無い）、なめらかに増えて減る。
#[test]
fn scroll_to_velocity_is_smooth() {
    use super::physics::{cubic_ease_slope, AnimateSim};
    let sim = AnimateSim::new(0.0, 1000.0, 0.4);
    // 中央（対称な曲線の最も速い所）の速度は平均の速度（2500/s）より大きい
    let mid = sim.dx(0.2);
    assert!(mid > 2500.0, "{mid}");
    // 60 Hz の刻みで、前半は増え続け、後半は減り続ける（揺れない）
    let v: Vec<f64> = (1..24).map(|i| sim.dx(f64::from(i) / 60.0)).collect();
    for w in v[..11].windows(2) {
        assert!(w[1] >= w[0] - 1.0, "前半は増える: {v:?}");
    }
    for w in v[13..].windows(2) {
        assert!(w[1] <= w[0] + 1.0, "後半は減る: {v:?}");
    }
    // 傾きの積分は 1（進みの割合）
    let n = 1000;
    let integral: f64 = (0..n).map(|i| cubic_ease_slope(SCROLL_TO_CURVE, (f64::from(i) + 0.5) / f64::from(n)) / f64::from(n)).sum();
    assert!((integral - 1.0).abs() < 0.01, "{integral}");
}
