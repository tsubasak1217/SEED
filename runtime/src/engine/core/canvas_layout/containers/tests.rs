// ============================================================
//  containers/tests.rs — 並べ方の純関数の単体テスト（Stack・Wrap・Grid・揃えの配り方）
//
//  子の大きさは表で与え、測る関数は「決まった軸はその値・決まっていない軸は表の値」を返す
//  （走査の measure と同じ約束）。期待値は手で計算した画素。
// ============================================================

use super::grid::{arrange_grid, column_count, GridParams};
use super::stack::{arrange_stack, StackParams};
use super::wrap::{arrange_wrap, WrapParams};
use super::*;
use crate::engine::components::{CanvasPadding, CrossAlign, ItemAlign, LayoutDirection, MainAlign};

/// 表の大きさで測る関数を作る（決まった軸はその値）。呼ばれた回数も数える。
fn table_measure<'a>(natural: &'a [[f32; 2]], calls: &'a mut usize) -> impl FnMut(usize, Constraint) -> [f32; 2] + 'a {
    move |i, c| {
        *calls += 1;
        [c[0].unwrap_or(natural[i][0]), c[1].unwrap_or(natural[i][1])]
    }
}

/// 余白（全辺同じ）。
fn pad(v: f32) -> CanvasPadding {
    CanvasPadding { left: v, top: v, right: v, bottom: v }
}

/// 既定の 1 列の指定（縦・先頭・余白なし）。
fn stack(direction: LayoutDirection) -> StackParams {
    StackParams {
        direction,
        spacing: 0.0,
        padding: CanvasPadding::default(),
        main_align: MainAlign::Start,
        cross_align: CrossAlign::Start,
        reverse: false,
    }
}

/// 子ごとの指定（何も上書きしない）を n 個。
fn plain(n: usize) -> Vec<ItemSpec> {
    vec![ItemSpec::default(); n]
}

/// 左上と大きさを並べて取り出す（比べやすくするため）。
fn rects(arr: &Arrangement) -> Vec<([f32; 2], [f32; 2])> {
    arr.slots.iter().map(|s| (s.origin, s.size)).collect()
}

// ─── 揃えの配り方 ─────────────────────────────────────────

/// 余り 60 を 3 つの子へ: 中央 30・末尾 60・間 30・前後 10/20・均等 15。はみ出し（負）は先頭。
#[test]
fn distribute_matches_css_like_rules() {
    assert_eq!(distribute(MainAlign::Start, 60.0, 3), (0.0, 0.0));
    assert_eq!(distribute(MainAlign::Center, 60.0, 3), (30.0, 0.0));
    assert_eq!(distribute(MainAlign::End, 60.0, 3), (60.0, 0.0));
    assert_eq!(distribute(MainAlign::SpaceBetween, 60.0, 3), (0.0, 30.0));
    assert_eq!(distribute(MainAlign::SpaceAround, 60.0, 3), (10.0, 20.0));
    assert_eq!(distribute(MainAlign::SpaceEvenly, 60.0, 3), (15.0, 15.0));
    assert_eq!(distribute(MainAlign::SpaceBetween, 60.0, 1), (0.0, 0.0), "子が 1 つなら先頭");
    assert_eq!(distribute(MainAlign::End, -40.0, 3), (0.0, 0.0), "はみ出しは先頭から");
    assert_eq!(distribute(MainAlign::Center, 10.0, 0), (0.0, 0.0));
}

// ─── Stack ────────────────────────────────────────────────

/// 縦・先頭・間隔 5・余白 10: 上から順に積み、中身は最大の幅 × 高さの和（余白込み）。
#[test]
fn stack_vertical_start_with_spacing_and_padding() {
    let natural = [[100.0, 20.0], [80.0, 30.0], [50.0, 10.0]];
    let mut calls = 0;
    let params = StackParams { spacing: 5.0, padding: pad(10.0), ..stack(LayoutDirection::Vertical) };
    let arr = arrange_stack(&params, [Some(200.0), Some(300.0)], &plain(3), &mut table_measure(&natural, &mut calls));
    assert_eq!(
        rects(&arr),
        vec![([10.0, 10.0], [100.0, 20.0]), ([10.0, 35.0], [80.0, 30.0]), ([10.0, 70.0], [50.0, 10.0])]
    );
    assert_eq!(arr.content, [120.0, 90.0]);
    assert_eq!(calls, 3, "子は 1 回ずつ測る");
    assert!(arr.slots.iter().all(|s| s.fill == [false, false]));
}

