// ============================================================
//  RingWakeLock.java — 鳴動の間 CPU を起こしておく部分ウェイクロック（PARTIAL_WAKE_LOCK。:seed_platform。W1-4a）
//
//  【なぜ受信機でも取るか】AlarmManager が配信の間だけ持つウェイクロックは AlarmReceiver.onReceive が返ると外れる。
//  RingService の onStartCommand は onReceive の直後に同じスレッドで走るが、その間に端末が眠らないよう、受信機が
//  前景サービスを起こした直後に「引き継ぎ」の短い時間（HANDOVER_TIMEOUT_MS）で取り、サービスが鳴動の長さで取り直す
//  （参照を数えないウェイクロックなので、acquire のたびに時間切れが上書きされる）。
//  必ず時間切れを付ける（止め忘れても電池を使い続けない。鳴動は安全弁＋余裕で切れる）。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import android.content.Context;
import android.os.PowerManager;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;

/**
 * 鳴動のウェイクロック（static のみ。プロセスで 1 つ）。
 */
final class RingWakeLock {

    private RingWakeLock() {
    }

    /** ウェイクロックの名前（dumpsys power に出る。「アプリの名前:用途」の慣例）。 */
    private static final String TAG = "seedplatform:ring";

    /** 受信機からサービスへの引き継ぎの間の時間切れ（ミリ秒。onStartCommand までの余裕を大きく見る）。 */
    static final long HANDOVER_TIMEOUT_MS = 30_000L;

    /** 鳴動の安全弁の時刻に足す余裕（ミリ秒。安全弁の処理が終わるまで起こしておく）。 */
    static final long DEADLINE_MARGIN_MS = 60_000L;

    /** ウェイクロック（最初に要るときに作る）。 */
    private static PowerManager.WakeLock lock;

    /**
     * 取る（取っていれば時間切れを付け直す）。
     *
     * @param context   どの Context でもよい
     * @param timeoutMs 時間切れ（ミリ秒）
     */
    static synchronized void acquire(Context context, long timeoutMs) {
        if (lock == null) {
            PowerManager power = context.getApplicationContext().getSystemService(PowerManager.class);
            if (power == null) {
                Log.w(PlatformContract.LOG_TAG, "PowerManager を取れないので、鳴動のウェイクロックを取りません");
                return;
            }
            lock = power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, TAG);
            // 参照を数えない: acquire は時間切れの付け直し、release は 1 回で外れる
            lock.setReferenceCounted(false);
        }
        lock.acquire(timeoutMs);
    }

    /** 外す（取っていなければ何もしない）。 */
    static synchronized void release() {
        if (lock != null && lock.isHeld()) {
            lock.release();
        }
    }
}
