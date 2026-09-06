// ============================================================
//  pick_2d.rs — 2D キャンバスアクター CPU ピッキング
//
//  GPU ID パスを使わず、カーソル位置をキャンバス空間（ortho）に変換し
//  各アクターの Sprite / Canvas 矩形（向き付き境界ボックス）との
//  ヒットテストを CPU 上で行う。表示（collect_canvas_rects /
//  collect_sprite_items）と完全に同一の変換チェーン（design_space・
//  自動解像度上書き・eff_viewport・スケールモード）を使うことで、
//  「見た目の矩形」と「クリック判定」を一致させる。
//
//  【選択優先度（重なり時）】
//  1. Sprite（表示物）を Canvas より最優先（canvas は補助）
//  2. 描画ゾーン: 前面（Foreground）を背面（Background）より優先
//  3. 優先度が同じなら子アクタ（深い階層）を優先
//  4. さらに同じなら DFS 順で最前面（後に描画される方）を優先
//  同一地点を連続クリックすると次の優先度の候補へ巡回する。
// ============================================================

use std::collections::HashMap;

use crate::engine::components::{
    AspectRatioAxis, CanvasComponent, CanvasDrawZone, CanvasTransform, ComponentKind,
    SkinnedSpriteComponent, SpriteComponent, TextComponent, Transform,
};
use crate::engine::core::loader::sprite_mesh::SpriteMesh;
use crate::engine::core::renderer::sprite_skin::build_bone_matrices;
use crate::engine::core::app_base::undo::ActorDfsSelectionCommand;
use crate::engine::ecs::{Entity, World};
use crate::engine::methods::gizmo_interact::{mat4x4_mul, screen_to_ray};
use crate::engine::structs::objects::Actor;

use super::App;
use super::canvas_text_bounds::TextBoundsMap;
use super::canvas_collect::{canvas_node_is_transparent, root_anchor_offset, skip_dfs_subtree};

/// 巡回選択で「同一地点クリック」とみなすスクリーン座標の許容誤差（ピクセル）。
const PICK_CYCLE_TOLERANCE_PX: f32 = 4.0;

/// キャンバス座標（ピクセル）→ 3D ワールド座標の変換スケール（mod.rs の CANVAS_WORLD_SCALE と同値）。
const CANVAS_WORLD_SCALE: f32 = 1.0 / 100.0;

/// ピック候補の種別（優先度: Sprite > Canvas）。
#[derive(Clone, Copy, PartialEq, Eq)]
pub(super) enum PickKind2d {
    Sprite,
    Canvas,
}

/// クリック点に当たった 2D ピック候補。優先度ソート用のメタ情報を持つ。
///
/// エディタの選択ピック（pick_2d_canvas）と Play 中のポインタイベント
/// （pointer_events.rs）で同じ候補型・同じ走査を共有する。
pub(super) struct PickCand2d {
    /// アクター DFS ID（選択に使用）
    pub dfs: usize,
    /// 候補アクターのルートエンティティ（ポインタイベントの配信先解決に使用）
    pub entity: Entity,
    /// Sprite / Canvas 種別（Sprite 最優先）
    pub kind: PickKind2d,
    /// 描画ゾーン（前面優先）
    pub zone: CanvasDrawZone,
    /// 階層の深さ（大きいほど子＝優先）
    pub depth: u32,
    /// スプライトの描画レイヤー（大きいほど手前。Canvas 候補は 0）
    pub layer: i32,
}

/// 候補収集ウォークの絞り込み条件。
///
/// エディタの選択ピックと Play 中のポインタイベントで「拾いたいもの」が違うため、
/// 走査本体（walk_pick_candidates_2d）は 1 つに保ったままここで振る舞いを切り替える。
#[derive(Clone, Copy)]
pub(super) struct PickFilter2d {
    /// true: `raycast_target = true` のスプライトだけを候補にする（ポインタイベント用）。
    /// false: 全スプライトを候補にする（エディタ選択用・従来動作）。
    pub require_raycast_target: bool,
    /// true: 非アクティブアクター・無効スロットを除外する（＝描画されているものだけ）。
    /// false: 非表示でも選択できる（エディタ選択用・従来動作）。
    pub respect_visibility: bool,
    /// true: CanvasComponent の矩形も候補に含める（エディタ選択用・従来動作）。
    /// false: スプライトだけを候補にする（ポインタイベント用）。
    pub include_canvas: bool,
}

impl PickFilter2d {
    /// エディタの選択ピック用（従来動作を完全に維持する）。
    pub(super) const EDITOR_SELECT: Self = Self {
        require_raycast_target: false,
        respect_visibility: false,
        include_canvas: true,
    };
    /// Play 中のポインタイベント用（オプトインかつ可視のスプライトのみ）。
    pub(super) const POINTER_EVENT: Self = Self {
        require_raycast_target: true,
        respect_visibility: true,
        include_canvas: false,
    };
}

