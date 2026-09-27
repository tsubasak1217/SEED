// ============================================================
//  AlarmStreamVolume.java — 鳴動の間だけアラームの音量（STREAM_ALARM）を変え、止めたら戻す（force_volume・keep_volume。W1-4a）
//
//    force_volume（0..1）… 鳴り始めに STREAM_ALARM の音量を「最大 × 値」（端末の最小〜最大に収める）にし、元の値を覚えておく。
//                          負の値は「触らない」（利用者の設定の音量のまま鳴らす）
//    keep_volume         … 鳴動中に利用者が音量を変えても、1 秒ごと（RingAudio が呼ぶ）に force_volume の値へ戻す。
//                          force_volume が負なら戻す先が無いので何もしない
//    止めたとき          … 覚えておいた元の音量へ戻す（force_volume を使ったときだけ）
//  音は USAGE_ALARM で鳴らすので、効くのはアラームの音量（STREAM_ALARM）。メディアの音量には触らない。
//  音量の変更が Android 17 の背面の音の制限の免除に入るかは未確認（docs/app_platform_roadmap.md X-7。実機・エミュレータで確かめる）。
//  このクラスは RingAudio のスレッドだけで使う（lock を持たない）。
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
        audio = context.getApplicationContext().getSystemService(AudioManager.class);
    }

    /**
     * 鳴り始めに音量を変える（負なら何もしない）。
     *
     * @param fraction 最大に対する割合（0..1。AlarmRequestReader が 1 を超えないようにそろえ済み）
     */
    void force(double fraction) {
        if (fraction < 0 || audio == null) {
            return;
        }
        if (audio.isVolumeFixed()) {
            // 音量が固定の端末（一部のテレビ・車載など）は変えられない
            Log.w(PlatformContract.LOG_TAG, "端末の音量が固定なので force_volume を使いません");
            return;
        }
        int max = audio.getStreamMaxVolume(STREAM);
        int min = audio.getStreamMinVolume(STREAM);
        int target = (int) Math.round(fraction * max);
        targetIndex = Math.max(min, Math.min(max, target));
        originalIndex = audio.getStreamVolume(STREAM);
        set(targetIndex);
        Log.i(PlatformContract.LOG_TAG, "アラームの音量を鳴動の間だけ " + originalIndex + " → " + targetIndex
                + "（最大 " + max + "）にします");
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
     * 変えていれば元の音量へ戻す（何度呼んでもよい）。
     */
    void restore() {
        if (!isForced()) {
            return;
        }
        set(originalIndex);
        Log.i(PlatformContract.LOG_TAG, "アラームの音量を元の " + originalIndex + " へ戻しました");
        originalIndex = UNSET;
        targetIndex = UNSET;
    }

    /**
     * 音量を変える（失敗はログだけ。鳴動は続ける）。
     *
     * @param index 音量の段階
     */
    private void set(int index) {
        try {
            audio.setStreamVolume(STREAM, index, NO_FLAGS);
        } catch (RuntimeException e) {
            // SecurityException（おやすみモードの方針で変えられない端末など）
            Log.w(PlatformContract.LOG_TAG, "アラームの音量を変えられませんでした: " + PlatformJson.describe(e));
        }
    }
}
