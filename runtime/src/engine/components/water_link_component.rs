// ============================================================
//  water_link_component.rs — 水位グラフのリンク（開口）コンポーネント（Phase W2.5）
//
//  正典: docs/water_interaction_roadmap.md §1.2「連通ボリューム方式（水位グラフ）」。
//
//  【役割】
//  2 つの水ボリューム（`WaterVolumeComponent` の kind = Region）をつなぐ
//  **開口（扉・窓・穴・バルブ）1 個**を宣言するデータである。
//  「どのボリュームとどのボリュームが、どの高さの、どれだけの断面積でつながるか」
//  だけを持ち、水の移動計算は一切行わない（ECS 理念：データとロジックの分離）。
//  計算は `engine::water::level_graph`（純粋計算）＋ `level_sim`（ECS 結線）が担う。
//
//  【ゲームプレイ制御はこのコンポーネントの値を変えるだけで行う】
//    ・バルブを閉める      → `openness` を 0 にする
//    ・壁を壊す            → リンクを追加する（またはスロットを enabled にする）
//    ・扉を半開きにする    → `openness` を 0.5 にする
//  スクリプトからは `WaterLink` として公開している（docs/scripting_api.md）。
//
//  【位置の解決】
//  開口のワールド位置はアクタの Transform から解決する（本コンポーネントは
//  位置を持たない）。`opening_bottom` は **アクタ原点からの相対 Y** であり、
//  「扉アクタを置いて、その足元を開口の下端にする」使い方を想定している。
//  この位置は演出接続（流量に比例した波源の発生地点）にも使われる。
//
//  【接続先の指定はアクタ名参照】
//  `volume_a` / `volume_b` に**水ボリュームを持つアクタの名前**を書く。
//  川（Spline）・大洋（Ocean）は水位グラフのノードになれない
//  （底面積が定義できないため。詳細は `level_sim.rs`）。
//  アクタ名のリネームには `rename_refs.rs` が追従する。
//
//  【シリアライズ】
//  全フィールドに #[serde(default)]（非ゼロ既定は default fn）を付け、
//  旧 .scene（フィールド欠落）でも読み込みが失敗しないようにする。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

// ─── デフォルト値関数（マジックナンバー禁止）────────────────────

/// opening_height の既定値（開口の高さ m）。
///
/// 標準的な扉の高さ 2m。この高さぶんが水没すると全断面が流れる。
fn default_opening_height() -> f32 { 2.0 }

/// opening_width の既定値（開口の幅 m）。
///
/// 標準的な扉の幅 1m。`幅 × 濡れ高さ × 開閉率` が実効断面積になる。
fn default_opening_width() -> f32 { 1.0 }

/// openness の既定値（開閉率 0..1）。
///
/// 既定は全開（1.0）。「リンクを置いたのに水が動かない」で悩ませないため。
/// バルブ演出は 0 まで落として使う（**0 で完全に水を止める**）。
fn default_openness() -> f32 { 1.0 }

/// flow_coefficient の既定値（流量係数 1/s）。
///
/// 流量 Q [m³/s] = 係数 × 断面積 [m²] × 水位差 [m]。
/// 既定 1.0 は「幅 1m の扉ごしに水位差 1m があるとき、毎秒 1m³ 弱が流れる」
/// 目安で、6m×6m の部屋なら数秒で目に見えて水位が動く。
/// 大きくすると勢いよく釣り合い、小さくするとじわじわ染み込む
/// （**どれだけ大きくしても発振はしない**。`level_graph` の釣り合いクランプ参照）。
fn default_flow_coefficient() -> f32 { 1.0 }

// ─── WaterLinkComponentData（シリアライズ用）──────────────────

/// WaterLinkComponent のシリアライズ用データ（.scene 保存・Undo スナップショット）。
#[derive(Clone, Serialize, Deserialize, Debug, PartialEq)]
pub struct WaterLinkComponentData {
    /// 接続先 A の**アクタ名**（そのアクタが持つ WaterVolume を指す）。空 = 未接続。
    #[serde(default)]
    pub volume_a: String,
    /// 接続先 B の**アクタ名**（そのアクタが持つ WaterVolume を指す）。空 = 未接続。
    #[serde(default)]
    pub volume_b: String,
    /// 開口の下端 Y（**アクタ原点からの相対 m**）。既定 0.0 ＝ アクタの足元。
    ///
    /// 低い位置に置いた開口ほど早く水を通す（＝階段穴が先に地下へ落とす）。
    #[serde(default)]
    pub opening_bottom: f32,
    /// 開口の高さ（m）。既定 2.0（扉相当）。
    #[serde(default = "default_opening_height")]
    pub opening_height: f32,
    /// 開口の幅（m）。既定 1.0（扉相当）。
    #[serde(default = "default_opening_width")]
    pub opening_width: f32,
    /// 開閉率（0..1）。**0 でバルブ全閉＝水は 1 滴も通らない**。既定 1.0（全開）。
    #[serde(default = "default_openness")]
    pub openness: f32,
    /// 流量係数（1/s）。大きいほど速く釣り合う。既定 1.0。
    #[serde(default = "default_flow_coefficient")]
    pub flow_coefficient: f32,
}

