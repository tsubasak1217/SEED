// ============================================================
//  canvas_layout/results_tests.rs — レイアウトの結果（W2 Item 4）のテスト
//
//  スクリプトの CanvasTransform.HasLayout・LayoutSize・LayoutRect の中身（results.rs・node_extent.rs）を、
//  layout_tests.rs の木を作る道具で作った表で確かめる:
//    - レイアウトが伸ばしたノード（コンテナの Stretch・伸ばす重み・親に合わせる）の大きさ ＝ レイアウトの幅 ÷ サイズ倍率、
//      大きさ × サイズ倍率 ＝ 描かれるスプライトの大きさ、画面の矩形 ＝ 描画のスプライトの 4 隅（canvas_collect.rs と同じ計算）
//    - dp のルート＋安全領域の下のノードの画面の矩形（Pixel 6a 相当: 1080×2400 px・2.625 px/dp・上 136・下 63）
//    - レイアウトの無いノード（Sprite・Text の枠・何も無い・回転）
//    - 表に無い・見せない行（3D ワールドキャンバスの下・3D アクター・フォルダ・世界線が違う・知らない Entity）
//    - 索引は読むまで作らない・描画からの受け渡し（Publish・Keep・Clear）とフレームの画面の分け方
//  レジストリ（host_api の read_floats）からの往復は host_api.rs のテスト。
// ============================================================

use std::sync::Arc;

use super::layout_tests::{add, add_item, add_stack, bare_node, canvas_node, near, play_table, sprite_node, vstack};
use super::*;
use crate::engine::components::{
    CanvasComponent, CanvasLayoutItemComponent, CanvasPadding, CanvasSafeAreaComponent, CanvasStackComponent,
    CanvasTransform, CanvasUnit, ComponentKind, CrossAlign, TextComponent,
};
use crate::engine::ecs::World;
use crate::engine::methods::gizmo_interact::mat4x4_mul;
use crate::engine::structs::objects::Actor;

/// 浮動小数の比較の許容量（画素・単位）。
const EPS: f32 = 1e-3;

/// 横長のテスト用のビューポート（画素）。
const VIEWPORT: [f32; 2] = [800.0, 600.0];

/// Pixel 6a の縦画面（画素）。
const PIXEL_6A: [f32; 2] = [1080.0, 2400.0];

/// Pixel 6a の 1 dp の画素数（420 dpi ÷ 160）。
const PIXEL_6A_DP: f32 = 2.625;

/// 単位クワッドの 4 隅（描画のスプライトの頂点。renderer のユニットクワッドと同じ）。
const UNIT_QUAD: [[f32; 2]; 4] = [[0.0, 0.0], [1.0, 0.0], [0.0, 1.0], [1.0, 1.0]];

/// ビューポートの半分（キャンバスのワールド座標〈中央が原点〉→ 画面の画素〈左上が原点〉）。
const HALF: f32 = 0.5;

// ─── 道具 ─────────────────────────────────────────────────

/// 木から名前でアクターを探す（深さ優先）。
fn find<'a>(actors: &'a [Actor], name: &str) -> Option<&'a Actor> {
    for actor in actors {
        if actor.name == name {
            return Some(actor);
        }
        if let Some(found) = find(actor.children(), name) {
            return Some(found);
        }
    }
    None
}

/// 木から名前でアクターを引く（無ければテストの失敗）。
fn actor<'a>(roots: &'a [Actor], name: &str) -> &'a Actor {
    find(roots, name).unwrap_or_else(|| panic!("{name} が木に無い"))
}

/// 資源からノードの結果を読む（スクリプトの LayoutSize・LayoutRect と同じ経路。自分の大きさはスロットから）。
fn readout(results: &CanvasLayoutResults, roots: &[Actor], world: &World, name: &str) -> NodeLayoutReadout {
    let a = actor(roots, name);
    results.readout(a.entity, || OwnSize::of(a, world)).unwrap_or_else(|| panic!("{name} の結果が無い"))
}

