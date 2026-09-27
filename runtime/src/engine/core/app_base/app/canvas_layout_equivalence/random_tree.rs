// ============================================================
//  canvas_layout_equivalence/random_tree.rs — ランダムなキャンバスのシーンの生成（テスト専用）
//
//  乱数は SplitMix64（種から決定的に同じ列を出す。失敗した種をそのまま再現できる）。
//  実データに合わせて「子の世界線はルートと同じ」（エディタの操作と scene の読み込みが保つ不変条件）で作る。
// ============================================================

use std::collections::HashMap;

use crate::engine::components::{
    AspectRatioAxis, CanvasComponent, CanvasDrawZone, CanvasTransform, Collider2dComponent,
    ColliderShape2dData, ComponentKind, SpriteComponent, TextComponent,
};
use crate::engine::core::app_base::scene::Scene;
use crate::engine::core::font::text_layout::TextLocalBox;
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

use super::super::canvas_text_bounds::{TextBounds, TextBoundsMap};

/// 対象の世界線。
pub(super) const TARGET_WORLD_LINE: u32 = 0;
/// 対象ではない世界線（ルートの絞り込みの確認用）。
const OTHER_WORLD_LINE: u32 = 1;
/// 木の深さの上限（ルート = 0）。
const MAX_DEPTH: u32 = 5;
/// 1 ノードの子の数の上限。
const MAX_CHILDREN: u64 = 3;
/// ルートの数の上限。
const MAX_ROOTS: u64 = 3;
/// 試すビューポートの大きさ（横長・縦長・フル HD・4:3）。
const VIEWPORTS: [[f32; 2]; 4] = [[1280.0, 720.0], [540.0, 1200.0], [1920.0, 1080.0], [800.0, 600.0]];
/// 位置の乱数の幅（px）。
const POSITION_RANGE: f32 = 600.0;
/// キャンバスの大きさの乱数の範囲（px）。
const CANVAS_SIZE_RANGE: (f32, f32) = (1.0, 2000.0);
/// スプライトの大きさの乱数の範囲（px）。
const SPRITE_SIZE_RANGE: (f32, f32) = (1.0, 400.0);
/// スケールの乱数の範囲。
const SCALE_RANGE: (f32, f32) = (0.25, 2.5);
/// 回転の乱数の範囲（度）。
const ROTATION_RANGE: f32 = 180.0;
/// レイヤーの乱数の範囲。
const LAYER_RANGE: i64 = 5;

/// 決定的な乱数（SplitMix64）。
pub(super) struct Rng(u64);

impl Rng {
    /// 種から作る。
    pub(super) fn new(seed: u64) -> Self {
        Self(seed ^ 0x9E37_79B9_7F4A_7C15)
    }

