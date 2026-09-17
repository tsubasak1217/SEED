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
    Transform, CanvasTransform,
    ModelComponent, InstanceMeta,
    ScriptComponent, PlaceholderScriptSlot,
    CameraComponent, CameraComponentData,
};
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::{ActorData, ActorKind};
use crate::engine::core::app_base::actor_file::{self, ActorFileError};
use crate::engine::core::app_base::scene_settings::SceneSettingsData;
use crate::engine::core::migration::{self, FormatKind, MigrationError};

/// ゲーム本編（Play で動くシーン）の世界線番号。
///
/// アクタ編集タブ（`OPEN_ACTOR`）は 1 以上の世界線へ `.actor` を読み込む。
/// スクリプトを走らせてよいのはこの世界線のアクタだけ
/// （`sync_script_owners` の説明を参照）。
const SCENE_WORLD_LINE: u32 = 0;

// ============================================================
//  SceneError — シーン読み書き時のエラー型
// ============================================================

/// シーンファイルの読み込み・保存時に発生しうるエラー。
#[derive(Debug)]
pub enum SceneError {
    Io(std::io::Error),
    Json(serde_json::Error),
    Load(LoadError),
    /// 形式の版の判定・変換に失敗した（未来版の拒否を含む）。
    /// メッセージはそのまま利用者へ見せられる日本語になっている。
    Migration(MigrationError),
}

impl std::fmt::Display for SceneError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            SceneError::Io(e)   => write!(f, "IO error: {e}"),
            SceneError::Json(e) => write!(f, "JSON error: {e}"),
            SceneError::Load(e) => write!(f, "Load error: {e}"),
            SceneError::Migration(e) => write!(f, "{e}"),
        }
    }
}

impl std::error::Error for SceneError {}
impl From<std::io::Error>    for SceneError { fn from(e: std::io::Error)    -> Self { Self::Io(e) } }
impl From<serde_json::Error> for SceneError { fn from(e: serde_json::Error) -> Self { Self::Json(e) } }
impl From<LoadError>         for SceneError { fn from(e: LoadError)          -> Self { Self::Load(e) } }
impl From<MigrationError>    for SceneError { fn from(e: MigrationError)     -> Self { Self::Migration(e) } }

/// `.actor` ローダのエラーをシーンのエラーへ移す（種類は保ったまま）。
impl From<ActorFileError> for SceneError {
    fn from(e: ActorFileError) -> Self {
        match e {
            ActorFileError::Io(e)        => Self::Io(e),
            ActorFileError::Json(e)      => Self::Json(e),
            ActorFileError::Migration(e) => Self::Migration(e),
        }
    }
}

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
    /// **旧形式の**エディタ視点（デバッグカメラの位置・向き・fov/far/speed）。
    ///
    /// 【読むが書かない】
    /// 人ごとに必ず違う値なので、保存のたびに差分が出て複数人開発では必ず衝突した。
    /// 現在は視点を `cache/editor/view/**.view.json`（`editor_view_state.rs`）へ、
    /// 共有したい fov/far/speed を `settings.debug_camera` へ分離してあり、
    /// **書き出し側（`SceneDataRef`）はこのキーを出さない**。
    /// ここに残しているのは既存 `.scene` を読むための後方互換だけで、
    /// そのシーンを保存し直した時点でキーごと消える（移行ツールは不要）。
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

/// `SceneData` の **書き出し専用・借用版**。
///
/// 直列化するだけなら Scene のメタデータ（名前・シェーディング設定・地形フォルダ参照）を
/// clone する必要が無い。さらにアクター列を `&[ActorData]` で受け取れるため、呼び出し側が
/// 用意したアクターデータ（Play スナップショットの地形マーカー版など）をそのまま
/// **所有権を渡さずに** 書き出せる。
///
/// 【`SceneData` と同じ JSON を出す責任】
/// フィールド名・`skip_serializing_if` 属性は `SceneData` と 1 対 1 に保つこと。
/// ずれると保存した `.scene` が読めなくなる（両者の往復テストが scene.rs 末尾にある）。
#[derive(Serialize)]
struct SceneDataRef<'a> {
    name:   &'a str,
    /// エディタ視点（旧形式）。**共有される `.scene` へは常に `None` を渡すこと。**
    ///
    /// `Some` を渡してよいのは「そのファイルが共有されない」と言い切れる複製出力だけ
    /// （現状は Play 用一時シーン `%TEMP%\SEED\_play_temp.scene` と、Play 開始状態の
    /// メモリ内スナップショット）。どちらもディスク上の共有ファイルにはならないので、
    /// 視点を埋めても衝突は起きず、Play 側で「メインカメラが無いときのフォールバック視点」
    /// として従来どおり効かせられる。
    #[serde(skip_serializing_if = "Option::is_none")]
    debug_camera: Option<&'a DebugCameraData>,
    #[serde(skip_serializing_if = "Option::is_none")]
    shading_asset: Option<&'a str>,
    #[serde(skip_serializing_if = "std::collections::BTreeMap::is_empty")]
    shading_params: &'a std::collections::BTreeMap<String, [f32; 4]>,
    #[serde(skip_serializing_if = "std::collections::BTreeMap::is_empty")]
    shading_bindings: &'a std::collections::BTreeMap<String, String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    settings: Option<&'a SceneSettingsData>,
    #[serde(skip_serializing_if = "Option::is_none")]
    terrain_dir: Option<&'a str>,
    actors: &'a [ActorData],
}

