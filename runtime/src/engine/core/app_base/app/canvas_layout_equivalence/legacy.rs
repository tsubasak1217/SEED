// ============================================================
//  canvas_layout_equivalence/legacy.rs — 旧実装の写し（同値の性質テストの基準。テスト専用）
//
//  W2-1a でレイアウトの計算を canvas_layout/ へ一本化する前の 5 つの走査を、
//  コミット 5fd3f5d1 の時点のソースから**そのまま**写したもの（名前に legacy_ を付けた以外は、
//  GPU を要する部分を次のとおり置き換えただけで、計算の式・順序は変えていない）:
//    - collect_sprite_items … テクスチャ・スキン・テキストの展開は GPU／フォントが要るので、
//                             「各ノードで描画が読んだレイアウトの値」を記録する形（legacy_sprite_trace）にした
//    - collect_canvas_rects … ボーンの可視化の腕だけ外した（テストは常に bone_overlay = None。旧実装も None なら何もしない）
//    - collect_canvas_id_items … draw_ctx.sprite_skin.draw_handle をクロージャにした
//    - walk_pick_candidates_2d / collect_actor2d_contexts … 名前だけ変えた
//  **このファイルを直さないこと**（旧実装の振る舞いの記録。直すと同値の検査が意味を失う）。
// ============================================================
#![allow(clippy::too_many_arguments)]

use std::collections::HashMap;
use std::sync::Arc;

use crate::engine::components::{
    AspectRatioAxis, CanvasComponent, CanvasDrawZone, CanvasTransform, Collider2dComponent,
    ComponentKind, SkinnedSpriteComponent, SpriteComponent, TextComponent,
};
use crate::engine::core::app_base::scene::Scene;
use crate::engine::core::loader::sprite_mesh::SpriteMesh;
use crate::engine::core::renderer::sprite_skin::{build_bone_matrices, SkinnedSpriteDraw};
use crate::engine::core::renderer::ui_draw_order::UiDrawKind;
use crate::engine::ecs::{Entity, World};
use crate::engine::methods::drawer::LineBatch;
use crate::engine::methods::gizmo_interact::mat4x4_mul;
use crate::engine::structs::objects::actor::{Actor, ActorKind};

use super::super::canvas_collect::{
    add_thick_rect, canvas_node_is_transparent, child_anchor_basis, node_anchor_offset,
    skip_dfs_subtree, text_id_item_local, BoneOverlayCtx, CanvasIdItem,
};
use super::super::canvas_text_bounds::TextBoundsMap;
use super::super::physics2d_ops::Actor2dPhysicsCtx;
use super::super::pick_2d::{
    hit_test_local_box_2d, hit_test_mesh_2d, hit_test_rect_2d, PickCand2d, PickFilter2d, PickKind2d,
};

/// 旧 canvas_collect.rs の非選択キャンバス枠のリング数（写し）。
const OUTLINE_RINGS_THIN: u32 = 1;
/// 旧 canvas_collect.rs の選択枠のリング数（写し）。
const OUTLINE_RINGS_THICK: u32 = 5;
/// 旧 canvas_collect.rs のルートキャンバス枠の色（写し）。
const ROOT_CANVAS_OUTLINE_COL: [f32; 4] = [0.0, 1.0, 0.0, 1.0];

/// 旧 collect_sprite_items が「描画アイテムを積む時点」で使っていたレイアウトの値（1 ノードぶん）。
#[derive(Clone, Debug, PartialEq)]
pub(super) enum SpriteTraceEntry {
    /// フォルダ: SEED.Draw の座標空間として親の行列とゾーンを登録した。
    Folder {
        entity: Entity,
        parent_world_rs: [[f32; 4]; 4],
        zone: CanvasDrawZone,
    },
    /// CanvasTransform を持つノード: スプライト・テキスト・パーティクル・座標空間を積んだ。
    Placed {
        entity: Entity,
        parent_world_rs: [[f32; 4]; 4],
        eff_ct: CanvasTransform,
        size_scale: [f32; 2],
        zone: CanvasDrawZone,
    },
}

/// 旧 collect_sprite_items の走査と計算の写し（描画アイテムの代わりにレイアウトの値を記録する）。
///
/// 旧実装の引数のうち draw_ctx・canvas_scale・y_sign・出力先は描画アイテムの組み立てにしか使わない
/// （レイアウトの値に影響しない）ので省いた。計算の式と順序は旧実装のまま。
pub(super) fn legacy_sprite_trace(
    actors: &[Actor],
    world: &World,
    wl: u32,
    parent_canvas_size: Option<[f32; 2]>,
    parent_world_rs: [[f32; 4]; 4],
    parent_cumul_scale: [f32; 2],
    viewport_size: Option<[f32; 2]>,
    canvas_viewport_overrides: &HashMap<Entity, [f32; 2]>,
    root_auto_sizes: &HashMap<Entity, [f32; 2]>,
    parent_zone: CanvasDrawZone,
    design_space: bool,
    out: &mut Vec<SpriteTraceEntry>,
) {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        if !actor.active || !actor.visible {
            continue;
        }
        if canvas_node_is_transparent(actor) {
            out.push(SpriteTraceEntry::Folder {
                entity: actor.entity,
                parent_world_rs,
                zone: parent_zone,
            });
            legacy_sprite_trace(
                &actor.children,
                world,
                wl,
                parent_canvas_size,
                parent_world_rs,
                parent_cumul_scale,
                viewport_size,
                canvas_viewport_overrides,
                root_auto_sizes,
                parent_zone,
                design_space,
                out,
            );
            continue;
        }
        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
        if let Some(ct) = ct_opt {
            let root_auto = if parent_canvas_size.is_none() {
                root_auto_sizes.get(&actor.entity).copied()
            } else {
                None
            };
            let ct = if root_auto.is_some() {
                CanvasTransform::default()
            } else {
                ct
            };
            let (sm_transform, sm_size, keep_aspect, is_width_axis) = (
                ct.scale_transform,
                ct.scale_size,
                ct.keep_aspect_ratio,
                matches!(ct.aspect_ratio_axis, AspectRatioAxis::Width),
            );
            let eff_viewport = if parent_canvas_size.is_none() {
                canvas_viewport_overrides
                    .get(&actor.entity)
                    .copied()
                    .or(viewport_size)
            } else {
                viewport_size
            };
            let [anchor_off_x, anchor_off_y] = node_anchor_offset(
                parent_canvas_size,
                ct.anchor,
                parent_cumul_scale,
                eff_viewport,
                design_space,
            );
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
            let my_canvas = actor
                .slots()
                .iter()
                .filter(|s| s.kind == ComponentKind::Canvas)
                .find_map(|s| world.get::<CanvasComponent>(s.entity));
            let my_zone = if parent_canvas_size.is_none() {
                my_canvas.map(|cc| cc.draw_zone).unwrap_or(parent_zone)
            } else {
                parent_zone
            };
            let size_scale_x = if sm_size {
                if keep_aspect && !is_width_axis {
                    parent_cumul_scale[1]
                } else {
                    parent_cumul_scale[0]
                }
            } else {
                1.0
            };
            let size_scale_y = if sm_size {
                if keep_aspect && is_width_axis {
                    parent_cumul_scale[0]
                } else {
                    parent_cumul_scale[1]
                }
            } else {
                1.0
            };
            let (my_eff_w, my_eff_h) = my_canvas
                .map(|cc| {
                    let [bw, bh] = root_auto.unwrap_or([cc.width, cc.height]);
                    (bw * size_scale_x, bh * size_scale_y)
                })
                .unwrap_or((1.0, 1.0));
            let self_world_rs = mat4x4_mul(
                parent_world_rs,
                CanvasTransform {
                    scale: [1.0, 1.0],
                    ..eff_ct.clone()
                }
                .to_mat4_sized(my_eff_w, my_eff_h),
            );
            // ── ここで旧実装はスプライト・スキン・テキスト・パーティクル・座標空間を積んでいた ──
            out.push(SpriteTraceEntry::Placed {
                entity: actor.entity,
                parent_world_rs,
                eff_ct: eff_ct.clone(),
                size_scale: [size_scale_x, size_scale_y],
                zone: my_zone,
            });
            let child_info =
                my_canvas.map(|cc| (root_auto.unwrap_or([cc.width, cc.height]), cc.auto_scale));
            let child_anchor_basis_size = child_anchor_basis(child_info.map(|(sz, _)| sz));
            let auto_scale_factor = if parent_canvas_size.is_none() {
                if let (Some([vw, vh]), Some((_, true))) = (eff_viewport, child_info) {
                    [vw / my_eff_w, vh / my_eff_h]
                } else {
                    [1.0f32, 1.0]
                }
            } else {
                [1.0f32, 1.0]
            };
            let child_cumul_scale = if sm_transform {
                [
                    parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
                    parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1],
                ]
            } else {
                [
                    ct.scale[0] * auto_scale_factor[0],
                    ct.scale[1] * auto_scale_factor[1],
                ]
            };
            legacy_sprite_trace(
                &actor.children,
                world,
                wl,
                child_anchor_basis_size,
                self_world_rs,
                child_cumul_scale,
                viewport_size,
                canvas_viewport_overrides,
                root_auto_sizes,
                my_zone,
                design_space,
                out,
            );
        }
    }
}

