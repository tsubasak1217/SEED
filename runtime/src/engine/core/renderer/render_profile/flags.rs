// ============================================================
//  render_profile/flags.rs — 描画の構成の旗（RenderProfileFlags）と、JSON からの読み取り
//
//  【旗の意味】（既定はすべて「用意する」＝ full。false にした旗の資源・パスだけを作らない）
//    scene_3d     … 3D のシーン（モデル・地形・散布・草・水・天球・SEED.Draw3D）を描くか。
//                   false なら描かない（あれば 1 回だけ警告）。キャンバス・文字・図形・スプライト・2D の粒子は描く
//    deferred     … G-Buffer（デファード）を使ってよいか。false なら前方描画（G-Buffer の RT を確保しない）
//    shadows      … シャドウマップを確保するか。false なら 1x1 の置き場だけ作り、影を描かない
//    gi           … GI（DDGI）のアトラスを確保するか。false なら 1x1 の置き場だけ作り、GI は平坦な環境光
//    bindless     … bindless のテクスチャ配列（4096 枠）とメガバッファ（224 MiB）を確保するか
//    ray_tracing  … レイトレーシングの機能（RT 影・RT 反射・DDGI の更新）をデバイスへ求めるか
//    post         … 後処理（ブルーム・ビネット・FXAA）を使ってよいか。false なら止める（トーンマップは残す）
//    picking      … Play 中もピッキング用の ID バッファ（画面と同じ大きさの Rgba32Float）を確保するか。
//                   Edit・エディタに埋め込んだ Play・SEED_ID_PASS_IN_PLAY のときは false でも確保する
//    memory_hint  … GPU メモリの確保の方針（wgpu の MemoryHints）。
//                   "performance"（既定。大きな塊でまとめて確保する）/ "memory_usage"（小さな塊で確保する）
//  旗は「下げる」向きにだけ効く（false で止める）。資源は起動時に確保するので、実行中には変えられない。
// ============================================================

use serde_json::Value;

use crate::engine::core::renderer::quality::QualityKnobs;
use crate::engine::core::renderer::render_features::{AoMode, GiMode, ReflectionMode, TranslucencyMode};

// ─── キーの名前（project_settings.json の render 節・render_profiles.json の flags・docs と一致させる）──

/// 3D のシーンを描くかのキー。
pub const KEY_SCENE_3D: &str = "scene_3d";
/// デファードの可否のキー。
pub const KEY_DEFERRED: &str = "deferred";
/// シャドウマップの確保のキー。
pub const KEY_SHADOWS: &str = "shadows";
/// GI のアトラスの確保のキー。
pub const KEY_GI: &str = "gi";
/// bindless の確保のキー。
pub const KEY_BINDLESS: &str = "bindless";
/// レイトレーシングの機能の要求のキー。
pub const KEY_RAY_TRACING: &str = "ray_tracing";
/// 後処理の可否のキー。
pub const KEY_POST: &str = "post";
/// Play 中のピッキングの ID バッファの確保のキー。
pub const KEY_PICKING: &str = "picking";
/// GPU メモリの確保の方針のキー。
pub const KEY_MEMORY_HINT: &str = "memory_hint";

/// 知っている旗のキーの一覧（表の順。ログ・警告・ドキュメントの突き合わせに使う）。
pub const KNOWN_FLAG_KEYS: [&str; 9] = [
    KEY_SCENE_3D,
    KEY_DEFERRED,
    KEY_SHADOWS,
    KEY_GI,
    KEY_BINDLESS,
    KEY_RAY_TRACING,
    KEY_POST,
    KEY_PICKING,
    KEY_MEMORY_HINT,
];

/// memory_hint の値: 大きな塊でまとめて確保する（wgpu の既定）。
pub const MEMORY_HINT_PERFORMANCE: &str = "performance";
/// memory_hint の値: 小さな塊で確保する（メモリを節約する）。
pub const MEMORY_HINT_MEMORY_USAGE: &str = "memory_usage";

/// GPU メモリの確保の方針（wgpu の `MemoryHints` に対応）。
///
/// wgpu 25 の Vulkan 実装（gpu-alloc）では、`Performance` は転送用の塊を 128 MiB から（倍々で 512 MiB まで）、
/// 32 MiB 未満の資源を 2 の冪に切り上げた塊（最初 8 MiB）から切り出す。`MemoryUsage` は転送用 8〜64 MiB・
/// 8 MiB 以上の資源は専用に確保する（切り上げない）。UI だけのアプリでは使い切らない塊が大半を占めるので小さくする。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum MemoryHint {
    /// 大きな塊でまとめて確保する（既定）。
    #[default]
    Performance,
    /// 小さな塊で確保する。
    MemoryUsage,
}

