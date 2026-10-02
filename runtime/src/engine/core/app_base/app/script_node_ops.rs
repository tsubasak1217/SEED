// ============================================================
//  script_node_ops.rs — 動的ノード API の遅延コマンドの適用（スクリプトから木を組み立てる。docs/scripting_api.md §7「動的ノード」）
//
//  host_api/nodes.rs が積んだ ScriptSceneCommand を、フレーム末尾に apply_script_scene_commands（script_scene_ops.rs）が
//  発行順にここへ渡す。ここは Actor ツリーとスロットの目録を変えるだけで、HIERARCHY の送信・描く理由（レイアウトは毎フレームの
//  描画で木の順から測り直すので、並べ替えも描く理由が立てば反映される）は呼び出し側がまとめて行う。
//
//    - CreateActor     … 空のアクタを組み立てて親の末尾の子へ（Instantiate と同じ取り付け・同じフォールバック）
//    - SetSiblingIndex … 論理の兄弟の順番を変える（node_tree::move_to_logical_index）
//    - AttachSlot      … FFI がその場で World へ入れたスロットを目録へ登録する
//    - RemoveSlot      … エディタの「コンポーネント削除」と同じ後始末（slot_ops::remove_slot_components）で外す
//    - AddScript       … スクリプトのスロットを足し、通常の構築経路（ScriptComponent::new）で CLR のインスタンスを作る
//                        （OnStart は次のフレームの BeginFrame。owner は Scene が毎フレーム木から同期する）
//  どれも Undo を積まず SCENE_MODIFIED も送らない（Play の世界は停止で Play 前の写しへ戻る。Instantiate と同じ）。
//  断ったときは [Script] の 1 行を出して木を変えない（スクリプト側のハンドルは無視される規約）。
// ============================================================

use crate::engine::components::script_component::script_scope_short_name;
use crate::engine::components::{ComponentKind, ScriptComponent};
use crate::engine::core::app_base::scene::Scene;
use crate::engine::core::scripting::node_tree;
use crate::engine::ecs::Entity;
use crate::engine::structs::objects::actor::ComponentSlot;
use crate::engine::structs::objects::Actor;

use super::slot_ops::remove_slot_components;
use super::{attach_actor_under, find_actor_by_entity_mut, App};

/// スクリプトが作るアクタの世界線（Play のシーン。親があれば取り付けで親の世界線へ揃う）。
const SCRIPT_SPAWN_WORLD_LINE: u32 = 0;

impl App {
    /// CreateActor を当てる: 予約済みのルートエンティティで空のアクタを組み立て、親の末尾の子へ取り付ける。
    ///
    /// # 引数
    /// * `entity` - ffi_node_create が予約したルート（Transform か CanvasTransform を挿入済み）
    /// * `name`   - アクタ名
    /// * `parent` - 親のルートエンティティ（None ならシーンのルート）
    /// * `is_2d`  - 2D（CanvasTransform）なら true
    pub(super) fn apply_script_create_actor(&mut self, entity: Entity, name: &str, parent: Option<Entity>, is_2d: bool) {
        let Some(scene) = self.scene.as_mut() else { return };
        // 同じフレームの間にワールドが入れ替わっていれば（シーン遷移は他のコマンドを捨てるので通常は来ない）何もしない
        if !scene.world.is_alive(entity) {
            return;
        }
        let mut actor = if is_2d { Actor::new_2d(entity, name) } else { Actor::new(entity, name) };
        actor.world_line = SCRIPT_SPAWN_WORLD_LINE;
        // スクリプトが生成した部分木の印（プレハブの当て直しで消さない・書き戻しでファイルへ書かない。Instantiate と同じ）
        actor.spawned_by_script = true;
        if is_2d {
            // 2D アクタが居る世界線として登録する（apply_script_instantiate と同じ）
            self.canvas_world_lines.insert(SCRIPT_SPAWN_WORLD_LINE);
        }
        if !attach_actor_under(&mut scene.actors, parent, actor) {
            eprintln!("[Script] Create: 指定した親が無効（破棄済み／種別不整合）のためルート直下へ作りました ({name})");
        }
    }

