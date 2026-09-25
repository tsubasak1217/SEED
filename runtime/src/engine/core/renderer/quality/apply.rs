// ============================================================
//  quality/apply.rs — 実効の描画品質を各設定へ当てる（純関数）
//
//  【役割】
//  RenderQuality（プリセット＋上書き）のつまみを、既存の設定値（プロジェクト設定・シーン設定が決めたもの）へ
//  「上限」「可否」として当てる。当て方はつまみごとに次のとおり:
//    - 上限（shadow_resolution / shadow_distance / shadow_pcf_taps）… 設定値と上限の小さい方
//    - 可否（deferred / bloom / fxaa / vignette / water_* / shadows）… false なら止める。true・未指定は設定値のまま
//  つまみが未指定なら設定値をそのまま返す（デスクトップの既定＝何も変えない。テストで固定）。
//
//  機能の方式の上限（gi / ao / reflection / shadow / translucency）は render_features.rs の
//  `resolve_with_caps`、描画スケールは quality/scale.rs、目標フレームレートの上限は
//  app/frame_pacing.rs の `cap_target_fps` が受け持つ（それぞれの値の持ち主のそばに置く）。
// ============================================================

use crate::engine::core::renderer::shadow_settings::ShadowQuality;

use super::knobs::QualityKnobs;
use super::resolve::RenderQuality;

/// 可否のつまみを当てる【純関数】。`false` を指定されたときだけ止める（`true`・未指定は要求どおり）。
pub fn allow(requested: bool, knob: Option<bool>) -> bool {
    requested && knob != Some(false)
}

/// シャドウマップの品質へ上限を当てる【純関数】（解像度・距離・PCF タップ数は小さい方）。
///
/// 上限が無ければ `base` をそのまま返す。`base` は `ShadowQuality::sanitize` 済みの前提で、
/// 小さい方を採るだけなので値域は崩れない（解像度は選択肢どうしの最小、距離・タップ数は下限以上）。
pub fn cap_shadow_quality(base: ShadowQuality, knobs: &QualityKnobs) -> ShadowQuality {
    ShadowQuality {
        resolution: knobs.shadow_resolution.map_or(base.resolution, |cap| base.resolution.min(cap)),
        distance: knobs.shadow_distance.map_or(base.distance, |cap| base.distance.min(cap)),
        pcf_taps: knobs.shadow_pcf_taps.map_or(base.pcf_taps, |cap| base.pcf_taps.min(cap)),
        ..base
    }
}

impl RenderQuality {
    /// 影（シャドウマップ）を描いてよいか（`shadows: false` で止める）。
    pub fn shadows_enabled(&self) -> bool {
        allow(true, self.knobs.shadows)
    }

    /// デファード（G-Buffer）を使うか（要求 `requested` に可否を当てる）。
    pub fn deferred(&self, requested: bool) -> bool {
        allow(requested, self.knobs.deferred)
    }

    /// ブルームを使うか。
    pub fn bloom(&self, requested: bool) -> bool {
        allow(requested, self.knobs.bloom)
    }

    /// FXAA を使うか。
    pub fn fxaa(&self, requested: bool) -> bool {
        allow(requested, self.knobs.fxaa)
    }

    /// ビネットを使うか。
    pub fn vignette(&self, requested: bool) -> bool {
        allow(requested, self.knobs.vignette)
    }

    /// 水面反射を使うか。
    pub fn water_reflection(&self, requested: bool) -> bool {
        allow(requested, self.knobs.water_reflection)
    }

    /// 水中コースティクスを使うか。
    pub fn water_caustics(&self, requested: bool) -> bool {
        allow(requested, self.knobs.water_caustics)
    }

    /// シャドウマップの品質へ上限を当てる（起動時・シャドウ資源を作る前に 1 回）。
    pub fn shadow_quality(&self, base: ShadowQuality) -> ShadowQuality {
        cap_shadow_quality(base, &self.knobs)
    }

    /// 目標フレームレートの上限（無ければ None）。
    pub fn target_fps_cap(&self) -> Option<u32> {
        self.knobs.target_fps
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 可否: false だけが止める。true・未指定は要求どおり（止まっているものを動かしはしない）。
    #[test]
    fn allow_only_turns_off() {
        assert!(allow(true, None));
        assert!(allow(true, Some(true)));
        assert!(!allow(true, Some(false)));
        assert!(!allow(false, None));
        assert!(!allow(false, Some(true)), "true でも要求 false を上げない");
    }

    /// つまみが無ければ何も変えない（デスクトップの既定）。
    #[test]
    fn empty_knobs_change_nothing() {
        let q = RenderQuality::default();
        for requested in [false, true] {
            assert_eq!(q.deferred(requested), requested);
            assert_eq!(q.bloom(requested), requested);
            assert_eq!(q.fxaa(requested), requested);
            assert_eq!(q.vignette(requested), requested);
            assert_eq!(q.water_reflection(requested), requested);
            assert_eq!(q.water_caustics(requested), requested);
        }
        assert!(q.shadows_enabled());
        let base = ShadowQuality::default();
        assert_eq!(q.shadow_quality(base), base);
        let custom = ShadowQuality { resolution: 4096, distance: 500.0, pcf_taps: 16, ..base };
        assert_eq!(q.shadow_quality(custom), custom);
        assert_eq!(q.target_fps_cap(), None);
        assert_eq!(q.render_scale(), 1.0);
    }

    /// 影の上限は小さい方（設定が上限より軽ければ設定のまま）。
    #[test]
    fn shadow_caps_take_the_smaller_value() {
        let knobs = QualityKnobs {
            shadow_resolution: Some(1024),
            shadow_distance: Some(50.0),
            shadow_pcf_taps: Some(4),
            ..QualityKnobs::NONE
        };
        let base = ShadowQuality::default();
        let capped = cap_shadow_quality(base, &knobs);
        assert_eq!(capped.resolution, 1024);
        assert_eq!(capped.distance, 50.0);
        assert_eq!(capped.pcf_taps, 4);
        // 上限と関係の無い欄はそのまま。
        assert_eq!(capped.split_lambda, base.split_lambda);
        assert_eq!(capped.slope_bias, base.slope_bias);
        // 設定のほうが軽ければ設定のまま（上げない）。
        let light = ShadowQuality { resolution: 1024, distance: 20.0, pcf_taps: 2, ..base };
        let knobs_high = QualityKnobs {
            shadow_resolution: Some(4096),
            shadow_distance: Some(1000.0),
            shadow_pcf_taps: Some(16),
            ..QualityKnobs::NONE
        };
        assert_eq!(cap_shadow_quality(light, &knobs_high), light);
        // 結果は sanitize しても変わらない（値域の内側）。
        assert_eq!(capped.sanitize(), capped);
    }
}
