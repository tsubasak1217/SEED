// ============================================================
//  interaction_source_component.rs — インタラクションソースコンポーネント（Phase I1）
//
//  「動く物」に付けて、インタラクションフィールド（草の揺れ・水の波紋・雪泥の轍を
//  1 系統で駆動する共有の場）へ**書き込む権利**を宣言するだけの ECS スロットコンポーネント。
//  正典は docs/water_interaction_roadmap.md §1.3。
//
//  ## ECS 理念（データとロジックの分離）
//  本コンポーネントは「半径・強さ・有効フラグ」の *データのみ* を持つ。
//    ・シーンからの収集とワールド解決 … `engine::interaction::collect`
//    ・速度の算出と場への焼き込み     … `renderer::interaction::InteractionFieldRenderer`
//    ・場の消費（草の曲げ 等）         … 各シェーダ（grass_gbuffer.wgsl 等）
//  書き手（本コンポーネント）は読み手を一切知らない。読み手を増やしても、
//  キャラに本コンポーネントを 1 個付けるだけで全表現が反応する。
//
//  ## 位置と速度は持たない
//  ワールド位置はアクターの `Transform` から解決する（本コンポーネントは位置を持たない）。
//  速度は「前フレームのワールド位置との差分 / dt」としてフィールド側が毎フレーム算出する。
//  ＝ **速度を宣言する必要は無く、アクタを動かすだけで場が反応する**。
//
//  ## シリアライズ
//  全フィールドに `#[serde(default)]`（非ゼロ既定値は default 関数）を付け、
//  本コンポーネントを知らない旧 `.scene` でも読み込みが失敗しないようにする。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

// ─── デフォルト値関数 ─────────────────────────────────────────
// マジックナンバー禁止のため、非ゼロ既定値はすべて関数に切り出す。

/// `radius` の既定値（m）。
///
/// 人型キャラクターの足元が草を踏み分ける幅としての 1m。半径を大きくすると
/// 「大型の獣が薙ぎ倒す」表現になる（データ差し替えだけで表現が変わる）。
fn default_radius() -> f32 { 1.0 }

/// `strength` の既定値（0..1）。1 = 場へ自分の速度をそのまま書き込む。
fn default_strength() -> f32 { 1.0 }

/// `stamp_shape` の既定値（`Circle`）。
///
/// 既定を円にしているのは、テクスチャを用意しなくても轍が機能するため
/// （＝コンポーネントを付けるだけで足跡が残る）。
fn default_stamp_shape() -> InteractionStampShape {
    InteractionStampShape::Circle
}

/// `stamp_size`（テクスチャ形状の実寸・メートル）の既定値。
///
/// `[進行方向に直交する幅, 進行方向の長さ]`。ブーツの足裏（12cm × 30cm）を
/// 想定した値で、追加してすぐ人型の足跡として使える大きさにしてある。
fn default_stamp_size() -> [f32; 2] {
    [0.12, 0.30]
}

/// `enabled` の既定値（true）。
///
/// スロットの `enabled`（コンポーネント自体の有効/無効）とは別に、
/// **ゲームロジックから一時的に場への書き込みを止める**ためのデータ側フラグ。
/// 例: 空中にいる間だけ草を踏まない、といった制御をスクリプトから行う。
fn default_enabled() -> bool { true }

// ─── 轍スタンプの形状（Phase I3.2）─────────────────────────────

/// 地表カバー場へ押し当てる痕（轍・足跡）の形状。
///
/// serde は文字列（`"Circle"` / `"Texture"`）でシリアライズする。
/// 旧シーン（stamp_shape 欠落）は `#[serde(default)]` により `Circle` になる。
///
/// 【草なびき・水の波紋には効かない】
///   形状はカバー場のスタンプ（轍）専用である。インタラクションフィールドは
///   ワールド俯瞰の低解像度テクスチャに「速度」を焼く仕組みで、そこへ向きを持つ
///   マスクを載せるには場のフォーマット自体を変える必要がある。
///   草が踏み分けられる範囲と、地面に残る痕の形は別物（前者は体の周り、
///   後者は接地面）なので、半径だけの等方な影響で意味的にも正しい。
#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Debug)]
pub enum InteractionStampShape {
    /// 等方な円（既定）。半径 `radius` の内側を中心ほど深く踏み固める。
    Circle,
    /// グレースケール画像を進行方向へ回転させて押し当てる。
    ///
    /// 白 = 最大の深さ、黒 = 踏まない。画像の **上方向が進行方向**。
    Texture,
}

