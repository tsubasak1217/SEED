// ============================================================
//  platform/mod.rs — 実行プラットフォームの特性表
//
//  【役割】
//  OS ごとの「できること・方針」の差を、コード中の cfg 分岐として散らさず
//  1 つの表（PlatformTraits）にまとめて参照できるようにする（データドリブン）。
//  新しいプラットフォームを足すときは、ここへ定数を 1 つ足して CURRENT の選択に加える。
//
//  【cfg 分岐との使い分け】
//  - そもそもコンパイルできない API（Win32 など）を使う箇所 → 従来どおり #[cfg]
//  - コンパイルはできるが振る舞いを変えたい箇所          → この表のフラグで分岐
//
//  【実行時にしか分からない値】
//  この表はコンパイル時定数。Android のアプリ専用フォルダのように OS が実行時にだけ教える値は
//  paths.rs に「起動時に 1 回だけ設定する値」として持つ（cfg 分岐を散らさないのは同じ）。
//  安全領域・画面の向きのように実行中に何度も変わる値は screen/ に持つ。
//  音声フォーカス（他のアプリとの音の譲り合い）の最新の状態は audio_focus.rs に持つ。
//  OS が起動のときに渡す起動オプション（Android の Intent の extras。起動するシーン）の読み方は launch_options.rs。
//
//  Android 対応の全体像は docs/android.md を参照。
// ============================================================

/// OS の音声フォーカスの最新の報告（Android の糊が JNI で書き、App がフレームごとに読む）。
pub mod audio_focus;
/// OS が起動のときに渡す起動オプション（Android の Intent の extras。起動するシーン。段階C-3）。
pub mod launch_options;
/// プラットフォームが与える書き込み先（セーブ・キャッシュの置き場。起動時に 1 回設定する）。
pub mod paths;
/// 実行時に更新される画面情報（安全領域・画面の向き・DPI。OS の報告とスクリプトへ見せる写し）。
pub mod screen;

use crate::engine::core::input::key_remap::{self, KeyRemapRule};

/// 実行プラットフォームの特性（能力と方針）。
///
/// 値はビルド時に決まる定数で、実行中に変わらない。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PlatformTraits {
    /// アプリがウィンドウの大きさを決められるか。
    ///
    /// false のプラットフォーム（Android）では、ウィンドウ＝端末の画面で大きさは OS が決める。
    /// このときプロジェクト設定の `window_width` / `window_height` はウィンドウ生成に使わず、
    /// 描画は常にサーフェスの実サイズで行う（内部解像度固定モードの基準解像度としては引き続き使う）。
    pub app_sizes_window: bool,

    /// C# スクリプト（CLR）をどこから用意するか。
    ///
    /// 起動材料（`LaunchArgs.embedded_clr`）が渡されていれば、どのプラットフォームでもそれを使う
    /// （同梱 .NET。`core/scripting/clr_host/embedded.rs`）。渡されていないときの振る舞いをここで決める。
    pub script_host_source: ScriptHostSource,

    /// サーフェス・リサイズ・タッチのライフサイクル診断ログを標準エラーへ出すか。
    ///
    /// Android 段階0 の実機検証（回転・バックグラウンド復帰・タッチ受信の確認）用。
    /// 段階A からはタッチ状態とタッチ由来のマウス状態のフレーム単位ログ（`app/touch_diag.rs`）も出す。
    /// デスクトップでは出さない（エディタの Output パネルを埋めないため）。
    pub lifecycle_diag_log: bool,

    /// タッチ入力を主に使う端末か（スクリプトの `Input.TouchSupported`）。
    ///
    /// プラットフォーム単位の値。タッチパネル付き PC でも実タッチは届く（TouchState に載る）が、
    /// デスクトップはマウスが主なので false のまま。
    pub touch_supported: bool,

    /// 指0（他に触れている指が無い状態で触れ始めた指）が MouseState（カーソル座標＋左ボタン）を
    /// 駆動するか（`input/touch/bridge.rs`）。
    ///
    /// true の端末（Android）は OS からマウスイベントが来ないため、これで既存のキャンバス UI の
    /// ポインタイベントやスクリプトのマウス API がタッチで動く。`mouse_simulates_touch` と排他。
    pub touch_drives_mouse: bool,

    /// マウス左ボタンで指を 1 本合成するか（`input/touch/bridge.rs`）。
    ///
    /// true の端末（デスクトップ）では、PC の Play でも `Input.GetTouch` を使うスクリプトを試せる。
    /// `touch_drives_mouse` と排他（両方 true だと同じ操作がマウスとタッチを往復して二重になる）。
    pub mouse_simulates_touch: bool,

    /// OS 固有のキーをエンジンの KeyCode へ置き換える表（`core/input/key_remap.rs`）。
    ///
    /// キー入力を入力状態へ入れる直前（`app/event_handler.rs` の on_keyboard_input）で引く。
    /// Android は戻るキー → Escape（Unity と同じ）。デスクトップは空（従来どおり何も置き換えない）。
    pub key_remap: &'static [KeyRemapRule],

    /// 表示倍率 1.0 に当たる DPI（スクリプトの `Screen.DPI` = winit の scale_factor × この値）。
    ///
    /// winit の scale_factor は OS の論理密度を基準値で割ったもの: Windows は 96 dpi を 1.0、
    /// Android は mdpi（160 dpi）を 1.0（densityDpi / 160）とする。掛け戻すと OS が報告する論理 DPI になる
    /// （Android は DisplayMetrics.densityDpi、Windows は 96 × 表示スケール）。
    pub reference_dpi: u32,

    /// 描画品質の設定を引くときのプラットフォーム名（段階D-2）。
    ///
    /// project_settings.json の `render_quality.<この名前>`（プリセット名とつまみの上書き）を読む。
    /// エディタのプロジェクト設定（レンダリング品質）の節の名前と一致させる。
    pub quality_platform_key: &'static str,

    /// 描画品質プリセットの既定（project_settings.json に指定が無いとき。段階D-2）。
    ///
    /// プリセットの中身は runtime/config/render_presets.json（renderer/quality/）。
    /// デスクトップは何も下げない `desktop`（従来の描画そのもの）、Android は軽量の `mobile`。
    pub default_render_quality: &'static str,
}

