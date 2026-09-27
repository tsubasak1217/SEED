// ============================================================
//  canvas_layout_ops.rs — レイアウトの部品（W2-1b）のインスペクタ更新
//
//  ・handle_set_canvas_layout_field: SET_CANVAS_LAYOUT_FIELD:{actor},{slot},{key},{value}
//      CanvasStack・CanvasWrap・CanvasGrid・CanvasLayoutItem・CanvasSafeArea と W2-2 の CanvasGesture の
//      1 つの欄を書き換える。6 種とも値だけの純データなので、欄ごとの分岐を書かずに「コンポーネントを JSON にして、鍵の指す欄を
//      置き換えて、元の型へ戻す」で扱う（データ駆動。欄を足しても、ここは直さなくてよい）。
//        - key   … serde の欄の名前。入れ子は "/" で区切る（例 "padding/left"。FieldReset の field_path と同じ書式）
//        - value … JSON の値として読めればそれ（数値・true/false）、読めなければ文字列（列挙の名前 "space_between" など）
//      型に合わない値（範囲外の列挙・数値の欄への文字列）は元の型へ戻せないので、何も変えずに捨てる。
//  ・handle_set_canvas_unit: SET_CANVAS_UNIT:{actor},{slot},{px|dp}（CanvasComponent の寸法の単位）
//  Undo は field_edit.rs の共通機構（field_edit_target の分類）が記録するので、ここでは書かない。
// ============================================================

use serde::de::DeserializeOwned;
use serde::Serialize;
use serde_json::Value;

use crate::engine::components::{
    CanvasComponent, CanvasGestureComponent, CanvasGridComponent, CanvasLayoutItemComponent,
    CanvasSafeAreaComponent, CanvasScrollComponent, CanvasStackComponent, CanvasUnit, CanvasWrapComponent, ComponentKind,
};
use crate::engine::ecs::{Entity, World};

use super::App;

/// 入れ子の欄の区切り（"padding/left"）。
const FIELD_PATH_SEPARATOR: char = '/';

/// dp を表す値の文字列（SET_CANVAS_UNIT）。
const UNIT_DP: &str = "dp";
/// px を表す値の文字列（SET_CANVAS_UNIT）。
const UNIT_PX: &str = "px";

/// インスペクタから届いた値の文字列を JSON の値にする【純関数】。
///
/// JSON として読めれば（数値・true/false・null）それ、読めなければ文字列（列挙の名前）。
fn parse_value(value: &str) -> Value {
    let trimmed = value.trim();
    serde_json::from_str::<Value>(trimmed).unwrap_or_else(|_| Value::String(trimmed.to_string()))
}

/// 値の型のまま、鍵の指す 1 つの欄を置き換えた新しい値を作る【純関数】。
///
/// # 引数
/// * `current` - 今の値
/// * `key`     - 欄の名前（入れ子は "/" 区切り）
/// * `value`   - 値の文字列
///
/// # 戻り値
/// 置き換えた値。欄が無い・型に合わないときは None（呼び出し側は何も変えない）。
pub(super) fn with_field<T: Serialize + DeserializeOwned>(current: &T, key: &str, value: &str) -> Option<T> {
    let mut json = serde_json::to_value(current).ok()?;
    let mut target = &mut json;
    for part in key.split(FIELD_PATH_SEPARATOR) {
        // 知らない欄は足さない（serde が読み飛ばして「変わらないのに変更扱い」になるのを防ぐ）
        target = target.as_object_mut()?.get_mut(part)?;
    }
    *target = parse_value(value);
    serde_json::from_value(json).ok()
}

