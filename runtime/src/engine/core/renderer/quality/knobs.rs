// ============================================================
//  quality/knobs.rs — 描画品質のつまみ（QualityKnobs）と、その読み取り・値域
//
//  【役割】
//  描画品質プリセット（runtime/config/render_presets.json）・プロジェクト設定の上書き
//  （project_settings.json の render_quality.<プラットフォーム>）・起動オプションの上書きが共通に使う
//  「つまみの集合」を定義し、JSON の値（キーと値の組）からつまみへ読み取る。
//
//  【つまみの意味（すべて省略できる。省略＝プロジェクト・シーンの設定のまま＝下げない）】
//    render_scale       … ゲーム画面の描画解像度の比率（0.5〜1.0。UI は画面の解像度のまま）
//    shadows            … false で影（シャドウマップ）を描かない
//    shadow             … 影の方式の上限（"shadowmap" / "rt"）
//    shadow_resolution  … シャドウマップ解像度の上限（1024 / 2048 / 4096）
//    shadow_distance    … 影を描く距離の上限 [m]
//    shadow_pcf_taps    … 影の輪郭をぼかすサンプル数の上限（1〜16）
//    gi / ao / reflection / translucency … 各機能の方式の上限（render_features.rs の FeatureCaps）
//    deferred / bloom / fxaa / vignette / water_reflection / water_caustics
//                       … false でその処理を止める（true は「要求どおり」で、止まっているものを動かしはしない）
//    target_fps         … 目標フレームレートの上限（1 以上）
//  「上限」は重い要求だけを下げる（軽い要求を上げない）。上げたい場合はプロジェクト・シーンの設定で上げる。
//
//  【読み方を寛容にしている理由】
//  つまみはプロジェクト設定（手で書き換えることもある）と起動オプション（検証用に手で打つ）から来る。
//  1 つの値が読めないだけで全体を捨てると「何も効かない」原因が分かりにくいので、読めない値は
//  そのつまみだけを捨てて理由を警告に残す（起動ログ `[SEED QUALITY]` に出る）。
// ============================================================

use serde_json::Value;

use crate::engine::core::renderer::render_features::{
    AoMode, FeatureCaps, GiMode, ReflectionMode, ShadowMode, TranslucencyMode,
};
use crate::engine::core::renderer::shadow_settings::{MAX_PCF_TAPS, SHADOW_RESOLUTION_CHOICES};

// ─── キーの名前（エディタの RenderQualitySettings・docs と一致させる）──────────

/// 描画スケールのキー。
pub const KEY_RENDER_SCALE: &str = "render_scale";
/// 影の有無のキー。
pub const KEY_SHADOWS: &str = "shadows";
/// 影の方式の上限のキー。
pub const KEY_SHADOW: &str = "shadow";
/// シャドウマップ解像度の上限のキー。
pub const KEY_SHADOW_RESOLUTION: &str = "shadow_resolution";
/// 影の距離の上限のキー。
pub const KEY_SHADOW_DISTANCE: &str = "shadow_distance";
/// PCF タップ数の上限のキー。
pub const KEY_SHADOW_PCF_TAPS: &str = "shadow_pcf_taps";
/// GI の方式の上限のキー。
pub const KEY_GI: &str = "gi";
/// AO の方式の上限のキー。
pub const KEY_AO: &str = "ao";
/// 反射の方式の上限のキー。
pub const KEY_REFLECTION: &str = "reflection";
/// 半透明の方式の上限のキー。
pub const KEY_TRANSLUCENCY: &str = "translucency";
/// デファード（G-Buffer）の可否のキー。
pub const KEY_DEFERRED: &str = "deferred";
/// ブルームの可否のキー。
pub const KEY_BLOOM: &str = "bloom";
/// FXAA の可否のキー。
pub const KEY_FXAA: &str = "fxaa";
/// ビネットの可否のキー。
pub const KEY_VIGNETTE: &str = "vignette";
/// 水面反射の可否のキー。
pub const KEY_WATER_REFLECTION: &str = "water_reflection";
/// 水中コースティクスの可否のキー。
pub const KEY_WATER_CAUSTICS: &str = "water_caustics";
/// 目標フレームレートの上限のキー。
pub const KEY_TARGET_FPS: &str = "target_fps";

