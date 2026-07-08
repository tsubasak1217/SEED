// ============================================================
//  physics_ops.rs — 物理システムのライフサイクル管理
//
//  【責務】
//    start_physics()  : Play 開始時（または編集時物理 ON 時）に物理スレッドを起動し、
//                       シーン内の全 Collider Actor を物理ワールドに登録する
//    stop_physics()   : Play 停止時に物理スレッドを終了する
//    update_physics() : 毎フレーム呼び出し。
//                       Kinematic Transform を物理スレッドに送信し、
//                       結果（動的 Rigidbody の Transform + 衝突イベント）を受信して適用する
//
//  【編集時物理シミュレーション】
//    edit_physics_with_rigidbody=false : 全ボディを kinematic + 重力なし で起動。
//                                        衝突検出のみ行い、Transform は更新しない。
//    edit_physics_with_rigidbody=true  : 通常の Play 物理と同様（重力・ダイナミクスあり）。
//
//  【フロー】
//    メインスレッド (Play/EditPhysics フレーム毎)
//      ① 物理結果受信 → Transform を ECS World に反映（Play or edit+rigidbody 時のみ）
//      ② 衝突イベント → C# スクリプトのコールバック呼び出し（ScriptingHost）
//      ③ 衝突中エンティティセット更新（コライダーワイヤー色変更用）
//      ④ Kinematic Actor の最新 Transform を物理スレッドへ送信
//
//  【クォータニオン規約】
//    SEED 内部・物理型ともに [x, y, z, w] 配列を使用する。
//    オイラー角（度）との変換には engine::structs::transforms::Quaternion を使う。
// ============================================================

use crate::engine::components::{
    ColliderComponent, Transform as ActorTransform, ComponentKind, ModelComponent,
};
use crate::engine::structs::objects::actor::Actor;
use crate::engine::structs::tensor::Vector3 as SeedVec3;
use crate::engine::structs::transforms::Quaternion as SeedQuat;
use crate::engine::physics::{
    PhysicsThread, PhysicsCommand, PhysicsObject,
    CollisionPhase, TriggerPhase,
};
use crate::engine::core::app_base::scene::Scene;
use super::{App, RuntimeMode, InspectorTransformDrag};

impl App {
    // ─── 起動 ────────────────────────────────────────────────────

    /// 物理スレッドを起動し、シーン内の全 Collider Actor を物理ワールドに登録する。
    ///
    /// Play モード: 通常の物理シミュレーション（重力・ダイナミクスあり）。
    /// Edit モード (edit_physics_with_rigidbody=false):
    ///   全ボディを kinematic にして重力を無効化し、衝突検出のみ行う。
    /// Edit モード (edit_physics_with_rigidbody=true):
    ///   通常と同様（重力・ダイナミクスあり）。
    pub(super) fn start_physics(&mut self) {
        let Some(scene) = &self.scene else { return };

        // 編集時コライダーのみモード: 全ボディを kinematic 扱い
        let force_kinematic = self.mode == RuntimeMode::Edit
            && !self.edit_physics_with_rigidbody;

        let thread = PhysicsThread::spawn();

        // シーン内の全 Actor を走査して ColliderComponent を持つものを収集・登録する
        let objects = collect_physics_objects(scene, self.active_world_line, force_kinematic);
        eprintln!("[Physics] start_physics: {} objects collected (world_line={}, force_kinematic={})",
            objects.len(), self.active_world_line, force_kinematic);
        for obj in &objects {
            eprintln!("[Physics]   entity_id={} pos=({:.3},{:.3},{:.3}) rigidbody={} kinematic={}",
                obj.entity_id, obj.position[0], obj.position[1], obj.position[2],
                obj.rigidbody.is_some(),
                obj.rigidbody.as_ref().map_or(false, |rb| rb.is_kinematic));
        }
        for obj in objects {
            thread.send(PhysicsCommand::AddObject(obj));
        }

        // コライダーのみモードでは重力を無効化する
        if force_kinematic {
            thread.send(PhysicsCommand::SetGravity { gravity: [0.0, 0.0, 0.0] });
        }

        self.physics_thread = Some(thread);
    }

    // ─── 停止 ────────────────────────────────────────────────────

    /// Play モード停止時に物理スレッドを終了する（Drop で Stop 送信・join）。
    pub(super) fn stop_physics(&mut self) {
        self.physics_thread = None;
    }

    // ─── 毎フレーム更新 ──────────────────────────────────────────