pub(super) fn legacy_collect_canvas_rects(
    actors: &[Actor],
    world: &World,
    wl: u32,
    lb: &mut LineBatch,
    // キャンバスアウトラインの色 [r, g, b, a]
    col: [f32; 4],
    // 現在選択中のアクター DFS ID リスト（Sprite アウトラインの描画判定に使う）
    selected_dfs_ids: &[usize],
    counter: &mut u32,
    parent_canvas_size: Option<[f32; 2]>,
    parent_world_rs: [[f32; 4]; 4],
    parent_cumul_scale: [f32; 2],
    canvas_scale: f32,
    y_sign: f32,
    viewport_size: Option<[f32; 2]>,
    canvas_viewport_overrides: &HashMap<Entity, [f32; 2]>,
    // ビューポート・ルートキャンバスの自動解像度マップ（collect_sprite_items と同じ扱い）
    root_auto_sizes: &HashMap<Entity, [f32; 2]>,
    // ビューポートタブの設計空間表示中か（= edit_view_is_2d。collect_sprite_items と同じ扱い）
    design_space: bool,
    // アウトラインのリング間隔（描画空間の単位）。
    // SS 表示では「画面 1px 相当」を渡すことで、ズームに依らず連続した太線に見える。
    outline_step: f32,
    // スキンスプライトのボーン可視化（Phase A2）。None = 描かない
    // （Play 中・ビューポートオプションで OFF・メッシュローダが無い場合）。
    bone_overlay: Option<&BoneOverlayCtx<'_>>,
    // テキストの実測枠（Text スロット entity → ローカル境界矩形）。
    // ピック（pick_2d）と同一の表を渡すことで、選択枠とクリック判定が必ず一致する。
    text_bounds: &TextBoundsMap,
) {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        let my_dfs = *counter as usize;
        *counter += 1;

        // 非アクティブアクター: 枠線を描画しない（DFS 番号は子孫分も進める）
        // 非表示（visible=false）も描画・ピックの対象外にする。
        // 実効判定は「祖先も含めて非表示ならサブツリーごと省く」で、この continue／
        // skip_dfs_subtree がそのままサブツリー全体の伝播になる（actor/visibility.rs の規則）。
        if !actor.active || !actor.visible {
            skip_dfs_subtree(&actor.children, counter);
            continue;
        }

        // フォルダノード: レイアウト透明（canvas_node_is_transparent）。
        // 枠は一切描かず、子へは親の文脈をそのまま渡して再帰する。
        // DFS 番号は上で自分の 1 つぶんだけ消費済み（子孫は再帰側で消費する）。
        if canvas_node_is_transparent(actor) {
            legacy_collect_canvas_rects(
                &actor.children,
                world,
                wl,
                lb,
                col,
                selected_dfs_ids,
                counter,
                parent_canvas_size,
                parent_world_rs,
                parent_cumul_scale,
                canvas_scale,
                y_sign,
                viewport_size,
                canvas_viewport_overrides,
                root_auto_sizes,
                design_space,
                outline_step,
                bone_overlay,
                text_bounds,
            );
            continue;
        }

        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
        if let Some(ct) = ct_opt {
            // ビューポート・ルートキャンバス: 自動解像度上書き + Transform 恒等化（Phase B）
            let root_auto = if parent_canvas_size.is_none() {
                root_auto_sizes.get(&actor.entity).copied()
            } else {
                None
            };
            let ct = if root_auto.is_some() {
                CanvasTransform::default()
            } else {
                ct
            };
            // スケールモードはこのノード自身の CanvasTransform から読み取る
            let (sm_transform, sm_size, keep_aspect, is_width_axis) = (
                ct.scale_transform,
                ct.scale_size,
                ct.keep_aspect_ratio,
                matches!(ct.aspect_ratio_axis, AspectRatioAxis::Width),
            );
            // アンカーオフセット計算（collect_sprite_items と同じロジック）
            // Camera 参照のルートキャンバスはオーバーライドマップの値を優先する
            let eff_viewport = if parent_canvas_size.is_none() {
                canvas_viewport_overrides
                    .get(&actor.entity)
                    .copied()
                    .or(viewport_size)
            } else {
                viewport_size
            };
            // アンカーオフセット（最上位＝ビューポート基準／子＝親キャンバス基準）は
            // 描画・枠・ピック・物理で共有する node_anchor_offset に一本化する。
            let [anchor_off_x, anchor_off_y] = node_anchor_offset(
                parent_canvas_size,
                ct.anchor,
                parent_cumul_scale,
                eff_viewport,
                design_space,
            );

            // 有効位置（スケールモードに応じて）
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

            // pivot はノーマライズ値のため実際のキャンバスサイズで補正する
            let my_canvas_r = actor
                .slots()
                .iter()
                .filter(|s| s.kind == ComponentKind::Canvas)
                .find_map(|s| world.get::<CanvasComponent>(s.entity));
            // アスペクト比維持を考慮したスケール係数
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
            // 自動解像度上書きがあればそれを基準サイズとする（なければ保存値）
            let (my_eff_w_r, my_eff_h_r) = my_canvas_r
                .map(|cc| {
                    let [bw, bh] = root_auto.unwrap_or([cc.width, cc.height]);
                    (bw * size_sc_x, bh * size_sc_y)
                })
                .unwrap_or((1.0, 1.0));

            let self_world_rs = mat4x4_mul(
                parent_world_rs,
                CanvasTransform {
                    scale: [1.0, 1.0],
                    ..eff_ct.clone()
                }
                .to_mat4_sized(my_eff_w_r, my_eff_h_r),
            );

            for slot in actor.slots() {
                match slot.kind {
                    ComponentKind::Canvas => {
                        // CanvasComponent: キャンバス領域のアウトラインを常に描画する。
                        // 色:   選択中=オレンジ（選択色は全アウトライン共通） /
                        //       ルート(基準)キャンバス=緑 / それ以外=通常色。
                        // 太さ: 非選択=一本線、選択中のみ太線（約 5 倍）で強調する。
                        if let Some(cc) = world.get::<CanvasComponent>(slot.entity) {
                            const SELECTED_COL: [f32; 4] = [1.0, 0.5, 0.05, 1.0];
                            let is_selected = selected_dfs_ids.contains(&my_dfs);
                            let is_root = parent_canvas_size.is_none();
                            let draw_col = if is_selected {
                                SELECTED_COL
                            } else if is_root {
                                ROOT_CANVAS_OUTLINE_COL
                            } else {
                                col
                            };
                            let rings = if is_selected {
                                OUTLINE_RINGS_THICK
                            } else {
                                OUTLINE_RINGS_THIN
                            };
                            // アウトラインも自動解像度上書きを反映する
                            let [base_w, base_h] = root_auto.unwrap_or([cc.width, cc.height]);
                            let eff_w = base_w * size_sc_x;
                            let eff_h = base_h * size_sc_y;
                            let m = mat4x4_mul(parent_world_rs, eff_ct.to_mat4_sized(eff_w, eff_h));
                            let csy = canvas_scale * y_sign;
                            let tp = |lx: f32, ly: f32| -> [f32; 3] {
                                [
                                    (m[0][0] * lx + m[0][1] * ly + m[0][3]) * canvas_scale,
                                    (m[1][0] * lx + m[1][1] * ly + m[1][3]) * csy,
                                    0.0f32,
                                ]
                            };
                            add_thick_rect(
                                lb,
                                [
                                    tp(0.0, 0.0),
                                    tp(eff_w, 0.0),
                                    tp(eff_w, eff_h),
                                    tp(0.0, eff_h),
                                ],
                                draw_col,
                                rings,
                                outline_step,
                            );
                        }
                    }
                    ComponentKind::Sprite => {
                        // SpriteComponent: 選択時のみアウトラインを太線で描画する。
                        // 選択色はキャンバス枠と共通のオレンジに統一する。
                        if selected_dfs_ids.contains(&my_dfs) {
                            if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                                let eff_w = sc.width * size_sc_x;
                                let eff_h = sc.height * size_sc_y;
                                const SPRITE_OUTLINE_COL: [f32; 4] = [1.0, 0.5, 0.05, 1.0];
                                let m = mat4x4_mul(
                                    parent_world_rs,
                                    eff_ct.to_sprite_mat4(eff_w, eff_h),
                                );
                                let csy2 = canvas_scale * y_sign;
                                let tp = |lx: f32, ly: f32| -> [f32; 3] {
                                    [
                                        (m[0][0] * lx + m[0][1] * ly + m[0][3]) * canvas_scale,
                                        (m[1][0] * lx + m[1][1] * ly + m[1][3]) * csy2,
                                        0.0f32,
                                    ]
                                };
                                add_thick_rect(
                                    lb,
                                    [tp(0.0, 0.0), tp(1.0, 0.0), tp(1.0, 1.0), tp(0.0, 1.0)],
                                    SPRITE_OUTLINE_COL,
                                    OUTLINE_RINGS_THICK,
                                    outline_step,
                                );
                            }
                        }
                    }
                    ComponentKind::SkinnedSprite => {
                        // 同値テスト用の写し: ボーンの可視化（bone_overlay）は GPU 側のメッシュが要るので扱わない
                        // （テストは常に None を渡す。旧実装も None なら何もしない）。
                        let _ = bone_overlay;
                    }
                    ComponentKind::Text => {
                        // TextComponent: 選択時のみ、実測したブロック枠を太線で描画する。
                        // 判定・描画の形状はピック（pick_2d の Text ヒット）と同じ表を使うので、
                        // 「枠は出ているのにクリックできない」というズレが起きない。
                        if !selected_dfs_ids.contains(&my_dfs) {
                            continue;
                        }
                        let Some(bx) = text_bounds.get(&slot.entity) else {
                            continue;
                        };
                        // 選択色はキャンバス枠・スプライト枠と共通のオレンジ
                        const TEXT_OUTLINE_COL: [f32; 4] = [1.0, 0.5, 0.05, 1.0];
                        // グリフは実寸 px で組まれるため、スプライトではなくメッシュ用
                        // 変換連鎖（to_mesh_mat4）を使う（描画・ピックと同一）。
                        // 枠モードは pivot を矩形側へ焼き込み済みなので行列は pivot 無し。
                        let m = mat4x4_mul(
                            parent_world_rs,
                            if bx.zero_pivot {
                                eff_ct.to_mesh_mat4_no_pivot(size_sc_x, size_sc_y)
                            } else {
                                eff_ct.to_mesh_mat4(size_sc_x, size_sc_y)
                            },
                        );
                        let csy_t = canvas_scale * y_sign;
                        let tp = |lx: f32, ly: f32| -> [f32; 3] {
                            [
                                (m[0][0] * lx + m[0][1] * ly + m[0][3]) * canvas_scale,
                                (m[1][0] * lx + m[1][1] * ly + m[1][3]) * csy_t,
                                0.0f32,
                            ]
                        };
                        add_thick_rect(
                            lb,
                            [
                                tp(bx.local.min[0], bx.local.min[1]),
                                tp(bx.local.max[0], bx.local.min[1]),
                                tp(bx.local.max[0], bx.local.max[1]),
                                tp(bx.local.min[0], bx.local.max[1]),
                            ],
                            TEXT_OUTLINE_COL,
                            OUTLINE_RINGS_THICK,
                            outline_step,
                        );
                    }
                    _ => {}
                }
            }

            // 子への継承情報を構築する（子のアンカー基準サイズにも自動解像度上書きを反映）。
            // スケールモードは各子が自身の CanvasTransform から読み取るため伝播しない。
            let child_info =
                my_canvas_r.map(|cc| (root_auto.unwrap_or([cc.width, cc.height]), cc.auto_scale));
            let child_anchor_basis_size = child_anchor_basis(child_info.map(|(sz, _)| sz));
            let auto_scale_factor = if parent_canvas_size.is_none() {
                if let (Some([vw, vh]), Some((_, true))) = (eff_viewport, child_info) {
                    [vw / my_eff_w_r, vh / my_eff_h_r]
                } else {
                    [1.0f32, 1.0]
                }
            } else {
                [1.0f32, 1.0]
            };
            let child_cumul_scale = if sm_transform {
                [
                    parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
                    parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1],
                ]
            } else {
                [
                    ct.scale[0] * auto_scale_factor[0],
                    ct.scale[1] * auto_scale_factor[1],
                ]
            };
            legacy_collect_canvas_rects(
                &actor.children,
                world,
                wl,
                lb,
                col,
                selected_dfs_ids,
                counter,
                child_anchor_basis_size,
                self_world_rs,
                child_cumul_scale,
                canvas_scale,
                y_sign,
                viewport_size,
                canvas_viewport_overrides,
                root_auto_sizes,
                design_space,
                outline_step,
                bone_overlay,
                text_bounds,
            );
        } else {
            // CanvasTransform なし（Actor3D 等）: 枠描画対象外だが、DFS 番号は
            // find_actor_by_dfs と同じ規則（子孫も含めて全カウント）で消費する。
            // 消費しないと以降の DFS ID がズレて選択ハイライトが別枠に付く。
            skip_dfs_subtree(&actor.children, counter);
        }
    }
}

