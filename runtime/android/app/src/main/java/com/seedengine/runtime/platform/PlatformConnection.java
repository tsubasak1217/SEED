// ============================================================
//  PlatformConnection.java — メインプロセスから :seed_platform の PlatformProvider への接続（W1-1）
//
//  【持ち続ける】
//  ContentProviderClient を 1 つ持ち続けて call する（毎回 ContentResolver.call するより速い。W1-0 の実測で
//  0.36〜0.51 ms 対 0.64〜0.87 ms）。**不安定な（unstable）取得**を使う: 安定な取得（acquireContentProviderClient）は、
//  プロバイダのプロセスが死ぬと依存するプロセス（＝ゲームの本体）までシステムに片付けられる。unstable ならこちらは生き残り、
//  DeadObjectException で死を知って、閉じて取り直せばよい（AOSP の ContentResolver.acquireUnstableContentProviderClient の
//  説明のとおり。docs/android.md §25）。
//
//  【つなぐ時機（描画のスレッドで待たない）】
//  :seed_platform のプロセスは最初の接続まで起動しない（使わないゲームには影響しない）。起動の待ちは約 120 ms（W1-0 で 123 ms）
//  あり、スクリプト（＝描画のスレッド）から来る最初の呼び出しで同期に待つとフレームが止まる。そこで:
//    ・つながっていないときの呼び出しは、背面のスレッド（SEEDPlatform）で接続を始めて、すぐ {"ok":false,"error":"connecting"} を返す
//    ・つながったら platform.connected のイベントをエンジンへ送る（スクリプトはそれを待って呼び直す）
//    ・待ってよい呼び出し元（デバッグの受信機など、描画のスレッドでないもの）は mayBlock=true で接続を待てる
//  つなぐときに、記録の知らせを受ける Binder（EventDoorbell）を platform.register_callback で登録する。
//
//  【知らせ（イベント）の受け取り】
//  :seed_platform が記録を足すと、登録した Binder へ oneway で呼び鈴が来る → 背面のスレッドで platform.poll_events を呼んで
//  未読を取り出す → 1 件ずつ EventSink（SeedPlatform.deliverEvent → nativeOnPlatformEvent）へ渡す。
//  接続と取り出しは同じ 1 本のスレッドで順に行うので、イベントの順序が入れ替わらない。
//
//  【スレッド】
//  invoke は呼び出し元のスレッド（JNI のエンジンのスレッド・デバッグの受信機のスレッド）で動く。client と状態は lock で守る。
//  lock を持ったまま Binder の呼び出し（温まっていれば 1 ms 未満）をするが、:seed_platform は呼び鈴を oneway で送るので、
//  相手を待たせる向きの呼び出しは無く、行き詰まらない。
// ============================================================

package com.seedengine.runtime.platform;

import android.content.ContentProviderClient;
import android.content.Context;
import android.os.Bundle;
import android.os.DeadObjectException;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.RemoteException;
import android.os.SystemClock;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * :seed_platform への接続（プロセスで 1 つ。SeedPlatform が持つ）。
 */
final class PlatformConnection implements EventDoorbell.Listener {

    /** 届いたイベント（UTF-8 の JSON）の渡し先。 */
    interface EventSink {
        /**
         * イベントを 1 件渡す（背面のスレッドから呼ばれる）。
         *
         * @param eventJsonUtf8 イベントの JSON（{"name","seq","time_ms","data"}）
         */
        void deliver(byte[] eventJsonUtf8);
    }

    /** 接続の状態。 */
    private enum State {
        /** つながっていない（最初・プロセスの死の後・接続の失敗の後）。 */
        DISCONNECTED,
        /** 背面のスレッドでつないでいる途中。 */
        CONNECTING,
        /** つながっている（client がある）。 */
        CONNECTED,
    }

    /** 1 回の invoke の中で「呼ぶ（→ 死んでいたらつなぎ直して）呼ぶ」を試す回数。 */
    private static final int MAX_CALL_ATTEMPTS = 2;

    /** 待ってよい呼び出し元が接続を待つ上限（ミリ秒）。冷えた起動は約 120 ms。重い端末の余裕を大きく見る。 */
    static final long BLOCKING_CONNECT_TIMEOUT_MS = 10_000L;

