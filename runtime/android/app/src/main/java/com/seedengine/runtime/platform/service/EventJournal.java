// ============================================================
//  EventJournal.java — :seed_platform のイベントの記録（未読をエンジンが取りに来る。W1-1 の骨組み・W1-3 で永続化）
//
//  【役割】
//  :seed_platform で起きたこと（目覚ましの発火・鳴らなかった・張り直し。W1-4 以降は鳴動・停止・通知の操作）を
//  通し番号付きで記録し、メインプロセス（エンジン）が platform.poll_events で未読を取り出す。記録が正本で、
//  Binder の呼び鈴（EventDoorbellClient）は「未読がある」の知らせにすぎない（呼び鈴を取りこぼしても、次の呼び鈴か
//  次の接続の登録で未読が取れる）。記録するのは EventRecorder（記録＋呼び鈴）を通す。
//
//  【永続化（W1-3）】
//  目覚ましの記録は取りこぼせない（エンジンが居ない朝に鳴った・再起動の間に鳴らなかった、を次の起動でアプリへ渡す）ので、
//  未読の記録を端末保護ストレージの seed_platform/journal.json へ書く（PlatformStorage。ロック解除の前でも書ける）。
//  足す・取り出すたびに全体を原子的に書き直す（DurableFile。未読は最大 MAX_RECORDS 件なので小さい）。
//  書けなければメモリの中の記録で続け、警告を残す（プロセスが生きている間はエンジンへ届く）。
//  形: {"format_version":1,"next_seq":次の番号,"dropped":捨てた件数,"events":[{"name","seq","time_ms","data"},…]}
//
//  【まだの割り切り（W1-4 で決める。docs/backlog.md）】
//    ・取り出した時点で既読にする（取り出した直後にメインプロセスが死ぬと、そのイベントはスクリプトへ届かない）。
//      エンジンが受け取りを確かめてから既読にする形（ack）は W1-4
//    ・既読の記録はすぐ捨てる。未読が上限を超えたら古いものから捨て、捨てた数を数える
// ============================================================

package com.seedengine.runtime.platform.service;

import android.content.Context;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.io.IOException;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;

/**
 * イベントの記録（:seed_platform のプロセスで 1 つ。どの Binder のスレッド・受信機からも呼べるよう同期する）。
 */
final class EventJournal {

    /** 溜めておく未読の記録の上限（件数）。エンジンが長く居ないときの蓋。 */
    private static final int MAX_RECORDS = 256;

    /** 最初の通し番号（0 はメインプロセスの中の知らせ＝PlatformContract.LOCAL_EVENT_SEQ に使うので 1 から）。 */
    private static final long FIRST_SEQ = 1;

    /** 記録のファイルの書式の版（形を変えたら上げる。違う版は読まずに空から始める）。 */
    private static final int FILE_FORMAT_VERSION = 1;

    // ── 記録のファイルの欄の名前（:seed_platform の中だけで読み書きする）──
    private static final String FILE_KEY_FORMAT_VERSION = "format_version";
    private static final String FILE_KEY_NEXT_SEQ = "next_seq";
    private static final String FILE_KEY_DROPPED = "dropped";
    private static final String FILE_KEY_EVENTS = "events";

    /** プロセスで 1 つの記録（最初に要るときに、置き場を決めてファイルから読む）。 */
    private static EventJournal instance;

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

    /** 記録のファイル（端末保護ストレージ）。 */
    private final File file;

    /** 未読の記録（古い順）。 */
    private final ArrayDeque<Record> records = new ArrayDeque<>();

    /** 次に払い出す通し番号。 */
    private long nextSeq = FIRST_SEQ;

    /** 上限のために読まれずに捨てた件数（診断用。ファイルにも残す）。 */
    private long droppedUnread;

    private EventJournal(File file) {
        this.file = file;
    }

    /**
     * プロセスで 1 つの記録を返す（最初の 1 回だけファイルから読む）。
     *
     * @param context どの Context でもよい（置き場を決めるのに使う）
     * @return 記録
     */
    static synchronized EventJournal get(Context context) {
        if (instance == null) {
            EventJournal journal = new EventJournal(PlatformStorage.journalFile(context));
            journal.load();
            instance = journal;
        }
        return instance;
    }

