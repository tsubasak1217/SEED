// ============================================================
//  light_component.rs — ライトコンポーネント
//
//  Actor にライト（光源）を持たせる ECS スロットコンポーネント。
//  本コンポーネントは「光源のパラメータ（種別・色・強度・減衰など）」の
//  データのみを保持する（ECS 理念：データとロジックの分離）。
//
//  向き・位置は Actor の Transform から解決する（本コンポーネント自身は
//  位置・回転を持たない）。実際の GPU ライトバッファへの収集・変換は
//  レンダラ側の lighting_ops / lighting.rs が毎フレーム行う。
//
//  【向きの慣例】
//  directional / spot の「照射方向」は Actor の Transform::forward()
//  （YXZ オイラー角の +Z 前方ベクトル）を用いる。すなわち
//  「ライトアクターが向いている方向へ光が進む」。
//  シェーダ側では L（面から光源への方向）を -forward で求める。
//
//  【シリアライズ】
//  全フィールドに #[serde(default)]（非ゼロ既定は default fn）を付け、
//  旧 .scene（フィールド欠落）でも読み込みが失敗しないようにする。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

// ─── デフォルト値関数 ─────────────────────────────────────────
// マジックナンバー禁止のため、非ゼロ既定値はすべて関数に切り出す。

/// color の既定値（白）。
fn default_color() -> [f32; 3] { [1.0, 1.0, 1.0] }
/// intensity の既定値。旧ハードコード方向光の輝度 vec3(3.0) に相当。
fn default_intensity() -> f32 { 3.0 }
/// range（point/spot の減衰距離）の既定値。
fn default_range() -> f32 { 10.0 }
/// spot 内側コーン角（度）の既定値。
fn default_inner_angle_deg() -> f32 { 25.0 }
/// spot 外側コーン角（度）の既定値。
fn default_outer_angle_deg() -> f32 { 35.0 }
/// rect（エリアライト）の幅の既定値。
fn default_rect_width() -> f32 { 1.0 }
/// rect（エリアライト）の高さの既定値。
fn default_rect_height() -> f32 { 1.0 }
/// cast_shadows の既定値（true）。R2 のシャドウで使用（R1 では保存のみ）。
fn default_cast_shadows() -> bool { true }
/// ソフト影の見込み半径の既定値（Phase R8 ソフトシャドウ）。
///
/// 意味は種別で異なる（GpuLight へ変換する light_ops.rs 側で解釈）:
///   - directional: 光源の角径（度）。既定 0.25 は太陽の実角径に近く、影が「わずかに柔らかく」なる。
///   - point/spot/rect: 光源のワールド半径。0.25 単位程度の面光源として扱う。
/// 0 にするとハードシャドウ（遮蔽レイ 1 本）になる。RT 影が有効なときのみ効果がある。
fn default_soft_radius() -> f32 { 0.25 }

// ─── LightKind ───────────────────────────────────────────────

/// ライトの種別。
///
/// - `Directional`: 平行光（減衰なし・向きのみ有効）。既定。
/// - `Point`      : 点光源（位置から全方向・range で減衰）。
/// - `Spot`       : スポット光（位置＋向き＋内外コーン角で円錐減衰）。
/// - `Rect`       : 矩形エリアライト（面光源。R1 では簡易近似で光らせる）。
///
/// serde は snake_case でシリアライズする（`"directional"` 等）。
/// 旧シーン（kind 欠落）は `#[serde(default)]` により Directional になる。
#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Debug)]
#[serde(rename_all = "snake_case")]
pub enum LightKind {
    /// 平行光（既定）
    Directional,
    /// 点光源
    Point,
    /// スポット光
    Spot,
    /// 矩形エリアライト
    Rect,
}

impl Default for LightKind {
    /// kind 省略時の既定は directional（後方互換：旧ハードコード光は方向光だった）。
    fn default() -> Self { LightKind::Directional }
}

impl LightKind {
    /// GPU ライト構造体へ渡す種別コード（シェーダの分岐と一致させる）。
    ///
    /// シェーダ側 `LIGHT_KIND_*` 定数と厳密に一致させること。
    pub fn to_code(self) -> u32 {
        match self {
            LightKind::Directional => 0,
            LightKind::Point       => 1,
            LightKind::Spot        => 2,
            LightKind::Rect        => 3,
        }
    }

    /// IPC 文字列（インスペクタのドロップダウン Tag）との相互変換。
    pub fn from_str_opt(s: &str) -> Option<Self> {
        match s {
            "directional" => Some(LightKind::Directional),
            "point"       => Some(LightKind::Point),
            "spot"        => Some(LightKind::Spot),
            "rect"        => Some(LightKind::Rect),
            _             => None,
        }
    }

