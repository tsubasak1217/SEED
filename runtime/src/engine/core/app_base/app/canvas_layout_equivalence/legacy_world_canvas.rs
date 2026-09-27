// ============================================================
//  canvas_layout_equivalence/legacy_world_canvas.rs — 3D ワールドキャンバスの子の旧い走査の写し（テスト専用）
//
//  W2-1b で canvas_collect.rs の walk_3d_canvas_children_id（GPU の ID 描画）と
//  collect_3d_canvas_child_outlines（エディタの枠）をレイアウトの表を読む形へ寄せる前のソースを、
//  そのまま写したもの（名前に legacy_ を付け、draw_ctx.sprite_skin.draw_handle をクロージャにした以外は変えていない）。
//  **このファイルを直さないこと**（旧実装の振る舞いの記録。直すと同値の検査が意味を失う）。
// ============================================================
#![allow(clippy::too_many_arguments)]

use std::sync::Arc;

use crate::engine::components::{
    AspectRatioAxis, CanvasComponent, CanvasTransform, ComponentKind, SkinnedSpriteComponent, SpriteComponent,
};
use crate::engine::core::renderer::sprite_skin::SkinnedSpriteDraw;
use crate::engine::core::renderer::ui_draw_order::UiDrawKind;
use crate::engine::ecs::{Entity, World};
use crate::engine::methods::gizmo_interact::mat4x4_mul;
use crate::engine::structs::objects::Actor;

use super::super::canvas_collect::{
    canvas_node_is_transparent, child_anchor_basis, node_anchor_offset, skip_dfs_subtree, text_id_item_local,
};
use super::super::canvas_text_bounds::TextBoundsMap;

