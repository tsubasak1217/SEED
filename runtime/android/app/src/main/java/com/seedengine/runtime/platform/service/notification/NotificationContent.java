// ============================================================
//  NotificationContent.java — notification.show の検査済みの中身 1 つ（:seed_platform。W1-5）
//
//  NotificationRequestReader が JSON から作り、NotificationFactory が android.app.Notification に組み立てる。
//  欄の意味と規則は Rust の bridge/notification/request.rs・C# の NotificationRequest と同じ。
// ============================================================

package com.seedengine.runtime.platform.service.notification;

import java.util.Collections;
import java.util.List;

/**
 * 出す通知の中身（不変）。
 */
final class NotificationContent {

    /** 通知の ID（NotificationManager の tag。同じ ID は置き換え）。 */
    final String id;

    /** 出すチャネルの ID。 */
    final String channelId;

    /** 題（空可）。 */
    final String title;

    /** 本文（空可。BigTextStyle で長文も出す）。 */
    final String body;

    /** 常駐（setOngoing。本文のタップでも消さない）か。 */
    final boolean ongoing;

    /** 種類（wire の名前。空なら無し。知らない値は NotificationCategories が付けない）。 */
    final String category;

    /** 操作（最大 MAX_NOTIFICATION_ACTIONS。変えられない一覧）。 */
    final List<NotificationActionItem> actions;

    /** 起動理由にそのまま返す任意の JSON（空可）。 */
    final String payloadJson;

    /**
     * @param id          通知の ID
     * @param channelId   チャネルの ID
     * @param title       題
     * @param body        本文
     * @param ongoing     常駐か
     * @param category    種類
     * @param actions     操作（呼び出し側は以後変えないこと）
     * @param payloadJson 任意の JSON
     */
    NotificationContent(String id, String channelId, String title, String body, boolean ongoing, String category,
                        List<NotificationActionItem> actions, String payloadJson) {
        this.id = id;
        this.channelId = channelId;
        this.title = title;
        this.body = body;
        this.ongoing = ongoing;
        this.category = category;
        this.actions = Collections.unmodifiableList(actions);
        this.payloadJson = payloadJson;
    }
}
