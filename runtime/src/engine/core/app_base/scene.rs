use std::path::Path;
use std::sync::Arc;
use serde::{Deserialize, Serialize};

use crate::engine::core::clock::FrameContext;
use crate::engine::core::loader::{load_model, LoadError};
use crate::engine::core::scripting::{ScriptComponent, ScriptingHost, PlaceholderScriptSlot};
use crate::engine::methods::drawer::DrawContext;
use crate::engine::structs::components::{Component, ComponentData, ModelComponent};
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::{ActorData, ActorTransform};

// ============================================================
//  SceneError
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
//  DebugCameraData — シーンに付随するデバッグカメラ状態
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
            yaw:      0.0,
            pitch:    0.0,
            fov_deg:  45.0,
            far:      1000.0,
            speed:    5.0,
        }
    }
}

// ============================================================
//  SceneData — シリアライズ用
// ============================================================

#[derive(Serialize, Deserialize)]
struct SceneData {
    name:   String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    debug_camera: Option<DebugCameraData>,
    actors: Vec<ActorData>,
}

// ============================================================
//  Scene
// ============================================================

/// シーン兼ヒエラルキー。
/// ルート Actor の集合を所有し、ライフサイクルを全 Actor へ伝播する。
pub struct Scene {
    pub name:   String,
    pub actors: Vec<Actor>,
}

impl Scene {
    pub fn new(name: impl Into<String>) -> Self {
        Self { name: name.into(), actors: Vec::new() }
    }

    pub fn add_actor(&mut self, actor: Actor) {
        self.actors.push(actor);
    }

    // ─── フレームライフサイクル ──────────────────────────────

    pub fn begin_frame(&mut self, ctx: &FrameContext) {
        for a in &mut self.actors { a.begin_frame(ctx); }
    }
    pub fn early_update(&mut self, ctx: &FrameContext) {
        for a in &mut self.actors { a.early_update(ctx); }
    }
    pub fn update(&mut self, ctx: &FrameContext) {
        for a in &mut self.actors { a.update(ctx); }
    }
    pub fn constant_update(&mut self, ctx: &FrameContext) {
        for a in &mut self.actors { a.constant_update(ctx); }
    }
    pub fn late_update(&mut self, ctx: &FrameContext) {
        for a in &mut self.actors { a.late_update(ctx); }
    }
    pub fn render(&mut self, ctx: &FrameContext) {
        for a in &mut self.actors { a.render(ctx); }
    }
    pub fn end_frame(&mut self, ctx: &FrameContext) {
        for a in &mut self.actors { a.end_frame(ctx); }
    }

    // ─── コンポーネント検索（ルート Actor の浅い探索）────────

    /// 指定型のコンポーネントを持つ最初のルート Actor からコンポーネントを返す。
    pub fn find_component<T: Component + 'static>(&self) -> Option<&T> {
        self.actors.iter().find_map(|a| a.get_component::<T>())
    }

    pub fn find_component_mut<T: Component + 'static>(&mut self) -> Option<&mut T> {
        self.actors.iter_mut().find_map(|a| a.get_component_mut::<T>())
    }

    /// 指定世界線に属する Actor のみを対象にコンポーネントを検索する。
    pub fn find_component_in_world_line<T: Component + 'static>(&self, world_line: u32) -> Option<&T> {
        self.actors.iter()
            .filter(|a| a.world_line == world_line)
            .find_map(|a| a.get_component::<T>())
    }

    pub fn find_component_in_world_line_mut<T: Component + 'static>(&mut self, world_line: u32) -> Option<&mut T> {
        self.actors.iter_mut()
            .filter(|a| a.world_line == world_line)
            .find_map(|a| a.get_component_mut::<T>())
    }

    // ─── 保存 ─────────────────────────────────────────────────

    pub fn save(&self, path: &Path, camera: &DebugCameraData) -> Result<(), SceneError> {
        let data = SceneData {
            name:         self.name.clone(),
            debug_camera: Some(camera.clone()),
            actors:       self.actors.iter().map(|a| a.to_data()).collect(),
        };
        let json = serde_json::to_string_pretty(&data)?;
        std::fs::write(path, json)?;
        Ok(())
    }

    // ─── 読み込み ─────────────────────────────────────────────

    /// `.actor` ファイル（ActorData JSON）を読み込み、単一アクターのシーンを生成する。
    /// アクター編集モード（world_line=1）用。
    pub fn load_actor(
        path: &Path,
        ctx: &DrawContext,
        scripting_host: Option<&Arc<ScriptingHost>>,
    ) -> Result<Self, SceneError> {
        let raw  = std::fs::read_to_string(path)?;
        // UTF-8 BOM (\u{FEFF}) が付いている場合は取り除く
        let json = raw.strip_prefix('\u{FEFF}').unwrap_or(&raw);
        let data: ActorData = serde_json::from_str(json)?;
        let name  = data.name.clone();
        let actor = build_actor(data, ctx, scripting_host)?;
        let mut scene = Scene::new(name);
        scene.add_actor(actor);
        Ok(scene)
    }

    pub fn load(
        path: &Path,
        ctx: &DrawContext,
        scripting_host: Option<&Arc<ScriptingHost>>,
    ) -> Result<(Self, Option<DebugCameraData>), SceneError> {
        let raw  = std::fs::read_to_string(path)?;
        let json = raw.strip_prefix('\u{FEFF}').unwrap_or(&raw);
        let data: SceneData = serde_json::from_str(json)?;

        let cam = data.debug_camera;
        let mut scene = Scene::new(data.name);
        for actor_data in data.actors {
            scene.actors.push(build_actor(actor_data, ctx, scripting_host)?);
        }
        Ok((scene, cam))
    }
}

