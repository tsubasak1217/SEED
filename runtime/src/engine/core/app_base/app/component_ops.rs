// ============================================================
//  component_ops.rs — コンポーネントスロットの追加・送信操作
//
//  【含む処理】
//  - handle_add_component:          コンポーネント追加（旧スタイル MC）
//  - send_actor_components:          スロット情報を IPC でエディタへ送信
//  - handle_add_component_to_actor:  アクターへのコンポーネント追加（現行フロー）
//
//  スロット編集・再構築 → slot_ops.rs
//  Canvas / Sprite 操作 → canvas_component_ops.rs
//  Camera コンポーネント操作 → camera_component_ops.rs
// ============================================================


use crate::engine::components::{
    ModelComponent, Transform as ActorTransform, ComponentKind, ComponentData,
    PlaceholderScriptSlot, CanvasComponent,
    GROUP_ID_BASE, CanvasTransform, SpriteComponent, InputMapComponent,
    CameraComponent, ColliderComponent, Collider2dComponent,
};
use crate::engine::core::app_base::undo::ComponentSlotsSnapshotCommand;

use super::{
    App, find_actor_by_dfs, find_actor_by_dfs_mut, find_actor_root_info,
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

        // このアクターがトップレベルルートか / サブツリーがビューポート所属（ルートが Actor2D）かを
        // 判定する。インスペクタのルートキャンバス UI（解像度自動計算表示・Transform 固定・
        // 子キャンバスの基準領域非表示）に使用する。
        let (is_root, root_is_2d) =
            find_actor_root_info(&scene.actors, wl, dfs_id).unwrap_or((false, false));
        // ビューポート所属ルートキャンバス = トップレベル Actor2D + Canvas スロットあり
        let is_viewport_root_canvas = is_root && root_is_2d
            && actor.slots().iter().any(|s| s.kind == ComponentKind::Canvas);

        // 2D Actor は CanvasTransform、3D Actor は Transform を World から取得する
        let is_2d = actor.is_2d();
        let transform_json = if is_2d {
            // CanvasTransform: position(XY), rotation(Z 回転), scale(XY), pivot(XY), anchor(XY),
            // スケールモード(scale_transform/scale_size/keep_aspect_ratio: 1|0,
            // aspect_ratio_axis: 0=Width/1=Height)
            let ct = scene.world.get::<CanvasTransform>(actor.entity).cloned().unwrap_or_default();
            let ct_axis = if matches!(ct.aspect_ratio_axis, crate::engine::components::AspectRatioAxis::Height) { 1u8 } else { 0u8 };
            format!(
                r#","canvas_transform":{{"px":{:.4},"py":{:.4},"rotation":{:.4},"sx":{:.4},"sy":{:.4},"pivx":{:.4},"pivy":{:.4},"anchor_x":{:.4},"anchor_y":{:.4},"scale_transform":{},"scale_size":{},"keep_aspect_ratio":{},"aspect_ratio_axis":{}}}"#,
                ct.position[0], ct.position[1], ct.rotation, ct.scale[0], ct.scale[1],
                ct.pivot[0], ct.pivot[1], ct.anchor[0], ct.anchor[1],
                ct.scale_transform as u8, ct.scale_size as u8, ct.keep_aspect_ratio as u8, ct_axis,
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
                    // スクリプトパスに加え [SerializeField] フィールドの現在値を送信する。
                    // エディタのインスペクターが初期値として表示し、SET_SCRIPT_FIELD で書き戻す。
                    let path_json   = serde_json::to_string(&d.type_name).unwrap_or_default();
                    let fields_json = serde_json::to_string(&d.fields).unwrap_or_else(|_| "{}".into());
                    ("ScriptComponent", format!(r#","model_path":{path_json},"script_fields":{fields_json}"#))
                }
                ComponentData::CanvasComponent(d) => {
                    // width / height / スケールモード / 自動スケール / ビューポート参照をインスペクター用に送信する
                    use crate::engine::components::CanvasViewportRef;
                    let (vp_ref_type, vp_actor_name, vp_slot_name) = match &d.viewport_ref {
                        CanvasViewportRef::Window     => ("window",      String::new(), String::new()),
                        // メインカメラ基準（レターボックス等を除いた実効表示矩形。カメラ無しはウィンドウ扱い）
                        CanvasViewportRef::MainCamera => ("main_camera", String::new(), String::new()),
                        CanvasViewportRef::Camera { actor_name, slot_name } => {
                            ("camera", actor_name.clone(), slot_name.clone())
                        }
                    };
                    let vp_actor_json = serde_json::to_string(&vp_actor_name).unwrap_or_default();
                    let vp_slot_json  = serde_json::to_string(&vp_slot_name).unwrap_or_default();
                    // gravity_mode: 0=screen_down, 1=canvas_down
                    let gravity_mode_val = if matches!(d.gravity_mode, crate::engine::components::GravityMode::CanvasDown) { 1u8 } else { 0u8 };
                    // draw_zone: "foreground"（3D ワールドの手前・デフォルト）| "background"（奥）
                    let draw_zone_str = if matches!(d.draw_zone, crate::engine::components::CanvasDrawZone::Background) { "background" } else { "foreground" };
                    // ビューポート・ルートキャンバス: 自動解像度（プロジェクト設定×カメラ設定）を
                    // インスペクタの読み取り専用表示用に添付する（Phase B）。
                    // 描画側（build_root_canvas_auto_size_map）と同一の計算を共有する。
                    let auto_size_json = if is_viewport_root_canvas {
                        use super::canvas_collect::{
                            effective_root_canvas_size,
                            find_camera_component_by_ref, find_main_camera_in_wl,
                        };
                        use crate::engine::components::CanvasViewportRef as VpRef;
                        // 参照カメラを解決する（Window / カメラ不在は None）
                        let cam = match &d.viewport_ref {
                            VpRef::Camera { actor_name, slot_name } =>
                                find_camera_component_by_ref(
                                    &scene.actors, &scene.world, actor_name, slot_name),
                            VpRef::MainCamera =>
                                find_main_camera_in_wl(&scene.actors, &scene.world, wl),
                            VpRef::Window => None,
                        };
                        let [aw, ah] = effective_root_canvas_size(self.project_resolution, cam);
                        format!(r#","auto_w":{aw:.1},"auto_h":{ah:.1}"#)
                    } else {
                        String::new()
                    };
                    // pivot: 3D キャンバス専用（Actor3D アタッチ時のみ有効）
                    // 注: スケールモード（scale_transform/scale_size/keep_aspect_ratio/
                    //     aspect_ratio_axis）は CanvasTransform 側へ移動したため、ここでは送らない。
                    ("CanvasComponent", format!(
                        r#","width":{:.4},"height":{:.4},"auto_scale":{},"vp_ref_type":"{vp_ref_type}","vp_ref_actor":{vp_actor_json},"vp_ref_slot":{vp_slot_json},"gravity_mode":{gravity_mode_val},"draw_zone":"{draw_zone_str}","pivot_x":{:.4},"pivot_y":{:.4}{auto_size_json}"#,
                        d.width, d.height,
                        d.auto_scale       as u8,
                        d.pivot[0],
                        d.pivot[1],
                    ))
                }
                ComponentData::SpriteComponent(d) => {
                    // テクスチャパス・カラー・サイズ・レイヤーをインスペクター用に送信する
                    let path_json = serde_json::to_string(&d.texture_path).unwrap_or_default();
                    ("SpriteComponent", format!(
                        r#","texture_path":{path_json},"cr":{:.4},"cg":{:.4},"cb":{:.4},"ca":{:.4},"sprite_w":{:.4},"sprite_h":{:.4},"layer":{}"#,
                        d.color[0], d.color[1], d.color[2], d.color[3], d.width, d.height,
                        d.layer,
                    ))
                }
                ComponentData::InputMapComponent(d) => {
                    // アセットパスをインスペクター用に送信する
                    let path_json = serde_json::to_string(&d.asset_path).unwrap_or_default();
                    ("InputMapComponent", format!(r#","asset_path":{path_json}"#))
                }
                ComponentData::AudioComponent(d) => {
                    // 音声パス・音量・ループ等の全フィールドをインスペクター用に送信する
                    let path_json = serde_json::to_string(&d.audio_path).unwrap_or_default();
                    ("AudioComponent", format!(
                        r#","audio_path":{path_json},"volume":{:.4},"loop":{},"play_on_start":{},"spatial":{},"min_distance":{:.4},"max_distance":{:.4},"pan":{:.4}"#,
                        d.volume, d.looped as u8, d.play_on_start as u8, d.spatial as u8,
                        d.min_distance, d.max_distance, d.pan,
                    ))
                }
                ComponentData::AnimatorComponent(d) => {
                    // アニメーター: クリップ数・既定クリップ・自動再生・速度をインスペクター用に送信する。
                    // 詳細なクリップ一覧編集 UI は P2 で対応するため、ここでは概要情報のみ返す。
                    let default_clip_json = serde_json::to_string(&d.default_clip).unwrap_or_default();
                    ("AnimatorComponent", format!(
                        r#","clip_count":{},"default_clip":{default_clip_json},"play_on_start":{},"speed":{:.4}"#,
                        d.clips.len(), d.play_on_start as u8, d.speed,
                    ))
                }
                ComponentData::CameraComponent(d) => {
                    // FOV / near / far / is_main / clear_color / scaling_mode / target_size /
                    // bar_color / projection / ortho_height をインスペクター用に送信する
                    ("CameraComponent", format!(
                        r#","fov_y_deg":{:.4},"near":{:.4},"far":{:.4},"is_main":{},"cr":{:.4},"cg":{:.4},"cb":{:.4},"ca":{:.4},"scaling_mode":"{}","target_width":{},"target_height":{},"bar_cr":{:.4},"bar_cg":{:.4},"bar_cb":{:.4},"bar_ca":{:.4},"projection":"{}","ortho_height":{:.4}"#,
                        d.fov_y_deg, d.near, d.far, d.is_main as u8,
                        d.clear_color[0], d.clear_color[1], d.clear_color[2], d.clear_color[3],
                        d.scaling_mode.as_str(), d.target_width, d.target_height,
                        d.bar_color[0], d.bar_color[1], d.bar_color[2], d.bar_color[3],
                        d.projection.as_str(), d.ortho_height,
                    ))
                }
                ComponentData::PluginComponent(d) => {
                    // プラグイン名とフィールド定義＋現在値をインスペクター用に送信する
                    let plugin_name_json = serde_json::to_string(&d.plugin_name).unwrap_or_default();
                    // フィールド定義はレジストリから取得する
                    let fields_json = if let Some(lp) = self.plugin_registry.get(&d.plugin_name) {
                        let defs = lp.plugin.field_defs();
                        let arr: Vec<String> = defs.iter().map(|def| {
                            let key   = serde_json::to_string(&def.key).unwrap_or_default();
                            let label = serde_json::to_string(&def.label).unwrap_or_default();
                            let kind  = serde_json::to_string(&def.kind).unwrap_or("null".to_string());
                            let cur   = serde_json::to_string(
                                d.fields.get(&def.key).map(|s| s.as_str()).unwrap_or(&def.default_value)
                            ).unwrap_or_default();
                            let tooltip = serde_json::to_string(&def.tooltip).unwrap_or_default();
                            format!(r#"{{"key":{key},"label":{label},"kind":{kind},"current_value":{cur},"tooltip":{tooltip}}}"#)
                        }).collect();
                        format!("[{}]", arr.join(","))
                    } else {
                        // プラグインが未ロードでも保存データは表示する（読み取り専用）
                        let arr: Vec<String> = d.fields.iter().map(|(k, v)| {
                            let key = serde_json::to_string(k).unwrap_or_default();
                            let val = serde_json::to_string(v).unwrap_or_default();
                            format!(r#"{{"key":{key},"label":{key},"kind":{{"type":"String","params":{{"max_len":256}}}},"current_value":{val},"tooltip":""}}"#)
                        }).collect();
                        format!("[{}]", arr.join(","))
                    };
                    ("PluginComponent", format!(
                        r#","plugin_name":{plugin_name_json},"plugin_fields":{fields_json}"#
                    ))
                }
                ComponentData::ColliderComponent(d) => {
                    // ColliderComponent のデータを JSON シリアライズしてエディタへ送信する
                    let json = serde_json::to_string(d).unwrap_or_default();
                    ("ColliderComponent", format!(r#","collider_data":{json}"#))
                }
                ComponentData::Collider2dComponent(d) => {
                    // Collider2dComponent のデータを JSON シリアライズしてエディタへ送信する
                    let json = serde_json::to_string(d).unwrap_or_default();
                    ("Collider2dComponent", format!(r#","collider_data":{json}"#))
                }
                ComponentData::LegacyRigidbodyComponent(_) => {
                    // 旧フォーマット互換: to_data_recursive からは生成されないため通常は到達しない
                    ("_legacy", String::new())
                }
            };
            comps_json.push_str(&format!(
                r#"{{"slot":{},"name":{},"type":"{}","enabled":{}{}}}"#,
                i,
                serde_json::to_string(&slot_data.name).unwrap_or_default(),
                type_name,
                slot_data.enabled as u8,
                extra,
            ));
        }
        comps_json.push(']');

        let name_json = serde_json::to_string(&actor.name).unwrap_or_default();
        // selected_slot_idx: Inspector 側でどのコンポーネントスロットを選択状態にするかを示す
        // transform_json は 3D: "transform":{...}、2D: "canvas_transform":{...} のいずれか
        // is_root / is_vp: ルートキャンバス判定用（HIERARCHY の is_vp と同一の分類規則）
        // active はアクター自身のフラグ（インスペクタのチェックボックス状態用。
        // 祖先の状態はヒエラルキー側の実効 active 表示が担う）
        let json = format!(
            r#"{{"id":{dfs_id},"name":{name_json},"selected_slot":{selected_slot_idx},"is_root":{},"is_vp":{},"active":{}{transform_json},"components":{comps_json}}}"#,
            is_root as u8, root_is_2d as u8, actor.active as u8,
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
                // args に .cs パスがあれば初期スクリプトパスとして設定する
                // （RequireComponent の自動追加でパス付きスロットを 1 回で作れる）。
                let init_path = args.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    // スロット専用エンティティを spawn してコンポーネントを格納する
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, PlaceholderScriptSlot {
                        script_path: init_path,
                        fields:      Default::default(),
                    });
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
                // CanvasComponent を追加する。
                // Actor3D アタッチ時は 3D ワールド向けデフォルト（640×360, auto_scale=false）を使用する。
                // Actor2D アタッチ時はスクリーンスペース向けデフォルト（1920×1080, auto_scale=true）を使用する。
                let name = slot_name.to_string();
                // アクターが 3D かどうかを事前確認する（不変参照を先に完了させてから可変操作へ移行）
                let is_actor_3d = {
                    let scene = self.scene.as_ref().unwrap();
                    let mut c = 0u32;
                    find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                        .map(|a| !a.is_2d())
                        .unwrap_or(false)
                };
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let cc = if is_actor_3d {
                        // 3D キャンバス: デフォルト解像度 640×360、自動スケール無効
                        CanvasComponent { width: 640.0, height: 360.0, auto_scale: false, ..CanvasComponent::default() }
                    } else {
                        CanvasComponent::default()
                    };
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, cc);
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
            "AudioComponent" => {
                // デフォルト（未設定）の AudioComponent をアクターに追加する。
                // 音声パス・音量・ループ等はインスペクターから後で設定する。
                use crate::engine::components::AudioComponent;
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, AudioComponent::default());
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<AudioComponent>(name, ComponentKind::Audio, slot_entity);
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
            "AnimatorComponent" => {
                // デフォルト（クリップ未設定）の AnimatorComponent をアクターに追加する。
                // クリップ一覧・既定クリップはインスペクター（P2）またはシーンファイルから設定する。
                use crate::engine::components::AnimatorComponent;
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, AnimatorComponent::default());
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<AnimatorComponent>(name, ComponentKind::Animator, slot_entity);
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
            "InputMapComponent" => {
                // デフォルト（未設定）の InputMapComponent をアクターに追加する。
                // アセットパスはインスペクターから後で設定する。
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, InputMapComponent::default());
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<InputMapComponent>(name, ComponentKind::InputMap, slot_entity);
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
            "CameraComponent" => {
                // デフォルト設定の CameraComponent をアクターに追加する。
                // FOV / near / far / is_main / clear_color はインスペクターから後で変更可能。
                // アスペクト比（target_width / target_height）の既定値は
                // プロジェクト設定のウィンドウ解像度に合わせる（取得不能時は Full HD キャッシュ値）。
                let name = slot_name.to_string();
                let (proj_w, proj_h) = self.project_resolution;
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, CameraComponent {
                        target_width:  proj_w,
                        target_height: proj_h,
                        ..CameraComponent::default()
                    });
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<CameraComponent>(name, ComponentKind::Camera, slot_entity);
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
            "ColliderComponent" => {
                // デフォルト（Box 形状）の ColliderComponent をアクターに追加する。
                // 形状・サイズ・オフセットはインスペクターから後で変更可能。
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, ColliderComponent::default());
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<ColliderComponent>(name, ComponentKind::Collider, slot_entity);
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
            "Collider2dComponent" => {
                // デフォルト（Box 形状 100×100px）の Collider2dComponent を 2D アクターに追加する。
                // 形状・サイズ・オフセットはインスペクターから後で変更可能。
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, Collider2dComponent::default());
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<Collider2dComponent>(name, ComponentKind::Collider2d, slot_entity);
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
            // "RigidbodyComponent" は ColliderComponent に統合されたため独立スロットとして追加しない
            plugin_type if plugin_type.starts_with("Plugin:") => {
                // "Plugin:{plugin_name}" フォーマット。
                // args にはプラグイン名が入る（plugin_type の後半部分と同じ）。
                use crate::engine::components::PluginComponent;
                let plugin_name = &plugin_type["Plugin:".len()..];
                let name = slot_name.to_string();
                let pc = PluginComponent::new(plugin_name);
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, pc);
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<PluginComponent>(name, ComponentKind::Plugin, slot_entity);
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
}
