// ============================================================
//  RingNotification.java — 鳴動の通知チャネルと通知（フルスクリーン通知・常駐・「開く」。:seed_platform。W1-4a）
//
//  【チャネル】seed_platform_alarm（PlatformContract.NOTIFICATION_CHANNEL_ALARM）。重要度 HIGH（ヘッドアップ・フルスクリーン通知が
//  出る）。チャネルの音と振動は無し（音は RingService が USAGE_ALARM で、振動は RingVibration が出す。通知の音と二重にしない）。
//  おやすみモードの例外（setBypassDnd）は要求しない（通知のポリシーへのアクセスが要り、アラームの音そのものは USAGE_ALARM で
//  おやすみモードの「アラーム」の扱いになる）。表示名・説明は res/values/seed_platform_strings.xml（言語ごとに差し替えられる）。
//
//  【通知】予約の title / body・CATEGORY_ALARM・常駐（ongoing）・ロック画面でも中身を出す（VISIBILITY_PUBLIC）。
//    フルスクリーン通知 … PlatformEntry 行き（起動理由 alarm）。画面オフ・ロック中は鳴動画面（MainActivity）がロック画面の上に出る。
//                         端末の使用中はヘッドアップ通知になる（W1-0 の実機）
//    本文のタップ       … 同じく起動理由 alarm（ヘッドアップ通知から鳴動画面へ。W1-0 の F-2）
//    「開く」の操作     … 起動理由 notification_action・action_id "open"
//  操作はすべて PlatformEntry 行きの Activity の PendingIntent（通知のトランポリンの禁止。PlatformEntryIntents）。
//  止めるボタンは置かない（止めるかどうかはアプリのスクリプトが決めて StopRinging を呼ぶ。W1-P2）。
//
//  【POST_NOTIFICATIONS が無いとき】Android 13 以降で通知の許可が無くても前景サービスは動く（通知が出ないだけ）。音と振動は続く。
//  ログで警告する（通知の許可を求める API は W1-5）。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.graphics.drawable.Icon;
import android.os.Build;
import android.util.Log;

import com.seedengine.runtime.R;
import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.service.PlatformEntryIntents;
import com.seedengine.runtime.platform.service.alarm.AlarmEntry;

import org.json.JSONObject;

/**
 * 鳴動の通知（static のみ。UI スレッドから使う）。
 */
final class RingNotification {

    private RingNotification() {
    }

    /**
     * 鳴動の通知の ID（2 つを交互に使う）。待ち行列の次の予約へ入れ替わるときに別の ID で出し直すと、新しい通知として
     * 知らせ直され、フルスクリーン通知ももう一度起きる（同じ ID の書き換えは setOnlyAlertOnce で知らせ直さない）。
     */
    private static final int[] NOTIFICATION_IDS = {7_201, 7_202};

    /** 通知の小さなアイコン（Android の標準の目覚ましの絵。アプリのアイコンはプロジェクトごとに変わりうるので使わない）。 */
    private static final int SMALL_ICON = android.R.drawable.ic_lock_idle_alarm;

    /**
     * 使う通知の ID。
     *
     * @param slot 交互に使う番号（0 か 1）
     * @return 通知の ID
     */
    static int notificationId(int slot) {
        return NOTIFICATION_IDS[slot % NOTIFICATION_IDS.length];
    }

    /**
     * 交互に使う番号の次。
     *
     * @param slot 今の番号
     * @return 次の番号
     */
    static int nextSlot(int slot) {
        return (slot + 1) % NOTIFICATION_IDS.length;
    }

    /**
     * チャネルを作る（あれば何もしない。利用者が変えた設定は OS が保つ）。
     *
     * @param context :seed_platform の Context
     */
    static void ensureChannel(Context context) {
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        if (manager == null) {
            return;
        }
        NotificationChannel channel = new NotificationChannel(PlatformContract.NOTIFICATION_CHANNEL_ALARM,
                context.getString(R.string.seed_platform_alarm_channel_name), NotificationManager.IMPORTANCE_HIGH);
        channel.setDescription(context.getString(R.string.seed_platform_alarm_channel_description));
        channel.setSound(null, null);
        channel.enableVibration(false);
        channel.setLockscreenVisibility(Notification.VISIBILITY_PUBLIC);
        manager.createNotificationChannel(channel);
    }

