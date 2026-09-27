// ============================================================
//  LeftoverVolumeNudge.java — 前面に来たときに、戻せずに残した鳴動の音量を :seed_platform に戻させる（メインプロセス。W1-7）
//
//  【なぜ】鳴動中に :seed_platform が強制停止・再起動で途切れると、force_volume で下げたアラームの音量（STREAM_ALARM）は
//  次に :seed_platform が起きたときの後始末（RingRecovery.onStartup）で戻す。ところが Android 17（Pixel 6a）は、前景サービスも
//  見える画面も無いプロセスからの setStreamVolume を AudioHardening が無視した（W1-7 の実機。受信機だけで起きたプロセス）。
//  戻せなかった控え（ringing.json の volume の欄）は残り、次の鳴動か、アプリが前面にいる間の :seed_platform への呼び出し
//  （PlatformProvider.call の入口。AlarmStartup.onMainProcessCall）で戻る。エンジンのスクリプトが SEED.Platform を使わないと
//  呼び出しが無いので、ここで MainActivity.onResume（アプリが前面＝同じ uid が前面）のときに軽い呼び出しを 1 つ送る。
//
//  【すること】端末保護ストレージの seed_platform/ringing.json が有るとき（:seed_platform の鳴動の控えが残っている＝機能 alarm の APK）
//  だけ、背面のスレッドで ContentResolver.call（platform.version。答えは使わない）を 1 回送る。
//  PlatformConnection（呼び鈴の登録）は使わない: エンジンが読み込まれる前に :seed_platform の記録を取り出して捨てないため。
//  ファイルの有無を見るだけで、書かない・フォルダも作らない（書き手は :seed_platform だけ）。
// ============================================================

package com.seedengine.runtime.platform;

import android.content.Context;
import android.util.Log;

import com.seedengine.runtime.platform.service.PlatformStorage;

import java.io.File;

/**
 * 残した音量の後始末の催促（static のみ。UI スレッドから呼ぶ）。
 */
public final class LeftoverVolumeNudge {

    private LeftoverVolumeNudge() {
    }

    /** 催促のスレッドの名前。 */
    private static final String THREAD_NAME = "SEEDVolumeNudge";

    /** 催促に使う :seed_platform の命令（答えを使わない軽いもの）。 */
    private static final String NUDGE_METHOD = PlatformContract.MODULE_PLATFORM + PlatformContract.METHOD_SEPARATOR
            + PlatformContract.METHOD_VERSION;

    /** ContentResolver.call の arg（使わない）。 */
    private static final String NO_ARG = null;

    /**
     * 前面に来た（MainActivity.onResume）。鳴動の控えが残っていれば :seed_platform へ軽い呼び出しを送る。すぐ返る。
     *
     * @param context どの Context でもよい（アプリの Context を使う）
     */
    public static void onHostResumed(Context context) {
        Context app = context.getApplicationContext();
        File ringing = new File(new File(PlatformStorage.deviceProtectedFilesDir(app), PlatformContract.STORAGE_DIR_NAME),
                PlatformStorage.RINGING_FILE_NAME);
        if (!ringing.isFile()) {
            return;
        }
        new Thread(() -> nudge(app), THREAD_NAME).start();
    }

    /** :seed_platform へ 1 回呼ぶ（背面のスレッド。失敗はログだけ）。 */
    private static void nudge(Context app) {
        try {
            app.getContentResolver().call(PlatformContract.authority(app), NUDGE_METHOD, NO_ARG, null);
            Log.i(PlatformContract.LOG_TAG, "鳴動の控えが残っていたので、:seed_platform に後始末を促しました");
        } catch (RuntimeException e) {
            // プロバイダが無い（機能 alarm の無い APK は ringing.json も無いので来ない）・:seed_platform が起きられない等
            Log.w(PlatformContract.LOG_TAG, ":seed_platform に鳴動の後始末を促せませんでした: " + PlatformJson.describe(e));
        }
    }
}
