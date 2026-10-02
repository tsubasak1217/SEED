// ============================================================
//  prefab_live_patch/plan.rs — 当て直しの計画（純粋な関数。World に触れない）
//
//  入力は 3 つ:
//   - live … 動いているインスタンスの部分木の要約（summary.rs）
//   - base … そのインスタンスが作られた「元の版」のファイルの中身（分からなければ None）
//   - new  … 保存された「新しい版」のファイルの中身
//  出力はノードごとの `NodePlan`（どのスロットをその場で当て直し・作り・消すか、どの子をどう扱い、
//  どの並びにするか）。実際に World を書き換えるのは apply.rs。
//
//  【突き合わせの鍵】
//   - ノード: 親の下での（名前, 同じ名前の兄弟の中の出現順）。foreign の子には鍵を振らない。
//   - スロット: （種別, 同じ種別の中の出現順）。Placeholder は Script に寄せる。スクリプトは別扱い（下）。
//
//  【3 方向（base がある）と 2 方向（base が無い）】
//  base があれば「ファイルが変えたところ」だけを当てる（Unity の上書き（オーバーライド）と同じ考え方）:
//   - new にあって base に無い        … ファイルが足した → 作る
//   - base にあって new に無い        … ファイルが消した → live にあれば消す
//   - base にも new にもあって live に無い … 実行中に消された（スクリプトが Destroy した等）→ 作り直さない
//   - live にあって base に無い        … 実行中に足された（親の付け替え等）→ 触らない
//  base が無ければ live を元の版の代わりにする。ただし「消す」は判断できないので**消さない**（安全側）。
//
//  【スクリプト】CLR のインスタンスは作り直さない（OnStart が走り直すと画面が初期化される）。
//  ノードのスクリプトの型名の並び（base が無ければ live のもの）が new と同じなら一切触らない
//  （[SerializeField] の値も触らない。Play 中にスクリプトが書き換えた値を正とする）。
//  並びが変わったノードだけ、そのノードのスクリプトを全部作り直す（消して new の並びで作る）。
//
//  【並び】子の最終的な並びは new の順。残す子（foreign・実行中に足された子・2 方向で消さない子）は、
//  live の中で直前にあった「生き残る子」のすぐ後ろに付いていく（スクリプトが積んだ行が見出しの後ろに
//  居続けるように）。スロットは並べ替えない（作ったスロットは末尾に足す）。
// ============================================================

use std::collections::HashMap;

use crate::engine::components::ComponentKind;
use crate::engine::structs::objects::actor::{ActorData, ComponentSlotData};

use super::super::field_edit::component_kind_of;
use super::summary::{family_of_kind, LiveNode, LiveSlot};

// ── 計画の型 ───────────────────────────────────────────────────

/// 1 ノード（とその子）の当て直しの計画。
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct NodePlan {
    /// このノードのスロットの扱い。
    pub slots: SlotPlan,
    /// active をこの値へ揃える（None = 触らない）。インスタンスの根は常に None（配置側の値を守る）。
    pub set_active: Option<bool>,
    /// visible をこの値へ揃える（None = 触らない）。インスタンスの根は常に None。
    pub set_visible: Option<bool>,
    /// 子の最終的な並び（この順に並べ直す）。
    pub children: Vec<ChildStep>,
    /// 破棄する子（live の添字）。ファイルから消えた子だけ（3 方向のときだけ起きる）。
    pub removed_children: Vec<usize>,
}

/// 子 1 個の扱い（`NodePlan::children` の並びがそのまま最終的な並び）。
#[derive(Debug, Clone, PartialEq)]
pub(crate) enum ChildStep {
    /// 既存の子 `live` を、new の子 `data`（元の版の子 `base`）で当て直す。中の計画つき。
    Patch { live: usize, data: usize, base: Option<usize>, plan: NodePlan },
    /// 既存の子 `live` をそのまま残す（foreign・実行中に足された子・2 方向で判断できない子）。
    Keep { live: usize },
    /// new の子 `data` を新しく組み立てる（スクリプト込み。その部分木だけ OnStart が走る）。
    Create { data: usize },
    /// 形（2D/3D・フォルダ）が変わったので、既存の子 `live` を破棄して new の子 `data` から作り直す。
    Replace { live: usize, data: usize },
}

