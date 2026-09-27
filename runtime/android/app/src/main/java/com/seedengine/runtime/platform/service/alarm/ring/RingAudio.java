// ============================================================
//  RingAudio.java — 鳴動の音（継ぎ目の無いループ再生・音量の漸増・アラームの音量の出し入れ。:seed_platform。W1-4a・W1-7）
//
//  【音の種類は USAGE_ALARM 固定】AudioAttributes は USAGE_ALARM + CONTENT_TYPE_SONIFICATION に固定する。
//  Android 17 の背面の音の制限の免除の条件（正確なアラームの権限＋USAGE_ALARM。X-7・§2.6）で、音量は STREAM_ALARM に属する。
//
//  【音声フォーカスは要求しない・失っても止めない】鳴動画面（エンジン）が前に出ると、エンジンが AUDIOFOCUS_GAIN（USAGE_GAME）を
//  要求する（AudioFocusController）。ここでフォーカスを取ると、そこで失って止める作りになりやすいので、そもそも要求しない
//  （MediaPlayer は自分ではフォーカスを要求しない）。W1-0 の方針（docs/app_platform_roadmap.md §2.2「既存の音声との関係」）。
//
//  【専用のスレッドで準備する（音の開始を早める）】MediaPlayer の生成・音源の読み込み・prepare（冷えたプロセスで数百 ms。W1-0 で
//  startForeground から音まで約 340 ms）を、RingService の UI スレッド（startForeground・通知の組み立て）と並べて進めるため、
//  専用の HandlerThread（SEEDRingAudio）で行う。MediaPlayer の知らせ（onError・onCompletion）もこのスレッドの Looper に届く。
//  状態（ループ・音量・漸増）はこのスレッドだけが触り、外からは start / stop / release を投げるだけ。
//
//  【ループ】RingLoopPlayer（MediaPlayer の setNextMediaPlayer の連鎖。W1-7。setLooping の継ぎ目の約 60 ms の途切れ〈G-3〉を消す）。
//  【漸増】fade_in_seconds の間、MediaPlayer の音量（0..1。STREAM_ALARM の音量に掛かる倍率）を 0 から 1 へ直線で上げる
//  （FADE_STEP_MS ごと）。0 秒なら最初から 1。漸増の起点は鳴動の鳴り始め（RingSession.startedAtUtcMs）なので、見張りで
//  鳴らし直した鳴動（W1-7）は殺される前の音量の続きから鳴る。
//  【音源】RingSoundSource の順（予約の音 → 同梱の既定の音 → 端末の既定のアラーム音）。途中で再生に失敗したら次の候補で鳴らし直す。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import android.content.Context;
import android.media.AudioAttributes;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.SystemClock;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.service.alarm.AlarmEntry;

import java.io.IOException;

/**
 * 鳴動の音（外からの呼び出しはどのスレッドからでもよい。中身は専用のスレッドで動く）。
 */
final class RingAudio {

    /** 専用のスレッドの名前。 */
    private static final String THREAD_NAME = "SEEDRingAudio";

    /** 漸増の 1 歩の間隔（ミリ秒）。 */
    private static final long FADE_STEP_MS = 100L;

    /** keep_volume の確かめの間隔（ミリ秒。docs の「1 秒ごとに戻す」）。 */
    private static final long KEEP_VOLUME_INTERVAL_MS = 1_000L;

    /** 1 秒のミリ秒。 */
    private static final double MILLIS_PER_SECOND = 1_000.0;

    /** 最大の音量（MediaPlayer の倍率）。 */
    private static final float FULL_GAIN = 1.0f;

    /** 漸増の始まりの音量（MediaPlayer の倍率）。 */
    private static final float FADE_START_GAIN = 0.0f;

    /** 経過が負にならないようにするときの下限（ミリ秒）。 */
    private static final long NO_ELAPSED_MS = 0L;

    /** 音源の候補（試す順）。 */
    private static final RingSoundSource[] SOURCES = RingSoundSource.values();

    /** 候補を最初から試すときの位置。 */
    private static final int FIRST_SOURCE_INDEX = 0;

    /** 鳴らしていないときの音源の位置。 */
    private static final int NO_SOURCE = -1;

