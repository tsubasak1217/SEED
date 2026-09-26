// ============================================================
//  AlarmRearmPlan.java — 張り直すときに、控えの予約をどう扱うかを決める（純粋な処理。W1-3）
//
//  BootReceiver（再起動・時刻／タイムゾーンの変更・アプリの更新・正確なアラームの許可）が控えを読んだあと、1 件ずつ次の 3 つに分ける:
//    toArm            … 予定時刻がまだ先 → setAlarmClock で張り直す（同じ PendingIntent なので既存の予約は置き換わるだけ）
//    awaitingDelivery … 予定時刻を過ぎたが、発火の PendingIntent がまだ残っていて、過ぎてから DELIVERY_GRACE_MS 以内
//                       → AlarmManager が今まさに配信している（時刻を進めたとき等）。AlarmReceiver に任せて触らない
//    missed           … それ以外の過ぎた予約（電源断・強制停止・許可の取り消しで AlarmManager から消えていた）
//                       → 鳴らさずに alarm.missed を記録し、控えから外す（受信機から直接鳴らさない。E-10）
//  Android の API に触らない（「張ってあるか」は呼び出し側が渡す）ので、後で JVM の単体テストにできる（W1-7）。
// ============================================================

package com.seedengine.runtime.platform.service.alarm;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/**
 * 張り直しの振り分けの結果（不変）。
 */
final class AlarmRearmPlan {

    /**
     * 過ぎた予約を「配信の途中」とみなす猶予（ミリ秒）。setAlarmClock の配信は時刻どおり（W1-0 の実機で +1 ms、
     * 使用中の重い端末でも許可まで 1.79 s）なので、これより前に過ぎて控えに残っているものは配信されなかったとみなす。
     */
    static final long DELIVERY_GRACE_MS = 60_000L;

    /** 「張ってあるか」を答えるもの（Android では AlarmScheduler.isArmed）。 */
    interface ArmedCheck {
        /**
         * @param id 予約の ID
         * @return 発火の PendingIntent が残っていれば true
         */
        boolean isArmed(String id);
    }

    /** 張り直す予約（予定時刻がまだ先）。 */
    final List<AlarmEntry> toArm;
    /** 配信の途中とみなして触らない予約。 */
    final List<AlarmEntry> awaitingDelivery;
    /** 鳴らなかったとみなす予約。 */
    final List<AlarmEntry> missed;

    private AlarmRearmPlan(List<AlarmEntry> toArm, List<AlarmEntry> awaitingDelivery, List<AlarmEntry> missed) {
        this.toArm = Collections.unmodifiableList(toArm);
        this.awaitingDelivery = Collections.unmodifiableList(awaitingDelivery);
        this.missed = Collections.unmodifiableList(missed);
    }

    /**
     * 控えの予約を振り分ける。
     *
     * @param entries  控えの予約
     * @param nowUtcMs 今の時刻（UTC の epoch ミリ秒）
     * @param armed    「張ってあるか」を答えるもの（過ぎた予約にだけ聞く）
     * @return 振り分けの結果
     */
    static AlarmRearmPlan plan(List<AlarmEntry> entries, long nowUtcMs, ArmedCheck armed) {
        List<AlarmEntry> toArm = new ArrayList<>();
        List<AlarmEntry> awaiting = new ArrayList<>();
        List<AlarmEntry> missed = new ArrayList<>();
        for (AlarmEntry entry : entries) {
            if (entry.triggerAtUtcMs > nowUtcMs) {
                toArm.add(entry);
            } else if (nowUtcMs - entry.triggerAtUtcMs <= DELIVERY_GRACE_MS && armed.isArmed(entry.id)) {
                awaiting.add(entry);
            } else {
                missed.add(entry);
            }
        }
        return new AlarmRearmPlan(toArm, awaiting, missed);
    }
}
