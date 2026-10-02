// ============================================================
//  prefab_live_patch/apply.rs — 計画を動いているアクタの木へ当てる（World を書き換える）
//
//  plan.rs が立てた `NodePlan` に従って、1 個のインスタンスの部分木を「その場で」書き換える。
//  ここでの約束（docs/editor_prefab.md 8 章）:
//   - 既存のエンティティは作り直さない。スロットの値は `field_edit::apply_component_data_in_place`
//     （Undo の復元と同じ関数）でその場で差し替える。`NeedsRebuild` が返ったスロットだけ、そのスロットを作り直す。
//   - スクリプトのスロットは計画が「作り直す」と決めたノードだけ触る（消す → 作る。消すときは OnDestroy が走り、
//     作ったものは次のフレームで OnStart が走る）。それ以外のスクリプトは CLR も [SerializeField] も触らない。
//   - 値の合成は 3 方向（元の版があるとき。merge.rs）／2 方向（無いとき。ファイルの値を当てる）。
//     値が今と同じなら何もしない（アニメーター・音などの実行中の状態を作り直さないため）。
//   - 根の名前・active・visible・Transform / CanvasTransform は触らない（配置側の持ち物）。
//   - 3D の子の Transform はワールド空間で持つため当てない（新しく作るノードだけ根の配置へ移す）。
// ============================================================

use std::sync::Arc;

use serde_json::Value;

use crate::engine::components::{CanvasTransform, ComponentKind, Transform};
use crate::engine::core::app_base::scene::build_actor;
use crate::engine::core::scripting::ScriptingHost;
use crate::engine::ecs::World;
use crate::engine::methods::drawer::DrawContext;
use crate::engine::methods::gizmo_interact::mat4x4_mul;
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::{slot_to_data, ActorData, ActorKind, ComponentSlot, ComponentSlotData};

use super::super::field_edit::{apply_component_data_in_place, SlotApply};
use super::super::{apply_delta_to_actor_subtree, despawn_actor_recursive};
use super::merge::{keep_live_keys, merge3};
use super::plan::{ChildStep, NodePlan, SlotPatch};

/// 3D モデルのスロットで、ファイルの値を当てずに動いている値を残す欄
/// （インスタンス行列はワールド空間で持つため、原点基準のファイルの値を当てると位置が飛ぶ）。
const MODEL_LIVE_KEYS: &[&str] = &["instances", "meta", "groups", "next_group_id"];

/// `ComponentData` の JSON で中身が入っている欄の名前（`#[serde(tag = "type", content = "data")]`）。
const COMPONENT_DATA_KEY: &str = "data";

/// スロットを 1 個だけ組み立てるための仮のアクタの名前（ログに出たときに出所が分かるように）。
const SLOT_BUILDER_NAME: &str = "__prefab_live_patch_slot__";

/// 当て直しに要る借用の束。
pub(crate) struct PatchContext<'a> {
    /// シーンの World。
    pub world: &'a mut World,
    /// 描画コンテキスト（モデル・スロットの組み立てに要る）。
    pub ctx: &'a DrawContext,
    /// スクリプトのホスト（無ければスクリプトは Placeholder で組み立てる）。
    pub host: Option<&'a Arc<ScriptingHost>>,
    /// 当てる先の世界線（Play のシーン＝0）。新しく作ったノードへ伝える。
    pub world_line: u32,
    /// 3D の根の配置行列 × ファイルの根の逆行列。新しく作る 3D ノードを配置先へ移すのに使う（2D・特異なら None）。
    pub delta_3d: Option<[[f32; 4]; 4]>,
}

/// 当て直しの集計（ログ・応答用）。
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub(crate) struct PatchStats {
    /// 値をその場で差し替えたスロット数。
    pub slots_patched: usize,
    /// その場で差し替えられず作り直したスロット数（`NeedsRebuild`）。
    pub slots_rebuilt: usize,
    /// 新しく作ったスロット数（作り直したスクリプトを含む）。
    pub slots_created: usize,
    /// 破棄したスロット数（作り直したスクリプトを含む）。
    pub slots_removed: usize,
    /// スクリプトを作り直したノード数。
    pub script_nodes_rebuilt: usize,
    /// 新しく作ったノード数（部分木の根の数）。
    pub nodes_created: usize,
    /// 破棄したノード数（部分木の根の数）。
    pub nodes_removed: usize,
    /// 形が変わって作り直したノード数。
    pub nodes_replaced: usize,
    /// active / visible / CanvasTransform を揃えたノード数。
    pub nodes_updated: usize,
}

