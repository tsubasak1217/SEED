// ============================================================
//  water_volume_component.rs — 水ボリュームコンポーネント
//
//  Actor に「水」を持たせる ECS スロットコンポーネント。
//  本コンポーネントは水の形状定義（種別・水面高さ・範囲）と見た目パラメータ
//  （色・フォーム・波・フレネル・屈折）の *データのみ* を保持する
//  （ECS 理念：データとロジックの分離）。
//
//  【位置の解決】
//  水面のワールド位置はアクターの Transform から解決する（本コンポーネント
//  自身は位置を持たない）。ワールド空間への解決は engine::water::collect が
//  行い、描画・問い合わせの双方はその中間表現 ResolvedWaterVolume だけを見る。
//
//  【種別ごとの意味の差】
//    Ocean  … XZ 無限の大洋。surface_height は「ワールド Y 絶対値」。
//    Region … 直方体の水塊。surface_height は「アクタ原点からの相対 Y」。
//    Spline … 川（W4 で実装予定）。現状は描画・問い合わせともに無視される。
//
//  【流速について】
//  W1 では流速フィールドを持たない（WaterQuery::flow_at は常にゼロを返す）。
//  川スプラインの流速は W4 で追加する。
//
//  【シリアライズ】
//  全フィールドに #[serde(default)]（非ゼロ既定は default fn）を付け、
//  旧 .scene（フィールド欠落）でも読み込みが失敗しないようにする。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

// ─── デフォルト値関数 ─────────────────────────────────────────
// マジックナンバー禁止のため、非ゼロ既定値はすべて関数に切り出す。

/// region_half_extents の既定値（ローカル AABB 半径：横 10m・縦 5m・奥 10m）。
fn default_region_half_extents() -> [f32; 3] { [10.0, 5.0, 10.0] }
/// ocean_extent の既定値（大洋クアッドの片側半径 2km）。
fn default_ocean_extent() -> f32 { 2000.0 }
/// shallow_color の既定値（浅場の緑がかった水色・リニア）。
fn default_shallow_color() -> [f32; 3] { [0.10, 0.45, 0.42] }
/// deep_color の既定値（深場の濃紺・リニア）。
fn default_deep_color() -> [f32; 3] { [0.01, 0.06, 0.12] }
/// absorption_distance の既定値（8m 進むと深場の色へほぼ収束する）。
fn default_absorption_distance() -> f32 { 8.0 }
/// surface_opacity の既定値（深場での最大不透明度）。
fn default_surface_opacity() -> f32 { 0.92 }
/// foam_color の既定値（白）。
fn default_foam_color() -> [f32; 3] { [1.0, 1.0, 1.0] }
/// foam_width の既定値（この水深より浅い所にフォームが出る）。
fn default_foam_width() -> f32 { 0.35 }
/// foam_intensity の既定値（フォームの濃さ）。
fn default_foam_intensity() -> f32 { 0.8 }
/// wave_amplitude の既定値（法線摂動の強さ。形状は変えず陰影だけ揺らす）。
fn default_wave_amplitude() -> f32 { 0.06 }
/// wave_scale の既定値（波の空間周波数 1/m）。
fn default_wave_scale() -> f32 { 0.12 }
/// wave_speed の既定値（波のスクロール速度）。
fn default_wave_speed() -> f32 { 0.6 }
/// fresnel_power の既定値（Schlick 近似の指数）。
fn default_fresnel_power() -> f32 { 5.0 }
/// fresnel_strength の既定値（フレネル反射の寄与率）。
fn default_fresnel_strength() -> f32 { 1.0 }
/// reflection_color の既定値（浅い角度で映る空の簡易色・リニア）。
fn default_reflection_color() -> [f32; 3] { [0.35, 0.50, 0.62] }
/// refraction_distortion の既定値（屈折 UV の最大歪み。画面比）。
fn default_refraction_distortion() -> f32 { 0.03 }
/// ripple_strength の既定値（波紋の法線摂動スケール。1.0 = 標準）。
///
/// インタラクションフィールドの波高勾配を水面法線へ足す際の倍率。
/// 0 にすると波紋・航跡の表示だけを切れる（場の計算自体は他の消費者と共有のため止まらない）。
fn default_ripple_strength() -> f32 { 1.0 }
/// ripple_foam_threshold の既定値（この波高（m 相当）を超えた所に航跡の泡が出る）。
///
/// 歩行が立てる波（振幅 0.03 前後）では泡が出ず、走り・飛び込みで出る値。
fn default_ripple_foam_threshold() -> f32 { 0.05 }

