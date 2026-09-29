// ============================================================
//  canvas_layout/pass.rs — アクター木を 1 回たどってレイアウトの表を作る走査
//
//  旧実装の 5 つの走査がそれぞれ持っていた「木のたどり方」と「親 → 子の文脈の受け渡し」を
//  ここに 1 つだけ置く。ノードごとの計算は placement.rs の純関数 `resolve` に任せ、
//  ここでは次だけを行う:
//    - ルートの世界線での絞り込みと、深さ優先の並び（find_actor_by_dfs と同じ規則）
//    - フォルダ（レイアウト透明）は文脈をそのまま子へ渡す
//    - CanvasTransform を持たないノードは 2D レイアウト木の外にする（描画・当たり判定は打ち切る。
//      2D 物理だけが子孫をたどるので、文脈は旧 collect_actor2d_contexts と同じ規則で素通しする）
//    - 祖先までの世界線・active・visible をフラグにする（読み手ごとの打ち切り規則を表で表す）
//    - 切り抜き（CanvasClipComponent）の領域を積み、各ノードへ切り抜きの番号を持たせる
//    - レイアウトの部品（W2-1b）: ノードを置く順は
//        1. 親のコンテナが割り当てた矩形があればそこへ（resolve_in_rect）。無ければ自分のアンカー・位置（resolve）
//           で置き、CanvasLayoutItem の fill_* があれば親の領域に合わせる
//        2. CanvasSafeArea があれば箱を安全領域の内側へ縮める（resize_box）
//        2b. CanvasLayoutItem の見た目の上書き（W2-7。実行中だけ）: レイヤーの底上げ（layer_bias）を祖先の分に足して
//           子へ渡し、見た目の平行移動（translate・translate_fraction）で有効位置と行列だけをずらす（translate_placement）。
//           安全領域は祖先のずらしを戻した位置で求める（CanvasParentFrame.visual_shift。横から入ってくる画面の箱が縮まない）。
//           見た目の倍率（visual_scale。W2 の手直し 3b）は平行移動の後の矩形の中心の周りに縮める（scale_placement）。
//           子の累積スケールごと縮むので、このノードのコンテナの並び・子孫の配置は倍率の空間で求まり、子孫の安全領域は
//           倍率の前の位置・大きさで求める（CanvasParentFrame.visual_scale と visual_shift の写像）。既定 (1, 1) のノードは通らない
//        3. コンテナ（CanvasStack・CanvasWrap・CanvasGrid）なら子を測って並べ（measure.rs・containers/）、
//           子ごとの矩形を「割り当て待ち」に積む。fit の軸は箱を中身に合わせる
//      子は必ずコンテナより後に訪ねる（深さ優先）ので、2 段の計算（測る → 並べる）が 1 回の走査の中で終わる。
//      測った結果は覚えておき（LayoutMeasurer）、同じノードを同じ条件で 2 度測らない（ノード数に比例）。
//      部品を使わない木では、従来とまったく同じ計算（resolve だけ）を通る。
//    - スクロールの窓（W2-3。scroll_view.rs）: CanvasScrollComponent を持つノードは、子へ渡す文脈をスクロールの位置だけ
//      平行移動し（コンテナなら スクロールの軸は箱の長さを決めずに並べる）、窓・中身の大きさを `scroll_regions` へ積む。
//      切り抜きと「見える範囲の外を飛ばす」が有効なら、子孫の部分木の範囲（キャンバス空間）を集めて、見える範囲
//      （切り抜き + cache_extent）と交わらない部分木に `culled` を立てる（canvas_scroll/visibility.rs）。
//      スクロールを使わない木では範囲を集めない（費用は増えない）。
// ============================================================

use std::collections::HashMap;

use crate::engine::components::{
    CanvasClipComponent, CanvasComponent, CanvasTransform, ComponentKind, SpriteComponent,
};
use crate::engine::core::renderer::ui_clip::UiClipId;
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

