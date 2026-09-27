// ============================================================
//  canvas_safe_area_component.rs — 安全領域の部品（W2-1b。W2-P5「安全領域は部品」）
//
//  CanvasComponent を持つ 2D キャンバスのノード（パネル）に付けると、そのノードのキャンバス領域を
//  `Screen.SafeArea`（カメラの穴・ステータスバー・ジェスチャーバーを避けた内側）の内側へ縮める。
//  子のアンカー・コンテナの並べ方・切り抜きは縮めた領域を基準にする。辺ごとに適用の有無を選べる
//  （例: 背景を下まで伸ばしたいので bottom だけ外す）。
//    - 追従: 安全領域はフレームごとの画面情報（platform/screen の写し）から読むので、画面の回転と
//            システムバーの出し入れ（SEED.Platform.Window.SetSystemBarsVisible）に次のフレームから追従する
//    - PC の Play: 安全領域は画面全体（縮めない）。環境変数 SEED_SIM_SAFE_AREA で模擬の切り欠きを出せる
//    - エディタの設計空間の表示（ビューポートのタブ）と Edit: 縮めない（端末の画面ではないため）
//    - スプライトの大きさは変えない（背景は安全領域の外まで塗りたいので、親のノードに置く）
//
//  【データとロジックの分離】このファイルはデータだけ。縮める計算は engine/core/canvas_layout/safe_area.rs、
//  木への当てはめはレイアウトの走査（canvas_layout/pass.rs）。規則の正典は docs/canvas_camera_rework.md §6.5。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

use super::canvas_layout_params::default_true;

/// 安全領域の部品（World に置く実体。シリアライズもこの型のまま行う）。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct CanvasSafeAreaComponent {
    /// 有効フラグ。false の間は縮めない。スクリプトから切り替えられる。
    #[serde(default = "default_true")]
    pub enabled: bool,
    /// 左の辺を安全領域へ寄せる。
    #[serde(default = "default_true")]
    pub left: bool,
    /// 上の辺を安全領域へ寄せる（ステータスバー・カメラの穴）。
    #[serde(default = "default_true")]
    pub top: bool,
    /// 右の辺を安全領域へ寄せる。
    #[serde(default = "default_true")]
    pub right: bool,
    /// 下の辺を安全領域へ寄せる（ナビゲーションバー・ジェスチャーバー）。
    #[serde(default = "default_true")]
    pub bottom: bool,
}

/// シリアライズ用データ（実体と同じ型）。
pub type CanvasSafeAreaComponentData = CanvasSafeAreaComponent;

impl Default for CanvasSafeAreaComponent {
    fn default() -> Self {
        Self {
            enabled: default_true(),
            left: default_true(),
            top: default_true(),
            right: default_true(),
            bottom: default_true(),
        }
    }
}

impl CanvasSafeAreaComponent {
    /// シリアライズ用データから実体を作る。
    pub fn from_data(data: CanvasSafeAreaComponentData) -> Self {
        data
    }

    /// 実体からシリアライズ用データを作る。
    pub fn to_data(&self) -> CanvasSafeAreaComponentData {
        self.clone()
    }

    /// 適用する辺（左・上・右・下の並び）。
    pub fn edges(&self) -> [bool; 4] {
        [self.left, self.top, self.right, self.bottom]
    }
}

impl Component for CanvasSafeAreaComponent {}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 欄の無い旧データ（`{}`）は既定（有効・4 辺とも適用）で読める。
    #[test]
    fn empty_data_defaults() {
        let d: CanvasSafeAreaComponentData = serde_json::from_str("{}").unwrap();
        assert_eq!(d, CanvasSafeAreaComponent::default());
        assert_eq!(d.edges(), [true; 4]);
    }

    /// 実体 ⇔ データの往復で値が保たれる。
    #[test]
    fn round_trip() {
        let c = CanvasSafeAreaComponent { bottom: false, ..CanvasSafeAreaComponent::default() };
        let json = serde_json::to_string(&c.to_data()).unwrap();
        let back: CanvasSafeAreaComponentData = serde_json::from_str(&json).unwrap();
        assert_eq!(CanvasSafeAreaComponent::from_data(back), c);
        assert_eq!(c.edges(), [true, true, true, false]);
    }
}
