// ============================================================
//  canvas_layout/layout_tests.rs — レイアウトの部品（W2-1b）の走査のテスト（World とアクター木を使う）
//
//  コンテナ（Stack・Wrap・Grid）・子の側の指定・親に合わせる・安全領域・dp・切り抜きとの組み合わせ・
//  入れ子のコンテナ・フォルダ・非表示の子・性能（測る回数がノード数に比例）を、表（CanvasLayoutTable）の値で確かめる。
//  並べ方の純関数そのもののテストは containers/tests.rs、W2-1a の旧実装との同値は app/canvas_layout_equivalence/。
//
//  座標の読み方: ノードの矩形の左上（キャンバスのワールド座標）は、表の行の world_rs の平行移動（行列の [0][3]・[1][3]）。
//  ルートの文脈はビューポート基準なし（ルートの左上が原点）か、Play と同じ中央原点（play_env）。
// ============================================================

use std::collections::HashMap;

use super::*;
use crate::engine::components::{
    CanvasClipComponent, CanvasComponent, CanvasDrawZone, CanvasGridComponent, CanvasLayoutItemComponent,
    CanvasPadding, CanvasSafeAreaComponent, CanvasStackComponent, CanvasTransform, CanvasUnit,
    CanvasWrapComponent, ComponentKind, CrossAlign, HiddenChildren, ItemAlign, LayoutDirection, MainAlign,
    SpriteComponent,
};
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

/// 浮動小数の比較の許容量（画素）。
const EPS: f32 = 1e-3;

// ─── 木を作る道具 ─────────────────────────────────────────

/// キャンバスのノード（CanvasTransform + CanvasComponent）。
fn canvas_node(world: &mut World, name: &str, ct: CanvasTransform, size: [f32; 2]) -> Actor {
    let entity = world.spawn();
    world.insert(entity, ct);
    let mut actor = Actor::new_2d(entity, name);
    let slot = world.spawn();
    world.insert(slot, CanvasComponent { width: size[0], height: size[1], auto_scale: false, ..CanvasComponent::default() });
    actor.add_slot_typed::<CanvasComponent>("Canvas", ComponentKind::Canvas, slot);
    actor
}

/// スプライトのノード（CanvasTransform + SpriteComponent）。
fn sprite_node(world: &mut World, name: &str, ct: CanvasTransform, size: [f32; 2]) -> Actor {
    let entity = world.spawn();
    world.insert(entity, ct);
    let mut actor = Actor::new_2d(entity, name);
    let slot = world.spawn();
    world.insert(slot, SpriteComponent { width: size[0], height: size[1], ..SpriteComponent::default() });
    actor.add_slot_typed::<SpriteComponent>("Sprite", ComponentKind::Sprite, slot);
    actor
}

/// CanvasTransform だけのノード（中身に合わせるコンテナ用）。
fn bare_node(world: &mut World, name: &str) -> Actor {
    let entity = world.spawn();
    world.insert(entity, CanvasTransform::default());
    Actor::new_2d(entity, name)
}

/// コンポーネントのスロットを足す。
fn add<T: crate::engine::ecs::Component>(world: &mut World, actor: &mut Actor, kind: ComponentKind, value: T) {
    let slot = world.spawn();
    world.insert(slot, value);
    actor.add_slot_typed::<T>("Slot", kind, slot);
}

/// 縦の 1 列のコンテナを足す。
fn add_stack(world: &mut World, actor: &mut Actor, c: CanvasStackComponent) {
    add(world, actor, ComponentKind::CanvasStack, c);
}

/// 子の側の指定を足す。
fn add_item(world: &mut World, actor: &mut Actor, c: CanvasLayoutItemComponent) {
    add(world, actor, ComponentKind::CanvasLayoutItem, c);
}

/// 何の文脈も使わない表（ルートの左上が原点）。
fn table_of(roots: &[Actor], world: &World) -> CanvasLayoutTable {
    let empty: HashMap<Entity, [f32; 2]> = HashMap::new();
    let env = CanvasLayoutEnv::without_viewport(&empty, AutoScaleDivisor::Raw);
    CanvasLayoutPass::run(roots, world, 0, CanvasParentFrame::viewport_root(CanvasDrawZone::Foreground), &env)
}

/// Play と同じ文脈（ビューポート中央が原点・設計空間でない）の表。
fn play_table(roots: &[Actor], world: &World, viewport: [f32; 2], screen: CanvasScreenEnv) -> CanvasLayoutTable {
    let empty: HashMap<Entity, [f32; 2]> = HashMap::new();
    let env = CanvasLayoutEnv {
        viewport_size: Some(viewport),
        viewport_overrides: &empty,
        root_auto_sizes: &empty,
        design_space: false,
        auto_scale_divisor: AutoScaleDivisor::Raw,
        screen,
    };
    CanvasLayoutPass::run(roots, world, 0, CanvasParentFrame::viewport_root(CanvasDrawZone::Foreground), &env)
}

/// 表の行を名前で引く（行の配置と、その行のアクター）。
fn placed<'t>(table: &'t CanvasLayoutTable, roots: &[Actor], name: &str) -> &'t CanvasNodePlacement {
    table
        .iter_with_actors(roots)
        .find(|(_, a)| a.name == name)
        .and_then(|(n, _)| n.placement())
        .unwrap_or_else(|| panic!("{name} が表に無い"))
}

