// ============================================================
//  RingAudio.java — 鳴動の音（MediaPlayer のループ再生・音量の漸増・アラームの音量の出し入れ。:seed_platform。W1-4a）
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
//  専用の HandlerThread（SEEDRingAudio）で行う。MediaPlayer の知らせ（onError）もこのスレッドの Looper に届く。
//  状態（player・音量・漸増）はこのスレッドだけが触り、外からは start / stop / release を投げるだけ。
//
//  【漸増】fade_in_seconds の間、MediaPlayer の音量（0..1。STREAM_ALARM の音量に掛かる倍率）を 0 から 1 へ直線で上げる
//  （FADE_STEP_MS ごと）。0 秒なら最初から 1。
//  【音源】RingSoundSource の順（予約の音 → 同梱の既定の音 → 端末の既定のアラーム音）。途中で再生に失敗したら次の候補で鳴らし直す。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import android.content.Context;
import android.media.AudioAttributes;
import android.media.MediaPlayer;
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

    /** 音源の候補（試す順）。 */
    private static final RingSoundSource[] SOURCES = RingSoundSource.values();

    /** 候補を最初から試すときの位置。 */
    private static final int FIRST_SOURCE_INDEX = 0;

    /** 鳴らしていないときの音源の位置。 */
    private static final int NO_SOURCE = -1;

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

    /** 鳴らしている MediaPlayer（無ければ null）。 */
    private MediaPlayer player;

    /** 鳴らしている予約（無ければ null）。 */
    private AlarmEntry entry;

    /** 鳴らしている音源の SOURCES の位置（無ければ NO_SOURCE）。 */
    private int sourceIndex = NO_SOURCE;

    /** アラームの音量の出し入れ（鳴動ごとに作る）。 */
    private AlarmStreamVolume volume;

    /** 漸増を始めた時刻（SystemClock.elapsedRealtime）。 */
    private long fadeStartedAtMs;

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
     * 予約の音で鳴らし始める（前の音は止めて音量を戻してから）。すぐ返る（準備は専用のスレッド）。
     *
     * @param next 鳴らす予約
     */
    void start(AlarmEntry next) {
        handler.post(() -> startOnAudioThread(next));
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
    private void startOnAudioThread(AlarmEntry next) {
        stopOnAudioThread();
        entry = next;
        volume = new AlarmStreamVolume(context);
        volume.force(next.forceVolume);
        if (!playFrom(FIRST_SOURCE_INDEX)) {
            Log.e(PlatformContract.LOG_TAG, "目覚まし " + next.id + " の音をどの音源でも鳴らせませんでした（振動と通知は続けます）");
            return;
        }
        fadeStartedAtMs = SystemClock.elapsedRealtime();
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
            MediaPlayer created = prepared(SOURCES[index]);
            if (created == null) {
                continue;
            }
            player = created;
            sourceIndex = index;
            player.setVolume(initialGain(), initialGain());
            player.start();
            Log.i(PlatformContract.LOG_TAG, "目覚まし " + entry.id + " を鳴らし始めました（音源 " + SOURCES[index]
                    + "・USAGE_ALARM・漸増 " + entry.fadeInSeconds + " 秒）");
            return true;
        }
        return false;
    }

    /**
     * 1 つの候補で MediaPlayer を作って準備する（専用のスレッド。当てはまらない・読めなければ null）。
     *
     * @param source 候補
     * @return 準備のできた MediaPlayer（ループ・USAGE_ALARM）
     */
    private MediaPlayer prepared(RingSoundSource source) {
        MediaPlayer created = new MediaPlayer();
        try {
            created.setAudioAttributes(ALARM_ATTRIBUTES);
            if (!source.applyTo(context, created, entry)) {
                created.release();
                return null;
            }
            created.setLooping(true);
            created.setOnErrorListener(this::onPlayerError);
            created.prepare();
            return created;
        } catch (IOException | RuntimeException e) {
            // RuntimeException: 音源の形式が読めない（IllegalStateException）・リソースが無い（NotFoundException）など
            Log.w(PlatformContract.LOG_TAG, "音源 " + source + " で鳴らせませんでした（次を試します）: " + PlatformJson.describe(e));
            created.release();
            return null;
        }
    }

    /**
     * 再生の途中の失敗（専用のスレッドに届く）。次の候補で鳴らし直す（無音にしない）。
     *
     * @return 失敗を扱ったので true（onCompletion を呼ばせない）
     */
    private boolean onPlayerError(MediaPlayer failed, int what, int extra) {
        if (failed != player) {
            // 既に止めた・入れ替えた MediaPlayer の知らせ
            return true;
        }
        Log.w(PlatformContract.LOG_TAG, "目覚まし " + entry.id + " の音源 " + SOURCES[sourceIndex] + " の再生に失敗しました（what "
                + what + "・extra " + extra + "）。次の音源で鳴らし直します");
        int failedIndex = sourceIndex;
        releasePlayer();
        if (!playFrom(failedIndex + 1)) {
            Log.e(PlatformContract.LOG_TAG, "目覚まし " + entry.id + " の音を鳴らし直せませんでした");
        }
        return true;
    }

    /** 漸増の 1 歩（専用のスレッド）。 */
    private void stepFade() {
        if (player == null) {
            return;
        }
        float gain = currentFadeGain();
        player.setVolume(gain, gain);
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

    /** 鳴らし始めの音量（漸増なら今の漸増の値。途中で音源を替えたときも漸増の続きから）。 */
    private float initialGain() {
        return entry.fadeInSeconds > 0 ? currentFadeGain() : FULL_GAIN;
    }

    /** 漸増の今の値（0..1）。 */
    private float currentFadeGain() {
        if (entry.fadeInSeconds <= 0 || fadeStartedAtMs == 0) {
            return entry.fadeInSeconds > 0 ? FADE_START_GAIN : FULL_GAIN;
        }
        double elapsedSeconds = (SystemClock.elapsedRealtime() - fadeStartedAtMs) / MILLIS_PER_SECOND;
        return (float) Math.min(FULL_GAIN, elapsedSeconds / entry.fadeInSeconds);
    }

    /** 止めて音量を戻す（専用のスレッド。何度呼んでもよい）。 */
    private void stopOnAudioThread() {
        handler.removeCallbacks(fadeStep);
        handler.removeCallbacks(keepVolume);
        releasePlayer();
        if (volume != null) {
            volume.restore();
            volume = null;
        }
        entry = null;
        fadeStartedAtMs = 0;
    }

    /** MediaPlayer を止めて手放す（専用のスレッド）。 */
    private void releasePlayer() {
        if (player == null) {
            return;
        }
        try {
            player.stop();
        } catch (IllegalStateException e) {
            // 準備の途中・失敗の後は stop できない。手放すだけでよい
            Log.w(PlatformContract.LOG_TAG, "MediaPlayer を止められませんでした（手放します）: " + PlatformJson.describe(e));
        }
        player.release();
        player = null;
        sourceIndex = NO_SOURCE;
    }
}
