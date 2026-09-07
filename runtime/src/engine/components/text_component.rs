// ============================================================
//  text_component.rs — キャンバス用テキスト表示コンポーネント
//
//  【役割】
//  キャンバス（Actor2D / Actor3D + CanvasComponent）配下のアクターに
//  文字列を表示させるコンポーネント。資金表示・釣った魚のサイズ・
//  ゲージの数値など、Play 中に毎フレーム書き換わる HUD を担う。
//
//  【描画経路】
//  既存の SDF/ビットマップフォント描画（`core::font::FontSystem`）を流用する。
//  文字のクアッドはキャンバスピクセル実寸で組み、SpriteComponent とまったく
//  同じ変換連鎖（CanvasTransform → 親キャンバス → カメラ VP）を通す。
//  したがってアンカー・ピボット・スケールモード・親子関係はスプライトと同じ挙動になる。
//
//  【座標系】
//  キャンバスローカル座標（原点 = このアクターの位置、X 右・Y 下）。
//  `align` / `vertical_align` はテキスト全体のブロックを原点に対してどう置くかを決める。
//
//  【ECS の位置づけ】
//  本コンポーネントは**データのみ**を保持する（ECS 理念）。文字列の配置計算・
//  頂点生成は描画側（`core::font::canvas_text`）が毎フレーム行い、
//  ここには一切のロジックを持たせない。
// ============================================================

use crate::engine::ecs::Component;
use serde::{Deserialize, Serialize};

// ─── 既定値関数（マジックナンバーをここへ集約する）───────────────

/// フォントサイズの既定値（キャンバスピクセル）。
pub const DEFAULT_FONT_SIZE: f32 = 24.0;

/// 行送り倍率の既定値（フォントサイズに対する倍率）。
pub const DEFAULT_LINE_SPACING: f32 = 1.2;

/// 文字色の既定値（不透明な白）。
pub const DEFAULT_TEXT_COLOR: [f32; 4] = [1.0, 1.0, 1.0, 1.0];

/// 縁取り色の既定値（不透明な黒）。
pub const DEFAULT_OUTLINE_COLOR: [f32; 4] = [0.0, 0.0, 0.0, 1.0];

/// 縁取り太さの既定値（0 = 縁取りなし）。
pub const DEFAULT_OUTLINE_WIDTH: f32 = 0.0;

/// 縁取り太さの下限（負の太さは意味を持たない）。
pub const MIN_OUTLINE_WIDTH: f32 = 0.0;

/// 縁取り太さの上限（キャンバスピクセル）。
///
/// SDF のスプレッドを超える太さは頭打ちになる（`sdf::outline_px_to_sdf`）ので、
/// 入力段階でも常識的な範囲へ丸めておく。
pub const MAX_OUTLINE_WIDTH: f32 = 64.0;

/// 枠サイズ（幅・高さ）の下限。0 = 枠なし（従来どおりの原点基準レイアウト）。
pub const MIN_BOX_SIZE: f32 = 0.0;

/// 枠サイズ（幅・高さ）の上限（キャンバスピクセル）。
///
/// 4K の 4 倍まで許容する現実的な上限。これを超える値は入力段階で丸める
/// （巨大な枠で折り返し計算が無意味に走るのを防ぐ）。
pub const MAX_BOX_SIZE: f32 = 16384.0;

/// 自動折り返しの既定値（枠を設定したら折り返すのが期待挙動）。
///
/// `box_width = 0`（枠なし）のときは無視されるため、旧シーンの挙動は変わらない。
pub const DEFAULT_TEXT_WRAP: bool = true;

/// 文字の太さ（SDF しきい値オフセット）の既定値。0 = フォント本来の太さ。
pub const DEFAULT_TEXT_WEIGHT: f32 = 0.0;

/// 文字の太さの絶対値上限（キャンバスピクセル）。
///
/// SDF は四方に `SDF_SPREAD_EM`（= 0.125em）ぶんしか焼かれていないため、
/// 実効的にはフォントサイズの 1/8 で頭打ちになる。入力段階でも
/// 縁取り（`MAX_OUTLINE_WIDTH`）と同じ常識的な範囲へ丸めておく。
pub const MAX_TEXT_WEIGHT: f32 = MAX_OUTLINE_WIDTH;

/// ドロップシャドウのオフセット上限（キャンバスピクセル。絶対値）。
///
/// 影は本体と同じグリフを平行移動して描くだけなので、極端な値でも破綻はしないが、
/// 誤入力で画面外へ飛ぶのを防ぐために上限を設ける。
pub const MAX_SHADOW_OFFSET: f32 = 1024.0;

