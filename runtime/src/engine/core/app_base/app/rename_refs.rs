// ============================================================
//  rename_refs.rs — アクタリネーム時の「アクタ名参照」追従更新
//
//  【背景】
//  シーン内にはアクタを「名前の文字列」で参照する仕組みが 3 つある:
//    1. スクリプトの [SerializeField] 参照フィールド
//       （ScriptComponent.fields、値は "アクタ名" または "アクタ名|スロット名"）
//    2. WaterVolumeComponent.control_point_ref（川の制御点参照、値は "アクタ名"）
//    3. CanvasViewportRef::Camera { actor_name, .. }（キャンバスの基準カメラ参照）
//  参照先アクタをリネームすると、これらの文字列が旧名のまま残り参照が切れる。
//
//  【方針】
//  handle_rename_actor（actor_ops.rs）から本モジュールの propagate_actor_rename を
//  呼び、同一世界線内の全アクタを DFS 走査して旧名一致の参照を新名へ書き換える。
//  同名アクタが複数存在する場合は「旧名一致の参照をすべて書き換える」割り切りとする
//  （名前参照は本質的に曖昧なため。docs/scripting_api.md の制限事項も参照）。
//
//  【スクリプトフィールドの誤書き換え防止】
//  ScriptComponent.fields は全フィールドを文字列で持ち、Rust 側だけでは
//  「参照フィールド」と「たまたま旧名と同じ値を持つプレーンな string」を
//  区別できない。値一致で候補を絞ったうえで、CLR 側リフレクション
//  （ScriptComponent::is_reference_field → ScriptBridge.IsReferenceField）で
//  参照フィールド型であることを確認してから書き換える二重ゲートにする。
//
//  【対象外】
//  - PlaceholderScriptSlot（コンパイル失敗スクリプト）: 型情報が無く参照判定
//    できないため書き換えない（コンパイルが直った後のリネームで追従される）。
// ============================================================

use crate::engine::components::{
    CanvasComponent, CanvasViewportRef, ComponentKind, ScriptComponent, WaterVolumeComponent,
};
use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;

use super::App;

impl App {
    /// 同一世界線内の全アクタを走査し、旧名 `old_name` へのアクタ名参照を
    /// 新名 `new_name` へ書き換える。
    ///
    /// 戻り値は「参照の書き換えが発生したアクタの DFS ID」のリスト
    /// （エディタへのコンポーネント再送の要否判断に使う）。
    pub(super) fn propagate_actor_rename(
        &mut self,
        wl:       u32,
        old_name: &str,
        new_name: &str,
    ) -> Vec<u32> {
        let Some(scene) = self.scene.as_mut() else { return Vec::new() };
        // actors（不変借用）と world（可変借用）は Scene の別フィールドなので
        // 同時に借用できる（分割借用）。
        let actors = &scene.actors;
        let world  = &mut scene.world;

        let mut changed = Vec::new();
        let mut counter = 0u32;
        for actor in actors.iter() {
            // DFS ID の採番規約は find_actor_by_dfs（actor_utils.rs）と完全一致させる:
            // 対象世界線のアクタのみ数え、preorder（自分 → 子孫）で 1 ずつ進める。
            if actor.world_line != wl { continue; }
            rewrite_refs_in_subtree(actor, world, old_name, new_name, &mut counter, &mut changed);
        }
        changed
    }
}

/// アクタ 1 本分のサブツリーを preorder 走査し、各スロットの参照を書き換える。
/// `counter` は世界線内 DFS ID の採番カウンタ（呼び出し側と共有）。
fn rewrite_refs_in_subtree(
    actor:    &Actor,
    world:    &mut World,
    old_name: &str,
    new_name: &str,
    counter:  &mut u32,
    changed:  &mut Vec<u32>,
) {
    let dfs_id = *counter;
    *counter += 1;

    if rewrite_refs_in_slots(actor, world, old_name, new_name) {
        changed.push(dfs_id);
    }

    for child in actor.children() {
        rewrite_refs_in_subtree(child, world, old_name, new_name, counter, changed);
    }
}

