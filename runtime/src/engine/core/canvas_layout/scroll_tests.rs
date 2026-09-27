// ============================================================
//  canvas_layout/scroll_tests.rs — スクロールの窓の走査のテスト（W2-3。World とアクター木を使う）
//
//  中身をスクロールの位置だけずらす・窓と中身の大きさの記録・見える範囲の外を飛ばす（1,000 行の一覧）・
//  スクロールの窓でもあるコンテナ・dp のキャンバスでの換算を、表（CanvasLayoutTable）の値で確かめる。
//  物理のテストは canvas_scroll/tests.rs、当たり判定との組み合わせは app/gesture_scene.rs のテスト。
//
//  座標の読み方: ノードの左上（キャンバスのワールド座標）は、表の行の配置の world_rs の平行移動（[0][3]・[1][3]）。
// ============================================================

use std::collections::HashMap;

use super::*;
use crate::engine::components::{
    CanvasClipComponent, CanvasComponent, CanvasDrawZone, CanvasScrollComponent, CanvasStackComponent, CanvasTransform,
    CanvasUnit, ComponentKind, LayoutDirection, ScrollContentSize, SpriteComponent,
};
use crate::engine::core::canvas_scroll::CanvasScrollState;
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

/// 浮動小数の比較の許容量（画素）。
const EPS: f32 = 1e-3;
/// 窓の左上（ルートのローカル）。
const WINDOW_ORIGIN: [f32; 2] = [50.0, 100.0];
/// 窓の大きさ。
const WINDOW_SIZE: [f32; 2] = [300.0, 400.0];
/// 行の高さ。
const ROW_HEIGHT: f32 = 40.0;
/// 見える範囲の外の余白の既定（CanvasScrollComponent の既定 cache_extent）。
const CACHE: f32 = 250.0;

/// キャンバスのノード（CanvasTransform + CanvasComponent）。
fn canvas_node(world: &mut World, name: &str, position: [f32; 2], size: [f32; 2]) -> Actor {
    let entity = world.spawn();
    world.insert(entity, CanvasTransform { position, ..CanvasTransform::default() });
    let mut actor = Actor::new_2d(entity, name);
    let slot = world.spawn();
    world.insert(slot, CanvasComponent { width: size[0], height: size[1], auto_scale: false, ..CanvasComponent::default() });
    actor.add_slot_typed::<CanvasComponent>("Canvas", ComponentKind::Canvas, slot);
    actor
}

/// スプライトのノード。
fn sprite_node(world: &mut World, name: &str, position: [f32; 2], size: [f32; 2]) -> Actor {
    let entity = world.spawn();
    world.insert(entity, CanvasTransform { position, ..CanvasTransform::default() });
    let mut actor = Actor::new_2d(entity, name);
    let slot = world.spawn();
    world.insert(slot, SpriteComponent { width: size[0], height: size[1], ..SpriteComponent::default() });
    actor.add_slot_typed::<SpriteComponent>("Sprite", ComponentKind::Sprite, slot);
    actor
}

/// コンポーネントのスロットを足し、スロットのエンティティを返す。
fn add<T: crate::engine::ecs::Component>(world: &mut World, actor: &mut Actor, kind: ComponentKind, value: T) -> Entity {
    let slot = world.spawn();
    world.insert(slot, value);
    actor.add_slot_typed::<T>("Slot", kind, slot);
    slot
}

/// ルート（800×600）の下に、切り抜きとスクロールの窓（300×400）と行（300×40 を縦に）を置いた木。
///
/// # 戻り値
/// (ルートの並び, World, スクロールのスロットのエンティティ)
fn list_scene(rows: usize, settings: CanvasScrollComponent, clip: bool) -> (Vec<Actor>, World, Entity) {
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", [0.0, 0.0], [800.0, 600.0]);
    let mut window = canvas_node(&mut world, "Window", WINDOW_ORIGIN, WINDOW_SIZE);
    if clip {
        add(&mut world, &mut window, ComponentKind::CanvasClip, CanvasClipComponent::default());
    }
    let slot = add(&mut world, &mut window, ComponentKind::CanvasScroll, settings);
    for i in 0..rows {
        let mut row = sprite_node(&mut world, &format!("Row{i}"), [0.0, i as f32 * ROW_HEIGHT], [WINDOW_SIZE[0], ROW_HEIGHT]);
        // 行の中の子（文字の代わりの点のノード）も一緒に飛ぶこと
        let label_entity = world.spawn();
        world.insert(label_entity, CanvasTransform { position: [8.0, LABEL_OFFSET_Y], ..CanvasTransform::default() });
        row.children_mut().push(Actor::new_2d(label_entity, &format!("Label{i}")));
        window.children_mut().push(row);
    }
    root.children_mut().push(window);
    (vec![root], world, slot)
}

