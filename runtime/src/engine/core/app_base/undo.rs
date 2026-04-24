// ============================================================
//  undo.rs — Undo/Redo 基盤
//
//  【設計】
//  - Command トレイトで操作を抽象化。
//  - UndoHistory が past/future スタックを管理。
//  - record()   : 操作はすでに適用済み → 履歴に積むだけ。
//  - apply()    : 操作を実行してから履歴に積む（将来の UI ボタン等用）。
//  - undo/redo  : past/future を移動し、Scene に再適用。
// ============================================================

use crate::engine::core::app_base::scene::Scene;
use crate::engine::structs::components::ModelComponent;
use crate::engine::structs::components::model_component::{GroupMeta, InstanceMeta};
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::{ActorData, ActorTransform, ComponentSlotData};

// ============================================================
//  Command トレイト
// ============================================================

pub trait Command {
    fn execute(&mut self, scene: &mut Scene);
    fn undo(&mut self, scene: &mut Scene);
    /// true を返すと構造変更（追加・削除）を示す。
    fn is_structural(&self) -> bool { false }
    /// Undo 後に復元すべき選択状態。None なら変更しない。
    fn selection_after_undo(&self) -> Option<Vec<u32>> { None }
    /// Redo (re-execute) 後に復元すべき選択状態。None なら変更しない。
    fn selection_after_redo(&self) -> Option<Vec<u32>> { None }
    /// Undo 実行後に AppBase がアクターツリーを再構築するためのデータ。
    fn actor_rebuild_for_undo(&self) -> Option<(u32, Vec<ActorData>)> { None }
    /// Redo 実行後に AppBase がアクターツリーを再構築するためのデータ。
    fn actor_rebuild_for_redo(&self) -> Option<(u32, Vec<ActorData>)> { None }
    /// Undo 実行後に AppBase がコンポーネントスロットを再構築するためのデータ。
    fn component_rebuild_for_undo(&self) -> Option<(u32, u32, Vec<ComponentSlotData>)> { None }
    /// Redo 実行後に AppBase がコンポーネントスロットを再構築するためのデータ。
    fn component_rebuild_for_redo(&self) -> Option<(u32, u32, Vec<ComponentSlotData>)> { None }
    /// Undo/Redo 後にインスペクターへ通知すべきアクターの (world_line, dfs_id)。
    fn actor_inspect_notify(&self) -> Option<(u32, u32)> { None }
}

// ============================================================
//  UndoHistory
// ============================================================

const MAX_HISTORY: usize = 100;

pub struct UndoHistory {
    past:   Vec<Box<dyn Command>>,
    future: Vec<Box<dyn Command>>,
}

impl UndoHistory {
    pub fn new() -> Self {
        Self { past: Vec::new(), future: Vec::new() }
    }

    /// 操作がすでに Scene に適用済みの場合に履歴へ積む。
    /// Redo スタックはクリアされる。
    pub fn record(&mut self, cmd: Box<dyn Command>) {
        self.past.push(cmd);
        self.future.clear();
        if self.past.len() > MAX_HISTORY {
            self.past.remove(0);
        }
    }

    /// コマンドを実行してから履歴に積む（UI ボタン等から直接使う場合）。
    pub fn apply(&mut self, mut cmd: Box<dyn Command>, scene: &mut Scene) {
        cmd.execute(scene);
        self.record(cmd);
    }

    /// 直前の操作を元に戻す。
    /// 戻せた場合 `Some((is_structural, selection_to_restore))`、何もなければ `None` を返す。
    pub fn undo(&mut self, scene: &mut Scene) -> Option<(bool, Option<Vec<u32>>)> {
        if let Some(mut cmd) = self.past.pop() {
            let structural = cmd.is_structural();
            let selection = cmd.selection_after_undo();
            cmd.undo(scene);
            self.future.push(cmd);
            Some((structural, selection))
        } else {
            None
        }
    }

    /// Undo した操作をやり直す。
    /// やり直せた場合 `Some((is_structural, selection_to_restore))`、何もなければ `None` を返す。
    pub fn redo(&mut self, scene: &mut Scene) -> Option<(bool, Option<Vec<u32>>)> {
        if let Some(mut cmd) = self.future.pop() {
            let structural = cmd.is_structural();
            let selection = cmd.selection_after_redo();
            cmd.execute(scene);
            self.past.push(cmd);
            Some((structural, selection))
        } else {
            None
        }
    }

    pub fn can_undo(&self) -> bool { !self.past.is_empty() }
    pub fn can_redo(&self) -> bool { !self.future.is_empty() }

