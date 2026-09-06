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
        }
    }
}

impl Default for TextComponent {
    fn default() -> Self {
        Self::from_data(TextComponentData::default())
    }
}

impl Component for TextComponent {}
