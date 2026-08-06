// ============================================================
//  cover_emitter_component.rs — カバーエミッタコンポーネント（Phase I3.1）
//
//  アクターに付けて「地表カバー場（雪・落ち葉・泥・濡れ）へ素材を降らせる権利」を
//  宣言するだけの ECS スロットコンポーネント。正典は docs/cover_field.md。
//
//  ## ECS 理念（データとロジックの分離）
//  本コンポーネントは「範囲種別・範囲パラメータ・素材 ID・強度」の *データのみ* を持つ。
//    ・シーンからの収集とワールド解決 … `app/terrain_cover_ops.rs::collect_cover_emitters`
//    ・積算アルゴリズム               … `engine::terrain::cover::accumulate_chunk`（純粋関数）
//    ・頂点への焼き込みと描画         … `app/terrain_cover_ops.rs` ＋ terrain_gbuffer_write.wgsl
//  エミッタは地形もカバー場も一切知らない。
//
//  ## 位置はアクターの Transform から取る
//  Region / TextureMask の「中心」はアクターのワールド位置である（本コンポーネントは
//  位置フィールドを持たない）。＝ **アクターを動かせば降る場所が動く**。
//  WaterVolumeComponent の Region と同じ規約であり、ギズモ操作がそのまま効く。
//
//  ## シリアライズ
//  全フィールドに `#[serde(default)]`（非ゼロ既定値は default 関数）を付け、
//  本コンポーネントを知らない旧 `.scene` でも読み込みが失敗しないようにする。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

// ─── デフォルト値関数（マジックナンバー禁止）───────────────────────────────

/// `range_kind` の既定値。
///
/// 既定を `Region` にしているのは、追加した瞬間にワールド全域が雪で埋まる
/// （＝取り返しの付きにくい編集が 1 フレームで起きる）事故を避けるため。
fn default_range_kind() -> CoverEmitterRangeKind {
    CoverEmitterRangeKind::Region
}

/// `extents`（直方体の各軸半径・メートル）の既定値。
///
/// 既定チャンク 1 個ぶん（16m）の半分＝8m 立方。追加直後に 1 チャンクへ収まる
/// 大きさで、効果がすぐ目で見える最小のサイズとして選んだ。
fn default_extents() -> [f32; 3] {
    [8.0, 8.0, 8.0]
}

/// `fade`（境界フェード幅・メートル）の既定値。
///
/// 0 だと領域の縁に定規で引いたような直線が出る。1m ぼかすと自然に見える。
fn default_fade() -> f32 {
    1.0
}

/// `mask_size`（TextureMask の XZ サイズ・メートル）の既定値。
fn default_mask_size() -> [f32; 2] {
    [32.0, 32.0]
}

/// `material_id` の既定値。
///
/// サンプルアセット（cover_materials.json）の先頭素材と一致させる。
/// 追加してシミュレートするだけで雪が積もる＝設定作業を要しない既定値。
fn default_material_id() -> String {
    "snow".to_string()
}

/// `strength`（量/秒）の既定値。
///
/// 0.2 = 5 秒で満量（量 1.0）まで積もる。シミュレートボタンを押して
/// 数秒で結果が見える速さとして選んだ。
fn default_strength() -> f32 {
    0.2
}

/// `enabled` の既定値（true）。
fn default_enabled() -> bool {
    true
}

// ─── 範囲種別 ────────────────────────────────────────────────────────────────

/// カバーエミッタの範囲種別。
///
/// serde は文字列（`"Global"` / `"Region"` / `"TextureMask"`）でシリアライズする。
/// 旧シーン（range_kind 欠落）は `#[serde(default)]` により `Region` になる。
#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Debug)]
pub enum CoverEmitterRangeKind {
    /// ワールド全域（天候）。位置も大きさも無関係。
    Global,
    /// アクター位置を中心とする直方体（`extents` が各軸の半径）。
    Region,
    /// アクター位置を中心とする XZ 矩形へグレースケール画像を投影する。
    TextureMask,
}

