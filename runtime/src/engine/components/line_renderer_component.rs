// ============================================================
//  line_renderer_component.rs — 3D ポリライン描画コンポーネント
//
//  Actor に「点列を結ぶ 1 本の線」を持たせるコンポーネント。
//  釣り糸・ロープ・軌跡・照準線など「毎フレーム点列が変わる線」を
//  スクリプト（gameObject.LineRenderer.SetPoints）から駆動するのが主用途。
//
//  【データだけを持つ】
//  ECS の理念どおり、ここには描画ロジックを一切置かない。
//  ・点列 → リボン頂点への展開は methods/drawer/line_ribbon.rs（純関数）
//  ・シーン走査と GPU 投入は core/app_base/app/line_renderer_ops.rs
//
//  【座標系】
//  local_space = true （既定）: points はアクターの Transform（SEED の Transform は
//    ワールド空間で保持される＝親子合成は無い）を基準としたローカル座標。
//    描画時に `Transform::to_mat4()` を掛けてワールドへ写す。
//  local_space = false        : points はそのままワールド座標として扱う
//    （竿先とウキのように「別々のアクターの位置を結ぶ」用途はこちらが素直）。
//
//  【太さ】
//  width はワールド単位（メートル）の直径。カメラ方向と直交する向きへ
//  ±width/2 だけ広げたリボンとして描くため、遠景では細く見える
//  （ギズモの px 指定太線とはここが異なる）。
// ============================================================

use crate::engine::ecs::Component;
use serde::{Deserialize, Serialize};

// ─── 上限・デフォルト値 ───────────────────────────────────────

/// 1 本の線が持てる点の最大数。
///
/// スクリプト API（`SetPoints`）は「float 配列 1 回の書き込み」で点列を丸ごと差し替える。
/// FFI の 1 回書き込み上限（`host_api::MAX_FLOAT_WRITE_LEN`）はこの値 × 3 に一致させてある。
/// 釣り糸・ロープ用途では数十点で十分なので、512 は実用上の余裕を見た値。
pub const MAX_LINE_POINTS: usize = 512;

/// 既定の線の太さ（ワールド単位＝メートル）。釣り糸相当の細さ。
fn default_width() -> f32 {
    0.02
}

/// 既定の線色（白・不透明）。
fn default_color() -> [f32; 4] {
    [1.0, 1.0, 1.0, 1.0]
}

/// `local_space` / `depth_test` / `visible` の既定値（すべて true）。
fn default_true() -> bool {
    true
}

// ─── LineRendererComponentData ───────────────────────────────

/// LineRenderer のシリアライズ用データ。
///
/// 全フィールドに `#[serde(default)]` を付けること（旧 `.scene` 互換の要）。
#[derive(Clone, Serialize, Deserialize)]
pub struct LineRendererComponentData {
    /// 線を構成する点列。2 点未満なら何も描かれない。
    ///
    /// 実運用ではスクリプトが毎フレーム差し替えるため、シーンに保存されるのは
    /// 「エディタで置いた初期形状」だけであることが多い。
    #[serde(default)]
    pub points: Vec<[f32; 3]>,
    /// 線の太さ（ワールド単位）。0 以下なら描画しない。
    #[serde(default = "default_width")]
    pub width: f32,
    /// 線の色（RGBA・リニア）。アルファ < 1 で半透明合成される。
    #[serde(default = "default_color")]
    pub color: [f32; 4],
    /// points をアクターローカル座標として扱うか（false = ワールド座標）。
    #[serde(default = "default_true")]
    pub local_space: bool,
    /// 深度テストを行うか（true = 手前の不透明物に隠れる / false = 常に最前面）。
    #[serde(default = "default_true")]
    pub depth_test: bool,
    /// 描画するか。false でスロットを消さずに一時的に隠せる。
    #[serde(default = "default_true")]
    pub visible: bool,
}

impl Default for LineRendererComponentData {
    fn default() -> Self {
        Self {
            points: Vec::new(),
            width: default_width(),
            color: default_color(),
            local_space: default_true(),
            depth_test: default_true(),
            visible: default_true(),
        }
    }
}

// ─── LineRendererComponent ───────────────────────────────────

