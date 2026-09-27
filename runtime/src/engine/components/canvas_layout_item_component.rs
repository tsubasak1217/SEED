// ============================================================
//  canvas_layout_item_component.rs — レイアウトの子の側の指定（W2-1b）
//
//  コンテナ（CanvasStack・CanvasWrap・CanvasGrid）の子に付けて、その子の並べられ方を指定する。
//  付けなくても子は「自分の大きさ・コンテナの揃え」で並ぶ（付けたときだけ上書き）。
//    - ignore_layout … コンテナに無視させる（自分のアンカー・位置のまま。重ねる飾りなど）
//    - flex          … 主軸の余りを分ける重み（CanvasStack のみ。0 = 自分の大きさ）
//    - preferred_*   … 大きさの指定（0 = 中身の大きさ。CanvasComponent・スプライト・テキストの枠から決まる）
//    - min_* / max_* … 大きさの下限・上限（max は 0 = 上限なし）
//    - align_self    … 交差軸の揃えの上書き（Grid はセルの中の置き方の上書き）
//    - fill_*        … 親に合わせる（コンテナの外の子で使う。親の CanvasComponent の領域いっぱいに広げる）
//  大きさの単位はキャンバスの単位（スプライトの幅・高さと同じ。親の累積スケールが掛かる）。
//
//  【データとロジックの分離】このファイルはデータだけ。読むのはレイアウトの走査（canvas_layout/measure.rs・pass.rs）。
//  規則の正典は docs/canvas_camera_rework.md §6.3。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

use super::canvas_layout_params::ItemAlign;

/// レイアウトの子の側の指定（World に置く実体。シリアライズもこの型のまま行う）。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, Default)]
pub struct CanvasLayoutItemComponent {
    /// コンテナに無視させる（並べる対象から外し、自分のアンカー・位置で置く）。
    #[serde(default)]
    pub ignore_layout: bool,
    /// 主軸の余りを分ける重み（CanvasStack のみ。0 = 伸ばさない）。
    #[serde(default)]
    pub flex: f32,
    /// 幅の指定（0 = 中身の大きさ）。
    #[serde(default)]
    pub preferred_width: f32,
    /// 高さの指定（0 = 中身の大きさ）。
    #[serde(default)]
    pub preferred_height: f32,
    /// 幅の下限（0 = 下限なし）。
    #[serde(default)]
    pub min_width: f32,
    /// 高さの下限（0 = 下限なし）。
    #[serde(default)]
    pub min_height: f32,
    /// 幅の上限（0 = 上限なし）。
    #[serde(default)]
    pub max_width: f32,
    /// 高さの上限（0 = 上限なし）。
    #[serde(default)]
    pub max_height: f32,
    /// 交差軸の揃えの上書き（既定 Auto = コンテナに従う）。
    #[serde(default)]
    pub align_self: ItemAlign,
    /// 親の幅に合わせる（コンテナの外で使う。親の CanvasComponent の領域の幅いっぱい）。
    #[serde(default)]
    pub fill_width: bool,
    /// 親の高さに合わせる（同上）。
    #[serde(default)]
    pub fill_height: bool,
}

/// シリアライズ用データ（実体と同じ型）。
pub type CanvasLayoutItemComponentData = CanvasLayoutItemComponent;

impl CanvasLayoutItemComponent {
    /// シリアライズ用データから実体を作る。
    pub fn from_data(data: CanvasLayoutItemComponentData) -> Self {
        data
    }

    /// 実体からシリアライズ用データを作る。
    pub fn to_data(&self) -> CanvasLayoutItemComponentData {
        self.clone()
    }

    /// 大きさの指定（軸ごと。0 以下は指定なし＝None）。
    pub fn preferred(&self) -> [Option<f32>; 2] {
        [positive(self.preferred_width), positive(self.preferred_height)]
    }

    /// 大きさの下限（軸ごと。0 以下は 0）。
    pub fn min(&self) -> [f32; 2] {
        [self.min_width.max(0.0), self.min_height.max(0.0)]
    }

    /// 大きさの上限（軸ごと。0 以下は上限なし＝無限大）。
    pub fn max(&self) -> [f32; 2] {
        [
            positive(self.max_width).unwrap_or(f32::INFINITY),
            positive(self.max_height).unwrap_or(f32::INFINITY),
        ]
    }

    /// 親に合わせる軸（幅・高さ）。
    pub fn fill(&self) -> [bool; 2] {
        [self.fill_width, self.fill_height]
    }
}

/// 正の有限値だけを Some にする（0・負・NaN・無限大は「指定なし」）。
fn positive(value: f32) -> Option<f32> {
    (value.is_finite() && value > 0.0).then_some(value)
}

impl Component for CanvasLayoutItemComponent {}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 欄の無い旧データ（`{}`）は既定（何も上書きしない）で読める。
    #[test]
    fn empty_data_defaults() {
        let d: CanvasLayoutItemComponentData = serde_json::from_str("{}").unwrap();
        assert_eq!(d, CanvasLayoutItemComponent::default());
        assert_eq!(d.preferred(), [None, None]);
        assert_eq!(d.max(), [f32::INFINITY, f32::INFINITY]);
    }

    /// 0・負は「指定なし」。上限 0 は上限なし。
    #[test]
    fn zero_means_unspecified() {
        let d = CanvasLayoutItemComponent {
            preferred_width: 120.0,
            preferred_height: -1.0,
            min_width: -5.0,
            max_height: 40.0,
            ..CanvasLayoutItemComponent::default()
        };
        assert_eq!(d.preferred(), [Some(120.0), None]);
        assert_eq!(d.min(), [0.0, 0.0]);
        assert_eq!(d.max(), [f32::INFINITY, 40.0]);
    }

    /// 実体 ⇔ データの往復で値が保たれる。
    #[test]
    fn round_trip() {
        let c = CanvasLayoutItemComponent {
            flex: 2.0,
            align_self: ItemAlign::Center,
            fill_height: true,
            ..CanvasLayoutItemComponent::default()
        };
        let json = serde_json::to_string(&c.to_data()).unwrap();
        let back: CanvasLayoutItemComponentData = serde_json::from_str(&json).unwrap();
        assert_eq!(CanvasLayoutItemComponent::from_data(back), c);
    }
}
