// ============================================================
//  actor/mod.rs — Actor（ECS エンティティのラッパー）
//
//  【設計】
//  Actor は「エンティティ ID + ヒエラルキー情報 + コンポーネント目録」を持つ。
//  コンポーネントの実データは World の SparseSet に格納され、
//  Actor はどの型のコンポーネントを持っているかのリスト（ComponentSlot）のみを保持する。
//
//  ─────────────────────────────────────────────────────────
//  Actor {
//      entity:     Entity,            // ECS ID（コンポーネント検索キー）
//      name:       String,            // ヒエラルキー表示名
//      world_line: u32,               // 所属世界線（DFS フィルタ用）
//      children:   Vec<Actor>,        // 子 Actor（ツリー順序を保持）
//      slots:      Vec<ComponentSlot> // 持っているコンポーネントの目録
//  }
//  ─────────────────────────────────────────────────────────
//
//  コンポーネントへのアクセス:
//      // 旧: actor.get_component::<ModelComponent>()
//      // 新: world.get::<ModelComponent>(actor.entity)
//
//  シリアライズは World を渡して actor.to_data(&world) を呼ぶ。
// ============================================================

use std::any::TypeId;
use serde::{Deserialize, Serialize};

use crate::engine::ecs::{Entity, World};
use crate::engine::components::{
    ComponentData, ComponentKind,
    Transform, CanvasTransform,
    ModelComponent, ModelComponentData,
    ScriptComponent, ScriptComponentData, PlaceholderScriptSlot,
    CanvasComponent, CanvasComponentData,
    SpriteComponent,
    InputMapComponent,
    CameraComponent,
    ColliderComponent, ColliderComponentData,
};
use crate::engine::core::clock::FrameContext;

// ─── ActorKind ────────────────────────────────────────────────────────────────

/// Actor の種別（3D / 2D）。
///
/// - Actor3D: デフォルト Transform（3D ワールド空間）を持つ
/// - Actor2D: CanvasTransform（XY キャンバス空間）を持つ
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum ActorKind {
    /// 3D Actor（Transform / WorldTransform）
    #[default]
    Actor3D,
    /// 2D Actor（CanvasTransform）
    Actor2D,
}

// ─── ComponentSlot ────────────────────────────────────────────────────────────

/// Actor が持つコンポーネント 1 スロットの目録エントリ。
///
/// 【設計】
/// 同一 Actor に同型コンポーネント（例: ModelComponent × 2）を複数アタッチできるよう、
/// スロットごとに専用の ECS エンティティを持つ。
/// コンポーネント実データは slot.entity に格納され、actor.entity とは独立している。
///
/// スロット名はユーザーが任意に命名できる（例: "Body", "Weapon"）。
pub struct ComponentSlot {
    /// ユーザー命名のスロット名（エディタ表示用）
    pub name: String,
    /// コンポーネント種別（シリアライズ・表示に使う）
    pub kind: ComponentKind,
    /// 実際の Rust 型 ID（has_component::<T>() など型安全チェック用）
    pub(crate) type_id: TypeId,
    /// スロット専用の ECS エンティティ（コンポーネント実データの格納先）。
    /// Actor の entity とは別に spawn され、スロットごとに独立したデータを保持する。
    pub entity: Entity,
    /// コンポーネントの有効フラグ（Unity の enabled 相当）。
    /// false のとき描画・スクリプト実行・物理収集などの対象から外れる。
    /// データは World に保持されたままなので、再度 true にすれば元の状態で復帰する。
    pub enabled: bool,
}

impl ComponentSlot {
    /// 型パラメータから ComponentSlot を生成するヘルパー。
    pub fn new<T: crate::engine::ecs::Component + 'static>(
        name:   impl Into<String>,
        kind:   ComponentKind,
        entity: Entity,
    ) -> Self {
        Self { name: name.into(), kind, type_id: TypeId::of::<T>(), entity, enabled: true }
    }

    /// このスロットが指定型のコンポーネントかを確認する。
    pub fn is<T: 'static>(&self) -> bool { self.type_id == TypeId::of::<T>() }
}

// ─── ActorData ────────────────────────────────────────────────────────────────