/// ドロップシャドウのぼかし幅の上限（キャンバスピクセル）。
///
/// SDF のスムース幅を広げてぼかすため、スプレッドを超えると効果が飽和する。
/// 縁取りと同じ上限にそろえる。
pub const MAX_SHADOW_SOFTNESS: f32 = MAX_OUTLINE_WIDTH;

/// ドロップシャドウのぼかし幅の下限（負のぼかしは意味を持たない）。
pub const MIN_SHADOW_SOFTNESS: f32 = 0.0;

/// ドロップシャドウ色の既定値（半透明の黒）。
pub const DEFAULT_SHADOW_COLOR: [f32; 4] = [0.0, 0.0, 0.0, 0.5];

/// 1 つの TextComponent が描画できる最大文字数。
///
/// スクリプトが誤って巨大な文字列を毎フレーム設定してもフレームバジェットを
/// 食い潰さないための安全弁（超過分は切り捨てて描画する）。
pub const MAX_TEXT_CHARS: usize = 4096;

fn default_font_size() -> f32 {
    DEFAULT_FONT_SIZE
}
fn default_line_spacing() -> f32 {
    DEFAULT_LINE_SPACING
}
fn default_color() -> [f32; 4] {
    DEFAULT_TEXT_COLOR
}
fn default_outline_color() -> [f32; 4] {
    DEFAULT_OUTLINE_COLOR
}
fn default_text_wrap() -> bool {
    DEFAULT_TEXT_WRAP
}
fn default_shadow_color() -> [f32; 4] {
    DEFAULT_SHADOW_COLOR
}

// ─── TextAlign ────────────────────────────────────────────────

/// テキストブロックの水平方向の基準位置。
///
/// アクター位置（CanvasTransform.position）に対して、行のどこを合わせるか。
/// `Left` = 行の左端が原点 / `Center` = 行の中央 / `Right` = 行の右端。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum TextAlign {
    #[default]
    Left,
    Center,
    Right,
}

impl TextAlign {
    /// IPC / スクリプト API で使う小文字キー（serde の表現と一致させること）。
    pub fn key(self) -> &'static str {
        match self {
            Self::Left => "left",
            Self::Center => "center",
            Self::Right => "right",
        }
    }

    /// キー文字列から復元する。未知の値は `None`（呼び出し側が既存値を保つ）。
    pub fn from_key(s: &str) -> Option<Self> {
        match s {
            "left" => Some(Self::Left),
            "center" => Some(Self::Center),
            "right" => Some(Self::Right),
            _ => None,
        }
    }
}

// ─── TextVerticalAlign ────────────────────────────────────────

/// テキストブロックの垂直方向の基準位置。
///
/// `Top` = ブロック上端が原点 / `Middle` = ブロック中央 / `Bottom` = ブロック下端。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum TextVerticalAlign {
    #[default]
    Top,
    Middle,
    Bottom,
}

impl TextVerticalAlign {
    /// IPC / スクリプト API で使う小文字キー（serde の表現と一致させること）。
    pub fn key(self) -> &'static str {
        match self {
            Self::Top => "top",
            Self::Middle => "middle",
            Self::Bottom => "bottom",
        }
    }

    /// キー文字列から復元する。未知の値は `None`（呼び出し側が既存値を保つ）。
    pub fn from_key(s: &str) -> Option<Self> {
        match s {
            "top" => Some(Self::Top),
            "middle" => Some(Self::Middle),
            "bottom" => Some(Self::Bottom),
            _ => None,
        }
    }
}

// ─── TextComponentData ────────────────────────────────────────

