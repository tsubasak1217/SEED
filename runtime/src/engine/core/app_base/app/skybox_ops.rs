// ============================================================
//  skybox_ops.rs — SkyboxComponent のインスペクタ編集
//
//  ・handle_set_skybox_field: インスペクタ（C#）からの SET_SKYBOX_FIELD IPC を
//    受けて SkyboxComponent のフィールドを更新する（handle_set_light_field と同流儀）。
//
//  ※ シーンからの描画対象収集・GPU リソース管理・描画は renderer/skybox.rs の
//    SkyboxSystem が担う（particle_system と同じ構成。ここではデータ編集のみ）。
// ============================================================

use crate::engine::components::{ComponentKind, SkyboxComponent, SkyboxMode};

use super::App;

impl App {
    /// インスペクタからの SkyboxComponent フィールド更新（SET_SKYBOX_FIELD IPC）。
    ///
    /// key: texture_path / mode / intensity / tint。
    /// tint は "r,g,b"（リニア）形式。不正な key・value は無視する。
    pub(super) fn handle_set_skybox_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        key:          &str,
        value:        &str,
    ) {
        use super::find_actor_by_dfs;

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（handle_set_light_field と同流儀）。
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Skybox)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        let Some(sb) = scene.world.get_mut::<SkyboxComponent>(entity) else { return };

        // key ごとに値を解釈して反映する（パース失敗は無視）。
        match key {
            "texture_path" => {
                // assets:// 仮想パス（または空文字＝未設定）。前後空白は保持しない。
                sb.texture_path = value.trim().to_string();
            }
            "mode" => {
                if let Some(m) = SkyboxMode::from_str_opt(value) { sb.mode = m; }
            }
            "intensity" => if let Ok(v) = value.parse::<f32>() { sb.intensity = v.max(0.0); },
            "tint" => {
                // "r,g,b"（リニア）をパースする。
                let parts: Vec<&str> = value.split(',').collect();
                if parts.len() == 3 {
                    if let (Ok(r), Ok(g), Ok(b)) = (
                        parts[0].parse::<f32>(),
                        parts[1].parse::<f32>(),
                        parts[2].parse::<f32>(),
                    ) {
                        sb.tint = [r.max(0.0), g.max(0.0), b.max(0.0)];
                    }
                }
            }
            _ => return,
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }
}