impl MemoryHint {
    /// 名前（JSON・ログ用）。
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Performance => MEMORY_HINT_PERFORMANCE,
            Self::MemoryUsage => MEMORY_HINT_MEMORY_USAGE,
        }
    }

    /// 名前から読む（前後の空白・大文字小文字は区別しない）。知らない名前は None。
    pub fn parse(text: &str) -> Option<Self> {
        let lower = text.trim().to_ascii_lowercase();
        match lower.as_str() {
            MEMORY_HINT_PERFORMANCE => Some(Self::Performance),
            MEMORY_HINT_MEMORY_USAGE => Some(Self::MemoryUsage),
            _ => None,
        }
    }

    /// wgpu のデバイスの作り方の指定へ変える。
    pub fn to_wgpu(self) -> wgpu::MemoryHints {
        match self {
            Self::Performance => wgpu::MemoryHints::Performance,
            Self::MemoryUsage => wgpu::MemoryHints::MemoryUsage,
        }
    }
}

/// 描画の構成の旗の集合。既定（`Default`）は full＝すべて用意する（従来と同じ）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RenderProfileFlags {
    /// 3D のシーンを描くか。
    pub scene_3d: bool,
    /// G-Buffer（デファード）を使ってよいか。
    pub deferred: bool,
    /// シャドウマップを確保するか。
    pub shadows: bool,
    /// GI（DDGI）のアトラスを確保するか。
    pub gi: bool,
    /// bindless のテクスチャ配列・メガバッファを確保するか。
    pub bindless: bool,
    /// レイトレーシングの機能をデバイスへ求めるか。
    pub ray_tracing: bool,
    /// 後処理（ブルーム・ビネット・FXAA）を使ってよいか。
    pub post: bool,
    /// Play 中もピッキングの ID バッファを確保するか。
    pub picking: bool,
    /// GPU メモリの確保の方針。
    pub memory_hint: MemoryHint,
}

impl Default for RenderProfileFlags {
    /// full（すべて用意する。従来と同じ）。
    fn default() -> Self {
        Self::FULL
    }
}

impl RenderProfileFlags {
    /// すべて用意する旗（full。従来の描画と同じ）。
    pub const FULL: Self = Self {
        scene_3d: true,
        deferred: true,
        shadows: true,
        gi: true,
        bindless: true,
        ray_tracing: true,
        post: true,
        picking: true,
        memory_hint: MemoryHint::Performance,
    };

    /// full と同じか（何も止めない＝従来どおり）。
    pub fn is_full(&self) -> bool {
        *self == Self::FULL
    }

    // ── 実効の判断（3D を描かない構成では、3D だけの資源は個々の旗によらず作らない）──
    //    `scene_3d: false` だけを書いても 3D の資源がまとめて止まるように、資源を作る側はこれらを読む
    //    （品質へ当てる上限 `quality_caps` も同じ規則）。

    /// シャドウマップを確保するか（`shadows` かつ `scene_3d`）。
    pub fn allocates_shadows(&self) -> bool {
        self.shadows && self.scene_3d
    }

    /// GI（DDGI）のアトラスを確保するか（`gi` かつ `scene_3d`）。
    pub fn allocates_gi(&self) -> bool {
        self.gi && self.scene_3d
    }

    /// bindless の資源を確保し、その機能をデバイスへ求めるか（`bindless` かつ `scene_3d`）。
    pub fn allocates_bindless(&self) -> bool {
        self.bindless && self.scene_3d
    }

    /// レイトレーシングの機能をデバイスへ求めるか（`ray_tracing` かつ `scene_3d`）。
    pub fn requests_ray_tracing(&self) -> bool {
        self.ray_tracing && self.scene_3d
    }

    /// G-Buffer（デファード）を使ってよいか（`deferred` かつ `scene_3d`）。
    pub fn allows_deferred(&self) -> bool {
        self.deferred && self.scene_3d
    }

