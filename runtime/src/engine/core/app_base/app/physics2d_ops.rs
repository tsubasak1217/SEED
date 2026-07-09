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
//  【座標変換：canvas_collect.rs と同一の変換チェーン】
//    collect_actor2d_contexts は canvas_collect.rs (collect_sprite_items) と
//    完全に同じ座標変換チェーンを使って body_pos_px を計算する。
//    これにより「スプライト描画位置 = 当たり判定位置」が保証される。
//
//    変換チェーン:
//    1. ルートアクターのアンカー計算（ビューポート基準・共通ヘルパー root_anchor_offset）
//       design_space=false（Play・SS オーバーレイ）:
//         anchor_off = [vw*anchor[0] - vw/2, vh*anchor[1] - vh/2]
//         （ortho 原点 = 画面中央、anchor=0.5 → offset=0 で中央配置）
//       design_space=true（ビューポートタブの設計空間編集 = edit_view_is_2d）:
//         anchor_off = [vw*anchor[0], vh*anchor[1]]
//         （キャンバス左上をワールド原点に一致させる。「キャンバスを編集」モードと同じ）
//
//    2. auto_scale_factor（ルートキャンバスのみ）
//       = [vp_w / canvas_w, vp_h / canvas_h]  (auto_scale=true の場合)
//       この係数を child_cumul_scale に常に含める
//
//    3. 子の累積スケール（scale_transform フラグに応じて）
//       scale_transform=true : parent_cumul_scale * ct.scale * auto_scale_factor
//       scale_transform=false: ct.scale * auto_scale_factor
//
//    4. pivot 補正でボディ中心（矩形の中心）を計算
//       pivot_corr_canonical = (0.5 - ct.pivot) × ref_size
//         ref_size: CanvasComponent あり → Canvas サイズ（Sprite 非依存）
//                   CanvasComponent なし → コライダーバウンディングボックスサイズ
//       body_pos_px = actor_pivot_world + R(parent_rot) * R(local_rot) * S(ct.scale) * pivot_corr_canonical
//
//  【書き戻し（Dynamic ボディ位置 → CanvasTransform）】
//    actor_pivot_world = new_pos * PPM - R(new_rot) * pivot_corr_local
//    eff_pos_local = R(-parent_world_rot) * (actor_pivot_world - parent_canvas_origin)
//    ct.position = sm_transform
//                  ? (eff_pos_local - anchor_off) / parent_cumul_scale
//                  : eff_pos_local - anchor_off
//
//  【編集時物理シミュレーション】
//    edit_physics_2d_with_rigidbody=false : 全ボディを kinematic + 重力なし で起動。
//                                           衝突検出のみ行い、CanvasTransform は更新しない。
//    edit_physics_2d_with_rigidbody=true  : 通常の Play 物理と同様（重力・ダイナミクスあり）。
// ============================================================

use std::collections::HashMap;
use crate::engine::components::{
    Collider2dComponent, CanvasTransform, CanvasComponent,
    ComponentKind, AspectRatioAxis, GravityMode, Transform as ActorTransform,
};
use crate::engine::ecs::Entity;
use crate::engine::physics::{
    PhysicsThread2d, PhysicsCommand2d, PhysicsObject2d,
    CollisionPhase2d, TriggerPhase2d, PIXELS_PER_METER, DEFAULT_GRAVITY_2D,
};
use crate::engine::core::app_base::scene::Scene;
use crate::engine::structs::objects::actor::{Actor, ActorKind};
use super::{App, RuntimeMode, InspectorTransformDrag, find_actor_by_dfs};
use super::canvas_collect::{build_canvas_viewport_map, root_anchor_offset};

// ─── Actor2d 物理コンテキスト ────────────────────────────────────────────────

/// アクター 1 つ分の「物理ワールド上のボディ位置」と「CanvasTransform 書き戻しに必要なデータ」を保持する。
///
/// collect_actor2d_contexts() で DFS 順に一括収集し、
/// start_physics_2d / update_physics_2d / frame_renderer のすべてのロジックで共有する。
///
/// 【設計方針】
///   body_pos_px = CanvasTransform.position のワールド位置（ピボット点）。
///   Collider と Sprite は完全に独立しており、互いに影響しない。
///   コライダーの位置は body_pos_px + R(rot) * collider.offset で決まる。
pub(crate) struct Actor2dPhysicsCtx {
    /// DFS 1-indexed エンティティ ID（物理スレッドの entity_id と一致）
    pub(crate) dfs_id:           u64,
    /// ECS アクターエンティティ（CanvasTransform 書き戻し用）
    pub(crate) actor_entity:     Entity,
    /// 物理ボディ中心（= 矩形中心）のワールド位置（ortho 空間、ビューポート中心が原点）。
    /// pivot_world_px にピボット補正（(0.5 - pivot) × 基準サイズ）を加算した値。
    pub(crate) body_pos_px:      [f32; 2],
    /// CanvasTransform のピボット点（= アクター position）のワールド座標（ortho 空間）。
    /// ギズモ表示位置・ドラッグ書き戻しなど「アクター位置そのもの」が必要な処理に使用する。
    pub(crate) pivot_world_px:   [f32; 2],
    /// 累積ワールド回転（ラジアン）= 全祖先の回転を合算した値。
    pub(crate) rot_rad:          f32,
    /// アクタースケール（ローカル）
    pub(crate) scale:            [f32; 2],
    /// アンカーオフセット（ortho 空間）。書き戻し時に使用。
    pub(crate) anchor_off:       [f32; 2],
    /// true なら親スケールで position をスケール済み（書き戻し時に除算が必要）
    pub(crate) sm_transform:     bool,
    /// 親累積スケール（auto_scale_factor 込み）。書き戻し時の sm_transform 逆スケールに使用。
    pub(crate) cumul_scale:      [f32; 2],
    /// ピボット補正ローカルベクトル（スケール・回転前の正規化値）。
    /// = (0.5 - pivot) × ref_size × size_eff
    ///   ref_size: CanvasComponent あり → Canvas サイズ / なし → コライダー AABB サイズ
    /// 書き戻し時に body_pos_px → actor_pivot_world を求めるために使用する。
    pub(crate) pivot_corr_local: [f32; 2],
    /// コライダー形状・オフセットのスケール係数 X。
    /// sm_size=true: parent_cumul_scale[0]、それ以外 1.0
    pub(crate) size_sx:          f32,
    /// コライダー形状・オフセットのスケール係数 Y
    pub(crate) size_sy:          f32,
    /// Collider2d スロットエンティティ。None = Collider2d コンポーネントなし。
    pub(crate) collider_slot_entity: Option<Entity>,
    /// 親キャンバスの原点ワールド位置（ortho 空間ピクセル）。書き戻し用。
    pub(crate) parent_canvas_origin: [f32; 2],
    /// 親の累積ワールド回転（ラジアン）。書き戻し用。
    pub(crate) parent_world_rot:     f32,
}

