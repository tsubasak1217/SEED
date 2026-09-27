// ============================================================
//  AlarmStartup.java — :seed_platform のプロセスが起きたときの目覚ましの照合（W1-3 の持ち越し。W1-4a）
//
//  PlatformProvider.onCreate（:seed_platform の起動の最初。受信機・サービスより前に UI スレッドで呼ばれる）から呼ぶ。
//  AlarmBook.reconcile（控えの未来の予約のうち AlarmManager から消えているものを張り直し、過ぎた予約は alarm.missed(device_off)）を
//  **背面のスレッド**で行う。UI スレッドで行わないのは、目覚ましの配信でプロセスが起きたとき、この後の AlarmReceiver の
//  「真っ先に startForegroundService」（10 秒の一時許可の間）を控えの読み書き・fsync で遅らせないため。AlarmBook は
//  static synchronized なので、照合と発火は必ずどちらかが先に終わる（配信の途中の予約は照合が触らない。AlarmRearmPlan）。
//  APK に機能 alarm が無ければ何もしない（控えも受信機も無い）。
// ============================================================

package com.seedengine.runtime.platform.service.alarm;

import android.content.Context;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

/**
 * 起動時の照合（static のみ）。
 */
public final class AlarmStartup {

    private AlarmStartup() {
    }

    /** 照合のスレッドの名前。 */
    private static final String THREAD_NAME = "SEEDAlarmReconcile";

    /**
     * 照合を背面のスレッドで始める（すぐ返る）。
     *
     * @param context :seed_platform の Context（アプリの Context を使う）
     */
    public static void reconcileInBackground(Context context) {
        Context appContext = context.getApplicationContext() != null ? context.getApplicationContext() : context;
        Thread worker = new Thread(() -> reconcile(appContext), THREAD_NAME);
        worker.start();
    }

    /**
     * 照合する（背面のスレッド。例外はログだけ）。
     *
     * @param context アプリの Context
     */
    private static void reconcile(Context context) {
        try {
            if (!AlarmModule.isFeatureEnabled(context)) {
                return;
            }
            AlarmBook.RearmResult result = AlarmBook.reconcile(context, System.currentTimeMillis());
            if (result.stored > 0) {
                Log.i(PlatformContract.LOG_TAG, "起動時の照合（" + result + "。張り直しは消えていた予約だけ）");
            }
        } catch (RuntimeException e) {
            Log.e(PlatformContract.LOG_TAG, "起動時の照合の途中で例外: " + PlatformJson.describe(e), e);
        }
    }
}