/// 3D Canvas 子アクターを再帰走査してスプライト ID アイテムを収集するヘルパー。
///
/// `parent_world_rs` は canvas_to_world（3D ワールド行列）で、
/// 各子スプライトのモデル行列を 3D ワールド空間で構築する。
/// 出力タプル末尾の `layer` は呼び出し側のキャンバス内レイヤーソートに使用する。
#[allow(clippy::too_many_arguments)]
pub(super) fn legacy_walk_3d_canvas_children_id(
    actors: &[Actor],
    world: &World,
    wl: u32,
    counter: &mut u32,
    parent_canvas_size: Option<[f32; 2]>,
    parent_world_rs: [[f32; 4]; 4],
    parent_cumul_scale: [f32; 2],
    mc_total: u32,
    skin_handle_of: &dyn Fn(Entity) -> Option<Arc<SkinnedSpriteDraw>>,
    // テキストの実測枠（Text スロット entity → ローカル境界矩形）
    text_bounds: &TextBoundsMap,
    // (raw_id, GPU 行列, テクスチャパス, スキンメッシュ, レイヤー, 描画種別)
    // 描画種別は同一レイヤー内の前後関係（スプライト → テキスト）に使う。
    out: &mut Vec<(
        u32,
        [[f32; 4]; 4],
        Option<String>,
        Option<Arc<SkinnedSpriteDraw>>,
        i32,
        UiDrawKind,
    )>,
) {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        let my_dfs = *counter;
        *counter += 1;

        // 非アクティブアクター: ピック対象から外す（DFS 番号は子孫分も進める）
        // 非表示（visible=false）も描画・ピックの対象外にする。
        // 実効判定は「祖先も含めて非表示ならサブツリーごと省く」で、この continue／
        // skip_dfs_subtree がそのままサブツリー全体の伝播になる（actor/visibility.rs の規則）。
        if !actor.active || !actor.visible {
            skip_dfs_subtree(&actor.children, counter);
            continue;
        }

        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();

        // フォルダノードはレイアウト透明（canvas_node_is_transparent）。
        // 自身は何も出力せず、下の else 分岐と同じく親の文脈をそのまま子へ渡す。
        let (next_canvas_size, next_world_rs, next_cumul_scale) =
            if let (false, Some(ct)) = (canvas_node_is_transparent(actor), ct_opt) {
            // スケールモードはこのノード自身の CanvasTransform から読み取る
            let (sm_transform, sm_size, keep_aspect, is_width_axis) = (
                ct.scale_transform,
                ct.scale_size,
                ct.keep_aspect_ratio,
                matches!(ct.aspect_ratio_axis, AspectRatioAxis::Width),
            );
            // アンカーオフセット（collect_sprite_items の 3D Canvas パスと同じロジック）
            // 3D ワールドキャンバス配下は常に「子レベル」（最上位分岐なし）。
            // 2D と同じ共通ヘルパーを使い、基準サイズの規則を 1 か所に保つ。
            let [anchor_off_x, anchor_off_y] =
                node_anchor_offset(parent_canvas_size, ct.anchor, parent_cumul_scale, None, false);

            // 有効位置（スケールモードに応じて親累積スケールを適用する）
            let eff_pos = if sm_transform {
                [
                    ct.position[0] * parent_cumul_scale[0] + anchor_off_x,
                    ct.position[1] * parent_cumul_scale[1] + anchor_off_y,
                ]
            } else {
                [ct.position[0] + anchor_off_x, ct.position[1] + anchor_off_y]
            };
            let eff_ct = CanvasTransform {
                position: eff_pos,
                rotation: ct.rotation,
                scale: ct.scale,
                pivot: ct.pivot,
                anchor: [0.0, 0.0],
                ..ct.clone()
            };

            // 自アクターの CanvasComponent
            let my_canvas = actor
                .slots()
                .iter()
                .filter(|s| s.kind == ComponentKind::Canvas)
                .find_map(|s| world.get::<CanvasComponent>(s.entity));

            // sm_size による拡縮スケール（アスペクト比維持を考慮）
            let size_sc_x = if sm_size {
                if keep_aspect && !is_width_axis {
                    parent_cumul_scale[1]
                } else {
                    parent_cumul_scale[0]
                }
            } else {
                1.0
            };
            let size_sc_y = if sm_size {
                if keep_aspect && is_width_axis {
                    parent_cumul_scale[0]
                } else {
                    parent_cumul_scale[1]
                }
            } else {
                1.0
            };
            let (my_eff_w, my_eff_h) = my_canvas
                .map(|cc| (cc.width * size_sc_x, cc.height * size_sc_y))
                .unwrap_or((1.0, 1.0));

            // 自分のワールド RS 行列（CanvasComponent サイズで to_mat4_sized を使う）
            let self_world_rs = mat4x4_mul(
                parent_world_rs,
                CanvasTransform {
                    scale: [1.0, 1.0],
                    ..eff_ct.clone()
                }
                .to_mat4_sized(my_eff_w, my_eff_h),
            );

            // SpriteComponent を持つアクターを ID アイテムとして追加する。
            // テクスチャなし（単色）は白テクスチャフォールバックで全面選択可能にする。
            // （enabled=false のスプライトは非表示のためピック対象外）
            // 3D ワールド行列 → GPU 列優先モデル行列（Z 成分を含めた 3D フルマトリクス）
            let to_gpu_mat = |sw: [[f32; 4]; 4]| {
                [
                    [sw[0][0], sw[1][0], sw[2][0], 0.0],
                    [sw[0][1], sw[1][1], sw[2][1], 0.0],
                    [sw[0][2], sw[1][2], sw[2][2], 0.0],
                    [sw[0][3], sw[1][3], sw[2][3], 1.0],
                ]
            };
            let mut pushed = false;
            for slot in actor.slots() {
                if slot.kind == ComponentKind::Sprite && slot.enabled {
                    if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                        let ew = sc.width * size_sc_x;
                        let eh = sc.height * size_sc_y;
                        // 3D ワールド空間のスプライト行列（canvas_to_world 込み）
                        let sw = mat4x4_mul(parent_world_rs, eff_ct.to_sprite_mat4(ew, eh));
                        // テクスチャなしは None → 白フォールバック（全面 alpha=1）
                        let tex_path = if sc.texture_path.is_empty() {
                            None
                        } else {
                            Some(sc.texture_path.clone())
                        };
                        // raw_id = canvas_id_offset + dfs_id（canvas_id_offset = mc_total）
                        // layer は呼び出し側のキャンバス内レイヤーソートに使用する
                        out.push((
                            mc_total + my_dfs + 1, to_gpu_mat(sw), tex_path, None,
                            sc.layer, UiDrawKind::Sprite,
                        ));
                        pushed = true;
                        break;
                    }
                }
            }
            // 矩形スプライトが無いアクターのみ、スキンスプライトをピック対象にする
            // （2D キャンバスの ID 収集 collect_canvas_id_items と同じ優先規約）。
            // 変形済み頂点は本フレームの描画収集が既に作っているので、ここでは
            // ハンドルを引くだけ（まだ作られていない＝描画対象でないならピック対象外）。
            if !pushed {
                for slot in actor.slots() {
                    if slot.kind != ComponentKind::SkinnedSprite || !slot.enabled {
                        continue;
                    }
                    let Some(ss) = world.get::<SkinnedSpriteComponent>(slot.entity) else {
                        continue;
                    };
                    let Some(mesh_draw) = skin_handle_of(slot.entity) else {
                        continue;
                    };
                    // メッシュ頂点は既に実寸を持つので、掛けるのは追加スケールのみ
                    let sw = mat4x4_mul(parent_world_rs, eff_ct.to_mesh_mat4(size_sc_x, size_sc_y));
                    let tex_path = if ss.texture_path.is_empty() {
                        None
                    } else {
                        Some(ss.texture_path.clone())
                    };
                    out.push((
                        mc_total + my_dfs + 1,
                        to_gpu_mat(sw),
                        tex_path,
                        Some(mesh_draw),
                        ss.layer,
                        UiDrawKind::Sprite,
                    ));
                    pushed = true;
                    break;
                }
            }
            // スプライト系が無いアクターのみテキストをピック対象にする
            // （2D の collect_canvas_id_items と同一の優先規約・同一の枠形状）。
            if !pushed {
                if let Some(item) = text_id_item_local(actor, world, text_bounds) {
                    // to_mesh_mat4（実寸 px ローカル）→ 枠のユニットクワッド化 の順に掛ける
                    // 枠モードは pivot を枠矩形へ焼き込み済み＝行列側は pivot 無し。
                    let node = if item.zero_pivot {
                        eff_ct.to_mesh_mat4_no_pivot(size_sc_x, size_sc_y)
                    } else {
                        eff_ct.to_mesh_mat4(size_sc_x, size_sc_y)
                    };
                    let sw = mat4x4_mul(mat4x4_mul(parent_world_rs, node), item.local_box_mat);
                    // テクスチャなし = 白フォールバック（枠全面 alpha=1）
                    out.push((
                        mc_total + my_dfs + 1, to_gpu_mat(sw), None, None,
                        item.layer, UiDrawKind::Text,
                    ));
                }
            }

            // 子への基準 Canvas サイズを計算する。
            // スケールモードは各子が自身の CanvasTransform から読み取るため伝播しない。
            let child_anchor_basis_size = child_anchor_basis(my_canvas.map(|cc| [cc.width, cc.height]));
            let child_cumul_scale = if sm_transform {
                [
                    parent_cumul_scale[0] * ct.scale[0],
                    parent_cumul_scale[1] * ct.scale[1],
                ]
            } else {
                [ct.scale[0], ct.scale[1]]
            };
            (child_anchor_basis_size, self_world_rs, child_cumul_scale)
        } else {
            // CanvasTransform なし: 親情報をそのまま引き継ぐ
            (parent_canvas_size, parent_world_rs, parent_cumul_scale)
        };

        // 常に子に再帰する（DFS カウンタを全アクターで維持するため）
        legacy_walk_3d_canvas_children_id(
            &actor.children,
            world,
            wl,
            counter,
            next_canvas_size,
            next_world_rs,
            next_cumul_scale,
            mc_total,
            skin_handle_of,
            text_bounds,
            out,
        );
    }
}

