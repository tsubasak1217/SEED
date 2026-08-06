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

use crate::engine::components::model_component::{GroupMeta, InstanceMeta};
use crate::engine::components::{CanvasTransform, ModelComponent, Transform};
use crate::engine::core::app_base::scene::Scene;
use crate::engine::ecs::Entity;
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::{ActorData, ComponentSlotData};
use crate::engine::terrain::chunk_coord::ChunkCoord;
use crate::engine::terrain::cover::CoverField;

/// カバー場（地表の積雪・落ち葉等）のスナップショット群。
///
/// キーはチャンク座標、値はそのチャンクのカバー場まるごと。
/// **変化のあったチャンクだけ**を持つ差分表現である
/// （`terrain_cover_ops.rs::diff_cover_fields` が作る）。
pub type CoverFieldSnapshots = std::collections::HashMap<ChunkCoord, CoverField>;

// ============================================================
//  Command トレイト
// ============================================================

/// Undo/Redo 可能な単一操作を表す抽象。UndoHistory の past/future スタックに積まれる。
pub trait Command {
    fn execute(&mut self, scene: &mut Scene);
    fn undo(&mut self, scene: &mut Scene);
    /// true を返すと構造変更（追加・削除）を示す。
    fn is_structural(&self) -> bool {
        false
    }
    /// Undo 後に復元すべき選択状態。None なら変更しない。
    fn selection_after_undo(&self) -> Option<Vec<u32>> {
        None
    }
    /// Redo (re-execute) 後に復元すべき選択状態。None なら変更しない。
    fn selection_after_redo(&self) -> Option<Vec<u32>> {
        None
    }
    /// Undo 実行後に AppBase がアクターツリーを再構築するためのデータ。
    fn actor_rebuild_for_undo(&self) -> Option<(u32, Vec<ActorData>)> {
        None
    }
    /// Redo 実行後に AppBase がアクターツリーを再構築するためのデータ。
    fn actor_rebuild_for_redo(&self) -> Option<(u32, Vec<ActorData>)> {
        None
    }
    /// Undo 実行後に AppBase がコンポーネントスロットを再構築するためのデータ。
    fn component_rebuild_for_undo(&self) -> Option<(u32, u32, Vec<ComponentSlotData>)> {
        None
    }
    /// Redo 実行後に AppBase がコンポーネントスロットを再構築するためのデータ。
    fn component_rebuild_for_redo(&self) -> Option<(u32, u32, Vec<ComponentSlotData>)> {
        None
    }
    /// Undo 実行後に AppBase が **1 スロットだけ**再適用するためのデータ
    /// (world_line, actor_dfs_id, slot_idx, slot_data)。
    /// インスペクタのフィールド編集（SlotFieldEditCommand）で使う。
    /// 全スロット再構築（component_rebuild_*）と違い、対象スロットの ECS エンティティを
    /// 維持したまま値だけ差し替えるため、同一アクタの他スロット（モデルの GPU 資源・
    /// スクリプトの CLR インスタンス・再生中の音声）を巻き添えにしない。
    fn slot_data_for_undo(&self) -> Option<(u32, u32, u32, ComponentSlotData)> {
        None
    }
    /// Redo 実行後に AppBase が 1 スロットだけ再適用するためのデータ。
    fn slot_data_for_redo(&self) -> Option<(u32, u32, u32, ComponentSlotData)> {
        None
    }
    /// Undo/Redo 後にインスペクターへ通知すべきアクターの (world_line, dfs_id)。
    fn actor_inspect_notify(&self) -> Option<(u32, u32)> {
        None
    }
    /// Undo 実行後に AppBase が復元すべきアクター DFS 選択状態 (dfs_ids, primary)。
    fn actor_dfs_selection_after_undo(&self) -> Option<(Vec<usize>, Option<usize>)> {
        None
    }
    /// Redo 実行後に AppBase が復元すべきアクター DFS 選択状態 (dfs_ids, primary)。
    fn actor_dfs_selection_after_redo(&self) -> Option<(Vec<usize>, Option<usize>)> {
        None
    }
    /// Undo 実行後に AppBase が地形へ書き戻すべきカバー場スナップショット。
    ///
    /// カバー場は `App.terrain`（Scene の外）にあるため `execute`/`undo` では触れない。
    /// `SlotFieldEditCommand` と同じく **AppBase が peek して適用する**流儀を採る。
    fn cover_fields_for_undo(&self) -> Option<CoverFieldSnapshots> {
        None
    }
    /// Redo 実行後に AppBase が地形へ書き戻すべきカバー場スナップショット。
    fn cover_fields_for_redo(&self) -> Option<CoverFieldSnapshots> {
        None
    }
}

