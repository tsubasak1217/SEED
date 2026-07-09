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

use crate::engine::ecs::{Entity, World, Phase, Schedule};
use crate::engine::core::clock::FrameContext;
use crate::engine::core::loader::{load_model, LoadError};
use crate::engine::core::scripting::ScriptingHost;
use crate::engine::methods::drawer::DrawContext;
use crate::engine::components::{
    ComponentData, ComponentKind,
    Transform,
    ModelComponent, InstanceMeta,
    ScriptComponent, PlaceholderScriptSlot,
    CameraComponent, CameraComponentData,
};
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::ActorData;

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
    /// ECS システムスケジューラ。フレームの各フェーズで run_phase() から実行される。
    /// エンジン標準システム（スクリプト駆動など）は Scene::new で登録される。
    pub schedule: Schedule,
}

impl Scene {
    pub fn new(name: impl Into<String>) -> Self {
        // エンジン標準の ECS システム（ScriptSystem 等）を登録した Schedule を構築する
        let mut schedule = Schedule::new();
        crate::engine::systems::register_default_systems(&mut schedule);
        Self { name: name.into(), world: World::new(), actors: Vec::new(), schedule }
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

    // ── フレームライフサイクル ─────────────────────────────────

    /// 指定フェーズの ECS システム群を World に対して実行する。
    /// frame_renderer のゲームロジックブロック（Play・非ポーズ時）から
    /// BeginFrame → EarlyUpdate → Update → ConstantUpdate(固定ステップ×N)
    /// → LateUpdate → Render → EndFrame の順で呼ばれる。
    pub fn run_phase(&mut self, phase: Phase, ctx: &FrameContext) {
        // スクリプト実行の前に、各 ScriptComponent へ所有 Actor（Entity）を同期する。
        // フレーム先頭（BeginFrame）で 1 回行えば、以降のフェーズでも保持される。
        // これによりスクリプトの gameObject/transform が自分のオブジェクトを指す。
        if matches!(phase, Phase::BeginFrame) {
            Self::sync_script_owners(&self.actors, &mut self.world);
        }
        // Actor ツリーの読み取り専用ポインタを公開しながらフェーズを実行する。
        // スクリプトの Find（名前検索）が Actor 名を参照できるようにするため。
        // actors と world は別フィールドなので分割借用で競合しない。
        let Self { actors, world, schedule, .. } = self;
        crate::engine::core::scripting::with_actors(actors, || {
            schedule.run_phase(phase, world, ctx);
        });
    }

    /// Actor ツリーを走査し、各スクリプトスロットの ScriptComponent に所有 Actor の
    /// Entity と実効アクティブフラグを書き込む。ScriptComponent はスロット専用 entity に
    /// 格納されており、それ自身は所有 Actor を知らないため、ここで橋渡しする。
    ///
    /// 実効アクティブ = 自身と全祖先の active が true かつ スロットの enabled が true。
    /// false のスクリプトは script_system がライフサイクル呼び出しをスキップする。
    fn sync_script_owners(
        actors: &[crate::engine::structs::objects::Actor],
        world:  &mut crate::engine::ecs::World,
    ) {
        use crate::engine::components::{ComponentKind, ScriptComponent};

        fn walk(
            actor:         &crate::engine::structs::objects::Actor,
            world:         &mut crate::engine::ecs::World,
            parent_active: bool,
        ) {
            let active = parent_active && actor.active;
            for slot in actor.slots() {
                if slot.kind == ComponentKind::Script {
                    if let Some(sc) = world.get_mut::<ScriptComponent>(slot.entity) {
                        sc.owner  = Some(actor.entity);
                        sc.active = active && slot.enabled;
                    }
                }
            }
            for child in actor.children() {
                walk(child, world, active);
            }
        }

        for actor in actors {
            walk(actor, world, true);
        }
    }

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
        let actor = build_actor(data, ctx, &mut scene.world, scripting_host, None)?;
        scene.add_actor(actor);
        Ok(scene)
    }

