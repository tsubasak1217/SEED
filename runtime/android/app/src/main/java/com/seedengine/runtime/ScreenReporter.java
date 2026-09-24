// ============================================================
//  ScreenReporter.java — 安全領域と画面の回転をネイティブ（エンジン）へ知らせる
//
//  【役割】
//  スクリプトの SEED.Screen.SafeArea / Orientation の源になる OS の値を集め、JNI で libSEED.so へ渡す。
//    ・安全領域 … WindowInsets のシステムバー（systemBars）と画面の切り欠き（displayCutout）の和に、
//                 隠れている間のナビゲーションバー（ジェスチャーバー）の範囲を加えたもの（理由は SAFE_AREA_* の説明）。
//                 窓（ウィンドウ）基準の値を、描画面（GameActivity の SurfaceView）の各辺からの距離へ直して渡す
//    ・回転     … Display.getRotation（自然な向きからの 90 度単位の回転。0..3）と、
//                 表示の自然な向きの大きさ（Display.Mode の physicalWidth / Height）
//  あわせて「そのときの描画面の大きさ」も渡す。エンジンは今のサーフェスと大きさが一致する報告だけを使う
//  （回転の直後は、この報告と描画面の大きさの変化が前後して届くため。platform/screen/report.rs）。
//
//  【契機】MainActivity の onApplyWindowInsets / onGlobalLayout / onConfigurationChanged と、
//  表示（Display）の変化の通知（DisplayListener）。180 度の回転は大きさも WindowInsets も変わらないことがあり、
//  表示の変化の通知でしか気付けないため。どれも UI スレッドで、前回と同じ値なら何もしない。
//  回転の通知の時点ではまだレイアウトが前の向きのまま（描画面の大きさ・WindowInsets が古い）ことがあるので、
//  回転の通知からの報告は「描画面の縦横が回転後の表示の縦横と合っているとき」だけ行う（合っていなければ
//  直後のレイアウト完了 onGlobalLayout が報告する）。
//  全体像は docs/android.md「画面の向きと安全領域」。
// ============================================================

package com.seedengine.runtime;

import android.app.Activity;
import android.hardware.display.DisplayManager;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.view.Display;
import android.view.View;

import androidx.core.graphics.Insets;
import androidx.core.view.ViewCompat;
import androidx.core.view.WindowInsetsCompat;

import java.util.Arrays;

/**
 * 安全領域と画面の回転を集めてネイティブへ渡す（MainActivity から使う）。
 *
 * <p>UI スレッド専用（状態を同期なしで持つ）。{@link #attach} してから {@link #detach} するまで働く。</p>
 */
final class ScreenReporter implements DisplayManager.DisplayListener {

    /** logcat のタグ（ネイティブ側と同じ）。 */
    private static final String LOG_TAG = "SEED";

    /**
     * 安全領域に数える、見えている間だけ効く WindowInsets の種類（システムバーと画面の切り欠きの和）。
     * getInsets に種類の和を渡すと、辺ごとに各種類の最大値が返る。
     * 没入モード（全画面）ではシステムバーが隠れているので、実際に効くのは切り欠きだけになる。
     */
    private static final int SAFE_AREA_VISIBLE_TYPES =
            WindowInsetsCompat.Type.systemBars() | WindowInsetsCompat.Type.displayCutout();

    /**
     * 隠れていても安全領域から外す WindowInsets の種類（ナビゲーションバー＝ジェスチャーバー・3 ボタン）。
     * 没入モードでもこの辺をなぞるとまずシステムがバーを出す（ゲームの操作として届かない）うえ、
     * 出てきたバーは画面の上に重なるため、iOS のホームインジケータと同じく常に避ける範囲として扱う。
     * ステータスバーは隠れている間は数えない（横持ちの上端がまるごと使えなくなるのを避ける）。
     */
    private static final int SAFE_AREA_HIDDEN_TYPES = WindowInsetsCompat.Type.navigationBars();

    /** 窓内の位置（getLocationInWindow の結果）の要素数。 */
    private static final int LOCATION_LENGTH = 2;

    /** 表示が取れないときの回転（Surface.ROTATION_0）。 */
    private static final int ROTATION_UNKNOWN = 0;

    /** 1 周（360 度）を 90 度単位で数えたときの、横倒し（90 / 270 度）の判定に使う割る数。 */
    private static final int QUARTER_TURNS_PER_HALF_TURN = 2;