    /// 次の 64 bit。
    pub(super) fn next_u64(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    /// [0, 1) の実数。
    pub(super) fn unit(&mut self) -> f32 {
        (self.next_u64() >> 40) as f32 / (1u64 << 24) as f32
    }

    /// [lo, hi) の実数。
    pub(super) fn range(&mut self, lo: f32, hi: f32) -> f32 {
        lo + (hi - lo) * self.unit()
    }

    /// 確率 p で true。
    pub(super) fn chance(&mut self, p: f32) -> bool {
        self.unit() < p
    }

    /// [0, n) の整数。
    pub(super) fn below(&mut self, n: u64) -> u64 {
        self.next_u64() % n.max(1)
    }
}

/// ランダムなシーン 1 つ（木と、レイアウトの文脈）。
pub(super) struct RandomScene {
    /// アクター木とコンポーネント。
    pub scene: Scene,
    /// テキストの実測枠（Text スロット entity → 枠）。
    pub text_bounds: TextBoundsMap,
    /// ルートキャンバスのビューポートの上書き。
    pub overrides: HashMap<Entity, [f32; 2]>,
    /// ルートキャンバスの自動解像度。
    pub root_auto: HashMap<Entity, [f32; 2]>,
    /// 基準ビューポート（None = アクター編集タブ相当）。
    pub viewport: Option<[f32; 2]>,
    /// 設計空間表示か。
    pub design_space: bool,
    /// 選択中の DFS 番号（キャンバス枠の太線・色の分岐を通すため）。
    pub selected: Vec<usize>,
}

/// ランダムなシーンを作る。
pub(super) fn generate(seed: u64) -> RandomScene {
    let mut rng = Rng::new(seed);
    let mut world = World::new();
    let mut text_bounds = TextBoundsMap::new();
    let mut overrides = HashMap::new();
    let mut root_auto = HashMap::new();
    let mut roots = Vec::new();
    let root_count = 1 + rng.below(MAX_ROOTS);
    for _ in 0..root_count {
        let wl = if rng.chance(0.2) { OTHER_WORLD_LINE } else { TARGET_WORLD_LINE };
        let root = random_node(&mut rng, &mut world, &mut text_bounds, 0, wl);
        // ルートキャンバスの自動解像度・ビューポートの上書き（Camera 参照）
        if rng.chance(0.3) {
            root_auto.insert(root.entity, random_viewport(&mut rng));
        }
        if rng.chance(0.3) {
            overrides.insert(root.entity, random_viewport(&mut rng));
        }
        roots.push(root);
    }
    let viewport = if rng.chance(0.2) { None } else { Some(random_viewport(&mut rng)) };
    let design_space = rng.chance(0.5);
    let total = count_nodes(&roots);
    let selected = (0..total).filter(|_| rng.chance(0.3)).collect();
    let mut scene = Scene::new("equivalence");
    scene.world = world;
    scene.actors = roots;
    RandomScene { scene, text_bounds, overrides, root_auto, viewport, design_space, selected }
}

/// 木のノードの数（子は世界線を問わず数える）。
fn count_nodes(actors: &[Actor]) -> usize {
    actors.iter().map(|a| 1 + count_nodes(&a.children)).sum()
}

/// ビューポートの大きさ（決まった大きさか、乱数の大きさ）。
fn random_viewport(rng: &mut Rng) -> [f32; 2] {
    if rng.chance(0.7) {
        VIEWPORTS[rng.below(VIEWPORTS.len() as u64) as usize]
    } else {
        [rng.range(200.0, 2600.0), rng.range(200.0, 2600.0)]
    }
}

/// 正規化の値（0・0.5・1 か乱数）。
fn random_normalized(rng: &mut Rng) -> [f32; 2] {
    match rng.below(4) {
        0 | 1 => [0.0, 0.0],
        2 => [0.5, 0.5],
        _ => [rng.unit(), rng.unit()],
    }
}

/// ランダムな CanvasTransform。
fn random_transform(rng: &mut Rng) -> CanvasTransform {
    let position = if rng.chance(0.2) {
        [0.0, 0.0]
    } else {
        [rng.range(-POSITION_RANGE, POSITION_RANGE), rng.range(-POSITION_RANGE, POSITION_RANGE)]
    };
    let rotation = match rng.below(7) {
        0..=4 => 0.0,
        5 => 90.0 * (rng.below(4) as f32),
        _ => rng.range(-ROTATION_RANGE, ROTATION_RANGE),
    };
    let scale = if rng.chance(0.6) {
        [1.0, 1.0]
    } else {
        let sign = if rng.chance(0.05) { -1.0 } else { 1.0 };
        [sign * rng.range(SCALE_RANGE.0, SCALE_RANGE.1), rng.range(SCALE_RANGE.0, SCALE_RANGE.1)]
    };
    CanvasTransform {
        position,
        rotation,
        scale,
        pivot: random_normalized(rng),
        anchor: random_normalized(rng),
        scale_transform: rng.chance(0.85),
        scale_size: rng.chance(0.85),
        keep_aspect_ratio: rng.chance(0.2),
        aspect_ratio_axis: if rng.chance(0.5) { AspectRatioAxis::Width } else { AspectRatioAxis::Height },
    }
}

/// ランダムなノード（とその子孫）を作る。
fn random_node(
    rng: &mut Rng,
    world: &mut World,
    text_bounds: &mut TextBoundsMap,
    depth: u32,
    wl: u32,
) -> Actor {
    let entity = world.spawn();
    let roll = rng.unit();
    let mut actor = if roll < 0.15 {
        // 2D フォルダ（生成時に恒等 CanvasTransform を持つ。レイアウト透明）
        world.insert(entity, CanvasTransform::default());
        Actor::new_folder_2d(entity, "Folder")
    } else if roll < 0.23 {
        // 3D アクター（CanvasTransform なし）。ときどき CanvasComponent も持つ（3D ワールドキャンバス相当）
        let mut a = Actor::new(entity, "Actor3D");
        if rng.chance(0.5) {
            add_canvas(rng, world, &mut a);
        }
        a
    } else {
        world.insert(entity, random_transform(rng));
        let mut a = Actor::new_2d(entity, "Node");
        if rng.chance(0.4) {
            add_canvas(rng, world, &mut a);
            if rng.chance(0.1) {
                // 2 つ目の Canvas スロット（枠はスロットごと・レイアウトは最初のもの）
                add_canvas(rng, world, &mut a);
            }
        }
        for _ in 0..rng.below(3) {
            add_sprite(rng, world, &mut a);
        }
        if rng.chance(0.3) {
            add_text(rng, world, text_bounds, &mut a);
        }
        if rng.chance(0.2) {
            add_collider(rng, world, &mut a);
        }
        a
    };
    actor.world_line = wl;
    actor.active = rng.chance(0.9);
    actor.visible = rng.chance(0.9);
    if depth < MAX_DEPTH {
        for _ in 0..rng.below(MAX_CHILDREN + 1) {
            let child = random_node(rng, world, text_bounds, depth + 1, wl);
            actor.add_child(child);
        }
    }
    actor
}

/// CanvasComponent のスロットを足す。
fn add_canvas(rng: &mut Rng, world: &mut World, actor: &mut Actor) {
    let slot = world.spawn();
    // まれに大きさ 0（自動スケールの割り算の扱いの違いを通す）
    let size = if rng.chance(0.03) {
        [0.0, rng.range(CANVAS_SIZE_RANGE.0, CANVAS_SIZE_RANGE.1)]
    } else {
        [rng.range(CANVAS_SIZE_RANGE.0, CANVAS_SIZE_RANGE.1), rng.range(CANVAS_SIZE_RANGE.0, CANVAS_SIZE_RANGE.1)]
    };
    world.insert(
        slot,
        CanvasComponent {
            width: size[0],
            height: size[1],
            auto_scale: rng.chance(0.5),
            draw_zone: if rng.chance(0.3) { CanvasDrawZone::Background } else { CanvasDrawZone::Foreground },
            ..CanvasComponent::default()
        },
    );
    actor.add_slot_typed::<CanvasComponent>("Canvas", ComponentKind::Canvas, slot);
}

/// SpriteComponent のスロットを足す。
fn add_sprite(rng: &mut Rng, world: &mut World, actor: &mut Actor) {
    let slot = world.spawn();
    world.insert(
        slot,
        SpriteComponent {
            width: rng.range(SPRITE_SIZE_RANGE.0, SPRITE_SIZE_RANGE.1),
            height: rng.range(SPRITE_SIZE_RANGE.0, SPRITE_SIZE_RANGE.1),
            layer: rng.below(LAYER_RANGE as u64) as i32 - (LAYER_RANGE as i32 / 2),
            raycast_target: rng.chance(0.6),
            texture_path: if rng.chance(0.5) { String::new() } else { "assets://t.png".to_string() },
            ..SpriteComponent::default()
        },
    );
    actor.add_slot_typed::<SpriteComponent>("Sprite", ComponentKind::Sprite, slot);
    if rng.chance(0.15) {
        if let Some(last) = actor.slots_mut().last_mut() {
            last.enabled = false;
        }
    }
}

/// TextComponent のスロットと実測枠を足す。
fn add_text(rng: &mut Rng, world: &mut World, text_bounds: &mut TextBoundsMap, actor: &mut Actor) {
    let slot = world.spawn();
    world.insert(
        slot,
        TextComponent {
            layer: rng.below(LAYER_RANGE as u64) as i32,
            content: "text".to_string(),
            ..TextComponent::default()
        },
    );
    actor.add_slot_typed::<TextComponent>("Text", ComponentKind::Text, slot);
    if rng.chance(0.8) {
        let min = [rng.range(-80.0, 0.0), rng.range(-40.0, 0.0)];
        let max = [min[0] + rng.range(0.0, 300.0), min[1] + rng.range(0.0, 80.0)];
        text_bounds.insert(slot, TextBounds { local: TextLocalBox { min, max }, zero_pivot: rng.chance(0.3) });
    }
    if rng.chance(0.1) {
        if let Some(last) = actor.slots_mut().last_mut() {
            last.enabled = false;
        }
    }
}

/// Collider2dComponent のスロットを足す。
fn add_collider(rng: &mut Rng, world: &mut World, actor: &mut Actor) {
    let slot = world.spawn();
    let shape = if rng.chance(0.5) {
        ColliderShape2dData::Box { half_extents: [rng.range(1.0, 200.0), rng.range(1.0, 200.0)] }
    } else {
        ColliderShape2dData::Circle { radius: rng.range(1.0, 200.0) }
    };
    world.insert(
        slot,
        Collider2dComponent {
            shape,
            keep_aspect_ratio: rng.chance(0.3),
            aspect_ratio_axis: if rng.chance(0.5) { AspectRatioAxis::Width } else { AspectRatioAxis::Height },
            ..Collider2dComponent::default()
        },
    );
    actor.add_slot_typed::<Collider2dComponent>("Collider2d", ComponentKind::Collider2d, slot);
    if rng.chance(0.2) {
        if let Some(last) = actor.slots_mut().last_mut() {
            last.enabled = false;
        }
    }
}
