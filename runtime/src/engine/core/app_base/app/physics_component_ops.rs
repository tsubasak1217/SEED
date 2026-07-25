// ============================================================
//  physics_component_ops.rs — 物理コンポーネントプロパティ設定操作
//
//  【含む処理】
//  - handle_set_collider_data: ColliderComponent のデータ全体を JSON で更新
//
//  エディタのインスペクターから SET_COLLIDER_DATA IPC が届いたときに呼び出される。
//  ColliderComponent にはリジッドボディ設定（use_rigidbody・質量・摩擦など）も含む。
//
//  【物理スレッド再登録について】
//  ECS 更新後、物理スレッドが起動中であれば対象エンティティを RemoveObject + AddObject で
//  再登録する。これにより、"編集時物理シミュレーション"を再起動しなくても質量・摩擦・
//  反発係数などのパラメータが即座に反映される。
// ============================================================

use crate::engine::components::{
    ComponentKind, ColliderComponent, ColliderComponentData,
    Transform as ActorTransform,
};
use crate::engine::physics::PhysicsObject;
use crate::engine::structs::tensor::Vector3 as SeedVec3;
use crate::engine::structs::transforms::Quaternion as SeedQuat;

use super::{App, RuntimeMode, find_actor_by_dfs};

impl App {
    /// ColliderComponent のデータ全体（リジッドボディ設定を含む）を JSON からデシリアライズして更新する。
    ///
    /// ECS 更新後、物理スレッドが動いている場合はボディを再登録してパラメータを即時反映する。
    ///
    /// フォーマット: SET_COLLIDER_DATA:{actor_dfs_id},{slot_idx},{json}
    pub(super) fn handle_set_collider_data(&mut self, actor_dfs_id: u32, slot_idx: u32, json: &str) {
        // ── JSON パース ──────────────────────────────────────────────────────
        let data: ColliderComponentData = match serde_json::from_str(json) {
            Ok(d)  => d,
            Err(e) => {
                if let Some(ipc) = &self.ipc {
                    ipc.send(&format!("LOAD_ERROR:SET_COLLIDER_DATA parse error: {e}"));
                }
                return;
            }
        };

        let wl = self.active_world_line;

        // ── ECS 更新 ─────────────────────────────────────────────────────────
        // スロットエンティティを特定して ColliderComponent を差し替える
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Collider)
                .map(|s| s.entity)
        };

        if let Some(entity) = slot_entity {
            let Some(scene) = &mut self.scene else { return };
            if let Some(cc) = scene.world.get_mut::<ColliderComponent>(entity) {
                *cc = ColliderComponent::from(data);
            }
        }

        // ── 物理ボディ再登録 ─────────────────────────────────────────────────
        // 物理スレッドが起動中の場合、対象ボディを RemoveObject + AddObject で再構築する。
        // これにより質量・摩擦・反発係数などが物理スレッドへ即時反映される。
        if self.physics_thread.is_some() {
            // actor_dfs_id は find_actor_by_dfs と同じ 0-indexed。
            // 一方、collect_physics_objects の entity_id は 1-indexed（dfs_counter は 1 始まり）。
            // そのため +1 して 1-indexed entity_id に変換する。
            let entity_id = actor_dfs_id as u64 + 1;
            // force_kinematic: Edit コライダーのみモードでは全ボディを kinematic にする
            let force_kinematic = self.mode == RuntimeMode::Edit && !self.edit_physics_with_rigidbody;

            // scene を先に借用して PhysicsObject を構築し、次に physics_thread へ送信する
            // （Rust の借用規則で self.scene と self.physics_thread を同時に借用できないため）
            let phys_obj: Option<PhysicsObject> = self.scene.as_ref().and_then(|scene| {
                let mut c = 0u32;
                let actor = find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)?;
                let tf       = scene.world.get::<ActorTransform>(actor.entity)?;
                let cslot    = actor.slots().iter().find(|s| s.kind == ComponentKind::Collider)?;
                let collider = scene.world.get::<ColliderComponent>(cslot.entity)?;

                let position = [tf.position[0], tf.position[1], tf.position[2]];
                let scale    = [tf.scale[0],    tf.scale[1],    tf.scale[2]];

                // YXZ オイラー角（度）→ クォータニオン [x, y, z, w] に変換
                let euler_rad = SeedVec3::new(
                    tf.rotation[0].to_radians(),
                    tf.rotation[1].to_radians(),
                    tf.rotation[2].to_radians(),
                );
                let q = SeedQuat::from_euler(euler_rad);
                let rotation = [q.x, q.y, q.z, q.w];

                let rigidbody = if collider.use_rigidbody {
                    let mut rb = collider.to_rigidbody_state();
                    // コライダーのみモードでは動的ボディも kinematic として扱う
                    if force_kinematic { rb.is_kinematic = true; }
                    Some(rb)
                } else {
                    None
                };

                Some(PhysicsObject {
                    entity_id,
                    position,
                    rotation,
                    scale,
                    collider:        collider.shape.to_physics_shape(),
                    collider_offset: collider.offset,
                    rigidbody,
                    is_trigger:      collider.is_trigger,
                    physics_layer:   collider.physics_layer,
                    layer_mask:      collider.layer_mask,
                    is_character_controller: collider.is_character_controller,
                })
            });

            // 旧ボディ削除 → 新パラメータで再登録（物理スレッドとキャラクター衝突ミラーの
            // 両方へ集約ヘルパで反映する）。
            self.physics_remove_object(entity_id);
            if let Some(obj) = phys_obj {
                self.physics_add_object(obj);
            }
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }
}
