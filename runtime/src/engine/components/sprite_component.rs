// ============================================================
//  sprite_component.rs — 2D スプライトコンポーネント
//
//  2D キャンバス上に画像（テクスチャ）を表示するコンポーネント。
//  必ず CanvasComponent を持つアクターの子アクターにアタッチする。
//  CanvasTransform と組み合わせてキャンバス座標系内の
//  位置・回転・スケールを制御する。
//
//  【配置ルール】
//  - 単体アクターに追加 → 自動で Canvas を追加し、子アクターを生成してそこに配置
//  - Canvas 持ちアクターを選択して追加 → 新規子アクターを生成してそこに配置
//  - Canvas の子アクターを選択して追加 → 選択アクターに直接追加
// ============================================================

use crate::engine::components::sprite_style::{
    is_plain_style, SpriteFill, SpriteNineSlice, SpriteShadow, SpriteShape,
};
use crate::engine::ecs::Component;
use serde::{Deserialize, Serialize};

// ─── SpriteComponentData ─────────────────────────────────────────────────────

/// SpriteComponent のシリアライズ用データ。
#[derive(Clone, Serialize, Deserialize)]
pub struct SpriteComponentData {
    /// テクスチャファイルパス（空文字列 = テクスチャなし、単色表示）
    pub texture_path: String,
    /// 表示カラー（RGBA 正規化値 [r, g, b, a]）
    pub color: [f32; 4],
    /// スプライト表示幅（キャンバスユニット）
    pub width: f32,
    /// スプライト表示高さ（キャンバスユニット）
    pub height: f32,
    /// 描画優先度レイヤー（大きいほど手前・同値はヒエラルキー DFS 順）。
    /// 旧データ互換のため #[serde(default)] で 0 として読み込む。
    #[serde(default)]
    pub layer: i32,
    /// ポストエフェクトアセット（.postfx）パス（空文字列 = 無効）。
    /// 指定時はテクスチャに .postfx のエフェクトチェーンを焼き込んで描画する
    /// （旧データ互換のため #[serde(default)] で空文字列として読み込む）。
    #[serde(default)]
    pub postfx_path: String,
    /// ポインタイベント（OnPointerEnter/Down/Click 等）のヒットテスト対象にするか。
    /// 既定 false（オプトイン）。旧データ互換のため #[serde(default)]。
    #[serde(default)]
    pub raycast_target: bool,
    /// 形（矩形の四隅の角丸・楕円・弧）と縁の線（W2-4。既定 = 直角の矩形・縁なし）。
    /// 既定なら保存しない（既存のシーンを保存し直してもファイルは変わらない）。
    #[serde(default, skip_serializing_if = "SpriteShape::is_default")]
    pub shape: SpriteShape,
    /// 塗り（単色・線形・放射のグラデーション。W2-4。既定 = 単色）。
    #[serde(default, skip_serializing_if = "SpriteFill::is_default")]
    pub fill: SpriteFill,
    /// 画像の 9 スライス（W2-4。既定 = 使わない）。
    #[serde(default, skip_serializing_if = "SpriteNineSlice::is_default")]
    pub nine_slice: SpriteNineSlice,
    /// ぼかしの影（W2-4。既定 = 描かない）。
    #[serde(default, skip_serializing_if = "SpriteShadow::is_default")]
    pub shadow: SpriteShadow,
}

// ─── SpriteComponent ─────────────────────────────────────────────────────────