    /** platform.poll_events 1 回で取り出す最大の件数。 */
    private static final int POLL_BATCH_MAX = 64;

    /** 呼び鈴 1 回ぶんの取り出しの回数の上限（これを超えて残っていれば、自分で呼び鈴を鳴らし直して続きを後に回す）。 */
    private static final int MAX_POLL_ROUNDS = 16;

    /** 背面のスレッドの名前（接続と取り出し）。 */
    private static final String WORKER_THREAD_NAME = "SEEDPlatform";

    /** ナノ秒 → ミリ秒。 */
    private static final long NANOS_PER_MILLI = 1_000_000L;

    /** アプリの Context（Activity を持たない）。 */
    private final Context appContext;

    /** :seed_platform のプロバイダの authority。 */
    private final String authority;

    /** イベントの渡し先。 */
    private final EventSink sink;

    /** 記録の知らせを受ける Binder（接続のたびに登録する。プロセスの間ずっと同じもの）。 */
    private final EventDoorbell doorbell = new EventDoorbell(this);

    /** client・state・connectDone・lastConnectFailure を守る。 */
    private final Object lock = new Object();

    /** 背面のスレッドの作成を守る（lock の後に取る。逆順には取らない）。 */
    private final Object workerLock = new Object();

    /** 取り出しを背面のスレッドへ出したが、まだ始まっていない（呼び鈴が続いても 1 回にまとめる）。 */
    private final AtomicBoolean pollScheduled = new AtomicBoolean(false);

    /** つながっているときの client（lock で守る）。 */
    private ContentProviderClient client;

    /** 接続の状態（lock で守る）。 */
    private State state = State.DISCONNECTED;

    /** 接続の途中なら、その完了で開く門（lock で守る）。 */
    private CountDownLatch connectDone;

    /** 最後の接続の失敗の理由（lock で守る。つながったら null）。 */
    private String lastConnectFailure;

    /** 背面のスレッドの Handler（workerLock で守る。最初に使うときに作る）。 */
    private Handler worker;

    /**
     * @param appContext アプリの Context
     * @param sink       イベントの渡し先
     */
    PlatformConnection(Context appContext, EventSink sink) {
        this.appContext = appContext;
        this.authority = PlatformContract.authority(appContext);
        this.sink = sink;
    }

    /**
     * 命令を 1 つ送り、返答を返す（例外を投げない）。
     *
     * @param module   モジュールの名前
     * @param method   メソッドの名前
     * @param json     引数の JSON（UTF-8。null は {}）
     * @param mayBlock つながっていないとき、接続を待ってよいか（描画のスレッドからは false）
     * @return 返答の JSON（UTF-8）。失敗も {"ok":false,"error":…}
     */
    byte[] invoke(String module, String method, byte[] json, boolean mayBlock) {
        if (!PlatformContract.isValidName(module) || !PlatformContract.isValidName(method)) {
            return PlatformJson.errorReply(PlatformContract.ERROR_INVALID_NAME);
        }
        String providerMethod = PlatformContract.providerMethod(module, method);
        Bundle extras = requestExtras(json);
        for (int attempt = 0; attempt < MAX_CALL_ATTEMPTS; attempt++) {
            boolean providerDied = false;
            CountDownLatch pending;
            synchronized (lock) {
                if (state == State.CONNECTED) {
                    try {
                        return replyBytes(client.call(providerMethod, null, extras));
                    } catch (DeadObjectException e) {
                        // :seed_platform が居なくなった。閉じて、つなぎ直しを始める（下）。
                        // 居なくなった知らせは、つなぎ直しの知らせより前に並ぶよう、先に背面のスレッドへ出す
                        // （イベントの渡し先を呼ぶのは常に SEEDPlatform スレッド。JNI の呼び出しの途中のこのスレッドからは呼ばない）
                        dropClientLocked();
                        providerDied = true;
                        postLocalEvent(PlatformContract.EVENT_DISCONNECTED);
                    } catch (RemoteException e) {
                        dropClientLocked();
                        return PlatformJson.errorReply(PlatformContract.ERROR_REMOTE, PlatformJson.describe(e));
                    } catch (RuntimeException e) {
                        return PlatformJson.errorReply(PlatformContract.ERROR_INTERNAL, PlatformJson.describe(e));
                    }
                }
                pending = startConnectLocked();
            }
            if (providerDied) {
                Log.w(PlatformContract.LOG_TAG, ":seed_platform が居なくなりました（" + providerMethod + " の途中）。つなぎ直します");
            }
            if (!mayBlock) {
                // 描画のスレッドではプロセスの起動を待たない。つながったら platform.connected が届く
                return PlatformJson.errorReply(PlatformContract.ERROR_CONNECTING,
                        providerDied ? PlatformContract.ERROR_PROVIDER_DIED : null);
            }
            if (!awaitConnect(pending)) {
                return PlatformJson.errorReply(PlatformContract.ERROR_CONNECT_TIMEOUT);
            }
            synchronized (lock) {
                if (state != State.CONNECTED) {
                    return PlatformJson.errorReply(lastConnectFailure != null
                            ? lastConnectFailure : PlatformContract.ERROR_PROVIDER_UNAVAILABLE);
                }
            }
        }
        return PlatformJson.errorReply(PlatformContract.ERROR_PROVIDER_DIED);
    }

