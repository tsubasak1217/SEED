// ============================================================
//  canvas_wrap_component.rs — 折り返して並べるコンテナ（W2-1b。チップの 2 段など）
//
//  2D キャンバスのノードに付けると、子を主軸（既定 横）へ並べ、領域の幅（縦なら高さ）に収まらなくなったら
//  次の行（列）へ折り返す。子は自分の大きさのまま（伸ばさない。交差軸の Stretch は行の高さまで伸ばす）。
//  領域が決まっていない（CanvasComponent も親のコンテナの指定も無い・fit_width）軸では折り返さない。
//
//  【データとロジックの分離】このファイルはデータだけ。並べる計算は engine/core/canvas_layout/containers/wrap.rs。
//  規則の正典は docs/canvas_camera_rework.md §6.3。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

use super::canvas_layout_params::{
    default_true, CanvasPadding, CrossAlign, HiddenChildren, LayoutDirection, MainAlign,
};

/// serde の既定値: 横に並べて下へ折り返す（チップの並びの向き）。
fn default_wrap_direction() -> LayoutDirection {
    LayoutDirection::Horizontal
}

/// 折り返して並べるコンテナ（World に置く実体。シリアライズもこの型のまま行う）。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct CanvasWrapComponent {
    /// 有効フラグ。false の間は並べない。スクリプトから切り替えられる。
    #[serde(default = "default_true")]
    pub enabled: bool,
    /// 主軸の向き（既定 横 = 左から右へ並べ、下へ折り返す）。
    #[serde(default = "default_wrap_direction")]
    pub direction: LayoutDirection,
    /// 同じ行の中の子の間隔（キャンバスの単位）。
    #[serde(default)]
    pub spacing: f32,
    /// 行（列）の間隔（キャンバスの単位）。
    #[serde(default)]
    pub run_spacing: f32,
    /// 内側の余白（キャンバスの単位）。
    #[serde(default)]
    pub padding: CanvasPadding,
    /// 行の中の揃え（主軸。既定 先頭）。
    #[serde(default)]
    pub main_align: MainAlign,
    /// 行の中の交差軸の揃え（既定 先頭。Stretch は行の高さまで伸ばす）。
    #[serde(default)]
    pub cross_align: CrossAlign,
    /// 行全体の揃え（交差軸。領域の高さが決まっているときに行の塊をどこへ置くか。既定 先頭）。
    #[serde(default)]
    pub run_align: MainAlign,
    /// 幅を中身に合わせる（CanvasComponent を持つときだけ意味がある）。
    #[serde(default)]
    pub fit_width: bool,
    /// 高さを中身に合わせる（同上。折り返した行の数に合わせて伸びる）。
    #[serde(default)]
    pub fit_height: bool,
    /// 非表示・無効の子の扱い（既定 詰める）。
    #[serde(default)]
    pub hidden_children: HiddenChildren,
}

/// シリアライズ用データ（実体と同じ型）。
pub type CanvasWrapComponentData = CanvasWrapComponent;

impl Default for CanvasWrapComponent {
    fn default() -> Self {
        Self {
            enabled: default_true(),
            direction: default_wrap_direction(),
            spacing: 0.0,
            run_spacing: 0.0,
            padding: CanvasPadding::default(),
            main_align: MainAlign::default(),
            cross_align: CrossAlign::default(),
            run_align: MainAlign::default(),
            fit_width: false,
            fit_height: false,
            hidden_children: HiddenChildren::default(),
        }
    }
}

impl CanvasWrapComponent {
    /// シリアライズ用データから実体を作る。
    pub fn from_data(data: CanvasWrapComponentData) -> Self {
        data
    }

    /// 実体からシリアライズ用データを作る。
    pub fn to_data(&self) -> CanvasWrapComponentData {
        self.clone()
    }
}

impl Component for CanvasWrapComponent {}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 欄の無い旧データ（`{}`）は既定（有効・横）で読める。
    #[test]
    fn empty_data_defaults() {
        let d: CanvasWrapComponentData = serde_json::from_str("{}").unwrap();
        assert_eq!(d, CanvasWrapComponent::default());
        assert_eq!(d.direction, LayoutDirection::Horizontal);
    }

    /// 実体 ⇔ データの往復で値が保たれる。
    #[test]
    fn round_trip() {
        let c = CanvasWrapComponent {
            spacing: 4.0,
            run_spacing: 6.0,
            run_align: MainAlign::End,
            fit_height: true,
            ..CanvasWrapComponent::default()
        };
        let json = serde_json::to_string(&c.to_data()).unwrap();
        let back: CanvasWrapComponentData = serde_json::from_str(&json).unwrap();
        assert_eq!(CanvasWrapComponent::from_data(back), c);
    }
}
