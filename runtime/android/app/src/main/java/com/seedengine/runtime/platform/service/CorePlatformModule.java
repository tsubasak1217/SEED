// ============================================================
//  CorePlatformModule.java — 基盤そのもののモジュール "platform"（:seed_platform。W1-1・W1-3 で paths を追加）
//
//    platform.ping              … 受け取った JSON をそのまま echo に入れて返す＋pid・プロセスの起動からの ms・プロセス名（往復の計測）
//    platform.version           … プロトコルの版・使えるモジュール・端末の API レベル
//    platform.register_callback … メインプロセスの呼び鈴（EventDoorbell）を登録する。未読があればすぐ鳴らす
//    platform.emit_test_event   … 試験イベント platform.test_event を記録して呼び鈴を鳴らす（デバッグ用）
//    platform.poll_events       … 未読の記録を取り出す（呼び鈴を受けたメインプロセスが呼ぶ）
//    platform.paths             … 端末保護ストレージの置き場の絶対パス（目覚ましの音源の書き出し先 sounds_dir。W1-3）
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

import java.io.File;

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
                return registerCallback(context, extras);
            case PlatformContract.METHOD_EMIT_TEST_EVENT:
                return emitTestEvent(context, request);
            case PlatformContract.METHOD_POLL_EVENTS:
                return pollEvents(context, request);
            case PlatformContract.METHOD_PATHS:
                return paths(context);
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
    private static byte[] registerCallback(Context context, Bundle extras) {
        IBinder binder = extras != null ? extras.getBinder(PlatformContract.BUNDLE_CALLBACK) : null;
        if (binder == null) {
            return PlatformJson.errorReply(PlatformContract.ERROR_MISSING_CALLBACK);
        }
        try {
            EventDoorbellClient.get().register(binder);
        } catch (RemoteException e) {
            return PlatformJson.errorReply(PlatformContract.ERROR_REMOTE, PlatformJson.describe(e));
        }
        EventJournal journal = EventJournal.get(context);
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
    private static byte[] emitTestEvent(Context context, Object request) {
        String message = PlatformJson.asObject(request).optString(PlatformContract.KEY_MESSAGE, DEFAULT_TEST_MESSAGE);
        JSONObject data = new JSONObject();
        PlatformJson.put(data, PlatformContract.KEY_MESSAGE, message);
        PlatformJson.put(data, PlatformContract.KEY_PID, Process.myPid());
        EventRecorder.Recorded recorded = EventRecorder.record(context, PlatformContract.EVENT_TEST, data);
        Log.i(PlatformContract.LOG_TAG, "試験イベントを記録しました（seq " + recorded.seq + "・呼び鈴 "
                + (recorded.doorbellRang ? "鳴らした" : "相手なし") + "）");
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_SEQ, recorded.seq);
        PlatformJson.put(fields, PlatformContract.KEY_DOORBELL, recorded.doorbellRang);
        return PlatformJson.okReply(fields);
    }

    /** poll_events: 未読を古い順に取り出す（取り出したものは既読）。 */
    private static byte[] pollEvents(Context context, Object request) {
        int max = PlatformJson.asObject(request).optInt(PlatformContract.KEY_MAX, DEFAULT_POLL_MAX);
        EventJournal.Batch batch = EventJournal.get(context).pollUnread(max);
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_EVENTS, new JSONArray(batch.events));
        PlatformJson.put(fields, PlatformContract.KEY_HAS_MORE, batch.hasMore);
        PlatformJson.put(fields, PlatformContract.KEY_LATEST_SEQ, batch.latestSeq);
        return PlatformJson.okReply(fields);
    }

    /**
     * paths: 端末保護ストレージの置き場の絶対パス（W1-3）。
     *
     * <p>メインプロセス（エンジンの Android の糊）が目覚ましの音源を書き出す先（sounds_dir）を知るために 1 回呼ぶ。
     * :seed_platform は Java だけで pak を読めないので、assets:// の音はメインプロセスが実ファイルにして渡す。
     * 同じアプリの UID なので、メインプロセスもこのフォルダへ書ける。返す前にフォルダを作っておく。</p>
     */
    private static byte[] paths(Context context) {
        File filesDir = PlatformStorage.deviceProtectedFilesDir(context);
        File soundsDir = PlatformStorage.soundsDir(context);
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_DEVICE_PROTECTED_FILES_DIR, filesDir.getAbsolutePath());
        PlatformJson.put(fields, PlatformContract.KEY_SOUNDS_DIR, soundsDir.getAbsolutePath());
        return PlatformJson.okReply(fields);
    }
}