    /// 物理スレッドから最新結果を受信し、ECS と衝突状態を同期する。
    ///
    /// 1. 物理スレッドの最新結果を受信して Transform を書き戻す
    ///    （Edit モードのコライダーのみ設定時は Transform 更新をスキップ）
    /// 2. 衝突イベントを C# スクリプトへ通知する
    /// 3. 衝突中エンティティ DFS ID セットを更新する（コライダー色変更用）
    /// 4. Kinematic Actor の Transform を物理スレッドへ送信する
    pub(super) fn update_physics(&mut self) {
        // 物理スレッドが未起動の場合はスキップする。
        // Play モードの初回起動は handle_redraw_requested() のフレーム末尾で行う。
        // （フレーム先頭で起動すると初回 GPU 処理の間に物理が先行してしまうため）
        let Some(thread) = &self.physics_thread else { return };

        // 診断ログ（最初の 120 フレームのみ）
        static DIAG_FRAME: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
        let diag_n = DIAG_FRAME.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        let diag = diag_n < 120;

        // edit コライダーのみモードでは Transform 更新をスキップする
        let should_apply_transforms = self.mode != RuntimeMode::Edit
            || self.edit_physics_with_rigidbody;

        // ── ギズモドラッグ中アクターの kinematic 切り替え ──────────
        // ビューポートのギズモドラッグ中は self.drag.gizmo_drag が Some になり、
        // 選択中アクターの DFS ID は self.actor_virtual_selected_idx（0-indexed）に入っている。
        // 物理 entity_id は 1-indexed なので +1 して変換する。
        // 2D アクター・Canvas アクターは物理を持たないため対象外とする。
        let new_drag_entity_id: Option<u64> = if self.drag.gizmo_drag.is_some() {
            // ビューポートギズモドラッグ: 選択中アクターを kinematic 化
            self.actor_virtual_selected_idx.map(|dfs_id| dfs_id as u64 + 1)
        } else {
            // インスペクタ経由の Transform ドラッグ（BeginTransformDrag IPC）
            match &self.inspector_transform_drag {
                Some(InspectorTransformDrag::Actor     { dfs_id, .. }) |
                Some(InspectorTransformDrag::ActorGroup{ dfs_id, .. }) => Some(*dfs_id as u64 + 1),
                _ => None,
            }
        };

        if new_drag_entity_id != self.dragging_physics_entity_id {
            // ドラッグ状態が変化した場合にボディタイプを切り替える
            if let Some(old_id) = self.dragging_physics_entity_id {
                // ドラッグ終了: 現在のアクター座標を final_position として渡し Dynamic に戻す。
                let final_position = self.scene.as_ref().and_then(|s| {
                    get_actor_transform_by_entity_id(s, self.active_world_line, old_id)
                });
                thread.send(PhysicsCommand::SetBodyKinematic {
                    entity_id: old_id,
                    is_kinematic: false,
                    final_position,
                });
                // コライダーのみモードのドラッグ終了: 最終有効位置・前フレーム位置をクリアする
                self.drag_collider_last_valid_pos = None;
                self.drag_prev_at_pos = None;
            }
            if let Some(new_id) = new_drag_entity_id {
                // ドラッグ開始: Kinematic に変更して重力・衝突力を一時停止する。
                // 現在の ECS 位置を final_position として渡すことで、接触補正などで
                // Rapier 内部位置が ECS とわずかにズレている場合でも
                // 持ち上げ開始時の回転ジャークを防ぐ。
                let ecs_start_pos = self.scene.as_ref()
                    .and_then(|s| get_actor_transform_by_entity_id(s, self.active_world_line, new_id));
                thread.send(PhysicsCommand::SetBodyKinematic {
                    entity_id: new_id,
                    is_kinematic: true,
                    final_position: ecs_start_pos,
                });
                // コライダーのみモードのドラッグ開始: 現在位置を最終有効位置・前フレーム位置として初期化する
                if self.mode == RuntimeMode::Edit && !self.edit_physics_with_rigidbody {
                    let init_pos = self.scene.as_ref()
                        .and_then(|s| get_actor_transform_by_entity_id(s, self.active_world_line, new_id));
                    self.drag_collider_last_valid_pos = init_pos;
                    self.drag_prev_at_pos = init_pos;
                }
            }
            self.dragging_physics_entity_id = new_drag_entity_id;
        }

        let result = thread.recv_latest();

        if let Some(ref result) = result {
            if diag {
                eprintln!("[Physics] frame={} transform_updates={}", diag_n, result.transform_updates.len());
                for (id, pos, _) in &result.transform_updates {
                    eprintln!("[Physics]   entity_id={} new_pos=({:.3},{:.3},{:.3})", id, pos[0], pos[1], pos[2]);
                }
            }

            // ① Dynamic Rigidbody の Transform を ECS に書き戻す
            // ドラッグ中のアクターは gizmo が位置を制御するため物理結果を無視する
            if should_apply_transforms {
                if let Some(scene) = &mut self.scene {
                    for (entity_id, new_pos, new_rot) in &result.transform_updates {
                        if Some(*entity_id) == self.dragging_physics_entity_id { continue; }
                        apply_physics_transform(scene, self.active_world_line, *entity_id, *new_pos, *new_rot);
                    }
                }
            }

            // ① コライダーのみ編集モードでのドラッグ中押し戻し処理。
            // ドラッグしているアクターの衝突イベント（Enter/Stay）を検出し、
            // 衝突があれば最終有効位置に戻すことで壁に対する押し戻しを実現する。
            let is_collider_only_drag = self.mode == RuntimeMode::Edit
                && !self.edit_physics_with_rigidbody
                && self.dragging_physics_entity_id.is_some();
            if is_collider_only_drag {
                let drag_id = self.dragging_physics_entity_id.unwrap();
                // NarrowPhase 直接クエリ結果を優先して使用する。
                // CollisionEvent はキネマティック vs スタティックのペアで未発火の場合があるため、
                // active_contact_entity_ids による検出がより確実。
                let has_blocking_collision = result.active_contact_entity_ids.contains(&drag_id)
                    || result.collision_events.iter().any(|e| {
                        (e.entity_a == drag_id || e.entity_b == drag_id)
                            && e.phase != CollisionPhase::Exit
                    });

                if has_blocking_collision {
                    // ── 衝突中: 最終有効位置に押し戻す ──────────────────────────
                    if let Some((last_pos, last_rot)) = self.drag_collider_last_valid_pos {
                        // 押し戻し前の「壁侵入位置」を取得する（ドラッグ参照原点の調整量を計算するため）
                        let wall_pos: Option<[f32; 3]> = self.scene.as_ref()
                            .and_then(|s| get_actor_transform_by_entity_id(s, self.active_world_line, drag_id))
                            .map(|(p, _)| p);

                        // ECS に安全位置を書き戻す
                        if let Some(scene) = &mut self.scene {
                            apply_physics_transform(scene, self.active_world_line, drag_id, last_pos, last_rot);
                        }
                        // 物理スレッドにも戻した位置を通知する（次ステップで衝突を解消する）
                        if let Some(thread) = &self.physics_thread {
                            thread.send(PhysicsCommand::UpdateKinematic {
                                entity_id: drag_id,
                                position:  last_pos,
                                rotation:  last_rot,
                            });
                        }

                        // ドラッグ開始行列をシフトしてチラつきを防止する。
                        //
                        // 問題: カーソルが壁位置に留まる限り on_cursor_moved が毎フレーム
                        //       AT を壁侵入位置に戻してしまい、押し戻しと交互にチラつく。
                        //
                        // 解決: 壁侵入量 offset = wall_pos - safe_pos を各ドラッグ開始行列の
                        //       平行移動成分から引くことで、カーソル静止時の計算結果を
                        //       「new_pos = delta * new_start = delta * (old_start - offset) = safe_pos」
                        //       にする。これ以降カーソルを戻した方向へは自然に動く。
                        if let Some(wall_p) = wall_pos {
                            let off = [
                                wall_p[0] - last_pos[0],
                                wall_p[1] - last_pos[1],
                                wall_p[2] - last_pos[2],
                            ];
                            // MC ドラッグ開始行列群を調整する
                            for (_, sm) in &mut self.drag.drag_root_starts {
                                sm[0][3] -= off[0]; sm[1][3] -= off[1]; sm[2][3] -= off[2];
                            }
                            for (_, sm) in &mut self.drag.drag_child_starts {
                                sm[0][3] -= off[0]; sm[1][3] -= off[1]; sm[2][3] -= off[2];
                            }
                            for (_, mats) in &mut self.drag.actor_extra_mc_drag_starts {
                                for sm in mats.iter_mut() {
                                    sm[0][3] -= off[0]; sm[1][3] -= off[1]; sm[2][3] -= off[2];
                                }
                            }
                            for (_, sm) in &mut self.drag.actor_child_drag_starts {
                                sm[0][3] -= off[0]; sm[1][3] -= off[1]; sm[2][3] -= off[2];
                            }
                            // Transform-only ドラッグ（MC なし）の場合も調整する
                            if let Some((_, start_tf)) = &mut self.drag.actor_transform_drag_start {
                                start_tf.position[0] -= off[0];
                                start_tf.position[1] -= off[1];
                                start_tf.position[2] -= off[2];
                            }
                        }
                    }
                } else {
                    // ── 衝突なし: 前フレームで送信した位置を有効位置として記録する ────
                    // on_cursor_moved は update_physics より先に走り AT を最新ギズモ位置に
                    // 更新してしまうため、現フレームの AT をそのまま last_valid_pos に使うと
                    // 「まだ物理が確認していない位置」を安全位置として採用してしまいチラつく。
                    // drag_prev_at_pos = 前フレーム末に物理スレッドへ送った位置 = 今の物理結果が
                    // 参照した位置 であり、衝突なしと確認された正しい安全位置。
                    if let Some(prev) = self.drag_prev_at_pos {
                        self.drag_collider_last_valid_pos = Some(prev);
                    }
                }
            }

            // ②-0 Play モード時: 衝突・トリガーイベントをスクリプトのコールバックへ配信する
            //（OnCollisionEnter / OnTriggerEnter 等。Edit モードの物理プレビューでは呼ばない）
            if self.mode == RuntimeMode::Play
                && (!result.collision_events.is_empty() || !result.trigger_events.is_empty())
            {
                self.dispatch_physics_events_to_scripts(
                    &result.collision_events,
                    &result.trigger_events,
                );
            }

            // ② 衝突イベントを IPC 経由でエディタへ転送する（スクリプトホスト存在時のみ）
            if self.scripting_host.is_some() {
                for event in &result.collision_events {
                    if let Some(ipc) = &self.ipc {
                        let phase_str = match event.phase {
                            CollisionPhase::Enter => "Enter",
                            CollisionPhase::Stay  => "Stay",
                            CollisionPhase::Exit  => "Exit",
                        };
                        ipc.send(&format!("COLLISION_EVENT:{},{},{phase_str}", event.entity_a, event.entity_b));
                    }
                }
                for event in &result.trigger_events {
                    if let Some(ipc) = &self.ipc {
                        let phase_str = match event.phase {
                            TriggerPhase::Enter => "Enter",
                            TriggerPhase::Exit  => "Exit",
                        };
                        ipc.send(&format!("TRIGGER_EVENT:{},{},{phase_str}", event.trigger_entity, event.other_entity));
                    }
                }
            }

            // ③ 衝突中エンティティ DFS ID セットを更新する（コライダーワイヤー色変更用）
            //
            // 通常衝突: Stay イベントが毎フレーム届くので Enter | Stay を収集する。
            // トリガー衝突: Enter/Exit は遷移時のみ来るため、毎フレーム送られる
            //   active_trigger_entity_ids を使って「触れている間ずっと」色を維持する。
            let mut frame_colliding: std::collections::HashSet<u64> = std::collections::HashSet::new();
            for event in &result.collision_events {
                match event.phase {
                    CollisionPhase::Enter | CollisionPhase::Stay => {
                        frame_colliding.insert(event.entity_a);
                        frame_colliding.insert(event.entity_b);
                    }
                    CollisionPhase::Exit => {}
                }
            }
            // active_trigger_entity_ids は現在オーバーラップ中の全エンティティを毎フレーム持つ
            for &eid in &result.active_trigger_entity_ids {
                frame_colliding.insert(eid);
            }
            self.active_collision_dfs_ids = frame_colliding;
        }

        // ④ Kinematic Actor の Transform を物理スレッドへ送信する
        let Some(scene) = &self.scene else { return };
        let Some(thread) = &self.physics_thread else { return };

        collect_kinematic_updates(scene, self.active_world_line)
            .into_iter()
            .for_each(|(entity_id, pos, rot)| {
                thread.send(PhysicsCommand::UpdateKinematic { entity_id, position: pos, rotation: rot });
            });

        // ⑤ ドラッグ中アクターの現在位置を kinematic 更新として送信する
        // 一時的に Kinematic 化されているため UpdateKinematic で gizmo 位置を物理スレッドに反映する
        if let Some(drag_entity_id) = self.dragging_physics_entity_id {
            if let Some((pos, rot)) = get_actor_transform_by_entity_id(scene, self.active_world_line, drag_entity_id) {
                thread.send(PhysicsCommand::UpdateKinematic { entity_id: drag_entity_id, position: pos, rotation: rot });
                // コライダーのみモードのドラッグ: 今フレームで物理スレッドに送った位置を記録する。
                // 次フレームで「衝突なし」の物理結果を受けたとき last_valid_pos をこの値に更新する。
                // これにより on_cursor_moved が AT を新位置に更新した後でも
                // 「物理が確認した安全位置」だけを last_valid_pos に採用できる。
                if self.mode == RuntimeMode::Edit && !self.edit_physics_with_rigidbody {
                    self.drag_prev_at_pos = Some((pos, rot));
                }
            }
        }
    }
}

