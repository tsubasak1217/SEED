// ============================================================
//  physics2d_ops.rs — 2D 物理システムのライフサイクル管理
//
//  【責務】
//    start_physics_2d()  : Play 開始時（または編集時 2D 物理 ON 時）に 2D 物理スレッドを起動し、
//                          シーン内の全 Collider2d Actor2D を物理ワールドに登録する
//    stop_physics_2d()   : Play 停止時に 2D 物理スレッドを終了する
//    update_physics_2d() : 毎フレーム呼び出し。
//                          Kinematic CanvasTransform を物理スレッドに送信し、
//                          結果（動的 Rigidbody の Transform + 衝突イベント）を受信して適用する
//
//  【座標変換：anchor + pivot 補正】
//    CanvasTransform.position は「ピボット点のキャンバスローカル位置」であり、
//    実際のワールド（物理）位置は以下の 2 段補正が必要:
//
//    1. アンカー補正
//       - ルートアクター（親 Canvas なし）: 補正なし（ワールドスペース）
//       - 子アクター: anchor_off = parent_canvas_size × anchor × cumul_scale
//       → eff_pos = position (* cumul_scale if sm_transform) + anchor_off
//
//    2. ピボット補正（視覚的 bbox 基準）
//       - 視覚的バウンディングボックス（Sprite があればスプライトサイズ、なければコライダー AABB）を基準に
//         pivot から center へのオフセットを計算し body_pos に加算する。
//       - body_pos = eff_pos + R(rot) * (0.5 - pivot) * visual_bbox * scale
//       → body_pos_px = eff_pos + pivot_corr_world
//
//    body_pos_px が物理ボディの世界座標（ピクセル単位）。
//    PIXELS_PER_METER で除算してメートルに変換して物理スレッドへ渡す。
//
//  【書き戻し（Dynamic ボディ位置 → CanvasTransform）】
//    eff_pos_new = new_pos * PPM
//    ct.position = sm_transform
//                  ? (eff_pos_new - anchor_off) / cumul_scale
//                  : eff_pos_new - anchor_off
//
//  【編集時物理シミュレーション】
//    edit_physics_2d_with_rigidbody=false : 全ボディを kinematic + 重力なし で起動。
//                                           衝突検出のみ行い、CanvasTransform は更新しない。
//    edit_physics_2d_with_rigidbody=true  : 通常の Play 物理と同様（重力・ダイナミクスあり）。
// ============================================================

use crate::engine::components::{
    Collider2dComponent, ColliderShape2dData, CanvasTransform, CanvasComponent,
    SpriteComponent, ComponentKind,
};
use crate::engine::ecs::Entity;
use crate::engine::physics::{
    PhysicsThread2d, PhysicsCommand2d, PhysicsObject2d,
    CollisionPhase2d, TriggerPhase2d, PIXELS_PER_METER,
};
use crate::engine::core::app_base::scene::Scene;
use crate::engine::structs::objects::actor::{Actor, ActorKind};
use super::{App, RuntimeMode, InspectorTransformDrag};

// ─── Actor2d 物理コンテキスト ────────────────────────────────────────────────

