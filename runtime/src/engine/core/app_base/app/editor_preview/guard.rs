// ============================================================
//  editor_preview/guard.rs — プレビューへ／から中身を動かす・中へ足す木の編集を拒否する（データを守るため）
//
//  【拒否するもの】（判断は純粋関数 preview_edit_refusal。文言は定数）
//  ・既存の中身をプレビューへ／から動かす: REPARENT・CREATE_GROUP_WITH_CHILDREN・WRAP_ACTOR
//      中から外へ → 「プレビューは動かせません」（保存されない中身が保存されるアクタの下へ出てしまう）
//      外から中へ → 「プレビューの中へは移せません」（利用者のアクタが保存されないプレビューの中へ消えてしまう）
//      両方に当たるときは前者
//  ・プレビューの中へ足す: CREATE_GROUP（親あり）・ADD_ACTOR / ADD_ACTOR_2D（親あり）・ADD_ACTOR_CHILD・
//    ADD_ACTOR_2D_CHILD・ADD_TEMPLATE_ACTOR（親あり）・CREATE_SPRITE_BONE_ACTORS・PASTE（選択のどれかが中）
//      → 「プレビューの中には追加できません」（足したアクタは保存されない）
//  【拒否しないもの】
//  ・値の編集・名前・表示・削除: メモリ上だけの変更で、保存の濾過・作り直しで消える
//    （エディタ側がプレビューの行を読み取り専用にしている）。
//  ・PREVIEW_PREFAB: プレビューの中にプレビューを入れ子にするのは可（作り直しも入れ子ごと行う）。
//  ・DROP_ACTOR: 置き先はトップレベルのキャンバスだけで、プレビューは必ず親を持つので当たらない。
//  判定は Edit のときだけ（Play 中の木にはシーンのプレビューが無い）。DFS は表示中の世界線（active_world_line）の木で数える。
// ============================================================

use crate::engine::core::app_base::ipc::IpcCommand;

use super::super::{App, RuntimeMode};
use super::tree::is_dfs_in_preview;
use super::wire::{format_error, LOG_PREFIX};

/// プレビュー（の中身）を外へ動かそうとしたときの文言。
pub(super) const MOVE_OUT_OF_PREVIEW_REFUSED: &str = "プレビューは動かせません（保存されない表示用のアクタです）";

/// 既存のアクタをプレビューの中へ動かそうとしたときの文言。
pub(super) const MOVE_INTO_PREVIEW_REFUSED: &str = "プレビューの中へは移せません（プレビューは保存されません）";

/// プレビューの中へ新しく足そうとしたときの文言。
pub(super) const ADD_INTO_PREVIEW_REFUSED: &str = "プレビューの中には追加できません（プレビューは保存されません）";

/// 木の編集の命令を、プレビューを守るために拒否するか（純粋な判定）。拒否するなら利用者向けの理由。
///
/// # 引数
/// * `cmd`        - 届いた命令
/// * `in_preview` - DFS 番号のアクタがプレビューの部分木の中か（根を含む）
/// * `selection`  - いまのアクタの選択（DFS 番号。PASTE の挿入先の判定に使う）
pub(super) fn preview_edit_refusal(
    cmd: &IpcCommand,
    in_preview: impl Fn(u32) -> bool,
    selection: &[usize],
) -> Option<&'static str> {
    use IpcCommand as C;
    match cmd {
        // ── 既存の中身をプレビューへ／から動かす ──
        C::Reparent { child, new_parent, .. } => {
            move_refusal(in_preview(*child), new_parent.is_some_and(|parent| in_preview(parent)))
        }
        C::CreateGroupWithChildren { parent, children, .. } => move_refusal(
            children.iter().any(|&child| in_preview(child)),
            parent.is_some_and(|parent| in_preview(parent)),
        ),
        // 包む: 子がプレビューの根なら根が動き、中のノードなら包みがプレビューの中へ入る（どちらもプレビューを動かす）
        C::WrapActor { child_dfs, .. } => move_refusal(in_preview(*child_dfs), false),

        // ── プレビューの中へ足す ──
        C::CreateGroup { parent: Some(parent), .. }
        | C::AddActor { parent_dfs_id: Some(parent), .. }
        | C::AddActor2D { parent_dfs_id: Some(parent), .. }
        | C::AddTemplateActor { parent_dfs_id: Some(parent), .. }
        | C::AddActorChild { parent_dfs_id: parent }
        | C::AddActor2dChild { parent_dfs_id: parent }
        | C::CreateSpriteBoneActors { actor_dfs_id: parent, .. } => add_refusal(in_preview(*parent)),
        // 貼り付けは選択の直後（同じ親の下）へ入るので、選択のどれかが中なら中へ入る
        C::Paste => add_refusal(
            selection
                .iter()
                .any(|&dfs| u32::try_from(dfs).is_ok_and(|dfs| in_preview(dfs))),
        ),

        // ── 拒否しないもの（理由はファイル冒頭）: 値・名前・表示・削除・PREVIEW_PREFAB・DROP_ACTOR ほか ──
        _ => None,
    }
}