use super::clip::{
    canvas_area_corners, clip_shape_of, corners_aabb, has_room, sprite_rect_corners, CanvasClipRegion, ClipRectSource,
};
use super::containers::spec::container_of;
use super::containers::{clamp_size, Constraint, LayoutSlot, AXIS_X, AXIS_Y};
use super::frame::{CanvasLayoutEnv, CanvasParentFrame, NO_VISUAL_SCALE};
use super::lookup::{layout_item_of, safe_area_of};
use super::measure::{box_per_rect, container_inner, LayoutMeasurer};
use super::placement::{
    pass_through_frame, resize_box, resolve, resolve_in_rect, scale_placement, translate_placement, CanvasNodeInput,
    CanvasNodePlacement,
};
use super::safe_area::{inset_box, world_rect_to_local, CanvasRect};
use super::scroll_view::{
    axis_directions, expand, local_far_edge, node_bounds, offset_px, scroll_of, translate_frame, union, viewport_size,
    CanvasScrollRegion, ClipAabb, ScrollNode,
};
use super::table::{CanvasLayoutNode, CanvasLayoutStats, CanvasLayoutTable, CanvasNodeFlags, CanvasNodeKind};
use crate::engine::components::ScrollContentSize;
use crate::engine::core::canvas_scroll::visibility::cull_outside;

/// 最上位ノードの深さ（当たり判定の「子を優先」の順位の起点）。
const ROOT_DEPTH: u32 = 0;

/// ルートが受け取るフラグ（祖先が居ないので、すべて真から始める）。
const ROOT_FLAGS: CanvasNodeFlags = CanvasNodeFlags {
    world_line_chain: true,
    active_chain: true,
    visible_chain: true,
    in_2d_tree: true,
};

/// レイアウトの表を作る走査。
pub struct CanvasLayoutPass;

impl CanvasLayoutPass {
    /// アクター木を 1 回たどってレイアウトの表を作る。
    ///
    /// # 引数
    /// * `roots`      - 走査のルートの並び（シーンの全アクター、または 3D ワールドキャンバスの子）
    /// * `world`      - コンポーネントの置き場
    /// * `world_line` - 対象の世界線（ルートだけ絞り込む。子は世界線を問わず表に入り、フラグで区別する）
    /// * `root_frame` - ルートが受け取る文脈（2D キャンバスは `CanvasParentFrame::viewport_root`）
    /// * `env`        - 走査全体の入力
    ///
    /// # 戻り値
    /// 深さ優先の並びの表（添字 = DFS 番号）と切り抜きの領域の表。
    pub fn run(
        roots: &[Actor],
        world: &World,
        world_line: u32,
        root_frame: CanvasParentFrame,
        env: &CanvasLayoutEnv<'_>,
    ) -> CanvasLayoutTable {
        let mut builder = TableBuilder {
            world,
            env,
            world_line,
            table: CanvasLayoutTable {
                nodes: Vec::new(),
                clip_regions: Vec::new(),
                world_line,
                stats: CanvasLayoutStats::default(),
                scroll_regions: Vec::new(),
            },
            pending_slots: HashMap::new(),
            measurer: LayoutMeasurer::new(world),
            bounds_depth: 0,
            subtree_bounds: HashMap::new(),
            scroll_container_content: HashMap::new(),
        };
        for root in roots.iter().filter(|a| a.world_line == world_line) {
            builder.visit(root, None, root_frame, ROOT_DEPTH, ROOT_FLAGS, None);
        }
        builder.table.stats.measure_calls = builder.measurer.measure_calls;
        builder.table
    }
}

/// 表を組み立てる間の状態。
struct TableBuilder<'w, 'e> {
    /// コンポーネントの置き場。
    world: &'w World,
    /// 走査全体の入力。
    env: &'w CanvasLayoutEnv<'e>,
    /// 対象の世界線。
    world_line: u32,
    /// 組み立て中の表。
    table: CanvasLayoutTable,
    /// コンテナが子へ割り当てた矩形（子を訪ねたときに取り出す。W2-1b）。
    pending_slots: HashMap<Entity, LayoutSlot>,
    /// ノードの大きさを測る道具（結果を覚えて、同じ条件で 2 度測らない。W2-1b）。
    measurer: LayoutMeasurer<'w>,
    /// 見える範囲の外を飛ばすスクロールの窓の中にいる深さ（0 より大きい間だけ部分木の範囲を集める。W2-3）。
    bounds_depth: u32,
    /// 行 → 部分木の範囲（キャンバス空間。飛ばす窓の子孫だけ。W2-3）。
    subtree_bounds: HashMap<u32, ClipAabb>,
    /// コンテナでもあるスクロールの窓が並べた中身の大きさ（窓のローカルの画素・余白を含む。W2-3）。
    scroll_container_content: HashMap<Entity, [f32; 2]>,
}