/// アクター 1 つ分の「物理ワールド上のボディ位置」と「CanvasTransform 書き戻しに必要なデータ」を保持する。
///
/// collect_actor2d_contexts() で DFS 順に一括収集し、
/// start_physics_2d / update_physics_2d / frame_renderer のすべてのロジックで共有する。
pub(crate) struct Actor2dPhysicsCtx {
    /// DFS 1-indexed エンティティ ID（物理スレッドの entity_id と一致）
    pub(crate) dfs_id:           u64,
    /// ECS アクターエンティティ（CanvasTransform 書き戻し用）
    pub(crate) actor_entity:     Entity,
    /// ピクセル単位のボディ中心ワールド位置（anchor + pivot 補正済み）
    pub(crate) body_pos_px:      [f32; 2],
    /// 累積ワールド回転（ラジアン）= 全祖先の回転を合算した値。
    /// 物理スレッドへの rotation および書き戻し時の回転計算に使用する。
    pub(crate) rot_rad:          f32,
    /// アクタースケール（ローカル）
    pub(crate) scale:            [f32; 2],
    /// アンカーオフセット（親キャンバスローカル空間・ピクセル）。書き戻し時に eff_pos から減算する。
    pub(crate) anchor_off:       [f32; 2],
    /// ピボット補正ローカルベクトル（ピクセル）。書き戻し時に R(new_rot) を掛けて減算する。
    pub(crate) pivot_corr_local: [f32; 2],
    /// true なら親スケールで position をスケール済み（書き戻し時に除算が必要）
    pub(crate) sm_transform:     bool,
    /// 親累積スケール（sm_transform=true 時の書き戻し除算に使用）
    pub(crate) cumul_scale:      [f32; 2],
    /// Collider2d スロットエンティティ。None = Collider2d コンポーネントなし。
    pub(crate) collider_slot_entity: Option<Entity>,
    /// 親キャンバスの原点ワールド位置（ピクセル）。
    /// 書き戻し時にワールド座標から親ローカル座標へ変換するために使用する。
    pub(crate) parent_canvas_origin: [f32; 2],
    /// 親の累積ワールド回転（ラジアン）。
    /// 書き戻し時に累積回転から親回転を除いてローカル回転を求めるために使用する。
    pub(crate) parent_world_rot:     f32,
}

