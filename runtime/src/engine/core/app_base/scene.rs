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
use crate::engine::core::app_base::scene_settings::SceneSettingsData;

// ============================================================
//  SceneError — シーン読み書き時のエラー型
// ============================================================

/// シーンファイルの読み込み・保存時に発生しうるエラー。
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

/// Edit モードのデバッグ用フリーカメラの保存データ（位置・向き・FOV・移動速度）。
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

/// シーンファイル（.scene の JSON）の直列化・逆直列化用データ型。
/// Scene::save / Scene::load がこの型を介してディスクとやり取りする。
#[derive(Serialize, Deserialize)]
struct SceneData {
    name:   String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    debug_camera: Option<DebugCameraData>,
    /// シーン既定のシェーディングアセット（WGSL ファイル）のパス。
    /// カメラ側が未指定のときのフォールバック先。None なら組み込み標準 PBR を使う。
    /// パスは `assets://` 仮想パスまたは絶対パス（engine/asset_fs.rs の規約）。
    #[serde(default, skip_serializing_if = "Option::is_none")]
    shading_asset: Option<String>,
    /// シーン既定のシェーディングアセットが宣言した `override` パラメータの上書き値。
    /// キー = アセット内の識別子／値 = 4 成分。空なら丸ごと省略する（旧 `.scene` 互換）。
    #[serde(default, skip_serializing_if = "std::collections::BTreeMap::is_empty")]
    shading_params: std::collections::BTreeMap<String, [f32; 4]>,
    /// `@ref` パラメータのバインド先（`"アクタ名|スロット名|変数名"`）。
    #[serde(default, skip_serializing_if = "std::collections::BTreeMap::is_empty")]
    shading_bindings: std::collections::BTreeMap<String, String>,
    /// シーン単位のビューポート／レンダリング設定（`scene_settings::SceneSettingsData`）。
    /// 旧 `.scene` にはこのキーが無いため None のまま読める。None のときは出力時もキーごと省略し、
    /// project_settings.json 側の設定（`App::load_graphics_settings`）がそのまま効く。
    #[serde(default, skip_serializing_if = "Option::is_none")]
    settings: Option<SceneSettingsData>,
    /// 地形一式（`.tvox` / `.tscatter` / `.tcover`）を置く「地形フォルダ」への参照。
    ///
    /// アセットルート相対のスラッシュ区切りパス（例 `terrain/Scene1`・`levels/forest/ground`）。
    /// 正規化と既定値の解決は `engine::terrain::dir_ref` が一手に担う。
    /// 旧 `.scene` にはこのキーが無いため `None` のまま読め、その場合は従来の固定パス
    /// `terrain/<シーン名>` が使われる（後方互換）。未設定なら出力時もキーごと省略する。
    #[serde(default, skip_serializing_if = "Option::is_none")]
    terrain_dir: Option<String>,
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
    /// シーン既定のシェーディングアセット（WGSL ファイル）のパス。
    /// CameraComponent 側が未指定のときのフォールバック先。
    /// None なら組み込み標準 PBR を使う。
    /// パスは `assets://` 仮想パスまたは絶対パス（engine/asset_fs.rs の規約）。
    pub shading_asset: Option<String>,
    /// シーン既定のシェーディングアセットのパラメータ上書き値。
    ///
    /// **上書きだけを持つ差分**であり、値の無いパラメータはアセットの既定値で描かれる。
    /// カメラ側にアセットが指定されている場合はカメラ側の値が使われる
    /// （アセットのフォールバック連鎖と同じ持ち主から値も採る）。
    pub shading_params: std::collections::BTreeMap<String, [f32; 4]>,
    /// `@ref` パラメータのバインド先（キーは `shading_params` と同じ空間）。
    ///
    /// ## アクタ改名の追従
    /// 値の 1 要素目がアクタ名なので、アクタ改名時に `rename_refs.rs` がここを書き換える。
    pub shading_bindings: std::collections::BTreeMap<String, String>,
    /// シーン単位のビューポート／レンダリング設定（`.scene` の `settings` 節）。
    /// None は「このシーンには設定が無い」＝旧シーン。その場合は起動時に読んだ
    /// project_settings.json の設定がそのまま使われる（フォールバック）。
    pub settings: Option<SceneSettingsData>,
    /// 地形一式を置く「地形フォルダ」への参照（アセットルート相対・スラッシュ区切り）。
    ///
    /// `None` は「このシーンには参照が無い」＝旧シーン。その場合は
    /// `terrain::dir_ref::default_for_scene`（＝`terrain/<シーン名>`）が使われる。
    /// 「名前を付けて保存」（`TERRAIN_SAVE_AS`）がここを書き換え、以後の保存・読込は
    /// すべてこの参照に従う。
    pub terrain_dir: Option<String>,
}