/// 描画（app/canvas_collect.rs の collect_node_draw_items）とまったく同じ計算で、スプライトの 4 隅の外接矩形を
/// 画面の画素（x, y, 幅, 高さ）で求める: 親の行列 × 有効トランスフォームの to_sprite_mat4(描く大きさ) で単位クワッドを写す。
fn drawn_sprite_screen_rect(table: &CanvasLayoutTable, roots: &[Actor], name: &str, sprite: [f32; 2], viewport: [f32; 2]) -> [f32; 4] {
    let (node, _) = table.iter_with_actors(roots).find(|(_, a)| a.name == name).unwrap_or_else(|| panic!("{name} が表に無い"));
    let placement = node.placement().expect("配置を持つ");
    let [w, h] = placement.sprite_size(sprite[0], sprite[1]);
    let m = mat4x4_mul(node.frame.world_rs, placement.eff_transform.to_sprite_mat4(w, h));
    let mut min = [f32::INFINITY; 2];
    let mut max = [f32::NEG_INFINITY; 2];
    for [u, v] in UNIT_QUAD {
        let x = m[0][0] * u + m[0][1] * v + m[0][3];
        let y = m[1][0] * u + m[1][1] * v + m[1][3];
        min = [min[0].min(x), min[1].min(y)];
        max = [max[0].max(x), max[1].max(y)];
    }
    [min[0] + viewport[0] * HALF, min[1] + viewport[1] * HALF, max[0] - min[0], max[1] - min[1]]
}

/// 2 つの矩形（x, y, 幅, 高さ）がほぼ等しいか。
fn near_rect(a: [f32; 4], b: [f32; 4]) -> bool {
    a.iter().zip(b.iter()).all(|(x, y)| (x - y).abs() < EPS)
}

/// 行の配置を名前で引く。
fn placement_of<'t>(table: &'t CanvasLayoutTable, roots: &[Actor], name: &str) -> &'t CanvasNodePlacement {
    table
        .iter_with_actors(roots)
        .find(|(_, a)| a.name == name)
        .and_then(|(n, _)| n.placement())
        .unwrap_or_else(|| panic!("{name} が表に無い"))
}

/// スプライトを持つノードの約束: 大きさ × サイズ倍率 ＝ 描かれるスプライトの大きさ、画面の矩形 ＝ 描画のスプライトの 4 隅。
fn assert_matches_drawn_sprite(
    table: &CanvasLayoutTable,
    results: &CanvasLayoutResults,
    roots: &[Actor],
    world: &World,
    name: &str,
    sprite: [f32; 2],
    viewport: [f32; 2],
) {
    let r = readout(results, roots, world, name);
    let p = placement_of(table, roots, name);
    let drawn = p.sprite_size(sprite[0], sprite[1]);
    for axis in 0..2 {
        assert!((r.size[axis] * p.size_scale[axis] - drawn[axis]).abs() < EPS, "{name} の軸 {axis}: {:?} × {:?} ≠ {drawn:?}", r.size, p.size_scale);
    }
    let expect = drawn_sprite_screen_rect(table, roots, name, sprite, viewport);
    assert_eq!(r.screen_rect, expect, "{name} の画面の矩形は描画のスプライトの 4 隅と同じ計算");
}

// ─── レイアウトが伸ばしたノード ─────────────────────────────