// ─── シーン走査ユーティリティ ────────────────────────────────────────────────

/// シーン内の全 Actor を DFS 順に走査して PhysicsObject リストを生成する。
///
/// entity_id は DFS カウンタ（1 始まり）で、apply_physics_transform と同一の順序を保つ。
///
/// `force_kinematic`: true の場合、use_rigidbody=true であっても全ボディを kinematic にする。
/// 編集時コライダーのみモードで使用する（衝突検出のみ・Transform 更新なし）。
fn collect_physics_objects(scene: &Scene, world_line: u32, force_kinematic: bool) -> Vec<PhysicsObject> {
    let mut objects      = Vec::new();
    let mut dfs_counter  = 0u32;

    // (アクター, 親までの実効アクティブ) をスタックで運ぶ（DFS 先行順を維持）
    let mut stack: Vec<(&Actor, bool)> = scene.actors.iter()
        .filter(|a| a.world_line == world_line)
        .rev()
        .map(|a| (a, true))
        .collect();

    while let Some((actor, parent_active)) = stack.pop() {
        dfs_counter += 1;
        let dfs_id = dfs_counter as u64;
        let active = parent_active && actor.active;

        for child in actor.children.iter().rev() {
            stack.push((child, active));
        }

        // 非アクティブアクターは物理シミュレーションへ登録しない
        // （DFS カウントは entity_id 整合のため上で進めている）
        if !active { continue; }

        let Some(tf) = scene.world.get::<ActorTransform>(actor.entity) else { continue };

        // Collider スロットを探す（enabled=false のスロットは対象外）
        let Some(collider_slot) = actor.slots().iter()
            .find(|s| s.kind == ComponentKind::Collider && s.enabled) else { continue };
        let Some(collider) = scene.world.get::<ColliderComponent>(collider_slot.entity) else { continue };

        let position = [tf.position[0], tf.position[1], tf.position[2]];
        let scale    = [tf.scale[0],    tf.scale[1],    tf.scale[2]];
        let rotation = euler_to_quat_arr(tf.rotation);
        let offset   = collider.offset;
        let shape    = collider.shape.to_physics_shape();

        let rigidbody = if collider.use_rigidbody {
            let mut rb = collider.to_rigidbody_state();
            // force_kinematic 時は動的ボディを kinematic に変換する
            if force_kinematic {
                rb.is_kinematic = true;
            }
            Some(rb)
        } else {
            None
        };

        objects.push(PhysicsObject {
            entity_id:       dfs_id,
            position,
            rotation,
            scale,
            collider:        shape,
            collider_offset: offset,
            rigidbody,
            is_trigger:      collider.is_trigger,
            physics_layer:   collider.physics_layer,
            layer_mask:      collider.layer_mask,
        });
    }
    objects
}