impl<'w, 'e> TableBuilder<'w, 'e> {
    /// ノード 1 つを表へ積み、子孫をたどる。
    ///
    /// # 引数
    /// * `actor`        - ノード
    /// * `parent`       - 親の行の添字
    /// * `frame`        - 親から受け取った文脈
    /// * `depth`        - 階層の深さ（フォルダを数えない）
    /// * `parent_flags` - 親までのフラグ
    /// * `parent_clip`  - 親から受け継いだ切り抜きの番号（このノード自身の描画アイテムが入る領域）
    ///
    /// # 戻り値
    /// 部分木の範囲（キャンバス空間。見える範囲の外を飛ばすスクロールの窓の中にいるときだけ Some。W2-3）。
    fn visit(
        &mut self,
        actor: &Actor,
        parent: Option<u32>,
        frame: CanvasParentFrame,
        depth: u32,
        parent_flags: CanvasNodeFlags,
        parent_clip: Option<UiClipId>,
    ) -> Option<ClipAabb> {
        let index = self.table.nodes.len() as u32;
        let flags = CanvasNodeFlags {
            world_line_chain: parent_flags.world_line_chain && actor.world_line == self.world_line,
            active_chain: parent_flags.active_chain && actor.active,
            visible_chain: parent_flags.visible_chain && actor.visible,
            in_2d_tree: parent_flags.in_2d_tree,
        };
        // 祖先のスクロールの窓が見える範囲の外を飛ばす（このノードの部分木の範囲が要る。W2-3）
        let tracking_bounds = self.bounds_depth > 0;

        // ── ノードの種類ごとに、配置と子へ渡す文脈を決める ──
        let (mut kind, mut child_frame, child_depth, child_in_2d_tree) = if actor.is_folder() {
            // フォルダ: レイアウト上は存在しないものとして、文脈をそのまま子へ渡す（深さも進めない）
            (CanvasNodeKind::Folder, frame, depth, flags.in_2d_tree)
        } else if let Some(transform) = self.world.get::<CanvasTransform>(actor.entity) {
            let placement = self.place_node(
                actor,
                &frame,
                &CanvasNodeInput {
                    entity: actor.entity,
                    transform,
                    canvas: layout_canvas_of(actor, self.world),
                },
            );
            let child_frame = placement.child_frame;
            (CanvasNodeKind::Placed(placement), child_frame, depth + 1, flags.in_2d_tree)
        } else {
            // CanvasTransform なし: 2D レイアウト木の外（描画・当たり判定はここで打ち切る）。
            // 2D 物理のために文脈だけ素通しする（アンカー基準は自身の CanvasComponent）。
            let canvas_base = layout_canvas_of(actor, self.world).map(|cc| {
                let root_auto = if frame.is_root() {
                    self.env.root_auto_sizes.get(&actor.entity).copied()
                } else {
                    None
                };
                root_auto.unwrap_or([cc.width, cc.height])
            });
            let child_frame = pass_through_frame(&frame, canvas_base);
            (CanvasNodeKind::NoTransform { child_frame }, child_frame, depth + 1, false)
        };

        // ── スクロールの窓（W2-3）: 子へ渡す文脈を位置だけ平行移動する（表の配置の child_frame も同じ値にそろえる）──
        let scroll: Option<ScrollNode<'w>> = match &kind {
            CanvasNodeKind::Placed(_) if flags.in_2d_tree => scroll_of(actor, self.world),
            _ => None,
        };
        let mut scroll_offset = [0.0, 0.0];
        if let (Some(s), CanvasNodeKind::Placed(placement)) = (&scroll, &mut kind) {
            scroll_offset = offset_px(s.settings, s.position, placement.child_frame.cumul_scale);
            translate_frame(&mut placement.child_frame, scroll_offset);
            child_frame = placement.child_frame;
        }

        // ── 切り抜きの領域（2D レイアウト木の中で、切り抜きのコンポーネントが有効なノードだけ）──
        let own_clip_region = match &kind {
            CanvasNodeKind::Placed(placement) if flags.in_2d_tree && clips_children(actor, self.world) => {
                self.push_clip_region(actor, index, &frame, placement, parent_clip)
            }
            _ => None,
        };

        self.table.nodes.push(CanvasLayoutNode {
            entity: actor.entity,
            parent,
            // 子孫を積み終えたら下で書き直す
            subtree_end: index + 1,
            depth,
            frame,
            kind,
            flags,
            clip: parent_clip,
            own_clip_region,
            culled: false,
        });