/// Stack の交差軸の Stretch・伸ばす重み・親に合わせる（fill）で伸ばされたスプライトのノード（親の累積スケール 2 の下）。
/// 大きさはレイアウトの幅 ÷ サイズ倍率になり、Sprite.Width（50）には依らない。描かれるスプライトと一致する。
#[test]
fn stretched_sprites_report_layout_size_and_drawn_rect() {
    let mut world = World::new();
    // パネル 200×150（scale 2 → 箱は 400×300 画素）。縦の列・交差軸 Stretch・余白 左 10・右 30（画素では 20・60）
    let mut panel = canvas_node(&mut world, "Panel", CanvasTransform { scale: [2.0, 2.0], ..CanvasTransform::default() }, [200.0, 150.0]);
    add_stack(&mut world, &mut panel, CanvasStackComponent {
        cross_align: CrossAlign::Stretch,
        padding: CanvasPadding { left: 10.0, top: 0.0, right: 30.0, bottom: 0.0 },
        ..vstack()
    });
    panel.add_child(sprite_node(&mut world, "Row", CanvasTransform::default(), [50.0, 40.0]));
    let mut body = sprite_node(&mut world, "Body", CanvasTransform::default(), [50.0, 10.0]);
    add_item(&mut world, &mut body, CanvasLayoutItemComponent { flex: 1.0, ..CanvasLayoutItemComponent::default() });
    panel.add_child(body);
    // コンテナに並べさせず、親（パネル）の領域いっぱいに合わせる
    let mut bg = sprite_node(&mut world, "Bg", CanvasTransform::default(), [1.0, 1.0]);
    add_item(&mut world, &mut bg, CanvasLayoutItemComponent { ignore_layout: true, fill_width: true, fill_height: true, ..CanvasLayoutItemComponent::default() });
    panel.add_child(bg);
    let roots = vec![panel];
    let table = Arc::new(play_table(&roots, &world, VIEWPORT, CanvasScreenEnv::NONE));
    let results = CanvasLayoutResults::new(Arc::clone(&table), VIEWPORT);

    // Row: 幅は箱 400 − 余白 80 = 320 画素 → 160 単位。高さは伸ばしていないので Sprite の 40
    let row = readout(&results, &roots, &world, "Row");
    assert!(near(row.size, [160.0, 40.0]), "{:?}", row.size);
    assert!(near_rect(row.screen_rect, [20.0, 0.0, 320.0, 80.0]), "{:?}", row.screen_rect);
    assert_matches_drawn_sprite(&table, &results, &roots, &world, "Row", [50.0, 40.0], VIEWPORT);

    // Body: 主軸は残り 300 − 80 = 220 画素 → 110 単位、交差軸は 160 単位
    let body = readout(&results, &roots, &world, "Body");
    assert!(near(body.size, [160.0, 110.0]), "{:?}", body.size);
    assert_matches_drawn_sprite(&table, &results, &roots, &world, "Body", [50.0, 10.0], VIEWPORT);

    // Bg: 親の箱 400×300 画素 → 200×150 単位（パネルの CanvasComponent の大きさと同じ）
    let bg = readout(&results, &roots, &world, "Bg");
    assert!(near(bg.size, [200.0, 150.0]), "{:?}", bg.size);
    assert!(near_rect(bg.screen_rect, [0.0, 0.0, 400.0, 300.0]), "{:?}", bg.screen_rect);
    assert_matches_drawn_sprite(&table, &results, &roots, &world, "Bg", [1.0, 1.0], VIEWPORT);

    // パネル自身（CanvasComponent）: キャンバスの基準の大きさ・キャンバス領域（scale 2 の見た目の大きさ）
    let panel = readout(&results, &roots, &world, "Panel");
    assert!(near(panel.size, [200.0, 150.0]), "{:?}", panel.size);
    assert!(near_rect(panel.screen_rect, [0.0, 0.0, 400.0, 300.0]), "{:?}", panel.screen_rect);
}

// ─── dp のルート＋安全領域 ───────────────────────────────────

