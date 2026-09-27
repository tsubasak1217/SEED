// ============================================================
//  canvas_layout/measure.rs — ノードの「自分の大きさ」を測る（W2-1b。レイアウトの 2 段の計算の 1 段目）
//
//  コンテナが子を並べる前に、子の大きさ（親のローカルの画素）を測る。決め方（軸ごと。先にあるものが勝つ）:
//    1. 決まった大きさ（親のコンテナが伸ばした大きさ）
//    2. CanvasLayoutItem の preferred_*（キャンバスの単位 × 子のサイズ倍率）
//    3. コンテナで、CanvasComponent を持たないか fit の軸 → 中身（子を並べた大きさ ＋ 余白）
//    4. CanvasComponent → キャンバス領域（基準の大きさ × サイズ倍率）
//    5. 最初の有効なスプライト → その大きさ × サイズ倍率
//    6. 最初の有効なテキストの枠（0 より大きい軸だけ）× サイズ倍率
//    7. どれも無ければ 0
//  決まった大きさでない軸は CanvasLayoutItem の下限・上限へ収める。
//
//  【ノード数に比例させる】測った結果は（エンティティ, 決まった大きさ）で覚える。コンテナは伸ばす子を
//  伸ばした後の大きさで 1 回だけ測り（containers/）、走査が子を配置するときは同じ鍵で引く
//  （`container_inner` を測るときと配置するときで共有する）ので、同じノードを同じ条件で 2 度測らない。
//  `measure_calls` は実際に測った回数（表を引いた回数は含まない）で、性能のテストが数える。
// ============================================================

use std::collections::HashMap;

use crate::engine::components::{CanvasTransform, HiddenChildren};
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

use super::containers::spec::{container_of, ContainerSpec};
use super::containers::{clamp_size, Arrangement, Constraint, ItemSpec, AXIS_X, AXIS_Y, UNCONSTRAINED};
use super::lookup::{first_sprite_size, is_hidden, layout_item_of, text_box_size};
use super::pass::layout_canvas_of;
use super::placement::{child_cumul_scale_of, size_scale_of, NO_AUTO_SCALE};

/// 倍率が 0 とみなせる大きさ（箱と矩形の換算で 0 除算しない）。
const SCALE_EPSILON: f32 = 1e-12;

/// 測った結果を覚える鍵（エンティティと、軸ごとの決まった大きさの浮動小数のビット）。
type MeasureKey = (Entity, [Option<u32>; 2]);

/// 決まった大きさを鍵のビットにする（-0.0 と 0.0 も別の鍵。同じ値なら同じビット）。
fn constraint_key(constraint: Constraint) -> [Option<u32>; 2] {
    constraint.map(|c| c.map(f32::to_bits))
}

/// コンテナの「箱の画素 ÷ 自分の矩形の画素」（子の累積スケール ÷ 自分のサイズ倍率）。
///
/// ノードの矩形（親のローカルの画素）と、子が並ぶ箱（ノードのローカルの画素）の換算。
/// スケール 1・既定のスケールモードなら 1。サイズ倍率が 0 の退化した軸は 1 とみなす。
pub fn box_per_rect(child_cumul: [f32; 2], size_scale: [f32; 2]) -> [f32; 2] {
    [0, 1].map(|a| {
        if size_scale[a].abs() > SCALE_EPSILON {
            child_cumul[a] / size_scale[a]
        } else {
            1.0
        }
    })
}

/// 箱の画素をノードの矩形の画素へ戻す（`box_per_rect` の逆。0 の軸はそのまま）。
pub fn rect_from_box(box_px: f32, per_rect: f32) -> f32 {
    if per_rect.abs() > SCALE_EPSILON {
        box_px / per_rect
    } else {
        box_px
    }
}

