// ============================================================
//  slot_ops.rs — コンポーネントスロットの編集・再構築操作
//
//  【含む処理】
//  - handle_set_model_path:      モデルパス変更（ModelComponent / PlaceholderScriptSlot）
//  - handle_remove_component_slot: スロット削除
//  - handle_rename_component_slot: スロット名変更
//  - handle_duplicate_component:   スロット複製
//  - snapshot_actor_slots:         スロット状態スナップショット
//  - rebuild_actor_slots:          スロット再構築（ComponentSlotData → World）
//  - handle_set_inputmap_path:     InputMapComponent パス変更
// ============================================================

use crate::engine::components::{
    ModelComponent, Transform as ActorTransform, ComponentKind, ComponentData,
    ScriptComponent, PlaceholderScriptSlot, CanvasComponent,
    SpriteComponent, InputMapComponent,
    CameraComponent, PluginComponent,
    ColliderComponent,
};
use crate::engine::structs::objects::actor::{ComponentSlotData, ComponentSlot};
use crate::engine::core::app_base::undo::ComponentSlotsSnapshotCommand;

use super::{
    App, find_actor_by_dfs, find_actor_by_dfs_mut,
};

impl App {

    /// コンポーネントスロットのパスを後から設定する（ModelComponent / PlaceholderScriptSlot 共通）。
    pub(super) fn handle_set_model_path(&mut self, actor_dfs_id: u32, slot_idx: u32, path: &str) {
        if self.draw_ctx.is_none() || self.ipc.is_none() || self.scene.is_none() { return; }
        let wl = self.active_world_line;

        // 対象スロットの entity と kind、および actor entity（Transform 参照用）を取得する
        let (actor_entity, slot_entity, slot_kind) = {
            let scene = self.scene.as_ref().unwrap();
            let mut c = 0u32;
            let actor = match find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c) {
                Some(a) => a,
                None => return,
            };
            let slot = actor.slots().get(slot_idx as usize);
            match slot {
                Some(s) => (actor.entity, s.entity, s.kind),
                None => return,
            }
        };