/// TextComponent のシリアライズ用データ（.scene 保存・Undo スナップショット）。
///
/// 全フィールドに `#[serde(default)]` を付けること（旧シーン互換の要）。
#[derive(Clone, Serialize, Deserialize)]
pub struct TextComponentData {
    /// 表示する文字列（改行 `\n` で複数行になる）。
    #[serde(default)]
    pub content: String,
    /// フォントサイズ（キャンバスピクセル）。
    #[serde(default = "default_font_size")]
    pub font_size: f32,
    /// 文字色（RGBA。0..1）。
    #[serde(default = "default_color")]
    pub color: [f32; 4],
    /// 水平方向の基準位置。
    #[serde(default)]
    pub align: TextAlign,
    /// 垂直方向の基準位置。
    #[serde(default)]
    pub vertical_align: TextVerticalAlign,
    /// 行送り（フォントサイズに対する倍率）。
    #[serde(default = "default_line_spacing")]
    pub line_spacing: f32,
    /// 描画レイヤー（大きいほど手前。SpriteComponent と同じ規約で共通ソートされる）。
    #[serde(default)]
    pub layer: i32,
    /// 使用フォントの assets:// 仮想パス。空文字 = 組み込みフォント。
    #[serde(default)]
    pub font_path: String,
    /// 縁取りの太さ（キャンバスピクセル）。0 = 縁取りなし。
    #[serde(default)]
    pub outline_width: f32,
    /// 縁取りの色（RGBA 0..1）。
    #[serde(default = "default_outline_color")]
    pub outline_color: [f32; 4],
    /// 枠の幅（キャンバスピクセル）。0 = 枠なし（従来どおりの原点基準レイアウト）。
    #[serde(default)]
    pub box_width: f32,
    /// 枠の最小高さ（キャンバスピクセル）。実際の高さは `max(box_height, 内容高さ)`。
    #[serde(default)]
    pub box_height: f32,
    /// 枠幅での自動折り返しを行うか（`box_width > 0` のときのみ有効）。
    #[serde(default = "default_text_wrap")]
    pub wrap: bool,
    /// 文字の太さ（キャンバスピクセル。負で細く・正で太く。0 = フォント本来）。
    #[serde(default)]
    pub weight: f32,
    /// ドロップシャドウの X オフセット（キャンバスピクセル）。
    #[serde(default)]
    pub shadow_offset_x: f32,
    /// ドロップシャドウの Y オフセット（キャンバスピクセル。Y は下向き）。
    #[serde(default)]
    pub shadow_offset_y: f32,
    /// ドロップシャドウの色（RGBA 0..1）。
    #[serde(default = "default_shadow_color")]
    pub shadow_color: [f32; 4],
    /// ドロップシャドウのぼかし幅（キャンバスピクセル。0 = シャープ）。
    #[serde(default)]
    pub shadow_softness: f32,
}

impl Default for TextComponentData {
    fn default() -> Self {
        Self {
            content: String::from("Text"),
            font_size: default_font_size(),
            color: default_color(),
            align: TextAlign::default(),
            vertical_align: TextVerticalAlign::default(),
            line_spacing: default_line_spacing(),
            layer: 0,
            font_path: String::new(),
            outline_width: DEFAULT_OUTLINE_WIDTH,
            outline_color: default_outline_color(),
            box_width: MIN_BOX_SIZE,
            box_height: MIN_BOX_SIZE,
            wrap: default_text_wrap(),
            weight: DEFAULT_TEXT_WEIGHT,
            shadow_offset_x: 0.0,
            shadow_offset_y: 0.0,
            shadow_color: default_shadow_color(),
            shadow_softness: MIN_SHADOW_SOFTNESS,
        }
    }
}

// ─── TextComponent ────────────────────────────────────────────

/// キャンバス用テキスト表示コンポーネント（ECS 実体）。
/// フィールド構成はシリアライズ用データと同一。
#[derive(Clone)]
pub struct TextComponent {
    /// 表示する文字列（改行 `\n` で複数行）。
    pub content: String,
    /// フォントサイズ（キャンバスピクセル）。
    pub font_size: f32,
    /// 文字色（RGBA。0..1）。
    pub color: [f32; 4],
    /// 水平方向の基準位置。
    pub align: TextAlign,
    /// 垂直方向の基準位置。
    pub vertical_align: TextVerticalAlign,
    /// 行送り（フォントサイズに対する倍率）。
    pub line_spacing: f32,
    /// 描画レイヤー（大きいほど手前）。
    pub layer: i32,
    /// 使用フォントの assets:// 仮想パス。空文字 = 組み込みフォント。
    pub font_path: String,
    /// 縁取りの太さ（キャンバスピクセル）。0 = 縁取りなし。
    pub outline_width: f32,
    /// 縁取りの色（RGBA 0..1）。
    pub outline_color: [f32; 4],
    /// 枠の幅（キャンバスピクセル）。0 = 枠なし。
    ///
    /// **0 か否かでレイアウト規則が切り替わる要のフィールド**。
    /// 0: align / vertical_align はアクター原点に対するブロック配置、pivot は無効。
    /// 正: align / vertical_align は枠内での配置、pivot は Sprite と同じ意味を持つ。
    pub box_width: f32,
    /// 枠の最小高さ（キャンバスピクセル）。実際の高さは `max(box_height, 内容高さ)`。
    pub box_height: f32,
    /// 枠幅での自動折り返しを行うか（`box_width > 0` のときのみ有効）。
    pub wrap: bool,
    /// 文字の太さ（キャンバスピクセル。負で細く・正で太く。0 = フォント本来）。
    pub weight: f32,
    /// ドロップシャドウの X オフセット（キャンバスピクセル）。
    pub shadow_offset_x: f32,
    /// ドロップシャドウの Y オフセット（キャンバスピクセル。Y は下向き）。
    pub shadow_offset_y: f32,
    /// ドロップシャドウの色（RGBA 0..1）。
    pub shadow_color: [f32; 4],
    /// ドロップシャドウのぼかし幅（キャンバスピクセル。0 = シャープ）。
    pub shadow_softness: f32,
}

