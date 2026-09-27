// ============================================================
//  canvas_scroll/physics.rs — 1 次元のスクロールのシミュレーション（慣性・跳ね返り・ばね・ScrollTo。W2-3）
//
//  【何をするか】指を離した後・ScrollTo の間の、軸 1 本の位置 x(t) と速度 dx(t) を時刻の関数として持つ。
//  フレームは「始めてからの経過時間 t」で位置を引くだけなので、フレームの刻みに結果が依らない。
//
//  【種類】（式は Flutter のソースと同じ。出典と定数は constants.rs・docs/ui_scroll_list.md §3）
//    - ClampingSim  … 端で止める慣性（Flutter ClampingScrollSimulation = Android OverScroller の spline の近似）
//    - FrictionSim  … 指数の減衰（Flutter FrictionSimulation。drag^t で速度が減る）。`through` は指定の位置で止まるよう drag を決める
//    - SpringSim    … ばね（Flutter SpringSimulation / ScrollSpringSimulation。過減衰・臨界・不足減衰の 3 つの解）
//    - BouncingSim  … 跳ね返りの慣性（Flutter BouncingScrollSimulation: 減衰で動き、端を越えたらばねへ乗り換える）
//    - AnimateSim   … ScrollTo（Flutter の animateTo と Curves.easeInOut）
//
//  【単位】位置・速度はスクロールの単位（キャンバスの単位。dp のキャンバスなら dp）と秒。画素で決まる定数
//  （Clamp の係数・止まったとみなす許容・ばねへ渡す速度の上限）は `UnitScale` で単位へ直す。
//  World にも FFI にも触れない純ロジック。
// ============================================================

use super::constants::*;

/// 単位の換算（画素・dp で決まる定数を、スクロールの単位へ直す）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct UnitScale {
    /// 1 単位の画素数（子の累積スケール）。
    pub px_per_unit: f64,
    /// 1 dp の画素数（画面の表示倍率）。
    pub dp_scale: f64,
}

impl UnitScale {
    /// 1 単位 = 1 画素 = 1 dp（テストと既定）。
    pub const IDENTITY: Self = Self { px_per_unit: 1.0, dp_scale: 1.0 };

    /// 壊れた値（0・負・NaN）を 1 にそろえて作る。
    pub fn new(px_per_unit: f64, dp_scale: f64) -> Self {
        let fix = |v: f64| if v.is_finite() && v > EPSILON { v } else { 1.0 };
        Self { px_per_unit: fix(px_per_unit), dp_scale: fix(dp_scale) }
    }

    /// 止まったとみなす速度（単位/秒）。
    pub fn tolerance_velocity(&self) -> f64 {
        TOLERANCE_VELOCITY_PX / self.px_per_unit
    }

    /// 止まったとみなす距離（単位）。
    pub fn tolerance_distance(&self) -> f64 {
        TOLERANCE_DISTANCE_PX / self.px_per_unit
    }

    /// dp の長さ・速度・加速度を単位へ直す。
    pub fn dp_to_units(&self, dp: f64) -> f64 {
        dp * self.dp_scale / self.px_per_unit
    }
}

// ─── 端で止める慣性（Flutter ClampingScrollSimulation）───

/// 端で止める慣性。x(t) = x₀ + 距離 × (1 − (1 − t/T)^rate)、dx(t) = v₀ × (1 − t/T)^(rate−1)。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ClampingSim {
    /// 始めの位置。
    position: f64,
    /// 始めの速度。
    velocity: f64,
    /// 止まるまでの時間（秒）。
    duration: f64,
    /// 止まるまでに進む距離（向きつき）。
    distance: f64,
    /// 減速の指数（DECELERATION_RATE）。
    rate: f64,
}

