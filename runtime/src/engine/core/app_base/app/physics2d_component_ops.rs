// ============================================================
//  physics2d_component_ops.rs — 2D 物理コンポーネントプロパティ設定操作
//
//  【含む処理】
//  - handle_set_collider2d_data: Collider2dComponent のデータ全体を JSON で更新
//
//  エディタのインスペクターから SET_COLLIDER2D_DATA IPC が届いたときに呼び出される。
//  Collider2dComponent にはリジッドボディ設定（use_rigidbody・質量・摩擦など）も含む。
//
//  【物理スレッド再登録について】
//  ECS 更新後、2D 物理スレッドが起動中であれば対象エンティティを RemoveObject + AddObject で
//  再登録する。これにより、"編集時物理シミュレーション"を再起動しなくても質量・摩擦・
//  反発係数などのパラメータが即座に反映される。
// ============================================================

use crate::engine::components::{
    ComponentKind, Collider2dComponent, Collider2dComponentData,
};
use crate::engine::physics::{PhysicsCommand2d, PhysicsObject2d, PIXELS_PER_METER};

use super::{App, RuntimeMode, find_actor_by_dfs};
use super::physics2d_ops::collect_actor2d_contexts;

impl App {
    /// Collider2dComponent のデータ全体（リジッドボディ設定を含む）を JSON からデシリアライズして更新する。
    ///
    /// ECS 更新後、2D 物理スレッドが動いている場合はボディを再登録してパラメータを即時反映する。
    ///
    /// フォーマット: SET_COLLIDER2D_DATA:{actor_dfs_id},{slot_idx},{json}
    pub(super) fn handle_set_collider2d_data(&mut self, actor_dfs_id: u32, slot_idx: u32, json: &str) {
        // ── JSON パース ──────────────────────────────────────────────────────
        let data: Collider2dComponentData = match serde_json::from_str(json) {
            Ok(d)  => d,
            Err(e) => {
                if let Some(ipc) = &self.ipc {
                    ipc.send(&format!("LOAD_ERROR:SET_COLLIDER2D_DATA parse error: {e}"));
                }
                return;
            }
        };

        let wl = self.active_world_line;

        // ── ECS 更新 ─────────────────────────────────────────────────────────
        // スロットエンティティを特定して Collider2dComponent を差し替える
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Collider2d)
                .map(|s| s.entity)
        };

        if let Some(entity) = slot_entity {
            let Some(scene) = &mut self.scene else { return };
            if let Some(cc) = scene.world.get_mut::<Collider2dComponent>(entity) {
                *cc = Collider2dComponent::from(data);
            }
        }

        // ── 物理ボディ再登録 ─────────────────────────────────────────────────
        // 2D 物理スレッドが起動中の場合、対象ボディを RemoveObject + AddObject で再構築する
        if self.physics_thread_2d.is_some() {
            // entity_id は 1-indexed（DFS カウンタは 1 始まり）
            let entity_id = actor_dfs_id as u64 + 1;
            // force_kinematic: Edit コライダーのみモードでは全ボディを kinematic にする
            let force_kinematic = self.mode == RuntimeMode::Edit && !self.edit_physics_2d_with_rigidbody;

            // PhysicsObject2d を構築する（self.scene と self.physics_thread_2d を同時借用しないよう分離）
            //
            // 【重要】ct.position を直接使ってはならない。
            // ct.position はピボット点の位置（anchor/pivot 補正前）であり、
            // 物理ボディには anchor + pivot 補正済みの body_pos_px を使う必要がある。
            // collect_actor2d_contexts でそれを取得する。
            let phys_obj: Option<PhysicsObject2d> = self.scene.as_ref().and_then(|scene| {
                // anchor + pivot 補正済みの body_pos_px を持つコンテキストを取得する
                let ctx = collect_actor2d_contexts(scene, wl)
                    .into_iter()
                    .find(|c| c.dfs_id == entity_id)?;

                let slot_entity = ctx.collider_slot_entity?;
                let collider    = scene.world.get::<Collider2dComponent>(slot_entity)?;

                // anchor + pivot 補正済みの body_pos_px をメートルに変換する
                let position = [
                    ctx.body_pos_px[0] / PIXELS_PER_METER,
                    ctx.body_pos_px[1] / PIXELS_PER_METER,
                ];
                // コライダーオフセットもピクセル → メートル変換
                let collider_offset = [
                    collider.offset[0] / PIXELS_PER_METER,
                    collider.offset[1] / PIXELS_PER_METER,
                ];

                let rigidbody = if collider.use_rigidbody {
                    let mut rb = collider.to_rigidbody_state();
                    // コライダーのみモードでは動的ボディも kinematic として扱う
                    if force_kinematic { rb.is_kinematic = true; }
                    Some(rb)
                } else {
                    None
                };

                Some(PhysicsObject2d {
                    entity_id,
                    position,
                    rotation:        ctx.rot_rad,
                    scale:           ctx.scale,
                    collider:        collider.shape.to_physics_shape(),
                    collider_offset,
                    rigidbody,
                    is_trigger:      collider.is_trigger,
                    physics_layer:   collider.physics_layer,
                    layer_mask:      collider.layer_mask,
                })
            });

            // 旧ボディ削除 → 新パラメータで再登録
            if let Some(thread) = &self.physics_thread_2d {
                thread.send(PhysicsCommand2d::RemoveObject { entity_id });
                if let Some(obj) = phys_obj {
                    thread.send(PhysicsCommand2d::AddObject(obj));
                }
            }
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }
}
