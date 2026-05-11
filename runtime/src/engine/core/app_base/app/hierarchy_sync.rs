// ============================================================
//  hierarchy_sync.rs — ヒエラルキー・アクターデータ送信
//
//  do_send_hierarchy / send_hierarchy / send_actor_data /
//  send_world_line_info / sync_anim_seeds / send_selected
// ============================================================

use crate::engine::components::{
    ModelComponent, Transform as ActorTransform, CanvasTransform, ComponentData,
};
use crate::engine::structs::tensor::Mat4x4;
use crate::engine::structs::transforms::Quaternion;

use super::{App, find_actor_by_dfs, collect_actor_nodes, build_hierarchy_json};

impl App {
    /// ヒエラルキーを JSON にシリアライズしてエディタへ送信する（実装本体）。
    pub(super) fn do_send_hierarchy(&self) {
        let Some(ipc)   = &self.ipc   else { return };
        let Some(scene) = &self.scene else { return };

        let wl = self.active_world_line;
        let roots: Vec<_> = scene.actors.iter()
            .filter(|a| a.world_line == wl)
            .collect();

        let mut nodes: Vec<(u32, String, Option<u32>)> = Vec::new();
        let mut counter = 0u32;
        for root in &roots {
            collect_actor_nodes(root, None, &mut counter, &mut nodes);
        }

        let json = build_hierarchy_json(&nodes);
        ipc.send(&format!("HIERARCHY:{json}"));
    }

    /// ヒエラルキー送信（スロットリング付き）。
    /// 100ms 以内に連続呼び出しされた場合はフラグを立てて遅延送信する。
    pub(super) fn send_hierarchy(&mut self) {
        let now = std::time::Instant::now();
        if let Some(last) = self.last_hierarchy_send {
            if now.duration_since(last).as_millis() < 100 {
                self.hierarchy_dirty = true;
                return;
            }
        }
        self.last_hierarchy_send = Some(now);
        self.hierarchy_dirty = false;
        self.do_send_hierarchy();
    }

    /// アクターデータを JSON でエディタへ送信する。
    pub(super) fn send_actor_data(&self, idx: u32) {
        let Some(ipc)   = &self.ipc   else { return };
        let Some(scene) = &self.scene else { return };

        // 仮想アクターノード（ModelComponent なし）のケース
        if self.active_world_line != 0 && idx >= 999_000_000 {
            let actor_idx = (idx - 999_000_000) as usize;
            let wl = self.active_world_line;
            if let Some(actor) = scene.actors.iter().filter(|a| a.world_line == wl).nth(actor_idx) {
                let name = serde_json::to_string(&actor.name).unwrap_or_default();
                let json = format!(
                    r#"{{"id":{idx},"name":{name},"transform":{{"px":0.0,"py":0.0,"pz":0.0,"ex":0.0,"ey":0.0,"ez":0.0,"sx":1.0,"sy":1.0,"sz":1.0}},"model_path":null}}"#
                );
                ipc.send(&format!("ACTOR_DATA:{json}"));
            }
            return;
        }

        let Some(mc) = scene.find_component_in_world_line::<ModelComponent>(self.active_world_line) else { return };

        let i = idx as usize;
        if i >= mc.instance_mats.len() { return; }

        let mat  = mc.instance_mats[i];
        let meta = &mc.instance_meta[i];

        // 位置: 第 4 列
        let (px, py, pz) = (mat[0][3], mat[1][3], mat[2][3]);

        // スケール: 各列ベクトルの長さ
        let scale_x = (mat[0][0]*mat[0][0] + mat[1][0]*mat[1][0] + mat[2][0]*mat[2][0]).sqrt();
        let scale_y = (mat[0][1]*mat[0][1] + mat[1][1]*mat[1][1] + mat[2][1]*mat[2][1]).sqrt();
        let scale_z = (mat[0][2]*mat[0][2] + mat[1][2]*mat[1][2] + mat[2][2]*mat[2][2]).sqrt();

        // 正規化された純回転行列を構築し Shepperd 法でクォータニオン → YXZ オイラー角
        let inv_x = if scale_x > 1e-10 { 1.0 / scale_x } else { 0.0 };
        let inv_y = if scale_y > 1e-10 { 1.0 / scale_y } else { 0.0 };
        let inv_z = if scale_z > 1e-10 { 1.0 / scale_z } else { 0.0 };
        let rot_mat = Mat4x4::new(
            mat[0][0]*inv_x, mat[0][1]*inv_y, mat[0][2]*inv_z, 0.0,
            mat[1][0]*inv_x, mat[1][1]*inv_y, mat[1][2]*inv_z, 0.0,
            mat[2][0]*inv_x, mat[2][1]*inv_y, mat[2][2]*inv_z, 0.0,
            0.0, 0.0, 0.0, 1.0,
        );
        let euler  = Quaternion::from_matrix(&rot_mat).to_euler();
        const DEG: f32 = 180.0 / std::f32::consts::PI;
        let (ex, ey, ez) = (euler.x * DEG, euler.y * DEG, euler.z * DEG);

        let name       = serde_json::to_string(&meta.name).unwrap_or_default();
        let model_path = serde_json::to_string(&mc.source_path).unwrap_or_default();

        let json = format!(
            r#"{{"id":{idx},"name":{name},"transform":{{"px":{px:.4},"py":{py:.4},"pz":{pz:.4},"ex":{ex:.4},"ey":{ey:.4},"ez":{ez:.4},"sx":{scale_x:.4},"sy":{scale_y:.4},"sz":{scale_z:.4}}},"model_path":{model_path}}}"#
        );
        ipc.send(&format!("ACTOR_DATA:{json}"));
    }

