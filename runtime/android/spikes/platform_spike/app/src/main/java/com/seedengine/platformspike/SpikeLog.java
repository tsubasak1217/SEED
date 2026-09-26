package com.seedengine.platformspike;

import android.app.Application;
import android.os.Process;
import android.os.SystemClock;
import android.util.Log;

/**
 * 計測の目印を logcat へ出す（タグ SEEDPlatformSpike）。
 *
 * <p>スクリプトは「MARK &lt;名前&gt;」の行を拾う。wall は UTC の epoch ミリ秒（予定時刻と比べる）、
 * rt は SystemClock.elapsedRealtime（端末全体で共通。プロセス間の差を取れる）。</p>
 */
final class SpikeLog {
    static final String TAG = "SEEDPlatformSpike";

    private SpikeLog() {}

    /** 目印を 1 行出す。details は「key=value」を空白で並べたもの。 */
    static void mark(String name, String details) {
        Log.i(TAG, "MARK " + name
                + " wall=" + System.currentTimeMillis()
                + " rt=" + SystemClock.elapsedRealtime()
                + " pid=" + Process.myPid()
                + " proc=" + Application.getProcessName()
                + (details == null || details.isEmpty() ? "" : " " + details));
    }

    static void i(String message) {
        Log.i(TAG, message);
    }

    static void w(String message, Throwable error) {
        Log.w(TAG, message, error);
    }

    /** 例外を 1 行の文字列にする（ログの 1 行に収めて grep しやすくする）。 */
    static String oneLine(Throwable error) {
        String text = error.getClass().getName() + ": " + error.getMessage();
        return text.replace('\n', ' ').replace(' ', '_');
    }
}
