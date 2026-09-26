// ============================================================
//  AlarmModule.java — 目覚ましのモジュール "alarm"（:seed_platform の命令。W1-3）
//
//    alarm.schedule           … 予約（同じ ID は置き換え）。返答 { id, trigger_at_utc_ms, replaced, sound_path }
//    alarm.cancel             … 1 つ取り消す（無い ID でも成功）。返答 { id, existed }
//    alarm.cancel_all         … 全部取り消す。返答 { count }
//    alarm.list               … 控えの一覧（予定時刻の順）。返答 { alarms: [ {id, trigger_at_utc_ms, …}, … ] }
//    alarm.can_schedule_exact … 正確なアラームを張れるか。返答 { can_schedule_exact }
//  APK に機能 alarm が入っていない（AlarmReceiver がマニフェストに無い）ときは、どの命令も feature_not_enabled で断る
//  （権限も受信機も無いので、張れても鳴らない。project_settings.json の android.features に "alarm" を足す）。
//  命令は冪等（同じ ID の予約は置き換え・取り消しは何度でも同じ結果）なので、:seed_platform が死んだ瞬間に処理済みだった
//  命令を PlatformConnection が送り直しても壊れない（docs/android.md §25.9）。
//  デスクトップの模擬（runtime/src/engine/platform/bridge/desktop_sim/）が同じ命令に同じ形で答える。
// ============================================================

package com.seedengine.runtime.platform.service.alarm;

import android.content.ComponentName;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.service.PlatformModule;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.List;

/**
 * モジュール "alarm"（状態は AlarmBook と控えが持ち、ここは持たない）。
 */
public final class AlarmModule implements PlatformModule {

    /** PackageManager の問い合わせの flags（何も足さない）。 */
    private static final int NO_COMPONENT_FLAGS = 0;

    /** 機能 alarm が APK に入っているか（マニフェストはプロセスの間に変わらないので 1 回だけ調べる。null = まだ）。 */
    private static volatile Boolean featureEnabled;

    @Override
    public String name() {
        return PlatformContract.MODULE_ALARM;
    }

    @Override
    public byte[] handle(Context context, String method, Object request, Bundle extras) {
        if (!isFeatureEnabled(context)) {
            return PlatformJson.errorReply(PlatformContract.ERROR_FEATURE_NOT_ENABLED,
                    "project_settings.json の android.features に \"" + PlatformContract.MODULE_ALARM + "\" がありません");
        }
        JSONObject arguments = PlatformJson.asObject(request);
        switch (method) {
            case PlatformContract.METHOD_ALARM_SCHEDULE:
                return schedule(context, arguments);
            case PlatformContract.METHOD_ALARM_CANCEL:
                return cancel(context, arguments);
            case PlatformContract.METHOD_ALARM_CANCEL_ALL:
                return cancelAll(context);
            case PlatformContract.METHOD_ALARM_LIST:
                return list(context);
            case PlatformContract.METHOD_ALARM_CAN_SCHEDULE_EXACT:
                return canScheduleExact(context);
            default:
                return PlatformJson.errorReply(PlatformContract.ERROR_UNKNOWN_METHOD);
        }
    }

    /** schedule: 引数を検査して予約する。 */
    private static byte[] schedule(Context context, JSONObject arguments) {
        AlarmRequestReader.Result read = AlarmRequestReader.readSchedule(arguments, System.currentTimeMillis());
        if (!read.ok()) {
            return PlatformJson.errorReply(read.error, read.detail);
        }
        AlarmEntry entry = read.entry;
        AlarmBook.Outcome outcome = AlarmBook.schedule(context, entry);
        if (!outcome.ok()) {
            return PlatformJson.errorReply(outcome.error, outcome.detail);
        }
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_ALARM_ID, entry.id);
        PlatformJson.put(fields, PlatformContract.KEY_ALARM_TRIGGER_AT_UTC_MS, entry.triggerAtUtcMs);
        PlatformJson.put(fields, PlatformContract.KEY_ALARM_REPLACED, outcome.flag);
        PlatformJson.put(fields, PlatformContract.KEY_ALARM_SOUND_PATH, entry.soundPath);
        return PlatformJson.okReply(fields);
    }

    /** cancel: 1 つ取り消す。 */
    private static byte[] cancel(Context context, JSONObject arguments) {
        AlarmRequestReader.Result read = AlarmRequestReader.readId(arguments);
        if (!read.ok()) {
            return PlatformJson.errorReply(read.error, read.detail);
        }
        AlarmBook.Outcome outcome = AlarmBook.cancel(context, read.id);
        if (!outcome.ok()) {
            return PlatformJson.errorReply(outcome.error, outcome.detail);
        }
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_ALARM_ID, read.id);
        PlatformJson.put(fields, PlatformContract.KEY_ALARM_EXISTED, outcome.flag);
        return PlatformJson.okReply(fields);
    }

    /** cancel_all: 全部取り消す。 */
    private static byte[] cancelAll(Context context) {
        AlarmBook.Outcome outcome = AlarmBook.cancelAll(context);
        if (!outcome.ok()) {
            return PlatformJson.errorReply(outcome.error, outcome.detail);
        }
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_COUNT, outcome.count);
        return PlatformJson.okReply(fields);
    }

    /** list: 控えの一覧。 */
    private static byte[] list(Context context) {
        List<AlarmEntry> entries = AlarmBook.list(context);
        JSONArray alarms = new JSONArray();
        for (AlarmEntry entry : entries) {
            alarms.put(entry.toJson());
        }
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_ALARMS, alarms);
        return PlatformJson.okReply(fields);
    }

    /** can_schedule_exact: 正確なアラームを張れるか。 */
    private static byte[] canScheduleExact(Context context) {
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_CAN_SCHEDULE_EXACT, AlarmScheduler.canScheduleExact(context));
        return PlatformJson.okReply(fields);
    }

    /**
     * 機能 alarm が APK に入っているか（AlarmReceiver がマニフェストに宣言されているか。機能の断片だけが宣言する）。
     *
     * @param context どの Context でもよい
     * @return 入っていれば true
     */
    private static boolean isFeatureEnabled(Context context) {
        Boolean cached = featureEnabled;
        if (cached != null) {
            return cached;
        }
        ComponentName receiver = new ComponentName(context, AlarmReceiver.class);
        PackageManager packages = context.getPackageManager();
        boolean enabled;
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                packages.getReceiverInfo(receiver, PackageManager.ComponentInfoFlags.of(NO_COMPONENT_FLAGS));
            } else {
                packages.getReceiverInfo(receiver, NO_COMPONENT_FLAGS);
            }
            enabled = true;
        } catch (PackageManager.NameNotFoundException e) {
            enabled = false;
        }
        featureEnabled = enabled;
        return enabled;
    }
}
