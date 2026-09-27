// ============================================================
//  NotificationChannelSpec.java — notification.ensure_channel の検査済みの中身（:seed_platform。W1-5）
//
//  NotificationRequestReader が JSON から作り、NotificationChannels が NotificationChannel にして作る。
// ============================================================

package com.seedengine.runtime.platform.service.notification;

/**
 * 作るチャネル（不変）。
 */
final class NotificationChannelSpec {

    /** チャネルの ID（RESERVED_NOTIFICATION_CHANNEL_PREFIX で始まらない）。 */
    final String id;

    /** 表示名（端末の設定の「通知」に出る）。 */
    final String name;

    /** 重要度（wire の名前。NOTIFICATION_IMPORTANCE_* のどれか）。 */
    final String importance;

    /** 説明（空可）。 */
    final String description;

    /**
     * @param id          チャネルの ID
     * @param name        表示名
     * @param importance  重要度（wire の名前）
     * @param description 説明
     */
    NotificationChannelSpec(String id, String name, String importance, String description) {
        this.id = id;
        this.name = name;
        this.importance = importance;
        this.description = description;
    }
}
