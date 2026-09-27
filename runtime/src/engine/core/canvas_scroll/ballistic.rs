// ============================================================
//  canvas_scroll/ballistic.rs — 指を離した後の動きを選ぶ（慣性・跳ね返り・ばね・スナップ。W2-3）
//
//  【規則】（位置の向きの速度。正 = 位置が増える向き。Flutter の createBallisticSimulation と同じ分岐）
//    Clamp（端で止める。ClampingScrollPhysics）
//      - 範囲の外（中身が縮んだ）… ばねで端へ
//      - 速度が許容より小さい・端でさらに外向き … 動かない
//      - それ以外 … ClampingSim（端に着いたら呼び出し側が止める）
//    Bounce（跳ね返り。BouncingScrollPhysics）
//      - 範囲の中で速度が許容より小さい … 動かない
//      - それ以外 … BouncingSim（範囲の外からはばね、端を越える慣性はばねへ乗り換える）
//    スナップ
//      - Page（PageScrollPhysics）: 端でさらに外向き（か止まっている）なら上の基本の動き。それ以外は
//        ページ = 位置 ÷ ページの長さ に、速度の向きへ 0.5 を足して丸めたページへばねで寄せる（1 回で最大 1 ページ）
//      - Interval（FixedExtentScrollPhysics の考え方）: 基本の動きで止まる位置に最も近い間隔の倍数を目標にし、
//        その向きへ十分な速度があれば「目標でちょうど止まる減衰」（FrictionSim::through）、無ければばねで寄せる
//    慣性を切った（inertia = false）ときは速度 0 として選ぶ（跳ね返りの戻りとスナップはする）。
// ============================================================

use crate::engine::components::{ScrollEdge, ScrollSnap};

use super::constants::{EPSILON, PAGE_FLING_BIAS};
use super::overscroll::AxisRange;
use super::physics::{BouncingSim, ClampingSim, FrictionSim, ScrollSim, SpringSim, UnitScale};

/// 軸 1 本の物理の設定（CanvasScrollComponent から作る）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct AxisPhysics {
    /// 端での振る舞い。
    pub edge: ScrollEdge,
    /// 離した後の慣性。
    pub inertia: bool,
    /// スナップ。
    pub snap: ScrollSnap,
    /// スナップの長さ（単位。None = Page は窓の長さ・Interval はスナップしない）。
    pub snap_interval: Option<f64>,
    /// Clamp の摩擦。
    pub fling_friction: f64,
    /// Bounce の減衰。
    pub bounce_drag: f64,
}

impl AxisPhysics {
    /// Bounce か。
    pub fn bounces(&self) -> bool {
        self.edge == ScrollEdge::Bounce
    }
}

/// 離した後の動きを作る（動かないなら None）【純関数】。
///
/// # 引数
/// * `position` - 今の位置
/// * `velocity` - 離した速度（位置の向き。単位/秒）
/// * `range`    - 範囲
/// * `physics`  - 物理の設定
/// * `scale`    - 換算
pub fn create_ballistic(
    position: f64,
    velocity: f64,
    range: AxisRange,
    physics: &AxisPhysics,
    scale: UnitScale,
) -> Option<ScrollSim> {
    let velocity = if physics.inertia && velocity.is_finite() { velocity } else { 0.0 };
    match physics.snap {
        ScrollSnap::None => base_ballistic(position, velocity, range, physics, scale),
        ScrollSnap::Page => {
            let page = physics.snap_interval.unwrap_or(range.viewport);
            if page <= EPSILON {
                return base_ballistic(position, velocity, range, physics, scale);
            }
            page_ballistic(position, velocity, range, physics, scale, page)
        }
        ScrollSnap::Interval => match physics.snap_interval {
            Some(interval) => interval_ballistic(position, velocity, range, physics, scale, interval),
            None => base_ballistic(position, velocity, range, physics, scale),
        },
    }
}