// ============================================================
//  collect_3d_canvas_child_outlines
// ============================================================

/// 3D Canvas（Actor3D + CanvasComponent）配下にネストされた子キャンバス
/// （CanvasComponent を持つ Actor2D）のアウトライン矩形コーナーを 3D ワールド空間で収集する。
///
/// # 目的
/// 3D キャンバスの子キャンバス枠は、ルート 3D キャンバス枠パス（frame_renderer）が
/// 子へ再帰しないため描画されていなかった。本関数はスプライトの子走査
/// （`walk_3d_canvas_children_id` / `collect_sprite_items`）と**同一の変換連鎖**で
/// 各ネストキャンバスの矩形を求めるため、枠が子キャンバスの描画スプライトと一致する。
///
/// # 変換の一致
/// スプライトは `mat4x4_mul(parent_world_rs, eff_ct.to_sprite_mat4(W, H))` をユニット
/// クワッドに適用して配置される。本関数の枠は `eff_ct.to_mat4_sized(W, H)` を
/// `[0,W]×[0,H]` のコーナーに適用する。`to_mat4_sized(W,H)` を `(W*u, H*v)` に適用した
/// 結果は `to_sprite_mat4(W,H)` を `(u,v)` に適用した結果と一致するため、枠 == スプライト。
///
/// # DFS カウンタ
/// `find_actor_by_dfs` の子ノード規則（world_line 無関係に子孫を全カウント）で `counter`
/// を進めるため、返却する `dfs_id` は選択ハイライト（`selected_actor_dfs_ids`）と整合する。
///
/// 出力タプル: `([TL, TR, BR, BL] の 3D ワールド座標, そのノードの dfs_id)`
#[allow(clippy::too_many_arguments)]
pub(super) fn legacy_collect_3d_canvas_child_outlines(
    actors: &[Actor],
    world: &World,
    wl: u32,
    counter: &mut u32,
    parent_canvas_size: Option<[f32; 2]>,
    parent_world_rs: [[f32; 4]; 4],
    parent_cumul_scale: [f32; 2],
    out: &mut Vec<([[f32; 3]; 4], u32)>,
) {
    for actor in actors {
        // このノードの 0 始まり DFS 番号（find_actor_by_dfs の子規則と同一。
        // 子ノードは world_line で除外せず全カウントする）。
        let my_dfs = *counter;
        *counter += 1;

        // 非アクティブアクター: スプライトが描画されないため枠も描かない。
        // DFS 番号は選択系と整合させるため子孫分も進める。
        // 非表示（visible=false）も描画・ピックの対象外にする。
        // 実効判定は「祖先も含めて非表示ならサブツリーごと省く」で、この continue／
        // skip_dfs_subtree がそのままサブツリー全体の伝播になる（actor/visibility.rs の規則）。
        if !actor.active || !actor.visible {
            skip_dfs_subtree(&actor.children, counter);
            continue;
        }

        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();

        // フォルダノードはレイアウト透明（canvas_node_is_transparent）。
        // 自身は何も出力せず、下の else 分岐と同じく親の文脈をそのまま子へ渡す。
        let (next_canvas_size, next_world_rs, next_cumul_scale) =
            if let (false, Some(ct)) = (canvas_node_is_transparent(actor), ct_opt) {
            // スケールモードはこのノード自身の CanvasTransform から読み取る
            let (sm_transform, sm_size, keep_aspect, is_width_axis) = (
                ct.scale_transform,
                ct.scale_size,
                ct.keep_aspect_ratio,
                matches!(ct.aspect_ratio_axis, AspectRatioAxis::Width),
            );
            // アンカーオフセット（walk_3d_canvas_children_id / collect_sprite_items と同じ）
            // 3D ワールドキャンバス配下は常に「子レベル」（最上位分岐なし）。
            // 2D と同じ共通ヘルパーを使い、基準サイズの規則を 1 か所に保つ。
            let [anchor_off_x, anchor_off_y] =
                node_anchor_offset(parent_canvas_size, ct.anchor, parent_cumul_scale, None, false);
            // 有効位置（スケールモードに応じて親累積スケールを適用する）
            let eff_pos = if sm_transform {
                [
                    ct.position[0] * parent_cumul_scale[0] + anchor_off_x,
                    ct.position[1] * parent_cumul_scale[1] + anchor_off_y,
                ]
            } else {
                [ct.position[0] + anchor_off_x, ct.position[1] + anchor_off_y]
            };
            let eff_ct = CanvasTransform {
                position: eff_pos,
                rotation: ct.rotation,
                scale: ct.scale,
                pivot: ct.pivot,
                anchor: [0.0, 0.0],
                ..ct.clone()
            };

            // 自アクターの CanvasComponent
            let my_canvas = actor
                .slots()
                .iter()
                .filter(|s| s.kind == ComponentKind::Canvas)
                .find_map(|s| world.get::<CanvasComponent>(s.entity));

            // sm_size による拡縮スケール（アスペクト比維持を考慮）
            let size_sc_x = if sm_size {
                if keep_aspect && !is_width_axis {
                    parent_cumul_scale[1]
                } else {
                    parent_cumul_scale[0]
                }
            } else {
                1.0
            };
            let size_sc_y = if sm_size {
                if keep_aspect && is_width_axis {
                    parent_cumul_scale[0]
                } else {
                    parent_cumul_scale[1]
                }
            } else {
                1.0
            };
            let (my_eff_w, my_eff_h) = my_canvas
                .map(|cc| (cc.width * size_sc_x, cc.height * size_sc_y))
                .unwrap_or((1.0, 1.0));

            // 子伝播用のワールド RS 行列（scale=[1,1]。スプライト子走査と同一）
            let self_world_rs = mat4x4_mul(
                parent_world_rs,
                CanvasTransform {
                    scale: [1.0, 1.0],
                    ..eff_ct.clone()
                }
                .to_mat4_sized(my_eff_w, my_eff_h),
            );

            // このノードが CanvasComponent を持つなら、その矩形アウトラインを出力する。
            // 枠行列は eff_ct（scale 込み）× to_mat4_sized（2D collect_canvas_rects の枠と同一形）。
            if my_canvas.is_some() {
                let m = mat4x4_mul(parent_world_rs, eff_ct.to_mat4_sized(my_eff_w, my_eff_h));
                let tp = |cx: f32, cy: f32| -> [f32; 3] {
                    [
                        m[0][0] * cx + m[0][1] * cy + m[0][3],
                        m[1][0] * cx + m[1][1] * cy + m[1][3],
                        m[2][0] * cx + m[2][1] * cy + m[2][3],
                    ]
                };
                out.push((
                    [
                        tp(0.0, 0.0),
                        tp(my_eff_w, 0.0),
                        tp(my_eff_w, my_eff_h),
                        tp(0.0, my_eff_h),
                    ],
                    my_dfs,
                ));
            }

            // 子への基準 Canvas サイズと累積スケール（walk_3d_canvas_children_id と同一）
            let child_anchor_basis_size = child_anchor_basis(my_canvas.map(|cc| [cc.width, cc.height]));
            let child_cumul_scale = if sm_transform {
                [
                    parent_cumul_scale[0] * ct.scale[0],
                    parent_cumul_scale[1] * ct.scale[1],
                ]
            } else {
                [ct.scale[0], ct.scale[1]]
            };
            (child_anchor_basis_size, self_world_rs, child_cumul_scale)
        } else {
            // CanvasTransform なし: 親情報をそのまま引き継ぐ
            (parent_canvas_size, parent_world_rs, parent_cumul_scale)
        };

        // 常に子に再帰する（DFS カウンタを全アクターで維持するため）
        legacy_collect_3d_canvas_child_outlines(
            &actor.children,
            world,
            wl,
            counter,
            next_canvas_size,
            next_world_rs,
            next_cumul_scale,
            out,
        );
    }
}