    /// `.actor` ファイルを既存の World に直接ロードし、Actor を返す。
    ///
    /// `load_actor` と異なり独自 World を作らないため、エンティティが
    /// main_scene.world に直接登録され、再オープン後もコンポーネントが正しく参照される。
    /// world_line は Actor と全子孫に再帰的に設定される。
    /// `root_entity` に Some を渡すと、ルートを予約済みエンティティで構築する
    /// （スクリプトの Instantiate 用。詳細は build_actor を参照）。
    pub fn load_actor_into(
        path:           &Path,
        ctx:            &DrawContext,
        world:          &mut World,
        scripting_host: Option<&Arc<ScriptingHost>>,
        world_line:     u32,
        root_entity:    Option<Entity>,
    ) -> Result<Actor, SceneError> {
        let raw  = crate::engine::asset_fs::read_string(path.to_str().unwrap_or(""))?;
        let json = raw.strip_prefix('\u{FEFF}').unwrap_or(&raw);
        let data: ActorData = serde_json::from_str(json)?;
        let mut actor = build_actor(data, ctx, world, scripting_host, root_entity)?;
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
            let actor = build_actor(actor_data, ctx, &mut scene.world, scripting_host, None)?;
            scene.actors.push(actor);
        }
        Ok((scene, cam))
    }
}

// ============================================================
//  build_actor — ActorData → Actor 構築
// ============================================================