/// シーン内の全アクターを DFS 順に走査し、Actor2D の物理コンテキストを収集する。
///
/// canvas_collect.rs (collect_sprite_items) と同一の座標変換チェーンを使用することで、
/// スプライト描画位置と当たり判定位置を完全に一致させる。
///
/// # 変換チェーンのポイント
/// - ルートアクターのアンカー: [vw * anchor - vw/2, vh * anchor - vh/2]（ビューポート中心基準）
/// - auto_scale_factor を child_cumul_scale に常に含める
/// - pivot_corr_local の基準サイズ:
///     CanvasComponent あり → Canvas サイズ × size_eff
///     CanvasComponent なし + Collider2d あり → コライダー AABB サイズ × size_eff
/// - body_pos_px = actor_pivot_world + pivot_corr_world（SS オフセット追加不要）
///
/// # 引数
/// - `viewport_size`: シーン SS モード時のウィンドウサイズ [w, h]（デフォルトビューポート）。
///   None = ワールドスペースまたはアクター編集タブ（スケールなし）。
/// - `canvas_viewport_overrides`: `CanvasViewportRef::Camera` を持つルートキャンバスアクターの
///   実効ビューポートサイズ上書きマップ（entity → [w, h]）。
///   空 HashMap を渡すとウィンドウサイズをそのまま使用する。
/// - `root_auto_sizes`: ビューポート・ルートキャンバスの自動解像度マップ
///   （build_root_canvas_auto_size_map）。登録済みルートは width/height をこの値へ置き換え、
///   CanvasTransform を恒等として扱う（canvas_collect.rs と同一の Phase B 規則）。
///
/// frame_renderer.rs の 2D コライダーワイヤーフレーム描画でも共用するため pub(crate)。
pub(crate) fn collect_actor2d_contexts(
    scene:         &Scene,
    world_line:    u32,
    viewport_size: Option<[f32; 2]>,
    canvas_viewport_overrides: &HashMap<Entity, [f32; 2]>,
    root_auto_sizes: &HashMap<Entity, [f32; 2]>,
    // ビューポートタブの設計空間表示中か（= edit_view_is_2d）。
    // true のときルートキャンバス左上をワールド原点に一致させる（描画と同一規則）。
    design_space:  bool,
) -> Vec<Actor2dPhysicsCtx> {
    let mut result      = Vec::new();
    let mut dfs_counter = 0u64;

    // スタック要素:
    //   (アクター, 親 Canvas サイズ, 親累積スケール, (scale_transform, scale_size),
    //    親キャンバス原点ワールド位置, 親累積ワールド回転)
    //   末尾に「親までの実効アクティブ」を追加（非アクティブは物理登録から除外する）
    type CtxElem<'a> = (&'a Actor, Option<[f32; 2]>, [f32; 2], (bool, bool, bool, bool), [f32; 2], f32, bool);

    let mut stack: Vec<CtxElem> = scene.actors.iter()
        .filter(|a| a.world_line == world_line)
        .rev()
        .map(|a| (a, None::<[f32; 2]>, [1.0f32, 1.0], (false, false, false, true), [0.0f32, 0.0], 0.0f32, true))
        .collect();

    while let Some((actor, parent_canvas_size, parent_cumul_scale, (sm_transform, sm_size, keep_aspect, is_width_axis), parent_canvas_origin, parent_world_rot, parent_active)) = stack.pop() {
        let active = parent_active && actor.active;
        dfs_counter += 1;
        let dfs_id = dfs_counter;

        // ── ビューポート・ルートキャンバスの自動解像度上書き（Phase B）───────────
        // Some のとき: 解像度を自動計算値へ置き換え、CanvasTransform を恒等として扱う
        // （canvas_collect.rs と同一の規則。保存データは書き換えない）。
        let root_auto = if parent_canvas_size.is_none() {
            root_auto_sizes.get(&actor.entity).copied()
        } else {
            None
        };

        // ── 自アクターの CanvasTransform を先取りする ──────────────────────────
        // ルートキャンバスの Transform 恒等化のため所有値に変換してから参照を取る
        let ct_owned: Option<CanvasTransform> = scene.world.get::<CanvasTransform>(actor.entity)
            .map(|ct| if root_auto.is_some() { CanvasTransform::default() } else { ct.clone() });
        let ct_opt = ct_owned.as_ref();

        // ── 自アクターの CanvasComponent を取得する ─────────────────────────────
        let my_canvas = actor.slots().iter()
            .find(|s| s.kind == ComponentKind::Canvas)
            .and_then(|s| scene.world.get::<CanvasComponent>(s.entity));

        // 自動解像度上書きを反映した基準キャンバスサイズ（なければ保存値）
        let my_canvas_base = my_canvas.map(|cc| root_auto.unwrap_or([cc.width, cc.height]));

        // sm_size による拡縮を反映した有効キャンバスサイズ（子 canvas 原点・auto_scale 計算用、アスペクト比考慮）
        let phys_sc_x = if sm_size { if keep_aspect && !is_width_axis { parent_cumul_scale[1] } else { parent_cumul_scale[0] } } else { 1.0 };
        let phys_sc_y = if sm_size { if keep_aspect && is_width_axis  { parent_cumul_scale[0] } else { parent_cumul_scale[1] } } else { 1.0 };
        let (my_eff_w, my_eff_h) = my_canvas_base.map(|[bw, bh]| (
            bw * phys_sc_x,
            bh * phys_sc_y,
        )).unwrap_or((1.0, 1.0));

        // 子が参照する「有効 Canvas サイズ」（scale_size・アスペクト比モード考慮済み）
        let child_canvas_size = my_canvas_base.map(|[bw, bh]| [
            bw * phys_sc_x,
            bh * phys_sc_y,
        ]);
        let child_sm = my_canvas
            .map(|cc| (
                cc.scale_transform, cc.scale_size,
                cc.keep_aspect_ratio, matches!(cc.aspect_ratio_axis, AspectRatioAxis::Width),
            ))
            .unwrap_or((false, false, false, true));

        // CanvasViewportRef::Camera を持つルートキャンバスのビューポートサイズを解決する。
        // ルートアクター（parent_canvas_size=None）のみオーバーライドマップを参照する。
        // canvas_collect.rs と同一のパターン。
        let eff_viewport = if parent_canvas_size.is_none() {
            canvas_viewport_overrides.get(&actor.entity).copied().or(viewport_size)
        } else {
            viewport_size
        };

        // auto_scale_factor: ルートキャンバス（parent_canvas_size=None）かつ auto_scale=true のとき
        // ビューポートサイズ / 基準キャンバスサイズ で計算する。
        // canvas_collect.rs と同一の計算。eff_viewport を使用してカメラ参照ビューポートに対応する。
        let auto_scale_factor = if parent_canvas_size.is_none() {
            if let (Some([vw, vh]), Some(true)) = (eff_viewport, my_canvas.map(|cc| cc.auto_scale)) {
                [vw / my_eff_w.max(f32::EPSILON), vh / my_eff_h.max(f32::EPSILON)]
            } else {
                [1.0f32, 1.0]
            }
        } else {
            [1.0f32, 1.0]
        };

        // 子への累積スケール（auto_scale_factor を常に含む）
        // canvas_collect.rs の child_cumul_scale と同一の計算:
        //   scale_transform=true : parent_cumul_scale * ct.scale * auto_scale_factor
        //   scale_transform=false: ct.scale * auto_scale_factor
        let child_cumul_scale = if let Some(ct) = ct_opt {
            if child_sm.0 {
                [parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
                 parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1]]
            } else {
                [ct.scale[0] * auto_scale_factor[0],
                 ct.scale[1] * auto_scale_factor[1]]
            }
        } else {
            parent_cumul_scale
        };

        // ── 子への canvas 原点・累積回転を計算する ─────────────────────────────
        // child_canvas_origin = 自アクターの canvas ローカル [0,0] がマップされるワールド位置。
        let (child_canvas_origin, child_world_rot) = if let Some(ct) = ct_opt {
            // アンカーオフセット（canvas_collect.rs と同一）:
            //   ルートレベル: [vw * anchor - vw/2, vh * anchor - vh/2]（ortho 中心基準）
            //   子レベル: parent_canvas_size * anchor * parent_cumul_scale
            // eff_viewport を使用: CanvasViewportRef::Camera 参照時はカメラの実効サイズを基準とする
            let anchor_off_child = match parent_canvas_size {
                None => {
                    if let Some([vw, vh]) = eff_viewport {
                        // ルートレベル: design_space に応じて原点位置を切り替える（共通ヘルパー）
                        root_anchor_offset(ct.anchor, vw, vh, design_space)
                    } else {
                        [0.0f32, 0.0]
                    }
                }
                Some([pw, ph]) => [
                    pw * ct.anchor[0] * parent_cumul_scale[0],
                    ph * ct.anchor[1] * parent_cumul_scale[1],
                ],
            };

            let eff_pos_local = if sm_transform {
                [ct.position[0] * parent_cumul_scale[0] + anchor_off_child[0],
                 ct.position[1] * parent_cumul_scale[1] + anchor_off_child[1]]
            } else {
                [ct.position[0] + anchor_off_child[0],
                 ct.position[1] + anchor_off_child[1]]
            };

            // 親ローカル座標 → ワールド座標
            let (sin_p, cos_p) = parent_world_rot.sin_cos();
            let actor_pivot_world = [
                parent_canvas_origin[0] + cos_p * eff_pos_local[0] - sin_p * eff_pos_local[1],
                parent_canvas_origin[1] + sin_p * eff_pos_local[0] + cos_p * eff_pos_local[1],
            ];

            // このアクターの累積ワールド回転
            let actor_world_rot = parent_world_rot + ct.rotation.to_radians();

            // canvas 有効サイズ（pivot オフセット計算に使用。自動解像度上書きを反映）
            let (canvas_eff_w, canvas_eff_h) = my_canvas_base.map(|[bw, bh]| (
                bw * if sm_size { parent_cumul_scale[0] } else { 1.0 },
                bh * if sm_size { parent_cumul_scale[1] } else { 1.0 },
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
            (parent_canvas_origin, parent_world_rot)
        };

        // 子をスタックに積む（DFS 順を保つため逆順）
        for child in actor.children.iter().rev() {
            stack.push((child, child_canvas_size, child_cumul_scale, child_sm, child_canvas_origin, child_world_rot, active));
        }

        // ── Actor2D のみ処理 ─────────────────────────────────────────────────
        if actor.actor_kind != ActorKind::Actor2D { continue; }

        let Some(ct) = ct_opt else { continue };

        // Collider2d スロットエンティティを探す。
        // 非アクティブアクター・enabled=false のスロットは物理登録の対象外にする
        // （レイアウトコンテキスト自体はドラッグ書き戻し等で使うため収集は継続する）。
        let collider_slot_entity = if active {
            actor.slots().iter()
                .find(|s| s.kind == ComponentKind::Collider2d && s.enabled)
                .map(|s| s.entity)
        } else {
            None
        };

        // ── 1. アンカー補正（canvas_collect.rs と同一） ───────────────────────
        // eff_viewport: CanvasViewportRef::Camera 参照時はカメラの実効サイズを基準とする
        let anchor_off = match parent_canvas_size {
            None => {
                if let Some([vw, vh]) = eff_viewport {
                    [vw * ct.anchor[0] - vw / 2.0, vh * ct.anchor[1] - vh / 2.0]
                } else {
                    [0.0f32, 0.0]
                }
            }
            Some([pw, ph]) => [
                pw * ct.anchor[0] * parent_cumul_scale[0],
                ph * ct.anchor[1] * parent_cumul_scale[1],
            ],
        };

        let eff_pos_local = if sm_transform {
            [ct.position[0] * parent_cumul_scale[0] + anchor_off[0],
             ct.position[1] * parent_cumul_scale[1] + anchor_off[1]]
        } else {
            [ct.position[0] + anchor_off[0],
             ct.position[1] + anchor_off[1]]
        };

        // 親ローカル座標 → ワールド座標（ortho 空間）
        let (sin_p, cos_p) = parent_world_rot.sin_cos();
        let actor_pivot_world = [
            parent_canvas_origin[0] + cos_p * eff_pos_local[0] - sin_p * eff_pos_local[1],
            parent_canvas_origin[1] + sin_p * eff_pos_local[0] + cos_p * eff_pos_local[1],
        ];

        // 累積ワールド回転
        let actor_world_rot = parent_world_rot + ct.rotation.to_radians();

        // size_eff: sm_size=true のとき parent_cumul_scale（auto_scale 込み）、それ以外 1.0。
        // Collider2d の keep_aspect_ratio を考慮してアスペクト比維持スケールを適用する。
        let size_eff = {
            let base = if sm_size { parent_cumul_scale } else { [1.0f32, 1.0] };
            // Collider2d の keep_aspect_ratio 設定を参照してsize_effを調整する
            if let Some(coll_ent) = collider_slot_entity {
                if let Some(coll) = scene.world.get::<Collider2dComponent>(coll_ent) {
                    if coll.keep_aspect_ratio && sm_size {
                        match &coll.aspect_ratio_axis {
                            AspectRatioAxis::Width  => [base[0], base[0]],
                            AspectRatioAxis::Height => [base[1], base[1]],
                        }
                    } else { base }
                } else { base }
            } else { base }
        };

        // ── 2. ピボット補正（基準サイズ選択） ─────────────────────────────────
        //
        // CanvasTransform.position は「ピボット点」のローカル位置。
        // ボディ中心を「矩形の中心」に合わせるため、pivot 位置から中心への補正を加算する。
        //
        //   pivot_corr_canonical = (0.5 - ct.pivot) × ref_size × size_eff
        //
        // 基準サイズの選択:
        //   ・CanvasComponent あり → Canvas サイズ（Sprite 非依存）
        //   ・CanvasComponent なし + Collider2d あり → コライダーバウンディングボックスサイズ
        //     → pivot=[0,0] = 左上端, pivot=[0,1] = 左下端 などのアンカー端揃えが機能する
        //   ・いずれもなし → 補正なし（body_pos = pivot 点のまま）
        //
        // canvas_collect.rs の to_mat4_sized は T(pos)*R(rot)*S(scale)*T(-pivot) の順なので
        //   pivot_corr_world = R(parent_rot)*R(local_rot)*S(ct.scale)*pivot_corr_canonical
        let pivot_corr_local: [f32; 2] = if let Some([bw, bh]) = my_canvas_base {
            // CanvasComponent あり: Canvas サイズ基準（自動解像度上書きを反映）
            let eff_w = bw * size_eff[0];
            let eff_h = bh * size_eff[1];
            [(0.5 - ct.pivot[0]) * eff_w, (0.5 - ct.pivot[1]) * eff_h]
        } else if let Some(slot_entity) = collider_slot_entity {
            // CanvasComponent なし: コライダーバウンディングボックスサイズ基準
            // これにより pivot=[0,0] の左上端や pivot=[0,1] の左下端にアンカーを合わせられる
            if let Some(collider) = scene.world.get::<Collider2dComponent>(slot_entity) {
                let (ref_w, ref_h) = collider.shape.bounding_size();
                let eff_w = ref_w * size_eff[0];
                let eff_h = ref_h * size_eff[1];
                [(0.5 - ct.pivot[0]) * eff_w, (0.5 - ct.pivot[1]) * eff_h]
            } else {
                [0.0f32, 0.0]
            }
        } else {
            [0.0f32, 0.0]
        };

        // canvas_collect.rs の to_mat4_sized と同じ変換順：
        //   R(local_rot) * S(ct.scale) * pivot_corr_canonical
        let local_rot = ct.rotation.to_radians();
        let (sin_l, cos_l) = local_rot.sin_cos();
        let pivx = pivot_corr_local[0];
        let pivy = pivot_corr_local[1];
        let rotated_scaled = [
            cos_l * ct.scale[0] * pivx - sin_l * ct.scale[1] * pivy,
            sin_l * ct.scale[0] * pivx + cos_l * ct.scale[1] * pivy,
        ];

        // さらに親のワールド回転で変換してワールド空間へ
        let (sin_p, cos_p) = parent_world_rot.sin_cos();
        let pivot_corr_world = [
            cos_p * rotated_scaled[0] - sin_p * rotated_scaled[1],
            sin_p * rotated_scaled[0] + cos_p * rotated_scaled[1],
        ];

        // ── 3. ボディ位置 = ピボット点 + ピボット補正 ────────────────────────
        let body_pos_px = [
            actor_pivot_world[0] + pivot_corr_world[0],
            actor_pivot_world[1] + pivot_corr_world[1],
        ];

        result.push(Actor2dPhysicsCtx {
            dfs_id,
            actor_entity: actor.entity,
            body_pos_px,
            pivot_world_px: actor_pivot_world,
            rot_rad:      actor_world_rot,
            scale:        ct.scale,
            anchor_off,
            pivot_corr_local,
            sm_transform,
            cumul_scale:          parent_cumul_scale,
            size_sx:              size_eff[0],
            size_sy:              size_eff[1],
            collider_slot_entity,
            parent_canvas_origin,
            parent_world_rot,
        });
    }

    result
}