    /**
     * つながっているか（ログ・デバッグ用。次の瞬間に変わりうる）。
     *
     * @return つながっていれば true
     */
    boolean isConnected() {
        synchronized (lock) {
            return state == State.CONNECTED;
        }
    }

    /** 記録の知らせ（Binder のスレッド）。取り出しを背面のスレッドへ 1 回だけ出す。 */
    @Override
    public void onEventsAvailable(long latestSeq) {
        if (pollScheduled.compareAndSet(false, true)) {
            workerHandler().post(this::pollOnWorker);
        }
    }

    /**
     * つなぎ始める（lock を持って呼ぶ）。接続の途中ならその門を返すだけ。
     *
     * @return 接続の完了で開く門
     */
    private CountDownLatch startConnectLocked() {
        if (state == State.CONNECTING && connectDone != null) {
            return connectDone;
        }
        state = State.CONNECTING;
        connectDone = new CountDownLatch(1);
        workerHandler().post(this::connectOnWorker);
        return connectDone;
    }

    /**
     * 背面のスレッドでつなぐ: プロバイダを取り（:seed_platform が居なければここで起動を待つ）、呼び鈴の Binder を登録する。
     * 結果をエンジンへ platform.connected / platform.connect_failed で知らせる。
     */
    private void connectOnWorker() {
        long startNs = SystemClock.elapsedRealtimeNanos();
        ContentProviderClient acquired = null;
        String failure = null;
        String detail = null;
        int platformPid = 0;
        try {
            acquired = appContext.getContentResolver().acquireUnstableContentProviderClient(authority);
            if (acquired == null) {
                failure = PlatformContract.ERROR_PROVIDER_UNAVAILABLE;
            } else {
                platformPid = registerDoorbell(acquired);
            }
        } catch (DeadObjectException e) {
            failure = PlatformContract.ERROR_PROVIDER_DIED;
            detail = PlatformJson.describe(e);
        } catch (RemoteException e) {
            failure = PlatformContract.ERROR_REMOTE;
            detail = PlatformJson.describe(e);
        } catch (RuntimeException e) {
            failure = PlatformContract.ERROR_PROVIDER_UNAVAILABLE;
            detail = PlatformJson.describe(e);
        }
        if (failure != null && acquired != null) {
            closeQuietly(acquired);
            acquired = null;
        }
        long connectMs = (SystemClock.elapsedRealtimeNanos() - startNs) / NANOS_PER_MILLI;

        CountDownLatch done;
        synchronized (lock) {
            if (failure == null) {
                client = acquired;
                state = State.CONNECTED;
                lastConnectFailure = null;
            } else {
                state = State.DISCONNECTED;
                lastConnectFailure = failure;
            }
            done = connectDone;
            connectDone = null;
        }
        if (done != null) {
            done.countDown();
        }

        JSONObject data = new JSONObject();
        PlatformJson.put(data, PlatformContract.KEY_CONNECT_MS, connectMs);
        if (failure == null) {
            PlatformJson.put(data, PlatformContract.KEY_PID, platformPid);
            Log.i(PlatformContract.LOG_TAG, ":seed_platform へつながりました（" + connectMs + " ms・pid " + platformPid + "）");
            deliverLocalEvent(PlatformContract.EVENT_CONNECTED, data);
        } else {
            PlatformJson.put(data, PlatformContract.KEY_ERROR, failure);
            if (detail != null) {
                PlatformJson.put(data, PlatformContract.KEY_DETAIL, detail);
            }
            Log.w(PlatformContract.LOG_TAG, ":seed_platform へつなげませんでした: " + failure
                    + (detail != null ? "（" + detail + "）" : ""));
            deliverLocalEvent(PlatformContract.EVENT_CONNECT_FAILED, data);
        }
    }

