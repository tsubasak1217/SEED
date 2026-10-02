// ============================================================
//  prefab_live_patch/summary.rs — 動いているインスタンスの「突き合わせ用の要約」
//
//  計画（plan.rs）を World に触れない純粋な関数にするため、動いているアクタの部分木から
//  突き合わせに要る情報だけを抜き出した値の木を作る。要約は計画を立てる間だけ使い捨てる。
//
//  【要約に入れるもの】
//   - 名前（兄弟の中の出現順と合わせて突き合わせの鍵になる）
//   - 形（2D/3D・フォルダ）… 変わっていたらノードごと作り直す
//   - active / visible … 2 方向（元の版が分からない）ときに「ファイルの値と違うか」を見る
//   - スロットの種別（Placeholder は Script に寄せる）とスクリプトの型名
//   - 突き合わせの対象外の印（foreign）… スクリプトが生成した部分木・エディタのプレビュー
// ============================================================

use crate::engine::components::{ComponentKind, PlaceholderScriptSlot, ScriptComponent};
use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::ActorKind;

/// 動いているノード 1 個の要約（計画の入力）。
#[derive(Debug, Clone)]
pub(crate) struct LiveNode {
    /// ヒエラルキーの表示名（突き合わせの鍵）。
    pub name: String,
    /// 3D / 2D。ファイル側と食い違えばノードごと作り直す。
    pub actor_kind: ActorKind,
    /// 整理専用のフォルダか。ファイル側と食い違えばノードごと作り直す。
    pub is_folder: bool,
    /// 自身のアクティブフラグ（祖先は考えない生の値）。
    pub active: bool,
    /// 自身の表示フラグ（祖先は考えない生の値）。
    pub visible: bool,
    /// 突き合わせの対象外か（スクリプトが Play 中に生成した部分木の根・エディタのプレビューの根）。
    /// true のノードは「ファイルに無い」扱いで消さず、値も触らず、そのまま残す。
    pub foreign: bool,
    /// スロットの要約（並びは Actor のスロット目録の順）。
    pub slots: Vec<LiveSlot>,
    /// 子の要約（並びは Actor の子の順）。foreign のノードは中を要約しない（空）。
    pub children: Vec<LiveNode>,
}

/// 動いているスロット 1 個の要約。
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct LiveSlot {
    /// 突き合わせに使う種別。CLR 不在の `Placeholder` は `Script` に寄せる
    /// （ファイル側のデータからは両者を区別できないため。`family_of_kind`）。
    pub family: ComponentKind,
    /// スクリプト（Placeholder を含む）の型名。スクリプト以外は None。
    pub script_type: Option<String>,
}

/// コンポーネントの種別を「突き合わせの種別」に寄せる（Placeholder → Script。他はそのまま）。
pub(crate) fn family_of_kind(kind: ComponentKind) -> ComponentKind {
    if kind == ComponentKind::Placeholder { ComponentKind::Script } else { kind }
}

/// インスタンスの根から要約を作る（根は foreign にしない。スクリプトが生成した画面そのものも当て直しの対象）。
pub(crate) fn summarize_instance(root: &Actor, world: &World) -> LiveNode {
    summarize_node(root, world, true)
}

/// 1 ノードの要約を作る（再帰）。
///
/// * `is_root` … インスタンスの根か。根は印があっても foreign にしない。
fn summarize_node(actor: &Actor, world: &World, is_root: bool) -> LiveNode {
    // 根以外で、スクリプトが生成した部分木・エディタのプレビューは突き合わせない
    let foreign = !is_root && (actor.spawned_by_script || actor.editor_preview.is_some());
    LiveNode {
        name: actor.name.clone(),
        actor_kind: actor.actor_kind,
        is_folder: actor.is_folder(),
        active: actor.active,
        visible: actor.visible,
        foreign,
        slots: actor.slots().iter().map(|slot| summarize_slot(slot.kind, slot.entity, world)).collect(),
        // foreign の中は計画で使わないので要約しない（大きな生成物を毎回たどらない）
        children: if foreign {
            Vec::new()
        } else {
            actor.children().iter().map(|child| summarize_node(child, world, false)).collect()
        },
    }
}

/// スロット 1 個の要約を作る（スクリプトなら World から型名を引く）。
fn summarize_slot(kind: ComponentKind, entity: crate::engine::ecs::Entity, world: &World) -> LiveSlot {
    let script_type = match kind {
        ComponentKind::Script => world.get::<ScriptComponent>(entity).map(|sc| sc.type_name().to_string()),
        ComponentKind::Placeholder => world.get::<PlaceholderScriptSlot>(entity).map(|ps| ps.script_path.clone()),
        _ => None,
    };
    LiveSlot { family: family_of_kind(kind), script_type }
}