/// シーン内の全アクターを DFS 順に走査し、Actor2D の物理コンテキストを収集する。
///
/// - DFS カウンタは 3D アクターも含めて全アクターをカウントするため、
///   物理スレッドの entity_id（= DFS 1-indexed）と一致する。
/// - anchor / pivot 補正を適用した body_pos_px を計算する。
/// - 親キャンバスの Transform（位置・回転）変化に追従するため、
///   parent_canvas_origin（親キャンバス原点のワールド位置）と
///   parent_world_rot（累積ワールド回転）を DFS スタック経由で伝播する。
///   これにより Sprite と同じ親子ワールド変換合成が実現される。
///
/// frame_renderer.rs の 2D コライダーワイヤーフレーム描画でも共用するため pub(crate)。
pub(crate) fn collect_actor2d_contexts(
    scene:      &Scene,
    world_line: u32,
) -> Vec<Actor2dPhysicsCtx> {
    let mut result      = Vec::new();
    let mut dfs_counter = 0u64;

    // スタック要素:
    //   (アクター, 親 Canvas サイズ, 親累積スケール, (scale_transform, scale_size),
    //    親キャンバス原点ワールド位置, 親累積ワールド回転)
    //
    // parent_canvas_origin : 親キャンバスのローカル [0,0] に対応するワールド座標（ピクセル）。
    //                        子の eff_pos_local をワールド座標へ変換する基点になる。
    // parent_world_rot     : 全祖先の回転を合算した累積ワールド回転（ラジアン）。
    type CtxElem<'a> = (&'a Actor, Option<[f32; 2]>, [f32; 2], (bool, bool), [f32; 2], f32);

    let mut stack: Vec<CtxElem> = scene.actors.iter()
        .filter(|a| a.world_line == world_line)
        .rev()
        .map(|a| (a, None::<[f32; 2]>, [1.0f32, 1.0], (false, false), [0.0f32, 0.0], 0.0f32))
        .collect();

    while let Some((actor, parent_canvas_size, parent_cumul_scale, (sm_transform, sm_size), parent_canvas_origin, parent_world_rot)) = stack.pop() {
        dfs_counter += 1;
        let dfs_id = dfs_counter;

        // ── 自アクターの CanvasTransform を先取りする ──────────────────────────
        // 子へ渡す canvas 原点・累積回転の計算に使用する。
        let ct_opt = scene.world.get::<CanvasTransform>(actor.entity);

        // ── 子アクターへの親コンテキストを計算する ─────────────────────────────
        let my_canvas = actor.slots().iter()
            .find(|s| s.kind == ComponentKind::Canvas)
            .and_then(|s| scene.world.get::<CanvasComponent>(s.entity));

        // 子が参照する「有効 Canvas サイズ」（scale_size モード考慮済み）
        let child_canvas_size = my_canvas.map(|cc| [
            cc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 },
            cc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 },
        ]);
        let child_sm = my_canvas
            .map(|cc| (cc.scale_transform, cc.scale_size))
            .unwrap_or((false, false));

        // 子への累積スケール（自分の scale_transform/scale に依存）
        let child_cumul_scale = if let Some(ct) = ct_opt {
            if child_sm.0 {
                [parent_cumul_scale[0] * ct.scale[0],
                 parent_cumul_scale[1] * ct.scale[1]]
            } else {
                [ct.scale[0], ct.scale[1]]
            }
        } else {
            parent_cumul_scale
        };

        // ── 子への canvas 原点・累積回転を計算する ─────────────────────────────
        //
        // child_canvas_origin = 自アクターの canvas ローカル [0,0] がマップされるワールド位置。
        //
        // 計算式:
        //   1. eff_pos_local = anchor_off + position (sm_transform に応じてスケール)
        //      … 自アクターのピボット点の「親ローカル座標」
        //   2. actor_pivot_world = parent_canvas_origin + R(parent_world_rot) * eff_pos_local
        //      … 自アクターのピボット点の「ワールド座標」
        //   3. canvas 原点 = actor_pivot_world - R(actor_world_rot) * (ct.pivot * canvas_eff_size)
        //      … ピボットオフセットを逆適用して canvas [0,0] を求める
        //
        // CanvasTransform が存在しない場合は親の値をそのまま引き継ぐ。
        let (child_canvas_origin, child_world_rot) = if let Some(ct) = ct_opt {
            let anchor_off_for_child = match parent_canvas_size {
                None           => [0.0f32, 0.0],
                Some([pw, ph]) => [
                    pw * ct.anchor[0] * parent_cumul_scale[0],
                    ph * ct.anchor[1] * parent_cumul_scale[1],
                ],
            };
            let eff_pos_local = if sm_transform {
                [ct.position[0] * parent_cumul_scale[0] + anchor_off_for_child[0],
                 ct.position[1] * parent_cumul_scale[1] + anchor_off_for_child[1]]
            } else {
                [ct.position[0] + anchor_off_for_child[0],
                 ct.position[1] + anchor_off_for_child[1]]
            };

            // 親ローカル座標 → ワールド座標
            let (sin_p, cos_p) = parent_world_rot.sin_cos();
            let actor_pivot_world = [
                parent_canvas_origin[0] + cos_p * eff_pos_local[0] - sin_p * eff_pos_local[1],
                parent_canvas_origin[1] + sin_p * eff_pos_local[0] + cos_p * eff_pos_local[1],
            ];

            // このアクターの累積ワールド回転
            let actor_world_rot = parent_world_rot + ct.rotation.to_radians();

            // canvas 有効サイズ（pivot オフセット計算に使用）
            let (canvas_eff_w, canvas_eff_h) = my_canvas.map(|cc| (
                cc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 },
                cc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 },
            )).unwrap_or((1.0, 1.0));

            // ピボットオフセットを逆適用して canvas [0,0] = 子の座標系原点を求める
            let pvx = ct.pivot[0] * canvas_eff_w;
            let pvy = ct.pivot[1] * canvas_eff_h;
            let (sin_a, cos_a) = actor_world_rot.sin_cos();
            let canvas_origin = [
                actor_pivot_world[0] - (cos_a * pvx - sin_a * pvy),
                actor_pivot_world[1] - (sin_a * pvx + cos_a * pvy),
            ];

            (canvas_origin, actor_world_rot)
        } else {
            // CanvasTransform なし: 親の変換をそのまま引き継ぐ
            (parent_canvas_origin, parent_world_rot)
        };

        // 子をスタックに積む（DFS 順を保つため逆順）
        for child in actor.children.iter().rev() {
            stack.push((child, child_canvas_size, child_cumul_scale, child_sm, child_canvas_origin, child_world_rot));
        }

        // ── Actor2D のみ処理 ─────────────────────────────────────────────────
        if actor.actor_kind != ActorKind::Actor2D { continue; }

        let Some(ct) = ct_opt else { continue };

        // Collider2d スロットエンティティを探す（なければコンテキストは記録しない）
        // ただし DFS カウントは全アクターで行うため、スキップはしない
        let collider_slot_entity = actor.slots().iter()
            .find(|s| s.kind == ComponentKind::Collider2d)
            .map(|s| s.entity);

        // ── 1. アンカー補正 ──────────────────────────────────────────────────
        // 親キャンバスのローカル座標における anchor オフセット。
        // 子への伝播計算と同一の式を使用する（変数は Actor2D スコープ用に再束縛）。
        let anchor_off = match parent_canvas_size {
            None       => [0.0f32, 0.0],
            Some([pw, ph]) => [
                pw * ct.anchor[0] * parent_cumul_scale[0],
                ph * ct.anchor[1] * parent_cumul_scale[1],
            ],
        };

        // sm_transform=true の場合、position に親の cumul_scale を乗算する
        let eff_pos_local = if sm_transform {
            [ct.position[0] * parent_cumul_scale[0] + anchor_off[0],
             ct.position[1] * parent_cumul_scale[1] + anchor_off[1]]
        } else {
            [ct.position[0] + anchor_off[0],
             ct.position[1] + anchor_off[1]]
        };

        // 親ローカル座標 → ワールド座標へ変換してピボット点のワールド位置を求める。
        // これにより親キャンバスの位置・回転変化に追従する。
        let (sin_p, cos_p) = parent_world_rot.sin_cos();
        let actor_pivot_world = [
            parent_canvas_origin[0] + cos_p * eff_pos_local[0] - sin_p * eff_pos_local[1],
            parent_canvas_origin[1] + sin_p * eff_pos_local[0] + cos_p * eff_pos_local[1],
        ];

        // 累積ワールド回転（全祖先の回転 + 自分のローカル回転）
        let actor_world_rot = parent_world_rot + ct.rotation.to_radians();

        // ── 2. ピボット補正（視覚的バウンディングボックス基準）─────────────────
        //
        // CanvasTransform.pivot はオブジェクトの「視覚的」 bounding box 内の基準点位置。
        // 物理ボディは常に視覚的 bounding box の中心（center）に置く:
        //   body_pos = actor_pivot_world + R(actor_world_rot) * (0.5 - pivot) * visual_bbox * scale
        //
        // visual_bbox の優先順位:
        //   1. SpriteComponent.width / .height（スプライトが存在する場合）
        //   2. Collider2dComponent の形状 AABB（スプライトなし・コライダーのみ）
        //   3. [0, 0]（コンポーネントなし）
        let visual_bbox: [f32; 2] = {
            // Sprite コンポーネントがあればスプライトサイズを視覚的 bbox として使用する
            let sprite_bbox = actor.slots().iter()
                .find(|s| s.kind == ComponentKind::Sprite)
                .and_then(|s| scene.world.get::<SpriteComponent>(s.entity))
                .map(|sp| [sp.width, sp.height]);

            if let Some(bbox) = sprite_bbox {
                bbox
            } else if let Some(slot_entity) = collider_slot_entity {
                // スプライトなし: コライダー形状の AABB をフォールバックとして使用する
                if let Some(collider) = scene.world.get::<Collider2dComponent>(slot_entity) {
                    match &collider.shape {
                        ColliderShape2dData::Box { half_extents } =>
                            [2.0 * half_extents[0], 2.0 * half_extents[1]],
                        ColliderShape2dData::Circle { radius } =>
                            [2.0 * radius, 2.0 * radius],
                        ColliderShape2dData::Capsule { radius, half_height } =>
                            [2.0 * radius, 2.0 * (radius + half_height)],
                        ColliderShape2dData::ConvexHull { vertices } => {
                            if vertices.is_empty() {
                                [0.0, 0.0]
                            } else {
                                let (min_x, max_x) = vertices.iter().fold(
                                    (f32::INFINITY, f32::NEG_INFINITY),
                                    |(mn, mx), v| (mn.min(v[0]), mx.max(v[0])),
                                );
                                let (min_y, max_y) = vertices.iter().fold(
                                    (f32::INFINITY, f32::NEG_INFINITY),
                                    |(mn, mx), v| (mn.min(v[1]), mx.max(v[1])),
                                );
                                [max_x - min_x, max_y - min_y]
                            }
                        }
                    }
                } else {
                    [0.0, 0.0]
                }
            } else {
                [0.0, 0.0]
            }
        };

        let pivot_corr_local: [f32; 2] = [
            (0.5 - ct.pivot[0]) * visual_bbox[0] * ct.scale[0],
            (0.5 - ct.pivot[1]) * visual_bbox[1] * ct.scale[1],
        ];

        // ピボット補正を累積ワールド回転で回転させてワールド空間に変換する
        let (sin_a, cos_a) = actor_world_rot.sin_cos();
        let pivot_corr_world = [
            cos_a * pivot_corr_local[0] - sin_a * pivot_corr_local[1],
            sin_a * pivot_corr_local[0] + cos_a * pivot_corr_local[1],
        ];
        let body_pos_px = [
            actor_pivot_world[0] + pivot_corr_world[0],
            actor_pivot_world[1] + pivot_corr_world[1],
        ];

        result.push(Actor2dPhysicsCtx {
            dfs_id,
            actor_entity: actor.entity,
            body_pos_px,
            rot_rad:      actor_world_rot,
            scale:        ct.scale,
            anchor_off,
            pivot_corr_local,
            sm_transform,
            cumul_scale:          parent_cumul_scale,
            collider_slot_entity,
            parent_canvas_origin,
            parent_world_rot,
        });
    }

    result
}