/// コンテナが子を並べる箱の中身の大きさ（余白を除く。軸ごと。None = 中身に合わせる）【純関数】。
///
/// 測るとき（measure）と走査が配置するとき（pass.rs）で**同じ計算**を通し、測った結果の表を引けるようにする。
///
/// # 引数
/// * `tight`      - ノードの矩形の決まった大きさ（親が伸ばした大きさ、無ければ preferred。親のローカルの画素）
/// * `canvas_box` - CanvasComponent の箱（基準の大きさ × 子の累積スケール。持たなければ None）
/// * `spec`       - コンテナの指定（fit と余白）
/// * `per_rect`   - `box_per_rect`
pub fn container_inner(
    tight: Constraint,
    canvas_box: Option<[f32; 2]>,
    spec: &ContainerSpec,
    per_rect: [f32; 2],
) -> Constraint {
    let pad = spec.padding_sum();
    [AXIS_X, AXIS_Y].map(|a| {
        let box_len = match (tight[a], canvas_box) {
            (Some(t), _) => Some(t * per_rect[a]),
            (None, Some(cb)) if !spec.fit[a] => Some(cb[a]),
            _ => None,
        };
        box_len.map(|b| (b - pad[a]).max(0.0))
    })
}

/// ノードの大きさを測る道具（1 回の走査の間だけ使う。結果を覚える）。
pub struct LayoutMeasurer<'w> {
    /// コンポーネントの置き場。
    world: &'w World,
    /// 測った結果（エンティティ, 決まった大きさ）→ 大きさ。
    cache: HashMap<MeasureKey, [f32; 2]>,
    /// 実際に測った回数（表を引いただけの回数は含まない）。
    pub measure_calls: u64,
}

impl<'w> LayoutMeasurer<'w> {
    /// 空の道具を作る。
    pub fn new(world: &'w World) -> Self {
        Self { world, cache: HashMap::new(), measure_calls: 0 }
    }

    /// ノードの大きさ（親のローカルの画素）を測る。決まった軸はその値を返す。
    ///
    /// # 引数
    /// * `actor`        - ノード（フォルダ以外。CanvasTransform を持たなければ 0）
    /// * `parent_cumul` - 親（コンテナ）の子の累積スケール
    /// * `constraint`   - 軸ごとの決まった大きさ
    pub fn measure(&mut self, actor: &Actor, parent_cumul: [f32; 2], constraint: Constraint) -> [f32; 2] {
        let key = (actor.entity, constraint_key(constraint));
        if let Some(size) = self.cache.get(&key) {
            return *size;
        }
        self.measure_calls += 1;
        let size = self.measure_uncached(actor, parent_cumul, constraint);
        self.cache.insert(key, size);
        size
    }

    /// 測る（表を引かない本体）。
    fn measure_uncached(&mut self, actor: &Actor, parent_cumul: [f32; 2], constraint: Constraint) -> [f32; 2] {
        let world = self.world;
        let Some(transform) = world.get::<CanvasTransform>(actor.entity) else {
            return [0.0; 2];
        };
        let size_scale = size_scale_of(transform, parent_cumul);
        let child_cumul = child_cumul_scale_of(transform, parent_cumul, NO_AUTO_SCALE);
        let item = layout_item_of(actor, world);
        let canvas = layout_canvas_of(actor, world);
        let spec = container_of(actor, world, child_cumul);
        let per_rect = box_per_rect(child_cumul, size_scale);

        // 決まった大きさ（親が伸ばした大きさ、無ければ自分の preferred）
        let preferred = item.map_or([None, None], |it| it.preferred());
        let tight: Constraint =
            [AXIS_X, AXIS_Y].map(|a| constraint[a].or(preferred[a].map(|p| p * size_scale[a])));

        // コンテナの中身（中身で決まる軸があるときだけ並べる）
        let content_rect = spec.as_ref().and_then(|spec| {
            let needs = |a: usize| tight[a].is_none() && (canvas.is_none() || spec.fit[a]);
            if !needs(AXIS_X) && !needs(AXIS_Y) {
                return None;
            }
            let canvas_box = canvas.map(|cc| [cc.width * child_cumul[AXIS_X], cc.height * child_cumul[AXIS_Y]]);
            let inner = container_inner(tight, canvas_box, spec, per_rect);
            let (_, arrangement) = self.arrange_children(actor, spec, child_cumul, inner);
            Some([AXIS_X, AXIS_Y].map(|a| rect_from_box(arrangement.content[a], per_rect[a])))
        });

        let sprite = first_sprite_size(actor, world);
        let text_box = text_box_size(actor, world);
        let mut size = [0.0f32; 2];
        for a in [AXIS_X, AXIS_Y] {
            let from_content = spec.as_ref().is_some_and(|s| canvas.is_none() || s.fit[a]);
            size[a] = if let Some(t) = tight[a] {
                t
            } else if let (true, Some(content)) = (from_content, content_rect) {
                content[a]
            } else if let Some(cc) = canvas {
                [cc.width, cc.height][a] * size_scale[a]
            } else if let Some(sp) = sprite {
                sp[a] * size_scale[a]
            } else if let Some(tb) = text_box[a] {
                tb * size_scale[a]
            } else {
                0.0
            };
            // 親が決めた大きさ以外は、子の側の下限・上限へ収める
            if constraint[a].is_none() {
                if let Some(it) = item {
                    size[a] = clamp_size(size[a], it.min()[a] * size_scale[a], it.max()[a] * size_scale[a]);
                }
            }
        }
        size
    }