impl ClampingSim {
    /// 作る。
    ///
    /// # 引数
    /// * `position` / `velocity` - 始めの位置・速度（単位・単位/秒）
    /// * `friction` - 摩擦（Android の ScrollFriction。既定 0.015）
    /// * `scale`    - 単位の換算（係数は dp/s² で決まっているので単位/s² へ直す）
    pub fn new(position: f64, velocity: f64, friction: f64, scale: UnitScale) -> Self {
        let rate = clamping_deceleration_rate();
        let coeff = scale.dp_to_units(clamping_physical_coeff_dp());
        // Flutter の _flingDuration: 基準の速度 = 摩擦 × 係数 / 変曲点、Android の時間 = (|v| / 基準)^(1/(rate−1))
        let reference_velocity = friction * coeff / CLAMPING_INFLEXION;
        let android_duration = if reference_velocity > EPSILON {
            (velocity.abs() / reference_velocity).powf(1.0 / (rate - 1.0))
        } else {
            0.0
        };
        let duration = rate * CLAMPING_INFLEXION * android_duration;
        // Flutter の _flingDistance: 距離 = v × T / rate（Android の getSplineFlingDistance と同じ値になる）
        let distance = velocity * duration / rate;
        Self { position, velocity, duration, distance, rate }
    }

    /// 止まるまでの時間（秒）。
    pub fn duration(&self) -> f64 {
        self.duration
    }

    /// 止まるまでに進む距離（向きつき）。
    pub fn distance(&self) -> f64 {
        self.distance
    }

    /// 止まる位置。
    pub fn final_x(&self) -> f64 {
        self.position + self.distance
    }

    /// 経過の割合（0〜1。時間 0 の退化は 1＝終わり）。
    fn progress(&self, t: f64) -> f64 {
        if self.duration <= EPSILON { 1.0 } else { (t / self.duration).clamp(0.0, 1.0) }
    }

    /// 位置。
    pub fn x(&self, t: f64) -> f64 {
        let p = self.progress(t);
        self.position + self.distance * (1.0 - (1.0 - p).powf(self.rate))
    }

    /// 速度。
    pub fn dx(&self, t: f64) -> f64 {
        let p = self.progress(t);
        self.velocity * (1.0 - p).powf(self.rate - 1.0)
    }

    /// 終わったか。
    pub fn is_done(&self, t: f64) -> bool {
        t >= self.duration
    }
}

// ─── 指数の減衰（Flutter FrictionSimulation。constantDeceleration = 0）───

/// 指数の減衰。x(t) = x₀ + v₀ (drag^t − 1) / ln drag、dx(t) = v₀ drag^t。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FrictionSim {
    /// 1 秒で速度が何倍になるか（0 と 1 の間）。
    drag: f64,
    /// ln(drag)（負）。
    drag_log: f64,
    /// 始めの位置。
    x0: f64,
    /// 始めの速度。
    v0: f64,
    /// 止まったとみなす速度。
    tolerance_velocity: f64,
    /// 止まったらこの位置へそろえる（`through` で作ったとき。浮動小数の誤差で目標を外さない）。
    end: Option<f64>,
}

impl FrictionSim {
    /// 作る。
    ///
    /// # 引数
    /// * `drag` - 1 秒で速度が何倍になるか（0 と 1 の間）
    /// * `x0` / `v0` - 始めの位置・速度
    /// * `tolerance_velocity` - 止まったとみなす速度
    pub fn new(drag: f64, x0: f64, v0: f64, tolerance_velocity: f64) -> Self {
        Self { drag, drag_log: drag.ln(), x0, v0, tolerance_velocity, end: None }
    }

    /// 始めの位置・速度から、終わりの位置でちょうど終わりの速度になるよう drag を決めて作る（Flutter FrictionSimulation.through）。
    ///
    /// x(t) − x₀ = (v(t) − v₀) / ln drag なので、ln drag = (v_end − v₀) / (end − x₀)。呼び出し側は
    /// 「向きが同じ・|v₀| > |v_end|・end が v₀ の向きにある」ことを確かめてから呼ぶ（そうでなければばねを使う）。
    pub fn through(x0: f64, end: f64, v0: f64, v_end: f64) -> Self {
        let drag = ((v0 - v_end) / (x0 - end)).exp();
        Self { end: Some(end), ..Self::new(drag, x0, v0, v_end.abs()) }
    }

    /// 止まる位置（速度が 0 になる極限）。
    pub fn final_x(&self) -> f64 {
        self.x0 - self.v0 / self.drag_log
    }