    /** 報告する Activity（回転の取得・表示の変化の購読に使う）。 */
    private final Activity activity;

    /** 描画面（GameActivity の SurfaceView）。attach 前・detach 後は null。 */
    private View surfaceView;

    /** 前回ネイティブへ渡した値（同じ値を繰り返し渡さないため）。まだ渡していなければ null。 */
    private int[] lastReport;

    /**
     * 描画面の大きさ・安全領域・回転をネイティブへ渡す（libSEED.so の jni_exports.rs）。
     *
     * @param frameWidth    描画面（SurfaceView）の幅（物理ピクセル）
     * @param frameHeight   描画面の高さ（物理ピクセル）
     * @param insetLeft     安全領域の、描画面の左端からの距離（物理ピクセル）
     * @param insetTop      上端からの距離
     * @param insetRight    右端からの距離
     * @param insetBottom   下端からの距離
     * @param rotation      Display.getRotation（0..3）
     * @param naturalWidth  表示の自然な向き（回転 0）の幅（Display.Mode の physicalWidth）
     * @param naturalHeight 表示の自然な向きの高さ（Display.Mode の physicalHeight）
     */
    private static native void nativeOnScreenChanged(
            int frameWidth, int frameHeight,
            int insetLeft, int insetTop, int insetRight, int insetBottom,
            int rotation, int naturalWidth, int naturalHeight);

    /** @param activity 報告する Activity（MainActivity） */
    ScreenReporter(Activity activity) {
        this.activity = activity;
    }

    /**
     * 描画面を覚え、表示の変化（回転）の購読を始める（MainActivity.onCreate の super の後に呼ぶ）。
     *
     * @param view 描画面（GameActivity の SurfaceView）
     */
    void attach(View view) {
        surfaceView = view;
        DisplayManager displays = activity.getSystemService(DisplayManager.class);
        if (displays != null) {
            // UI スレッドの Looper で受ける（報告は UI スレッド専用）。
            displays.registerDisplayListener(this, new Handler(Looper.getMainLooper()));
        }
    }

    /** 購読をやめ、以降は何も報告しない（MainActivity.onDestroy で呼ぶ）。 */
    void detach() {
        DisplayManager displays = activity.getSystemService(DisplayManager.class);
        if (displays != null) {
            displays.unregisterDisplayListener(this);
        }
        surfaceView = null;
    }

    /**
     * レイアウトが確定してから報告する（次の UI ループで {@link #report} を呼ぶ）。
     * WindowInsets の配信の時点では、描画面の大きさがまだ前の値のことがあるため。
     */
    void reportAfterLayout() {
        View view = surfaceView;
        if (view != null) {
            view.post(this::report);
        }
    }

    /**
     * 回転（構成変更・表示の変化）の通知から報告する。レイアウトがまだ前の向きのままなら何もしない
     * （描画面の大きさと WindowInsets が古く、新しい回転と組み合わせると食い違った報告になるため。
     * レイアウトが済めば onGlobalLayout から報告される）。
     */
    void reportAfterRotation() {
        View view = surfaceView;
        if (view != null) {
            view.post(() -> {
                if (layoutMatchesDisplay(view)) {
                    report();
                }
            });
        }
    }

