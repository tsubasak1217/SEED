// ============================================================
//  skybox_component.rs — スカイボックス（天球）コンポーネント
//
//  Actor に「天球（背景の全周画像）」を持たせる ECS スロットコンポーネント。
//  本コンポーネントは天球のパラメータ（テクスチャ・配置モード・強度・色味）の
//  データのみを保持する（ECS 理念：データとロジックの分離）。実際の描画は
//  レンダラ側の renderer/skybox.rs（SkyboxSystem）＋ skybox_ops.rs が毎フレーム行う。
//
//  【テクスチャ形式】
//  v1 は equirectangular（正距円筒）画像 1 枚を推奨実装とする（assets:// 仮想パス）。
//  シェーダが「サンプル方向ベクトル → 緯度経度 UV」へ変換して球面へ貼る。
//  ※ キューブマップ 6 枚対応は将来 TODO（テクスチャ形式の分岐が必要）。
//
//  【配置モード（SkyboxMode）】
//   - CameraLocked（既定）: 常にカメラ位置を中心とする無限遠の天球。ビュー行列の
//     平行移動を除去し、深度は far 付近（depth write off・LessEqual）で描く。標準スカイボックス。
//   - WorldAnchored: アクターの Transform（位置・回転・スケール）で配置される内向き球として
//     ワールドに実在する（深度あり・接近／内外移動可能）。
//
//  【シーンに複数ある場合】
//   - CameraLocked は最初の 1 つだけが有効（2 つ目以降は警告して無視。skybox_ops で判定）。
//   - WorldAnchored は複数配置できる（それぞれワールド上の実体のため）。
//
//  【シリアライズ】
//  全フィールドに #[serde(default)]（非ゼロ既定は default fn）を付け、旧 .scene
//  （フィールド欠落）でも読み込みが失敗しないようにする。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

// ─── デフォルト値関数 ─────────────────────────────────────────
// マジックナンバー禁止のため、非ゼロ既定値はすべて関数に切り出す。

/// intensity（テクスチャ色への乗算強度）の既定値。1.0＝素の色。
/// HDR メインパスへ描くため 1.0 超で発光的になり Bloom と連動する。
fn default_intensity() -> f32 {
    1.0
}
/// tint（色味乗算）の既定値（白＝素通し）。
fn default_tint() -> [f32; 3] {
    [1.0, 1.0, 1.0]
}

// ─── 色調整（色相／彩度／明度／コントラスト）の値域と既定値 ─────
//
// これらは **UI のスライダー値域・ランタイムの clamp・シェーダの解釈**の
// 3 者が参照する唯一の正典である（マジックナンバーを 3 か所に散らさない）。
// エディタ側の値域表（editor/src/Controls/ComponentFieldRanges.cs）は
// この定数と同じ値を持つこと（根拠コメントで相互参照している）。

/// 色相シフトの下限（度）。-180°と +180° は色相環上で同じ位置を指す。
pub const SKY_HUE_SHIFT_MIN_DEG: f32 = -180.0;
/// 色相シフトの上限（度）。
pub const SKY_HUE_SHIFT_MAX_DEG: f32 = 180.0;
/// 彩度／明度／コントラストの下限（0 = 完全に効かせた側）。
pub const SKY_ADJUST_MIN: f32 = 0.0;
/// 彩度／明度／コントラストの上限（2 = 2 倍まで強調）。
pub const SKY_ADJUST_MAX: f32 = 2.0;

/// 色相シフト（度）の既定値。0＝無変換。
fn default_hue_shift() -> f32 {
    0.0
}
/// 彩度の既定値。1＝無変換。
fn default_saturation() -> f32 {
    1.0
}
/// 明度（乗算）の既定値。1＝無変換。
fn default_brightness() -> f32 {
    1.0
}
/// コントラストの既定値。1＝無変換。
fn default_contrast() -> f32 {
    1.0
}

// ─── SkyboxMode ──────────────────────────────────────────────

/// スカイボックスの配置モード。
///
/// - `CameraLocked` : カメラ位置中心・無限遠の標準スカイボックス（既定）。
/// - `WorldAnchored`: アクターの Transform で配置される内向き球（ワールドに実在）。
///
/// serde は snake_case でシリアライズする（`"camera_locked"` / `"world_anchored"`）。
/// 旧シーン（mode 欠落）は `#[serde(default)]` により CameraLocked になる。
#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Debug)]
#[serde(rename_all = "snake_case")]
pub enum SkyboxMode {
    /// カメラ位置中心・無限遠（既定）
    CameraLocked,
    /// アクター Transform で配置される内向き球
    WorldAnchored,
}

