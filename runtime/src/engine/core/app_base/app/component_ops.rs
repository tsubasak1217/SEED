// ============================================================
//  component_ops.rs — コンポーネントスロットの追加・削除・変更操作
//
//  handle_add_component / send_actor_components /
//  handle_add_component_to_actor / handle_set_model_path /
//  handle_remove_component_slot / handle_rename_component_slot /
//  handle_duplicate_component /
//  snapshot_actor_slots / rebuild_actor_slots
// ============================================================

use crate::engine::components::{
    ModelComponent, Transform as ActorTransform, ComponentKind, ComponentData,
    ScriptComponent, PlaceholderScriptSlot, CanvasComponent,
    GROUP_ID_BASE, CanvasTransform, SpriteComponent,
};
use crate::engine::structs::objects::actor::{ComponentSlotData, ComponentSlot};
use crate::engine::structs::objects::Actor;
use crate::engine::core::app_base::undo::{ComponentSlotsSnapshotCommand, ActorTreeSnapshotCommand};

use super::{
    App, find_actor_by_dfs, find_actor_by_dfs_mut,
    actor_subtree_size, find_parent_canvas_info,
};

impl App {
    /// インスペクターの「コンポーネントを追加」リクエストを処理する（旧スタイル）。
    ///
    /// actor_id が 999_000_000 以上の仮想 ID を受け取り、ModelComponent を追加する。
    pub(super) fn handle_add_component(&mut self, actor_id: u32, component_type: &str, args: &str) {
        if self.draw_ctx.is_none() || self.ipc.is_none() || self.scene.is_none() { return; }

        let wl = self.active_world_line;
        // 仮想 ID から実際のインデックスへ変換する（999_000_000 以上が仮想 ID）
        let actor_idx = if actor_id >= 999_000_000 {
            (actor_id - 999_000_000) as usize
        } else {
            return;
        };

        match component_type {
            "ModelComponent" => {
                let path = std::path::Path::new(args);
                let model = match crate::engine::core::loader::load_model(path) {
                    Ok(m) => m,
                    Err(e) => {
                        if let Some(ipc) = &self.ipc { ipc.send(&format!("LOAD_ERROR:{e}")); }
                        return;
                    }
                };

                // GPU リソース構築（ctx の借用はここで完結させる）
                let (gpu_model, instanced_batch) = {
                    let ctx = self.draw_ctx.as_ref().unwrap();
                    (ctx.upload_model(&model), ctx.create_instanced_batch(&model, 1))
                };
                // Arc 化してキャッシュにも登録する（今後の Undo/Redo リビルドで再利用可能にする）
                let model_arc: std::sync::Arc<crate::engine::core::loader::model::Model> = {
                    let ctx = self.draw_ctx.as_ref().unwrap();
                    let arc = std::sync::Arc::new(model);
                    ctx.model_cache.borrow_mut()
                        .entry(args.to_string())
                        .or_insert_with(|| std::sync::Arc::clone(&arc));
                    arc
                };

                // アクターの現在 Transform（World から取得）を初期インスタンス位置に使う
                let initial_mat: [[f32; 4]; 4] = {
                    let scene = self.scene.as_ref().unwrap();
                    scene.actors.iter()
                        .filter(|a| a.world_line == wl)
                        .nth(actor_idx)
                        .and_then(|a| scene.world.get::<ActorTransform>(a.entity))
                        .map(|tf| tf.to_mat4())
                        .unwrap_or([[1.0,0.0,0.0,0.0],[0.0,1.0,0.0,0.0],[0.0,0.0,1.0,0.0],[0.0,0.0,0.0,1.0]])
                };
                let mc = ModelComponent {
                    source_path:     args.to_string(),
                    model:           Some(model_arc),
                    gpu_model:       Some(gpu_model),
                    instanced_batch: Some(instanced_batch),
                    instance_mats:   vec![initial_mat],
                    instance_meta:   vec![crate::engine::components::InstanceMeta::new("Instance_0")],
                    group_meta:      Vec::new(),
                    next_group_id:   GROUP_ID_BASE,
                };

                // スロット専用エンティティを spawn して world に insert し、スロットを登録する
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, mc);
                    if let Some(actor) = scene.actors.iter_mut()
                        .filter(|a| a.world_line == wl)
                        .nth(actor_idx)
                    {
                        actor.add_slot_typed::<ModelComponent>("ModelComponent".to_string(), ComponentKind::Model, slot_entity);
                        true
                    } else {
                        scene.world.despawn(slot_entity);
                        false
                    }
                };

                if found {
                    self.actor_virtual_selected_idx = None;
                    self.actor_virtual_selected_slot_idx = 0;
                    self.selected_instances = vec![0];
                    self.sync_anim_seeds();
                    self.send_selected();
                    self.send_hierarchy();
                    if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                }
            }
            _ => {}
        }
    }

    /// アクタースロットのコンポーネント一覧を送信する。
    ///
    /// 選択中アクターのコンポーネント情報をエディタへ送信する。
    /// selected_slot_idx: ピッキングで選択された MC スロットの連番（Inspector ハイライト用）。
    pub(super) fn send_actor_components(&self, dfs_id: u32, selected_slot_idx: usize) {
        let Some(ipc)   = &self.ipc   else { return };
        let Some(scene) = &self.scene else { return };
        let wl = self.active_world_line;

        let mut c = 0u32;
        let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c) else { return };

        // 2D Actor は CanvasTransform、3D Actor は Transform を World から取得する
        let is_2d = actor.is_2d();
        let transform_json = if is_2d {
            // CanvasTransform: position(XY), rotation(Z 回転), scale(XY), pivot(XY), anchor(XY)
            let ct = scene.world.get::<CanvasTransform>(actor.entity).cloned().unwrap_or_default();
            format!(
                r#","canvas_transform":{{"px":{:.4},"py":{:.4},"rotation":{:.4},"sx":{:.4},"sy":{:.4},"pivx":{:.4},"pivy":{:.4},"anchor_x":{:.4},"anchor_y":{:.4}}}"#,
                ct.position[0], ct.position[1], ct.rotation, ct.scale[0], ct.scale[1],
                ct.pivot[0], ct.pivot[1], ct.anchor[0], ct.anchor[1],
            )
        } else {
            let tf = scene.world.get::<ActorTransform>(actor.entity).cloned().unwrap_or_default();
            let [px, py, pz] = tf.position;
            let [ex, ey, ez] = tf.rotation;
            let [sx, sy, sz] = tf.scale;
            format!(
                r#","transform":{{"px":{px:.4},"py":{py:.4},"pz":{pz:.4},"ex":{ex:.4},"ey":{ey:.4},"ez":{ez:.4},"sx":{sx:.4},"sy":{sy:.4},"sz":{sz:.4}}}"#
            )
        };

        // actor.to_data() でシリアライズ済みコンポーネント一覧を取得
        let actor_data = actor.to_data(&scene.world);

        let mut comps_json = String::from("[");
        for (i, slot_data) in actor_data.components.iter().enumerate() {
            if i > 0 { comps_json.push(','); }
            let (type_name, extra) = match &slot_data.component {
                ComponentData::ModelComponent(d) => {
                    let path_json = serde_json::to_string(&d.model_path).unwrap_or_default();
                    ("ModelComponent", format!(r#","model_path":{path_json}"#))
                }
                ComponentData::ScriptComponent(d) => {
                    let path_json = serde_json::to_string(&d.type_name).unwrap_or_default();
                    ("ScriptComponent", format!(r#","model_path":{path_json}"#))
                }
                ComponentData::CanvasComponent(d) => {
                    // width / height をインスペクター用に送信する
                    ("CanvasComponent", format!(r#","width":{:.4},"height":{:.4}"#, d.width, d.height))
                }
                ComponentData::SpriteComponent(d) => {
                    // テクスチャパス・カラー・サイズをインスペクター用に送信する
                    let path_json = serde_json::to_string(&d.texture_path).unwrap_or_default();
                    ("SpriteComponent", format!(
                        r#","texture_path":{path_json},"cr":{:.4},"cg":{:.4},"cb":{:.4},"ca":{:.4},"sprite_w":{:.4},"sprite_h":{:.4}"#,
                        d.color[0], d.color[1], d.color[2], d.color[3], d.width, d.height,
                    ))
                }
            };
            comps_json.push_str(&format!(
                r#"{{"slot":{},"name":{},"type":"{}"{}}}"#,
                i,
                serde_json::to_string(&slot_data.name).unwrap_or_default(),
                type_name,
                extra,
            ));
        }
        comps_json.push(']');

        let name_json = serde_json::to_string(&actor.name).unwrap_or_default();
        // selected_slot_idx: Inspector 側でどのコンポーネントスロットを選択状態にするかを示す
        // transform_json は 3D: "transform":{...}、2D: "canvas_transform":{...} のいずれか
        let json = format!(
            r#"{{"id":{dfs_id},"name":{name_json},"selected_slot":{selected_slot_idx}{transform_json},"components":{comps_json}}}"#
        );
        ipc.send(&format!("ACTOR_COMPONENTS:{json}"));
    }

    /// コンポーネントをアクターに追加する（新アーキテクチャ版）。
    ///
    /// actor_dfs_id で特定したアクターに component_type のコンポーネントを追加する。
    /// slot_name はスロットの表示名、args はコンポーネント初期化引数（モデルパス等）。
    pub(super) fn handle_add_component_to_actor(
        &mut self,
        actor_dfs_id:   u32,
        component_type: &str,
        slot_name:      &str,
        args:           &str,
    ) {
        if self.draw_ctx.is_none() || self.ipc.is_none() || self.scene.is_none() { return; }
        let wl = self.active_world_line;

        // actor.entity を先に取得（Transform 参照のみに使用）
        let actor_entity_opt = {
            let scene = self.scene.as_ref().unwrap();
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c).map(|a| a.entity)
        };
        let Some(actor_entity) = actor_entity_opt else { return };

        let before_slots = self.snapshot_actor_slots(wl, actor_dfs_id);

        match component_type {
            "ModelComponent" => {
                use crate::engine::components::InstanceMeta;
                let mc = if args.is_empty() {
                    ModelComponent::empty()
                } else {
                    let path = std::path::Path::new(args);
                    let model = match crate::engine::core::loader::load_model(path) {
                        Ok(m) => m,
                        Err(e) => {
                            if let Some(ipc) = &self.ipc { ipc.send(&format!("LOAD_ERROR:{e}")); }
                            return;
                        }
                    };
                    let (gpu_model, instanced_batch) = {
                        let ctx = self.draw_ctx.as_ref().unwrap();
                        (ctx.upload_model(&model), ctx.create_instanced_batch(&model, 1))
                    };
                    // Arc 化してキャッシュに登録する
                    let model_arc: std::sync::Arc<crate::engine::core::loader::model::Model> = {
                        let ctx = self.draw_ctx.as_ref().unwrap();
                        let arc = std::sync::Arc::new(model);
                        ctx.model_cache.borrow_mut()
                            .entry(args.to_string())
                            .or_insert_with(|| std::sync::Arc::clone(&arc));
                        arc
                    };
                    // アクターの現在 transform を初期インスタンス位置に使う
                    let initial_mat: [[f32; 4]; 4] = {
                        let scene = self.scene.as_ref().unwrap();
                        scene.world.get::<ActorTransform>(actor_entity)
                            .map(|t| t.to_mat4())
                            .unwrap_or([[1.0,0.0,0.0,0.0],[0.0,1.0,0.0,0.0],[0.0,0.0,1.0,0.0],[0.0,0.0,0.0,1.0]])
                    };
                    ModelComponent {
                        source_path:     args.to_string(),
                        model:           Some(model_arc),
                        gpu_model:       Some(gpu_model),
                        instanced_batch: Some(instanced_batch),
                        instance_mats:   vec![initial_mat],
                        instance_meta:   vec![InstanceMeta::new("Instance_0")],
                        group_meta:      Vec::new(),
                        next_group_id:   GROUP_ID_BASE,
                    }
                };
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    // スロット専用エンティティを spawn してコンポーネントを格納する
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, mc);
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<ModelComponent>(name, ComponentKind::Model, slot_entity);
                        true
                    } else {
                        scene.world.despawn(slot_entity);
                        false
                    }
                };
                if found {
                    let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
                    self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
                        world_line: wl, actor_dfs_id, before_slots, after_slots,
                    }));
                    self.actor_virtual_selected_idx = None;
                    self.actor_virtual_selected_slot_idx = 0;
                    self.selected_instances.clear();
                    self.send_hierarchy();
                    self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
                    if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                }
            }
            "ScriptComponent" => {
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    // スロット専用エンティティを spawn してコンポーネントを格納する
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, PlaceholderScriptSlot { script_path: String::new() });
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<PlaceholderScriptSlot>(name, ComponentKind::Placeholder, slot_entity);
                        true
                    } else {
                        scene.world.despawn(slot_entity);
                        false
                    }
                };
                if found {
                    let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
                    self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
                        world_line: wl, actor_dfs_id, before_slots, after_slots,
                    }));
                    self.actor_virtual_selected_idx = None;
                    self.actor_virtual_selected_slot_idx = 0;
                    self.selected_instances.clear();
                    self.send_hierarchy();
                    self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
                    if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                }
            }
            "CanvasComponent" => {
                // デフォルトサイズ（1920×1080）の CanvasComponent を追加する。
                // サイズはインスペクターから後で変更可能。
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, CanvasComponent::default());
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<CanvasComponent>(name, ComponentKind::Canvas, slot_entity);
                        true
                    } else {
                        scene.world.despawn(slot_entity);
                        false
                    }
                };
                if found {
                    let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
                    self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
                        world_line: wl, actor_dfs_id, before_slots, after_slots,
                    }));
                    self.actor_virtual_selected_slot_idx = 0;
                    self.selected_instances.clear();
                    self.send_hierarchy();
                    self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
                    if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                }
            }
            "SpriteComponent" => {
                // SpriteComponent をアクターに直接追加する（Canvas の子アクターを選択した場合）。
                // 自動親子化ロジックを経由せずに直接追加するパス。
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, SpriteComponent::default());
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<SpriteComponent>(name, ComponentKind::Sprite, slot_entity);
                        true
                    } else {
                        scene.world.despawn(slot_entity);
                        false
                    }
                };
                if found {
                    let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
                    self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
                        world_line: wl, actor_dfs_id, before_slots, after_slots,
                    }));
                    self.actor_virtual_selected_slot_idx = 0;
                    self.selected_instances.clear();
                    self.send_hierarchy();
                    self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
                    if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                }
            }
            _ => {}
        }
    }

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

        // PlaceholderScriptSlot の場合はスロット entity のパスのみ更新して早期リターン
        if slot_kind == ComponentKind::Placeholder {
            let scene = self.scene.as_mut().unwrap();
            if let Some(ps) = scene.world.get_mut::<PlaceholderScriptSlot>(slot_entity) {
                ps.script_path = path.to_string();
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
        let (gpu_model, instanced_batch) = {
            let ctx = self.draw_ctx.as_ref().unwrap();
            (ctx.upload_model(&model), ctx.create_instanced_batch(&model, 1))
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
            true
        } else { false };
        if found {
            self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
            if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
        }
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
                    let gpu_model       = ctx.upload_model(&*model_arc);
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
                    if let Some(sc) = ScriptComponent::new(std::sync::Arc::clone(h), sc_data.type_name.clone()) {
                        scene.world.insert(slot_entity, sc);
                        let mut c = 0u32;
                        if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                            actor.add_slot_typed::<ScriptComponent>(slot_data.name, ComponentKind::Script, slot_entity);
                        } else { scene.world.despawn(slot_entity); }
                        true
                    } else {
                        scene.world.insert(slot_entity, PlaceholderScriptSlot { script_path: sc_data.type_name });
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
                scene.world.insert(slot_entity, CanvasComponent { width: cc_data.width, height: cc_data.height });
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
                });
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<SpriteComponent>(slot_data.name, ComponentKind::Sprite, slot_entity);
                } else { scene.world.despawn(slot_entity); }
                true
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

    // ─── Canvas 2D コンポーネント自動親子化 ───────────────────────

    /// Canvas 上に配置するコンポーネント（Sprite など）を追加する際の
    /// 自動親子化処理エントリポイント。
    ///
    /// 以下の 3 パターンを自動判定して処理する:
    /// 1. 対象アクターが Canvas を持つ → 新規子 Actor2D を作成してそこに追加
    /// 2. 対象アクターの親が Canvas を持つ → 対象アクターに直接追加
    /// 3. どちらでもない → Canvas を追加してから子 Actor2D を作成して追加
    pub(super) fn handle_add_canvas_child_component(
        &mut self,
        actor_dfs_id:   u32,
        component_type: &str,
        slot_name:      &str,
        args:           &str,
    ) {
        if self.scene.is_none() { return; }
        let wl = self.active_world_line;

        // 対象アクターが Canvas を持つか確認
        let target_has_canvas = {
            let scene = self.scene.as_ref().unwrap();
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .map(|a| a.has_kind(ComponentKind::Canvas))
                .unwrap_or(false)
        };

        // 親アクターが Canvas を持つか確認
        let (_parent_dfs, parent_has_canvas) = {
            let scene = self.scene.as_ref().unwrap();
            find_parent_canvas_info(&scene.actors, wl, actor_dfs_id)
        };

        if target_has_canvas {
            // Case 1: 対象が Canvas → 新規子アクターを作成してそこに追加
            self.spawn_canvas_child_with_component(actor_dfs_id, component_type, slot_name);
        } else if parent_has_canvas {
            // Case 2: 親が Canvas → 対象アクターに直接追加（通常フロー）
            self.handle_add_component_to_actor(actor_dfs_id, component_type, slot_name, args);
        } else {
            // Case 3: Canvas なし → Canvas を追加してから子アクターを作成して追加
            self.add_canvas_then_child_with_component(actor_dfs_id, component_type, slot_name);
        }
    }

    /// 指定アクターの子として新規 Actor2D を生成し、そこにコンポーネントを追加する。
    ///
    /// アクターツリーが変更されるため ActorTreeSnapshotCommand で Undo を記録する。
    fn spawn_canvas_child_with_component(
        &mut self,
        parent_dfs_id:  u32,
        component_type: &str,
        slot_name:      &str,
    ) {
        let wl = self.active_world_line;
        let before_actors = self.snapshot_actors_for_wl(wl);

        // 親のサブツリーサイズを記録（子を追加した後の child DFS id 計算用）
        let parent_size_before = {
            let scene = self.scene.as_ref().unwrap();
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, parent_dfs_id, &mut c)
                .map(|a| actor_subtree_size(a))
                .unwrap_or(1)
        };

        // 新規子 Actor2D のエンティティを生成して CanvasTransform を挿入する
        let child_entity = {
            let scene = self.scene.as_mut().unwrap();
            let e = scene.world.spawn();
            scene.world.insert(e, CanvasTransform::default());
            e
        };

        // 子アクターを構築して親の children に追加する
        let child_added = {
            let scene = self.scene.as_mut().unwrap();
            let mut child = Actor::new_2d(child_entity, slot_name);
            child.world_line = wl;
            let mut c = 0u32;
            if let Some(parent) = find_actor_by_dfs_mut(&mut scene.actors, wl, parent_dfs_id, &mut c) {
                parent.add_child(child);
                true
            } else {
                scene.world.despawn(child_entity);
                false
            }
        };
        if !child_added { return; }

        // 新規子の DFS id = 親 DFS id + 追加前の親サブツリーサイズ
        let child_dfs_id = parent_dfs_id + parent_size_before;

        // 子アクターにコンポーネントスロットを追加する
        self.insert_canvas_component_slot(wl, child_dfs_id, component_type, slot_name);

        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));

        // 子アクターを選択状態にしてエディタへ通知する
        self.actor_virtual_selected_idx      = Some(child_dfs_id as usize);
        self.selected_actor_dfs_ids          = vec![child_dfs_id as usize];
        self.selected_instances.clear();
        self.send_selected();
        self.send_hierarchy();
        self.send_actor_components(child_dfs_id, 0);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 対象アクターに Canvas を追加し、さらに子 Actor2D を生成してコンポーネントを追加する。
    ///
    /// 対象アクターに Canvas も親 Canvas もない場合（空のアクター等）に呼ばれる。
    /// アクターツリーとコンポーネントの両方が変わるため ActorTreeSnapshotCommand で記録する。
    fn add_canvas_then_child_with_component(
        &mut self,
        actor_dfs_id:   u32,
        component_type: &str,
        slot_name:      &str,
    ) {
        let wl = self.active_world_line;
        let before_actors = self.snapshot_actors_for_wl(wl);

        // 対象アクターに Canvas スロットを追加する
        {
            let scene = self.scene.as_mut().unwrap();
            let slot_entity = scene.world.spawn();
            scene.world.insert(slot_entity, CanvasComponent::default());
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                actor.add_slot_typed::<CanvasComponent>(
                    "Canvas".to_string(), ComponentKind::Canvas, slot_entity,
                );
            } else {
                scene.world.despawn(slot_entity);
                return;
            }
        }

        // Canvas 追加後の対象アクターのサブツリーサイズを取得する
        // （Canvas スロット追加はノード数に影響しないためサブツリーサイズは変わらない）
        let parent_size = {
            let scene = self.scene.as_ref().unwrap();
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .map(|a| actor_subtree_size(a))
                .unwrap_or(1)
        };

        // 新規子 Actor2D のエンティティを生成して CanvasTransform を挿入する
        let child_entity = {
            let scene = self.scene.as_mut().unwrap();
            let e = scene.world.spawn();
            scene.world.insert(e, CanvasTransform::default());
            e
        };

        // 子アクターを構築して対象アクターの children に追加する
        let child_added = {
            let scene = self.scene.as_mut().unwrap();
            let mut child = Actor::new_2d(child_entity, slot_name);
            child.world_line = wl;
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                actor.add_child(child);
                true
            } else {
                scene.world.despawn(child_entity);
                false
            }
        };
        if !child_added { return; }

        let child_dfs_id = actor_dfs_id + parent_size;

        // 子アクターにコンポーネントスロットを追加する
        self.insert_canvas_component_slot(wl, child_dfs_id, component_type, slot_name);

        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));

        // 子アクターを選択状態にしてエディタへ通知する
        self.actor_virtual_selected_idx      = Some(child_dfs_id as usize);
        self.selected_actor_dfs_ids          = vec![child_dfs_id as usize];
        self.selected_instances.clear();
        self.send_selected();
        self.send_hierarchy();
        self.send_actor_components(child_dfs_id, 0);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 指定アクターの指定スロットに Canvas 2D コンポーネントを挿入する内部ヘルパー。
    ///
    /// 現在は SpriteComponent のみ対応。
    /// 今後 Canvas 上に配置するコンポーネント（Image など）を追加したらここに case を足す。
    fn insert_canvas_component_slot(
        &mut self,
        wl:             u32,
        actor_dfs_id:   u32,
        component_type: &str,
        slot_name:      &str,
    ) {
        match component_type {
            "SpriteComponent" => {
                let scene = self.scene.as_mut().unwrap();
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, SpriteComponent::default());
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<SpriteComponent>(
                        slot_name.to_string(), ComponentKind::Sprite, slot_entity,
                    );
                } else {
                    scene.world.despawn(slot_entity);
                }
            }
            _ => {}
        }
    }

    // ─── SpriteComponent プロパティ設定 ───────────────────────────

    /// SpriteComponent のテクスチャパスを更新する。
    pub(super) fn handle_set_sprite_path(&mut self, actor_dfs_id: u32, slot_idx: u32, path: &str) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Sprite)
                .map(|s| s.entity)
        };
        if let Some(entity) = slot_entity {
            let Some(scene) = &mut self.scene else { return };
            if let Some(sc) = scene.world.get_mut::<SpriteComponent>(entity) {
                sc.texture_path = path.to_string();
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// SpriteComponent のカラーを更新する（RGBA 正規化値）。
    pub(super) fn handle_set_sprite_color(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        r: f32, g: f32, b: f32, a: f32,
    ) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Sprite)
                .map(|s| s.entity)
        };
        if let Some(entity) = slot_entity {
            let Some(scene) = &mut self.scene else { return };
            if let Some(sc) = scene.world.get_mut::<SpriteComponent>(entity) {
                sc.color = [r, g, b, a];
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// SpriteComponent のサイズを更新する（キャンバスユニット）。
    pub(super) fn handle_set_sprite_size(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        width: f32,
        height: f32,
    ) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Sprite)
                .map(|s| s.entity)
        };
        if let Some(entity) = slot_entity {
            let Some(scene) = &mut self.scene else { return };
            if let Some(sc) = scene.world.get_mut::<SpriteComponent>(entity) {
                sc.width  = width;
                sc.height = height;
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// CanvasTransform の anchor を更新する。
    ///
    /// anchor は親 Canvas における position 基準点（[0,1]×[0,1]）。
    /// (0,0) = 左上, (0.5,0.5) = 中央, (1,1) = 右下。
    pub(super) fn handle_set_canvas_anchor(&mut self, actor_dfs_id: u32, ax: f32, ay: f32) {
        let wl = self.active_world_line;
        let Some(scene) = &mut self.scene else { return };
        let mut c = 0u32;
        let actor = match crate::engine::core::app_base::app::find_actor_by_dfs_mut(
            &mut scene.actors, wl, actor_dfs_id, &mut c,
        ) {
            Some(a) => a,
            None    => return,
        };
        if let Some(ct) = scene.world.get_mut::<CanvasTransform>(actor.entity) {
            ct.anchor = [ax.clamp(0.0, 1.0), ay.clamp(0.0, 1.0)];
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// Undo/Redo 時にアクターのコンポーネントスロットを再構築する。
    ///
    /// 既存スロットの ECS エンティティを全て despawn し、slots_data から
    /// 新たにスロット専用エンティティを spawn してコンポーネントを insert する。
    /// actor.slots もまるごと置き換えるため、Undo/Redo のみで使用すること。
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
                        let gpu_model       = ctx.upload_model(&*model_arc);
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
                        }
                    };
                    scene.world.insert(slot_entity, mc);
                    new_slots.push(ComponentSlot::new::<ModelComponent>(slot_data.name, ComponentKind::Model, slot_entity));
                }
                ComponentData::ScriptComponent(sc_data) => {
                    if let Some(h) = &host {
                        if let Some(sc) = ScriptComponent::new(std::sync::Arc::clone(h), sc_data.type_name.clone()) {
                            scene.world.insert(slot_entity, sc);
                            new_slots.push(ComponentSlot::new::<ScriptComponent>(slot_data.name, ComponentKind::Script, slot_entity));
                        } else {
                            scene.world.insert(slot_entity, PlaceholderScriptSlot { script_path: sc_data.type_name });
                            new_slots.push(ComponentSlot::new::<PlaceholderScriptSlot>(slot_data.name, ComponentKind::Placeholder, slot_entity));
                        }
                    } else {
                        scene.world.insert(slot_entity, PlaceholderScriptSlot { script_path: sc_data.type_name });
                        new_slots.push(ComponentSlot::new::<PlaceholderScriptSlot>(slot_data.name, ComponentKind::Placeholder, slot_entity));
                    }
                }
                ComponentData::CanvasComponent(cc_data) => {
                    scene.world.insert(slot_entity, CanvasComponent { width: cc_data.width, height: cc_data.height });
                    new_slots.push(ComponentSlot::new::<CanvasComponent>(slot_data.name, ComponentKind::Canvas, slot_entity));
                }
                ComponentData::SpriteComponent(sc_data) => {
                    scene.world.insert(slot_entity, SpriteComponent {
                        texture_path: sc_data.texture_path,
                        color:        sc_data.color,
                        width:        sc_data.width,
                        height:       sc_data.height,
                    });
                    new_slots.push(ComponentSlot::new::<SpriteComponent>(slot_data.name, ComponentKind::Sprite, slot_entity));
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
}