    /**
     * 通知が出るか（通知の許可・フルスクリーン通知の許可）をログへ出す（出なくても鳴動は続ける）。
     *
     * @param context :seed_platform の Context
     */
    static void logVisibility(Context context) {
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        if (manager == null) {
            return;
        }
        if (!manager.areNotificationsEnabled()) {
            Log.w(PlatformContract.LOG_TAG, "通知が許可されていないので、鳴動の通知・フルスクリーン通知は出ません（音と振動は続けます。"
                    + "Android 13 以降は POST_NOTIFICATIONS の許可が要る）");
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE && !manager.canUseFullScreenIntent()) {
            Log.w(PlatformContract.LOG_TAG, "フルスクリーン通知が許可されていないので、ロック画面の上に鳴動画面は出ません"
                    + "（ヘッドアップ通知になります。設定の「全画面通知」）");
        }
    }

    /**
     * 鳴動の通知を作る。
     *
     * @param context :seed_platform の Context
     * @param session 鳴動
     * @return 通知
     */
    static Notification build(Context context, RingSession session) {
        AlarmEntry entry = session.entry;
        JSONObject alarmLaunch = PlatformEntryIntents.launchJson(PlatformContract.LAUNCH_KIND_ALARM, entry.id,
                PlatformEntryIntents.NO_TEXT, entry.triggerAtUtcMs, session.firedAtUtcMs, entry.payloadJson);
        JSONObject openLaunch = PlatformEntryIntents.launchJson(PlatformContract.LAUNCH_KIND_NOTIFICATION_ACTION, entry.id,
                PlatformContract.LAUNCH_ACTION_OPEN, entry.triggerAtUtcMs, session.firedAtUtcMs, entry.payloadJson);
        PendingIntent fullScreen = PlatformEntryIntents.activity(context, PlatformEntryIntents.REQUEST_RING_FULL_SCREEN,
                alarmLaunch, true);
        PendingIntent content = PlatformEntryIntents.activity(context, PlatformEntryIntents.REQUEST_RING_CONTENT,
                alarmLaunch, false);
        PendingIntent open = PlatformEntryIntents.activity(context, PlatformEntryIntents.REQUEST_RING_ACTION_OPEN,
                openLaunch, false);
        Notification.Action openAction = new Notification.Action.Builder(Icon.createWithResource(context, SMALL_ICON),
                context.getString(R.string.seed_platform_alarm_action_open), open).build();

        String title = entry.title.isEmpty() ? context.getString(R.string.seed_platform_alarm_default_title) : entry.title;
        Notification.Builder builder = baseBuilder(context, title)
                .setContentText(entry.body)
                .setWhen(session.startedAtUtcMs)
                .setShowWhen(true)
                .setFullScreenIntent(fullScreen, true)
                .setContentIntent(content)
                .addAction(openAction);
        if (!entry.body.isEmpty()) {
            builder.setStyle(new Notification.BigTextStyle().bigText(entry.body));
        }
        return builder.build();
    }

    /**
     * 鳴らすものが無いのに前景サービスとして起こされたとき（取り消しと配信が重なった古い配信）の仮の通知。
     * startForegroundService の後は必ず startForeground を呼ぶ約束なので出し、すぐ消す。
     *
     * @param context :seed_platform の Context
     * @return 通知
     */
    static Notification placeholder(Context context) {
        return baseBuilder(context, context.getString(R.string.seed_platform_alarm_default_title)).build();
    }

    /**
     * 共通の部分（チャネル・アイコン・種類・ロック画面の表示・常駐・知らせは最初の 1 回だけ・前景サービスの通知をすぐ出す）。
     *
     * @param context :seed_platform の Context
     * @param title   題
     * @return 組み立ての途中
     */
    private static Notification.Builder baseBuilder(Context context, String title) {
        Notification.Builder builder = new Notification.Builder(context, PlatformContract.NOTIFICATION_CHANNEL_ALARM)
                .setSmallIcon(SMALL_ICON)
                .setContentTitle(title)
                .setCategory(Notification.CATEGORY_ALARM)
                .setVisibility(Notification.VISIBILITY_PUBLIC)
                .setOngoing(true)
                .setOnlyAlertOnce(true);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            // Android 12 以降は前景サービスの通知を最大 10 秒遅らせることがある。鳴動の通知はすぐ出す
            builder.setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE);
        }
        return builder;
    }
}