// ─── App メソッド ─────────────────────────────────────────────────────────────

impl App {
    // ─── 起動 ────────────────────────────────────────────────────

    /// 2D 物理スレッドを起動し、シーン内の全 Collider2d Actor2D を物理ワールドに登録する。
    ///
    /// Play モード: 通常の 2D 物理シミュレーション（重力・ダイナミクスあり）。
    /// Edit モード (edit_physics_2d_with_rigidbody=false):
    ///   全ボディを kinematic にして重力を無効化し、衝突検出のみ行う。
    /// Edit モード (edit_physics_2d_with_rigidbody=true):
    ///   通常と同様（重力・ダイナミクスあり）。
    pub(super) fn start_physics_2d(&mut self) {
        let Some(scene) = &self.scene else { return };

        // 編集時コライダーのみモード: 全ボディを kinematic 扱い
        let force_kinematic = self.mode == RuntimeMode::Edit
            && !self.edit_physics_2d_with_rigidbody;

        // actor2d コンテキストを収集し、Collider2d 付きのものを物理ワールドに登録する
        let contexts = collect_actor2d_contexts(scene, self.active_world_line);

        let thread = PhysicsThread2d::spawn();

        for ctx in &contexts {
            let Some(slot_entity) = ctx.collider_slot_entity else { continue };
            let Some(collider) = scene.world.get::<Collider2dComponent>(slot_entity) else { continue };

            let position = [
                ctx.body_pos_px[0] / PIXELS_PER_METER,
                ctx.body_pos_px[1] / PIXELS_PER_METER,
            ];
            let collider_offset = [
                collider.offset[0] / PIXELS_PER_METER,
                collider.offset[1] / PIXELS_PER_METER,
            ];
            let shape = collider.shape.to_physics_shape();

            let rigidbody = if collider.use_rigidbody {
                let mut rb = collider.to_rigidbody_state();
                if force_kinematic {
                    rb.is_kinematic = true;
                }
                Some(rb)
            } else {
                None
            };

            thread.send(PhysicsCommand2d::AddObject(PhysicsObject2d {
                entity_id:     ctx.dfs_id,
                position,
                rotation:      ctx.rot_rad,
                scale:         ctx.scale,
                collider:      shape,
                collider_offset,
                rigidbody,
                is_trigger:    collider.is_trigger,
                physics_layer: collider.physics_layer,
                layer_mask:    collider.layer_mask,
            }));
        }

        eprintln!(
            "[Physics2D] start_physics_2d: {} objects registered (world_line={}, force_kinematic={})",
            contexts.iter().filter(|c| c.collider_slot_entity.is_some()).count(),
            self.active_world_line,
            force_kinematic,
        );

        // コライダーのみモードでは重力を無効化する
        if force_kinematic {
            thread.send(PhysicsCommand2d::SetGravity { gravity: [0.0, 0.0] });
        }

        self.physics_thread_2d = Some(thread);
    }