pub(super) fn legacy_collect_canvas_id_items(
    actors: &[Actor],
    world: &World,
    wl: u32,
    counter: &mut u32,
    parent_canvas_size: Option<[f32; 2]>,
    parent_world_rs: [[f32; 4]; 4],
    parent_cumul_scale: [f32; 2],
    canvas_scale: f32,
    y_sign: f32,
    viewport_size: Option<[f32; 2]>,
    canvas_viewport_overrides: &HashMap<Entity, [f32; 2]>,
    // ビューポート・ルートキャンバスの自動解像度マップ（collect_sprite_items と同じ扱い）
    root_auto_sizes: &HashMap<Entity, [f32; 2]>,
    // 3D MC インスタンスの総数（canvas_id の raw_id オフセット計算に使用）
    mc_total: u32,
    // 親（ルートキャンバス）から継承する描画ゾーン（collect_sprite_items と同じ扱い）
    parent_zone: CanvasDrawZone,
    // スクリーンスペースのサブツリー内かどうか。
    // CanvasTransform を持たないアクター（Actor3D。3D ワールドキャンバス等）を通過した時点で
    // false になり、それ以降の子孫は DFS カウントのみ行い ID quad を出力しない。
    // 3D ワールドキャンバス配下のスプライトは WS 用の collect_3d_canvas_child_id_items が
    // 担当するため、ここで出力すると SS 座標の誤った ID quad が重なり誤選択の原因になる。
    in_ss_subtree: bool,
    // ビューポートタブの設計空間表示中か（= edit_view_is_2d。collect_sprite_items と同じ扱い）
    design_space: bool,
    // スキンスプライトの変形済み頂点を引くための描画コンテキスト。
    // 本フレームのスプライト収集（collect_sprite_items）が既にディスパッチ済みの
    // 変形結果をそのまま使う（ID パス用に再変形はしない）。
    // 同値テスト用の写し: 旧実装の draw_ctx.sprite_skin.draw_handle をクロージャへ置き換えた（GPU 不要）
    skin_handle_of: &dyn Fn(Entity) -> Option<Arc<SkinnedSpriteDraw>>,
    // テキストの実測枠（Text スロット entity → ローカル境界矩形）。
    // 選択枠（collect_canvas_rects）・CPU ピック（pick_2d）と同一の表を渡すことで、
    // 「枠は出るのにクリックできない」というズレを構造的に防ぐ。
    // 表に無い（＝実測できなかった）Text はピック対象にしない。
    text_bounds: &TextBoundsMap,
    out: &mut Vec<CanvasIdItem>,
) {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        let my_dfs = *counter;
        *counter += 1;

        // 非アクティブアクター: 描画されないためピック（ID）対象からも外す。
        // DFS 番号は選択系と整合させるため子孫分も含めて進める。
        // 非表示（visible=false）も描画・ピックの対象外にする。
        // 実効判定は「祖先も含めて非表示ならサブツリーごと省く」で、この continue／
        // skip_dfs_subtree がそのままサブツリー全体の伝播になる（actor/visibility.rs の規則）。
        if !actor.active || !actor.visible {
            skip_dfs_subtree(&actor.children, counter);
            continue;
        }

        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
        // フォルダノードはレイアウト透明（canvas_node_is_transparent）。
        // ID quad を出力せず、親の文脈（canvas_size / cumul_scale / world_rs / zone）を
        // そのまま子へ素通しする（下の else 分岐がそのまま該当する）。
        let is_transparent = canvas_node_is_transparent(actor);
        // CanvasTransform を持たないアクター配下は SS サブツリー外として扱う。
        // フォルダは常に素通しなので、親の in_ss をそのまま維持する。
        let next_in_ss = in_ss_subtree && (is_transparent || ct_opt.is_some());
        let (next_canvas_size, next_cumul_scale, next_world_rs, next_zone) =
            if let (true, Some(ct)) = (in_ss_subtree && !is_transparent, ct_opt) {
                // ビューポート・ルートキャンバス: 自動解像度上書き + Transform 恒等化（Phase B）
                let root_auto = if parent_canvas_size.is_none() {
                    root_auto_sizes.get(&actor.entity).copied()
                } else {
                    None
                };
                let ct = if root_auto.is_some() {
                    CanvasTransform::default()
                } else {
                    ct
                };
                // スケールモードはこのノード自身の CanvasTransform から読み取る
                let (sm_transform, sm_size, keep_aspect, is_width_axis) = (
                    ct.scale_transform,
                    ct.scale_size,
                    ct.keep_aspect_ratio,
                    matches!(ct.aspect_ratio_axis, AspectRatioAxis::Width),
                );
                // アンカーオフセット（collect_sprite_items と同じロジック）
                // Camera 参照のルートキャンバスはオーバーライドマップの値を優先する
                let eff_viewport = if parent_canvas_size.is_none() {
                    canvas_viewport_overrides
                        .get(&actor.entity)
                        .copied()
                        .or(viewport_size)
                } else {
                    viewport_size
                };
                // アンカーオフセット（最上位＝ビューポート基準／子＝親キャンバス基準）は
                // 描画・枠・ピック・物理で共有する node_anchor_offset に一本化する。
                let [anchor_off_x, anchor_off_y] = node_anchor_offset(
                    parent_canvas_size,
                    ct.anchor,
                    parent_cumul_scale,
                    eff_viewport,
                    design_space,
                );
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
                // 描画ゾーンの決定（collect_sprite_items と同じロジック）:
                // ルートレベルのキャンバスは自身の draw_zone、それ以外は親から継承する。
                let my_zone = if parent_canvas_size.is_none() {
                    my_canvas.map(|cc| cc.draw_zone).unwrap_or(parent_zone)
                } else {
                    parent_zone
                };
                // アスペクト比維持を考慮したスケール係数
                let id_sc_x = if sm_size {
                    if keep_aspect && !is_width_axis {
                        parent_cumul_scale[1]
                    } else {
                        parent_cumul_scale[0]
                    }
                } else {
                    1.0
                };
                let id_sc_y = if sm_size {
                    if keep_aspect && is_width_axis {
                        parent_cumul_scale[0]
                    } else {
                        parent_cumul_scale[1]
                    }
                } else {
                    1.0
                };
                // 自動解像度上書きがあればそれを基準サイズとする（なければ保存値）
                let (my_eff_w, my_eff_h) = my_canvas
                    .map(|cc| {
                        let [bw, bh] = root_auto.unwrap_or([cc.width, cc.height]);
                        (bw * id_sc_x, bh * id_sc_y)
                    })
                    .unwrap_or((1.0, 1.0));

                // 子への親ワールド RS 行列
                let self_world_rs = mat4x4_mul(
                    parent_world_rs,
                    CanvasTransform {
                        scale: [1.0, 1.0],
                        ..eff_ct.clone()
                    }
                    .to_mat4_sized(my_eff_w, my_eff_h),
                );

                // ID quad 用 GPU 行列の構築
                // SpriteComponent を持つアクターをピッキング対象にする。
                // テクスチャなし（単色）は白テクスチャフォールバックを使用して全面選択可能にする。
                let csy = canvas_scale * y_sign;
                // (行列, テクスチャパス, レイヤー, スキンメッシュ, 描画種別)
                let mut gpu_mat_and_path: Option<(
                    [[f32; 4]; 4],
                    Option<String>,
                    i32,
                    Option<Arc<SkinnedSpriteDraw>>,
                    UiDrawKind,
                )> = None;
                // スキンスプライト（`.sprite_mesh`）: 変形済み頂点でメッシュ形状のまま
                // ID を書く。矩形スプライトより先に走査するのではなく**後**に見るため、
                // 同一アクターに両方あるときは従来どおり SpriteComponent が優先される。
                for slot in actor.slots() {
                    // enabled=false のスプライトは非表示のためピック対象からも外す
                    if slot.kind == ComponentKind::Sprite && slot.enabled {
                        if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                            let ew = sc.width * id_sc_x;
                            let eh = sc.height * id_sc_y;
                            let sw = mat4x4_mul(parent_world_rs, eff_ct.to_sprite_mat4(ew, eh));
                            // テクスチャなしは None → 白フォールバック（全面 alpha=1）
                            let tex_path = if sc.texture_path.is_empty() {
                                None
                            } else {
                                Some(sc.texture_path.clone())
                            };
                            gpu_mat_and_path = Some((
                                [
                                    [sw[0][0] * canvas_scale, sw[1][0] * csy, 0.0, 0.0],
                                    [sw[0][1] * canvas_scale, sw[1][1] * csy, 0.0, 0.0],
                                    [0.0, 0.0, 1.0, 0.0],
                                    [sw[0][3] * canvas_scale, sw[1][3] * csy, 0.0, 1.0],
                                ],
                                tex_path,
                                sc.layer,
                                None,
                                UiDrawKind::Sprite,
                            ));
                            break;
                        }
                    }
                }
                // 矩形スプライトが無いアクターのみ、スキンスプライトをピック対象にする。
                if gpu_mat_and_path.is_none() {
                    for slot in actor.slots() {
                        if slot.kind != ComponentKind::SkinnedSprite || !slot.enabled {
                            continue;
                        }
                        let Some(ss) = world.get::<SkinnedSpriteComponent>(slot.entity) else {
                            continue;
                        };
                        // 本フレームの描画収集で変形済みの頂点バッファを引く。
                        // まだ変形されていない（＝描画対象でない）場合はピック対象外。
                        let Some(mesh_draw) = skin_handle_of(slot.entity) else {
                            continue;
                        };
                        let mw = mat4x4_mul(parent_world_rs, eff_ct.to_mesh_mat4(id_sc_x, id_sc_y));
                        let tex_path = if ss.texture_path.is_empty() {
                            None
                        } else {
                            Some(ss.texture_path.clone())
                        };
                        gpu_mat_and_path = Some((
                            [
                                [mw[0][0] * canvas_scale, mw[1][0] * csy, 0.0, 0.0],
                                [mw[0][1] * canvas_scale, mw[1][1] * csy, 0.0, 0.0],
                                [0.0, 0.0, 1.0, 0.0],
                                [mw[0][3] * canvas_scale, mw[1][3] * csy, 0.0, 1.0],
                            ],
                            tex_path,
                            ss.layer,
                            Some(mesh_draw),
                            UiDrawKind::Sprite,
                        ));
                        break;
                    }
                }
                // スプライト系が無いアクターのみ、テキストをピック対象にする。
                // 形状は実測ブロック枠（text_bounds）そのもの。テキスト描画と同じ
                // to_mesh_mat4 チェーンに `translate(min)·scale(size)` を掛けて
                // ユニットクワッドを枠へ一致させる（tex_path=None → 白フォールバックで
                // 枠全面が alpha=1 ＝ 文字の隙間でも掴める）。
                if gpu_mat_and_path.is_none() {
                    if let Some(item) = text_id_item_local(actor, world, text_bounds) {
                        // 枠モードは pivot を枠矩形へ焼き込み済み＝行列側は pivot 無し。
                        let node = if item.zero_pivot {
                            eff_ct.to_mesh_mat4_no_pivot(id_sc_x, id_sc_y)
                        } else {
                            eff_ct.to_mesh_mat4(id_sc_x, id_sc_y)
                        };
                        let layer = item.layer;
                        let tw = mat4x4_mul(
                            mat4x4_mul(parent_world_rs, node),
                            item.local_box_mat,
                        );
                        gpu_mat_and_path = Some((
                            [
                                [tw[0][0] * canvas_scale, tw[1][0] * csy, 0.0, 0.0],
                                [tw[0][1] * canvas_scale, tw[1][1] * csy, 0.0, 0.0],
                                [0.0, 0.0, 1.0, 0.0],
                                [tw[0][3] * canvas_scale, tw[1][3] * csy, 0.0, 1.0],
                            ],
                            // テクスチャなし = 白フォールバック（枠全面 alpha=1）で
                            // 文字と文字の隙間もクリックできる
                            None,
                            layer,
                            None,
                            UiDrawKind::Text,
                        ));
                    }
                }

                if let Some((gpu_mat, tex_path, layer, mesh, kind)) = gpu_mat_and_path {
                    // raw_id = mc_total + my_dfs + 1
                    // （0 = 背景、1..mc_total = 3D MC インスタンス）
                    // 描画ゾーン・レイヤーは呼び出し側の描画順ソートに使用する
                    out.push(CanvasIdItem {
                        raw_id: mc_total + my_dfs + 1,
                        model: gpu_mat,
                        tex_path,
                        mesh,
                        zone: my_zone,
                        layer,
                        kind,
                        // W2-1b で型に足した欄（旧実装は切り抜きを見ないので常に None。同値の比較には含めない）
                        clip: None,
                    });
                }

                // 子への継承情報を計算する（collect_sprite_items と同じロジック。
                // 子のアンカー基準サイズにも自動解像度上書きを反映する）。
                // スケールモードは各子が自身の CanvasTransform から読み取るため伝播しない。
                let child_info =
                    my_canvas.map(|cc| (root_auto.unwrap_or([cc.width, cc.height]), cc.auto_scale));
                let child_anchor_basis_size = child_anchor_basis(child_info.map(|(sz, _)| sz));
                let auto_scale_factor = if parent_canvas_size.is_none() {
                    if let (Some([vw, vh]), Some((_, true))) = (eff_viewport, child_info) {
                        [vw / my_eff_w, vh / my_eff_h]
                    } else {
                        [1.0f32, 1.0]
                    }
                } else {
                    [1.0f32, 1.0]
                };
                let child_cumul_scale = if sm_transform {
                    [
                        parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
                        parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1],
                    ]
                } else {
                    [
                        ct.scale[0] * auto_scale_factor[0],
                        ct.scale[1] * auto_scale_factor[1],
                    ]
                };
                (child_anchor_basis_size, child_cumul_scale, self_world_rs, my_zone)
            } else {
                // CanvasTransform なし・または SS サブツリー外:
                // ID quad は出力せず、子は親の情報をそのまま引き継ぐ（DFS カウントのみ）
                (
                    parent_canvas_size,
                    parent_cumul_scale,
                    parent_world_rs,
                    parent_zone,
                )
            };

        // 常に子に再帰する（DFS カウンタを全アクターで管理するため）
        legacy_collect_canvas_id_items(
            &actor.children,
            world,
            wl,
            counter,
            next_canvas_size,
            next_world_rs,
            next_cumul_scale,
            canvas_scale,
            y_sign,
            viewport_size,
            canvas_viewport_overrides,
            root_auto_sizes,
            mc_total,
            next_zone,
            next_in_ss,
            design_space,
            skin_handle_of,
            text_bounds,
            out,
        );
    }
}

