// ============================================================
//  canvas_layout/visual_scale_tests.rs — 見た目の倍率（CanvasLayoutItem.visual_scale。W2 の手直し 3b）のテスト
//
//  1. 既定（倍率 1）の木の表は、見た目の倍率を足す前のコードとビット単位で同じ（golden の指紋）。
//     指紋は「倍率を足す前からある欄」だけを浮動小数の Debug 表記（往復で同じ値に戻る最短の表記。-0.0 も区別する）で
//     並べた文字列の FNV-1a（64 ビット）。倍率のために足した欄（文脈の visual_scale・統計の scaled）は入れない。
//     既定の値で表が変わる変更をしたときにだけ落ちる（レイアウトの規則を意図して変えたなら、新しい値へ書き換える）。
//  2. 倍率 0.9 で、矩形の中心が動かない・子（入れ子のキャンバスの子を含む）が中心へ寄る・当たり判定（描画と同じ行列・
//     切り抜きの領域・Item 4 の LayoutRect）がそろう・コンテナの並びがそのまま縮む・安全領域が縮み直さない。
//  木を作る道具は layout_tests.rs のものを使う。
// ============================================================

use super::clip::{canvas_area_corners, corners_aabb, sprite_rect_corners};
use super::layout_tests::{add, add_item, add_stack, bare_node, canvas_node, play_table, sprite_node, table_of, vstack};
use super::*;
use crate::engine::components::{
    CanvasClipComponent, CanvasComponent, CanvasLayoutItemComponent, CanvasPadding, CanvasSafeAreaComponent,
    CanvasStackComponent, CanvasTransform, CanvasUnit, ComponentKind, CrossAlign, LayoutDirection, SpriteComponent,
};
use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;

/// 浮動小数の比較の許容量（画素）。
const EPS: f32 = 1e-3;

/// 試す見た目の倍率（予測型の戻るのプレビューのいちばん小さい倍率と同じ 0.9）。
const PREVIEW_SCALE: f32 = 0.9;

/// Pixel 6a の縦画面（画素）。
const PIXEL_6A: [f32; 2] = [1080.0, 2400.0];

/// Pixel 6a の 1 dp の画素数（420 dpi ÷ 160）。
const PIXEL_6A_DP: f32 = 2.625;

/// Pixel 6a の安全領域（画面の中央が原点・Y 下向き。上 136 px・下 63 px）。
const PIXEL_6A_SAFE: CanvasRect = CanvasRect { min: [-540.0, -1064.0], max: [540.0, 1137.0] };

/// 既定の倍率の golden の木の表の指紋（見た目の倍率を足す前のコード〈2026-09-29 の作業ツリー〉で取った値）。
const GOLDEN_FINGERPRINT: u64 = 0x0cf7_13a4_4e78_8533;

/// FNV-1a（64 ビット）の初期値。
const FNV_OFFSET_BASIS: u64 = 0xcbf2_9ce4_8422_2325;

/// FNV-1a（64 ビット）の素数。
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;

/// FNV-1a（64 ビット）【純関数】。
fn fnv1a(bytes: &[u8]) -> u64 {
    bytes.iter().fold(FNV_OFFSET_BASIS, |hash, b| (hash ^ u64::from(*b)).wrapping_mul(FNV_PRIME))
}

/// 値の Debug 表記を区切りつきで足す（浮動小数は往復で同じ値に戻る表記なので、ビット単位の比較になる）。
fn push(out: &mut String, value: impl std::fmt::Debug) {
    out.push_str(&format!("{value:?}|"));
}

/// 文脈の「倍率を足す前からある欄」を足す。
fn push_frame(out: &mut String, f: &CanvasParentFrame) {
    push(out, (f.anchor_basis, f.world_rs, f.cumul_scale, f.zone, f.layer_bias, f.visual_shift));
}

