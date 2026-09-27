// ============================================================
//  AlarmBook.java — 予約の控え（AlarmStore）と AlarmManager（AlarmScheduler）を食い違わないように組み合わせる（W1-3）
//
//  :seed_platform の中で予約を触るのは、命令（AlarmModule。Binder のスレッド）・発火（AlarmReceiver）・張り直し（BootReceiver。
//  どちらも UI スレッド）・起動時の照合（AlarmStartup。背面のスレッド。W1-4a）の 4 か所。すべてこのクラスの static synchronized を
//  通し、「控えを読む → 決める → 書く → 張る」を 1 つの lock の中で行う（別スレッドの割り込みで控えと AlarmManager が食い違わないように）。
//
//  【順序の決まり（落ちたときに「失う」より「二重に知らせる」側へ倒す）】
//    予約     : 控えに書く → 張る。張れなければ控えを元へ戻す（控えに無い予約は鳴っても無視されるので、先に控え）
//    取り消し : 控えから消す → 外す（先に外して控えの書き込みに失敗すると、次の張り直しで復活してしまう）
//    発火     : 鳴動へ渡す（W1-4a）→ 記録する（alarm.fired。鳴動の前景サービスを起こせなかったら渡さずに
//               alarm.missed(start_failed)）→ 控えから消す（間で落ちると、次の張り直しで alarm.missed も記録されうる）
//    張り直し : 過ぎた予約を記録する（alarm.missed）→ 控えから消す
//    照合     : 張り直しと同じ。ただし PendingIntent が残っている予約（張ってある）には触らない（W1-4a）
//  lock の順は AlarmBook → RingRegistry（発火の鳴動への受け渡し。W1-4a）・AlarmBook → EventJournal（記録）だけで、
//  逆向きは無い（行き詰まらない。RingRegistry は AlarmBook を呼ばない）。
// ============================================================

package com.seedengine.runtime.platform.service.alarm;

import android.content.Context;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.function.Consumer;

/**
 * 予約の操作（static のみ。プロセスで 1 つの lock）。
 */
final class AlarmBook {

    private AlarmBook() {
    }

    /** 見つからないときの位置。 */
    private static final int NOT_FOUND = -1;

    /** 命令の結果（error が null なら成功）。 */
    static final class Outcome {
        /** 失敗の理由（PlatformContract.ERROR_*。成功なら null）。 */
        final String error;
        /** 失敗の説明（null 可）。 */
        final String detail;
        /** schedule: 置き換えたか / cancel: 控えにあったか。 */
        final boolean flag;
        /** cancel_all: 取り消した件数。 */
        final int count;

        private Outcome(String error, String detail, boolean flag, int count) {
            this.error = error;
            this.detail = detail;
            this.flag = flag;
            this.count = count;
        }

        /** @return 成功なら true */
        boolean ok() {
            return error == null;
        }

        static Outcome success(boolean flag, int count) {
            return new Outcome(null, null, flag, count);
        }

        static Outcome failure(String error, String detail) {
            return new Outcome(error, detail, false, 0);
        }
    }

    /** 張り直しの結果。 */
    static final class RearmResult {
        /** 控えにあった件数。 */
        final int stored;
        /** 張り直した件数。 */
        final int armed;
        /** 張り直せなかった件数（控えには残した）。 */
        final int failed;
        /** 鳴らなかったと記録して控えから外した件数。 */
        final int missed;
        /** 配信の途中とみなして触らなかった件数。 */
        final int awaiting;

        RearmResult(int stored, int armed, int failed, int missed, int awaiting) {
            this.stored = stored;
            this.armed = armed;
            this.failed = failed;
            this.missed = missed;
            this.awaiting = awaiting;
        }

        @Override
        public String toString() {
            return "控え " + stored + "・張り直し " + armed + "・失敗 " + failed + "・鳴らなかった " + missed + "・配信待ち " + awaiting;
        }
    }