/// スロットのエンティティのレイアウトの部品の 1 つの欄を置き換える。
///
/// # 戻り値
/// 置き換えたら true。
fn set_layout_field(world: &mut World, kind: ComponentKind, entity: Entity, key: &str, value: &str) -> bool {
    /// 型ごとの置き換え（実体を取り出して新しい値を入れ直す）。
    fn apply<T: Serialize + DeserializeOwned + crate::engine::ecs::Component>(
        world: &mut World,
        entity: Entity,
        key: &str,
        value: &str,
    ) -> bool {
        let Some(current) = world.get::<T>(entity) else { return false };
        let Some(next) = with_field(current, key, value) else { return false };
        world.insert(entity, next);
        true
    }
    match kind {
        ComponentKind::CanvasStack => apply::<CanvasStackComponent>(world, entity, key, value),
        ComponentKind::CanvasWrap => apply::<CanvasWrapComponent>(world, entity, key, value),
        ComponentKind::CanvasGrid => apply::<CanvasGridComponent>(world, entity, key, value),
        ComponentKind::CanvasLayoutItem => apply::<CanvasLayoutItemComponent>(world, entity, key, value),
        ComponentKind::CanvasSafeArea => apply::<CanvasSafeAreaComponent>(world, entity, key, value),
        // ジェスチャーを受けるノード（W2-2）: 旗・軸（any / horizontal / vertical）・最小のヒット領域
        ComponentKind::CanvasGesture => apply::<CanvasGestureComponent>(world, entity, key, value),
        // スクロールの領域の設定（W2-3。設定だけを差し替える＝同じエンティティの実行中の状態は残る）
        ComponentKind::CanvasScroll => apply::<CanvasScrollComponent>(world, entity, key, value),
        _ => false,
    }
}

impl App {
    /// 対象スロット（指定の種類だけ）のエンティティと種類を引く。
    fn layout_slot_entity(
        &self,
        actor_dfs_id: u32,
        slot_idx: u32,
        accept: impl Fn(ComponentKind) -> bool,
    ) -> Option<(Entity, ComponentKind)> {
        use super::find_actor_by_dfs;
        let scene = self.scene.as_ref()?;
        let mut c = 0u32;
        find_actor_by_dfs(&scene.actors, self.active_world_line, actor_dfs_id, &mut c)
            .and_then(|a| a.slots().get(slot_idx as usize))
            .filter(|s| accept(s.kind))
            .map(|s| (s.entity, s.kind))
    }

