// ============================================================
//  scene.rs — シーン（World のオーナー + アクターツリー管理）
//
//  【設計】
//  Scene は ECS の World を所有し、Actor ツリー（ルートのリスト）を管理する。
//  コンポーネントの実データは scene.world に格納される。
//  Actor はツリー構造（DFS 順）を保持し、world_line フィルタリングを担う。
//
//  重要な分離:
//  - scene.world  : コンポーネントデータ（SparseSet）
//  - scene.actors : ツリー順序・ヒエラルキー情報（Actor の Vec）
//
//  両者は Actor.entity をキーで連携する。
// ============================================================

use std::path::Path;
use std::sync::Arc;
use serde::{Deserialize, Serialize};

use crate::engine::ecs::{Entity, World};
use crate::engine::core::clock::FrameContext;
use crate::engine::core::loader::{load_model, LoadError};
use crate::engine::core::scripting::ScriptingHost;
use crate::engine::methods::drawer::DrawContext;
use crate::engine::components::{
    ComponentData, ComponentKind,
    Transform,
    ModelComponent, ModelComponentData, InstanceMeta, GROUP_ID_BASE,
    ScriptComponent, PlaceholderScriptSlot,
    CameraComponent, CameraComponentData,
};
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::{ActorData, ComponentSlotData};

// ============================================================
//  SceneError — シーン読み書き時のエラー型
// ============================================================

#[derive(Debug)]
pub enum SceneError {
    Io(std::io::Error),
    Json(serde_json::Error),
    Load(LoadError),
}

impl std::fmt::Display for SceneError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            SceneError::Io(e)   => write!(f, "IO error: {e}"),
            SceneError::Json(e) => write!(f, "JSON error: {e}"),
            SceneError::Load(e) => write!(f, "Load error: {e}"),
        }
    }
}

impl std::error::Error for SceneError {}
impl From<std::io::Error>    for SceneError { fn from(e: std::io::Error)    -> Self { Self::Io(e) } }
impl From<serde_json::Error> for SceneError { fn from(e: serde_json::Error) -> Self { Self::Json(e) } }
impl From<LoadError>         for SceneError { fn from(e: LoadError)          -> Self { Self::Load(e) } }

// ============================================================
//  DebugCameraData — デバッグカメラの保存データ
// ============================================================

#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct DebugCameraData {
    pub position: [f32; 3],
    pub yaw:      f32,
    pub pitch:    f32,
    pub fov_deg:  f32,
    pub far:      f32,
    pub speed:    f32,
}

impl Default for DebugCameraData {
    fn default() -> Self {
        Self {
            position: [0.0, 2.0, -10.0],
            yaw: 0.0, pitch: 0.0, fov_deg: 45.0, far: 1000.0, speed: 5.0,
        }
    }
}

// ============================================================
//  CanvasCameraData — 2D アクター編集カメラの保存データ
// ============================================================

/// 2D アクター編集モード用カメラの保存データ。
///
/// XY 平面を正射影で見るカメラ。RMB ドラッグでパン、スクロールでズーム。
#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct CanvasCameraData {
    /// カメラの XY パン量（ワールドユニット）
    pub pan_x:       f32,
    pub pan_y:       f32,
    /// 垂直方向に見える範囲の半分（ワールドユニット）。小さいほどズームイン。
    pub ortho_half_h: f32,
}

impl Default for CanvasCameraData {
    fn default() -> Self {
        Self { pan_x: 0.0, pan_y: 0.0, ortho_half_h: 10.0 }
    }
}

// ============================================================
//  SceneData — シーンファイルのデシリアライズ用内部型
// ============================================================

#[derive(Serialize, Deserialize)]
struct SceneData {
    name:   String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    debug_camera: Option<DebugCameraData>,
    actors: Vec<ActorData>,
}

// ============================================================
//  Scene — World のオーナー + アクターツリー管理
// ============================================================

/// シーン本体。World（コンポーネントデータ）と Actor ツリーを所有する。
pub struct Scene {
    pub name:   String,
    /// ECS コンポーネントストア。全 Actor のコンポーネントデータを格納する。
    pub world:  World,
    /// ルート Actor のリスト（順序を保持し DFS ID の計算に使う）。
    pub actors: Vec<Actor>,
}

impl Scene {
    pub fn new(name: impl Into<String>) -> Self {
        Self { name: name.into(), world: World::new(), actors: Vec::new() }
    }

    pub fn add_actor(&mut self, actor: Actor) {
        self.actors.push(actor);
    }

    // ── World へのショートハンドアクセス ─────────────────────

    /// Entity の Transform コンポーネントへの不変参照を返す。
    pub fn transform(&self, entity: Entity) -> Option<&Transform> {
        self.world.get::<Transform>(entity)
    }

