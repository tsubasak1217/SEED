// ============================================================
//  RingLoopPlayer.java — 継ぎ目の無いループ再生（MediaPlayer の setNextMediaPlayer の連鎖。:seed_platform の音声のスレッド。W1-7）
//
//  【なぜ】MediaPlayer.setLooping(true) は、終わりで先頭へ戻る間に出力のトラックが止まる。W1-4b の実機（Pixel 6a・既定の音
//  2.22 s の WAV）で、AudioFlinger のトラックがループの継ぎ目ごとに止まり（AT::remove … I）約 57〜69 ms 後に再開した
//  （AT::add … A。dumpsys media.audio_flinger のトラックの記録。G-3）＝聞こえる途切れ。
//  【どうするか】同じ音源の MediaPlayer を 2 つ用意し、今のもの（current）に次（next）を setNextMediaPlayer でつなぐ
//  （次の準備は current を鳴らし始めた後。音の開始を 2 つ目の prepare で遅らせない）。
//  current が終わるとシステムが next を続けて鳴らす（Android の「次のプレーヤー」。出力のトラックを引き継ぐ）。終わった知らせ
//  （onCompletion）で、終わったものを手放し、next を current にして、新しく準備した MediaPlayer を次につなぐ。音源の形式を問わない
//  （予約の音源〈mp3・ogg など〉・同梱の WAV・端末のアラーム音のどれにも効く）。継ぎ目が消えたかは実機の同じ記録で確かめる
//  （docs/android.md §25.12）。
//  【短い音源】長さが MIN_CHAINED_DURATION_MS より短い（または長さが分からない）音源は、次を準備する前に終わりうるので
//  setLooping に落とす（途切れはあるが止まらない）。次を準備できなかったときも同じ。
//
//  このクラスは RingAudio の音声のスレッドだけで使う（MediaPlayer の知らせもそのスレッドの Looper に届く）。lock を持たない。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import android.content.Context;
import android.media.AudioAttributes;
import android.media.MediaPlayer;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.service.alarm.AlarmEntry;

import java.io.IOException;

/**
 * 1 つの音源の継ぎ目の無いループ（音声のスレッド専用）。
 */
final class RingLoopPlayer {

    /** 再生の途中の失敗の知らせ（RingAudio が次の音源へ落とす）。 */
    interface ErrorListener {
        /**
         * @param failed 失敗した RingLoopPlayer
         * @param what   MediaPlayer の what
         * @param extra  MediaPlayer の extra
         */
        void onLoopError(RingLoopPlayer failed, int what, int extra);
    }

    /**
     * 次のプレーヤーの連鎖を使う最短の長さ（ミリ秒）。これより短い音源は setLooping に落とす（1 周の間に次を準備し終える余裕）。
     */
    static final int MIN_CHAINED_DURATION_MS = 1_000;

    /** 長さが分からないときの MediaPlayer.getDuration の値。 */
    private static final int UNKNOWN_DURATION = -1;

    /** アプリの Context。 */
    private final Context context;
    /** 音源の候補。 */
    private final RingSoundSource source;
    /** 鳴らす予約（音源のパス）。 */
    private final AlarmEntry entry;
    /** 音の属性（USAGE_ALARM）。 */
    private final AudioAttributes attributes;
    /** 失敗の知らせの先。 */
    private final ErrorListener errorListener;

    /** 鳴っている MediaPlayer。 */
    private MediaPlayer current;
    /** current の次につないだ MediaPlayer（連鎖しないときは null）。 */
    private MediaPlayer next;
    /** 今の音量（MediaPlayer の倍率。新しく準備したものにも同じ値を付ける）。 */
    private float gain;
    /** 連鎖で鳴らしているか（false なら setLooping）。 */
    private boolean chained;
    /** 手放したか。 */
    private boolean released;
    /** 継ぎ目を越えた回数（ログ用）。 */
    private int seams;

    private RingLoopPlayer(Context context, RingSoundSource source, AlarmEntry entry, AudioAttributes attributes,
                           ErrorListener errorListener) {
        this.context = context;
        this.source = source;
        this.entry = entry;
        this.attributes = attributes;
        this.errorListener = errorListener;
    }

    /**
     * 1 つの候補でループを準備する（再生はまだ始めない）。当てはまらない候補（予約に音源が無い等）なら null。
     *
     * @param context       アプリの Context
     * @param source        音源の候補
     * @param entry         鳴らす予約
     * @param attributes    音の属性
     * @param initialGain   最初の音量（0..1）
     * @param errorListener 失敗の知らせの先
     * @return 準備のできたループ（当てはまらなければ null）
     * @throws IOException 当てはまるが読めない・準備できない
     */
    static RingLoopPlayer prepare(Context context, RingSoundSource source, AlarmEntry entry, AudioAttributes attributes,
                                  float initialGain, ErrorListener errorListener) throws IOException {
        RingLoopPlayer loop = new RingLoopPlayer(context, source, entry, attributes, errorListener);
        loop.gain = initialGain;
        loop.current = loop.newPlayer();
        if (loop.current == null) {
            return null;
        }
        int duration = loop.current.getDuration();
        loop.chained = duration != UNKNOWN_DURATION && duration >= MIN_CHAINED_DURATION_MS;
        if (!loop.chained) {
            loop.current.setLooping(true);
            Log.i(PlatformContract.LOG_TAG, "鳴動の音 " + source + " のループ: setLooping（長さ " + duration + " ms）");
        }
        return loop;
    }