/// Kinematic オブジェクトの現在 Transform を DFS 順に収集する。
///
/// collect_physics_objects と同一の DFS 順を使うことで entity_id が対応する。
fn collect_kinematic_updates(
    scene:      &Scene,
    world_line: u32,
) -> Vec<(u64, [f32; 3], [f32; 4])> {
    let mut updates     = Vec::new();
    let mut dfs_counter = 0u32;

    let mut stack: Vec<&Actor> = scene.actors.iter()
        .filter(|a| a.world_line == world_line)
        .rev()
        .collect();

    while let Some(actor) = stack.pop() {
        dfs_counter += 1;
        let dfs_id = dfs_counter as u64;

        for child in actor.children.iter().rev() {
            stack.push(child);
        }

        let Some(collider_slot) = actor.slots().iter().find(|s| s.kind == ComponentKind::Collider) else { continue };
        let Some(collider) = scene.world.get::<ColliderComponent>(collider_slot.entity) else { continue };
        if !collider.use_rigidbody || !collider.is_kinematic { continue; }

        let Some(tf) = scene.world.get::<ActorTransform>(actor.entity) else { continue };
        let position = [tf.position[0], tf.position[1], tf.position[2]];
        let rotation = euler_to_quat_arr(tf.rotation);

        updates.push((dfs_id, position, rotation));
    }
    updates
}

