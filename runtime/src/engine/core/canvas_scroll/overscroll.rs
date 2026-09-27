// ============================================================
//  canvas_scroll/overscroll.rs — 指のドラッグを位置へ当てる（範囲・端の外の摩擦。W2-3）
//
//  【規則】（位置の向き: 正 = 位置が増える＝中身が上・左へ動く。指の移動の逆）
//    1. 範囲の中は指の移動のとおり（1:1）
//    2. Clamp は範囲の外へ出ない（端で止まる。Flutter ClampingScrollPhysics.applyBoundaryConditions）
//    3. Bounce は範囲の外へ摩擦つきで出る。摩擦の係数は Flutter BouncingScrollPhysics.frictionFactor
//       f(x) = 0.52 × (1 − x / 窓の長さ)²（x = はみ出しの量）。はみ出しは窓の長さに近づくほど進まなくなる
//    4. はみ出した所から戻る向きの移動にも同じ摩擦が掛かり、端へ戻った残りは範囲の中で 1:1
//
//  【連続の形】Flutter はフレームごとに f をその時のはみ出しで 1 回掛ける（1 フレームの移動が大きいと結果が
//  フレームの刻みに依る）。ここは同じ摩擦の曲線を dx/do = f(x) の微分方程式として解いた閉じた式で当てる
//  （u = 1 − x/V と置くと d(1/u)/do = 0.52 / V なので 1/u が指の移動に比例する）。小さな刻みの極限で Flutter と一致し、
//  フレームの刻みに依らない。入れ子の受け渡しのため、使った入力の量も返す。
// ============================================================

use super::constants::{BOUNCE_OVERSCROLL_FRICTION, EPSILON};

/// はみ出しの割合の上限（u = 1 − x/V が 0 にならないように。x が窓の長さに届くと 1/u が無限大になる）。
const MAX_OVERSCROLL_FRACTION: f64 = 0.999;

/// 軸 1 本の範囲（スクロールの単位）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct AxisRange {
    /// 位置の最小（常に 0）。
    pub min: f64,
    /// 位置の最大（中身 − 窓。0 以上）。
    pub max: f64,
    /// 窓の長さ（はみ出しの摩擦の基準）。
    pub viewport: f64,
}

impl AxisRange {
    /// 作る（最大は最小より小さくしない）。
    pub fn new(min: f64, max: f64, viewport: f64) -> Self {
        Self { min, max: max.max(min), viewport: viewport.max(0.0) }
    }

    /// 範囲の外か。
    pub fn out_of_range(&self, position: f64) -> bool {
        position < self.min || position > self.max
    }

    /// 範囲へ収める。
    pub fn clamp(&self, position: f64) -> f64 {
        position.clamp(self.min, self.max)
    }

    /// 向き（正・負）へまだ動けるか（範囲の中で、その向きの端に達していない）。
    pub fn can_move(&self, position: f64, direction: f64) -> bool {
        if direction > 0.0 {
            position < self.max
        } else if direction < 0.0 {
            position > self.min
        } else {
            false
        }
    }
}

/// はみ出しから、さらに外へ `pull` だけ指を動かした後のはみ出し【純関数】。
///
/// # 引数
/// * `over` - 今のはみ出し（0 以上）
/// * `pull` - 外へ向かう指の移動（0 以上）
/// * `viewport` - 窓の長さ
pub fn overscroll_after_pull(over: f64, pull: f64, viewport: f64) -> f64 {
    if viewport <= EPSILON {
        // 窓の長さが無い退化: 係数だけ掛ける
        return over + BOUNCE_OVERSCROLL_FRICTION * pull;
    }
    let u0 = (1.0 - over / viewport).max(1.0 - MAX_OVERSCROLL_FRACTION);
    let inverse = 1.0 / u0 + BOUNCE_OVERSCROLL_FRICTION * pull / viewport;
    viewport * (1.0 - 1.0 / inverse)
}

/// はみ出しから、範囲へ戻る向きへ `push` だけ指を動かした後のはみ出しと、使った入力【純関数】。
///
/// # 戻り値
/// (新しいはみ出し, 使った入力)。端まで戻り切ったら (0, 戻るのに使った量) で、残りは呼び出し側が範囲の中で当てる。
pub fn overscroll_after_push(over: f64, push: f64, viewport: f64) -> (f64, f64) {
    if viewport <= EPSILON {
        let need = over / BOUNCE_OVERSCROLL_FRICTION;
        return if push >= need { (0.0, need) } else { (over - BOUNCE_OVERSCROLL_FRICTION * push, push) };
    }
    let u0 = (1.0 - over / viewport).max(1.0 - MAX_OVERSCROLL_FRACTION);
    // 1/u が 1（はみ出し 0）へ戻るのに要る入力
    let need = (1.0 / u0 - 1.0) * viewport / BOUNCE_OVERSCROLL_FRICTION;
    if push >= need {
        return (0.0, need);
    }
    let inverse = 1.0 / u0 - BOUNCE_OVERSCROLL_FRICTION * push / viewport;
    ((viewport * (1.0 - 1.0 / inverse)).max(0.0), push)
}

/// ドラッグの移動を位置へ当てる【純関数】。
///
/// # 引数
/// * `position` - 今の位置
/// * `delta`    - 移動（位置の向き）
/// * `range`    - 範囲
/// * `bounce`   - Bounce（端の外へ出られる・はみ出しを摩擦つきで戻す）か
/// * `allow_overscroll` - 範囲の外へ出てよいか（入れ子の最後の段で、残りを内側が跳ね返りとして受けるとき true）
///
/// # 戻り値
/// (新しい位置, 使った入力（向きつき）)。使わなかった残りは外側のスクロールへ渡せる。
pub fn apply_user_delta(position: f64, delta: f64, range: AxisRange, bounce: bool, allow_overscroll: bool) -> (f64, f64) {
    if delta == 0.0 || !delta.is_finite() {
        return (position, 0.0);
    }
    let mut p = position;
    let mut rest = delta;
    let mut used = 0.0;

    // ── 1. はみ出していて、戻る向きの入力: 摩擦つきで端まで戻す（Clamp は 1:1。中身が縮んだときにだけ起きる）──
    if p < range.min && rest > 0.0 {
        let over = range.min - p;
        let (after, consumed) = if bounce {
            overscroll_after_push(over, rest, range.viewport)
        } else {
            let step = rest.min(over);
            (over - step, step)
        };
        p = range.min - after;
        used += consumed;
        rest -= consumed;
    } else if p > range.max && rest < 0.0 {
        let over = p - range.max;
        let (after, consumed) = if bounce {
            overscroll_after_push(over, -rest, range.viewport)
        } else {
            let step = (-rest).min(over);
            (over - step, step)
        };
        p = range.max + after;
        used -= consumed;
        rest += consumed;
    }

    // ── 2. 範囲の中は 1:1（端で止める）──
    if rest != 0.0 && !range.out_of_range(p) {
        let clamped = range.clamp(p + rest);
        let step = clamped - p;
        p = clamped;
        used += step;
        rest -= step;
    }

    // ── 3. Bounce で外へ出てよいとき: 残りを摩擦つきで範囲の外へ ──
    if rest != 0.0 && bounce && allow_overscroll {
        if rest < 0.0 && p <= range.min {
            let after = overscroll_after_pull(range.min - p, -rest, range.viewport);
            p = range.min - after;
            used += rest;
        } else if rest > 0.0 && p >= range.max {
            let after = overscroll_after_pull(p - range.max, rest, range.viewport);
            p = range.max + after;
            used += rest;
        }
    }
    (p, used)
}