        // ── 見える範囲の外を飛ばす範囲（切り抜きと cull_outside が両方有効な窓。切り抜きの AABB + cache_extent）──
        let cull_view = match (&scroll, own_clip_region) {
            (Some(s), Some(id)) if s.settings.cull_outside => {
                let region = &self.table.clip_regions[id as usize];
                let margin = s.settings.cache_extent_units();
                let cumul = child_frame.cumul_scale;
                Some(expand(corners_aabb(&region.corners), [margin * cumul[0].abs(), margin * cumul[1].abs()]))
            }
            _ => None,
        };
        if cull_view.is_some() {
            self.bounds_depth += 1;
        }

        let child_flags = CanvasNodeFlags {
            in_2d_tree: child_in_2d_tree,
            ..flags
        };
        let child_clip = own_clip_region.or(parent_clip);
        let mut children_bounds: Option<ClipAabb> = None;
        // 窓の直接の子の行（中身の大きさを測る。窓でなければ集めない）
        let mut child_rows: Vec<u32> = Vec::new();
        for child in &actor.children {
            let child_row = self.table.nodes.len() as u32;
            let bounds = self.visit(child, Some(index), child_frame, child_depth, child_flags, child_clip);
            children_bounds = union(children_bounds, bounds);
            if scroll.is_some() {
                child_rows.push(child_row);
            }
        }
        let end = self.table.nodes.len() as u32;
        self.table.nodes[index as usize].subtree_end = end;

        if let Some(view) = cull_view {
            self.bounds_depth -= 1;
            let culled = cull_outside(&mut self.table.nodes, index as usize + 1, end as usize, &self.subtree_bounds, view);
            self.table.stats.culled += culled;
        }
        if let Some(s) = &scroll {
            self.push_scroll_region(actor, index, s, scroll_offset, own_clip_region, &child_rows);
        }