/// ノードの矩形の左上（キャンバスのワールド座標。world_rs の平行移動）。
fn origin(p: &CanvasNodePlacement) -> [f32; 2] {
    [p.world_rs[0][3], p.world_rs[1][3]]
}

/// 2 つの点がほぼ等しいか。
fn near(a: [f32; 2], b: [f32; 2]) -> bool {
    (a[0] - b[0]).abs() < EPS && (a[1] - b[1]).abs() < EPS
}

/// 既定の縦の 1 列（有効・縦・先頭）。
fn vstack() -> CanvasStackComponent {
    CanvasStackComponent::default()
}

// ─── Stack ────────────────────────────────────────────────

/// コンテナの下の子は、自分のアンカー・位置より並べ方が優先する（パネル (100,50) の余白 20・間隔 10）。
#[test]
fn stack_overrides_child_anchor_and_position() {
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), [1280.0, 720.0]);
    let mut panel = canvas_node(&mut world, "Panel", CanvasTransform { position: [100.0, 50.0], ..CanvasTransform::default() }, [400.0, 300.0]);
    add_stack(&mut world, &mut panel, CanvasStackComponent { spacing: 10.0, padding: CanvasPadding { left: 20.0, top: 20.0, right: 20.0, bottom: 20.0 }, ..vstack() });
    // 子の anchor・position は無視される（コンテナの配置が優先）
    panel.add_child(sprite_node(&mut world, "A", CanvasTransform { anchor: [1.0, 1.0], position: [999.0, 999.0], ..CanvasTransform::default() }, [100.0, 40.0]));
    panel.add_child(sprite_node(&mut world, "B", CanvasTransform::default(), [200.0, 30.0]));
    root.add_child(panel);
    let roots = vec![root];
    let table = table_of(&roots, &world);
    assert!(near(origin(placed(&table, &roots, "A")), [120.0, 70.0]));
    assert!(near(origin(placed(&table, &roots, "B")), [120.0, 120.0]));
    let a = placed(&table, &roots, "A");
    assert_eq!(a.sprite_size(100.0, 40.0), [100.0, 40.0], "伸ばしていない子のスプライトは自分の大きさ");
    assert!(a.layout_adjusted && a.layout_rect == Some([100.0, 40.0]));
    assert_eq!(table.stats.containers, 1);
    assert_eq!(table.stats.placed_by_layout, 2);
}

/// 交差軸の Stretch: 子のスプライトは箱の幅いっぱい（余白を除く）で描かれ、キャンバスの子は領域がその幅になる。
#[test]
fn stack_cross_stretch_fills_sprites_and_canvas_areas() {
    let mut world = World::new();
    let mut panel = canvas_node(&mut world, "Panel", CanvasTransform::default(), [400.0, 300.0]);
    add_stack(&mut world, &mut panel, CanvasStackComponent { cross_align: CrossAlign::Stretch, padding: CanvasPadding { left: 10.0, top: 0.0, right: 30.0, bottom: 0.0 }, ..vstack() });
    panel.add_child(sprite_node(&mut world, "Row", CanvasTransform::default(), [50.0, 40.0]));
    let mut sub = canvas_node(&mut world, "SubPanel", CanvasTransform::default(), [60.0, 20.0]);
    // SubPanel の子は SubPanel の新しい領域（360 幅）を基準にアンカーする
    sub.add_child(sprite_node(&mut world, "RightEdge", CanvasTransform { anchor: [1.0, 0.0], ..CanvasTransform::default() }, [5.0, 5.0]));
    panel.add_child(sub);
    let roots = vec![panel];
    let table = table_of(&roots, &world);
    let row = placed(&table, &roots, "Row");
    assert_eq!(row.sprite_size(50.0, 40.0), [360.0, 40.0], "幅は伸ばし、高さは自分の大きさ");
    assert!(near(origin(row), [10.0, 0.0]));
    let sub = placed(&table, &roots, "SubPanel");
    assert!(near(sub.eff_size, [360.0, 20.0]), "{:?}", sub.eff_size);
    assert!(near(origin(placed(&table, &roots, "RightEdge")), [370.0, 40.0]), "子のアンカーは伸ばした領域の右端");
}

/// 伸ばす重み: 高さ 300 から固定 60 を引いた 240 を 1:2 で分ける。
#[test]
fn stack_flex_children_share_the_rest() {
    let mut world = World::new();
    let mut panel = canvas_node(&mut world, "Panel", CanvasTransform::default(), [100.0, 300.0]);
    add_stack(&mut world, &mut panel, vstack());
    panel.add_child(sprite_node(&mut world, "Header", CanvasTransform::default(), [100.0, 60.0]));
    let mut body = sprite_node(&mut world, "Body", CanvasTransform::default(), [100.0, 10.0]);
    add_item(&mut world, &mut body, CanvasLayoutItemComponent { flex: 1.0, ..CanvasLayoutItemComponent::default() });
    panel.add_child(body);
    let mut footer = sprite_node(&mut world, "Footer", CanvasTransform::default(), [100.0, 10.0]);
    add_item(&mut world, &mut footer, CanvasLayoutItemComponent { flex: 2.0, ..CanvasLayoutItemComponent::default() });
    panel.add_child(footer);
    let roots = vec![panel];
    let table = table_of(&roots, &world);
    let body = placed(&table, &roots, "Body");
    assert!(near(origin(body), [0.0, 60.0]));
    assert!((body.sprite_size(100.0, 10.0)[1] - 80.0).abs() < EPS, "伸ばした主軸はスプライトも伸びる");
    let footer = placed(&table, &roots, "Footer");
    assert!(near(origin(footer), [0.0, 140.0]));
    assert!((footer.sprite_size(100.0, 10.0)[1] - 160.0).abs() < EPS);
}

