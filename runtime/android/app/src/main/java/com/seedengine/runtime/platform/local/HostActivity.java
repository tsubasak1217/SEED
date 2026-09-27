// ============================================================
//  HostActivity.java — メインプロセスで答える命令が使う Activity（MainActivity）とアプリの Context の置き場（W1-4a・W1-5）
//
//  SeedPlatform.init（MainActivity.onCreate）が登録する。Activity を強く持たない（WeakReference。破棄の後に残さない）。
//  MainActivity は onDestroy でプロセスごと終わる（android.md §14.2）ので、登録は実質プロセスで 1 回。
//  アプリの Context（W1-5。権限の状態の問い合わせは Activity が無くてもできる）は Application なので強く持ってよい。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.app.Activity;
import android.content.Context;

import java.lang.ref.WeakReference;

/**
 * 画面・権限の命令が使う Activity とアプリの Context（static のみ）。
 */
public final class HostActivity {

    private HostActivity() {
    }

    /** 登録された Activity（無ければ中身が null）。 */
    private static volatile WeakReference<Activity> current = new WeakReference<>(null);

    /** アプリの Context（登録の前は null）。 */
    private static volatile Context application;

    /**
     * 登録する（MainActivity.onCreate から。後の登録が勝つ）。
     *
     * @param activity MainActivity
     */
    public static void attach(Activity activity) {
        current = new WeakReference<>(activity);
        application = activity.getApplicationContext();
    }

    /**
     * 登録された Activity（破棄されていれば null）。
     *
     * @return Activity
     */
    static Activity get() {
        Activity activity = current.get();
        return activity != null && !activity.isDestroyed() ? activity : null;
    }

    /**
     * アプリの Context（W1-5。Activity が破棄された後も使える。登録の前は null）。
     *
     * @return アプリの Context
     */
    static Context applicationContext() {
        return application;
    }
}
