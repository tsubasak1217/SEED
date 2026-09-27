// ============================================================
//  HapticsVibrateCommand.java — haptics.vibrate（決まった長さの振動。W1-6）
//
//  引数 { ms }。返答 { ms }（そろえた後の長さ）。
//  【ms の規則】（Rust の runtime/src/engine/platform/bridge/haptics/mod.rs の read_vibrate_ms と同じ。変えるときは両方）
//    数でない・有限でない・MIN_VIBRATE_MS（1）未満 → invalid_argument。小数は切り捨て。MAX_VIBRATE_MS（5000）を超えたらそろえる
//    （呼び間違いで長く鳴り続けないため。長い振動が要る用途は目覚ましの鳴動〈Alarms〉を使う）。
//  中身は haptics/HapticFeedback.vibrate（createOneShot・API 33 以降は USAGE_MEDIA）。振動子が無ければ no_vibrator。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.content.Context;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.haptics.HapticFeedback;

import org.json.JSONObject;

/**
 * haptics.vibrate。
 */
final class HapticsVibrateCommand implements MainProcessCommand {

    @Override
    public byte[] handle(JSONObject arguments) {
        Object value = arguments.opt(PlatformContract.KEY_HAPTICS_MS);
        double requested = value instanceof Number ? ((Number) value).doubleValue() : Double.NaN;
        if (Double.isNaN(requested) || Double.isInfinite(requested) || requested < PlatformContract.MIN_VIBRATE_MS) {
            return PlatformJson.errorReply(PlatformContract.ERROR_INVALID_ARGUMENT,
                    PlatformContract.KEY_HAPTICS_MS + " は " + PlatformContract.MIN_VIBRATE_MS + " 以上の数にしてください");
        }
        // 小数は切り捨て（1 以上なので long への変換の切り捨てと同じ）、上限にそろえる
        long milliseconds = Math.min((long) requested, PlatformContract.MAX_VIBRATE_MS);
        Context context = HostActivity.applicationContext();
        if (context == null) {
            return PlatformJson.errorReply(PlatformContract.ERROR_NOT_INITIALIZED);
        }
        if (!HapticFeedback.vibrate(context, milliseconds)) {
            return PlatformJson.errorReply(PlatformContract.ERROR_NO_VIBRATOR);
        }
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_HAPTICS_MS, milliseconds);
        return PlatformJson.okReply(fields);
    }
}
