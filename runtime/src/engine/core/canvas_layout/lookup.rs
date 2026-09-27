// ============================================================
//  canvas_layout/lookup.rs — レイアウトの走査がノードのスロットから読む値（W2-1b）
//
//  「ノードの自分の大きさ」を決める材料（スプライト・テキストの枠）と、レイアウトの部品
//  （CanvasLayoutItem・CanvasSafeArea）の引き方を 1 か所に置く。どれもスロットの有効・無効を見る
//  （無効なスロットは付いていないのと同じ）。CanvasComponent と切り抜きの引き方は W2-1a のまま pass.rs にある。
// ============================================================

use crate::engine::components::{
    CanvasLayoutItemComponent, CanvasSafeAreaComponent, ComponentKind, SpriteComponent, TextComponent,
};
use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;

/// 子の側の指定（有効なスロットの最初の CanvasLayoutItem。無ければ None）。
pub fn layout_item_of<'w>(actor: &Actor, world: &'w World) -> Option<&'w CanvasLayoutItemComponent> {
    actor
        .slots()
        .iter()
        .filter(|s| s.kind == ComponentKind::CanvasLayoutItem && s.enabled)
        .find_map(|s| world.get::<CanvasLayoutItemComponent>(s.entity))
}

/// 安全領域の部品（有効なスロットの最初の CanvasSafeArea で、コンポーネントも有効なもの。無ければ None）。
pub fn safe_area_of<'w>(actor: &Actor, world: &'w World) -> Option<&'w CanvasSafeAreaComponent> {
    actor
        .slots()
        .iter()
        .filter(|s| s.kind == ComponentKind::CanvasSafeArea && s.enabled)
        .find_map(|s| world.get::<CanvasSafeAreaComponent>(s.entity))
        .filter(|c| c.enabled)
}

/// 最初の有効なスプライトの大きさ（キャンバスの単位。無ければ None）。
///
/// 切り抜きの矩形（clip.rs の FirstSprite）と同じ選び方。
pub fn first_sprite_size(actor: &Actor, world: &World) -> Option<[f32; 2]> {
    actor
        .slots()
        .iter()
        .filter(|s| s.kind == ComponentKind::Sprite && s.enabled)
        .find_map(|s| world.get::<SpriteComponent>(s.entity))
        .map(|sc| [sc.width, sc.height])
}

/// 最初の有効なテキストの枠の大きさ（軸ごと。枠が 0 以下の軸は None）。
///
/// 枠の無いテキスト（大きさは文字で決まる）はここでは測れない（文字の寸法は W2-6 の Text.Measure）。
/// 大きさを決めたいときは CanvasLayoutItem の preferred_* で指定する。
pub fn text_box_size(actor: &Actor, world: &World) -> [Option<f32>; 2] {
    actor
        .slots()
        .iter()
        .filter(|s| s.kind == ComponentKind::Text && s.enabled)
        .find_map(|s| world.get::<TextComponent>(s.entity))
        .map_or([None, None], |tc| {
            [
                (tc.box_width > 0.0).then_some(tc.box_width),
                (tc.box_height > 0.0).then_some(tc.box_height),
            ]
        })
}

/// ノードが非表示・無効か（コンテナの「非表示・無効の子の扱い」の判定）。
pub fn is_hidden(actor: &Actor) -> bool {
    !actor.visible || !actor.active
}