/// 配置の「倍率を足す前からある欄」を足す。
fn push_placement(out: &mut String, p: &CanvasNodePlacement) {
    push(
        out,
        (
            p.root_auto,
            &p.transform,
            p.eff_viewport,
            p.anchor_offset,
            &p.eff_transform,
            p.zone,
            p.size_scale,
            p.canvas_base,
            p.canvas_auto_scale,
            p.eff_size,
            p.world_rs,
            p.auto_scale_factor,
        ),
    );
    push_frame(out, &p.child_frame);
    push(out, (p.sprite_fill, p.layout_rect, p.layout_adjusted, p.layer_bias));
}

/// 表の指紋（行・切り抜きの領域・統計の、倍率を足す前からある欄）。
fn fingerprint(table: &CanvasLayoutTable) -> u64 {
    let mut out = String::new();
    for node in &table.nodes {
        push(&mut out, (node.parent, node.subtree_end, node.depth, node.flags, node.clip, node.own_clip_region, node.culled));
        push_frame(&mut out, &node.frame);
        match &node.kind {
            CanvasNodeKind::Placed(p) => push_placement(&mut out, p),
            CanvasNodeKind::NoTransform { child_frame } => push_frame(&mut out, child_frame),
            CanvasNodeKind::Folder => push(&mut out, "folder"),
        }
    }
    for region in &table.clip_regions {
        push(&mut out, region);
    }
    let s = &table.stats;
    push(&mut out, (s.containers, s.placed_by_layout, s.safe_areas, s.measure_calls, s.scrolls, s.culled, s.translated));
    fnv1a(out.as_bytes())
}

/// ルートのキャンバスの設定を書き換える（単位・自動スケール）。
fn set_root_canvas(world: &mut World, root: &Actor, unit: CanvasUnit) {
    let slot = root.slots()[0].entity;
    let cc = world.get_mut::<CanvasComponent>(slot).expect("ルートのキャンバス");
    cc.unit = unit;
    cc.auto_scale = true;
}

