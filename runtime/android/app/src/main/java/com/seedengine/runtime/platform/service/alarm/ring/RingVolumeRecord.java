// ============================================================
//  RingVolumeRecord.java — force_volume で変える前のアラームの音量の控え（不変。ringing.json の volume の欄。W1-7）
//
//  AlarmStreamVolume が STREAM_ALARM を変える**前に** RingStateStore へ書き（先に書く＝変えた直後にプロセスが殺されても
//  元の値が残る）、止めて戻した後に消す。:seed_platform が鳴動中に殺されて鳴動を戻せなかったとき（RingRecovery）や、
//  鳴らし直すとき（戻した鳴動の force は、今の音量〈= 自分で下げた値〉ではなく控えの元の値を「元」とする）に使う。
//  W1-4b の G-6（殺されると利用者のアラームの音量が下がったまま戻らない）を塞ぐ。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import com.seedengine.runtime.platform.PlatformJson;

import org.json.JSONException;
import org.json.JSONObject;

/**
 * 変える前の音量の控え（不変）。
 */
final class RingVolumeRecord {

    /** 欄: 変える前の音量の段階。 */
    private static final String KEY_ORIGINAL_INDEX = "original_index";

    /** 欄: 鳴動中に保つ音量の段階（診断用）。 */
    private static final String KEY_FORCED_INDEX = "forced_index";

    /** 変える前の音量の段階（STREAM_ALARM）。 */
    final int originalIndex;

    /** 鳴動中に保つ音量の段階。 */
    final int forcedIndex;

    /**
     * @param originalIndex 変える前の音量の段階
     * @param forcedIndex   鳴動中に保つ音量の段階
     */
    RingVolumeRecord(int originalIndex, int forcedIndex) {
        this.originalIndex = originalIndex;
        this.forcedIndex = forcedIndex;
    }

    /**
     * ringing.json の volume の欄にする。
     *
     * @return JSON のオブジェクト
     */
    JSONObject toJson() {
        JSONObject json = new JSONObject();
        PlatformJson.put(json, KEY_ORIGINAL_INDEX, originalIndex);
        PlatformJson.put(json, KEY_FORCED_INDEX, forcedIndex);
        return json;
    }

    /**
     * ringing.json の volume の欄から作る。
     *
     * @param json volume の欄
     * @return 控え
     * @throws JSONException 元の音量が無い
     */
    static RingVolumeRecord fromJson(JSONObject json) throws JSONException {
        return new RingVolumeRecord(json.getInt(KEY_ORIGINAL_INDEX), json.optInt(KEY_FORCED_INDEX, json.getInt(KEY_ORIGINAL_INDEX)));
    }

    @Override
    public String toString() {
        return "元の音量 " + originalIndex + "（鳴動中 " + forcedIndex + "）";
    }
}