// ─── 岸波（ショアフィールド。Phase W1.5）の既定値 ────────────
//
// 既定は「Ocean に付ければそのまま浜へ寄せる波が出る」値にしてある。
// 岸波は地形の水深から作られるため、地形の無いシーン・水深が深いだけの水域では
// これらの値でも一切現れない（＝既定が有効でも見た目が壊れることはない）。

/// shore_wave_strength の既定値（1.0 = 標準）。**0 で完全無効（W1 と同一出力）**。
fn default_shore_wave_strength() -> f32 { 1.0 }
/// shore_wave_length の既定値（うねりの波長 m）。
///
/// 浜へ寄せるうねりとして自然に見える長さ。短くすると細かいさざ波、
/// 長くすると外洋の大きなうねりになる。
fn default_shore_wave_length() -> f32 { 12.0 }
/// shore_wave_period の既定値（うねりが 1 波長進む周期 秒）。
///
/// 波長 12m / 周期 4s ＝ 位相速度 3m/s。実際の浜のうねりに近い速さ。
fn default_shore_wave_period() -> f32 { 4.0 }
/// shore_wave_foam の既定値（砕け波・打ち上げの泡量 0..1）。
fn default_shore_wave_foam() -> f32 { 0.8 }

// ─── WaterVolumeKind ─────────────────────────────────────────

/// 水ボリュームの種別。
///
/// - `Ocean`  : XZ 方向に無限の大洋。水面は `surface_height`（ワールド Y 絶対値）に張る。
///              描画クアッドはカメラに追従し、片側半径 `ocean_extent` で作られる。
/// - `Region` : 直方体（軸平行 AABB）で区切った水塊。既定。
///              水面 Y は「アクタ原点 + `surface_height`」で決まる。
/// - `Spline` : 川（スプライン水路）。**W4 で実装。現状は描画・問い合わせともに無視される。**
///
/// serde は文字列（`"Ocean"` / `"Region"` / `"Spline"`）でシリアライズする。
/// 旧シーン（kind 欠落）は `#[serde(default)]` により Region になる。
#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Debug)]
pub enum WaterVolumeKind {
    /// XZ 無限の大洋（水面 Y はワールド絶対値）
    Ocean,
    /// 直方体で区切った水塊（既定）
    Region,
    /// 川スプライン。**W4 で実装。現状は描画・問い合わせともに無視される。**
    Spline,
}

impl Default for WaterVolumeKind {
    /// kind 省略時の既定は Region（局所的な水たまり／プールが最も一般的なため）。
    fn default() -> Self { WaterVolumeKind::Region }
}

impl WaterVolumeKind {
    /// インスペクタへ送る種別文字列（C# 側ドロップダウンの Tag と一致させる）。
    pub fn as_str(self) -> &'static str {
        match self {
            WaterVolumeKind::Ocean  => "Ocean",
            WaterVolumeKind::Region => "Region",
            WaterVolumeKind::Spline => "Spline",
        }
    }

    /// IPC 文字列から種別へ変換する。未知の文字列は None（呼び出し側で無視する）。
    pub fn from_str_opt(s: &str) -> Option<Self> {
        match s {
            "Ocean"  => Some(WaterVolumeKind::Ocean),
            "Region" => Some(WaterVolumeKind::Region),
            "Spline" => Some(WaterVolumeKind::Spline),
            _        => None,
        }
    }
}

// ─── WaterVolumeComponentData（シリアライズ用）───────────────