/// golden の木（dp のルート・親に合わせる・安全領域・縦横の列・伸ばす重み・回転と pivot と Scale のパネル・入れ子の
/// キャンバス・切り抜き・中身に合わせる札・平行移動・レイヤーの底上げ・px のルートの自動スケール）。
fn golden_scene(world: &mut World) -> Vec<Actor> {
    let mut root = canvas_node(world, "Root", CanvasTransform::default(), [540.0, 1200.0]);
    set_root_canvas(world, &root, CanvasUnit::Dp);

    // 画面の枠（親に合わせる・横へずらす・底上げ）と、安全領域の中の縦の列
    let mut frame = canvas_node(world, "Frame", CanvasTransform::default(), [1.0, 1.0]);
    add_item(world, &mut frame, CanvasLayoutItemComponent {
        fill_width: true,
        fill_height: true,
        translate_fraction: [0.125, 0.0],
        layer_bias: 20_000,
        ..CanvasLayoutItemComponent::default()
    });
    let mut body = canvas_node(world, "Body", CanvasTransform::default(), [1.0, 1.0]);
    add_item(world, &mut body, CanvasLayoutItemComponent { fill_width: true, fill_height: true, ..CanvasLayoutItemComponent::default() });
    add(world, &mut body, ComponentKind::CanvasSafeArea, CanvasSafeAreaComponent { enabled: true, left: true, top: true, right: true, bottom: true });
    add_stack(world, &mut body, CanvasStackComponent {
        spacing: 8.0,
        padding: CanvasPadding { left: 16.0, top: 12.0, right: 16.0, bottom: 12.0 },
        cross_align: CrossAlign::Stretch,
        ..vstack()
    });
    body.add_child(sprite_node(world, "Header", CanvasTransform::default(), [100.0, 56.0]));
    let mut list = sprite_node(world, "List", CanvasTransform::default(), [100.0, 10.0]);
    add_item(world, &mut list, CanvasLayoutItemComponent { flex: 1.0, ..CanvasLayoutItemComponent::default() });
    body.add_child(list);
    let mut row = bare_node(world, "Row");
    add_stack(world, &mut row, CanvasStackComponent {
        direction: LayoutDirection::Horizontal,
        spacing: 4.0,
        padding: CanvasPadding { left: 2.0, top: 3.0, right: 2.0, bottom: 3.0 },
        ..vstack()
    });
    for i in 0..3u8 {
        row.add_child(sprite_node(world, &format!("Chip{i}"), CanvasTransform::default(), [40.0 + 8.0 * f32::from(i), 24.0]));
    }
    body.add_child(row);
    frame.add_child(body);
    root.add_child(frame);

    // 回転・pivot・Scale のあるパネル（アンカー中央・切り抜き）と入れ子のキャンバス
    let mut panel = canvas_node(world, "Panel", CanvasTransform {
        anchor: [0.5, 0.5],
        position: [-30.0, 40.0],
        rotation: 12.0,
        pivot: [0.5, 0.5],
        scale: [1.25, 1.25],
        ..CanvasTransform::default()
    }, [200.0, 120.0]);
    add(world, &mut panel, ComponentKind::CanvasClip, CanvasClipComponent { enabled: true, ..CanvasClipComponent::default() });
    panel.add_child(sprite_node(world, "Corner", CanvasTransform {
        anchor: [1.0, 1.0],
        position: [-20.0, -10.0],
        pivot: [1.0, 1.0],
        ..CanvasTransform::default()
    }, [20.0, 10.0]));
    let mut inner = canvas_node(world, "Inner", CanvasTransform { position: [10.0, 10.0], ..CanvasTransform::default() }, [80.0, 40.0]);
    inner.add_child(sprite_node(world, "Dot", CanvasTransform { anchor: [0.5, 0.5], pivot: [0.5, 0.5], ..CanvasTransform::default() }, [6.0, 6.0]));
    panel.add_child(inner);
    root.add_child(panel);

    // ダイアログ風の札（縦の列・高さは中身に合わせる・pivot 中央・Scale 0.9）
    let mut card = canvas_node(world, "Card", CanvasTransform {
        anchor: [0.5, 0.5],
        pivot: [0.5, 0.5],
        scale: [0.9, 0.9],
        ..CanvasTransform::default()
    }, [280.0, 10.0]);
    add_stack(world, &mut card, CanvasStackComponent {
        fit_height: true,
        spacing: 16.0,
        padding: CanvasPadding { left: 24.0, top: 24.0, right: 24.0, bottom: 24.0 },
        ..vstack()
    });
    card.add_child(sprite_node(world, "Title", CanvasTransform::default(), [232.0, 28.0]));
    card.add_child(sprite_node(world, "Message", CanvasTransform::default(), [232.0, 60.0]));
    root.add_child(card);

    // 平行移動のトースト
    let mut toast = sprite_node(world, "Toast", CanvasTransform {
        anchor: [0.5, 1.0],
        position: [-100.0, -80.0],
        ..CanvasTransform::default()
    }, [200.0, 48.0]);
    add_item(world, &mut toast, CanvasLayoutItemComponent { translate: [0.0, 24.0], ..CanvasLayoutItemComponent::default() });
    root.add_child(toast);

    // px のルート（自動スケール）と回転したスプライト
    let px_root = {
        let mut px = canvas_node(world, "PxRoot", CanvasTransform::default(), [1920.0, 1080.0]);
        set_root_canvas(world, &px, CanvasUnit::Px);
        px.add_child(sprite_node(world, "PxSprite", CanvasTransform {
            position: [100.0, 50.0],
            rotation: -7.0,
            ..CanvasTransform::default()
        }, [64.0, 32.0]));
        px
    };
    vec![root, px_root]
}

/// Pixel 6a の文脈（dp の倍率・安全領域）の表。
fn pixel_6a_table(roots: &[Actor], world: &World) -> CanvasLayoutTable {
    play_table(roots, world, PIXEL_6A, CanvasScreenEnv { dp_scale: PIXEL_6A_DP, safe_area: Some(PIXEL_6A_SAFE) })
}

