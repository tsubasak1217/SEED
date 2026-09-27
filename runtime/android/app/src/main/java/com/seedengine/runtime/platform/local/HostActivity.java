// ============================================================
//  HostActivity.java — メインプロセスで答える画面の命令が操作する Activity（MainActivity）の置き場（W1-4a）
//
//  SeedPlatform.init（MainActivity.onCreate）が登録する。Activity を強く持たない（WeakReference。破棄の後に残さない）。
//  MainActivity は onDestroy でプロセスごと終わる（android.md §14.2）ので、登録は実質プロセスで 1 回。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.app.Activity;

import java.lang.ref.WeakReference;

/**
 * 画面の命令が操作する Activity（static のみ）。
 */
public final class HostActivity {

    private HostActivity() {
    }

    /** 登録された Activity（無ければ中身が null）。 */
    private static volatile WeakReference<Activity> current = new WeakReference<>(null);

    /**
     * 登録する（MainActivity.onCreate から。後の登録が勝つ）。
     *
     * @param activity MainActivity
     */
    public static void attach(Activity activity) {
        current = new WeakReference<>(activity);
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
}
