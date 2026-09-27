// ============================================================
//  sprite_style/shape.rs — スプライトの形（矩形・楕円・弧）と縁の線（W2-4）
//
//  形はスプライトの矩形 [0, 幅]×[0, 高さ]（キャンバスの単位）の中に置く:
//    - Rect    … 矩形。四隅ごとの角丸（左上・右上・右下・左下）。半径 0 なら角は直角
//    - Ellipse … 矩形に内接する楕円（正方形なら円。丸いアイコン・丸いボタン）
//    - Arc     … 矩形の中心に置いた円の弧（リング）。外側の半径 = 短い辺の半分、太さ `arc_thickness`、
//                開始角 `arc_start`（度）から `arc_sweep`（度）だけ時計回り。進捗の輪（ProgressRing）に使う
//  縁の線（`border_width` > 0）は形の**内側**に引く（CSS の box-sizing: border-box と同じ。外形は変わらない）。
//  描き方（SDF）の正典は engine/core/renderer/ui_shape/sdf.rs。
// ============================================================

use serde::{Deserialize, Serialize};

/// 弧の開始角の既定（度）。-90 = 真上（12 時の位置）。進捗の輪が上から時計回りに伸びる。
const DEFAULT_ARC_START_DEG: f32 = -90.0;
/// 弧の角度の既定（度）。360 = 一周（リング）。
const DEFAULT_ARC_SWEEP_DEG: f32 = 360.0;
/// 弧の太さの既定（キャンバスの単位）。
const DEFAULT_ARC_THICKNESS: f32 = 8.0;
/// 縁の色の既定（不透明の黒）。
const DEFAULT_BORDER_COLOR: [f32; 4] = [0.0, 0.0, 0.0, 1.0];

/// serde の既定値: 弧の開始角。
fn default_arc_start() -> f32 {
    DEFAULT_ARC_START_DEG
}

/// serde の既定値: 弧の角度。
fn default_arc_sweep() -> f32 {
    DEFAULT_ARC_SWEEP_DEG
}

/// serde の既定値: 弧の太さ。
fn default_arc_thickness() -> f32 {
    DEFAULT_ARC_THICKNESS
}

/// serde の既定値: 縁の色。
fn default_border_color() -> [f32; 4] {
    DEFAULT_BORDER_COLOR
}

/// 形の種類。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SpriteShapeKind {
    /// 矩形（四隅ごとの角丸。既定）。
    #[default]
    Rect,
    /// 矩形に内接する楕円（正方形なら円）。
    Ellipse,
    /// 矩形の中心に置いた円の弧（リング）。
    Arc,
}

impl SpriteShapeKind {
    /// 保存・IPC・スクリプトで使う名前（serde の名前と同じ）。
    pub fn key(self) -> &'static str {
        match self {
            Self::Rect => "rect",
            Self::Ellipse => "ellipse",
            Self::Arc => "arc",
        }
    }

    /// 名前から種類を引く（知らない名前は None）。
    pub fn from_key(key: &str) -> Option<Self> {
        match key.trim() {
            "rect" => Some(Self::Rect),
            "ellipse" => Some(Self::Ellipse),
            "arc" => Some(Self::Arc),
            _ => None,
        }
    }
}

/// スプライトの形と縁の線。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct SpriteShape {
    /// 形の種類。
    #[serde(default)]
    pub kind: SpriteShapeKind,
    /// 四隅の角丸の半径（左上・右上・右下・左下。キャンバスの単位）。Rect のときだけ効く。
    /// 辺の長さを超える指定は描くときに CSS と同じ規則で縮める（隣り合う 2 つの和が辺の長さに収まるよう一律に）。
    #[serde(default)]
    pub corner_radii: [f32; 4],
    /// 縁の線の太さ（キャンバスの単位。0 = 縁なし）。形の内側に引く。
    #[serde(default)]
    pub border_width: f32,
    /// 縁の線の色（RGBA 0..1。スプライトの `color`〈塗りの色〉とは独立。塗りを透明にしても縁は見える）。
    #[serde(default = "default_border_color")]
    pub border_color: [f32; 4],
    /// 弧の開始角（度。0 = +X・時計回りが正。Arc のときだけ効く）。
    #[serde(default = "default_arc_start")]
    pub arc_start: f32,
    /// 弧の角度（度。0〜360。360 以上は一周のリング。Arc のときだけ効く）。
    #[serde(default = "default_arc_sweep")]
    pub arc_sweep: f32,
    /// 弧の太さ（キャンバスの単位。Arc のときだけ効く）。
    #[serde(default = "default_arc_thickness")]
    pub arc_thickness: f32,
    /// 弧の端を丸くするか（false = 切りっぱなし。Arc のときだけ効く）。
    #[serde(default)]
    pub arc_round_caps: bool,
}

impl Default for SpriteShape {
    fn default() -> Self {
        Self {
            kind: SpriteShapeKind::Rect,
            corner_radii: [0.0; 4],
            border_width: 0.0,
            border_color: DEFAULT_BORDER_COLOR,
            arc_start: DEFAULT_ARC_START_DEG,
            arc_sweep: DEFAULT_ARC_SWEEP_DEG,
            arc_thickness: DEFAULT_ARC_THICKNESS,
            arc_round_caps: false,
        }
    }
}

impl SpriteShape {
    /// すべての欄が既定か（保存を省く判定。`skip_serializing_if`）。
    pub fn is_default(&self) -> bool {
        *self == Self::default()
    }

    /// 従来のスプライトと同じ「角の直角な矩形・縁なし」か（形の SDF を使わずに描ける）。
    ///
    /// 縁の色・弧の欄は Rect では使わないので見ない。
    pub fn is_plain_rect(&self) -> bool {
        self.kind == SpriteShapeKind::Rect
            && self.corner_radii.iter().all(|r| *r <= 0.0)
            && self.border_width <= 0.0
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 欄の無い旧データ（`{}`）は既定で読め、既定は「直角の矩形・縁なし」。
    #[test]
    fn empty_json_is_the_plain_default() {
        let shape: SpriteShape = serde_json::from_str("{}").unwrap();
        assert!(shape.is_default());
        assert!(shape.is_plain_rect());
    }

    /// 角丸・縁・楕円・弧はどれも「直角の矩形」ではない。
    #[test]
    fn plain_rect_detection() {
        let rounded = SpriteShape { corner_radii: [0.0, 4.0, 0.0, 0.0], ..SpriteShape::default() };
        assert!(!rounded.is_plain_rect());
        let bordered = SpriteShape { border_width: 1.0, ..SpriteShape::default() };
        assert!(!bordered.is_plain_rect());
        let ellipse = SpriteShape { kind: SpriteShapeKind::Ellipse, ..SpriteShape::default() };
        assert!(!ellipse.is_plain_rect());
        // 縁の色だけ変えても直角の矩形のまま（縁が無ければ見えない）
        let color_only = SpriteShape { border_color: [1.0, 0.0, 0.0, 1.0], ..SpriteShape::default() };
        assert!(color_only.is_plain_rect());
        assert!(!color_only.is_default());
    }

    /// 種類の名前の往復と、serde の名前が同じであること。
    #[test]
    fn kind_keys_round_trip() {
        for kind in [SpriteShapeKind::Rect, SpriteShapeKind::Ellipse, SpriteShapeKind::Arc] {
            assert_eq!(SpriteShapeKind::from_key(kind.key()), Some(kind));
            let json = serde_json::to_string(&kind).unwrap();
            assert_eq!(json, format!("\"{}\"", kind.key()));
        }
        assert_eq!(SpriteShapeKind::from_key("star"), None);
    }
}