    /// 現在の世界線情報をエディタへ送信する。
    pub(super) fn send_world_line_info(&self) {
        let Some(ipc) = &self.ipc else { return };
        let wl = self.active_world_line;
        let actor_name = self.scene.as_ref()
            .and_then(|s| s.actors.iter().find(|a| a.world_line == wl))
            .map(|a| a.name.clone())
            .unwrap_or_else(|| if wl == 0 { "Scene".to_string() } else { "<none>".to_string() });
        let inst_count = self.scene.as_ref()
            .and_then(|s| s.find_component_in_world_line::<ModelComponent>(wl))
            .map(|mc| mc.instance_mats.len())
            .unwrap_or(0);
        ipc.send(&format!("WORLD_LINE_INFO:WL:{wl} | Actor:{actor_name} | Instances:{inst_count}"));
    }

    /// InstanceMeta::anim_seed を instanced_batch に同期する。
    /// 構造変更（追加・削除・Undo/Redo）後に呼び出す。
    pub(super) fn sync_anim_seeds(&mut self) {
        let wl = self.active_world_line;
        if let Some(scene) = &mut self.scene {
            if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(wl) {
                let seeds: Vec<u32> = mc.instance_meta.iter().map(|m| m.anim_seed).collect();
                mc.set_batch_anim_seeds(&seeds);
            }
        }
    }

    /// 現在の選択インスタンスをエディタへ通知する。
    pub(super) fn send_selected(&self) {
        let Some(ipc) = &self.ipc else { return };
        // マルチ選択: selected_actor_dfs_ids が 2 件以上の場合は SELECTED_MULTI で全仮想 ID を送る
        if self.selected_actor_dfs_ids.len() > 1 {
            let ids = self.selected_actor_dfs_ids.iter()
                .map(|&dfs| (999_000_000u64 + dfs as u64).to_string())
                .collect::<Vec<_>>().join(",");
            ipc.send(&format!("SELECTED_MULTI:{ids}"));
            return;
        }
        // 単一選択: プライマリ actor_virtual_selected_idx で通知
        if let Some(actor_idx) = self.actor_virtual_selected_idx {
            ipc.send(&format!("SELECTED:{}", 999_000_000u64 + actor_idx as u64));
            return;
        }
        // 未選択
        ipc.send("SELECTED:-1");
    }
}