// ============================================================
//  UndoHistory
// ============================================================

const MAX_HISTORY: usize = 100;

/// Undo/Redo 履歴本体。実行済みコマンドの past スタックと、Undo 済みコマンドの
/// future スタックを管理し、Command トレイトを介して Scene への再適用を仲介する。
pub struct UndoHistory {
    past: Vec<Box<dyn Command>>,
    future: Vec<Box<dyn Command>>,
}

impl UndoHistory {
    pub fn new() -> Self {
        Self {
            past: Vec::new(),
            future: Vec::new(),
        }
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

    /// past スタックの深さ。
    ///
    /// インスペクタのフィールド編集マージ（field_edit.rs）が
    /// 「直前に自分が積んだコマンドがまだ先頭にあるか」を判定するために使う。
    /// 記録直後の深さと一致していれば、その間に他の操作は積まれていない。
    pub fn past_len(&self) -> usize {
        self.past.len()
    }

    pub fn can_undo(&self) -> bool {
        !self.past.is_empty()
    }
    pub fn can_redo(&self) -> bool {
        !self.future.is_empty()
    }

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
    /// undo() 直後: 1 スロットだけ再適用すべきデータ（インスペクタのフィールド編集）。
    pub fn peek_undone_slot_data(&self) -> Option<(u32, u32, u32, ComponentSlotData)> {
        self.future.last()?.slot_data_for_undo()
    }
    /// redo() 直後: 1 スロットだけ再適用すべきデータ（インスペクタのフィールド編集）。
    pub fn peek_redone_slot_data(&self) -> Option<(u32, u32, u32, ComponentSlotData)> {
        self.past.last()?.slot_data_for_redo()
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
    /// undo() 直後: 地形へ書き戻すべきカバー場スナップショット（`CoverFieldEditCommand`）。
    ///
    /// 借用の都合で複製を返す（呼び出し側が `&mut self`（App）で書き戻すため、
    /// 履歴への不変借用を保ったままにできない）。1 エントリは変化チャンク数 × 2KB で、
    /// Ctrl+Z 1 回につき 1 度しか起きないので複製コストは問題にならない。
    pub fn peek_undone_cover_fields(&self) -> Option<CoverFieldSnapshots> {
        self.future.last()?.cover_fields_for_undo()
    }
    /// redo() 直後: 地形へ書き戻すべきカバー場スナップショット。
    pub fn peek_redone_cover_fields(&self) -> Option<CoverFieldSnapshots> {
        self.past.last()?.cover_fields_for_redo()
    }
}

// ============================================================
//  SceneSnapshotCommand — 構造変更（追加・削除）の Undo/Redo
// ============================================================

/// ModelComponent の完全スナップショット。
/// 追加・削除操作の前後状態を保持し、任意の方向に復元する。
pub struct SceneSnapshotCommand {
    pub before_mats: Vec<[[f32; 4]; 4]>,
    pub before_meta: Vec<InstanceMeta>,
    pub before_groups: Vec<GroupMeta>,
    pub before_gid: u32,
    pub after_mats: Vec<[[f32; 4]; 4]>,
    pub after_meta: Vec<InstanceMeta>,
    pub after_groups: Vec<GroupMeta>,
    pub after_gid: u32,
    pub before_selection: Vec<u32>,
    pub after_selection: Vec<u32>,
}

impl Command for SceneSnapshotCommand {
    fn execute(&mut self, scene: &mut Scene) {
        if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(0) {
            mc.instance_mats = self.after_mats.clone();
            mc.instance_meta = self.after_meta.clone();
            mc.group_meta = self.after_groups.clone();
            mc.next_group_id = self.after_gid;
            mc.mark_batch_dirty();
        }
    }
    fn undo(&mut self, scene: &mut Scene) {
        if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(0) {
            mc.instance_mats = self.before_mats.clone();
            mc.instance_meta = self.before_meta.clone();
            mc.group_meta = self.before_groups.clone();
            mc.next_group_id = self.before_gid;
            mc.mark_batch_dirty();
        }
    }
    fn is_structural(&self) -> bool {
        true
    }
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
    pub old_mat: [[f32; 4]; 4],
    pub new_mat: [[f32; 4]; 4],
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
        for &(idx, _, new_mat) in &self.transforms {
            set_instance_mat(scene, idx, new_mat);
        }
    }
    fn undo(&mut self, scene: &mut Scene) {
        for &(idx, old_mat, _) in &self.transforms {
            set_instance_mat(scene, idx, old_mat);
        }
    }
}

// ============================================================
//  SelectionCommand — 選択状態の変更
// ============================================================

/// 選択状態（選択中インスタンス集合）の変更を Undo/Redo するコマンド。
pub struct SelectionCommand {
    pub before: Vec<u32>,
    pub after: Vec<u32>,
}

impl Command for SelectionCommand {
    fn execute(&mut self, _scene: &mut Scene) {}
    fn undo(&mut self, _scene: &mut Scene) {}
    fn selection_after_undo(&self) -> Option<Vec<u32>> {
        Some(self.before.clone())
    }
    fn selection_after_redo(&self) -> Option<Vec<u32>> {
        Some(self.after.clone())
    }
}

// ============================================================
//  ActorTreeSnapshotCommand — アクターツリー構造変更の Undo/Redo
// ============================================================

/// ADD_ACTOR / REMOVE_ACTOR のスナップショット。
/// execute/undo は No-op で、AppBase が peek_*_actor_rebuild() を使い GPU 再構築する。
pub struct ActorTreeSnapshotCommand {
    pub world_line: u32,
    pub before_actors: Vec<ActorData>,
    pub after_actors: Vec<ActorData>,
}

impl Command for ActorTreeSnapshotCommand {
    fn execute(&mut self, _scene: &mut Scene) {}
    fn undo(&mut self, _scene: &mut Scene) {}
    fn is_structural(&self) -> bool {
        true
    }
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
    pub world_line: u32,
    pub actor_dfs_id: u32,
    pub before_slots: Vec<ComponentSlotData>,
    pub after_slots: Vec<ComponentSlotData>,
}

impl Command for ComponentSlotsSnapshotCommand {
    fn execute(&mut self, _scene: &mut Scene) {}
    fn undo(&mut self, _scene: &mut Scene) {}
    fn is_structural(&self) -> bool {
        true
    }
    fn component_rebuild_for_undo(&self) -> Option<(u32, u32, Vec<ComponentSlotData>)> {
        Some((
            self.world_line,
            self.actor_dfs_id,
            self.before_slots.clone(),
        ))
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
        for cmd in &mut self.commands {
            cmd.execute(scene);
        }
    }
    fn undo(&mut self, scene: &mut Scene) {
        for cmd in self.commands.iter_mut().rev() {
            cmd.undo(scene);
        }
    }
    fn is_structural(&self) -> bool {
        self.commands.iter().any(|c| c.is_structural())
    }
    fn selection_after_undo(&self) -> Option<Vec<u32>> {
        self.commands
            .iter()
            .rev()
            .find_map(|c| c.selection_after_undo())
    }
    fn selection_after_redo(&self) -> Option<Vec<u32>> {
        self.commands
            .iter()
            .rev()
            .find_map(|c| c.selection_after_redo())
    }
    fn actor_rebuild_for_undo(&self) -> Option<(u32, Vec<ActorData>)> {
        self.commands
            .iter()
            .rev()
            .find_map(|c| c.actor_rebuild_for_undo())
    }
    fn actor_rebuild_for_redo(&self) -> Option<(u32, Vec<ActorData>)> {
        self.commands
            .iter()
            .rev()
            .find_map(|c| c.actor_rebuild_for_redo())
    }
    fn component_rebuild_for_undo(&self) -> Option<(u32, u32, Vec<ComponentSlotData>)> {
        self.commands
            .iter()
            .rev()
            .find_map(|c| c.component_rebuild_for_undo())
    }
    fn component_rebuild_for_redo(&self) -> Option<(u32, u32, Vec<ComponentSlotData>)> {
        self.commands
            .iter()
            .rev()
            .find_map(|c| c.component_rebuild_for_redo())
    }
    fn slot_data_for_undo(&self) -> Option<(u32, u32, u32, ComponentSlotData)> {
        self.commands.iter().rev().find_map(|c| c.slot_data_for_undo())
    }
    fn slot_data_for_redo(&self) -> Option<(u32, u32, u32, ComponentSlotData)> {
        self.commands.iter().rev().find_map(|c| c.slot_data_for_redo())
    }
    fn actor_inspect_notify(&self) -> Option<(u32, u32)> {
        self.commands.iter().find_map(|c| c.actor_inspect_notify())
    }
    fn actor_dfs_selection_after_undo(&self) -> Option<(Vec<usize>, Option<usize>)> {
        self.commands
            .iter()
            .rev()
            .find_map(|c| c.actor_dfs_selection_after_undo())
    }
    fn actor_dfs_selection_after_redo(&self) -> Option<(Vec<usize>, Option<usize>)> {
        self.commands
            .iter()
            .rev()
            .find_map(|c| c.actor_dfs_selection_after_redo())
    }
    fn cover_fields_for_undo(&self) -> Option<CoverFieldSnapshots> {
        self.commands.iter().rev().find_map(|c| c.cover_fields_for_undo())
    }
    fn cover_fields_for_redo(&self) -> Option<CoverFieldSnapshots> {
        self.commands.iter().rev().find_map(|c| c.cover_fields_for_redo())
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
    pub after_dfs_ids: Vec<usize>,
    pub before_primary: Option<usize>,
    pub after_primary: Option<usize>,
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
    pub world_line: u32,
    pub dfs_id: u32,
    pub old_transform: Transform,
    pub new_transform: Transform,
}

impl Command for ActorTransformCommand {
    fn execute(&mut self, scene: &mut Scene) {
        set_actor_transform(
            scene,
            self.world_line,
            self.dfs_id,
            self.new_transform.clone(),
        );
    }
    fn undo(&mut self, scene: &mut Scene) {
        set_actor_transform(
            scene,
            self.world_line,
            self.dfs_id,
            self.old_transform.clone(),
        );
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
    pub wl: u32,
    pub dfs_id: u32,
    pub old_tf: Transform,
    pub new_tf: Transform,
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
fn set_mc_mat_in_actor_at_slot(
    scene: &mut Scene,
    wl: u32,
    dfs_id: u32,
    slot_i: usize,
    idx: u32,
    mat: [[f32; 4]; 4],
) {
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
fn find_mc_entity_at_slot_by_dfs(
    actors: &[Actor],
    wl: u32,
    dfs_id: u32,
    slot_i: usize,
) -> Option<Entity> {
    let mut c = 0u32;
    find_mc_entity_at_slot_in_actors(actors, wl, dfs_id, slot_i, &mut c)
}

fn find_mc_entity_at_slot_in_actors(
    actors: &[Actor],
    wl: u32,
    dfs_id: u32,
    slot_i: usize,
    c: &mut u32,
) -> Option<Entity> {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        if *c == dfs_id {
            return actor.mc_entity_at(slot_i);
        }
        *c += 1;
        if let Some(e) = find_mc_entity_at_slot_in_children(actor, dfs_id, slot_i, c) {
            return Some(e);
        }
    }
    None
}

fn find_mc_entity_at_slot_in_children(
    actor: &Actor,
    dfs_id: u32,
    slot_i: usize,
    c: &mut u32,
) -> Option<Entity> {
    for child in actor.children() {
        if *c == dfs_id {
            return child.mc_entity_at(slot_i);
        }
        *c += 1;
        if let Some(e) = find_mc_entity_at_slot_in_children(child, dfs_id, slot_i, c) {
            return Some(e);
        }
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
        if actor.world_line != wl {
            continue;
        }
        if *c == dfs_id {
            return actor.mc_entity();
        }
        *c += 1;
        if let Some(e) = find_mc_entity_in_children(actor, dfs_id, c) {
            return Some(e);
        }
    }
    None
}

fn find_mc_entity_in_children(actor: &Actor, dfs_id: u32, c: &mut u32) -> Option<Entity> {
    for child in actor.children() {
        if *c == dfs_id {
            return child.mc_entity();
        }
        *c += 1;
        if let Some(e) = find_mc_entity_in_children(child, dfs_id, c) {
            return Some(e);
        }
    }
    None
}

// ============================================================
//  CanvasTransformCommand — 2D アクターの CanvasTransform 変更
// ============================================================

/// 2D アクターの CanvasTransform（位置・回転・スケール）変更を Undo/Redo するコマンド。
pub struct CanvasTransformCommand {
    pub world_line: u32,
    pub dfs_id: u32,
    pub old_ct: CanvasTransform,
    pub new_ct: CanvasTransform,
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
        if actor.world_line != wl {
            continue;
        }
        if *c == dfs_id {
            return Some(actor.entity);
        }
        *c += 1;
        if let Some(e) = find_entity_in_children(actor, dfs_id, c) {
            return Some(e);
        }
    }
    None
}

fn find_entity_in_children(actor: &Actor, dfs_id: u32, c: &mut u32) -> Option<Entity> {
    for child in actor.children() {
        if *c == dfs_id {
            return Some(child.entity);
        }
        *c += 1;
        if let Some(e) = find_entity_in_children(child, dfs_id, c) {
            return Some(e);
        }
    }
    None
}


// ============================================================
//  SlotFieldEditCommand — インスペクタのフィールド編集（汎用）
// ============================================================

/// コンポーネントスロット 1 個分の値変更をまるごと Undo/Redo する汎用コマンド。
///
/// 【設計】
/// インスペクタから飛んでくる `SET_*_FIELD` 系 IPC は種類が数十あり、
/// ハンドラごとに個別 Undo コマンドを書くと必ず対応漏れが出る。
/// そこで **IPC 適用の前後でスロットのシリアライズ表現（ComponentSlotData）を
/// スナップショットし、差分があれば本コマンドを積む**という 1 本の経路に集約する
/// （分類表は app/field_edit.rs、記録は ipc_handler.rs のディスパッチ入口）。
///
/// `execute` / `undo` は Scene だけでは完結しない（モデルの GPU 再アップロードや
/// スクリプトの CLR インスタンス生成に App 側の資源が要る）ため No-op とし、
/// AppBase が `slot_data_for_undo/redo()` を読んで `apply_slot_data` で適用する。
/// これは ComponentSlotsSnapshotCommand と同じ流儀である。
pub struct SlotFieldEditCommand {
    pub world_line: u32,
    pub actor_dfs_id: u32,
    /// アクタ内のスロット連番（ComponentSlot のインデックス）。
    pub slot_idx: u32,
    /// 編集前のスロット状態（名前・enabled・コンポーネント値を含む）。
    pub before: ComponentSlotData,
    /// 編集後のスロット状態。
    pub after: ComponentSlotData,
}

impl Command for SlotFieldEditCommand {
    fn execute(&mut self, _scene: &mut Scene) {}
    fn undo(&mut self, _scene: &mut Scene) {}
    fn slot_data_for_undo(&self) -> Option<(u32, u32, u32, ComponentSlotData)> {
        Some((
            self.world_line,
            self.actor_dfs_id,
            self.slot_idx,
            self.before.clone(),
        ))
    }
    fn slot_data_for_redo(&self) -> Option<(u32, u32, u32, ComponentSlotData)> {
        Some((
            self.world_line,
            self.actor_dfs_id,
            self.slot_idx,
            self.after.clone(),
        ))
    }
    fn actor_inspect_notify(&self) -> Option<(u32, u32)> {
        Some((self.world_line, self.actor_dfs_id))
    }
}

// ============================================================
//  ActorActiveCommand — アクターのアクティブフラグ変更
// ============================================================

/// アクターの active フラグ（Unity の SetActive 相当）の変更を Undo/Redo するコマンド。
/// ヒエラルキー表示にも出るため is_structural = true とし、Undo 後に再送信させる。
pub struct ActorActiveCommand {
    pub world_line: u32,
    pub dfs_id: u32,
    pub before: bool,
    pub after: bool,
}

impl Command for ActorActiveCommand {
    fn execute(&mut self, scene: &mut Scene) {
        set_actor_active(scene, self.world_line, self.dfs_id, self.after);
    }
    fn undo(&mut self, scene: &mut Scene) {
        set_actor_active(scene, self.world_line, self.dfs_id, self.before);
    }
    fn is_structural(&self) -> bool {
        true
    }
    fn actor_inspect_notify(&self) -> Option<(u32, u32)> {
        Some((self.world_line, self.dfs_id))
    }
}

/// DFS id でアクターの active フラグを更新する。
/// アクターツリー探索は app 側の既存ユーティリティを共有する（DFS 規則の二重実装を避ける）。
fn set_actor_active(scene: &mut Scene, wl: u32, dfs_id: u32, active: bool) {
    use crate::engine::core::app_base::app::find_actor_by_dfs_mut;
    let mut c = 0u32;
    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, dfs_id, &mut c) {
        actor.active = active;
    }
}

// ============================================================
//  SceneShadingCommand — シーン設定の「シェーダ」まわりの編集
// ============================================================

/// シーン既定のシェーディング設定（アセットパス・パラメータ上書き値・`@ref` バインド）
/// をまるごと Undo/Redo するコマンド。
///
/// 【設計】
/// シーン設定はコンポーネントスロットではないので `SlotFieldEditCommand` に載らない。
/// とはいえ対象は「3 つの小さな値」だけなので、**変更前後をそのまま持つ**のが最も単純で、
/// スナップショットの取り方も他のフィールド編集と同じ（`field_edit.rs` の共通機構）にできる。
///
/// `execute` / `undo` は Scene だけで完結する（GPU 資源も CLR も絡まない純粋なデータ）ため、
/// `SlotFieldEditCommand` と違ってここで直接書き戻す。
/// 巻き戻した内容をシーン設定ウィンドウへ反映させるのは AppBase の責務
/// （Undo/Redo の後に `SCENE_SHADING_PARAMS` を送り直す）。
pub struct SceneShadingCommand {
    /// 編集前の状態。
    pub before: SceneShadingState,
    /// 編集後の状態。
    pub after:  SceneShadingState,
}

/// シーン既定のシェーディング設定 1 式（Undo のスナップショット単位）。
#[derive(Clone, PartialEq, Debug, Default)]
pub struct SceneShadingState {
    /// シーン既定のシェーディングアセットのパス（未設定は None）。
    pub asset:    Option<String>,
    /// パラメータの上書き値（アセット既定値からの差分）。
    pub params:   std::collections::BTreeMap<String, [f32; 4]>,
    /// `@ref` パラメータのバインド先。
    pub bindings: std::collections::BTreeMap<String, String>,
}

impl SceneShadingState {
    /// 現在のシーンから状態を取り出す。
    pub fn capture(scene: &Scene) -> Self {
        Self {
            asset:    scene.shading_asset.clone(),
            params:   scene.shading_params.clone(),
            bindings: scene.shading_bindings.clone(),
        }
    }