impl CoverEmitterRangeKind {
    /// インスペクタへ送る種別文字列（C# 側ドロップダウンの Tag と一致させる）。
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Global => "Global",
            Self::Region => "Region",
            Self::TextureMask => "TextureMask",
        }
    }

    /// IPC 文字列から種別へ変換する。未知の文字列は `None`（呼び出し側で無視する）。
    pub fn from_str_opt(s: &str) -> Option<Self> {
        match s {
            "Global" => Some(Self::Global),
            "Region" => Some(Self::Region),
            "TextureMask" => Some(Self::TextureMask),
            _ => None,
        }
    }
}

// ─── CoverEmitterComponentData（シリアライズ用）─────────────────────────────

/// `CoverEmitterComponent` のシリアライズ用データ（.scene 保存・Undo スナップショット）。
#[derive(Clone, Serialize, Deserialize, Debug, PartialEq)]
pub struct CoverEmitterComponentData {
    /// 範囲種別。既定 `Region`。
    #[serde(default = "default_range_kind")]
    pub range_kind: CoverEmitterRangeKind,
    /// `Region` のときの各軸半径（メートル）。アクター位置が中心。
    #[serde(default = "default_extents")]
    pub extents: [f32; 3],
    /// `Region` の境界フェード幅（メートル）。0 で硬い境界。
    #[serde(default = "default_fade")]
    pub fade: f32,
    /// `TextureMask` のグレースケール画像パス（`assets://...` 可）。空 = マスク無効。
    #[serde(default)]
    pub mask_path: String,
    /// `TextureMask` の XZ サイズ（メートル）。アクター位置が矩形の中心。
    #[serde(default = "default_mask_size")]
    pub mask_size: [f32; 2],
    /// 降らせる素材の ID（cover_materials.json の `id`）。
    #[serde(default = "default_material_id")]
    pub material_id: String,
    /// 強度（量/秒）。被覆 1.0・水平面で 1 秒あたりこの量だけ積もる。
    #[serde(default = "default_strength")]
    pub strength: f32,
    /// 有効フラグ。false の間は一切降らせない。
    #[serde(default = "default_enabled")]
    pub enabled: bool,
}

impl Default for CoverEmitterComponentData {
    fn default() -> Self {
        Self {
            range_kind: default_range_kind(),
            extents: default_extents(),
            fade: default_fade(),
            mask_path: String::new(),
            mask_size: default_mask_size(),
            material_id: default_material_id(),
            strength: default_strength(),
            enabled: default_enabled(),
        }
    }
}

// ─── CoverEmitterComponent（ランタイム）─────────────────────────────────────

/// 地表カバー場への書き手（Phase I3.1）。
///
/// 「雪を降らせる」「落ち葉を積もらせる」「雨で濡らす」は、すべて
/// 本コンポーネント 1 種の *パラメータ違い* である
/// （素材の差は cover_materials.json 側のデータで表現する）。
#[derive(Clone, Debug)]
pub struct CoverEmitterComponent {
    /// 範囲種別。
    pub range_kind: CoverEmitterRangeKind,
    /// `Region` の各軸半径（メートル）。
    pub extents: [f32; 3],
    /// `Region` の境界フェード幅（メートル）。
    pub fade: f32,
    /// `TextureMask` のグレースケール画像パス。
    pub mask_path: String,
    /// `TextureMask` の XZ サイズ（メートル）。
    pub mask_size: [f32; 2],
    /// 降らせる素材の ID。
    pub material_id: String,
    /// 強度（量/秒）。
    pub strength: f32,
    /// 有効フラグ。
    pub enabled: bool,
}

impl Default for CoverEmitterComponent {
    fn default() -> Self {
        Self::from_data(&CoverEmitterComponentData::default())
    }
}

