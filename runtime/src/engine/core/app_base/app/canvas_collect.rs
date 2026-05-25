// ============================================================
//  canvas_collect.rs — キャンバスアクター情報収集ユーティリティ
//
//  スプライト描画アイテム・キャンバス矩形アウトライン・ID パスアイテムを
//  アクターツリーから DFS 順に収集するためのフリー関数群。
//
//  フレームループ (frame.rs の on_redraw_requested) から呼び出される。
//  CanvasComponent のスケールモード (scale_transform / scale_size) に応じた
//  累積スケール伝播を親子間で管理する。
// ============================================================

use std::collections::HashMap;
use std::sync::Arc;

use crate::engine::components::{
    CanvasTransform, CanvasComponent, SpriteComponent, ComponentKind,
    CanvasViewportRef, CameraComponent, ScalingMode,
};
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;
use crate::engine::methods::drawer::{
    DrawContext, LineBatch, GpuSpriteTexture, load_sprite_texture,
};
use crate::engine::methods::gizmo_interact::mat4x4_mul;

// ============================================================
//  collect_sprite_items
// ============================================================

/// スプライト描画リソースをアクターツリーから DFS 順に収集する。
///
/// CanvasTransform + SpriteComponent を持つアクターを再帰的に走査し、
/// `(GPU行列, カラー, テクスチャArc)` のタプルを `out` に追加する。
/// テクスチャは `draw_ctx.sprite_tex_cache` でキャッシュする（失敗も記録して毎フレームスキップ）。
///
/// # スケールモード
/// - `parent_scale_mode = (scale_transform, scale_size)`
/// - `scale_transform=true`  : 子の位置に親の累積スケールを乗算する
/// - `scale_size=true`       : 子のサイズに親の累積スケールを乗算する
/// - 回転は常に追従する
#[allow(clippy::too_many_arguments)]
pub(super) fn collect_sprite_items(
    actors:             &[Actor],
    world:              &World,
    wl:                 u32,
    draw_ctx:           &DrawContext,
    // 親アクターの CanvasComponent サイズ（anchor 計算用）。None = ルートレベル。
    parent_canvas_size: Option<[f32; 2]>,
    // 親のワールド行列（スケールなし: 回転+平行移動のみ）
    parent_world_rs:    [[f32; 4]; 4],
    // 親の累積スケール。スケールモードに応じて子に伝播するかを制御する。
    parent_cumul_scale: [f32; 2],
    // 直前の親 CanvasComponent のスケールモード (scale_transform, scale_size)
    parent_scale_mode:  (bool, bool),
    // ワールドスペース変換スケール（1.0=スクリーンスペース, CANVAS_WORLD_SCALE=ワールドスペース）
    canvas_scale:       f32,
    // Y 軸符号（スクリーンスペース=1.0, ワールドスペース=-1.0 で Y を反転）
    y_sign:             f32,
    // シーン SS モード時のビューポートサイズ（ルートアンカー計算用）。None = アクター編集タブまたはワールドスペース。
    viewport_size:      Option<[f32; 2]>,
    // ルートキャンバスアクターごとの有効ビューポートサイズ上書き（Camera 参照用）。
    // actor.entity → [w, h]。viewport_size より優先される。
    canvas_viewport_overrides: &HashMap<Entity, [f32; 2]>,
    out:                &mut Vec<([[f32; 4]; 4], [f32; 4], Option<Arc<GpuSpriteTexture>>)>,
) {
    let (sm_transform, sm_size) = parent_scale_mode;

    for actor in actors {
        if actor.world_line != wl { continue; }
        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
        if let Some(ct) = ct_opt {
            // アンカーオフセット計算:
            // ルートレベル（parent_canvas_size=None）かつシーン SS モードでは
            // ビューポートを仮想親として扱い、ortho 原点（画面中央）からのオフセットを計算する。
            // Camera 参照が設定されているルートキャンバスはオーバーライドマップの値を優先する。
            // それ以外は親キャンバスサイズ基準。
            let eff_viewport = if parent_canvas_size.is_none() {
                canvas_viewport_overrides.get(&actor.entity).copied().or(viewport_size)
            } else {
                viewport_size
            };
            let (anchor_off_x, anchor_off_y) = if parent_canvas_size.is_none() {
                if let Some([vw, vh]) = eff_viewport {
                    // 画面中央が ortho 原点 → anchor=0,0 で画面左上に寄せるため -vp/2 オフセット
                    (vw * ct.anchor[0] - vw / 2.0,
                     vh * ct.anchor[1] - vh / 2.0)
                } else {
                    (0.0, 0.0)
                }
            } else {
                (parent_canvas_size.map_or(0.0, |[pw, _]| pw * ct.anchor[0] * parent_cumul_scale[0]),
                 parent_canvas_size.map_or(0.0, |[_, ph]| ph * ct.anchor[1] * parent_cumul_scale[1]))
            };

            // 有効位置（スケールモードに応じて位置にスケールを乗算する）
            let eff_pos = if sm_transform {
                [ct.position[0] * parent_cumul_scale[0] + anchor_off_x,
                 ct.position[1] * parent_cumul_scale[1] + anchor_off_y]
            } else {
                [ct.position[0] + anchor_off_x,
                 ct.position[1] + anchor_off_y]
            };

            // 有効 CanvasTransform（位置を調整済み・anchor は適用済み）
            let eff_ct = CanvasTransform {
                position: eff_pos,
                rotation: ct.rotation,
                scale:    ct.scale,
                pivot:    ct.pivot,
                anchor:   [0.0, 0.0],
            };

            // 自アクターの CanvasComponent を取得する
            let my_canvas = actor.slots().iter()
                .filter(|s| s.kind == ComponentKind::Canvas)
                .find_map(|s| world.get::<CanvasComponent>(s.entity));
            // sm_size による拡縮を反映した有効キャンバスサイズ
            let (my_eff_w, my_eff_h) = my_canvas.map(|cc| (
                cc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 },
                cc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 },
            )).unwrap_or((1.0, 1.0));

            // 自分のワールド行列（スケールなし）を親 world_rs と合成する
            // pivot オフセットを正しく計算するため実際のキャンバスサイズを渡す
            let self_world_rs = mat4x4_mul(
                parent_world_rs,
                CanvasTransform { scale: [1.0, 1.0], ..eff_ct.clone() }
                    .to_mat4_sized(my_eff_w, my_eff_h),
            );

            // SpriteComponent スロットを走査して GPU 行列とテクスチャを収集する
            for slot in actor.slots() {
                if slot.kind == ComponentKind::Sprite {
                    if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                        // scale_size モードに応じたスプライト有効サイズ
                        let eff_w = sc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 };
                        let eff_h = sc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 };
                        // スプライト行優先行列を親 world_rs と合成し、GPU 列優先に変換する
                        let sprite_world = mat4x4_mul(parent_world_rs, eff_ct.to_sprite_mat4(eff_w, eff_h));
                        // y_sign でキャンバス Y 軸（下向き）→ ワールド Y 軸（上向き）を反転する
                        let csy = canvas_scale * y_sign;
                        let gpu_mat = [
                            [sprite_world[0][0] * canvas_scale, sprite_world[1][0] * csy, 0.0, 0.0],
                            [sprite_world[0][1] * canvas_scale, sprite_world[1][1] * csy, 0.0, 0.0],
                            [0.0,                               0.0,                      1.0, 0.0],
                            [sprite_world[0][3] * canvas_scale, sprite_world[1][3] * csy, 0.0, 1.0],
                        ];
                        // テクスチャをキャッシュから取得または新規ロードする
                        // キャッシュ値: Some(arc)=成功 / None=失敗済み（毎フレームのリトライ・ログ爆発防止）
                        let tex = if sc.texture_path.is_empty() {
                            None
                        } else {
                            let path_str = sc.texture_path.clone();
                            let mut cache = draw_ctx.sprite_tex_cache.borrow_mut();
                            if !cache.contains_key(&path_str) {
                                // 初回のみロード試行（成否に関わらずキャッシュに記録）
                                let loaded = load_sprite_texture(
                                    &draw_ctx.device,
                                    &draw_ctx.queue,
                                    &path_str,
                                    &draw_ctx.pipelines.sprite.tex_bgl,
                                    &draw_ctx.pipelines.sprite.sampler,
                                );
                                // None（失敗）もキャッシュに入れて次フレームからスキップ
                                cache.insert(path_str.clone(), loaded);
                            }
                            // Some(Some(arc))=成功 / Some(None)=失敗 → flatten で None に統一
                            cache.get(&sc.texture_path).and_then(|e| e.clone())
                        };
                        out.push((gpu_mat, sc.color, tex));
                    }
                }
            }

            // 子アクターへの CanvasComponent 情報を構築する
            let child_info = my_canvas
                .map(|cc| ([cc.width, cc.height], (cc.scale_transform, cc.scale_size), cc.auto_scale));
            let child_canvas_size = child_info.map(|(sz, _, _)| sz);
            let child_scale_mode  = child_info.map(|(_, sm, _)| sm).unwrap_or((false, false));
            // ルートキャンバスかつ auto_scale=true のとき、ビューポートサイズ/参照サイズで自動スケールする
            // Camera 参照の場合は eff_viewport がカメラの描画範囲になる
            let auto_scale_factor = if parent_canvas_size.is_none() {
                if let (Some([vw, vh]), Some((_, _, true))) = (eff_viewport, child_info) {
                    [vw / my_eff_w, vh / my_eff_h]
                } else {
                    [1.0f32, 1.0]
                }
            } else {
                [1.0f32, 1.0]
            };
            // 子への累積スケール（scale_transform に応じて自分のスケールを積む）
            let child_cumul_scale = if child_scale_mode.0 {
                [parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
                 parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1]]
            } else {
                // スケール伝播なし: auto_scale のみ適用
                [ct.scale[0] * auto_scale_factor[0],
                 ct.scale[1] * auto_scale_factor[1]]
            };
            collect_sprite_items(
                &actor.children, world, wl, draw_ctx,
                child_canvas_size, self_world_rs,
                child_cumul_scale, child_scale_mode,
                canvas_scale, y_sign, viewport_size, canvas_viewport_overrides, out,
            );
        }
    }
}

