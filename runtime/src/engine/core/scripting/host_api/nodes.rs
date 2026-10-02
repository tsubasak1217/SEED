// ============================================================
//  scripting/host_api/nodes.rs — 動的ノード API の FFI（スクリプトから木を組み立てる。docs/scripting_api.md §7「動的ノード」）
//
//  C# の GameObject.ChildCount / GetChild / Children / SiblingIndex（読み）、SetSiblingIndex / SetAsFirstSibling /
//  SetAsLastSibling・Create / Create2D / Create3D・AddComponent<T> / RemoveComponent<T> / AddScript<T>（書き）の入口。
//  host_api の子モジュールなので、World・Actor ツリーの公開ポインタ（WORLD_PTR / ACTORS_PTR）と遅延コマンドの
//  待ち行列（SCENE_COMMANDS）をそのまま使う（host_api.rs は 4000 行を超えるので関数群はこちらへ分けた）。
//
//  【その場で行うこと・フレーム末尾に回すこと】（Instantiate / SetParent と同じモデル）
//    - 読み（子・兄弟の順番）: その場で Actor ツリーを読む。同じフレームに発行した生成・並べ替え・付け替え・破棄は
//      まだ反映されていない（GameObject.Parent と同じ）。
//    - Create: ルートのエンティティをその場で予約して Transform / CanvasTransform を入れ（同じフレームに位置などを
//      書ける）、アクタの構築と親への取り付けはフレーム末尾（ScriptSceneCommand::CreateActor）。
//    - AddComponent: スロット専用のエンティティとコンポーネントをその場で World へ入れる（World は可変で公開されて
//      いるので、戻り値のハンドルへ同じフレームに書ける）。スロットの目録への登録はフレーム末尾（AttachSlot）。
//      その間の GetComponent / HasComponent は node_pending.rs の保留の表を引く。
//    - RemoveComponent: 取り外しの予約（node_pending の表から隠す）。後始末（コンポーネントの除去・despawn・目録から外す）は
//      フレーム末尾に、エディタの「コンポーネント削除」と同じ関数で行う（RemoveSlot）。
//    - SetSiblingIndex: フレーム末尾（SetSiblingIndex）。並べ方の計算は node_tree.rs。
//    - AddScript: フレーム末尾にスクリプトのスロットを足し、通常の構築経路（ScriptComponent::new）で CLR のインスタンスを
//      作る。OnStart は次のフレームの BeginFrame で走る（AddScript）。
//  OnDestroy の中からの呼び出しは、生成・破棄と同じく受けない（解体中の木を触らない）。
// ============================================================

use crate::engine::components::{CanvasTransform, ComponentKind, Transform};
use crate::engine::core::scripting::{name_pending, node_pending, node_tree};
use crate::engine::ecs::Entity;
use crate::engine::structs::objects::Actor;

use super::component_kinds::{addable_kind_of, insert_default_slot};
use super::{
    actor_of_entity, is_in_on_destroy, is_pending_spawn, resolve_component_slot, str_from, ScriptSceneCommand,
    ACTORS_PTR, SCENE_COMMANDS, WORLD_PTR,
};

// ── 読みの op（C# の ScriptHost.NodeQuery* と一致させる）────────────────

/// 論理の子の数（戻り値 = 数。対象が無ければ -1）。
pub(super) const NODE_QUERY_CHILD_COUNT: i32 = 0;
/// arg 番目の論理の子（out へ [index, generation]。戻り値 1 / 無ければ 0）。
pub(super) const NODE_QUERY_CHILD_AT: i32 = 1;
/// 論理の子の一覧（戻り値 = 数。2 × 数 ≦ cap のときだけ out へ [index, generation] の組を書く。対象が無ければ -1）。
pub(super) const NODE_QUERY_CHILDREN: i32 = 2;
/// 論理の兄弟の中の順番（戻り値 = 順番。無い・フォルダ・まだ構築していなければ -1）。
pub(super) const NODE_QUERY_SIBLING_INDEX: i32 = 3;

/// 失敗・対象なしの戻り値。
const NODE_NOT_FOUND: i32 = -1;
/// 受けた（成功）の戻り値。
const NODE_OK: i32 = 1;
/// 断った（失敗）の戻り値。
const NODE_REFUSED: i32 = 0;
/// out へ書くエンティティ 1 つの要素数（index, generation）。
const ENTITY_WORDS: usize = 2;

// ── 生成の種別（C# の ScriptHost.NodeCreate* と一致させる）───────────────