/// Actor のシリアライズ用データ（JSON 保存・Undo スナップショット）。
#[derive(Clone, Serialize, Deserialize)]
pub struct ActorData {
    pub name:       String,
    /// エディタ/AI 用の DFS 順 ID。保存時は出力しない。
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub dfs_id:     Option<u32>,
    /// Actor の種別（3D / 2D）。省略時は Actor3D。
    #[serde(default, skip_serializing_if = "is_actor3d")]
    pub actor_kind: ActorKind,
    /// 3D アクターのトランスフォーム。Actor3D のみ使用。
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub transform:  Option<Transform>,
    /// 2D アクターのキャンバストランスフォーム（position/rotation/scale/pivot/anchor）。
    /// Actor2D のみ使用。既存の .actor ファイルとの互換性のため省略可（省略時はデフォルト）。
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub canvas_transform: Option<CanvasTransform>,
    pub components: Vec<ComponentSlotData>,
    pub children:   Vec<ActorData>,
    /// アクターのアクティブフラグ（Unity の activeSelf 相当）。
    /// false のとき自身と全子孫が描画・更新・物理の対象から外れる。
    /// 既存ファイルとの互換性のため省略時は true、true の場合は書き出さない。
    #[serde(default = "default_true", skip_serializing_if = "is_true")]
    pub active: bool,
    /// プレハブ参照リンク（アセット相対 `assets://` パス）。Unity のプレハブに相当する。
    /// このアクターが `.actor` / `.actor2d` ファイルのインスタンスである場合に参照元のパスを保持する。
    /// **インスタンスのルートのみが Some を持ち、子アクターは常に None**。
    /// シーンロード時・`.actor` 保存時に、このパスの参照先ファイルからサブツリーを再展開する
    /// （ルートの Transform / name / active / world_line は維持し、それ以外はファイル内容で置換）。
    /// 参照先ファイルが無い／読めない場合はシーン保存値のまま（リンク維持）。
    /// 旧 `.scene` との互換性のため省略可（省略時 None）、None のときは書き出さない。
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub prefab_source: Option<String>,
}

/// actor_kind が Actor3D（デフォルト）の場合は JSON に書き出さない。
fn is_actor3d(k: &ActorKind) -> bool { *k == ActorKind::Actor3D }

/// serde 用: デフォルト true / true なら省略（active / enabled フラグの互換性維持）。
fn default_true() -> bool { true }
fn is_true(v: &bool) -> bool { *v }

/// ComponentSlot のシリアライズ用データ。
#[derive(Clone, Serialize, Deserialize)]
pub struct ComponentSlotData {
    pub name:      String,
    pub component: ComponentData,
    /// コンポーネントの有効フラグ。省略時は true、true の場合は書き出さない。
    #[serde(default = "default_true", skip_serializing_if = "is_true")]
    pub enabled: bool,
}

// ─── Actor ────────────────────────────────────────────────────────────────────

/// ECS エンティティのラッパー。
///
/// - 実データは World に格納（コンポーネント検索は world.get::<T>(actor.entity)）
/// - ヒエラルキーはこの構造体が所有（children: Vec<Actor>）
/// - DFS 順の計算・世界線フィルタに必要な情報はフィールドで保持
pub struct Actor {
    /// ECS エンティティ ID（World でのコンポーネント検索キー）
    pub entity:     Entity,
    /// ヒエラルキー表示名
    pub name:       String,
    /// 所属世界線（0=通常シーン, N=アクター編集タブ）
    pub world_line: u32,
    /// Actor の種別（3D / 2D）
    pub actor_kind: ActorKind,
    /// 子 Actor（順序を保持しているため DFS が確定的）
    pub children:   Vec<Actor>,
    /// アクティブフラグ（Unity の activeSelf 相当）。
    /// 自身が true でも祖先が false なら実効的に非アクティブになる
    /// （実効判定は collect_inactive_actor_entities で集合として計算する）。
    pub active:     bool,
    /// プレハブ参照リンク（アセット相対 `assets://` パス）。ActorData の同名フィールドと対応する。
    /// **インスタンスのルートのみ Some**（子アクターは常に None）。
    /// build_actor で ActorData から復元し、to_data で書き戻す。
    pub prefab_source: Option<String>,
    /// 保持コンポーネントの目録（実データは World）
    slots:          Vec<ComponentSlot>,
}

