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

// ============================================================
//  Command トレイト
// ============================================================

pub trait Command {
    fn execute(&mut self, scene: &mut Scene);
    fn undo(&mut self, scene: &mut Scene);
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

    /// 直前の操作を元に戻す。戻せた場合 true を返す。
    pub fn undo(&mut self, scene: &mut Scene) -> bool {
        if let Some(mut cmd) = self.past.pop() {
            cmd.undo(scene);
            self.future.push(cmd);
            true
        } else {
            false
        }
    }

    /// Undo した操作をやり直す。やり直せた場合 true を返す。
    pub fn redo(&mut self, scene: &mut Scene) -> bool {
        if let Some(mut cmd) = self.future.pop() {
            cmd.execute(scene);
            self.past.push(cmd);
            true
        } else {
            false
        }
    }

    pub fn can_undo(&self) -> bool { !self.past.is_empty() }
    pub fn can_redo(&self) -> bool { !self.future.is_empty() }
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

// ── 内部ヘルパー ──────────────────────────────────────────────

fn set_instance_mat(scene: &mut Scene, idx: u32, mat: [[f32; 4]; 4]) {
    if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
        if let Some(m) = mc.instance_mats.get_mut(idx as usize) {
            *m = mat;
        }
        mc.instanced_batch.mark_dirty();
    }
}
