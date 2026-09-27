// ============================================================
//  BootRearmGate.java — 同じ起動で重ねて届く BOOT_COMPLETED / LOCKED_BOOT_COMPLETED を見分ける（W1-7。W1-4b の G-7）
//
//  【なぜ】BootReceiver には 1 回の起動で LOCKED_BOOT_COMPLETED と BOOT_COMPLETED の 2 つが届き（ロック解除の前と後）、
//  Android 15+ は強制停止の後に停止状態から出たときにも届く（W1-4b の実機で `adb install -r` の後に 2 回ずつ計 4 回）。
//  張り直しは冪等なので実害は無いが、そのたびに alarms.rescheduled(boot) が記録され、アプリに何度も届いていた。
//
//  【見分け方】端末の起動の回数 Settings.Global.BOOT_COUNT（API 24+。アプリが読める）を、張り直しを済ませたときに
//  端末保護ストレージの boot_rearm.json へ書く（DurableFile）。次に届いた起動の放送の回数が同じなら「同じ起動の 2 回目以降」。
//  起動の回数が読めない端末では毎回「初めて」とみなす（前と同じ振る舞い）。
//  2 回目以降でも、BootReceiver は消えている予約だけを張り直す（強制停止で AlarmManager の予約が消えた後に届いた場合を
//  取りこぼさない）。記録は何かを張り直した・鳴らなかったときだけ（W1-7 の BootReceiver の説明）。
//  書き手は :seed_platform の BootReceiver（UI スレッド）だけ。このクラスの lock で直列にする。
// ============================================================

package com.seedengine.runtime.platform.service.alarm;

import android.content.Context;
import android.provider.Settings;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.service.DurableFile;
import com.seedengine.runtime.platform.service.PlatformStorage;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;

/**
 * 起動ごとの張り直しの印（static のみ）。
 */
final class BootRearmGate {

    private BootRearmGate() {
    }

    /** 起動の回数が読めないときの値。 */
    private static final int UNKNOWN_BOOT_COUNT = -1;

    /** 欄: 張り直しを済ませた起動の回数。 */
    private static final String KEY_BOOT_COUNT = "boot_count";

    /**
     * この起動で初めての起動の放送か（初めてなら印を書く）。
     *
     * @param context :seed_platform の Context
     * @return 初めて（または起動の回数が読めない）なら true。同じ起動の 2 回目以降なら false
     */
    static synchronized boolean firstForThisBoot(Context context) {
        int bootCount = Settings.Global.getInt(context.getContentResolver(), Settings.Global.BOOT_COUNT, UNKNOWN_BOOT_COUNT);
        if (bootCount == UNKNOWN_BOOT_COUNT) {
            return true;
        }
        if (readHandledBootCount(context) == bootCount) {
            return false;
        }
        JSONObject json = new JSONObject();
        PlatformJson.put(json, KEY_BOOT_COUNT, bootCount);
        try {
            DurableFile.writeAtomically(PlatformStorage.bootRearmFile(context), PlatformJson.utf8(json.toString()));
        } catch (IOException e) {
            // 書けなくても張り直しは続ける（次の放送も「初めて」になり、記録が重なるだけ）
            Log.w(PlatformContract.LOG_TAG, "張り直しの印を書けませんでした: " + PlatformJson.describe(e));
        }
        return true;
    }

    /** 張り直しを済ませた起動の回数（無い・読めなければ UNKNOWN_BOOT_COUNT）。 */
    private static int readHandledBootCount(Context context) {
        try {
            byte[] bytes = DurableFile.readOrNull(PlatformStorage.bootRearmFile(context));
            if (bytes == null) {
                return UNKNOWN_BOOT_COUNT;
            }
            return new JSONObject(PlatformJson.text(bytes)).optInt(KEY_BOOT_COUNT, UNKNOWN_BOOT_COUNT);
        } catch (IOException | JSONException e) {
            Log.w(PlatformContract.LOG_TAG, "張り直しの印を読めませんでした（初めてとして扱います）: " + PlatformJson.describe(e));
            return UNKNOWN_BOOT_COUNT;
        }
    }
}