/// 親から推定する（2D の親・Canvas を持つ親の下は 2D、それ以外と親なしは 3D）。
pub(super) const NODE_CREATE_AUTO: i32 = 0;
/// 3D（Transform）。
pub(super) const NODE_CREATE_3D: i32 = 1;
/// 2D（CanvasTransform。親相対・pivot 0・大きさ 0）。
pub(super) const NODE_CREATE_2D: i32 = 2;

/// 名前が空のときの既定の名前（エディタの handle_add_actor / handle_add_actor_2d と同じ）。
const DEFAULT_ACTOR_NAME: &str = "Actor";

// ── 兄弟の順番（C# の ScriptHost.SiblingLast と一致させる）──────────────

/// SetSiblingIndex の「末尾」（SetAsLastSibling）。
pub(super) const SIBLING_LAST: i32 = -1;

// ── コンポーネントの op（C# の ScriptHost.NodeComponent* と一致させる）────────

/// AddComponent（name = C# の ComponentKindName。out へスロットのエンティティ）。
pub(super) const NODE_COMPONENT_ADD: i32 = 0;
/// RemoveComponent（name = ComponentKindName、index = 同種の中の番号）。
pub(super) const NODE_COMPONENT_REMOVE: i32 = 1;
/// AddScript（name = スクリプトの型名。フレーム末尾に作る）。
pub(super) const NODE_COMPONENT_ADD_SCRIPT: i32 = 2;

// ─── 共通 ─────────────────────────────────────────────────────

/// エンティティを out の 2 要素へ書く。
///
/// # Safety
/// `out` は少なくとも `ENTITY_WORDS` 要素書ける領域であること（呼び出し側が null と容量を確かめる）。
unsafe fn write_entity(out: *mut u32, entity: Entity) {
    // SAFETY: 呼び出し側が out を ENTITY_WORDS 要素書ける領域だと確かめている
    unsafe {
        *out = entity.index();
        *out.add(1) = entity.generation();
    }
}

/// アクタのルートとして生きているか（Transform か CanvasTransform を持つ。ffi_destroy・ffi_set_parent と同じ規約）。
fn is_live_root(entity: Entity) -> bool {
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() {
        return false;
    }
    // SAFETY: WORLD_PTR は with_world のスコープ内でだけ非 null（スクリプトのフェーズ中）
    let world = unsafe { &*ptr };
    world.get::<Transform>(entity).is_some() || world.get::<CanvasTransform>(entity).is_some()
}

/// アクタのルートか（木に載っているか、このフレームに生成を予約したもの）。スロットのエンティティを弾く。
fn is_actor_root(entity: Entity) -> bool {
    actor_of_entity(entity).is_some() || is_pending_spawn(entity)
}

/// 遅延コマンドを積む。
fn push_command(cmd: ScriptSceneCommand) {
    SCENE_COMMANDS.with(|q| q.borrow_mut().push(cmd));
}

// ─── 読み（子・兄弟の順番）──────────────────────────────────────

/// 子と兄弟の順番を読む（op は NODE_QUERY_*）。戻り値は op ごと（冒頭の定数の説明）。
///
/// # 引数
/// * `op` / `idx` / `generation` - 操作と対象のルートエンティティ
/// * `arg` - CHILD_AT の番号（ほかは使わない）
/// * `out` / `cap` - 書き込み先と容量（u32 の要素数）
pub(super) unsafe extern "system" fn ffi_node_query(
    op: i32,
    idx: u32,
    generation: u32,
    arg: i32,
    out: *mut u32,
    cap: i32,
) -> i32 {
    let actors_ptr = ACTORS_PTR.with(|p| p.get());
    if actors_ptr.is_null() {
        return NODE_NOT_FOUND;
    }
    // SAFETY: ACTORS_PTR はスクリプトのフェーズ中だけ非 null で、その間ツリーは構造不変
    let actors: &[Actor] = unsafe { &*actors_ptr };
    let entity = Entity::from_raw(idx, generation);

    match op {
        NODE_QUERY_SIBLING_INDEX => node_tree::logical_place(actors, entity)
            .map_or(NODE_NOT_FOUND, |place| place.index as i32),
        NODE_QUERY_CHILD_COUNT | NODE_QUERY_CHILD_AT | NODE_QUERY_CHILDREN => {
            // 木に載っていない（このフレームに生成したばかり）なら子は無い。知らないエンティティは -1
            let Some(actor) = actor_of_entity(entity) else {
                return if is_pending_spawn(entity) { pending_children_answer(op) } else { NODE_NOT_FOUND };
            };
            let children = node_tree::logical_children(actor.children());
            match op {
                NODE_QUERY_CHILD_COUNT => children.len() as i32,
                NODE_QUERY_CHILD_AT => {
                    let Some(child) = usize::try_from(arg).ok().and_then(|i| children.get(i)) else {
                        return NODE_REFUSED;
                    };
                    if out.is_null() || cap < ENTITY_WORDS as i32 {
                        return NODE_REFUSED;
                    }
                    // SAFETY: out は null でなく容量 ENTITY_WORDS 以上（直前で確かめた）
                    unsafe { write_entity(out, child.entity) };
                    NODE_OK
                }
                _ => {
                    // 一覧: 収まるときだけ書く（足りなければ数だけ返す＝呼び出し側が確保し直す）
                    let needed = children.len() * ENTITY_WORDS;
                    if !out.is_null() && cap >= 0 && needed <= cap as usize {
                        for (i, child) in children.iter().enumerate() {
                            // SAFETY: needed（= 数 × ENTITY_WORDS）≦ cap を確かめたので範囲内
                            unsafe { write_entity(out.add(i * ENTITY_WORDS), child.entity) };
                        }
                    }
                    children.len() as i32
                }
            }
        }
        _ => NODE_NOT_FOUND,
    }
}

