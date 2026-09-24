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
//
//  Android 対応の全体像は docs/android.md を参照。
// ============================================================

/// プラットフォームが与える書き込み先（セーブ・キャッシュの置き場。起動時に 1 回設定する）。
pub mod paths;

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

    /// C# スクリプト（CLR）を実行できるか。
    ///
    /// false のときはスクリプトホスト（SEEDScripting.dll）を探さず、スクリプト無しで起動を続ける。
    /// Android は段階B（CoreCLR ランタイムの同梱）まで false。
    pub scripting_supported: bool,

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
}

/// デスクトップ（Windows）の特性。従来の SEED.exe の振る舞いそのもの。
pub const DESKTOP: PlatformTraits = PlatformTraits {
    app_sizes_window:      true,
    scripting_supported:   true,
    lifecycle_diag_log:    false,
    touch_supported:       false,
    touch_drives_mouse:    false,
    mouse_simulates_touch: true,
    key_remap:             key_remap::DESKTOP_KEY_REMAP,
};

/// Android の特性（段階0 の 1 枚絵 ＋ 段階A のタッチ入力・戻るキー）。
pub const ANDROID: PlatformTraits = PlatformTraits {
    app_sizes_window:      false,
    scripting_supported:   false,
    lifecycle_diag_log:    true,
    touch_supported:       true,
    touch_drives_mouse:    true,
    mouse_simulates_touch: false,
    key_remap:             key_remap::ANDROID_KEY_REMAP,
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
        assert!(DESKTOP.scripting_supported);
        assert!(!DESKTOP.lifecycle_diag_log);
        // キーの置き換えは Android だけ（PC のキー入力は従来どおり素通し）。
        assert!(DESKTOP.key_remap.is_empty());
        #[cfg(not(target_os = "android"))]
        assert_eq!(CURRENT, DESKTOP);
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
}