impl Default for SkyboxMode {
    /// mode 省略時の既定は CameraLocked（標準スカイボックス）。
    fn default() -> Self {
        SkyboxMode::CameraLocked
    }
}

impl SkyboxMode {
    /// GPU（シェーダ）へ渡すモードコード。skybox.wgsl の `SKYBOX_MODE_*` と一致させること。
    pub fn to_code(self) -> u32 {
        match self {
            SkyboxMode::CameraLocked => 0,
            SkyboxMode::WorldAnchored => 1,
        }
    }

    /// IPC 文字列（インスペクタのドロップダウン Tag）→ enum。
    pub fn from_str_opt(s: &str) -> Option<Self> {
        match s {
            "camera_locked" => Some(SkyboxMode::CameraLocked),
            "world_anchored" => Some(SkyboxMode::WorldAnchored),
            _ => None,
        }
    }

    /// インスペクタ／シリアライズへ送るモード文字列。
    pub fn as_str(self) -> &'static str {
        match self {
            SkyboxMode::CameraLocked => "camera_locked",
            SkyboxMode::WorldAnchored => "world_anchored",
        }
    }
}

// ─── SkyboxComponentData（シリアライズ用）─────────────────────

/// SkyboxComponent のシリアライズ用データ（.scene 保存・Undo スナップショット）。
#[derive(Clone, Serialize, Deserialize, Debug)]
pub struct SkyboxComponentData {
    /// equirectangular 画像への参照（assets:// 仮想パス。空は「未設定＝描画しない」）。
    #[serde(default)]
    pub texture_path: String,
    /// 配置モード（既定 CameraLocked）。
    #[serde(default)]
    pub mode: SkyboxMode,
    /// 強度（テクスチャ色への乗算。既定 1.0。>1 で発光的＝Bloom 連動）。
    #[serde(default = "default_intensity")]
    pub intensity: f32,
    /// 色味（リニア RGB 乗算。既定 白）。
    #[serde(default = "default_tint")]
    pub tint: [f32; 3],

    // ── 色調整（全ての空サンプル経路へ一貫して効く。既定値で従来と同一出力）──
    /// 色相シフト（度。-180〜180。既定 0＝無変換）。
    #[serde(default = "default_hue_shift")]
    pub hue_shift: f32,
    /// 彩度（0〜2。0＝グレースケール / 1＝無変換 / 2＝彩度 2 倍）。
    #[serde(default = "default_saturation")]
    pub saturation: f32,
    /// 明度（0〜2。色への乗算。1＝無変換）。
    #[serde(default = "default_brightness")]
    pub brightness: f32,
    /// コントラスト（0〜2。中間グレー基準の線形補間。1＝無変換）。
    #[serde(default = "default_contrast")]
    pub contrast: f32,
}

impl Default for SkyboxComponentData {
    fn default() -> Self {
        Self {
            texture_path: String::new(),
            mode: SkyboxMode::default(),
            intensity: default_intensity(),
            tint: default_tint(),
            hue_shift: default_hue_shift(),
            saturation: default_saturation(),
            brightness: default_brightness(),
            contrast: default_contrast(),
        }
    }
}

// ─── SkyboxComponent（ECS 実体）──────────────────────────────

/// スカイボックスコンポーネント（ECS 実体）。
///
/// 保持するのは天球のパラメータのみ。位置・向き・スケール（WorldAnchored 用）は
/// Actor の Transform からレンダラが毎フレーム解決する。揮発状態は持たない。
#[derive(Clone, Debug)]
pub struct SkyboxComponent {
    pub texture_path: String,
    pub mode: SkyboxMode,
    pub intensity: f32,
    pub tint: [f32; 3],
    /// 色相シフト（度）。
    pub hue_shift: f32,
    /// 彩度。
    pub saturation: f32,
    /// 明度（乗算）。
    pub brightness: f32,
    /// コントラスト（中間グレー基準）。
    pub contrast: f32,
}