impl CoverEmitterComponent {
    /// シリアライズ用データからランタイム表現を作る（.scene 読込・複製・Undo 復元）。
    pub fn from_data(data: &CoverEmitterComponentData) -> Self {
        Self {
            range_kind: data.range_kind,
            extents: data.extents,
            fade: data.fade,
            mask_path: data.mask_path.clone(),
            mask_size: data.mask_size,
            material_id: data.material_id.clone(),
            strength: data.strength,
            enabled: data.enabled,
        }
    }

    /// ランタイム表現をシリアライズ用データへ変換する（.scene 保存・Undo スナップショット）。
    pub fn to_data(&self) -> CoverEmitterComponentData {
        CoverEmitterComponentData {
            range_kind: self.range_kind,
            extents: self.extents,
            fade: self.fade,
            mask_path: self.mask_path.clone(),
            mask_size: self.mask_size,
            material_id: self.material_id.clone(),
            strength: self.strength,
            enabled: self.enabled,
        }
    }
}

impl Component for CoverEmitterComponent {}

// ─── ユニットテスト ──────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 既定値が「Region・8m 立方・雪・0.2/秒・有効」であること
    /// （インスペクタ追加直後の見え方の契約）。
    #[test]
    fn defaults_are_documented_values() {
        let c = CoverEmitterComponent::default();
        assert_eq!(c.range_kind, CoverEmitterRangeKind::Region);
        assert_eq!(c.extents, [8.0, 8.0, 8.0]);
        assert_eq!(c.material_id, "snow");
        assert_eq!(c.strength, 0.2);
        assert!(c.enabled);
        assert!(c.mask_path.is_empty(), "マスクは未設定で追加される");
    }

    /// 既定が Global でないこと（追加した瞬間に世界中が雪に埋まる事故の防止）。
    #[test]
    fn default_range_is_not_global() {
        assert_ne!(
            CoverEmitterComponent::default().range_kind,
            CoverEmitterRangeKind::Global
        );
    }

    /// to_data → from_data で値が完全に往復すること。
    #[test]
    fn data_round_trips() {
        let c = CoverEmitterComponent {
            range_kind: CoverEmitterRangeKind::TextureMask,
            extents: [1.0, 2.0, 3.0],
            fade: 0.5,
            mask_path: "assets://masks/snow.png".to_string(),
            mask_size: [64.0, 48.0],
            material_id: "leaf_carpet".to_string(),
            strength: 1.25,
            enabled: false,
        };
        let back = CoverEmitterComponent::from_data(&c.to_data());
        assert_eq!(back.to_data(), c.to_data());
    }

    /// 種別文字列が往復すること（C# ドロップダウンの Tag との契約）。
    #[test]
    fn range_kind_string_round_trips() {
        for k in [
            CoverEmitterRangeKind::Global,
            CoverEmitterRangeKind::Region,
            CoverEmitterRangeKind::TextureMask,
        ] {
            assert_eq!(CoverEmitterRangeKind::from_str_opt(k.as_str()), Some(k));
        }
        assert_eq!(CoverEmitterRangeKind::from_str_opt("Nope"), None);
    }

    /// **フィールドが 1 つも無い旧 .scene（`{}`）でも読み込めること**（serde default の要）。
    #[test]
    fn deserializes_from_empty_json_with_defaults() {
        let d: CoverEmitterComponentData =
            serde_json::from_str("{}").expect("空 JSON から既定値で復元できること");
        assert_eq!(d, CoverEmitterComponentData::default());
    }

    /// 一部フィールドだけを持つ JSON でも、残りは既定値で埋まること。
    #[test]
    fn deserializes_partial_json() {
        let d: CoverEmitterComponentData =
            serde_json::from_str(r#"{"strength":2.0}"#).expect("部分 JSON から復元できること");
        assert_eq!(d.strength, 2.0);
        assert_eq!(d.material_id, "snow", "未指定フィールドは既定値");
        assert_eq!(d.range_kind, CoverEmitterRangeKind::Region);
    }
}
