// ============================================================
//  render_features.rs — レンダリング機能マトリクス（機能 × モード）
//
//  RT-Shadow / RT-GI / RT-Reflection / RT-AO / RT-Translucency の 5 機能を
//  それぞれ独立した enum（モード）で表現し、まとめ役 `RenderFeatures` に集約する。
//  各機能は「レイトレ実装」と「代替（スクリーンスペース系／ラスタ／なし）」を
//  モードとして選べる。データドリブン: エディタからは機能ごとの小文字文字列
//  （例 shadow="rt"）で受け取り、serde でこの enum 群へデシリアライズする。
//
//  ■ 降格・未実装判定の集約点 = `RenderFeatures::resolve()`
//    - RT 非対応 GPU では Rt 系モードを実効不可なので代替へ自動降格する。
//    - Reflection / AO / Translucency の Rt/Ssr/Ssao は「フレームワークのみ・実体未実装」。
//      resolve は常にフォールバック（Off / Raster）へ倒す。実装が入ったら resolve の
//      該当腕を「rt_supported 条件で通す」よう変えるだけでよい（呼び出し側は不変）。
//    実行時に分岐する箇所（frame_renderer 等）は必ず resolve() の結果
//    （ResolvedFeatures）を参照すること。生の RenderFeatures を直接見ないこと。
//
//  ■ TLAS 構築ゲートの一般化
//    ResolvedFeatures::needs_tlas() は「解決後モードのいずれかが Rt か」を返す。
//    frame_renderer の TLAS 構築はこの 1 メソッドで判定するため、将来
//    Reflection/AO/Translucency の Rt が resolve を通るようになれば、ゲート側は
//    一切触らずに TLAS が構築されるようになる。
// ============================================================

use serde::{Serialize, Deserialize};

// ============================================================
//  機能ごとのモード enum（serde 互換・文字列表現は小文字）
// ============================================================

/// 影の描画方式。現行の rt_shadows（bool）を置換する。
/// 既定はシャドウマップ（＝従来のプロジェクト既定 rt_shadows=false と一致）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ShadowMode {
    /// インラインレイトレ影（Phase R8, RT 対応 GPU のみ）。
    Rt,
    /// シャドウマップ（CSM＋スポット, 従来経路）。既定。
    #[serde(rename = "shadowmap")]
    ShadowMap,
}

impl Default for ShadowMode {
    fn default() -> Self { ShadowMode::ShadowMap }
}

/// GI（間接光）の方式。現行の GiSettings.enabled（bool）を置換する。
/// 将来 Ssgi（スクリーンスペース GI）を追加予定。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum GiMode {
    /// DDGI（プローブ格子レイトレ GI, Phase RT-GI, RT 対応 GPU のみ）。
    Rt,
    /// フラット（環境光のみ・間接光なし）。既定。
    Flat,
    // 将来追加予定: Ssgi（スクリーンスペース GI）。
}

impl Default for GiMode {
    fn default() -> Self { GiMode::Flat }
}

/// 反射の方式。**実体は未実装**（フレームワークのみ）。
/// Rt/Ssr が選ばれても現状は Off と同じ動作＝フォールバックする。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ReflectionMode {
    /// レイトレ反射（未実装）。
    Rt,
    /// スクリーンスペース反射（未実装）。
    Ssr,
    /// 反射なし（既定・現状動作）。
    Off,
}

impl Default for ReflectionMode {
    fn default() -> Self { ReflectionMode::Off }
}

/// AO の方式。既定 Off ＝現状のマテリアル AO のみ。
/// Rt/Ssao は**未実装**で、選ばれても Off へフォールバックする。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum AoMode {
    /// レイトレ AO（未実装）。
    Rt,
    /// スクリーンスペース AO（未実装）。
    Ssao,
    /// 追加 AO なし（既定・マテリアル AO のみ）。
    Off,
}

impl Default for AoMode {
    fn default() -> Self { AoMode::Off }
}