        // スクリプトスロット（Placeholder / Script）の場合はパスを更新して早期リターン。
        // CLR ホストがあれば新パスで実インスタンスを生成し直す（Placeholder → Script 昇格を含む）。
        // スクリプトが変わるためフィールド値は引き継がない。
        if slot_kind == ComponentKind::Placeholder || slot_kind == ComponentKind::Script {
            let host = self.scripting_host.clone();
            let scene = self.scene.as_mut().unwrap();

            // 旧コンポーネントを除去する（ScriptComponent の Drop で CLR インスタンスも破棄される）
            scene.world.remove::<ScriptComponent>(slot_entity);
            scene.world.remove::<PlaceholderScriptSlot>(slot_entity);

            // 新パスでインスタンス生成を試み、失敗時は Placeholder として保持する
            let created = host.as_ref()
                .and_then(|h| ScriptComponent::new(std::sync::Arc::clone(h), path.to_string()));
            let new_kind = if let Some(sc) = created {
                scene.world.insert(slot_entity, sc);
                ComponentKind::Script
            } else {
                scene.world.insert(slot_entity, PlaceholderScriptSlot {
                    script_path: path.to_string(),
                    fields:      Default::default(),
                });
                ComponentKind::Placeholder
            };
            // スロットの kind と type_id を新しい状態に合わせて更新する
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                if let Some(slot) = actor.slots_mut().get_mut(slot_idx as usize) {
                    slot.kind    = new_kind;
                    slot.type_id = if new_kind == ComponentKind::Script {
                        std::any::TypeId::of::<ScriptComponent>()
                    } else {
                        std::any::TypeId::of::<PlaceholderScriptSlot>()
                    };
                }
            }
            self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
            if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
            return;
        }

        // ModelComponent の場合: モデルをロードして GPU リソース再構築
        let model = match crate::engine::core::loader::load_model(std::path::Path::new(path)) {
            Ok(m) => m,
            Err(e) => {
                if let Some(ipc) = &self.ipc { ipc.send(&format!("LOAD_ERROR:{e}")); }
                return;
            }
        };
        // モデルが変わるとマテリアルスロットの数・意味が変わるため、
        // 既存のマテリアルオーバーライドは破棄する（新モデルに対応しないスロット指定が残るのを防ぐ）。
        let (gpu_model, instanced_batch) = {
            let ctx = self.draw_ctx.as_ref().unwrap();
            (ctx.upload_model_with_overrides(&model, &[]), ctx.create_instanced_batch(&model, 1))
        };
        // Arc 化してキャッシュに登録する
        let model_arc: std::sync::Arc<crate::engine::core::loader::model::Model> = {
            let ctx = self.draw_ctx.as_ref().unwrap();
            let arc = std::sync::Arc::new(model);
            ctx.model_cache.borrow_mut()
                .entry(path.to_string())
                .or_insert_with(|| std::sync::Arc::clone(&arc));
            arc
        };
        use crate::engine::components::InstanceMeta;
        let scene = self.scene.as_mut().unwrap();
        // actor transform を先取得（actor.entity から Transform を参照）
        let initial_mat = scene.world.get::<ActorTransform>(actor_entity)
            .map(|t| t.to_mat4())
            .unwrap_or([[1.0,0.0,0.0,0.0],[0.0,1.0,0.0,0.0],[0.0,0.0,1.0,0.0],[0.0,0.0,0.0,1.0]]);
        // スロット専用 entity の ModelComponent を更新する
        let found = if let Some(mc) = scene.world.get_mut::<ModelComponent>(slot_entity) {
            if mc.instance_mats.is_empty() {
                mc.instance_mats.push(initial_mat);
                mc.instance_meta.push(InstanceMeta::new("Instance_0"));
            }
            mc.source_path     = path.to_string();
            mc.model           = Some(model_arc);
            mc.gpu_model       = Some(gpu_model);
            mc.instanced_batch = Some(instanced_batch);
            mc.material_overrides.clear();
            true
        } else { false };
        if found {
            self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
            if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
        }
    }

    /// インスペクタからの ModelComponent フィールド更新（SET_MODEL_FIELD IPC）。
    ///
    /// LightComponent の handle_set_light_field と同流儀。
    /// key: cast_shadows（現状はこれのみ。将来のフィールド追加もこの関数に集約する）。
    /// 不正な key・value は無視する。
    pub(super) fn handle_set_model_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        key:          &str,
        value:        &str,
    ) {
        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（handle_set_light_field と同流儀）。
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Model)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        let Some(mc) = scene.world.get_mut::<ModelComponent>(entity) else { return };

        match key {
            "cast_shadows" => mc.cast_shadows = value == "1" || value == "true",
            _ => return,
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// インスペクタからのマテリアルオーバーライド設定/解除（SET_MATERIAL_OVERRIDE IPC, Phase R7）。
    ///
    /// - `mat_slot`: Model::materials のインデックス（＝ そのマテリアルスロット）。
    /// - `json` が空文字列 または `{"kind":"embedded"}` の場合: そのスロットのオーバーライドを
    ///   削除し、glTF 埋込マテリアルへ戻す。
    /// - それ以外: `MaterialOverrideKind`（`{"kind":"mat_asset","path":".."}` /
    ///   `{"kind":"inline",...}`）としてパースし、同一 slot の既存エントリを置換（無ければ追加）する。
    ///
    /// 変更を反映するため、対象 ModelComponent の `gpu_model` を
    /// `upload_model_with_overrides` で再構築する（GpuModel は per-MC 専有のため、
    /// この MC 以外の描画には一切影響しない）。
    ///
    /// 【借用の扱い】`scene`（World 可変参照）と `draw_ctx`（GPU アップロード）を同時に
    /// 借用できないため、(1) モデル Arc の取得 → (2) material_overrides の更新（scene のみ）→
    /// (3) scene 借用を落として draw_ctx で GpuModel を再構築 → (4) 結果を scene へ書き戻す、
    /// の 4 段階に分け、各段階でブロックスコープを閉じて前段の借用を確実に解放する
    /// （handle_set_model_path と同じ流儀）。
    pub(super) fn handle_set_material_override(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        mat_slot:     u32,
        json:         &str,
    ) {
        if self.draw_ctx.is_none() || self.scene.is_none() { return; }
        let wl = self.active_world_line;

        // 対象スロットの entity を解決する（Model コンポーネントのみ対象）
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Model)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };

        // json をパースする。空 or {"kind":"embedded"} は「削除（埋込に戻す）」の合図。
        const EMBEDDED_JSON: &str = r#"{"kind":"embedded"}"#;
        let trimmed = json.trim();
        let new_kind: Option<crate::engine::components::MaterialOverrideKind> =
            if trimmed.is_empty() || trimmed == EMBEDDED_JSON {
                None
            } else {
                match serde_json::from_str(trimmed) {
                    Ok(k)  => Some(k),
                    Err(e) => {
                        eprintln!("[SEED] SET_MATERIAL_OVERRIDE: json パース失敗（{e}）: {trimmed}");
                        return;
                    }
                }
            };

        // (1) モデル Arc を取得（無ければ何もしない：未ロード ModelComponent への操作は無効）
        let model_arc = {
            let Some(scene) = &self.scene else { return };
            match scene.world.get::<ModelComponent>(entity).and_then(|mc| mc.model.clone()) {
                Some(m) => m,
                None    => return,
            }
        };

        // (2) material_overrides を更新する（同一 slot は置換）
        {
            let Some(scene) = &mut self.scene else { return };
            let Some(mc) = scene.world.get_mut::<ModelComponent>(entity) else { return };
            mc.material_overrides.retain(|o| o.slot != mat_slot as usize);
            if let Some(kind) = new_kind {
                mc.material_overrides.push(crate::engine::components::MaterialOverride {
                    slot: mat_slot as usize,
                    kind,
                });
            }
        }

        // (3) scene 借用を落としてから draw_ctx で GpuModel を再構築する
        let new_gpu_model = {
            let Some(scene) = &self.scene else { return };
            let Some(mc) = scene.world.get::<ModelComponent>(entity) else { return };
            let ctx = self.draw_ctx.as_ref().unwrap();
            ctx.upload_model_with_overrides(&model_arc, &mc.material_overrides)
        };

        // (4) 再構築結果を書き戻し、バッチ更新をマークする
        {
            let Some(scene) = &mut self.scene else { return };
            if let Some(mc) = scene.world.get_mut::<ModelComponent>(entity) {
                mc.gpu_model = Some(new_gpu_model);
                mc.mark_batch_dirty();
            }
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// コンポーネントスロットを削除する。
    pub(super) fn handle_remove_component_slot(&mut self, actor_dfs_id: u32, slot_idx: u32) {
        let Some(_scene) = &self.scene else { return };
        let wl = self.active_world_line;

        let before_slots = self.snapshot_actor_slots(wl, actor_dfs_id);

        {
            let scene = self.scene.as_mut().unwrap();
            // スロットの entity と kind を先に取り出して actors の borrow を解放する
            let removal_info = {
                let mut c = 0u32;
                find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                    .and_then(|a| a.slots().get(slot_idx as usize).map(|s| (s.entity, s.kind)))
            };
            if let Some((slot_entity, kind)) = removal_info {
                // スロット専用エンティティからコンポーネントを除去して despawn する。
                // 各スロットは独自 entity を持つため、is_last_of_kind チェックは不要。
                match kind {
                    ComponentKind::Model       => { scene.world.remove::<ModelComponent>(slot_entity); }
                    ComponentKind::Script      => { scene.world.remove::<ScriptComponent>(slot_entity); }
                    ComponentKind::Placeholder => { scene.world.remove::<PlaceholderScriptSlot>(slot_entity); }
                    ComponentKind::Canvas      => { scene.world.remove::<CanvasComponent>(slot_entity); }
                    ComponentKind::Sprite      => { scene.world.remove::<SpriteComponent>(slot_entity); }
                    ComponentKind::InputMap    => { scene.world.remove::<InputMapComponent>(slot_entity); }
                    ComponentKind::Camera      => { scene.world.remove::<CameraComponent>(slot_entity); }
                    ComponentKind::Plugin      => { scene.world.remove::<PluginComponent>(slot_entity); }
                    ComponentKind::Collider    => { scene.world.remove::<ColliderComponent>(slot_entity); }
                    ComponentKind::Collider2d  => {
                        use crate::engine::components::Collider2dComponent;
                        scene.world.remove::<Collider2dComponent>(slot_entity);
                    }
                    ComponentKind::Audio       => {
                        use crate::engine::components::AudioComponent;
                        scene.world.remove::<AudioComponent>(slot_entity);
                    }
                    ComponentKind::Animator    => {
                        use crate::engine::components::AnimatorComponent;
                        scene.world.remove::<AnimatorComponent>(slot_entity);
                    }
                    ComponentKind::Light       => {
                        use crate::engine::components::LightComponent;
                        scene.world.remove::<LightComponent>(slot_entity);
                    }
                    ComponentKind::ParticleEmitter => {
                        use crate::engine::components::ParticleEmitterComponent;
                        scene.world.remove::<ParticleEmitterComponent>(slot_entity);
                    }
                }
                scene.world.despawn(slot_entity);
                // アクターのスロットリストから削除
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.remove_slot_at(slot_idx as usize);
                }
            }
        }

        let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
        self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
            world_line: wl,
            actor_dfs_id,
            before_slots,
            after_slots,
        }));

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// コンポーネントスロット名を変更する。
    pub(super) fn handle_rename_component_slot(&mut self, actor_dfs_id: u32, slot_idx: u32, name: &str) {
        let Some(scene) = &mut self.scene else { return };
        let wl = self.active_world_line;
        let mut c = 0u32;
        if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
            if let Some(slot) = actor.slots_mut().get_mut(slot_idx as usize) {
                slot.name = name.to_string();
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// コンポーネントを複製する（DUPLICATE_COMPONENT）。
    pub(super) fn handle_duplicate_component(&mut self, actor_dfs_id: u32, slot_idx: u32) {
        if self.draw_ctx.is_none() { return; }
        let wl = self.active_world_line;
        let host = self.scripting_host.clone();

        // スロットデータを先にスナップショット（actors/world の借用を解放）
        let slot_data_opt = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|actor| actor.to_data(&scene.world).components.into_iter().nth(slot_idx as usize))
                .map(|mut sd| { sd.name = format!("{} Copy", sd.name); sd })
        };
        let Some(slot_data) = slot_data_opt else { return };

        let before_slots = self.snapshot_actor_slots(wl, actor_dfs_id);

        use crate::engine::core::loader::load_model;

        if self.draw_ctx.is_none() { return; }
        let mut scene = match self.scene.take() {
            Some(s) => s,
            None => return,
        };
        let ctx = self.draw_ctx.as_ref().unwrap();

        // 新コンポーネントを world に insert してスロット追加
        // スロット専用エンティティを spawn し、各スロットが独立したコンポーネントを持つ。
        let slot_added = match slot_data.component {
            ComponentData::ModelComponent(mc_data) => {
                let cast_shadows = mc_data.cast_shadows;
                let mc = if mc_data.model_path.is_empty() {
                    ModelComponent {
                        source_path:     String::new(),
                        model:           None,
                        gpu_model:       None,
                        instanced_batch: None,
                        instance_mats:   mc_data.instances,
                        instance_meta:   mc_data.meta,
                        group_meta:      mc_data.groups,
                        next_group_id:   mc_data.next_group_id,
                        anim_drive:      None,
                        cast_shadows,
                        material_overrides: mc_data.material_overrides,
                    }
                } else {
                    let path = std::path::Path::new(&mc_data.model_path);
                    // キャッシュから CPU モデルを取得するか、ディスクから読み込む
                    let model_arc: std::sync::Arc<crate::engine::core::loader::model::Model> = {
                        let mut cache = ctx.model_cache.borrow_mut();
                        if let Some(cached) = cache.get(&mc_data.model_path) {
                            std::sync::Arc::clone(cached)
                        } else {
                            match load_model(path) {
                                Ok(m) => {
                                    let arc = std::sync::Arc::new(m);
                                    cache.insert(mc_data.model_path.clone(), std::sync::Arc::clone(&arc));
                                    arc
                                }
                                Err(e) => {
                                    if let Some(ipc) = &self.ipc { ipc.send(&format!("LOAD_ERROR:{e}")); }
                                    self.scene = Some(scene);
                                    return;
                                }
                            }
                        }
                    };
                    let gpu_model       = ctx.upload_model_with_overrides(&*model_arc, &mc_data.material_overrides);
                    let instanced_batch = ctx.create_instanced_batch(&*model_arc, mc_data.instances.len() as u32);
                    ModelComponent {
                        source_path:     mc_data.model_path,
                        model:           Some(model_arc),
                        gpu_model:       Some(gpu_model),
                        instanced_batch: Some(instanced_batch),
                        instance_mats:   mc_data.instances,
                        instance_meta:   mc_data.meta,
                        group_meta:      mc_data.groups,
                        next_group_id:   mc_data.next_group_id,
                        anim_drive:      None,
                        cast_shadows,
                        material_overrides: mc_data.material_overrides,
                    }
                };
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, mc);
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<ModelComponent>(slot_data.name, ComponentKind::Model, slot_entity);
                } else {
                    scene.world.despawn(slot_entity);
                }
                true
            }
            ComponentData::ScriptComponent(sc_data) => {
                let slot_entity = scene.world.spawn();
                if let Some(h) = &host {
                    // フィールド値も含めて CLR インスタンスを復元する
                    if let Some(sc) = ScriptComponent::new_with_fields(
                        std::sync::Arc::clone(h), sc_data.type_name.clone(), sc_data.fields.clone(),
                    ) {
                        scene.world.insert(slot_entity, sc);
                        let mut c = 0u32;
                        if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                            actor.add_slot_typed::<ScriptComponent>(slot_data.name, ComponentKind::Script, slot_entity);
                        } else { scene.world.despawn(slot_entity); }
                        true
                    } else {
                        scene.world.insert(slot_entity, PlaceholderScriptSlot {
                            script_path: sc_data.type_name,
                            fields:      sc_data.fields,
                        });
                        let mut c = 0u32;
                        if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                            actor.add_slot_typed::<PlaceholderScriptSlot>(slot_data.name, ComponentKind::Placeholder, slot_entity);
                        } else { scene.world.despawn(slot_entity); }
                        true
                    }
                } else {
                    scene.world.despawn(slot_entity);
                    false
                }
            }
            ComponentData::CanvasComponent(cc_data) => {
                // CanvasComponent を複製して新スロット専用エンティティに insert
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, CanvasComponent {
                                    width:             cc_data.width,
                                    height:            cc_data.height,
                                    auto_scale:        cc_data.auto_scale,
                                    viewport_ref:      cc_data.viewport_ref.clone(),
                                    gravity_mode:      cc_data.gravity_mode,
                                    draw_zone:         cc_data.draw_zone,
                                    pivot:             cc_data.pivot,
                                });
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<CanvasComponent>(slot_data.name, ComponentKind::Canvas, slot_entity);
                } else { scene.world.despawn(slot_entity); }
                true
            }
            ComponentData::SpriteComponent(sc_data) => {
                // SpriteComponent を複製して新スロット専用エンティティに insert
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, SpriteComponent {
                    texture_path: sc_data.texture_path,
                    color:        sc_data.color,
                    width:        sc_data.width,
                    height:       sc_data.height,
                    layer:        sc_data.layer,
                });
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<SpriteComponent>(slot_data.name, ComponentKind::Sprite, slot_entity);
                } else { scene.world.despawn(slot_entity); }
                true
            }
            ComponentData::InputMapComponent(ic_data) => {
                // InputMapComponent を複製して新スロット専用エンティティに insert
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, InputMapComponent { asset_path: ic_data.asset_path });
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<InputMapComponent>(slot_data.name, ComponentKind::InputMap, slot_entity);
                } else { scene.world.despawn(slot_entity); }
                true
            }
            ComponentData::CameraComponent(cc_data) => {
                // CameraComponent を複製して新スロット専用エンティティに insert
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, CameraComponent::from_data(cc_data));
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<CameraComponent>(slot_data.name, ComponentKind::Camera, slot_entity);
                } else { scene.world.despawn(slot_entity); }
                true
            }
            ComponentData::PluginComponent(pc_data) => {
                // PluginComponent を複製して新スロット専用エンティティに insert
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, PluginComponent {
                    plugin_name: pc_data.plugin_name,
                    fields:      pc_data.fields,
                });
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<PluginComponent>(slot_data.name, ComponentKind::Plugin, slot_entity);
                } else { scene.world.despawn(slot_entity); }
                true
            }
            ComponentData::ColliderComponent(cc_data) => {
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, ColliderComponent::from(cc_data));
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<ColliderComponent>(slot_data.name, ComponentKind::Collider, slot_entity);
                } else { scene.world.despawn(slot_entity); }
                true
            }
            ComponentData::Collider2dComponent(cc_data) => {
                use crate::engine::components::Collider2dComponent;
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, Collider2dComponent::from(cc_data));
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<Collider2dComponent>(slot_data.name, ComponentKind::Collider2d, slot_entity);
                } else { scene.world.despawn(slot_entity); }
                true
            }
            ComponentData::AudioComponent(ac_data) => {
                use crate::engine::components::AudioComponent;
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, AudioComponent::from_data(ac_data));
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<AudioComponent>(slot_data.name, ComponentKind::Audio, slot_entity);
                } else { scene.world.despawn(slot_entity); }
                true
            }
            ComponentData::AnimatorComponent(an_data) => {
                use crate::engine::components::AnimatorComponent;
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, AnimatorComponent::from_data(an_data));
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<AnimatorComponent>(slot_data.name, ComponentKind::Animator, slot_entity);
                } else { scene.world.despawn(slot_entity); }
                true
            }
            ComponentData::LightComponent(lc_data) => {
                use crate::engine::components::LightComponent;
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, LightComponent::from_data(lc_data));
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<LightComponent>(slot_data.name, ComponentKind::Light, slot_entity);
                } else { scene.world.despawn(slot_entity); }
                true
            }
            ComponentData::ParticleEmitterComponent(pe_data) => {
                use crate::engine::components::ParticleEmitterComponent;
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, ParticleEmitterComponent::from_data(pe_data));
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<ParticleEmitterComponent>(slot_data.name, ComponentKind::ParticleEmitter, slot_entity);
                } else { scene.world.despawn(slot_entity); }
                true
            }
            ComponentData::LegacyRigidbodyComponent(_) => {
                // 旧フォーマット互換: ColliderComponent に統合済みのためスロット追加しない
                false
            }
        };

        self.scene = Some(scene);
        if !slot_added { return; }

        let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
        self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
            world_line: wl,
            actor_dfs_id,
            before_slots,
            after_slots,
        }));

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 指定アクターのコンポーネントスロット一覧をデータとしてスナップショットする。
    pub(super) fn snapshot_actor_slots(&self, wl: u32, actor_dfs_id: u32) -> Vec<ComponentSlotData> {
        let Some(scene) = &self.scene else { return Vec::new() };
        let mut c = 0u32;
        find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
            .map(|actor| actor.to_data(&scene.world).components)
            .unwrap_or_default()
    }

    // ── Canvas 2D コンポーネント自動親子化 ────────────────────────

    pub(super) fn rebuild_actor_slots(
        &mut self,
        wl:           u32,
        actor_dfs_id: u32,
        slots_data:   Vec<ComponentSlotData>,
    ) {
        use crate::engine::core::loader::load_model;

        if self.draw_ctx.is_none() { return; }
        let host = self.scripting_host.clone();

        // scene を一時取り出して draw_ctx との借用競合を回避する
        let mut scene = match self.scene.take() {
            Some(s) => s,
            None => return,
        };
        let ctx = self.draw_ctx.as_ref().unwrap();

        // 既存スロットの entity を全て despawn する
        let existing: Vec<crate::engine::ecs::Entity> = {
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .map(|a| a.slot_entities().collect())
                .unwrap_or_default()
        };
        for e in existing { scene.world.despawn(e); }

        // 各スロットデータから新エンティティを生成してコンポーネントを insert する
        let mut new_slots = Vec::new();
        for slot_data in slots_data {
            let slot_entity = scene.world.spawn();
            match slot_data.component {
                ComponentData::ModelComponent(mc_data) => {
                    let cast_shadows = mc_data.cast_shadows;
                    let mc = if mc_data.model_path.is_empty() {
                        ModelComponent {
                            source_path:     String::new(),
                            model:           None,
                            gpu_model:       None,
                            instanced_batch: None,
                            instance_mats:   mc_data.instances,
                            instance_meta:   mc_data.meta,
                            group_meta:      mc_data.groups,
                            next_group_id:   mc_data.next_group_id,
                            anim_drive:      None,
                            cast_shadows,
                            material_overrides: mc_data.material_overrides,
                        }
                    } else {
                        let path = std::path::Path::new(&mc_data.model_path);
                        // キャッシュから CPU モデルを取得するか、ディスクから再ロードする
                        let model_arc: std::sync::Arc<crate::engine::core::loader::model::Model> = {
                            let mut cache = ctx.model_cache.borrow_mut();
                            if let Some(cached) = cache.get(&mc_data.model_path) {
                                std::sync::Arc::clone(cached)
                            } else {
                                match load_model(path) {
                                    Ok(m) => {
                                        let arc = std::sync::Arc::new(m);
                                        cache.insert(mc_data.model_path.clone(), std::sync::Arc::clone(&arc));
                                        arc
                                    }
                                    Err(_) => { scene.world.despawn(slot_entity); continue; }
                                }
                            }
                        };
                        let gpu_model       = ctx.upload_model_with_overrides(&*model_arc, &mc_data.material_overrides);
                        let instanced_batch = ctx.create_instanced_batch(&*model_arc, mc_data.instances.len() as u32);
                        ModelComponent {
                            source_path:     mc_data.model_path,
                            model:           Some(model_arc),
                            gpu_model:       Some(gpu_model),
                            instanced_batch: Some(instanced_batch),
                            instance_mats:   mc_data.instances,
                            instance_meta:   mc_data.meta,
                            group_meta:      mc_data.groups,
                            next_group_id:   mc_data.next_group_id,
                            anim_drive:      None,
                            cast_shadows,
                            material_overrides: mc_data.material_overrides,
                        }
                    };
                    scene.world.insert(slot_entity, mc);
                    new_slots.push(ComponentSlot::new::<ModelComponent>(slot_data.name, ComponentKind::Model, slot_entity));
                }
                ComponentData::ScriptComponent(sc_data) => {
                    // フィールド値も含めて CLR インスタンスを復元する（失敗時は Placeholder）
                    let created = host.as_ref().and_then(|h| ScriptComponent::new_with_fields(
                        std::sync::Arc::clone(h), sc_data.type_name.clone(), sc_data.fields.clone(),
                    ));
                    if let Some(sc) = created {
                        scene.world.insert(slot_entity, sc);
                        new_slots.push(ComponentSlot::new::<ScriptComponent>(slot_data.name, ComponentKind::Script, slot_entity));
                    } else {
                        scene.world.insert(slot_entity, PlaceholderScriptSlot {
                            script_path: sc_data.type_name,
                            fields:      sc_data.fields,
                        });
                        new_slots.push(ComponentSlot::new::<PlaceholderScriptSlot>(slot_data.name, ComponentKind::Placeholder, slot_entity));
                    }
                }
                ComponentData::CanvasComponent(cc_data) => {
                    scene.world.insert(slot_entity, CanvasComponent {
                                    width:             cc_data.width,
                                    height:            cc_data.height,
                                    auto_scale:        cc_data.auto_scale,
                                    viewport_ref:      cc_data.viewport_ref.clone(),
                                    gravity_mode:      cc_data.gravity_mode,
                                    draw_zone:         cc_data.draw_zone,
                                    pivot:             cc_data.pivot,
                                });
                    new_slots.push(ComponentSlot::new::<CanvasComponent>(slot_data.name, ComponentKind::Canvas, slot_entity));
                }
                ComponentData::SpriteComponent(sc_data) => {
                    scene.world.insert(slot_entity, SpriteComponent {
                        texture_path: sc_data.texture_path,
                        color:        sc_data.color,
                        width:        sc_data.width,
                        height:       sc_data.height,
                        layer:        sc_data.layer,
                    });
                    new_slots.push(ComponentSlot::new::<SpriteComponent>(slot_data.name, ComponentKind::Sprite, slot_entity));
                }
                ComponentData::InputMapComponent(ic_data) => {
                    scene.world.insert(slot_entity, InputMapComponent { asset_path: ic_data.asset_path });
                    new_slots.push(ComponentSlot::new::<InputMapComponent>(slot_data.name, ComponentKind::InputMap, slot_entity));
                }
                ComponentData::CameraComponent(cc_data) => {
                    scene.world.insert(slot_entity, CameraComponent::from_data(cc_data));
                    new_slots.push(ComponentSlot::new::<CameraComponent>(slot_data.name, ComponentKind::Camera, slot_entity));
                }
                ComponentData::PluginComponent(pc_data) => {
                    // PluginComponent を新スロット専用エンティティに insert する
                    scene.world.insert(slot_entity, PluginComponent {
                        plugin_name: pc_data.plugin_name,
                        fields:      pc_data.fields,
                    });
                    new_slots.push(ComponentSlot::new::<PluginComponent>(slot_data.name, ComponentKind::Plugin, slot_entity));
                }
                ComponentData::ColliderComponent(cc_data) => {
                    scene.world.insert(slot_entity, ColliderComponent::from(cc_data));
                    new_slots.push(ComponentSlot::new::<ColliderComponent>(slot_data.name, ComponentKind::Collider, slot_entity));
                }
                ComponentData::Collider2dComponent(cc_data) => {
                    use crate::engine::components::Collider2dComponent;
                    scene.world.insert(slot_entity, Collider2dComponent::from(cc_data));
                    new_slots.push(ComponentSlot::new::<Collider2dComponent>(slot_data.name, ComponentKind::Collider2d, slot_entity));
                }
                ComponentData::AudioComponent(ac_data) => {
                    use crate::engine::components::AudioComponent;
                    scene.world.insert(slot_entity, AudioComponent::from_data(ac_data));
                    new_slots.push(ComponentSlot::new::<AudioComponent>(slot_data.name, ComponentKind::Audio, slot_entity));
                }
                ComponentData::AnimatorComponent(an_data) => {
                    use crate::engine::components::AnimatorComponent;
                    scene.world.insert(slot_entity, AnimatorComponent::from_data(an_data));
                    new_slots.push(ComponentSlot::new::<AnimatorComponent>(slot_data.name, ComponentKind::Animator, slot_entity));
                }
                ComponentData::LightComponent(lc_data) => {
                    use crate::engine::components::LightComponent;
                    scene.world.insert(slot_entity, LightComponent::from_data(lc_data));
                    new_slots.push(ComponentSlot::new::<LightComponent>(slot_data.name, ComponentKind::Light, slot_entity));
                }
                ComponentData::ParticleEmitterComponent(pe_data) => {
                    use crate::engine::components::ParticleEmitterComponent;
                    scene.world.insert(slot_entity, ParticleEmitterComponent::from_data(pe_data));
                    new_slots.push(ComponentSlot::new::<ParticleEmitterComponent>(slot_data.name, ComponentKind::ParticleEmitter, slot_entity));
                }
                ComponentData::LegacyRigidbodyComponent(_) => {
                    // 旧フォーマット互換: ColliderComponent に統合済みのためスキップする
                    scene.world.despawn(slot_entity);
                }
            }
        }

        // actor.slots をまるごと置き換える
        {
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                actor.replace_slots(new_slots);
            }
        }

        self.scene = Some(scene);
    }

    /// InputMapComponent のアセットパスを更新する。
    pub(super) fn handle_set_inputmap_path(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        path: &str,
    ) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::InputMap)
                .map(|s| s.entity)
        };
        if let Some(entity) = slot_entity {
            let Some(scene) = &mut self.scene else { return };
            if let Some(ic) = scene.world.get_mut::<InputMapComponent>(entity) {
                ic.asset_path = path.to_string();
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }
}
