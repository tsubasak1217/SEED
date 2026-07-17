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

// ============================================================
//  マテリアルスロット一覧 JSON 構築（Phase R7）
// ============================================================

/// `AlphaMode` をインスペクター送信用の文字列表現に変換する。
fn alpha_mode_str(mode: crate::engine::core::loader::model::AlphaMode) -> &'static str {
    use crate::engine::core::loader::model::AlphaMode;
    match mode {
        AlphaMode::Opaque => "opaque",
        AlphaMode::Mask   => "mask",
        AlphaMode::Blend  => "blend",
    }
}

/// ModelComponent のマテリアルスロット一覧を ACTOR_COMPONENTS 用 JSON 配列文字列にする。
///
/// 各要素は `{"slot":i,"name":..,"mode":"embedded"|"mat"|"inline","base_color":[..],
/// "metallic":..,"roughness":..,"emissive":[..],"alpha_mode":"..","alpha_cutoff":..,
/// "cull_face":"back"|"front"|"none","path":".."}`。
/// - `mode` は該当スロットに `material_overrides` があるかどうかで決まる。
/// - factor/alpha 系フィールドは常に「現在の実効値」（オーバーライド適用後。無ければ glTF 埋込値）。
/// - `path` は MatAsset オーバーライドのときのみ非空。
/// - `mc` が None（モデル未ロード等）または `model` が未ロードの場合は空配列 `[]` を返す。
fn build_materials_json(mc: Option<&ModelComponent>) -> String {
    use crate::engine::components::MaterialOverrideKind;
    use crate::engine::core::renderer::material_asset;

    let Some(mc)    = mc else { return "[]".to_string() };
    let Some(model) = mc.model.as_ref() else { return "[]".to_string() };

    let mut items = Vec::with_capacity(model.materials.len());
    for (i, mat) in model.materials.iter().enumerate() {
        // 埋込値をベースラインとして用意する
        let mut base_color   = mat.base_color_factor;
        let mut metallic     = mat.metallic_factor;
        let mut roughness    = mat.roughness_factor;
        let mut emissive     = mat.emissive_factor;
        let mut alpha_mode   = alpha_mode_str(mat.alpha_mode).to_string();
        let mut alpha_cutoff = mat.alpha_cutoff;
        let mut ior          = mat.ior;
        let mut transmission = mat.transmission;
        let mut mr_tex_ignore = mat.mr_tex_ignore;
        // カリング面はインスペクタ表示・送信ともに小文字文字列（"back"|"front"|"none"）で扱う。
        let mut cull_face    = mat.cull_face.as_str().to_ascii_lowercase();
        let mut path         = String::new();
        let mut mode         = "embedded";

        if let Some(ovr) = mc.material_overrides.iter().find(|o| o.slot == i) {
            match &ovr.kind {
                MaterialOverrideKind::Inline {
                    base_color: bc, metallic: mt, roughness: rg, emissive: em,
                    alpha_mode: am, alpha_cutoff: ac, ior: ir, transmission: tr, mr_tex_ignore: mi, cull_face: cf,
                } => {
                    mode = "inline";
                    if let Some(v) = bc { base_color   = *v; }
                    if let Some(v) = mt { metallic     = *v; }
                    if let Some(v) = rg { roughness    = *v; }
                    if let Some(v) = em { emissive     = *v; }
                    if let Some(v) = am { alpha_mode   = v.clone(); }
                    if let Some(v) = ac { alpha_cutoff = *v; }
                    if let Some(v) = ir { ior          = *v; }
                    if let Some(v) = tr { transmission = *v; }
                    if let Some(v) = mi { mr_tex_ignore = *v; }
                    if let Some(v) = cf { cull_face    = v.to_ascii_lowercase(); }
                }
                MaterialOverrideKind::MatAsset { path: p } => {
                    mode = "mat";
                    path = p.clone();
                    // .mat のロードに成功すればその内容を実効値として表示する。
                    // 失敗時（ファイル無し等）は埋込値のままフォールバック表示する。
                    if let Some(asset) = material_asset::load(p) {
                        base_color   = asset.base_color;
                        metallic     = asset.metallic;
                        roughness    = asset.roughness;
                        emissive     = asset.emissive;
                        alpha_mode   = asset.alpha_mode.clone();
                        alpha_cutoff = asset.alpha_cutoff;
                        ior          = asset.ior;
                        transmission = asset.transmission;
                        mr_tex_ignore = asset.mr_tex_ignore;
                        cull_face    = asset.cull_face.to_ascii_lowercase();
                    }
                }
            }
        }

        let name_json = serde_json::to_string(&mat.name).unwrap_or_default();
        let path_json = serde_json::to_string(&path).unwrap_or_default();
        items.push(format!(
            r#"{{"slot":{i},"name":{name_json},"mode":"{mode}","base_color":[{:.4},{:.4},{:.4},{:.4}],"metallic":{:.4},"roughness":{:.4},"emissive":[{:.4},{:.4},{:.4}],"alpha_mode":"{alpha_mode}","alpha_cutoff":{:.4},"ior":{:.4},"transmission":{:.4},"mr_tex_ignore":{mr_tex_ignore},"cull_face":"{cull_face}","path":{path_json}}}"#,
            base_color[0], base_color[1], base_color[2], base_color[3],
            metallic, roughness,
            emissive[0], emissive[1], emissive[2],
            alpha_cutoff, ior, transmission,
        ));
    }
    format!("[{}]", items.join(","))
}

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
                    anim_drive:      None,
                    cast_shadows:    true,
                    material_overrides: Vec::new(),
                    batch_instance_id: crate::engine::components::next_batch_instance_id(),
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
                    // components は filter_map で構築されスロットと 1:1 対応しないため、
                    // 同一 source_path の ModelComponent をこのアクターの Model スロットから探す。
                    // アニメ一覧・マテリアル一覧（Phase R7）の両方で同じ mc を参照する。
                    let mc_opt = actor.slots().iter()
                        .filter(|s| s.kind == ComponentKind::Model)
                        .filter_map(|s| scene.world.get::<ModelComponent>(s.entity))
                        .find(|mc| mc.source_path == d.model_path);

                    // glTF モデル内蔵アニメ名一覧を添付する（C# の「内蔵アニメを追加」UI 用）。
                    // 名前は解決キー（AnimClipRef.anim）と一致させるため raw 値をそのまま送る
                    // （無名アニメは空文字。C# 側での表示・重複扱いは C# ウェーブで対応）。
                    let anims: Vec<String> = mc_opt
                        .and_then(|mc| mc.model.as_ref())
                        .map(|m| m.animations.iter().map(|a| a.name.clone()).collect())
                        .unwrap_or_default();
                    let anims_json = serde_json::to_string(&anims).unwrap_or_else(|_| "[]".to_string());

                    // マテリアルスロット一覧（Phase R7: .mat マテリアル＋マルチマテリアル編集）。
                    // 各スロットの現在の実効値（オーバーライド適用後、無ければ glTF 埋込値）を送る。
                    let materials_json = build_materials_json(mc_opt);

                    // ジョイント（ボーン）名一覧を添付する（JointAttachComponent のソケット用）。
                    // skin ジョイント名を優先し、skin が無ければ全ノード名を送る（animations 一覧と同流儀）。
                    let joints: Vec<String> = mc_opt
                        .and_then(|mc| mc.model.as_ref())
                        .map(|m| super::jointattach_ops::model_joint_names(m))
                        .unwrap_or_default();
                    let joints_json = serde_json::to_string(&joints).unwrap_or_else(|_| "[]".to_string());

                    // 影を落とすかをインスペクター用に送信する（LightComponent.cast_shadows と同一慣例）
                    ("ModelComponent", format!(
                        r#","model_path":{path_json},"animations":{anims_json},"materials":{materials_json},"joints":{joints_json},"cast_shadows":{}"#,
                        d.cast_shadows as u8,
                    ))
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
                    // テクスチャパス・カラー・サイズ・レイヤー・ポストエフェクト参照をインスペクター用に送信する
                    let path_json   = serde_json::to_string(&d.texture_path).unwrap_or_default();
                    let postfx_json = serde_json::to_string(&d.postfx_path).unwrap_or_default();
                    ("SpriteComponent", format!(
                        r#","texture_path":{path_json},"cr":{:.4},"cg":{:.4},"cb":{:.4},"ca":{:.4},"sprite_w":{:.4},"sprite_h":{:.4},"layer":{},"postfx_path":{postfx_json}"#,
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
                    // アニメーター: クリップ一覧（name/path）・既定クリップ・自動再生・速度を
                    // インスペクター／タイムラインパネル用に送信する（P2 でクリップ編集 UI が消費する）。
                    // clips は AnimClipRef の配列（[{"name":"..","path":".."},...]）としてそのまま JSON 化する。
                    let clips_json         = serde_json::to_string(&d.clips).unwrap_or_else(|_| "[]".to_string());
                    let default_clip_json  = serde_json::to_string(&d.default_clip).unwrap_or_default();
                    ("AnimatorComponent", format!(
                        r#","clips":{clips_json},"default_clip":{default_clip_json},"play_on_start":{},"speed":{:.4}"#,
                        d.play_on_start as u8, d.speed,
                    ))
                }
                ComponentData::LightComponent(d) => {
                    // ライト: 種別・色・強度・range・スポット内外角・rect サイズ・影フラグを
                    // インスペクター用に送信する（種別ごとに関連フィールドのみ UI 側で表示する）。
                    ("LightComponent", format!(
                        r#","kind":"{}","lr":{:.4},"lg":{:.4},"lb":{:.4},"intensity":{:.4},"range":{:.4},"inner_angle":{:.4},"outer_angle":{:.4},"rect_width":{:.4},"rect_height":{:.4},"soft_radius":{:.4},"bounce_intensity":{:.4},"cast_shadows":{}"#,
                        d.kind.as_str(),
                        d.color[0], d.color[1], d.color[2],
                        d.intensity, d.range,
                        d.inner_angle_deg, d.outer_angle_deg,
                        d.rect_width, d.rect_height,
                        d.soft_radius,
                        d.bounce_intensity,
                        d.cast_shadows as u8,
                    ))
                }
                ComponentData::JointAttachComponent(d) => {
                    // ジョイントアタッチ: ジョイント名・位置/回転/スケールオフセットに加え、
                    // ターゲットモデル（祖先アクターの最初の Model スロット）のジョイント名一覧を
                    // 添付する（インスペクタのドロップダウン用。モデル無しは空配列）。
                    let joint_json  = serde_json::to_string(&d.joint_name).unwrap_or_default();
                    let joints      = super::jointattach_ops::collect_target_model_joints(
                        &scene.actors, &scene.world, wl, dfs_id);
                    let joints_json = serde_json::to_string(&joints).unwrap_or_else(|_| "[]".to_string());
                    ("JointAttachComponent", format!(
                        r#","joint_name":{joint_json},"joints":{joints_json},"offset_px":{:.4},"offset_py":{:.4},"offset_pz":{:.4},"offset_ex":{:.4},"offset_ey":{:.4},"offset_ez":{:.4},"offset_sx":{:.4},"offset_sy":{:.4},"offset_sz":{:.4}"#,
                        d.offset_pos[0], d.offset_pos[1], d.offset_pos[2],
                        d.offset_rot_deg[0], d.offset_rot_deg[1], d.offset_rot_deg[2],
                        d.offset_scale[0], d.offset_scale[1], d.offset_scale[2],
                    ))
                }
                ComponentData::ParticleEmitterComponent(d) => {
                    // パーティクルエミッタ: 形状・出現範囲・放出制御・寿命・初速・方向・重力・
                    // 回転・サイズ・カーブ・テクスチャ・ブレンド・シミュレーション空間・
                    // 再生フラグをインスペクター用に送信する。
                    // texture_path / shape_model_path は audio_path と同流儀で JSON 文字列と
                    // してエスケープする。カーブは ParamCurve の JSON をそのまま埋め込む
                    // （外側 JSON の値としてオブジェクト/配列を直接使う。二重クォートしない）。
                    let paths_json      = serde_json::to_string(&d.texture_paths).unwrap_or_else(|_| "[]".to_string());
                    let model_path_json = serde_json::to_string(d.shape.model_path()).unwrap_or_default();
                    let box_half        = d.spawn_volume.box_half_extents();
                    let sphere_radius   = d.spawn_volume.sphere_radius();
                    let speed_curve_json     = serde_json::to_string(&d.speed_curve).unwrap_or_else(|_| "{}".to_string());
                    let rot_speed_curve_json = serde_json::to_string(&d.rot_speed_curve).unwrap_or_else(|_| "{}".to_string());
                    let scale_curve_json     = serde_json::to_string(&d.scale_curve).unwrap_or_else(|_| "{}".to_string());
                    let color_curves_json    = serde_json::to_string(&d.color_curves).unwrap_or_else(|_| "[]".to_string());
                    ("ParticleEmitterComponent", format!(
                        r#","max_particles":{},"shape":"{}","shape_model_path":{model_path_json},"spawn_volume":"{}","spawn_box_x":{:.4},"spawn_box_y":{:.4},"spawn_box_z":{:.4},"spawn_sphere_radius":{:.4},"emit_mode":"{}","emit_count_total":{},"initial_delay":{:.4},"prewarm_time":{:.4},"emit_interval":{:.4},"particles_per_emit":{},"lifetime_min":{:.4},"lifetime_max":{:.4},"speed_min":{:.4},"speed_max":{:.4},"dir_x":{:.4},"dir_y":{:.4},"dir_z":{:.4},"direction_randomness":{:.4},"gravity_x":{:.4},"gravity_y":{:.4},"gravity_z":{:.4},"drag":{:.4},"rot_speed_min":{:.4},"rot_speed_max":{:.4},"initial_rot_min":{:.4},"initial_rot_max":{:.4},"size_min":{:.4},"size_max":{:.4},"texture_paths":{paths_json},"blend":"{}","sim_space":"{}","playing":{},"speed_curve":{speed_curve_json},"rot_speed_curve":{rot_speed_curve_json},"scale_curve":{scale_curve_json},"color_curves":{color_curves_json}"#,
                        d.max_particles,
                        d.shape.as_str(),
                        d.spawn_volume.as_str(),
                        box_half[0], box_half[1], box_half[2],
                        sphere_radius,
                        d.emit_mode.as_str(), d.emit_mode.count_total(),
                        d.initial_delay, d.prewarm_time, d.emit_interval, d.particles_per_emit,
                        d.lifetime[0], d.lifetime[1],
                        d.initial_speed[0], d.initial_speed[1],
                        d.direction_local[0], d.direction_local[1], d.direction_local[2],
                        d.direction_randomness,
                        d.gravity[0], d.gravity[1], d.gravity[2],
                        d.drag,
                        d.rot_speed_range[0], d.rot_speed_range[1],
                        d.initial_rotation_range[0], d.initial_rotation_range[1],
                        d.size_range[0], d.size_range[1],
                        d.blend.as_str(), d.sim_space.as_str(),
                        d.playing as u8,
                    ))
                }
                ComponentData::SkyboxComponent(d) => {
                    // スカイボックス: テクスチャ参照・配置モード・強度・色味をインスペクター用に送信する。
                    // texture_path は JSON 文字列としてエスケープする（audio_path / model_path と同流儀）。
                    let path_json = serde_json::to_string(&d.texture_path).unwrap_or_else(|_| "\"\"".to_string());
                    ("SkyboxComponent", format!(
                        r#","texture_path":{path_json},"mode":"{}","intensity":{:.4},"tr":{:.4},"tg":{:.4},"tb":{:.4}"#,
                        d.mode.as_str(),
                        d.intensity,
                        d.tint[0], d.tint[1], d.tint[2],
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
        // プレハブ参照リンク（あれば assets:// パス、無ければ null）を Inspector 上部の
        // 参照表示・リンク解除 UI 用に添付する（C# パースは次ウェーブ）。
        let prefab_source_json = match &actor.prefab_source {
            Some(p) => serde_json::to_string(p).unwrap_or_else(|_| "null".to_string()),
            None    => "null".to_string(),
        };
        // selected_slot_idx: Inspector 側でどのコンポーネントスロットを選択状態にするかを示す
        // transform_json は 3D: "transform":{...}、2D: "canvas_transform":{...} のいずれか
        // is_root / is_vp: ルートキャンバス判定用（HIERARCHY の is_vp と同一の分類規則）
        // active はアクター自身のフラグ（インスペクタのチェックボックス状態用。
        // 祖先の状態はヒエラルキー側の実効 active 表示が担う）
        // prefab_source: プレハブ参照リンク（null = リンクなし）
        let json = format!(
            r#"{{"id":{dfs_id},"name":{name_json},"selected_slot":{selected_slot_idx},"is_root":{},"is_vp":{},"active":{},"prefab_source":{prefab_source_json}{transform_json},"components":{comps_json}}}"#,
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
                        anim_drive:      None,
                        cast_shadows:    true,
                        material_overrides: Vec::new(),
                        batch_instance_id: crate::engine::components::next_batch_instance_id(),
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
            "LightComponent" => {
                // デフォルト（directional・白・強度3）の LightComponent をアクターに追加する。
                // 種別・色・強度などはインスペクターまたはシーンファイルから設定する。
                use crate::engine::components::LightComponent;
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, LightComponent::default());
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<LightComponent>(name, ComponentKind::Light, slot_entity);
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
            "JointAttachComponent" => {
                // デフォルト（joint_name 空＝無効・オフセットなし）の JointAttachComponent を追加する。
                // ジョイント名・オフセットはインスペクターまたはシーンファイルから設定する。
                use crate::engine::components::JointAttachComponent;
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, JointAttachComponent::default());
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<JointAttachComponent>(name, ComponentKind::JointAttach, slot_entity);
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
            "ParticleEmitterComponent" => {
                // デフォルト設定の ParticleEmitterComponent をアクターに追加する。
                // 放出パラメータ・色・テクスチャなどはインスペクターまたはシーンファイルから設定する。
                use crate::engine::components::ParticleEmitterComponent;
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, ParticleEmitterComponent::default());
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<ParticleEmitterComponent>(name, ComponentKind::ParticleEmitter, slot_entity);
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
            "SkyboxComponent" => {
                // デフォルト（テクスチャ未設定・CameraLocked）の SkyboxComponent をアクターに追加する。
                // テクスチャ参照・モード・強度・色味はインスペクターまたはシーンファイルから設定する。
                use crate::engine::components::SkyboxComponent;
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, SkyboxComponent::default());
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<SkyboxComponent>(name, ComponentKind::Skybox, slot_entity);
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
