// ============================================================
//  script_ops.rs — スクリプト関連の IPC 操作
//
//  【含む処理】
//  - handle_set_script_field : [SerializeField] フィールド値の設定
//  - handle_reload_scripts   : ユーザースクリプトの再コンパイルと全再生成
//                              （ホットリロード。スクリプトの実行状態は失われる）
// ============================================================

use std::collections::BTreeMap;

use crate::engine::components::{
    ComponentKind, ScriptComponent, PlaceholderScriptSlot,
};
use crate::engine::ecs::Entity;
use crate::engine::structs::objects::Actor;

use super::{App, find_actor_by_dfs};

/// リロード時に収集するスクリプトスロット 1 件分の情報。
struct ScriptSlotInfo {
    entity: Entity,
    path:   String,
    fields: BTreeMap<String, String>,
}

impl App {

    /// ScriptComponent / PlaceholderScriptSlot の [SerializeField] フィールド値を設定する。
    ///
    /// - ScriptComponent  : CLR インスタンスへ即時反映し、シリアライズ用の fields にも保存
    /// - Placeholder      : fields のみ保存（CLR 起動後の生成時に適用される）
    pub(super) fn handle_set_script_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        field:        &str,
        value:        &str,
    ) {
        let wl = self.active_world_line;
        let Some(scene) = self.scene.as_mut() else { return };

        // 対象スロットの entity を特定する
        let slot_entity = {
            let mut c = 0u32;
            let Some(actor) = find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c) else { return };
            let Some(slot) = actor.slots().get(slot_idx as usize) else { return };
            slot.entity
        };

        // Script / Placeholder のどちらかへ値を書き込む
        if let Some(sc) = scene.world.get_mut::<ScriptComponent>(slot_entity) {
            sc.set_field(field, value);
        } else if let Some(ps) = scene.world.get_mut::<PlaceholderScriptSlot>(slot_entity) {
            ps.fields.insert(field.to_string(), value.to_string());
        } else {
            return;
        }

        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// ユーザースクリプトを再コンパイルし、シーン内の全スクリプトを再生成する。
    ///
    /// 【手順】
    /// 1. 全スクリプトスロット（Script / Placeholder）のパスとフィールド値を収集
    /// 2. 旧 ScriptComponent を World から除去（Drop で CLR インスタンス破棄）
    /// 3. CLR 側で再コンパイル（collectible ALC により旧アセンブリはアンロード）
    /// 4. 新アセンブリでインスタンスを再生成し、フィールド値を復元
    ///
    /// スクリプトのプライベート状態（実行中の変数など）は失われる。
    pub(super) fn handle_reload_scripts(&mut self) {
        let Some(host) = self.scripting_host.clone() else {
            if let Some(ipc) = &self.ipc { ipc.send("SCRIPTS_RELOADED:-1,CLR not loaded"); }
            return;
        };
        let Some(assets_root) = self.assets_root.clone() else {
            if let Some(ipc) = &self.ipc { ipc.send("SCRIPTS_RELOADED:-1,assets root unknown"); }
            return;
        };
        let Some(scene) = self.scene.as_mut() else { return };

        // 1. 全世界線のスクリプトスロットを収集する
        let mut infos: Vec<ScriptSlotInfo> = Vec::new();
        fn collect(actor: &Actor, world: &crate::engine::ecs::World, out: &mut Vec<ScriptSlotInfo>) {
            for slot in actor.slots() {
                match slot.kind {
                    ComponentKind::Script => {
                        if let Some(sc) = world.get::<ScriptComponent>(slot.entity) {
                            out.push(ScriptSlotInfo {
                                entity: slot.entity,
                                path:   sc.type_name().to_string(),
                                fields: sc.fields.clone(),
                            });
                        }
                    }
                    ComponentKind::Placeholder => {
                        if let Some(ps) = world.get::<PlaceholderScriptSlot>(slot.entity) {
                            out.push(ScriptSlotInfo {
                                entity: slot.entity,
                                path:   ps.script_path.clone(),
                                fields: ps.fields.clone(),
                            });
                        }
                    }
                    _ => {}
                }
            }
            for child in actor.children() {
                collect(child, world, out);
            }
        }
        for actor in &scene.actors {
            collect(actor, &scene.world, &mut infos);
        }

        // 2. 旧インスタンスをすべて破棄する（ALC アンロードの前提条件）
        for info in &infos {
            scene.world.remove::<ScriptComponent>(info.entity);
            scene.world.remove::<PlaceholderScriptSlot>(info.entity);
        }

        // 3. 再コンパイル
        let count = host.compile_scripts(&assets_root);

        // 4. 再生成（失敗したスロットは Placeholder として保持する）
        let mut restored = 0usize;
        let mut kind_by_entity: std::collections::HashMap<Entity, ComponentKind> =
            std::collections::HashMap::new();
        for info in infos {
            let created = ScriptComponent::new_with_fields(
                std::sync::Arc::clone(&host), info.path.clone(), info.fields.clone(),
            );
            let kind = if let Some(sc) = created {
                scene.world.insert(info.entity, sc);
                restored += 1;
                ComponentKind::Script
            } else {
                scene.world.insert(info.entity, PlaceholderScriptSlot {
                    script_path: info.path,
                    fields:      info.fields,
                });
                ComponentKind::Placeholder
            };
            kind_by_entity.insert(info.entity, kind);
        }

        // スロットの kind / type_id を再生成結果に合わせて更新する
        fn update_kinds(actor: &mut Actor, kinds: &std::collections::HashMap<Entity, ComponentKind>) {
            for slot in actor.slots_mut() {
                if let Some(kind) = kinds.get(&slot.entity) {
                    slot.kind    = *kind;
                    slot.type_id = if *kind == ComponentKind::Script {
                        std::any::TypeId::of::<ScriptComponent>()
                    } else {
                        std::any::TypeId::of::<PlaceholderScriptSlot>()
                    };
                }
            }
            for child in actor.children_mut() {
                update_kinds(child, kinds);
            }
        }
        for actor in &mut scene.actors {
            update_kinds(actor, &kind_by_entity);
        }

        // エディタへ結果を通知する（count: コンパイルされた型数、restored: 再生成数）
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("SCRIPTS_RELOADED:{count},{restored}"));
        }
    }
}