/// dp のルート（Pixel 6a 相当）に安全領域（上 136・下 63）。子の画面の矩形は画素、大きさは dp（矩形 ÷ Screen.DpScale）。
#[test]
fn dp_root_with_safe_area_maps_children_to_pixel_rects() {
    let safe = CanvasRect { min: [-540.0, -1064.0], max: [540.0, 1137.0] };
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), [540.0, 1200.0]);
    let slot = root.slots()[0].entity;
    world.get_mut::<CanvasComponent>(slot).expect("キャンバス").unit = CanvasUnit::Dp;
    add(&mut world, &mut root, ComponentKind::CanvasSafeArea, CanvasSafeAreaComponent { enabled: true, left: true, top: true, right: true, bottom: true });
    root.add_child(sprite_node(&mut world, "Icon", CanvasTransform { position: [16.0, 24.0], ..CanvasTransform::default() }, [48.0, 48.0]));
    let mut header = sprite_node(&mut world, "Header", CanvasTransform::default(), [10.0, 56.0]);
    add_item(&mut world, &mut header, CanvasLayoutItemComponent { fill_width: true, ..CanvasLayoutItemComponent::default() });
    root.add_child(header);
    let roots = vec![root];
    let screen = CanvasScreenEnv { dp_scale: PIXEL_6A_DP, safe_area: Some(safe) };
    let table = Arc::new(play_table(&roots, &world, PIXEL_6A, screen));
    let results = CanvasLayoutResults::new(Arc::clone(&table), PIXEL_6A);

    // ルート: 安全領域の箱（1080×2201 画素・上から 136）。大きさは dp
    let root = readout(&results, &roots, &world, "Root");
    assert!(near_rect(root.screen_rect, [0.0, 136.0, 1080.0, 2201.0]), "{:?}", root.screen_rect);
    assert!(near(root.size, [1080.0 / PIXEL_6A_DP, 2201.0 / PIXEL_6A_DP]), "{:?}", root.size);

    // Icon: 位置 (16, 24) dp → (42, 136 + 63) 画素、48 dp = 126 画素
    let icon = readout(&results, &roots, &world, "Icon");
    assert!(near_rect(icon.screen_rect, [42.0, 199.0, 126.0, 126.0]), "{:?}", icon.screen_rect);
    assert!(near(icon.size, [48.0, 48.0]), "{:?}", icon.size);
    assert!((icon.screen_rect[2] / PIXEL_6A_DP - icon.size[0]).abs() < EPS, "dp は 画素 ÷ DpScale");
    assert_matches_drawn_sprite(&table, &results, &roots, &world, "Icon", [48.0, 48.0], PIXEL_6A);

    // Header: 幅は安全領域の箱いっぱい（1080 画素 = 411.43 dp）、高さは 56 dp = 147 画素
    let header = readout(&results, &roots, &world, "Header");
    assert!(near_rect(header.screen_rect, [0.0, 136.0, 1080.0, 147.0]), "{:?}", header.screen_rect);
    assert!(near(header.size, [1080.0 / PIXEL_6A_DP, 56.0]), "{:?}", header.size);
    assert_matches_drawn_sprite(&table, &results, &roots, &world, "Header", [10.0, 56.0], PIXEL_6A);
}

// ─── レイアウトの無いノード ─────────────────────────────────

/// レイアウトの部品の無いノード: Sprite はその大きさ（pivot・回転は描画と同じ外接矩形）、Text は枠、何も無ければ 0。
#[test]
fn nodes_without_layout_use_their_own_size() {
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), VIEWPORT);
    root.add_child(sprite_node(&mut world, "Plain", CanvasTransform { position: [100.0, 50.0], pivot: [0.5, 0.5], ..CanvasTransform::default() }, [60.0, 30.0]));
    root.add_child(sprite_node(&mut world, "Rotated", CanvasTransform { position: [300.0, 200.0], rotation: 90.0, ..CanvasTransform::default() }, [60.0, 30.0]));
    let mut label = bare_node(&mut world, "Label");
    add(&mut world, &mut label, ComponentKind::Text, TextComponent { box_width: 120.0, box_height: 0.0, ..TextComponent::default() });
    root.add_child(label);
    let empty = bare_node(&mut world, "Empty");
    if let Some(ct) = world.get_mut::<CanvasTransform>(empty.entity) {
        ct.position = [10.0, 20.0];
    }
    root.add_child(empty);
    let roots = vec![root];
    let table = Arc::new(play_table(&roots, &world, VIEWPORT, CanvasScreenEnv::NONE));
    let results = CanvasLayoutResults::new(Arc::clone(&table), VIEWPORT);

    // Sprite: 大きさはそのまま（割り算を通さない）。pivot 0.5 なので位置が中心
    let plain = readout(&results, &roots, &world, "Plain");
    assert_eq!(plain.size, [60.0, 30.0]);
    assert!(near_rect(plain.screen_rect, [70.0, 35.0, 60.0, 30.0]), "{:?}", plain.screen_rect);
    assert_matches_drawn_sprite(&table, &results, &roots, &world, "Plain", [60.0, 30.0], VIEWPORT);

    // 90 度の回転: 大きさ（単位）は回さず、画面の矩形は外接矩形（幅と高さが入れ替わる）
    let rotated = readout(&results, &roots, &world, "Rotated");
    assert_eq!(rotated.size, [60.0, 30.0]);
    assert!(near_rect(rotated.screen_rect, [270.0, 200.0, 30.0, 60.0]), "{:?}", rotated.screen_rect);
    assert_matches_drawn_sprite(&table, &results, &roots, &world, "Rotated", [60.0, 30.0], VIEWPORT);

    // Text の枠: 枠のある軸だけ（高さ 0 = 文字で決まる軸は測らない → 0）
    assert!(near(readout(&results, &roots, &world, "Label").size, [120.0, 0.0]));

    // 何も無いノード: 大きさ 0・矩形は位置の 1 点
    let empty = readout(&results, &roots, &world, "Empty");
    assert_eq!(empty.size, [0.0, 0.0]);
    assert!(near_rect(empty.screen_rect, [10.0, 20.0, 0.0, 0.0]), "{:?}", empty.screen_rect);
}