impl InteractionStampShape {
    /// インスペクタへ送る種別文字列（C# 側ドロップダウンの Tag と一致させる）。
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Circle => "Circle",
            Self::Texture => "Texture",
        }
    }

    /// IPC 文字列から種別へ変換する。未知の文字列は `None`（呼び出し側で無視する）。
    pub fn from_str_opt(s: &str) -> Option<Self> {
        match s {
            "Circle" => Some(Self::Circle),
            "Texture" => Some(Self::Texture),
            _ => None,
        }
    }
}

// ─── InteractionSourceComponentData（シリアライズ用）───────────

/// `InteractionSourceComponent` のシリアライズ用データ（.scene 保存・Undo スナップショット）。
#[derive(Clone, Serialize, Deserialize, Debug)]
pub struct InteractionSourceComponentData {
    /// 影響半径（m）。この半径の内側の場へ自分の速度を書き込む。既定 1.0。
    #[serde(default = "default_radius")]
    pub radius: f32,
    /// 書き込みの強さ（0..1）。0 = 何も書かない、1 = 中心で速度をそのまま書く。既定 1.0。
    #[serde(default = "default_strength")]
    pub strength: f32,
    /// 有効フラグ。false の間は場へ一切書き込まない。既定 true。
    #[serde(default = "default_enabled")]
    pub enabled: bool,
    /// 轍スタンプの形状（Phase I3.2）。既定 `Circle`。
    #[serde(default = "default_stamp_shape")]
    pub stamp_shape: InteractionStampShape,
    /// `Texture` 形状のグレースケール画像パス（`assets://...` 可）。空 = 痕を付けない。
    #[serde(default)]
    pub stamp_mask_path: String,
    /// `Texture` 形状の実寸（メートル）。`[横幅, 進行方向の長さ]`。
    #[serde(default = "default_stamp_size")]
    pub stamp_size: [f32; 2],
}

impl Default for InteractionSourceComponentData {
    fn default() -> Self {
        Self {
            radius:   default_radius(),
            strength: default_strength(),
            enabled:  default_enabled(),
            stamp_shape:     default_stamp_shape(),
            stamp_mask_path: String::new(),
            stamp_size:      default_stamp_size(),
        }
    }
}

// ─── InteractionSourceComponent（ランタイム）───────────────────

/// インタラクションフィールドへの書き手（Phase I1）。
///
/// 動くアクター（キャラクター・車両・落下物など）に付ける。付けるだけで、
/// そのアクターが移動した軌跡が共有の瞬発場へ焼かれ、草が押し倒される
/// （将来は水の波紋・雪泥の轍も同じ場から駆動される）。
#[derive(Clone, Debug)]
pub struct InteractionSourceComponent {
    /// 影響半径（m）。
    pub radius: f32,
    /// 書き込みの強さ（0..1）。
    pub strength: f32,
    /// 有効フラグ（false の間は場へ書き込まない）。
    pub enabled: bool,
    /// 轍スタンプの形状（Phase I3.2）。
    pub stamp_shape: InteractionStampShape,
    /// `Texture` 形状のグレースケール画像パス。空 = 痕を付けない。
    pub stamp_mask_path: String,
    /// `Texture` 形状の実寸（メートル）。`[横幅, 進行方向の長さ]`。
    pub stamp_size: [f32; 2],
}

impl Default for InteractionSourceComponent {
    fn default() -> Self {
        Self {
            radius:   default_radius(),
            strength: default_strength(),
            enabled:  default_enabled(),
            stamp_shape:     default_stamp_shape(),
            stamp_mask_path: String::new(),
            stamp_size:      default_stamp_size(),
        }
    }
}