/// 何の文脈も使わない表（ルートの左上が原点）。
fn table_of(roots: &[Actor], world: &World) -> CanvasLayoutTable {
    let empty: HashMap<Entity, [f32; 2]> = HashMap::new();
    let env = CanvasLayoutEnv::without_viewport(&empty, AutoScaleDivisor::Raw);
    CanvasLayoutPass::run(roots, world, 0, CanvasParentFrame::viewport_root(CanvasDrawZone::Foreground), &env)
}

/// 名前で表の行を引く。
fn row_of<'t>(table: &'t CanvasLayoutTable, roots: &[Actor], name: &str) -> &'t CanvasLayoutNode {
    table.iter_with_actors(roots).find(|(_, a)| a.name == name).map(|(n, _)| n).expect("行がある")
}

/// ノードの左上（キャンバスのワールド座標）。
fn origin(node: &CanvasLayoutNode) -> [f32; 2] {
    let m = node.placement().expect("配置がある").world_rs;
    [m[0][3], m[1][3]]
}

/// 位置を書く（スクロールのシステムの代わり）。
fn set_position(world: &mut World, slot: Entity, y: f64) {
    world.insert(slot, CanvasScrollState { position: [0.0, y], ..CanvasScrollState::default() });
}

/// 子はスクロールの位置だけずれ、窓と中身の大きさ・換算・当てた平行移動が表に残る。窓自身は動かない。
#[test]
fn scroll_offsets_children_and_reports_region() {
    let (roots, mut world, slot) = list_scene(100, CanvasScrollComponent::default(), true);
    set_position(&mut world, slot, 120.0);
    let table = table_of(&roots, &world);
    let window = row_of(&table, &roots, "Window");
    assert_eq!(origin(window), WINDOW_ORIGIN, "窓は動かない");
    let row5 = row_of(&table, &roots, "Row5");
    assert!((origin(row5)[1] - (WINDOW_ORIGIN[1] + 5.0 * ROW_HEIGHT - 120.0)).abs() < EPS);
    assert!((origin(row5)[0] - WINDOW_ORIGIN[0]).abs() < EPS, "横はずれない");
    assert_eq!(table.scroll_regions.len(), 1);
    let region = &table.scroll_regions[0];
    assert_eq!(region.slot, slot);
    assert_eq!(region.viewport_px, WINDOW_SIZE);
    assert_eq!(region.content_px, [WINDOW_SIZE[0], 100.0 * ROW_HEIGHT], "中身は行のいちばん遠い下端");
    assert_eq!(region.px_per_unit, [1.0, 1.0]);
    assert_eq!(region.offset_px, [0.0, 120.0]);
    assert_eq!(table.stats.scrolls, 1);
    // 状態が無い（エディタの Edit）ときはずれない
    world.remove::<CanvasScrollState>(slot);
    let table = table_of(&roots, &world);
    assert!((origin(row_of(&table, &roots, "Row5"))[1] - (WINDOW_ORIGIN[1] + 5.0 * ROW_HEIGHT)).abs() < EPS);
}

/// 見える範囲（窓の切り抜き + 250。キャンバスの Y の上端・下端）。
const VIEW_TOP: f32 = WINDOW_ORIGIN[1] - CACHE;
/// 見える範囲の下端。
const VIEW_BOTTOM: f32 = WINDOW_ORIGIN[1] + WINDOW_SIZE[1] + CACHE;
/// 行の子（点のノード）の行の中の位置。
const LABEL_OFFSET_Y: f32 = 10.0;

/// 窓の見える範囲と交わる行だけが残る。
fn expected_visible_rows(rows: usize, position: f32) -> Vec<usize> {
    (0..rows)
        .filter(|&i| {
            let top = WINDOW_ORIGIN[1] + i as f32 * ROW_HEIGHT - position;
            top <= VIEW_BOTTOM && top + ROW_HEIGHT >= VIEW_TOP
        })
        .collect()
}

