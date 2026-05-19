// ============================================================
//  camera_component_ops.rs — CameraComponent プロパティ設定操作
//
//  【含む処理】
//  - handle_set_camera_fov:         視野角（度）の更新
//  - handle_set_camera_near:        near clip の更新
//  - handle_set_camera_far:         far clip の更新
//  - handle_set_camera_main:        メインカメラフラグの更新
//  - handle_set_camera_clear_color: クリアカラー（RGBA）の更新
// ============================================================

use crate::engine::components::{ComponentKind, CameraComponent};

use super::{App, find_actor_by_dfs};

impl App {
    /// CameraComponent の FOV（視野角・度）を更新する。
    pub(super) fn handle_set_camera_fov(&mut self, actor_dfs_id: u32, slot_idx: u32, value: f32) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Camera)
                .map(|s| s.entity)
        };
        if let Some(entity) = slot_entity {
            let Some(scene) = &mut self.scene else { return };
            if let Some(cc) = scene.world.get_mut::<CameraComponent>(entity) {
                // 1° 未満や 179° 超は物理的に不正な値のためクランプする
                cc.fov_y_deg = value.clamp(1.0, 179.0);
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// CameraComponent の near clip を更新する。
    pub(super) fn handle_set_camera_near(&mut self, actor_dfs_id: u32, slot_idx: u32, value: f32) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Camera)
                .map(|s| s.entity)
        };
        if let Some(entity) = slot_entity {
            let Some(scene) = &mut self.scene else { return };
            if let Some(cc) = scene.world.get_mut::<CameraComponent>(entity) {
                // 0 以下は深度バッファの精度を完全に失うためクランプする
                cc.near = value.max(0.0001);
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// CameraComponent の far clip を更新する。
    pub(super) fn handle_set_camera_far(&mut self, actor_dfs_id: u32, slot_idx: u32, value: f32) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Camera)
                .map(|s| s.entity)
        };
        if let Some(entity) = slot_entity {
            let Some(scene) = &mut self.scene else { return };
            if let Some(cc) = scene.world.get_mut::<CameraComponent>(entity) {
                // near より大きい値を保証する（near はすでに 0.0001 以上）
                cc.far = value.max(cc.near + 0.1);
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// CameraComponent の is_main フラグを更新する。
    ///
    /// is_main = true に設定されたカメラが Play モードで使用されるゲームカメラとなる。
    /// 複数の Camera を is_main=true にした場合は DFS で最初に見つかったものが優先される。
    pub(super) fn handle_set_camera_main(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        is_main: bool,
    ) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Camera)
                .map(|s| s.entity)
        };
        if let Some(entity) = slot_entity {
            let Some(scene) = &mut self.scene else { return };
            if let Some(cc) = scene.world.get_mut::<CameraComponent>(entity) {
                cc.is_main = is_main;
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// CameraComponent のクリアカラーを更新する（RGBA 正規化値 0.0〜1.0）。
    pub(super) fn handle_set_camera_clear_color(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        r: f32, g: f32, b: f32, a: f32,
    ) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Camera)
                .map(|s| s.entity)
        };
        if let Some(entity) = slot_entity {
            let Some(scene) = &mut self.scene else { return };
            if let Some(cc) = scene.world.get_mut::<CameraComponent>(entity) {
                cc.clear_color = [r, g, b, a];
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }
}
