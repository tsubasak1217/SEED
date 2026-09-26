package com.seedengine.platformspike;

import android.content.BroadcastReceiver;
import android.content.ComponentName;
import android.content.ContentProviderClient;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.ServiceConnection;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.Message;
import android.os.Messenger;
import android.os.SystemClock;

import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;

/**
 * メインプロセス ↔ :seed_platform の呼び出しの往復時間を測る（メインプロセスのワーカースレッドで呼ぶ）。
 *
 * <p>結果は「BENCH &lt;名前&gt; n=.. median_us=.. min_us=.. max_us=.. values_us=..」の行で logcat へ出す。</p>
 * <ol>
 *   <li>冷えた呼び出し（:seed_platform が居ない状態からの最初の ContentResolver.call。プロセスの起動を含む）</li>
 *   <li>ContentResolver.call（毎回プロバイダを取得・解放）… ping と 256 バイトの byte[] の往復</li>
 *   <li>ContentProviderClient を持ったままの call</li>
 *   <li>AIDL（バインドしてからの同期呼び出し）</li>
 *   <li>Messenger（バインドしてからの送信 → replyTo への返信）</li>
 *   <li>放送で返す（:seed_platform → メインの実行時の受信機。「記録あり」の知らせの遅延）</li>
 * </ol>
 */
final class IpcBench {
    private static final String PAYLOAD_PREFIX = "{\"module\":\"alarm\",\"method\":\"schedule\",\"pad\":\"";
    private static final String PAYLOAD_SUFFIX = "\"}";
    private static final char PAYLOAD_FILL = 'x';

    private IpcBench() {}

    /** 1 回分の操作（例外はそのまま投げて計測を止める）。 */
    private interface Op {
        void run() throws Exception;
    }

    static void run(Context app) {
        SpikeLog.mark("bench_begin", "");
        try {
            long start = System.nanoTime();
            Bundle first = PlatformClient.call(app, SpikeContract.M_PING, null);
            long coldUs = (System.nanoTime() - start) / SpikeContract.NS_PER_US;
            SpikeLog.i("BENCH cold_resolver_call first_us=" + coldUs + " ok=" + first.getBoolean(SpikeContract.R_OK)
                    + " platform_pid=" + first.getInt(SpikeContract.R_PID) + " error=" + first.getString(SpikeContract.R_ERROR));

            report("resolver_call_ping", measure(() -> check(PlatformClient.call(app, SpikeContract.M_PING, null))));

            Bundle payload = new Bundle();
            payload.putByteArray(SpikeContract.EXTRA_JSON, samplePayload());
            report("resolver_call_invoke256", measure(() -> check(PlatformClient.call(app, SpikeContract.M_INVOKE, payload))));

            try (ContentProviderClient client = app.getContentResolver()
                    .acquireContentProviderClient(SpikeContract.AUTHORITY)) {
                if (client == null) {
                    SpikeLog.i("BENCH provider_client acquire_failed");
                } else {
                    report("provider_client_ping", measure(() -> client.call(SpikeContract.M_PING, null, null)));
                    report("provider_client_invoke256", measure(() -> client.call(SpikeContract.M_INVOKE, null, payload)));
                }
            }

            benchAidl(app);
            benchMessenger(app);
            benchBroadcastBack(app);
        } catch (Exception e) {
            SpikeLog.w("計測に失敗しました", e);
            SpikeLog.i("BENCH error " + SpikeLog.oneLine(e));
        }
        SpikeLog.mark("bench_end", "");
    }

    private static void check(Bundle result) {
        if (!result.getBoolean(SpikeContract.R_OK)) {
            throw new IllegalStateException("call failed: " + result.getString(SpikeContract.R_ERROR));
        }
    }

    private static long[] measure(Op op) throws Exception {
        long[] micros = new long[SpikeContract.BENCH_ITERATIONS];
        for (int i = 0; i < micros.length; i++) {
            long start = System.nanoTime();
            op.run();
            micros[i] = (System.nanoTime() - start) / SpikeContract.NS_PER_US;
        }
        return micros;
    }

    private static void report(String name, long[] micros) {
        long[] sorted = micros.clone();
        Arrays.sort(sorted);
        int n = sorted.length;
        double median = n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        StringBuilder values = new StringBuilder();
        for (int i = 0; i < micros.length; i++) {
            if (i > 0) {
                values.append(',');
            }
            values.append(micros[i]);
        }
        SpikeLog.i("BENCH " + name + " n=" + n + " median_us=" + median + " min_us=" + sorted[0]
                + " max_us=" + sorted[n - 1] + " values_us=" + values);
    }

    private static byte[] samplePayload() {
        StringBuilder text = new StringBuilder(PAYLOAD_PREFIX);
        while (text.length() + PAYLOAD_SUFFIX.length() < SpikeContract.BENCH_PAYLOAD_BYTES) {
            text.append(PAYLOAD_FILL);
        }
        text.append(PAYLOAD_SUFFIX);
        return text.toString().getBytes(StandardCharsets.UTF_8);
    }

