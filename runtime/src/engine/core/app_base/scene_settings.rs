//! シーン単位のビューポート／レンダリング設定（`.scene` の `settings` 節）。
//!
//! 従来これらの設定は `assets/project_settings.json`（プロジェクト全体で 1 つ）に
//! 保存されていたが、シーンごとに見た目・カメラ設定を切り替えられるよう
//! `.scene` ファイル側へ移行した。
//!
//! - 書き手: エディタ（C# 側）。`settings` 節を読み書きし、変更時に IPC
//!   `SET_SCENE_SETTINGS:{json}` でランタイムへ通知する。
//! - 読み手: ランタイム。シーンロード時に [`crate::engine::core::app_base::App::apply_scene_settings`]
//!   が App の各フィールドへ反映する（スタンドアロン Play の見た目互換がこの型の存在理由）。
//!
//! ## serde 互換
//! 旧 `.scene`（`settings` 節が無い／一部キーしか無い）を壊さないため、
//! **全フィールドに `#[serde(default)]`（非ゼロ既定値は `#[serde(default = "fn")]`）を付ける**。
//! 既定値はマジックナンバーを書かず、既存の描画系デフォルト（`PostFxSettings::default()` /
//! `DebugCameraData::default()` / `DEFAULT_AMBIENT_*`）から引く。

use serde::{Deserialize, Serialize};

use crate::engine::core::app_base::scene::DebugCameraData;
use crate::engine::core::renderer::{
    PostFxSettings, RenderFeatures, TransparencyMode,
    DEFAULT_AMBIENT_COLOR, DEFAULT_AMBIENT_INTENSITY,
};

// ============================================================
//  既定値プロバイダ（serde default 用）
// ============================================================
//  マジックナンバー禁止のため、既定値はすべて既存のデフォルト実装／定数から引く。

/// デバッグカメラ FOV（度）の既定値。デバッグカメラ保存データの既定と一致させる。
fn default_camera_fov() -> f32 { DebugCameraData::default().fov_deg }
/// デバッグカメラ far クリップの既定値。
fn default_camera_far() -> f32 { DebugCameraData::default().far }
/// デバッグカメラ移動速度の既定値。
fn default_camera_speed() -> f32 { DebugCameraData::default().speed }
/// ブルーム強度の既定値（`PostFxSettings` の既定と一致）。
fn default_bloom_intensity() -> f32 { PostFxSettings::default().bloom_intensity }
/// 透明描画方式の既定値（文字列表現。既定は距離ソート = "sort"）。
fn default_transparency() -> String { TransparencyMode::default().as_str().to_string() }
/// Deferred（G-Buffer）レンダリングの既定値。
fn default_deferred() -> bool { PostFxSettings::default().deferred }
/// GI 強度の既定値。
fn default_gi_intensity() -> f32 { PostFxSettings::default().gi.intensity }
/// 反射強度の既定値。
fn default_reflection_intensity() -> f32 { PostFxSettings::default().reflection_intensity }
/// AO 強度の既定値。
fn default_ao_intensity() -> f32 { PostFxSettings::default().ao_intensity }
/// 環境光カラーの既定値（白）。
fn default_ambient_color() -> [f32; 3] { DEFAULT_AMBIENT_COLOR }
/// 環境光強度の既定値。
fn default_ambient_intensity() -> f32 { DEFAULT_AMBIENT_INTENSITY }

// ============================================================
//  DebugCameraSettings — シーンビューのデバッグカメラ設定
// ============================================================

/// シーンビュー（Edit モードのデバッグカメラ）に関する設定。
///
/// カメラの「位置・向き」は従来どおり `.scene` の `debug_camera` 節が持つ。
/// こちらは画角・描画距離・移動速度・投影方式といった**表示設定**のみを扱う。
///
/// なお **グリッド表示 / 軸ギズモ表示（`SHOW_GRID` / `SHOW_AXIS_GIZMO`）は
/// 本スキーマに含めない**。`view_mode` と同じくセッション限りの非永続設定として、
/// エディタのシーンパネル上部トグルが IPC で直接切り替える（起動時は常に表示）。
#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct DebugCameraSettings {
    /// 垂直画角（度）。
    #[serde(default = "default_camera_fov")]
    pub fov: f32,
    /// far クリップ距離。
    #[serde(default = "default_camera_far")]
    pub far: f32,
    /// カメラ移動速度。
    #[serde(default = "default_camera_speed")]
    pub speed: f32,
    /// 2D（正射投影）モードかどうか。true で正射投影。
    #[serde(default)]
    pub ortho_2d: bool,
}

