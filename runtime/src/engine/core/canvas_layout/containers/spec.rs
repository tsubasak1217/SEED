// ============================================================
//  containers/spec.rs — コンテナのコンポーネント → 並べ方の指定（画素へ換算）
//
//  ノードが持つコンテナのコンポーネント（CanvasStack・CanvasWrap・CanvasGrid）のうち、
//  スロットとコンポーネントの両方が有効な**最初の 1 つ**を並べ方の指定にする（複数付けたときはスロットの順で先のもの）。
//  間隔・余白・セルの最小幅はキャンバスの単位なので、子の位置と同じく「コンテナの子の累積スケール」を掛けて画素にする
//  （子のアンカーと同じ換算。auto_scale のルートでもビューポートの画素になる）。
// ============================================================

use crate::engine::components::{
    CanvasGridComponent, CanvasPadding, CanvasStackComponent, CanvasWrapComponent, ComponentKind,
    HiddenChildren,
};
use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;

use super::grid::{arrange_grid, GridParams};
use super::stack::{arrange_stack, StackParams};
use super::wrap::{arrange_wrap, WrapParams};
use super::{axes, padding_sum, Arrangement, Constraint, ItemSpec, MeasureFn, AXIS_X, AXIS_Y};

/// コンテナの種類と並べ方（画素へ換算済み）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ContainerKind {
    /// 1 列に並べる。
    Stack(StackParams),
    /// 折り返して並べる。
    Wrap(WrapParams),
    /// 格子に並べる。
    Grid(GridParams),
}

/// ノードのコンテナの指定。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ContainerSpec {
    /// 種類と並べ方。
    pub kind: ContainerKind,
    /// 中身に合わせる軸（CanvasComponent を持つコンテナのときだけ効く）。
    pub fit: [bool; 2],
    /// 非表示・無効の子の扱い。
    pub hidden: HiddenChildren,
}

impl ContainerSpec {
    /// 余白（画素）の軸ごとの和。
    pub fn padding_sum(&self) -> [f32; 2] {
        padding_sum(match &self.kind {
            ContainerKind::Stack(p) => &p.padding,
            ContainerKind::Wrap(p) => &p.padding,
            ContainerKind::Grid(p) => &p.padding,
        })
    }

    /// 伸ばす重み（flex）を使う種類か（CanvasStack だけ）。
    pub fn uses_flex(&self) -> bool {
        matches!(self.kind, ContainerKind::Stack(_))
    }

    /// 並べる【純関数への振り分け】。
    ///
    /// # 引数
    /// * `inner`   - 箱の中身の大きさ（余白を除く。None = 中身に合わせる）
    /// * `items`   - 子ごとの指定
    /// * `measure` - 子の大きさを測る関数
    pub fn arrange(&self, inner: Constraint, items: &[ItemSpec], measure: &mut MeasureFn<'_>) -> Arrangement {
        match &self.kind {
            ContainerKind::Stack(p) => arrange_stack(p, inner, items, measure),
            ContainerKind::Wrap(p) => arrange_wrap(p, inner, items, measure),
            ContainerKind::Grid(p) => arrange_grid(p, inner, items, measure),
        }
    }
}

/// 余白（キャンバスの単位）を画素へ換算する（左右は X、上下は Y の倍率）。
fn padding_px(padding: &CanvasPadding, scale: [f32; 2]) -> CanvasPadding {
    CanvasPadding {
        left: padding.left * scale[AXIS_X],
        top: padding.top * scale[AXIS_Y],
        right: padding.right * scale[AXIS_X],
        bottom: padding.bottom * scale[AXIS_Y],
    }
}

/// セルの縦横比（キャンバスの単位の幅 ÷ 高さ）を画素の比へ換算する。
///
/// 縦横の倍率が違う（旧来の auto_scale のルートの下）とき、セルはキャンバスの単位で指定の比になり、
/// 他の部品と同じだけ画面で伸び縮みする。0 以下（行の高さは中身）と倍率が壊れているときはそのまま。
fn aspect_ratio_px(ratio: f32, px_scale: [f32; 2]) -> f32 {
    let (sx, sy) = (px_scale[AXIS_X], px_scale[AXIS_Y]);
    if ratio > 0.0 && sx.is_finite() && sy.is_finite() && sx > 0.0 && sy > 0.0 {
        ratio * sx / sy
    } else {
        ratio
    }
}

/// ノードのコンテナの指定を読む（スロットとコンポーネントの両方が有効な最初の 1 つ。無ければ None）。
///
/// # 引数
/// * `actor`    - ノード
/// * `world`    - コンポーネントの置き場
/// * `px_scale` - キャンバスの単位 → 画素の倍率（コンテナの子の累積スケール）
pub fn container_of(actor: &Actor, world: &World, px_scale: [f32; 2]) -> Option<ContainerSpec> {
    actor.slots().iter().filter(|s| s.enabled).find_map(|slot| match slot.kind {
        ComponentKind::CanvasStack => world
            .get::<CanvasStackComponent>(slot.entity)
            .filter(|c| c.enabled)
            .map(|c| {
                let (main, _) = axes(c.direction);
                ContainerSpec {
                    kind: ContainerKind::Stack(StackParams {
                        direction: c.direction,
                        spacing: c.spacing * px_scale[main],
                        padding: padding_px(&c.padding, px_scale),
                        main_align: c.main_align,
                        cross_align: c.cross_align,
                        reverse: c.reverse,
                    }),
                    fit: [c.fit_width, c.fit_height],
                    hidden: c.hidden_children,
                }
            }),
        ComponentKind::CanvasWrap => world
            .get::<CanvasWrapComponent>(slot.entity)
            .filter(|c| c.enabled)
            .map(|c| {
                let (main, cross) = axes(c.direction);
                ContainerSpec {
                    kind: ContainerKind::Wrap(WrapParams {
                        direction: c.direction,
                        spacing: c.spacing * px_scale[main],
                        run_spacing: c.run_spacing * px_scale[cross],
                        padding: padding_px(&c.padding, px_scale),
                        main_align: c.main_align,
                        cross_align: c.cross_align,
                        run_align: c.run_align,
                    }),
                    fit: [c.fit_width, c.fit_height],
                    hidden: c.hidden_children,
                }
            }),
        ComponentKind::CanvasGrid => world
            .get::<CanvasGridComponent>(slot.entity)
            .filter(|c| c.enabled)
            .map(|c| ContainerSpec {
                kind: ContainerKind::Grid(GridParams {
                    columns: c.columns,
                    cell_min_width: c.cell_min_width * px_scale[AXIS_X],
                    cell_aspect_ratio: aspect_ratio_px(c.cell_aspect_ratio, px_scale),
                    spacing: [c.spacing_x * px_scale[AXIS_X], c.spacing_y * px_scale[AXIS_Y]],
                    padding: padding_px(&c.padding, px_scale),
                    cell_align: c.cell_align,
                }),
                fit: [c.fit_width, c.fit_height],
                hidden: c.hidden_children,
            }),
        _ => None,
    })
}