/// 既定（倍率 1）の木の表は、見た目の倍率を足す前のコードとビット単位で同じ。
#[test]
fn default_visual_scale_keeps_golden_table() {
    let mut world = World::new();
    let roots = golden_scene(&mut world);
    let table = pixel_6a_table(&roots, &world);
    // 木がねらった道（コンテナ・親に合わせる・安全領域・平行移動・切り抜き）を通っていること
    let s = &table.stats;
    assert_eq!((s.containers, s.safe_areas, s.translated), (3, 1, 2), "{s:?}");
    assert!(s.placed_by_layout >= 9 && s.measure_calls > 0, "{s:?}");
    assert_eq!(table.clip_regions.len(), 1);
    let fp = fingerprint(&table);
    eprintln!("[3b golden] 指紋 = {fp:#018x}");
    assert_eq!(fp, GOLDEN_FINGERPRINT, "既定の倍率で表が変わった（指紋 {fp:#018x}）");
    assert_eq!(table.stats.scaled, 0, "既定の倍率は倍率の計算を通らない");
}

// ─── 倍率 0.9 の振る舞い ──────────────────────────────────

/// 表の行を名前で引く（親の文脈と配置）。
fn row<'t>(table: &'t CanvasLayoutTable, roots: &[Actor], name: &str) -> (&'t CanvasParentFrame, &'t CanvasNodePlacement) {
    table
        .iter_with_actors(roots)
        .find(|(_, a)| a.name == name)
        .and_then(|(n, _)| n.placement().map(|p| (&n.frame, p)))
        .unwrap_or_else(|| panic!("{name} が表に無い"))
}

/// 点 p を中心 c の周りに s 倍した点。
fn scaled_about(p: [f32; 2], c: [f32; 2], s: f32) -> [f32; 2] {
    [c[0] + s * (p[0] - c[0]), c[1] + s * (p[1] - c[1])]
}

/// 2 つの点がほぼ等しいか（許容量 EPS）。
fn close(a: [f32; 2], b: [f32; 2]) -> bool {
    (a[0] - b[0]).abs() < EPS && (a[1] - b[1]).abs() < EPS
}

/// 4 隅の XY がそれぞれ「元の 4 隅を中心の周りに s 倍した点」と等しいか。
fn corners_scaled(after: &[[f32; 3]; 4], before: &[[f32; 3]; 4], c: [f32; 2], s: f32) -> bool {
    after.iter().zip(before).all(|(a, b)| close([a[0], a[1]], scaled_about([b[0], b[1]], c, s)))
}

/// スプライトを描く 4 隅（描画・当たり判定・Item 4 の LayoutRect と同じ行列）。
fn sprite_corners(frame: &CanvasParentFrame, p: &CanvasNodePlacement, width: f32, height: f32) -> [[f32; 3]; 4] {
    sprite_rect_corners(frame.world_rs, &p.eff_transform, p.sprite_size(width, height))
}

/// キャンバス領域の 4 隅の外接矩形の中心。
fn area_center(frame: &CanvasParentFrame, p: &CanvasNodePlacement) -> [f32; 2] {
    let (min, max) = corners_aabb(&canvas_area_corners(frame.world_rs, &p.eff_transform, p.eff_size));
    [(min[0] + max[0]) / 2.0, (min[1] + max[1]) / 2.0]
}