impl Scene {
    pub fn new(name: impl Into<String>) -> Self {
        // エンジン標準の ECS システム（ScriptSystem 等）を登録した Schedule を構築する
        let mut schedule = Schedule::new();
        crate::engine::systems::register_default_systems(&mut schedule);
        // シェーディングアセットは既定で未設定（組み込み標準 PBR を使う）
        // シーン設定は既定で未設定（project_settings.json 側の設定が使われる）
        Self { name: name.into(), world: World::new(), actors: Vec::new(), schedule, shading_asset: None,
               shading_params: std::collections::BTreeMap::new(),
               shading_bindings: std::collections::BTreeMap::new(), settings: None,
               // 地形フォルダ参照は既定で未設定（terrain/<シーン名> が使われる）
               terrain_dir: None }
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
            // シーン既定のシェーディングアセット（未設定なら None のまま出力を省略する）
            shading_asset:    self.shading_asset.clone(),
            shading_params:   self.shading_params.clone(),
            shading_bindings: self.shading_bindings.clone(),
            // シーン単位のビューポート／レンダリング設定（未設定なら None のまま出力を省略する）
            settings:      self.settings.clone(),
            // 地形フォルダ参照（未設定なら None のまま出力を省略する＝旧 .scene と同じ形）
            terrain_dir:   self.terrain_dir.clone(),
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
        // シーン既定のシェーディングアセットを復元する（旧 .scene には無いので None のまま）
        scene.shading_asset    = data.shading_asset;
        scene.shading_params   = data.shading_params;
        scene.shading_bindings = data.shading_bindings;
        // シーン単位のビューポート／レンダリング設定を復元する（旧 .scene には無いので None のまま）。
        // 実際の適用は呼び出し側（App::load_play_scene / IPC LOAD_SCENE ハンドラ）が
        // App::apply_scene_settings で行う。
        scene.settings = data.settings;
        // 地形フォルダ参照を復元する（旧 .scene には無いので None のまま＝従来の既定パス）。
        scene.terrain_dir = data.terrain_dir;
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

    // フォルダノード（is_folder）は整理専用の透過ノードで Transform を一切持たない。
    // よってデフォルト Transform / CanvasTransform の挿入をスキップする
    // （子のワールド変換に影響させないため）。予約済みルートに仮挿入された
    // Transform があれば取り除いておく（フォルダに Transform を残さない）。
    if data.is_folder {
        if reused {
            world.remove::<Transform>(entity);
        }
    } else {
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
    }

    let mut actor = Actor::new(entity, data.name);
    actor.actor_kind = data.actor_kind;
    // フォルダノードフラグを復元する（省略時 serde デフォルトで false = 通常アクター）。
    actor.is_folder = data.is_folder;
    // アクティブフラグを復元する（省略時は serde デフォルトで true）
    actor.active = data.active;
    // プレハブ参照リンクを復元する（インスタンスのルートのみ Some、子は None）。
    // シーンロード時の再展開・ライブ反映の対象判定に使用する。
    actor.prefab_source = data.prefab_source;
    // 地形散布の自動生成マーカーを復元する（手動配置は None）。
    // 再散布時に既存生成アクタを特定して置き換えるために使用する。
    actor.scatter_prop_id = data.scatter_prop_id;

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
                let cast_shadows = mc_data.cast_shadows;
                // 【地形チャンクの特例】source_path が `terrain://` 接頭辞の場合は実ファイルが
                // 存在しないため load_model をスキップする（さもないとシーンロード全体が失敗する）。
                // model/gpu_model は None のままにし、terrain_ops の rebuild_terrain_after_load が
                // 対応する .tvox からメッシュを再構築して埋める。空パス（未設定）と同じ経路で扱う。
                let is_terrain_synthetic =
                    mc_data.model_path.starts_with(crate::engine::components::TERRAIN_SOURCE_SCHEME);
                if mc_data.model_path.is_empty() || is_terrain_synthetic {
                    // モデル未設定の空コンポーネント、または地形チャンク（後で rebuild される）。
                    // source_path は保持する（terrain:// パスは rebuild 後の描画・RT キャスタ判定に必要）。
                    let meta = mc_data.meta;
                    world.insert(slot_entity, ModelComponent {
                        source_path:     mc_data.model_path,
                        model:           None,
                        gpu_model:       None,
                        instanced_batch: None,
                        instance_mats:   mc_data.instances,
                        instance_meta:   meta,
                        group_meta:      mc_data.groups,
                        next_group_id:   mc_data.next_group_id,
                        anim_drive:      None,
                        cast_shadows,
                        material_overrides: mc_data.material_overrides,
                        // セマンティックタグ（旧 .scene には無いため ModelComponentData 側で既定 0）。
                        render_tag:      mc_data.render_tag,
                        // 描画オフセット（旧 .scene には無いため ModelComponentData 側で既定＝恒等）。
                        offset_position:      mc_data.offset_position,
                        offset_rotation:      mc_data.offset_rotation,
                        offset_scale:      mc_data.offset_scale,
                        batch_instance_id: crate::engine::components::next_batch_instance_id(),
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
                    let gpu_model       = ctx.upload_model_with_overrides(&*model, &mc_data.material_overrides);
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
                        anim_drive:      None,
                        cast_shadows,
                        material_overrides: mc_data.material_overrides,
                        // セマンティックタグ（旧 .scene には無いため ModelComponentData 側で既定 0）。
                        render_tag:      mc_data.render_tag,
                        // 描画オフセット（旧 .scene には無いため ModelComponentData 側で既定＝恒等）。
                        offset_position:      mc_data.offset_position,
                        offset_rotation:      mc_data.offset_rotation,
                        offset_scale:      mc_data.offset_scale,
                        batch_instance_id: crate::engine::components::next_batch_instance_id(),
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
                // 復元は必ず from_data 経由（フィールド追加時の写し漏れを原理的に防ぐ）
                world.insert(slot_entity, SpriteComponent::from_data(sc_data));
                actor.add_slot_typed::<SpriteComponent>(slot_name, ComponentKind::Sprite, slot_entity);
            }
            ComponentData::SkinnedSpriteComponent(sc_data) => {
                use crate::engine::components::SkinnedSpriteComponent;
                world.insert(slot_entity, SkinnedSpriteComponent::from_data(sc_data));
                actor.add_slot_typed::<SkinnedSpriteComponent>(
                    slot_name, ComponentKind::SkinnedSprite, slot_entity);
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
            ComponentData::LineRendererComponent(lr_data) => {
                // 3D ポリラインを ECS ワールドに挿入してスロットを登録する
                use crate::engine::components::LineRendererComponent;
                world.insert(slot_entity, LineRendererComponent::from_data(lr_data));
                actor.add_slot_typed::<LineRendererComponent>(
                    slot_name, ComponentKind::LineRenderer, slot_entity);
            }
            ComponentData::TextComponent(t_data) => {
                // キャンバステキストを ECS ワールドに挿入してスロットを登録する
                use crate::engine::components::TextComponent;
                world.insert(slot_entity, TextComponent::from_data(t_data));
                actor.add_slot_typed::<TextComponent>(
                    slot_name, ComponentKind::Text, slot_entity);
            }
            ComponentData::WaterVolumeComponent(wv_data) => {
                // 水ボリュームコンポーネントを ECS ワールドに挿入してスロットを登録する
                use crate::engine::components::WaterVolumeComponent;
                world.insert(slot_entity, WaterVolumeComponent::from_data(wv_data));
                actor.add_slot_typed::<WaterVolumeComponent>(slot_name, ComponentKind::WaterVolume, slot_entity);
            }
            ComponentData::WaterLinkComponent(wl_data) => {
                // 水位グラフのリンク＝開口（W2.5）を ECS ワールドに挿入してスロットを登録する
                use crate::engine::components::WaterLinkComponent;
                world.insert(slot_entity, WaterLinkComponent::from_data(wl_data));
                actor.add_slot_typed::<WaterLinkComponent>(slot_name, ComponentKind::WaterLink, slot_entity);
            }
            ComponentData::InteractionSourceComponent(is_data) => {
                // インタラクションソースを ECS ワールドに挿入してスロットを登録する
                use crate::engine::components::InteractionSourceComponent;
                world.insert(slot_entity, InteractionSourceComponent::from_data(&is_data));
                actor.add_slot_typed::<InteractionSourceComponent>(
                    slot_name, ComponentKind::InteractionSource, slot_entity);
            }
            ComponentData::CoverEmitterComponent(ce_data) => {
                // カバーエミッタ（I3.1）を ECS ワールドに挿入してスロットを登録する
                use crate::engine::components::CoverEmitterComponent;
                world.insert(slot_entity, CoverEmitterComponent::from_data(&ce_data));
                actor.add_slot_typed::<CoverEmitterComponent>(
                    slot_name, ComponentKind::CoverEmitter, slot_entity);
            }
            ComponentData::ControlPointComponent(cp_data) => {
                // コントロールポイント（汎用パスの点列）を ECS ワールドに挿入してスロットを登録する
                use crate::engine::components::ControlPointComponent;
                world.insert(slot_entity, ControlPointComponent::from_data(&cp_data));
                actor.add_slot_typed::<ControlPointComponent>(
                    slot_name, ComponentKind::ControlPoint, slot_entity);
            }
            ComponentData::AnimatorComponent(an_data) => {
                // アニメーターコンポーネントを ECS ワールドに挿入してスロットを登録する
                use crate::engine::components::AnimatorComponent;
                world.insert(slot_entity, AnimatorComponent::from_data(an_data));
                actor.add_slot_typed::<AnimatorComponent>(slot_name, ComponentKind::Animator, slot_entity);
            }
            ComponentData::LightComponent(lc_data) => {
                // ライトコンポーネントを ECS ワールドに挿入してスロットを登録する
                use crate::engine::components::LightComponent;
                world.insert(slot_entity, LightComponent::from_data(lc_data));
                actor.add_slot_typed::<LightComponent>(slot_name, ComponentKind::Light, slot_entity);
            }
            ComponentData::JointAttachComponent(ja_data) => {
                // ジョイントアタッチ（ソケット）コンポーネントを ECS ワールドに挿入してスロットを登録する
                use crate::engine::components::JointAttachComponent;
                world.insert(slot_entity, JointAttachComponent::from_data(ja_data));
                actor.add_slot_typed::<JointAttachComponent>(slot_name, ComponentKind::JointAttach, slot_entity);
            }
            ComponentData::ParticleEmitterComponent(pe_data) => {
                // パーティクルエミッタコンポーネントを ECS ワールドに挿入してスロットを登録する
                use crate::engine::components::ParticleEmitterComponent;
                world.insert(slot_entity, ParticleEmitterComponent::from_data(pe_data));
                actor.add_slot_typed::<ParticleEmitterComponent>(slot_name, ComponentKind::ParticleEmitter, slot_entity);
            }
            ComponentData::SkyboxComponent(sb_data) => {
                // スカイボックスコンポーネントを ECS ワールドに挿入してスロットを登録する
                use crate::engine::components::SkyboxComponent;
                world.insert(slot_entity, SkyboxComponent::from_data(sb_data));
                actor.add_slot_typed::<SkyboxComponent>(slot_name, ComponentKind::Skybox, slot_entity);
            }
            ComponentData::TerrainChunkComponent(tc_data) => {
                // 地形チャンクコンポーネントを ECS ワールドに挿入してスロットを登録する。
                // 実メッシュ（ModelComponent）は rebuild_terrain_after_load が .tvox から復元する。
                use crate::engine::components::TerrainChunkComponent;
                world.insert(slot_entity, TerrainChunkComponent::from_data(tc_data));
                actor.add_slot_typed::<TerrainChunkComponent>(slot_name, ComponentKind::TerrainChunk, slot_entity);
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

// ============================================================
//  テスト — find_main_camera の子アクタ解決
//
//  埋め込みインプレース Play の黒画面調査（fps-degradation）向け。
//  実機構成「Actor / Player[Camera スロット] / ...」のように、メインカメラが
//  トップレベルではなく**子アクタのスロット**に付いている場合でも、
//  find_main_camera が DFS でそれを見つけ、そのアクタのワールド Transform を
//  返すことを保証する回帰テスト。
//
//  【背景】Pause 時の sync_debug_camera_to_main_camera（この find_main_camera を
//  使う）がゲームカメラ位置を正しく再現できている実機観察と整合し、
//  「黒画面はカメラ解決失敗ではない」ことをヘッドレスで裏付ける。
//  Transform はワールド空間保持（transform_sync.rs）のため、ここで返る位置は
//  そのまま描画カメラ位置になる。
// ============================================================
#[cfg(test)]
mod find_main_camera_tests {
    use super::Scene;
    use crate::engine::components::{CameraComponent, ComponentKind, Transform};
    use crate::engine::structs::objects::Actor;

    /// 指定ワールド位置の Transform をアクタ本体 entity に持たせる。
    fn spawn_actor_at(scene: &mut Scene, name: &str, pos: [f32; 3]) -> Actor {
        let e = scene.world.spawn();
        let mut tf = Transform::default();
        tf.position = pos;
        scene.world.insert(e, tf);
        Actor::new(e, name)
    }

    /// アクタへ is_main フラグ指定の Camera スロットを 1 つ追加する。
    fn attach_main_camera(scene: &mut Scene, actor: &mut Actor, is_main: bool) {
        let slot_e = scene.world.spawn();
        let mut cc = CameraComponent::default();
        cc.is_main = is_main;
        scene.world.insert(slot_e, cc);
        actor.add_slot_typed::<CameraComponent>("Camera", ComponentKind::Camera, slot_e);
    }

    /// メインカメラがトップレベルではなく子アクタのスロットに付いていても、
    /// find_main_camera が DFS で解決し、その子アクタのワールド Transform を返す。
    #[test]
    fn resolves_main_camera_on_child_actor() {
        let mut scene = Scene::new("t");

        // Root（カメラ無し）> Player（is_main カメラ・既知のワールド位置）
        let mut root   = spawn_actor_at(&mut scene, "Root", [0.0, 0.0, 0.0]);
        let mut player = spawn_actor_at(&mut scene, "Player", [3.0, 4.0, 5.0]);
        attach_main_camera(&mut scene, &mut player, true);
        root.add_child(player);
        scene.actors.push(root);

        let found = scene.find_main_camera();
        assert!(found.is_some(), "子アクタの is_main カメラが解決できていない");
        let (tf, cd) = found.unwrap();
        // 返る Transform は子アクタ Player のワールド位置そのもの。
        assert_eq!(tf.position, [3.0, 4.0, 5.0], "子カメラのワールド位置が返っていない");
        assert!(cd.is_main, "返った CameraComponentData が is_main でない");
    }

    /// is_main=false のカメラしか無い場合は None（メインカメラ扱いしない）。
    #[test]
    fn ignores_non_main_camera() {
        let mut scene = Scene::new("t");
        let mut player = spawn_actor_at(&mut scene, "Player", [1.0, 2.0, 3.0]);
        attach_main_camera(&mut scene, &mut player, false);
        scene.actors.push(player);

        assert!(scene.find_main_camera().is_none(),
                "is_main=false のカメラはメインカメラとして解決してはならない");
    }

    /// DFS 順で最初に見つかった is_main カメラを採用する（複数トップレベル）。
    #[test]
    fn returns_first_main_camera_in_dfs_order() {
        let mut scene = Scene::new("t");

        let mut a = spawn_actor_at(&mut scene, "A", [10.0, 0.0, 0.0]);
        attach_main_camera(&mut scene, &mut a, true);
        let mut b = spawn_actor_at(&mut scene, "B", [20.0, 0.0, 0.0]);
        attach_main_camera(&mut scene, &mut b, true);
        scene.actors.push(a);
        scene.actors.push(b);

        let (tf, _) = scene.find_main_camera().expect("メインカメラが見つからない");
        assert_eq!(tf.position, [10.0, 0.0, 0.0], "DFS 先頭のメインカメラを返すべき");
    }
}
