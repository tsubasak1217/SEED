// ============================================================
//  sprite_style/fill.rs — スプライトの塗り（単色・線形・放射のグラデーション。W2-4）
//
//  塗りの色は「テクスチャの色 × 塗りの色 × スプライトの color」（color は従来どおりの乗算の色＝不透明度の
//  フェードもそのまま効く）。単色（既定）は塗りの色 = 白（＝従来と同じ tex × color）。
//    - Linear … 2〜4 色。角度 `angle`（度。0 = 左 → 右、90 = 上 → 下。時計回りが正）の直線に沿って変わる。
//               線の長さは矩形の四隅がちょうど 0 と 1 に乗る長さ（CSS の linear-gradient と同じ）
//    - Radial … 2〜4 色。中心 `center`（矩形に対する割合 0..1）から、半径 `radius`（幅・高さに対する割合）の
//               楕円の縁で 1 になる（既定は矩形に内接する楕円）
//  色の位置 `stops`（0..1・昇順）が空なら等間隔。色の数より短い・範囲の外・逆順は描くときに直す
//  （engine/core/renderer/ui_shape/fill.rs の `normalized_stops`）。色の補間は乗算済みアルファ（CSS と同じ）。
// ============================================================

use serde::{Deserialize, Serialize};

/// グラデーションの色の数の下限。
pub const GRADIENT_MIN_COLORS: usize = 2;
/// グラデーションの色の数の上限（シェーダーの色の表の大きさ）。
pub const GRADIENT_MAX_COLORS: usize = 4;

/// 線形グラデーションの角度の既定（度）。90 = 上 → 下。
const DEFAULT_ANGLE_DEG: f32 = 90.0;
/// 放射グラデーションの中心の既定（矩形の中央）。
const DEFAULT_CENTER: [f32; 2] = [0.5, 0.5];
/// 放射グラデーションの半径の既定（幅・高さの半分＝矩形に内接する楕円）。
const DEFAULT_RADIUS: [f32; 2] = [0.5, 0.5];
/// グラデーションの色の既定（白 → 黒。インスペクタで種類を切り替えた直下に何か見えるように）。
const DEFAULT_COLORS: [[f32; 4]; 2] = [[1.0, 1.0, 1.0, 1.0], [0.0, 0.0, 0.0, 1.0]];

/// serde の既定値: グラデーションの色。
fn default_colors() -> Vec<[f32; 4]> {
    DEFAULT_COLORS.to_vec()
}

/// serde の既定値: 角度。
fn default_angle() -> f32 {
    DEFAULT_ANGLE_DEG
}

/// serde の既定値: 放射の中心。
fn default_center() -> [f32; 2] {
    DEFAULT_CENTER
}

/// serde の既定値: 放射の半径。
fn default_radius() -> [f32; 2] {
    DEFAULT_RADIUS
}

/// 塗りの種類。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SpriteFillKind {
    /// 単色（スプライトの color。既定）。
    #[default]
    Solid,
    /// 線形グラデーション。
    Linear,
    /// 放射グラデーション。
    Radial,
}

impl SpriteFillKind {
    /// 保存・IPC・スクリプトで使う名前（serde の名前と同じ）。
    pub fn key(self) -> &'static str {
        match self {
            Self::Solid => "solid",
            Self::Linear => "linear",
            Self::Radial => "radial",
        }
    }

    /// 名前から種類を引く（知らない名前は None）。
    pub fn from_key(key: &str) -> Option<Self> {
        match key.trim() {
            "solid" => Some(Self::Solid),
            "linear" => Some(Self::Linear),
            "radial" => Some(Self::Radial),
            _ => None,
        }
    }
}

/// スプライトの塗り。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct SpriteFill {
    /// 塗りの種類。
    #[serde(default)]
    pub kind: SpriteFillKind,
    /// グラデーションの色（2〜4 色。RGBA 0..1。スプライトの color が掛かる）。
    #[serde(default = "default_colors")]
    pub colors: Vec<[f32; 4]>,
    /// 色の位置（0..1・昇順。空なら等間隔）。
    #[serde(default)]
    pub stops: Vec<f32>,
    /// 線形の角度（度。0 = 左 → 右、90 = 上 → 下）。
    #[serde(default = "default_angle")]
    pub angle: f32,
    /// 放射の中心（矩形に対する割合。0,0 = 左上）。
    #[serde(default = "default_center")]
    pub center: [f32; 2],
    /// 放射の半径（幅・高さに対する割合）。
    #[serde(default = "default_radius")]
    pub radius: [f32; 2],
}

impl Default for SpriteFill {
    fn default() -> Self {
        Self {
            kind: SpriteFillKind::Solid,
            colors: default_colors(),
            stops: Vec::new(),
            angle: DEFAULT_ANGLE_DEG,
            center: DEFAULT_CENTER,
            radius: DEFAULT_RADIUS,
        }
    }
}

impl SpriteFill {
    /// すべての欄が既定か（保存を省く判定。`skip_serializing_if`）。
    pub fn is_default(&self) -> bool {
        *self == Self::default()
    }

    /// 単色か（グラデーションの欄は使わない）。
    pub fn is_solid(&self) -> bool {
        self.kind == SpriteFillKind::Solid
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 欄の無い旧データ（`{}`）は既定（単色）で読める。
    #[test]
    fn empty_json_is_solid_default() {
        let fill: SpriteFill = serde_json::from_str("{}").unwrap();
        assert!(fill.is_default());
        assert!(fill.is_solid());
        assert_eq!(fill.colors.len(), GRADIENT_MIN_COLORS);
    }

    /// 書いた欄だけが変わる（残りは既定）。
    #[test]
    fn partial_json_keeps_other_defaults() {
        let fill: SpriteFill =
            serde_json::from_str(r#"{"kind":"linear","colors":[[1,0,0,1],[0,1,0,1],[0,0,1,1]]}"#).unwrap();
        assert_eq!(fill.kind, SpriteFillKind::Linear);
        assert_eq!(fill.colors.len(), 3);
        assert_eq!(fill.angle, DEFAULT_ANGLE_DEG);
        assert!(!fill.is_default());
    }

    /// 種類の名前の往復と、serde の名前が同じであること。
    #[test]
    fn kind_keys_round_trip() {
        for kind in [SpriteFillKind::Solid, SpriteFillKind::Linear, SpriteFillKind::Radial] {
            assert_eq!(SpriteFillKind::from_key(kind.key()), Some(kind));
            assert_eq!(serde_json::to_string(&kind).unwrap(), format!("\"{}\"", kind.key()));
        }
    }
}