    /// 旗を `キー=値` の並びにする（起動ログ用。キーの順は KNOWN_FLAG_KEYS と同じ）。
    pub fn describe(&self) -> String {
        [
            format!("{KEY_SCENE_3D}={}", self.scene_3d),
            format!("{KEY_DEFERRED}={}", self.deferred),
            format!("{KEY_SHADOWS}={}", self.shadows),
            format!("{KEY_GI}={}", self.gi),
            format!("{KEY_BINDLESS}={}", self.bindless),
            format!("{KEY_RAY_TRACING}={}", self.ray_tracing),
            format!("{KEY_POST}={}", self.post),
            format!("{KEY_PICKING}={}", self.picking),
            format!("{KEY_MEMORY_HINT}={}", self.memory_hint.as_str()),
        ]
        .join(" ")
    }

    /// 止めた旗に合わせて描画品質へ当てる上限（下げる向きだけ。`QualityKnobs::overlaid` で重ねる）。
    ///
    /// 毎フレームの可否の判定（デファード・影・後処理・水面・GI などの方式）は描画品質のつまみが既に
    /// 受け持っているので、構成はそこへ「止める」値を足すだけにする（判定の重複を作らない）。
    /// full（何も止めない）なら何も指定しないつまみ（`QualityKnobs::NONE`）を返す。
    pub fn quality_caps(&self) -> QualityKnobs {
        let mut caps = QualityKnobs::NONE;
        if !self.allows_deferred() {
            // G-Buffer を使わない（前方描画）。AO・反射・SSGI はデファードの上に乗るので一緒に止まる。
            caps.deferred = Some(false);
            caps.ao = Some(AoMode::Off);
            caps.reflection = Some(ReflectionMode::Off);
        }
        if !self.allocates_shadows() {
            caps.shadows = Some(false);
        }
        if !self.allocates_gi() {
            caps.gi = Some(GiMode::Flat);
        }
        if !self.requests_ray_tracing() {
            // レイトレーシングを求めないので、半透明は通常のラスタへ（RT は GPU の可否でも降格するが明示する）。
            caps.translucency = Some(TranslucencyMode::Raster);
        }
        if !self.scene_3d {
            // 水面・水中のコースティクスは 3D の水があるときだけの処理。
            caps.water_reflection = Some(false);
            caps.water_caustics = Some(false);
        }
        if !self.post {
            caps.bloom = Some(false);
            caps.fxaa = Some(false);
            caps.vignette = Some(false);
        }
        caps
    }
}

// ============================================================
//  JSON の値から旗へ
// ============================================================

/// JSON のオブジェクト（キーと値の組）の旗を `flags` へ重ねる【純関数】。
///
/// `skip_keys` のキー（構成の名前 `profile` など、旗でないもの）は読み飛ばす。読めないキー・値は
/// その旗だけを捨て、理由を `warnings` へ足す（`context` は警告の頭に付ける出どころ）。
pub fn overlay_flags(
    flags: &mut RenderProfileFlags,
    object: &serde_json::Map<String, Value>,
    skip_keys: &[&str],
    context: &str,
    warnings: &mut Vec<String>,
) {
    for (key, value) in object {
        if skip_keys.contains(&key.as_str()) {
            continue;
        }
        if let Err(reason) = apply_flag(flags, key, value) {
            warnings.push(format!("{context}: {reason}"));
        }
    }
}

/// 旗を 1 つ書き込む【純関数】。
///
/// 真偽の旗は JSON の true / false と、文字列の "true" / "false"（起動オプションの `キー=値` から来る）を読む。
///
/// # 戻り値
/// 読めなければ理由（知らないキー・型の違い・知らない名前）。
pub fn apply_flag(flags: &mut RenderProfileFlags, key: &str, value: &Value) -> Result<(), String> {
    match key {
        KEY_SCENE_3D => flags.scene_3d = read_bool(key, value)?,
        KEY_DEFERRED => flags.deferred = read_bool(key, value)?,
        KEY_SHADOWS => flags.shadows = read_bool(key, value)?,
        KEY_GI => flags.gi = read_bool(key, value)?,
        KEY_BINDLESS => flags.bindless = read_bool(key, value)?,
        KEY_RAY_TRACING => flags.ray_tracing = read_bool(key, value)?,
        KEY_POST => flags.post = read_bool(key, value)?,
        KEY_PICKING => flags.picking = read_bool(key, value)?,
        KEY_MEMORY_HINT => {
            let text = value
                .as_str()
                .ok_or_else(|| format!("{key} は文字列（{MEMORY_HINT_PERFORMANCE} / {MEMORY_HINT_MEMORY_USAGE}）で指定してください（{value}）"))?;
            flags.memory_hint = MemoryHint::parse(text).ok_or_else(|| {
                format!("{key} の値 '{text}' は知りません（{MEMORY_HINT_PERFORMANCE} / {MEMORY_HINT_MEMORY_USAGE}）")
            })?;
        }
        _ => {
            return Err(format!(
                "知らない旗 {key} を読み飛ばしました（使えるのは {}）",
                KNOWN_FLAG_KEYS.join(" / ")
            ))
        }
    }
    Ok(())
}