/// 1 ノードのスロットの扱い。
#[derive(Debug, Clone, PartialEq, Default)]
pub(crate) struct SlotPlan {
    /// その場で当て直すスロット（スクリプト以外）。
    pub patches: Vec<SlotPatch>,
    /// 破棄するスロット（live の添字。昇順）。
    pub removed: Vec<usize>,
    /// 新しく作って末尾へ足すスロット（new の添字。昇順＝ファイルの並び）。
    pub created: Vec<usize>,
    /// スクリプトの型名の並びが変わったので、このノードのスクリプトを作り直すか
    /// （true のとき live のスクリプトは全部 `removed`、new のスクリプトは全部 `created` に入っている）。
    pub scripts_rebuilt: bool,
}

/// スロット 1 個のその場の当て直し。
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct SlotPatch {
    /// 動いているスロットの添字。
    pub live: usize,
    /// new のスロットの添字。
    pub data: usize,
    /// 元の版のスロットの添字（3 方向で、元の版にも同じ鍵のスロットがあるとき）。
    pub base: Option<usize>,
}

impl NodePlan {
    /// この計画で何も変わらないか（テスト用）。
    #[cfg(test)]
    pub(crate) fn is_noop(&self) -> bool {
        self.slots.patches.is_empty()
            && self.slots.removed.is_empty()
            && self.slots.created.is_empty()
            && self.set_active.is_none()
            && self.set_visible.is_none()
            && self.removed_children.is_empty()
            && self.children.iter().all(|step| match step {
                ChildStep::Patch { plan, .. } => plan.is_noop(),
                ChildStep::Keep { .. } => true,
                ChildStep::Create { .. } | ChildStep::Replace { .. } => false,
            })
    }
}

// ── 入口 ──────────────────────────────────────────────────────

/// インスタンス 1 個の当て直しの計画を立てる。
///
/// * `live` … 動いているインスタンスの根の要約
/// * `base` … 元の版のファイルの中身（分からなければ None ＝ 2 方向）
/// * `new`  … 新しい版のファイルの中身
///
/// 根の名前・active・visible・Transform は配置側（シーン・スクリプト）の持ち物なので触らない
/// （`PREFAB_REAPPLY_PATH` の再展開が維持する値と同じ）。
pub(crate) fn plan_instance(live: &LiveNode, base: Option<&ActorData>, new: &ActorData) -> NodePlan {
    plan_node(live, base, new, true)
}

/// 1 ノードの計画（再帰）。
fn plan_node(live: &LiveNode, base: Option<&ActorData>, new: &ActorData, is_root: bool) -> NodePlan {
    let slots = plan_slots(&live.slots, base.map(|b| b.components.as_slice()), &new.components);
    let (children, removed_children) = plan_children(live, base, new);
    let (set_active, set_visible) = if is_root {
        (None, None)
    } else {
        (
            flag_change(live.active, base.map(|b| b.active), new.active),
            flag_change(live.visible, base.map(|b| b.visible), new.visible),
        )
    };
    NodePlan { slots, set_active, set_visible, children, removed_children }
}

/// active / visible を揃えるか決める。
///
/// 3 方向: ファイルが変えたとき（base ≠ new）だけ new へ。2 方向: live と違えば new へ（ファイルを正とする）。
fn flag_change(live: bool, base: Option<bool>, new: bool) -> Option<bool> {
    let changed = match base {
        Some(b) => b != new,
        None => live != new,
    };
    if changed && live != new { Some(new) } else { None }
}

// ── 子の突き合わせ ───────────────────────────────────────────────

/// 兄弟の中の鍵（名前, 同じ名前の中の出現順）。
type SiblingKey = (String, usize);