/// スナップの無い基本の動き。
fn base_ballistic(
    position: f64,
    velocity: f64,
    range: AxisRange,
    physics: &AxisPhysics,
    scale: UnitScale,
) -> Option<ScrollSim> {
    let tolerance = scale.tolerance_velocity();
    match physics.edge {
        ScrollEdge::Clamp => {
            if range.out_of_range(position) {
                let end = range.clamp(position);
                return Some(ScrollSim::Spring(SpringSim::new(position, end, 0.0, scale)));
            }
            if velocity.abs() < tolerance
                || (velocity > 0.0 && position >= range.max)
                || (velocity < 0.0 && position <= range.min)
            {
                return None;
            }
            Some(ScrollSim::Clamping(ClampingSim::new(position, velocity, physics.fling_friction, scale)))
        }
        ScrollEdge::Bounce => {
            if velocity.abs() < tolerance && !range.out_of_range(position) {
                return None;
            }
            Some(ScrollSim::Bouncing(BouncingSim::new(
                position,
                velocity,
                range.min,
                range.max,
                physics.bounce_drag,
                scale,
            )))
        }
    }
}

/// ページの目標（Flutter PageScrollPhysics._getTargetPixels）【純関数】。
///
/// # 引数
/// * `position` - 今の位置
/// * `velocity` - 速度（位置の向き）
/// * `page`     - ページの長さ
/// * `tolerance_velocity` - 止まったとみなす速度
pub fn page_target(position: f64, velocity: f64, page: f64, tolerance_velocity: f64) -> f64 {
    let mut index = position / page;
    if velocity < -tolerance_velocity {
        index -= PAGE_FLING_BIAS;
    } else if velocity > tolerance_velocity {
        index += PAGE_FLING_BIAS;
    }
    index.round() * page
}

/// ページ送り。
fn page_ballistic(
    position: f64,
    velocity: f64,
    range: AxisRange,
    physics: &AxisPhysics,
    scale: UnitScale,
    page: f64,
) -> Option<ScrollSim> {
    // 端でさらに外向き（か止まっている）: 基本の動き（跳ね返りの戻り・止まる）
    if (velocity <= 0.0 && position <= range.min) || (velocity >= 0.0 && position >= range.max) {
        return base_ballistic(position, velocity, range, physics, scale);
    }
    let target = range.clamp(page_target(position, velocity, page, scale.tolerance_velocity()));
    if (target - position).abs() <= scale.tolerance_distance() {
        return None;
    }
    Some(ScrollSim::Spring(SpringSim::new(position, target, velocity, scale)))
}

/// 基本の動きで止まる位置（跳ね返るものは範囲へ収めた位置）【純関数】。
fn natural_end(sim: &Option<ScrollSim>, position: f64, range: AxisRange) -> f64 {
    /// 止まる位置を求めるときの「十分に長い時間」（秒。どの動きもこれより前に終わる）。
    const SETTLE_HORIZON_SECS: f64 = 60.0;
    match sim {
        Some(ScrollSim::Clamping(s)) => range.clamp(s.final_x()),
        Some(s) => range.clamp(s.x(SETTLE_HORIZON_SECS)),
        None => range.clamp(position),
    }
}

/// 間隔のスナップ。
fn interval_ballistic(
    position: f64,
    velocity: f64,
    range: AxisRange,
    physics: &AxisPhysics,
    scale: UnitScale,
    interval: f64,
) -> Option<ScrollSim> {
    let tolerance_v = scale.tolerance_velocity();
    let tolerance_d = scale.tolerance_distance();
    let base = base_ballistic(position, velocity, range, physics, scale);
    let end = natural_end(&base, position, range);
    let target = range.clamp((end / interval).round() * interval);
    if velocity.abs() < tolerance_v && (target - position).abs() < tolerance_d {
        return None;
    }
    let toward = (target - position) * velocity > 0.0;
    if toward && velocity.abs() > tolerance_v && !range.out_of_range(position) {
        // 目標でちょうど止まる減衰（終わりの速度 = 許容。向きは始めの速度と同じ）
        return Some(ScrollSim::Friction(FrictionSim::through(
            position,
            target,
            velocity,
            tolerance_v * velocity.signum(),
        )));
    }
    Some(ScrollSim::Spring(SpringSim::new(position, target, velocity, scale)))
}