/// 残った行の子（点）が見える範囲の中か（範囲の端で行だけが交わると、点の子は外として飛ぶ）。
fn label_visible(i: usize, position: f32) -> bool {
    let y = WINDOW_ORIGIN[1] + i as f32 * ROW_HEIGHT - position + LABEL_OFFSET_Y;
    (VIEW_TOP..=VIEW_BOTTOM).contains(&y)
}

/// 1,000 行の一覧: 見える範囲の外の行（と行の子）は飛ばされ、描画アイテム・当たり判定の対象は見えている行の分だけ。
#[test]
fn thousand_row_list_culls_rows_outside_the_view() {
    const ROWS: usize = 1000;
    for &position in &[0.0f32, 20_000.0, 39_600.0] {
        let (roots, mut world, slot) = list_scene(ROWS, CanvasScrollComponent::default(), true);
        set_position(&mut world, slot, f64::from(position));
        let table = table_of(&roots, &world);
        let expected = expected_visible_rows(ROWS, position);
        let visible: Vec<usize> = (0..ROWS)
            .filter(|i| row_of(&table, &roots, &format!("Row{i}")).is_drawn_in_view())
            .collect();
        assert_eq!(visible, expected, "位置 {position}");
        assert!(expected.len() < 30, "見えている行は窓 + 余白の分だけ: {}", expected.len());
        // 飛ばした行は子も一緒に飛ぶ。残った行の子は、点が見える範囲の中のときだけ残る
        let labels: Vec<usize> = expected.iter().copied().filter(|&i| label_visible(i, position)).collect();
        for &i in &expected {
            assert_eq!(row_of(&table, &roots, &format!("Label{i}")).is_drawn_in_view(), label_visible(i, position));
        }
        let kept = expected.len() + labels.len();
        assert_eq!(table.stats.culled as usize, ROWS * 2 - kept, "位置 {position}");
        // 窓と祖先は飛ばない
        assert!(row_of(&table, &roots, "Window").is_drawn_in_view());
        assert!(row_of(&table, &roots, "Root").is_drawn_in_view());
        // 当たり判定も同じ（飛ばした行は対象外）
        let pickable = table.nodes.iter().filter(|n| n.is_pickable_in_view(true)).count();
        assert_eq!(pickable, 2 + kept);
    }
}

/// 飛ばすのは切り抜きと cull_outside が両方有効なときだけ（切り抜きが無ければ窓の外も見えるので飛ばさない）。
#[test]
fn culling_requires_clip_and_setting() {
    let (roots, mut world, slot) = list_scene(200, CanvasScrollComponent::default(), false);
    set_position(&mut world, slot, 0.0);
    assert_eq!(table_of(&roots, &world).stats.culled, 0, "切り抜きが無い");
    let (roots, mut world, slot) =
        list_scene(200, CanvasScrollComponent { cull_outside: false, ..CanvasScrollComponent::default() }, true);
    set_position(&mut world, slot, 0.0);
    assert_eq!(table_of(&roots, &world).stats.culled, 0, "cull_outside = false");
    // 余白 0 なら窓と交わる行だけ（行 0〜10: 11 行目の上端 400 は窓の下端 400 に接する）
    let (roots, mut world, slot) =
        list_scene(200, CanvasScrollComponent { cache_extent: 0.0, ..CanvasScrollComponent::default() }, true);
    set_position(&mut world, slot, 0.0);
    let table = table_of(&roots, &world);
    let visible = (0..200).filter(|i| row_of(&table, &roots, &format!("Row{i}")).is_drawn_in_view()).count();
    assert_eq!(visible, 11);
}

/// Fixed の中身の大きさ（仮想化の一覧: 行は見える分しか無いが、全体の長さは数で決める）。
#[test]
fn fixed_content_size_is_reported() {
    let settings = CanvasScrollComponent {
        content_size: ScrollContentSize::Fixed,
        content_width: 300.0,
        content_height: 72_000.0,
        ..CanvasScrollComponent::default()
    };
    let (roots, world, _) = list_scene(5, settings, true);
    let table = table_of(&roots, &world);
    assert_eq!(table.scroll_regions[0].content_px, [300.0, 72_000.0]);
}

