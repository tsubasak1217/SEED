// ============================================================
//  BackGestureReporter.java — 戻るの手ぶりをエンジンへ知らせる（番号・イベント・合成の戻るキー。W2 の手直し P1-3）
//
//  API ごとのコールバック（BackAnimationCallbackApi34・BackInvokedCallbackApi33）から呼ばれ、android.window の型を持たない
//  値（進み具合・端の語・指の位置）でイベントを作る（API 33 未満の端末でもこのクラスは読み込める）。
//
//  【イベント】（SeedPlatform.emitLocalEvent。seq 0。名前と欄は PlatformContract の *BACK*。Rust の wire::app と一致させる）
//    started   … platform.back_started   { gesture, progress, edge, touch_x, touch_y }（API 34 以上）
//    progressed … platform.back_progressed { gesture, progress, edge, touch_x, touch_y }（API 34 以上。毎フレーム）
//    cancelled … platform.back_cancelled { gesture }（API 34 以上）
//    invoked   … platform.back_invoked   { gesture }（API 33 以上）を流してから、合成の KEYCODE_BACK（BackKeyInjector）を渡す
//                 （スクリプトには Escape で届く。知らせとキーは別の道で届くので、同じフレームとは限らない）
//  【番号の決まり】（デスクトップの模擬 desktop_sim/back_state.rs と同じ）
//    started は番号を 1 増やして手ぶりの途中にする。progressed・cancelled は今の番号（cancelled は途中を終える）。
//    invoked は途中でなければ 1 増やす（API 33 は started が無い。ボタンの戻るでも started の無いことがありうる）。
//  【値の整え】進み具合は 0〜1 へそろえる。指の位置は有限でなければ 0（ボタンの戻るでは NaN になる見込み〈記憶による〉。
//    org.json は NaN を入れられず例外になり、UI スレッドのコールバックでアプリが落ちるため）。
//  【ログ】始まり・取り消し・確定を 1 行ずつ（進み具合は件数だけを終わりの行に添える。毎フレームは出さない）。
// ============================================================

package com.seedengine.runtime.back;

import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.SeedPlatform;

import org.json.JSONObject;

/**
 * 戻るの手ぶりの知らせ（UI スレッド専用。BackCallbackController が 1 つ作り、コールバックが呼ぶ）。
 */
final class BackGestureReporter {

    /** 進み具合の下限（BackEvent.getProgress の範囲の下端）。 */
    private static final float MIN_PROGRESS = 0f;

    /** 進み具合の上限（BackEvent.getProgress の範囲の上端）。 */
    private static final float MAX_PROGRESS = 1f;

    /** 指の位置が取れないとき（有限でない値）に入れる値（Rust の模擬と同じ 0）。 */
    private static final float UNKNOWN_TOUCH_POSITION = 0f;

    /** 確定のときに合成の KEYCODE_BACK を渡す係。 */
    private final BackKeyInjector keyInjector;

    /** 最後の手ぶりの通し番号（まだ無ければ 0。最初の手ぶりが 1）。 */
    private long gesture;

    /** 手ぶりの途中か（started の後、cancelled・invoked の前）。 */
    private boolean inProgress;

    /** 今の手ぶりで流した progressed の件数（終わりのログに添える）。 */
    private long progressedCount;

    /**
     * 作る。
     *
     * @param keyInjector 確定のときに合成の KEYCODE_BACK を渡す係
     */
    BackGestureReporter(BackKeyInjector keyInjector) {
        this.keyInjector = keyInjector;
    }

    /**
     * 手ぶりが始まった（API 34 以上の onBackStarted）。
     *
     * @param progress 進み具合（0〜1）
     * @param edge     端の語（PlatformContract.BACK_EDGE_*）
     * @param touchX   指の x（窓の座標の px。取れなければ NaN）
     * @param touchY   指の y（同じ）
     */
    void onStarted(float progress, String edge, float touchX, float touchY) {
        gesture++;
        inProgress = true;
        progressedCount = 0;
        SeedPlatform.emitLocalEvent(PlatformContract.EVENT_BACK_STARTED, backEventData(progress, edge, touchX, touchY));
        Log.i(BackCallbackController.LOG_TAG, BackCallbackController.LOG_PREFIX + "手ぶりの始まり gesture=" + gesture + " edge=" + edge
                + " progress=" + progress);
    }

    /**
     * 手ぶりが進んだ（API 34 以上の onBackProgressed。毎フレーム来るのでログは出さない）。
     *
     * @param progress 進み具合（0〜1）
     * @param edge     端の語
     * @param touchX   指の x
     * @param touchY   指の y
     */
    void onProgressed(float progress, String edge, float touchX, float touchY) {
        progressedCount++;
        SeedPlatform.emitLocalEvent(PlatformContract.EVENT_BACK_PROGRESSED, backEventData(progress, edge, touchX, touchY));
    }

    /** 手ぶりが取り消された（API 34 以上の onBackCancelled）。 */
    void onCancelled() {
        inProgress = false;
        SeedPlatform.emitLocalEvent(PlatformContract.EVENT_BACK_CANCELLED, gestureData());
        Log.i(BackCallbackController.LOG_TAG, BackCallbackController.LOG_PREFIX + "取り消し gesture=" + gesture
                + "（進み具合 " + progressedCount + " 件）");
    }

    /** 戻るが確定した（API 33 以上の onBackInvoked）。知らせを流してから、合成の KEYCODE_BACK を渡す。 */
    void onInvoked() {
        if (!inProgress) {
            // started の無い確定（API 33・ボタンの戻る）は新しい手ぶり
            gesture++;
            progressedCount = 0;
        }
        inProgress = false;
        SeedPlatform.emitLocalEvent(PlatformContract.EVENT_BACK_INVOKED, gestureData());
        boolean delivered = keyInjector.injectBack();
        Log.i(BackCallbackController.LOG_TAG, BackCallbackController.LOG_PREFIX + "確定 gesture=" + gesture
                + "（進み具合 " + progressedCount + " 件）→ 合成の KEYCODE_BACK" + (delivered ? " → Escape" : " を渡せませんでした"));
    }

    /**
     * started・progressed の data（{ gesture, progress, edge, touch_x, touch_y }）。
     */
    private JSONObject backEventData(float progress, String edge, float touchX, float touchY) {
        JSONObject data = gestureData();
        PlatformJson.put(data, PlatformContract.KEY_BACK_PROGRESS, clampProgress(progress));
        PlatformJson.put(data, PlatformContract.KEY_BACK_EDGE, edge);
        PlatformJson.put(data, PlatformContract.KEY_BACK_TOUCH_X, finiteOrUnknown(touchX));
        PlatformJson.put(data, PlatformContract.KEY_BACK_TOUCH_Y, finiteOrUnknown(touchY));
        return data;
    }

    /** cancelled・invoked の data（{ gesture }。started・progressed の data の土台）。 */
    private JSONObject gestureData() {
        JSONObject data = new JSONObject();
        PlatformJson.put(data, PlatformContract.KEY_BACK_GESTURE, gesture);
        return data;
    }

    /** 進み具合を 0〜1 へそろえる（有限でなければ 0）。 */
    private static float clampProgress(float progress) {
        if (!Float.isFinite(progress)) {
            return MIN_PROGRESS;
        }
        return Math.max(MIN_PROGRESS, Math.min(MAX_PROGRESS, progress));
    }

    /** 指の位置（有限でなければ 0。ボタンの戻るの NaN を JSON に入れないため）。 */
    private static float finiteOrUnknown(float position) {
        return Float.isFinite(position) ? position : UNKNOWN_TOUCH_POSITION;
    }
}