impl Default for DebugCameraSettings {
    fn default() -> Self {
        Self {
            fov:             default_camera_fov(),
            far:             default_camera_far(),
            speed:           default_camera_speed(),
            ortho_2d:        false,
        }
    }
}

// ============================================================
//  RenderingSettings — シーンのレンダリング設定
// ============================================================

/// シーンのレンダリング設定（ポストエフェクト・機能マトリクス・環境光）。
///
/// キー名は移行元である `project_settings.json` の既存キーと**意図的に同一**にしてある
/// （エディタ側の読み書きコードと 1:1 対応させるため。キー名を変更してはならない）。
///
/// なお **`view_mode`（シーンビュー表示モード: Lit / Unlit / G-Buffer デバッグ等）は
/// 本スキーマに含めない**。セッション限りの非永続設定として維持する
/// （デバッグ表示のままシーンに保存されると事故になるため）。
#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct RenderingSettings {
    /// ブルーム有効フラグ。
    #[serde(default)]
    pub bloom: bool,
    /// ブルーム合成強度。
    #[serde(default = "default_bloom_intensity")]
    pub bloom_intensity: f32,
    /// FXAA 有効フラグ。
    #[serde(default)]
    pub fxaa: bool,
    /// 透明描画方式（"sort" = 距離ソート / "wboit" = Weighted Blended OIT）。
    /// 変換は `TransparencyMode::from_str`（未知の文字列は距離ソートへフォールバック）。
    #[serde(default = "default_transparency")]
    pub transparency: String,
    /// Deferred（G-Buffer）レンダリング有効フラグ。false でフォワード経路へフォールバック。
    #[serde(default = "default_deferred")]
    pub deferred: bool,
    /// RT 屈折の逐次グラブ（ガラス越しガラスの多重屈折）。重量オプション。
    #[serde(default)]
    pub refract_sequential_grab: bool,
    /// GI（間接光）の強度倍率。
    #[serde(default = "default_gi_intensity")]
    pub gi_intensity: f32,
    /// 反射（SSR / RT）の強度倍率。
    #[serde(default = "default_reflection_intensity")]
    pub reflection_intensity: f32,
    /// AO（SSAO / RT-AO）の強度倍率。
    #[serde(default = "default_ao_intensity")]
    pub ao_intensity: f32,
    /// 描画機能マトリクス（影 / GI / 反射 / AO / 半透明の各方式）。
    /// `RenderFeatures` 自身が全フィールド `#[serde(default)]` 済みで部分指定を許す。
    #[serde(default)]
    pub features: RenderFeatures,
    /// 環境光カラー（[r, g, b]）。
    #[serde(default = "default_ambient_color")]
    pub ambient_color: [f32; 3],
    /// 環境光強度（0 で完全な暗闇）。
    #[serde(default = "default_ambient_intensity")]
    pub ambient_intensity: f32,
}

impl Default for RenderingSettings {
    fn default() -> Self {
        Self {
            bloom:                   false,
            bloom_intensity:         default_bloom_intensity(),
            fxaa:                    false,
            transparency:            default_transparency(),
            deferred:                default_deferred(),
            refract_sequential_grab: false,
            gi_intensity:            default_gi_intensity(),
            reflection_intensity:    default_reflection_intensity(),
            ao_intensity:            default_ao_intensity(),
            features:                RenderFeatures::default(),
            ambient_color:           default_ambient_color(),
            ambient_intensity:       default_ambient_intensity(),
        }
    }
}

// ============================================================
//  PhysicsSettings — 編集時物理（エディタ専用）設定
// ============================================================

/// 編集時物理（Edit モードで物理シミュレーションを走らせる機能）の設定。
///
/// **エディタ専用**。ランタイムは `.scene` への保存／復元と IPC 経由の受け渡しだけを行い、
/// この節を自身へ適用することはない（Play モードに編集時物理は無関係であり、
/// 適用すると物理スレッドの起動・停止シーケンスを壊すため）。
#[derive(Serialize, Deserialize, Clone, Debug, Default)]
pub struct PhysicsSettings {
    /// 編集時物理を有効にするか。
    #[serde(default)]
    pub edit_physics: bool,
    /// 編集時物理で RigidBody を有効にするか（false は常時押し戻しモード）。
    #[serde(default)]
    pub edit_physics_rigidbody: bool,
}