/// 知っているつまみのキーの一覧（表の順。ログ・テスト・ドキュメントの突き合わせに使う）。
pub const KNOWN_KEYS: [&str; 17] = [
    KEY_RENDER_SCALE,
    KEY_SHADOWS,
    KEY_SHADOW,
    KEY_SHADOW_RESOLUTION,
    KEY_SHADOW_DISTANCE,
    KEY_SHADOW_PCF_TAPS,
    KEY_GI,
    KEY_AO,
    KEY_REFLECTION,
    KEY_TRANSLUCENCY,
    KEY_DEFERRED,
    KEY_BLOOM,
    KEY_FXAA,
    KEY_VIGNETTE,
    KEY_WATER_REFLECTION,
    KEY_WATER_CAUSTICS,
    KEY_TARGET_FPS,
];

// ─── 値域 ───────────────────────────────────────────────────

/// 描画スケールの下限（画面の半分の解像度。これより下げると文字・細い線が読めなくなる）。
pub const MIN_RENDER_SCALE: f32 = 0.5;
/// 描画スケールの上限（等倍。画面より高い解像度で描く超解像は扱わない）。
pub const MAX_RENDER_SCALE: f32 = 1.0;
/// 影の距離の上限として受け付ける最小値 [m]（shadow_settings の SHADOW_DISTANCE_MIN と同じ考え方）。
const MIN_SHADOW_DISTANCE: f32 = 1.0;
/// PCF タップ数の上限として受け付ける最小値。
const MIN_PCF_TAPS: u32 = 1;
/// 目標フレームレートの上限として受け付ける最小値（0 は「無制限」なので上限にならない）。
const MIN_TARGET_FPS_CAP: u32 = 1;

// ============================================================
//  QualityKnobs
// ============================================================

/// 描画品質のつまみの集合。`None` のつまみは「プロジェクト・シーンの設定のまま」。
///
/// 値は読み取り時に値域へ収めてある（`apply_knob`）ので、使う側は検査しなくてよい。
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct QualityKnobs {
    /// ゲーム画面の描画スケール（MIN_RENDER_SCALE〜MAX_RENDER_SCALE）。
    pub render_scale: Option<f32>,
    /// 影（シャドウマップ）を描くか（false で描かない）。
    pub shadows: Option<bool>,
    /// 影の方式の上限。
    pub shadow: Option<ShadowMode>,
    /// シャドウマップ解像度の上限（SHADOW_RESOLUTION_CHOICES のいずれか）。
    pub shadow_resolution: Option<u32>,
    /// 影を描く距離の上限 [m]。
    pub shadow_distance: Option<f32>,
    /// PCF タップ数の上限。
    pub shadow_pcf_taps: Option<u32>,
    /// GI の方式の上限。
    pub gi: Option<GiMode>,
    /// AO の方式の上限。
    pub ao: Option<AoMode>,
    /// 反射の方式の上限。
    pub reflection: Option<ReflectionMode>,
    /// 半透明の方式の上限。
    pub translucency: Option<TranslucencyMode>,
    /// デファード（G-Buffer）を使ってよいか（false で前方描画）。
    pub deferred: Option<bool>,
    /// ブルームを使ってよいか。
    pub bloom: Option<bool>,
    /// FXAA を使ってよいか。
    pub fxaa: Option<bool>,
    /// ビネットを使ってよいか。
    pub vignette: Option<bool>,
    /// 水面反射を使ってよいか。
    pub water_reflection: Option<bool>,
    /// 水中コースティクスを使ってよいか。
    pub water_caustics: Option<bool>,
    /// 目標フレームレートの上限。
    pub target_fps: Option<u32>,
}

impl QualityKnobs {
    /// 何も指定しないつまみ（すべて「設定のまま」）。デスクトップの既定プリセットと同じ。
    pub const NONE: Self = Self {
        render_scale: None,
        shadows: None,
        shadow: None,
        shadow_resolution: None,
        shadow_distance: None,
        shadow_pcf_taps: None,
        gi: None,
        ao: None,
        reflection: None,
        translucency: None,
        deferred: None,
        bloom: None,
        fxaa: None,
        vignette: None,
        water_reflection: None,
        water_caustics: None,
        target_fps: None,
    };