/// 描画品質の設定の節の名前（デスクトップ＝Windows）。
pub const DESKTOP_QUALITY_KEY: &str = "desktop";

/// 描画品質の設定の節の名前（Android）。
pub const ANDROID_QUALITY_KEY: &str = "android";

/// デスクトップの既定の描画品質プリセット（何も下げない）。
pub const DESKTOP_DEFAULT_RENDER_QUALITY: &str = "desktop";

/// Android の既定の描画品質プリセット（軽量）。
pub const ANDROID_DEFAULT_RENDER_QUALITY: &str = "mobile";

/// C# スクリプトのホスト（CLR と SEEDScripting.dll）の用意の仕方（起動材料が渡されていないとき）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ScriptHostSource {
    /// ファイルを探す（PC）: 開発ビルド出力か実行ファイルの bin/ の SEEDScripting.dll を、
    /// インストール済み（または bin/dotnet に同梱）の .NET で起動する（`ScriptingHost::load`）。
    SearchFiles,
    /// 同梱 .NET だけを使う（Android）: 糊が APK から用意した起動材料（`LaunchArgs.embedded_clr`）が
    /// 無ければスクリプト無しで起動する（探しても見つかる場所が無く、誤った案内が出るだけなので探さない）。
    EmbeddedOnly,
}

/// Windows の論理 DPI の基準（表示スケール 100% = 96 dpi）。
pub const DESKTOP_REFERENCE_DPI: u32 = 96;

/// Android の論理 DPI の基準（mdpi = 160 dpi。DisplayMetrics.DENSITY_DEFAULT）。
pub const ANDROID_REFERENCE_DPI: u32 = 160;

/// デスクトップ（Windows）の特性。従来の SEED.exe の振る舞いそのもの。
pub const DESKTOP: PlatformTraits = PlatformTraits {
    app_sizes_window:      true,
    script_host_source:    ScriptHostSource::SearchFiles,
    lifecycle_diag_log:    false,
    touch_supported:       false,
    touch_drives_mouse:    false,
    mouse_simulates_touch: true,
    key_remap:             key_remap::DESKTOP_KEY_REMAP,
    reference_dpi:         DESKTOP_REFERENCE_DPI,
    quality_platform_key:  DESKTOP_QUALITY_KEY,
    default_render_quality: DESKTOP_DEFAULT_RENDER_QUALITY,
};

/// Android の特性（段階0 の 1 枚絵 ＋ 段階A のタッチ入力・戻るキー・画面情報 ＋ 段階B の同梱 .NET のスクリプト）。
pub const ANDROID: PlatformTraits = PlatformTraits {
    app_sizes_window:      false,
    script_host_source:    ScriptHostSource::EmbeddedOnly,
    lifecycle_diag_log:    true,
    touch_supported:       true,
    touch_drives_mouse:    true,
    mouse_simulates_touch: false,
    key_remap:             key_remap::ANDROID_KEY_REMAP,
    reference_dpi:         ANDROID_REFERENCE_DPI,
    quality_platform_key:  ANDROID_QUALITY_KEY,
    default_render_quality: ANDROID_DEFAULT_RENDER_QUALITY,
};

