// ============================================================
//  AudioFocusController.java — 音声フォーカス（他のアプリとの音の譲り合い）の要求・放棄と、変化のネイティブへの通知
//
//  【Android の音声フォーカス】
//  音を出すアプリは、鳴らす前に AudioManager へ音声フォーカスを要求し、他のアプリ（着信の呼び出し音・
//  音楽アプリ・通知音）に奪われたら音を止める（または下げる）。OS は止めてくれないので、アプリの責任。
//
//  【このクラスの約束】
//  ・前面に来たとき（MainActivity.onResume）に AUDIOFOCUS_GAIN（USAGE_GAME）を要求し、前面を離れるとき（onPause）に手放す。
//  ・要求の結果とフォーカスの変化を、状態の番号（STATE_*）にしてネイティブへ渡す（JNI nativeOnAudioFocusChanged）。
//    エンジンはその番号から「出力全体を止めるか・全体の音量を下げるか」を決める（core/audio/output_policy.rs）。
//      取り戻した（GAIN）                        → STATE_GAINED         … 再開
//      一時的に失った（LOSS_TRANSIENT）           → STATE_LOST_TRANSIENT … 止める（取り戻したら再開）
//      下げてよい（LOSS_TRANSIENT_CAN_DUCK）      → STATE_DUCKED         … 全体の音量を下げる
//      恒久的に失った（LOSS）                     → STATE_LOST           … 止める
//      自分で手放した（onPause）                  → STATE_RELEASED       … 止める
//  ・恒久的に失ったとき（他のアプリが音楽の再生を始めた）は OS がこちらの要求を捨てるので GAIN は届かない。
//    止めたまま、次に前面へ戻ったとき（onResume）に要求し直す。
//  ・通話中などすぐには渡せないときは、後で渡してもらう（setAcceptsDelayedFocusGain）。渡されるまでは止めておく。
//  ・「下げてよい」は OS に自動で下げさせず、通知を受けてエンジンが自分で全体の音量を下げる
//    （setWillPauseWhenDucked(true)。OS の自動ダッキングは再生の種類や端末で効き方が変わり得るため、どの端末でも同じ振る舞いにする）。
//  UI スレッド専用（通知も UI スレッドの Looper で受ける）。全体像は docs/android.md「音声」。
// ============================================================

package com.seedengine.runtime;

import android.content.Context;
import android.media.AudioAttributes;
import android.media.AudioFocusRequest;
import android.media.AudioManager;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

/**
 * 音声フォーカスを要求・放棄し、状態の変化をネイティブへ知らせる（MainActivity から使う）。
 *
 * <p>UI スレッド専用。onCreate で作り、onResume で {@link #request}、onPause で {@link #abandon} を呼ぶ。</p>
 */
final class AudioFocusController implements AudioManager.OnAudioFocusChangeListener {

    /** logcat のタグ（ネイティブ側と同じ）。 */
    private static final String LOG_TAG = "SEED";

    // ── ネイティブへ渡す状態の番号（libSEED.so の platform/audio_focus.rs の AudioFocus::from_code と一致させる）──

    /** 持っている（鳴らしてよい）。 */
    static final int STATE_GAINED = 0;
    /** 一時的に失った（止めて待つ）。 */
    static final int STATE_LOST_TRANSIENT = 1;
    /** 音量を下げれば鳴らしてよい（ダッキング）。 */
    static final int STATE_DUCKED = 2;
    /** 恒久的に失った（止める。次に前面へ戻るまで要求し直さない）。 */
    static final int STATE_LOST = 3;
    /** 自分から手放した（前面を離れた）。 */
    static final int STATE_RELEASED = 4;

    /** 音声フォーカスの窓口（取れない端末は無い想定だが、取れなければ何もしない）。 */
    private final AudioManager audioManager;

    /** 要求の内容（要求と放棄に同じものを使う）。 */
    private final AudioFocusRequest focusRequest;