    /// コンテナの子を並べる（並べる子の一覧と並べた結果）。
    ///
    /// # 引数
    /// * `actor`       - コンテナのノード
    /// * `spec`        - コンテナの指定
    /// * `child_cumul` - コンテナの子の累積スケール（子の大きさ・下限・上限の換算）
    /// * `inner`       - 箱の中身の大きさ（`container_inner`）
    pub fn arrange_children<'a>(
        &mut self,
        actor: &'a Actor,
        spec: &ContainerSpec,
        child_cumul: [f32; 2],
        inner: Constraint,
    ) -> (Vec<&'a Actor>, Arrangement) {
        let (items, specs) = self.layout_children(actor, spec, child_cumul);
        let arrangement = {
            let mut measure = |i: usize, c: Constraint| self.measure(items[i], child_cumul, c);
            spec.arrange(inner, &specs, &mut measure)
        };
        (items, arrangement)
    }

    /// コンテナが並べる子の一覧と子ごとの指定（フォルダの中の子も並べる。並びは木の順）。
    ///
    /// 並べない子: CanvasTransform を持たない・CanvasLayoutItem.ignore_layout・非表示か無効で「詰める」。
    fn layout_children<'a>(
        &self,
        actor: &'a Actor,
        spec: &ContainerSpec,
        child_cumul: [f32; 2],
    ) -> (Vec<&'a Actor>, Vec<ItemSpec>) {
        let mut items = Vec::new();
        let mut specs = Vec::new();
        self.collect_items(&actor.children, false, spec, child_cumul, &mut items, &mut specs);
        (items, specs)
    }

    /// 子の並びを 1 段たどって、並べる子を積む（フォルダは中へ入る）。
    fn collect_items<'a>(
        &self,
        children: &'a [Actor],
        hidden_ancestor: bool,
        spec: &ContainerSpec,
        child_cumul: [f32; 2],
        items: &mut Vec<&'a Actor>,
        specs: &mut Vec<ItemSpec>,
    ) {
        let world = self.world;
        for child in children {
            let hidden = hidden_ancestor || is_hidden(child);
            if child.is_folder() {
                // フォルダはレイアウト透明: 中の子をこのコンテナの子として並べる
                self.collect_items(&child.children, hidden, spec, child_cumul, items, specs);
                continue;
            }
            let Some(transform) = world.get::<CanvasTransform>(child.entity) else { continue };
            if hidden && spec.hidden == HiddenChildren::Collapse {
                continue;
            }
            let item = layout_item_of(child, world);
            if item.is_some_and(|it| it.ignore_layout) {
                continue;
            }
            let size_scale = size_scale_of(transform, child_cumul);
            let item_spec = item.map_or_else(ItemSpec::default, |it| {
                let (min, max) = (it.min(), it.max());
                ItemSpec {
                    // 伸ばす重みは 1 列に並べるコンテナだけが使う
                    flex: if spec.uses_flex() { it.flex } else { 0.0 },
                    align_self: it.align_self,
                    min: [min[AXIS_X] * size_scale[AXIS_X], min[AXIS_Y] * size_scale[AXIS_Y]],
                    max: [max[AXIS_X] * size_scale[AXIS_X], max[AXIS_Y] * size_scale[AXIS_Y]],
                }
            });
            items.push(child);
            specs.push(item_spec);
        }
    }

    /// 何も決まっていない条件でノードの大きさを測る（親に合わせる子の、合わせない軸の大きさ）。
    pub fn natural_size(&mut self, actor: &Actor, parent_cumul: [f32; 2]) -> [f32; 2] {
        self.measure(actor, parent_cumul, UNCONSTRAINED)
    }
}