/// 名前の並びから、各要素の鍵（名前, 出現順）を作る。`None` の要素（foreign）は数えない。
fn sibling_keys<'a>(names: impl Iterator<Item = Option<&'a str>>) -> Vec<Option<SiblingKey>> {
    let mut seen: HashMap<&'a str, usize> = HashMap::new();
    names
        .map(|name| {
            name.map(|n| {
                let occurrence = seen.entry(n).or_insert(0);
                let key = (n.to_string(), *occurrence);
                *occurrence += 1;
                key
            })
        })
        .collect()
}

/// 鍵 → 添字の表を作る（`None` の鍵は載せない）。
fn key_index(keys: &[Option<SiblingKey>]) -> HashMap<SiblingKey, usize> {
    keys.iter()
        .enumerate()
        .filter_map(|(i, key)| key.clone().map(|k| (k, i)))
        .collect()
}

/// その場で当て直せる形か（2D/3D とフォルダの別が同じ）。違えば作り直す。
fn same_shape(live: &LiveNode, data: &ActorData) -> bool {
    live.actor_kind == data.actor_kind && live.is_folder == data.is_folder
}

/// 子の計画を立てる。戻り値は（最終的な並び, 破棄する live の子の添字）。
fn plan_children(live: &LiveNode, base: Option<&ActorData>, new: &ActorData) -> (Vec<ChildStep>, Vec<usize>) {
    // ── 鍵を振る（foreign の live の子には振らない）──
    let live_keys = sibling_keys(live.children.iter().map(|c| (!c.foreign).then_some(c.name.as_str())));
    let live_map = key_index(&live_keys);
    let new_keys = sibling_keys(new.children.iter().map(|c| Some(c.name.as_str())));
    let new_map = key_index(&new_keys);
    let base_map = base.map(|b| key_index(&sibling_keys(b.children.iter().map(|c| Some(c.name.as_str())))));

    // ── new の順に、当て直す・作る・作り直すを決める ──
    let mut matched = vec![false; live.children.len()];
    let mut file_steps: Vec<ChildStep> = Vec::new();
    for (data_idx, key) in new_keys.iter().enumerate() {
        let Some(key) = key else { continue };
        let base_idx = base_map.as_ref().and_then(|m| m.get(key).copied());
        match live_map.get(key).copied() {
            Some(live_idx) => {
                matched[live_idx] = true;
                let live_child = &live.children[live_idx];
                let new_child = &new.children[data_idx];
                if same_shape(live_child, new_child) {
                    let base_child = base.zip(base_idx).map(|(b, i)| &b.children[i]);
                    let plan = plan_node(live_child, base_child, new_child, false);
                    file_steps.push(ChildStep::Patch { live: live_idx, data: data_idx, base: base_idx, plan });
                } else {
                    file_steps.push(ChildStep::Replace { live: live_idx, data: data_idx });
                }
            }
            None => {
                // 3 方向で元の版にもあった子が live に無い = 実行中に消された。作り直さない。
                let removed_at_runtime = base_idx.is_some();
                if !removed_at_runtime {
                    file_steps.push(ChildStep::Create { data: data_idx });
                }
            }
        }
    }

    // ── 突き合わなかった live の子: 消すか残すか ──
    let mut removed = Vec::new();
    let mut kept = vec![false; live.children.len()];
    for (live_idx, key) in live_keys.iter().enumerate() {
        if matched[live_idx] {
            continue;
        }
        let removed_by_file = match (key, &base_map) {
            // 元の版にあって new に無い = ファイルが消した（3 方向だけ判断できる）
            (Some(k), Some(bm)) => bm.contains_key(k) && !new_map.contains_key(k),
            // foreign・2 方向は消さない
            _ => false,
        };
        if removed_by_file {
            removed.push(live_idx);
        } else {
            kept[live_idx] = true;
        }
    }

    // ── 残す子を「直前の生き残り」に結び付けて並べる ──
    // 生き残り = Patch / Replace で new の並びに位置を持つ live の子。
    let mut anchored: HashMap<Option<usize>, Vec<usize>> = HashMap::new();
    let mut last_survivor: Option<usize> = None;
    for live_idx in 0..live.children.len() {
        if matched[live_idx] {
            last_survivor = Some(live_idx);
        } else if kept[live_idx] {
            anchored.entry(last_survivor).or_default().push(live_idx);
        }
    }
    let mut steps: Vec<ChildStep> = Vec::with_capacity(file_steps.len() + live.children.len());
    if let Some(first) = anchored.remove(&None) {
        steps.extend(first.into_iter().map(|live| ChildStep::Keep { live }));
    }
    for step in file_steps {
        let anchor = match &step {
            ChildStep::Patch { live, .. } | ChildStep::Replace { live, .. } => Some(*live),
            ChildStep::Keep { .. } | ChildStep::Create { .. } => None,
        };
        steps.push(step);
        if let Some(followers) = anchor.and_then(|a| anchored.remove(&Some(a))) {
            steps.extend(followers.into_iter().map(|live| ChildStep::Keep { live }));
        }
    }
    (steps, removed)
}