/// 画面の枠（親に合わせる・切り抜き・縦の列）と、その中のスプライトと入れ子のキャンバス（アンカーした子を持つ）の木。
/// `scale` を画面の枠の見た目の倍率にする。
fn screen_scene(world: &mut World, scale: f32) -> Vec<Actor> {
    let mut root = canvas_node(world, "Root", CanvasTransform::default(), [400.0, 800.0]);
    let mut screen = canvas_node(world, "Screen", CanvasTransform::default(), [1.0, 1.0]);
    add_item(world, &mut screen, CanvasLayoutItemComponent {
        fill_width: true,
        fill_height: true,
        visual_scale: [scale, scale],
        ..CanvasLayoutItemComponent::default()
    });
    add(world, &mut screen, ComponentKind::CanvasClip, CanvasClipComponent { enabled: true, ..CanvasClipComponent::default() });
    add_stack(world, &mut screen, CanvasStackComponent {
        spacing: 10.0,
        padding: CanvasPadding { left: 20.0, top: 30.0, right: 20.0, bottom: 0.0 },
        cross_align: CrossAlign::Stretch,
        ..vstack()
    });
    screen.add_child(sprite_node(world, "Header", CanvasTransform::default(), [100.0, 56.0]));
    let mut nested = canvas_node(world, "Nested", CanvasTransform::default(), [100.0, 50.0]);
    nested.add_child(sprite_node(world, "Leaf", CanvasTransform {
        anchor: [1.0, 1.0],
        position: [-12.0, -8.0],
        ..CanvasTransform::default()
    }, [12.0, 8.0]));
    screen.add_child(nested);
    root.add_child(screen);
    vec![root]
}

/// 倍率 0.9: 画面の矩形の中心は動かず、大きさが 0.9 倍。子（並べた子・入れ子のキャンバスとその子）の描く矩形は
/// すべて中心の周りに 0.9 倍した位置へ寄る。切り抜き・Item 4 の LayoutRect（node_extent）も同じ矩形。単位の大きさは変わらない。
#[test]
fn visual_scale_keeps_center_and_pulls_descendants_inward() {
    let mut world_a = World::new();
    let roots_a = screen_scene(&mut world_a, 1.0);
    let a = table_of(&roots_a, &world_a);
    let mut world_b = World::new();
    let roots_b = screen_scene(&mut world_b, PREVIEW_SCALE);
    let b = table_of(&roots_b, &world_b);
    assert_eq!((a.stats.scaled, b.stats.scaled), (0, 1), "倍率 1 は通らない・0.9 は 1 つ");

    // 画面の枠: 中心は (200, 400) のまま、大きさは 360×720
    let (fa, sa) = row(&a, &roots_a, "Screen");
    let (fb, sb) = row(&b, &roots_b, "Screen");
    let center = area_center(fa, sa);
    assert!(close(center, [200.0, 400.0]), "{center:?}");
    assert!(close(area_center(fb, sb), center), "中心は動かない: {:?}", area_center(fb, sb));
    assert!(close(sb.eff_size, [360.0, 720.0]), "{:?}", sb.eff_size);
    assert_eq!(sb.canvas_base, sa.canvas_base, "単位の大きさ（倍率の前）は変わらない");
    assert!(close(sb.size_scale, [PREVIEW_SCALE, PREVIEW_SCALE]));
    assert!(sb.layout_adjusted);

    // 子孫: 描く矩形の 4 隅が中心の周りに 0.9 倍
    for (name, size) in [("Header", [100.0, 56.0]), ("Leaf", [12.0, 8.0])] {
        let (fa, pa) = row(&a, &roots_a, name);
        let (fb, pb) = row(&b, &roots_b, name);
        let before = sprite_corners(fa, pa, size[0], size[1]);
        let after = sprite_corners(fb, pb, size[0], size[1]);
        assert!(corners_scaled(&after, &before, center, PREVIEW_SCALE), "{name}: {before:?} → {after:?}");
    }
    // 入れ子のキャンバス: 領域も 0.9 倍の位置・大きさ（単位の大きさはそのまま）
    let (fa, na) = row(&a, &roots_a, "Nested");
    let (fb, nb) = row(&b, &roots_b, "Nested");
    let before = canvas_area_corners(fa.world_rs, &na.eff_transform, na.eff_size);
    let after = canvas_area_corners(fb.world_rs, &nb.eff_transform, nb.eff_size);
    assert!(corners_scaled(&after, &before, center, PREVIEW_SCALE), "Nested: {before:?} → {after:?}");
    assert_eq!(nb.canvas_base, na.canvas_base);

    // 切り抜き（画面の枠の領域）も同じ
    assert_eq!((a.clip_regions.len(), b.clip_regions.len()), (1, 1));
    assert!(corners_scaled(&b.clip_regions[0].corners, &a.clip_regions[0].corners, center, PREVIEW_SCALE));

    // Item 4 の LayoutRect（node_extent）: 矩形は縮み、LayoutSize（単位）は変わらない
    let own = || OwnSize { sprite: Some([100.0, 56.0]), text_box: [None, None] };
    let (fha, ha) = row(&a, &roots_a, "Header");
    let (fhb, hb) = row(&b, &roots_b, "Header");
    let header_a = node_extent(fha, ha, own);
    let header_b = node_extent(fhb, hb, own);
    assert!(close(header_b.size, header_a.size), "LayoutSize は倍率の前: {:?} / {:?}", header_a.size, header_b.size);
    assert!(close(header_b.world_rect.min, scaled_about(header_a.world_rect.min, center, PREVIEW_SCALE)));
    assert!(close(header_b.world_rect.max, scaled_about(header_a.world_rect.max, center, PREVIEW_SCALE)));
}