    /// 位置 x を通る時刻（通らなければ無限大）。x = x₀ + v₀ (drag^t − 1) / ln drag を t について解いた式。
    pub fn time_at_x(&self, x: f64) -> f64 {
        if (x - self.x0).abs() <= EPSILON {
            return 0.0;
        }
        let final_x = self.final_x();
        let beyond = if self.v0 > 0.0 { x < self.x0 || x > final_x } else { x > self.x0 || x < final_x };
        if self.v0.abs() <= EPSILON || beyond {
            return f64::INFINITY;
        }
        let arg = 1.0 + (x - self.x0) * self.drag_log / self.v0;
        if arg <= 0.0 {
            return f64::INFINITY;
        }
        arg.ln() / self.drag_log
    }

    /// 位置。
    pub fn x(&self, t: f64) -> f64 {
        if let (Some(end), true) = (self.end, self.is_done(t)) {
            return end;
        }
        self.x0 + self.v0 * (self.drag.powf(t) - 1.0) / self.drag_log
    }

    /// 速度。
    pub fn dx(&self, t: f64) -> f64 {
        self.v0 * self.drag.powf(t)
    }

    /// 終わったか（速度が許容を下回った）。
    pub fn is_done(&self, t: f64) -> bool {
        self.dx(t).abs() < self.tolerance_velocity
    }
}

// ─── ばね（Flutter SpringSimulation / ScrollSpringSimulation）───

/// ばねの解（変位 = 位置 − 終わりの位置 の時間の関数）。
#[derive(Clone, Copy, Debug, PartialEq)]
enum SpringSolution {
    /// 臨界減衰: (c1 + c2 t) e^{rt}。
    Critical { r: f64, c1: f64, c2: f64 },
    /// 過減衰: c1 e^{r1 t} + c2 e^{r2 t}。
    Over { r1: f64, r2: f64, c1: f64, c2: f64 },
    /// 不足減衰: e^{rt} (c1 cos wt + c2 sin wt)。
    Under { w: f64, r: f64, c1: f64, c2: f64 },
}

impl SpringSolution {
    /// 質量・硬さ・減衰係数と、始めの変位・速度から解を作る。
    fn new(mass: f64, stiffness: f64, damping: f64, x0: f64, v0: f64) -> Self {
        let cmk = damping * damping - 4.0 * mass * stiffness;
        if cmk.abs() <= EPSILON {
            let r = -damping / (2.0 * mass);
            SpringSolution::Critical { r, c1: x0, c2: v0 - r * x0 }
        } else if cmk > 0.0 {
            let root = cmk.sqrt();
            let r1 = (-damping - root) / (2.0 * mass);
            let r2 = (-damping + root) / (2.0 * mass);
            let c2 = (v0 - r1 * x0) / (r2 - r1);
            SpringSolution::Over { r1, r2, c1: x0 - c2, c2 }
        } else {
            let w = (4.0 * mass * stiffness - damping * damping).sqrt() / (2.0 * mass);
            let r = -damping / (2.0 * mass);
            SpringSolution::Under { w, r, c1: x0, c2: (v0 - r * x0) / w }
        }
    }

    /// 変位。
    fn x(&self, t: f64) -> f64 {
        match *self {
            SpringSolution::Critical { r, c1, c2 } => (c1 + c2 * t) * (r * t).exp(),
            SpringSolution::Over { r1, r2, c1, c2 } => c1 * (r1 * t).exp() + c2 * (r2 * t).exp(),
            SpringSolution::Under { w, r, c1, c2 } => (r * t).exp() * (c1 * (w * t).cos() + c2 * (w * t).sin()),
        }
    }

    /// 速度。
    fn dx(&self, t: f64) -> f64 {
        match *self {
            SpringSolution::Critical { r, c1, c2 } => {
                let power = (r * t).exp();
                c2 * power + r * (c1 + c2 * t) * power
            }
            SpringSolution::Over { r1, r2, c1, c2 } => c1 * r1 * (r1 * t).exp() + c2 * r2 * (r2 * t).exp(),
            SpringSolution::Under { w, r, c1, c2 } => {
                let power = (r * t).exp();
                let cos = (w * t).cos();
                let sin = (w * t).sin();
                power * (c2 * w * cos - c1 * w * sin) + r * power * (c2 * sin + c1 * cos)
            }
        }
    }
}