/// 計画を 1 ノード（とその子）へ当てる。
///
/// * `actor`   … 動いているノード
/// * `plan`    … このノードの計画（plan.rs）
/// * `base`    … このノードに対応する元の版のノード（無ければ 2 方向）
/// * `new`     … このノードに対応する新しい版のノード
/// * `is_root` … インスタンスの根か（根は配置側の値を守る）
pub(crate) fn apply_node(
    actor: &mut Actor,
    plan: NodePlan,
    base: Option<&ActorData>,
    new: &ActorData,
    is_root: bool,
    pc: &mut PatchContext<'_>,
    stats: &mut PatchStats,
) {
    // ── ノードの値（active / visible / 2D の CanvasTransform）──
    let mut node_changed = false;
    if let Some(active) = plan.set_active {
        actor.active = active;
        node_changed = true;
    }
    if let Some(visible) = plan.set_visible {
        actor.visible = visible;
        node_changed = true;
    }
    if !is_root && patch_canvas_transform(actor, base, new, pc.world) {
        node_changed = true;
    }
    if node_changed {
        stats.nodes_updated += 1;
    }

    // ── スロット ──
    apply_slots(actor, &plan, base, new, pc, stats);

    // ── 子 ──
    apply_children(actor, plan, base, new, pc, stats);
}

// ── ノードの値 ─────────────────────────────────────────────────

/// 2D の子ノードの CanvasTransform を当てる（変わったら true）。
///
/// 3D の Transform はワールド空間で持つため当てない（モジュール冒頭のコメント）。
/// 2D のフォルダは常に単位変換（scene.rs の setup_actor_root_transform）なので当てない。
fn patch_canvas_transform(actor: &Actor, base: Option<&ActorData>, new: &ActorData, world: &mut World) -> bool {
    if actor.actor_kind != ActorKind::Actor2D || actor.is_folder() {
        return false;
    }
    let (Some(new_ct), Some(live_ct)) = (new.canvas_transform.as_ref(), world.get::<CanvasTransform>(actor.entity)) else {
        return false;
    };
    let (Ok(new_json), Ok(live_json)) = (serde_json::to_value(new_ct), serde_json::to_value(live_ct)) else {
        return false;
    };
    let merged = match base.and_then(|b| b.canvas_transform.as_ref()).and_then(|b| serde_json::to_value(b).ok()) {
        Some(base_json) => merge3(&base_json, &new_json, &live_json),
        None => new_json,
    };
    if merged == live_json {
        return false;
    }
    match serde_json::from_value::<CanvasTransform>(merged) {
        Ok(ct) => {
            world.insert(actor.entity, ct);
            true
        }
        Err(e) => {
            eprintln!("[PrefabLivePatch] CanvasTransform を合成できませんでした（{}）: {e}", actor.name);
            false
        }
    }
}

// ── スロット ───────────────────────────────────────────────────

/// スロットの計画を当てる（その場の当て直し → 破棄 → 作成の順。添字は当て直しの間は変わらない）。
fn apply_slots(
    actor: &mut Actor,
    plan: &NodePlan,
    base: Option<&ActorData>,
    new: &ActorData,
    pc: &mut PatchContext<'_>,
    stats: &mut PatchStats,
) {
    // 1) その場の当て直し（スクリプト以外）
    for patch in &plan.slots.patches {
        patch_slot(actor, patch, base, new, pc, stats);
    }

    // 2) 破棄（後ろから。スクリプトは World から外れたときに OnDestroy が走る）
    for &live_idx in plan.slots.removed.iter().rev() {
        let Some(slot) = actor.slots().get(live_idx) else { continue };
        pc.world.despawn(slot.entity);
        actor.remove_slot_at(live_idx);
        stats.slots_removed += 1;
    }

    // 3) 作成（ファイルの並びで末尾へ。スクリプトは次のフレームで OnStart が走る）
    for &data_idx in &plan.slots.created {
        let Some(slot_data) = new.components.get(data_idx) else { continue };
        if let Some(slot) = build_single_slot(slot_data.clone(), actor.actor_kind, pc) {
            actor.slots_mut().push(slot);
            stats.slots_created += 1;
        }
    }
    if plan.slots.scripts_rebuilt {
        stats.script_nodes_rebuilt += 1;
    }
}