// ─── 内部ヘルパー ──────────────────────────────────────────

pub(crate) fn build_actor(
    data: ActorData,
    ctx: &DrawContext,
    scripting_host: Option<&Arc<ScriptingHost>>,
) -> Result<Actor, SceneError> {
    let mut actor = Actor::with_name(data.name);

    // トランスフォームを復元
    if let Some(tf) = data.transform {
        actor.transform = tf;
    }

    for slot in data.components {
        let slot_name = slot.name.clone();
        match slot.component {
            ComponentData::ModelComponent(mc_data) => {
                use crate::engine::structs::components::model_component::{InstanceMeta, GROUP_ID_BASE};
                if mc_data.model_path.is_empty() {
                    // 空コンポーネント（モデル未設定）
                    actor.add_component_named(slot_name, ModelComponent {
                        source_path:     String::new(),
                        model:           None,
                        gpu_model:       None,
                        instanced_batch: None,
                        instance_mats:   mc_data.instances,
                        instance_meta:   mc_data.meta,
                        group_meta:      mc_data.groups,
                        next_group_id:   mc_data.next_group_id,
                    });
                } else {
                    let path  = Path::new(&mc_data.model_path);
                    let model = load_model(path)?;
                    let total = mc_data.instances.len();
                    let gpu_model       = ctx.upload_model(&model);
                    let instanced_batch = ctx.create_instanced_batch(&model, total as u32);
                    let mut meta = mc_data.meta;
                    if meta.len() < total {
                        let start = meta.len();
                        meta.resize_with(total, || InstanceMeta::new("Instance"));
                        for i in start..total {
                            meta[i].name = format!("Instance_{i}");
                        }
                    }
                    actor.add_component_named(slot_name, ModelComponent {
                        source_path:   mc_data.model_path,
                        model:         Some(model),
                        gpu_model:     Some(gpu_model),
                        instanced_batch: Some(instanced_batch),
                        instance_mats: mc_data.instances,
                        instance_meta: meta,
                        group_meta:    mc_data.groups,
                        next_group_id: mc_data.next_group_id,
                    });
                }
            }
            ComponentData::ScriptComponent(sc_data) => {
                if let Some(host) = scripting_host {
                    if let Some(sc) = ScriptComponent::new(Arc::clone(host), sc_data.type_name) {
                        actor.add_component_named(slot_name, sc);
                    }
                } else {
                    // エディタモード（CLR なし）: PlaceholderScriptSlot として復元する
                    actor.add_component_named(slot_name, PlaceholderScriptSlot {
                        script_path: sc_data.type_name,
                    });
                }
            }
        }
    }

    for child_data in data.children {
        actor.add_child(build_actor(child_data, ctx, scripting_host)?);
    }

    Ok(actor)
}