/// 無視させる子は自分のアンカー・位置のまま。非表示の子は「詰める」と飛ばし、「場所を残す」と場所を取る。
#[test]
fn ignore_layout_and_hidden_children() {
    for (hidden_mode, expect_c_y) in [(HiddenChildren::Collapse, 20.0), (HiddenChildren::KeepSpace, 40.0)] {
        let mut world = World::new();
        let mut panel = canvas_node(&mut world, "Panel", CanvasTransform::default(), [200.0, 200.0]);
        add_stack(&mut world, &mut panel, CanvasStackComponent { hidden_children: hidden_mode, ..vstack() });
        panel.add_child(sprite_node(&mut world, "A", CanvasTransform::default(), [10.0, 20.0]));
        let mut deco = sprite_node(&mut world, "Deco", CanvasTransform { anchor: [0.5, 0.5], ..CanvasTransform::default() }, [8.0, 8.0]);
        add_item(&mut world, &mut deco, CanvasLayoutItemComponent { ignore_layout: true, ..CanvasLayoutItemComponent::default() });
        panel.add_child(deco);
        let mut hidden = sprite_node(&mut world, "Hidden", CanvasTransform::default(), [10.0, 20.0]);
        hidden.visible = false;
        panel.add_child(hidden);
        panel.add_child(sprite_node(&mut world, "C", CanvasTransform::default(), [10.0, 20.0]));
        let roots = vec![panel];
        let table = table_of(&roots, &world);
        assert!(near(origin(placed(&table, &roots, "Deco")), [100.0, 100.0]), "無視させた子はアンカー（中央）のまま");
        assert!(!placed(&table, &roots, "Deco").layout_adjusted);
        assert!(near(origin(placed(&table, &roots, "C")), [0.0, expect_c_y]), "{hidden_mode:?}");
    }
}

/// フォルダの中の子も、フォルダが無いのと同じにコンテナの子として並ぶ。
#[test]
fn folder_children_are_laid_out_as_container_children() {
    let mut world = World::new();
    let mut panel = canvas_node(&mut world, "Panel", CanvasTransform::default(), [200.0, 200.0]);
    add_stack(&mut world, &mut panel, CanvasStackComponent { direction: LayoutDirection::Horizontal, spacing: 4.0, ..vstack() });
    panel.add_child(sprite_node(&mut world, "A", CanvasTransform::default(), [30.0, 10.0]));
    let folder_entity = world.spawn();
    world.insert(folder_entity, CanvasTransform::default());
    let mut folder = Actor::new_folder_2d(folder_entity, "Folder");
    folder.add_child(sprite_node(&mut world, "B", CanvasTransform::default(), [20.0, 10.0]));
    folder.add_child(sprite_node(&mut world, "C", CanvasTransform::default(), [10.0, 10.0]));
    panel.add_child(folder);
    let roots = vec![panel];
    let table = table_of(&roots, &world);
    assert!(near(origin(placed(&table, &roots, "B")), [34.0, 0.0]));
    assert!(near(origin(placed(&table, &roots, "C")), [58.0, 0.0]));
}

/// 入れ子: 中身に合わせる横の列（CanvasComponent なし）を縦の列の中へ。内側の大きさが外側の並びに効く。
#[test]
fn nested_containers_measure_inner_content() {
    let mut world = World::new();
    let mut outer = canvas_node(&mut world, "Outer", CanvasTransform::default(), [300.0, 600.0]);
    add_stack(&mut world, &mut outer, CanvasStackComponent { spacing: 10.0, main_align: MainAlign::End, ..vstack() });
    let mut row = bare_node(&mut world, "Row");
    add_stack(&mut world, &mut row, CanvasStackComponent { direction: LayoutDirection::Horizontal, spacing: 5.0, padding: CanvasPadding { left: 2.0, top: 3.0, right: 2.0, bottom: 3.0 }, ..vstack() });
    for (i, h) in [20.0, 40.0, 30.0].into_iter().enumerate() {
        row.add_child(sprite_node(&mut world, &format!("Chip{i}"), CanvasTransform::default(), [50.0, h]));
    }
    outer.add_child(row);
    outer.add_child(sprite_node(&mut world, "Tail", CanvasTransform::default(), [80.0, 50.0]));
    let roots = vec![outer];
    let table = table_of(&roots, &world);
    // Row の中身 = 幅 50×3 + 5×2 + 4 = 164、高さ 40 + 6 = 46。外側は末尾寄せ: 600 − (46 + 10 + 50) = 494 から
    let row = placed(&table, &roots, "Row");
    assert!(near(origin(row), [0.0, 494.0]), "{:?}", origin(row));
    assert_eq!(row.layout_rect, Some([164.0, 46.0]));
    assert!(near(origin(placed(&table, &roots, "Chip1")), [57.0, 497.0]), "内側の余白と間隔");
    assert!(near(origin(placed(&table, &roots, "Tail")), [0.0, 550.0]));
}

