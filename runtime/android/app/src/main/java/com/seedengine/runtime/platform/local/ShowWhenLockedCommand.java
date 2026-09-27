// ============================================================
//  ShowWhenLockedCommand.java — window.set_show_when_locked（ロック画面の上に出す＋画面を点ける の切り替え。W1-4a）
//
//  引数 { on: 真偽 }。MainActivity の setShowWhenLocked(on)・setTurnScreenOn(on) を UI スレッドで切り替える
//  （runOnUiThread へ投げてすぐ返る。返答 { on }）。目覚ましの鳴動で起動したときは LaunchReason が上げているので、
//  アプリは鳴動を片付けた後にこれで下ろす（下ろさないと、アプリを開いたまま電源ボタンを押してもロック画面が出ない。AC-5）。
//  ロックは解除しない（requestDismissKeyguard は使わない。docs/app_platform_roadmap.md §2.2 の「キーガード」）。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.app.Activity;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

import org.json.JSONObject;

/**
 * window.set_show_when_locked。
 */
final class ShowWhenLockedCommand implements MainProcessCommand {

    @Override
    public byte[] handle(JSONObject arguments) {
        Object value = arguments.opt(PlatformContract.KEY_WINDOW_ON);
        if (!(value instanceof Boolean)) {
            return PlatformJson.errorReply(PlatformContract.ERROR_INVALID_ARGUMENT,
                    PlatformContract.KEY_WINDOW_ON + " は真偽にしてください");
        }
        boolean on = (Boolean) value;
        Activity activity = HostActivity.get();
        if (activity == null) {
            return PlatformJson.errorReply(PlatformContract.ERROR_NO_ACTIVITY);
        }
        // Activity の窓の操作は UI スレッドだけ（呼び出し元はエンジンのスレッド）。API 27 からの API（minSdk 29）
        activity.runOnUiThread(() -> {
            activity.setShowWhenLocked(on);
            activity.setTurnScreenOn(on);
            Log.i(PlatformContract.LOG_TAG, "ロック画面の上に出す・画面を点ける: " + on);
        });
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_WINDOW_ON, on);
        return PlatformJson.okReply(fields);
    }
}