    /**
     * 呼び鈴の Binder を登録する（接続のたびに。:seed_platform が作り直されると登録は消えるため）。
     *
     * @param target 取ったばかりの client
     * @return :seed_platform の pid（返答に無ければ 0）
     * @throws RemoteException       Binder の呼び出しの失敗
     * @throws IllegalStateException 返答が失敗・読めない
     */
    private int registerDoorbell(ContentProviderClient target) throws RemoteException {
        Bundle extras = requestExtras(null);
        extras.putBinder(PlatformContract.BUNDLE_CALLBACK, doorbell);
        byte[] reply = replyBytes(target.call(
                PlatformContract.providerMethod(PlatformContract.MODULE_PLATFORM, PlatformContract.METHOD_REGISTER_CALLBACK),
                null, extras));
        JSONObject parsed = parseReply(reply);
        if (parsed == null || !parsed.optBoolean(PlatformContract.KEY_OK)) {
            throw new IllegalStateException("register_callback が失敗しました: " + PlatformJson.text(reply));
        }
        return parsed.optInt(PlatformContract.KEY_PID);
    }

    /** 背面のスレッドで未読の記録を取り出し、1 件ずつエンジンへ渡す。 */
    private void pollOnWorker() {
        // 先に下ろす: 取り出しの最中に来た呼び鈴で、もう 1 回取り出しが予約されるように（取りこぼさない）
        pollScheduled.set(false);
        JSONObject request = new JSONObject();
        PlatformJson.put(request, PlatformContract.KEY_MAX, POLL_BATCH_MAX);
        byte[] requestJson = PlatformJson.utf8(request.toString());
        String providerMethod = PlatformContract.providerMethod(
                PlatformContract.MODULE_PLATFORM, PlatformContract.METHOD_POLL_EVENTS);
        for (int round = 0; round < MAX_POLL_ROUNDS; round++) {
            byte[] reply = callIfConnected(providerMethod, requestJson);
            if (reply == null) {
                // つながっていない。次の接続の register_callback で、未読があれば呼び鈴が鳴り直す
                return;
            }
            JSONObject parsed = parseReply(reply);
            if (parsed == null || !parsed.optBoolean(PlatformContract.KEY_OK)) {
                Log.w(PlatformContract.LOG_TAG, "未読の記録を取り出せませんでした: " + PlatformJson.text(reply));
                return;
            }
            JSONArray events = parsed.optJSONArray(PlatformContract.KEY_EVENTS);
            int count = events != null ? events.length() : 0;
            for (int i = 0; i < count; i++) {
                JSONObject event = events.optJSONObject(i);
                if (event != null) {
                    deliver(PlatformJson.utf8(event.toString()));
                }
            }
            if (!parsed.optBoolean(PlatformContract.KEY_HAS_MORE)) {
                return;
            }
        }
        // まだ残っている: 他の仕事（接続など）を先に通すため、自分で呼び鈴を鳴らし直して続きを後に回す
        onEventsAvailable(PlatformContract.LOCAL_EVENT_SEQ);
    }

    /**
     * つながっていれば呼ぶ（背面のスレッドの取り出し用。つながっていなければつながない）。
     *
     * @param providerMethod call の method
     * @param json           引数
     * @return 返答。つながっていない・失敗したら null
     */
    private byte[] callIfConnected(String providerMethod, byte[] json) {
        boolean providerDied = false;
        synchronized (lock) {
            if (state != State.CONNECTED) {
                return null;
            }
            try {
                return replyBytes(client.call(providerMethod, null, requestExtras(json)));
            } catch (DeadObjectException e) {
                dropClientLocked();
                providerDied = true;
            } catch (RemoteException | RuntimeException e) {
                Log.w(PlatformContract.LOG_TAG, providerMethod + " を呼べませんでした: " + PlatformJson.describe(e));
                return null;
            }
        }
        if (providerDied) {
            Log.w(PlatformContract.LOG_TAG, ":seed_platform が居なくなりました（" + providerMethod + " の途中）");
            deliverLocalEvent(PlatformContract.EVENT_DISCONNECTED, new JSONObject());
        }
        return null;
    }

