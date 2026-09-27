// ============================================================
//  actor/canvas_layout_slots.rs — キャンバス UI の純データの部品のスロットの出し入れを 1 か所にまとめる
//
//  W2-1b で足した 5 種（CanvasStack・CanvasWrap・CanvasGrid・CanvasLayoutItem・CanvasSafeArea）と、
//  W2-2 で足した CanvasGesture（ジェスチャーを受けるノード）は、どれも「値だけの純データ」で、スロットの作成・
//  保存・読込・複製・削除・既定値・インスペクタへの送信の手順がまったく同じになる。各連携点（scene.rs の
//  build_actor・slot_ops.rs・component_ops.rs・component_reset_ops.rs・field_edit.rs・slot_to_data）に種類ごとの
//  腕を並べる代わりに、連携点はここの関数を 1 行呼ぶだけにする（種類を足すときの直し漏れを防ぐ。
//  種類ごとの違いはこのファイルの表だけ）。名前の「layout」は W2-1b の名残（インスペクタの鍵・IPC の
//  SET_CANVAS_LAYOUT_FIELD も同じ。W2-2 の CanvasGesture もこの鍵・命令で編集する）。
//
//  【種類の表】`LAYOUT_KINDS`（エディタの "type" 名 ⇔ ComponentKind）。match の腕はこのファイルの中だけにある。
// ============================================================

use serde::Serialize;

use crate::engine::components::{
    CanvasGestureComponent, CanvasGridComponent, CanvasLayoutItemComponent, CanvasSafeAreaComponent,
    CanvasStackComponent, CanvasWrapComponent, ComponentData, ComponentKind,
};
use crate::engine::ecs::{Entity, World};

use super::ComponentSlot;

/// インスペクタへ送る JSON で、レイアウトの部品の値を入れる鍵（スロット共通の "enabled" と鍵がぶつからないよう入れ子にする）。
pub const INSPECTOR_KEY: &str = "layout";

/// キャンバス UI の純データの部品の種類の表（エディタの "type" 名 ⇔ ComponentKind）。
pub const LAYOUT_KINDS: [(&str, ComponentKind); 6] = [
    ("CanvasStackComponent", ComponentKind::CanvasStack),
    ("CanvasWrapComponent", ComponentKind::CanvasWrap),
    ("CanvasGridComponent", ComponentKind::CanvasGrid),
    ("CanvasLayoutItemComponent", ComponentKind::CanvasLayoutItem),
    ("CanvasSafeAreaComponent", ComponentKind::CanvasSafeArea),
    // ジェスチャーを受けるノード（W2-2）
    ("CanvasGestureComponent", ComponentKind::CanvasGesture),
];

/// レイアウトの部品の種類か。
pub fn is_layout_kind(kind: ComponentKind) -> bool {
    LAYOUT_KINDS.iter().any(|(_, k)| *k == kind)
}

/// エディタの "type" 名からレイアウトの部品の種類を引く（該当しなければ None）。
pub fn kind_of_type_name(type_name: &str) -> Option<ComponentKind> {
    LAYOUT_KINDS.iter().find(|(name, _)| *name == type_name).map(|(_, k)| *k)
}

/// ComponentData がレイアウトの部品なら、その種類を返す（該当しなければ None）。
pub fn kind_of_data(data: &ComponentData) -> Option<ComponentKind> {
    match data {
        ComponentData::CanvasStackComponent(_) => Some(ComponentKind::CanvasStack),
        ComponentData::CanvasWrapComponent(_) => Some(ComponentKind::CanvasWrap),
        ComponentData::CanvasGridComponent(_) => Some(ComponentKind::CanvasGrid),
        ComponentData::CanvasLayoutItemComponent(_) => Some(ComponentKind::CanvasLayoutItem),
        ComponentData::CanvasSafeAreaComponent(_) => Some(ComponentKind::CanvasSafeArea),
        ComponentData::CanvasGestureComponent(_) => Some(ComponentKind::CanvasGesture),
        _ => None,
    }
}

