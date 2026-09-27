// ============================================================
//  canvas_scroll/constants.rs — スクロールの物理の定数の表（出典つき。W2-3）
//
//  値はすべて Android の OverScroller・ViewConfiguration と Flutter の scroll_simulation.dart・scroll_physics.dart・
//  friction_simulation.dart・page_view.dart・curves.dart・viewport.dart から取った（2026-09-28 に Flutter の master の
//  ソースを取得して確かめた。Android の値は Flutter のソースの注記「See DECELERATION_RATE」等が指す OverScroller の定数）。
//  長さの単位が付く値は「論理画素（dp）」の値で、使う側が 1 dp の画素数（dp_scale）で画素へ換算する。
//  表は docs/ui_scroll_list.md §3 にも同じものがある（変えるときは両方を直す）。
// ============================================================

// ─── Clamp（端で止める）の慣性: Flutter ClampingScrollSimulation = Android OverScroller の spline ───

/// 減速の指数。出典: Android `OverScroller.DECELERATION_RATE` = ln(0.78) / ln(0.9)（Flutter `_kDecelerationRate`）。
pub fn clamping_deceleration_rate() -> f64 {
    /// ln の中の値（分子）。
    const NUMERATOR_BASE: f64 = 0.78;
    /// ln の中の値（分母）。
    const DENOMINATOR_BASE: f64 = 0.9;
    NUMERATOR_BASE.ln() / DENOMINATOR_BASE.ln()
}

/// 張力の曲線の変曲点。出典: Android `OverScroller.INFLEXION` = 0.35（Flutter `_kInflexion`）。
pub const CLAMPING_INFLEXION: f64 = 0.35;

/// 地球の重力加速度（m/s²）。出典: Android `SensorManager.GRAVITY_EARTH` = 9.80665（Flutter `_physicalCoeff` の 1 項目）。
pub const GRAVITY_EARTH: f64 = 9.80665;

/// 1 m のインチ数。出典: Android `OverScroller` の `mPhysicalCoeff` = 39.37（Flutter も同じ）。
pub const INCHES_PER_METER: f64 = 39.37;

/// 1 インチの dp 数（Android の基準の密度 mdpi = 160 dpi）。出典: Flutter `_physicalCoeff` の 160.0。
pub const DP_PER_INCH: f64 = 160.0;

/// 手触りの調整の係数。出典: Android `OverScroller` の「look and feel tuning」0.84（Flutter も同じ）。
pub const PHYSICAL_COEFF_TUNING: f64 = 0.84;

/// Clamp の慣性の係数（dp/s²）= 重力 × インチ/m × dp/インチ × 調整。画素では × dp_scale。
pub fn clamping_physical_coeff_dp() -> f64 {
    GRAVITY_EARTH * INCHES_PER_METER * DP_PER_INCH * PHYSICAL_COEFF_TUNING
}

// ─── Bounce（跳ね返り）: Flutter BouncingScrollPhysics・BouncingScrollSimulation ───

/// 端の外へ引っぱるときの摩擦の係数（ScrollDecelerationRate.normal）。
/// 出典: Flutter `BouncingScrollPhysics.frictionFactor` = 0.52 × (1 − はみ出しの割合)²。
/// 2 乗の形は overscroll.rs の閉じた式（1/(1 − x/V) が指の移動に比例する）の前提。
pub const BOUNCE_OVERSCROLL_FRICTION: f64 = 0.52;

/// 慣性がばねへ渡す速度の上限（dp/s）。出典: Flutter `BouncingScrollSimulation.maxSpringTransferVelocity` = 5000。
pub const BOUNCE_MAX_SPRING_TRANSFER_DP: f64 = 5000.0;

// ─── ばね: Flutter ScrollPhysics の既定のばね（_kDefaultSpring）───

/// ばねの質量。出典: Flutter `SpringDescription.withDampingRatio(mass: 0.5, stiffness: 100.0, ratio: 1.1)`。
pub const SPRING_MASS: f64 = 0.5;
/// ばねの硬さ。出典: 同上。
pub const SPRING_STIFFNESS: f64 = 100.0;
/// ばねの減衰比（1 より大きい＝振動しない過減衰）。出典: 同上。
pub const SPRING_DAMPING_RATIO: f64 = 1.1;
/// 減衰比から減衰係数を作る式の 2（c = 比 × 2√(mk)。臨界減衰の定義）。
pub const CRITICAL_DAMPING_FACTOR: f64 = 2.0;

// ─── 止まったとみなす許容（Flutter ScrollPhysics.toleranceFor）───

/// 速度の許容（物理画素/秒）。出典: Flutter `toleranceFor` の velocity = 1 / (0.050 × devicePixelRatio) 論理画素/秒
/// = 20 物理画素/秒（画面の画素で一定）。
pub const TOLERANCE_VELOCITY_PX: f64 = 20.0;
/// 位置の許容（物理画素）。出典: Flutter `toleranceFor` の distance = 1 / devicePixelRatio 論理画素 = 1 物理画素。
pub const TOLERANCE_DISTANCE_PX: f64 = 1.0;

// ─── ScrollTo の動き（Flutter Curves.easeInOut）───

/// ScrollTo の動きの 3 次ベジェの制御点（x1, y1, x2, y2）。出典: Flutter `Curves.easeInOut` = Cubic(0.42, 0.0, 0.58, 1.0)。
pub const SCROLL_TO_CURVE: [f64; 4] = [0.42, 0.0, 0.58, 1.0];
/// 3 次ベジェの逆算の許容誤差。出典: Flutter `Cubic._cubicErrorBound` = 0.001。
pub const CUBIC_ERROR_BOUND: f64 = 0.001;

// ─── ページのスナップ（Flutter PageScrollPhysics._getTargetPixels）───

/// フリックの向きへページを進める量（0.5 を足して丸める＝向きの側の次のページ）。出典: Flutter `PageScrollPhysics`。
pub const PAGE_FLING_BIAS: f64 = 0.5;

// ─── 数値の保護 ───

/// 長さ・時間を 0 とみなす大きさ（0 除算の保護）。
pub const EPSILON: f64 = 1e-9;
