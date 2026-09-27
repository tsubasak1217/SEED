// ============================================================
//  canvas_clip_component.rs — 「子を切り抜く」コンポーネント（W2-1a。UI の切り抜きの本番）
//
//  2D キャンバスのノードに付けると、そのノードのレイアウトの矩形で子孫を切り抜く
//  （はみ出した部分は描かれず、押せない）。スクロール領域・一覧・カードの中身を枠の内側に収める用途。
//
//  【矩形】（決め方の正典は engine/core/canvas_layout/clip.rs）
//    1. ノードが CanvasComponent を持つ … キャンバス領域（エディタのキャンバス枠と同じ矩形）
//    2. 持たない                     … 最初の有効な SpriteComponent の矩形（背景の板を枠にする）
//    3. どちらも無い                 … 切り抜かない
//  ノード自身の描画（背景の板）は切らない。入れ子は祖先の矩形との積で切る（深さの上限なし）。
//  回転したノードは 4 隅の外接矩形で切る。3D ワールドキャンバスの配下は切り抜かない。
//
//  【データとロジックの分離】このファイルはデータだけ（ECS のコンポーネント）。
//  領域を作るのはレイアウトの走査（canvas_layout/pass.rs）、切るのは描画（renderer/ui_clip.rs・ui_draw_pass.rs）と
//  当たり判定（app/pick_2d.rs）。
//
//  将来の欄（角丸・円の切り抜きの半径など）は W2-4 で足す（足すときは #[serde(default)] を付けること）。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

/// serde の既定値: 有効（切り抜く）。
fn default_enabled() -> bool {
    true
}

/// 「子を切り抜く」コンポーネントのシリアライズ用データ（`.scene` / `.actor` の `data`）。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct CanvasClipComponentData {
    /// 有効フラグ。false の間は切り抜かない（スクリプトから切り替えられる）。既定 true。
    #[serde(default = "default_enabled")]
    pub enabled: bool,
}

impl Default for CanvasClipComponentData {
    fn default() -> Self {
        Self { enabled: default_enabled() }
    }
}

/// 「子を切り抜く」コンポーネント（World に置く実体）。
#[derive(Clone, Debug, PartialEq)]
pub struct CanvasClipComponent {
    /// 有効フラグ。false の間は切り抜かない。スロットの有効・無効（インスペクタの見出しの切り替え）とは別で、
    /// 両方が有効のときだけ切り抜く。
    pub enabled: bool,
}

impl Default for CanvasClipComponent {
    fn default() -> Self {
        Self::from_data(CanvasClipComponentData::default())
    }
}

impl CanvasClipComponent {
    /// シリアライズ用データから実体を作る。
    pub fn from_data(data: CanvasClipComponentData) -> Self {
        Self { enabled: data.enabled }
    }

    /// 実体からシリアライズ用データを作る。
    pub fn to_data(&self) -> CanvasClipComponentData {
        CanvasClipComponentData { enabled: self.enabled }
    }
}

impl Component for CanvasClipComponent {}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 欄の無い旧データ（`{}`）は既定（有効）で読める。
    #[test]
    fn empty_data_defaults_to_enabled() {
        let data: CanvasClipComponentData = serde_json::from_str("{}").unwrap();
        assert!(data.enabled);
        assert!(CanvasClipComponent::from_data(data).enabled);
    }

    /// 実体 ⇔ データの往復で値が保たれる。
    #[test]
    fn round_trip_keeps_enabled() {
        let c = CanvasClipComponent { enabled: false };
        let json = serde_json::to_string(&c.to_data()).unwrap();
        let back: CanvasClipComponentData = serde_json::from_str(&json).unwrap();
        assert_eq!(CanvasClipComponent::from_data(back), c);
    }
}
