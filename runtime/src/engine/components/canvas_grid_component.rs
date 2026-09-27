// ============================================================
//  canvas_grid_component.rs — 格子に並べるコンテナ（W2-1b。月の 3×4・庭の 8×6 など）
//
//  2D キャンバスのノードに付けると、子を左上から行優先で格子のセルへ並べる。
//    - 列数: `columns` が 1 以上ならその数。0 なら「セルの最小幅」から領域の幅に入るだけの列数（自動）
//    - セルの幅: 領域の幅を列数で等分（間隔を除く）。領域の幅が決まっていなければセルの最小幅
//    - セルの高さ: セルの幅 ÷ 縦横比（`cell_aspect_ratio` = 幅 ÷ 高さ。1 で正方形）。0 なら行ごとに中身の最大の高さ
//    - 子のセルの中の置き方: `cell_align`（既定 Stretch = セルいっぱい）。子の CanvasLayoutItem.align_self で上書き
//
//  【データとロジックの分離】このファイルはデータだけ。並べる計算は engine/core/canvas_layout/containers/grid.rs。
//  規則の正典は docs/canvas_camera_rework.md §6.3。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

use super::canvas_layout_params::{default_true, CanvasPadding, CrossAlign, HiddenChildren};

/// serde の既定値: 列数（固定の 3 列。0 にすると自動）。
fn default_columns() -> u32 {
    3
}

/// serde の既定値: 自動の列数に使うセルの最小幅（キャンバスの単位）。
fn default_cell_min_width() -> f32 {
    100.0
}

/// serde の既定値: セルの縦横比（幅 ÷ 高さ。1 = 正方形）。
fn default_cell_aspect_ratio() -> f32 {
    1.0
}

/// serde の既定値: セルの中の子の置き方（セルいっぱい）。
fn default_cell_align() -> CrossAlign {
    CrossAlign::Stretch
}

/// 格子に並べるコンテナ（World に置く実体。シリアライズもこの型のまま行う）。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct CanvasGridComponent {
    /// 有効フラグ。false の間は並べない。スクリプトから切り替えられる。
    #[serde(default = "default_true")]
    pub enabled: bool,
    /// 列数（1 以上で固定。0 でセルの最小幅から自動）。
    #[serde(default = "default_columns")]
    pub columns: u32,
    /// 自動の列数に使うセルの最小幅（キャンバスの単位）。領域の幅が決まらないときのセルの幅にも使う。
    #[serde(default = "default_cell_min_width")]
    pub cell_min_width: f32,
    /// セルの縦横比（幅 ÷ 高さ）。0 以下なら行の高さは中身（その行の子の最大の高さ）。
    #[serde(default = "default_cell_aspect_ratio")]
    pub cell_aspect_ratio: f32,
    /// 列の間隔（キャンバスの単位）。
    #[serde(default)]
    pub spacing_x: f32,
    /// 行の間隔（キャンバスの単位）。
    #[serde(default)]
    pub spacing_y: f32,
    /// 内側の余白（キャンバスの単位）。
    #[serde(default)]
    pub padding: CanvasPadding,
    /// セルの中の子の置き方（縦横とも。既定 Stretch = セルいっぱい）。
    #[serde(default = "default_cell_align")]
    pub cell_align: CrossAlign,
    /// 幅を中身に合わせる（CanvasComponent を持つときだけ意味がある）。
    #[serde(default)]
    pub fit_width: bool,
    /// 高さを中身に合わせる（行の数に合わせて伸びる）。
    #[serde(default)]
    pub fit_height: bool,
    /// 非表示・無効の子の扱い（既定 詰める）。
    #[serde(default)]
    pub hidden_children: HiddenChildren,
}

/// シリアライズ用データ（実体と同じ型）。
pub type CanvasGridComponentData = CanvasGridComponent;

impl Default for CanvasGridComponent {
    fn default() -> Self {
        Self {
            enabled: default_true(),
            columns: default_columns(),
            cell_min_width: default_cell_min_width(),
            cell_aspect_ratio: default_cell_aspect_ratio(),
            spacing_x: 0.0,
            spacing_y: 0.0,
            padding: CanvasPadding::default(),
            cell_align: default_cell_align(),
            fit_width: false,
            fit_height: false,
            hidden_children: HiddenChildren::default(),
        }
    }
}

impl CanvasGridComponent {
    /// シリアライズ用データから実体を作る。
    pub fn from_data(data: CanvasGridComponentData) -> Self {
        data
    }

    /// 実体からシリアライズ用データを作る。
    pub fn to_data(&self) -> CanvasGridComponentData {
        self.clone()
    }
}

impl Component for CanvasGridComponent {}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 欄の無い旧データ（`{}`）は既定（3 列・正方形・セルいっぱい）で読める。
    #[test]
    fn empty_data_defaults() {
        let d: CanvasGridComponentData = serde_json::from_str("{}").unwrap();
        assert_eq!(d, CanvasGridComponent::default());
        assert_eq!(d.columns, 3);
        assert_eq!(d.cell_align, CrossAlign::Stretch);
    }

    /// 実体 ⇔ データの往復で値が保たれる。
    #[test]
    fn round_trip() {
        let c = CanvasGridComponent {
            columns: 0,
            cell_min_width: 48.0,
            cell_aspect_ratio: 0.0,
            spacing_x: 2.0,
            spacing_y: 3.0,
            fit_height: true,
            ..CanvasGridComponent::default()
        };
        let json = serde_json::to_string(&c.to_data()).unwrap();
        let back: CanvasGridComponentData = serde_json::from_str(&json).unwrap();
        assert_eq!(CanvasGridComponent::from_data(back), c);
    }
}