/// 物理スレッドから受け取った Transform を対応 Actor の ECS に書き戻す。
///
/// entity_id は collect_physics_objects と同一の DFS 順カウンタで照合する。
/// ActorTransform を更新した後、レンダラーが参照する ModelComponent の行列も同期する。
fn apply_physics_transform(
    scene:      &mut Scene,
    world_line: u32,
    entity_id:  u64,
    new_pos:    [f32; 3],
    new_rot:    [f32; 4],
) {
    let mut dfs_counter = 0u32;

    // 安全な借用のため actor の情報を先にフラット化する
    let actor_info: Vec<_> = {
        let mut stack: Vec<&Actor> = scene.actors.iter()
            .filter(|a| a.world_line == world_line)
            .rev()
            .collect();
        let mut result = Vec::new();
        while let Some(actor) = stack.pop() {
            for child in actor.children.iter().rev() { stack.push(child); }
            let model_slot = actor.slots().iter()
                .find(|s| s.kind == ComponentKind::Model)
                .map(|s| s.entity);
            result.push((actor.entity, model_slot));
        }
        result
    };

    for (actor_entity, model_slot) in actor_info {
        dfs_counter += 1;
        if dfs_counter as u64 != entity_id { continue; }

        // ActorTransform を更新し TRS 行列を計算する（スケールは物理で変化しないので維持）
        let new_mat = if let Some(tf) = scene.world.get_mut::<ActorTransform>(actor_entity) {
            tf.position = new_pos;
            tf.rotation = quat_arr_to_euler_deg(new_rot);
            tf.to_mat4()
        } else {
            return;
        };

        // ModelComponent の行列を更新し GPU キャッシュを無効化する
        if let Some(slot_entity) = model_slot {
            if let Some(mc) = scene.world.get_mut::<ModelComponent>(slot_entity) {
                if let Some(m) = mc.instance_mats.first_mut() {
                    *m = new_mat;
                }
                if let Some(batch) = mc.instanced_batch.as_mut() {
                    batch.mark_dirty();
                }
            }
        }
        return;
    }
}