// ============================================================
//  collect_canvas_rects
// ============================================================

/// CanvasComponent / Sprite のアウトライン矩形を LineBatch に追加する。
///
/// - CanvasComponent: キャンバス領域のアウトラインを常に描画する
/// - SpriteComponent: `selected_dfs_ids` に含まれる DFS ID のみ描画する
///
/// DFS カウンタは `collect_sprite_items` と同じ規則で全アクターを数える。
#[allow(clippy::too_many_arguments)]
pub(super) fn collect_canvas_rects(
    actors:             &[Actor],
    world:              &World,
    wl:                 u32,
    lb:                 &mut LineBatch,
    // キャンバスアウトラインの色 [r, g, b, a]
    col:                [f32; 4],
    // 現在選択中のアクター DFS ID リスト（Sprite アウトラインの描画判定に使う）
    selected_dfs_ids:   &[usize],
    counter:            &mut u32,
    parent_canvas_size: Option<[f32; 2]>,
    parent_world_rs:    [[f32; 4]; 4],
    parent_cumul_scale: [f32; 2],
    parent_scale_mode:  (bool, bool),
    canvas_scale:       f32,
    y_sign:             f32,
    viewport_size:      Option<[f32; 2]>,
    canvas_viewport_overrides: &HashMap<Entity, [f32; 2]>,
) {
    let (sm_transform, sm_size) = parent_scale_mode;

    for actor in actors {
        if actor.world_line != wl { continue; }
        let my_dfs = *counter as usize;
        *counter += 1;

        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
        if let Some(ct) = ct_opt {
            // アンカーオフセット計算（collect_sprite_items と同じロジック）
            // Camera 参照のルートキャンバスはオーバーライドマップの値を優先する
            let eff_viewport = if parent_canvas_size.is_none() {
                canvas_viewport_overrides.get(&actor.entity).copied().or(viewport_size)
            } else {
                viewport_size
            };
            let (anchor_off_x, anchor_off_y) = if parent_canvas_size.is_none() {
                if let Some([vw, vh]) = eff_viewport {
                    (vw * ct.anchor[0] - vw / 2.0,
                     vh * ct.anchor[1] - vh / 2.0)
                } else {
                    (0.0, 0.0)
                }
            } else {
                (parent_canvas_size.map_or(0.0, |[pw, _]| pw * ct.anchor[0] * parent_cumul_scale[0]),
                 parent_canvas_size.map_or(0.0, |[_, ph]| ph * ct.anchor[1] * parent_cumul_scale[1]))
            };

            // 有効位置（スケールモードに応じて）
            let eff_pos = if sm_transform {
                [ct.position[0] * parent_cumul_scale[0] + anchor_off_x,
                 ct.position[1] * parent_cumul_scale[1] + anchor_off_y]
            } else {
                [ct.position[0] + anchor_off_x,
                 ct.position[1] + anchor_off_y]
            };
            let eff_ct = CanvasTransform {
                position: eff_pos,
                rotation: ct.rotation,
                scale:    ct.scale,
                pivot:    ct.pivot,
                anchor:   [0.0, 0.0],
            };

            // pivot はノーマライズ値のため実際のキャンバスサイズで補正する
            let my_canvas_r = actor.slots().iter()
                .filter(|s| s.kind == ComponentKind::Canvas)
                .find_map(|s| world.get::<CanvasComponent>(s.entity));
            let (my_eff_w_r, my_eff_h_r) = my_canvas_r.map(|cc| (
                cc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 },
                cc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 },
            )).unwrap_or((1.0, 1.0));

            let self_world_rs = mat4x4_mul(
                parent_world_rs,
                CanvasTransform { scale: [1.0, 1.0], ..eff_ct.clone() }
                    .to_mat4_sized(my_eff_w_r, my_eff_h_r),
            );

            for slot in actor.slots() {
                match slot.kind {
                    ComponentKind::Canvas => {
                        // CanvasComponent: キャンバス領域のアウトラインを常に描画する
                        if let Some(cc) = world.get::<CanvasComponent>(slot.entity) {
                            let eff_w = cc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 };
                            let eff_h = cc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 };
                            let m = mat4x4_mul(parent_world_rs, eff_ct.to_mat4_sized(eff_w, eff_h));
                            let csy = canvas_scale * y_sign;
                            // ローカル座標をワールド（canvas_scale / y_sign 適用後）に変換するクロージャ
                            let tp = |lx: f32, ly: f32| -> [f32; 3] {
                                [(m[0][0]*lx + m[0][1]*ly + m[0][3]) * canvas_scale,
                                 (m[1][0]*lx + m[1][1]*ly + m[1][3]) * csy,
                                 0.0f32]
                            };
                            let tl = tp(0.0,   0.0  );
                            let tr = tp(eff_w, 0.0  );
                            let br = tp(eff_w, eff_h);
                            let bl = tp(0.0,   eff_h);
                            lb.add_line(tl, tr, col);
                            lb.add_line(tr, br, col);
                            lb.add_line(br, bl, col);
                            lb.add_line(bl, tl, col);
                        }
                    }
                    ComponentKind::Sprite => {
                        // SpriteComponent: 選択時のみアウトラインを描画する
                        if selected_dfs_ids.contains(&my_dfs) {
                            if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                                let eff_w = sc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 };
                                let eff_h = sc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 };
                                const SPRITE_OUTLINE_COL: [f32; 4] = [1.0, 0.95, 0.6, 0.85];
                                let m = mat4x4_mul(parent_world_rs, eff_ct.to_sprite_mat4(eff_w, eff_h));
                                let csy2 = canvas_scale * y_sign;
                                let tp = |lx: f32, ly: f32| -> [f32; 3] {
                                    [(m[0][0]*lx + m[0][1]*ly + m[0][3]) * canvas_scale,
                                     (m[1][0]*lx + m[1][1]*ly + m[1][3]) * csy2,
                                     0.0f32]
                                };
                                let tl = tp(0.0, 0.0);
                                let tr = tp(1.0, 0.0);
                                let br = tp(1.0, 1.0);
                                let bl = tp(0.0, 1.0);
                                lb.add_line(tl, tr, SPRITE_OUTLINE_COL);
                                lb.add_line(tr, br, SPRITE_OUTLINE_COL);
                                lb.add_line(br, bl, SPRITE_OUTLINE_COL);
                                lb.add_line(bl, tl, SPRITE_OUTLINE_COL);
                            }
                        }
                    }
                    _ => {}
                }
            }

            // 子への継承情報を構築する
            let child_info = my_canvas_r
                .map(|cc| ([cc.width, cc.height], (cc.scale_transform, cc.scale_size), cc.auto_scale));
            let child_canvas_size = child_info.map(|(sz, _, _)| sz);
            let child_scale_mode  = child_info.map(|(_, sm, _)| sm).unwrap_or((false, false));
            let auto_scale_factor = if parent_canvas_size.is_none() {
                if let (Some([vw, vh]), Some((_, _, true))) = (eff_viewport, child_info) {
                    [vw / my_eff_w_r, vh / my_eff_h_r]
                } else {
                    [1.0f32, 1.0]
                }
            } else {
                [1.0f32, 1.0]
            };
            let child_cumul_scale = if child_scale_mode.0 {
                [parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
                 parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1]]
            } else {
                [ct.scale[0] * auto_scale_factor[0],
                 ct.scale[1] * auto_scale_factor[1]]
            };
            collect_canvas_rects(
                &actor.children, world, wl, lb, col,
                selected_dfs_ids, counter,
                child_canvas_size, self_world_rs,
                child_cumul_scale, child_scale_mode,
                canvas_scale, y_sign, viewport_size, canvas_viewport_overrides,
            );
        }
    }
}