impl SkyboxComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    pub fn from_data(data: SkyboxComponentData) -> Self {
        Self {
            texture_path: data.texture_path,
            mode: data.mode,
            intensity: data.intensity,
            tint: data.tint,
            hue_shift: data.hue_shift,
            saturation: data.saturation,
            brightness: data.brightness,
            contrast: data.contrast,
        }
    }

    /// シリアライズ用データへ変換する。
    pub fn to_data(&self) -> SkyboxComponentData {
        SkyboxComponentData {
            texture_path: self.texture_path.clone(),
            mode: self.mode,
            intensity: self.intensity,
            tint: self.tint,
            hue_shift: self.hue_shift,
            saturation: self.saturation,
            brightness: self.brightness,
            contrast: self.contrast,
        }
    }

    /// GPU（`SkyboxUniform.adjust` / `ReflectionSkyUniform.adjust`）へ渡す
    /// 色調整パラメータ 4 要素を作る。
    ///
    /// 並びは WGSL `sky_apply_color_adjust()` の引数規約
    /// （x=色相シフト[度] / y=彩度 / z=明度 / w=コントラスト）と厳密に一致させること。
    /// 描画・反射の両経路がこの 1 本を通ることで、値の並び替えミスが構造的に起きない。
    pub fn color_adjust(&self) -> [f32; 4] {
        [self.hue_shift, self.saturation, self.brightness, self.contrast]
    }
}

impl Default for SkyboxComponent {
    fn default() -> Self {
        Self::from_data(SkyboxComponentData::default())
    }
}

impl Component for SkyboxComponent {}

// ============================================================
//  テスト（シリアライズ互換と既定値の契約）
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 色調整の既定値が「無変換」であること。
    /// これが崩れると、既存シーンを開いただけで空の色が変わる。
    #[test]
    fn color_adjust_defaults_are_identity() {
        let d = SkyboxComponentData::default();
        assert_eq!(d.hue_shift, 0.0, "色相シフトの既定は 0°");
        assert_eq!(d.saturation, 1.0, "彩度の既定は 1");
        assert_eq!(d.brightness, 1.0, "明度の既定は 1");
        assert_eq!(d.contrast, 1.0, "コントラストの既定は 1");
        // GPU へ渡す並び（x=色相 / y=彩度 / z=明度 / w=コントラスト）が
        // レンダラ側の恒等値と一致すること。
        assert_eq!(
            SkyboxComponent::default().color_adjust(),
            crate::engine::core::renderer::sky_color_adjust::SKY_COLOR_ADJUST_IDENTITY,
            "既定の色調整がレンダラ側の恒等値と一致しない"
        );
    }

    /// **旧 .scene 互換**: 色調整フィールドが無い JSON を読んでも失敗せず、既定値（無変換）になる。
    #[test]
    fn legacy_scene_without_color_adjust_loads_with_identity() {
        let legacy = r#"{
            "texture_path": "assets://sky/day.hdr",
            "mode": "world_anchored",
            "intensity": 2.5,
            "tint": [0.9, 0.8, 0.7]
        }"#;
        let d: SkyboxComponentData =
            serde_json::from_str(legacy).expect("色調整フィールドが無い旧データも読めること");
        assert_eq!(d.texture_path, "assets://sky/day.hdr");
        assert_eq!(d.mode, SkyboxMode::WorldAnchored);
        assert_eq!(d.intensity, 2.5);
        assert_eq!(d.tint, [0.9, 0.8, 0.7]);
        assert_eq!(SkyboxComponent::from_data(d).color_adjust(), [0.0, 1.0, 1.0, 1.0]);
    }

    /// serde 往復（保存 → 読み込み）で色調整が保存されること。
    #[test]
    fn color_adjust_round_trips_through_serde() {
        let mut c = SkyboxComponent::default();
        c.hue_shift = -37.5;
        c.saturation = 1.75;
        c.brightness = 0.25;
        c.contrast = 1.5;
        let json = serde_json::to_string(&c.to_data()).expect("シリアライズ成功");
        let back: SkyboxComponentData = serde_json::from_str(&json).expect("デシリアライズ成功");
        let back = SkyboxComponent::from_data(back);
        assert_eq!(back.color_adjust(), [-37.5, 1.75, 0.25, 1.5]);
    }

    /// 値域定数が「既定値を含み、UI のスライダー端として妥当」であること。
    #[test]
    fn adjust_ranges_contain_their_defaults() {
        assert!(SKY_HUE_SHIFT_MIN_DEG < 0.0 && SKY_HUE_SHIFT_MAX_DEG > 0.0);
        assert_eq!(SKY_HUE_SHIFT_MIN_DEG, -SKY_HUE_SHIFT_MAX_DEG, "色相は 0 対称であること");
        assert!(SKY_ADJUST_MIN <= 1.0 && SKY_ADJUST_MAX >= 1.0, "既定値 1 を含むこと");
    }
}