// ── スロットの突き合わせ ─────────────────────────────────────────

/// スロットの鍵（種別, 同じ種別の中の出現順）。スクリプトには振らない。
type SlotKey = (ComponentKind, usize);

/// 種別の並びから、各要素の鍵を作る（スクリプトは None）。
fn slot_keys(families: impl Iterator<Item = ComponentKind>) -> Vec<Option<SlotKey>> {
    let mut seen: HashMap<ComponentKind, usize> = HashMap::new();
    families
        .map(|family| {
            if family == ComponentKind::Script {
                return None;
            }
            let occurrence = seen.entry(family).or_insert(0);
            let key = (family, *occurrence);
            *occurrence += 1;
            Some(key)
        })
        .collect()
}

/// 鍵 → 添字の表を作る。
fn slot_index(keys: &[Option<SlotKey>]) -> HashMap<SlotKey, usize> {
    keys.iter().enumerate().filter_map(|(i, key)| key.map(|k| (k, i))).collect()
}

/// ファイル側スロットの突き合わせの種別。
fn data_family(slot: &ComponentSlotData) -> ComponentKind {
    family_of_kind(component_kind_of(&slot.component))
}

/// ファイル側スロットのスクリプトの型名（スクリプト以外は None）。
fn data_script_type(slot: &ComponentSlotData) -> Option<&str> {
    match &slot.component {
        crate::engine::components::ComponentData::ScriptComponent(d) => Some(d.type_name.as_str()),
        _ => None,
    }
}