/// 半透明の描画方式。既定 Raster ＝現行の WBOIT/距離ソート経路。
/// Rt は**未実装**で、選ばれても Raster へフォールバックする。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum TranslucencyMode {
    /// レイトレ半透明（未実装）。
    Rt,
    /// ラスタ半透明（既定・従来の WBOIT/距離ソート）。
    Raster,
}

impl Default for TranslucencyMode {
    fn default() -> Self { TranslucencyMode::Raster }
}

// ============================================================
//  RenderFeatures — 5 機能のモードをまとめる要求（未解決）値
// ============================================================

/// レンダリング機能マトリクスの要求値（エディタ／プロジェクト設定が指定するモード集合）。
///
/// これは「ユーザーが選んだ生のモード」であり、GPU 対応可否や実装有無は反映されていない。
/// 実行時の分岐には必ず resolve() で得た ResolvedFeatures を使うこと。
///
/// serde: 各フィールド #[serde(default)]。欠落キーは enum の Default になる
/// （旧 .scene／旧エディタ JSON との後方互換。features オブジェクトの部分指定も許す）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub struct RenderFeatures {
    /// 影の方式。
    #[serde(default)]
    pub shadow: ShadowMode,
    /// GI（間接光）の方式。
    #[serde(default)]
    pub gi: GiMode,
    /// 反射の方式（未実装）。
    #[serde(default)]
    pub reflection: ReflectionMode,
    /// AO の方式（未実装）。
    #[serde(default)]
    pub ao: AoMode,
    /// 半透明の方式。
    #[serde(default)]
    pub translucency: TranslucencyMode,
}

impl RenderFeatures {
    /// 要求モードを GPU 対応可否・実装有無で解決し、実効モード（ResolvedFeatures）を返す。
    ///
    /// **降格・未実装判定はここ 1 箇所に集約する**（呼び出し側で個別に判定しない）。
    /// - `rt_supported`: この GPU がインラインレイトレに対応するか
    ///   （rt_shadow::rt_shadows_supported()）。
    ///
    /// 降格規則:
    /// - Shadow::Rt / Gi::Rt … rt_supported==false なら代替（ShadowMap / Flat）へ降格。
    /// - Reflection / Ao / Translucency … Rt/Ssr/Ssao はいずれも**未実装**のため、
    ///   rt_supported に関わらず常にフォールバック（Off / Raster）へ倒す。
    ///   実装完了時は該当腕を「rt_supported 条件付きで通す」よう変更する。
    pub fn resolve(&self, rt_supported: bool) -> ResolvedFeatures {
        ResolvedFeatures {
            // 影: RT 対応時のみ Rt を通す。非対応ならシャドウマップへ。
            shadow: match self.shadow {
                ShadowMode::Rt if rt_supported => ShadowMode::Rt,
                _ => ShadowMode::ShadowMap,
            },
            // GI: RT 対応時のみ Rt を通す。非対応ならフラットへ。
            gi: match self.gi {
                GiMode::Rt if rt_supported => GiMode::Rt,
                _ => GiMode::Flat,
            },
            // 反射: 実体未実装。常に Off へ倒す。
            //   実装時 →  ReflectionMode::Rt  if rt_supported => Rt,
            //             ReflectionMode::Ssr                 => Ssr,
            reflection: ReflectionMode::Off,
            // AO: 実体未実装。常に Off へ倒す（マテリアル AO のみ）。
            ao: AoMode::Off,
            // 半透明: Rt 未実装。常に Raster（従来経路）へ倒す。
            translucency: TranslucencyMode::Raster,
        }
    }