    // ─── 停止 ────────────────────────────────────────────────────

    /// Play モード停止時に 2D 物理スレッドを終了する（Drop で Stop 送信・join）。
    pub(super) fn stop_physics_2d(&mut self) {
        self.physics_thread_2d = None;
    }

    // ─── 毎フレーム更新 ──────────────────────────────────────────

    /// 2D 物理スレッドから最新結果を受信し、ECS と衝突状態を同期する。
    ///
    /// 1. Actor2d コンテキストを一括収集（anchor + pivot 補正済みの body_pos_px を持つ）
    /// 2. 物理スレッドの最新結果を受信して CanvasTransform を書き戻す
    ///    （Edit モードのコライダーのみ設定時は CanvasTransform 更新をスキップ）
    /// 3. 衝突イベントを IPC 経由でエディタへ通知する
    /// 4. 衝突中エンティティ DFS ID セットを更新する（コライダー色変更用）
    /// 5. Kinematic Actor2D の body_pos を物理スレッドへ送信する
    pub(super) fn update_physics_2d(&mut self) {
        // 物理スレッドが未起動の場合はスキップする。
        // Play モードの初回起動は handle_redraw_requested() のフレーム末尾で行う。
        // （フレーム先頭で起動すると初回 GPU 処理の間に物理が先行してしまうため）
        let Some(_) = &self.physics_thread_2d else { return };

        // Edit コライダーのみモードでは CanvasTransform 更新をスキップする
        let should_apply_transforms = self.mode != RuntimeMode::Edit
            || self.edit_physics_2d_with_rigidbody;

        // ── Actor2d コンテキストを一括収集（read-only borrow で完結させる）────
        let contexts: Vec<Actor2dPhysicsCtx> = if let Some(scene) = &self.scene {
            collect_actor2d_contexts(scene, self.active_world_line)
        } else {
            return;
        };

        // ── ギズモドラッグ中アクター（2D）の kinematic 切り替え ──────────────
        // 2D アクターはビューポートの 2D ギズモドラッグ対象になる。
        let new_drag_entity_id: Option<u64> = if self.drag.gizmo_drag.is_some() {
            self.actor_virtual_selected_idx.map(|dfs_id| dfs_id as u64 + 1)
        } else {
            match &self.inspector_transform_drag {
                Some(InspectorTransformDrag::CanvasActor { dfs_id, .. }) => Some(*dfs_id as u64 + 1),
                _ => None,
            }
        };

        // ドラッグ状態変化時にボディタイプを切り替える
        if new_drag_entity_id != self.dragging_physics_2d_entity_id {
            let thread = self.physics_thread_2d.as_ref().unwrap();
            if let Some(old_id) = self.dragging_physics_2d_entity_id {
                // ドラッグ終了: 現在のボディ位置・回転を final_position として渡し Dynamic に戻す
                let final_position = contexts.iter()
                    .find(|c| c.dfs_id == old_id)
                    .map(|c| (
                        [c.body_pos_px[0] / PIXELS_PER_METER, c.body_pos_px[1] / PIXELS_PER_METER],
                        c.rot_rad,
                    ));
                thread.send(PhysicsCommand2d::SetBodyKinematic {
                    entity_id:      old_id,
                    is_kinematic:   false,
                    final_position,
                });
            }
            if let Some(new_id) = new_drag_entity_id {
                // ドラッグ開始: Kinematic に変更して重力・衝突力を一時停止する
                let ecs_start_pos = contexts.iter()
                    .find(|c| c.dfs_id == new_id)
                    .map(|c| (
                        [c.body_pos_px[0] / PIXELS_PER_METER, c.body_pos_px[1] / PIXELS_PER_METER],
                        c.rot_rad,
                    ));
                thread.send(PhysicsCommand2d::SetBodyKinematic {
                    entity_id:    new_id,
                    is_kinematic: true,
                    final_position: ecs_start_pos,
                });
            }
            self.dragging_physics_2d_entity_id = new_drag_entity_id;
        }

        let thread = self.physics_thread_2d.as_ref().unwrap();
        let result = thread.recv_latest();

        if let Some(ref result) = result {
            // ① Dynamic Rigidbody2D の CanvasTransform を ECS に書き戻す
            if should_apply_transforms {
                if let Some(scene) = &mut self.scene {
                    // 初回の transform_updates を一度だけログに出して確認する
                    static FIRST_RESULT_LOGGED: std::sync::atomic::AtomicBool =
                        std::sync::atomic::AtomicBool::new(false);
                    if !result.transform_updates.is_empty()
                        && !FIRST_RESULT_LOGGED.swap(true, std::sync::atomic::Ordering::Relaxed)
                    {
                        for (eid, pos, rot) in &result.transform_updates {
                            eprintln!(
                                "[PHYS2D 初回結果] entity={} pos_m=[{:.4},{:.4}] rot={:.3}",
                                eid, pos[0], pos[1], rot,
                            );
                        }
                    }

                    for (entity_id, new_pos, new_rot) in &result.transform_updates {
                        if Some(*entity_id) == self.dragging_physics_2d_entity_id { continue; }

                        // entity_id に対応するコンテキストを探す
                        if let Some(ctx) = contexts.iter().find(|c| c.dfs_id == *entity_id) {
                            write_back_canvas_transform(scene, ctx, *new_pos, *new_rot);
                        }
                    }
                }
            }

            // ② 衝突イベントを IPC 経由でエディタへ通知する
            if self.scripting_host.is_some() {
                for event in &result.collision_events {
                    if let Some(ipc) = &self.ipc {
                        let phase_str = match event.phase {
                            CollisionPhase2d::Enter => "Enter",
                            CollisionPhase2d::Stay  => "Stay",
                            CollisionPhase2d::Exit  => "Exit",
                        };
                        ipc.send(&format!("COLLISION_2D_EVENT:{},{},{phase_str}",
                            event.entity_a, event.entity_b));
                    }
                }
                for event in &result.trigger_events {
                    if let Some(ipc) = &self.ipc {
                        let phase_str = match event.phase {
                            TriggerPhase2d::Enter => "Enter",
                            TriggerPhase2d::Exit  => "Exit",
                        };
                        ipc.send(&format!("TRIGGER_2D_EVENT:{},{},{phase_str}",
                            event.trigger_entity, event.other_entity));
                    }
                }
            }

            // ③ 衝突中エンティティ DFS ID セットを更新する
            let mut frame_colliding: std::collections::HashSet<u64> =
                std::collections::HashSet::new();
            for event in &result.collision_events {
                match event.phase {
                    CollisionPhase2d::Enter | CollisionPhase2d::Stay => {
                        frame_colliding.insert(event.entity_a);
                        frame_colliding.insert(event.entity_b);
                    }
                    CollisionPhase2d::Exit => {}
                }
            }
            for &eid in &result.active_trigger_entity_ids {
                frame_colliding.insert(eid);
            }
            self.active_collision_2d_dfs_ids = frame_colliding;
        }

        // ④ Kinematic Actor2D の body_pos を物理スレッドへ送信する
        let Some(scene) = &self.scene else { return };
        let thread = self.physics_thread_2d.as_ref().unwrap();

        for ctx in &contexts {
            let Some(slot_entity) = ctx.collider_slot_entity else { continue };
            let Some(collider) = scene.world.get::<Collider2dComponent>(slot_entity) else { continue };
            // Kinematic ボディのみ更新する（動的ボディはスレッドが位置を管理する）
            if !collider.use_rigidbody || !collider.is_kinematic { continue; }

            thread.send(PhysicsCommand2d::UpdateKinematic {
                entity_id: ctx.dfs_id,
                position:  [ctx.body_pos_px[0] / PIXELS_PER_METER, ctx.body_pos_px[1] / PIXELS_PER_METER],
                rotation:  ctx.rot_rad,
            });
        }

        // ⑤ ドラッグ中 Actor2D の現在位置を kinematic 更新として送信する
        if let Some(drag_entity_id) = self.dragging_physics_2d_entity_id {
            if let Some(ctx) = contexts.iter().find(|c| c.dfs_id == drag_entity_id) {
                thread.send(PhysicsCommand2d::UpdateKinematic {
                    entity_id: ctx.dfs_id,
                    position:  [ctx.body_pos_px[0] / PIXELS_PER_METER, ctx.body_pos_px[1] / PIXELS_PER_METER],
                    rotation:  ctx.rot_rad,
                });
            }
        }
    }
}