/// CanvasComponent を持つコンテナの fit_height: 領域の高さが中身に合い、背景のスプライトも伸びる。
#[test]
fn fit_height_resizes_canvas_container() {
    let mut world = World::new();
    let mut list = canvas_node(&mut world, "List", CanvasTransform::default(), [200.0, 999.0]);
    add(&mut world, &mut list, ComponentKind::Sprite, SpriteComponent { width: 200.0, height: 999.0, ..SpriteComponent::default() });
    add_stack(&mut world, &mut list, CanvasStackComponent { fit_height: true, spacing: 4.0, padding: CanvasPadding { left: 0.0, top: 8.0, right: 0.0, bottom: 8.0 }, ..vstack() });
    for i in 0..3 {
        list.add_child(sprite_node(&mut world, &format!("Item{i}"), CanvasTransform::default(), [200.0, 30.0]));
    }
    let roots = vec![list];
    let table = table_of(&roots, &world);
    let list = placed(&table, &roots, "List");
    assert!(near(list.eff_size, [200.0, 114.0]), "8 + 30×3 + 4×2 + 8 = 114: {:?}", list.eff_size);
    assert_eq!(list.sprite_size(200.0, 999.0), [200.0, 114.0], "背景の板も中身に合わせる");
    assert!(near(origin(placed(&table, &roots, "Item2")), [0.0, 76.0]));
}

// ─── Wrap・Grid ───────────────────────────────────────────

/// 折り返し: 幅 150 に 40 幅のチップを間隔 10 で 5 つ → 3 + 2 に分かれる。
#[test]
fn wrap_in_tree_breaks_lines() {
    let mut world = World::new();
    let mut chips = canvas_node(&mut world, "Chips", CanvasTransform::default(), [150.0, 100.0]);
    add(&mut world, &mut chips, ComponentKind::CanvasWrap, CanvasWrapComponent { spacing: 10.0, run_spacing: 6.0, ..CanvasWrapComponent::default() });
    for i in 0..5 {
        chips.add_child(sprite_node(&mut world, &format!("Chip{i}"), CanvasTransform::default(), [40.0, 20.0]));
    }
    let roots = vec![chips];
    let table = table_of(&roots, &world);
    assert!(near(origin(placed(&table, &roots, "Chip2")), [100.0, 0.0]));
    assert!(near(origin(placed(&table, &roots, "Chip3")), [0.0, 26.0]));
    assert!(near(origin(placed(&table, &roots, "Chip4")), [50.0, 26.0]));
}

/// 格子: 自動の列数（幅 330・最小 100・間隔 10 → 3 列・セル 103.33）と縦横比 2、セルいっぱいのスプライト。
#[test]
fn grid_in_tree_auto_columns() {
    let mut world = World::new();
    let mut grid = canvas_node(&mut world, "Grid", CanvasTransform::default(), [330.0, 400.0]);
    add(&mut world, &mut grid, ComponentKind::CanvasGrid, CanvasGridComponent {
        columns: 0,
        cell_min_width: 100.0,
        cell_aspect_ratio: 2.0,
        spacing_x: 10.0,
        spacing_y: 10.0,
        ..CanvasGridComponent::default()
    });
    for i in 0..4 {
        grid.add_child(sprite_node(&mut world, &format!("Cell{i}"), CanvasTransform::default(), [1.0, 1.0]));
    }
    let roots = vec![grid];
    let table = table_of(&roots, &world);
    let cell_w = (330.0 - 20.0) / 3.0;
    let c3 = placed(&table, &roots, "Cell3");
    assert!(near(origin(c3), [0.0, cell_w / 2.0 + 10.0]), "4 つ目は 2 行目の先頭: {:?}", origin(c3));
    let s = c3.sprite_size(1.0, 1.0);
    assert!((s[0] - cell_w).abs() < EPS && (s[1] - cell_w / 2.0).abs() < EPS, "{s:?}");
    assert!(near(origin(placed(&table, &roots, "Cell2")), [2.0 * (cell_w + 10.0), 0.0]));
}

/// 切り抜きとの組み合わせ: コンテナ自身が切り抜くと、並べた子は切り抜きの中に入る（領域はコンテナの領域）。
#[test]
fn container_with_clip_puts_children_inside_the_clip() {
    let mut world = World::new();
    let mut list = canvas_node(&mut world, "List", CanvasTransform { position: [10.0, 10.0], ..CanvasTransform::default() }, [100.0, 50.0]);
    add_stack(&mut world, &mut list, vstack());
    add(&mut world, &mut list, ComponentKind::CanvasClip, CanvasClipComponent { enabled: true, ..CanvasClipComponent::default() });
    for i in 0..4 {
        list.add_child(sprite_node(&mut world, &format!("Row{i}"), CanvasTransform::default(), [100.0, 30.0]));
    }
    let roots = vec![list];
    let table = table_of(&roots, &world);
    assert_eq!(table.clip_regions.len(), 1);
    let (min, max) = clip::corners_aabb(&table.clip_regions[0].corners);
    assert_eq!((min, max), ([10.0, 10.0], [110.0, 60.0]));
    let rows: Vec<_> = table.iter_with_actors(&roots).filter(|(_, a)| a.name.starts_with("Row")).collect();
    assert!(rows.iter().all(|(n, _)| n.clip == Some(0)), "並べた子はすべて切り抜きの中");
    assert!(near(origin(placed(&table, &roots, "Row3")), [10.0, 100.0]), "はみ出す子も並べる（見えないだけ）");
}

