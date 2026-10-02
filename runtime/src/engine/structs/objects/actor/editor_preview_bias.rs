// ============================================================
//  actor/editor_preview_bias.rs — エディタのプレビューの根へレイヤーの底上げを付ける
//
//  【なぜ要るか】
//  SEED の 2D は「ゾーン → レイヤー → 種別（スプライト → … → テキスト）」の順に描くので、同じレイヤーだと
//  下の画面の文字が上の画面の板より手前に出る（docs/ui_navigation.md §6）。実行時は ScreenStack が段 i の枠に
//  i × LayerStep、ModalHost が帯に 100 万・200 万・300 万を `CanvasLayoutItemComponent::layer_bias`
//  （`#[serde(skip)]` の実行中だけの欄）で付けるが、Edit ではスクリプトが動かないので誰も付けない。
//  そこでプレビューの根に、エディタが決めた値（`EditorPreviewInfo::layer_bias`）を付けて実行時の見た目に近づける。
//
//  【呼ぶ所】
//  ・プレビューを組み立てたとき（app/editor_preview/ops.rs。根へ印を付けた直後）
//  ・ActorData から組み直したとき（core/app_base/scene.rs の build_actor）。`layer_bias` は保存されない欄なので、
//    Undo/Redo・Play 停止の復元（写しの ActorData から組み直す）のたびに印の値から付け直す。
//  純粋なデータの editor_preview.rs と分けたのは、ここが World のコンポーネントに書くため（単一責任）。
// ============================================================

use crate::engine::components::{CanvasLayoutItemComponent, ComponentKind};
use crate::engine::ecs::World;

use super::editor_preview::NO_LAYER_BIAS;
use super::Actor;

/// プレビューの根の印にあるレイヤーの底上げを、根の最初の CanvasLayoutItem スロットの `layer_bias` へ書く。
///
/// 加算ではなく上書きする（組み直しのたびに呼んでも値が積み上がらない）。子のスロットには触れない
/// （底上げはレイアウトの走査で子孫へ足し込まれる）。
///
/// # 戻り値
/// 書けたら true。根に CanvasLayoutItem スロットが無い（World に実体が無い）ときは何もしないで false
/// （呼び出し側が「重なって見えることがある」と知らせる）。
/// 印が無い・底上げが 0 のときは付けるものが無いので、何もしないで true。
pub fn apply_preview_layer_bias(world: &mut World, actor: &Actor) -> bool {
    let bias = actor.editor_preview.as_ref().map_or(NO_LAYER_BIAS, |info| info.layer_bias);
    if bias == NO_LAYER_BIAS {
        return true;
    }
    let Some(slot_entity) = actor.first_slot_entity_of_kind(ComponentKind::CanvasLayoutItem) else {
        return false;
    };
    match world.get_mut::<CanvasLayoutItemComponent>(slot_entity) {
        Some(item) => {
            item.layer_bias = bias;
            true
        }
        None => false,
    }
}

// ============================================================
//  テスト — 書けること・スロットが無ければ false・0 なら触らないこと
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::structs::objects::actor::EditorPreviewInfo;

    /// テスト用: 底上げの値つきのプレビューの根の印。
    fn info(layer_bias: i32) -> EditorPreviewInfo {
        EditorPreviewInfo {
            prefab: "assets://ui/screens/AlarmEdit.actor".to_string(),
            frame: None,
            frame_body: String::new(),
            layer_bias,
        }
    }

    /// テスト用: 2D のアクタを作り、`with_item` なら CanvasLayoutItem スロットを 1 つ付ける（スロットの実体を返す）。
    fn actor_2d(world: &mut World, with_item: bool) -> (Actor, Option<crate::engine::ecs::Entity>) {
        let mut actor = Actor::new_2d(world.spawn(), "Screen");
        if !with_item {
            return (actor, None);
        }
        let slot = world.spawn();
        world.insert(slot, CanvasLayoutItemComponent::default());
        actor.add_slot_typed::<CanvasLayoutItemComponent>("CanvasLayoutItem", ComponentKind::CanvasLayoutItem, slot);
        (actor, Some(slot))
    }

    /// 根の CanvasLayoutItem に底上げが書かれ（上書きなので 2 回呼んでも積み上がらない）、true を返すこと。
    #[test]
    fn writes_bias_into_first_layout_item() {
        const BIAS: i32 = 3_000_000;
        let mut world = World::new();
        let (mut actor, slot) = actor_2d(&mut world, true);
        actor.editor_preview = Some(info(BIAS));

        assert!(apply_preview_layer_bias(&mut world, &actor), "書けたら true");
        assert!(apply_preview_layer_bias(&mut world, &actor), "組み直しで再び呼んでもよい");
        let item = world.get::<CanvasLayoutItemComponent>(slot.unwrap()).unwrap();
        assert_eq!(item.layer_bias, BIAS, "上書きなので積み上がらない");
    }

    /// 根に CanvasLayoutItem スロットが無ければ何もしないで false。
    #[test]
    fn returns_false_without_layout_item() {
        let mut world = World::new();
        let (mut actor, _) = actor_2d(&mut world, false);
        actor.editor_preview = Some(info(10_000));
        assert!(!apply_preview_layer_bias(&mut world, &actor), "スロットが無ければ false");
    }

    /// 底上げが 0・印が無いときは触らない（既にある値もそのまま）で true。
    #[test]
    fn zero_bias_or_no_mark_leaves_component_untouched() {
        const EXISTING: i32 = 7;
        let mut world = World::new();
        let (mut actor, slot) = actor_2d(&mut world, true);
        let slot = slot.unwrap();
        world.get_mut::<CanvasLayoutItemComponent>(slot).unwrap().layer_bias = EXISTING;

        actor.editor_preview = Some(info(NO_LAYER_BIAS));
        assert!(apply_preview_layer_bias(&mut world, &actor), "0 なら何もしないで true");
        assert_eq!(world.get::<CanvasLayoutItemComponent>(slot).unwrap().layer_bias, EXISTING, "0 なら触らない");

        actor.editor_preview = None;
        assert!(apply_preview_layer_bias(&mut world, &actor), "印が無ければ何もしないで true");
        assert_eq!(world.get::<CanvasLayoutItemComponent>(slot).unwrap().layer_bias, EXISTING);
    }
}