/// スロット 1 個をその場で当て直す。
fn patch_slot(
    actor: &mut Actor,
    patch: &SlotPatch,
    base: Option<&ActorData>,
    new: &ActorData,
    pc: &mut PatchContext<'_>,
    stats: &mut PatchStats,
) {
    let Some(new_slot) = new.components.get(patch.data) else { return };
    let base_slot = base.zip(patch.base).and_then(|(b, i)| b.components.get(i));
    let Some(slot) = actor.slots().get(patch.live) else { return };
    let (entity, kind) = (slot.entity, slot.kind);
    // World に実体が無いスロットは保存にも出ない（slot_to_data の規約）。当て直しようがないので飛ばす。
    let Some(live_slot) = slot_to_data(pc.world, slot) else { return };

    // ── 値（ComponentData）──
    if let Some(merged) = merged_component(kind, base_slot, new_slot, &live_slot) {
        match apply_component_data_in_place(pc.world, entity, &merged) {
            SlotApply::Applied => stats.slots_patched += 1,
            SlotApply::NeedsRebuild => {
                // GPU 資源・モデルの差し替えが要る: このスロットだけ作り直す（他のスロット・スクリプトは触らない）
                let data = ComponentSlotData { name: live_slot.name.clone(), component: merged, enabled: live_slot.enabled };
                if let Some(rebuilt) = build_single_slot(data, actor.actor_kind, pc) {
                    pc.world.despawn(entity);
                    actor.slots_mut()[patch.live] = rebuilt;
                    stats.slots_rebuilt += 1;
                }
            }
        }
    }

    // ── スロット名・有効フラグ（3 方向はファイルが変えたときだけ。2 方向はファイルの値）──
    let slot = &mut actor.slots_mut()[patch.live];
    let name_changed = base_slot.map_or(true, |b| b.name != new_slot.name);
    if name_changed && slot.name != new_slot.name {
        slot.name = new_slot.name.clone();
    }
    let enabled_changed = base_slot.map_or(true, |b| b.enabled != new_slot.enabled);
    if enabled_changed && slot.enabled != new_slot.enabled {
        slot.enabled = new_slot.enabled;
    }
}

/// 当て直し後のコンポーネントの値を作る。今の値と同じなら None（何もしない）。
fn merged_component(
    kind: ComponentKind,
    base: Option<&ComponentSlotData>,
    new: &ComponentSlotData,
    live: &ComponentSlotData,
) -> Option<crate::engine::components::ComponentData> {
    let live_json = serde_json::to_value(&live.component).ok()?;
    let new_json = serde_json::to_value(&new.component).ok()?;
    let mut merged = match base.and_then(|b| serde_json::to_value(&b.component).ok()) {
        Some(base_json) => merge3(&base_json, &new_json, &live_json),
        None => new_json,
    };
    if kind == ComponentKind::Model {
        // インスタンス行列はワールド空間（ファイルは原点基準）なので動いている値を残す
        let live_data = live_json.get(COMPONENT_DATA_KEY).cloned().unwrap_or(Value::Null);
        if let Some(data) = merged.get_mut(COMPONENT_DATA_KEY) {
            keep_live_keys(data, &live_data, MODEL_LIVE_KEYS);
        }
    }
    if merged == live_json {
        return None;
    }
    match serde_json::from_value(merged) {
        Ok(component) => Some(component),
        Err(e) => {
            eprintln!("[PrefabLivePatch] コンポーネントを合成できませんでした（{}）: {e}", new.name);
            None
        }
    }
}

/// スロット 1 個を組み立てる（`build_actor` に 1 スロットだけの仮のアクタを組ませて、スロットだけ取り出す）。
///
/// `build_actor` のスロットの組み立て（モデルの読み込み・スクリプトの生成・Placeholder への退避・
/// レイアウトの部品）をそのまま使うための手段。仮のアクタの本体のエンティティはすぐ捨てる。
/// スクリプトの持ち主（owner）は毎フェーズ木から引き直されるので、仮のアクタで作っても正しく付く。
fn build_single_slot(slot: ComponentSlotData, actor_kind: ActorKind, pc: &mut PatchContext<'_>) -> Option<ComponentSlot> {
    let temp = ActorData {
        name: SLOT_BUILDER_NAME.to_string(),
        dfs_id: None,
        actor_kind,
        transform: None,
        canvas_transform: None,
        is_folder: false,
        components: vec![slot],
        children: Vec::new(),
        active: true,
        visible: true,
        prefab_source: None,
        prefab_hash: None,
        scatter_prop_id: None,
        editor_preview: None,
    };
    let mut built = match build_actor(temp, pc.ctx, pc.world, pc.host, None) {
        Ok(actor) => actor,
        Err(e) => {
            eprintln!("[PrefabLivePatch] スロットを組み立てられませんでした: {e}");
            return None;
        }
    };
    let slot = built.slots_mut().pop();
    pc.world.despawn(built.entity);
    slot
}