    /** 漸増を始めていないときの起点。 */
    private static final long NO_FADE_ORIGIN = 0L;

    /** 鳴動の音の属性（USAGE_ALARM 固定。X-7）。 */
    private static final AudioAttributes ALARM_ATTRIBUTES = new AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_ALARM)
            .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
            .build();

    /** アプリの Context。 */
    private final Context context;

    /** 専用のスレッド。 */
    private final HandlerThread thread;

    /** 専用のスレッドの Handler。 */
    private final Handler handler;

    // ── ここから下は専用のスレッドだけが触る ──

    /** 鳴らしているループ（無ければ null）。 */
    private RingLoopPlayer loop;

    /** 鳴らしている予約（無ければ null）。 */
    private AlarmEntry entry;

    /** 鳴らしている音源の SOURCES の位置（無ければ NO_SOURCE）。 */
    private int sourceIndex = NO_SOURCE;

    /** アラームの音量の出し入れ（鳴動ごとに作る）。 */
    private AlarmStreamVolume volume;

    /** 漸増の起点（SystemClock.elapsedRealtime の値。鳴動の鳴り始めに当たる時刻）。 */
    private long fadeStartedAtMs = NO_FADE_ORIGIN;

    /** 漸増の 1 歩。 */
    private final Runnable fadeStep = this::stepFade;

    /** keep_volume の確かめ。 */
    private final Runnable keepVolume = this::enforceVolume;

    /**
     * @param context どの Context でもよい（アプリの Context を使う）
     */
    RingAudio(Context context) {
        this.context = context.getApplicationContext();
        this.thread = new HandlerThread(THREAD_NAME);
        this.thread.start();
        this.handler = new Handler(thread.getLooper());
    }

    /**
     * 鳴動の音で鳴らし始める（前の音は止めて音量を戻してから）。すぐ返る（準備は専用のスレッド）。
     *
     * @param session 鳴らす鳴動（予約と鳴り始めの時刻）
     */
    void start(RingSession session) {
        AlarmEntry next = session.entry;
        long startedAtUtcMs = session.startedAtUtcMs;
        handler.post(() -> startOnAudioThread(next, startedAtUtcMs));
    }

    /**
     * 止めて音量を戻す。すぐ返る。
     */
    void stop() {
        handler.post(this::stopOnAudioThread);
    }

    /**
     * 止めて、専用のスレッドを終える（サービスの onDestroy。以後は使わない）。
     */
    void release() {
        handler.post(this::stopOnAudioThread);
        thread.quitSafely();
    }

    /** 鳴らし始める（専用のスレッド）。 */
    private void startOnAudioThread(AlarmEntry next, long startedAtUtcMs) {
        stopOnAudioThread();
        entry = next;
        volume = new AlarmStreamVolume(context);
        volume.force(next.forceVolume);
        // 漸増の起点は鳴動の鳴り始め（鳴らし直した鳴動は、殺される前の漸増の続きから）
        long elapsedSinceStart = Math.max(NO_ELAPSED_MS, System.currentTimeMillis() - startedAtUtcMs);
        fadeStartedAtMs = SystemClock.elapsedRealtime() - elapsedSinceStart;
        if (!playFrom(FIRST_SOURCE_INDEX)) {
            Log.e(PlatformContract.LOG_TAG, "目覚まし " + next.id + " の音をどの音源でも鳴らせませんでした（振動と通知は続けます）");
            return;
        }
        if (next.fadeInSeconds > 0) {
            handler.postDelayed(fadeStep, FADE_STEP_MS);
        }
        if (next.keepVolume && volume.isForced()) {
            handler.postDelayed(keepVolume, KEEP_VOLUME_INTERVAL_MS);
        } else if (next.keepVolume) {
            Log.w(PlatformContract.LOG_TAG, "keep_volume は force_volume が負のときは効きません（目覚まし " + next.id + "）");
        }
    }

    /**
     * 候補を firstIndex から順に試し、鳴らせた最初のもので鳴らす（専用のスレッド）。
     *
     * @param firstIndex 試し始める SOURCES の位置
     * @return 鳴らせたら true
     */
    private boolean playFrom(int firstIndex) {
        for (int index = firstIndex; index < SOURCES.length; index++) {
            RingLoopPlayer created = prepared(SOURCES[index]);
            if (created == null) {
                continue;
            }
            loop = created;
            sourceIndex = index;
            loop.start();
            Log.i(PlatformContract.LOG_TAG, "目覚まし " + entry.id + " を鳴らし始めました（音源 " + SOURCES[index]
                    + "・USAGE_ALARM・漸増 " + entry.fadeInSeconds + " 秒）");
            return true;
        }
        return false;
    }

    /**
     * 1 つの候補でループを準備する（専用のスレッド。当てはまらない・読めなければ null）。
     *
     * @param source 候補
     * @return 準備のできたループ（USAGE_ALARM）
     */
    private RingLoopPlayer prepared(RingSoundSource source) {
        try {
            return RingLoopPlayer.prepare(context, source, entry, ALARM_ATTRIBUTES, initialGain(), this::onLoopError);
        } catch (IOException | RuntimeException e) {
            // RuntimeException: 音源の形式が読めない（IllegalStateException）・リソースが無い（NotFoundException）など
            Log.w(PlatformContract.LOG_TAG, "音源 " + source + " で鳴らせませんでした（次を試します）: " + PlatformJson.describe(e));
            return null;
        }
    }

    /**
     * 再生の途中の失敗（専用のスレッドに届く）。次の候補で鳴らし直す（無音にしない）。
     */
    private void onLoopError(RingLoopPlayer failed, int what, int extra) {
        if (failed != loop) {
            // 既に止めた・入れ替えたループの知らせ
            return;
        }
        Log.w(PlatformContract.LOG_TAG, "目覚まし " + entry.id + " の音源 " + SOURCES[sourceIndex] + " の再生に失敗しました（what "
                + what + "・extra " + extra + "）。次の音源で鳴らし直します");
        int failedIndex = sourceIndex;
        releaseLoop();
        if (!playFrom(failedIndex + 1)) {
            Log.e(PlatformContract.LOG_TAG, "目覚まし " + entry.id + " の音を鳴らし直せませんでした");
        }
    }

    /** 漸増の 1 歩（専用のスレッド）。 */
    private void stepFade() {
        if (loop == null) {
            return;
        }
        float gain = currentFadeGain();
        loop.setVolume(gain);
        if (gain < FULL_GAIN) {
            handler.postDelayed(fadeStep, FADE_STEP_MS);
        }
    }

    /** keep_volume の確かめ（専用のスレッド）。 */
    private void enforceVolume() {
        if (volume == null) {
            return;
        }
        volume.enforce();
        handler.postDelayed(keepVolume, KEEP_VOLUME_INTERVAL_MS);
    }

    /** 鳴らし始めの音量（漸増なら今の漸増の値。途中で音源を替えたとき・鳴らし直したときも漸増の続きから）。 */
    private float initialGain() {
        return entry.fadeInSeconds > 0 ? currentFadeGain() : FULL_GAIN;
    }

    /** 漸増の今の値（0..1）。 */
    private float currentFadeGain() {
        if (entry.fadeInSeconds <= 0 || fadeStartedAtMs == NO_FADE_ORIGIN) {
            return entry.fadeInSeconds > 0 ? FADE_START_GAIN : FULL_GAIN;
        }
        double elapsedSeconds = (SystemClock.elapsedRealtime() - fadeStartedAtMs) / MILLIS_PER_SECOND;
        return (float) Math.min(FULL_GAIN, elapsedSeconds / entry.fadeInSeconds);
    }

    /** 止めて音量を戻す（専用のスレッド。何度呼んでもよい）。 */
    private void stopOnAudioThread() {
        handler.removeCallbacks(fadeStep);
        handler.removeCallbacks(keepVolume);
        releaseLoop();
        if (volume != null) {
            volume.restore();
            volume = null;
        }
        entry = null;
        fadeStartedAtMs = NO_FADE_ORIGIN;
    }

    /** ループを止めて手放す（専用のスレッド）。 */
    private void releaseLoop() {
        if (loop == null) {
            return;
        }
        loop.release();
        loop = null;
        sourceIndex = NO_SOURCE;
    }
}
