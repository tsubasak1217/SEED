// ============================================================
//  SystemBarsController.java — システムバー（ステータスバー・ナビゲーションバー）の出し方（W1-2 の既定・W1-6 の実行中の切り替え）
//
//  【役割】
//  プロジェクト設定 android.system_bars（"hidden"＝既定・ゲーム向け／"visible"＝アプリ向け）を APK のリソース
//  R.bool.seed_system_bars_visible から読み、起動時の既定として Activity の窓へ当てる。値は SeedAndroid がビルドのたびに生成する
//  app/src/seedFeatures/res/values/seed_platform.xml（無ければ main の res/values/seed_platform_defaults.xml の false）。
//  W1-6 から、スクリプトの Window.SetSystemBarsVisible（window.set_system_bars_visible → local/SystemBarsVisibleCommand →
//  MainActivity.setSystemBarsVisible）で実行中に切り替えられ、以後はその状態が「今の出し方」になる（既定は起動時だけの初期値）。
//    隠す … バーを隠す。端からのスワイプで一時的に出せ、フォーカスが戻ったら隠し直す（従来のゲームの振る舞い）。テーマの
//            android:windowFullscreen と同じ FLAG_FULLSCREEN も付ける（起動時の隠した状態と、出した後に隠した状態をそろえる）
//    出す   … FLAG_FULLSCREEN を外してバーを出したままにする。描画面はエッジツーエッジ（targetSdk 35 以降の強制）のままバーの裏まで
//            広がるので、バーの分は安全領域（ScreenReporter が WindowInsets の systemBars を足して nativeOnScreenChanged で渡す。
//            スクリプトの Screen.SafeArea）で避ける。切り替えると WindowInsets が配り直され、MainActivity.onApplyWindowInsets →
//            ScreenReporter.reportAfterLayout が安全領域を知らせ直す（docs/android.md §25.15）
//  docs/android.md §25.10.4・§25.15。
//
//  UI スレッド専用（MainActivity の onCreate・onWindowFocusChanged・setSystemBarsVisible から呼ぶ）。
// ============================================================

package com.seedengine.runtime;

import android.app.Activity;
import android.view.Window;
import android.view.WindowManager;

import androidx.core.view.WindowCompat;
import androidx.core.view.WindowInsetsCompat;
import androidx.core.view.WindowInsetsControllerCompat;

/**
 * システムバーの出し方を窓へ当てる（MainActivity から使う）。
 */
final class SystemBarsController {

    /** 当てる先の Activity。 */
    private final Activity activity;

    /** 起動時の既定でバーを出したままにするか（プロジェクト設定 android.system_bars が "visible"）。 */
    private final boolean visibleByDefault;

    /** 今の出し方（起動時は既定。Window.SetSystemBarsVisible で変わる。UI スレッドだけが読み書きする）。 */
    private boolean visible;

    /**
     * リソースから既定を読む（MainActivity.onCreate の super.onCreate の後に作る）。
     *
     * @param activity 当てる先の Activity（MainActivity）
     */
    SystemBarsController(Activity activity) {
        this.activity = activity;
        this.visibleByDefault = activity.getResources().getBoolean(R.bool.seed_system_bars_visible);
        this.visible = visibleByDefault;
    }

    /** @return 起動時の既定でバーを出したままにする設定か（ログ・診断用） */
    boolean isVisibleByDefault() {
        return visibleByDefault;
    }

    /** 既定の出し方を当てる（MainActivity.onCreate で 1 回）。 */
    void applyDefault() {
        applyCurrent();
    }

    /**
     * 実行中に出し方を切り替え、以後の状態にする（W1-6。スクリプトの Window.SetSystemBarsVisible）。
     *
     * @param show 出すなら true、隠すなら false
     */
    void setVisible(boolean show) {
        visible = show;
        applyCurrent();
    }

    /**
     * フォーカスが戻った（通知の引き下ろし等の後）。今の状態が「隠す」なら、スワイプで一時的に出たバーを隠し直す。
     * 「出す」では何もしない（バーは出たまま）。
     */
    void onFocusRegained() {
        if (!visible) {
            hideBars();
        }
    }

    /** 今の状態を窓へ当てる。 */
    private void applyCurrent() {
        if (visible) {
            showBars();
        } else {
            hideBars();
        }
    }

    /** ステータスバー・ナビゲーションバーを隠す（端からのスワイプで一時的に出せる）。 */
    private void hideBars() {
        setFullscreenFlag(true);
        WindowInsetsControllerCompat controller = insetsController();
        controller.setSystemBarsBehavior(WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
        controller.hide(WindowInsetsCompat.Type.systemBars());
    }

    /**
     * ステータスバー・ナビゲーションバーを出す。テーマ（Theme.SeedRuntime）の android:windowFullscreen が窓に付ける
     * FLAG_FULLSCREEN がステータスバーを隠すので、先に外してから出す。
     */
    private void showBars() {
        setFullscreenFlag(false);
        insetsController().show(WindowInsetsCompat.Type.systemBars());
    }

    /**
     * 窓の FLAG_FULLSCREEN（テーマの android:windowFullscreen が付けるもの）を出し入れする。今と同じなら触らない
     * （窓の属性を書き換えるとレイアウトし直しになるので、フォーカスが戻るたびに書かない）。
     * FLAG_FULLSCREEN は API 30 で非推奨（WindowInsetsController が後継）だが、テーマが付けるこの印を外さないと API 30 以降でも
     * ステータスバーが出ないので、非推奨の注意を抑えて使う。
     *
     * @param on 付けるなら true
     */
    @SuppressWarnings("deprecation")
    private void setFullscreenFlag(boolean on) {
        Window window = activity.getWindow();
        int fullscreen = WindowManager.LayoutParams.FLAG_FULLSCREEN;
        boolean current = (window.getAttributes().flags & fullscreen) != 0;
        if (current == on) {
            return;
        }
        if (on) {
            window.addFlags(fullscreen);
        } else {
            window.clearFlags(fullscreen);
        }
    }

    /** 窓の WindowInsets の制御（androidx の互換版。API 29 でも同じ呼び方）。 */
    private WindowInsetsControllerCompat insetsController() {
        Window window = activity.getWindow();
        return WindowCompat.getInsetsController(window, window.getDecorView());
    }
}
