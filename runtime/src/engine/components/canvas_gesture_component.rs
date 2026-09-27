// ============================================================
//  canvas_gesture_component.rs — ジェスチャーを受けるノード（W2-2。W2-P3「ジェスチャーの調停」）
//
//  2D キャンバスのノードに付けると、そのノードが指（ポインタ）ごとのジェスチャーアリーナに参加する。
//  受けたいジェスチャー（タップ・長押し・ドラッグ・フリック）を旗で選び、ドラッグの軸・押下の見た目の
//  イベント（PressDown / PressCancel / PressUp）の有無・ヒット領域の最小の大きさ（dp）を持つ。
//    - ヒット領域: CanvasComponent があればキャンバス領域、無ければ最初の有効な Sprite の矩形
//      （切り抜きの矩形と同じ選び方）。見た目が最小の大きさより小さければ、中心をそろえて広げる（既定 48 dp）
//    - 旗をすべて外したノードは「遮る」だけのノードになる（後ろのノードへ指を渡さない。ダイアログの板など）
//    - 付けていないノードはアリーナに参加しない（従来の OnPointer* の振る舞いは変わらない）
//
//  【データとロジックの分離】このファイルはデータだけ。判定は engine/core/input/gesture/（アリーナ・認識器）、
//  木からの当たり判定の材料づくりと配達は app/gesture_scene.rs・app/gesture_events.rs。
//  規則の正典は docs/input_gestures.md。
//
//  【スクリプトとの受け渡し】列挙（ドラッグの軸）はスクリプト（C# の SEED.GestureDragAxis）と添字で受け渡す
//  （canvas_layout_params.rs の IndexedEnum。並びを変えるとスクリプトの値が変わるので、足すときは末尾へ）。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

use super::canvas_layout_params::{default_true, IndexedEnum};

/// ヒット領域の最小の大きさの既定（dp）。出典: Material Design・Android のアクセシビリティの最小の押せる大きさ 48 dp。
pub const DEFAULT_MIN_HIT_SIZE_DP: f32 = 48.0;

/// ヒット領域の最小の大きさの既定を返す（serde の default）。
fn default_min_hit_size_dp() -> f32 {
    DEFAULT_MIN_HIT_SIZE_DP
}

/// ドラッグの軸。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum GestureDragAxis {
    /// 全方向（押した位置からの距離で始まる。スライダの 2 次元版・並べ替え・自由な移動）。
    #[default]
    Any,
    /// 横だけ（|dx| で始まる。横スクロール・左スワイプの操作・横のスライダ）。
    Horizontal,
    /// 縦だけ（|dy| で始まる。縦スクロール・下へ引いて閉じるシート）。
    Vertical,
}

impl IndexedEnum for GestureDragAxis {
    const ALL: &'static [Self] = &[Self::Any, Self::Horizontal, Self::Vertical];
}

/// ジェスチャーを受けるノード（World に置く実体。シリアライズもこの型のまま行う）。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct CanvasGestureComponent {
    /// 有効フラグ。false の間はアリーナに参加しない（当たり判定の候補にもならない＝後ろのノードへ届く）。
    #[serde(default = "default_true")]
    pub enabled: bool,
    /// タップを受ける（押して・動かず・離す）。
    #[serde(default = "default_true")]
    pub tap: bool,
    /// 長押しを受ける（一定時間・動いたら不成立）。
    #[serde(default)]
    pub long_press: bool,
    /// ドラッグを受ける（DragStart / DragUpdate / DragEnd）。
    #[serde(default)]
    pub drag: bool,
    /// フリックを受ける（離した時点の速度。ドラッグを受けなくても、指を取るためのドラッグの認識器が参加する）。
    #[serde(default)]
    pub fling: bool,
    /// ドラッグ・フリックの軸。
    #[serde(default)]
    pub drag_axis: GestureDragAxis,
    /// 押下の見た目のイベント（PressDown / PressCancel / PressUp）を受ける（タップ・長押しを受けるノードだけ）。
    #[serde(default = "default_true")]
    pub press_feedback: bool,
    /// ヒット領域の最小の大きさ（dp。縦横それぞれ。見た目がこれより小さいと中心をそろえて広げる。0 = 広げない）。
    #[serde(default = "default_min_hit_size_dp")]
    pub min_hit_size_dp: f32,
}

/// シリアライズ用データ（実体と同じ型）。
pub type CanvasGestureComponentData = CanvasGestureComponent;

impl Default for CanvasGestureComponent {
    /// 既定はボタン（タップと押下の見た目。48 dp）。
    fn default() -> Self {
        Self {
            enabled: default_true(),
            tap: default_true(),
            long_press: false,
            drag: false,
            fling: false,
            drag_axis: GestureDragAxis::default(),
            press_feedback: default_true(),
            min_hit_size_dp: DEFAULT_MIN_HIT_SIZE_DP,
        }
    }
}

impl CanvasGestureComponent {
    /// シリアライズ用データから実体を作る。
    pub fn from_data(data: CanvasGestureComponentData) -> Self {
        data
    }

    /// 実体からシリアライズ用データを作る。
    pub fn to_data(&self) -> CanvasGestureComponentData {
        self.clone()
    }

    /// タップか長押しを受ける（押下の見た目の対象になりうる）か。
    pub fn wants_press(&self) -> bool {
        self.tap || self.long_press
    }

    /// 指を取るドラッグの認識器が要るか（ドラッグかフリックを受ける）。
    pub fn wants_drag_recognizer(&self) -> bool {
        self.drag || self.fling
    }

    /// ヒット領域の最小の大きさ（dp。負・NaN は 0＝広げない）。
    pub fn min_hit_size(&self) -> f32 {
        if self.min_hit_size_dp.is_finite() && self.min_hit_size_dp > 0.0 { self.min_hit_size_dp } else { 0.0 }
    }
}

impl Component for CanvasGestureComponent {}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 欄の無い旧データ（`{}`）は既定（有効・タップ・押下の見た目・48 dp）で読める。
    #[test]
    fn empty_data_defaults() {
        let d: CanvasGestureComponentData = serde_json::from_str("{}").unwrap();
        assert_eq!(d, CanvasGestureComponent::default());
        assert!(d.wants_press() && !d.wants_drag_recognizer());
        assert_eq!(d.min_hit_size(), 48.0);
    }

    /// 実体 ⇔ データの往復（列挙は snake_case）と、列挙の添字。
    #[test]
    fn round_trip_and_axis_index() {
        let c = CanvasGestureComponent {
            tap: false,
            drag: true,
            drag_axis: GestureDragAxis::Vertical,
            min_hit_size_dp: -1.0,
            ..CanvasGestureComponent::default()
        };
        let json = serde_json::to_string(&c.to_data()).unwrap();
        assert!(json.contains(r#""drag_axis":"vertical""#), "{json}");
        let back: CanvasGestureComponentData = serde_json::from_str(&json).unwrap();
        assert_eq!(CanvasGestureComponent::from_data(back), c);
        assert_eq!(c.min_hit_size(), 0.0, "負は広げない");
        assert_eq!(GestureDragAxis::Horizontal.index(), 1);
        assert_eq!(GestureDragAxis::from_script_value(2.0), Some(GestureDragAxis::Vertical));
    }
}