// ============================================================
//  collect_canvas_id_items
// ============================================================

/// キャンバスアクター ID アイテムを DFS 順に収集する。
///
/// DFS カウンタは `find_actor_by_dfs` と同じ規則で全アクターを数える
/// （`CanvasTransform` がないアクターも子を含めてカウント）。
///
/// # 出力 `out` のタプル要素
/// - `raw_id`:         GPU に書き込む ID 値 (`mc_total + dfs + 1`)
/// - `gpu_mat`:        GPU 変換行列（Sprite のみ出力）
/// - `sprite_tex_path`: `Some(path)` → スプライトありでアルファマスク有効
///                      `None`       → スプライトなしで全面選択可能
#[allow(clippy::too_many_arguments)]
pub(super) fn collect_canvas_id_items(
    actors:             &[Actor],
    world:              &World,
    wl:                 u32,
    counter:            &mut u32,
    parent_canvas_size: Option<[f32; 2]>,
    parent_world_rs:    [[f32; 4]; 4],
    parent_cumul_scale: [f32; 2],
    parent_scale_mode:  (bool, bool),
    canvas_scale:       f32,
    y_sign:             f32,
    viewport_size:      Option<[f32; 2]>,
    canvas_viewport_overrides: &HashMap<Entity, [f32; 2]>,
    // 3D MC インスタンスの総数（canvas_id の raw_id オフセット計算に使用）
    mc_total:           u32,
    out:                &mut Vec<(u32, [[f32; 4]; 4], Option<String>)>,
) {
    let (sm_transform, sm_size) = parent_scale_mode;
    for actor in actors {
        if actor.world_line != wl { continue; }
        let my_dfs = *counter;
        *counter += 1;

        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
        let (next_canvas_size, next_cumul_scale, next_scale_mode, next_world_rs) =
            if let Some(ct) = ct_opt {
                // アンカーオフセット（collect_sprite_items と同じロジック）
                // Camera 参照のルートキャンバスはオーバーライドマップの値を優先する
                let eff_viewport = if parent_canvas_size.is_none() {
                    canvas_viewport_overrides.get(&actor.entity).copied().or(viewport_size)
                } else {
                    viewport_size
                };
                let (anchor_off_x, anchor_off_y) =
                    if parent_canvas_size.is_none() {
                        if let Some([vw, vh]) = eff_viewport {
                            (vw * ct.anchor[0] - vw / 2.0,
                             vh * ct.anchor[1] - vh / 2.0)
                        } else { (0.0, 0.0) }
                    } else {
                        (parent_canvas_size.map_or(0.0, |[pw, _]| pw * ct.anchor[0] * parent_cumul_scale[0]),
                         parent_canvas_size.map_or(0.0, |[_, ph]| ph * ct.anchor[1] * parent_cumul_scale[1]))
                    };
                let eff_pos = if sm_transform {
                    [ct.position[0] * parent_cumul_scale[0] + anchor_off_x,
                     ct.position[1] * parent_cumul_scale[1] + anchor_off_y]
                } else {
                    [ct.position[0] + anchor_off_x,
                     ct.position[1] + anchor_off_y]
                };
                let eff_ct = CanvasTransform {
                    position: eff_pos,
                    rotation: ct.rotation,
                    scale:    ct.scale,
                    pivot:    ct.pivot,
                    anchor:   [0.0, 0.0],
                };

                // 自アクターの CanvasComponent
                let my_canvas = actor.slots().iter()
                    .filter(|s| s.kind == ComponentKind::Canvas)
                    .find_map(|s| world.get::<CanvasComponent>(s.entity));
                let (my_eff_w, my_eff_h) = my_canvas.map(|cc| (
                    cc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 },
                    cc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 },
                )).unwrap_or((1.0, 1.0));

                // 子への親ワールド RS 行列
                let self_world_rs = mat4x4_mul(
                    parent_world_rs,
                    CanvasTransform { scale: [1.0, 1.0], ..eff_ct.clone() }
                        .to_mat4_sized(my_eff_w, my_eff_h),
                );

                // ID quad 用 GPU 行列の構築
                // テクスチャパスが有効な Sprite のみビューポートからピッキング可能にする
                // Sprite なし・テクスチャパス空（単色）のアクターは空白領域とみなし選択不可
                let csy = canvas_scale * y_sign;
                let mut gpu_mat_and_path: Option<([[f32; 4]; 4], String)> = None;
                for slot in actor.slots() {
                    if slot.kind == ComponentKind::Sprite {
                        if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                            // テクスチャパスが空 = 単色スプライト → 選択不可
                            if sc.texture_path.is_empty() { break; }
                            let ew = sc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 };
                            let eh = sc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 };
                            let sw = mat4x4_mul(parent_world_rs, eff_ct.to_sprite_mat4(ew, eh));
                            gpu_mat_and_path = Some(([
                                [sw[0][0] * canvas_scale, sw[1][0] * csy, 0.0, 0.0],
                                [sw[0][1] * canvas_scale, sw[1][1] * csy, 0.0, 0.0],
                                [0.0, 0.0, 1.0, 0.0],
                                [sw[0][3] * canvas_scale, sw[1][3] * csy, 0.0, 1.0],
                            ], sc.texture_path.clone()));
                            break;
                        }
                    }
                }

                if let Some((gpu_mat, tex_path)) = gpu_mat_and_path {
                    // raw_id = mc_total + my_dfs + 1
                    // （0 = 背景、1..mc_total = 3D MC インスタンス）
                    out.push((mc_total + my_dfs + 1, gpu_mat, Some(tex_path)));
                }

                // 子への継承情報を計算する（collect_sprite_items と同じロジック）
                let child_info = my_canvas.map(|cc| (
                    [cc.width, cc.height],
                    (cc.scale_transform, cc.scale_size),
                    cc.auto_scale,
                ));
                let child_canvas_size = child_info.map(|(sz, _, _)| sz);
                let child_scale_mode  = child_info.map(|(_, sm, _)| sm).unwrap_or((false, false));
                let auto_scale_factor = if parent_canvas_size.is_none() {
                    if let (Some([vw, vh]), Some((_, _, true))) = (eff_viewport, child_info) {
                        [vw / my_eff_w, vh / my_eff_h]
                    } else { [1.0f32, 1.0] }
                } else { [1.0f32, 1.0] };
                let child_cumul_scale = if child_scale_mode.0 {
                    [parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
                     parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1]]
                } else {
                    [ct.scale[0] * auto_scale_factor[0],
                     ct.scale[1] * auto_scale_factor[1]]
                };
                (child_canvas_size, child_cumul_scale, child_scale_mode, self_world_rs)
            } else {
                // CanvasTransform なし: 子は親の情報をそのまま引き継ぐ
                (parent_canvas_size, parent_cumul_scale, parent_scale_mode, parent_world_rs)
            };

        // 常に子に再帰する（DFS カウンタを全アクターで管理するため）
        collect_canvas_id_items(
            &actor.children, world, wl, counter,
            next_canvas_size, next_world_rs,
            next_cumul_scale, next_scale_mode,
            canvas_scale, y_sign, viewport_size, canvas_viewport_overrides,
            mc_total, out,
        );
    }
}

