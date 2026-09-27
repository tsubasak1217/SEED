// ============================================================
//  canvas_scroll/controller.rs — スクロール 1 つの状態の移り変わり（ドラッグ・離す・触れて止める・進める・要求。W2-3）
//
//  【段階】（state.rs の ScrollPhase。Flutter の ScrollActivity の Idle / Drag / Ballistic / Driven / Hold に当たる）
//
//      Idle ──ドラッグ開始──▶ Dragging ──離す──▶ Ballistic ──終わる──▶ Idle
//        │                      ▲                  │ 触れる
//        │ScrollTo              └──ドラッグ開始── Held ◀──┘（Animating も触れると Held）
//        ▼                                          │ 触れずに離す
//      Animating ──終わる──▶ Idle                   └──▶ 離した後の動き（速度 0。はみ出しの戻り・スナップ）
//
//  物理（離した後の動き）は ballistic.rs、指の移動の当て方は overscroll.rs、入れ子の受け渡しは nesting.rs。
//  ここは World に触れない（App の scroll_events.rs が状態を取り出して呼び、書き戻す）。
// ============================================================

use crate::engine::components::{CanvasScrollComponent, ScrollEdge};

use super::ballistic::{create_ballistic, AxisPhysics};
use super::physics::{AnimateSim, ScrollSim};
use super::state::{AxisMotion, CanvasScrollState, ScrollPhase, ScrollRequest, AXES};

/// 設定から軸 1 本の物理の設定を作る（どの軸も同じ設定）。
pub fn axis_physics(settings: &CanvasScrollComponent) -> AxisPhysics {
    AxisPhysics {
        edge: settings.edge,
        inertia: settings.inertia,
        snap: settings.snap,
        snap_interval: settings.snap_interval_value(),
        fling_friction: settings.fling_friction_value(),
        bounce_drag: settings.bounce_drag_value(),
    }
}

/// スクロールする軸の一覧（0 = X・1 = Y）。
pub fn scroll_axes(settings: &CanvasScrollComponent) -> impl Iterator<Item = usize> + '_ {
    (0..AXES).filter(move |&axis| settings.direction.scrolls_axis(axis))
}

/// ドラッグを始める（動きを止めて Dragging）。
pub fn begin_drag(state: &mut CanvasScrollState, pointer: u32) {
    state.stop_motion();
    state.phase = ScrollPhase::Dragging;
    state.drag_pointer = Some(pointer);
}

/// 指で触れて止める（指に触れずに動いている途中だけ）。
///
/// # 戻り値
/// 止めたなら true。
pub fn hold(state: &mut CanvasScrollState) -> bool {
    if !state.phase.moves_by_itself() {
        return false;
    }
    state.stop_motion();
    state.phase = ScrollPhase::Held;
    true
}

/// 指を離した（ドラッグの終わり・触れて止めた後に離した・大きさが変わった）: 軸ごとに離した後の動きを作る。
///
/// # 引数
/// * `velocity` - 離した速度（位置の向き。単位/秒。スクロールしない軸は無視）
pub fn release(settings: &CanvasScrollComponent, state: &mut CanvasScrollState, velocity: [f64; AXES]) {
    state.drag_pointer = None;
    state.stop_motion();
    let physics = axis_physics(settings);
    if let Some(metrics) = state.metrics {
        for axis in scroll_axes(settings) {
            let sim = create_ballistic(
                state.position[axis],
                velocity[axis],
                metrics.range(axis),
                &physics,
                metrics.unit_scale(axis),
            );
            if let Some(sim) = sim {
                state.velocity[axis] = sim.dx(0.0);
                state.motion[axis] = Some(AxisMotion { sim, elapsed: 0.0 });
            }
        }
    }
    state.phase = if state.motion.iter().any(Option::is_some) { ScrollPhase::Ballistic } else { ScrollPhase::Idle };
}