    /// シーンへ状態を書き戻す。
    pub fn apply(&self, scene: &mut Scene) {
        scene.shading_asset    = self.asset.clone();
        scene.shading_params   = self.params.clone();
        scene.shading_bindings = self.bindings.clone();
    }
}

impl Command for SceneShadingCommand {
    fn execute(&mut self, scene: &mut Scene) { self.after.apply(scene); }
    fn undo(&mut self, scene: &mut Scene)    { self.before.apply(scene); }
}

// ============================================================
//  CoverFieldEditCommand — 地表カバー場（積雪・落ち葉等）の編集
// ============================================================

/// カバー場の 1 操作（再生セッション／N 秒進める／全消去）を Undo/Redo するコマンド。
///
/// 【なぜ地形専用スタックではなくメイン履歴なのか】
///   カバー場を積もらせる操作の入口は **`CoverEmitterComponent` を持つアクタのインスペクタ**
///   であり、ユーザーから見ればコンポーネント操作の一部である。ところが地形専用スタック
///   （`TerrainEdit` / `TERRAIN_UNDO`）はエディタが**地形編集モード中にしか送らない**ため、
///   「コンポーネントを追加 → シミュレート → Ctrl+Z」で雪が戻らず、代わりにコンポーネント
///   追加が取り消されるという壊れた順序になっていた。カバーはメイン履歴（`UndoHistory`）で
///   管轄し、地形密度・ペイントだけを地形専用スタックに残すことで積み順を 1 本化する。
///
/// 【`execute` / `undo` が No-op である理由】
///   カバー場の実体は `App.terrain`（Scene の外）にあり、書き戻しには
///   再焼き付け予約（`cover_pending_apply`）や `.tcover` のダーティ化も伴う。
///   Scene だけでは完結しないので、`SlotFieldEditCommand` と同じく AppBase が
///   `cover_fields_for_undo/redo()` を peek して `restore_cover_snapshots` で適用する。
///
/// 【メモリ】1 チャンク 2KB × 変化チャンク数 × 2（before + after）。
///   既定 48 チャンクが全変化しても約 192KB で、密度 1 ストローク（143KB × 2 × チャンク数）
///   よりはるかに軽い。
pub struct CoverFieldEditCommand {
    /// 操作前のカバー場（変化のあったチャンクのみ）。
    pub before: CoverFieldSnapshots,
    /// 操作後のカバー場（`before` と同じキー集合）。
    pub after: CoverFieldSnapshots,
}

impl Command for CoverFieldEditCommand {
    fn execute(&mut self, _scene: &mut Scene) {}
    fn undo(&mut self, _scene: &mut Scene) {}
    fn cover_fields_for_undo(&self) -> Option<CoverFieldSnapshots> {
        Some(self.before.clone())
    }
    fn cover_fields_for_redo(&self) -> Option<CoverFieldSnapshots> {
        Some(self.after.clone())
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用: 指定チャンクへ「量 `amount` の素材 0 が積もった」カバー場を作る。
    fn cover_snapshot(coord: ChunkCoord, amount: f32) -> CoverFieldSnapshots {
        let mut field = CoverField::new();
        field.deposit(0, 0, 0, amount);
        let mut map = CoverFieldSnapshots::new();
        map.insert(coord, field);
        map
    }

    /// テスト用: カバー場の 1 操作ぶんのコマンド（空 → 積もった状態）。
    fn cover_command(coord: ChunkCoord) -> Box<dyn Command> {
        let mut before = CoverFieldSnapshots::new();
        before.insert(coord, CoverField::new());
        Box::new(CoverFieldEditCommand {
            before,
            after: cover_snapshot(coord, 1.0),
        })
    }

    /// 「コンポーネント追加 → カバー場シミュレート → Ctrl+Z ×2」の積み順を固定する。
    ///
    /// これは実際に報告された不具合（カバーの Undo が地形専用スタックへ積まれていたため、
    /// 通常の Ctrl+Z が雪ではなくコンポーネント追加を取り消した）の回帰テストである。
    /// 1 回目の Undo でカバー場が戻り、2 回目でコンポーネント追加が戻ること
    /// ＝ **両者が同一の履歴に、操作した順で積まれていること**を検証する。
    #[test]
    fn cover_edit_undoes_before_earlier_component_add() {
        let mut scene = Scene::new("undo_order_test");
        let mut history = UndoHistory::new();
        let coord = ChunkCoord::new(0, 0, 0);

        // ① コンポーネント追加（Cover Emitter を 1 つ足したスロット構成のスナップショット）。
        use crate::engine::components::cover_emitter_component::CoverEmitterComponent;
        use crate::engine::components::ComponentData;
        let added_slot = ComponentSlotData {
            name: "CoverEmitter".to_string(),
            component: ComponentData::CoverEmitterComponent(
                CoverEmitterComponent::default().to_data(),
            ),
            enabled: true,
        };
        history.record(Box::new(ComponentSlotsSnapshotCommand {
            world_line: 0,
            actor_dfs_id: 0,
            before_slots: Vec::new(),
            after_slots: vec![added_slot],
        }));
        // ② カバー場のシミュレート。
        history.record(cover_command(coord));

        // ─── 1 回目の Ctrl+Z: カバー場が戻る ───
        assert!(history.undo(&mut scene).is_some(), "1 回目の undo は成功する");
        let restored = history
            .peek_undone_cover_fields()
            .expect("1 回目の undo はカバー場の巻き戻しであること");
        assert!(
            restored[&coord].is_empty(),
            "積もる前（空のカバー場）へ戻ること"
        );
        assert!(
            history.peek_undone_component_rebuild().is_none(),
            "1 回目の undo でコンポーネント構成を巻き戻さないこと"
        );

        // ─── 2 回目の Ctrl+Z: コンポーネント追加が戻る ───
        assert!(history.undo(&mut scene).is_some(), "2 回目の undo は成功する");
        let (_wl, _dfs, slots) = history
            .peek_undone_component_rebuild()
            .expect("2 回目の undo はコンポーネント構成の巻き戻しであること");
        assert!(slots.is_empty(), "コンポーネント追加前（スロット無し）へ戻ること");
        assert!(
            history.peek_undone_cover_fields().is_none(),
            "2 回目の undo でカバー場を巻き戻さないこと"
        );
    }

    /// Redo がカバー場を「操作後」へ進めること（undo と対称であること）。
    #[test]
    fn cover_edit_redo_restores_after_state() {
        let mut scene = Scene::new("undo_order_test");
        let mut history = UndoHistory::new();
        let coord = ChunkCoord::new(1, 0, -2);

        history.record(cover_command(coord));
        history.undo(&mut scene);
        assert!(history.redo(&mut scene).is_some(), "redo は成功する");

        let restored = history
            .peek_redone_cover_fields()
            .expect("redo はカバー場を進めること");
        assert!(!restored[&coord].is_empty(), "積もった状態へ戻ること");
    }
}