impl App {
    /// 2D キャンバス空間でクリックされたアクターを CPU 矩形ヒットテストで選択する。
    ///
    /// `cx`, `cy` はウィンドウスクリーン座標（左上原点、Y-down）。
    /// Sprite が Canvas より優先され、重なり時は描画ゾーン→子優先の順で並ぶ。
    /// 同一地点を連続クリックすると次の候補へ巡回選択する。
    pub(super) fn pick_2d_canvas(&mut self, cx: f32, cy: f32) {
        let wl = self.active_world_line;
        let win_size = self.window.as_ref().map(|w| w.inner_size());
        let vp_w = win_size.map_or(1280.0, |s| s.width as f32);
        let vp_h = win_size.map_or(720.0, |s| s.height as f32);

        // スクリーン座標 → キャンバス空間（ortho）座標
        // 2D ortho カメラ: pan + NDC * half_size
        let cam_2d = self.canvas_cameras.get(&wl).cloned().unwrap_or_default();
        let half_h = cam_2d.ortho_half_h;
        let half_w = half_h * (vp_w / vp_h);
        let ndx = 2.0 * cx / vp_w - 1.0;
        let ndy = 2.0 * cy / vp_h - 1.0; // Y-down
        let canvas_x = cam_2d.pan_x + ndx * half_w;
        let canvas_y = cam_2d.pan_y + ndy * half_h;

        // ── 表示と同一条件のレイアウトマップ・フラグを構築する ────────────────
        // アクター編集/キャンバス編集タブ（actor_edit_canvas_wls）:
        //   ビューポート基準なし（viewport_size=None）・自動解像度なし・design_space=false。
        // シーンのビューポートタブ（2D シーンビュー）:
        //   ビューポート基準あり・自動解像度あり・design_space=edit_view_is_2d()。
        let is_actor_edit = self.actor_edit_canvas_wls.contains(&wl);
        let design_space = self.edit_view_is_2d();

        // テキストの実測枠を先に作る（フォントレジストリの可変借用が要るため、
        // scene を不変借用する候補収集より前に済ませる）。
        let text_boxes = self.build_text_bounds_map();

        // 候補を収集する（scene を読み取り専用で借用して完結させる）
        let mut cands: Vec<PickCand2d> = Vec::new();
        if let Some(scene) = self.scene.as_ref() {
            // アクター編集/キャンバス編集タブはビューポート基準・自動解像度なし。
            // 2D シーンビューは表示と同一のレイアウトマップを構築する。
            let (viewport_size, overrides, root_auto) = if is_actor_edit {
                (None, HashMap::new(), HashMap::new())
            } else {
                let (ov, ra) =
                    self.build_ss_layout_maps(&scene.actors, &scene.world, wl, vp_w, vp_h, None);
                (Some([vp_w, vp_h]), ov, ra)
            };

            const IDENTITY: [[f32; 4]; 4] = [
                [1.0, 0.0, 0.0, 0.0],
                [0.0, 1.0, 0.0, 0.0],
                [0.0, 0.0, 1.0, 0.0],
                [0.0, 0.0, 0.0, 1.0],
            ];
            // `.sprite_mesh` の CPU キャッシュ（スキンスプライトの三角形判定に使う）。
            // ハンドルを clone して渡すので、`scene` の不変借用と競合しない。
            let mesh_cache = self.sprite_mesh_cpu_handle();
            let mesh_of = |path: &str| {
                super::sprite_bone_ops::load_sprite_mesh_cached(&mesh_cache, path)
            };

            let mut counter: u32 = 0;
            walk_pick_candidates_2d(
                &scene.actors,
                &scene.world,
                wl,
                canvas_x,
                canvas_y,
                &mut counter,
                IDENTITY,
                [1.0, 1.0],
                None,
                0,
                CanvasDrawZone::Foreground,
                viewport_size,
                &overrides,
                &root_auto,
                design_space,
                &mesh_of,
                &text_boxes,
                PickFilter2d::EDITOR_SELECT,
                &mut cands,
            );
        }

        // ── 優先度順にソートする（index 0 = 既定で選択される最優先候補）────────
        //   1. Sprite < Canvas         （Sprite 最優先）
        //   2. Foreground < Background  （前面優先）
        //   3. depth 降順              （子＝深い階層を優先）
        //   4. dfs 降順                （DFS 後方＝最前面を優先）
        cands.sort_by(|a, b| {
            let ka = (kind_rank(a.kind), zone_rank(a.zone));
            let kb = (kind_rank(b.kind), zone_rank(b.zone));
            ka.cmp(&kb)
                .then(b.depth.cmp(&a.depth))
                .then(b.dfs.cmp(&a.dfs))
        });
        let sorted_dfs: Vec<usize> = cands.iter().map(|c| c.dfs).collect();

        // ── 巡回選択: 同一地点＆同一候補列の連続クリックで次候補へ回す ──────────
        let chosen: Option<usize> = if sorted_dfs.is_empty() {
            self.pick_2d_cycle = None;
            None
        } else {
            let cycle_index = match &self.pick_2d_cycle {
                Some(([px, py], prev_list, prev_idx))
                    if (px - cx).abs() <= PICK_CYCLE_TOLERANCE_PX
                        && (py - cy).abs() <= PICK_CYCLE_TOLERANCE_PX
                        && *prev_list == sorted_dfs =>
                {
                    (prev_idx + 1) % sorted_dfs.len()
                }
                _ => 0,
            };
            self.pick_2d_cycle = Some(([cx, cy], sorted_dfs.clone(), cycle_index));
            Some(sorted_dfs[cycle_index])
        };

        // ── 選択状態を更新する（Undo 記録付き）───────────────────────────────
        let before_dfs_ids = self.selected_actor_dfs_ids.clone();
        let before_primary = self.actor_virtual_selected_idx;

        if let Some(dfs_id) = chosen {
            if self.drag.ctrl_at_press {
                // Ctrl+クリック: マルチ選択トグル
                if self.selected_actor_dfs_ids.contains(&dfs_id) {
                    self.selected_actor_dfs_ids.retain(|&x| x != dfs_id);
                    if self.actor_virtual_selected_idx == Some(dfs_id) {
                        self.actor_virtual_selected_idx =
                            self.selected_actor_dfs_ids.last().copied();
                    }
                } else {
                    self.selected_actor_dfs_ids.push(dfs_id);
                    self.actor_virtual_selected_idx = Some(dfs_id);
                }
            } else {
                // 通常クリック: 単一選択
                self.actor_virtual_selected_idx = Some(dfs_id);
                self.selected_actor_dfs_ids = vec![dfs_id];
            }
            self.selected_instances.clear();
            self.send_actor_components(dfs_id as u32, 0);
        } else if !self.drag.ctrl_at_press {
            // 空クリック: 選択解除
            self.actor_virtual_selected_idx = None;
            self.selected_actor_dfs_ids.clear();
            self.selected_instances.clear();
        }

        // アクター DFS 選択の Undo 記録
        let after_dfs_ids = self.selected_actor_dfs_ids.clone();
        let after_primary = self.actor_virtual_selected_idx;
        if before_dfs_ids != after_dfs_ids || before_primary != after_primary {
            self.undo_history.record(Box::new(ActorDfsSelectionCommand {
                before_dfs_ids,
                after_dfs_ids,
                before_primary,
                after_primary,
            }));
        }
        self.send_selected();
    }
}

