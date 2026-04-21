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

// ============================================================
//  Command トレイト
// ============================================================

pub trait Command {
    fn execute(&mut self, scene: &mut Scene);
    fn undo(&mut self, scene: &mut Scene);
    /// true を返すと構造変更（追加・削除）を示す。
    /// Undo/Redo 後に選択状態・ヒエラルキー再送信が必要か判定するために使用。
    fn is_structural(&self) -> bool { false }
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
    /// 戻せた場合 `Some(is_structural)`、何もなければ `None` を返す。
    pub fn undo(&mut self, scene: &mut Scene) -> Option<bool> {
        if let Some(mut cmd) = self.past.pop() {
            let structural = cmd.is_structural();
            cmd.undo(scene);
            self.future.push(cmd);
            Some(structural)
        } else {
            None
        }
    }

    /// Undo した操作をやり直す。
    /// やり直せた場合 `Some(is_structural)`、何もなければ `None` を返す。
    pub fn redo(&mut self, scene: &mut Scene) -> Option<bool> {
        if let Some(mut cmd) = self.future.pop() {
            let structural = cmd.is_structural();
            cmd.execute(scene);
            self.past.push(cmd);
            Some(structural)
        } else {
            None
        }
    }

    pub fn can_undo(&self) -> bool { !self.past.is_empty() }
    pub fn can_redo(&self) -> bool { !self.future.is_empty() }
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
}

impl Command for SceneSnapshotCommand {
    fn execute(&mut self, scene: &mut Scene) {
        if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
            mc.instance_mats = self.after_mats.clone();
            mc.instance_meta = self.after_meta.clone();
            mc.group_meta    = self.after_groups.clone();
            mc.next_group_id = self.after_gid;
            mc.instanced_batch.mark_dirty();
        }
    }
    fn undo(&mut self, scene: &mut Scene) {
        if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
            mc.instance_mats = self.before_mats.clone();
            mc.instance_meta = self.before_meta.clone();
            mc.group_meta    = self.before_groups.clone();
            mc.next_group_id = self.before_gid;
            mc.instanced_batch.mark_dirty();
        }
    }
    fn is_structural(&self) -> bool { true }
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

// ── 内部ヘルパー ──────────────────────────────────────────────

fn set_instance_mat(scene: &mut Scene, idx: u32, mat: [[f32; 4]; 4]) {
    if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
        if let Some(m) = mc.instance_mats.get_mut(idx as usize) {
            *m = mat;
        }
        mc.instanced_batch.mark_dirty();
    }
}