/// 回転・pivot・Scale を持つノードでも、描く矩形（自分のスプライト・キャンバス領域）と子の矩形は
/// 描画の矩形の中心の周りに縮む（ノードの軸に沿って。縦横同じ倍率なので回転と入れ替わる）。
#[test]
fn visual_scale_follows_rotation_pivot_and_own_scale() {
    const SCALE: f32 = 0.8;
    let build = |scale: f32| {
        let mut world = World::new();
        let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), [800.0, 800.0]);
        let mut panel = canvas_node(&mut world, "Panel", CanvasTransform {
            position: [300.0, 250.0],
            rotation: 30.0,
            pivot: [0.25, 0.75],
            scale: [1.5, 1.5],
            ..CanvasTransform::default()
        }, [200.0, 100.0]);
        add(&mut world, &mut panel, ComponentKind::Sprite, SpriteComponent { width: 200.0, height: 100.0, ..SpriteComponent::default() });
        add_item(&mut world, &mut panel, CanvasLayoutItemComponent { visual_scale: [scale, scale], ..CanvasLayoutItemComponent::default() });
        panel.add_child(sprite_node(&mut world, "Child", CanvasTransform {
            anchor: [1.0, 0.0],
            position: [-30.0, 10.0],
            rotation: -15.0,
            ..CanvasTransform::default()
        }, [30.0, 20.0]));
        root.add_child(panel);
        let roots = vec![root];
        let table = table_of(&roots, &world);
        (roots, table)
    };
    let (roots_a, a) = build(1.0);
    let (roots_b, b) = build(SCALE);
    let (fa, pa) = row(&a, &roots_a, "Panel");
    let (fb, pb) = row(&b, &roots_b, "Panel");
    let center = area_center(fa, pa);
    // 自分のスプライト・キャンバス領域・子の 4 隅
    let own_before = sprite_corners(fa, pa, 200.0, 100.0);
    let own_after = sprite_corners(fb, pb, 200.0, 100.0);
    assert!(corners_scaled(&own_after, &own_before, center, SCALE), "{own_before:?} → {own_after:?}");
    let area_before = canvas_area_corners(fa.world_rs, &pa.eff_transform, pa.eff_size);
    let area_after = canvas_area_corners(fb.world_rs, &pb.eff_transform, pb.eff_size);
    assert!(corners_scaled(&area_after, &area_before, center, SCALE));
    let (fca, ca) = row(&a, &roots_a, "Child");
    let (fcb, cb) = row(&b, &roots_b, "Child");
    assert!(corners_scaled(&sprite_corners(fcb, cb, 30.0, 20.0), &sprite_corners(fca, ca, 30.0, 20.0), center, SCALE));
}

