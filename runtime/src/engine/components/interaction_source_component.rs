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

/// `enabled` の既定値（true）。
///
/// スロットの `enabled`（コンポーネント自体の有効/無効）とは別に、
/// **ゲームロジックから一時的に場への書き込みを止める**ためのデータ側フラグ。
/// 例: 空中にいる間だけ草を踏まない、といった制御をスクリプトから行う。
fn default_enabled() -> bool { true }

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
}

impl Default for InteractionSourceComponentData {
    fn default() -> Self {
        Self {
            radius:   default_radius(),
            strength: default_strength(),
            enabled:  default_enabled(),
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
}

impl Default for InteractionSourceComponent {
    fn default() -> Self {
        Self {
            radius:   default_radius(),
            strength: default_strength(),
            enabled:  default_enabled(),
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
        }
    }

    /// ランタイム表現をシリアライズ用データへ変換する（.scene 保存・Undo スナップショット）。
    pub fn to_data(&self) -> InteractionSourceComponentData {
        InteractionSourceComponentData {
            radius:   self.radius,
            strength: self.strength,
            enabled:  self.enabled,
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

    /// to_data → from_data で値が完全に往復すること。
    #[test]
    fn data_round_trips() {
        let c = InteractionSourceComponent { radius: 2.5, strength: 0.25, enabled: false };
        let back = InteractionSourceComponent::from_data(&c.to_data());
        assert_eq!(back.radius, 2.5);
        assert_eq!(back.strength, 0.25);
        assert!(!back.enabled);
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