    /// Entity の Transform コンポーネントへの可変参照を返す。
    pub fn transform_mut(&mut self, entity: Entity) -> Option<&mut Transform> {
        self.world.get_mut::<Transform>(entity)
    }

    /// Entity の指定コンポーネントへの不変参照を返す。
    pub fn get<T: crate::engine::ecs::Component>(&self, entity: Entity) -> Option<&T> {
        self.world.get::<T>(entity)
    }

    /// Entity の指定コンポーネントへの可変参照を返す。
    pub fn get_mut<T: crate::engine::ecs::Component>(&mut self, entity: Entity) -> Option<&mut T> {
        self.world.get_mut::<T>(entity)
    }

    // ── 検索ヘルパー ──────────────────────────────────────────

    /// Play モード用のメインカメラを DFS で探す。
    ///
    /// `is_main = true` の CameraComponent を持つ最初の Actor の
    /// (Transform, CameraComponentData) を返す。
    /// シーン内にメインカメラが存在しない場合は None を返す。
    pub fn find_main_camera(&self) -> Option<(Transform, CameraComponentData)> {
        fn search(
            actor: &Actor,
            world: &World,
        ) -> Option<(Transform, CameraComponentData)> {
            // このアクターの Camera スロットを確認する
            for slot in actor.slots() {
                if slot.kind == ComponentKind::Camera {
                    if let Some(cc) = world.get::<CameraComponent>(slot.entity) {
                        if cc.is_main {
                            // Actor 本体の Transform を取得する
                            if let Some(tf) = world.get::<Transform>(actor.entity) {
                                return Some((tf.clone(), cc.to_data()));
                            }
                        }
                    }
                }
            }
            // 子アクターを再帰探索する
            for child in actor.children() {
                if let Some(result) = search(child, world) {
                    return Some(result);
                }
            }
            None
        }

        for actor in &self.actors {
            if let Some(result) = search(actor, &self.world) {
                return Some(result);
            }
        }
        None
    }

    /// 指定 world_line の ModelComponent を持つ最初のスロットの
    /// (Entity, &ModelComponent) を返す。
    /// スロット専用 entity からコンポーネントを検索する。
    pub fn find_model_in_world_line(&self, wl: u32) -> Option<(Entity, &ModelComponent)> {
        for root in self.actors.iter().filter(|a| a.world_line == wl) {
            for slot in root.slots() {
                if let Some(mc) = self.world.get::<ModelComponent>(slot.entity) {
                    return Some((slot.entity, mc));
                }
            }
        }
        None
    }

