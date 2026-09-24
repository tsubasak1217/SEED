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
//  Android 対応の全体像は docs/android.md を参照。
// ============================================================

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
    /// デスクトップでは出さない（エディタの Output パネルを埋めないため）。
    pub lifecycle_diag_log: bool,
}

/// デスクトップ（Windows）の特性。従来の SEED.exe の振る舞いそのもの。
pub const DESKTOP: PlatformTraits = PlatformTraits {
    app_sizes_window:    true,
    scripting_supported: true,
    lifecycle_diag_log:  false,
};

/// Android の特性（段階0: 実機/エミュレータに 1 枚絵を出すスパイク時点）。
pub const ANDROID: PlatformTraits = PlatformTraits {
    app_sizes_window:    false,
    scripting_supported: false,
    lifecycle_diag_log:  true,
};

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
        #[cfg(not(target_os = "android"))]
        assert_eq!(CURRENT, DESKTOP);
    }
}