// ─── App メソッド ─────────────────────────────────────────────────────────────

impl App {
    // ─── ビューポートサイズ計算 ─────────────────────────────────────

    /// 2D 物理変換に使用するビューポートサイズを計算して返す。
    ///
    /// シーン SS キャンバスモードのとき実ビューポートサイズ [w, h] を返す。
    /// それ以外（ワールドスペース・アクター編集タブ等）は None を返す。
    pub(super) fn compute_viewport_size_2d(&self) -> Option<[f32; 2]> {
        let is_canvas        = self.canvas_world_lines.contains(&self.active_world_line);
        let is_actor_edit_2d = self.actor_edit_canvas_wls.contains(&self.active_world_line);
        let in_editor        = self.mode == RuntimeMode::Edit;
        // Edit の 2D シーンビュー（ビューポートタブ）も SS レイアウト扱いにする
        // （frame_renderer 側の use_screen_space と同一条件。描画と物理・ギズモの座標系を一致させる）
        let use_screen_space = self.canvas_screen_space_overlay || !in_editor || is_actor_edit_2d
            || self.edit_view_is_2d();
        let scene_canvas_ss  = is_canvas && use_screen_space && !is_actor_edit_2d;

        if !scene_canvas_ss { return None; }

        self.window.as_ref().map(|w| {
            let s = w.inner_size();
            [s.width as f32, s.height as f32]
        }).or(Some([1280.0, 720.0]))
    }