// ─── 表に無い・見せない行 ───────────────────────────────────

/// 3D ワールドキャンバス（CanvasTransform を持たない 3D アクター）の下・フォルダ・世界線が違う子・知らない Entity は HasLayout = false。
/// 非表示の子は表の行があるので true（配置は求めてある）。表の無い資源はすべて false。
#[test]
fn nodes_outside_the_game_screen_tree_have_no_layout() {
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), VIEWPORT);
    root.add_child(sprite_node(&mut world, "Shown", CanvasTransform::default(), [10.0, 10.0]));
    let mut hidden = sprite_node(&mut world, "Hidden", CanvasTransform::default(), [10.0, 10.0]);
    hidden.visible = false;
    root.add_child(hidden);
    let folder_entity = world.spawn();
    world.insert(folder_entity, CanvasTransform::default());
    let mut folder = Actor::new_folder_2d(folder_entity, "Folder");
    folder.add_child(sprite_node(&mut world, "InFolder", CanvasTransform::default(), [10.0, 10.0]));
    root.add_child(folder);
    let mut other_line = sprite_node(&mut world, "OtherLine", CanvasTransform::default(), [10.0, 10.0]);
    other_line.world_line = 7;
    root.add_child(other_line);
    // 3D ワールドキャンバス: CanvasTransform を持たない 3D アクター + CanvasComponent（子は別の表で描く）
    let canvas3d_entity = world.spawn();
    let mut world_canvas = Actor::new(canvas3d_entity, "WorldCanvas");
    add(&mut world, &mut world_canvas, ComponentKind::Canvas, CanvasComponent { width: 300.0, height: 200.0, ..CanvasComponent::default() });
    world_canvas.add_child(sprite_node(&mut world, "OnWorldCanvas", CanvasTransform::default(), [10.0, 10.0]));
    let stranger = world.spawn();
    let roots = vec![root, world_canvas];
    let table = Arc::new(play_table(&roots, &world, VIEWPORT, CanvasScreenEnv::NONE));
    let results = CanvasLayoutResults::new(Arc::clone(&table), VIEWPORT);

    let has = |name: &str| results.has_layout(actor(&roots, name).entity);
    assert!(has("Root") && has("Shown") && has("InFolder"));
    assert!(has("Hidden"), "非表示でも表の行（配置）はある");
    assert!(!has("Folder"), "フォルダは大きさを持たない");
    assert!(!has("OtherLine"), "世界線が違う子");
    assert!(!has("WorldCanvas"), "CanvasTransform を持たない 3D アクター");
    assert!(!has("OnWorldCanvas"), "3D ワールドキャンバスの下（メインの表の値は canvas_to_world を反映しない）");
    assert!(!results.has_layout(stranger), "表に無い Entity");
    let on_world_canvas = actor(&roots, "OnWorldCanvas");
    assert!(results.readout(on_world_canvas.entity, || OwnSize::of(on_world_canvas, &world)).is_none());

    // まだ描画していない（表の無い）資源
    let empty = CanvasLayoutResults::default();
    assert!(!empty.has_table() && !empty.has_layout(actor(&roots, "Shown").entity));
}

// ─── 索引と受け渡し ─────────────────────────────────────────