/// WaterVolumeComponent のシリアライズ用データ（.scene 保存・Undo スナップショット）。
#[derive(Clone, Serialize, Deserialize, Debug)]
pub struct WaterVolumeComponentData {
    /// 水ボリュームの種別（既定 Region）
    #[serde(default)]
    pub kind: WaterVolumeKind,
    /// 水面高さ。**Ocean = ワールド Y 絶対値 / Region = アクタ原点からの相対 Y**。
    #[serde(default)]
    pub surface_height: f32,
    /// Region のローカル AABB 半径（アクタ位置が中心）。既定 [10, 5, 10]。
    #[serde(default = "default_region_half_extents")]
    pub region_half_extents: [f32; 3],
    /// Ocean 水面クアッドの片側半径（m）。カメラに追従して張られる。既定 2000。
    #[serde(default = "default_ocean_extent")]
    pub ocean_extent: f32,
    /// 浅場の色（リニア RGB）
    #[serde(default = "default_shallow_color")]
    pub shallow_color: [f32; 3],
    /// 深場の色（リニア RGB）
    #[serde(default = "default_deep_color")]
    pub deep_color: [f32; 3],
    /// 深色へ収束するまでの水中距離（m）。小さいほど濁って見える。
    #[serde(default = "default_absorption_distance")]
    pub absorption_distance: f32,
    /// 深場での最大不透明度（0..1）
    #[serde(default = "default_surface_opacity")]
    pub surface_opacity: f32,
    /// 岸フォームの色（リニア RGB）
    #[serde(default = "default_foam_color")]
    pub foam_color: [f32; 3],
    /// フォームが出る水深（m）。この深さより浅い所に白波が乗る。
    #[serde(default = "default_foam_width")]
    pub foam_width: f32,
    /// フォームの強度（0..1）
    #[serde(default = "default_foam_intensity")]
    pub foam_intensity: f32,
    /// 法線摂動の強さ（形状は変えず陰影のみ揺らす）
    #[serde(default = "default_wave_amplitude")]
    pub wave_amplitude: f32,
    /// 波の空間周波数（1/m）。大きいほど細かい波になる。
    #[serde(default = "default_wave_scale")]
    pub wave_scale: f32,
    /// 波のスクロール速度
    #[serde(default = "default_wave_speed")]
    pub wave_speed: f32,
    /// フレネル指数（Schlick 近似の累乗。大きいほど正面が透ける）
    #[serde(default = "default_fresnel_power")]
    pub fresnel_power: f32,
    /// フレネル反射の寄与率（0..1）
    #[serde(default = "default_fresnel_strength")]
    pub fresnel_strength: f32,
    /// 浅い角度で映る簡易反射色（リニア RGB）
    #[serde(default = "default_reflection_color")]
    pub reflection_color: [f32; 3],
    /// 屈折 UV の最大歪み（画面比）
    #[serde(default = "default_refraction_distortion")]
    pub refraction_distortion: f32,
    /// 波紋・航跡（インタラクションフィールド）の法線摂動スケール（Phase I2）
    #[serde(default = "default_ripple_strength")]
    pub ripple_strength: f32,
    /// 波紋フォームが出る波高しきい値（m 相当。Phase I2）
    #[serde(default = "default_ripple_foam_threshold")]
    pub ripple_foam_threshold: f32,
    /// 岸波（ショアフィールド）の強さ。**0 で完全無効**（Phase W1.5）
    #[serde(default = "default_shore_wave_strength")]
    pub shore_wave_strength: f32,
    /// 岸へ寄せるうねりの波長（m。Phase W1.5）
    #[serde(default = "default_shore_wave_length")]
    pub shore_wave_length: f32,
    /// 岸へ寄せるうねりの周期（秒。Phase W1.5）
    #[serde(default = "default_shore_wave_period")]
    pub shore_wave_period: f32,
    /// 砕け波・打ち上げの泡量（0..1。Phase W1.5）
    #[serde(default = "default_shore_wave_foam")]
    pub shore_wave_foam: f32,
}