    /** 今の描画面の大きさ・安全領域・回転を集め、前回と違えばネイティブへ渡す（レイアウト完了後に呼ぶ）。 */
    void report() {
        View view = surfaceView;
        if (view == null || !view.isAttachedToWindow()) {
            return;
        }
        int width = view.getWidth();
        int height = view.getHeight();
        WindowInsetsCompat insets = ViewCompat.getRootWindowInsets(view);
        Display display = currentDisplay();
        if (width <= 0 || height <= 0 || insets == null || display == null) {
            return;
        }

        // 窓基準の安全領域（窓の各辺からの距離）を、描画面の各辺からの距離へ直す。
        // 描画面は窓いっぱい（全画面）なので通常は同じ値になるが、窓内の位置と大きさから求めておく。
        Insets safe = Insets.max(
                insets.getInsets(SAFE_AREA_VISIBLE_TYPES),
                insets.getInsetsIgnoringVisibility(SAFE_AREA_HIDDEN_TYPES));
        int[] origin = new int[LOCATION_LENGTH];
        view.getLocationInWindow(origin);
        View window = view.getRootView();
        int windowRight = window.getWidth() - safe.right;
        int windowBottom = window.getHeight() - safe.bottom;
        int left = clamp(safe.left - origin[0], width);
        int top = clamp(safe.top - origin[1], height);
        int right = clamp(origin[0] + width - windowRight, width);
        int bottom = clamp(origin[1] + height - windowBottom, height);
        int rotation = display.getRotation();
        Display.Mode mode = display.getMode();
        int naturalWidth = mode.getPhysicalWidth();
        int naturalHeight = mode.getPhysicalHeight();

        int[] report = {width, height, left, top, right, bottom, rotation, naturalWidth, naturalHeight};
        if (Arrays.equals(report, lastReport)) {
            return;
        }
        lastReport = report;
        Log.i(LOG_TAG, "[SEED SCREEN] Java 報告: frame=" + width + "x" + height
                + " insets=(" + left + "," + top + "," + right + "," + bottom + ")"
                + " rotation=" + rotation + " natural=" + naturalWidth + "x" + naturalHeight
                + " 内訳{" + describeInsetTypes(insets) + "}");
        try {
            nativeOnScreenChanged(width, height, left, top, right, bottom, rotation, naturalWidth, naturalHeight);
        } catch (UnsatisfiedLinkError e) {
            // 古い libSEED.so（関数が無い）でも Activity は動かし続ける（安全領域は全画面扱いになる）。
            Log.w(LOG_TAG, "nativeOnScreenChanged を呼べませんでした: " + e);
        }
    }

    // ── DisplayManager.DisplayListener ─────────────────────────────

    /** 表示が増えた（何もしない）。 */
    @Override
    public void onDisplayAdded(int displayId) {
    }

    /** 表示が消えた（何もしない）。 */
    @Override
    public void onDisplayRemoved(int displayId) {
    }

    /** 表示が変わった（回転を含む）。この Activity の表示なら報告し直す。 */
    @Override
    public void onDisplayChanged(int displayId) {
        Display display = currentDisplay();
        if (display != null && display.getDisplayId() == displayId) {
            reportAfterRotation();
        }
    }

    // ── 補助 ────────────────────────────────────────────────────

    /** この Activity が表示されている表示（API 30 以降は Activity.getDisplay、API 29 は既定の表示）。 */
    @SuppressWarnings("deprecation")
    private Display currentDisplay() {
        return Build.VERSION.SDK_INT >= Build.VERSION_CODES.R
                ? activity.getDisplay()
                : activity.getWindowManager().getDefaultDisplay();
    }

    /**
     * 描画面の縦横（縦長か横長か）が、今の回転での表示の縦横と合っているか。
     * 回転の通知の直後でまだレイアウトが前の向きのままなら false。
     */
    private boolean layoutMatchesDisplay(View view) {
        Display display = currentDisplay();
        if (display == null) {
            return false;
        }
        Display.Mode mode = display.getMode();
        boolean sideways = display.getRotation() % QUARTER_TURNS_PER_HALF_TURN != 0;
        int displayWidth = sideways ? mode.getPhysicalHeight() : mode.getPhysicalWidth();
        int displayHeight = sideways ? mode.getPhysicalWidth() : mode.getPhysicalHeight();
        boolean displayIsLandscape = displayWidth > displayHeight;
        boolean viewIsLandscape = view.getWidth() > view.getHeight();
        return displayIsLandscape == viewIsLandscape;
    }

    /** 距離を 0 以上・描画面の大きさ以下に収める（描画面の外にある切り欠き・バーの分を落とす）。 */
    private static int clamp(int value, int limit) {
        return Math.max(0, Math.min(value, limit));
    }

    /**
     * 診断用の内訳（窓基準の各種類の距離）。
     * 見えているバー（getInsets）と、隠れていても出ればかかる範囲（getInsetsIgnoringVisibility）を並べる。
     */
    private static String describeInsetTypes(WindowInsetsCompat insets) {
        return "cutout=" + format(insets.getInsets(WindowInsetsCompat.Type.displayCutout()))
                + " bars=" + format(insets.getInsets(WindowInsetsCompat.Type.systemBars()))
                + " navIgnoringVisibility="
                + format(insets.getInsetsIgnoringVisibility(WindowInsetsCompat.Type.navigationBars()))
                + " statusIgnoringVisibility="
                + format(insets.getInsetsIgnoringVisibility(WindowInsetsCompat.Type.statusBars()));
    }

    /** Insets を「(左,上,右,下)」の文字列にする。 */
    private static String format(Insets insets) {
        return "(" + insets.left + "," + insets.top + "," + insets.right + "," + insets.bottom + ")";
    }
}