/// データからコンポーネントを World のスロット専用エンティティへ入れ、スロットを作る
/// （シーンの読込・Undo の復元・複製）。レイアウトの部品でなければ何もせず None。
///
/// # 引数
/// * `world`       - コンポーネントの置き場
/// * `slot_entity` - スロット専用のエンティティ（呼び出し側が spawn 済み）
/// * `name`        - スロット名
/// * `data`        - シリアライズ用データ
pub fn insert_from_data(
    world: &mut World,
    slot_entity: Entity,
    name: impl Into<String>,
    data: &ComponentData,
) -> Option<ComponentSlot> {
    let slot = match data {
        ComponentData::CanvasStackComponent(d) => {
            world.insert(slot_entity, CanvasStackComponent::from_data(d.clone()));
            ComponentSlot::new::<CanvasStackComponent>(name, ComponentKind::CanvasStack, slot_entity)
        }
        ComponentData::CanvasWrapComponent(d) => {
            world.insert(slot_entity, CanvasWrapComponent::from_data(d.clone()));
            ComponentSlot::new::<CanvasWrapComponent>(name, ComponentKind::CanvasWrap, slot_entity)
        }
        ComponentData::CanvasGridComponent(d) => {
            world.insert(slot_entity, CanvasGridComponent::from_data(d.clone()));
            ComponentSlot::new::<CanvasGridComponent>(name, ComponentKind::CanvasGrid, slot_entity)
        }
        ComponentData::CanvasLayoutItemComponent(d) => {
            world.insert(slot_entity, CanvasLayoutItemComponent::from_data(d.clone()));
            ComponentSlot::new::<CanvasLayoutItemComponent>(name, ComponentKind::CanvasLayoutItem, slot_entity)
        }
        ComponentData::CanvasSafeAreaComponent(d) => {
            world.insert(slot_entity, CanvasSafeAreaComponent::from_data(d.clone()));
            ComponentSlot::new::<CanvasSafeAreaComponent>(name, ComponentKind::CanvasSafeArea, slot_entity)
        }
        ComponentData::CanvasGestureComponent(d) => {
            world.insert(slot_entity, CanvasGestureComponent::from_data(d.clone()));
            ComponentSlot::new::<CanvasGestureComponent>(name, ComponentKind::CanvasGesture, slot_entity)
        }
        _ => return None,
    };
    Some(slot)
}

/// Undo の復元（値の詰め替え）: スロットのエンティティのコンポーネントをデータで置き換える。
///
/// # 戻り値
/// レイアウトの部品なら true（置き換えた）、そうでなければ false（何もしない）。
pub fn apply_in_place(world: &mut World, slot_entity: Entity, data: &ComponentData) -> bool {
    match data {
        ComponentData::CanvasStackComponent(d) => world.insert(slot_entity, CanvasStackComponent::from_data(d.clone())),
        ComponentData::CanvasWrapComponent(d) => world.insert(slot_entity, CanvasWrapComponent::from_data(d.clone())),
        ComponentData::CanvasGridComponent(d) => world.insert(slot_entity, CanvasGridComponent::from_data(d.clone())),
        ComponentData::CanvasLayoutItemComponent(d) => {
            world.insert(slot_entity, CanvasLayoutItemComponent::from_data(d.clone()))
        }
        ComponentData::CanvasSafeAreaComponent(d) => {
            world.insert(slot_entity, CanvasSafeAreaComponent::from_data(d.clone()))
        }
        ComponentData::CanvasGestureComponent(d) => {
            world.insert(slot_entity, CanvasGestureComponent::from_data(d.clone()))
        }
        _ => return false,
    }
    true
}

/// スロットのコンポーネントをシリアライズ用データにする（保存・Undo のスナップショット）。
/// レイアウトの部品でない・実体が無ければ None。
pub fn to_data(world: &World, slot: &ComponentSlot) -> Option<ComponentData> {
    match slot.kind {
        ComponentKind::CanvasStack => world
            .get::<CanvasStackComponent>(slot.entity)
            .map(|c| ComponentData::CanvasStackComponent(c.to_data())),
        ComponentKind::CanvasWrap => world
            .get::<CanvasWrapComponent>(slot.entity)
            .map(|c| ComponentData::CanvasWrapComponent(c.to_data())),
        ComponentKind::CanvasGrid => world
            .get::<CanvasGridComponent>(slot.entity)
            .map(|c| ComponentData::CanvasGridComponent(c.to_data())),
        ComponentKind::CanvasLayoutItem => world
            .get::<CanvasLayoutItemComponent>(slot.entity)
            .map(|c| ComponentData::CanvasLayoutItemComponent(c.to_data())),
        ComponentKind::CanvasSafeArea => world
            .get::<CanvasSafeAreaComponent>(slot.entity)
            .map(|c| ComponentData::CanvasSafeAreaComponent(c.to_data())),
        ComponentKind::CanvasGesture => world
            .get::<CanvasGestureComponent>(slot.entity)
            .map(|c| ComponentData::CanvasGestureComponent(c.to_data())),
        _ => None,
    }
}