/// スロットの計画を立てる。
fn plan_slots(live: &[LiveSlot], base: Option<&[ComponentSlotData]>, new: &[ComponentSlotData]) -> SlotPlan {
    let mut plan = SlotPlan::default();

    // ── スクリプト: 型名の並びが変わったときだけ全部作り直す ──
    let new_scripts: Vec<&str> = new.iter().filter_map(data_script_type).collect();
    let reference_scripts: Vec<&str> = match base {
        Some(b) => b.iter().filter_map(data_script_type).collect(),
        None => live.iter().filter_map(|s| s.script_type.as_deref()).collect(),
    };
    if reference_scripts != new_scripts {
        plan.scripts_rebuilt = true;
        plan.removed.extend(live.iter().enumerate().filter(|(_, s)| s.family == ComponentKind::Script).map(|(i, _)| i));
        plan.created.extend(new.iter().enumerate().filter(|(_, s)| data_script_type(s).is_some()).map(|(i, _)| i));
    }

    // ── スクリプト以外: （種別, 出現順）で突き合わせる ──
    let live_keys = slot_keys(live.iter().map(|s| s.family));
    let live_map = slot_index(&live_keys);
    let new_keys = slot_keys(new.iter().map(data_family));
    let new_map = slot_index(&new_keys);
    let base_map = base.map(|b| slot_index(&slot_keys(b.iter().map(data_family))));

    let mut matched = vec![false; live.len()];
    for (data_idx, key) in new_keys.iter().enumerate() {
        let Some(key) = key else { continue };
        let base_idx = base_map.as_ref().and_then(|m| m.get(key).copied());
        match live_map.get(key).copied() {
            Some(live_idx) => {
                matched[live_idx] = true;
                plan.patches.push(SlotPatch { live: live_idx, data: data_idx, base: base_idx });
            }
            // 3 方向で元の版にもあったのに live に無い = 実行中に外された。作り直さない。
            None if base_idx.is_some() => {}
            None => plan.created.push(data_idx),
        }
    }
    for (live_idx, key) in live_keys.iter().enumerate() {
        let Some(key) = key else { continue };
        if matched[live_idx] {
            continue;
        }
        // 元の版にあって new に無い = ファイルが外した（3 方向だけ判断できる。2 方向では消さない）
        let removed_by_file = base_map.as_ref().is_some_and(|bm| bm.contains_key(key)) && !new_map.contains_key(key);
        if removed_by_file {
            plan.removed.push(live_idx);
        }
    }
    plan.removed.sort_unstable();
    plan.created.sort_unstable();
    plan
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::structs::objects::actor::ActorKind;

    // ── テスト用の組み立て ──

    /// スクリプト以外の代表として使うスロット（値だけの純データ。`{}` で読める）。
    const CLIP: &str = r#"{"name":"Clip","component":{"type":"CanvasClipComponent","data":{}}}"#;
    /// もう 1 種類のスロット（同じく `{}` で読める）。
    const STACK: &str = r#"{"name":"Stack","component":{"type":"CanvasStackComponent","data":{}}}"#;

    /// スクリプトのスロットの JSON。
    fn script(type_name: &str) -> String {
        format!(r#"{{"name":"{type_name}","component":{{"type":"ScriptComponent","data":{{"type_name":"{type_name}"}}}}}}"#)
    }

    /// ファイル側のノードを JSON から作る（2D・スロットと子を指定）。
    fn data(name: &str, slots: &[String], children: Vec<ActorData>) -> ActorData {
        let json = format!(r#"{{"name":"{name}","actor_kind":"Actor2D","components":[{}],"children":[]}}"#, slots.join(","));
        let mut d: ActorData = serde_json::from_str(&json).expect("テスト用の ActorData が読めること");
        d.children = children;
        d
    }

    /// ファイル側の葉（スロット無し）。
    fn leaf(name: &str) -> ActorData {
        data(name, &[], Vec::new())
    }

    /// 動いている側のノード（2D・スクリプト以外のスロットの種別とスクリプトの型名を指定）。
    fn live(name: &str, slots: Vec<LiveSlot>, children: Vec<LiveNode>) -> LiveNode {
        LiveNode {
            name: name.to_string(),
            actor_kind: ActorKind::Actor2D,
            is_folder: false,
            active: true,
            visible: true,
            foreign: false,
            slots,
            children,
        }
    }

    /// 動いている側の葉。
    fn live_leaf(name: &str) -> LiveNode {
        live(name, Vec::new(), Vec::new())
    }

    /// 動いている側の、スクリプトが生成した部分木の根（foreign）。
    fn foreign(name: &str) -> LiveNode {
        LiveNode { foreign: true, ..live_leaf(name) }
    }

    /// スクリプト以外のスロットの要約。
    fn slot(family: ComponentKind) -> LiveSlot {
        LiveSlot { family, script_type: None }
    }

    /// スクリプトのスロットの要約。
    fn live_script(type_name: &str) -> LiveSlot {
        LiveSlot { family: ComponentKind::Script, script_type: Some(type_name.to_string()) }
    }

    /// 子の計画を「種類と添字」の短い文字列にする（並びの比較を読みやすくするため）。
    fn shape(steps: &[ChildStep]) -> Vec<String> {
        steps
            .iter()
            .map(|s| match s {
                ChildStep::Patch { live, data, .. } => format!("P{live}:{data}"),
                ChildStep::Keep { live } => format!("K{live}"),
                ChildStep::Create { data } => format!("C{data}"),
                ChildStep::Replace { live, data } => format!("R{live}:{data}"),
            })
            .collect()
    }

    // ── 子の追加・削除・順序 ──

    /// ファイルに増えた子は、new の並びの位置に Create になる。
    #[test]
    fn added_child_is_created_in_file_order() {
        let live_root = live("Root", vec![], vec![live_leaf("A"), live_leaf("C")]);
        let new = data("Root", &[], vec![leaf("A"), leaf("B"), leaf("C")]);
        let plan = plan_instance(&live_root, None, &new);
        assert_eq!(shape(&plan.children), ["P0:0", "C1", "P1:2"]);
        assert!(plan.removed_children.is_empty());
    }

    /// 3 方向: ファイルから消えた子は破棄する。
    #[test]
    fn removed_child_is_destroyed_with_base() {
        let live_root = live("Root", vec![], vec![live_leaf("A"), live_leaf("B")]);
        let base = data("Root", &[], vec![leaf("A"), leaf("B")]);
        let new = data("Root", &[], vec![leaf("A")]);
        let plan = plan_instance(&live_root, Some(&base), &new);
        assert_eq!(plan.removed_children, vec![1]);
        assert_eq!(shape(&plan.children), ["P0:0"]);
    }

    /// 2 方向（元の版が分からない）: ファイルに無い子は消さずに残す（実行中に足されたのか判断できない）。
    #[test]
    fn missing_child_is_kept_without_base() {
        let live_root = live("Root", vec![], vec![live_leaf("A"), live_leaf("B")]);
        let new = data("Root", &[], vec![leaf("A")]);
        let plan = plan_instance(&live_root, None, &new);
        assert!(plan.removed_children.is_empty());
        assert_eq!(shape(&plan.children), ["P0:0", "K1"]);
    }

    /// 3 方向: 元の版にも new にもあるのに live に無い子（実行中に Destroy された）は作り直さない。
    #[test]
    fn child_destroyed_at_runtime_is_not_recreated() {
        let live_root = live("Root", vec![], vec![live_leaf("A")]);
        let base = data("Root", &[], vec![leaf("A"), leaf("B")]);
        let new = data("Root", &[], vec![leaf("A"), leaf("B")]);
        let plan = plan_instance(&live_root, Some(&base), &new);
        assert_eq!(shape(&plan.children), ["P0:0"]);
        assert!(plan.is_noop(), "何も変わらないこと: {plan:?}");
    }

    /// 3 方向: live にあって元の版に無い子（実行中に付け替えで入ってきた等）は消さない。
    #[test]
    fn child_added_at_runtime_is_kept_with_base() {
        let live_root = live("Root", vec![], vec![live_leaf("A"), live_leaf("Extra")]);
        let base = data("Root", &[], vec![leaf("A")]);
        let new = data("Root", &[], vec![leaf("A")]);
        let plan = plan_instance(&live_root, Some(&base), &new);
        assert!(plan.removed_children.is_empty());
        assert_eq!(shape(&plan.children), ["P0:0", "K1"]);
    }

    /// 順序の入れ替えは new の並びに揃える。
    #[test]
    fn reordered_children_follow_file_order() {
        let live_root = live("Root", vec![], vec![live_leaf("A"), live_leaf("B"), live_leaf("C")]);
        let new = data("Root", &[], vec![leaf("C"), leaf("A"), leaf("B")]);
        let plan = plan_instance(&live_root, None, &new);
        assert_eq!(shape(&plan.children), ["P2:0", "P0:1", "P1:2"]);
    }

    /// 同じ名前の兄弟は出現順で突き合わせる（3 個目は新しく作る）。
    #[test]
    fn same_name_siblings_match_by_occurrence() {
        let live_root = live("Root", vec![], vec![live_leaf("Item"), live_leaf("Other"), live_leaf("Item")]);
        let new = data("Root", &[], vec![leaf("Item"), leaf("Item"), leaf("Item"), leaf("Other")]);
        let plan = plan_instance(&live_root, None, &new);
        assert_eq!(shape(&plan.children), ["P0:0", "P2:1", "C2", "P1:3"]);
    }

    /// スクリプトが生成した部分木（foreign）は消さず、直前の生き残りの子の後ろに付いていく。
    #[test]
    fn foreign_children_are_kept_after_their_anchor() {
        // Header の後ろにスクリプトが積んだ行 Row0・Row1、最後に Footer
        let live_root = live(
            "List",
            vec![],
            vec![foreign("Spawned"), live_leaf("Header"), foreign("Row0"), foreign("Row1"), live_leaf("Footer")],
        );
        // ファイルでは Footer を先頭へ動かし、Divider を足した
        let base = data("List", &[], vec![leaf("Header"), leaf("Footer")]);
        let new = data("List", &[], vec![leaf("Footer"), leaf("Header"), leaf("Divider")]);
        let plan = plan_instance(&live_root, Some(&base), &new);
        assert!(plan.removed_children.is_empty(), "foreign は消さない");
        assert_eq!(shape(&plan.children), ["K0", "P4:0", "P1:1", "K2", "K3", "C2"]);
    }

    /// foreign の子は同じ名前でもファイルの子と突き合わせない（生成物をファイルの値で上書きしない）。
    #[test]
    fn foreign_child_never_matches_file_child() {
        let live_root = live("Root", vec![], vec![foreign("Row")]);
        let new = data("Root", &[], vec![leaf("Row")]);
        let plan = plan_instance(&live_root, None, &new);
        assert_eq!(shape(&plan.children), ["K0", "C0"]);
    }

    /// 形（2D/3D・フォルダ）が変わった子はノードごと作り直す。
    #[test]
    fn shape_change_replaces_child() {
        let mut folder = live_leaf("Group");
        folder.is_folder = true;
        let live_root = live("Root", vec![], vec![folder]);
        let new = data("Root", &[], vec![leaf("Group")]);
        let plan = plan_instance(&live_root, None, &new);
        assert_eq!(shape(&plan.children), ["R0:0"]);
    }

    /// 根の active / visible は触らず、子は 3 方向でファイルが変えたときだけ揃える。
    #[test]
    fn flags_follow_file_changes_only() {
        let mut hidden_at_runtime = live_leaf("Panel");
        hidden_at_runtime.visible = false; // スクリプトが隠した
        let mut root = live("Root", vec![], vec![hidden_at_runtime, live_leaf("Badge")]);
        root.active = false;
        let base = data("Root", &[], vec![leaf("Panel"), leaf("Badge")]);
        let mut new = data("Root", &[], vec![leaf("Panel"), leaf("Badge")]);
        new.active = true;
        new.children[1].visible = false; // ファイルで Badge を隠した
        let plan = plan_instance(&root, Some(&base), &new);
        assert_eq!(plan.set_active, None, "根は配置側の値を守る");
        let ChildStep::Patch { plan: panel, .. } = &plan.children[0] else { panic!("Panel は Patch") };
        assert_eq!(panel.set_visible, None, "ファイルが変えていない値は実行中の値を守る");
        let ChildStep::Patch { plan: badge, .. } = &plan.children[1] else { panic!("Badge は Patch") };
        assert_eq!(badge.set_visible, Some(false), "ファイルが変えた値は揃える");
    }

    // ── スロット ──

    /// スロットの増減（3 方向）: 増えたものは作り、ファイルが外したものは破棄する。
    #[test]
    fn slots_added_and_removed_with_base() {
        let live_root = live("Root", vec![slot(ComponentKind::CanvasClip), slot(ComponentKind::CanvasStack)], vec![]);
        let base = data("Root", &[CLIP.to_string(), STACK.to_string()], vec![]);
        let new = data("Root", &[CLIP.to_string(), CLIP.to_string()], vec![]);
        let plan = plan_instance(&live_root, Some(&base), &new);
        assert_eq!(plan.slots.patches, vec![SlotPatch { live: 0, data: 0, base: Some(0) }]);
        assert_eq!(plan.slots.removed, vec![1], "ファイルが外した Stack は破棄する");
        assert_eq!(plan.slots.created, vec![1], "2 個目の Clip は作る");
        assert!(!plan.slots.scripts_rebuilt);
    }

    /// 2 方向ではファイルに無いスロットを消さない（実行中に足された可能性があるため）。
    #[test]
    fn slots_are_not_removed_without_base() {
        let live_root = live("Root", vec![slot(ComponentKind::CanvasClip), slot(ComponentKind::CanvasStack)], vec![]);
        let new = data("Root", &[CLIP.to_string()], vec![]);
        let plan = plan_instance(&live_root, None, &new);
        assert_eq!(plan.slots.patches, vec![SlotPatch { live: 0, data: 0, base: None }]);
        assert!(plan.slots.removed.is_empty());
        assert!(plan.slots.created.is_empty());
    }

    /// スクリプトの並びが同じなら、スクリプトのスロットには一切触らない（CLR を作り直さない）。
    #[test]
    fn unchanged_script_list_keeps_scripts() {
        let live_root = live("Root", vec![live_script("Screen"), slot(ComponentKind::CanvasClip)], vec![]);
        let new = data("Root", &[script("Screen"), CLIP.to_string()], vec![]);
        let plan = plan_instance(&live_root, None, &new);
        assert!(!plan.slots.scripts_rebuilt);
        assert_eq!(plan.slots.patches, vec![SlotPatch { live: 1, data: 1, base: None }], "スクリプトは当て直しに入らない");
        assert!(plan.slots.removed.is_empty() && plan.slots.created.is_empty());
    }

    /// スクリプトの並びが変わったノードだけ、スクリプトを全部作り直す。
    #[test]
    fn changed_script_list_rebuilds_scripts_of_that_node_only() {
        let child = live("Child", vec![live_script("Row")], vec![]);
        let live_root = live("Root", vec![live_script("Screen"), slot(ComponentKind::CanvasClip)], vec![child]);
        let new = data(
            "Root",
            &[script("Screen"), script("Extra"), CLIP.to_string()],
            vec![data("Child", &[script("Row")], vec![])],
        );
        let plan = plan_instance(&live_root, None, &new);
        assert!(plan.slots.scripts_rebuilt);
        assert_eq!(plan.slots.removed, vec![0], "live のスクリプトを破棄");
        assert_eq!(plan.slots.created, vec![0, 1], "new のスクリプトを並び順に作る");
        let ChildStep::Patch { plan: child_plan, .. } = &plan.children[0] else { panic!("Child は Patch") };
        assert!(!child_plan.slots.scripts_rebuilt, "並びが同じ子のスクリプトは作り直さない");
    }

    /// 3 方向ではスクリプトの並びを「元の版 → 新しい版」で比べる（実行中の差は理由にしない）。
    #[test]
    fn script_list_is_compared_against_base() {
        // CLR が無く Placeholder だったが、型名の並びはファイルと同じ
        let live_root = live("Root", vec![live_script("Screen")], vec![]);
        let base = data("Root", &[script("Screen")], vec![]);
        let new = data("Root", &[script("Screen")], vec![]);
        let plan = plan_instance(&live_root, Some(&base), &new);
        assert!(!plan.slots.scripts_rebuilt);
        // ファイルで並びが変わったら作り直す
        let new2 = data("Root", &[script("Screen2")], vec![]);
        let plan2 = plan_instance(&live_root, Some(&base), &new2);
        assert!(plan2.slots.scripts_rebuilt);
        assert_eq!(plan2.slots.removed, vec![0]);
        assert_eq!(plan2.slots.created, vec![0]);
    }

    /// 何も変わっていないファイルなら計画は空（当て直しの件数に数えても値は動かない）。
    #[test]
    fn identical_file_yields_noop_plan() {
        let live_root = live("Root", vec![slot(ComponentKind::CanvasClip)], vec![live_leaf("A")]);
        let base = data("Root", &[CLIP.to_string()], vec![leaf("A")]);
        let plan = plan_instance(&live_root, Some(&base), &base);
        // スロットの当て直し自体は計画に載る（値が同じかは apply が JSON で比べて飛ばす）
        assert_eq!(plan.slots.patches.len(), 1);
        assert!(plan.removed_children.is_empty());
        assert_eq!(shape(&plan.children), ["P0:0"]);
    }
}