// ============================================================
//  一括アップグレード用の再直列化
// ============================================================

/// 変換済みの `.scene` の `Value` を、**エンジンが保存するのと同じ並び**のテキストにする。
///
/// 一括アップグレード（`migration::upgrade`）から呼ぶ。`Value` をそのまま
/// `to_string_pretty` すると `serde_json::Map` の並び（アルファベット順）になり、
/// 中身が 1 つも変わらないファイルでも全行が差分になってしまう。
/// `SceneData` を経由すれば欄の並びは宣言順のまま（＝普通に保存したときと同じ）になり、
/// 差分は「版の行」と「実際に変換された値」だけで済む。
///
/// 副作用として「`SceneData` として読めないシーンは書き換えない」という安全弁にもなる
/// （呼び出し側はエラーを `failed` として報告し、ファイルには触らない）。
pub fn scene_text_from_value(value: serde_json::Value) -> Result<String, SceneError> {
    let data: SceneData = serde_json::from_value(value)?;
    Ok(migration::to_stamped_pretty_json(FormatKind::Scene, &data)?)
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
    /// 実効アクティブ = 自身と全祖先の active が true かつ スロットの enabled が true
    /// **かつ、シーン世界線（`world_line == SCENE_WORLD_LINE`）に居ること**。
    /// false のスクリプトは script_system がライフサイクル呼び出しをスキップする。
    ///
    /// 【世界線で切る理由】
    /// アクタ編集タブ（`OPEN_ACTOR`）で開いた `.actor` は、同じ `World` の
    /// 別の世界線（`world_line >= 1`）へ読み込まれる。ここで世界線を見ないと、
    /// **編集タブで開いているだけのアクタのスクリプトが Play 中に走ってしまう**。
    ///
    /// これは「静的フィールドで自分を登録するシングルトン」（`PauseMenu` /
    /// `ResultPanel` など、`OnStart` で `Current = this` を立てる作り）を静かに壊す:
    /// 編集タブ側のインスタンスが本体として登録されてしまい、ゲーム側から開こうとしても
    /// <b>画面に映らない世界線のパネルが開く</b>（＝「押しても何も出ない」）。
    /// 実際に 2026-09 の釣果パネルの不具合はこれが原因だった
    /// （ResultPanel.actor を編集タブで開いたあとの釣り上げだけパネルが出なかった）。
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
            // 世界線はトップレベルアクタが持ち、子は親の世界線に属する。
            // シーン世界線以外（＝アクタ編集タブのプレビュー）はサブツリーごと非アクティブ。
            let in_scene = actor.world_line == SCENE_WORLD_LINE;
            walk(actor, world, in_scene);
        }
    }

    // ── 保存 ──────────────────────────────────────────────────

    /// シーンを `.scene` の JSON テキストへ直列化する（ファイルへは書かない）。
    ///
    /// アクター列は `self.actors` を丸ごと `to_data` した内容になる。
    /// **アクター列を差し替えて書きたい場合**（Play スナップショットのように地形
    /// サブツリーを位置マーカーへ削ぐなど）は `to_json_with_actors` を直接呼ぶ。
    ///
    /// # 引数
    /// * `camera` - エディタ視点を `debug_camera` 節として埋め込むか。
    ///   共有される `.scene` には **必ず `None`**（規約は `SceneDataRef::debug_camera` 参照）。
    pub fn to_json(&self, camera: Option<&DebugCameraData>) -> Result<String, SceneError> {
        let actors: Vec<ActorData> =
            self.actors.iter().map(|a| a.to_data(&self.world)).collect();
        self.to_json_with_actors(camera, &actors)
    }

    /// シーンのメタデータ（名前・シェーディング・シーン設定・地形フォルダ参照）と
    /// **呼び出し側が用意したアクターデータ列**から `.scene` の JSON テキストを組み立てる。
    ///
    /// 【なぜアクター列を外から受け取るのか】
    /// 通常の保存は「World の現状をそのまま」書けばよいが、Play スナップショット
    /// （`app/play_snapshot.rs`）は地形サブツリーを位置マーカーへ削いだアクター列を
    /// 書きたい。チャンク数百枚を serde するコストを避け、地形の復元をメモリ上の
    /// `TerrainState` からに揃えるためである。両者で異なるのはアクター列だけなので、
    /// そこだけを引数にして直列化本体（メタデータの組み立てと JSON 化）を共有する。
    pub fn to_json_with_actors(
        &self,
        camera: Option<&DebugCameraData>,
        actors: &[ActorData],
    ) -> Result<String, SceneError> {
        // 借用版の直列化型を使い、メタデータの clone を避ける。
        let data = SceneDataRef {
            name:         &self.name,
            // エディタ視点は共有ファイルへ書かない（`None` なら丸ごと省略される）。
            debug_camera: camera,
            // シーン既定のシェーディングアセット（未設定なら None のまま出力を省略する）
            shading_asset:    self.shading_asset.as_deref(),
            shading_params:   &self.shading_params,
            shading_bindings: &self.shading_bindings,
            // シーン単位のビューポート／レンダリング設定（未設定なら None のまま出力を省略する）
            settings:      self.settings.as_ref(),
            // 地形フォルダ参照（未設定なら None のまま出力を省略する＝旧 .scene と同じ形）
            terrain_dir:   self.terrain_dir.as_deref(),
            actors,
        };
        // 先頭に現行の `format_version` を刻む（`migration::to_stamped_pretty_json`）。
        // 版が JSON の 1 行目に来るので、差分を見たときに「どの版か」がすぐ判る。
        // Play 用一時シーンと Play スナップショットも同じ直列化を通るため、
        // 復元側（`from_json`）から見れば常に現行版として読める。
        Ok(migration::to_stamped_pretty_json(FormatKind::Scene, &data)?)
    }

    /// シーンを `.scene` ファイルへ保存する（直列化は `to_json` に委譲）。
    ///
    /// # 引数
    /// * `path`   - 書き込み先の実パス
    /// * `camera` - `debug_camera` 節として埋め込むエディタ視点。
    ///   **共有される `.scene`（上書き保存・別名保存）では必ず `None`** を渡すこと。
    ///   視点は `editor_view_state` のユーザー別サイドカーが持つ。
    ///   `Some` を渡してよいのは Play 用一時シーンのような共有されない複製だけ
    ///   （判断は `app/scene_save_ops.rs::write_scene_file` に集約してある）。
    pub fn save(&self, path: &Path, camera: Option<&DebugCameraData>) -> Result<(), SceneError> {
        let json = self.to_json(camera)?;
        // 直接 write せず「旧版を .backup へ退避 → .tmp へ書き切ってから rename」する。
        // 途中で落ちても元の .scene は無傷で残り、誤った内容で上書きしても
        // 直前 10 世代から戻せる（safe_write.rs のコメント参照）。
        if let Some(warning) = crate::engine::core::app_base::safe_write::write_atomic_with_backup(path, &json)? {
            eprintln!("[SEED SAVE] {warning}");
        }
        Ok(())
    }

    // ── 読み込み ──────────────────────────────────────────────

    /// `.actor` ファイル（ActorData JSON）を読み込み、単一アクターのシーンを生成する。
    ///
    /// 読み込みは `actor_file`（版の変換・刻印を含む唯一の経路）に委ねる。
    pub fn load_actor(
        path:           &Path,
        ctx:            &DrawContext,
        scripting_host: Option<&Arc<ScriptingHost>>,
    ) -> Result<Self, SceneError> {
        let data = actor_file::load(path.to_str().unwrap_or(""))?;
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
        let data = actor_file::load(path.to_str().unwrap_or(""))?;
        let mut actor = build_actor(data, ctx, world, scripting_host, root_entity)?;
        // world_line を自身と全子孫へ伝播する
        actor.set_world_line_recursive(world_line);
        Ok(actor)
    }

    /// `.scene` ファイルを読み込んでシーンを構築する（構築本体は `from_json`）。
    ///
    /// 【エディタ視点の解決はここだけ】
    /// 返すデバッグカメラは「ユーザー別サイドカー（`cache/editor/view/**.view.json`）
    /// → `.scene` のトップレベル `debug_camera`（旧シーン互換）→ 無し」の優先順で決まる。
    /// **ファイルから読む経路すべて**（Play 起動・IPC `LOAD_SCENE`・スクリプトのシーン遷移・
    /// Play 停止時の開始シーン読み直し）がこの関数を通るため、ここに置けば経路差が出ない。
    ///
    /// 逆に `from_json`（メモリ上の JSON からの復元＝Play 開始状態のスナップショット）は
    /// サイドカーを見ない。スナップショットには Play 開始時点の視点が埋め込まれており、
    /// そちらを正とするのが正しい（`app/play_snapshot.rs`）。
    pub fn load(
        path:           &Path,
        ctx:            &DrawContext,
        scripting_host: Option<&Arc<ScriptingHost>>,
    ) -> Result<(Self, Option<DebugCameraData>), SceneError> {
        let path_str = path.to_str().unwrap_or("");
        let raw = crate::engine::asset_fs::read_string(path_str)?;
        let (scene, cam) = Self::from_json(&raw, ctx, scripting_host)?;

        // ユーザー別サイドカーがあれば position / yaw / pitch だけを差し替える
        // （fov / far / speed は旧シーンの値 or 既定のまま。settings 節があれば
        //  呼び出し側の `apply_scene_settings` が直後に上書きする）。
        let cam = crate::engine::core::app_base::editor_view_state::merge_into_camera(
            cam,
            crate::engine::core::app_base::editor_view_state::load_for_scene(path_str),
        );
        Ok((scene, cam))
    }

    /// `.scene` の JSON テキストからシーンを構築する（ファイル読み込みを伴わない）。
    ///
    /// `load` の実体。ファイル経路と、メモリ上の直列化文字列から復元する経路
    /// （Play スナップショットの復元＝`app/play_snapshot.rs`）で共有する。
    /// 先頭の BOM は許容する（エディタや外部ツールが付けることがあるため）。
    ///
    /// 【形式の版】
    /// ここが `.scene` の**唯一の読み込み口**なので、版の判定と変換もここで行う
    /// （`migration::load_json`）。古い `.scene` はメモリ上でだけ現行版へ持ち上げられ、
    /// ファイルは書き換わらない。未来の版はエラーになる（`SceneError::Migration`）。
    pub fn from_json(
        raw:            &str,
        ctx:            &DrawContext,
        scripting_host: Option<&Arc<ScriptingHost>>,
    ) -> Result<(Self, Option<DebugCameraData>), SceneError> {
        let data: SceneData = migration::load_json(FormatKind::Scene, raw)?;

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

/// アクタ本体 entity の既定トランスフォーム（Transform / CanvasTransform）を整える。
///
/// `build_actor` から切り出した純粋な World 操作。DrawContext を必要としないので
/// 単体テストから直接検証できる（フォルダの不変条件を守るための要）。
///
/// # 分岐
/// - **3D フォルダ**（is_folder かつ Actor3D）: Transform を一切持たない。
///   予約済みルート（Instantiate）に仮挿入された Transform があれば取り除く。
/// - **2D フォルダ**（is_folder かつ Actor2D）: **単位 CanvasTransform を必ず持つ**。
///   キャンバス系の走査（canvas_collect / pick_2d / collider2d / physics2d 等）は
///   CanvasTransform を持たないアクタでサブツリーを打ち切る仕様のため、変換なしの
///   フォルダで包むと配下のスプライトが丸ごと描画・当たり判定から外れてしまう。
///   単位変換なら子のワールド変換に影響しないので「透過ノード」の性質は保てる。
///   保存側（`Actor::to_data_recursive`）は World の CanvasTransform をそのまま書き出すが、
///   ここでは保存値を採用せず**常に単位変換**を入れる（フォルダの透過性を保存データに
///   依存させない不変条件。手書きや旧データで非単位の値が入っていても無視する）。
/// - **通常 3D**: Transform を挿入する。ただし予約済みルートに既に Transform がある場合
///   （Instantiate 直後にスクリプトが Position を設定済み）はその値を優先する。
/// - **通常 2D**: 保存済み canvas_transform（pivot/anchor 含む）を復元。旧フォーマット
///   （フィールド無し）との互換のため既定値へフォールバックする。
///   予約時に仮挿入された 3D Transform は不要なので取り除く。
pub(crate) fn setup_actor_root_transform(
    world:            &mut World,
    entity:           Entity,
    actor_kind:       ActorKind,
    is_folder:        bool,
    transform:        Option<Transform>,
    canvas_transform: Option<CanvasTransform>,
    reused:           bool,
) {
    match (is_folder, actor_kind) {
        // 3D フォルダ: 変換を一切持たせない
        (true, ActorKind::Actor3D) => {
            if reused { world.remove::<Transform>(entity); }
        }
        // 2D フォルダ: 単位 CanvasTransform を必ず持たせる（保存値は採用しない）
        (true, ActorKind::Actor2D) => {
            if reused { world.remove::<Transform>(entity); }
            world.insert(entity, CanvasTransform::default());
        }
        // 通常 3D
        (false, ActorKind::Actor3D) => {
            if !(reused && world.contains::<Transform>(entity)) {
                world.insert(entity, transform.unwrap_or_default());
            }
        }
        // 通常 2D
        (false, ActorKind::Actor2D) => {
            if reused { world.remove::<Transform>(entity); }
            world.insert(entity, canvas_transform.unwrap_or_default());
        }
    }
}

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
    // 予約済みルートがあればそれを使い、なければ新規 spawn する
    let reused = root_entity.is_some();
    let entity = root_entity.unwrap_or_else(|| world.spawn());

    // アクタ本体 entity の既定トランスフォームを整える（種別・フォルダ属性で分岐）。
    // 分岐の中身と根拠は `setup_actor_root_transform` のコメントを参照。
    setup_actor_root_transform(
        world,
        entity,
        data.actor_kind,
        data.is_folder,
        data.transform,
        data.canvas_transform.clone(),
        reused,
    );

    let mut actor = Actor::new(entity, data.name);
    actor.actor_kind = data.actor_kind;
    // フォルダノードフラグを復元する（省略時 serde デフォルトで false = 通常アクター）。
    actor.is_folder = data.is_folder;
    // アクティブフラグを復元する（省略時は serde デフォルトで true）
    actor.active = data.active;
    // 表示フラグを復元する（省略時は serde デフォルトで true）。
    // active と独立で、false のとき描画だけが止まる。
    actor.visible = data.visible;
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
                // 表示するか（旧 .scene には無いため ModelComponentData 側で既定 true）。
                let visible      = mc_data.visible;
                // LOD を適用しないか（旧 .scene には無いため既定 false）。
                let disable_lod  = mc_data.disable_lod;
                // RT 対象外フラグ（旧 .scene には無いため ModelComponentData 側で既定 false）。
                let rt_exclude   = mc_data.rt_exclude;
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
                        visible,
                        disable_lod,
                        rt_exclude,
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
                    use crate::engine::core::app_base::app::model_streaming;
                    let path  = Path::new(&mc_data.model_path);
                    // ── CPU モデルの入手（3 段構え）────────────────────────────────
                    // ① プロセス内 CPU キャッシュ（同一パスの 2 体目以降）: 即返る。
                    // ② 非同期スコープ（プレイ中の Instantiate）かつ ① ミス:
                    //    ワーカーへ要求だけ出し、モデル無し（gpu_model=None）で構築する。
                    //    完成後に `App::pump_model_streaming` が差し込む（描画は None の間スキップ）。
                    //    先読み済みなら要求を出した時点で完成品が返るので、待ちは発生しない。
                    // ③ それ以外（シーン読み込み・サムネイル・エディタ操作）: 従来どおり同期ロード。
                    //    エディタは SCENE_LOADED を「全モデルが揃った」合図に使うため、
                    //    ここを非同期にしてはいけない。
                    let cached: Option<Arc<crate::engine::core::loader::model::Model>> = {
                        let cache = ctx.model_cache.borrow();
                        cache.get(&mc_data.model_path).map(Arc::clone)
                    };
                    let model: Option<Arc<crate::engine::core::loader::model::Model>> =
                        match cached {
                            Some(c) => Some(c),
                            None if model_streaming::async_model_enabled() => {
                                // 非同期要求。RAM キャッシュに先読み済みなら即座に Some が返る。
                                let ready = model_streaming::request_model_async(
                                    slot_entity, &mc_data.model_path,
                                );
                                if let Some(m) = &ready {
                                    ctx.model_cache.borrow_mut()
                                        .insert(mc_data.model_path.clone(), Arc::clone(m));
                                }
                                ready
                            }
                            None => {
                                let m = Arc::new(load_model(path)?);
                                ctx.model_cache.borrow_mut()
                                    .insert(mc_data.model_path.clone(), Arc::clone(&m));
                                Some(m)
                            }
                        };

                    let total = mc_data.instances.len();
                    // モデルが手元にある場合のみ GPU リソースを作る。
                    // 非同期待ちの間は両方 None ＝ 描画・RT・ピッキングから外れるだけで、
                    // Transform・スクリプト・コライダーは通常どおり動く。
                    let (gpu_model, instanced_batch) = match &model {
                        Some(m) => (
                            Some(ctx.upload_model_with_overrides(&**m, &mc_data.material_overrides)),
                            Some(ctx.create_instanced_batch(&**m, total as u32)),
                        ),
                        None => (None, None),
                    };
                    let mut meta = mc_data.meta;
                    if meta.len() < total {
                        let start = meta.len();
                        meta.resize_with(total, || InstanceMeta::new("Instance"));
                        for i in start..total { meta[i].name = format!("Instance_{i}"); }
                    }
                    world.insert(slot_entity, ModelComponent {
                        source_path:     mc_data.model_path,
                        model,
                        gpu_model,
                        instanced_batch,
                        instance_mats:   mc_data.instances,
                        instance_meta:   meta,
                        group_meta:      mc_data.groups,
                        next_group_id:   mc_data.next_group_id,
                        anim_drive:      None,
                        cast_shadows,
                        visible,
                        disable_lod,
                        rt_exclude,
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
            ComponentData::AudioDictionaryComponent(ad_data) => {
                // 音声辞書コンポーネントを ECS ワールドに挿入してスロットを登録する
                use crate::engine::components::AudioDictionaryComponent;
                world.insert(slot_entity, AudioDictionaryComponent::from_data(ad_data));
                actor.add_slot_typed::<AudioDictionaryComponent>(
                    slot_name, ComponentKind::AudioDictionary, slot_entity);
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

// ============================================================
//  テスト — フォルダノードの既定トランスフォーム不変条件
//  （setup_actor_root_transform = シーン読込 / プレハブ展開 / Instantiate /
//    Undo スナップショット復元がすべて通る唯一の経路）
// ============================================================
#[cfg(test)]
mod folder_transform_tests {
    use super::*;

    /// 2D フォルダは **常に単位 CanvasTransform を持つ**（保存値があっても無視する）。
    ///
    /// キャンバス系の走査は CanvasTransform を持たないアクタでサブツリーを打ち切るため、
    /// 変換を持たないフォルダで包むと配下のスプライトが描画対象から外れてしまう。
    /// 一方で非単位の変換を復元してしまうと「フォルダは透過」という前提が壊れるので、
    /// 読み込み時に単位変換へ正規化する。
    #[test]
    fn folder_2d_always_gets_identity_canvas_transform() {
        let mut world = World::new();
        let e = world.spawn();

        // 保存データ側に非単位の CanvasTransform が入っていても採用しない。
        let mut saved = CanvasTransform::default();
        saved.position = [100.0, -50.0];
        saved.scale    = [2.0, 3.0];
        saved.rotation = 45.0;

        setup_actor_root_transform(
            &mut world, e, ActorKind::Actor2D, true, None, Some(saved), false,
        );

        let ct = world.get::<CanvasTransform>(e).cloned()
            .expect("2D フォルダは CanvasTransform を必ず持つ");
        let identity = CanvasTransform::default();
        assert_eq!(ct.position, identity.position, "2D フォルダの位置は単位（原点）でなければならない");
        assert_eq!(ct.scale,    identity.scale,    "2D フォルダのスケールは単位でなければならない");
        assert_eq!(ct.rotation, identity.rotation, "2D フォルダの回転は単位でなければならない");
        // 3D 用 Transform は持たない（2D アクタ共通）。
        assert!(!world.contains::<Transform>(e), "2D フォルダに Transform を持たせてはならない");
    }

    /// 3D フォルダは Transform も CanvasTransform も一切持たない（完全な透過ノード）。
    #[test]
    fn folder_3d_carries_no_transform_at_all() {
        let mut world = World::new();
        let e = world.spawn();

        setup_actor_root_transform(
            &mut world, e, ActorKind::Actor3D, true, Some(Transform::default()), None, false,
        );

        assert!(!world.contains::<Transform>(e), "3D フォルダは Transform を持たない");
        assert!(!world.contains::<CanvasTransform>(e), "3D フォルダは CanvasTransform を持たない");
    }

    /// 通常（非フォルダ）2D アクタは保存済み CanvasTransform をそのまま復元する
    /// （フォルダの正規化が通常アクタへ波及していないことの対照テスト）。
    #[test]
    fn normal_2d_actor_restores_saved_canvas_transform() {
        let mut world = World::new();
        let e = world.spawn();

        let mut saved = CanvasTransform::default();
        saved.position = [12.0, 34.0];

        setup_actor_root_transform(
            &mut world, e, ActorKind::Actor2D, false, None, Some(saved), false,
        );

        let ct = world.get::<CanvasTransform>(e).cloned().unwrap();
        assert_eq!(ct.position, [12.0, 34.0], "通常 2D アクタは保存値を復元する");
    }

    /// 書き出した JSON が、読み込み側の型（`SceneData`）でそのまま読めること。
    ///
    /// 直列化は借用版の `SceneDataRef`、逆直列化は所有版の `SceneData` という
    /// 2 つの型に分かれている。フィールド名や `skip_serializing_if` がずれると
    /// 「保存した .scene が読めない」という最悪の退行になるため、往復で固定する。
    #[test]
    fn scene_json_is_readable_as_scene_data() {
        let mut scene = Scene::new("round_trip");
        scene.shading_asset = Some("assets://shading/x.wgsl".to_string());
        scene.terrain_dir   = Some("terrain/round_trip".to_string());
        scene.shading_params.insert("tint".to_string(), [1.0, 2.0, 3.0, 4.0]);
        scene.shading_bindings.insert("target".to_string(), "Actor|Model|pos".to_string());
        // アクターを 1 体（子付き）置く。GPU は要らない。
        let parent_e = scene.world.spawn();
        scene.world.insert(parent_e, Transform::default());
        let mut parent = Actor::new(parent_e, "Parent");
        let child_e = scene.world.spawn();
        scene.world.insert(child_e, Transform::default());
        parent.add_child(Actor::new(child_e, "Child"));
        scene.actors.push(parent);

        let camera = DebugCameraData::default();
        let json   = scene.to_json(Some(&camera)).expect("直列化できること");
        let data: SceneData = serde_json::from_str(&json).expect("読み込み側の型で読めること");

        assert_eq!(data.name, "round_trip");
        assert_eq!(data.shading_asset.as_deref(), Some("assets://shading/x.wgsl"));
        assert_eq!(data.terrain_dir.as_deref(), Some("terrain/round_trip"));
        assert_eq!(data.shading_params.get("tint"), Some(&[1.0, 2.0, 3.0, 4.0]));
        assert_eq!(data.shading_bindings.get("target").map(String::as_str), Some("Actor|Model|pos"));
        assert!(data.debug_camera.is_some(), "明示的に渡したデバッグカメラは往復する");
        assert_eq!(data.actors.len(), 1, "トップレベルアクター数が往復する");
        assert_eq!(data.actors[0].name, "Parent");
        assert_eq!(data.actors[0].children.len(), 1, "子ツリーが往復する");
        assert_eq!(data.actors[0].children[0].name, "Child");
    }

    /// 保存した `.scene` の JSON は、**先頭に**現行の `format_version` を持つこと。
    ///
    /// 版の欄が先頭に来ていれば、差分を見たときに「どの版のファイルか」が 1 行目で判る。
    /// また、その欄があっても読み込み側（`SceneData`）が素通しできることを同時に固定する
    /// （`SceneData` に欄を足していないので、未知キーとして無視されるのが正しい）。
    #[test]
    fn saved_scene_json_starts_with_the_current_format_version() {
        let mut scene = Scene::new("stamped");
        let e = scene.world.spawn();
        scene.world.insert(e, Transform::default());
        scene.actors.push(Actor::new(e, "Solo"));

        let json = scene.to_json(None).expect("直列化できること");

        // 先頭（1 行目が "{" なので 2 行目）に版の欄が来ること
        let version_key = crate::engine::core::migration::JSON_VERSION_KEY;
        assert!(
            json.lines().nth(1).unwrap_or_default().contains(version_key),
            "版の欄が JSON の先頭に無い:\n{json}"
        );
        // 値が現行版であること
        let value: serde_json::Value = serde_json::from_str(&json).expect("JSON として読めること");
        assert_eq!(
            value[version_key],
            serde_json::json!(FormatKind::Scene.current_version())
        );
        // 版の欄はトップレベルだけ（シーン内の各アクタには付けない）
        assert_eq!(
            json.matches(version_key).count(),
            1,
            "版の欄がトップレベル以外にも出ている:\n{json}"
        );
        // 読み込み側の型でそのまま読めること
        let data: SceneData = serde_json::from_str(&json).expect("読み込み側の型で読めること");
        assert_eq!(data.name, "stamped");
        assert_eq!(data.actors.len(), 1);
    }

    /// **共有される `.scene` にはエディタ視点（`debug_camera`）を 1 バイトも書かない。**
    ///
    /// これが本改修の核心の不変条件。ここが崩れると「保存するたびに人ごとの視点で
    /// 差分が出てコンフリクトする」という元の問題がそのまま戻る。
    /// キーの有無を JSON の生テキストで直接検査する（型を経由すると
    /// `skip_serializing_if` の取り違えを見逃すため）。
    #[test]
    fn saved_scene_json_has_no_top_level_debug_camera() {
        let mut scene = Scene::new("no_view_state");
        let e = scene.world.spawn();
        scene.world.insert(e, Transform::default());
        scene.actors.push(Actor::new(e, "Solo"));

        // `Scene::save` が使うのと同じ引数（camera = None）で直列化する。
        let json = scene.to_json(None).expect("直列化できること");

        let value: serde_json::Value = serde_json::from_str(&json).expect("JSON として読めること");
        let obj = value.as_object().expect("トップレベルはオブジェクト");
        assert!(
            !obj.contains_key("debug_camera"),
            "共有される .scene にエディタ視点が書かれている: {json}"
        );
        // 生テキストにも現れないこと（ネストした別ノードに紛れ込んでいない保証）
        assert!(
            !json.contains("debug_camera"),
            "出力のどこかに debug_camera が残っている: {json}"
        );
        // 共有すべき情報は従来どおり出る
        assert_eq!(obj.get("name").and_then(|v| v.as_str()), Some("no_view_state"));
        assert!(obj.contains_key("actors"));

        // 書いた JSON は読み込み側の型でそのまま読めること（キー欠落で壊れない）。
        let data: SceneData = serde_json::from_str(&json).expect("読み込み側の型で読めること");
        assert!(data.debug_camera.is_none(), "読み戻しても視点は無い");
        assert_eq!(data.actors.len(), 1);
    }

    /// 旧 `.scene`（トップレベル `debug_camera` あり）は今も読める（後方互換）。
    ///
    /// 保存し直せばキーは消えるが、それまでの間は旧形式のまま開けなければならない。
    #[test]
    fn legacy_scene_with_debug_camera_is_still_readable() {
        let raw = r#"{
            "name": "legacy",
            "debug_camera": {
                "position": [1.0, 2.0, 3.0],
                "yaw": 0.5, "pitch": -0.25,
                "fov_deg": 60.0, "far": 500.0, "speed": 12.0
            },
            "actors": []
        }"#;
        let data: SceneData = serde_json::from_str(raw).expect("旧形式が読めること");
        let cam = data.debug_camera.expect("旧形式の視点が読めること");
        assert_eq!(cam.position, [1.0, 2.0, 3.0]);
        assert_eq!(cam.fov_deg, 60.0);
        assert_eq!(cam.speed, 12.0);
    }

    /// ActorData（is_folder=true / Actor2D）の serde 往復後も、読み込み経路を通せば
    /// 単位 CanvasTransform が入る（シーン保存 → 読込のラウンドトリップ相当）。
    #[test]
    fn folder_2d_scene_roundtrip_yields_identity_canvas_transform() {
        // 2D フォルダを World 上に作り、保存用データへ変換する。
        let mut world = World::new();
        let fe = world.spawn();
        world.insert(fe, CanvasTransform::default());
        let folder = Actor::new_folder_2d(fe, "UIGroup");
        let data   = folder.to_data(&world);
        assert!(data.is_folder);
        assert_eq!(data.actor_kind, ActorKind::Actor2D);

        // .scene と同じく JSON を経由してから読み込み経路へ通す。
        let json: String = serde_json::to_string(&data).unwrap();
        let back: ActorData = serde_json::from_str(&json).unwrap();
        assert!(back.is_folder, "is_folder が往復で失われている");
        assert_eq!(back.actor_kind, ActorKind::Actor2D, "actor_kind が往復で失われている");

        let mut world2 = World::new();
        let e2 = world2.spawn();
        setup_actor_root_transform(
            &mut world2, e2, back.actor_kind, back.is_folder,
            back.transform, back.canvas_transform, false,
        );
        assert!(world2.contains::<CanvasTransform>(e2),
                "読み込んだ 2D フォルダに CanvasTransform が入っていない");
    }
}
