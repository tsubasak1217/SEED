// ============================================================
//  NotificationActionItem.java — 通知の操作（ボタン）1 つ（:seed_platform。W1-5）
//
//  押すと PlatformEntry 行きの Activity の PendingIntent でアプリが開き、起動理由 notification_action の action_id に
//  この id が入る（通知のトランポリンの禁止により受信機を挟まない。NotificationFactory）。検査は NotificationRequestReader。
// ============================================================

package com.seedengine.runtime.platform.service.notification;

/**
 * 通知の操作 1 つ（不変）。
 */
final class NotificationActionItem {

    /** 操作の ID（起動理由の action_id。1〜MAX_NOTIFICATION_ID_LENGTH 文字）。 */
    final String id;

    /** 表示の文字（1〜MAX_NOTIFICATION_TEXT_LENGTH 文字）。 */
    final String label;

    /**
     * @param id    操作の ID
     * @param label 表示の文字
     */
    NotificationActionItem(String id, String label) {
        this.id = id;
        this.label = label;
    }
}