/// 主軸の揃え: 箱 100 に 20 × 3（余り 40）。
#[test]
fn stack_main_align_distributes_free_space() {
    let natural = [[10.0, 20.0]; 3];
    let ys = |align: MainAlign| -> Vec<f32> {
        let mut calls = 0;
        let params = StackParams { main_align: align, ..stack(LayoutDirection::Vertical) };
        let arr = arrange_stack(&params, [Some(10.0), Some(100.0)], &plain(3), &mut table_measure(&natural, &mut calls));
        arr.slots.iter().map(|s| s.origin[1]).collect()
    };
    assert_eq!(ys(MainAlign::Start), vec![0.0, 20.0, 40.0]);
    assert_eq!(ys(MainAlign::Center), vec![20.0, 40.0, 60.0]);
    assert_eq!(ys(MainAlign::End), vec![40.0, 60.0, 80.0]);
    assert_eq!(ys(MainAlign::SpaceBetween), vec![0.0, 40.0, 80.0]);
    let around = ys(MainAlign::SpaceAround);
    assert!((around[0] - 40.0 / 6.0).abs() < 1e-4 && (around[2] - (100.0 - 20.0 - 40.0 / 6.0)).abs() < 1e-4, "{around:?}");
    assert_eq!(ys(MainAlign::SpaceEvenly), vec![10.0, 40.0, 70.0]);
}

/// 交差軸の揃え（縦に並べると左右）: 中央・末尾・Stretch（箱の幅いっぱい・fill）。子の上書きが優先する。
#[test]
fn stack_cross_align_and_align_self() {
    let natural = [[40.0, 10.0], [40.0, 10.0], [40.0, 10.0]];
    let mut calls = 0;
    let params = StackParams { cross_align: CrossAlign::Center, ..stack(LayoutDirection::Vertical) };
    let mut items = plain(3);
    items[1].align_self = ItemAlign::End;
    items[2] = ItemSpec { align_self: ItemAlign::Stretch, max: [150.0, f32::INFINITY], ..ItemSpec::default() };
    let arr = arrange_stack(&params, [Some(200.0), None], &items, &mut table_measure(&natural, &mut calls));
    assert_eq!(arr.slots[0].origin[0], 80.0, "中央");
    assert_eq!(arr.slots[1].origin[0], 160.0, "子の上書きで末尾");
    assert_eq!((arr.slots[2].origin[0], arr.slots[2].size[0]), (0.0, 150.0), "Stretch は上限へ収める");
    assert_eq!(arr.slots[2].fill, [true, false], "伸ばした軸はスプライトも伸ばす");
    assert_eq!(arr.content[1], 30.0, "主軸は中身に合わせる");
}

/// 交差軸の大きさが決まっていない（中身に合わせる）と Stretch は先頭と同じ（自分の大きさ）。
#[test]
fn stack_stretch_without_cross_size_keeps_natural() {
    let natural = [[40.0, 10.0], [70.0, 10.0]];
    let mut calls = 0;
    let params = StackParams { cross_align: CrossAlign::Stretch, ..stack(LayoutDirection::Vertical) };
    let arr = arrange_stack(&params, [None, None], &plain(2), &mut table_measure(&natural, &mut calls));
    assert_eq!(arr.slots[0].size, [40.0, 10.0]);
    assert_eq!(arr.slots[0].fill, [false, false]);
    assert_eq!(arr.content, [70.0, 20.0]);
}