    /**
     * 予約する（同じ ID は置き換え）。
     *
     * @param context :seed_platform の Context
     * @param entry   予約（AlarmRequestReader が検査済み）
     * @return 結果（flag = 置き換えたか）
     */
    static synchronized Outcome schedule(Context context, AlarmEntry entry) {
        List<AlarmEntry> original = AlarmStore.load(context);
        int index = indexOf(original, entry.id);
        if (index == NOT_FOUND && original.size() >= PlatformContract.MAX_SCHEDULED_ALARMS) {
            return Outcome.failure(PlatformContract.ERROR_TOO_MANY_ALARMS, "上限 " + PlatformContract.MAX_SCHEDULED_ALARMS + " 件");
        }
        // 張れないと分かっているなら控えにも書かない（黙って不正確な予約に落とさない）
        if (!AlarmScheduler.canScheduleExact(context)) {
            return Outcome.failure(PlatformContract.ERROR_EXACT_ALARM_NOT_ALLOWED, null);
        }
        List<AlarmEntry> updated = new ArrayList<>(original);
        if (index == NOT_FOUND) {
            updated.add(entry);
        } else {
            updated.set(index, entry);
        }
        if (!AlarmStore.save(context, updated)) {
            return Outcome.failure(PlatformContract.ERROR_STORE_WRITE_FAILED, null);
        }
        AlarmScheduler.ArmResult armed = AlarmScheduler.arm(context, entry);
        if (!armed.ok()) {
            // 張れなかった: 控えを元へ戻す（戻せなくても、次の張り直しで張るか鳴らなかったと記録される）
            if (!AlarmStore.save(context, original)) {
                Log.w(PlatformContract.LOG_TAG, "予約 " + entry.id + " を張れず、控えも元へ戻せませんでした");
            }
            return Outcome.failure(armed.error, armed.detail);
        }
        Log.i(PlatformContract.LOG_TAG, "目覚まし " + entry.id + " を予約しました（" + entry.triggerAtUtcMs + "・あと "
                + (entry.triggerAtUtcMs - System.currentTimeMillis()) + " ms" + (index == NOT_FOUND ? "" : "・置き換え") + "）");
        return Outcome.success(index != NOT_FOUND, 0);
    }

    /**
     * 予約を 1 つ取り消す（無い ID でも成功。冪等）。
     *
     * @param context :seed_platform の Context
     * @param id      予約の ID
     * @return 結果（flag = 控えにあったか）
     */
    static synchronized Outcome cancel(Context context, String id) {
        List<AlarmEntry> entries = AlarmStore.load(context);
        boolean existed = entries.removeIf(entry -> entry.id.equals(id));
        if (existed && !AlarmStore.save(context, entries)) {
            return Outcome.failure(PlatformContract.ERROR_STORE_WRITE_FAILED, null);
        }
        // 控えに無くても外す（控えが壊れて読めなかったときに残った予約も止める）
        boolean wasArmed = AlarmScheduler.disarm(context, id);
        Log.i(PlatformContract.LOG_TAG, "目覚まし " + id + " を取り消しました（控え " + (existed ? "あり" : "なし")
                + "・予約 " + (wasArmed ? "あり" : "なし") + "）");
        return Outcome.success(existed, 0);
    }

    /**
     * 予約を全部取り消す。
     *
     * @param context :seed_platform の Context
     * @return 結果（count = 控えにあった件数）
     */
    static synchronized Outcome cancelAll(Context context) {
        List<AlarmEntry> entries = AlarmStore.load(context);
        if (!entries.isEmpty() && !AlarmStore.save(context, new ArrayList<>())) {
            return Outcome.failure(PlatformContract.ERROR_STORE_WRITE_FAILED, null);
        }
        for (AlarmEntry entry : entries) {
            AlarmScheduler.disarm(context, entry.id);
        }
        Log.i(PlatformContract.LOG_TAG, "目覚ましを全部取り消しました（" + entries.size() + " 件）");
        return Outcome.success(false, entries.size());
    }

    /**
     * 控えの一覧（予定時刻の順。同じ時刻は ID の順）。
     *
     * @param context :seed_platform の Context
     * @return 予約の一覧
     */
    static synchronized List<AlarmEntry> list(Context context) {
        List<AlarmEntry> entries = AlarmStore.load(context);
        entries.sort(Comparator.comparingLong((AlarmEntry entry) -> entry.triggerAtUtcMs).thenComparing(entry -> entry.id));
        return entries;
    }