// ── 優先度ランク（小さいほど優先）─────────────────────────────────────────────

/// 種別ランク: Sprite（表示物）を最優先、Canvas は補助。
fn kind_rank(k: PickKind2d) -> u8 {
    match k {
        PickKind2d::Sprite => 0,
        PickKind2d::Canvas => 1,
    }
}

/// 描画ゾーンランク: 前面（Foreground）を背面（Background）より優先。
pub(super) fn zone_rank(z: CanvasDrawZone) -> u8 {
    match z {
        CanvasDrawZone::Foreground => 0,
        CanvasDrawZone::Background => 1,
    }
}

// ── ヒットテスト ─────────────────────────────────────────────────────────────

/// キャンバス空間の点 (px, py) が、行列 m で定義された
/// ローカル空間 [0, eff_w] × [0, eff_h] の矩形内にあるか判定する。
///
/// m[0][3], m[1][3] が平行移動成分、2×2 部分が回転×スケールを表す。
/// 逆行列をクラメールの公式で解き、ローカル座標が範囲内か確認する。
/// canvas_drop.rs のドロップ先キャンバスヒット判定でも共用するため pub(super)。
pub(super) fn hit_test_rect_2d(
    px: f32,
    py: f32,
    m: &[[f32; 4]; 4],
    eff_w: f32,
    eff_h: f32,
) -> bool {
    // 2×2 回転スケール行列の行列式
    let det = m[0][0] * m[1][1] - m[0][1] * m[1][0];
    if det.abs() < 1e-9 {
        return false;
    } // 面積 0 の退化矩形は無視する
    let dx = px - m[0][3];
    let dy = py - m[1][3];
    // クラメールの公式で逆変換
    let lx = (dx * m[1][1] - dy * m[0][1]) / det;
    let ly = (m[0][0] * dy - m[1][0] * dx) / det;
    lx >= 0.0 && lx <= eff_w && ly >= 0.0 && ly <= eff_h
}

/// キャンバス空間の点 (px, py) が、ローカル境界矩形 [min, max] に入るか判定する。
///
/// `hit_test_rect_2d` は原点が矩形の左上である前提（スプライト・キャンバス）だが、
/// テキストは align / vertical_align によって枠が原点の左や上へはみ出す。
/// そのため min/max を明示できるこの版を使う。
pub(super) fn hit_test_local_box_2d(
    px: f32,
    py: f32,
    m: &[[f32; 4]; 4],
    min: [f32; 2],
    max: [f32; 2],
) -> bool {
    let det = m[0][0] * m[1][1] - m[0][1] * m[1][0];
    if det.abs() < 1e-9 {
        return false;
    }
    let dx = px - m[0][3];
    let dy = py - m[1][3];
    let lx = (dx * m[1][1] - dy * m[0][1]) / det;
    let ly = (m[0][0] * dy - m[1][0] * dx) / det;
    lx >= min[0] && lx <= max[0] && ly >= min[1] && ly <= max[1]
}

