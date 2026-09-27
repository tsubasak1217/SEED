// ============================================================
//  RingRegistry.java — 鳴動の状態の正本（今鳴っている 1 つと待ち行列。:seed_platform のプロセスの中だけ。W1-4a）
//
//  【なぜ static な置き場か】
//  鳴動の状態を読む・変える者は 3 つとも同じ :seed_platform のプロセスにいる:
//    AlarmReceiver（UI スレッド）… 配信された予約を渡す（offer）
//    AlarmModule（Binder のスレッド）… alarm.get_ringing / alarm.stop_ringing
//    RingService（UI スレッド）… 状態に合わせて音・振動・通知を出し入れする・安全弁で止める
//  bindService の往復を挟むほどの隔たりは無いので、プロセスで 1 つの置き場を synchronized で守る（読み書きは値の出し入れだけで、
//  音・ファイル・Binder には触らない＝lock を短く持つ）。記録（EventRecorder）は lock の外で呼び出し側が行う。
//  プロセスが死ねばメモリの状態は消える。W1-7 から、変えた者（RingControl・RingService・RingRecovery）が lock の外で
//  写し（snapshot。変えるたびに増える版つき）を ringing.json へ書き（RingStateStore）、見張り（RingWatchdog）の発火で
//  restore へ読み戻して鳴らし直す（RingRecovery）。
//
//  【待ち行列（Flutter 版の「後の予約を黙って捨てる」を塞ぐ）】
//  鳴動中に別の予約が配信されたら捨てずに待たせ（alarm.queued）、今の鳴動が止まったら（停止・安全弁）先頭を繰り上げて鳴らす。
//  同じ ID が待ち行列にあれば置き換える（同じ ID の予約は置き換え、の約束と同じ）。
//  規則は Rust の模擬 runtime/src/engine/platform/bridge/desktop_sim/ring_state.rs と同じ（変えるときは両方）。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import com.seedengine.runtime.platform.service.alarm.AlarmEntry;

import java.util.ArrayList;
import java.util.List;

/**
 * 鳴動の状態（static のみ。プロセスで 1 つ）。
 */
final class RingRegistry {

    private RingRegistry() {
    }

    /** 最初に払い出す鳴動の通し番号。 */
    private static final long FIRST_SERIAL = 1L;

    /** 待ち行列の中に見つからないときの位置。 */
    private static final int NOT_FOUND = -1;

    /** 待ち行列の 1 件（配信を受けた時刻つき）。 */
    private static final class Waiting {
        /** 予約。 */
        final AlarmEntry entry;
        /** 配信を受けた時刻（UTC の epoch ミリ秒）。 */
        final long firedAtUtcMs;

        Waiting(AlarmEntry entry, long firedAtUtcMs) {
            this.entry = entry;
            this.firedAtUtcMs = firedAtUtcMs;
        }
    }

    /** offer の結果。 */
    static final class Offer {
        /** 鳴り始めた鳴動（待たせたなら null）。 */
        final RingSession started;
        /** 待たせたときの、今鳴っている予約の ID（鳴り始めたなら null）。 */
        final String waitingFor;

        private Offer(RingSession started, String waitingFor) {
            this.started = started;
            this.waitingFor = waitingFor;
        }

        /** @return 待ち行列に入れたなら true */
        boolean queued() {
            return started == null;
        }
    }

    /** 止めた結果（何も止めなかったら stopped が null）。 */
    static final class Stop {
        /** 止めた予約（鳴っていたか待ち行列にいたもの。何も止めなければ null）。 */
        final AlarmEntry stopped;
        /** 止めたのが鳴動中のものだったか（false なら待ち行列から外しただけ）。 */
        final boolean wasRinging;
        /** 繰り上がって鳴り始めた鳴動（無ければ null）。 */
        final RingSession next;

        private Stop(AlarmEntry stopped, boolean wasRinging, RingSession next) {
            this.stopped = stopped;
            this.wasRinging = wasRinging;
            this.next = next;
        }

        /** @return 何かを止めたなら true */
        boolean any() {
            return stopped != null;
        }
    }

    /** 何も止めなかった結果（共有してよい）。 */
    private static final Stop NOTHING = new Stop(null, false, null);

    /** 今鳴っている鳴動（無ければ null）。 */
    private static RingSession current;

    /** 待ち行列（配信の順）。 */
    private static final List<Waiting> queue = new ArrayList<>();

    /** 次に払い出す鳴動の通し番号。 */
    private static long nextSerial = FIRST_SERIAL;

    /** 状態の版（変えるたびに 1 増える。写し〈snapshot〉の新旧を比べる。W1-7）。 */
    private static long version;

    /** 最初の鳴動の「鳴らし直した回数」。 */
    private static final int FIRST_RESTORE_COUNT = 0;

    /**
     * 配信された予約を渡す。何も鳴っていなければ鳴り始め、鳴っていれば待ち行列へ入れる（同じ ID は置き換え）。
     *
     * @param entry        予約
     * @param firedAtUtcMs 配信を受けた時刻
     * @param nowUtcMs     今の時刻（鳴り始めの時刻にする）
     * @return 結果
     */
    static synchronized Offer offer(AlarmEntry entry, long firedAtUtcMs, long nowUtcMs) {
        version++;
        if (current == null) {
            current = newSession(entry, firedAtUtcMs, nowUtcMs);
            return new Offer(current, null);
        }
        int index = indexInQueue(entry.id);
        Waiting waiting = new Waiting(entry, firedAtUtcMs);
        if (index == NOT_FOUND) {
            queue.add(waiting);
        } else {
            queue.set(index, waiting);
        }
        return new Offer(null, current.entry.id);
    }