/// アクタの全スロットを調べ、旧名一致の参照を新名へ書き換える。
/// 1 件でも書き換えたら true を返す。
fn rewrite_refs_in_slots(
    actor:    &Actor,
    world:    &mut World,
    old_name: &str,
    new_name: &str,
) -> bool {
    let mut any = false;

    for slot in actor.slots() {
        match slot.kind {
            // ── 川の制御点参照（値は "アクタ名" のみ） ──────────────
            ComponentKind::WaterVolume => {
                if let Some(w) = world.get_mut::<WaterVolumeComponent>(slot.entity) {
                    if w.control_point_ref == old_name {
                        w.control_point_ref = new_name.to_string();
                        any = true;
                    }
                }
            }
            // ── キャンバスの基準カメラ参照（アクタ名＋スロット名） ──
            ComponentKind::Canvas => {
                if let Some(c) = world.get_mut::<CanvasComponent>(slot.entity) {
                    if let CanvasViewportRef::Camera { actor_name, .. } = &mut c.viewport_ref {
                        if actor_name == old_name {
                            *actor_name = new_name.to_string();
                            any = true;
                        }
                    }
                }
            }
            // ── スクリプトの [SerializeField] 参照フィールド ────────
            // 値は "アクタ名" または "アクタ名|スロット名"。
            ComponentKind::Script => {
                if let Some(sc) = world.get_mut::<ScriptComponent>(slot.entity) {
                    any |= rewrite_script_reference_fields(sc, old_name, new_name);
                }
            }
            _ => {}
        }
    }

    any
}

/// ScriptComponent の参照フィールドのうち、値が旧名に一致するものを新名へ書き換える。
///
/// 二重ゲート:
/// 1. 値ゲート — 値が旧名そのもの、または "旧名|..."（スロット指定付き）
/// 2. 型ゲート — CLR リフレクションで参照フィールド型であることを確認
///    （プレーンな string フィールドの誤書き換え防止）
fn rewrite_script_reference_fields(
    sc:       &mut ScriptComponent,
    old_name: &str,
    new_name: &str,
) -> bool {
    // 参照値の区切り文字（C# 側 ScriptReference.SlotSeparator と一致させる）
    const SLOT_SEPARATOR: char = '|';
    let slot_prefix = format!("{old_name}{SLOT_SEPARATOR}");

    // 借用の都合で候補を先に収集してから書き換える
    // （set_field は &mut self を取り、fields の iter と同時には呼べない）
    let candidates: Vec<(String, String)> = sc.fields.iter()
        .filter(|(_, v)| v.as_str() == old_name || v.starts_with(&slot_prefix))
        .map(|(k, v)| (k.clone(), v.clone()))
        .collect();

    let mut any = false;
    for (field_path, value) in candidates {
        // 型ゲート: 本当に参照フィールドか（CLR リフレクション）
        if !sc.is_reference_field(&field_path) { continue; }

        // "旧名" → "新名"、"旧名|スロット" → "新名|スロット"
        let new_value = format!("{new_name}{}", &value[old_name.len()..]);
        // set_field が CLR への FFI 反映・シリアライズ用 fields 更新・
        // 参照再解決フラグ（refs_dirty）まで一括で行う
        sc.set_field(&field_path, &new_value);
        any = true;
    }
    any
}