/// このフレームに生成したばかり（木に未構築）のノードへの子の問い合わせの答え（子は無い）。
fn pending_children_answer(op: i32) -> i32 {
    match op {
        NODE_QUERY_CHILD_AT => NODE_REFUSED,
        _ => 0,
    }
}

// ─── 生成（Create / Create2D / Create3D）────────────────────────

/// 空のアクタを作る（ルートをその場で予約し、構築と親への取り付けはフレーム末尾）。成功 1 / 失敗 0。
///
/// # 引数
/// * `name` / `name_len` - 名前（空なら "Actor"）
/// * `parent_idx` / `parent_generation` / `has_parent` - 親（has_parent = 0 ならシーンのルート）
/// * `kind` - NODE_CREATE_*（自動・3D・2D）
/// * `out` - 予約したルートのエンティティ（2 要素）
pub(super) unsafe extern "system" fn ffi_node_create(
    name: *const u8,
    name_len: i32,
    parent_idx: u32,
    parent_generation: u32,
    has_parent: i32,
    kind: i32,
    out: *mut u32,
) -> i32 {
    if is_in_on_destroy() {
        return NODE_REFUSED;
    }
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() || out.is_null() {
        return NODE_REFUSED;
    }
    let parent = (has_parent != 0).then(|| Entity::from_raw(parent_idx, parent_generation));
    let is_2d = match kind {
        NODE_CREATE_3D => false,
        NODE_CREATE_2D => true,
        NODE_CREATE_AUTO => infer_2d_from_parent(parent),
        _ => return NODE_REFUSED,
    };
    // SAFETY: C# が渡した (ポインタ, 長さ) の UTF-8（str_from は null・負の長さを空文字にする）
    let raw_name = unsafe { str_from(name, name_len) };
    let name = if raw_name.is_empty() { DEFAULT_ACTOR_NAME } else { raw_name };

    // ルートのエンティティを予約し、既定の変換を入れる（同じフレームに位置などを書ける）
    // SAFETY: WORLD_PTR はスクリプトのフェーズ中だけ非 null で、その間 Rust 側は World への参照を保持しない
    let world = unsafe { &mut *ptr };
    let entity = world.spawn();
    if is_2d {
        // 親相対・pivot 0・anchor 0・大きさは持たない（エディタの handle_add_actor_2d と同じ既定値）
        world.insert(entity, CanvasTransform::default());
    } else {
        world.insert(entity, Transform::default());
    }
    // 同じフレームの Name の読みが付けた名前を返すように（構築はフレーム末尾。name_pending は取り出しで空になる）
    name_pending::set(entity, name);
    push_command(ScriptSceneCommand::CreateActor { entity, name: name.to_string(), parent, is_2d });
    // SAFETY: out は null でない（冒頭で確かめた）。C# は 2 要素の領域を渡す
    unsafe { write_entity(out, entity) };
    NODE_OK
}

/// 親から 2D / 3D を推定する（2D の親・Canvas を持つ 3D の親の下は 2D、それ以外と親なしは 3D）。
fn infer_2d_from_parent(parent: Option<Entity>) -> bool {
    let Some(p) = parent else { return false };
    let ptr = WORLD_PTR.with(|w| w.get());
    if ptr.is_null() {
        return false;
    }
    // SAFETY: スクリプトのフェーズ中だけ非 null
    let world = unsafe { &*ptr };
    // 2D の親（このフレームに作った親も、予約のときに CanvasTransform を入れてあるので分かる）
    if world.get::<CanvasTransform>(p).is_some() {
        return true;
    }
    // Canvas を持つ 3D の親（2D の子だけを置ける UI の根）
    actor_of_entity(p).is_some_and(|a| a.has_kind(ComponentKind::Canvas))
}