    /**
     * 発火を処理する（AlarmReceiver から）: 控えの予約を確かめ、鳴動へ渡し（handOff。W1-4a）、alarm.fired（鳴動の
     * 前景サービスを起こせなかったなら handOff を呼ばずに alarm.missed(start_failed)）を記録し、控えから消す（一回限り）。
     *
     * <p>鳴動へ渡すのを記録より先にするのは、alarm.fired を受けたアプリが GetRinging を呼んだとき必ずその鳴動が返るようにするため
     * （記録と同時にエンジンへ呼び鈴が鳴る）。handOff は値の出し入れだけで、ファイルには触らないこと（この lock の中で呼ぶ）。</p>
     *
     * @param context         :seed_platform の Context
     * @param id              発火の Intent の予約 ID（null 可）
     * @param intentTriggerAt 発火の Intent の予定時刻（AlarmScheduler.UNKNOWN_TRIGGER_AT なら確かめない）
     * @param nowUtcMs        配信を受けた時刻
     * @param handOff         鳴動へ渡す処理（鳴動の前景サービスを起こせなかったなら null。W1-4a）
     * @return 鳴った予約（控えに無い・古い配信なら null＝何もしない）
     */
    static synchronized AlarmEntry fire(Context context, String id, long intentTriggerAt, long nowUtcMs,
                                        Consumer<AlarmEntry> handOff) {
        if (id == null) {
            Log.w(PlatformContract.LOG_TAG, "予約 ID の無い発火を捨てました");
            return null;
        }
        List<AlarmEntry> entries = AlarmStore.load(context);
        int index = indexOf(entries, id);
        if (index == NOT_FOUND) {
            // 取り消しや張り直し（鳴らなかった扱い）の後に届いた配信。控えが正本なので鳴らさない
            Log.i(PlatformContract.LOG_TAG, "目覚まし " + id + " の発火を受けましたが、控えに無いので何もしません");
            return null;
        }
        AlarmEntry entry = entries.get(index);
        if (intentTriggerAt != AlarmScheduler.UNKNOWN_TRIGGER_AT && intentTriggerAt != entry.triggerAtUtcMs) {
            // 配信の途中で同じ ID が別の時刻に予約し直された（新しい予約は AlarmManager が持っている）
            Log.i(PlatformContract.LOG_TAG, "目覚まし " + id + " の古い配信を捨てました（配信 " + intentTriggerAt
                    + "・控え " + entry.triggerAtUtcMs + "）");
            return null;
        }
        if (handOff != null) {
            handOff.accept(entry);
            AlarmEvents.recordFired(context, entry, nowUtcMs);
        } else {
            // 配信は届いたが鳴らせない（前景サービスを起こせなかった）。「鳴った」とは知らせない
            AlarmEvents.recordMissed(context, entry, PlatformContract.MISSED_REASON_START_FAILED);
        }
        entries.remove(index);
        if (!AlarmStore.save(context, entries)) {
            Log.w(PlatformContract.LOG_TAG, "鳴った目覚まし " + id + " を控えから消せませんでした（次の張り直しで鳴らなかった扱いになりえます）");
        }
        // 配信済みの PendingIntent を片付ける（同じ予約なので、新しい予約を外すことは無い）
        AlarmScheduler.disarm(context, id);
        return entry;
    }

    /**
     * 控えから張り直す（BootReceiver から）。まだ先の予約は setAlarmClock で張り直し、過ぎた予約は鳴らさずに
     * alarm.missed を記録して控えから外す（受信機から直接鳴らさない。E-10）。配信の途中の予約には触らない。
     *
     * @param context      :seed_platform の Context
     * @param missedReason 過ぎた予約の理由（PlatformContract.MISSED_REASON_*）
     * @param nowUtcMs     今の時刻
     * @return 結果（控えが空なら全部 0）
     */
    static synchronized RearmResult rearm(Context context, String missedReason, long nowUtcMs) {
        return applyRearm(context, missedReason, nowUtcMs, false);
    }