/// 真偽を読む（JSON の真偽か、文字列の "true" / "false"）。
fn read_bool(key: &str, value: &Value) -> Result<bool, String> {
    match value {
        Value::Bool(b) => Ok(*b),
        Value::String(text) => match text.trim().to_ascii_lowercase().as_str() {
            "true" => Ok(true),
            "false" => Ok(false),
            _ => Err(format!("{key} は true / false で指定してください（{text:?}）")),
        },
        other => Err(format!("{key} は true / false で指定してください（{other}）")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 既定は full（すべて用意する・performance）で、何も止めない＝品質へ上限を当てない。
    #[test]
    fn default_is_full_and_caps_nothing() {
        let flags = RenderProfileFlags::default();
        assert!(flags.is_full());
        assert_eq!(flags.memory_hint, MemoryHint::Performance);
        assert!(flags.quality_caps().is_empty(), "full は品質に何も当てない（従来どおり）");
    }

    /// 旗は JSON の真偽・文字列の真偽・memory_hint の名前を読み、読めないものは警告して捨てる。
    #[test]
    fn overlay_reads_bools_and_hint() {
        let json: Value = serde_json::from_str(
            r#"{"profile":"ui","shadows":false,"post":"false","memory_hint":"Memory_Usage","gi":3,"nope":true}"#,
        )
        .unwrap();
        let mut flags = RenderProfileFlags::default();
        let mut warnings = Vec::new();
        overlay_flags(&mut flags, json.as_object().unwrap(), &["profile"], "test", &mut warnings);
        assert!(!flags.shadows);
        assert!(!flags.post);
        assert_eq!(flags.memory_hint, MemoryHint::MemoryUsage);
        assert!(flags.gi, "読めない値（数）は捨てて元のまま");
        assert_eq!(warnings.len(), 2, "gi の型違いと知らない旗 nope: {warnings:?}");
    }

    /// 3D を描かない構成は、デファード・影・GI・RT 半透明・水を品質の上限で止める。後処理は post で止める。
    #[test]
    fn no_3d_caps_quality() {
        let flags = RenderProfileFlags { scene_3d: false, post: false, ..RenderProfileFlags::FULL };
        let caps = flags.quality_caps();
        assert_eq!(caps.deferred, Some(false));
        assert_eq!(caps.shadows, Some(false));
        assert_eq!(caps.gi, Some(GiMode::Flat));
        assert_eq!(caps.translucency, Some(TranslucencyMode::Raster));
        assert_eq!(caps.water_reflection, Some(false));
        assert_eq!(caps.bloom, Some(false));
        assert_eq!(caps.fxaa, Some(false));
        assert_eq!(caps.vignette, Some(false));
        assert_eq!(caps.render_scale, None, "描画スケールは変えない（画面の見た目を変えないため）");
    }

    /// scene_3d=false だけを書いても、3D だけの資源（影・GI・bindless・RT・デファード）はまとめて止まる。
    #[test]
    fn no_3d_implies_no_3d_resources() {
        let flags = RenderProfileFlags { scene_3d: false, ..RenderProfileFlags::FULL };
        assert!(!flags.allocates_shadows());
        assert!(!flags.allocates_gi());
        assert!(!flags.allocates_bindless());
        assert!(!flags.requests_ray_tracing());
        assert!(!flags.allows_deferred());
        let full = RenderProfileFlags::FULL;
        assert!(full.allocates_shadows() && full.allocates_gi() && full.allocates_bindless());
        assert!(full.requests_ray_tracing() && full.allows_deferred());
    }

    /// memory_hint は wgpu の指定へ対応する。
    #[test]
    fn memory_hint_maps_to_wgpu() {
        assert!(matches!(MemoryHint::Performance.to_wgpu(), wgpu::MemoryHints::Performance));
        assert!(matches!(MemoryHint::MemoryUsage.to_wgpu(), wgpu::MemoryHints::MemoryUsage));
        assert_eq!(MemoryHint::parse(" performance "), Some(MemoryHint::Performance));
        assert_eq!(MemoryHint::parse("low"), None);
    }
}