/// キャンバス空間の点 (px, py) が、スキンメッシュの**変形後の三角形**のいずれかに
/// 入っているか判定する（Phase A2）。
///
/// # なぜ逆変換してから判定するのか
/// スキン後の頂点は「メッシュローカル（スプライトローカルのキャンバスピクセル）」
/// 座標であり、`m`（= parent_world_rs × to_mesh_mat4）がそれをキャンバス空間へ写す。
/// 三角形を 1 つずつ順変換するより、**点を 1 度だけ逆変換**するほうが安い
/// （頂点数に依らず逆行列計算は 1 回）。
///
/// GPU の ID パスも変形後頂点でメッシュ形状のまま ID を描くため、この判定と
/// GPU ピックは同じ形になる（＝ どちらの経路でもクリック判定が見た目と一致する）。
fn hit_test_mesh_2d(
    px: f32,
    py: f32,
    m: &[[f32; 4]; 4],
    mesh: &SpriteMesh,
    bone_matrices: &[[[f32; 4]; 4]],
) -> bool {
    // 2×2 回転スケール行列の行列式（面積 0 の退化行列は判定不能）
    let det = m[0][0] * m[1][1] - m[0][1] * m[1][0];
    if det.abs() < 1e-9 {
        return false;
    }
    let dx = px - m[0][3];
    let dy = py - m[1][3];
    // クラメールの公式でメッシュローカル座標へ逆変換
    let lx = (dx * m[1][1] - dy * m[0][1]) / det;
    let ly = (m[0][0] * dy - m[1][0] * dx) / det;

    // 変形後頂点を 1 度だけ計算する（CPU スキニングの正典 = SpriteMesh::skin_vertex）
    let deformed: Vec<[f32; 2]> = (0..mesh.vertex_count())
        .map(|vi| mesh.skin_vertex(vi, bone_matrices))
        .collect();

    // 各三角形で内外判定（符号付き面積の符号が 3 辺で揃えば内側）
    for tri in mesh.triangles.chunks_exact(3) {
        let (a, b, c) = (
            deformed[tri[0] as usize],
            deformed[tri[1] as usize],
            deformed[tri[2] as usize],
        );
        if point_in_triangle([lx, ly], a, b, c) {
            return true;
        }
    }
    false
}

/// 点が三角形の内側（辺上を含む）にあるか。
///
/// 3 辺の外積の符号が揃っているかで判定する。三角形の巻き方向（CW/CCW）に
/// 依存しないよう「負が無い or 正が無い」で見る。
fn point_in_triangle(p: [f32; 2], a: [f32; 2], b: [f32; 2], c: [f32; 2]) -> bool {
    let cross = |u: [f32; 2], v: [f32; 2], w: [f32; 2]| {
        (v[0] - u[0]) * (w[1] - u[1]) - (v[1] - u[1]) * (w[0] - u[0])
    };
    let d1 = cross(a, b, p);
    let d2 = cross(b, c, p);
    let d3 = cross(c, a, p);
    let has_neg = d1 < 0.0 || d2 < 0.0 || d3 < 0.0;
    let has_pos = d1 > 0.0 || d2 > 0.0 || d3 > 0.0;
    !(has_neg && has_pos)
}

// ── 候補収集ウォーク（collect_canvas_rects と同一の変換チェーン）──────────────

