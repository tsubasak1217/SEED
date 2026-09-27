// ============================================================
//  AlarmStreamVolume.java — 鳴動の間だけアラームの音量（STREAM_ALARM）を変え、止めたら戻す（force_volume・keep_volume。W1-4a・W1-7）
//
//    force_volume（0..1）… 鳴り始めに STREAM_ALARM の音量を「最大 × 値」（端末の最小〜最大に収める）にし、元の値を覚えておく。
//                          負の値は「触らない」（利用者の設定の音量のまま鳴らす）
//    keep_volume         … 鳴動中に利用者が音量を変えても、1 秒ごと（RingAudio が呼ぶ）に force_volume の値へ戻す。
//                          force_volume が負なら戻す先が無いので何もしない
//    止めたとき          … 覚えておいた元の音量へ戻す（force_volume を使ったときだけ）
//  音は USAGE_ALARM で鳴らすので、効くのはアラームの音量（STREAM_ALARM）。メディアの音量には触らない。
//  音量の変更が Android 17 の背面の音の制限の免除に入るかは未確認（docs/app_platform_roadmap.md X-7。実機・エミュレータで確かめる）。
//
//  【元の音量の控え（W1-7）】変える**前に**元の値を ringing.json の volume の欄へ書き（RingStateStore.recordVolume）、戻した後に
//  消す。鳴動中に :seed_platform が殺されても元の値が残り、鳴らし直すとき（force は今の音量〈= 自分で下げた値〉ではなく控えの値を
//  元とする）・鳴らし直せないとき（restoreLeftover）に元へ戻せる。W1-4b の G-6（殺されると音量が下がったまま）を塞ぐ。
//  前のプロセスの控えが残っているのに今の鳴動が音量を変えない（force_volume が負）ときは、控えの値へ戻してから鳴らす。
//  このクラスのインスタンスは RingAudio のスレッドだけで使う（restoreLeftover は RingStateStore の lock の中で動く）。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import android.content.Context;
import android.media.AudioManager;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

/**
 * 鳴動の間のアラームの音量（音声のスレッド専用）。
 */
final class AlarmStreamVolume {

    /** 変えるストリーム（USAGE_ALARM の音が属する）。 */
    private static final int STREAM = AudioManager.STREAM_ALARM;

    /** setStreamVolume の flags（音量の UI を出さない・音を鳴らさない）。 */
    private static final int NO_FLAGS = 0;

    /** まだ覚えていない・変えていないときの値。 */
    private static final int UNSET = -1;

    /** アプリの Context（元の音量の控えを書く）。 */
    private final Context context;

    /** AudioManager（取れなければ null。そのときは何もしない）。 */
    private final AudioManager audio;

    /** 鳴り始める前の音量（force で変えたときだけ。UNSET なら戻さない）。 */
    private int originalIndex = UNSET;

    /** 鳴動中に保つ音量（UNSET なら変えていない）。 */
    private int targetIndex = UNSET;

    /**
     * @param context どの Context でもよい
     */
    AlarmStreamVolume(Context context) {
        this.context = context.getApplicationContext();
        audio = this.context.getSystemService(AudioManager.class);
    }

    /**
     * 鳴り始めに音量を変える（負なら変えない。前のプロセスの控えが残っていれば、それを元の値とする）。
     *
     * @param fraction 最大に対する割合（0..1。AlarmRequestReader が 1 を超えないようにそろえ済み）
     */
    void force(double fraction) {
        int recorded = RingStateStore.recordedOriginalVolume(context);
        if (fraction < 0 || audio == null || audio.isVolumeFixed()) {
            if (audio != null && audio.isVolumeFixed() && fraction >= 0) {
                // 音量が固定の端末（一部のテレビ・車載など）は変えられない
                Log.w(PlatformContract.LOG_TAG, "端末の音量が固定なので force_volume を使いません");
            }
            if (recorded != RingStateStore.NO_RECORDED_VOLUME && audio != null) {
                // 前のプロセスが下げたまま殺された音量を、今の鳴動（音量を変えない）の前に元へ戻す（戻ったと確かめたときだけ控えを消す）
                set(recorded);
                if (audio.getStreamVolume(STREAM) == recorded) {
                    RingStateStore.clearVolume(context);
                    Log.i(PlatformContract.LOG_TAG, "前のプロセスが変えたアラームの音量を元の " + recorded + " へ戻しました");
                }
            }
            return;
        }
        int max = audio.getStreamMaxVolume(STREAM);
        int min = audio.getStreamMinVolume(STREAM);
        int target = (int) Math.round(fraction * max);
        targetIndex = Math.max(min, Math.min(max, target));
        // 鳴らし直し（前のプロセスの控えがある）なら、今の音量は自分で下げた値なので、控えの値を元とする
        originalIndex = recorded != RingStateStore.NO_RECORDED_VOLUME ? recorded : audio.getStreamVolume(STREAM);
        // 変える前に元の値を控える（変えた直後に殺されても戻せる）
        RingStateStore.recordVolume(context, originalIndex, targetIndex);
        set(targetIndex);
        Log.i(PlatformContract.LOG_TAG, "アラームの音量を鳴動の間だけ " + originalIndex + " → " + targetIndex
                + "（最大 " + max + (recorded != RingStateStore.NO_RECORDED_VOLUME ? "・元の値は前のプロセスの控え" : "") + "）にします");
    }