// ─── 親に合わせる ─────────────────────────────────────────

/// fill_width・fill_height: 親のキャンバス領域いっぱい（上下限へ収める）。合わせない軸は自分の位置と大きさ。
#[test]
fn fill_parent_matches_parent_area() {
    let mut world = World::new();
    let mut parent = canvas_node(&mut world, "Parent", CanvasTransform::default(), [300.0, 200.0]);
    let mut bg = sprite_node(&mut world, "Bg", CanvasTransform::default(), [1.0, 1.0]);
    add_item(&mut world, &mut bg, CanvasLayoutItemComponent { fill_width: true, fill_height: true, ..CanvasLayoutItemComponent::default() });
    parent.add_child(bg);
    let mut bar = sprite_node(&mut world, "Bar", CanvasTransform { position: [5.0, 150.0], ..CanvasTransform::default() }, [10.0, 20.0]);
    add_item(&mut world, &mut bar, CanvasLayoutItemComponent { fill_width: true, max_width: 250.0, ..CanvasLayoutItemComponent::default() });
    parent.add_child(bar);
    let roots = vec![parent];
    let table = table_of(&roots, &world);
    let bg = placed(&table, &roots, "Bg");
    assert!(near(origin(bg), [0.0, 0.0]));
    assert_eq!(bg.sprite_size(1.0, 1.0), [300.0, 200.0]);
    let bar = placed(&table, &roots, "Bar");
    assert!(near(origin(bar), [0.0, 150.0]), "合わせない軸（縦）は自分の位置: {:?}", origin(bar));
    assert_eq!(bar.sprite_size(10.0, 20.0), [250.0, 20.0], "上限 250");
}

// ─── 安全領域 ─────────────────────────────────────────────

/// Play の文脈（1080×2400・中央原点）で、全画面のパネルを安全領域（上 136・下 63）へ縮める。
#[test]
fn safe_area_shrinks_full_screen_panel() {
    let viewport = [1080.0, 2400.0];
    let safe = CanvasRect { min: [-540.0, -1064.0], max: [540.0, 1137.0] };
    let build = |edges: [bool; 4]| {
        let mut world = World::new();
        let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), viewport);
        let mut panel = canvas_node(&mut world, "Panel", CanvasTransform::default(), viewport);
        let [left, top, right, bottom] = edges;
        add(&mut world, &mut panel, ComponentKind::CanvasSafeArea, CanvasSafeAreaComponent { enabled: true, left, top, right, bottom });
        panel.add_child(sprite_node(&mut world, "TopLeft", CanvasTransform::default(), [10.0, 10.0]));
        panel.add_child(sprite_node(&mut world, "BottomRight", CanvasTransform { anchor: [1.0, 1.0], ..CanvasTransform::default() }, [10.0, 10.0]));
        root.add_child(panel);
        let roots = vec![root];
        let table = play_table(&roots, &world, viewport, CanvasScreenEnv { dp_scale: 1.0, safe_area: Some(safe) });
        (roots, table)
    };
    let (roots, table) = build([true; 4]);
    let panel = placed(&table, &roots, "Panel");
    assert!(near(panel.eff_size, [1080.0, 2201.0]), "{:?}", panel.eff_size);
    assert!(near(origin(placed(&table, &roots, "TopLeft")), [-540.0, -1064.0]), "左上は安全領域の左上");
    assert!(near(origin(placed(&table, &roots, "BottomRight")), [540.0, 1137.0]), "右下のアンカーは安全領域の右下");
    assert_eq!(table.stats.safe_areas, 1);

    // 下の辺を外すと、下は画面の端のまま
    let (roots, table) = build([true, true, true, false]);
    assert!(near(origin(placed(&table, &roots, "BottomRight")), [540.0, 1200.0]));
}

/// 画面を回した（横画面で切り欠きが左）ときも同じ規則。安全領域の無い文脈・部品が無効なら縮めない。
#[test]
fn safe_area_follows_rotation_and_is_inert_without_screen() {
    let viewport = [2400.0, 1080.0];
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), viewport);
    let mut panel = canvas_node(&mut world, "Panel", CanvasTransform::default(), viewport);
    add(&mut world, &mut panel, ComponentKind::CanvasSafeArea, CanvasSafeAreaComponent::default());
    panel.add_child(sprite_node(&mut world, "TopLeft", CanvasTransform::default(), [10.0, 10.0]));
    root.add_child(panel);
    let roots = vec![root];
    let landscape = CanvasRect { min: [-1200.0 + 136.0, -540.0], max: [1200.0 - 63.0, 540.0] };
    let table = play_table(&roots, &world, viewport, CanvasScreenEnv { dp_scale: 1.0, safe_area: Some(landscape) });
    assert!(near(origin(placed(&table, &roots, "TopLeft")), [-1064.0, -540.0]));
    assert!(near(placed(&table, &roots, "Panel").eff_size, [2201.0, 1080.0]));
    // 画面の情報が無い（エディタの Edit・設計空間の表示）なら縮めない
    let table = play_table(&roots, &world, viewport, CanvasScreenEnv::NONE);
    assert!(near(origin(placed(&table, &roots, "TopLeft")), [-1200.0, -540.0]));
    assert_eq!(table.stats.safe_areas, 0);
}