/// スクロールの窓でもある縦の Stack: スクロールの軸は箱の長さを決めずに並べる（子は縮まず、中身は並べた長さ＋余白）。
#[test]
fn scroll_container_lays_out_unconstrained_along_scroll_axis() {
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", [0.0, 0.0], [800.0, 600.0]);
    let mut window = canvas_node(&mut world, "Window", WINDOW_ORIGIN, WINDOW_SIZE);
    add(&mut world, &mut window, ComponentKind::CanvasClip, CanvasClipComponent::default());
    let slot = add(&mut world, &mut window, ComponentKind::CanvasScroll, CanvasScrollComponent::default());
    add(
        &mut world,
        &mut window,
        ComponentKind::CanvasStack,
        CanvasStackComponent {
            direction: LayoutDirection::Vertical,
            spacing: 10.0,
            padding: crate::engine::components::CanvasPadding { left: 0.0, top: 20.0, right: 0.0, bottom: 30.0 },
            ..CanvasStackComponent::default()
        },
    );
    for i in 0..50 {
        window.children_mut().push(sprite_node(&mut world, &format!("Item{i}"), [0.0, 0.0], [WINDOW_SIZE[0], ROW_HEIGHT]));
    }
    root.children_mut().push(window);
    let roots = vec![root];
    set_position(&mut world, slot, 100.0);
    let table = table_of(&roots, &world);
    // 並べた長さ: 余白 20 + 50 × 40 + 49 × 10 + 余白 30
    let content = 20.0 + 50.0 * ROW_HEIGHT + 49.0 * 10.0 + 30.0;
    assert!((table.scroll_regions[0].content_px[1] - content).abs() < EPS, "{:?}", table.scroll_regions[0].content_px);
    // 子は縮まず（40 のまま）、スクロールの位置だけずれて並ぶ
    let item3 = row_of(&table, &roots, "Item3");
    assert!((origin(item3)[1] - (WINDOW_ORIGIN[1] + 20.0 + 3.0 * (ROW_HEIGHT + 10.0) - 100.0)).abs() < EPS);
}

/// dp のルートキャンバス: 位置・大きさはキャンバスの単位（dp）で、画素へは 1 dp の画素数を掛ける。
#[test]
fn dp_canvas_scroll_converts_units() {
    let dp_scale = 2.0;
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", [0.0, 0.0], [400.0, 800.0]);
    if let Some(slot) = root.slots().iter().find(|s| s.kind == ComponentKind::Canvas).map(|s| s.entity) {
        if let Some(cc) = world.get_mut::<CanvasComponent>(slot) {
            cc.unit = CanvasUnit::Dp;
        }
    }
    let mut window = canvas_node(&mut world, "Window", [10.0, 20.0], [200.0, 300.0]);
    add(&mut world, &mut window, ComponentKind::CanvasClip, CanvasClipComponent::default());
    let slot = add(&mut world, &mut window, ComponentKind::CanvasScroll, CanvasScrollComponent::default());
    for i in 0..30 {
        window.children_mut().push(sprite_node(&mut world, &format!("Row{i}"), [0.0, i as f32 * ROW_HEIGHT], [200.0, ROW_HEIGHT]));
    }
    root.children_mut().push(window);
    let roots = vec![root];
    set_position(&mut world, slot, 50.0);
    let empty: HashMap<Entity, [f32; 2]> = HashMap::new();
    let env = CanvasLayoutEnv {
        viewport_size: Some([800.0, 1600.0]),
        viewport_overrides: &empty,
        root_auto_sizes: &empty,
        design_space: false,
        auto_scale_divisor: AutoScaleDivisor::Raw,
        screen: CanvasScreenEnv { dp_scale, safe_area: None },
    };
    let table = CanvasLayoutPass::run(&roots, &world, 0, CanvasParentFrame::viewport_root(CanvasDrawZone::Foreground), &env);
    let region = &table.scroll_regions[0];
    assert_eq!(region.px_per_unit, [dp_scale, dp_scale]);
    assert_eq!(region.viewport_px, [200.0 * dp_scale, 300.0 * dp_scale]);
    assert_eq!(region.content_px[1], 30.0 * ROW_HEIGHT * dp_scale);
    assert_eq!(region.offset_px, [0.0, 50.0 * dp_scale], "50 dp = 100 px");
    // 行 2 の上端は窓の上端 + (2 × 40 − 50) dp
    let window_y = origin(row_of(&table, &roots, "Window"))[1];
    let row2_y = origin(row_of(&table, &roots, "Row2"))[1];
    assert!((row2_y - window_y - (2.0 * ROW_HEIGHT - 50.0) * dp_scale).abs() < EPS);
}