    /// undo() 直後: future の末尾が今 undo したコマンド。
    pub fn peek_undone_actor_rebuild(&self) -> Option<(u32, Vec<ActorData>)> {
        self.future.last()?.actor_rebuild_for_undo()
    }
    pub fn peek_redone_actor_rebuild(&self) -> Option<(u32, Vec<ActorData>)> {
        self.past.last()?.actor_rebuild_for_redo()
    }
    pub fn peek_undone_component_rebuild(&self) -> Option<(u32, u32, Vec<ComponentSlotData>)> {
        self.future.last()?.component_rebuild_for_undo()
    }
    pub fn peek_redone_component_rebuild(&self) -> Option<(u32, u32, Vec<ComponentSlotData>)> {
        self.past.last()?.component_rebuild_for_redo()
    }
    /// undo() 直後: future の末尾のコマンドのインスペクター通知先。
    pub fn peek_undone_actor_inspect(&self) -> Option<(u32, u32)> {
        self.future.last()?.actor_inspect_notify()
    }
    /// redo() 直後: past の末尾のコマンドのインスペクター通知先。
    pub fn peek_redone_actor_inspect(&self) -> Option<(u32, u32)> {
        self.past.last()?.actor_inspect_notify()
    }
}

// ============================================================
//  SceneSnapshotCommand — 構造変更（追加・削除）の Undo/Redo
// ============================================================

/// ModelComponent の完全スナップショット。
/// 追加・削除操作の前後状態を保持し、任意の方向に復元する。
pub struct SceneSnapshotCommand {
    pub before_mats:   Vec<[[f32; 4]; 4]>,
    pub before_meta:   Vec<InstanceMeta>,
    pub before_groups: Vec<GroupMeta>,
    pub before_gid:    u32,
    pub after_mats:    Vec<[[f32; 4]; 4]>,
    pub after_meta:    Vec<InstanceMeta>,
    pub after_groups:  Vec<GroupMeta>,
    pub after_gid:     u32,
    pub before_selection: Vec<u32>,
    pub after_selection:  Vec<u32>,
}

impl Command for SceneSnapshotCommand {
    fn execute(&mut self, scene: &mut Scene) {
        if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
            mc.instance_mats = self.after_mats.clone();
            mc.instance_meta = self.after_meta.clone();
            mc.group_meta    = self.after_groups.clone();
            mc.next_group_id = self.after_gid;
            mc.mark_batch_dirty();
        }
    }
    fn undo(&mut self, scene: &mut Scene) {
        if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
            mc.instance_mats = self.before_mats.clone();
            mc.instance_meta = self.before_meta.clone();
            mc.group_meta    = self.before_groups.clone();
            mc.next_group_id = self.before_gid;
            mc.mark_batch_dirty();
        }
    }
    fn is_structural(&self) -> bool { true }
    fn selection_after_undo(&self) -> Option<Vec<u32>> {
        Some(self.before_selection.clone())
    }
    fn selection_after_redo(&self) -> Option<Vec<u32>> {
        Some(self.after_selection.clone())
    }
}

// ============================================================
//  TransformCommand — インスタンス変換行列の変更
// ============================================================

pub struct TransformCommand {
    pub instance_idx: u32,
    pub old_mat:      [[f32; 4]; 4],
    pub new_mat:      [[f32; 4]; 4],
}

impl Command for TransformCommand {
    fn execute(&mut self, scene: &mut Scene) {
        set_instance_mat(scene, self.instance_idx, self.new_mat);
    }
    fn undo(&mut self, scene: &mut Scene) {
        set_instance_mat(scene, self.instance_idx, self.old_mat);
    }
}

// ============================================================
//  MultiTransformCommand — 複数インスタンスの一括変換（複数選択ドラッグ用）
// ============================================================

pub struct MultiTransformCommand {
    /// (instance_idx, old_mat, new_mat)
    pub transforms: Vec<(u32, [[f32; 4]; 4], [[f32; 4]; 4])>,
}

impl Command for MultiTransformCommand {
    fn execute(&mut self, scene: &mut Scene) {
        for &(idx, _, new_mat) in &self.transforms { set_instance_mat(scene, idx, new_mat); }
    }
    fn undo(&mut self, scene: &mut Scene) {
        for &(idx, old_mat, _) in &self.transforms { set_instance_mat(scene, idx, old_mat); }
    }
}

// ============================================================
//  SelectionCommand — 選択状態の変更
// ============================================================

pub struct SelectionCommand {
    pub before: Vec<u32>,
    pub after:  Vec<u32>,
}