    /**
     * 変えていれば、今の音量が違うときに保つ音量へ戻す（keep_volume。1 秒ごとに呼ぶ）。
     */
    void enforce() {
        if (!isForced()) {
            return;
        }
        int now = audio.getStreamVolume(STREAM);
        if (now != targetIndex) {
            Log.i(PlatformContract.LOG_TAG, "アラームの音量が " + now + " に変わったので " + targetIndex + " へ戻します（keep_volume）");
            set(targetIndex);
        }
    }

    /**
     * 音量を変えたか（keep_volume が効くか）。
     *
     * @return 変えていれば true
     */
    boolean isForced() {
        return targetIndex != UNSET;
    }

    /**
     * 変えていれば元の音量へ戻し、控えを消す（何度呼んでもよい）。
     */
    void restore() {
        if (!isForced()) {
            return;
        }
        set(originalIndex);
        RingStateStore.clearVolume(context);
        Log.i(PlatformContract.LOG_TAG, "アラームの音量を元の " + originalIndex + " へ戻しました");
        originalIndex = UNSET;
        targetIndex = UNSET;
    }

    /**
     * 前のプロセスの控え（record）が今も同じ物なら、元の音量へ戻して控えを消す（鳴動を戻せなかったとき。RingRecovery）。
     *
     * <p>戻した後に getStreamVolume で確かめ、戻っていなければ控えを残す。Android 17（Pixel 6a）は、前景サービスも見える画面も無い
     * （受信機だけで起きた）プロセスからの setStreamVolume を AudioHardening が無視した（logcat「AudioHardening volume control …
     * ignored … level: partial」。W1-7 の実機の T5 の強制停止）。残した控えは、アプリが前面に出たとき（RingRecovery.retryLeftoverVolume）か
     * 次の鳴動（force が控えの元の値を使い、止めたときに戻す）で戻る。</p>
     *
     * @param context どの Context でもよい
     * @param record  load で読んだ volume の欄（null なら何もしない）
     * @return 戻したら true（戻せず控えを残したら false）
     */
    static boolean restoreLeftover(Context context, RingVolumeRecord record) {
        if (record == null) {
            return false;
        }
        AudioManager manager = context.getApplicationContext().getSystemService(AudioManager.class);
        // 戻す処理を試したか（控えが既に別の者に片付けられていたら試さない＝警告しない）
        boolean[] attempted = {false};
        boolean restored = RingStateStore.restoreVolumeIfSame(context, record, () -> {
            attempted[0] = true;
            setVolume(manager, record.originalIndex);
            return manager != null && manager.getStreamVolume(STREAM) == record.originalIndex;
        });
        if (restored) {
            Log.i(PlatformContract.LOG_TAG, "前のプロセスが変えたアラームの音量を元の " + record.originalIndex + " へ戻しました");
        } else if (attempted[0]) {
            Log.w(PlatformContract.LOG_TAG, "前のプロセスが変えたアラームの音量を元の " + record.originalIndex
                    + " へ戻せませんでした（背面からの音量の変更が無視された見込み）。控えを残し、アプリが前面に出たとき・次の鳴動で戻します");
        }
        return restored;
    }

    /**
     * 音量を変える（失敗はログだけ。鳴動は続ける）。
     *
     * @param index 音量の段階
     */
    private void set(int index) {
        setVolume(audio, index);
    }

    /**
     * 音量を変える（AudioManager が無い・失敗はログだけ）。
     *
     * @param manager AudioManager（null なら何もしない）
     * @param index   音量の段階
     */
    private static void setVolume(AudioManager manager, int index) {
        if (manager == null) {
            return;
        }
        try {
            manager.setStreamVolume(STREAM, index, NO_FLAGS);
        } catch (RuntimeException e) {
            // SecurityException（おやすみモードの方針で変えられない端末など）
            Log.w(PlatformContract.LOG_TAG, "アラームの音量を変えられませんでした: " + PlatformJson.describe(e));
        }
    }
}