// ============================================================
//  SceneSettingsData — `.scene` の settings 節ルート
// ============================================================

/// `.scene` の `settings` 節のルート型。
///
/// 各サブ節も `#[serde(default)]` 付きのため、節ごと欠落していても
/// その節の既定値で読み込める（旧 `.scene` の後方互換）。
#[derive(Serialize, Deserialize, Clone, Debug, Default)]
pub struct SceneSettingsData {
    /// シーンビューのデバッグカメラ設定。
    #[serde(default)]
    pub debug_camera: DebugCameraSettings,
    /// レンダリング設定。
    #[serde(default)]
    pub rendering: RenderingSettings,
    /// 編集時物理設定（エディタ専用。ランタイムは保存／復元のみ行う）。
    #[serde(default)]
    pub physics: PhysicsSettings,
}

// ============================================================
//  テスト — エディタ（C# 側）との JSON スキーマ契約を固定する
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::renderer::{
        AoMode, GiMode, ReflectionMode, ShadowMode, TranslucencyMode,
    };

    /// エディタが書き出す想定の完全な JSON を、キー名・値表現ごとそのまま読めること。
    /// この JSON はエディタ側実装との契約であり、キー名を変更してはならない。
    #[test]
    fn parses_full_editor_schema() {
        let json = r#"{
            "debug_camera": {
                "fov": 60.0, "far": 500.0, "speed": 12.0, "ortho_2d": true
            },
            "rendering": {
                "bloom": true, "bloom_intensity": 0.25, "fxaa": true,
                "transparency": "wboit", "deferred": false, "refract_sequential_grab": true,
                "gi_intensity": 2.0, "reflection_intensity": 3.0, "ao_intensity": 4.0,
                "features": { "shadow": "shadowmap", "gi": "rt", "reflection": "off",
                              "ao": "off", "translucency": "raster" },
                "ambient_color": [0.1, 0.2, 0.3], "ambient_intensity": 0.5
            },
            "physics": { "edit_physics": true, "edit_physics_rigidbody": true }
        }"#;
        let s: SceneSettingsData = serde_json::from_str(json).expect("解析に失敗");
        assert_eq!(s.debug_camera.fov, 60.0);
        assert!(s.debug_camera.ortho_2d);
        assert_eq!(s.rendering.transparency, "wboit");
        assert!(!s.rendering.deferred);
        assert_eq!(s.rendering.ambient_color, [0.1, 0.2, 0.3]);
        assert_eq!(s.rendering.features.shadow, ShadowMode::ShadowMap);
        assert_eq!(s.rendering.features.gi, GiMode::Rt);
        assert_eq!(s.rendering.features.reflection, ReflectionMode::Off);
        assert_eq!(s.rendering.features.ao, AoMode::Off);
        assert_eq!(s.rendering.features.translucency, TranslucencyMode::Raster);
        assert!(s.physics.edit_physics_rigidbody);
    }

    /// 空オブジェクト・部分指定でも読めること（旧 .scene / 部分保存の後方互換）。
    /// 指定しなかったキーは既定値（PostFxSettings 等のデフォルト）になる。
    #[test]
    fn missing_keys_fall_back_to_defaults() {
        let s: SceneSettingsData = serde_json::from_str("{}").expect("空オブジェクトの解析に失敗");
        let d = SceneSettingsData::default();
        assert_eq!(s.debug_camera.fov, d.debug_camera.fov);
        assert_eq!(s.rendering.transparency, d.rendering.transparency);
        assert_eq!(s.rendering.deferred, d.rendering.deferred);
        assert_eq!(s.rendering.ambient_intensity, DEFAULT_AMBIENT_INTENSITY);

        // 部分指定: 指定したキーだけ上書きされ、他は既定のまま
        let partial: SceneSettingsData =
            serde_json::from_str(r#"{"rendering":{"bloom":true}}"#).expect("部分指定の解析に失敗");
        assert!(partial.rendering.bloom);
        assert_eq!(partial.rendering.bloom_intensity, d.rendering.bloom_intensity);
        assert_eq!(partial.debug_camera.far, d.debug_camera.far);
    }
}