/// 2D スプライトコンポーネント。
///
/// Actor2D にアタッチして、キャンバス上に画像を表示する。
/// CanvasTransform が示す位置・回転・スケールで描画領域が決まる。
/// 親アクターに CanvasComponent が必要。
///
/// # 座標系
/// CanvasTransform の position は親 Canvas を基準とした相対座標。
/// to_mat4_sized(width, height) でスプライト領域の変換行列を計算する。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SpriteComponent {
    /// テクスチャファイルパス（空文字列 = テクスチャなし、単色表示）
    pub texture_path: String,
    /// 表示カラー（RGBA 正規化値 [r, g, b, a]）
    pub color: [f32; 4],
    /// スプライト表示幅（キャンバスユニット）
    pub width: f32,
    /// スプライト表示高さ（キャンバスユニット）
    pub height: f32,
    /// 描画優先度レイヤー（大きいほど手前・同値はヒエラルキー DFS 順）。
    /// 比較は同一描画ゾーン内（ビューポートはゾーン単位で全キャンバス横断、
    /// ワールドキャンバスはそのキャンバス内）で行われる。
    #[serde(default)]
    pub layer: i32,
    /// ポストエフェクトアセット（.postfx）パス（空文字列 = 無効）。
    /// 指定時はテクスチャに .postfx のエフェクトチェーンを焼き込んで描画する。
    #[serde(default)]
    pub postfx_path: String,
    /// ポインタイベント（OnPointerEnter/Down/Click 等）のヒットテスト対象にするか。
    ///
    /// 既定 false のオプトイン方式。false のスプライトは Play 中のポインタ判定から
    /// 完全に除外される（背景・装飾がボタンのクリックを食わないようにするため）。
    /// エディタの選択ピッキングには影響しない（そちらは常に全スプライトが対象）。
    #[serde(default)]
    pub raycast_target: bool,
    /// 形（矩形の四隅の角丸・楕円・弧）と縁の線（W2-4。既定 = 直角の矩形・縁なし）。
    /// 既定なら保存しない（既存のシーンを保存し直してもファイルは変わらない）。
    #[serde(default, skip_serializing_if = "SpriteShape::is_default")]
    pub shape: SpriteShape,
    /// 塗り（単色・線形・放射のグラデーション。W2-4。既定 = 単色）。
    #[serde(default, skip_serializing_if = "SpriteFill::is_default")]
    pub fill: SpriteFill,
    /// 画像の 9 スライス（W2-4。既定 = 使わない）。
    #[serde(default, skip_serializing_if = "SpriteNineSlice::is_default")]
    pub nine_slice: SpriteNineSlice,
    /// ぼかしの影（W2-4。既定 = 描かない）。
    #[serde(default, skip_serializing_if = "SpriteShadow::is_default")]
    pub shadow: SpriteShadow,
}

impl SpriteComponent {
    /// シリアライズ用データから復元する（to_data の逆）。
    /// シーン読込・Undo/Redo の両方から使う唯一の復元経路。
    pub fn from_data(data: SpriteComponentData) -> Self {
        Self {
            texture_path: data.texture_path,
            color: data.color,
            width: data.width,
            height: data.height,
            layer: data.layer,
            postfx_path: data.postfx_path,
            raycast_target: data.raycast_target,
            shape: data.shape,
            fill: data.fill,
            nine_slice: data.nine_slice,
            shadow: data.shadow,
        }
    }

    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> SpriteComponentData {
        SpriteComponentData {
            texture_path: self.texture_path.clone(),
            color: self.color,
            width: self.width,
            height: self.height,
            layer: self.layer,
            postfx_path: self.postfx_path.clone(),
            raycast_target: self.raycast_target,
            shape: self.shape.clone(),
            fill: self.fill.clone(),
            nine_slice: self.nine_slice.clone(),
            shadow: self.shadow.clone(),
        }
    }
}

impl SpriteComponent {
    /// 形と塗りの欄（W2-4）がすべて既定か（＝従来のパイプラインで今と画素単位で同じに描く）。
    pub fn is_plain_style(&self) -> bool {
        is_plain_style(&self.shape, &self.fill, &self.nine_slice, &self.shadow)
    }
}

impl Default for SpriteComponent {
    fn default() -> Self {
        // デフォルトは白色・100×100 キャンバスユニット・テクスチャなし・レイヤー 0
        Self {
            texture_path: String::new(),
            color: [1.0, 1.0, 1.0, 1.0],
            width: 100.0,
            height: 100.0,
            layer: 0,
            postfx_path: String::new(),
            raycast_target: false,
            shape: SpriteShape::default(),
            fill: SpriteFill::default(),
            nine_slice: SpriteNineSlice::default(),
            shadow: SpriteShadow::default(),
        }
    }
}

impl Component for SpriteComponent {}