/// ばね（既定のばね＝質量 0.5・硬さ 100・減衰比 1.1）で終わりの位置へ寄せる。終わったら終わりの位置にそろえる。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SpringSim {
    /// 終わりの位置。
    end: f64,
    /// 解。
    solution: SpringSolution,
    /// 止まったとみなす距離。
    tolerance_distance: f64,
    /// 止まったとみなす速度。
    tolerance_velocity: f64,
}

impl SpringSim {
    /// 既定のばねで作る。
    ///
    /// # 引数
    /// * `start` / `end` - 始めの位置・終わりの位置
    /// * `velocity` - 始めの速度
    /// * `scale` - 許容の換算
    pub fn new(start: f64, end: f64, velocity: f64, scale: UnitScale) -> Self {
        let damping = SPRING_DAMPING_RATIO * CRITICAL_DAMPING_FACTOR * (SPRING_MASS * SPRING_STIFFNESS).sqrt();
        Self {
            end,
            solution: SpringSolution::new(SPRING_MASS, SPRING_STIFFNESS, damping, start - end, velocity),
            tolerance_distance: scale.tolerance_distance(),
            tolerance_velocity: scale.tolerance_velocity(),
        }
    }

    /// 終わりの位置。
    pub fn end(&self) -> f64 {
        self.end
    }

    /// 位置（終わったら終わりの位置）。
    pub fn x(&self, t: f64) -> f64 {
        if self.is_done(t) { self.end } else { self.end + self.solution.x(t) }
    }

    /// 速度。
    pub fn dx(&self, t: f64) -> f64 {
        self.solution.dx(t)
    }

    /// 終わったか（変位も速度も許容の内）。
    pub fn is_done(&self, t: f64) -> bool {
        self.solution.x(t).abs() < self.tolerance_distance && self.solution.dx(t).abs() < self.tolerance_velocity
    }
}

// ─── 跳ね返りの慣性（Flutter BouncingScrollSimulation。constantDeceleration = 0）───

/// 跳ね返りの慣性: 範囲の中は指数の減衰で動き、端を越える時刻からばねへ乗り換えて端へ戻る。
/// 範囲の外から始めたら最初からばね。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct BouncingSim {
    /// 範囲の中の減衰（範囲の外から始めたら None）。
    friction: Option<FrictionSim>,
    /// ばね（端を越えない慣性なら None）。
    spring: Option<SpringSim>,
    /// ばねへ乗り換える時刻（−∞ = 最初から・+∞ = 乗り換えない）。
    spring_time: f64,
}

impl BouncingSim {
    /// 作る。
    ///
    /// # 引数
    /// * `position` / `velocity` - 始めの位置・速度
    /// * `leading` / `trailing` - 範囲の始まり・終わり（0 と最大の位置）
    /// * `drag`  - 範囲の中の減衰（既定 0.135）
    /// * `scale` - 換算（許容・ばねへ渡す速度の上限）
    pub fn new(position: f64, velocity: f64, leading: f64, trailing: f64, drag: f64, scale: UnitScale) -> Self {
        if position < leading {
            return Self {
                friction: None,
                spring: Some(SpringSim::new(position, leading, velocity, scale)),
                spring_time: f64::NEG_INFINITY,
            };
        }
        if position > trailing {
            return Self {
                friction: None,
                spring: Some(SpringSim::new(position, trailing, velocity, scale)),
                spring_time: f64::NEG_INFINITY,
            };
        }
        let friction = FrictionSim::new(drag, position, velocity, scale.tolerance_velocity());
        let final_x = friction.final_x();
        // ばねへ渡す速度の上限（Flutter は正の向きだけ min で切り詰める。ここは向きによらず大きさで切り詰める）
        let max_transfer = scale.dp_to_units(BOUNCE_MAX_SPRING_TRANSFER_DP);
        let cap = |v: f64| v.signum() * v.abs().min(max_transfer);
        let (spring, spring_time) = if velocity > 0.0 && final_x > trailing {
            let t = friction.time_at_x(trailing);
            (Some(SpringSim::new(trailing, trailing, cap(friction.dx(t)), scale)), t)
        } else if velocity < 0.0 && final_x < leading {
            let t = friction.time_at_x(leading);
            (Some(SpringSim::new(leading, leading, cap(friction.dx(t)), scale)), t)
        } else {
            (None, f64::INFINITY)
        };
        Self { friction: Some(friction), spring, spring_time }
    }