/// 定義済みの全プラットフォームの特性（表全体への検査用）。
pub const ALL: [PlatformTraits; 2] = [DESKTOP, ANDROID];

/// このビルドが動くプラットフォームの特性。
pub const CURRENT: PlatformTraits = if cfg!(target_os = "android") {
    ANDROID
} else {
    DESKTOP
};

#[cfg(test)]
mod tests {
    use super::*;

    /// デスクトップ（テストを走らせるホスト）の特性は従来の SEED.exe と同じであること。
    /// ここが崩れると Windows 版の振る舞い（ウィンドウ解像度・スクリプト実行・ログ量）が変わる。
    #[test]
    fn desktop_traits_keep_existing_behavior() {
        assert!(DESKTOP.app_sizes_window);
        assert_eq!(DESKTOP.script_host_source, ScriptHostSource::SearchFiles);
        assert!(!DESKTOP.lifecycle_diag_log);
        // キーの置き換えは Android だけ（PC のキー入力は従来どおり素通し）。
        assert!(DESKTOP.key_remap.is_empty());
        #[cfg(not(target_os = "android"))]
        assert_eq!(CURRENT, DESKTOP);
    }

    /// Android のスクリプトは同梱 .NET（APK から展開したもの）だけを使い、PC の探索経路は使わない。
    #[test]
    fn android_uses_embedded_runtime_only() {
        assert_eq!(ANDROID.script_host_source, ScriptHostSource::EmbeddedOnly);
    }

    /// Android は戻るキーを Escape へ置き換える表を使う（Unity と同じ対応）。
    #[test]
    fn android_remaps_back_key_to_escape() {
        use winit::keyboard::{KeyCode, NativeKeyCode, PhysicalKey};
        let back = PhysicalKey::Unidentified(NativeKeyCode::Android(key_remap::ANDROID_KEYCODE_BACK));
        assert_eq!(
            key_remap::remap_physical_key(ANDROID.key_remap, back),
            PhysicalKey::Code(KeyCode::Escape)
        );
    }

    /// マウス ⇔ タッチの相互変換は、どのプラットフォームでも片方向だけ（二重駆動の防止）。
    #[test]
    fn pointer_conversion_directions_are_exclusive() {
        for traits in ALL {
            assert!(
                !(traits.touch_drives_mouse && traits.mouse_simulates_touch),
                "touch_drives_mouse と mouse_simulates_touch は排他: {traits:?}"
            );
        }
    }

    /// タッチ関連の既定: PC はマウス＝指、Android は指0＝マウス。
    #[test]
    fn touch_traits_per_platform() {
        assert!(!DESKTOP.touch_supported);
        assert!(DESKTOP.mouse_simulates_touch);
        assert!(!DESKTOP.touch_drives_mouse);
        assert!(ANDROID.touch_supported);
        assert!(ANDROID.touch_drives_mouse);
        assert!(!ANDROID.mouse_simulates_touch);
    }

    /// 描画品質の既定: デスクトップは何も下げない desktop、Android は軽量の mobile（段階D-2）。
    /// 設定の節の名前はプラットフォームごとに別（同じ project_settings.json に両方を書ける）。
    #[test]
    fn render_quality_defaults_per_platform() {
        assert_eq!(DESKTOP.default_render_quality, "desktop");
        assert_eq!(ANDROID.default_render_quality, "mobile");
        assert_eq!(DESKTOP.quality_platform_key, "desktop");
        assert_eq!(ANDROID.quality_platform_key, "android");
        assert_ne!(DESKTOP.quality_platform_key, ANDROID.quality_platform_key);
    }

    /// DPI の基準は winit の scale_factor の定義と同じ（Windows 96 / Android 160）。
    /// Android の Pixel 6（densityDpi 420 → scale_factor 2.625）が 420 に戻ること。
    #[test]
    fn reference_dpi_matches_winit_scale_factor_definition() {
        use screen::snapshot::dpi_from_scale_factor;
        assert_eq!(DESKTOP.reference_dpi, 96);
        assert_eq!(ANDROID.reference_dpi, 160);
        let pixel6 = dpi_from_scale_factor(Some(420.0 / 160.0), ANDROID.reference_dpi as f32);
        assert!((pixel6 - 420.0).abs() < 1e-3, "dpi={pixel6}");
    }
}