impl Default for WaterVolumeComponentData {
    fn default() -> Self {
        Self {
            kind:                  WaterVolumeKind::default(),
            surface_height:        0.0,
            region_half_extents:   default_region_half_extents(),
            ocean_extent:          default_ocean_extent(),
            shallow_color:         default_shallow_color(),
            deep_color:            default_deep_color(),
            absorption_distance:   default_absorption_distance(),
            surface_opacity:       default_surface_opacity(),
            foam_color:            default_foam_color(),
            foam_width:            default_foam_width(),
            foam_intensity:        default_foam_intensity(),
            wave_amplitude:        default_wave_amplitude(),
            wave_scale:            default_wave_scale(),
            wave_speed:            default_wave_speed(),
            fresnel_power:         default_fresnel_power(),
            fresnel_strength:      default_fresnel_strength(),
            reflection_color:      default_reflection_color(),
            refraction_distortion: default_refraction_distortion(),
            ripple_strength:       default_ripple_strength(),
            ripple_foam_threshold: default_ripple_foam_threshold(),
            shore_wave_strength:   default_shore_wave_strength(),
            shore_wave_length:     default_shore_wave_length(),
            shore_wave_period:     default_shore_wave_period(),
            shore_wave_foam:       default_shore_wave_foam(),
        }
    }
}

// ─── WaterVolumeComponent（ECS 実体）─────────────────────────

/// 水ボリュームコンポーネント（ECS 実体）。
///
/// 保持するのは水の形状定義と見た目パラメータのみ。ワールド位置は Actor の
/// Transform から engine::water::collect が毎フレーム解決する。
/// 揮発状態は持たない（Data と同一構成）。
#[derive(Clone, Debug)]
pub struct WaterVolumeComponent {
    /// 水ボリュームの種別
    pub kind: WaterVolumeKind,
    /// 水面高さ（Ocean = ワールド Y 絶対値 / Region = アクタ原点からの相対 Y）
    pub surface_height: f32,
    /// Region のローカル AABB 半径（アクタ位置が中心）
    pub region_half_extents: [f32; 3],
    /// Ocean 水面クアッドの片側半径（m）
    pub ocean_extent: f32,
    /// 浅場の色（リニア RGB）
    pub shallow_color: [f32; 3],
    /// 深場の色（リニア RGB）
    pub deep_color: [f32; 3],
    /// 深色へ収束するまでの水中距離（m）
    pub absorption_distance: f32,
    /// 深場での最大不透明度（0..1）
    pub surface_opacity: f32,
    /// 岸フォームの色（リニア RGB）
    pub foam_color: [f32; 3],
    /// フォームが出る水深（m）
    pub foam_width: f32,
    /// フォームの強度（0..1）
    pub foam_intensity: f32,
    /// 法線摂動の強さ
    pub wave_amplitude: f32,
    /// 波の空間周波数（1/m）
    pub wave_scale: f32,
    /// 波のスクロール速度
    pub wave_speed: f32,
    /// フレネル指数
    pub fresnel_power: f32,
    /// フレネル反射の寄与率（0..1）
    pub fresnel_strength: f32,
    /// 浅い角度での簡易反射色（リニア RGB）
    pub reflection_color: [f32; 3],
    /// 屈折 UV の最大歪み（画面比）
    pub refraction_distortion: f32,
    /// 波紋・航跡の法線摂動スケール（Phase I2）
    pub ripple_strength: f32,
    /// 波紋フォームが出る波高しきい値（m 相当。Phase I2）
    pub ripple_foam_threshold: f32,
    /// 岸波の強さ（0 で完全無効。Phase W1.5）
    pub shore_wave_strength: f32,
    /// 岸へ寄せるうねりの波長（m。Phase W1.5）
    pub shore_wave_length: f32,
    /// 岸へ寄せるうねりの周期（秒。Phase W1.5）
    pub shore_wave_period: f32,
    /// 砕け波・打ち上げの泡量（0..1。Phase W1.5）
    pub shore_wave_foam: f32,
}