/// 1 フレーム進める（慣性・跳ね返り・スナップ・ScrollTo）。
///
/// # 引数
/// * `dt` - 経過時間（秒。実時間）
pub fn advance(settings: &CanvasScrollComponent, state: &mut CanvasScrollState, dt: f64) {
    let dt = if dt.is_finite() && dt > 0.0 { dt } else { 0.0 };
    for axis in 0..AXES {
        let Some(mut motion) = state.motion[axis] else { continue };
        motion.elapsed += dt;
        let t = motion.elapsed;
        let mut done = motion.sim.is_done(t);
        let mut x = match (&motion.sim, done) {
            // ScrollTo は終わったら目標にそろえる（イージングの浮動小数の誤差を残さない）
            (ScrollSim::Animate(anim), true) => anim.target(),
            (sim, _) => sim.x(t),
        };
        let mut v = if done { 0.0 } else { motion.sim.dx(t) };
        // Clamp の慣性は端に着いたら止まる（Flutter の BallisticScrollActivity.applyMoveTo が進めなくなったら終わる）
        if let (ScrollSim::Clamping(_), ScrollEdge::Clamp, Some(metrics)) = (&motion.sim, settings.edge, state.metrics) {
            let range = metrics.range(axis);
            if range.out_of_range(x) {
                x = range.clamp(x);
                v = 0.0;
                done = true;
            }
        }
        state.position[axis] = x;
        state.velocity[axis] = v;
        state.motion[axis] = if done { None } else { Some(motion) };
    }
    let any_motion = state.motion.iter().any(Option::is_some);
    if state.phase.moves_by_itself() && !any_motion {
        state.phase = ScrollPhase::Idle;
        state.velocity = [0.0; AXES];
    }
}

/// スクリプトの位置の要求（Jump・ScrollTo）を当てる。
pub fn apply_request(settings: &CanvasScrollComponent, state: &mut CanvasScrollState) {
    let Some(request) = state.pending.take() else { return };
    let clamp = |state: &CanvasScrollState, axis: usize, v: f64| match state.max_position(axis) {
        Some(max) => v.clamp(0.0, max),
        None => v.max(0.0),
    };
    match request {
        ScrollRequest::Jump(target) => {
            state.stop_motion();
            state.drag_pointer = None;
            for axis in scroll_axes(settings) {
                state.position[axis] = clamp(state, axis, target[axis]);
            }
            state.phase = ScrollPhase::Idle;
        }
        ScrollRequest::Animate { target, duration } => {
            state.stop_motion();
            state.drag_pointer = None;
            for axis in scroll_axes(settings) {
                let to = clamp(state, axis, target[axis]);
                if (to - state.position[axis]).abs() > f64::EPSILON {
                    state.motion[axis] = Some(AxisMotion {
                        sim: ScrollSim::Animate(AnimateSim::new(state.position[axis], to, duration)),
                        elapsed: 0.0,
                    });
                }
            }
            state.phase = if state.motion.iter().any(Option::is_some) { ScrollPhase::Animating } else { ScrollPhase::Idle };
        }
    }
}

/// 窓・中身の大きさが変わった（範囲の最大が変わった）ときの手当て（Flutter の applyNewDimensions）。
///
/// - 止まっている … 離した後の動きを速度 0 で作る（はみ出していればばねで戻る・スナップし直す）
/// - 慣性の途中 … 今の速度で作り直す（新しい範囲で端・スナップを決め直す）
/// - ドラッグ中・触れて止めている・ScrollTo … 何もしない（離したとき・終わったときに新しい範囲で決まる）
pub fn on_range_changed(settings: &CanvasScrollComponent, state: &mut CanvasScrollState) {
    match state.phase {
        ScrollPhase::Idle => release(settings, state, [0.0; AXES]),
        ScrollPhase::Ballistic => {
            let velocity = state.velocity;
            release(settings, state, velocity);
        }
        ScrollPhase::Dragging | ScrollPhase::Held | ScrollPhase::Animating => {}
    }
}
