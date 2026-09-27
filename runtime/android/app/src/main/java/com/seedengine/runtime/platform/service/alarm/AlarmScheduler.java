// ============================================================
//  AlarmScheduler.java — AlarmManager.setAlarmClock での予約・取り消し（:seed_platform。W1-3）
//
//  【なぜ setAlarmClock か】「システムは配信時刻を調整しない。最も重要なアラームとして扱い、必要なら低電力モードを抜けて
//  配信する」（developer.android.com「Schedule alarms」）。Doze でも時刻どおりで、ステータスバーに目覚ましの印が出る。
//  正確なアラームが許可されていなければ予約せず exact_alarm_not_allowed を返す（黙って不正確な予約に落とさない。
//  Flutter 版の穴。docs/app_platform_roadmap.md §2.2）。
//
//  【PendingIntent の見分け方（要求コードの決め方）】
//  発火の PendingIntent は要求コードを 1 つの定数（REQUEST_CODE_ALARM_FIRE）に固定し、予約 ID ごとの区別は Intent の
//  data の URI（seedalarm://alarm/<ID を URI の規則で符号化>）で付ける。PendingIntent の同一性は「要求コード＋
//  Intent.filterEquals（action・data・部品など。extras は含まない）」で決まるので、同じ ID は同じ PendingIntent になり、
//  「同じ PendingIntent の予約は前の予約を置き換える」（公式の文書）で置き換えが成り立つ。取り消しも同じ要求コード・同じ
//  Intent で作り直して cancel する。ID の hashCode を要求コードにしない（衝突すると別の予約を消す。Flutter 版の教訓）。
//  extras には ID と予定時刻を入れる（予定時刻は「予約し直された後に届いた古い配信」を見分ける版の印。AlarmReceiver）。
//
//  【ステータスバーの印を押したとき（AlarmClockInfo の showIntent）】
//  W1-4a から、信頼できる起動の入口 activity-alias PlatformEntry 行き（起動理由 alarm_clock_info。PlatformEntryIntents）。
//  W1-3 はランチャーと同じ起動（MAIN/LAUNCHER）だった。部品は**名前の文字列**で指す:
//    ・MainActivity のクラスを参照すると static 初期化子が libSEED.so を読み込む（Java だけのこのプロセスで触ってはいけない）
//    ・getLaunchIntentForPackage は PackageManager で解決するので、再起動の後・最初のロック解除の前（BootReceiver の
//      LOCKED_BOOT_COMPLETED）は directBootAware でない MainActivity が見えず null になりうる（推論。Direct Boot の間は
//      端末保護ストレージを使える部品だけが解決される）。名前で作れば解決しないので、いつでも同じ PendingIntent になる
//  showIntent は全予約で 1 つを共有する（要求コードが同じ・FLAG_UPDATE_CURRENT）ので、予約ごとの値（ID など）は入れない。
// ============================================================

package com.seedengine.runtime.platform.service.alarm;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.service.PlatformEntryIntents;

import org.json.JSONObject;

/**
 * AlarmManager の操作（static のみ）。
 */
final class AlarmScheduler {

    private AlarmScheduler() {
    }

    /** 発火の Intent の action（AlarmReceiver がこれだけを受ける）。 */
    static final String ACTION_ALARM_FIRE = "com.seedengine.runtime.platform.action.ALARM_FIRE";

    /** 発火の Intent の extra: 予約の ID。 */
    static final String EXTRA_ALARM_ID = "com.seedengine.runtime.platform.extra.ALARM_ID";

    /** 発火の Intent の extra: 予約したときの予定時刻（UTC の epoch ミリ秒）。 */
    static final String EXTRA_TRIGGER_AT_UTC_MS = "com.seedengine.runtime.platform.extra.TRIGGER_AT_UTC_MS";

    /** extra に予定時刻が無いときの値。 */
    static final long UNKNOWN_TRIGGER_AT = -1L;

    /** 発火の Intent の data の URI の scheme（予約 ID ごとに PendingIntent を分けるため）。 */
    private static final String FIRE_URI_SCHEME = "seedalarm";

    /** 発火の Intent の data の URI の authority。 */
    private static final String FIRE_URI_AUTHORITY = "alarm";

    /** 発火の PendingIntent の要求コード（全予約で共通。区別は data の URI）。 */
    private static final int REQUEST_CODE_ALARM_FIRE = 0;

    /** 予約の結果（error が null なら成功）。 */
    static final class ArmResult {
        /** 失敗の理由（PlatformContract.ERROR_*。成功なら null）。 */
        final String error;
        /** 失敗の説明。 */
        final String detail;

        private ArmResult(String error, String detail) {
            this.error = error;
            this.detail = detail;
        }

        /** @return 成功なら true */
        boolean ok() {
            return error == null;
        }
    }

    /** 成功の結果（共有してよい）。 */
    private static final ArmResult ARMED = new ArmResult(null, null);