/// スロットのエンティティからコンポーネントを外す（スロットの削除）。
///
/// # 戻り値
/// レイアウトの部品の種類なら true（外した・もともと無かった）、そうでなければ false。
pub fn remove(world: &mut World, kind: ComponentKind, slot_entity: Entity) -> bool {
    match kind {
        ComponentKind::CanvasStack => {
            world.remove::<CanvasStackComponent>(slot_entity);
        }
        ComponentKind::CanvasWrap => {
            world.remove::<CanvasWrapComponent>(slot_entity);
        }
        ComponentKind::CanvasGrid => {
            world.remove::<CanvasGridComponent>(slot_entity);
        }
        ComponentKind::CanvasLayoutItem => {
            world.remove::<CanvasLayoutItemComponent>(slot_entity);
        }
        ComponentKind::CanvasSafeArea => {
            world.remove::<CanvasSafeAreaComponent>(slot_entity);
        }
        ComponentKind::CanvasGesture => {
            world.remove::<CanvasGestureComponent>(slot_entity);
        }
        _ => return false,
    }
    true
}

/// 種類の既定値のデータ（エディタの「コンポーネントを追加」・フィールドの既定値へのリセット）。
pub fn default_data(kind: ComponentKind) -> Option<ComponentData> {
    Some(match kind {
        ComponentKind::CanvasStack => ComponentData::CanvasStackComponent(CanvasStackComponent::default().to_data()),
        ComponentKind::CanvasWrap => ComponentData::CanvasWrapComponent(CanvasWrapComponent::default().to_data()),
        ComponentKind::CanvasGrid => ComponentData::CanvasGridComponent(CanvasGridComponent::default().to_data()),
        ComponentKind::CanvasLayoutItem => {
            ComponentData::CanvasLayoutItemComponent(CanvasLayoutItemComponent::default().to_data())
        }
        ComponentKind::CanvasSafeArea => {
            ComponentData::CanvasSafeAreaComponent(CanvasSafeAreaComponent::default().to_data())
        }
        ComponentKind::CanvasGesture => {
            ComponentData::CanvasGestureComponent(CanvasGestureComponent::default().to_data())
        }
        _ => return None,
    })
}

/// インスペクタへ送る値の JSON の断片（`,"layout":{…}`。ACTOR_COMPONENTS の 1 コンポーネントの末尾へ足す）と
/// エディタの "type" 名。レイアウトの部品でなければ None。
///
/// 値は serde の書式そのまま（列挙は snake_case・余白は {"left":…}）なので、エディタは JSON を読むだけでよい。
pub fn inspector_json(data: &ComponentData) -> Option<(&'static str, String)> {
    /// 値を `,"layout":{…}` にする（直列化できない値は空の {}）。
    fn fragment<T: Serialize>(value: &T) -> String {
        let json = serde_json::to_string(value).unwrap_or_else(|_| "{}".to_string());
        format!(r#","{INSPECTOR_KEY}":{json}"#)
    }
    let (type_name, json) = match data {
        ComponentData::CanvasStackComponent(d) => ("CanvasStackComponent", fragment(d)),
        ComponentData::CanvasWrapComponent(d) => ("CanvasWrapComponent", fragment(d)),
        ComponentData::CanvasGridComponent(d) => ("CanvasGridComponent", fragment(d)),
        ComponentData::CanvasLayoutItemComponent(d) => ("CanvasLayoutItemComponent", fragment(d)),
        ComponentData::CanvasSafeAreaComponent(d) => ("CanvasSafeAreaComponent", fragment(d)),
        ComponentData::CanvasGestureComponent(d) => ("CanvasGestureComponent", fragment(d)),
        _ => return None,
    };
    Some((type_name, json))
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 表のすべての種類で、既定値の作成 → スロット化 → 保存 → 読み戻しが往復する。
    #[test]
    fn every_kind_round_trips_through_a_slot() {
        for (type_name, kind) in LAYOUT_KINDS {
            assert_eq!(kind_of_type_name(type_name), Some(kind));
            assert!(is_layout_kind(kind));
            let data = default_data(kind).expect("既定値がある");
            assert_eq!(kind_of_data(&data), Some(kind));
            let mut world = World::new();
            let entity = world.spawn();
            let slot = insert_from_data(&mut world, entity, "Layout", &data).expect("スロットになる");
            assert_eq!(slot.kind, kind);
            let back = to_data(&world, &slot).expect("保存できる");
            assert_eq!(serde_json::to_string(&back).unwrap(), serde_json::to_string(&data).unwrap());
            let (name, json) = inspector_json(&back).expect("インスペクタへ送れる");
            assert_eq!(name, type_name);
            assert!(json.starts_with(r#","layout":{"#), "{json}");
            assert!(remove(&mut world, kind, entity));
            assert!(to_data(&world, &slot).is_none());
        }
        assert!(!is_layout_kind(ComponentKind::Sprite));
        assert!(kind_of_type_name("SpriteComponent").is_none());
    }
}