/// 伸ばす: 箱 350 に固定 50 と重み 1・2 → 残り 300 を 100・200 に分ける（主軸を fill）。上限で止まる子は余りを残す。
#[test]
fn stack_flex_shares_remaining_space() {
    let natural = [[10.0, 50.0], [10.0, 5.0], [10.0, 5.0]];
    let mut calls = 0;
    let items = vec![
        ItemSpec::default(),
        ItemSpec { flex: 1.0, ..ItemSpec::default() },
        ItemSpec { flex: 2.0, ..ItemSpec::default() },
    ];
    let arr = arrange_stack(&stack(LayoutDirection::Vertical), [Some(10.0), Some(350.0)], &items, &mut table_measure(&natural, &mut calls));
    assert_eq!(rects(&arr)[1], ([0.0, 50.0], [10.0, 100.0]));
    assert_eq!(rects(&arr)[2], ([0.0, 150.0], [10.0, 200.0]));
    assert_eq!(arr.slots[1].fill, [false, true]);
    assert_eq!(calls, 3, "伸ばす子も 1 回だけ測る（伸ばした大きさで）");

    let mut calls = 0;
    let capped = vec![
        ItemSpec::default(),
        ItemSpec { flex: 1.0, max: [f32::INFINITY, 60.0], ..ItemSpec::default() },
        ItemSpec { flex: 1.0, ..ItemSpec::default() },
    ];
    let arr = arrange_stack(&stack(LayoutDirection::Vertical), [Some(10.0), Some(350.0)], &capped, &mut table_measure(&natural, &mut calls));
    assert_eq!(arr.slots[1].size[1], 60.0, "上限");
    assert_eq!(arr.slots[2].size[1], 150.0, "配り直さない（重みで分けた分のまま）");
}

/// 主軸が決まっていない（中身に合わせる）と伸ばさない。横向き・逆順も確かめる。
#[test]
fn stack_horizontal_reverse_and_fit() {
    let natural = [[30.0, 10.0], [20.0, 12.0], [10.0, 8.0]];
    let mut calls = 0;
    let items = vec![ItemSpec::default(), ItemSpec { flex: 1.0, ..ItemSpec::default() }, ItemSpec::default()];
    let params = StackParams { spacing: 2.0, reverse: true, ..stack(LayoutDirection::Horizontal) };
    let arr = arrange_stack(&params, [None, None], &items, &mut table_measure(&natural, &mut calls));
    // 逆順: 最後の子（10）→ 2 番目（20）→ 最初（30）
    assert_eq!(arr.slots[2].origin, [0.0, 0.0]);
    assert_eq!(arr.slots[1].origin, [12.0, 0.0]);
    assert_eq!(arr.slots[0].origin, [34.0, 0.0]);
    assert_eq!(arr.slots[1].size, [20.0, 12.0], "中身に合わせるときは伸ばさない");
    assert_eq!(arr.content, [64.0, 12.0]);
}

// ─── Wrap ─────────────────────────────────────────────────

/// 幅 150 に 40 × 5（間隔 10）: 3 個で 140 → 4 個目から次の行。行の間隔 5、行の高さはその行の最大。
#[test]
fn wrap_breaks_runs_and_stacks_them() {
    let natural = [[40.0, 20.0], [40.0, 30.0], [40.0, 20.0], [40.0, 10.0], [40.0, 25.0]];
    let mut calls = 0;
    let params = WrapParams {
        direction: LayoutDirection::Horizontal,
        spacing: 10.0,
        run_spacing: 5.0,
        padding: CanvasPadding::default(),
        main_align: MainAlign::Start,
        cross_align: CrossAlign::Start,
        run_align: MainAlign::Start,
    };
    let arr = arrange_wrap(&params, [Some(150.0), None], &plain(5), &mut table_measure(&natural, &mut calls));
    let origins: Vec<[f32; 2]> = arr.slots.iter().map(|s| s.origin).collect();
    assert_eq!(origins, vec![[0.0, 0.0], [50.0, 0.0], [100.0, 0.0], [0.0, 35.0], [50.0, 35.0]]);
    assert_eq!(arr.content, [140.0, 60.0], "幅は最長の行・高さは行の和と間隔");
    assert_eq!(calls, 5);
}

/// 行の中の揃え（中央）・行の塊の揃え（末尾）・行の中の Stretch（行の高さまで）。幅が決まらなければ 1 行。
#[test]
fn wrap_alignments_and_single_run_without_width() {
    let natural = [[40.0, 20.0], [40.0, 10.0], [40.0, 20.0]];
    let mut calls = 0;
    let params = WrapParams {
        direction: LayoutDirection::Horizontal,
        spacing: 0.0,
        run_spacing: 0.0,
        padding: CanvasPadding::default(),
        main_align: MainAlign::Center,
        cross_align: CrossAlign::Stretch,
        run_align: MainAlign::End,
    };
    let arr = arrange_wrap(&params, [Some(100.0), Some(100.0)], &plain(3), &mut table_measure(&natural, &mut calls));
    // 1 行目は 2 個（80）で余り 20 → 中央で 10 から。行は 2 つ（高さ 20・20）で余り 60 → 末尾へ
    assert_eq!(arr.slots[0].origin, [10.0, 60.0]);
    assert_eq!(arr.slots[1].size, [40.0, 20.0], "行の高さまで伸ばす");
    assert_eq!(arr.slots[1].fill, [false, true]);
    assert_eq!(arr.slots[2].origin, [30.0, 80.0]);

    let mut calls = 0;
    let arr = arrange_wrap(&params, [None, None], &plain(3), &mut table_measure(&natural, &mut calls));
    assert!(arr.slots.iter().all(|s| s.origin[1] == 0.0), "幅が決まらなければ折り返さない");
    assert_eq!(arr.content, [120.0, 20.0]);
}