    /**
     * 起動時の照合（:seed_platform のプロセスが起きたとき。AlarmStartup から。W1-4a）。控えのまだ先の予約のうち、
     * 発火の PendingIntent が無い（FLAG_NO_CREATE で null＝AlarmManager から消えている）ものだけを張り直し、過ぎた予約は
     * 鳴らさずに alarm.missed(device_off) を記録して控えから外す。張ってある予約には触らない。
     *
     * <p>Android 10〜14 は強制停止の後、次の再起動まで BOOT_COMPLETED が届かない（15 以降は停止状態から出たときに届く）。
     * その間に予約が AlarmManager から消えたままになるのを、アプリが次に :seed_platform を起こしたとき（最初の SEED.Platform の
     * 呼び出し・目覚ましの配信など）に直す保険。配信の途中の予約（過ぎてから 60 秒以内で PendingIntent が残っている）には
     * 触らないので、配信でプロセスが起きたときに鳴らす予約を「鳴らなかった」にしない（AlarmRearmPlan）。</p>
     *
     * @param context  :seed_platform の Context
     * @param nowUtcMs 今の時刻
     * @return 結果（控えが空なら全部 0）
     */
    static synchronized RearmResult reconcile(Context context, long nowUtcMs) {
        return applyRearm(context, PlatformContract.MISSED_REASON_DEVICE_OFF, nowUtcMs, true);
    }

    /**
     * 控えから張り直す（rearm と reconcile の共通。lock を持って呼ぶ）。
     *
     * @param context          :seed_platform の Context
     * @param missedReason     過ぎた予約の理由
     * @param nowUtcMs         今の時刻
     * @param skipAlreadyArmed 張ってある（PendingIntent が残っている）予約は張り直さない（照合）
     * @return 結果
     */
    private static RearmResult applyRearm(Context context, String missedReason, long nowUtcMs, boolean skipAlreadyArmed) {
        List<AlarmEntry> entries = AlarmStore.load(context);
        if (entries.isEmpty()) {
            return new RearmResult(0, 0, 0, 0, 0);
        }
        AlarmRearmPlan plan = AlarmRearmPlan.plan(entries, nowUtcMs, id -> AlarmScheduler.isArmed(context, id));

        // ── まだ先の予約を張り直す（許可が無ければ控えに残し、許可が戻ったとき〈permission_changed〉に張る）──
        boolean canExact = AlarmScheduler.canScheduleExact(context);
        int armed = 0;
        int failed = 0;
        for (AlarmEntry entry : plan.toArm) {
            if (skipAlreadyArmed && AlarmScheduler.isArmed(context, entry.id)) {
                continue;
            }
            AlarmScheduler.ArmResult result = canExact
                    ? AlarmScheduler.arm(context, entry)
                    : null;
            if (result != null && result.ok()) {
                armed++;
            } else {
                failed++;
                Log.w(PlatformContract.LOG_TAG, "目覚まし " + entry.id + " を張り直せませんでした（"
                        + (result != null ? result.error + " " + result.detail : PlatformContract.ERROR_EXACT_ALARM_NOT_ALLOWED)
                        + "）。控えには残します");
            }
        }

        // ── 過ぎた予約: 先に記録し（失わない）、それから控えから外す ──
        for (AlarmEntry entry : plan.missed) {
            AlarmEvents.recordMissed(context, entry, missedReason);
            AlarmScheduler.disarm(context, entry.id);
        }
        if (!plan.missed.isEmpty()) {
            List<AlarmEntry> keep = new ArrayList<>(entries);
            keep.removeIf(entry -> containsSameReservation(plan.missed, entry));
            if (!AlarmStore.save(context, keep)) {
                Log.w(PlatformContract.LOG_TAG, "鳴らなかった目覚ましを控えから外せませんでした（次の張り直しで再び記録されえます）");
            }
        }
        return new RearmResult(entries.size(), armed, failed, plan.missed.size(), plan.awaitingDelivery.size());
    }

    /** 一覧の中の ID の位置（無ければ NOT_FOUND）。 */
    private static int indexOf(List<AlarmEntry> entries, String id) {
        for (int i = 0; i < entries.size(); i++) {
            if (entries.get(i).id.equals(id)) {
                return i;
            }
        }
        return NOT_FOUND;
    }

    /** 一覧に同じ予約（ID・予定時刻・受け付けた時刻が同じ）があるか。 */
    private static boolean containsSameReservation(List<AlarmEntry> entries, AlarmEntry target) {
        for (AlarmEntry entry : entries) {
            if (entry.isSameReservation(target)) {
                return true;
            }
        }
        return false;
    }
}