    /**
     * 前のプロセスの鳴動を戻す（見張りの発火。RingRecovery）。何も鳴っておらず待ち行列も空のときだけ戻す。
     * 戻した鳴動は新しい通し番号で、鳴り始めの時刻は写しのまま（安全弁の時刻を延ばさない）。
     *
     * @param restored 鳴らす鳴動（restoreCount は呼び出し側が増やした値）
     * @param waiting  待ち行列（配信の順）
     * @return 戻した鳴動（既に何かが鳴っている・待っていれば null）
     */
    static synchronized RingSession restore(RingSnapshot.Current restored, List<RingSnapshot.Waiting> waiting) {
        if (current != null || !queue.isEmpty()) {
            return null;
        }
        version++;
        current = new RingSession(nextSerial++, restored.entry, restored.firedAtUtcMs, restored.startedAtUtcMs,
                restored.restoreCount);
        for (RingSnapshot.Waiting row : waiting) {
            queue.add(new Waiting(row.entry, row.firedAtUtcMs));
        }
        return current;
    }

    /**
     * 今の状態の写し（版つき。RingStateStore が書く）。
     *
     * @return 写し
     */
    static synchronized RingSnapshot snapshot() {
        RingSnapshot.Current head = current == null ? null
                : new RingSnapshot.Current(current.entry, current.firedAtUtcMs, current.startedAtUtcMs, current.restoreCount);
        List<RingSnapshot.Waiting> rows = new ArrayList<>();
        for (Waiting waiting : queue) {
            rows.add(new RingSnapshot.Waiting(waiting.entry, waiting.firedAtUtcMs));
        }
        return new RingSnapshot(version, head, rows);
    }

    /**
     * ID で止める（アプリの alarm.stop_ringing）。空文字なら今鳴っているもの。待ち行列の予約も ID で外せる。
     *
     * @param idOrEmpty 予約の ID（空文字 = 今鳴っているもの）
     * @param nowUtcMs  今の時刻（繰り上がった鳴動の鳴り始め）
     * @return 結果
     */
    static synchronized Stop stop(String idOrEmpty, long nowUtcMs) {
        if (current != null && (idOrEmpty.isEmpty() || current.entry.id.equals(idOrEmpty))) {
            return stopCurrent(nowUtcMs);
        }
        int index = idOrEmpty.isEmpty() ? NOT_FOUND : indexInQueue(idOrEmpty);
        if (index == NOT_FOUND) {
            return NOTHING;
        }
        version++;
        return new Stop(queue.remove(index).entry, false, null);
    }

    /**
     * 通し番号で止める（安全弁の時間切れ）。その鳴動が今も鳴っているときだけ止める（入れ替わった後の古い知らせは無視）。
     *
     * @param serial   鳴動の通し番号
     * @param nowUtcMs 今の時刻
     * @return 結果
     */
    static synchronized Stop stopSession(long serial, long nowUtcMs) {
        if (current == null || current.serial != serial) {
            return NOTHING;
        }
        return stopCurrent(nowUtcMs);
    }

    /**
     * 全部やめる（鳴らし続けられなくなったとき）。今鳴っているもの・待ち行列の順に返す。
     *
     * @return やめた予約
     */
    static synchronized List<AlarmEntry> abortAll() {
        List<AlarmEntry> aborted = new ArrayList<>();
        if (current != null || !queue.isEmpty()) {
            version++;
        }
        if (current != null) {
            aborted.add(current.entry);
            current = null;
        }
        for (Waiting waiting : queue) {
            aborted.add(waiting.entry);
        }
        queue.clear();
        return aborted;
    }

    /**
     * 今鳴っている鳴動。
     *
     * @return 鳴動（無ければ null）
     */
    static synchronized RingSession current() {
        return current;
    }

    /**
     * 待ち行列の件数（ログ用）。
     *
     * @return 件数
     */
    static synchronized int queuedCount() {
        return queue.size();
    }

    /** 今鳴っているものを止め、待ち行列の先頭を繰り上げる（lock を持って呼ぶ）。 */
    private static Stop stopCurrent(long nowUtcMs) {
        version++;
        AlarmEntry stopped = current.entry;
        current = null;
        if (!queue.isEmpty()) {
            Waiting head = queue.remove(0);
            current = newSession(head.entry, head.firedAtUtcMs, nowUtcMs);
        }
        return new Stop(stopped, true, current);
    }

    /** 新しい鳴動を作る（lock を持って呼ぶ）。 */
    private static RingSession newSession(AlarmEntry entry, long firedAtUtcMs, long nowUtcMs) {
        return new RingSession(nextSerial++, entry, firedAtUtcMs, nowUtcMs, FIRST_RESTORE_COUNT);
    }

    /** 待ち行列の中の ID の位置（lock を持って呼ぶ。無ければ NOT_FOUND）。 */
    private static int indexInQueue(String id) {
        for (int i = 0; i < queue.size(); i++) {
            if (queue.get(i).entry.id.equals(id)) {
                return i;
            }
        }
        return NOT_FOUND;
    }
}