/// 動かす命令の拒否理由（中から外へを優先）。
fn move_refusal(moves_out_of_preview: bool, moves_into_preview: bool) -> Option<&'static str> {
    if moves_out_of_preview {
        Some(MOVE_OUT_OF_PREVIEW_REFUSED)
    } else if moves_into_preview {
        Some(MOVE_INTO_PREVIEW_REFUSED)
    } else {
        None
    }
}

/// 足す命令の拒否理由。
fn add_refusal(adds_into_preview: bool) -> Option<&'static str> {
    adds_into_preview.then_some(ADD_INTO_PREVIEW_REFUSED)
}

impl App {
    /// プレビューを守るために木の編集の命令を捨てる（ipc_handler.rs の命令ごとの最初、写しの閲覧専用の判定の直後に呼ぶ）。
    ///
    /// Edit のときだけ判定する。DFS は表示中の世界線（active_world_line）の木で数える。
    /// 捨てたら `PREVIEW_ERROR:{理由}` を返す。
    ///
    /// # 戻り値
    /// 捨てたら true（呼び出し側はその命令を処理しない）。
    pub(crate) fn refuse_preview_structure_edit(&self, cmd: &IpcCommand) -> bool {
        if self.mode != RuntimeMode::Edit {
            return false;
        }
        let Some(scene) = self.scene.as_ref() else { return false };
        let wl = self.active_world_line;
        let in_preview = |dfs: u32| is_dfs_in_preview(&scene.actors, wl, dfs);
        let Some(reason) = preview_edit_refusal(cmd, in_preview, &self.selected_actor_dfs_ids) else {
            return false;
        };
        eprintln!("{LOG_PREFIX} {reason}");
        if let Some(ipc) = &self.ipc {
            ipc.send(&format_error(reason));
        }
        true
    }
}