pub(super) fn legacy_walk_pick_candidates_2d(
    actors: &[Actor],
    world: &World,
    wl: u32,
    canvas_x: f32,
    canvas_y: f32,
    counter: &mut u32,
    parent_world_rs: [[f32; 4]; 4],
    parent_cumul_scale: [f32; 2],
    parent_canvas_size: Option<[f32; 2]>,
    depth: u32,
    parent_zone: CanvasDrawZone,
    viewport_size: Option<[f32; 2]>,
    overrides: &HashMap<Entity, [f32; 2]>,
    root_auto_sizes: &HashMap<Entity, [f32; 2]>,
    design_space: bool,
    // `.sprite_mesh` を CPU キャッシュから引くローダ（スキンスプライトの三角形判定用）
    mesh_of: &dyn Fn(&str) -> Option<std::sync::Arc<SpriteMesh>>,
    // テキストの実測枠（Text スロット entity → ローカル境界矩形）。
    // 空の表を渡せばテキストはヒット対象から外れる（フォント未初期化時など）。
    text_boxes: &TextBoundsMap,
    // 候補の絞り込み条件（エディタ選択 / ポインタイベントで切り替える）
    filter: PickFilter2d,
    out: &mut Vec<PickCand2d>,
) {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        let my_dfs = *counter as usize;
        *counter += 1;

        // 実効非表示アクター（自身または祖先が visible=false）: エディタ選択・ポインタ
        // イベントの両方で、常に自身と全子孫を候補から外す。
        // 描画（collect_sprite_items / collect_canvas_id_items 等）が同じ条件で
        // サブツリーごと省くため、「見えていないものはクリックできない」を描画と一致させる。
        // 3D 側の ID ピックは元々非表示を除外済みなので、ここで 2D 側もそれに揃える。
        // visible は「スクリプトも物理も動くが描画だけ止める」フラグであり、経路（filter）に
        // よらず描画されない以上ピックもされない、という一意の規則にするため filter を見ない
        // （active は経路で意味が違う＝下の respect_active でエディタのみ無視するのに対し、
        // visible にそのような使い分けの要求はなく、常に除外が正しい）。
        // DFS 番号だけは正典どおり消費する（番号ズレ = 誤配信の原因）。
        if !actor.visible {
            skip_dfs_subtree(&actor.children, counter);
            continue;
        }

        // 非アクティブアクター（ポインタイベント時のみ。エディタ選択では従来どおり
        // 非アクティブでも選択できる）: 自身と全子孫を候補から外す。
        // DFS 番号だけは正典どおり消費する（番号ズレ = 誤配信の原因）。
        if filter.respect_active && !actor.active {
            skip_dfs_subtree(&actor.children, counter);
            continue;
        }

        // フォルダノード: レイアウト透明（canvas_node_is_transparent）。
        // 自身はサイズを持たないためヒット候補にせず（フォルダは決してピックされない）、
        // 子へは親の文脈をそのまま渡して再帰する。depth も進めない
        // （深さは重なり解決の優先度に使うため、階層に現れないフォルダで増やさない）。
        if canvas_node_is_transparent(actor) {
            legacy_walk_pick_candidates_2d(
                &actor.children,
                world,
                wl,
                canvas_x,
                canvas_y,
                counter,
                parent_world_rs,
                parent_cumul_scale,
                parent_canvas_size,
                depth,
                parent_zone,
                viewport_size,
                overrides,
                root_auto_sizes,
                design_space,
                mesh_of,
                text_boxes,
                filter,
                out,
            );
            continue;
        }

        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
        let Some(ct) = ct_opt else {
            // CanvasTransform なし（Actor3D 等）: ヒット対象外だが、DFS 番号は
            // find_actor_by_dfs と同じ規則（子孫も含めて全カウント）で消費する。
            // ここで子孫の番号を消費しないと以降の DFS ID がズレて、
            // ビューポートタブのクリックでワールドの別アクターが選択されてしまう。
            skip_dfs_subtree(&actor.children, counter);
            continue;
        };

        // ビューポート・ルートキャンバス: 自動解像度上書き + Transform 恒等化
        let root_auto = if parent_canvas_size.is_none() {
            root_auto_sizes.get(&actor.entity).copied()
        } else {
            None
        };
        let ct = if root_auto.is_some() {
            CanvasTransform::default()
        } else {
            ct
        };
        // スケールモードはこのノード自身の CanvasTransform から読み取る
        let (sm_transform, sm_size, keep_aspect, is_width_axis) = (
            ct.scale_transform,
            ct.scale_size,
            ct.keep_aspect_ratio,
            matches!(ct.aspect_ratio_axis, AspectRatioAxis::Width),
        );

        // アンカーオフセット（collect_canvas_rects と同一。Camera 参照を優先）
        let eff_viewport = if parent_canvas_size.is_none() {
            overrides.get(&actor.entity).copied().or(viewport_size)
        } else {
            viewport_size
        };
        // アンカーオフセットは描画（canvas_collect）と同じ共通ヘルパーを使う。
        let [anchor_off_x, anchor_off_y] = node_anchor_offset(
            parent_canvas_size,
            ct.anchor,
            parent_cumul_scale,
            eff_viewport,
            design_space,
        );

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

        let my_canvas = actor
            .slots()
            .iter()
            .filter(|s| s.kind == ComponentKind::Canvas)
            .find_map(|s| world.get::<CanvasComponent>(s.entity));

        // 描画ゾーン（ルートは自身、子は親から継承）
        let my_zone = if parent_canvas_size.is_none() {
            my_canvas.map(|cc| cc.draw_zone).unwrap_or(parent_zone)
        } else {
            parent_zone
        };

        // スケールモードに応じた有効サイズ係数（アスペクト比維持を考慮）
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
            .map(|cc| {
                let [bw, bh] = root_auto.unwrap_or([cc.width, cc.height]);
                (bw * size_sc_x, bh * size_sc_y)
            })
            .unwrap_or((1.0, 1.0));

        let self_world_rs = mat4x4_mul(
            parent_world_rs,
            CanvasTransform {
                scale: [1.0, 1.0],
                ..eff_ct.clone()
            }
            .to_mat4_sized(my_eff_w, my_eff_h),
        );

        // ── Sprite ヒット（最優先候補）────────────────────────────────────────
        for slot in actor.slots() {
            if slot.kind == ComponentKind::Sprite {
                // スロット無効（enabled=false）もポインタイベント経路（respect_active=true）
                // でのみ除外する。エディタ選択は従来どおり無効スロットも選択対象に含める。
                if filter.respect_active && !slot.enabled {
                    continue;
                }
                if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                    // ポインタイベントはオプトイン（raycast_target = true のみ判定対象）
                    if filter.require_raycast_target && !sc.raycast_target {
                        continue;
                    }
                    let eff_w = sc.width * size_sc_x;
                    let eff_h = sc.height * size_sc_y;
                    let m = mat4x4_mul(parent_world_rs, eff_ct.to_mat4_sized(eff_w, eff_h));
                    if hit_test_rect_2d(canvas_x, canvas_y, &m, eff_w, eff_h) {
                        out.push(PickCand2d {
                            dfs: my_dfs,
                            entity: actor.entity,
                            kind: PickKind2d::Sprite,
                            zone: my_zone,
                            depth,
                            layer: sc.layer,
                        });
                    }
                }
            }
        }

        // ── SkinnedSprite ヒット（変形後メッシュの三角形で判定）──────────────
        // 矩形スプライトと同じ優先度（PickKind2d::Sprite）で積む。
        // 判定形状は GPU ID パスと同じ「変形後メッシュ」なので、CPU ピック
        // （アクター編集 2D タブ）と GPU ピックでクリック結果が一致する。
        for slot in actor.slots() {
            if slot.kind != ComponentKind::SkinnedSprite || !slot.enabled {
                continue;
            }
            let Some(ss) = world.get::<SkinnedSpriteComponent>(slot.entity) else {
                continue;
            };
            // ポインタイベントはオプトイン（raycast_target = true のみ判定対象）
            if filter.require_raycast_target && !ss.raycast_target {
                continue;
            }
            let Some(mesh) = mesh_of(&ss.mesh_path) else {
                continue;
            };
            // メッシュ頂点は既に実寸を持つので、掛けるのは追加スケールのみ（描画と同一）
            let m = mat4x4_mul(parent_world_rs, eff_ct.to_mesh_mat4(size_sc_x, size_sc_y));
            let (bone_mats, _) = build_bone_matrices(&mesh, ss, actor, world);
            if hit_test_mesh_2d(canvas_x, canvas_y, &m, &mesh, &bone_mats) {
                out.push(PickCand2d {
                    dfs: my_dfs,
                    entity: actor.entity,
                    kind: PickKind2d::Sprite,
                    zone: my_zone,
                    depth,
                    layer: ss.layer,
                });
                break;
            }
        }

        // ── Text ヒット（テキストのレイアウト枠）────────────────────────────
        // 判定形状は描画と同じ「実測したブロック枠」を to_mesh_mat4 でキャンバス空間へ
        // 写したもの（スキンスプライトと同じチェーン。フォントサイズは行列側で拡縮する）。
        // 優先度はスプライトと同一（PickKind2d::Sprite・layer は TextComponent.layer）。
        //
        // ポインタイベント（require_raycast_target）では対象外。TextComponent は
        // raycast_target を持たない＝オプトインできないため、勝手に当たり判定を
        // 増やさない（Play 中の入力挙動を変えない）。
        if !filter.require_raycast_target {
            for slot in actor.slots() {
                if slot.kind != ComponentKind::Text {
                    continue;
                }
                // スロット無効（enabled=false）もポインタイベント経路（respect_active=true）
                // でのみ除外する。エディタ選択は従来どおり無効スロットも選択対象に含める。
                if filter.respect_active && !slot.enabled {
                    continue;
                }
                let Some(bx) = text_boxes.get(&slot.entity) else {
                    continue;
                };
                let Some(tc) = world.get::<TextComponent>(slot.entity) else {
                    continue;
                };
                // 枠モード（box_width > 0）は pivot を枠矩形へ焼き込み済みなので、
                // 行列側では pivot を効かせない（描画・ID パスと同一の規約）。
                let m = mat4x4_mul(
                    parent_world_rs,
                    if bx.zero_pivot {
                        eff_ct.to_mesh_mat4_no_pivot(size_sc_x, size_sc_y)
                    } else {
                        eff_ct.to_mesh_mat4(size_sc_x, size_sc_y)
                    },
                );
                if hit_test_local_box_2d(canvas_x, canvas_y, &m, bx.local.min, bx.local.max) {
                    out.push(PickCand2d {
                        dfs: my_dfs,
                        entity: actor.entity,
                        kind: PickKind2d::Sprite,
                        zone: my_zone,
                        depth,
                        layer: tc.layer,
                    });
                }
            }
        }

        // ── Canvas 矩形ヒット（補助候補）──────────────────────────────────────
        if filter.include_canvas && my_canvas.is_some() {
            let m = mat4x4_mul(parent_world_rs, eff_ct.to_mat4_sized(my_eff_w, my_eff_h));
            if hit_test_rect_2d(canvas_x, canvas_y, &m, my_eff_w, my_eff_h) {
                out.push(PickCand2d {
                    dfs: my_dfs,
                    entity: actor.entity,
                    kind: PickKind2d::Canvas,
                    zone: my_zone,
                    depth,
                    layer: 0,
                });
            }
        }

        // ── 子への継承情報を計算して再帰する（collect_canvas_rects と同一）─────
        // スケールモードは各子が自身の CanvasTransform から読み取るため伝播しない。
        let child_info =
            my_canvas.map(|cc| (root_auto.unwrap_or([cc.width, cc.height]), cc.auto_scale));
        let child_anchor_basis_size = child_anchor_basis(child_info.map(|(sz, _)| sz));
        let auto_scale_factor = if parent_canvas_size.is_none() {
            if let (Some([vw, vh]), Some((_, true))) = (eff_viewport, child_info) {
                [
                    vw / my_eff_w.max(f32::EPSILON),
                    vh / my_eff_h.max(f32::EPSILON),
                ]
            } else {
                [1.0f32, 1.0]
            }
        } else {
            [1.0f32, 1.0]
        };
        let child_cumul_scale = if sm_transform {
            [
                parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
                parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1],
            ]
        } else {
            [
                ct.scale[0] * auto_scale_factor[0],
                ct.scale[1] * auto_scale_factor[1],
            ]
        };

        legacy_walk_pick_candidates_2d(
            &actor.children,
            world,
            wl,
            canvas_x,
            canvas_y,
            counter,
            self_world_rs,
            child_cumul_scale,
            child_anchor_basis_size,
            depth + 1,
            my_zone,
            viewport_size,
            overrides,
            root_auto_sizes,
            design_space,
            mesh_of,
            text_boxes,
            filter,
            out,
        );
    }
}

