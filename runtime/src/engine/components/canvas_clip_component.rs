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
//  【形（W2-4）】`shape` で切り抜きの形を選ぶ（既定 Rect = 従来どおり矩形の scissor だけ）:
//    - Rect        … 矩形（軸に沿った外接矩形の scissor。W2-1a と同じ）
//    - RoundedRect … 四隅ごとの角丸（`corner_radii`。ノードのキャンバスの単位）
//    - Ellipse     … 矩形に内接する楕円（丸いアイコン）
//    - SpriteShape … 最初の有効な SpriteComponent の形（角丸・楕円）に合わせる（角丸のカードの背景と同じ形で切る）
//  角丸・楕円は**いちばん内側の 1 つだけ**をシェーダーの SDF で切り、外側の角丸・楕円の祖先は外接矩形の scissor で切る
//  （W2-0 の決定。docs/app_platform_roadmap.md §3.8.4）。SDF で切るのはスプライト（画像・形）だけで、テキスト・
//  SEED.Draw の図形・2D パーティクルは外接矩形の scissor のまま（docs/ui_components.md）。当たり判定も同じ形を見る。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

/// serde の既定値: 有効（切り抜く）。
fn default_enabled() -> bool {
    true
}

/// 切り抜きの形（W2-4）。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ClipShapeMode {
    /// 矩形（既定。従来どおりの scissor）。
    #[default]
    Rect,
    /// 四隅ごとの角丸（`corner_radii`）。
    RoundedRect,
    /// 矩形に内接する楕円。
    Ellipse,
    /// 最初の有効な SpriteComponent の形（角丸・楕円）に合わせる（弧は矩形として扱う）。
    SpriteShape,
}

impl ClipShapeMode {
    /// 保存・IPC・スクリプトで使う名前（serde の名前と同じ）。
    pub fn key(self) -> &'static str {
        match self {
            Self::Rect => "rect",
            Self::RoundedRect => "rounded_rect",
            Self::Ellipse => "ellipse",
            Self::SpriteShape => "sprite_shape",
        }
    }

    /// 名前から形を引く（知らない名前は None）。
    pub fn from_key(key: &str) -> Option<Self> {
        match key.trim() {
            "rect" => Some(Self::Rect),
            "rounded_rect" => Some(Self::RoundedRect),
            "ellipse" => Some(Self::Ellipse),
            "sprite_shape" => Some(Self::SpriteShape),
            _ => None,
        }
    }
}

/// 「子を切り抜く」コンポーネントのシリアライズ用データ（`.scene` / `.actor` の `data`）。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct CanvasClipComponentData {
    /// 有効フラグ。false の間は切り抜かない（スクリプトから切り替えられる）。既定 true。
    #[serde(default = "default_enabled")]
    pub enabled: bool,
    /// 切り抜きの形（W2-4。既定 Rect。既定なら保存しない）。
    #[serde(default, skip_serializing_if = "is_rect_shape")]
    pub shape: ClipShapeMode,
    /// 四隅の角丸の半径（左上・右上・右下・左下。ノードのキャンバスの単位。RoundedRect のときだけ効く。
    /// すべて 0 なら保存しない）。
    #[serde(default, skip_serializing_if = "is_zero_radii")]
    pub corner_radii: [f32; 4],
}

/// serde の保存を省く判定: 形が既定の矩形か。
fn is_rect_shape(shape: &ClipShapeMode) -> bool {
    *shape == ClipShapeMode::Rect
}

/// serde の保存を省く判定: 角丸がすべて 0 か。
fn is_zero_radii(radii: &[f32; 4]) -> bool {
    radii.iter().all(|r| *r == 0.0)
}

impl Default for CanvasClipComponentData {
    fn default() -> Self {
        Self { enabled: default_enabled(), shape: ClipShapeMode::Rect, corner_radii: [0.0; 4] }
    }
}

/// 「子を切り抜く」コンポーネント（World に置く実体）。
#[derive(Clone, Debug, PartialEq)]
pub struct CanvasClipComponent {
    /// 有効フラグ。false の間は切り抜かない。スロットの有効・無効（インスペクタの見出しの切り替え）とは別で、
    /// 両方が有効のときだけ切り抜く。
    pub enabled: bool,
    /// 切り抜きの形（W2-4）。
    pub shape: ClipShapeMode,
    /// 四隅の角丸の半径（左上・右上・右下・左下。RoundedRect のときだけ効く）。
    pub corner_radii: [f32; 4],
}

impl Default for CanvasClipComponent {
    fn default() -> Self {
        Self::from_data(CanvasClipComponentData::default())
    }
}

impl CanvasClipComponent {
    /// シリアライズ用データから実体を作る。
    pub fn from_data(data: CanvasClipComponentData) -> Self {
        Self { enabled: data.enabled, shape: data.shape, corner_radii: data.corner_radii }
    }

    /// 実体からシリアライズ用データを作る。
    pub fn to_data(&self) -> CanvasClipComponentData {
        CanvasClipComponentData { enabled: self.enabled, shape: self.shape, corner_radii: self.corner_radii }
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

    /// 既定の形（矩形・角丸 0）は保存しない（既存のシーンのファイルが変わらない）。
    #[test]
    fn default_shape_is_not_serialized() {
        let json = serde_json::to_string(&CanvasClipComponentData::default()).unwrap();
        assert_eq!(json, r#"{"enabled":true}"#);
        let data: CanvasClipComponentData = serde_json::from_str(r#"{"enabled":true,"shape":"ellipse"}"#).unwrap();
        assert_eq!(data.shape, ClipShapeMode::Ellipse);
        for mode in [ClipShapeMode::Rect, ClipShapeMode::RoundedRect, ClipShapeMode::Ellipse, ClipShapeMode::SpriteShape] {
            assert_eq!(ClipShapeMode::from_key(mode.key()), Some(mode));
            assert_eq!(serde_json::to_string(&mode).unwrap(), format!("\"{}\"", mode.key()));
        }
    }

    /// 実体 ⇔ データの往復で値が保たれる。
    #[test]
    fn round_trip_keeps_enabled() {
        let c = CanvasClipComponent { enabled: false, shape: ClipShapeMode::RoundedRect, corner_radii: [1.0, 2.0, 3.0, 4.0] };
        let json = serde_json::to_string(&c.to_data()).unwrap();
        let back: CanvasClipComponentData = serde_json::from_str(&json).unwrap();
        assert_eq!(CanvasClipComponent::from_data(back), c);
    }
}