    /**
     * ネイティブへ音声フォーカスの状態を渡す（libSEED.so の jni_exports.rs）。
     *
     * @param state 状態の番号（STATE_*）
     */
    private static native void nativeOnAudioFocusChanged(int state);

    /** @param context 音声フォーカスを要求するアプリの Context（MainActivity。onCreate 以降に渡す） */
    AudioFocusController(Context context) {
        audioManager = context.getSystemService(AudioManager.class);
        // ゲームの音として要求する（音量は USAGE_MEDIA と同じメディアの音量＝STREAM_MUSIC に属する）。
        AudioAttributes attributes = new AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_GAME)
                .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                .build();
        focusRequest = new AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
                .setAudioAttributes(attributes)
                // 通話中などで今は渡せないとき、後で GAIN を届けてもらう（それまで止めておく）。
                .setAcceptsDelayedFocusGain(true)
                // 「下げてよい」を OS の自動ダッキングに任せず、通知を受け取って自分で下げる。
                .setWillPauseWhenDucked(true)
                .setOnAudioFocusChangeListener(this, new Handler(Looper.getMainLooper()))
                .build();
    }

    /** 音声フォーカスを要求し、結果をネイティブへ知らせる（onResume から呼ぶ）。 */
    void request() {
        if (audioManager == null) {
            Log.w(LOG_TAG, "[SEED AUDIO] AudioManager を取得できないため音声フォーカスを要求しません（音は鳴らします）");
            return;
        }
        int result = audioManager.requestAudioFocus(focusRequest);
        switch (result) {
            case AudioManager.AUDIOFOCUS_REQUEST_GRANTED:
                report(STATE_GAINED, "要求が通りました");
                break;
            case AudioManager.AUDIOFOCUS_REQUEST_DELAYED:
                report(STATE_LOST_TRANSIENT, "今は渡せないため後で渡されます（通話中など。それまで止めます）");
                break;
            default:
                report(STATE_LOST, "要求が拒否されました（" + result + "。次に前面へ戻るまで止めます）");
                break;
        }
    }

    /** 音声フォーカスを手放し、ネイティブへ知らせる（onPause から呼ぶ）。 */
    void abandon() {
        if (audioManager == null) {
            return;
        }
        audioManager.abandonAudioFocusRequest(focusRequest);
        report(STATE_RELEASED, "前面を離れるため手放しました");
    }

    /** OS からの音声フォーカスの変化（UI スレッド）。 */
    @Override
    public void onAudioFocusChange(int focusChange) {
        switch (focusChange) {
            case AudioManager.AUDIOFOCUS_GAIN:
                report(STATE_GAINED, "取り戻しました（AUDIOFOCUS_GAIN）");
                break;
            case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT:
                report(STATE_LOST_TRANSIENT, "一時的に失いました（AUDIOFOCUS_LOSS_TRANSIENT）");
                break;
            case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK:
                report(STATE_DUCKED, "音量を下げれば鳴らしてよい状態になりました（AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK）");
                break;
            case AudioManager.AUDIOFOCUS_LOSS:
                report(STATE_LOST, "恒久的に失いました（AUDIOFOCUS_LOSS。次に前面へ戻るまで止めます）");
                break;
            default:
                Log.w(LOG_TAG, "[SEED AUDIO] 知らない音声フォーカスの変化を受け取りました（無視します）: " + focusChange);
                break;
        }
    }

    /**
     * 状態をログに残してネイティブへ渡す。
     *
     * @param state  状態の番号（STATE_*）
     * @param detail ログに添える説明
     */
    private static void report(int state, String detail) {
        Log.i(LOG_TAG, "[SEED AUDIO] Java: 音声フォーカス: " + detail + " → 状態 " + state);
        try {
            nativeOnAudioFocusChanged(state);
        } catch (UnsatisfiedLinkError e) {
            // 古い libSEED.so（関数が無い）でもアプリは動かし続ける（音声フォーカスに従わないだけ）。
            Log.w(LOG_TAG, "[SEED AUDIO] nativeOnAudioFocusChanged を呼べませんでした: " + e);
        }
    }
}