/// 倍率を当てたノード自身がコンテナで、親のコンテナに中身の大きさで測られる（ダイアログの札の形）: 並びも倍率の空間で
/// 求まり、子は中心の周りに 0.9 倍した位置・大きさになる（親が倍率の前に測った結果を引き違えない＝計測の記憶の鍵に累積スケール）。
#[test]
fn visual_scale_on_measured_container_rearranges_in_scaled_space() {
    let build = |scale: f32| {
        let mut world = World::new();
        let mut outer = canvas_node(&mut world, "Outer", CanvasTransform::default(), [400.0, 800.0]);
        add_stack(&mut world, &mut outer, CanvasStackComponent { cross_align: CrossAlign::Center, ..vstack() });
        let mut card = bare_node(&mut world, "Card");
        add_stack(&mut world, &mut card, CanvasStackComponent {
            spacing: 5.0,
            padding: CanvasPadding { left: 10.0, top: 10.0, right: 10.0, bottom: 10.0 },
            ..vstack()
        });
        add_item(&mut world, &mut card, CanvasLayoutItemComponent { visual_scale: [scale, scale], ..CanvasLayoutItemComponent::default() });
        card.add_child(sprite_node(&mut world, "Title", CanvasTransform::default(), [200.0, 30.0]));
        card.add_child(sprite_node(&mut world, "Body", CanvasTransform::default(), [150.0, 60.0]));
        outer.add_child(card);
        let roots = vec![outer];
        let table = table_of(&roots, &world);
        (roots, table)
    };
    let (roots_a, a) = build(1.0);
    let (roots_b, b) = build(PREVIEW_SCALE);
    // 札の矩形 = 中身 220×115 を幅 400 の真ん中に（x 90）。中心 (200, 57.5)
    let (_, card_a) = row(&a, &roots_a, "Card");
    assert_eq!(card_a.layout_rect, Some([220.0, 115.0]));
    let center = [200.0, 57.5];
    let (_, card_b) = row(&b, &roots_b, "Card");
    assert!(close(card_b.layout_rect.expect("割り当てた矩形"), [198.0, 103.5]), "{:?}", card_b.layout_rect);
    for (name, size) in [("Title", [200.0, 30.0]), ("Body", [150.0, 60.0])] {
        let (fa, pa) = row(&a, &roots_a, name);
        let (fb, pb) = row(&b, &roots_b, name);
        let before = sprite_corners(fa, pa, size[0], size[1]);
        let after = sprite_corners(fb, pb, size[0], size[1]);
        assert!(corners_scaled(&after, &before, center, PREVIEW_SCALE), "{name}: {before:?} → {after:?}");
    }
}

/// 親のコンテナに交差軸を伸ばされた子のコンテナ（横の列・両端寄せ）に倍率を当てても、伸ばされた幅を倍率の空間の幅として
/// 並べる（割り当ての矩形は倍率の前の大きさなので、倍率を掛けてから箱にする）。子は中心の周りに 0.9 倍した位置へ。
#[test]
fn visual_scale_on_stretched_container_child_uses_scaled_slot() {
    let build = |scale: f32| {
        let mut world = World::new();
        let mut outer = canvas_node(&mut world, "Outer", CanvasTransform::default(), [400.0, 800.0]);
        add_stack(&mut world, &mut outer, CanvasStackComponent {
            cross_align: CrossAlign::Stretch,
            padding: CanvasPadding { left: 20.0, top: 40.0, right: 20.0, bottom: 0.0 },
            ..vstack()
        });
        let mut row = bare_node(&mut world, "Row");
        add_stack(&mut world, &mut row, CanvasStackComponent {
            direction: LayoutDirection::Horizontal,
            main_align: crate::engine::components::MainAlign::SpaceBetween,
            ..vstack()
        });
        add_item(&mut world, &mut row, CanvasLayoutItemComponent { visual_scale: [scale, scale], ..CanvasLayoutItemComponent::default() });
        for i in 0..3u8 {
            row.add_child(sprite_node(&mut world, &format!("Chip{i}"), CanvasTransform::default(), [50.0, 30.0]));
        }
        outer.add_child(row);
        let roots = vec![outer];
        let table = table_of(&roots, &world);
        (roots, table)
    };
    let (roots_a, a) = build(1.0);
    let (roots_b, b) = build(PREVIEW_SCALE);
    // 列の矩形 = 幅 360（伸ばした）× 高さ 30 を (20, 40) に。中心 (200, 55)
    let (_, row_a) = row(&a, &roots_a, "Row");
    assert_eq!(row_a.layout_rect, Some([360.0, 30.0]));
    let center = [200.0, 55.0];
    for i in 0..3 {
        let name = format!("Chip{i}");
        let (fa, pa) = row(&a, &roots_a, &name);
        let (fb, pb) = row(&b, &roots_b, &name);
        let before = sprite_corners(fa, pa, 50.0, 30.0);
        let after = sprite_corners(fb, pb, 50.0, 30.0);
        assert!(corners_scaled(&after, &before, center, PREVIEW_SCALE), "{name}: {before:?} → {after:?}");
    }
}

