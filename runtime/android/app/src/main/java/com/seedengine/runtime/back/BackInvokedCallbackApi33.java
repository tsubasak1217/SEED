// ============================================================
//  BackInvokedCallbackApi33.java — Android 13（API 33）の戻るのコールバック（W2 の手直し P1-3）
//
//  API 33 の OnBackInvokedCallback には確定（onBackInvoked）しか無い（進み具合の OnBackAnimationCallback は API 34 から。
//  ローカルの SDK の android-36/data/api-versions.xml で確かめた）。確定を BackGestureReporter へ渡すだけ
//  （番号は確定で 1 増え、platform.back_invoked と合成の KEYCODE_BACK になる）。
//  このクラスは android.window.OnBackInvokedCallback（API 33）を実装するので、API 33 以上の端末でだけ作る
//  （BackDispatcherApi33 が SDK_INT で分ける。34 以上は BackAnimationCallbackApi34）。
// ============================================================

package com.seedengine.runtime.back;

import android.os.Build;
import android.window.OnBackInvokedCallback;

import androidx.annotation.RequiresApi;

/**
 * API 33 の戻るのコールバック（UI スレッドで呼ばれる）。
 */
@RequiresApi(Build.VERSION_CODES.TIRAMISU)
final class BackInvokedCallbackApi33 implements OnBackInvokedCallback {

    /** 知らせる先。 */
    private final BackGestureReporter reporter;

    /**
     * 作る（API 33 以上の端末でだけ呼ぶこと）。
     *
     * @param reporter 知らせる先
     */
    BackInvokedCallbackApi33(BackGestureReporter reporter) {
        this.reporter = reporter;
    }

    /** 戻るが確定した。 */
    @Override
    public void onBackInvoked() {
        reporter.onInvoked();
    }
}