    /// [SEED FEATURES] ログ 1 行を生成する（要求と実効の差＝降格／未実装を注記する）。
    ///
    /// 例: shadow=rt gi=rt reflection=off(未実装) ao=off(未実装) translucency=raster
    /// - Rt/Ssr/Ssao など「要求したが実効が代替に落ちた」機能には (未実装) を付ける。
    pub fn log_line(&self, rt_supported: bool) -> String {
        let r = self.resolve(rt_supported);
        let refl_note  = if self.reflection   != ReflectionMode::Off  { "(未実装)" } else { "" };
        let ao_note    = if self.ao           != AoMode::Off          { "(未実装)" } else { "" };
        let trans_note = if self.translucency == TranslucencyMode::Rt { "(未実装)" } else { "" };
        format!(
            "shadow={} gi={} reflection={}{} ao={}{} translucency={}{}",
            mode_str_shadow(r.shadow),
            mode_str_gi(r.gi),
            mode_str_reflection(r.reflection), refl_note,
            mode_str_ao(r.ao), ao_note,
            mode_str_translucency(r.translucency), trans_note,
        )
    }
}

// ============================================================
//  ResolvedFeatures — 解決済み（実効）モード集合
// ============================================================

/// 解決済みのレンダリング機能モード。GPU 対応・実装有無を反映済みで、
/// 「このフレームで実際に走らせる」モードを表す。frame_renderer 等の実行時分岐は
/// これを参照する。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ResolvedFeatures {
    /// 実効の影方式。
    pub shadow: ShadowMode,
    /// 実効の GI 方式。
    pub gi: GiMode,
    /// 実効の反射方式（現状は常に Off）。
    pub reflection: ReflectionMode,
    /// 実効の AO 方式（現状は常に Off）。
    pub ao: AoMode,
    /// 実効の半透明方式（現状は常に Raster）。
    pub translucency: TranslucencyMode,
}

impl ResolvedFeatures {
    /// この実効モードでインラインレイトレ影を使うか。
    pub fn rt_shadow(&self) -> bool { self.shadow == ShadowMode::Rt }

    /// この実効モードで DDGI（レイトレ GI）を使うか。
    pub fn rt_gi(&self) -> bool { self.gi == GiMode::Rt }

    /// TLAS（レイトレ加速構造）を構築する必要があるか。
    ///
    /// **いずれかの機能が Rt に解決されれば true**。TLAS 構築ゲートを一般化する集約点で、
    /// 将来 Reflection/AO/Translucency の Rt が resolve を通るようになれば、
    /// frame_renderer 側を触らずに自動で TLAS 構築が走る。
    pub fn needs_tlas(&self) -> bool {
        self.shadow == ShadowMode::Rt
            || self.gi == GiMode::Rt
            || self.reflection == ReflectionMode::Rt
            || self.ao == AoMode::Rt
            || self.translucency == TranslucencyMode::Rt
    }
}