// ─── dp ───────────────────────────────────────────────────

/// dp のルート: Pixel 6a（1080×2400 px・2.625 px/dp）は 411.43×914.29 dp。子の位置・大きさ・アンカーは dp で効く。
#[test]
fn dp_root_converts_units() {
    let viewport = [1080.0, 2400.0];
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), [540.0, 1200.0]);
    let slot = root.slots()[0].entity;
    world.get_mut::<CanvasComponent>(slot).unwrap().unit = CanvasUnit::Dp;
    world.get_mut::<CanvasComponent>(slot).unwrap().auto_scale = true;
    root.add_child(sprite_node(&mut world, "Icon", CanvasTransform { position: [16.0, 24.0], ..CanvasTransform::default() }, [48.0, 48.0]));
    root.add_child(sprite_node(&mut world, "Corner", CanvasTransform { anchor: [1.0, 1.0], position: [-48.0, -48.0], ..CanvasTransform::default() }, [48.0, 48.0]));
    let roots = vec![root];
    let table = play_table(&roots, &world, viewport, CanvasScreenEnv { dp_scale: 2.625, safe_area: None });
    let root_p = placed(&table, &roots, "Root");
    let base = root_p.canvas_base.unwrap();
    assert!((base[0] - 411.428_57).abs() < EPS && (base[1] - 914.285_7).abs() < EPS, "{base:?}");
    assert_eq!(root_p.eff_size, viewport, "ルートの矩形は画面の画素");
    let icon = placed(&table, &roots, "Icon");
    assert!(near(origin(icon), [-540.0 + 16.0 * 2.625, -1200.0 + 24.0 * 2.625]), "{:?}", origin(icon));
    assert_eq!(icon.sprite_size(48.0, 48.0), [126.0, 126.0], "48 dp = 126 px");
    assert!(near(origin(placed(&table, &roots, "Corner")), [540.0 - 126.0, 1200.0 - 126.0]), "右下のアンカーは画面の右下");
    // PC（1 dp = 1 px）の同じシーン: 画面 540×1200 なら 540×1200 dp
    let table = play_table(&roots, &world, [540.0, 1200.0], CanvasScreenEnv::NONE);
    assert_eq!(placed(&table, &roots, "Root").canvas_base, Some([540.0, 1200.0]));
}

/// px のルート（既定）と部品の無い木は、画面の情報（dp の倍率・安全領域）があっても表がまったく変わらない。
#[test]
fn px_scenes_ignore_the_screen_env() {
    let viewport = [1280.0, 720.0];
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), [1920.0, 1080.0]);
    let slot = root.slots()[0].entity;
    world.get_mut::<CanvasComponent>(slot).unwrap().auto_scale = true;
    let mut panel = canvas_node(&mut world, "Panel", CanvasTransform { anchor: [0.5, 0.5], position: [-100.0, -50.0], ..CanvasTransform::default() }, [200.0, 100.0]);
    panel.add_child(sprite_node(&mut world, "S", CanvasTransform { position: [3.0, 4.0], rotation: 15.0, ..CanvasTransform::default() }, [30.0, 20.0]));
    root.add_child(panel);
    let roots = vec![root];
    let none = play_table(&roots, &world, viewport, CanvasScreenEnv::NONE);
    let screen = CanvasScreenEnv { dp_scale: 2.625, safe_area: Some(CanvasRect { min: [-600.0, -300.0], max: [600.0, 300.0] }) };
    let with_screen = play_table(&roots, &world, viewport, screen);
    assert_eq!(none, with_screen, "px のルート・部品なしの木は画面の情報で変わらない");
    assert!(none.iter_with_actors(&roots).all(|(n, _)| n.placement().is_none_or(|p| !p.layout_adjusted)));
}

// ─── 性能（測る回数がノード数に比例） ─────────────────────

/// 入れ子のコンテナの木（深さ 6・枝 4）を作る。葉はスプライト、途中は交互に縦・横の列（中身に合わせる）。
fn nested_tree(world: &mut World, depth: u32, branch: usize, count: &mut usize) -> Actor {
    *count += 1;
    if depth == 0 {
        return sprite_node(world, "Leaf", CanvasTransform::default(), [10.0, 10.0]);
    }
    let mut node = bare_node(world, "Box");
    let direction = if depth % 2 == 0 { LayoutDirection::Vertical } else { LayoutDirection::Horizontal };
    add_stack(world, &mut node, CanvasStackComponent { direction, spacing: 1.0, cross_align: CrossAlign::Stretch, ..vstack() });
    for i in 0..branch {
        let mut child = nested_tree(world, depth - 1, branch, count);
        if i == 0 {
            add_item(world, &mut child, CanvasLayoutItemComponent { flex: 1.0, align_self: ItemAlign::Center, ..CanvasLayoutItemComponent::default() });
        }
        node.add_child(child);
    }
    node
}