    /// 変更をインスペクタへ送り返し、シーンの変更を知らせる。
    fn notify_layout_changed(&self, actor_dfs_id: u32) {
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// インスペクタからのレイアウトの部品の欄の更新（SET_CANVAS_LAYOUT_FIELD）。
    ///
    /// # 引数
    /// * `actor_dfs_id` - 対象アクターの DFS 番号
    /// * `slot_idx`     - 対象スロットの添字（レイアウトの部品以外のスロットへの誤配は弾く）
    /// * `key`          - 欄の名前（serde の名前。入れ子は "/" 区切り）
    /// * `value`        - 値の文字列（型に合わない値は無視し、インスペクタへの再送信もしない）
    pub(super) fn handle_set_canvas_layout_field(&mut self, actor_dfs_id: u32, slot_idx: u32, key: &str, value: &str) {
        use crate::engine::structs::objects::actor::canvas_layout_slots::is_layout_kind;
        let Some((entity, kind)) = self.layout_slot_entity(actor_dfs_id, slot_idx, is_layout_kind) else { return };
        let Some(scene) = &mut self.scene else { return };
        if !set_layout_field(&mut scene.world, kind, entity, key, value) {
            return;
        }
        self.notify_layout_changed(actor_dfs_id);
    }

    /// CanvasComponent の寸法の単位を更新する（SET_CANVAS_UNIT。ビューポート・ルートキャンバス用）。
    ///
    /// unit: "dp" = 端末に依らない単位、"px" = 画素（従来動作）。それ以外は無視する。
    pub(super) fn handle_set_canvas_unit(&mut self, actor_dfs_id: u32, slot_idx: u32, unit: &str) {
        let unit = match unit.trim() {
            UNIT_DP => CanvasUnit::Dp,
            UNIT_PX => CanvasUnit::Px,
            _ => return,
        };
        let Some((entity, _)) = self.layout_slot_entity(actor_dfs_id, slot_idx, |k| k == ComponentKind::Canvas) else {
            return;
        };
        let Some(scene) = &mut self.scene else { return };
        let Some(cc) = scene.world.get_mut::<CanvasComponent>(entity) else { return };
        cc.unit = unit;
        self.notify_layout_changed(actor_dfs_id);
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::{LayoutDirection, MainAlign};

    /// 数値・真偽・列挙の名前・入れ子の欄を置き換えられる。
    #[test]
    fn with_field_sets_numbers_bools_enums_and_nested() {
        let c = CanvasStackComponent::default();
        let c = with_field(&c, "spacing", "12.5").unwrap();
        assert_eq!(c.spacing, 12.5);
        let c = with_field(&c, "reverse", "true").unwrap();
        assert!(c.reverse);
        let c = with_field(&c, "direction", "horizontal").unwrap();
        assert_eq!(c.direction, LayoutDirection::Horizontal);
        let c = with_field(&c, "main_align", "space_between").unwrap();
        assert_eq!(c.main_align, MainAlign::SpaceBetween);
        let c = with_field(&c, "padding/left", "8").unwrap();
        assert_eq!(c.padding.left, 8.0);
    }

    /// 型に合わない値・知らない欄は置き換えない（None）。
    #[test]
    fn with_field_rejects_bad_values_and_unknown_keys() {
        let c = CanvasGridComponent::default();
        assert!(with_field(&c, "columns", "-1").is_none(), "u32 に負は入らない");
        assert!(with_field(&c, "cell_align", "diagonal").is_none(), "知らない列挙の名前");
        assert!(with_field(&c, "spacing_x", "abc").is_none(), "数値の欄に文字列");
        assert!(with_field(&c, "no_such_field", "1").is_none());
        assert!(with_field(&c, "padding/no_such", "1").is_none());
    }

    /// World の実体を種類ごとに置き換える（種類の違うスロットは触らない）。
    #[test]
    fn set_layout_field_updates_the_world() {
        let mut world = World::new();
        let e = world.spawn();
        world.insert(e, CanvasSafeAreaComponent::default());
        assert!(set_layout_field(&mut world, ComponentKind::CanvasSafeArea, e, "bottom", "false"));
        assert!(!world.get::<CanvasSafeAreaComponent>(e).unwrap().bottom);
        assert!(!set_layout_field(&mut world, ComponentKind::CanvasStack, e, "spacing", "1"), "実体が無い");
        assert!(!set_layout_field(&mut world, ComponentKind::Sprite, e, "bottom", "true"), "種類が違う");
    }

    /// ジェスチャーを受けるノード（W2-2）の旗・軸・大きさも同じ命令で書き換えられる（範囲外の軸は捨てる）。
    #[test]
    fn set_layout_field_updates_gesture_component() {
        use crate::engine::components::GestureDragAxis;
        let mut world = World::new();
        let e = world.spawn();
        world.insert(e, CanvasGestureComponent::default());
        assert!(set_layout_field(&mut world, ComponentKind::CanvasGesture, e, "drag", "true"));
        assert!(set_layout_field(&mut world, ComponentKind::CanvasGesture, e, "drag_axis", "vertical"));
        assert!(set_layout_field(&mut world, ComponentKind::CanvasGesture, e, "min_hit_size_dp", "56"));
        assert!(!set_layout_field(&mut world, ComponentKind::CanvasGesture, e, "drag_axis", "diagonal"));
        let c = world.get::<CanvasGestureComponent>(e).unwrap();
        assert!(c.drag && c.drag_axis == GestureDragAxis::Vertical && c.min_hit_size_dp == 56.0);
    }
}