impl Default for WaterLinkComponentData {
    fn default() -> Self {
        Self {
            volume_a:         String::new(),
            volume_b:         String::new(),
            opening_bottom:   0.0,
            opening_height:   default_opening_height(),
            opening_width:    default_opening_width(),
            openness:         default_openness(),
            flow_coefficient: default_flow_coefficient(),
        }
    }
}

// ─── WaterLinkComponent（ECS 実体）───────────────────────────

/// 水位グラフのリンク（開口）コンポーネント（ECS 実体）。
///
/// 保持するのは接続先の名前と開口の寸法・開閉率のみ。
/// ワールド位置は Actor の Transform から `engine::water::level_sim` が毎フレーム解決する。
/// 揮発状態は持たない（Data と同一構成）。
#[derive(Clone, Debug, PartialEq)]
pub struct WaterLinkComponent {
    /// 接続先 A のアクタ名（空 = 未接続）
    pub volume_a: String,
    /// 接続先 B のアクタ名（空 = 未接続）
    pub volume_b: String,
    /// 開口の下端 Y（アクタ原点からの相対 m）
    pub opening_bottom: f32,
    /// 開口の高さ（m）
    pub opening_height: f32,
    /// 開口の幅（m）
    pub opening_width: f32,
    /// 開閉率（0..1。0 = バルブ全閉）
    pub openness: f32,
    /// 流量係数（1/s）
    pub flow_coefficient: f32,
}

impl WaterLinkComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    pub fn from_data(data: WaterLinkComponentData) -> Self {
        Self {
            volume_a:         data.volume_a,
            volume_b:         data.volume_b,
            opening_bottom:   data.opening_bottom,
            opening_height:   data.opening_height,
            opening_width:    data.opening_width,
            openness:         data.openness,
            flow_coefficient: data.flow_coefficient,
        }
    }

    /// シリアライズ用データへ変換する。
    pub fn to_data(&self) -> WaterLinkComponentData {
        WaterLinkComponentData {
            // 名前は所有権を渡せない（&self 受け）ため複製する。
            volume_a:         self.volume_a.clone(),
            volume_b:         self.volume_b.clone(),
            opening_bottom:   self.opening_bottom,
            opening_height:   self.opening_height,
            opening_width:    self.opening_width,
            openness:         self.openness,
            flow_coefficient: self.flow_coefficient,
        }
    }
}

impl Default for WaterLinkComponent {
    fn default() -> Self { Self::from_data(WaterLinkComponentData::default()) }
}

impl Component for WaterLinkComponent {}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 空 JSON（全フィールド欠落＝旧 .scene 相当）でもデシリアライズでき、
    /// 既定値が入ること（serde(default) の付け漏れ検出）。
    #[test]
    fn deserializes_from_empty_json_with_defaults() {
        let d: WaterLinkComponentData =
            serde_json::from_str("{}").expect("空 JSON を読めること");
        let def = WaterLinkComponentData::default();
        assert_eq!(d, def);
        // 既定は「全開の扉」。置いただけで水が通る。
        assert_eq!(d.openness, 1.0);
        assert_eq!(d.opening_width, 1.0);
        assert_eq!(d.opening_height, 2.0);
        assert_eq!(d.flow_coefficient, 1.0);
        assert_eq!(d.opening_bottom, 0.0);
        assert!(d.volume_a.is_empty());
        assert!(d.volume_b.is_empty());
    }

    /// from_data / to_data が全フィールドを往復すること（写し漏れ検出）。
    #[test]
    fn data_roundtrip_preserves_all_fields() {
        let src = WaterLinkComponentData {
            volume_a:         "RoomA".to_string(),
            volume_b:         "Basement".to_string(),
            opening_bottom:   -1.5,
            opening_height:   3.25,
            opening_width:    0.75,
            openness:         0.5,
            flow_coefficient: 2.5,
        };
        let back = WaterLinkComponent::from_data(src.clone()).to_data();
        assert_eq!(back, src);
    }
}