    /// 指定 world_line の ModelComponent を持つ最初のスロットの
    /// (Entity, &mut ModelComponent) を返す。
    pub fn find_model_in_world_line_mut(&mut self, wl: u32) -> Option<(Entity, &mut ModelComponent)> {
        // borrow checker 対策: スロット entity を先に取得し world を別借用する
        let slot_entity = {
            let mut found = None;
            'outer: for root in self.actors.iter().filter(|a| a.world_line == wl) {
                for slot in root.slots() {
                    if self.world.contains::<ModelComponent>(slot.entity) {
                        found = Some(slot.entity);
                        break 'outer;
                    }
                }
            }
            found?
        };
        self.world.get_mut::<ModelComponent>(slot_entity).map(|mc| (slot_entity, mc))
    }

    /// 後方互換 API: world_line 内の最初の T コンポーネント（不変）を返す。
    /// スロット専用 entity からコンポーネントを検索する。
    pub fn find_component_in_world_line<T: crate::engine::ecs::Component>(&self, wl: u32) -> Option<&T> {
        for root in self.actors.iter().filter(|a| a.world_line == wl) {
            for slot in root.slots() {
                if let Some(c) = self.world.get::<T>(slot.entity) {
                    return Some(c);
                }
            }
        }
        None
    }

    /// 後方互換 API: world_line 内の最初の T コンポーネント（可変）を返す。
    /// スロット専用 entity からコンポーネントを検索する。
    pub fn find_component_in_world_line_mut<T: crate::engine::ecs::Component>(&mut self, wl: u32) -> Option<&mut T> {
        // borrow checker 対策: スロット entity を先に特定してから world を可変借用する
        let slot_entity = {
            let mut found = None;
            'outer: for root in self.actors.iter().filter(|a| a.world_line == wl) {
                for slot in root.slots() {
                    if self.world.contains::<T>(slot.entity) {
                        found = Some(slot.entity);
                        break 'outer;
                    }
                }
            }
            found?
        };
        self.world.get_mut::<T>(slot_entity)
    }

    // ── フレームライフサイクル（System 移行前の暫定）───────────

    pub fn begin_frame(&self, _ctx: &FrameContext) {}
    pub fn early_update(&self, _ctx: &FrameContext) {}
    pub fn update(&self, _ctx: &FrameContext) {}
    pub fn constant_update(&self, _ctx: &FrameContext) {}
    pub fn late_update(&self, _ctx: &FrameContext) {}
    pub fn render(&self, _ctx: &FrameContext) {}
    pub fn end_frame(&self, _ctx: &FrameContext) {}

    // ── 保存 ──────────────────────────────────────────────────

    pub fn save(&self, path: &Path, camera: &DebugCameraData) -> Result<(), SceneError> {
        let data = SceneData {
            name:         self.name.clone(),
            debug_camera: Some(camera.clone()),
            actors:       self.actors.iter().map(|a| a.to_data(&self.world)).collect(),
        };
        let json = serde_json::to_string_pretty(&data)?;
        std::fs::write(path, json)?;
        Ok(())
    }

    // ── 読み込み ──────────────────────────────────────────────

    /// `.actor` ファイル（ActorData JSON）を読み込み、単一アクターのシーンを生成する。
    pub fn load_actor(
        path:           &Path,
        ctx:            &DrawContext,
        scripting_host: Option<&Arc<ScriptingHost>>,
    ) -> Result<Self, SceneError> {
        let raw  = crate::engine::asset_fs::read_string(path.to_str().unwrap_or(""))?;
        let json = raw.strip_prefix('\u{FEFF}').unwrap_or(&raw);
        let data: ActorData = serde_json::from_str(json)?;
        let name = data.name.clone();
        let mut scene = Scene::new(name);
        let actor = build_actor(data, ctx, &mut scene.world, scripting_host)?;
        scene.add_actor(actor);
        Ok(scene)
    }

    /// `.actor` ファイルを既存の World に直接ロードし、Actor を返す。
    ///
    /// `load_actor` と異なり独自 World を作らないため、エンティティが
    /// main_scene.world に直接登録され、再オープン後もコンポーネントが正しく参照される。
    /// world_line は Actor と全子孫に再帰的に設定される。
    pub fn load_actor_into(
        path:           &Path,
        ctx:            &DrawContext,
        world:          &mut World,
        scripting_host: Option<&Arc<ScriptingHost>>,
        world_line:     u32,
    ) -> Result<Actor, SceneError> {
        let raw  = crate::engine::asset_fs::read_string(path.to_str().unwrap_or(""))?;
        let json = raw.strip_prefix('\u{FEFF}').unwrap_or(&raw);
        let data: ActorData = serde_json::from_str(json)?;
        let mut actor = build_actor(data, ctx, world, scripting_host)?;
        // world_line を自身と全子孫へ伝播する
        actor.set_world_line_recursive(world_line);
        Ok(actor)
    }

    pub fn load(
        path:           &Path,
        ctx:            &DrawContext,
        scripting_host: Option<&Arc<ScriptingHost>>,
    ) -> Result<(Self, Option<DebugCameraData>), SceneError> {
        let raw  = crate::engine::asset_fs::read_string(path.to_str().unwrap_or(""))?;
        let json = raw.strip_prefix('\u{FEFF}').unwrap_or(&raw);
        let data: SceneData = serde_json::from_str(json)?;

        let cam = data.debug_camera;
        let mut scene = Scene::new(data.name);
        for actor_data in data.actors {
            let actor = build_actor(actor_data, ctx, &mut scene.world, scripting_host)?;
            scene.actors.push(actor);
        }
        Ok((scene, cam))
    }
}

// ============================================================
//  build_actor — ActorData → Actor 構築
// ============================================================