impl WaterVolumeComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    pub fn from_data(data: WaterVolumeComponentData) -> Self {
        Self {
            kind:                  data.kind,
            surface_height:        data.surface_height,
            region_half_extents:   data.region_half_extents,
            ocean_extent:          data.ocean_extent,
            shallow_color:         data.shallow_color,
            deep_color:            data.deep_color,
            absorption_distance:   data.absorption_distance,
            surface_opacity:       data.surface_opacity,
            foam_color:            data.foam_color,
            foam_width:            data.foam_width,
            foam_intensity:        data.foam_intensity,
            wave_amplitude:        data.wave_amplitude,
            wave_scale:            data.wave_scale,
            wave_speed:            data.wave_speed,
            fresnel_power:         data.fresnel_power,
            fresnel_strength:      data.fresnel_strength,
            reflection_color:      data.reflection_color,
            refraction_distortion: data.refraction_distortion,
            ripple_strength:       data.ripple_strength,
            ripple_foam_threshold: data.ripple_foam_threshold,
            shore_wave_strength:   data.shore_wave_strength,
            shore_wave_length:     data.shore_wave_length,
            shore_wave_period:     data.shore_wave_period,
            shore_wave_foam:       data.shore_wave_foam,
        }
    }

    /// シリアライズ用データへ変換する。
    pub fn to_data(&self) -> WaterVolumeComponentData {
        WaterVolumeComponentData {
            kind:                  self.kind,
            surface_height:        self.surface_height,
            region_half_extents:   self.region_half_extents,
            ocean_extent:          self.ocean_extent,
            shallow_color:         self.shallow_color,
            deep_color:            self.deep_color,
            absorption_distance:   self.absorption_distance,
            surface_opacity:       self.surface_opacity,
            foam_color:            self.foam_color,
            foam_width:            self.foam_width,
            foam_intensity:        self.foam_intensity,
            wave_amplitude:        self.wave_amplitude,
            wave_scale:            self.wave_scale,
            wave_speed:            self.wave_speed,
            fresnel_power:         self.fresnel_power,
            fresnel_strength:      self.fresnel_strength,
            reflection_color:      self.reflection_color,
            refraction_distortion: self.refraction_distortion,
            ripple_strength:       self.ripple_strength,
            ripple_foam_threshold: self.ripple_foam_threshold,
            shore_wave_strength:   self.shore_wave_strength,
            shore_wave_length:     self.shore_wave_length,
            shore_wave_period:     self.shore_wave_period,
            shore_wave_foam:       self.shore_wave_foam,
        }
    }
}

impl Default for WaterVolumeComponent {
    fn default() -> Self { Self::from_data(WaterVolumeComponentData::default()) }
}