// ─── 兄弟の順番（SetSiblingIndex / SetAsFirstSibling / SetAsLastSibling）────

/// 兄弟の順番を変える（フレーム末尾に当てる）。受けた 1 / 断った 0。
///
/// # 引数
/// * `idx` / `generation` - 対象のルートエンティティ
/// * `index` - 0 以上 = その順番（兄弟の数以上なら末尾）、SIBLING_LAST = 末尾
pub(super) unsafe extern "system" fn ffi_node_set_sibling(idx: u32, generation: u32, index: i32) -> i32 {
    if is_in_on_destroy() {
        return NODE_REFUSED;
    }
    let entity = Entity::from_raw(idx, generation);
    if !is_live_root(entity) {
        return NODE_REFUSED;
    }
    let index = match index {
        SIBLING_LAST => None,
        i if i >= 0 => Some(i as usize),
        _ => return NODE_REFUSED,
    };
    push_command(ScriptSceneCommand::SetSiblingIndex { entity, index });
    NODE_OK
}

// ─── コンポーネント（AddComponent / RemoveComponent / AddScript）──────────

/// コンポーネント・スクリプトを足す・外す（op は NODE_COMPONENT_*）。戻り値は成功 1 / 失敗 0。
///
/// # 引数
/// * `op` / `idx` / `generation` - 操作と対象のアクタのルートエンティティ
/// * `name` / `name_len` - C# の ComponentKindName（AddScript ではスクリプトの型名）
/// * `index` - RemoveComponent の同種の中の番号
/// * `out` - AddComponent が足したスロットのエンティティ（2 要素）
pub(super) unsafe extern "system" fn ffi_node_component(
    op: i32,
    idx: u32,
    generation: u32,
    name: *const u8,
    name_len: i32,
    index: i32,
    out: *mut u32,
) -> i32 {
    if is_in_on_destroy() {
        return NODE_REFUSED;
    }
    let root = Entity::from_raw(idx, generation);
    // スロットのエンティティ・破棄済み・知らないエンティティを弾く
    if !is_live_root(root) || !is_actor_root(root) {
        return NODE_REFUSED;
    }
    // SAFETY: C# が渡した (ポインタ, 長さ) の UTF-8
    let name = unsafe { str_from(name, name_len) };
    if name.is_empty() {
        return NODE_REFUSED;
    }
    match op {
        // SAFETY: out は C# が渡した 2 要素の領域（add_component が null を確かめる）
        NODE_COMPONENT_ADD => unsafe { add_component(root, name, out) },
        NODE_COMPONENT_REMOVE => remove_component(root, name, index),
        NODE_COMPONENT_ADD_SCRIPT => {
            push_command(ScriptSceneCommand::AddScript { root, type_name: name.to_string() });
            NODE_OK
        }
        _ => NODE_REFUSED,
    }
}

/// AddComponent の本体（その場で World へ入れ、目録への登録はフレーム末尾）。
unsafe fn add_component(root: Entity, kind_name: &str, out: *mut u32) -> i32 {
    let Some(kind) = addable_kind_of(kind_name) else { return NODE_REFUSED };
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() || out.is_null() {
        return NODE_REFUSED;
    }
    // SAFETY: WORLD_PTR はスクリプトのフェーズ中だけ非 null
    let world = unsafe { &mut *ptr };
    let slot_entity = world.spawn();
    // スロット名は C# の種別名（"Sprite"・"Text" …。GetComponent<T>(name) で引ける）
    let Some(slot) = insert_default_slot(world, slot_entity, kind, kind_name) else {
        world.despawn(slot_entity);
        return NODE_REFUSED;
    };
    node_pending::add_slot(root, slot_entity, kind_name);
    push_command(ScriptSceneCommand::AttachSlot { root, slot });
    // SAFETY: out は null でない（冒頭で確かめた）
    unsafe { write_entity(out, slot_entity) };
    NODE_OK
}

/// RemoveComponent の本体（取り外しを予約し、後始末はフレーム末尾）。
fn remove_component(root: Entity, kind_name: &str, index: i32) -> i32 {
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() {
        return NODE_REFUSED;
    }
    // SAFETY: スクリプトのフェーズ中だけ非 null
    let world = unsafe { &*ptr };
    let Some(slot_entity) = resolve_component_slot(world, root, kind_name, None, index) else {
        return NODE_REFUSED;
    };
    // Transform / CanvasTransform（ルート直付け）は外せない
    if slot_entity == root {
        return NODE_REFUSED;
    }
    node_pending::mark_removed(slot_entity);
    push_command(ScriptSceneCommand::RemoveSlot { root, slot_entity });
    NODE_OK
}
