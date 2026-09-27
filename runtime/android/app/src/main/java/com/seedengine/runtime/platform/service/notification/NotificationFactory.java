// ============================================================
//  NotificationFactory.java — notification.show の中身から android.app.Notification を組み立てる（:seed_platform。W1-5）
//
//  【見た目】題・本文（本文は BigTextStyle で長文も折りたたみを開けば全部見える）・種類（NotificationCategories。知らない値は付けない）・
//  常駐（setOngoing）。常駐でない通知は本文のタップで消える（setAutoCancel）。操作（ボタン）を押しても通知は消えない
//  （Android の決まり）ので、アプリが起動理由を見て Notifications.Cancel(id) で消す。
//
//  【起動】本文のタップも操作も、PlatformEntry 行きの Activity の PendingIntent（W1-4a の PlatformEntryIntents）。
//  Android 12+ は通知から受信機・サービスを経由して Activity を開けない（通知のトランポリンの禁止）ので、受信機を挟まない。
//    本文のタップ … 起動理由 notification_tap { id, payload_json }
//    操作         … 起動理由 notification_action { id, action_id, payload_json }
//  PendingIntent の同一性は「用途の要求コード（REQUEST_NOTIFICATION_TAP / _ACTION）＋ identity（用途・通知の ID・操作の ID を
//  長さ付きで並べたもの。Intent.setIdentifier）」。通知・操作ごとに別の PendingIntent になり、FLAG_UPDATE_CURRENT が別の通知の
//  起動理由を書き換えることが無い（理由は PlatformEntryIntents の【通知の PendingIntent の同一性】）。同じ通知を出し直すと
//  同じ PendingIntent の extras（payload）が新しくなる。
//
//  【アイコン】小さなアイコンは Android の標準の絵 ic_popup_reminder（アプリのアイコンはプロジェクトごとに変わり、ステータスバーでは
//  形の影しか出ないため使わない。プロジェクトごとの通知のアイコンは backlog）。
// ============================================================

package com.seedengine.runtime.platform.service.notification;

import android.app.Notification;
import android.app.PendingIntent;
import android.content.Context;
import android.graphics.drawable.Icon;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.service.PlatformEntryIntents;

import org.json.JSONObject;

/**
 * 通知の組み立て（static のみ）。
 */
final class NotificationFactory {

    private NotificationFactory() {
    }

    /** 通知の小さなアイコン（Android の標準の絵。鳴動の通知は ic_lock_idle_alarm）。 */
    private static final int SMALL_ICON = android.R.drawable.ic_popup_reminder;

    /** identity の用途の部品: 本文のタップ。 */
    private static final String IDENTITY_TAP = "tap";

    /** identity の用途の部品: 操作。 */
    private static final String IDENTITY_ACTION = "action";

    /** 本文のタップには操作の ID が無い。 */
    private static final String NO_ACTION_ID = PlatformEntryIntents.NO_TEXT;

    /**
     * 通知を組み立てる。
     *
     * @param context :seed_platform の Context
     * @param content 検査済みの中身
     * @return 通知
     */
    static Notification build(Context context, NotificationContent content) {
        Notification.Builder builder = new Notification.Builder(context, content.channelId)
                .setSmallIcon(SMALL_ICON)
                .setContentTitle(content.title)
                .setContentText(content.body)
                .setContentIntent(tapIntent(context, content))
                .setOngoing(content.ongoing)
                // 常駐でない通知は本文のタップで消す（常駐はアプリが Cancel するまで残す）
                .setAutoCancel(!content.ongoing);
        if (!content.body.isEmpty()) {
            builder.setStyle(new Notification.BigTextStyle().bigText(content.body));
        }
        String category = NotificationCategories.toAndroid(content.category);
        if (category != null) {
            builder.setCategory(category);
        } else if (!content.category.isEmpty()) {
            Log.w(PlatformContract.LOG_TAG, "通知 " + content.id + " の種類 " + content.category + " は語彙に無いので付けません");
        }
        for (NotificationActionItem action : content.actions) {
            builder.addAction(new Notification.Action.Builder(Icon.createWithResource(context, SMALL_ICON), action.label,
                    actionIntent(context, content, action)).build());
        }
        return builder.build();
    }

    /** 本文のタップ（起動理由 notification_tap）。 */
    private static PendingIntent tapIntent(Context context, NotificationContent content) {
        JSONObject launch = PlatformEntryIntents.launchJson(PlatformContract.LAUNCH_KIND_NOTIFICATION_TAP, content.id, NO_ACTION_ID,
                PlatformEntryIntents.NO_TIME, PlatformEntryIntents.NO_TIME, content.payloadJson);
        return PlatformEntryIntents.activity(context, PlatformEntryIntents.REQUEST_NOTIFICATION_TAP,
                PlatformEntryIntents.identity(IDENTITY_TAP, content.id), launch, false);
    }

    /** 操作（起動理由 notification_action）。 */
    private static PendingIntent actionIntent(Context context, NotificationContent content, NotificationActionItem action) {
        JSONObject launch = PlatformEntryIntents.launchJson(PlatformContract.LAUNCH_KIND_NOTIFICATION_ACTION, content.id, action.id,
                PlatformEntryIntents.NO_TIME, PlatformEntryIntents.NO_TIME, content.payloadJson);
        return PlatformEntryIntents.activity(context, PlatformEntryIntents.REQUEST_NOTIFICATION_ACTION,
                PlatformEntryIntents.identity(IDENTITY_ACTION, content.id, action.id), launch, false);
    }
}
