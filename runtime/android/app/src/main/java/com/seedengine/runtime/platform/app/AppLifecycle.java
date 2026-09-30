// ============================================================
//  AppLifecycle.java — 前面・背面の知らせ（platform.resumed / platform.paused。メインプロセス。2026-10-01）
//
//  MainActivity の onResume / onPause を、スクリプトへのイベントにする（W3-5 で見つかった不足: 設定の画面から戻ったときに
//  権限を問い直す契機が無かった）。
//    onPause  → platform.paused  { count }                   … count は何回目の背面か（1 から）
//    onResume → platform.resumed { count, background_ms }    … count は何回目の前面への戻りか（1 から）・直前の onPause からの ms
//  規則:
//    - プロセスの最初の onResume（起動）では流さない（resumed は必ず paused の後に来る＝「戻った」だけを知らせる）。
//      MainActivity.onDestroy はプロセスを終えるので、1 つのプロセスに Activity は 1 つ（数はプロセスの中で数える）。
//    - どちらもメインプロセスの中で作る知らせ（seq 0。:seed_platform の記録を通らない）。SeedPlatform.emitLocalEvent が
//      nativeOnPlatformEvent でエンジンの箱へ積み、次のフレームでスクリプトの SEED.Events と PlatformEvents.OnEvent へ届く。
//      スクリプトの準備（最初のシーンの OnStart）の前に届いた分はエンジンが保持してから配る（既存の仕組み）。
//    - 背面にいる間は描画の面が無くフレームが回らないことが多いので、paused は背面にいる間には届かず、戻ったときに resumed の
//      直前に届くことがある（推論。実機で確かめる。保存などを paused に頼らない）。
//  MainActivity は onResume の最後（権限の結果・変化〈PermissionLifecycle〉の後）と onPause の最初に呼ぶ
//  （スクリプトが resumed を受けた時点で、戻ったときの権限のイベントはもう届いている）。
//  デスクトップの模擬は窓のフォーカスの出入り（runtime の desktop_sim/lifecycle_commands.rs）。
// ============================================================

package com.seedengine.runtime.platform.app;

import android.os.SystemClock;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.SeedPlatform;

import org.json.JSONObject;

/**
 * 前面・背面の知らせ（static のみ。MainActivity の UI スレッドだけが呼ぶ）。
 */
public final class AppLifecycle {

    private AppLifecycle() {
    }

    /** まだ onPause を見ていない印（{@link #pausedAtElapsedMs}）。 */
    private static final long NOT_PAUSED = -1L;

    /** 最初の onResume（起動）を見たか（見る前の onResume は流さない）。 */
    private static boolean startedOnce;

    /** 前面へ戻った回数（流した resumed の数）。 */
    private static long resumeCount;

    /** 前面を離れた回数（流した paused の数）。 */
    private static long pauseCount;

    /** 最後の onPause の時刻（SystemClock.elapsedRealtime。前面にいる間は NOT_PAUSED）。 */
    private static long pausedAtElapsedMs = NOT_PAUSED;

    /**
     * 前面に来た（MainActivity.onResume の最後）。起動の最初の 1 回は覚えるだけ、以後は platform.resumed を流す。
     * 失敗（想定外の例外）は Activity の寿命の処理を止めないよう、ログに残して飲み込む（PermissionLifecycle と同じ方針）。
     */
    public static void onResumed() {
        try {
            if (!startedOnce) {
                startedOnce = true;
                pausedAtElapsedMs = NOT_PAUSED;
                return;
            }
            resumeCount++;
            long now = SystemClock.elapsedRealtime();
            // onPause を見ていない（onResume が続いた。ふつうは起きない）ときは 0
            long backgroundMs = pausedAtElapsedMs == NOT_PAUSED ? 0L : Math.max(0L, now - pausedAtElapsedMs);
            pausedAtElapsedMs = NOT_PAUSED;
            JSONObject data = new JSONObject();
            PlatformJson.put(data, PlatformContract.KEY_APP_LIFECYCLE_COUNT, resumeCount);
            PlatformJson.put(data, PlatformContract.KEY_APP_BACKGROUND_MS, backgroundMs);
            SeedPlatform.emitLocalEvent(PlatformContract.EVENT_APP_RESUMED, data);
            Log.i(PlatformContract.LOG_TAG, "前面へ戻りました（" + resumeCount + " 回目・背面 " + backgroundMs + " ms）");
        } catch (RuntimeException e) {
            Log.e(PlatformContract.LOG_TAG, "前面への復帰の知らせに失敗しました（Activity は続けます）", e);
        }
    }

    /**
     * 前面を離れる（MainActivity.onPause の最初）。platform.paused を流す。
     * 失敗（想定外の例外）はログに残して飲み込む。
     */
    public static void onPaused() {
        try {
            // onPause は onResume の後にしか来ないが、念のため「起動を見た」にして次の onResume を resumed として流す（組を崩さない）
            startedOnce = true;
            pauseCount++;
            pausedAtElapsedMs = SystemClock.elapsedRealtime();
            JSONObject data = new JSONObject();
            PlatformJson.put(data, PlatformContract.KEY_APP_LIFECYCLE_COUNT, pauseCount);
            SeedPlatform.emitLocalEvent(PlatformContract.EVENT_APP_PAUSED, data);
            Log.i(PlatformContract.LOG_TAG, "前面を離れました（" + pauseCount + " 回目）");
        } catch (RuntimeException e) {
            Log.e(PlatformContract.LOG_TAG, "前面を離れる知らせに失敗しました（Activity は続けます）", e);
        }
    }
}