pub(super) fn legacy_collect_actor2d_contexts(
    scene: &Scene,
    world_line: u32,
    viewport_size: Option<[f32; 2]>,
    canvas_viewport_overrides: &HashMap<Entity, [f32; 2]>,
    root_auto_sizes: &HashMap<Entity, [f32; 2]>,
    // ビューポートタブの設計空間表示中か（= edit_view_is_2d）。
    // true のときルートキャンバス左上をワールド原点に一致させる（描画と同一規則）。
    design_space: bool,
) -> Vec<Actor2dPhysicsCtx> {
    let mut result = Vec::new();
    let mut dfs_counter = 0u64;

    // スタック要素:
    //   (アクター, 親 Canvas サイズ, 親累積スケール,
    //    親キャンバス原点ワールド位置, 親累積ワールド回転)
    //   末尾に「親までの実効アクティブ」を追加（非アクティブは物理登録から除外する）。
    //   スケールモードは各ノードが自身の CanvasTransform から読み取るため伝播しない。
    type CtxElem<'a> = (&'a Actor, Option<[f32; 2]>, [f32; 2], [f32; 2], f32, bool);

    let mut stack: Vec<CtxElem> = scene
        .actors
        .iter()
        .filter(|a| a.world_line == world_line)
        .rev()
        .map(|a| {
            (
                a,
                None::<[f32; 2]>,
                [1.0f32, 1.0],
                [0.0f32, 0.0],
                0.0f32,
                true,
            )
        })
        .collect();

    while let Some((
        actor,
        parent_canvas_size,
        parent_cumul_scale,
        parent_canvas_origin,
        parent_world_rot,
        parent_active,
    )) = stack.pop()
    {
        let active = parent_active && actor.active;
        dfs_counter += 1;
        let dfs_id = dfs_counter;

        // ── フォルダノード: レイアウト透明（canvas_node_is_transparent）─────────
        // フォルダは位置・サイズを持たないグループなので、物理コンテキストを
        // 一切生成せず、子へは**親の文脈をそのまま**引き継いで積む。
        // これで canvas_collect.rs 側の描画（フォルダを素通しする）と座標が一致する。
        // DFS 番号はフォルダ 1 ノードぶんだけ上で消費済み。
        if canvas_node_is_transparent(actor) {
            for child in actor.children.iter().rev() {
                stack.push((
                    child,
                    parent_canvas_size,
                    parent_cumul_scale,
                    parent_canvas_origin,
                    parent_world_rot,
                    active,
                ));
            }
            continue;
        }

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
        let ct_owned: Option<CanvasTransform> =
            scene.world.get::<CanvasTransform>(actor.entity).map(|ct| {
                if root_auto.is_some() {
                    CanvasTransform::default()
                } else {
                    ct.clone()
                }
            });
        let ct_opt = ct_owned.as_ref();

        // スケールモードはこのノード自身の CanvasTransform から読み取る
        // （CanvasTransform を持たないノードはスケールなしの中立値）。
        let (sm_transform, sm_size, keep_aspect, is_width_axis) = ct_opt
            .map(|ct| {
                (
                    ct.scale_transform,
                    ct.scale_size,
                    ct.keep_aspect_ratio,
                    matches!(ct.aspect_ratio_axis, AspectRatioAxis::Width),
                )
            })
            .unwrap_or((false, false, false, true));

        // ── 自アクターの CanvasComponent を取得する ─────────────────────────────
        let my_canvas = actor
            .slots()
            .iter()
            .find(|s| s.kind == ComponentKind::Canvas)
            .and_then(|s| scene.world.get::<CanvasComponent>(s.entity));

        // 自動解像度上書きを反映した基準キャンバスサイズ（なければ保存値）
        let my_canvas_base = my_canvas.map(|cc| root_auto.unwrap_or([cc.width, cc.height]));

        // sm_size による拡縮を反映した有効キャンバスサイズ（子 canvas 原点・auto_scale 計算用、アスペクト比考慮）
        let phys_sc_x = if sm_size {
            if keep_aspect && !is_width_axis {
                parent_cumul_scale[1]
            } else {
                parent_cumul_scale[0]
            }
        } else {
            1.0
        };
        let phys_sc_y = if sm_size {
            if keep_aspect && is_width_axis {
                parent_cumul_scale[0]
            } else {
                parent_cumul_scale[1]
            }
        } else {
            1.0
        };
        let (my_eff_w, my_eff_h) = my_canvas_base
            .map(|[bw, bh]| (bw * phys_sc_x, bh * phys_sc_y))
            .unwrap_or((1.0, 1.0));

        // 子が参照する「有効 Canvas サイズ」（scale_size・アスペクト比モード考慮済み）
        let child_anchor_basis_size =
            child_anchor_basis(my_canvas_base.map(|[bw, bh]| [bw * phys_sc_x, bh * phys_sc_y]));

        // CanvasViewportRef::Camera を持つルートキャンバスのビューポートサイズを解決する。
        // ルートアクター（parent_canvas_size=None）のみオーバーライドマップを参照する。
        // canvas_collect.rs と同一のパターン。
        let eff_viewport = if parent_canvas_size.is_none() {
            canvas_viewport_overrides
                .get(&actor.entity)
                .copied()
                .or(viewport_size)
        } else {
            viewport_size
        };

        // auto_scale_factor: ルートキャンバス（parent_canvas_size=None）かつ auto_scale=true のとき
        // ビューポートサイズ / 基準キャンバスサイズ で計算する。
        // canvas_collect.rs と同一の計算。eff_viewport を使用してカメラ参照ビューポートに対応する。
        let auto_scale_factor = if parent_canvas_size.is_none() {
            if let (Some([vw, vh]), Some(true)) = (eff_viewport, my_canvas.map(|cc| cc.auto_scale))
            {
                [
                    vw / my_eff_w.max(f32::EPSILON),
                    vh / my_eff_h.max(f32::EPSILON),
                ]
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
            if sm_transform {
                [
                    parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
                    parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1],
                ]
            } else {
                [
                    ct.scale[0] * auto_scale_factor[0],
                    ct.scale[1] * auto_scale_factor[1],
                ]
            }
        } else {
            parent_cumul_scale
        };

        // ── 子への canvas 原点・累積回転を計算する ─────────────────────────────
        // child_canvas_origin = 自アクターの canvas ローカル [0,0] がマップされるワールド位置。
        let (child_canvas_origin, child_world_rot) = if let Some(ct) = ct_opt {
            // アンカーオフセット（canvas_collect.rs と同一の共通ヘルパー）:
            //   最上位: ビューポート基準（design_space により原点位置が変わる）
            //   子レベル: 親のアンカー基準サイズ × anchor × parent_cumul_scale
            // eff_viewport を使用: CanvasViewportRef::Camera 参照時はカメラの実効サイズを基準とする
            let anchor_off_child = node_anchor_offset(
                parent_canvas_size,
                ct.anchor,
                parent_cumul_scale,
                eff_viewport,
                design_space,
            );

            let eff_pos_local = if sm_transform {
                [
                    ct.position[0] * parent_cumul_scale[0] + anchor_off_child[0],
                    ct.position[1] * parent_cumul_scale[1] + anchor_off_child[1],
                ]
            } else {
                [
                    ct.position[0] + anchor_off_child[0],
                    ct.position[1] + anchor_off_child[1],
                ]
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
            let (canvas_eff_w, canvas_eff_h) = my_canvas_base
                .map(|[bw, bh]| {
                    (
                        bw * if sm_size { parent_cumul_scale[0] } else { 1.0 },
                        bh * if sm_size { parent_cumul_scale[1] } else { 1.0 },
                    )
                })
                .unwrap_or((1.0, 1.0));

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
            stack.push((
                child,
                child_anchor_basis_size,
                child_cumul_scale,
                child_canvas_origin,
                child_world_rot,
                active,
            ));
        }

        // ── Actor2D のみ処理 ─────────────────────────────────────────────────
        if actor.actor_kind != ActorKind::Actor2D {
            continue;
        }

        let Some(ct) = ct_opt else { continue };

        // Collider2d スロットエンティティを探す。
        // 非アクティブアクター・enabled=false のスロットは物理登録の対象外にする
        // （レイアウトコンテキスト自体はドラッグ書き戻し等で使うため収集は継続する）。
        let collider_slot_entity = if active {
            actor
                .slots()
                .iter()
                .find(|s| s.kind == ComponentKind::Collider2d && s.enabled)
                .map(|s| s.entity)
        } else {
            None
        };

        // ── 1. アンカー補正（canvas_collect.rs と同一） ───────────────────────
        // eff_viewport: CanvasViewportRef::Camera 参照時はカメラの実効サイズを基準とする
        // 描画（canvas_collect）と同じ共通ヘルパーを使う。
        // 以前はここだけ design_space を見ずに常に `-vp/2` していたため、
        // ビューポートタブ（設計空間）でギズモ位置が描画とズレていた。
        let anchor_off = node_anchor_offset(
            parent_canvas_size,
            ct.anchor,
            parent_cumul_scale,
            eff_viewport,
            design_space,
        );

        let eff_pos_local = if sm_transform {
            [
                ct.position[0] * parent_cumul_scale[0] + anchor_off[0],
                ct.position[1] * parent_cumul_scale[1] + anchor_off[1],
            ]
        } else {
            [
                ct.position[0] + anchor_off[0],
                ct.position[1] + anchor_off[1],
            ]
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
            let base = if sm_size {
                parent_cumul_scale
            } else {
                [1.0f32, 1.0]
            };
            // Collider2d の keep_aspect_ratio 設定を参照してsize_effを調整する
            if let Some(coll_ent) = collider_slot_entity {
                if let Some(coll) = scene.world.get::<Collider2dComponent>(coll_ent) {
                    if coll.keep_aspect_ratio && sm_size {
                        match &coll.aspect_ratio_axis {
                            AspectRatioAxis::Width => [base[0], base[0]],
                            AspectRatioAxis::Height => [base[1], base[1]],
                        }
                    } else {
                        base
                    }
                } else {
                    base
                }
            } else {
                base
            }
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
            rot_rad: actor_world_rot,
            scale: ct.scale,
            anchor_off,
            pivot_corr_local,
            sm_transform,
            cumul_scale: parent_cumul_scale,
            size_sx: size_eff[0],
            size_sy: size_eff[1],
            collider_slot_entity,
            parent_canvas_origin,
            parent_world_rot,
        });
    }

    result
}