// ─── 書き戻しユーティリティ ──────────────────────────────────────────────────

/// 物理スレッドから受け取った新しい位置・回転を CanvasTransform に書き戻す。
///
/// body_pos（ワールド座標）から ct.position（親キャンバスローカル座標）への逆変換を行う。
///
/// # 逆変換の計算（新方式: 親ワールド変換を考慮）
///   1. pivot 補正除去:
///      actor_pivot_world = new_body_pos_px - R(new_rot) * pivot_corr_local
///   2. 親ワールド変換の逆適用（ワールド → 親ローカル）:
///      eff_pos_local = R(-parent_world_rot) * (actor_pivot_world - parent_canvas_origin)
///   3. アンカーオフセット除去 + sm_transform 逆スケール:
///      ct.position = sm_transform
///                    ? (eff_pos_local - anchor_off) / cumul_scale
///                    : eff_pos_local - anchor_off
///   4. ローカル回転:
///      ct.rotation = new_rot - parent_world_rot  （累積回転からローカル回転を取り出す）
fn write_back_canvas_transform(
    scene:   &mut Scene,
    ctx:     &Actor2dPhysicsCtx,
    new_pos: [f32; 2],
    new_rot: f32,
) {
    // ① ピボット補正を累積ワールド回転で除去してピボット点のワールド座標を求める
    let (sin_new, cos_new) = new_rot.sin_cos();
    let pc = ctx.pivot_corr_local;
    let pivot_corr_world_new = [
        cos_new * pc[0] - sin_new * pc[1],
        sin_new * pc[0] + cos_new * pc[1],
    ];
    let actor_pivot_world = [
        new_pos[0] * PIXELS_PER_METER - pivot_corr_world_new[0],
        new_pos[1] * PIXELS_PER_METER - pivot_corr_world_new[1],
    ];

    // ② 親ワールド変換の逆適用: ワールド座標 → 親キャンバスローカル座標
    //    R(-parent_world_rot) * (actor_pivot_world - parent_canvas_origin)
    let dx = actor_pivot_world[0] - ctx.parent_canvas_origin[0];
    let dy = actor_pivot_world[1] - ctx.parent_canvas_origin[1];
    let (sin_p, cos_p) = ctx.parent_world_rot.sin_cos();
    // R(-rot) = [[cos, sin], [-sin, cos]]
    let eff_pos_local = [
        cos_p * dx + sin_p * dy,
       -sin_p * dx + cos_p * dy,
    ];

    // ③ アンカーオフセット除去 + sm_transform 逆スケール
    let eff_pos_no_anchor = [
        eff_pos_local[0] - ctx.anchor_off[0],
        eff_pos_local[1] - ctx.anchor_off[1],
    ];
    let new_ct_pos = if ctx.sm_transform
        && ctx.cumul_scale[0].abs() > f32::EPSILON
        && ctx.cumul_scale[1].abs() > f32::EPSILON
    {
        [
            eff_pos_no_anchor[0] / ctx.cumul_scale[0],
            eff_pos_no_anchor[1] / ctx.cumul_scale[1],
        ]
    } else {
        eff_pos_no_anchor
    };

    // ④ ローカル回転 = 累積ワールド回転 - 親の累積回転
    let new_local_rot = new_rot - ctx.parent_world_rot;

    if let Some(ct) = scene.world.get_mut::<CanvasTransform>(ctx.actor_entity) {
        ct.position = new_ct_pos;
        ct.rotation = new_local_rot.to_degrees();
    }
}