    /// `upper` で指定されたつまみを上書きした集合を返す（`upper` の `None` は下の値を残す）【純関数】。
    ///
    /// プリセット → プロジェクトの上書き → 起動オプションの上書き、の順に重ねるのに使う。
    pub fn overlaid(self, upper: &QualityKnobs) -> Self {
        Self {
            render_scale: upper.render_scale.or(self.render_scale),
            shadows: upper.shadows.or(self.shadows),
            shadow: upper.shadow.or(self.shadow),
            shadow_resolution: upper.shadow_resolution.or(self.shadow_resolution),
            shadow_distance: upper.shadow_distance.or(self.shadow_distance),
            shadow_pcf_taps: upper.shadow_pcf_taps.or(self.shadow_pcf_taps),
            gi: upper.gi.or(self.gi),
            ao: upper.ao.or(self.ao),
            reflection: upper.reflection.or(self.reflection),
            translucency: upper.translucency.or(self.translucency),
            deferred: upper.deferred.or(self.deferred),
            bloom: upper.bloom.or(self.bloom),
            fxaa: upper.fxaa.or(self.fxaa),
            vignette: upper.vignette.or(self.vignette),
            water_reflection: upper.water_reflection.or(self.water_reflection),
            water_caustics: upper.water_caustics.or(self.water_caustics),
            target_fps: upper.target_fps.or(self.target_fps),
        }
    }

    /// 機能ごとの方式の上限（render_features の FeatureCaps）を取り出す。
    pub fn feature_caps(&self) -> FeatureCaps {
        FeatureCaps {
            shadow: self.shadow,
            gi: self.gi,
            reflection: self.reflection,
            ao: self.ao,
            translucency: self.translucency,
        }
    }

    /// 1 つも指定が無いか（デスクトップの既定プリセットと同じ＝何も下げない）。
    pub fn is_empty(&self) -> bool {
        *self == Self::NONE
    }

    /// 指定のあるつまみを `キー=値` の並びにする（起動ログ用。キーの順は KNOWN_KEYS と同じ）。
    pub fn describe(&self) -> String {
        let mut parts: Vec<String> = Vec::new();
        let mut push = |key: &str, value: Option<String>| {
            if let Some(v) = value {
                parts.push(format!("{key}={v}"));
            }
        };
        push(KEY_RENDER_SCALE, self.render_scale.map(|v| format!("{v}")));
        push(KEY_SHADOWS, self.shadows.map(|v| v.to_string()));
        push(KEY_SHADOW, self.shadow.map(|v| enum_text(&v)));
        push(KEY_SHADOW_RESOLUTION, self.shadow_resolution.map(|v| v.to_string()));
        push(KEY_SHADOW_DISTANCE, self.shadow_distance.map(|v| format!("{v}")));
        push(KEY_SHADOW_PCF_TAPS, self.shadow_pcf_taps.map(|v| v.to_string()));
        push(KEY_GI, self.gi.map(|v| enum_text(&v)));
        push(KEY_AO, self.ao.map(|v| enum_text(&v)));
        push(KEY_REFLECTION, self.reflection.map(|v| enum_text(&v)));
        push(KEY_TRANSLUCENCY, self.translucency.map(|v| enum_text(&v)));
        push(KEY_DEFERRED, self.deferred.map(|v| v.to_string()));
        push(KEY_BLOOM, self.bloom.map(|v| v.to_string()));
        push(KEY_FXAA, self.fxaa.map(|v| v.to_string()));
        push(KEY_VIGNETTE, self.vignette.map(|v| v.to_string()));
        push(KEY_WATER_REFLECTION, self.water_reflection.map(|v| v.to_string()));
        push(KEY_WATER_CAUSTICS, self.water_caustics.map(|v| v.to_string()));
        push(KEY_TARGET_FPS, self.target_fps.map(|v| v.to_string()));
        if parts.is_empty() {
            "（下げるつまみなし）".to_string()
        } else {
            parts.join(" ")
        }
    }
}

/// serde の文字列表現（小文字）を取り出す（ログ用）。表現できなければ Debug 表記。
fn enum_text<T: serde::Serialize + std::fmt::Debug>(value: &T) -> String {
    match serde_json::to_value(value) {
        Ok(Value::String(text)) => text,
        _ => format!("{value:?}"),
    }
}

