// ============================================================
//  EventJournal.java — :seed_platform のイベントの記録（未読をエンジンが取りに来る。W1-1 の骨組み）
//
//  【役割】
//  :seed_platform で起きたこと（W1-3 以降は目覚ましの発火・停止・通知の操作。W1-1 は試験イベントだけ）を通し番号付きで記録し、
//  メインプロセス（エンジン）が platform.poll_events で未読を取り出す。記録が正本で、Binder の呼び鈴（EventDoorbellClient）は
//  「未読がある」の知らせにすぎない（呼び鈴を取りこぼしても、次の呼び鈴か次の接続の登録で未読が取れる）。
//
//  【W1-1 の割り切り（W1-3・W1-4 で変える。docs/backlog.md）】
//    ・記録はメモリの中だけ（:seed_platform のプロセスが死ぬと消える）。目覚ましの記録は端末保護ストレージへ書く（W1-3）
//    ・取り出した時点で既読にする（取り出した直後にメインプロセスが死ぬと、そのイベントはスクリプトへ届かない）。
//      エンジンが受け取りを確かめてから既読にする形（ack）は W1-4 で決める
//    ・既読の記録はすぐ捨てる。未読が上限を超えたら古いものから捨て、捨てた数を数える
// ============================================================

package com.seedengine.runtime.platform.service;

import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

import org.json.JSONObject;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;

/**
 * イベントの記録（:seed_platform のプロセスで 1 つ。どの Binder のスレッドからも呼べるよう同期する）。
 */
final class EventJournal {

    /** 溜めておく未読の記録の上限（件数）。エンジンが長く居ないときの蓋。 */
    private static final int MAX_RECORDS = 256;

    /** 最初の通し番号（0 はメインプロセスの中の知らせ＝PlatformContract.LOCAL_EVENT_SEQ に使うので 1 から）。 */
    private static final long FIRST_SEQ = 1;

    /** プロセスで 1 つの記録。 */
    private static final EventJournal INSTANCE = new EventJournal();

    /** 記録 1 件。 */
    private static final class Record {
        /** 通し番号。 */
        final long seq;
        /** イベント（{"name","seq","time_ms","data"}。作った後は変えない）。 */
        final JSONObject event;

        Record(long seq, JSONObject event) {
            this.seq = seq;
            this.event = event;
        }
    }

    /** 取り出しの結果。 */
    static final class Batch {
        /** 取り出したイベント（古い順）。 */
        final List<JSONObject> events;
        /** まだ未読が残っているか。 */
        final boolean hasMore;
        /** 記録の最新の通し番号。 */
        final long latestSeq;

        Batch(List<JSONObject> events, boolean hasMore, long latestSeq) {
            this.events = events;
            this.hasMore = hasMore;
            this.latestSeq = latestSeq;
        }
    }

    /** 未読の記録（古い順）。 */
    private final ArrayDeque<Record> records = new ArrayDeque<>();

    /** 次に払い出す通し番号。 */
    private long nextSeq = FIRST_SEQ;

    /** 上限のために読まれずに捨てた件数（診断用）。 */
    private long droppedUnread;

    private EventJournal() {
    }

    /**
     * プロセスで 1 つの記録を返す。
     *
     * @return 記録
     */
    static EventJournal get() {
        return INSTANCE;
    }

    /**
     * 1 件記録する。
     *
     * @param name 名前（PlatformContract.EVENT_*）
     * @param data 中身
     * @return 付けた通し番号
     */
    synchronized long append(String name, JSONObject data) {
        long seq = nextSeq++;
        records.addLast(new Record(seq, PlatformJson.event(name, seq, System.currentTimeMillis(), data)));
        while (records.size() > MAX_RECORDS) {
            records.removeFirst();
            droppedUnread++;
            Log.w(PlatformContract.LOG_TAG, "未読の記録が上限（" + MAX_RECORDS + " 件）を超えたので古いものを捨てました（累計 "
                    + droppedUnread + " 件）");
        }
        return seq;
    }

    /**
     * 未読を古い順に最大 max 件取り出す（取り出したものは既読にして捨てる）。
     *
     * @param max 最大の件数（1 未満は 1）
     * @return 取り出した結果
     */
    synchronized Batch pollUnread(int max) {
        int limit = Math.max(1, max);
        List<JSONObject> taken = new ArrayList<>();
        while (!records.isEmpty() && taken.size() < limit) {
            taken.add(records.removeFirst().event);
        }
        return new Batch(taken, !records.isEmpty(), latestSeqLocked());
    }

    /**
     * 未読があるか。
     *
     * @return あれば true
     */
    synchronized boolean hasUnread() {
        return !records.isEmpty();
    }

    /**
     * 記録の最新の通し番号（まだ何も記録していなければ 0）。
     *
     * @return 通し番号
     */
    synchronized long latestSeq() {
        return latestSeqLocked();
    }

    /** 最新の通し番号（同期の中から）。 */
    private long latestSeqLocked() {
        return nextSeq - FIRST_SEQ;
    }
}