        // ── 部分木の範囲（祖先の窓が飛ばす判定に使う）──
        if !tracking_bounds {
            return None;
        }
        let own = match &self.table.nodes[index as usize].kind {
            CanvasNodeKind::Placed(placement) => Some(node_bounds(actor, self.world, &frame, placement)),
            _ => None,
        };
        let subtree = union(own, children_bounds);
        if let Some(bounds) = subtree {
            self.subtree_bounds.insert(index, bounds);
        }
        subtree
    }

    /// スクロールの領域を 1 つ積む（窓・中身の大きさと換算。W2-3）。
    ///
    /// # 引数
    /// * `actor`      - 窓のノード
    /// * `index`      - 窓の行
    /// * `scroll`     - 窓のスクロール
    /// * `offset`     - 子へ当てた平行移動（窓のローカルの画素）
    /// * `own_clip`   - 窓の切り抜きの番号
    /// * `child_rows` - 直接の子の行（`actor.children` と同じ並び）
    fn push_scroll_region(
        &mut self,
        actor: &Actor,
        index: u32,
        scroll: &ScrollNode<'_>,
        offset: [f32; 2],
        own_clip: Option<UiClipId>,
        child_rows: &[u32],
    ) {
        let CanvasNodeKind::Placed(placement) = &self.table.nodes[index as usize].kind else { return };
        let cumul = placement.child_frame.cumul_scale;
        let viewport = viewport_size(actor, self.world, placement);
        let axis_dirs = axis_directions(&placement.world_rs);
        let content = match scroll.settings.content_size {
            ScrollContentSize::Fixed => {
                let fixed = scroll.settings.fixed_content();
                [fixed[0] * cumul[0].abs(), fixed[1] * cumul[1].abs()]
            }
            ScrollContentSize::Auto => {
                let mut far = [0.0f32; 2];
                for (child, &row) in actor.children.iter().zip(child_rows) {
                    self.accumulate_far_edge(child, row, &mut far);
                }
                if let Some(content) = self.scroll_container_content.remove(&actor.entity) {
                    far = [far[0].max(content[0]), far[1].max(content[1])];
                }
                far
            }
        };
        let view = own_clip
            .and_then(|id| self.table.clip_regions.get(id as usize))
            .map(|region| corners_aabb(&region.corners));
        self.table.scroll_regions.push(CanvasScrollRegion {
            owner: index,
            node: actor.entity,
            slot: scroll.slot,
            viewport_px: viewport,
            content_px: content,
            px_per_unit: [cumul[0].abs(), cumul[1].abs()],
            axis_dirs,
            offset_px: offset,
            view,
            dp_scale: self.env.screen.dp_scale(),
        });
        self.table.stats.scrolls += 1;
    }

    /// 窓の子（フォルダは中へ入る）の矩形のいちばん遠い端を `far` へ足し込む（見えている・有効な子だけ。W2-3）。
    fn accumulate_far_edge(&self, actor: &Actor, row: u32, far: &mut [f32; 2]) {
        let Some(node) = self.table.nodes.get(row as usize) else { return };
        match &node.kind {
            CanvasNodeKind::Folder => {
                let mut child_row = row + 1;
                for child in &actor.children {
                    self.accumulate_far_edge(child, child_row, far);
                    child_row = self.table.nodes.get(child_row as usize).map_or(child_row + 1, |n| n.subtree_end);
                }
            }
            CanvasNodeKind::Placed(placement) if node.flags.visible_chain && node.flags.active_chain => {
                let edge = local_far_edge(actor, self.world, placement);
                *far = [far[0].max(edge[0]), far[1].max(edge[1])];
            }
            _ => {}
        }
    }

    /// 切り抜きの領域を 1 つ積む（矩形が決まらない・表が一杯なら積まない）。
    ///
    /// # 戻り値
    /// 積んだ領域の番号。
    fn push_clip_region(
        &mut self,
        actor: &Actor,
        owner: u32,
        frame: &CanvasParentFrame,
        placement: &CanvasNodePlacement,
        parent_clip: Option<UiClipId>,
    ) -> Option<UiClipId> {
        if !has_room(&self.table.clip_regions) {
            return None;
        }
        let sprite = actor
            .slots()
            .iter()
            .filter(|s| s.kind == ComponentKind::Sprite && s.enabled)
            .find_map(|s| self.world.get::<SpriteComponent>(s.entity));
        // 領域の 4 隅と、ローカルの大きさ（画素の大きさ ÷ サイズ倍率 = キャンバスの単位。角丸の半径と同じ単位。W2-4）
        let (corners, source, eff) = if placement.canvas_base.is_some() {
            // 1. キャンバス領域（エディタのキャンバス枠と同じ矩形）
            (
                canvas_area_corners(frame.world_rs, &placement.eff_transform, placement.eff_size),
                ClipRectSource::CanvasArea,
                placement.eff_size,
            )
        } else {
            // 2. 最初の有効なスプライトの矩形（W2-0 の試作と同じ）。無ければ切らない
            let sprite = sprite?;
            // レイアウトが伸ばした軸は矩形の大きさ（描画と同じ。W2-1b）
            let size = placement.sprite_size(sprite.width, sprite.height);
            (
                sprite_rect_corners(frame.world_rs, &placement.eff_transform, size),
                ClipRectSource::FirstSprite,
                size,
            )
        };
        let local_size = [0, 1].map(|a| {
            let scale = placement.size_scale[a];
            if scale.abs() > f32::EPSILON { eff[a] / scale } else { eff[a] }
        });
        // 切り抜きの形（W2-4。有効な CanvasClip の最初のもの。既定の矩形なら形なし）
        let shape = clip_component_of(actor, self.world)
            .map(|clip| clip_shape_of(clip.shape, clip.corner_radii, sprite.map(|s| &s.shape), local_size))
            .unwrap_or_default();
        let id = self.table.clip_regions.len() as UiClipId;
        self.table.clip_regions.push(CanvasClipRegion {
            corners,
            parent: parent_clip,
            owner,
            source,
            shape,
        });
        Some(id)
    }
}

