// ============================================================
//  BackAnimationCallbackApi34.java — Android 14（API 34）以上の戻るのコールバック（進み具合つき。W2 の手直し P1-3）
//
//  OnBackAnimationCallback（API 34。onBackStarted / onBackProgressed / onBackCancelled は default メソッド）を実装し、
//  BackEvent（API 34）の値を android.window の型を持たない値にして BackGestureReporter へ渡す:
//    progress … BackEvent.getProgress()（0〜1）
//    edge     … BackEvent.getSwipeEdge()：EDGE_LEFT → "left"、EDGE_RIGHT → "right"、それ以外（API 36 の EDGE_NONE〈ボタンの戻る〉と
//                知らない値）→ "none"（PlatformContract.BACK_EDGE_*）
//    touch    … BackEvent.getTouchX() / getTouchY()（窓の座標の px。ボタンの戻るでは NaN の見込み〈記憶による〉。Reporter が 0 にする）
//  ボタンの戻る（3 ボタンのナビゲーション・adb の input keyevent 4）でも、システムはキーの down で onBackStarted（進み具合 0）、
//  up で onBackInvoked を呼ぶ見込み（記憶による。実機で確かめる）。
//  このクラスは API 34 の型を実装するので、API 34 以上の端末でだけ作る（BackDispatcherApi33 が SDK_INT で分け、
//  作るのは create の static メソッドを通す＝API 33 の端末でこのクラスを読み込まない）。
// ============================================================

package com.seedengine.runtime.back;

import android.os.Build;
import android.window.BackEvent;
import android.window.OnBackAnimationCallback;
import android.window.OnBackInvokedCallback;

import androidx.annotation.RequiresApi;

import com.seedengine.runtime.platform.PlatformContract;

/**
 * API 34 以上の戻るのコールバック（UI スレッドで呼ばれる）。
 */
@RequiresApi(Build.VERSION_CODES.UPSIDE_DOWN_CAKE)
final class BackAnimationCallbackApi34 implements OnBackAnimationCallback {

    /** 知らせる先。 */
    private final BackGestureReporter reporter;

    /**
     * 作る。
     *
     * @param reporter 知らせる先
     */
    private BackAnimationCallbackApi34(BackGestureReporter reporter) {
        this.reporter = reporter;
    }

    /**
     * 作る（API 34 以上の端末でだけ呼ぶこと）。戻り値を API 33 の型にして、呼び手（BackDispatcherApi33）が API 34 の型を持たないようにする。
     *
     * @param reporter 知らせる先
     * @return コールバック
     */
    static OnBackInvokedCallback create(BackGestureReporter reporter) {
        return new BackAnimationCallbackApi34(reporter);
    }

    /** 手ぶりが始まった。 */
    @Override
    public void onBackStarted(BackEvent backEvent) {
        reporter.onStarted(backEvent.getProgress(), edgeName(backEvent.getSwipeEdge()), backEvent.getTouchX(), backEvent.getTouchY());
    }

    /** 手ぶりが進んだ（毎フレーム）。 */
    @Override
    public void onBackProgressed(BackEvent backEvent) {
        reporter.onProgressed(backEvent.getProgress(), edgeName(backEvent.getSwipeEdge()), backEvent.getTouchX(), backEvent.getTouchY());
    }

    /** 手ぶりが取り消された（指を戻した・キーが取り消された）。 */
    @Override
    public void onBackCancelled() {
        reporter.onCancelled();
    }

    /** 戻るが確定した。 */
    @Override
    public void onBackInvoked() {
        reporter.onInvoked();
    }

    /**
     * BackEvent の端を wire の語にする。
     *
     * @param swipeEdge BackEvent.getSwipeEdge()
     * @return "left" / "right" / "none"
     */
    static String edgeName(int swipeEdge) {
        if (swipeEdge == BackEvent.EDGE_LEFT) {
            return PlatformContract.BACK_EDGE_LEFT;
        }
        if (swipeEdge == BackEvent.EDGE_RIGHT) {
            return PlatformContract.BACK_EDGE_RIGHT;
        }
        // API 36 の EDGE_NONE（ボタンの戻る）と、将来の知らない値
        return PlatformContract.BACK_EDGE_NONE;
    }
}
