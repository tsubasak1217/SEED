// ============================================================
//  editor_preview/undo.rs — プレビューの出し入れの Undo（シーンを変えない印付きの木の写し）
//
//  【なぜ履歴へ積むのか】Undo の各コマンドは対象を entity ではなく (world_line, DFS 番号) で持ち、
//  適用時に引き直す（play_mode_ops.rs の enter_play の 0-a）。履歴に載らない木の変更が挟まると、
//  それより前に積んだ Undo の DFS 番号が別のアクタを指し、Ctrl+Z が別のアクタの値を書き換えてしまう。
//  だからプレビューの出し入れ・作り直しも、前後の木の写し（ActorTreeSnapshotCommand）を 1 件積む。
//
//  【何が違うのか】保存されるシーンの中身は変えないので `is_scene_neutral` だけ true を返す。
//  Undo/Redo の後のヒエラルキーは「未保存にしない」知らせ（HIERARCHY_QUIET）付きで送られる
//  （ipc_handler.rs の Undo / Redo の腕）。それ以外の振る舞いは中身（inner）とまったく同じ。
// ============================================================

use crate::engine::core::app_base::scene::Scene;
use crate::engine::core::app_base::undo::{ActorTreeSnapshotCommand, Command, CoverFieldSnapshots};
use crate::engine::structs::objects::actor::{ActorData, ComponentSlotData};

/// エディタのプレビューの出し入れ・作り直しの Undo（中身は前後の木の写し）。
pub(super) struct EditorPreviewTreeCommand {
    /// 前後の木の写し（復元の仕組みは普通のアクタの追加・削除と同じ）
    pub(super) inner: ActorTreeSnapshotCommand,
}

/// 「シーンを変えない」以外は中身と同じに振る舞う（Command の全メソッドを中身へ委ねる。
/// 中身の型が将来どれかを上書きしても、包みが黙って食い違わないように既定のままにしない）。
impl Command for EditorPreviewTreeCommand {
    fn execute(&mut self, scene: &mut Scene) {
        self.inner.execute(scene);
    }
    fn undo(&mut self, scene: &mut Scene) {
        self.inner.undo(scene);
    }
    fn is_structural(&self) -> bool {
        self.inner.is_structural()
    }
    fn selection_after_undo(&self) -> Option<Vec<u32>> {
        self.inner.selection_after_undo()
    }
    fn selection_after_redo(&self) -> Option<Vec<u32>> {
        self.inner.selection_after_redo()
    }
    fn actor_rebuild_for_undo(&self) -> Option<(u32, Vec<ActorData>)> {
        self.inner.actor_rebuild_for_undo()
    }
    fn actor_rebuild_for_redo(&self) -> Option<(u32, Vec<ActorData>)> {
        self.inner.actor_rebuild_for_redo()
    }
    fn component_rebuild_for_undo(&self) -> Option<(u32, u32, Vec<ComponentSlotData>)> {
        self.inner.component_rebuild_for_undo()
    }
    fn component_rebuild_for_redo(&self) -> Option<(u32, u32, Vec<ComponentSlotData>)> {
        self.inner.component_rebuild_for_redo()
    }
    fn slot_data_for_undo(&self) -> Option<(u32, u32, u32, ComponentSlotData)> {
        self.inner.slot_data_for_undo()
    }
    fn slot_data_for_redo(&self) -> Option<(u32, u32, u32, ComponentSlotData)> {
        self.inner.slot_data_for_redo()
    }
    fn actor_inspect_notify(&self) -> Option<(u32, u32)> {
        self.inner.actor_inspect_notify()
    }
    fn actor_dfs_selection_after_undo(&self) -> Option<(Vec<usize>, Option<usize>)> {
        self.inner.actor_dfs_selection_after_undo()
    }
    fn actor_dfs_selection_after_redo(&self) -> Option<(Vec<usize>, Option<usize>)> {
        self.inner.actor_dfs_selection_after_redo()
    }
    fn cover_fields_for_undo(&self) -> Option<CoverFieldSnapshots> {
        self.inner.cover_fields_for_undo()
    }
    fn cover_fields_for_redo(&self) -> Option<CoverFieldSnapshots> {
        self.inner.cover_fields_for_redo()
    }
    /// プレビューは保存されないので、出し入れは保存されるシーンの中身を変えない。
    fn is_scene_neutral(&self) -> bool {
        true
    }
}

// ============================================================
//  テスト — 「シーンを変えない」印の履歴での見え方
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::app_base::undo::{CompositeCommand, UndoHistory};

    /// テスト用: 中身が空の木の写し（世界線だけ指定）。
    fn tree_snapshot(world_line: u32) -> ActorTreeSnapshotCommand {
        ActorTreeSnapshotCommand { world_line, before_actors: Vec::new(), after_actors: Vec::new() }
    }

    /// テスト用: プレビューの出し入れの Undo。
    fn preview_command(world_line: u32) -> Box<dyn Command> {
        Box::new(EditorPreviewTreeCommand { inner: tree_snapshot(world_line) })
    }

    /// 包みを積んで Undo すると、戻したのが「シーンを変えない操作」と分かり、木の組み直しは中身と同じに出ること。
    /// Redo でも同じく分かること。
    #[test]
    fn neutral_wrapper_is_reported_after_undo_and_redo() {
        let mut scene = Scene::new("preview_undo");
        let mut history = UndoHistory::new();
        assert!(!history.peek_undone_is_scene_neutral(), "何も戻していなければ false");

        history.record(preview_command(2));
        let (structural, selection) = history.undo(&mut scene).expect("undo できること");
        assert!(structural, "木の写しなので構造変更として扱われること");
        assert!(selection.is_none());
        assert!(history.peek_undone_is_scene_neutral(), "戻したのはシーンを変えない操作");
        let (wl, _) = history.peek_undone_actor_rebuild().expect("木の組み直しは中身と同じに出ること");
        assert_eq!(wl, 2);

        history.redo(&mut scene).expect("redo できること");
        assert!(history.peek_redone_is_scene_neutral(), "やり直したのもシーンを変えない操作");
        assert!(history.peek_redone_actor_rebuild().is_some());
    }

    /// 普通の木の写し（アクタの追加・削除）はシーンを変える操作のまま（従来どおり未保存にする）。
    #[test]
    fn plain_tree_snapshot_is_not_neutral() {
        let mut scene = Scene::new("plain_undo");
        let mut history = UndoHistory::new();
        history.record(Box::new(tree_snapshot(0)));
        history.undo(&mut scene).expect("undo できること");
        assert!(!history.peek_undone_is_scene_neutral());
        history.redo(&mut scene).expect("redo できること");
        assert!(!history.peek_redone_is_scene_neutral());
    }

    /// まとめたコマンドは、空でなく全部がシーンを変えない操作のときだけ「変えない」。
    #[test]
    fn composite_is_neutral_only_when_all_are() {
        let all = CompositeCommand { commands: vec![preview_command(0), preview_command(1)] };
        assert!(all.is_scene_neutral(), "全部がプレビューの出し入れ");

        let mixed = CompositeCommand { commands: vec![preview_command(0), Box::new(tree_snapshot(0))] };
        assert!(!mixed.is_scene_neutral(), "普通の編集が混ざれば未保存にする");

        let empty = CompositeCommand { commands: Vec::new() };
        assert!(!empty.is_scene_neutral(), "空のまとめはシーンを変えない操作とはみなさない");
    }
}
