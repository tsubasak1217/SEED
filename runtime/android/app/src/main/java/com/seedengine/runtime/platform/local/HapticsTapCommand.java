// ============================================================
//  HapticsTapCommand.java — haptics.tap（軽いクリックの触感。W1-6）
//
//  引数なし。返答 {}。中身は haptics/HapticFeedback.tap（EFFECT_CLICK・API 33 以降は USAGE_TOUCH）。振動子が無ければ no_vibrator。
//  Vibrator はどのスレッドからでも呼べるので、エンジンのスレッドのまま同期に鳴らす（Activity は要らない。アプリの Context だけ）。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.content.Context;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.haptics.HapticFeedback;

import org.json.JSONObject;

/**
 * haptics.tap。
 */
final class HapticsTapCommand implements MainProcessCommand {

    @Override
    public byte[] handle(JSONObject arguments) {
        Context context = HostActivity.applicationContext();
        if (context == null) {
            return PlatformJson.errorReply(PlatformContract.ERROR_NOT_INITIALIZED);
        }
        if (!HapticFeedback.tap(context)) {
            return PlatformJson.errorReply(PlatformContract.ERROR_NO_VIBRATOR);
        }
        return PlatformJson.okReply(new JSONObject());
    }
}