    /// SetSiblingIndex を当てる: 論理の兄弟の順番を変える（フォルダは透過。node_tree.rs）。
    ///
    /// # 引数
    /// * `entity` - 対象のルートエンティティ
    /// * `index`  - 論理の順番（None・兄弟の数以上なら末尾）
    pub(super) fn apply_script_set_sibling_index(&mut self, entity: Entity, index: Option<usize>) {
        let Some(scene) = self.scene.as_mut() else { return };
        if let Err(e) = node_tree::move_to_logical_index(&mut scene.actors, entity, index) {
            eprintln!("[Script] SetSiblingIndex 拒否: {}", e.message());
        }
    }

    /// AttachSlot を当てる: FFI がその場で World へ入れたスロットをアクタのスロットの目録へ登録する。
    /// アクタが無ければ（破棄済み・生成に失敗した）スロットのエンティティを捨てる（リークさせない）。
    ///
    /// # 引数
    /// * `root` - アクタのルートエンティティ
    /// * `slot` - 目録の 1 行（コンポーネントは slot.entity に入れ済み）
    pub(super) fn apply_script_attach_slot(&mut self, root: Entity, slot: ComponentSlot) {
        let Some(scene) = self.scene.as_mut() else { return };
        let Scene { actors, world, .. } = scene;
        match find_actor_by_entity_mut(actors, root) {
            Some(actor) => actor.slots_mut().push(slot),
            None => {
                eprintln!("[Script] AddComponent: 対象のアクターがシーンに存在しないため捨てました ({})", slot.name);
                world.despawn(slot.entity);
            }
        }
    }

    /// RemoveSlot を当てる: エディタの「コンポーネント削除」と同じ後始末でスロットを外す。
    ///
    /// # 引数
    /// * `root`        - アクタのルートエンティティ
    /// * `slot_entity` - 外すスロット専用のエンティティ
    pub(super) fn apply_script_remove_slot(&mut self, root: Entity, slot_entity: Entity) {
        // 辞書を含むコンポーネント構成が変わり得るので、キー索引を作り直す（エディタの削除と同じ）
        self.mark_audio_dictionary_dirty();
        let Some(scene) = self.scene.as_mut() else { return };
        let Scene { actors, world, .. } = scene;
        let Some(actor) = find_actor_by_entity_mut(actors, root) else {
            // アクタごと破棄済み（despawn_actor_recursive がスロットも消している）。残っていれば消す
            if world.is_alive(slot_entity) {
                world.despawn(slot_entity);
            }
            return;
        };
        let Some(i) = actor.slots().iter().position(|s| s.entity == slot_entity) else {
            eprintln!("[Script] RemoveComponent 拒否: スロットが見つかりません");
            return;
        };
        let kind = actor.slots()[i].kind;
        remove_slot_components(world, kind, slot_entity);
        actor.remove_slot_at(i);
    }

    /// AddScript を当てる: スクリプトのスロットを足し、通常の構築経路で CLR のインスタンスを作る。
    /// OnStart は次のフレームの BeginFrame で走る（`started` が false のまま足すため）。
    ///
    /// # 引数
    /// * `root`      - アクタのルートエンティティ
    /// * `type_name` - スクリプトの型名（C# の FullName。ScriptBridge.CreateComponent が型名でも .cs パスでも引ける）
    pub(super) fn apply_script_add_script(&mut self, root: Entity, type_name: &str) {
        let Some(host) = self.scripting_host.clone() else {
            eprintln!("[Script] AddScript 失敗: スクリプトのホストがありません ({type_name})");
            return;
        };
        let Some(scene) = self.scene.as_mut() else { return };
        let Scene { actors, world, .. } = scene;
        let Some(actor) = find_actor_by_entity_mut(actors, root) else {
            eprintln!("[Script] AddScript 拒否: 対象のアクターがシーンに存在しません ({type_name})");
            return;
        };
        let Some(sc) = ScriptComponent::new(host, type_name) else {
            eprintln!("[Script] AddScript 失敗: スクリプトの型を作れません ({type_name})");
            return;
        };
        let slot_entity = world.spawn();
        world.insert(slot_entity, sc);
        // スロット名はクラス名（エディタがスクリプトのスロットに付ける名前と同じ語幹）
        actor.add_slot_typed::<ScriptComponent>(script_scope_short_name(type_name), ComponentKind::Script, slot_entity);
    }
}