// ============================================================
//  テスト
//
//  スクリプト参照の書き換えは CLR ホストが必要なため単体テストでは扱えない
//  （実機確認手順は docs 参照）。ここでは CLR 不要な WaterVolume / Canvas の
//  参照書き換えと DFS ID 採番・世界線分離を検証する。
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::WaterVolumeComponentData;
    use crate::engine::structs::objects::Actor;

    /// テスト用の World + アクタツリーを組み立てるヘルパ。
    struct TestWorld {
        world:  World,
        actors: Vec<Actor>,
    }

    impl TestWorld {
        fn new() -> Self {
            Self { world: World::new(), actors: Vec::new() }
        }

        /// 指定名のアクタを作る（スロット無し）。
        fn make_actor(&mut self, name: &str, wl: u32) -> Actor {
            let entity = self.world.spawn();
            let mut actor = Actor::new(entity, name);
            actor.world_line = wl;
            actor
        }

        /// control_point_ref を持つ WaterVolume スロットを追加する。
        fn add_water_slot(&mut self, actor: &mut Actor, cp_ref: &str) {
            let slot_entity = self.world.spawn();
            let mut data = WaterVolumeComponentData::default();
            data.control_point_ref = cp_ref.to_string();
            self.world.insert(slot_entity, WaterVolumeComponent::from_data(data));
            actor.add_slot_typed::<WaterVolumeComponent>(
                "WaterVolumeComponent", ComponentKind::WaterVolume, slot_entity);
        }

        /// カメラ参照付きの Canvas スロットを追加する。
        fn add_canvas_slot(&mut self, actor: &mut Actor, cam_actor: &str, cam_slot: &str) {
            let slot_entity = self.world.spawn();
            let mut canvas = CanvasComponent::default();
            canvas.viewport_ref = CanvasViewportRef::Camera {
                actor_name: cam_actor.to_string(),
                slot_name:  cam_slot.to_string(),
            };
            self.world.insert(slot_entity, canvas);
            actor.add_slot_typed::<CanvasComponent>(
                "CanvasComponent", ComponentKind::Canvas, slot_entity);
        }

        /// 世界線 wl のアクタへ propagate 相当の走査を直接実行する
        ///（App を組み立てずにモジュール内部関数を検証するため）。
        fn propagate(&mut self, wl: u32, old: &str, new: &str) -> Vec<u32> {
            let mut changed = Vec::new();
            let mut counter = 0u32;
            for actor in &self.actors {
                if actor.world_line != wl { continue; }
                rewrite_refs_in_subtree(
                    actor, &mut self.world, old, new, &mut counter, &mut changed);
            }
            changed
        }

        /// アクタの WaterVolume スロット（先頭）の control_point_ref を読む。
        fn water_ref_of(&self, actor: &Actor) -> String {
            let slot = actor.slots().iter()
                .find(|s| s.kind == ComponentKind::WaterVolume).unwrap();
            self.world.get::<WaterVolumeComponent>(slot.entity)
                .unwrap().control_point_ref.clone()
        }
    }

    /// WaterVolume の control_point_ref が旧名一致で書き換わり、
    /// 変更アクタの DFS ID が返ること。
    #[test]
    fn water_control_point_ref_follows_rename() {
        let mut t = TestWorld::new();
        let path = t.make_actor("RiverPath", 0);          // DFS 0
        let mut river = t.make_actor("River", 0);         // DFS 1
        t.add_water_slot(&mut river, "RiverPath");
        t.actors.push(path);
        t.actors.push(river);

        let changed = t.propagate(0, "RiverPath", "MainRiverPath");

        assert_eq!(changed, vec![1], "水アクタ（DFS 1）だけが変更されること");
        assert_eq!(t.water_ref_of(&t.actors[1]), "MainRiverPath");
    }

    /// 旧名に一致しない参照は書き換えないこと。
    #[test]
    fn unrelated_refs_are_untouched() {
        let mut t = TestWorld::new();
        let mut river = t.make_actor("River", 0);
        t.add_water_slot(&mut river, "OtherPath");
        t.actors.push(river);

        let changed = t.propagate(0, "RiverPath", "MainRiverPath");

        assert!(changed.is_empty());
        assert_eq!(t.water_ref_of(&t.actors[0]), "OtherPath");
    }

    /// Canvas のカメラ参照はアクタ名だけが書き換わり、スロット名は保たれること。
    #[test]
    fn canvas_camera_ref_follows_rename_keeping_slot_name() {
        let mut t = TestWorld::new();
        let mut ui = t.make_actor("UI", 0);
        t.add_canvas_slot(&mut ui, "PlayerCam", "MainCamera");
        t.actors.push(ui);

        let changed = t.propagate(0, "PlayerCam", "HeroCam");

        assert_eq!(changed, vec![0]);
        let slot = t.actors[0].slots().iter()
            .find(|s| s.kind == ComponentKind::Canvas).unwrap();
        let canvas = t.world.get::<CanvasComponent>(slot.entity).unwrap();
        match &canvas.viewport_ref {
            CanvasViewportRef::Camera { actor_name, slot_name } => {
                assert_eq!(actor_name, "HeroCam");
                assert_eq!(slot_name, "MainCamera", "スロット名は変えないこと");
            }
            other => panic!("Camera 参照のまま維持されること: {other:?}"),
        }
    }

    /// 別世界線のアクタは走査対象外であること（参照も DFS 採番も影響しない）。
    #[test]
    fn other_world_lines_are_not_scanned() {
        let mut t = TestWorld::new();
        let mut wl1 = t.make_actor("River", 1);
        t.add_water_slot(&mut wl1, "RiverPath");
        t.actors.push(wl1);

        let changed = t.propagate(0, "RiverPath", "MainRiverPath");

        assert!(changed.is_empty(), "世界線 1 のアクタは世界線 0 の走査で変更されない");
        assert_eq!(t.water_ref_of(&t.actors[0]), "RiverPath");
    }

    /// 子アクタのスロットも走査され、DFS ID が preorder（親→子）で採番されること。
    #[test]
    fn nested_child_slots_are_rewritten_with_preorder_dfs_ids() {
        let mut t = TestWorld::new();
        let mut root = t.make_actor("Root", 0);           // DFS 0
        let mut child = t.make_actor("Child", 0);         // DFS 1
        t.add_water_slot(&mut child, "RiverPath");
        root.children_mut().push(child);
        t.actors.push(root);

        let changed = t.propagate(0, "RiverPath", "NewPath");

        assert_eq!(changed, vec![1], "子アクタの DFS ID は 1（preorder）");
    }
}