    /// 2D 物理・スクリーン座標収集用に、ビューポート上書きマップと
    /// ルート自動解像度マップをまとめて構築する共通ヘルパー。
    ///
    /// `viewport_size` が Some（= シーン SS レイアウト）の場合は
    /// build_ss_layout_maps（描画と同一。View2D では設計空間表示）を使用し、
    /// None（ワールドスペース・アクター編集タブ）ではビューポート上書きのみ構築して
    /// 従来動作を維持する。
    fn build_2d_layout_maps(
        &self,
        scene:         &Scene,
        viewport_size: Option<[f32; 2]>,
        win_w: f32,
        win_h: f32,
    ) -> (HashMap<Entity, [f32; 2]>, HashMap<Entity, [f32; 2]>) {
        if viewport_size.is_some() {
            self.build_ss_layout_maps(
                &scene.actors, &scene.world,
                self.active_world_line, win_w, win_h, None,
            )
        } else {
            (
                build_canvas_viewport_map(
                    &scene.actors, &scene.world,
                    self.active_world_line, win_w, win_h, None,
                ),
                HashMap::new(),
            )
        }
    }

    /// 指定 DFS ID の 2D アクターについて、描画（collect_sprite_items）と
    /// **完全に同一の変換チェーン**で計算したレイアウトコンテキストを返す。
    ///
    /// 自動解像度・ルート恒等化・ビューポート基準アンカー・auto_scale を
    /// すべて反映済みの pivot_world_px / anchor_off / 親原点等が得られるため、
    /// ギズモ表示位置・ドラッグ書き戻しの座標計算に使用する。
    /// シーン SS レイアウト時のみ Some（ワールドスペース・アクター編集タブは None =
    /// 従来経路を使用する）。
    pub(super) fn actor_2d_layout_ctx(&self, dfs_id: u32) -> Option<Actor2dPhysicsCtx> {
        let viewport_size = self.compute_viewport_size_2d()?;
        let scene = self.scene.as_ref()?;
        let [win_w, win_h] = viewport_size;

        // 対象アクターのエンティティを解決する（コンテキストの検索キー）
        let entity = {
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, self.active_world_line, dfs_id, &mut c)?.entity
        };