// ── 子 ────────────────────────────────────────────────────────

/// 子の計画を当てる（破棄 → 計画の並びで組み直し）。
fn apply_children(
    actor: &mut Actor,
    plan: NodePlan,
    base: Option<&ActorData>,
    new: &ActorData,
    pc: &mut PatchContext<'_>,
    stats: &mut PatchStats,
) {
    // 今の子を取り出して、添字で 1 回ずつ取れるようにする
    let mut old: Vec<Option<Actor>> = std::mem::take(actor.children_mut()).into_iter().map(Some).collect();

    // ── ファイルから消えた子を破棄する（スクリプトは World から外れたときに OnDestroy が走る）──
    for &live_idx in &plan.removed_children {
        if let Some(child) = old.get_mut(live_idx).and_then(Option::take) {
            despawn_actor_recursive(&child, pc.world);
            stats.nodes_removed += 1;
        }
    }

    // ── 計画の並びで組み直す ──
    let mut next: Vec<Actor> = Vec::with_capacity(plan.children.len());
    for step in plan.children {
        match step {
            ChildStep::Patch { live, data, base: base_idx, plan: child_plan } => {
                let Some(mut child) = old.get_mut(live).and_then(Option::take) else { continue };
                let base_child = base.zip(base_idx).and_then(|(b, i)| b.children.get(i));
                if let Some(new_child) = new.children.get(data) {
                    apply_node(&mut child, child_plan, base_child, new_child, false, pc, stats);
                }
                next.push(child);
            }
            ChildStep::Keep { live } => {
                if let Some(child) = old.get_mut(live).and_then(Option::take) {
                    next.push(child);
                }
            }
            ChildStep::Create { data } => {
                if let Some(child) = new.children.get(data).and_then(|d| build_child(d, pc)) {
                    next.push(child);
                    stats.nodes_created += 1;
                }
            }
            ChildStep::Replace { live, data } => {
                let Some(old_child) = old.get_mut(live).and_then(Option::take) else { continue };
                // 先に組み立て、成功してから古い方を破棄する（失敗したら古い方を残す）
                match new.children.get(data).and_then(|d| build_child(d, pc)) {
                    Some(child) => {
                        despawn_actor_recursive(&old_child, pc.world);
                        next.push(child);
                        stats.nodes_replaced += 1;
                    }
                    None => next.push(old_child),
                }
            }
        }
    }

    // 計画はすべての子を「当てる・残す・作り直す・消す」のどれかに振り分ける。取り残しは計画の誤り。
    // 取り残しても木から落とすとエンティティが宙に浮くので、末尾へ戻しておく。
    let leftovers: Vec<Actor> = old.into_iter().flatten().collect();
    debug_assert!(leftovers.is_empty(), "当て直しの計画が子 {} 個を扱っていません", leftovers.len());
    next.extend(leftovers);

    *actor.children_mut() = next;
}

/// 新しい子（部分木）をファイルの中身から組み立てる（スクリプト込み。次のフレームで OnStart が走る）。
fn build_child(data: &ActorData, pc: &mut PatchContext<'_>) -> Option<Actor> {
    let mut child = match build_actor(data.clone(), pc.ctx, pc.world, pc.host, None) {
        Ok(actor) => actor,
        Err(e) => {
            eprintln!("[PrefabLivePatch] 子 {} を組み立てられませんでした: {e}", data.name);
            return None;
        }
    };
    child.set_world_line_recursive(pc.world_line);
    // 3D: ファイルは「根＝原点」基準で、子の Transform・インスタンス行列はワールド空間で持つ。
    // 根の配置へ移す（プレハブの再展開 reinstantiate_single と同じ考え方）。
    if !child.is_2d() {
        if let Some(delta) = pc.delta_3d {
            // 子の根自身の Transform（apply_delta_to_actor_subtree は根を触らない）
            if let Some(tf) = pc.world.get_mut::<Transform>(child.entity) {
                *tf = Transform::from_mat4(&mat4x4_mul(delta, tf.to_mat4()));
            }
            // 子孫の Transform とモデルのインスタンス行列
            apply_delta_to_actor_subtree(&mut child, pc.world, delta);
        }
    }
    Some(child)
}
