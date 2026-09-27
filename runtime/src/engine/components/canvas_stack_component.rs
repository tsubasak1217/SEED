// ============================================================
//  canvas_stack_component.rs — 縦・横に並べるコンテナ（W2-1b。UI のレイアウト部品）
//
//  2D キャンバスのノードに付けると、子（フォルダの中の子も含む）を縦か横に 1 列に並べる。
//  子の位置は**コンテナが決める**（子のアンカー・位置は使わない。規則の正典は
//  docs/canvas_camera_rework.md §6.3）。子の大きさは「自分の大きさ」（CanvasComponent → 最初のスプライト →
//  テキストの枠 → CanvasLayoutItem の指定）か、「伸ばす」（CanvasLayoutItem.flex の重みで主軸の余りを分ける・
//  交差軸の Stretch）。
//
//  【領域】ノードに CanvasComponent があればそのキャンバス領域の中に並べる（fit_width / fit_height で
//  その軸を中身に合わせられる）。無ければ中身に合わせた大きさになる（親のコンテナが伸ばせばその大きさ）。
//
//  【データとロジックの分離】このファイルはデータだけ。並べる計算は engine/core/canvas_layout/containers/stack.rs、
//  木への当てはめはレイアウトの走査（canvas_layout/pass.rs）。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

use super::canvas_layout_params::{
    default_true, CanvasPadding, CrossAlign, HiddenChildren, LayoutDirection, MainAlign,
};

/// 縦・横に並べるコンテナ（World に置く実体。シリアライズもこの型のまま行う）。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct CanvasStackComponent {
    /// 有効フラグ。false の間は並べない（子は自分のアンカー・位置に戻る）。スクリプトから切り替えられる。
    #[serde(default = "default_true")]
    pub enabled: bool,
    /// 並べる向き（既定 縦）。
    #[serde(default)]
    pub direction: LayoutDirection,
    /// 子の間隔（キャンバスの単位）。
    #[serde(default)]
    pub spacing: f32,
    /// 内側の余白（キャンバスの単位）。
    #[serde(default)]
    pub padding: CanvasPadding,
    /// 主軸の揃え（既定 先頭）。
    #[serde(default)]
    pub main_align: MainAlign,
    /// 交差軸の揃え（既定 先頭。Stretch は交差軸いっぱいに伸ばす）。
    #[serde(default)]
    pub cross_align: CrossAlign,
    /// 逆順に並べる（最後の子が先頭に来る）。
    #[serde(default)]
    pub reverse: bool,
    /// 幅を中身に合わせる（CanvasComponent を持つときだけ意味がある。持たなければ常に中身に合わせる）。
    #[serde(default)]
    pub fit_width: bool,
    /// 高さを中身に合わせる（同上）。
    #[serde(default)]
    pub fit_height: bool,
    /// 非表示・無効の子の扱い（既定 詰める）。
    #[serde(default)]
    pub hidden_children: HiddenChildren,
}

/// シリアライズ用データ（実体と同じ型。純データのコンポーネントなので分けない）。
pub type CanvasStackComponentData = CanvasStackComponent;

impl Default for CanvasStackComponent {
    fn default() -> Self {
        Self {
            enabled: default_true(),
            direction: LayoutDirection::default(),
            spacing: 0.0,
            padding: CanvasPadding::default(),
            main_align: MainAlign::default(),
            cross_align: CrossAlign::default(),
            reverse: false,
            fit_width: false,
            fit_height: false,
            hidden_children: HiddenChildren::default(),
        }
    }
}

impl CanvasStackComponent {
    /// シリアライズ用データから実体を作る。
    pub fn from_data(data: CanvasStackComponentData) -> Self {
        data
    }

    /// 実体からシリアライズ用データを作る。
    pub fn to_data(&self) -> CanvasStackComponentData {
        self.clone()
    }
}

impl Component for CanvasStackComponent {}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 欄の無い旧データ（`{}`）は既定（有効・縦・先頭）で読める。
    #[test]
    fn empty_data_defaults() {
        let d: CanvasStackComponentData = serde_json::from_str("{}").unwrap();
        assert_eq!(d, CanvasStackComponent::default());
        assert!(d.enabled);
        assert_eq!(d.direction, LayoutDirection::Vertical);
    }

    /// 実体 ⇔ データの往復で値が保たれる。
    #[test]
    fn round_trip() {
        let c = CanvasStackComponent {
            direction: LayoutDirection::Horizontal,
            spacing: 8.0,
            padding: CanvasPadding { left: 1.0, top: 2.0, right: 3.0, bottom: 4.0 },
            main_align: MainAlign::SpaceBetween,
            cross_align: CrossAlign::Stretch,
            reverse: true,
            fit_height: true,
            hidden_children: HiddenChildren::KeepSpace,
            ..CanvasStackComponent::default()
        };
        let json = serde_json::to_string(&c.to_data()).unwrap();
        let back: CanvasStackComponentData = serde_json::from_str(&json).unwrap();
        assert_eq!(CanvasStackComponent::from_data(back), c);
    }
}