// ─── ログ用の小文字文字列化（serde の表現と一致させる）──────────
fn mode_str_shadow(m: ShadowMode) -> &'static str {
    match m { ShadowMode::Rt => "rt", ShadowMode::ShadowMap => "shadowmap" }
}
fn mode_str_gi(m: GiMode) -> &'static str {
    match m { GiMode::Rt => "rt", GiMode::Flat => "flat" }
}
fn mode_str_reflection(m: ReflectionMode) -> &'static str {
    match m { ReflectionMode::Rt => "rt", ReflectionMode::Ssr => "ssr", ReflectionMode::Off => "off" }
}
fn mode_str_ao(m: AoMode) -> &'static str {
    match m { AoMode::Rt => "rt", AoMode::Ssao => "ssao", AoMode::Off => "off" }
}
fn mode_str_translucency(m: TranslucencyMode) -> &'static str {
    match m { TranslucencyMode::Rt => "rt", TranslucencyMode::Raster => "raster" }
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 既定値が「現状の見た目を壊さない」代替側であること。
    #[test]
    fn defaults_are_fallback_side() {
        let f = RenderFeatures::default();
        assert_eq!(f.shadow, ShadowMode::ShadowMap);
        assert_eq!(f.gi, GiMode::Flat);
        assert_eq!(f.reflection, ReflectionMode::Off);
        assert_eq!(f.ao, AoMode::Off);
        assert_eq!(f.translucency, TranslucencyMode::Raster);
    }

    /// serde 文字列表現が小文字であること（往復）。
    #[test]
    fn serde_roundtrip_lowercase_strings() {
        let f = RenderFeatures {
            shadow: ShadowMode::Rt,
            gi: GiMode::Rt,
            reflection: ReflectionMode::Ssr,
            ao: AoMode::Ssao,
            translucency: TranslucencyMode::Rt,
        };
        let json = serde_json::to_string(&f).unwrap();
        assert!(json.contains("\"shadow\":\"rt\""), "json={json}");
        assert!(json.contains("\"gi\":\"rt\""), "json={json}");
        assert!(json.contains("\"reflection\":\"ssr\""), "json={json}");
        assert!(json.contains("\"ao\":\"ssao\""), "json={json}");
        assert!(json.contains("\"translucency\":\"rt\""), "json={json}");
        let back: RenderFeatures = serde_json::from_str(&json).unwrap();
        assert_eq!(f, back);

        let sm = serde_json::to_string(&ShadowMode::ShadowMap).unwrap();
        assert_eq!(sm, "\"shadowmap\"");
    }

    /// 欠落キーは serde default（各 enum の Default）で埋まること（旧エディタ互換）。
    #[test]
    fn serde_missing_keys_use_defaults() {
        let f: RenderFeatures = serde_json::from_str(r#"{"shadow":"rt"}"#).unwrap();
        assert_eq!(f.shadow, ShadowMode::Rt);
        assert_eq!(f.gi, GiMode::Flat);
        assert_eq!(f.reflection, ReflectionMode::Off);
        assert_eq!(f.ao, AoMode::Off);
        assert_eq!(f.translucency, TranslucencyMode::Raster);

        let empty: RenderFeatures = serde_json::from_str("{}").unwrap();
        assert_eq!(empty, RenderFeatures::default());
    }

    /// RT 非対応 GPU では Rt 系が代替へ降格すること。
    #[test]
    fn resolve_downgrades_rt_when_unsupported() {
        let f = RenderFeatures {
            shadow: ShadowMode::Rt,
            gi: GiMode::Rt,
            reflection: ReflectionMode::Rt,
            ao: AoMode::Rt,
            translucency: TranslucencyMode::Rt,
        };
        let r = f.resolve(false);
        assert_eq!(r.shadow, ShadowMode::ShadowMap);
        assert_eq!(r.gi, GiMode::Flat);
        assert!(!r.needs_tlas());

        let r = f.resolve(true);
        assert_eq!(r.shadow, ShadowMode::Rt);
        assert_eq!(r.gi, GiMode::Rt);
        assert_eq!(r.reflection, ReflectionMode::Off);
        assert_eq!(r.ao, AoMode::Off);
        assert_eq!(r.translucency, TranslucencyMode::Raster);
        assert!(r.needs_tlas());
    }

    /// 未実装機能が Off/Raster のみのときは TLAS 不要であること。
    #[test]
    fn needs_tlas_false_without_rt() {
        let f = RenderFeatures {
            shadow: ShadowMode::ShadowMap,
            gi: GiMode::Flat,
            reflection: ReflectionMode::Ssr,
            ao: AoMode::Ssao,
            translucency: TranslucencyMode::Raster,
        };
        assert!(!f.resolve(true).needs_tlas());
    }

    /// ログ行が未実装注記を含むこと。
    #[test]
    fn log_line_annotates_unimplemented() {
        let f = RenderFeatures {
            shadow: ShadowMode::Rt,
            gi: GiMode::Rt,
            reflection: ReflectionMode::Ssr,
            ao: AoMode::Rt,
            translucency: TranslucencyMode::Rt,
        };
        let line = f.log_line(true);
        assert!(line.contains("shadow=rt"), "{line}");
        assert!(line.contains("gi=rt"), "{line}");
        assert!(line.contains("reflection=off(未実装)"), "{line}");
        assert!(line.contains("ao=off(未実装)"), "{line}");
        assert!(line.contains("translucency=raster(未実装)"), "{line}");
    }
}