/// 測る回数はノード数に比例する（同じノードを同じ条件で 2 度測らない）。深さ 5 と 6 で比が同じで、1 ノードあたり 2 回以下。
#[test]
fn measure_calls_are_linear_in_node_count() {
    let mut ratios = Vec::new();
    for depth in [5u32, 6] {
        let mut world = World::new();
        let mut count = 0;
        let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), [4000.0, 4000.0]);
        add_stack(&mut world, &mut root, CanvasStackComponent { cross_align: CrossAlign::Stretch, ..vstack() });
        root.add_child(nested_tree(&mut world, depth, 4, &mut count));
        let roots = vec![root];
        let started = std::time::Instant::now();
        let table = table_of(&roots, &world);
        let elapsed = started.elapsed();
        let calls = table.stats.measure_calls;
        eprintln!("[W2-1b 性能] 深さ {depth}: ノード {count} 個・測った回数 {calls}・コンテナ {}・走査 {:.2} ms", table.stats.containers, elapsed.as_secs_f64() * 1000.0);
        assert!(calls as usize <= 2 * count, "1 ノードあたり 2 回以下: {calls} / {count}");
        ratios.push(calls as f64 / count as f64);
    }
    assert!((ratios[1] - ratios[0]).abs() < 0.25, "ノードが 4 倍でも 1 ノードあたりの回数はほぼ同じ: {ratios:?}");
}

// ─── 見た目の上書き（W2-7: 平行移動・レイヤーの底上げ）─────────────

/// 親に合わせた画面を translate_fraction で右へずらす（右から入ってくる途中）。大きさ・子の並びは変わらず、子孫が付いてくる。
#[test]
fn translate_fraction_moves_filled_node_and_descendants() {
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), [400.0, 800.0]);
    let mut screen = canvas_node(&mut world, "Screen", CanvasTransform::default(), [1.0, 1.0]);
    add_item(&mut world, &mut screen, CanvasLayoutItemComponent { fill_width: true, fill_height: true, translate_fraction: [0.25, 0.0], ..CanvasLayoutItemComponent::default() });
    add_stack(&mut world, &mut screen, CanvasStackComponent { padding: CanvasPadding { left: 10.0, top: 20.0, right: 0.0, bottom: 0.0 }, ..vstack() });
    screen.add_child(sprite_node(&mut world, "Row", CanvasTransform::default(), [100.0, 40.0]));
    root.add_child(screen);
    let roots = vec![root];
    let table = table_of(&roots, &world);
    let s = placed(&table, &roots, "Screen");
    assert!(near(origin(s), [100.0, 0.0]), "幅 400 の 0.25 = 100 だけ右: {:?}", origin(s));
    assert_eq!(s.sprite_size(1.0, 1.0), [400.0, 800.0], "大きさはレイアウトのまま");
    assert!(s.layout_adjusted);
    assert!(near(origin(placed(&table, &roots, "Row")), [110.0, 20.0]), "子はずらした親の中の同じ位置");
    assert_eq!(table.stats.translated, 1);
}

/// translate（キャンバスの単位）は親の累積スケールを掛けて画素にし、割合（自分のキャンバスの領域に対する）と足し合わせる。
#[test]
fn translate_units_scale_with_parent_and_add_to_fraction() {
    // 累積スケール 2 の親（scale 2 のキャンバス）の下: translate 40 → 80 画素
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), [800.0, 800.0]);
    let mut scaled = canvas_node(&mut world, "Scaled", CanvasTransform { scale: [2.0, 2.0], ..CanvasTransform::default() }, [400.0, 400.0]);
    let mut toast = sprite_node(&mut world, "Toast", CanvasTransform { position: [0.0, 100.0], ..CanvasTransform::default() }, [200.0, 50.0]);
    add_item(&mut world, &mut toast, CanvasLayoutItemComponent { translate: [40.0, 0.0], ..CanvasLayoutItemComponent::default() });
    scaled.add_child(toast);
    root.add_child(scaled);
    let roots = vec![root];
    let table = table_of(&roots, &world);
    let base = origin(placed(&table, &roots, "Scaled"));
    let t = origin(placed(&table, &roots, "Toast"));
    assert!(near([t[0] - base[0], t[1] - base[1]], [80.0, 200.0]), "位置 (0,100) と translate (40,0) が 2 倍: {base:?} → {t:?}");

    // 割合はキャンバスの領域（200×100）に対する割合。単位と足し合わせる
    let mut world2 = World::new();
    let mut root2 = canvas_node(&mut world2, "Root", CanvasTransform::default(), [400.0, 800.0]);
    let mut panel = canvas_node(&mut world2, "Panel", CanvasTransform::default(), [200.0, 100.0]);
    add_item(&mut world2, &mut panel, CanvasLayoutItemComponent { translate: [5.0, 0.0], translate_fraction: [0.5, -1.0], ..CanvasLayoutItemComponent::default() });
    root2.add_child(panel);
    let roots2 = vec![root2];
    let table2 = table_of(&roots2, &world2);
    assert!(near(origin(placed(&table2, &roots2, "Panel")), [105.0, -100.0]), "{:?}", origin(placed(&table2, &roots2, "Panel")));
}

