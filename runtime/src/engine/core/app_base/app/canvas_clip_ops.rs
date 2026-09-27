// ============================================================
//  canvas_clip_ops.rs — CanvasClipComponent（子を切り抜く。W2-1a）のインスペクタ更新
//
//  ・handle_set_canvas_clip_field: インスペクタ（C#）からの SET_CANVAS_CLIP_FIELD IPC を受けて
//    CanvasClipComponent のフィールドを更新する（interaction_ops.rs と同じ流儀）。
//    Undo は field_edit.rs の共通機構（field_edit_target の分類）が記録するので、ここでは書かない。
//
//  値の解釈規則:
//    ・enabled … "true" / "false"（それ以外は無視）
// ============================================================

use crate::engine::components::{CanvasClipComponent, ComponentKind};

use super::App;

/// 真を表す値の文字列（インスペクタのチェックボックスが送る）。
const VALUE_TRUE: &str = "true";
/// 偽を表す値の文字列。
const VALUE_FALSE: &str = "false";

impl App {
    /// インスペクタからの CanvasClipComponent フィールド更新（SET_CANVAS_CLIP_FIELD IPC）。
    ///
    /// # 引数
    /// * `actor_dfs_id` - 対象アクターの DFS 番号
    /// * `slot_idx`     - 対象スロットの添字（CanvasClip 以外のスロットへの誤配は弾く）
    /// * `key`          - フィールド名（enabled）
    /// * `value`        - 値の文字列（不正な値は無視し、インスペクタへの再送信も行わない）
    pub(super) fn handle_set_canvas_clip_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        key: &str,
        value: &str,
    ) {
        use super::find_actor_by_dfs;

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（kind 違いのスロットへの誤配は弾く）
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::CanvasClip)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        let Some(clip) = scene.world.get_mut::<CanvasClipComponent>(entity) else { return };

        match key {
            "enabled" => match parse_bool(value) {
                Some(v) => clip.enabled = v,
                None => return,
            },
            // 切り抜きの形・角丸（W2-4。sprite_style_ipc.rs）
            _ => {
                if !super::sprite_style_ipc::apply_clip_shape_field(clip, key, value) {
                    return;
                }
            }
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }
}

/// "true" / "false" を bool にする【純関数】（前後の空白は許す。それ以外は None）。
fn parse_bool(value: &str) -> Option<bool> {
    match value.trim() {
        VALUE_TRUE => Some(true),
        VALUE_FALSE => Some(false),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::parse_bool;

    /// インスペクタのチェックボックスが送る値だけを読む。
    #[test]
    fn parse_bool_accepts_only_true_false() {
        assert_eq!(parse_bool("true"), Some(true));
        assert_eq!(parse_bool(" false "), Some(false));
        assert_eq!(parse_bool("1"), None);
        assert_eq!(parse_bool(""), None);
    }
}
