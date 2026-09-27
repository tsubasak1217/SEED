// ============================================================
//  containers/grid.rs — 格子に並べる（CanvasGrid の計算。純関数）
//
//  手順:
//    1. 列数: 固定（columns ≥ 1）か、セルの最小幅から箱の幅に入るだけ（columns = 0。箱の幅が決まっていなければ 1 行）
//    2. セルの幅: 箱の幅を列数で等分（間隔を除く）。箱の幅が決まっていなければセルの最小幅（0 以下なら子の最大の幅）
//    3. セルの高さ: 縦横比（幅 ÷ 高さ）が正ならセルの幅 ÷ 縦横比。0 以下なら行ごとに子の最大の高さ
//    4. 子は左上から行優先でセルへ入れ、セルの中は揃え（子の align_self → コンテナの cell_align）で置く。
//       Stretch はセルいっぱい（下限・上限へ収める）、それ以外は自分の大きさで先頭・中央・末尾
// ============================================================

use crate::engine::components::{CanvasPadding, CrossAlign};

use super::{
    cross_offset, padding_origin, padding_sum, Arrangement, Constraint, ItemSpec, LayoutSlot, MeasureFn,
    AXIS_X, AXIS_Y, UNCONSTRAINED,
};

/// 格子に並べる指定（画素へ換算済み）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct GridParams {
    /// 列数（1 以上で固定。0 で自動）。
    pub columns: u32,
    /// 自動の列数に使うセルの最小幅（画素）。
    pub cell_min_width: f32,
    /// セルの縦横比（幅 ÷ 高さ）。0 以下なら行の高さは中身。
    pub cell_aspect_ratio: f32,
    /// 列・行の間隔（画素）。
    pub spacing: [f32; 2],
    /// 内側の余白（画素）。
    pub padding: CanvasPadding,
    /// セルの中の子の置き方。
    pub cell_align: CrossAlign,
}

/// 列数を決める【純関数】。
///
/// # 引数
/// * `params`  - 並べ方
/// * `inner_w` - 箱の中身の幅（決まっていなければ None）
/// * `count`   - 子の数
pub fn column_count(params: &GridParams, inner_w: Option<f32>, count: usize) -> usize {
    if params.columns > 0 {
        return params.columns as usize;
    }
    let min_w = params.cell_min_width;
    match inner_w {
        Some(w) if min_w > 0.0 => {
            let per_cell = min_w + params.spacing[AXIS_X];
            (((w + params.spacing[AXIS_X]) / per_cell).floor() as usize).max(1)
        }
        // 最小幅が無ければ自動にできない（1 列）
        Some(_) => 1,
        // 幅が決まっていなければ 1 行に並べる
        None => count.max(1),
    }
}

/// 子を格子に並べる【純関数】。
///
/// # 引数
/// * `params`  - 並べ方
/// * `inner`   - 箱の中身の大きさ（余白を除く。None = 中身に合わせる）
/// * `items`   - 子ごとの指定（並べる順）
/// * `measure` - 子の大きさを測る関数
pub fn arrange_grid(
    params: &GridParams,
    inner: Constraint,
    items: &[ItemSpec],
    measure: &mut MeasureFn<'_>,
) -> Arrangement {
    let count = items.len();
    let columns = column_count(params, inner[AXIS_X], count);
    let rows = count.div_ceil(columns);
    let [spacing_x, spacing_y] = params.spacing;
    let align_of = |i: usize| items[i].align_self.resolve(params.cell_align);

    // ── セルの幅 ──
    let mut natural: Vec<Option<[f32; 2]>> = vec![None; count];
    let cell_w = match inner[AXIS_X] {
        Some(w) => ((w - spacing_x * (columns as f32 - 1.0)) / columns as f32).max(0.0),
        None if params.cell_min_width > 0.0 => params.cell_min_width,
        None => {
            // 幅の手掛かりが無いので子の最大の幅にする（ここで測った大きさは下で使い回す）
            let mut widest = 0.0f32;
            for (i, slot) in natural.iter_mut().enumerate() {
                let size = measure(i, UNCONSTRAINED);
                widest = widest.max(size[AXIS_X]);
                *slot = Some(size);
            }
            widest
        }
    };

    // ── 行の高さ ──
    let mut row_heights = vec![0.0f32; rows];
    if params.cell_aspect_ratio > 0.0 {
        row_heights.fill(cell_w / params.cell_aspect_ratio);
    } else {
        for i in 0..count {
            // 横に伸ばす子はセルの幅を決まった幅として高さを測る（中身の高さが幅で変わる子のため）
            let size = if align_of(i) == CrossAlign::Stretch {
                measure(i, [Some(items[i].clamp(AXIS_X, cell_w)), None])
            } else {
                // 自分の大きさで置く子は、ここで測った大きさを下のセルへの配置でも使う
                let own = natural[i].unwrap_or_else(|| measure(i, UNCONSTRAINED));
                natural[i] = Some(own);
                own
            };
            let row = i / columns;
            row_heights[row] = row_heights[row].max(size[AXIS_Y]);
        }
    }

    // ── セルへ入れる ──
    let origin0 = padding_origin(&params.padding);
    let mut row_tops = Vec::with_capacity(rows);
    let mut top = 0.0f32;
    for h in &row_heights {
        row_tops.push(top);
        top += h + spacing_y;
    }
    let mut slots = Vec::with_capacity(count);
    for i in 0..count {
        let (row, col) = (i / columns, i % columns);
        let cell_origin = [
            origin0[AXIS_X] + col as f32 * (cell_w + spacing_x),
            origin0[AXIS_Y] + row_tops[row],
        ];
        let cell_size = [cell_w, row_heights[row]];
        let align = align_of(i);
        let slot = if align == CrossAlign::Stretch {
            LayoutSlot {
                origin: cell_origin,
                size: [items[i].clamp(AXIS_X, cell_size[AXIS_X]), items[i].clamp(AXIS_Y, cell_size[AXIS_Y])],
                fill: [true, true],
            }
        } else {
            let size = *natural[i].get_or_insert_with(|| measure(i, UNCONSTRAINED));
            LayoutSlot {
                origin: [
                    cell_origin[AXIS_X] + cross_offset(align, cell_size[AXIS_X], size[AXIS_X]),
                    cell_origin[AXIS_Y] + cross_offset(align, cell_size[AXIS_Y], size[AXIS_Y]),
                ],
                size,
                fill: [false, false],
            }
        };
        slots.push(slot);
    }

    // 中身の幅は列数ぶん（子が列数より少なくても、固定の列数の格子の幅を保つ。子が無ければ 0）
    let content_w = if count == 0 {
        0.0
    } else {
        columns as f32 * cell_w + spacing_x * (columns as f32 - 1.0)
    };
    let content_h = row_heights.iter().sum::<f32>() + spacing_y * rows.saturating_sub(1) as f32;
    let pad = padding_sum(&params.padding);
    Arrangement { content: [content_w + pad[AXIS_X], content_h + pad[AXIS_Y]], slots }
}