    /**
     * 1 件記録する（ファイルへも書く）。
     *
     * @param name 名前（PlatformContract.EVENT_*）
     * @param data 中身
     * @return 付けた通し番号
     */
    synchronized long append(String name, JSONObject data) {
        long seq = nextSeq++;
        records.addLast(new Record(seq, PlatformJson.event(name, seq, System.currentTimeMillis(), data)));
        trimToLimit();
        persist();
        return seq;
    }

    /**
     * 未読を古い順に最大 max 件取り出す（取り出したものは既読にして捨てる。ファイルからも消す）。
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
        if (!taken.isEmpty()) {
            persist();
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

    /** 未読が上限を超えていれば古いものから捨てる（同期の中から）。 */
    private void trimToLimit() {
        while (records.size() > MAX_RECORDS) {
            records.removeFirst();
            droppedUnread++;
            Log.w(PlatformContract.LOG_TAG, "未読の記録が上限（" + MAX_RECORDS + " 件）を超えたので古いものを捨てました（累計 "
                    + droppedUnread + " 件）");
        }
    }

    /** 記録をファイルへ書く（同期の中から。書けなければ警告してメモリの記録で続ける）。 */
    private void persist() {
        JSONArray events = new JSONArray();
        for (Record record : records) {
            events.put(record.event);
        }
        JSONObject root = new JSONObject();
        PlatformJson.put(root, FILE_KEY_FORMAT_VERSION, FILE_FORMAT_VERSION);
        PlatformJson.put(root, FILE_KEY_NEXT_SEQ, nextSeq);
        PlatformJson.put(root, FILE_KEY_DROPPED, droppedUnread);
        PlatformJson.put(root, FILE_KEY_EVENTS, events);
        try {
            DurableFile.writeAtomically(file, PlatformJson.utf8(root.toString()));
        } catch (IOException e) {
            Log.w(PlatformContract.LOG_TAG, "記録をファイルへ書けませんでした（メモリの中で続けます）: " + PlatformJson.describe(e));
        }
    }

    /** ファイルから読む（get の中から 1 回。無い・壊れている・版が違うなら空から始める）。 */
    private void load() {
        byte[] bytes;
        try {
            bytes = DurableFile.readOrNull(file);
        } catch (IOException e) {
            Log.w(PlatformContract.LOG_TAG, "記録のファイルを読めませんでした（空から始めます）: " + PlatformJson.describe(e));
            return;
        }
        if (bytes == null) {
            return;
        }
        try {
            JSONObject root = new JSONObject(PlatformJson.text(bytes));
            int version = root.optInt(FILE_KEY_FORMAT_VERSION, 0);
            if (version != FILE_FORMAT_VERSION) {
                Log.w(PlatformContract.LOG_TAG, "記録のファイルの版が違うので読みません（" + version + "）。空から始めます");
                return;
            }
            JSONArray events = root.optJSONArray(FILE_KEY_EVENTS);
            long maxSeq = FIRST_SEQ - 1;
            int count = events != null ? events.length() : 0;
            for (int i = 0; i < count; i++) {
                JSONObject event = events.optJSONObject(i);
                if (event == null) {
                    continue;
                }
                long seq = event.optLong(PlatformContract.KEY_SEQ, FIRST_SEQ - 1);
                records.addLast(new Record(seq, event));
                maxSeq = Math.max(maxSeq, seq);
            }
            // 通し番号は戻さない（ファイルの next_seq と、読んだ記録の最大＋1 の大きいほう）
            nextSeq = Math.max(root.optLong(FILE_KEY_NEXT_SEQ, FIRST_SEQ), maxSeq + 1);
            droppedUnread = root.optLong(FILE_KEY_DROPPED, 0);
            trimToLimit();
            Log.i(PlatformContract.LOG_TAG, "記録をファイルから読みました（未読 " + records.size() + " 件・次の番号 " + nextSeq + "）");
        } catch (JSONException e) {
            records.clear();
            Log.w(PlatformContract.LOG_TAG, "記録のファイルが壊れていたので空から始めます: " + PlatformJson.describe(e));
        }
    }
}