// ============================================================
//  canvas_viewport_utils — CanvasViewportRef::Camera 解決ユーティリティ
//
//  frame_renderer.rs と physics2d_ops.rs の両方から参照されるため、
//  canvas_collect.rs (pub(super)) に配置する。
// ============================================================

/// スケーリングモードに応じたゲームビューポート矩形・アスペクト比・FOV を計算する。
///
/// 戻り値: (vp_x, vp_y, vp_w, vp_h, proj_aspect, fov_y_rad)
pub(super) fn compute_game_viewport(
    scaling_mode: &ScalingMode,
    window_w:  f32,
    window_h:  f32,
    target_w:  u32,
    target_h:  u32,
    fov_y_deg: f32,
) -> (f32, f32, f32, f32, f32, f32) {
    let tw = target_w.max(1) as f32;
    let th = target_h.max(1) as f32;
    let target_aspect = tw / th;
    let window_aspect = if window_h > 0.0 { window_w / window_h } else { target_aspect };
    let fov_y_rad = fov_y_deg.to_radians();

    match scaling_mode {
        ScalingMode::VertMinus => {
            (0.0, 0.0, window_w, window_h, window_aspect, fov_y_rad)
        }
        ScalingMode::HorPlus => {
            let fov_x     = 2.0 * ((fov_y_rad * 0.5).tan() * target_aspect).atan();
            let fov_y_adj = if window_aspect > 0.0 {
                2.0 * ((fov_x * 0.5).tan() / window_aspect).atan()
            } else {
                fov_y_rad
            };
            (0.0, 0.0, window_w, window_h, window_aspect, fov_y_adj)
        }
        ScalingMode::LetterBox => {
            let scale = window_w / tw;
            let vp_h  = (th * scale).min(window_h);
            let y_off = ((window_h - vp_h) * 0.5).max(0.0);
            (0.0, y_off, window_w, vp_h, target_aspect, fov_y_rad)
        }
        ScalingMode::PillarBox => {
            let scale = window_h / th;
            let vp_w  = (tw * scale).min(window_w);
            let x_off = ((window_w - vp_w) * 0.5).max(0.0);
            (x_off, 0.0, vp_w, window_h, target_aspect, fov_y_rad)
        }
        ScalingMode::LetterPillarBox => {
            if window_aspect >= target_aspect {
                let vp_w  = window_h * target_aspect;
                let x_off = ((window_w - vp_w) * 0.5).max(0.0);
                (x_off, 0.0, vp_w, window_h, target_aspect, fov_y_rad)
            } else {
                let vp_h  = window_w / target_aspect;
                let y_off = ((window_h - vp_h) * 0.5).max(0.0);
                (0.0, y_off, window_w, vp_h, target_aspect, fov_y_rad)
            }
        }
        ScalingMode::FullScale => {
            (0.0, 0.0, window_w, window_h, target_aspect, fov_y_rad)
        }
    }
}