impl<'w, 'e> TableBuilder<'w, 'e> {
    /// CanvasTransform を持つノードの配置を決める（W2-1b のレイアウトの部品を含む）。
    ///
    /// 部品を使わないノード（割り当ても fill も安全領域もコンテナも無い）は `resolve` の結果そのまま。
    fn place_node(
        &mut self,
        actor: &Actor,
        frame: &CanvasParentFrame,
        input: &CanvasNodeInput<'_>,
    ) -> CanvasNodePlacement {
        // ── 1. 親のコンテナの割り当て／自分のアンカー・位置（＋親に合わせる）──
        let slot = self.pending_slots.remove(&actor.entity);
        let mut placement = match &slot {
            Some(slot) => {
                self.table.stats.placed_by_layout += 1;
                resolve_in_rect(frame, input, self.env, slot)
            }
            None => {
                let resolved = resolve(frame, input, self.env);
                match self.fill_parent_slot(actor, frame, &resolved) {
                    Some(fill_slot) => {
                        self.table.stats.placed_by_layout += 1;
                        resolve_in_rect(frame, input, self.env, &fill_slot)
                    }
                    None => resolved,
                }
            }
        };

        // ── 2. 安全領域（CanvasComponent を持つノードの箱を縮める）──
        let safe_applied = self.apply_safe_area(actor, frame, &mut placement);

        // ── 2b. 見た目の上書き（W2-7・3b。レイヤーの底上げ・平行移動・倍率。コンテナの並べ方より前に当て、子孫が付いてくる）──
        let visual_scale = self.apply_visual_overrides(actor, frame, &mut placement);

        // ── 3. コンテナ（子を測って並べ、子ごとの矩形を割り当て待ちに積む）──
        // 見た目の倍率（3b）を当てたノードは、子の累積スケール・サイズ倍率・箱がそろって倍率の空間にあるので、並びもその空間で求める
        let child_cumul = placement.child_frame.cumul_scale;
        let Some(spec) = container_of(actor, self.world, child_cumul) else {
            return placement;
        };
        self.table.stats.containers += 1;
        let pad = spec.padding_sum();
        let inner: Constraint = match (&slot, safe_applied) {
            // 親のコンテナの下: 親が測ったときと同じ条件で箱を決める（測った結果の表を引ける）
            (Some(slot), false) => {
                let preferred = layout_item_of(actor, self.world).map_or([None, None], |it| it.preferred());
                // 割り当てられた矩形は親のローカル（倍率の前）なので、倍率を当てたノードは倍率の空間の大きさにする
                let slot_size = match visual_scale {
                    Some(s) => [slot.size[AXIS_X] * s[AXIS_X], slot.size[AXIS_Y] * s[AXIS_Y]],
                    None => slot.size,
                };
                let tight: Constraint = [AXIS_X, AXIS_Y].map(|a| {
                    slot.fill[a]
                        .then_some(slot_size[a])
                        .or(preferred[a].map(|p| p * placement.size_scale[a]))
                });
                let canvas_box = input.canvas.map(|cc| [cc.width * child_cumul[AXIS_X], cc.height * child_cumul[AXIS_Y]]);
                container_inner(tight, canvas_box, &spec, box_per_rect(child_cumul, placement.size_scale))
            }
            // それ以外: 実際の箱（安全領域で縮めた・親に合わせた後）。fit の軸と箱の無い軸は中身に合わせる
            _ => {
                let assigned = placement.layout_rect.is_some();
                let has_canvas = placement.canvas_base.is_some();
                let box_size = placement.box_size();
                [AXIS_X, AXIS_Y].map(|a| match box_size {
                    Some(b) if assigned || (has_canvas && !spec.fit[a]) => Some((b[a] - pad[a]).max(0.0)),
                    _ => None,
                })
            }
        };
        // スクロールの窓でもあるコンテナ（W2-3）: スクロールの軸は箱の長さを決めずに並べる（中身は窓より長くてよい）。
        // 並べた中身の大きさ（余白を含む）は、窓の中身の大きさ（Auto）に使う
        let scroll_settings = scroll_of(actor, self.world).map(|s| s.settings);
        let inner: Constraint = match scroll_settings {
            Some(settings) => [AXIS_X, AXIS_Y].map(|a| if settings.direction.scrolls_axis(a) { None } else { inner[a] }),
            None => inner,
        };
        let (items, arrangement) = self.measurer.arrange_children(actor, &spec, child_cumul, inner);
        if scroll_settings.is_some() {
            self.scroll_container_content.insert(actor.entity, arrangement.content);
        }

        // 中身に合わせる（CanvasComponent を持ち、親のコンテナの割り当てが無いときだけ。割り当てがあれば割り当てが勝つ）
        if slot.is_none() && placement.canvas_base.is_some() && (spec.fit[AXIS_X] || spec.fit[AXIS_Y]) {
            if let Some(box_size) = placement.box_size() {
                let new_box = [AXIS_X, AXIS_Y].map(|a| if spec.fit[a] { arrangement.content[a] } else { box_size[a] });
                placement = resize_box(&placement, frame, [0.0, 0.0], new_box);
                // 中身に合わせた軸は、ノードのスプライト（背景の板）も新しい大きさで描く
                for a in [AXIS_X, AXIS_Y] {
                    if spec.fit[a] {
                        placement.sprite_fill[a] = Some(placement.eff_size[a]);
                    }
                }
            }
        }

        for (item, item_slot) in items.iter().zip(arrangement.slots) {
            self.pending_slots.insert(item.entity, item_slot);
        }
        placement
    }

