// ============================================================
//  CorePlatformModule.java — 基盤そのもののモジュール "platform"（:seed_platform。W1-1）
//
//    platform.ping              … 受け取った JSON をそのまま echo に入れて返す＋pid・プロセスの起動からの ms・プロセス名（往復の計測）
//    platform.version           … プロトコルの版・使えるモジュール・端末の API レベル
//    platform.register_callback … メインプロセスの呼び鈴（EventDoorbell）を登録する。未読があればすぐ鳴らす
//    platform.emit_test_event   … 試験イベント platform.test_event を記録して呼び鈴を鳴らす（デバッグ用）
//    platform.poll_events       … 未読の記録を取り出す（呼び鈴を受けたメインプロセスが呼ぶ）
//  デスクトップの模擬（runtime/src/engine/platform/bridge/desktop_sim.rs）の ping / version / emit_test_event と同じ形で答える。
// ============================================================

package com.seedengine.runtime.platform.service;

import android.app.Application;
import android.content.Context;
import android.os.Build;
import android.os.Bundle;
import android.os.IBinder;
import android.os.Process;
import android.os.RemoteException;
import android.os.SystemClock;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.Collection;
import java.util.function.Supplier;

/**
 * モジュール "platform"（状態は EventJournal と EventDoorbellClient が持ち、ここは持たない）。
 */
final class CorePlatformModule implements PlatformModule {

    /** 試験イベントの引数に message が無いときの文言。 */
    private static final String DEFAULT_TEST_MESSAGE = "test";

    /** poll_events の引数に max が無いときの件数。 */
    private static final int DEFAULT_POLL_MAX = 64;

    /** 使えるモジュールの名前（version の返答用。PlatformProvider の表から取る）。 */
    private final Supplier<Collection<String>> moduleNames;

    /**
     * @param moduleNames 使えるモジュールの名前（呼ぶたびに今の表を返すもの）
     */
    CorePlatformModule(Supplier<Collection<String>> moduleNames) {
        this.moduleNames = moduleNames;
    }

    @Override
    public String name() {
        return PlatformContract.MODULE_PLATFORM;
    }

    @Override
    public byte[] handle(Context context, String method, Object request, Bundle extras) {
        switch (method) {
            case PlatformContract.METHOD_PING:
                return ping(request);
            case PlatformContract.METHOD_VERSION:
                return version();
            case PlatformContract.METHOD_REGISTER_CALLBACK:
                return registerCallback(extras);
            case PlatformContract.METHOD_EMIT_TEST_EVENT:
                return emitTestEvent(request);
            case PlatformContract.METHOD_POLL_EVENTS:
                return pollEvents(request);
            default:
                return PlatformJson.errorReply(PlatformContract.ERROR_UNKNOWN_METHOD);
        }
    }

    /** ping: 受け取った JSON をそのまま echo に入れ、pid・プロセスの起動からの ms・プロセス名を添える。 */
    private static byte[] ping(Object request) {
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_ECHO, request);
        PlatformJson.put(fields, PlatformContract.KEY_PID, Process.myPid());
        PlatformJson.put(fields, PlatformContract.KEY_UPTIME_MS,
                SystemClock.elapsedRealtime() - Process.getStartElapsedRealtime());
        PlatformJson.put(fields, PlatformContract.KEY_PROCESS, Application.getProcessName());
        return PlatformJson.okReply(fields);
    }

    /** version: プロトコルの版・使えるモジュール・端末の API レベル。 */
    private byte[] version() {
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_PROTOCOL, PlatformContract.PROTOCOL_VERSION);
        PlatformJson.put(fields, PlatformContract.KEY_MODULES, new JSONArray(moduleNames.get()));
        PlatformJson.put(fields, PlatformContract.KEY_SDK_INT, Build.VERSION.SDK_INT);
        return PlatformJson.okReply(fields);
    }

    /** register_callback: 呼び鈴の相手を登録し、未読があればすぐ鳴らす（エンジンが居ない間の記録を取らせる）。 */
    private static byte[] registerCallback(Bundle extras) {
        IBinder binder = extras != null ? extras.getBinder(PlatformContract.BUNDLE_CALLBACK) : null;
        if (binder == null) {
            return PlatformJson.errorReply(PlatformContract.ERROR_MISSING_CALLBACK);
        }
        try {
            EventDoorbellClient.get().register(binder);
        } catch (RemoteException e) {
            return PlatformJson.errorReply(PlatformContract.ERROR_REMOTE, PlatformJson.describe(e));
        }
        EventJournal journal = EventJournal.get();
        long latestSeq = journal.latestSeq();
        if (journal.hasUnread()) {
            EventDoorbellClient.get().ring(latestSeq);
        }
        Log.i(PlatformContract.LOG_TAG, "エンジンの呼び鈴を登録しました（pid " + Process.myPid() + "・記録の最新 " + latestSeq + "）");
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_PID, Process.myPid());
        PlatformJson.put(fields, PlatformContract.KEY_LATEST_SEQ, latestSeq);
        return PlatformJson.okReply(fields);
    }

    /** emit_test_event: 試験イベントを記録して呼び鈴を鳴らす。 */
    private static byte[] emitTestEvent(Object request) {
        String message = PlatformJson.asObject(request).optString(PlatformContract.KEY_MESSAGE, DEFAULT_TEST_MESSAGE);
        JSONObject data = new JSONObject();
        PlatformJson.put(data, PlatformContract.KEY_MESSAGE, message);
        PlatformJson.put(data, PlatformContract.KEY_PID, Process.myPid());
        long seq = EventJournal.get().append(PlatformContract.EVENT_TEST, data);
        boolean rang = EventDoorbellClient.get().ring(seq);
        Log.i(PlatformContract.LOG_TAG, "試験イベントを記録しました（seq " + seq + "・呼び鈴 " + (rang ? "鳴らした" : "相手なし") + "）");
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_SEQ, seq);
        PlatformJson.put(fields, PlatformContract.KEY_DOORBELL, rang);
        return PlatformJson.okReply(fields);
    }

    /** poll_events: 未読を古い順に取り出す（取り出したものは既読）。 */
    private static byte[] pollEvents(Object request) {
        int max = PlatformJson.asObject(request).optInt(PlatformContract.KEY_MAX, DEFAULT_POLL_MAX);
        EventJournal.Batch batch = EventJournal.get().pollUnread(max);
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_EVENTS, new JSONArray(batch.events));
        PlatformJson.put(fields, PlatformContract.KEY_HAS_MORE, batch.hasMore);
        PlatformJson.put(fields, PlatformContract.KEY_LATEST_SEQ, batch.latestSeq);
        return PlatformJson.okReply(fields);
    }
}