/// `CanvasViewportRef::Camera` で参照されるカメラアクターのビューポートサイズを解決する。
///
/// 参照カメラのスケーリングモードを適用し、帯を除いたコンテンツ領域のサイズを返す。
/// 参照先が見つからない場合は `[win_w, win_h]` を返す。
pub(super) fn resolve_camera_viewport_size(
    actors:         &[Actor],
    world:          &World,
    cam_actor_name: &str,
    cam_slot_name:  &str,
    win_w: f32,
    win_h: f32,
) -> [f32; 2] {
    /// DFS でアクター名と指定スロット名の CameraComponent を持つ最初のアクターを返す。
    fn find_camera_component<'a>(
        actors:     &'a [Actor],
        world:      &'a World,
        actor_name: &str,
        slot_name:  &str,
    ) -> Option<&'a CameraComponent> {
        for a in actors {
            if a.name == actor_name {
                if let Some(cam) = a.slots().iter()
                    .find(|s| s.name == slot_name && s.kind == ComponentKind::Camera)
                    .and_then(|s| world.get::<CameraComponent>(s.entity))
                {
                    return Some(cam);
                }
            }
            if let Some(found) = find_camera_component(a.children(), world, actor_name, slot_name) {
                return Some(found);
            }
        }
        None
    }

    let Some(cam) = find_camera_component(actors, world, cam_actor_name, cam_slot_name) else {
        return [win_w, win_h];
    };

    let (_, _, rendered_w, rendered_h, _, _) = compute_game_viewport(
        &cam.scaling_mode,
        win_w, win_h,
        cam.target_width, cam.target_height, cam.fov_y_deg,
    );
    [rendered_w, rendered_h]
}

/// シーン SS モード時に各ルートキャンバスアクターの有効ビューポートサイズを事前解決する。
///
/// `CanvasViewportRef::Camera` を持つルートキャンバスのみマップに追加する。
/// `Window` 参照はデフォルトの `viewport_size` にフォールバックするため不要。
pub(super) fn build_canvas_viewport_map(
    actors:        &[Actor],
    world:         &World,
    wl:            u32,
    win_w:         f32,
    win_h:         f32,
    _game_viewport: Option<(f32, f32, f32, f32)>,
) -> HashMap<Entity, [f32; 2]> {
    let mut map = HashMap::new();
    for actor in actors {
        if actor.world_line != wl { continue; }
        if world.get::<CanvasTransform>(actor.entity).is_none() { continue; }
        for slot in actor.slots() {
            if slot.kind != ComponentKind::Canvas { continue; }
            if let Some(cc) = world.get::<CanvasComponent>(slot.entity) {
                if let CanvasViewportRef::Camera { actor_name, slot_name } = &cc.viewport_ref {
                    let vp = resolve_camera_viewport_size(
                        actors, world, actor_name, slot_name, win_w, win_h,
                    );
                    map.insert(actor.entity, vp);
                }
                break;
            }
        }
    }
    map
}