    /// 時刻 t の区間（ばねなら (ばね, ばねの中の時刻)、減衰なら None）。
    fn spring_at(&self, t: f64) -> Option<(&SpringSim, f64)> {
        let spring = self.spring.as_ref()?;
        if t > self.spring_time || self.friction.is_none() {
            let offset = if self.spring_time.is_finite() { self.spring_time } else { 0.0 };
            Some((spring, t - offset))
        } else {
            None
        }
    }

    /// 位置。
    pub fn x(&self, t: f64) -> f64 {
        match (self.spring_at(t), self.friction.as_ref()) {
            (Some((spring, local)), _) => spring.x(local),
            (None, Some(friction)) => friction.x(t),
            (None, None) => 0.0,
        }
    }

    /// 速度。
    pub fn dx(&self, t: f64) -> f64 {
        match (self.spring_at(t), self.friction.as_ref()) {
            (Some((spring, local)), _) => spring.dx(local),
            (None, Some(friction)) => friction.dx(t),
            (None, None) => 0.0,
        }
    }

    /// 終わったか。
    pub fn is_done(&self, t: f64) -> bool {
        match (self.spring_at(t), self.friction.as_ref()) {
            (Some((spring, local)), _) => spring.is_done(local),
            (None, Some(friction)) => friction.is_done(t),
            (None, None) => true,
        }
    }

    /// 端を越えてばねへ乗り換える（跳ね返る）か。
    pub fn bounces(&self) -> bool {
        self.spring.is_some()
    }
}

// ─── ScrollTo（Flutter の animateTo と Curves.easeInOut）───

/// 3 次ベジェの 1 成分（端点 0 と 1、制御点 a・b、媒介変数 m）。
fn cubic_component(a: f64, b: f64, m: f64) -> f64 {
    3.0 * a * (1.0 - m) * (1.0 - m) * m + 3.0 * b * (1.0 - m) * m * m + m * m * m
}

/// 3 次ベジェの 1 成分の媒介変数での微分。
fn cubic_component_slope(a: f64, b: f64, m: f64) -> f64 {
    3.0 * a * (1.0 - m) * (1.0 - m) + 6.0 * (b - a) * (1.0 - m) * m + 3.0 * (1.0 - b) * m * m
}

/// 時間の割合 t に当たる媒介変数（Flutter `Cubic.transformInternal` と同じ二分法。誤差の上限 0.001）。
fn cubic_parameter(curve: [f64; 4], t: f64) -> f64 {
    /// 二分法の上限の回数（誤差の上限 0.001 に 2^-10 で届くので十分。無限の繰り返しの保護）。
    const MAX_BISECTION_STEPS: usize = 64;
    let [a, _, c, _] = curve;
    let (mut start, mut end) = (0.0, 1.0);
    let mut midpoint = 0.5;
    for _ in 0..MAX_BISECTION_STEPS {
        midpoint = (start + end) / 2.0;
        let estimate = cubic_component(a, c, midpoint);
        if (t - estimate).abs() < CUBIC_ERROR_BOUND {
            break;
        }
        if estimate < t {
            start = midpoint;
        } else {
            end = midpoint;
        }
    }
    midpoint
}

/// 3 次ベジェのイージング（Flutter `Cubic.transformInternal` と同じ）【純関数】。
///
/// # 引数
/// * `curve` - 制御点 (x1, y1, x2, y2)
/// * `t`     - 時間の割合（0〜1）
pub fn cubic_ease(curve: [f64; 4], t: f64) -> f64 {
    if t.is_nan() || t <= 0.0 {
        return 0.0;
    }
    if t >= 1.0 {
        return 1.0;
    }
    let [_, b, _, d] = curve;
    cubic_component(b, d, cubic_parameter(curve, t))
}

