// ============================================================
//  water_link_ops.rs — WaterLinkComponent のインスペクタ更新（Phase W2.5）
//
//  ・handle_set_water_link_field: インスペクタ（C#）からの SET_WATER_LINK_FIELD IPC を
//    受けて WaterLinkComponent のフィールドを更新する
//    （water_ops.rs の SET_WATER_FIELD と同流儀）。
//
//  値の解釈規則:
//    ・volume_a / volume_b はアクタ名の文字列（前後空白のみ落とす。存在検証はしない）
//    ・openness は 0..1 へ clamp（バルブ。0 = 全閉）
//    ・幅・高さ・流量係数は負値を許さない（0 で下限）
//    ・opening_bottom（開口下端のアクタ相対 Y）は負値を許す
//
//  **専用ファイルにした理由**: water_ops.rs は WaterVolumeComponent 専用で、
//  対象コンポーネント型もクランプ規則も違う。1 ファイル 1 責務の方針に従い分離する。
// ============================================================

use crate::engine::components::water_link_component::WaterLinkComponent;
use crate::engine::components::ComponentKind;

use super::App;

// ─── クランプ境界の定数（マジックナンバー禁止）──────────────────

/// 開閉率の下限（0 = バルブ全閉）。
const OPENNESS_MIN: f32 = 0.0;
/// 開閉率の上限（1 = 全開）。
const OPENNESS_MAX: f32 = 1.0;
/// 寸法・係数など「負値に意味が無い」パラメータの下限。
const NON_NEGATIVE_MIN: f32 = 0.0;

impl App {
    /// インスペクタからの WaterLinkComponent フィールド更新（SET_WATER_LINK_FIELD IPC）。
    ///
    /// key: volume_a / volume_b / opening_bottom / opening_height /
    ///      opening_width / openness / flow_coefficient。
    /// 不正な key・value は無視する（インスペクタへの再送信も行わない）。
    pub(super) fn handle_set_water_link_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        key:          &str,
        value:        &str,
    ) {
        use super::find_actor_by_dfs;

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する。
        // kind が WaterLink でないスロットへの誤配は弾く。
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            let Some(actor) = find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                else { return };
            actor.slots().get(slot_idx as usize)
                .filter(|s| s.kind == ComponentKind::WaterLink)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };

        let Some(scene) = &mut self.scene else { return };
        let Some(l) = scene.world.get_mut::<WaterLinkComponent>(entity) else { return };

        // key ごとに値を解釈して反映する（パース失敗は無視）。
        match key {
            "volume_a" | "volume_b" => {
                // 接続先アクタ名。空文字列 = 未接続（そのリンクは水を通さない）。
                // **存在しない名前でもそのまま保存する**（保存 → アクタ生成の順で
                // 組み立てる作業を許すため。解決できない間はリンクが無視されるだけ）。
                // 前後の空白だけは落とす（D&D と手入力で差が出ないように）。
                let name = value.trim().to_string();
                if key == "volume_a" { l.volume_a = name; } else { l.volume_b = name; }
            }
            "opening_bottom" => {
                // 開口下端のアクタ相対 Y。床より下（負値）も正当なので clamp しない。
                if let Ok(v) = value.parse::<f32>() { l.opening_bottom = v; }
            }
            "opening_height" => {
                // 開口の高さ（m）。負の高さは断面積を反転させるので 0 で下限を切る。
                if let Ok(v) = value.parse::<f32>() { l.opening_height = v.max(NON_NEGATIVE_MIN); }
            }
            "opening_width" => {
                // 開口の幅（m）。同上。
                if let Ok(v) = value.parse::<f32>() { l.opening_width = v.max(NON_NEGATIVE_MIN); }
            }
            "openness" => {
                // 開閉率（バルブ）。0..1 の外は意味を持たないので clamp する。
                if let Ok(v) = value.parse::<f32>() {
                    l.openness = v.clamp(OPENNESS_MIN, OPENNESS_MAX);
                }
            }
            "flow_coefficient" => {
                // 流量係数（1/s）。負値は「逆流ポンプ」になってしまうので 0 で下限を切る。
                // 上限は設けない（大きくしても level_graph の釣り合いクランプが発振を防ぐ）。
                if let Ok(v) = value.parse::<f32>() { l.flow_coefficient = v.max(NON_NEGATIVE_MIN); }
            }
            _ => return,
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }
}