impl Command for SelectionCommand {
    fn execute(&mut self, _scene: &mut Scene) {}
    fn undo(&mut self, _scene: &mut Scene) {}
    fn selection_after_undo(&self) -> Option<Vec<u32>> { Some(self.before.clone()) }
    fn selection_after_redo(&self) -> Option<Vec<u32>> { Some(self.after.clone()) }
}

// ============================================================
//  ActorTreeSnapshotCommand — アクターツリー構造変更の Undo/Redo
// ============================================================

/// ADD_ACTOR / REMOVE_ACTOR のスナップショット。
/// execute/undo は No-op で、AppBase が peek_*_actor_rebuild() を使い GPU 再構築する。
pub struct ActorTreeSnapshotCommand {
    pub world_line:    u32,
    pub before_actors: Vec<ActorData>,
    pub after_actors:  Vec<ActorData>,
}

impl Command for ActorTreeSnapshotCommand {
    fn execute(&mut self, _scene: &mut Scene) {}
    fn undo(&mut self, _scene: &mut Scene) {}
    fn is_structural(&self) -> bool { true }
    fn actor_rebuild_for_undo(&self) -> Option<(u32, Vec<ActorData>)> {
        Some((self.world_line, self.before_actors.clone()))
    }
    fn actor_rebuild_for_redo(&self) -> Option<(u32, Vec<ActorData>)> {
        Some((self.world_line, self.after_actors.clone()))
    }
}

// ============================================================
//  ComponentSlotsSnapshotCommand — コンポーネントスロット変更の Undo/Redo
// ============================================================

/// ADD_COMPONENT / REMOVE_COMPONENT のスナップショット。
pub struct ComponentSlotsSnapshotCommand {
    pub world_line:   u32,
    pub actor_dfs_id: u32,
    pub before_slots: Vec<ComponentSlotData>,
    pub after_slots:  Vec<ComponentSlotData>,
}

impl Command for ComponentSlotsSnapshotCommand {
    fn execute(&mut self, _scene: &mut Scene) {}
    fn undo(&mut self, _scene: &mut Scene) {}
    fn is_structural(&self) -> bool { true }
    fn component_rebuild_for_undo(&self) -> Option<(u32, u32, Vec<ComponentSlotData>)> {
        Some((self.world_line, self.actor_dfs_id, self.before_slots.clone()))
    }
    fn component_rebuild_for_redo(&self) -> Option<(u32, u32, Vec<ComponentSlotData>)> {
        Some((self.world_line, self.actor_dfs_id, self.after_slots.clone()))
    }
}

// ============================================================
//  ActorTransformCommand — アクター自身の Transform 変更
// ============================================================

pub struct ActorTransformCommand {
    pub world_line:    u32,
    pub dfs_id:        u32,
    pub old_transform: ActorTransform,
    pub new_transform: ActorTransform,
}

impl Command for ActorTransformCommand {
    fn execute(&mut self, scene: &mut Scene) {
        let mut c = 0u32;
        if let Some(actor) = dfs_find_mut(&mut scene.actors, self.world_line, self.dfs_id, &mut c) {
            actor.transform = self.new_transform.clone();
        }
    }
    fn undo(&mut self, scene: &mut Scene) {
        let mut c = 0u32;
        if let Some(actor) = dfs_find_mut(&mut scene.actors, self.world_line, self.dfs_id, &mut c) {
            actor.transform = self.old_transform.clone();
        }
    }
    fn actor_inspect_notify(&self) -> Option<(u32, u32)> {
        Some((self.world_line, self.dfs_id))
    }
}

fn dfs_find_mut<'a>(actors: &'a mut Vec<Actor>, wl: u32, dfs_id: u32, c: &mut u32) -> Option<&'a mut Actor> {
    for actor in actors.iter_mut() {
        if actor.world_line != wl { continue; }
        if *c == dfs_id { return Some(actor); }
        *c += 1;
        if let Some(found) = dfs_find_child_mut(actor, dfs_id, c) { return Some(found); }
    }
    None
}

fn dfs_find_child_mut<'a>(actor: &'a mut Actor, dfs_id: u32, c: &mut u32) -> Option<&'a mut Actor> {
    for child in actor.children_mut().iter_mut() {
        if *c == dfs_id { return Some(child); }
        *c += 1;
        if let Some(found) = dfs_find_child_mut(child, dfs_id, c) { return Some(found); }
    }
    None
}

// ── 内部ヘルパー ──────────────────────────────────────────────

fn set_instance_mat(scene: &mut Scene, idx: u32, mat: [[f32; 4]; 4]) {
    if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
        if let Some(m) = mc.instance_mats.get_mut(idx as usize) {
            *m = mat;
        }
        mc.mark_batch_dirty();
    }
}
