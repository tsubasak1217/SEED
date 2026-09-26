package com.seedengine.platformspike;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;

import java.util.ArrayList;
import java.util.List;

/**
 * AlarmManager.setAlarmClock での予約・取り消し・張り直し（:seed_platform で動く）。
 *
 * <p>PendingIntent は予約 ID ごとに data の URI で区別する（extras は PendingIntent の同一性に効かないため。
 * 言語の hashCode を requestCode に使わない＝アプリ仕様 §6.12 の教訓 7）。</p>
 */
final class AlarmScheduler {
    private static final String FIRE_URI_PREFIX = "seedalarm://alarm/";
    private static final int REQUEST_CODE_FIRE = 0;
    private static final int REQUEST_CODE_SHOW = 1;

    private AlarmScheduler() {}

    static boolean canScheduleExact(Context context) {
        return context.getSystemService(AlarmManager.class).canScheduleExactAlarms();
    }

    /** 予約する。成功なら null、失敗なら理由の文字列。 */
    static String schedule(Context context, AlarmEntry entry) {
        AlarmManager alarms = context.getSystemService(AlarmManager.class);
        if (!alarms.canScheduleExactAlarms()) {
            // 黙って不正確な予約に落とさない（ロードマップ §2.2 の方針）。
            return "exact_alarm_not_permitted";
        }
        PendingIntent operation = firePendingIntent(context, entry);
        // ステータスバーの目覚ましの印をタップしたときに開く画面（普通の起動）。
        PendingIntent show = PendingIntent.getActivity(context, REQUEST_CODE_SHOW,
                new Intent(context, MainActivity.class), PendingIntent.FLAG_IMMUTABLE);
        try {
            alarms.setAlarmClock(new AlarmManager.AlarmClockInfo(entry.triggerAtMs, show), operation);
        } catch (SecurityException e) {
            return "security:" + SpikeLog.oneLine(e);
        }
        SpikeLog.mark("alarm_scheduled", "id=" + entry.id
                + " trigger_at=" + entry.triggerAtMs
                + " in_ms=" + (entry.triggerAtMs - System.currentTimeMillis())
                + " fgs_type=" + entry.fgsType
                + " max_ring_s=" + entry.maxRingSeconds);
        return null;
    }

    static void cancel(Context context, String id) {
        PendingIntent operation = existingFirePendingIntent(context, id);
        if (operation != null) {
            context.getSystemService(AlarmManager.class).cancel(operation);
            operation.cancel();
        }
        SpikeLog.mark("alarm_cancelled", "id=" + id + " was_armed=" + (operation != null));
    }

    /** この予約の PendingIntent がシステムに残っているか（強制停止・更新で消えるかの確認に使う）。 */
    static boolean isArmed(Context context, String id) {
        return existingFirePendingIntent(context, id) != null;
    }

    /**
     * 控えの全件を張り直す（再起動・更新・アプリの起動のたび）。過ぎたものは鳴らさずに「鳴らなかった」と記録して控えから外す。
     */
    static String rearmAll(Context context, String reason) {
        List<AlarmEntry> stored = AlarmStore.load(context);
        AlarmManager alarms = context.getSystemService(AlarmManager.class);
        AlarmManager.AlarmClockInfo next = alarms.getNextAlarmClock();
        String nextCreator = next != null && next.getShowIntent() != null
                ? next.getShowIntent().getCreatorPackage() : "none";
        long now = System.currentTimeMillis();
        int aliveBefore = 0;
        int armed = 0;
        int missed = 0;
        List<AlarmEntry> keep = new ArrayList<>();
        for (AlarmEntry entry : stored) {
            if (isArmed(context, entry.id)) {
                aliveBefore++;
            }
            if (entry.triggerAtMs <= now) {
                missed++;
                SpikeLog.mark("alarm_missed", "id=" + entry.id + " sched=" + entry.triggerAtMs
                        + " late_ms=" + (now - entry.triggerAtMs) + " reason=" + reason);
                continue;
            }
            String error = schedule(context, entry);
            if (error == null) {
                armed++;
            } else {
                SpikeLog.mark("rearm_failed", "id=" + entry.id + " error=" + error);
            }
            // 失敗しても控えには残す（権限が戻ったときに張り直せるように）。
            keep.add(entry);
        }
        AlarmStore.save(context, keep);
        String summary = "reason=" + reason + " stored=" + stored.size() + " pi_alive_before=" + aliveBefore
                + " armed=" + armed + " missed=" + missed
                + " next_alarm_clock_before=" + (next != null ? next.getTriggerTime() : -1)
                + " next_alarm_clock_creator=" + nextCreator;
        SpikeLog.mark("rearm_all", summary + " " + DeviceState.describe(context));
        return summary;
    }

    private static Intent fireIntent(Context context, String id) {
        return new Intent(context, AlarmReceiver.class)
                .setAction(SpikeContract.ACTION_ALARM_FIRE)
                .setData(Uri.parse(FIRE_URI_PREFIX + Uri.encode(id)));
    }

    private static PendingIntent firePendingIntent(Context context, AlarmEntry entry) {
        Intent intent = fireIntent(context, entry.id)
                .putExtra(SpikeContract.EXTRA_ID, entry.id)
                .putExtra(SpikeContract.EXTRA_TRIGGER_AT, entry.triggerAtMs)
                .putExtra(SpikeContract.EXTRA_FGS_TYPE, entry.fgsType)
                .putExtra(SpikeContract.EXTRA_MAX_RING_SECONDS, entry.maxRingSeconds);
        return PendingIntent.getBroadcast(context, REQUEST_CODE_FIRE, intent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }

    private static PendingIntent existingFirePendingIntent(Context context, String id) {
        return PendingIntent.getBroadcast(context, REQUEST_CODE_FIRE, fireIntent(context, id),
                PendingIntent.FLAG_NO_CREATE | PendingIntent.FLAG_IMMUTABLE);
    }
}