    /// インスペクタへ送る種別文字列。
    pub fn as_str(self) -> &'static str {
        match self {
            LightKind::Directional => "directional",
            LightKind::Point       => "point",
            LightKind::Spot        => "spot",
            LightKind::Rect        => "rect",
        }
    }
}

// ─── LightComponentData（シリアライズ用）─────────────────────

/// LightComponent のシリアライズ用データ（.scene 保存・Undo スナップショット）。
#[derive(Clone, Serialize, Deserialize, Debug)]
pub struct LightComponentData {
    /// ライト種別（既定 directional）
    #[serde(default)]
    pub kind: LightKind,
    /// 光の色（リニア RGB、既定 [1,1,1]）
    #[serde(default = "default_color")]
    pub color: [f32; 3],
    /// 光の強度（色に乗算。既定 3.0＝旧方向光相当）
    #[serde(default = "default_intensity")]
    pub intensity: f32,
    /// point/spot の減衰距離（この距離でおおよそ消灯。既定 10.0）
    #[serde(default = "default_range")]
    pub range: f32,
    /// spot 内側コーン角（度）。この角度内は全光量。既定 25。
    #[serde(default = "default_inner_angle_deg")]
    pub inner_angle_deg: f32,
    /// spot 外側コーン角（度）。この角度でゼロまで減衰。既定 35。
    #[serde(default = "default_outer_angle_deg")]
    pub outer_angle_deg: f32,
    /// rect エリアライトの幅（ワールド単位）。既定 1.0。
    #[serde(default = "default_rect_width")]
    pub rect_width: f32,
    /// rect エリアライトの高さ（ワールド単位）。既定 1.0。
    #[serde(default = "default_rect_height")]
    pub rect_height: f32,
    /// 影を落とすか（R2 のシャドウマップで使用。R1 では保存のみ）。既定 true。
    #[serde(default = "default_cast_shadows")]
    pub cast_shadows: bool,
    /// ソフト影の見込み半径（Phase R8）。directional=角径(度) / 局所光=ワールド半径。
    /// 0 でハードシャドウ。RT 影が有効なときのみ効く。既定 0.25。
    #[serde(default = "default_soft_radius")]
    pub soft_radius: f32,
}

impl Default for LightComponentData {
    fn default() -> Self {
        Self {
            kind:            LightKind::default(),
            color:           default_color(),
            intensity:       default_intensity(),
            range:           default_range(),
            inner_angle_deg: default_inner_angle_deg(),
            outer_angle_deg: default_outer_angle_deg(),
            rect_width:      default_rect_width(),
            rect_height:     default_rect_height(),
            cast_shadows:    default_cast_shadows(),
            soft_radius:     default_soft_radius(),
        }
    }
}

// ─── LightComponent（ECS 実体）───────────────────────────────

/// ライトコンポーネント（ECS 実体）。
///
/// 保持するのはライトのパラメータのみ。位置・向きは Actor の Transform から
/// レンダラが毎フレーム解決する。揮発状態は持たない（Data と同一構成）。
#[derive(Clone, Debug)]
pub struct LightComponent {
    pub kind:            LightKind,
    pub color:           [f32; 3],
    pub intensity:       f32,
    pub range:           f32,
    pub inner_angle_deg: f32,
    pub outer_angle_deg: f32,
    pub rect_width:      f32,
    pub rect_height:     f32,
    pub cast_shadows:    bool,
    pub soft_radius:     f32,
}

impl LightComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    pub fn from_data(data: LightComponentData) -> Self {
        Self {
            kind:            data.kind,
            color:           data.color,
            intensity:       data.intensity,
            range:           data.range,
            inner_angle_deg: data.inner_angle_deg,
            outer_angle_deg: data.outer_angle_deg,
            rect_width:      data.rect_width,
            rect_height:     data.rect_height,
            cast_shadows:    data.cast_shadows,
            soft_radius:     data.soft_radius,
        }
    }

    /// シリアライズ用データへ変換する。
    pub fn to_data(&self) -> LightComponentData {
        LightComponentData {
            kind:            self.kind,
            color:           self.color,
            intensity:       self.intensity,
            range:           self.range,
            inner_angle_deg: self.inner_angle_deg,
            outer_angle_deg: self.outer_angle_deg,
            rect_width:      self.rect_width,
            rect_height:     self.rect_height,
            cast_shadows:    self.cast_shadows,
            soft_radius:     self.soft_radius,
        }
    }
}

impl Default for LightComponent {
    fn default() -> Self { Self::from_data(LightComponentData::default()) }
}

impl Component for LightComponent {}