/// 索引は読むまで作らない。描画からの受け渡し: Publish は表を共有して差し替え（索引は作り直し待ち）、
/// Keep（一時停止の見た目）はそのまま、Clear（Edit など）は空にし、資源の無い World には何も足さない。
#[test]
fn index_is_lazy_and_handoff_updates_the_resource() {
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), VIEWPORT);
    root.add_child(sprite_node(&mut world, "Node", CanvasTransform::default(), [10.0, 10.0]));
    let roots = vec![root];
    let node = actor(&roots, "Node").entity;
    let table = Arc::new(play_table(&roots, &world, VIEWPORT, CanvasScreenEnv::NONE));

    // 作っただけでは索引は無い。最初に読んだときに作る
    let results = CanvasLayoutResults::new(Arc::clone(&table), VIEWPORT);
    assert!(results.has_table() && !results.index_built());
    assert!(results.has_layout(node));
    assert!(results.index_built(), "最初の読み取りで作る");

    // シーンの World へ渡す（表は写さずに共有する）
    let mut scene_world = World::new();
    CanvasLayoutHandoff::for_frame(LayoutFrameView::GameScreen, Some(&table), VIEWPORT).apply(&mut scene_world);
    assert_eq!(Arc::strong_count(&table), 3, "ここ・results・資源の 3 つ（写しを作らない）");
    let published = scene_world.resource::<CanvasLayoutResults>().expect("資源");
    assert!(published.has_table() && !published.index_built(), "渡しただけでは索引を作らない");
    assert!(published.has_layout(node) && published.index_built());

    // 次のフレームの Publish は差し替え（索引は作り直し待ちに戻る）
    CanvasLayoutHandoff::for_frame(LayoutFrameView::GameScreen, Some(&table), VIEWPORT).apply(&mut scene_world);
    assert!(!scene_world.resource::<CanvasLayoutResults>().expect("資源").index_built());

    // 一時停止の見た目は何もしない（再開した最初のフレームも止める前の表を読む）
    CanvasLayoutHandoff::for_frame(LayoutFrameView::PausedGame, None, VIEWPORT).apply(&mut scene_world);
    assert!(scene_world.resource::<CanvasLayoutResults>().expect("資源").has_layout(node));

    // Edit など: 空にする
    CanvasLayoutHandoff::for_frame(LayoutFrameView::NotGame, Some(&table), VIEWPORT).apply(&mut scene_world);
    assert!(!scene_world.resource::<CanvasLayoutResults>().expect("資源").has_table());

    // 資源の無い World を空にしても何も足さない
    let mut fresh = World::new();
    CanvasLayoutHandoff::Clear.apply(&mut fresh);
    assert!(fresh.resource::<CanvasLayoutResults>().is_none());
}

/// フレームの画面の分け方と、それぞれの受け渡し。
#[test]
fn frame_view_decides_the_handoff() {
    assert_eq!(LayoutFrameView::classify(false, false, false), LayoutFrameView::NotGame, "Edit");
    assert_eq!(LayoutFrameView::classify(false, true, false), LayoutFrameView::NotGame, "Edit（一時停止の旗は見ない）");
    assert_eq!(LayoutFrameView::classify(true, true, false), LayoutFrameView::PausedGame);
    assert_eq!(LayoutFrameView::classify(true, true, true), LayoutFrameView::PausedGame);
    assert_eq!(LayoutFrameView::classify(true, false, true), LayoutFrameView::NotGame, "アクター編集タブ・サムネイル");
    assert_eq!(LayoutFrameView::classify(true, false, false), LayoutFrameView::GameScreen);

    let table = Arc::new(CanvasLayoutTable::default());
    assert!(matches!(CanvasLayoutHandoff::for_frame(LayoutFrameView::GameScreen, Some(&table), VIEWPORT), CanvasLayoutHandoff::Publish { .. }));
    assert!(matches!(CanvasLayoutHandoff::for_frame(LayoutFrameView::GameScreen, None, VIEWPORT), CanvasLayoutHandoff::Clear), "キャンバスの無いシーン");
    assert!(matches!(CanvasLayoutHandoff::for_frame(LayoutFrameView::PausedGame, Some(&table), VIEWPORT), CanvasLayoutHandoff::Keep));
    assert!(matches!(CanvasLayoutHandoff::for_frame(LayoutFrameView::NotGame, Some(&table), VIEWPORT), CanvasLayoutHandoff::Clear));
}