impl Actor {
    pub fn new(entity: Entity, name: impl Into<String>) -> Self {
        Self {
            entity,
            name:       name.into(),
            world_line: 0,
            actor_kind: ActorKind::Actor3D,
            children:   Vec::new(),
            active:     true,
            prefab_source: None,
            slots:      Vec::new(),
        }
    }

    /// 2D Actor として生成するヘルパー。
    pub fn new_2d(entity: Entity, name: impl Into<String>) -> Self {
        Self {
            entity,
            name:       name.into(),
            world_line: 0,
            actor_kind: ActorKind::Actor2D,
            children:   Vec::new(),
            active:     true,
            prefab_source: None,
            slots:      Vec::new(),
        }
    }

    pub fn with_name(name: impl Into<String>) -> Self {
        // entity は World::spawn() で払い出す。ここでは Default を使う。
        // 実際の使用では Scene::spawn_actor() を経由することを推奨。
        Self::new(Entity::default(), name)
    }

    // ─── コンポーネント目録 ────────────────────────────────────

    /// 指定型のコンポーネントスロットを持つか確認する。
    pub fn has_component<T: 'static>(&self) -> bool {
        self.slots.iter().any(|s| s.is::<T>())
    }

    /// 指定 ComponentKind のスロットを持つか確認する。
    pub fn has_kind(&self, kind: ComponentKind) -> bool {
        self.slots.iter().any(|s| s.kind == kind)
    }

    /// スロット目録（不変）
    pub fn slots(&self) -> &[ComponentSlot] { &self.slots }

    /// スロット目録（可変）— Undo 復元など内部操作用
    pub fn slots_mut(&mut self) -> &mut Vec<ComponentSlot> { &mut self.slots }

    /// スロットを追加する（World への insert と entity spawn は呼び出し元が行う）。
    pub fn add_slot(&mut self, name: impl Into<String>, kind: ComponentKind, type_id: TypeId, entity: Entity) {
        self.slots.push(ComponentSlot { name: name.into(), kind, type_id, entity, enabled: true });
    }

    /// 型パラメータ付きスロット追加ヘルパー。
    /// entity はスロット専用の ECS エンティティ（呼び出し元で world.spawn() して渡す）。
    pub fn add_slot_typed<T: crate::engine::ecs::Component + 'static>(
        &mut self,
        name:   impl Into<String>,
        kind:   ComponentKind,
        entity: Entity,
    ) {
        self.slots.push(ComponentSlot::new::<T>(name, kind, entity));
    }

    /// 指定 ComponentKind の最初のスロットが使う ECS エンティティを返す。
    pub fn first_slot_entity_of_kind(&self, kind: ComponentKind) -> Option<Entity> {
        self.slots.iter().find(|s| s.kind == kind).map(|s| s.entity)
    }

    /// ModelComponent スロットの entity を返す（後方互換ヘルパー）。
    /// 複数 ModelComponent スロットがある場合は最初のものを返す。
    pub fn mc_entity(&self) -> Option<Entity> {
        self.first_slot_entity_of_kind(ComponentKind::Model)
    }

    /// slot_i 番目（Model スロット内の連番）の ModelComponent entity を返す。
    /// ピッキング後の選択スロット特定に使用する。
    pub fn mc_entity_at(&self, slot_i: usize) -> Option<Entity> {
        self.slots.iter()
            .filter(|s| s.kind == ComponentKind::Model)
            .nth(slot_i)
            .map(|s| s.entity)
    }

    /// 全スロットの entity をイテレートする（despawn・収集用）。
    pub fn slot_entities(&self) -> impl Iterator<Item = Entity> + '_ {
        self.slots.iter().map(|s| s.entity)
    }

    /// インデックス指定でスロットを削除する（World からの remove は呼び出し元が行う）。
    pub fn remove_slot_at(&mut self, idx: usize) {
        if idx < self.slots.len() { self.slots.remove(idx); }
    }

    /// スロット目録をまるごと置き換える（Undo/Redo 用）。
    pub fn replace_slots(&mut self, slots: Vec<ComponentSlot>) { self.slots = slots; }

    // ─── 親子関係 ─────────────────────────────────────────────

    pub fn add_child(&mut self, child: Actor) { self.children.push(child); }
    pub fn children(&self)     -> &[Actor]         { &self.children }
    pub fn children_mut(&mut self) -> &mut Vec<Actor> { &mut self.children }

    // ─── world_line 伝播 ──────────────────────────────────────

    /// world_line を自身と全子孫に再帰的に設定する（Undo 復元・OpenActor 用）。
    pub fn set_world_line_recursive(&mut self, wl: u32) {
        self.world_line = wl;
        for child in &mut self.children { child.set_world_line_recursive(wl); }
    }

    // ─── シリアライズ ─────────────────────────────────────────

    /// World を参照してシリアライズ用データを生成する。
    pub fn to_data(&self, world: &World) -> ActorData {
        self.to_data_recursive(world, &mut None)
    }

    /// DFS ID カウンタを伴う再帰的なシリアライズ。
    pub fn to_data_recursive(&self, world: &World, counter: &mut Option<u32>) -> ActorData {
        let dfs_id = if let Some(c) = counter {
            let id = *c;
            *c += 1;
            Some(id)
        } else { None };

        let transform        = world.get::<Transform>(self.entity).cloned();
        let canvas_transform = world.get::<CanvasTransform>(self.entity).cloned();
        let components = self.slots.iter().filter_map(|slot| {
            let data = match slot.kind {
                ComponentKind::Model => {
                    world.get::<ModelComponent>(slot.entity)
                        .map(|mc| ComponentData::ModelComponent(mc.to_data()))
                }
                ComponentKind::Script => {
                    world.get::<ScriptComponent>(slot.entity)
                        .map(|sc| ComponentData::ScriptComponent(sc.to_data()))
                }
                ComponentKind::Placeholder => {
                    world.get::<PlaceholderScriptSlot>(slot.entity)
                        .map(|ps| ComponentData::ScriptComponent(ps.to_data()))
                }
                ComponentKind::Canvas => {
                    world.get::<CanvasComponent>(slot.entity)
                        .map(|cc| ComponentData::CanvasComponent(cc.to_data()))
                }
                ComponentKind::Sprite => {
                    world.get::<SpriteComponent>(slot.entity)
                        .map(|sc| ComponentData::SpriteComponent(sc.to_data()))
                }
                ComponentKind::InputMap => {
                    world.get::<InputMapComponent>(slot.entity)
                        .map(|ic| ComponentData::InputMapComponent(ic.to_data()))
                }
                ComponentKind::Camera => {
                    world.get::<CameraComponent>(slot.entity)
                        .map(|cc| ComponentData::CameraComponent(cc.to_data()))
                }
                ComponentKind::Plugin => {
                    world.get::<crate::engine::components::PluginComponent>(slot.entity)
                        .map(|pc| pc.clone())
                        .map(ComponentData::PluginComponent)
                }
                ComponentKind::Collider => {
                    world.get::<ColliderComponent>(slot.entity)
                        .map(|cc| ComponentData::ColliderComponent(ColliderComponentData::from(cc)))
                }
                ComponentKind::Collider2d => {
                    // 2D コライダーコンポーネントをシリアライズ用データに変換する
                    world.get::<crate::engine::components::Collider2dComponent>(slot.entity)
                        .map(|cc| ComponentData::Collider2dComponent(crate::engine::components::Collider2dComponentData::from(cc)))
                }
                ComponentKind::Audio => {
                    world.get::<crate::engine::components::AudioComponent>(slot.entity)
                        .map(|ac| ComponentData::AudioComponent(ac.to_data()))
                }
                ComponentKind::Animator => {
                    world.get::<crate::engine::components::AnimatorComponent>(slot.entity)
                        .map(|an| ComponentData::AnimatorComponent(an.to_data()))
                }
            };
            data.map(|d| ComponentSlotData {
                name:      slot.name.clone(),
                component: d,
                enabled:   slot.enabled,
            })
        }).collect();

        ActorData {
            name:             self.name.clone(),
            dfs_id,
            actor_kind:       self.actor_kind,
            transform,
            canvas_transform,
            components,
            children:         self.children.iter().map(|c| c.to_data_recursive(world, counter)).collect(),
            active:           self.active,
            // プレハブ参照リンクを往復させる（ルートのみ Some、子は None）。
            prefab_source:    self.prefab_source.clone(),
        }
    }

    /// 2D Actor かどうかを返す。
    pub fn is_2d(&self) -> bool { self.actor_kind == ActorKind::Actor2D }
}

impl Default for Actor {
    fn default() -> Self { Self::with_name("Actor") }
}