/// ActorData から Actor を構築し、コンポーネントを World に挿入する。
pub fn build_actor(
    data:           ActorData,
    ctx:            &DrawContext,
    world:          &mut World,
    scripting_host: Option<&Arc<ScriptingHost>>,
) -> Result<Actor, SceneError> {
    use crate::engine::components::CanvasTransform;
    use crate::engine::structs::objects::actor::ActorKind;

    let entity = world.spawn();

    // actor_kind に応じてデフォルトトランスフォームを挿入する。
    // Actor3D → Transform（3D ワールド空間）
    // Actor2D → CanvasTransform（XY キャンバス空間）+ Transform（ダミーとして挿入しない）
    match data.actor_kind {
        ActorKind::Actor3D => {
            world.insert(entity, data.transform.unwrap_or_default());
        }
        ActorKind::Actor2D => {
            // 保存済み canvas_transform があれば復元（pivot/anchor を含む）。
            // 旧フォーマット（canvas_transform フィールドなし）との互換のためデフォルトにフォールバック。
            world.insert(entity, data.canvas_transform.unwrap_or_default());
        }
    }

    let mut actor = Actor::new(entity, data.name);
    actor.actor_kind = data.actor_kind;

    for slot in data.components {
        let slot_name = slot.name.clone();
        // スロットごとに専用エンティティを spawn してコンポーネントを格納する。
        // これにより同型コンポーネントを複数スロット持っても互いに干渉しない。
        let slot_entity = world.spawn();
        match slot.component {
            ComponentData::ModelComponent(mc_data) => {
                use std::path::Path;
                if mc_data.model_path.is_empty() {
                    // モデル未設定の空コンポーネント
                    let meta = mc_data.meta;
                    world.insert(slot_entity, ModelComponent {
                        source_path:     String::new(),
                        model:           None,
                        gpu_model:       None,
                        instanced_batch: None,
                        instance_mats:   mc_data.instances,
                        instance_meta:   meta,
                        group_meta:      mc_data.groups,
                        next_group_id:   mc_data.next_group_id,
                    });
                } else {
                    use std::sync::Arc;
                    let path  = Path::new(&mc_data.model_path);
                    // キャッシュから CPU モデルを取得するか、ディスクから読み込んでキャッシュに追加する
                    let model: Arc<crate::engine::core::loader::model::Model> = {
                        let mut cache = ctx.model_cache.borrow_mut();
                        if let Some(cached) = cache.get(&mc_data.model_path) {
                            Arc::clone(cached)
                        } else {
                            let m = Arc::new(load_model(path)?);
                            cache.insert(mc_data.model_path.clone(), Arc::clone(&m));
                            m
                        }
                    };
                    let total = mc_data.instances.len();
                    let gpu_model       = ctx.upload_model(&*model);
                    let instanced_batch = ctx.create_instanced_batch(&*model, total as u32);
                    let mut meta = mc_data.meta;
                    if meta.len() < total {
                        let start = meta.len();
                        meta.resize_with(total, || InstanceMeta::new("Instance"));
                        for i in start..total { meta[i].name = format!("Instance_{i}"); }
                    }
                    world.insert(slot_entity, ModelComponent {
                        source_path:     mc_data.model_path,
                        model:           Some(model),
                        gpu_model:       Some(gpu_model),
                        instanced_batch: Some(instanced_batch),
                        instance_mats:   mc_data.instances,
                        instance_meta:   meta,
                        group_meta:      mc_data.groups,
                        next_group_id:   mc_data.next_group_id,
                    });
                }
                actor.add_slot_typed::<ModelComponent>(slot_name, ComponentKind::Model, slot_entity);
            }
            ComponentData::ScriptComponent(sc_data) => {
                if let Some(host) = scripting_host {
                    if let Some(sc) = ScriptComponent::new(Arc::clone(host), sc_data.type_name.clone()) {
                        world.insert(slot_entity, sc);
                        actor.add_slot_typed::<ScriptComponent>(slot_name, ComponentKind::Script, slot_entity);
                    } else {
                        world.insert(slot_entity, PlaceholderScriptSlot { script_path: sc_data.type_name });
                        actor.add_slot_typed::<PlaceholderScriptSlot>(slot_name, ComponentKind::Placeholder, slot_entity);
                    }
                } else {
                    world.insert(slot_entity, PlaceholderScriptSlot { script_path: sc_data.type_name });
                    actor.add_slot_typed::<PlaceholderScriptSlot>(slot_name, ComponentKind::Placeholder, slot_entity);
                }
            }
            ComponentData::CanvasComponent(cc_data) => {
                use crate::engine::components::CanvasComponent;
                world.insert(slot_entity, CanvasComponent {
                    width:           cc_data.width,
                    height:          cc_data.height,
                    scale_size:      cc_data.scale_size,
                    scale_transform: cc_data.scale_transform,
                    auto_scale:      cc_data.auto_scale,
                });
                actor.add_slot_typed::<CanvasComponent>(slot_name, ComponentKind::Canvas, slot_entity);
            }
            ComponentData::SpriteComponent(sc_data) => {
                use crate::engine::components::SpriteComponent;
                world.insert(slot_entity, SpriteComponent {
                    texture_path: sc_data.texture_path,
                    color:        sc_data.color,
                    width:        sc_data.width,
                    height:       sc_data.height,
                });
                actor.add_slot_typed::<SpriteComponent>(slot_name, ComponentKind::Sprite, slot_entity);
            }
            ComponentData::InputMapComponent(ic_data) => {
                use crate::engine::components::InputMapComponent;
                world.insert(slot_entity, InputMapComponent { asset_path: ic_data.asset_path });
                actor.add_slot_typed::<InputMapComponent>(slot_name, ComponentKind::InputMap, slot_entity);
            }
            ComponentData::CameraComponent(cc_data) => {
                world.insert(slot_entity, CameraComponent::from_data(cc_data));
                actor.add_slot_typed::<CameraComponent>(slot_name, ComponentKind::Camera, slot_entity);
            }
        }
    }

    for child_data in data.children {
        actor.add_child(build_actor(child_data, ctx, world, scripting_host)?);
    }

    Ok(actor)
}