    private static void benchAidl(Context app) throws Exception {
        AtomicReference<IPlatformSpike> service = new AtomicReference<>();
        CountDownLatch connected = new CountDownLatch(1);
        ServiceConnection connection = new ServiceConnection() {
            @Override
            public void onServiceConnected(ComponentName name, IBinder binder) {
                service.set(IPlatformSpike.Stub.asInterface(binder));
                connected.countDown();
            }

            @Override
            public void onServiceDisconnected(ComponentName name) {
                service.set(null);
            }
        };
        long start = System.nanoTime();
        boolean bound = app.bindService(new Intent(app, PlatformAidlService.class), connection, Context.BIND_AUTO_CREATE);
        try {
            if (!bound || !connected.await(SpikeContract.BENCH_TIMEOUT_MS, TimeUnit.MILLISECONDS)) {
                SpikeLog.i("BENCH aidl bind_failed bound=" + bound);
                return;
            }
            SpikeLog.i("BENCH aidl_bind bind_us=" + (System.nanoTime() - start) / SpikeContract.NS_PER_US);
            byte[] payload = samplePayload();
            report("aidl_invoke256", measure(() -> service.get().invoke(SpikeContract.M_INVOKE, payload)));
        } finally {
            if (bound) {
                app.unbindService(connection);
            }
        }
    }

    private static void benchMessenger(Context app) throws Exception {
        HandlerThread replyThread = new HandlerThread("spike-bench-reply");
        replyThread.start();
        AtomicReference<CountDownLatch> pending = new AtomicReference<>();
        AtomicLong receivedAt = new AtomicLong();
        Messenger replyTo = new Messenger(new Handler(replyThread.getLooper(), message -> {
            if (message.what == PlatformMessengerService.MSG_PONG) {
                receivedAt.set(System.nanoTime());
                CountDownLatch latch = pending.get();
                if (latch != null) {
                    latch.countDown();
                }
            }
            return true;
        }));
        AtomicReference<Messenger> service = new AtomicReference<>();
        CountDownLatch connected = new CountDownLatch(1);
        ServiceConnection connection = new ServiceConnection() {
            @Override
            public void onServiceConnected(ComponentName name, IBinder binder) {
                service.set(new Messenger(binder));
                connected.countDown();
            }

            @Override
            public void onServiceDisconnected(ComponentName name) {
                service.set(null);
            }
        };
        long start = System.nanoTime();
        boolean bound = app.bindService(new Intent(app, PlatformMessengerService.class), connection,
                Context.BIND_AUTO_CREATE);
        try {
            if (!bound || !connected.await(SpikeContract.BENCH_TIMEOUT_MS, TimeUnit.MILLISECONDS)) {
                SpikeLog.i("BENCH messenger bind_failed bound=" + bound);
                return;
            }
            SpikeLog.i("BENCH messenger_bind bind_us=" + (System.nanoTime() - start) / SpikeContract.NS_PER_US);
            long[] micros = new long[SpikeContract.BENCH_ITERATIONS];
            for (int i = 0; i < micros.length; i++) {
                CountDownLatch latch = new CountDownLatch(1);
                pending.set(latch);
                Message ping = Message.obtain(null, PlatformMessengerService.MSG_PING);
                ping.arg1 = i;
                ping.replyTo = replyTo;
                long sent = System.nanoTime();
                service.get().send(ping);
                if (!latch.await(SpikeContract.BENCH_TIMEOUT_MS, TimeUnit.MILLISECONDS)) {
                    SpikeLog.i("BENCH messenger timeout i=" + i);
                    return;
                }
                micros[i] = (receivedAt.get() - sent) / SpikeContract.NS_PER_US;
            }
            report("messenger_roundtrip", micros);
        } finally {
            if (bound) {
                app.unbindService(connection);
            }
            replyThread.quitSafely();
        }
    }

    private static void benchBroadcastBack(Context app) throws Exception {
        HandlerThread thread = new HandlerThread("spike-bench-broadcast");
        thread.start();
        AtomicReference<CountDownLatch> pending = new AtomicReference<>();
        AtomicLong oneWayNs = new AtomicLong();
        BroadcastReceiver receiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context context, Intent intent) {
                long now = SystemClock.elapsedRealtimeNanos();
                oneWayNs.set(now - intent.getLongExtra(SpikeContract.EXTRA_T_SEND_NS, now));
                CountDownLatch latch = pending.get();
                if (latch != null) {
                    latch.countDown();
                }
            }
        };
        app.registerReceiver(receiver, new IntentFilter(SpikeContract.ACTION_BENCH_PONG), null,
                new Handler(thread.getLooper()), Context.RECEIVER_NOT_EXPORTED);
        try {
            long[] oneWay = new long[SpikeContract.BENCH_ITERATIONS];
            long[] roundTrip = new long[SpikeContract.BENCH_ITERATIONS];
            for (int i = 0; i < oneWay.length; i++) {
                CountDownLatch latch = new CountDownLatch(1);
                pending.set(latch);
                long start = SystemClock.elapsedRealtimeNanos();
                check(PlatformClient.call(app, SpikeContract.M_BENCH_BROADCAST_BACK, null));
                if (!latch.await(SpikeContract.BENCH_TIMEOUT_MS, TimeUnit.MILLISECONDS)) {
                    SpikeLog.i("BENCH broadcast_back timeout i=" + i);
                    return;
                }
                roundTrip[i] = (SystemClock.elapsedRealtimeNanos() - start) / SpikeContract.NS_PER_US;
                oneWay[i] = oneWayNs.get() / SpikeContract.NS_PER_US;
            }
            report("broadcast_back_oneway", oneWay);
            report("broadcast_back_roundtrip", roundTrip);
        } finally {
            app.unregisterReceiver(receiver);
            thread.quitSafely();
        }
    }
}