impl TextComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    pub fn from_data(data: TextComponentData) -> Self {
        Self {
            content: data.content,
            font_size: data.font_size,
            color: data.color,
            align: data.align,
            vertical_align: data.vertical_align,
            line_spacing: data.line_spacing,
            layer: data.layer,
            font_path: data.font_path,
            outline_width: data.outline_width,
            outline_color: data.outline_color,
            box_width: data.box_width,
            box_height: data.box_height,
            wrap: data.wrap,
            weight: data.weight,
            shadow_offset_x: data.shadow_offset_x,
            shadow_offset_y: data.shadow_offset_y,
            shadow_color: data.shadow_color,
            shadow_softness: data.shadow_softness,
        }
    }

    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> TextComponentData {
        TextComponentData {
            content: self.content.clone(),
            font_size: self.font_size,
            color: self.color,
            align: self.align,
            vertical_align: self.vertical_align,
            line_spacing: self.line_spacing,
            layer: self.layer,
            font_path: self.font_path.clone(),
            outline_width: self.outline_width,
            outline_color: self.outline_color,
            box_width: self.box_width,
            box_height: self.box_height,
            wrap: self.wrap,
            weight: self.weight,
            shadow_offset_x: self.shadow_offset_x,
            shadow_offset_y: self.shadow_offset_y,
            shadow_color: self.shadow_color,
            shadow_softness: self.shadow_softness,
        }
    }
}

impl Default for TextComponent {
    fn default() -> Self {
        Self::from_data(TextComponentData::default())
    }
}

impl Component for TextComponent {}

// ============================================================
//  単体テスト（serde 互換 = 旧 .scene が読めることの保証）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 新フィールドを持たない旧 .scene の JSON が既定値で読める。
    ///
    /// ここが落ちると「そのフィールドが無いシーンの読み込みが丸ごと失敗」する。
    #[test]
    fn legacy_scene_json_loads_with_defaults() {
        let json = r#"{
            "content": "所持金",
            "font_size": 32.0,
            "color": [1.0, 1.0, 1.0, 1.0],
            "align": "center",
            "vertical_align": "middle",
            "line_spacing": 1.2,
            "layer": 3,
            "font_path": "",
            "outline_width": 2.0,
            "outline_color": [0.0, 0.0, 0.0, 1.0]
        }"#;
        let d: TextComponentData = serde_json::from_str(json).expect("旧シーンが読める");
        // 枠なし = 従来レイアウト。折り返しフラグは既定 true だが枠なしでは無視される。
        assert_eq!(d.box_width, MIN_BOX_SIZE);
        assert_eq!(d.box_height, MIN_BOX_SIZE);
        assert!(d.wrap);
        assert_eq!(d.weight, DEFAULT_TEXT_WEIGHT);
        assert_eq!(d.shadow_offset_x, 0.0);
        assert_eq!(d.shadow_offset_y, 0.0);
        assert_eq!(d.shadow_color, DEFAULT_SHADOW_COLOR);
        assert_eq!(d.shadow_softness, MIN_SHADOW_SOFTNESS);
        // 既存フィールドが壊れていないことも併せて確認する。
        assert_eq!(d.content, "所持金");
        assert_eq!(d.align, TextAlign::Center);
        assert_eq!(d.vertical_align, TextVerticalAlign::Middle);
    }

    /// from_data / to_data が新フィールドを往復できる（Undo のスナップショット経路）。
    #[test]
    fn from_data_to_data_roundtrips_new_fields() {
        let mut data = TextComponentData::default();
        data.box_width = 320.0;
        data.box_height = 120.0;
        data.wrap = false;
        data.weight = -1.5;
        data.shadow_offset_x = 2.0;
        data.shadow_offset_y = 3.0;
        data.shadow_color = [1.0, 0.0, 0.0, 0.25];
        data.shadow_softness = 4.0;
        let back = TextComponent::from_data(data.clone()).to_data();
        assert_eq!(back.box_width, data.box_width);
        assert_eq!(back.box_height, data.box_height);
        assert_eq!(back.wrap, data.wrap);
        assert_eq!(back.weight, data.weight);
        assert_eq!(back.shadow_offset_x, data.shadow_offset_x);
        assert_eq!(back.shadow_offset_y, data.shadow_offset_y);
        assert_eq!(back.shadow_color, data.shadow_color);
        assert_eq!(back.shadow_softness, data.shadow_softness);
    }
}