// ─── 数学ユーティリティ ──────────────────────────────────────────────────────

/// YXZ オイラー角（度）を SEED のクォータニオン [x, y, z, w] に変換する。
fn euler_to_quat_arr(euler_deg: [f32; 3]) -> [f32; 4] {
    let euler_rad = SeedVec3::new(
        euler_deg[0].to_radians(),
        euler_deg[1].to_radians(),
        euler_deg[2].to_radians(),
    );
    let q = SeedQuat::from_euler(euler_rad);
    [q.x, q.y, q.z, q.w]
}

/// SEED のクォータニオン [x, y, z, w] を YXZ オイラー角（度）に変換する。
fn quat_arr_to_euler_deg(q_arr: [f32; 4]) -> [f32; 3] {
    let q = SeedQuat::new(q_arr[0], q_arr[1], q_arr[2], q_arr[3]);
    let euler = q.to_euler();
    [euler.x.to_degrees(), euler.y.to_degrees(), euler.z.to_degrees()]
}

/// 物理 entity_id（1-indexed DFS）に対応するアクターの現在 Transform を取得する。
///
/// ドラッグ中アクターの gizmo 位置を物理スレッドへ送信するために使用する。
fn get_actor_transform_by_entity_id(
    scene:      &Scene,
    world_line: u32,
    entity_id:  u64,
) -> Option<([f32; 3], [f32; 4])> {
    let mut dfs_counter = 0u32;
    let mut stack: Vec<&crate::engine::structs::objects::actor::Actor> = scene.actors.iter()
        .filter(|a| a.world_line == world_line)
        .rev()
        .collect();
    while let Some(actor) = stack.pop() {
        dfs_counter += 1;
        for child in actor.children.iter().rev() { stack.push(child); }
        if dfs_counter as u64 != entity_id { continue; }
        if let Some(tf) = scene.world.get::<ActorTransform>(actor.entity) {
            return Some((
                [tf.position[0], tf.position[1], tf.position[2]],
                euler_to_quat_arr(tf.rotation),
            ));
        }
    }
    None
}