/// 倍率を当てた画面の中の安全領域の箱は、倍率の前の位置・大きさで縮める量を求め、箱ごと中心の周りに縮む
/// （縮めている途中で上下の辺が画面の端から離れても、ステータスバーの分の余白が消えない）。
#[test]
fn safe_area_inside_scaled_node_keeps_insets() {
    let build = |scale: f32| {
        let mut world = World::new();
        let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), PIXEL_6A);
        let mut frame = canvas_node(&mut world, "Frame", CanvasTransform::default(), [1.0, 1.0]);
        add_item(&mut world, &mut frame, CanvasLayoutItemComponent {
            fill_width: true,
            fill_height: true,
            visual_scale: [scale, scale],
            ..CanvasLayoutItemComponent::default()
        });
        let mut body = canvas_node(&mut world, "Body", CanvasTransform::default(), [1.0, 1.0]);
        add_item(&mut world, &mut body, CanvasLayoutItemComponent { fill_width: true, fill_height: true, ..CanvasLayoutItemComponent::default() });
        add(&mut world, &mut body, ComponentKind::CanvasSafeArea, CanvasSafeAreaComponent { enabled: true, left: true, top: true, right: true, bottom: true });
        body.add_child(sprite_node(&mut world, "BottomRight", CanvasTransform { anchor: [1.0, 1.0], ..CanvasTransform::default() }, [10.0, 10.0]));
        frame.add_child(body);
        root.add_child(frame);
        let roots = vec![root];
        let table = play_table(&roots, &world, PIXEL_6A, CanvasScreenEnv { dp_scale: 1.0, safe_area: Some(PIXEL_6A_SAFE) });
        (roots, table)
    };
    let (roots_a, a) = build(1.0);
    let (roots_b, b) = build(PREVIEW_SCALE);
    // 画面（中央が原点）の中心 (0, 0) の周り
    let center = [0.0, 0.0];
    let (_, body_a) = row(&a, &roots_a, "Body");
    let (_, body_b) = row(&b, &roots_b, "Body");
    assert!(close(body_a.eff_size, [1080.0, 2201.0]), "{:?}", body_a.eff_size);
    assert!(
        close(body_b.eff_size, [1080.0 * PREVIEW_SCALE, 2201.0 * PREVIEW_SCALE]),
        "縮める量は倍率の前のまま: {:?}",
        body_b.eff_size
    );
    let (fa, pa) = row(&a, &roots_a, "BottomRight");
    let (fb, pb) = row(&b, &roots_b, "BottomRight");
    assert!(corners_scaled(&sprite_corners(fb, pb, 10.0, 10.0), &sprite_corners(fa, pa, 10.0, 10.0), center, PREVIEW_SCALE));
    assert_eq!(b.stats.safe_areas, 1);
}