        // 描画と同一のマップ・チェーンでコンテキストを収集する
        let (canvas_vp_overrides, root_auto_sizes) =
            self.build_2d_layout_maps(scene, Some(viewport_size), win_w, win_h);
        collect_actor2d_contexts(
            scene, self.active_world_line, Some(viewport_size),
            &canvas_vp_overrides, &root_auto_sizes, self.edit_view_is_2d(),
        )
        .into_iter()
        .find(|ctx| ctx.actor_entity == entity)
    }

    /// スクリプトの ScreenPosition API 用に、全 Actor2D の
    /// 「アクターエンティティ → ウィンドウ左上原点のスクリーン座標（ピクセル）」を収集する。
    ///
    /// collect_actor2d_contexts（描画・物理と同一の座標変換チェーン）の body_pos_px は
    /// ビューポート中心を原点とする ortho 空間なので、ウィンドウ半分を加算して
    /// 左上原点のスクリーン座標へ変換する。座標は CanvasTransform のピボット点に対応する。
    /// frame_renderer がフレームごとに呼び、host_api へ公開する。
    pub(super) fn collect_2d_screen_positions(&self) -> Vec<(Entity, [f32; 2])> {
        let viewport_size = self.compute_viewport_size_2d();
        let Some(scene) = &self.scene else { return Vec::new() };

        // ビューポート上書き + ルート自動解像度マップ（描画と同一条件・共通ヘルパー）
        let (win_w, win_h) = viewport_size.map(|[w, h]| (w, h)).unwrap_or((1280.0, 720.0));
        let (canvas_vp_overrides, root_auto_sizes) =
            self.build_2d_layout_maps(scene, viewport_size, win_w, win_h);

        // ScreenPosition API はゲーム画面（中央原点）基準で body_pos_px を得て
        // +win/2 で左上原点スクリーン座標へ変換する契約のため、design_space=false 固定。
        let contexts = collect_actor2d_contexts(
            scene, self.active_world_line, viewport_size, &canvas_vp_overrides,
            &root_auto_sizes, false,
        );

        // ortho 空間（ビューポート中心原点・Y 下向き）→ ウィンドウ左上原点へ変換する
        contexts.into_iter()
            .map(|c| (
                c.actor_entity,
                [c.body_pos_px[0] + win_w * 0.5, c.body_pos_px[1] + win_h * 0.5],
            ))
            .collect()
    }

    // ─── 起動 ────────────────────────────────────────────────────

    /// 2D 物理スレッドを起動し、シーン内の全 Collider2d Actor2D を物理ワールドに登録する。
    ///
    /// Play モード: 通常の 2D 物理シミュレーション（重力・ダイナミクスあり）。
    /// Edit モード (edit_physics_2d_with_rigidbody=false):
    ///   全ボディを kinematic にして重力を無効化し、衝突検出のみ行う。
    /// Edit モード (edit_physics_2d_with_rigidbody=true):
    ///   通常と同様（重力・ダイナミクスあり）。
    pub(super) fn start_physics_2d(&mut self) {
        // ビューポートサイズを先に取得する（scene の借用が解放されてから再借用するため）
        let viewport_size = self.compute_viewport_size_2d();

        let Some(scene) = &self.scene else { return };

        // ビューポート上書き + ルート自動解像度マップ（描画と同一条件・共通ヘルパー）
        let (win_w, win_h) = viewport_size.map(|[w, h]| (w, h)).unwrap_or((1280.0, 720.0));
        let (canvas_vp_overrides, root_auto_sizes) =
            self.build_2d_layout_maps(scene, viewport_size, win_w, win_h);

        // 編集時コライダーのみモード: 全ボディを kinematic 扱い
        let force_kinematic = self.mode == RuntimeMode::Edit
            && !self.edit_physics_2d_with_rigidbody;

        // actor2d コンテキストを収集し、Collider2d 付きのものを物理ワールドに登録する。
        // design_space は表示（frame_renderer）と一致させ、コライダー位置と描画をそろえる。
        let design_space = self.edit_view_is_2d();
        let contexts = collect_actor2d_contexts(
            scene, self.active_world_line, viewport_size, &canvas_vp_overrides, &root_auto_sizes,
            design_space,
        );

        // ── 2D キャンバス物理スレッドを起動する ────────────────────────────────
        let thread = PhysicsThread2d::spawn();

        for ctx in &contexts {
            let Some(slot_entity) = ctx.collider_slot_entity else { continue };
            let Some(collider) = scene.world.get::<Collider2dComponent>(slot_entity) else { continue };

            // body_pos_px は ortho 空間（ビューポート中心が原点）で計算済みなので PPM で除算するだけ
            let position = [
                ctx.body_pos_px[0] / PIXELS_PER_METER,
                ctx.body_pos_px[1] / PIXELS_PER_METER,
            ];
            // コライダーオフセットに size_sx/sy を適用する
            let collider_offset = [
                collider.offset[0] * ctx.size_sx / PIXELS_PER_METER,
                collider.offset[1] * ctx.size_sy / PIXELS_PER_METER,
            ];
            // コライダー形状に size_sx/sy を適用する（scale_size=true 時のみ実際にスケールされる）
            let shape = collider.shape.to_physics_shape_scaled(ctx.size_sx, ctx.size_sy);

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

        // 重力方向を設定する（3D キャンバスの gravity_mode / Z 回転を考慮）
        if !force_kinematic {
            let gravity = self.compute_scene_gravity_2d();
            if gravity != [0.0, crate::engine::physics::DEFAULT_GRAVITY_2D[1]] {
                thread.send(PhysicsCommand2d::SetGravity { gravity });
            }
        }

        self.physics_thread_2d = Some(thread);
    }

    // ─── 停止 ────────────────────────────────────────────────────

    /// Play モード停止時に 2D 物理スレッドを終了する（Drop で Stop 送信・join）。
    pub(super) fn stop_physics_2d(&mut self) {
        self.physics_thread_2d = None;
        // 3D キャンバス物理スレッドもすべて停止する（Drop で Stop 送信）
        self.canvas_3d_physics.clear();
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
        let Some(_) = &self.physics_thread_2d else { return };

        // Edit コライダーのみモードでは CanvasTransform 更新をスキップする
        let should_apply_transforms = self.mode != RuntimeMode::Edit
            || self.edit_physics_2d_with_rigidbody;

        // ビューポートサイズを取得する（scene 借用前に計算）
        let viewport_size = self.compute_viewport_size_2d();

        // ── Actor2d コンテキストを一括収集（read-only borrow で完結させる）────
        let contexts: Vec<Actor2dPhysicsCtx> = if let Some(scene) = &self.scene {
            // ビューポート上書き + ルート自動解像度マップ（描画と同一条件・共通ヘルパー）
            let (win_w, win_h) = viewport_size.map(|[w, h]| (w, h)).unwrap_or((1280.0, 720.0));
            let (canvas_vp_overrides, root_auto_sizes) =
                self.build_2d_layout_maps(scene, viewport_size, win_w, win_h);
            collect_actor2d_contexts(
                scene, self.active_world_line, viewport_size, &canvas_vp_overrides, &root_auto_sizes,
                self.edit_view_is_2d(),
            )
        } else {
            return;
        };

        // ── ギズモドラッグ中アクター（2D）の kinematic 切り替え ──────────────
        let new_drag_entity_id: Option<u64> = if self.drag.gizmo_drag.is_some() {
            self.actor_virtual_selected_idx.map(|dfs_id| dfs_id as u64 + 1)
        } else {
            match &self.inspector_transform_drag {
                Some(InspectorTransformDrag::CanvasActor { dfs_id, .. }) => Some(*dfs_id as u64 + 1),
                _ => None,
            }
        };

        // ドラッグ状態変化時にボディタイプを切り替える。
        // ※ ライブシミュレーション開始（&mut self メソッド）を呼ぶため、
        //   thread は送信のたびに狭いスコープで借用する。
        if new_drag_entity_id != self.dragging_physics_2d_entity_id {
            if let Some(old_id) = self.dragging_physics_2d_entity_id {
                // ドラッグ終了: 現在レイアウト位置を final_position として Dynamic に戻す。
                // ドラッグ中ライブシミュレーションが走っている場合は、この復帰だけで
                // 演算がそのまま継続する（再起動は行わない）。
                let final_position = contexts.iter()
                    .find(|c| c.dfs_id == old_id)
                    .map(|c| (
                        [c.body_pos_px[0] / PIXELS_PER_METER, c.body_pos_px[1] / PIXELS_PER_METER],
                        c.rot_rad,
                    ));
                if let Some(thread) = &self.physics_thread_2d {
                    thread.send(PhysicsCommand2d::SetBodyKinematic {
                        entity_id:      old_id,
                        is_kinematic:   false,
                        final_position,
                        // Dynamic 復帰時は smooth は無視される（値は任意）
                        smooth:         false,
                    });
                }
            }
            if let Some(new_id) = new_drag_entity_id {
                let ecs_start_pos = contexts.iter()
                    .find(|c| c.dfs_id == new_id)
                    .map(|c| (
                        [c.body_pos_px[0] / PIXELS_PER_METER, c.body_pos_px[1] / PIXELS_PER_METER],
                        c.rot_rad,
                    ));

                // 【ドラッグ中ライブシミュレーション（2D）】の発動判定を先に行う（smooth 指定に使う）。
                // 2D RigidBody タイムラインモードで最新フレーム停止中に Collider2d 付き
                // アクターのドラッグが開始された場合、3D と同様に物理を Pause 解除して
                // ドラッグ中も演算を継続する（自身は kinematic 追従・他は押しのけ）。
                // 過去フレームへシーク中（!at_latest）は発動しない（誤爆防止）。
                let start_live_sim = self.mode == RuntimeMode::Edit
                    && self.edit_physics_2d_enabled
                    && self.edit_physics_2d_with_rigidbody
                    && self.edit_physics_paused
                    && self.edit_physics_at_latest
                    && contexts.iter()
                        .any(|c| c.dfs_id == new_id && c.collider_slot_entity.is_some());

                // ライブシミュレーション時のみ smooth=true（目標追従・速度クランプ）にして、
                // 可動 kinematic が乗っている Dynamic ボディを吹き飛ばすのを防ぐ。
                if let Some(thread) = &self.physics_thread_2d {
                    thread.send(PhysicsCommand2d::SetBodyKinematic {
                        entity_id:    new_id,
                        is_kinematic: true,
                        final_position: ecs_start_pos,
                        smooth:       start_live_sim,
                    });
                }

                if start_live_sim {
                    self.begin_edit_physics_drag_live_sim();
                }
            }
            self.dragging_physics_2d_entity_id = new_drag_entity_id;
        }

        let thread = self.physics_thread_2d.as_ref().unwrap();
        let result = thread.recv_latest();

        if let Some(ref result) = result {
            // ① Dynamic Rigidbody2D の CanvasTransform を ECS に書き戻す
            if should_apply_transforms {
                if let Some(scene) = &mut self.scene {
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

                        if let Some(ctx) = contexts.iter().find(|c| c.dfs_id == *entity_id) {
                            write_back_canvas_transform(scene, ctx, *new_pos, *new_rot);
                        }
                    }
                }
            }

            // ②-0 Play モード時: 2D 衝突・トリガーイベントをスクリプトのコールバックへ配信する
            //（3D と同じ OnCollisionEnter / OnTriggerEnter 等が呼ばれる）
            if self.mode == RuntimeMode::Play
                && (!result.collision_events.is_empty() || !result.trigger_events.is_empty())
            {
                self.dispatch_physics2d_events_to_scripts(
                    &result.collision_events,
                    &result.trigger_events,
                );
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
            //
            // 【修正2】3D と同様、色判定は毎フレームの NarrowPhase 直接クエリ
            // （active_contact_entity_ids）へ寄せる。イベントベースの Stay 集合は
            // Stopped 取りこぼしで残留しうるため使用しない。トリガーは別集合と和を取る。
            let mut frame_colliding: std::collections::HashSet<u64> =
                std::collections::HashSet::new();
            for &eid in &result.active_contact_entity_ids {
                frame_colliding.insert(eid);
            }
            for &eid in &result.active_trigger_entity_ids {
                frame_colliding.insert(eid);
            }
            self.active_collision_2d_dfs_ids = frame_colliding;

            // 収束停止判定用に、全 Dynamic ボディ（2D）の最大速度を退避する。
            self.store_edit_physics_rest_speeds_2d(result.max_linear_speed, result.max_angular_speed);
        }

        // ④ ビューポートサイズ変化時に Static ボディを再登録する
        //
        // ウィンドウリサイズによって anchor ベースのボディ位置（床・壁など）が変化するため、
        // use_rigidbody=false の static ボディを RemoveObject + AddObject で再配置する。
        //
        // kinematic ボディは⑤で毎フレーム UpdateKinematic を送信しており、
        // dynamic ボディはシミュレーション継続のため位置リセットを行わない。
        let viewport_changed = self.last_physics_2d_viewport != viewport_size;
        if viewport_changed {
            self.last_physics_2d_viewport = viewport_size;

            if let (Some(thread), Some(scene)) = (&self.physics_thread_2d, &self.scene) {
                for ctx in &contexts {
                    let Some(slot_entity) = ctx.collider_slot_entity else { continue };
                    let Some(collider) = scene.world.get::<Collider2dComponent>(slot_entity) else { continue };

                    if collider.use_rigidbody {
                        // rigidbody ボディ（dynamic/kinematic）のコライダー形状を差し替える。
                        // scale_size=true のとき size_sx/size_sy がビューポートに連動するため、
                        // RescaleCollider で形状のみ更新する（位置・速度・回転は保持）。
                        let shape = collider.shape.to_physics_shape_scaled(ctx.size_sx, ctx.size_sy);
                        let collider_offset = [
                            collider.offset[0] * ctx.size_sx / PIXELS_PER_METER,
                            collider.offset[1] * ctx.size_sy / PIXELS_PER_METER,
                        ];
                        thread.send(PhysicsCommand2d::RescaleCollider {
                            entity_id:       ctx.dfs_id,
                            shape,
                            scale:           ctx.scale,
                            collider_offset,
                        });
                    } else {
                        // static ボディ: anchor ベースのボディ位置が変化するため RemoveObject + AddObject で再登録する
                        let position = [
                            ctx.body_pos_px[0] / PIXELS_PER_METER,
                            ctx.body_pos_px[1] / PIXELS_PER_METER,
                        ];
                        let collider_offset = [
                            collider.offset[0] * ctx.size_sx / PIXELS_PER_METER,
                            collider.offset[1] * ctx.size_sy / PIXELS_PER_METER,
                        ];
                        let shape = collider.shape.to_physics_shape_scaled(ctx.size_sx, ctx.size_sy);

                        // 旧ボディを削除して新位置で再登録する
                        thread.send(PhysicsCommand2d::RemoveObject { entity_id: ctx.dfs_id });
                        thread.send(PhysicsCommand2d::AddObject(PhysicsObject2d {
                            entity_id:     ctx.dfs_id,
                            position,
                            rotation:      ctx.rot_rad,
                            scale:         ctx.scale,
                            collider:      shape,
                            collider_offset,
                            rigidbody:     None, // static ボディは rigidbody なし
                            is_trigger:    collider.is_trigger,
                            physics_layer: collider.physics_layer,
                            layer_mask:    collider.layer_mask,
                        }));
                    }
                }
            }
        }

        // ⑤ Kinematic Actor2D の body_pos を物理スレッドへ送信する
        let Some(scene) = &self.scene else { return };
        let thread = self.physics_thread_2d.as_ref().unwrap();

        for ctx in &contexts {
            let Some(slot_entity) = ctx.collider_slot_entity else { continue };
            let Some(collider) = scene.world.get::<Collider2dComponent>(slot_entity) else { continue };
            if !collider.use_rigidbody || !collider.is_kinematic { continue; }

            thread.send(PhysicsCommand2d::UpdateKinematic {
                entity_id: ctx.dfs_id,
                position:  [ctx.body_pos_px[0] / PIXELS_PER_METER, ctx.body_pos_px[1] / PIXELS_PER_METER],
                rotation:  ctx.rot_rad,
            });
        }

        // ⑥ ScreenDown モードの 3D キャンバスがある場合、Z 回転変化に応じて重力を更新する
        {
            let gravity = self.compute_scene_gravity_2d();
            let should_apply = self.mode != RuntimeMode::Edit || self.edit_physics_2d_with_rigidbody;
            if should_apply {
                let thread = self.physics_thread_2d.as_ref().unwrap();
                thread.send(PhysicsCommand2d::SetGravity { gravity });
            }
        }

        // ⑦ ドラッグ中 Actor2D の現在位置を kinematic 更新として送信する
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
/// `new_pos` は「ortho 空間ピクセル / PIXELS_PER_METER」（メートル単位）。
/// body_pos_px = キャンバス中心のワールド位置（ピボット点 + ピボット補正適用済み）なので、
/// まずピボット補正を逆適用して actor_pivot_world を求めてから逆変換する。
///
/// # 逆変換の計算
///   1. new_pos * PPM = new_body_px（ボディ中心 ortho 座標）
///   2. actor_pivot_world = new_body_px - R(new_rot) * pivot_corr_local（ピボット補正逆適用）
///   3. eff_pos_local = R(-parent_world_rot) * (actor_pivot_world - parent_canvas_origin)
///   4. ct.position = (eff_pos_local - anchor_off) / cumul_scale  (sm_transform=true)
///                  = eff_pos_local - anchor_off                    (sm_transform=false)
///   5. ct.rotation = new_rot - parent_world_rot
fn write_back_canvas_transform(
    scene:   &mut Scene,
    ctx:     &Actor2dPhysicsCtx,
    new_pos: [f32; 2],
    new_rot: f32,
) {
    // ① new_pos（メートル）→ ortho ピクセル = ボディ中心のワールド座標
    let new_body_px = [
        new_pos[0] * PIXELS_PER_METER,
        new_pos[1] * PIXELS_PER_METER,
    ];

    // ピボット補正を逆適用してピボット点のワールド座標を求める。
    // 順変換: body_pos = actor_pivot + R(parent_rot)*R(local_rot)*S(scale)*pivot_corr_local
    // 逆変換: actor_pivot = body_pos - R(parent_rot)*R(new_local_rot)*S(scale)*pivot_corr_local
    let new_local_rot = new_rot - ctx.parent_world_rot;
    let (sin_nl, cos_nl) = new_local_rot.sin_cos();
    let cx = ctx.pivot_corr_local[0]; // (0.5-pivot.x) * eff_w
    let cy = ctx.pivot_corr_local[1]; // (0.5-pivot.y) * eff_h
    // R(new_local_rot) * S(ctx.scale) * pivot_corr_local
    let rotated_scaled = [
        cos_nl * ctx.scale[0] * cx - sin_nl * ctx.scale[1] * cy,
        sin_nl * ctx.scale[0] * cx + cos_nl * ctx.scale[1] * cy,
    ];
    // さらに親のワールド回転を掛けてワールド空間へ
    let (sin_p, cos_p) = ctx.parent_world_rot.sin_cos();
    let pivot_corr_world = [
        cos_p * rotated_scaled[0] - sin_p * rotated_scaled[1],
        sin_p * rotated_scaled[0] + cos_p * rotated_scaled[1],
    ];
    let actor_pivot_world = [
        new_body_px[0] - pivot_corr_world[0],
        new_body_px[1] - pivot_corr_world[1],
    ];

    // ② 親ワールド変換の逆適用: ortho ワールド座標 → 親キャンバスローカル座標
    //    eff_pos_local = R(-parent_world_rot) * (actor_pivot_world - parent_canvas_origin)
    let dx = actor_pivot_world[0] - ctx.parent_canvas_origin[0];
    let dy = actor_pivot_world[1] - ctx.parent_canvas_origin[1];
    let (sin_p, cos_p) = ctx.parent_world_rot.sin_cos();
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

// ─── 重力方向ヘルパー ────────────────────────────────────────────────────────

impl App {
    /// シーン内の 3D キャンバス（Actor3D + CanvasComponent）の gravity_mode と Z 回転から
    /// 現在のシーンで使用すべき重力方向ベクトルを計算する。
    ///
    /// 3D キャンバスが見つからない場合は DEFAULT_GRAVITY_2D を返す。
    /// 複数の 3D キャンバスがある場合は最初に見つかったものを使用する。
    pub(super) fn compute_scene_gravity_2d(&self) -> [f32; 2] {
        let wl = self.active_world_line;
        let Some(scene) = &self.scene else {
            return crate::engine::physics::DEFAULT_GRAVITY_2D;
        };

        // シーン内の Actor3D + CanvasComponent を持つアクターを探す
        for actor in scene.actors.iter().filter(|a| a.world_line == wl && !a.is_2d()) {
            let Some(slot) = actor.slots().iter().find(|s| s.kind == ComponentKind::Canvas) else { continue };
            let Some(cc) = scene.world.get::<CanvasComponent>(slot.entity) else { continue };

            let z_rot_deg = scene.world.get::<ActorTransform>(actor.entity)
                .map(|t| t.rotation[2])
                .unwrap_or(0.0);
            return compute_canvas_gravity(cc.gravity_mode, z_rot_deg);
        }

        crate::engine::physics::DEFAULT_GRAVITY_2D
    }
}

/// キャンバスの重力方向ベクトル [gx, gy] を計算する（m/s² 単位）。
///
/// `CanvasDown`: 常に [0, 9.81]（キャンバスローカル Y+ 下方向）。
/// `ScreenDown`: キャンバスの Z 回転 θ から、スクリーン下方向がキャンバスローカルで
///   どの方向に対応するかを計算する。
///   θ=0 → [0, 9.81]（変化なし）、θ=90° → [9.81, 0]（キャンバス「右」が画面下）
fn compute_canvas_gravity(mode: GravityMode, z_rot_deg: f32) -> [f32; 2] {
    const G: f32 = 9.81;
    match mode {
        GravityMode::CanvasDown => [0.0, G],
        GravityMode::ScreenDown => {
            // スクリーン下 [0, G] をキャンバス Z 回転で逆変換:
            // rotate([0, G], -θ) = [G*sin(θ), G*cos(θ)]
            let theta = z_rot_deg.to_radians();
            [G * theta.sin(), G * theta.cos()]
        }
    }
}
