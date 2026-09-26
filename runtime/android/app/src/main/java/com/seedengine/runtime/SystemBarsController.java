// ============================================================
//  SystemBarsController.java — システムバー（ステータスバー・ナビゲーションバー）の既定の出し方（W1-2）
//
//  【役割】
//  プロジェクト設定 android.system_bars（"hidden"＝既定・ゲーム向け／"visible"＝アプリ向け）を APK のリソース
//  R.bool.seed_system_bars_visible から読み、Activity の窓へ当てる。値は SeedAndroid がビルドのたびに生成する
//  app/src/seedFeatures/res/values/seed_platform.xml（無ければ main の res/values/seed_platform_defaults.xml の false）。
//    hidden  … バーを隠す。端からのスワイプで一時的に出せ、フォーカスが戻ったら隠し直す（従来のゲームの振る舞い）
//    visible … テーマの全画面（windowFullscreen）を外してバーを出したままにする。描画面はエッジツーエッジ（targetSdk 35 以降の強制）
//              のままバーの裏まで広がるので、バーの分は安全領域（ScreenReporter が WindowInsets の systemBars を足して
//              nativeOnScreenChanged で渡す。スクリプトの Screen.SafeArea）で避ける
//  スクリプトから実行中に切り替える API（Window.SetSystemBarsVisible）は W1-6。docs/android.md §25.10。
//
//  UI スレッド専用（MainActivity の onCreate・onWindowFocusChanged から呼ぶ）。
// ============================================================

package com.seedengine.runtime;

import android.app.Activity;
import android.view.Window;
import android.view.WindowManager;

import androidx.core.view.WindowCompat;
import androidx.core.view.WindowInsetsCompat;
import androidx.core.view.WindowInsetsControllerCompat;

/**
 * システムバーの既定の出し方を窓へ当てる（MainActivity から使う）。
 */
final class SystemBarsController {

    /** 当てる先の Activity。 */
    private final Activity activity;

    /** バーを出したままにするか（プロジェクト設定 android.system_bars が "visible"）。 */
    private final boolean visible;

    /**
     * リソースから既定を読む（MainActivity.onCreate の super.onCreate の後に作る）。
     *
     * @param activity 当てる先の Activity（MainActivity）
     */
    SystemBarsController(Activity activity) {
        this.activity = activity;
        this.visible = activity.getResources().getBoolean(R.bool.seed_system_bars_visible);
    }

    /** @return バーを出したままにする設定か（ログ・診断用） */
    boolean isVisibleByDefault() {
        return visible;
    }

    /** 既定の出し方を当てる（MainActivity.onCreate で 1 回）。 */
    void applyDefault() {
        if (visible) {
            showBars();
        } else {
            hideBars();
        }
    }

    /**
     * フォーカスが戻った（通知の引き下ろし等の後）。隠す設定なら、スワイプで一時的に出たバーを隠し直す。
     * 出す設定では何もしない（バーは出たまま）。
     */
    void onFocusRegained() {
        if (!visible) {
            hideBars();
        }
    }

    /** ステータスバー・ナビゲーションバーを隠す（端からのスワイプで一時的に出せる）。 */
    private void hideBars() {
        WindowInsetsControllerCompat controller = insetsController();
        controller.setSystemBarsBehavior(WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
        controller.hide(WindowInsetsCompat.Type.systemBars());
    }

    /**
     * ステータスバー・ナビゲーションバーを出す。テーマ（Theme.SeedRuntime）の android:windowFullscreen が窓に付ける
     * FLAG_FULLSCREEN がステータスバーを隠すので、先に外してから出す。
     */
    private void showBars() {
        activity.getWindow().clearFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
        insetsController().show(WindowInsetsCompat.Type.systemBars());
    }

    /** 窓の WindowInsets の制御（androidx の互換版。API 29 でも同じ呼び方）。 */
    private WindowInsetsControllerCompat insetsController() {
        Window window = activity.getWindow();
        return WindowCompat.getInsetsController(window, window.getDecorView());
    }
}