/// 何も上書きしないノード（CanvasLayoutItem なし・値が 0）の表は従来とまったく同じ（行列も底上げも）。
#[test]
fn zero_visual_overrides_leave_table_unchanged() {
    let build = |item: Option<CanvasLayoutItemComponent>| {
        let mut world = World::new();
        let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), [400.0, 800.0]);
        let mut panel = canvas_node(&mut world, "Panel", CanvasTransform { position: [3.0, 7.0], rotation: 15.0, ..CanvasTransform::default() }, [200.0, 100.0]);
        if let Some(item) = item {
            add_item(&mut world, &mut panel, item);
        }
        panel.add_child(sprite_node(&mut world, "Child", CanvasTransform { position: [1.0, 2.0], ..CanvasTransform::default() }, [10.0, 10.0]));
        root.add_child(panel);
        let roots = vec![root];
        let table = table_of(&roots, &world);
        (roots, table)
    };
    let (roots_a, a) = build(None);
    let (roots_b, b) = build(Some(CanvasLayoutItemComponent::default()));
    for name in ["Root", "Panel", "Child"] {
        let (pa, pb) = (placed(&a, &roots_a, name), placed(&b, &roots_b, name));
        assert_eq!(pa.world_rs, pb.world_rs, "{name} の行列");
        assert_eq!(pa.layer_bias, 0);
        assert_eq!(pb.layer_bias, 0);
    }
    assert_eq!(b.stats.translated, 0);
}

/// レイヤーの底上げは祖先から足し合わせて子孫へ伝わる。兄弟には伝わらない。
#[test]
fn layer_bias_accumulates_down_the_tree() {
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), [400.0, 800.0]);
    let mut modal = canvas_node(&mut world, "Modal", CanvasTransform::default(), [400.0, 800.0]);
    add_item(&mut world, &mut modal, CanvasLayoutItemComponent { layer_bias: 3_000_000, ..CanvasLayoutItemComponent::default() });
    let mut card = canvas_node(&mut world, "Card", CanvasTransform::default(), [200.0, 100.0]);
    add_item(&mut world, &mut card, CanvasLayoutItemComponent { layer_bias: 10, ..CanvasLayoutItemComponent::default() });
    card.add_child(sprite_node(&mut world, "Label", CanvasTransform::default(), [50.0, 20.0]));
    modal.add_child(card);
    root.add_child(modal);
    root.add_child(sprite_node(&mut world, "Sibling", CanvasTransform::default(), [10.0, 10.0]));
    let roots = vec![root];
    let table = table_of(&roots, &world);
    assert_eq!(placed(&table, &roots, "Root").layer_bias, 0);
    assert_eq!(placed(&table, &roots, "Modal").layer_bias, 3_000_000);
    assert_eq!(placed(&table, &roots, "Card").layer_bias, 3_000_010);
    assert_eq!(placed(&table, &roots, "Label").layer_bias, 3_000_010, "子孫は祖先の和");
    assert_eq!(placed(&table, &roots, "Sibling").layer_bias, 0, "兄弟には伝わらない");
    assert_eq!(biased_layer(5, placed(&table, &roots, "Label").layer_bias), 3_000_015);
    assert_eq!(biased_layer(i32::MAX, 10), i32::MAX, "飽和する");
}

/// 横から入ってくる画面の中の安全領域の箱は、ずらす前の位置で縮める（途中で右の辺が画面の外へ出ても縮み直さない）。
#[test]
fn safe_area_inside_translated_node_ignores_translation() {
    let viewport = [1080.0, 2400.0];
    let safe = CanvasRect { min: [-540.0, -1064.0], max: [540.0, 1137.0] };
    let build = |fraction: f32| {
        let mut world = World::new();
        let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), viewport);
        let mut frame = canvas_node(&mut world, "Frame", CanvasTransform::default(), [1.0, 1.0]);
        add_item(&mut world, &mut frame, CanvasLayoutItemComponent { fill_width: true, fill_height: true, translate_fraction: [fraction, 0.0], ..CanvasLayoutItemComponent::default() });
        let mut body = canvas_node(&mut world, "Body", CanvasTransform::default(), [1.0, 1.0]);
        add_item(&mut world, &mut body, CanvasLayoutItemComponent { fill_width: true, fill_height: true, ..CanvasLayoutItemComponent::default() });
        add(&mut world, &mut body, ComponentKind::CanvasSafeArea, CanvasSafeAreaComponent { enabled: true, left: true, top: true, right: true, bottom: true });
        body.add_child(sprite_node(&mut world, "BottomRight", CanvasTransform { anchor: [1.0, 1.0], ..CanvasTransform::default() }, [10.0, 10.0]));
        frame.add_child(body);
        root.add_child(frame);
        let roots = vec![root];
        let table = play_table(&roots, &world, viewport, CanvasScreenEnv { dp_scale: 1.0, safe_area: Some(safe) });
        let body = placed(&table, &roots, "Body").eff_size;
        let br = origin(placed(&table, &roots, "BottomRight"));
        (body, br)
    };
    let (body0, br0) = build(0.0);
    let (body_half, br_half) = build(0.5);
    assert!(near(body0, [1080.0, 2201.0]), "{body0:?}");
    assert!(near(body_half, body0), "半分ずらしても箱の大きさは同じ: {body_half:?}");
    assert!(near(br_half, [br0[0] + 540.0, br0[1]]), "中身はずらした分だけ動くだけ: {br0:?} → {br_half:?}");
}
