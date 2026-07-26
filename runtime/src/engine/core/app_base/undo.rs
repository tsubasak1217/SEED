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

use crate::engine::ecs::Entity;
use crate::engine::components::{
    ModelComponent, Transform, CanvasTransform,
};
use crate::engine::components::model_component::{GroupMeta, InstanceMeta};
use crate::engine::core::app_base::scene::Scene;
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::{ActorData, ComponentSlotData};

// ============================================================
//  Command トレイト
// ============================================================

/// Undo/Redo 可能な単一操作を表す抽象。UndoHistory の past/future スタックに積まれる。
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
    /// Undo 実行後に AppBase が復元すべきアクター DFS 選択状態 (dfs_ids, primary)。
    fn actor_dfs_selection_after_undo(&self) -> Option<(Vec<usize>, Option<usize>)> { None }
    /// Redo 実行後に AppBase が復元すべきアクター DFS 選択状態 (dfs_ids, primary)。
    fn actor_dfs_selection_after_redo(&self) -> Option<(Vec<usize>, Option<usize>)> { None }
}

// ============================================================
//  UndoHistory
// ============================================================

const MAX_HISTORY: usize = 100;

/// Undo/Redo 履歴本体。実行済みコマンドの past スタックと、Undo 済みコマンドの
/// future スタックを管理し、Command トレイトを介して Scene への再適用を仲介する。
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

    /// 最後に積んだコマンドを取り出す（CompositeCommand へ統合するため）。
    /// future スタックは変更しない。
    pub fn pop_last(&mut self) -> Option<Box<dyn Command>> {
        self.past.pop()
    }

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
    /// undo() 直後: 復元すべきアクター DFS 選択状態。
    pub fn peek_undone_actor_dfs_selection(&self) -> Option<(Vec<usize>, Option<usize>)> {
        self.future.last()?.actor_dfs_selection_after_undo()
    }
    /// redo() 直後: 復元すべきアクター DFS 選択状態。
    pub fn peek_redone_actor_dfs_selection(&self) -> Option<(Vec<usize>, Option<usize>)> {
        self.past.last()?.actor_dfs_selection_after_redo()
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
        if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(0) {
            mc.instance_mats = self.after_mats.clone();
            mc.instance_meta = self.after_meta.clone();
            mc.group_meta    = self.after_groups.clone();
            mc.next_group_id = self.after_gid;
            mc.mark_batch_dirty();
        }
    }
    fn undo(&mut self, scene: &mut Scene) {
        if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(0) {
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

/// 単一 MC インスタンスの変換行列変更を Undo/Redo するコマンド。
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

/// 複数 MC インスタンスの変換行列を一括で Undo/Redo するコマンド（複数選択ドラッグ用）。
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

/// 選択状態（選択中インスタンス集合）の変更を Undo/Redo するコマンド。
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
//  CompositeCommand — 複数コマンドをアトミックに Undo/Redo する合成コマンド
// ============================================================

/// 複数の Command を1つの Undo/Redo 操作として扱うラッパー。
/// プライマリ Actor のドラッグと非プライマリ Actor のドラッグを
/// 1 Ctrl+Z で戻せるようにするために使用する。
pub struct CompositeCommand {
    /// execute 順に格納。undo は逆順で実行する。
    pub commands: Vec<Box<dyn Command>>,
}

impl Command for CompositeCommand {
    fn execute(&mut self, scene: &mut Scene) {
        for cmd in &mut self.commands { cmd.execute(scene); }
    }
    fn undo(&mut self, scene: &mut Scene) {
        for cmd in self.commands.iter_mut().rev() { cmd.undo(scene); }
    }
    fn is_structural(&self) -> bool {
        self.commands.iter().any(|c| c.is_structural())
    }
    fn selection_after_undo(&self) -> Option<Vec<u32>> {
        self.commands.iter().rev().find_map(|c| c.selection_after_undo())
    }
    fn selection_after_redo(&self) -> Option<Vec<u32>> {
        self.commands.iter().rev().find_map(|c| c.selection_after_redo())
    }
    fn actor_rebuild_for_undo(&self) -> Option<(u32, Vec<ActorData>)> {
        self.commands.iter().rev().find_map(|c| c.actor_rebuild_for_undo())
    }
    fn actor_rebuild_for_redo(&self) -> Option<(u32, Vec<ActorData>)> {
        self.commands.iter().rev().find_map(|c| c.actor_rebuild_for_redo())
    }
    fn component_rebuild_for_undo(&self) -> Option<(u32, u32, Vec<ComponentSlotData>)> {
        self.commands.iter().rev().find_map(|c| c.component_rebuild_for_undo())
    }
    fn component_rebuild_for_redo(&self) -> Option<(u32, u32, Vec<ComponentSlotData>)> {
        self.commands.iter().rev().find_map(|c| c.component_rebuild_for_redo())
    }
    fn actor_inspect_notify(&self) -> Option<(u32, u32)> {
        self.commands.iter().find_map(|c| c.actor_inspect_notify())
    }
    fn actor_dfs_selection_after_undo(&self) -> Option<(Vec<usize>, Option<usize>)> {
        self.commands.iter().rev().find_map(|c| c.actor_dfs_selection_after_undo())
    }
    fn actor_dfs_selection_after_redo(&self) -> Option<(Vec<usize>, Option<usize>)> {
        self.commands.iter().rev().find_map(|c| c.actor_dfs_selection_after_redo())
    }
}

// ============================================================
//  MultiActorDragTransformCommand — マルチ選択ドラッグ（非プライマリアクター）
//
//  ActorTreeSnapshotCommand の代替。GPU 再構築を行わず
//  MC の instance_mats[0] と ActorTransform を直接書き換える軽量コマンド。
// ============================================================

/// マルチ選択ドラッグで移動した非プライマリアクターの変換を軽量に Undo/Redo する。
pub struct MultiActorDragTransformCommand {
    pub wl: u32,
    /// (dfs_id, ドラッグ前の行列, ドラッグ後の行列)
    pub transforms: Vec<(u32, [[f32; 4]; 4], [[f32; 4]; 4])>,
}

impl Command for MultiActorDragTransformCommand {
    fn execute(&mut self, scene: &mut Scene) {
        for &(dfs_id, _, new_mat) in &self.transforms {
            set_mc_mat_in_actor(scene, self.wl, dfs_id, 0, new_mat);
            set_actor_transform(scene, self.wl, dfs_id, Transform::from_mat4(&new_mat));
        }
    }
    fn undo(&mut self, scene: &mut Scene) {
        for &(dfs_id, old_mat, _) in &self.transforms {
            set_mc_mat_in_actor(scene, self.wl, dfs_id, 0, old_mat);
            set_actor_transform(scene, self.wl, dfs_id, Transform::from_mat4(&old_mat));
        }
    }
}

// ============================================================
//  ActorDfsSelectionCommand — アクター DFS 選択状態の変更
// ============================================================

/// アクター DFS 選択を Undo/Redo するコマンド。
/// execute/undo は No-op で、AppBase が peek_*_actor_dfs_selection() で読み取って反映する。
pub struct ActorDfsSelectionCommand {
    pub before_dfs_ids: Vec<usize>,
    pub after_dfs_ids:  Vec<usize>,
    pub before_primary: Option<usize>,
    pub after_primary:  Option<usize>,
}

impl Command for ActorDfsSelectionCommand {
    fn execute(&mut self, _scene: &mut Scene) {}
    fn undo(&mut self, _scene: &mut Scene) {}
    fn actor_dfs_selection_after_undo(&self) -> Option<(Vec<usize>, Option<usize>)> {
        Some((self.before_dfs_ids.clone(), self.before_primary))
    }
    fn actor_dfs_selection_after_redo(&self) -> Option<(Vec<usize>, Option<usize>)> {
        Some((self.after_dfs_ids.clone(), self.after_primary))
    }
}

// ============================================================
//  ActorTransformCommand — アクター自身の Transform 変更
// ============================================================

/// アクター自身の Transform（位置・回転・スケール）変更を Undo/Redo するコマンド。
pub struct ActorTransformCommand {
    pub world_line:    u32,
    pub dfs_id:        u32,
    pub old_transform: Transform,
    pub new_transform: Transform,
}

impl Command for ActorTransformCommand {
    fn execute(&mut self, scene: &mut Scene) {
        set_actor_transform(scene, self.world_line, self.dfs_id, self.new_transform.clone());
    }
    fn undo(&mut self, scene: &mut Scene) {
        set_actor_transform(scene, self.world_line, self.dfs_id, self.old_transform.clone());
    }
    fn actor_inspect_notify(&self) -> Option<(u32, u32)> {
        Some((self.world_line, self.dfs_id))
    }
}

// ============================================================
//  ActorGroupTransformCommand — actor edit モードの全インスタンス一括移動
//  instance_mats と actor transform を同時に undo/redo する。
// ============================================================

/// actor edit モードでのアクター一括移動（アクター Transform ＋ 配下 MC インスタンス
/// ＋子孫アクターの変換）をまとめて Undo/Redo するコマンド。
pub struct ActorGroupTransformCommand {
    pub wl:         u32,
    pub dfs_id:     u32,
    pub old_tf:     Transform,
    pub new_tf:     Transform,
    /// (instance_idx, old_mat, new_mat) — 選択スロット MC インスタンスの変換
    pub transforms: Vec<(u32, [[f32; 4]; 4], [[f32; 4]; 4])>,
    /// (child_dfs_id, old_tf, new_tf, old_mc_mat, new_mc_mat) — 子孫アクター
    pub child_transforms: Vec<(u32, Transform, Transform, [[f32; 4]; 4], [[f32; 4]; 4])>,
    /// (slot_i, instance_idx, old_mat, new_mat) — 追加 MC スロット（複数 MC 対応）
    /// slot_i はアクター内の Model スロット連番。選択スロット以外の MC 全スロットを格納する。
    pub extra_slot_transforms: Vec<(usize, u32, [[f32; 4]; 4], [[f32; 4]; 4])>,
}

impl Command for ActorGroupTransformCommand {
    fn execute(&mut self, scene: &mut Scene) {
        for &(idx, _, new_mat) in &self.transforms {
            set_mc_mat_in_actor(scene, self.wl, self.dfs_id, idx, new_mat);
        }
        // 追加 MC スロットも更新する（複数 MC 対応）
        for &(slot_i, idx, _, new_mat) in &self.extra_slot_transforms {
            set_mc_mat_in_actor_at_slot(scene, self.wl, self.dfs_id, slot_i, idx, new_mat);
        }
        set_actor_transform(scene, self.wl, self.dfs_id, self.new_tf.clone());
        for (child_dfs, _, new_tf, _, new_mc_mat) in &self.child_transforms {
            set_mc_mat_in_actor(scene, self.wl, *child_dfs, 0, *new_mc_mat);
            set_actor_transform(scene, self.wl, *child_dfs, new_tf.clone());
        }
    }
    fn undo(&mut self, scene: &mut Scene) {
        for &(idx, old_mat, _) in &self.transforms {
            set_mc_mat_in_actor(scene, self.wl, self.dfs_id, idx, old_mat);
        }
        // 追加 MC スロットも元に戻す（複数 MC 対応）
        for &(slot_i, idx, old_mat, _) in &self.extra_slot_transforms {
            set_mc_mat_in_actor_at_slot(scene, self.wl, self.dfs_id, slot_i, idx, old_mat);
        }
        set_actor_transform(scene, self.wl, self.dfs_id, self.old_tf.clone());
        for (child_dfs, old_tf, _, old_mc_mat, _) in &self.child_transforms {
            set_mc_mat_in_actor(scene, self.wl, *child_dfs, 0, *old_mc_mat);
            set_actor_transform(scene, self.wl, *child_dfs, old_tf.clone());
        }
    }
    fn actor_inspect_notify(&self) -> Option<(u32, u32)> {
        Some((self.wl, self.dfs_id))
    }
}

// ── 内部ヘルパー ──────────────────────────────────────────────

/// world_line 0 の ModelComponent のインスタンス行列を更新する。
fn set_instance_mat(scene: &mut Scene, idx: u32, mat: [[f32; 4]; 4]) {
    if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(0) {
        if let Some(m) = mc.instance_mats.get_mut(idx as usize) {
            *m = mat;
        }
        mc.mark_batch_dirty();
    }
}

/// 指定世界線・インスタンスの行列を更新する。
fn set_instance_mat_in_wl(scene: &mut Scene, wl: u32, idx: u32, mat: [[f32; 4]; 4]) {
    if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(wl) {
        if let Some(m) = mc.instance_mats.get_mut(idx as usize) {
            *m = mat;
        }
        mc.mark_batch_dirty();
    }
}

/// DFS id でアクターの ModelComponent インスタンス行列を更新する（ECS 版）。
/// スロット専用 entity の MC を参照する（actor.entity ではない）。
fn set_mc_mat_in_actor(scene: &mut Scene, wl: u32, dfs_id: u32, idx: u32, mat: [[f32; 4]; 4]) {
    let mc_entity = find_mc_entity_by_dfs(&scene.actors, wl, dfs_id);
    if let Some(entity) = mc_entity {
        if let Some(mc) = scene.world.get_mut::<ModelComponent>(entity) {
            if let Some(m) = mc.instance_mats.get_mut(idx as usize) {
                *m = mat;
            }
            mc.mark_batch_dirty();
        }
    }
}

/// DFS id + スロットインデックスで指定 MC スロットのインスタンス行列を更新する。
/// slot_i はアクター内の Model スロット連番（0-indexed）。
/// 複数 MC スロット対応の Undo/Redo に使用する。
fn set_mc_mat_in_actor_at_slot(scene: &mut Scene, wl: u32, dfs_id: u32, slot_i: usize, idx: u32, mat: [[f32; 4]; 4]) {
    let mc_entity = find_mc_entity_at_slot_by_dfs(&scene.actors, wl, dfs_id, slot_i);
    if let Some(entity) = mc_entity {
        if let Some(mc) = scene.world.get_mut::<ModelComponent>(entity) {
            if let Some(m) = mc.instance_mats.get_mut(idx as usize) {
                *m = mat;
            }
            mc.mark_batch_dirty();
        }
    }
}

/// DFS id でアクターの slot_i 番目 MC スロット entity を返す。
fn find_mc_entity_at_slot_by_dfs(actors: &[Actor], wl: u32, dfs_id: u32, slot_i: usize) -> Option<Entity> {
    let mut c = 0u32;
    find_mc_entity_at_slot_in_actors(actors, wl, dfs_id, slot_i, &mut c)
}

fn find_mc_entity_at_slot_in_actors(actors: &[Actor], wl: u32, dfs_id: u32, slot_i: usize, c: &mut u32) -> Option<Entity> {
    for actor in actors {
        if actor.world_line != wl { continue; }
        if *c == dfs_id { return actor.mc_entity_at(slot_i); }
        *c += 1;
        if let Some(e) = find_mc_entity_at_slot_in_children(actor, dfs_id, slot_i, c) { return Some(e); }
    }
    None
}

fn find_mc_entity_at_slot_in_children(actor: &Actor, dfs_id: u32, slot_i: usize, c: &mut u32) -> Option<Entity> {
    for child in actor.children() {
        if *c == dfs_id { return child.mc_entity_at(slot_i); }
        *c += 1;
        if let Some(e) = find_mc_entity_at_slot_in_children(child, dfs_id, slot_i, c) { return Some(e); }
    }
    None
}

/// DFS id でアクターの最初の ModelComponent スロット entity を返す。
fn find_mc_entity_by_dfs(actors: &[Actor], wl: u32, dfs_id: u32) -> Option<Entity> {
    let mut c = 0u32;
    find_mc_entity_in_actors(actors, wl, dfs_id, &mut c)
}

fn find_mc_entity_in_actors(actors: &[Actor], wl: u32, dfs_id: u32, c: &mut u32) -> Option<Entity> {
    for actor in actors {
        if actor.world_line != wl { continue; }
        if *c == dfs_id { return actor.mc_entity(); }
        *c += 1;
        if let Some(e) = find_mc_entity_in_children(actor, dfs_id, c) { return Some(e); }
    }
    None
}

fn find_mc_entity_in_children(actor: &Actor, dfs_id: u32, c: &mut u32) -> Option<Entity> {
    for child in actor.children() {
        if *c == dfs_id { return child.mc_entity(); }
        *c += 1;
        if let Some(e) = find_mc_entity_in_children(child, dfs_id, c) { return Some(e); }
    }
    None
}

// ============================================================
//  CanvasTransformCommand — 2D アクターの CanvasTransform 変更
// ============================================================

/// 2D アクターの CanvasTransform（位置・回転・スケール）変更を Undo/Redo するコマンド。
pub struct CanvasTransformCommand {
    pub world_line: u32,
    pub dfs_id:     u32,
    pub old_ct:     CanvasTransform,
    pub new_ct:     CanvasTransform,
}

impl Command for CanvasTransformCommand {
    fn execute(&mut self, scene: &mut Scene) {
        set_canvas_transform(scene, self.world_line, self.dfs_id, self.new_ct.clone());
    }
    fn undo(&mut self, scene: &mut Scene) {
        set_canvas_transform(scene, self.world_line, self.dfs_id, self.old_ct.clone());
    }
    fn actor_inspect_notify(&self) -> Option<(u32, u32)> {
        Some((self.world_line, self.dfs_id))
    }
}

/// DFS id でアクターの CanvasTransform を更新する（ECS 版）。
fn set_canvas_transform(scene: &mut Scene, wl: u32, dfs_id: u32, ct: CanvasTransform) {
    let entity = find_entity_by_dfs(&scene.actors, wl, dfs_id);
    if let Some(entity) = entity {
        if let Some(t) = scene.world.get_mut::<CanvasTransform>(entity) {
            *t = ct;
        }
    }
}

/// DFS id でアクターの Transform を更新する（ECS 版）。
fn set_actor_transform(scene: &mut Scene, wl: u32, dfs_id: u32, tf: Transform) {
    let entity = find_entity_by_dfs(&scene.actors, wl, dfs_id);
    if let Some(entity) = entity {
        if let Some(t) = scene.world.get_mut::<Transform>(entity) {
            *t = tf;
        }
    }
}

/// DFS id でアクターのエンティティを探す（不変）。
fn find_entity_by_dfs(actors: &[Actor], wl: u32, dfs_id: u32) -> Option<Entity> {
    let mut c = 0u32;
    find_entity_in_actors(actors, wl, dfs_id, &mut c)
}

fn find_entity_in_actors(actors: &[Actor], wl: u32, dfs_id: u32, c: &mut u32) -> Option<Entity> {
    for actor in actors {
        if actor.world_line != wl { continue; }
        if *c == dfs_id { return Some(actor.entity); }
        *c += 1;
        if let Some(e) = find_entity_in_children(actor, dfs_id, c) { return Some(e); }
    }
    None
}

fn find_entity_in_children(actor: &Actor, dfs_id: u32, c: &mut u32) -> Option<Entity> {
    for child in actor.children() {
        if *c == dfs_id { return Some(child.entity); }
        *c += 1;
        if let Some(e) = find_entity_in_children(child, dfs_id, c) { return Some(e); }
    }
    None
}

// ============================================================
//  PluginFieldCommand — プラグインフィールド値の変更 Undo コマンド
// ============================================================

use crate::engine::components::PluginComponent;

/// プラグインコンポーネントのフィールド値を 1 つ変更する Undo コマンド。
///
/// slot_entity を直接保持することで find_actor_by_dfs を不要にしている。
/// execute: new_value を適用
/// undo:    old_value に戻す
pub struct PluginFieldCommand {
    /// スロット専用 ECS エンティティ（PluginComponent が格納されている）
    pub slot_entity:  Entity,
    /// 変更フィールドのキー
    pub key:          String,
    /// 変更前の値
    pub old_value:    String,
    /// 変更後の値
    pub new_value:    String,
    /// インスペクタ再送信用 (world_line, actor_dfs_id)
    pub world_line:   u32,
    pub actor_dfs_id: u32,
}

impl Command for PluginFieldCommand {
    fn execute(&mut self, scene: &mut Scene) {
        if let Some(pc) = scene.world.get_mut::<PluginComponent>(self.slot_entity) {
            pc.set_field(&self.key, &self.new_value);
        }
    }

    fn undo(&mut self, scene: &mut Scene) {
        if let Some(pc) = scene.world.get_mut::<PluginComponent>(self.slot_entity) {
            pc.set_field(&self.key, &self.old_value);
        }
    }

    fn actor_inspect_notify(&self) -> Option<(u32, u32)> {
        Some((self.world_line, self.actor_dfs_id))
    }
}