// ============================================================
//  JSON の値からつまみへ
// ============================================================

/// JSON のオブジェクト（キーと値の組）からつまみを読む【純関数】。
///
/// `skip_keys` のキー（プリセット名 `preset` など、つまみでないもの）は読み飛ばす。
/// 読めないキー・値はそのつまみだけを捨て、理由を `warnings` へ足す（`context` は警告の頭に付ける出どころ）。
pub fn parse_knobs(
    object: &serde_json::Map<String, Value>,
    skip_keys: &[&str],
    context: &str,
    warnings: &mut Vec<String>,
) -> QualityKnobs {
    let mut knobs = QualityKnobs::NONE;
    for (key, value) in object {
        if skip_keys.contains(&key.as_str()) {
            continue;
        }
        if let Err(reason) = apply_knob(&mut knobs, key, value) {
            warnings.push(format!("{context}: {reason}"));
        }
    }
    knobs
}

/// つまみを 1 つ書き込む（値は値域へ収める）【純関数】。
///
/// # 戻り値
/// 読めなければ理由（知らないキー・型の違い・範囲外の数値など）。
pub fn apply_knob(knobs: &mut QualityKnobs, key: &str, value: &Value) -> Result<(), String> {
    match key {
        KEY_RENDER_SCALE => {
            let scale = read_f32(key, value)?;
            knobs.render_scale = Some(scale.clamp(MIN_RENDER_SCALE, MAX_RENDER_SCALE));
        }
        KEY_SHADOWS => knobs.shadows = Some(read_bool(key, value)?),
        KEY_SHADOW => knobs.shadow = Some(read_enum(key, value)?),
        KEY_SHADOW_RESOLUTION => {
            let resolution = read_u32(key, value)?;
            knobs.shadow_resolution = Some(nearest_shadow_resolution(resolution));
        }
        KEY_SHADOW_DISTANCE => {
            let distance = read_f32(key, value)?;
            knobs.shadow_distance = Some(distance.max(MIN_SHADOW_DISTANCE));
        }
        KEY_SHADOW_PCF_TAPS => {
            let taps = read_u32(key, value)?;
            knobs.shadow_pcf_taps = Some(taps.clamp(MIN_PCF_TAPS, MAX_PCF_TAPS));
        }
        KEY_GI => knobs.gi = Some(read_enum(key, value)?),
        KEY_AO => knobs.ao = Some(read_enum(key, value)?),
        KEY_REFLECTION => knobs.reflection = Some(read_enum(key, value)?),
        KEY_TRANSLUCENCY => knobs.translucency = Some(read_enum(key, value)?),
        KEY_DEFERRED => knobs.deferred = Some(read_bool(key, value)?),
        KEY_BLOOM => knobs.bloom = Some(read_bool(key, value)?),
        KEY_FXAA => knobs.fxaa = Some(read_bool(key, value)?),
        KEY_VIGNETTE => knobs.vignette = Some(read_bool(key, value)?),
        KEY_WATER_REFLECTION => knobs.water_reflection = Some(read_bool(key, value)?),
        KEY_WATER_CAUSTICS => knobs.water_caustics = Some(read_bool(key, value)?),
        KEY_TARGET_FPS => {
            let fps = read_u32(key, value)?;
            if fps < MIN_TARGET_FPS_CAP {
                return Err(format!(
                    "{key} は {MIN_TARGET_FPS_CAP} 以上で指定してください（0 は上限にならないので書かないでください）"
                ));
            }
            knobs.target_fps = Some(fps);
        }
        _ => {
            return Err(format!(
                "知らないつまみ {key} を読み飛ばしました（使えるのは {}）",
                KNOWN_KEYS.join(" / ")
            ))
        }
    }
    Ok(())
}

/// シャドウマップ解像度を選択肢のうち最も近いものへ丸める（shadow_settings の nearest_resolution と同じ規則）。
fn nearest_shadow_resolution(value: u32) -> u32 {
    SHADOW_RESOLUTION_CHOICES
        .iter()
        .copied()
        .min_by_key(|choice| choice.abs_diff(value))
        .unwrap_or(value)
}