    /**
     * 鳴らし始める。連鎖で鳴らすときは、鳴らし始めた**後で**次を準備してつなぐ（音の開始を 2 つ目の準備で遅らせない。
     * 1 周〈MIN_CHAINED_DURATION_MS 以上〉の間につなげばよい）。
     */
    void start() {
        if (released) {
            return;
        }
        current.start();
        if (!chained) {
            return;
        }
        if (linkNext()) {
            Log.i(PlatformContract.LOG_TAG, "鳴動の音 " + source + " のループ: 次のプレーヤーの連鎖（継ぎ目なし）");
        } else {
            // 次を準備できない: 今のものを setLooping で鳴らし続ける（途切れはあるが止めない）
            chained = false;
            current.setLooping(true);
        }
    }

    /**
     * 音量を変える（鳴っているものと次のもの）。
     *
     * @param newGain 0..1
     */
    void setVolume(float newGain) {
        gain = newGain;
        if (current != null) {
            current.setVolume(gain, gain);
        }
        if (next != null) {
            next.setVolume(gain, gain);
        }
    }

    /** 止めて手放す（何度呼んでもよい）。 */
    void release() {
        released = true;
        releaseQuietly(current);
        releaseQuietly(next);
        current = null;
        next = null;
    }

    /** 音源の候補（ログ用）。 */
    RingSoundSource source() {
        return source;
    }

    /**
     * 新しい MediaPlayer を作って準備する（当てはまらない候補なら null）。
     *
     * @return 準備のできた MediaPlayer
     * @throws IOException 読めない・準備できない（作ったものは手放してから投げる）
     */
    private MediaPlayer newPlayer() throws IOException {
        MediaPlayer created = new MediaPlayer();
        try {
            created.setAudioAttributes(attributes);
            if (!source.applyTo(context, created, entry)) {
                created.release();
                return null;
            }
            created.setOnErrorListener(this::onError);
            created.setOnCompletionListener(this::onCompletion);
            created.prepare();
            created.setVolume(gain, gain);
            return created;
        } catch (IOException | RuntimeException e) {
            created.release();
            if (e instanceof IOException) {
                throw (IOException) e;
            }
            throw new IOException("MediaPlayer を準備できません: " + PlatformJson.describe(e), e);
        }
    }

    /**
     * 次の MediaPlayer を準備して current につなぐ。
     *
     * @return つないだら true（準備できなければ false。呼び出し側が setLooping に落とす）
     */
    private boolean linkNext() {
        try {
            MediaPlayer prepared = newPlayer();
            if (prepared == null) {
                return false;
            }
            current.setNextMediaPlayer(prepared);
            next = prepared;
            return true;
        } catch (IOException | RuntimeException e) {
            Log.w(PlatformContract.LOG_TAG, "鳴動の音の次のプレーヤーを準備できませんでした（setLooping に落とします）: "
                    + PlatformJson.describe(e));
            return false;
        }
    }

    /** 1 周が終わった（音声のスレッド）。next はシステムが既に鳴らし始めている。 */
    private void onCompletion(MediaPlayer finished) {
        if (released || finished != current || next == null) {
            // 手放した後・入れ替えた後の古い知らせ、または setLooping で鳴らしている（ループでは届かない）
            return;
        }
        releaseQuietly(finished);
        current = next;
        next = null;
        seams++;
        if (!linkNext()) {
            // 次を準備できない: 今のものを setLooping で鳴らし続ける（途切れはあるが止めない）
            chained = false;
            current.setLooping(true);
        }
    }

    /** 再生の途中の失敗（音声のスレッド）。 */
    private boolean onError(MediaPlayer failed, int what, int extra) {
        if (released || (failed != current && failed != next)) {
            return true;
        }
        Log.w(PlatformContract.LOG_TAG, "鳴動の音 " + source + " の再生に失敗しました（what " + what + "・extra " + extra
                + "・継ぎ目 " + seams + " 回の後）");
        errorListener.onLoopError(this, what, extra);
        return true;
    }

    /** MediaPlayer を止めて手放す（null・止められない状態でもよい）。 */
    private static void releaseQuietly(MediaPlayer player) {
        if (player == null) {
            return;
        }
        try {
            player.stop();
        } catch (IllegalStateException e) {
            // 準備の途中・失敗の後・終わった後は stop できない。手放すだけでよい
        }
        player.release();
    }
}