    /// 親に合わせる（CanvasLayoutItem.fill_*）ときの矩形（コンテナの外のノード。W2-1b）。
    ///
    /// 合わせる軸は親の箱（親の CanvasComponent の領域 × 累積スケール＝子のアンカーの基準と同じ）の全体、
    /// 合わせない軸は自分の大きさで、`resolve` が決めた位置のまま。最上位（親が居ない）では使わない。
    fn fill_parent_slot(
        &mut self,
        actor: &Actor,
        frame: &CanvasParentFrame,
        resolved: &CanvasNodePlacement,
    ) -> Option<LayoutSlot> {
        if frame.is_root() {
            return None;
        }
        let item = layout_item_of(actor, self.world)?;
        let fill = item.fill();
        if !fill[AXIS_X] && !fill[AXIS_Y] {
            return None;
        }
        let basis = frame.anchor_basis?;
        let parent_box = [basis[AXIS_X] * frame.cumul_scale[AXIS_X], basis[AXIS_Y] * frame.cumul_scale[AXIS_Y]];
        let natural = self.measurer.natural_size(actor, frame.cumul_scale);
        let (min, max) = (item.min(), item.max());
        let pos = resolved.eff_transform.position;
        let pivot = resolved.eff_transform.pivot;
        let size_scale = resolved.size_scale;
        let mut slot = LayoutSlot { origin: [0.0; 2], size: [0.0; 2], fill };
        for a in [AXIS_X, AXIS_Y] {
            if fill[a] {
                slot.size[a] = clamp_size(parent_box[a], min[a] * size_scale[a], max[a] * size_scale[a]);
                slot.origin[a] = 0.0;
            } else {
                slot.size[a] = natural[a];
                slot.origin[a] = pos[a] - pivot[a] * natural[a];
            }
        }
        Some(slot)
    }

    /// CanvasLayoutItem の見た目の上書き（W2-7・W2 の手直し 3b。実行中だけの値）を当てる。
    ///
    /// - レイヤーの底上げ: 祖先の分（`frame.layer_bias`）に自分の `layer_bias` を足し、自分の表示と子へ渡す文脈に持たせる
    /// - 見た目の平行移動: `translate × 親の累積スケール + translate_fraction × 自分の矩形` だけ有効位置と行列をずらす
    ///   （自分の矩形 = レイアウトが割り当てた矩形。無ければ CanvasComponent の領域。どちらも無ければ割合は効かない）
    /// - 見た目の倍率（3b）: 平行移動の後の矩形の中心の周りに縮める・広げる（`scale_placement`。子孫・描画・当たり判定が付いてくる）
    ///
    /// CanvasLayoutItem を持たない・値が既定（0・倍率 1）のノードは何もしない（従来とまったく同じ計算のまま）。
    ///
    /// # 戻り値
    /// 当てた見た目の倍率（倍率を当てなければ None。コンテナの並べ方が矩形の大きさを倍率の空間へ合わせるのに使う）。
    fn apply_visual_overrides(
        &mut self,
        actor: &Actor,
        frame: &CanvasParentFrame,
        placement: &mut CanvasNodePlacement,
    ) -> Option<[f32; 2]> {
        let item = layout_item_of(actor, self.world)?;
        if item.layer_bias != 0 {
            let bias = placement.layer_bias.saturating_add(item.layer_bias);
            placement.layer_bias = bias;
            placement.child_frame.layer_bias = bias;
        }
        if item.has_translation() {
            let own_size = match (placement.layout_rect, placement.canvas_base) {
                (Some(rect), _) => rect,
                (None, Some(_)) => placement.eff_size,
                (None, None) => [0.0, 0.0],
            };
            let offset = item.translation_px(frame.cumul_scale, own_size);
            if offset != [0.0, 0.0] {
                *placement = translate_placement(placement, frame, offset);
                self.table.stats.translated += 1;
            }
        }
        // 見た目の倍率（既定 (1, 1) は has_visual_scale が false＝ここで素通し。計算は従来のまま）
        if !item.has_visual_scale() {
            return None;
        }
        let scale = item.effective_visual_scale();
        *placement = scale_placement(placement, frame, scale);
        self.table.stats.scaled += 1;
        Some(scale)
    }