// ============================================================
//  テスト — 拒否の判定表
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用: DFS 10〜19 がプレビューの部分木の中（10 が根）という木。
    fn in_preview(dfs: u32) -> bool {
        (10..20).contains(&dfs)
    }

    /// 判定のテスト用の短縮（選択なし）。
    fn refusal(cmd: &IpcCommand) -> Option<&'static str> {
        preview_edit_refusal(cmd, in_preview, &[])
    }

    /// 親子付け替え: 中から外へ・外から中へで文言を出し分け、両方なら前者。どちらでもなければ通す。
    #[test]
    fn reparent_into_or_out_of_preview_is_refused() {
        let reparent = |child: u32, new_parent: Option<u32>| IpcCommand::Reparent {
            child,
            new_parent,
            anchor_sibling: None,
            place_before: false,
        };
        assert_eq!(refusal(&reparent(11, Some(3))), Some(MOVE_OUT_OF_PREVIEW_REFUSED), "中から外へ");
        assert_eq!(refusal(&reparent(10, None)), Some(MOVE_OUT_OF_PREVIEW_REFUSED), "根をトップレベルへ");
        assert_eq!(refusal(&reparent(3, Some(12))), Some(MOVE_INTO_PREVIEW_REFUSED), "外から中へ");
        assert_eq!(refusal(&reparent(11, Some(12))), Some(MOVE_OUT_OF_PREVIEW_REFUSED), "両方なら前者");
        assert_eq!(refusal(&reparent(3, Some(4))), None, "プレビューに関わらない付け替えは通す");
        assert_eq!(refusal(&reparent(3, None)), None);
    }

    /// 子をまとめてグループにする・包む: 中の子を動かすなら前者、外の子を中のグループへ入れるなら後者。
    #[test]
    fn grouping_and_wrapping_preview_content_is_refused() {
        let group = |parent: Option<u32>, children: Vec<u32>| IpcCommand::CreateGroupWithChildren {
            name: "Group".to_string(),
            parent,
            children,
        };
        assert_eq!(refusal(&group(None, vec![2, 15])), Some(MOVE_OUT_OF_PREVIEW_REFUSED));
        assert_eq!(refusal(&group(Some(12), vec![2, 3])), Some(MOVE_INTO_PREVIEW_REFUSED));
        assert_eq!(refusal(&group(Some(12), vec![15])), Some(MOVE_OUT_OF_PREVIEW_REFUSED), "両方なら前者");
        assert_eq!(refusal(&group(Some(1), vec![2, 3])), None);

        assert_eq!(
            refusal(&IpcCommand::WrapActor { child_dfs: 10, is_2d: true }),
            Some(MOVE_OUT_OF_PREVIEW_REFUSED)
        );
        assert_eq!(refusal(&IpcCommand::WrapActor { child_dfs: 2, is_2d: false }), None);
    }

    /// プレビューの中へ足す命令は拒否し、親なし（トップレベル）や外の親なら通す。
    #[test]
    fn adding_into_preview_is_refused() {
        let added_into = [
            IpcCommand::CreateGroup { name: "G".to_string(), parent: Some(12) },
            IpcCommand::AddActor { world_line: 0, parent_dfs_id: Some(12) },
            IpcCommand::AddActor2D { world_line: 0, parent_dfs_id: Some(10) },
            IpcCommand::AddActorChild { parent_dfs_id: 19 },
            IpcCommand::AddActor2dChild { parent_dfs_id: 11 },
            IpcCommand::AddTemplateActor { world_line: 0, parent_dfs_id: Some(13), path: "a.actor".to_string() },
            IpcCommand::CreateSpriteBoneActors { actor_dfs_id: 14, slot_idx: 0 },
        ];
        for cmd in &added_into {
            assert_eq!(refusal(cmd), Some(ADD_INTO_PREVIEW_REFUSED));
        }

        let allowed = [
            IpcCommand::CreateGroup { name: "G".to_string(), parent: None },
            IpcCommand::AddActor { world_line: 0, parent_dfs_id: None },
            IpcCommand::AddActor2D { world_line: 0, parent_dfs_id: Some(9) },
            IpcCommand::AddActorChild { parent_dfs_id: 20 },
            IpcCommand::AddTemplateActor { world_line: 0, parent_dfs_id: None, path: "a.actor".to_string() },
        ];
        for cmd in &allowed {
            assert_eq!(refusal(cmd), None);
        }
    }

    /// 貼り付けは選択のどれかがプレビューの中なら拒否する（選択なし・外だけなら通す）。
    #[test]
    fn paste_next_to_preview_content_is_refused() {
        assert_eq!(preview_edit_refusal(&IpcCommand::Paste, in_preview, &[2, 15]), Some(ADD_INTO_PREVIEW_REFUSED));
        assert_eq!(preview_edit_refusal(&IpcCommand::Paste, in_preview, &[2, 3]), None);
        assert_eq!(preview_edit_refusal(&IpcCommand::Paste, in_preview, &[]), None);
    }

    /// 値・名前・表示・削除・入れ子のプレビュー・ドロップは拒否しない。
    #[test]
    fn edits_that_stay_in_memory_are_not_refused() {
        let passes = [
            IpcCommand::RenameActor { dfs_id: 12, name: "x".to_string() },
            IpcCommand::SetActorVisible { dfs_id: 12, visible: false },
            IpcCommand::SetActorActive { dfs_id: 12, active: false },
            IpcCommand::Delete(vec![12]),
            IpcCommand::DeleteRecursive(vec![10]),
            IpcCommand::RemoveActor(10),
            IpcCommand::PreviewClear { world_line: 0, dfs: 12 },
            IpcCommand::DropActor { path: "a.actor".to_string(), screen_x: 1, screen_y: 2 },
            IpcCommand::Copy,
        ];
        for cmd in &passes {
            assert_eq!(preview_edit_refusal(cmd, in_preview, &[12]), None);
        }
        let nested = IpcCommand::PreviewPrefab {
            world_line: 0,
            parent_dfs: 12,
            request: crate::engine::core::app_base::ipc::PreviewPrefabRequest {
                prefab: "assets://dialog.actor".to_string(),
                under: String::new(),
                frame: None,
                frame_body: String::new(),
                layer_bias: 0,
            },
        };
        assert_eq!(refusal(&nested), None, "プレビューの中にプレビューを入れ子にするのは可");
    }
}