/// 3 次ベジェのイージングの傾き（進みの割合 ÷ 時間の割合。媒介変数の微分の比 y'(m) / x'(m)）【純関数】。
/// 数値微分と違い、二分法の誤差で速度が揺れない（イベントの速度に使う）。
pub fn cubic_ease_slope(curve: [f64; 4], t: f64) -> f64 {
    if !(t > 0.0 && t < 1.0) {
        return 0.0;
    }
    let [a, b, c, d] = curve;
    let m = cubic_parameter(curve, t);
    let dx = cubic_component_slope(a, c, m);
    if dx.abs() <= EPSILON {
        return 0.0;
    }
    cubic_component_slope(b, d, m) / dx
}

/// ScrollTo の動き（始め → 目標を、時間をかけてイージングで動く）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct AnimateSim {
    /// 始めの位置。
    from: f64,
    /// 目標の位置。
    to: f64,
    /// 時間（秒。正）。
    duration: f64,
}

impl AnimateSim {
    /// 作る（時間は正であること。0 は呼び出し側が「すぐ移す」として扱う）。
    pub fn new(from: f64, to: f64, duration: f64) -> Self {
        Self { from, to, duration: duration.max(EPSILON) }
    }

    /// 目標の位置。
    pub fn target(&self) -> f64 {
        self.to
    }

    /// 位置。
    pub fn x(&self, t: f64) -> f64 {
        self.from + (self.to - self.from) * cubic_ease(SCROLL_TO_CURVE, t / self.duration)
    }

    /// 速度（イージングの傾き × 距離 ÷ 時間）。
    pub fn dx(&self, t: f64) -> f64 {
        (self.to - self.from) / self.duration * cubic_ease_slope(SCROLL_TO_CURVE, t / self.duration)
    }

    /// 終わったか。
    pub fn is_done(&self, t: f64) -> bool {
        t >= self.duration
    }
}

// ─── まとめ ───

/// 軸 1 本の動き（どのシミュレーションか）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ScrollSim {
    /// 端で止める慣性。
    Clamping(ClampingSim),
    /// 跳ね返りの慣性。
    Bouncing(BouncingSim),
    /// ばね（端へ戻る・スナップ）。
    Spring(SpringSim),
    /// 指定の位置で止まる減衰（間隔のスナップ）。
    Friction(FrictionSim),
    /// ScrollTo。
    Animate(AnimateSim),
}

impl ScrollSim {
    /// 位置。
    pub fn x(&self, t: f64) -> f64 {
        match self {
            ScrollSim::Clamping(s) => s.x(t),
            ScrollSim::Bouncing(s) => s.x(t),
            ScrollSim::Spring(s) => s.x(t),
            ScrollSim::Friction(s) => s.x(t),
            ScrollSim::Animate(s) => s.x(t),
        }
    }

    /// 速度。
    pub fn dx(&self, t: f64) -> f64 {
        match self {
            ScrollSim::Clamping(s) => s.dx(t),
            ScrollSim::Bouncing(s) => s.dx(t),
            ScrollSim::Spring(s) => s.dx(t),
            ScrollSim::Friction(s) => s.dx(t),
            ScrollSim::Animate(s) => s.dx(t),
        }
    }

    /// 終わったか。
    pub fn is_done(&self, t: f64) -> bool {
        match self {
            ScrollSim::Clamping(s) => s.is_done(t),
            ScrollSim::Bouncing(s) => s.is_done(t),
            ScrollSim::Spring(s) => s.is_done(t),
            ScrollSim::Friction(s) => s.is_done(t),
            ScrollSim::Animate(s) => s.is_done(t),
        }
    }

    /// ScrollTo か（指で触れて止めてよい・入力を吸い込む動き）。
    pub fn is_animation(&self) -> bool {
        matches!(self, ScrollSim::Animate(_))
    }

    /// 診断ログの名前。
    pub fn label(&self) -> &'static str {
        match self {
            ScrollSim::Clamping(_) => "clamping",
            ScrollSim::Bouncing(_) => "bouncing",
            ScrollSim::Spring(_) => "spring",
            ScrollSim::Friction(_) => "friction",
            ScrollSim::Animate(_) => "animate",
        }
    }
}