// ─── Grid ─────────────────────────────────────────────────

/// 固定 3 列・幅 320・間隔 10 → セルの幅 100、縦横比 2 → 高さ 50。5 個で 2 行。既定はセルいっぱい（fill）。
#[test]
fn grid_fixed_columns_with_aspect() {
    let natural = [[5.0, 5.0]; 5];
    let mut calls = 0;
    let params = GridParams {
        columns: 3,
        cell_min_width: 0.0,
        cell_aspect_ratio: 2.0,
        spacing: [10.0, 10.0],
        padding: pad(4.0),
        cell_align: CrossAlign::Stretch,
    };
    let arr = arrange_grid(&params, [Some(320.0), None], &plain(5), &mut table_measure(&natural, &mut calls));
    assert_eq!(rects(&arr)[0], ([4.0, 4.0], [100.0, 50.0]));
    assert_eq!(rects(&arr)[2], ([224.0, 4.0], [100.0, 50.0]));
    assert_eq!(rects(&arr)[4], ([114.0, 64.0], [100.0, 50.0]));
    assert!(arr.slots.iter().all(|s| s.fill == [true, true]));
    assert_eq!(arr.content, [328.0, 118.0]);
    assert_eq!(calls, 0, "セルいっぱいで大きさが決まっている子は測らない");
}

/// 自動の列数: 幅 330・最小 100・間隔 10 → 3 列（340 / 110）。幅が決まらなければ 1 行。最小幅 0 は 1 列。
#[test]
fn grid_auto_column_count() {
    let params = GridParams {
        columns: 0,
        cell_min_width: 100.0,
        cell_aspect_ratio: 1.0,
        spacing: [10.0, 10.0],
        padding: CanvasPadding::default(),
        cell_align: CrossAlign::Stretch,
    };
    assert_eq!(column_count(&params, Some(330.0), 10), 3);
    assert_eq!(column_count(&params, Some(320.0), 10), 3, "100×3 + 10×2 = 320 ちょうど");
    assert_eq!(column_count(&params, Some(319.0), 10), 2);
    assert_eq!(column_count(&params, Some(50.0), 10), 1, "入らなくても 1 列");
    assert_eq!(column_count(&params, None, 7), 7);
    assert_eq!(column_count(&GridParams { cell_min_width: 0.0, ..params }, Some(330.0), 10), 1);
}

/// 縦横比 0（行の高さは中身）とセルの中の中央寄せ（自分の大きさ）。
#[test]
fn grid_content_rows_and_center_align() {
    let natural = [[20.0, 30.0], [40.0, 10.0], [30.0, 50.0]];
    let mut calls = 0;
    let params = GridParams {
        columns: 2,
        cell_min_width: 0.0,
        cell_aspect_ratio: 0.0,
        spacing: [0.0, 0.0],
        padding: CanvasPadding::default(),
        cell_align: CrossAlign::Center,
    };
    let arr = arrange_grid(&params, [Some(100.0), None], &plain(3), &mut table_measure(&natural, &mut calls));
    // セル 50 幅。1 行目の高さ 30（最大）、2 行目 50
    assert_eq!(rects(&arr)[0], ([15.0, 0.0], [20.0, 30.0]));
    assert_eq!(rects(&arr)[1], ([55.0, 10.0], [40.0, 10.0]));
    assert_eq!(rects(&arr)[2], ([10.0, 30.0], [30.0, 50.0]));
    assert_eq!(arr.content, [100.0, 80.0]);
    assert_eq!(calls, 3, "自分の大きさで置く子は 1 回だけ測る");
}