impl Component for WaterVolumeComponent {}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// kind の文字列表現が IPC 仕様（"Ocean"/"Region"/"Spline"）と一致すること。
    #[test]
    fn kind_str_roundtrip() {
        for k in [WaterVolumeKind::Ocean, WaterVolumeKind::Region, WaterVolumeKind::Spline] {
            assert_eq!(WaterVolumeKind::from_str_opt(k.as_str()), Some(k));
        }
        // 未知文字列は None（呼び出し側で無視される）
        assert_eq!(WaterVolumeKind::from_str_opt("river"), None);
    }

    /// kind 省略時の既定は Region であること。
    #[test]
    fn kind_default_is_region() {
        assert_eq!(WaterVolumeKind::default(), WaterVolumeKind::Region);
    }

    /// 空 JSON（全フィールド欠落＝旧 .scene 相当）でもデシリアライズでき、
    /// 既定値が入ること（serde(default) の付け漏れ検出）。
    #[test]
    fn deserializes_from_empty_json_with_defaults() {
        let d: WaterVolumeComponentData = serde_json::from_str("{}").expect("空 JSON を読めること");
        let def = WaterVolumeComponentData::default();
        assert_eq!(d.kind, def.kind);
        assert_eq!(d.surface_height, def.surface_height);
        assert_eq!(d.region_half_extents, def.region_half_extents);
        assert_eq!(d.ocean_extent, def.ocean_extent);
        assert_eq!(d.shallow_color, def.shallow_color);
        assert_eq!(d.deep_color, def.deep_color);
        assert_eq!(d.absorption_distance, def.absorption_distance);
        assert_eq!(d.surface_opacity, def.surface_opacity);
        assert_eq!(d.foam_color, def.foam_color);
        assert_eq!(d.foam_width, def.foam_width);
        assert_eq!(d.foam_intensity, def.foam_intensity);
        assert_eq!(d.wave_amplitude, def.wave_amplitude);
        assert_eq!(d.wave_scale, def.wave_scale);
        assert_eq!(d.wave_speed, def.wave_speed);
        assert_eq!(d.fresnel_power, def.fresnel_power);
        assert_eq!(d.fresnel_strength, def.fresnel_strength);
        assert_eq!(d.reflection_color, def.reflection_color);
        assert_eq!(d.refraction_distortion, def.refraction_distortion);
        assert_eq!(d.ripple_strength, def.ripple_strength);
        assert_eq!(d.ripple_foam_threshold, def.ripple_foam_threshold);
        assert_eq!(d.shore_wave_strength, def.shore_wave_strength);
        assert_eq!(d.shore_wave_length, def.shore_wave_length);
        assert_eq!(d.shore_wave_period, def.shore_wave_period);
        assert_eq!(d.shore_wave_foam, def.shore_wave_foam);
    }

    /// kind は文字列としてシリアライズされること（C# 側の期待に合わせる）。
    #[test]
    fn kind_serializes_as_string() {
        let json = serde_json::to_string(&WaterVolumeKind::Ocean).unwrap();
        assert_eq!(json, "\"Ocean\"");
    }

    /// from_data / to_data が全フィールドを往復すること（写し漏れ検出）。
    #[test]
    fn data_roundtrip_preserves_all_fields() {
        let src = WaterVolumeComponentData {
            kind: WaterVolumeKind::Ocean,
            surface_height: 12.5,
            region_half_extents: [1.0, 2.0, 3.0],
            ocean_extent: 111.0,
            shallow_color: [0.1, 0.2, 0.3],
            deep_color: [0.4, 0.5, 0.6],
            absorption_distance: 4.5,
            surface_opacity: 0.5,
            foam_color: [0.7, 0.8, 0.9],
            foam_width: 1.25,
            foam_intensity: 0.25,
            wave_amplitude: 0.75,
            wave_scale: 0.33,
            wave_speed: 2.5,
            fresnel_power: 3.5,
            fresnel_strength: 0.6,
            reflection_color: [0.11, 0.22, 0.33],
            refraction_distortion: 0.07,
            ripple_strength: 1.5,
            ripple_foam_threshold: 0.2,
            shore_wave_strength: 0.75,
            shore_wave_length: 9.5,
            shore_wave_period: 3.25,
            shore_wave_foam: 0.4,
        };
        let back = WaterVolumeComponent::from_data(src.clone()).to_data();
        assert_eq!(back.kind, src.kind);
        assert_eq!(back.surface_height, src.surface_height);
        assert_eq!(back.region_half_extents, src.region_half_extents);
        assert_eq!(back.ocean_extent, src.ocean_extent);
        assert_eq!(back.shallow_color, src.shallow_color);
        assert_eq!(back.deep_color, src.deep_color);
        assert_eq!(back.absorption_distance, src.absorption_distance);
        assert_eq!(back.surface_opacity, src.surface_opacity);
        assert_eq!(back.foam_color, src.foam_color);
        assert_eq!(back.foam_width, src.foam_width);
        assert_eq!(back.foam_intensity, src.foam_intensity);
        assert_eq!(back.wave_amplitude, src.wave_amplitude);
        assert_eq!(back.wave_scale, src.wave_scale);
        assert_eq!(back.wave_speed, src.wave_speed);
        assert_eq!(back.fresnel_power, src.fresnel_power);
        assert_eq!(back.fresnel_strength, src.fresnel_strength);
        assert_eq!(back.reflection_color, src.reflection_color);
        assert_eq!(back.refraction_distortion, src.refraction_distortion);
        assert_eq!(back.ripple_strength, src.ripple_strength);
        assert_eq!(back.ripple_foam_threshold, src.ripple_foam_threshold);
        assert_eq!(back.shore_wave_strength, src.shore_wave_strength);
        assert_eq!(back.shore_wave_length, src.shore_wave_length);
        assert_eq!(back.shore_wave_period, src.shore_wave_period);
        assert_eq!(back.shore_wave_foam, src.shore_wave_foam);
    }
}