impl InteractionSourceComponent {
    /// シリアライズ用データからランタイム表現を作る（.scene 読込・複製・Undo 復元）。
    pub fn from_data(data: &InteractionSourceComponentData) -> Self {
        Self {
            radius:   data.radius,
            strength: data.strength,
            enabled:  data.enabled,
            stamp_shape:     data.stamp_shape,
            stamp_mask_path: data.stamp_mask_path.clone(),
            stamp_size:      data.stamp_size,
        }
    }

    /// ランタイム表現をシリアライズ用データへ変換する（.scene 保存・Undo スナップショット）。
    pub fn to_data(&self) -> InteractionSourceComponentData {
        InteractionSourceComponentData {
            radius:   self.radius,
            strength: self.strength,
            enabled:  self.enabled,
            stamp_shape:     self.stamp_shape,
            stamp_mask_path: self.stamp_mask_path.clone(),
            stamp_size:      self.stamp_size,
        }
    }
}

impl Component for InteractionSourceComponent {}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 既定値が「半径 1m・強さ 1・有効」であること（インスペクタ追加直後の見え方の契約）。
    #[test]
    fn defaults_are_documented_values() {
        let c = InteractionSourceComponent::default();
        assert_eq!(c.radius, 1.0);
        assert_eq!(c.strength, 1.0);
        assert!(c.enabled);
    }

    /// to_data → from_data で値が完全に往復すること（轍スタンプの形状込み）。
    #[test]
    fn data_round_trips() {
        let c = InteractionSourceComponent {
            radius: 2.5,
            strength: 0.25,
            enabled: false,
            stamp_shape: InteractionStampShape::Texture,
            stamp_mask_path: "assets://tex/tire.png".to_string(),
            stamp_size: [0.4, 1.2],
        };
        let back = InteractionSourceComponent::from_data(&c.to_data());
        assert_eq!(back.radius, 2.5);
        assert_eq!(back.strength, 0.25);
        assert!(!back.enabled);
        assert_eq!(back.stamp_shape, InteractionStampShape::Texture);
        assert_eq!(back.stamp_mask_path, "assets://tex/tire.png");
        assert_eq!(back.stamp_size, [0.4, 1.2]);
    }

    /// 轍スタンプ形状の文字列が C# ドロップダウンの Tag と一致すること。
    #[test]
    fn stamp_shape_strings_are_stable() {
        assert_eq!(InteractionStampShape::Circle.as_str(), "Circle");
        assert_eq!(InteractionStampShape::Texture.as_str(), "Texture");
        assert_eq!(
            InteractionStampShape::from_str_opt("Texture"),
            Some(InteractionStampShape::Texture)
        );
        assert_eq!(InteractionStampShape::from_str_opt("Blob"), None);
    }

    /// **フィールドが 1 つも無い旧 .scene（`{}`）でも読み込めること**（serde default の要）。
    /// ここが壊れると、旧シーンの読み込みが丸ごと失敗する。
    #[test]
    fn deserializes_from_empty_json_with_defaults() {
        let d: InteractionSourceComponentData =
            serde_json::from_str("{}").expect("空 JSON から既定値で復元できること");
        assert_eq!(d.radius, 1.0);
        assert_eq!(d.strength, 1.0);
        assert!(d.enabled);
        // 轍スタンプ（I3.2）を知らない旧シーンは円形の既定値になる。
        assert_eq!(d.stamp_shape, InteractionStampShape::Circle);
        assert!(d.stamp_mask_path.is_empty());
    }

    /// 一部フィールドだけを持つ JSON でも、残りは既定値で埋まること。
    #[test]
    fn deserializes_partial_json() {
        let d: InteractionSourceComponentData =
            serde_json::from_str(r#"{"radius":4.0}"#).expect("部分 JSON から復元できること");
        assert_eq!(d.radius, 4.0);
        assert_eq!(d.strength, 1.0, "未指定フィールドは既定値");
        assert!(d.enabled);
    }
}