/// ActorData から Actor を構築し、コンポーネントを World に挿入する。
///
/// `root_entity` に Some を渡すと、ルートの entity を新規 spawn せずその予約済み
/// エンティティを使う（スクリプトの Instantiate 用。予約時に挿入済みの Transform を
/// スクリプトが設定した値として優先する）。子アクターには影響しない。
pub fn build_actor(
    data:           ActorData,
    ctx:            &DrawContext,
    world:          &mut World,
    scripting_host: Option<&Arc<ScriptingHost>>,
    root_entity:    Option<Entity>,
) -> Result<Actor, SceneError> {
    use crate::engine::structs::objects::actor::ActorKind;
    use crate::engine::components::Transform;

    // 予約済みルートがあればそれを使い、なければ新規 spawn する
    let reused = root_entity.is_some();
    let entity = root_entity.unwrap_or_else(|| world.spawn());

    // actor_kind に応じてデフォルトトランスフォームを挿入する。
    // Actor3D → Transform（3D ワールド空間）
    // Actor2D → CanvasTransform（XY キャンバス空間）+ Transform（ダミーとして挿入しない）
    match data.actor_kind {
        ActorKind::Actor3D => {
            // 予約済みルートに既に Transform がある場合（Instantiate 直後にスクリプトが
            // Position を設定済み）はその値を優先し、アクターファイルの値で上書きしない。
            if !(reused && world.contains::<Transform>(entity)) {
                world.insert(entity, data.transform.unwrap_or_default());
            }
        }
        ActorKind::Actor2D => {
            // 予約時に仮挿入された 3D Transform は 2D アクターには不要なので取り除く
            if reused {
                world.remove::<Transform>(entity);
            }
            // 保存済み canvas_transform があれば復元（pivot/anchor を含む）。
            // 旧フォーマット（canvas_transform フィールドなし）との互換のためデフォルトにフォールバック。
            world.insert(entity, data.canvas_transform.unwrap_or_default());
        }
    }

    let mut actor = Actor::new(entity, data.name);
    actor.actor_kind = data.actor_kind;
    // アクティブフラグを復元する（省略時は serde デフォルトで true）
    actor.active = data.active;

    for slot in data.components {
        let slot_name = slot.name.clone();
        // このスロットの有効フラグ（match 後に追加されたスロットへ反映する）
        let slot_enabled = slot.enabled;
        let n_slots_before = actor.slots().len();
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
                // CLR ホストがあれば実インスタンスを生成し、[SerializeField] 値も復元する。
                // 生成失敗（型が見つからない等）または CLR 不在時は Placeholder にフォールバック。
                let created = scripting_host.and_then(|host| {
                    ScriptComponent::new_with_fields(
                        Arc::clone(host),
                        sc_data.type_name.clone(),
                        sc_data.fields.clone(),
                    )
                });
                if let Some(sc) = created {
                    world.insert(slot_entity, sc);
                    actor.add_slot_typed::<ScriptComponent>(slot_name, ComponentKind::Script, slot_entity);
                } else {
                    world.insert(slot_entity, PlaceholderScriptSlot {
                        script_path: sc_data.type_name,
                        fields:      sc_data.fields,
                    });
                    actor.add_slot_typed::<PlaceholderScriptSlot>(slot_name, ComponentKind::Placeholder, slot_entity);
                }
            }
            ComponentData::CanvasComponent(cc_data) => {
                use crate::engine::components::CanvasComponent;
                world.insert(slot_entity, CanvasComponent {
                    width:             cc_data.width,
                    height:            cc_data.height,
                    auto_scale:        cc_data.auto_scale,
                    viewport_ref:      cc_data.viewport_ref.clone(),
                    gravity_mode:      cc_data.gravity_mode,
                    draw_zone:         cc_data.draw_zone,
                    pivot:             cc_data.pivot,
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
                    layer:        sc_data.layer,
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
            ComponentData::PluginComponent(pc_data) => {
                // PluginComponent はそのまま復元する。
                // 対応する Plugin が存在しなくてもデータは保持し続ける（プラグイン無効時の互換性維持）。
                use crate::engine::components::PluginComponent;
                world.insert(slot_entity, PluginComponent {
                    plugin_name: pc_data.plugin_name,
                    fields:      pc_data.fields,
                });
                actor.add_slot_typed::<PluginComponent>(slot_name, ComponentKind::Plugin, slot_entity);
            }
            ComponentData::ColliderComponent(cc_data) => {
                use crate::engine::components::ColliderComponent;
                world.insert(slot_entity, ColliderComponent::from(cc_data));
                actor.add_slot_typed::<ColliderComponent>(slot_name, ComponentKind::Collider, slot_entity);
            }
            ComponentData::Collider2dComponent(cc_data) => {
                // 2D コライダーコンポーネントを ECS ワールドに挿入してスロットを登録する
                use crate::engine::components::Collider2dComponent;
                world.insert(slot_entity, Collider2dComponent::from(cc_data));
                actor.add_slot_typed::<Collider2dComponent>(slot_name, ComponentKind::Collider2d, slot_entity);
            }
            ComponentData::AudioComponent(ac_data) => {
                // オーディオソースコンポーネントを ECS ワールドに挿入してスロットを登録する
                use crate::engine::components::AudioComponent;
                world.insert(slot_entity, AudioComponent::from_data(ac_data));
                actor.add_slot_typed::<AudioComponent>(slot_name, ComponentKind::Audio, slot_entity);
            }
            ComponentData::LegacyRigidbodyComponent(rb_data) => {
                // 旧フォーマット（Rigidbody が独立コンポーネント）の後方互換マイグレーション。
                // スロットエンティティは生成せず、同アクターの ColliderComponent にデータを適用する。
                use crate::engine::components::ColliderComponent;
                world.despawn(slot_entity);
                if let Some(collider_slot) = actor.slots().iter()
                    .find(|s| s.kind == ComponentKind::Collider)
                {
                    if let Some(cc) = world.get_mut::<ColliderComponent>(collider_slot.entity) {
                        cc.use_rigidbody            = true;
                        cc.mass                     = rb_data.mass;
                        cc.restitution              = rb_data.restitution;
                        cc.friction                 = rb_data.friction;
                        cc.linear_damping           = rb_data.linear_damping;
                        cc.angular_damping          = rb_data.angular_damping;
                        cc.gravity_scale            = rb_data.gravity_scale;
                        cc.is_kinematic             = rb_data.is_kinematic;
                        cc.freeze_position          = rb_data.freeze_position;
                        cc.freeze_rotation          = rb_data.freeze_rotation;
                        cc.initial_linear_velocity  = rb_data.initial_linear_velocity;
                        cc.initial_angular_velocity = rb_data.initial_angular_velocity;
                    }
                }
            }
        }

        // このループで追加されたスロットへ有効フラグを復元する。
        // 各アームは高々 1 スロット追加のため、追加があった場合のみ末尾へ反映する
        // （LegacyRigidbody のようにスロットを追加しないアームでは何もしない）。
        if actor.slots().len() > n_slots_before {
            if let Some(last) = actor.slots_mut().last_mut() {
                last.enabled = slot_enabled;
            }
        }
    }

    for child_data in data.children {
        // 子アクターは常に新規エンティティで構築する（予約は ルートのみ）
        actor.add_child(build_actor(child_data, ctx, world, scripting_host, None)?);
    }

    Ok(actor)
}