/// アクターツリーを DFS ウォークし、クリック点に当たる Sprite/Canvas 候補を集める。
///
/// collect_canvas_rects / collect_sprite_items と完全に同じ変換チェーン
/// （design_space・自動解像度上書き・eff_viewport・スケールモード）を使うことで、
/// 描画される矩形とヒット判定を一致させる。SS ピック専用のため canvas_scale=1・
/// y_sign=1（ortho 空間）で計算する。
#[allow(clippy::too_many_arguments)]
pub(super) fn walk_pick_candidates_2d(
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

        // 非アクティブアクター（ポインタイベント時のみ）: 自身と全子孫を候補から外す。
        // 描画（collect_sprite_items）が同じ条件でサブツリーごと省くため、
        // 「見えていないものはクリックできない」を描画と一致させられる。
        // DFS 番号だけは正典どおり消費する（番号ズレ = 誤配信の原因）。
        if filter.respect_visibility && !actor.active {
            skip_dfs_subtree(&actor.children, counter);
            continue;
        }

        // フォルダノード: レイアウト透明（canvas_node_is_transparent）。
        // 自身はサイズを持たないためヒット候補にせず（フォルダは決してピックされない）、
        // 子へは親の文脈をそのまま渡して再帰する。depth も進めない
        // （深さは重なり解決の優先度に使うため、階層に現れないフォルダで増やさない）。
        if canvas_node_is_transparent(actor) {
            walk_pick_candidates_2d(
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
        let (anchor_off_x, anchor_off_y) = if parent_canvas_size.is_none() {
            if let Some([vw, vh]) = eff_viewport {
                let [ox, oy] = root_anchor_offset(ct.anchor, vw, vh, design_space);
                (ox, oy)
            } else {
                (0.0, 0.0)
            }
        } else {
            (
                parent_canvas_size.map_or(0.0, |[pw, _]| pw * ct.anchor[0] * parent_cumul_scale[0]),
                parent_canvas_size.map_or(0.0, |[_, ph]| ph * ct.anchor[1] * parent_cumul_scale[1]),
            )
        };

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
                if filter.respect_visibility && !slot.enabled {
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
                if filter.respect_visibility && !slot.enabled {
                    continue;
                }
                let Some(bx) = text_boxes.get(&slot.entity) else {
                    continue;
                };
                let Some(tc) = world.get::<TextComponent>(slot.entity) else {
                    continue;
                };
                let m = mat4x4_mul(parent_world_rs, eff_ct.to_mesh_mat4(size_sc_x, size_sc_y));
                if hit_test_local_box_2d(canvas_x, canvas_y, &m, bx.min, bx.max) {
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
        let child_canvas_size = child_info.map(|(sz, _)| sz);
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

        walk_pick_candidates_2d(
            &actor.children,
            world,
            wl,
            canvas_x,
            canvas_y,
            counter,
            self_world_rs,
            child_cumul_scale,
            child_canvas_size,
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

// ── 3D ワールドキャンバスのレイピック（world タブ用フォールバック）──────────────

impl App {
    /// world（3D）タブで、カメラレイと 3D ワールドキャンバス面の交差により
    /// キャンバスアクターの DFS ID を返す（GPU ID ピックが背景ヒットした際のフォールバック）。
    ///
    /// トップレベル Actor3D + CanvasComponent のキャンバス面（[0,w]×[0,h]）を対象にし、
    /// レイ最近点のキャンバスを返す。重なり時の巡回は 2D 専用のためここでは行わない。
    pub(super) fn pick_3d_world_canvas(&self, cx: f32, cy: f32) -> Option<usize> {
        let scene = self.scene.as_ref()?;
        let win = self.window.as_ref().map(|w| w.inner_size());
        let vp_w = win.map_or(1280.0, |s| s.width as f32);
        let vp_h = win.map_or(720.0, |s| s.height as f32);
        let cam_v = self.camera.position();
        let cam = [cam_v.x, cam_v.y, cam_v.z];
        let view = self.camera.view_matrix();
        let proj = self.camera.projection_matrix();
        let (r0, rd) = screen_to_ray(cx, cy, vp_w, vp_h, &view.data, &proj.data, cam);

        let mut counter = 0u32;
        let mut best: Option<(f32, usize)> = None; // (レイパラメータ t, DFS ID)
        walk_3d_canvas_pick(
            &scene.actors,
            &scene.world,
            self.active_world_line,
            &mut counter,
            r0,
            rd,
            &mut best,
        );
        best.map(|(_, dfs)| dfs)
    }
}

/// 3D ワールドキャンバス（Actor3D + CanvasComponent）の面とレイの交差を DFS 走査で調べ、
/// レイ最近点のキャンバス DFS ID を `best` に記録する。
fn walk_3d_canvas_pick(
    actors: &[Actor],
    world: &World,
    wl: u32,
    counter: &mut u32,
    r0: [f32; 3],
    rd: [f32; 3],
    best: &mut Option<(f32, usize)>,
) {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        let my_dfs = *counter as usize;
        *counter += 1;

        // Actor3D + CanvasComponent の面をテストする（描画と同一の canvas_to_world）
        if !actor.is_2d() {
            let canvas_slot = actor
                .slots()
                .iter()
                .find(|s| s.kind == ComponentKind::Canvas);
            if let (Some(cs), Some(tf)) = (canvas_slot, world.get::<Transform>(actor.entity)) {
                if let Some(cc) = world.get::<CanvasComponent>(cs.entity) {
                    let cws = CANVAS_WORLD_SCALE;
                    let (px, py) = (cc.pivot[0], cc.pivot[1]);
                    let ctw = mat4x4_mul(
                        tf.to_mat4(),
                        [
                            [cws, 0.0, 0.0, -px * cc.width * cws],
                            [0.0, -cws, 0.0, py * cc.height * cws],
                            [0.0, 0.0, 1.0, 0.0],
                            [0.0, 0.0, 0.0, 1.0],
                        ],
                    );
                    if let Some(t) = ray_hit_canvas_quad(r0, rd, &ctw, cc.width, cc.height) {
                        if best.map_or(true, |(bt, _)| t < bt) {
                            *best = Some((t, my_dfs));
                        }
                    }
                }
            }
        }

        walk_3d_canvas_pick(&actor.children, world, wl, counter, r0, rd, best);
    }
}

/// レイ (r0, rd) と、行列 `ctw`（canvas_to_world）が定義するローカル矩形
/// [0,w]×[0,h] 平面との交差レイパラメータ t を返す（ヒットなしは None）。
fn ray_hit_canvas_quad(
    r0: [f32; 3],
    rd: [f32; 3],
    ctw: &[[f32; 4]; 4],
    w: f32,
    h: f32,
) -> Option<f32> {
    let o = [ctw[0][3], ctw[1][3], ctw[2][3]]; // 面の原点（ローカル [0,0]）
    let xw = [ctw[0][0], ctw[1][0], ctw[2][0]]; // ローカル +x（1px）→ ワールドベクトル
    let yw = [ctw[0][1], ctw[1][1], ctw[2][1]]; // ローカル +y（1px）→ ワールドベクトル
    let n = cross(xw, yw); // 面の法線
    let denom = dot(rd, n);
    if denom.abs() < 1e-9 {
        return None;
    } // レイが面と平行
    let t = dot(sub(o, r0), n) / denom;
    if t <= 0.0 {
        return None;
    } // 面がカメラ後方
    let p = [r0[0] + rd[0] * t, r0[1] + rd[1] * t, r0[2] + rd[2] * t];
    let l = sub(p, o);
    // xw ⊥ yw のためローカル px 座標は各軸への射影で求まる
    let lx = dot(l, xw) / dot(xw, xw).max(1e-12);
    let ly = dot(l, yw) / dot(yw, yw).max(1e-12);
    if lx >= 0.0 && lx <= w && ly >= 0.0 && ly <= h {
        Some(t)
    } else {
        None
    }
}

#[inline]
fn dot(a: [f32; 3], b: [f32; 3]) -> f32 {
    a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
}
#[inline]
fn sub(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}
#[inline]
fn cross(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::CanvasComponent;
    use crate::engine::core::font::text_layout::TextLocalBox;
    use crate::engine::core::loader::sprite_mesh::IDENTITY_MAT4;
    use crate::engine::structs::objects::Actor;

    // ── フォルダノード透過（canvas_node_is_transparent）のヒット判定テスト ──────
    //
    //  UI アクターをフォルダで括っても、括る前とまったく同じ位置がクリック可能で
    //  なければならない。フォルダ自身は矩形を持たないため決して候補にならない。

    /// ルートキャンバス（FishingUI 相当）の設計解像度。ビューポートと同値にして
    /// auto_scale の影響（倍率 1.0）を排除する。
    const UI_CANVAS: [f32; 2] = [1280.0, 720.0];
    /// 中間キャンバス（Panel）のサイズ。ルートと**別サイズ**にすることで、
    /// 「フォルダを不透明に扱うとルートレベル扱いになる」バグを確実に検出できる。
    const PANEL_SIZE: [f32; 2] = [400.0, 300.0];
    /// 中間キャンバスのローカル位置（ルート左上からのオフセット）。
    const PANEL_POSITION: [f32; 2] = [100.0, 50.0];
    /// テスト用スプライトのサイズ。
    const SEG_SIZE: [f32; 2] = [40.0, 20.0];
    /// テスト用スプライトのアンカー（中間キャンバス中央）。
    const SEG_ANCHOR: [f32; 2] = [0.5, 0.5];
    /// テスト用スプライトのローカル位置。
    const SEG_POSITION: [f32; 2] = [0.0, -140.0];

    /// FishingUI(1280x720) > Panel(400x300) > [フォルダ] > GaugeSeg00 のシーンを作る。
    /// `use_folder=true` のとき、スプライトを 2D フォルダで 1 段包む。
    fn build_ui_scene(use_folder: bool) -> (Vec<Actor>, World) {
        let mut world = World::new();

        /// キャンバスアクターを 1 つ作る小ヘルパー。
        fn make_canvas(
            world: &mut World,
            name: &str,
            size: [f32; 2],
            position: [f32; 2],
        ) -> Actor {
            let entity = world.spawn();
            world.insert(
                entity,
                CanvasTransform {
                    position,
                    ..CanvasTransform::default()
                },
            );
            let slot = world.spawn();
            world.insert(
                slot,
                CanvasComponent {
                    width: size[0],
                    height: size[1],
                    ..CanvasComponent::default()
                },
            );
            let mut a = Actor::new_2d(entity, name);
            a.world_line = 0;
            a.add_slot_typed::<CanvasComponent>("Canvas", ComponentKind::Canvas, slot);
            a
        }

        let mut root = make_canvas(&mut world, "FishingUI", UI_CANVAS, [0.0, 0.0]);
        let mut panel = make_canvas(&mut world, "Panel", PANEL_SIZE, PANEL_POSITION);

        // スプライト（ゲージセグメント相当）
        let seg_entity = world.spawn();
        world.insert(
            seg_entity,
            CanvasTransform {
                position: SEG_POSITION,
                anchor: SEG_ANCHOR,
                ..CanvasTransform::default()
            },
        );
        let sprite_slot = world.spawn();
        world.insert(
            sprite_slot,
            SpriteComponent {
                width: SEG_SIZE[0],
                height: SEG_SIZE[1],
                ..SpriteComponent::default()
            },
        );
        let mut seg = Actor::new_2d(seg_entity, "GaugeSeg00");
        seg.world_line = 0;
        seg.add_slot_typed::<SpriteComponent>("Sprite", ComponentKind::Sprite, sprite_slot);

        if use_folder {
            // 2D フォルダ（生成時に恒等 CanvasTransform を持ち CanvasComponent は持たない）
            let folder_entity = world.spawn();
            world.insert(folder_entity, CanvasTransform::default());
            let mut folder = Actor::new_folder_2d(folder_entity, "GaugeSegments");
            folder.world_line = 0;
            folder.add_child(seg);
            panel.add_child(folder);
        } else {
            panel.add_child(seg);
        }
        root.add_child(panel);

        (vec![root], world)
    }

    /// スプライト中心の設計空間座標（design_space=true ではキャンバス左上が原点）。
    fn seg_center() -> [f32; 2] {
        [
            PANEL_POSITION[0] + PANEL_SIZE[0] * SEG_ANCHOR[0] + SEG_POSITION[0]
                + SEG_SIZE[0] * 0.5,
            PANEL_POSITION[1] + PANEL_SIZE[1] * SEG_ANCHOR[1] + SEG_POSITION[1]
                + SEG_SIZE[1] * 0.5,
        ]
    }

    /// 設計空間（design_space=true。ビューポートタブ）でエディタ選択ピックを走らせる。
    /// テキスト枠を持たないケース用の薄いラッパ。
    fn editor_hits(actors: &[Actor], world: &World, pt: [f32; 2]) -> Vec<PickCand2d> {
        editor_hits_with_text(actors, world, pt, &TextBoundsMap::new())
    }

    /// テキスト実測枠を渡してエディタ選択ピックを走らせる。
    fn editor_hits_with_text(
        actors: &[Actor],
        world: &World,
        pt: [f32; 2],
        text_boxes: &TextBoundsMap,
    ) -> Vec<PickCand2d> {
        let empty: HashMap<Entity, [f32; 2]> = HashMap::new();
        let mesh_of = |_: &str| None;
        let mut out = Vec::new();
        let mut counter = 0u32;
        walk_pick_candidates_2d(
            actors,
            world,
            0,
            pt[0],
            pt[1],
            &mut counter,
            IDENTITY_MAT4,
            [1.0, 1.0],
            None,
            0,
            CanvasDrawZone::Foreground,
            Some(UI_CANVAS),
            &empty,
            &empty,
            true,
            &mesh_of,
            text_boxes,
            PickFilter2d::EDITOR_SELECT,
            &mut out,
        );
        out
    }

    /// フォルダで括ってもスプライトのヒット位置は変わらない（描画位置と一致する）。
    #[test]
    fn folder_does_not_move_sprite_hit_area() {
        let center = seg_center();

        for use_folder in [false, true] {
            let (actors, world) = build_ui_scene(use_folder);
            let hits = editor_hits(&actors, &world, center);
            assert!(
                hits.iter().any(|c| c.kind == PickKind2d::Sprite),
                "フォルダ={use_folder}: スプライト中心はヒットしなければならない"
            );
        }
    }

    /// フォルダ自身は矩形を持たないため、決してピック候補にならない。
    #[test]
    fn folder_is_never_a_pick_candidate() {
        let center = seg_center();
        let (actors, world) = build_ui_scene(true);
        let hits = editor_hits(&actors, &world, center);
        // DFS: 0=FishingUI / 1=Panel / 2=GaugeSegments(フォルダ) / 3=GaugeSeg00
        assert!(
            hits.iter().all(|c| c.dfs != 2),
            "フォルダ（dfs=2）が候補に含まれてはならない"
        );
        assert!(hits.iter().any(|c| c.dfs == 3), "スプライト（dfs=3）は候補になる");
    }

    // ── TextComponent のピック（2D 編集）─────────────────────────────────
    //
    //  テキストは実測したブロック枠（text_layout::measure_text_box）で当たる。
    //  枠の実測はフォントに依存するので、テストでは描画と同じ組み込みフォントで
    //  測った値を表に入れて走らせる（＝実行時と同じ経路をたどる）。

    /// テスト用テキストのローカル位置（ルートキャンバス左上からのオフセット）。
    const TEXT_POSITION: [f32; 2] = [200.0, 120.0];
    /// テスト用テキストの内容・サイズ。
    const TEXT_CONTENT: &str = "Score";
    const TEXT_FONT_SIZE: f32 = 32.0;
    const TEXT_LINE_SPACING: f32 = 1.2;

    /// FishingUI(1280x720) > Label(TextComponent) のシーンと、実測した枠の表を作る。
    fn build_text_scene() -> (Vec<Actor>, World, TextBoundsMap, TextLocalBox) {
        use crate::engine::components::TextComponent;
        use crate::engine::core::font::text_layout::measure_text_box;
        use ab_glyph::FontArc;

        let mut world = World::new();
        // ルートキャンバス
        let root_entity = world.spawn();
        world.insert(root_entity, CanvasTransform::default());
        let root_slot = world.spawn();
        world.insert(
            root_slot,
            CanvasComponent {
                width: UI_CANVAS[0],
                height: UI_CANVAS[1],
                ..CanvasComponent::default()
            },
        );
        let mut root = Actor::new_2d(root_entity, "FishingUI");
        root.world_line = 0;
        root.add_slot_typed::<CanvasComponent>("Canvas", ComponentKind::Canvas, root_slot);

        // テキストアクター
        let text_entity = world.spawn();
        world.insert(
            text_entity,
            CanvasTransform {
                position: TEXT_POSITION,
                ..CanvasTransform::default()
            },
        );
        let text_slot = world.spawn();
        let tc = TextComponent {
            content: TEXT_CONTENT.to_string(),
            font_size: TEXT_FONT_SIZE,
            line_spacing: TEXT_LINE_SPACING,
            ..TextComponent::default()
        };
        let (align, valign) = (tc.align, tc.vertical_align);
        world.insert(text_slot, tc);
        let mut label = Actor::new_2d(text_entity, "Label");
        label.world_line = 0;
        label.add_slot_typed::<TextComponent>("Text", ComponentKind::Text, text_slot);
        root.add_child(label);

        // 描画と同じ組み込みフォントで実測する
        let font = FontArc::try_from_slice(crate::engine::core::font::DEFAULT_FONT_BYTES)
            .expect("組み込みフォントを読める");
        let bx = measure_text_box(
            &font,
            TEXT_CONTENT,
            TEXT_FONT_SIZE,
            TEXT_LINE_SPACING,
            align,
            valign,
            0.0,
        )
        .expect("枠が得られる");
        let mut map = TextBoundsMap::new();
        map.insert(text_slot, bx);

        (vec![root], world, map, bx)
    }

    /// テキストのブロック枠の内側はピック候補になる（スプライトと同じ優先度）。
    #[test]
    fn text_actor_is_picked_inside_its_layout_box() {
        let (actors, world, boxes, bx) = build_text_scene();
        // 枠の中心（設計空間 = キャンバス左上原点）
        let center = [
            TEXT_POSITION[0] + (bx.min[0] + bx.max[0]) * 0.5,
            TEXT_POSITION[1] + (bx.min[1] + bx.max[1]) * 0.5,
        ];
        let hits = editor_hits_with_text(&actors, &world, center, &boxes);
        // DFS: 0=FishingUI / 1=Label
        assert!(
            hits.iter().any(|c| c.dfs == 1 && c.kind == PickKind2d::Sprite),
            "テキスト枠の中心はテキストアクター（dfs=1）にヒットする"
        );
    }

    /// テキストのブロック枠の外はヒットしない（キャンバス候補だけが残る）。
    #[test]
    fn text_actor_is_not_picked_outside_its_layout_box() {
        let (actors, world, boxes, bx) = build_text_scene();
        // 枠の右外側（同じ行の高さで、幅ぶん右へ外す）
        let outside = [
            TEXT_POSITION[0] + bx.max[0] + (bx.max[0] - bx.min[0]),
            TEXT_POSITION[1] + (bx.min[1] + bx.max[1]) * 0.5,
        ];
        let hits = editor_hits_with_text(&actors, &world, outside, &boxes);
        assert!(
            !hits.iter().any(|c| c.dfs == 1),
            "枠の外ではテキストアクターはヒットしない"
        );
    }

    /// 実測枠が無い（フォントが引けない・空文字）テキストはヒットしない。
    #[test]
    fn text_without_measured_box_is_not_picked() {
        let (actors, world, _boxes, bx) = build_text_scene();
        let center = [
            TEXT_POSITION[0] + (bx.min[0] + bx.max[0]) * 0.5,
            TEXT_POSITION[1] + (bx.min[1] + bx.max[1]) * 0.5,
        ];
        let hits = editor_hits_with_text(&actors, &world, center, &TextBoundsMap::new());
        assert!(
            !hits.iter().any(|c| c.dfs == 1),
            "枠が測れないテキストはピック対象外"
        );
    }

    /// スプライト矩形の外は、フォルダの有無に関わらずヒットしない。
    #[test]
    fn folder_does_not_widen_hit_area() {
        // スプライトから十分離れた点（キャンバス内・スプライト外）
        let c = seg_center();
        let outside = [c[0] + SEG_SIZE[0], c[1]];
        for use_folder in [false, true] {
            let (actors, world) = build_ui_scene(use_folder);
            let hits = editor_hits(&actors, &world, outside);
            assert!(
                !hits.iter().any(|c| c.kind == PickKind2d::Sprite),
                "フォルダ={use_folder}: 矩形外でスプライトが当たってはならない"
            );
        }
    }

    /// 最小の矩形メッシュ（[0,0]-[100,80]・1 ボーン）。
    const QUAD_ONE_BONE: &str =
        include_str!("../../../../../tests/fixtures/quad_one_bone.sprite_mesh");

    /// 点-三角形判定: 内側・頂点・辺上は真、外側は偽。巻き方向に依存しない。
    #[test]
    fn point_in_triangle_basics() {
        let a = [0.0, 0.0];
        let b = [10.0, 0.0];
        let c = [0.0, 10.0];
        assert!(point_in_triangle([1.0, 1.0], a, b, c), "内側");
        assert!(point_in_triangle([0.0, 0.0], a, b, c), "頂点");
        assert!(point_in_triangle([5.0, 0.0], a, b, c), "辺上");
        assert!(!point_in_triangle([9.0, 9.0], a, b, c), "外側（斜辺の外）");
        assert!(!point_in_triangle([-1.0, 1.0], a, b, c), "外側（左）");
        // 巻き方向を逆にしても結果は同じ
        assert!(point_in_triangle([1.0, 1.0], a, c, b));
        assert!(!point_in_triangle([9.0, 9.0], a, c, b));
    }

    /// 無変形（恒等スキン）の矩形メッシュは、その矩形の内外がそのまま判定結果になる。
    #[test]
    fn hit_test_mesh_matches_quad_bounds() {
        let mesh = SpriteMesh::from_json(QUAD_ONE_BONE).expect("読み込み成功");
        let bones = mesh.identity_bone_matrices();
        let m = IDENTITY_MAT4;

        // フィクスチャは [0,0]-[100,80] の矩形（docs/sprite_skinning.md §3）
        assert!(hit_test_mesh_2d(50.0, 40.0, &m, &mesh, &bones), "中心は当たる");
        assert!(hit_test_mesh_2d(0.5, 0.5, &m, &mesh, &bones), "左上近傍は当たる");
        assert!(!hit_test_mesh_2d(-1.0, 40.0, &m, &mesh, &bones), "左外は当たらない");
        assert!(!hit_test_mesh_2d(101.0, 40.0, &m, &mesh, &bones), "右外は当たらない");
        assert!(!hit_test_mesh_2d(50.0, 81.0, &m, &mesh, &bones), "下外は当たらない");
    }

    /// 平行移動した行列を与えると、判定領域も同じだけ動く（点側を逆変換している証拠）。
    #[test]
    fn hit_test_mesh_follows_model_matrix() {
        let mesh = SpriteMesh::from_json(QUAD_ONE_BONE).expect("読み込み成功");
        let bones = mesh.identity_bone_matrices();
        // x へ +200 平行移動する行列（行優先: m[0][3] が tx）
        let mut m = IDENTITY_MAT4;
        m[0][3] = 200.0;

        assert!(!hit_test_mesh_2d(50.0, 40.0, &m, &mesh, &bones), "元の位置には無い");
        assert!(hit_test_mesh_2d(250.0, 40.0, &m, &mesh, &bones), "移動後の位置に当たる");
    }

    /// 面積 0 の退化行列は判定不能として偽を返す（ゼロ除算を作らない）。
    #[test]
    fn hit_test_mesh_rejects_degenerate_matrix() {
        let mesh = SpriteMesh::from_json(QUAD_ONE_BONE).expect("読み込み成功");
        let bones = mesh.identity_bone_matrices();
        let mut m = IDENTITY_MAT4;
        m[0][0] = 0.0;
        m[1][1] = 0.0;
        assert!(!hit_test_mesh_2d(50.0, 40.0, &m, &mesh, &bones));
    }
}
