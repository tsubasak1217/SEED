// ============================================================
//  containers/stack.rs — 縦・横に 1 列に並べる（CanvasStack の計算。純関数）
//
//  手順（主軸 = 並べる向き、交差軸 = もう一方）:
//    1. 伸ばさない子を測る（交差軸が Stretch で、コンテナの交差軸の大きさが決まっていればその大きさで）
//    2. 伸ばす子（flex > 0）へ主軸の余りを重みで分け、その大きさで測る（主軸が決まっていなければ伸ばさない）
//    3. 主軸の揃えで先頭の空きと子の間の足し分を決め、逆順なら後ろから並べる
//    4. 交差軸は子ごとの揃え（align_self があればそれ、無ければコンテナの cross_align）で置く
//  重みで分けた大きさは子の下限・上限へ収める（収めて余った分は配り直さない。docs の正典に明記）。
//  はみ出す（子の和が箱より大きい）ときは揃えにかかわらず先頭から並べ、末尾がはみ出す。
// ============================================================

use crate::engine::components::{CanvasPadding, CrossAlign, LayoutDirection, MainAlign};

use super::{
    axes, cross_offset, distribute, on_axes, padding_origin, padding_sum, Arrangement, Constraint,
    ItemSpec, LayoutSlot, MeasureFn,
};

/// 1 列に並べる指定（画素へ換算済み）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct StackParams {
    /// 並べる向き。
    pub direction: LayoutDirection,
    /// 子の間隔（画素）。
    pub spacing: f32,
    /// 内側の余白（画素）。
    pub padding: CanvasPadding,
    /// 主軸の揃え。
    pub main_align: MainAlign,
    /// 交差軸の揃え（コンテナの既定）。
    pub cross_align: CrossAlign,
    /// 逆順に並べる。
    pub reverse: bool,
}

/// 子を 1 列に並べる【純関数】。
///
/// # 引数
/// * `params`  - 並べ方
/// * `inner`   - 箱の中身の大きさ（余白を除く。軸ごと。None = 中身に合わせる）
/// * `items`   - 子ごとの指定（並べる順）
/// * `measure` - 子の大きさを測る関数
pub fn arrange_stack(
    params: &StackParams,
    inner: Constraint,
    items: &[ItemSpec],
    measure: &mut MeasureFn<'_>,
) -> Arrangement {
    let (main, cross) = axes(params.direction);
    let count = items.len();
    let inner_main = inner[main];
    let inner_cross = inner[cross];
    let gaps = params.spacing * count.saturating_sub(1) as f32;
    // 主軸の大きさが決まっているときだけ伸ばす（中身に合わせるなら伸ばす先が無い）
    let flex_active = inner_main.is_some();

    // 交差軸を伸ばす子は、交差軸の大きさ（子の上下限へ収めたもの）を決まった大きさとして測る
    let cross_align_of = |i: usize| items[i].align_self.resolve(params.cross_align);
    let stretched_cross = |i: usize| -> Option<f32> {
        match (cross_align_of(i), inner_cross) {
            (CrossAlign::Stretch, Some(avail)) => Some(items[i].clamp(cross, avail)),
            _ => None,
        }
    };

    // ── 1. 伸ばさない子を測る ──
    let mut sizes = vec![[0.0f32; 2]; count];
    for (i, spec) in items.iter().enumerate() {
        if flex_active && spec.is_flex() {
            continue;
        }
        let mut constraint: Constraint = [None, None];
        constraint[cross] = stretched_cross(i);
        sizes[i] = measure(i, constraint);
    }

    // ── 2. 伸ばす子へ主軸の余りを重みで分けて測る ──
    if let Some(avail_main) = inner_main {
        let total_flex: f32 = items.iter().filter(|s| s.is_flex()).map(|s| s.flex).sum();
        if total_flex > 0.0 {
            let fixed_main: f32 = items
                .iter()
                .enumerate()
                .filter(|(_, s)| !s.is_flex())
                .map(|(i, _)| sizes[i][main])
                .sum();
            let remaining = (avail_main - fixed_main - gaps).max(0.0);
            for (i, spec) in items.iter().enumerate().filter(|(_, s)| s.is_flex()) {
                let share = spec.clamp(main, remaining * spec.flex / total_flex);
                let mut constraint: Constraint = [None, None];
                constraint[main] = Some(share);
                constraint[cross] = stretched_cross(i);
                sizes[i] = measure(i, constraint);
            }
        }
    }

    // ── 3. 中身の大きさと主軸の揃え ──
    let content_main: f32 = sizes.iter().map(|s| s[main]).sum::<f32>() + gaps;
    let content_cross = sizes.iter().map(|s| s[cross]).fold(0.0f32, f32::max);
    let free = inner_main.map_or(0.0, |avail| avail - content_main);
    let (leading, extra_gap) = distribute(params.main_align, free, count);
    let cross_avail = inner_cross.unwrap_or(content_cross);
    let origin0 = padding_origin(&params.padding);

    // ── 4. 並べる（逆順なら後ろの子から） ──
    let order: Vec<usize> = if params.reverse { (0..count).rev().collect() } else { (0..count).collect() };
    let mut slots = vec![LayoutSlot { origin: [0.0; 2], size: [0.0; 2], fill: [false; 2] }; count];
    let mut cursor = leading;
    for i in order {
        let size = sizes[i];
        let align = cross_align_of(i);
        let stretch = align == CrossAlign::Stretch && inner_cross.is_some();
        let offset_cross = cross_offset(align, cross_avail, size[cross]);
        let offset = on_axes(main, cursor, offset_cross);
        let mut fill = [false; 2];
        fill[main] = flex_active && items[i].is_flex();
        fill[cross] = stretch;
        slots[i] = LayoutSlot {
            origin: [origin0[0] + offset[0], origin0[1] + offset[1]],
            size,
            fill,
        };
        cursor += size[main] + params.spacing + extra_gap;
    }

    let pad = padding_sum(&params.padding);
    let content = on_axes(main, content_main, content_cross);
    Arrangement { content: [content[0] + pad[0], content[1] + pad[1]], slots }
}