    /// 安全領域の部品があれば、ノードの箱を安全領域の内側へ縮める（W2-1b）。
    ///
    /// # 戻り値
    /// 縮めた（部品が有効で、画面の安全領域があり、ノードが CanvasComponent を持つ）なら true。
    fn apply_safe_area(
        &mut self,
        actor: &Actor,
        frame: &CanvasParentFrame,
        placement: &mut CanvasNodePlacement,
    ) -> bool {
        let Some(safe_world) = self.env.screen.safe_area else { return false };
        if placement.canvas_base.is_none() {
            return false;
        }
        let Some(component) = safe_area_of(actor, self.world) else { return false };
        let Some(box_size) = placement.box_size() else { return false };
        // 祖先の見た目の平行移動（W2-7）の分だけ安全領域も一緒にずらす＝ずらす前の位置で縮める量を求める
        // （横から入ってくる画面・下から出るシートの途中で、中身の箱が画面の端に合わせて縮み直さない）
        let shift = frame.visual_shift;
        let safe_world = if frame.visual_scale != NO_VISUAL_SCALE {
            // 祖先の見た目の倍率（3b）もあるときは同じ写像（p → 倍率 × p + ずらし）で安全領域を写す＝倍率の前の位置・大きさで
            // 縮める量を求め、縮めた箱も倍率どおりに縮む（予測型の戻るで縮めている画面の中身が縮み直さない）
            visual_rect(safe_world, frame.visual_scale, shift)
        } else if shift == [0.0, 0.0] {
            safe_world
        } else {
            CanvasRect {
                min: [safe_world.min[0] + shift[0], safe_world.min[1] + shift[1]],
                max: [safe_world.max[0] + shift[0], safe_world.max[1] + shift[1]],
            }
        };
        let safe_local = world_rect_to_local(&placement.world_rs, safe_world);
        let rect = inset_box(box_size, safe_local, component.edges());
        *placement = resize_box(placement, frame, rect.min, rect.size());
        self.table.stats.safe_areas += 1;
        true
    }
}

/// ワールドの矩形を見た目の写像（p → scale × p + shift。W2 の手直し 3b）で写す【純関数】。
///
/// 負の倍率（裏返し）では端が入れ替わるので、写した 2 つの端の小さい方・大きい方を取り直す。
fn visual_rect(rect: CanvasRect, scale: [f32; 2], shift: [f32; 2]) -> CanvasRect {
    let a = [0, 1].map(|k| scale[k] * rect.min[k] + shift[k]);
    let b = [0, 1].map(|k| scale[k] * rect.max[k] + shift[k]);
    CanvasRect {
        min: [a[0].min(b[0]), a[1].min(b[1])],
        max: [a[0].max(b[0]), a[1].max(b[1])],
    }
}

/// レイアウトに使う CanvasComponent（Canvas スロットのうち最初にコンポーネントが引けたもの）。
///
/// 旧実装の描画・枠・ID 描画・当たり判定と同じ引き方（スロットの有効・無効は見ない）。
pub fn layout_canvas_of<'w>(actor: &Actor, world: &'w World) -> Option<&'w CanvasComponent> {
    actor
        .slots()
        .iter()
        .filter(|s| s.kind == ComponentKind::Canvas)
        .find_map(|s| world.get::<CanvasComponent>(s.entity))
}

/// ノードの有効な CanvasClipComponent（スロットもコンポーネントも有効な最初のもの。W2-4 で形を読むため）。
pub fn clip_component_of<'w>(actor: &Actor, world: &'w World) -> Option<&'w CanvasClipComponent> {
    actor
        .slots()
        .iter()
        .filter(|s| s.kind == ComponentKind::CanvasClip && s.enabled)
        .find_map(|s| world.get::<CanvasClipComponent>(s.entity).filter(|c| c.enabled))
}

/// ノードが子孫を切り抜くか（有効な CanvasClipComponent のスロットを持ち、コンポーネントも有効か）。
///
/// スロットの有効・無効（インスペクタの見出しの切り替え）と、コンポーネントの `enabled`
/// （スクリプトから切り替える）の両方が有効のときだけ切り抜く。
pub fn clips_children(actor: &Actor, world: &World) -> bool {
    actor
        .slots()
        .iter()
        .filter(|s| s.kind == ComponentKind::CanvasClip && s.enabled)
        .any(|s| world.get::<CanvasClipComponent>(s.entity).is_some_and(|c| c.enabled))
}