    /**
     * 正確なアラームを張れるか（Android 12 以上は canScheduleExactAlarms。11 以前は権限が要らないので true）。
     *
     * @param context どの Context でもよい
     * @return 張れるなら true
     */
    static boolean canScheduleExact(Context context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S) {
            return true;
        }
        AlarmManager alarms = context.getSystemService(AlarmManager.class);
        return alarms != null && alarms.canScheduleExactAlarms();
    }

    /**
     * 予約を張る（同じ ID の予約があれば置き換わる）。
     *
     * <p>setAlarmClock が失敗したとき、この呼び出しで初めて作った PendingIntent は取り消す（張れていない予約を
     * 「張ってある」と見せないため。isArmed が見る）。前から張ってあった場合は前の予約を生かすため取り消さない
     * （FLAG_UPDATE_CURRENT で extras の予定時刻だけは新しい値になるので、前の予約の配信は AlarmReceiver が
     * 「古い配信」として捨てうる。失敗するのは権限の取り消し〈前の予約も既に消えている〉か AlarmManager の件数の上限だけ）。</p>
     *
     * @param context :seed_platform の Context
     * @param entry   予約
     * @return 結果
     */
    static ArmResult arm(Context context, AlarmEntry entry) {
        AlarmManager alarms = context.getSystemService(AlarmManager.class);
        if (alarms == null) {
            return new ArmResult(PlatformContract.ERROR_SCHEDULE_FAILED, "AlarmManager を取れませんでした");
        }
        if (!canScheduleExact(context)) {
            return new ArmResult(PlatformContract.ERROR_EXACT_ALARM_NOT_ALLOWED, null);
        }
        boolean wasArmed = isArmed(context, entry.id);
        Intent fire = fireIntent(context, entry.id)
                .putExtra(EXTRA_ALARM_ID, entry.id)
                .putExtra(EXTRA_TRIGGER_AT_UTC_MS, entry.triggerAtUtcMs);
        PendingIntent operation = PendingIntent.getBroadcast(context, REQUEST_CODE_ALARM_FIRE, fire,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        try {
            alarms.setAlarmClock(new AlarmManager.AlarmClockInfo(entry.triggerAtUtcMs, showIntent(context)), operation);
            return ARMED;
        } catch (RuntimeException e) {
            // SecurityException（正確なアラームの権限が無い）・IllegalStateException（アプリごとの件数の上限）など
            if (!wasArmed) {
                operation.cancel();
            }
            return new ArmResult(PlatformContract.ERROR_SCHEDULE_FAILED, PlatformJson.describe(e));
        }
    }

    /**
     * 予約を外す（AlarmManager から取り消し、PendingIntent も取り消す）。
     *
     * @param context :seed_platform の Context
     * @param id      予約の ID
     * @return 張ってあった（PendingIntent があった）なら true
     */
    static boolean disarm(Context context, String id) {
        PendingIntent existing = existingFireIntent(context, id);
        if (existing == null) {
            return false;
        }
        AlarmManager alarms = context.getSystemService(AlarmManager.class);
        if (alarms != null) {
            alarms.cancel(existing);
        }
        existing.cancel();
        return true;
    }

    /**
     * この ID の発火の PendingIntent がシステムに残っているか（張ってある・配信の途中の目安。再起動・強制停止で消える）。
     *
     * @param context :seed_platform の Context
     * @param id      予約の ID
     * @return 残っていれば true
     */
    static boolean isArmed(Context context, String id) {
        return existingFireIntent(context, id) != null;
    }

    /**
     * 発火の Intent から予約の ID を取り出す（extra が無ければ data の URI の最後の区切り）。
     *
     * @param intent 発火の Intent
     * @return ID（取れなければ null）
     */
    static String alarmIdOf(Intent intent) {
        String id = intent.getStringExtra(EXTRA_ALARM_ID);
        if (id != null) {
            return id;
        }
        Uri data = intent.getData();
        return data != null ? data.getLastPathSegment() : null;
    }

    /** 発火の Intent（extras なし＝PendingIntent の同一性を決める部分だけ）。 */
    private static Intent fireIntent(Context context, String id) {
        Uri data = new Uri.Builder().scheme(FIRE_URI_SCHEME).authority(FIRE_URI_AUTHORITY).appendPath(id).build();
        return new Intent(context, AlarmReceiver.class).setAction(ACTION_ALARM_FIRE).setData(data);
    }

    /** 張ってある発火の PendingIntent（無ければ null。作らない）。 */
    private static PendingIntent existingFireIntent(Context context, String id) {
        return PendingIntent.getBroadcast(context, REQUEST_CODE_ALARM_FIRE, fireIntent(context, id),
                PendingIntent.FLAG_NO_CREATE | PendingIntent.FLAG_IMMUTABLE);
    }

    /**
     * ステータスバーの目覚ましの印を押したときに開く画面（PlatformEntry 経由のアプリ。起動理由 alarm_clock_info。W1-4a）。
     *
     * @param context :seed_platform の Context
     * @return PendingIntent
     */
    private static PendingIntent showIntent(Context context) {
        JSONObject launch = PlatformEntryIntents.launchJson(PlatformContract.LAUNCH_KIND_ALARM_CLOCK_INFO,
                PlatformEntryIntents.NO_TEXT, PlatformEntryIntents.NO_TEXT, PlatformEntryIntents.NO_TIME,
                PlatformEntryIntents.NO_TIME, PlatformEntryIntents.NO_TEXT);
        return PlatformEntryIntents.activity(context, PlatformEntryIntents.REQUEST_ALARM_CLOCK_INFO, launch, false);
    }
}