/// 3D ポリライン描画コンポーネント（ECS 実体）。
/// フィールド構成はシリアライズ用データと同一。
#[derive(Clone)]
pub struct LineRendererComponent {
    /// 線を構成する点列（`local_space` に従いローカル or ワールド）。
    pub points: Vec<[f32; 3]>,
    /// 線の太さ（ワールド単位）。
    pub width: f32,
    /// 線の色（RGBA・リニア）。
    pub color: [f32; 4],
    /// points をアクターローカル座標として扱うか。
    pub local_space: bool,
    /// 深度テストを行うか。
    pub depth_test: bool,
    /// 描画するか。
    pub visible: bool,
}

impl LineRendererComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    ///
    /// 点数が上限を超えるデータ（手書き `.scene` など）は上限で切り詰める。
    /// 描画側・FFI 側と上限を一致させ、想定外の巨大バッファ生成を防ぐため。
    pub fn from_data(data: LineRendererComponentData) -> Self {
        let mut points = data.points;
        points.truncate(MAX_LINE_POINTS);
        Self {
            points,
            width: data.width,
            color: data.color,
            local_space: data.local_space,
            depth_test: data.depth_test,
            visible: data.visible,
        }
    }

    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> LineRendererComponentData {
        LineRendererComponentData {
            points: self.points.clone(),
            width: self.width,
            color: self.color,
            local_space: self.local_space,
            depth_test: self.depth_test,
            visible: self.visible,
        }
    }
}

impl Default for LineRendererComponent {
    fn default() -> Self {
        Self::from_data(LineRendererComponentData::default())
    }
}

impl Component for LineRendererComponent {}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// シリアライズ往復で全フィールドが保存・復元されること。
    #[test]
    fn serde_roundtrip_preserves_all_fields() {
        let src = LineRendererComponentData {
            points: vec![[0.0, 1.0, 2.0], [3.0, 4.0, 5.0], [6.0, 7.0, 8.0]],
            width: 0.125,
            color: [0.1, 0.2, 0.3, 0.4],
            local_space: false,
            depth_test: false,
            visible: false,
        };
        let json = serde_json::to_string(&src).expect("シリアライズできること");
        let back: LineRendererComponentData =
            serde_json::from_str(&json).expect("デシリアライズできること");

        assert_eq!(back.points, src.points);
        assert_eq!(back.width, src.width);
        assert_eq!(back.color, src.color);
        assert_eq!(back.local_space, src.local_space);
        assert_eq!(back.depth_test, src.depth_test);
        assert_eq!(back.visible, src.visible);
    }

    /// コンポーネント実体 → データ → 実体の往復が値を保つこと。
    #[test]
    fn component_data_roundtrip() {
        let comp = LineRendererComponent {
            points: vec![[1.0, 0.0, 0.0], [2.0, 0.0, 0.0]],
            width: 0.5,
            color: [1.0, 0.0, 0.0, 0.5],
            local_space: false,
            depth_test: true,
            visible: true,
        };
        let back = LineRendererComponent::from_data(comp.to_data());
        assert_eq!(back.points, comp.points);
        assert_eq!(back.width, comp.width);
        assert_eq!(back.color, comp.color);
        assert_eq!(back.local_space, comp.local_space);
        assert_eq!(back.depth_test, comp.depth_test);
        assert_eq!(back.visible, comp.visible);
    }

    /// フィールドが欠落した旧シーン JSON でも読め、既定値で補完されること
    /// （`#[serde(default)]` 漏れの回帰検出）。
    #[test]
    fn missing_fields_fall_back_to_defaults() {
        let data: LineRendererComponentData =
            serde_json::from_str("{}").expect("空オブジェクトを読めること");
        assert!(data.points.is_empty());
        assert_eq!(data.width, default_width());
        assert_eq!(data.color, default_color());
        assert!(data.local_space, "local_space の既定は true（ローカル空間）");
        assert!(data.depth_test, "depth_test の既定は true");
        assert!(data.visible, "visible の既定は true");
    }

    /// 上限を超える点列は from_data で切り詰められること。
    #[test]
    fn from_data_truncates_to_max_points() {
        let data = LineRendererComponentData {
            points: vec![[0.0, 0.0, 0.0]; MAX_LINE_POINTS + 10],
            ..Default::default()
        };
        let comp = LineRendererComponent::from_data(data);
        assert_eq!(comp.points.len(), MAX_LINE_POINTS);
    }
}
