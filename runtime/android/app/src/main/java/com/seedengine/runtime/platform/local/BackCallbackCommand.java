// ============================================================
//  BackCallbackCommand.java — app.set_back_callback（予測型の戻るの自分のコールバックの出し入れ。W2 の手直し P1-3）
//
//  WindowToggleCommand（window.set_* の 3 つ）と同じ形:
//    1. 引数 { on: 真偽 } を読む（真偽でなければ invalid_argument）
//    2. 操作する Activity（HostActivity に登録された MainActivity）を取る（無ければ no_activity）
//    3. 予測型の戻るが有効（app/BackCallbackHost.isPredictiveBackEnabled。APK の印 seed_predictive_back が true で API 33 以上）なら、
//       コールバックの出し入れは UI スレッドだけなので Activity.runOnUiThread へ投げてすぐ返る（呼び出し元はエンジンのスレッド）。
//       無効なら何もしない（戻るは従来どおり KEYCODE_BACK → Escape。predictive_back の無いプロジェクトの振る舞いは変わらない）
//    4. 返答 { on, enabled }
//  返答に enabled を足すので WindowToggleCommand の派生にはしない（あちらの返答は { on } に固定）。
//  中身（API ごとの登録と外し方）は back/BackCallbackController。デスクトップの模擬は desktop_sim/back_commands.rs。docs/android.md §25.18。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.app.Activity;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.app.BackCallbackHost;

import org.json.JSONObject;

/**
 * app.set_back_callback。
 */
final class BackCallbackCommand implements MainProcessCommand {

    @Override
    public byte[] handle(JSONObject arguments) {
        Object value = arguments.opt(PlatformContract.KEY_APP_ON);
        if (!(value instanceof Boolean)) {
            return PlatformJson.errorReply(PlatformContract.ERROR_INVALID_ARGUMENT,
                    PlatformContract.KEY_APP_ON + " は真偽にしてください");
        }
        boolean on = (Boolean) value;
        Activity activity = HostActivity.get();
        if (activity == null) {
            return PlatformJson.errorReply(PlatformContract.ERROR_NO_ACTIVITY);
        }
        boolean enabled = false;
        if (activity instanceof BackCallbackHost) {
            BackCallbackHost host = (BackCallbackHost) activity;
            enabled = host.isPredictiveBackEnabled();
            if (enabled) {
                // コールバックの登録・外しは UI スレッドで（呼び出し元はエンジンのスレッド）。効くのは少し後
                activity.runOnUiThread(() -> host.setAppHandlesBack(on));
            }
        } else {
            // HostActivity に登録されるのは MainActivity だけなので起きない見込み（起きたら取り違えの印としてログに残す）
            Log.w(PlatformContract.LOG_TAG, "予測型の戻るを切り替えられる Activity ではありません: " + activity.getClass().getName());
        }
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_APP_ON, on);
        PlatformJson.put(fields, PlatformContract.KEY_APP_ENABLED, enabled);
        return PlatformJson.okReply(fields);
    }
}