/// 真偽値を読む（JSON の true / false だけ。起動オプションの文字列は呼び出し側で JSON にしてある）。
fn read_bool(key: &str, value: &Value) -> Result<bool, String> {
    value
        .as_bool()
        .ok_or_else(|| format!("{key} は true / false で指定してください（{value}）"))
}

/// 有限の数を読む。
fn read_f32(key: &str, value: &Value) -> Result<f32, String> {
    match value.as_f64() {
        Some(number) if number.is_finite() => Ok(number as f32),
        _ => Err(format!("{key} は数で指定してください（{value}）")),
    }
}

/// 0 以上の整数を読む（u32 に収まらない値は範囲外として読めない扱い）。
fn read_u32(key: &str, value: &Value) -> Result<u32, String> {
    value
        .as_u64()
        .and_then(|number| u32::try_from(number).ok())
        .ok_or_else(|| format!("{key} は 0 以上の整数で指定してください（{value}）"))
}

/// 方式（小文字の文字列）を読む。serde の表現（render_features の enum）をそのまま使う。
fn read_enum<T: serde::de::DeserializeOwned>(key: &str, value: &Value) -> Result<T, String> {
    // 大文字・前後の空白は手書きの揺れとして吸収する（serde の表現は小文字）。
    let normalized = match value {
        Value::String(text) => Value::String(text.trim().to_ascii_lowercase()),
        other => other.clone(),
    };
    serde_json::from_value(normalized)
        .map_err(|_| format!("{key} の値 {value} は使えません（方式の名前を小文字の文字列で指定してください）"))
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    fn object(json: &str) -> serde_json::Map<String, Value> {
        match serde_json::from_str::<Value>(json).unwrap() {
            Value::Object(map) => map,
            other => panic!("オブジェクトではありません: {other}"),
        }
    }

    /// 全キーを 1 回ずつ読めること（KNOWN_KEYS と apply_knob の分岐が一致している）。
    #[test]
    fn every_known_key_is_readable() {
        let samples: [(&str, Value); 17] = [
            (KEY_RENDER_SCALE, Value::from(0.75)),
            (KEY_SHADOWS, Value::from(false)),
            (KEY_SHADOW, Value::from("shadowmap")),
            (KEY_SHADOW_RESOLUTION, Value::from(1024)),
            (KEY_SHADOW_DISTANCE, Value::from(40.0)),
            (KEY_SHADOW_PCF_TAPS, Value::from(4)),
            (KEY_GI, Value::from("flat")),
            (KEY_AO, Value::from("off")),
            (KEY_REFLECTION, Value::from("off")),
            (KEY_TRANSLUCENCY, Value::from("raster")),
            (KEY_DEFERRED, Value::from(false)),
            (KEY_BLOOM, Value::from(false)),
            (KEY_FXAA, Value::from(true)),
            (KEY_VIGNETTE, Value::from(false)),
            (KEY_WATER_REFLECTION, Value::from(false)),
            (KEY_WATER_CAUSTICS, Value::from(false)),
            (KEY_TARGET_FPS, Value::from(30)),
        ];
        let sample_keys: Vec<&str> = samples.iter().map(|(k, _)| *k).collect();
        assert_eq!(sample_keys, KNOWN_KEYS.to_vec(), "テストの見本と KNOWN_KEYS の順をそろえる");
        let mut knobs = QualityKnobs::NONE;
        for (key, value) in &samples {
            apply_knob(&mut knobs, key, value).unwrap_or_else(|e| panic!("{key}: {e}"));
        }
        assert_eq!(knobs.render_scale, Some(0.75));
        assert_eq!(knobs.shadows, Some(false));
        assert_eq!(knobs.shadow, Some(ShadowMode::ShadowMap));
        assert_eq!(knobs.shadow_resolution, Some(1024));
        assert_eq!(knobs.shadow_distance, Some(40.0));
        assert_eq!(knobs.shadow_pcf_taps, Some(4));
        assert_eq!(knobs.gi, Some(GiMode::Flat));
        assert_eq!(knobs.ao, Some(AoMode::Off));
        assert_eq!(knobs.reflection, Some(ReflectionMode::Off));
        assert_eq!(knobs.translucency, Some(TranslucencyMode::Raster));
        assert_eq!(knobs.deferred, Some(false));
        assert_eq!(knobs.bloom, Some(false));
        assert_eq!(knobs.fxaa, Some(true));
        assert_eq!(knobs.vignette, Some(false));
        assert_eq!(knobs.water_reflection, Some(false));
        assert_eq!(knobs.water_caustics, Some(false));
        assert_eq!(knobs.target_fps, Some(30));
        // describe はすべてのつまみを出す。
        let text = knobs.describe();
        for key in KNOWN_KEYS {
            assert!(text.contains(&format!("{key}=")), "{key} が無い: {text}");
        }
    }

    /// 範囲外の値は値域へ収める（スケール 0.5〜1.0・解像度は選択肢へ・タップ 1〜16・距離 1 以上）。
    #[test]
    fn values_are_clamped_into_range() {
        let mut warnings = Vec::new();
        let knobs = parse_knobs(
            &object(
                r#"{"render_scale": 0.1, "shadow_resolution": 3000, "shadow_pcf_taps": 99, "shadow_distance": -5}"#,
            ),
            &[],
            "テスト",
            &mut warnings,
        );
        assert!(warnings.is_empty(), "{warnings:?}");
        assert_eq!(knobs.render_scale, Some(MIN_RENDER_SCALE));
        assert_eq!(knobs.shadow_resolution, Some(2048));
        assert_eq!(knobs.shadow_pcf_taps, Some(MAX_PCF_TAPS));
        assert_eq!(knobs.shadow_distance, Some(MIN_SHADOW_DISTANCE));
        let knobs = parse_knobs(&object(r#"{"render_scale": 2.0}"#), &[], "テスト", &mut warnings);
        assert_eq!(knobs.render_scale, Some(MAX_RENDER_SCALE));
    }

    /// 読めない値・知らないキーはそのつまみだけ捨てて警告し、他のつまみは読む。
    #[test]
    fn bad_values_are_skipped_with_warnings() {
        let mut warnings = Vec::new();
        let knobs = parse_knobs(
            &object(
                r#"{"preset": "mobile", "render_scale": "big", "bloom": "no", "gi": "photon",
                    "unknown_knob": 1, "target_fps": 0, "fxaa": false}"#,
            ),
            &["preset"],
            "プロジェクト設定",
            &mut warnings,
        );
        assert_eq!(knobs.fxaa, Some(false), "読める値は読む");
        assert_eq!(knobs.render_scale, None);
        assert_eq!(knobs.bloom, None);
        assert_eq!(knobs.gi, None);
        assert_eq!(knobs.target_fps, None);
        assert_eq!(warnings.len(), 5, "{warnings:?}");
        assert!(warnings.iter().all(|w| w.starts_with("プロジェクト設定: ")), "{warnings:?}");
        assert!(warnings.iter().any(|w| w.contains("unknown_knob")), "{warnings:?}");
    }

    /// 方式の名前は大文字・空白の揺れを吸収する。
    #[test]
    fn enum_names_are_normalized() {
        let mut knobs = QualityKnobs::NONE;
        apply_knob(&mut knobs, KEY_GI, &Value::from(" SSGI ")).unwrap();
        assert_eq!(knobs.gi, Some(GiMode::Ssgi));
        apply_knob(&mut knobs, KEY_SHADOW, &Value::from("ShadowMap")).unwrap();
        assert_eq!(knobs.shadow, Some(ShadowMode::ShadowMap));
    }

    /// 上書きは上の Some だけが勝ち、None は下の値を残す。
    #[test]
    fn overlay_keeps_lower_values_for_none() {
        let lower = QualityKnobs { render_scale: Some(0.5), bloom: Some(false), ..QualityKnobs::NONE };
        let upper = QualityKnobs { render_scale: Some(0.75), fxaa: Some(true), ..QualityKnobs::NONE };
        let merged = lower.overlaid(&upper);
        assert_eq!(merged.render_scale, Some(0.75));
        assert_eq!(merged.bloom, Some(false));
        assert_eq!(merged.fxaa, Some(true));
        assert_eq!(QualityKnobs::NONE.overlaid(&QualityKnobs::NONE), QualityKnobs::NONE);
        assert!(QualityKnobs::NONE.is_empty());
        assert!(!merged.is_empty());
    }
}
