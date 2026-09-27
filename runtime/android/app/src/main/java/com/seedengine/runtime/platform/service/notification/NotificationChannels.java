// ============================================================
//  NotificationChannels.java — アプリの通知チャネルを作る・出せるかを調べる（:seed_platform。W1-5）
//
//  【作る】NotificationManager.createNotificationChannel（API 26）。同じ ID のチャネルが既にあれば、OS は名前と説明だけを変え、
//  重要度など利用者が変えられる設定は保つ（アプリからは重要度を上げられない）。何度呼んでもよい（冪等）。
//  重要度の語彙（low / default / high）→ NotificationManager.IMPORTANCE_LOW / DEFAULT / HIGH（API 24）。
//
//  【出せるか】show の前に見る: ① アプリの通知が有効か（areNotificationsEnabled。Android 13+ は POST_NOTIFICATIONS の許可、
//  12 以前は利用者の切り替え）→ 無効なら notifications_disabled。② チャネルがあるか（getNotificationChannel が null なら
//  channel_not_found）。③ チャネルが利用者に止められていないか（重要度 IMPORTANCE_NONE なら notifications_disabled）。
//  OS は無効なときの notify を黙って捨てるので、ここで理由を返してスクリプトが案内（Permissions.Request 等）できるようにする。
// ============================================================

package com.seedengine.runtime.platform.service.notification;

import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.content.Context;

import com.seedengine.runtime.platform.PlatformContract;

/**
 * チャネルの作成と、出せるかの判定（static のみ）。
 */
final class NotificationChannels {

    private NotificationChannels() {
    }

    /** 出せるかの判定の結果（出せるなら error が null）。 */
    static final class Readiness {
        /** 出せない理由（PlatformContract.ERROR_*。出せるなら null）。 */
        final String error;
        /** 説明（返答の detail）。 */
        final String detail;

        private Readiness(String error, String detail) {
            this.error = error;
            this.detail = detail;
        }

        /** @return 出せるなら true */
        boolean ok() {
            return error == null;
        }
    }

    /** 出せる（理由なし）。 */
    private static final Readiness READY = new Readiness(null, null);

    /**
     * チャネルを作る（あれば名前と説明だけ変わる）。
     *
     * @param context :seed_platform の Context
     * @param spec    作るチャネル
     */
    static void ensure(Context context, NotificationChannelSpec spec) {
        NotificationChannel channel = new NotificationChannel(spec.id, spec.name, importance(spec.importance));
        if (!spec.description.isEmpty()) {
            channel.setDescription(spec.description);
        }
        manager(context).createNotificationChannel(channel);
    }

    /**
     * アプリの通知が端末で有効か（Android 13+ は POST_NOTIFICATIONS の許可も含む）。
     *
     * @param context どの Context でもよい
     * @return 有効なら true
     */
    static boolean areEnabled(Context context) {
        return manager(context).areNotificationsEnabled();
    }

    /**
     * そのチャネルへ今出せるか。
     *
     * @param context   :seed_platform の Context
     * @param channelId チャネルの ID
     * @return 判定
     */
    static Readiness readiness(Context context, String channelId) {
        NotificationManager manager = manager(context);
        if (!manager.areNotificationsEnabled()) {
            return new Readiness(PlatformContract.ERROR_NOTIFICATIONS_DISABLED,
                    "アプリの通知が無効です（Android 13 以降は POST_NOTIFICATIONS の許可。Permissions.Request(PostNotifications)）");
        }
        NotificationChannel channel = manager.getNotificationChannel(channelId);
        if (channel == null) {
            return new Readiness(PlatformContract.ERROR_CHANNEL_NOT_FOUND,
                    "チャネル " + channelId + " がありません（先に notification.ensure_channel）");
        }
        if (channel.getImportance() == NotificationManager.IMPORTANCE_NONE) {
            return new Readiness(PlatformContract.ERROR_NOTIFICATIONS_DISABLED,
                    "チャネル " + channelId + " は利用者に止められています（Permissions.OpenSettings(PostNotifications) で通知の設定へ）");
        }
        return READY;
    }

    /**
     * 重要度の wire の名前を NotificationManager の値にする（語彙は NotificationRequestReader が確かめ済み）。
     *
     * @param importance wire の名前
     * @return NotificationManager.IMPORTANCE_*
     */
    private static int importance(String importance) {
        switch (importance) {
            case PlatformContract.NOTIFICATION_IMPORTANCE_LOW:
                return NotificationManager.IMPORTANCE_LOW;
            case PlatformContract.NOTIFICATION_IMPORTANCE_HIGH:
                return NotificationManager.IMPORTANCE_HIGH;
            default:
                return NotificationManager.IMPORTANCE_DEFAULT;
        }
    }

    /**
     * NotificationManager（システムのサービスなので、アプリのプロセスでは必ずある）。
     *
     * @param context どの Context でもよい
     * @return NotificationManager
     */
    static NotificationManager manager(Context context) {
        return context.getSystemService(NotificationManager.class);
    }
}
