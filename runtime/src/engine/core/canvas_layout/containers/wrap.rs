// ============================================================
//  containers/wrap.rs — 折り返して並べる（CanvasWrap の計算。純関数）
//
//  手順（主軸 = 並べる向き、交差軸 = 折り返して行が積まれる向き）:
//    1. 子を自分の大きさで測る（折り返しでは伸ばさない）
//    2. 主軸の箱の長さを超えたら次の行へ送る（行の先頭の子は必ず置く。箱の長さが決まっていなければ 1 行）
//    3. 行の塊を交差軸の揃え（run_align）で置き、行の中は主軸の揃え（main_align）・交差軸の揃え（cross_align）で置く
//  行の中の交差軸の Stretch は、その行のいちばん大きい子の大きさまで伸ばす。
// ============================================================

use crate::engine::components::{CanvasPadding, CrossAlign, LayoutDirection, MainAlign};

use super::{
    axes, cross_offset, distribute, on_axes, padding_origin, padding_sum, Arrangement, Constraint,
    ItemSpec, LayoutSlot, MeasureFn, UNCONSTRAINED,
};

/// 行の境目を判定するときの許容量（画素。浮動小数の誤差でちょうど収まる子を次の行へ送らない）。
const FIT_EPSILON: f32 = 1e-3;

/// 折り返して並べる指定（画素へ換算済み）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct WrapParams {
    /// 主軸の向き（横なら左から右へ並べて下へ折り返す）。
    pub direction: LayoutDirection,
    /// 行の中の子の間隔（画素）。
    pub spacing: f32,
    /// 行の間隔（画素）。
    pub run_spacing: f32,
    /// 内側の余白（画素）。
    pub padding: CanvasPadding,
    /// 行の中の主軸の揃え。
    pub main_align: MainAlign,
    /// 行の中の交差軸の揃え。
    pub cross_align: CrossAlign,
    /// 行の塊の交差軸の揃え。
    pub run_align: MainAlign,
}

/// 1 行ぶん（子の添字の範囲と行の大きさ）。
struct Run {
    /// 先頭の子の添字。
    start: usize,
    /// 末尾の次の子の添字。
    end: usize,
    /// 主軸の長さ（子と間隔の和）。
    main: f32,
    /// 交差軸の長さ（いちばん大きい子）。
    cross: f32,
}

/// 子を折り返して並べる【純関数】。
///
/// # 引数
/// * `params`  - 並べ方
/// * `inner`   - 箱の中身の大きさ（余白を除く。None = 中身に合わせる。主軸が None なら折り返さない）
/// * `items`   - 子ごとの指定（並べる順）
/// * `measure` - 子の大きさを測る関数
pub fn arrange_wrap(
    params: &WrapParams,
    inner: Constraint,
    items: &[ItemSpec],
    measure: &mut MeasureFn<'_>,
) -> Arrangement {
    let (main, cross) = axes(params.direction);
    let count = items.len();

    // ── 1. 自分の大きさで測る ──
    let sizes: Vec<[f32; 2]> = (0..count).map(|i| measure(i, UNCONSTRAINED)).collect();

    // ── 2. 行へ分ける ──
    let mut runs: Vec<Run> = Vec::new();
    for (i, size) in sizes.iter().enumerate() {
        let fits = match (runs.last(), inner[main]) {
            (Some(run), Some(limit)) => run.main + params.spacing + size[main] <= limit + FIT_EPSILON,
            (Some(_), None) => true,
            (None, _) => false,
        };
        match runs.last_mut() {
            Some(run) if fits => {
                run.main += params.spacing + size[main];
                run.cross = run.cross.max(size[cross]);
                run.end = i + 1;
            }
            _ => runs.push(Run { start: i, end: i + 1, main: size[main], cross: size[cross] }),
        }
    }

    // ── 3. 中身の大きさと行の塊の揃え ──
    let content_main = runs.iter().map(|r| r.main).fold(0.0f32, f32::max);
    let content_cross = runs.iter().map(|r| r.cross).sum::<f32>()
        + params.run_spacing * runs.len().saturating_sub(1) as f32;
    let avail_main = inner[main].unwrap_or(content_main);
    let free_cross = inner[cross].map_or(0.0, |avail| avail - content_cross);
    let (run_leading, run_extra) = distribute(params.run_align, free_cross, runs.len());
    let origin0 = padding_origin(&params.padding);

    // ── 4. 行の中を並べる ──
    let mut slots = vec![LayoutSlot { origin: [0.0; 2], size: [0.0; 2], fill: [false; 2] }; count];
    let mut run_cursor = run_leading;
    for run in &runs {
        let (leading, extra_gap) = distribute(params.main_align, avail_main - run.main, run.end - run.start);
        let mut cursor = leading;
        for i in run.start..run.end {
            let align = items[i].align_self.resolve(params.cross_align);
            let mut size = sizes[i];
            let stretch = align == CrossAlign::Stretch;
            if stretch {
                size[cross] = items[i].clamp(cross, run.cross);
            }
            let offset = on_axes(main, cursor, run_cursor + cross_offset(align, run.cross, size[cross]));
            let mut fill = [false; 2];
            fill[cross] = stretch;
            slots[i] = LayoutSlot { origin: [origin0[0] + offset[0], origin0[1] + offset[1]], size, fill };
            cursor += size[main] + params.spacing + extra_gap;
        }
        run_cursor += run.cross + params.run_spacing + run_extra;
    }

    let pad = padding_sum(&params.padding);
    let content = on_axes(main, content_main, content_cross);
    Arrangement { content: [content[0] + pad[0], content[1] + pad[1]], slots }
}
