// ============================================================
//  canvas_layout/containers/ — レイアウトのコンテナの並べ方（W2-1b。純関数）
//
//  コンテナ（CanvasStack・CanvasWrap・CanvasGrid）が子を並べる計算だけを置く。World もアクターも読まない:
//  入力は「コンテナの箱の中身の大きさ（軸ごと。決まっていなければ None）」「子ごとの指定（ItemSpec）」
//  「子の大きさを測る関数（measure）」、出力は「子ごとの矩形（LayoutSlot）」と「中身の大きさ」。
//  木への当てはめ（どの子を並べるか・単位の換算・結果の反映）はレイアウトの走査（pass.rs・measure.rs）が行う。
//
//  【座標】すべてコンテナのローカル座標の画素（コンテナの箱の左上が原点・Y 下向き）。余白・間隔も画素へ換算済み。
//
//  【2 段の計算を 1 回の走査の中で】子の大きさが決まってから親が並べる（測る → 並べる）。測る関数は
//  「軸ごとの決まった大きさ（Constraint）」を受け取り、決まっていない軸の大きさを返す。並べ方は、伸ばす子には
//  伸ばした後の大きさを決まった大きさとして渡して**1 回だけ**測る（測り直さない）。走査は測った結果を
//  （エンティティ, 決まった大きさ）で覚えるので、子を配置するときの再計算は表を引くだけになる（ノード数に比例）。
//
//  構成: mod.rs（共通の型と小さな計算）・stack.rs・wrap.rs・grid.rs・spec.rs（コンポーネント → 並べ方の指定）
// ============================================================

pub mod grid;
pub mod spec;
pub mod stack;
pub mod wrap;

#[cfg(test)]
mod tests;

use crate::engine::components::{CanvasPadding, ItemAlign, LayoutDirection, MainAlign};

/// X 軸の添字。
pub const AXIS_X: usize = 0;
/// Y 軸の添字。
pub const AXIS_Y: usize = 1;

/// 軸ごとの決まった大きさ（None = 決まっていない＝中身の大きさで測る）。
pub type Constraint = [Option<f32>; 2];

/// 何も決まっていない（中身の大きさで測る）。
pub const UNCONSTRAINED: Constraint = [None, None];

/// 子の大きさを測る関数（子の添字・決まった大きさ → 大きさ）。決まった軸はその値をそのまま返すこと。
pub type MeasureFn<'m> = dyn FnMut(usize, Constraint) -> [f32; 2] + 'm;

/// コンテナが子へ割り当てた矩形（コンテナのローカル座標の画素）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct LayoutSlot {
    /// 左上。
    pub origin: [f32; 2],
    /// 大きさ。
    pub size: [f32; 2],
    /// コンテナが大きさを決めた軸（伸ばした・セルいっぱい）。その軸では子のスプライトを矩形の大きさで描く。
    pub fill: [bool; 2],
}

/// 並べる前にコンテナが知っている子ごとの指定（CanvasLayoutItem を画素へ換算したもの）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ItemSpec {
    /// 主軸の余りを分ける重み（0 = 伸ばさない）。
    pub flex: f32,
    /// 揃えの上書き。
    pub align_self: ItemAlign,
    /// 大きさの下限（画素）。
    pub min: [f32; 2],
    /// 大きさの上限（画素。上限なしは無限大）。
    pub max: [f32; 2],
}

impl Default for ItemSpec {
    fn default() -> Self {
        Self { flex: 0.0, align_self: ItemAlign::Auto, min: [0.0; 2], max: [f32::INFINITY; 2] }
    }
}

impl ItemSpec {
    /// 軸の大きさを下限・上限へ収める。
    pub fn clamp(&self, axis: usize, value: f32) -> f32 {
        clamp_size(value, self.min[axis], self.max[axis])
    }

    /// 伸ばすか（重みが正の有限値）。
    pub fn is_flex(&self) -> bool {
        self.flex.is_finite() && self.flex > 0.0
    }
}

/// 並べた結果。
#[derive(Clone, Debug, PartialEq, Default)]
pub struct Arrangement {
    /// 中身の大きさ（余白を含む。コンテナの箱の大きさを中身に合わせるときの値）。
    pub content: [f32; 2],
    /// 子ごとの矩形（添字は ItemSpec の並び）。
    pub slots: Vec<LayoutSlot>,
}

/// 余白（画素へ換算済み）の軸ごとの和（左右・上下）。
pub fn padding_sum(padding: &CanvasPadding) -> [f32; 2] {
    [padding.left + padding.right, padding.top + padding.bottom]
}

/// 余白の左上（箱の原点から中身の原点までのずれ）。
pub fn padding_origin(padding: &CanvasPadding) -> [f32; 2] {
    [padding.left, padding.top]
}

/// 向きから（主軸, 交差軸）の添字を返す。
pub fn axes(direction: LayoutDirection) -> (usize, usize) {
    match direction {
        LayoutDirection::Horizontal => (AXIS_X, AXIS_Y),
        LayoutDirection::Vertical => (AXIS_Y, AXIS_X),
    }
}

/// 大きさを下限・上限へ収める（上限が下限より小さければ下限を優先。負にはしない）。
pub fn clamp_size(value: f32, min: f32, max: f32) -> f32 {
    let v = if value.is_finite() { value } else { 0.0 };
    v.min(max).max(min).max(0.0)
}

/// 主軸の揃えで、余りを「先頭の空き」と「子の間に足す量」へ分ける【純関数】。
///
/// # 引数
/// * `align` - 揃え
/// * `free`  - 余り（負ならはみ出し。はみ出しは先頭寄せ以外でも端へ寄せない＝先頭から並べる）
/// * `count` - 並べる子の数
///
/// # 戻り値
/// (先頭の空き, 子の間に足す量)
pub fn distribute(align: MainAlign, free: f32, count: usize) -> (f32, f32) {
    let free = free.max(0.0);
    if count == 0 {
        return (0.0, 0.0);
    }
    let n = count as f32;
    match align {
        MainAlign::Start => (0.0, 0.0),
        MainAlign::Center => (free * 0.5, 0.0),
        MainAlign::End => (free, 0.0),
        MainAlign::SpaceBetween => {
            if count > 1 {
                (0.0, free / (n - 1.0))
            } else {
                (0.0, 0.0)
            }
        }
        MainAlign::SpaceAround => (free / n * 0.5, free / n),
        MainAlign::SpaceEvenly => (free / (n + 1.0), free / (n + 1.0)),
    }
}

/// 交差軸の揃えで、子の交差軸の位置（中身の原点から）を求める【純関数】。
///
/// # 引数
/// * `align` - 揃え（Stretch は呼び出し側が大きさを伸ばしてから呼ぶので先頭と同じ）
/// * `avail` - 交差軸に使える長さ
/// * `size`  - 子の交差軸の大きさ
pub fn cross_offset(align: crate::engine::components::CrossAlign, avail: f32, size: f32) -> f32 {
    use crate::engine::components::CrossAlign;
    match align {
        CrossAlign::Start | CrossAlign::Stretch => 0.0,
        CrossAlign::Center => (avail - size) * 0.5,
        CrossAlign::End => avail - size,
    }
}

/// 軸 `axis` の値を持つ 2 要素の配列を作る（主軸・交差軸 → X・Y の並べ替え）。
pub fn on_axes(main_axis: usize, main: f32, cross: f32) -> [f32; 2] {
    let mut out = [0.0; 2];
    out[main_axis] = main;
    out[1 - main_axis] = cross;
    out
}
