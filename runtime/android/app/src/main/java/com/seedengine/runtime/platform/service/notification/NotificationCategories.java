// ============================================================
//  NotificationCategories.java — 通知の種類（wire の名前）→ Notification.CATEGORY_* の表（:seed_platform。W1-5）
//
//  種類は OS が通知の扱い（おやすみモードの例外・並べ方など）を決める手がかり。語彙は wire の NOTIFICATION_CATEGORY_* の 5 つで、
//  表に無い値は付けない（誤りにはしない。ログだけ）。Notification.CATEGORY_* は API 21（REMINDER は 23）からの文字列の定数で、
//  minSdk 29 ではどれも使える（api-versions.xml で確かめた）。種類を足すときはこの表と Rust・C# の語彙に 1 行ずつ足す。
// ============================================================

package com.seedengine.runtime.platform.service.notification;

import android.app.Notification;

import com.seedengine.runtime.platform.PlatformContract;

import java.util.Collections;
import java.util.HashMap;
import java.util.Map;

/**
 * 種類の表（static のみ）。
 */
final class NotificationCategories {

    private NotificationCategories() {
    }

    /** wire の名前 → Notification.CATEGORY_*（作った後は変えない）。 */
    private static final Map<String, String> TABLE = buildTable();

    /**
     * 表を作る。
     *
     * @return 変えられない表
     */
    private static Map<String, String> buildTable() {
        Map<String, String> table = new HashMap<>();
        table.put(PlatformContract.NOTIFICATION_CATEGORY_ALARM, Notification.CATEGORY_ALARM);
        table.put(PlatformContract.NOTIFICATION_CATEGORY_REMINDER, Notification.CATEGORY_REMINDER);
        table.put(PlatformContract.NOTIFICATION_CATEGORY_STATUS, Notification.CATEGORY_STATUS);
        table.put(PlatformContract.NOTIFICATION_CATEGORY_EVENT, Notification.CATEGORY_EVENT);
        table.put(PlatformContract.NOTIFICATION_CATEGORY_PROGRESS, Notification.CATEGORY_PROGRESS);
        return Collections.unmodifiableMap(table);
    }

    /**
     * 種類を Android の値にする。
     *
     * @param category wire の名前（空可）
     * @return Notification.CATEGORY_*（空・表に無ければ null＝付けない）
     */
    static String toAndroid(String category) {
        return TABLE.get(category);
    }
}