    /** client を閉じて「つながっていない」にする（lock を持って呼ぶ）。 */
    private void dropClientLocked() {
        if (client != null) {
            closeQuietly(client);
            client = null;
        }
        state = State.DISCONNECTED;
    }

    /**
     * 接続の完了を待つ（待ってよい呼び出し元だけ）。
     *
     * @param pending 接続の門
     * @return 時間内に完了したら true
     */
    private static boolean awaitConnect(CountDownLatch pending) {
        try {
            return pending.await(BLOCKING_CONNECT_TIMEOUT_MS, TimeUnit.MILLISECONDS);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return false;
        }
    }

    /**
     * 中身の無い知らせ（接続の状態）を背面のスレッドから送るよう予約する（呼び出し元のスレッドでは渡し先を呼ばない）。
     *
     * @param name 名前（PlatformContract.EVENT_*）
     */
    private void postLocalEvent(String name) {
        workerHandler().post(() -> deliverLocalEvent(name, new JSONObject()));
    }

    /**
     * メインプロセスの中で作った知らせ（接続の状態）をエンジンへ渡す（背面のスレッド SEEDPlatform から呼ぶ）。
     *
     * @param name 名前（PlatformContract.EVENT_*）
     * @param data 中身
     */
    private void deliverLocalEvent(String name, JSONObject data) {
        JSONObject event = PlatformJson.event(name, PlatformContract.LOCAL_EVENT_SEQ, System.currentTimeMillis(), data);
        deliver(PlatformJson.utf8(event.toString()));
    }

    /**
     * イベントを渡し先へ（渡し先の例外で背面のスレッドを止めない）。
     *
     * @param eventJsonUtf8 イベント
     */
    private void deliver(byte[] eventJsonUtf8) {
        try {
            sink.deliver(eventJsonUtf8);
        } catch (RuntimeException e) {
            Log.w(PlatformContract.LOG_TAG, "イベントを渡せませんでした: " + PlatformJson.describe(e));
        }
    }

    /**
     * 背面のスレッドの Handler（最初に使うときにスレッドを作る。プロセスの間ずっと使う）。
     *
     * @return Handler
     */
    private Handler workerHandler() {
        synchronized (workerLock) {
            if (worker == null) {
                HandlerThread thread = new HandlerThread(WORKER_THREAD_NAME);
                thread.start();
                worker = new Handler(thread.getLooper());
            }
            return worker;
        }
    }

    /**
     * call の extras を作る（引数の JSON を入れる）。
     *
     * @param json 引数（null は {}）
     * @return extras
     */
    private static Bundle requestExtras(byte[] json) {
        Bundle extras = new Bundle();
        extras.putByteArray(PlatformContract.BUNDLE_JSON, json != null ? json : PlatformJson.emptyObject());
        return extras;
    }

    /**
     * call の返答から JSON を取り出す（無ければ no_reply の失敗の返答）。
     *
     * @param reply call の返答（null 可）
     * @return 返答の JSON（UTF-8）
     */
    private static byte[] replyBytes(Bundle reply) {
        byte[] json = reply != null ? reply.getByteArray(PlatformContract.BUNDLE_JSON) : null;
        return json != null ? json : PlatformJson.errorReply(PlatformContract.ERROR_NO_REPLY);
    }

    /**
     * 返答の JSON を読む。
     *
     * @param reply 返答（UTF-8）
     * @return オブジェクト。読めなければ null
     */
    private static JSONObject parseReply(byte[] reply) {
        try {
            return new JSONObject(PlatformJson.text(reply));
        } catch (JSONException e) {
            return null;
        }
    }

    /**
     * client を閉じる（閉じるときの例外は捨てる。もう使わないので）。
     *
     * @param target 閉じる client
     */
    private static void closeQuietly(ContentProviderClient target) {
        try {
            target.close();
        } catch (RuntimeException e) {
            Log.w(PlatformContract.LOG_TAG, "client を閉じられませんでした: " + PlatformJson.describe(e));
        }
    }
}
